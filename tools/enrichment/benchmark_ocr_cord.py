#!/usr/bin/env python3
"""Benchmark the offline OCR worker on a pinned CORD v2 validation or test split.

This harness intentionally accepts only a complete, immutable 100-row CORD v2
parquet. It extracts every embedded image, writes deterministic corpus
and evidence-worker manifests, and scores polygon detection and case-sensitive
Unicode-NFC recognition without changing the evidence worker.
"""

from __future__ import annotations

import argparse
import functools
import importlib.metadata
import json
import math
import os
import statistics
import struct
import subprocess
import sys
import time
import unicodedata
from collections import Counter
from collections.abc import Iterable, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any, TypeAlias

from benchmark_ocr import (
    Backend,
    BenchmarkError,
    _load_model_identity,
    _read_jsonl,
    begin_benchmark_report,
    canonical_evidence_sha256,
    canonical_json,
    isolated_python_command,
    parse_backend,
    publish_benchmark_report,
    raw_output_pair_sha256,
    runtime_metadata,
    safe_host_metadata,
    sanitized_runtime_environment,
    sha256_bytes,
    sha256_file,
    utc_timestamp,
    validate_release_matrix,
)

SCHEMA_VERSION = 5
PROTOCOL = "bstrings-derived-CORD-v2-rowid-constrained-segmentation-tolerant-OCR-v5"
# The integrated OCR executable owns this separate wire contract. Scorer/report
# revisions must not leak into the worker's schema-version-1 input manifest.
OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION = 1
CORD_V2_COMMIT = "7f0115a4b758a71d6473b8d085751692da2fef98"
CORD_V2_ROWS = 100
CORD_V2_TEST_BYTES = 234_202_795
CORD_V2_TEST_SHA256 = "51c65f1788faff392abe2a0b55b023eb23e9be551c509138eaa3a832514224e7"
CORD_V2_VALIDATION_BYTES = 242_080_800
CORD_V2_VALIDATION_SHA256 = "0d0f6dac11fdcc549de2746aa9f53136a3bc22a2a1aff2b0b847f7622ad60c15"
CORD_V2_FILES = {
    "test": {
        "filename": "test-00000-of-00001-9c204eb3f4e11791.parquet",
        "bytes": CORD_V2_TEST_BYTES,
        "sha256": CORD_V2_TEST_SHA256,
    },
    "validation": {
        "filename": "validation-00000-of-00001-cc3c5779fe22e8ca.parquet",
        "bytes": CORD_V2_VALIDATION_BYTES,
        "sha256": CORD_V2_VALIDATION_SHA256,
    },
}
CORD_V2_METADATA_SPLITS = {"test": "test", "validation": "valid"}
for _split_config in CORD_V2_FILES.values():
    _split_config["url"] = (
        "https://huggingface.co/datasets/naver-clova-ix/cord-v2/resolve/"
        f"{CORD_V2_COMMIT}/data/{_split_config['filename']}"
    )
CORD_V2_TEST_URL = str(CORD_V2_FILES["test"]["url"])
CORD_V2_TEST_ROWS = CORD_V2_ROWS
DETERMINISM_ROW_INDICES = tuple(range(10))
MATCH_IOU_THRESHOLD = 0.5
IGNORE_PRECISION_THRESHOLD = 0.5
ROW_CLUSTER_NORMALIZED_DISTANCE = 0.5
REFERENCE_ROW_SPLIT_NORMALIZED_DISTANCE = 0.3
REFERENCE_ROW_MERGE_NORMALIZED_DISTANCE = 0.75
SEGMENTATION_OVERLAP_THRESHOLD = 0.5
CONFIDENCE_PARITY_MAX_ABS_DELTA = 1e-4
SHAPELY_VERSION = "2.1.2"
# CORD maintainers document that annotation coordinates can cross an image edge
# and recommend projecting them back to the raster. These conjunctive,
# calibration-derived guards admit only small edge truncations; the raw quad is
# retained in the pinned source annotation while scoring uses its exact
# intersection with the verified image rectangle.
ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_PIXELS = 24.0
ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_RATIO = 0.05
ANNOTATION_BOUNDARY_MINIMUM_RETAINED_AREA_RATIO = 0.75

Point: TypeAlias = tuple[float, float]
Polygon: TypeAlias = tuple[Point, ...]


@dataclass(frozen=True)
class CordLine:
    line_index: int
    text: str
    polygon: Polygon


@dataclass(frozen=True)
class CordWord:
    line_index: int
    word_index: int
    row_id: int
    text: str
    polygon: Polygon


@dataclass(frozen=True)
class CordPhysicalRow:
    row_id: int
    text: str
    polygon: Polygon
    words: tuple[CordWord, ...]


@dataclass(frozen=True)
class ParsedCordAnnotation:
    document: dict[str, Any]
    image_id: int
    width: int
    height: int
    lines: tuple[CordLine, ...]
    words: tuple[CordWord, ...]
    rows: tuple[CordPhysicalRow, ...]
    roi_polygon: Polygon
    dontcare_polygons: tuple[Polygon, ...]
    repeating_symbol_polygons: tuple[Polygon, ...]
    clipped_valid_lines: int
    clipped_dontcare_regions: int
    clipped_repeating_symbol_regions: int
    annotation_boundary_clips: tuple[AnnotationBoundaryClip, ...] = ()

    @property
    def ignored_polygons(self) -> tuple[Polygon, ...]:
        return self.dontcare_polygons + self.repeating_symbol_polygons


@dataclass(frozen=True)
class CordDocument:
    row_index: int
    image_id: int
    relative_path: str
    path: Path
    length: int
    sha256: str
    annotation_sha256: str
    lines: tuple[CordLine, ...]
    dontcare_polygons: tuple[Polygon, ...]
    repeating_symbol_polygons: tuple[Polygon, ...]
    clipped_valid_lines: int
    clipped_dontcare_regions: int
    clipped_repeating_symbol_regions: int
    words: tuple[CordWord, ...] = ()
    rows: tuple[CordPhysicalRow, ...] = ()
    roi_polygon: Polygon = ()
    annotation_boundary_clips: tuple[AnnotationBoundaryClip, ...] = ()

    @property
    def ignored_polygons(self) -> tuple[Polygon, ...]:
        return self.dontcare_polygons + self.repeating_symbol_polygons


@dataclass(frozen=True)
class ExtractedCorpus:
    documents: tuple[CordDocument, ...]
    corpus_manifest: Path
    worker_manifest: Path
    inventory: Path
    corpus_manifest_sha256: str
    worker_manifest_sha256: str
    selection_sha256: str
    split: str = "test"


@dataclass(frozen=True)
class Prediction:
    text: str
    polygon: Polygon
    confidence: float
    record_id: str


@dataclass(frozen=True)
class PredictionPhysicalRow:
    text: str
    polygon: Polygon
    confidence: float
    record_id: str
    members: tuple[Prediction, ...]


@dataclass(frozen=True)
class PolygonMatch:
    truth_index: int
    prediction_index: int
    iou: float


@dataclass(frozen=True)
class AnnotationBoundaryClip:
    locator: str
    clipped_area: float
    horizontal_overshoot_pixels: float
    horizontal_overshoot_ratio: float
    original_area: float
    outside_vertices: int
    retained_area_ratio: float
    sides: tuple[str, ...]
    vertical_overshoot_pixels: float
    vertical_overshoot_ratio: float


class AnnotationBoundaryError(BenchmarkError):
    """A safely reportable annotation-edge guard failure."""

    def __init__(
        self,
        message: str,
        *,
        locator: str,
        predicate: str,
        horizontal_overshoot_pixels: float,
        horizontal_overshoot_ratio: float,
        vertical_overshoot_pixels: float,
        vertical_overshoot_ratio: float,
        original_area: float,
        clipped_area: float | None,
        retained_area_ratio: float | None,
        outside_vertices: int,
        sides: tuple[str, ...],
    ) -> None:
        super().__init__(message, stage="annotation-parse")
        self.diagnostic = {
            "kind": "annotationBoundary",
            "locator": locator,
            "predicate": predicate,
            "horizontalOvershootPixels": horizontal_overshoot_pixels,
            "horizontalOvershootRatio": horizontal_overshoot_ratio,
            "verticalOvershootPixels": vertical_overshoot_pixels,
            "verticalOvershootRatio": vertical_overshoot_ratio,
            "originalArea": original_area,
            "clippedArea": clipped_area,
            "retainedAreaRatio": retained_area_ratio,
            "outsideVertices": outside_vertices,
            "sides": list(sides),
        }


@dataclass(frozen=True)
class SequenceCounts:
    reference_units: int
    predicted_units: int
    matches: int
    edits: int


def _as_mapping(value: Any, label: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise BenchmarkError(f"CORD {label} is not an object", stage="annotation-parse")
    return value


def _distribution_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "not-installed"


@functools.lru_cache(maxsize=1)
def _shapely_runtime() -> tuple[Any, Any, str, str]:
    version = _distribution_version("shapely")
    if version != SHAPELY_VERSION:
        raise BenchmarkError(
            f"Shapely {SHAPELY_VERSION} is required; resolved {version}",
            stage="metrics-runtime",
        )
    try:
        import shapely
        from shapely.geometry import Polygon as ShapelyPolygon
        from shapely.ops import unary_union
    except ImportError as exc:
        raise BenchmarkError(
            f"Shapely {SHAPELY_VERSION} is required for exact polygon-union coverage",
            stage="metrics-runtime",
        ) from exc
    return ShapelyPolygon, unary_union, version, str(shapely.geos_version_string)


def _as_list(value: Any, label: str) -> list[Any]:
    if not isinstance(value, list):
        raise BenchmarkError(f"CORD {label} is not an array", stage="annotation-parse")
    return value


def _coordinate(value: Any, label: str) -> float:
    if isinstance(value, bool):
        raise BenchmarkError(f"CORD {label} is not numeric", stage="annotation-parse")
    try:
        numeric = float(value)
    except (TypeError, ValueError) as exc:
        raise BenchmarkError(f"CORD {label} is not numeric", stage="annotation-parse") from exc
    if not math.isfinite(numeric):
        raise BenchmarkError(f"CORD {label} is not finite", stage="annotation-parse")
    return numeric


def _cross(origin: Point, left: Point, right: Point) -> float:
    return (left[0] - origin[0]) * (right[1] - origin[1]) - (left[1] - origin[1]) * (
        right[0] - origin[0]
    )


def convex_hull(points: Iterable[Point]) -> Polygon:
    unique = sorted(set(points))
    if len(unique) < 3:
        raise BenchmarkError("A CORD polygon has fewer than three points", stage="geometry")
    lower: list[Point] = []
    for point in unique:
        while len(lower) >= 2 and _cross(lower[-2], lower[-1], point) <= 0:
            lower.pop()
        lower.append(point)
    upper: list[Point] = []
    for point in reversed(unique):
        while len(upper) >= 2 and _cross(upper[-2], upper[-1], point) <= 0:
            upper.pop()
        upper.append(point)
    hull = tuple(lower[:-1] + upper[:-1])
    if len(hull) < 3 or polygon_area(hull) <= 0:
        raise BenchmarkError("A CORD polygon is degenerate", stage="geometry")
    return hull


def polygon_area(polygon: Sequence[Point]) -> float:
    if len(polygon) < 3:
        return 0.0
    return (
        abs(
            sum(
                polygon[index][0] * polygon[(index + 1) % len(polygon)][1]
                - polygon[(index + 1) % len(polygon)][0] * polygon[index][1]
                for index in range(len(polygon))
            )
        )
        / 2.0
    )


def _line_intersection(start: Point, end: Point, clip_start: Point, clip_end: Point) -> Point:
    segment = (end[0] - start[0], end[1] - start[1])
    clip = (clip_end[0] - clip_start[0], clip_end[1] - clip_start[1])
    denominator = segment[0] * clip[1] - segment[1] * clip[0]
    if abs(denominator) < 1e-12:
        return end
    offset = (clip_start[0] - start[0], clip_start[1] - start[1])
    scale = (offset[0] * clip[1] - offset[1] * clip[0]) / denominator
    return start[0] + scale * segment[0], start[1] + scale * segment[1]


def polygon_intersection(subject: Polygon, clip: Polygon) -> Polygon:
    output = list(convex_hull(subject))
    clip_hull = convex_hull(clip)
    for index, clip_start in enumerate(clip_hull):
        clip_end = clip_hull[(index + 1) % len(clip_hull)]
        source = output
        output = []
        if not source:
            break
        start = source[-1]
        for end in source:
            end_inside = _cross(clip_start, clip_end, end) >= -1e-9
            start_inside = _cross(clip_start, clip_end, start) >= -1e-9
            if end_inside:
                if not start_inside:
                    output.append(_line_intersection(start, end, clip_start, clip_end))
                output.append(end)
            elif start_inside:
                output.append(_line_intersection(start, end, clip_start, clip_end))
            start = end
    if len(output) < 3:
        return ()
    try:
        return convex_hull(output)
    except BenchmarkError:
        return ()


def polygon_iou(left: Polygon, right: Polygon) -> float:
    left_area = polygon_area(left)
    right_area = polygon_area(right)
    if left_area <= 0 or right_area <= 0:
        return 0.0
    overlap = polygon_area(polygon_intersection(left, right))
    denominator = left_area + right_area - overlap
    return overlap / denominator if denominator > 0 else 0.0


def _quad(value: Any) -> Polygon:
    quad = _as_mapping(value, "quad")
    required = frozenset(f"{axis}{index}" for index in range(1, 5) for axis in ("x", "y"))
    if frozenset(quad) != required:
        raise BenchmarkError(
            "A CORD quad does not use the exact v2 schema", stage="annotation-parse"
        )
    if any(type(quad[name]) is not int for name in required):
        raise BenchmarkError("A CORD quad coordinate is not an integer", stage="annotation-parse")
    points = tuple(
        (
            _coordinate(quad[f"x{index}"], f"quad.x{index}"),
            _coordinate(quad[f"y{index}"], f"quad.y{index}"),
        )
        for index in range(1, 5)
    )
    hull = convex_hull(points)
    if len(hull) != 4:
        raise BenchmarkError(
            "A CORD quad does not have four distinct hull vertices",
            stage="annotation-parse",
        )
    return hull


def _dontcare_polygon_group(value: Any) -> tuple[Polygon, ...]:
    group = _as_list(value, "dontcare polygon group")
    if not group:
        raise BenchmarkError(
            "A CORD dontcare polygon group is empty",
            stage="annotation-parse",
        )
    return tuple(_quad(item) for item in group)


def _repeating_symbol_polygon_group(value: Any) -> tuple[Polygon, ...]:
    group = _as_list(value, "repeating_symbol polygon group")
    if not group:
        raise BenchmarkError(
            "A CORD repeating_symbol polygon group is empty",
            stage="annotation-parse",
        )
    polygons = []
    for raw_item in group:
        item = _as_mapping(raw_item, "repeating_symbol entry")
        if frozenset(item) != frozenset({"quad", "text"}):
            raise BenchmarkError(
                "A CORD repeating_symbol entry does not use the exact v2 schema",
                stage="annotation-parse",
            )
        text = item["text"]
        # CORD v2 contains valid cut-line regions whose label text is empty.
        # Scoring uses the validated polygon, not this descriptive string.
        if not isinstance(text, str) or "\x00" in text:
            raise BenchmarkError(
                "A CORD repeating_symbol has invalid text", stage="annotation-parse"
            )
        polygons.append(_quad(item["quad"]))
    return tuple(polygons)


def _clip_annotation_polygon(
    polygon: Polygon,
    width: int,
    height: int,
    *,
    locator: str,
) -> tuple[Polygon, AnnotationBoundaryClip | None]:
    if all(0 <= x <= width and 0 <= y <= height for x, y in polygon):
        return polygon, None
    left = max((max(0.0, -x) for x, _ in polygon), default=0.0)
    right = max((max(0.0, x - width) for x, _ in polygon), default=0.0)
    top = max((max(0.0, -y) for _, y in polygon), default=0.0)
    bottom = max((max(0.0, y - height) for _, y in polygon), default=0.0)
    horizontal_overshoot = max(left, right)
    vertical_overshoot = max(top, bottom)
    horizontal_ratio = horizontal_overshoot / width
    vertical_ratio = vertical_overshoot / height
    outside_vertices = sum(not (0 <= x <= width and 0 <= y <= height) for x, y in polygon)
    sides = tuple(
        name
        for name, overshoot in (
            ("left", left),
            ("right", right),
            ("top", top),
            ("bottom", bottom),
        )
        if overshoot > 0
    )
    original_area = polygon_area(polygon)
    if (
        horizontal_overshoot > ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_PIXELS
        or vertical_overshoot > ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_PIXELS
        or horizontal_ratio > ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_RATIO
        or vertical_ratio > ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_RATIO
    ):
        raise AnnotationBoundaryError(
            "A CORD annotation polygon exceeds the bounded edge overshoot "
            f"({locator}: horizontal={horizontal_overshoot:.12g}px/"
            f"{horizontal_ratio:.12g}, vertical={vertical_overshoot:.12g}px/"
            f"{vertical_ratio:.12g})",
            locator=locator,
            predicate="edgeOvershoot",
            horizontal_overshoot_pixels=horizontal_overshoot,
            horizontal_overshoot_ratio=horizontal_ratio,
            vertical_overshoot_pixels=vertical_overshoot,
            vertical_overshoot_ratio=vertical_ratio,
            original_area=original_area,
            clipped_area=None,
            retained_area_ratio=None,
            outside_vertices=outside_vertices,
            sides=sides,
        )
    image_bounds: Polygon = (
        (0.0, 0.0),
        (float(width), 0.0),
        (float(width), float(height)),
        (0.0, float(height)),
    )
    clipped = polygon_intersection(polygon, image_bounds)
    if not clipped or polygon_area(clipped) <= 0:
        raise AnnotationBoundaryError(
            "A CORD annotation polygon does not intersect the declared image",
            locator=locator,
            predicate="emptyIntersection",
            horizontal_overshoot_pixels=horizontal_overshoot,
            horizontal_overshoot_ratio=horizontal_ratio,
            vertical_overshoot_pixels=vertical_overshoot,
            vertical_overshoot_ratio=vertical_ratio,
            original_area=original_area,
            clipped_area=0.0,
            retained_area_ratio=0.0,
            outside_vertices=outside_vertices,
            sides=sides,
        )
    clipped_area = polygon_area(clipped)
    retained_area_ratio = clipped_area / original_area
    if retained_area_ratio < ANNOTATION_BOUNDARY_MINIMUM_RETAINED_AREA_RATIO:
        raise AnnotationBoundaryError(
            "A CORD annotation polygon retains too little area after edge clipping "
            f"({locator}: retained={retained_area_ratio:.12g})",
            locator=locator,
            predicate="retainedArea",
            horizontal_overshoot_pixels=horizontal_overshoot,
            horizontal_overshoot_ratio=horizontal_ratio,
            vertical_overshoot_pixels=vertical_overshoot,
            vertical_overshoot_ratio=vertical_ratio,
            original_area=original_area,
            clipped_area=clipped_area,
            retained_area_ratio=retained_area_ratio,
            outside_vertices=outside_vertices,
            sides=sides,
        )
    return clipped, AnnotationBoundaryClip(
        locator=locator,
        clipped_area=clipped_area,
        horizontal_overshoot_pixels=horizontal_overshoot,
        horizontal_overshoot_ratio=horizontal_ratio,
        original_area=original_area,
        outside_vertices=outside_vertices,
        retained_area_ratio=retained_area_ratio,
        sides=sides,
        vertical_overshoot_pixels=vertical_overshoot,
        vertical_overshoot_ratio=vertical_ratio,
    )


def _annotation_boundary_clip_record(clip: AnnotationBoundaryClip) -> dict[str, Any]:
    return {
        "locator": clip.locator,
        "clippedArea": clip.clipped_area,
        "horizontalOvershootPixels": clip.horizontal_overshoot_pixels,
        "horizontalOvershootRatio": clip.horizontal_overshoot_ratio,
        "originalArea": clip.original_area,
        "outsideVertices": clip.outside_vertices,
        "retainedAreaRatio": clip.retained_area_ratio,
        "sides": list(clip.sides),
        "verticalOvershootPixels": clip.vertical_overshoot_pixels,
        "verticalOvershootRatio": clip.vertical_overshoot_ratio,
    }


def _reject_duplicate_json_members(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise BenchmarkError(
                "A CORD ground_truth value contains a duplicate JSON field",
                stage="annotation-parse",
            )
        result[key] = value
    return result


def _normalized_text(value: str) -> str:
    return " ".join(unicodedata.normalize("NFC", value).split())


def _image_bounds(width: int, height: int) -> Polygon:
    return (
        (0.0, 0.0),
        (float(width), 0.0),
        (float(width), float(height)),
        (0.0, float(height)),
    )


def parse_cord_annotation(
    row_index: int,
    value: str | dict[str, Any],
    *,
    expected_split: str = "test",
) -> ParsedCordAnnotation:
    if expected_split not in CORD_V2_FILES:
        raise BenchmarkError("The requested CORD split is unsupported", stage="annotation-parse")
    if isinstance(value, str):
        try:
            document = json.loads(value, object_pairs_hook=_reject_duplicate_json_members)
        except BenchmarkError:
            raise
        except json.JSONDecodeError as exc:
            raise BenchmarkError(
                "A CORD ground_truth value is not valid JSON", stage="annotation-parse"
            ) from exc
    else:
        document = value
    annotation = _as_mapping(document, "ground_truth")
    expected_top_level = frozenset(
        {"dontcare", "gt_parse", "meta", "repeating_symbol", "roi", "valid_line"}
    )
    if frozenset(annotation) != expected_top_level:
        raise BenchmarkError(
            "A CORD ground_truth value does not use the exact v2 top-level schema",
            stage="annotation-parse",
        )
    meta = _as_mapping(annotation.get("meta"), "meta")
    if frozenset(meta) != frozenset({"version", "split", "image_id", "image_size"}):
        raise BenchmarkError(
            "A CORD meta value does not use the exact v2 schema", stage="annotation-parse"
        )
    if meta.get("version") != "2.0.0":
        raise BenchmarkError("A CORD row is not version 2.0.0", stage="annotation-parse")
    metadata_split = CORD_V2_METADATA_SPLITS[expected_split]
    if meta.get("split") != metadata_split:
        raise BenchmarkError(
            f"A CORD row is not from the {expected_split} split", stage="annotation-parse"
        )
    image_id = meta.get("image_id")
    if type(image_id) is not int or image_id < 0:
        raise BenchmarkError("A CORD row has an invalid image_id", stage="annotation-parse")
    image_size = _as_mapping(meta.get("image_size"), "image_size")
    if frozenset(image_size) != frozenset({"width", "height"}):
        raise BenchmarkError(
            "A CORD image_size value does not use the exact v2 schema",
            stage="annotation-parse",
        )
    width = image_size.get("width")
    height = image_size.get("height")
    if type(width) is not int or type(height) is not int or width <= 0 or height <= 0:
        raise BenchmarkError("A CORD row has invalid image dimensions", stage="annotation-parse")
    valid_lines = _as_list(annotation.get("valid_line"), "valid_line")
    if not valid_lines:
        raise BenchmarkError("A CORD row has no valid_line annotations", stage="annotation-parse")
    lines: list[CordLine] = []
    all_words: list[CordWord] = []
    annotation_boundary_clips: list[AnnotationBoundaryClip] = []
    clipped_valid_lines = 0
    for line_index, raw_line in enumerate(valid_lines):
        line = _as_mapping(raw_line, "valid_line entry")
        if frozenset(line) != frozenset({"category", "group_id", "sub_group_id", "words"}):
            raise BenchmarkError(
                "A CORD valid_line entry does not use the exact v2 schema",
                stage="annotation-parse",
            )
        category = line["category"]
        if not isinstance(category, str) or not category or "\x00" in category:
            raise BenchmarkError(
                "A CORD valid_line has an invalid category", stage="annotation-parse"
            )
        for name in ("group_id", "sub_group_id"):
            if type(line[name]) is not int or line[name] < 0:
                raise BenchmarkError(
                    f"A CORD valid_line has an invalid {name}", stage="annotation-parse"
                )
        words = _as_list(line.get("words"), "valid_line words")
        if not words:
            raise BenchmarkError("A CORD valid_line has no words", stage="annotation-parse")
        texts: list[str] = []
        points: list[Point] = []
        line_was_clipped = False
        for word_index, raw_word in enumerate(words):
            word = _as_mapping(raw_word, "word")
            if frozenset(word) != frozenset({"quad", "is_key", "row_id", "text"}):
                raise BenchmarkError(
                    "A CORD word does not use the exact v2 schema", stage="annotation-parse"
                )
            if type(word["is_key"]) is not int or word["is_key"] not in {0, 1}:
                raise BenchmarkError("A CORD word has an invalid is_key", stage="annotation-parse")
            if type(word["row_id"]) is not int or word["row_id"] < 0:
                raise BenchmarkError("A CORD word has an invalid row_id", stage="annotation-parse")
            text = word.get("text")
            if not isinstance(text, str) or not text or "\x00" in text:
                raise BenchmarkError("A CORD word has invalid text", stage="annotation-parse")
            normalized_text = _normalized_text(text)
            if not normalized_text:
                raise BenchmarkError("A CORD word has invalid text", stage="annotation-parse")
            word_polygon, word_clip = _clip_annotation_polygon(
                _quad(word.get("quad")),
                width,
                height,
                locator=f"valid_line[{line_index}].words[{word_index}]",
            )
            texts.append(normalized_text)
            points.extend(word_polygon)
            if word_clip is not None:
                annotation_boundary_clips.append(word_clip)
                line_was_clipped = True
            all_words.append(
                CordWord(
                    line_index=line_index,
                    word_index=word_index,
                    row_id=word["row_id"],
                    text=normalized_text,
                    polygon=word_polygon,
                )
            )
        line_polygon = convex_hull(points)
        clipped_valid_lines += line_was_clipped
        lines.append(
            CordLine(
                line_index=line_index,
                text=" ".join(texts),
                polygon=line_polygon,
            )
        )
    raw_dontcare = tuple(
        (f"dontcare[{group_index}][{polygon_index}]", polygon)
        for group_index, item in enumerate(_as_list(annotation.get("dontcare"), "dontcare"))
        for polygon_index, polygon in enumerate(_dontcare_polygon_group(item))
    )
    raw_repeating_symbols = tuple(
        (f"repeating_symbol[{group_index}][{polygon_index}]", polygon)
        for group_index, item in enumerate(
            _as_list(annotation.get("repeating_symbol"), "repeating_symbol")
        )
        for polygon_index, polygon in enumerate(_repeating_symbol_polygon_group(item))
    )
    clipped_dontcare = tuple(
        _clip_annotation_polygon(polygon, width, height, locator=locator)
        for locator, polygon in raw_dontcare
    )
    clipped_repeating_symbols = tuple(
        _clip_annotation_polygon(polygon, width, height, locator=locator)
        for locator, polygon in raw_repeating_symbols
    )
    dontcare = tuple(item[0] for item in clipped_dontcare)
    repeating_symbols = tuple(item[0] for item in clipped_repeating_symbols)
    annotation_boundary_clips.extend(
        clip for _, clip in (*clipped_dontcare, *clipped_repeating_symbols) if clip is not None
    )
    _as_mapping(annotation.get("gt_parse"), "gt_parse")
    roi = _as_mapping(annotation.get("roi"), "roi")
    if roi:
        roi_polygon = polygon_intersection(_quad(roi), _image_bounds(width, height))
        if not roi_polygon or polygon_area(roi_polygon) <= 0:
            raise BenchmarkError(
                "A CORD receipt ROI does not intersect the declared image",
                stage="annotation-parse",
            )
    else:
        roi_polygon = _image_bounds(width, height)
    words_by_row: dict[int, list[CordWord]] = {}
    for word in all_words:
        words_by_row.setdefault(word.row_id, []).append(word)
    physical_rows = []
    for row_id in sorted(words_by_row):
        row_words = tuple(
            sorted(
                words_by_row[row_id],
                key=lambda word: (
                    min(point[0] for point in word.polygon),
                    min(point[1] for point in word.polygon),
                    word.line_index,
                    word.word_index,
                ),
            )
        )
        physical_rows.append(
            CordPhysicalRow(
                row_id=row_id,
                text=" ".join(word.text for word in row_words),
                polygon=convex_hull(point for word in row_words for point in word.polygon),
                words=row_words,
            )
        )
    return ParsedCordAnnotation(
        document=annotation,
        image_id=image_id,
        width=width,
        height=height,
        lines=tuple(lines),
        words=tuple(all_words),
        rows=tuple(physical_rows),
        roi_polygon=roi_polygon,
        dontcare_polygons=dontcare,
        repeating_symbol_polygons=repeating_symbols,
        clipped_valid_lines=clipped_valid_lines,
        clipped_dontcare_regions=sum(item[1] is not None for item in clipped_dontcare),
        clipped_repeating_symbol_regions=sum(
            item[1] is not None for item in clipped_repeating_symbols
        ),
        annotation_boundary_clips=tuple(annotation_boundary_clips),
    )


def _image_extension(value: bytes) -> str:
    if value.startswith(b"\x89PNG\r\n\x1a\n"):
        return ".png"
    if value.startswith(b"\xff\xd8\xff"):
        return ".jpg"
    if value.startswith((b"II*\x00", b"MM\x00*")):
        return ".tif"
    if value.startswith(b"BM"):
        return ".bmp"
    if len(value) >= 12 and value.startswith(b"RIFF") and value[8:12] == b"WEBP":
        return ".webp"
    raise BenchmarkError("A CORD image uses an unsupported format", stage="corpus-extract")


def _embedded_image_dimensions(value: bytes) -> tuple[int, int]:
    extension = _image_extension(value)
    try:
        if extension == ".png":
            if len(value) < 24 or value[12:16] != b"IHDR":
                raise ValueError("invalid PNG header")
            width, height = struct.unpack(">II", value[16:24])
        elif extension == ".jpg":
            cursor = 2
            width = height = 0
            start_of_frame = {
                0xC0,
                0xC1,
                0xC2,
                0xC3,
                0xC5,
                0xC6,
                0xC7,
                0xC9,
                0xCA,
                0xCB,
                0xCD,
                0xCE,
                0xCF,
            }
            while cursor < len(value):
                if value[cursor] != 0xFF:
                    raise ValueError("invalid JPEG marker")
                while cursor < len(value) and value[cursor] == 0xFF:
                    cursor += 1
                if cursor >= len(value):
                    break
                marker = value[cursor]
                cursor += 1
                if marker in {0x01, 0xD8, 0xD9} or 0xD0 <= marker <= 0xD7:
                    continue
                if cursor + 2 > len(value):
                    raise ValueError("truncated JPEG segment")
                segment_length = int.from_bytes(value[cursor : cursor + 2], "big")
                if segment_length < 2 or cursor + segment_length > len(value):
                    raise ValueError("invalid JPEG segment length")
                if marker in start_of_frame:
                    if segment_length < 7:
                        raise ValueError("truncated JPEG frame")
                    height = int.from_bytes(value[cursor + 3 : cursor + 5], "big")
                    width = int.from_bytes(value[cursor + 5 : cursor + 7], "big")
                    break
                cursor += segment_length
        elif extension == ".bmp":
            if len(value) < 26:
                raise ValueError("truncated BMP header")
            width = struct.unpack_from("<i", value, 18)[0]
            height = abs(struct.unpack_from("<i", value, 22)[0])
        elif extension == ".tif":
            endian = "<" if value[:2] == b"II" else ">"
            if len(value) < 8 or struct.unpack_from(endian + "H", value, 2)[0] != 42:
                raise ValueError("invalid TIFF header")
            directory = struct.unpack_from(endian + "I", value, 4)[0]
            if directory + 2 > len(value):
                raise ValueError("invalid TIFF directory")
            entry_count = struct.unpack_from(endian + "H", value, directory)[0]
            dimensions: dict[int, int] = {}
            for index in range(entry_count):
                offset = directory + 2 + index * 12
                if offset + 12 > len(value):
                    raise ValueError("truncated TIFF directory")
                tag, field_type, count = struct.unpack_from(endian + "HHI", value, offset)
                if tag not in {256, 257} or count != 1 or field_type not in {3, 4}:
                    continue
                dimensions[tag] = struct.unpack_from(
                    endian + ("H" if field_type == 3 else "I"), value, offset + 8
                )[0]
            width = dimensions.get(256, 0)
            height = dimensions.get(257, 0)
        else:
            chunk = value[12:16]
            if chunk == b"VP8X" and len(value) >= 30:
                width = 1 + int.from_bytes(value[24:27], "little")
                height = 1 + int.from_bytes(value[27:30], "little")
            elif chunk == b"VP8 " and len(value) >= 30 and value[23:26] == b"\x9d\x01\x2a":
                width = int.from_bytes(value[26:28], "little") & 0x3FFF
                height = int.from_bytes(value[28:30], "little") & 0x3FFF
            elif chunk == b"VP8L" and len(value) >= 25 and value[20] == 0x2F:
                bits = int.from_bytes(value[21:25], "little")
                width = 1 + (bits & 0x3FFF)
                height = 1 + ((bits >> 14) & 0x3FFF)
            else:
                raise ValueError("unsupported WebP header")
    except (IndexError, struct.error, ValueError) as exc:
        raise BenchmarkError(
            "A CORD embedded image has an invalid dimension header",
            stage="corpus-extract",
        ) from exc
    if width <= 0 or height <= 0:
        raise BenchmarkError(
            "A CORD embedded image has invalid physical dimensions",
            stage="corpus-extract",
        )
    return width, height


def _verify_embedded_image_dimensions(
    value: bytes, annotation: ParsedCordAnnotation
) -> tuple[int, int]:
    dimensions = _embedded_image_dimensions(value)
    if dimensions != (annotation.width, annotation.height):
        raise BenchmarkError(
            "CORD annotation dimensions differ from the embedded image",
            stage="corpus-extract",
        )
    return dimensions


def _split_config(split: str) -> dict[str, object]:
    config = CORD_V2_FILES.get(split)
    if config is None:
        raise BenchmarkError("The requested CORD split is unsupported", stage="corpus-verify")
    return config


def verify_cord_parquet(path: Path, split: str = "test") -> str:
    config = _split_config(split)
    try:
        size = path.stat().st_size
    except OSError as exc:
        raise BenchmarkError(
            "The pinned CORD parquet is unavailable", stage="corpus-verify"
        ) from exc
    if size != config["bytes"]:
        raise BenchmarkError(
            f"The CORD parquet byte length does not match the pinned {split} split",
            stage="corpus-verify",
        )
    digest = sha256_file(path)
    if digest != config["sha256"]:
        raise BenchmarkError(
            f"The CORD parquet SHA-256 does not match the pinned {split} split",
            stage="corpus-verify",
        )
    return digest


def _atomic_write(path: Path, value: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(f"{path.name}.incomplete-{os.getpid()}")
    temporary.write_bytes(value)
    os.replace(temporary, path)


def _embedded_image(value: Any) -> bytes:
    image = _as_mapping(value, "image")
    raw = image.get("bytes")
    if not isinstance(raw, bytes) or not raw:
        raise BenchmarkError(
            "The pinned CORD parquet does not contain embedded image bytes",
            stage="corpus-extract",
        )
    return raw


def _selection_sha256(documents: Sequence[CordDocument]) -> str:
    material = [
        {"rowIndex": document.row_index, "imageSha256": document.sha256} for document in documents
    ]
    return sha256_bytes(canonical_json(material).encode("utf-8"))


def extract_cord_corpus(
    parquet_path: Path, output_root: Path, *, split: str = "test"
) -> ExtractedCorpus:
    _split_config(split)
    try:
        import pyarrow
        import pyarrow.parquet as parquet
    except ImportError as exc:
        raise BenchmarkError(
            "pyarrow is required to read the pinned CORD parquet",
            stage="dependency-check",
        ) from exc
    if pyarrow.__version__ != "25.0.0":
        raise BenchmarkError(
            "The CORD benchmark requires the pinned pyarrow 25.0.0 runtime",
            stage="dependency-check",
        )

    try:
        source = parquet.ParquetFile(parquet_path)
    except Exception as exc:
        raise BenchmarkError(
            "The pinned CORD parquet could not be opened", stage="corpus-read"
        ) from exc
    if source.metadata.num_rows != CORD_V2_ROWS:
        raise BenchmarkError(
            "The pinned CORD parquet does not contain exactly 100 rows",
            stage="corpus-read",
        )
    schema = source.schema_arrow
    columns = frozenset(schema.names)
    image_type = schema.field("image").type if "image" in columns else None
    schema_valid = (
        columns == {"image", "ground_truth"}
        and image_type is not None
        and pyarrow.types.is_struct(image_type)
        and tuple(field.name for field in image_type) == ("bytes", "path")
        and pyarrow.types.is_binary(image_type.field("bytes").type)
        and pyarrow.types.is_string(image_type.field("path").type)
        and pyarrow.types.is_string(schema.field("ground_truth").type)
    )
    if not schema_valid:
        raise BenchmarkError(
            "The pinned CORD parquet does not use the exact image/ground_truth schema",
            stage="corpus-read",
        )

    documents: list[CordDocument] = []
    image_ids: set[int] = set()
    corpus_rows: list[dict[str, Any]] = []
    worker_rows: list[dict[str, Any]] = []
    inventory_rows: list[str] = []
    try:
        batches = source.iter_batches(batch_size=8, columns=["image", "ground_truth"])
        rows = (row for batch in batches for row in batch.to_pylist())
        for row_index, row in enumerate(rows):
            if row_index >= CORD_V2_ROWS:
                raise BenchmarkError("The CORD reader returned extra rows", stage="corpus-read")
            raw_image = _embedded_image(row.get("image"))
            ground_truth = row.get("ground_truth")
            annotation = parse_cord_annotation(row_index, ground_truth, expected_split=split)
            image_width, image_height = _verify_embedded_image_dimensions(raw_image, annotation)
            if annotation.image_id in image_ids:
                raise BenchmarkError(
                    f"The CORD {split} split contains a duplicate image_id", stage="corpus-read"
                )
            image_ids.add(annotation.image_id)
            image_sha256 = sha256_bytes(raw_image)
            extension = _image_extension(raw_image)
            relative_path = f"images/row-{row_index:04d}-{image_sha256[:12]}{extension}"
            image_path = (output_root / relative_path).resolve()
            if image_path.is_file():
                if (
                    image_path.stat().st_size != len(raw_image)
                    or sha256_file(image_path) != image_sha256
                ):
                    raise BenchmarkError(
                        "An existing extracted CORD image failed identity verification",
                        stage="corpus-extract",
                    )
            else:
                _atomic_write(image_path, raw_image)
            annotation_sha256 = sha256_bytes(canonical_json(annotation.document).encode("utf-8"))
            document = CordDocument(
                row_index=row_index,
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
            documents.append(document)
            corpus_rows.append(
                {
                    "schemaVersion": SCHEMA_VERSION,
                    "rowIndex": row_index,
                    "imageId": annotation.image_id,
                    "path": relative_path,
                    "length": len(raw_image),
                    "sha256": image_sha256,
                    "annotationSha256": annotation_sha256,
                    "imageWidth": image_width,
                    "imageHeight": image_height,
                    "groundTruthLines": len(annotation.lines),
                    "groundTruthWords": len(annotation.words),
                    "groundTruthPhysicalRows": len(annotation.rows),
                    "dontcareRegions": len(annotation.dontcare_polygons),
                    "repeatingSymbolRegions": len(annotation.repeating_symbol_polygons),
                    "clippedValidLines": annotation.clipped_valid_lines,
                    "clippedDontcareRegions": annotation.clipped_dontcare_regions,
                    "clippedRepeatingSymbolRegions": (annotation.clipped_repeating_symbol_regions),
                    "annotationBoundaryClips": [
                        _annotation_boundary_clip_record(clip)
                        for clip in annotation.annotation_boundary_clips
                    ],
                }
            )
            worker_rows.append(
                {
                    "schemaVersion": OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION,
                    "path": str(image_path),
                    "length": len(raw_image),
                    "sha256": image_sha256,
                }
            )
            inventory_rows.append(str(image_path))
    except BenchmarkError:
        raise
    except Exception as exc:
        raise BenchmarkError(
            "The pinned CORD parquet could not be read", stage="corpus-read"
        ) from exc
    if len(documents) != CORD_V2_ROWS:
        raise BenchmarkError(
            f"The CORD reader did not return all 100 {split} rows", stage="corpus-read"
        )

    corpus_manifest = output_root / f"cord-v2-{split}-manifest.jsonl"
    worker_manifest = output_root / f"cord-v2-{split}-worker-input-manifest.jsonl"
    inventory = output_root / f"cord-v2-{split}-inventory.txt"
    _atomic_write(
        corpus_manifest,
        "".join(canonical_json(row) + "\n" for row in corpus_rows).encode("utf-8"),
    )
    _atomic_write(
        worker_manifest,
        "".join(canonical_json(row) + "\n" for row in worker_rows).encode("utf-8"),
    )
    _atomic_write(inventory, "".join(f"{row}\n" for row in inventory_rows).encode("utf-8"))
    return ExtractedCorpus(
        documents=tuple(documents),
        corpus_manifest=corpus_manifest,
        worker_manifest=worker_manifest,
        inventory=inventory,
        corpus_manifest_sha256=sha256_file(corpus_manifest),
        worker_manifest_sha256=sha256_file(worker_manifest),
        selection_sha256=_selection_sha256(documents),
        split=split,
    )


def create_corpus_view(
    corpus: ExtractedCorpus,
    row_indices: Sequence[int],
    output_root: Path,
    *,
    name: str,
) -> ExtractedCorpus:
    by_row = {document.row_index: document for document in corpus.documents}
    if len(row_indices) != len(set(row_indices)) or any(
        index not in by_row for index in row_indices
    ):
        raise BenchmarkError("A CORD corpus view has invalid row indices", stage="corpus-view")
    documents = tuple(by_row[index] for index in row_indices)
    corpus_rows = [
        {
            "schemaVersion": SCHEMA_VERSION,
            "rowIndex": document.row_index,
            "imageId": document.image_id,
            "path": document.relative_path,
            "length": document.length,
            "sha256": document.sha256,
            "annotationSha256": document.annotation_sha256,
            "groundTruthLines": len(document.lines),
            "groundTruthWords": len(document.words),
            "groundTruthPhysicalRows": len(document.rows),
            "dontcareRegions": len(document.dontcare_polygons),
            "repeatingSymbolRegions": len(document.repeating_symbol_polygons),
            "clippedValidLines": document.clipped_valid_lines,
            "clippedDontcareRegions": document.clipped_dontcare_regions,
            "clippedRepeatingSymbolRegions": document.clipped_repeating_symbol_regions,
            "annotationBoundaryClips": [
                _annotation_boundary_clip_record(clip)
                for clip in document.annotation_boundary_clips
            ],
        }
        for document in documents
    ]
    worker_rows = [
        {
            "schemaVersion": OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION,
            "path": str(document.path),
            "length": document.length,
            "sha256": document.sha256,
        }
        for document in documents
    ]
    corpus_manifest = output_root / f"{name}-manifest.jsonl"
    worker_manifest = output_root / f"{name}-worker-input-manifest.jsonl"
    inventory = output_root / f"{name}-inventory.txt"
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
        "".join(f"{document.path}\n" for document in documents).encode("utf-8"),
    )
    return ExtractedCorpus(
        documents=documents,
        corpus_manifest=corpus_manifest,
        worker_manifest=worker_manifest,
        inventory=inventory,
        corpus_manifest_sha256=sha256_file(corpus_manifest),
        worker_manifest_sha256=sha256_file(worker_manifest),
        selection_sha256=_selection_sha256(documents),
        split=corpus.split,
    )


def _maximum_matches(
    truths: Sequence[CordLine | CordPhysicalRow],
    predictions: Sequence[Prediction | PredictionPhysicalRow],
    *,
    exact_text: bool,
) -> tuple[PolygonMatch, ...]:
    edges: list[list[tuple[int, float]]] = []
    for truth in truths:
        candidates = []
        for prediction_index, prediction in enumerate(predictions):
            if exact_text and prediction.text != truth.text:
                continue
            iou = polygon_iou(truth.polygon, prediction.polygon)
            if iou >= MATCH_IOU_THRESHOLD:
                candidates.append((prediction_index, iou))
        edges.append(sorted(candidates, key=lambda item: (-item[1], item[0])))

    prediction_to_truth: dict[int, int] = {}

    def augment(truth_index: int, seen: set[int]) -> bool:
        for prediction_index, _ in edges[truth_index]:
            if prediction_index in seen:
                continue
            seen.add(prediction_index)
            previous = prediction_to_truth.get(prediction_index)
            if previous is None or augment(previous, seen):
                prediction_to_truth[prediction_index] = truth_index
                return True
        return False

    for truth_index in range(len(truths)):
        augment(truth_index, set())
    matches = [
        PolygonMatch(
            truth_index=truth_index,
            prediction_index=prediction_index,
            iou=polygon_iou(truths[truth_index].polygon, predictions[prediction_index].polygon),
        )
        for prediction_index, truth_index in prediction_to_truth.items()
    ]
    return tuple(sorted(matches, key=lambda match: (match.truth_index, match.prediction_index)))


def _prh(true_positive: int, false_positive: int, false_negative: int) -> dict[str, Any]:
    precision_denominator = true_positive + false_positive
    recall_denominator = true_positive + false_negative
    precision = true_positive / precision_denominator if precision_denominator else 1.0
    recall = true_positive / recall_denominator if recall_denominator else 1.0
    hmean = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {
        "truePositive": true_positive,
        "falsePositive": false_positive,
        "falseNegative": false_negative,
        "precision": precision,
        "recall": recall,
        "hmean": hmean,
    }


def _coverage_prh(
    matched_predictions: int,
    predicted_units: int,
    matched_truths: int,
    reference_units: int,
) -> dict[str, Any]:
    """Return precision/recall when segmentation cardinalities may differ.

    A split or merged text row can legitimately cover a different number of
    regions on each side.  Recording both matched cardinalities avoids forcing
    that many-to-many result into a one-to-one true-positive count.
    """

    precision = matched_predictions / predicted_units if predicted_units else 1.0
    recall = matched_truths / reference_units if reference_units else 1.0
    hmean = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {
        # Retain the conventional fields for report readers while making the
        # segmentation-tolerant numerator explicit.
        "truePositive": min(matched_predictions, matched_truths),
        "falsePositive": predicted_units - matched_predictions,
        "falseNegative": reference_units - matched_truths,
        "matchedPredictions": matched_predictions,
        "matchedTruths": matched_truths,
        "predictedUnits": predicted_units,
        "referenceUnits": reference_units,
        "precision": precision,
        "recall": recall,
        "hmean": hmean,
    }


def _sequence_levenshtein(reference: Sequence[Any], predicted: Sequence[Any]) -> int:
    if len(reference) < len(predicted):
        reference, predicted = predicted, reference
    previous = list(range(len(predicted) + 1))
    for row, reference_value in enumerate(reference, start=1):
        current = [row]
        for column, predicted_value in enumerate(predicted, start=1):
            current.append(
                min(
                    current[-1] + 1,
                    previous[column] + 1,
                    previous[column - 1] + (reference_value != predicted_value),
                )
            )
        previous = current
    return previous[-1]


def _lcs_length(reference: Sequence[Any], predicted: Sequence[Any]) -> int:
    previous = [0] * (len(predicted) + 1)
    for reference_value in reference:
        current = [0]
        for column, predicted_value in enumerate(predicted, start=1):
            if reference_value == predicted_value:
                current.append(previous[column - 1] + 1)
            else:
                current.append(max(current[-1], previous[column]))
        previous = current
    return previous[-1]


def _sequence_counts(reference: Sequence[Any], predicted: Sequence[Any]) -> SequenceCounts:
    return SequenceCounts(
        reference_units=len(reference),
        predicted_units=len(predicted),
        matches=_lcs_length(reference, predicted),
        edits=_sequence_levenshtein(reference, predicted),
    )


def _reading_order(prediction: Prediction) -> tuple[Any, ...]:
    xs = [point[0] for point in prediction.polygon]
    ys = [point[1] for point in prediction.polygon]
    return min(ys), min(xs), max(ys), max(xs), prediction.text, prediction.record_id


def _polygon_center(polygon: Polygon) -> tuple[float, float]:
    return (
        statistics.fmean(point[0] for point in polygon),
        statistics.fmean(point[1] for point in polygon),
    )


def _polygon_dimensions(polygon: Polygon) -> tuple[float, float]:
    xs = [point[0] for point in polygon]
    ys = [point[1] for point in polygon]
    return max(xs) - min(xs), max(ys) - min(ys)


def _polygon_text_slope(polygon: Polygon) -> float | None:
    width, height = _polygon_dimensions(polygon)
    if width <= 0 or height <= 0 or width < 1.5 * height:
        return None
    center_x, center_y = _polygon_center(polygon)
    covariance_xx = sum((x - center_x) ** 2 for x, _ in polygon)
    covariance_yy = sum((y - center_y) ** 2 for _, y in polygon)
    covariance_xy = sum((x - center_x) * (y - center_y) for x, y in polygon)
    if covariance_xx <= 0:
        return None
    # The principal axis tracks the long edges of a word quadrilateral.  Using
    # the leftmost and rightmost vertices instead accidentally measures a box
    # diagonal whenever perspective makes only one corner extremal.
    angle = 0.5 * math.atan2(
        2.0 * covariance_xy,
        covariance_xx - covariance_yy,
    )
    slope = math.tan(angle)
    return slope if math.isfinite(slope) and abs(slope) <= 0.5 else None


def _cluster_prediction_rows(
    predictions: Sequence[Prediction],
    dominant_slope: float | None = None,
    normalized_distance_threshold: float = ROW_CLUSTER_NORMALIZED_DISTANCE,
) -> tuple[tuple[PredictionPhysicalRow, ...], float]:
    if not predictions:
        return (), 0.0
    if dominant_slope is None:
        slopes = [
            slope
            for prediction in predictions
            if (slope := _polygon_text_slope(prediction.polygon)) is not None
        ]
        median_slope = statistics.median(slopes) if slopes else 0.0
        ordered_slopes = sorted(slopes)
        candidates = {0.0, -0.02, 0.02, median_slope}
        if ordered_slopes:
            candidates.add(ordered_slopes[len(ordered_slopes) // 4])
            candidates.add(ordered_slopes[(3 * len(ordered_slopes)) // 4])
        alternatives = []
        for candidate in sorted(candidates):
            rows, _ = _cluster_prediction_rows(
                predictions,
                dominant_slope=candidate,
                normalized_distance_threshold=normalized_distance_threshold,
            )
            total_thickness = sum(
                max(y - candidate * x for x, y in row.polygon)
                - min(y - candidate * x for x, y in row.polygon)
                for row in rows
            )
            alternatives.append(
                (
                    total_thickness,
                    len(rows),
                    abs(candidate - median_slope),
                    candidate,
                    rows,
                )
            )
        _, _, _, selected_slope, selected_rows = min(alternatives, key=lambda item: item[:4])
        return selected_rows, selected_slope
    items = []
    for source_index, prediction in enumerate(predictions):
        center_x, center_y = _polygon_center(prediction.polygon)
        deskewed_verticals = [y - dominant_slope * x for x, y in prediction.polygon]
        height = max(deskewed_verticals) - min(deskewed_verticals)
        items.append(
            {
                "prediction": prediction,
                "x": center_x,
                "v": center_y - dominant_slope * center_x,
                "height": max(height, 1.0),
                "source_index": source_index,
            }
        )
    groups: list[list[dict[str, Any]]] = []
    for item in sorted(
        items,
        key=lambda value: (
            value["v"],
            value["x"],
            value["source_index"],
        ),
    ):
        candidates = []
        for group_index, group in enumerate(groups):
            row_v = statistics.median(value["v"] for value in group)
            row_height = statistics.median(value["height"] for value in group)
            normalized_distance = abs(item["v"] - row_v) / max(min(item["height"], row_height), 1.0)
            if normalized_distance <= normalized_distance_threshold:
                candidates.append((normalized_distance, group_index))
        if candidates:
            groups[min(candidates)[1]].append(item)
        else:
            groups.append([item])
    groups.sort(
        key=lambda group: (
            statistics.median(value["v"] for value in group),
            min(value["x"] for value in group),
            min(value["source_index"] for value in group),
        )
    )
    rows = []
    for group in groups:
        members = tuple(
            value["prediction"]
            for value in sorted(
                group,
                key=lambda value: (
                    value["x"],
                    value["v"],
                    value["source_index"],
                ),
            )
        )
        identity = sha256_bytes(
            canonical_json([member.record_id for member in members]).encode("utf-8")
        )
        rows.append(
            PredictionPhysicalRow(
                text=" ".join(_normalized_text(member.text) for member in members),
                polygon=convex_hull(point for member in members for point in member.polygon),
                confidence=statistics.fmean(member.confidence for member in members),
                record_id=f"sha256:{identity}",
                members=members,
            )
        )
    return tuple(rows), dominant_slope


def _reference_rows(document: CordDocument) -> tuple[CordPhysicalRow, ...]:
    if document.words:
        _, reference_slope = _cluster_prediction_rows(
            tuple(
                Prediction(
                    text=word.text,
                    polygon=word.polygon,
                    confidence=1.0,
                    record_id=f"reference-slope-{index}",
                )
                for index, word in enumerate(document.words)
            )
        )
        groups: list[CordPhysicalRow] = []
        for row_id in sorted({word.row_id for word in document.words}):
            row_words = tuple(word for word in document.words if word.row_id == row_id)
            row_polygon = convex_hull(point for word in row_words for point in word.polygon)
            word_by_identity: dict[str, CordWord] = {}
            word_predictions = []
            for word in row_words:
                identity = f"reference-word-{row_id}-{word.line_index}-{word.word_index}"
                word_by_identity[identity] = word
                word_predictions.append(
                    Prediction(
                        text=word.text,
                        polygon=word.polygon,
                        confidence=1.0,
                        record_id=identity,
                    )
                )
            split_rows, _ = _cluster_prediction_rows(
                word_predictions,
                dominant_slope=reference_slope,
                normalized_distance_threshold=REFERENCE_ROW_SPLIT_NORMALIZED_DISTANCE,
            )
            split_line_sets = [
                {word_by_identity[member.record_id].line_index for member in split_row.members}
                for split_row in split_rows
            ]
            source_lines = {word.line_index for word in row_words}
            accept_split = (
                len(split_rows) == 2
                and len(source_lines) == 2
                and all(len(line_set) == 1 for line_set in split_line_sets)
                and set().union(*split_line_sets) == source_lines
                and all(any(character.isalnum() for character in row.text) for row in split_rows)
            )
            if accept_split:
                for split_row in split_rows:
                    members = tuple(
                        word_by_identity[member.record_id] for member in split_row.members
                    )
                    groups.append(
                        CordPhysicalRow(
                            row_id=row_id,
                            text=split_row.text,
                            polygon=split_row.polygon,
                            words=members,
                        )
                    )
                continue
            serialized_rows, _ = _cluster_prediction_rows(
                word_predictions,
                dominant_slope=_polygon_text_slope(row_polygon) or 0.0,
                normalized_distance_threshold=REFERENCE_ROW_MERGE_NORMALIZED_DISTANCE,
            )
            groups.append(
                CordPhysicalRow(
                    row_id=row_id,
                    text=" ".join(row.text for row in serialized_rows),
                    polygon=row_polygon,
                    words=row_words,
                )
            )

        distinct_row_ids = sorted({group.row_id for group in groups})
        row_id_rank = {row_id: rank for rank, row_id in enumerate(distinct_row_ids)}

        def group_geometry(group: CordPhysicalRow) -> tuple[float, float, float, float]:
            center_x, center_y = _polygon_center(group.polygon)
            verticals = [y - reference_slope * x for x, y in group.polygon]
            xs = [x for x, _ in group.polygon]
            return (
                center_y - reference_slope * center_x,
                max(verticals) - min(verticals),
                min(xs),
                max(xs),
            )

        while True:
            candidates = []
            for left_index, left in enumerate(groups):
                for right_index in range(left_index + 1, len(groups)):
                    right = groups[right_index]
                    left_ids = {word.row_id for word in left.words}
                    right_ids = {word.row_id for word in right.words}
                    if left_ids & right_ids:
                        continue
                    if (
                        min(
                            abs(row_id_rank[left_id] - row_id_rank[right_id])
                            for left_id in left_ids
                            for right_id in right_ids
                        )
                        != 1
                    ):
                        continue
                    left_lines = {word.line_index for word in left.words}
                    right_lines = {word.line_index for word in right.words}
                    if not left_lines & right_lines:
                        continue
                    left_v, left_height, left_min_x, left_max_x = group_geometry(left)
                    right_v, right_height, right_min_x, right_max_x = group_geometry(right)
                    if not (left_max_x <= right_min_x or right_max_x <= left_min_x):
                        continue
                    normalized_distance = abs(left_v - right_v) / max(
                        min(left_height, right_height), 1.0
                    )
                    if normalized_distance <= REFERENCE_ROW_MERGE_NORMALIZED_DISTANCE:
                        candidates.append((normalized_distance, left_index, right_index))
            if not candidates:
                break
            _, left_index, right_index = min(candidates)
            left = groups[left_index]
            right = groups[right_index]
            ordered = sorted(
                (left, right),
                key=lambda group: min(x for x, _ in group.polygon),
            )
            merged_words = tuple(word for group in ordered for word in group.words)
            merged = CordPhysicalRow(
                row_id=min(left.row_id, right.row_id),
                text=" ".join(group.text for group in ordered),
                polygon=convex_hull(point for group in ordered for point in group.polygon),
                words=merged_words,
            )
            groups = [
                group
                for index, group in enumerate(groups)
                if index not in {left_index, right_index}
            ]
            groups.append(merged)
        groups.sort(
            key=lambda group: (
                group_geometry(group)[0],
                group_geometry(group)[2],
                group.row_id,
            )
        )
        return tuple(
            CordPhysicalRow(
                row_id=row_index,
                text=group.text,
                polygon=group.polygon,
                words=group.words,
            )
            for row_index, group in enumerate(groups)
        )
    if document.rows:
        return tuple(sorted(document.rows, key=lambda row: row.row_id))
    return tuple(
        CordPhysicalRow(
            row_id=line.line_index,
            text=_normalized_text(line.text),
            polygon=line.polygon,
            words=(),
        )
        for line in document.lines
    )


def _segmentation_overlap(left: Polygon, right: Polygon) -> float:
    minimum_area = min(polygon_area(left), polygon_area(right))
    if minimum_area <= 0:
        return 0.0
    return polygon_area(polygon_intersection(left, right)) / minimum_area


def _overlap_components(
    truths: Sequence[CordPhysicalRow],
    predictions: Sequence[PredictionPhysicalRow],
) -> tuple[tuple[tuple[int, ...], tuple[int, ...]], ...]:
    """Build spatial components without assuming one-to-one row segmentation."""

    truth_to_predictions: list[list[int]] = [[] for _ in truths]
    prediction_to_truths: list[list[int]] = [[] for _ in predictions]
    for truth_index, truth in enumerate(truths):
        for prediction_index, prediction in enumerate(predictions):
            if _segmentation_overlap(
                truth.polygon, prediction.polygon
            ) >= SEGMENTATION_OVERLAP_THRESHOLD or any(
                _segmentation_overlap(truth.polygon, member.polygon)
                >= SEGMENTATION_OVERLAP_THRESHOLD
                for member in prediction.members
            ):
                truth_to_predictions[truth_index].append(prediction_index)
                prediction_to_truths[prediction_index].append(truth_index)

    components: list[tuple[tuple[int, ...], tuple[int, ...]]] = []
    seen_truths: set[int] = set()
    seen_predictions: set[int] = set()
    for initial_kind, initial_index in [("truth", index) for index in range(len(truths))] + [
        ("prediction", index) for index in range(len(predictions))
    ]:
        if (
            initial_kind == "truth"
            and initial_index in seen_truths
            or initial_kind == "prediction"
            and initial_index in seen_predictions
        ):
            continue
        pending = [(initial_kind, initial_index)]
        component_truths: set[int] = set()
        component_predictions: set[int] = set()
        while pending:
            kind, index = pending.pop()
            if kind == "truth":
                if index in seen_truths:
                    continue
                seen_truths.add(index)
                component_truths.add(index)
                pending.extend(
                    ("prediction", prediction_index)
                    for prediction_index in truth_to_predictions[index]
                )
            else:
                if index in seen_predictions:
                    continue
                seen_predictions.add(index)
                component_predictions.add(index)
                pending.extend(
                    ("truth", truth_index) for truth_index in prediction_to_truths[index]
                )
        components.append((tuple(sorted(component_truths)), tuple(sorted(component_predictions))))
    return tuple(components)


def _reference_component_text(truths: Sequence[CordPhysicalRow], indices: Sequence[int]) -> str:
    return " ".join(_normalized_text(truths[index].text) for index in sorted(indices))


def _prediction_component_text(
    truths: Sequence[CordPhysicalRow],
    truth_indices: Sequence[int],
    predictions: Sequence[PredictionPhysicalRow],
    prediction_indices: Sequence[int],
) -> str:
    members = tuple(
        (prediction_index, member_index, member)
        for prediction_index in sorted(prediction_indices)
        for member_index, member in enumerate(predictions[prediction_index].members)
    )
    assigned: dict[int, list[tuple[int, int, Prediction]]] = {index: [] for index in truth_indices}
    unassigned = []
    for prediction_index, member_index, member in members:
        candidates = sorted(
            (
                (_segmentation_overlap(member.polygon, truths[index].polygon), index)
                for index in truth_indices
            ),
            key=lambda item: (-item[0], item[1]),
        )
        if candidates and candidates[0][0] > 0:
            assigned[candidates[0][1]].append((prediction_index, member_index, member))
        else:
            unassigned.append((prediction_index, member_index, member))

    def member_order(value: tuple[int, int, Prediction]) -> tuple[Any, ...]:
        prediction_index, member_index, member = value
        center_x, center_y = _polygon_center(member.polygon)
        return prediction_index, center_x, center_y, member_index

    text_fragments = []
    for truth_index in sorted(truth_indices):
        truth_members = tuple(member for _, _, member in assigned[truth_index])
        truth_slope = _polygon_text_slope(truths[truth_index].polygon) or 0.0
        rows, _ = _cluster_prediction_rows(
            truth_members,
            dominant_slope=truth_slope,
            normalized_distance_threshold=REFERENCE_ROW_MERGE_NORMALIZED_DISTANCE,
        )
        text_fragments.extend(row.text for row in rows)
    text_fragments.extend(
        _normalized_text(member.text) for _, _, member in sorted(unassigned, key=member_order)
    )
    return " ".join(text_fragments)


def _union_covered_fraction(targets: Sequence[Polygon], covers: Sequence[Polygon]) -> float:
    ShapelyPolygon, unary_union, _, _ = _shapely_runtime()
    target_shapes = [ShapelyPolygon(target) for target in targets if polygon_area(target) > 0]
    if not target_shapes:
        return 0.0
    target_union = unary_union(target_shapes)
    target_area = float(target_union.area)
    if target_area <= 0:
        return 0.0
    cover_shapes = [ShapelyPolygon(cover) for cover in covers if polygon_area(cover) > 0]
    covered_area = (
        float(target_union.intersection(unary_union(cover_shapes)).area) if cover_shapes else 0.0
    )
    return min(covered_area / target_area, 1.0)


def _covered_fraction(target: Polygon, covers: Sequence[Polygon]) -> float:
    return _union_covered_fraction((target,), covers)


def _segmentation_covered_fraction(target: Polygon, covers: Sequence[Polygon]) -> float:
    """Exact union coverage retained as a named scorer primitive for tests/reports."""

    return _covered_fraction(target, covers)


def _truth_localization_support(
    truth: CordPhysicalRow, prediction_member_polygons: Sequence[Polygon]
) -> float:
    if truth.words:
        return _union_covered_fraction(
            tuple(word.polygon for word in truth.words),
            prediction_member_polygons,
        )
    return _covered_fraction(truth.polygon, prediction_member_polygons)


def _prediction_localization_support(
    prediction: PredictionPhysicalRow, truths: Sequence[CordPhysicalRow]
) -> float:
    prediction_member_polygons = tuple(member.polygon for member in prediction.members)
    candidates = [
        _union_covered_fraction(
            prediction_member_polygons,
            tuple(truth.polygon for truth in truths),
        )
    ]
    truth_word_polygons = tuple(word.polygon for truth in truths for word in truth.words)
    if truth_word_polygons:
        candidates.append(_union_covered_fraction(prediction_member_polygons, truth_word_polygons))
    return max(candidates)


def _ignored_prediction(prediction: Prediction, ignored: Sequence[Polygon]) -> bool:
    prediction_area = polygon_area(prediction.polygon)
    return (
        prediction_area > 0
        and _covered_fraction(prediction.polygon, ignored) > IGNORE_PRECISION_THRESHOLD
    )


def _inside_evaluation_roi(prediction: Prediction, roi: Polygon) -> bool:
    prediction_area = polygon_area(prediction.polygon)
    if prediction_area <= 0:
        return False
    return (
        polygon_area(polygon_intersection(prediction.polygon, roi)) / prediction_area
        > IGNORE_PRECISION_THRESHOLD
    )


def score_document(document: CordDocument, predictions: Sequence[Prediction]) -> dict[str, Any]:
    truths = _reference_rows(document)
    ignore_classes = tuple(
        (
            prediction,
            _ignored_prediction(prediction, document.dontcare_polygons)
            and not any(
                _segmentation_overlap(prediction.polygon, truth.polygon)
                >= SEGMENTATION_OVERLAP_THRESHOLD
                for truth in truths
            ),
            _ignored_prediction(prediction, document.repeating_symbol_polygons)
            and not any(
                _segmentation_overlap(prediction.polygon, truth.polygon)
                >= SEGMENTATION_OVERLAP_THRESHOLD
                for truth in truths
            ),
        )
        for prediction in predictions
    )
    roi = document.roi_polygon
    roi_classes = tuple(
        (prediction, _inside_evaluation_roi(prediction, roi) if roi else True)
        for prediction in predictions
    )
    inside_roi = {prediction.record_id for prediction, included in roi_classes if included}
    overlaps_valid_text = {
        prediction.record_id
        for prediction in predictions
        if any(
            _segmentation_overlap(prediction.polygon, truth.polygon)
            >= SEGMENTATION_OVERLAP_THRESHOLD
            for truth in truths
        )
    }
    included_fragments = tuple(
        prediction
        for prediction, ignored_by_dontcare, ignored_by_repeating_symbol in ignore_classes
        if (
            (prediction.record_id in inside_roi or prediction.record_id in overlaps_valid_text)
            and not ignored_by_dontcare
            and not ignored_by_repeating_symbol
        )
    )
    included, dominant_slope = _cluster_prediction_rows(included_fragments)
    ignored_count = len(predictions) - len(included_fragments)
    components = _overlap_components(truths, included)
    supported_truths: set[int] = set()
    supported_predictions: set[int] = set()
    exact_components = []
    component_sequences = []
    for truth_indices, prediction_indices in components:
        prediction_member_polygons = tuple(
            member.polygon for index in prediction_indices for member in included[index].members
        )
        component_truths = tuple(truths[index] for index in truth_indices)
        component_supported_truths = {
            index
            for index in truth_indices
            if _truth_localization_support(truths[index], prediction_member_polygons)
            >= SEGMENTATION_OVERLAP_THRESHOLD
        }
        component_supported_predictions = {
            index
            for index in prediction_indices
            if _prediction_localization_support(included[index], component_truths)
            >= SEGMENTATION_OVERLAP_THRESHOLD
        }
        supported_truths.update(component_supported_truths)
        supported_predictions.update(component_supported_predictions)
        truth_text = _reference_component_text(truths, truth_indices)
        prediction_text = _prediction_component_text(
            truths,
            truth_indices,
            included,
            prediction_indices,
        )
        truth_words = tuple(truth_text.split())
        prediction_words = tuple(prediction_text.split())
        component_sequences.append(
            (
                _sequence_counts(truth_text, prediction_text),
                _sequence_counts(truth_words, prediction_words),
            )
        )
        spatially_complete = (
            bool(truth_indices)
            and bool(prediction_indices)
            and component_supported_truths == set(truth_indices)
            and component_supported_predictions == set(prediction_indices)
        )
        if spatially_complete and truth_text == prediction_text:
            exact_components.append((truth_indices, prediction_indices))
    matched_truths = len(supported_truths)
    matched_predictions = len(supported_predictions)
    exact_truths = sum(len(item[0]) for item in exact_components)
    exact_predictions = sum(len(item[1]) for item in exact_components)
    detection = _coverage_prh(
        matched_predictions,
        len(included),
        matched_truths,
        len(truths),
    )
    end_to_end = _coverage_prh(
        exact_predictions,
        len(included),
        exact_truths,
        len(truths),
    )

    reference_words = tuple(word for row in truths for word in row.text.split())
    predicted_words = tuple(word for prediction in included for word in prediction.text.split())
    reference_word_counts = Counter(reference_words)
    predicted_word_counts = Counter(predicted_words)
    matching_tokens = sum((reference_word_counts & predicted_word_counts).values())
    token_precision = matching_tokens / max(len(predicted_words), 1)
    token_recall = matching_tokens / max(len(reference_words), 1)
    token_f1 = (
        2 * token_precision * token_recall / (token_precision + token_recall)
        if token_precision + token_recall
        else 0.0
    )
    character = SequenceCounts(
        reference_units=sum(item[0].reference_units for item in component_sequences),
        predicted_units=sum(item[0].predicted_units for item in component_sequences),
        matches=sum(item[0].matches for item in component_sequences),
        edits=sum(item[0].edits for item in component_sequences),
    )
    words = SequenceCounts(
        reference_units=sum(item[1].reference_units for item in component_sequences),
        predicted_units=sum(item[1].predicted_units for item in component_sequences),
        matches=sum(item[1].matches for item in component_sequences),
        edits=sum(item[1].edits for item in component_sequences),
    )
    maximum_characters = max(character.reference_units, character.predicted_units, 1)
    return {
        "rowIndex": document.row_index,
        "imageSha256": document.sha256,
        "groundTruthLines": len(truths),
        "groundTruthValidLines": len(document.lines),
        "groundTruthWords": len(document.words),
        "dontcareRegions": len(document.dontcare_polygons),
        "repeatingSymbolRegions": len(document.repeating_symbol_polygons),
        "clippedValidLines": document.clipped_valid_lines,
        "clippedDontcareRegions": document.clipped_dontcare_regions,
        "clippedRepeatingSymbolRegions": document.clipped_repeating_symbol_regions,
        "annotationBoundaryClipRecords": len(document.annotation_boundary_clips),
        "annotationBoundaryMaximumOvershootPixels": max(
            (
                max(clip.horizontal_overshoot_pixels, clip.vertical_overshoot_pixels)
                for clip in document.annotation_boundary_clips
            ),
            default=0.0,
        ),
        "annotationBoundaryMaximumOvershootRatio": max(
            (
                max(clip.horizontal_overshoot_ratio, clip.vertical_overshoot_ratio)
                for clip in document.annotation_boundary_clips
            ),
            default=0.0,
        ),
        "annotationBoundaryMinimumRetainedAreaRatio": min(
            (clip.retained_area_ratio for clip in document.annotation_boundary_clips),
            default=1.0,
        ),
        "rawPredictedLines": len(predictions),
        "predictedFragments": len(included_fragments),
        "predictedLines": len(included),
        "mergedPredictionFragments": len(included_fragments) - len(included),
        "dominantTextSlope": dominant_slope,
        "ignoredPredictions": ignored_count,
        "ignoredByDontcare": sum(item[1] for item in ignore_classes),
        "ignoredByRepeatingSymbol": sum(item[2] for item in ignore_classes),
        "ignoredByRoi": sum(
            not included and prediction.record_id not in overlaps_valid_text
            for prediction, included in roi_classes
        ),
        "localizationUnsupportedTruths": len(truths) - matched_truths,
        "localizationUnsupportedPredictions": len(included) - matched_predictions,
        "detection": detection,
        "endToEndExact": end_to_end,
        "exactLineRate": exact_truths / max(len(truths), 1),
        "documentExact": (end_to_end["falsePositive"] == 0 and end_to_end["falseNegative"] == 0),
        "wordAccuracy": token_f1,
        "tokenPrecision": token_precision,
        "tokenRecall": token_recall,
        "tokenF1": token_f1,
        "charPrecision": character.matches / max(character.predicted_units, 1),
        "charRecall": character.matches / max(character.reference_units, 1),
        "oneMinusNed": max(0.0, 1.0 - character.edits / maximum_characters),
        "pageCer": character.edits / max(character.reference_units, 1),
        "pageWer": words.edits / max(words.reference_units, 1),
        "counts": {
            "referenceCharacters": character.reference_units,
            "predictedCharacters": character.predicted_units,
            "matchingCharacters": character.matches,
            "characterEdits": character.edits,
            "referenceWords": words.reference_units,
            "predictedWords": words.predicted_units,
            "matchingWords": words.matches,
            "wordEdits": words.edits,
            "matchingTokensOrderInvariant": matching_tokens,
        },
    }


def aggregate_metrics(per_document: Sequence[dict[str, Any]]) -> dict[str, Any]:
    if not per_document:
        raise BenchmarkError("No CORD documents were scored", stage="metrics")
    detection = _coverage_prh(
        sum(item["detection"]["matchedPredictions"] for item in per_document),
        sum(item["detection"]["predictedUnits"] for item in per_document),
        sum(item["detection"]["matchedTruths"] for item in per_document),
        sum(item["detection"]["referenceUnits"] for item in per_document),
    )
    end_to_end = _coverage_prh(
        sum(item["endToEndExact"]["matchedPredictions"] for item in per_document),
        sum(item["endToEndExact"]["predictedUnits"] for item in per_document),
        sum(item["endToEndExact"]["matchedTruths"] for item in per_document),
        sum(item["endToEndExact"]["referenceUnits"] for item in per_document),
    )
    counts = {
        key: sum(item["counts"][key] for item in per_document) for key in per_document[0]["counts"]
    }
    token_precision = counts["matchingTokensOrderInvariant"] / max(counts["predictedWords"], 1)
    token_recall = counts["matchingTokensOrderInvariant"] / max(counts["referenceWords"], 1)
    token_f1 = (
        2 * token_precision * token_recall / (token_precision + token_recall)
        if token_precision + token_recall
        else 0.0
    )
    micro = {
        "documents": len(per_document),
        "groundTruthLines": sum(item["groundTruthLines"] for item in per_document),
        "groundTruthValidLines": sum(item["groundTruthValidLines"] for item in per_document),
        "groundTruthWords": sum(item["groundTruthWords"] for item in per_document),
        "dontcareRegions": sum(item["dontcareRegions"] for item in per_document),
        "repeatingSymbolRegions": sum(item["repeatingSymbolRegions"] for item in per_document),
        "clippedValidLines": sum(item["clippedValidLines"] for item in per_document),
        "clippedDontcareRegions": sum(item["clippedDontcareRegions"] for item in per_document),
        "clippedRepeatingSymbolRegions": sum(
            item["clippedRepeatingSymbolRegions"] for item in per_document
        ),
        "annotationBoundaryClipRecords": sum(
            item["annotationBoundaryClipRecords"] for item in per_document
        ),
        "annotationBoundaryMaximumOvershootPixels": max(
            item["annotationBoundaryMaximumOvershootPixels"] for item in per_document
        ),
        "annotationBoundaryMaximumOvershootRatio": max(
            item["annotationBoundaryMaximumOvershootRatio"] for item in per_document
        ),
        "annotationBoundaryMinimumRetainedAreaRatio": min(
            item["annotationBoundaryMinimumRetainedAreaRatio"] for item in per_document
        ),
        "rawPredictedLines": sum(item["rawPredictedLines"] for item in per_document),
        "predictedFragments": sum(item["predictedFragments"] for item in per_document),
        "predictedLines": sum(item["predictedLines"] for item in per_document),
        "mergedPredictionFragments": sum(
            item["mergedPredictionFragments"] for item in per_document
        ),
        "ignoredPredictions": sum(item["ignoredPredictions"] for item in per_document),
        "ignoredByDontcare": sum(item["ignoredByDontcare"] for item in per_document),
        "ignoredByRepeatingSymbol": sum(item["ignoredByRepeatingSymbol"] for item in per_document),
        "ignoredByRoi": sum(item["ignoredByRoi"] for item in per_document),
        "localizationUnsupportedTruths": sum(
            item["localizationUnsupportedTruths"] for item in per_document
        ),
        "localizationUnsupportedPredictions": sum(
            item["localizationUnsupportedPredictions"] for item in per_document
        ),
        "detection": detection,
        "endToEndExact": end_to_end,
        "exactLineRate": end_to_end["matchedTruths"] / max(end_to_end["referenceUnits"], 1),
        "documentExactRate": sum(item["documentExact"] for item in per_document)
        / len(per_document),
        "wordAccuracy": token_f1,
        "tokenPrecision": token_precision,
        "tokenRecall": token_recall,
        "tokenF1": token_f1,
        "charPrecision": counts["matchingCharacters"] / max(counts["predictedCharacters"], 1),
        "charRecall": counts["matchingCharacters"] / max(counts["referenceCharacters"], 1),
        "oneMinusNed": max(
            0.0,
            1.0
            - counts["characterEdits"]
            / max(counts["referenceCharacters"], counts["predictedCharacters"], 1),
        ),
        "pageCer": counts["characterEdits"] / max(counts["referenceCharacters"], 1),
        "pageWer": counts["wordEdits"] / max(counts["referenceWords"], 1),
        "counts": counts,
    }
    macro = {
        "detectionPrecision": statistics.fmean(
            item["detection"]["precision"] for item in per_document
        ),
        "detectionRecall": statistics.fmean(item["detection"]["recall"] for item in per_document),
        "detectionHmean": statistics.fmean(item["detection"]["hmean"] for item in per_document),
        "endToEndPrecision": statistics.fmean(
            item["endToEndExact"]["precision"] for item in per_document
        ),
        "endToEndRecall": statistics.fmean(
            item["endToEndExact"]["recall"] for item in per_document
        ),
        "endToEndHmean": statistics.fmean(item["endToEndExact"]["hmean"] for item in per_document),
        "exactLineRate": statistics.fmean(item["exactLineRate"] for item in per_document),
        "documentExactRate": statistics.fmean(
            float(item["documentExact"]) for item in per_document
        ),
        "wordAccuracy": statistics.fmean(item["wordAccuracy"] for item in per_document),
        "tokenPrecision": statistics.fmean(item["tokenPrecision"] for item in per_document),
        "tokenRecall": statistics.fmean(item["tokenRecall"] for item in per_document),
        "tokenF1": statistics.fmean(item["tokenF1"] for item in per_document),
        "charPrecision": statistics.fmean(item["charPrecision"] for item in per_document),
        "charRecall": statistics.fmean(item["charRecall"] for item in per_document),
        "oneMinusNed": statistics.fmean(item["oneMinusNed"] for item in per_document),
        "pageCer": statistics.fmean(item["pageCer"] for item in per_document),
        "pageWer": statistics.fmean(item["pageWer"] for item in per_document),
    }
    worst = sorted(
        per_document,
        key=lambda item: (
            -item["pageCer"],
            -item["pageWer"],
            item["endToEndExact"]["hmean"],
            item["detection"]["hmean"],
            item["rowIndex"],
        ),
    )[:5]
    worst_documents = [
        {
            "rowIndex": item["rowIndex"],
            "imageSha256": item["imageSha256"],
            "detectionHmean": item["detection"]["hmean"],
            "endToEndHmean": item["endToEndExact"]["hmean"],
            "pageCer": item["pageCer"],
            "pageWer": item["pageWer"],
        }
        for item in worst
    ]
    return {
        "micro": micro,
        "macro": macro,
        "worstDocuments": worst_documents,
        "perDocument": list(per_document),
    }


def _prediction(record: dict[str, Any]) -> Prediction:
    text = record.get("text")
    attributes = record.get("attributes")
    record_id = record.get("recordId")
    origin = record.get("origin")
    if not isinstance(text, str) or not text or "\x00" in text:
        raise BenchmarkError("A CORD OCR record has invalid text", stage="scoring")
    if not isinstance(attributes, dict) or not isinstance(origin, dict):
        raise BenchmarkError("A CORD OCR record has invalid provenance", stage="scoring")
    if origin.get("kind") != "ocr" or attributes.get("pageNumber") != 1:
        raise BenchmarkError("A CORD output record is not single-page OCR", stage="scoring")
    if attributes.get("coordinateSpace") != "render-pixels":
        raise BenchmarkError("A CORD OCR record uses an invalid coordinate space", stage="scoring")
    raw_box = attributes.get("box")
    if not isinstance(raw_box, list) or len(raw_box) != 4:
        raise BenchmarkError("A CORD OCR record has an invalid box", stage="scoring")
    points = []
    for raw_point in raw_box:
        if not isinstance(raw_point, list) or len(raw_point) != 2:
            raise BenchmarkError("A CORD OCR record has an invalid box", stage="scoring")
        points.append(
            (_coordinate(raw_point[0], "prediction.x"), _coordinate(raw_point[1], "prediction.y"))
        )
    confidence = attributes.get("confidence")
    if isinstance(confidence, bool) or not isinstance(confidence, (int, float)):
        raise BenchmarkError("A CORD OCR record has invalid confidence", stage="scoring")
    numeric_confidence = float(confidence)
    if not math.isfinite(numeric_confidence) or not 0 <= numeric_confidence <= 1:
        raise BenchmarkError("A CORD OCR record has invalid confidence", stage="scoring")
    if not isinstance(record_id, str):
        raise BenchmarkError("A CORD OCR record has no identity", stage="scoring")
    return Prediction(
        text=unicodedata.normalize("NFC", text),
        polygon=convex_hull(points),
        confidence=numeric_confidence,
        record_id=record_id,
    )


def validate_run_provenance(
    records: Sequence[dict[str, Any]],
    assessments: Sequence[dict[str, Any]],
    corpus: ExtractedCorpus,
    model_pack_sha256: str,
    requested_threads: int,
) -> tuple[bool, tuple[str, ...], str, dict[str, int], dict[str, int]]:
    errors: list[str] = []
    documents = {str(document.path): document for document in corpus.documents}
    assessment_by_source = {
        assessment.get("sourceFile"): assessment
        for assessment in assessments
        if isinstance(assessment, dict) and isinstance(assessment.get("sourceFile"), str)
    }
    if len(assessments) != len(corpus.documents):
        errors.append("the run did not emit exactly one assessment per selected source")
    if len(assessment_by_source) != len(assessments):
        errors.append("the run emitted a duplicate or malformed assessment source")
    unknown_assessment_sources = set(assessment_by_source) - set(documents)
    if unknown_assessment_sources:
        errors.append("an assessment references an unknown CORD source")
    providers = {
        assessment.get("provider")
        for assessment in assessments
        if isinstance(assessment, dict) and isinstance(assessment.get("provider"), str)
    }
    if len(providers) != 1:
        errors.append("the run did not report exactly one resolved provider")
    provider = next(iter(providers), "")
    expected_thread_keys = {
        "cpu": {"cpu"},
        "cuda": {"cuda"},
        "directml": {"directml"},
        "hybrid-cuda-cpu": {"cpu", "cuda"},
        "hybrid-directml-cpu": {"cpu", "directml"},
    }.get(provider, set())
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

    for source_file, document in documents.items():
        assessment = assessment_by_source.get(source_file)
        if assessment is None:
            errors.append(f"row {document.row_index}: missing assessment")
            continue
        if assessment.get("status") != "processed" or assessment.get("pages") != 1:
            errors.append(f"row {document.row_index}: assessment was not single-page processed")
        if assessment.get("sourceSha256") != document.sha256:
            errors.append(f"row {document.row_index}: source hash mismatch")
        if assessment.get("modelPackSha256") != model_pack_sha256:
            errors.append(f"row {document.row_index}: model-pack hash mismatch")
        if assessment.get("provider") != provider:
            errors.append(f"row {document.row_index}: provider mismatch")
        validate_threads(assessment, f"row {document.row_index} assessment")
        validate_workers(assessment, f"row {document.row_index} assessment")
    record_ids: set[str] = set()
    for record in records:
        source_file = record.get("sourceFile")
        if source_file not in documents:
            errors.append("a string record references an unknown CORD source")
            continue
        origin = record.get("origin")
        attributes = record.get("attributes")
        if not isinstance(origin, dict) or not isinstance(attributes, dict):
            errors.append("a string record has incomplete provenance")
            continue
        if origin.get("provider") != provider or attributes.get("provider") != provider:
            errors.append("a string record changed the resolved provider")
        if attributes.get("sourceSha256") != documents[source_file].sha256:
            errors.append("a string record changed the source hash")
        if attributes.get("modelPackSha256") != model_pack_sha256:
            errors.append("a string record changed the model-pack hash")
        validate_threads(attributes, "a string record")
        validate_workers(attributes, "a string record")
        record_id = record.get("recordId")
        expected_id = "sha256:" + sha256_bytes(
            canonical_json(
                {key: value for key, value in record.items() if key != "recordId"}
            ).encode("utf-8")
        )
        if record_id != expected_id:
            errors.append("a string record identifier is not reproducible")
        if isinstance(record_id, str) and record_id in record_ids:
            errors.append("the run emitted a duplicate string record identity")
        if isinstance(record_id, str):
            record_ids.add(record_id)
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
        provider,
        resolved_thread_counts,
        resolved_worker_counts,
    )


def score_records(corpus: ExtractedCorpus, records: Sequence[dict[str, Any]]) -> dict[str, Any]:
    grouped: dict[str, list[Prediction]] = {str(document.path): [] for document in corpus.documents}
    for record in records:
        source_file = record.get("sourceFile")
        if source_file not in grouped:
            raise BenchmarkError("A record references an unknown CORD source", stage="scoring")
        grouped[source_file].append(_prediction(record))
    per_document = [
        score_document(document, grouped[str(document.path)]) for document in corpus.documents
    ]
    return aggregate_metrics(per_document)


def _assert_perfect_oracle(metrics: dict[str, Any], *, row_index: int, variant: str) -> None:
    checks = (
        metrics["detection"]["precision"] == 1.0,
        metrics["detection"]["recall"] == 1.0,
        metrics["detection"]["hmean"] == 1.0,
        metrics["endToEndExact"]["precision"] == 1.0,
        metrics["endToEndExact"]["recall"] == 1.0,
        metrics["endToEndExact"]["hmean"] == 1.0,
        metrics["tokenPrecision"] == 1.0,
        metrics["tokenRecall"] == 1.0,
        metrics["tokenF1"] == 1.0,
        metrics["charPrecision"] == 1.0,
        metrics["charRecall"] == 1.0,
        metrics["oneMinusNed"] == 1.0,
        metrics["pageCer"] == 0.0,
        metrics["pageWer"] == 0.0,
    )
    if not all(checks):
        raise BenchmarkError(
            f"CORD scorer oracle failed for row {row_index} ({variant})",
            stage="scorer-oracle",
        )


def validate_scorer_oracle(corpus: ExtractedCorpus) -> dict[str, Any]:
    variants = ("physical-row", "word-split")
    minimum_prediction_hull_member_union_coverage = 1.0
    minimum_truth_member_union_coverage = 1.0
    for document in corpus.documents:
        row_predictions = tuple(
            reversed(
                tuple(
                    Prediction(
                        row.text,
                        row.polygon,
                        1.0,
                        f"oracle-row-{document.row_index}-{row.row_id}",
                    )
                    for row in _reference_rows(document)
                )
            )
        )
        word_predictions = tuple(
            reversed(
                tuple(
                    Prediction(
                        word.text,
                        word.polygon,
                        1.0,
                        (
                            f"oracle-word-{document.row_index}-{word.row_id}-"
                            f"{word.line_index}-{word.word_index}"
                        ),
                    )
                    for word in document.words
                )
            )
        )
        _assert_perfect_oracle(
            score_document(document, row_predictions),
            row_index=document.row_index,
            variant=variants[0],
        )
        _assert_perfect_oracle(
            score_document(document, word_predictions),
            row_index=document.row_index,
            variant=variants[1],
        )
        truths = _reference_rows(document)
        prediction_rows, _ = _cluster_prediction_rows(word_predictions)
        components = _overlap_components(truths, prediction_rows)
        minimum_prediction_hull_member_union_coverage = min(
            minimum_prediction_hull_member_union_coverage,
            *(
                _covered_fraction(
                    prediction.polygon,
                    tuple(member.polygon for member in prediction.members),
                )
                for prediction in prediction_rows
            ),
        )
        for truth_index, truth in enumerate(truths):
            _, prediction_indices = next(
                component for component in components if truth_index in component[0]
            )
            member_polygons = tuple(
                member.polygon
                for prediction_index in prediction_indices
                for member in prediction_rows[prediction_index].members
            )
            minimum_truth_member_union_coverage = min(
                minimum_truth_member_union_coverage,
                _covered_fraction(truth.polygon, member_polygons),
            )
    return {
        "passed": True,
        "documents": len(corpus.documents),
        "variants": list(variants),
        "expectedAccuracyMetrics": 1.0,
        "expectedErrorMetrics": 0.0,
        "minimumWordSplitPredictionHullMemberUnionCoverage": (
            minimum_prediction_hull_member_union_coverage
        ),
        "minimumWordSplitTruthMemberUnionCoverage": minimum_truth_member_union_coverage,
    }


def _critical_record(record: dict[str, Any]) -> dict[str, Any]:
    normalized = {key: value for key, value in record.items() if key != "recordId"}
    origin = normalized.get("origin")
    if isinstance(origin, dict):
        normalized["origin"] = {key: value for key, value in origin.items() if key != "provider"}
    attributes = normalized.get("attributes")
    if isinstance(attributes, dict):
        ignored = {
            "confidence",
            "executionProvider",
            "provider",
            "requestedProvider",
            "requestedThreads",
            "resolvedThreadCounts",
            "resolvedWorkerCounts",
            "runtimeSha256",
        }
        normalized["attributes"] = {
            key: value for key, value in attributes.items() if key not in ignored
        }
    return normalized


def _critical_assessment(assessment: dict[str, Any]) -> dict[str, Any]:
    ignored = {
        "provider",
        "requestedProvider",
        "requestedThreads",
        "resolvedThreadCounts",
        "resolvedWorkerCounts",
        "runtimeSha256",
    }
    return {key: value for key, value in assessment.items() if key not in ignored}


def critical_evidence_sha256(
    records: Sequence[dict[str, Any]], assessments: Sequence[dict[str, Any]]
) -> str:
    normalized = {
        "records": sorted((_critical_record(record) for record in records), key=canonical_json),
        "assessments": sorted(
            (_critical_assessment(assessment) for assessment in assessments), key=canonical_json
        ),
    }
    return sha256_bytes(canonical_json(normalized).encode("utf-8"))


def _confidence_by_critical_record(
    records: Sequence[dict[str, Any]],
) -> dict[str, list[float]]:
    values: dict[str, list[float]] = {}
    for record in records:
        attributes = record.get("attributes")
        if not isinstance(attributes, dict):
            raise BenchmarkError("A CORD OCR record has invalid confidence", stage="parity")
        confidence = attributes.get("confidence")
        if isinstance(confidence, bool) or not isinstance(confidence, (int, float)):
            raise BenchmarkError("A CORD OCR record has invalid confidence", stage="parity")
        key = sha256_bytes(canonical_json(_critical_record(record)).encode("utf-8"))
        values.setdefault(key, []).append(float(confidence))
    return {key: sorted(confidences) for key, confidences in sorted(values.items())}


def confidence_parity(
    evidence: Sequence[dict[str, list[float]]],
    *,
    maximum_absolute_delta: float = CONFIDENCE_PARITY_MAX_ABS_DELTA,
) -> dict[str, Any]:
    if len(evidence) < 2:
        return {
            "evaluated": False,
            "passed": None,
            "maximumAbsoluteDelta": 0.0,
            "meanAbsoluteDelta": 0.0,
            "changedRecords": 0,
            "comparedRecords": 0,
            "threshold": maximum_absolute_delta,
        }
    baseline = evidence[0]
    deltas = []
    structurally_equal = True
    for candidate in evidence[1:]:
        if baseline.keys() != candidate.keys():
            structurally_equal = False
            continue
        for key, baseline_values in baseline.items():
            candidate_values = candidate[key]
            if len(baseline_values) != len(candidate_values):
                structurally_equal = False
                continue
            deltas.extend(
                abs(left - right)
                for left, right in zip(baseline_values, candidate_values, strict=True)
            )
    maximum_delta = max(deltas, default=0.0)
    return {
        "evaluated": True,
        "passed": structurally_equal and maximum_delta <= maximum_absolute_delta,
        "structurallyAligned": structurally_equal,
        "maximumAbsoluteDelta": maximum_delta,
        "meanAbsoluteDelta": statistics.fmean(deltas) if deltas else 0.0,
        "changedRecords": sum(delta > 0 for delta in deltas),
        "comparedRecords": len(deltas),
        "threshold": maximum_absolute_delta,
    }


def _quality_gate(metrics: dict[str, Any], thresholds: dict[str, float | None]) -> bool | None:
    if not any(value is not None for value in thresholds.values()):
        return None
    micro = metrics["micro"]
    checks = (
        thresholds["minimumDetectionHmean"] is None
        or micro["detection"]["hmean"] >= thresholds["minimumDetectionHmean"],
        thresholds["minimumEndToEndHmean"] is None
        or micro["endToEndExact"]["hmean"] >= thresholds["minimumEndToEndHmean"],
        thresholds["minimumWordAccuracy"] is None
        or micro["wordAccuracy"] >= thresholds["minimumWordAccuracy"],
        thresholds["maximumPageCer"] is None or micro["pageCer"] <= thresholds["maximumPageCer"],
        thresholds["maximumPageWer"] is None or micro["pageWer"] <= thresholds["maximumPageWer"],
    )
    return all(checks)


def run_backend_once(
    *,
    backend: Backend,
    worker: Path,
    model_pack: Path,
    model_id: str,
    revision: str,
    model_pack_sha256: str,
    worker_sha256: str,
    engine_version: str,
    corpus: ExtractedCorpus,
    output_root: Path,
    threads: int,
    timeout_seconds: float,
    thresholds: dict[str, float | None],
) -> dict[str, Any]:
    if sha256_file(worker) != worker_sha256:
        raise BenchmarkError(
            "The OCR worker changed before a backend run",
            backend=backend.requested_provider,
            stage="worker-identity",
        )
    output_root.mkdir(parents=True, exist_ok=True)
    strings = output_root / "strings.jsonl"
    assessments_path = output_root / "assessments.jsonl"
    arguments = [
        "--airgap",
        "--paths-from",
        str(corpus.inventory),
        "--input-manifest",
        str(corpus.worker_manifest),
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
        "--threads",
        str(threads),
        "--max-pages",
        "1",
    ]
    command = isolated_python_command(backend.python_executable, worker, arguments)
    process_cwd = output_root / "process-cwd"
    try:
        process_cwd.mkdir()
    except FileExistsError as exc:
        raise BenchmarkError(
            "The CORD OCR worker process directory already exists",
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
            stdin=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=environment,
            cwd=process_cwd,
            timeout=timeout_seconds,
        )
    except subprocess.TimeoutExpired as exc:
        raise BenchmarkError(
            "The CORD OCR worker exceeded its timeout",
            backend=backend.requested_provider,
            stage="worker",
        ) from exc
    elapsed = time.perf_counter() - started
    if completed.returncode != 0:
        raise BenchmarkError(
            "The CORD OCR worker returned a non-zero status",
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
        provider,
        resolved_thread_counts,
        resolved_worker_counts,
    ) = validate_run_provenance(
        records,
        assessments,
        corpus,
        model_pack_sha256,
        threads,
    )
    try:
        metrics = score_records(corpus, records)
    except BenchmarkError as exc:
        raise exc.with_context(backend=backend.requested_provider, stage="scoring") from exc
    except (TypeError, ValueError) as exc:
        raise BenchmarkError(
            "The CORD metrics could not be computed",
            backend=backend.requested_provider,
            stage="scoring",
        ) from exc
    per_document_path = output_root / "metrics-per-document.jsonl"
    _atomic_write(
        per_document_path,
        "".join(
            canonical_json(document_metrics) + "\n" for document_metrics in metrics["perDocument"]
        ).encode("utf-8"),
    )
    quality_gate = _quality_gate(metrics, thresholds)
    execution_provider_counts = Counter(
        str(record.get("attributes", {}).get("executionProvider", "unknown"))
        for record in records
        if isinstance(record.get("attributes"), dict)
    )
    cpu_lane_records = execution_provider_counts.get("cpu", 0)
    non_cpu_lane_records = sum(
        count
        for provider_name, count in execution_provider_counts.items()
        if provider_name != "cpu"
    )
    both_hybrid_lanes = cpu_lane_records > 0 and non_cpu_lane_records > 0
    return {
        "requestedProvider": backend.requested_provider,
        "resolvedProvider": provider,
        "requestedThreads": threads,
        "resolvedThreadCounts": resolved_thread_counts,
        "resolvedWorkerCounts": resolved_worker_counts,
        "workerSha256": worker_sha256,
        "runtimeSha256": sha256_file(backend.python_executable),
        "elapsedSeconds": elapsed,
        "documentsPerSecond": len(corpus.documents) / elapsed if elapsed else math.inf,
        "stringRecords": len(records),
        "executionProviderRecordCounts": dict(sorted(execution_provider_counts.items())),
        "hybridLaneRecordCoverage": {
            "bothLanesProducedRecords": both_hybrid_lanes,
            "cpuLaneRecords": cpu_lane_records,
            "nonCpuLaneRecords": non_cpu_lane_records,
        },
        "throughputComparable": (backend.requested_provider != "hybrid" or both_hybrid_lanes),
        "rawOutputHashes": {
            "stringsSha256": sha256_file(strings),
            "assessmentsSha256": sha256_file(assessments_path),
            "pairSha256": raw_output_pair_sha256(strings, assessments_path),
        },
        "canonicalEvidenceSha256": canonical_evidence_sha256(records, assessments),
        "criticalEvidenceSha256": critical_evidence_sha256(records, assessments),
        "_confidenceByCriticalRecord": _confidence_by_critical_record(records),
        "metricsSha256": sha256_bytes(canonical_json(metrics).encode("utf-8")),
        "perDocumentMetricsSha256": sha256_file(per_document_path),
        "provenancePassed": provenance_passed,
        "provenanceErrors": provenance_errors,
        "qualityGatePassed": quality_gate,
        "metrics": metrics,
    }


def benchmark_backend(
    *,
    backend: Backend,
    quality_corpus: ExtractedCorpus,
    determinism_corpus: ExtractedCorpus,
    determinism_repetitions: int,
    output_root: Path,
    runtime: dict[str, Any] | None = None,
    **kwargs: Any,
) -> dict[str, Any]:
    quality_run = run_backend_once(
        backend=backend,
        corpus=quality_corpus,
        output_root=output_root / "quality-all-100",
        **kwargs,
    )
    determinism_runs = [
        run_backend_once(
            backend=backend,
            corpus=determinism_corpus,
            output_root=output_root / "determinism-rows-0000-0009" / f"run-{index + 1:02d}",
            **kwargs,
        )
        for index in range(determinism_repetitions)
    ]
    runs = [quality_run, *determinism_runs]
    providers = {run["resolvedProvider"] for run in runs}
    runtime_hashes = {run["runtimeSha256"] for run in runs}
    raw_hashes = {run["rawOutputHashes"]["pairSha256"] for run in determinism_runs}
    canonical_hashes = {run["canonicalEvidenceSha256"] for run in determinism_runs}
    critical_hashes = {
        run.get("criticalEvidenceSha256", run["canonicalEvidenceSha256"])
        for run in determinism_runs
    }
    metric_hashes = {run["metricsSha256"] for run in determinism_runs}
    execution_counts = {
        canonical_json(run["executionProviderRecordCounts"]) for run in determinism_runs
    }
    resolved_thread_counts = {canonical_json(run["resolvedThreadCounts"]) for run in runs}
    resolved_worker_counts = {canonical_json(run["resolvedWorkerCounts"]) for run in runs}
    public_quality_run = {
        key: value for key, value in quality_run.items() if not key.startswith("_")
    }
    public_determinism_runs = [
        {key: value for key, value in run.items() if not key.startswith("_")}
        for run in determinism_runs
    ]
    return {
        "requestedProvider": backend.requested_provider,
        "resolvedProvider": next(iter(providers), ""),
        "requestedThreads": quality_run["requestedThreads"],
        "resolvedThreadCounts": quality_run["resolvedThreadCounts"],
        "stableResolvedThreadCounts": len(resolved_thread_counts) == 1,
        "resolvedWorkerCounts": quality_run["resolvedWorkerCounts"],
        "stableResolvedWorkerCounts": len(resolved_worker_counts) == 1,
        "workerSha256": quality_run["workerSha256"],
        "runtimeSha256": next(iter(runtime_hashes), ""),
        "runtime": runtime,
        "qualityRows": len(quality_corpus.documents),
        "qualityElapsedSeconds": quality_run["elapsedSeconds"],
        "qualityDocumentsPerSecond": quality_run["documentsPerSecond"],
        "determinismRows": len(determinism_corpus.documents),
        "determinismRepetitions": determinism_repetitions,
        "provenancePassed": all(run["provenancePassed"] for run in runs),
        "byteDeterminismEvaluated": determinism_repetitions > 1,
        "byteDeterministic": len(raw_hashes) == 1,
        "canonicalEvidenceDeterministic": len(canonical_hashes) == 1,
        "criticalEvidenceDeterministic": len(critical_hashes) == 1,
        "metricsDeterministic": len(metric_hashes) == 1,
        "stableResolvedProvider": len(providers) == 1,
        "stableRuntime": len(runtime_hashes) == 1,
        "canonicalEvidenceSha256": quality_run["canonicalEvidenceSha256"],
        "criticalEvidenceSha256": quality_run.get(
            "criticalEvidenceSha256", quality_run["canonicalEvidenceSha256"]
        ),
        "_confidenceByCriticalRecord": quality_run.get("_confidenceByCriticalRecord", {}),
        "metricsSha256": quality_run["metricsSha256"],
        "qualityGatePassed": quality_run["qualityGatePassed"],
        "executionProviderCountsStable": len(execution_counts) == 1,
        "executionProviderRecordCounts": quality_run["executionProviderRecordCounts"],
        "hybridLaneRecordCoverage": quality_run["hybridLaneRecordCoverage"],
        "throughputComparable": quality_run["throughputComparable"],
        "metrics": quality_run["metrics"],
        "qualityRun": public_quality_run,
        "determinism": {
            "selectionSha256": determinism_corpus.selection_sha256,
            "rowIndices": [document.row_index for document in determinism_corpus.documents],
            "byteDeterministic": len(raw_hashes) == 1,
            "canonicalEvidenceDeterministic": len(canonical_hashes) == 1,
            "criticalEvidenceDeterministic": len(critical_hashes) == 1,
            "metricsDeterministic": len(metric_hashes) == 1,
            "runs": public_determinism_runs,
        },
    }


def _thresholds(args: argparse.Namespace) -> dict[str, float | None]:
    return {
        "minimumDetectionHmean": args.min_detection_hmean,
        "minimumEndToEndHmean": args.min_end_to_end_hmean,
        "minimumWordAccuracy": args.min_word_accuracy,
        "maximumPageCer": args.max_page_cer,
        "maximumPageWer": args.max_page_wer,
    }


def assemble_report(
    *,
    generated_at_utc: str,
    parquet_sha256: str,
    corpus: ExtractedCorpus,
    engine: dict[str, Any],
    settings: dict[str, Any],
    thresholds: dict[str, float | None],
    backends: Sequence[dict[str, Any]],
    release_matrix: bool,
) -> dict[str, Any]:
    split = corpus.split
    split_config = _split_config(split)
    _, _, shapely_version, geos_version = _shapely_runtime()
    canonical_hashes = {
        backend["canonicalEvidenceSha256"]
        for backend in backends
        if backend["canonicalEvidenceSha256"]
    }
    critical_hashes = {
        backend.get("criticalEvidenceSha256", "")
        for backend in backends
        if backend.get("criticalEvidenceSha256")
    }
    metric_hashes = {backend["metricsSha256"] for backend in backends if backend["metricsSha256"]}
    cross_evidence = (
        all(backend["canonicalEvidenceSha256"] for backend in backends)
        and len(canonical_hashes) == 1
    )
    cross_critical_evidence = (
        all(backend.get("criticalEvidenceSha256") for backend in backends)
        and len(critical_hashes) == 1
    )
    confidence_result = confidence_parity(
        [backend.get("_confidenceByCriticalRecord", {}) for backend in backends]
    )
    cross_metrics = (
        all(backend["metricsSha256"] for backend in backends) and len(metric_hashes) == 1
    )
    quality_configured = any(value is not None for value in thresholds.values())
    quality_passed = (
        all(backend["qualityGatePassed"] is True for backend in backends)
        if quality_configured
        else None
    )
    resolved = {backend["resolvedProvider"] for backend in backends}
    release_passed = (
        len(backends) == 3
        and len(resolved) == 3
        and cross_critical_evidence
        and cross_metrics
        and confidence_result["passed"] is True
        if release_matrix
        else None
    )
    backend_integrity = all(
        backend["provenancePassed"]
        and backend["byteDeterministic"]
        and backend["canonicalEvidenceDeterministic"]
        and backend.get("criticalEvidenceDeterministic", True)
        and backend["metricsDeterministic"]
        and backend["stableResolvedProvider"]
        and backend["stableRuntime"]
        and backend["stableResolvedThreadCounts"]
        and backend["stableResolvedWorkerCounts"]
        and backend["executionProviderCountsStable"]
        and backend["throughputComparable"]
        for backend in backends
    )
    integrity_passed = (
        backend_integrity
        and (
            cross_critical_evidence and cross_metrics and confidence_result["passed"] is True
            if len(backends) > 1
            else True
        )
        and (release_passed is not False)
    )
    passed = integrity_passed and quality_passed is True
    return {
        "schemaVersion": SCHEMA_VERSION,
        "generatedAtUtc": generated_at_utc,
        "benchmark": f"CORD-v2-{split}-OCR",
        "protocol": PROTOCOL,
        "evaluationRole": "scorer-development",
        "claimScope": f"real Indonesian receipt images from the complete CORD v2 {split} split",
        "claimExclusions": (
            "not an official CORD leaderboard metric; does not establish quality on general PDFs, "
            "handwriting, arbitrary documents, or other languages"
        ),
        "host": safe_host_metadata(),
        "benchmarkDependencies": {
            "pyarrow": _distribution_version("pyarrow"),
            "shapely": shapely_version,
            "geos": geos_version,
        },
        "corpus": {
            "name": "naver-clova-ix/cord-v2",
            "split": split,
            "license": "CC-BY-4.0",
            "commit": CORD_V2_COMMIT,
            "sourceUrl": split_config["url"],
            "parquetBytes": split_config["bytes"],
            "parquetSha256": parquet_sha256,
            "rows": len(corpus.documents),
            "allRowsRequired": CORD_V2_ROWS,
            "groundTruthValidLines": sum(len(document.lines) for document in corpus.documents),
            "groundTruthWords": sum(len(document.words) for document in corpus.documents),
            "groundTruthPhysicalRows": sum(
                len(_reference_rows(document)) for document in corpus.documents
            ),
            "groundTruthRowIdGroups": sum(len(document.rows) for document in corpus.documents),
            "dontcareRegions": sum(
                len(document.dontcare_polygons) for document in corpus.documents
            ),
            "repeatingSymbolRegions": sum(
                len(document.repeating_symbol_polygons) for document in corpus.documents
            ),
            "clippedValidLines": sum(document.clipped_valid_lines for document in corpus.documents),
            "clippedDontcareRegions": sum(
                document.clipped_dontcare_regions for document in corpus.documents
            ),
            "clippedRepeatingSymbolRegions": sum(
                document.clipped_repeating_symbol_regions for document in corpus.documents
            ),
            "annotationBoundaryClipRecords": sum(
                len(document.annotation_boundary_clips) for document in corpus.documents
            ),
            "annotationBoundaryMaximumOvershootPixels": max(
                (
                    max(clip.horizontal_overshoot_pixels, clip.vertical_overshoot_pixels)
                    for document in corpus.documents
                    for clip in document.annotation_boundary_clips
                ),
                default=0.0,
            ),
            "annotationBoundaryMaximumOvershootRatio": max(
                (
                    max(clip.horizontal_overshoot_ratio, clip.vertical_overshoot_ratio)
                    for document in corpus.documents
                    for clip in document.annotation_boundary_clips
                ),
                default=0.0,
            ),
            "annotationBoundaryMinimumRetainedAreaRatio": min(
                (
                    clip.retained_area_ratio
                    for document in corpus.documents
                    for clip in document.annotation_boundary_clips
                ),
                default=1.0,
            ),
            "qualitySelectionSha256": corpus.selection_sha256,
            "corpusManifestSha256": corpus.corpus_manifest_sha256,
            "workerManifestSha256": corpus.worker_manifest_sha256,
        },
        "engine": engine,
        "settings": settings,
        "metricDefinitions": {
            "groundTruthUnit": (
                "prediction-independent rows derived within each CORD row_id; a row_id is split "
                "only when two alphanumeric, one-valid_line geometry clusters prove two baselines; "
                "whole adjacent row_id groups may merge only when they share a valid_line, do not "
                "overlap horizontally, and satisfy the reference baseline-distance bound"
            ),
            "groundTruthPolygon": (
                "convex hull of the complete row_id group or of a permitted whole split/merge; "
                "words are never reassigned between row_id groups"
            ),
            "annotationBoundaryHandling": (
                "valid_line, dontcare, and repeating_symbol polygons are derived from the four "
                "raw quad coordinates and intersected with the verified image rectangle only "
                "when each axis overshoots by no more than both 24 pixels and 5 percent and the "
                "intersection retains at least 75 percent of the original area; every clip is "
                "recorded and all other boundary discrepancies fail validation"
            ),
            "predictionRows": (
                "prediction-only slope candidates (zero, +/-0.02, polygon-slope median and "
                "quartiles) ranked by total deskewed row thickness, then normalized-baseline "
                "clustering and x order; irreducible geometry ties preserve raw output order "
                "and never use recognized text or record identifiers"
            ),
            "matching": (
                "deterministic spatial components linked by row or raw-fragment overlap; exact "
                "Shapely polygon-union coverage is measured in both directions. Prediction "
                "cluster hulls are used only for component connectivity, never as evidence of "
                "covered ink. Exact prediction-member unions must cover either the reference-row "
                "polygon or its exact CORD-word union in reverse; forward truth recall must cover "
                "the exact CORD-word union whenever words exist (the row polygon is only a "
                "no-word fallback)"
            ),
            "minimumSegmentationOverlap": SEGMENTATION_OVERLAP_THRESHOLD,
            "segmentationOverlap": "intersection area / smaller polygon area",
            "exactText": "case-sensitive Unicode NFC with whitespace collapsed between fragments",
            "componentTextOrder": (
                "reference row order is frozen without prediction geometry; raw prediction "
                "fragments are spatially assigned to frozen rows and ordered by independently "
                "deskewed physical subrows"
            ),
            "wordAccuracy": "compatibility alias for order-invariant exact-token F1",
            "tokenPrecisionRecallF1": (
                "pagewise Counter intersection over exact whitespace tokens; order invariant"
            ),
            "charPrecisionRecall": (
                "spatial-component LCS characters divided by predicted/reference characters"
            ),
            "oneMinusNed": (
                "1 - summed spatial-component character edit distance / max(reference,predicted)"
            ),
            "pageCerWer": (
                "summed spatial-component edit distance / reference units; unmatched output is "
                "retained"
            ),
            "ignoredPrediction": (
                "prediction overlap / prediction area > 0.5 with a CORD dontcare or "
                "repeating_symbol exact polygon union, unless it also substantially overlaps "
                "valid text; disjoint annotation quads are never convex-hulled"
            ),
            "roiHandling": (
                "predictions with at most 50 percent of their area in the CORD receipt ROI are "
                "excluded from annotated-receipt scoring and counted unless they substantially "
                "overlap valid text"
            ),
            "repeatingSymbolHandling": (
                "validated CORD cut-line symbols are not valid lines and substantially "
                "overlapping predictions are ignored"
            ),
        },
        "protocolConstants": {
            "segmentationOverlapThreshold": SEGMENTATION_OVERLAP_THRESHOLD,
            "ignorePrecisionThreshold": IGNORE_PRECISION_THRESHOLD,
            "predictionRowNormalizedDistance": ROW_CLUSTER_NORMALIZED_DISTANCE,
            "referenceSplitNormalizedDistance": REFERENCE_ROW_SPLIT_NORMALIZED_DISTANCE,
            "referenceMergeNormalizedDistance": REFERENCE_ROW_MERGE_NORMALIZED_DISTANCE,
            "confidenceParityMaximumAbsoluteDelta": CONFIDENCE_PARITY_MAX_ABS_DELTA,
            "annotationBoundaryMaximumOvershootPixels": (
                ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_PIXELS
            ),
            "annotationBoundaryMaximumOvershootRatio": (
                ANNOTATION_BOUNDARY_MAXIMUM_OVERSHOOT_RATIO
            ),
            "annotationBoundaryMinimumRetainedAreaRatio": (
                ANNOTATION_BOUNDARY_MINIMUM_RETAINED_AREA_RATIO
            ),
            "shapelyVersion": SHAPELY_VERSION,
        },
        "qualityGate": {
            "configured": quality_configured,
            "thresholds": thresholds,
            "passed": quality_passed,
        },
        "parity": {
            "crossBackendCanonicalEvidence": cross_evidence,
            "canonicalEvidenceSha256": (next(iter(canonical_hashes), "") if cross_evidence else ""),
            "crossBackendCriticalEvidence": cross_critical_evidence,
            "criticalEvidenceSha256": (
                next(iter(critical_hashes), "") if cross_critical_evidence else ""
            ),
            "criticalEvidenceDefinition": (
                "exact records and assessments excluding provider, runtime, threads, recordId, "
                "and confidence"
            ),
            "confidence": confidence_result,
            "crossBackendMetrics": cross_metrics,
            "metricsSha256": next(iter(metric_hashes), "") if cross_metrics else "",
        },
        "releaseMatrix": {
            "enabled": release_matrix,
            "passed": release_passed,
        },
        "backends": [
            {key: value for key, value in backend.items() if not key.startswith("_")}
            for backend in backends
        ],
        "integrityPassed": integrity_passed,
        "passed": passed,
    }


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Benchmark offline OCR on a complete pinned CORD v2 split."
    )
    parser.add_argument("--cord-parquet", type=Path, required=True)
    parser.add_argument("--cord-split", choices=tuple(CORD_V2_FILES), default="test")
    parser.add_argument("--worker", type=Path, required=True)
    parser.add_argument("--model-pack", type=Path, required=True)
    parser.add_argument("--backend", action="append", type=parse_backend, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--work-directory", type=Path, required=True)
    parser.add_argument("--engine-version", default="3.9.2")
    parser.add_argument("--determinism-repetitions", type=int, default=2)
    parser.add_argument(
        "--threads",
        type=int,
        default=0,
        help="OCR worker threads; 0 uses the bounded provider-aware production policy",
    )
    parser.add_argument("--timeout-seconds", type=float, default=7200.0)
    parser.add_argument("--release-matrix", action="store_true")
    parser.add_argument("--min-detection-hmean", type=float)
    parser.add_argument("--min-end-to-end-hmean", type=float)
    parser.add_argument("--min-word-accuracy", type=float)
    parser.add_argument("--max-page-cer", type=float)
    parser.add_argument("--max-page-wer", type=float)
    parser.add_argument("--require-pass", action="store_true")
    return parser.parse_args(argv)


def _validate_arguments(args: argparse.Namespace) -> tuple[Path, Path, Path, Path, Path]:
    parquet_path = args.cord_parquet.expanduser().resolve()
    worker = args.worker.expanduser().resolve()
    model_pack = args.model_pack.expanduser().resolve()
    output = args.output.expanduser().resolve()
    work_root = args.work_directory.expanduser().resolve()
    if not parquet_path.is_file() or not worker.is_file() or not model_pack.is_file():
        raise BenchmarkError(
            "The CORD parquet, worker, and model pack must be existing local files",
            stage="arguments",
        )
    if not 2 <= args.determinism_repetitions <= 10:
        raise BenchmarkError("Determinism repetitions must be between 2 and 10", stage="arguments")
    if not 0 <= args.threads <= 256:
        raise BenchmarkError("Threads must be between 0 and 256", stage="arguments")
    if args.timeout_seconds <= 0 or not math.isfinite(args.timeout_seconds):
        raise BenchmarkError("The timeout must be positive and finite", stage="arguments")
    providers = [backend.requested_provider for backend in args.backend]
    if len(providers) != len(set(providers)):
        raise BenchmarkError("Each requested provider may appear only once", stage="arguments")
    for backend in args.backend:
        if not backend.python_executable.is_file():
            raise BenchmarkError(
                "A benchmark backend executable is unavailable",
                backend=backend.requested_provider,
                stage="arguments",
            )
    if args.release_matrix:
        validate_release_matrix(args.backend)
    for name, value in _thresholds(args).items():
        if value is None:
            continue
        if not math.isfinite(value) or value < 0:
            raise BenchmarkError(f"The {name} threshold is invalid", stage="arguments")
        if name.startswith("minimum") and value > 1:
            raise BenchmarkError(f"The {name} threshold must be in 0..1", stage="arguments")
    return parquet_path, worker, model_pack, output, work_root


def run(args: argparse.Namespace) -> dict[str, Any]:
    output_hint = args.output.expanduser().resolve()
    report_marker = begin_benchmark_report(output_hint)
    parquet_path, worker, model_pack, output, work_root = _validate_arguments(args)
    benchmark_script = Path(__file__).resolve()
    worker_sha256 = sha256_file(worker)
    benchmark_script_sha256 = sha256_file(benchmark_script)
    parquet_sha256 = verify_cord_parquet(parquet_path, args.cord_split)
    model_id, revision, model_pack_sha256 = _load_model_identity(model_pack)
    work_root.mkdir(parents=True, exist_ok=True)
    corpus = extract_cord_corpus(parquet_path, work_root / "corpus", split=args.cord_split)
    scorer_oracle = validate_scorer_oracle(corpus)
    determinism_corpus = create_corpus_view(
        corpus,
        DETERMINISM_ROW_INDICES,
        work_root / "corpus" / "views",
        name="determinism-rows-0000-0009",
    )
    thresholds = _thresholds(args)
    runtimes = {
        backend.requested_provider: runtime_metadata(
            backend, timeout_seconds=min(args.timeout_seconds, 30.0)
        )
        for backend in args.backend
    }
    common = {
        "worker": worker,
        "model_pack": model_pack,
        "model_id": model_id,
        "revision": revision,
        "model_pack_sha256": model_pack_sha256,
        "worker_sha256": worker_sha256,
        "engine_version": args.engine_version,
        "threads": args.threads,
        "timeout_seconds": args.timeout_seconds,
        "thresholds": thresholds,
    }
    backends = [
        benchmark_backend(
            backend=backend,
            quality_corpus=corpus,
            determinism_corpus=determinism_corpus,
            determinism_repetitions=args.determinism_repetitions,
            output_root=work_root / "results" / backend.requested_provider,
            runtime=runtimes[backend.requested_provider],
            **common,
        )
        for backend in args.backend
    ]
    if sha256_file(worker) != worker_sha256:
        raise BenchmarkError("The OCR worker changed during the benchmark", stage="worker-identity")
    if sha256_file(benchmark_script) != benchmark_script_sha256:
        raise BenchmarkError(
            "The CORD benchmark script changed during the benchmark",
            stage="benchmark-identity",
        )
    report = assemble_report(
        generated_at_utc=utc_timestamp(),
        parquet_sha256=parquet_sha256,
        corpus=corpus,
        engine={
            "name": "rapidocr",
            "version": args.engine_version,
            "modelId": model_id,
            "modelRevision": revision,
            "modelPackSha256": model_pack_sha256,
            "workerSha256": worker_sha256,
            "benchmarkScriptSha256": benchmark_script_sha256,
        },
        settings={
            "split": args.cord_split,
            "scorerOracle": scorer_oracle,
            "threads": args.threads,
            "threadPolicy": "provider-aware-auto" if args.threads == 0 else "fixed",
            "qualityRepetitions": 1,
            "determinismRepetitions": args.determinism_repetitions,
            "determinismRows": list(DETERMINISM_ROW_INDICES),
            "determinismSelectionSha256": determinism_corpus.selection_sha256,
            "releaseMatrix": args.release_matrix,
        },
        thresholds=thresholds,
        backends=backends,
        release_matrix=args.release_matrix,
    )
    publish_benchmark_report(output, report_marker, report)
    return report


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
        print("OCR CORD benchmark failed; no partial result was accepted.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
