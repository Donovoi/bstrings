from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

ENRICHMENT_ROOT = Path(__file__).resolve().parents[1]
BENCHMARK_ROOT = ENRICHMENT_ROOT / "benchmarks"
for root in (ENRICHMENT_ROOT, BENCHMARK_ROOT):
    if str(root) not in sys.path:
        sys.path.insert(0, str(root))

from translation_worthiness_falsifier import (  # noqa: E402
    DEFAULT_COUNTEREXAMPLES,
    HASH_NAMES,
    LOCKED_ARTIFACT_TYPE,
    MANDATORY_COUNTEREXAMPLE_FAMILIES,
    ContaminationPolicy,
    FalsifierError,
    canonical_json,
    contamination_diagnostics,
    counterexample_report,
    load_counterexamples,
    load_locked_predictions,
    locked_prediction_report,
    paired_fake_translator_report,
)

from translation_worthiness_corpus import (  # noqa: E402
    DEFAULT_CORPUS,
    DEFAULT_SPLITS,
    CorpusRow,
    Prediction,
    ValidatedCorpus,
    validate_corpus,
)


class TranslationWorthinessFalsifierTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    @staticmethod
    def identities() -> dict[str, str]:
        return {name: f"{index:x}" * 64 for index, name in enumerate(HASH_NAMES)}

    @staticmethod
    def artifact(ids: list[str]) -> dict[str, object]:
        identities = TranslationWorthinessFalsifierTests.identities()
        return {
            "schemaVersion": 1,
            "artifactType": LOCKED_ARTIFACT_TYPE,
            "researchOnly": True,
            "promotionEligible": False,
            "phase": "locked-test",
            "frozenBeforeTest": True,
            "identities": identities,
            "thresholds": {"retainMax": 0.2, "suppressMin": 0.8},
            "preregistration": {
                "cells": ["native-static|latin|8-31", "ocr|latin|32-127"],
                "k": 2,
                "familyWiseAlpha": 0.05,
                "method": "bonferroni",
                "perCellAlpha": 0.025,
            },
            "sources": [
                {
                    "kind": "project-synthetic",
                    "license": "project-synthetic",
                    "use": "train-calibration-test",
                    "manualLabels": True,
                    "revision": "synthetic-v1",
                }
            ],
            "privateVeto": {"status": "not-run", "influence": "none"},
            "predictions": [
                {"id": identifier, "score": 0.1, "decision": "retain"} for identifier in ids
            ],
        }

    def write_artifact(self, value: object, *, raw: str | None = None) -> Path:
        path = self.root / "locked-predictions.json"
        path.write_text(raw if raw is not None else canonical_json(value), encoding="utf-8")
        return path

    def test_bundled_contamination_is_aggregate_diagnostic_only(self) -> None:
        corpus = validate_corpus(DEFAULT_CORPUS, DEFAULT_SPLITS)

        report = contamination_diagnostics(corpus)

        self.assertEqual("diagnostic-only", report["policyMode"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        self.assertGreater(report["crossSplitGroupPairs"], 0)
        self.assertGreaterEqual(report["tokenShingleOverlapPairs"], 0)
        self.assertGreaterEqual(report["characterNgramOverlapPairs"], 0)
        serialized = canonical_json(report)
        self.assertNotIn('"id"', serialized)
        self.assertNotIn('"text"', serialized)
        self.assertNotIn("0042", serialized)
        self.assertNotIn("A7", serialized)

    def test_contamination_normalizes_templates_and_fails_only_with_explicit_policy(self) -> None:
        left = CorpusRow(
            "left-row",
            "Revisio\u0301n   necesaria 0042",
            "human-worthy",
            "left-group",
            "plain-language",
            "native-static",
            "latin",
            "8-31",
        )
        right = CorpusRow(
            "right-row",
            "REVISIÓN NECESARIA 0042",
            "human-worthy",
            "right-group",
            "plain-language",
            "ocr",
            "latin",
            "8-31",
        )
        corpus = ValidatedCorpus((left, right), {"left-group": "train", "right-group": "test"})

        diagnostic = contamination_diagnostics(corpus)
        self.assertEqual(1, diagnostic["normalizedExactOverlapPairs"])
        self.assertEqual(1.0, diagnostic["maximumTokenShingleJaccard"])
        self.assertEqual(1.0, diagnostic["maximumCharacterNgramJaccard"])
        with self.assertRaisesRegex(FalsifierError, "explicit policy"):
            contamination_diagnostics(corpus, ContaminationPolicy(maximum_normalized_exact_pairs=0))

    def test_mandatory_counterexamples_cover_every_family_and_fail_open(self) -> None:
        rows = load_counterexamples(DEFAULT_COUNTEREXAMPLES)
        predictions = {row.identifier: Prediction(0.1, "retain") for row in rows}

        report = counterexample_report(rows, predictions)

        self.assertEqual(set(MANDATORY_COUNTEREXAMPLE_FAMILIES), {row.family for row in rows})
        self.assertEqual(0, report["falseSuppressions"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        serialized = canonical_json(report)
        self.assertNotIn('"id"', serialized)
        self.assertNotIn('"text"', serialized)
        for row in rows:
            self.assertNotIn(row.identifier, serialized)
            self.assertNotIn(row.text, serialized)

    def test_mandatory_counterexamples_reject_one_false_suppression(self) -> None:
        rows = load_counterexamples(DEFAULT_COUNTEREXAMPLES)
        predictions = {row.identifier: Prediction(0.1, "retain") for row in rows}
        protected = next(row for row in rows if row.label != "machine")
        predictions[protected.identifier] = Prediction(0.9, "suppress")

        with self.assertRaisesRegex(FalsifierError, "counterexample was suppressed"):
            counterexample_report(rows, predictions)

    def test_locked_artifact_validates_identities_thresholds_and_aggregate_report(self) -> None:
        ids = ["row-one", "row-two", "row-three"]
        value = self.artifact(ids)
        value["predictions"] = [
            {"id": ids[0], "score": 0.1, "decision": "retain"},
            {"id": ids[1], "score": 0.5, "decision": "abstain"},
            {"id": ids[2], "score": 0.9, "decision": "suppress"},
        ]

        locked = load_locked_predictions(
            self.write_artifact(value),
            expected_ids=set(ids),
            expected_identities=self.identities(),
        )
        report = locked_prediction_report(locked)

        self.assertEqual(2, report["preregisteredK"])
        self.assertEqual("bonferroni", report["simultaneousMethod"])
        self.assertEqual(5, report["identityChecks"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        serialized = canonical_json(report)
        for identifier in ids:
            self.assertNotIn(identifier, serialized)
        for digest in self.identities().values():
            self.assertNotIn(digest, serialized)

    def test_locked_artifact_rejects_malformed_and_mismatched_hashes(self) -> None:
        ids = ["row-one"]
        malformed = self.artifact(ids)
        malformed["identities"]["model"] = "A" * 64
        with self.assertRaisesRegex(FalsifierError, "malformed or mismatched"):
            load_locked_predictions(
                self.write_artifact(malformed),
                expected_ids=set(ids),
                expected_identities=self.identities(),
            )

        mismatch = self.artifact(ids)
        mismatch["identities"]["model"] = "f" * 64
        with self.assertRaisesRegex(FalsifierError, "malformed or mismatched"):
            load_locked_predictions(
                self.write_artifact(mismatch),
                expected_ids=set(ids),
                expected_identities=self.identities(),
            )

    def test_locked_artifact_rejects_threshold_and_score_decision_inconsistency(self) -> None:
        ids = ["row-one"]
        reversed_thresholds = self.artifact(ids)
        reversed_thresholds["thresholds"] = {"retainMax": 0.9, "suppressMin": 0.8}
        with self.assertRaisesRegex(FalsifierError, "abstention interval"):
            load_locked_predictions(
                self.write_artifact(reversed_thresholds),
                expected_ids=set(ids),
                expected_identities=self.identities(),
            )

        inconsistent = self.artifact(ids)
        inconsistent["predictions"] = [{"id": ids[0], "score": 0.95, "decision": "retain"}]
        with self.assertRaisesRegex(FalsifierError, "inconsistent"):
            load_locked_predictions(
                self.write_artifact(inconsistent),
                expected_ids=set(ids),
                expected_identities=self.identities(),
            )

        non_finite = self.artifact(ids)
        non_finite["predictions"] = [{"id": ids[0], "score": float("nan"), "decision": "abstain"}]
        with self.assertRaisesRegex(FalsifierError, "non-finite"):
            load_locked_predictions(
                self.write_artifact(non_finite),
                expected_ids=set(ids),
                expected_identities=self.identities(),
            )

    def test_locked_artifact_rejects_schema_drift_duplicates_and_incomplete_coverage(self) -> None:
        ids = ["row-one", "row-two"]
        extra = self.artifact(ids)
        extra["unexpected"] = True
        duplicate = self.artifact(ids)
        duplicate["predictions"] = [
            {"id": ids[0], "score": 0.1, "decision": "retain"},
            {"id": ids[0], "score": 0.1, "decision": "retain"},
        ]
        incomplete = self.artifact(ids)
        incomplete["predictions"] = [{"id": ids[0], "score": 0.1, "decision": "retain"}]

        for value in (extra, duplicate, incomplete):
            with self.subTest(case=value), self.assertRaises(FalsifierError):
                load_locked_predictions(
                    self.write_artifact(value),
                    expected_ids=set(ids),
                    expected_identities=self.identities(),
                )

        duplicate_json = canonical_json(self.artifact(ids)).replace(
            '"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1', 1
        )
        with self.assertRaisesRegex(FalsifierError, "duplicate object"):
            load_locked_predictions(
                self.write_artifact({}, raw=duplicate_json),
                expected_ids=set(ids),
                expected_identities=self.identities(),
            )

    def test_locked_artifact_enforces_preregistered_k_and_simultaneous_bounds(self) -> None:
        ids = ["row-one"]
        cases = []
        bad_k = self.artifact(ids)
        bad_k["preregistration"]["k"] = 1
        cases.append(bad_k)
        unadjusted = self.artifact(ids)
        unadjusted["preregistration"]["perCellAlpha"] = 0.05
        cases.append(unadjusted)
        wrong_method = self.artifact(ids)
        wrong_method["preregistration"]["method"] = "unadjusted"
        cases.append(wrong_method)

        for value in cases:
            with (
                self.subTest(preregistration=value["preregistration"]),
                self.assertRaises(FalsifierError),
            ):
                load_locked_predictions(
                    self.write_artifact(value),
                    expected_ids=set(ids),
                    expected_identities=self.identities(),
                )

    def test_locked_artifact_enforces_phase_licensing_and_private_veto_only(self) -> None:
        ids = ["row-one"]
        phase = self.artifact(ids)
        phase["frozenBeforeTest"] = False
        nlon = self.artifact(ids)
        nlon["sources"] = [
            {
                "kind": "nlon",
                "license": "GPL-3.0",
                "use": "train",
                "manualLabels": True,
                "revision": "synthetic-test",
            }
        ]
        codesearchnet_blanket = self.artifact(ids)
        codesearchnet_blanket["sources"] = [
            {
                "kind": "codesearchnet",
                "license": "MIT",
                "use": "manually-labelled",
                "manualLabels": True,
                "revision": "synthetic-test",
            }
        ]
        evaluation_leak = self.artifact(ids)
        evaluation_leak["sources"] = [
            {
                "kind": "glotocr",
                "license": "recorded-evaluation-license",
                "use": "train",
                "manualLabels": True,
                "revision": "synthetic-test",
            }
        ]
        private_approval = self.artifact(ids)
        private_approval["privateVeto"] = {"status": "pass", "influence": "approve"}

        for value in (
            phase,
            nlon,
            codesearchnet_blanket,
            evaluation_leak,
            private_approval,
        ):
            with self.subTest(value=value), self.assertRaises(FalsifierError):
                load_locked_predictions(
                    self.write_artifact(value),
                    expected_ids=set(ids),
                    expected_identities=self.identities(),
                )

    def test_paired_fake_translator_separates_incremental_savings_from_cache_paths(self) -> None:
        report = paired_fake_translator_report()

        baseline = report["baseline"]
        candidate = report["candidate"]
        attribution = report["attribution"]
        self.assertTrue(report["cacheStatesIdenticalAtStart"])
        self.assertTrue(report["runCacheStatesIdenticalAtStart"])
        self.assertGreater(attribution["proposalsOnProtectedOnlyPath"], 0)
        self.assertGreater(attribution["proposalsOnRunCachePath"], 0)
        self.assertGreater(attribution["proposalsOnPersistentCachePath"], 0)
        self.assertGreater(attribution["proposalsOnModelInputPath"], 0)
        self.assertEqual(
            baseline["translatorInputTexts"] - candidate["translatorInputTexts"],
            attribution["learnedOnlyTranslatorInputSavings"],
        )
        self.assertEqual(0, attribution["protectedOnlySavingsCredited"])
        self.assertEqual(0, attribution["runCacheSavingsCredited"])
        self.assertEqual(0, attribution["persistentCacheSavingsCredited"])
        self.assertEqual(
            baseline["protectedOnlyBypassTexts"],
            candidate["protectedOnlyBypassTexts"],
        )
        self.assertEqual(baseline["persistentCacheHits"], candidate["persistentCacheHits"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        serialized = canonical_json(report)
        self.assertNotIn('"text"', serialized)
        self.assertNotIn("Mensaje", serialized)
        self.assertNotIn("Bonjour", serialized)
        self.assertNotIn("01234567", serialized)


if __name__ == "__main__":
    unittest.main()
