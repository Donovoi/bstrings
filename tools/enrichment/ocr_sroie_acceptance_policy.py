#!/usr/bin/env python3
"""Frozen quality policy for the pinned ICDAR 2019 SROIE OCR acceptance run.

This module deliberately contains no dataset reader.  It validates canonical
reports and derives/evaluates thresholds through the generic, count-recomputing
OCR policy implementation.  The held-out test parquet is therefore never a
policy input.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import stat
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import ocr_acceptance_policy as generic_policy

SCHEMA_VERSION = 1
METRICS_REPORT_SCHEMA_VERSION = 1
POLICY_ID = "bstrings-icdar2019-sroie-train-test-ocr-v1"
PROTOCOL = "bstrings-icdar2019-sroie-train-calibration-test-one-shot-v1"
DATASET_ID = "jsdnrs/ICDAR2019-SROIE"
DATASET_REVISION = "bffe40c26759f3376ec2b3ae9031dbba54cd587c"
RAW_TRAIN_ROWS = 626
RAW_TEST_ROWS = 361
TRAIN_FILE = "data/train-00000-of-00001.parquet"
TRAIN_BYTES = 318_620_215
TRAIN_SHA256 = "b18c16b4d8481e5e4537a1700e4616907fe4acd92d6362a7e430b0e866213887"
TEST_FILE = "data/test-00000-of-00001.parquet"
TEST_BYTES = 191_045_976
TEST_SHA256 = "04f8f31b45944cc6e6459a7a95c851a721fc93ffec0a5c29ece9ded734a684c2"
MAX_POLICY_BYTES = 2 * 1024 * 1024
MAX_METRICS_REPORT_BYTES = 128 * 1024 * 1024
SHA256_PATTERN = re.compile(r"[0-9a-f]{64}")

# Dataset Viewer schema metadata is frozen as a descriptor.  The adapter still
# validates the physical Arrow schema from train and, only after the one-shot
# claim, from the verified test snapshot.
SCHEMA_DESCRIPTOR: dict[str, Any] = {
    "bboxes": [["int64"]],
    "entities": {
        "address": "string",
        "company": "string",
        "date": "string",
        "total": "string",
    },
    "image": "Image",
    "image_size": {"height": "int64", "width": "int64"},
    "key": "string",
    "words": ["string"],
}
SCHEMA_DESCRIPTOR_SHA256 = generic_policy.sha256_canonical(SCHEMA_DESCRIPTOR)

METRIC_SPECS = generic_policy.METRIC_SPECS
METRIC_SPEC_ROWS = tuple(
    {
        "absoluteBoundary": spec.absolute_boundary,
        "calibrationMargin": spec.calibration_margin,
        "direction": spec.direction,
        "name": spec.name,
    }
    for spec in METRIC_SPECS
)
METRIC_SPECS_SHA256 = generic_policy.sha256_canonical(METRIC_SPEC_ROWS)


class PolicyError(ValueError):
    """A frozen SROIE policy or its bound evidence is invalid."""


@dataclass(frozen=True)
class ValidatedPolicy:
    value: dict[str, Any]
    file_sha256: str


def canonical_json(value: Any) -> str:
    try:
        return generic_policy.canonical_json(value)
    except generic_policy.PolicyError as exc:
        raise PolicyError(str(exc)) from exc


def sha256_canonical(value: Any) -> str:
    return generic_policy.sha256_canonical(value)


def dataset_identity() -> dict[str, Any]:
    return {
        "datasetId": DATASET_ID,
        "datasetRevision": DATASET_REVISION,
        "schemaDescriptorSha256": SCHEMA_DESCRIPTOR_SHA256,
        "test": {
            "bytes": TEST_BYTES,
            "file": TEST_FILE,
            "rows": RAW_TEST_ROWS,
            "sha256": TEST_SHA256,
        },
        "train": {
            "bytes": TRAIN_BYTES,
            "file": TRAIN_FILE,
            "rows": RAW_TRAIN_ROWS,
            "sha256": TRAIN_SHA256,
        },
    }


def _mapping(value: Any, *, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise PolicyError(f"The {name} is not an object")
    return value


def _sha256(value: Any, *, name: str) -> str:
    if not isinstance(value, str) or SHA256_PATTERN.fullmatch(value) is None:
        raise PolicyError(f"The {name} is not a lowercase SHA-256")
    return value


def _strict_json(
    path: Path, *, maximum_bytes: int = MAX_POLICY_BYTES, name: str = "SROIE policy"
) -> tuple[dict[str, Any], str]:
    lexical = path.expanduser()
    if not lexical.is_absolute():
        lexical = Path.cwd() / lexical
    lexical = Path(os.path.abspath(os.fspath(lexical)))

    def unsafe_attributes(value: os.stat_result) -> bool:
        return bool(
            getattr(value, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        )

    def identity(value: os.stat_result) -> tuple[int | None, ...]:
        return tuple(
            getattr(value, field, None)
            for field in ("st_dev", "st_ino", "st_size", "st_mtime_ns")
        )

    def directory_id(value: os.stat_result) -> tuple[int | None, int | None]:
        return (getattr(value, "st_dev", None), getattr(value, "st_ino", None))

    try:
        parent_identities: list[tuple[Path, tuple[int | None, ...]]] = []
        for parent in reversed(lexical.parents):
            parent_stat = os.lstat(parent)
            if (
                not stat.S_ISDIR(parent_stat.st_mode)
                or stat.S_ISLNK(parent_stat.st_mode)
                or unsafe_attributes(parent_stat)
            ):
                raise PolicyError(f"The {name} has an unsafe parent")
            parent_identities.append((parent, directory_id(parent_stat)))
        lexical_stat = os.lstat(lexical)
        if (
            not stat.S_ISREG(lexical_stat.st_mode)
            or stat.S_ISLNK(lexical_stat.st_mode)
            or unsafe_attributes(lexical_stat)
            or lexical_stat.st_size > maximum_bytes
        ):
            raise PolicyError(f"The {name} is unavailable, unsafe, or too large")
        descriptor = os.open(
            lexical,
            os.O_RDONLY
            | getattr(os, "O_BINARY", 0)
            | getattr(os, "O_NOFOLLOW", 0),
        )
        try:
            before = os.fstat(descriptor)
            if (
                not stat.S_ISREG(before.st_mode)
                or unsafe_attributes(before)
                or identity(before) != identity(lexical_stat)
            ):
                raise PolicyError(f"The {name} changed before it was read")
            raw = bytearray()
            while len(raw) <= maximum_bytes:
                chunk = os.read(descriptor, min(4 * 1024 * 1024, maximum_bytes + 1 - len(raw)))
                if not chunk:
                    break
                raw.extend(chunk)
            after = os.fstat(descriptor)
        finally:
            os.close(descriptor)
        final_stat = os.lstat(lexical)
        parents_stable = True
        for parent, expected in parent_identities:
            current = os.lstat(parent)
            if (
                not stat.S_ISDIR(current.st_mode)
                or stat.S_ISLNK(current.st_mode)
                or unsafe_attributes(current)
                or directory_id(current) != expected
            ):
                parents_stable = False
                break
        if (
            len(raw) > maximum_bytes
            or identity(before) != identity(after)
            or identity(after) != identity(final_stat)
            or not stat.S_ISREG(final_stat.st_mode)
            or stat.S_ISLNK(final_stat.st_mode)
            or unsafe_attributes(final_stat)
            or len(raw) != after.st_size
            or not parents_stable
        ):
            raise PolicyError(f"The {name} changed while it was read")
    except PolicyError:
        raise
    except OSError as exc:
        raise PolicyError(f"The {name} is unavailable, unsafe, or too large") from exc

    def reject_pairs(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
        output: dict[str, Any] = {}
        for key, value in pairs:
            if key in output:
                raise PolicyError(f"Duplicate {name} field: {key}")
            output[key] = value
        return output

    def reject_constant(value: str) -> None:
        raise PolicyError(f"Non-finite JSON constant is forbidden: {value}")

    raw_bytes = bytes(raw)
    try:
        value = json.loads(
            raw_bytes,
            object_pairs_hook=reject_pairs,
            parse_constant=reject_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PolicyError(f"The {name} is not strict UTF-8 JSON") from exc
    if not isinstance(value, dict):
        raise PolicyError(f"The {name} root is not an object")
    if raw_bytes != (canonical_json(value) + "\n").encode("utf-8"):
        raise PolicyError(f"The {name} is not canonical JSON")
    return value, hashlib.sha256(raw_bytes).hexdigest()


def _validate_identity(identity: Any) -> dict[str, Any]:
    value = dict(_mapping(identity, name="candidate identity"))
    expected_keys = {
        "benchmark",
        "calibrationCorpus",
        "dataset",
        "dependencies",
        "engine",
        "protocol",
        "runtimeProfiles",
        "schemaVersion",
        "worker",
    }
    if set(value) != expected_keys:
        raise PolicyError("The candidate identity schema changed")
    if (
        value.get("schemaVersion") != SCHEMA_VERSION
        or value.get("protocol") != PROTOCOL
        or value.get("dataset") != dataset_identity()
    ):
        raise PolicyError("The candidate identity protocol or dataset changed")
    benchmark = _mapping(value.get("benchmark"), name="benchmark identity")
    required_sources = {
        "acceptanceWrapperSha256",
        "cordScorerSha256",
        "genericBenchmarkSha256",
        "genericPolicySha256",
        "scoringConstantsSha256",
        "sroieAdapterSha256",
        "sroiePolicySha256",
    }
    if set(benchmark) != required_sources:
        raise PolicyError("The benchmark source identity schema changed")
    for name, digest in benchmark.items():
        _sha256(digest, name=name)
    corpus = _mapping(value.get("calibrationCorpus"), name="calibration corpus identity")
    if set(corpus) != {
        "duplicateAuditSha256",
        "excludedRows",
        "corpusManifestSha256",
        "imageIdentitiesSha256",
        "parquetSha256",
        "selectedDocuments",
        "sourceImageDigestsSha256",
        "sourceRows",
        "workerManifestSha256",
    }:
        raise PolicyError("The calibration corpus identity schema changed")
    selected_documents = corpus.get("selectedDocuments")
    excluded_rows = corpus.get("excludedRows")
    if (
        type(corpus.get("sourceRows")) is not int
        or corpus.get("sourceRows") != RAW_TRAIN_ROWS
        or type(selected_documents) is not int
        or not 0 < selected_documents <= RAW_TRAIN_ROWS
        or type(excluded_rows) is not int
        or excluded_rows < 0
        or selected_documents + excluded_rows != RAW_TRAIN_ROWS
    ):
        raise PolicyError("The calibration corpus row accounting changed")
    for name in (
        "corpusManifestSha256",
        "duplicateAuditSha256",
        "imageIdentitiesSha256",
        "parquetSha256",
        "sourceImageDigestsSha256",
        "workerManifestSha256",
    ):
        _sha256(corpus.get(name), name=name)
    if corpus.get("parquetSha256") != TRAIN_SHA256:
        raise PolicyError("The calibration parquet identity changed")
    runtime_profiles = _mapping(value.get("runtimeProfiles"), name="runtime profiles")
    if set(runtime_profiles) != {"benchmark", "cpu", "directml"}:
        raise PolicyError("The runtime profile set changed")
    for profile_name, raw_profile in runtime_profiles.items():
        profile = _mapping(raw_profile, name=f"{profile_name} runtime profile")
        if set(profile) != {
            "executableSha256",
            "inventorySha256",
            "pythonVersion",
            "requestedProvider",
        }:
            raise PolicyError(f"The {profile_name} runtime identity schema changed")
        if profile.get("requestedProvider") != profile_name:
            raise PolicyError(f"The {profile_name} runtime provider changed")
        if not isinstance(profile.get("pythonVersion"), str) or not profile["pythonVersion"]:
            raise PolicyError(f"The {profile_name} Python version is invalid")
        _sha256(profile.get("executableSha256"), name=f"{profile_name} executable")
        _sha256(profile.get("inventorySha256"), name=f"{profile_name} inventory")
    worker = _mapping(value.get("worker"), name="worker identity")
    if set(worker) != {"sha256"}:
        raise PolicyError("The worker identity schema changed")
    _sha256(worker.get("sha256"), name="worker")
    engine = _mapping(value.get("engine"), name="engine identity")
    if set(engine) != {"modelId", "modelPackSha256", "modelRevision", "name", "version"}:
        raise PolicyError("The engine identity schema changed")
    _sha256(engine.get("modelPackSha256"), name="model pack")
    if any(
        not isinstance(engine.get(name), str) or not engine[name]
        for name in (
            "modelId",
            "modelRevision",
            "name",
            "version",
        )
    ):
        raise PolicyError("The engine string identity is invalid")
    dependencies = _mapping(value.get("dependencies"), name="dependency identity")
    if set(dependencies) != {"geos", "pyarrow", "shapely"} or any(
        not isinstance(item, str) or not item for item in dependencies.values()
    ):
        raise PolicyError("The dependency identity schema changed")
    canonical_json(value)
    return value


def _validate_report(
    report: Mapping[str, Any],
    *,
    expected_identity: Mapping[str, Any],
    expected_identities: Sequence[Mapping[str, Any]],
) -> dict[str, float]:
    expected_keys = {
        "acceptancePassed",
        "documents",
        "evaluationCompleted",
        "evaluationRole",
        "evidence",
        "expectedIdentitiesSha256",
        "identity",
        "identitySha256",
        "integrityPassed",
        "finalDisposition",
        "metrics",
        "metricsSha256",
        "protocol",
        "rawRows",
        "runSucceeded",
        "schemaVersion",
    }
    if set(report) != expected_keys:
        raise PolicyError("The calibration report schema changed")
    identity = _validate_identity(report.get("identity"))
    frozen_identity = _validate_identity(expected_identity)
    if canonical_json(identity) != canonical_json(frozen_identity):
        raise PolicyError("The calibration report candidate identity changed")
    if (
        report.get("schemaVersion") != METRICS_REPORT_SCHEMA_VERSION
        or report.get("protocol") != PROTOCOL
        or report.get("evaluationRole") != "calibration"
        or report.get("rawRows") != RAW_TRAIN_ROWS
        or report.get("documents") != len(expected_identities)
        or report.get("documents") != identity["calibrationCorpus"]["selectedDocuments"]
        or report.get("evaluationCompleted") is not True
        or report.get("finalDisposition") != "accepted"
        or report.get("runSucceeded") is not True
        or report.get("integrityPassed") is not True
        or report.get("acceptancePassed") is not True
    ):
        raise PolicyError("The calibration report did not pass its frozen protocol")
    if report.get("identitySha256") != sha256_canonical(identity):
        raise PolicyError("The calibration report identity digest changed")
    expected_identity_rows = [dict(item) for item in expected_identities]
    if report.get("expectedIdentitiesSha256") != sha256_canonical(expected_identity_rows):
        raise PolicyError("The calibration image identities changed")
    metrics = _mapping(report.get("metrics"), name="calibration metrics")
    if report.get("metricsSha256") != sha256_canonical(metrics):
        raise PolicyError("The calibration metrics digest changed")
    try:
        return generic_policy.extract_measurements_for_identities(
            metrics,
            expected_identities=expected_identity_rows,
        )
    except generic_policy.PolicyError as exc:
        raise PolicyError(str(exc)) from exc


def build_policy(
    *,
    calibration_report: Mapping[str, Any],
    calibration_report_sha256: str,
    expected_identity: Mapping[str, Any],
    expected_identities: Sequence[Mapping[str, Any]],
    frozen_at_utc: str,
) -> dict[str, Any]:
    _sha256(calibration_report_sha256, name="calibration report")
    try:
        frozen_at_utc = generic_policy.validate_utc_timestamp(frozen_at_utc)
    except generic_policy.PolicyError as exc:
        raise PolicyError(str(exc)) from exc
    measurements = _validate_report(
        calibration_report,
        expected_identity=expected_identity,
        expected_identities=expected_identities,
    )
    try:
        checks = generic_policy.absolute_floor_checks(measurements)
        failed = [name for name, check in checks.items() if check["passed"] is not True]
        if failed:
            raise PolicyError("Calibration failed absolute quality floors: " + ", ".join(failed))
        thresholds = generic_policy.derive_thresholds(measurements)
    except generic_policy.PolicyError as exc:
        raise PolicyError(str(exc)) from exc
    identity = _validate_identity(expected_identity)
    identities_sha256 = sha256_canonical([dict(item) for item in expected_identities])
    return {
        "calibration": {
            "documents": len(expected_identities),
            "rawRows": RAW_TRAIN_ROWS,
            "expectedIdentitiesSha256": identities_sha256,
            "measurements": measurements,
            "measurementsSha256": sha256_canonical(measurements),
            "reportSha256": calibration_report_sha256,
        },
        "candidateIdentity": identity,
        "candidateIdentitySha256": sha256_canonical(identity),
        "dataset": dataset_identity(),
        "frozenAtUtc": frozen_at_utc,
        "metricSpecs": list(METRIC_SPEC_ROWS),
        "metricSpecsSha256": METRIC_SPECS_SHA256,
        "policyId": POLICY_ID,
        "protocol": PROTOCOL,
        "schemaVersion": SCHEMA_VERSION,
        "thresholds": thresholds,
    }


def encoded_policy(value: Mapping[str, Any]) -> bytes:
    return (canonical_json(value) + "\n").encode("utf-8")


def publish_policy(path: Path, value: Mapping[str, Any]) -> None:
    output = path.expanduser()
    if not output.is_absolute():
        output = Path.cwd() / output
    output = Path(os.path.abspath(os.fspath(output)))
    output.parent.mkdir(parents=True, exist_ok=True)
    raw = encoded_policy(value)
    try:
        descriptor = os.open(output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    except FileExistsError as exc:
        raise PolicyError("The policy output already exists") from exc
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(raw)
            handle.flush()
            os.fsync(handle.fileno())
    except Exception:
        output.unlink(missing_ok=True)
        raise


def validate_policy(
    path: Path,
    *,
    calibration_report_path: Path,
    expected_identity: Mapping[str, Any],
    expected_identities: Sequence[Mapping[str, Any]],
) -> ValidatedPolicy:
    value, file_sha256 = _strict_json(path)
    calibration_report, calibration_report_sha256 = _strict_json(
        calibration_report_path,
        maximum_bytes=MAX_METRICS_REPORT_BYTES,
        name="calibration report",
    )
    report_measurements = _validate_report(
        calibration_report,
        expected_identity=expected_identity,
        expected_identities=expected_identities,
    )
    expected_keys = {
        "calibration",
        "candidateIdentity",
        "candidateIdentitySha256",
        "dataset",
        "frozenAtUtc",
        "metricSpecs",
        "metricSpecsSha256",
        "policyId",
        "protocol",
        "schemaVersion",
        "thresholds",
    }
    if set(value) != expected_keys:
        raise PolicyError("The SROIE policy schema changed")
    try:
        generic_policy.validate_utc_timestamp(value.get("frozenAtUtc"))
    except generic_policy.PolicyError as exc:
        raise PolicyError(str(exc)) from exc
    if (
        value.get("schemaVersion") != SCHEMA_VERSION
        or value.get("policyId") != POLICY_ID
        or value.get("protocol") != PROTOCOL
        or value.get("dataset") != dataset_identity()
        or value.get("metricSpecs") != list(METRIC_SPEC_ROWS)
        or value.get("metricSpecsSha256") != METRIC_SPECS_SHA256
    ):
        raise PolicyError("The SROIE policy protocol, dataset, or metrics changed")
    identity = _validate_identity(value.get("candidateIdentity"))
    expected = _validate_identity(expected_identity)
    if canonical_json(identity) != canonical_json(expected):
        raise PolicyError("The live candidate identity differs from calibration")
    if value.get("candidateIdentitySha256") != sha256_canonical(identity):
        raise PolicyError("The policy candidate identity digest changed")
    calibration = _mapping(value.get("calibration"), name="calibration policy")
    if set(calibration) != {
        "documents",
        "expectedIdentitiesSha256",
        "measurements",
        "measurementsSha256",
        "rawRows",
        "reportSha256",
    }:
        raise PolicyError("The policy calibration schema changed")
    expected_rows = [dict(item) for item in expected_identities]
    if (
        calibration.get("documents") != len(expected_rows)
        or calibration.get("rawRows") != RAW_TRAIN_ROWS
        or calibration.get("expectedIdentitiesSha256") != sha256_canonical(expected_rows)
        or calibration.get("reportSha256") != calibration_report_sha256
    ):
        raise PolicyError("The policy calibration evidence changed")
    measurements = _mapping(calibration.get("measurements"), name="calibration measurements")
    if (
        calibration.get("measurementsSha256") != sha256_canonical(measurements)
        or canonical_json(measurements) != canonical_json(report_measurements)
    ):
        raise PolicyError("The policy calibration measurement digest changed")
    try:
        validated_measurements = generic_policy.validate_measurements(measurements)
        thresholds = generic_policy.derive_thresholds(validated_measurements)
    except generic_policy.PolicyError as exc:
        raise PolicyError(str(exc)) from exc
    if value.get("thresholds") != thresholds:
        raise PolicyError("The policy thresholds are not derived from calibration")
    return ValidatedPolicy(value=value, file_sha256=file_sha256)


def evaluate_confirmatory(
    validated_policy: ValidatedPolicy,
    metrics: Mapping[str, Any],
    *,
    expected_identities: Sequence[Mapping[str, Any]],
) -> dict[str, Any]:
    if not isinstance(validated_policy, ValidatedPolicy):
        raise PolicyError("A validated SROIE policy is required")
    try:
        measurements = generic_policy.extract_measurements_for_identities(
            metrics,
            expected_identities=[dict(item) for item in expected_identities],
        )
        evaluated = generic_policy.evaluate_thresholds(
            measurements,
            validated_policy.value["thresholds"],
        )
        floors = generic_policy.absolute_floor_checks(measurements)
    except generic_policy.PolicyError as exc:
        raise PolicyError(str(exc)) from exc
    floors_passed = all(check["passed"] is True for check in floors.values())
    return {
        **evaluated,
        "absoluteFloorChecks": floors,
        "absoluteFloorsPassed": floors_passed,
        "measurements": measurements,
        "passed": evaluated["passed"] is True and floors_passed,
        "policySha256": validated_policy.file_sha256,
    }
