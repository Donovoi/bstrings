#!/usr/bin/env python3
"""Create two isolated blank human-review packets from the public queue.

This command does not label records, contact reviewers, use a network, or claim
that review occurred.  Each destination is a separate, previously absent,
external physical directory containing the same queue text and instructions but
only its own pseudonymous reviewer placeholder.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import stat
import sys
import tempfile
from collections.abc import Mapping, Sequence
from pathlib import Path
from typing import Any

import translation_worthiness_adjudication as adjudication

SCHEMA_VERSION = 1
PACKET_ARTIFACT = "translation-worthiness-blind-review-packet"
TEMPLATE_ARTIFACT = "translation-worthiness-independent-review"
PACKET_STATE = "blank-unassigned-template"
PACKET_FILES = ("instructions.txt", "packet.json", "review-template.blank.json")
REVIEWER_PLACEHOLDERS = (
    "reviewer-one-pseudonym-placeholder",
    "reviewer-two-pseudonym-placeholder",
)

LABEL_DEFINITIONS = {
    "ambiguous": (
        "Insufficient context or genuinely uncertain; do not guess. This is an abstention label."
    ),
    "human-worthy": (
        "Natural-language material intended for a person and worth preserving for translation."
    ),
    "machine": (
        "Only code, identifiers, structure, encoded data, low-level material, or noise; no "
        "translation-worthy human language."
    ),
    "mixed": (
        "Both translation-worthy human language and machine-oriented material. Any human "
        "material makes this a protected positive."
    ),
}
CONFIDENCE_DEFINITIONS = {
    "high": "The label is clear from the provided text.",
    "low": "Material uncertainty remains; this will require later third-person adjudication.",
    "medium": "The label is more likely than alternatives but some uncertainty remains.",
}
RATIONALE_DEFINITIONS = {
    "code-comment": "Human-language comment or documentation embedded in code.",
    "config-human-value": "Human-language value embedded in configuration.",
    "human-fragment": "Short but translation-worthy human-language fragment.",
    "human-sentence": "Complete translation-worthy human-language sentence.",
    "localized-message": "Localized or non-English human-language message.",
    "log-human-message": "Human-readable message embedded in a log record.",
    "machine-encoded": "Encoded or serialized machine data without human material.",
    "machine-identifier": "Identifier, symbol, path, key, or token without human material.",
    "machine-low-level": "Opcode, register, address, binary, or other low-level material.",
    "machine-noise": "Corrupt, random, or language-like noise without human intent.",
    "mixed-human-machine": "Human material and machine-oriented material coexist.",
    "uncertain-context": "Context is insufficient for a reliable label.",
}


class ReviewPacketError(RuntimeError):
    """Raised when blank packets cannot be produced without weakening isolation."""


def _canonical_json(value: Any) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    ).encode("utf-8")


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


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
            raise ReviewPacketError(f"{name} is unavailable") from exc
        if stat.S_ISLNK(current_stat.st_mode) or _is_reparse(current_stat):
            raise ReviewPacketError(f"{name} contains a link or reparse component")
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
        raise ReviewPacketError(f"{name} is unavailable") from exc
    if (
        not stat.S_ISDIR(source_stat.st_mode)
        or not stat.S_ISDIR(resolved_stat.st_mode)
        or stat.S_ISLNK(source_stat.st_mode)
        or _is_reparse(source_stat)
        or _is_reparse(resolved_stat)
    ):
        raise ReviewPacketError(f"{name} must be a physical directory")
    return resolved


def _prepare_destination(path: Path, *, name: str) -> Path:
    adjudication._require_external(path, name=name)
    if path.exists() or path.is_symlink():
        raise ReviewPacketError(f"{name} must be absent")
    parent = _physical_directory(path.parent, name=f"{name} parent")
    if not path.name or path.name in {".", ".."}:
        raise ReviewPacketError(f"{name} has an invalid directory name")
    destination = parent / path.name
    if destination.exists() or destination.is_symlink():
        raise ReviewPacketError(f"{name} must be absent")
    return destination


def _text_hashes(rows: Sequence[adjudication.QueueRow]) -> list[Mapping[str, str]]:
    return [
        {
            "id": row.identifier,
            "textSha256": _sha256(row.text.encode("utf-8", errors="strict")),
        }
        for row in rows
    ]


def _template(
    rows: Sequence[adjudication.QueueRow], queue_sha256: str, reviewer_placeholder: str
) -> Mapping[str, Any]:
    return {
        "schemaVersion": SCHEMA_VERSION,
        "artifactType": TEMPLATE_ARTIFACT,
        "researchOnly": True,
        "promotionEligible": False,
        "queueSha256": queue_sha256,
        "reviewerId": reviewer_placeholder,
        # Null prevents this generated blank template from claiming a human review.
        "reviewerType": None,
        "independent": True,
        "otherReviewsVisible": False,
        "reviews": [
            {
                "id": value["id"],
                "textSha256": value["textSha256"],
                "label": None,
                "confidence": None,
                "rationaleCodes": [],
            }
            for value in _text_hashes(rows)
        ],
    }


def _packet(
    rows: Sequence[adjudication.QueueRow],
    queue_sha256: str,
    queue_bytes: int,
    reviewer_placeholder: str,
) -> Mapping[str, Any]:
    text_hashes = _text_hashes(rows)
    items = [
        {
            "id": row.identifier,
            "text": row.text,
            "textSha256": text_hash["textSha256"],
        }
        for row, text_hash in zip(rows, text_hashes, strict=True)
    ]
    return {
        "schemaVersion": SCHEMA_VERSION,
        "artifactType": PACKET_ARTIFACT,
        "packetState": PACKET_STATE,
        "researchOnly": True,
        "promotionEligible": False,
        "humanReviewCompleted": False,
        "labelsAssigned": False,
        "networkAccess": "forbidden",
        "queueSha256": queue_sha256,
        "queueBytes": queue_bytes,
        "textBindingsSha256": _sha256(_canonical_json(text_hashes)),
        "reviewerIdPlaceholder": reviewer_placeholder,
        "reviewerIdPolicy": (
            "Replace the placeholder with a privacy-controlled pseudonym; do not use a name, "
            "email address, employer identity, or account identifier."
        ),
        "independencePolicy": (
            "Review every item independently. Do not seek, receive, discuss, or inspect any "
            "other review artifact, model prediction, automatic label, or proposed answer."
        ),
        "instructions": {
            "labelExactlyOne": True,
            "confidenceExactlyOne": True,
            "rationaleCodesAtLeastOne": True,
            "rationaleCodesSortedUnique": True,
            "doNotGuess": True,
            "templateCompletion": (
                "Set reviewerType to human only if a real human performed the review; replace "
                "every null label and confidence and provide at least one rationale code."
            ),
        },
        "taxonomy": LABEL_DEFINITIONS,
        "confidence": CONFIDENCE_DEFINITIONS,
        "rationaleCodes": RATIONALE_DEFINITIONS,
        "items": items,
    }


def _instructions(reviewer_placeholder: str) -> bytes:
    lines = [
        "Translation-worthiness independent blind review",
        "",
        "STATE: BLANK TEMPLATE ONLY. NO LABELS ARE ASSIGNED AND NO HUMAN REVIEW IS CLAIMED.",
        f"Reviewer pseudonym placeholder: {reviewer_placeholder}",
        "",
        "Review only the text in packet.json. Do not use automation, model predictions, or any",
        "other review artifact. Choose exactly one taxonomy label and one confidence for every",
        "row, plus at least one sorted unique rationale code. Use ambiguous rather than guessing.",
        "Replace the reviewer placeholder with a privacy-controlled pseudonym. Set reviewerType",
        "to human only after a real human personally completes every row.",
        "",
        "Labels:",
    ]
    lines.extend(f"- {name}: {definition}" for name, definition in LABEL_DEFINITIONS.items())
    lines.extend(("", "Confidence:"))
    lines.extend(f"- {name}: {definition}" for name, definition in CONFIDENCE_DEFINITIONS.items())
    lines.extend(("", "Rationale codes:"))
    lines.extend(f"- {name}: {definition}" for name, definition in RATIONALE_DEFINITIONS.items())
    return ("\n".join(lines) + "\n").encode("utf-8")


def _write_file(path: Path, value: bytes) -> None:
    with path.open("xb") as handle:
        handle.write(value)
        handle.flush()
        os.fsync(handle.fileno())


def _stage_packet(
    parent: Path,
    rows: Sequence[adjudication.QueueRow],
    queue_sha256: str,
    queue_bytes: int,
    reviewer_placeholder: str,
) -> Path:
    temporary = Path(tempfile.mkdtemp(prefix=".bstrings-review-packet-", dir=parent))
    try:
        _write_file(temporary / "instructions.txt", _instructions(reviewer_placeholder))
        _write_file(
            temporary / "packet.json",
            _canonical_json(_packet(rows, queue_sha256, queue_bytes, reviewer_placeholder)),
        )
        _write_file(
            temporary / "review-template.blank.json",
            _canonical_json(_template(rows, queue_sha256, reviewer_placeholder)),
        )
    except BaseException:
        _remove_known_packet(temporary)
        raise
    return temporary


def _remove_known_packet(path: Path) -> None:
    if not path.exists():
        return
    for name in PACKET_FILES:
        (path / name).unlink(missing_ok=True)
    try:
        path.rmdir()
    except OSError:
        # Never recursively delete unexpected content, even inside staging.
        raise


def create_packets(queue: Path, first: Path, second: Path) -> Mapping[str, Any]:
    rows, resolved_queue, queue_sha256 = adjudication.load_queue(queue)
    adjudication._require_external(resolved_queue, name="annotation queue")
    destinations = (
        _prepare_destination(first, name="first blind review packet"),
        _prepare_destination(second, name="second blind review packet"),
    )
    if destinations[0] == destinations[1]:
        raise ReviewPacketError("Blind review packet destinations must be distinct")
    staged: list[Path] = []
    published: list[Path] = []
    try:
        for destination, reviewer_placeholder in zip(
            destinations, REVIEWER_PLACEHOLDERS, strict=True
        ):
            staged.append(
                _stage_packet(
                    destination.parent,
                    rows,
                    queue_sha256,
                    resolved_queue.stat().st_size,
                    reviewer_placeholder,
                )
            )
        for temporary, destination in zip(staged, destinations, strict=True):
            os.replace(temporary, destination)
            published.append(destination)
        staged.clear()
    except BaseException:
        for temporary in staged:
            _remove_known_packet(temporary)
        for destination in published:
            _remove_known_packet(destination)
        raise
    return {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-review-packet-generation",
        "status": "blank-packets-created",
        "researchOnly": True,
        "promotionEligible": False,
        "humanReviewCompleted": False,
        "labelsAssigned": False,
        "queueSha256": queue_sha256,
        "recordsPerPacket": len(rows),
        "packetCount": 2,
    }


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--queue", type=Path, required=True)
    parser.add_argument("--packet-one", type=Path, required=True)
    parser.add_argument("--packet-two", type=Path, required=True)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    try:
        options = parse_arguments(arguments)
        report = create_packets(options.queue, options.packet_one, options.packet_two)
        print(json.dumps(report, sort_keys=True, separators=(",", ":")))
        return 0
    except (ReviewPacketError, adjudication.AdjudicationError) as exc:
        print(f"translation-worthiness review packet error: {exc}", file=sys.stderr)
        return 2
    except OSError:
        print(
            "translation-worthiness review packet error: filesystem operation failed",
            file=sys.stderr,
        )
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
