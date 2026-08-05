#!/usr/bin/env python3
"""Pre-registered CORD OCR quality policy derivation and evaluation."""

from __future__ import annotations

import hashlib
import json
import math
import os
import re
import statistics
from collections.abc import Mapping, Sequence
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Literal

SCHEMA_VERSION = 1
POLICY_ID = "bstrings-cord-v2-train-image-hash-600-200-v1"
CALIBRATION_DOCUMENTS = 600
CONFIRMATORY_DOCUMENTS = 200
MAX_POLICY_BYTES = 2 * 1024 * 1024
MAX_METRICS_REPORT_BYTES = 256 * 1024 * 1024
METRICS_REPORT_SCHEMA_VERSION = 1
DATASET_ID = "naver-clova-ix/cord-v2"
DATASET_REVISION = "7f0115a4b758a71d6473b8d085751692da2fef98"
TRAIN_SHARDS = (
    (
        "train-00000-of-00004-b4aaeceff1d90ecb.parquet",
        490_224_630,
        "da3994eee1bf9bd3c57f0d53a72c3a6812c8696c5ba26245987949ddf73483cc",
    ),
    (
        "train-00001-of-00004-7dbbe248962764c5.parquet",
        441_418_432,
        "cce4def16a0d6a6c75f80be712f7494c56c318a8829b712f5c62650155c9e58e",
    ),
    (
        "train-00002-of-00004-688fe1305a55e5cc.parquet",
        443_802_181,
        "591e2db8fe8b1d364b054f46e8c375b7f00e72578914ba46a573b6858162cab2",
    ),
    (
        "train-00003-of-00004-2d0cd200555ed7fd.parquet",
        455_555_434,
        "1ffd9de8d6fbcee7630fd4cdfedff05b9b7fabc0fae4fc17557c5fe7cf178748",
    ),
)
TRAIN_SHARD_SHA256 = tuple(shard[2] for shard in TRAIN_SHARDS)
SELECTION_MANIFEST_BYTES = 124_181
SELECTION_MANIFEST_SHA256 = "4deb7deec2a5ee69e182c9030ef0e1dee5bdf5960a2f9bec9ba5f404293fd6e1"
SELECTION_ENTRIES_SHA256 = {
    "calibration": "ece7bf666fccc109f0d65449bcd0f6fdbdb9b8f326df9dc07418553025de85e9",
    "complete": "b7cd1b3b9ee19a6c617adffcec4cfb3e5e0a3567e1e08b377ea525f0dd012a8c",
    "confirmatory": "c1415ecc2cd56cff29b9896edcf753200de16c256f41f62edcead0a1236579b0",
}
ENGINE_NAME = "rapidocr"
ENGINE_VERSION = "3.9.2"
CORE_SCORER_SCHEMA_VERSION = 5
CORE_SCORER_PROTOCOL = "bstrings-derived-CORD-v2-rowid-constrained-segmentation-tolerant-OCR-v5"
CORE_SCORER_SHA256 = "e9faec03c4477c4b4f039803f270f5a76aebd5e16bd0eeedee4d30a21e6903bf"
GENERIC_BENCHMARK_SHA256 = "b79cd6997201585798a72af26ca1d5d886c0d1b2909d5c31b01e806259d0e49a"
SCORING_CONSTANTS_SHA256 = "cf5671a4d144b544d406e180b1b5d1c62b0f8d1701819ba53dff1357388cd0b5"
GEOS_VERSION = "3.13.1"
MODEL_ID = "PaddlePaddle/PP-OCRv6-medium-onnx+RapidAI/RapidOCR-classifier"
MODEL_REVISION = (
    "det-61323801669c338b7891481ec7bac61ce31b576a_"
    "rec-50c7eacafc52fa7bcf4194e8cd08e46f8558504b_"
    "cls-390c78b5e9a2e69bd9e33be9cd77f7b2622ce55b"
)
MODEL_PACK_SHA256 = "b3b683eb29ec09e9da835e09fb4470792af7702cfc6cee40f2725c232d258534"
SHA256_PATTERN = re.compile(r"[0-9a-f]{64}")
UTC_TIMESTAMP_PATTERN = re.compile(r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z")


class PolicyError(RuntimeError):
    """The calibration policy or its input metrics violated the frozen contract."""


@dataclass(frozen=True)
class MetricSpec:
    name: str
    direction: Literal["higher", "lower"]
    absolute_boundary: float
    calibration_margin: float


@dataclass(frozen=True)
class ValidatedPolicy:
    file_sha256: str
    value: dict[str, Any]


@dataclass(frozen=True)
class ValidatedMetricsReport:
    file_sha256: str
    measurements: dict[str, float]
    metrics: dict[str, Any]
    role: Literal["calibration", "confirmatory"]
    value: dict[str, Any]


@dataclass(frozen=True)
class SelectionEntry:
    global_index: int
    image_sha256: str
    role: Literal["calibration", "confirmatory"]
    row_index: int
    shard_ordinal: int


@dataclass(frozen=True)
class ValidatedRoleSelection:
    entries: tuple[SelectionEntry, ...]
    entries_sha256: str
    manifest_sha256: str
    role: Literal["calibration", "confirmatory"]


METRIC_SPECS = (
    MetricSpec("localizationCoverageHmean", "higher", 0.85, 0.03),
    MetricSpec("exactEndToEndRowHmean", "higher", 0.50, 0.05),
    MetricSpec("exactTokenF1", "higher", 0.85, 0.02),
    MetricSpec("characterErrorRate", "lower", 0.12, 0.02),
    MetricSpec("wordErrorRate", "lower", 0.25, 0.03),
    MetricSpec("macroTokenF1", "higher", 0.80, 0.03),
    MetricSpec("macroCharacterErrorRate", "lower", 0.20, 0.03),
    MetricSpec("macroWordErrorRate", "lower", 0.35, 0.05),
    MetricSpec("p10TokenF1", "higher", 0.65, 0.05),
    MetricSpec("p90CharacterErrorRate", "lower", 0.30, 0.05),
    MetricSpec("fractionTokenF1BelowHalf", "lower", 0.05, 0.02),
)
SPEC_BY_NAME = {spec.name: spec for spec in METRIC_SPECS}
UNIT_INTERVAL_METRICS = {
    "exactEndToEndRowHmean",
    "exactTokenF1",
    "fractionTokenF1BelowHalf",
    "localizationCoverageHmean",
    "macroTokenF1",
    "p10TokenF1",
}


def canonical_json(value: Any) -> str:
    try:
        return json.dumps(
            value,
            allow_nan=False,
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        )
    except (TypeError, ValueError) as exc:
        raise PolicyError("A policy value is not strict canonical JSON") from exc


def sha256_canonical(value: Any) -> str:
    return hashlib.sha256(canonical_json(value).encode("utf-8")).hexdigest()


def encoded_policy(policy: Mapping[str, Any]) -> bytes:
    return (canonical_json(policy) + "\n").encode("utf-8")


def _reject_duplicate_pairs(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    result = {}
    for key, value in pairs:
        if key in result:
            raise PolicyError(f"Duplicate policy field: {key}")
        result[key] = value
    return result


def _reject_json_constant(value: str) -> None:
    raise PolicyError(f"Non-finite JSON constant is forbidden: {value}")


def load_policy(path: Path) -> tuple[dict[str, Any], str]:
    resolved = path.expanduser().resolve()
    if not resolved.is_file() or resolved.stat().st_size > MAX_POLICY_BYTES:
        raise PolicyError("The policy file is unavailable or too large")
    raw = resolved.read_bytes()
    try:
        decoded = raw.decode("utf-8")
        value = json.loads(
            decoded,
            object_pairs_hook=_reject_duplicate_pairs,
            parse_constant=_reject_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PolicyError("The policy is not strict UTF-8 JSON") from exc
    if not isinstance(value, dict) or raw != encoded_policy(value):
        raise PolicyError("The policy is not a canonical JSON object")
    return value, hashlib.sha256(raw).hexdigest()


def publish_policy(path: Path, policy: Mapping[str, Any]) -> dict[str, Any]:
    output = path.expanduser().resolve()
    marker = output.with_name(output.name + ".incomplete")
    if output.exists() or marker.exists():
        raise PolicyError("The policy output or incomplete marker already exists")
    output.parent.mkdir(parents=True, exist_ok=True)
    raw = encoded_policy(policy)
    if len(raw) > MAX_POLICY_BYTES:
        raise PolicyError("The policy output is too large")
    with marker.open("xb") as handle:
        handle.write(raw)
        handle.flush()
        os.fsync(handle.fileno())
    os.replace(marker, output)
    return {"bytes": len(raw), "sha256": hashlib.sha256(raw).hexdigest()}


def load_metrics_report(
    path: Path,
    *,
    selection: ValidatedRoleSelection,
    expected_identity: Mapping[str, Any],
) -> ValidatedMetricsReport:
    if not isinstance(selection, ValidatedRoleSelection) or selection.role not in {
        "calibration",
        "confirmatory",
    }:
        raise PolicyError("A validated report role selection is required")
    resolved = path.expanduser().resolve()
    if not resolved.is_file() or resolved.stat().st_size > MAX_METRICS_REPORT_BYTES:
        raise PolicyError("The metrics report is unavailable or too large")
    raw = resolved.read_bytes()
    try:
        decoded = raw.decode("utf-8")
        value = json.loads(
            decoded,
            object_pairs_hook=_reject_duplicate_pairs,
            parse_constant=_reject_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PolicyError("The metrics report is not strict UTF-8 JSON") from exc
    if not isinstance(value, dict) or raw != encoded_policy(value):
        raise PolicyError("The metrics report is not a canonical JSON object")
    _exact_keys(
        value,
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
        name="metrics report",
    )
    expected_documents = (
        CALIBRATION_DOCUMENTS if selection.role == "calibration" else CONFIRMATORY_DOCUMENTS
    )
    if (
        type(value["schemaVersion"]) is not int
        or value["schemaVersion"] != METRICS_REPORT_SCHEMA_VERSION
        or value["evaluationRole"] != selection.role
        or type(value["documents"]) is not int
        or value["documents"] != expected_documents
        or value["selectionSha256"] != selection.entries_sha256
        or value["runSucceeded"] is not True
        or value["integrityPassed"] is not True
        or type(value["acceptancePassed"]) is not bool
    ):
        raise PolicyError("The metrics report role, completion, or integrity identity is invalid")
    _mapping(value["evidence"], name="metrics report evidence")
    report_identity = validate_identity(_mapping(value["identity"], name="report identity"))
    validated_expected_identity = validate_identity(expected_identity)
    if canonical_json(report_identity) != canonical_json(validated_expected_identity):
        raise PolicyError("The metrics report runtime/model/scorer identity changed")
    metrics = _mapping(value["metrics"], name="report metrics")
    metrics_sha256 = _sha256(value["metricsSha256"], name="report metrics")
    if sha256_canonical(metrics) != metrics_sha256:
        raise PolicyError("The metrics report digest is not bound to its metrics")
    measurements = extract_measurements(metrics, selection=selection)
    if selection.role == "calibration":
        expected_acceptance = all(
            check["passed"] for check in absolute_floor_checks(measurements).values()
        )
        if value["acceptancePassed"] is not expected_acceptance:
            raise PolicyError("The calibration report acceptance result is inconsistent")
    return ValidatedMetricsReport(
        file_sha256=hashlib.sha256(raw).hexdigest(),
        measurements=measurements,
        metrics=json.loads(canonical_json(metrics)),
        role=selection.role,
        value=json.loads(canonical_json(value)),
    )


def _selection_entry_dict(entry: SelectionEntry) -> dict[str, Any]:
    return {
        "globalIndex": entry.global_index,
        "imageSha256": entry.image_sha256,
        "role": entry.role,
        "rowIndex": entry.row_index,
        "shardOrdinal": entry.shard_ordinal,
    }


def load_role_selections(path: Path) -> dict[str, ValidatedRoleSelection]:
    resolved = path.expanduser().resolve()
    if not resolved.is_file() or resolved.stat().st_size != SELECTION_MANIFEST_BYTES:
        raise PolicyError("The frozen selection manifest length is invalid")
    raw = resolved.read_bytes()
    if hashlib.sha256(raw).hexdigest() != SELECTION_MANIFEST_SHA256:
        raise PolicyError("The frozen selection manifest SHA-256 is invalid")
    try:
        value = json.loads(
            raw.decode("utf-8"),
            object_pairs_hook=_reject_duplicate_pairs,
            parse_constant=_reject_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PolicyError("The selection manifest is not strict UTF-8 JSON") from exc
    if not isinstance(value, dict) or raw != encoded_policy(value):
        raise PolicyError("The selection manifest is not canonical JSON")
    _exact_keys(
        value,
        {"algorithm", "dataset", "entries", "entriesSha256", "schemaVersion"},
        name="selection manifest",
    )
    if type(value["schemaVersion"]) is not int or value["schemaVersion"] != 1:
        raise PolicyError("The selection manifest schema version is invalid")
    dataset = _mapping(value["dataset"], name="selection dataset")
    _exact_keys(
        dataset,
        {"id", "revision", "rows", "shards", "split", "uniqueImageSha256"},
        name="selection dataset",
    )
    expected_shards = [
        {"bytes": length, "fileName": name, "sha256": sha256}
        for name, length, sha256 in TRAIN_SHARDS
    ]
    if (
        dataset["id"] != DATASET_ID
        or dataset["revision"] != DATASET_REVISION
        or dataset["split"] != "train"
        or type(dataset["rows"]) is not int
        or dataset["rows"] != 800
        or type(dataset["uniqueImageSha256"]) is not int
        or dataset["uniqueImageSha256"] != 798
        or dataset["shards"] != expected_shards
    ):
        raise PolicyError("The selection dataset identity is invalid")
    algorithm = _mapping(value["algorithm"], name="selection algorithm")
    _exact_keys(
        algorithm,
        {
            "confirmatoryRows",
            "description",
            "duplicateGroupsMayNotCrossRoles",
            "requiredSelectedRows",
        },
        name="selection algorithm",
    )
    if (
        type(algorithm["confirmatoryRows"]) is not int
        or algorithm["confirmatoryRows"] != CONFIRMATORY_DOCUMENTS
        or type(algorithm["requiredSelectedRows"]) is not int
        or algorithm["requiredSelectedRows"] != CONFIRMATORY_DOCUMENTS
        or algorithm["duplicateGroupsMayNotCrossRoles"] is not True
        or not isinstance(algorithm["description"], str)
        or not algorithm["description"]
    ):
        raise PolicyError("The selection algorithm identity is invalid")
    entries_raw = value["entries"]
    if not isinstance(entries_raw, list) or len(entries_raw) != 800:
        raise PolicyError("The selection manifest does not contain exactly 800 rows")
    entries = []
    roles_by_image: dict[str, set[str]] = {}
    for global_index, raw_entry in enumerate(entries_raw):
        entry = _mapping(raw_entry, name=f"selection entry {global_index}")
        _exact_keys(
            entry,
            {"globalIndex", "imageSha256", "role", "rowIndex", "shardOrdinal"},
            name=f"selection entry {global_index}",
        )
        expected_shard = global_index // 200
        expected_row = global_index % 200
        if (
            type(entry["globalIndex"]) is not int
            or entry["globalIndex"] != global_index
            or type(entry["rowIndex"]) is not int
            or entry["rowIndex"] != expected_row
            or type(entry["shardOrdinal"]) is not int
            or entry["shardOrdinal"] != expected_shard
            or entry["role"] not in {"calibration", "confirmatory"}
        ):
            raise PolicyError(f"Selection entry {global_index} has invalid row or role identity")
        image_sha256 = _sha256(entry["imageSha256"], name="selection image")
        roles_by_image.setdefault(image_sha256, set()).add(entry["role"])
        entries.append(
            SelectionEntry(
                global_index=global_index,
                image_sha256=image_sha256,
                role=entry["role"],
                row_index=expected_row,
                shard_ordinal=expected_shard,
            )
        )
    if len(roles_by_image) != 798 or any(len(roles) != 1 for roles in roles_by_image.values()):
        raise PolicyError("Duplicate image identities cross roles or cardinality changed")
    entries_by_role = {
        role: tuple(entry for entry in entries if entry.role == role)
        for role in ("calibration", "confirmatory")
    }
    if (
        len(entries_by_role["calibration"]) != CALIBRATION_DOCUMENTS
        or len(entries_by_role["confirmatory"]) != CONFIRMATORY_DOCUMENTS
    ):
        raise PolicyError("The frozen role cardinalities changed")
    recorded_hashes = _mapping(value["entriesSha256"], name="selection entry hashes")
    _exact_keys(recorded_hashes, set(SELECTION_ENTRIES_SHA256), name="selection entry hashes")
    computed_hashes = {
        "complete": sha256_canonical([_selection_entry_dict(entry) for entry in entries]),
        **{
            role: sha256_canonical(
                [_selection_entry_dict(entry) for entry in entries_by_role[role]]
            )
            for role in ("calibration", "confirmatory")
        },
    }
    if recorded_hashes != SELECTION_ENTRIES_SHA256 or computed_hashes != SELECTION_ENTRIES_SHA256:
        raise PolicyError("The frozen role selection hashes changed")
    return {
        role: ValidatedRoleSelection(
            entries=entries_by_role[role],
            entries_sha256=SELECTION_ENTRIES_SHA256[role],
            manifest_sha256=SELECTION_MANIFEST_SHA256,
            role=role,
        )
        for role in ("calibration", "confirmatory")
    }


def _sha256(value: Any, *, name: str) -> str:
    if not isinstance(value, str) or SHA256_PATTERN.fullmatch(value) is None:
        raise PolicyError(f"The {name} SHA-256 is invalid")
    return value


def _exact_keys(value: Mapping[str, Any], expected: set[str], *, name: str) -> None:
    if set(value) != expected:
        raise PolicyError(f"The {name} schema is incomplete or contains unknown fields")


def validate_utc_timestamp(value: Any) -> str:
    if not isinstance(value, str) or UTC_TIMESTAMP_PATTERN.fullmatch(value) is None:
        raise PolicyError("The policy freeze timestamp is not strict UTC RFC 3339")
    try:
        parsed = datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as exc:
        raise PolicyError("The policy freeze timestamp is invalid") from exc
    if parsed.tzinfo != timezone.utc:
        raise PolicyError("The policy freeze timestamp is not UTC")
    return value


def validate_identity(identity: Mapping[str, Any]) -> dict[str, Any]:
    identity = _mapping(identity, name="frozen identity")
    _exact_keys(
        identity,
        {"benchmark", "corpus", "dependencies", "engine", "runtimeProfiles"},
        name="frozen identity",
    )
    benchmark = _mapping(identity["benchmark"], name="benchmark identity")
    _exact_keys(
        benchmark,
        {
            "genericBenchmarkSha256",
            "policyModuleSha256",
            "protocol",
            "schemaVersion",
            "scorerSha256",
            "scoringConstantsSha256",
            "scriptSha256",
        },
        name="benchmark identity",
    )
    for key in (
        "genericBenchmarkSha256",
        "policyModuleSha256",
        "scorerSha256",
        "scoringConstantsSha256",
        "scriptSha256",
    ):
        _sha256(benchmark[key], name=f"benchmark {key}")
    policy_module_sha256 = hashlib.sha256(Path(__file__).resolve().read_bytes()).hexdigest()
    if (
        type(benchmark["schemaVersion"]) is not int
        or benchmark["schemaVersion"] != CORE_SCORER_SCHEMA_VERSION
        or benchmark["protocol"] != CORE_SCORER_PROTOCOL
        or benchmark["scorerSha256"] != CORE_SCORER_SHA256
        or benchmark["genericBenchmarkSha256"] != GENERIC_BENCHMARK_SHA256
        or benchmark["scoringConstantsSha256"] != SCORING_CONSTANTS_SHA256
        or benchmark["policyModuleSha256"] != policy_module_sha256
    ):
        raise PolicyError("The benchmark schema version or protocol is invalid")

    corpus = _mapping(identity["corpus"], name="corpus identity")
    _exact_keys(
        corpus,
        {
            "calibrationCorpusManifestSha256",
            "calibrationSelectionSha256",
            "calibrationWorkerManifestSha256",
            "completeSelectionSha256",
            "confirmatoryInputManifestSha256",
            "confirmatorySelectionSha256",
            "confirmatoryWorkerManifestSha256",
            "datasetId",
            "datasetRevision",
            "selectionManifestBytes",
            "selectionManifestSha256",
            "trainShardSha256",
        },
        name="corpus identity",
    )
    if (
        corpus["datasetId"] != DATASET_ID
        or corpus["datasetRevision"] != DATASET_REVISION
        or corpus["selectionManifestBytes"] != SELECTION_MANIFEST_BYTES
        or corpus["selectionManifestSha256"] != SELECTION_MANIFEST_SHA256
        or corpus["completeSelectionSha256"] != SELECTION_ENTRIES_SHA256["complete"]
        or corpus["calibrationSelectionSha256"] != SELECTION_ENTRIES_SHA256["calibration"]
        or corpus["confirmatorySelectionSha256"] != SELECTION_ENTRIES_SHA256["confirmatory"]
        or not isinstance(corpus["trainShardSha256"], list)
        or tuple(corpus["trainShardSha256"]) != TRAIN_SHARD_SHA256
    ):
        raise PolicyError("The pinned dataset or selection identity changed")
    _sha256(corpus["calibrationCorpusManifestSha256"], name="calibration corpus manifest")
    _sha256(corpus["calibrationWorkerManifestSha256"], name="calibration worker manifest")
    _sha256(corpus["confirmatoryInputManifestSha256"], name="confirmatory input manifest")
    _sha256(corpus["confirmatoryWorkerManifestSha256"], name="confirmatory worker manifest")

    dependencies = _mapping(identity["dependencies"], name="dependency identity")
    _exact_keys(dependencies, {"geos", "pyarrow", "shapely"}, name="dependency identity")
    if (
        dependencies["pyarrow"] != "25.0.0"
        or dependencies["shapely"] != "2.1.2"
        or dependencies["geos"] != GEOS_VERSION
    ):
        raise PolicyError("The pinned PyArrow, Shapely, or GEOS identity changed")

    engine = _mapping(identity["engine"], name="engine identity")
    _exact_keys(
        engine,
        {"modelId", "modelPackSha256", "modelRevision", "name", "version", "workerSha256"},
        name="engine identity",
    )
    if (
        engine["name"] != ENGINE_NAME
        or engine["version"] != ENGINE_VERSION
        or engine["modelId"] != MODEL_ID
        or engine["modelRevision"] != MODEL_REVISION
        or engine["modelPackSha256"] != MODEL_PACK_SHA256
    ):
        raise PolicyError("The frozen OCR engine or model identity changed")
    _sha256(engine["workerSha256"], name="OCR worker")

    runtime_profiles = _mapping(identity["runtimeProfiles"], name="runtime profiles")
    _exact_keys(runtime_profiles, {"benchmark", "cpu", "directml"}, name="runtime profiles")
    for profile_name in ("benchmark", "cpu", "directml"):
        profile = _mapping(runtime_profiles[profile_name], name=f"{profile_name} runtime")
        _exact_keys(
            profile,
            {"executableSha256", "inventorySha256", "pythonVersion"},
            name=f"{profile_name} runtime",
        )
        _sha256(profile["executableSha256"], name=f"{profile_name} executable")
        _sha256(profile["inventorySha256"], name=f"{profile_name} inventory")
        if not isinstance(profile["pythonVersion"], str) or not profile["pythonVersion"]:
            raise PolicyError(f"The {profile_name} Python version is invalid")
    return json.loads(canonical_json(identity))


def _finite_metric(value: Any, *, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise PolicyError(f"The {name} metric is not numeric")
    result = float(value)
    if not math.isfinite(result) or result < 0:
        raise PolicyError(f"The {name} metric is invalid")
    return result


def _nonnegative_count(value: Any, *, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise PolicyError(f"The {name} count is invalid")
    return value


def _coverage_from_row(value: Any, *, name: str) -> dict[str, float | int]:
    coverage = _mapping(value, name=name)
    expected_keys = {
        "falseNegative",
        "falsePositive",
        "hmean",
        "matchedPredictions",
        "matchedTruths",
        "precision",
        "predictedUnits",
        "recall",
        "referenceUnits",
        "truePositive",
    }
    _exact_keys(coverage, expected_keys, name=name)
    matched_predictions = _nonnegative_count(
        coverage.get("matchedPredictions"), name=f"{name} matched predictions"
    )
    predicted_units = _nonnegative_count(
        coverage.get("predictedUnits"), name=f"{name} predicted units"
    )
    matched_truths = _nonnegative_count(
        coverage.get("matchedTruths"), name=f"{name} matched truths"
    )
    reference_units = _nonnegative_count(
        coverage.get("referenceUnits"), name=f"{name} reference units"
    )
    if matched_predictions > predicted_units or matched_truths > reference_units:
        raise PolicyError(f"The {name} matched cardinality exceeds its denominator")
    precision = matched_predictions / predicted_units if predicted_units else 1.0
    recall = matched_truths / reference_units if reference_units else 1.0
    hmean = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    expected = {
        "falseNegative": reference_units - matched_truths,
        "falsePositive": predicted_units - matched_predictions,
        "hmean": hmean,
        "matchedPredictions": matched_predictions,
        "matchedTruths": matched_truths,
        "precision": precision,
        "predictedUnits": predicted_units,
        "recall": recall,
        "referenceUnits": reference_units,
        "truePositive": min(matched_predictions, matched_truths),
    }
    for key in ("falseNegative", "falsePositive", "truePositive"):
        if _nonnegative_count(coverage.get(key), name=f"{name} {key}") != expected[key]:
            raise PolicyError(f"The {name} values are not derived from their cardinalities")
    for key in ("hmean", "precision", "recall"):
        if _finite_metric(coverage.get(key), name=f"{name} {key}") != expected[key]:
            raise PolicyError(f"The {name} values are not derived from their cardinalities")
    if any(
        _finite_metric(coverage.get(key), name=f"{name} {key}") > 1
        for key in ("hmean", "precision", "recall")
    ):
        raise PolicyError(f"The {name} values are not derived from their cardinalities")
    return expected


def _sequence_counts_from_row(value: Any, *, name: str) -> dict[str, int]:
    counts = _mapping(value, name=name)
    expected_keys = {
        "characterEdits",
        "matchingCharacters",
        "matchingTokensOrderInvariant",
        "matchingWords",
        "predictedCharacters",
        "predictedWords",
        "referenceCharacters",
        "referenceWords",
        "wordEdits",
    }
    _exact_keys(counts, expected_keys, name=name)
    validated = {
        key: _nonnegative_count(value, name=f"{name} {key}") for key, value in counts.items()
    }
    if (
        validated["referenceCharacters"] < validated["referenceWords"]
        or validated["predictedCharacters"] < validated["predictedWords"]
        or validated["matchingCharacters"] < validated["matchingWords"]
        or validated["matchingCharacters"]
        > min(validated["referenceCharacters"], validated["predictedCharacters"])
        or validated["matchingWords"]
        > min(validated["referenceWords"], validated["predictedWords"])
        or validated["matchingTokensOrderInvariant"]
        > min(validated["referenceWords"], validated["predictedWords"])
        or validated["matchingWords"] > validated["matchingTokensOrderInvariant"]
    ):
        raise PolicyError(f"The {name} matching count exceeds its sequence cardinality")
    if (
        validated["characterEdits"]
        < max(validated["referenceCharacters"], validated["predictedCharacters"])
        - validated["matchingCharacters"]
        or validated["wordEdits"]
        < max(validated["referenceWords"], validated["predictedWords"]) - validated["matchingWords"]
    ):
        raise PolicyError(f"The {name} edit distance is inconsistent with its LCS count")
    return validated


def _coverage_from_totals(
    rows: Sequence[Mapping[str, float | int]],
) -> dict[str, float | int]:
    matched_predictions = sum(int(row["matchedPredictions"]) for row in rows)
    predicted_units = sum(int(row["predictedUnits"]) for row in rows)
    matched_truths = sum(int(row["matchedTruths"]) for row in rows)
    reference_units = sum(int(row["referenceUnits"]) for row in rows)
    precision = matched_predictions / predicted_units if predicted_units else 1.0
    recall = matched_truths / reference_units if reference_units else 1.0
    return {
        "falseNegative": reference_units - matched_truths,
        "falsePositive": predicted_units - matched_predictions,
        "hmean": 2 * precision * recall / (precision + recall) if precision + recall else 0.0,
        "matchedPredictions": matched_predictions,
        "matchedTruths": matched_truths,
        "precision": precision,
        "predictedUnits": predicted_units,
        "recall": recall,
        "referenceUnits": reference_units,
        "truePositive": min(matched_predictions, matched_truths),
    }


def _mapping(value: Any, *, name: str) -> Mapping[str, Any]:
    if not isinstance(value, Mapping):
        raise PolicyError(f"The {name} object is unavailable")
    return value


def nearest_rank(values: Sequence[float], percentile: float) -> float:
    if not values or not 0 < percentile <= 1:
        raise PolicyError("Nearest-rank input is invalid")
    ordered = sorted(_finite_metric(value, name="quantile") for value in values)
    rank = math.ceil(percentile * len(ordered))
    return ordered[rank - 1]


def _document_identities(rows: Sequence[Mapping[str, Any]]) -> list[dict[str, Any]]:
    identities = []
    seen_rows: set[int] = set()
    for index, row in enumerate(rows):
        row_index = row.get("rowIndex")
        image_sha256 = row.get("imageSha256")
        if (
            isinstance(row_index, bool)
            or not isinstance(row_index, int)
            or row_index < 0
            or row_index in seen_rows
        ):
            raise PolicyError(f"Per-document row identity {index} is invalid or duplicated")
        seen_rows.add(row_index)
        identities.append(
            {"imageSha256": _sha256(image_sha256, name="document image"), "rowIndex": row_index}
        )
    return identities


def validate_role_selection(
    selection: ValidatedRoleSelection,
    *,
    expected_role: Literal["calibration", "confirmatory"],
) -> list[dict[str, Any]]:
    if expected_role not in {"calibration", "confirmatory"}:
        raise PolicyError("The requested policy role is invalid")
    if not isinstance(selection, ValidatedRoleSelection) or selection.role != expected_role:
        raise PolicyError(f"A validated {expected_role} selection is required")
    expected_count = (
        CALIBRATION_DOCUMENTS if expected_role == "calibration" else CONFIRMATORY_DOCUMENTS
    )
    if (
        len(selection.entries) != expected_count
        or selection.entries_sha256 != SELECTION_ENTRIES_SHA256[expected_role]
        or selection.manifest_sha256 != SELECTION_MANIFEST_SHA256
        or any(not isinstance(entry, SelectionEntry) for entry in selection.entries)
        or any(entry.role != expected_role for entry in selection.entries)
        or sha256_canonical([_selection_entry_dict(entry) for entry in selection.entries])
        != SELECTION_ENTRIES_SHA256[expected_role]
    ):
        raise PolicyError(f"The validated {expected_role} selection identity changed")
    return [
        {"imageSha256": entry.image_sha256, "rowIndex": entry.global_index}
        for entry in selection.entries
    ]


def extract_measurements(
    metrics: Mapping[str, Any],
    *,
    selection: ValidatedRoleSelection,
) -> dict[str, float]:
    if not isinstance(selection, ValidatedRoleSelection) or selection.role not in {
        "calibration",
        "confirmatory",
    }:
        raise PolicyError("A validated role selection is required")
    expected_identities = validate_role_selection(selection, expected_role=selection.role)
    expected_documents = len(expected_identities)
    micro = _mapping(metrics.get("micro"), name="micro metrics")
    macro = _mapping(metrics.get("macro"), name="macro metrics")
    per_document_raw = metrics.get("perDocument")
    if not isinstance(per_document_raw, list) or len(per_document_raw) != expected_documents:
        raise PolicyError(
            f"The policy requires exactly {expected_documents} per-document metric rows"
        )
    if type(micro.get("documents")) is not int or micro.get("documents") != expected_documents:
        raise PolicyError("The micro document count does not match the policy role")
    per_document = [
        _mapping(item, name=f"per-document metrics {index}")
        for index, item in enumerate(per_document_raw)
    ]
    if _document_identities(per_document) != expected_identities:
        raise PolicyError("Per-document metrics do not match the frozen role selection")
    document_detection = [
        _coverage_from_row(item.get("detection"), name=f"document {index} detection")
        for index, item in enumerate(per_document)
    ]
    document_end_to_end = [
        _coverage_from_row(item.get("endToEndExact"), name=f"document {index} end-to-end")
        for index, item in enumerate(per_document)
    ]
    for index, (detection_row, end_to_end_row) in enumerate(
        zip(document_detection, document_end_to_end, strict=True)
    ):
        if (
            end_to_end_row["predictedUnits"] != detection_row["predictedUnits"]
            or end_to_end_row["referenceUnits"] != detection_row["referenceUnits"]
            or end_to_end_row["matchedPredictions"] > detection_row["matchedPredictions"]
            or end_to_end_row["matchedTruths"] > detection_row["matchedTruths"]
        ):
            raise PolicyError(
                f"Document {index} end-to-end coverage is not a subset of localization coverage"
            )
    document_counts = [
        _sequence_counts_from_row(item.get("counts"), name=f"document {index} sequence")
        for index, item in enumerate(per_document)
    ]
    for index, (counts, detection_row, end_to_end_row) in enumerate(
        zip(document_counts, document_detection, document_end_to_end, strict=True)
    ):
        if counts["referenceWords"] < detection_row["referenceUnits"]:
            raise PolicyError(
                f"Document {index} reference token count is smaller than its physical rows"
            )
        if counts["matchingWords"] < max(
            end_to_end_row["matchedPredictions"], end_to_end_row["matchedTruths"]
        ):
            raise PolicyError(
                f"Document {index} exact row count exceeds its ordered matching words"
            )
    document_token_f1 = []
    document_cer = []
    document_wer = []
    for index, (item, counts) in enumerate(zip(per_document, document_counts, strict=True)):
        token_precision = counts["matchingTokensOrderInvariant"] / max(counts["predictedWords"], 1)
        token_recall = counts["matchingTokensOrderInvariant"] / max(counts["referenceWords"], 1)
        token_f1 = (
            2 * token_precision * token_recall / (token_precision + token_recall)
            if token_precision + token_recall
            else 0.0
        )
        character_error_rate = counts["characterEdits"] / max(counts["referenceCharacters"], 1)
        word_error_rate = counts["wordEdits"] / max(counts["referenceWords"], 1)
        recorded = (
            _finite_metric(item.get("tokenF1"), name=f"document {index} token F1"),
            _finite_metric(item.get("pageCer"), name=f"document {index} character error rate"),
            _finite_metric(item.get("pageWer"), name=f"document {index} word error rate"),
        )
        if recorded != (token_f1, character_error_rate, word_error_rate):
            raise PolicyError(
                f"Document {index} token F1, CER, or WER is not derived from its counts"
            )
        document_token_f1.append(token_f1)
        document_cer.append(character_error_rate)
        document_wer.append(word_error_rate)
    recomputed_macro = {
        "tokenF1": statistics.fmean(document_token_f1),
        "pageCer": statistics.fmean(document_cer),
        "pageWer": statistics.fmean(document_wer),
    }
    for name, recomputed in recomputed_macro.items():
        recorded = _finite_metric(macro.get(name), name=f"macro {name}")
        if recorded != recomputed:
            raise PolicyError(f"The macro {name} metric is not derived from per-document rows")

    detection = _coverage_from_totals(document_detection)
    end_to_end = _coverage_from_totals(document_end_to_end)
    recorded_detection = _coverage_from_row(micro.get("detection"), name="micro detection")
    recorded_end_to_end = _coverage_from_row(micro.get("endToEndExact"), name="micro end-to-end")
    if recorded_detection != detection or recorded_end_to_end != end_to_end:
        raise PolicyError("The micro coverage metrics are not derived from per-document counts")
    total_counts = {
        key: sum(counts[key] for counts in document_counts) for key in document_counts[0]
    }
    if _sequence_counts_from_row(micro.get("counts"), name="micro sequence") != total_counts:
        raise PolicyError("The micro sequence counts are not derived from per-document counts")
    token_precision = total_counts["matchingTokensOrderInvariant"] / max(
        total_counts["predictedWords"], 1
    )
    token_recall = total_counts["matchingTokensOrderInvariant"] / max(
        total_counts["referenceWords"], 1
    )
    token_f1 = (
        2 * token_precision * token_recall / (token_precision + token_recall)
        if token_precision + token_recall
        else 0.0
    )
    micro_cer = total_counts["characterEdits"] / max(total_counts["referenceCharacters"], 1)
    micro_wer = total_counts["wordEdits"] / max(total_counts["referenceWords"], 1)
    recorded_micro = (
        float(recorded_detection["hmean"]),
        float(recorded_end_to_end["hmean"]),
        _finite_metric(micro.get("tokenF1"), name="micro token F1"),
        _finite_metric(micro.get("pageCer"), name="micro CER"),
        _finite_metric(micro.get("pageWer"), name="micro WER"),
    )
    recomputed_micro = (detection["hmean"], end_to_end["hmean"], token_f1, micro_cer, micro_wer)
    if recorded_micro != recomputed_micro:
        raise PolicyError("The micro acceptance metrics are not derived from per-document counts")
    return {
        "characterErrorRate": micro_cer,
        "exactEndToEndRowHmean": float(end_to_end["hmean"]),
        "exactTokenF1": token_f1,
        "fractionTokenF1BelowHalf": sum(value < 0.50 for value in document_token_f1)
        / expected_documents,
        "localizationCoverageHmean": float(detection["hmean"]),
        "macroCharacterErrorRate": recomputed_macro["pageCer"],
        "macroTokenF1": recomputed_macro["tokenF1"],
        "macroWordErrorRate": recomputed_macro["pageWer"],
        "p10TokenF1": nearest_rank(document_token_f1, 0.10),
        "p90CharacterErrorRate": nearest_rank(document_cer, 0.90),
        "wordErrorRate": micro_wer,
    }


def metric_passed(value: float, *, spec: MetricSpec, boundary: float) -> bool:
    return value >= boundary if spec.direction == "higher" else value <= boundary


def validate_measurements(measurements: Mapping[str, float]) -> dict[str, float]:
    if set(measurements) != set(SPEC_BY_NAME):
        raise PolicyError("The measurement set is incomplete or contains unknown metrics")
    validated = {name: _finite_metric(value, name=name) for name, value in measurements.items()}
    if any(validated[name] > 1 for name in UNIT_INTERVAL_METRICS):
        raise PolicyError("A bounded F1, Hmean, percentile, or fraction metric exceeds 1")
    return validated


def absolute_floor_checks(measurements: Mapping[str, float]) -> dict[str, dict[str, Any]]:
    validated = validate_measurements(measurements)
    return {
        spec.name: {
            "boundary": spec.absolute_boundary,
            "direction": spec.direction,
            "passed": metric_passed(
                validated[spec.name],
                spec=spec,
                boundary=spec.absolute_boundary,
            ),
            "value": validated[spec.name],
        }
        for spec in METRIC_SPECS
    }


def derive_thresholds(measurements: Mapping[str, float]) -> dict[str, float]:
    validated = validate_measurements(measurements)
    checks = absolute_floor_checks(measurements)
    failed = [name for name, check in checks.items() if check["passed"] is not True]
    if failed:
        raise PolicyError("Calibration failed absolute quality floors: " + ", ".join(failed))
    thresholds = {}
    for spec in METRIC_SPECS:
        value = validated[spec.name]
        if spec.direction == "higher":
            thresholds[spec.name] = max(spec.absolute_boundary, value - spec.calibration_margin)
        else:
            thresholds[spec.name] = min(spec.absolute_boundary, value + spec.calibration_margin)
    return thresholds


def evaluate_thresholds(
    measurements: Mapping[str, float], thresholds: Mapping[str, float]
) -> dict[str, Any]:
    validated = validate_measurements(measurements)
    if set(thresholds) != set(SPEC_BY_NAME):
        raise PolicyError("The measurement or threshold set is incomplete")
    checks = {
        spec.name: {
            "direction": spec.direction,
            "passed": metric_passed(
                validated[spec.name],
                spec=spec,
                boundary=_finite_metric(thresholds[spec.name], name=f"{spec.name} threshold"),
            ),
            "threshold": thresholds[spec.name],
            "value": validated[spec.name],
        }
        for spec in METRIC_SPECS
    }
    return {"checks": checks, "passed": all(check["passed"] for check in checks.values())}


def _calibration_generated_at(report: ValidatedMetricsReport) -> str:
    evidence = _mapping(report.value.get("evidence"), name="calibration report evidence")
    generated_at = validate_utc_timestamp(evidence.get("generatedAtUtc"))
    return generated_at


def build_policy(
    *,
    calibration_selection: ValidatedRoleSelection,
    calibration_report_path: Path,
    frozen_at_utc: str,
    identity: Mapping[str, Any],
) -> dict[str, Any]:
    validate_utc_timestamp(frozen_at_utc)
    validated_identity = validate_identity(identity)
    validate_role_selection(calibration_selection, expected_role="calibration")
    report = load_metrics_report(
        calibration_report_path,
        selection=calibration_selection,
        expected_identity=validated_identity,
    )
    if frozen_at_utc != _calibration_generated_at(report):
        raise PolicyError("The policy freeze timestamp does not match the calibration report")
    thresholds = derive_thresholds(report.measurements)
    return {
        "absoluteFloors": {spec.name: spec.absolute_boundary for spec in METRIC_SPECS},
        "calibration": {
            "documents": CALIBRATION_DOCUMENTS,
            "measurements": report.measurements,
            "reportSha256": report.file_sha256,
        },
        "calibrationMargins": {spec.name: spec.calibration_margin for spec in METRIC_SPECS},
        "directions": {spec.name: spec.direction for spec in METRIC_SPECS},
        "frozenAtUtc": frozen_at_utc,
        "identity": validated_identity,
        "policyId": POLICY_ID,
        "schemaVersion": SCHEMA_VERSION,
        "thresholds": thresholds,
    }


def validate_policy(
    policy_path: Path,
    *,
    expected_calibration_selection: ValidatedRoleSelection,
    expected_calibration_report_path: Path,
    expected_identity: Mapping[str, Any],
) -> ValidatedPolicy:
    policy_value, policy_file_sha256 = load_policy(policy_path)
    policy = _mapping(policy_value, name="policy")
    expected_keys = {
        "absoluteFloors",
        "calibration",
        "calibrationMargins",
        "directions",
        "frozenAtUtc",
        "identity",
        "policyId",
        "schemaVersion",
        "thresholds",
    }
    if set(policy) != expected_keys:
        raise PolicyError("The policy schema is incomplete or contains unknown fields")
    if (
        type(policy.get("schemaVersion")) is not int
        or policy.get("schemaVersion") != SCHEMA_VERSION
        or policy.get("policyId") != POLICY_ID
    ):
        raise PolicyError("The policy schema or identity is unsupported")
    validate_utc_timestamp(policy.get("frozenAtUtc"))
    calibration = _mapping(policy.get("calibration"), name="policy calibration")
    _exact_keys(
        calibration,
        {"documents", "measurements", "reportSha256"},
        name="policy calibration",
    )
    calibration_report = load_metrics_report(
        expected_calibration_report_path,
        selection=expected_calibration_selection,
        expected_identity=expected_identity,
    )
    if policy.get("frozenAtUtc") != _calibration_generated_at(calibration_report):
        raise PolicyError("The policy freeze timestamp does not match the calibration report")
    if (
        type(calibration.get("documents")) is not int
        or calibration.get("documents") != CALIBRATION_DOCUMENTS
        or calibration.get("reportSha256") != calibration_report.file_sha256
    ):
        raise PolicyError("The calibration report identity does not match the policy")
    validated_identity = validate_identity(_mapping(policy.get("identity"), name="policy identity"))
    expected_identity = validate_identity(expected_identity)
    if canonical_json(validated_identity) != canonical_json(expected_identity):
        raise PolicyError("The frozen runtime/model/scorer identity does not match the policy")
    if policy.get("absoluteFloors") != {spec.name: spec.absolute_boundary for spec in METRIC_SPECS}:
        raise PolicyError("The absolute quality floors were changed")
    if policy.get("calibrationMargins") != {
        spec.name: spec.calibration_margin for spec in METRIC_SPECS
    }:
        raise PolicyError("The calibration margins were changed")
    if policy.get("directions") != {spec.name: spec.direction for spec in METRIC_SPECS}:
        raise PolicyError("The metric directions were changed")
    measurements = _mapping(calibration.get("measurements"), name="calibration measurements")
    expected_measurements = calibration_report.measurements
    if measurements != expected_measurements:
        raise PolicyError("The policy measurements do not match the calibration report")
    expected_thresholds = derive_thresholds(measurements)
    if policy.get("thresholds") != expected_thresholds:
        raise PolicyError("The policy thresholds were changed or derived incorrectly")
    if hashlib.sha256(encoded_policy(policy)).hexdigest() != policy_file_sha256:
        raise PolicyError("The loaded policy bytes do not match the validated policy")
    return ValidatedPolicy(
        file_sha256=policy_file_sha256,
        value=json.loads(canonical_json(policy)),
    )


def evaluate_confirmatory(
    policy_path: Path,
    confirmatory_report_path: Path,
    *,
    expected_calibration_report_path: Path,
    expected_calibration_selection: ValidatedRoleSelection,
    expected_identity: Mapping[str, Any],
    selection: ValidatedRoleSelection,
) -> dict[str, Any]:
    validated = validate_policy(
        policy_path,
        expected_calibration_selection=expected_calibration_selection,
        expected_calibration_report_path=expected_calibration_report_path,
        expected_identity=expected_identity,
    )
    validate_role_selection(selection, expected_role="confirmatory")
    report = load_metrics_report(
        confirmatory_report_path,
        selection=selection,
        expected_identity=expected_identity,
    )
    measurements = report.measurements
    thresholds = _mapping(validated.value.get("thresholds"), name="policy thresholds")
    result = evaluate_thresholds(measurements, thresholds)
    if report.value["acceptancePassed"] is not result["passed"]:
        raise PolicyError("The confirmatory report acceptance result is inconsistent")
    return {
        "confirmatoryReportSha256": report.file_sha256,
        "measurements": measurements,
        "policySha256": validated.file_sha256,
        **result,
    }


def policy_contract() -> dict[str, Any]:
    return {
        "calibrationDocuments": CALIBRATION_DOCUMENTS,
        "confirmatoryDocuments": CONFIRMATORY_DOCUMENTS,
        "metrics": [asdict(spec) for spec in METRIC_SPECS],
        "policyId": POLICY_ID,
        "schemaVersion": SCHEMA_VERSION,
    }
