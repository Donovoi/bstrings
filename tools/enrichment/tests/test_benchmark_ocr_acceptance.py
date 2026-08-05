from __future__ import annotations

import argparse
import copy
import hashlib
import importlib.util
import json
import os
import py_compile
import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import benchmark_ocr as benchmark_core  # noqa: E402
import benchmark_ocr_acceptance as acceptance  # noqa: E402
import benchmark_ocr_cord as cord  # noqa: E402
import ocr_acceptance_policy as policy  # noqa: E402


def polygon(left: float, top: float, right: float, bottom: float):
    return cord.convex_hull(((left, top), (right, top), (right, bottom), (left, bottom)))


def annotation_fixture(*, split: str = "train") -> dict:
    return {
        "meta": {
            "version": "2.0.0",
            "split": split,
            "image_id": 7,
            "image_size": {"width": 100, "height": 100},
        },
        "valid_line": [
            {
                "category": "menu.nm",
                "group_id": 0,
                "sub_group_id": 0,
                "words": [
                    {
                        "text": "Cafe\u0301",
                        "quad": {
                            "x1": 0,
                            "y1": 0,
                            "x2": 40,
                            "y2": 0,
                            "x3": 40,
                            "y3": 10,
                            "x4": 0,
                            "y4": 10,
                        },
                        "is_key": 0,
                        "row_id": 1,
                    }
                ],
            }
        ],
        "dontcare": [],
        "repeating_symbol": [],
        "roi": {},
        "gt_parse": {},
    }


def make_selection(
    role: str, *, count: int = 1, image_sha256: str = "a" * 64
) -> policy.ValidatedRoleSelection:
    return policy.ValidatedRoleSelection(
        entries=tuple(
            policy.SelectionEntry(
                global_index=index,
                image_sha256=image_sha256,
                role=role,
                row_index=index,
                shard_ordinal=0,
            )
            for index in range(count)
        ),
        entries_sha256=("c" if role == "calibration" else "d") * 64,
        manifest_sha256="e" * 64,
        role=role,
    )


def make_document(root: Path, *, role: str = "calibration", row_index: int = 0):
    source = root / f"{role}-{row_index}.png"
    source.write_bytes(b"synthetic-image")
    image_sha256 = benchmark_core.sha256_file(source)
    word = cord.CordWord(0, 0, row_index, "A", polygon(0, 0, 20, 10))
    row = cord.CordPhysicalRow(row_index, "A", word.polygon, (word,))
    line = cord.CordLine(0, "A", word.polygon)
    return cord.CordDocument(
        row_index=row_index,
        image_id=row_index,
        relative_path=source.name,
        path=source,
        length=source.stat().st_size,
        sha256=image_sha256,
        annotation_sha256="b" * 64,
        lines=(line,),
        dontcare_polygons=(),
        repeating_symbol_polygons=(),
        clipped_valid_lines=0,
        clipped_dontcare_regions=0,
        clipped_repeating_symbol_regions=0,
        words=(word,),
        rows=(row,),
        roi_polygon=polygon(0, 0, 100, 100),
    )


def make_corpus(root: Path, document: cord.CordDocument, *, role: str) -> cord.ExtractedCorpus:
    root.mkdir(parents=True, exist_ok=True)
    target = root / document.relative_path
    if target != document.path:
        target.write_bytes(document.path.read_bytes())
        document = cord.CordDocument(**{**document.__dict__, "path": target})
    manifest = root / f"{role}-manifest.jsonl"
    worker = root / f"{role}-worker.jsonl"
    inventory = root / f"{role}-inventory.txt"
    manifest_row = {
        "schemaVersion": acceptance.SCORING_CORPUS_MANIFEST_SCHEMA_VERSION,
        "role": role,
        "rowIndex": document.row_index,
        "imageId": document.image_id,
        "path": document.relative_path,
        "length": document.length,
        "sha256": document.sha256,
        "annotationSha256": document.annotation_sha256,
        "groundTruthLines": len(document.lines),
        "groundTruthWords": len(document.words),
        "groundTruthPhysicalRows": len(document.rows),
        "clippedValidLines": document.clipped_valid_lines,
        "clippedDontcareRegions": document.clipped_dontcare_regions,
        "clippedRepeatingSymbolRegions": document.clipped_repeating_symbol_regions,
        "annotationBoundaryClips": [
            cord._annotation_boundary_clip_record(clip)
            for clip in document.annotation_boundary_clips
        ],
    }
    worker_row = {
        "schemaVersion": cord.SCHEMA_VERSION,
        "path": str(document.path),
        "length": document.length,
        "sha256": document.sha256,
    }
    manifest.write_bytes(acceptance._canonical_bytes(manifest_row))
    worker.write_bytes(acceptance._canonical_bytes(worker_row))
    inventory.write_bytes(f"{document.path}\n".encode())
    return cord.ExtractedCorpus(
        documents=(document,),
        corpus_manifest=manifest,
        worker_manifest=worker,
        inventory=inventory,
        corpus_manifest_sha256=benchmark_core.sha256_file(manifest),
        worker_manifest_sha256=benchmark_core.sha256_file(worker),
        selection_sha256="f" * 64,
        split="train",
    )


def make_inputs(
    root: Path, document: cord.CordDocument, *, role: str = "confirmatory"
) -> acceptance.PreparedRoleInputs:
    root.mkdir(parents=True, exist_ok=True)
    target = root / document.relative_path
    if target != document.path:
        target.write_bytes(document.path.read_bytes())
    image = acceptance.PreparedImage(
        row_index=document.row_index,
        relative_path=document.relative_path,
        path=target,
        length=document.length,
        sha256=document.sha256,
    )
    manifest = root / "confirmatory-input.jsonl"
    worker = root / "confirmatory-worker.jsonl"
    inventory = root / "confirmatory-inventory.txt"
    manifest.write_bytes(
        acceptance._canonical_bytes(
            {
                "schemaVersion": 1,
                "role": role,
                "rowIndex": image.row_index,
                "path": image.relative_path,
                "length": image.length,
                "sha256": image.sha256,
            }
        )
    )
    worker.write_bytes(
        acceptance._canonical_bytes(
            {
                "schemaVersion": cord.SCHEMA_VERSION,
                "path": str(image.path),
                "length": image.length,
                "sha256": image.sha256,
            }
        )
    )
    inventory.write_bytes(f"{image.path}\n".encode())
    return acceptance.PreparedRoleInputs(
        images=(image,),
        input_manifest=manifest,
        worker_manifest=worker,
        inventory=inventory,
        input_manifest_sha256=benchmark_core.sha256_file(manifest),
        worker_manifest_sha256=benchmark_core.sha256_file(worker),
        selection_sha256="1" * 64,
    )


def make_metrics(document: cord.CordDocument) -> dict:
    prediction = cord.Prediction("A", document.words[0].polygon, 1.0, "exact")
    return cord.aggregate_metrics((cord.score_document(document, (prediction,)),))


def make_runtime_profile(profile_name: str, executable_sha256: str) -> dict:
    file_sha256 = executable_sha256
    files = [{"path": "python.exe", "bytes": 7, "sha256": file_sha256}]
    available = ["CPUExecutionProvider"]
    required = [] if profile_name == "benchmark" else ["CPUExecutionProvider"]
    if profile_name == "directml":
        available.append("DmlExecutionProvider")
        required.append("DmlExecutionProvider")
    directml = {
        "required": False,
        "status": "not-required",
        "systemDlls": [],
        "adapters": [],
    }
    if profile_name == "directml":
        directml = {
            "required": True,
            "status": "available",
            "systemDlls": [
                {
                    "name": name,
                    "bytes": 7,
                    "sha256": character * 64,
                    "fileVersion": "1.0",
                    "productVersion": "1.0",
                }
                for name, character in (
                    ("d3d12.dll", "5"),
                    ("directml.dll", "6"),
                    ("dxgi.dll", "7"),
                )
            ],
            "adapters": [
                {
                    "name": "Synthetic adapter",
                    "adapterCompatibility": "Synthetic vendor",
                    "vendorId": "1234",
                    "deviceId": "5678",
                    "subsystemId": "12345678",
                    "driverVersion": "1.0",
                    "driverDateUtc": "2026-08-05T00:00:00.0000000Z",
                    "infFilename": "synthetic.inf",
                    "status": "OK",
                    "pnpDeviceIdSha256": "8" * 64,
                }
            ],
        }
    value = {
        "schemaVersion": 2,
        "requestedProvider": profile_name,
        "pythonVersion": "3.14.5",
        "implementation": "CPython",
        "system": "Windows",
        "release": "11",
        "machine": "AMD64",
        "packages": {},
        "executableSha256": executable_sha256,
        "executableName": "python.exe",
        "executable": {
            "rootId": "runtime-root-0",
            "path": "python.exe",
            "bytes": 7,
            "sha256": executable_sha256,
        },
        "providerAvailability": {
            "available": available,
            "required": required,
            "requirementsSatisfied": True,
        },
        "offlineEnvironment": dict(benchmark_core.OFFLINE_ENVIRONMENT),
        "environmentPolicy": benchmark_core.runtime_environment_policy(),
        "runtimeRoots": [
            {
                "rootId": "runtime-root-0",
                "roles": ["basePrefix", "executable", "prefix", "sysPath-000"],
                "schemaVersion": 1,
                "digestSha256": policy.sha256_canonical(files),
                "fileCount": 1,
                "totalBytes": 7,
                "files": files,
            }
        ],
        "loadPaths": [
            {
                "ordinal": 0,
                "rootId": "runtime-root-0",
                "path": ".",
                "kind": "directory",
            }
        ],
        "windowsDirectml": directml,
    }
    value["inventorySha256"] = policy.sha256_canonical(value)
    return value


def make_benchmark_run(
    provider: str,
    metrics: dict,
    frozen: acceptance.FrozenInputs,
    determinism_corpus: cord.ExtractedCorpus,
) -> dict:
    resolved = "hybrid-directml-cpu" if provider == "hybrid" else provider
    runtime = frozen.runtimes["cpu" if provider == "cpu" else "directml"]
    repetitions = [
        {
            "canonicalEvidenceSha256": "6" * 64,
            "criticalEvidenceSha256": "7" * 64,
            "metricsSha256": "8" * 64,
            "perDocumentMetricsSha256": "9" * 64,
            "qualityGatePassed": None,
            "rawOutputHashes": {"pairSha256": "5" * 64},
        }
        for _ in range(acceptance.DETERMINISM_REPETITIONS)
    ]
    return {
        "requestedProvider": provider,
        "resolvedProvider": resolved,
        "requestedThreads": 0,
        "resolvedThreadCounts": {provider: 1},
        "stableResolvedThreadCounts": True,
        "resolvedWorkerCounts": {provider: 1},
        "stableResolvedWorkerCounts": True,
        "workerSha256": frozen.worker_sha256,
        "runtimeSha256": runtime["executableSha256"],
        "qualityRows": 1,
        "determinismRows": acceptance.DETERMINISM_DOCUMENTS,
        "determinismRepetitions": acceptance.DETERMINISM_REPETITIONS,
        "provenancePassed": True,
        "byteDeterminismEvaluated": True,
        "byteDeterministic": True,
        "canonicalEvidenceDeterministic": True,
        "criticalEvidenceDeterministic": True,
        "metricsDeterministic": True,
        "stableResolvedProvider": True,
        "stableRuntime": True,
        "canonicalEvidenceSha256": "6" * 64,
        "criticalEvidenceSha256": "7" * 64,
        "_confidenceByCriticalRecord": {},
        "metricsSha256": "8" * 64,
        "qualityGatePassed": None,
        "executionProviderCountsStable": True,
        "executionProviderRecordCounts": {resolved: 1},
        "hybridLaneRecordCoverage": {
            "bothLanesProducedRecords": provider == "hybrid",
            "cpuLaneRecords": 1,
            "nonCpuLaneRecords": 1 if provider == "hybrid" else 0,
        },
        "throughputComparable": True,
        "metrics": metrics,
        "qualityRun": {"perDocumentMetricsSha256": "9" * 64},
        "determinism": {
            "selectionSha256": determinism_corpus.selection_sha256,
            "rowIndices": [item.row_index for item in determinism_corpus.documents],
            "runs": repetitions,
        },
    }


def make_ledger_fixture(root: Path):
    identity = {
        "corpus": {
            "confirmatoryInputManifestSha256": "1" * 64,
            "confirmatoryWorkerManifestSha256": "2" * 64,
        }
    }
    role = policy.ValidatedRoleSelection(
        entries=(),
        entries_sha256=policy.SELECTION_ENTRIES_SHA256["confirmatory"],
        manifest_sha256=policy.SELECTION_MANIFEST_SHA256,
        role="confirmatory",
    )
    report = acceptance._metrics_report(
        role=role,
        identity=identity,
        metrics={},
        integrity_passed=True,
        acceptance_passed=True,
        evidence={
            "artifacts": {"confirmatoryCorpusManifest": {"sha256": "5" * 64}},
            "policy": {
                "calibrationReportSha256": "3" * 64,
                "policySha256": "4" * 64,
            },
        },
    )
    placeholder = root / "placeholder"
    context = acceptance.CalibrationContext(
        report={},
        report_sha256="3" * 64,
        metrics={},
        identity=identity,
        calibration_corpus_manifest=placeholder,
        calibration_worker_manifest=placeholder,
        calibration_inventory=placeholder,
        confirmatory_input_manifest=placeholder,
        confirmatory_worker_manifest=placeholder,
        confirmatory_inventory=placeholder,
        calibration_corpus_manifest_sha256="0" * 64,
        calibration_worker_manifest_sha256="0" * 64,
        confirmatory_input_manifest_sha256="1" * 64,
        confirmatory_worker_manifest_sha256="2" * 64,
        runtime_inventory_paths={},
        runtime_inventory_sha256={},
    )
    return report, context


class OcrAcceptanceWrapperTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def fake_frozen(self, selections: dict) -> acceptance.FrozenInputs:
        worker = self.root / "worker.py"
        model = self.root / "model.json"
        selection = self.root / "selection.json"
        cpu = self.root / "cpu.exe"
        dml = self.root / "dml.exe"
        for path in (worker, model, selection, cpu, dml):
            path.write_bytes(path.name.encode("utf-8"))
        return acceptance.FrozenInputs(
            shards=(),
            selections=selections,
            selection_manifest=selection,
            worker=worker,
            model_pack=model,
            cpu_backend=benchmark_core.Backend("cpu", cpu),
            directml_backend=benchmark_core.Backend("directml", dml),
            runtimes={
                "benchmark": make_runtime_profile("benchmark", "0" * 64),
                "cpu": make_runtime_profile("cpu", "1" * 64),
                "directml": make_runtime_profile("directml", "2" * 64),
            },
            model_id="model",
            model_revision="revision",
            model_pack_sha256=benchmark_core.sha256_file(model),
            worker_sha256=benchmark_core.sha256_file(worker),
        )

    def test_train_adapter_requires_train_and_preserves_original_metadata(self) -> None:
        parsed = acceptance.parse_train_annotation(9, policy.canonical_json(annotation_fixture()))
        self.assertEqual("train", parsed.document["meta"]["split"])
        self.assertEqual("Café", parsed.words[0].text)
        with self.assertRaisesRegex(acceptance.AcceptanceError, "not from the train split"):
            acceptance.parse_train_annotation(
                9, policy.canonical_json(annotation_fixture(split="test"))
            )

    def _synthetic_parquet(self, *, selected_index: int, selected_label: str):
        import pyarrow as pa
        import pyarrow.parquet as pq

        raw_image = b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\rIHDR" + struct.pack(">II", 100, 100)
        images = pa.array(
            [{"bytes": raw_image, "path": f"row-{index}.png"} for index in range(200)],
            type=pa.struct([pa.field("bytes", pa.binary()), pa.field("path", pa.string())]),
        )
        labels = pa.array(
            [
                selected_label if index == selected_index else "must-not-be-parsed"
                for index in range(200)
            ]
        )
        shard = self.root / f"synthetic-{selected_index}.parquet"
        pq.write_table(pa.table({"image": images, "ground_truth": labels}), shard)
        return shard, raw_image

    def test_role_corpus_parses_only_selected_ground_truth(self) -> None:
        selected_index = 17
        shard, raw_image = self._synthetic_parquet(
            selected_index=selected_index, selected_label="selected"
        )
        selection = make_selection(
            "calibration",
            image_sha256=benchmark_core.sha256_bytes(raw_image),
        )
        selection = policy.ValidatedRoleSelection(
            entries=(
                policy.SelectionEntry(
                    selected_index,
                    benchmark_core.sha256_bytes(raw_image),
                    "calibration",
                    selected_index,
                    0,
                ),
            ),
            entries_sha256=selection.entries_sha256,
            manifest_sha256=selection.manifest_sha256,
            role="calibration",
        )
        word = cord.CordWord(0, 0, 1, "A", polygon(0, 0, 20, 10))
        clip = cord.AnnotationBoundaryClip(
            locator="valid_line[0].words[0]",
            clipped_area=200.0,
            horizontal_overshoot_pixels=1.0,
            horizontal_overshoot_ratio=0.01,
            original_area=210.0,
            outside_vertices=2,
            retained_area_ratio=200.0 / 210.0,
            sides=("left",),
            vertical_overshoot_pixels=0.0,
            vertical_overshoot_ratio=0.0,
        )
        parsed = cord.ParsedCordAnnotation(
            document=annotation_fixture(),
            image_id=7,
            width=100,
            height=100,
            lines=(cord.CordLine(0, "A", word.polygon),),
            words=(word,),
            rows=(cord.CordPhysicalRow(1, "A", word.polygon, (word,)),),
            roi_polygon=polygon(0, 0, 100, 100),
            dontcare_polygons=(),
            repeating_symbol_polygons=(),
            clipped_valid_lines=1,
            clipped_dontcare_regions=0,
            clipped_repeating_symbol_regions=0,
            annotation_boundary_clips=(clip,),
        )
        with (
            patch.object(policy, "validate_role_selection", return_value=[]),
            patch.object(acceptance, "parse_train_annotation", return_value=parsed) as parser,
        ):
            corpus = acceptance.extract_role_corpus((shard,), selection, self.root / "role-corpus")
        parser.assert_called_once_with(selected_index, "selected")
        self.assertEqual((selected_index,), tuple(item.row_index for item in corpus.documents))
        manifest = json.loads(corpus.corpus_manifest.read_text(encoding="utf-8"))
        self.assertEqual(
            acceptance.SCORING_CORPUS_MANIFEST_SCHEMA_VERSION, manifest["schemaVersion"]
        )
        self.assertEqual(1, manifest["clippedValidLines"])
        self.assertEqual(
            "valid_line[0].words[0]", manifest["annotationBoundaryClips"][0]["locator"]
        )

    def test_blind_input_extraction_never_reads_ground_truth_column(self) -> None:
        import pyarrow.parquet as pq

        selected_index = 23
        shard, raw_image = self._synthetic_parquet(
            selected_index=selected_index,
            selected_label="CONFIRMATORY SECRET LABEL",
        )
        selection = policy.ValidatedRoleSelection(
            entries=(
                policy.SelectionEntry(
                    selected_index,
                    benchmark_core.sha256_bytes(raw_image),
                    "confirmatory",
                    selected_index,
                    0,
                ),
            ),
            entries_sha256="d" * 64,
            manifest_sha256="e" * 64,
            role="confirmatory",
        )
        real_parquet_file = pq.ParquetFile
        columns_seen = []

        class RecordingParquet:
            def __init__(self, path):
                self.inner = real_parquet_file(path)
                self.metadata = self.inner.metadata
                self.schema_arrow = self.inner.schema_arrow

            def iter_batches(self, **kwargs):
                columns_seen.append(kwargs.get("columns"))
                return self.inner.iter_batches(**kwargs)

        with (
            patch.object(policy, "validate_role_selection", return_value=[]),
            patch.object(pq, "ParquetFile", RecordingParquet),
            patch.object(acceptance, "parse_train_annotation") as parser,
        ):
            inputs = acceptance.extract_role_inputs((shard,), selection, self.root / "blind-inputs")
        self.assertEqual([["image"]], columns_seen)
        parser.assert_not_called()
        self.assertNotIn("SECRET", inputs.input_manifest.read_text(encoding="utf-8"))

    def test_all_five_micro_metrics_recompute_from_per_document_counts(self) -> None:
        metrics = make_metrics(make_document(self.root))
        result = acceptance.validate_micro_recomputation(metrics)
        self.assertTrue(result["allFiveMicroMetricsRecomputable"])
        self.assertIsNone(result["missingCountGap"])
        self.assertEqual(set(acceptance.MICRO_METRIC_PATHS), set(result["metrics"]))
        tampered = copy.deepcopy(metrics)
        tampered["micro"]["tokenF1"] = 0.0
        with self.assertRaisesRegex(acceptance.AcceptanceError, "exactTokenF1"):
            acceptance.validate_micro_recomputation(tampered)

    def test_backend_adapter_freezes_threads_thresholds_and_repetitions(self) -> None:
        frozen = self.fake_frozen({})
        corpus = make_corpus(
            self.root / "corpus", make_document(self.root, row_index=4), role="calibration"
        )
        with patch.object(cord, "benchmark_backend", return_value={"ok": True}) as runner:
            acceptance._benchmark_backend(
                frozen,
                corpus,
                corpus,
                frozen.cpu_backend,
                self.root / "result",
                10.0,
            )
        kwargs = runner.call_args.kwargs
        self.assertEqual(0, kwargs["threads"])
        self.assertEqual(acceptance.DETERMINISM_REPETITIONS, kwargs["determinism_repetitions"])
        self.assertEqual(acceptance.NO_THRESHOLD_OVERRIDES, kwargs["thresholds"])
        self.assertTrue(all(value is None for value in kwargs["thresholds"].values()))

    def test_determinism_checks_bind_rows_repetitions_and_all_hash_layers(self) -> None:
        frozen = self.fake_frozen({})
        document = make_document(self.root, row_index=4)
        corpus = make_corpus(self.root / "det", document, role="calibration")
        run = make_benchmark_run("cpu", make_metrics(document), frozen, corpus)
        self.assertTrue(all(acceptance._determinism_checks(run, corpus).values()))
        run["determinism"]["runs"][1]["perDocumentMetricsSha256"] = "a" * 64
        self.assertFalse(
            acceptance._determinism_checks(run, corpus)["perDocumentMetricsDeterministic"]
        )

    def test_direct_shard_verifier_rejects_same_size_mutation(self) -> None:
        first = self.root / "train-a.parquet"
        second = self.root / "train-b.parquet"
        first.write_bytes(b"aaaa")
        second.write_bytes(b"bbbb")
        frozen = (
            (first.name, 4, benchmark_core.sha256_file(first)),
            (second.name, 4, benchmark_core.sha256_file(second)),
        )
        with patch.object(policy, "TRAIN_SHARDS", frozen):
            self.assertEqual(
                (first.resolve(), second.resolve()),
                acceptance.verify_train_shards((first, second)),
            )
            second.write_bytes(b"bbbc")
            with self.assertRaisesRegex(acceptance.AcceptanceError, "changed"):
                acceptance.verify_train_shards((first, second))

    def test_live_identity_uses_exact_current_source_hashes(self) -> None:
        frozen = self.fake_frozen({})
        sources = acceptance._source_hashes()
        self.assertEqual(policy.SCORING_CONSTANTS_SHA256, sources["scoringConstantsSha256"])
        with (
            patch.object(policy, "validate_identity", side_effect=lambda value: value),
            patch.object(cord, "_shapely_runtime", return_value=(None, None, "2.1.2", "3.13")),
            patch.object(cord, "_distribution_version", return_value="25.0.0"),
        ):
            identity, _ = acceptance.build_live_identity(
                frozen,
                calibration_corpus_manifest_sha256="1" * 64,
                calibration_worker_manifest_sha256="2" * 64,
                confirmatory_input_manifest_sha256="3" * 64,
                confirmatory_worker_manifest_sha256="4" * 64,
            )
        benchmark = identity["benchmark"]
        self.assertEqual(
            {
                "genericBenchmarkSha256",
                "policyModuleSha256",
                "protocol",
                "schemaVersion",
                "scorerSha256",
                "scoringConstantsSha256",
                "scriptSha256",
            },
            set(benchmark),
        )
        self.assertEqual(
            benchmark_core.sha256_file(Path(acceptance.__file__)), benchmark["scriptSha256"]
        )
        self.assertEqual({"benchmark", "cpu", "directml"}, set(identity["runtimeProfiles"]))

    def test_runtime_inventories_are_canonical_and_backend_refs_are_compact(self) -> None:
        frozen = self.fake_frozen({})
        paths = acceptance._persist_runtime_inventories(
            frozen.runtimes, self.root / "runtime-inventories"
        )
        snapshot = acceptance._runtime_inventory_snapshot(paths, frozen.runtimes)
        self.assertEqual({"benchmark", "cpu", "directml"}, set(snapshot))
        for name, path in paths.items():
            raw = path.read_bytes()
            self.assertEqual(acceptance._canonical_bytes(frozen.runtimes[name]), raw)
            self.assertEqual(snapshot[name]["artifactSha256"], hashlib.sha256(raw).hexdigest())
            self.assertEqual(
                frozen.runtimes[name]["inventorySha256"],
                snapshot[name]["inventorySha256"],
            )
        reference = acceptance._runtime_reference(frozen.runtimes["cpu"])
        self.assertEqual(
            {
                "executableSha256",
                "inventorySha256",
                "loadPathsSha256",
                "requestedProvider",
                "runtimeRootsSha256",
                "schemaVersion",
            },
            set(reference),
        )
        self.assertNotIn("files", policy.canonical_json(reference))

    def test_runtime_inventory_recomputes_each_root_digest(self) -> None:
        value = make_runtime_profile("cpu", "1" * 64)
        value["runtimeRoots"][0]["files"][0]["bytes"] = 8
        value["runtimeRoots"][0]["totalBytes"] = 8
        value.pop("inventorySha256")
        value["inventorySha256"] = policy.sha256_canonical(value)
        with self.assertRaisesRegex(acceptance.AcceptanceError, "root 0 digest"):
            acceptance._validate_runtime_inventory("cpu", value)

    def test_runtime_inventory_accepts_a_rooted_missing_load_path(self) -> None:
        value = make_runtime_profile("cpu", "1" * 64)
        value["loadPaths"].append(
            {
                "ordinal": 1,
                "rootId": "runtime-root-0",
                "path": "missing-packages",
                "kind": "missing",
            }
        )
        value.pop("inventorySha256")
        value["inventorySha256"] = policy.sha256_canonical(value)
        acceptance._validate_runtime_inventory("cpu", value)

    def test_runtime_snapshot_rejects_live_or_persisted_mutation(self) -> None:
        frozen = self.fake_frozen({})
        paths = acceptance._persist_runtime_inventories(
            frozen.runtimes, self.root / "runtime-inventories"
        )
        changed = copy.deepcopy(frozen.runtimes)
        changed["cpu"]["release"] = "changed"
        changed["cpu"].pop("inventorySha256")
        changed["cpu"]["inventorySha256"] = policy.sha256_canonical(changed["cpu"])
        with self.assertRaisesRegex(acceptance.AcceptanceError, "live cpu runtime"):
            acceptance._runtime_inventory_snapshot(paths, changed)
        paths["cpu"].write_bytes(acceptance._canonical_bytes(changed["cpu"]))
        with self.assertRaisesRegex(acceptance.AcceptanceError, "live cpu runtime"):
            acceptance._runtime_inventory_snapshot(paths, frozen.runtimes)

    def test_runtime_mutation_makes_postrun_identity_reverification_fail(self) -> None:
        frozen = self.fake_frozen({})
        paths = acceptance._persist_runtime_inventories(
            frozen.runtimes, self.root / "runtime-inventories"
        )
        runtime_snapshot = acceptance._runtime_inventory_snapshot(paths, frozen.runtimes)
        changed = copy.deepcopy(frozen)
        changed.runtimes["cpu"]["release"] = "changed"
        changed.runtimes["cpu"].pop("inventorySha256")
        changed.runtimes["cpu"]["inventorySha256"] = policy.sha256_canonical(
            changed.runtimes["cpu"]
        )
        with (
            patch.object(acceptance, "_prepared_snapshot", return_value={}),
            patch.object(acceptance, "verify_frozen_inputs", return_value=changed),
        ):
            passed, detail = acceptance._reverify_identity(
                argparse.Namespace(),
                {},
                {},
                prepared_artifacts={},
                snapshot_before={},
                runtime_inventory_paths=paths,
                runtime_snapshot_before=runtime_snapshot,
            )
        self.assertFalse(passed)
        self.assertEqual("runtime-identity", detail["error"]["stage"])

    def test_report_artifacts_and_source_labels_never_use_absolute_paths(self) -> None:
        root = self.root / "calibration"
        artifact = root / "prepared" / "manifest.jsonl"
        artifact.parent.mkdir(parents=True)
        artifact.write_bytes(b"{}\n")
        reference = acceptance._artifact(artifact, evidence_root=root)
        self.assertEqual("prepared/manifest.jsonl", reference["path"])
        self.assertFalse(Path(reference["path"]).is_absolute())
        for source in acceptance._source_hashes()["files"].values():
            self.assertFalse(Path(source["path"]).is_absolute())
            self.assertNotIn("..", Path(source["path"]).parts)
        with self.assertRaises(acceptance.AcceptanceError):
            acceptance._artifact(self.root / "outside", evidence_root=root)
        for escaped in ("../outside", "C:/outside", r"C:\outside"):
            with self.subTest(escaped=escaped), self.assertRaises(acceptance.AcceptanceError):
                acceptance._resolve_evidence_artifact(root, escaped)

    def test_trusted_loader_executes_and_binds_source_bytes_not_cached_bytecode(self) -> None:
        source = self.root / "frozen_module.py"
        cache = Path(importlib.util.cache_from_source(str(source)))
        source.write_text("value = 'poison'\n", encoding="utf-8")
        py_compile.compile(
            str(source),
            cfile=str(cache),
            doraise=True,
            invalidation_mode=py_compile.PycInvalidationMode.UNCHECKED_HASH,
        )
        source_bytes = b"value = 'source'\n"
        source.write_bytes(source_bytes)
        with patch.object(acceptance, "_SOURCE_ROOT", self.root):
            module = acceptance._trusted_source_module("synthetic_frozen_module", source.name)
        try:
            self.assertEqual("source", module.value)
            self.assertIsNone(module.__cached__)
            self.assertEqual(
                hashlib.sha256(source_bytes).hexdigest(),
                acceptance._LOADED_SOURCE_HASHES["synthetic_frozen_module"],
            )
        finally:
            sys.modules.pop("synthetic_frozen_module", None)
            acceptance._LOADED_SOURCE_HASHES.pop("synthetic_frozen_module", None)

    def test_source_identity_rejects_loaded_bytes_missing_from_disk(self) -> None:
        with (
            patch.dict(
                acceptance._LOADED_SOURCE_HASHES,
                {"benchmark_ocr": "0" * 64},
                clear=False,
            ),
            self.assertRaisesRegex(acceptance.AcceptanceError, "on-disk bytes"),
        ):
            acceptance._source_hashes()

    def test_trusted_loader_binds_bytes_even_if_source_changes_during_exec(self) -> None:
        source = self.root / "swapping_module.py"
        original = (
            b"from pathlib import Path\n"
            b"Path(__file__).write_text(\"value = 'disk'\\n\", encoding='utf-8')\n"
            b"value = 'loaded'\n"
        )
        source.write_bytes(original)
        with patch.object(acceptance, "_SOURCE_ROOT", self.root):
            module = acceptance._trusted_source_module("swapping_module", source.name)
        try:
            self.assertEqual("loaded", module.value)
            self.assertIn("disk", source.read_text(encoding="utf-8"))
            self.assertEqual(
                hashlib.sha256(original).hexdigest(),
                acceptance._LOADED_SOURCE_HASHES["swapping_module"],
            )
        finally:
            sys.modules.pop("swapping_module", None)
            acceptance._LOADED_SOURCE_HASHES.pop("swapping_module", None)

    def test_real_entrypoint_requires_isolation_and_ignores_poison_paths(self) -> None:
        poison = self.root / "poison"
        poison.mkdir()
        marker = self.root / "poison-loaded"
        (poison / "benchmark_ocr.py").write_text(
            f"from pathlib import Path\nPath({str(marker)!r}).write_text('loaded')\n",
            encoding="utf-8",
        )
        (self.root / "json.py").write_text(
            f"from pathlib import Path\nPath({str(marker)!r}).write_text('json')\n",
            encoding="utf-8",
        )
        environment = os.environ.copy()
        environment["PYTHONPATH"] = str(poison)
        environment["PATH"] = str(poison)
        wrapper = str(Path(acceptance.__file__).resolve())
        plain = subprocess.run(
            [sys.executable, "-B", wrapper, "--help"],
            cwd=self.root,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(1, plain.returncode)
        self.assertIn("-I -B", plain.stderr)
        self.assertFalse(marker.exists())
        isolated = subprocess.run(
            [sys.executable, "-I", "-B", wrapper, "--help"],
            cwd=self.root,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(0, isolated.returncode, isolated.stderr)
        self.assertFalse(marker.exists())
        guarded = subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                wrapper,
                "verify-completed",
                "--report",
                str(self.root / "missing-confirmatory-report.json"),
            ],
            cwd=self.root,
            env=environment,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(1, guarded.returncode)
        self.assertNotIn("requires an isolated -I -B", guarded.stderr)
        self.assertIn("confirmatory report is unavailable", guarded.stderr)
        self.assertFalse(marker.exists())
        if os.name == "nt":
            for variable, displayed_name in (("WINDIR", "WINDIR"), ("sYsTeMrOoT", "SystemRoot")):
                poisoned_windows = {
                    name: value
                    for name, value in environment.items()
                    if name.casefold() != variable.casefold()
                }
                poisoned_windows[variable] = str(poison)
                rejected = subprocess.run(
                    [sys.executable, "-I", "-B", wrapper, "--help"],
                    cwd=self.root,
                    env=poisoned_windows,
                    capture_output=True,
                    text=True,
                    check=False,
                )
                self.assertEqual(1, rejected.returncode)
                self.assertIn(f"inherited {displayed_name}", rejected.stderr)
                self.assertFalse(marker.exists())

    def test_prepared_snapshot_detects_image_mutation_during_run(self) -> None:
        document = make_document(self.root)
        role = make_selection("calibration", image_sha256=document.sha256)
        corpus = make_corpus(self.root / "prepared", document, role="calibration")
        artifacts = {
            "calibration": (
                corpus.corpus_manifest,
                corpus.worker_manifest,
                corpus.inventory,
                role,
            ),
            "confirmatoryInput": (
                corpus.corpus_manifest,
                corpus.worker_manifest,
                corpus.inventory,
                role,
            ),
        }
        before = acceptance._prepared_snapshot(artifacts)
        corpus.documents[0].path.write_bytes(b"mutated-image!")
        passed, detail = acceptance._reverify_identity(
            argparse.Namespace(),
            {},
            {},
            prepared_artifacts=artifacts,
            snapshot_before=before,
            runtime_inventory_paths={},
            runtime_snapshot_before={},
        )
        self.assertFalse(passed)
        self.assertFalse(detail["passed"])

    def test_calibration_uses_blind_confirmatory_inputs_and_repeated_cpu(self) -> None:
        calibration_role = make_selection("calibration")
        confirmatory_role = make_selection("confirmatory")
        frozen = self.fake_frozen(
            {"calibration": calibration_role, "confirmatory": confirmatory_role}
        )
        document = make_document(self.root)
        corpus = make_corpus(self.root / "cal-corpus", document, role="calibration")
        inputs = make_inputs(
            self.root / "confirm-input", make_document(self.root, role="confirmatory")
        )
        run = make_benchmark_run("cpu", make_metrics(document), frozen, corpus)
        args = argparse.Namespace(
            work_directory=self.root / "work",
            timeout_seconds=10.0,
            policy_output=self.root / "policy.json",
        )
        failed_floor = {
            spec.name: {"passed": False, "boundary": 1.0, "value": 0.0}
            for spec in policy.METRIC_SPECS
        }
        with (
            patch.object(acceptance, "extract_role_corpus", return_value=corpus) as scoring,
            patch.object(acceptance, "extract_role_inputs", return_value=inputs) as blind,
            patch.object(acceptance, "verify_train_shards"),
            patch.object(policy, "load_role_selections"),
            patch.object(acceptance, "build_live_identity", return_value=({"id": 1}, {"src": 1})),
            patch.object(
                acceptance,
                "_prepared_snapshot",
                return_value={
                    "calibration": {
                        "manifestSha256": "1" * 64,
                        "workerManifestSha256": "2" * 64,
                    },
                    "confirmatoryInput": {
                        "manifestSha256": "3" * 64,
                        "workerManifestSha256": "4" * 64,
                    },
                },
            ),
            patch.object(acceptance, "_create_determinism_corpus", return_value=corpus),
            patch.object(acceptance, "_benchmark_backend", return_value=run) as benchmark,
            patch.object(
                acceptance,
                "_artifact",
                return_value={"bytes": 1, "path": "artifact", "sha256": "a" * 64},
            ),
            patch.object(policy, "extract_measurements", return_value={"m": 0.0}),
            patch.object(policy, "absolute_floor_checks", return_value=failed_floor),
            patch.object(acceptance, "_reverify_identity", return_value=(True, {"passed": True})),
        ):
            report, policy_value = acceptance.execute_calibration(args, frozen)
        scoring.assert_called_once()
        blind.assert_called_once()
        benchmark.assert_called_once()
        self.assertIsNone(policy_value)
        self.assertFalse(report["acceptancePassed"])
        blind_boundary = report["evidence"]["preparedBeforeCalibration"]
        self.assertFalse(blind_boundary["confirmatoryGroundTruthConvertedOrParsed"])
        self.assertTrue(blind_boundary["confirmatoryGroundTruthMayBeMaterializedByArrow"])
        self.assertEqual(
            {
                "acceptancePassed",
                "documents",
                "evaluationRole",
                "evidence",
                "identity",
                "integrityPassed",
                "metrics",
                "metricsSha256",
                "runSucceeded",
                "schemaVersion",
                "selectionSha256",
            },
            set(report),
        )

    def test_confirmatory_claim_precedes_labels_and_runs_exact_matrix(self) -> None:
        calibration_role = make_selection("calibration")
        confirmatory_document = make_document(self.root, role="confirmatory")
        confirmatory_role = make_selection(
            "confirmatory", image_sha256=confirmatory_document.sha256
        )
        frozen = self.fake_frozen(
            {"calibration": calibration_role, "confirmatory": confirmatory_role}
        )
        calibration_corpus = make_corpus(
            self.root / "calibration-prepared",
            make_document(self.root, role="calibration"),
            role="calibration",
        )
        inputs = make_inputs(self.root / "confirmatory-prepared", confirmatory_document)
        scoring_corpus = make_corpus(
            self.root / "scoring", confirmatory_document, role="confirmatory"
        )
        metrics = make_metrics(scoring_corpus.documents[0])
        runtime_paths = acceptance._persist_runtime_inventories(
            frozen.runtimes, self.root / "runtime-inventories"
        )
        runtime_snapshot = acceptance._runtime_inventory_snapshot(runtime_paths, frozen.runtimes)
        context = acceptance.CalibrationContext(
            report={
                "evidence": {
                    "identitySources": {"src": 1},
                    "runtimeProfiles": runtime_snapshot,
                }
            },
            report_sha256="b" * 64,
            metrics={},
            identity={"id": 1},
            calibration_corpus_manifest=calibration_corpus.corpus_manifest,
            calibration_worker_manifest=calibration_corpus.worker_manifest,
            calibration_inventory=calibration_corpus.inventory,
            confirmatory_input_manifest=inputs.input_manifest,
            confirmatory_worker_manifest=inputs.worker_manifest,
            confirmatory_inventory=inputs.inventory,
            calibration_corpus_manifest_sha256=calibration_corpus.corpus_manifest_sha256,
            calibration_worker_manifest_sha256=calibration_corpus.worker_manifest_sha256,
            confirmatory_input_manifest_sha256=inputs.input_manifest_sha256,
            confirmatory_worker_manifest_sha256=inputs.worker_manifest_sha256,
            runtime_inventory_paths=runtime_paths,
            runtime_inventory_sha256={
                name: value["artifactSha256"] for name, value in runtime_snapshot.items()
            },
        )
        args = argparse.Namespace(
            work_directory=self.root / "work",
            timeout_seconds=10.0,
            policy=self.root / "policy.json",
            calibration_report=self.root / "calibration.json",
            output=self.root / "confirmatory.json",
        )
        events = []
        providers = []

        def start(*unused, **kwargs):
            events.append("claim")
            return acceptance.AttemptClaim(
                path=self.root / "attempt.json", value={}, sha256="f" * 64
            )

        def extract(*unused, **kwargs):
            events.append("labels")
            return scoring_corpus

        def benchmark(_frozen, _corpus, det, backend, _output, _timeout):
            providers.append(backend.requested_provider)
            return make_benchmark_run(
                backend.requested_provider, copy.deepcopy(metrics), frozen, det
            )

        validated = policy.ValidatedPolicy(
            file_sha256="a" * 64, value={"thresholds": {"metric": 0.0}}
        )
        with (
            patch.object(acceptance, "build_live_identity", return_value=({"id": 1}, {"src": 1})),
            patch.object(policy, "validate_policy", return_value=validated),
            patch.object(acceptance, "_recheck_policy_bytes"),
            patch.object(acceptance, "_start_attempt", side_effect=start),
            patch.object(acceptance, "extract_role_corpus", side_effect=extract),
            patch.object(acceptance, "verify_train_shards"),
            patch.object(policy, "load_role_selections"),
            patch.object(acceptance, "_create_determinism_corpus", return_value=scoring_corpus),
            patch.object(
                acceptance,
                "_prepared_snapshot",
                return_value={
                    "calibration": {
                        "manifestSha256": "1" * 64,
                        "workerManifestSha256": "2" * 64,
                    },
                    "confirmatoryInput": {
                        "manifestSha256": "3" * 64,
                        "workerManifestSha256": "4" * 64,
                    },
                },
            ),
            patch.object(acceptance, "_benchmark_backend", side_effect=benchmark),
            patch.object(
                acceptance,
                "_artifact",
                return_value={"bytes": 1, "path": "artifact", "sha256": "a" * 64},
            ),
            patch.object(policy, "extract_measurements", return_value={"metric": 1.0}),
            patch.object(
                policy,
                "evaluate_thresholds",
                return_value={"checks": {}, "passed": True},
            ),
            patch.object(acceptance, "_reverify_identity", return_value=(True, {"passed": True})),
            patch.object(cord, "confidence_parity", return_value={"passed": True}),
            patch.object(
                policy,
                "evaluate_confirmatory",
                return_value={"passed": True, "policySha256": "a" * 64},
            ),
        ):
            report, _ = acceptance.execute_confirmatory(args, frozen, context)
        self.assertLess(events.index("claim"), events.index("labels"))
        self.assertEqual(["cpu", "directml", "hybrid"], providers)
        self.assertTrue(report["integrityPassed"])
        self.assertTrue(report["acceptancePassed"])
        self.assertEqual(
            "hybrid-directml-cpu",
            report["evidence"]["backends"][2]["resolvedProvider"],
        )

    def test_project_global_ledger_ignores_recalibration_and_candidate_paths(self) -> None:
        document = make_document(self.root, role="confirmatory")
        inputs = make_inputs(self.root / "prepared", document)
        common = dict(
            report={},
            report_sha256="a" * 64,
            metrics={},
            identity={},
            calibration_corpus_manifest=inputs.input_manifest,
            calibration_worker_manifest=inputs.worker_manifest,
            calibration_inventory=inputs.inventory,
            confirmatory_input_manifest=inputs.input_manifest,
            confirmatory_worker_manifest=inputs.worker_manifest,
            confirmatory_inventory=inputs.inventory,
            calibration_corpus_manifest_sha256=inputs.input_manifest_sha256,
            calibration_worker_manifest_sha256=inputs.worker_manifest_sha256,
            confirmatory_input_manifest_sha256=inputs.input_manifest_sha256,
            confirmatory_worker_manifest_sha256=inputs.worker_manifest_sha256,
            runtime_inventory_paths={},
            runtime_inventory_sha256={},
        )
        original = acceptance.CalibrationContext(**common)
        copied = acceptance.CalibrationContext(**{**common, "report_sha256": "9" * 64})
        global_ledger = self.root / "project-global-attempt.json"
        with patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", global_ledger):
            first = acceptance._start_attempt(original, "b" * 64, {"candidate": "a"})
            with self.assertRaisesRegex(acceptance.AcceptanceError, "already exists"):
                acceptance._start_attempt(copied, "c" * 64, {"candidate": "b"})
        self.assertEqual(global_ledger.resolve(), first.path)
        self.assertNotIn(str(self.root), first.path.read_text(encoding="utf-8"))

    def test_policy_byte_recheck_rejects_atomic_swap(self) -> None:
        validated = policy.ValidatedPolicy(
            file_sha256="a" * 64,
            value={"schemaVersion": 1, "thresholds": {"metric": 1.0}},
        )
        with (
            patch.object(
                policy,
                "load_policy",
                return_value=(
                    {"schemaVersion": 1, "thresholds": {"metric": 0.0}},
                    "b" * 64,
                ),
            ),
            self.assertRaisesRegex(acceptance.AcceptanceError, "changed"),
        ):
            acceptance._recheck_policy_bytes(self.root / "policy.json", validated)

    def test_postclaim_evaluation_cannot_switch_to_another_valid_policy(self) -> None:
        validated = policy.ValidatedPolicy(
            file_sha256="a" * 64,
            value={"frozenAtUtc": "2026-08-05T00:00:00Z"},
        )
        with self.assertRaisesRegex(acceptance.AcceptanceError, "path-bound policy"):
            acceptance._validate_postclaim_policy_result(
                {"passed": True, "policySha256": "b" * 64},
                validated,
                quality_passed=True,
            )

    def test_two_phase_commit_finalizes_ledger_before_report(self) -> None:
        output = self.root / "report.json"
        output, marker = acceptance._new_report_marker(output)
        attempt = self.root / "attempt.json"
        report, context = make_ledger_fixture(self.root)
        with patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", attempt):
            claim = acceptance._start_attempt(context, "4" * 64, report["identity"])
        acceptance._commit_confirmatory_report(output, marker, report, claim)
        ledger = json.loads(attempt.read_text(encoding="utf-8"))
        self.assertEqual("completed", ledger["status"])
        self.assertFalse(any(key.endswith("Path") for key in ledger))
        self.assertTrue(output.is_file())
        acceptance.verify_completed_confirmation(output, attempt)

    def test_ledger_rejects_mutated_claim_and_unknown_fields(self) -> None:
        report, context = make_ledger_fixture(self.root)
        report_sha256 = benchmark_core.sha256_bytes(acceptance._canonical_bytes(report))
        for name, mutate in (
            (
                "immutable-field",
                lambda value: value.update({"policySha256": "a" * 64}),
            ),
            ("unknown-field", lambda value: value.update({"unexpected": True})),
        ):
            with self.subTest(name=name):
                attempt = self.root / f"{name}.json"
                with patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", attempt):
                    claim = acceptance._start_attempt(context, "4" * 64, report["identity"])
                value = json.loads(attempt.read_text(encoding="utf-8"))
                mutate(value)
                acceptance._atomic_replace(attempt, acceptance._canonical_bytes(value))
                with self.assertRaisesRegex(
                    acceptance.AcceptanceError, "original immutable attempt claim"
                ):
                    acceptance._finish_attempt(claim, report_sha256=report_sha256, report=report)

    def test_ledger_finalization_failure_never_publishes_accepted_report(self) -> None:
        output = self.root / "report.json"
        output, marker = acceptance._new_report_marker(output)
        report = {
            "acceptancePassed": True,
            "documents": 1,
            "evaluationRole": "confirmatory",
            "evidence": {},
            "identity": {},
            "integrityPassed": True,
            "metrics": {},
            "metricsSha256": policy.sha256_canonical({}),
            "runSucceeded": True,
            "schemaVersion": 1,
            "selectionSha256": "a" * 64,
        }
        with (
            patch.object(acceptance, "_finish_attempt", side_effect=OSError("disk")),
            self.assertRaises(OSError),
        ):
            acceptance._commit_confirmatory_report(
                output, marker, report, self.root / "attempt.json"
            )
        self.assertFalse(output.exists())
        self.assertFalse(output.with_name("report.json.staged").exists())

    def test_failure_quarantines_partial_evidence_without_metrics(self) -> None:
        work = self.root / "work"
        phase = work / "confirmatory"
        phase.mkdir(parents=True)
        attempt = self.root / "attempt.json"
        report, context = make_ledger_fixture(self.root)
        with patch.object(acceptance, "CONFIRMATORY_ATTEMPT_LEDGER", attempt):
            claim = acceptance._start_attempt(context, "4" * 64, report["identity"])
        args = argparse.Namespace(work_directory=work)
        diagnostic = {
            "kind": "annotationBoundary",
            "globalRowIndex": 222,
            "locator": "valid_line[0].words[0]",
            "predicate": "edgeOvershoot",
            "horizontalOvershootPixels": 6.0,
            "horizontalOvershootRatio": 0.06,
            "verticalOvershootPixels": 0.0,
            "verticalOvershootRatio": 0.0,
            "originalArea": 460.0,
            "clippedArea": None,
            "retainedAreaRatio": None,
            "outsideVertices": 2,
            "sides": ["left"],
        }
        error = acceptance.AcceptanceError(
            "failed",
            stage="annotation-parse",
            backend="cpu",
            diagnostic=diagnostic,
        )
        acceptance._fail_attempt(claim, error)
        marker = acceptance._quarantine_confirmatory_partials(args, claim, error)
        self.assertIsNotNone(marker)
        self.assertTrue(marker.is_file())
        ledger = json.loads(attempt.read_text(encoding="utf-8"))
        self.assertEqual("quarantined", ledger["status"])
        self.assertEqual(diagnostic, ledger["failure"]["diagnostic"])
        self.assertNotIn("metrics", ledger)

    def test_boundary_failure_diagnostic_is_safe_structured_and_row_bound(self) -> None:
        annotation = annotation_fixture()
        annotation["valid_line"][0]["words"][0]["quad"]["x1"] = -6  # type: ignore[index]
        annotation["valid_line"][0]["words"][0]["quad"]["x4"] = -6  # type: ignore[index]
        with self.assertRaises(acceptance.AcceptanceError) as raised:
            acceptance.parse_train_annotation(222, cord.canonical_json(annotation))
        diagnostic = raised.exception.diagnostic
        self.assertIsInstance(diagnostic, dict)
        self.assertEqual(222, diagnostic["globalRowIndex"])
        self.assertEqual("valid_line[0].words[0]", diagnostic["locator"])
        self.assertEqual("edgeOvershoot", diagnostic["predicate"])

        report = acceptance._failure_report(
            argparse.Namespace(phase="calibration"), raised.exception
        )
        self.assertEqual(diagnostic, report["evidence"]["failure"]["diagnostic"])
        self.assertNotIn(str(self.root), cord.canonical_json(report))

        poisoned = dict(diagnostic, localPath=str(self.root))
        self.assertIsNone(acceptance._safe_failure_diagnostic(poisoned))

    def test_cli_has_no_attempt_threads_or_threshold_overrides(self) -> None:
        base = [
            "confirmatory",
            "--parquet",
            "a.parquet",
            "--selection-manifest",
            "selection.json",
            "--worker",
            "worker.py",
            "--model-pack",
            "model.json",
            "--cpu-python",
            "cpu.exe",
            "--directml-python",
            "dml.exe",
            "--output",
            "report.json",
            "--work-directory",
            "work",
            "--calibration-report",
            "calibration.json",
            "--calibration-evidence-root",
            "work/calibration",
            "--policy",
            "policy.json",
        ]
        args = acceptance.parse_arguments(base)
        self.assertFalse(hasattr(args, "attempt_manifest"))
        verify_args = acceptance.parse_arguments(
            ["verify-completed", "--report", "confirmatory.json"]
        )
        self.assertEqual("verify-completed", verify_args.phase)
        self.assertFalse(hasattr(verify_args, "attempt_manifest"))
        for forbidden in ("--attempt-manifest", "--threads", "--min-detection-hmean"):
            with self.assertRaises(SystemExit):
                acceptance.parse_arguments([*base, forbidden, "x"])

    def test_failure_report_uses_exact_policy_loader_top_level_schema(self) -> None:
        args = argparse.Namespace(phase="calibration")
        report = acceptance._failure_report(args, acceptance.AcceptanceError("bad", stage="input"))
        self.assertEqual(
            {
                "acceptancePassed",
                "documents",
                "evaluationRole",
                "evidence",
                "identity",
                "integrityPassed",
                "metrics",
                "metricsSha256",
                "runSucceeded",
                "schemaVersion",
                "selectionSha256",
            },
            set(report),
        )
        self.assertFalse(report["runSucceeded"])
        self.assertFalse(report["integrityPassed"])
        self.assertIsNone(report["evidence"]["failure"]["diagnostic"])


if __name__ == "__main__":
    unittest.main()
