#!/usr/bin/env python3
"""Build a research-only corpus from two independent human review artifacts.

The command never labels source text itself.  It verifies that two distinct
human reviewers independently covered the exact acquisition queue.  Conflicts
and low-confidence agreements require a third, non-independent adjudication.
The resulting corpus is split by upstream project so related files from one
project cannot cross train, calibration, and locked-test boundaries.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import stat
import sys
import tempfile
from collections import Counter
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

from translation_worthiness_corpus import (
    LABELS,
    LENGTH_BAND_NAMES,
    ORIGIN_FAMILIES,
    SCRIPT_FAMILIES,
    SOURCE_FAMILIES,
    length_band,
)

SCHEMA_VERSION = 1
CORPUS_ID = "translation-worthiness-reviewed-public-v1"
CORPUS_LICENSE = "reviewed-public-source-manifest-v1"
QUEUE_LABEL = "UNLABELED"
SPLIT_POLICY = "duplicate-linked-project-component-sha256-rank-v1"
SPLIT_NAMES = ("calibration", "test", "train")

MAX_QUEUE_BYTES = 512 * 1024 * 1024
MAX_REVIEW_BYTES = 128 * 1024 * 1024
MAX_LINE_BYTES = 64 * 1024
MAX_RECORDS = 100_000
IDENTIFIER_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]{0,63}$", re.ASCII)
REVISION_PATTERN = re.compile(r"^[0-9a-f]{40}$", re.ASCII)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$", re.ASCII)
LICENSE_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9.+-]{0,63}$", re.ASCII)
PROJECT_PATTERN = re.compile(
    r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}/[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
    re.ASCII,
)
SOURCE_PROVENANCE = frozenset(
    {
        "upstream-repository-first-party-source-path",
        "upstream-repository-first-party-example-path",
    }
)
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]

QUEUE_FIELDS = frozenset(
    {
        "schemaVersion",
        "id",
        "groupId",
        "text",
        "originFamily",
        "scriptFamily",
        "lengthBand",
        "sourceId",
        "sourceProject",
        "sourceRevision",
        "sourcePath",
        "sourceOrdinal",
        "sourceKind",
        "sourceProvenance",
        "licenseId",
        "label",
        "reviewState",
        "requiredIndependentReviews",
        "researchOnly",
        "promotionEligible",
    }
)
REVIEW_DOCUMENT_FIELDS = frozenset(
    {
        "schemaVersion",
        "artifactType",
        "researchOnly",
        "promotionEligible",
        "queueSha256",
        "reviewerId",
        "reviewerType",
        "independent",
        "otherReviewsVisible",
        "reviews",
    }
)
REVIEW_FIELDS = frozenset({"id", "textSha256", "label", "confidence", "rationaleCodes"})
CONFIDENCE_LEVELS = ("high", "low", "medium")
RATIONALE_CODES = frozenset(
    {
        "code-comment",
        "config-human-value",
        "human-fragment",
        "human-sentence",
        "localized-message",
        "log-human-message",
        "machine-encoded",
        "machine-identifier",
        "machine-low-level",
        "machine-noise",
        "mixed-human-machine",
        "uncertain-context",
    }
)


class AdjudicationError(RuntimeError):
    """Raised when acquisition or review evidence cannot be trusted."""


@dataclass(frozen=True)
class QueueRow:
    value: Mapping[str, Any]
    identifier: str
    text: str
    group_id: str
    source_project: str


@dataclass(frozen=True)
class Review:
    label: str
    confidence: str
    rationale_codes: tuple[str, ...]


@dataclass(frozen=True)
class ReviewDocument:
    reviewer_id: str
    reviews: Mapping[str, Review]


def _is_reparse(stat_result: os.stat_result) -> bool:
    attributes = getattr(stat_result, "st_file_attributes", 0)
    flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return bool(attributes & flag)


def _reject_reparse_components(path: Path, *, name: str) -> None:
    current = path.absolute()
    while True:
        try:
            current_stat = os.lstat(current)
        except OSError as exc:
            raise AdjudicationError(f"{name} is unavailable") from exc
        if stat.S_ISLNK(current_stat.st_mode) or _is_reparse(current_stat):
            raise AdjudicationError(f"{name} contains a link or reparse component")
        if current.parent == current:
            return
        current = current.parent


def _physical_directory(path: Path, *, name: str) -> Path:
    _reject_reparse_components(path, name=name)
    try:
        source_stat = os.lstat(path)
        resolved = path.resolve(strict=True)
        resolved_stat = os.lstat(resolved)
    except OSError as exc:
        raise AdjudicationError(f"{name} is unavailable") from exc
    if (
        not stat.S_ISDIR(source_stat.st_mode)
        or not stat.S_ISDIR(resolved_stat.st_mode)
        or stat.S_ISLNK(source_stat.st_mode)
        or _is_reparse(source_stat)
        or _is_reparse(resolved_stat)
    ):
        raise AdjudicationError(f"{name} must be a physical directory")
    return resolved


def _require_external(path: Path, *, name: str) -> None:
    repository = REPOSITORY_ROOT.resolve(strict=True)
    resolved = path.resolve(strict=False)
    if resolved == repository or repository in resolved.parents:
        raise AdjudicationError(f"{name} must remain outside the repository")


def _physical_file(path: Path, *, name: str, maximum_bytes: int) -> Path:
    _reject_reparse_components(path, name=name)
    try:
        source_stat = os.lstat(path)
        resolved = path.resolve(strict=True)
        resolved_stat = os.lstat(resolved)
    except OSError as exc:
        raise AdjudicationError(f"{name} is unavailable") from exc
    if (
        not stat.S_ISREG(source_stat.st_mode)
        or not stat.S_ISREG(resolved_stat.st_mode)
        or stat.S_ISLNK(source_stat.st_mode)
        or _is_reparse(source_stat)
        or _is_reparse(resolved_stat)
        or source_stat.st_size <= 0
        or source_stat.st_size > maximum_bytes
    ):
        raise AdjudicationError(f"{name} must be a bounded physical file")
    return resolved


def _reject_json_constant(value: str) -> None:
    del value
    raise AdjudicationError("JSON input contains a non-finite numeric constant")


def _unique_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise AdjudicationError("JSON input contains a duplicate object property")
        value[key] = item
    return value


def _strict_json(raw: bytes, *, name: str) -> Any:
    try:
        return json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_json_constant,
        )
    except AdjudicationError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise AdjudicationError(f"{name} is not strict UTF-8 JSON") from exc


def _read_json(path: Path, *, name: str, maximum_bytes: int) -> tuple[Mapping[str, Any], Path]:
    resolved = _physical_file(path, name=name, maximum_bytes=maximum_bytes)
    value = _strict_json(resolved.read_bytes(), name=name)
    if not isinstance(value, dict):
        raise AdjudicationError(f"{name} must be a JSON object")
    return value, resolved


def _sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _sha256_file(path: Path) -> str:
    hasher = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            hasher.update(chunk)
    return hasher.hexdigest()


def _canonical_json(value: Mapping[str, Any]) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    ).encode("utf-8")


def _identifier(value: Any, *, field: str) -> str:
    if not isinstance(value, str) or not IDENTIFIER_PATTERN.fullmatch(value):
        raise AdjudicationError(f"Queue or review has an invalid {field}")
    return value


def _source_project(value: Any) -> str:
    if not isinstance(value, str) or not PROJECT_PATTERN.fullmatch(value):
        raise AdjudicationError("Queue has an invalid sourceProject")
    return value


def _strict_relative_path(value: Any) -> str:
    if not isinstance(value, str) or not value or len(value) > 512 or "\\" in value:
        raise AdjudicationError("Queue has an invalid sourcePath")
    parsed = PurePosixPath(value)
    if parsed.is_absolute() or any(part in {"", ".", ".."} for part in parsed.parts):
        raise AdjudicationError("Queue has an invalid sourcePath")
    return value


def load_queue(path: Path) -> tuple[tuple[QueueRow, ...], Path, str]:
    resolved = _physical_file(path, name="annotation queue", maximum_bytes=MAX_QUEUE_BYTES)
    queue_sha256 = _sha256_file(resolved)
    identifiers: set[str] = set()
    prior_identifier = ""
    rows: list[QueueRow] = []
    with resolved.open("rb") as handle:
        for _ordinal, raw_line in enumerate(handle, start=1):
            if not raw_line.endswith(b"\n") or len(raw_line) > MAX_LINE_BYTES:
                raise AdjudicationError("Annotation queue has an unterminated or oversized row")
            value = _strict_json(raw_line, name="annotation queue row")
            if not isinstance(value, dict) or frozenset(value) != QUEUE_FIELDS:
                raise AdjudicationError("Annotation queue row does not use the schema-1 allowlist")
            if (
                type(value["schemaVersion"]) is not int
                or value["schemaVersion"] != SCHEMA_VERSION
                or value["label"] != QUEUE_LABEL
                or value["reviewState"] != "pending"
                or type(value["requiredIndependentReviews"]) is not int
                or value["requiredIndependentReviews"] != 2
                or value["researchOnly"] is not True
                or value["promotionEligible"] is not False
            ):
                raise AdjudicationError("Annotation queue row has an invalid research contract")
            identifier = _identifier(value["id"], field="id")
            group_id = _identifier(value["groupId"], field="groupId")
            source_project = _source_project(value["sourceProject"])
            _identifier(value["sourceId"], field="sourceId")
            if identifier in identifiers or identifier <= prior_identifier:
                raise AdjudicationError("Annotation queue IDs must be sorted and unique")
            identifiers.add(identifier)
            prior_identifier = identifier
            text = value["text"]
            if (
                not isinstance(text, str)
                or not text
                or "\x00" in text
                or any(0xD800 <= ord(character) <= 0xDFFF for character in text)
                or any(ord(character) < 32 and character not in "\t\r\n" for character in text)
            ):
                raise AdjudicationError("Annotation queue contains invalid text")
            if value["lengthBand"] not in LENGTH_BAND_NAMES or value["lengthBand"] != length_band(
                text
            ):
                raise AdjudicationError("Annotation queue has an invalid lengthBand")
            if value["originFamily"] not in ORIGIN_FAMILIES:
                raise AdjudicationError("Annotation queue has an invalid originFamily")
            if value["scriptFamily"] not in SCRIPT_FAMILIES:
                raise AdjudicationError("Annotation queue has an invalid scriptFamily")
            if value["sourceKind"] not in SOURCE_FAMILIES:
                raise AdjudicationError("Annotation queue has an invalid sourceKind")
            if value["sourceProvenance"] not in SOURCE_PROVENANCE:
                raise AdjudicationError("Annotation queue has an invalid sourceProvenance")
            if not isinstance(value["sourceOrdinal"], int) or value["sourceOrdinal"] < 1:
                raise AdjudicationError("Annotation queue has an invalid sourceOrdinal")
            if not isinstance(value["sourceRevision"], str) or not REVISION_PATTERN.fullmatch(
                value["sourceRevision"]
            ):
                raise AdjudicationError("Annotation queue has an invalid sourceRevision")
            _strict_relative_path(value["sourcePath"])
            if not isinstance(value["licenseId"], str) or not LICENSE_PATTERN.fullmatch(
                value["licenseId"]
            ):
                raise AdjudicationError("Annotation queue has an invalid licenseId")
            rows.append(
                QueueRow(
                    value=value,
                    identifier=identifier,
                    text=text,
                    group_id=group_id,
                    source_project=source_project,
                )
            )
            if len(rows) > MAX_RECORDS:
                raise AdjudicationError("Annotation queue exceeds the record limit")
    if not rows:
        raise AdjudicationError("Annotation queue is empty")
    return tuple(rows), resolved, queue_sha256


def load_review_document(
    path: Path,
    *,
    name: str,
    queue_rows: Sequence[QueueRow],
    queue_sha256: str,
    adjudication: bool,
    required_ids: set[str],
) -> tuple[ReviewDocument, Path]:
    value, resolved = _read_json(path, name=name, maximum_bytes=MAX_REVIEW_BYTES)
    if frozenset(value) != REVIEW_DOCUMENT_FIELDS:
        raise AdjudicationError(f"{name} does not use the schema-1 allowlist")
    expected_artifact = (
        "translation-worthiness-adjudication"
        if adjudication
        else "translation-worthiness-independent-review"
    )
    if (
        type(value["schemaVersion"]) is not int
        or value["schemaVersion"] != SCHEMA_VERSION
        or value["artifactType"] != expected_artifact
        or value["researchOnly"] is not True
        or value["promotionEligible"] is not False
        or value["queueSha256"] != queue_sha256
        or value["reviewerType"] != "human"
        or value["independent"] is not (not adjudication)
        or value["otherReviewsVisible"] is not adjudication
    ):
        raise AdjudicationError(f"{name} has an invalid review contract")
    reviewer_id = _identifier(value["reviewerId"], field="reviewerId")
    raw_reviews = value["reviews"]
    if not isinstance(raw_reviews, list):
        raise AdjudicationError(f"{name} reviews must be an array")
    text_hashes = {row.identifier: _sha256_bytes(row.text.encode("utf-8")) for row in queue_rows}
    reviews: dict[str, Review] = {}
    prior_identifier = ""
    for item in raw_reviews:
        if not isinstance(item, dict) or frozenset(item) != REVIEW_FIELDS:
            raise AdjudicationError(f"{name} review does not use the schema-1 allowlist")
        identifier = _identifier(item["id"], field="review id")
        if identifier in reviews or identifier <= prior_identifier:
            raise AdjudicationError(f"{name} review IDs must be sorted and unique")
        prior_identifier = identifier
        if identifier not in text_hashes or item["textSha256"] != text_hashes[identifier]:
            raise AdjudicationError(f"{name} does not bind the exact queue text")
        if item["label"] not in LABELS or item["confidence"] not in CONFIDENCE_LEVELS:
            raise AdjudicationError(f"{name} has an invalid label or confidence")
        rationale_codes = item["rationaleCodes"]
        if (
            not isinstance(rationale_codes, list)
            or not rationale_codes
            or rationale_codes != sorted(set(rationale_codes))
            or any(code not in RATIONALE_CODES for code in rationale_codes)
        ):
            raise AdjudicationError(f"{name} has invalid rationale codes")
        reviews[identifier] = Review(
            label=item["label"],
            confidence=item["confidence"],
            rationale_codes=tuple(rationale_codes),
        )
    if set(reviews) != required_ids:
        raise AdjudicationError(f"{name} does not cover exactly the required queue IDs")
    return ReviewDocument(reviewer_id=reviewer_id, reviews=reviews), resolved


def _split_projects(
    rows: Sequence[QueueRow],
) -> tuple[Mapping[str, str], Mapping[str, str], Mapping[str, list[str]]]:
    projects = sorted({row.source_project for row in rows})
    parent = {project: project for project in projects}

    def find(project: str) -> str:
        while parent[project] != project:
            parent[project] = parent[parent[project]]
            project = parent[project]
        return project

    def union(left: str, right: str) -> None:
        left_root = find(left)
        right_root = find(right)
        if left_root == right_root:
            return
        lower, upper = sorted((left_root, right_root))
        parent[upper] = lower

    projects_by_text: dict[str, set[str]] = {}
    for row in rows:
        projects_by_text.setdefault(row.text, set()).add(row.source_project)
    for linked_projects in projects_by_text.values():
        ordered = sorted(linked_projects)
        for project in ordered[1:]:
            union(ordered[0], project)

    components_by_root: dict[str, list[str]] = {}
    for project in projects:
        components_by_root.setdefault(find(project), []).append(project)
    components = sorted(tuple(sorted(members)) for members in components_by_root.values())
    if len(components) < 3:
        raise AdjudicationError("At least three duplicate-isolated project components are required")

    component_identity = {
        members: hashlib.sha256("\0".join(members).encode("utf-8")).hexdigest()
        for members in components
    }
    ranked = sorted(
        components,
        key=lambda members: hashlib.sha256(
            f"{SPLIT_POLICY}\0{component_identity[members]}".encode()
        ).digest(),
    )
    calibration_count = max(1, round(len(ranked) * 0.15))
    test_count = max(1, round(len(ranked) * 0.15))
    train_count = len(ranked) - calibration_count - test_count
    if train_count < 1:
        train_count = 1
        if calibration_count > test_count:
            calibration_count -= 1
        else:
            test_count -= 1
    project_split: dict[str, str] = {}
    component_group: dict[str, str] = {}
    for component_index, members in enumerate(ranked):
        if component_index < train_count:
            split = "train"
        elif component_index < train_count + calibration_count:
            split = "calibration"
        else:
            split = "test"
        group_id = f"public-component-{component_identity[members][:16]}"
        for project in members:
            project_split[project] = split
            component_group[project] = group_id
    groups_by_split: dict[str, set[str]] = {name: set() for name in SPLIT_NAMES}
    for row in rows:
        groups_by_split[project_split[row.source_project]].add(component_group[row.source_project])
    if any(not groups_by_split[name] for name in SPLIT_NAMES):
        raise AdjudicationError("Project-disjoint split left an empty role")
    return (
        project_split,
        component_group,
        {name: sorted(groups_by_split[name]) for name in SPLIT_NAMES},
    )


def _write_file(path: Path, payload: bytes) -> None:
    with path.open("xb") as handle:
        handle.write(payload)
        handle.flush()
        os.fsync(handle.fileno())


def _jsonl_bytes(rows: Sequence[Mapping[str, Any]]) -> bytes:
    return b"".join(_canonical_json(row) for row in rows)


def build_bundle(
    queue_path: Path,
    review_a_path: Path,
    review_b_path: Path,
    output_directory: Path,
    adjudication_path: Path | None = None,
) -> Mapping[str, Any]:
    queue_rows, resolved_queue, queue_sha256 = load_queue(queue_path)
    _require_external(resolved_queue, name="annotation queue")
    all_ids = {row.identifier for row in queue_rows}
    review_a, resolved_a = load_review_document(
        review_a_path,
        name="review A",
        queue_rows=queue_rows,
        queue_sha256=queue_sha256,
        adjudication=False,
        required_ids=all_ids,
    )
    review_b, resolved_b = load_review_document(
        review_b_path,
        name="review B",
        queue_rows=queue_rows,
        queue_sha256=queue_sha256,
        adjudication=False,
        required_ids=all_ids,
    )
    _require_external(resolved_a, name="review A")
    _require_external(resolved_b, name="review B")
    if review_a.reviewer_id == review_b.reviewer_id:
        raise AdjudicationError("Independent reviews must use distinct human reviewer IDs")
    needs_adjudication = {
        identifier
        for identifier in all_ids
        if review_a.reviews[identifier].label != review_b.reviews[identifier].label
        or review_a.reviews[identifier].confidence == "low"
        or review_b.reviews[identifier].confidence == "low"
    }
    adjudication: ReviewDocument | None = None
    resolved_adjudication: Path | None = None
    if needs_adjudication:
        if adjudication_path is None:
            raise AdjudicationError("Conflicts or low-confidence reviews require adjudication")
        adjudication, resolved_adjudication = load_review_document(
            adjudication_path,
            name="adjudication",
            queue_rows=queue_rows,
            queue_sha256=queue_sha256,
            adjudication=True,
            required_ids=needs_adjudication,
        )
        _require_external(resolved_adjudication, name="adjudication")
        if adjudication.reviewer_id in {review_a.reviewer_id, review_b.reviewer_id}:
            raise AdjudicationError("Adjudication requires a third human reviewer")
        if any(review.confidence == "low" for review in adjudication.reviews.values()):
            raise AdjudicationError("Adjudication cannot remain low confidence")
    elif adjudication_path is not None:
        raise AdjudicationError("An adjudication artifact was supplied without conflicts")

    final_labels: dict[str, str] = {}
    for identifier in all_ids:
        if identifier in needs_adjudication:
            assert adjudication is not None
            final_labels[identifier] = adjudication.reviews[identifier].label
        else:
            final_labels[identifier] = review_a.reviews[identifier].label

    project_split, component_group, groups_by_split = _split_projects(queue_rows)
    labels_by_text: dict[str, set[str]] = {}
    for row in queue_rows:
        labels_by_text.setdefault(row.text, set()).add(final_labels[row.identifier])
    context_conflict_texts = {text for text, labels in labels_by_text.items() if len(labels) > 1}
    corpus_rows: list[Mapping[str, Any]] = []
    provenance_rows: list[Mapping[str, Any]] = []
    fasttext_rows: list[Mapping[str, Any]] = []
    for row in queue_rows:
        corpus_rows.append(
            {
                "schemaVersion": SCHEMA_VERSION,
                "id": row.identifier,
                "text": row.text,
                "label": final_labels[row.identifier],
                "groupId": component_group[row.source_project],
                "sourceFamily": row.value["sourceKind"],
                "originFamily": row.value["originFamily"],
                "scriptFamily": row.value["scriptFamily"],
                "lengthBand": row.value["lengthBand"],
                "license": CORPUS_LICENSE,
            }
        )
        provenance_rows.append(
            {
                "id": row.identifier,
                "sourceId": row.value["sourceId"],
                "sourceGroupId": row.group_id,
                "corpusGroupId": component_group[row.source_project],
                "sourceProject": row.source_project,
                "sourceRevision": row.value["sourceRevision"],
                "sourcePath": row.value["sourcePath"],
                "sourceOrdinal": row.value["sourceOrdinal"],
                "sourceKind": row.value["sourceKind"],
                "sourceProvenance": row.value["sourceProvenance"],
                "licenseId": row.value["licenseId"],
                "split": project_split[row.source_project],
                "reviewDisposition": (
                    "adjudicated" if row.identifier in needs_adjudication else "agreed"
                ),
                "fastTextDisposition": (
                    "excluded-adjudicated"
                    if row.identifier in needs_adjudication
                    else (
                        "excluded-context-conflict"
                        if row.text in context_conflict_texts
                        else "eligible-dual-agreement"
                    )
                ),
            }
        )
        if row.identifier not in needs_adjudication and row.text not in context_conflict_texts:
            fasttext_rows.append(
                {
                    "id": row.identifier,
                    "groupId": component_group[row.source_project],
                    "split": project_split[row.source_project],
                    "text": row.text,
                    "label": final_labels[row.identifier],
                    "sourceFamily": row.value["sourceKind"],
                    "originFamily": row.value["originFamily"],
                    "scriptFamily": row.value["scriptFamily"],
                    "lengthBand": row.value["lengthBand"],
                    "license": row.value["licenseId"],
                    "adjudication": {
                        "method": "dual-independent-agreement",
                        "labels": [
                            review_a.reviews[row.identifier].label,
                            review_b.reviews[row.identifier].label,
                        ],
                    },
                }
            )
    corpus_bytes = _jsonl_bytes(corpus_rows)
    fasttext_corpus_bytes = _canonical_json(
        {
            "schemaVersion": SCHEMA_VERSION,
            "artifactType": "translation-worthiness-dual-adjudicated-corpus",
            "researchOnly": True,
            "promotionEligible": False,
            "corpusId": CORPUS_ID,
            "adjudicationPolicy": {
                "method": "dual-independent-agreement",
                "adjudicatorsPerRow": 2,
                "independent": True,
                "agreementRequired": True,
            },
            "rows": fasttext_rows,
        }
    )
    splits_bytes = _canonical_json(
        {
            "schemaVersion": SCHEMA_VERSION,
            "corpusId": CORPUS_ID,
            "license": CORPUS_LICENSE,
            "splits": groups_by_split,
        }
    )
    provenance_bytes = _canonical_json(
        {
            "schemaVersion": SCHEMA_VERSION,
            "artifactType": "translation-worthiness-reviewed-provenance",
            "researchOnly": True,
            "promotionEligible": False,
            "corpusId": CORPUS_ID,
            "queueSha256": queue_sha256,
            "reviewArtifacts": {
                "reviewA": _sha256_file(resolved_a),
                "reviewB": _sha256_file(resolved_b),
                "adjudication": (
                    _sha256_file(resolved_adjudication)
                    if resolved_adjudication is not None
                    else None
                ),
            },
            "reviewers": {
                "reviewA": review_a.reviewer_id,
                "reviewB": review_b.reviewer_id,
                "adjudicator": adjudication.reviewer_id if adjudication is not None else None,
            },
            "reviewEvidenceBoundary": (
                "reviewer identity, humanity, independence, and review isolation are "
                "attested by the supplied artifacts and are not technically proven"
            ),
            "records": provenance_rows,
        }
    )
    report = {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-adjudication",
        "researchOnly": True,
        "promotionEligible": False,
        "status": "complete",
        "corpusId": CORPUS_ID,
        "records": len(queue_rows),
        "groups": len(set(component_group.values())),
        "sourceGroups": len({row.group_id for row in queue_rows}),
        "projects": len({row.source_project for row in queue_rows}),
        "duplicateIsolatedProjectComponents": len(set(component_group.values())),
        "adjudicatedRecords": len(needs_adjudication),
        "fastTextEligibleRecords": len(fasttext_rows),
        "fastTextExcludedContextConflictRecords": sum(
            row.text in context_conflict_texts for row in queue_rows
        ),
        "labels": dict(sorted(Counter(final_labels.values()).items())),
        "artifacts": {
            "corpus.jsonl": _sha256_bytes(corpus_bytes),
            "dual-adjudicated-corpus.json": _sha256_bytes(fasttext_corpus_bytes),
            "splits.json": _sha256_bytes(splits_bytes),
            "provenance.json": _sha256_bytes(provenance_bytes),
        },
        "inputs": {
            "queue": _sha256_file(resolved_queue),
            "reviewA": _sha256_file(resolved_a),
            "reviewB": _sha256_file(resolved_b),
            "adjudication": (
                _sha256_file(resolved_adjudication) if resolved_adjudication is not None else None
            ),
        },
        "splitPolicy": SPLIT_POLICY,
    }
    report_bytes = _canonical_json(report)

    if output_directory.exists() or output_directory.is_symlink() or not output_directory.name:
        raise AdjudicationError("Output directory must be absent beneath a physical parent")
    parent = _physical_directory(output_directory.parent, name="output parent")
    output = parent / output_directory.name
    _require_external(output, name="adjudication output")
    temporary = Path(
        tempfile.mkdtemp(prefix=f".{output.name}.", suffix=".partial", dir=output.parent)
    )
    try:
        _write_file(temporary / "corpus.jsonl", corpus_bytes)
        _write_file(temporary / "dual-adjudicated-corpus.json", fasttext_corpus_bytes)
        _write_file(temporary / "splits.json", splits_bytes)
        _write_file(temporary / "provenance.json", provenance_bytes)
        _write_file(temporary / "report.json", report_bytes)
        os.replace(temporary, output)
    finally:
        if temporary.exists():
            shutil.rmtree(temporary)
    return report


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--queue", required=True, type=Path)
    parser.add_argument("--review-a", required=True, type=Path)
    parser.add_argument("--review-b", required=True, type=Path)
    parser.add_argument("--adjudication", type=Path)
    parser.add_argument("--output-directory", required=True, type=Path)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    try:
        options = parse_arguments(arguments)
        report = build_bundle(
            options.queue,
            options.review_a,
            options.review_b,
            options.output_directory,
            options.adjudication,
        )
        print(_canonical_json(report).decode("utf-8"), end="")
        return 0
    except AdjudicationError as exc:
        print(f"translation-worthiness adjudication error: {exc}", file=sys.stderr)
        return 2
    except OSError:
        print(
            "translation-worthiness adjudication error: filesystem operation failed",
            file=sys.stderr,
        )
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
