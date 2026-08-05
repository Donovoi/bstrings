#!/usr/bin/env python3
"""Run a repaired SROIE holdout diagnostic after the one-shot test was consumed.

This is deliberately separate from ``benchmark_ocr_sroie_acceptance.py``.  It
cannot create, reset, recover, or complete the confirmatory ledger and it never
reports an acceptance decision.  The input is a preserved copy of the already
consumed test artifact.  Results are development diagnostics only.
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
        "SROIE post-hoc diagnostics must be launched with the benchmark Python and -I -B",
        file=sys.stderr,
    )
    raise SystemExit(1)

import argparse
import atexit
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
from collections.abc import Mapping, Sequence
from contextlib import suppress
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Any

POSTHOC_SCHEMA_VERSION = 1
POSTHOC_PROTOCOL = "bstrings-ICDAR2019-SROIE-repaired-holdout-posthoc-v1"
EXPECTED_SROIE_ADAPTER_PROTOCOL = "bstrings-ICDAR2019-SROIE-parquet-adapter-v3"
ACKNOWLEDGEMENT = "POST_HOC_DIAGNOSTIC_ONLY_NOT_INDEPENDENT_ACCEPTANCE"
ORIGINAL_TERMINAL_RESULT_SHA256 = "10d13e0e93c13c31be6515953cc8563bd576f71537200f642e08fbd050d099b3"
ORIGINAL_TERMINAL_RESULT_ASSET = "sroie-ocr-terminal-result-v2.json"
ORIGINAL_TERMINAL_RELEASE_TAG = "ocr-sroie-terminal-v2-20260805-23992fc"
ORIGINAL_CANDIDATE_COMMIT = "23992fc75b624a3c6dab5bfbd0a4b52949133525"
ORIGINAL_LEDGER_SHA256 = "54b46a2b7cdf77b25cdf2af49cbd5bc86057fd4c7c179887bf447801cc6888f4"
ORIGINAL_QUARANTINE_MARKER_SHA256 = (
    "36118acc204f0e1fbc382c32e059aad88daa67129ea391a4abd4cfe536f790b4"
)
EXPECTED_REPAIRED_TEST_REGIONS = 1
EXPECTED_REPAIR_IDENTITY = {
    "bboxRepairAuditSha256": "56eb3c67428076fc47a8a7a59d08a62496a25ee7196e8f8dd6e7c1f433849c23",
    "bboxRepairRecordsSha256": "a57d5de2e9fe2e72b5636a43e2944ea112f3bd7f428193f9979a584c1eb2ffe4",
    "repairedRegionCount": 1,
    "scoringAnnotationIdentitiesSha256": (
        "35f01b789d41e403d90b32bda509662f96fe275c7d388f6c6e2e6375c0431505"
    ),
    "sourcePayloadIdentitiesSha256": (
        "630a307e8e57beb87a541c0b3870375d1517b032916190ae7607a914ce1ca422"
    ),
    "sourceRegionCount": 19_386,
}
EXPECTED_REPAIR_IDENTITY_SHA256 = (
    "8a2c74537cd0e42d5a671ea52d7b902ccc29a5b389513867c2bd3f1b132357d4"
)
PINNED_GIT_EXE_SHA256 = "7b7971dd13f0c3a284e538601f2f9770b3a87dfaccb5fb52d68141c67ed22364"
GIT_TIMEOUT_SECONDS = 30.0
MAX_GIT_OUTPUT_BYTES = 64 * 1024
MAX_TERMINAL_RESULT_BYTES = 2 * 1024 * 1024
MAX_LEDGER_BYTES = 2 * 1024 * 1024
_INVOCATION_CWD = Path.cwd().absolute()
_SOURCE_FILE = (
    Path(__file__) if Path(__file__).is_absolute() else _INVOCATION_CWD / Path(__file__)
).absolute()
_SOURCE_ROOT = _SOURCE_FILE.parent
_REPOSITORY_ROOT = _SOURCE_FILE.parents[2]
_BOOTSTRAP_CWD_OWNER: tempfile.TemporaryDirectory[str] | None = None
_BOOTSTRAP_DLL_HANDLES: list[Any] = []
_TRUSTED_SOURCE_HASHES: dict[str, str] = {}
_TRUSTED_BOOTSTRAPPED = False


def _bootstrap_temporary_directory(
    trusted_root: Path,
) -> tuple[tempfile.TemporaryDirectory[str], Path]:
    """Create the bootstrap sandbox below an explicitly trusted directory."""

    root = trusted_root.resolve()
    try:
        parent_ids: list[tuple[Path, tuple[int | None, int | None]]] = []
        for parent in reversed((root, *root.parents)):
            value = os.lstat(parent)
            attributes = getattr(value, "st_file_attributes", 0)
            if (
                not stat.S_ISDIR(value.st_mode)
                or stat.S_ISLNK(value.st_mode)
                or attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
            ):
                raise RuntimeError("The bootstrap temporary root is unsafe")
            parent_ids.append((parent, (value.st_dev, value.st_ino)))
        temporary = tempfile.TemporaryDirectory(
            prefix="bstrings-sroie-posthoc-", dir=str(root)
        )
        controlled = Path(temporary.name).resolve()
        value = os.lstat(controlled)
        if (
            controlled.parent != root
            or not stat.S_ISDIR(value.st_mode)
            or stat.S_ISLNK(value.st_mode)
            or getattr(value, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
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
            temporary.cleanup()
            raise RuntimeError("The bootstrap temporary directory is unsafe")
    except OSError as exc:
        raise RuntimeError("The bootstrap temporary root is unsafe") from exc
    return temporary, controlled


def _cleanup_bootstrap_sandbox() -> None:
    if _BOOTSTRAP_CWD_OWNER is None:
        return
    with suppress(OSError):
        os.chdir(_SOURCE_ROOT)
    _BOOTSTRAP_CWD_OWNER.cleanup()


def _sanitize_bootstrap_environment() -> None:
    global _BOOTSTRAP_CWD_OWNER
    retained: dict[str, str] = {}
    windows_directory: Path | None = None
    temporary_root = Path("/tmp")
    kernel32: Any = None
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
        class _Guid(ctypes.Structure):
            _fields_ = (
                ("data1", ctypes.c_uint32),
                ("data2", ctypes.c_uint16),
                ("data3", ctypes.c_uint16),
                ("data4", ctypes.c_ubyte * 8),
            )

        program_data_guid = _Guid(
            0x62AB5D82,
            0xFDC1,
            0x4DC3,
            (ctypes.c_ubyte * 8)(0xA9, 0xDD, 0x07, 0x0D, 0x1D, 0x49, 0x5D, 0x97),
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
        local_app_data_guid = _Guid(
            0xF1B32785,
            0x6FBA,
            0x4FCF,
            (ctypes.c_ubyte * 8)(0x9D, 0x55, 0x7B, 0x8E, 0x7F, 0x15, 0x70, 0x91),
        )

        def known_folder_path(identifier: _Guid, name: str) -> Path:
            output = ctypes.c_wchar_p()
            result = known_folder(ctypes.byref(identifier), 0, None, ctypes.byref(output))
            if result != 0 or not output.value:
                raise RuntimeError(f"The canonical {name} folder is unavailable")
            try:
                return Path(output.value).resolve()
            finally:
                release(ctypes.cast(output, ctypes.c_void_p))

        program_data = known_folder_path(program_data_guid, "machine ProgramData")
        temporary_root = known_folder_path(local_app_data_guid, "user LocalAppData") / "Temp"
        retained.update(
            {
                "ALLUSERSPROFILE": str(program_data),
                "PATH": str((windows_directory / "System32").resolve()),
                "ProgramData": str(program_data),
                "SystemRoot": str(windows_directory),
                "WINDIR": str(windows_directory),
            }
        )
    else:
        retained["PATH"] = "/usr/bin:/bin"
    retained.update(
        {
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
    )
    os.environ.clear()
    os.environ.update(retained)
    temporary, controlled_cwd = _bootstrap_temporary_directory(temporary_root)
    os.environ["TEMP"] = str(controlled_cwd)
    os.environ["TMP"] = str(controlled_cwd)
    os.chdir(controlled_cwd)
    if os.name == "nt":
        set_default = kernel32.SetDefaultDllDirectories
        set_default.argtypes = [ctypes.c_uint32]
        set_default.restype = ctypes.c_int
        if not set_default(0x00001000):
            raise RuntimeError("The safe Windows DLL search policy could not be enabled")
        _BOOTSTRAP_DLL_HANDLES.append(
            os.add_dll_directory(str(Path(sys.executable).resolve().parent))
        )
        _BOOTSTRAP_DLL_HANDLES.append(
            os.add_dll_directory(str((windows_directory / "System32").resolve()))
        )
    _BOOTSTRAP_CWD_OWNER = temporary
    atexit.register(_cleanup_bootstrap_sandbox)


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
    _TRUSTED_SOURCE_HASHES[name] = hashlib.sha256(source).hexdigest()
    return module


if __name__ == "__main__":
    _sanitize_bootstrap_environment()
    _trusted_source_module("benchmark_ocr", "benchmark_ocr.py")
    _trusted_source_module("benchmark_ocr_cord", "benchmark_ocr_cord.py")
    _trusted_source_module("ocr_acceptance_policy", "ocr_acceptance_policy.py")
    _trusted_source_module("benchmark_ocr_sroie", "benchmark_ocr_sroie.py")
    _trusted_source_module("ocr_sroie_acceptance_policy", "ocr_sroie_acceptance_policy.py")
    acceptance = _trusted_source_module(
        "benchmark_ocr_sroie_acceptance", "benchmark_ocr_sroie_acceptance.py"
    )
    acceptance._LOADED_SOURCE_HASHES.update(_TRUSTED_SOURCE_HASHES)
    _TRUSTED_BOOTSTRAPPED = True
else:
    import benchmark_ocr_sroie_acceptance as acceptance


@dataclass(frozen=True)
class CandidateCommit:
    commit: str
    git_executable_sha256: str
    repositoryClean: bool
    candidateSourceSetSha256: str


@dataclass(frozen=True)
class ConsumedAttempt:
    ledger: dict[str, Any]
    ledger_sha256: str
    quarantine_marker: dict[str, Any]
    quarantine_marker_sha256: str
    terminal_result: dict[str, Any]
    terminal_result_sha256: str


@dataclass(frozen=True)
class PosthocSealContext:
    """Trusted diagnostic values frozen before the report is staged."""

    validated_policy: acceptance.sroie_policy.ValidatedPolicy
    validated_policy_value_sha256: str
    expected_identities_json: str
    pre_run_binding_sha256: str
    expected_cpu_runtime_sha256: str
    expected_directml_runtime_sha256: str
    expected_worker_sha256: str
    observed_integrity_passed: bool
    observed_threshold_comparison_passed: bool
    backends_sha256: str
    backend_result_artifacts_sha256: str
    cross_backend_integrity_sha256: str
    determinism_sha256: str
    evaluations_sha256: str
    metrics_sha256: str
    report_sha256: str


_INITIAL_CLAIM_KEYS = {
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
_FAILED_LEDGER_KEYS = _INITIAL_CLAIM_KEYS | {
    "acceptancePassed",
    "claimSha256",
    "failedAtUtc",
    "failure",
    "finalDisposition",
    "integrityPassed",
    "quarantinedAtUtc",
    "quarantineMarkerSha256",
    "runSucceeded",
}


def _error(message: str, *, stage: str) -> acceptance.AcceptanceError:
    return acceptance.AcceptanceError(message, stage=stage)


def _is_sha256(value: Any) -> bool:
    return (
        isinstance(value, str)
        and len(value) == 64
        and all(character in "0123456789abcdef" for character in value)
    )


def _require_acknowledgement(value: Any) -> None:
    if value != ACKNOWLEDGEMENT:
        raise _error(
            "The exact consumed-holdout post-hoc acknowledgement is required",
            stage="acknowledgement",
        )


def _strict_timestamp(value: Any) -> datetime:
    try:
        accepted = acceptance.generic_policy.validate_utc_timestamp(value)
        return datetime.fromisoformat(accepted[:-1] + "+00:00")
    except (acceptance.generic_policy.PolicyError, ValueError) as exc:
        raise _error("A consumed-attempt timestamp is invalid", stage="consumed-attempt") from exc


def _expected_ledger_path() -> Path:
    return acceptance._lexical_absolute(
        acceptance._MACHINE_STATE_ROOT
        / "bstrings"
        / "acceptance-ledgers"
        / "icdar2019-sroie-bffe40c26759-test-one-shot-v1.json"
    )


def _load_quarantined_ledger() -> tuple[dict[str, Any], str, dict[str, Any], str]:
    ledger_path = acceptance._lexical_absolute(acceptance.CONFIRMATORY_ATTEMPT_LEDGER)
    if ledger_path != _expected_ledger_path():
        raise _error("The permanent one-shot ledger namespace changed", stage="consumed-attempt")
    ledger, ledger_sha256 = acceptance._strict_json(
        ledger_path,
        maximum_bytes=MAX_LEDGER_BYTES,
        name="consumed SROIE attempt ledger",
    )
    failure = ledger.get("failure")
    digest_fields = {
        "calibrationReportSha256",
        "candidateIdentitySha256",
        "claimSha256",
        "expectedTestSha256",
        "policySha256",
        "quarantineMarkerSha256",
        "reportOutputPathSha256",
        "witnessGhExecutableSha256",
        "witnessReleaseVerificationSha256",
        "witnessSha256",
    }
    if (
        ledger_sha256 != ORIGINAL_LEDGER_SHA256
        or set(ledger) != _FAILED_LEDGER_KEYS
        or ledger.get("status") != "quarantined"
        or ledger.get("phase") != "confirmatory"
        or ledger.get("finalDisposition") != "failed"
        or ledger.get("acceptancePassed") is not False
        or ledger.get("integrityPassed") is not False
        or ledger.get("runSucceeded") is not False
        or ledger.get("datasetId") != acceptance.sroie_policy.DATASET_ID
        or ledger.get("datasetRevision") != acceptance.sroie_policy.DATASET_REVISION
        or ledger.get("expectedTestBytes") != acceptance.sroie_policy.TEST_BYTES
        or ledger.get("expectedTestSha256") != acceptance.sroie_policy.TEST_SHA256
        or type(ledger.get("schemaVersion")) is not int
        or ledger["schemaVersion"] <= 0
        or not isinstance(ledger.get("protocol"), str)
        or not ledger["protocol"]
        or ledger.get("witnessGhVersion") != acceptance.PINNED_GH_VERSION
        or any(not _is_sha256(ledger.get(key)) for key in digest_fields)
        or not isinstance(failure, Mapping)
        or set(failure) != {"backend", "stage", "type"}
        or failure.get("backend") is not None
        or failure.get("stage") != "annotation-parse"
        or failure.get("type") != "AcceptanceError"
    ):
        raise _error(
            "The permanent ledger is not the failed and quarantined SROIE attempt",
            stage="consumed-attempt",
        )
    started = _strict_timestamp(ledger.get("startedAtUtc"))
    failed = _strict_timestamp(ledger.get("failedAtUtc"))
    quarantined = _strict_timestamp(ledger.get("quarantinedAtUtc"))
    if not started <= failed <= quarantined:
        raise _error("The consumed-attempt chronology is invalid", stage="consumed-attempt")

    original_claim = {key: ledger[key] for key in _INITIAL_CLAIM_KEYS}
    original_claim["status"] = "started"
    claim_sha256 = hashlib.sha256(acceptance._canonical_bytes(original_claim)).hexdigest()
    if ledger.get("claimSha256") != claim_sha256:
        raise _error(
            "The quarantined ledger does not bind its initial claim", stage="consumed-attempt"
        )

    marker_path = ledger_path.with_name(ledger_path.name + ".quarantined.json")
    marker, marker_sha256 = acceptance._strict_json(
        marker_path,
        maximum_bytes=MAX_LEDGER_BYTES,
        name="consumed SROIE quarantine marker",
    )
    if (
        marker_sha256 != ORIGINAL_QUARANTINE_MARKER_SHA256
        or set(marker) != {"claimSha256", "quarantinedAtUtc", "schemaVersion", "status"}
        or marker.get("status") != "quarantined"
        or marker.get("claimSha256") != claim_sha256
        or marker.get("schemaVersion") != ledger.get("schemaVersion")
        or marker.get("quarantinedAtUtc") != ledger.get("quarantinedAtUtc")
        or ledger.get("quarantineMarkerSha256") != marker_sha256
    ):
        raise _error("The permanent quarantine marker binding changed", stage="consumed-attempt")
    if acceptance._regular_file_object_id(
        ledger_path, name="consumed SROIE attempt ledger"
    ) == acceptance._regular_file_object_id(marker_path, name="consumed SROIE quarantine marker"):
        raise _error("The ledger and quarantine marker alias", stage="consumed-attempt")
    return ledger, ledger_sha256, marker, marker_sha256


def _load_terminal_result(path: Path) -> tuple[dict[str, Any], str]:
    _, raw, _ = acceptance._read_regular_file(
        path,
        maximum_bytes=MAX_TERMINAL_RESULT_BYTES,
        name="immutable original SROIE terminal result",
        stage="terminal-result",
    )
    digest = hashlib.sha256(raw).hexdigest()
    if digest != ORIGINAL_TERMINAL_RESULT_SHA256:
        raise _error(
            "The original immutable terminal-result digest changed", stage="terminal-result"
        )
    value = acceptance._parse_json_bytes(
        raw,
        maximum_bytes=MAX_TERMINAL_RESULT_BYTES,
        name="immutable original SROIE terminal result",
        canonical=False,
    )
    acceptance._validate_public_report_privacy(value)
    return value, digest


def _load_consumed_attempt(terminal_result: Path) -> ConsumedAttempt:
    ledger, ledger_sha256, marker, marker_sha256 = _load_quarantined_ledger()
    terminal, terminal_sha256 = _load_terminal_result(terminal_result)
    terminal_failure = terminal.get("failure")
    terminal_ledger = terminal.get("ledger")
    terminal_witness = terminal.get("pretestWitness")
    terminal_release = terminal.get("resultRelease")
    if (
        set(terminal)
        != {
            "acceptancePassed",
            "calibrationAccepted",
            "calibrationReportSha256",
            "candidateIdentitySha256",
            "datasetId",
            "datasetRevision",
            "evaluationCompleted",
            "evaluationRole",
            "expectedTestBytes",
            "expectedTestDocuments",
            "expectedTestSha256",
            "failure",
            "finalDisposition",
            "independentHoldoutConsumed",
            "integrityPassed",
            "ledger",
            "policySha256",
            "pretestWitness",
            "protocol",
            "quarantineMarkerSha256",
            "resultRelease",
            "runSucceeded",
            "schemaVersion",
            "sourceCommitSha1",
            "startedAtUtc",
            "terminalAtUtc",
            "testSnapshotVerified",
        }
        or terminal.get("acceptancePassed") is not False
        or terminal.get("calibrationAccepted") is not True
        or terminal.get("calibrationReportSha256") != ledger["calibrationReportSha256"]
        or terminal.get("candidateIdentitySha256") != ledger["candidateIdentitySha256"]
        or terminal.get("datasetId") != ledger["datasetId"]
        or terminal.get("datasetRevision") != ledger["datasetRevision"]
        or terminal.get("evaluationCompleted") is not False
        or terminal.get("evaluationRole") != "confirmatory"
        or terminal.get("expectedTestBytes") != ledger["expectedTestBytes"]
        or terminal.get("expectedTestDocuments") != acceptance.sroie_policy.RAW_TEST_ROWS
        or terminal.get("expectedTestSha256") != ledger["expectedTestSha256"]
        or not isinstance(terminal_failure, Mapping)
        or set(terminal_failure) != {"backend", "reason", "stage", "type"}
        or terminal_failure.get("backend") != ledger["failure"]["backend"]
        or terminal_failure.get("stage") != ledger["failure"]["stage"]
        or terminal_failure.get("type") != ledger["failure"]["type"]
        or terminal_failure.get("reason") != "source-annotation-degenerate-bounding-box"
        or terminal.get("finalDisposition") != "failed"
        or terminal.get("independentHoldoutConsumed") is not True
        or terminal.get("integrityPassed") is not False
        or not isinstance(terminal_ledger, Mapping)
        or terminal_ledger
        != {"sha256": ledger_sha256, "status": "quarantined"}
        or terminal.get("policySha256") != ledger["policySha256"]
        or not isinstance(terminal_witness, Mapping)
        or terminal_witness
        != {
            "assetName": "sroie-ocr-pretest-witness-v2.json",
            "repository": acceptance.PINNED_RELEASE_REPOSITORY,
            "sha256": ledger["witnessSha256"],
            "tag": "ocr-sroie-acceptance-v2-20260805-23992fc",
        }
        or terminal.get("protocol") != ledger["protocol"]
        or terminal.get("quarantineMarkerSha256") != marker_sha256
        or not isinstance(terminal_release, Mapping)
        or terminal_release
        != {
            "assetName": ORIGINAL_TERMINAL_RESULT_ASSET,
            "repository": acceptance.PINNED_RELEASE_REPOSITORY,
            "tag": ORIGINAL_TERMINAL_RELEASE_TAG,
        }
        or terminal.get("runSucceeded") is not False
        or terminal.get("schemaVersion") != ledger["schemaVersion"]
        or terminal.get("sourceCommitSha1") != ORIGINAL_CANDIDATE_COMMIT
        or terminal.get("startedAtUtc") != ledger["startedAtUtc"]
        or terminal.get("terminalAtUtc") != ledger["failedAtUtc"]
        or terminal.get("testSnapshotVerified") is not True
    ):
        raise _error(
            "The immutable terminal result does not bind the consumed attempt",
            stage="terminal-result",
        )
    identities = {
        acceptance._regular_file_object_id(
            acceptance.CONFIRMATORY_ATTEMPT_LEDGER,
            name="consumed SROIE attempt ledger",
        ),
        acceptance._regular_file_object_id(
            acceptance.CONFIRMATORY_ATTEMPT_LEDGER.with_name(
                acceptance.CONFIRMATORY_ATTEMPT_LEDGER.name + ".quarantined.json"
            ),
            name="consumed SROIE quarantine marker",
        ),
        acceptance._regular_file_object_id(
            terminal_result, name="immutable original SROIE terminal result"
        ),
    }
    if len(identities) != 3:
        raise _error(
            "A consumed-attempt artifact aliases another artifact", stage="consumed-attempt"
        )
    return ConsumedAttempt(
        ledger=ledger,
        ledger_sha256=ledger_sha256,
        quarantine_marker=marker,
        quarantine_marker_sha256=marker_sha256,
        terminal_result=terminal,
        terminal_result_sha256=terminal_sha256,
    )


def _recheck_consumed_attempt(path: Path, frozen: ConsumedAttempt) -> None:
    current = _load_consumed_attempt(path)
    if current != frozen:
        raise _error(
            "A consumed-attempt binding changed during diagnostics", stage="consumed-attempt"
        )


def _git_environment() -> dict[str, str]:
    allowed = {
        key: os.environ[key] for key in ("SystemRoot", "WINDIR", "TEMP", "TMP") if key in os.environ
    }
    allowed.update(acceptance._OUTER_OFFLINE_ENVIRONMENT)
    allowed["GIT_CONFIG_NOSYSTEM"] = "1"
    allowed["GIT_OPTIONAL_LOCKS"] = "0"
    allowed["GIT_TERMINAL_PROMPT"] = "0"
    return allowed


def _run_git(
    git_executable: Path,
    arguments: Sequence[str],
    *,
    maximum_stdout_bytes: int = MAX_GIT_OUTPUT_BYTES,
) -> bytes:
    command = [
        str(git_executable),
        "-c",
        "core.fsmonitor=false",
        "-c",
        "core.hooksPath=NUL" if os.name == "nt" else "core.hooksPath=/dev/null",
        "-C",
        str(_REPOSITORY_ROOT),
        *arguments,
    ]
    try:
        completed = subprocess.run(
            command,
            stdin=subprocess.DEVNULL,
            capture_output=True,
            check=False,
            timeout=GIT_TIMEOUT_SECONDS,
            env=_git_environment(),
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise _error(
            "The candidate Git identity could not be checked", stage="candidate-commit"
        ) from exc
    if (
        completed.returncode != 0
        or len(completed.stdout) > maximum_stdout_bytes
        or len(completed.stderr) > MAX_GIT_OUTPUT_BYTES
    ):
        raise _error("The candidate Git identity check failed", stage="candidate-commit")
    return completed.stdout


_CANDIDATE_SOURCE_PATHS = (
    "tools/enrichment/benchmark_ocr.py",
    "tools/enrichment/benchmark_ocr_cord.py",
    "tools/enrichment/benchmark_ocr_sroie.py",
    "tools/enrichment/benchmark_ocr_sroie_acceptance.py",
    "tools/enrichment/benchmark_ocr_sroie_posthoc.py",
    "tools/enrichment/bstrings_ocr.py",
    "tools/enrichment/ocr_acceptance_policy.py",
    "tools/enrichment/ocr_sroie_acceptance_policy.py",
)


def _verify_candidate_sources(git_executable: Path, expected_commit: str) -> str:
    source_hashes: dict[str, str] = {}
    for relative in _CANDIDATE_SOURCE_PATHS:
        expected = _run_git(
            git_executable,
            ("show", f"{expected_commit}:{relative}"),
            maximum_stdout_bytes=8 * 1024 * 1024,
        )
        source_path = _REPOSITORY_ROOT / Path(relative)
        _, current, _ = acceptance._read_regular_file(
            source_path,
            maximum_bytes=8 * 1024 * 1024,
            name="candidate source",
            stage="candidate-commit",
        )
        if current != expected:
            raise _error(
                "A loaded candidate source differs from the claimed commit",
                stage="candidate-commit",
            )
        source_hashes[relative] = hashlib.sha256(current).hexdigest()
    return acceptance.sroie_policy.sha256_canonical(source_hashes)


def _verify_candidate_commit(git_executable: Path, expected_commit: str) -> CandidateCommit:
    if (
        not isinstance(expected_commit, str)
        or len(expected_commit) != 40
        or any(character not in "0123456789abcdef" for character in expected_commit)
    ):
        raise _error("A full lowercase candidate commit is required", stage="candidate-commit")
    git_executable = acceptance._lexical_absolute(git_executable)
    acceptance._regular_file_object_id(git_executable, name="Git executable")
    git_executable_sha256 = acceptance.benchmark_core.sha256_file(git_executable)
    if git_executable_sha256 != PINNED_GIT_EXE_SHA256:
        raise _error("The Git executable hash is not pinned", stage="candidate-commit")
    try:
        repository = (
            _run_git(git_executable, ("rev-parse", "--show-toplevel"))
            .decode("utf-8", "strict")
            .strip()
        )
        head = (
            _run_git(git_executable, ("rev-parse", "--verify", "HEAD^{commit}"))
            .decode("ascii", "strict")
            .strip()
        )
    except UnicodeDecodeError as exc:
        raise _error(
            "The candidate Git identity is not strict text", stage="candidate-commit"
        ) from exc
    if (
        os.path.normcase(os.path.abspath(repository))
        != os.path.normcase(os.path.abspath(str(_REPOSITORY_ROOT)))
        or head != expected_commit
    ):
        raise _error(
            "The working tree does not match the candidate commit", stage="candidate-commit"
        )
    if _run_git(git_executable, ("status", "--porcelain=v1", "--untracked-files=all")):
        raise _error("The candidate repository is not clean", stage="candidate-commit")
    tracked_state = _run_git(
        git_executable,
        ("ls-files", "-v"),
        maximum_stdout_bytes=8 * 1024 * 1024,
    )
    if any(not line.startswith(b"H ") for line in tracked_state.splitlines()):
        raise _error(
            "The candidate index contains hidden or non-standard tracked state",
            stage="candidate-commit",
        )
    source_tree_sha256 = _verify_candidate_sources(git_executable, expected_commit)
    return CandidateCommit(
        commit=expected_commit,
        git_executable_sha256=git_executable_sha256,
        repositoryClean=True,
        candidateSourceSetSha256=source_tree_sha256,
    )


def _runner_source_snapshot() -> tuple[str, tuple[int | None, ...]]:
    _, raw, identity = acceptance._read_regular_file(
        _SOURCE_FILE,
        maximum_bytes=8 * 1024 * 1024,
        name="post-hoc diagnostic runner source",
        stage="source-identity",
    )
    return hashlib.sha256(raw).hexdigest(), identity


def _copy_repaired_test(
    source: Path, destination: Path
) -> tuple[str, tuple[int | None, int | None]]:
    source = acceptance._lexical_absolute(source)
    initial_object = acceptance._regular_file_object_id(source, name="repaired test copy")
    try:
        initial_sha256 = acceptance.sroie.verify_sroie_parquet(source, split="test")
    except acceptance.benchmark_core.BenchmarkError as exc:
        raise _error(str(exc), stage=exc.stage or "posthoc-corpus") from exc
    _, destination_chain = acceptance._create_fresh_snapshot_directory(destination.parent)
    source_descriptor: int | None = None
    destination_descriptor: int | None = None
    digest = hashlib.sha256()
    copied = 0
    try:
        source_descriptor = os.open(
            source,
            os.O_RDONLY | getattr(os, "O_BINARY", 0) | getattr(os, "O_NOFOLLOW", 0),
        )
        source_before = os.fstat(source_descriptor)
        destination_descriptor = os.open(
            destination,
            os.O_WRONLY
            | os.O_CREAT
            | os.O_EXCL
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
            0o600,
        )
        while True:
            chunk = os.read(source_descriptor, acceptance.HASH_CHUNK_BYTES)
            if not chunk:
                break
            digest.update(chunk)
            view = memoryview(chunk)
            while view:
                written = os.write(destination_descriptor, view)
                if written <= 0:
                    raise OSError("short write")
                view = view[written:]
            copied += len(chunk)
        os.fsync(destination_descriptor)
        source_after = os.fstat(source_descriptor)
        destination_after = os.fstat(destination_descriptor)
    except OSError as exc:
        raise _error(
            "The repaired test copy could not be snapshotted", stage="posthoc-corpus"
        ) from exc
    finally:
        if source_descriptor is not None:
            os.close(source_descriptor)
        if destination_descriptor is not None:
            os.close(destination_descriptor)
    copied_sha256 = digest.hexdigest()
    if (
        not stat.S_ISREG(source_before.st_mode)
        or acceptance._unsafe_file_attributes(source_before)
        or acceptance._file_identity(source_before) != acceptance._file_identity(source_after)
        or not stat.S_ISREG(destination_after.st_mode)
        or copied != source_before.st_size
        or copied != acceptance.sroie_policy.TEST_BYTES
        or initial_sha256 != acceptance.sroie_policy.TEST_SHA256
        or copied_sha256 != acceptance.sroie_policy.TEST_SHA256
        or acceptance._regular_file_object_id(source, name="repaired test copy") != initial_object
        or not acceptance._parents_stable(destination_chain)
    ):
        raise _error("The repaired test copy changed during snapshotting", stage="posthoc-corpus")
    try:
        source_after_sha256 = acceptance.sroie.verify_sroie_parquet(source, split="test")
        destination_sha256 = acceptance.sroie.verify_sroie_parquet(destination, split="test")
    except acceptance.benchmark_core.BenchmarkError as exc:
        raise _error(str(exc), stage=exc.stage or "posthoc-corpus") from exc
    if (
        source_after_sha256 != copied_sha256
        or destination_sha256 != copied_sha256
        or acceptance._regular_file_object_id(destination, name="post-hoc test snapshot")
        == initial_object
    ):
        raise _error(
            "The post-hoc test snapshot is not independent storage", stage="posthoc-corpus"
        )
    return copied_sha256, initial_object


def _recheck_repaired_test(
    source: Path,
    *,
    expected_sha256: str,
    expected_object_id: tuple[int | None, int | None],
) -> None:
    try:
        current_sha256 = acceptance.sroie.verify_sroie_parquet(source, split="test")
    except acceptance.benchmark_core.BenchmarkError as exc:
        raise _error(str(exc), stage=exc.stage or "posthoc-corpus") from exc
    if (
        current_sha256 != expected_sha256
        or acceptance._regular_file_object_id(source, name="repaired test copy")
        != expected_object_id
    ):
        raise _error("The repaired test source changed during diagnostics", stage="posthoc-corpus")


_POSTHOC_ARTIFACT_KEYS = frozenset(
    {
        "posthocBboxRepairAudit",
        "posthocCorpusManifest",
        "posthocDuplicateAudit",
        "posthocInventory",
        "posthocTestSnapshot",
        "posthocWorkerManifest",
    }
)


def _posthoc_artifact_paths(corpus: Any, snapshot: Path) -> dict[str, Path]:
    return {
        "posthocBboxRepairAudit": corpus.bbox_repair_audit,
        "posthocCorpusManifest": corpus.corpus_manifest,
        "posthocDuplicateAudit": corpus.duplicate_audit,
        "posthocInventory": corpus.inventory,
        "posthocTestSnapshot": snapshot,
        "posthocWorkerManifest": corpus.worker_manifest,
    }


def _capture_posthoc_artifacts(
    paths: Mapping[str, Path],
    *,
    evidence_root: Path,
    corpus_identity: Mapping[str, Any],
) -> dict[str, dict[str, Any]]:
    if set(paths) != _POSTHOC_ARTIFACT_KEYS:
        raise _error("The post-hoc artifact set changed", stage="artifact")
    artifacts = {
        name: acceptance._safe_artifact(path, evidence_root=evidence_root)
        for name, path in paths.items()
    }
    bindings = {
        "posthocBboxRepairAudit": "bboxRepairAuditSha256",
        "posthocCorpusManifest": "corpusManifestSha256",
        "posthocDuplicateAudit": "duplicateAuditSha256",
        "posthocTestSnapshot": "parquetSha256",
        "posthocWorkerManifest": "workerManifestSha256",
    }
    if (
        any(
            artifacts[artifact].get("sha256") != corpus_identity.get(identity_key)
            for artifact, identity_key in bindings.items()
        )
        or artifacts["posthocTestSnapshot"].get("bytes")
        != acceptance.sroie_policy.TEST_BYTES
    ):
        raise _error(
            "The post-hoc artifact bytes do not bind the scored corpus", stage="artifact"
        )
    return artifacts


def _recheck_posthoc_artifacts(
    paths: Mapping[str, Path],
    *,
    evidence_root: Path,
    corpus_identity: Mapping[str, Any],
    expected: Mapping[str, Mapping[str, Any]],
) -> None:
    current = _capture_posthoc_artifacts(
        paths, evidence_root=evidence_root, corpus_identity=corpus_identity
    )
    if current != expected:
        raise _error("A post-hoc evidence artifact changed before publication", stage="artifact")


_BACKEND_RESULT_FILES = (
    "assessments.jsonl",
    "metrics-per-document-case-sensitive.jsonl",
    "metrics-per-document.jsonl",
    "strings.jsonl",
)


def _posthoc_backend_result_paths(phase_root: Path) -> dict[str, Path]:
    paths: dict[str, Path] = {}
    for provider in ("cpu", "directml", "hybrid"):
        provider_root = phase_root / "results" / provider
        run_roots = [provider_root / "quality-all-100"]
        run_roots.extend(
            provider_root
            / "determinism-rows-0000-0009"
            / f"run-{index:02d}"
            for index in range(1, acceptance.DETERMINISM_REPETITIONS + 1)
        )
        for run_root in run_roots:
            for file_name in _BACKEND_RESULT_FILES:
                path = run_root / file_name
                paths[path.relative_to(phase_root).as_posix()] = path
    return paths


def _capture_backend_result_artifacts(
    phase_root: Path,
) -> dict[str, dict[str, Any]]:
    paths = _posthoc_backend_result_paths(phase_root)
    return {
        name: acceptance._safe_artifact(path, evidence_root=phase_root)
        for name, path in paths.items()
    }


def _validate_backend_result_bindings(
    runs: Sequence[Mapping[str, Any]],
    artifacts: Mapping[str, Mapping[str, Any]],
) -> None:
    """Bind every published backend-result descriptor to its run claim."""

    providers = ("cpu", "directml", "hybrid")
    if len(runs) != len(providers) or set(artifacts) != set(
        _posthoc_backend_result_paths(Path("posthoc-evidence-root"))
    ):
        raise _error("The post-hoc backend result set changed", stage="artifact")
    for provider, backend in zip(providers, runs, strict=True):
        if not isinstance(backend, Mapping) or backend.get("requestedProvider") != provider:
            raise _error("A post-hoc backend result is invalid", stage="artifact")
        determinism = backend.get("determinism")
        repetitions = determinism.get("runs") if isinstance(determinism, Mapping) else None
        quality = backend.get("qualityRun")
        if (
            not isinstance(quality, Mapping)
            or not isinstance(repetitions, list)
            or len(repetitions) != acceptance.DETERMINISM_REPETITIONS
            or any(not isinstance(run, Mapping) for run in repetitions)
        ):
            raise _error("A post-hoc backend run set is incomplete", stage="artifact")
        run_roots = [("quality-all-100", quality)]
        run_roots.extend(
            (
                f"determinism-rows-0000-0009/run-{index:02d}",
                repetition,
            )
            for index, repetition in enumerate(repetitions, start=1)
        )
        for relative_root, run in run_roots:
            raw_hashes = run.get("rawOutputHashes")
            metrics = run.get("metrics")
            diagnostics = (
                metrics.get("caseSensitiveDiagnostics")
                if isinstance(metrics, Mapping)
                else None
            )
            expected_hashes = {
                "assessments.jsonl": (
                    raw_hashes.get("assessmentsSha256")
                    if isinstance(raw_hashes, Mapping)
                    else None
                ),
                "metrics-per-document-case-sensitive.jsonl": (
                    diagnostics.get("perDocumentMetricsSha256")
                    if isinstance(diagnostics, Mapping)
                    else None
                ),
                "metrics-per-document.jsonl": run.get("perDocumentMetricsSha256"),
                "strings.jsonl": (
                    raw_hashes.get("stringsSha256")
                    if isinstance(raw_hashes, Mapping)
                    else None
                ),
            }
            for file_name, expected_sha256 in expected_hashes.items():
                artifact_name = f"results/{provider}/{relative_root}/{file_name}"
                artifact = artifacts.get(artifact_name)
                if (
                    not _is_sha256(expected_sha256)
                    or not _valid_public_artifact(artifact)
                    or artifact.get("path") != artifact_name
                    or artifact.get("sha256") != expected_sha256
                ):
                    raise _error(
                        "A post-hoc backend result is not bound to its run claim",
                        stage="artifact",
                    )


def _recheck_backend_result_artifacts(
    phase_root: Path,
    expected: Mapping[str, Mapping[str, Any]],
) -> None:
    current = _capture_backend_result_artifacts(phase_root)
    if current != expected:
        raise _error("A post-hoc backend result changed before publication", stage="artifact")


def _require_disjoint_posthoc_paths(args: argparse.Namespace) -> None:
    work_root = acceptance._lexical_absolute(args.work_directory)
    output = acceptance._lexical_absolute(args.output)
    repository = acceptance._lexical_absolute(_REPOSITORY_ROOT)
    ledger = acceptance._lexical_absolute(acceptance.CONFIRMATORY_ATTEMPT_LEDGER)
    marker = ledger.with_name(ledger.name + ".quarantined.json")
    protected_files = tuple(
        acceptance._lexical_absolute(path)
        for path in (
            _SOURCE_FILE,
            Path(sys.executable),
            args.git_executable,
            args.worker,
            args.model_pack,
            args.cpu_python,
            args.directml_python,
            args.calibration_report,
            args.policy,
            args.original_terminal_result,
            args.repaired_test_copy,
            ledger,
            marker,
        )
    )
    output_artifacts = {
        output,
        output.with_name(output.name + ".incomplete"),
        output.with_name(output.name + ".staged"),
    }
    if (
        work_root == repository
        or work_root.is_relative_to(repository)
        or output.is_relative_to(repository)
        or work_root == ledger.parent
        or work_root.is_relative_to(ledger.parent)
        or output.is_relative_to(ledger.parent)
        or any(work_root == path or path.is_relative_to(work_root) for path in protected_files)
        or any(artifact in protected_files for artifact in output_artifacts)
        or output == work_root
        or output.is_relative_to(work_root)
    ):
        raise _error(
            "The post-hoc work and report paths are not disjoint from protected inputs",
            stage="arguments",
        )


def _load_v3_calibration(
    args: argparse.Namespace, frozen: acceptance.FrozenCandidate
) -> tuple[acceptance.CalibrationContext, Any, dict[str, Any]]:
    context = acceptance.load_calibration_context(
        args.calibration_report, args.calibration_evidence_root
    )
    identity = acceptance.build_identity(frozen, context.identity["calibrationCorpus"])
    if acceptance.sroie_policy.canonical_json(identity) != acceptance.sroie_policy.canonical_json(
        context.identity
    ):
        raise _error("The live candidate differs from v3 calibration", stage="calibration-input")
    acceptance._verify_persisted_runtime_context(context, frozen)
    try:
        policy = acceptance.sroie_policy.validate_policy(
            args.policy,
            calibration_report_path=args.calibration_report,
            expected_identity=identity,
            expected_identities=context.expected_identities,
        )
    except acceptance.sroie_policy.PolicyError as exc:
        raise _error(str(exc), stage="policy") from exc
    report = context.report
    if (
        acceptance.sroie_policy.POLICY_ID != "bstrings-icdar2019-sroie-train-test-ocr-v3"
        or acceptance.sroie.SROIE_PROTOCOL != EXPECTED_SROIE_ADAPTER_PROTOCOL
        or report.get("evaluationRole") != "calibration"
        or report.get("acceptancePassed") is not True
        or report.get("integrityPassed") is not True
        or report.get("runSucceeded") is not True
        or report.get("protocol") != acceptance.PROTOCOL
        or report.get("schemaVersion") != acceptance.SCHEMA_VERSION
        or policy.value.get("policyId") != acceptance.sroie_policy.POLICY_ID
        or policy.value.get("protocol") != acceptance.PROTOCOL
    ):
        raise _error(
            "A completed and accepted v3 calibration is required", stage="calibration-input"
        )
    acceptance._recheck_policy(args.policy, policy)
    return context, policy, identity


def _recheck_v3_calibration(
    args: argparse.Namespace,
    frozen_context: acceptance.CalibrationContext,
    frozen_policy: Any,
) -> None:
    current = acceptance.load_calibration_context(
        args.calibration_report, args.calibration_evidence_root
    )
    if (
        current.report_sha256 != frozen_context.report_sha256
        or acceptance.sroie_policy.canonical_json(current.report)
        != acceptance.sroie_policy.canonical_json(frozen_context.report)
        or current.expected_identities != frozen_context.expected_identities
        or current.source_image_sha256s != frozen_context.source_image_sha256s
    ):
        raise _error(
            "The v3 calibration evidence changed during diagnostics", stage="calibration-input"
        )
    acceptance._recheck_policy(args.policy, frozen_policy)


def _posthoc_integrity(
    args: argparse.Namespace,
    frozen: acceptance.FrozenCandidate,
    identity: Mapping[str, Any],
    corpus: Any,
    determinism: Any,
    runs: Sequence[Mapping[str, Any]],
) -> tuple[dict[str, bool], dict[str, Any], dict[str, dict[str, bool]]]:
    determinism_checks = {
        run["requestedProvider"]: acceptance._determinism_checks(run, determinism) for run in runs
    }
    critical_hashes = {run.get("criticalEvidenceSha256") for run in runs}
    metrics_hashes = {run.get("metricsSha256") for run in runs}
    per_document_hashes = {
        run.get("qualityRun", {}).get("perDocumentMetricsSha256") for run in runs
    }
    confidence = acceptance.cord.confidence_parity(
        [run.get("_confidenceByCriticalRecord", {}) for run in runs]
    )
    checks = {
        "allBackendDeterminismPassed": all(
            all(values.values()) for values in determinism_checks.values()
        ),
        "allBackendProvenancePassed": all(run.get("provenancePassed") is True for run in runs),
        "candidateStable": acceptance._candidate_stable(args, identity),
        "confidenceParityPassed": confidence.get("passed") is True,
        "criticalEvidenceEqual": len(critical_hashes) == 1 and None not in critical_hashes,
        "documentCountsExact": all(run.get("qualityRows") == len(corpus.documents) for run in runs),
        "hybridMeaningfulLaneCoverage": acceptance._meaningful_hybrid_lane_coverage(runs[2]),
        "metricsEqual": len(metrics_hashes) == 1 and None not in metrics_hashes,
        "perDocumentMetricsEqual": len(per_document_hashes) == 1
        and None not in per_document_hashes,
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
        "workerHashesExact": all(run.get("workerSha256") == frozen.worker_sha256 for run in runs),
    }
    return checks, confidence, determinism_checks


def _public_candidate_binding(
    candidate_commit: CandidateCommit,
    runner_sha256: str,
) -> dict[str, Any]:
    return {
        "commit": candidate_commit.commit,
        "gitExecutableSha256": candidate_commit.git_executable_sha256,
        "repositoryClean": candidate_commit.repositoryClean,
        "runnerSha256": runner_sha256,
        "candidateSourceSetSha256": candidate_commit.candidateSourceSetSha256,
    }


def _public_consumed_binding(consumed: ConsumedAttempt) -> dict[str, Any]:
    return {
        "claimSha256": consumed.ledger["claimSha256"],
        "failure": dict(consumed.ledger["failure"]),
        "finalDisposition": "failed",
        "ledgerSha256": consumed.ledger_sha256,
        "originalCandidateCommit": ORIGINAL_CANDIDATE_COMMIT,
        "originalTerminalReleaseTag": ORIGINAL_TERMINAL_RELEASE_TAG,
        "originalTerminalResultAsset": ORIGINAL_TERMINAL_RESULT_ASSET,
        "originalTerminalResultSha256": consumed.terminal_result_sha256,
        "quarantineMarkerSha256": consumed.quarantine_marker_sha256,
        "status": "quarantined",
    }


def _public_calibration_binding(
    context: acceptance.CalibrationContext,
    policy: acceptance.sroie_policy.ValidatedPolicy,
    identity: Mapping[str, Any],
) -> dict[str, Any]:
    return {
        "candidateIdentitySha256": acceptance.sroie_policy.sha256_canonical(identity),
        "policyId": acceptance.sroie_policy.POLICY_ID,
        "policySha256": policy.file_sha256,
        "reportSha256": context.report_sha256,
    }


def _public_repaired_binding(
    corpus_identity: Mapping[str, Any],
    repair_identity: Mapping[str, Any],
    repair_identity_sha256: str,
) -> dict[str, Any]:
    return {
        **copy.deepcopy(dict(corpus_identity)),
        "expectedRepairIdentitySha256": EXPECTED_REPAIR_IDENTITY_SHA256,
        "repairAppliedOnlyToDerivedScoringGeometry": True,
        "repairIdentity": copy.deepcopy(dict(repair_identity)),
        "repairIdentitySha256": repair_identity_sha256,
        "repairPolicy": acceptance.sroie.SROIE_BBOX_REPAIR_POLICY,
        "sourceArtifactReadOnlyAndReverified": True,
    }


def _pre_run_binding(
    *,
    candidate_commit: CandidateCommit,
    runner_sha256: str,
    consumed: ConsumedAttempt,
    calibration_context: acceptance.CalibrationContext,
    policy: acceptance.sroie_policy.ValidatedPolicy,
    identity: Mapping[str, Any],
    expected_identities: Sequence[Mapping[str, Any]],
    repaired_holdout: Mapping[str, Any],
    artifacts: Mapping[str, Mapping[str, Any]],
) -> dict[str, Any]:
    """Freeze all trusted inputs available before any OCR backend executes."""

    return {
        "artifactPublication": {
            "artifactVisibility": "private-local-not-for-release",
            "publicReleaseContents": "path-free-report-only",
        },
        "artifacts": copy.deepcopy(dict(artifacts)),
        "candidate": _public_candidate_binding(candidate_commit, runner_sha256),
        "candidateCommit": candidate_commit.commit,
        "consumedAttempt": _public_consumed_binding(consumed),
        "expectedIdentitiesSha256": acceptance.sroie_policy.sha256_canonical(
            [dict(item) for item in expected_identities]
        ),
        "identity": copy.deepcopy(dict(identity)),
        "identitySha256": acceptance.sroie_policy.sha256_canonical(identity),
        "repairedHoldout": copy.deepcopy(dict(repaired_holdout)),
        "v3Calibration": _public_calibration_binding(calibration_context, policy, identity),
    }


def _valid_public_artifact(value: Any) -> bool:
    return (
        isinstance(value, Mapping)
        and set(value) == {"bytes", "path", "sha256"}
        and type(value.get("bytes")) is int
        and value["bytes"] >= 0
        and isinstance(value.get("path"), str)
        and bool(value["path"])
        and _is_sha256(value.get("sha256"))
    )


def _validate_posthoc_report(
    report: Mapping[str, Any], seal_context: PosthocSealContext
) -> None:
    acceptance._validate_public_report_privacy(report)
    try:
        expected_identity_rows = json.loads(seal_context.expected_identities_json)
    except (AttributeError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise _error("The post-hoc identity anchor is invalid", stage="report") from exc
    if (
        not isinstance(seal_context, PosthocSealContext)
        or not isinstance(
            seal_context.validated_policy,
            acceptance.sroie_policy.ValidatedPolicy,
        )
        or acceptance.sroie_policy.sha256_canonical(seal_context.validated_policy.value)
        != seal_context.validated_policy_value_sha256
        or not isinstance(expected_identity_rows, list)
        or any(not isinstance(item, Mapping) for item in expected_identity_rows)
        or acceptance.sroie_policy.canonical_json(expected_identity_rows)
        != seal_context.expected_identities_json
        or any(
            not _is_sha256(value)
            for value in (
                seal_context.pre_run_binding_sha256,
                seal_context.expected_cpu_runtime_sha256,
                seal_context.expected_directml_runtime_sha256,
                seal_context.expected_worker_sha256,
                seal_context.backends_sha256,
                seal_context.backend_result_artifacts_sha256,
                seal_context.cross_backend_integrity_sha256,
                seal_context.determinism_sha256,
                seal_context.evaluations_sha256,
                seal_context.metrics_sha256,
                seal_context.report_sha256,
            )
        )
        or type(seal_context.observed_integrity_passed) is not bool
        or type(seal_context.observed_threshold_comparison_passed) is not bool
        or acceptance.sroie_policy.sha256_canonical(report)
        != seal_context.report_sha256
    ):
        raise _error("The post-hoc sealing context changed", stage="report")
    identity = report.get("identity")
    metrics = report.get("metrics")
    evidence = report.get("evidence")
    threshold_result = report.get("diagnosticThresholdComparisonPassed")
    report_keys = {
        "acceptancePassed",
        "candidateCommit",
        "diagnosticThresholdComparisonPassed",
        "documents",
        "evaluationCompleted",
        "evaluationRole",
        "evidence",
        "expectedIdentitiesSha256",
        "finalDisposition",
        "identity",
        "identitySha256",
        "independentHoldout",
        "integrityPassed",
        "metrics",
        "metricsSha256",
        "protocol",
        "rawRows",
        "repairPromptedByHeldoutFailure",
        "runSucceeded",
        "schemaVersion",
        "scoringProtocol",
    }
    if (
        set(report) != report_keys
        or report.get("schemaVersion") != POSTHOC_SCHEMA_VERSION
        or report.get("protocol") != POSTHOC_PROTOCOL
        or report.get("scoringProtocol") != acceptance.PROTOCOL
        or report.get("evaluationRole") != "post-hoc-diagnostic"
        or report.get("independentHoldout") is not False
        or report.get("acceptancePassed") is not None
        or report.get("repairPromptedByHeldoutFailure") is not True
        or report.get("evaluationCompleted") is not True
        or report.get("runSucceeded") is not True
        or type(report.get("integrityPassed")) is not bool
        or type(threshold_result) is not bool
        or (threshold_result and report.get("integrityPassed") is not True)
        or report.get("finalDisposition")
        != (
            "diagnostic-complete"
            if report.get("integrityPassed") is True
            else "diagnostic-integrity-failed"
        )
        or not isinstance(identity, Mapping)
        or report.get("identitySha256") != acceptance.sroie_policy.sha256_canonical(identity)
        or not isinstance(metrics, Mapping)
        or report.get("metricsSha256") != acceptance.sroie_policy.sha256_canonical(metrics)
        or not isinstance(evidence, Mapping)
        or report.get("documents") != acceptance.sroie_policy.RAW_TEST_ROWS
        or report.get("rawRows") != acceptance.sroie_policy.RAW_TEST_ROWS
        or not _is_sha256(report.get("expectedIdentitiesSha256"))
        or not isinstance(report.get("candidateCommit"), str)
        or len(report["candidateCommit"]) != 40
        or any(character not in "0123456789abcdef" for character in report["candidateCommit"])
    ):
        raise _error("The post-hoc diagnostic report contract changed", stage="report")
    consumed = evidence.get("consumedAttempt")
    calibration = evidence.get("v3Calibration")
    repaired = evidence.get("repairedHoldout")
    candidate = evidence.get("candidate")
    backends = evidence.get("backends")
    artifacts = evidence.get("artifacts")
    artifact_publication = evidence.get("artifactPublication")
    backend_result_artifacts = evidence.get("backendResultArtifacts")
    cross_backend = evidence.get("crossBackendIntegrity")
    cross_checks = cross_backend.get("checks") if isinstance(cross_backend, Mapping) else None
    diagnostic = evidence.get("diagnosticPolicyComparison")
    evaluations = diagnostic.get("evaluations") if isinstance(diagnostic, Mapping) else None
    provider_names = ("cpu", "directml", "hybrid")
    integrity_check_keys = {
        "allBackendDeterminismPassed",
        "allBackendProvenancePassed",
        "candidateStable",
        "confidenceParityPassed",
        "criticalEvidenceEqual",
        "documentCountsExact",
        "hybridMeaningfulLaneCoverage",
        "metricsEqual",
        "perDocumentMetricsEqual",
        "requestedProvidersExact",
        "resolvedProvidersExact",
        "runtimeHashesExact",
        "workerHashesExact",
    }
    determinism_check_keys = {
        "byteDeterministic",
        "canonicalEvidenceDeterministic",
        "criticalEvidenceDeterministic",
        "executionProviderCountsStable",
        "metricsDeterministic",
        "provenancePassed",
        "repetitionCountExact",
        "rowIdentitiesExact",
        "selectionIdentityExact",
        "stableProvider",
        "stableRuntime",
        "stableTextNormalization",
        "stableThreadCounts",
        "stableWorkerCounts",
        "thresholdOverridesAbsent",
    }
    determinism = evidence.get("determinism") if isinstance(evidence, Mapping) else None
    determinism_checks = (
        determinism.get("checksByBackend") if isinstance(determinism, Mapping) else None
    )
    confidence = (
        cross_backend.get("confidenceParity") if isinstance(cross_backend, Mapping) else None
    )
    execution = evidence.get("execution") if isinstance(evidence, Mapping) else None
    holdout_status = evidence.get("holdoutStatus") if isinstance(evidence, Mapping) else None
    if (
        set(evidence)
        != {
            "artifactPublication",
            "artifacts",
            "backends",
            "backendResultArtifacts",
            "candidate",
            "consumedAttempt",
            "crossBackendIntegrity",
            "determinism",
            "diagnosticPolicyComparison",
            "execution",
            "generatedAtUtc",
            "holdoutStatus",
            "repairedHoldout",
            "v3Calibration",
        }
        or not isinstance(consumed, Mapping)
        or set(consumed)
        != {
            "claimSha256",
            "failure",
            "finalDisposition",
            "ledgerSha256",
            "originalCandidateCommit",
            "originalTerminalReleaseTag",
            "originalTerminalResultAsset",
            "originalTerminalResultSha256",
            "quarantineMarkerSha256",
            "status",
        }
        or consumed.get("status") != "quarantined"
        or consumed.get("finalDisposition") != "failed"
        or not _is_sha256(consumed.get("claimSha256"))
        or consumed.get("ledgerSha256") != ORIGINAL_LEDGER_SHA256
        or consumed.get("quarantineMarkerSha256")
        != ORIGINAL_QUARANTINE_MARKER_SHA256
        or consumed.get("originalCandidateCommit") != ORIGINAL_CANDIDATE_COMMIT
        or consumed.get("originalTerminalReleaseTag") != ORIGINAL_TERMINAL_RELEASE_TAG
        or consumed.get("originalTerminalResultAsset") != ORIGINAL_TERMINAL_RESULT_ASSET
        or consumed.get("originalTerminalResultSha256") != ORIGINAL_TERMINAL_RESULT_SHA256
        or not isinstance(consumed.get("failure"), Mapping)
        or set(consumed["failure"]) != {"backend", "stage", "type"}
        or not isinstance(calibration, Mapping)
        or set(calibration)
        != {
            "candidateIdentitySha256",
            "policyId",
            "policySha256",
            "reportSha256",
        }
        or calibration.get("policyId") != acceptance.sroie_policy.POLICY_ID
        or not _is_sha256(calibration.get("reportSha256"))
        or calibration.get("policySha256")
        != seal_context.validated_policy.file_sha256
        or calibration.get("candidateIdentitySha256") != report.get("identitySha256")
        or not isinstance(candidate, Mapping)
        or set(candidate)
        != {
            "commit",
            "gitExecutableSha256",
            "repositoryClean",
            "runnerSha256",
            "candidateSourceSetSha256",
        }
        or candidate.get("commit") != report.get("candidateCommit")
        or candidate.get("gitExecutableSha256") != PINNED_GIT_EXE_SHA256
        or candidate.get("repositoryClean") is not True
        or not _is_sha256(candidate.get("runnerSha256"))
        or not _is_sha256(candidate.get("candidateSourceSetSha256"))
        or not isinstance(artifacts, Mapping)
        or set(artifacts) != _POSTHOC_ARTIFACT_KEYS
        or any(not _valid_public_artifact(value) for value in artifacts.values())
        or len({value["path"] for value in artifacts.values()}) != len(artifacts)
        or artifact_publication
        != {
            "artifactVisibility": "private-local-not-for-release",
            "publicReleaseContents": "path-free-report-only",
        }
        or not isinstance(backend_result_artifacts, Mapping)
        or set(backend_result_artifacts)
        != set(_posthoc_backend_result_paths(Path("posthoc-evidence-root")))
        or any(
            not _valid_public_artifact(value)
            for value in backend_result_artifacts.values()
        )
        or any(
            value.get("path") != name
            for name, value in backend_result_artifacts.items()
        )
        or not isinstance(backends, list)
        or len(backends) != 3
        or any(not isinstance(run, Mapping) for run in backends)
        or tuple(run.get("requestedProvider") for run in backends) != provider_names
        or tuple(run.get("resolvedProvider") for run in backends)
        != ("cpu", "directml", "hybrid-directml-cpu")
        or not isinstance(cross_backend, Mapping)
        or set(cross_backend) != {"checks", "confidenceParity"}
        or not isinstance(cross_checks, Mapping)
        or set(cross_checks) != integrity_check_keys
        or any(type(value) is not bool for value in cross_checks.values())
        or all(cross_checks.values()) != report.get("integrityPassed")
        or not isinstance(confidence, Mapping)
        or set(confidence)
        != {
            "changedRecords",
            "comparedRecords",
            "evaluated",
            "maximumAbsoluteDelta",
            "meanAbsoluteDelta",
            "passed",
            "structurallyAligned",
            "threshold",
        }
        or confidence.get("evaluated") is not True
        or type(confidence.get("passed")) is not bool
        or type(confidence.get("structurallyAligned")) is not bool
        or type(confidence.get("changedRecords")) is not int
        or type(confidence.get("comparedRecords")) is not int
        or confidence["changedRecords"] < 0
        or confidence["comparedRecords"] < confidence["changedRecords"]
        or any(
            type(confidence.get(name)) not in {int, float}
            or not math.isfinite(float(confidence[name]))
            or confidence[name] < 0
            for name in ("maximumAbsoluteDelta", "meanAbsoluteDelta", "threshold")
        )
        or confidence.get("threshold")
        != acceptance.cord.CONFIDENCE_PARITY_MAX_ABS_DELTA
        or confidence.get("passed")
        is not (
            confidence.get("structurallyAligned") is True
            and confidence.get("maximumAbsoluteDelta") <= confidence.get("threshold")
        )
        or confidence.get("passed") is not cross_checks.get("confidenceParityPassed")
        or not isinstance(determinism, Mapping)
        or set(determinism) != {"checksByBackend", "repetitions"}
        or determinism.get("repetitions") != acceptance.DETERMINISM_REPETITIONS
        or not isinstance(determinism_checks, Mapping)
        or set(determinism_checks) != set(provider_names)
        or any(
            not isinstance(checks, Mapping)
            or set(checks) != determinism_check_keys
            or any(type(value) is not bool for value in checks.values())
            for checks in determinism_checks.values()
        )
        or not isinstance(execution, Mapping)
        or set(execution) != {"backendOrder", "threads"}
        or execution.get("backendOrder") != list(provider_names)
        or execution.get("threads") != 0
        or not isinstance(holdout_status, Mapping)
        or holdout_status
        != {
            "independentHoldout": False,
            "oneShotAttemptConsumed": True,
            "posthocOnly": True,
        }
        or not isinstance(diagnostic, Mapping)
        or set(diagnostic)
        != {
            "allBackendThresholdsPassed",
            "evaluations",
            "isAcceptanceDecision",
            "reportableResultPassed",
        }
        or not isinstance(evaluations, Mapping)
        or set(evaluations) != set(provider_names)
        or any(not isinstance(value, Mapping) for value in evaluations.values())
        or type(diagnostic.get("allBackendThresholdsPassed")) is not bool
        or diagnostic.get("isAcceptanceDecision") is not False
        or diagnostic.get("reportableResultPassed") != threshold_result
        or threshold_result
        != (
            report.get("integrityPassed") is True
            and diagnostic.get("allBackendThresholdsPassed") is True
        )
        or not isinstance(repaired, Mapping)
        or repaired.get("repairPolicy") != acceptance.sroie.SROIE_BBOX_REPAIR_POLICY
        or repaired.get("repairedRegionCount") != EXPECTED_REPAIRED_TEST_REGIONS
        or repaired.get("expectedRepairIdentitySha256")
        != EXPECTED_REPAIR_IDENTITY_SHA256
        or repaired.get("repairIdentity") != EXPECTED_REPAIR_IDENTITY
        or repaired.get("repairIdentitySha256") != EXPECTED_REPAIR_IDENTITY_SHA256
        or repaired.get("parquetSha256") != acceptance.sroie_policy.TEST_SHA256
        or any(
            repaired.get(key) != value for key, value in EXPECTED_REPAIR_IDENTITY.items()
        )
        or repaired.get("repairAppliedOnlyToDerivedScoringGeometry") is not True
        or repaired.get("sourceArtifactReadOnlyAndReverified") is not True
        or artifacts["posthocTestSnapshot"].get("bytes")
        != acceptance.sroie_policy.TEST_BYTES
        or artifacts["posthocTestSnapshot"].get("sha256")
        != repaired.get("parquetSha256")
        or artifacts["posthocBboxRepairAudit"].get("sha256")
        != repaired.get("bboxRepairAuditSha256")
        or artifacts["posthocCorpusManifest"].get("sha256")
        != repaired.get("corpusManifestSha256")
        or artifacts["posthocDuplicateAudit"].get("sha256")
        != repaired.get("duplicateAuditSha256")
        or artifacts["posthocWorkerManifest"].get("sha256")
        != repaired.get("workerManifestSha256")
    ):
        raise _error("The post-hoc evidence binding changed", stage="report")

    try:
        acceptance.generic_policy.validate_utc_timestamp(evidence.get("generatedAtUtc"))
    except acceptance.generic_policy.PolicyError as exc:
        raise _error("The post-hoc report timestamp is invalid", stage="report") from exc
    pre_run_projection = {
        "artifactPublication": artifact_publication,
        "artifacts": artifacts,
        "candidate": candidate,
        "candidateCommit": report.get("candidateCommit"),
        "consumedAttempt": consumed,
        "expectedIdentitiesSha256": report.get("expectedIdentitiesSha256"),
        "identity": identity,
        "identitySha256": report.get("identitySha256"),
        "repairedHoldout": repaired,
        "v3Calibration": calibration,
    }
    if (
        acceptance.sroie_policy.sha256_canonical(pre_run_projection)
        != seal_context.pre_run_binding_sha256
        or acceptance.sroie_policy.sha256_canonical(backend_result_artifacts)
        != seal_context.backend_result_artifacts_sha256
    ):
        raise _error("The trusted post-hoc input binding changed", stage="report")
    _validate_backend_result_bindings(backends, backend_result_artifacts)

    expected_identity_rows = [dict(item) for item in expected_identity_rows]
    recomputed_evaluations: dict[str, Any] = {}
    backend_metrics: list[Mapping[str, Any]] = []
    try:
        for provider_name, backend in zip(provider_names, backends, strict=True):
            candidate_metrics = backend.get("metrics")
            if not isinstance(candidate_metrics, Mapping):
                raise _error("A post-hoc backend has no metrics", stage="report")
            recomputed_evaluations[provider_name] = (
                acceptance.sroie_policy.evaluate_confirmatory(
                    seal_context.validated_policy,
                    candidate_metrics,
                    expected_identities=expected_identity_rows,
                )
            )
            backend_metrics.append(candidate_metrics)
    except acceptance.sroie_policy.PolicyError as exc:
        raise _error(
            "The post-hoc policy evaluation cannot be authenticated",
            stage="report",
        ) from exc
    per_document_sha256 = [
        acceptance._sha256_bytes(acceptance._per_document_bytes(candidate_metrics))
        for candidate_metrics in backend_metrics
    ]
    quality_passed = all(
        evaluation.get("passed") is True for evaluation in recomputed_evaluations.values()
    )
    determinism_passed = all(
        all(value is True for value in checks.values())
        for checks in determinism_checks.values()
    )
    runtime_profiles = identity.get("runtimeProfiles")
    cpu_profile = runtime_profiles.get("cpu") if isinstance(runtime_profiles, Mapping) else None
    directml_profile = (
        runtime_profiles.get("directml") if isinstance(runtime_profiles, Mapping) else None
    )
    worker_identity = identity.get("worker")
    runtime_hashes_exact = (
        isinstance(cpu_profile, Mapping)
        and isinstance(directml_profile, Mapping)
        and cpu_profile.get("executableSha256")
        == seal_context.expected_cpu_runtime_sha256
        and directml_profile.get("executableSha256")
        == seal_context.expected_directml_runtime_sha256
        and backends[0].get("runtimeSha256")
        == seal_context.expected_cpu_runtime_sha256
        and all(
            backend.get("runtimeSha256")
            == seal_context.expected_directml_runtime_sha256
            for backend in backends[1:]
        )
    )
    worker_hashes_exact = (
        isinstance(worker_identity, Mapping)
        and worker_identity.get("sha256") == seal_context.expected_worker_sha256
        and all(
            backend.get("workerSha256") == seal_context.expected_worker_sha256
            for backend in backends
        )
    )
    if (
        report.get("expectedIdentitiesSha256")
        != acceptance.sroie_policy.sha256_canonical(expected_identity_rows)
        or report.get("metricsSha256") != seal_context.metrics_sha256
        or acceptance.sroie_policy.sha256_canonical(backends)
        != seal_context.backends_sha256
        or acceptance.sroie_policy.sha256_canonical(cross_backend)
        != seal_context.cross_backend_integrity_sha256
        or acceptance.sroie_policy.sha256_canonical(determinism)
        != seal_context.determinism_sha256
        or acceptance.sroie_policy.sha256_canonical(evaluations)
        != seal_context.evaluations_sha256
        or acceptance.sroie_policy.canonical_json(evaluations)
        != acceptance.sroie_policy.canonical_json(recomputed_evaluations)
        or any(
            backend.get("metricsSha256")
            != acceptance.sroie_policy.sha256_canonical(backend_metrics[index])
            or acceptance.sroie_policy.canonical_json(backend_metrics[index])
            != acceptance.sroie_policy.canonical_json(metrics)
            or not isinstance(backend.get("qualityRun"), Mapping)
            or backend["qualityRun"].get("metricsSha256")
            != acceptance.sroie_policy.sha256_canonical(backend_metrics[index])
            or backend["qualityRun"].get("perDocumentMetricsSha256")
            != per_document_sha256[index]
            or acceptance.sroie_policy.canonical_json(backend["qualityRun"].get("metrics"))
            != acceptance.sroie_policy.canonical_json(backend_metrics[index])
            for index, backend in enumerate(backends)
        )
        or cross_checks.get("allBackendDeterminismPassed") is not determinism_passed
        or cross_checks.get("allBackendProvenancePassed")
        is not all(backend.get("provenancePassed") is True for backend in backends)
        or cross_checks.get("criticalEvidenceEqual")
        is not (
            len({backend.get("criticalEvidenceSha256") for backend in backends}) == 1
            and all(_is_sha256(backend.get("criticalEvidenceSha256")) for backend in backends)
        )
        or cross_checks.get("documentCountsExact")
        is not all(backend.get("qualityRows") == report["documents"] for backend in backends)
        or cross_checks.get("hybridMeaningfulLaneCoverage")
        is not acceptance._meaningful_hybrid_lane_coverage(backends[2])
        or cross_checks.get("metricsEqual")
        is not (len({backend.get("metricsSha256") for backend in backends}) == 1)
        or cross_checks.get("perDocumentMetricsEqual")
        is not (len(set(per_document_sha256)) == 1)
        or cross_checks.get("requestedProvidersExact") is not True
        or cross_checks.get("resolvedProvidersExact") is not True
        or cross_checks.get("runtimeHashesExact") is not runtime_hashes_exact
        or cross_checks.get("workerHashesExact") is not worker_hashes_exact
        or cross_checks.get("candidateStable") is not True
        or diagnostic.get("allBackendThresholdsPassed") is not quality_passed
        or threshold_result is not (report.get("integrityPassed") is True and quality_passed)
        or report.get("integrityPassed") is not seal_context.observed_integrity_passed
        or threshold_result is not seal_context.observed_threshold_comparison_passed
    ):
        raise _error("The post-hoc result cannot be authenticated", stage="report")


def _failure_report(args: argparse.Namespace, error: Exception) -> dict[str, Any]:
    commit = getattr(args, "candidate_commit", None)
    return {
        "acceptancePassed": None,
        "candidateCommit": commit if isinstance(commit, str) and len(commit) == 40 else None,
        "diagnosticThresholdComparisonPassed": None,
        "documents": 0,
        "evaluationCompleted": False,
        "evaluationRole": "post-hoc-diagnostic",
        "evidence": {
            "failure": acceptance._failure_details(error),
            "generatedAtUtc": acceptance.benchmark_core.utc_timestamp(),
            "phase": "post-hoc-diagnostic",
        },
        "expectedIdentitiesSha256": acceptance.sroie_policy.sha256_canonical([]),
        "finalDisposition": "failed",
        "identity": {},
        "identitySha256": acceptance.sroie_policy.sha256_canonical({}),
        "independentHoldout": False,
        "integrityPassed": False,
        "metrics": {},
        "metricsSha256": acceptance.sroie_policy.sha256_canonical({}),
        "protocol": POSTHOC_PROTOCOL,
        "rawRows": acceptance.sroie_policy.RAW_TEST_ROWS,
        "repairPromptedByHeldoutFailure": True,
        "runSucceeded": False,
        "schemaVersion": POSTHOC_SCHEMA_VERSION,
        "scoringProtocol": acceptance.PROTOCOL,
    }


def _recheck_publication_inputs(
    args: argparse.Namespace,
    *,
    consumed: ConsumedAttempt,
    frozen: acceptance.FrozenCandidate,
    identity: Mapping[str, Any],
    context: acceptance.CalibrationContext,
    policy: Any,
    runner_source: tuple[str, tuple[int | None, ...]],
    candidate_commit: CandidateCommit,
    repaired_test_sha256: str,
    repaired_test_object_id: tuple[int | None, int | None],
    phase_root_chain: Sequence[tuple[Path, tuple[int | None, int | None]]],
    artifact_paths: Mapping[str, Path],
    artifacts: Mapping[str, Mapping[str, Any]],
    backend_result_artifacts: Mapping[str, Mapping[str, Any]],
    corpus_identity: Mapping[str, Any],
    phase_root: Path,
) -> None:
    _recheck_consumed_attempt(args.original_terminal_result, consumed)
    _recheck_repaired_test(
        args.repaired_test_copy,
        expected_sha256=repaired_test_sha256,
        expected_object_id=repaired_test_object_id,
    )
    _recheck_v3_calibration(args, context, policy)
    acceptance._verify_persisted_runtime_context(context, frozen)
    if not acceptance._candidate_stable(args, identity):
        raise _error("The OCR candidate changed during diagnostics", stage="candidate")
    if _runner_source_snapshot() != runner_source:
        raise _error("The post-hoc runner changed during diagnostics", stage="source-identity")
    if _verify_candidate_commit(args.git_executable, args.candidate_commit) != candidate_commit:
        raise _error("The candidate commit changed during diagnostics", stage="candidate-commit")
    if not acceptance._parents_stable(phase_root_chain):
        raise _error("The post-hoc work directory changed", stage="arguments")
    _recheck_posthoc_artifacts(
        artifact_paths,
        evidence_root=phase_root,
        corpus_identity=corpus_identity,
        expected=artifacts,
    )
    _recheck_backend_result_artifacts(phase_root, backend_result_artifacts)


def _record_posthoc_failure(
    args: argparse.Namespace,
    output: Path,
    marker: Path,
    error: Exception,
) -> None:
    staged_path = output.with_name(output.name + ".staged")
    staged_path.unlink(missing_ok=True)
    failure = _failure_report(args, error)
    acceptance._validate_public_report_privacy(failure)
    if output.exists():
        raw = acceptance._canonical_bytes(failure)
        acceptance._atomic_replace(output, raw)
        _, persisted, _ = acceptance._read_regular_file(
            output,
            maximum_bytes=acceptance.MAX_REPORT_BYTES,
            name="post-hoc failure report",
            stage="report",
        )
        if persisted != raw:
            raise _error("The post-hoc failure report changed", stage="report")
        if marker.exists():
            acceptance._durable_remove_marker(marker)
        return
    if marker.exists():
        failure_staged = acceptance._stage_report(output, marker, failure)
        acceptance._finalize_report(output, marker, failure_staged)


def execute(args: argparse.Namespace) -> dict[str, Any]:
    _require_acknowledgement(args.acknowledge_consumed_holdout)
    _require_disjoint_posthoc_paths(args)
    candidate_commit = _verify_candidate_commit(args.git_executable, args.candidate_commit)
    runner_source = _runner_source_snapshot()
    consumed = _load_consumed_attempt(args.original_terminal_result)
    frozen = acceptance.verify_candidate(args)
    context, policy, identity = _load_v3_calibration(args, frozen)
    output, marker = acceptance._new_report_marker(args.output)
    try:
        phase_root, phase_root_chain = acceptance._create_fresh_snapshot_directory(
            acceptance._lexical_absolute(args.work_directory) / "posthoc-diagnostic"
        )
        snapshot = phase_root / "source-snapshot" / acceptance.sroie_policy.TEST_FILE
        parquet_sha256, repaired_source_object_id = _copy_repaired_test(
            args.repaired_test_copy, snapshot
        )
        corpus = acceptance.sroie.extract_sroie_corpus(
            snapshot, phase_root / "scoring-corpus", split="test"
        )
        if (
            corpus.source_row_count != acceptance.sroie_policy.RAW_TEST_ROWS
            or len(corpus.documents) != acceptance.sroie_policy.RAW_TEST_ROWS
            or corpus.excluded_row_indices
            or corpus.repaired_region_count != EXPECTED_REPAIRED_TEST_REGIONS
        ):
            raise _error(
                "The complete repaired test copy was not extracted", stage="posthoc-corpus"
            )
        acceptance._require_disjoint_source_images(
            context.source_image_sha256s, corpus.source_image_sha256s
        )
        expected_identities = acceptance._expected_identities(corpus)
        repair_identity = acceptance._validate_bbox_repair_audit(
            corpus.bbox_repair_audit,
            corpus_manifest=corpus.corpus_manifest,
            split="test",
            expected_raw_rows=acceptance.sroie_policy.RAW_TEST_ROWS,
            expected_identities=expected_identities,
            expected_repair_identity=acceptance._corpus_repair_identity(corpus),
        )
        repair_identity_sha256 = acceptance.sroie_policy.sha256_canonical(repair_identity)
        if (
            repair_identity != EXPECTED_REPAIR_IDENTITY
            or repair_identity_sha256 != EXPECTED_REPAIR_IDENTITY_SHA256
        ):
            raise _error(
                "The repaired holdout identity differs from the frozen post-hoc expectation",
                stage="bbox-repair-audit",
            )
        if (
            acceptance._validate_duplicate_audit(
                corpus.duplicate_audit,
                split="test",
                expected_raw_rows=acceptance.sroie_policy.RAW_TEST_ROWS,
                expected_identities=expected_identities,
                expected_source_payload_identities_sha256=repair_identity[
                    "sourcePayloadIdentitiesSha256"
                ],
                expected_scoring_annotation_identities_sha256=repair_identity[
                    "scoringAnnotationIdentitiesSha256"
                ],
            )
            != corpus.source_image_sha256s
        ):
            raise _error("The repaired holdout duplicate audit changed", stage="duplicate-audit")
        corpus_identity = acceptance._corpus_identity(corpus, parquet_sha256=parquet_sha256)
        artifact_paths = _posthoc_artifact_paths(corpus, snapshot)
        artifacts = _capture_posthoc_artifacts(
            artifact_paths,
            evidence_root=phase_root,
            corpus_identity=corpus_identity,
        )
        repaired_holdout = _public_repaired_binding(
            corpus_identity,
            repair_identity,
            repair_identity_sha256,
        )
        expected_identities_json = acceptance.sroie_policy.canonical_json(
            [dict(item) for item in expected_identities]
        )
        policy_value_sha256 = acceptance.sroie_policy.sha256_canonical(policy.value)
        expected_cpu_runtime_sha256 = frozen.runtimes["cpu"]["executableSha256"]
        expected_directml_runtime_sha256 = frozen.runtimes["directml"][
            "executableSha256"
        ]
        expected_worker_sha256 = frozen.worker_sha256
        pre_run_binding = _pre_run_binding(
            candidate_commit=candidate_commit,
            runner_sha256=runner_source[0],
            consumed=consumed,
            calibration_context=context,
            policy=policy,
            identity=identity,
            expected_identities=expected_identities,
            repaired_holdout=repaired_holdout,
            artifacts=artifacts,
        )
        pre_run_binding_sha256 = acceptance.sroie_policy.sha256_canonical(pre_run_binding)

        determinism = acceptance._create_determinism_corpus(
            corpus, phase_root / "views", role="posthoc"
        )
        backends = (
            frozen.cpu_backend,
            frozen.directml_backend,
            acceptance.benchmark_core.Backend("hybrid", frozen.directml_backend.python_executable),
        )
        runs = [
            acceptance._benchmark_backend(
                frozen,
                corpus,
                determinism,
                backend,
                phase_root / "results" / backend.requested_provider,
                args.timeout_seconds,
            )
            for backend in backends
        ]
        policy_comparisons: dict[str, Any] = {}
        for run in runs:
            try:
                policy_comparisons[run["requestedProvider"]] = (
                    acceptance.sroie_policy.evaluate_confirmatory(
                        policy,
                        run["metrics"],
                        expected_identities=expected_identities,
                    )
                )
            except acceptance.sroie_policy.PolicyError as exc:
                raise _error(str(exc), stage="diagnostic-quality") from exc
        checks, confidence, determinism_checks = _posthoc_integrity(
            args, frozen, identity, corpus, determinism, runs
        )
        integrity_passed = all(checks.values())
        raw_comparison_passed = all(
            value.get("passed") is True for value in policy_comparisons.values()
        )
        comparison_passed = integrity_passed and raw_comparison_passed

        backend_result_artifacts = _capture_backend_result_artifacts(phase_root)
        public_runs = [acceptance._public_run(run) for run in runs]
        _validate_backend_result_bindings(public_runs, backend_result_artifacts)
        _recheck_publication_inputs(
            args,
            consumed=consumed,
            frozen=frozen,
            identity=identity,
            context=context,
            policy=policy,
            runner_source=runner_source,
            candidate_commit=candidate_commit,
            repaired_test_sha256=parquet_sha256,
            repaired_test_object_id=repaired_source_object_id,
            phase_root_chain=phase_root_chain,
            artifact_paths=artifact_paths,
            artifacts=artifacts,
            backend_result_artifacts=backend_result_artifacts,
            corpus_identity=corpus_identity,
            phase_root=phase_root,
        )

        cross_backend_integrity = {
            "checks": checks,
            "confidenceParity": confidence,
        }
        determinism_evidence = {
            "checksByBackend": determinism_checks,
            "repetitions": acceptance.DETERMINISM_REPETITIONS,
        }
        diagnostic_comparison = {
            "evaluations": policy_comparisons,
            "allBackendThresholdsPassed": raw_comparison_passed,
            "isAcceptanceDecision": False,
            "reportableResultPassed": comparison_passed,
        }
        evidence = {
            "artifactPublication": copy.deepcopy(pre_run_binding["artifactPublication"]),
            "artifacts": copy.deepcopy(pre_run_binding["artifacts"]),
            "backendResultArtifacts": copy.deepcopy(backend_result_artifacts),
            "backends": copy.deepcopy(public_runs),
            "candidate": copy.deepcopy(pre_run_binding["candidate"]),
            "consumedAttempt": copy.deepcopy(pre_run_binding["consumedAttempt"]),
            "crossBackendIntegrity": copy.deepcopy(cross_backend_integrity),
            "determinism": copy.deepcopy(determinism_evidence),
            "diagnosticPolicyComparison": copy.deepcopy(diagnostic_comparison),
            "execution": {
                "backendOrder": ["cpu", "directml", "hybrid"],
                "threads": 0,
            },
            "generatedAtUtc": acceptance.benchmark_core.utc_timestamp(),
            "holdoutStatus": {
                "independentHoldout": False,
                "oneShotAttemptConsumed": True,
                "posthocOnly": True,
            },
            "repairedHoldout": copy.deepcopy(pre_run_binding["repairedHoldout"]),
            "v3Calibration": copy.deepcopy(pre_run_binding["v3Calibration"]),
        }
        report = {
            "acceptancePassed": None,
            "candidateCommit": pre_run_binding["candidateCommit"],
            "diagnosticThresholdComparisonPassed": comparison_passed,
            "documents": len(corpus.documents),
            "evaluationCompleted": True,
            "evaluationRole": "post-hoc-diagnostic",
            "evidence": evidence,
            "expectedIdentitiesSha256": pre_run_binding["expectedIdentitiesSha256"],
            "finalDisposition": (
                "diagnostic-complete" if integrity_passed else "diagnostic-integrity-failed"
            ),
            "identity": copy.deepcopy(pre_run_binding["identity"]),
            "identitySha256": pre_run_binding["identitySha256"],
            "independentHoldout": False,
            "integrityPassed": integrity_passed,
            "metrics": copy.deepcopy(runs[0]["metrics"]),
            "metricsSha256": acceptance.sroie_policy.sha256_canonical(runs[0]["metrics"]),
            "protocol": POSTHOC_PROTOCOL,
            "rawRows": corpus.source_row_count,
            "repairPromptedByHeldoutFailure": True,
            "runSucceeded": True,
            "schemaVersion": POSTHOC_SCHEMA_VERSION,
            "scoringProtocol": acceptance.PROTOCOL,
        }
        seal_context = PosthocSealContext(
            validated_policy=policy,
            validated_policy_value_sha256=policy_value_sha256,
            expected_identities_json=expected_identities_json,
            pre_run_binding_sha256=pre_run_binding_sha256,
            expected_cpu_runtime_sha256=expected_cpu_runtime_sha256,
            expected_directml_runtime_sha256=expected_directml_runtime_sha256,
            expected_worker_sha256=expected_worker_sha256,
            observed_integrity_passed=integrity_passed,
            observed_threshold_comparison_passed=comparison_passed,
            backends_sha256=acceptance.sroie_policy.sha256_canonical(public_runs),
            backend_result_artifacts_sha256=acceptance.sroie_policy.sha256_canonical(
                backend_result_artifacts
            ),
            cross_backend_integrity_sha256=acceptance.sroie_policy.sha256_canonical(
                cross_backend_integrity
            ),
            determinism_sha256=acceptance.sroie_policy.sha256_canonical(
                determinism_evidence
            ),
            evaluations_sha256=acceptance.sroie_policy.sha256_canonical(
                policy_comparisons
            ),
            metrics_sha256=acceptance.sroie_policy.sha256_canonical(runs[0]["metrics"]),
            report_sha256=acceptance.sroie_policy.sha256_canonical(report),
        )
        _validate_posthoc_report(report, seal_context)
        staged = acceptance._stage_report(output, marker, report)
        _recheck_publication_inputs(
            args,
            consumed=consumed,
            frozen=frozen,
            identity=identity,
            context=context,
            policy=policy,
            runner_source=runner_source,
            candidate_commit=candidate_commit,
            repaired_test_sha256=parquet_sha256,
            repaired_test_object_id=repaired_source_object_id,
            phase_root_chain=phase_root_chain,
            artifact_paths=artifact_paths,
            artifacts=artifacts,
            backend_result_artifacts=backend_result_artifacts,
            corpus_identity=corpus_identity,
            phase_root=phase_root,
        )
        acceptance._finalize_report(output, marker, staged)
        persisted, persisted_sha256 = acceptance._strict_json(
            output,
            maximum_bytes=acceptance.MAX_REPORT_BYTES,
            name="post-hoc report",
        )
        if (
            persisted_sha256 != staged.sha256
            or acceptance.sroie_policy.canonical_json(persisted)
            != acceptance.sroie_policy.canonical_json(report)
            or marker.exists()
        ):
            raise _error("The finalized post-hoc report changed", stage="report")
        _validate_posthoc_report(persisted, seal_context)
        return persisted
    except Exception as exc:
        _record_posthoc_failure(args, output, marker, exc)
        raise


def _argument_path(value: str) -> Path:
    path = Path(value)
    return path if path.is_absolute() else _INVOCATION_CWD / path


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--acknowledge-consumed-holdout", required=True)
    parser.add_argument("--candidate-commit", required=True)
    parser.add_argument("--git-executable", type=_argument_path, required=True)
    parser.add_argument("--worker", type=_argument_path, required=True)
    parser.add_argument("--model-pack", type=_argument_path, required=True)
    parser.add_argument("--cpu-python", type=_argument_path, required=True)
    parser.add_argument("--directml-python", type=_argument_path, required=True)
    parser.add_argument("--calibration-report", type=_argument_path, required=True)
    parser.add_argument("--calibration-evidence-root", type=_argument_path, required=True)
    parser.add_argument("--policy", type=_argument_path, required=True)
    parser.add_argument("--original-terminal-result", type=_argument_path, required=True)
    parser.add_argument("--repaired-test-copy", type=_argument_path, required=True)
    parser.add_argument("--work-directory", type=_argument_path, required=True)
    parser.add_argument("--output", type=_argument_path, required=True)
    parser.add_argument("--timeout-seconds", type=float, default=7200.0)
    return parser.parse_args(argv)


def _diagnostic_exit_code(report: Mapping[str, Any]) -> int:
    """Return success for a sound completed diagnostic, regardless of its measured quality."""

    return 0 if report.get("runSucceeded") is True and report.get("integrityPassed") is True else 2


def main(argv: Sequence[str] | None = None) -> int:
    try:
        arguments = parse_arguments(argv)
        if not math.isfinite(arguments.timeout_seconds) or arguments.timeout_seconds <= 0:
            raise _error("The timeout must be positive and finite", stage="arguments")
        if not _TRUSTED_BOOTSTRAPPED:
            acceptance._sanitize_outer_environment()
        report = execute(arguments)
    except (
        acceptance.AcceptanceError,
        acceptance.benchmark_core.BenchmarkError,
        acceptance.generic_policy.PolicyError,
        acceptance.sroie_policy.PolicyError,
        OSError,
        ValueError,
    ) as exc:
        print(f"SROIE post-hoc diagnostic failed: {exc}", file=sys.stderr)
        return 1
    print(acceptance.sroie_policy.canonical_json(report))
    return _diagnostic_exit_code(report)


if __name__ == "__main__":
    raise SystemExit(main())
