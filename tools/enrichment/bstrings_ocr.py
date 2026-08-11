#!/usr/bin/env python3
"""Offline, provenance-preserving OCR adapter for the bstrings analysis workflow.

``--ocr-model-path`` names a small JSON manifest rather than an opaque model
archive.  The manifest is itself hash-pinned by the command line and must name
relative, individually hash-pinned model components::

    {
      "schemaVersion": 1,
      "modelId": "PaddlePaddle/PP-OCRv6-medium-onnx",
      "revision": "<bundle revision>",
      "detector": {"path": "detector.onnx", "sha256": "..."},
      "recognizer": {"path": "recognizer.onnx", "sha256": "..."},
      "classifier": {"path": "classifier.onnx", "sha256": "..."},
      "dictionary": {"path": "ppocrv6_dict.txt", "sha256": "..."}
    }

Detector, recognizer, classifier, and character-dictionary paths are required
and supplied explicitly to RapidOCR, so a valid run never downloads models.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import heapq
import hmac
import importlib.metadata
import ipaddress
import json
import math
import os
import sys
import tempfile
import threading
import time
import warnings
from collections import deque
from collections.abc import Callable, Iterable, Iterator, Sequence
from concurrent.futures import Future, ThreadPoolExecutor
from contextlib import suppress
from dataclasses import dataclass
from functools import partial
from pathlib import Path
from typing import Any, Protocol

SCHEMA_VERSION = 1
MAX_PATH_LIST_LINE_CHARACTERS = 32 * 1024
MAX_INPUT_MANIFEST_LINE_CHARACTERS = 256 * 1024
MAX_ROUTING_MANIFEST_LINE_CHARACTERS = 512 * 1024
MAX_JSONL_LINE_CHARACTERS = 16 * 1024 * 1024
MAX_MODEL_MANIFEST_BYTES = 1024 * 1024
HASH_CHUNK_BYTES = 4 * 1024 * 1024
ORT_PROVIDER_NAMES = {
    "cpu": "CPUExecutionProvider",
    "cuda": "CUDAExecutionProvider",
    "directml": "DmlExecutionProvider",
}
SINGLE_PROVIDER_PRIORITY = ("cuda", "directml", "cpu")
HYBRID_QUEUE_DEPTH_PER_LANE = 2
# The existing multipage path retains its measured weighting. Cross-source
# image scheduling uses the newer isolated same-model ratio without changing
# established multipage behavior.
HYBRID_SECOND_CPU_TASK = 13
HYBRID_GPU_TASK_WEIGHT = 6
HYBRID_CROSS_SOURCE_GPU_TASK_WEIGHT = 12
# Retain enough ordered results to keep the GPU fed while the first, slower CPU
# source runs. Live decoded images remain bounded separately by the lane gates.
HYBRID_CROSS_SOURCE_MAX_PENDING = HYBRID_SECOND_CPU_TASK + HYBRID_QUEUE_DEPTH_PER_LANE
HYBRID_SOURCE_MAX_PENDING = HYBRID_CROSS_SOURCE_MAX_PENDING
MAX_OCR_SESSION_THREADS = 256
CPU_PARALLEL_MAX_WORKERS = 4
CPU_PARALLEL_RESERVED_THREADS = 2
CPU_PARALLEL_TARGET_THREADS_PER_WORKER = 5
CPU_PARALLEL_QUEUE_DEPTH_PER_WORKER = 2
SPOOL_SORT_CHUNK_RECORDS = 256
SPOOL_SORT_CHUNK_BYTES = 8 * 1024 * 1024
SPOOL_SORT_MERGE_FAN_IN = 8
SELF_TEST_IMAGE_SIZE = (768, 160)
SELF_TEST_TEXT = "BSTRINGS OCR 42"
SELF_TEST_MAX_TEXT_CHARACTERS = 1024
SELF_TEST_COMPONENTS = (
    ("detector", "text_det"),
    ("classifier", "text_cls"),
    ("recognizer", "text_rec"),
)
KNOWN_IMAGE_EXTENSIONS = frozenset(
    {".bmp", ".gif", ".ico", ".jfif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp"}
)
AIRGAP_ENVIRONMENT = {
    "DO_NOT_TRACK": "1",
    "HF_HUB_DISABLE_TELEMETRY": "1",
    "HF_HUB_OFFLINE": "1",
    "PIP_DISABLE_PIP_VERSION_CHECK": "1",
    "PIP_NO_INDEX": "1",
    "PYTHONDONTWRITEBYTECODE": "1",
    "PYTHONNOUSERSITE": "1",
    "TRANSFORMERS_OFFLINE": "1",
    "UV_OFFLINE": "1",
}
_AIRGAP_ENABLED = False


class OcrError(RuntimeError):
    """Raised when complete, attributable OCR output cannot be produced."""


class AirgapNetworkError(OcrError):
    """Raised when an offline run attempts a non-loopback network operation."""


class EvidenceReadLease:
    """Hold a source open and deny Windows write/delete sharing during OCR use."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._handle: int | None = None
        self._portable_handle: Any | None = None

    def __enter__(self) -> EvidenceReadLease:
        if os.name != "nt":
            try:
                self._portable_handle = self.path.open("rb")
            except OSError as exc:
                raise OcrError("Could not acquire an evidence read lease for OCR") from exc
            return self
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        create_file = kernel32.CreateFileW
        create_file.argtypes = [
            ctypes.c_wchar_p,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_void_p,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_void_p,
        ]
        create_file.restype = ctypes.c_void_p
        handle = create_file(
            str(self.path),
            0x80000000,  # GENERIC_READ
            0x00000001,  # FILE_SHARE_READ only: deny write and delete sharing
            None,
            3,  # OPEN_EXISTING
            0x08000000,  # FILE_FLAG_SEQUENTIAL_SCAN
            None,
        )
        invalid_handle = ctypes.c_void_p(-1).value
        if handle in (None, invalid_handle):
            error = ctypes.get_last_error()
            raise OcrError(
                f"Could not acquire an immutable OCR evidence read lease (WinError {error})"
            )
        self._handle = int(handle)
        return self

    def __exit__(self, *_: object) -> None:
        if self._portable_handle is not None:
            self._portable_handle.close()
            self._portable_handle = None
        if self._handle is not None:
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
            kernel32.CloseHandle.restype = ctypes.c_int
            kernel32.CloseHandle(ctypes.c_void_p(self._handle))
            self._handle = None


@dataclass(frozen=True)
class ModelComponent:
    name: str
    path: Path
    sha256: str


@dataclass(frozen=True)
class ModelPack:
    manifest_path: Path
    manifest_sha256: str
    detector: ModelComponent
    recognizer: ModelComponent
    classifier: ModelComponent
    dictionary: ModelComponent

    @property
    def components(self) -> tuple[ModelComponent, ...]:
        return self.detector, self.recognizer, self.classifier, self.dictionary


@dataclass(frozen=True)
class OcrConfig:
    paths_from: Path | None
    output: Path | None
    assessments_output: Path | None
    ocr_executable: Path
    engine: str
    engine_version: str
    model_path: Path
    model_id: str
    model_revision: str
    model_sha256: str
    mode: str
    provider: str
    dpi: int
    threads: int
    max_pages: int
    max_pixels: int
    max_input_bytes: int
    max_output_bytes: int
    max_output_records: int
    max_text_characters: int
    pdf_text_min_characters: int
    self_test: bool
    airgap: bool
    input_manifest: Path | None = None
    routing_manifest: Path | None = None
    progress_total_files: int = 0


@dataclass(frozen=True)
class OcrHit:
    text: str
    confidence: float
    box: tuple[tuple[int | float, int | float], ...]


@dataclass(frozen=True)
class SourceResult:
    records: SourceRecordSpool | None
    assessment: dict[str, Any]


@dataclass(frozen=True)
class InputManifestEntry:
    path: str
    length: int
    sha256: str


@dataclass(frozen=True)
class RoutingManifestEntry:
    path: str
    length: int
    sha256: str
    decision_id: str
    scheduled_routes: tuple[str, ...]


class OcrRuntime(Protocol):
    engine_version: str
    pdfium_version: str
    resolved_provider: str
    requested_threads: int
    resolved_thread_counts: dict[str, int]
    resolved_worker_counts: dict[str, int]

    def iter_image_frames(
        self, path: Path, max_pages: int, max_pixels: int
    ) -> Iterator[tuple[int, Any]]: ...

    def image_frame_count(self, path: Path, max_pages: int, max_pixels: int) -> int: ...

    def open_pdf(self, path: Path) -> Any: ...

    def pdf_page_count(self, document: Any) -> int: ...

    def get_pdf_page(self, document: Any, index: int) -> Any: ...

    def pdf_page_size(self, page: Any) -> tuple[float, float]: ...

    def extract_pdf_text(self, page: Any, maximum_text: int) -> str: ...

    def render_pdf_page(self, page: Any, dpi: int, max_pixels: int) -> Any: ...

    def image_size(self, image: Any) -> tuple[int, int]: ...

    def raster_sha256(self, image: Any) -> str: ...

    def ocr(self, image: Any) -> Any: ...

    def run_inference_self_test(self) -> None: ...

    def close(self, value: Any | None = None) -> None: ...


@dataclass(frozen=True)
class PreparedRasterSource:
    source_file: str
    path: Path
    source_sha256: str
    source_size: int
    mtime_before: int
    image: Any
    route_decision_id: str | None
    evidence_lease: EvidenceReadLease


@dataclass(frozen=True)
class ScheduledRasterWork:
    future: Future[PageRecordSpool]
    lane: OcrRuntime
    image: Any


@dataclass(frozen=True)
class PendingRasterSource:
    source: PreparedRasterSource
    work: ScheduledRasterWork


def provider_candidates(requested: str, available: Iterable[str]) -> tuple[str, ...]:
    """Return provider candidates in deterministic preference order."""

    available_names = frozenset(available)
    if requested in ORT_PROVIDER_NAMES:
        if ORT_PROVIDER_NAMES[requested] not in available_names:
            raise OcrError("The requested OCR execution provider is unavailable")
        return (requested,)
    if requested == "auto":
        candidates = tuple(
            provider
            for provider in SINGLE_PROVIDER_PRIORITY
            if ORT_PROVIDER_NAMES[provider] in available_names
        )
        if not candidates:
            raise OcrError("No supported OCR execution provider is available")
        return candidates
    if requested == "hybrid":
        candidates = tuple(
            provider
            for provider in ("cuda", "directml")
            if ORT_PROVIDER_NAMES[provider] in available_names
        )
        if not candidates:
            raise OcrError("Hybrid OCR requires an available GPU execution provider")
        if ORT_PROVIDER_NAMES["cpu"] not in available_names:
            raise OcrError("Hybrid OCR requires an independent CPU execution provider")
        return candidates
    raise OcrError("Unsupported OCR execution provider")


def canonical_json(value: dict[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def resolve_session_threads(
    requested: int,
    provider: str,
    *,
    available_threads: int | None = None,
    hybrid_cpu_lane: bool = False,
) -> int:
    """Resolve a bounded ONNX session thread count without hardware ambiguity."""

    if type(requested) is not int or not 0 <= requested <= MAX_OCR_SESSION_THREADS:
        raise OcrError(f"OCR threads must be between 0 and {MAX_OCR_SESSION_THREADS}")
    if provider not in ORT_PROVIDER_NAMES:
        raise OcrError("Unsupported OCR execution provider")
    if requested:
        return requested
    if available_threads is None:
        process_count = getattr(os, "process_cpu_count", None)
        available_threads = process_count() if callable(process_count) else os.cpu_count()
    available = max(1, min(int(available_threads or 1), MAX_OCR_SESSION_THREADS))
    if provider != "cpu":
        return 1
    if hybrid_cpu_lane:
        return max(1, available - min(CPU_PARALLEL_RESERVED_THREADS, available - 1))
    return available


def resolve_cpu_worker_layout(
    requested: int,
    *,
    available_threads: int | None = None,
) -> tuple[int, int]:
    """Return bounded independent CPU workers and threads per ONNX session.

    ``--threads`` remains an exact per-session override. Automatic mode keeps a
    small coordinator/decoding reserve, then targets several useful intra-op
    threads per independent RapidOCR engine. The product of workers and session
    threads never exceeds that automatic capacity. An explicit override larger
    than the capacity deliberately retains one exact worker instead of silently
    changing the user's request.
    """

    if type(requested) is not int or not 0 <= requested <= MAX_OCR_SESSION_THREADS:
        raise OcrError(f"OCR threads must be between 0 and {MAX_OCR_SESSION_THREADS}")
    if available_threads is None:
        process_count = getattr(os, "process_cpu_count", None)
        available_threads = process_count() if callable(process_count) else os.cpu_count()
    available = max(1, min(int(available_threads or 1), MAX_OCR_SESSION_THREADS))
    reserved = min(CPU_PARALLEL_RESERVED_THREADS, max(0, available - 1))
    capacity = max(1, available - reserved)
    if requested:
        workers = max(1, min(CPU_PARALLEL_MAX_WORKERS, capacity // requested))
        return workers, requested
    workers = max(
        1,
        min(
            CPU_PARALLEL_MAX_WORKERS,
            (capacity + CPU_PARALLEL_TARGET_THREADS_PER_WORKER - 1)
            // CPU_PARALLEL_TARGET_THREADS_PER_WORKER,
        ),
    )
    return workers, max(1, capacity // workers)


def with_record_id(record: dict[str, Any]) -> dict[str, Any]:
    material = canonical_json(record).encode("utf-8")
    return {**record, "recordId": f"sha256:{hashlib.sha256(material).hexdigest()}"}


def _is_sha256(value: str) -> bool:
    return len(value) == 64 and all(character in "0123456789abcdefABCDEF" for character in value)


def sha256_file(path: Path, maximum_bytes: int | None = None) -> str:
    try:
        size = path.stat().st_size
    except OSError as exc:
        raise OcrError("A required local artifact is unavailable") from exc
    if maximum_bytes is not None and size > maximum_bytes:
        raise OcrError("A required local artifact exceeds its safety limit")
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            while chunk := handle.read(HASH_CHUNK_BYTES):
                digest.update(chunk)
    except OSError as exc:
        raise OcrError("A required local artifact could not be read") from exc
    return digest.hexdigest()


def _resolve_component(
    root: Path, name: str, value: Any, *, required_suffixes: frozenset[str]
) -> ModelComponent:
    if not isinstance(value, dict) or frozenset(value) != frozenset(("path", "sha256")):
        raise OcrError(f"OCR model manifest component '{name}' is incomplete")
    relative = value.get("path")
    expected_hash = value.get("sha256")
    if not isinstance(relative, str) or not relative or Path(relative).is_absolute():
        raise OcrError(f"OCR model manifest component '{name}' must use a relative path")
    if not isinstance(expected_hash, str) or not _is_sha256(expected_hash):
        raise OcrError(f"OCR model manifest component '{name}' has an invalid SHA-256")
    path = (root / relative).resolve()
    try:
        path.relative_to(root)
    except ValueError as exc:
        raise OcrError(f"OCR model manifest component '{name}' escapes the model pack") from exc
    if not path.is_file():
        raise OcrError(f"OCR model manifest component '{name}' is missing")
    if path.suffix.lower() not in required_suffixes:
        raise OcrError(f"OCR model manifest component '{name}' has an unsupported format")
    actual_hash = sha256_file(path)
    if not _constant_time_hex_equal(actual_hash, expected_hash):
        raise OcrError(f"OCR model manifest component '{name}' failed SHA-256 verification")
    return ModelComponent(name, path, expected_hash.lower())


def _constant_time_hex_equal(left: str, right: str) -> bool:
    if not _is_sha256(left) or not _is_sha256(right):
        return False
    return hmac.compare_digest(bytes.fromhex(left), bytes.fromhex(right))


def load_model_pack(
    manifest_path: Path,
    expected_sha256: str,
    expected_model_id: str,
    expected_revision: str,
) -> ModelPack:
    if not _is_sha256(expected_sha256):
        raise OcrError("The expected OCR model-pack SHA-256 is invalid")
    manifest_path = manifest_path.resolve()
    if not manifest_path.is_file():
        raise OcrError("The OCR model-pack manifest is missing")
    actual_manifest_hash = sha256_file(manifest_path, MAX_MODEL_MANIFEST_BYTES)
    if not _constant_time_hex_equal(actual_manifest_hash, expected_sha256):
        raise OcrError("The OCR model-pack manifest failed SHA-256 verification")
    try:
        raw = manifest_path.read_bytes()
        document = json.loads(raw.decode("utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise OcrError("The OCR model-pack manifest is not valid UTF-8 JSON") from exc
    if not isinstance(document, dict) or document.get("schemaVersion") != SCHEMA_VERSION:
        raise OcrError("The OCR model-pack manifest does not use schema version 1")
    allowed = {
        "schemaVersion",
        "modelId",
        "revision",
        "detector",
        "recognizer",
        "dictionary",
        "classifier",
    }
    unknown = frozenset(document).difference(allowed)
    if unknown:
        raise OcrError("The OCR model-pack manifest contains unsupported fields")
    if (
        document.get("modelId") != expected_model_id
        or document.get("revision") != expected_revision
    ):
        raise OcrError("The OCR model-pack identity does not match the verified configuration")
    root = manifest_path.parent.resolve()
    detector = _resolve_component(
        root, "detector", document.get("detector"), required_suffixes=frozenset((".onnx",))
    )
    recognizer = _resolve_component(
        root, "recognizer", document.get("recognizer"), required_suffixes=frozenset((".onnx",))
    )
    classifier = _resolve_component(
        root,
        "classifier",
        document.get("classifier"),
        required_suffixes=frozenset((".onnx",)),
    )
    dictionary = _resolve_component(
        root,
        "dictionary",
        document.get("dictionary"),
        required_suffixes=frozenset((".txt", ".dict")),
    )
    return ModelPack(
        manifest_path,
        expected_sha256.lower(),
        detector,
        recognizer,
        classifier,
        dictionary,
    )


def rapidocr_parameters(
    pack: ModelPack,
    provider: str,
    threads: int,
    *,
    engine_type: Any | None = None,
    sessions: dict[str, Any] | None = None,
) -> dict[str, Any]:
    if provider not in {"cpu", "cuda", "directml"}:
        raise OcrError("Unsupported OCR execution provider")
    params: dict[str, Any] = {
        "Global.use_det": True,
        "Global.use_rec": True,
        "Global.use_cls": True,
        "Global.log_level": "error",
        "Det.model_path": str(pack.detector.path),
        "Rec.model_path": str(pack.recognizer.path),
        "Cls.model_path": str(pack.classifier.path),
        "EngineConfig.onnxruntime.intra_op_num_threads": threads,
        "EngineConfig.onnxruntime.inter_op_num_threads": 1,
        "EngineConfig.onnxruntime.use_cuda": provider == "cuda",
        "EngineConfig.onnxruntime.use_dml": provider == "directml",
    }
    params["Rec.rec_keys_path"] = str(pack.dictionary.path)
    if engine_type is not None:
        params.update(
            {
                "Det.engine_type": engine_type,
                "Rec.engine_type": engine_type,
                "Cls.engine_type": engine_type,
            }
        )
    if sessions is not None:
        if frozenset(sessions) != frozenset(("Det", "Rec", "Cls")):
            raise OcrError("OCR session map is incomplete")
        params.update({f"{name}.session": session for name, session in sessions.items()})
    return params


class _ComponentCallProbe:
    """Observe RapidOCR stage entry while preserving the concrete component."""

    def __init__(self, component: Callable[..., Any], name: str, invoked: set[str]) -> None:
        self._component = component
        self._name = name
        self._invoked = invoked

    def __call__(self, *args: Any, **kwargs: Any) -> Any:
        self._invoked.add(self._name)
        return self._component(*args, **kwargs)

    def __getattr__(self, name: str) -> Any:
        return getattr(self._component, name)


class RapidOcrRuntime:
    """Lazy dependency boundary so unit tests never require OCR libraries."""

    def __init__(
        self,
        config: OcrConfig,
        pack: ModelPack,
        provider: str,
        *,
        hybrid_cpu_lane: bool = False,
        session_threads: int | None = None,
        cooperative_sessions: bool = False,
    ) -> None:
        if provider not in ORT_PROVIDER_NAMES:
            raise OcrError("RapidOCR requires one concrete execution provider")
        try:
            import cv2
            import numpy
            import onnxruntime
            import pypdfium2
            import rapidocr
            from PIL import Image, ImageDraw, ImageFont
            from rapidocr.main import DEFAULT_CFG_PATH
            from rapidocr.utils.parse_parameters import ParseParams
        except ImportError as exc:
            raise OcrError("The bundled OCR runtime is incomplete") from exc

        try:
            installed_version = str(
                getattr(rapidocr, "__version__", None) or importlib.metadata.version("rapidocr")
            )
        except importlib.metadata.PackageNotFoundError as exc:
            raise OcrError("The bundled RapidOCR version cannot be verified") from exc
        if installed_version != config.engine_version:
            raise OcrError("The bundled RapidOCR version does not match the verified configuration")
        self.engine_version = installed_version
        self.resolved_provider = provider
        self.requested_threads = config.threads
        default_threads = resolve_session_threads(
            config.threads,
            provider,
            hybrid_cpu_lane=hybrid_cpu_lane,
        )
        if session_threads is not None and (
            type(session_threads) is not int or not 1 <= session_threads <= MAX_OCR_SESSION_THREADS
        ):
            raise OcrError("A parallel OCR session reported an invalid thread count")
        if config.threads and session_threads is not None and session_threads != config.threads:
            raise OcrError("A parallel OCR session changed an explicit thread override")
        self.resolved_threads = session_threads or default_threads
        self.resolved_thread_counts = {provider: self.resolved_threads}
        self.resolved_worker_counts = {provider: 1}
        try:
            self.pdfium_version = str(
                getattr(pypdfium2, "__version__", None) or importlib.metadata.version("pypdfium2")
            )
        except importlib.metadata.PackageNotFoundError:
            self.pdfium_version = "unknown"
        self._cv2 = cv2
        self._numpy = numpy
        self._pdfium = pypdfium2
        self._Image = Image
        self._ImageDraw = ImageDraw
        self._ImageFont = ImageFont
        sessions = {
            "Det": self._create_session(
                onnxruntime,
                pack.detector.path,
                provider,
                self.resolved_threads,
                cooperative_sessions,
            ),
            "Rec": self._create_session(
                onnxruntime,
                pack.recognizer.path,
                provider,
                self.resolved_threads,
                cooperative_sessions,
            ),
            "Cls": self._create_session(
                onnxruntime,
                pack.classifier.path,
                provider,
                self.resolved_threads,
                cooperative_sessions,
            ),
        }

        class LocalSessionRapidOCR(rapidocr.RapidOCR):
            """Enable RapidOCR 3.9.2's documented custom-session configuration."""

            def _load_config(self, config_path: str | None, params: dict[str, Any] | None) -> Any:
                selected_path = (
                    Path(config_path)
                    if config_path is not None and Path(config_path).is_file()
                    else DEFAULT_CFG_PATH
                )
                cfg = ParseParams.load(selected_path)
                # RapidOCR 3.9.2 supports `*.session`, but its default OmegaConf
                # object policy rejects InferenceSession unless enabled first.
                cfg._set_flag("allow_objects", True)
                if params:
                    cfg = ParseParams.update_batch(cfg, params)
                if cfg.Global.model_root_dir is None:
                    cfg.Global.model_root_dir = DEFAULT_CFG_PATH.parent / "models"
                return cfg

        try:
            self._engine = LocalSessionRapidOCR(
                params=rapidocr_parameters(
                    pack,
                    provider,
                    self.resolved_threads,
                    engine_type=rapidocr.EngineType.ONNXRUNTIME,
                    sessions=sessions,
                )
            )
        except Exception as exc:
            raise OcrError("RapidOCR could not initialize from the local model pack") from exc

    @staticmethod
    def _create_session(
        onnxruntime: Any,
        model_path: Path,
        provider: str,
        threads: int,
        cooperative_sessions: bool = False,
    ) -> Any:
        provider_name = {
            "cpu": "CPUExecutionProvider",
            "cuda": "CUDAExecutionProvider",
            "directml": "DmlExecutionProvider",
        }[provider]
        available = set(onnxruntime.get_available_providers())
        if provider_name not in available:
            raise OcrError("The requested OCR execution provider is unavailable")
        options = onnxruntime.SessionOptions()
        options.graph_optimization_level = onnxruntime.GraphOptimizationLevel.ORT_ENABLE_ALL
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        if cooperative_sessions:
            # Independent lanes have independent ONNX thread pools. Prevent an
            # idle stage from spin-waiting while another lane needs the budget.
            options.add_session_config_entry("session.intra_op.allow_spinning", "0")
            options.add_session_config_entry("session.inter_op.allow_spinning", "0")
        if provider == "directml":
            options.enable_mem_pattern = False
            options.execution_mode = onnxruntime.ExecutionMode.ORT_SEQUENTIAL
        providers = [provider_name]
        if provider_name != "CPUExecutionProvider":
            providers.append("CPUExecutionProvider")
        try:
            session = onnxruntime.InferenceSession(
                str(model_path), sess_options=options, providers=providers
            )
        except Exception as exc:
            raise OcrError("A local OCR model could not initialize") from exc
        active = session.get_providers()
        if not active or active[0] != provider_name:
            raise OcrError("The OCR runtime silently changed execution provider")
        return session

    def configure_parallel_preprocessing(self) -> None:
        """Disable OpenCV's process-wide inner pool before outer lanes start."""

        try:
            self._cv2.setNumThreads(1)
            configured = int(self._cv2.getNumThreads())
        except Exception as exc:
            raise OcrError("OpenCV parallel preprocessing could not be bounded") from exc
        if configured != 1:
            raise OcrError("OpenCV parallel preprocessing did not honor its thread bound")

    def image_frame_count(self, path: Path, max_pages: int, max_pixels: int) -> int:
        try:
            source = self._Image.open(path)
        except Exception as exc:
            raise OcrError("A recognized image could not be decoded") from exc
        try:
            frame_count = int(getattr(source, "n_frames", 1))
            if frame_count <= 0 or frame_count > max_pages:
                raise OcrError("An image exceeds the configured frame limit")
            for index in range(frame_count):
                source.seek(index)
                width, height = source.size
                _validate_pixel_count(width, height, max_pixels)
            return frame_count
        except OcrError:
            raise
        except Exception as exc:
            raise OcrError("A recognized image could not be inspected safely") from exc
        finally:
            source.close()

    def iter_image_frames(
        self, path: Path, max_pages: int, max_pixels: int
    ) -> Iterator[tuple[int, Any]]:
        try:
            source = self._Image.open(path)
        except Exception as exc:
            raise OcrError("A recognized image could not be decoded") from exc
        try:
            frame_count = int(getattr(source, "n_frames", 1))
            if frame_count <= 0 or frame_count > max_pages:
                raise OcrError("An image exceeds the configured frame limit")
            for index in range(frame_count):
                source.seek(index)
                width, height = source.size
                _validate_pixel_count(width, height, max_pixels)
                with warnings.catch_warnings():
                    warnings.simplefilter("error", self._Image.DecompressionBombWarning)
                    try:
                        frame = source.convert("RGB").copy()
                        frame.load()
                    except Exception as exc:
                        raise OcrError("An image frame could not be decoded safely") from exc
                yield index + 1, frame
        finally:
            source.close()

    def open_pdf(self, path: Path) -> Any:
        try:
            return self._pdfium.PdfDocument(str(path))
        except Exception as exc:
            raise OcrError("A recognized PDF could not be opened") from exc

    def pdf_page_count(self, document: Any) -> int:
        return len(document)

    def get_pdf_page(self, document: Any, index: int) -> Any:
        return document[index]

    def pdf_page_size(self, page: Any) -> tuple[float, float]:
        width, height = page.get_size()
        return float(width), float(height)

    def extract_pdf_text(self, page: Any, maximum_text: int) -> str:
        text_page = None
        try:
            text_page = page.get_textpage()
            character_count = int(text_page.count_chars())
            if character_count < 0 or character_count > maximum_text:
                raise OcrError("A PDF text layer exceeds the configured text limit")
            text = text_page.get_text_range(index=0, count=character_count, errors="strict")
        except Exception as exc:
            if isinstance(exc, OcrError):
                raise
            raise OcrError("PDF text-layer extraction failed") from exc
        finally:
            self.close(text_page)
        if not isinstance(text, str):
            raise OcrError("PDF text-layer extraction returned invalid text")
        return text

    def render_pdf_page(self, page: Any, dpi: int, max_pixels: int) -> Any:
        width_points, height_points = self.pdf_page_size(page)
        width = max(1, math.ceil(width_points * dpi / 72.0))
        height = max(1, math.ceil(height_points * dpi / 72.0))
        _validate_pixel_count(width, height, max_pixels)
        bitmap = None
        try:
            bitmap = page.render(scale=dpi / 72.0)
            image = bitmap.to_pil().convert("RGB").copy()
            image.load()
        except Exception as exc:
            raise OcrError("A PDF page could not be rendered safely") from exc
        finally:
            self.close(bitmap)
        actual_width, actual_height = image.size
        _validate_pixel_count(actual_width, actual_height, max_pixels)
        return image

    def image_size(self, image: Any) -> tuple[int, int]:
        width, height = image.size
        return int(width), int(height)

    def raster_sha256(self, image: Any) -> str:
        width, height = self.image_size(image)
        digest = hashlib.sha256()
        digest.update(f"RGB\0{width}\0{height}\0".encode("ascii"))
        digest.update(image.tobytes())
        return digest.hexdigest()

    def ocr(self, image: Any) -> Any:
        try:
            return self._engine(self._numpy.asarray(image))
        except Exception as exc:
            raise OcrError("RapidOCR inference failed") from exc

    def _create_self_test_image(self) -> Any:
        image = self._Image.new("RGB", SELF_TEST_IMAGE_SIZE, color="white")
        try:
            drawing = self._ImageDraw.Draw(image)
            font = self._ImageFont.load_default(size=52)
            drawing.text((24, 42), SELF_TEST_TEXT, fill="black", font=font)
            image.load()
            return image
        except Exception as exc:
            self.close(image)
            raise OcrError("OCR inference self-test raster creation failed") from exc

    def run_inference_self_test(self) -> None:
        invoked: set[str] = set()
        originals: dict[str, Callable[..., Any]] = {}
        image = None
        try:
            for component_name, attribute_name in SELF_TEST_COMPONENTS:
                component = getattr(self._engine, attribute_name, None)
                if not callable(component):
                    raise OcrError(
                        f"OCR inference self-test cannot observe the {component_name} stage"
                    )
                originals[attribute_name] = component
                setattr(
                    self._engine,
                    attribute_name,
                    _ComponentCallProbe(component, component_name, invoked),
                )

            image = self._create_self_test_image()
            width, height = self.image_size(image)
            hits = parse_ocr_result(
                self.ocr(image),
                width,
                height,
                SELF_TEST_MAX_TEXT_CHARACTERS,
            )
            missing = [name for name, _ in SELF_TEST_COMPONENTS if name not in invoked]
            if missing:
                raise OcrError(
                    "OCR inference self-test did not execute every model stage: "
                    + ", ".join(missing)
                )
            if not hits:
                raise OcrError("OCR inference self-test recovered no text")
        except OcrError as exc:
            raise OcrError(f"{self.resolved_provider} OCR inference self-test failed") from exc
        except Exception as exc:
            raise OcrError(f"{self.resolved_provider} OCR inference self-test failed") from exc
        finally:
            for attribute_name, component in originals.items():
                setattr(self._engine, attribute_name, component)
            self.close(image)

    def close(self, value: Any | None = None) -> None:
        if value is None:
            engine_close = getattr(getattr(self, "_engine", None), "close", None)
            if callable(engine_close):
                with suppress(Exception):
                    engine_close()
            return
        close = getattr(value, "close", None)
        if callable(close):
            with suppress(Exception):
                close()


class CpuParallelOcrRuntime:
    """Independent same-model CPU engines for bounded source-level parallelism."""

    def __init__(self, lanes: Sequence[OcrRuntime]) -> None:
        if len(lanes) < 2 or len(lanes) > CPU_PARALLEL_MAX_WORKERS:
            raise OcrError("Parallel CPU OCR requires a bounded set of independent workers")
        first = lanes[0]
        if any(lane.resolved_provider != "cpu" for lane in lanes):
            raise OcrError("Parallel CPU OCR requires CPU-only workers")
        if any(lane.engine_version != first.engine_version for lane in lanes):
            raise OcrError("Parallel CPU OCR worker versions do not match")
        if any(lane.pdfium_version != first.pdfium_version for lane in lanes):
            raise OcrError("Parallel CPU OCR PDF renderers do not match")
        if any(lane.requested_threads != first.requested_threads for lane in lanes):
            raise OcrError("Parallel CPU OCR worker thread requests do not match")
        counts = [lane.resolved_thread_counts for lane in lanes]
        if any(count != counts[0] for count in counts) or set(counts[0]) != {"cpu"}:
            raise OcrError("Parallel CPU OCR worker thread resolutions do not match")
        self.lanes = tuple(lanes)
        self.primary_runtime = first
        self.engine_version = first.engine_version
        self.pdfium_version = first.pdfium_version
        self.resolved_provider = "cpu"
        self.requested_threads = first.requested_threads
        self.resolved_thread_counts = dict(counts[0])
        self.resolved_worker_counts = {"cpu": len(self.lanes)}
        self.configure_parallel_preprocessing()

    def __getattr__(self, name: str) -> Any:
        return getattr(self.primary_runtime, name)

    def configure_parallel_preprocessing(self) -> None:
        configure = getattr(self.primary_runtime, "configure_parallel_preprocessing", None)
        if callable(configure):
            configure()

    def ocr(self, image: Any) -> Any:
        return self.primary_runtime.ocr(image)

    def run_inference_self_test(self) -> None:
        for lane in self.lanes:
            lane.run_inference_self_test()

    def close(self, value: Any | None = None) -> None:
        if value is not None:
            self.primary_runtime.close(value)
            return
        for lane in self.lanes:
            lane.close()


class HybridOcrRuntime:
    """Independent GPU and CPU RapidOCR engines with CPU-side preparation."""

    def __init__(self, gpu_runtime: OcrRuntime, cpu_runtime: OcrRuntime) -> None:
        if gpu_runtime.resolved_provider not in {"cuda", "directml"}:
            raise OcrError("Hybrid OCR requires a verified GPU runtime")
        if cpu_runtime.resolved_provider != "cpu":
            raise OcrError("Hybrid OCR requires a verified CPU-only runtime")
        if gpu_runtime.engine_version != cpu_runtime.engine_version:
            raise OcrError("Hybrid OCR lane versions do not match")
        if gpu_runtime.requested_threads != cpu_runtime.requested_threads:
            raise OcrError("Hybrid OCR lane thread requests do not match")
        self.gpu_runtime = gpu_runtime
        self.cpu_runtime = cpu_runtime
        self.engine_version = gpu_runtime.engine_version
        self.pdfium_version = cpu_runtime.pdfium_version
        self.gpu_provider = gpu_runtime.resolved_provider
        self.resolved_provider = f"hybrid-{self.gpu_provider}-cpu"
        self.requested_threads = gpu_runtime.requested_threads
        self.resolved_thread_counts = dict(
            sorted(
                {
                    **gpu_runtime.resolved_thread_counts,
                    **cpu_runtime.resolved_thread_counts,
                }.items()
            )
        )
        self.resolved_worker_counts = dict(
            sorted(
                {
                    **gpu_runtime.resolved_worker_counts,
                    **cpu_runtime.resolved_worker_counts,
                }.items()
            )
        )

    def iter_image_frames(
        self, path: Path, max_pages: int, max_pixels: int
    ) -> Iterator[tuple[int, Any]]:
        return self.cpu_runtime.iter_image_frames(path, max_pages, max_pixels)

    def image_frame_count(self, path: Path, max_pages: int, max_pixels: int) -> int:
        return self.cpu_runtime.image_frame_count(path, max_pages, max_pixels)

    def open_pdf(self, path: Path) -> Any:
        return self.cpu_runtime.open_pdf(path)

    def pdf_page_count(self, document: Any) -> int:
        return self.cpu_runtime.pdf_page_count(document)

    def get_pdf_page(self, document: Any, index: int) -> Any:
        return self.cpu_runtime.get_pdf_page(document, index)

    def pdf_page_size(self, page: Any) -> tuple[float, float]:
        return self.cpu_runtime.pdf_page_size(page)

    def extract_pdf_text(self, page: Any, maximum_text: int) -> str:
        return self.cpu_runtime.extract_pdf_text(page, maximum_text)

    def render_pdf_page(self, page: Any, dpi: int, max_pixels: int) -> Any:
        return self.cpu_runtime.render_pdf_page(page, dpi, max_pixels)

    def image_size(self, image: Any) -> tuple[int, int]:
        return self.cpu_runtime.image_size(image)

    def raster_sha256(self, image: Any) -> str:
        return self.cpu_runtime.raster_sha256(image)

    def ocr(self, image: Any) -> Any:
        # The scheduler uses the concrete lanes. This fallback keeps one-task
        # callers GPU-first without ever sharing a GPU session concurrently.
        return self.gpu_runtime.ocr(image)

    def configure_parallel_preprocessing(self) -> None:
        configure = getattr(self.cpu_runtime, "configure_parallel_preprocessing", None)
        if callable(configure):
            configure()

    def run_inference_self_test(self) -> None:
        self.gpu_runtime.run_inference_self_test()
        self.cpu_runtime.run_inference_self_test()

    def close(self, value: Any | None = None) -> None:
        if value is not None:
            self.cpu_runtime.close(value)
            return
        self.gpu_runtime.close()
        self.cpu_runtime.close()


def _validate_pixel_count(width: int | float, height: int | float, maximum: int) -> None:
    if (
        width <= 0
        or height <= 0
        or not math.isfinite(float(width))
        or not math.isfinite(float(height))
    ):
        raise OcrError("An image or page has invalid dimensions")
    if int(math.ceil(float(width))) * int(math.ceil(float(height))) > maximum:
        raise OcrError("An image or rendered page exceeds the configured pixel limit")


def _as_list(value: Any) -> list[Any]:
    if value is None:
        return []
    to_list = getattr(value, "tolist", None)
    if callable(to_list):
        value = to_list()
    if isinstance(value, tuple):
        return list(value)
    if isinstance(value, list):
        return value
    raise OcrError("RapidOCR returned an unsupported result collection")


def _result_field(result: Any, name: str) -> Any:
    if isinstance(result, dict):
        return result.get(name)
    return getattr(result, name, None)


def _number(value: Any) -> int | float:
    if isinstance(value, bool):
        raise OcrError("RapidOCR returned a Boolean coordinate")
    try:
        numeric = float(value)
    except (TypeError, ValueError) as exc:
        raise OcrError("RapidOCR returned an invalid coordinate") from exc
    if not math.isfinite(numeric):
        raise OcrError("RapidOCR returned a non-finite coordinate")
    rounded = round(numeric, 4)
    return int(rounded) if rounded.is_integer() else rounded


def normalize_box(
    value: Any, width: int, height: int
) -> tuple[tuple[int | float, int | float], ...]:
    raw = _as_list(value)
    if len(raw) == 4 and all(not isinstance(item, (list, tuple)) for item in raw):
        left, top, right, bottom = (_number(item) for item in raw)
        raw = [[left, top], [right, top], [right, bottom], [left, bottom]]
    elif len(raw) == 8 and all(not isinstance(item, (list, tuple)) for item in raw):
        raw = [[raw[index], raw[index + 1]] for index in range(0, 8, 2)]
    if len(raw) != 4:
        raise OcrError("RapidOCR returned a box without four corners")
    points: list[tuple[int | float, int | float]] = []
    for point in raw:
        pair = _as_list(point)
        if len(pair) != 2:
            raise OcrError("RapidOCR returned an invalid box corner")
        points.append((_number(pair[0]), _number(pair[1])))
    xs = [float(point[0]) for point in points]
    ys = [float(point[1]) for point in points]
    tolerance = 2.0
    if (
        min(xs) < -tolerance
        or min(ys) < -tolerance
        or max(xs) > width + tolerance
        or max(ys) > height + tolerance
    ):
        raise OcrError("RapidOCR returned a box outside the rendered image")
    if max(xs) <= min(xs) or max(ys) <= min(ys):
        raise OcrError("RapidOCR returned a degenerate box")
    return tuple(points)


def _iter_ocr_hits(result: Any, width: int, height: int, maximum_text: int) -> Iterator[OcrHit]:
    if result is None:
        return
    if isinstance(result, tuple) and len(result) == 2:
        result = result[0]
    texts = _result_field(result, "txts")
    boxes = _result_field(result, "boxes")
    scores = _result_field(result, "scores")
    if texts is not None or boxes is not None or scores is not None:
        text_values = _as_list(texts)
        box_values = _as_list(boxes)
        score_values = _as_list(scores)
        if not (len(text_values) == len(box_values) == len(score_values)):
            raise OcrError("RapidOCR returned mismatched text, box, and confidence counts")
        rows: Iterable[tuple[Any, Any, Any]] = zip(
            box_values, text_values, score_values, strict=True
        )
    else:

        def legacy_rows() -> Iterator[tuple[Any, Any, Any]]:
            for row in _as_list(result):
                values = _as_list(row)
                if len(values) < 3:
                    raise OcrError("RapidOCR returned an unsupported result row")
                yield values[0], values[1], values[2]

        rows = legacy_rows()

    for box_value, text_value, score_value in rows:
        if not isinstance(text_value, str):
            raise OcrError("RapidOCR returned non-text output")
        if not text_value.strip():
            continue
        if "\x00" in text_value or len(text_value) > maximum_text:
            raise OcrError("RapidOCR returned text outside the configured safety limits")
        try:
            confidence = float(score_value)
        except (TypeError, ValueError) as exc:
            raise OcrError("RapidOCR returned an invalid confidence") from exc
        if not math.isfinite(confidence) or not 0.0 <= confidence <= 1.0:
            raise OcrError("RapidOCR returned a confidence outside 0..1")
        yield OcrHit(
            text=text_value,
            confidence=round(confidence, 8),
            box=normalize_box(box_value, width, height),
        )


def parse_ocr_result(result: Any, width: int, height: int, maximum_text: int) -> list[OcrHit]:
    hits = list(_iter_ocr_hits(result, width, height, maximum_text))
    hits.sort(key=_hit_sort_key)
    return hits


def _hit_sort_key(hit: OcrHit) -> tuple[Any, ...]:
    xs = [float(point[0]) for point in hit.box]
    ys = [float(point[1]) for point in hit.box]
    return (min(ys), min(xs), max(ys), max(xs), hit.text, hit.confidence)


def _format_number(value: int | float) -> str:
    if isinstance(value, int):
        return str(value)
    return f"{value:.4f}".rstrip("0").rstrip(".")


def _location_value(
    page_number: int, box: tuple[tuple[int | float, int | float], ...], kind: str
) -> str:
    coordinates = ",".join(_format_number(value) for point in box for value in point)
    return f"page={page_number:08d};box={coordinates};source={kind}"


def _component_attributes(pack: ModelPack) -> dict[str, str]:
    return {f"{component.name}Sha256": component.sha256 for component in pack.components}


def make_record(
    *,
    config: OcrConfig,
    pack: ModelPack,
    source_file: str,
    source_sha256: str,
    source_size: int,
    route_decision_id: str | None,
    origin_kind: str,
    location_kind: str,
    page_number: int,
    box: tuple[tuple[int | float, int | float], ...],
    confidence: float | None,
    text: str,
    runtime_sha256: str,
    render_sha256: str | None,
    render_dpi: int | None,
    coordinate_space: str,
    width: int | float,
    height: int | float,
    pdfium_version: str,
    resolved_provider: str,
    execution_provider: str,
    requested_threads: int,
    resolved_thread_counts: dict[str, int],
    resolved_worker_counts: dict[str, int],
) -> dict[str, Any]:
    if origin_kind not in {"ocr", "pdf-text"}:
        raise OcrError("Unsupported OCR evidence origin")
    if not text or "\x00" in text or len(text) > config.max_text_characters:
        raise OcrError("Extracted text is outside the configured safety limits")
    attributes: dict[str, Any] = {
        "sourceSha256": source_sha256,
        "sourceSize": source_size,
        "routeDecisionId": route_decision_id,
        "pageNumber": page_number,
        "box": [list(point) for point in box],
        "confidence": confidence,
        "modelPackSha256": pack.manifest_sha256,
        "runtimeSha256": runtime_sha256,
        "renderSha256": render_sha256,
        "pageSha256": render_sha256,
        "renderDpi": render_dpi,
        "coordinateSpace": coordinate_space,
        "pixelWidth": width if coordinate_space != "pdf-points" else None,
        "pixelHeight": height if coordinate_space != "pdf-points" else None,
        "pageWidthPoints": width if coordinate_space == "pdf-points" else None,
        "pageHeightPoints": height if coordinate_space == "pdf-points" else None,
        "requestedProvider": config.provider,
        "provider": resolved_provider,
        "executionProvider": execution_provider,
        "requestedThreads": requested_threads,
        "resolvedThreadCounts": dict(sorted(resolved_thread_counts.items())),
        "resolvedWorkerCounts": dict(sorted(resolved_worker_counts.items())),
        **_component_attributes(pack),
    }
    if origin_kind == "pdf-text":
        attributes.update(
            {
                "textLayerExtractor": "pypdfium2",
                "textLayerExtractorVersion": pdfium_version,
            }
        )
    record = {
        "schemaVersion": SCHEMA_VERSION,
        "recordType": "string",
        "text": text,
        "sourceFile": source_file,
        "location": {
            "kind": location_kind,
            "value": _location_value(page_number, box, origin_kind),
        },
        "origin": {
            "extractor": config.engine,
            "version": config.engine_version,
            "kind": origin_kind,
            "model": config.model_id,
            "revision": config.model_revision,
            "modelSha256": config.model_sha256.lower(),
            "provider": resolved_provider,
        },
        "attributes": {key: value for key, value in attributes.items() if value is not None},
    }
    return with_record_id(record)


def _identity_fields(config: OcrConfig) -> dict[str, Any]:
    return {
        "engine": config.engine,
        "engineVersion": config.engine_version,
        "model": config.model_id,
        "revision": config.model_revision,
        "modelSha256": config.model_sha256.lower(),
    }


def make_assessment(
    *,
    config: OcrConfig,
    pack: ModelPack,
    source_file: str,
    status: str,
    pages: int,
    string_records: int,
    pdf_text_records: int,
    ocr_records: int,
    source_sha256: str | None,
    source_size: int | None,
    route_decision_id: str | None,
    runtime_sha256: str,
    rendered_pages: int,
    resolved_provider: str,
    requested_threads: int,
    resolved_thread_counts: dict[str, int],
    resolved_worker_counts: dict[str, int],
) -> dict[str, Any]:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "recordType": "ocr-assessment",
        "sourceFile": source_file,
        "status": status,
        "pages": pages,
        "stringRecords": string_records,
        **_identity_fields(config),
        "modelPackSha256": pack.manifest_sha256,
        **_component_attributes(pack),
        "sourceSha256": source_sha256,
        "sourceSize": source_size,
        **({"routeDecisionId": route_decision_id} if route_decision_id is not None else {}),
        "runtimeSha256": runtime_sha256,
        "mode": config.mode,
        "requestedProvider": config.provider,
        "provider": resolved_provider,
        "requestedThreads": requested_threads,
        "resolvedThreadCounts": dict(sorted(resolved_thread_counts.items())),
        "resolvedWorkerCounts": dict(sorted(resolved_worker_counts.items())),
        "dpi": config.dpi,
        "renderedPages": rendered_pages,
        "pdfTextRecords": pdf_text_records,
        "ocrRecords": ocr_records,
    }


def _sniff_source(path: Path) -> str:
    try:
        with path.open("rb") as handle:
            header = handle.read(16)
    except OSError as exc:
        raise OcrError("An inventory item could not be read") from exc
    lowered_suffix = path.suffix.lower()
    if header.startswith(b"%PDF-") or lowered_suffix == ".pdf":
        return "pdf"
    image_magic = (
        header.startswith(b"\x89PNG\r\n\x1a\n")
        or header.startswith(b"\xff\xd8\xff")
        or header.startswith((b"GIF87a", b"GIF89a"))
        or header.startswith((b"II*\x00", b"MM\x00*"))
        or header.startswith(b"BM")
        or (header.startswith(b"RIFF") and header[8:12] == b"WEBP")
        or header.startswith(b"\x00\x00\x01\x00")
    )
    if image_magic or lowered_suffix in KNOWN_IMAGE_EXTENSIONS:
        return "image"
    return "not-applicable"


def _source_snapshot(path: Path) -> tuple[int, int]:
    try:
        stat = path.stat()
    except OSError as exc:
        raise OcrError("An inventory item is unavailable") from exc
    if not path.is_file():
        raise OcrError("An inventory item is not a regular file")
    return stat.st_size, stat.st_mtime_ns


def _verify_source_hash_after_use(path: Path, expected_sha256: str | None) -> None:
    if expected_sha256 is None:
        return
    if not hmac.compare_digest(sha256_file(path), expected_sha256):
        raise OcrError("An evidence input changed during OCR examination")


def _pdf_text_is_suspicious(text: str, minimum_characters: int) -> bool:
    stripped = text.strip()
    if len(stripped) < minimum_characters:
        return True
    replacement_count = text.count("\ufffd")
    control_count = sum(ord(character) < 32 and character not in "\t\r\n" for character in text)
    denominator = max(1, len(text))
    return replacement_count / denominator > 0.01 or control_count / denominator > 0.01


def _full_page_box(width: float, height: float) -> tuple[tuple[int | float, int | float], ...]:
    return ((0, 0), (_number(width), 0), (_number(width), _number(height)), (0, _number(height)))


def _records_for_raster(
    *,
    runtime: OcrRuntime,
    config: OcrConfig,
    pack: ModelPack,
    source_file: str,
    source_sha256: str,
    source_size: int,
    route_decision_id: str | None,
    page_number: int,
    image: Any,
    location_kind: str,
    runtime_sha256: str,
    render_dpi: int | None,
    resolved_provider: str,
    execution_provider: str,
    requested_threads: int,
    resolved_thread_counts: dict[str, int],
    resolved_worker_counts: dict[str, int],
    budget: OutputBudget,
    spool_directory: Path,
) -> PageRecordSpool:
    width, height = runtime.image_size(image)
    _validate_pixel_count(width, height, config.max_pixels)
    render_sha256 = runtime.raster_sha256(image)
    records = PageRecordSpool(spool_directory, budget)
    try:
        for hit in _iter_ocr_hits(runtime.ocr(image), width, height, config.max_text_characters):
            records.append(
                make_record(
                    config=config,
                    pack=pack,
                    source_file=source_file,
                    source_sha256=source_sha256,
                    source_size=source_size,
                    route_decision_id=route_decision_id,
                    origin_kind="ocr",
                    location_kind=location_kind,
                    page_number=page_number,
                    box=hit.box,
                    confidence=hit.confidence,
                    text=hit.text,
                    runtime_sha256=runtime_sha256,
                    render_sha256=render_sha256,
                    render_dpi=render_dpi,
                    coordinate_space="render-pixels",
                    width=width,
                    height=height,
                    pdfium_version=runtime.pdfium_version,
                    resolved_provider=resolved_provider,
                    execution_provider=execution_provider,
                    requested_threads=requested_threads,
                    resolved_thread_counts=resolved_thread_counts,
                    resolved_worker_counts=resolved_worker_counts,
                )
            )
        return records
    except BaseException:
        records.close()
        raise


class OutputBudget:
    """Reserve the exact canonical JSONL bytes before publication."""

    def __init__(self, maximum_records: int, maximum_bytes: int) -> None:
        self.maximum_records = maximum_records
        self.maximum_bytes = maximum_bytes
        self.records = 0
        self.bytes = 0
        self._lock = threading.Lock()

    def reserve(self, value: dict[str, Any], *, is_record: bool) -> str:
        line = canonical_json(value)
        if len(line) > MAX_JSONL_LINE_CHARACTERS:
            raise OcrError("An OCR JSONL record exceeds the line safety limit")
        encoded_bytes = len(line.encode("utf-8")) + 1
        with self._lock:
            next_records = self.records + (1 if is_record else 0)
            next_bytes = self.bytes + encoded_bytes
            if next_records > self.maximum_records:
                raise OcrError("OCR output exceeds the configured record limit")
            if next_bytes > self.maximum_bytes:
                raise OcrError("OCR outputs exceed the configured byte limit")
            self.records = next_records
            self.bytes = next_bytes
        return line


def _create_text_spool(directory: Path, prefix: str) -> tuple[Path, Any]:
    directory.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=prefix, suffix=".jsonl", dir=directory)
    path = Path(name)
    try:
        handle = os.fdopen(descriptor, "w", encoding="utf-8", newline="\n")
    except BaseException:
        with suppress(OSError):
            os.close(descriptor)
        with suppress(FileNotFoundError):
            path.unlink()
        raise
    return path, handle


def _iter_spool_records(path: Path, description: str) -> Iterator[tuple[dict[str, Any], str]]:
    try:
        handle = path.open("r", encoding="utf-8", errors="strict", newline=None)
    except OSError as exc:
        raise OcrError(f"{description} could not be reopened") from exc
    with handle:
        while True:
            line = handle.readline(MAX_JSONL_LINE_CHARACTERS + 2)
            if line == "":
                break
            value = line.rstrip("\r\n")
            if len(value) > MAX_JSONL_LINE_CHARACTERS:
                raise OcrError(f"{description} contains a line above the safety limit")
            try:
                record = json.loads(value)
            except (UnicodeError, json.JSONDecodeError) as exc:
                raise OcrError(f"{description} contains invalid JSON") from exc
            if not isinstance(record, dict):
                raise OcrError(f"{description} contains a non-record value")
            yield record, value


class PageRecordSpool:
    """Budgeted page records with bounded deterministic external sorting."""

    def __init__(self, directory: Path, budget: OutputBudget) -> None:
        self.path, self._handle = _create_text_spool(directory, "bstrings-ocr-page.")
        self._budget = budget
        self._closed = False

    def append(self, record: dict[str, Any]) -> None:
        if self._closed:
            raise OcrError("OCR page spool is closed")
        line = self._budget.reserve(record, is_record=True)
        self._handle.write(line)
        self._handle.write("\n")

    def _write_sorted_chunk(self, items: list[tuple[tuple[Any, ...], str]]) -> Path:
        items.sort(key=lambda item: item[0])
        path, handle = _create_text_spool(self.path.parent, "bstrings-ocr-sort.")
        try:
            with handle:
                for _key, line in items:
                    handle.write(line)
                    handle.write("\n")
            return path
        except BaseException:
            with suppress(Exception):
                handle.close()
            with suppress(FileNotFoundError):
                path.unlink()
            raise

    @staticmethod
    def _merge_sorted_chunks(paths: Sequence[Path]) -> Iterator[str]:
        iterators = [iter(_iter_spool_records(path, "An OCR sort spool")) for path in paths]
        pending: list[tuple[tuple[Any, ...], int, str]] = []
        try:
            for index, iterator in enumerate(iterators):
                try:
                    record, line = next(iterator)
                except StopIteration:
                    continue
                heapq.heappush(pending, (_record_sort_key(record), index, line))
            while pending:
                _key, index, line = heapq.heappop(pending)
                yield line
                try:
                    record, next_line = next(iterators[index])
                except StopIteration:
                    continue
                heapq.heappush(
                    pending,
                    (_record_sort_key(record), index, next_line),
                )
        finally:
            for iterator in iterators:
                close = getattr(iterator, "close", None)
                if close is not None:
                    close()

    def _merge_to_chunk(self, paths: Sequence[Path]) -> Path:
        destination, handle = _create_text_spool(self.path.parent, "bstrings-ocr-merge.")
        try:
            with handle:
                for line in self._merge_sorted_chunks(paths):
                    handle.write(line)
                    handle.write("\n")
            return destination
        except BaseException:
            with suppress(Exception):
                handle.close()
            with suppress(FileNotFoundError):
                destination.unlink()
            raise

    def iter_sorted_lines(self) -> Iterator[str]:
        if self._closed:
            raise OcrError("OCR page spool is closed")
        self._handle.flush()
        created: set[Path] = set()
        chunk_paths: list[Path] = []
        items: list[tuple[tuple[Any, ...], str]] = []
        item_bytes = 0

        def flush_items() -> None:
            nonlocal item_bytes
            if not items:
                return
            chunk = self._write_sorted_chunk(items)
            created.add(chunk)
            chunk_paths.append(chunk)
            items.clear()
            item_bytes = 0

        try:
            for record, line in _iter_spool_records(self.path, "An OCR page spool"):
                encoded_bytes = len(line.encode("utf-8")) + 1
                if items and (
                    len(items) >= SPOOL_SORT_CHUNK_RECORDS
                    or item_bytes + encoded_bytes > SPOOL_SORT_CHUNK_BYTES
                ):
                    flush_items()
                items.append((_record_sort_key(record), line))
                item_bytes += encoded_bytes
            flush_items()

            while len(chunk_paths) > SPOOL_SORT_MERGE_FAN_IN:
                merged: list[Path] = []
                for offset in range(0, len(chunk_paths), SPOOL_SORT_MERGE_FAN_IN):
                    group = chunk_paths[offset : offset + SPOOL_SORT_MERGE_FAN_IN]
                    destination = self._merge_to_chunk(group)
                    created.add(destination)
                    merged.append(destination)
                    for path in group:
                        with suppress(FileNotFoundError):
                            path.unlink()
                        created.discard(path)
                chunk_paths = merged

            yield from self._merge_sorted_chunks(chunk_paths)
        finally:
            for path in created:
                with suppress(FileNotFoundError):
                    path.unlink()

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        with suppress(Exception):
            self._handle.close()
        with suppress(FileNotFoundError):
            self.path.unlink()


class SourceRecordSpool:
    """Bounded disk-backed records for one source, already ordered by page."""

    def __init__(self, directory: Path) -> None:
        self.path, self._handle = _create_text_spool(directory, "bstrings-ocr-source.")
        self._identifiers: set[str] = set()
        self.string_records = 0
        self.pdf_text_records = 0
        self.ocr_records = 0
        self._closed = False

    def append_page(self, spools: Sequence[PageRecordSpool]) -> None:
        if self._closed:
            raise OcrError("OCR source spool is closed")
        try:
            for spool in spools:
                for line in spool.iter_sorted_lines():
                    try:
                        record = json.loads(line)
                    except json.JSONDecodeError as exc:
                        raise OcrError("An OCR page spool contains invalid JSON") from exc
                    record_id = str(record["recordId"])
                    if record_id in self._identifiers:
                        raise OcrError("OCR produced duplicate provenance records")
                    self._identifiers.add(record_id)
                    self._handle.write(line)
                    self._handle.write("\n")
                    self.string_records += 1
                    if record["origin"]["kind"] == "pdf-text":
                        self.pdf_text_records += 1
                    else:
                        self.ocr_records += 1
        finally:
            for spool in spools:
                spool.close()

    def iter_records(self) -> Iterator[dict[str, Any]]:
        if self._closed:
            raise OcrError("OCR source spool is closed")
        self._handle.flush()
        for record, _line in _iter_spool_records(self.path, "An OCR source spool"):
            yield record

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        with suppress(Exception):
            self._handle.close()
        with suppress(FileNotFoundError):
            self.path.unlink()


class RasterScheduler:
    """Bounded deterministic OCR scheduling for concrete independent lanes."""

    def __init__(self, runtime: OcrRuntime) -> None:
        self.runtime = runtime
        self.resolved_provider = runtime.resolved_provider
        self._hybrid = isinstance(runtime, HybridOcrRuntime)
        self._parallel_cpu = isinstance(runtime, CpuParallelOcrRuntime)
        self._pending: deque[
            tuple[
                int,
                tuple[PageRecordSpool, ...],
                PageRecordSpool | Future[PageRecordSpool],
            ]
        ] = deque()
        self._ordinal = 0
        self._output_ordinal = 0
        self._cross_source_ordinal = 0
        self._active = False
        self._closed = False
        self._sink: Callable[[Sequence[PageRecordSpool]], None] | None = None
        self.maximum_pending_observed = 0
        self.maximum_cross_source_pending_observed = 0
        self._cross_source_pending = 0
        self._cross_source_lock = threading.Lock()
        self._executors: dict[str, ThreadPoolExecutor] = {}
        self._capacity: dict[str, threading.BoundedSemaphore] = {}
        self._lanes: dict[str, OcrRuntime] = {}
        self.source_max_pending = 1
        self.cross_source_max_pending = 0
        if self._hybrid:
            configure = getattr(runtime, "configure_parallel_preprocessing", None)
            if callable(configure):
                configure()
            assert isinstance(runtime, HybridOcrRuntime)
            self._lanes = {
                "gpu": runtime.gpu_runtime,
                "cpu": runtime.cpu_runtime,
            }
            self._executors = {
                "gpu": ThreadPoolExecutor(max_workers=1, thread_name_prefix="bstrings-ocr-gpu"),
                "cpu": ThreadPoolExecutor(max_workers=1, thread_name_prefix="bstrings-ocr-cpu"),
            }
            self._capacity = {
                name: threading.BoundedSemaphore(HYBRID_QUEUE_DEPTH_PER_LANE)
                for name in self._executors
            }
            self.source_max_pending = HYBRID_SOURCE_MAX_PENDING
            self.cross_source_max_pending = HYBRID_CROSS_SOURCE_MAX_PENDING
        elif self._parallel_cpu:
            assert isinstance(runtime, CpuParallelOcrRuntime)
            self._lanes = {f"cpu-{index}": lane for index, lane in enumerate(runtime.lanes)}
            self._executors = {
                name: ThreadPoolExecutor(
                    max_workers=1,
                    thread_name_prefix=f"bstrings-ocr-{name}",
                )
                for name in self._lanes
            }
            self._capacity = {
                name: threading.BoundedSemaphore(CPU_PARALLEL_QUEUE_DEPTH_PER_WORKER)
                for name in self._executors
            }
            self.source_max_pending = len(self._lanes) * CPU_PARALLEL_QUEUE_DEPTH_PER_WORKER
            self.cross_source_max_pending = self.source_max_pending

    @property
    def cross_source_enabled(self) -> bool:
        return bool(self._executors)

    def begin_source(self, sink: Callable[[Sequence[PageRecordSpool]], None]) -> None:
        if self._closed or self._active or self._pending:
            raise OcrError("OCR scheduler state is invalid")
        self._ordinal = 0
        self._output_ordinal = 0
        self.maximum_pending_observed = 0
        self._sink = sink
        self._active = True

    def _track_pending(self) -> None:
        self.maximum_pending_observed = max(self.maximum_pending_observed, len(self._pending))

    def _drain_one(self) -> None:
        if not self._pending or self._sink is None:
            raise OcrError("OCR scheduler state is invalid")
        _ordinal, prefix, value = self._pending.popleft()
        try:
            selected = value.result() if isinstance(value, Future) else value
        except BaseException:
            for spool in prefix:
                spool.close()
            raise
        self._sink((*prefix, selected))

    @staticmethod
    def _hybrid_lane_name(ordinal: int, gpu_task_weight: int = HYBRID_GPU_TASK_WEIGHT) -> str:
        if ordinal == 1:
            return "cpu"
        if (
            ordinal >= HYBRID_SECOND_CPU_TASK
            and (ordinal - HYBRID_SECOND_CPU_TASK) % (gpu_task_weight + 1) == 0
        ):
            return "cpu"
        return "gpu"

    def _lane_name(self, ordinal: int, *, cross_source: bool = False) -> str:
        if self._hybrid:
            weight = HYBRID_CROSS_SOURCE_GPU_TASK_WEIGHT if cross_source else HYBRID_GPU_TASK_WEIGHT
            return self._hybrid_lane_name(ordinal, weight)
        if self._parallel_cpu:
            return f"cpu-{ordinal % len(self._lanes)}"
        raise OcrError("OCR scheduler has no parallel lane")

    @staticmethod
    def _execute(
        lane: OcrRuntime,
        image: Any,
        work: Callable[..., PageRecordSpool],
    ) -> PageRecordSpool:
        try:
            return work(runtime=lane, execution_provider=lane.resolved_provider)
        finally:
            lane.close(image)

    def _finish_cross_source_future(self, capacity: threading.BoundedSemaphore) -> None:
        capacity.release()
        with self._cross_source_lock:
            self._cross_source_pending -= 1

    def submit(
        self,
        image: Any,
        work: Callable[..., PageRecordSpool],
        prefix_spools: Sequence[PageRecordSpool] = (),
    ) -> None:
        if not self._active or self._closed:
            raise OcrError("OCR scheduler is not accepting raster work")
        ordinal = self._ordinal
        self._ordinal += 1
        output_ordinal = self._output_ordinal
        self._output_ordinal += 1
        prefix = tuple(prefix_spools)
        if not self._executors:
            try:
                records = work(
                    runtime=self.runtime,
                    execution_provider=self.runtime.resolved_provider,
                )
            except BaseException:
                for spool in prefix:
                    spool.close()
                raise
            finally:
                self.runtime.close(image)
            self._pending.append((output_ordinal, prefix, records))
            self._track_pending()
            self._drain_one()
            return

        lane_name = self._lane_name(ordinal)
        lane = self._lanes[lane_name]
        capacity = self._capacity[lane_name]
        try:
            capacity.acquire()
        except BaseException:
            for spool in prefix:
                spool.close()
            lane.close(image)
            raise
        try:
            future = self._executors[lane_name].submit(self._execute, lane, image, work)
        except BaseException:
            capacity.release()
            for spool in prefix:
                spool.close()
            lane.close(image)
            raise
        future.add_done_callback(lambda _future, gate=capacity: gate.release())
        self._pending.append((output_ordinal, prefix, future))
        self._track_pending()
        if len(self._pending) >= self.source_max_pending:
            self._drain_one()

    def submit_spool(self, spool: PageRecordSpool) -> None:
        if not self._active or self._closed:
            raise OcrError("OCR scheduler is not accepting page records")
        output_ordinal = self._output_ordinal
        self._output_ordinal += 1
        self._pending.append((output_ordinal, (), spool))
        self._track_pending()
        if not self._executors or len(self._pending) >= self.source_max_pending:
            self._drain_one()

    def submit_cross_source(
        self,
        image: Any,
        work: Callable[..., PageRecordSpool],
    ) -> ScheduledRasterWork:
        if not self._executors or self._active or self._closed:
            raise OcrError("OCR scheduler is not accepting cross-source raster work")
        ordinal = self._cross_source_ordinal
        self._cross_source_ordinal += 1
        lane_name = self._lane_name(ordinal, cross_source=True)
        lane = self._lanes[lane_name]
        capacity = self._capacity[lane_name]
        try:
            capacity.acquire()
        except BaseException:
            lane.close(image)
            raise
        try:
            future = self._executors[lane_name].submit(self._execute, lane, image, work)
        except BaseException:
            capacity.release()
            lane.close(image)
            raise
        with self._cross_source_lock:
            self._cross_source_pending += 1
            self.maximum_cross_source_pending_observed = max(
                self.maximum_cross_source_pending_observed,
                self._cross_source_pending,
            )
        future.add_done_callback(
            lambda _future, gate=capacity: self._finish_cross_source_future(gate)
        )
        return ScheduledRasterWork(future=future, lane=lane, image=image)

    @staticmethod
    def finish_cross_source(work: ScheduledRasterWork) -> PageRecordSpool:
        return work.future.result()

    @staticmethod
    def cancel_and_drain_cross_sources(works: Iterable[ScheduledRasterWork]) -> None:
        retained: list[ScheduledRasterWork] = []
        for work in works:
            if work.future.cancel():
                work.lane.close(work.image)
            else:
                retained.append(work)
        for work in retained:
            with suppress(BaseException):
                work.future.result().close()

    def finish_source(self) -> None:
        if not self._active:
            raise OcrError("OCR scheduler has no active source")
        try:
            while self._pending:
                self._drain_one()
        except BaseException:
            self.abort_source()
            raise
        self._sink = None
        self._active = False

    def abort_source(self) -> None:
        if not self._active and not self._pending:
            return
        for _ordinal, prefix, value in self._pending:
            for spool in prefix:
                spool.close()
            if isinstance(value, Future):
                with suppress(BaseException):
                    value.result().close()
            else:
                value.close()
        self._pending.clear()
        self._sink = None
        self._active = False

    def close(self) -> None:
        if self._closed:
            return
        self.abort_source()
        for executor in self._executors.values():
            executor.shutdown(wait=True, cancel_futures=False)
        self._closed = True


def _process_image(
    *,
    runtime: OcrRuntime,
    config: OcrConfig,
    pack: ModelPack,
    source_file: str,
    path: Path,
    source_sha256: str,
    source_size: int,
    route_decision_id: str | None,
    runtime_sha256: str,
    scheduler: RasterScheduler,
    spool: SourceRecordSpool,
    budget: OutputBudget,
) -> tuple[int, int]:
    pages = 0
    scheduler.begin_source(spool.append_page)
    try:
        for page_number, image in runtime.iter_image_frames(
            path, config.max_pages, config.max_pixels
        ):
            pages += 1
            work = partial(
                _records_for_raster,
                config=config,
                pack=pack,
                source_file=source_file,
                source_sha256=source_sha256,
                source_size=source_size,
                route_decision_id=route_decision_id,
                page_number=page_number,
                image=image,
                location_kind="image_region",
                runtime_sha256=runtime_sha256,
                render_dpi=None,
                resolved_provider=scheduler.resolved_provider,
                requested_threads=scheduler.runtime.requested_threads,
                resolved_thread_counts=scheduler.runtime.resolved_thread_counts,
                resolved_worker_counts=scheduler.runtime.resolved_worker_counts,
                budget=budget,
                spool_directory=spool.path.parent,
            )
            try:
                scheduler.submit(image, work)
            except BaseException:
                runtime.close(image)
                raise
        if pages == 0:
            raise OcrError("A recognized image contained no frames")
        scheduler.finish_source()
        return pages, pages
    except BaseException:
        scheduler.abort_source()
        raise


def _process_pdf(
    *,
    runtime: OcrRuntime,
    config: OcrConfig,
    pack: ModelPack,
    source_file: str,
    path: Path,
    source_sha256: str,
    source_size: int,
    route_decision_id: str | None,
    runtime_sha256: str,
    scheduler: RasterScheduler,
    spool: SourceRecordSpool,
    budget: OutputBudget,
) -> tuple[int, int]:
    document = runtime.open_pdf(path)
    rendered_pages = 0
    try:
        pages = runtime.pdf_page_count(document)
        if pages <= 0 or pages > config.max_pages:
            raise OcrError("A PDF exceeds the configured page limit or contains no pages")
        scheduler.begin_source(spool.append_page)
        for index in range(pages):
            page = runtime.get_pdf_page(document, index)
            image = None
            page_spool: PageRecordSpool | None = PageRecordSpool(spool.path.parent, budget)
            try:
                page_number = index + 1
                width_points, height_points = runtime.pdf_page_size(page)
                _validate_pixel_count(width_points, height_points, 10**12)
                text = runtime.extract_pdf_text(page, config.max_text_characters)
                if "\x00" in text or len(text) > config.max_text_characters:
                    raise OcrError("A PDF text layer exceeds the configured text limit")
                if text.strip():
                    page_spool.append(
                        make_record(
                            config=config,
                            pack=pack,
                            source_file=source_file,
                            source_sha256=source_sha256,
                            source_size=source_size,
                            route_decision_id=route_decision_id,
                            origin_kind="pdf-text",
                            location_kind="page_region",
                            page_number=page_number,
                            box=_full_page_box(width_points, height_points),
                            confidence=None,
                            text=text,
                            runtime_sha256=runtime_sha256,
                            render_sha256=None,
                            render_dpi=None,
                            coordinate_space="pdf-points",
                            width=width_points,
                            height=height_points,
                            pdfium_version=runtime.pdfium_version,
                            resolved_provider=scheduler.resolved_provider,
                            execution_provider="cpu",
                            requested_threads=scheduler.runtime.requested_threads,
                            resolved_thread_counts=scheduler.runtime.resolved_thread_counts,
                            resolved_worker_counts=scheduler.runtime.resolved_worker_counts,
                        )
                    )
                should_render = config.mode == "force" or _pdf_text_is_suspicious(
                    text, config.pdf_text_min_characters
                )
                if should_render:
                    image = runtime.render_pdf_page(page, config.dpi, config.max_pixels)
                    rendered_pages += 1
                    work = partial(
                        _records_for_raster,
                        config=config,
                        pack=pack,
                        source_file=source_file,
                        source_sha256=source_sha256,
                        source_size=source_size,
                        route_decision_id=route_decision_id,
                        page_number=page_number,
                        image=image,
                        location_kind="page_region",
                        runtime_sha256=runtime_sha256,
                        render_dpi=config.dpi,
                        resolved_provider=scheduler.resolved_provider,
                        requested_threads=scheduler.runtime.requested_threads,
                        resolved_thread_counts=scheduler.runtime.resolved_thread_counts,
                        resolved_worker_counts=scheduler.runtime.resolved_worker_counts,
                        budget=budget,
                        spool_directory=spool.path.parent,
                    )
                    scheduler.submit(image, work, (page_spool,))
                    image = None
                else:
                    scheduler.submit_spool(page_spool)
                page_spool = None
            finally:
                if page_spool is not None:
                    page_spool.close()
                runtime.close(image)
                runtime.close(page)
        scheduler.finish_source()
    except BaseException:
        scheduler.abort_source()
        raise
    finally:
        runtime.close(document)
    return pages, rendered_pages


def _prepare_single_frame_raster_source(
    *,
    runtime: OcrRuntime,
    config: OcrConfig,
    source_file: str,
    expected_identity: InputManifestEntry | None,
    route_decision_id: str | None,
    evidence_lease: EvidenceReadLease,
) -> PreparedRasterSource | None:
    path = Path(source_file)
    size_before, mtime_before = _source_snapshot(path)
    if expected_identity is not None and size_before != expected_identity.length:
        raise OcrError("An evidence input length does not match the verified input manifest")
    if _sniff_source(path) != "image":
        return None
    if size_before > config.max_input_bytes:
        raise OcrError("An OCR-applicable input exceeds the configured byte limit")
    source_sha256 = sha256_file(path, config.max_input_bytes)
    if expected_identity is not None and not _constant_time_hex_equal(
        source_sha256, expected_identity.sha256
    ):
        raise OcrError("An evidence input SHA-256 does not match the verified input manifest")
    if runtime.image_frame_count(path, config.max_pages, config.max_pixels) != 1:
        return None

    iterator = iter(runtime.iter_image_frames(path, config.max_pages, config.max_pixels))
    image: Any | None = None
    try:
        try:
            page_number, image = next(iterator)
        except StopIteration as exc:
            raise OcrError("A recognized image contained no frames") from exc
        if page_number != 1:
            raise OcrError("A single-frame image reported an invalid page number")
        sentinel = object()
        additional = next(iterator, sentinel)
        if additional is not sentinel:
            assert isinstance(additional, tuple)
            runtime.close(additional[1])
            raise OcrError("An image frame count changed during OCR preparation")
        prepared = PreparedRasterSource(
            source_file=source_file,
            path=path,
            source_sha256=source_sha256,
            source_size=size_before,
            mtime_before=mtime_before,
            image=image,
            route_decision_id=route_decision_id,
            evidence_lease=evidence_lease,
        )
        image = None
        return prepared
    finally:
        if image is not None:
            runtime.close(image)
        close = getattr(iterator, "close", None)
        if close is not None:
            close()


def _finalize_processed_source(
    *,
    config: OcrConfig,
    pack: ModelPack,
    source_file: str,
    path: Path,
    source_sha256: str,
    source_size: int,
    route_decision_id: str | None,
    mtime_before: int,
    runtime_sha256: str,
    records: SourceRecordSpool,
    pages: int,
    rendered_pages: int,
    resolved_provider: str,
    resolved_thread_counts: dict[str, int],
    resolved_worker_counts: dict[str, int],
) -> SourceResult:
    size_after, mtime_after = _source_snapshot(path)
    if (source_size, mtime_before) != (size_after, mtime_after):
        raise OcrError("An evidence input changed during OCR examination")
    assessment = make_assessment(
        config=config,
        pack=pack,
        source_file=source_file,
        status="processed",
        pages=pages,
        string_records=records.string_records,
        pdf_text_records=records.pdf_text_records,
        ocr_records=records.ocr_records,
        source_sha256=source_sha256,
        source_size=source_size,
        route_decision_id=route_decision_id,
        runtime_sha256=runtime_sha256,
        rendered_pages=rendered_pages,
        resolved_provider=resolved_provider,
        requested_threads=config.threads,
        resolved_thread_counts=resolved_thread_counts,
        resolved_worker_counts=resolved_worker_counts,
    )
    return SourceResult(records, assessment)


def process_source(
    *,
    runtime: OcrRuntime,
    config: OcrConfig,
    pack: ModelPack,
    source_file: str,
    runtime_sha256: str,
    scheduler: RasterScheduler,
    budget: OutputBudget,
    expected_identity: InputManifestEntry | None = None,
    route_decision_id: str | None = None,
) -> SourceResult:
    path = Path(source_file)
    size_before, mtime_before = _source_snapshot(path)
    if expected_identity is not None and size_before != expected_identity.length:
        raise OcrError("An evidence input length does not match the verified input manifest")
    source_kind = _sniff_source(path)
    if source_kind == "not-applicable":
        source_sha256 = None
        if expected_identity is not None:
            source_sha256 = sha256_file(path)
            if not _constant_time_hex_equal(source_sha256, expected_identity.sha256):
                raise OcrError(
                    "An evidence input SHA-256 does not match the verified input manifest"
                )
        size_after, mtime_after = _source_snapshot(path)
        if (size_before, mtime_before) != (size_after, mtime_after):
            raise OcrError("An evidence input changed during OCR examination")
        assessment = make_assessment(
            config=config,
            pack=pack,
            source_file=source_file,
            status="not-applicable",
            pages=0,
            string_records=0,
            pdf_text_records=0,
            ocr_records=0,
            source_sha256=source_sha256,
            source_size=size_before,
            route_decision_id=route_decision_id,
            runtime_sha256=runtime_sha256,
            rendered_pages=0,
            resolved_provider=scheduler.resolved_provider,
            requested_threads=config.threads,
            resolved_thread_counts=scheduler.runtime.resolved_thread_counts,
            resolved_worker_counts=scheduler.runtime.resolved_worker_counts,
        )
        return SourceResult(None, assessment)
    if size_before > config.max_input_bytes:
        raise OcrError("An OCR-applicable input exceeds the configured byte limit")
    source_sha256 = sha256_file(path, config.max_input_bytes)
    if expected_identity is not None and not _constant_time_hex_equal(
        source_sha256, expected_identity.sha256
    ):
        raise OcrError("An evidence input SHA-256 does not match the verified input manifest")
    assert config.output is not None
    records = SourceRecordSpool(config.output.resolve().parent)
    try:
        if source_kind == "image":
            pages, rendered_pages = _process_image(
                runtime=runtime,
                config=config,
                pack=pack,
                source_file=source_file,
                path=path,
                source_sha256=source_sha256,
                source_size=size_before,
                route_decision_id=route_decision_id,
                runtime_sha256=runtime_sha256,
                scheduler=scheduler,
                spool=records,
                budget=budget,
            )
        else:
            pages, rendered_pages = _process_pdf(
                runtime=runtime,
                config=config,
                pack=pack,
                source_file=source_file,
                path=path,
                source_sha256=source_sha256,
                source_size=size_before,
                route_decision_id=route_decision_id,
                runtime_sha256=runtime_sha256,
                scheduler=scheduler,
                spool=records,
                budget=budget,
            )
        return _finalize_processed_source(
            config=config,
            pack=pack,
            source_file=source_file,
            path=path,
            source_sha256=source_sha256,
            source_size=size_before,
            route_decision_id=route_decision_id,
            mtime_before=mtime_before,
            runtime_sha256=runtime_sha256,
            records=records,
            pages=pages,
            rendered_pages=rendered_pages,
            resolved_provider=scheduler.resolved_provider,
            resolved_thread_counts=scheduler.runtime.resolved_thread_counts,
            resolved_worker_counts=scheduler.runtime.resolved_worker_counts,
        )
    except BaseException:
        records.close()
        raise


def _record_sort_key(record: dict[str, Any]) -> tuple[Any, ...]:
    attributes = record["attributes"]
    box = attributes["box"]
    xs = [float(point[0]) for point in box]
    ys = [float(point[1]) for point in box]
    origin_order = 0 if record["origin"]["kind"] == "pdf-text" else 1
    return (
        int(attributes["pageNumber"]),
        origin_order,
        min(ys),
        min(xs),
        max(ys),
        max(xs),
        record["text"],
        record["recordId"],
    )


def read_inventory(path: Path) -> Iterable[str]:
    try:
        handle = path.open("r", encoding="utf-8-sig", newline=None)
    except OSError as exc:
        raise OcrError("The OCR input inventory could not be opened") from exc
    seen: set[str] = set()
    with handle:
        line_number = 0
        while True:
            line = handle.readline(MAX_PATH_LIST_LINE_CHARACTERS + 2)
            if line == "":
                break
            line_number += 1
            value = line.rstrip("\r\n")
            if len(value) > MAX_PATH_LIST_LINE_CHARACTERS:
                raise OcrError("An OCR inventory line exceeds the path safety limit")
            if not value.strip() or "\x00" in value:
                raise OcrError("The OCR inventory contains an empty or invalid path")
            identity = os.path.normcase(os.path.abspath(value))
            if identity in seen:
                raise OcrError("The OCR inventory contains a duplicate path")
            seen.add(identity)
            yield value


def _path_literals_equal(left: str, right: str) -> bool:
    """Match the C# contract: ordinal-ignore-case on Windows, ordinal elsewhere."""

    if os.name == "nt":
        return left.casefold() == right.casefold()
    return left == right


def _reject_duplicate_json_members(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    document: dict[str, Any] = {}
    for name, value in pairs:
        if name in document:
            raise OcrError("The OCR input manifest contains a duplicate JSON field")
        document[name] = value
    return document


def _parse_input_manifest_entry(value: str) -> InputManifestEntry:
    try:
        document = json.loads(value, object_pairs_hook=_reject_duplicate_json_members)
    except OcrError:
        raise
    except json.JSONDecodeError as exc:
        raise OcrError("The OCR input manifest contains invalid JSON") from exc
    required = frozenset(("schemaVersion", "path", "length", "sha256"))
    if not isinstance(document, dict) or frozenset(document) != required:
        raise OcrError("The OCR input manifest row does not match schema version 1")
    schema_version = document["schemaVersion"]
    source_path = document["path"]
    length = document["length"]
    source_sha256 = document["sha256"]
    if type(schema_version) is not int or schema_version != SCHEMA_VERSION:
        raise OcrError("The OCR input manifest row does not use schema version 1")
    if (
        not isinstance(source_path, str)
        or not source_path
        or "\x00" in source_path
        or not os.path.isabs(source_path)
        or not _path_literals_equal(source_path, os.path.abspath(source_path))
    ):
        raise OcrError("The OCR input manifest contains a non-canonical evidence path")
    if type(length) is not int or not 0 <= length <= 2**63 - 1:
        raise OcrError("The OCR input manifest contains an invalid evidence length")
    if (
        not isinstance(source_sha256, str)
        or not _is_sha256(source_sha256)
        or source_sha256 != source_sha256.lower()
    ):
        raise OcrError("The OCR input manifest contains an invalid evidence SHA-256")
    return InputManifestEntry(source_path, length, source_sha256)


def read_input_manifest(path: Path) -> Iterable[InputManifestEntry]:
    try:
        handle = path.open("r", encoding="utf-8-sig", errors="strict", newline=None)
    except OSError as exc:
        raise OcrError("The OCR input manifest could not be opened") from exc
    with handle:
        try:
            while True:
                line = handle.readline(MAX_INPUT_MANIFEST_LINE_CHARACTERS + 2)
                if line == "":
                    break
                value = line.rstrip("\r\n")
                if len(value) > MAX_INPUT_MANIFEST_LINE_CHARACTERS:
                    raise OcrError("An OCR input manifest line exceeds the safety limit")
                if not value.strip() or "\x00" in value:
                    raise OcrError("The OCR input manifest contains an empty or invalid row")
                yield _parse_input_manifest_entry(value)
        except UnicodeError as exc:
            raise OcrError("The OCR input manifest is not strict UTF-8") from exc


def _reject_duplicate_routing_json_members(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    document: dict[str, Any] = {}
    for name, value in pairs:
        if name in document:
            raise OcrError("The OCR routing manifest contains a duplicate JSON field")
        document[name] = value
    return document


def _routing_string_array(document: dict[str, Any], name: str, ordinal: int) -> tuple[str, ...]:
    value = document[name]
    if (
        not isinstance(value, list)
        or any(not isinstance(item, str) or not item for item in value)
        or value != sorted(set(value))
    ):
        raise OcrError(f"OCR routing row {ordinal} contains an invalid sorted {name} array")
    return tuple(value)


def _parse_routing_manifest_entry(value: str, expected_ordinal: int) -> RoutingManifestEntry:
    try:
        document = json.loads(value, object_pairs_hook=_reject_duplicate_routing_json_members)
    except OcrError:
        raise
    except json.JSONDecodeError as exc:
        raise OcrError(f"OCR routing row {expected_ordinal} contains invalid JSON") from exc

    required = frozenset(
        (
            "schemaVersion",
            "recordType",
            "policyVersion",
            "ordinal",
            "decisionId",
            "sourceFile",
            "sourceSize",
            "sourceSha256",
            "classifier",
            "signals",
            "eligibleRoutes",
            "scheduledRoutes",
            "conflicts",
        )
    )
    if not isinstance(document, dict) or frozenset(document) != required:
        raise OcrError(f"OCR routing row {expected_ordinal} has unsupported fields")
    schema_version = document["schemaVersion"]
    policy_version = document["policyVersion"]
    valid_policy = type(schema_version) is int and (
        (schema_version == SCHEMA_VERSION and policy_version == "content-routing-v1")
        or (schema_version == 2 and policy_version == "content-routing-v2")
    )
    if (
        type(document["schemaVersion"]) is not int
        or not valid_policy
        or document["recordType"] != "content-route"
        or type(document["ordinal"]) is not int
        or document["ordinal"] != expected_ordinal
    ):
        raise OcrError(f"OCR routing row {expected_ordinal} has an invalid identity")

    source_path = document["sourceFile"]
    source_size = document["sourceSize"]
    source_sha256 = document["sourceSha256"]
    decision_id = document["decisionId"]
    if (
        not isinstance(source_path, str)
        or not source_path
        or "\x00" in source_path
        or not os.path.isabs(source_path)
        or not _path_literals_equal(source_path, os.path.abspath(source_path))
    ):
        raise OcrError(f"OCR routing row {expected_ordinal} has an invalid source path")
    if type(source_size) is not int or not 0 <= source_size <= 2**63 - 1:
        raise OcrError(f"OCR routing row {expected_ordinal} has an invalid source length")
    if (
        not isinstance(source_sha256, str)
        or not _is_sha256(source_sha256)
        or source_sha256 != source_sha256.lower()
    ):
        raise OcrError(f"OCR routing row {expected_ordinal} has an invalid source SHA-256")
    if (
        not isinstance(decision_id, str)
        or not decision_id.startswith("sha256:")
        or len(decision_id) != 71
        or not _is_sha256(decision_id[7:])
        or decision_id != decision_id.lower()
    ):
        raise OcrError(f"OCR routing row {expected_ordinal} has an invalid decision identity")

    classifier = document["classifier"]
    if (
        not isinstance(classifier, dict)
        or classifier.get("engine") != "magika"
        or classifier.get("status") not in {"ok", "error"}
    ):
        raise OcrError(f"OCR routing row {expected_ordinal} has an invalid classifier")
    signals = _routing_string_array(document, "signals", expected_ordinal)
    eligible_routes = _routing_string_array(document, "eligibleRoutes", expected_ordinal)
    scheduled_routes = _routing_string_array(document, "scheduledRoutes", expected_ordinal)
    conflicts = _routing_string_array(document, "conflicts", expected_ordinal)
    del signals, conflicts
    allowed_routes = {"native", "floss", "ocr"}
    native_scheduled = "native" in scheduled_routes
    if (
        "native" not in eligible_routes
        or (schema_version == SCHEMA_VERSION and not native_scheduled)
        or (schema_version == 2 and native_scheduled)
        or not set(eligible_routes).issubset(allowed_routes)
        or not set(scheduled_routes).issubset(eligible_routes)
    ):
        raise OcrError(f"OCR routing row {expected_ordinal} has invalid route policy")

    decision_material = dict(document)
    del decision_material["decisionId"]
    expected_decision_id = (
        "sha256:" + hashlib.sha256(canonical_json(decision_material).encode("utf-8")).hexdigest()
    )
    if not hmac.compare_digest(decision_id, expected_decision_id):
        raise OcrError(f"OCR routing row {expected_ordinal} has an altered decision identity")
    return RoutingManifestEntry(
        source_path, source_size, source_sha256, decision_id, scheduled_routes
    )


def read_routing_manifest(path: Path) -> Iterable[RoutingManifestEntry]:
    try:
        handle = path.open("r", encoding="utf-8", errors="strict", newline=None)
    except OSError as exc:
        raise OcrError("The OCR routing manifest could not be opened") from exc
    seen_paths: set[str] = set()
    with handle:
        try:
            expected_ordinal = 1
            while True:
                line = handle.readline(MAX_ROUTING_MANIFEST_LINE_CHARACTERS + 2)
                if line == "":
                    return
                value = line.rstrip("\r\n")
                if len(value) > MAX_ROUTING_MANIFEST_LINE_CHARACTERS:
                    raise OcrError("An OCR routing manifest line exceeds the safety limit")
                if not value.strip() or "\x00" in value:
                    raise OcrError("The OCR routing manifest contains an empty or invalid row")
                entry = _parse_routing_manifest_entry(value, expected_ordinal)
                path_identity = os.path.normcase(os.path.abspath(entry.path))
                if path_identity in seen_paths:
                    raise OcrError("The OCR routing manifest contains a duplicate source path")
                seen_paths.add(path_identity)
                yield entry
                expected_ordinal += 1
        except UnicodeError as exc:
            raise OcrError("The OCR routing manifest is not strict UTF-8") from exc


def read_input_pairs(
    inventory_path: Path, manifest_path: Path | None
) -> Iterable[tuple[str, InputManifestEntry | None]]:
    inventory = iter(read_inventory(inventory_path))
    if manifest_path is None:
        yield from ((source_file, None) for source_file in inventory)
        return

    manifest = iter(read_input_manifest(manifest_path))
    sentinel = object()
    try:
        while True:
            source_file = next(inventory, sentinel)
            manifest_entry = next(manifest, sentinel)
            if source_file is sentinel and manifest_entry is sentinel:
                return
            if source_file is sentinel:
                raise OcrError("The OCR input manifest has more rows than the inventory")
            if manifest_entry is sentinel:
                raise OcrError("The OCR input manifest has fewer rows than the inventory")
            assert isinstance(source_file, str)
            assert isinstance(manifest_entry, InputManifestEntry)
            if not _path_literals_equal(source_file, manifest_entry.path):
                raise OcrError("The OCR input manifest path does not match the inventory order")
            yield source_file, manifest_entry
    finally:
        for iterator in (inventory, manifest):
            close = getattr(iterator, "close", None)
            if close is not None:
                close()


def read_routed_input_pairs(
    inventory_path: Path,
    input_manifest_path: Path | None,
    routing_manifest_path: Path | None,
) -> Iterable[tuple[str, InputManifestEntry | None, str | None]]:
    candidates = iter(read_input_pairs(inventory_path, input_manifest_path))
    if routing_manifest_path is None:
        yield from ((source_file, identity, None) for source_file, identity in candidates)
        return
    if input_manifest_path is None:
        raise OcrError("The OCR routing manifest requires a verified input manifest")

    routes = iter(read_routing_manifest(routing_manifest_path))
    sentinel = object()
    candidate = next(candidates, sentinel)
    try:
        for route in routes:
            if candidate is sentinel:
                continue
            assert isinstance(candidate, tuple)
            source_file, identity = candidate
            assert isinstance(identity, InputManifestEntry)
            if not _path_literals_equal(source_file, route.path):
                continue
            if identity.length != route.length or not hmac.compare_digest(
                identity.sha256, route.sha256
            ):
                raise OcrError("An OCR candidate does not match its routed source identity")
            if "ocr" not in route.scheduled_routes:
                raise OcrError("An OCR candidate was not scheduled by content routing")
            yield source_file, identity, route.decision_id
            candidate = next(candidates, sentinel)
        if candidate is not sentinel:
            raise OcrError("The OCR routing manifest does not cover every OCR candidate")
    finally:
        for iterator in (candidates, routes):
            close = getattr(iterator, "close", None)
            if close is not None:
                close()


class AtomicJsonlPair:
    """Stage two JSONL files and replace the pair with rollback on commit failure."""

    def __init__(self, output: Path, assessments: Path, maximum_bytes: int) -> None:
        self.output = output.resolve()
        self.assessments = assessments.resolve()
        if self.output == self.assessments:
            raise OcrError("OCR string and assessment outputs must be different files")
        self.maximum_bytes = maximum_bytes
        self._bytes = 0
        self._temporary: dict[Path, Path] = {}
        self._handles: dict[Path, Any] = {}
        self._markers = (
            Path(str(self.output) + ".incomplete"),
            Path(str(self.assessments) + ".incomplete"),
        )
        self._committed = False

    def __enter__(self) -> AtomicJsonlPair:
        created_markers: set[Path] = set()
        descriptor: int | None = None
        untracked_handle: Any | None = None
        try:
            for destination in (self.output, self.assessments):
                destination.parent.mkdir(parents=True, exist_ok=True)
            for marker in self._markers:
                marker.parent.mkdir(parents=True, exist_ok=True)
                if not marker.exists():
                    created_markers.add(marker)
                marker.write_text("OCR output is incomplete.\n", encoding="utf-8", newline="\n")
            for destination in (self.output, self.assessments):
                descriptor, name = tempfile.mkstemp(
                    prefix=destination.name + ".partial.", dir=destination.parent
                )
                temporary = Path(name)
                self._temporary[destination] = temporary
                untracked_handle = os.fdopen(
                    descriptor,
                    "w",
                    encoding="utf-8",
                    newline="\n",
                )
                descriptor = None
                self._handles[destination] = untracked_handle
                untracked_handle = None
            return self
        except BaseException:
            if descriptor is not None:
                with suppress(OSError):
                    os.close(descriptor)
            if untracked_handle is not None:
                with suppress(Exception):
                    untracked_handle.close()
            for handle in self._handles.values():
                with suppress(Exception):
                    handle.close()
            self._handles.clear()
            for temporary in self._temporary.values():
                with suppress(FileNotFoundError):
                    temporary.unlink()
            self._temporary.clear()
            for marker in created_markers:
                with suppress(FileNotFoundError):
                    marker.unlink()
            raise

    def write(self, destination: Path, record: dict[str, Any]) -> None:
        destination = destination.resolve()
        handle = self._handles[destination]
        line = canonical_json(record)
        if len(line) > MAX_JSONL_LINE_CHARACTERS:
            raise OcrError("An OCR JSONL record exceeds the line safety limit")
        encoded_bytes = len(line.encode("utf-8")) + 1
        self._bytes += encoded_bytes
        if self._bytes > self.maximum_bytes:
            raise OcrError("OCR outputs exceed the configured byte limit")
        handle.write(line)
        handle.write("\n")

    def commit(self) -> None:
        for handle in self._handles.values():
            handle.flush()
            os.fsync(handle.fileno())
            handle.close()
        self._handles.clear()
        backups: dict[Path, Path] = {}
        installed: set[Path] = set()
        try:
            for destination in (self.output, self.assessments):
                if destination.exists():
                    descriptor, backup_name = tempfile.mkstemp(
                        prefix=destination.name + ".backup.", dir=destination.parent
                    )
                    os.close(descriptor)
                    backup = Path(backup_name)
                    backup.unlink()
                    os.replace(destination, backup)
                    backups[destination] = backup
            for destination in (self.output, self.assessments):
                os.replace(self._temporary[destination], destination)
                installed.add(destination)
            for marker in self._markers:
                marker.unlink()
            self._committed = True
        except BaseException:
            for destination in installed:
                with suppress(FileNotFoundError):
                    destination.unlink()
            for destination, backup in backups.items():
                if backup.exists():
                    with suppress(OSError):
                        os.replace(backup, destination)
            for marker in self._markers:
                with suppress(OSError):
                    marker.write_text("OCR output is incomplete.\n", encoding="utf-8", newline="\n")
            raise
        finally:
            if self._committed:
                for backup in backups.values():
                    with suppress(OSError):
                        backup.unlink()

    def __exit__(self, exc_type: Any, exc: Any, traceback: Any) -> None:
        for handle in self._handles.values():
            with suppress(Exception):
                handle.close()
        for temporary in self._temporary.values():
            with suppress(FileNotFoundError):
                temporary.unlink()
        if self._committed:
            return


def _validate_config(config: OcrConfig) -> None:
    if config.engine.strip().lower() != "rapidocr":
        raise OcrError("Only the verified RapidOCR engine is supported")
    if (
        not config.engine_version.strip()
        or not config.model_id.strip()
        or not config.model_revision.strip()
    ):
        raise OcrError("OCR engine and model identity fields are required")
    if not _is_sha256(config.model_sha256):
        raise OcrError("The configured OCR model SHA-256 is invalid")
    if config.mode not in {"auto", "force"}:
        raise OcrError("OCR mode must be auto or force")
    if config.provider not in {"auto", "cpu", "cuda", "directml", "hybrid"}:
        raise OcrError("OCR provider must be auto, cpu, cuda, directml, or hybrid")
    if config.progress_total_files < 0:
        raise OcrError("OCR progress total cannot be negative")
    numeric_values = (
        config.dpi,
        config.max_pages,
        config.max_pixels,
        config.max_input_bytes,
        config.max_output_bytes,
        config.max_output_records,
        config.max_text_characters,
        config.pdf_text_min_characters,
    )
    if any(value <= 0 for value in numeric_values) or not 72 <= config.dpi <= 600:
        raise OcrError("OCR safety limits and DPI must be positive and within range")
    if type(config.threads) is not int or not 0 <= config.threads <= MAX_OCR_SESSION_THREADS:
        raise OcrError(f"OCR threads must be between 0 and {MAX_OCR_SESSION_THREADS}")
    if not config.ocr_executable.is_file():
        raise OcrError("The configured OCR executable is missing")
    if not config.self_test and (
        config.paths_from is None or config.output is None or config.assessments_output is None
    ):
        raise OcrError("OCR examination requires inventory, string output, and assessment output")
    if config.input_manifest is not None and config.paths_from is None:
        raise OcrError("The OCR input manifest requires a path inventory")
    if config.routing_manifest is not None and (
        config.self_test or config.paths_from is None or config.input_manifest is None
    ):
        raise OcrError(
            "The OCR routing manifest requires an examination inventory and verified input manifest"
        )
    protected = {config.ocr_executable.resolve(), config.model_path.resolve()}
    if config.paths_from is not None:
        if not config.paths_from.is_file():
            raise OcrError("The OCR input inventory is missing")
        protected.add(config.paths_from.resolve())
    if config.input_manifest is not None:
        if not config.input_manifest.is_file():
            raise OcrError("The OCR input manifest is missing")
        protected.add(config.input_manifest.resolve())
    if config.routing_manifest is not None:
        if not config.routing_manifest.is_file():
            raise OcrError("The OCR routing manifest is missing")
        protected.add(config.routing_manifest.resolve())
    destinations = {
        value.resolve() for value in (config.output, config.assessments_output) if value is not None
    }
    if protected.intersection(destinations):
        raise OcrError("An OCR output path overlaps a required input or runtime artifact")


def _available_ort_providers() -> tuple[str, ...]:
    try:
        import onnxruntime
    except ImportError as exc:
        raise OcrError("The bundled ONNX Runtime is incomplete") from exc
    try:
        return tuple(str(value) for value in onnxruntime.get_available_providers())
    except Exception as exc:
        raise OcrError("Available OCR execution providers could not be verified") from exc


def _load_cpu_runtime(config: OcrConfig, pack: ModelPack) -> OcrRuntime:
    worker_count, session_threads = resolve_cpu_worker_layout(config.threads)
    lanes: list[OcrRuntime] = []
    try:
        for _ in range(worker_count):
            lanes.append(
                RapidOcrRuntime(
                    config,
                    pack,
                    "cpu",
                    session_threads=session_threads,
                    cooperative_sessions=worker_count > 1,
                )
            )
        if len(lanes) == 1:
            return lanes[0]
        return CpuParallelOcrRuntime(lanes)
    except BaseException:
        for lane in lanes:
            lane.close()
        raise


def load_runtime(config: OcrConfig, pack: ModelPack) -> OcrRuntime:
    candidates = provider_candidates(config.provider, _available_ort_providers())
    if config.provider == "cpu":
        return _load_cpu_runtime(config, pack)
    if config.provider in ORT_PROVIDER_NAMES:
        return RapidOcrRuntime(config, pack, candidates[0])
    if config.provider == "auto":
        last_error: OcrError | None = None
        for provider in candidates:
            try:
                if provider == "cpu":
                    return _load_cpu_runtime(config, pack)
                return RapidOcrRuntime(config, pack, provider)
            except OcrError as exc:
                last_error = exc
        raise OcrError(
            "No advertised OCR provider passed model-session verification"
        ) from last_error

    gpu_runtime: OcrRuntime | None = None
    last_error = None
    for provider in candidates:
        try:
            gpu_runtime = RapidOcrRuntime(config, pack, provider, cooperative_sessions=True)
            break
        except OcrError as exc:
            last_error = exc
    if gpu_runtime is None:
        raise OcrError(
            "No advertised GPU provider passed model-session verification"
        ) from last_error
    try:
        cpu_runtime = RapidOcrRuntime(
            config,
            pack,
            "cpu",
            hybrid_cpu_lane=True,
            cooperative_sessions=True,
        )
        return HybridOcrRuntime(gpu_runtime, cpu_runtime)
    except BaseException:
        gpu_runtime.close()
        raise


def _validate_resolved_provider(requested: str, resolved: str) -> None:
    valid_single = resolved in ORT_PROVIDER_NAMES
    valid_hybrid = resolved in {"hybrid-cuda-cpu", "hybrid-directml-cpu"}
    if not valid_single and not valid_hybrid:
        raise OcrError("The active OCR runtime reported an unsupported provider")
    if requested in ORT_PROVIDER_NAMES and resolved != requested:
        raise OcrError("The active OCR runtime changed the requested provider")
    if requested == "hybrid" and not valid_hybrid:
        raise OcrError("The active OCR runtime did not establish both hybrid lanes")
    if requested == "auto" and not (valid_single or valid_hybrid):
        raise OcrError("Automatic OCR provider selection did not resolve to a valid provider")


def _validate_runtime_threads(config: OcrConfig, runtime: OcrRuntime) -> None:
    if runtime.requested_threads != config.threads:
        raise OcrError("The active OCR runtime changed the requested thread count")
    expected_keys = {
        "cpu": {"cpu"},
        "cuda": {"cuda"},
        "directml": {"directml"},
        "hybrid-cuda-cpu": {"cpu", "cuda"},
        "hybrid-directml-cpu": {"cpu", "directml"},
    }[runtime.resolved_provider]
    counts = runtime.resolved_thread_counts
    if set(counts) != expected_keys or any(
        type(value) is not int or not 1 <= value <= MAX_OCR_SESSION_THREADS
        for value in counts.values()
    ):
        raise OcrError("The active OCR runtime reported invalid resolved thread counts")
    worker_counts = runtime.resolved_worker_counts
    if set(worker_counts) != expected_keys or any(
        type(value) is not int or not 1 <= value <= CPU_PARALLEL_MAX_WORKERS
        for value in worker_counts.values()
    ):
        raise OcrError("The active OCR runtime reported invalid resolved worker counts")
    if any(worker_counts[provider] != 1 for provider in expected_keys.difference({"cpu"})):
        raise OcrError("A GPU OCR provider reported unsupported source-level concurrency")
    if runtime.resolved_provider.startswith("hybrid-") and worker_counts["cpu"] != 1:
        raise OcrError("A hybrid OCR runtime changed its verified CPU lane count")
    if config.threads and any(value != config.threads for value in counts.values()):
        raise OcrError("The active OCR runtime changed an explicit thread override")
    if not config.threads and any(
        counts[provider] != 1 for provider in expected_keys.difference({"cpu"})
    ):
        raise OcrError("An automatic GPU OCR session used a non-conservative thread count")


def run_pipeline(config: OcrConfig, runtime: OcrRuntime | None = None) -> dict[str, Any]:
    _validate_config(config)
    pack = load_model_pack(
        config.model_path,
        config.model_sha256,
        config.model_id,
        config.model_revision,
    )
    runtime_sha256 = sha256_file(config.ocr_executable)
    runtime = runtime or load_runtime(config, pack)
    scheduler: RasterScheduler | None = None
    try:
        if runtime.engine_version != config.engine_version:
            raise OcrError("The active OCR runtime version does not match the verified identity")
        _validate_resolved_provider(config.provider, runtime.resolved_provider)
        _validate_runtime_threads(config, runtime)
        runtime.run_inference_self_test()
        if config.self_test:
            return {
                "inputFiles": 0,
                "processedFiles": 0,
                "notApplicableFiles": 0,
                "pages": 0,
                "stringRecords": 0,
                "provider": runtime.resolved_provider,
                "requestedThreads": config.threads,
                "resolvedThreadCounts": dict(runtime.resolved_thread_counts),
                "resolvedWorkerCounts": dict(runtime.resolved_worker_counts),
            }
        assert config.paths_from is not None
        assert config.output is not None
        assert config.assessments_output is not None
        stats = {
            "inputFiles": 0,
            "processedFiles": 0,
            "notApplicableFiles": 0,
            "pages": 0,
            "stringRecords": 0,
            "provider": runtime.resolved_provider,
            "requestedThreads": config.threads,
            "resolvedThreadCounts": dict(runtime.resolved_thread_counts),
            "resolvedWorkerCounts": dict(runtime.resolved_worker_counts),
        }
        budget = OutputBudget(config.max_output_records, config.max_output_bytes)
        scheduler = RasterScheduler(runtime)
        record_ids: set[str] = set()
        pending_sources: deque[PendingRasterSource] = deque()
        last_progress_percent = -1.0
        last_progress_time = 0.0

        def report_progress(completed: int, *, force: bool = False) -> None:
            nonlocal last_progress_percent, last_progress_time
            total = config.progress_total_files
            if total <= 0:
                return
            bounded = min(max(completed, 0), total)
            percent = bounded * 100.0 / total
            now = time.monotonic()
            if (
                not force
                and percent < 100.0
                and percent - last_progress_percent < 1.0
                and now - last_progress_time < 5.0
            ):
                return
            print(
                f"Progress: offline OCR: {percent:.1f}% ({bounded:,}/{total:,} files)",
                file=sys.stderr,
                flush=True,
            )
            last_progress_percent = percent
            last_progress_time = now

        report_progress(0, force=True)

        def publish_source(result: SourceResult, outputs: AtomicJsonlPair) -> None:
            try:
                budget.reserve(result.assessment, is_record=False)
                stats["inputFiles"] += 1
                stats["pages"] += int(result.assessment["pages"])
                if result.assessment["status"] == "processed":
                    stats["processedFiles"] += 1
                else:
                    stats["notApplicableFiles"] += 1
                if result.records is not None:
                    for record in result.records.iter_records():
                        record_id = str(record["recordId"])
                        if record_id in record_ids:
                            raise OcrError("OCR output contains a duplicate record identity")
                        record_ids.add(record_id)
                        stats["stringRecords"] += 1
                        outputs.write(config.output, record)
                outputs.write(config.assessments_output, result.assessment)
                report_progress(stats["inputFiles"])
            finally:
                if result.records is not None:
                    result.records.close()

        def finish_pending_source(outputs: AtomicJsonlPair) -> None:
            pending = pending_sources.popleft()
            selected: PageRecordSpool | None = None
            records: SourceRecordSpool | None = None
            try:
                selected = scheduler.finish_cross_source(pending.work)
                records = SourceRecordSpool(config.output.resolve().parent)
                records.append_page((selected,))
                result = _finalize_processed_source(
                    config=config,
                    pack=pack,
                    source_file=pending.source.source_file,
                    path=pending.source.path,
                    source_sha256=pending.source.source_sha256,
                    source_size=pending.source.source_size,
                    route_decision_id=pending.source.route_decision_id,
                    mtime_before=pending.source.mtime_before,
                    runtime_sha256=runtime_sha256,
                    records=records,
                    pages=1,
                    rendered_pages=1,
                    resolved_provider=scheduler.resolved_provider,
                    resolved_thread_counts=runtime.resolved_thread_counts,
                    resolved_worker_counts=runtime.resolved_worker_counts,
                )
                _verify_source_hash_after_use(pending.source.path, pending.source.source_sha256)
                publish_source(result, outputs)
            except BaseException:
                if selected is not None:
                    selected.close()
                if records is not None:
                    records.close()
                raise
            finally:
                pending.source.evidence_lease.__exit__(None, None, None)

        def finish_pending_sources(outputs: AtomicJsonlPair) -> None:
            while pending_sources:
                finish_pending_source(outputs)

        with AtomicJsonlPair(
            config.output, config.assessments_output, config.max_output_bytes
        ) as outputs:
            try:
                for source_file, expected_identity, route_decision_id in read_routed_input_pairs(
                    config.paths_from, config.input_manifest, config.routing_manifest
                ):
                    if (
                        scheduler.cross_source_enabled
                        and len(pending_sources) >= scheduler.cross_source_max_pending
                    ):
                        finish_pending_source(outputs)
                    if Path(source_file).resolve() in {
                        config.output.resolve(),
                        config.assessments_output.resolve(),
                    }:
                        raise OcrError("The OCR inventory includes one of its output files")

                    evidence_lease = EvidenceReadLease(Path(source_file))
                    evidence_lease.__enter__()
                    lease_transferred = False
                    try:
                        prepared = None
                        if scheduler.cross_source_enabled:
                            prepared = _prepare_single_frame_raster_source(
                                runtime=runtime,
                                config=config,
                                source_file=source_file,
                                expected_identity=expected_identity,
                                route_decision_id=route_decision_id,
                                evidence_lease=evidence_lease,
                            )
                        if prepared is not None:
                            work = partial(
                                _records_for_raster,
                                config=config,
                                pack=pack,
                                source_file=source_file,
                                source_sha256=prepared.source_sha256,
                                source_size=prepared.source_size,
                                route_decision_id=prepared.route_decision_id,
                                page_number=1,
                                image=prepared.image,
                                location_kind="image_region",
                                runtime_sha256=runtime_sha256,
                                render_dpi=None,
                                resolved_provider=scheduler.resolved_provider,
                                requested_threads=config.threads,
                                resolved_thread_counts=runtime.resolved_thread_counts,
                                resolved_worker_counts=runtime.resolved_worker_counts,
                                budget=budget,
                                spool_directory=config.output.resolve().parent,
                            )
                            scheduled = scheduler.submit_cross_source(prepared.image, work)
                            try:
                                pending_sources.append(PendingRasterSource(prepared, scheduled))
                                lease_transferred = True
                            except BaseException:
                                scheduler.cancel_and_drain_cross_sources((scheduled,))
                                raise
                            continue

                        finish_pending_sources(outputs)
                        result = process_source(
                            runtime=runtime,
                            config=config,
                            pack=pack,
                            source_file=source_file,
                            runtime_sha256=runtime_sha256,
                            scheduler=scheduler,
                            budget=budget,
                            expected_identity=expected_identity,
                            route_decision_id=route_decision_id,
                        )
                        try:
                            _verify_source_hash_after_use(
                                Path(source_file), result.assessment.get("sourceSha256")
                            )
                        except BaseException:
                            if result.records is not None:
                                result.records.close()
                            raise
                        publish_source(result, outputs)
                    finally:
                        if not lease_transferred:
                            evidence_lease.__exit__(None, None, None)
                finish_pending_sources(outputs)
                if (
                    config.progress_total_files > 0
                    and stats["inputFiles"] != config.progress_total_files
                ):
                    raise OcrError(
                        "OCR inventory count changed: expected "
                        f"{config.progress_total_files:,} files but processed "
                        f"{stats['inputFiles']:,}"
                    )
                outputs.commit()
            finally:
                if pending_sources:
                    remaining = tuple(pending_sources)
                    try:
                        scheduler.cancel_and_drain_cross_sources(
                            pending.work for pending in remaining
                        )
                    finally:
                        for pending in remaining:
                            pending.source.evidence_lease.__exit__(None, None, None)
                        pending_sources.clear()
        return stats
    finally:
        if scheduler is not None:
            scheduler.close()
        runtime.close()


def _is_loopback_host(host: object) -> bool:
    if isinstance(host, bytes):
        host = host.decode("ascii", errors="ignore")
    if not isinstance(host, str):
        return False
    normalized = host.strip().lower().rstrip(".")
    if normalized == "localhost":
        return True
    with suppress(ValueError):
        return ipaddress.ip_address(normalized.split("%", maxsplit=1)[0]).is_loopback
    return False


def _airgap_audit_hook(event: str, arguments: tuple[Any, ...]) -> None:
    if event == "socket.connect" and len(arguments) >= 2:
        address = arguments[1]
        if isinstance(address, tuple) and address and not _is_loopback_host(address[0]):
            raise AirgapNetworkError("Air-gap mode blocked a non-loopback network connection")
    elif event == "socket.getaddrinfo" and arguments and not _is_loopback_host(arguments[0]):
        raise AirgapNetworkError("Air-gap mode blocked non-loopback name resolution")


def enable_airgap_mode() -> None:
    global _AIRGAP_ENABLED
    for name, value in AIRGAP_ENVIRONMENT.items():
        os.environ[name] = value
    if not _AIRGAP_ENABLED:
        sys.addaudithook(_airgap_audit_hook)
        _AIRGAP_ENABLED = True


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Extract PDF text and OCR images/PDF pages using only local model files."
    )
    parser.add_argument("--airgap", action="store_true")
    parser.add_argument("--paths-from", type=Path)
    parser.add_argument("--input-manifest", type=Path)
    parser.add_argument("--routing-manifest", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--assessments-output", type=Path)
    parser.add_argument("--ocr-executable", type=Path, required=True)
    parser.add_argument("--ocr-engine", required=True)
    parser.add_argument("--ocr-engine-version", required=True)
    parser.add_argument("--ocr-model-path", type=Path, required=True)
    parser.add_argument("--ocr-model-id", required=True)
    parser.add_argument("--ocr-model-revision", required=True)
    parser.add_argument("--ocr-model-sha256", required=True)
    parser.add_argument("--ocr-mode", choices=("auto", "force", "forensic", "all"), default="auto")
    parser.add_argument(
        "--provider",
        choices=("auto", "cpu", "cuda", "directml", "hybrid"),
        default="auto",
    )
    parser.add_argument("--dpi", type=int, default=300)
    parser.add_argument(
        "--threads",
        type=int,
        default=0,
        help="ONNX session threads; 0 selects a bounded provider-aware value",
    )
    parser.add_argument("--max-pages", type=int, default=10_000)
    parser.add_argument("--max-pixels", type=int, default=100_000_000)
    parser.add_argument("--max-input-bytes", type=int, default=8 * 1024**3)
    parser.add_argument("--max-output-bytes", type=int, default=4 * 1024**3)
    parser.add_argument("--max-output-records", type=int, default=1_000_000)
    parser.add_argument("--max-text-characters", type=int, default=4 * 1024 * 1024)
    parser.add_argument("--pdf-text-min-characters", type=int, default=32)
    parser.add_argument(
        "--progress-total-files",
        type=int,
        default=0,
        help="Expected inventory files for measured percentage reporting",
    )
    parser.add_argument("--self-test", action="store_true")
    return parser.parse_args(argv)


def config_from_arguments(args: argparse.Namespace) -> OcrConfig:
    mode = "force" if args.ocr_mode in {"force", "forensic", "all"} else "auto"
    return OcrConfig(
        paths_from=args.paths_from,
        output=args.output,
        assessments_output=args.assessments_output,
        ocr_executable=args.ocr_executable,
        engine=args.ocr_engine,
        engine_version=args.ocr_engine_version,
        model_path=args.ocr_model_path,
        model_id=args.ocr_model_id,
        model_revision=args.ocr_model_revision,
        model_sha256=args.ocr_model_sha256,
        mode=mode,
        provider=args.provider,
        dpi=args.dpi,
        threads=args.threads,
        max_pages=args.max_pages,
        max_pixels=args.max_pixels,
        max_input_bytes=args.max_input_bytes,
        max_output_bytes=args.max_output_bytes,
        max_output_records=args.max_output_records,
        max_text_characters=args.max_text_characters,
        pdf_text_min_characters=args.pdf_text_min_characters,
        self_test=args.self_test,
        airgap=args.airgap,
        input_manifest=args.input_manifest,
        routing_manifest=args.routing_manifest,
        progress_total_files=args.progress_total_files,
    )


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = parse_arguments(argv)
        config = config_from_arguments(args)
        if config.airgap:
            enable_airgap_mode()
        stats = run_pipeline(config)
        if config.self_test:
            print(
                canonical_json(
                    {
                        "schemaVersion": SCHEMA_VERSION,
                        "status": "ok",
                        **_identity_fields(config),
                        "provider": stats["provider"],
                        "requestedThreads": stats["requestedThreads"],
                        "resolvedThreadCounts": stats["resolvedThreadCounts"],
                        "resolvedWorkerCounts": stats["resolvedWorkerCounts"],
                    }
                )
            )
        elif stats["inputFiles"] == 0:
            # Empty inventories are valid and produce two complete empty JSONL files.
            pass
        return 0
    except (OcrError, OSError, UnicodeError, ValueError):
        # Evidence paths, extracted text, and library exception messages can contain case data.
        print("OCR examination failed; outputs remain incomplete.", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
