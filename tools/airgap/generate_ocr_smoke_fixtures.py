#!/usr/bin/env python3
"""Create attribution-safe image and scanned-PDF fixtures for offline OCR smoke tests."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any

SCHEMA_VERSION = 1
LINES = (
    "analyst@example.com",
    "https://example.org/case?id=42",
    "192.0.2.42 CVE-2026-12345",
    r"C:\Evidence\Case-17\memory.raw",
)
MARKERS = (
    "analyst@example.com",
    "https://example.org/case?id=42",
    "192.0.2.42",
    "CVE-2026-12345",
    r"C:\Evidence\Case-17\memory.raw",
)


class FixtureError(RuntimeError):
    """Raised when deterministic fixture generation cannot complete."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(4 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def canonical_json(value: Any) -> bytes:
    serialized = json.dumps(
        value,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    )
    return (serialized + "\n").encode()


def build(output: Path) -> None:
    if output.exists():
        raise FixtureError(f"Fixture destination must not already exist: {output}")
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError as exc:
        raise FixtureError("Pillow is required to generate OCR smoke fixtures") from exc

    output.parent.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix="bstrings-ocr-fixtures-", dir=output.parent))
    try:
        # Pillow's bundled default font keeps generation independent of host fonts.
        font = ImageFont.load_default(size=52)
        image = Image.new("RGB", (2600, 700), color="white")
        drawing = ImageDraw.Draw(image)
        for index, line in enumerate(LINES):
            drawing.text((64, 54 + index * 146), line, fill="black", font=font)

        png = temporary / "synthetic-identifiers.png"
        pdf = temporary / "synthetic-scanned-identifiers.pdf"
        image.save(png, format="PNG", optimize=False)
        image.save(pdf, format="PDF", resolution=300.0)
        image.close()

        files = [
            {
                "path": item.name,
                "bytes": item.stat().st_size,
                "sha256": sha256_file(item),
            }
            for item in (png, pdf)
        ]
        manifest = {
            "schemaVersion": SCHEMA_VERSION,
            "classification": "synthetic",
            "expectedLines": list(LINES),
            "expectedMarkers": list(MARKERS),
            "files": files,
        }
        (temporary / "expected.json").write_bytes(canonical_json(manifest))
        os.replace(temporary, output)
    except BaseException:
        shutil.rmtree(temporary, ignore_errors=True)
        raise
    print(
        json.dumps(
            {
                "schemaVersion": SCHEMA_VERSION,
                "status": "ok",
                "output": str(output),
            },
            separators=(",", ":"),
            sort_keys=True,
        )
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        build(args.output.expanduser().resolve())
        return 0
    except (FixtureError, OSError, ValueError) as exc:
        print(f"OCR fixture generation failed: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
