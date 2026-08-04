#!/usr/bin/env python3
"""Create or verify a strict SHA-256 manifest for a bstrings air-gap bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import stat
import sys
from collections.abc import Iterable
from pathlib import Path, PurePosixPath
from typing import Any

SCHEMA_VERSION = 1


class ManifestError(RuntimeError):
    """Raised when a bundle cannot be represented or verified safely."""


def hash_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def is_reparse_point(path: Path) -> bool:
    metadata = path.lstat()
    attributes = getattr(metadata, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    return path.is_symlink() or bool(attributes & reparse_flag)


def iter_bundle_files(root: Path, excluded: set[Path]) -> Iterable[Path]:
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        for directory in directories:
            candidate = current_path / directory
            if is_reparse_point(candidate):
                raise ManifestError(
                    f"Bundle contains a directory link or reparse point: {candidate}"
                )
        for filename in files:
            candidate = current_path / filename
            if is_reparse_point(candidate):
                raise ManifestError(
                    f"Bundle contains a file link or reparse point: {candidate}"
                )
            if candidate.absolute() in excluded:
                continue
            if not candidate.is_file():
                raise ManifestError(f"Bundle entry is not a regular file: {candidate}")
            yield candidate


def normalized_relative_path(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def create_manifest(root: Path, manifest_path: Path) -> dict[str, Any]:
    root = root.resolve(strict=True)
    manifest_path = manifest_path.resolve()
    if manifest_path.parent != root:
        raise ManifestError("The air-gap manifest must be written at the bundle root")
    entries = []
    for path in sorted(
        iter_bundle_files(root, {manifest_path}),
        key=lambda item: normalized_relative_path(root, item),
    ):
        entries.append(
            {
                "path": normalized_relative_path(root, path),
                "bytes": path.stat().st_size,
                "sha256": hash_file(path),
            }
        )
    manifest = {"schemaVersion": SCHEMA_VERSION, "files": entries}
    temporary = manifest_path.with_suffix(manifest_path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    temporary.replace(manifest_path)
    return manifest


def validate_relative_path(value: object) -> str:
    if not isinstance(value, str) or not value:
        raise ManifestError("Manifest file paths must be non-empty strings")
    pure = PurePosixPath(value)
    if pure.is_absolute() or ".." in pure.parts or "\\" in value:
        raise ManifestError(f"Manifest contains an unsafe relative path: {value!r}")
    normalized = pure.as_posix()
    if normalized != value or normalized in {".", "airgap-manifest.json"}:
        raise ManifestError(f"Manifest contains a non-canonical path: {value!r}")
    return normalized


def verify_manifest(root: Path, manifest_path: Path) -> dict[str, int]:
    root = root.resolve(strict=True)
    manifest_path = manifest_path.resolve(strict=True)
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        raise ManifestError(f"Could not read the air-gap manifest: {exc}") from exc
    if (
        not isinstance(manifest, dict)
        or manifest.get("schemaVersion") != SCHEMA_VERSION
    ):
        raise ManifestError("Unsupported or missing air-gap manifest schema")
    raw_entries = manifest.get("files")
    if not isinstance(raw_entries, list) or not raw_entries:
        raise ManifestError("The air-gap manifest contains no files")

    expected: dict[str, tuple[int, str]] = {}
    for entry in raw_entries:
        if not isinstance(entry, dict):
            raise ManifestError("Manifest file entries must be objects")
        relative = validate_relative_path(entry.get("path"))
        size = entry.get("bytes")
        digest = entry.get("sha256")
        if relative in expected:
            raise ManifestError(f"Manifest contains a duplicate path: {relative}")
        if not isinstance(size, int) or size < 0:
            raise ManifestError(f"Manifest contains an invalid size for {relative}")
        if (
            not isinstance(digest, str)
            or len(digest) != 64
            or any(character not in "0123456789abcdef" for character in digest)
        ):
            raise ManifestError(f"Manifest contains an invalid SHA-256 for {relative}")
        expected[relative] = (size, digest)

    actual_paths = {
        normalized_relative_path(root, path): path
        for path in iter_bundle_files(root, {manifest_path})
    }
    missing = sorted(set(expected) - set(actual_paths))
    unexpected = sorted(set(actual_paths) - set(expected))
    if missing or unexpected:
        raise ManifestError(
            f"Bundle file set differs from its manifest; missing={missing[:5]}, "
            f"unexpected={unexpected[:5]}"
        )

    total_bytes = 0
    for relative in sorted(expected):
        size, digest = expected[relative]
        path = actual_paths[relative]
        actual_size = path.stat().st_size
        if actual_size != size:
            raise ManifestError(
                f"Bundle file size mismatch for {relative}: expected {size}, got {actual_size}"
            )
        actual_digest = hash_file(path)
        if actual_digest != digest:
            raise ManifestError(
                f"Bundle SHA-256 mismatch for {relative}: expected {digest}, got {actual_digest}"
            )
        total_bytes += actual_size
    return {"files": len(expected), "bytes": total_bytes}


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    for command in ("create", "verify"):
        child = subparsers.add_parser(command)
        child.add_argument("--root", required=True, type=Path)
        child.add_argument("--manifest", required=True, type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_arguments()
    try:
        if args.command == "create":
            manifest = create_manifest(args.root, args.manifest)
            result = {
                "status": "created",
                "files": len(manifest["files"]),
                "bytes": sum(entry["bytes"] for entry in manifest["files"]),
            }
        else:
            result = {"status": "verified", **verify_manifest(args.root, args.manifest)}
        print(json.dumps(result, sort_keys=True))
        return 0
    except (ManifestError, OSError) as exc:
        print(f"air-gap manifest error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
