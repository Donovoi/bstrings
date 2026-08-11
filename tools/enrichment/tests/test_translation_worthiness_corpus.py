from __future__ import annotations

import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from train_translation_worthiness import main as train_main  # noqa: E402
from translation_worthiness_corpus import (  # noqa: E402
    CORPUS_ID,
    DEFAULT_CORPUS,
    DEFAULT_SPLITS,
    LABELS,
    LENGTH_BAND_NAMES,
    ORIGIN_FAMILIES,
    PROJECT_LICENSE,
    SCHEMA_VERSION,
    SCRIPT_FAMILIES,
    WorthinessCorpusError,
    evaluation_report,
    load_corpus,
    load_predictions,
    main,
    validate_corpus,
    validation_report,
    zero_miss_upper_95,
)


def canonical_json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def corpus_row(
    identifier: str,
    group_id: str,
    *,
    text: str = "Mensaje sintético",
    label: str = "human-worthy",
    license_name: str = PROJECT_LICENSE,
) -> dict[str, object]:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "id": identifier,
        "text": text,
        "label": label,
        "groupId": group_id,
        "sourceFamily": "plain-language",
        "originFamily": "native-static",
        "scriptFamily": "latin",
        "lengthBand": "8-31",
        "license": license_name,
    }


def split_manifest(
    *,
    train: list[str] | None = None,
    calibration: list[str] | None = None,
    test: list[str] | None = None,
) -> dict[str, object]:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "corpusId": CORPUS_ID,
        "license": PROJECT_LICENSE,
        "splits": {
            "calibration": calibration or ["g-calibration"],
            "test": test or ["g-test"],
            "train": train or ["g-train"],
        },
    }


class TemporaryFixture:
    def __init__(self) -> None:
        self._temporary = tempfile.TemporaryDirectory()
        self.root = Path(self._temporary.name)
        self.corpus = self.root / "corpus.jsonl"
        self.splits = self.root / "splits.json"
        self.predictions = self.root / "predictions.jsonl"

    def close(self) -> None:
        self._temporary.cleanup()

    def write_corpus(self, rows: list[dict[str, object]]) -> None:
        self.corpus.write_text(
            "".join(canonical_json(row) + "\n" for row in rows), encoding="utf-8"
        )

    def write_splits(self, value: dict[str, object]) -> None:
        self.splits.write_text(canonical_json(value) + "\n", encoding="utf-8")

    def write_predictions(self, rows: list[dict[str, object]]) -> None:
        self.predictions.write_text(
            "".join(canonical_json(row) + "\n" for row in rows), encoding="utf-8"
        )

    def write_valid_minimum(self) -> None:
        self.write_corpus(
            [
                corpus_row("row-train", "g-train", text="Texto train"),
                corpus_row("row-calibration", "g-calibration", text="Texto calibrar"),
                corpus_row("row-test", "g-test", text="Texto test"),
            ]
        )
        self.write_splits(split_manifest())


class TranslationWorthinessCorpusTests(unittest.TestCase):
    def test_bundled_corpus_is_valid_bounded_and_group_disjoint(self) -> None:
        corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
        report = validation_report(corpus)

        self.assertEqual("valid", report["status"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        self.assertEqual(len(corpus.rows), report["records"])
        self.assertEqual(len({row.group_id for row in corpus.rows}), report["groups"])
        self.assertEqual(set(LABELS), {row.label for row in corpus.rows})
        self.assertEqual(set(ORIGIN_FAMILIES), {row.origin_family for row in corpus.rows})
        self.assertEqual(set(SCRIPT_FAMILIES), {row.script_family for row in corpus.rows})
        self.assertEqual(set(LENGTH_BAND_NAMES), {row.length_band for row in corpus.rows})
        self.assertEqual(
            len(corpus.split_by_group),
            sum(split["groups"] for split in report["splits"].values()),
        )
        serialized = canonical_json(report)
        self.assertNotIn('"text"', serialized)
        self.assertNotIn("plain-es-001", serialized)
        self.assertNotIn("La copia", serialized)

    def test_corpus_rejects_schema_drift_duplicate_ids_and_unbounded_ids(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        fixture.write_splits(split_manifest())

        cases = []
        extra = corpus_row("row-train", "g-train")
        extra["unexpected"] = True
        cases.append([extra])
        cases.append(
            [
                corpus_row("row-duplicate", "g-train"),
                corpus_row("row-duplicate", "g-calibration"),
            ]
        )
        cases.append([corpus_row("UPPERCASE-ID", "g-train")])
        boolean_schema = corpus_row("row-boolean-schema", "g-train")
        boolean_schema["schemaVersion"] = True
        cases.append([boolean_schema])
        for rows in cases:
            with self.subTest(rows=rows):
                fixture.write_corpus(rows)
                with self.assertRaises(WorthinessCorpusError):
                    load_corpus(fixture.corpus)

    def test_corpus_rejects_duplicate_text_split_across_source_groups(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        fixture.write_corpus(
            [
                corpus_row("row-one", "g-train"),
                corpus_row("row-two", "g-calibration"),
            ]
        )

        with self.assertRaisesRegex(WorthinessCorpusError, "different source group"):
            load_corpus(fixture.corpus)

    def test_corpus_rejects_unapproved_license_and_length_band_drift(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        fixture.write_splits(split_manifest())

        fixture.write_corpus([corpus_row("row-license", "g-train", license_name="unknown-license")])
        with self.assertRaisesRegex(WorthinessCorpusError, "unapproved license"):
            load_corpus(fixture.corpus)

        drift = corpus_row("row-band", "g-train", text="Mensaje sintético")
        drift["lengthBand"] = "32-127"
        fixture.write_corpus([drift])
        with self.assertRaisesRegex(WorthinessCorpusError, "mismatched lengthBand"):
            load_corpus(fixture.corpus)

        over_length = corpus_row("row-long", "g-train", text="x" * 2_049)
        over_length["lengthBand"] = "512-2048"
        fixture.write_corpus([over_length])
        with self.assertRaisesRegex(WorthinessCorpusError, "outside"):
            load_corpus(fixture.corpus)

    def test_corpus_rejects_duplicate_json_properties_without_echoing_values(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        fixture.corpus.write_text(
            '{"schemaVersion":1,"id":"private-looking-value","id":"duplicate"}\n',
            encoding="utf-8",
        )

        with self.assertRaises(WorthinessCorpusError) as raised:
            load_corpus(fixture.corpus)
        self.assertNotIn("private-looking-value", str(raised.exception))

    def test_split_manifest_rejects_overlap_missing_unknown_and_unsorted_groups(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        fixture.write_valid_minimum()
        manifests = [
            split_manifest(train=["g-train"], calibration=["g-calibration"], test=["g-train"]),
            split_manifest(train=["g-train"], calibration=["g-calibration"], test=["g-unknown"]),
            split_manifest(
                train=["g-train"], calibration=["g-calibration"], test=["g-test", "g-a"]
            ),
        ]
        for manifest in manifests:
            with self.subTest(manifest=manifest):
                fixture.write_splits(manifest)
                with self.assertRaises(WorthinessCorpusError):
                    validate_corpus(fixture.corpus, fixture.splits)

    def test_prediction_contract_requires_exact_unique_complete_rows(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        fixture.write_valid_minimum()
        corpus = validate_corpus(fixture.corpus, fixture.splits)
        valid = [{"id": row.identifier, "score": 0.25, "decision": "retain"} for row in corpus.rows]

        cases = [
            valid[:-1],
            [*valid, valid[0]],
            [{**row, "extra": True} for row in valid],
            [{**row, "score": float("inf")} for row in valid],
            [{**row, "decision": "machine"} for row in valid],
        ]
        for rows in cases:
            with self.subTest(rows=rows):
                fixture.write_predictions(rows)
                with self.assertRaises(WorthinessCorpusError):
                    load_predictions(fixture.predictions, corpus)

    def test_evaluation_reports_only_aggregate_false_suppression_strata(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
        rows = [{"id": row.identifier, "score": 0.1, "decision": "retain"} for row in corpus.rows]
        positive = next(
            row
            for row in corpus.rows
            if row.label == "human-worthy" and corpus.split_by_group[row.group_id] == "test"
        )
        ambiguous = next(
            row
            for row in corpus.rows
            if row.label == "ambiguous" and corpus.split_by_group[row.group_id] == "test"
        )
        for prediction in rows:
            if prediction["id"] in {positive.identifier, ambiguous.identifier}:
                prediction["score"] = 0.9
                prediction["decision"] = "suppress"
        fixture.write_predictions(rows)

        predictions = load_predictions(fixture.predictions, corpus)
        report = evaluation_report(corpus, predictions)
        overall = report["overall"]
        self.assertEqual(1, overall["falseSuppressions"])
        self.assertEqual(1, overall["falseSuppressionGroups"])
        self.assertEqual(1, overall["ambiguousSuppressions"])
        self.assertIsNone(overall["zeroFalseSuppressionUpper95"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        self.assertEqual("synthetic-group-diagnostic-only", report["confidenceUnit"])
        self.assertEqual("locked-test", report["evaluationPhase"])
        self.assertEqual("test", report["evaluatedSplit"])
        self.assertEqual(1, report["labelDecisionMatrix"]["human-worthy"]["suppress"])
        self.assertIn("mixedFacet", report)
        origin = next(
            cell
            for cell in report["byOriginFamily"]
            if cell["originFamily"] == positive.origin_family
        )
        self.assertGreaterEqual(origin["falseSuppressions"], 1)
        serialized = canonical_json(report)
        self.assertNotIn('"id"', serialized)
        self.assertNotIn('"text"', serialized)
        self.assertNotIn(positive.identifier, serialized)
        self.assertNotIn(positive.text, serialized)
        self.assertIn("byOriginScriptLength", report)

    def test_zero_miss_upper_bound_is_one_sided_95_percent(self) -> None:
        self.assertIsNone(zero_miss_upper_95(0, 0))
        self.assertIsNone(zero_miss_upper_95(100, 1))
        self.assertAlmostEqual(1 - math.pow(0.05, 0.1), zero_miss_upper_95(10, 0), 12)

    def test_all_retain_predictions_report_zero_misses_and_a_bound_per_cell(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)
        fixture.write_predictions(
            [{"id": row.identifier, "score": 0.0, "decision": "retain"} for row in corpus.rows]
        )
        predictions = load_predictions(fixture.predictions, corpus)

        report = evaluation_report(corpus, predictions)

        self.assertEqual(0, report["overall"]["falseSuppressions"])
        self.assertEqual(0, report["overall"]["falseSuppressionGroups"])
        self.assertIsNotNone(report["overall"]["zeroFalseSuppressionUpper95"])
        for cell in report["byOriginScriptLength"]:
            if cell["knownHumanRecords"] > 0:
                self.assertIsNotNone(cell["zeroFalseSuppressionUpper95"])
            else:
                self.assertIsNone(cell["zeroFalseSuppressionUpper95"])

    def test_evaluate_cli_safely_overwrites_only_an_aggregate_report(self) -> None:
        fixture = TemporaryFixture()
        self.addCleanup(fixture.close)
        model = fixture.root / "model.bin"
        manifest = fixture.root / "model-manifest.json"
        self.assertEqual(
            0,
            train_main(
                [
                    "--corpus",
                    str(DEFAULT_CORPUS),
                    "--splits",
                    str(DEFAULT_SPLITS),
                    "--model-output",
                    str(model),
                    "--predictions-output",
                    str(fixture.predictions),
                    "--manifest-output",
                    str(manifest),
                ]
            ),
        )
        output = fixture.root / "aggregate.json"
        output.write_text("previous", encoding="utf-8")

        exit_code = main(
            [
                "evaluate",
                "--corpus",
                str(DEFAULT_CORPUS),
                "--splits",
                str(DEFAULT_SPLITS),
                "--predictions",
                str(fixture.predictions),
                "--model-manifest",
                str(manifest),
                "--model",
                str(model),
                "--trainer",
                str(Path(__file__).resolve().parents[1] / "train_translation_worthiness.py"),
                "--phase",
                "locked-test",
                "--output",
                str(output),
            ]
        )

        self.assertEqual(0, exit_code)
        report = json.loads(output.read_text(encoding="utf-8"))
        self.assertEqual("translation-worthiness-evaluation", report["reportType"])
        serialized = output.read_text(encoding="utf-8")
        self.assertNotIn('"id"', serialized)
        self.assertNotIn('"text"', serialized)
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        self.assertFalse(list(fixture.root.glob("*.partial")))


if __name__ == "__main__":
    unittest.main()
