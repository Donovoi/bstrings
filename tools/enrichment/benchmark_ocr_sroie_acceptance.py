#!/usr/bin/env python3
"""Calibrate and perform the one-shot pinned SROIE OCR acceptance run.

The confirmatory test pathname remains an opaque string until an exclusive,
machine-global attempt ledger has been durably created.  Only then is the file
opened once, copied into a private snapshot while hashing, and handed to the
SROIE adapter.  There is intentionally no dry-run or test preflight mode.
"""

from __future__ import annotations

import sys

if __name__ == "__main__" and (
    sys.flags.isolated != 1
    or sys.flags.ignore_environment != 1
    or sys.flags.no_user_site != 1
    or not sys.dont_write_bytecode
):
    print(
        "SROIE OCR acceptance must be launched with the exact benchmark Python and -I -B",
        file=sys.stderr,
    )
    raise SystemExit(1)

import argparse
import atexit
import base64
import binascii
import copy
import ctypes
import hashlib
import json
import math
import os
import stat
import subprocess
import tempfile
import types
import uuid
from collections.abc import Callable, Mapping, Sequence
from contextlib import suppress
from dataclasses import dataclass
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import Any

_INVOCATION_CWD = Path.cwd().absolute()
_SOURCE_FILE = (
    Path(__file__) if Path(__file__).is_absolute() else _INVOCATION_CWD / Path(__file__)
).absolute()
_SOURCE_ROOT = _SOURCE_FILE.parent.absolute()
_LOADED_SOURCE_HASHES: dict[str, str] = {}
_CONTROLLED_CWD_OWNER: tempfile.TemporaryDirectory[str] | None = None
_ACCESS_EVENT_HOOK: Callable[[str], None] | None = None
_DLL_DIRECTORY_HANDLES: list[Any] = []
_TEST_PATH_PIPE_DESCRIPTOR: int | None = None


def _capture_entry_source() -> tuple[str, tuple[int | None, ...]]:
    try:
        for parent in reversed(_SOURCE_FILE.parents):
            parent_stat = os.lstat(parent)
            parent_attributes = getattr(parent_stat, "st_file_attributes", 0)
            if (
                not stat.S_ISDIR(parent_stat.st_mode)
                or stat.S_ISLNK(parent_stat.st_mode)
                or parent_attributes
                & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
            ):
                raise RuntimeError("The acceptance wrapper has an unsafe parent")
        lexical_stat = os.lstat(_SOURCE_FILE)
        descriptor = os.open(
            _SOURCE_FILE,
            os.O_RDONLY
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            before = os.fstat(descriptor)
            source = bytearray()
            while True:
                chunk = os.read(descriptor, 4 * 1024 * 1024)
                if not chunk:
                    break
                source.extend(chunk)
            after = os.fstat(descriptor)
        finally:
            os.close(descriptor)
    except OSError as exc:
        raise RuntimeError("The acceptance wrapper source cannot be frozen") from exc
    identity_names = ("st_dev", "st_ino", "st_size", "st_mtime_ns")
    lexical_identity = tuple(getattr(lexical_stat, name, None) for name in identity_names)
    before_identity = tuple(getattr(before, name, None) for name in identity_names)
    after_identity = tuple(getattr(after, name, None) for name in identity_names)
    attributes = getattr(lexical_stat, "st_file_attributes", 0)
    if (
        not stat.S_ISREG(lexical_stat.st_mode)
        or stat.S_ISLNK(lexical_stat.st_mode)
        or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        or lexical_identity != before_identity
        or before_identity != after_identity
        or len(source) != before.st_size
    ):
        raise RuntimeError("The acceptance wrapper source cannot be frozen")
    return hashlib.sha256(source).hexdigest(), after_identity


_ENTRY_SOURCE_SHA256, _ENTRY_SOURCE_IDENTITY = _capture_entry_source()


class _Guid(ctypes.Structure):
    _fields_ = (
        ("data1", ctypes.c_uint32),
        ("data2", ctypes.c_uint16),
        ("data3", ctypes.c_uint16),
        ("data4", ctypes.c_ubyte * 8),
    )


def _windows_program_data_known_folder() -> Path:
    identifier = uuid.UUID("62ab5d82-fdc1-4dc3-a9dd-070d1d495d97")
    guid = _Guid(
        identifier.time_low,
        identifier.time_mid,
        identifier.time_hi_version,
        (ctypes.c_ubyte * 8).from_buffer_copy(identifier.bytes[8:]),
    )
    shell32 = ctypes.WinDLL("shell32", use_last_error=True)
    ole32 = ctypes.WinDLL("ole32", use_last_error=True)
    known_folder = shell32.SHGetKnownFolderPath
    known_folder.argtypes = (
        ctypes.POINTER(_Guid),
        ctypes.c_uint32,
        ctypes.c_void_p,
        ctypes.POINTER(ctypes.c_wchar_p),
    )
    known_folder.restype = ctypes.c_long
    release = ole32.CoTaskMemFree
    release.argtypes = (ctypes.c_void_p,)
    release.restype = None
    output = ctypes.c_wchar_p()
    result = known_folder(ctypes.byref(guid), 0, None, ctypes.byref(output))
    if result != 0 or not output.value:
        raise RuntimeError("The canonical machine ProgramData folder is unavailable")
    try:
        path = Path(output.value)
    finally:
        release(ctypes.cast(output, ctypes.c_void_p))
    if not path.is_absolute():
        raise RuntimeError("The canonical machine ProgramData folder is unsafe")
    try:
        for component in reversed((path, *path.parents)):
            component_stat = os.lstat(component)
            if (
                not stat.S_ISDIR(component_stat.st_mode)
                or stat.S_ISLNK(component_stat.st_mode)
                or getattr(component_stat, "st_file_attributes", 0)
                & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
            ):
                raise RuntimeError("The canonical machine ProgramData folder is unsafe")
    except OSError as exc:
        raise RuntimeError("The canonical machine ProgramData folder is unsafe") from exc
    return Path(os.path.abspath(os.fspath(path)))


def _machine_state_root() -> Path:
    return _windows_program_data_known_folder() if os.name == "nt" else Path("/var/lib")


_MACHINE_STATE_ROOT = _machine_state_root()

CONFIRMATORY_ATTEMPT_LEDGER = (
    _MACHINE_STATE_ROOT
    / "bstrings"
    / "acceptance-ledgers"
    # This dataset-level name is intentionally stable across protocol revisions.
    # A scoring change must never replenish the held-out test attempt.
    / "icdar2019-sroie-bffe40c26759-test-one-shot-v1.json"
)

_OUTER_OFFLINE_ENVIRONMENT = {
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


def _event(name: str) -> None:
    if _ACCESS_EVENT_HOOK is not None:
        _ACCESS_EVENT_HOOK(name)


def _cleanup_outer_sandbox() -> None:
    global _TEST_PATH_PIPE_DESCRIPTOR
    if _TEST_PATH_PIPE_DESCRIPTOR is not None:
        with suppress(OSError):
            os.close(_TEST_PATH_PIPE_DESCRIPTOR)
        _TEST_PATH_PIPE_DESCRIPTOR = None
    if _CONTROLLED_CWD_OWNER is None:
        return
    with suppress(OSError):
        os.chdir(_SOURCE_ROOT)
    _CONTROLLED_CWD_OWNER.cleanup()


def _sanitize_outer_environment() -> None:
    global _CONTROLLED_CWD_OWNER
    retained: dict[str, str] = {}
    windows_directory: Path | None = None
    if os.name == "nt":
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        get_windows_directory = kernel32.GetWindowsDirectoryW
        get_windows_directory.argtypes = [ctypes.c_wchar_p, ctypes.c_uint32]
        get_windows_directory.restype = ctypes.c_uint32
        buffer = ctypes.create_unicode_buffer(32_768)
        length = get_windows_directory(buffer, len(buffer))
        if length <= 0 or length >= len(buffer):
            raise RuntimeError("The canonical Windows directory is unavailable")
        windows_directory = Path(buffer.value).resolve()
        retained.update(
            {
                "PATH": str((windows_directory / "System32").resolve()),
                "SystemRoot": str(windows_directory),
                "WINDIR": str(windows_directory),
            }
        )
    else:
        retained["PATH"] = "/usr/bin:/bin"
    temporary = tempfile.TemporaryDirectory(prefix="bstrings-sroie-acceptance-")
    controlled_cwd = Path(temporary.name).resolve()
    retained.update(_OUTER_OFFLINE_ENVIRONMENT)
    retained["TEMP"] = str(controlled_cwd)
    retained["TMP"] = str(controlled_cwd)
    os.environ.clear()
    os.environ.update(retained)
    os.chdir(controlled_cwd)
    if os.name == "nt":
        set_default = kernel32.SetDefaultDllDirectories
        set_default.argtypes = [ctypes.c_uint32]
        set_default.restype = ctypes.c_int
        if not set_default(0x00001000):
            raise RuntimeError("The safe Windows DLL search policy could not be enabled")
        _DLL_DIRECTORY_HANDLES.append(
            os.add_dll_directory(str(Path(sys.executable).resolve().parent))
        )
        _DLL_DIRECTORY_HANDLES.append(
            os.add_dll_directory(str((windows_directory / "System32").resolve()))
        )
    _CONTROLLED_CWD_OWNER = temporary
    atexit.register(_cleanup_outer_sandbox)


def _trusted_source_module(name: str, file_name: str) -> Any:
    path = (_SOURCE_ROOT / file_name).absolute()
    if path.parent != _SOURCE_ROOT:
        raise RuntimeError(f"The frozen {file_name} source is unsafe")
    try:
        parent_ids: list[tuple[Path, tuple[int | None, int | None]]] = []
        for parent in reversed(path.parents):
            parent_stat = os.lstat(parent)
            attributes = getattr(parent_stat, "st_file_attributes", 0)
            if (
                not stat.S_ISDIR(parent_stat.st_mode)
                or stat.S_ISLNK(parent_stat.st_mode)
                or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
            ):
                raise RuntimeError(f"The frozen {file_name} source is unsafe")
            parent_ids.append((parent, (parent_stat.st_dev, parent_stat.st_ino)))
        lexical = os.lstat(path)
        lexical_attributes = getattr(lexical, "st_file_attributes", 0)
        descriptor = os.open(
            path,
            os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
        with os.fdopen(descriptor, "rb") as handle:
            identity = os.fstat(handle.fileno())
            attributes = getattr(identity, "st_file_attributes", 0)
            if (
                not stat.S_ISREG(identity.st_mode)
                or stat.S_ISLNK(lexical.st_mode)
                or lexical_attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
                or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
                or (lexical.st_dev, lexical.st_ino, lexical.st_size, lexical.st_mtime_ns)
                != (identity.st_dev, identity.st_ino, identity.st_size, identity.st_mtime_ns)
            ):
                raise RuntimeError(f"The frozen {file_name} source is unsafe")
            source = handle.read()
            after = os.fstat(handle.fileno())
        final = os.lstat(path)
        final_attributes = getattr(final, "st_file_attributes", 0)
        if (
            (identity.st_dev, identity.st_ino, identity.st_size, identity.st_mtime_ns)
            != (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
            or (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns)
            != (final.st_dev, final.st_ino, final.st_size, final.st_mtime_ns)
            or stat.S_ISLNK(final.st_mode)
            or final_attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
            or any(
                (current := os.lstat(parent)).st_dev != expected[0]
                or current.st_ino != expected[1]
                or not stat.S_ISDIR(current.st_mode)
                or stat.S_ISLNK(current.st_mode)
                or getattr(current, "st_file_attributes", 0)
                & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
                for parent, expected in parent_ids
            )
        ):
            raise RuntimeError(f"The frozen {file_name} source is unsafe")
    except OSError as exc:
        raise RuntimeError(f"The frozen {file_name} source is unsafe") from exc
    if len(source) != identity.st_size:
        raise RuntimeError(f"The frozen {file_name} source changed while it was read")
    module = types.ModuleType(name)
    module.__file__ = str(path)
    module.__package__ = ""
    module.__cached__ = None
    sys.modules[name] = module
    try:
        exec(compile(source, str(path), "exec", dont_inherit=True), module.__dict__)
    except Exception:
        sys.modules.pop(name, None)
        raise
    _LOADED_SOURCE_HASHES[name] = hashlib.sha256(source).hexdigest()
    return module


if __name__ == "__main__":
    _sanitize_outer_environment()
    benchmark_core = _trusted_source_module("benchmark_ocr", "benchmark_ocr.py")
    cord = _trusted_source_module("benchmark_ocr_cord", "benchmark_ocr_cord.py")
    generic_policy = _trusted_source_module("ocr_acceptance_policy", "ocr_acceptance_policy.py")
    sroie = _trusted_source_module("benchmark_ocr_sroie", "benchmark_ocr_sroie.py")
    sroie_policy = _trusted_source_module(
        "ocr_sroie_acceptance_policy", "ocr_sroie_acceptance_policy.py"
    )
else:
    import benchmark_ocr as benchmark_core
    import benchmark_ocr_cord as cord
    import benchmark_ocr_sroie as sroie
    import ocr_acceptance_policy as generic_policy
    import ocr_sroie_acceptance_policy as sroie_policy


SCHEMA_VERSION = 1
PROTOCOL = sroie_policy.PROTOCOL
DETERMINISM_DOCUMENTS = 10
DETERMINISM_REPETITIONS = 2
MAX_REPORT_BYTES = 128 * 1024 * 1024
MAX_JSONL_BYTES = 64 * 1024 * 1024
HASH_CHUNK_BYTES = 4 * 1024 * 1024
REPORT_INCOMPLETE_MARKER = b"SROIE OCR acceptance has not completed.\n"
PINNED_GH_VERSION = "2.97.0"
PINNED_GH_EXE_SHA256 = "e2efa10a5d2ce93cac9bc4b676932b62947c0967c01c8f2c3a9cb4437ad358d3"
PINNED_RELEASE_REPOSITORY = "Donovoi/bstrings"
PINNED_RELEASE_REPOSITORY_ID = "816198865"
PINNED_RELEASE_OWNER_ID = "24791115"
GH_COMMAND_TIMEOUT_SECONDS = 120.0
MAX_GH_STDOUT_BYTES = 32 * 1024 * 1024
MAX_GH_STDERR_BYTES = 1024 * 1024
HYBRID_MINIMUM_LANE_FRACTION = 0.05
NO_THRESHOLD_OVERRIDES: dict[str, float | None] = {
    "minimumDetectionHmean": None,
    "minimumEndToEndHmean": None,
    "minimumWordAccuracy": None,
    "maximumPageCer": None,
    "maximumPageWer": None,
}


class AcceptanceError(RuntimeError):
    def __init__(
        self,
        message: str,
        *,
        stage: str,
        backend: str | None = None,
        claim: AttemptClaim | None = None,
    ):
        super().__init__(message)
        self.stage = stage
        self.backend = backend
        self.claim = claim


@dataclass(frozen=True)
class FrozenCandidate:
    worker: Path
    model_pack: Path
    worker_sha256: str
    model_id: str
    model_revision: str
    model_pack_sha256: str
    cpu_backend: Any
    directml_backend: Any
    runtimes: dict[str, dict[str, Any]]


@dataclass(frozen=True)
class CalibrationContext:
    report: dict[str, Any]
    report_sha256: str
    identity: dict[str, Any]
    expected_identities: tuple[dict[str, Any], ...]
    corpus_manifest: Path
    duplicate_audit: Path
    worker_manifest: Path
    inventory: Path
    runtime_inventories: dict[str, Path]
    source_image_sha256s: tuple[str, ...]


@dataclass(frozen=True)
class AttemptClaim:
    path: Path
    value: dict[str, Any]
    sha256: str


@dataclass(frozen=True)
class StagedReport:
    path: Path
    sha256: str
    byte_length: int
    file_identity: tuple[int | None, ...]


@dataclass(frozen=True)
class ValidatedWitness:
    value: dict[str, Any]
    file_sha256: str
    release_verification: dict[str, Any]
    release_verification_sha256: str
    gh_executable_sha256: str
    gh_version: str


def _canonical_bytes(value: Mapping[str, Any]) -> bytes:
    return (sroie_policy.canonical_json(value) + "\n").encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _fsync_parent_directory(path: Path) -> None:
    if os.name == "nt":
        return
    descriptor = os.open(path.parent, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
    try:
        os.fsync(descriptor)
    finally:
        os.close(descriptor)


def _atomic_create(path: Path, value: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    parent_snapshot: list[tuple[Path, tuple[int | None, int | None]]] = []
    for parent in reversed(path.parents):
        parent_stat = os.lstat(parent)
        if (
            not stat.S_ISDIR(parent_stat.st_mode)
            or stat.S_ISLNK(parent_stat.st_mode)
            or _unsafe_file_attributes(parent_stat)
        ):
            raise AcceptanceError("The output has an unsafe parent", stage="atomic")
        parent_snapshot.append((parent, _directory_object_id(parent_stat)))
    try:
        descriptor = os.open(
            path,
            os.O_WRONLY
            | os.O_CREAT
            | os.O_EXCL
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
    except FileExistsError as exc:
        raise AcceptanceError(
            f"The immutable output already exists: {path.name}", stage="atomic"
        ) from exc
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(value)
            handle.flush()
            os.fsync(handle.fileno())
        _fsync_parent_directory(path)
        if not _parents_stable(parent_snapshot):
            raise AcceptanceError("The output parent changed while writing", stage="atomic")
    except Exception:
        path.unlink(missing_ok=True)
        raise


def _atomic_replace(path: Path, value: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f"{path.name}.replace-{os.getpid()}")
    _atomic_create(temporary, value)
    _durable_replace(temporary, path)
    _fsync_parent_directory(path)


def _durable_replace(source: Path, destination: Path) -> None:
    if os.name != "nt":
        os.replace(source, destination)
        _fsync_parent_directory(destination)
        return
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    move = kernel32.MoveFileExW
    move.argtypes = (ctypes.c_wchar_p, ctypes.c_wchar_p, ctypes.c_uint32)
    move.restype = ctypes.c_int
    # MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH
    if not move(str(source), str(destination), 0x00000001 | 0x00000008):
        raise OSError(ctypes.get_last_error(), "MoveFileExW failed")


def _new_report_marker(path: Path) -> tuple[Path, Path]:
    output = _lexical_absolute(path)
    marker = output.with_name(output.name + ".incomplete")
    if output.exists() or marker.exists() or output.with_name(output.name + ".staged").exists():
        raise AcceptanceError("The report output or staging marker already exists", stage="report")
    _atomic_create(marker, REPORT_INCOMPLETE_MARKER)
    _require_report_marker(marker)
    return output, marker


def _require_report_marker(marker: Path) -> None:
    _, raw, _ = _read_regular_file(
        marker,
        maximum_bytes=len(REPORT_INCOMPLETE_MARKER),
        name="incomplete report marker",
        stage="report",
    )
    if raw != REPORT_INCOMPLETE_MARKER:
        raise AcceptanceError("The report staging marker changed", stage="report")


def _validate_public_report_privacy(value: Any, *, key: str = "") -> None:
    blocked_keys = {
        "address",
        "annotation",
        "company",
        "email",
        "groundtruth",
        "handle",
        "hypothesis",
        "hypotheses",
        "label",
        "labels",
        "prediction",
        "predictions",
        "rawoutput",
        "recognizedtext",
        "reference",
        "references",
        "sourcetext",
        "text",
        "token",
        "tokens",
        "transcription",
        "transcriptions",
        "username",
        "word",
        "words",
    }
    if isinstance(value, Mapping):
        for child_key, child in value.items():
            normalized = str(child_key).replace("-", "").replace("_", "").lower()
            if normalized in blocked_keys:
                raise AcceptanceError(
                    "The public report contains a forbidden evidence field", stage="privacy"
                )
            _validate_public_report_privacy(child, key=str(child_key))
        return
    if isinstance(value, (list, tuple)):
        for child in value:
            _validate_public_report_privacy(child, key=key)
        return
    if not isinstance(value, str):
        return
    windows = PureWindowsPath(value)
    posix = PurePosixPath(value)
    if (
        windows.is_absolute()
        or bool(windows.drive)
        or bool(windows.root)
        or posix.is_absolute()
        or value.startswith("\\")
    ):
        raise AcceptanceError("The public report contains an absolute path", stage="privacy")
    if key == "path" and (
        not value
        or "\\" in value
        or windows.drive
        or any(part in {".", ".."} for part in posix.parts)
    ):
        raise AcceptanceError("The public artifact path is unsafe", stage="privacy")


def _stage_report(output: Path, marker: Path, report: Mapping[str, Any]) -> StagedReport:
    _validate_public_report_privacy(report)
    raw = _canonical_bytes(report)
    if len(raw) > MAX_REPORT_BYTES:
        raise AcceptanceError("The report exceeds the bounded size", stage="report")
    staged = output.with_name(output.name + ".staged")
    _require_report_marker(marker)
    if output.exists() or staged.exists():
        raise AcceptanceError("The report staging state changed", stage="report")
    _atomic_create(staged, raw)
    _, persisted, identity = _read_regular_file(
        staged, maximum_bytes=MAX_REPORT_BYTES, name="staged report", stage="report"
    )
    if persisted != raw:
        raise AcceptanceError("The staged report bytes changed", stage="report")
    return StagedReport(
        path=staged,
        sha256=_sha256_bytes(persisted),
        byte_length=len(persisted),
        file_identity=identity,
    )


def _verify_report_file(
    path: Path,
    *,
    sha256: str,
    byte_length: int,
    file_identity: tuple[int | None, ...] | None = None,
) -> tuple[int | None, ...]:
    _, raw, identity = _read_regular_file(
        path, maximum_bytes=MAX_REPORT_BYTES, name="acceptance report", stage="report"
    )
    if (
        len(raw) != byte_length
        or _sha256_bytes(raw) != sha256
        or (file_identity is not None and identity != file_identity)
    ):
        raise AcceptanceError("The acceptance report identity changed", stage="report")
    return identity


def _durable_remove_marker(marker: Path) -> None:
    if os.name != "nt":
        marker.unlink()
        _fsync_parent_directory(marker)
        return
    tombstone = marker.with_name(f"{marker.name}.sealed-{os.getpid()}")
    _durable_replace(marker, tombstone)
    tombstone.unlink()


def _finalize_report(output: Path, marker: Path, staged: StagedReport) -> None:
    _require_report_marker(marker)
    if output.exists():
        raise AcceptanceError("The report cannot be atomically finalized", stage="report")
    _verify_report_file(
        staged.path,
        sha256=staged.sha256,
        byte_length=staged.byte_length,
        file_identity=staged.file_identity,
    )
    _durable_replace(staged.path, output)
    _verify_report_file(
        output, sha256=staged.sha256, byte_length=staged.byte_length
    )
    _durable_remove_marker(marker)


def _unsafe_file_attributes(value: os.stat_result) -> bool:
    return bool(
        getattr(value, "st_file_attributes", 0)
        & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    )


def _file_identity(value: os.stat_result) -> tuple[int | None, ...]:
    return tuple(
        getattr(value, name, None)
        for name in ("st_dev", "st_ino", "st_size", "st_mtime_ns")
    )


def _directory_object_id(value: os.stat_result) -> tuple[int | None, int | None]:
    return (getattr(value, "st_dev", None), getattr(value, "st_ino", None))


def _parents_stable(
    parents: Sequence[tuple[Path, tuple[int | None, int | None]]]
) -> bool:
    for parent, expected in parents:
        try:
            value = os.lstat(parent)
        except OSError:
            return False
        if (
            not stat.S_ISDIR(value.st_mode)
            or stat.S_ISLNK(value.st_mode)
            or _unsafe_file_attributes(value)
            or _directory_object_id(value) != expected
        ):
            return False
    return True


def _lexical_absolute(path: Path) -> Path:
    expanded = path.expanduser()
    if not expanded.is_absolute():
        expanded = _INVOCATION_CWD / expanded
    return Path(os.path.abspath(os.fspath(expanded)))


def _read_regular_file(
    path: Path, *, maximum_bytes: int, name: str, stage: str = "input"
) -> tuple[Path, bytes, tuple[int | None, ...]]:
    lexical = _lexical_absolute(path)
    try:
        parent_identities: list[tuple[Path, tuple[int | None, ...]]] = []
        for parent in reversed(lexical.parents):
            parent_stat = os.lstat(parent)
            if (
                not stat.S_ISDIR(parent_stat.st_mode)
                or stat.S_ISLNK(parent_stat.st_mode)
                or _unsafe_file_attributes(parent_stat)
            ):
                raise AcceptanceError(f"The {name} has an unsafe parent", stage=stage)
            parent_identities.append((parent, _directory_object_id(parent_stat)))
        lexical_stat = os.lstat(lexical)
        if (
            not stat.S_ISREG(lexical_stat.st_mode)
            or stat.S_ISLNK(lexical_stat.st_mode)
            or _unsafe_file_attributes(lexical_stat)
            or lexical_stat.st_size > maximum_bytes
        ):
            raise AcceptanceError(
                f"The {name} is unavailable, unsafe, or too large", stage=stage
            )
        descriptor = os.open(
            lexical,
            os.O_RDONLY
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
        )
    except AcceptanceError:
        raise
    except OSError as exc:
        raise AcceptanceError(
            f"The {name} is unavailable, unsafe, or too large", stage=stage
        ) from exc
    try:
        before = os.fstat(descriptor)
        if (
            not stat.S_ISREG(before.st_mode)
            or _unsafe_file_attributes(before)
            or _file_identity(before) != _file_identity(lexical_stat)
        ):
            raise AcceptanceError(f"The {name} changed before it was read", stage=stage)
        chunks: list[bytes] = []
        total = 0
        while True:
            chunk = os.read(descriptor, min(HASH_CHUNK_BYTES, maximum_bytes + 1 - total))
            if not chunk:
                break
            chunks.append(chunk)
            total += len(chunk)
            if total > maximum_bytes:
                raise AcceptanceError(f"The {name} is too large", stage=stage)
        after = os.fstat(descriptor)
    finally:
        os.close(descriptor)
    try:
        final_stat = os.lstat(lexical)
        parents_stable = _parents_stable(parent_identities)
    except OSError as exc:
        raise AcceptanceError(f"The {name} changed while it was read", stage=stage) from exc
    if (
        _file_identity(before) != _file_identity(after)
        or _file_identity(after) != _file_identity(final_stat)
        or stat.S_ISLNK(final_stat.st_mode)
        or _unsafe_file_attributes(final_stat)
        or not parents_stable
        or total != before.st_size
        or total != after.st_size
    ):
        raise AcceptanceError(f"The {name} changed while it was read", stage=stage)
    return lexical, b"".join(chunks), _file_identity(after)


def _regular_file_object_id(path: Path, *, name: str) -> tuple[int | None, int | None]:
    lexical = _lexical_absolute(path)
    try:
        parent_identities: list[tuple[Path, tuple[int | None, ...]]] = []
        for parent in reversed(lexical.parents):
            parent_stat = os.lstat(parent)
            if (
                not stat.S_ISDIR(parent_stat.st_mode)
                or stat.S_ISLNK(parent_stat.st_mode)
                or _unsafe_file_attributes(parent_stat)
            ):
                raise AcceptanceError(f"The {name} has an unsafe parent", stage="input")
            parent_identities.append((parent, _directory_object_id(parent_stat)))
        leaf = os.lstat(lexical)
        descriptor = os.open(
            lexical,
            os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            opened = os.fstat(descriptor)
        finally:
            os.close(descriptor)
        final_leaf = os.lstat(lexical)
        parents_stable = _parents_stable(parent_identities)
    except AcceptanceError:
        raise
    except OSError as exc:
        raise AcceptanceError(f"The {name} is unavailable or unsafe", stage="input") from exc
    object_id = (opened.st_dev, opened.st_ino)
    if (
        not stat.S_ISREG(leaf.st_mode)
        or stat.S_ISLNK(leaf.st_mode)
        or _unsafe_file_attributes(leaf)
        or _file_identity(leaf) != _file_identity(opened)
        or _file_identity(opened) != _file_identity(final_leaf)
        or not stat.S_ISREG(final_leaf.st_mode)
        or stat.S_ISLNK(final_leaf.st_mode)
        or _unsafe_file_attributes(final_leaf)
        or not parents_stable
        or object_id[0] is None
        or object_id[1] in {None, 0}
    ):
        raise AcceptanceError(f"The {name} is unavailable or unsafe", stage="input")
    return object_id


def _parse_json_bytes(
    raw: bytes, *, maximum_bytes: int, name: str, canonical: bool
) -> dict[str, Any]:
    if len(raw) > maximum_bytes:
        raise AcceptanceError(f"The {name} is too large", stage="input")
    def reject_pairs(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
        output: dict[str, Any] = {}
        for key, value in pairs:
            if key in output:
                raise AcceptanceError(f"The {name} contains a duplicate field", stage="input")
            output[key] = value
        return output

    try:
        value = json.loads(
            raw,
            object_pairs_hook=reject_pairs,
            parse_constant=lambda value: (_ for _ in ()).throw(
                AcceptanceError(f"The {name} contains {value}", stage="input")
            ),
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AcceptanceError(f"The {name} is not strict UTF-8 JSON", stage="input") from exc
    if not isinstance(value, dict) or (canonical and raw != _canonical_bytes(value)):
        raise AcceptanceError(f"The {name} is not canonical JSON", stage="input")
    return value


def _strict_json(path: Path, *, maximum_bytes: int, name: str) -> tuple[dict[str, Any], str]:
    _, raw, _ = _read_regular_file(path, maximum_bytes=maximum_bytes, name=name)
    value = _parse_json_bytes(raw, maximum_bytes=maximum_bytes, name=name, canonical=True)
    return value, _sha256_bytes(raw)


def _parse_jsonl_bytes(
    raw_document: bytes, *, name: str, stage: str = "input"
) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for line_number, raw in enumerate(raw_document.splitlines(keepends=True), start=1):
        if not raw.endswith(b"\n") or len(raw) > 1024 * 1024:
            raise AcceptanceError(f"The {name} line framing is invalid", stage=stage)
        try:
            value = json.loads(raw)
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise AcceptanceError(
                f"The {name} line {line_number} is invalid", stage=stage
            ) from exc
        if not isinstance(value, dict) or raw != _canonical_bytes(value):
            raise AcceptanceError(f"The {name} is not canonical JSONL", stage=stage)
        rows.append(value)
    return rows


def _strict_jsonl(path: Path, *, name: str) -> list[dict[str, Any]]:
    _, raw_document, _ = _read_regular_file(
        path, maximum_bytes=MAX_JSONL_BYTES, name=name
    )
    return _parse_jsonl_bytes(raw_document, name=name)


def _safe_artifact(path: Path, *, evidence_root: Path) -> dict[str, Any]:
    resolved = _lexical_absolute(path)
    root = _lexical_absolute(evidence_root)
    try:
        relative = resolved.relative_to(root).as_posix()
    except ValueError as exc:
        raise AcceptanceError("An artifact is outside the evidence root", stage="artifact") from exc
    _, raw, _ = _read_regular_file(
        resolved,
        maximum_bytes=512 * 1024 * 1024,
        name="evidence artifact",
        stage="artifact",
    )
    return {
        "bytes": len(raw),
        "path": relative,
        "sha256": _sha256_bytes(raw),
    }


def _resolve_artifact(root: Path, value: Any, *, name: str) -> Path:
    if not isinstance(value, Mapping) or set(value) != {"bytes", "path", "sha256"}:
        raise AcceptanceError(f"The {name} artifact identity is invalid", stage="calibration-input")
    raw_path = value.get("path")
    relative = PurePosixPath(raw_path) if isinstance(raw_path, str) else PurePosixPath(".")
    windows = PureWindowsPath(raw_path) if isinstance(raw_path, str) else PureWindowsPath(".")
    if (
        not isinstance(raw_path, str)
        or not raw_path
        or "\\" in raw_path
        or relative.is_absolute()
        or windows.is_absolute()
        or bool(windows.drive)
        or any(part in {".", ".."} for part in relative.parts)
    ):
        raise AcceptanceError(f"The {name} artifact path is unsafe", stage="calibration-input")
    base = _lexical_absolute(root)
    path = _lexical_absolute(base / Path(*relative.parts))
    try:
        path.relative_to(base)
    except ValueError as exc:
        raise AcceptanceError(
            f"The {name} artifact escapes its root", stage="calibration-input"
        ) from exc
    _, raw, _ = _read_regular_file(
        path,
        maximum_bytes=512 * 1024 * 1024,
        name=name,
        stage="calibration-input",
    )
    if len(raw) != value.get("bytes") or _sha256_bytes(raw) != value.get("sha256"):
        raise AcceptanceError(f"The {name} artifact changed", stage="calibration-input")
    return path


def _require_outer_runtime_isolation() -> None:
    if __name__ == "__main__" and (
        sys.flags.isolated != 1 or not sys.dont_write_bytecode or _CONTROLLED_CWD_OWNER is None
    ):
        raise AcceptanceError("The outer benchmark runtime is not isolated", stage="isolation")


def verify_candidate(args: argparse.Namespace) -> FrozenCandidate:
    worker = _lexical_absolute(args.worker)
    model_pack = _lexical_absolute(args.model_pack)
    cpu_python = _lexical_absolute(args.cpu_python)
    directml_python = _lexical_absolute(args.directml_python)
    for path, name in (
        (worker, "OCR worker"),
        (model_pack, "model-pack manifest"),
        (cpu_python, "CPU Python runtime"),
        (directml_python, "DirectML Python runtime"),
    ):
        _regular_file_object_id(path, name=name)
    if os.path.normcase(str(cpu_python)) == os.path.normcase(str(directml_python)):
        raise AcceptanceError("CPU and DirectML runtimes must differ", stage="candidate")
    if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
        raise AcceptanceError("The timeout must be positive and finite", stage="arguments")
    cpu = benchmark_core.Backend("cpu", cpu_python)
    directml = benchmark_core.Backend("directml", directml_python)
    try:
        benchmark_core.validate_release_matrix(
            (cpu, directml, benchmark_core.Backend("hybrid", directml_python))
        )
        runtimes = {
            "benchmark": benchmark_core.runtime_metadata(
                benchmark_core.Backend("benchmark", _lexical_absolute(Path(sys.executable))),
                timeout_seconds=min(args.timeout_seconds, 30.0),
            ),
            "cpu": benchmark_core.runtime_metadata(
                cpu, timeout_seconds=min(args.timeout_seconds, 30.0)
            ),
            "directml": benchmark_core.runtime_metadata(
                directml, timeout_seconds=min(args.timeout_seconds, 30.0)
            ),
        }
        model_id, model_revision, model_pack_sha256 = benchmark_core._load_model_identity(
            model_pack
        )
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(
            str(exc), stage=exc.stage or "candidate", backend=exc.backend
        ) from exc
    return FrozenCandidate(
        worker=worker,
        model_pack=model_pack,
        worker_sha256=benchmark_core.sha256_file(worker),
        model_id=model_id,
        model_revision=model_revision,
        model_pack_sha256=model_pack_sha256,
        cpu_backend=cpu,
        directml_backend=directml,
        runtimes=runtimes,
    )


def _scoring_constants() -> dict[str, Any]:
    return {
        "confidenceParityMaximumAbsoluteDelta": cord.CONFIDENCE_PARITY_MAX_ABS_DELTA,
        "duplicateImagePolicy": sroie.SROIE_DUPLICATE_IMAGE_POLICY,
        "hybridMinimumLaneFraction": HYBRID_MINIMUM_LANE_FRACTION,
        "predictionRowNormalizedDistance": cord.ROW_CLUSTER_NORMALIZED_DISTANCE,
        "referenceMergeNormalizedDistance": cord.REFERENCE_ROW_MERGE_NORMALIZED_DISTANCE,
        "referenceSplitNormalizedDistance": cord.REFERENCE_ROW_SPLIT_NORMALIZED_DISTANCE,
        "segmentationOverlapThreshold": cord.SEGMENTATION_OVERLAP_THRESHOLD,
        "textNormalization": sroie.SROIE_PRIMARY_TEXT_NORMALIZATION,
        "sroieCorpusManifestSchemaVersion": (sroie.SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION),
        "workerInputManifestSchemaVersion": cord.OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION,
    }


def _source_hashes() -> dict[str, str]:
    paths = {
        "acceptanceWrapperSha256": _lexical_absolute(Path(__file__)),
        "cordScorerSha256": _lexical_absolute(Path(cord.__file__)),
        "genericBenchmarkSha256": _lexical_absolute(Path(benchmark_core.__file__)),
        "genericPolicySha256": _lexical_absolute(Path(generic_policy.__file__)),
        "sroieAdapterSha256": _lexical_absolute(Path(sroie.__file__)),
        "sroiePolicySha256": _lexical_absolute(Path(sroie_policy.__file__)),
    }
    module_names = {
        "cordScorerSha256": "benchmark_ocr_cord",
        "genericBenchmarkSha256": "benchmark_ocr",
        "genericPolicySha256": "ocr_acceptance_policy",
        "sroieAdapterSha256": "benchmark_ocr_sroie",
        "sroiePolicySha256": "ocr_sroie_acceptance_policy",
    }
    output: dict[str, str] = {}
    for key, path in paths.items():
        if key == "acceptanceWrapperSha256":
            _, current_bytes, current_identity = _read_regular_file(
                _SOURCE_FILE,
                maximum_bytes=8 * 1024 * 1024,
                name="acceptance wrapper source",
                stage="source-identity",
            )
            current = _sha256_bytes(current_bytes)
            loaded = _ENTRY_SOURCE_SHA256
            if current_identity != _ENTRY_SOURCE_IDENTITY:
                raise AcceptanceError(
                    "The acceptance wrapper source identity changed on disk",
                    stage="source-identity",
                )
        else:
            _, current_bytes, _ = _read_regular_file(
                path,
                maximum_bytes=8 * 1024 * 1024,
                name="loaded benchmark source",
                stage="source-identity",
            )
            current = _sha256_bytes(current_bytes)
            loaded = _LOADED_SOURCE_HASHES.get(module_names.get(key, ""), current)
        if current != loaded:
            raise AcceptanceError("A loaded source changed on disk", stage="source-identity")
        output[key] = loaded
    output["scoringConstantsSha256"] = sroie_policy.sha256_canonical(_scoring_constants())
    return output


def _runtime_identity(runtimes: Mapping[str, Mapping[str, Any]]) -> dict[str, Any]:
    return {
        name: {
            "executableSha256": profile["executableSha256"],
            "inventorySha256": profile["inventorySha256"],
            "pythonVersion": profile["pythonVersion"],
            "requestedProvider": name,
        }
        for name, profile in sorted(runtimes.items())
    }


def _corpus_identity(corpus: Any, *, parquet_sha256: str) -> dict[str, Any]:
    identities = _expected_identities(corpus)
    return {
        "corpusManifestSha256": corpus.corpus_manifest_sha256,
        "duplicateAuditSha256": corpus.duplicate_audit_sha256,
        "excludedRows": len(corpus.excluded_row_indices),
        "imageIdentitiesSha256": sroie_policy.sha256_canonical(identities),
        "parquetSha256": parquet_sha256,
        "selectedDocuments": len(corpus.documents),
        "sourceImageDigestsSha256": sroie_policy.sha256_canonical(
            [
                {"imageSha256": digest, "rowIndex": index}
                for index, digest in enumerate(corpus.source_image_sha256s)
            ]
        ),
        "sourceRows": corpus.source_row_count,
        "workerManifestSha256": corpus.worker_manifest_sha256,
    }


def build_identity(
    frozen: FrozenCandidate, calibration_corpus: Mapping[str, Any]
) -> dict[str, Any]:
    try:
        _, _, shapely_version, geos_version = cord._shapely_runtime()
        pyarrow_version = cord._distribution_version("pyarrow")
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(str(exc), stage=exc.stage or "dependency") from exc
    return {
        "benchmark": _source_hashes(),
        "calibrationCorpus": dict(calibration_corpus),
        "dataset": sroie_policy.dataset_identity(),
        "dependencies": {
            "geos": geos_version,
            "pyarrow": pyarrow_version,
            "shapely": shapely_version,
        },
        "engine": {
            "modelId": frozen.model_id,
            "modelPackSha256": frozen.model_pack_sha256,
            "modelRevision": frozen.model_revision,
            "name": generic_policy.ENGINE_NAME,
            "version": generic_policy.ENGINE_VERSION,
        },
        "protocol": PROTOCOL,
        "runtimeProfiles": _runtime_identity(frozen.runtimes),
        "schemaVersion": SCHEMA_VERSION,
        "worker": {"sha256": frozen.worker_sha256},
    }


def _expected_identities(corpus: Any) -> list[dict[str, Any]]:
    return [
        {"imageSha256": document.sha256, "rowIndex": document.row_index}
        for document in corpus.documents
    ]


def _create_determinism_corpus(corpus: Any, root: Path, *, role: str) -> Any:
    row_indices = tuple(document.row_index for document in corpus.documents[:DETERMINISM_DOCUMENTS])
    if len(row_indices) != DETERMINISM_DOCUMENTS or row_indices != tuple(sorted(row_indices)):
        raise AcceptanceError(
            "The first ten selected deterministic rows changed", stage="determinism"
        )
    try:
        return cord.create_corpus_view(
            corpus,
            row_indices,
            root,
            name=f"sroie-{role}-determinism-first-10",
        )
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(str(exc), stage=exc.stage or "determinism") from exc


def _runtime_reference(profile: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "executableSha256": profile["executableSha256"],
        "inventorySha256": profile["inventorySha256"],
        "loadPathsSha256": sroie_policy.sha256_canonical(profile["loadPaths"]),
        "requestedProvider": profile["requestedProvider"],
        "runtimeRootsSha256": sroie_policy.sha256_canonical(profile["runtimeRoots"]),
        "schemaVersion": 1,
    }


def _profile_metrics(metrics: Mapping[str, Any], text_normalization: str) -> dict[str, Any]:
    profiled = copy.deepcopy(dict(metrics))
    per_document = profiled.get("perDocument")
    if not isinstance(per_document, list):
        raise AcceptanceError("The OCR metrics lack per-document rows", stage="scoring")
    for item in per_document:
        if not isinstance(item, dict):
            raise AcceptanceError("An OCR metric row is invalid", stage="scoring")
        item["textNormalization"] = text_normalization
    profiled["textNormalization"] = text_normalization
    return profiled


def _per_document_bytes(metrics: Mapping[str, Any]) -> bytes:
    per_document = metrics.get("perDocument")
    if not isinstance(per_document, list):
        raise AcceptanceError("The OCR metrics lack per-document rows", stage="scoring")
    return "".join(
        sroie_policy.canonical_json(item) + "\n" for item in per_document
    ).encode("utf-8")


def _rescore_backend_run(
    run: Mapping[str, Any],
    corpus: Any,
    output_root: Path,
    *,
    backend_name: str,
) -> dict[str, Any]:
    try:
        raw_output_hashes = run.get("rawOutputHashes")
        expected_strings_sha256 = (
            raw_output_hashes.get("stringsSha256")
            if isinstance(raw_output_hashes, Mapping)
            else None
        )
        strings_path = output_root / "strings.jsonl"
        _, strings_bytes, _ = _read_regular_file(
            strings_path,
            maximum_bytes=MAX_JSONL_BYTES,
            name="OCR strings output",
            stage="scoring",
        )
        if (
            not isinstance(expected_strings_sha256, str)
            or _sha256_bytes(strings_bytes) != expected_strings_sha256
        ):
            raise AcceptanceError(
                "The OCR strings output differs from the validated worker output",
                stage="scoring",
                backend=backend_name,
            )
        records = _parse_jsonl_bytes(
            strings_bytes,
            name="OCR strings output",
            stage="scoring",
        )
        strict_metrics = _profile_metrics(
            run["metrics"], sroie.SROIE_DIAGNOSTIC_TEXT_NORMALIZATION
        )
        primary_metrics = _profile_metrics(
            sroie.score_records_case_insensitive(corpus, records),
            sroie.SROIE_PRIMARY_TEXT_NORMALIZATION,
        )
        strict_per_document = _per_document_bytes(strict_metrics)
        strict_metrics_sha256 = sroie_policy.sha256_canonical(strict_metrics)
        strict_per_document_sha256 = hashlib.sha256(strict_per_document).hexdigest()
        primary_metrics["caseSensitiveDiagnostics"] = {
            "metrics": strict_metrics,
            "metricsSha256": strict_metrics_sha256,
            "perDocumentMetricsSha256": strict_per_document_sha256,
            "textNormalization": sroie.SROIE_DIAGNOSTIC_TEXT_NORMALIZATION,
        }
        primary_per_document = _per_document_bytes(primary_metrics)
        cord._atomic_write(
            output_root / "metrics-per-document-case-sensitive.jsonl",
            strict_per_document,
        )
        cord._atomic_write(output_root / "metrics-per-document.jsonl", primary_per_document)
    except (KeyError, TypeError, ValueError, benchmark_core.BenchmarkError) as exc:
        raise AcceptanceError(
            "The SROIE scoring profiles could not be bound",
            stage="scoring",
            backend=backend_name,
        ) from exc
    updated = dict(run)
    updated["textNormalization"] = sroie.SROIE_PRIMARY_TEXT_NORMALIZATION
    updated["metrics"] = primary_metrics
    updated["metricsSha256"] = sroie_policy.sha256_canonical(primary_metrics)
    updated["perDocumentMetricsSha256"] = hashlib.sha256(primary_per_document).hexdigest()
    return updated


def _benchmark_backend(
    frozen: FrozenCandidate,
    corpus: Any,
    determinism_corpus: Any,
    backend: Any,
    root: Path,
    timeout_seconds: float,
) -> dict[str, Any]:
    runtime_name = "cpu" if backend.requested_provider == "cpu" else "directml"
    try:
        run = cord.benchmark_backend(
            backend=backend,
            quality_corpus=corpus,
            determinism_corpus=determinism_corpus,
            determinism_repetitions=DETERMINISM_REPETITIONS,
            output_root=root,
            runtime=_runtime_reference(frozen.runtimes[runtime_name]),
            worker=frozen.worker,
            model_pack=frozen.model_pack,
            model_id=frozen.model_id,
            revision=frozen.model_revision,
            model_pack_sha256=frozen.model_pack_sha256,
            worker_sha256=frozen.worker_sha256,
            engine_version=generic_policy.ENGINE_VERSION,
            threads=0,
            timeout_seconds=timeout_seconds,
            thresholds=dict(NO_THRESHOLD_OVERRIDES),
        )
        quality_run = _rescore_backend_run(
            run["qualityRun"],
            corpus,
            root / "quality-all-100",
            backend_name=backend.requested_provider,
        )
        determinism_runs = [
            _rescore_backend_run(
                repetition,
                determinism_corpus,
                root / "determinism-rows-0000-0009" / f"run-{index + 1:02d}",
                backend_name=backend.requested_provider,
            )
            for index, repetition in enumerate(run["determinism"]["runs"])
        ]
        metric_hashes = {item["metricsSha256"] for item in determinism_runs}
        updated = dict(run)
        updated["textNormalization"] = sroie.SROIE_PRIMARY_TEXT_NORMALIZATION
        updated["stableTextNormalization"] = all(
            item.get("textNormalization") == sroie.SROIE_PRIMARY_TEXT_NORMALIZATION
            for item in [quality_run, *determinism_runs]
        )
        updated["metrics"] = quality_run["metrics"]
        updated["metricsSha256"] = quality_run["metricsSha256"]
        updated["metricsDeterministic"] = len(metric_hashes) == 1
        updated["qualityRun"] = quality_run
        updated["determinism"] = {
            **run["determinism"],
            "metricsDeterministic": len(metric_hashes) == 1,
            "runs": determinism_runs,
        }
        return updated
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(str(exc), stage=exc.stage or "backend", backend=exc.backend) from exc


def _determinism_checks(run: Mapping[str, Any], corpus: Any) -> dict[str, bool]:
    detail = run.get("determinism")
    repetitions = detail.get("runs") if isinstance(detail, Mapping) else None
    expected_rows = [document.row_index for document in corpus.documents]
    if not isinstance(repetitions, list):
        repetitions = []
    return {
        "byteDeterministic": run.get("byteDeterministic") is True,
        "canonicalEvidenceDeterministic": run.get("canonicalEvidenceDeterministic") is True,
        "criticalEvidenceDeterministic": run.get("criticalEvidenceDeterministic") is True,
        "executionProviderCountsStable": run.get("executionProviderCountsStable") is True,
        "metricsDeterministic": run.get("metricsDeterministic") is True,
        "provenancePassed": run.get("provenancePassed") is True,
        "repetitionCountExact": (
            run.get("determinismRepetitions") == DETERMINISM_REPETITIONS
            and len(repetitions) == DETERMINISM_REPETITIONS
        ),
        "rowIdentitiesExact": (
            isinstance(detail, Mapping)
            and detail.get("rowIndices") == expected_rows
            and run.get("determinismRows") == DETERMINISM_DOCUMENTS
        ),
        "selectionIdentityExact": (
            isinstance(detail, Mapping) and detail.get("selectionSha256") == corpus.selection_sha256
        ),
        "stableProvider": run.get("stableResolvedProvider") is True,
        "stableRuntime": run.get("stableRuntime") is True,
        "stableTextNormalization": run.get("stableTextNormalization") is True
        and run.get("textNormalization") == sroie.SROIE_PRIMARY_TEXT_NORMALIZATION,
        "stableThreadCounts": run.get("stableResolvedThreadCounts") is True,
        "stableWorkerCounts": run.get("stableResolvedWorkerCounts") is True,
        "thresholdOverridesAbsent": run.get("qualityGatePassed") is None
        and all(
            isinstance(repetition, Mapping) and repetition.get("qualityGatePassed") is None
            for repetition in repetitions
        ),
    }


def _public_run(run: Mapping[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in run.items() if not key.startswith("_")}


def _persist_runtime_inventories(root: Path, frozen: FrozenCandidate) -> dict[str, Path]:
    paths: dict[str, Path] = {}
    for name, value in frozen.runtimes.items():
        path = root / f"{name}-runtime-inventory.json"
        _atomic_create(path, _canonical_bytes(value))
        paths[name] = path
    return paths


def _failure_details(error: Exception) -> dict[str, Any]:
    return {
        "backend": error.backend if isinstance(error, AcceptanceError) else None,
        "stage": error.stage if isinstance(error, AcceptanceError) else "unexpected",
        "type": type(error).__name__,
    }


def _report(
    *,
    role: str,
    documents: int,
    raw_rows: int,
    identity: Mapping[str, Any],
    expected_identities: Sequence[Mapping[str, Any]],
    metrics: Mapping[str, Any],
    evidence: Mapping[str, Any],
    integrity_passed: bool,
    acceptance_passed: bool,
) -> dict[str, Any]:
    return {
        "acceptancePassed": acceptance_passed,
        "documents": documents,
        "evaluationCompleted": True,
        "evaluationRole": role,
        "evidence": dict(evidence),
        "expectedIdentitiesSha256": sroie_policy.sha256_canonical(
            [dict(item) for item in expected_identities]
        ),
        "identity": dict(identity),
        "identitySha256": sroie_policy.sha256_canonical(identity),
        "integrityPassed": integrity_passed,
        "finalDisposition": "accepted" if acceptance_passed else "rejected",
        "metrics": dict(metrics),
        "metricsSha256": sroie_policy.sha256_canonical(metrics),
        "protocol": PROTOCOL,
        "rawRows": raw_rows,
        "runSucceeded": True,
        "schemaVersion": SCHEMA_VERSION,
    }


def _candidate_stable(args: argparse.Namespace, identity: Mapping[str, Any]) -> bool:
    after = verify_candidate(args)
    rebuilt = build_identity(after, identity["calibrationCorpus"])
    return sroie_policy.canonical_json(rebuilt) == sroie_policy.canonical_json(identity)


def execute_calibration(
    args: argparse.Namespace, frozen: FrozenCandidate
) -> tuple[dict[str, Any], dict[str, Any] | None]:
    phase_root = _lexical_absolute(args.work_directory) / "calibration"
    if phase_root.exists():
        raise AcceptanceError("The calibration work directory already exists", stage="arguments")
    phase_root.mkdir(parents=True)
    train = _lexical_absolute(args.train_parquet)
    try:
        parquet_sha256 = sroie.verify_sroie_parquet(train, split="train")
        corpus = sroie.extract_sroie_corpus(train, phase_root / "scoring-corpus", split="train")
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(str(exc), stage=exc.stage or "calibration-corpus") from exc
    if (
        parquet_sha256 != sroie_policy.TRAIN_SHA256
        or corpus.source_row_count != sroie_policy.RAW_TRAIN_ROWS
        or not corpus.documents
        or len(corpus.documents) + len(corpus.excluded_row_indices) != corpus.source_row_count
    ):
        raise AcceptanceError(
            "The full pinned train corpus was not extracted", stage="calibration-corpus"
        )
    expected_identities = _expected_identities(corpus)
    if (
        _validate_duplicate_audit(
            corpus.duplicate_audit,
            split="train",
            expected_raw_rows=sroie_policy.RAW_TRAIN_ROWS,
            expected_identities=expected_identities,
        )
        != corpus.source_image_sha256s
    ):
        raise AcceptanceError(
            "The train duplicate audit source digests changed", stage="duplicate-audit"
        )
    determinism = _create_determinism_corpus(corpus, phase_root / "views", role="calibration")
    runtime_paths = _persist_runtime_inventories(phase_root / "runtime", frozen)
    identity = build_identity(frozen, _corpus_identity(corpus, parquet_sha256=parquet_sha256))
    run = _benchmark_backend(
        frozen,
        corpus,
        determinism,
        frozen.cpu_backend,
        phase_root / "results" / "cpu",
        args.timeout_seconds,
    )
    try:
        measurements = generic_policy.extract_measurements_for_identities(
            run["metrics"], expected_identities=expected_identities
        )
        floors = generic_policy.absolute_floor_checks(measurements)
    except generic_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="quality") from exc
    checks = _determinism_checks(run, determinism)
    integrity_checks = {
        "candidateStable": _candidate_stable(args, identity),
        "determinismPassed": all(checks.values()),
        "documentCountExact": run.get("qualityRows") == len(corpus.documents),
        "providerExact": run.get("requestedProvider") == "cpu"
        and run.get("resolvedProvider") == "cpu",
        "runtimeHashExact": run.get("runtimeSha256") == frozen.runtimes["cpu"]["executableSha256"],
        "workerHashExact": run.get("workerSha256") == frozen.worker_sha256,
    }
    integrity_passed = all(integrity_checks.values())
    floors_passed = all(check["passed"] is True for check in floors.values())
    evidence = {
        "artifacts": {
            "calibrationCorpusManifest": _safe_artifact(
                corpus.corpus_manifest, evidence_root=phase_root
            ),
            "calibrationDuplicateAudit": _safe_artifact(
                corpus.duplicate_audit, evidence_root=phase_root
            ),
            "calibrationInventory": _safe_artifact(corpus.inventory, evidence_root=phase_root),
            "calibrationWorkerManifest": _safe_artifact(
                corpus.worker_manifest, evidence_root=phase_root
            ),
            "runtimeInventories": {
                name: _safe_artifact(path, evidence_root=phase_root)
                for name, path in runtime_paths.items()
            },
        },
        "backend": _public_run(run),
        "determinism": {"checks": checks, "repetitions": DETERMINISM_REPETITIONS},
        "generatedAtUtc": benchmark_core.utc_timestamp(),
        "integrityChecks": integrity_checks,
        "phase": "calibration",
        "quality": {"absoluteFloorChecks": floors, "measurements": measurements},
        "testArtifactAccessed": False,
        "thresholdOverridesPermitted": False,
    }
    report = _report(
        role="calibration",
        documents=len(corpus.documents),
        raw_rows=corpus.source_row_count,
        identity=identity,
        expected_identities=expected_identities,
        metrics=run["metrics"],
        evidence=evidence,
        integrity_passed=integrity_passed,
        acceptance_passed=integrity_passed and floors_passed,
    )
    if report["acceptancePassed"] is not True:
        return report, None
    report_sha256 = _sha256_bytes(_canonical_bytes(report))
    policy = sroie_policy.build_policy(
        calibration_report=report,
        calibration_report_sha256=report_sha256,
        expected_identity=identity,
        expected_identities=expected_identities,
        frozen_at_utc=benchmark_core.utc_timestamp(),
    )
    return report, policy


def _calibration_expected_identities(manifest: Path) -> tuple[dict[str, Any], ...]:
    rows = _strict_jsonl(manifest, name="calibration corpus manifest")
    identities = tuple(
        {"imageSha256": row.get("sha256"), "rowIndex": row.get("rowIndex")} for row in rows
    )
    row_indices = [item["rowIndex"] for item in identities]
    if (
        not identities
        or row_indices != sorted(set(row_indices))
        or any(
            isinstance(index, bool)
            or not isinstance(index, int)
            or not 0 <= index < sroie_policy.RAW_TRAIN_ROWS
            for index in row_indices
        )
    ):
        raise AcceptanceError(
            "The calibration corpus identities changed", stage="calibration-input"
        )
    # Validation is performed without manufacturing metrics by hashing the
    # exact shape and comparing it with the frozen candidate identity.
    if any(
        not isinstance(item["imageSha256"], str)
        or len(item["imageSha256"]) != 64
        or any(character not in "0123456789abcdef" for character in item["imageSha256"])
        for item in identities
    ):
        raise AcceptanceError(
            "The calibration image identities are invalid", stage="calibration-input"
        )
    return identities


def _validate_duplicate_audit(
    path: Path,
    *,
    split: str,
    expected_raw_rows: int,
    expected_identities: Sequence[Mapping[str, Any]],
) -> tuple[str, ...]:
    audit, _ = _strict_json(path, maximum_bytes=MAX_JSONL_BYTES, name=f"{split} duplicate audit")
    required = {
        "conflictingAnnotationDuplicateGroups",
        "dataset",
        "duplicateGroupAudit",
        "duplicateGroups",
        "excludedRowIndices",
        "excludedRows",
        "identicalAnnotationDuplicateGroups",
        "includedRows",
        "policy",
        "protocol",
        "revision",
        "schemaVersion",
        "selectionSha256",
        "sourceImageDigestSetSha256",
        "sourceImageDigests",
        "sourceRows",
        "split",
    }
    source_rows = audit.get("sourceImageDigests")
    excluded = audit.get("excludedRowIndices")
    if (
        set(audit) != required
        or audit.get("schemaVersion") != sroie.SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION
        or audit.get("protocol") != sroie.SROIE_PROTOCOL
        or audit.get("dataset") != sroie.SROIE_REPOSITORY
        or audit.get("revision") != sroie.SROIE_COMMIT
        or audit.get("split") != split
        or audit.get("policy") != sroie.SROIE_DUPLICATE_IMAGE_POLICY
        or audit.get("sourceRows") != expected_raw_rows
        or audit.get("includedRows") != len(expected_identities)
        or not isinstance(excluded, list)
        or audit.get("excludedRows") != len(excluded)
        or len(expected_identities) + len(excluded) != expected_raw_rows
        or audit.get("selectionSha256")
        != sroie_policy.sha256_canonical([dict(item) for item in expected_identities])
        or not isinstance(source_rows, list)
        or len(source_rows) != expected_raw_rows
    ):
        raise AcceptanceError("The duplicate-selection audit changed", stage="duplicate-audit")
    digests: list[str] = []
    for index, row in enumerate(source_rows):
        digest = row.get("imageSha256") if isinstance(row, Mapping) else None
        if (
            not isinstance(row, Mapping)
            or set(row) != {"imageSha256", "rowIndex"}
            or row.get("rowIndex") != index
            or not isinstance(digest, str)
            or len(digest) != 64
            or any(character not in "0123456789abcdef" for character in digest)
        ):
            raise AcceptanceError("A source image digest is invalid", stage="duplicate-audit")
        digests.append(digest)
    if audit.get("sourceImageDigestSetSha256") != sroie_policy.sha256_canonical(
        sorted(set(digests))
    ):
        raise AcceptanceError(
            "The source image digest-set identity changed", stage="duplicate-audit"
        )
    grouped_rows: dict[str, list[int]] = {}
    for row_index, digest in enumerate(digests):
        grouped_rows.setdefault(digest, []).append(row_index)
    expected_duplicate_digests = [
        digest for digest, indices in grouped_rows.items() if len(indices) > 1
    ]
    group_audit = audit.get("duplicateGroupAudit")
    if not isinstance(group_audit, list) or len(group_audit) != len(
        expected_duplicate_digests
    ):
        raise AcceptanceError("The duplicate group inventory changed", stage="duplicate-audit")
    computed_excluded: list[int] = []
    identical_groups = 0
    conflicting_groups = 0
    group_keys = {
        "annotationSha256s",
        "decision",
        "distinctAnnotationDigests",
        "excludedRowIndices",
        "imageSha256",
        "includedRowIndices",
        "rowCount",
        "rowIndices",
    }
    for group_index, (group, expected_digest) in enumerate(
        zip(group_audit, expected_duplicate_digests, strict=True)
    ):
        expected_rows = grouped_rows[expected_digest]
        annotation_sha256s = group.get("annotationSha256s") if isinstance(group, Mapping) else None
        if (
            not isinstance(group, Mapping)
            or set(group) != group_keys
            or group.get("imageSha256") != expected_digest
            or group.get("rowIndices") != expected_rows
            or group.get("rowCount") != len(expected_rows)
            or not isinstance(annotation_sha256s, list)
            or annotation_sha256s != sorted(set(annotation_sha256s))
            or not annotation_sha256s
            or any(
                not isinstance(digest, str)
                or len(digest) != 64
                or any(character not in "0123456789abcdef" for character in digest)
                for digest in annotation_sha256s
            )
            or group.get("distinctAnnotationDigests") != len(annotation_sha256s)
        ):
            raise AcceptanceError(
                f"Duplicate group {group_index} is invalid", stage="duplicate-audit"
            )
        if split == "test":
            decision = "score-all-test-rows"
            included_rows = expected_rows
            if len(annotation_sha256s) == 1:
                identical_groups += 1
            else:
                conflicting_groups += 1
        elif len(annotation_sha256s) == 1:
            decision = "keep-lowest-row-index"
            included_rows = [expected_rows[0]]
            identical_groups += 1
        else:
            decision = "exclude-conflicting-group"
            included_rows = []
            conflicting_groups += 1
        excluded_rows = [index for index in expected_rows if index not in included_rows]
        if (
            group.get("decision") != decision
            or group.get("includedRowIndices") != included_rows
            or group.get("excludedRowIndices") != excluded_rows
        ):
            raise AcceptanceError(
                f"Duplicate group {group_index} decision changed", stage="duplicate-audit"
            )
        computed_excluded.extend(excluded_rows)
    computed_excluded.sort()
    included_rows = sorted(set(range(expected_raw_rows)) - set(computed_excluded))
    identity_rows = [item.get("rowIndex") for item in expected_identities]
    if (
        excluded != computed_excluded
        or audit.get("duplicateGroups") != len(group_audit)
        or audit.get("identicalAnnotationDuplicateGroups") != identical_groups
        or audit.get("conflictingAnnotationDuplicateGroups") != conflicting_groups
        or identity_rows != included_rows
        or any(
            not isinstance(identity, Mapping)
            or set(identity) != {"imageSha256", "rowIndex"}
            or identity.get("imageSha256") != digests[identity["rowIndex"]]
            for identity in expected_identities
        )
    ):
        raise AcceptanceError(
            "The duplicate selection cannot be recomputed", stage="duplicate-audit"
        )
    return tuple(digests)


def load_calibration_context(report_path: Path, evidence_root: Path) -> CalibrationContext:
    report, report_sha256 = _strict_json(
        report_path, maximum_bytes=MAX_REPORT_BYTES, name="calibration report"
    )
    evidence = report.get("evidence")
    artifacts = evidence.get("artifacts") if isinstance(evidence, Mapping) else None
    if not isinstance(artifacts, Mapping):
        raise AcceptanceError(
            "The calibration artifact inventory is absent", stage="calibration-input"
        )
    root = _lexical_absolute(evidence_root) / "calibration"
    corpus_manifest = _resolve_artifact(
        root, artifacts.get("calibrationCorpusManifest"), name="calibration corpus manifest"
    )
    duplicate_audit = _resolve_artifact(
        root, artifacts.get("calibrationDuplicateAudit"), name="calibration duplicate audit"
    )
    worker_manifest = _resolve_artifact(
        root, artifacts.get("calibrationWorkerManifest"), name="calibration worker manifest"
    )
    inventory = _resolve_artifact(
        root, artifacts.get("calibrationInventory"), name="calibration inventory"
    )
    runtime_artifacts = artifacts.get("runtimeInventories")
    if not isinstance(runtime_artifacts, Mapping) or set(runtime_artifacts) != {
        "benchmark",
        "cpu",
        "directml",
    }:
        raise AcceptanceError(
            "The calibration runtime artifacts changed", stage="calibration-input"
        )
    runtime_paths = {
        name: _resolve_artifact(root, value, name=f"{name} runtime inventory")
        for name, value in runtime_artifacts.items()
    }
    expected = _calibration_expected_identities(corpus_manifest)
    source_image_sha256s = _validate_duplicate_audit(
        duplicate_audit,
        split="train",
        expected_raw_rows=sroie_policy.RAW_TRAIN_ROWS,
        expected_identities=expected,
    )
    identity = report.get("identity")
    if not isinstance(identity, dict):
        raise AcceptanceError("The calibration identity is absent", stage="calibration-input")
    corpus_identity = identity.get("calibrationCorpus")
    if (
        not isinstance(corpus_identity, Mapping)
        or corpus_identity.get("corpusManifestSha256")
        != benchmark_core.sha256_file(corpus_manifest)
        or corpus_identity.get("workerManifestSha256")
        != benchmark_core.sha256_file(worker_manifest)
        or corpus_identity.get("duplicateAuditSha256")
        != benchmark_core.sha256_file(duplicate_audit)
        or corpus_identity.get("imageIdentitiesSha256") != sroie_policy.sha256_canonical(expected)
        or corpus_identity.get("sourceImageDigestsSha256")
        != sroie_policy.sha256_canonical(
            [
                {"imageSha256": digest, "rowIndex": index}
                for index, digest in enumerate(source_image_sha256s)
            ]
        )
    ):
        raise AcceptanceError("The calibration corpus binding changed", stage="calibration-input")
    return CalibrationContext(
        report=report,
        report_sha256=report_sha256,
        identity=identity,
        expected_identities=expected,
        corpus_manifest=corpus_manifest,
        duplicate_audit=duplicate_audit,
        worker_manifest=worker_manifest,
        inventory=inventory,
        runtime_inventories=runtime_paths,
        source_image_sha256s=source_image_sha256s,
    )


def _recheck_policy(path: Path, validated: Any) -> None:
    value, digest = sroie_policy._strict_json(path)
    if digest != validated.file_sha256 or sroie_policy.canonical_json(
        value
    ) != sroie_policy.canonical_json(validated.value):
        raise AcceptanceError("The frozen policy changed around the one-shot claim", stage="policy")


def _safe_release_asset_name(value: str) -> bool:
    return (
        bool(value)
        and value == Path(value).name
        and value not in {".", ".."}
        and not any(character in value for character in "*?[]/\\")
        and not PureWindowsPath(value).drive
    )


def _fresh_gh_environment(root: Path) -> dict[str, str]:
    environment = {
        "GH_CONFIG_DIR": str(root / "gh-config"),
        "GH_NO_UPDATE_NOTIFIER": "1",
        "GH_PROMPT_DISABLED": "1",
        "HOME": str(root / "home"),
        "NO_COLOR": "1",
        "TEMP": str(root / "temp"),
        "TMP": str(root / "temp"),
        "USERPROFILE": str(root / "home"),
        "XDG_CONFIG_HOME": str(root / "xdg-config"),
    }
    for name in ("SystemRoot", "WINDIR"):
        if name in os.environ:
            environment[name] = os.environ[name]
    for directory in (
        root / "gh-config",
        root / "home",
        root / "temp",
        root / "xdg-config",
    ):
        directory.mkdir(parents=True, exist_ok=False)
    return environment


def _run_pinned_gh(
    executable: Path,
    arguments: Sequence[str],
    *,
    cwd: Path,
    environment: Mapping[str, str],
) -> subprocess.CompletedProcess[bytes]:
    lexical, executable_bytes, identity_before = _read_regular_file(
        executable,
        maximum_bytes=256 * 1024 * 1024,
        name="pinned GitHub CLI executable",
        stage="witness",
    )
    if _sha256_bytes(executable_bytes) != PINNED_GH_EXE_SHA256:
        raise AcceptanceError("The GitHub CLI executable hash is not pinned", stage="witness")
    command = [str(lexical), *arguments]
    creation_flags = 0x08000000 if os.name == "nt" else 0
    try:
        result = subprocess.run(
            command,
            cwd=cwd,
            env=dict(environment),
            stdin=subprocess.DEVNULL,
            capture_output=True,
            timeout=GH_COMMAND_TIMEOUT_SECONDS,
            check=False,
            shell=False,
            creationflags=creation_flags,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise AcceptanceError("The pinned GitHub CLI could not run", stage="witness") from exc
    _, executable_after, identity_after = _read_regular_file(
        lexical,
        maximum_bytes=256 * 1024 * 1024,
        name="pinned GitHub CLI executable",
        stage="witness",
    )
    if (
        identity_after != identity_before
        or _sha256_bytes(executable_after) != PINNED_GH_EXE_SHA256
    ):
        raise AcceptanceError("The GitHub CLI executable changed while it ran", stage="witness")
    if (
        len(result.stdout) > MAX_GH_STDOUT_BYTES
        or len(result.stderr) > MAX_GH_STDERR_BYTES
    ):
        raise AcceptanceError("The GitHub CLI output exceeded its bound", stage="witness")
    if result.returncode != 0:
        raise AcceptanceError("GitHub release verification failed", stage="witness")
    return result


def _verify_release_statement(
    verification: Mapping[str, Any],
    *,
    release_tag: str,
    asset_name: str,
    asset_sha256: str,
    source_commit_sha1: str,
) -> None:
    attestation = verification.get("attestation")
    verification_result = verification.get("verificationResult")
    bundle = attestation.get("bundle") if isinstance(attestation, Mapping) else None
    envelope = bundle.get("dsseEnvelope") if isinstance(bundle, Mapping) else None
    statement = (
        verification_result.get("statement")
        if isinstance(verification_result, Mapping)
        else None
    )
    predicate = statement.get("predicate") if isinstance(statement, Mapping) else None
    subjects = statement.get("subject") if isinstance(statement, Mapping) else None
    signature = (
        verification_result.get("signature")
        if isinstance(verification_result, Mapping)
        else None
    )
    certificate = signature.get("certificate") if isinstance(signature, Mapping) else None
    timestamps = (
        verification_result.get("verifiedTimestamps")
        if isinstance(verification_result, Mapping)
        else None
    )
    if (
        not isinstance(attestation, Mapping)
        or not isinstance(bundle, Mapping)
        or bundle.get("mediaType") != "application/vnd.dev.sigstore.bundle.v0.3+json"
        or not isinstance(envelope, Mapping)
        or envelope.get("payloadType") != "application/vnd.in-toto+json"
        or not isinstance(envelope.get("payload"), str)
        or not envelope["payload"]
        or not isinstance(envelope.get("signatures"), list)
        or not envelope["signatures"]
        or not isinstance(verification_result, Mapping)
        or verification_result.get("mediaType")
        != "application/vnd.dev.sigstore.verificationresult+json;version=0.1"
        or not isinstance(certificate, Mapping)
        or certificate.get("subjectAlternativeName")
        != "https://dotcom.releases.github.com"
        or not isinstance(timestamps, list)
        or not timestamps
        or not isinstance(statement, Mapping)
        or statement.get("_type") != "https://in-toto.io/Statement/v1"
        or statement.get("predicateType")
        != "https://in-toto.io/attestation/release/v0.2"
        or not isinstance(predicate, Mapping)
        or predicate.get("repository") != PINNED_RELEASE_REPOSITORY
        or predicate.get("repositoryId") != PINNED_RELEASE_REPOSITORY_ID
        or predicate.get("ownerId") != PINNED_RELEASE_OWNER_ID
        or predicate.get("tag") != release_tag
        or predicate.get("purl")
        != f"pkg:github/{PINNED_RELEASE_REPOSITORY}@{release_tag}"
        or not isinstance(subjects, list)
        or not all(isinstance(subject, Mapping) for subject in subjects)
    ):
        raise AcceptanceError(
            "The GitHub release verification does not attest the witness asset",
            stage="witness",
        )
    try:
        decoded_payload = base64.b64decode(envelope["payload"], validate=True)
        payload_statement = _parse_json_bytes(
            decoded_payload,
            maximum_bytes=MAX_GH_STDOUT_BYTES,
            name="GitHub release DSSE payload",
            canonical=False,
        )
    except (binascii.Error, ValueError) as exc:
        raise AcceptanceError(
            "The GitHub release DSSE payload is invalid", stage="witness"
        ) from exc
    if sroie_policy.canonical_json(payload_statement) != sroie_policy.canonical_json(statement):
        raise AcceptanceError(
            "The GitHub release statement differs from its DSSE payload", stage="witness"
        )
    release_uri = f"pkg:github/{PINNED_RELEASE_REPOSITORY}@{release_tag}"
    release_subjects = [subject for subject in subjects if subject.get("uri") == release_uri]
    asset_subjects = [subject for subject in subjects if "name" in subject]
    asset_names = [subject.get("name") for subject in asset_subjects]
    matching_assets = [subject for subject in asset_subjects if subject.get("name") == asset_name]
    if (
        len(release_subjects) != 1
        or set(release_subjects[0]) != {"digest", "uri"}
        or release_subjects[0].get("digest") != {"sha1": source_commit_sha1}
        or len(asset_names) != len(set(asset_names))
        or len(matching_assets) != 1
        or set(matching_assets[0]) != {"digest", "name"}
        or matching_assets[0].get("digest") != {"sha256": asset_sha256}
    ):
        raise AcceptanceError(
            "The GitHub release verification does not bind the exact witness bytes",
            stage="witness",
        )


def _load_witness(
    gh_executable: Path,
    release_tag: str,
    witness_asset: str,
    *,
    context: CalibrationContext,
    policy_sha256: str,
    identity: Mapping[str, Any],
) -> ValidatedWitness:
    if (
        not release_tag
        or release_tag.strip() != release_tag
        or any(character.isspace() for character in release_tag)
        or not _safe_release_asset_name(witness_asset)
    ):
        raise AcceptanceError("The immutable release selector is unsafe", stage="witness")
    with tempfile.TemporaryDirectory(prefix="bstrings-sroie-gh-") as temporary:
        root = Path(temporary)
        environment = _fresh_gh_environment(root)
        version_result = _run_pinned_gh(
            gh_executable,
            ("--version",),
            cwd=root,
            environment=environment,
        )
        try:
            first_version_line = version_result.stdout.decode("utf-8", "strict").splitlines()[0]
        except (UnicodeDecodeError, IndexError) as exc:
            raise AcceptanceError(
                "The GitHub CLI version output is invalid", stage="witness"
            ) from exc
        if not first_version_line.startswith(f"gh version {PINNED_GH_VERSION} "):
            raise AcceptanceError("The GitHub CLI version is not pinned", stage="witness")
        verification_result = _run_pinned_gh(
            gh_executable,
            (
                "release",
                "verify",
                release_tag,
                "--repo",
                PINNED_RELEASE_REPOSITORY,
                "--format",
                "json",
            ),
            cwd=root,
            environment=environment,
        )
        verification = _parse_json_bytes(
            verification_result.stdout,
            maximum_bytes=MAX_GH_STDOUT_BYTES,
            name="GitHub release verification",
            canonical=False,
        )
        download_root = root / "download"
        download_root.mkdir(exist_ok=False)
        _run_pinned_gh(
            gh_executable,
            (
                "release",
                "download",
                release_tag,
                "--repo",
                PINNED_RELEASE_REPOSITORY,
                "--pattern",
                witness_asset,
                "--dir",
                str(download_root),
            ),
            cwd=root,
            environment=environment,
        )
        entries = list(download_root.iterdir())
        if len(entries) != 1 or entries[0].name != witness_asset:
            raise AcceptanceError("The exact witness asset was not downloaded", stage="witness")
        _, raw_witness, _ = _read_regular_file(
            entries[0],
            maximum_bytes=1024 * 1024,
            name="downloaded immutable witness",
            stage="witness",
        )
        value = _parse_json_bytes(
            raw_witness,
            maximum_bytes=1024 * 1024,
            name="downloaded immutable witness",
            canonical=True,
        )
    file_sha256 = _sha256_bytes(raw_witness)
    expected_keys = {
        "calibrationReportSha256",
        "candidateIdentitySha256",
        "datasetId",
        "datasetRevision",
        "expectedTestBytes",
        "expectedTestSha256",
        "githubImmutableRelease",
        "policySha256",
        "protocol",
        "schemaVersion",
        "sourceCommitSha1",
    }
    release = value.get("githubImmutableRelease")
    if (
        set(value) != expected_keys
        or value.get("schemaVersion") != SCHEMA_VERSION
        or value.get("protocol") != PROTOCOL
        or value.get("datasetId") != sroie_policy.DATASET_ID
        or value.get("datasetRevision") != sroie_policy.DATASET_REVISION
        or value.get("expectedTestBytes") != sroie_policy.TEST_BYTES
        or value.get("expectedTestSha256") != sroie_policy.TEST_SHA256
        or value.get("calibrationReportSha256") != context.report_sha256
        or value.get("policySha256") != policy_sha256
        or value.get("candidateIdentitySha256") != sroie_policy.sha256_canonical(identity)
        or not isinstance(release, Mapping)
        or set(release) != {"assetName", "repository", "tag"}
        or release.get("repository") != PINNED_RELEASE_REPOSITORY
        or release.get("assetName") != witness_asset
        or release.get("tag") != release_tag
        or not isinstance(value.get("sourceCommitSha1"), str)
        or len(value["sourceCommitSha1"]) != 40
        or any(character not in "0123456789abcdef" for character in value["sourceCommitSha1"])
        or any(
            not isinstance(release.get(name), str) or not release[name].strip()
            for name in ("assetName", "tag")
        )
    ):
        raise AcceptanceError("The immutable release witness is invalid", stage="witness")
    _verify_release_statement(
        verification,
        release_tag=release_tag,
        asset_name=witness_asset,
        asset_sha256=file_sha256,
        source_commit_sha1=value["sourceCommitSha1"],
    )
    verification_sha256 = _sha256_bytes(_canonical_bytes(verification))
    return ValidatedWitness(
        value=value,
        file_sha256=file_sha256,
        release_verification=verification,
        release_verification_sha256=verification_sha256,
        gh_executable_sha256=PINNED_GH_EXE_SHA256,
        gh_version=PINNED_GH_VERSION,
    )


def _start_attempt(
    context: CalibrationContext,
    validated_policy: Any,
    identity: Mapping[str, Any],
    witness: ValidatedWitness,
    report_output: Path,
) -> AttemptClaim:
    ledger_parents = _prepare_machine_ledger_directory()
    value = {
        "calibrationReportSha256": context.report_sha256,
        "candidateIdentitySha256": sroie_policy.sha256_canonical(identity),
        "datasetId": sroie_policy.DATASET_ID,
        "datasetRevision": sroie_policy.DATASET_REVISION,
        "expectedTestBytes": sroie_policy.TEST_BYTES,
        "expectedTestSha256": sroie_policy.TEST_SHA256,
        "phase": "confirmatory",
        "policySha256": validated_policy.file_sha256,
        "protocol": PROTOCOL,
        "reportOutputPathSha256": _sha256_bytes(
            os.path.normcase(str(_lexical_absolute(report_output))).encode("utf-8")
        ),
        "schemaVersion": SCHEMA_VERSION,
        "startedAtUtc": benchmark_core.utc_timestamp(),
        "status": "started",
        "witnessSha256": witness.file_sha256,
        "witnessReleaseVerificationSha256": witness.release_verification_sha256,
        "witnessGhExecutableSha256": witness.gh_executable_sha256,
        "witnessGhVersion": witness.gh_version,
    }
    raw = _canonical_bytes(value)
    _atomic_create(CONFIRMATORY_ATTEMPT_LEDGER, raw)
    claim = AttemptClaim(
        path=CONFIRMATORY_ATTEMPT_LEDGER,
        value=value,
        sha256=_sha256_bytes(raw),
    )
    _event("ledger-created")
    if not _parents_stable(ledger_parents):
        raise AcceptanceError(
            "The machine-global ledger parent changed during claim creation",
            stage="one-shot",
            claim=claim,
        )
    return claim


def _prepare_machine_ledger_directory(
) -> tuple[tuple[Path, tuple[int | None, int | None]], ...]:
    expected = _lexical_absolute(
        _MACHINE_STATE_ROOT / "bstrings" / "acceptance-ledgers"
    )
    if _lexical_absolute(CONFIRMATORY_ATTEMPT_LEDGER).parent != expected:
        raise AcceptanceError("The machine-global ledger namespace changed", stage="one-shot")
    components = (_lexical_absolute(_MACHINE_STATE_ROOT), expected.parent, expected)
    snapshots: list[tuple[Path, tuple[int | None, int | None]]] = []
    for component in components:
        if not component.exists():
            try:
                os.mkdir(component, 0o700)
            except OSError as exc:
                raise AcceptanceError(
                    "The machine-global ledger directory cannot be created",
                    stage="one-shot",
                ) from exc
        try:
            value = os.lstat(component)
        except OSError as exc:
            raise AcceptanceError(
                "The machine-global ledger directory is unavailable", stage="one-shot"
            ) from exc
        if (
            not stat.S_ISDIR(value.st_mode)
            or stat.S_ISLNK(value.st_mode)
            or _unsafe_file_attributes(value)
        ):
            raise AcceptanceError(
                "The machine-global ledger directory is unsafe", stage="one-shot"
            )
        snapshots.append((component, _directory_object_id(value)))
    if not _parents_stable(snapshots):
        raise AcceptanceError(
            "The machine-global ledger directory changed", stage="one-shot"
        )
    return tuple(snapshots)


def _load_claim(claim: AttemptClaim) -> dict[str, Any]:
    value, digest = _strict_json(claim.path, maximum_bytes=2 * 1024 * 1024, name="attempt ledger")
    if digest != claim.sha256 or sroie_policy.canonical_json(value) != sroie_policy.canonical_json(
        claim.value
    ):
        raise AcceptanceError("The immutable attempt claim changed", stage="one-shot")
    return value


def _load_attempt_state(claim: AttemptClaim) -> dict[str, Any]:
    value, _ = _strict_json(
        claim.path, maximum_bytes=2 * 1024 * 1024, name="attempt ledger"
    )
    if any(
        key != "status" and value.get(key) != expected
        for key, expected in claim.value.items()
    ):
        raise AcceptanceError("The immutable attempt claim fields changed", stage="one-shot")
    if value.get("status") != "started" and value.get("claimSha256") != claim.sha256:
        raise AcceptanceError("The attempt state does not bind its initial claim", stage="one-shot")
    return value


def _confirmatory_report_bindings(report: Mapping[str, Any]) -> dict[str, Any]:
    expected_report_keys = {
        "acceptancePassed",
        "documents",
        "evaluationCompleted",
        "evaluationRole",
        "evidence",
        "expectedIdentitiesSha256",
        "finalDisposition",
        "identity",
        "identitySha256",
        "integrityPassed",
        "metrics",
        "metricsSha256",
        "protocol",
        "rawRows",
        "runSucceeded",
        "schemaVersion",
    }
    evidence = report.get("evidence")
    artifacts = evidence.get("artifacts") if isinstance(evidence, Mapping) else None
    duplicate_audit = (
        artifacts.get("confirmatoryDuplicateAudit") if isinstance(artifacts, Mapping) else None
    )
    duplicate_audit_sha256 = (
        duplicate_audit.get("sha256") if isinstance(duplicate_audit, Mapping) else None
    )
    test_snapshot_sha256 = (
        evidence.get("testSnapshotSha256") if isinstance(evidence, Mapping) else None
    )
    acceptance_passed = report.get("acceptancePassed")
    integrity_passed = report.get("integrityPassed")
    run_succeeded = report.get("runSucceeded")
    final_disposition = report.get("finalDisposition")
    identity = report.get("identity")
    metrics = report.get("metrics")
    expected_identities_sha256 = report.get("expectedIdentitiesSha256")

    def is_sha256(candidate: Any) -> bool:
        return (
            isinstance(candidate, str)
            and len(candidate) == 64
            and all(character in "0123456789abcdef" for character in candidate)
        )

    if (
        set(report) != expected_report_keys
        or type(report.get("documents")) is not int
        or report["documents"] != sroie_policy.RAW_TEST_ROWS
        or type(report.get("rawRows")) is not int
        or report["rawRows"] != sroie_policy.RAW_TEST_ROWS
        or report.get("evaluationCompleted") is not True
        or report.get("evaluationRole") != "confirmatory"
        or report.get("protocol") != PROTOCOL
        or report.get("schemaVersion") != SCHEMA_VERSION
        or type(acceptance_passed) is not bool
        or type(integrity_passed) is not bool
        or run_succeeded is not True
        or final_disposition != ("accepted" if acceptance_passed else "rejected")
        or (acceptance_passed and not integrity_passed)
        or not isinstance(identity, Mapping)
        or report.get("identitySha256") != sroie_policy.sha256_canonical(identity)
        or not isinstance(metrics, Mapping)
        or report.get("metricsSha256") != sroie_policy.sha256_canonical(metrics)
        or not is_sha256(expected_identities_sha256)
        or not is_sha256(duplicate_audit_sha256)
        or test_snapshot_sha256 != sroie_policy.TEST_SHA256
    ):
        raise AcceptanceError(
            "The confirmatory report cannot complete the ledger", stage="one-shot"
        )
    return {
        "acceptancePassed": acceptance_passed,
        "confirmatoryDocuments": report["documents"],
        "confirmatoryDuplicateAuditSha256": duplicate_audit_sha256,
        "confirmatoryImageIdentitiesSha256": expected_identities_sha256,
        "confirmatoryRawRows": report["rawRows"],
        "evaluationCompleted": True,
        "finalDisposition": final_disposition,
        "integrityPassed": integrity_passed,
        "runSucceeded": run_succeeded,
        "testSnapshotSha256": test_snapshot_sha256,
    }


def _prepare_attempt_report(
    claim: AttemptClaim, report: Mapping[str, Any], staged: StagedReport
) -> None:
    value = _load_claim(claim)
    _verify_report_file(
        staged.path,
        sha256=staged.sha256,
        byte_length=staged.byte_length,
        file_identity=staged.file_identity,
    )
    value.update(_confirmatory_report_bindings(report))
    value.update(
        {
            "claimSha256": claim.sha256,
            "reportBytes": staged.byte_length,
            "reportSha256": staged.sha256,
            "reportStagedAtUtc": benchmark_core.utc_timestamp(),
            "stagedFileIdentity": list(staged.file_identity),
            "status": "report-staged",
        }
    )
    _atomic_replace(claim.path, _canonical_bytes(value))
    _event("report-staged")


def _publish_prepared_report(
    claim: AttemptClaim, output: Path, marker: Path, staged: StagedReport
) -> None:
    value = _load_attempt_state(claim)
    if (
        value.get("status") != "report-staged"
        or value.get("reportSha256") != staged.sha256
        or value.get("reportBytes") != staged.byte_length
        or value.get("stagedFileIdentity") != list(staged.file_identity)
        or output.exists()
        or not marker.exists()
    ):
        raise AcceptanceError("The prepared report state changed", stage="report")
    _require_report_marker(marker)
    _verify_report_file(
        staged.path,
        sha256=staged.sha256,
        byte_length=staged.byte_length,
        file_identity=staged.file_identity,
    )
    _durable_replace(staged.path, output)
    _verify_report_file(
        output, sha256=staged.sha256, byte_length=staged.byte_length
    )
    _event("report-published")


def _complete_attempt(claim: AttemptClaim, output: Path) -> None:
    value = _load_attempt_state(claim)
    if value.get("status") != "report-staged":
        raise AcceptanceError("The attempt is not ready for completion", stage="one-shot")
    _, raw, _ = _read_regular_file(
        output, maximum_bytes=MAX_REPORT_BYTES, name="published report", stage="report"
    )
    if (
        len(raw) != value.get("reportBytes")
        or _sha256_bytes(raw) != value.get("reportSha256")
    ):
        raise AcceptanceError("The published report differs from the ledger", stage="report")
    report = _parse_json_bytes(
        raw, maximum_bytes=MAX_REPORT_BYTES, name="published report", canonical=True
    )
    if _confirmatory_report_bindings(report) != {
        key: value.get(key) for key in _confirmatory_report_bindings(report)
    }:
        raise AcceptanceError("The published report bindings changed", stage="report")
    value.update(
        {
            "completedAtUtc": benchmark_core.utc_timestamp(),
            "status": "completed",
        }
    )
    _atomic_replace(claim.path, _canonical_bytes(value))
    _event("ledger-completed")


def _seal_report(claim: AttemptClaim, output: Path, marker: Path) -> None:
    value = _load_attempt_state(claim)
    if value.get("status") != "completed" or not marker.exists():
        raise AcceptanceError("The report cannot be sealed", stage="report")
    _require_report_marker(marker)
    _verify_report_file(
        output,
        sha256=value["reportSha256"],
        byte_length=value["reportBytes"],
    )
    _durable_remove_marker(marker)
    final = _load_attempt_state(claim)
    _verify_report_file(
        output,
        sha256=final["reportSha256"],
        byte_length=final["reportBytes"],
    )
    if final.get("status") != "completed" or marker.exists():
        raise AcceptanceError("The final report seal is incomplete", stage="report")
    _event("report-sealed")


def _claim_from_persisted_state(path: Path, value: Mapping[str, Any]) -> AttemptClaim:
    initial_keys = {
        "calibrationReportSha256",
        "candidateIdentitySha256",
        "datasetId",
        "datasetRevision",
        "expectedTestBytes",
        "expectedTestSha256",
        "phase",
        "policySha256",
        "protocol",
        "reportOutputPathSha256",
        "schemaVersion",
        "startedAtUtc",
        "status",
        "witnessGhExecutableSha256",
        "witnessGhVersion",
        "witnessReleaseVerificationSha256",
        "witnessSha256",
    }
    if not initial_keys.issubset(value):
        raise AcceptanceError("The persisted attempt claim is incomplete", stage="recovery")
    initial = {key: value[key] for key in initial_keys}
    initial["status"] = "started"
    digest = _sha256_bytes(_canonical_bytes(initial))
    if value.get("status") != "started" and value.get("claimSha256") != digest:
        raise AcceptanceError("The persisted attempt claim digest changed", stage="recovery")
    return AttemptClaim(path=path, value=initial, sha256=digest)


def _recover_report_commit(output: Path, *, expected_claim: AttemptClaim) -> dict[str, Any]:
    output = _lexical_absolute(output)
    marker = output.with_name(output.name + ".incomplete")
    staged_path = output.with_name(output.name + ".staged")
    value, _ = _strict_json(
        CONFIRMATORY_ATTEMPT_LEDGER,
        maximum_bytes=2 * 1024 * 1024,
        name="attempt ledger",
    )
    expected_output_hash = _sha256_bytes(
        os.path.normcase(str(output)).encode("utf-8")
    )
    if value.get("reportOutputPathSha256") != expected_output_hash:
        raise AcceptanceError("The recovery output differs from the claim", stage="recovery")
    claim = _claim_from_persisted_state(CONFIRMATORY_ATTEMPT_LEDGER, value)
    if (
        claim.sha256 != expected_claim.sha256
        or sroie_policy.canonical_json(claim.value)
        != sroie_policy.canonical_json(expected_claim.value)
    ):
        raise AcceptanceError(
            "The recovery ledger does not match the authenticated initial claim",
            stage="recovery",
        )
    status = value.get("status")
    if status == "report-staged":
        if not marker.exists() or staged_path.exists() == output.exists():
            raise AcceptanceError("The prepared report is not recoverable", stage="recovery")
        if staged_path.exists():
            identity_value = value.get("stagedFileIdentity")
            if not isinstance(identity_value, list) or len(identity_value) != 4:
                raise AcceptanceError("The staged report identity is invalid", stage="recovery")
            staged = StagedReport(
                path=staged_path,
                sha256=value["reportSha256"],
                byte_length=value["reportBytes"],
                file_identity=tuple(identity_value),
            )
            _publish_prepared_report(claim, output, marker, staged)
        else:
            _verify_report_file(
                output,
                sha256=value["reportSha256"],
                byte_length=value["reportBytes"],
            )
        _complete_attempt(claim, output)
        _seal_report(claim, output, marker)
    elif status == "completed":
        if staged_path.exists() or not output.exists():
            raise AcceptanceError("The completed report is not recoverable", stage="recovery")
        _verify_report_file(
            output,
            sha256=value["reportSha256"],
            byte_length=value["reportBytes"],
        )
        if marker.exists():
            _seal_report(claim, output, marker)
    else:
        raise AcceptanceError(
            "The one-shot attempt cannot resume held-out evaluation", stage="recovery"
        )
    report, report_sha256 = _strict_json(
        output, maximum_bytes=MAX_REPORT_BYTES, name="recovered report"
    )
    final = _load_attempt_state(claim)
    if (
        final.get("status") != "completed"
        or final.get("reportSha256") != report_sha256
        or marker.exists()
    ):
        raise AcceptanceError("The recovered report is not terminal", stage="recovery")
    return report


def _fail_attempt(claim: AttemptClaim, error: Exception) -> None:
    value = _load_attempt_state(claim)
    if value.get("status") not in {"started", "report-staged"}:
        raise AcceptanceError("The attempt cannot transition to failed", stage="terminalization")
    value.update(
        {
            "acceptancePassed": False,
            "claimSha256": claim.sha256,
            "failedAtUtc": benchmark_core.utc_timestamp(),
            "failure": _failure_details(error),
            "finalDisposition": "failed",
            "integrityPassed": False,
            "runSucceeded": False,
            "status": "failed",
        }
    )
    _atomic_replace(claim.path, _canonical_bytes(value))


def _quarantine(args: argparse.Namespace, claim: AttemptClaim) -> None:
    failed = _load_attempt_state(claim)
    if failed.get("status") != "failed":
        raise AcceptanceError("Only a failed attempt can be quarantined", stage="terminalization")
    marker_value = {
        "claimSha256": claim.sha256,
        "quarantinedAtUtc": benchmark_core.utc_timestamp(),
        "schemaVersion": SCHEMA_VERSION,
        "status": "quarantined",
    }
    global_marker = claim.path.with_name(claim.path.name + ".quarantined.json")
    if not global_marker.exists():
        _atomic_create(global_marker, _canonical_bytes(marker_value))
    phase = _lexical_absolute(args.work_directory) / "confirmatory"
    phase.mkdir(parents=True, exist_ok=True)
    local_marker = phase / "CONFIRMATORY_EVIDENCE_QUARANTINED.json"
    if not local_marker.exists():
        _atomic_create(local_marker, _canonical_bytes(marker_value))
    ledger, _ = _strict_json(claim.path, maximum_bytes=2 * 1024 * 1024, name="attempt ledger")
    ledger.update(
        {
            "quarantineMarkerSha256": benchmark_core.sha256_file(global_marker),
            "quarantinedAtUtc": marker_value["quarantinedAtUtc"],
            "status": "quarantined",
        }
    )
    _atomic_replace(claim.path, _canonical_bytes(ledger))
    final = _load_attempt_state(claim)
    if final.get("status") != "quarantined":
        raise AcceptanceError("The attempt quarantine was not persisted", stage="terminalization")
    _event("attempt-quarantined")


def _terminalize_failed_attempt(
    args: argparse.Namespace, claim: AttemptClaim, error: Exception
) -> None:
    try:
        _fail_attempt(claim, error)
        _quarantine(args, claim)
    except Exception as exc:
        raise AcceptanceError(
            "The postclaim failure could not be durably quarantined",
            stage="terminalization",
        ) from exc


def _read_test_path_after_claim() -> str:
    _event("test-path-received")
    if _TEST_PATH_PIPE_DESCRIPTOR is None:
        stream = getattr(sys.stdin, "buffer", sys.stdin)
        raw = stream.readline(32_769)
        extra = stream.read(1)
        if isinstance(raw, str):
            raw = raw.encode("utf-8", "strict")
    else:
        with os.fdopen(os.dup(_TEST_PATH_PIPE_DESCRIPTOR), "rb") as stream:
            raw = stream.readline(32_769)
            extra = stream.read(1)
    if not raw or len(raw) > 32_768 or not raw.endswith(b"\n"):
        raise AcceptanceError(
            "The held-out test path pipe is empty or malformed", stage="test-identity"
        )
    if extra not in {b"", ""}:
        raise AcceptanceError(
            "The held-out test path pipe contains extra data", stage="test-identity"
        )
    try:
        decoded = raw[:-1].removesuffix(b"\r").decode("utf-8", "strict")
    except UnicodeDecodeError as exc:
        raise AcceptanceError(
            "The held-out test path is not strict UTF-8", stage="test-identity"
        ) from exc
    if not decoded or "\x00" in decoded or "\n" in decoded or "\r" in decoded:
        raise AcceptanceError("The held-out test path is invalid", stage="test-identity")
    return _held_out_path(decoded)


def _create_fresh_snapshot_directory(
    root: Path,
) -> tuple[Path, tuple[tuple[Path, tuple[int | None, int | None]], ...]]:
    root = _lexical_absolute(root)
    missing: list[Path] = []
    cursor = root
    try:
        while True:
            try:
                existing = os.lstat(cursor)
                break
            except FileNotFoundError:
                missing.append(cursor)
                if cursor == cursor.parent:
                    raise AcceptanceError(
                        "The snapshot destination has no safe existing ancestor",
                        stage="test-snapshot",
                    ) from None
                cursor = cursor.parent
        if not missing or missing[0] != root:
            raise AcceptanceError(
                "The snapshot destination is not fresh", stage="test-snapshot"
            )
        existing_components = [*reversed(cursor.parents), cursor]
        snapshots: list[tuple[Path, tuple[int | None, int | None]]] = []
        for component in existing_components:
            value = os.lstat(component)
            if (
                not stat.S_ISDIR(value.st_mode)
                or stat.S_ISLNK(value.st_mode)
                or _unsafe_file_attributes(value)
            ):
                raise AcceptanceError(
                    "The snapshot destination has an unsafe parent",
                    stage="test-snapshot",
                )
            snapshots.append((component, _directory_object_id(value)))
        if (
            not stat.S_ISDIR(existing.st_mode)
            or stat.S_ISLNK(existing.st_mode)
            or _unsafe_file_attributes(existing)
            or not _parents_stable(snapshots)
        ):
            raise AcceptanceError(
                "The snapshot destination has an unsafe parent",
                stage="test-snapshot",
            )
        for component in reversed(missing):
            if not _parents_stable(snapshots):
                raise AcceptanceError(
                    "The snapshot destination parent changed",
                    stage="test-snapshot",
                )
            os.mkdir(component, 0o700)
            value = os.lstat(component)
            if (
                not stat.S_ISDIR(value.st_mode)
                or stat.S_ISLNK(value.st_mode)
                or _unsafe_file_attributes(value)
            ):
                raise AcceptanceError(
                    "The snapshot destination was redirected",
                    stage="test-snapshot",
                )
            snapshots.append((component, _directory_object_id(value)))
            if not _parents_stable(snapshots):
                raise AcceptanceError(
                    "The snapshot destination parent changed",
                    stage="test-snapshot",
                )
    except AcceptanceError:
        raise
    except OSError as exc:
        raise AcceptanceError(
            "The snapshot destination could not be created safely",
            stage="test-snapshot",
        ) from exc
    return root, tuple(snapshots)


def _remove_snapshot_temporary(
    temporary: Path,
    destination_chain: Sequence[tuple[Path, tuple[int | None, int | None]]],
    expected_object_id: tuple[int | None, int | None] | None,
) -> None:
    if not _parents_stable(destination_chain):
        return
    try:
        current = os.lstat(temporary)
        if (
            expected_object_id is not None
            and _directory_object_id(current) == expected_object_id
            and stat.S_ISREG(current.st_mode)
            and not stat.S_ISLNK(current.st_mode)
            and not _unsafe_file_attributes(current)
        ):
            temporary.unlink()
    except OSError:
        return


def _snapshot_test_after_claim(
    raw_path: str,
    root: Path,
    *,
    forbidden_file_ids: frozenset[tuple[int | None, int | None]] = frozenset(),
) -> tuple[Path, str]:
    try:
        return _snapshot_test_after_claim_inner(
            raw_path,
            root,
            forbidden_file_ids=forbidden_file_ids,
        )
    except AcceptanceError:
        raise
    except (OSError, ValueError) as exc:
        raise AcceptanceError(
            "The held-out snapshot filesystem operation failed",
            stage="test-snapshot",
        ) from exc


def _snapshot_test_after_claim_inner(
    raw_path: str,
    root: Path,
    *,
    forbidden_file_ids: frozenset[tuple[int | None, int | None]],
) -> tuple[Path, str]:
    _event("test-path-materialized")
    source = _lexical_absolute(Path(raw_path))
    config = sroie.SROIE_FILES["test"]
    expected_name = Path(str(config["filename"])).name
    if source.name != expected_name:
        raise AcceptanceError("The held-out test filename changed", stage="test-identity")
    _event("test-lstat")
    try:
        parent_identities: list[tuple[Path, tuple[int | None, ...]]] = []
        for parent in reversed(source.parents):
            parent_stat = os.lstat(parent)
            if (
                not stat.S_ISDIR(parent_stat.st_mode)
                or stat.S_ISLNK(parent_stat.st_mode)
                or _unsafe_file_attributes(parent_stat)
            ):
                raise AcceptanceError(
                    "The held-out test has an unsafe parent", stage="test-identity"
                )
            parent_identities.append((parent, _directory_object_id(parent_stat)))
        source_lstat = os.lstat(source)
    except OSError as exc:
        raise AcceptanceError(
            "The held-out test source is unavailable", stage="test-identity"
        ) from exc
    attributes = getattr(source_lstat, "st_file_attributes", 0)
    if not stat.S_ISREG(source_lstat.st_mode) or attributes & getattr(
        stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0
    ):
        raise AcceptanceError(
            "The held-out test source is not a regular file", stage="test-identity"
        )
    root, destination_chain = _create_fresh_snapshot_directory(root)
    if not _parents_stable(destination_chain):
        raise AcceptanceError(
            "The snapshot destination parent changed before source access",
            stage="test-snapshot",
        )
    _event("snapshot-destination-ready")
    destination = root / expected_name
    temporary = destination.with_name(destination.name + ".incomplete")
    digest = hashlib.sha256()
    total = 0
    temporary_object_id: tuple[int | None, int | None] | None = None
    persisted_temporary_identity: tuple[int | None, ...] | None = None
    _event("test-open")
    source_descriptor = os.open(
        source,
        os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0),
    )
    try:
        before = os.fstat(source_descriptor)
        if (
            _file_identity(before) != _file_identity(source_lstat)
            or (before.st_dev, before.st_ino) in forbidden_file_ids
        ):
            raise AcceptanceError(
                "The held-out test source changed or aliases a preclaim input",
                stage="test-identity",
            )
        output_descriptor = os.open(
            temporary,
            os.O_WRONLY
            | os.O_CREAT
            | os.O_EXCL
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        try:
            output_before = os.fstat(output_descriptor)
            temporary_lstat = os.lstat(temporary)
            temporary_object_id = _directory_object_id(output_before)
            if (
                not stat.S_ISREG(output_before.st_mode)
                or _unsafe_file_attributes(output_before)
                or _file_identity(temporary_lstat) != _file_identity(output_before)
                or stat.S_ISLNK(temporary_lstat.st_mode)
                or _unsafe_file_attributes(temporary_lstat)
                or not _parents_stable(destination_chain)
            ):
                raise AcceptanceError(
                    "The snapshot temporary file was redirected",
                    stage="test-snapshot",
                )
            with (
                os.fdopen(source_descriptor, "rb", closefd=False) as input_handle,
                os.fdopen(output_descriptor, "wb") as output_handle,
            ):
                while True:
                    chunk = input_handle.read(HASH_CHUNK_BYTES)
                    if not chunk:
                        break
                    digest.update(chunk)
                    total += len(chunk)
                    output_handle.write(chunk)
                output_handle.flush()
                os.fsync(output_handle.fileno())
                output_after = os.fstat(output_handle.fileno())
            after = os.fstat(source_descriptor)
        except Exception:
            _remove_snapshot_temporary(
                temporary, destination_chain, temporary_object_id
            )
            raise
    finally:
        os.close(source_descriptor)
    try:
        final_stat = os.lstat(source)
        parents_stable = _parents_stable(parent_identities)
    except OSError as exc:
        _remove_snapshot_temporary(temporary, destination_chain, temporary_object_id)
        raise AcceptanceError(
            "The held-out test changed while it was snapshotted", stage="test-toctou"
        ) from exc
    if (
        _file_identity(before) != _file_identity(after)
        or _file_identity(after) != _file_identity(final_stat)
        or stat.S_ISLNK(final_stat.st_mode)
        or _unsafe_file_attributes(final_stat)
        or not parents_stable
    ):
        _remove_snapshot_temporary(temporary, destination_chain, temporary_object_id)
        raise AcceptanceError(
            "The held-out test changed while it was snapshotted", stage="test-toctou"
        )
    temporary_final = os.lstat(temporary)
    persisted_temporary_identity = _file_identity(output_after)
    if (
        temporary_object_id is None
        or _directory_object_id(output_before) != temporary_object_id
        or _directory_object_id(output_after) != temporary_object_id
        or _file_identity(output_after) != _file_identity(temporary_final)
        or output_after.st_size != total
        or not stat.S_ISREG(temporary_final.st_mode)
        or stat.S_ISLNK(temporary_final.st_mode)
        or _unsafe_file_attributes(temporary_final)
        or not _parents_stable(destination_chain)
    ):
        _remove_snapshot_temporary(temporary, destination_chain, temporary_object_id)
        raise AcceptanceError(
            "The snapshot destination changed while bytes were copied",
            stage="test-snapshot",
        )
    computed = digest.hexdigest()
    if total != sroie_policy.TEST_BYTES or computed != sroie_policy.TEST_SHA256:
        _remove_snapshot_temporary(temporary, destination_chain, temporary_object_id)
        raise AcceptanceError("The held-out test size or SHA-256 is wrong", stage="test-identity")
    if not _parents_stable(destination_chain):
        raise AcceptanceError(
            "The snapshot destination changed before publication",
            stage="test-snapshot",
        )
    _durable_replace(temporary, destination)
    _, persisted, destination_identity = _read_regular_file(
        destination,
        maximum_bytes=sroie_policy.TEST_BYTES,
        name="verified held-out snapshot",
        stage="test-snapshot",
    )
    if (
        len(persisted) != total
        or _sha256_bytes(persisted) != computed
        or destination_identity != persisted_temporary_identity
        or not _parents_stable(destination_chain)
    ):
        raise AcceptanceError(
            "The published held-out snapshot identity changed",
            stage="test-snapshot",
        )
    _event("test-snapshot-verified")
    return destination, computed


def _verify_persisted_runtime_context(context: CalibrationContext, frozen: FrozenCandidate) -> None:
    for name, path in context.runtime_inventories.items():
        value, _ = _strict_json(
            path, maximum_bytes=512 * 1024 * 1024, name=f"{name} runtime inventory"
        )
        if sroie_policy.canonical_json(value) != sroie_policy.canonical_json(frozen.runtimes[name]):
            raise AcceptanceError(
                "A live runtime differs from calibration", stage="runtime-identity"
            )


def _preclaim_file_object_ids(
    args: argparse.Namespace,
    frozen: FrozenCandidate,
    context: CalibrationContext,
) -> frozenset[tuple[int | None, int | None]]:
    paths = {
        "acceptance wrapper": _SOURCE_FILE,
        "benchmark Python runtime": Path(sys.executable),
        "CORD scorer source": Path(cord.__file__),
        "calibration corpus manifest": context.corpus_manifest,
        "calibration duplicate audit": context.duplicate_audit,
        "calibration inventory": context.inventory,
        "calibration report": args.calibration_report,
        "calibration worker manifest": context.worker_manifest,
        "CPU Python runtime": frozen.cpu_backend.python_executable,
        "DirectML Python runtime": frozen.directml_backend.python_executable,
        "GitHub CLI executable": args.gh_executable,
        "model-pack manifest": frozen.model_pack,
        "policy": args.policy,
        "report incomplete marker": _lexical_absolute(args.output).with_name(
            _lexical_absolute(args.output).name + ".incomplete"
        ),
        "SROIE adapter source": Path(sroie.__file__),
        "SROIE policy source": Path(sroie_policy.__file__),
        "generic benchmark source": Path(benchmark_core.__file__),
        "generic policy source": Path(generic_policy.__file__),
        "worker": frozen.worker,
    }
    for name, path in context.runtime_inventories.items():
        paths[f"{name} runtime inventory"] = path
    identifiers = {
        _regular_file_object_id(path, name=name) for name, path in paths.items()
    }
    return frozenset(identifiers)


def _require_disjoint_source_images(
    calibration_source_digests: Sequence[str], test_source_digests: Sequence[str]
) -> None:
    if set(calibration_source_digests) & set(test_source_digests):
        raise AcceptanceError(
            "Train and held-out test contain duplicate images", stage="holdout-independence"
        )


def _meaningful_hybrid_lane_coverage(run: Mapping[str, Any]) -> bool:
    coverage = run.get("hybridLaneRecordCoverage")
    total = run.get("stringRecords")
    cpu = coverage.get("cpuLaneRecords") if isinstance(coverage, Mapping) else None
    non_cpu = coverage.get("nonCpuLaneRecords") if isinstance(coverage, Mapping) else None
    if any(type(value) is not int or value < 0 for value in (total, cpu, non_cpu)):
        return False
    minimum = max(1, math.ceil(total * HYBRID_MINIMUM_LANE_FRACTION))
    return (
        total > 0
        and cpu + non_cpu == total
        and cpu >= minimum
        and non_cpu >= minimum
        and coverage.get("bothLanesProducedRecords") is True
    )


def execute_confirmatory(
    args: argparse.Namespace,
    frozen: FrozenCandidate,
    context: CalibrationContext,
) -> tuple[dict[str, Any], AttemptClaim]:
    phase_root = _lexical_absolute(args.work_directory) / "confirmatory"
    if phase_root.exists():
        raise AcceptanceError("The confirmatory work directory already exists", stage="one-shot")
    identity = build_identity(frozen, context.identity["calibrationCorpus"])
    if sroie_policy.canonical_json(identity) != sroie_policy.canonical_json(context.identity):
        raise AcceptanceError("The live identity differs from calibration", stage="frozen-identity")
    _verify_persisted_runtime_context(context, frozen)
    try:
        validated_policy = sroie_policy.validate_policy(
            args.policy,
            calibration_report_path=args.calibration_report,
            expected_identity=identity,
            expected_identities=context.expected_identities,
        )
    except sroie_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="policy") from exc
    _recheck_policy(args.policy, validated_policy)
    witness = _load_witness(
        args.gh_executable,
        args.release_tag,
        args.witness_asset,
        context=context,
        policy_sha256=validated_policy.file_sha256,
        identity=identity,
    )
    protected_file_ids = _preclaim_file_object_ids(args, frozen, context)
    try:
        claim = _start_attempt(context, validated_policy, identity, witness, args.output)
    except AcceptanceError as exc:
        if isinstance(exc.claim, AttemptClaim):
            args._attempt_claim = exc.claim
        raise
    args._attempt_claim = claim
    _recheck_policy(args.policy, validated_policy)
    witness_after_claim = _load_witness(
        args.gh_executable,
        args.release_tag,
        args.witness_asset,
        context=context,
        policy_sha256=validated_policy.file_sha256,
        identity=identity,
    )
    if witness_after_claim != witness:
        raise AcceptanceError("The immutable witness changed around the claim", stage="witness")
    snapshot, parquet_sha256 = _snapshot_test_after_claim(
        _read_test_path_after_claim(),
        phase_root / "source-snapshot",
        forbidden_file_ids=(
            protected_file_ids
            | frozenset({_regular_file_object_id(claim.path, name="attempt ledger")})
        ),
    )
    try:
        verified = sroie.verify_sroie_parquet(snapshot, split="test")
        corpus = sroie.extract_sroie_corpus(snapshot, phase_root / "scoring-corpus", split="test")
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(str(exc), stage=exc.stage or "test-corpus") from exc
    if (
        verified != parquet_sha256
        or verified != sroie_policy.TEST_SHA256
        or corpus.source_row_count != sroie_policy.RAW_TEST_ROWS
        or len(corpus.documents) != sroie_policy.RAW_TEST_ROWS
        or corpus.excluded_row_indices
    ):
        raise AcceptanceError(
            "The complete pinned test corpus was not extracted", stage="test-corpus"
        )
    _require_disjoint_source_images(context.source_image_sha256s, corpus.source_image_sha256s)
    expected_identities = _expected_identities(corpus)
    if (
        _validate_duplicate_audit(
            corpus.duplicate_audit,
            split="test",
            expected_raw_rows=sroie_policy.RAW_TEST_ROWS,
            expected_identities=expected_identities,
        )
        != corpus.source_image_sha256s
    ):
        raise AcceptanceError(
            "The test duplicate audit source digests changed", stage="duplicate-audit"
        )
    determinism = _create_determinism_corpus(corpus, phase_root / "views", role="confirmatory")
    backends = (
        frozen.cpu_backend,
        frozen.directml_backend,
        benchmark_core.Backend("hybrid", frozen.directml_backend.python_executable),
    )
    runs = [
        _benchmark_backend(
            frozen,
            corpus,
            determinism,
            backend,
            phase_root / "results" / backend.requested_provider,
            args.timeout_seconds,
        )
        for backend in backends
    ]
    evaluations: dict[str, Any] = {}
    for run in runs:
        try:
            evaluations[run["requestedProvider"]] = sroie_policy.evaluate_confirmatory(
                validated_policy,
                run["metrics"],
                expected_identities=expected_identities,
            )
        except sroie_policy.PolicyError as exc:
            raise AcceptanceError(str(exc), stage="quality") from exc
    determinism_checks = {
        run["requestedProvider"]: _determinism_checks(run, determinism) for run in runs
    }
    critical_hashes = {run.get("criticalEvidenceSha256") for run in runs}
    metrics_hashes = {run.get("metricsSha256") for run in runs}
    per_document_hashes = {
        run.get("qualityRun", {}).get("perDocumentMetricsSha256") for run in runs
    }
    confidence = cord.confidence_parity(
        [run.get("_confidenceByCriticalRecord", {}) for run in runs]
    )
    integrity_checks = {
        "allBackendDeterminismPassed": all(
            all(values.values()) for values in determinism_checks.values()
        ),
        "allBackendProvenancePassed": all(run.get("provenancePassed") is True for run in runs),
        "candidateStable": _candidate_stable(args, identity),
        "confidenceParityPassed": confidence.get("passed") is True,
        "criticalEvidenceEqual": len(critical_hashes) == 1 and None not in critical_hashes,
        "documentCountsExact": all(run.get("qualityRows") == len(corpus.documents) for run in runs),
        "hybridMeaningfulLaneCoverage": _meaningful_hybrid_lane_coverage(runs[2]),
        "metricsEqual": len(metrics_hashes) == 1 and None not in metrics_hashes,
        "perDocumentMetricsEqual": (
            len(per_document_hashes) == 1 and None not in per_document_hashes
        ),
        "requestedProvidersExact": [run.get("requestedProvider") for run in runs]
        == ["cpu", "directml", "hybrid"],
        "resolvedProvidersExact": [run.get("resolvedProvider") for run in runs]
        == ["cpu", "directml", "hybrid-directml-cpu"],
        "runtimeHashesExact": (
            runs[0].get("runtimeSha256") == frozen.runtimes["cpu"]["executableSha256"]
            and all(
                run.get("runtimeSha256") == frozen.runtimes["directml"]["executableSha256"]
                for run in runs[1:]
            )
        ),
        "allRawSourceImageSha256SetsDisjoint": True,
        "workerHashesExact": all(run.get("workerSha256") == frozen.worker_sha256 for run in runs),
    }
    integrity_passed = all(integrity_checks.values())
    quality_passed = all(value.get("passed") is True for value in evaluations.values())
    evidence = {
        "artifacts": {
            "confirmatoryCorpusManifest": _safe_artifact(
                corpus.corpus_manifest, evidence_root=phase_root
            ),
            "confirmatoryDuplicateAudit": _safe_artifact(
                corpus.duplicate_audit, evidence_root=phase_root
            ),
            "confirmatoryInventory": _safe_artifact(corpus.inventory, evidence_root=phase_root),
            "confirmatoryWorkerManifest": _safe_artifact(
                corpus.worker_manifest, evidence_root=phase_root
            ),
            "verifiedTestSnapshot": _safe_artifact(snapshot, evidence_root=phase_root),
        },
        "backends": [_public_run(run) for run in runs],
        "crossBackendIntegrity": {
            "checks": integrity_checks,
            "confidenceParity": confidence,
        },
        "determinism": {
            "checksByBackend": determinism_checks,
            "repetitions": DETERMINISM_REPETITIONS,
        },
        "execution": {
            "backendOrder": ["cpu", "directml", "hybrid"],
            "oneShotAttemptLedger": claim.path.name,
            "threads": 0,
        },
        "generatedAtUtc": benchmark_core.utc_timestamp(),
        "phase": "confirmatory",
        "policy": {
            "evaluations": evaluations,
            "policyId": sroie_policy.POLICY_ID,
            "policySha256": validated_policy.file_sha256,
        },
        "testSnapshotSha256": parquet_sha256,
        "holdoutOverlapCheck": {
            "algorithm": "exact SHA-256 equality over decoded embedded image bytes",
            "calibrationRawImages": sroie_policy.RAW_TRAIN_ROWS,
            "confirmatoryRawImages": sroie_policy.RAW_TEST_ROWS,
            "perceptualSimilarityClaimed": False,
        },
        "thresholdOverridesPermitted": False,
        "witness": {
            "ghExecutableSha256": witness.gh_executable_sha256,
            "ghVersion": witness.gh_version,
            "githubImmutableRelease": witness.value["githubImmutableRelease"],
            "releaseVerificationSha256": witness.release_verification_sha256,
            "sha256": witness.file_sha256,
        },
    }
    report = _report(
        role="confirmatory",
        documents=len(corpus.documents),
        raw_rows=corpus.source_row_count,
        identity=identity,
        expected_identities=expected_identities,
        metrics=runs[0]["metrics"],
        evidence=evidence,
        integrity_passed=integrity_passed,
        acceptance_passed=integrity_passed and quality_passed,
    )
    _recheck_policy(args.policy, validated_policy)
    return report, claim


def _failure_report(args: argparse.Namespace, error: Exception) -> dict[str, Any]:
    role = args.phase
    raw_rows = sroie_policy.RAW_TRAIN_ROWS if role == "calibration" else sroie_policy.RAW_TEST_ROWS
    metrics: dict[str, Any] = {}
    return {
        "acceptancePassed": False,
        "documents": 0,
        "evaluationCompleted": False,
        "evaluationRole": role,
        "evidence": {
            "failure": _failure_details(error),
            "generatedAtUtc": benchmark_core.utc_timestamp(),
            "phase": role,
        },
        "expectedIdentitiesSha256": sroie_policy.sha256_canonical([]),
        "identity": {},
        "identitySha256": sroie_policy.sha256_canonical({}),
        "integrityPassed": False,
        "finalDisposition": "failed",
        "metrics": metrics,
        "metricsSha256": sroie_policy.sha256_canonical(metrics),
        "protocol": PROTOCOL,
        "rawRows": raw_rows,
        "runSucceeded": False,
        "schemaVersion": SCHEMA_VERSION,
    }


def run(args: argparse.Namespace) -> dict[str, Any]:
    _require_outer_runtime_isolation()
    if args.phase == "confirmatory" and CONFIRMATORY_ATTEMPT_LEDGER.exists():
        raise AcceptanceError(
            "A one-shot ledger already exists; automatic recovery is forbidden",
            stage="one-shot",
        )
    output, marker = _new_report_marker(args.output)
    claim: AttemptClaim | None = None
    try:
        frozen = verify_candidate(args)
        if args.phase == "calibration":
            report, policy = execute_calibration(args, frozen)
            staged = _stage_report(output, marker, report)
            if policy is not None:
                sroie_policy.publish_policy(args.policy_output, policy)
            _finalize_report(output, marker, staged)
            return report
        context = load_calibration_context(args.calibration_report, args.calibration_evidence_root)
        report, claim = execute_confirmatory(args, frozen, context)
        staged = _stage_report(output, marker, report)
        _prepare_attempt_report(claim, report, staged)
        _publish_prepared_report(claim, output, marker, staged)
        _complete_attempt(claim, output)
        _seal_report(claim, output, marker)
        return report
    except Exception as exc:
        active = claim or getattr(args, "_attempt_claim", None) or getattr(exc, "claim", None)
        if isinstance(active, AttemptClaim):
            try:
                _terminalize_failed_attempt(args, active, exc)
            except Exception as terminalization_error:
                raise terminalization_error from exc
            # A postclaim failure has consumed the one-shot attempt.  Keep the
            # incomplete marker and any staged/published bytes for quarantine
            # review; do not manufacture a sparse final report.
            raise
        staged = output.with_name(output.name + ".staged")
        staged.unlink(missing_ok=True)
        if marker.exists():
            failure = _failure_report(args, exc)
            failure_staged = _stage_report(output, marker, failure)
            _finalize_report(output, marker, failure_staged)
        raise


def _argument_path(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else _INVOCATION_CWD / path


def _held_out_path(value: str) -> str:
    # Lexical only: no resolve, stat, existence check, symlink check, or open.
    path = Path(value)
    return os.path.normpath(str(path if path.is_absolute() else _INVOCATION_CWD / path))


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="phase", required=True)

    def candidate(subparser: argparse.ArgumentParser) -> None:
        subparser.add_argument("--worker", type=_argument_path, required=True)
        subparser.add_argument("--model-pack", type=_argument_path, required=True)
        subparser.add_argument("--cpu-python", type=_argument_path, required=True)
        subparser.add_argument("--directml-python", type=_argument_path, required=True)
        subparser.add_argument("--work-directory", type=_argument_path, required=True)
        subparser.add_argument("--output", type=_argument_path, required=True)
        subparser.add_argument("--timeout-seconds", type=float, default=7200.0)

    calibration = subparsers.add_parser("calibration")
    candidate(calibration)
    calibration.add_argument("--train-parquet", type=_argument_path, required=True)
    calibration.add_argument("--policy-output", type=_argument_path, required=True)

    confirmatory = subparsers.add_parser("confirmatory")
    candidate(confirmatory)
    confirmatory.add_argument("--calibration-report", type=_argument_path, required=True)
    confirmatory.add_argument("--calibration-evidence-root", type=_argument_path, required=True)
    confirmatory.add_argument("--policy", type=_argument_path, required=True)
    confirmatory.add_argument("--gh-executable", type=_argument_path, required=True)
    confirmatory.add_argument("--release-tag", required=True)
    confirmatory.add_argument("--witness-asset", required=True)
    return parser.parse_args(argv)


def _detach_confirmatory_stdin() -> None:
    global _TEST_PATH_PIPE_DESCRIPTOR
    if _TEST_PATH_PIPE_DESCRIPTOR is not None:
        raise AcceptanceError("The held-out test path pipe is already detached", stage="isolation")
    try:
        standard_input = sys.stdin.fileno()
        retained = os.dup(standard_input)
        os.set_inheritable(retained, False)
        null_descriptor = os.open(os.devnull, os.O_RDONLY | getattr(os, "O_BINARY", 0))
        try:
            os.dup2(null_descriptor, standard_input, inheritable=False)
        finally:
            os.close(null_descriptor)
    except (OSError, ValueError) as exc:
        with suppress(UnboundLocalError, OSError):
            os.close(retained)
        raise AcceptanceError(
            "The held-out test path pipe cannot be isolated from preclaim children",
            stage="isolation",
        ) from exc
    _TEST_PATH_PIPE_DESCRIPTOR = retained


def main(argv: Sequence[str] | None = None) -> int:
    try:
        arguments = parse_arguments(argv)
        if arguments.phase == "confirmatory":
            _detach_confirmatory_stdin()
        report = run(arguments)
    except (
        AcceptanceError,
        benchmark_core.BenchmarkError,
        generic_policy.PolicyError,
        sroie_policy.PolicyError,
        OSError,
        ValueError,
    ) as exc:
        print(f"SROIE OCR acceptance failed: {exc}", file=sys.stderr)
        return 1
    print(sroie_policy.canonical_json(report))
    return (
        0
        if report.get("runSucceeded") is True
        and report.get("integrityPassed") is True
        and report.get("acceptancePassed") is True
        else 2
    )


if __name__ == "__main__":
    raise SystemExit(main())
