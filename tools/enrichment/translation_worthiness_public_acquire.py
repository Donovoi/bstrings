#!/usr/bin/env python3
"""Build an external, unlabeled dual-review queue from pinned public sources.

This command has no download path. A caller must separately provide the exact
cached files named by the committed manifest. Every source and license is
verified before any queue artifact is published.
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
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, BinaryIO

SCHEMA_VERSION = 1
MANIFEST_ID = "translation-worthiness-public-sources-v1"
DEFAULT_SOURCE_MANIFEST = Path(__file__).with_name("translation_worthiness_public_sources_v1.json")
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]

MAX_MANIFEST_BYTES = 256 * 1024
MAX_SOURCE_BYTES = 1024 * 1024
MAX_LICENSE_BYTES = 128 * 1024
MAX_TEXT_CODE_POINTS = 2_048
MAX_RECORDS_PER_SOURCE = 20_000
MAX_SOURCES = 64

SOURCE_KINDS = ("code", "config", "log")
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
LICENSE_IDS = ("Apache-2.0", "MIT")
ASSET_PROVENANCE = (
    "upstream-repository-first-party-example-path",
    "upstream-repository-first-party-source-path",
)

IDENTIFIER_PATTERN = re.compile(r"^[a-z0-9][a-z0-9-]{0,63}$", re.ASCII)
PROJECT_PATTERN = re.compile(r"^[A-Za-z0-9_.-]{1,64}/[A-Za-z0-9_.-]{1,100}$", re.ASCII)
REVISION_PATTERN = re.compile(r"^[0-9a-f]{40}$", re.ASCII)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$", re.ASCII)

MANIFEST_FIELDS = frozenset(
    {
        "schemaVersion",
        "manifestId",
        "labelPolicy",
        "networkAccess",
        "researchOnly",
        "promotionEligible",
        "sources",
    }
)
SOURCE_FIELDS = frozenset(
    {
        "sourceId",
        "sourceProject",
        "sourceRevision",
        "sourcePath",
        "sourceUrl",
        "cacheRelativePath",
        "bytes",
        "sha256",
        "sourceKind",
        "originFamily",
        "assetProvenance",
        "license",
        "licensingLimit",
    }
)
LICENSE_FIELDS = frozenset(
    {
        "id",
        "sourcePath",
        "sourceUrl",
        "cacheRelativePath",
        "bytes",
        "sha256",
    }
)

OUTPUT_QUEUE = "annotation-queue.jsonl"
OUTPUT_GROUPS = "groups.json"
OUTPUT_SOURCES = "sources.json"
OUTPUT_REPORT = "report.json"


class PublicAcquisitionError(RuntimeError):
    """Raised when public-source provenance or filesystem safety fails."""


@dataclass(frozen=True)
class FilePin:
    source_path: str
    source_url: str
    cache_relative_path: str
    byte_count: int
    sha256: str


@dataclass(frozen=True)
class LicensePin(FilePin):
    identifier: str


@dataclass(frozen=True)
class SourcePin(FilePin):
    source_id: str
    source_project: str
    source_revision: str
    source_kind: str
    origin_family: str
    asset_provenance: str
    licensing_limit: str
    license: LicensePin


@dataclass(frozen=True)
class SourceManifest:
    sources: tuple[SourcePin, ...]


@dataclass(frozen=True)
class ArtifactIdentity:
    name: str
    byte_count: int
    sha256: str


def _reject_json_constant(value: str) -> None:
    del value
    raise PublicAcquisitionError("JSON input contains a non-finite numeric constant")


def _unique_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise PublicAcquisitionError("JSON input contains a duplicate object property")
        value[key] = item
    return value


def _strict_json(raw: bytes, *, name: str) -> Any:
    try:
        return json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_json_constant,
        )
    except PublicAcquisitionError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise PublicAcquisitionError(f"{name} is not strict UTF-8 JSON") from exc


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def _exact_object(value: Any, fields: frozenset[str], *, name: str) -> Mapping[str, Any]:
    if not isinstance(value, dict) or frozenset(value) != fields:
        raise PublicAcquisitionError(f"{name} violates its schema-1 property allowlist")
    return value


def _safe_relative(value: Any, *, name: str) -> str:
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        raise PublicAcquisitionError(f"A public source has an invalid {name}")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in path.parts):
        raise PublicAcquisitionError(f"A public source has an unsafe {name}")
    return value


def _positive_integer(value: Any, *, name: str, maximum: int) -> int:
    if type(value) is not int or not 1 <= value <= maximum:
        raise PublicAcquisitionError(f"A public source has an invalid {name}")
    return value


def _file_pin(
    value: Mapping[str, Any],
    *,
    source_project: str,
    source_revision: str,
    source_id: str,
    is_license: bool,
) -> FilePin:
    source_path = _safe_relative(value["sourcePath"], name="upstream path")
    cache_path = _safe_relative(value["cacheRelativePath"], name="cache path")
    expected_cache = f"{source_id}/{'LICENSE' if is_license else 'content.txt'}"
    if cache_path != expected_cache:
        raise PublicAcquisitionError("A public source cache path is not canonical")
    source_url = value["sourceUrl"]
    expected_url = (
        f"https://raw.githubusercontent.com/{source_project}/{source_revision}/{source_path}"
    )
    if source_url != expected_url:
        raise PublicAcquisitionError("A public source URL is not pinned to its exact revision")
    digest = value["sha256"]
    if not isinstance(digest, str) or SHA256_PATTERN.fullmatch(digest) is None:
        raise PublicAcquisitionError("A public source SHA-256 pin is invalid")
    return FilePin(
        source_path=source_path,
        source_url=source_url,
        cache_relative_path=cache_path,
        byte_count=_positive_integer(
            value["bytes"],
            name="byte-length pin",
            maximum=MAX_LICENSE_BYTES if is_license else MAX_SOURCE_BYTES,
        ),
        sha256=digest,
    )


def load_source_manifest(path: Path = DEFAULT_SOURCE_MANIFEST) -> SourceManifest:
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise PublicAcquisitionError("The public-source manifest could not be read") from exc
    if not raw or len(raw) > MAX_MANIFEST_BYTES:
        raise PublicAcquisitionError("The public-source manifest is empty or over length")
    value = _exact_object(
        _strict_json(raw, name="public-source manifest"),
        MANIFEST_FIELDS,
        name="public-source manifest",
    )
    if (
        type(value["schemaVersion"]) is not int
        or value["schemaVersion"] != SCHEMA_VERSION
        or value["manifestId"] != MANIFEST_ID
        or value["labelPolicy"] != "UNLABELED-dual-independent-review-only"
        or value["networkAccess"] != "forbidden"
        or value["researchOnly"] is not True
        or value["promotionEligible"] is not False
    ):
        raise PublicAcquisitionError("The public-source manifest identity is unsupported")
    source_values = value["sources"]
    if not isinstance(source_values, list) or not 1 <= len(source_values) <= MAX_SOURCES:
        raise PublicAcquisitionError("The public-source manifest has an invalid source count")

    sources: list[SourcePin] = []
    for source_value in source_values:
        source = _exact_object(source_value, SOURCE_FIELDS, name="public source")
        source_id = source["sourceId"]
        project = source["sourceProject"]
        revision = source["sourceRevision"]
        if not isinstance(source_id, str) or IDENTIFIER_PATTERN.fullmatch(source_id) is None:
            raise PublicAcquisitionError("A public source id is invalid")
        if not isinstance(project, str) or PROJECT_PATTERN.fullmatch(project) is None:
            raise PublicAcquisitionError("A public source project is invalid")
        if not isinstance(revision, str) or REVISION_PATTERN.fullmatch(revision) is None:
            raise PublicAcquisitionError("A public source revision is not an immutable commit")
        file_pin = _file_pin(
            source,
            source_project=project,
            source_revision=revision,
            source_id=source_id,
            is_license=False,
        )
        license_value = _exact_object(source["license"], LICENSE_FIELDS, name="source license")
        license_id = license_value["id"]
        if license_id not in LICENSE_IDS:
            raise PublicAcquisitionError("A public source license is not allowlisted")
        license_file = _file_pin(
            license_value,
            source_project=project,
            source_revision=revision,
            source_id=source_id,
            is_license=True,
        )
        source_kind = source["sourceKind"]
        origin_family = source["originFamily"]
        asset_provenance = source["assetProvenance"]
        limit = source["licensingLimit"]
        if source_kind not in SOURCE_KINDS or origin_family not in ORIGIN_FAMILIES:
            raise PublicAcquisitionError("A public source kind or origin is unsupported")
        if asset_provenance not in ASSET_PROVENANCE:
            raise PublicAcquisitionError("A public source provenance statement is unsupported")
        if not isinstance(limit, str) or not 20 <= len(limit) <= 600 or "\n" in limit:
            raise PublicAcquisitionError("A public source licensing limit is invalid")
        top_level = file_pin.source_path.split("/", 1)[0].lower()
        if top_level in {"deps", "external", "lib", "third_party", "vendor"}:
            raise PublicAcquisitionError("A vendored public source path is not eligible")
        sources.append(
            SourcePin(
                **file_pin.__dict__,
                source_id=source_id,
                source_project=project,
                source_revision=revision,
                source_kind=source_kind,
                origin_family=origin_family,
                asset_provenance=asset_provenance,
                licensing_limit=limit,
                license=LicensePin(**license_file.__dict__, identifier=license_id),
            )
        )
    if [source.source_id for source in sources] != sorted(
        source.source_id for source in sources
    ) or len({source.source_id for source in sources}) != len(sources):
        raise PublicAcquisitionError("Public sources must have sorted unique source ids")
    if len({source.source_project for source in sources}) != len(sources):
        raise PublicAcquisitionError("Public sources must come from independent projects")
    return SourceManifest(sources=tuple(sources))


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
            raise PublicAcquisitionError(f"The {name} is unavailable") from exc
        if stat.S_ISLNK(current_stat.st_mode) or _is_reparse(current_stat):
            raise PublicAcquisitionError(f"The {name} contains a link or reparse component")
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
        raise PublicAcquisitionError(f"The {name} is unavailable") from exc
    if (
        not stat.S_ISDIR(source_stat.st_mode)
        or not stat.S_ISDIR(resolved_stat.st_mode)
        or stat.S_ISLNK(source_stat.st_mode)
        or _is_reparse(source_stat)
        or _is_reparse(resolved_stat)
    ):
        raise PublicAcquisitionError(f"The {name} must be a physical directory")
    return resolved


def _require_external(path: Path, *, name: str) -> None:
    repository = REPOSITORY_ROOT.resolve(strict=True)
    resolved = path.resolve(strict=False)
    if resolved == repository or repository in resolved.parents:
        raise PublicAcquisitionError(f"The {name} must remain outside the repository")


def _physical_cache_file(root: Path, relative: str, *, name: str) -> Path:
    relative_path = PurePosixPath(_safe_relative(relative, name="cache path"))
    current = root
    for part in relative_path.parts[:-1]:
        current = _physical_directory(current / part, name="public-source cache subdirectory")
        if root not in current.parents:
            raise PublicAcquisitionError("A public-source cache path escapes its root")
    path = current / relative_path.name
    try:
        source_stat = os.lstat(path)
        resolved = path.resolve(strict=True)
        resolved_stat = os.lstat(resolved)
    except OSError as exc:
        raise PublicAcquisitionError(f"The cached public {name} is unavailable") from exc
    if (
        root not in resolved.parents
        or not stat.S_ISREG(source_stat.st_mode)
        or not stat.S_ISREG(resolved_stat.st_mode)
        or stat.S_ISLNK(source_stat.st_mode)
        or _is_reparse(source_stat)
        or _is_reparse(resolved_stat)
    ):
        raise PublicAcquisitionError(f"The cached public {name} must be a physical file")
    return resolved


def _verify_handle(handle: BinaryIO, pin: FilePin, *, name: str) -> bytes:
    try:
        handle_stat = os.fstat(handle.fileno())
        if not stat.S_ISREG(handle_stat.st_mode) or _is_reparse(handle_stat):
            raise PublicAcquisitionError(f"The cached public {name} must be a physical file")
        raw = handle.read(pin.byte_count + 1)
        trailing = handle.read(1)
    except OSError as exc:
        raise PublicAcquisitionError(f"The cached public {name} could not be verified") from exc
    if handle_stat.st_size != pin.byte_count or len(raw) != pin.byte_count or trailing:
        raise PublicAcquisitionError(f"The cached public {name} byte length does not match")
    if hashlib.sha256(raw).hexdigest() != pin.sha256:
        raise PublicAcquisitionError(f"The cached public {name} SHA-256 does not match")
    return raw


def _verified_bytes(root: Path, pin: FilePin, *, name: str) -> bytes:
    path = _physical_cache_file(root, pin.cache_relative_path, name=name)
    try:
        with path.open("rb") as handle:
            return _verify_handle(handle, pin, name=name)
    except OSError as exc:
        raise PublicAcquisitionError(f"The cached public {name} could not be opened") from exc


def length_band(text: str) -> str:
    for name, minimum, maximum in LENGTH_BANDS:
        if minimum <= len(text) <= maximum:
            return name
    raise PublicAcquisitionError("A public-source line is outside the 1--2,048 boundary")


def script_family(text: str) -> str:
    scripts: set[str] = set()
    has_ascii_symbol = False
    for character in text:
        point = ord(character)
        if character.isascii():
            if character.isalpha():
                scripts.add("latin")
            elif not character.isspace():
                has_ascii_symbol = True
        elif 0x0400 <= point <= 0x052F:
            scripts.add("cyrillic")
        elif 0x0600 <= point <= 0x06FF:
            scripts.add("arabic")
        elif 0x0900 <= point <= 0x097F:
            scripts.add("devanagari")
        elif 0x3040 <= point <= 0x30FF:
            scripts.add("japanese")
        elif 0x3400 <= point <= 0x9FFF:
            scripts.add("han")
        elif character.isalpha():
            scripts.add("latin")
        elif not character.isspace():
            has_ascii_symbol = True
    if len(scripts) > 1:
        return "mixed-script"
    if scripts:
        return next(iter(scripts))
    if has_ascii_symbol:
        return "ascii-symbolic"
    return "ascii-symbolic"


def _source_lines(raw: bytes, source: SourcePin) -> Sequence[dict[str, Any]]:
    try:
        text = raw.decode("utf-8", errors="strict")
    except UnicodeDecodeError as exc:
        raise PublicAcquisitionError("A cached public source is not strict UTF-8") from exc
    if text.startswith("\ufeff") or "\x00" in text:
        raise PublicAcquisitionError("A cached public source has a BOM or NUL")
    rows: list[dict[str, Any]] = []
    for ordinal, line in enumerate(text.splitlines(), start=1):
        if not line.strip():
            continue
        if any(ord(character) < 32 and character != "\t" for character in line):
            raise PublicAcquisitionError("A cached public source contains control text")
        band = length_band(line)
        rows.append(
            {
                "schemaVersion": SCHEMA_VERSION,
                "id": f"public-{source.source_id}-l{ordinal:06d}",
                "groupId": f"public-{source.source_id}-file",
                "text": line,
                "originFamily": source.origin_family,
                "scriptFamily": script_family(line),
                "lengthBand": band,
                "sourceId": source.source_id,
                "sourceProject": source.source_project,
                "sourceRevision": source.source_revision,
                "sourcePath": source.source_path,
                "sourceOrdinal": ordinal,
                "sourceKind": source.source_kind,
                "sourceProvenance": source.asset_provenance,
                "licenseId": source.license.identifier,
                "label": "UNLABELED",
                "reviewState": "pending",
                "requiredIndependentReviews": 2,
                "researchOnly": True,
                "promotionEligible": False,
            }
        )
        if len(rows) > MAX_RECORDS_PER_SOURCE:
            raise PublicAcquisitionError("A cached public source exceeds its record boundary")
    if not rows:
        raise PublicAcquisitionError("A cached public source contains no annotation records")
    return rows


def _physical_output_parent(output: Path) -> tuple[Path, Path]:
    if output.exists() or output.is_symlink():
        raise PublicAcquisitionError("The public queue output directory must not already exist")
    parent = _physical_directory(output.parent, name="public queue output parent")
    destination = parent / output.name
    if not output.name or destination == parent:
        raise PublicAcquisitionError("The public queue output directory name is invalid")
    return parent, destination


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _artifact(path: Path, *, relative_name: str | None = None) -> ArtifactIdentity:
    return ArtifactIdentity(
        name=relative_name or path.name,
        byte_count=path.stat().st_size,
        sha256=_sha256_file(path),
    )


def _write_bytes(path: Path, raw: bytes, *, relative_name: str | None = None) -> ArtifactIdentity:
    path.parent.mkdir(parents=False, exist_ok=True)
    with path.open("xb") as handle:
        handle.write(raw)
        handle.flush()
        os.fsync(handle.fileno())
    return _artifact(path, relative_name=relative_name)


def _write_json(path: Path, value: Any) -> ArtifactIdentity:
    return _write_bytes(path, (canonical_json(value) + "\n").encode("utf-8"))


def _write_jsonl(path: Path, rows: Sequence[Mapping[str, Any]]) -> ArtifactIdentity:
    digest = hashlib.sha256()
    byte_count = 0
    with path.open("xb") as handle:
        for row in rows:
            raw = (canonical_json(row) + "\n").encode("utf-8")
            handle.write(raw)
            digest.update(raw)
            byte_count += len(raw)
        handle.flush()
        os.fsync(handle.fileno())
    return ArtifactIdentity(name=path.name, byte_count=byte_count, sha256=digest.hexdigest())


def _artifact_value(identity: ArtifactIdentity) -> dict[str, Any]:
    return {"name": identity.name, "bytes": identity.byte_count, "sha256": identity.sha256}


def _duplicate_linked_project_components(
    rows: Sequence[Mapping[str, Any]],
) -> tuple[list[dict[str, Any]], int]:
    projects = sorted({str(row["sourceProject"]) for row in rows})
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
        if left_root < right_root:
            parent[right_root] = left_root
        else:
            parent[left_root] = right_root

    projects_by_text: dict[str, set[str]] = {}
    for row in rows:
        projects_by_text.setdefault(str(row["text"]), set()).add(str(row["sourceProject"]))
    cross_project_duplicate_texts = 0
    for linked_projects in projects_by_text.values():
        if len(linked_projects) < 2:
            continue
        cross_project_duplicate_texts += 1
        ordered = sorted(linked_projects)
        for linked_project in ordered[1:]:
            union(ordered[0], linked_project)

    component_projects: dict[str, list[str]] = {}
    for project in projects:
        component_projects.setdefault(find(project), []).append(project)
    ordered_components = sorted(tuple(sorted(values)) for values in component_projects.values())

    components: list[dict[str, Any]] = []
    for ordinal, linked_projects in enumerate(ordered_components, start=1):
        project_set = set(linked_projects)
        component_rows = [row for row in rows if row["sourceProject"] in project_set]
        components.append(
            {
                "componentId": f"public-project-component-{ordinal:03d}",
                "linkRule": "exact-text-cross-project-v1",
                "sourceProjects": list(linked_projects),
                "sourceIds": sorted({str(row["sourceId"]) for row in component_rows}),
                "groupIds": sorted({str(row["groupId"]) for row in component_rows}),
                "records": len(component_rows),
            }
        )
    return components, cross_project_duplicate_texts


def materialize_public_queue(
    cache_directory: Path,
    output_directory: Path,
    *,
    manifest: SourceManifest | None = None,
) -> Mapping[str, Any]:
    manifest = manifest or load_source_manifest()
    cache_root = _physical_directory(cache_directory, name="public-source cache directory")
    _require_external(cache_root, name="public-source cache")
    parent, destination = _physical_output_parent(output_directory)
    _require_external(destination, name="public queue output")
    if cache_root == destination or cache_root in destination.parents:
        raise PublicAcquisitionError("The public queue output must not be inside its source cache")

    rows: list[dict[str, Any]] = []
    license_bytes: dict[str, bytes] = {}
    verified_sources: list[dict[str, Any]] = []
    for source in manifest.sources:
        raw = _verified_bytes(cache_root, source, name="source")
        license_raw = _verified_bytes(cache_root, source.license, name="license")
        try:
            license_raw.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exc:
            raise PublicAcquisitionError("A cached public license is not strict UTF-8") from exc
        source_rows = _source_lines(raw, source)
        rows.extend(source_rows)
        license_bytes[source.source_id] = license_raw
        verified_sources.append(
            {
                "sourceId": source.source_id,
                "sourceProject": source.source_project,
                "sourceRevision": source.source_revision,
                "sourcePath": source.source_path,
                "sourceUrl": source.source_url,
                "cacheRelativePath": source.cache_relative_path,
                "bytes": source.byte_count,
                "sha256": source.sha256,
                "sourceKind": source.source_kind,
                "originFamily": source.origin_family,
                "assetProvenance": source.asset_provenance,
                "licensingLimit": source.licensing_limit,
                "license": {
                    "id": source.license.identifier,
                    "sourcePath": source.license.source_path,
                    "sourceUrl": source.license.source_url,
                    "cacheRelativePath": source.license.cache_relative_path,
                    "bytes": source.license.byte_count,
                    "sha256": source.license.sha256,
                },
            }
        )
    if len({row["id"] for row in rows}) != len(rows):
        raise PublicAcquisitionError("The public annotation queue has duplicate record ids")
    duplicate_components, duplicate_texts = _duplicate_linked_project_components(rows)

    groups = []
    for source in manifest.sources:
        group_id = f"public-{source.source_id}-file"
        groups.append(
            {
                "groupId": group_id,
                "groupingUnit": "upstream-project-file-template",
                "sourceId": source.source_id,
                "sourceProject": source.source_project,
                "sourceRevision": source.source_revision,
                "sourcePath": source.source_path,
                "records": sum(row["groupId"] == group_id for row in rows),
            }
        )
    counts = {
        "sources": len(manifest.sources),
        "independentProjects": len({source.source_project for source in manifest.sources}),
        "groups": len(groups),
        "records": len(rows),
        "duplicateLinkedProjectComponents": len(duplicate_components),
        "crossProjectDuplicateTexts": duplicate_texts,
        "bySourceKind": {
            name: sum(row["sourceKind"] == name for row in rows) for name in SOURCE_KINDS
        },
        "byOriginFamily": {
            name: sum(row["originFamily"] == name for row in rows) for name in ORIGIN_FAMILIES
        },
        "byScriptFamily": {
            name: sum(row["scriptFamily"] == name for row in rows) for name in SCRIPT_FAMILIES
        },
        "byLengthBand": {
            name: sum(row["lengthBand"] == name for row in rows) for name, _, _ in LENGTH_BANDS
        },
        "byLicense": {name: sum(row["licenseId"] == name for row in rows) for name in LICENSE_IDS},
    }
    report = {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-public-annotation-queue",
        "manifestId": MANIFEST_ID,
        "status": "complete",
        "label": "UNLABELED",
        "requiredIndependentReviews": 2,
        "networkAccess": "forbidden",
        "researchOnly": True,
        "promotionEligible": False,
        "counts": counts,
    }

    stage = Path(tempfile.mkdtemp(prefix=f".{destination.name}.", suffix=".partial", dir=parent))
    try:
        queue_identity = _write_jsonl(stage / OUTPUT_QUEUE, rows)
        groups_identity = _write_json(
            stage / OUTPUT_GROUPS,
            {
                "schemaVersion": SCHEMA_VERSION,
                "manifestId": MANIFEST_ID,
                "researchOnly": True,
                "promotionEligible": False,
                "groups": groups,
                "duplicateLinkedProjectComponents": duplicate_components,
                "crossProjectDuplicateTexts": duplicate_texts,
            },
        )
        report_identity = _write_json(stage / OUTPUT_REPORT, report)
        licenses_directory = stage / "licenses"
        licenses_directory.mkdir()
        license_artifacts: dict[str, dict[str, Any]] = {}
        for source in manifest.sources:
            name = f"{source.source_id}-LICENSE"
            identity = _write_bytes(
                licenses_directory / name,
                license_bytes[source.source_id],
                relative_name=f"licenses/{name}",
            )
            license_artifacts[source.source_id] = _artifact_value(identity)
        external_sources = []
        for source in verified_sources:
            external_sources.append(
                {
                    **source,
                    "license": {
                        **source["license"],
                        "artifact": license_artifacts[source["sourceId"]],
                    },
                }
            )
        _write_json(
            stage / OUTPUT_SOURCES,
            {
                "schemaVersion": SCHEMA_VERSION,
                "manifestId": MANIFEST_ID,
                "labelPolicy": "UNLABELED-dual-independent-review-only",
                "networkAccess": "forbidden",
                "researchOnly": True,
                "promotionEligible": False,
                "sources": external_sources,
                "artifacts": {
                    "annotationQueue": _artifact_value(queue_identity),
                    "groups": _artifact_value(groups_identity),
                    "report": _artifact_value(report_identity),
                },
                "counts": counts,
            },
        )
        os.replace(stage, destination)
        return report
    finally:
        if stage.exists() and stage.parent == parent and stage.name.endswith(".partial"):
            shutil.rmtree(stage)


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache-directory", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    try:
        options = parse_arguments(arguments)
        report = materialize_public_queue(options.cache_directory, options.output_directory)
        sys.stdout.write(canonical_json(report) + "\n")
        return 0
    except PublicAcquisitionError as exc:
        print(f"public-source acquisition error: {exc}", file=sys.stderr)
        return 2
    except OSError:
        print("public-source acquisition error: filesystem operation failed", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
