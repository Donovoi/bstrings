from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import subprocess
import sys
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import benchmark_ocr_sroie_posthoc as posthoc  # noqa: E402
from test_benchmark_ocr_sroie_acceptance import (  # noqa: E402
    _confirmatory_metrics,
    confirmatory_fixture,
)


class SroiePosthocTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    @staticmethod
    def _overlap_records() -> list[dict]:
        image_sha256s = (
            "489f7ad676d5731c6fd56242e56cf3bbcaae9a63656dd8247c16c521754b8f6a",
            "1f73b51c8f4827626dd93d02ebfa924c2cbeb6f346039afc09a544a0e349cf2b",
            "f6335f9be62df520e485548ceefb21605bac5a9b08ec3766148c61f570a02b1e",
            "b20922fadce7966f9b417141fab2a1d4135ec12c5c3e11ee46f7da1029d372c4",
            "1a0bfb1ade2aa37a33ccba07b0785371d5521edcda5d521ffa2c3d49975f4a19",
            "3c867120ac3499b731fb2ae4b6b9b56eb88d7df7753ef7e08bd2739f1ea4b8cf",
            "2b97328f51c6b2772e77c08fe363357373a250f52287b057bf4422b16f01ae68",
            "1526ccb2d981c1a20b3e64dc1b831cf34d12f2ff0d25faa10191e180da750879",
        )
        return [
            {
                "imageSha256": image_sha256,
                "testRowIndex": test_row,
                "trainRowIndex": train_row,
            }
            for image_sha256, (test_row, train_row) in zip(
                image_sha256s,
                posthoc.EXPECTED_CALIBRATION_OVERLAP_RECORDS,
                strict=True,
            )
        ]

    @staticmethod
    def _clean_identities() -> list[dict]:
        excluded = {test_row for test_row, _ in posthoc.EXPECTED_CALIBRATION_OVERLAP_RECORDS}
        return [
            {
                "imageSha256": hashlib.sha256(f"held-out-{index}".encode()).hexdigest(),
                "rowIndex": index,
            }
            for index in range(posthoc.acceptance.sroie_policy.RAW_TEST_ROWS)
            if index not in excluded
        ]

    def _ledger(self) -> tuple[Path, dict, dict]:
        state_root = self.root / "machine-state"
        ledger = (
            state_root
            / "bstrings"
            / "acceptance-ledgers"
            / "icdar2019-sroie-bffe40c26759-test-one-shot-v1.json"
        )
        ledger.parent.mkdir(parents=True)
        started = "2026-08-05T14:03:44Z"
        failed = "2026-08-05T14:03:53Z"
        quarantined = "2026-08-05T14:03:54Z"
        initial = {
            "calibrationReportSha256": "1" * 64,
            "candidateIdentitySha256": "2" * 64,
            "datasetId": posthoc.acceptance.sroie_policy.DATASET_ID,
            "datasetRevision": posthoc.acceptance.sroie_policy.DATASET_REVISION,
            "expectedTestBytes": posthoc.acceptance.sroie_policy.TEST_BYTES,
            "expectedTestSha256": posthoc.acceptance.sroie_policy.TEST_SHA256,
            "phase": "confirmatory",
            "policySha256": "3" * 64,
            "protocol": "bstrings-ICDAR2019-SROIE-train-test-ocr-v2",
            "reportOutputPathSha256": "4" * 64,
            "schemaVersion": 1,
            "startedAtUtc": started,
            "status": "started",
            "witnessGhExecutableSha256": "5" * 64,
            "witnessGhVersion": posthoc.acceptance.PINNED_GH_VERSION,
            "witnessReleaseVerificationSha256": "6" * 64,
            "witnessSha256": "7" * 64,
        }
        claim_sha256 = hashlib.sha256(posthoc.acceptance._canonical_bytes(initial)).hexdigest()
        marker = {
            "claimSha256": claim_sha256,
            "quarantinedAtUtc": quarantined,
            "schemaVersion": 1,
            "status": "quarantined",
        }
        marker_path = ledger.with_name(ledger.name + ".quarantined.json")
        marker_path.write_bytes(posthoc.acceptance._canonical_bytes(marker))
        final = {
            **initial,
            "acceptancePassed": False,
            "claimSha256": claim_sha256,
            "failedAtUtc": failed,
            "failure": {
                "backend": None,
                "stage": "annotation-parse",
                "type": "AcceptanceError",
            },
            "finalDisposition": "failed",
            "integrityPassed": False,
            "quarantinedAtUtc": quarantined,
            "quarantineMarkerSha256": posthoc.acceptance.benchmark_core.sha256_file(marker_path),
            "runSucceeded": False,
            "status": "quarantined",
        }
        ledger.write_bytes(posthoc.acceptance._canonical_bytes(final))
        return ledger, final, marker

    def _report(self) -> tuple[dict, posthoc.PosthocSealContext]:
        acceptance_report, acceptance_context = confirmatory_fixture(accepted=True)
        identity = {
            "candidate": "synthetic",
            "runtimeProfiles": {
                "cpu": {"executableSha256": "a" * 64},
                "directml": {"executableSha256": "b" * 64},
            },
            "worker": {"sha256": "1" * 64},
        }
        clean_identities = self._clean_identities()
        metrics = _confirmatory_metrics(clean_identities)
        metrics_sha256 = posthoc.acceptance.sroie_policy.sha256_canonical(metrics)
        determinism_identities = clean_identities[: posthoc.acceptance.DETERMINISM_DOCUMENTS]
        determinism_metrics = _confirmatory_metrics(determinism_identities)
        artifact_hashes = {
            "posthocBboxRepairAudit": posthoc.EXPECTED_REPAIR_IDENTITY["bboxRepairAuditSha256"],
            "posthocCorpusManifest": "7" * 64,
            "posthocDeterminismCorpusManifest": "2" * 64,
            "posthocDeterminismInventory": "3" * 64,
            "posthocDeterminismWorkerManifest": "4" * 64,
            "posthocDuplicateAudit": "8" * 64,
            "posthocEvaluationCorpusManifest": "5" * 64,
            "posthocEvaluationInventory": "6" * 64,
            "posthocEvaluationWorkerManifest": "b" * 64,
            "posthocInventory": "9" * 64,
            "posthocTestSnapshot": posthoc.acceptance.sroie_policy.TEST_SHA256,
            "posthocWorkerManifest": "a" * 64,
        }
        artifacts = {
            name: {
                "bytes": (
                    posthoc.acceptance.sroie_policy.TEST_BYTES
                    if name == "posthocTestSnapshot"
                    else 1
                ),
                "path": f"synthetic/{name}.json",
                "sha256": digest,
            }
            for name, digest in artifact_hashes.items()
        }
        strict_per_document_sha256 = metrics["caseSensitiveDiagnostics"]["perDocumentMetricsSha256"]
        primary_per_document_sha256 = hashlib.sha256(
            posthoc.acceptance._per_document_bytes(metrics)
        ).hexdigest()
        determinism_strict_per_document_sha256 = determinism_metrics["caseSensitiveDiagnostics"][
            "perDocumentMetricsSha256"
        ]
        determinism_primary_per_document_sha256 = hashlib.sha256(
            posthoc.acceptance._per_document_bytes(determinism_metrics)
        ).hexdigest()

        def make_run(
            provider: str,
            resolved: str,
            run_metrics: dict,
            *,
            records: int,
        ) -> dict:
            runtime_sha256 = "a" * 64 if provider == "cpu" else "b" * 64
            if provider == "cpu":
                execution_counts = {"cpu": records}
            elif provider == "directml":
                execution_counts = {"directml": records}
            else:
                cpu_records = max(1, records // 20)
                execution_counts = {
                    "cpu": cpu_records,
                    "directml": records - cpu_records,
                }
            cpu_records = execution_counts.get("cpu", 0)
            non_cpu_records = sum(
                count for name, count in execution_counts.items() if name != "cpu"
            )
            both_lanes = cpu_records > 0 and non_cpu_records > 0
            elapsed_seconds = float(len(run_metrics["perDocument"]))
            return {
                "canonicalEvidenceSha256": "4" * 64,
                "criticalEvidenceSha256": "5" * 64,
                "documentsPerSecond": 1.0,
                "elapsedSeconds": elapsed_seconds,
                "executionProviderRecordCounts": execution_counts,
                "hybridLaneRecordCoverage": {
                    "bothLanesProducedRecords": both_lanes,
                    "cpuLaneRecords": cpu_records,
                    "nonCpuLaneRecords": non_cpu_records,
                },
                "metrics": copy.deepcopy(run_metrics),
                "metricsSha256": posthoc.acceptance.sroie_policy.sha256_canonical(run_metrics),
                "perDocumentMetricsSha256": hashlib.sha256(
                    posthoc.acceptance._per_document_bytes(run_metrics)
                ).hexdigest(),
                "provenanceErrors": [],
                "provenancePassed": True,
                "qualityGatePassed": None,
                "rawOutputHashes": {
                    "assessmentsSha256": "c" * 64,
                    "pairSha256": "d" * 64,
                    "stringsSha256": "e" * 64,
                },
                "requestedProvider": provider,
                "requestedThreads": 0,
                "resolvedProvider": resolved,
                "resolvedThreadCounts": {resolved: 1},
                "resolvedWorkerCounts": {resolved: 1},
                "runtimeSha256": runtime_sha256,
                "stringRecords": records,
                "textNormalization": (posthoc.acceptance.sroie.SROIE_PRIMARY_TEXT_NORMALIZATION),
                "throughputComparable": provider != "hybrid" or both_lanes,
                "workerSha256": "1" * 64,
            }

        backends = []
        for provider, resolved in (
            ("cpu", "cpu"),
            ("directml", "directml"),
            ("hybrid", "hybrid-directml-cpu"),
        ):
            quality_run = make_run(provider, resolved, metrics, records=100)
            determinism_runs = [
                make_run(provider, resolved, determinism_metrics, records=10)
                for _ in range(posthoc.acceptance.DETERMINISM_REPETITIONS)
            ]
            runtime_sha256 = quality_run["runtimeSha256"]
            backends.append(
                {
                    "byteDeterminismEvaluated": True,
                    "byteDeterministic": True,
                    "canonicalEvidenceDeterministic": True,
                    "canonicalEvidenceSha256": quality_run["canonicalEvidenceSha256"],
                    "criticalEvidenceDeterministic": True,
                    "criticalEvidenceSha256": quality_run["criticalEvidenceSha256"],
                    "determinism": {
                        "byteDeterministic": True,
                        "canonicalEvidenceDeterministic": True,
                        "criticalEvidenceDeterministic": True,
                        "metricsDeterministic": True,
                        "rowIndices": [item["rowIndex"] for item in determinism_identities],
                        "runs": determinism_runs,
                        "selectionSha256": posthoc.acceptance.sroie_policy.sha256_canonical(
                            determinism_identities
                        ),
                    },
                    "determinismRepetitions": (posthoc.acceptance.DETERMINISM_REPETITIONS),
                    "determinismRows": posthoc.acceptance.DETERMINISM_DOCUMENTS,
                    "executionProviderCountsStable": True,
                    "executionProviderRecordCounts": quality_run["executionProviderRecordCounts"],
                    "hybridLaneRecordCoverage": quality_run["hybridLaneRecordCoverage"],
                    "metrics": copy.deepcopy(metrics),
                    "metricsDeterministic": True,
                    "metricsSha256": metrics_sha256,
                    "provenancePassed": True,
                    "qualityDocumentsPerSecond": quality_run["documentsPerSecond"],
                    "qualityElapsedSeconds": quality_run["elapsedSeconds"],
                    "qualityGatePassed": None,
                    "qualityRows": posthoc.EXPECTED_UNCONTAMINATED_TEST_DOCUMENTS,
                    "qualityRun": quality_run,
                    "requestedProvider": provider,
                    "requestedThreads": 0,
                    "resolvedProvider": resolved,
                    "resolvedThreadCounts": quality_run["resolvedThreadCounts"],
                    "resolvedWorkerCounts": quality_run["resolvedWorkerCounts"],
                    "runtime": {
                        "executableSha256": runtime_sha256,
                        "inventorySha256": "6" * 64,
                        "loadPathsSha256": "7" * 64,
                        "requestedProvider": ("cpu" if provider == "cpu" else "directml"),
                        "runtimeRootsSha256": "8" * 64,
                        "schemaVersion": 1,
                    },
                    "runtimeSha256": runtime_sha256,
                    "stableResolvedProvider": True,
                    "stableResolvedThreadCounts": True,
                    "stableResolvedWorkerCounts": True,
                    "stableRuntime": True,
                    "stableTextNormalization": True,
                    "textNormalization": (
                        posthoc.acceptance.sroie.SROIE_PRIMARY_TEXT_NORMALIZATION
                    ),
                    "throughputComparable": quality_run["throughputComparable"],
                    "workerSha256": "1" * 64,
                }
            )
        backend_result_artifacts = {}
        for name in posthoc._posthoc_backend_result_paths(Path("posthoc-evidence-root")):
            is_determinism = "/determinism-" in name
            if name.endswith("/assessments.jsonl"):
                digest = "c" * 64
            elif name.endswith("/strings.jsonl"):
                digest = "e" * 64
            elif name.endswith("/metrics-per-document-case-sensitive.jsonl"):
                digest = (
                    determinism_strict_per_document_sha256
                    if is_determinism
                    else strict_per_document_sha256
                )
            else:
                digest = (
                    determinism_primary_per_document_sha256
                    if is_determinism
                    else primary_per_document_sha256
                )
            backend_result_artifacts[name] = {
                "bytes": 1,
                "path": name,
                "sha256": digest,
            }
        overlap_records = self._overlap_records()
        overlap_by_test_row = {
            record["testRowIndex"]: record["imageSha256"] for record in overlap_records
        }
        full_identities = [
            {
                "imageSha256": overlap_by_test_row.get(
                    index,
                    hashlib.sha256(f"held-out-{index}".encode()).hexdigest(),
                ),
                "rowIndex": index,
            }
            for index in range(posthoc.acceptance.sroie_policy.RAW_TEST_ROWS)
        ]
        corpus_identity = {
            **posthoc.EXPECTED_REPAIR_IDENTITY,
            "corpusManifestSha256": artifact_hashes["posthocCorpusManifest"],
            "duplicateAuditSha256": artifact_hashes["posthocDuplicateAudit"],
            "excludedRows": 0,
            "imageIdentitiesSha256": posthoc.acceptance.sroie_policy.sha256_canonical(
                full_identities
            ),
            "parquetSha256": artifact_hashes["posthocTestSnapshot"],
            "selectedDocuments": posthoc.acceptance.sroie_policy.RAW_TEST_ROWS,
            "sourceImageDigestsSha256": (
                posthoc.acceptance.sroie_policy.sha256_canonical(full_identities)
            ),
            "sourceRows": posthoc.acceptance.sroie_policy.RAW_TEST_ROWS,
            "workerManifestSha256": artifact_hashes["posthocWorkerManifest"],
        }
        repaired_holdout = {
            **corpus_identity,
            "expectedRepairIdentitySha256": posthoc.EXPECTED_REPAIR_IDENTITY_SHA256,
            "repairAppliedOnlyToDerivedScoringGeometry": True,
            "repairPolicy": posthoc.acceptance.sroie.SROIE_BBOX_REPAIR_POLICY,
            "repairIdentity": copy.deepcopy(posthoc.EXPECTED_REPAIR_IDENTITY),
            "repairIdentitySha256": posthoc.EXPECTED_REPAIR_IDENTITY_SHA256,
            "sourceArtifactReadOnlyAndReverified": True,
        }
        selected_rows = [item["rowIndex"] for item in clean_identities]
        clean_identity_sha256 = posthoc.acceptance.sroie_policy.sha256_canonical(clean_identities)
        determinism_identity_sha256 = posthoc.acceptance.sroie_policy.sha256_canonical(
            determinism_identities
        )
        evaluation_corpus = {
            "crossSplitOverlap": {
                "algorithm": posthoc.OVERLAP_ALGORITHM,
                "calibrationRawImageDigestsSha256": "d" * 64,
                "calibrationRawImages": posthoc.acceptance.sroie_policy.RAW_TRAIN_ROWS,
                "calibrationSelectedDocuments": (posthoc.EXPECTED_CALIBRATION_SELECTED_DOCUMENTS),
                "calibrationSelectedImageIdentitiesSha256": "e" * 64,
                "overlapDocuments": len(overlap_records),
                "overlapIdentitySha256": (posthoc.EXPECTED_CALIBRATION_OVERLAP_IDENTITY_SHA256),
                "overlapRecords": overlap_records,
                "perceptualSimilarityClaimed": False,
                "testRawImageDigestsSha256": corpus_identity["sourceImageDigestsSha256"],
                "testRawImages": posthoc.acceptance.sroie_policy.RAW_TEST_ROWS,
            },
            "determinismView": {
                "corpusManifestSha256": artifact_hashes["posthocDeterminismCorpusManifest"],
                "documents": posthoc.acceptance.DETERMINISM_DOCUMENTS,
                "imageIdentitiesSha256": determinism_identity_sha256,
                "inventorySha256": artifact_hashes["posthocDeterminismInventory"],
                "rowIndices": selected_rows[: posthoc.acceptance.DETERMINISM_DOCUMENTS],
                "selectionSha256": determinism_identity_sha256,
                "workerManifestSha256": artifact_hashes["posthocDeterminismWorkerManifest"],
            },
            "scoringView": {
                "corpusManifestSha256": artifact_hashes["posthocEvaluationCorpusManifest"],
                "documents": posthoc.EXPECTED_UNCONTAMINATED_TEST_DOCUMENTS,
                "excludedDocuments": len(overlap_records),
                "excludedRowIndices": [record["testRowIndex"] for record in overlap_records],
                "fullTestDocuments": posthoc.acceptance.sroie_policy.RAW_TEST_ROWS,
                "imageIdentitiesSha256": clean_identity_sha256,
                "inventorySha256": artifact_hashes["posthocEvaluationInventory"],
                "overlapExclusionPolicy": posthoc.OVERLAP_EXCLUSION_POLICY,
                "parentCorpusIdentitySha256": (
                    posthoc.acceptance.sroie_policy.sha256_canonical(corpus_identity)
                ),
                "repairedRowIndices": list(posthoc.EXPECTED_REPAIRED_TEST_ROW_INDICES),
                "repairedRowsIncluded": True,
                "rowIndices": selected_rows,
                "selectionSha256": clean_identity_sha256,
                "workerManifestSha256": artifact_hashes["posthocEvaluationWorkerManifest"],
            },
        }
        policy_evaluation = posthoc.acceptance.sroie_policy.evaluate_confirmatory(
            acceptance_context.validated_policy,
            metrics,
            expected_identities=clean_identities,
        )
        report = {
            "acceptancePassed": None,
            "candidateCommit": "a" * 40,
            "diagnosticThresholdComparisonPassed": True,
            "documents": posthoc.EXPECTED_UNCONTAMINATED_TEST_DOCUMENTS,
            "evaluationCompleted": True,
            "evaluationRole": "post-hoc-diagnostic",
            "evidence": {
                "artifactPublication": {
                    "artifactVisibility": "private-local-not-for-release",
                    "publicReleaseContents": "path-free-report-only",
                },
                "artifacts": artifacts,
                "backendResultArtifacts": backend_result_artifacts,
                "backends": backends,
                "candidate": {
                    "commit": "a" * 40,
                    "gitExecutableSha256": posthoc.PINNED_GIT_EXE_SHA256,
                    "repositoryClean": True,
                    "runnerSha256": "1" * 64,
                    "candidateSourceSetSha256": "2" * 64,
                },
                "consumedAttempt": {
                    "claimSha256": "c" * 64,
                    "failure": {
                        "backend": None,
                        "stage": "annotation-parse",
                        "type": "AcceptanceError",
                    },
                    "finalDisposition": "failed",
                    "ledgerSha256": posthoc.ORIGINAL_LEDGER_SHA256,
                    "originalCandidateCommit": posthoc.ORIGINAL_CANDIDATE_COMMIT,
                    "originalTerminalReleaseTag": posthoc.ORIGINAL_TERMINAL_RELEASE_TAG,
                    "originalTerminalResultAsset": posthoc.ORIGINAL_TERMINAL_RESULT_ASSET,
                    "originalTerminalResultSha256": (posthoc.ORIGINAL_TERMINAL_RESULT_SHA256),
                    "quarantineMarkerSha256": (posthoc.ORIGINAL_QUARANTINE_MARKER_SHA256),
                    "status": "quarantined",
                },
                "crossBackendIntegrity": {
                    "checks": {
                        key: value
                        for key, value in acceptance_report["evidence"]["crossBackendIntegrity"][
                            "checks"
                        ].items()
                        if key != "allRawSourceImageSha256SetsDisjoint"
                    },
                    "confidenceParity": copy.deepcopy(
                        acceptance_report["evidence"]["crossBackendIntegrity"]["confidenceParity"]
                    ),
                },
                "determinism": copy.deepcopy(acceptance_report["evidence"]["determinism"]),
                "diagnosticPolicyComparison": {
                    "allBackendThresholdsPassed": True,
                    "evaluations": {
                        provider: copy.deepcopy(policy_evaluation)
                        for provider in ("cpu", "directml", "hybrid")
                    },
                    "isAcceptanceDecision": False,
                    "reportableResultPassed": True,
                },
                "evaluationCorpus": evaluation_corpus,
                "execution": {"backendOrder": ["cpu", "directml", "hybrid"], "threads": 0},
                "generatedAtUtc": "2026-08-05T00:00:00Z",
                "holdoutStatus": {
                    "crossSplitExactOverlapsDetected": True,
                    "crossSplitOverlapDocuments": len(overlap_records),
                    "crossSplitOverlapsExcludedFromScoring": True,
                    "independentHoldout": False,
                    "oneShotAttemptConsumed": True,
                    "posthocOnly": True,
                    "withinTestDuplicateExclusions": 0,
                },
                "repairedHoldout": repaired_holdout,
                "v3Calibration": {
                    "candidateIdentitySha256": (
                        posthoc.acceptance.sroie_policy.sha256_canonical(identity)
                    ),
                    "policyId": posthoc.acceptance.sroie_policy.POLICY_ID,
                    "policySha256": acceptance_context.validated_policy.file_sha256,
                    "reportSha256": "d" * 64,
                },
            },
            "expectedIdentitiesSha256": posthoc.acceptance.sroie_policy.sha256_canonical(
                clean_identities
            ),
            "finalDisposition": "diagnostic-complete",
            "identity": identity,
            "identitySha256": posthoc.acceptance.sroie_policy.sha256_canonical(identity),
            "independentHoldout": False,
            "integrityPassed": True,
            "metrics": metrics,
            "metricsSha256": posthoc.acceptance.sroie_policy.sha256_canonical(metrics),
            "protocol": posthoc.POSTHOC_PROTOCOL,
            "rawRows": posthoc.acceptance.sroie_policy.RAW_TEST_ROWS,
            "repairPromptedByHeldoutFailure": True,
            "runSucceeded": True,
            "schemaVersion": posthoc.POSTHOC_SCHEMA_VERSION,
            "scoringProtocol": posthoc.acceptance.PROTOCOL,
        }
        pre_run_projection = {
            "artifactPublication": report["evidence"]["artifactPublication"],
            "artifacts": report["evidence"]["artifacts"],
            "candidate": report["evidence"]["candidate"],
            "candidateCommit": report["candidateCommit"],
            "consumedAttempt": report["evidence"]["consumedAttempt"],
            "evaluationCorpus": report["evidence"]["evaluationCorpus"],
            "expectedIdentitiesSha256": report["expectedIdentitiesSha256"],
            "identity": report["identity"],
            "identitySha256": report["identitySha256"],
            "repairedHoldout": report["evidence"]["repairedHoldout"],
            "v3Calibration": report["evidence"]["v3Calibration"],
        }
        raw_output_pair_bindings = {
            f"results/{provider}/{root}": "d" * 64
            for provider in ("cpu", "directml", "hybrid")
            for root in (
                "quality-all-100",
                "determinism-rows-0000-0009/run-01",
                "determinism-rows-0000-0009/run-02",
            )
        }
        context = posthoc.PosthocSealContext(
            validated_policy=acceptance_context.validated_policy,
            validated_policy_value_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                acceptance_context.validated_policy.value
            ),
            expected_identities_json=posthoc.acceptance.sroie_policy.canonical_json(
                [dict(item) for item in clean_identities]
            ),
            pre_run_binding_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                pre_run_projection
            ),
            expected_cpu_runtime_sha256="a" * 64,
            expected_directml_runtime_sha256="b" * 64,
            expected_worker_sha256="1" * 64,
            observed_integrity_passed=True,
            observed_threshold_comparison_passed=True,
            backends_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                report["evidence"]["backends"]
            ),
            backend_result_artifacts_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                report["evidence"]["backendResultArtifacts"]
            ),
            cross_backend_integrity_sha256=(
                posthoc.acceptance.sroie_policy.sha256_canonical(
                    report["evidence"]["crossBackendIntegrity"]
                )
            ),
            determinism_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                report["evidence"]["determinism"]
            ),
            evaluations_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                report["evidence"]["diagnosticPolicyComparison"]["evaluations"]
            ),
            metrics_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(metrics),
            raw_output_pair_bindings_json=(
                posthoc.acceptance.sroie_policy.canonical_json(raw_output_pair_bindings)
            ),
            observed_backends_json=posthoc.acceptance.sroie_policy.canonical_json(
                report["evidence"]["backends"]
            ),
            observed_backend_result_artifacts_json=(
                posthoc.acceptance.sroie_policy.canonical_json(
                    report["evidence"]["backendResultArtifacts"]
                )
            ),
            observed_confidence_parity_json=(
                posthoc.acceptance.sroie_policy.canonical_json(
                    report["evidence"]["crossBackendIntegrity"]["confidenceParity"]
                )
            ),
            report_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(report),
        )
        return report, context

    def _rebind_post_run(
        self,
        report: dict,
        context: posthoc.PosthocSealContext,
    ) -> posthoc.PosthocSealContext:
        evidence = report["evidence"]
        return replace(
            context,
            backends_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(evidence["backends"]),
            backend_result_artifacts_sha256=(
                posthoc.acceptance.sroie_policy.sha256_canonical(evidence["backendResultArtifacts"])
            ),
            cross_backend_integrity_sha256=(
                posthoc.acceptance.sroie_policy.sha256_canonical(evidence["crossBackendIntegrity"])
            ),
            determinism_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                evidence["determinism"]
            ),
            evaluations_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                evidence["diagnosticPolicyComparison"]["evaluations"]
            ),
            metrics_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(report["metrics"]),
            report_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(report),
        )

    def test_exact_acknowledgement_is_required(self) -> None:
        posthoc._require_acknowledgement(posthoc.ACKNOWLEDGEMENT)
        for value in (None, "", "yes", posthoc.ACKNOWLEDGEMENT.lower()):
            with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "acknowledgement"):
                posthoc._require_acknowledgement(value)

    def test_bootstrap_temporary_directory_ignores_inherited_temp_paths(self) -> None:
        trusted = self.root / "trusted-temp"
        inherited = self.root / "inherited-temp"
        trusted.mkdir()
        inherited.mkdir()
        with patch.dict(
            os.environ,
            {"TEMP": str(inherited), "TMP": str(inherited)},
            clear=False,
        ):
            owner, controlled = posthoc._bootstrap_temporary_directory(trusted)
        try:
            self.assertEqual(trusted.resolve(), controlled.parent)
            self.assertFalse(controlled.is_relative_to(inherited.resolve()))
        finally:
            owner.cleanup()

    def test_isolated_production_entrypoint_loads_only_frozen_siblings(self) -> None:
        completed = subprocess.run(
            [sys.executable, "-I", "-B", str(Path(posthoc.__file__).resolve()), "--help"],
            stdin=subprocess.DEVNULL,
            capture_output=True,
            check=False,
            timeout=30,
        )
        self.assertEqual(0, completed.returncode, completed.stderr.decode(errors="replace"))
        self.assertIn(b"--acknowledge-consumed-holdout", completed.stdout)

    def test_failed_quarantined_ledger_is_recomputed_and_bound(self) -> None:
        ledger, final, marker = self._ledger()
        ledger_digest = posthoc.acceptance.benchmark_core.sha256_file(ledger)
        marker_digest = final["quarantineMarkerSha256"]
        with (
            patch.object(posthoc.acceptance, "_MACHINE_STATE_ROOT", self.root / "machine-state"),
            patch.object(posthoc.acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", ledger),
            patch.object(posthoc, "ORIGINAL_LEDGER_SHA256", ledger_digest),
            patch.object(posthoc, "ORIGINAL_QUARANTINE_MARKER_SHA256", marker_digest),
        ):
            loaded, digest, loaded_marker, marker_digest = posthoc._load_quarantined_ledger()
        self.assertEqual(final, loaded)
        self.assertEqual(marker, loaded_marker)
        self.assertEqual(posthoc.acceptance.benchmark_core.sha256_file(ledger), digest)
        self.assertEqual(final["quarantineMarkerSha256"], marker_digest)

    def test_ledger_tampering_fails_closed(self) -> None:
        ledger, final, _ = self._ledger()
        machine_root = self.root / "machine-state"
        for mutate in (
            lambda value: value.update(status="failed"),
            lambda value: value.update(candidateIdentitySha256="f" * 64),
            lambda value: value["failure"].update(stage="scoring"),
            lambda value: value.update(acceptancePassed=True),
        ):
            changed = copy.deepcopy(final)
            mutate(changed)
            ledger.write_bytes(posthoc.acceptance._canonical_bytes(changed))
            with (
                patch.object(posthoc.acceptance, "_MACHINE_STATE_ROOT", machine_root),
                patch.object(posthoc.acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", ledger),
                patch.object(
                    posthoc,
                    "ORIGINAL_LEDGER_SHA256",
                    posthoc.acceptance.benchmark_core.sha256_file(ledger),
                ),
                patch.object(
                    posthoc,
                    "ORIGINAL_QUARANTINE_MARKER_SHA256",
                    changed["quarantineMarkerSha256"],
                ),
                self.assertRaises(posthoc.acceptance.AcceptanceError),
            ):
                posthoc._load_quarantined_ledger()

    def test_terminal_result_requires_exact_digest_and_privacy(self) -> None:
        terminal = self.root / "terminal.json"
        raw = posthoc.acceptance._canonical_bytes({"status": "failed"})
        terminal.write_bytes(raw)
        digest = hashlib.sha256(raw).hexdigest()
        with patch.object(posthoc, "ORIGINAL_TERMINAL_RESULT_SHA256", digest):
            value, loaded_digest = posthoc._load_terminal_result(terminal)
            self.assertEqual({"status": "failed"}, value)
            self.assertEqual(digest, loaded_digest)
        terminal.write_bytes(posthoc.acceptance._canonical_bytes({"status": "changed"}))
        with (
            patch.object(posthoc, "ORIGINAL_TERMINAL_RESULT_SHA256", digest),
            self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "digest"),
        ):
            posthoc._load_terminal_result(terminal)

        leaked = posthoc.acceptance._canonical_bytes({"recognizedText": "secret"})
        terminal.write_bytes(leaked)
        with (
            patch.object(
                posthoc,
                "ORIGINAL_TERMINAL_RESULT_SHA256",
                hashlib.sha256(leaked).hexdigest(),
            ),
            self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "forbidden"),
        ):
            posthoc._load_terminal_result(terminal)

    def test_candidate_commit_requires_exact_clean_head(self) -> None:
        git = self.root / "git.exe"
        git.write_bytes(b"synthetic git")
        commit = "a" * 40
        outputs = [
            (str(self.root) + "\n").encode(),
            (commit + "\n").encode(),
            b"",
            b"H tracked.txt\n",
        ]
        with (
            patch.object(posthoc, "_REPOSITORY_ROOT", self.root),
            patch.object(posthoc, "_run_git", side_effect=outputs),
            patch.object(
                posthoc,
                "PINNED_GIT_EXE_SHA256",
                posthoc.acceptance.benchmark_core.sha256_file(git),
            ),
            patch.object(posthoc, "_verify_candidate_sources", return_value="f" * 64),
        ):
            evidence = posthoc._verify_candidate_commit(git, commit)
        self.assertEqual(commit, evidence.commit)
        self.assertTrue(evidence.repositoryClean)

        dirty = [
            (str(self.root) + "\n").encode(),
            (commit + "\n").encode(),
            b"?? untracked\n",
        ]
        with (
            patch.object(posthoc, "_REPOSITORY_ROOT", self.root),
            patch.object(posthoc, "_run_git", side_effect=dirty),
            patch.object(
                posthoc,
                "PINNED_GIT_EXE_SHA256",
                posthoc.acceptance.benchmark_core.sha256_file(git),
            ),
            self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "not clean"),
        ):
            posthoc._verify_candidate_commit(git, commit)

    def test_git_runner_never_uses_a_shell_and_is_bounded(self) -> None:
        git = self.root / "git.exe"
        completed = subprocess.CompletedProcess([str(git)], 0, b"ok\n", b"")
        with patch.object(posthoc.subprocess, "run", return_value=completed) as invoked:
            self.assertEqual(b"ok\n", posthoc._run_git(git, ("status",)))
        keyword = invoked.call_args.kwargs
        self.assertNotIn("shell", keyword)
        self.assertTrue(keyword["capture_output"])
        self.assertIs(keyword["stdin"], subprocess.DEVNULL)
        self.assertEqual(posthoc.GIT_TIMEOUT_SECONDS, keyword["timeout"])

    def test_v3_calibration_gate_accepts_distinct_benchmark_and_adapter_protocols(
        self,
    ) -> None:
        identity = {"calibrationCorpus": {}}
        context = SimpleNamespace(
            expected_identities=tuple(),
            identity=identity,
            report={
                "acceptancePassed": True,
                "evaluationRole": "calibration",
                "integrityPassed": True,
                "protocol": posthoc.acceptance.PROTOCOL,
                "runSucceeded": True,
                "schemaVersion": posthoc.acceptance.SCHEMA_VERSION,
            },
            report_sha256="a" * 64,
            source_image_sha256s=tuple(),
        )
        policy = SimpleNamespace(
            file_sha256="b" * 64,
            value={
                "policyId": posthoc.acceptance.sroie_policy.POLICY_ID,
                "protocol": posthoc.acceptance.PROTOCOL,
            },
        )
        args = argparse.Namespace(
            calibration_evidence_root=self.root,
            calibration_report=self.root / "calibration-report.json",
            policy=self.root / "acceptance-policy.json",
        )
        with (
            patch.object(
                posthoc.acceptance,
                "load_calibration_context",
                return_value=context,
            ),
            patch.object(posthoc.acceptance, "build_identity", return_value=identity),
            patch.object(posthoc.acceptance, "_verify_persisted_runtime_context"),
            patch.object(
                posthoc.acceptance.sroie_policy,
                "validate_policy",
                return_value=policy,
            ),
            patch.object(posthoc.acceptance, "_recheck_policy"),
        ):
            loaded_context, loaded_policy, loaded_identity = posthoc._load_v3_calibration(
                args, SimpleNamespace()
            )
        self.assertIs(context, loaded_context)
        self.assertIs(policy, loaded_policy)
        self.assertEqual(identity, loaded_identity)
        self.assertNotEqual(
            posthoc.acceptance.PROTOCOL,
            posthoc.acceptance.sroie.SROIE_PROTOCOL,
        )

        with (
            patch.object(
                posthoc.acceptance,
                "load_calibration_context",
                return_value=context,
            ),
            patch.object(posthoc.acceptance, "build_identity", return_value=identity),
            patch.object(posthoc.acceptance, "_verify_persisted_runtime_context"),
            patch.object(
                posthoc.acceptance.sroie_policy,
                "validate_policy",
                return_value=policy,
            ),
            patch.object(posthoc.acceptance, "_recheck_policy"),
            patch.object(posthoc.acceptance.sroie, "SROIE_PROTOCOL", "retired-v2"),
            self.assertRaisesRegex(
                posthoc.acceptance.AcceptanceError,
                "accepted v3 calibration",
            ),
        ):
            posthoc._load_v3_calibration(args, SimpleNamespace())

    def test_cross_backend_integrity_requires_all_three_equal_paths(self) -> None:
        frozen = SimpleNamespace(
            worker_sha256="1" * 64,
            runtimes={
                "cpu": {"executableSha256": "2" * 64},
                "directml": {"executableSha256": "3" * 64},
            },
        )
        corpus = SimpleNamespace(documents=(object(), object()))
        runs = []
        for provider, resolved, runtime in (
            ("cpu", "cpu", "2" * 64),
            ("directml", "directml", "3" * 64),
            ("hybrid", "hybrid-directml-cpu", "3" * 64),
        ):
            runs.append(
                {
                    "_confidenceByCriticalRecord": {},
                    "criticalEvidenceSha256": "4" * 64,
                    "metricsSha256": "5" * 64,
                    "provenancePassed": True,
                    "qualityRows": 2,
                    "qualityRun": {"perDocumentMetricsSha256": "6" * 64},
                    "requestedProvider": provider,
                    "resolvedProvider": resolved,
                    "runtimeSha256": runtime,
                    "workerSha256": "1" * 64,
                }
            )
        with (
            patch.object(posthoc.acceptance, "_determinism_checks", return_value={"ok": True}),
            patch.object(posthoc.acceptance, "_candidate_stable", return_value=True),
            patch.object(
                posthoc.acceptance.cord,
                "confidence_parity",
                return_value={"passed": True},
            ),
            patch.object(posthoc.acceptance, "_meaningful_hybrid_lane_coverage", return_value=True),
        ):
            checks, _, deterministic = posthoc._posthoc_integrity(
                argparse.Namespace(), frozen, {}, corpus, object(), runs
            )
        self.assertTrue(all(checks.values()))
        self.assertEqual({"cpu", "directml", "hybrid"}, set(deterministic))

        changed = copy.deepcopy(runs)
        changed[2]["metricsSha256"] = "f" * 64
        with (
            patch.object(posthoc.acceptance, "_determinism_checks", return_value={"ok": True}),
            patch.object(posthoc.acceptance, "_candidate_stable", return_value=True),
            patch.object(
                posthoc.acceptance.cord,
                "confidence_parity",
                return_value={"passed": True},
            ),
            patch.object(posthoc.acceptance, "_meaningful_hybrid_lane_coverage", return_value=True),
        ):
            checks, _, _ = posthoc._posthoc_integrity(
                argparse.Namespace(), frozen, {}, corpus, object(), changed
            )
        self.assertFalse(checks["metricsEqual"])

    def test_exact_cross_split_overlaps_are_excluded_and_repair_row_is_retained(
        self,
    ) -> None:
        raw_calibration = [
            hashlib.sha256(f"train-{index}".encode()).hexdigest()
            for index in range(posthoc.acceptance.sroie_policy.RAW_TRAIN_ROWS)
        ]
        test_images = [
            hashlib.sha256(f"test-{index}".encode()).hexdigest()
            for index in range(posthoc.acceptance.sroie_policy.RAW_TEST_ROWS)
        ]
        for record in self._overlap_records():
            raw_calibration[record["trainRowIndex"]] = record["imageSha256"]
            test_images[record["testRowIndex"]] = record["imageSha256"]
        omitted_calibration_rows = set(range(600, 610))
        calibration_identities = [
            {"imageSha256": digest, "rowIndex": row_index}
            for row_index, digest in enumerate(raw_calibration)
            if row_index not in omitted_calibration_rows
        ]
        corpus = SimpleNamespace(
            documents=tuple(
                SimpleNamespace(row_index=row_index, sha256=digest)
                for row_index, digest in enumerate(test_images)
            ),
            source_image_sha256s=tuple(test_images),
        )

        records, included = posthoc._uncontaminated_posthoc_rows(
            raw_calibration,
            calibration_identities,
            corpus,
        )
        self.assertEqual(self._overlap_records(), records)
        self.assertEqual(posthoc.EXPECTED_UNCONTAMINATED_TEST_DOCUMENTS, len(included))
        self.assertIn(posthoc.EXPECTED_REPAIRED_TEST_ROW_INDICES[0], included)
        self.assertEqual(tuple(sorted(included)), included)

        changed_cases = {
            "missing selected overlap": (
                raw_calibration,
                [
                    item
                    for item in calibration_identities
                    if item["rowIndex"] != posthoc.EXPECTED_CALIBRATION_OVERLAP_RECORDS[0][1]
                ],
                corpus,
            ),
            "unsorted calibration": (
                raw_calibration,
                list(reversed(calibration_identities)),
                corpus,
            ),
            "seven overlaps": (
                raw_calibration,
                calibration_identities,
                SimpleNamespace(
                    documents=tuple(
                        SimpleNamespace(
                            row_index=row_index,
                            sha256=(
                                hashlib.sha256(b"changed-test-overlap").hexdigest()
                                if row_index == posthoc.EXPECTED_CALIBRATION_OVERLAP_RECORDS[0][0]
                                else digest
                            ),
                        )
                        for row_index, digest in enumerate(test_images)
                    ),
                    source_image_sha256s=tuple(
                        hashlib.sha256(b"changed-test-overlap").hexdigest()
                        if row_index == posthoc.EXPECTED_CALIBRATION_OVERLAP_RECORDS[0][0]
                        else digest
                        for row_index, digest in enumerate(test_images)
                    ),
                ),
            ),
        }
        for name, (raw, selected, changed_corpus) in changed_cases.items():
            with self.subTest(name=name), self.assertRaises(posthoc.acceptance.AcceptanceError):
                posthoc._uncontaminated_posthoc_rows(raw, selected, changed_corpus)

    def test_artifact_set_is_bound_and_mutation_fails_before_publication(self) -> None:
        evidence_root = self.root / "evidence"
        evidence_root.mkdir()
        paths = {
            name: evidence_root / f"{index}-{name}.bin"
            for index, name in enumerate(sorted(posthoc._POSTHOC_ARTIFACT_KEYS))
        }
        for index, path in enumerate(paths.values(), start=1):
            path.write_bytes(bytes([index]))
        snapshot = paths["posthocTestSnapshot"]
        corpus_identity = {
            "bboxRepairAuditSha256": posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocBboxRepairAudit"]
            ),
            "corpusManifestSha256": posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocCorpusManifest"]
            ),
            "duplicateAuditSha256": posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocDuplicateAudit"]
            ),
            "parquetSha256": posthoc.acceptance.benchmark_core.sha256_file(snapshot),
            "workerManifestSha256": posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocWorkerManifest"]
            ),
        }
        evaluation_corpus = SimpleNamespace(
            corpus_manifest_sha256=posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocEvaluationCorpusManifest"]
            ),
            worker_manifest_sha256=posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocEvaluationWorkerManifest"]
            ),
        )
        determinism_corpus = SimpleNamespace(
            corpus_manifest_sha256=posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocDeterminismCorpusManifest"]
            ),
            worker_manifest_sha256=posthoc.acceptance.benchmark_core.sha256_file(
                paths["posthocDeterminismWorkerManifest"]
            ),
        )
        with patch.object(posthoc.acceptance.sroie_policy, "TEST_BYTES", snapshot.stat().st_size):
            captured = posthoc._capture_posthoc_artifacts(
                paths,
                evidence_root=evidence_root,
                corpus_identity=corpus_identity,
                evaluation_corpus=evaluation_corpus,
                determinism_corpus=determinism_corpus,
            )
            for name in (
                "posthocCorpusManifest",
                "posthocEvaluationInventory",
                "posthocDeterminismCorpusManifest",
            ):
                with self.subTest(name=name):
                    original = paths[name].read_bytes()
                    paths[name].write_bytes(b"changed")
                    with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "artifact"):
                        posthoc._recheck_posthoc_artifacts(
                            paths,
                            evidence_root=evidence_root,
                            corpus_identity=corpus_identity,
                            evaluation_corpus=evaluation_corpus,
                            determinism_corpus=determinism_corpus,
                            expected=captured,
                        )
                    paths[name].write_bytes(original)

    def test_all_backend_outputs_are_hash_bound_before_publication(self) -> None:
        phase_root = self.root / "posthoc-diagnostic"
        paths = posthoc._posthoc_backend_result_paths(phase_root)
        self.assertEqual(36, len(paths))
        for name, path in paths.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode("utf-8"))
        captured = posthoc._capture_backend_result_artifacts(phase_root)
        self.assertEqual(set(paths), set(captured))
        runs = []
        for provider in ("cpu", "directml", "hybrid"):
            run_claims = []
            for root in (
                "quality-all-100",
                "determinism-rows-0000-0009/run-01",
                "determinism-rows-0000-0009/run-02",
            ):
                run_root = phase_root / "results" / provider / root
                pair_sha256 = posthoc.acceptance.benchmark_core.raw_output_pair_sha256(
                    run_root / "strings.jsonl",
                    run_root / "assessments.jsonl",
                )
                run_claims.append({"rawOutputHashes": {"pairSha256": pair_sha256}})
            runs.append(
                {
                    "qualityRun": run_claims[0],
                    "determinism": {"runs": run_claims[1:]},
                }
            )
        pair_bindings = posthoc._capture_raw_output_pair_bindings(runs, phase_root)
        self.assertEqual(9, len(pair_bindings))
        runs[0]["qualityRun"]["rawOutputHashes"]["pairSha256"] = "f" * 64
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "pair claim"):
            posthoc._capture_raw_output_pair_bindings(runs, phase_root)
        changed = next(iter(paths.values()))
        changed.write_bytes(b"changed")
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "backend result"):
            posthoc._recheck_backend_result_artifacts(phase_root, captured)

    def test_public_backend_run_is_normalized_before_in_memory_validation(self) -> None:
        public = posthoc._public_json_run(
            {
                "_private": "removed",
                "provenanceErrors": (),
                "nested": {"values": (1, 2)},
            }
        )
        self.assertNotIn("_private", public)
        self.assertEqual([], public["provenanceErrors"])
        self.assertEqual([1, 2], public["nested"]["values"])

    def test_partial_success_publication_is_replaced_by_failure_report(self) -> None:
        output = self.root / "posthoc-report.json"
        marker = output.with_name(output.name + ".incomplete")
        output.write_bytes(b'{"integrityPassed":true,"runSucceeded":true}\n')
        marker.write_bytes(posthoc.acceptance.REPORT_INCOMPLETE_MARKER)
        args = argparse.Namespace(candidate_commit="a" * 40)
        error = posthoc.acceptance.AcceptanceError("changed", stage="artifact")

        posthoc._record_posthoc_failure(args, output, marker, error)

        persisted = json.loads(output.read_text(encoding="utf-8"))
        self.assertIs(persisted["acceptancePassed"], None)
        self.assertFalse(persisted["evaluationCompleted"])
        self.assertFalse(persisted["integrityPassed"])
        self.assertFalse(persisted["runSucceeded"])
        self.assertFalse(marker.exists())

    def test_diagnostic_exit_success_means_integrity_not_quality_acceptance(self) -> None:
        self.assertEqual(
            0,
            posthoc._diagnostic_exit_code(
                {
                    "diagnosticThresholdComparisonPassed": False,
                    "integrityPassed": True,
                    "runSucceeded": True,
                }
            ),
        )
        self.assertEqual(
            2,
            posthoc._diagnostic_exit_code({"integrityPassed": False, "runSucceeded": True}),
        )

    def test_report_is_diagnostic_only_and_rejects_private_fields(self) -> None:
        report, seal_context = self._report()
        posthoc._validate_posthoc_report(report, seal_context)
        for key, value in (
            ("acceptancePassed", False),
            ("independentHoldout", True),
            ("repairPromptedByHeldoutFailure", False),
            ("evaluationRole", "confirmatory"),
        ):
            changed = copy.deepcopy(report)
            changed[key] = value
            with self.assertRaises(posthoc.acceptance.AcceptanceError):
                posthoc._validate_posthoc_report(changed, seal_context)

        leaked = copy.deepcopy(report)
        leaked["evidence"]["leak"] = {"recognizedText": "private evidence"}
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "forbidden"):
            posthoc._validate_posthoc_report(leaked, seal_context)
        path_leak = copy.deepcopy(report)
        path_leak["evidence"]["leak"] = "C:\\private\\evidence.json"
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "absolute path"):
            posthoc._validate_posthoc_report(path_leak, seal_context)

        mismatched = copy.deepcopy(report)
        mismatched["evidence"]["artifacts"]["posthocTestSnapshot"]["sha256"] = "f" * 64
        rebound = replace(
            seal_context,
            report_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(mismatched),
        )
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "binding"):
            posthoc._validate_posthoc_report(mismatched, rebound)

        for name, mutate in (
            (
                "overlap row restored",
                lambda value: value["evidence"]["evaluationCorpus"]["scoringView"][
                    "rowIndices"
                ].__setitem__(0, posthoc.EXPECTED_CALIBRATION_OVERLAP_RECORDS[0][0]),
            ),
            (
                "clean row omitted",
                lambda value: value["evidence"]["evaluationCorpus"]["scoringView"][
                    "rowIndices"
                ].pop(),
            ),
            (
                "determinism input changed",
                lambda value: value["evidence"]["evaluationCorpus"]["determinismView"][
                    "rowIndices"
                ].__setitem__(0, 10),
            ),
        ):
            with self.subTest(name=name):
                changed = copy.deepcopy(report)
                mutate(changed)
                rebound = self._rebind_post_run(changed, seal_context)
                with self.assertRaisesRegex(
                    posthoc.acceptance.AcceptanceError,
                    "evaluation corpus",
                ):
                    posthoc._validate_posthoc_report(changed, rebound)

    def test_pre_run_trust_anchor_rejects_coherent_report_rebinding(self) -> None:
        def mutate_identity(value: dict) -> None:
            value["identity"]["candidate"] = "forged"
            digest = posthoc.acceptance.sroie_policy.sha256_canonical(value["identity"])
            value["identitySha256"] = digest
            value["evidence"]["v3Calibration"]["candidateIdentitySha256"] = digest

        def mutate_runtime_and_worker(value: dict) -> None:
            value["identity"]["runtimeProfiles"]["cpu"]["executableSha256"] = "c" * 64
            value["identity"]["runtimeProfiles"]["directml"]["executableSha256"] = "d" * 64
            value["identity"]["worker"]["sha256"] = "e" * 64
            for index, backend in enumerate(value["evidence"]["backends"]):
                runtime_sha256 = "c" * 64 if index == 0 else "d" * 64
                backend["runtimeSha256"] = runtime_sha256
                backend["workerSha256"] = "e" * 64
                backend["runtime"]["executableSha256"] = runtime_sha256
                runs = [backend["qualityRun"], *backend["determinism"]["runs"]]
                for run in runs:
                    run["runtimeSha256"] = runtime_sha256
                    run["workerSha256"] = "e" * 64
            digest = posthoc.acceptance.sroie_policy.sha256_canonical(value["identity"])
            value["identitySha256"] = digest
            value["evidence"]["v3Calibration"]["candidateIdentitySha256"] = digest

        def mutate_bound_evaluation_inventory(value: dict) -> None:
            value["evidence"]["artifacts"]["posthocEvaluationInventory"]["sha256"] = "f" * 64
            value["evidence"]["evaluationCorpus"]["scoringView"]["inventorySha256"] = "f" * 64

        mutations = (
            lambda value: (
                value.update(candidateCommit="f" * 40),
                value["evidence"]["candidate"].update(commit="f" * 40),
            ),
            lambda value: value["evidence"]["candidate"].update(
                runnerSha256="f" * 64,
                candidateSourceSetSha256="e" * 64,
            ),
            lambda value: value["evidence"]["consumedAttempt"].update(claimSha256="f" * 64),
            lambda value: value["evidence"]["consumedAttempt"]["failure"].update(stage="scoring"),
            lambda value: value["evidence"]["v3Calibration"].update(reportSha256="f" * 64),
            lambda value: value["evidence"]["artifacts"]["posthocInventory"].update(
                sha256="f" * 64
            ),
            mutate_bound_evaluation_inventory,
            mutate_identity,
            mutate_runtime_and_worker,
        )
        for mutate in mutations:
            with self.subTest(mutation=mutate):
                report, context = self._report()
                mutate(report)
                rebound = self._rebind_post_run(report, context)
                with self.assertRaisesRegex(
                    posthoc.acceptance.AcceptanceError,
                    "binding",
                ):
                    posthoc._validate_posthoc_report(report, rebound)

    def test_policy_value_mutation_is_rejected_by_immutable_digest(self) -> None:
        report, context = self._report()
        context.validated_policy.value["thresholds"] = {}
        with self.assertRaisesRegex(
            posthoc.acceptance.AcceptanceError,
            "sealing context changed",
        ):
            posthoc._validate_posthoc_report(report, context)

    def test_observed_failed_integrity_cannot_be_relabelled_successful(self) -> None:
        report, context = self._report()
        confidence = report["evidence"]["crossBackendIntegrity"]["confidenceParity"]
        confidence["passed"] = False
        confidence["maximumAbsoluteDelta"] = confidence["threshold"] + 0.01
        checks = report["evidence"]["crossBackendIntegrity"]["checks"]
        checks["confidenceParityPassed"] = False
        report["integrityPassed"] = False
        report["diagnosticThresholdComparisonPassed"] = False
        report["finalDisposition"] = "diagnostic-integrity-failed"
        report["evidence"]["diagnosticPolicyComparison"]["reportableResultPassed"] = False
        context = replace(
            self._rebind_post_run(report, context),
            observed_integrity_passed=False,
            observed_threshold_comparison_passed=False,
            observed_confidence_parity_json=(
                posthoc.acceptance.sroie_policy.canonical_json(confidence)
            ),
        )
        posthoc._validate_posthoc_report(report, context)

        confidence["passed"] = True
        confidence["maximumAbsoluteDelta"] = 0.0
        checks["confidenceParityPassed"] = True
        report["integrityPassed"] = True
        report["diagnosticThresholdComparisonPassed"] = True
        report["finalDisposition"] = "diagnostic-complete"
        report["evidence"]["diagnosticPolicyComparison"]["reportableResultPassed"] = True
        rebound = self._rebind_post_run(report, context)
        with self.assertRaisesRegex(
            posthoc.acceptance.AcceptanceError,
            "binding|cannot be authenticated",
        ):
            posthoc._validate_posthoc_report(report, rebound)

    def test_backend_result_claim_and_descriptor_must_match(self) -> None:
        report, context = self._report()
        artifact = report["evidence"]["backendResultArtifacts"][
            "results/cpu/quality-all-100/strings.jsonl"
        ]
        artifact["sha256"] = "f" * 64
        rebound = self._rebind_post_run(report, context)
        with self.assertRaisesRegex(
            posthoc.acceptance.AcceptanceError,
            "binding",
        ):
            posthoc._validate_posthoc_report(report, rebound)

        report, context = self._report()
        report["evidence"]["backends"][0]["qualityRun"]["rawOutputHashes"]["stringsSha256"] = (
            "f" * 64
        )
        rebound = self._rebind_post_run(report, context)
        with self.assertRaisesRegex(
            posthoc.acceptance.AcceptanceError,
            "binding",
        ):
            posthoc._validate_posthoc_report(report, rebound)

    def test_coherently_resealed_backend_forgeries_are_rejected(self) -> None:
        def forge_component_hashes(value: dict) -> None:
            run = value["evidence"]["backends"][0]["qualityRun"]
            run["rawOutputHashes"]["stringsSha256"] = "f" * 64
            run["rawOutputHashes"]["assessmentsSha256"] = "e" * 64
            artifacts = value["evidence"]["backendResultArtifacts"]
            artifacts["results/cpu/quality-all-100/strings.jsonl"]["sha256"] = "f" * 64
            artifacts["results/cpu/quality-all-100/assessments.jsonl"]["sha256"] = "e" * 64

        def forge_thread_counts(value: dict) -> None:
            backend = value["evidence"]["backends"][0]
            backend["resolvedThreadCounts"] = {"forged": 999999}
            for run in [backend["qualityRun"], *backend["determinism"]["runs"]]:
                run["resolvedThreadCounts"] = {"forged": 999999}

        def forge_timings(value: dict) -> None:
            for backend in value["evidence"]["backends"]:
                quality = backend["qualityRun"]
                quality["elapsedSeconds"] = 0.5
                quality["documentsPerSecond"] = posthoc.EXPECTED_UNCONTAMINATED_TEST_DOCUMENTS / 0.5
                backend["qualityElapsedSeconds"] = quality["elapsedSeconds"]
                backend["qualityDocumentsPerSecond"] = quality["documentsPerSecond"]
                for run in backend["determinism"]["runs"]:
                    run["elapsedSeconds"] = 0.25
                    run["documentsPerSecond"] = posthoc.acceptance.DETERMINISM_DOCUMENTS / 0.25

        mutations = (
            (
                "extra backend field",
                lambda value: value["evidence"]["backends"][0].update(unexpectedClaim="forged"),
            ),
            (
                "negative string count",
                lambda value: value["evidence"]["backends"][0]["qualityRun"].update(
                    stringRecords=-999
                ),
            ),
            (
                "negative elapsed time",
                lambda value: value["evidence"]["backends"][0]["qualityRun"].update(
                    elapsedSeconds=-1.0
                ),
            ),
            (
                "quality provider swap",
                lambda value: value["evidence"]["backends"][0]["qualityRun"].update(
                    requestedProvider="hybrid"
                ),
            ),
            (
                "determinism provider swap",
                lambda value: value["evidence"]["backends"][0]["determinism"]["runs"][0].update(
                    requestedProvider="hybrid"
                ),
            ),
            (
                "forged provenance error",
                lambda value: value["evidence"]["backends"][0]["qualityRun"].update(
                    provenanceErrors=["forged"]
                ),
            ),
            (
                "quality canonical evidence",
                lambda value: value["evidence"]["backends"][0]["qualityRun"].update(
                    canonicalEvidenceSha256="f" * 64
                ),
            ),
            (
                "determinism critical evidence",
                lambda value: value["evidence"]["backends"][0]["determinism"]["runs"][0].update(
                    criticalEvidenceSha256="f" * 64
                ),
            ),
            ("component hashes and descriptors", forge_component_hashes),
            ("resolved thread maps", forge_thread_counts),
            ("timing and throughput", forge_timings),
            (
                "determinism row swap",
                lambda value: value["evidence"]["backends"][0]["determinism"].update(
                    rowIndices=[999]
                ),
            ),
            (
                "unbound raw pair",
                lambda value: value["evidence"]["backends"][0]["qualityRun"][
                    "rawOutputHashes"
                ].update(pairSha256="f" * 64),
            ),
        )
        for name, mutate in mutations:
            with self.subTest(name=name):
                report, context = self._report()
                mutate(report)
                rebound = self._rebind_post_run(report, context)
                with self.assertRaises(posthoc.acceptance.AcceptanceError):
                    posthoc._validate_posthoc_report(report, rebound)

        report, context = self._report()
        confidence = report["evidence"]["crossBackendIntegrity"]["confidenceParity"]
        confidence.update(
            changedRecords=1,
            comparedRecords=999,
            maximumAbsoluteDelta=confidence["threshold"] / 2,
            meanAbsoluteDelta=confidence["threshold"] / 4,
        )
        rebound = self._rebind_post_run(report, context)
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "binding"):
            posthoc._validate_posthoc_report(report, rebound)

        report, context = self._report()
        perfect_metrics = copy.deepcopy(report["metrics"])
        for item in perfect_metrics["perDocument"]:
            item["counts"].update(
                characterEdits=0,
                matchingCharacters=100,
                matchingTokensOrderInvariant=100,
                matchingWords=100,
                wordEdits=0,
            )
            for coverage_name in ("detection", "endToEndExact"):
                coverage = item[coverage_name]
                coverage.update(
                    falseNegative=0,
                    falsePositive=0,
                    hmean=1.0,
                    matchedPredictions=100,
                    matchedTruths=100,
                    precision=1.0,
                    recall=1.0,
                    truePositive=100,
                )
            item.update(pageCer=0.0, pageWer=0.0, tokenF1=1.0)
        perfect_metrics["macro"].update(pageCer=0.0, pageWer=0.0, tokenF1=1.0)
        perfect_micro = perfect_metrics["micro"]
        perfect_micro["counts"].update(
            characterEdits=0,
            matchingCharacters=100 * len(perfect_metrics["perDocument"]),
            matchingTokensOrderInvariant=100 * len(perfect_metrics["perDocument"]),
            matchingWords=100 * len(perfect_metrics["perDocument"]),
            wordEdits=0,
        )
        for coverage_name in ("detection", "endToEndExact"):
            coverage = perfect_micro[coverage_name]
            total = 100 * len(perfect_metrics["perDocument"])
            coverage.update(
                falseNegative=0,
                falsePositive=0,
                hmean=1.0,
                matchedPredictions=total,
                matchedTruths=total,
                precision=1.0,
                recall=1.0,
                truePositive=total,
            )
        perfect_micro.update(pageCer=0.0, pageWer=0.0, tokenF1=1.0)
        strict_metrics = copy.deepcopy(perfect_metrics)
        strict_metrics.pop("caseSensitiveDiagnostics")
        strict_metrics["textNormalization"] = (
            posthoc.acceptance.sroie.SROIE_DIAGNOSTIC_TEXT_NORMALIZATION
        )
        for item in strict_metrics["perDocument"]:
            item["textNormalization"] = posthoc.acceptance.sroie.SROIE_DIAGNOSTIC_TEXT_NORMALIZATION
        strict_per_document_sha256 = hashlib.sha256(
            posthoc.acceptance._per_document_bytes(strict_metrics)
        ).hexdigest()
        perfect_metrics["caseSensitiveDiagnostics"] = {
            "metrics": strict_metrics,
            "metricsSha256": posthoc.acceptance.sroie_policy.sha256_canonical(strict_metrics),
            "perDocumentMetricsSha256": strict_per_document_sha256,
            "textNormalization": (posthoc.acceptance.sroie.SROIE_DIAGNOSTIC_TEXT_NORMALIZATION),
        }
        perfect_sha256 = posthoc.acceptance.sroie_policy.sha256_canonical(perfect_metrics)
        perfect_per_document_sha256 = hashlib.sha256(
            posthoc.acceptance._per_document_bytes(perfect_metrics)
        ).hexdigest()
        report["metrics"] = copy.deepcopy(perfect_metrics)
        report["metricsSha256"] = perfect_sha256
        evaluation = posthoc.acceptance.sroie_policy.evaluate_confirmatory(
            context.validated_policy,
            perfect_metrics,
            expected_identities=self._clean_identities(),
        )
        for backend in report["evidence"]["backends"]:
            provider = backend["requestedProvider"]
            backend["metrics"] = copy.deepcopy(perfect_metrics)
            backend["metricsSha256"] = perfect_sha256
            backend["qualityRun"]["metrics"] = copy.deepcopy(perfect_metrics)
            backend["qualityRun"]["metricsSha256"] = perfect_sha256
            backend["qualityRun"]["perDocumentMetricsSha256"] = perfect_per_document_sha256
            artifact_root = f"results/{provider}/quality-all-100"
            report["evidence"]["backendResultArtifacts"][
                f"{artifact_root}/metrics-per-document.jsonl"
            ]["sha256"] = perfect_per_document_sha256
            report["evidence"]["backendResultArtifacts"][
                f"{artifact_root}/metrics-per-document-case-sensitive.jsonl"
            ]["sha256"] = strict_per_document_sha256
            report["evidence"]["diagnosticPolicyComparison"]["evaluations"][provider] = (
                copy.deepcopy(evaluation)
            )
        rebound = self._rebind_post_run(report, context)
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "binding"):
            posthoc._validate_posthoc_report(report, rebound)

        report, context = self._report()
        forged_identities = [
            {
                "imageSha256": hashlib.sha256(f"forged-{index}".encode()).hexdigest(),
                "rowIndex": 900 + index,
            }
            for index in range(posthoc.acceptance.DETERMINISM_DOCUMENTS)
        ]
        forged_metrics = _confirmatory_metrics(forged_identities)
        forged_metrics_sha256 = posthoc.acceptance.sroie_policy.sha256_canonical(forged_metrics)
        forged_primary_sha256 = hashlib.sha256(
            posthoc.acceptance._per_document_bytes(forged_metrics)
        ).hexdigest()
        forged_strict_sha256 = forged_metrics["caseSensitiveDiagnostics"][
            "perDocumentMetricsSha256"
        ]
        for backend in report["evidence"]["backends"]:
            provider = backend["requestedProvider"]
            for run_index, run in enumerate(backend["determinism"]["runs"], start=1):
                run["metrics"] = copy.deepcopy(forged_metrics)
                run["metricsSha256"] = forged_metrics_sha256
                run["perDocumentMetricsSha256"] = forged_primary_sha256
                root = f"results/{provider}/determinism-rows-0000-0009/run-{run_index:02d}"
                report["evidence"]["backendResultArtifacts"][f"{root}/metrics-per-document.jsonl"][
                    "sha256"
                ] = forged_primary_sha256
                report["evidence"]["backendResultArtifacts"][
                    f"{root}/metrics-per-document-case-sensitive.jsonl"
                ]["sha256"] = forged_strict_sha256
        rebound = self._rebind_post_run(report, context)
        with self.assertRaisesRegex(
            posthoc.acceptance.AcceptanceError,
            "binding",
        ):
            posthoc._validate_posthoc_report(report, rebound)

    def test_report_rejects_synthetic_metrics_even_when_context_hashes_are_rebound(self) -> None:
        report, seal_context = self._report()
        report["metrics"] = {}
        report["metricsSha256"] = posthoc.acceptance.sroie_policy.sha256_canonical({})
        for backend in report["evidence"]["backends"]:
            backend["metrics"] = {}
            backend["metricsSha256"] = report["metricsSha256"]
            backend["qualityRun"]["metrics"] = {}
            backend["qualityRun"]["metricsSha256"] = report["metricsSha256"]
        rebound = replace(
            seal_context,
            backends_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                report["evidence"]["backends"]
            ),
            metrics_sha256=report["metricsSha256"],
            report_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(report),
        )
        with self.assertRaises(posthoc.acceptance.AcceptanceError):
            posthoc._validate_posthoc_report(report, rebound)


if __name__ == "__main__":
    unittest.main()
