#!/usr/bin/env python3
"""Benchmark the offline OCR worker with synthetic, attribution-safe evidence.

The benchmark creates a deterministic clean image, a multi-page scanned PDF,
and a deliberately degraded image.  Clean exact-identifier recovery and
provenance are hard gates.  Stress-image quality is reported separately so it
cannot hide a regression in the forensic invariants.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import platform
import re
import shutil
import stat
import statistics
import subprocess
import sys
import tempfile
import time
from collections import Counter
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1
SUPPORTED_PROVIDERS = frozenset({"auto", "cpu", "cuda", "directml", "hybrid"})
RELEASE_MATRIX_PROVIDERS = frozenset({"cpu", "directml", "hybrid"})
RUNTIME_DISTRIBUTIONS = (
    "numpy",
    "onnxruntime",
    "onnxruntime-directml",
    "onnxruntime-gpu",
    "Pillow",
    "pyarrow",
    "pypdfium2",
    "rapidocr",
    "rapidocr-onnxruntime",
    "shapely",
)
RUNTIME_METADATA_SCHEMA_VERSION = 2
RUNTIME_ROOT_SCHEMA_VERSION = 1
OFFLINE_ENVIRONMENT = {
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
PYTHON_ENVIRONMENT_VARIABLES_CLEARED = (
    "PYTHONCASEOK",
    "PYTHONEXECUTABLE",
    "PYTHONHOME",
    "PYTHONINSPECT",
    "PYTHONPATH",
    "PYTHONSTARTUP",
    "PYTHONUSERBASE",
)
ORT_ENVIRONMENT_VARIABLES_CLEARED = (
    "CUDA_MODULE_LOADING",
    "CUDA_PATH",
    "CUDA_PATH_V12_0",
    "CUDNN_PATH",
    "KMP_AFFINITY",
    "KMP_BLOCKTIME",
    "MKL_NUM_THREADS",
    "OMP_NUM_THREADS",
    "ORT_DISABLE_ALL",
    "ORT_LOG_SEVERITY_LEVEL",
)
WINDOWS_DIRECTML_REQUIRED_DLLS = frozenset({"directml.dll", "d3d12.dll", "dxgi.dll"})
WINDOWS_DIRECTML_IDENTITY_DLLS = (
    "DirectML.dll",
    "d3d12.dll",
    "D3D12Core.dll",
    "dxgi.dll",
    "dxcore.dll",
)
_WINDOWS_REPARSE_POINT = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
MAX_BENCHMARK_JSONL_BYTES = 256 * 1024 * 1024
MAX_BENCHMARK_JSONL_RECORDS = 100_000
MAX_BENCHMARK_JSONL_LINE_BYTES = 16 * 1024 * 1024
REPORT_INCOMPLETE_MARKER = b"OCR benchmark run has not completed.\n"
# ONNX execution providers can differ by a few final decimal places while
# producing identical OCR text and geometry.  Keep the raw value as evidence,
# but require every provider pair to remain within this explicit absolute bound.
CONFIDENCE_ABSOLUTE_TOLERANCE = 1e-4
SYNTHETIC_LINES = (
    "analyst@example.com",
    "https://example.org/case?id=42",
    "192.0.2.42 CVE-2026-12345",
    r"C:\Evidence\Case-17\memory.raw",
)
SYNTHETIC_IDENTIFIERS = (
    "analyst@example.com",
    "https://example.org/case?id=42",
    "192.0.2.42",
    "CVE-2026-12345",
    r"C:\Evidence\Case-17\memory.raw",
)


class BenchmarkError(RuntimeError):
    """Raised when comparable OCR results cannot be produced."""

    def __init__(
        self,
        message: str,
        *,
        backend: str | None = None,
        stage: str | None = None,
    ) -> None:
        super().__init__(message)
        self.backend = backend
        self.stage = stage

    def with_context(self, *, backend: str, stage: str) -> BenchmarkError:
        resolved_backend = self.backend or backend
        resolved_stage = self.stage or stage
        if resolved_backend == self.backend and resolved_stage == self.stage:
            return self
        return BenchmarkError(str(self), backend=resolved_backend, stage=resolved_stage)

    def safe_message(self) -> str:
        fields = []
        if self.backend:
            fields.append(f"backend={self.backend}")
        if self.stage:
            fields.append(f"stage={self.stage}")
        context = f" ({', '.join(fields)})" if fields else ""
        return f"OCR benchmark failed{context}: {self} No partial result was accepted."


@dataclass(frozen=True)
class Backend:
    requested_provider: str
    python_executable: Path


@dataclass(frozen=True)
class CorpusCase:
    case_id: str
    classification: str
    path: Path
    pages: int
    expected_lines: tuple[str, ...]
    expected_identifiers: tuple[str, ...]
    expected_multiplier: int = 1


@dataclass(frozen=True)
class TokenDelta:
    token: str
    expected_count: int
    observed_count: int
    omitted_count: int
    added_count: int


@dataclass(frozen=True)
class LineAssignment:
    edit_errors: int
    expected_characters: int
    matched_lines: int
    omitted_lines: int
    added_lines: int
    exact_multiset: bool

    @property
    def character_error_rate(self) -> float:
        return self.edit_errors / max(self.expected_characters, 1)


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _atomic_replace_bytes(path: Path, value: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f"{path.name}.partial-{os.getpid()}-{time.time_ns()}")
    try:
        with temporary.open("xb") as handle:
            handle.write(value)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def begin_benchmark_report(output: Path) -> Path:
    marker = output.with_name(output.name + ".incomplete")
    _atomic_replace_bytes(marker, REPORT_INCOMPLETE_MARKER)
    return marker


def publish_benchmark_report(output: Path, marker: Path, report: dict[str, Any]) -> None:
    expected_marker = output.with_name(output.name + ".incomplete")
    if marker != expected_marker:
        raise BenchmarkError("The OCR benchmark completion marker path is invalid")
    try:
        marker_value = marker.read_bytes()
    except OSError as error:
        raise BenchmarkError("The OCR benchmark completion marker is missing") from error
    if marker.is_symlink() or marker_value != REPORT_INCOMPLETE_MARKER:
        raise BenchmarkError("The OCR benchmark completion marker is invalid")

    _atomic_replace_bytes(output, (canonical_json(report) + "\n").encode("utf-8"))
    try:
        marker.unlink()
    except OSError as error:
        output.unlink(missing_ok=True)
        raise BenchmarkError("The OCR benchmark completion marker was not removed") from error


def raw_output_pair_sha256(strings_path: Path, assessments_path: Path) -> str:
    digest = hashlib.sha256()
    for label, path in ((b"strings", strings_path), (b"assessments", assessments_path)):
        length = path.stat().st_size
        if length > MAX_BENCHMARK_JSONL_BYTES:
            raise BenchmarkError("The OCR worker JSONL exceeds the benchmark byte limit")
        digest.update(len(label).to_bytes(2, "big"))
        digest.update(label)
        digest.update(length.to_bytes(8, "big"))
        with path.open("rb") as handle:
            for chunk in iter(lambda: handle.read(4 * 1024 * 1024), b""):
                digest.update(chunk)
        if path.stat().st_size != length:
            raise BenchmarkError("The OCR worker JSONL changed while it was hashed")
    return digest.hexdigest()


def parse_backend(value: str) -> Backend:
    provider, separator, executable = value.partition("=")
    provider = provider.strip().lower()
    if not separator or provider not in SUPPORTED_PROVIDERS or not executable.strip():
        raise argparse.ArgumentTypeError(
            "backends must use provider=python-executable with a supported provider"
        )
    return Backend(provider, Path(executable.strip()).expanduser().resolve())


def safe_host_metadata() -> dict[str, str]:
    return {
        "system": platform.system(),
        "release": platform.release(),
        "version": platform.version(),
        "machine": platform.machine(),
        "benchmarkPython": platform.python_version(),
    }


def isolated_python_command(
    python_executable: Path,
    script: Path,
    arguments: Sequence[str] = (),
) -> list[str]:
    """Build the fixed isolated Python command used by every OCR worker."""

    executable = python_executable.expanduser().resolve()
    worker = script.expanduser().resolve()
    if not executable.is_file() or executable.is_symlink():
        raise BenchmarkError("The isolated Python executable is unavailable")
    if not worker.is_file() or worker.is_symlink():
        raise BenchmarkError("The isolated Python script is unavailable")
    return [str(executable), "-I", "-B", str(worker), *arguments]


def _windows_directory_from_api() -> Path:
    try:
        import ctypes

        buffer = ctypes.create_unicode_buffer(32768)
        length = ctypes.windll.kernel32.GetWindowsDirectoryW(buffer, len(buffer))
    except (AttributeError, OSError) as exc:
        raise BenchmarkError("The canonical Windows directory could not be queried") from exc
    if length <= 0 or length >= len(buffer):
        raise BenchmarkError("The canonical Windows directory query failed")
    return Path(buffer.value)


def _trusted_windows_directory() -> Path:
    root = Path(os.path.abspath(_windows_directory_from_api())).resolve()
    if not root.is_dir() or root.is_symlink():
        raise BenchmarkError("The canonical Windows directory is unavailable")
    for name in ("SystemRoot", "WINDIR"):
        inherited = os.environ.get(name)
        if inherited and os.path.normcase(str(Path(inherited).resolve())) != os.path.normcase(
            str(root)
        ):
            raise BenchmarkError(f"The inherited {name} disagrees with the Windows API")
    return root


def sanitized_runtime_environment(
    python_executable: Path,
    working_directory: Path,
) -> dict[str, str]:
    """Return the minimal, offline environment shared by probes and workers."""

    executable = python_executable.expanduser().resolve()
    work = working_directory.expanduser().resolve()
    if not work.is_dir() or work.is_symlink():
        raise BenchmarkError("The isolated process working directory is unavailable")

    environment: dict[str, str] = {}
    path_entries = [str(executable.parent)]
    if os.name == "nt":
        system_root = _trusted_windows_directory()
        system32 = system_root / "System32"
        if not system32.is_dir() or system32.is_symlink():
            raise BenchmarkError("The Windows system directories are unavailable")
        comspec = system32 / "cmd.exe"
        environment.update(
            {
                "COMSPEC": str(comspec),
                "SystemRoot": str(system_root),
                "WINDIR": str(system_root),
            }
        )
        path_entries.extend((str(system32), str(system_root)))
    else:
        path_entries.extend(part for part in os.defpath.split(os.pathsep) if part)

    environment.update(
        {
            "PATH": os.pathsep.join(dict.fromkeys(path_entries)),
            "TEMP": str(work),
            "TMP": str(work),
            "TMPDIR": str(work),
            **OFFLINE_ENVIRONMENT,
        }
    )
    # Documented for callers through runtime_metadata.environmentPolicy. The
    # environment starts empty, so Python/ORT injection variables not listed
    # above cannot leak in from the invoking shell.
    return environment


def runtime_environment_policy() -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "baseEnvironment": "minimal-allowlist-v1",
        "clearedVariableNames": sorted(
            (*PYTHON_ENVIRONMENT_VARIABLES_CLEARED, *ORT_ENVIRONMENT_VARIABLES_CLEARED)
        ),
        "dllSearchPolicy": (
            "fresh-cwd-runtime-and-system32-v1" if os.name == "nt" else "runtime-and-os-default-v1"
        ),
        "offlineVariableNames": sorted(OFFLINE_ENVIRONMENT),
        "pythonFlags": ["-I", "-B"],
        "workingDirectoryPolicy": "fresh-empty-temporary-cwd-v1",
    }


def _stat_fingerprint(value: os.stat_result) -> tuple[int, ...]:
    return (
        stat.S_IFMT(value.st_mode),
        0 if os.name == "nt" else stat.S_IMODE(value.st_mode),
        value.st_size,
        value.st_mtime_ns,
        int(getattr(value, "st_file_attributes", 0)),
    )


def _file_identity_fingerprint(value: os.stat_result) -> tuple[int, ...]:
    return (*_stat_fingerprint(value), value.st_dev, value.st_ino, value.st_nlink)


def _directory_fingerprint(value: os.stat_result) -> tuple[int, ...]:
    return (
        stat.S_IFMT(value.st_mode),
        0 if os.name == "nt" else stat.S_IMODE(value.st_mode),
        int(getattr(value, "st_file_attributes", 0)),
    )


def _is_link_or_reparse(value: os.stat_result) -> bool:
    return stat.S_ISLNK(value.st_mode) or bool(
        int(getattr(value, "st_file_attributes", 0)) & _WINDOWS_REPARSE_POINT
    )


def _scan_runtime_tree(
    root: Path,
) -> dict[str, tuple[str, tuple[int, ...], Path]]:
    try:
        root_stat = os.lstat(root)
    except OSError as exc:
        raise BenchmarkError("A runtime inventory root is unavailable") from exc
    if _is_link_or_reparse(root_stat) or not stat.S_ISDIR(root_stat.st_mode):
        raise BenchmarkError("A runtime inventory root is a link, reparse point, or non-directory")
    if root.parent == root:
        raise BenchmarkError("A filesystem root cannot be used as a runtime inventory root")

    result: dict[str, tuple[str, tuple[int, ...], Path]] = {
        "": ("directory", _directory_fingerprint(root_stat), root)
    }
    normalized_paths: set[str] = set()
    pending: list[tuple[str, Path]] = [("", root)]
    while pending:
        parent_relative, directory = pending.pop()
        try:
            with os.scandir(directory) as scanner:
                entries = sorted(scanner, key=lambda entry: (entry.name.casefold(), entry.name))
        except OSError as exc:
            raise BenchmarkError("A runtime inventory directory could not be enumerated") from exc
        child_directories: list[tuple[str, Path]] = []
        for entry in entries:
            relative = f"{parent_relative}/{entry.name}" if parent_relative else entry.name
            if not relative or relative.startswith("/") or "/../" in f"/{relative}/":
                raise BenchmarkError("A runtime inventory path is not canonical and relative")
            normalized = os.path.normcase(relative)
            if normalized in normalized_paths:
                raise BenchmarkError("A runtime inventory contains duplicate canonical paths")
            normalized_paths.add(normalized)
            try:
                entry_stat = entry.stat(follow_symlinks=False)
            except OSError as exc:
                raise BenchmarkError("A runtime inventory entry could not be inspected") from exc
            if entry.is_symlink() or _is_link_or_reparse(entry_stat):
                raise BenchmarkError("A runtime inventory contains a link or reparse point")
            path = Path(entry.path)
            if stat.S_ISDIR(entry_stat.st_mode):
                kind = "directory"
                fingerprint = _directory_fingerprint(entry_stat)
                child_directories.append((relative, path))
            elif stat.S_ISREG(entry_stat.st_mode):
                kind = "file"
                fingerprint = _stat_fingerprint(entry_stat)
            else:
                raise BenchmarkError("A runtime inventory contains a non-regular entry")
            result[relative] = (kind, fingerprint, path)
        pending.extend(reversed(child_directories))
    return result


def _hash_inventory_file(path: Path, expected: tuple[int, ...]) -> tuple[int, str]:
    try:
        before = os.lstat(path)
    except OSError as exc:
        raise BenchmarkError("A runtime file disappeared before hashing") from exc
    if (
        _is_link_or_reparse(before)
        or not stat.S_ISREG(before.st_mode)
        or _stat_fingerprint(before) != expected
    ):
        raise BenchmarkError("A runtime file changed before hashing")

    flags = os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0)
    digest = hashlib.sha256()
    descriptor: int | None = None
    try:
        descriptor = os.open(path, flags)
        opened_before = os.fstat(descriptor)
        before_identity = _file_identity_fingerprint(before)
        if _file_identity_fingerprint(opened_before) != before_identity:
            raise BenchmarkError("A runtime file changed while it was opened")
        while chunk := os.read(descriptor, 4 * 1024 * 1024):
            digest.update(chunk)
        opened_after = os.fstat(descriptor)
        if _file_identity_fingerprint(opened_after) != before_identity:
            raise BenchmarkError("A runtime file changed while it was hashed")
    except OSError as exc:
        raise BenchmarkError("A runtime file could not be hashed") from exc
    finally:
        if descriptor is not None:
            os.close(descriptor)

    try:
        after = os.lstat(path)
    except OSError as exc:
        raise BenchmarkError("A runtime file disappeared after hashing") from exc
    if _is_link_or_reparse(after) or _file_identity_fingerprint(after) != before_identity:
        raise BenchmarkError("A runtime file changed while it was hashed")
    return before.st_size, digest.hexdigest()


def _inventory_runtime_root(root: Path, *, root_id: str, roles: Sequence[str]) -> dict[str, Any]:
    absolute = Path(os.path.abspath(root))
    before = _scan_runtime_tree(absolute)
    files = []
    for relative, (kind, fingerprint, path) in sorted(before.items()):
        if kind != "file":
            continue
        length, sha256 = _hash_inventory_file(path, fingerprint)
        files.append({"path": relative, "bytes": length, "sha256": sha256})
    after = _scan_runtime_tree(absolute)
    before_identity = {
        relative: (kind, fingerprint) for relative, (kind, fingerprint, _) in before.items()
    }
    after_identity = {
        relative: (kind, fingerprint) for relative, (kind, fingerprint, _) in after.items()
    }
    if before_identity != after_identity:
        raise BenchmarkError("A runtime root changed while it was inventoried")
    if not files:
        raise BenchmarkError("A runtime inventory root contains no regular files")
    return {
        "rootId": root_id,
        "roles": sorted(set(roles)),
        "schemaVersion": RUNTIME_ROOT_SCHEMA_VERSION,
        "fileCount": len(files),
        "totalBytes": sum(entry["bytes"] for entry in files),
        "digestSha256": sha256_bytes(canonical_json(files).encode("utf-8")),
        "files": files,
    }


def _path_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
    except ValueError:
        return False
    return True


def _canonical_private_path(value: str, *, name: str) -> Path:
    if not isinstance(value, str) or not value:
        raise BenchmarkError(f"The runtime probe returned no {name}")
    path = Path(value)
    if not path.is_absolute():
        raise BenchmarkError(f"The runtime probe returned a non-absolute {name}")
    return Path(os.path.abspath(path))


def _runtime_coverage_roots(
    probe: Mapping[str, Any],
) -> tuple[list[tuple[Path, list[str]]], list[dict[str, Any]], Path]:
    prefix = _canonical_private_path(probe["prefix"], name="prefix")
    base_prefix = _canonical_private_path(probe["basePrefix"], name="base prefix")
    executable = _canonical_private_path(probe["executable"], name="executable")
    raw_sys_path = probe.get("sysPath")
    if not isinstance(raw_sys_path, list) or not raw_sys_path:
        raise BenchmarkError("The runtime probe returned no load paths")

    raw_roots: list[tuple[Path, str]] = [
        (prefix, "prefix"),
        (base_prefix, "basePrefix"),
        (executable.parent, "executable"),
    ]
    fixed_roots = tuple(path for path, _ in raw_roots)
    load_paths: list[tuple[int, Path, str]] = []
    for ordinal, raw_path in enumerate(raw_sys_path):
        path = _canonical_private_path(raw_path, name="load path")
        try:
            path_stat = os.lstat(path)
        except FileNotFoundError as exc:
            if not any(_path_within(path, root) for root in fixed_roots):
                raise BenchmarkError(
                    "A missing runtime load path is outside every inventoried root"
                ) from exc
            load_paths.append((ordinal, path, "missing"))
            continue
        except OSError as exc:
            raise BenchmarkError("A runtime load path is unavailable") from exc
        if _is_link_or_reparse(path_stat):
            raise BenchmarkError("A runtime load path is a link or reparse point")
        if stat.S_ISDIR(path_stat.st_mode):
            kind = "directory"
            root = path
        elif stat.S_ISREG(path_stat.st_mode):
            kind = "file"
            root = path.parent
        else:
            raise BenchmarkError("A runtime load path is not a regular file or directory")
        raw_roots.append((root, f"sysPath-{ordinal:03d}"))
        load_paths.append((ordinal, path, kind))

    unique_paths = sorted(
        {path for path, _ in raw_roots},
        key=lambda path: (len(path.parts), os.path.normcase(str(path))),
    )
    reduced_paths: list[Path] = []
    for candidate in unique_paths:
        if not any(_path_within(candidate, existing) for existing in reduced_paths):
            reduced_paths.append(candidate)
    roles_by_root = {root: [] for root in reduced_paths}
    for path, role in raw_roots:
        covering = [root for root in reduced_paths if _path_within(path, root)]
        if len(covering) != 1:
            raise BenchmarkError("A runtime load-bearing path has ambiguous root coverage")
        roles_by_root[covering[0]].append(role)

    ordered_roots = sorted(
        reduced_paths,
        key=lambda root: min(
            next(
                index
                for index, (_, candidate_role) in enumerate(raw_roots)
                if candidate_role == role
            )
            for role in roles_by_root[root]
        ),
    )
    roots = [(root, roles_by_root[root]) for root in ordered_roots]
    public_load_paths = []
    for ordinal, path, kind in load_paths:
        root_index = next(
            index for index, (root, _) in enumerate(roots) if _path_within(path, root)
        )
        root = roots[root_index][0]
        relative = path.relative_to(root).as_posix() or "."
        public_load_paths.append(
            {
                "ordinal": ordinal,
                "rootId": f"runtime-root-{root_index}",
                "path": relative,
                "kind": kind,
            }
        )
    return roots, public_load_paths, executable


def _runtime_probe_script() -> str:
    offline_names = repr(tuple(sorted(OFFLINE_ENVIRONMENT)))
    return (
        "import importlib.metadata as metadata\n"
        "import json\n"
        "import os\n"
        "import platform\n"
        "import re\n"
        "import sys\n"
        f"offline_names={offline_names}\n"
        "packages={}\n"
        "for distribution in metadata.distributions():\n"
        "    raw_name=distribution.metadata.get('Name')\n"
        "    if not isinstance(raw_name,str) or not raw_name.strip():\n"
        "        raise RuntimeError('distribution without canonical name')\n"
        "    name=re.sub(r'[-_.]+','-',raw_name).lower()\n"
        "    version=str(distribution.version)\n"
        "    if name in packages and packages[name] != version:\n"
        "        raise RuntimeError('conflicting distribution versions')\n"
        "    packages[name]=version\n"
        "try:\n"
        "    import onnxruntime\n"
        "    providers=sorted(set(str(value) for value in onnxruntime.get_available_providers()))\n"
        "    onnxruntime_importable=True\n"
        "except Exception:\n"
        "    providers=[]\n"
        "    onnxruntime_importable=False\n"
        "value={\n"
        "    'schemaVersion':1,\n"
        "    'pythonVersion':platform.python_version(),\n"
        "    'implementation':platform.python_implementation(),\n"
        "    'system':platform.system(),\n"
        "    'release':platform.release(),\n"
        "    'machine':platform.machine(),\n"
        "    'prefix':os.path.abspath(sys.prefix),\n"
        "    'basePrefix':os.path.abspath(sys.base_prefix),\n"
        "    'executable':os.path.abspath(sys.executable),\n"
        "    'sysPath':[os.path.abspath(value) for value in sys.path\n"
        "               if isinstance(value,str) and value],\n"
        "    'packages':dict(sorted(packages.items())),\n"
        "    'onnxruntimeImportable':onnxruntime_importable,\n"
        "    'availableExecutionProviders':providers,\n"
        "    'offlineEnvironment':{name:os.environ.get(name) for name in offline_names},\n"
        "}\n"
        "print(json.dumps(value,sort_keys=True,separators=(',',':')))\n"
    )


def _run_runtime_probe(backend: Backend, timeout_seconds: float) -> dict[str, Any]:
    executable = backend.python_executable.expanduser().resolve()
    if not executable.is_file() or executable.is_symlink():
        raise BenchmarkError(
            "The runtime identity executable is unavailable",
            backend=backend.requested_provider,
            stage="runtime-probe",
        )
    with tempfile.TemporaryDirectory(prefix="bstrings-runtime-probe-") as temporary:
        working_directory = Path(temporary)
        environment = sanitized_runtime_environment(executable, working_directory)
        try:
            completed = subprocess.run(
                [str(executable), "-I", "-B", "-c", _runtime_probe_script()],
                check=False,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                env=environment,
                cwd=working_directory,
                timeout=timeout_seconds,
            )
        except subprocess.TimeoutExpired as exc:
            raise BenchmarkError(
                "The runtime identity probe exceeded its timeout",
                backend=backend.requested_provider,
                stage="runtime-probe",
            ) from exc
    if completed.returncode != 0:
        raise BenchmarkError(
            "The runtime identity probe failed",
            backend=backend.requested_provider,
            stage="runtime-probe",
        )
    try:
        value = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise BenchmarkError(
            "The runtime identity probe returned invalid JSON",
            backend=backend.requested_provider,
            stage="runtime-probe",
        ) from exc
    expected_keys = {
        "availableExecutionProviders",
        "basePrefix",
        "executable",
        "implementation",
        "machine",
        "offlineEnvironment",
        "onnxruntimeImportable",
        "packages",
        "prefix",
        "pythonVersion",
        "release",
        "schemaVersion",
        "sysPath",
        "system",
    }
    if not isinstance(value, dict) or set(value) != expected_keys or value["schemaVersion"] != 1:
        raise BenchmarkError(
            "The runtime identity probe returned an invalid schema",
            backend=backend.requested_provider,
            stage="runtime-probe",
        )
    for key in ("implementation", "machine", "pythonVersion", "release", "system"):
        if not isinstance(value[key], str) or not value[key]:
            raise BenchmarkError("The runtime identity probe returned invalid platform data")
    packages = value["packages"]
    providers = value["availableExecutionProviders"]
    if (
        not isinstance(packages, dict)
        or any(
            not isinstance(key, str) or not isinstance(item, str) for key, item in packages.items()
        )
        or list(packages) != sorted(packages)
        or not isinstance(providers, list)
        or any(not isinstance(item, str) or not item for item in providers)
        or providers != sorted(set(providers))
        or type(value["onnxruntimeImportable"]) is not bool
        or value["offlineEnvironment"] != OFFLINE_ENVIRONMENT
    ):
        raise BenchmarkError(
            "The runtime identity probe returned invalid package, provider, or offline data",
            backend=backend.requested_provider,
            stage="runtime-probe",
        )
    probed_executable = _canonical_private_path(value["executable"], name="executable")
    if os.path.normcase(str(probed_executable)) != os.path.normcase(str(executable)):
        raise BenchmarkError(
            "The runtime identity probe changed the Python executable",
            backend=backend.requested_provider,
            stage="runtime-probe",
        )
    return value


def _provider_availability(backend: Backend, probe: Mapping[str, Any]) -> dict[str, Any]:
    available = list(probe["availableExecutionProviders"])
    requested = backend.requested_provider
    if requested == "benchmark":
        required: list[str] = []
    elif requested in {"auto", "cpu"}:
        required = ["CPUExecutionProvider"]
    elif requested == "directml":
        required = ["CPUExecutionProvider", "DmlExecutionProvider"]
    elif requested == "cuda":
        required = ["CPUExecutionProvider", "CUDAExecutionProvider"]
    elif requested == "hybrid":
        accelerated = next(
            (
                provider
                for provider in ("CUDAExecutionProvider", "DmlExecutionProvider")
                if provider in available
            ),
            None,
        )
        if accelerated is None:
            raise BenchmarkError(
                "The hybrid runtime advertises no supported accelerated provider",
                backend=requested,
                stage="runtime-provider",
            )
        required = ["CPUExecutionProvider", accelerated]
    else:
        raise BenchmarkError(
            "The runtime identity provider is unsupported", stage="runtime-provider"
        )
    satisfied = all(provider in available for provider in required)
    if required and (probe["onnxruntimeImportable"] is not True or not satisfied):
        raise BenchmarkError(
            "The runtime does not advertise every required execution provider",
            backend=requested,
            stage="runtime-provider",
        )
    return {
        "available": available,
        "required": sorted(required),
        "requirementsSatisfied": satisfied,
    }


def _windows_host_probe_script() -> str:
    dll_names = ",".join(f"'{name}'" for name in WINDOWS_DIRECTML_IDENTITY_DLLS)
    return rf"""
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom
function Get-TextSha256([string]$Value) {{
    $algorithm = [System.Security.Cryptography.SHA256]::Create()
    try {{
        $bytes = $algorithm.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
        return -join ($bytes | ForEach-Object {{ $_.ToString('x2') }})
    }} finally {{
        $algorithm.Dispose()
    }}
}}
$dlls = @()
foreach ($name in @({dll_names})) {{
    $path = Join-Path ([Environment]::SystemDirectory) $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {{ continue }}
    $before = Get-Item -LiteralPath $path -Force
    if (($before.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {{
        throw 'A DirectML system DLL is a reparse point'
    }}
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $after = Get-Item -LiteralPath $path -Force
    if ($before.Length -ne $after.Length -or
        $before.LastWriteTimeUtc.Ticks -ne $after.LastWriteTimeUtc.Ticks) {{
        throw 'A DirectML system DLL changed while it was hashed'
    }}
    $dlls += [ordered]@{{
        name = $after.Name.ToLowerInvariant()
        bytes = [int64]$after.Length
        sha256 = $hash
        fileVersion = [string]$after.VersionInfo.FileVersion
        productVersion = [string]$after.VersionInfo.ProductVersion
    }}
}}
$adapters = @()
foreach ($adapter in @(Get-CimInstance -ClassName Win32_VideoController)) {{
    $pnp = [string]$adapter.PNPDeviceID
    $vendorId = ''
    $deviceId = ''
    $subsystemId = ''
    if ($pnp -match 'VEN_([0-9A-Fa-f]{{4}})') {{ $vendorId = $Matches[1].ToLowerInvariant() }}
    if ($pnp -match 'DEV_([0-9A-Fa-f]{{4}})') {{ $deviceId = $Matches[1].ToLowerInvariant() }}
    if ($pnp -match 'SUBSYS_([0-9A-Fa-f]{{8}})') {{ $subsystemId = $Matches[1].ToLowerInvariant() }}
    $driverDateUtc = ''
    if ($null -ne $adapter.DriverDate) {{
        $driverDateUtc = ([datetime]$adapter.DriverDate).ToUniversalTime().ToString(
            'yyyy-MM-ddTHH:mm:ss.fffffffZ',
            [Globalization.CultureInfo]::InvariantCulture
        )
    }}
    $adapters += [ordered]@{{
        name = [string]$adapter.Name
        adapterCompatibility = [string]$adapter.AdapterCompatibility
        vendorId = $vendorId
        deviceId = $deviceId
        subsystemId = $subsystemId
        driverVersion = [string]$adapter.DriverVersion
        driverDateUtc = $driverDateUtc
        infFilename = [string]$adapter.InfFilename
        status = [string]$adapter.Status
        pnpDeviceIdSha256 = Get-TextSha256 $pnp
    }}
}}
[ordered]@{{
    schemaVersion = 1
    systemDlls = @($dlls | Sort-Object name)
    adapters = @($adapters | Sort-Object pnpDeviceIdSha256)
}} | ConvertTo-Json -Depth 6 -Compress
"""


def _run_windows_host_probe(
    python_executable: Path,
    working_directory: Path,
    timeout_seconds: float,
) -> dict[str, Any]:
    system_root = _trusted_windows_directory()
    powershell = system_root / "System32/WindowsPowerShell/v1.0/powershell.exe"
    if not powershell.is_file() or powershell.is_symlink():
        fallback = shutil.which("powershell.exe")
        if not fallback:
            raise BenchmarkError("Windows PowerShell is unavailable", stage="directml-host")
        powershell = Path(fallback).resolve()
    environment = sanitized_runtime_environment(python_executable, working_directory)
    try:
        completed = subprocess.run(
            [
                str(powershell),
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                _windows_host_probe_script(),
            ],
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=environment,
            cwd=working_directory,
            timeout=timeout_seconds,
        )
    except subprocess.TimeoutExpired as exc:
        raise BenchmarkError(
            "The DirectML host identity probe timed out", stage="directml-host"
        ) from exc
    if completed.returncode != 0:
        raise BenchmarkError("The DirectML host identity probe failed", stage="directml-host")
    try:
        value = json.loads(completed.stdout)
    except json.JSONDecodeError as exc:
        raise BenchmarkError("The DirectML host identity probe returned invalid JSON") from exc
    if not isinstance(value, dict):
        raise BenchmarkError("The DirectML host identity probe returned an invalid object")
    return value


def _normalize_windows_directml_probe(value: Mapping[str, Any]) -> dict[str, Any]:
    if set(value) != {"adapters", "schemaVersion", "systemDlls"} or value.get("schemaVersion") != 1:
        raise BenchmarkError("The DirectML host identity schema is invalid", stage="directml-host")
    raw_dlls = value.get("systemDlls")
    raw_adapters = value.get("adapters")
    if not isinstance(raw_dlls, list) or not isinstance(raw_adapters, list):
        raise BenchmarkError("The DirectML host identity collections are invalid")
    allowed_dll_names = {name.casefold() for name in WINDOWS_DIRECTML_IDENTITY_DLLS}
    dlls_by_name: dict[str, dict[str, Any]] = {}
    dll_hashes: set[str] = set()
    for raw in raw_dlls:
        if not isinstance(raw, dict) or set(raw) != {
            "bytes",
            "fileVersion",
            "name",
            "productVersion",
            "sha256",
        }:
            raise BenchmarkError("A DirectML system DLL identity is invalid")
        raw_name = raw["name"]
        name = raw_name.casefold() if isinstance(raw_name, str) else ""
        if (
            not name
            or name not in allowed_dll_names
            or isinstance(raw["bytes"], bool)
            or not isinstance(raw["bytes"], int)
            or raw["bytes"] <= 0
            or not isinstance(raw["sha256"], str)
            or re.fullmatch(r"[0-9a-f]{64}", raw["sha256"]) is None
            or not isinstance(raw["fileVersion"], str)
            or not isinstance(raw["productVersion"], str)
        ):
            raise BenchmarkError("A DirectML system DLL identity is invalid")
        if name in dlls_by_name or raw["sha256"] in dll_hashes:
            raise BenchmarkError("DirectML system DLL identities are duplicated")
        dlls_by_name[name] = {**raw, "name": name}
        dll_hashes.add(raw["sha256"])
    dlls = sorted(dlls_by_name.values(), key=lambda item: item["name"])
    if not WINDOWS_DIRECTML_REQUIRED_DLLS.issubset({item["name"] for item in dlls}):
        raise BenchmarkError("A required DirectML system DLL is unavailable")

    adapter_keys = {
        "adapterCompatibility",
        "deviceId",
        "driverDateUtc",
        "driverVersion",
        "infFilename",
        "name",
        "pnpDeviceIdSha256",
        "status",
        "subsystemId",
        "vendorId",
    }
    adapters_by_hash: dict[str, dict[str, Any]] = {}
    for raw in raw_adapters:
        if (
            not isinstance(raw, dict)
            or set(raw) != adapter_keys
            or any(not isinstance(item, str) for item in raw.values())
            or not raw["name"]
            or re.fullmatch(r"[0-9a-f]{64}", raw["pnpDeviceIdSha256"]) is None
            or (raw["vendorId"] and re.fullmatch(r"[0-9a-f]{4}", raw["vendorId"]) is None)
            or (raw["deviceId"] and re.fullmatch(r"[0-9a-f]{4}", raw["deviceId"]) is None)
            or (raw["subsystemId"] and re.fullmatch(r"[0-9a-f]{8}", raw["subsystemId"]) is None)
        ):
            raise BenchmarkError("A DirectML adapter identity is invalid")
        pnp_sha256 = raw["pnpDeviceIdSha256"]
        if pnp_sha256 in adapters_by_hash:
            raise BenchmarkError("DirectML adapter identities are duplicated")
        adapters_by_hash[pnp_sha256] = dict(raw)
    adapters = sorted(adapters_by_hash.values(), key=lambda item: item["pnpDeviceIdSha256"])
    if not any(adapter["status"].casefold() == "ok" for adapter in adapters):
        raise BenchmarkError("No healthy DirectML video adapter was identified")
    return {"schemaVersion": 1, "systemDlls": dlls, "adapters": adapters}


def _windows_directml_identity(
    backend: Backend,
    provider_availability: Mapping[str, Any],
    timeout_seconds: float,
) -> dict[str, Any]:
    required = "DmlExecutionProvider" in provider_availability["required"]
    if not required:
        return {"required": False, "status": "not-required", "systemDlls": [], "adapters": []}
    if platform.system() != "Windows":
        raise BenchmarkError(
            "DirectML host identity is unavailable on this platform",
            backend=backend.requested_provider,
            stage="directml-host",
        )
    with tempfile.TemporaryDirectory(prefix="bstrings-directml-host-") as temporary:
        work = Path(temporary)
        before = _normalize_windows_directml_probe(
            _run_windows_host_probe(backend.python_executable, work, timeout_seconds)
        )
        after = _normalize_windows_directml_probe(
            _run_windows_host_probe(backend.python_executable, work, timeout_seconds)
        )
    if canonical_json(before) != canonical_json(after):
        raise BenchmarkError("The DirectML host identity changed during its probe")
    return {
        "required": True,
        "status": "available",
        "systemDlls": before["systemDlls"],
        "adapters": before["adapters"],
    }


def runtime_metadata(backend: Backend, timeout_seconds: float = 30.0) -> dict[str, Any]:
    if not math.isfinite(timeout_seconds) or timeout_seconds <= 0:
        raise BenchmarkError("The runtime identity timeout must be positive and finite")
    probe_before = _run_runtime_probe(backend, timeout_seconds)
    roots, load_paths, executable = _runtime_coverage_roots(probe_before)
    inventories = [
        _inventory_runtime_root(
            root,
            root_id=f"runtime-root-{index}",
            roles=roles,
        )
        for index, (root, roles) in enumerate(roots)
    ]

    provider_availability = _provider_availability(backend, probe_before)
    windows_directml = _windows_directml_identity(
        backend, provider_availability, min(timeout_seconds, 30.0)
    )
    probe_after = _run_runtime_probe(backend, timeout_seconds)
    if canonical_json(probe_before) != canonical_json(probe_after):
        raise BenchmarkError(
            "The runtime probe identity changed while the runtime was inventoried",
            backend=backend.requested_provider,
            stage="runtime-mutation",
        )
    inventories_after = [
        _inventory_runtime_root(
            root,
            root_id=f"runtime-root-{index}",
            roles=roles,
        )
        for index, (root, roles) in enumerate(roots)
    ]
    if canonical_json(inventories) != canonical_json(inventories_after):
        raise BenchmarkError(
            "The runtime files changed while the runtime was inventoried",
            backend=backend.requested_provider,
            stage="runtime-mutation",
        )

    executable_root_index = next(
        (index for index, (root, _) in enumerate(roots) if _path_within(executable, root)),
        None,
    )
    if executable_root_index is None:
        raise BenchmarkError("The Python executable is outside the inventoried runtime roots")
    executable_relative = executable.relative_to(roots[executable_root_index][0]).as_posix()
    executable_entry = next(
        (
            entry
            for entry in inventories[executable_root_index]["files"]
            if entry["path"] == executable_relative
        ),
        None,
    )
    if executable_entry is None:
        raise BenchmarkError("The Python executable is absent from the runtime inventory")

    result = {
        "schemaVersion": RUNTIME_METADATA_SCHEMA_VERSION,
        "requestedProvider": backend.requested_provider,
        "pythonVersion": probe_before["pythonVersion"],
        "implementation": probe_before["implementation"],
        "system": probe_before["system"],
        "release": probe_before["release"],
        "machine": probe_before["machine"],
        "packages": probe_before["packages"],
        "offlineEnvironment": dict(OFFLINE_ENVIRONMENT),
        "environmentPolicy": runtime_environment_policy(),
        "providerAvailability": provider_availability,
        "executableName": executable.name,
        "executableSha256": executable_entry["sha256"],
        "executable": {
            "rootId": f"runtime-root-{executable_root_index}",
            "path": executable_relative,
            "bytes": executable_entry["bytes"],
            "sha256": executable_entry["sha256"],
        },
        "runtimeRoots": inventories,
        "loadPaths": load_paths,
        "windowsDirectml": windows_directml,
    }
    return {
        **result,
        "inventorySha256": sha256_bytes(canonical_json(result).encode("utf-8")),
    }


def validate_release_matrix(backends: Sequence[Backend]) -> None:
    requested = [backend.requested_provider for backend in backends]
    if len(backends) != 3 or frozenset(requested) != RELEASE_MATRIX_PROVIDERS:
        raise BenchmarkError(
            "The release matrix requires exactly CPU, DirectML, and hybrid backends",
            stage="release-matrix",
        )
    runtime_paths = {
        backend.requested_provider: os.path.normcase(str(backend.python_executable.resolve()))
        for backend in backends
    }
    if runtime_paths["cpu"] in {
        runtime_paths["directml"],
        runtime_paths["hybrid"],
    }:
        raise BenchmarkError(
            "The release matrix requires the CPU runtime to differ from accelerated runtimes",
            stage="release-matrix",
        )


def _load_model_identity(path: Path) -> tuple[str, str, str]:
    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BenchmarkError("The OCR model-pack manifest is not readable JSON") from exc
    if not isinstance(payload, dict) or payload.get("schemaVersion") != 1:
        raise BenchmarkError("The OCR model-pack manifest does not use schema version 1")
    model_id = payload.get("modelId")
    revision = payload.get("revision")
    if not isinstance(model_id, str) or not model_id.strip():
        raise BenchmarkError("The OCR model-pack manifest has no model identity")
    if not isinstance(revision, str) or not revision.strip():
        raise BenchmarkError("The OCR model-pack manifest has no immutable revision")
    return model_id, revision, sha256_file(path)


def _font(font_path: Path, size: int) -> Any:
    try:
        from PIL import ImageFont

        return ImageFont.truetype(str(font_path), size=size)
    except (ImportError, OSError) as exc:
        raise BenchmarkError("The pinned benchmark font could not be loaded") from exc


def _render_lines(font_path: Path, size: int, *, stress: bool) -> Any:
    try:
        from PIL import Image, ImageDraw, ImageFilter
    except ImportError as exc:
        raise BenchmarkError("Pillow is required to create the synthetic OCR corpus") from exc

    width, height = (2600, 640) if not stress else (1700, 440)
    background = 255 if not stress else 226
    foreground = 0 if not stress else 82
    image = Image.new("L", (width, height), color=background)
    drawing = ImageDraw.Draw(image)
    font = _font(font_path, size)
    top = 52 if not stress else 34
    spacing = 132 if not stress else 88
    for index, line in enumerate(SYNTHETIC_LINES):
        drawing.text((64, top + index * spacing), line, fill=foreground, font=font)

    if stress:
        # Deterministic degradation: lower contrast, gentle blur, and a small
        # rotation.  No random state or platform-dependent noise is involved.
        image = image.filter(ImageFilter.GaussianBlur(radius=0.65))
        image = image.rotate(
            0.7,
            resample=Image.Resampling.BICUBIC,
            expand=False,
            fillcolor=background,
        )
    return image.convert("RGB")


def build_corpus(root: Path, font_path: Path, pdf_pages: int) -> tuple[CorpusCase, ...]:
    if pdf_pages < 1 or pdf_pages > 1000:
        raise BenchmarkError("PDF page count must be between 1 and 1000")
    root.mkdir(parents=True, exist_ok=True)

    clean = _render_lines(font_path, 48, stress=False)
    clean_path = root / "clean-identifiers.png"
    clean.save(clean_path, format="PNG", optimize=False)

    stress = _render_lines(font_path, 29, stress=True)
    stress_path = root / "stress-identifiers.png"
    stress.save(stress_path, format="PNG", optimize=False)

    pdf_path = root / "clean-scanned-identifiers.pdf"
    pdf_images = [clean.copy() for _ in range(pdf_pages)]
    try:
        pdf_images[0].save(
            pdf_path,
            format="PDF",
            resolution=300.0,
            save_all=True,
            append_images=pdf_images[1:],
        )
    finally:
        for image in pdf_images:
            image.close()
        clean.close()
        stress.close()

    return (
        CorpusCase(
            "clean-image",
            "clean",
            clean_path.resolve(),
            1,
            SYNTHETIC_LINES,
            SYNTHETIC_IDENTIFIERS,
        ),
        CorpusCase(
            "clean-scanned-pdf",
            "clean",
            pdf_path.resolve(),
            pdf_pages,
            SYNTHETIC_LINES,
            SYNTHETIC_IDENTIFIERS,
            pdf_pages,
        ),
        CorpusCase(
            "stress-image",
            "stress",
            stress_path.resolve(),
            1,
            SYNTHETIC_LINES,
            SYNTHETIC_IDENTIFIERS,
        ),
    )


def corpus_hash(cases: Sequence[CorpusCase], font_path: Path) -> str:
    material = {
        "fontSha256": sha256_file(font_path),
        "cases": [
            {
                "caseId": case.case_id,
                "classification": case.classification,
                "pages": case.pages,
                "sha256": sha256_file(case.path),
                "expectedLines": case.expected_lines,
                "expectedIdentifiers": case.expected_identifiers,
                "expectedMultiplier": case.expected_multiplier,
            }
            for case in cases
        ],
    }
    return hashlib.sha256(canonical_json(material).encode("utf-8")).hexdigest()


def _read_jsonl(
    path: Path, *, backend: str | None = None, stage: str | None = None
) -> list[dict[str, Any]]:
    try:
        size = path.stat().st_size
        if size > MAX_BENCHMARK_JSONL_BYTES:
            raise BenchmarkError(
                "The OCR worker JSONL exceeds the benchmark byte limit",
                backend=backend,
                stage=stage,
            )
        records: list[dict[str, Any]] = []
        consumed = 0
        with path.open("rb") as handle:
            while True:
                raw_line = handle.readline(MAX_BENCHMARK_JSONL_LINE_BYTES + 2)
                if not raw_line:
                    break
                consumed += len(raw_line)
                if consumed > MAX_BENCHMARK_JSONL_BYTES:
                    raise BenchmarkError(
                        "The OCR worker JSONL exceeds the benchmark byte limit",
                        backend=backend,
                        stage=stage,
                    )
                if len(raw_line) > MAX_BENCHMARK_JSONL_LINE_BYTES + 1:
                    raise BenchmarkError(
                        "The OCR worker emitted an oversized JSONL record",
                        backend=backend,
                        stage=stage,
                    )
                if not raw_line.endswith(b"\n"):
                    raise BenchmarkError(
                        "The OCR worker emitted an unterminated JSONL record",
                        backend=backend,
                        stage=stage,
                    )
                payload = raw_line[:-1]
                if payload.endswith(b"\r") or not payload:
                    raise BenchmarkError(
                        "The OCR worker emitted a non-canonical JSONL record",
                        backend=backend,
                        stage=stage,
                    )
                value = json.loads(payload.decode("utf-8"))
                if not isinstance(value, dict):
                    raise BenchmarkError(
                        "The OCR worker emitted a non-object JSONL record",
                        backend=backend,
                        stage=stage,
                    )
                records.append(value)
                if len(records) > MAX_BENCHMARK_JSONL_RECORDS:
                    raise BenchmarkError(
                        "The OCR worker JSONL exceeds the benchmark record limit",
                        backend=backend,
                        stage=stage,
                    )
        if consumed != size:
            raise BenchmarkError(
                "The OCR worker JSONL changed while it was read",
                backend=backend,
                stage=stage,
            )
        return records
    except BenchmarkError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BenchmarkError(
            "The OCR worker did not produce valid JSONL",
            backend=backend,
            stage=stage,
        ) from exc


def _count_tokens(texts: Iterable[str], tokens: Iterable[str]) -> Counter[str]:
    material = "\n".join(texts)
    return Counter({token: material.count(token) for token in tokens})


def token_deltas(case: CorpusCase, records: Sequence[dict[str, Any]]) -> tuple[TokenDelta, ...]:
    texts = [
        record.get("text", "")
        for record in records
        if record.get("sourceFile") == str(case.path) and isinstance(record.get("text"), str)
    ]
    observed = _count_tokens(texts, case.expected_identifiers)
    return tuple(
        TokenDelta(
            token=token,
            expected_count=case.expected_multiplier,
            observed_count=observed[token],
            omitted_count=max(case.expected_multiplier - observed[token], 0),
            added_count=max(observed[token] - case.expected_multiplier, 0),
        )
        for token in case.expected_identifiers
    )


def _levenshtein(left: str, right: str) -> int:
    if len(left) < len(right):
        left, right = right, left
    previous = list(range(len(right) + 1))
    for left_index, left_character in enumerate(left, start=1):
        current = [left_index]
        for right_index, right_character in enumerate(right, start=1):
            current.append(
                min(
                    current[-1] + 1,
                    previous[right_index] + 1,
                    previous[right_index - 1] + (left_character != right_character),
                )
            )
        previous = current
    return previous[-1]


def _minimum_cost_assignment(costs: Sequence[Sequence[int]]) -> tuple[int, ...]:
    """Return a deterministic minimum-cost assignment for a square integer matrix."""

    size = len(costs)
    if size == 0:
        return ()
    if any(len(row) != size for row in costs):
        raise ValueError("The assignment cost matrix must be square")

    row_potential = [0] * (size + 1)
    column_potential = [0] * (size + 1)
    matched_row = [0] * (size + 1)
    predecessor = [0] * (size + 1)
    for row in range(1, size + 1):
        matched_row[0] = row
        column = 0
        minimum = [sys.maxsize] * (size + 1)
        used = [False] * (size + 1)
        while True:
            used[column] = True
            active_row = matched_row[column]
            delta = sys.maxsize
            next_column = 0
            for candidate in range(1, size + 1):
                if used[candidate]:
                    continue
                reduced = (
                    costs[active_row - 1][candidate - 1]
                    - row_potential[active_row]
                    - column_potential[candidate]
                )
                if reduced < minimum[candidate]:
                    minimum[candidate] = reduced
                    predecessor[candidate] = column
                if minimum[candidate] < delta:
                    delta = minimum[candidate]
                    next_column = candidate
            for candidate in range(size + 1):
                if used[candidate]:
                    row_potential[matched_row[candidate]] += delta
                    column_potential[candidate] -= delta
                else:
                    minimum[candidate] -= delta
            column = next_column
            if matched_row[column] == 0:
                break
        while True:
            previous = predecessor[column]
            matched_row[column] = matched_row[previous]
            column = previous
            if column == 0:
                break

    assignment = [-1] * size
    for column in range(1, size + 1):
        assignment[matched_row[column] - 1] = column - 1
    return tuple(assignment)


def expected_line_multiset(case: CorpusCase) -> Counter[str]:
    return Counter(line for _ in range(case.expected_multiplier) for line in case.expected_lines)


def _observed_lines(case: CorpusCase, records: Sequence[dict[str, Any]]) -> tuple[str, ...]:
    return tuple(
        record.get("text", "")
        for record in records
        if record.get("sourceFile") == str(case.path) and isinstance(record.get("text"), str)
    )


def assign_lines(case: CorpusCase, records: Sequence[dict[str, Any]]) -> LineAssignment:
    expected = tuple(expected_line_multiset(case).elements())
    observed = _observed_lines(case, records)
    expected_count = len(expected)
    observed_count = len(observed)
    size = expected_count + observed_count
    if size:
        costs = [
            [
                (
                    _levenshtein(expected[row], observed[column])
                    if row < expected_count and column < observed_count
                    else len(expected[row])
                    if row < expected_count
                    else len(observed[column])
                    if column < observed_count
                    else 0
                )
                for column in range(size)
            ]
            for row in range(size)
        ]
        assignment = _minimum_cost_assignment(costs)
        errors = sum(costs[row][column] for row, column in enumerate(assignment))
        matched = sum(
            row < expected_count and column < observed_count
            for row, column in enumerate(assignment)
        )
    else:
        errors = 0
        matched = 0
    return LineAssignment(
        edit_errors=errors,
        expected_characters=sum(len(line) for line in expected),
        matched_lines=matched,
        omitted_lines=expected_count - matched,
        added_lines=observed_count - matched,
        exact_multiset=Counter(observed) == expected_line_multiset(case),
    )


def line_character_error_rate(case: CorpusCase, records: Sequence[dict[str, Any]]) -> float:
    return assign_lines(case, records).character_error_rate


def _record_without_id(record: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in record.items() if key != "recordId"}


def _canonical_record(record: dict[str, Any]) -> dict[str, Any]:
    normalized = _record_without_id(record)
    origin = normalized.get("origin")
    if isinstance(origin, dict):
        normalized["origin"] = {key: value for key, value in origin.items() if key != "provider"}
    attributes = normalized.get("attributes")
    if isinstance(attributes, dict):
        ignored = frozenset(
            (
                "executionProvider",
                "provider",
                "requestedProvider",
                "requestedThreads",
                "resolvedThreadCounts",
                "resolvedWorkerCounts",
                "runtimeSha256",
            )
        )
        normalized["attributes"] = {
            key: value for key, value in attributes.items() if key not in ignored
        }
    return normalized


def _critical_canonical_record(record: dict[str, Any]) -> dict[str, Any]:
    """Return provider-neutral evidence used to align confidence observations."""
    normalized = _canonical_record(record)
    attributes = normalized.get("attributes")
    if isinstance(attributes, dict):
        normalized["attributes"] = {
            key: value for key, value in attributes.items() if key != "confidence"
        }
    return normalized


def _canonical_assessment(assessment: dict[str, Any]) -> dict[str, Any]:
    ignored = frozenset(
        (
            "provider",
            "requestedProvider",
            "requestedThreads",
            "resolvedThreadCounts",
            "resolvedWorkerCounts",
            "runtimeSha256",
        )
    )
    return {key: value for key, value in assessment.items() if key not in ignored}


def canonical_evidence_sha256(
    records: Sequence[dict[str, Any]], assessments: Sequence[dict[str, Any]]
) -> str:
    normalized = {
        "records": sorted((_canonical_record(record) for record in records), key=canonical_json),
        "assessments": sorted(
            (_canonical_assessment(assessment) for assessment in assessments),
            key=canonical_json,
        ),
    }
    return sha256_bytes(canonical_json(normalized).encode("utf-8"))


def critical_evidence_sha256(
    records: Sequence[dict[str, Any]], assessments: Sequence[dict[str, Any]]
) -> str:
    """Hash material evidence while excluding provider metadata and confidence."""
    normalized = {
        "records": sorted(
            (_critical_canonical_record(record) for record in records),
            key=canonical_json,
        ),
        "assessments": sorted(
            (_canonical_assessment(assessment) for assessment in assessments),
            key=canonical_json,
        ),
    }
    return sha256_bytes(canonical_json(normalized).encode("utf-8"))


def cross_backend_confidence_parity(
    backends: Sequence[dict[str, Any]],
    *,
    absolute_tolerance: float = CONFIDENCE_ABSOLUTE_TOLERANCE,
) -> dict[str, Any]:
    """Compare raw confidence values after exact critical-evidence alignment.

    The private ``_parityRecords`` field is removed before the public report is
    written.  Confidence remains unmodified in each backend's raw JSONL output;
    this function only converts it to ``float`` for bounded comparison.
    """
    errors: list[str] = []
    if isinstance(absolute_tolerance, bool) or not isinstance(absolute_tolerance, (int, float)):
        raise BenchmarkError("The confidence parity tolerance must be finite and non-negative")
    try:
        tolerance = float(absolute_tolerance)
    except (OverflowError, ValueError) as exc:
        raise BenchmarkError(
            "The confidence parity tolerance must be finite and non-negative"
        ) from exc
    if not math.isfinite(tolerance) or tolerance < 0.0:
        raise BenchmarkError("The confidence parity tolerance must be finite and non-negative")
    evaluated = len(backends) > 1
    observations: list[tuple[str, dict[str, float]]] = []

    for index, backend in enumerate(backends):
        provider_value = backend.get("requestedProvider")
        provider = provider_value if isinstance(provider_value, str) else f"backend-{index + 1}"
        records = backend.get("_parityRecords")
        if not isinstance(records, list):
            errors.append(f"{provider}: confidence parity records are unavailable")
            observations.append((provider, {}))
            continue
        aligned: dict[str, float] = {}
        for record_index, record in enumerate(records):
            if not isinstance(record, dict):
                errors.append(f"{provider}: record {record_index} is not an object")
                continue
            attributes = record.get("attributes")
            if not isinstance(attributes, dict):
                errors.append(f"{provider}: record {record_index} has no attributes")
                continue
            confidence = attributes.get("confidence")
            if isinstance(confidence, bool) or not isinstance(confidence, (int, float)):
                errors.append(f"{provider}: record {record_index} has no numeric confidence")
                continue
            try:
                value = float(confidence)
            except (OverflowError, ValueError):
                errors.append(
                    f"{provider}: record {record_index} confidence is not finite within 0..1"
                )
                continue
            if not math.isfinite(value) or not 0.0 <= value <= 1.0:
                errors.append(
                    f"{provider}: record {record_index} confidence is not finite within 0..1"
                )
                continue
            alignment_key = canonical_json(_critical_canonical_record(record))
            if alignment_key in aligned:
                errors.append(f"{provider}: duplicate critical evidence prevents alignment")
                continue
            aligned[alignment_key] = value
        observations.append((provider, aligned))

    aligned_count = 0
    absolute_differences: list[float] = []
    if observations:
        reference_provider, reference = observations[0]
        reference_keys = set(reference)
        aligned_count = len(reference)
        if evaluated and not reference_keys:
            errors.append("confidence parity has no records to compare")
        for provider, observed in observations[1:]:
            observed_keys = set(observed)
            if observed_keys != reference_keys:
                missing = len(reference_keys - observed_keys)
                unexpected = len(observed_keys - reference_keys)
                errors.append(
                    f"{provider}: critical evidence alignment differs from {reference_provider} "
                    f"(missing={missing}, unexpected={unexpected})"
                )
        if not errors and evaluated:
            for alignment_key in sorted(reference_keys):
                values = [observed[alignment_key] for _, observed in observations]
                for left in range(len(values)):
                    for right in range(left + 1, len(values)):
                        absolute_differences.append(abs(values[left] - values[right]))

    alignment_valid = not errors
    maximum = max(absolute_differences, default=0.0)
    mean = statistics.fmean(absolute_differences) if absolute_differences else 0.0
    if evaluated and not errors and maximum > tolerance:
        errors.append(
            "cross-backend confidence drift exceeded the absolute tolerance "
            f"({maximum:.12g} > {tolerance:.12g})"
        )
    return {
        "evaluated": evaluated,
        "absoluteTolerance": tolerance,
        "passed": evaluated and not errors,
        "alignedRecordCount": aligned_count if alignment_valid else 0,
        "pairwiseComparisons": len(absolute_differences),
        "maxAbsoluteDifference": maximum if absolute_differences else None,
        "meanAbsoluteDifference": mean if absolute_differences else None,
        "errors": list(dict.fromkeys(errors)),
    }


def validate_provenance(
    records: Sequence[dict[str, Any]],
    assessments: Sequence[dict[str, Any]],
    cases: Sequence[CorpusCase],
    model_pack_sha256: str,
    requested_threads: int,
) -> tuple[bool, tuple[str, ...], str, dict[str, int], dict[str, int]]:
    errors: list[str] = []
    assessment_by_source = {
        value.get("sourceFile"): value
        for value in assessments
        if isinstance(value, dict) and isinstance(value.get("sourceFile"), str)
    }
    resolved = {
        value.get("provider") for value in assessments if isinstance(value.get("provider"), str)
    }
    if len(resolved) != 1:
        errors.append("the run did not report exactly one resolved provider")
    resolved_provider = next(iter(resolved), "")
    expected_thread_keys = {
        "cpu": {"cpu"},
        "cuda": {"cuda"},
        "directml": {"directml"},
        "hybrid-cuda-cpu": {"cpu", "cuda"},
        "hybrid-directml-cpu": {"cpu", "directml"},
    }.get(resolved_provider, set())
    observed_thread_counts: list[dict[str, int]] = []
    observed_worker_counts: list[dict[str, int]] = []

    def validate_threads(value: dict[str, Any], description: str) -> None:
        if value.get("requestedThreads") != requested_threads:
            errors.append(f"{description} changed the requested thread count")
        raw_counts = value.get("resolvedThreadCounts")
        if not isinstance(raw_counts, dict) or set(raw_counts) != expected_thread_keys:
            errors.append(f"{description} reported invalid resolved thread-count keys")
            return
        if any(
            isinstance(count, bool) or not isinstance(count, int) or not 1 <= count <= 256
            for count in raw_counts.values()
        ):
            errors.append(f"{description} reported invalid resolved thread counts")
            return
        counts = {str(key): int(count) for key, count in raw_counts.items()}
        if requested_threads and any(count != requested_threads for count in counts.values()):
            errors.append(f"{description} changed an explicit thread override")
        if not requested_threads and any(
            count != 1 for key, count in counts.items() if key != "cpu"
        ):
            errors.append(f"{description} used non-conservative automatic GPU threads")
        observed_thread_counts.append(counts)

    def validate_workers(value: dict[str, Any], description: str) -> None:
        raw_counts = value.get("resolvedWorkerCounts")
        if not isinstance(raw_counts, dict) or set(raw_counts) != expected_thread_keys:
            errors.append(f"{description} reported invalid resolved worker-count keys")
            return
        if any(
            isinstance(count, bool) or not isinstance(count, int) or not 1 <= count <= 256
            for count in raw_counts.values()
        ):
            errors.append(f"{description} reported invalid resolved worker counts")
            return
        counts = {str(key): int(count) for key, count in raw_counts.items()}
        if any(count != 1 for key, count in counts.items() if key != "cpu"):
            errors.append(f"{description} used non-conservative GPU worker counts")
        observed_worker_counts.append(counts)

    expected_sources = {str(case.path) for case in cases}
    assessment_sources = [
        value.get("sourceFile")
        for value in assessments
        if isinstance(value, dict) and isinstance(value.get("sourceFile"), str)
    ]
    if len(assessment_sources) != len(set(assessment_sources)):
        errors.append("the run reported duplicate source assessments")
    if set(assessment_sources) != expected_sources:
        errors.append("the run assessment sources did not exactly match the corpus")

    for case in cases:
        assessment = assessment_by_source.get(str(case.path))
        if assessment is None:
            errors.append(f"{case.case_id}: missing assessment")
            continue
        if assessment.get("status") != "processed":
            errors.append(f"{case.case_id}: assessment was not processed")
        if assessment.get("sourceSha256") != sha256_file(case.path):
            errors.append(f"{case.case_id}: source hash mismatch")
        if assessment.get("modelPackSha256") != model_pack_sha256:
            errors.append(f"{case.case_id}: model-pack hash mismatch")
        if assessment.get("provider") != resolved_provider:
            errors.append(f"{case.case_id}: provider mismatch")
        validate_threads(assessment, f"{case.case_id} assessment")
        validate_workers(assessment, f"{case.case_id} assessment")

    for record in records:
        if record.get("sourceFile") not in expected_sources:
            errors.append("a string record references an unknown corpus source")
        origin = record.get("origin")
        attributes = record.get("attributes")
        if not isinstance(origin, dict) or not isinstance(attributes, dict):
            errors.append("a string record has no structured origin or attributes")
            continue
        if origin.get("provider") != resolved_provider:
            errors.append("a string record changed the resolved provider")
        if attributes.get("provider") != resolved_provider:
            errors.append("a string record's attributes changed the resolved provider")
        if attributes.get("modelPackSha256") != model_pack_sha256:
            errors.append("a string record changed the model-pack hash")
        validate_threads(attributes, "a string record")
        validate_workers(attributes, "a string record")
        source = origin.get("kind")
        if source == "ocr" and not attributes.get("renderSha256"):
            errors.append("an OCR record has no rendered-raster hash")
        record_id = record.get("recordId")
        expected_id = (
            "sha256:"
            + hashlib.sha256(canonical_json(_record_without_id(record)).encode("utf-8")).hexdigest()
        )
        if record_id != expected_id:
            errors.append("a string record identifier is not reproducible")
    thread_variants = {canonical_json(counts) for counts in observed_thread_counts}
    if len(thread_variants) != 1:
        errors.append("the run did not report one stable resolved thread-count map")
    resolved_thread_counts = observed_thread_counts[0] if len(thread_variants) == 1 else {}
    worker_variants = {canonical_json(counts) for counts in observed_worker_counts}
    if len(worker_variants) != 1:
        errors.append("the run did not report one stable resolved worker-count map")
    resolved_worker_counts = observed_worker_counts[0] if len(worker_variants) == 1 else {}
    return (
        not errors,
        tuple(dict.fromkeys(errors)),
        resolved_provider,
        resolved_thread_counts,
        resolved_worker_counts,
    )


def _case_metrics(case: CorpusCase, records: Sequence[dict[str, Any]]) -> dict[str, Any]:
    deltas = token_deltas(case, records)
    lines = assign_lines(case, records)
    expected = sum(delta.expected_count for delta in deltas)
    retained = sum(min(delta.observed_count, delta.expected_count) for delta in deltas)
    omitted = sum(delta.omitted_count for delta in deltas)
    added = sum(delta.added_count for delta in deltas)
    return {
        "caseId": case.case_id,
        "classification": case.classification,
        "pages": case.pages,
        "identifierRecall": retained / expected if expected else 1.0,
        "omittedIdentifiers": omitted,
        "addedIdentifiers": added,
        "expectedLineRecords": sum(expected_line_multiset(case).values()),
        "observedLineRecords": len(_observed_lines(case, records)),
        "exactLineMultisetPassed": lines.exact_multiset,
        "lineEditErrors": lines.edit_errors,
        "lineExpectedCharacters": lines.expected_characters,
        "lineCharacterErrorRate": lines.character_error_rate,
        "omittedLines": lines.omitted_lines,
        "addedLines": lines.added_lines,
        "identifierDeltas": [asdict(delta) for delta in deltas],
    }


def _run_once(
    *,
    backend: Backend,
    worker: Path,
    model_pack: Path,
    model_id: str,
    revision: str,
    model_pack_sha256: str,
    worker_sha256: str,
    engine_version: str,
    cases: Sequence[CorpusCase],
    output_root: Path,
    dpi: int,
    threads: int,
    timeout_seconds: float,
) -> dict[str, Any]:
    if sha256_file(worker) != worker_sha256:
        raise BenchmarkError(
            "The OCR worker changed before a backend run",
            backend=backend.requested_provider,
            stage="worker-identity",
        )
    output_root.mkdir(parents=True, exist_ok=True)
    inventory = output_root / "inventory.txt"
    input_manifest = output_root / "input-manifest.jsonl"
    strings = output_root / "strings.jsonl"
    assessments_path = output_root / "assessments.jsonl"
    inventory.write_text("".join(f"{case.path}\n" for case in cases), encoding="utf-8")
    input_manifest.write_text(
        "".join(
            canonical_json(
                {
                    "schemaVersion": 1,
                    "path": str(case.path),
                    "length": case.path.stat().st_size,
                    "sha256": sha256_file(case.path),
                }
            )
            + "\n"
            for case in cases
        ),
        encoding="utf-8",
    )

    arguments = [
        "--airgap",
        "--paths-from",
        str(inventory),
        "--input-manifest",
        str(input_manifest),
        "--output",
        str(strings),
        "--assessments-output",
        str(assessments_path),
        "--ocr-executable",
        str(backend.python_executable),
        "--ocr-engine",
        "rapidocr",
        "--ocr-engine-version",
        engine_version,
        "--ocr-model-path",
        str(model_pack),
        "--ocr-model-id",
        model_id,
        "--ocr-model-revision",
        revision,
        "--ocr-model-sha256",
        model_pack_sha256,
        "--ocr-mode",
        "force",
        "--provider",
        backend.requested_provider,
        "--dpi",
        str(dpi),
        "--threads",
        str(threads),
    ]
    command = isolated_python_command(backend.python_executable, worker, arguments)
    process_cwd = output_root / "process-cwd"
    try:
        process_cwd.mkdir()
    except FileExistsError as exc:
        raise BenchmarkError(
            "The OCR worker process directory already exists",
            backend=backend.requested_provider,
            stage="worker-isolation",
        ) from exc
    environment = sanitized_runtime_environment(backend.python_executable, process_cwd)
    started = time.perf_counter()
    try:
        completed = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=environment,
            cwd=process_cwd,
            timeout=timeout_seconds,
        )
    except subprocess.TimeoutExpired as exc:
        raise BenchmarkError(
            "The OCR worker exceeded the benchmark timeout",
            backend=backend.requested_provider,
            stage="worker",
        ) from exc
    elapsed = time.perf_counter() - started
    if completed.returncode != 0:
        raise BenchmarkError(
            "The OCR worker returned a non-zero status",
            backend=backend.requested_provider,
            stage="worker",
        )
    if sha256_file(worker) != worker_sha256:
        raise BenchmarkError(
            "The OCR worker changed during a backend run",
            backend=backend.requested_provider,
            stage="worker-identity",
        )

    records = _read_jsonl(strings, backend=backend.requested_provider, stage="strings-output")
    assessments = _read_jsonl(
        assessments_path,
        backend=backend.requested_provider,
        stage="assessments-output",
    )
    (
        provenance_passed,
        provenance_errors,
        resolved_provider,
        resolved_thread_counts,
        resolved_worker_counts,
    ) = validate_provenance(
        records,
        assessments,
        cases,
        model_pack_sha256,
        threads,
    )
    try:
        case_results = [_case_metrics(case, records) for case in cases]
    except BenchmarkError as exc:
        raise exc.with_context(backend=backend.requested_provider, stage="metrics") from exc
    except (TypeError, ValueError) as exc:
        raise BenchmarkError(
            "The OCR benchmark metrics could not be computed",
            backend=backend.requested_provider,
            stage="metrics",
        ) from exc
    clean = [result for result in case_results if result["classification"] == "clean"]
    stress = [result for result in case_results if result["classification"] == "stress"]
    total_pages = sum(case.pages for case in cases)
    clean_gate = provenance_passed and all(
        result["identifierRecall"] == 1.0
        and result["omittedIdentifiers"] == 0
        and result["addedIdentifiers"] == 0
        and result["exactLineMultisetPassed"]
        and result["lineCharacterErrorRate"] == 0.0
        and result["omittedLines"] == 0
        and result["addedLines"] == 0
        for result in clean
    )
    strings_sha256 = sha256_file(strings)
    assessments_sha256 = sha256_file(assessments_path)
    return {
        "requestedProvider": backend.requested_provider,
        "resolvedProvider": resolved_provider,
        "requestedThreads": threads,
        "resolvedThreadCounts": resolved_thread_counts,
        "resolvedWorkerCounts": resolved_worker_counts,
        "workerSha256": worker_sha256,
        "runtimeSha256": sha256_file(backend.python_executable),
        "elapsedSeconds": elapsed,
        "pagesPerSecond": total_pages / elapsed if elapsed else math.inf,
        "stringRecords": len(records),
        "rawOutputHashes": {
            "stringsSha256": strings_sha256,
            "assessmentsSha256": assessments_sha256,
            "pairSha256": raw_output_pair_sha256(strings, assessments_path),
        },
        "canonicalEvidenceSha256": canonical_evidence_sha256(records, assessments),
        "criticalEvidenceSha256": critical_evidence_sha256(records, assessments),
        "_parityRecords": records,
        "provenancePassed": provenance_passed,
        "provenanceErrors": provenance_errors,
        "cleanGatePassed": clean_gate,
        "cleanIdentifierRecall": statistics.fmean(result["identifierRecall"] for result in clean),
        "stressIdentifierRecall": statistics.fmean(result["identifierRecall"] for result in stress),
        "cases": case_results,
    }


def benchmark_backend(
    *,
    backend: Backend,
    repetitions: int,
    output_root: Path,
    runtime: dict[str, Any] | None = None,
    **kwargs: Any,
) -> dict[str, Any]:
    runs = [
        _run_once(
            backend=backend,
            output_root=output_root / f"run-{index + 1:02d}",
            **kwargs,
        )
        for index in range(repetitions)
    ]
    resolved = {run["resolvedProvider"] for run in runs}
    runtime_hashes = {run["runtimeSha256"] for run in runs}
    raw_output_hashes = {run["rawOutputHashes"]["pairSha256"] for run in runs}
    canonical_evidence_hashes = {run["canonicalEvidenceSha256"] for run in runs}
    critical_evidence_hashes = {run.get("criticalEvidenceSha256", "") for run in runs}
    parity_records = [run.pop("_parityRecords", None) for run in runs]
    resolved_thread_counts = {canonical_json(run["resolvedThreadCounts"]) for run in runs}
    resolved_worker_counts = {canonical_json(run["resolvedWorkerCounts"]) for run in runs}
    deterministic_metrics = {
        canonical_json(
            {
                "clean": run["cleanIdentifierRecall"],
                "stress": run["stressIdentifierRecall"],
                "cases": run["cases"],
            }
        )
        for run in runs
    }
    return {
        "requestedProvider": backend.requested_provider,
        "resolvedProvider": next(iter(resolved), ""),
        "requestedThreads": runs[0]["requestedThreads"],
        "resolvedThreadCounts": runs[0]["resolvedThreadCounts"],
        "stableResolvedThreadCounts": len(resolved_thread_counts) == 1,
        "resolvedWorkerCounts": runs[0]["resolvedWorkerCounts"],
        "stableResolvedWorkerCounts": len(resolved_worker_counts) == 1,
        "workerSha256": runs[0]["workerSha256"],
        "runtimeSha256": next(iter(runtime_hashes), ""),
        "runtime": runtime,
        "repetitions": repetitions,
        "coldElapsedSeconds": runs[0]["elapsedSeconds"],
        "medianElapsedSeconds": statistics.median(run["elapsedSeconds"] for run in runs),
        "medianPagesPerSecond": statistics.median(run["pagesPerSecond"] for run in runs),
        "cleanGatePassed": all(run["cleanGatePassed"] for run in runs),
        "provenancePassed": all(run["provenancePassed"] for run in runs),
        "deterministicMetrics": len(deterministic_metrics) == 1,
        "byteDeterminismEvaluated": repetitions > 1,
        "byteDeterministic": len(raw_output_hashes) == 1,
        "canonicalEvidenceDeterministic": len(canonical_evidence_hashes) == 1,
        "canonicalEvidenceSha256": (
            next(iter(canonical_evidence_hashes), "") if len(canonical_evidence_hashes) == 1 else ""
        ),
        "criticalEvidenceDeterministic": (
            "" not in critical_evidence_hashes and len(critical_evidence_hashes) == 1
        ),
        "criticalEvidenceSha256": (
            next(iter(critical_evidence_hashes), "")
            if "" not in critical_evidence_hashes and len(critical_evidence_hashes) == 1
            else ""
        ),
        "_parityRecords": parity_records[0],
        "stableResolvedProvider": len(resolved) == 1,
        "stableRuntime": len(runtime_hashes) == 1,
        "cleanIdentifierRecall": statistics.fmean(run["cleanIdentifierRecall"] for run in runs),
        "stressIdentifierRecall": statistics.fmean(run["stressIdentifierRecall"] for run in runs),
        "runs": runs,
    }


def validate_determinism_policy(
    repetitions: int, *, release_matrix: bool, require_pass: bool
) -> None:
    if (release_matrix or require_pass) and repetitions < 2:
        raise BenchmarkError(
            "Release or required-pass OCR benchmarks need at least two repetitions"
        )


def backend_result_passed(backend: dict[str, Any]) -> bool:
    return bool(
        backend["cleanGatePassed"]
        and backend["provenancePassed"]
        and backend["deterministicMetrics"]
        and backend["byteDeterminismEvaluated"]
        and backend["byteDeterministic"]
        and backend["canonicalEvidenceDeterministic"]
        and backend["stableResolvedProvider"]
        and backend["stableRuntime"]
        and backend["stableResolvedThreadCounts"]
        and backend["stableResolvedWorkerCounts"]
    )


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Benchmark offline OCR quality, evidence invariants, and throughput."
    )
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--model-pack", type=Path, required=True)
    parser.add_argument(
        "--backend",
        action="append",
        type=parse_backend,
        required=True,
        help="Repeat provider=python-executable for each runtime to compare.",
    )
    parser.add_argument("--font", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-directory", type=Path)
    parser.add_argument("--engine-version", default="3.9.2")
    parser.add_argument("--pdf-pages", type=int, default=10)
    parser.add_argument("--repetitions", type=int, default=2)
    parser.add_argument("--dpi", type=int, default=300)
    parser.add_argument(
        "--threads",
        type=int,
        default=0,
        help="OCR worker threads; 0 uses the bounded provider-aware production policy",
    )
    parser.add_argument("--timeout-seconds", type=float, default=3600.0)
    parser.add_argument(
        "--release-matrix",
        action="store_true",
        help=(
            "Require independent CPU and accelerated runtime paths and gate "
            "cross-backend canonical evidence parity."
        ),
    )
    parser.add_argument("--require-pass", action="store_true")
    return parser.parse_args(argv)


def run(args: argparse.Namespace) -> dict[str, Any]:
    output = args.output.expanduser().resolve()
    report_marker = begin_benchmark_report(output)
    validate_determinism_policy(
        args.repetitions,
        release_matrix=args.release_matrix,
        require_pass=args.require_pass,
    )
    worker = args.worker.expanduser().resolve()
    model_pack = args.model_pack.expanduser().resolve()
    font = args.font.expanduser().resolve()
    if not worker.is_file() or not model_pack.is_file() or not font.is_file():
        raise BenchmarkError("The worker, model pack, and font must be existing local files")
    if args.repetitions < 1 or args.repetitions > 20:
        raise BenchmarkError("Repetitions must be between 1 and 20")
    if args.dpi < 72 or args.dpi > 1200 or args.threads < 0 or args.threads > 256:
        raise BenchmarkError("DPI or thread count is outside the benchmark safety limits")
    if args.timeout_seconds <= 0 or not math.isfinite(args.timeout_seconds):
        raise BenchmarkError("The timeout must be a positive finite number")
    if len({backend.requested_provider for backend in args.backend}) != len(args.backend):
        raise BenchmarkError("Each requested provider may be benchmarked only once")
    for backend in args.backend:
        if not backend.python_executable.is_file():
            raise BenchmarkError("A benchmark backend executable is unavailable")
    if args.release_matrix:
        validate_release_matrix(args.backend)

    benchmark_script = Path(__file__).resolve()
    worker_sha256 = sha256_file(worker)
    benchmark_script_sha256 = sha256_file(benchmark_script)
    model_id, revision, model_pack_sha256 = _load_model_identity(model_pack)
    runtime_identities = {
        backend.requested_provider: runtime_metadata(
            backend, timeout_seconds=min(args.timeout_seconds, 30.0)
        )
        for backend in args.backend
    }
    owned_temporary: tempfile.TemporaryDirectory[str] | None = None
    if args.work_directory is None:
        owned_temporary = tempfile.TemporaryDirectory(prefix="bstrings-ocr-benchmark-")
        work_root = Path(owned_temporary.name)
    else:
        work_root = args.work_directory.expanduser().resolve()
        work_root.mkdir(parents=True, exist_ok=True)
    try:
        cases = build_corpus(work_root / "corpus", font, args.pdf_pages)
        digest = corpus_hash(cases, font)
        common = {
            "worker": worker,
            "model_pack": model_pack,
            "model_id": model_id,
            "revision": revision,
            "model_pack_sha256": model_pack_sha256,
            "worker_sha256": worker_sha256,
            "engine_version": args.engine_version,
            "cases": cases,
            "dpi": args.dpi,
            "threads": args.threads,
            "timeout_seconds": args.timeout_seconds,
        }
        backends = [
            benchmark_backend(
                backend=backend,
                repetitions=args.repetitions,
                output_root=work_root / "results" / backend.requested_provider,
                runtime=runtime_identities[backend.requested_provider],
                **common,
            )
            for backend in args.backend
        ]
        runtime_identities_after = {
            backend.requested_provider: runtime_metadata(
                backend, timeout_seconds=min(args.timeout_seconds, 30.0)
            )
            for backend in args.backend
        }
        if canonical_json(runtime_identities) != canonical_json(runtime_identities_after):
            raise BenchmarkError(
                "A complete OCR runtime inventory changed during the benchmark",
                stage="runtime-mutation",
            )
        if sha256_file(worker) != worker_sha256:
            raise BenchmarkError(
                "The OCR worker changed during the benchmark",
                stage="worker-identity",
            )
        if sha256_file(benchmark_script) != benchmark_script_sha256:
            raise BenchmarkError(
                "The OCR benchmark script changed during the benchmark",
                stage="benchmark-identity",
            )
        exact_canonical_hashes = {
            backend["canonicalEvidenceSha256"]
            for backend in backends
            if backend["canonicalEvidenceSha256"]
        }
        cross_backend_exact_canonical = (
            all(backend["canonicalEvidenceSha256"] for backend in backends)
            and len(exact_canonical_hashes) == 1
        )
        critical_hashes = {
            backend["criticalEvidenceSha256"]
            for backend in backends
            if backend["criticalEvidenceSha256"]
        }
        cross_backend_critical = (
            all(backend["criticalEvidenceSha256"] for backend in backends)
            and len(critical_hashes) == 1
        )
        confidence_parity = cross_backend_confidence_parity(backends)
        cross_backend_parity = cross_backend_critical and confidence_parity["passed"]
        for backend in backends:
            backend.pop("_parityRecords", None)
        resolved_providers = {backend["resolvedProvider"] for backend in backends}
        release_matrix_passed = (
            len(backends) == 3
            and len(resolved_providers) == 3
            and cross_backend_parity
            and all(backend["byteDeterminismEvaluated"] for backend in backends)
            if args.release_matrix
            else None
        )
        backend_passed = all(backend_result_passed(backend) for backend in backends)
        report = {
            "schemaVersion": SCHEMA_VERSION,
            "generatedAtUtc": utc_timestamp(),
            "host": safe_host_metadata(),
            "corpus": {
                "classification": "synthetic",
                "caseHash": digest,
                "fontSha256": sha256_file(font),
                "cases": [
                    {
                        "caseId": case.case_id,
                        "classification": case.classification,
                        "pages": case.pages,
                        "sha256": sha256_file(case.path),
                    }
                    for case in cases
                ],
            },
            "engine": {
                "name": "rapidocr",
                "version": args.engine_version,
                "modelId": model_id,
                "modelRevision": revision,
                "modelPackSha256": model_pack_sha256,
                "workerSha256": worker_sha256,
                "benchmarkScriptSha256": benchmark_script_sha256,
            },
            "settings": {
                "dpi": args.dpi,
                "threads": args.threads,
                "threadPolicy": "provider-aware-auto" if args.threads == 0 else "fixed",
                "pdfPages": args.pdf_pages,
                "repetitions": args.repetitions,
                "releaseMatrix": args.release_matrix,
            },
            "parity": {
                "crossBackendEvidence": cross_backend_parity,
                "crossBackendCanonicalEvidence": cross_backend_exact_canonical,
                "crossBackendExactCanonicalEvidence": cross_backend_exact_canonical,
                "crossBackendCriticalEvidence": cross_backend_critical,
                "canonicalEvidenceSha256": (
                    next(iter(exact_canonical_hashes), "") if cross_backend_exact_canonical else ""
                ),
                "criticalEvidenceSha256": (
                    next(iter(critical_hashes), "") if cross_backend_critical else ""
                ),
                "confidence": confidence_parity,
            },
            "releaseMatrix": {
                "enabled": args.release_matrix,
                "requiredProviders": sorted(RELEASE_MATRIX_PROVIDERS),
                "passed": release_matrix_passed,
            },
            "backends": backends,
            "passed": backend_passed
            and (cross_backend_parity if len(backends) > 1 else True)
            and (release_matrix_passed is not False),
        }
        publish_benchmark_report(output, report_marker, report)
        return report
    finally:
        if owned_temporary is not None:
            owned_temporary.cleanup()


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = parse_arguments(argv)
        report = run(args)
        print(
            canonical_json(
                {
                    "schemaVersion": SCHEMA_VERSION,
                    "status": "passed" if report["passed"] else "failed",
                    "outputSha256": sha256_file(args.output.expanduser().resolve()),
                }
            )
        )
        return 0 if report["passed"] or not args.require_pass else 1
    except BenchmarkError as exc:
        print(exc.safe_message(), file=sys.stderr)
        return 2
    except (OSError, UnicodeError, ValueError):
        print("OCR benchmark failed; no partial result was accepted.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
