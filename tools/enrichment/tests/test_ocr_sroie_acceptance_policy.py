from __future__ import annotations

import copy
import hashlib
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import ocr_sroie_acceptance_policy as policy  # noqa: E402


def coverage(matched: int, total: int) -> dict:
    value = matched / total
    return {
        "falseNegative": total - matched,
        "falsePositive": total - matched,
        "hmean": value,
        "matchedPredictions": matched,
        "matchedTruths": matched,
        "precision": value,
        "predictedUnits": total,
        "recall": value,
        "referenceUnits": total,
        "truePositive": matched,
    }


def metrics(identities: list[dict]) -> dict:
    per_document = [
        {
            "textNormalization": policy.PRIMARY_TEXT_NORMALIZATION,
            "counts": {
                "characterEdits": 8,
                "matchingCharacters": 92,
                "matchingTokensOrderInvariant": 90,
                "matchingWords": 85,
                "predictedCharacters": 100,
                "predictedWords": 100,
                "referenceCharacters": 100,
                "referenceWords": 100,
                "wordEdits": 15,
            },
            "detection": coverage(92, 100),
            "endToEndExact": coverage(70, 100),
            "imageSha256": identity["imageSha256"],
            "pageCer": 0.08,
            "pageWer": 0.15,
            "rowIndex": identity["rowIndex"],
            "tokenF1": 0.90,
        }
        for identity in identities
    ]
    documents = len(per_document)
    counts = {
        key: sum(item["counts"][key] for item in per_document) for key in per_document[0]["counts"]
    }
    micro = {
        "counts": counts,
        "detection": coverage(92 * documents, 100 * documents),
        "documents": documents,
        "endToEndExact": coverage(70 * documents, 100 * documents),
        "pageCer": 0.08,
        "pageWer": 0.15,
        "tokenF1": 0.90,
    }
    macro = {"pageCer": 0.08, "pageWer": 0.15, "tokenF1": 0.90}
    strict_per_document = copy.deepcopy(per_document)
    for item in strict_per_document:
        item["textNormalization"] = policy.DIAGNOSTIC_TEXT_NORMALIZATION
    strict_metrics = {
        "textNormalization": policy.DIAGNOSTIC_TEXT_NORMALIZATION,
        "macro": copy.deepcopy(macro),
        "micro": copy.deepcopy(micro),
        "perDocument": strict_per_document,
    }
    strict_per_document_bytes = "".join(
        policy.canonical_json(item) + "\n" for item in strict_per_document
    ).encode("utf-8")
    return {
        "textNormalization": policy.PRIMARY_TEXT_NORMALIZATION,
        "caseSensitiveDiagnostics": {
            "metrics": strict_metrics,
            "metricsSha256": policy.sha256_canonical(strict_metrics),
            "perDocumentMetricsSha256": hashlib.sha256(strict_per_document_bytes).hexdigest(),
            "textNormalization": policy.DIAGNOSTIC_TEXT_NORMALIZATION,
        },
        "macro": macro,
        "micro": micro,
        "perDocument": per_document,
    }


def identity(selected_documents: int) -> dict:
    runtime = {
        name: {
            "executableSha256": character * 64,
            "inventorySha256": character.upper().lower() * 64,
            "pythonVersion": "3.13.5",
            "requestedProvider": name,
        }
        for name, character in (("benchmark", "1"), ("cpu", "2"), ("directml", "3"))
    }
    source_keys = {
        "acceptanceWrapperSha256",
        "cordScorerSha256",
        "genericBenchmarkSha256",
        "genericPolicySha256",
        "scoringConstantsSha256",
        "sroieAdapterSha256",
        "sroiePolicySha256",
    }
    return {
        "benchmark": {name: "a" * 64 for name in source_keys},
        "calibrationCorpus": {
            "bboxRepairAuditSha256": "8" * 64,
            "bboxRepairRecordsSha256": "9" * 64,
            "corpusManifestSha256": "b" * 64,
            "duplicateAuditSha256": "c" * 64,
            "excludedRows": policy.RAW_TRAIN_ROWS - selected_documents,
            "imageIdentitiesSha256": "d" * 64,
            "parquetSha256": policy.TRAIN_SHA256,
            "repairedRegionCount": 1,
            "scoringAnnotationIdentitiesSha256": "0" * 64,
            "selectedDocuments": selected_documents,
            "sourceImageDigestsSha256": "e" * 64,
            "sourcePayloadIdentitiesSha256": "7" * 64,
            "sourceRegionCount": 1000,
            "sourceRows": policy.RAW_TRAIN_ROWS,
            "workerManifestSha256": "f" * 64,
        },
        "dataset": policy.dataset_identity(),
        "dependencies": {"geos": "3.13.1", "pyarrow": "25.0.0", "shapely": "2.1.2"},
        "engine": {
            "modelId": "offline/model",
            "modelPackSha256": "4" * 64,
            "modelRevision": "revision",
            "name": "rapidocr",
            "version": "3.9.2",
        },
        "protocol": policy.PROTOCOL,
        "runtimeProfiles": runtime,
        "schemaVersion": policy.SCHEMA_VERSION,
        "worker": {"sha256": "5" * 64},
    }


def report(identities: list[dict], candidate: dict) -> dict:
    value = metrics(identities)
    return {
        "acceptancePassed": True,
        "documents": len(identities),
        "evaluationCompleted": True,
        "evaluationRole": "calibration",
        "evidence": {},
        "expectedIdentitiesSha256": policy.sha256_canonical(identities),
        "identity": candidate,
        "identitySha256": policy.sha256_canonical(candidate),
        "integrityPassed": True,
        "finalDisposition": "accepted",
        "metrics": value,
        "metricsSha256": policy.sha256_canonical(value),
        "protocol": policy.PROTOCOL,
        "rawRows": policy.RAW_TRAIN_ROWS,
        "runSucceeded": True,
        "schemaVersion": policy.METRICS_REPORT_SCHEMA_VERSION,
    }


class SroiePolicyTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.identities = [
            {"imageSha256": "6" * 64, "rowIndex": 0},
            {"imageSha256": "7" * 64, "rowIndex": 2},
        ]
        self.identity = identity(len(self.identities))
        self.report = report(self.identities, self.identity)
        self.report_path = self.root / "calibration-report.json"
        self.report_path.write_bytes(policy.encoded_policy(self.report))
        self.report_sha256 = hashlib.sha256(self.report_path.read_bytes()).hexdigest()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def build(self) -> dict:
        return policy.build_policy(
            calibration_report=self.report,
            calibration_report_sha256=self.report_sha256,
            expected_identity=self.identity,
            expected_identities=self.identities,
            frozen_at_utc="2026-08-05T00:00:00Z",
        )

    def test_dataset_identity_distinguishes_raw_rows_from_selected_documents(self) -> None:
        dataset = policy.dataset_identity()
        self.assertEqual(626, dataset["train"]["rows"])
        self.assertEqual(361, dataset["test"]["rows"])
        built = self.build()
        self.assertEqual(2, built["calibration"]["documents"])
        self.assertEqual(626, built["calibration"]["rawRows"])

    def test_bbox_repair_identity_and_count_tampering_fails_closed(self) -> None:
        corpus = self.identity["calibrationCorpus"]
        for name in (
            "bboxRepairAuditSha256",
            "bboxRepairRecordsSha256",
            "sourcePayloadIdentitiesSha256",
            "scoringAnnotationIdentitiesSha256",
        ):
            with self.subTest(name=name):
                wrong = copy.deepcopy(self.identity)
                wrong["calibrationCorpus"][name] = "z" * 64
                with self.assertRaisesRegex(policy.PolicyError, "lowercase SHA-256"):
                    policy._validate_identity(wrong)
        for source_regions, repaired_regions in ((0, 0), (10, 11), (True, 0), (10, True), (10, -1)):
            with self.subTest(source_regions=source_regions, repaired_regions=repaired_regions):
                wrong = copy.deepcopy(self.identity)
                wrong["calibrationCorpus"]["sourceRegionCount"] = source_regions
                wrong["calibrationCorpus"]["repairedRegionCount"] = repaired_regions
                with self.assertRaisesRegex(policy.PolicyError, "row accounting"):
                    policy._validate_identity(wrong)
        missing = copy.deepcopy(self.identity)
        del missing["calibrationCorpus"]["bboxRepairAuditSha256"]
        with self.assertRaisesRegex(policy.PolicyError, "schema changed"):
            policy._validate_identity(missing)
        self.assertEqual(1000, corpus["sourceRegionCount"])

    def test_reports_bind_primary_and_case_sensitive_scoring_profiles(self) -> None:
        wrong = copy.deepcopy(self.report)
        wrong["metrics"]["textNormalization"] = policy.DIAGNOSTIC_TEXT_NORMALIZATION
        wrong["metricsSha256"] = policy.sha256_canonical(wrong["metrics"])
        with self.assertRaisesRegex(policy.PolicyError, "primary text normalization"):
            policy.build_policy(
                calibration_report=wrong,
                calibration_report_sha256=self.report_sha256,
                expected_identity=self.identity,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )
        wrong = copy.deepcopy(self.report)
        del wrong["metrics"]["caseSensitiveDiagnostics"]["perDocumentMetricsSha256"]
        wrong["metricsSha256"] = policy.sha256_canonical(wrong["metrics"])
        with self.assertRaisesRegex(policy.PolicyError, "diagnostic schema"):
            policy.build_policy(
                calibration_report=wrong,
                calibration_report_sha256=self.report_sha256,
                expected_identity=self.identity,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )
        wrong = copy.deepcopy(self.report)
        wrong["metrics"]["perDocument"][0]["textNormalization"] = (
            policy.DIAGNOSTIC_TEXT_NORMALIZATION
        )
        wrong["metricsSha256"] = policy.sha256_canonical(wrong["metrics"])
        with self.assertRaisesRegex(policy.PolicyError, "per-document text normalization"):
            policy.build_policy(
                calibration_report=wrong,
                calibration_report_sha256=self.report_sha256,
                expected_identity=self.identity,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )
        wrong = copy.deepcopy(self.report)
        strict = wrong["metrics"]["caseSensitiveDiagnostics"]["metrics"]
        strict["micro"]["tokenF1"] = 1.0
        diagnostic = wrong["metrics"]["caseSensitiveDiagnostics"]
        diagnostic["metricsSha256"] = policy.sha256_canonical(strict)
        wrong["metricsSha256"] = policy.sha256_canonical(wrong["metrics"])
        with self.assertRaisesRegex(policy.PolicyError, "diagnostic metrics are invalid"):
            policy.build_policy(
                calibration_report=wrong,
                calibration_report_sha256=self.report_sha256,
                expected_identity=self.identity,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )

    def test_policy_round_trip_and_dynamic_confirmatory_count(self) -> None:
        path = self.root / "policy.json"
        policy.publish_policy(path, self.build())
        validated = policy.validate_policy(
            path,
            calibration_report_path=self.report_path,
            expected_identity=self.identity,
            expected_identities=self.identities,
        )
        confirmatory = [
            {"imageSha256": "8" * 64, "rowIndex": 1},
            {"imageSha256": "9" * 64, "rowIndex": 5},
            {"imageSha256": "0" * 64, "rowIndex": 8},
        ]
        evaluated = policy.evaluate_confirmatory(
            validated,
            metrics(confirmatory),
            expected_identities=confirmatory,
        )
        self.assertTrue(evaluated["passed"])
        self.assertTrue(evaluated["absoluteFloorsPassed"])

    def test_v2_report_and_policy_cannot_cross_the_v3_boundary(self) -> None:
        legacy_report = copy.deepcopy(self.report)
        legacy_report["schemaVersion"] = 1
        legacy_report["protocol"] = "bstrings-icdar2019-sroie-train-calibration-test-one-shot-v2"
        with self.assertRaisesRegex(policy.PolicyError, "frozen protocol"):
            policy.build_policy(
                calibration_report=legacy_report,
                calibration_report_sha256=self.report_sha256,
                expected_identity=self.identity,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )

        legacy_policy = self.build()
        legacy_policy.update(
            {
                "schemaVersion": 1,
                "policyId": "bstrings-icdar2019-sroie-train-test-ocr-v2",
                "protocol": "bstrings-icdar2019-sroie-train-calibration-test-one-shot-v2",
            }
        )
        legacy_policy_path = self.root / "legacy-policy.json"
        legacy_policy_path.write_bytes(policy.encoded_policy(legacy_policy))
        with self.assertRaisesRegex(policy.PolicyError, "protocol, dataset, or metrics"):
            policy.validate_policy(
                legacy_policy_path,
                calibration_report_path=self.report_path,
                expected_identity=self.identity,
                expected_identities=self.identities,
            )

    def test_calibration_selected_count_and_duplicate_audit_are_bound(self) -> None:
        wrong = copy.deepcopy(self.identity)
        wrong["calibrationCorpus"]["selectedDocuments"] = 3
        with self.assertRaisesRegex(policy.PolicyError, "row accounting"):
            policy.build_policy(
                calibration_report=self.report,
                calibration_report_sha256=self.report_sha256,
                expected_identity=wrong,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )
        wrong = copy.deepcopy(self.identity)
        wrong["calibrationCorpus"]["duplicateAuditSha256"] = "1" * 64
        with self.assertRaisesRegex(policy.PolicyError, "report candidate identity"):
            policy.build_policy(
                calibration_report=self.report,
                calibration_report_sha256=self.report_sha256,
                expected_identity=wrong,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )

    def test_non_partitioning_duplicate_accounting_fails_closed(self) -> None:
        wrong = copy.deepcopy(self.identity)
        wrong["calibrationCorpus"]["excludedRows"] -= 1
        with self.assertRaisesRegex(policy.PolicyError, "row accounting"):
            policy.build_policy(
                calibration_report=self.report,
                calibration_report_sha256=self.report_sha256,
                expected_identity=wrong,
                expected_identities=self.identities,
                frozen_at_utc="2026-08-05T00:00:00Z",
            )

    def test_policy_threshold_tampering_is_rejected(self) -> None:
        value = self.build()
        value["thresholds"]["exactTokenF1"] -= 0.01
        path = self.root / "policy.json"
        path.write_bytes(policy.encoded_policy(value))
        with self.assertRaisesRegex(policy.PolicyError, "not derived"):
            policy.validate_policy(
                path,
                calibration_report_path=self.report_path,
                expected_identity=self.identity,
                expected_identities=self.identities,
            )

    def test_policy_validation_rederives_the_exact_calibration_report(self) -> None:
        path = self.root / "policy.json"
        policy.publish_policy(path, self.build())
        changed = copy.deepcopy(self.report)
        changed["metrics"]["perDocument"][0]["tokenF1"] = 0.91
        changed["metricsSha256"] = policy.sha256_canonical(changed["metrics"])
        self.report_path.write_bytes(policy.encoded_policy(changed))
        with self.assertRaises(policy.PolicyError):
            policy.validate_policy(
                path,
                calibration_report_path=self.report_path,
                expected_identity=self.identity,
                expected_identities=self.identities,
            )

    def test_policy_reader_rejects_a_leaf_symlink(self) -> None:
        target = self.root / "policy-target.json"
        target.write_bytes(policy.encoded_policy(self.build()))
        link = self.root / "policy-link.json"
        try:
            link.symlink_to(target)
        except OSError as exc:
            self.skipTest(f"symlinks unavailable: {exc}")
        with self.assertRaisesRegex(policy.PolicyError, "unsafe"):
            policy._strict_json(link)


if __name__ == "__main__":
    unittest.main()
