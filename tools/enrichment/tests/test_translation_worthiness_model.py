from __future__ import annotations

import json
import sys
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from train_translation_worthiness import main as train_main  # noqa: E402
from translation_worthiness_corpus import (  # noqa: E402
    DEFAULT_CORPUS,
    DEFAULT_SPLITS,
    WorthinessCorpusError,
    evaluation_report,
    load_model_manifest,
    load_predictions,
    validate_corpus,
)
from translation_worthiness_model import (  # noqa: E402
    ABLATION_CODES,
    DEFAULT_LEARNING_RATE,
    choose_thresholds,
    deserialize_model,
    feature_contract_sha256,
    serialize_model,
    train_model,
    validate_model_manifest_identity,
)

TRAINER_PATH = Path(__file__).resolve().parents[1] / "train_translation_worthiness.py"


class TranslationWorthinessModelTests(unittest.TestCase):
    def _train(self, root: Path, stem: str, ablation: str = "combined") -> tuple[Path, Path, Path]:
        model = root / f"{stem}.bin"
        predictions = root / f"{stem}.jsonl"
        manifest = root / f"{stem}.manifest.json"
        exit_code = train_main(
            [
                "--corpus",
                str(DEFAULT_CORPUS),
                "--splits",
                str(DEFAULT_SPLITS),
                "--ablation",
                ablation,
                "--model-output",
                str(model),
                "--predictions-output",
                str(predictions),
                "--manifest-output",
                str(manifest),
            ]
        )
        self.assertEqual(0, exit_code)
        return model, predictions, manifest

    def test_all_learned_ablation_models_round_trip_and_score_deterministically(self) -> None:
        corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
        train_rows = [row for row in corpus.rows if corpus.split_by_group[row.group_id] == "train"]
        for ablation in ABLATION_CODES:
            with self.subTest(ablation=ablation):
                model = train_model(train_rows, ablation=ablation, bucket_count=128, epochs=3)
                serialized = serialize_model(model)
                restored = deserialize_model(serialized)
                self.assertEqual(serialized, serialize_model(restored))
                sample = corpus.rows[0].text
                self.assertEqual(model.score(sample), restored.score(sample))

    def test_serialized_model_must_match_manifest_feature_identity(self) -> None:
        corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
        train_rows = [row for row in corpus.rows if corpus.split_by_group[row.group_id] == "train"]
        model = train_model(
            train_rows,
            ablation="combined",
            bucket_count=128,
            epochs=3,
        )
        serialized = serialize_model(model)
        validated = validate_model_manifest_identity(
            serialized,
            ablation="combined",
            bucket_count=128,
            ngram_min=2,
            ngram_max=5,
        )
        self.assertEqual(model, validated)
        with self.assertRaisesRegex(WorthinessCorpusError, "disagrees with its manifest"):
            validate_model_manifest_identity(
                serialized,
                ablation="engineered-only",
                bucket_count=128,
                ngram_min=2,
                ngram_max=5,
            )

    def test_two_clean_temporary_builds_are_byte_identical(self) -> None:
        with (
            tempfile.TemporaryDirectory() as first_name,
            tempfile.TemporaryDirectory() as second_name,
        ):
            first = self._train(Path(first_name), "candidate")
            second = self._train(Path(second_name), "candidate")

            for left, right in zip(first, second, strict=True):
                self.assertEqual(left.read_bytes(), right.read_bytes())
            manifest = json.loads(first[2].read_text(encoding="utf-8"))
            self.assertTrue(manifest["researchOnly"])
            self.assertFalse(manifest["promotionEligible"])
            self.assertEqual("train", manifest["protocol"]["fitSplit"])
            self.assertEqual("calibration", manifest["protocol"]["selectionSplit"])
            self.assertEqual("test", manifest["protocol"]["lockedTestSplit"])
            serialized_manifest = first[2].read_text(encoding="utf-8")
            self.assertNotIn('"text"', serialized_manifest)
            self.assertNotIn("plain-es-001", serialized_manifest)

    def test_test_label_changes_cannot_change_fit_or_calibration_thresholds(self) -> None:
        corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
        train_rows = tuple(
            row for row in corpus.rows if corpus.split_by_group[row.group_id] == "train"
        )
        calibration_rows = tuple(
            row for row in corpus.rows if corpus.split_by_group[row.group_id] == "calibration"
        )
        model = train_model(
            train_rows,
            ablation="combined",
            bucket_count=128,
            epochs=3,
            learning_rate=DEFAULT_LEARNING_RATE,
        )
        thresholds = choose_thresholds(model, calibration_rows)
        changed_rows = tuple(
            replace(row, label="machine") if corpus.split_by_group[row.group_id] == "test" else row
            for row in corpus.rows
        )
        changed_train = tuple(
            row for row in changed_rows if corpus.split_by_group[row.group_id] == "train"
        )
        changed_calibration = tuple(
            row for row in changed_rows if corpus.split_by_group[row.group_id] == "calibration"
        )
        changed_model = train_model(
            changed_train,
            ablation="combined",
            bucket_count=128,
            epochs=3,
            learning_rate=DEFAULT_LEARNING_RATE,
        )
        self.assertEqual(serialize_model(model), serialize_model(changed_model))
        self.assertEqual(thresholds, choose_thresholds(changed_model, changed_calibration))

    def test_locked_manifest_and_thresholds_control_phase_evaluation(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_name:
            root = Path(temporary_name)
            model_path, predictions_path, manifest_path = self._train(root, "locked")
            corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
            manifest = load_model_manifest(
                manifest_path,
                corpus_path=DEFAULT_CORPUS,
                split_path=DEFAULT_SPLITS,
                trainer_path=TRAINER_PATH,
                model_path=model_path,
                predictions_path=predictions_path,
                expected_feature_contract_sha256=feature_contract_sha256(),
            )
            predictions = load_predictions(predictions_path, corpus, manifest)
            calibration = evaluation_report(
                corpus, predictions, phase="calibration-select", manifest=manifest
            )
            locked_test = evaluation_report(
                corpus, predictions, phase="locked-test", manifest=manifest
            )

            self.assertEqual("calibration", calibration["evaluatedSplit"])
            self.assertEqual("test", locked_test["evaluatedSplit"])
            self.assertEqual(
                set(("ambiguous", "human-worthy", "machine", "mixed")),
                set(locked_test["labelDecisionMatrix"]),
            )
            self.assertIn("mixedFacet", locked_test)
            self.assertTrue(locked_test["researchOnly"])
            self.assertFalse(locked_test["promotionEligible"])

    def test_manifest_hash_and_prediction_decision_tampering_are_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_name:
            root = Path(temporary_name)
            model_path, predictions_path, manifest_path = self._train(root, "tamper")
            corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
            model_path.write_bytes(model_path.read_bytes() + b"x")
            with self.assertRaisesRegex(WorthinessCorpusError, "hash identity"):
                load_model_manifest(
                    manifest_path,
                    corpus_path=DEFAULT_CORPUS,
                    split_path=DEFAULT_SPLITS,
                    trainer_path=TRAINER_PATH,
                    model_path=model_path,
                    predictions_path=predictions_path,
                    expected_feature_contract_sha256=feature_contract_sha256(),
                )

            model_path, predictions_path, manifest_path = self._train(root, "retamper")
            manifest = load_model_manifest(
                manifest_path,
                corpus_path=DEFAULT_CORPUS,
                split_path=DEFAULT_SPLITS,
                trainer_path=TRAINER_PATH,
                model_path=model_path,
                predictions_path=predictions_path,
                expected_feature_contract_sha256=feature_contract_sha256(),
            )
            rows = [
                json.loads(line)
                for line in predictions_path.read_text(encoding="utf-8").splitlines()
            ]
            rows[0]["decision"] = "retain" if rows[0]["decision"] != "retain" else "suppress"
            predictions_path.write_text(
                "".join(
                    json.dumps(row, separators=(",", ":"), sort_keys=True) + "\n" for row in rows
                ),
                encoding="utf-8",
            )
            with self.assertRaises(WorthinessCorpusError):
                load_predictions(predictions_path, corpus, manifest)

    def test_training_overwrites_safely_and_rejects_aliases(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_name:
            root = Path(temporary_name)
            first = self._train(root, "overwrite")
            original = tuple(path.read_bytes() for path in first)
            second = self._train(root, "overwrite")
            self.assertEqual(original, tuple(path.read_bytes() for path in second))
            self.assertFalse(list(root.glob("*.partial")))
            exit_code = train_main(
                [
                    "--model-output",
                    str(DEFAULT_CORPUS),
                    "--predictions-output",
                    str(root / "alias-predictions.jsonl"),
                    "--manifest-output",
                    str(root / "alias-manifest.json"),
                ]
            )
            self.assertEqual(2, exit_code)


if __name__ == "__main__":
    unittest.main()
