from __future__ import annotations

import hashlib
import json
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path
from unittest import mock

BENCHMARK_ROOT = Path(__file__).resolve().parents[1] / "benchmarks"
if str(BENCHMARK_ROOT) not in sys.path:
    sys.path.insert(0, str(BENCHMARK_ROOT))

import translation_worthiness_fasttext as fasttext  # noqa: E402


def canonical_bytes(value: object) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True) + "\n"
    ).encode("utf-8")


def sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def parameters(dim: int) -> dict[str, int | float | str]:
    return {
        "bucket": 1000,
        "dim": dim,
        "epoch": 5,
        "loss": "softmax",
        "lr": 0.1,
        "maxn": 5,
        "minCount": 1,
        "minn": 2,
        "thread": 1,
        "wordNgrams": 1,
        "ws": 5,
    }


def row(identifier: str, split: str, label: str, marker: str) -> dict[str, object]:
    text = f"{marker} sample text"
    return {
        "id": identifier,
        "groupId": f"group-{identifier}",
        "split": split,
        "text": text,
        "label": label,
        "sourceFamily": "plain-language",
        "originFamily": "unknown",
        "scriptFamily": "latin",
        "lengthBand": "8-31",
        "license": "CC-BY-4.0",
        "adjudication": {
            "method": "dual-independent-agreement",
            "labels": [label, label],
        },
    }


def corpus_value() -> dict[str, object]:
    rows = [
        row("cal-ambiguous", "calibration", "ambiguous", "CAL_AMBIG"),
        row("cal-human", "calibration", "human-worthy", "CAL_HUMAN"),
        row("cal-machine", "calibration", "machine", "CAL_MACHINE"),
        row("cal-mixed", "calibration", "mixed", "CAL_MIXED"),
        row("test-ambiguous", "test", "ambiguous", "TEST_AMBIG"),
        row("test-human", "test", "human-worthy", "TEST_HUMAN"),
        row("test-machine", "test", "machine", "TEST_MACHINE"),
        row("test-mixed", "test", "mixed", "TEST_MIXED"),
        row("train-human", "train", "human-worthy", "TRAIN_HUMAN"),
        row("train-machine", "train", "machine", "TRAIN_MACHINE"),
        row("train-mixed", "train", "mixed", "TRAIN_MIXED"),
    ]
    return {
        "schemaVersion": 1,
        "artifactType": "translation-worthiness-dual-adjudicated-corpus",
        "researchOnly": True,
        "promotionEligible": False,
        "corpusId": "dual-adjudicated-fixture-v1",
        "adjudicationPolicy": {
            "method": "dual-independent-agreement",
            "adjudicatorsPerRow": 2,
            "independent": True,
            "agreementRequired": True,
        },
        "rows": rows,
    }


FAKE_SOURCE = r"""
import json
import sys
from pathlib import Path

MALFORMED = {malformed}
root = Path(__file__).resolve().parent
arguments = sys.argv[1:]
command = arguments[0]
entry = {{"command": command}}
if command == "supervised":
    options = {{arguments[index]: arguments[index + 1] for index in range(1, len(arguments), 2)}}
    train = Path(options["-input"]).read_text(encoding="utf-8")
    entry["input"] = train
    dim = options["-dim"]
    Path(options["-output"] + ".bin").write_bytes(b"FAKEFT1:" + dim.encode("ascii"))
elif command == "predict-prob":
    model = Path(arguments[1]).read_bytes()
    lines = Path(arguments[2]).read_text(encoding="utf-8").splitlines()
    entry["input"] = "\n".join(lines)
    weak = model.endswith(b":16")
    for text in lines:
        if MALFORMED:
            print("malformed")
            continue
        if "AMBIG" in text:
            score = 0.75 if weak else 0.50
        elif "MACHINE" in text:
            score = 0.76 if weak else 0.05
        elif "MIXED" in text:
            score = 0.78 if weak else 0.90
        else:
            score = 0.80 if weak else 0.95
        print(f"__label__human {{score:.6f}} __label__machine {{1.0 - score:.6f}}")
else:
    raise SystemExit(9)
with (root / "calls.jsonl").open("a", encoding="utf-8") as handle:
    handle.write(json.dumps(entry, separators=(",", ":"), sort_keys=True) + "\n")
"""


class FastTextFixture:
    def __init__(self, *, malformed: bool = False) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.corpus = self.root / "corpus.json"
        self.fake = self.root / "fake_fasttext.py"
        self.source_archive = self.root / "fasttext-source.tar"
        self.lock = self.root / "source-lock.json"
        self.model = self.root / "selected.bin"
        self.plan = self.root / "plan.json"
        self.report = self.root / "report.json"
        self.fake.write_text(
            textwrap.dedent(FAKE_SOURCE.format(malformed="True" if malformed else "False")),
            encoding="utf-8",
        )
        self.source_archive.write_bytes(b"fake-fasttext-source-archive-v1")
        self.write_corpus(corpus_value())
        self.write_lock()

    def close(self) -> None:
        self.temporary.cleanup()

    @property
    def calls(self) -> list[dict[str, str]]:
        path = self.root / "calls.jsonl"
        if not path.exists():
            return []
        return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]

    def write_corpus(self, value: dict[str, object]) -> None:
        self.corpus.write_bytes(canonical_bytes(value))

    def lock_value(self) -> dict[str, object]:
        families = []
        for identifier, dim in (("family-a", 8), ("family-b", 16)):
            config = parameters(dim)
            families.append(
                {
                    "familyId": identifier,
                    "parameters": config,
                    "configSha256": sha256(
                        canonical_bytes({"familyId": identifier, "parameters": config})
                    ),
                    "modelSha256": sha256(b"FAKEFT1:" + str(dim).encode("ascii")),
                }
            )
        return {
            "schemaVersion": 1,
            "artifactType": "translation-worthiness-fasttext-source-lock",
            "researchOnly": True,
            "promotionEligible": False,
            "upstream": {
                "repository": "https://github.com/facebookresearch/fastText",
                "revision": "0" * 40,
                "sourceSha256": sha256(self.source_archive.read_bytes()),
                "license": "MIT",
            },
            "executable": {
                "sha256": sha256(Path(sys.executable).read_bytes()),
                "version": "fake-fasttext-test-v1",
                "fixedArguments": [str(self.fake)],
                "fixedArgumentFileSha256": [sha256(self.fake.read_bytes())],
            },
            "families": families,
        }

    def write_lock(self, value: dict[str, object] | None = None) -> str:
        self.lock.write_bytes(canonical_bytes(value or self.lock_value()))
        return sha256(self.lock.read_bytes())

    @property
    def lock_sha256(self) -> str:
        return sha256(self.lock.read_bytes())

    def calibrate_arguments(self) -> list[str]:
        return [
            "calibrate",
            "--corpus",
            str(self.corpus),
            "--source-lock",
            str(self.lock),
            "--source-lock-sha256",
            self.lock_sha256,
            "--fasttext-executable",
            sys.executable,
            "--fasttext-source-archive",
            str(self.source_archive),
            "--model-output",
            str(self.model),
            "--plan-output",
            str(self.plan),
        ]

    def locked_test_arguments(self) -> list[str]:
        return [
            "locked-test",
            "--corpus",
            str(self.corpus),
            "--source-lock",
            str(self.lock),
            "--source-lock-sha256",
            self.lock_sha256,
            "--fasttext-executable",
            sys.executable,
            "--fasttext-source-archive",
            str(self.source_archive),
            "--model",
            str(self.model),
            "--plan",
            str(self.plan),
            "--plan-sha256",
            sha256(self.plan.read_bytes()),
            "--report-output",
            str(self.report),
        ]


class TranslationWorthinessFastTextTests(unittest.TestCase):
    def test_pinned_fake_executable_runs_locked_protocol_and_aggregate_evaluator(self) -> None:
        fixture = FastTextFixture()
        self.addCleanup(fixture.close)

        self.assertEqual(0, fasttext.main(fixture.calibrate_arguments()))
        calls_after_calibration = fixture.calls
        self.assertEqual(
            2, sum(call["command"] == "supervised" for call in calls_after_calibration)
        )
        self.assertEqual(
            2, sum(call["command"] == "predict-prob" for call in calls_after_calibration)
        )
        training_exports = [
            call["input"] for call in calls_after_calibration if call["command"] == "supervised"
        ]
        self.assertTrue(all("TRAIN_" in value for value in training_exports))
        self.assertTrue(
            all("CAL_" not in value and "TEST_" not in value for value in training_exports)
        )
        self.assertTrue(all("__label__" in value for value in training_exports))

        plan = json.loads(fixture.plan.read_text(encoding="utf-8"))
        self.assertEqual("family-a", plan["selected"]["familyId"])
        self.assertTrue(plan["researchOnly"])
        self.assertFalse(plan["promotionEligible"])

        self.assertEqual(0, fasttext.main(fixture.locked_test_arguments()))
        locked_calls = fixture.calls[len(calls_after_calibration) :]
        self.assertEqual(1, len(locked_calls))
        self.assertEqual("predict-prob", locked_calls[0]["command"])
        self.assertIn("TEST_", locked_calls[0]["input"])
        self.assertNotIn("__label__", locked_calls[0]["input"])

        report = json.loads(fixture.report.read_text(encoding="utf-8"))
        self.assertEqual("translation-worthiness-evaluation", report["reportType"])
        self.assertEqual("locked-test", report["evaluationPhase"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        self.assertEqual("family-a", report["experiment"]["modelFamily"])
        self.assertEqual(
            {"ambiguous", "human-worthy", "machine", "mixed"},
            set(report["labelDecisionMatrix"]),
        )
        self.assertIn("mixedFacet", report)
        serialized = fixture.report.read_text(encoding="utf-8")
        self.assertNotIn("TEST_HUMAN", serialized)
        self.assertNotIn("test-human", serialized)
        self.assertFalse(list(fixture.root.glob("*.partial")))

    def test_test_labels_cannot_change_training_selection_thresholds_or_scores(self) -> None:
        first = FastTextFixture()
        second = FastTextFixture()
        self.addCleanup(first.close)
        self.addCleanup(second.close)
        changed = corpus_value()
        changed_rows = changed["rows"]
        assert isinstance(changed_rows, list)
        human = next(value for value in changed_rows if value["id"] == "test-human")
        machine = next(value for value in changed_rows if value["id"] == "test-machine")
        human["label"], machine["label"] = machine["label"], human["label"]
        human["adjudication"]["labels"] = [human["label"], human["label"]]
        machine["adjudication"]["labels"] = [machine["label"], machine["label"]]
        second.write_corpus(changed)

        first_model, first_plan_raw = fasttext.calibrate(
            first.corpus,
            first.lock,
            first.lock_sha256,
            Path(sys.executable),
            first.source_archive,
        )
        second_model, second_plan_raw = fasttext.calibrate(
            second.corpus,
            second.lock,
            second.lock_sha256,
            Path(sys.executable),
            second.source_archive,
        )
        first_plan = json.loads(first_plan_raw)
        second_plan = json.loads(second_plan_raw)
        self.assertEqual(first_model, second_model)
        self.assertEqual(first_plan["selected"], second_plan["selected"])
        self.assertEqual(first_plan["calibration"], second_plan["calibration"])
        self.assertEqual(first_plan["phaseHashes"], second_plan["phaseHashes"])
        self.assertNotEqual(first_plan["corpusSha256"], second_plan["corpusSha256"])

    def test_alias_hash_config_model_and_malformed_output_drift_fail_closed(self) -> None:
        fixture = FastTextFixture()
        self.addCleanup(fixture.close)
        alias_arguments = fixture.calibrate_arguments()
        alias_arguments[alias_arguments.index("--model-output") + 1] = str(fixture.corpus)
        self.assertEqual(2, fasttext.main(alias_arguments))
        self.assertFalse(fixture.plan.exists())

        hardlink = fixture.root / "corpus-hardlink.json"
        hardlink.hardlink_to(fixture.corpus)
        hardlink_arguments = fixture.calibrate_arguments()
        hardlink_arguments[hardlink_arguments.index("--model-output") + 1] = str(hardlink)
        self.assertEqual(2, fasttext.main(hardlink_arguments))

        archive_alias_arguments = fixture.calibrate_arguments()
        archive_alias_arguments[archive_alias_arguments.index("--model-output") + 1] = str(
            fixture.source_archive
        )
        self.assertEqual(2, fasttext.main(archive_alias_arguments))

        repository_output = fasttext.REPOSITORY_ROOT / ".fasttext-output-must-not-be-created"
        self.assertFalse(repository_output.exists())
        repository_arguments = fixture.calibrate_arguments()
        repository_arguments[repository_arguments.index("--model-output") + 1] = str(
            repository_output
        )
        self.assertEqual(2, fasttext.main(repository_arguments))
        self.assertFalse(repository_output.exists())

        old_sha256 = fixture.lock_sha256
        fixture.lock.write_bytes(fixture.lock.read_bytes() + b" ")
        drift_arguments = fixture.calibrate_arguments()
        drift_arguments[drift_arguments.index("--source-lock-sha256") + 1] = old_sha256
        self.assertEqual(2, fasttext.main(drift_arguments))
        fixture.write_lock()

        original_archive = fixture.source_archive.read_bytes()
        fixture.source_archive.write_bytes(original_archive + b"drift")
        self.assertEqual(2, fasttext.main(fixture.calibrate_arguments()))
        fixture.source_archive.write_bytes(original_archive)

        bad_executable = fixture.lock_value()
        bad_executable["executable"]["sha256"] = "0" * 64
        fixture.write_lock(bad_executable)
        self.assertEqual(2, fasttext.main(fixture.calibrate_arguments()))

        bad_config = fixture.lock_value()
        bad_config["families"][0]["configSha256"] = "0" * 64
        fixture.write_lock(bad_config)
        self.assertEqual(2, fasttext.main(fixture.calibrate_arguments()))

        bad_model = fixture.lock_value()
        bad_model["families"][0]["modelSha256"] = "0" * 64
        fixture.write_lock(bad_model)
        self.assertEqual(2, fasttext.main(fixture.calibrate_arguments()))

        malformed = FastTextFixture(malformed=True)
        self.addCleanup(malformed.close)
        self.assertEqual(2, fasttext.main(malformed.calibrate_arguments()))
        self.assertFalse(malformed.model.exists())
        self.assertFalse(malformed.plan.exists())

    def test_locked_plan_and_selected_model_tampering_are_rejected_before_test_scoring(
        self,
    ) -> None:
        fixture = FastTextFixture()
        self.addCleanup(fixture.close)
        self.assertEqual(0, fasttext.main(fixture.calibrate_arguments()))
        prior_calls = len(fixture.calls)
        fixture.model.write_bytes(fixture.model.read_bytes() + b"x")
        self.assertEqual(2, fasttext.main(fixture.locked_test_arguments()))
        self.assertEqual(prior_calls, len(fixture.calls))
        self.assertFalse(fixture.report.exists())

        self.assertEqual(0, fasttext.main(fixture.calibrate_arguments()))
        prior_calls = len(fixture.calls)
        old_plan_sha256 = sha256(fixture.plan.read_bytes())
        fixture.plan.write_bytes(fixture.plan.read_bytes() + b" ")
        arguments = fixture.locked_test_arguments()
        arguments[arguments.index("--plan-sha256") + 1] = old_plan_sha256
        self.assertEqual(2, fasttext.main(arguments))
        self.assertEqual(prior_calls, len(fixture.calls))

    def test_reparse_components_are_rejected(self) -> None:
        fixture = FastTextFixture()
        self.addCleanup(fixture.close)
        link = fixture.root / "fake-link.py"
        try:
            link.symlink_to(fixture.fake)
        except OSError:
            original = fasttext._is_reparse

            def simulated_reparse(path: Path) -> bool:
                return Path(path) == fixture.fake or original(path)

            with (
                mock.patch.object(fasttext, "_is_reparse", side_effect=simulated_reparse),
                self.assertRaisesRegex(fasttext.FastTextChallengerError, "reparse point"),
            ):
                fasttext.load_source_lock(
                    fixture.lock,
                    fixture.lock_sha256,
                    Path(sys.executable),
                    fixture.source_archive,
                )
            return
        value = fixture.lock_value()
        value["executable"]["fixedArguments"] = [str(link)]
        value["executable"]["fixedArgumentFileSha256"] = [sha256(fixture.fake.read_bytes())]
        fixture.write_lock(value)
        self.assertEqual(2, fasttext.main(fixture.calibrate_arguments()))


if __name__ == "__main__":
    unittest.main()
