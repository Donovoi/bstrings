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

from test_benchmark_ocr_sroie_acceptance import confirmatory_fixture  # noqa: E402

import benchmark_ocr_sroie_posthoc as posthoc  # noqa: E402


class SroiePosthocTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

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
        claim_sha256 = hashlib.sha256(
            posthoc.acceptance._canonical_bytes(initial)
        ).hexdigest()
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
            "quarantineMarkerSha256": posthoc.acceptance.benchmark_core.sha256_file(
                marker_path
            ),
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
        metrics = copy.deepcopy(acceptance_report["metrics"])
        artifact_hashes = {
            "posthocBboxRepairAudit": posthoc.EXPECTED_REPAIR_IDENTITY[
                "bboxRepairAuditSha256"
            ],
            "posthocCorpusManifest": "7" * 64,
            "posthocDuplicateAudit": "8" * 64,
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
        backends = copy.deepcopy(acceptance_report["evidence"]["backends"])
        strict_per_document_sha256 = metrics["caseSensitiveDiagnostics"][
            "perDocumentMetricsSha256"
        ]
        primary_per_document_sha256 = backends[0]["qualityRun"][
            "perDocumentMetricsSha256"
        ]
        for backend in backends:
            quality_run = backend["qualityRun"]
            quality_run["rawOutputHashes"] = {
                "assessmentsSha256": "c" * 64,
                "pairSha256": "d" * 64,
                "stringsSha256": "e" * 64,
            }
            quality_run["perDocumentMetricsSha256"] = primary_per_document_sha256
            backend["determinism"] = {
                "runs": [copy.deepcopy(quality_run), copy.deepcopy(quality_run)]
            }
        backend_result_artifacts = {}
        for name in posthoc._posthoc_backend_result_paths(Path("posthoc-evidence-root")):
            if name.endswith("/assessments.jsonl"):
                digest = "c" * 64
            elif name.endswith("/strings.jsonl"):
                digest = "e" * 64
            elif name.endswith("/metrics-per-document-case-sensitive.jsonl"):
                digest = strict_per_document_sha256
            else:
                digest = primary_per_document_sha256
            backend_result_artifacts[name] = {
                "bytes": 1,
                "path": name,
                "sha256": digest,
            }
        report = {
            "acceptancePassed": None,
            "candidateCommit": "a" * 40,
            "diagnosticThresholdComparisonPassed": True,
            "documents": posthoc.acceptance.sroie_policy.RAW_TEST_ROWS,
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
                    "originalTerminalResultSha256": (
                        posthoc.ORIGINAL_TERMINAL_RESULT_SHA256
                    ),
                    "quarantineMarkerSha256": (
                        posthoc.ORIGINAL_QUARANTINE_MARKER_SHA256
                    ),
                    "status": "quarantined",
                },
                "crossBackendIntegrity": {
                    "checks": {
                        key: value
                        for key, value in acceptance_report["evidence"][
                            "crossBackendIntegrity"
                        ]["checks"].items()
                        if key != "allRawSourceImageSha256SetsDisjoint"
                    },
                    "confidenceParity": copy.deepcopy(
                        acceptance_report["evidence"]["crossBackendIntegrity"][
                            "confidenceParity"
                        ]
                    ),
                },
                "determinism": copy.deepcopy(
                    acceptance_report["evidence"]["determinism"]
                ),
                "diagnosticPolicyComparison": {
                    "allBackendThresholdsPassed": True,
                    "evaluations": copy.deepcopy(
                        acceptance_report["evidence"]["policy"]["evaluations"]
                    ),
                    "isAcceptanceDecision": False,
                    "reportableResultPassed": True,
                },
                "execution": {"backendOrder": ["cpu", "directml", "hybrid"], "threads": 0},
                "generatedAtUtc": "2026-08-05T00:00:00Z",
                "holdoutStatus": {
                    "independentHoldout": False,
                    "oneShotAttemptConsumed": True,
                    "posthocOnly": True,
                },
                "repairedHoldout": {
                    **posthoc.EXPECTED_REPAIR_IDENTITY,
                    "corpusManifestSha256": artifact_hashes["posthocCorpusManifest"],
                    "duplicateAuditSha256": artifact_hashes["posthocDuplicateAudit"],
                    "expectedRepairIdentitySha256": (
                        posthoc.EXPECTED_REPAIR_IDENTITY_SHA256
                    ),
                    "repairAppliedOnlyToDerivedScoringGeometry": True,
                    "repairPolicy": posthoc.acceptance.sroie.SROIE_BBOX_REPAIR_POLICY,
                    "repairIdentity": copy.deepcopy(posthoc.EXPECTED_REPAIR_IDENTITY),
                    "repairIdentitySha256": posthoc.EXPECTED_REPAIR_IDENTITY_SHA256,
                    "repairedRegionCount": posthoc.EXPECTED_REPAIRED_TEST_REGIONS,
                    "parquetSha256": artifact_hashes["posthocTestSnapshot"],
                    "sourceArtifactReadOnlyAndReverified": True,
                    "workerManifestSha256": artifact_hashes["posthocWorkerManifest"],
                },
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
                acceptance_context.expected_identities
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
            "expectedIdentitiesSha256": report["expectedIdentitiesSha256"],
            "identity": report["identity"],
            "identitySha256": report["identitySha256"],
            "repairedHoldout": report["evidence"]["repairedHoldout"],
            "v3Calibration": report["evidence"]["v3Calibration"],
        }
        context = posthoc.PosthocSealContext(
            validated_policy=acceptance_context.validated_policy,
            validated_policy_value_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                acceptance_context.validated_policy.value
            ),
            expected_identities_json=posthoc.acceptance.sroie_policy.canonical_json(
                [dict(item) for item in acceptance_context.expected_identities]
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
            backends_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                evidence["backends"]
            ),
            backend_result_artifacts_sha256=(
                posthoc.acceptance.sroie_policy.sha256_canonical(
                    evidence["backendResultArtifacts"]
                )
            ),
            cross_backend_integrity_sha256=(
                posthoc.acceptance.sroie_policy.sha256_canonical(
                    evidence["crossBackendIntegrity"]
                )
            ),
            determinism_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                evidence["determinism"]
            ),
            evaluations_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                evidence["diagnosticPolicyComparison"]["evaluations"]
            ),
            metrics_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(
                report["metrics"]
            ),
            report_sha256=posthoc.acceptance.sroie_policy.sha256_canonical(report),
        )

    def test_exact_acknowledgement_is_required(self) -> None:
        posthoc._require_acknowledgement(posthoc.ACKNOWLEDGEMENT)
        for value in (None, "", "yes", posthoc.ACKNOWLEDGEMENT.lower()):
            with self.assertRaisesRegex(
                posthoc.acceptance.AcceptanceError, "acknowledgement"
            ):
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
        with patch.object(posthoc.acceptance.sroie_policy, "TEST_BYTES", snapshot.stat().st_size):
            captured = posthoc._capture_posthoc_artifacts(
                paths,
                evidence_root=evidence_root,
                corpus_identity=corpus_identity,
            )
            paths["posthocCorpusManifest"].write_bytes(b"changed")
            with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "artifact"):
                posthoc._recheck_posthoc_artifacts(
                    paths,
                    evidence_root=evidence_root,
                    corpus_identity=corpus_identity,
                    expected=captured,
                )

    def test_all_backend_outputs_are_hash_bound_before_publication(self) -> None:
        phase_root = self.root / "posthoc-diagnostic"
        paths = posthoc._posthoc_backend_result_paths(phase_root)
        self.assertEqual(36, len(paths))
        for name, path in paths.items():
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode("utf-8"))
        captured = posthoc._capture_backend_result_artifacts(phase_root)
        self.assertEqual(set(paths), set(captured))
        changed = next(iter(paths.values()))
        changed.write_bytes(b"changed")
        with self.assertRaisesRegex(posthoc.acceptance.AcceptanceError, "backend result"):
            posthoc._recheck_backend_result_artifacts(phase_root, captured)

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
            posthoc._diagnostic_exit_code(
                {"integrityPassed": False, "runSucceeded": True}
            ),
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
                backend["runtimeSha256"] = "c" * 64 if index == 0 else "d" * 64
                backend["workerSha256"] = "e" * 64
            digest = posthoc.acceptance.sroie_policy.sha256_canonical(value["identity"])
            value["identitySha256"] = digest
            value["evidence"]["v3Calibration"]["candidateIdentitySha256"] = digest

        mutations = (
            lambda value: (
                value.update(candidateCommit="f" * 40),
                value["evidence"]["candidate"].update(commit="f" * 40),
            ),
            lambda value: value["evidence"]["candidate"].update(
                runnerSha256="f" * 64,
                candidateSourceSetSha256="e" * 64,
            ),
            lambda value: value["evidence"]["consumedAttempt"].update(
                claimSha256="f" * 64
            ),
            lambda value: value["evidence"]["consumedAttempt"]["failure"].update(
                stage="scoring"
            ),
            lambda value: value["evidence"]["v3Calibration"].update(
                reportSha256="f" * 64
            ),
            lambda value: value["evidence"]["artifacts"]["posthocInventory"].update(
                sha256="f" * 64
            ),
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
                    "trusted post-hoc input binding",
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
            "cannot be authenticated",
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
            "not bound to its run claim",
        ):
            posthoc._validate_posthoc_report(report, rebound)

        report, context = self._report()
        report["evidence"]["backends"][0]["qualityRun"]["rawOutputHashes"][
            "stringsSha256"
        ] = "f" * 64
        rebound = self._rebind_post_run(report, context)
        with self.assertRaisesRegex(
            posthoc.acceptance.AcceptanceError,
            "not bound to its run claim",
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
