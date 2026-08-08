from __future__ import annotations

import io
import json
import sys
import tempfile
import threading
import time
import unittest
from contextlib import redirect_stderr
from dataclasses import replace
from pathlib import Path
from typing import Any
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bstrings_ocr import (  # noqa: E402
    CPU_PARALLEL_QUEUE_DEPTH_PER_WORKER,
    HYBRID_SOURCE_MAX_PENDING,
    AtomicJsonlPair,
    CpuParallelOcrRuntime,
    HybridOcrRuntime,
    OcrConfig,
    OcrError,
    RapidOcrRuntime,
    RasterScheduler,
    _validate_resolved_provider,
    load_model_pack,
    make_record,
    parse_arguments,
    provider_candidates,
    rapidocr_parameters,
    resolve_cpu_worker_layout,
    resolve_session_threads,
    run_pipeline,
    sha256_file,
)


class FakeImage:
    def __init__(self, key: str, width: int = 320, height: int = 120) -> None:
        self.key = key
        self.size = (width, height)
        self.closed = False

    def close(self) -> None:
        self.closed = True


class FakePage:
    def __init__(
        self,
        key: str,
        text: str,
        width: float = 612.0,
        height: float = 792.0,
    ) -> None:
        self.key = key
        self.text = text
        self.width = width
        self.height = height
        self.closed = False

    def close(self) -> None:
        self.closed = True


class FakeDocument:
    def __init__(self, pages: list[FakePage]) -> None:
        self.pages = pages
        self.closed = False

    def close(self) -> None:
        self.closed = True


class FakeRuntime:
    engine_version = "3.9.2"
    pdfium_version = "5.12.1"

    def __init__(self, resolved_provider: str = "cpu", requested_threads: int = 1) -> None:
        self.resolved_provider = resolved_provider
        self.requested_threads = requested_threads
        self.resolved_thread_counts = {
            resolved_provider: requested_threads or (1 if resolved_provider != "cpu" else 4)
        }
        self.resolved_worker_counts = {resolved_provider: 1}
        self.image_frames: dict[str, list[FakeImage]] = {}
        self.documents: dict[str, FakeDocument] = {}
        self.ocr_results: dict[str, Any] = {}
        self.ocr_calls: list[str] = []
        self.render_calls: list[str] = []
        self.self_test_calls = 0
        self.self_test_error: BaseException | None = None
        self.parallel_preprocessing_calls = 0
        self.closed = False

    def iter_image_frames(self, path: Path, max_pages: int, max_pixels: int) -> Any:
        frames = self.image_frames[path.name]
        if len(frames) > max_pages:
            raise OcrError("too many fake frames")
        for index, frame in enumerate(frames, start=1):
            if frame.size[0] * frame.size[1] > max_pixels:
                raise OcrError("fake pixel limit")
            yield index, frame

    def image_frame_count(self, path: Path, max_pages: int, max_pixels: int) -> int:
        frames = self.image_frames[path.name]
        if not frames or len(frames) > max_pages:
            raise OcrError("invalid fake frame count")
        if any(frame.size[0] * frame.size[1] > max_pixels for frame in frames):
            raise OcrError("fake pixel limit")
        return len(frames)

    def open_pdf(self, path: Path) -> FakeDocument:
        return self.documents[path.name]

    def pdf_page_count(self, document: FakeDocument) -> int:
        return len(document.pages)

    def get_pdf_page(self, document: FakeDocument, index: int) -> FakePage:
        return document.pages[index]

    def pdf_page_size(self, page: FakePage) -> tuple[float, float]:
        return page.width, page.height

    def extract_pdf_text(self, page: FakePage, maximum_text: int) -> str:
        if len(page.text) > maximum_text:
            raise OcrError("fake text limit")
        return page.text

    def render_pdf_page(self, page: FakePage, dpi: int, max_pixels: int) -> FakeImage:
        self.render_calls.append(page.key)
        image = FakeImage(f"render:{page.key}", 400, 500)
        if image.size[0] * image.size[1] > max_pixels:
            raise OcrError("fake pixel limit")
        return image

    def image_size(self, image: FakeImage) -> tuple[int, int]:
        return image.size

    def raster_sha256(self, image: FakeImage) -> str:
        import hashlib

        return hashlib.sha256(f"{image.key}\0{image.size[0]}\0{image.size[1]}".encode()).hexdigest()

    def ocr(self, image: FakeImage) -> Any:
        self.ocr_calls.append(image.key)
        value = self.ocr_results.get(image.key, {"txts": [], "boxes": [], "scores": []})
        if isinstance(value, BaseException):
            raise value
        if callable(value):
            return value()
        return value

    def run_inference_self_test(self) -> None:
        self.self_test_calls += 1
        if self.self_test_error is not None:
            raise self.self_test_error

    def configure_parallel_preprocessing(self) -> None:
        self.parallel_preprocessing_calls += 1

    def close(self, value: Any | None = None) -> None:
        if value is None:
            self.closed = True
            return
        close = getattr(value, "close", None)
        if close is not None:
            close()


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines()]


class OcrWorkerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.runtime_executable = self.root / "rapidocr.exe"
        self.runtime_executable.write_bytes(b"pinned rapidocr runtime")
        self.model_id = "PaddlePaddle/PP-OCRv6-medium-onnx"
        self.revision = "6132380+50c7eac"
        self.model_manifest = self._make_model_pack()
        self.inventory = self.root / "inventory.txt"
        self.input_manifest = self.root / "input-manifest.jsonl"
        self.output = self.root / "ocr-strings.jsonl"
        self.assessments = self.root / "ocr-assessments.jsonl"

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def _make_model_pack(self) -> Path:
        components = {
            "detector": ("detector.onnx", b"detector model"),
            "recognizer": ("recognizer.onnx", b"recognizer model"),
            "classifier": ("classifier.onnx", b"classifier model"),
            "dictionary": ("ppocrv6_dict.txt", b"a\nb\nc\n"),
        }
        manifest: dict[str, Any] = {
            "schemaVersion": 1,
            "modelId": self.model_id,
            "revision": self.revision,
        }
        for name, (filename, payload) in components.items():
            path = self.root / filename
            path.write_bytes(payload)
            manifest[name] = {"path": filename, "sha256": sha256_file(path)}
        path = self.root / "ocr-model-pack.json"
        path.write_text(
            json.dumps(manifest, ensure_ascii=False, separators=(",", ":"), sort_keys=True),
            encoding="utf-8",
        )
        return path

    def _config(self, *, mode: str = "auto", self_test: bool = False) -> OcrConfig:
        return OcrConfig(
            paths_from=None if self_test else self.inventory,
            output=None if self_test else self.output,
            assessments_output=None if self_test else self.assessments,
            ocr_executable=self.runtime_executable,
            engine="rapidocr",
            engine_version="3.9.2",
            model_path=self.model_manifest,
            model_id=self.model_id,
            model_revision=self.revision,
            model_sha256=sha256_file(self.model_manifest),
            mode=mode,
            provider="cpu",
            dpi=300,
            threads=1,
            max_pages=100,
            max_pixels=2_000_000,
            max_input_bytes=1024 * 1024,
            max_output_bytes=8 * 1024 * 1024,
            max_output_records=10_000,
            max_text_characters=1024 * 1024,
            pdf_text_min_characters=32,
            self_test=self_test,
            airgap=True,
        )

    def _write_inventory(self, *paths: Path) -> None:
        self.inventory.write_text(
            "".join(f"{path.resolve()}\n" for path in paths), encoding="utf-8"
        )

    def _input_manifest_row(
        self,
        path: Path,
        *,
        length: int | None = None,
        sha256: str | None = None,
    ) -> dict[str, Any]:
        return {
            "schemaVersion": 1,
            "path": str(path.resolve()),
            "length": path.stat().st_size if length is None else length,
            "sha256": sha256_file(path) if sha256 is None else sha256,
        }

    def _write_input_manifest_rows(self, *rows: dict[str, Any]) -> None:
        self.input_manifest.write_text(
            "".join(
                json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n" for row in rows
            ),
            encoding="utf-8",
        )

    def _image(self, name: str = "evidence.png") -> Path:
        path = self.root / name
        path.write_bytes(b"\x89PNG\r\n\x1a\nsynthetic test payload")
        return path

    def _pdf(self, name: str = "evidence.pdf") -> Path:
        path = self.root / name
        path.write_bytes(b"%PDF-1.7\nsynthetic test payload")
        return path

    def test_image_records_preserve_exact_identifiers_and_sort_by_geometry(self) -> None:
        source = self._image()
        self._write_inventory(source)
        runtime = FakeRuntime()
        runtime.image_frames[source.name] = [FakeImage("image:1")]
        lower_text = "https://example.test/a?id=42 analyst@example.com"
        upper_text = "6F9619FF-8B86-D011-B42D-00C04FC964FF 192.0.2.7"
        runtime.ocr_results["image:1"] = {
            "txts": [lower_text, upper_text],
            "boxes": [
                [[10, 60], [300, 60], [300, 90], [10, 90]],
                [[8, 10], [310, 10], [310, 35], [8, 35]],
            ],
            "scores": [0.91, 0.99],
        }

        stderr = io.StringIO()
        with redirect_stderr(stderr):
            stats = run_pipeline(replace(self._config(), progress_total_files=1), runtime)

        records = read_jsonl(self.output)
        self.assertEqual([upper_text, lower_text], [record["text"] for record in records])
        self.assertEqual([0.99, 0.91], [record["attributes"]["confidence"] for record in records])
        self.assertTrue(all(record["origin"]["kind"] == "ocr" for record in records))
        self.assertTrue(all(record["location"]["kind"] == "image_region" for record in records))
        self.assertTrue(all(record["attributes"]["sourceSha256"] for record in records))
        self.assertTrue(all(record["attributes"]["runtimeSha256"] for record in records))
        self.assertTrue(all(record["attributes"]["renderSha256"] for record in records))
        self.assertTrue(all(record["origin"]["provider"] == "cpu" for record in records))
        self.assertEqual(2, stats["stringRecords"])
        self.assertIn("Progress: offline OCR: 0.0% (0/1 files)", stderr.getvalue())
        self.assertIn("Progress: offline OCR: 100.0% (1/1 files)", stderr.getvalue())
        assessment = read_jsonl(self.assessments)[0]
        self.assertEqual(
            sha256_file(self.root / "ppocrv6_dict.txt"), assessment["dictionarySha256"]
        )
        self.assertEqual(sha256_file(self.model_manifest), assessment["modelPackSha256"])
        self.assertEqual(
            {
                "status": "processed",
                "pages": 1,
                "stringRecords": 2,
                "ocrRecords": 2,
                "pdfTextRecords": 0,
                "renderedPages": 1,
            },
            {
                key: assessment[key]
                for key in (
                    "status",
                    "pages",
                    "stringRecords",
                    "ocrRecords",
                    "pdfTextRecords",
                    "renderedPages",
                )
            },
        )
        self.assertFalse(Path(str(self.output) + ".incomplete").exists())
        self.assertFalse(Path(str(self.assessments) + ".incomplete").exists())

    def test_force_pdf_emits_text_layer_before_ocr_for_hybrid_page(self) -> None:
        source = self._pdf()
        self._write_inventory(source)
        pdf_text = "Born-digital identity admin@example.com\n"
        ocr_text = "Scanned address 198.51.100.42"
        runtime = FakeRuntime()
        runtime.documents[source.name] = FakeDocument([FakePage("p1", pdf_text)])
        runtime.ocr_results["render:p1"] = {
            "txts": [ocr_text],
            "boxes": [[[20, 40], [300, 40], [300, 80], [20, 80]]],
            "scores": [0.96],
        }

        run_pipeline(self._config(mode="force"), runtime)

        records = read_jsonl(self.output)
        self.assertEqual(["pdf-text", "ocr"], [row["origin"]["kind"] for row in records])
        self.assertEqual(pdf_text, records[0]["text"])
        self.assertEqual(ocr_text, records[1]["text"])
        self.assertEqual("pypdfium2", records[0]["attributes"]["textLayerExtractor"])
        self.assertNotIn("renderSha256", records[0]["attributes"])
        self.assertEqual(300, records[1]["attributes"]["renderDpi"])
        self.assertEqual(["p1"], runtime.render_calls)
        assessment = read_jsonl(self.assessments)[0]
        self.assertEqual(1, assessment["pages"])
        self.assertEqual(2, assessment["stringRecords"])
        self.assertEqual(1, assessment["pdfTextRecords"])
        self.assertEqual(1, assessment["ocrRecords"])
        self.assertEqual(1, assessment["renderedPages"])

    def test_auto_pdf_extracts_usable_text_without_unnecessary_render(self) -> None:
        source = self._pdf()
        self._write_inventory(source)
        text = "This born-digital page contains analyst@example.com and enough usable text."
        runtime = FakeRuntime()
        runtime.documents[source.name] = FakeDocument([FakePage("p1", text)])

        run_pipeline(self._config(mode="auto"), runtime)

        records = read_jsonl(self.output)
        self.assertEqual(1, len(records))
        self.assertEqual("pdf-text", records[0]["origin"]["kind"])
        self.assertEqual([], runtime.render_calls)
        self.assertEqual([], runtime.ocr_calls)
        self.assertEqual(0, read_jsonl(self.assessments)[0]["renderedPages"])

    def test_auto_pdf_renders_suspicious_short_text_layer(self) -> None:
        source = self._pdf()
        self._write_inventory(source)
        runtime = FakeRuntime()
        runtime.documents[source.name] = FakeDocument([FakePage("p1", "Header")])
        runtime.ocr_results["render:p1"] = {
            "txts": ["carved@example.test"],
            "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
            "scores": [0.9],
        }

        run_pipeline(self._config(mode="auto"), runtime)

        self.assertEqual(["p1"], runtime.render_calls)
        self.assertEqual(
            ["pdf-text", "ocr"],
            [record["origin"]["kind"] for record in read_jsonl(self.output)],
        )

    def test_output_is_byte_deterministic_when_engine_order_changes(self) -> None:
        source = self._image()
        self._write_inventory(source)
        rows = [
            ([[2, 40], [100, 40], [100, 60], [2, 60]], "lower@example.test", 0.8),
            ([[2, 5], [100, 5], [100, 20], [2, 20]], "upper@example.test", 0.9),
        ]
        calls = 0

        def alternating_result() -> list[list[Any]]:
            nonlocal calls
            calls += 1
            selected = rows if calls % 2 else list(reversed(rows))
            return [[box, text, score] for box, text, score in selected]

        runtime = FakeRuntime()
        runtime.image_frames[source.name] = [FakeImage("image:1")]
        runtime.ocr_results["image:1"] = alternating_result
        config = self._config()
        run_pipeline(config, runtime)
        first_records = self.output.read_bytes()
        first_assessments = self.assessments.read_bytes()
        run_pipeline(config, runtime)

        self.assertEqual(first_records, self.output.read_bytes())
        self.assertEqual(first_assessments, self.assessments.read_bytes())

    def test_failure_preserves_previous_outputs_and_leaves_incomplete_markers(self) -> None:
        source = self._image("broken.png")
        self._write_inventory(source)
        self.output.write_text("previous strings\n", encoding="utf-8")
        self.assessments.write_text("previous assessments\n", encoding="utf-8")
        runtime = FakeRuntime()
        runtime.image_frames[source.name] = [FakeImage("broken")]
        runtime.ocr_results["broken"] = OcrError("synthetic inference failure")

        with self.assertRaises(OcrError):
            run_pipeline(self._config(), runtime)

        self.assertEqual("previous strings\n", self.output.read_text(encoding="utf-8"))
        self.assertEqual("previous assessments\n", self.assessments.read_text(encoding="utf-8"))
        self.assertTrue(Path(str(self.output) + ".incomplete").is_file())
        self.assertTrue(Path(str(self.assessments) + ".incomplete").is_file())
        self.assertEqual([], list(self.root.glob("*.partial.*")))

    def test_every_inventory_item_has_exact_accounting_record(self) -> None:
        image = self._image()
        binary = self.root / "memory.raw"
        binary.write_bytes(b"not an image or PDF")
        self._write_inventory(binary, image)
        runtime = FakeRuntime()
        runtime.image_frames[image.name] = [FakeImage("image:1")]
        runtime.ocr_results["image:1"] = {
            "txts": ["one@example.test"],
            "boxes": [[[1, 1], [100, 1], [100, 20], [1, 20]]],
            "scores": [0.95],
        }

        stats = run_pipeline(self._config(), runtime)

        assessments = read_jsonl(self.assessments)
        self.assertEqual(2, len(assessments))
        self.assertEqual(
            [str(binary.resolve()), str(image.resolve())],
            [row["sourceFile"] for row in assessments],
        )
        self.assertEqual(["not-applicable", "processed"], [row["status"] for row in assessments])
        self.assertEqual([0, 1], [row["stringRecords"] for row in assessments])
        self.assertIsNone(assessments[0]["sourceSha256"])
        self.assertEqual(binary.stat().st_size, assessments[0]["sourceSize"])
        self.assertEqual(2, stats["inputFiles"])
        self.assertEqual(1, stats["processedFiles"])
        self.assertEqual(1, stats["notApplicableFiles"])

    def test_verified_input_manifest_populates_every_assessment_identity(self) -> None:
        binary = self.root / "memory.raw"
        binary.write_bytes(b"not an OCR-applicable artifact")
        image = self._image()
        self._write_inventory(binary, image)
        manifest_rows = (self._input_manifest_row(binary), self._input_manifest_row(image))
        self._write_input_manifest_rows(*manifest_rows)
        runtime = FakeRuntime()
        runtime.image_frames[image.name] = [FakeImage("image:1")]
        runtime.ocr_results["image:1"] = {
            "txts": ["owner@example.test"],
            "boxes": [[[1, 1], [160, 1], [160, 20], [1, 20]]],
            "scores": [0.99],
        }

        run_pipeline(replace(self._config(), input_manifest=self.input_manifest), runtime)

        assessments = read_jsonl(self.assessments)
        self.assertEqual(["not-applicable", "processed"], [row["status"] for row in assessments])
        for assessment, expected in zip(assessments, manifest_rows, strict=True):
            self.assertEqual(expected["length"], assessment["sourceSize"])
            self.assertEqual(expected["sha256"], assessment["sourceSha256"])
        record = read_jsonl(self.output)[0]
        self.assertEqual(manifest_rows[1]["length"], record["attributes"]["sourceSize"])
        self.assertEqual(manifest_rows[1]["sha256"], record["attributes"]["sourceSha256"])

    def test_input_manifest_path_and_order_mismatch_fails_atomically(self) -> None:
        first = self.root / "first.raw"
        second = self.root / "second.raw"
        first.write_bytes(b"first")
        second.write_bytes(b"second")
        self._write_inventory(first, second)
        self._write_input_manifest_rows(
            self._input_manifest_row(second), self._input_manifest_row(first)
        )

        with self.assertRaises(OcrError):
            run_pipeline(replace(self._config(), input_manifest=self.input_manifest), FakeRuntime())

        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())
        self.assertTrue(Path(str(self.output) + ".incomplete").is_file())

    def test_input_manifest_short_and_extra_counts_fail_atomically(self) -> None:
        first = self.root / "first.raw"
        second = self.root / "second.raw"
        first.write_bytes(b"first")
        second.write_bytes(b"second")
        self._write_inventory(first, second)
        self._write_input_manifest_rows(self._input_manifest_row(first))
        config = replace(self._config(), input_manifest=self.input_manifest)

        with self.assertRaises(OcrError):
            run_pipeline(config, FakeRuntime())
        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())

        self._write_inventory(first)
        self._write_input_manifest_rows(
            self._input_manifest_row(first), self._input_manifest_row(second)
        )
        with self.assertRaises(OcrError):
            run_pipeline(config, FakeRuntime())
        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())

    def test_input_manifest_length_mismatch_fails_before_examination(self) -> None:
        source = self._image()
        self._write_inventory(source)
        self._write_input_manifest_rows(
            self._input_manifest_row(source, length=source.stat().st_size + 1)
        )
        runtime = FakeRuntime()

        with self.assertRaises(OcrError):
            run_pipeline(replace(self._config(), input_manifest=self.input_manifest), runtime)

        self.assertEqual([], runtime.ocr_calls)
        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())

    def test_input_manifest_hash_mismatch_fails_before_publishing(self) -> None:
        source = self._image()
        self._write_inventory(source)
        self._write_input_manifest_rows(self._input_manifest_row(source, sha256="0" * 64))
        runtime = FakeRuntime()

        with self.assertRaises(OcrError):
            run_pipeline(replace(self._config(), input_manifest=self.input_manifest), runtime)

        self.assertEqual([], runtime.ocr_calls)
        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())

    def test_not_applicable_manifest_same_length_mutation_fails_atomically(self) -> None:
        configured_limit = self._config().max_input_bytes
        source = self.root / "memory.raw"
        source.write_bytes(b"A" * (configured_limit + 1))
        expected = self._input_manifest_row(source)
        self._write_inventory(source)
        self._write_input_manifest_rows(expected)
        source.write_bytes(b"B" + b"A" * configured_limit)
        self.assertEqual(expected["length"], source.stat().st_size)
        self.assertNotEqual(expected["sha256"], sha256_file(source))
        self.output.write_text("previous strings\n", encoding="utf-8")
        self.assessments.write_text("previous assessments\n", encoding="utf-8")
        runtime = FakeRuntime()

        with self.assertRaisesRegex(OcrError, "SHA-256 does not match"):
            run_pipeline(replace(self._config(), input_manifest=self.input_manifest), runtime)

        self.assertEqual([], runtime.ocr_calls)
        self.assertEqual("previous strings\n", self.output.read_text(encoding="utf-8"))
        self.assertEqual("previous assessments\n", self.assessments.read_text(encoding="utf-8"))
        for marker in (
            Path(str(self.output) + ".incomplete"),
            Path(str(self.assessments) + ".incomplete"),
        ):
            self.assertTrue(marker.is_file())
            self.assertNotIn(str(expected["sha256"]), marker.read_text(encoding="utf-8"))
        self.assertEqual([], list(self.root.glob("*.partial.*")))

    def test_input_manifest_rejects_non_schema_one_and_non_utf8_rows(self) -> None:
        source = self.root / "memory.raw"
        source.write_bytes(b"memory")
        self._write_inventory(source)
        valid = self._input_manifest_row(source)
        invalid_rows = (
            {**valid, "unknown": "field"},
            {**valid, "schemaVersion": True},
            {**valid, "sha256": str(valid["sha256"]).upper()},
            {**valid, "path": "relative.raw"},
        )
        config = replace(self._config(), input_manifest=self.input_manifest)
        for row in invalid_rows:
            with self.subTest(row=row):
                self._write_input_manifest_rows(row)
                with self.assertRaises(OcrError):
                    run_pipeline(config, FakeRuntime())

        duplicate = (
            '{"schemaVersion":1,"schemaVersion":1,"path":'
            + json.dumps(str(source.resolve()))
            + f',"length":{source.stat().st_size},"sha256":{json.dumps(sha256_file(source))}}}\n'
        )
        self.input_manifest.write_text(duplicate, encoding="utf-8")
        with self.assertRaises(OcrError):
            run_pipeline(config, FakeRuntime())

        self.input_manifest.write_bytes(b'\xff{"schemaVersion":1}\n')
        with self.assertRaises(OcrError):
            run_pipeline(config, FakeRuntime())

    def test_missing_and_hash_mismatched_model_packs_fail_closed(self) -> None:
        missing = self.root / "missing.json"
        with self.assertRaises(OcrError):
            load_model_pack(missing, "a" * 64, self.model_id, self.revision)
        with self.assertRaises(OcrError):
            load_model_pack(self.model_manifest, "b" * 64, self.model_id, self.revision)

        detector = self.root / "detector.onnx"
        detector.write_bytes(b"tampered detector")
        with self.assertRaises(OcrError):
            load_model_pack(
                self.model_manifest,
                sha256_file(self.model_manifest),
                self.model_id,
                self.revision,
            )

    def test_self_test_uses_same_identity_contract_without_evidence_outputs(self) -> None:
        runtime = FakeRuntime()
        stats = run_pipeline(self._config(self_test=True), runtime)
        self.assertEqual(0, stats["inputFiles"])
        self.assertEqual(1, runtime.self_test_calls)
        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())
        self.assertTrue(runtime.closed)

    def test_hybrid_self_test_invokes_both_lanes_and_propagates_failure(self) -> None:
        gpu_runtime = FakeRuntime("directml")
        cpu_runtime = FakeRuntime("cpu")
        runtime = HybridOcrRuntime(gpu_runtime, cpu_runtime)
        config = replace(self._config(self_test=True), provider="hybrid")

        stats = run_pipeline(config, runtime)

        self.assertEqual("hybrid-directml-cpu", stats["provider"])
        self.assertEqual(1, gpu_runtime.self_test_calls)
        self.assertEqual(1, cpu_runtime.self_test_calls)
        self.assertTrue(gpu_runtime.closed)
        self.assertTrue(cpu_runtime.closed)
        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())

        failing_gpu = FakeRuntime("directml")
        failing_cpu = FakeRuntime("cpu")
        failing_cpu.self_test_error = OcrError("synthetic classifier failure")
        failing_runtime = HybridOcrRuntime(failing_gpu, failing_cpu)

        with self.assertRaisesRegex(OcrError, "synthetic classifier failure"):
            run_pipeline(config, failing_runtime)

        self.assertEqual(1, failing_gpu.self_test_calls)
        self.assertEqual(1, failing_cpu.self_test_calls)
        self.assertTrue(failing_gpu.closed)
        self.assertTrue(failing_cpu.closed)

    def test_concrete_self_test_requires_detector_classifier_and_recognizer(self) -> None:
        class Component:
            def __init__(self) -> None:
                self.calls = 0

            def __call__(self, value: Any) -> Any:
                self.calls += 1
                return value

        class Engine:
            def __init__(self, *, skip_classifier: bool = False) -> None:
                self.text_det = Component()
                self.text_cls = Component()
                self.text_rec = Component()
                self.skip_classifier = skip_classifier

            def __call__(self, image: FakeImage) -> dict[str, Any]:
                self.text_det(image)
                if not self.skip_classifier:
                    self.text_cls(image)
                self.text_rec(image)
                return {
                    "txts": ["BSTRINGS OCR 42"],
                    "boxes": [[[1, 1], [300, 1], [300, 30], [1, 30]]],
                    "scores": [0.99],
                }

        class Numpy:
            @staticmethod
            def asarray(image: FakeImage) -> FakeImage:
                return image

        def runtime_for(engine: Engine, image: FakeImage) -> RapidOcrRuntime:
            runtime = object.__new__(RapidOcrRuntime)
            runtime.engine_version = "3.9.2"
            runtime.pdfium_version = "5.12.1"
            runtime.resolved_provider = "cpu"
            runtime._engine = engine
            runtime._numpy = Numpy()
            runtime._create_self_test_image = lambda: image
            return runtime

        image = FakeImage("self-test")
        engine = Engine()
        original_components = (engine.text_det, engine.text_cls, engine.text_rec)
        runtime_for(engine, image).run_inference_self_test()

        self.assertEqual(1, engine.text_det.calls)
        self.assertEqual(1, engine.text_cls.calls)
        self.assertEqual(1, engine.text_rec.calls)
        self.assertEqual(original_components, (engine.text_det, engine.text_cls, engine.text_rec))
        self.assertTrue(image.closed)

        incomplete_image = FakeImage("incomplete-self-test")
        incomplete_engine = Engine(skip_classifier=True)
        incomplete_components = (
            incomplete_engine.text_det,
            incomplete_engine.text_cls,
            incomplete_engine.text_rec,
        )
        with self.assertRaisesRegex(OcrError, "inference self-test failed"):
            runtime_for(incomplete_engine, incomplete_image).run_inference_self_test()
        self.assertEqual(
            incomplete_components,
            (incomplete_engine.text_det, incomplete_engine.text_cls, incomplete_engine.text_rec),
        )
        self.assertTrue(incomplete_image.closed)

    def test_cli_contract_and_local_rapidocr_parameters(self) -> None:
        args = parse_arguments(
            [
                "--airgap",
                "--paths-from",
                "inventory.txt",
                "--input-manifest",
                "input-manifest.jsonl",
                "--output",
                "strings.jsonl",
                "--assessments-output",
                "assessments.jsonl",
                "--ocr-executable",
                "rapidocr.exe",
                "--ocr-engine",
                "rapidocr",
                "--ocr-engine-version",
                "3.9.2",
                "--ocr-model-path",
                "pack.json",
                "--ocr-model-id",
                self.model_id,
                "--ocr-model-revision",
                self.revision,
                "--ocr-model-sha256",
                "a" * 64,
                "--ocr-mode",
                "force",
                "--progress-total-files",
                "27",
            ]
        )
        self.assertEqual("force", args.ocr_mode)
        self.assertEqual("auto", args.provider)
        self.assertEqual(0, args.threads)
        self.assertEqual(27, args.progress_total_files)
        self.assertEqual(Path("input-manifest.jsonl"), args.input_manifest)
        pack = load_model_pack(
            self.model_manifest,
            sha256_file(self.model_manifest),
            self.model_id,
            self.revision,
        )
        params = rapidocr_parameters(pack, "directml", 3)
        self.assertEqual(str(pack.detector.path), params["Det.model_path"])
        self.assertEqual(str(pack.recognizer.path), params["Rec.model_path"])
        self.assertEqual(str(pack.dictionary.path), params["Rec.rec_keys_path"])
        self.assertEqual(str(pack.classifier.path), params["Cls.model_path"])
        self.assertTrue(params["EngineConfig.onnxruntime.use_dml"])
        self.assertFalse(params["EngineConfig.onnxruntime.use_cuda"])
        self.assertTrue(params["Global.use_cls"])
        self.assertEqual(3, params["EngineConfig.onnxruntime.intra_op_num_threads"])

    def test_session_threads_are_provider_aware_bounded_and_overridable(self) -> None:
        self.assertEqual(16, resolve_session_threads(0, "cpu", available_threads=16))
        self.assertEqual(
            14,
            resolve_session_threads(
                0,
                "cpu",
                available_threads=16,
                hybrid_cpu_lane=True,
            ),
        )
        self.assertEqual(1, resolve_session_threads(0, "directml", available_threads=16))
        self.assertEqual(1, resolve_session_threads(0, "cuda", available_threads=16))
        self.assertEqual(7, resolve_session_threads(7, "cpu", available_threads=16))
        self.assertEqual(7, resolve_session_threads(7, "directml", available_threads=16))
        self.assertEqual(256, resolve_session_threads(0, "cpu", available_threads=4096))
        with self.assertRaises(OcrError):
            resolve_session_threads(257, "cpu", available_threads=16)

    def test_cooperative_onnx_sessions_disable_pool_spinning(self) -> None:
        created_options: list[Any] = []

        class FakeOptions:
            def __init__(self) -> None:
                self.entries: dict[str, str] = {}
                created_options.append(self)

            def add_session_config_entry(self, name: str, value: str) -> None:
                self.entries[name] = value

        class FakeSession:
            def __init__(self, *_args: Any, **_kwargs: Any) -> None:
                pass

            def get_providers(self) -> list[str]:
                return ["CPUExecutionProvider"]

        class FakeOnnxRuntime:
            SessionOptions = FakeOptions
            InferenceSession = FakeSession

            class GraphOptimizationLevel:
                ORT_ENABLE_ALL = "all"

            class ExecutionMode:
                ORT_SEQUENTIAL = "sequential"

            @staticmethod
            def get_available_providers() -> list[str]:
                return ["CPUExecutionProvider"]

        RapidOcrRuntime._create_session(
            FakeOnnxRuntime,
            self.root / "detector.onnx",
            "cpu",
            5,
            cooperative_sessions=True,
        )

        self.assertEqual(1, len(created_options))
        self.assertEqual(5, created_options[0].intra_op_num_threads)
        self.assertEqual(1, created_options[0].inter_op_num_threads)
        self.assertEqual(
            {
                "session.inter_op.allow_spinning": "0",
                "session.intra_op.allow_spinning": "0",
            },
            created_options[0].entries,
        )

    def test_parallel_preprocessing_requires_verified_single_thread_opencv(self) -> None:
        class FakeCv2:
            def __init__(self, reported: int) -> None:
                self.reported = reported
                self.requested: list[int] = []

            def setNumThreads(self, value: int) -> None:
                self.requested.append(value)

            def getNumThreads(self) -> int:
                return self.reported

        runtime = RapidOcrRuntime.__new__(RapidOcrRuntime)
        runtime._cv2 = FakeCv2(1)
        runtime.configure_parallel_preprocessing()
        self.assertEqual([1], runtime._cv2.requested)

        runtime._cv2 = FakeCv2(2)
        with self.assertRaisesRegex(OcrError, "did not honor"):
            runtime.configure_parallel_preprocessing()

    def test_cpu_worker_layout_is_bounded_and_preserves_explicit_threads(self) -> None:
        self.assertEqual((4, 5), resolve_cpu_worker_layout(0, available_threads=22))
        self.assertEqual((4, 5), resolve_cpu_worker_layout(5, available_threads=22))
        self.assertEqual((2, 8), resolve_cpu_worker_layout(8, available_threads=22))
        self.assertEqual((1, 32), resolve_cpu_worker_layout(32, available_threads=22))
        self.assertEqual((1, 1), resolve_cpu_worker_layout(0, available_threads=1))

    def test_thread_request_and_resolution_are_recorded_in_every_output(self) -> None:
        source = self._image("threaded.png")
        self._write_inventory(source)
        runtime = FakeRuntime("cpu", requested_threads=0)
        runtime.image_frames[source.name] = [FakeImage("threaded:1")]
        runtime.ocr_results["threaded:1"] = {
            "txts": ["threaded@example.test"],
            "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
            "scores": [0.99],
        }

        stats = run_pipeline(replace(self._config(), threads=0), runtime)

        record = read_jsonl(self.output)[0]
        assessment = read_jsonl(self.assessments)[0]
        self.assertEqual(0, record["attributes"]["requestedThreads"])
        self.assertEqual({"cpu": 4}, record["attributes"]["resolvedThreadCounts"])
        self.assertEqual({"cpu": 1}, record["attributes"]["resolvedWorkerCounts"])
        self.assertEqual(0, assessment["requestedThreads"])
        self.assertEqual({"cpu": 4}, assessment["resolvedThreadCounts"])
        self.assertEqual({"cpu": 1}, assessment["resolvedWorkerCounts"])
        self.assertEqual(0, stats["requestedThreads"])
        self.assertEqual({"cpu": 4}, stats["resolvedThreadCounts"])
        self.assertEqual({"cpu": 1}, stats["resolvedWorkerCounts"])

    def test_multipage_scheduler_bounds_pending_results_and_drains_in_order(self) -> None:
        class PendingPage:
            def __init__(self, ordinal: int) -> None:
                self.ordinal = ordinal
                self.closed = False

            def close(self) -> None:
                self.closed = True

        gpu = FakeRuntime("directml")
        cpu = FakeRuntime("cpu")
        scheduler = RasterScheduler(HybridOcrRuntime(gpu, cpu))
        observed: list[int] = []
        images: list[FakeImage] = []
        page_spools: list[PendingPage] = []

        def drain(spools: Any) -> None:
            for spool in spools:
                observed.append(spool.ordinal)
                spool.close()

        scheduler.begin_source(drain)
        for ordinal in range(100):
            image = FakeImage(f"bounded:{ordinal}")
            images.append(image)

            def work(*, runtime: Any, execution_provider: str, value: int = ordinal) -> Any:
                self.assertIn(execution_provider, {"cpu", "directml"})
                self.assertIsNotNone(runtime)
                spool = PendingPage(value)
                page_spools.append(spool)
                return spool

            scheduler.submit(image, work)

        self.assertTrue(observed, "the scheduler retained every completed page until finish")
        self.assertLessEqual(scheduler.maximum_pending_observed, HYBRID_SOURCE_MAX_PENDING)
        scheduler.finish_source()
        scheduler.close()
        self.assertEqual(list(range(100)), observed)
        self.assertTrue(all(image.closed for image in images))
        self.assertTrue(all(spool.closed for spool in page_spools))

    def test_hybrid_multipage_source_spools_records_in_order_and_removes_spool(self) -> None:
        source = self._image("bounded-multipage.tiff")
        self._write_inventory(source)
        gpu = FakeRuntime("directml")
        cpu = FakeRuntime("cpu")
        frames = [FakeImage(f"multipage:{ordinal:03d}") for ordinal in range(100)]
        cpu.image_frames[source.name] = frames
        for ordinal, frame in enumerate(frames):
            result = {
                "txts": [f"row-{ordinal:03d}@example.test"],
                "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                "scores": [0.99],
            }
            gpu.ocr_results[frame.key] = result
            cpu.ocr_results[frame.key] = result

        stats = run_pipeline(
            replace(self._config(), provider="hybrid"),
            HybridOcrRuntime(gpu, cpu),
        )

        records = read_jsonl(self.output)
        self.assertEqual(
            [f"row-{ordinal:03d}@example.test" for ordinal in range(100)],
            [record["text"] for record in records],
        )
        self.assertEqual(100, stats["stringRecords"])
        self.assertEqual([], list(self.root.glob("bstrings-ocr-source.*.jsonl")))
        self.assertTrue(all(frame.closed for frame in frames))

    def test_hybrid_multipage_budget_failure_drains_and_removes_every_spool(self) -> None:
        source = self._image("over-budget-multipage.tiff")
        self._write_inventory(source)
        self.output.write_text("previous strings\n", encoding="utf-8")
        self.assessments.write_text("previous assessments\n", encoding="utf-8")
        gpu = FakeRuntime("directml")
        cpu = FakeRuntime("cpu")
        frames = [FakeImage(f"over-budget:{ordinal:03d}") for ordinal in range(20)]
        cpu.image_frames[source.name] = frames
        for ordinal, frame in enumerate(frames):
            result = {
                "txts": [f"row-{ordinal:03d}@example.test"],
                "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                "scores": [0.99],
            }
            gpu.ocr_results[frame.key] = result
            cpu.ocr_results[frame.key] = result

        with self.assertRaisesRegex(OcrError, "record limit"):
            run_pipeline(
                replace(
                    self._config(),
                    provider="hybrid",
                    max_output_records=5,
                ),
                HybridOcrRuntime(gpu, cpu),
            )

        self.assertEqual("previous strings\n", self.output.read_text(encoding="utf-8"))
        self.assertEqual(
            "previous assessments\n",
            self.assessments.read_text(encoding="utf-8"),
        )
        self.assertTrue(all(frame.closed for frame in frames))
        for prefix in (
            "bstrings-ocr-page.*.jsonl",
            "bstrings-ocr-source.*.jsonl",
            "bstrings-ocr-sort.*.jsonl",
            "bstrings-ocr-merge.*.jsonl",
        ):
            self.assertEqual([], list(self.root.glob(prefix)))

    def test_model_pack_requires_local_character_dictionary(self) -> None:
        manifest = json.loads(self.model_manifest.read_text(encoding="utf-8"))
        del manifest["dictionary"]
        self.model_manifest.write_text(
            json.dumps(manifest, ensure_ascii=False, separators=(",", ":"), sort_keys=True),
            encoding="utf-8",
        )
        with self.assertRaises(OcrError):
            load_model_pack(
                self.model_manifest,
                sha256_file(self.model_manifest),
                self.model_id,
                self.revision,
            )

    def test_record_limit_failure_is_atomic(self) -> None:
        source = self._image()
        self._write_inventory(source)
        runtime = FakeRuntime()
        runtime.image_frames[source.name] = [FakeImage("image:1")]
        runtime.ocr_results["image:1"] = {
            "txts": ["one@example.test", "two@example.test"],
            "boxes": [
                [[1, 1], [100, 1], [100, 20], [1, 20]],
                [[1, 30], [100, 30], [100, 50], [1, 50]],
            ],
            "scores": [0.9, 0.9],
        }
        config = replace(self._config(), max_output_records=1)

        with self.assertRaises(OcrError):
            run_pipeline(config, runtime)

        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())
        self.assertTrue(Path(str(self.output) + ".incomplete").exists())

    def test_atomic_pair_enter_rolls_back_first_temp_when_second_creation_fails(self) -> None:
        self.output.write_text("previous strings\n", encoding="utf-8")
        self.assessments.write_text("previous assessments\n", encoding="utf-8")
        real_mkstemp = tempfile.mkstemp
        created: list[Path] = []
        calls = 0

        def fail_second_temp(*args: Any, **kwargs: Any) -> tuple[int, str]:
            nonlocal calls
            calls += 1
            if calls == 2:
                raise OSError("synthetic second temporary failure")
            descriptor, name = real_mkstemp(*args, **kwargs)
            created.append(Path(name))
            return descriptor, name

        pair = AtomicJsonlPair(self.output, self.assessments, 1024 * 1024)
        with (
            patch("bstrings_ocr.tempfile.mkstemp", side_effect=fail_second_temp),
            self.assertRaisesRegex(OSError, "second temporary"),
        ):
            pair.__enter__()

        self.assertEqual(1, len(created))
        self.assertFalse(created[0].exists())
        self.assertEqual("previous strings\n", self.output.read_text(encoding="utf-8"))
        self.assertEqual(
            "previous assessments\n",
            self.assessments.read_text(encoding="utf-8"),
        )
        self.assertFalse(Path(str(self.output) + ".incomplete").exists())
        self.assertFalse(Path(str(self.assessments) + ".incomplete").exists())

    def test_pdf_text_limit_failure_is_atomic(self) -> None:
        source = self._pdf()
        self._write_inventory(source)
        runtime = FakeRuntime()
        runtime.documents[source.name] = FakeDocument([FakePage("p1", "x" * 33)])
        config = replace(self._config(), max_text_characters=32)

        with self.assertRaises(OcrError):
            run_pipeline(config, runtime)

        self.assertFalse(self.output.exists())
        self.assertFalse(self.assessments.exists())
        self.assertEqual([], runtime.ocr_calls)
        self.assertTrue(Path(str(self.output) + ".incomplete").exists())

    def test_provider_selection_prefers_verified_cuda_then_directml_then_cpu(self) -> None:
        available = [
            "CPUExecutionProvider",
            "DmlExecutionProvider",
            "CUDAExecutionProvider",
        ]
        self.assertEqual(("cuda", "directml", "cpu"), provider_candidates("auto", available))
        self.assertEqual(
            ("directml", "cpu"),
            provider_candidates("auto", ["CPUExecutionProvider", "DmlExecutionProvider"]),
        )
        self.assertEqual(("cpu",), provider_candidates("auto", ["CPUExecutionProvider"]))

    def test_explicit_and_hybrid_provider_selection_fail_when_unavailable(self) -> None:
        with self.assertRaises(OcrError):
            provider_candidates("cuda", ["CPUExecutionProvider"])
        with self.assertRaises(OcrError):
            provider_candidates("hybrid", ["CPUExecutionProvider"])
        with self.assertRaises(OcrError):
            provider_candidates("hybrid", ["DmlExecutionProvider"])

    def test_auto_contract_accepts_valid_hybrid_resolution(self) -> None:
        _validate_resolved_provider("auto", "hybrid-cuda-cpu")
        _validate_resolved_provider("auto", "hybrid-directml-cpu")

    def test_parallel_cpu_sources_overlap_are_bounded_ordered_and_byte_stable(self) -> None:
        sources = [self._image(f"parallel-{index:02d}.png") for index in range(8)]
        self._write_inventory(*sources)

        def execute() -> tuple[bytes, bytes, dict[str, Any], list[FakeRuntime], int, int]:
            lanes = [FakeRuntime("cpu", requested_threads=5) for _ in range(4)]
            images = [FakeImage(f"parallel:{index:02d}") for index in range(len(sources))]
            for source, image in zip(sources, images, strict=True):
                lanes[0].image_frames[source.name] = [image]
            barrier = threading.Barrier(4)
            lock = threading.Lock()
            active = 0
            maximum_active = 0

            def result(index: int) -> Any:
                def run() -> dict[str, Any]:
                    nonlocal active, maximum_active
                    with lock:
                        active += 1
                        maximum_active = max(maximum_active, active)
                    try:
                        if index < 4:
                            barrier.wait(timeout=5)
                        time.sleep((len(sources) - index) * 0.002)
                        return {
                            "txts": [f"parallel-{index:02d}@example.test"],
                            "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                            "scores": [0.99],
                        }
                    finally:
                        with lock:
                            active -= 1

                return run

            for index, image in enumerate(images):
                for lane in lanes:
                    lane.ocr_results[image.key] = result(index)

            captured: list[RasterScheduler] = []

            class CapturingScheduler(RasterScheduler):
                def __init__(self, runtime: Any) -> None:
                    super().__init__(runtime)
                    captured.append(self)

            with patch("bstrings_ocr.RasterScheduler", CapturingScheduler):
                stats = run_pipeline(
                    replace(self._config(), provider="cpu", threads=5),
                    CpuParallelOcrRuntime(lanes),
                )
            self.assertEqual(1, len(captured))
            return (
                self.output.read_bytes(),
                self.assessments.read_bytes(),
                stats,
                lanes,
                maximum_active,
                captured[0].maximum_cross_source_pending_observed,
            )

        first_output, first_assessments, stats, lanes, maximum_active, maximum_pending = execute()
        second_output, second_assessments, _, _, second_maximum_active, _ = execute()

        records = [json.loads(line) for line in first_output.decode("utf-8").splitlines()]
        assessments = [json.loads(line) for line in first_assessments.decode("utf-8").splitlines()]
        self.assertEqual(first_output, second_output)
        self.assertEqual(first_assessments, second_assessments)
        self.assertEqual(4, maximum_active)
        self.assertEqual(4, second_maximum_active)
        self.assertGreaterEqual(maximum_pending, 4)
        self.assertLessEqual(
            maximum_pending,
            len(lanes) * CPU_PARALLEL_QUEUE_DEPTH_PER_WORKER,
        )
        self.assertEqual(
            [str(source.resolve()) for source in sources],
            [record["sourceFile"] for record in records],
        )
        self.assertEqual(
            [f"parallel-{index:02d}@example.test" for index in range(len(sources))],
            [record["text"] for record in records],
        )
        self.assertTrue(
            all(record["attributes"]["resolvedThreadCounts"] == {"cpu": 5} for record in records)
        )
        self.assertTrue(
            all(record["attributes"]["resolvedWorkerCounts"] == {"cpu": 4} for record in records)
        )
        self.assertTrue(
            all(assessment["resolvedWorkerCounts"] == {"cpu": 4} for assessment in assessments)
        )
        self.assertEqual({"cpu": 5}, stats["resolvedThreadCounts"])
        self.assertEqual({"cpu": 4}, stats["resolvedWorkerCounts"])
        self.assertEqual(1, lanes[0].parallel_preprocessing_calls)
        self.assertTrue(all(lane.closed for lane in lanes))
        for prefix in ("bstrings-ocr-page.*.jsonl", "bstrings-ocr-source.*.jsonl"):
            self.assertEqual([], list(self.root.glob(prefix)))

    def test_parallel_cpu_failure_drains_lanes_and_preserves_atomic_outputs(self) -> None:
        first = self._image("parallel-failure.png")
        second = self._image("parallel-drain.png")
        self._write_inventory(first, second)
        self.output.write_text("previous strings\n", encoding="utf-8")
        self.assessments.write_text("previous assessments\n", encoding="utf-8")
        lanes = [FakeRuntime("cpu", requested_threads=8) for _ in range(2)]
        first_image = FakeImage("parallel-failure:first")
        second_image = FakeImage("parallel-failure:second")
        lanes[0].image_frames[first.name] = [first_image]
        lanes[0].image_frames[second.name] = [second_image]
        barrier = threading.Barrier(2)

        def fail() -> Any:
            barrier.wait(timeout=5)
            raise OcrError("synthetic parallel CPU failure")

        def complete() -> dict[str, Any]:
            barrier.wait(timeout=5)
            return {
                "txts": ["drained-cpu@example.test"],
                "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                "scores": [0.99],
            }

        lanes[0].ocr_results[first_image.key] = fail
        lanes[1].ocr_results[second_image.key] = complete

        with self.assertRaisesRegex(OcrError, "synthetic parallel CPU failure"):
            run_pipeline(
                replace(self._config(), provider="cpu", threads=8),
                CpuParallelOcrRuntime(lanes),
            )

        self.assertEqual("previous strings\n", self.output.read_text(encoding="utf-8"))
        self.assertEqual(
            "previous assessments\n",
            self.assessments.read_text(encoding="utf-8"),
        )
        self.assertTrue(Path(str(self.output) + ".incomplete").is_file())
        self.assertTrue(Path(str(self.assessments) + ".incomplete").is_file())
        self.assertTrue(first_image.closed)
        self.assertTrue(second_image.closed)
        self.assertTrue(all(lane.closed for lane in lanes))
        for prefix in (
            "bstrings-ocr-page.*.jsonl",
            "bstrings-ocr-source.*.jsonl",
            "bstrings-ocr-sort.*.jsonl",
            "bstrings-ocr-merge.*.jsonl",
        ):
            self.assertEqual([], list(self.root.glob(prefix)))

    def test_hybrid_single_frame_sources_use_both_lanes_concurrently_in_order(self) -> None:
        first = self._image("first.png")
        second = self._image("second.png")
        self._write_inventory(first, second)
        self._write_input_manifest_rows(
            self._input_manifest_row(first), self._input_manifest_row(second)
        )
        cpu = FakeRuntime("cpu")
        gpu = FakeRuntime("directml")
        first_image = FakeImage("cross:first")
        second_image = FakeImage("cross:second")
        cpu.image_frames[first.name] = [first_image]
        cpu.image_frames[second.name] = [second_image]
        barrier = threading.Barrier(2)

        def synchronized_result(text: str, delay: float) -> Any:
            def run() -> dict[str, Any]:
                barrier.wait(timeout=2)
                time.sleep(delay)
                return {
                    "txts": [text],
                    "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                    "scores": [0.99],
                }

            return run

        gpu.ocr_results["cross:first"] = synchronized_result("first@example.test", 0.05)
        cpu.ocr_results["cross:second"] = synchronized_result("second@example.test", 0.0)

        stats = run_pipeline(
            replace(
                self._config(),
                provider="hybrid",
                input_manifest=self.input_manifest,
            ),
            HybridOcrRuntime(gpu, cpu),
        )

        records = read_jsonl(self.output)
        self.assertEqual(
            [str(first.resolve()), str(second.resolve())],
            [record["sourceFile"] for record in records],
        )
        self.assertEqual(
            ["first@example.test", "second@example.test"],
            [record["text"] for record in records],
        )
        self.assertEqual(
            ["directml", "cpu"],
            [record["attributes"]["executionProvider"] for record in records],
        )
        self.assertTrue(
            all(record["origin"]["provider"] == "hybrid-directml-cpu" for record in records)
        )
        assessments = read_jsonl(self.assessments)
        self.assertEqual(
            [str(first.resolve()), str(second.resolve())],
            [assessment["sourceFile"] for assessment in assessments],
        )
        self.assertTrue(
            all(assessment["provider"] == "hybrid-directml-cpu" for assessment in assessments)
        )
        self.assertEqual(["cross:first"], gpu.ocr_calls)
        self.assertEqual(["cross:second"], cpu.ocr_calls)
        self.assertTrue(first_image.closed)
        self.assertTrue(second_image.closed)
        self.assertEqual(2, stats["inputFiles"])
        self.assertEqual(2, stats["processedFiles"])
        self.assertEqual(2, stats["pages"])
        self.assertEqual("hybrid-directml-cpu", stats["provider"])

    def test_hybrid_cross_source_lane_weighting_is_run_global_and_deterministic(self) -> None:
        sources = [self._image(f"weighted-{index:02d}.png") for index in range(27)]
        self._write_inventory(*sources)
        self._write_input_manifest_rows(*(self._input_manifest_row(path) for path in sources))
        cpu = FakeRuntime("cpu")
        gpu = FakeRuntime("directml")
        for index, source in enumerate(sources):
            key = f"weighted:{index:02d}"
            cpu.image_frames[source.name] = [FakeImage(key)]
            result = {
                "txts": [f"row-{index:02d}@example.test"],
                "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                "scores": [0.99],
            }
            cpu.ocr_results[key] = result
            gpu.ocr_results[key] = result

        run_pipeline(
            replace(
                self._config(),
                provider="hybrid",
                input_manifest=self.input_manifest,
            ),
            HybridOcrRuntime(gpu, cpu),
        )

        records = read_jsonl(self.output)
        self.assertEqual(
            [str(source.resolve()) for source in sources],
            [record["sourceFile"] for record in records],
        )
        self.assertEqual(
            ["cpu" if index in {1, 13, 26} else "directml" for index in range(27)],
            [record["attributes"]["executionProvider"] for record in records],
        )
        self.assertEqual(24, len(gpu.ocr_calls))
        self.assertEqual(3, len(cpu.ocr_calls))

    def test_hybrid_cross_source_window_keeps_gpu_fed_while_first_cpu_runs(self) -> None:
        sources = [self._image(f"sustained-{index:02d}.png") for index in range(15)]
        self._write_inventory(*sources)
        self._write_input_manifest_rows(*(self._input_manifest_row(path) for path in sources))
        cpu = FakeRuntime("cpu")
        gpu = FakeRuntime("directml")
        cpu_started = threading.Event()
        sustained_gpu_work = threading.Event()
        count_lock = threading.Lock()
        gpu_completed = 0

        def result(index: int) -> dict[str, Any]:
            return {
                "txts": [f"sustained-{index:02d}@example.test"],
                "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                "scores": [0.99],
            }

        def gpu_result(index: int) -> Any:
            def run() -> dict[str, Any]:
                nonlocal gpu_completed
                if not cpu_started.wait(timeout=5):
                    raise OcrError("CPU lane did not start")
                with count_lock:
                    gpu_completed += 1
                    if gpu_completed >= 12:
                        sustained_gpu_work.set()
                return result(index)

            return run

        def slow_cpu_result() -> dict[str, Any]:
            cpu_started.set()
            if not sustained_gpu_work.wait(timeout=5):
                raise OcrError("GPU lane starved behind the ordered CPU result")
            return result(1)

        for index, source in enumerate(sources):
            key = f"sustained:{index:02d}"
            cpu.image_frames[source.name] = [FakeImage(key)]
            if index == 1:
                cpu.ocr_results[key] = slow_cpu_result
            elif index == 13:
                cpu.ocr_results[key] = result(index)
            else:
                gpu.ocr_results[key] = gpu_result(index)

        run_pipeline(
            replace(
                self._config(),
                provider="hybrid",
                input_manifest=self.input_manifest,
            ),
            HybridOcrRuntime(gpu, cpu),
        )

        records = read_jsonl(self.output)
        self.assertGreaterEqual(gpu_completed, 12)
        self.assertEqual(
            [str(source.resolve()) for source in sources],
            [record["sourceFile"] for record in records],
        )
        self.assertEqual(13, len(gpu.ocr_calls))
        self.assertEqual(2, len(cpu.ocr_calls))

    def test_hybrid_cross_source_failure_drains_work_and_preserves_outputs(self) -> None:
        first = self._image("failing-first.png")
        second = self._image("drained-second.png")
        self._write_inventory(first, second)
        self._write_input_manifest_rows(
            self._input_manifest_row(first), self._input_manifest_row(second)
        )
        self.output.write_text("previous strings\n", encoding="utf-8")
        self.assessments.write_text("previous assessments\n", encoding="utf-8")
        cpu = FakeRuntime("cpu")
        gpu = FakeRuntime("directml")
        first_image = FakeImage("failure:first")
        second_image = FakeImage("failure:second")
        cpu.image_frames[first.name] = [first_image]
        cpu.image_frames[second.name] = [second_image]
        barrier = threading.Barrier(2)

        def fail() -> Any:
            barrier.wait(timeout=2)
            raise OcrError("synthetic cross-source failure")

        def complete() -> dict[str, Any]:
            barrier.wait(timeout=2)
            time.sleep(0.02)
            return {
                "txts": ["drained@example.test"],
                "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                "scores": [0.99],
            }

        gpu.ocr_results["failure:first"] = fail
        cpu.ocr_results["failure:second"] = complete

        with self.assertRaisesRegex(OcrError, "synthetic cross-source failure"):
            run_pipeline(
                replace(
                    self._config(),
                    provider="hybrid",
                    input_manifest=self.input_manifest,
                ),
                HybridOcrRuntime(gpu, cpu),
            )

        self.assertEqual("previous strings\n", self.output.read_text(encoding="utf-8"))
        self.assertEqual("previous assessments\n", self.assessments.read_text(encoding="utf-8"))
        self.assertTrue(Path(str(self.output) + ".incomplete").is_file())
        self.assertTrue(Path(str(self.assessments) + ".incomplete").is_file())
        self.assertEqual(["failure:first"], gpu.ocr_calls)
        self.assertEqual(["failure:second"], cpu.ocr_calls)
        self.assertTrue(first_image.closed)
        self.assertTrue(second_image.closed)
        self.assertTrue(gpu.closed)
        self.assertTrue(cpu.closed)

    def test_hybrid_cross_source_dense_output_fails_budget_and_cleans_spools(self) -> None:
        sources = [self._image(f"dense-{index:02d}.png") for index in range(4)]
        self._write_inventory(*sources)
        self.output.write_text("previous strings\n", encoding="utf-8")
        self.assessments.write_text("previous assessments\n", encoding="utf-8")
        cpu = FakeRuntime("cpu")
        gpu = FakeRuntime("directml")
        images: list[FakeImage] = []
        for source_index, source in enumerate(sources):
            key = f"dense:{source_index:02d}"
            image = FakeImage(key)
            images.append(image)
            cpu.image_frames[source.name] = [image]
            result = {
                "txts": [
                    f"dense-{source_index:02d}-{hit_index:02d}@example.test"
                    for hit_index in range(4)
                ],
                "boxes": [
                    [
                        [1, 1 + hit_index * 20],
                        [250, 1 + hit_index * 20],
                        [250, 18 + hit_index * 20],
                        [1, 18 + hit_index * 20],
                    ]
                    for hit_index in range(4)
                ],
                "scores": [0.99] * 4,
            }
            cpu.ocr_results[key] = result
            gpu.ocr_results[key] = result

        with (
            patch("bstrings_ocr.make_record", wraps=make_record) as record_factory,
            self.assertRaisesRegex(OcrError, "record limit"),
        ):
            run_pipeline(
                replace(
                    self._config(),
                    provider="hybrid",
                    max_output_records=5,
                ),
                HybridOcrRuntime(gpu, cpu),
            )

        self.assertLess(record_factory.call_count, 16)
        self.assertEqual("previous strings\n", self.output.read_text(encoding="utf-8"))
        self.assertEqual(
            "previous assessments\n",
            self.assessments.read_text(encoding="utf-8"),
        )
        self.assertTrue(Path(str(self.output) + ".incomplete").is_file())
        self.assertTrue(Path(str(self.assessments) + ".incomplete").is_file())
        self.assertTrue(all(image.closed for image in images))
        for prefix in (
            "bstrings-ocr-page.*.jsonl",
            "bstrings-ocr-source.*.jsonl",
            "bstrings-ocr-sort.*.jsonl",
            "bstrings-ocr-merge.*.jsonl",
        ):
            self.assertEqual([], list(self.root.glob(prefix)))

    def test_hybrid_lanes_run_concurrently_and_output_remains_ordered(self) -> None:
        source = self._image("two-frame.tiff")
        self._write_inventory(source)
        cpu = FakeRuntime("cpu")
        gpu = FakeRuntime("directml")
        cpu.image_frames[source.name] = [FakeImage("frame:1"), FakeImage("frame:2")]
        barrier = threading.Barrier(2)

        def synchronized_result(text: str, delay: float) -> Any:
            def run() -> dict[str, Any]:
                barrier.wait(timeout=2)
                time.sleep(delay)
                return {
                    "txts": [text],
                    "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
                    "scores": [0.99],
                }

            return run

        gpu.ocr_results["frame:1"] = synchronized_result("first@example.test", 0.05)
        cpu.ocr_results["frame:2"] = synchronized_result("second@example.test", 0.0)
        runtime = HybridOcrRuntime(gpu, cpu)

        run_pipeline(replace(self._config(), provider="hybrid"), runtime)

        records = read_jsonl(self.output)
        self.assertEqual(
            ["first@example.test", "second@example.test"],
            [record["text"] for record in records],
        )
        self.assertEqual(
            ["directml", "cpu"],
            [record["attributes"]["executionProvider"] for record in records],
        )
        self.assertTrue(
            all(record["origin"]["provider"] == "hybrid-directml-cpu" for record in records)
        )
        assessment = read_jsonl(self.assessments)[0]
        self.assertEqual("hybrid-directml-cpu", assessment["provider"])
        self.assertEqual("hybrid", assessment["requestedProvider"])

    def test_hybrid_one_task_uses_gpu_lane_only(self) -> None:
        source = self._image()
        self._write_inventory(source)
        cpu = FakeRuntime("cpu")
        gpu = FakeRuntime("cuda")
        cpu.image_frames[source.name] = [FakeImage("single")]
        gpu.ocr_results["single"] = {
            "txts": ["single@example.test"],
            "boxes": [[[1, 1], [200, 1], [200, 20], [1, 20]]],
            "scores": [0.99],
        }

        run_pipeline(replace(self._config(), provider="hybrid"), HybridOcrRuntime(gpu, cpu))

        self.assertEqual(["single"], gpu.ocr_calls)
        self.assertEqual([], cpu.ocr_calls)
        record = read_jsonl(self.output)[0]
        self.assertEqual("hybrid-cuda-cpu", record["origin"]["provider"])
        self.assertEqual("cuda", record["attributes"]["executionProvider"])

    def test_hybrid_and_cpu_extract_exactly_equivalent_evidence_values(self) -> None:
        source = self._image("equivalent.tiff")
        self._write_inventory(source)
        result = {
            "txts": ["owner@example.test 198.51.100.9"],
            "boxes": [[[2, 3], [250, 3], [250, 30], [2, 30]]],
            "scores": [0.98765432],
        }
        cpu_only = FakeRuntime("cpu")
        cpu_only.image_frames[source.name] = [FakeImage("same:1"), FakeImage("same:2")]
        cpu_only.ocr_results.update({"same:1": result, "same:2": result})
        run_pipeline(self._config(), cpu_only)
        cpu_records = read_jsonl(self.output)

        cpu_lane = FakeRuntime("cpu")
        gpu_lane = FakeRuntime("directml")
        cpu_lane.image_frames[source.name] = [FakeImage("same:1"), FakeImage("same:2")]
        cpu_lane.ocr_results.update({"same:1": result, "same:2": result})
        gpu_lane.ocr_results.update({"same:1": result, "same:2": result})
        run_pipeline(
            replace(self._config(), provider="hybrid"),
            HybridOcrRuntime(gpu_lane, cpu_lane),
        )
        hybrid_records = read_jsonl(self.output)

        def evidence_values(records: list[dict[str, Any]]) -> list[tuple[Any, ...]]:
            return [
                (
                    record["text"],
                    record["location"],
                    record["attributes"]["box"],
                    record["attributes"]["confidence"],
                    record["attributes"]["sourceSha256"],
                )
                for record in records
            ]

        self.assertEqual(evidence_values(cpu_records), evidence_values(hybrid_records))


if __name__ == "__main__":
    unittest.main()
