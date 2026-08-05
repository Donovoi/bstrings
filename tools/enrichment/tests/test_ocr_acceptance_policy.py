from __future__ import annotations

import copy
import hashlib
import math
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import ocr_acceptance_policy as policy  # noqa: E402

ROLE_SELECTIONS = policy.load_role_selections(
    Path(__file__).resolve().parents[1] / "cord-v2-train-selection-v1.json"
)
CALIBRATION_SELECTION = ROLE_SELECTIONS["calibration"]
CONFIRMATORY_SELECTION = ROLE_SELECTIONS["confirmatory"]


def make_coverage(matched: int, total: int) -> dict:
    precision = matched / total
    recall = matched / total
    hmean = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {
        "falseNegative": total - matched,
        "falsePositive": total - matched,
        "hmean": hmean,
        "matchedPredictions": matched,
        "matchedTruths": matched,
        "precision": precision,
        "predictedUnits": total,
        "recall": recall,
        "referenceUnits": total,
        "truePositive": matched,
    }


def make_metrics_for_identities(identities: list[dict]) -> dict:
    documents = len(identities)
    per_document = [
        {
            "imageSha256": identity["imageSha256"],
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
            "detection": make_coverage(92, 100),
            "endToEndExact": make_coverage(70, 100),
            "pageCer": 0.08,
            "pageWer": 0.15,
            "rowIndex": identity["rowIndex"],
            "tokenF1": 0.90,
        }
        for identity in identities
    ]
    micro_counts = {
        key: sum(item["counts"][key] for item in per_document) for key in per_document[0]["counts"]
    }
    return {
        "macro": {"pageCer": 0.08, "pageWer": 0.15, "tokenF1": 0.90},
        "micro": {
            "counts": micro_counts,
            "detection": make_coverage(92 * documents, 100 * documents),
            "documents": documents,
            "endToEndExact": make_coverage(70 * documents, 100 * documents),
            "pageCer": 0.08,
            "pageWer": 0.15,
            "tokenF1": 0.90,
        },
        "perDocument": per_document,
    }


def make_metrics(selection: policy.ValidatedRoleSelection) -> dict:
    identities = policy.validate_role_selection(selection, expected_role=selection.role)
    return make_metrics_for_identities(identities)


def make_explicit_identities(count: int = 4) -> list[dict]:
    return [
        {
            "imageSha256": hashlib.sha256(f"synthetic-image-{index}".encode()).hexdigest(),
            "rowIndex": 1000 + index,
        }
        for index in range(count)
    ]


def refresh_micro_counts(metrics: dict) -> None:
    metrics["micro"]["counts"] = {
        key: sum(item["counts"][key] for item in metrics["perDocument"])
        for key in metrics["perDocument"][0]["counts"]
    }


def write_metrics_report(
    directory: Path,
    name: str,
    *,
    selection: policy.ValidatedRoleSelection,
    identity: dict,
    metrics: dict,
    acceptance_passed: bool = True,
    generated_at_utc: str = "2026-08-05T00:00:00Z",
) -> Path:
    value = {
        "acceptancePassed": acceptance_passed,
        "documents": len(selection.entries),
        "evaluationRole": selection.role,
        "evidence": {"fixture": True, "generatedAtUtc": generated_at_utc},
        "identity": identity,
        "integrityPassed": True,
        "metrics": metrics,
        "metricsSha256": policy.sha256_canonical(metrics),
        "runSucceeded": True,
        "schemaVersion": policy.METRICS_REPORT_SCHEMA_VERSION,
        "selectionSha256": selection.entries_sha256,
    }
    path = directory / name
    path.write_bytes(policy.encoded_policy(value))
    return path


def write_policy_value(directory: Path, name: str, value: dict) -> Path:
    path = directory / name
    path.write_bytes(policy.encoded_policy(value))
    return path


def make_identity() -> dict:
    module_sha256 = hashlib.sha256(Path(policy.__file__).read_bytes()).hexdigest()
    return {
        "benchmark": {
            "genericBenchmarkSha256": policy.GENERIC_BENCHMARK_SHA256,
            "policyModuleSha256": module_sha256,
            "protocol": policy.CORE_SCORER_PROTOCOL,
            "schemaVersion": policy.CORE_SCORER_SCHEMA_VERSION,
            "scorerSha256": policy.CORE_SCORER_SHA256,
            "scoringConstantsSha256": policy.SCORING_CONSTANTS_SHA256,
            "scriptSha256": "3" * 64,
        },
        "corpus": {
            "calibrationCorpusManifestSha256": "4" * 64,
            "calibrationSelectionSha256": policy.SELECTION_ENTRIES_SHA256["calibration"],
            "calibrationWorkerManifestSha256": "5" * 64,
            "completeSelectionSha256": policy.SELECTION_ENTRIES_SHA256["complete"],
            "confirmatoryInputManifestSha256": "b" * 64,
            "confirmatorySelectionSha256": policy.SELECTION_ENTRIES_SHA256["confirmatory"],
            "confirmatoryWorkerManifestSha256": "c" * 64,
            "datasetId": policy.DATASET_ID,
            "datasetRevision": policy.DATASET_REVISION,
            "selectionManifestBytes": policy.SELECTION_MANIFEST_BYTES,
            "selectionManifestSha256": policy.SELECTION_MANIFEST_SHA256,
            "trainShardSha256": list(policy.TRAIN_SHARD_SHA256),
        },
        "dependencies": {"geos": "3.13.1", "pyarrow": "25.0.0", "shapely": "2.1.2"},
        "engine": {
            "modelId": policy.MODEL_ID,
            "modelPackSha256": policy.MODEL_PACK_SHA256,
            "modelRevision": policy.MODEL_REVISION,
            "name": policy.ENGINE_NAME,
            "version": policy.ENGINE_VERSION,
            "workerSha256": "6" * 64,
        },
        "runtimeProfiles": {
            "benchmark": {
                "executableSha256": "d" * 64,
                "inventorySha256": "e" * 64,
                "pythonVersion": "3.14.6",
            },
            "cpu": {
                "executableSha256": "7" * 64,
                "inventorySha256": "8" * 64,
                "pythonVersion": "3.14.6",
            },
            "directml": {
                "executableSha256": "9" * 64,
                "inventorySha256": "a" * 64,
                "pythonVersion": "3.14.5",
            },
        },
    }


class OcrAcceptancePolicyTests(unittest.TestCase):
    def test_explicit_identity_recomputation_matches_frozen_cord_paths(self) -> None:
        for selection in (CALIBRATION_SELECTION, CONFIRMATORY_SELECTION):
            with self.subTest(role=selection.role):
                identities = policy.validate_role_selection(
                    selection,
                    expected_role=selection.role,
                )
                metrics = make_metrics(selection)
                self.assertEqual(
                    policy.extract_measurements(metrics, selection=selection),
                    policy.extract_measurements_for_identities(
                        metrics,
                        expected_identities=identities,
                    ),
                )

    def test_explicit_identities_require_exact_order_count_and_unique_rows(self) -> None:
        identities = make_explicit_identities()
        metrics = make_metrics_for_identities(identities)
        self.assertEqual(
            0.90,
            policy.extract_measurements_for_identities(
                metrics,
                expected_identities=identities,
            )["exactTokenF1"],
        )

        with self.assertRaisesRegex(policy.PolicyError, "exactly 3"):
            policy.extract_measurements_for_identities(
                metrics,
                expected_identities=identities[:-1],
            )

        reordered = list(identities)
        reordered[0], reordered[1] = reordered[1], reordered[0]
        with self.assertRaisesRegex(policy.PolicyError, "expected identities"):
            policy.extract_measurements_for_identities(
                metrics,
                expected_identities=reordered,
            )

        duplicated = copy.deepcopy(identities)
        duplicated[1]["rowIndex"] = duplicated[0]["rowIndex"]
        with self.assertRaisesRegex(policy.PolicyError, "invalid or duplicated"):
            policy.extract_measurements_for_identities(
                metrics,
                expected_identities=duplicated,
            )

        extra_field = copy.deepcopy(identities)
        extra_field[0]["unexpected"] = True
        with self.assertRaisesRegex(policy.PolicyError, "unknown fields"):
            policy.extract_measurements_for_identities(
                metrics,
                expected_identities=extra_field,
            )

        with self.assertRaisesRegex(policy.PolicyError, "non-empty ordered sequence"):
            policy.extract_measurements_for_identities(metrics, expected_identities=[])

    def test_explicit_identity_path_rejects_aggregate_and_document_tampering(self) -> None:
        identities = make_explicit_identities()

        aggregate_tampered = make_metrics_for_identities(identities)
        aggregate_tampered["micro"]["tokenF1"] = 0.91
        with self.assertRaisesRegex(policy.PolicyError, "micro acceptance metrics"):
            policy.extract_measurements_for_identities(
                aggregate_tampered,
                expected_identities=identities,
            )

        document_tampered = make_metrics_for_identities(identities)
        document_tampered["perDocument"][0]["tokenF1"] = 0.91
        with self.assertRaisesRegex(policy.PolicyError, "not derived from its counts"):
            policy.extract_measurements_for_identities(
                document_tampered,
                expected_identities=identities,
            )

    def test_nearest_rank_and_strict_tail_fraction_are_exact(self) -> None:
        self.assertEqual(1.0, policy.nearest_rank([4.0, 1.0, 3.0, 2.0], 0.25))
        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["tokenF1"] = 0.50
        metrics["perDocument"][0]["counts"]["matchingTokensOrderInvariant"] = 50
        metrics["perDocument"][0]["counts"]["matchingWords"] = 50
        metrics["perDocument"][0]["counts"]["wordEdits"] = 50
        metrics["perDocument"][0]["endToEndExact"] = make_coverage(50, 100)
        metrics["perDocument"][0]["pageWer"] = 0.50
        metrics["perDocument"][1]["tokenF1"] = 0.49
        metrics["perDocument"][1]["counts"]["matchingTokensOrderInvariant"] = 49
        metrics["perDocument"][1]["counts"]["matchingWords"] = 49
        metrics["perDocument"][1]["counts"]["wordEdits"] = 51
        metrics["perDocument"][1]["endToEndExact"] = make_coverage(49, 100)
        metrics["perDocument"][1]["pageWer"] = 0.51
        exact_matches = sum(
            item["endToEndExact"]["matchedPredictions"] for item in metrics["perDocument"]
        )
        exact_units = sum(
            item["endToEndExact"]["predictedUnits"] for item in metrics["perDocument"]
        )
        metrics["micro"]["endToEndExact"] = make_coverage(exact_matches, exact_units)
        metrics["micro"]["tokenF1"] = (
            sum(item["counts"]["matchingTokensOrderInvariant"] for item in metrics["perDocument"])
            / policy.CALIBRATION_DOCUMENTS
            / 100
        )
        metrics["macro"]["tokenF1"] = (
            sum(item["tokenF1"] for item in metrics["perDocument"]) / policy.CALIBRATION_DOCUMENTS
        )
        metrics["macro"]["pageWer"] = (
            sum(item["pageWer"] for item in metrics["perDocument"]) / policy.CALIBRATION_DOCUMENTS
        )
        metrics["micro"]["pageWer"] = metrics["macro"]["pageWer"]
        refresh_micro_counts(metrics)
        measurements = policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)
        self.assertEqual(1 / policy.CALIBRATION_DOCUMENTS, measurements["fractionTokenF1BelowHalf"])

    def test_policy_derivation_uses_stricter_absolute_and_calibration_limits(self) -> None:
        metrics = make_metrics(CALIBRATION_SELECTION)
        measurements = policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)
        thresholds = policy.derive_thresholds(measurements)
        self.assertAlmostEqual(0.89, thresholds["localizationCoverageHmean"])
        self.assertAlmostEqual(0.65, thresholds["exactEndToEndRowHmean"])
        self.assertAlmostEqual(0.88, thresholds["exactTokenF1"])
        self.assertAlmostEqual(0.10, thresholds["characterErrorRate"])
        self.assertAlmostEqual(0.18, thresholds["wordErrorRate"])
        self.assertAlmostEqual(0.02, thresholds["fractionTokenF1BelowHalf"])

    def test_calibration_cannot_issue_policy_below_an_absolute_floor(self) -> None:
        metrics = make_metrics(CALIBRATION_SELECTION)
        lower_token_precision = 84 / 100
        lower_token_f1 = (
            2
            * lower_token_precision
            * lower_token_precision
            / (lower_token_precision + lower_token_precision)
        )
        for item in metrics["perDocument"]:
            item["counts"]["matchingTokensOrderInvariant"] = 84
            item["counts"]["matchingWords"] = 84
            item["counts"]["wordEdits"] = 16
            item["tokenF1"] = lower_token_f1
            item["pageWer"] = 0.16
        metrics["macro"]["tokenF1"] = lower_token_f1
        metrics["macro"]["pageWer"] = 0.16
        metrics["micro"]["tokenF1"] = lower_token_f1
        metrics["micro"]["pageWer"] = 0.16
        refresh_micro_counts(metrics)
        measurements = policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)
        with self.assertRaisesRegex(policy.PolicyError, "exactTokenF1"):
            policy.derive_thresholds(measurements)

    def test_threshold_equality_passes_and_epsilon_failure_is_detected(self) -> None:
        measurements = {spec.name: spec.absolute_boundary for spec in policy.METRIC_SPECS}
        thresholds = dict(measurements)
        self.assertTrue(policy.evaluate_thresholds(measurements, thresholds)["passed"])
        for spec in policy.METRIC_SPECS:
            failed = dict(measurements)
            failed[spec.name] += -1e-12 if spec.direction == "higher" else 1e-12
            result = policy.evaluate_thresholds(failed, thresholds)
            self.assertFalse(result["passed"], spec.name)
            self.assertFalse(result["checks"][spec.name]["passed"])

    def test_policy_validation_rejects_identity_report_and_threshold_tampering(self) -> None:
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        identity = make_identity()
        calibration_metrics = make_metrics(CALIBRATION_SELECTION)
        calibration_report = write_metrics_report(
            directory,
            "calibration.json",
            selection=CALIBRATION_SELECTION,
            identity=identity,
            metrics=calibration_metrics,
        )
        built = policy.build_policy(
            calibration_selection=CALIBRATION_SELECTION,
            calibration_report_path=calibration_report,
            frozen_at_utc="2026-08-05T00:00:00Z",
            identity=identity,
        )
        policy_path = write_policy_value(directory, "policy.json", built)
        validated = policy.validate_policy(
            policy_path,
            expected_calibration_selection=CALIBRATION_SELECTION,
            expected_calibration_report_path=calibration_report,
            expected_identity=identity,
        )
        self.assertEqual(policy.POLICY_ID, validated.value["policyId"])

        wrong_report = write_metrics_report(
            directory,
            "wrong-report.json",
            selection=CALIBRATION_SELECTION,
            identity=identity,
            metrics=calibration_metrics,
        )
        wrong_value = policy.load_policy(wrong_report)[0]
        wrong_value["evidence"]["changed"] = True
        wrong_report.write_bytes(policy.encoded_policy(wrong_value))
        with self.assertRaises(policy.PolicyError):
            policy.validate_policy(
                policy_path,
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_calibration_report_path=wrong_report,
                expected_identity=identity,
            )
        wrong_identity = copy.deepcopy(identity)
        wrong_identity["engine"]["workerSha256"] = "0" * 64
        with self.assertRaises(policy.PolicyError):
            policy.validate_policy(
                policy_path,
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_calibration_report_path=calibration_report,
                expected_identity=wrong_identity,
            )
        tampered = copy.deepcopy(built)
        tampered["thresholds"]["exactTokenF1"] -= 0.01
        with self.assertRaises(policy.PolicyError):
            policy.validate_policy(
                write_policy_value(directory, "threshold-tampered.json", tampered),
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_calibration_report_path=calibration_report,
                expected_identity=identity,
            )
        tampered = copy.deepcopy(built)
        tampered["calibration"]["measurements"]["exactTokenF1"] = 0.85
        tampered["thresholds"] = policy.derive_thresholds(tampered["calibration"]["measurements"])
        with self.assertRaisesRegex(policy.PolicyError, "do not match"):
            policy.validate_policy(
                write_policy_value(directory, "measurements-tampered.json", tampered),
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_calibration_report_path=calibration_report,
                expected_identity=identity,
            )

        tampered = copy.deepcopy(built)
        tampered["calibration"]["extra"] = True
        with self.assertRaisesRegex(policy.PolicyError, "policy calibration"):
            policy.validate_policy(
                write_policy_value(directory, "schema-tampered.json", tampered),
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_calibration_report_path=calibration_report,
                expected_identity=identity,
            )
        tampered = copy.deepcopy(built)
        tampered["frozenAtUtc"] = {"not": "a timestamp"}
        with self.assertRaisesRegex(policy.PolicyError, "timestamp"):
            policy.validate_policy(
                write_policy_value(directory, "timestamp-tampered.json", tampered),
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_calibration_report_path=calibration_report,
                expected_identity=identity,
            )
        for index, timestamp in enumerate(("2026-08-04T23:59:59Z", "2026-08-05T00:00:01Z")):
            tampered = copy.deepcopy(built)
            tampered["frozenAtUtc"] = timestamp
            with self.assertRaisesRegex(policy.PolicyError, "does not match"):
                policy.validate_policy(
                    write_policy_value(directory, f"timestamp-rebound-{index}.json", tampered),
                    expected_calibration_selection=CALIBRATION_SELECTION,
                    expected_calibration_report_path=calibration_report,
                    expected_identity=identity,
                )

        with self.assertRaisesRegex(policy.PolicyError, "does not match"):
            policy.build_policy(
                calibration_selection=CALIBRATION_SELECTION,
                calibration_report_path=calibration_report,
                frozen_at_utc="2026-08-05T00:00:01Z",
                identity=identity,
            )

    def test_macro_tail_counts_and_nonfinite_values_fail_closed(self) -> None:
        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["macro"]["tokenF1"] = 0.89
        with self.assertRaisesRegex(policy.PolicyError, "not derived"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][3]["pageCer"] = math.nan
        with self.assertRaises(policy.PolicyError):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"].pop()
        metrics["micro"]["documents"] -= 1
        with self.assertRaisesRegex(policy.PolicyError, "exactly 600"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        measurements = {spec.name: spec.absolute_boundary for spec in policy.METRIC_SPECS}
        measurements["exactTokenF1"] = 1.01
        with self.assertRaisesRegex(policy.PolicyError, "exceeds 1"):
            policy.derive_thresholds(measurements)

    def test_per_document_and_micro_acceptance_metrics_are_recomputed(self) -> None:
        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["tokenF1"] = 1.10
        metrics["macro"]["tokenF1"] = (
            sum(item["tokenF1"] for item in metrics["perDocument"]) / policy.CALIBRATION_DOCUMENTS
        )
        with self.assertRaisesRegex(policy.PolicyError, "not derived from its counts"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["micro"]["detection"]["hmean"] = 1.0
        metrics["micro"]["endToEndExact"]["hmean"] = 1.0
        metrics["micro"]["tokenF1"] = 1.0
        metrics["micro"]["pageCer"] = 0.0
        metrics["micro"]["pageWer"] = 0.0
        with self.assertRaisesRegex(policy.PolicyError, "micro"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["detection"]["matchedPredictions"] = 101
        with self.assertRaisesRegex(policy.PolicyError, "exceeds its denominator"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["detection"]["precision"] = True
        with self.assertRaisesRegex(policy.PolicyError, "not numeric"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["endToEndExact"] = make_coverage(93, 100)
        with self.assertRaisesRegex(policy.PolicyError, "subset of localization"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["endToEndExact"] = make_coverage(86, 100)
        with self.assertRaisesRegex(policy.PolicyError, "ordered matching words"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["detection"] = make_coverage(92, 101)
        metrics["perDocument"][0]["endToEndExact"] = make_coverage(70, 101)
        with self.assertRaisesRegex(policy.PolicyError, "smaller than its physical rows"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["counts"]["matchingCharacters"] = 85
        with self.assertRaisesRegex(policy.PolicyError, "edit distance"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["counts"]["matchingWords"] = 91
        with self.assertRaisesRegex(policy.PolicyError, "matching count"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["counts"]["matchingCharacters"] = 84
        with self.assertRaisesRegex(policy.PolicyError, "matching count"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["perDocument"][0]["counts"].update(
            {
                "characterEdits": 0,
                "matchingCharacters": 0,
                "predictedCharacters": 0,
                "referenceCharacters": 0,
            }
        )
        metrics["perDocument"][0]["pageCer"] = 0.0
        with self.assertRaisesRegex(policy.PolicyError, "sequence cardinality"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

    def test_confirmatory_evaluation_requires_all_200_documents(self) -> None:
        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        identity = make_identity()
        calibration_metrics = make_metrics(CALIBRATION_SELECTION)
        calibration_report = write_metrics_report(
            directory,
            "calibration.json",
            selection=CALIBRATION_SELECTION,
            identity=identity,
            metrics=calibration_metrics,
        )
        built = policy.build_policy(
            calibration_selection=CALIBRATION_SELECTION,
            calibration_report_path=calibration_report,
            frozen_at_utc="2026-08-05T00:00:00Z",
            identity=identity,
        )
        policy_path = write_policy_value(directory, "policy.json", built)
        validated = policy.validate_policy(
            policy_path,
            expected_calibration_selection=CALIBRATION_SELECTION,
            expected_calibration_report_path=calibration_report,
            expected_identity=identity,
        )
        confirmatory_metrics = make_metrics(CONFIRMATORY_SELECTION)
        confirmatory_report = write_metrics_report(
            directory,
            "confirmatory.json",
            selection=CONFIRMATORY_SELECTION,
            identity=identity,
            metrics=confirmatory_metrics,
        )
        result = policy.evaluate_confirmatory(
            policy_path,
            confirmatory_report,
            expected_calibration_report_path=calibration_report,
            expected_calibration_selection=CALIBRATION_SELECTION,
            expected_identity=identity,
            selection=CONFIRMATORY_SELECTION,
        )
        self.assertTrue(result["passed"])
        self.assertEqual(validated.file_sha256, result["policySha256"])
        duplicate_metrics = make_metrics(CONFIRMATORY_SELECTION)
        duplicate_metrics["perDocument"][1]["rowIndex"] = duplicate_metrics["perDocument"][0][
            "rowIndex"
        ]
        duplicate_metrics["perDocument"][1]["imageSha256"] = duplicate_metrics["perDocument"][0][
            "imageSha256"
        ]
        duplicate_report = write_metrics_report(
            directory,
            "duplicate.json",
            selection=CONFIRMATORY_SELECTION,
            identity=identity,
            metrics=duplicate_metrics,
        )
        with self.assertRaisesRegex(policy.PolicyError, "duplicated"):
            policy.evaluate_confirmatory(
                policy_path,
                duplicate_report,
                expected_calibration_report_path=calibration_report,
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_identity=identity,
                selection=CONFIRMATORY_SELECTION,
            )
        short_metrics = make_metrics(CONFIRMATORY_SELECTION)
        short_metrics["perDocument"].pop()
        short_metrics["micro"]["documents"] -= 1
        short_report = write_metrics_report(
            directory,
            "short.json",
            selection=CONFIRMATORY_SELECTION,
            identity=identity,
            metrics=short_metrics,
        )
        with self.assertRaisesRegex(policy.PolicyError, "exactly 200"):
            policy.evaluate_confirmatory(
                policy_path,
                short_report,
                expected_calibration_report_path=calibration_report,
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_identity=identity,
                selection=CONFIRMATORY_SELECTION,
            )

        weak = copy.deepcopy(built)
        weak["thresholds"]["exactTokenF1"] = 0.0
        weak_path = write_policy_value(directory, "weak.json", weak)
        with self.assertRaises(policy.PolicyError):
            policy.evaluate_confirmatory(
                weak_path,
                confirmatory_report,
                expected_calibration_report_path=calibration_report,
                expected_calibration_selection=CALIBRATION_SELECTION,
                expected_identity=identity,
                selection=CONFIRMATORY_SELECTION,
            )

    def test_role_selection_and_strict_scalar_types_fail_closed(self) -> None:
        manifest = Path(__file__).resolve().parents[1] / "cord-v2-train-selection-v1.json"
        loaded = policy.load_role_selections(manifest)
        self.assertEqual(policy.CALIBRATION_DOCUMENTS, len(loaded["calibration"].entries))
        self.assertEqual(policy.CONFIRMATORY_DOCUMENTS, len(loaded["confirmatory"].entries))

        fabricated_entries = list(CALIBRATION_SELECTION.entries)
        fabricated_entries[0] = policy.SelectionEntry(
            global_index=fabricated_entries[0].global_index,
            image_sha256="0" * 64,
            role="calibration",
            row_index=fabricated_entries[0].row_index,
            shard_ordinal=fabricated_entries[0].shard_ordinal,
        )
        fabricated = policy.ValidatedRoleSelection(
            entries=tuple(fabricated_entries),
            entries_sha256=CALIBRATION_SELECTION.entries_sha256,
            manifest_sha256=CALIBRATION_SELECTION.manifest_sha256,
            role="calibration",
        )
        with self.assertRaisesRegex(policy.PolicyError, "identity changed"):
            policy.validate_role_selection(fabricated, expected_role="calibration")

        metrics = make_metrics(CALIBRATION_SELECTION)
        metrics["micro"]["documents"] = float(policy.CALIBRATION_DOCUMENTS)
        with self.assertRaisesRegex(policy.PolicyError, "document count"):
            policy.extract_measurements(metrics, selection=CALIBRATION_SELECTION)

        identity = make_identity()
        identity["benchmark"]["schemaVersion"] = True
        with self.assertRaisesRegex(policy.PolicyError, "schema version"):
            policy.validate_identity(identity)

        identity = make_identity()
        identity["benchmark"]["schemaVersion"] = 999
        identity["benchmark"]["protocol"] = "wrong-protocol"
        with self.assertRaisesRegex(policy.PolicyError, "schema version"):
            policy.validate_identity(identity)

        directory = Path(self.enterContext(tempfile.TemporaryDirectory()))
        identity = make_identity()
        report_path = write_metrics_report(
            directory,
            "unbound.json",
            selection=CALIBRATION_SELECTION,
            identity=identity,
            metrics=make_metrics(CALIBRATION_SELECTION),
        )
        report_value = policy.load_policy(report_path)[0]
        report_value["metricsSha256"] = "0" * 64
        report_path.write_bytes(policy.encoded_policy(report_value))
        with self.assertRaisesRegex(policy.PolicyError, "not bound"):
            policy.load_metrics_report(
                report_path,
                selection=CALIBRATION_SELECTION,
                expected_identity=identity,
            )

    def test_policy_file_is_canonical_exclusive_and_byte_hashed(self) -> None:
        value = {"a": 1, "b": {"c": 2}}
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "policy.json"
            published = policy.publish_policy(output, value)
            self.assertEqual(len(output.read_bytes()), published["bytes"])
            self.assertFalse(output.with_name(output.name + ".incomplete").exists())
            loaded, digest = policy.load_policy(output)
            self.assertEqual(value, loaded)
            self.assertEqual(published["sha256"], digest)
            with self.assertRaises(policy.PolicyError):
                policy.publish_policy(output, value)

            output.write_text('{"a":1, "b":{"c":2}}\n', encoding="utf-8")
            with self.assertRaisesRegex(policy.PolicyError, "canonical"):
                policy.load_policy(output)
            output.write_text('{"a":1,"a":2}\n', encoding="utf-8")
            with self.assertRaisesRegex(policy.PolicyError, "Duplicate"):
                policy.load_policy(output)

            nonfinite = Path(directory) / "nonfinite.json"
            with self.assertRaisesRegex(policy.PolicyError, "canonical JSON"):
                policy.publish_policy(nonfinite, {"a": math.nan})
            self.assertFalse(nonfinite.exists())
            self.assertFalse(nonfinite.with_name(nonfinite.name + ".incomplete").exists())
            nonfinite.write_bytes(b'{"a":NaN}\n')
            with self.assertRaisesRegex(policy.PolicyError, "Non-finite"):
                policy.load_policy(nonfinite)

            blocked = Path(directory) / "blocked.json"
            blocked.with_name(blocked.name + ".incomplete").write_bytes(b"partial")
            with self.assertRaises(policy.PolicyError):
                policy.publish_policy(blocked, value)


if __name__ == "__main__":
    unittest.main()
