#!/usr/bin/env python3
"""Run the frozen CORD-v2 OCR calibration and one-shot acceptance protocol."""

from __future__ import annotations

import sys

if __name__ == "__main__" and (
    sys.flags.isolated != 1
    or sys.flags.ignore_environment != 1
    or sys.flags.no_user_site != 1
    or not sys.dont_write_bytecode
):
    print(
        "OCR acceptance must be launched with the exact benchmark Python and -I -B",
        file=sys.stderr,
    )
    raise SystemExit(1)

import argparse
import atexit
import ctypes
import hashlib
import json
import math
import os
import stat
import tempfile
import types
from collections.abc import Iterable, Mapping, Sequence
from contextlib import suppress
from dataclasses import dataclass, replace
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import Any

_INVOCATION_CWD = Path.cwd().resolve()
_SOURCE_FILE = (
    Path(__file__) if Path(__file__).is_absolute() else _INVOCATION_CWD / Path(__file__)
).absolute()
_SOURCE_ROOT = _SOURCE_FILE.parent.resolve()
_LOADED_SOURCE_HASHES: dict[str, str] = {}
_CONTROLLED_CWD: Path | None = None
_CONTROLLED_CWD_OWNER: tempfile.TemporaryDirectory[str] | None = None
_DLL_DIRECTORY_HANDLES: list[Any] = []
_DLL_SEARCH_POLICY = "not-configured"
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
_OUTER_CLEARED_ENVIRONMENT = {
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
    "PYTHONCASEOK",
    "PYTHONEXECUTABLE",
    "PYTHONHASHSEED",
    "PYTHONHOME",
    "PYTHONINSPECT",
    "PYTHONPATH",
    "PYTHONSTARTUP",
    "PYTHONUSERBASE",
}


def _cleanup_outer_sandbox() -> None:
    if _CONTROLLED_CWD_OWNER is None:
        return
    with suppress(OSError):
        os.chdir(_SOURCE_ROOT)
    _CONTROLLED_CWD_OWNER.cleanup()


def _canonical_environment_names(names: Iterable[str]) -> set[str]:
    if os.name == "nt":
        return {name.upper() for name in names}
    return set(names)


def _sanitize_outer_environment() -> None:
    global _CONTROLLED_CWD, _CONTROLLED_CWD_OWNER, _DLL_SEARCH_POLICY
    kernel32: Any = None
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
        canonical_system_root = os.path.normcase(str(windows_directory))
        for name in ("SystemRoot", "WINDIR"):
            inherited_value = os.environ.get(name)
            if inherited_value and os.path.normcase(str(Path(inherited_value).resolve())) != (
                canonical_system_root
            ):
                raise RuntimeError(f"The inherited {name} does not identify Windows")
    temporary = tempfile.TemporaryDirectory(prefix="bstrings-ocr-acceptance-")
    controlled_cwd = Path(temporary.name).resolve()
    if os.name == "nt":
        retained = {
            "SystemRoot": str(windows_directory),
            "WINDIR": str(windows_directory),
        }
        system_directory = str((windows_directory / "System32").resolve())
        retained["PATH"] = system_directory
    else:
        retained = {}
        system_directory = None
        retained["PATH"] = "/usr/bin:/bin"
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
            os.chdir(_SOURCE_ROOT)
            temporary.cleanup()
            raise RuntimeError("The safe Windows DLL search policy could not be enabled")
        for directory in (Path(sys.executable).resolve().parent, Path(system_directory)):
            _DLL_DIRECTORY_HANDLES.append(os.add_dll_directory(str(directory)))
        _DLL_SEARCH_POLICY = "windows-default-dirs-plus-runtime-and-system32-v1"
    else:
        _DLL_SEARCH_POLICY = "empty-cwd-and-minimal-system-path-v1"
    _CONTROLLED_CWD = controlled_cwd
    _CONTROLLED_CWD_OWNER = temporary
    atexit.register(_cleanup_outer_sandbox)


def _trusted_source_module(name: str, file_name: str) -> Any:
    path = (_SOURCE_ROOT / file_name).absolute()
    if not path.exists() or path.is_symlink() or path.parent.resolve() != _SOURCE_ROOT:
        raise RuntimeError(f"The frozen {file_name} source is unsafe")
    with path.open("rb") as handle:
        identity = os.fstat(handle.fileno())
        attributes = getattr(identity, "st_file_attributes", 0)
        if not stat.S_ISREG(identity.st_mode) or attributes & getattr(
            stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0
        ):
            raise RuntimeError(f"The frozen {file_name} source is unsafe")
        source = handle.read()
    if len(source) != identity.st_size:
        raise RuntimeError(f"The frozen {file_name} source changed while it was read")
    code = compile(source, str(path), "exec", dont_inherit=True)
    module = types.ModuleType(name)
    module.__file__ = str(path)
    module.__package__ = ""
    module.__cached__ = None
    sys.modules[name] = module
    try:
        exec(code, module.__dict__)
    except Exception:
        sys.modules.pop(name, None)
        raise
    _LOADED_SOURCE_HASHES[name] = hashlib.sha256(source).hexdigest()
    return module


if __name__ == "__main__":
    _sanitize_outer_environment()
    benchmark_core = _trusted_source_module("benchmark_ocr", "benchmark_ocr.py")
    cord = _trusted_source_module("benchmark_ocr_cord", "benchmark_ocr_cord.py")
    selection_builder = _trusted_source_module(
        "generate_cord_selection", "generate_cord_selection.py"
    )
    acceptance_policy = _trusted_source_module("ocr_acceptance_policy", "ocr_acceptance_policy.py")
else:
    import benchmark_ocr as benchmark_core
    import benchmark_ocr_cord as cord
    import generate_cord_selection as selection_builder
    import ocr_acceptance_policy as acceptance_policy

SCHEMA_VERSION = 2
SCORING_CORPUS_MANIFEST_SCHEMA_VERSION = 2
PROTOCOL = "bstrings-cord-v2-train-ocr-calibration-confirmatory-v3"
DETERMINISM_DOCUMENTS = 10
DETERMINISM_REPETITIONS = 2
CONFIRMATORY_ATTEMPT_LEDGER = (
    # This path names the dataset-level one-shot namespace and deliberately
    # remains stable across acceptance-protocol revisions.
    Path(__file__).resolve().with_name("cord-v2-train-confirmatory-attempt-v1.json")
)
CONFIRMATORY_RETIREMENT_MARKER = (
    Path(__file__).resolve().with_name("cord-v2-train-confirmatory-retired-v1.json")
)
MAX_REPORT_BYTES = 64 * 1024 * 1024
MAX_RUNTIME_INVENTORY_BYTES = 512 * 1024 * 1024
ROLE_NAMES = ("calibration", "confirmatory")
NO_THRESHOLD_OVERRIDES: dict[str, float | None] = {
    "minimumDetectionHmean": None,
    "minimumEndToEndHmean": None,
    "minimumWordAccuracy": None,
    "maximumPageCer": None,
    "maximumPageWer": None,
}
MICRO_METRIC_PATHS = {
    "localizationCoverageHmean": ("detection", "hmean"),
    "exactEndToEndRowHmean": ("endToEndExact", "hmean"),
    "exactTokenF1": ("tokenF1",),
    "characterErrorRate": ("pageCer",),
    "wordErrorRate": ("pageWer",),
}


class AcceptanceError(RuntimeError):
    """The acceptance orchestration contract could not be completed."""

    def __init__(
        self,
        message: str,
        *,
        stage: str,
        backend: str | None = None,
        diagnostic: Mapping[str, Any] | None = None,
    ) -> None:
        super().__init__(message)
        self.stage = stage
        self.backend = backend
        self.diagnostic = dict(diagnostic) if diagnostic is not None else None


@dataclass(frozen=True)
class FrozenInputs:
    shards: tuple[Path, ...]
    selections: dict[str, acceptance_policy.ValidatedRoleSelection]
    selection_manifest: Path
    worker: Path
    model_pack: Path
    cpu_backend: benchmark_core.Backend
    directml_backend: benchmark_core.Backend
    runtimes: dict[str, dict[str, Any]]
    model_id: str
    model_revision: str
    model_pack_sha256: str
    worker_sha256: str


@dataclass(frozen=True)
class PreparedImage:
    row_index: int
    relative_path: str
    path: Path
    length: int
    sha256: str


@dataclass(frozen=True)
class PreparedRoleInputs:
    images: tuple[PreparedImage, ...]
    input_manifest: Path
    worker_manifest: Path
    inventory: Path
    input_manifest_sha256: str
    worker_manifest_sha256: str
    selection_sha256: str


@dataclass(frozen=True)
class CalibrationContext:
    report: dict[str, Any]
    report_sha256: str
    metrics: Mapping[str, Any]
    identity: Mapping[str, Any]
    calibration_corpus_manifest: Path
    calibration_worker_manifest: Path
    calibration_inventory: Path
    confirmatory_input_manifest: Path
    confirmatory_worker_manifest: Path
    confirmatory_inventory: Path
    calibration_corpus_manifest_sha256: str
    calibration_worker_manifest_sha256: str
    confirmatory_input_manifest_sha256: str
    confirmatory_worker_manifest_sha256: str
    runtime_inventory_paths: dict[str, Path]
    runtime_inventory_sha256: dict[str, str]


@dataclass(frozen=True)
class AttemptClaim:
    path: Path
    value: dict[str, Any]
    sha256: str


def _require_outer_runtime_isolation() -> None:
    source_root = os.path.normcase(str(_SOURCE_ROOT))
    normalized_paths = [os.path.normcase(str(Path(path).resolve())) for path in sys.path]
    allowed_environment = {
        *(_OUTER_OFFLINE_ENVIRONMENT),
        "PATH",
        "TEMP",
        "TMP",
        *(name for name in ("SystemRoot", "WINDIR") if name in os.environ),
    }
    if (
        sys.flags.isolated != 1
        or sys.flags.ignore_environment != 1
        or sys.flags.no_user_site != 1
        or not sys.dont_write_bytecode
        or not normalized_paths
        or source_root in normalized_paths
        or any(not path for path in sys.path)
        or any(name in os.environ for name in _OUTER_CLEARED_ENVIRONMENT)
        or any(
            os.environ.get(name) != expected
            for name, expected in _OUTER_OFFLINE_ENVIRONMENT.items()
        )
        or _canonical_environment_names(os.environ)
        != _canonical_environment_names(allowed_environment)
        or _CONTROLLED_CWD is None
        or Path.cwd().resolve() != _CONTROLLED_CWD
        or not _CONTROLLED_CWD.is_dir()
        or _CONTROLLED_CWD.is_symlink()
        or _DLL_SEARCH_POLICY == "not-configured"
        or __name__ == "__main__"
        and set(_LOADED_SOURCE_HASHES)
        != {
            "benchmark_ocr",
            "benchmark_ocr_cord",
            "generate_cord_selection",
            "ocr_acceptance_policy",
        }
    ):
        raise AcceptanceError(
            "The acceptance wrapper requires an isolated -I -B benchmark interpreter",
            stage="runtime-isolation",
        )


def _outer_isolation_evidence() -> dict[str, Any]:
    return {
        "dllSearchPolicy": _DLL_SEARCH_POLICY,
        "dontWriteBytecode": bool(sys.dont_write_bytecode),
        "effectiveEnvironmentNames": sorted(os.environ),
        "environmentPolicy": "minimal-allowlist-v1",
        "ignoreEnvironment": sys.flags.ignore_environment == 1,
        "isolatedInterpreter": sys.flags.isolated == 1,
        "noUserSite": sys.flags.no_user_site == 1,
        "workingDirectoryPolicy": "fresh-empty-temporary-directory-v1",
    }


def _canonical_bytes(value: Mapping[str, Any]) -> bytes:
    return (acceptance_policy.canonical_json(value) + "\n").encode("utf-8")


def _sha256_bytes(value: bytes) -> str:
    return benchmark_core.sha256_bytes(value)


def _strict_json(path: Path, *, maximum_bytes: int, name: str) -> tuple[dict[str, Any], str]:
    resolved = path.expanduser().resolve()
    if (
        not resolved.is_file()
        or resolved.is_symlink()
        or resolved.stat().st_size <= 0
        or resolved.stat().st_size > maximum_bytes
    ):
        raise AcceptanceError(f"The {name} is unavailable or has an invalid size", stage="input")
    raw = resolved.read_bytes()
    try:
        value = json.loads(
            raw.decode("utf-8"),
            object_pairs_hook=acceptance_policy._reject_duplicate_pairs,
            parse_constant=acceptance_policy._reject_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AcceptanceError(f"The {name} is not strict UTF-8 JSON", stage="input") from exc
    if not isinstance(value, dict) or raw != _canonical_bytes(value):
        raise AcceptanceError(f"The {name} is not a canonical JSON object", stage="input")
    return value, _sha256_bytes(raw)


def _atomic_create(path: Path, value: bytes) -> None:
    output = path.expanduser().resolve()
    marker = output.with_name(output.name + ".incomplete")
    if output.exists() or marker.exists():
        raise AcceptanceError(f"The output already exists: {output.name}", stage="atomic-output")
    output.parent.mkdir(parents=True, exist_ok=True)
    try:
        with marker.open("xb") as handle:
            handle.write(value)
            handle.flush()
            os.fsync(handle.fileno())
        if output.exists():
            raise AcceptanceError(
                f"The output appeared during publication: {output.name}",
                stage="atomic-output",
            )
        os.replace(marker, output)
    except Exception:
        marker.unlink(missing_ok=True)
        raise


def _atomic_replace(path: Path, value: bytes) -> None:
    output = path.expanduser().resolve()
    temporary = output.with_name(f"{output.name}.partial-{os.getpid()}")
    try:
        with temporary.open("xb") as handle:
            handle.write(value)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)


def _new_report_marker(output: Path) -> tuple[Path, Path]:
    resolved = output.expanduser().resolve()
    marker = resolved.with_name(resolved.name + ".incomplete")
    if resolved.exists() or marker.exists():
        raise AcceptanceError("The report output or marker already exists", stage="arguments")
    return resolved, benchmark_core.begin_benchmark_report(resolved)


def _publish_report(output: Path, marker: Path, report: dict[str, Any]) -> str:
    raw = _canonical_bytes(report)
    if len(raw) > MAX_REPORT_BYTES:
        raise AcceptanceError("The acceptance report is too large", stage="report")
    benchmark_core.publish_benchmark_report(output, marker, report)
    if output.read_bytes() != raw:
        output.unlink(missing_ok=True)
        raise AcceptanceError("The published report bytes changed", stage="report")
    return _sha256_bytes(raw)


def _stage_final_report(output: Path, marker: Path, report: Mapping[str, Any]) -> tuple[Path, str]:
    staged = output.with_name(output.name + ".staged")
    expected_marker = output.with_name(output.name + ".incomplete")
    if (
        marker != expected_marker
        or not marker.is_file()
        or marker.is_symlink()
        or marker.read_bytes() != benchmark_core.REPORT_INCOMPLETE_MARKER
        or staged.exists()
        or output.exists()
    ):
        raise AcceptanceError("The report staging state is invalid", stage="report-commit")
    raw = _canonical_bytes(report)
    if len(raw) > MAX_REPORT_BYTES:
        raise AcceptanceError("The acceptance report is too large", stage="report-commit")
    _atomic_create(staged, raw)
    return staged, _sha256_bytes(raw)


def _finalize_staged_report(output: Path, marker: Path, staged: Path) -> None:
    if (
        output.exists()
        or not staged.is_file()
        or staged.is_symlink()
        or not marker.is_file()
        or marker.is_symlink()
        or marker.read_bytes() != benchmark_core.REPORT_INCOMPLETE_MARKER
    ):
        raise AcceptanceError("The staged report cannot be finalized", stage="report-commit")
    try:
        os.replace(staged, output)
        marker.unlink()
    except Exception as exc:
        output.unlink(missing_ok=True)
        raise AcceptanceError(
            "The staged report could not be atomically finalized", stage="report-commit"
        ) from exc


def verify_train_shards(paths: Sequence[Path]) -> tuple[Path, ...]:
    expected = {
        file_name: (length, sha256) for file_name, length, sha256 in acceptance_policy.TRAIN_SHARDS
    }
    if len(paths) != len(expected):
        raise AcceptanceError(
            "Exactly four pinned train shards are required", stage="corpus-identity"
        )
    supplied: dict[str, Path] = {}
    for raw_path in paths:
        path = raw_path.expanduser().resolve()
        if path.name in supplied or path.name not in expected:
            raise AcceptanceError(
                "The pinned train shard filenames changed", stage="corpus-identity"
            )
        expected_length, expected_sha256 = expected[path.name]
        if (
            not path.is_file()
            or path.is_symlink()
            or path.stat().st_size != expected_length
            or benchmark_core.sha256_file(path) != expected_sha256
        ):
            raise AcceptanceError(
                f"The pinned train shard changed: {path.name}", stage="corpus-identity"
            )
        supplied[path.name] = path
    return tuple(supplied[file_name] for file_name, _, _ in acceptance_policy.TRAIN_SHARDS)


def verify_frozen_inputs(args: argparse.Namespace) -> FrozenInputs:
    try:
        shards = verify_train_shards(args.parquet)
        selections = acceptance_policy.load_role_selections(args.selection_manifest)
    except (OSError, acceptance_policy.PolicyError) as exc:
        raise AcceptanceError(str(exc), stage="corpus-identity") from exc

    selection_manifest = args.selection_manifest.expanduser().resolve()
    worker = args.worker.expanduser().resolve()
    model_pack = args.model_pack.expanduser().resolve()
    cpu_python = args.cpu_python.expanduser().resolve()
    directml_python = args.directml_python.expanduser().resolve()
    for path, name in (
        (worker, "OCR worker"),
        (model_pack, "model-pack manifest"),
        (cpu_python, "CPU Python runtime"),
        (directml_python, "DirectML Python runtime"),
    ):
        if not path.is_file() or path.is_symlink():
            raise AcceptanceError(f"The {name} is unavailable", stage="arguments")
    if os.path.normcase(str(cpu_python)) == os.path.normcase(str(directml_python)):
        raise AcceptanceError(
            "The CPU and accelerated runtimes must be different files", stage="arguments"
        )
    if not math.isfinite(args.timeout_seconds) or args.timeout_seconds <= 0:
        raise AcceptanceError("The timeout must be positive and finite", stage="arguments")

    cpu_backend = benchmark_core.Backend("cpu", cpu_python)
    directml_backend = benchmark_core.Backend("directml", directml_python)
    benchmark_backend = benchmark_core.Backend("benchmark", Path(sys.executable).resolve())
    try:
        benchmark_core.validate_release_matrix(
            (
                cpu_backend,
                directml_backend,
                benchmark_core.Backend("hybrid", directml_python),
            )
        )
        runtimes = {
            "benchmark": benchmark_core.runtime_metadata(
                benchmark_backend, timeout_seconds=min(args.timeout_seconds, 30.0)
            ),
            "cpu": benchmark_core.runtime_metadata(
                cpu_backend, timeout_seconds=min(args.timeout_seconds, 30.0)
            ),
            "directml": benchmark_core.runtime_metadata(
                directml_backend, timeout_seconds=min(args.timeout_seconds, 30.0)
            ),
        }
        model_id, model_revision, model_pack_sha256 = benchmark_core._load_model_identity(
            model_pack
        )
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(
            str(exc), stage=exc.stage or "runtime-identity", backend=exc.backend
        ) from exc
    return FrozenInputs(
        shards=shards,
        selections=selections,
        selection_manifest=selection_manifest,
        worker=worker,
        model_pack=model_pack,
        cpu_backend=cpu_backend,
        directml_backend=directml_backend,
        runtimes=runtimes,
        model_id=model_id,
        model_revision=model_revision,
        model_pack_sha256=model_pack_sha256,
        worker_sha256=benchmark_core.sha256_file(worker),
    )


def parse_train_annotation(row_index: int, value: str) -> cord.ParsedCordAnnotation:
    try:
        original = json.loads(
            value,
            object_pairs_hook=cord._reject_duplicate_json_members,
            parse_constant=acceptance_policy._reject_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AcceptanceError(
            "A selected train annotation is not strict JSON", stage="annotation-parse"
        ) from exc
    if not isinstance(original, dict):
        raise AcceptanceError(
            "A selected train annotation is not an object", stage="annotation-parse"
        )
    meta = original.get("meta")
    if not isinstance(meta, dict) or meta.get("split") != "train":
        raise AcceptanceError(
            "A selected annotation is not from the train split", stage="annotation-parse"
        )
    adapted = json.loads(cord.canonical_json(original))
    adapted["meta"]["split"] = "test"
    try:
        parsed = cord.parse_cord_annotation(row_index, adapted, expected_split="test")
    except benchmark_core.BenchmarkError as exc:
        raw_diagnostic = getattr(exc, "diagnostic", None)
        diagnostic = (
            {**raw_diagnostic, "globalRowIndex": row_index}
            if isinstance(raw_diagnostic, Mapping)
            else {"kind": "annotationParse", "globalRowIndex": row_index}
        )
        raise AcceptanceError(
            str(exc),
            stage=exc.stage or "annotation-parse",
            diagnostic=diagnostic,
        ) from exc
    return replace(parsed, document=original)


def _validate_train_schema(parquet: Any, pyarrow: Any, *, shard_name: str) -> None:
    if parquet.metadata.num_rows != 200:
        raise AcceptanceError(
            f"The pinned train shard row count changed: {shard_name}", stage="corpus-read"
        )
    schema = parquet.schema_arrow
    columns = frozenset(schema.names)
    image_type = schema.field("image").type if "image" in columns else None
    valid = (
        columns == {"image", "ground_truth"}
        and image_type is not None
        and pyarrow.types.is_struct(image_type)
        and tuple(field.name for field in image_type) == ("bytes", "path")
        and pyarrow.types.is_binary(image_type.field("bytes").type)
        and pyarrow.types.is_string(image_type.field("path").type)
        and pyarrow.types.is_string(schema.field("ground_truth").type)
    )
    if not valid:
        raise AcceptanceError(
            f"The pinned train shard schema changed: {shard_name}", stage="corpus-read"
        )


def extract_role_inputs(
    shards: Sequence[Path],
    role_selection: acceptance_policy.ValidatedRoleSelection,
    output_root: Path,
) -> PreparedRoleInputs:
    """Materialize selected images without reading any selected annotation value."""
    role = role_selection.role
    try:
        acceptance_policy.validate_role_selection(role_selection, expected_role=role)
        import pyarrow
        import pyarrow.parquet as pq
    except (ImportError, acceptance_policy.PolicyError) as exc:
        raise AcceptanceError(str(exc), stage="dependency-check") from exc
    if pyarrow.__version__ != selection_builder.PYARROW_VERSION:
        raise AcceptanceError("The pinned PyArrow runtime changed", stage="dependency-check")
    root = output_root.expanduser().resolve()
    if root.exists():
        raise AcceptanceError("The role input output already exists", stage="corpus-extract")
    root.mkdir(parents=True)
    selected = {(entry.shard_ordinal, entry.row_index): entry for entry in role_selection.entries}
    images_by_index: dict[int, PreparedImage] = {}
    for shard_ordinal, shard in enumerate(shards):
        try:
            parquet = pq.ParquetFile(shard)
            _validate_train_schema(parquet, pyarrow, shard_name=shard.name)
            # Excluding ground_truth at the Parquet reader is the blind-holdout
            # boundary: no confirmatory label value is decoded before the ledger.
            batches = parquet.iter_batches(batch_size=1, columns=["image"])
            row_count = 0
            for row_index, batch in enumerate(batches):
                if batch.num_rows != 1:
                    raise AcceptanceError(
                        "The role-only image reader did not preserve single-row batches",
                        stage="corpus-read",
                    )
                row_count += batch.num_rows
                entry = selected.get((shard_ordinal, row_index))
                if entry is None:
                    continue
                row = batch.to_pylist()[0]
                raw_image = cord._embedded_image(row.get("image"))
                image_sha256 = benchmark_core.sha256_bytes(raw_image)
                if image_sha256 != entry.image_sha256:
                    raise AcceptanceError(
                        "A selected image does not match the frozen role manifest",
                        stage="corpus-identity",
                    )
                extension = cord._image_extension(raw_image)
                relative_path = (
                    f"images/{role}-global-{entry.global_index:04d}-{image_sha256[:12]}{extension}"
                )
                image_path = root / relative_path
                _atomic_create(image_path, raw_image)
                images_by_index[entry.global_index] = PreparedImage(
                    row_index=entry.global_index,
                    relative_path=relative_path,
                    path=image_path,
                    length=len(raw_image),
                    sha256=image_sha256,
                )
            if row_count != 200:
                raise AcceptanceError(
                    f"The train reader returned an invalid row count: {shard.name}",
                    stage="corpus-read",
                )
        except AcceptanceError:
            raise
        except Exception as exc:
            raise AcceptanceError(
                f"The pinned train shard could not be read: {shard.name}",
                stage="corpus-read",
            ) from exc
    expected_indices = [entry.global_index for entry in role_selection.entries]
    if set(images_by_index) != set(expected_indices):
        raise AcceptanceError(
            f"The {role} image reader did not return its complete frozen role",
            stage="corpus-read",
        )
    images = tuple(images_by_index[index] for index in expected_indices)
    input_rows = [
        {
            "schemaVersion": SCHEMA_VERSION,
            "role": role,
            "rowIndex": image.row_index,
            "path": image.relative_path,
            "length": image.length,
            "sha256": image.sha256,
        }
        for image in images
    ]
    worker_rows = [
        {
            "schemaVersion": cord.OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION,
            "path": str(image.path),
            "length": image.length,
            "sha256": image.sha256,
        }
        for image in images
    ]
    input_manifest = root / f"cord-v2-train-{role}-input-manifest.jsonl"
    worker_manifest = root / f"cord-v2-train-{role}-worker-input-manifest.jsonl"
    inventory = root / f"cord-v2-train-{role}-inventory.txt"
    _atomic_create(
        input_manifest,
        "".join(cord.canonical_json(row) + "\n" for row in input_rows).encode("utf-8"),
    )
    _atomic_create(
        worker_manifest,
        "".join(cord.canonical_json(row) + "\n" for row in worker_rows).encode("utf-8"),
    )
    _atomic_create(
        inventory,
        "".join(f"{image.path}\n" for image in images).encode("utf-8"),
    )
    selection_sha256 = acceptance_policy.sha256_canonical(
        [{"imageSha256": image.sha256, "rowIndex": image.row_index} for image in images]
    )
    return PreparedRoleInputs(
        images=images,
        input_manifest=input_manifest,
        worker_manifest=worker_manifest,
        inventory=inventory,
        input_manifest_sha256=benchmark_core.sha256_file(input_manifest),
        worker_manifest_sha256=benchmark_core.sha256_file(worker_manifest),
        selection_sha256=selection_sha256,
    )


def extract_role_corpus(
    shards: Sequence[Path],
    role_selection: acceptance_policy.ValidatedRoleSelection,
    output_root: Path,
    *,
    reuse_existing: bool = False,
) -> cord.ExtractedCorpus:
    role = role_selection.role
    try:
        acceptance_policy.validate_role_selection(role_selection, expected_role=role)
        import pyarrow
        import pyarrow.parquet as pq
    except (ImportError, acceptance_policy.PolicyError) as exc:
        raise AcceptanceError(str(exc), stage="dependency-check") from exc
    if pyarrow.__version__ != selection_builder.PYARROW_VERSION:
        raise AcceptanceError("The pinned PyArrow runtime changed", stage="dependency-check")
    root = output_root.expanduser().resolve()
    if root.exists() and not reuse_existing:
        raise AcceptanceError("The role corpus output already exists", stage="corpus-extract")
    if reuse_existing:
        if not root.is_dir() or root.is_symlink():
            raise AcceptanceError(
                "The prepared role corpus is unavailable", stage="corpus-identity"
            )
    else:
        root.mkdir(parents=True)

    selected = {(entry.shard_ordinal, entry.row_index): entry for entry in role_selection.entries}
    documents_by_index: dict[int, cord.CordDocument] = {}
    annotation_by_image: dict[str, str] = {}
    for shard_ordinal, shard in enumerate(shards):
        try:
            parquet = pq.ParquetFile(shard)
            _validate_train_schema(parquet, pyarrow, shard_name=shard.name)
            batches = parquet.iter_batches(batch_size=1, columns=["image", "ground_truth"])
            row_count = 0
            for row_index, batch in enumerate(batches):
                if batch.num_rows != 1:
                    raise AcceptanceError(
                        "The role-only train reader did not preserve single-row batches",
                        stage="corpus-read",
                    )
                row_count += batch.num_rows
                entry = selected.get((shard_ordinal, row_index))
                if entry is None:
                    # PyArrow may materialize an interleaved ground_truth column page,
                    # but the other role is never converted to a Python value and is
                    # never passed to the annotation parser.
                    continue
                row = batch.to_pylist()[0]
                raw_image = cord._embedded_image(row.get("image"))
                image_sha256 = benchmark_core.sha256_bytes(raw_image)
                if image_sha256 != entry.image_sha256:
                    raise AcceptanceError(
                        "A selected image does not match the frozen role manifest",
                        stage="corpus-identity",
                    )
                ground_truth = row.get("ground_truth")
                if not isinstance(ground_truth, str):
                    raise AcceptanceError(
                        "A selected train annotation is unavailable", stage="annotation-parse"
                    )
                annotation = parse_train_annotation(entry.global_index, ground_truth)
                width, height = cord._verify_embedded_image_dimensions(raw_image, annotation)
                annotation_sha256 = benchmark_core.sha256_bytes(
                    cord.canonical_json(annotation.document).encode("utf-8")
                )
                previous = annotation_by_image.setdefault(image_sha256, annotation_sha256)
                if previous != annotation_sha256:
                    raise AcceptanceError(
                        "Duplicate selected images have different canonical annotations",
                        stage="annotation-identity",
                    )
                extension = cord._image_extension(raw_image)
                relative_path = (
                    f"images/{role}-global-{entry.global_index:04d}-{image_sha256[:12]}{extension}"
                )
                image_path = root / relative_path
                if reuse_existing:
                    if (
                        not image_path.is_file()
                        or image_path.is_symlink()
                        or image_path.read_bytes() != raw_image
                    ):
                        raise AcceptanceError(
                            "A prepared role image changed", stage="corpus-identity"
                        )
                else:
                    _atomic_create(image_path, raw_image)
                documents_by_index[entry.global_index] = cord.CordDocument(
                    row_index=entry.global_index,
                    image_id=annotation.image_id,
                    relative_path=relative_path,
                    path=image_path,
                    length=len(raw_image),
                    sha256=image_sha256,
                    annotation_sha256=annotation_sha256,
                    lines=annotation.lines,
                    dontcare_polygons=annotation.dontcare_polygons,
                    repeating_symbol_polygons=annotation.repeating_symbol_polygons,
                    clipped_valid_lines=annotation.clipped_valid_lines,
                    clipped_dontcare_regions=annotation.clipped_dontcare_regions,
                    clipped_repeating_symbol_regions=(annotation.clipped_repeating_symbol_regions),
                    words=annotation.words,
                    rows=annotation.rows,
                    roi_polygon=annotation.roi_polygon,
                    annotation_boundary_clips=annotation.annotation_boundary_clips,
                )
                if width <= 0 or height <= 0:
                    raise AcceptanceError(
                        "A selected image has invalid dimensions", stage="corpus-read"
                    )
            if row_count != 200:
                raise AcceptanceError(
                    f"The train reader returned an invalid row count: {shard.name}",
                    stage="corpus-read",
                )
        except AcceptanceError:
            raise
        except Exception as exc:
            raise AcceptanceError(
                f"The pinned train shard could not be read: {shard.name}",
                stage="corpus-read",
            ) from exc

    expected_indices = [entry.global_index for entry in role_selection.entries]
    if set(documents_by_index) != set(expected_indices):
        raise AcceptanceError(
            f"The {role} reader did not return its complete frozen role",
            stage="corpus-read",
        )
    documents = tuple(documents_by_index[index] for index in expected_indices)
    corpus_rows = [
        {
            "schemaVersion": SCORING_CORPUS_MANIFEST_SCHEMA_VERSION,
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
        for document in documents
    ]
    worker_rows = [
        {
            "schemaVersion": cord.OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION,
            "path": str(document.path),
            "length": document.length,
            "sha256": document.sha256,
        }
        for document in documents
    ]
    corpus_manifest = root / f"cord-v2-train-{role}-manifest.jsonl"
    worker_manifest = root / f"cord-v2-train-{role}-worker-input-manifest.jsonl"
    inventory = root / f"cord-v2-train-{role}-inventory.txt"
    outputs = {
        corpus_manifest: "".join(cord.canonical_json(row) + "\n" for row in corpus_rows).encode(
            "utf-8"
        ),
        worker_manifest: "".join(cord.canonical_json(row) + "\n" for row in worker_rows).encode(
            "utf-8"
        ),
        inventory: "".join(f"{document.path}\n" for document in documents).encode("utf-8"),
    }
    for path, expected_bytes in outputs.items():
        if reuse_existing:
            if not path.is_file() or path.is_symlink() or path.read_bytes() != expected_bytes:
                raise AcceptanceError("A prepared role manifest changed", stage="corpus-identity")
        else:
            _atomic_create(path, expected_bytes)
    return cord.ExtractedCorpus(
        documents=documents,
        corpus_manifest=corpus_manifest,
        worker_manifest=worker_manifest,
        inventory=inventory,
        corpus_manifest_sha256=benchmark_core.sha256_file(corpus_manifest),
        worker_manifest_sha256=benchmark_core.sha256_file(worker_manifest),
        selection_sha256=cord._selection_sha256(documents),
        split="train",
    )


def _scoring_constants() -> dict[str, Any]:
    return {
        "annotationBoundaryMaximumOvershootPixels": (
            cord.ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_PIXELS
        ),
        "annotationBoundaryMaximumOvershootRatio": (
            cord.ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_RATIO
        ),
        "annotationBoundaryMinimumRetainedAreaRatio": (
            cord.ANNOTATION_BOUNDARY_MINIMUM_RETAINED_AREA_RATIO
        ),
        "confidenceParityMaximumAbsoluteDelta": cord.CONFIDENCE_PARITY_MAX_ABS_DELTA,
        "ignorePrecisionThreshold": cord.IGNORE_PRECISION_THRESHOLD,
        "predictionRowNormalizedDistance": cord.ROW_CLUSTER_NORMALIZED_DISTANCE,
        "referenceMergeNormalizedDistance": cord.REFERENCE_ROW_MERGE_NORMALIZED_DISTANCE,
        "referenceSplitNormalizedDistance": cord.REFERENCE_ROW_SPLIT_NORMALIZED_DISTANCE,
        "segmentationOverlapThreshold": cord.SEGMENTATION_OVERLAP_THRESHOLD,
        "shapelyVersion": cord.SHAPELY_VERSION,
    }


def _source_hashes() -> dict[str, Any]:
    paths = {
        "acceptanceWrapper": Path(__file__).resolve(),
        "benchmarkDependency": Path(benchmark_core.__file__).resolve(),
        "cordScorer": Path(cord.__file__).resolve(),
        "policyModule": Path(acceptance_policy.__file__).resolve(),
        "selectionModule": Path(selection_builder.__file__).resolve(),
    }
    loaded_names = {
        "benchmarkDependency": "benchmark_ocr",
        "cordScorer": "benchmark_ocr_cord",
        "policyModule": "ocr_acceptance_policy",
        "selectionModule": "generate_cord_selection",
    }
    repository_root = Path(__file__).resolve().parents[2]
    file_hashes = {}
    current_file_hashes = {}
    for name, path in paths.items():
        loaded_name = loaded_names.get(name)
        current_file_hashes[name] = benchmark_core.sha256_file(path)
        file_hashes[name] = (
            _LOADED_SOURCE_HASHES[loaded_name]
            if loaded_name in _LOADED_SOURCE_HASHES
            else current_file_hashes[name]
        )
    changed = [name for name in paths if file_hashes[name] != current_file_hashes[name]]
    if changed:
        raise AcceptanceError(
            "A loaded frozen source no longer matches its on-disk bytes",
            stage="source-identity",
        )
    return {
        "files": {
            name: {
                "currentFileSha256": current_file_hashes[name],
                "path": paths[name].relative_to(repository_root).as_posix(),
                "sha256": file_hashes[name],
            }
            for name in sorted(paths)
        },
        "scoringConstants": _scoring_constants(),
        "scoringConstantsSha256": acceptance_policy.sha256_canonical(_scoring_constants()),
    }


def build_live_identity(
    frozen: FrozenInputs,
    *,
    calibration_corpus_manifest_sha256: str,
    calibration_worker_manifest_sha256: str,
    confirmatory_input_manifest_sha256: str,
    confirmatory_worker_manifest_sha256: str,
) -> tuple[dict[str, Any], dict[str, Any]]:
    sources = _source_hashes()
    try:
        _, _, shapely_version, geos_version = cord._shapely_runtime()
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(str(exc), stage="dependency-identity") from exc
    selection_bytes = frozen.selection_manifest.stat().st_size
    selection_sha256 = benchmark_core.sha256_file(frozen.selection_manifest)
    identity = {
        "benchmark": {
            "genericBenchmarkSha256": sources["files"]["benchmarkDependency"]["sha256"],
            "policyModuleSha256": sources["files"]["policyModule"]["sha256"],
            "protocol": cord.PROTOCOL,
            "schemaVersion": cord.SCHEMA_VERSION,
            "scorerSha256": sources["files"]["cordScorer"]["sha256"],
            "scoringConstantsSha256": sources["scoringConstantsSha256"],
            "scriptSha256": sources["files"]["acceptanceWrapper"]["sha256"],
        },
        "corpus": {
            "calibrationCorpusManifestSha256": calibration_corpus_manifest_sha256,
            "calibrationSelectionSha256": acceptance_policy.SELECTION_ENTRIES_SHA256["calibration"],
            "calibrationWorkerManifestSha256": calibration_worker_manifest_sha256,
            "completeSelectionSha256": acceptance_policy.SELECTION_ENTRIES_SHA256["complete"],
            "confirmatoryInputManifestSha256": confirmatory_input_manifest_sha256,
            "confirmatorySelectionSha256": acceptance_policy.SELECTION_ENTRIES_SHA256[
                "confirmatory"
            ],
            "confirmatoryWorkerManifestSha256": confirmatory_worker_manifest_sha256,
            "datasetId": acceptance_policy.DATASET_ID,
            "datasetRevision": acceptance_policy.DATASET_REVISION,
            "selectionManifestBytes": selection_bytes,
            "selectionManifestSha256": selection_sha256,
            # `verify_train_shards` hashes every byte before this identity is built.
            "trainShardSha256": list(acceptance_policy.TRAIN_SHARD_SHA256),
        },
        "dependencies": {
            "geos": geos_version,
            "pyarrow": cord._distribution_version("pyarrow"),
            "shapely": shapely_version,
        },
        "engine": {
            "modelId": frozen.model_id,
            "modelPackSha256": benchmark_core.sha256_file(frozen.model_pack),
            "modelRevision": frozen.model_revision,
            "name": acceptance_policy.ENGINE_NAME,
            "version": acceptance_policy.ENGINE_VERSION,
            "workerSha256": benchmark_core.sha256_file(frozen.worker),
        },
        "runtimeProfiles": {
            name: {
                "executableSha256": runtime["executableSha256"],
                "inventorySha256": runtime["inventorySha256"],
                "pythonVersion": runtime["pythonVersion"],
            }
            for name, runtime in frozen.runtimes.items()
        },
    }
    try:
        return acceptance_policy.validate_identity(identity), sources
    except acceptance_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="frozen-identity") from exc


def validate_micro_recomputation(metrics: Mapping[str, Any]) -> dict[str, Any]:
    per_document = metrics.get("perDocument")
    recorded_micro = metrics.get("micro")
    if not isinstance(per_document, list) or not isinstance(recorded_micro, Mapping):
        raise AcceptanceError(
            "The scorer metrics do not expose per-document and micro values", stage="metrics"
        )
    try:
        recomputed_micro = cord.aggregate_metrics(per_document)["micro"]
    except (benchmark_core.BenchmarkError, KeyError, TypeError, ValueError) as exc:
        raise AcceptanceError(
            "The micro metrics could not be recomputed from per-document counts", stage="metrics"
        ) from exc
    results = {}
    for name, path in MICRO_METRIC_PATHS.items():
        recorded: Any = recorded_micro
        recomputed: Any = recomputed_micro
        for key in path:
            if not isinstance(recorded, Mapping) or not isinstance(recomputed, Mapping):
                raise AcceptanceError(
                    f"The {name} micro metric path is unavailable", stage="metrics"
                )
            recorded = recorded.get(key)
            recomputed = recomputed.get(key)
        if recorded != recomputed:
            raise AcceptanceError(
                f"The recorded {name} does not match per-document recomputation",
                stage="metrics",
            )
        results[name] = {"recorded": recorded, "recomputed": recomputed}
    return {
        "allFiveMicroMetricsRecomputable": True,
        "countSources": {
            "characterErrorRate": ["counts.characterEdits", "counts.referenceCharacters"],
            "exactEndToEndRowHmean": [
                "endToEndExact.matchedPredictions",
                "endToEndExact.predictedUnits",
                "endToEndExact.matchedTruths",
                "endToEndExact.referenceUnits",
            ],
            "exactTokenF1": [
                "counts.matchingTokensOrderInvariant",
                "counts.predictedWords",
                "counts.referenceWords",
            ],
            "localizationCoverageHmean": [
                "detection.matchedPredictions",
                "detection.predictedUnits",
                "detection.matchedTruths",
                "detection.referenceUnits",
            ],
            "wordErrorRate": ["counts.wordEdits", "counts.referenceWords"],
        },
        "metrics": results,
        "missingCountGap": None,
    }


def _determinism_row_indices(
    role: acceptance_policy.ValidatedRoleSelection,
) -> tuple[int, ...]:
    indices = tuple(entry.global_index for entry in role.entries[:DETERMINISM_DOCUMENTS])
    if len(indices) != DETERMINISM_DOCUMENTS or indices != tuple(sorted(indices)):
        raise AcceptanceError(
            "The frozen role does not provide the first ten global indices",
            stage="determinism-selection",
        )
    return indices


def _create_determinism_corpus(
    corpus: cord.ExtractedCorpus,
    role: acceptance_policy.ValidatedRoleSelection,
    output_root: Path,
) -> cord.ExtractedCorpus:
    try:
        return cord.create_corpus_view(
            corpus,
            _determinism_row_indices(role),
            output_root,
            name=f"{role.role}-determinism-first-10-global-indices",
        )
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(
            str(exc), stage=exc.stage or "determinism-selection", backend=exc.backend
        ) from exc


def _benchmark_backend(
    frozen: FrozenInputs,
    quality_corpus: cord.ExtractedCorpus,
    determinism_corpus: cord.ExtractedCorpus,
    backend: benchmark_core.Backend,
    output_root: Path,
    timeout_seconds: float,
) -> dict[str, Any]:
    try:
        runtime_name = "cpu" if backend.requested_provider == "cpu" else "directml"
        return cord.benchmark_backend(
            backend=backend,
            quality_corpus=quality_corpus,
            determinism_corpus=determinism_corpus,
            determinism_repetitions=DETERMINISM_REPETITIONS,
            output_root=output_root,
            runtime=_runtime_reference(frozen.runtimes[runtime_name]),
            worker=frozen.worker,
            model_pack=frozen.model_pack,
            model_id=frozen.model_id,
            revision=frozen.model_revision,
            model_pack_sha256=frozen.model_pack_sha256,
            worker_sha256=frozen.worker_sha256,
            engine_version=acceptance_policy.ENGINE_VERSION,
            threads=0,
            timeout_seconds=timeout_seconds,
            thresholds=dict(NO_THRESHOLD_OVERRIDES),
        )
    except benchmark_core.BenchmarkError as exc:
        raise AcceptanceError(
            str(exc), stage=exc.stage or "backend-run", backend=exc.backend
        ) from exc


def _determinism_checks(
    run: Mapping[str, Any], determinism_corpus: cord.ExtractedCorpus
) -> dict[str, bool]:
    detail = run.get("determinism")
    repetitions = detail.get("runs") if isinstance(detail, Mapping) else None
    expected_rows = [document.row_index for document in determinism_corpus.documents]
    if not isinstance(repetitions, list):
        repetitions = []

    def one_hash(path: Sequence[str]) -> bool:
        values: set[str] = set()
        for repetition in repetitions:
            value: Any = repetition
            for key in path:
                value = value.get(key) if isinstance(value, Mapping) else None
            if not isinstance(value, str) or len(value) != 64:
                return False
            values.add(value)
        return len(repetitions) == DETERMINISM_REPETITIONS and len(values) == 1

    return {
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
            isinstance(detail, Mapping)
            and detail.get("selectionSha256") == determinism_corpus.selection_sha256
        ),
        "rawPairBytesDeterministic": one_hash(("rawOutputHashes", "pairSha256")),
        "canonicalEvidenceDeterministic": (
            run.get("canonicalEvidenceDeterministic") is True
            and one_hash(("canonicalEvidenceSha256",))
        ),
        "criticalEvidenceDeterministic": (
            run.get("criticalEvidenceDeterministic") is True
            and one_hash(("criticalEvidenceSha256",))
        ),
        "aggregateMetricsDeterministic": (
            run.get("metricsDeterministic") is True and one_hash(("metricsSha256",))
        ),
        "perDocumentMetricsDeterministic": one_hash(("perDocumentMetricsSha256",)),
        "coreByteDeterminismPassed": (
            run.get("byteDeterminismEvaluated") is True and run.get("byteDeterministic") is True
        ),
        "executionShapeStable": all(
            run.get(key) is True
            for key in (
                "executionProviderCountsStable",
                "stableResolvedProvider",
                "stableResolvedThreadCounts",
                "stableResolvedWorkerCounts",
                "stableRuntime",
            )
        ),
        "thresholdOverridesAbsent": (
            run.get("qualityGatePassed") is None
            and all(
                repetition.get("qualityGatePassed") is None
                for repetition in repetitions
                if isinstance(repetition, Mapping)
            )
        ),
    }


def _public_run(run: Mapping[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in run.items() if not key.startswith("_")}


def _artifact(path: Path, *, evidence_root: Path) -> dict[str, Any]:
    resolved = path.expanduser().resolve()
    root = evidence_root.expanduser().resolve()
    try:
        relative = resolved.relative_to(root).as_posix()
    except ValueError as exc:
        raise AcceptanceError(
            "An evidence artifact is outside the declared evidence root",
            stage="artifact-identity",
        ) from exc
    return {
        "bytes": resolved.stat().st_size,
        "path": relative,
        "sha256": benchmark_core.sha256_file(resolved),
    }


def _resolve_evidence_artifact(evidence_root: Path, value: Any) -> Path:
    if not isinstance(value, str) or not value or "\\" in value:
        raise AcceptanceError("An evidence artifact path is invalid", stage="calibration-input")
    relative = PurePosixPath(value)
    windows_path = PureWindowsPath(value)
    if (
        relative.is_absolute()
        or windows_path.is_absolute()
        or bool(windows_path.drive)
        or ".." in relative.parts
        or "." in relative.parts
    ):
        raise AcceptanceError(
            "An evidence artifact path escapes its root", stage="calibration-input"
        )
    root = evidence_root.expanduser().resolve()
    candidate = root.joinpath(*relative.parts).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as exc:
        raise AcceptanceError(
            "An evidence artifact path escapes its root", stage="calibration-input"
        ) from exc
    return candidate


def _runtime_relative_path(value: Any, *, allow_dot: bool = False) -> bool:
    if not isinstance(value, str) or not value or "\\" in value:
        return False
    if value == ".":
        return allow_dot
    posix = PurePosixPath(value)
    windows = PureWindowsPath(value)
    return not (
        posix.is_absolute()
        or windows.is_absolute()
        or windows.drive
        or ".." in posix.parts
        or "." in posix.parts
    )


def _reject_absolute_runtime_values(profile_name: str, item: Any) -> None:
    if isinstance(item, Mapping):
        for child in item.values():
            _reject_absolute_runtime_values(profile_name, child)
    elif isinstance(item, list):
        for child in item:
            _reject_absolute_runtime_values(profile_name, child)
    elif isinstance(item, str):
        posix = PurePosixPath(item)
        windows = PureWindowsPath(item)
        if posix.is_absolute() or windows.is_absolute() or windows.drive:
            raise AcceptanceError(
                f"The {profile_name} runtime inventory exposes an absolute path",
                stage="runtime-identity",
            )


def _valid_lower_hex(value: Any, length: int) -> bool:
    return (
        isinstance(value, str)
        and len(value) == length
        and all(character in "0123456789abcdef" for character in value)
    )


def _validate_directml_inventory(
    profile_name: str, value: Mapping[str, Any], *, required: bool
) -> None:
    if (
        set(value) != {"adapters", "required", "status", "systemDlls"}
        or value.get("required") is not required
        or not isinstance(value.get("systemDlls"), list)
        or not isinstance(value.get("adapters"), list)
    ):
        raise AcceptanceError(
            f"The {profile_name} DirectML host inventory is invalid",
            stage="runtime-identity",
        )
    system_dlls = value["systemDlls"]
    adapters = value["adapters"]
    if not required:
        if value.get("status") != "not-required" or system_dlls or adapters:
            raise AcceptanceError(
                f"The {profile_name} profile unexpectedly contains DirectML host data",
                stage="runtime-identity",
            )
        return
    if value.get("status") != "available":
        raise AcceptanceError(
            "The DirectML host inventory is unavailable", stage="runtime-identity"
        )

    previous_dll = ""
    for index, row in enumerate(system_dlls):
        name = row.get("name") if isinstance(row, Mapping) else None
        length = row.get("bytes") if isinstance(row, Mapping) else None
        if (
            not isinstance(row, Mapping)
            or set(row) != {"bytes", "fileVersion", "name", "productVersion", "sha256"}
            or not isinstance(name, str)
            or not name
            or name != name.lower()
            or name <= previous_dll
            or isinstance(length, bool)
            or not isinstance(length, int)
            or length <= 0
            or not isinstance(row.get("fileVersion"), str)
            or not isinstance(row.get("productVersion"), str)
        ):
            raise AcceptanceError(
                f"The DirectML system DLL {index} is invalid", stage="runtime-identity"
            )
        _ledger_sha256(row.get("sha256"), name=f"DirectML system DLL {index} SHA-256")
        previous_dll = name
    if not benchmark_core.WINDOWS_DIRECTML_REQUIRED_DLLS.issubset(
        {row["name"] for row in system_dlls}
    ):
        raise AcceptanceError(
            "The DirectML runtime inventory omits a required system DLL",
            stage="runtime-identity",
        )

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
    previous_adapter = ""
    healthy = False
    for index, row in enumerate(adapters):
        identity_hash = row.get("pnpDeviceIdSha256") if isinstance(row, Mapping) else None
        if (
            not isinstance(row, Mapping)
            or set(row) != adapter_keys
            or any(not isinstance(item, str) for item in row.values())
            or not row.get("name")
            or not _valid_lower_hex(identity_hash, 64)
            or identity_hash <= previous_adapter
            or row.get("vendorId")
            and not _valid_lower_hex(row["vendorId"], 4)
            or row.get("deviceId")
            and not _valid_lower_hex(row["deviceId"], 4)
            or row.get("subsystemId")
            and not _valid_lower_hex(row["subsystemId"], 8)
        ):
            raise AcceptanceError(
                f"The DirectML adapter {index} is invalid", stage="runtime-identity"
            )
        previous_adapter = identity_hash
        healthy = healthy or row["status"].casefold() == "ok"
    if not healthy:
        raise AcceptanceError(
            "The DirectML runtime inventory has no healthy adapter",
            stage="runtime-identity",
        )


def _validate_runtime_inventory(profile_name: str, value: Mapping[str, Any]) -> None:
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
    expected_required = {
        "benchmark": [],
        "cpu": ["CPUExecutionProvider"],
        "directml": ["CPUExecutionProvider", "DmlExecutionProvider"],
    }.get(profile_name)
    provider = value.get("providerAvailability")
    packages = value.get("packages")
    executable = value.get("executable")
    runtime_roots = value.get("runtimeRoots")
    load_paths = value.get("loadPaths")
    if (
        expected_required is None
        or set(value) != expected_keys
        or value.get("schemaVersion") != 2
        or value.get("requestedProvider") != profile_name
        or any(
            not isinstance(value.get(name), str) or not value.get(name)
            for name in (
                "executableName",
                "implementation",
                "machine",
                "pythonVersion",
                "release",
                "system",
            )
        )
        or not isinstance(packages, Mapping)
        or list(packages) != sorted(packages)
        or any(
            not isinstance(name, str) or not name or not isinstance(version, str) or not version
            for name, version in packages.items()
        )
        or not isinstance(provider, Mapping)
        or set(provider) != {"available", "required", "requirementsSatisfied"}
        or not isinstance(provider.get("available"), list)
        or provider.get("available") != sorted(set(provider.get("available", [])))
        or provider.get("required") != expected_required
        or provider.get("requirementsSatisfied") is not True
        or any(item not in provider.get("available", []) for item in expected_required)
        or value.get("offlineEnvironment") != benchmark_core.OFFLINE_ENVIRONMENT
        or value.get("environmentPolicy") != benchmark_core.runtime_environment_policy()
        or not isinstance(executable, Mapping)
        or set(executable) != {"bytes", "path", "rootId", "sha256"}
        or not isinstance(runtime_roots, list)
        or not runtime_roots
        or not isinstance(load_paths, list)
        or not load_paths
        or not isinstance(value.get("windowsDirectml"), Mapping)
    ):
        raise AcceptanceError(
            f"The {profile_name} full runtime inventory is incomplete",
            stage="runtime-identity",
        )
    _reject_absolute_runtime_values(profile_name, value)
    _ledger_sha256(value.get("executableSha256"), name="runtime executable SHA-256")
    _ledger_sha256(value.get("inventorySha256"), name="runtime inventory SHA-256")
    preimage = dict(value)
    inventory_sha256 = preimage.pop("inventorySha256")
    if acceptance_policy.sha256_canonical(preimage) != inventory_sha256:
        raise AcceptanceError(
            f"The {profile_name} runtime inventory digest is invalid",
            stage="runtime-identity",
        )

    root_ids: set[str] = set()
    roles_by_root: dict[str, set[str]] = {}
    file_rows: dict[tuple[str, str], tuple[int, str]] = {}
    for root_index, runtime_root in enumerate(runtime_roots):
        files = runtime_root.get("files") if isinstance(runtime_root, Mapping) else None
        roles = runtime_root.get("roles") if isinstance(runtime_root, Mapping) else None
        root_id = runtime_root.get("rootId") if isinstance(runtime_root, Mapping) else None
        if (
            not isinstance(runtime_root, Mapping)
            or set(runtime_root)
            != {
                "digestSha256",
                "fileCount",
                "files",
                "roles",
                "rootId",
                "schemaVersion",
                "totalBytes",
            }
            or runtime_root.get("schemaVersion") != 1
            or root_id != f"runtime-root-{root_index}"
            or not isinstance(roles, list)
            or not roles
            or roles != sorted(set(roles))
            or isinstance(runtime_root.get("fileCount"), bool)
            or not isinstance(runtime_root.get("fileCount"), int)
            or runtime_root.get("fileCount") <= 0
            or isinstance(runtime_root.get("totalBytes"), bool)
            or not isinstance(runtime_root.get("totalBytes"), int)
            or runtime_root.get("totalBytes") <= 0
            or not isinstance(files, list)
            or len(files) != runtime_root.get("fileCount")
        ):
            raise AcceptanceError(
                f"The {profile_name} runtime root {root_index} is invalid",
                stage="runtime-identity",
            )
        root_ids.add(root_id)
        roles_by_root[root_id] = set(roles)
        _ledger_sha256(runtime_root.get("digestSha256"), name=f"runtime root {root_index} digest")
        if _sha256_bytes(
            acceptance_policy.canonical_json(files).encode("utf-8")
        ) != runtime_root.get("digestSha256"):
            raise AcceptanceError(
                f"The {profile_name} runtime root {root_index} digest is invalid",
                stage="runtime-identity",
            )
        total_bytes = 0
        previous_path = ""
        for file_index, row in enumerate(files):
            path = row.get("path") if isinstance(row, Mapping) else None
            length = row.get("bytes") if isinstance(row, Mapping) else None
            if (
                not isinstance(row, Mapping)
                or set(row) != {"bytes", "path", "sha256"}
                or not _runtime_relative_path(path)
                or path <= previous_path
                or isinstance(length, bool)
                or not isinstance(length, int)
                or length < 0
            ):
                raise AcceptanceError(
                    f"The {profile_name} runtime file {root_index}:{file_index} is invalid",
                    stage="runtime-identity",
                )
            _ledger_sha256(
                row.get("sha256"), name=f"runtime file {root_index}:{file_index} SHA-256"
            )
            file_rows[(root_id, path)] = (length, row["sha256"])
            previous_path = path
            total_bytes += length
        if total_bytes != runtime_root.get("totalBytes"):
            raise AcceptanceError(
                f"The {profile_name} runtime root {root_index} byte total changed",
                stage="runtime-identity",
            )

    for ordinal, row in enumerate(load_paths):
        path = row.get("path") if isinstance(row, Mapping) else None
        root_id = row.get("rootId") if isinstance(row, Mapping) else None
        if (
            not isinstance(row, Mapping)
            or set(row) != {"kind", "ordinal", "path", "rootId"}
            or row.get("ordinal") != ordinal
            or root_id not in root_ids
            or row.get("kind") not in {"directory", "file", "missing"}
            or not _runtime_relative_path(path, allow_dot=True)
            or row.get("kind") != "missing"
            and f"sysPath-{ordinal:03d}" not in roles_by_root.get(root_id, set())
            or row.get("kind") == "file"
            and (root_id, path) not in file_rows
        ):
            raise AcceptanceError(
                f"The {profile_name} load path {ordinal} is invalid",
                stage="runtime-identity",
            )

    all_roles = [role for roles in roles_by_root.values() for role in roles]
    expected_load_roles = {
        f"sysPath-{index:03d}" for index, row in enumerate(load_paths) if row["kind"] != "missing"
    }
    observed_load_roles = {role for role in all_roles if role.startswith("sysPath-")}
    if (
        any(all_roles.count(role) != 1 for role in ("prefix", "basePrefix", "executable"))
        or observed_load_roles != expected_load_roles
    ):
        raise AcceptanceError(
            f"The {profile_name} runtime roots do not cover every load-bearing role once",
            stage="runtime-identity",
        )

    executable_path = executable.get("path")
    executable_length = executable.get("bytes")
    executable_root_id = executable.get("rootId")
    if (
        executable_root_id not in root_ids
        or "executable" not in roles_by_root.get(executable_root_id, set())
        or not _runtime_relative_path(executable_path)
        or isinstance(executable_length, bool)
        or not isinstance(executable_length, int)
        or executable_length <= 0
        or executable.get("sha256") != value.get("executableSha256")
        or file_rows.get((executable_root_id, executable_path))
        != (executable_length, executable.get("sha256"))
        or PurePosixPath(executable_path).name != value.get("executableName")
    ):
        raise AcceptanceError(
            f"The {profile_name} executable is not covered by a runtime root",
            stage="runtime-identity",
        )
    _validate_directml_inventory(
        profile_name, value["windowsDirectml"], required=profile_name == "directml"
    )


def _persist_runtime_inventories(
    runtimes: Mapping[str, Mapping[str, Any]], output_root: Path
) -> dict[str, Path]:
    if set(runtimes) != {"benchmark", "cpu", "directml"}:
        raise AcceptanceError(
            "The three full runtime inventories are required", stage="runtime-identity"
        )
    root = output_root.expanduser().resolve()
    if root.exists():
        raise AcceptanceError(
            "The runtime inventory output already exists", stage="runtime-identity"
        )
    root.mkdir(parents=True)
    paths = {}
    for name in ("benchmark", "cpu", "directml"):
        profile = runtimes[name]
        _validate_runtime_inventory(name, profile)
        path = root / f"{name}-runtime-inventory-v2.json"
        _atomic_create(path, _canonical_bytes(profile))
        paths[name] = path
    return paths


def _runtime_inventory_snapshot(
    paths: Mapping[str, Path], runtimes: Mapping[str, Mapping[str, Any]]
) -> dict[str, Any]:
    if set(paths) != {"benchmark", "cpu", "directml"} or set(runtimes) != set(paths):
        raise AcceptanceError(
            "The persisted runtime inventory set changed", stage="runtime-identity"
        )
    snapshot = {}
    for name in ("benchmark", "cpu", "directml"):
        value, artifact_sha256 = _strict_json(
            paths[name],
            maximum_bytes=MAX_RUNTIME_INVENTORY_BYTES,
            name=f"{name} runtime inventory",
        )
        _validate_runtime_inventory(name, value)
        if acceptance_policy.canonical_json(value) != acceptance_policy.canonical_json(
            runtimes[name]
        ):
            raise AcceptanceError(
                f"The live {name} runtime differs from its persisted preimage",
                stage="runtime-identity",
            )
        snapshot[name] = {
            "artifactBytes": paths[name].stat().st_size,
            "artifactSha256": artifact_sha256,
            "executableSha256": value["executableSha256"],
            "fileCount": sum(root["fileCount"] for root in value["runtimeRoots"]),
            "inventorySha256": value["inventorySha256"],
            "loadPathsSha256": acceptance_policy.sha256_canonical(value["loadPaths"]),
            "pythonVersion": value["pythonVersion"],
            "rootCount": len(value["runtimeRoots"]),
            "runtimeRootsSha256": acceptance_policy.sha256_canonical(value["runtimeRoots"]),
            "totalBytes": sum(root["totalBytes"] for root in value["runtimeRoots"]),
        }
    return snapshot


def _runtime_reference(profile: Mapping[str, Any]) -> dict[str, Any]:
    return {
        "executableSha256": profile["executableSha256"],
        "inventorySha256": profile["inventorySha256"],
        "loadPathsSha256": acceptance_policy.sha256_canonical(profile["loadPaths"]),
        "requestedProvider": profile["requestedProvider"],
        "runtimeRootsSha256": acceptance_policy.sha256_canonical(profile["runtimeRoots"]),
        "schemaVersion": 1,
    }


def _identity_stable(
    before: Mapping[str, Any],
    sources_before: Mapping[str, Any],
    after: Mapping[str, Any],
    sources_after: Mapping[str, Any],
) -> bool:
    return acceptance_policy.canonical_json(before) == acceptance_policy.canonical_json(
        after
    ) and acceptance_policy.canonical_json(sources_before) == acceptance_policy.canonical_json(
        sources_after
    )


def _strict_jsonl(path: Path, *, name: str) -> list[dict[str, Any]]:
    resolved = path.expanduser().resolve()
    if (
        not resolved.is_file()
        or resolved.is_symlink()
        or resolved.stat().st_size <= 0
        or resolved.stat().st_size > MAX_REPORT_BYTES
    ):
        raise AcceptanceError(f"The {name} is unavailable", stage="corpus-identity")
    raw = resolved.read_bytes()
    if not raw.endswith(b"\n"):
        raise AcceptanceError(f"The {name} is not canonical JSONL", stage="corpus-identity")
    rows: list[dict[str, Any]] = []
    for raw_line in raw.splitlines(keepends=True):
        try:
            row = json.loads(
                raw_line.decode("utf-8"),
                object_pairs_hook=acceptance_policy._reject_duplicate_pairs,
                parse_constant=acceptance_policy._reject_json_constant,
            )
        except (UnicodeDecodeError, json.JSONDecodeError) as exc:
            raise AcceptanceError(
                f"The {name} is not strict UTF-8 JSONL", stage="corpus-identity"
            ) from exc
        if not isinstance(row, dict) or raw_line != _canonical_bytes(row):
            raise AcceptanceError(f"The {name} is not canonical JSONL", stage="corpus-identity")
        rows.append(row)
    return rows


def _prepared_role_snapshot(
    manifest: Path,
    worker_manifest: Path,
    inventory: Path,
    selection: acceptance_policy.ValidatedRoleSelection,
    *,
    name: str,
) -> dict[str, Any]:
    manifest_rows = _strict_jsonl(manifest, name=f"{name} manifest")
    worker_rows = _strict_jsonl(worker_manifest, name=f"{name} worker manifest")
    expected = list(selection.entries)
    if len(manifest_rows) != len(expected) or len(worker_rows) != len(expected):
        raise AcceptanceError(
            f"The {name} manifest document count changed", stage="corpus-identity"
        )
    image_identities = []
    worker_paths = []
    root = manifest.expanduser().resolve().parent
    for index, (manifest_row, worker_row, entry) in enumerate(
        zip(manifest_rows, worker_rows, expected, strict=True)
    ):
        relative_path = manifest_row.get("path")
        length = manifest_row.get("length")
        sha256 = manifest_row.get("sha256")
        worker_path_raw = worker_row.get("path")
        if (
            manifest_row.get("rowIndex") != entry.global_index
            or manifest_row.get("role") != selection.role
            or sha256 != entry.image_sha256
            or isinstance(length, bool)
            or not isinstance(length, int)
            or length <= 0
            or not isinstance(relative_path, str)
            or not relative_path
            or worker_row.get("length") != length
            or worker_row.get("sha256") != sha256
            or not isinstance(worker_path_raw, str)
        ):
            raise AcceptanceError(
                f"The {name} image identity {index} changed", stage="corpus-identity"
            )
        expected_path = (root / relative_path).resolve()
        worker_path = Path(worker_path_raw).expanduser().resolve()
        if (
            os.path.normcase(str(worker_path)) != os.path.normcase(str(expected_path))
            or not worker_path.is_relative_to(root)
            or not worker_path.is_file()
            or worker_path.is_symlink()
            or worker_path.stat().st_size != length
            or benchmark_core.sha256_file(worker_path) != sha256
        ):
            raise AcceptanceError(
                f"The {name} extracted image {index} changed", stage="corpus-identity"
            )
        worker_paths.append(str(worker_path))
        image_identities.append(
            {"length": length, "rowIndex": entry.global_index, "sha256": sha256}
        )
    inventory_path = inventory.expanduser().resolve()
    expected_inventory = "".join(f"{path}\n" for path in worker_paths).encode("utf-8")
    if (
        not inventory_path.is_file()
        or inventory_path.is_symlink()
        or inventory_path.read_bytes() != expected_inventory
    ):
        raise AcceptanceError(f"The {name} inventory changed", stage="corpus-identity")
    return {
        "documents": len(expected),
        "imageIdentitiesSha256": acceptance_policy.sha256_canonical(image_identities),
        "inventorySha256": benchmark_core.sha256_file(inventory_path),
        "manifestSha256": benchmark_core.sha256_file(manifest),
        "workerManifestSha256": benchmark_core.sha256_file(worker_manifest),
    }


def _prepared_snapshot(
    artifacts: Mapping[
        str,
        tuple[
            Path,
            Path,
            Path,
            acceptance_policy.ValidatedRoleSelection,
        ],
    ],
) -> dict[str, Any]:
    return {
        name: _prepared_role_snapshot(*paths, name=name)
        for name, paths in sorted(artifacts.items())
    }


def _reverify_identity(
    args: argparse.Namespace,
    before: Mapping[str, Any],
    sources_before: Mapping[str, Any],
    *,
    prepared_artifacts: Mapping[
        str,
        tuple[
            Path,
            Path,
            Path,
            acceptance_policy.ValidatedRoleSelection,
        ],
    ],
    snapshot_before: Mapping[str, Any],
    runtime_inventory_paths: Mapping[str, Path],
    runtime_snapshot_before: Mapping[str, Any],
) -> tuple[bool, dict[str, Any]]:
    try:
        snapshot_after = _prepared_snapshot(prepared_artifacts)
        frozen_after = verify_frozen_inputs(args)
        runtime_snapshot_after = _runtime_inventory_snapshot(
            runtime_inventory_paths, frozen_after.runtimes
        )
        after, sources_after = build_live_identity(
            frozen_after,
            calibration_corpus_manifest_sha256=snapshot_after["calibration"]["manifestSha256"],
            calibration_worker_manifest_sha256=snapshot_after["calibration"][
                "workerManifestSha256"
            ],
            confirmatory_input_manifest_sha256=snapshot_after["confirmatoryInput"][
                "manifestSha256"
            ],
            confirmatory_worker_manifest_sha256=snapshot_after["confirmatoryInput"][
                "workerManifestSha256"
            ],
        )
    except Exception as exc:
        return False, {
            "passed": False,
            "error": {
                "backend": getattr(exc, "backend", None),
                "stage": getattr(exc, "stage", "identity-reverification"),
                "type": type(exc).__name__,
            },
        }
    prepared_stable = acceptance_policy.canonical_json(
        snapshot_before
    ) == acceptance_policy.canonical_json(snapshot_after)
    runtime_stable = acceptance_policy.canonical_json(
        runtime_snapshot_before
    ) == acceptance_policy.canonical_json(runtime_snapshot_after)
    stable = (
        prepared_stable
        and runtime_stable
        and _identity_stable(before, sources_before, after, sources_after)
    )
    return stable, {
        "passed": stable,
        "identitySha256": acceptance_policy.sha256_canonical(after),
        "preparedArtifactsStable": prepared_stable,
        "preparedSnapshotSha256": acceptance_policy.sha256_canonical(snapshot_after),
        "runtimeInventoriesStable": runtime_stable,
        "runtimeSnapshotSha256": acceptance_policy.sha256_canonical(runtime_snapshot_after),
        "sourcesSha256": acceptance_policy.sha256_canonical(sources_after),
    }


def _metrics_report(
    *,
    role: acceptance_policy.ValidatedRoleSelection,
    identity: Mapping[str, Any],
    metrics: Mapping[str, Any],
    integrity_passed: bool,
    acceptance_passed: bool,
    evidence: Mapping[str, Any],
) -> dict[str, Any]:
    return {
        "acceptancePassed": acceptance_passed,
        "documents": len(role.entries),
        "evaluationRole": role.role,
        "evidence": dict(evidence),
        "identity": dict(identity),
        "integrityPassed": integrity_passed,
        "metrics": dict(metrics),
        "metricsSha256": acceptance_policy.sha256_canonical(metrics),
        "runSucceeded": True,
        "schemaVersion": acceptance_policy.METRICS_REPORT_SCHEMA_VERSION,
        "selectionSha256": role.entries_sha256,
    }


def _stage_report(path: Path, report: Mapping[str, Any]) -> Path:
    _atomic_create(path, _canonical_bytes(report))
    return path


def execute_calibration(
    args: argparse.Namespace,
    frozen: FrozenInputs,
) -> tuple[dict[str, Any], dict[str, Any] | None]:
    phase_root = args.work_directory.expanduser().resolve() / "calibration"
    if phase_root.exists():
        raise AcceptanceError("The calibration work directory already exists", stage="arguments")
    calibration_role = frozen.selections["calibration"]
    confirmatory_role = frozen.selections["confirmatory"]
    runtime_inventory_paths = _persist_runtime_inventories(
        frozen.runtimes, phase_root / "runtime-inventories"
    )
    runtime_snapshot_before = _runtime_inventory_snapshot(runtime_inventory_paths, frozen.runtimes)
    prepared_root = phase_root / "prepared-corpora"
    calibration_corpus = extract_role_corpus(
        frozen.shards, calibration_role, prepared_root / "calibration"
    )
    confirmatory_inputs = extract_role_inputs(
        frozen.shards, confirmatory_role, prepared_root / "confirmatory"
    )
    verify_train_shards(frozen.shards)
    acceptance_policy.load_role_selections(frozen.selection_manifest)
    prepared_artifacts = {
        "calibration": (
            calibration_corpus.corpus_manifest,
            calibration_corpus.worker_manifest,
            calibration_corpus.inventory,
            calibration_role,
        ),
        "confirmatoryInput": (
            confirmatory_inputs.input_manifest,
            confirmatory_inputs.worker_manifest,
            confirmatory_inputs.inventory,
            confirmatory_role,
        ),
    }
    snapshot_before = _prepared_snapshot(prepared_artifacts)
    identity_before, sources_before = build_live_identity(
        frozen,
        calibration_corpus_manifest_sha256=snapshot_before["calibration"]["manifestSha256"],
        calibration_worker_manifest_sha256=snapshot_before["calibration"]["workerManifestSha256"],
        confirmatory_input_manifest_sha256=snapshot_before["confirmatoryInput"]["manifestSha256"],
        confirmatory_worker_manifest_sha256=snapshot_before["confirmatoryInput"][
            "workerManifestSha256"
        ],
    )
    determinism_corpus = _create_determinism_corpus(
        calibration_corpus, calibration_role, phase_root / "views"
    )
    run = _benchmark_backend(
        frozen,
        calibration_corpus,
        determinism_corpus,
        frozen.cpu_backend,
        phase_root / "results" / "cpu",
        args.timeout_seconds,
    )
    recomputation = validate_micro_recomputation(run["metrics"])
    try:
        measurements = acceptance_policy.extract_measurements(
            run["metrics"], selection=calibration_role
        )
        floor_checks = acceptance_policy.absolute_floor_checks(measurements)
    except acceptance_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="quality") from exc
    quality_passed = all(check["passed"] is True for check in floor_checks.values())
    identity_stable, identity_reverification = _reverify_identity(
        args,
        identity_before,
        sources_before,
        prepared_artifacts=prepared_artifacts,
        snapshot_before=snapshot_before,
        runtime_inventory_paths=runtime_inventory_paths,
        runtime_snapshot_before=runtime_snapshot_before,
    )
    determinism_checks = _determinism_checks(run, determinism_corpus)
    integrity_checks = {
        "allRunProvenancePassed": run.get("provenancePassed") is True,
        "determinismProtocolPassed": all(determinism_checks.values()),
        "identityStableDuringRun": identity_stable,
        "qualityDocumentCountExact": run.get("qualityRows") == len(calibration_role.entries),
        "requestedProviderCpu": run.get("requestedProvider") == "cpu",
        "resolvedProviderCpu": run.get("resolvedProvider") == "cpu",
        "runtimeHashMatches": (
            run.get("runtimeSha256") == frozen.runtimes["cpu"].get("executableSha256")
        ),
        "workerHashMatches": run.get("workerSha256") == frozen.worker_sha256,
    }
    output_integrity = all(integrity_checks.values())
    generated_at = benchmark_core.utc_timestamp()
    evidence = {
        "absoluteFloorChecks": floor_checks,
        "artifacts": {
            "calibrationCorpusManifest": _artifact(
                calibration_corpus.corpus_manifest, evidence_root=phase_root
            ),
            "calibrationInventory": _artifact(
                calibration_corpus.inventory, evidence_root=phase_root
            ),
            "calibrationWorkerManifest": _artifact(
                calibration_corpus.worker_manifest, evidence_root=phase_root
            ),
            "confirmatoryInputManifest": _artifact(
                confirmatory_inputs.input_manifest, evidence_root=phase_root
            ),
            "confirmatoryInventory": _artifact(
                confirmatory_inputs.inventory, evidence_root=phase_root
            ),
            "confirmatoryWorkerManifest": _artifact(
                confirmatory_inputs.worker_manifest, evidence_root=phase_root
            ),
            "benchmarkRuntimeInventory": _artifact(
                runtime_inventory_paths["benchmark"], evidence_root=phase_root
            ),
            "cpuRuntimeInventory": _artifact(
                runtime_inventory_paths["cpu"], evidence_root=phase_root
            ),
            "directmlRuntimeInventory": _artifact(
                runtime_inventory_paths["directml"], evidence_root=phase_root
            ),
        },
        "backend": _public_run(run),
        "benchmark": "CORD-v2-train-OCR-acceptance",
        "claimScope": "frozen 600-row CORD v2 train calibration role",
        "preparedBeforeCalibration": {
            "calibrationScoringCorpus": True,
            "confirmatoryGroundTruthConvertedOrParsed": False,
            "confirmatoryGroundTruthMayBeMaterializedByArrow": True,
            "confirmatoryOpaqueImageInputs": True,
        },
        "determinism": {
            "checks": determinism_checks,
            "repetitions": DETERMINISM_REPETITIONS,
            "rowIndices": [document.row_index for document in determinism_corpus.documents],
            "selectionSha256": determinism_corpus.selection_sha256,
        },
        "execution": {
            "backendOrder": ["cpu"],
            "determinismRepetitionsPerBackend": DETERMINISM_REPETITIONS,
            "outerRuntimeIsolation": _outer_isolation_evidence(),
            "qualityRunsPerBackend": 1,
            "threads": 0,
        },
        "generatedAtUtc": generated_at,
        "identityReverification": identity_reverification,
        "identitySources": sources_before,
        "integrityChecks": integrity_checks,
        "microMetricRecomputation": recomputation,
        "phase": "calibration",
        "protocol": PROTOCOL,
        "qualityPolicyPassed": quality_passed,
        "runtimeProfiles": runtime_snapshot_before,
        "selection": {
            "entriesSha256": calibration_role.entries_sha256,
            "manifestSha256": calibration_role.manifest_sha256,
            "role": calibration_role.role,
        },
        "thresholdOverridesPermitted": False,
    }
    report = _metrics_report(
        role=calibration_role,
        identity=identity_before,
        metrics=run["metrics"],
        integrity_passed=output_integrity,
        acceptance_passed=output_integrity and quality_passed,
        evidence=evidence,
    )
    policy_value = None
    if output_integrity and quality_passed:
        staged_report = _stage_report(phase_root / "calibration-policy-input.json", report)
        try:
            policy_value = acceptance_policy.build_policy(
                calibration_selection=calibration_role,
                calibration_report_path=staged_report,
                frozen_at_utc=generated_at,
                identity=identity_before,
            )
        except acceptance_policy.PolicyError as exc:
            raise AcceptanceError(str(exc), stage="policy") from exc
        finally:
            staged_report.unlink(missing_ok=True)
    return report, policy_value


def load_calibration_context(path: Path, evidence_root: Path) -> CalibrationContext:
    report, report_sha256 = _strict_json(
        path, maximum_bytes=MAX_REPORT_BYTES, name="calibration report"
    )
    artifact_root = evidence_root.expanduser().resolve()
    if (
        not artifact_root.is_dir()
        or artifact_root.is_symlink()
        or artifact_root.name != "calibration"
    ):
        raise AcceptanceError(
            "The explicit calibration evidence root is invalid",
            stage="calibration-input",
        )
    expected_keys = {
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
    }
    if (
        set(report) != expected_keys
        or report.get("schemaVersion") != acceptance_policy.METRICS_REPORT_SCHEMA_VERSION
        or report.get("evaluationRole") != "calibration"
        or report.get("documents") != acceptance_policy.CALIBRATION_DOCUMENTS
        or report.get("selectionSha256")
        != acceptance_policy.SELECTION_ENTRIES_SHA256["calibration"]
        or report.get("runSucceeded") is not True
        or report.get("integrityPassed") is not True
        or report.get("acceptancePassed") is not True
        or not isinstance(report.get("metrics"), Mapping)
        or acceptance_policy.sha256_canonical(report["metrics"]) != report.get("metricsSha256")
    ):
        raise AcceptanceError(
            "The calibration report is incomplete or was not accepted", stage="calibration-input"
        )
    evidence = report.get("evidence")
    if (
        not isinstance(evidence, Mapping)
        or evidence.get("protocol") != PROTOCOL
        or evidence.get("phase") != "calibration"
    ):
        raise AcceptanceError(
            "The calibration report evidence identity changed", stage="calibration-input"
        )
    artifacts = evidence.get("artifacts")
    if not isinstance(artifacts, Mapping):
        raise AcceptanceError("Calibration artifacts are unavailable", stage="calibration-input")
    expected_artifacts = {
        "benchmarkRuntimeInventory",
        "calibrationCorpusManifest",
        "calibrationInventory",
        "calibrationWorkerManifest",
        "confirmatoryInputManifest",
        "confirmatoryInventory",
        "confirmatoryWorkerManifest",
        "cpuRuntimeInventory",
        "directmlRuntimeInventory",
    }
    if set(artifacts) != expected_artifacts:
        raise AcceptanceError(
            "The calibration artifact inventory changed", stage="calibration-input"
        )

    def verified_artifact(name: str) -> tuple[Path, str]:
        artifact = artifacts.get(name)
        if not isinstance(artifact, Mapping):
            raise AcceptanceError(f"The {name} identity is unavailable", stage="calibration-input")
        artifact_path = _resolve_evidence_artifact(artifact_root, artifact.get("path"))
        expected = artifact.get("sha256")
        expected_bytes = artifact.get("bytes")
        if (
            not artifact_path.is_file()
            or artifact_path.is_symlink()
            or not isinstance(expected, str)
            or isinstance(expected_bytes, bool)
            or not isinstance(expected_bytes, int)
            or expected_bytes <= 0
            or artifact_path.stat().st_size != expected_bytes
            or benchmark_core.sha256_file(artifact_path) != expected
        ):
            raise AcceptanceError(
                f"The {name} failed live identity verification", stage="calibration-input"
            )
        return artifact_path, expected

    calibration_corpus, calibration_corpus_sha = verified_artifact("calibrationCorpusManifest")
    calibration_worker, calibration_worker_sha = verified_artifact("calibrationWorkerManifest")
    calibration_inventory, _ = verified_artifact("calibrationInventory")
    confirmatory_input, confirmatory_input_sha = verified_artifact("confirmatoryInputManifest")
    confirmatory_worker, confirmatory_worker_sha = verified_artifact("confirmatoryWorkerManifest")
    confirmatory_inventory, _ = verified_artifact("confirmatoryInventory")
    runtime_inventory_paths: dict[str, Path] = {}
    runtime_inventory_sha256: dict[str, str] = {}
    runtime_values: dict[str, dict[str, Any]] = {}
    for profile_name, artifact_name in (
        ("benchmark", "benchmarkRuntimeInventory"),
        ("cpu", "cpuRuntimeInventory"),
        ("directml", "directmlRuntimeInventory"),
    ):
        runtime_path, runtime_sha256 = verified_artifact(artifact_name)
        runtime_value, strict_sha256 = _strict_json(
            runtime_path,
            maximum_bytes=MAX_RUNTIME_INVENTORY_BYTES,
            name=f"{profile_name} runtime inventory",
        )
        if strict_sha256 != runtime_sha256:
            raise AcceptanceError(
                f"The {profile_name} runtime artifact hash changed",
                stage="calibration-input",
            )
        _validate_runtime_inventory(profile_name, runtime_value)
        runtime_inventory_paths[profile_name] = runtime_path
        runtime_inventory_sha256[profile_name] = runtime_sha256
        runtime_values[profile_name] = runtime_value
    runtime_snapshot = _runtime_inventory_snapshot(runtime_inventory_paths, runtime_values)
    if acceptance_policy.canonical_json(runtime_snapshot) != acceptance_policy.canonical_json(
        evidence.get("runtimeProfiles", {})
    ):
        raise AcceptanceError(
            "The persisted runtime inventories differ from calibration evidence",
            stage="calibration-input",
        )
    identity = report.get("identity")
    if not isinstance(identity, Mapping):
        raise AcceptanceError("The calibration identity is unavailable", stage="calibration-input")
    corpus_identity = identity.get("corpus")
    if not isinstance(corpus_identity, Mapping) or any(
        corpus_identity.get(name) != expected
        for name, expected in (
            ("calibrationCorpusManifestSha256", calibration_corpus_sha),
            ("calibrationWorkerManifestSha256", calibration_worker_sha),
            ("confirmatoryInputManifestSha256", confirmatory_input_sha),
            ("confirmatoryWorkerManifestSha256", confirmatory_worker_sha),
        )
    ):
        raise AcceptanceError(
            "The prepared corpus hashes differ from the frozen identity",
            stage="calibration-input",
        )
    identity_runtimes = identity.get("runtimeProfiles")
    if not isinstance(identity_runtimes, Mapping) or any(
        not isinstance(identity_runtimes.get(name), Mapping)
        or identity_runtimes[name].get("executableSha256")
        != runtime_values[name].get("executableSha256")
        or identity_runtimes[name].get("inventorySha256")
        != runtime_snapshot[name].get("inventorySha256")
        or identity_runtimes[name].get("pythonVersion") != runtime_values[name].get("pythonVersion")
        for name in ("benchmark", "cpu", "directml")
    ):
        raise AcceptanceError(
            "The runtime artifacts differ from the frozen identity",
            stage="calibration-input",
        )
    return CalibrationContext(
        report=report,
        report_sha256=report_sha256,
        metrics=report["metrics"],
        identity=identity,
        calibration_corpus_manifest=calibration_corpus,
        calibration_worker_manifest=calibration_worker,
        calibration_inventory=calibration_inventory,
        confirmatory_input_manifest=confirmatory_input,
        confirmatory_worker_manifest=confirmatory_worker,
        confirmatory_inventory=confirmatory_inventory,
        calibration_corpus_manifest_sha256=calibration_corpus_sha,
        calibration_worker_manifest_sha256=calibration_worker_sha,
        confirmatory_input_manifest_sha256=confirmatory_input_sha,
        confirmatory_worker_manifest_sha256=confirmatory_worker_sha,
        runtime_inventory_paths=runtime_inventory_paths,
        runtime_inventory_sha256=runtime_inventory_sha256,
    )


def _canonical_attempt_path() -> Path:
    return CONFIRMATORY_ATTEMPT_LEDGER.expanduser().resolve()


def _reject_retired_confirmatory_role() -> None:
    if CONFIRMATORY_RETIREMENT_MARKER.is_file():
        raise AcceptanceError(
            "The CORD v2 train confirmatory role is retired and cannot be run",
            stage="one-shot",
        )


def _start_attempt(
    context: CalibrationContext,
    policy_sha256: str,
    identity: Mapping[str, Any],
) -> AttemptClaim:
    path = _canonical_attempt_path()
    value = {
        "schemaVersion": SCHEMA_VERSION,
        "protocol": PROTOCOL,
        "phase": "confirmatory",
        "datasetId": acceptance_policy.DATASET_ID,
        "datasetRevision": acceptance_policy.DATASET_REVISION,
        "status": "started",
        "startedAtUtc": benchmark_core.utc_timestamp(),
        "calibrationReportSha256": context.report_sha256,
        "policySha256": policy_sha256,
        "selectionManifestSha256": acceptance_policy.SELECTION_MANIFEST_SHA256,
        "confirmatorySelectionSha256": acceptance_policy.SELECTION_ENTRIES_SHA256["confirmatory"],
        "confirmatoryInputManifestSha256": (context.confirmatory_input_manifest_sha256),
        "confirmatoryWorkerManifestSha256": (context.confirmatory_worker_manifest_sha256),
        "trainShardSha256": list(acceptance_policy.TRAIN_SHARD_SHA256),
        "identitySha256": acceptance_policy.sha256_canonical(identity),
        "candidateIdentitySha256": acceptance_policy.sha256_canonical(identity),
    }
    raw = _canonical_bytes(value)
    _atomic_create(path, raw)
    return AttemptClaim(path=path, value=value, sha256=_sha256_bytes(raw))


def _recheck_policy_bytes(path: Path, validated: acceptance_policy.ValidatedPolicy) -> None:
    try:
        value, file_sha256 = acceptance_policy.load_policy(path)
    except acceptance_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="policy-toctou") from exc
    if file_sha256 != validated.file_sha256 or acceptance_policy.canonical_json(
        value
    ) != acceptance_policy.canonical_json(validated.value):
        raise AcceptanceError(
            "The validated policy bytes changed around the one-shot claim",
            stage="policy-toctou",
        )


def _validate_postclaim_policy_result(
    evaluated: Mapping[str, Any],
    validated: acceptance_policy.ValidatedPolicy,
    *,
    quality_passed: bool,
) -> None:
    if (
        evaluated.get("passed") is not quality_passed
        or evaluated.get("policySha256") != validated.file_sha256
    ):
        raise AcceptanceError("The path-bound policy result changed", stage="quality")


STARTED_LEDGER_KEYS = frozenset(
    {
        "calibrationReportSha256",
        "candidateIdentitySha256",
        "confirmatoryInputManifestSha256",
        "confirmatorySelectionSha256",
        "confirmatoryWorkerManifestSha256",
        "datasetId",
        "datasetRevision",
        "identitySha256",
        "phase",
        "policySha256",
        "protocol",
        "schemaVersion",
        "selectionManifestSha256",
        "startedAtUtc",
        "status",
        "trainShardSha256",
    }
)
COMPLETED_LEDGER_KEYS = STARTED_LEDGER_KEYS | {
    "acceptancePassed",
    "claimSha256",
    "completedAtUtc",
    "confirmatoryScoringManifestSha256",
    "integrityPassed",
    "reportSha256",
    "runSucceeded",
}
FAILED_LEDGER_KEYS = STARTED_LEDGER_KEYS | {
    "acceptancePassed",
    "claimSha256",
    "failedAtUtc",
    "failure",
    "integrityPassed",
    "runSucceeded",
}
QUARANTINED_LEDGER_KEYS = FAILED_LEDGER_KEYS | {
    "quarantineMarkerSha256",
    "quarantinedAtUtc",
}

ANNOTATION_BOUNDARY_DIAGNOSTIC_KEYS = frozenset(
    {
        "clippedArea",
        "globalRowIndex",
        "horizontalOvershootPixels",
        "horizontalOvershootRatio",
        "kind",
        "locator",
        "originalArea",
        "outsideVertices",
        "predicate",
        "retainedAreaRatio",
        "sides",
        "verticalOvershootPixels",
        "verticalOvershootRatio",
    }
)


def _safe_failure_diagnostic(value: Any) -> dict[str, Any] | None:
    if not isinstance(value, Mapping):
        return None
    if set(value) == {"globalRowIndex", "kind"}:
        row_index = value.get("globalRowIndex")
        if (
            value.get("kind") == "annotationParse"
            and type(row_index) is int
            and 0
            <= row_index
            < acceptance_policy.CALIBRATION_DOCUMENTS + acceptance_policy.CONFIRMATORY_DOCUMENTS
        ):
            return {"globalRowIndex": row_index, "kind": "annotationParse"}
        return None
    if set(value) != ANNOTATION_BOUNDARY_DIAGNOSTIC_KEYS:
        return None
    row_index = value.get("globalRowIndex")
    locator = value.get("locator")
    predicate = value.get("predicate")
    outside_vertices = value.get("outsideVertices")
    sides = value.get("sides")
    if (
        value.get("kind") != "annotationBoundary"
        or type(row_index) is not int
        or not 0
        <= row_index
        < acceptance_policy.CALIBRATION_DOCUMENTS + acceptance_policy.CONFIRMATORY_DOCUMENTS
        or not isinstance(locator, str)
        or len(locator) > 128
        or not locator.startswith(("valid_line[", "dontcare[", "repeating_symbol["))
        or predicate not in {"edgeOvershoot", "emptyIntersection", "retainedArea"}
        or type(outside_vertices) is not int
        or not 1 <= outside_vertices <= 4
        or not isinstance(sides, list)
        or not sides
        or len(sides) != len(set(sides))
        or any(side not in {"left", "right", "top", "bottom"} for side in sides)
    ):
        return None
    required_numbers = (
        "horizontalOvershootPixels",
        "horizontalOvershootRatio",
        "originalArea",
        "verticalOvershootPixels",
        "verticalOvershootRatio",
    )
    if (
        any(
            isinstance(value.get(name), bool)
            or not isinstance(value.get(name), (int, float))
            or not math.isfinite(float(value[name]))
            or float(value[name]) < 0
            for name in required_numbers
        )
        or float(value["originalArea"]) <= 0
    ):
        return None
    for name in ("clippedArea", "retainedAreaRatio"):
        item = value.get(name)
        if item is not None and (
            isinstance(item, bool)
            or not isinstance(item, (int, float))
            or not math.isfinite(float(item))
            or float(item) < 0
        ):
            return None
    try:
        encoded = acceptance_policy.canonical_json(value)
    except acceptance_policy.PolicyError:
        return None
    if len(encoded.encode("utf-8")) > 4096:
        return None
    return json.loads(encoded)


def _failure_details(error: Exception) -> dict[str, Any]:
    return {
        "backend": getattr(error, "backend", None),
        "diagnostic": _safe_failure_diagnostic(getattr(error, "diagnostic", None)),
        "stage": getattr(error, "stage", "unknown"),
        "type": type(error).__name__,
    }


def _ledger_sha256(value: Any, *, name: str) -> str:
    if not isinstance(value, str) or acceptance_policy.SHA256_PATTERN.fullmatch(value) is None:
        raise AcceptanceError(f"The ledger {name} is invalid", stage="one-shot")
    return value


def _report_claim_bindings(report: Mapping[str, Any]) -> dict[str, str]:
    evidence = report.get("evidence")
    policy_evidence = evidence.get("policy") if isinstance(evidence, Mapping) else None
    identity = report.get("identity")
    corpus_identity = identity.get("corpus") if isinstance(identity, Mapping) else None
    if not isinstance(policy_evidence, Mapping) or not isinstance(corpus_identity, Mapping):
        raise AcceptanceError(
            "The report does not expose immutable claim bindings", stage="one-shot"
        )
    return {
        "calibrationReportSha256": _ledger_sha256(
            policy_evidence.get("calibrationReportSha256"), name="calibration report SHA-256"
        ),
        "confirmatoryInputManifestSha256": _ledger_sha256(
            corpus_identity.get("confirmatoryInputManifestSha256"),
            name="confirmatory input manifest SHA-256",
        ),
        "confirmatoryWorkerManifestSha256": _ledger_sha256(
            corpus_identity.get("confirmatoryWorkerManifestSha256"),
            name="confirmatory worker manifest SHA-256",
        ),
        "identitySha256": acceptance_policy.sha256_canonical(identity),
        "policySha256": _ledger_sha256(policy_evidence.get("policySha256"), name="policy SHA-256"),
    }


def _validate_ledger_claim(
    value: Mapping[str, Any],
    report: Mapping[str, Any],
    *,
    completed: bool,
    report_sha256: str | None = None,
    claim_sha256: str | None = None,
) -> None:
    expected_keys = COMPLETED_LEDGER_KEYS if completed else STARTED_LEDGER_KEYS
    expected_status = "completed" if completed else "started"
    bindings = _report_claim_bindings(report)
    if (
        set(value) != expected_keys
        or type(value.get("schemaVersion")) is not int
        or value.get("schemaVersion") != SCHEMA_VERSION
        or value.get("protocol") != PROTOCOL
        or value.get("phase") != "confirmatory"
        or value.get("status") != expected_status
        or value.get("datasetId") != acceptance_policy.DATASET_ID
        or value.get("datasetRevision") != acceptance_policy.DATASET_REVISION
        or value.get("selectionManifestSha256") != acceptance_policy.SELECTION_MANIFEST_SHA256
        or value.get("confirmatorySelectionSha256")
        != acceptance_policy.SELECTION_ENTRIES_SHA256["confirmatory"]
        or value.get("trainShardSha256") != list(acceptance_policy.TRAIN_SHARD_SHA256)
        or report.get("evaluationRole") != "confirmatory"
        or report.get("selectionSha256")
        != acceptance_policy.SELECTION_ENTRIES_SHA256["confirmatory"]
        or any(value.get(key) != expected for key, expected in bindings.items())
        or value.get("candidateIdentitySha256") != bindings["identitySha256"]
    ):
        raise AcceptanceError("The immutable confirmatory ledger claim changed", stage="one-shot")
    try:
        acceptance_policy.validate_utc_timestamp(value.get("startedAtUtc"))
    except acceptance_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="one-shot") from exc
    for key in (
        "calibrationReportSha256",
        "candidateIdentitySha256",
        "confirmatoryInputManifestSha256",
        "confirmatorySelectionSha256",
        "confirmatoryWorkerManifestSha256",
        "identitySha256",
        "policySha256",
        "selectionManifestSha256",
    ):
        _ledger_sha256(value.get(key), name=key)
    if completed:
        evidence = report.get("evidence")
        artifacts = evidence.get("artifacts") if isinstance(evidence, Mapping) else None
        scoring = (
            artifacts.get("confirmatoryCorpusManifest") if isinstance(artifacts, Mapping) else None
        )
        expected_scoring_sha256 = scoring.get("sha256") if isinstance(scoring, Mapping) else None
        try:
            acceptance_policy.validate_utc_timestamp(value.get("completedAtUtc"))
        except acceptance_policy.PolicyError as exc:
            raise AcceptanceError(str(exc), stage="one-shot") from exc
        original_claim = {key: value.get(key) for key in STARTED_LEDGER_KEYS}
        original_claim["status"] = "started"
        computed_claim_sha256 = _sha256_bytes(_canonical_bytes(original_claim))
        if (
            report_sha256 is None
            or claim_sha256 is None
            or value.get("claimSha256") != claim_sha256
            or computed_claim_sha256 != claim_sha256
            or value.get("reportSha256") != report_sha256
            or value.get("runSucceeded") is not report.get("runSucceeded")
            or value.get("integrityPassed") is not report.get("integrityPassed")
            or value.get("acceptancePassed") is not report.get("acceptancePassed")
            or value.get("confirmatoryScoringManifestSha256") != expected_scoring_sha256
            or type(value.get("runSucceeded")) is not bool
            or type(value.get("integrityPassed")) is not bool
            or type(value.get("acceptancePassed")) is not bool
        ):
            raise AcceptanceError("The completed ledger outcome changed", stage="one-shot")
        _ledger_sha256(value.get("reportSha256"), name="report SHA-256")
        _ledger_sha256(value.get("claimSha256"), name="claim SHA-256")
        _ledger_sha256(
            value.get("confirmatoryScoringManifestSha256"),
            name="confirmatory scoring manifest SHA-256",
        )


def _load_original_claim(claim: AttemptClaim) -> dict[str, Any]:
    value, file_sha256 = _strict_json(
        claim.path, maximum_bytes=2 * 1024 * 1024, name="attempt manifest"
    )
    if file_sha256 != claim.sha256 or acceptance_policy.canonical_json(
        value
    ) != acceptance_policy.canonical_json(claim.value):
        raise AcceptanceError("The original immutable attempt claim changed", stage="one-shot")
    return value


def _finish_attempt(claim: AttemptClaim, *, report_sha256: str, report: Mapping[str, Any]) -> None:
    value = _load_original_claim(claim)
    _validate_ledger_claim(value, report, completed=False)
    evidence = report.get("evidence")
    artifacts = evidence.get("artifacts") if isinstance(evidence, Mapping) else None
    scoring_manifest = (
        artifacts.get("confirmatoryCorpusManifest") if isinstance(artifacts, Mapping) else None
    )
    scoring_sha256 = (
        scoring_manifest.get("sha256") if isinstance(scoring_manifest, Mapping) else None
    )
    if not isinstance(scoring_sha256, str) or len(scoring_sha256) != 64:
        raise AcceptanceError(
            "The confirmatory scoring manifest is not bound to the report",
            stage="one-shot",
        )
    value.update(
        {
            "status": "completed",
            "claimSha256": claim.sha256,
            "completedAtUtc": benchmark_core.utc_timestamp(),
            "reportSha256": report_sha256,
            "confirmatoryScoringManifestSha256": scoring_sha256,
            "runSucceeded": report["runSucceeded"],
            "integrityPassed": report["integrityPassed"],
            "acceptancePassed": report["acceptancePassed"],
        }
    )
    _validate_ledger_claim(
        value,
        report,
        completed=True,
        report_sha256=report_sha256,
        claim_sha256=claim.sha256,
    )
    _atomic_replace(claim.path, _canonical_bytes(value))


def _validate_failed_ledger(
    value: Mapping[str, Any], claim: AttemptClaim, *, quarantined: bool
) -> None:
    expected_keys = QUARANTINED_LEDGER_KEYS if quarantined else FAILED_LEDGER_KEYS
    expected_status = "quarantined" if quarantined else "failed"
    original = {key: value.get(key) for key in STARTED_LEDGER_KEYS}
    original["status"] = "started"
    failure = value.get("failure")
    if (
        set(value) != expected_keys
        or value.get("status") != expected_status
        or acceptance_policy.canonical_json(original)
        != acceptance_policy.canonical_json(claim.value)
        or value.get("claimSha256") != claim.sha256
        or _sha256_bytes(_canonical_bytes(original)) != claim.sha256
        or value.get("runSucceeded") is not False
        or value.get("integrityPassed") is not False
        or value.get("acceptancePassed") is not False
        or not isinstance(failure, Mapping)
        or set(failure) != {"backend", "diagnostic", "stage", "type"}
        or failure.get("backend") is not None
        and not isinstance(failure.get("backend"), str)
        or not isinstance(failure.get("stage"), str)
        or not isinstance(failure.get("type"), str)
        or failure.get("diagnostic") is not None
        and _safe_failure_diagnostic(failure.get("diagnostic")) != failure.get("diagnostic")
    ):
        raise AcceptanceError("The failed ledger schema changed", stage="one-shot")
    try:
        acceptance_policy.validate_utc_timestamp(value.get("failedAtUtc"))
        if quarantined:
            acceptance_policy.validate_utc_timestamp(value.get("quarantinedAtUtc"))
    except acceptance_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="one-shot") from exc
    _ledger_sha256(value.get("claimSha256"), name="claim SHA-256")
    if quarantined:
        _ledger_sha256(value.get("quarantineMarkerSha256"), name="quarantine marker SHA-256")


def _fail_attempt(claim: AttemptClaim, error: Exception) -> None:
    try:
        value = _load_original_claim(claim)
    except Exception:
        # The in-memory canonical claim is the trusted recovery source.
        value = json.loads(acceptance_policy.canonical_json(claim.value))
    value.update(
        {
            "status": "failed",
            "claimSha256": claim.sha256,
            "failedAtUtc": benchmark_core.utc_timestamp(),
            "runSucceeded": False,
            "integrityPassed": False,
            "acceptancePassed": False,
            "failure": _failure_details(error),
        }
    )
    _validate_failed_ledger(value, claim, quarantined=False)
    _atomic_replace(claim.path, _canonical_bytes(value))


def _quarantine_confirmatory_partials(
    args: argparse.Namespace, claim: AttemptClaim, error: Exception
) -> Path | None:
    phase_root = args.work_directory.expanduser().resolve() / "confirmatory"
    if not phase_root.is_dir() or phase_root.is_symlink():
        return None
    value = {
        "attemptLedgerName": claim.path.name,
        "failedAtUtc": benchmark_core.utc_timestamp(),
        "failure": _failure_details(error),
        "schemaVersion": SCHEMA_VERSION,
        "status": "quarantined",
    }
    global_marker = claim.path.with_name(claim.path.name + ".quarantined.json")
    if not global_marker.exists():
        _atomic_create(global_marker, _canonical_bytes(value))
    phase_marker = phase_root / "CONFIRMATORY_EVIDENCE_QUARANTINED.json"
    if not phase_marker.exists():
        _atomic_create(phase_marker, _canonical_bytes(value))
    ledger, _ = _strict_json(claim.path, maximum_bytes=2 * 1024 * 1024, name="attempt manifest")
    _validate_failed_ledger(ledger, claim, quarantined=False)
    ledger.update(
        {
            "quarantineMarkerSha256": benchmark_core.sha256_file(global_marker),
            "quarantinedAtUtc": benchmark_core.utc_timestamp(),
            "status": "quarantined",
        }
    )
    _validate_failed_ledger(ledger, claim, quarantined=True)
    _atomic_replace(claim.path, _canonical_bytes(ledger))
    return phase_marker


def verify_completed_confirmation(report_path: Path, attempt_path: Path) -> dict[str, Any]:
    report, report_sha256 = _strict_json(
        report_path, maximum_bytes=MAX_REPORT_BYTES, name="confirmatory report"
    )
    ledger, _ = _strict_json(attempt_path, maximum_bytes=2 * 1024 * 1024, name="attempt manifest")
    _validate_ledger_claim(
        ledger,
        report,
        completed=True,
        report_sha256=report_sha256,
        claim_sha256=ledger.get("claimSha256"),
    )
    evidence = report.get("evidence")
    artifacts = evidence.get("artifacts") if isinstance(evidence, Mapping) else None
    scoring = (
        artifacts.get("confirmatoryCorpusManifest") if isinstance(artifacts, Mapping) else None
    )
    scoring_sha256 = scoring.get("sha256") if isinstance(scoring, Mapping) else None
    output = report_path.expanduser().resolve()
    if (
        ledger.get("status") != "completed"
        or ledger.get("reportSha256") != report_sha256
        or ledger.get("confirmatoryScoringManifestSha256") != scoring_sha256
        or ledger.get("identitySha256")
        != acceptance_policy.sha256_canonical(report.get("identity", {}))
        or ledger.get("runSucceeded") is not report.get("runSucceeded")
        or ledger.get("integrityPassed") is not report.get("integrityPassed")
        or ledger.get("acceptancePassed") is not report.get("acceptancePassed")
        or report.get("evaluationRole") != "confirmatory"
        or output.with_name(output.name + ".incomplete").exists()
        or output.with_name(output.name + ".staged").exists()
        or attempt_path.with_name(attempt_path.name + ".quarantined.json").exists()
    ):
        raise AcceptanceError(
            "The completed ledger does not bind the final confirmatory report",
            stage="one-shot-consumer",
        )
    return report


def execute_confirmatory(
    args: argparse.Namespace,
    frozen: FrozenInputs,
    context: CalibrationContext,
) -> tuple[dict[str, Any], AttemptClaim]:
    phase_root = args.work_directory.expanduser().resolve() / "confirmatory"
    if phase_root.exists():
        raise AcceptanceError("The confirmatory work directory already exists", stage="one-shot")
    role = frozen.selections["confirmatory"]
    preclaim_artifacts = {
        "calibration": (
            context.calibration_corpus_manifest,
            context.calibration_worker_manifest,
            context.calibration_inventory,
            frozen.selections["calibration"],
        ),
        "confirmatoryInput": (
            context.confirmatory_input_manifest,
            context.confirmatory_worker_manifest,
            context.confirmatory_inventory,
            role,
        ),
    }
    preclaim_snapshot = _prepared_snapshot(preclaim_artifacts)
    identity_before, sources_before = build_live_identity(
        frozen,
        calibration_corpus_manifest_sha256=preclaim_snapshot["calibration"]["manifestSha256"],
        calibration_worker_manifest_sha256=preclaim_snapshot["calibration"]["workerManifestSha256"],
        confirmatory_input_manifest_sha256=preclaim_snapshot["confirmatoryInput"]["manifestSha256"],
        confirmatory_worker_manifest_sha256=preclaim_snapshot["confirmatoryInput"][
            "workerManifestSha256"
        ],
    )
    if acceptance_policy.canonical_json(identity_before) != acceptance_policy.canonical_json(
        context.identity
    ):
        raise AcceptanceError(
            "The live identity differs from the calibration identity", stage="frozen-identity"
        )
    calibration_evidence = context.report.get("evidence")
    calibration_sources = (
        calibration_evidence.get("identitySources")
        if isinstance(calibration_evidence, Mapping)
        else None
    )
    if not isinstance(calibration_sources, Mapping) or acceptance_policy.canonical_json(
        calibration_sources
    ) != acceptance_policy.canonical_json(sources_before):
        raise AcceptanceError(
            "The scorer dependency source inventory changed after calibration",
            stage="frozen-identity",
        )
    calibration_runtime_profiles = (
        calibration_evidence.get("runtimeProfiles")
        if isinstance(calibration_evidence, Mapping)
        else None
    )
    runtime_snapshot_before = _runtime_inventory_snapshot(
        context.runtime_inventory_paths, frozen.runtimes
    )
    if (
        not isinstance(calibration_runtime_profiles, Mapping)
        or acceptance_policy.canonical_json(calibration_runtime_profiles)
        != acceptance_policy.canonical_json(runtime_snapshot_before)
        or any(
            runtime_snapshot_before[name].get("artifactSha256")
            != context.runtime_inventory_sha256.get(name)
            for name in ("benchmark", "cpu", "directml")
        )
    ):
        raise AcceptanceError(
            "The canonical benchmark/CPU/DirectML runtime inventories changed",
            stage="runtime-identity",
        )
    try:
        validated_policy = acceptance_policy.validate_policy(
            args.policy,
            expected_calibration_selection=frozen.selections["calibration"],
            expected_calibration_report_path=args.calibration_report,
            expected_identity=identity_before,
        )
    except acceptance_policy.PolicyError as exc:
        raise AcceptanceError(str(exc), stage="policy") from exc

    _recheck_policy_bytes(args.policy, validated_policy)
    attempt = _start_attempt(context, validated_policy.file_sha256, identity_before)
    args._attempt_claim = attempt
    _recheck_policy_bytes(args.policy, validated_policy)
    corpus = extract_role_corpus(
        frozen.shards,
        role,
        phase_root / "scoring-corpus",
    )
    opaque_rows = _strict_jsonl(
        context.confirmatory_input_manifest, name="confirmatory input manifest"
    )
    if [
        {"imageSha256": row.get("sha256"), "rowIndex": row.get("rowIndex")} for row in opaque_rows
    ] != [
        {"imageSha256": document.sha256, "rowIndex": document.row_index}
        for document in corpus.documents
    ]:
        raise AcceptanceError(
            "The confirmatory scoring corpus differs from the opaque input selection",
            stage="corpus-identity",
        )
    verify_train_shards(frozen.shards)
    acceptance_policy.load_role_selections(frozen.selection_manifest)
    determinism_corpus = _create_determinism_corpus(corpus, role, phase_root / "views")
    prepared_artifacts = {
        "calibration": (
            context.calibration_corpus_manifest,
            context.calibration_worker_manifest,
            context.calibration_inventory,
            frozen.selections["calibration"],
        ),
        "confirmatoryInput": (
            context.confirmatory_input_manifest,
            context.confirmatory_worker_manifest,
            context.confirmatory_inventory,
            role,
        ),
        "confirmatoryScoring": (
            corpus.corpus_manifest,
            corpus.worker_manifest,
            corpus.inventory,
            role,
        ),
    }
    snapshot_before = _prepared_snapshot(prepared_artifacts)
    backends = (
        frozen.cpu_backend,
        frozen.directml_backend,
        benchmark_core.Backend("hybrid", frozen.directml_backend.python_executable),
    )
    runs = [
        _benchmark_backend(
            frozen,
            corpus,
            determinism_corpus,
            backend,
            phase_root / "results" / backend.requested_provider,
            args.timeout_seconds,
        )
        for backend in backends
    ]
    recomputation = {
        run["requestedProvider"]: validate_micro_recomputation(run["metrics"]) for run in runs
    }
    evaluations = {}
    for run in runs:
        try:
            measurements = acceptance_policy.extract_measurements(run["metrics"], selection=role)
            evaluations[run["requestedProvider"]] = {
                "measurements": measurements,
                **acceptance_policy.evaluate_thresholds(
                    measurements, validated_policy.value["thresholds"]
                ),
            }
        except acceptance_policy.PolicyError as exc:
            raise AcceptanceError(str(exc), stage="quality") from exc
    identity_stable, identity_reverification = _reverify_identity(
        args,
        identity_before,
        sources_before,
        prepared_artifacts=prepared_artifacts,
        snapshot_before=snapshot_before,
        runtime_inventory_paths=context.runtime_inventory_paths,
        runtime_snapshot_before=runtime_snapshot_before,
    )
    critical_hashes = {run["criticalEvidenceSha256"] for run in runs}
    metric_hashes = {run["metricsSha256"] for run in runs}
    per_document_metric_hashes = {
        run.get("qualityRun", {}).get("perDocumentMetricsSha256") for run in runs
    }
    confidence = cord.confidence_parity(
        [run.get("_confidenceByCriticalRecord", {}) for run in runs]
    )
    determinism_checks = {
        run["requestedProvider"]: _determinism_checks(run, determinism_corpus) for run in runs
    }
    integrity_checks = {
        "allBackendProvenancePassed": all(run.get("provenancePassed") for run in runs),
        "criticalEvidenceEqual": len(critical_hashes) == 1,
        "metricsEqual": len(metric_hashes) == 1,
        "perDocumentMetricsEqual": (
            None not in per_document_metric_hashes and len(per_document_metric_hashes) == 1
        ),
        "allBackendDeterminismPassed": all(
            all(checks.values()) for checks in determinism_checks.values()
        ),
        "confidenceParityPassed": confidence.get("passed") is True,
        "identityStableDuringRun": identity_stable,
        "requestedProvidersExact": [run.get("requestedProvider") for run in runs]
        == ["cpu", "directml", "hybrid"],
        "resolvedProvidersExact": [run.get("resolvedProvider") for run in runs]
        == ["cpu", "directml", "hybrid-directml-cpu"],
        "hybridBothLanesProducedRecords": (
            runs[2].get("hybridLaneRecordCoverage", {}).get("bothLanesProducedRecords") is True
        ),
        "runtimeHashesMatch": (
            runs[0].get("runtimeSha256") == frozen.runtimes["cpu"].get("executableSha256")
            and all(
                run.get("runtimeSha256") == frozen.runtimes["directml"].get("executableSha256")
                for run in runs[1:]
            )
        ),
        "workerHashesMatch": all(run.get("workerSha256") == frozen.worker_sha256 for run in runs),
        "qualityDocumentCountsExact": all(
            run.get("qualityRows") == len(role.entries) for run in runs
        ),
    }
    output_integrity = all(integrity_checks.values())
    quality_passed = all(evaluation.get("passed") is True for evaluation in evaluations.values())
    generated_at = benchmark_core.utc_timestamp()
    evidence = {
        "artifacts": {
            "confirmatoryCorpusManifest": _artifact(
                corpus.corpus_manifest, evidence_root=phase_root
            ),
            "confirmatoryWorkerManifest": _artifact(
                corpus.worker_manifest, evidence_root=phase_root
            ),
            "confirmatoryInventory": _artifact(corpus.inventory, evidence_root=phase_root),
        },
        "backends": [_public_run(run) for run in runs],
        "benchmark": "CORD-v2-train-OCR-acceptance",
        "claimScope": "frozen 200-row CORD v2 train confirmatory role",
        "crossBackendIntegrity": {
            "checks": integrity_checks,
            "confidence": confidence,
            "criticalEvidenceSha256": next(iter(critical_hashes), ""),
            "metricsSha256": next(iter(metric_hashes), ""),
        },
        "determinism": {
            "checksByBackend": determinism_checks,
            "repetitions": DETERMINISM_REPETITIONS,
            "rowIndices": [document.row_index for document in determinism_corpus.documents],
            "selectionSha256": determinism_corpus.selection_sha256,
        },
        "execution": {
            "backendOrder": ["cpu", "directml", "hybrid"],
            "determinismRepetitionsPerBackend": DETERMINISM_REPETITIONS,
            "oneShotAttemptLedger": attempt.path.name,
            "outerRuntimeIsolation": _outer_isolation_evidence(),
            "qualityRunsPerBackend": 1,
            "threads": 0,
        },
        "generatedAtUtc": generated_at,
        "identityReverification": identity_reverification,
        "identitySources": sources_before,
        "microMetricRecomputation": recomputation,
        "phase": "confirmatory",
        "policy": {
            "calibrationReportSha256": context.report_sha256,
            "policyId": acceptance_policy.POLICY_ID,
            "policySha256": validated_policy.file_sha256,
            "evaluations": evaluations,
        },
        "protocol": PROTOCOL,
        "qualityPolicyPassed": quality_passed,
        "runtimeProfiles": runtime_snapshot_before,
        "selection": {
            "entriesSha256": role.entries_sha256,
            "manifestSha256": role.manifest_sha256,
            "role": role.role,
        },
        "thresholdOverridesPermitted": False,
    }
    report = _metrics_report(
        role=role,
        identity=identity_before,
        metrics=runs[0]["metrics"],
        integrity_passed=output_integrity,
        acceptance_passed=output_integrity and quality_passed,
        evidence=evidence,
    )
    if output_integrity:
        staged_report = _stage_report(phase_root / "confirmatory-policy-input.json", report)
        try:
            evaluated = acceptance_policy.evaluate_confirmatory(
                args.policy,
                staged_report,
                expected_calibration_report_path=args.calibration_report,
                expected_calibration_selection=frozen.selections["calibration"],
                expected_identity=identity_before,
                selection=role,
            )
        except acceptance_policy.PolicyError as exc:
            raise AcceptanceError(str(exc), stage="quality") from exc
        finally:
            staged_report.unlink(missing_ok=True)
        _validate_postclaim_policy_result(
            evaluated, validated_policy, quality_passed=quality_passed
        )
    _recheck_policy_bytes(args.policy, validated_policy)
    return report, attempt


def _commit_confirmatory_report(
    output: Path,
    marker: Path,
    report: dict[str, Any],
    attempt: AttemptClaim,
) -> str:
    staged, report_sha256 = _stage_final_report(output, marker, report)
    try:
        _finish_attempt(attempt, report_sha256=report_sha256, report=report)
    except Exception:
        staged.unlink(missing_ok=True)
        raise
    try:
        _finalize_staged_report(output, marker, staged)
        verify_completed_confirmation(output, attempt.path)
    except Exception:
        output.unlink(missing_ok=True)
        staged.unlink(missing_ok=True)
        raise
    return report_sha256


def _failure_report(args: argparse.Namespace, error: Exception) -> dict[str, Any]:
    role = args.phase
    documents = (
        acceptance_policy.CALIBRATION_DOCUMENTS
        if role == "calibration"
        else acceptance_policy.CONFIRMATORY_DOCUMENTS
    )
    metrics: dict[str, Any] = {}
    return {
        "acceptancePassed": False,
        "documents": documents,
        "evaluationRole": role,
        "evidence": {
            "benchmark": "CORD-v2-train-OCR-acceptance",
            "failure": _failure_details(error),
            "generatedAtUtc": benchmark_core.utc_timestamp(),
            "phase": role,
            "protocol": PROTOCOL,
            "qualityPolicyPassed": None,
            "thresholdOverridesPermitted": False,
        },
        "identity": {},
        "integrityPassed": False,
        "metrics": metrics,
        "metricsSha256": acceptance_policy.sha256_canonical(metrics),
        "runSucceeded": False,
        "schemaVersion": acceptance_policy.METRICS_REPORT_SCHEMA_VERSION,
        "selectionSha256": acceptance_policy.SELECTION_ENTRIES_SHA256[role],
    }


def run(args: argparse.Namespace) -> dict[str, Any]:
    _require_outer_runtime_isolation()
    attempt_path = _canonical_attempt_path() if args.phase == "confirmatory" else None
    attempt_existed_before = attempt_path.exists() if attempt_path is not None else False
    if args.phase == "calibration":
        policy_output = args.policy_output.expanduser().resolve()
        policy_marker = policy_output.with_name(policy_output.name + ".incomplete")
        if policy_output.exists() or policy_marker.exists():
            raise AcceptanceError("The policy output or marker already exists", stage="arguments")
    output, marker = _new_report_marker(args.output)
    try:
        frozen = verify_frozen_inputs(args)
        if args.phase == "calibration":
            report, policy_value = execute_calibration(args, frozen)
            if policy_value is not None:
                acceptance_policy.publish_policy(args.policy_output, policy_value)
            _publish_report(output, marker, report)
            return report

        context = load_calibration_context(args.calibration_report, args.calibration_evidence_root)
        report, attempt = execute_confirmatory(args, frozen, context)
        if attempt.path != attempt_path:
            raise AcceptanceError("The canonical attempt ledger path changed", stage="one-shot")
        _commit_confirmatory_report(
            output,
            marker,
            report,
            attempt,
        )
        return report
    except Exception as exc:
        attempt_claim = getattr(args, "_attempt_claim", None)
        if isinstance(attempt_claim, AttemptClaim) and not attempt_existed_before:
            _fail_attempt(attempt_claim, exc)
            with suppress(Exception):
                _quarantine_confirmatory_partials(args, attempt_claim, exc)
        args.output.expanduser().resolve().with_name(
            args.output.expanduser().resolve().name + ".staged"
        ).unlink(missing_ok=True)
        if marker.is_file():
            with suppress(Exception):
                _publish_report(output, marker, _failure_report(args, exc))
        raise


def _argument_path(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else (_INVOCATION_CWD / path)


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="phase", required=True)

    def common(subparser: argparse.ArgumentParser) -> None:
        subparser.add_argument("--parquet", action="append", type=_argument_path, required=True)
        subparser.add_argument("--selection-manifest", type=_argument_path, required=True)
        subparser.add_argument("--worker", type=_argument_path, required=True)
        subparser.add_argument("--model-pack", type=_argument_path, required=True)
        subparser.add_argument("--cpu-python", type=_argument_path, required=True)
        subparser.add_argument("--directml-python", type=_argument_path, required=True)
        subparser.add_argument("--output", type=_argument_path, required=True)
        subparser.add_argument("--work-directory", type=_argument_path, required=True)
        subparser.add_argument("--timeout-seconds", type=float, default=7200.0)

    calibration = subparsers.add_parser("calibration")
    common(calibration)
    calibration.add_argument("--policy-output", type=_argument_path, required=True)

    confirmatory = subparsers.add_parser("confirmatory")
    common(confirmatory)
    confirmatory.add_argument("--calibration-report", type=_argument_path, required=True)
    confirmatory.add_argument("--calibration-evidence-root", type=_argument_path, required=True)
    confirmatory.add_argument("--policy", type=_argument_path, required=True)
    verifier = subparsers.add_parser("verify-completed")
    verifier.add_argument("--report", type=_argument_path, required=True)
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    try:
        args = parse_arguments(argv)
        if args.phase == "confirmatory":
            _reject_retired_confirmatory_role()
        if args.phase == "verify-completed":
            _require_outer_runtime_isolation()
            report = verify_completed_confirmation(args.report, _canonical_attempt_path())
        else:
            report = run(args)
    except (
        AcceptanceError,
        acceptance_policy.PolicyError,
        benchmark_core.BenchmarkError,
        selection_builder.SelectionError,
        OSError,
        ValueError,
    ) as exc:
        print(f"OCR acceptance failed: {exc}", file=sys.stderr)
        return 1
    print(acceptance_policy.canonical_json(report))
    return (
        0
        if report["runSucceeded"] is True
        and report["integrityPassed"] is True
        and report["acceptancePassed"] is True
        else 2
    )


if __name__ == "__main__":
    raise SystemExit(main())
