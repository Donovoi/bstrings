#!/usr/bin/env python3
"""Adapt the pinned ICDAR 2019 SROIE conversion to the CORD OCR scorer.

The adapter is deliberately split-neutral: callers choose either the pinned
``train`` or ``test`` parquet, while any calibration/holdout policy remains the
responsibility of the acceptance orchestrator.  Only the image, image-size,
localized transcription, and bounding-box columns are read.  SROIE's ``words``
entries are localized transcription regions (and may themselves contain
spaces), so each source entry becomes one ``CordLine``, ``CordWord``, and
``CordPhysicalRow``.  This preserves source segmentation instead of inventing
line membership; the CORD scorer's overlap components can still reconcile a
worker that joins or splits adjacent regions.

Reference text is normalized once, deterministically: Unicode NFC followed by
collapsing every run of Unicode whitespace to one ASCII space.  Empty or NUL
containing normalized transcriptions fail closed.
"""

from __future__ import annotations

import stat
import unicodedata
from collections.abc import Mapping, Sequence
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any

from benchmark_ocr import BenchmarkError, canonical_json, sha256_bytes, sha256_file
from benchmark_ocr_cord import (
    OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION,
    CordDocument,
    CordLine,
    CordPhysicalRow,
    CordWord,
    ExtractedCorpus,
    Prediction,
    _atomic_write,
    _embedded_image_dimensions,
    _image_extension,
    convex_hull,
    score_document,
    score_records,
)

SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION = 2
SROIE_BBOX_REPAIR_AUDIT_SCHEMA_VERSION = 1
SROIE_PROTOCOL = "bstrings-ICDAR2019-SROIE-parquet-adapter-v3"
SROIE_PRIMARY_TEXT_NORMALIZATION = "unicode-nfc-casefold-v1"
SROIE_DIAGNOSTIC_TEXT_NORMALIZATION = "unicode-nfc-case-sensitive-v1"
SROIE_COMMIT = "bffe40c26759f3376ec2b3ae9031dbba54cd587c"
SROIE_REPOSITORY = "jsdnrs/ICDAR2019-SROIE"
SROIE_DUPLICATE_IMAGE_POLICY = (
    "train-deduplicate-by-image-and-source-payload-digest-test-score-all-v3"
)
SROIE_BBOX_REPAIR_POLICY = "single-axis-zero-extent-minimal-in-bounds-one-pixel-v1"
SROIE_FILES: dict[str, dict[str, object]] = {
    "train": {
        "filename": "train-00000-of-00001.parquet",
        "bytes": 318_620_215,
        "sha256": "b18c16b4d8481e5e4537a1700e4616907fe4acd92d6362a7e430b0e866213887",
        "rows": 626,
        "rowGroups": 7,
    },
    "test": {
        "filename": "test-00000-of-00001.parquet",
        "bytes": 191_045_976,
        "sha256": "04f8f31b45944cc6e6459a7a95c851a721fc93ffec0a5c29ece9ded734a684c2",
        "rows": 361,
        "rowGroups": 4,
    },
}
for _file_config in SROIE_FILES.values():
    _file_config["url"] = (
        f"https://huggingface.co/datasets/{SROIE_REPOSITORY}/resolve/"
        f"{SROIE_COMMIT}/data/{_file_config['filename']}"
    )


@dataclass(frozen=True)
class ParsedSroieAnnotation:
    """A validated SROIE row and its scorer-native annotation objects."""

    document: dict[str, Any]
    raw_image: bytes
    source_image_path: str
    width: int
    height: int
    lines: tuple[CordLine, ...]
    words: tuple[CordWord, ...]
    rows: tuple[CordPhysicalRow, ...]
    roi_polygon: tuple[tuple[float, float], ...]
    source_payload_sha256: str
    scoring_annotation_sha256: str
    bbox_repairs: tuple[dict[str, Any], ...]


@dataclass(frozen=True, kw_only=True)
class ExtractedSroieCorpus(ExtractedCorpus):
    """CORD-compatible corpus plus the privacy-safe duplicate-selection audit."""

    duplicate_audit: Path
    duplicate_audit_sha256: str
    bbox_repair_audit: Path
    bbox_repair_audit_sha256: str
    bbox_repair_records_sha256: str
    source_payload_identities_sha256: str
    scoring_annotation_identities_sha256: str
    source_region_count: int
    repaired_region_count: int
    source_image_sha256s: tuple[str, ...]
    source_row_count: int
    excluded_row_indices: tuple[int, ...]


@dataclass(frozen=True)
class _RowIdentity:
    row_index: int
    image_sha256: str
    source_payload_sha256: str
    scoring_annotation_sha256: str
    source_region_count: int
    repaired_region_count: int
    repair_records_sha256: str


@dataclass(frozen=True)
class _BBoxRepair:
    source_bbox: tuple[int, int, int, int]
    scoring_bbox: tuple[int, int, int, int]
    degenerate_axis: str
    direction: str


def _casefold_text(value: str) -> str:
    """Normalize comparison text without changing stored OCR evidence."""

    return unicodedata.normalize("NFC", value).casefold()


def _casefold_document(document: CordDocument) -> CordDocument:
    words = tuple(replace(word, text=_casefold_text(word.text)) for word in document.words)
    rows = tuple(
        replace(
            row,
            text=_casefold_text(row.text),
            words=tuple(replace(word, text=_casefold_text(word.text)) for word in row.words),
        )
        for row in document.rows
    )
    lines = tuple(replace(line, text=_casefold_text(line.text)) for line in document.lines)
    return replace(document, words=words, rows=rows, lines=lines)


def score_document_case_insensitive(
    document: CordDocument, predictions: Sequence[Prediction]
) -> dict[str, Any]:
    """Score exact SROIE text after NFC casefold while preserving all other semantics."""

    return score_document(
        _casefold_document(document),
        tuple(
            replace(prediction, text=_casefold_text(prediction.text)) for prediction in predictions
        ),
    )


def score_records_case_insensitive(
    corpus: ExtractedCorpus, records: Sequence[dict[str, Any]]
) -> dict[str, Any]:
    """Apply the SROIE case-insensitive profile to in-memory scoring views only."""

    scoring_corpus = replace(
        corpus,
        documents=tuple(_casefold_document(document) for document in corpus.documents),
    )
    scoring_records = []
    for record in records:
        value = dict(record)
        text = value.get("text")
        if isinstance(text, str):
            value["text"] = _casefold_text(text)
        scoring_records.append(value)
    return score_records(scoring_corpus, scoring_records)


def _split_config(split: str) -> dict[str, object]:
    config = SROIE_FILES.get(split)
    if config is None:
        raise BenchmarkError("The requested SROIE split is unsupported", stage="corpus-verify")
    return config


def _regular_file_snapshot(path: Path) -> tuple[int, int, int, int]:
    try:
        metadata = path.lstat()
    except OSError as exc:
        raise BenchmarkError(
            "The pinned SROIE parquet is unavailable", stage="corpus-verify"
        ) from exc
    if path.is_symlink() or not stat.S_ISREG(metadata.st_mode):
        raise BenchmarkError(
            "The pinned SROIE parquet is not a physical regular file",
            stage="corpus-verify",
        )
    return (
        metadata.st_size,
        metadata.st_dev,
        metadata.st_ino,
        metadata.st_mtime_ns,
    )


def verify_sroie_parquet(path: Path, split: str = "train") -> str:
    """Verify one complete physical pinned parquet before any row is read."""

    config = _split_config(split)
    if path.name != config["filename"]:
        raise BenchmarkError(
            f"The SROIE parquet basename does not match the pinned {split} split",
            stage="corpus-verify",
        )
    before = _regular_file_snapshot(path)
    if before[0] != config["bytes"]:
        raise BenchmarkError(
            f"The SROIE parquet byte length does not match the pinned {split} split",
            stage="corpus-verify",
        )
    try:
        digest = sha256_file(path)
    except OSError as exc:
        raise BenchmarkError(
            "The pinned SROIE parquet could not be hashed", stage="corpus-verify"
        ) from exc
    after = _regular_file_snapshot(path)
    if before != after:
        raise BenchmarkError(
            "The pinned SROIE parquet changed while it was being verified",
            stage="corpus-verify",
        )
    if digest != config["sha256"]:
        raise BenchmarkError(
            f"The SROIE parquet SHA-256 does not match the pinned {split} split",
            stage="corpus-verify",
        )
    return digest


def _exact_mapping(value: Any, keys: set[str], label: str) -> dict[str, Any]:
    if not isinstance(value, Mapping) or set(value) != keys:
        raise BenchmarkError(
            f"A SROIE {label} value does not match the pinned schema",
            stage="annotation-parse",
        )
    return dict(value)


def _positive_integer(value: Any, label: str) -> int:
    if type(value) is not int or value <= 0:
        raise BenchmarkError(
            f"A SROIE {label} value is not a positive integer",
            stage="annotation-parse",
        )
    return value


def _normalized_transcription(value: Any, index: int) -> str:
    if not isinstance(value, str) or "\x00" in value:
        raise BenchmarkError(
            f"SROIE transcription {index} is not a valid string",
            stage="annotation-parse",
        )
    # Keep this transformation aligned with benchmark_ocr_cord._normalized_text:
    # NFC first, then Python's Unicode-aware split/join whitespace collapse.
    normalized = " ".join(unicodedata.normalize("NFC", value).split())
    if not normalized:
        raise BenchmarkError(
            f"SROIE transcription {index} is empty after normalization",
            stage="annotation-parse",
        )
    return normalized


def _bbox_with_repair(
    value: Any, index: int, width: int, height: int
) -> tuple[tuple[int, int, int, int], _BBoxRepair | None]:
    if (
        not isinstance(value, list)
        or len(value) != 4
        or any(type(coordinate) is not int for coordinate in value)
    ):
        raise BenchmarkError(
            f"SROIE bounding box {index} is not an exact four-integer box",
            stage="annotation-parse",
        )
    left, top, right, bottom = value
    if not (0 <= left <= right <= width and 0 <= top <= bottom <= height):
        raise BenchmarkError(
            f"SROIE bounding box {index} is degenerate or outside the image",
            stage="annotation-parse",
        )
    source_bbox = (left, top, right, bottom)
    zero_width = left == right
    zero_height = top == bottom
    if zero_width == zero_height:
        if zero_width:
            raise BenchmarkError(
                f"SROIE bounding box {index} is degenerate on both axes",
                stage="annotation-parse",
            )
        return source_bbox, None
    if zero_width:
        if right < width:
            right += 1
            direction = "increase-upper"
        else:
            left -= 1
            direction = "decrease-lower"
        axis = "x"
    else:
        if bottom < height:
            bottom += 1
            direction = "increase-upper"
        else:
            top -= 1
            direction = "decrease-lower"
        axis = "y"
    scoring_bbox = (left, top, right, bottom)
    if not (0 <= left < right <= width and 0 <= top < bottom <= height):
        raise BenchmarkError(
            f"SROIE bounding box {index} cannot be repaired in bounds",
            stage="annotation-parse",
        )
    return scoring_bbox, _BBoxRepair(
        source_bbox=source_bbox,
        scoring_bbox=scoring_bbox,
        degenerate_axis=axis,
        direction=direction,
    )


def _bbox(value: Any, index: int, width: int, height: int) -> tuple[int, int, int, int]:
    """Return scorer geometry after applying the frozen minimal repair policy."""

    return _bbox_with_repair(value, index, width, height)[0]


def parse_sroie_row(row_index: int, row: Mapping[str, Any]) -> ParsedSroieAnnotation:
    """Validate one selected-column row without reading key/entity fields."""

    if type(row_index) is not int or row_index < 0:
        raise BenchmarkError("A SROIE row index is invalid", stage="annotation-parse")
    document = _exact_mapping(row, {"image", "image_size", "words", "bboxes"}, "row")
    image = _exact_mapping(document["image"], {"bytes", "path"}, "image")
    raw_image = image["bytes"]
    source_image_path = image["path"]
    if not isinstance(raw_image, bytes) or not raw_image:
        raise BenchmarkError(
            "A SROIE row does not contain embedded image bytes",
            stage="annotation-parse",
        )
    if (
        not isinstance(source_image_path, str)
        or not source_image_path
        or source_image_path in {".", ".."}
        or not source_image_path.endswith(".jpg")
        or "/" in source_image_path
        or "\\" in source_image_path
        or ":" in source_image_path
        or any(unicodedata.category(character) == "Cc" for character in source_image_path)
    ):
        raise BenchmarkError(
            "A SROIE embedded image path is not a plain relative .jpg basename",
            stage="annotation-parse",
        )

    image_size = _exact_mapping(document["image_size"], {"width", "height"}, "image_size")
    width = _positive_integer(image_size["width"], "image width")
    height = _positive_integer(image_size["height"], "image height")
    try:
        embedded_width, embedded_height = _embedded_image_dimensions(raw_image)
        embedded_extension = _image_extension(raw_image)
    except BenchmarkError as exc:
        raise BenchmarkError(
            "A SROIE embedded image has an invalid or unsupported format",
            stage="annotation-parse",
        ) from exc
    if embedded_extension != ".jpg":
        raise BenchmarkError("A SROIE embedded image is not JPEG data", stage="annotation-parse")
    if (embedded_width, embedded_height) != (width, height):
        raise BenchmarkError(
            "SROIE image_size differs from the embedded image dimensions",
            stage="annotation-parse",
        )

    source_words = document["words"]
    source_bboxes = document["bboxes"]
    if not isinstance(source_words, list) or not isinstance(source_bboxes, list):
        raise BenchmarkError(
            "A SROIE words or bboxes value is not a list", stage="annotation-parse"
        )
    if not source_words or len(source_words) != len(source_bboxes):
        raise BenchmarkError(
            "A SROIE row has empty or misaligned words and bboxes",
            stage="annotation-parse",
        )

    source_payload = {
        "imageSize": {"width": width, "height": height},
        "words": source_words,
        "bboxes": source_bboxes,
    }
    try:
        source_payload_sha256 = sha256_bytes(canonical_json(source_payload).encode("utf-8"))
    except (TypeError, ValueError) as exc:
        raise BenchmarkError(
            "A SROIE source annotation payload is not canonical JSON",
            stage="annotation-parse",
        ) from exc

    normalized_regions: list[dict[str, Any]] = []
    pending_repairs: list[tuple[int, _BBoxRepair]] = []
    lines: list[CordLine] = []
    words: list[CordWord] = []
    rows: list[CordPhysicalRow] = []
    for index, (source_text, source_box) in enumerate(
        zip(source_words, source_bboxes, strict=True)
    ):
        text = _normalized_transcription(source_text, index)
        (left, top, right, bottom), repair = _bbox_with_repair(source_box, index, width, height)
        if repair is not None:
            pending_repairs.append((index, repair))
        polygon = convex_hull(((left, top), (right, top), (right, bottom), (left, bottom)))
        word = CordWord(
            line_index=index,
            word_index=0,
            row_id=index,
            text=text,
            polygon=polygon,
        )
        lines.append(CordLine(line_index=index, text=text, polygon=polygon))
        words.append(word)
        rows.append(CordPhysicalRow(row_id=index, text=text, polygon=polygon, words=(word,)))
        normalized_regions.append(
            {"sourceIndex": index, "text": text, "bbox": [left, top, right, bottom]}
        )

    annotation_document = {
        "schemaVersion": SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION,
        "imageSize": {"width": width, "height": height},
        "regions": normalized_regions,
        "normalization": "unicode-nfc-collapse-whitespace-v1",
        "bboxRepairPolicy": SROIE_BBOX_REPAIR_POLICY,
    }
    scoring_annotation_sha256 = sha256_bytes(canonical_json(annotation_document).encode("utf-8"))
    bbox_repairs = tuple(
        {
            "rowIndex": row_index,
            "sourceIndex": index,
            "imageWidth": width,
            "imageHeight": height,
            "sourceBbox": list(repair.source_bbox),
            "scoringBbox": list(repair.scoring_bbox),
            "degenerateAxis": repair.degenerate_axis,
            "direction": repair.direction,
            "axisExpansionPixels": 1,
            "sourcePayloadSha256": source_payload_sha256,
            "scoringAnnotationSha256": scoring_annotation_sha256,
        }
        for index, repair in pending_repairs
    )
    roi_polygon = (
        (0.0, 0.0),
        (float(width), 0.0),
        (float(width), float(height)),
        (0.0, float(height)),
    )
    return ParsedSroieAnnotation(
        document=annotation_document,
        raw_image=raw_image,
        source_image_path=source_image_path,
        width=width,
        height=height,
        lines=tuple(lines),
        words=tuple(words),
        rows=tuple(rows),
        roi_polygon=roi_polygon,
        source_payload_sha256=source_payload_sha256,
        scoring_annotation_sha256=scoring_annotation_sha256,
        bbox_repairs=bbox_repairs,
    )


def _expected_arrow_schema(pyarrow: Any) -> Any:
    element_string = pyarrow.field("element", pyarrow.string())
    element_box = pyarrow.field("element", pyarrow.list_(pyarrow.field("element", pyarrow.int64())))
    return pyarrow.schema(
        [
            pyarrow.field(
                "image",
                pyarrow.struct(
                    [
                        pyarrow.field("bytes", pyarrow.binary()),
                        pyarrow.field("path", pyarrow.string()),
                    ]
                ),
            ),
            pyarrow.field("key", pyarrow.string()),
            pyarrow.field(
                "image_size",
                pyarrow.struct(
                    [
                        pyarrow.field("width", pyarrow.int64()),
                        pyarrow.field("height", pyarrow.int64()),
                    ]
                ),
            ),
            pyarrow.field(
                "entities",
                pyarrow.struct(
                    [
                        pyarrow.field("company", pyarrow.string()),
                        pyarrow.field("date", pyarrow.string()),
                        pyarrow.field("address", pyarrow.string()),
                        pyarrow.field("total", pyarrow.string()),
                    ]
                ),
            ),
            pyarrow.field("words", pyarrow.list_(element_string)),
            pyarrow.field("bboxes", pyarrow.list_(element_box)),
        ]
    )


def _selection_sha256(documents: Sequence[CordDocument]) -> str:
    material = [
        {"rowIndex": document.row_index, "imageSha256": document.sha256} for document in documents
    ]
    return sha256_bytes(canonical_json(material).encode("utf-8"))


def _fresh_output_root(path: Path) -> Path:
    resolved = path.resolve()
    if path.is_symlink() or resolved.exists():
        raise BenchmarkError(
            "The SROIE extraction root must be fresh and non-existent",
            stage="corpus-extract",
        )
    return resolved


def _duplicate_selection(
    identities: Sequence[_RowIdentity], *, split: str
) -> tuple[set[int], dict[str, Any]]:
    groups: dict[str, list[_RowIdentity]] = {}
    for identity in identities:
        groups.setdefault(identity.image_sha256, []).append(identity)

    included: set[int] = set()
    duplicate_groups: list[dict[str, Any]] = []
    conflicting_groups = 0
    identical_groups = 0
    for image_sha256, members in groups.items():
        ordered = sorted(members, key=lambda item: item.row_index)
        if len(ordered) == 1:
            included.add(ordered[0].row_index)
            continue
        source_payload_sha256s = sorted({item.source_payload_sha256 for item in ordered})
        scoring_annotation_sha256s = sorted({item.scoring_annotation_sha256 for item in ordered})
        row_indices = [item.row_index for item in ordered]
        if split == "test":
            # The held-out population is fixed before its annotations are opened.
            # Duplicate/conflict information remains auditable, but it must never
            # decide which test rows count toward the result.
            decision = "score-all-test-rows"
            kept_rows = row_indices
            included.update(row_indices)
            if len(source_payload_sha256s) == 1:
                identical_groups += 1
            else:
                conflicting_groups += 1
        elif len(source_payload_sha256s) == 1:
            decision = "keep-lowest-row-index"
            kept_rows = [row_indices[0]]
            included.add(row_indices[0])
            identical_groups += 1
        else:
            decision = "exclude-conflicting-group"
            kept_rows = []
            conflicting_groups += 1
        duplicate_groups.append(
            {
                "imageSha256": image_sha256,
                "rowCount": len(row_indices),
                "rowIndices": row_indices,
                "distinctSourcePayloadDigests": len(source_payload_sha256s),
                "sourcePayloadSha256s": source_payload_sha256s,
                "distinctScoringAnnotationDigests": len(scoring_annotation_sha256s),
                "scoringAnnotationSha256s": scoring_annotation_sha256s,
                "decision": decision,
                "includedRowIndices": kept_rows,
                "excludedRowIndices": [index for index in row_indices if index not in kept_rows],
            }
        )

    excluded = sorted({item.row_index for item in identities} - included)
    source_images = [
        {"rowIndex": item.row_index, "imageSha256": item.image_sha256} for item in identities
    ]
    source_annotations = [
        {
            "rowIndex": item.row_index,
            "sourcePayloadSha256": item.source_payload_sha256,
            "scoringAnnotationSha256": item.scoring_annotation_sha256,
        }
        for item in identities
    ]
    audit = {
        "schemaVersion": SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION,
        "protocol": SROIE_PROTOCOL,
        "dataset": SROIE_REPOSITORY,
        "revision": SROIE_COMMIT,
        "split": split,
        "policy": SROIE_DUPLICATE_IMAGE_POLICY,
        "sourceRows": len(identities),
        "includedRows": len(included),
        "excludedRows": len(excluded),
        "duplicateGroups": len(duplicate_groups),
        "identicalAnnotationDuplicateGroups": identical_groups,
        "conflictingAnnotationDuplicateGroups": conflicting_groups,
        "sourceImageDigests": source_images,
        "sourceAnnotationDigests": source_annotations,
        "sourceImageDigestSetSha256": sha256_bytes(
            canonical_json(sorted({item.image_sha256 for item in identities})).encode("utf-8")
        ),
        "duplicateGroupAudit": duplicate_groups,
        "excludedRowIndices": excluded,
    }
    return included, audit


def extract_sroie_corpus(
    parquet_path: Path,
    output_root: Path,
    *,
    split: str = "train",
) -> ExtractedSroieCorpus:
    """Verify and extract a complete pinned SROIE split for CORD scoring."""

    config = _split_config(split)
    verified_sha256 = verify_sroie_parquet(parquet_path, split)
    verified_snapshot = _regular_file_snapshot(parquet_path)
    try:
        import pyarrow
        import pyarrow.parquet as parquet
    except ImportError as exc:
        raise BenchmarkError(
            "pyarrow is required to read the pinned SROIE parquet",
            stage="dependency-check",
        ) from exc
    if pyarrow.__version__ != "25.0.0":
        raise BenchmarkError(
            "The SROIE adapter requires the pinned pyarrow 25.0.0 runtime",
            stage="dependency-check",
        )

    try:
        source = parquet.ParquetFile(parquet_path)
    except Exception as exc:
        raise BenchmarkError(
            "The pinned SROIE parquet could not be opened", stage="corpus-read"
        ) from exc
    if source.metadata.num_rows != config["rows"]:
        raise BenchmarkError(
            f"The pinned SROIE parquet does not contain exactly {config['rows']} {split} rows",
            stage="corpus-read",
        )
    if source.metadata.num_row_groups != config["rowGroups"]:
        raise BenchmarkError(
            f"The pinned SROIE parquet does not contain exactly {config['rowGroups']} "
            f"{split} row groups",
            stage="corpus-read",
        )
    if not source.schema_arrow.equals(_expected_arrow_schema(pyarrow), check_metadata=False):
        raise BenchmarkError(
            "The pinned SROIE parquet does not use the exact expected schema",
            stage="corpus-read",
        )

    output_root = _fresh_output_root(output_root)
    identities: list[_RowIdentity] = []
    first_pass_repairs: list[dict[str, Any]] = []
    try:
        batches = source.iter_batches(
            batch_size=8, columns=["image", "image_size", "words", "bboxes"]
        )
        source_rows = (row for batch in batches for row in batch.to_pylist())
        for row_index, row in enumerate(source_rows):
            if row_index >= config["rows"]:
                raise BenchmarkError("The SROIE reader returned extra rows", stage="corpus-read")
            parsed = parse_sroie_row(row_index, row)
            repair_records_sha256 = sha256_bytes(
                canonical_json(list(parsed.bbox_repairs)).encode("utf-8")
            )
            identities.append(
                _RowIdentity(
                    row_index=row_index,
                    image_sha256=sha256_bytes(parsed.raw_image),
                    source_payload_sha256=parsed.source_payload_sha256,
                    scoring_annotation_sha256=parsed.scoring_annotation_sha256,
                    source_region_count=len(parsed.words),
                    repaired_region_count=len(parsed.bbox_repairs),
                    repair_records_sha256=repair_records_sha256,
                )
            )
            first_pass_repairs.extend(parsed.bbox_repairs)
    except BenchmarkError:
        raise
    except Exception as exc:
        raise BenchmarkError(
            "The pinned SROIE parquet could not be read", stage="corpus-read"
        ) from exc
    if len(identities) != config["rows"]:
        raise BenchmarkError(
            f"The SROIE reader did not return all {config['rows']} {split} rows",
            stage="corpus-read",
        )

    included_row_indices, duplicate_audit_document = _duplicate_selection(identities, split=split)
    if not included_row_indices:
        raise BenchmarkError(
            "The SROIE duplicate policy excluded every source row",
            stage="corpus-read",
        )
    duplicate_group_by_row = {
        row_index: group
        for group in duplicate_audit_document["duplicateGroupAudit"]
        for row_index in group["rowIndices"]
    }

    documents: list[CordDocument] = []
    corpus_rows: list[dict[str, Any]] = []
    worker_rows: list[dict[str, Any]] = []
    inventory_rows: list[str] = []
    second_pass_repairs: list[dict[str, Any]] = []
    second_pass_rows = 0
    try:
        batches = source.iter_batches(
            batch_size=8, columns=["image", "image_size", "words", "bboxes"]
        )
        source_rows = (row for batch in batches for row in batch.to_pylist())
        for row_index, row in enumerate(source_rows):
            second_pass_rows = row_index + 1
            if row_index >= config["rows"]:
                raise BenchmarkError("The SROIE reader returned extra rows", stage="corpus-read")
            parsed = parse_sroie_row(row_index, row)
            image_sha256 = sha256_bytes(parsed.raw_image)
            annotation_sha256 = parsed.scoring_annotation_sha256
            repair_records_sha256 = sha256_bytes(
                canonical_json(list(parsed.bbox_repairs)).encode("utf-8")
            )
            second_pass_repairs.extend(parsed.bbox_repairs)
            if identities[row_index] != _RowIdentity(
                row_index=row_index,
                image_sha256=image_sha256,
                source_payload_sha256=parsed.source_payload_sha256,
                scoring_annotation_sha256=annotation_sha256,
                source_region_count=len(parsed.words),
                repaired_region_count=len(parsed.bbox_repairs),
                repair_records_sha256=repair_records_sha256,
            ):
                raise BenchmarkError(
                    "A SROIE row identity changed between selection and extraction",
                    stage="corpus-read",
                )
            if row_index not in included_row_indices:
                continue

            extension = ".jpg"
            relative_path = f"images/row-{row_index:04d}-{image_sha256[:12]}{extension}"
            image_path = (output_root / relative_path).resolve()
            _atomic_write(image_path, parsed.raw_image)

            document = CordDocument(
                row_index=row_index,
                image_id=row_index,
                relative_path=relative_path,
                path=image_path,
                length=len(parsed.raw_image),
                sha256=image_sha256,
                annotation_sha256=annotation_sha256,
                lines=parsed.lines,
                dontcare_polygons=(),
                repeating_symbol_polygons=(),
                clipped_valid_lines=0,
                clipped_dontcare_regions=0,
                clipped_repeating_symbol_regions=0,
                words=parsed.words,
                rows=parsed.rows,
                roi_polygon=parsed.roi_polygon,
            )
            documents.append(document)
            duplicate_group = duplicate_group_by_row.get(row_index)
            corpus_rows.append(
                {
                    "schemaVersion": SROIE_SCORING_CORPUS_MANIFEST_SCHEMA_VERSION,
                    "protocol": SROIE_PROTOCOL,
                    "dataset": SROIE_REPOSITORY,
                    "revision": SROIE_COMMIT,
                    "split": split,
                    "rowIndex": row_index,
                    "imageId": row_index,
                    "path": relative_path,
                    "length": len(parsed.raw_image),
                    "sha256": image_sha256,
                    "annotationSha256": annotation_sha256,
                    "sourcePayloadSha256": parsed.source_payload_sha256,
                    "scoringAnnotationSha256": annotation_sha256,
                    "imageWidth": parsed.width,
                    "imageHeight": parsed.height,
                    "groundTruthLines": len(parsed.lines),
                    "groundTruthWords": len(parsed.words),
                    "groundTruthPhysicalRows": len(parsed.rows),
                    "dontcareRegions": 0,
                    "repeatingSymbolRegions": 0,
                    "clippedValidLines": 0,
                    "clippedDontcareRegions": 0,
                    "clippedRepeatingSymbolRegions": 0,
                    "annotationBoundaryClips": [],
                    "bboxRepairPolicy": SROIE_BBOX_REPAIR_POLICY,
                    "bboxRepairCount": len(parsed.bbox_repairs),
                    "bboxRepairRecordsSha256": repair_records_sha256,
                    "duplicateImagePolicy": SROIE_DUPLICATE_IMAGE_POLICY,
                    "duplicateGroupSize": (duplicate_group["rowCount"] if duplicate_group else 1),
                    "duplicateSelection": (
                        duplicate_group["decision"] if duplicate_group else "singleton"
                    ),
                }
            )
            worker_rows.append(
                {
                    "schemaVersion": OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION,
                    "path": str(image_path),
                    "length": len(parsed.raw_image),
                    "sha256": image_sha256,
                }
            )
            inventory_rows.append(str(image_path))
    except BenchmarkError:
        raise
    except Exception as exc:
        raise BenchmarkError(
            "The pinned SROIE parquet could not be read", stage="corpus-read"
        ) from exc

    if second_pass_rows != config["rows"]:
        raise BenchmarkError(
            "The SROIE second extraction pass did not return every source row",
            stage="corpus-read",
        )
    if second_pass_repairs != first_pass_repairs:
        raise BenchmarkError(
            "The SROIE repair audit changed between selection and extraction",
            stage="corpus-read",
        )
    if len(documents) != len(included_row_indices):
        raise BenchmarkError(
            "The SROIE extraction did not produce the duplicate-selected row count",
            stage="corpus-read",
        )
    if verified_snapshot != _regular_file_snapshot(parquet_path):
        raise BenchmarkError(
            "The pinned SROIE parquet changed while it was being read",
            stage="corpus-read",
        )
    try:
        final_sha256 = sha256_file(parquet_path)
    except OSError as exc:
        raise BenchmarkError(
            "The pinned SROIE parquet could not be reverified after extraction",
            stage="corpus-read",
        ) from exc
    if final_sha256 != verified_sha256:
        raise BenchmarkError(
            "The pinned SROIE parquet content changed while it was being read",
            stage="corpus-read",
        )

    corpus_manifest = output_root / f"sroie-{split}-manifest.jsonl"
    worker_manifest = output_root / f"sroie-{split}-worker-input-manifest.jsonl"
    inventory = output_root / f"sroie-{split}-inventory.txt"
    duplicate_audit = output_root / f"sroie-{split}-duplicate-selection-audit.json"
    bbox_repair_audit = output_root / f"sroie-{split}-bbox-repair-audit.json"
    selection_sha256 = _selection_sha256(documents)
    duplicate_audit_document["selectionSha256"] = selection_sha256
    bbox_repair_records_sha256 = sha256_bytes(canonical_json(second_pass_repairs).encode("utf-8"))
    source_payload_identities = [
        {"rowIndex": item.row_index, "sourcePayloadSha256": item.source_payload_sha256}
        for item in identities
    ]
    scoring_annotation_identities = [
        {
            "rowIndex": item.row_index,
            "scoringAnnotationSha256": item.scoring_annotation_sha256,
        }
        for item in identities
    ]
    source_payload_identities_sha256 = sha256_bytes(
        canonical_json(source_payload_identities).encode("utf-8")
    )
    scoring_annotation_identities_sha256 = sha256_bytes(
        canonical_json(scoring_annotation_identities).encode("utf-8")
    )
    source_region_count = sum(item.source_region_count for item in identities)
    repaired_region_count = len(second_pass_repairs)
    bbox_repair_audit_document = {
        "schemaVersion": SROIE_BBOX_REPAIR_AUDIT_SCHEMA_VERSION,
        "protocol": SROIE_PROTOCOL,
        "dataset": SROIE_REPOSITORY,
        "revision": SROIE_COMMIT,
        "split": split,
        "policy": SROIE_BBOX_REPAIR_POLICY,
        "sourceRows": len(identities),
        "sourceRegions": source_region_count,
        "repairedRegions": repaired_region_count,
        "unchangedRegions": source_region_count - repaired_region_count,
        "repairRecordsSha256": bbox_repair_records_sha256,
        "sourcePayloadIdentitiesSha256": source_payload_identities_sha256,
        "scoringAnnotationIdentitiesSha256": scoring_annotation_identities_sha256,
        "rowIdentities": [
            {
                "rowIndex": item.row_index,
                "sourcePayloadSha256": item.source_payload_sha256,
                "scoringAnnotationSha256": item.scoring_annotation_sha256,
                "sourceRegions": item.source_region_count,
                "repairedRegions": item.repaired_region_count,
                "repairRecordsSha256": item.repair_records_sha256,
            }
            for item in identities
        ],
        "repairs": second_pass_repairs,
    }
    _atomic_write(
        corpus_manifest,
        "".join(canonical_json(row) + "\n" for row in corpus_rows).encode("utf-8"),
    )
    _atomic_write(
        worker_manifest,
        "".join(canonical_json(row) + "\n" for row in worker_rows).encode("utf-8"),
    )
    _atomic_write(
        inventory,
        "".join(f"{row}\n" for row in inventory_rows).encode("utf-8"),
    )
    _atomic_write(
        duplicate_audit,
        (canonical_json(duplicate_audit_document) + "\n").encode("utf-8"),
    )
    _atomic_write(
        bbox_repair_audit,
        (canonical_json(bbox_repair_audit_document) + "\n").encode("utf-8"),
    )
    return ExtractedSroieCorpus(
        documents=tuple(documents),
        corpus_manifest=corpus_manifest,
        worker_manifest=worker_manifest,
        inventory=inventory,
        corpus_manifest_sha256=sha256_file(corpus_manifest),
        worker_manifest_sha256=sha256_file(worker_manifest),
        selection_sha256=selection_sha256,
        split=split,
        duplicate_audit=duplicate_audit,
        duplicate_audit_sha256=sha256_file(duplicate_audit),
        bbox_repair_audit=bbox_repair_audit,
        bbox_repair_audit_sha256=sha256_file(bbox_repair_audit),
        bbox_repair_records_sha256=bbox_repair_records_sha256,
        source_payload_identities_sha256=source_payload_identities_sha256,
        scoring_annotation_identities_sha256=scoring_annotation_identities_sha256,
        source_region_count=source_region_count,
        repaired_region_count=repaired_region_count,
        source_image_sha256s=tuple(item.image_sha256 for item in identities),
        source_row_count=len(identities),
        excluded_row_indices=tuple(duplicate_audit_document["excludedRowIndices"]),
    )
