#!/usr/bin/env python3
"""Validate and evaluate the research-only translation-worthiness corpus.

The module intentionally has no runtime model dependency. Reports contain only
aggregate counters and allowlisted stratum names; source text and row IDs are
never copied into report output.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import sys
import tempfile
from collections import Counter, defaultdict
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1
CORPUS_ID = "translation-worthiness-project-synthetic-v1"
PROJECT_LICENSE = "project-synthetic"
DEFAULT_CORPUS = Path(__file__).with_name("translation_worthiness_synthetic_v1.jsonl")
DEFAULT_SPLITS = Path(__file__).with_name("translation_worthiness_splits_v1.json")

MAX_JSONL_BYTES = 16 * 1024 * 1024
MAX_JSON_LINE_BYTES = 64 * 1024
MAX_RECORDS = 100_000
MAX_IDENTIFIER_CHARACTERS = 64
MAX_TEXT_CODE_POINTS = 2_048
IDENTIFIER_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$", re.ASCII)

LABELS = ("ambiguous", "human-worthy", "machine", "mixed")
SOURCE_FAMILIES = (
    "code",
    "config",
    "encoded",
    "log",
    "low-level",
    "ocr-like",
    "plain-language",
    "registry",
    "structured-token",
    "url",
)
ORIGIN_FAMILIES = (
    "decoded",
    "floss-decoded",
    "floss-static",
    "native-static",
    "ocr",
    "pdf-text",
    "unknown",
)
SCRIPT_FAMILIES = (
    "arabic",
    "ascii-symbolic",
    "cyrillic",
    "devanagari",
    "han",
    "japanese",
    "latin",
    "mixed-script",
)
LENGTH_BANDS: tuple[tuple[str, int, int], ...] = (
    ("1-7", 1, 7),
    ("8-31", 8, 31),
    ("32-127", 32, 127),
    ("128-511", 128, 511),
    ("512-2048", 512, 2_048),
)
LENGTH_BAND_NAMES = tuple(name for name, _, _ in LENGTH_BANDS)
SPLIT_NAMES = ("calibration", "test", "train")
DECISIONS = ("abstain", "retain", "suppress")

CORPUS_FIELDS = frozenset(
    {
        "schemaVersion",
        "id",
        "text",
        "label",
        "groupId",
        "sourceFamily",
        "originFamily",
        "scriptFamily",
        "lengthBand",
        "license",
    }
)
SPLIT_FIELDS = frozenset({"schemaVersion", "corpusId", "license", "splits"})
PREDICTION_FIELDS = frozenset({"id", "score", "decision"})
MODEL_MANIFEST_FIELDS = frozenset(
    {
        "schemaVersion",
        "artifactType",
        "researchOnly",
        "promotionEligible",
        "corpusId",
        "license",
        "experimentId",
        "ablation",
        "scoreOrientation",
        "protocol",
        "thresholds",
        "hashes",
        "model",
    }
)
PROTOCOL_FIELDS = frozenset({"fitSplit", "selectionSplit", "lockedTestSplit", "state"})
THRESHOLD_FIELDS = frozenset({"suppressMax", "retainMin"})
HASH_FIELDS = frozenset(
    {
        "corpusSha256",
        "splitsSha256",
        "trainerSha256",
        "featureContractSha256",
        "modelSha256",
        "predictionsSha256",
    }
)
MODEL_FIELDS = frozenset(
    {
        "formatVersion",
        "bucketCount",
        "ngramMin",
        "ngramMax",
        "epochs",
        "learningRate",
        "byteLength",
    }
)
EXPERIMENT_ABLATIONS = (
    "exact-only",
    "engineered-only",
    "character-ngrams-only",
    "combined",
)
EVALUATION_PHASES = ("calibration-select", "locked-test")
SCORE_ORIENTATION = "higher-contains-any-human"
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$", re.ASCII)


class WorthinessCorpusError(RuntimeError):
    """Raised when research corpus evidence is malformed or incomplete."""


@dataclass(frozen=True)
class CorpusRow:
    identifier: str
    text: str
    label: str
    group_id: str
    source_family: str
    origin_family: str
    script_family: str
    length_band: str


@dataclass(frozen=True)
class ValidatedCorpus:
    rows: tuple[CorpusRow, ...]
    split_by_group: Mapping[str, str]
    corpus_id: str = CORPUS_ID
    license_id: str = PROJECT_LICENSE

    @property
    def row_by_id(self) -> Mapping[str, CorpusRow]:
        return {row.identifier: row for row in self.rows}


@dataclass(frozen=True)
class Prediction:
    score: float
    decision: str


@dataclass(frozen=True)
class ModelManifest:
    experiment_id: str
    ablation: str
    suppress_max: float
    retain_min: float
    hashes: Mapping[str, str]
    model: Mapping[str, int | float]


def _reject_json_constant(value: str) -> None:
    del value
    raise WorthinessCorpusError("JSON input contains a non-finite numeric constant")


def _unique_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise WorthinessCorpusError("JSON input contains a duplicate object property")
        result[key] = value
    return result


def _load_json(raw: bytes, *, name: str) -> Any:
    try:
        return json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_json_constant,
        )
    except WorthinessCorpusError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise WorthinessCorpusError(f"{name} is not strict UTF-8 JSON") from exc


def _physical_file(path: Path, *, name: str) -> Path:
    resolved = path.resolve(strict=False)
    if not path.is_file() or path.is_symlink() or resolved.is_symlink():
        raise WorthinessCorpusError(f"{name} must be a physical file")
    return resolved


def _read_jsonl(path: Path, *, name: str) -> list[dict[str, Any]]:
    resolved = _physical_file(path, name=name)
    size = resolved.stat().st_size
    if size <= 0 or size > MAX_JSONL_BYTES:
        raise WorthinessCorpusError(f"{name} is empty or exceeds the bounded size")
    raw = resolved.read_bytes()
    if not raw.endswith(b"\n"):
        raise WorthinessCorpusError(f"{name} must end with one JSONL newline")
    rows: list[dict[str, Any]] = []
    for ordinal, line in enumerate(raw.splitlines(), start=1):
        if not line or len(line) > MAX_JSON_LINE_BYTES:
            raise WorthinessCorpusError(f"{name} row {ordinal} is empty or over length")
        value = _load_json(line, name=f"{name} row {ordinal}")
        if not isinstance(value, dict):
            raise WorthinessCorpusError(f"{name} row {ordinal} must be an object")
        rows.append(value)
        if len(rows) > MAX_RECORDS:
            raise WorthinessCorpusError(f"{name} exceeds the bounded row count")
    return rows


def _required_identifier(value: Any, *, field: str, ordinal: int) -> str:
    if (
        not isinstance(value, str)
        or len(value) > MAX_IDENTIFIER_CHARACTERS
        or IDENTIFIER_PATTERN.fullmatch(value) is None
    ):
        raise WorthinessCorpusError(f"Corpus row {ordinal} has an invalid {field}")
    return value


def length_band(text: str) -> str:
    count = len(text)
    for name, minimum, maximum in LENGTH_BANDS:
        if minimum <= count <= maximum:
            return name
    raise WorthinessCorpusError("Corpus text is outside the 1--2,048 code-point boundary")


def _allowlisted(value: Any, allowed: Sequence[str], *, field: str, ordinal: int) -> str:
    if not isinstance(value, str) or value not in allowed:
        raise WorthinessCorpusError(f"Corpus row {ordinal} has an invalid {field}")
    return value


def load_corpus(path: Path, *, required_license: str = PROJECT_LICENSE) -> tuple[CorpusRow, ...]:
    parsed = _read_jsonl(path, name="translation-worthiness corpus")
    identifiers: set[str] = set()
    text_groups: dict[str, str] = {}
    rows: list[CorpusRow] = []
    for ordinal, value in enumerate(parsed, start=1):
        if frozenset(value) != CORPUS_FIELDS:
            raise WorthinessCorpusError(f"Corpus row {ordinal} does not use the schema-1 allowlist")
        if type(value["schemaVersion"]) is not int or value["schemaVersion"] != SCHEMA_VERSION:
            raise WorthinessCorpusError(f"Corpus row {ordinal} has an unsupported schema version")
        identifier = _required_identifier(value["id"], field="id", ordinal=ordinal)
        group_id = _required_identifier(value["groupId"], field="groupId", ordinal=ordinal)
        if identifier in identifiers:
            raise WorthinessCorpusError(f"Corpus row {ordinal} duplicates a prior id")
        identifiers.add(identifier)
        text = value["text"]
        if (
            not isinstance(text, str)
            or not text
            or "\x00" in text
            or any(0xD800 <= ord(character) <= 0xDFFF for character in text)
        ):
            raise WorthinessCorpusError(f"Corpus row {ordinal} has invalid text")
        if any(ord(character) < 32 and character not in "\t\r\n" for character in text):
            raise WorthinessCorpusError(f"Corpus row {ordinal} has unsupported control text")
        prior_group = text_groups.setdefault(text, group_id)
        if prior_group != group_id:
            raise WorthinessCorpusError(
                f"Corpus row {ordinal} places duplicate text in a different source group"
            )
        declared_band = _allowlisted(
            value["lengthBand"], LENGTH_BAND_NAMES, field="lengthBand", ordinal=ordinal
        )
        if declared_band != length_band(text):
            raise WorthinessCorpusError(f"Corpus row {ordinal} has a mismatched lengthBand")
        if value["license"] != required_license:
            raise WorthinessCorpusError(f"Corpus row {ordinal} has an unapproved license")
        rows.append(
            CorpusRow(
                identifier=identifier,
                text=text,
                label=_allowlisted(value["label"], LABELS, field="label", ordinal=ordinal),
                group_id=group_id,
                source_family=_allowlisted(
                    value["sourceFamily"],
                    SOURCE_FAMILIES,
                    field="sourceFamily",
                    ordinal=ordinal,
                ),
                origin_family=_allowlisted(
                    value["originFamily"],
                    ORIGIN_FAMILIES,
                    field="originFamily",
                    ordinal=ordinal,
                ),
                script_family=_allowlisted(
                    value["scriptFamily"],
                    SCRIPT_FAMILIES,
                    field="scriptFamily",
                    ordinal=ordinal,
                ),
                length_band=declared_band,
            )
        )
    if not rows:
        raise WorthinessCorpusError("Translation-worthiness corpus contains no rows")
    return tuple(rows)


def load_split_manifest(
    path: Path,
    rows: Sequence[CorpusRow],
    *,
    required_corpus_id: str = CORPUS_ID,
    required_license: str = PROJECT_LICENSE,
) -> Mapping[str, str]:
    resolved = _physical_file(path, name="translation-worthiness split manifest")
    raw = resolved.read_bytes()
    if not raw or len(raw) > MAX_JSON_LINE_BYTES:
        raise WorthinessCorpusError("Split manifest is empty or over length")
    value = _load_json(raw, name="translation-worthiness split manifest")
    if not isinstance(value, dict) or frozenset(value) != SPLIT_FIELDS:
        raise WorthinessCorpusError("Split manifest does not use the schema-1 allowlist")
    if (
        type(value["schemaVersion"]) is not int
        or value["schemaVersion"] != SCHEMA_VERSION
        or value["corpusId"] != required_corpus_id
        or value["license"] != required_license
    ):
        raise WorthinessCorpusError("Split manifest identity is unsupported")
    splits = value["splits"]
    if not isinstance(splits, dict) or tuple(sorted(splits)) != SPLIT_NAMES:
        raise WorthinessCorpusError("Split manifest must define train, calibration, and test")

    corpus_groups = {row.group_id for row in rows}
    split_by_group: dict[str, str] = {}
    for split in SPLIT_NAMES:
        groups = splits[split]
        if (
            not isinstance(groups, list)
            or not groups
            or not all(isinstance(group, str) for group in groups)
            or groups != sorted(groups)
            or len(groups) != len(set(groups))
        ):
            raise WorthinessCorpusError(f"Split {split} must contain sorted unique group IDs")
        for group in groups:
            _required_identifier(group, field="split groupId", ordinal=1)
            if group in split_by_group:
                raise WorthinessCorpusError("A group appears in more than one split")
            split_by_group[group] = split
    if set(split_by_group) != corpus_groups:
        raise WorthinessCorpusError("Split groups do not exactly cover the corpus groups")
    return split_by_group


def validate_corpus(
    corpus_path: Path,
    split_path: Path,
    *,
    corpus_id: str = CORPUS_ID,
    license_id: str = PROJECT_LICENSE,
) -> ValidatedCorpus:
    rows = load_corpus(corpus_path, required_license=license_id)
    split_by_group = load_split_manifest(
        split_path,
        rows,
        required_corpus_id=corpus_id,
        required_license=license_id,
    )
    return ValidatedCorpus(
        rows=rows,
        split_by_group=split_by_group,
        corpus_id=corpus_id,
        license_id=license_id,
    )


def _complete_counts(values: Iterable[str], allowed: Sequence[str]) -> dict[str, int]:
    counts = Counter(values)
    return {name: counts[name] for name in allowed}


def validation_report(corpus: ValidatedCorpus) -> dict[str, Any]:
    split_records = Counter(corpus.split_by_group[row.group_id] for row in corpus.rows)
    split_groups = Counter(corpus.split_by_group.values())
    return {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-corpus-validation",
        "corpusId": corpus.corpus_id,
        "license": corpus.license_id,
        "researchOnly": True,
        "promotionEligible": False,
        "status": "valid",
        "records": len(corpus.rows),
        "groups": len(corpus.split_by_group),
        "counts": {
            "labels": _complete_counts((row.label for row in corpus.rows), LABELS),
            "sourceFamilies": _complete_counts(
                (row.source_family for row in corpus.rows), SOURCE_FAMILIES
            ),
            "originFamilies": _complete_counts(
                (row.origin_family for row in corpus.rows), ORIGIN_FAMILIES
            ),
            "scriptFamilies": _complete_counts(
                (row.script_family for row in corpus.rows), SCRIPT_FAMILIES
            ),
            "lengthBands": _complete_counts(
                (row.length_band for row in corpus.rows), LENGTH_BAND_NAMES
            ),
        },
        "splits": {
            split: {"groups": split_groups[split], "records": split_records[split]}
            for split in SPLIT_NAMES
        },
    }


def _sha256_file(path: Path, *, name: str) -> str:
    resolved = _physical_file(path, name=name)
    digest = hashlib.sha256()
    try:
        with resolved.open("rb") as handle:
            while chunk := handle.read(1024 * 1024):
                digest.update(chunk)
    except OSError as exc:
        raise WorthinessCorpusError(f"Could not hash {name}") from exc
    return digest.hexdigest()


def _manifest_object(value: Any, fields: frozenset[str], *, name: str) -> Mapping[str, Any]:
    if not isinstance(value, dict) or frozenset(value) != fields:
        raise WorthinessCorpusError(f"Model manifest {name} does not use its allowlist")
    return value


def _manifest_float(value: Any, *, name: str) -> float:
    if (
        isinstance(value, bool)
        or not isinstance(value, (int, float))
        or not math.isfinite(float(value))
    ):
        raise WorthinessCorpusError(f"Model manifest {name} is invalid")
    return float(value)


def load_model_manifest(
    path: Path,
    *,
    corpus_path: Path,
    split_path: Path,
    trainer_path: Path,
    model_path: Path,
    predictions_path: Path,
    expected_feature_contract_sha256: str,
    expected_corpus_id: str = CORPUS_ID,
    expected_license: str = PROJECT_LICENSE,
) -> ModelManifest:
    resolved = _physical_file(path, name="translation-worthiness model manifest")
    raw = resolved.read_bytes()
    if not raw or len(raw) > MAX_JSON_LINE_BYTES:
        raise WorthinessCorpusError("Model manifest is empty or over length")
    value = _load_json(raw, name="translation-worthiness model manifest")
    value = _manifest_object(value, MODEL_MANIFEST_FIELDS, name="root")
    if (
        type(value["schemaVersion"]) is not int
        or value["schemaVersion"] != SCHEMA_VERSION
        or value["artifactType"] != "translation-worthiness-model-manifest"
        or value["researchOnly"] is not True
        or value["promotionEligible"] is not False
        or value["corpusId"] != expected_corpus_id
        or value["license"] != expected_license
        or value["scoreOrientation"] != SCORE_ORIENTATION
    ):
        raise WorthinessCorpusError("Model manifest identity is unsupported")
    experiment_id = _required_identifier(value["experimentId"], field="experimentId", ordinal=1)
    ablation = _allowlisted(value["ablation"], EXPERIMENT_ABLATIONS, field="ablation", ordinal=1)
    protocol = _manifest_object(value["protocol"], PROTOCOL_FIELDS, name="protocol")
    if protocol != {
        "fitSplit": "train",
        "selectionSplit": "calibration",
        "lockedTestSplit": "test",
        "state": "locked",
    }:
        raise WorthinessCorpusError("Model manifest does not lock train/calibration/test phases")
    thresholds = _manifest_object(value["thresholds"], THRESHOLD_FIELDS, name="thresholds")
    suppress_max = _manifest_float(thresholds["suppressMax"], name="suppressMax")
    retain_min = _manifest_float(thresholds["retainMin"], name="retainMin")
    if suppress_max >= retain_min:
        raise WorthinessCorpusError("Model manifest thresholds have no abstention interval")
    hashes = _manifest_object(value["hashes"], HASH_FIELDS, name="hashes")
    for name, digest in hashes.items():
        if not isinstance(digest, str) or SHA256_PATTERN.fullmatch(digest) is None:
            raise WorthinessCorpusError(f"Model manifest {name} is not SHA-256")
    expected_hashes = {
        "corpusSha256": _sha256_file(corpus_path, name="translation-worthiness corpus"),
        "splitsSha256": _sha256_file(split_path, name="translation-worthiness split manifest"),
        "trainerSha256": _sha256_file(trainer_path, name="translation-worthiness trainer"),
        "featureContractSha256": expected_feature_contract_sha256,
        "modelSha256": _sha256_file(model_path, name="translation-worthiness model"),
        "predictionsSha256": _sha256_file(
            predictions_path, name="translation-worthiness predictions"
        ),
    }
    if dict(hashes) != expected_hashes:
        raise WorthinessCorpusError("Model manifest hash identity does not match its inputs")
    model = _manifest_object(value["model"], MODEL_FIELDS, name="model")
    integer_fields = (
        "formatVersion",
        "bucketCount",
        "ngramMin",
        "ngramMax",
        "epochs",
        "byteLength",
    )
    if any(type(model[name]) is not int or model[name] <= 0 for name in integer_fields):
        raise WorthinessCorpusError("Model manifest contains an invalid integer field")
    _manifest_float(model["learningRate"], name="learningRate")
    if (
        model["byteLength"]
        != _physical_file(model_path, name="translation-worthiness model").stat().st_size
    ):
        raise WorthinessCorpusError("Model manifest byte length does not match the model")
    return ModelManifest(
        experiment_id=experiment_id,
        ablation=ablation,
        suppress_max=suppress_max,
        retain_min=retain_min,
        hashes=dict(hashes),
        model=dict(model),
    )


def load_predictions(
    path: Path,
    corpus: ValidatedCorpus,
    manifest: ModelManifest | None = None,
) -> Mapping[str, Prediction]:
    parsed = _read_jsonl(path, name="translation-worthiness predictions")
    expected_ids = set(corpus.row_by_id)
    predictions: dict[str, Prediction] = {}
    for ordinal, value in enumerate(parsed, start=1):
        if frozenset(value) != PREDICTION_FIELDS:
            raise WorthinessCorpusError(
                f"Prediction row {ordinal} does not use the id/score/decision allowlist"
            )
        identifier = value["id"]
        if not isinstance(identifier, str) or identifier not in expected_ids:
            raise WorthinessCorpusError(f"Prediction row {ordinal} has an unknown id")
        if identifier in predictions:
            raise WorthinessCorpusError(f"Prediction row {ordinal} duplicates a prior id")
        score = value["score"]
        if (
            isinstance(score, bool)
            or not isinstance(score, (int, float))
            or not math.isfinite(float(score))
        ):
            raise WorthinessCorpusError(f"Prediction row {ordinal} has an invalid score")
        decision = value["decision"]
        if not isinstance(decision, str) or decision not in DECISIONS:
            raise WorthinessCorpusError(f"Prediction row {ordinal} has an invalid decision")
        if manifest is not None:
            expected_decision = (
                "suppress"
                if float(score) <= manifest.suppress_max
                else "retain"
                if float(score) >= manifest.retain_min
                else "abstain"
            )
            if decision != expected_decision:
                raise WorthinessCorpusError(
                    f"Prediction row {ordinal} is inconsistent with the locked thresholds"
                )
        predictions[identifier] = Prediction(score=float(score), decision=decision)
    if set(predictions) != expected_ids:
        raise WorthinessCorpusError("Predictions do not exactly cover the corpus")
    return predictions


def zero_miss_upper_95(independent_positive_units: int, missed_units: int) -> float | None:
    if independent_positive_units <= 0 or missed_units != 0:
        return None
    return round(1 - math.pow(0.05, 1 / independent_positive_units), 12)


def _aggregate_cell(
    rows: Sequence[CorpusRow], predictions: Mapping[str, Prediction]
) -> dict[str, Any]:
    positives = [row for row in rows if row.label in {"human-worthy", "mixed"}]
    false_suppressions = sum(
        predictions[row.identifier].decision == "suppress" for row in positives
    )
    positive_groups = {row.group_id for row in positives}
    false_suppression_groups = {
        row.group_id for row in positives if predictions[row.identifier].decision == "suppress"
    }
    ambiguous = [row for row in rows if row.label == "ambiguous"]
    decisions = Counter(predictions[row.identifier].decision for row in rows)
    scores = [predictions[row.identifier].score for row in rows]
    return {
        "records": len(rows),
        "knownHumanRecords": len(positives),
        "knownHumanGroups": len(positive_groups),
        "falseSuppressions": false_suppressions,
        "falseSuppressionGroups": len(false_suppression_groups),
        "ambiguousRecords": len(ambiguous),
        "ambiguousSuppressions": sum(
            predictions[row.identifier].decision == "suppress" for row in ambiguous
        ),
        "decisions": {decision: decisions[decision] for decision in DECISIONS},
        "meanScore": round(sum(scores) / len(scores), 12) if scores else None,
        "zeroFalseSuppressionUpper95": zero_miss_upper_95(
            len(positive_groups), len(false_suppression_groups)
        ),
    }


def _strata(
    rows: Sequence[CorpusRow],
    predictions: Mapping[str, Prediction],
    dimensions: Sequence[tuple[str, Any]],
) -> list[dict[str, Any]]:
    cells: dict[tuple[str, ...], list[CorpusRow]] = defaultdict(list)
    for row in rows:
        key = tuple(accessor(row) for _, accessor in dimensions)
        cells[key].append(row)
    result: list[dict[str, Any]] = []
    for key in sorted(cells):
        identity = {dimensions[index][0]: value for index, value in enumerate(key)}
        result.append({**identity, **_aggregate_cell(cells[key], predictions)})
    return result


def _label_decision_matrix(
    rows: Sequence[CorpusRow], predictions: Mapping[str, Prediction]
) -> dict[str, dict[str, int]]:
    matrix = {label: Counter() for label in LABELS}
    for row in rows:
        matrix[row.label][predictions[row.identifier].decision] += 1
    return {
        label: {decision: matrix[label][decision] for decision in DECISIONS} for label in LABELS
    }


def evaluation_report(
    corpus: ValidatedCorpus,
    predictions: Mapping[str, Prediction],
    *,
    phase: str = "locked-test",
    manifest: ModelManifest | None = None,
) -> dict[str, Any]:
    if phase not in EVALUATION_PHASES:
        raise WorthinessCorpusError("Evaluation phase is unsupported")
    selected_split = "calibration" if phase == "calibration-select" else "test"
    rows = tuple(
        row for row in corpus.rows if corpus.split_by_group[row.group_id] == selected_split
    )
    if not rows:
        raise WorthinessCorpusError("Evaluation phase selected no rows")
    split_accessor = lambda row: corpus.split_by_group[row.group_id]  # noqa: E731
    report = {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-evaluation",
        "corpusId": corpus.corpus_id,
        "license": corpus.license_id,
        "researchOnly": True,
        "promotionEligible": False,
        "evaluationPhase": phase,
        "evaluatedSplit": selected_split,
        "confidenceUnit": "synthetic-group-diagnostic-only",
        "overall": _aggregate_cell(rows, predictions),
        "labelDecisionMatrix": _label_decision_matrix(rows, predictions),
        "mixedFacet": _aggregate_cell(
            tuple(row for row in rows if row.label == "mixed"), predictions
        ),
        "bySplit": _strata(rows, predictions, (("split", split_accessor),)),
        "bySourceFamily": _strata(
            rows, predictions, (("sourceFamily", lambda row: row.source_family),)
        ),
        "byOriginFamily": _strata(
            rows, predictions, (("originFamily", lambda row: row.origin_family),)
        ),
        "byScriptFamily": _strata(
            rows, predictions, (("scriptFamily", lambda row: row.script_family),)
        ),
        "byLengthBand": _strata(rows, predictions, (("lengthBand", lambda row: row.length_band),)),
        "byOriginScriptLength": _strata(
            rows,
            predictions,
            (
                ("originFamily", lambda row: row.origin_family),
                ("scriptFamily", lambda row: row.script_family),
                ("lengthBand", lambda row: row.length_band),
            ),
        ),
    }
    if manifest is not None:
        report["experiment"] = {
            "experimentId": manifest.experiment_id,
            "ablation": manifest.ablation,
            "scoreOrientation": SCORE_ORIENTATION,
            "thresholds": {
                "suppressMax": manifest.suppress_max,
                "retainMin": manifest.retain_min,
            },
            "hashes": dict(manifest.hashes),
        }
    return report


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def _publish_report(report: Mapping[str, Any], output: Path | None, inputs: Sequence[Path]) -> None:
    serialized = (canonical_json(report) + "\n").encode("utf-8")
    if output is None:
        sys.stdout.buffer.write(serialized)
        return
    output = output.resolve(strict=False)
    if any(output == path.resolve(strict=False) for path in inputs):
        raise WorthinessCorpusError("Report output must not alias an input")
    if output.exists() and (not output.is_file() or output.is_symlink()):
        raise WorthinessCorpusError("Report output must be a physical file or absent")
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{output.name}.", suffix=".partial", dir=output.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(serialized)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, output)
    finally:
        temporary.unlink(missing_ok=True)


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    validate = subparsers.add_parser("validate", help="validate corpus and split governance")
    evaluate = subparsers.add_parser("evaluate", help="evaluate aggregate prediction safety")
    for command in (validate, evaluate):
        command.add_argument("--corpus", type=Path, default=DEFAULT_CORPUS)
        command.add_argument("--splits", type=Path, default=DEFAULT_SPLITS)
        command.add_argument("--corpus-id", default=CORPUS_ID)
        command.add_argument("--license-id", default=PROJECT_LICENSE)
        command.add_argument("--output", type=Path)
    evaluate.add_argument("--predictions", type=Path, required=True)
    evaluate.add_argument("--model-manifest", type=Path, required=True)
    evaluate.add_argument("--model", type=Path, required=True)
    evaluate.add_argument("--trainer", type=Path, required=True)
    evaluate.add_argument("--phase", choices=EVALUATION_PHASES, required=True)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    try:
        options = parse_arguments(arguments)
        corpus = validate_corpus(
            options.corpus,
            options.splits,
            corpus_id=options.corpus_id,
            license_id=options.license_id,
        )
        if options.command == "validate":
            report = validation_report(corpus)
            inputs = [options.corpus, options.splits]
        else:
            from translation_worthiness_model import (
                feature_contract_sha256,
                validate_model_manifest_identity,
            )

            manifest = load_model_manifest(
                options.model_manifest,
                corpus_path=options.corpus,
                split_path=options.splits,
                trainer_path=options.trainer,
                model_path=options.model,
                predictions_path=options.predictions,
                expected_feature_contract_sha256=feature_contract_sha256(),
                expected_corpus_id=corpus.corpus_id,
                expected_license=corpus.license_id,
            )
            if manifest.ablation != "exact-only":
                validate_model_manifest_identity(
                    options.model.read_bytes(),
                    ablation=manifest.ablation,
                    bucket_count=int(manifest.model["bucketCount"]),
                    ngram_min=int(manifest.model["ngramMin"]),
                    ngram_max=int(manifest.model["ngramMax"]),
                )
            predictions = load_predictions(options.predictions, corpus, manifest)
            report = evaluation_report(corpus, predictions, phase=options.phase, manifest=manifest)
            inputs = [
                options.corpus,
                options.splits,
                options.predictions,
                options.model_manifest,
                options.model,
                options.trainer,
            ]
        _publish_report(report, options.output, inputs)
        return 0
    except WorthinessCorpusError as exc:
        print(f"translation-worthiness corpus error: {exc}", file=sys.stderr)
        return 2
    except OSError:
        print("translation-worthiness corpus error: filesystem operation failed", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
