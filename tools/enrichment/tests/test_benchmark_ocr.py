from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import benchmark_ocr as benchmark_core  # noqa: E402
from benchmark_ocr import (  # noqa: E402
    CONFIDENCE_ABSOLUTE_TOLERANCE,
    OFFLINE_ENVIRONMENT,
    Backend,
    BenchmarkError,
    CorpusCase,
    _inventory_runtime_root,
    _read_jsonl,
    _runtime_coverage_roots,
    _windows_directml_identity,
    assign_lines,
    backend_result_passed,
    begin_benchmark_report,
    benchmark_backend,
    canonical_evidence_sha256,
    canonical_json,
    critical_evidence_sha256,
    cross_backend_confidence_parity,
    isolated_python_command,
    line_character_error_rate,
    parse_backend,
    publish_benchmark_report,
    runtime_metadata,
    sanitized_runtime_environment,
    token_deltas,
    validate_determinism_policy,
    validate_provenance,
    validate_release_matrix,
)


class OcrBenchmarkTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "synthetic.png"
        self.source.write_bytes(b"synthetic image")
        self.case = CorpusCase(
            case_id="clean-image",
            classification="clean",
            path=self.source.resolve(),
            pages=1,
            expected_lines=("analyst@example.com", "192.0.2.42 CVE-2026-12345"),
            expected_identifiers=("analyst@example.com", "192.0.2.42", "CVE-2026-12345"),
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_publish_requires_the_exact_live_incomplete_marker(self) -> None:
        output = self.root / "report.json"
        output.write_text('{"prior":true}\n', encoding="utf-8")
        marker = begin_benchmark_report(output)
        marker.unlink()

        with self.assertRaisesRegex(BenchmarkError, "completion marker is missing"):
            publish_benchmark_report(output, marker, {"passed": True})

        self.assertEqual('{"prior":true}\n', output.read_text(encoding="utf-8"))

    def _record(self, text: str, provider: str = "cpu") -> dict[str, object]:
        thread_counts = {
            "cpu": {"cpu": 16},
            "cuda": {"cuda": 1},
            "directml": {"directml": 1},
        }.get(provider, {"cpu": 15, "directml": 1})
        worker_counts = {
            "cpu": {"cpu": 4},
            "cuda": {"cuda": 1},
            "directml": {"directml": 1},
        }.get(provider, {"cpu": 1, "directml": 1})
        record: dict[str, object] = {
            "schemaVersion": 1,
            "recordType": "string",
            "text": text,
            "sourceFile": str(self.source.resolve()),
            "location": {"kind": "image_region", "value": "page=1"},
            "origin": {
                "extractor": "rapidocr",
                "version": "3.9.2",
                "kind": "ocr",
                "model": "synthetic/model",
                "revision": "abc123",
                "modelSha256": "1" * 64,
                "provider": provider,
            },
            "attributes": {
                "sourceSha256": hashlib.sha256(b"synthetic image").hexdigest(),
                "modelPackSha256": "2" * 64,
                "runtimeSha256": "3" * 64,
                "renderSha256": "4" * 64,
                "provider": provider,
                "requestedThreads": 0,
                "resolvedThreadCounts": thread_counts,
                "resolvedWorkerCounts": worker_counts,
            },
        }
        record["recordId"] = (
            "sha256:" + hashlib.sha256(canonical_json(record).encode("utf-8")).hexdigest()
        )
        return record

    def _assessment(self, provider: str = "cpu") -> dict[str, object]:
        thread_counts = {
            "cpu": {"cpu": 16},
            "cuda": {"cuda": 1},
            "directml": {"directml": 1},
        }.get(provider, {"cpu": 15, "directml": 1})
        worker_counts = {
            "cpu": {"cpu": 4},
            "cuda": {"cuda": 1},
            "directml": {"directml": 1},
        }.get(provider, {"cpu": 1, "directml": 1})
        return {
            "schemaVersion": 1,
            "recordType": "ocr-assessment",
            "sourceFile": str(self.source.resolve()),
            "status": "processed",
            "sourceSha256": hashlib.sha256(b"synthetic image").hexdigest(),
            "modelPackSha256": "2" * 64,
            "provider": provider,
            "requestedThreads": 0,
            "resolvedThreadCounts": thread_counts,
            "resolvedWorkerCounts": worker_counts,
        }

    def test_backend_requires_supported_provider_and_executable(self) -> None:
        backend = parse_backend(f"directml={sys.executable}")
        self.assertEqual("directml", backend.requested_provider)
        self.assertEqual(Path(sys.executable).resolve(), backend.python_executable)
        with self.assertRaises(argparse.ArgumentTypeError):
            parse_backend("metal=/tmp/python")
        with self.assertRaises(argparse.ArgumentTypeError):
            parse_backend("cpu")

    def _runtime_probe(self, root: Path, executable: Path) -> dict[str, object]:
        return {
            "schemaVersion": 1,
            "pythonVersion": "3.14.5",
            "implementation": "CPython",
            "system": "Windows",
            "release": "11",
            "machine": "AMD64",
            "prefix": str(root),
            "basePrefix": str(root),
            "executable": str(executable),
            "sysPath": [str(root / "python314.zip"), str(root / "Lib")],
            "packages": {"onnxruntime": "1.28.0", "rapidocr": "3.9.2"},
            "onnxruntimeImportable": True,
            "availableExecutionProviders": ["CPUExecutionProvider"],
            "offlineEnvironment": dict(OFFLINE_ENVIRONMENT),
        }

    def test_preclaim_runtime_probes_receive_closed_stdin(self) -> None:
        executable = Path(sys.executable).resolve()
        runtime_probe = self._runtime_probe(Path(sys.prefix).resolve(), executable)
        observed: list[object] = []

        def runtime_capture(command, **kwargs):
            observed.append(kwargs.get("stdin"))
            return subprocess.CompletedProcess(
                command,
                0,
                json.dumps(runtime_probe, sort_keys=True),
                "",
            )

        with patch("benchmark_ocr.subprocess.run", side_effect=runtime_capture):
            benchmark_core._run_runtime_probe(Backend("cpu", executable), 1.0)

        system_root = self.root / "windows"
        powershell = system_root / "System32/WindowsPowerShell/v1.0/powershell.exe"
        powershell.parent.mkdir(parents=True)
        powershell.write_bytes(b"synthetic powershell")
        host_probe = {"adapters": [], "schemaVersion": 1, "systemDlls": []}

        def host_capture(command, **kwargs):
            observed.append(kwargs.get("stdin"))
            return subprocess.CompletedProcess(
                command,
                0,
                json.dumps(host_probe, sort_keys=True),
                "",
            )

        with (
            patch("benchmark_ocr._trusted_windows_directory", return_value=system_root),
            patch("benchmark_ocr.subprocess.run", side_effect=host_capture),
        ):
            benchmark_core._run_windows_host_probe(executable, self.root, 1.0)

        self.assertEqual([subprocess.DEVNULL, subprocess.DEVNULL], observed)

    @staticmethod
    def _directml_probe(directml_sha256: str) -> dict[str, object]:
        def dll(name: str, sha256: str) -> dict[str, object]:
            return {
                "name": name,
                "bytes": 4096,
                "sha256": sha256,
                "fileVersion": "10.0.1.0",
                "productVersion": "10.0.1.0",
            }

        return {
            "schemaVersion": 1,
            "systemDlls": [
                dll("d3d12.dll", "1" * 64),
                dll("directml.dll", directml_sha256),
                dll("dxgi.dll", "3" * 64),
            ],
            "adapters": [
                {
                    "name": "Synthetic GPU",
                    "adapterCompatibility": "Synthetic",
                    "vendorId": "1234",
                    "deviceId": "5678",
                    "subsystemId": "12345678",
                    "driverVersion": "1.2.3.4",
                    "driverDateUtc": "2026-08-05T00:00:00.0000000Z",
                    "infFilename": "oem1.inf",
                    "status": "OK",
                    "pnpDeviceIdSha256": "4" * 64,
                }
            ],
        }

    def test_runtime_root_inventory_is_complete_canonical_and_stable(self) -> None:
        runtime = self.root / "runtime"
        (runtime / "nested").mkdir(parents=True)
        (runtime / "__pycache__").mkdir()
        (runtime / "z.bin").write_bytes(b"z")
        (runtime / "A.txt").write_bytes(b"alpha")
        (runtime / "nested/b.bin").write_bytes(b"beta")
        (runtime / "__pycache__/module.pyc").write_bytes(b"compiled")

        first = _inventory_runtime_root(runtime, root_id="runtime-root-0", roles=("prefix",))
        second = _inventory_runtime_root(runtime, root_id="runtime-root-0", roles=("prefix",))
        self.assertEqual(first, second)
        self.assertEqual(
            {
                "digestSha256",
                "fileCount",
                "files",
                "roles",
                "rootId",
                "schemaVersion",
                "totalBytes",
            },
            set(first),
        )
        paths = [entry["path"] for entry in first["files"]]
        self.assertEqual(sorted(paths), paths)
        self.assertIn("__pycache__/module.pyc", paths)
        self.assertEqual(4, first["fileCount"])
        self.assertEqual(
            hashlib.sha256(canonical_json(first["files"]).encode("utf-8")).hexdigest(),
            first["digestSha256"],
        )

    def test_runtime_root_rejects_reparse_and_mid_inventory_mutation(self) -> None:
        reparse_root = self.root / "reparse-runtime"
        reparse_root.mkdir()
        suspect = reparse_root / "suspect.dll"
        suspect.write_bytes(b"REPARSE")
        original_reparse = benchmark_core._is_link_or_reparse

        def mark_suspect(value: os.stat_result) -> bool:
            return (
                stat.S_ISREG(value.st_mode) and value.st_size == len(b"REPARSE")
            ) or original_reparse(value)

        with (
            patch("benchmark_ocr._is_link_or_reparse", side_effect=mark_suspect),
            self.assertRaisesRegex(BenchmarkError, "link or reparse"),
        ):
            _inventory_runtime_root(reparse_root, root_id="runtime-root-0", roles=("prefix",))

        mutation_root = self.root / "mutation-runtime"
        mutation_root.mkdir()
        mutable = mutation_root / "runtime.dll"
        mutable.write_bytes(b"before")
        original_hash = benchmark_core._hash_inventory_file
        mutated = False

        def mutate_after_hash(path: Path, expected: tuple[int, ...]) -> tuple[int, str]:
            nonlocal mutated
            result = original_hash(path, expected)
            if not mutated:
                path.write_bytes(b"after-change")
                mutated = True
            return result

        with (
            patch("benchmark_ocr._hash_inventory_file", side_effect=mutate_after_hash),
            self.assertRaisesRegex(BenchmarkError, "changed"),
        ):
            _inventory_runtime_root(mutation_root, root_id="runtime-root-0", roles=("prefix",))

    def test_runtime_coverage_binds_missing_load_path_and_uses_zero_based_roots(self) -> None:
        runtime = self.root / "coverage-runtime"
        (runtime / "Lib").mkdir(parents=True)
        executable = runtime / "python.exe"
        executable.write_bytes(b"python")
        probe = self._runtime_probe(runtime, executable)
        roots, load_paths, resolved_executable = _runtime_coverage_roots(probe)
        self.assertEqual(executable, resolved_executable)
        self.assertEqual(1, len(roots))
        self.assertEqual(
            {
                "ordinal": 0,
                "rootId": "runtime-root-0",
                "path": "python314.zip",
                "kind": "missing",
            },
            load_paths[0],
        )
        self.assertEqual("runtime-root-0", load_paths[1]["rootId"])

    def test_runtime_metadata_exposes_exact_multi_root_preimage_without_absolute_paths(
        self,
    ) -> None:
        runtime = self.root / "metadata-runtime"
        (runtime / "Lib").mkdir(parents=True)
        (runtime / "Lib/runtime.py").write_bytes(b"pass\n")
        executable = runtime / "python.exe"
        executable.write_bytes(b"python-runtime")
        probe = self._runtime_probe(runtime, executable)
        backend = Backend("benchmark", executable)
        with patch("benchmark_ocr._run_runtime_probe", return_value=probe):
            metadata = runtime_metadata(backend)
        expected_keys = {
            "environmentPolicy",
            "executable",
            "executableName",
            "executableSha256",
            "implementation",
            "inventorySha256",
            "loadPaths",
            "machine",
            "offlineEnvironment",
            "packages",
            "providerAvailability",
            "pythonVersion",
            "release",
            "requestedProvider",
            "runtimeRoots",
            "schemaVersion",
            "system",
            "windowsDirectml",
        }
        self.assertEqual(expected_keys, set(metadata))
        digest = metadata.pop("inventorySha256")
        self.assertEqual(
            hashlib.sha256(canonical_json(metadata).encode("utf-8")).hexdigest(), digest
        )
        serialized = canonical_json(metadata)
        self.assertNotIn(str(self.root), serialized)
        self.assertEqual("runtime-root-0", metadata["executable"]["rootId"])
        self.assertEqual(
            {"available", "required", "requirementsSatisfied"},
            set(metadata["providerAvailability"]),
        )

    def test_directml_dll_swap_is_rejected(self) -> None:
        availability = {
            "available": ["CPUExecutionProvider", "DmlExecutionProvider"],
            "required": ["CPUExecutionProvider", "DmlExecutionProvider"],
            "requirementsSatisfied": True,
        }
        with (
            patch("benchmark_ocr.platform.system", return_value="Windows"),
            patch(
                "benchmark_ocr._run_windows_host_probe",
                side_effect=[self._directml_probe("2" * 64), self._directml_probe("9" * 64)],
            ),
            self.assertRaisesRegex(BenchmarkError, "changed"),
        ):
            _windows_directml_identity(Backend("directml", Path(sys.executable)), availability, 1)

    def test_directml_probe_forces_utf8_and_preserves_unicode_adapter_name(self) -> None:
        script = benchmark_core._windows_host_probe_script()
        self.assertIn("System.Text.UTF8Encoding($false)", script)
        self.assertIn("[Console]::OutputEncoding = $utf8NoBom", script)
        self.assertIn("$OutputEncoding = $utf8NoBom", script)
        probe = self._directml_probe("2" * 64)
        probe["adapters"][0]["name"] = "Carte vidéo 日本語"  # type: ignore[index]
        normalized = benchmark_core._normalize_windows_directml_probe(probe)
        self.assertEqual("Carte vidéo 日本語", normalized["adapters"][0]["name"])

    def test_directml_probe_canonicalizes_culture_order_and_rejects_duplicates(self) -> None:
        probe = self._directml_probe("2" * 64)
        by_name = {row["name"]: row for row in probe["systemDlls"]}  # type: ignore[union-attr]
        dxcore = {**by_name["dxgi.dll"], "name": "dxcore.dll", "sha256": "5" * 64}
        d3d12core = {
            **by_name["d3d12.dll"],
            "name": "D3D12Core.dll",
            "sha256": "6" * 64,
        }
        probe["systemDlls"] = [
            by_name["dxgi.dll"],
            dxcore,
            d3d12core,
            by_name["directml.dll"],
            by_name["d3d12.dll"],
        ]
        second_adapter = {
            **probe["adapters"][0],  # type: ignore[index]
            "name": "Ångström GPU",
            "pnpDeviceIdSha256": "0" * 64,
        }
        probe["adapters"] = [probe["adapters"][0], second_adapter]  # type: ignore[index]
        normalized = benchmark_core._normalize_windows_directml_probe(probe)
        self.assertEqual(
            ["d3d12.dll", "d3d12core.dll", "directml.dll", "dxcore.dll", "dxgi.dll"],
            [row["name"] for row in normalized["systemDlls"]],
        )
        self.assertEqual(
            ["0" * 64, "4" * 64],
            [row["pnpDeviceIdSha256"] for row in normalized["adapters"]],
        )

        duplicated = copy.deepcopy(probe)
        duplicated["systemDlls"].append(
            {**by_name["dxgi.dll"], "name": "DXGI.DLL", "sha256": "7" * 64}
        )
        with self.assertRaisesRegex(BenchmarkError, "duplicated"):
            benchmark_core._normalize_windows_directml_probe(duplicated)

    def test_isolated_command_and_environment_ignore_python_path_poison(self) -> None:
        worker = self.root / "worker.py"
        worker.write_text("pass\n", encoding="utf-8")
        process_cwd = self.root / "process-cwd"
        process_cwd.mkdir()
        command = isolated_python_command(Path(sys.executable), worker, ("--test",))
        self.assertEqual(["-I", "-B"], command[1:3])
        with patch.dict(
            os.environ,
            {
                "PYTHONPATH": str(self.root / "poison"),
                "PYTHONHOME": str(self.root / "poison-home"),
                "PYTHONSTARTUP": str(self.root / "poison-startup.py"),
                "OMP_NUM_THREADS": "999",
            },
            clear=False,
        ):
            environment = sanitized_runtime_environment(Path(sys.executable), process_cwd)
        for name in ("PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "OMP_NUM_THREADS"):
            self.assertNotIn(name, environment)
        self.assertEqual(
            OFFLINE_ENVIRONMENT, {key: environment[key] for key in OFFLINE_ENVIRONMENT}
        )
        self.assertEqual(str(process_cwd.resolve()), environment["TEMP"])

    @unittest.skipUnless(os.name == "nt", "Windows SystemRoot validation")
    def test_sanitized_environment_rejects_inherited_system_root_poison(self) -> None:
        process_cwd = self.root / "process-cwd"
        process_cwd.mkdir()
        with (
            patch.dict(
                os.environ,
                {"SystemRoot": str(self.root), "WINDIR": str(self.root)},
                clear=False,
            ),
            self.assertRaisesRegex(BenchmarkError, "disagrees"),
        ):
            sanitized_runtime_environment(Path(sys.executable), process_cwd)

    def test_token_deltas_measure_omissions_and_additions_exactly(self) -> None:
        duplicate = self._record("analyst@example.com analyst@example.com 192.0.2.42")
        deltas = {delta.token: delta for delta in token_deltas(self.case, [duplicate])}
        self.assertEqual(1, deltas["analyst@example.com"].added_count)
        self.assertEqual(0, deltas["analyst@example.com"].omitted_count)
        self.assertEqual(1, deltas["CVE-2026-12345"].omitted_count)

    def test_line_error_rate_is_zero_for_exact_records(self) -> None:
        records = [self._record(line) for line in self.case.expected_lines]
        self.assertEqual(0.0, line_character_error_rate(self.case, records))
        self.assertTrue(assign_lines(self.case, records).exact_multiset)

    def test_line_assignment_cannot_reuse_a_candidate(self) -> None:
        records = [self._record(self.case.expected_lines[0])]
        result = assign_lines(self.case, records)
        self.assertEqual(1, result.matched_lines)
        self.assertEqual(1, result.omitted_lines)
        self.assertGreater(result.character_error_rate, 0.0)
        self.assertFalse(result.exact_multiset)

    def test_line_assignment_penalizes_unrelated_output(self) -> None:
        records = [self._record(line) for line in self.case.expected_lines]
        records.append(self._record("unrelated output"))
        result = assign_lines(self.case, records)
        self.assertEqual(1, result.added_lines)
        self.assertGreaterEqual(result.edit_errors, len("unrelated output"))
        self.assertFalse(result.exact_multiset)

    def test_provenance_accepts_reproducible_complete_records(self) -> None:
        record = self._record("analyst@example.com")
        passed, errors, resolved, thread_counts, worker_counts = validate_provenance(
            [record], [self._assessment()], [self.case], "2" * 64, 0
        )
        self.assertTrue(passed, errors)
        self.assertEqual((), errors)
        self.assertEqual("cpu", resolved)
        self.assertEqual({"cpu": 16}, thread_counts)
        self.assertEqual({"cpu": 4}, worker_counts)

    def test_provenance_rejects_provider_and_record_id_changes(self) -> None:
        record = self._record("analyst@example.com", provider="cuda")
        record["recordId"] = "sha256:" + "0" * 64
        passed, errors, _, _, _ = validate_provenance(
            [record], [self._assessment(provider="cpu")], [self.case], "2" * 64, 0
        )
        self.assertFalse(passed)
        self.assertTrue(any("provider" in error for error in errors))
        self.assertTrue(any("identifier" in error for error in errors))

    def test_provenance_rejects_duplicate_assessments_and_unknown_sources(self) -> None:
        record = self._record("analyst@example.com")
        record["sourceFile"] = str(self.root / "unknown.png")
        record["recordId"] = (
            "sha256:"
            + hashlib.sha256(
                canonical_json(
                    {key: value for key, value in record.items() if key != "recordId"}
                ).encode("utf-8")
            ).hexdigest()
        )
        assessment = self._assessment()
        passed, errors, _, _, _ = validate_provenance(
            [record], [assessment, dict(assessment)], [self.case], "2" * 64, 0
        )
        self.assertFalse(passed)
        self.assertTrue(any("duplicate" in error for error in errors))
        self.assertTrue(any("unknown" in error for error in errors))

    def test_canonical_evidence_hash_ignores_only_runtime_provider_fields(self) -> None:
        cpu = self._record("analyst@example.com", provider="cpu")
        directml = self._record("analyst@example.com", provider="directml")
        cpu["attributes"]["requestedProvider"] = "cpu"  # type: ignore[index]
        directml["attributes"]["requestedProvider"] = "directml"  # type: ignore[index]
        cpu["attributes"]["executionProvider"] = "cpu"  # type: ignore[index]
        directml["attributes"]["executionProvider"] = "directml"  # type: ignore[index]
        cpu["attributes"]["requestedThreads"] = 0  # type: ignore[index]
        directml["attributes"]["requestedThreads"] = 0  # type: ignore[index]
        cpu["attributes"]["resolvedThreadCounts"] = {"cpu": 16}  # type: ignore[index]
        directml["attributes"]["resolvedThreadCounts"] = {"directml": 1}  # type: ignore[index]
        cpu["attributes"]["resolvedWorkerCounts"] = {"cpu": 4}  # type: ignore[index]
        directml["attributes"]["resolvedWorkerCounts"] = {"directml": 1}  # type: ignore[index]
        cpu_assessment = self._assessment("cpu")
        cpu_assessment.update(
            {
                "requestedThreads": 0,
                "resolvedThreadCounts": {"cpu": 16},
                "resolvedWorkerCounts": {"cpu": 4},
            }
        )
        directml_assessment = self._assessment("directml")
        directml_assessment.update(
            {
                "requestedThreads": 0,
                "resolvedThreadCounts": {"directml": 1},
                "resolvedWorkerCounts": {"directml": 1},
            }
        )
        self.assertEqual(
            canonical_evidence_sha256([cpu], [cpu_assessment]),
            canonical_evidence_sha256([directml], [directml_assessment]),
        )
        changed = self._record("different text", provider="cpu")
        self.assertNotEqual(
            canonical_evidence_sha256([cpu], [self._assessment("cpu")]),
            canonical_evidence_sha256([changed], [self._assessment("cpu")]),
        )

        cpu["attributes"]["confidence"] = 0.9  # type: ignore[index]
        directml["attributes"]["confidence"] = 0.90005  # type: ignore[index]
        self.assertNotEqual(
            canonical_evidence_sha256([cpu], [cpu_assessment]),
            canonical_evidence_sha256([directml], [directml_assessment]),
        )
        self.assertEqual(
            critical_evidence_sha256([cpu], [cpu_assessment]),
            critical_evidence_sha256([directml], [directml_assessment]),
        )

    def _confidence_backend(
        self,
        provider: str,
        records: list[dict[str, object]],
    ) -> dict[str, object]:
        return {"requestedProvider": provider, "_parityRecords": records}

    def _confidence_record(
        self,
        confidence: object,
        *,
        provider: str = "cpu",
        text: str = "analyst@example.com",
    ) -> dict[str, object]:
        record = self._record(text, provider=provider)
        record["attributes"]["box"] = [[1, 2], [3, 2], [3, 4], [1, 4]]  # type: ignore[index]
        record["attributes"]["confidence"] = confidence  # type: ignore[index]
        return record

    def test_confidence_parity_accepts_bounded_provider_drift(self) -> None:
        cpu = self._confidence_record(0.9)
        directml = self._confidence_record(0.90005, provider="directml")

        result = cross_backend_confidence_parity(
            [
                self._confidence_backend("cpu", [cpu]),
                self._confidence_backend("directml", [directml]),
            ]
        )

        self.assertTrue(result["passed"], result["errors"])
        self.assertEqual(CONFIDENCE_ABSOLUTE_TOLERANCE, result["absoluteTolerance"])
        self.assertEqual(1, result["alignedRecordCount"])
        self.assertEqual(1, result["pairwiseComparisons"])
        self.assertAlmostEqual(0.00005, result["maxAbsoluteDifference"])
        self.assertEqual([], result["errors"])

    def test_confidence_parity_rejects_drift_over_tolerance(self) -> None:
        cpu = self._confidence_record(0.9)
        directml = self._confidence_record(0.9002, provider="directml")

        result = cross_backend_confidence_parity(
            [
                self._confidence_backend("cpu", [cpu]),
                self._confidence_backend("directml", [directml]),
            ]
        )

        self.assertFalse(result["passed"])
        self.assertEqual(1, result["alignedRecordCount"])
        self.assertGreater(result["maxAbsoluteDifference"], CONFIDENCE_ABSOLUTE_TOLERANCE)
        self.assertTrue(any("exceeded" in error for error in result["errors"]))

    def test_confidence_parity_compares_every_provider_pair(self) -> None:
        result = cross_backend_confidence_parity(
            [
                self._confidence_backend("cpu", [self._confidence_record(0.9001)]),
                self._confidence_backend(
                    "directml",
                    [self._confidence_record(0.9, provider="directml")],
                ),
                self._confidence_backend(
                    "hybrid",
                    [self._confidence_record(0.9002, provider="hybrid-directml-cpu")],
                ),
            ]
        )

        self.assertFalse(result["passed"])
        self.assertEqual(3, result["pairwiseComparisons"])
        self.assertAlmostEqual(0.0002, result["maxAbsoluteDifference"])

    def test_confidence_parity_rejects_changed_text_or_box(self) -> None:
        cpu = self._confidence_record(0.9)
        for change in ("text", "box"):
            with self.subTest(change=change):
                directml = copy.deepcopy(self._confidence_record(0.9, provider="directml"))
                if change == "text":
                    directml["text"] = "changed@example.com"
                else:
                    directml["attributes"]["box"][0][0] = 99  # type: ignore[index]
                result = cross_backend_confidence_parity(
                    [
                        self._confidence_backend("cpu", [cpu]),
                        self._confidence_backend("directml", [directml]),
                    ]
                )
                self.assertFalse(result["passed"])
                self.assertEqual(0, result["alignedRecordCount"])
                self.assertTrue(any("alignment differs" in error for error in result["errors"]))

    def test_confidence_parity_rejects_duplicate_or_missing_evidence(self) -> None:
        cpu = self._confidence_record(0.9)
        directml = self._confidence_record(0.9, provider="directml")
        duplicate = cross_backend_confidence_parity(
            [
                self._confidence_backend("cpu", [cpu, copy.deepcopy(cpu)]),
                self._confidence_backend("directml", [directml]),
            ]
        )
        self.assertFalse(duplicate["passed"])
        self.assertTrue(any("duplicate" in error for error in duplicate["errors"]))

        missing = cross_backend_confidence_parity(
            [
                self._confidence_backend("cpu", [cpu]),
                self._confidence_backend("directml", []),
            ]
        )
        self.assertFalse(missing["passed"])
        self.assertTrue(any("missing=1" in error for error in missing["errors"]))

        empty = cross_backend_confidence_parity(
            [
                self._confidence_backend("cpu", []),
                self._confidence_backend("directml", []),
            ]
        )
        self.assertFalse(empty["passed"])
        self.assertTrue(any("no records" in error for error in empty["errors"]))

    def test_confidence_parity_rejects_invalid_confidence_values(self) -> None:
        invalid_values = (float("nan"), float("inf"), 10**1000, -0.01, 1.01, True, None)
        for invalid in invalid_values:
            with self.subTest(confidence=invalid):
                cpu = self._confidence_record(invalid)
                directml = self._confidence_record(0.9, provider="directml")
                result = cross_backend_confidence_parity(
                    [
                        self._confidence_backend("cpu", [cpu]),
                        self._confidence_backend("directml", [directml]),
                    ]
                )
                self.assertFalse(result["passed"])
                self.assertTrue(any("confidence" in error for error in result["errors"]))

    def test_backend_aggregation_requires_stable_provider_and_metrics(self) -> None:
        base = {
            "requestedProvider": "auto",
            "resolvedProvider": "directml",
            "requestedThreads": 0,
            "resolvedThreadCounts": {"directml": 1},
            "resolvedWorkerCounts": {"directml": 1},
            "workerSha256": "9" * 64,
            "runtimeSha256": "a" * 64,
            "elapsedSeconds": 2.0,
            "pagesPerSecond": 5.0,
            "stringRecords": 4,
            "rawOutputHashes": {
                "stringsSha256": "b" * 64,
                "assessmentsSha256": "c" * 64,
                "pairSha256": "d" * 64,
            },
            "canonicalEvidenceSha256": "e" * 64,
            "provenancePassed": True,
            "provenanceErrors": (),
            "cleanGatePassed": True,
            "cleanIdentifierRecall": 1.0,
            "stressIdentifierRecall": 0.8,
            "cases": [{"caseId": "clean", "identifierRecall": 1.0}],
        }
        with patch("benchmark_ocr._run_once", side_effect=[base, dict(base)]):
            result = benchmark_backend(
                backend=Backend("auto", Path(sys.executable)),
                repetitions=2,
                output_root=self.root,
            )
        self.assertTrue(result["cleanGatePassed"])
        self.assertTrue(result["deterministicMetrics"])
        self.assertTrue(result["byteDeterministic"])
        self.assertTrue(result["canonicalEvidenceDeterministic"])
        self.assertTrue(result["stableResolvedProvider"])
        self.assertEqual(5.0, result["medianPagesPerSecond"])

    def test_backend_aggregation_detects_raw_output_divergence(self) -> None:
        base = {
            "requestedProvider": "cpu",
            "resolvedProvider": "cpu",
            "requestedThreads": 0,
            "resolvedThreadCounts": {"cpu": 16},
            "resolvedWorkerCounts": {"cpu": 4},
            "workerSha256": "9" * 64,
            "runtimeSha256": "a" * 64,
            "elapsedSeconds": 1.0,
            "pagesPerSecond": 1.0,
            "rawOutputHashes": {"pairSha256": "b" * 64},
            "canonicalEvidenceSha256": "c" * 64,
            "provenancePassed": True,
            "cleanGatePassed": True,
            "cleanIdentifierRecall": 1.0,
            "stressIdentifierRecall": 1.0,
            "cases": [],
        }
        changed = dict(base)
        changed["rawOutputHashes"] = {"pairSha256": "d" * 64}
        with patch("benchmark_ocr._run_once", side_effect=[base, changed]):
            result = benchmark_backend(
                backend=Backend("cpu", Path(sys.executable)),
                repetitions=2,
                output_root=self.root,
            )
        self.assertFalse(result["byteDeterministic"])
        self.assertTrue(result["canonicalEvidenceDeterministic"])

    def test_single_repetition_never_counts_as_evaluated_determinism(self) -> None:
        base = {
            "requestedProvider": "cpu",
            "resolvedProvider": "cpu",
            "requestedThreads": 0,
            "resolvedThreadCounts": {"cpu": 16},
            "resolvedWorkerCounts": {"cpu": 4},
            "workerSha256": "9" * 64,
            "runtimeSha256": "a" * 64,
            "elapsedSeconds": 1.0,
            "pagesPerSecond": 1.0,
            "rawOutputHashes": {"pairSha256": "b" * 64},
            "canonicalEvidenceSha256": "c" * 64,
            "provenancePassed": True,
            "cleanGatePassed": True,
            "cleanIdentifierRecall": 1.0,
            "stressIdentifierRecall": 1.0,
            "cases": [],
        }
        with patch("benchmark_ocr._run_once", return_value=base):
            result = benchmark_backend(
                backend=Backend("cpu", Path(sys.executable)),
                repetitions=1,
                output_root=self.root,
            )
        self.assertFalse(result["byteDeterminismEvaluated"])
        self.assertTrue(result["byteDeterministic"])
        self.assertFalse(backend_result_passed(result))

    def test_release_and_required_pass_reject_one_repetition(self) -> None:
        with self.assertRaises(BenchmarkError):
            validate_determinism_policy(1, release_matrix=True, require_pass=False)
        with self.assertRaises(BenchmarkError):
            validate_determinism_policy(1, release_matrix=False, require_pass=True)
        validate_determinism_policy(1, release_matrix=False, require_pass=False)
        validate_determinism_policy(2, release_matrix=True, require_pass=True)

    def test_release_matrix_requires_independent_cpu_and_accelerated_paths(self) -> None:
        backends = (
            Backend("cpu", self.root / "cpu-python"),
            Backend("directml", self.root / "dml-python"),
            Backend("hybrid", self.root / "hybrid-python"),
        )
        validate_release_matrix(backends)
        with self.assertRaises(BenchmarkError):
            validate_release_matrix((backends[0], backends[1]))
        validate_release_matrix(
            (backends[0], backends[1], Backend("hybrid", backends[1].python_executable))
        )
        with self.assertRaises(BenchmarkError):
            validate_release_matrix(
                (
                    backends[0],
                    Backend("directml", backends[0].python_executable),
                    backends[2],
                )
            )

    def test_jsonl_reader_enforces_streaming_record_and_line_bounds(self) -> None:
        candidate = self.root / "worker.jsonl"
        candidate.write_text('{"value":1}\n{"value":2}\n', encoding="utf-8")
        with (
            patch("benchmark_ocr.MAX_BENCHMARK_JSONL_RECORDS", 1),
            self.assertRaises(BenchmarkError),
        ):
            _read_jsonl(candidate, backend="cpu", stage="fixture")

        candidate.write_text('{"value":1}', encoding="utf-8")
        with self.assertRaises(BenchmarkError):
            _read_jsonl(candidate, backend="cpu", stage="fixture")

    def test_backend_rejects_unstable_resolved_thread_counts(self) -> None:
        base = {
            "requestedProvider": "cpu",
            "resolvedProvider": "cpu",
            "requestedThreads": 0,
            "resolvedThreadCounts": {"cpu": 16},
            "resolvedWorkerCounts": {"cpu": 4},
            "workerSha256": "9" * 64,
            "runtimeSha256": "a" * 64,
            "elapsedSeconds": 1.0,
            "pagesPerSecond": 1.0,
            "rawOutputHashes": {"pairSha256": "b" * 64},
            "canonicalEvidenceSha256": "c" * 64,
            "provenancePassed": True,
            "cleanGatePassed": True,
            "cleanIdentifierRecall": 1.0,
            "stressIdentifierRecall": 1.0,
            "cases": [],
        }
        changed = dict(base)
        changed["resolvedThreadCounts"] = {"cpu": 15}
        with patch("benchmark_ocr._run_once", side_effect=[base, changed]):
            result = benchmark_backend(
                backend=Backend("cpu", Path(sys.executable)),
                repetitions=2,
                output_root=self.root,
            )

        self.assertFalse(result["stableResolvedThreadCounts"])
        self.assertFalse(backend_result_passed(result))

    def test_provenance_rejects_missing_mismatched_and_invalid_worker_counts(self) -> None:
        for target in ("assessment", "record"):
            with self.subTest(target=target):
                record = self._record("analyst@example.com")
                assessment = self._assessment()
                if target == "assessment":
                    assessment.pop("resolvedWorkerCounts")
                else:
                    record["attributes"].pop("resolvedWorkerCounts")  # type: ignore[union-attr]
                    record["recordId"] = (
                        "sha256:"
                        + hashlib.sha256(
                            canonical_json(
                                {key: value for key, value in record.items() if key != "recordId"}
                            ).encode("utf-8")
                        ).hexdigest()
                    )
                passed, errors, _, _, _ = validate_provenance(
                    [record], [assessment], [self.case], "2" * 64, 0
                )
                self.assertFalse(passed)
                self.assertTrue(any("worker-count keys" in error for error in errors))

        mismatched = self._record("analyst@example.com")
        mismatched["attributes"]["resolvedWorkerCounts"] = {"cpu": 3}  # type: ignore[index]
        mismatched["recordId"] = (
            "sha256:"
            + hashlib.sha256(
                canonical_json(
                    {key: value for key, value in mismatched.items() if key != "recordId"}
                ).encode("utf-8")
            ).hexdigest()
        )
        passed, errors, _, _, worker_counts = validate_provenance(
            [mismatched], [self._assessment()], [self.case], "2" * 64, 0
        )
        self.assertFalse(passed)
        self.assertEqual({}, worker_counts)
        self.assertIn("the run did not report one stable resolved worker-count map", errors)

        invalid = self._assessment()
        invalid["resolvedWorkerCounts"] = {"cpu": 0}
        passed, errors, _, _, _ = validate_provenance([], [invalid], [self.case], "2" * 64, 0)
        self.assertFalse(passed)
        self.assertTrue(any("invalid resolved worker counts" in error for error in errors))

    def test_provenance_rejects_nonconservative_gpu_worker_counts(self) -> None:
        assessment = self._assessment("directml")
        assessment["resolvedWorkerCounts"] = {"directml": 2}
        passed, errors, _, _, _ = validate_provenance([], [assessment], [self.case], "2" * 64, 0)
        self.assertFalse(passed)
        self.assertIn(
            "clean-image assessment used non-conservative GPU worker counts",
            errors,
        )

    def test_backend_rejects_unstable_resolved_worker_counts(self) -> None:
        base = {
            "requestedProvider": "cpu",
            "resolvedProvider": "cpu",
            "requestedThreads": 0,
            "resolvedThreadCounts": {"cpu": 16},
            "resolvedWorkerCounts": {"cpu": 4},
            "workerSha256": "9" * 64,
            "runtimeSha256": "a" * 64,
            "elapsedSeconds": 1.0,
            "pagesPerSecond": 1.0,
            "rawOutputHashes": {"pairSha256": "b" * 64},
            "canonicalEvidenceSha256": "c" * 64,
            "provenancePassed": True,
            "cleanGatePassed": True,
            "cleanIdentifierRecall": 1.0,
            "stressIdentifierRecall": 1.0,
            "cases": [],
        }
        changed = dict(base)
        changed["resolvedWorkerCounts"] = {"cpu": 3}
        with patch("benchmark_ocr._run_once", side_effect=[base, changed]):
            result = benchmark_backend(
                backend=Backend("cpu", Path(sys.executable)),
                repetitions=2,
                output_root=self.root,
            )

        self.assertFalse(result["stableResolvedWorkerCounts"])
        self.assertFalse(backend_result_passed(result))


if __name__ == "__main__":
    unittest.main()
