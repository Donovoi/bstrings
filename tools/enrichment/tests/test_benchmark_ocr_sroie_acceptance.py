from __future__ import annotations

import argparse
import base64
import copy
import hashlib
import io
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import benchmark_ocr_sroie_acceptance as acceptance  # noqa: E402


def context(root: Path, identity: dict | None = None) -> acceptance.CalibrationContext:
    return acceptance.CalibrationContext(
        report={},
        report_sha256="a" * 64,
        identity=identity or {"calibrationCorpus": {}, "candidate": "identity"},
        expected_identities=({"imageSha256": "b" * 64, "rowIndex": 0},),
        corpus_manifest=root / "corpus.jsonl",
        duplicate_audit=root / "duplicates.json",
        worker_manifest=root / "worker.jsonl",
        inventory=root / "inventory.txt",
        runtime_inventories={},
        source_image_sha256s=("b" * 64, "c" * 64),
    )


def witness_value(
    calibration: acceptance.CalibrationContext,
    *,
    policy_sha256: str,
    identity: dict,
) -> dict:
    return {
        "calibrationReportSha256": calibration.report_sha256,
        "candidateIdentitySha256": acceptance.sroie_policy.sha256_canonical(identity),
        "datasetId": acceptance.sroie_policy.DATASET_ID,
        "datasetRevision": acceptance.sroie_policy.DATASET_REVISION,
        "expectedTestBytes": acceptance.sroie_policy.TEST_BYTES,
        "expectedTestSha256": acceptance.sroie_policy.TEST_SHA256,
        "githubImmutableRelease": {
            "assetName": "sroie-one-shot-witness.json",
            "repository": "Donovoi/bstrings",
            "tag": "sroie-acceptance-witness-v1",
        },
        "policySha256": policy_sha256,
        "protocol": acceptance.PROTOCOL,
        "schemaVersion": acceptance.SCHEMA_VERSION,
        "sourceCommitSha1": "7" * 40,
    }


def release_verification(value: dict, witness_sha256: str) -> dict:
    release = value["githubImmutableRelease"]
    statement = {
        "_type": "https://in-toto.io/Statement/v1",
        "predicate": {
            "ownerId": acceptance.PINNED_RELEASE_OWNER_ID,
            "purl": f"pkg:github/{release['repository']}@{release['tag']}",
            "repository": release["repository"],
            "repositoryId": acceptance.PINNED_RELEASE_REPOSITORY_ID,
            "tag": release["tag"],
        },
        "predicateType": "https://in-toto.io/attestation/release/v0.2",
        "subject": [
            {
                "digest": {"sha1": value["sourceCommitSha1"]},
                "uri": f"pkg:github/{release['repository']}@{release['tag']}",
            },
            {
                "digest": {"sha256": witness_sha256},
                "name": release["assetName"],
            },
        ],
    }
    return {
        "attestation": {
            "bundle": {
                "mediaType": "application/vnd.dev.sigstore.bundle.v0.3+json",
                "dsseEnvelope": {
                    "payload": base64.b64encode(
                        acceptance.sroie_policy.canonical_json(statement).encode("utf-8")
                    ).decode("ascii"),
                    "payloadType": "application/vnd.in-toto+json",
                    "signatures": [{"sig": "signature"}],
                }
            }
        },
        "verificationResult": {
            "mediaType": "application/vnd.dev.sigstore.verificationresult+json;version=0.1",
            "signature": {
                "certificate": {
                    "certificateIssuer": "GitHub Fulcio",
                    "subjectAlternativeName": "https://dotcom.releases.github.com",
                }
            },
            "statement": statement,
            "verifiedTimestamps": [{"timestamp": "2026-08-05T00:00:00Z"}],
        },
    }


def confirmatory_report(*, accepted: bool, integrity: bool = True) -> dict:
    identity = {"candidate": "synthetic"}
    metrics: dict[str, object] = {}
    return {
        "acceptancePassed": accepted,
        "documents": acceptance.sroie_policy.RAW_TEST_ROWS,
        "evaluationCompleted": True,
        "evaluationRole": "confirmatory",
        "evidence": {
            "artifacts": {"confirmatoryDuplicateAudit": {"sha256": "2" * 64}},
            "testSnapshotSha256": acceptance.sroie_policy.TEST_SHA256,
        },
        "expectedIdentitiesSha256": "1" * 64,
        "finalDisposition": "accepted" if accepted else "rejected",
        "identity": identity,
        "identitySha256": acceptance.sroie_policy.sha256_canonical(identity),
        "integrityPassed": integrity,
        "metrics": metrics,
        "metricsSha256": acceptance.sroie_policy.sha256_canonical(metrics),
        "protocol": acceptance.PROTOCOL,
        "rawRows": acceptance.sroie_policy.RAW_TEST_ROWS,
        "runSucceeded": True,
        "schemaVersion": acceptance.SCHEMA_VERSION,
    }


class SroieAcceptanceTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_dataset_one_shot_ledger_name_is_stable_across_protocol_versions(self) -> None:
        self.assertEqual(
            "icdar2019-sroie-bffe40c26759-test-one-shot-v1.json",
            acceptance.CONFIRMATORY_ATTEMPT_LEDGER.name,
        )

    def test_isolated_github_environment_requires_only_an_explicit_token(self) -> None:
        outer_token = acceptance.os.environ.get("GH_TOKEN")
        with (
            patch.object(acceptance, "_ENTRY_GH_TOKEN", None),
            self.assertRaisesRegex(acceptance.AcceptanceError, "GH_TOKEN"),
        ):
            acceptance._fresh_gh_environment(self.root / "missing-token")
        with (
            patch.object(acceptance, "_ENTRY_GH_TOKEN", "bad token"),
            self.assertRaisesRegex(acceptance.AcceptanceError, "GH_TOKEN"),
        ):
            acceptance._fresh_gh_environment(self.root / "invalid-token")
        with patch.object(acceptance, "_ENTRY_GH_TOKEN", "g" * 40):
            environment = acceptance._fresh_gh_environment(self.root / "authenticated")
        self.assertEqual("g" * 40, environment["GH_TOKEN"])
        self.assertEqual("1", environment["GH_PROMPT_DISABLED"])
        self.assertEqual(
            str(self.root / "authenticated" / "gh-config"),
            environment["GH_CONFIG_DIR"],
        )
        self.assertEqual(outer_token, acceptance.os.environ.get("GH_TOKEN"))

    def test_determinism_binds_the_case_insensitive_scoring_profile(self) -> None:
        documents = tuple(SimpleNamespace(row_index=index) for index in range(10))
        corpus = SimpleNamespace(documents=documents, selection_sha256="a" * 64)
        repetition = {"qualityGatePassed": None}
        run = {
            "byteDeterministic": True,
            "canonicalEvidenceDeterministic": True,
            "criticalEvidenceDeterministic": True,
            "executionProviderCountsStable": True,
            "metricsDeterministic": True,
            "provenancePassed": True,
            "determinismRepetitions": acceptance.DETERMINISM_REPETITIONS,
            "determinismRows": acceptance.DETERMINISM_DOCUMENTS,
            "qualityGatePassed": None,
            "stableResolvedProvider": True,
            "stableRuntime": True,
            "stableTextNormalization": True,
            "textNormalization": acceptance.sroie.SROIE_PRIMARY_TEXT_NORMALIZATION,
            "stableResolvedThreadCounts": True,
            "stableResolvedWorkerCounts": True,
            "determinism": {
                "rowIndices": list(range(10)),
                "selectionSha256": corpus.selection_sha256,
                "runs": [
                    dict(repetition) for _ in range(acceptance.DETERMINISM_REPETITIONS)
                ],
            },
        }
        self.assertTrue(all(acceptance._determinism_checks(run, corpus).values()))
        run["textNormalization"] = acceptance.sroie.SROIE_DIAGNOSTIC_TEXT_NORMALIZATION
        self.assertFalse(
            acceptance._determinism_checks(run, corpus)["stableTextNormalization"]
        )

    def test_rescore_binds_recomputable_primary_and_strict_metrics(self) -> None:
        polygon = ((0.0, 0.0), (20.0, 0.0), (20.0, 10.0), (0.0, 10.0))
        source = self.root / "receipt.jpg"
        document = acceptance.cord.CordDocument(
            row_index=0,
            image_id=0,
            relative_path="images/receipt.jpg",
            path=source,
            length=1,
            sha256="a" * 64,
            annotation_sha256="b" * 64,
            lines=(acceptance.cord.CordLine(0, "TOTAL", polygon),),
            dontcare_polygons=(),
            repeating_symbol_polygons=(),
            clipped_valid_lines=0,
            clipped_dontcare_regions=0,
            clipped_repeating_symbol_regions=0,
        )
        corpus = acceptance.cord.ExtractedCorpus(
            documents=(document,),
            corpus_manifest=self.root / "corpus.jsonl",
            worker_manifest=self.root / "worker.jsonl",
            inventory=self.root / "inventory.txt",
            corpus_manifest_sha256="c" * 64,
            worker_manifest_sha256="d" * 64,
            selection_sha256="e" * 64,
        )
        record = {
            "attributes": {
                "box": [[0, 0], [20, 0], [20, 10], [0, 10]],
                "confidence": 1.0,
                "coordinateSpace": "render-pixels",
                "pageNumber": 1,
            },
            "origin": {"kind": "ocr"},
            "recordId": "case-only",
            "sourceFile": str(source),
            "text": "total",
        }
        strict = acceptance.cord.score_records(corpus, [record])
        output = self.root / "result"
        output.mkdir()
        strings_bytes = acceptance._canonical_bytes(record)
        (output / "strings.jsonl").write_bytes(strings_bytes)
        run = {
            "metrics": strict,
            "rawOutputHashes": {"stringsSha256": hashlib.sha256(strings_bytes).hexdigest()},
        }
        result = acceptance._rescore_backend_run(run, corpus, output, backend_name="cpu")
        primary = result["metrics"]
        diagnostic = primary["caseSensitiveDiagnostics"]
        self.assertEqual(1.0, primary["micro"]["tokenF1"])
        self.assertEqual(0.0, diagnostic["metrics"]["micro"]["tokenF1"])
        self.assertEqual(
            acceptance.sroie_policy.sha256_canonical(diagnostic["metrics"]),
            diagnostic["metricsSha256"],
        )
        self.assertEqual(
            hashlib.sha256(
                (output / "metrics-per-document-case-sensitive.jsonl").read_bytes()
            ).hexdigest(),
            diagnostic["perDocumentMetricsSha256"],
        )
        self.assertEqual("total", record["text"])
        changed = {**record, "text": "different"}
        (output / "strings.jsonl").write_bytes(acceptance._canonical_bytes(changed))
        with self.assertRaisesRegex(
            acceptance.AcceptanceError, "differs from the validated worker output"
        ):
            acceptance._rescore_backend_run(run, corpus, output, backend_name="cpu")

    def test_cli_separates_train_and_postclaim_piped_test_path(self) -> None:
        common = [
            "--worker",
            "worker.py",
            "--model-pack",
            "model.json",
            "--cpu-python",
            "cpu.exe",
            "--directml-python",
            "dml.exe",
            "--work-directory",
            "work",
            "--output",
            "report.json",
        ]
        calibration = acceptance.parse_arguments(
            [
                "calibration",
                *common,
                "--train-parquet",
                "train.parquet",
                "--policy-output",
                "policy.json",
            ]
        )
        self.assertTrue(hasattr(calibration, "train_parquet"))
        self.assertFalse(hasattr(calibration, "test_parquet"))
        confirmatory = acceptance.parse_arguments(
            [
                "confirmatory",
                *common,
                "--calibration-report",
                "calibration.json",
                "--calibration-evidence-root",
                "evidence",
                "--policy",
                "policy.json",
                "--gh-executable",
                "gh.exe",
                "--release-tag",
                "sroie-acceptance-witness-v1",
                "--witness-asset",
                "sroie-one-shot-witness.json",
            ]
        )
        self.assertFalse(hasattr(confirmatory, "test_parquet"))
        self.assertFalse(hasattr(confirmatory, "train_parquet"))
        self.assertTrue(hasattr(confirmatory, "gh_executable"))

    def test_preclaim_failure_does_not_touch_held_out_path(self) -> None:
        events: list[str] = []
        calibration = context(self.root)
        args = argparse.Namespace(
            work_directory=self.root / "work",
            policy=self.root / "policy.json",
            gh_executable=self.root / "gh.exe",
            release_tag="sroie-acceptance-witness-v1",
            witness_asset="sroie-one-shot-witness.json",
        )
        with (
            patch.object(acceptance, "_ACCESS_EVENT_HOOK", events.append),
            patch.object(acceptance, "build_identity", return_value=calibration.identity),
            patch.object(
                acceptance,
                "_verify_persisted_runtime_context",
                side_effect=acceptance.AcceptanceError("runtime", stage="runtime-identity"),
            ),
            self.assertRaisesRegex(acceptance.AcceptanceError, "runtime"),
        ):
            acceptance.execute_confirmatory(args, object(), calibration)
        self.assertEqual([], events)

    def test_invalid_witness_fails_before_ledger_and_test_access(self) -> None:
        events: list[str] = []
        calibration = context(self.root)
        args = argparse.Namespace(
            work_directory=self.root / "work",
            calibration_report=self.root / "calibration.json",
            policy=self.root / "policy.json",
            gh_executable=self.root / "gh.exe",
            release_tag="sroie-acceptance-witness-v1",
            witness_asset="sroie-one-shot-witness.json",
        )
        validated = SimpleNamespace(file_sha256="e" * 64, value={})
        with (
            patch.object(acceptance, "_ACCESS_EVENT_HOOK", events.append),
            patch.object(acceptance, "build_identity", return_value=calibration.identity),
            patch.object(acceptance, "_verify_persisted_runtime_context"),
            patch.object(acceptance.sroie_policy, "validate_policy", return_value=validated),
            patch.object(acceptance, "_recheck_policy"),
            patch.object(
                acceptance,
                "_load_witness",
                side_effect=acceptance.AcceptanceError("witness", stage="witness"),
            ),
            patch.object(acceptance, "_start_attempt") as start,
            patch.object(acceptance, "_snapshot_test_after_claim") as snapshot,
            self.assertRaisesRegex(acceptance.AcceptanceError, "witness"),
        ):
            acceptance.execute_confirmatory(args, object(), calibration)
        start.assert_not_called()
        snapshot.assert_not_called()
        self.assertEqual([], events)

    def test_valid_witness_binds_release_and_candidate(self) -> None:
        candidate = {"candidate": "one"}
        calibration = context(self.root, candidate)
        policy_sha256 = "e" * 64
        value = witness_value(
            calibration,
            policy_sha256=policy_sha256,
            identity=candidate,
        )
        raw_witness = acceptance._canonical_bytes(value)
        witness_sha256 = hashlib.sha256(raw_witness).hexdigest()
        verification = release_verification(value, witness_sha256)
        calls: list[tuple[str, ...]] = []

        def run_gh(_executable: Path, arguments: tuple[str, ...], *, cwd: Path, **_: object):
            calls.append(arguments)
            if arguments == ("--version",):
                return SimpleNamespace(
                    stdout=b"gh version 2.97.0 (2026-07-21)\n", stderr=b"", returncode=0
                )
            if arguments[0:2] == ("release", "verify"):
                return SimpleNamespace(
                    stdout=json.dumps(verification).encode("utf-8"), stderr=b"", returncode=0
                )
            download_root = Path(arguments[arguments.index("--dir") + 1])
            (download_root / value["githubImmutableRelease"]["assetName"]).write_bytes(
                raw_witness
            )
            return SimpleNamespace(stdout=b"", stderr=b"", returncode=0)

        with (
            patch.object(acceptance, "_ENTRY_GH_TOKEN", "g" * 40),
            patch.object(acceptance, "_run_pinned_gh", side_effect=run_gh),
        ):
            validated = acceptance._load_witness(
                self.root / "gh.exe",
                value["githubImmutableRelease"]["tag"],
                value["githubImmutableRelease"]["assetName"],
                context=calibration,
                policy_sha256=policy_sha256,
                identity=candidate,
            )
        self.assertEqual(witness_sha256, validated.file_sha256)
        self.assertEqual(acceptance.PINNED_GH_EXE_SHA256, validated.gh_executable_sha256)
        self.assertEqual(("--version",), calls[0])
        self.assertEqual(("release", "verify"), calls[1][:2])
        self.assertEqual(("release", "download"), calls[2][:2])

        tampered_verification = release_verification(value, "0" * 64)

        def run_tampered(
            executable: Path, arguments: tuple[str, ...], *, cwd: Path, **kwargs: object
        ):
            if arguments[0:2] == ("release", "verify"):
                return SimpleNamespace(
                    stdout=json.dumps(tampered_verification).encode("utf-8"),
                    stderr=b"",
                    returncode=0,
                )
            return run_gh(executable, arguments, cwd=cwd, **kwargs)

        with (
            patch.object(acceptance, "_ENTRY_GH_TOKEN", "g" * 40),
            patch.object(acceptance, "_run_pinned_gh", side_effect=run_tampered),
            self.assertRaisesRegex(acceptance.AcceptanceError, "witness"),
        ):
            acceptance._load_witness(
                self.root / "gh.exe",
                value["githubImmutableRelease"]["tag"],
                value["githubImmutableRelease"]["assetName"],
                context=calibration,
                policy_sha256=policy_sha256,
                identity=candidate,
            )

    def _claim(self, events: list[str]) -> acceptance.AttemptClaim:
        calibration = context(self.root)
        validated_policy = SimpleNamespace(file_sha256="e" * 64)
        witness = acceptance.ValidatedWitness(
            value={},
            file_sha256="f" * 64,
            release_verification={},
            release_verification_sha256="0" * 64,
            gh_executable_sha256=acceptance.PINNED_GH_EXE_SHA256,
            gh_version=acceptance.PINNED_GH_VERSION,
        )
        with (
            patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", self.root / "ledger.json"),
            patch.object(acceptance, "_prepare_machine_ledger_directory", return_value=()),
            patch.object(acceptance, "_ACCESS_EVENT_HOOK", events.append),
        ):
            return acceptance._start_attempt(
                calibration,
                validated_policy,
                calibration.identity,
                witness,
                self.root / "report.json",
            )

    def test_ledger_is_created_before_first_test_access_and_snapshot_hashes_all_bytes(self) -> None:
        events: list[str] = []
        payload = b"synthetic held-out bytes only"
        source_root = self.root / "source"
        source_root.mkdir()
        source = source_root / "test-00000-of-00001.parquet"
        source.write_bytes(payload)
        claim = self._claim(events)
        expected = hashlib.sha256(payload).hexdigest()
        with (
            patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", claim.path),
            patch.object(acceptance, "_ACCESS_EVENT_HOOK", events.append),
            patch.object(acceptance.sroie_policy, "TEST_BYTES", len(payload)),
            patch.object(acceptance.sroie_policy, "TEST_SHA256", expected),
        ):
            snapshot, digest = acceptance._snapshot_test_after_claim(
                str(source), self.root / "snapshot"
            )
        self.assertEqual(payload, snapshot.read_bytes())
        self.assertEqual(expected, digest)
        self.assertEqual(
            [
                "ledger-created",
                "test-path-materialized",
                "test-lstat",
                "snapshot-destination-ready",
                "test-open",
                "test-snapshot-verified",
            ],
            events,
        )

    def test_replay_cannot_create_a_second_machine_global_claim(self) -> None:
        events: list[str] = []
        first = self._claim(events)
        with self.assertRaisesRegex(acceptance.AcceptanceError, "already exists"):
            self._claim(events)
        ledger = json.loads(first.path.read_text(encoding="utf-8"))
        self.assertEqual("started", ledger["status"])
        self.assertEqual(["ledger-created"], events)

    @unittest.skipUnless(os.name == "nt", "Windows Known Folder test")
    def test_machine_ledger_root_ignores_programdata_environment(self) -> None:
        baseline = acceptance._machine_state_root()
        with patch.dict(os.environ, {"PROGRAMDATA": str(self.root / "attacker")}, clear=False):
            changed = acceptance._machine_state_root()
        self.assertEqual(os.path.normcase(str(baseline)), os.path.normcase(str(changed)))
        self.assertNotEqual(
            os.path.normcase(str(changed)),
            os.path.normcase(str(self.root / "attacker")),
        )

    def test_report_transaction_orders_staging_publication_completion_and_seal(self) -> None:
        events: list[str] = []
        claim = self._claim(events)
        output, marker = acceptance._new_report_marker(self.root / "report.json")
        report = confirmatory_report(accepted=True)
        staged = acceptance._stage_report(output, marker, report)
        with patch.object(acceptance, "_ACCESS_EVENT_HOOK", events.append):
            acceptance._prepare_attempt_report(claim, report, staged)
            acceptance._publish_prepared_report(claim, output, marker, staged)
            acceptance._complete_attempt(claim, output)
            self.assertTrue(marker.exists())
            acceptance._seal_report(claim, output, marker)
        self.assertLess(events.index("report-staged"), events.index("report-published"))
        self.assertLess(events.index("report-published"), events.index("ledger-completed"))
        self.assertLess(events.index("ledger-completed"), events.index("report-sealed"))
        self.assertEqual("completed", json.loads(claim.path.read_text())["status"])
        self.assertFalse(marker.exists())

    def test_staged_report_mutation_is_rejected_before_publication(self) -> None:
        claim = self._claim([])
        output, marker = acceptance._new_report_marker(self.root / "report.json")
        report = confirmatory_report(accepted=False)
        staged = acceptance._stage_report(output, marker, report)
        acceptance._prepare_attempt_report(claim, report, staged)
        raw = staged.path.read_bytes()
        staged.path.write_bytes(raw[:-2] + (b"0" if raw[-2:-1] != b"0" else b"1") + raw[-1:])
        with self.assertRaisesRegex(acceptance.AcceptanceError, "identity changed"):
            acceptance._publish_prepared_report(claim, output, marker, staged)
        self.assertEqual("report-staged", json.loads(claim.path.read_text())["status"])
        self.assertTrue(marker.exists())
        self.assertFalse(output.exists())

    def test_crash_after_report_staging_recovers_without_test_access(self) -> None:
        events: list[str] = []
        claim = self._claim(events)
        output, marker = acceptance._new_report_marker(self.root / "report.json")
        report = confirmatory_report(accepted=False)
        staged = acceptance._stage_report(output, marker, report)
        acceptance._prepare_attempt_report(claim, report, staged)
        events.clear()
        with (
            patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", claim.path),
            patch.object(acceptance, "_ACCESS_EVENT_HOOK", events.append),
        ):
            recovered = acceptance._recover_report_commit(
                output, expected_claim=claim
            )
        self.assertEqual(report, recovered)
        self.assertFalse(marker.exists())
        self.assertEqual("completed", json.loads(claim.path.read_text())["status"])
        self.assertFalse(any(name.startswith("test-") for name in events))

    def test_forged_recovery_ledger_is_rejected(self) -> None:
        claim = self._claim([])
        output, marker = acceptance._new_report_marker(self.root / "report.json")
        report = confirmatory_report(accepted=False)
        staged = acceptance._stage_report(output, marker, report)
        acceptance._prepare_attempt_report(claim, report, staged)
        forged = json.loads(claim.path.read_text(encoding="utf-8"))
        forged["policySha256"] = "9" * 64
        forged_initial = dict(claim.value)
        forged_initial["policySha256"] = "9" * 64
        forged["claimSha256"] = hashlib.sha256(
            acceptance._canonical_bytes(forged_initial)
        ).hexdigest()
        claim.path.write_bytes(acceptance._canonical_bytes(forged))
        with (
            patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", claim.path),
            self.assertRaisesRegex(acceptance.AcceptanceError, "authenticated initial claim"),
        ):
            acceptance._recover_report_commit(output, expected_claim=claim)

    def test_cli_fails_closed_on_any_existing_one_shot_ledger(self) -> None:
        ledger = self.root / "existing-ledger.json"
        ledger.write_bytes(b"{}\n")
        output = self.root / "must-not-exist.json"
        args = argparse.Namespace(phase="confirmatory", output=output)
        with (
            patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", ledger),
            patch.object(acceptance, "_require_outer_runtime_isolation"),
            self.assertRaisesRegex(acceptance.AcceptanceError, "automatic recovery is forbidden"),
        ):
            acceptance.run(args)
        self.assertFalse(output.exists())
        self.assertFalse(output.with_name(output.name + ".incomplete").exists())

    def test_postcreate_parent_instability_carries_and_quarantines_claim(self) -> None:
        calibration = context(self.root)
        validated_policy = SimpleNamespace(file_sha256="e" * 64)
        witness = acceptance.ValidatedWitness(
            value={},
            file_sha256="f" * 64,
            release_verification={},
            release_verification_sha256="0" * 64,
            gh_executable_sha256=acceptance.PINNED_GH_EXE_SHA256,
            gh_version=acceptance.PINNED_GH_VERSION,
        )
        ledger_path = self.root / "unstable-ledger.json"
        with (
            patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", ledger_path),
            patch.object(acceptance, "_prepare_machine_ledger_directory", return_value=()),
            patch.object(acceptance, "_parents_stable", side_effect=(True, False)),
            self.assertRaisesRegex(acceptance.AcceptanceError, "parent changed") as captured,
        ):
            acceptance._start_attempt(
                calibration,
                validated_policy,
                calibration.identity,
                witness,
                self.root / "report.json",
            )
        claimed = captured.exception.claim
        self.assertIsInstance(claimed, acceptance.AttemptClaim)
        self.assertTrue(ledger_path.exists())
        acceptance._terminalize_failed_attempt(
            argparse.Namespace(work_directory=self.root / "work"),
            claimed,
            captured.exception,
        )
        self.assertEqual("quarantined", json.loads(ledger_path.read_text())["status"])

    def test_hardlink_alias_to_preclaim_file_is_rejected(self) -> None:
        protected = self.root / "policy.json"
        protected.write_bytes(b"synthetic protected bytes")
        source_root = self.root / "source"
        source_root.mkdir()
        source = source_root / "test-00000-of-00001.parquet"
        try:
            os.link(protected, source)
        except OSError as exc:
            self.skipTest(f"hardlinks unavailable: {exc}")
        object_id = acceptance._regular_file_object_id(protected, name="policy")
        with self.assertRaisesRegex(acceptance.AcceptanceError, "aliases"):
            acceptance._snapshot_test_after_claim(
                str(source),
                self.root / "snapshot",
                forbidden_file_ids=frozenset({object_id}),
            )

    def test_hybrid_lane_coverage_requires_meaningful_work_on_both_lanes(self) -> None:
        self.assertTrue(
            acceptance._meaningful_hybrid_lane_coverage(
                {
                    "stringRecords": 100,
                    "hybridLaneRecordCoverage": {
                        "bothLanesProducedRecords": True,
                        "cpuLaneRecords": 5,
                        "nonCpuLaneRecords": 95,
                    },
                }
            )
        )
        self.assertFalse(
            acceptance._meaningful_hybrid_lane_coverage(
                {
                    "stringRecords": 100,
                    "hybridLaneRecordCoverage": {
                        "bothLanesProducedRecords": True,
                        "cpuLaneRecords": 1,
                        "nonCpuLaneRecords": 99,
                    },
                }
            )
        )

    def test_public_report_privacy_validator_recurses(self) -> None:
        with self.assertRaisesRegex(acceptance.AcceptanceError, "absolute path"):
            acceptance._validate_public_report_privacy(
                {"evidence": {"nested": [{"value": str(self.root / "secret.txt")}]}},
            )
        with self.assertRaisesRegex(acceptance.AcceptanceError, "forbidden evidence"):
            acceptance._validate_public_report_privacy(
                {"evidence": {"nested": [{"sourceText": "private"}]}},
            )

        for field in (
            "groundTruth",
            "labels",
            "prediction",
            "rawOutput",
            "recognizedText",
            "reference",
            "tokens",
            "transcription",
            "words",
        ):
            with self.subTest(field=field), self.assertRaisesRegex(
                acceptance.AcceptanceError, "forbidden evidence"
            ):
                acceptance._validate_public_report_privacy({field: "secret label"})

        for private_path in (r"C:relative\secret.txt", r"\Users\secret.txt"):
            with self.subTest(path=private_path), self.assertRaisesRegex(
                acceptance.AcceptanceError, "absolute path"
            ):
                acceptance._validate_public_report_privacy({"value": private_path})

    def test_confirmatory_report_bindings_reject_impossible_states(self) -> None:
        baseline = confirmatory_report(accepted=True)
        mutations = {
            "acceptance-not-bool": {"acceptancePassed": "true"},
            "accepted-without-integrity": {"integrityPassed": False},
            "disposition-mismatch": {"finalDisposition": "rejected"},
            "invalid-identity-digest": {"expectedIdentitiesSha256": "z" * 64},
            "not-completed-run": {"runSucceeded": False},
            "unknown-field": {"unexpected": True},
        }
        for name, mutation in mutations.items():
            report = copy.deepcopy(baseline)
            report.update(mutation)
            with self.subTest(name=name), self.assertRaisesRegex(
                acceptance.AcceptanceError, "cannot complete"
            ):
                acceptance._confirmatory_report_bindings(report)

        invalid_audit = copy.deepcopy(baseline)
        invalid_audit["evidence"]["artifacts"]["confirmatoryDuplicateAudit"][
            "sha256"
        ] = "z" * 64
        with self.assertRaisesRegex(acceptance.AcceptanceError, "cannot complete"):
            acceptance._confirmatory_report_bindings(invalid_audit)

    def test_snapshot_destination_parent_swap_fails_before_source_open(self) -> None:
        source_root = self.root / "source"
        source_root.mkdir()
        source = source_root / "test-00000-of-00001.parquet"
        source.write_bytes(b"sealed")
        stability_checks = 0

        def become_unstable(*_: object) -> bool:
            nonlocal stability_checks
            stability_checks += 1
            return stability_checks < 2

        with (
            patch.object(acceptance, "_parents_stable", side_effect=become_unstable),
            patch.object(acceptance.os, "open") as opened,
            self.assertRaisesRegex(acceptance.AcceptanceError, "parent changed"),
        ):
            acceptance._snapshot_test_after_claim(
                str(source), self.root / "work" / "confirmatory" / "snapshot"
            )
        opened.assert_not_called()

    def test_snapshot_destination_parent_symlink_is_rejected(self) -> None:
        source_root = self.root / "source"
        source_root.mkdir()
        source = source_root / "test-00000-of-00001.parquet"
        source.write_bytes(b"sealed")
        redirect = self.root / "redirect"
        redirect.mkdir()
        link = self.root / "linked-work"
        try:
            link.symlink_to(redirect, target_is_directory=True)
        except OSError as exc:
            self.skipTest(f"symlinks unavailable: {exc}")
        with self.assertRaisesRegex(acceptance.AcceptanceError, "unsafe parent"):
            acceptance._snapshot_test_after_claim(
                str(source), link / "confirmatory" / "snapshot"
            )
        self.assertEqual([], list(redirect.iterdir()))

    def test_snapshot_oserror_never_leaks_path_through_main(self) -> None:
        sentinel = r"C:\secret\held-out.parquet"
        arguments = argparse.Namespace(phase="calibration")

        def invoke_snapshot(_: argparse.Namespace) -> dict:
            acceptance._snapshot_test_after_claim("unused", self.root / "snapshot")
            return {}

        stderr = io.StringIO()
        with (
            patch.object(acceptance, "parse_arguments", return_value=arguments),
            patch.object(acceptance, "run", side_effect=invoke_snapshot),
            patch.object(
                acceptance,
                "_snapshot_test_after_claim_inner",
                side_effect=OSError(sentinel),
            ),
            patch.object(acceptance.sys, "stderr", stderr),
        ):
            exit_code = acceptance.main([])
        self.assertEqual(1, exit_code)
        self.assertNotIn(sentinel, stderr.getvalue())
        self.assertIn("snapshot filesystem operation failed", stderr.getvalue())

    def test_piped_test_path_is_received_after_ledger_event(self) -> None:
        events: list[str] = []
        with (
            patch.object(acceptance, "_ACCESS_EVENT_HOOK", events.append),
            patch.object(acceptance, "_TEST_PATH_PIPE_DESCRIPTOR", None),
            patch.object(
                acceptance.sys,
                "stdin",
                io.StringIO(str(self.root / "test-00000-of-00001.parquet") + "\n"),
            ),
        ):
            self._claim(events)
            received = acceptance._read_test_path_after_claim()
        self.assertTrue(received.endswith("test-00000-of-00001.parquet"))
        self.assertLess(events.index("ledger-created"), events.index("test-path-received"))

    def test_wrong_hash_consumes_and_quarantines_attempt(self) -> None:
        events: list[str] = []
        payload = b"wrong identity"
        source_root = self.root / "source"
        source_root.mkdir()
        source = source_root / "test-00000-of-00001.parquet"
        source.write_bytes(payload)
        claim = self._claim(events)
        error: Exception
        with (
            patch.object(acceptance.sroie_policy, "TEST_BYTES", len(payload)),
            patch.object(acceptance.sroie_policy, "TEST_SHA256", "0" * 64),
        ):
            with self.assertRaises(acceptance.AcceptanceError) as captured:
                acceptance._snapshot_test_after_claim(str(source), self.root / "snapshot")
            error = captured.exception
        acceptance._fail_attempt(claim, error)
        acceptance._quarantine(argparse.Namespace(work_directory=self.root / "work"), claim)
        ledger = json.loads(claim.path.read_text(encoding="utf-8"))
        self.assertEqual("quarantined", ledger["status"])
        self.assertFalse(ledger["runSucceeded"])
        self.assertFalse((self.root / "snapshot" / source.name).exists())

    def test_terminalization_failure_retains_incomplete_marker_and_no_report(self) -> None:
        claim = self._claim([])
        args = argparse.Namespace(
            phase="confirmatory",
            output=self.root / "terminal-report.json",
            calibration_report=self.root / "calibration.json",
            calibration_evidence_root=self.root / "evidence",
        )

        def fail_after_claim(namespace: argparse.Namespace, *_: object):
            namespace._attempt_claim = claim
            raise acceptance.AcceptanceError("postclaim", stage="backend")

        with (
            patch.object(acceptance, "_require_outer_runtime_isolation"),
            patch.object(acceptance, "verify_candidate", return_value=object()),
            patch.object(acceptance, "load_calibration_context", return_value=context(self.root)),
            patch.object(acceptance, "execute_confirmatory", side_effect=fail_after_claim),
            patch.object(
                acceptance,
                "_terminalize_failed_attempt",
                side_effect=acceptance.AcceptanceError(
                    "terminalization", stage="terminalization"
                ),
            ),
            self.assertRaisesRegex(acceptance.AcceptanceError, "terminalization"),
        ):
            acceptance.run(args)
        self.assertFalse(args.output.exists())
        self.assertTrue(args.output.with_name(args.output.name + ".incomplete").exists())

    def test_source_identity_change_during_snapshot_is_rejected(self) -> None:
        events: list[str] = []
        payload = b"stable bytes but unstable metadata"
        source_root = self.root / "source"
        source_root.mkdir()
        source = source_root / "test-00000-of-00001.parquet"
        source.write_bytes(payload)
        self._claim(events)
        expected = hashlib.sha256(payload).hexdigest()
        before = acceptance.os.lstat(source)
        after = SimpleNamespace(
            st_dev=before.st_dev,
            st_file_attributes=getattr(before, "st_file_attributes", 0),
            st_ino=before.st_ino,
            st_mode=before.st_mode,
            st_size=len(payload),
            st_mtime_ns=before.st_mtime_ns + 1,
        )
        real_fstat = acceptance.os.fstat
        calls = 0

        def mutate_source_after_copy(descriptor: int):
            nonlocal calls
            calls += 1
            if calls == 1:
                return before
            if calls == 4:
                return after
            return real_fstat(descriptor)

        with (
            patch.object(acceptance.sroie_policy, "TEST_BYTES", len(payload)),
            patch.object(acceptance.sroie_policy, "TEST_SHA256", expected),
            patch.object(acceptance.os, "fstat", side_effect=mutate_source_after_copy),
            self.assertRaisesRegex(acceptance.AcceptanceError, "changed") as captured,
        ):
            acceptance._snapshot_test_after_claim(str(source), self.root / "snapshot")
        self.assertEqual("test-toctou", captured.exception.stage)
        self.assertFalse((self.root / "snapshot" / source.name).exists())

    def test_cross_split_overlap_checks_all_source_digests(self) -> None:
        excluded_train_digest = "9" * 64
        with self.assertRaisesRegex(acceptance.AcceptanceError, "duplicate images"):
            acceptance._require_disjoint_source_images(
                ("1" * 64, excluded_train_digest),
                (excluded_train_digest, "2" * 64),
            )
        acceptance._require_disjoint_source_images(("1" * 64,), ("2" * 64,))

    def test_duplicate_audit_recomputes_every_group_decision_and_partition(self) -> None:
        repeated = "3" * 64
        singleton = "4" * 64
        identities = [{"imageSha256": singleton, "rowIndex": 2}]
        audit = {
            "conflictingAnnotationDuplicateGroups": 1,
            "dataset": acceptance.sroie.SROIE_REPOSITORY,
            "duplicateGroupAudit": [
                {
                    "annotationSha256s": ["5" * 64, "6" * 64],
                    "decision": "exclude-conflicting-group",
                    "distinctAnnotationDigests": 2,
                    "excludedRowIndices": [0, 1],
                    "imageSha256": repeated,
                    "includedRowIndices": [],
                    "rowCount": 2,
                    "rowIndices": [0, 1],
                }
            ],
            "duplicateGroups": 1,
            "excludedRowIndices": [0, 1],
            "excludedRows": 2,
            "identicalAnnotationDuplicateGroups": 0,
            "includedRows": 1,
            "policy": acceptance.sroie.SROIE_DUPLICATE_IMAGE_POLICY,
            "protocol": acceptance.sroie.SROIE_PROTOCOL,
            "revision": acceptance.sroie.SROIE_COMMIT,
            "schemaVersion": acceptance.sroie.SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION,
            "selectionSha256": acceptance.sroie_policy.sha256_canonical(identities),
            "sourceImageDigestSetSha256": acceptance.sroie_policy.sha256_canonical(
                sorted({repeated, singleton})
            ),
            "sourceImageDigests": [
                {"imageSha256": repeated, "rowIndex": 0},
                {"imageSha256": repeated, "rowIndex": 1},
                {"imageSha256": singleton, "rowIndex": 2},
            ],
            "sourceRows": 3,
            "split": "train",
        }
        path = self.root / "audit.json"
        path.write_bytes(acceptance._canonical_bytes(audit))
        self.assertEqual(
            (repeated, repeated, singleton),
            acceptance._validate_duplicate_audit(
                path,
                split="train",
                expected_raw_rows=3,
                expected_identities=identities,
            ),
        )
        tampered = copy.deepcopy(audit)
        tampered["duplicateGroupAudit"][0]["decision"] = "keep-lowest-row-index"
        path.write_bytes(acceptance._canonical_bytes(tampered))
        with self.assertRaisesRegex(acceptance.AcceptanceError, "decision changed"):
            acceptance._validate_duplicate_audit(
                path,
                split="train",
                expected_raw_rows=3,
                expected_identities=identities,
            )

    def test_test_duplicate_audit_cannot_exclude_any_row(self) -> None:
        repeated = "3" * 64
        singleton = "4" * 64
        identities = [
            {"imageSha256": repeated, "rowIndex": 0},
            {"imageSha256": repeated, "rowIndex": 1},
            {"imageSha256": singleton, "rowIndex": 2},
        ]
        audit = {
            "conflictingAnnotationDuplicateGroups": 1,
            "dataset": acceptance.sroie.SROIE_REPOSITORY,
            "duplicateGroupAudit": [
                {
                    "annotationSha256s": ["5" * 64, "6" * 64],
                    "decision": "score-all-test-rows",
                    "distinctAnnotationDigests": 2,
                    "excludedRowIndices": [],
                    "imageSha256": repeated,
                    "includedRowIndices": [0, 1],
                    "rowCount": 2,
                    "rowIndices": [0, 1],
                }
            ],
            "duplicateGroups": 1,
            "excludedRowIndices": [],
            "excludedRows": 0,
            "identicalAnnotationDuplicateGroups": 0,
            "includedRows": 3,
            "policy": acceptance.sroie.SROIE_DUPLICATE_IMAGE_POLICY,
            "protocol": acceptance.sroie.SROIE_PROTOCOL,
            "revision": acceptance.sroie.SROIE_COMMIT,
            "schemaVersion": acceptance.sroie.SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION,
            "selectionSha256": acceptance.sroie_policy.sha256_canonical(identities),
            "sourceImageDigestSetSha256": acceptance.sroie_policy.sha256_canonical(
                sorted({repeated, singleton})
            ),
            "sourceImageDigests": [
                {"imageSha256": repeated, "rowIndex": 0},
                {"imageSha256": repeated, "rowIndex": 1},
                {"imageSha256": singleton, "rowIndex": 2},
            ],
            "sourceRows": 3,
            "split": "test",
        }
        path = self.root / "test-audit.json"
        path.write_bytes(acceptance._canonical_bytes(audit))
        self.assertEqual(
            (repeated, repeated, singleton),
            acceptance._validate_duplicate_audit(
                path,
                split="test",
                expected_raw_rows=3,
                expected_identities=identities,
            ),
        )
        audit["duplicateGroupAudit"][0]["decision"] = "exclude-conflicting-group"
        audit["duplicateGroupAudit"][0]["includedRowIndices"] = []
        audit["duplicateGroupAudit"][0]["excludedRowIndices"] = [0, 1]
        path.write_bytes(acceptance._canonical_bytes(audit))
        with self.assertRaisesRegex(acceptance.AcceptanceError, "decision changed"):
            acceptance._validate_duplicate_audit(
                path,
                split="test",
                expected_raw_rows=3,
                expected_identities=identities,
            )

    def test_failure_report_is_path_and_entity_free(self) -> None:
        secret = str(self.root / "test-00000-of-00001.parquet")
        error = acceptance.AcceptanceError(secret, stage="test-identity")
        value = acceptance._failure_report(argparse.Namespace(phase="confirmatory"), error)
        encoded = acceptance.sroie_policy.canonical_json(value)
        self.assertNotIn(secret, encoded)
        self.assertNotIn("company", encoded)
        self.assertNotIn("address", encoded)


if __name__ == "__main__":
    unittest.main()
