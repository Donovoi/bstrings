#!/usr/bin/env python3
"""Freeze the image-only CORD v2 train calibration/confirmatory selection."""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
from collections import Counter
from collections.abc import Sequence
from pathlib import Path
from typing import Any

DATASET_ID = "naver-clova-ix/cord-v2"
DATASET_REVISION = "7f0115a4b758a71d6473b8d085751692da2fef98"
PYARROW_VERSION = "25.0.0"
TRAIN_ROWS = 800
UNIQUE_IMAGE_HASHES = 798
CONFIRMATORY_ROWS = 200
SHARDS = (
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
EXPECTED_SELECTION_HASHES = {
    "complete": "b7cd1b3b9ee19a6c617adffcec4cfb3e5e0a3567e1e08b377ea525f0dd012a8c",
    "calibration": "ece7bf666fccc109f0d65449bcd0f6fdbdb9b8f326df9dc07418553025de85e9",
    "confirmatory": "c1415ecc2cd56cff29b9896edcf753200de16c256f41f62edcead0a1236579b0",
}


class SelectionError(RuntimeError):
    """The pinned corpus or generated selection violated its frozen contract."""


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(4 * 1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def selection_hash(entries: Sequence[dict[str, Any]]) -> str:
    return sha256_bytes(canonical_json(list(entries)).encode("utf-8"))


def assign_roles(
    entries: Sequence[dict[str, Any]],
    *,
    expected_rows: int = TRAIN_ROWS,
    expected_unique_hashes: int = UNIQUE_IMAGE_HASHES,
    confirmatory_rows: int = CONFIRMATORY_ROWS,
) -> tuple[list[dict[str, Any]], list[dict[str, Any]], list[dict[str, Any]]]:
    assigned = [dict(entry) for entry in entries]
    counts = Counter(entry["imageSha256"] for entry in assigned)
    if len(assigned) != expected_rows or len(counts) != expected_unique_hashes:
        raise SelectionError("Pinned train image identity cardinality changed")

    confirmatory_hashes: set[str] = set()
    selected_rows = 0
    for image_sha256 in sorted(counts):
        confirmatory_hashes.add(image_sha256)
        selected_rows += counts[image_sha256]
        if selected_rows >= confirmatory_rows:
            break
    if selected_rows != confirmatory_rows:
        raise SelectionError("The frozen grouping rule no longer selects its exact row count")

    for entry in assigned:
        entry["role"] = (
            "confirmatory" if entry["imageSha256"] in confirmatory_hashes else "calibration"
        )
    calibration = [entry for entry in assigned if entry["role"] == "calibration"]
    confirmatory = [entry for entry in assigned if entry["role"] == "confirmatory"]
    return assigned, calibration, confirmatory


def _validate_shards(paths: Sequence[Path]) -> tuple[Path, ...]:
    try:
        installed_pyarrow = importlib.metadata.version("pyarrow")
    except importlib.metadata.PackageNotFoundError as exc:
        raise SelectionError(f"PyArrow {PYARROW_VERSION} is required") from exc
    if installed_pyarrow != PYARROW_VERSION:
        raise SelectionError(f"PyArrow {PYARROW_VERSION} is required")
    if len(paths) != len(SHARDS):
        raise SelectionError(f"Exactly {len(SHARDS)} ordered train shards are required")
    resolved = tuple(path.expanduser().resolve() for path in paths)
    for path, (expected_name, expected_bytes, expected_sha256) in zip(
        resolved, SHARDS, strict=True
    ):
        if not path.is_file() or path.name != expected_name:
            raise SelectionError(f"The expected pinned shard is unavailable: {expected_name}")
        if path.stat().st_size != expected_bytes or sha256_file(path) != expected_sha256:
            raise SelectionError(f"Pinned shard identity mismatch: {expected_name}")
    return resolved


def build_manifest(paths: Sequence[Path]) -> dict[str, Any]:
    import pyarrow.parquet as pq

    resolved = _validate_shards(paths)
    entries: list[dict[str, Any]] = []
    global_index = 0
    for shard_ordinal, path in enumerate(resolved):
        parquet = pq.ParquetFile(path)
        if parquet.metadata.num_rows != 200:
            raise SelectionError(f"Pinned shard row count mismatch: {path.name}")
        table = pq.read_table(path, columns=["image"])
        images = table.column("image").combine_chunks()
        try:
            image_bytes = images.field("bytes")
        except (KeyError, TypeError) as exc:
            raise SelectionError("The pinned image.bytes field is unavailable") from exc
        if len(image_bytes) != 200 or image_bytes.null_count:
            raise SelectionError(f"Pinned image column is incomplete: {path.name}")
        for row_index, value in enumerate(image_bytes):
            raw = value.as_py()
            if not isinstance(raw, bytes) or not raw:
                raise SelectionError(f"Pinned image bytes are invalid: {path.name}:{row_index}")
            entries.append(
                {
                    "globalIndex": global_index,
                    "imageSha256": sha256_bytes(raw),
                    "rowIndex": row_index,
                    "shardOrdinal": shard_ordinal,
                }
            )
            global_index += 1

    entries, calibration, confirmatory = assign_roles(entries)
    actual_hashes = {
        "complete": selection_hash(entries),
        "calibration": selection_hash(calibration),
        "confirmatory": selection_hash(confirmatory),
    }
    if actual_hashes != EXPECTED_SELECTION_HASHES:
        raise SelectionError("The generated selection does not match the pre-registered hashes")
    # Reject a shard changed between its initial identity check and image extraction.
    _validate_shards(resolved)

    return {
        "algorithm": {
            "confirmatoryRows": CONFIRMATORY_ROWS,
            "description": (
                "group identical embedded-image SHA-256 values, sort unique hashes "
                "lexicographically, and select the shortest prefix totaling at least 200 rows"
            ),
            "duplicateGroupsMayNotCrossRoles": True,
            "requiredSelectedRows": CONFIRMATORY_ROWS,
        },
        "dataset": {
            "id": DATASET_ID,
            "revision": DATASET_REVISION,
            "rows": TRAIN_ROWS,
            "shards": [
                {"bytes": length, "fileName": name, "sha256": sha256}
                for name, length, sha256 in SHARDS
            ],
            "split": "train",
            "uniqueImageSha256": UNIQUE_IMAGE_HASHES,
        },
        "entries": entries,
        "entriesSha256": actual_hashes,
        "schemaVersion": 1,
    }


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--parquet", action="append", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--verify", action="store_true")
    return parser.parse_args(argv)


def run(args: argparse.Namespace) -> dict[str, Any]:
    manifest = build_manifest(args.parquet)
    output = args.output.expanduser().resolve()
    encoded = (canonical_json(manifest) + "\n").encode("utf-8")
    if args.verify:
        if not output.is_file() or output.read_bytes() != encoded:
            raise SelectionError("The frozen selection manifest does not match the pinned corpus")
    else:
        marker = output.with_name(output.name + ".incomplete")
        if output.exists() or marker.exists():
            raise SelectionError("The selection output or incomplete marker already exists")
        output.parent.mkdir(parents=True, exist_ok=True)
        with marker.open("xb") as handle:
            handle.write(encoded)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(marker, output)
    return {
        "entriesSha256": manifest["entriesSha256"],
        "manifestBytes": len(encoded),
        "manifestSha256": sha256_bytes(encoded),
        "status": "verified" if args.verify else "created",
    }


def main(argv: Sequence[str] | None = None) -> int:
    try:
        print(canonical_json(run(parse_arguments(argv))))
        return 0
    except (OSError, SelectionError, ValueError) as exc:
        print(f"CORD selection failed: {exc}", file=os.sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
