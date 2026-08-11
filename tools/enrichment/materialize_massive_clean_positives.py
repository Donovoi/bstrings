#!/usr/bin/env python3
"""Materialize a pinned, research-only MASSIVE 1.1 clean-positive corpus.

The command never downloads data. It accepts only the already-cached official
archive, verifies its pinned byte length and SHA-256, and publishes a new
external artifact directory with one atomic directory rename. Source text is
never written to the repository by this module.
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
import tarfile
import tempfile
from collections import Counter
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, BinaryIO

SCHEMA_VERSION = 1
DEFAULT_SOURCE_MANIFEST = Path(__file__).with_name("translation_worthiness_massive_source_v1.json")
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]

PINNED_ARCHIVE_URL = (
    "https://amazon-massive-nlu-dataset.s3.amazonaws.com/amazon-massive-dataset-1.1.tar.gz"
)
PINNED_ARCHIVE_BYTES = 40_251_390
PINNED_ARCHIVE_SHA256 = "4cba5faa11c71437928e17cb1b9b3d8b8e727e7ea363a3a9a8045e19c0491577"
PINNED_HF_REPOSITORY = "AmazonScience/massive"
PINNED_HF_REVISION = "ff6bd8e4b27c3543e4f8fe2108f32bb95a6f8740"
PINNED_LICENSE = "CC-BY-4.0"
SAMPLING_RULE_VERSION = "massive-seed-sha256-v1"
DEFAULT_SEEDS_PER_SOURCE_SPLIT = 512
MAX_SEEDS_PER_SOURCE_SPLIT = 2_000
CORPUS_ID = "translation-worthiness-massive-1.1-clean-positive-v1"

SOURCE_TO_RESEARCH_SPLIT = {"train": "train", "dev": "calibration", "test": "test"}
SOURCE_SPLITS = tuple(SOURCE_TO_RESEARCH_SPLIT)
RESEARCH_SPLITS = ("calibration", "test", "train")

MAX_ARCHIVE_MEMBER_BYTES = 16 * 1024 * 1024
MAX_ARCHIVE_EXPANDED_BYTES = 700 * 1024 * 1024
MAX_SOURCE_LINE_BYTES = 64 * 1024
MAX_SOURCE_ROWS_PER_LOCALE = 25_000
MAX_TEXT_CODE_POINTS = 2_048
MAX_ANNOTATED_CODE_POINTS = 4_096
MAX_NOTICE_BYTES = 64 * 1024
MAX_LICENSE_BYTES = 128 * 1024

LOCALE_PATTERN = re.compile(r"^[a-z]{2}-[A-Z]{2}$", re.ASCII)
SEED_ID_PATTERN = re.compile(r"^(?:0|[1-9][0-9]{0,9})$", re.ASCII)
ANNOTATION_PATTERN = re.compile(r"\[([a-z][a-z0-9_]{0,63}) : ([^\[\]\r\n]{1,2048})\]", re.ASCII)

SOURCE_ROW_FIELDS = frozenset(
    {
        "id",
        "locale",
        "partition",
        "scenario",
        "intent",
        "utt",
        "annot_utt",
        "worker_id",
        "slot_method",
        "judgments",
    }
)
SOURCE_ROW_REQUIRED_FIELDS = frozenset(
    {"id", "locale", "partition", "scenario", "intent", "utt", "annot_utt", "worker_id"}
)
SOURCE_MANIFEST_FIELDS = frozenset(
    {
        "schemaVersion",
        "corpusId",
        "archive",
        "dataset",
        "expectedLocales",
        "license",
        "notice",
        "sampling",
        "researchOnly",
        "promotionEligible",
    }
)

OUTPUT_CORPUS = "corpus.jsonl"
OUTPUT_SPLITS = "splits.json"
OUTPUT_SOURCE = "source.json"
OUTPUT_REPORT = "report.json"
OUTPUT_NOTICE = "NOTICE.md"
OUTPUT_LICENSE = "LICENSE"


class MassiveAcquisitionError(RuntimeError):
    """Raised when pinned source evidence or an output safety invariant fails."""


@dataclass(frozen=True)
class SourcePin:
    archive_url: str
    archive_bytes: int
    archive_sha256: str
    hugging_face_repository: str
    hugging_face_revision: str
    expected_locales: tuple[str, ...]
    notice_member: str
    notice_repository_url: str
    notice_repository_revision: str
    license_member: str
    license_spdx: str


@dataclass(frozen=True)
class SourceRecord:
    seed_id: str
    locale: str
    source_partition: str
    utterance: str
    fragments: tuple[str, ...]


@dataclass(frozen=True)
class ArtifactIdentity:
    name: str
    byte_count: int
    sha256: str


def _reject_json_constant(value: str) -> None:
    del value
    raise MassiveAcquisitionError("JSON input contains a non-finite numeric constant")


def _unique_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise MassiveAcquisitionError("JSON input contains a duplicate object property")
        result[key] = value
    return result


def _strict_json(raw: bytes, *, name: str) -> Any:
    try:
        return json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_json_constant,
        )
    except MassiveAcquisitionError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise MassiveAcquisitionError(f"{name} is not strict UTF-8 JSON") from exc


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def _exact_object(value: Any, fields: frozenset[str], *, name: str) -> Mapping[str, Any]:
    if not isinstance(value, dict) or frozenset(value) != fields:
        raise MassiveAcquisitionError(f"{name} does not use its schema-1 property allowlist")
    return value


def load_source_pin(path: Path = DEFAULT_SOURCE_MANIFEST) -> SourcePin:
    try:
        raw = path.read_bytes()
    except OSError as exc:
        raise MassiveAcquisitionError("The MASSIVE source manifest could not be read") from exc
    if not raw or len(raw) > MAX_SOURCE_LINE_BYTES:
        raise MassiveAcquisitionError("The MASSIVE source manifest is empty or over length")
    value = _exact_object(
        _strict_json(raw, name="MASSIVE source manifest"),
        SOURCE_MANIFEST_FIELDS,
        name="MASSIVE source manifest",
    )
    archive = _exact_object(
        value["archive"], frozenset({"url", "bytes", "sha256"}), name="archive pin"
    )
    dataset = _exact_object(
        value["dataset"],
        frozenset({"version", "huggingFaceRepository", "huggingFaceRevision"}),
        name="dataset pin",
    )
    license_value = _exact_object(
        value["license"], frozenset({"spdx", "archiveMember"}), name="license pin"
    )
    notice = _exact_object(
        value["notice"],
        frozenset({"archiveMember", "officialRepositoryUrl", "officialRepositoryRevision"}),
        name="notice pin",
    )
    sampling = _exact_object(
        value["sampling"],
        frozenset({"ruleVersion", "defaultSeedCountPerSourceSplit"}),
        name="sampling pin",
    )
    locales = value["expectedLocales"]
    if (
        type(value["schemaVersion"]) is not int
        or value["schemaVersion"] != SCHEMA_VERSION
        or value["corpusId"] != CORPUS_ID
        or value["researchOnly"] is not True
        or value["promotionEligible"] is not False
        or archive
        != {
            "url": PINNED_ARCHIVE_URL,
            "bytes": PINNED_ARCHIVE_BYTES,
            "sha256": PINNED_ARCHIVE_SHA256,
        }
        or dataset
        != {
            "version": "1.1",
            "huggingFaceRepository": PINNED_HF_REPOSITORY,
            "huggingFaceRevision": PINNED_HF_REVISION,
        }
        or license_value != {"spdx": PINNED_LICENSE, "archiveMember": "1.1/LICENSE"}
        or sampling
        != {
            "ruleVersion": SAMPLING_RULE_VERSION,
            "defaultSeedCountPerSourceSplit": DEFAULT_SEEDS_PER_SOURCE_SPLIT,
        }
    ):
        raise MassiveAcquisitionError("The MASSIVE source manifest identity is unsupported")
    if (
        not isinstance(locales, list)
        or len(locales) != 52
        or locales != sorted(locales)
        or len(set(locales)) != len(locales)
        or not all(
            isinstance(locale, str) and LOCALE_PATTERN.fullmatch(locale) for locale in locales
        )
    ):
        raise MassiveAcquisitionError("The MASSIVE locale allowlist is invalid")
    if (
        not isinstance(notice["archiveMember"], str)
        or notice["archiveMember"] != "1.1/NOTICE.md"
        or not isinstance(notice["officialRepositoryUrl"], str)
        or not notice["officialRepositoryUrl"].startswith("https://github.com/alexa/massive/blob/")
        or not isinstance(notice["officialRepositoryRevision"], str)
        or not re.fullmatch(r"[0-9a-f]{40}", notice["officialRepositoryRevision"], re.ASCII)
        or notice["officialRepositoryRevision"] not in notice["officialRepositoryUrl"]
    ):
        raise MassiveAcquisitionError("The MASSIVE official NOTICE pin is invalid")
    return SourcePin(
        archive_url=archive["url"],
        archive_bytes=archive["bytes"],
        archive_sha256=archive["sha256"],
        hugging_face_repository=dataset["huggingFaceRepository"],
        hugging_face_revision=dataset["huggingFaceRevision"],
        expected_locales=tuple(locales),
        notice_member=notice["archiveMember"],
        notice_repository_url=notice["officialRepositoryUrl"],
        notice_repository_revision=notice["officialRepositoryRevision"],
        license_member=license_value["archiveMember"],
        license_spdx=license_value["spdx"],
    )


def _is_reparse(stat_result: os.stat_result) -> bool:
    attributes = getattr(stat_result, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return bool(attributes & reparse_flag)


def physical_archive(path: Path) -> Path:
    try:
        source_stat = os.lstat(path)
        resolved = path.resolve(strict=True)
        resolved_stat = os.lstat(resolved)
    except OSError as exc:
        raise MassiveAcquisitionError("The cached MASSIVE archive is unavailable") from exc
    if (
        not stat.S_ISREG(source_stat.st_mode)
        or not stat.S_ISREG(resolved_stat.st_mode)
        or stat.S_ISLNK(source_stat.st_mode)
        or _is_reparse(source_stat)
        or _is_reparse(resolved_stat)
    ):
        raise MassiveAcquisitionError("The cached MASSIVE archive must be a physical file")
    return resolved


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def _verify_archive_handle(handle: BinaryIO, pin: SourcePin) -> None:
    try:
        handle_stat = os.fstat(handle.fileno())
        if not stat.S_ISREG(handle_stat.st_mode) or _is_reparse(handle_stat):
            raise MassiveAcquisitionError("The cached MASSIVE archive must be a physical file")
        digest = hashlib.sha256()
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
        handle.seek(0)
    except OSError as exc:
        raise MassiveAcquisitionError("The cached MASSIVE archive could not be verified") from exc
    if handle_stat.st_size != pin.archive_bytes:
        raise MassiveAcquisitionError(
            "The cached MASSIVE archive byte length does not match the pin"
        )
    if digest.hexdigest() != pin.archive_sha256:
        raise MassiveAcquisitionError("The cached MASSIVE archive SHA-256 does not match the pin")


def verify_archive(path: Path, pin: SourcePin) -> Path:
    resolved = physical_archive(path)
    try:
        with resolved.open("rb") as handle:
            _verify_archive_handle(handle, pin)
    except OSError as exc:
        raise MassiveAcquisitionError("The cached MASSIVE archive could not be opened") from exc
    return resolved


def _require_external(path: Path, *, name: str) -> None:
    repository = REPOSITORY_ROOT.resolve(strict=True)
    resolved = path.resolve(strict=False)
    if resolved == repository or repository in resolved.parents:
        raise MassiveAcquisitionError(f"The MASSIVE {name} must remain outside the repository")


def _safe_member_name(name: str) -> bool:
    if not name or "\\" in name or name.startswith("/") or "\x00" in name:
        return False
    path = PurePosixPath(name.rstrip("/"))
    return bool(path.parts) and all(part not in {"", ".", ".."} for part in path.parts)


def _archive_members(archive: tarfile.TarFile, pin: SourcePin) -> Mapping[str, tarfile.TarInfo]:
    expected_files = {
        pin.notice_member,
        pin.license_member,
        "1.1/CITATION.md",
        "1.1/CHANGELOG.md",
        *(f"1.1/data/{locale}.jsonl" for locale in pin.expected_locales),
    }
    expected_directories = {"1.1", "1.1/data"}
    members: dict[str, tarfile.TarInfo] = {}
    expanded_bytes = 0
    for member in archive.getmembers():
        name = member.name.rstrip("/")
        if not _safe_member_name(member.name) or name in members:
            raise MassiveAcquisitionError(
                "The MASSIVE archive contains an unsafe or duplicate member"
            )
        members[name] = member
        if name in expected_directories:
            if not member.isdir():
                raise MassiveAcquisitionError("A MASSIVE archive directory has the wrong type")
            continue
        if name not in expected_files or not member.isfile() or member.sparse is not None:
            raise MassiveAcquisitionError("The MASSIVE archive member allowlist is violated")
        if member.size < 0 or member.size > MAX_ARCHIVE_MEMBER_BYTES:
            raise MassiveAcquisitionError("A MASSIVE archive member exceeds its size boundary")
        expanded_bytes += member.size
        if expanded_bytes > MAX_ARCHIVE_EXPANDED_BYTES:
            raise MassiveAcquisitionError("The MASSIVE archive exceeds its expanded-size boundary")
    if set(members) != expected_files | expected_directories:
        raise MassiveAcquisitionError(
            "The MASSIVE archive does not exactly match its member inventory"
        )
    return members


def _member_bytes(
    archive: tarfile.TarFile, member: tarfile.TarInfo, maximum: int, *, name: str
) -> bytes:
    if member.size <= 0 or member.size > maximum:
        raise MassiveAcquisitionError(f"The MASSIVE {name} is empty or over length")
    handle = archive.extractfile(member)
    if handle is None:
        raise MassiveAcquisitionError(f"The MASSIVE {name} could not be read")
    raw = handle.read(maximum + 1)
    if len(raw) != member.size or len(raw) > maximum:
        raise MassiveAcquisitionError(f"The MASSIVE {name} did not match its archive header")
    return raw


def _valid_text(value: Any, *, maximum: int, name: str) -> str:
    if not isinstance(value, str) or not value or len(value) > maximum or "\x00" in value:
        raise MassiveAcquisitionError(f"A MASSIVE source row has invalid {name}")
    if any(ord(character) < 32 and character not in "\t" for character in value):
        raise MassiveAcquisitionError(f"A MASSIVE source row has control characters in {name}")
    return value


def annotation_fragments(annotated: str) -> tuple[str, ...]:
    fragments: list[str] = []
    cursor = 0
    for match in ANNOTATION_PATTERN.finditer(annotated):
        between = annotated[cursor : match.start()]
        if "[" in between or "]" in between:
            raise MassiveAcquisitionError("A MASSIVE annotation has unsupported bracket structure")
        fragments.append(_valid_text(match.group(2), maximum=MAX_TEXT_CODE_POINTS, name="fragment"))
        cursor = match.end()
    if "[" in annotated[cursor:] or "]" in annotated[cursor:]:
        raise MassiveAcquisitionError("A MASSIVE annotation has unsupported bracket structure")
    return tuple(fragments)


def _source_rows(handle: BinaryIO, *, locale: str) -> Sequence[SourceRecord]:
    records: list[SourceRecord] = []
    ordinal = 0
    while True:
        raw = handle.readline(MAX_SOURCE_LINE_BYTES + 1)
        if not raw:
            break
        ordinal += 1
        if ordinal > MAX_SOURCE_ROWS_PER_LOCALE or len(raw) > MAX_SOURCE_LINE_BYTES:
            raise MassiveAcquisitionError("A MASSIVE locale file exceeds its row boundary")
        if raw in {b"\n", b"\r\n"}:
            raise MassiveAcquisitionError("A MASSIVE locale row is empty")
        value = _strict_json(raw.rstrip(b"\r\n"), name="MASSIVE source row")
        if (
            not isinstance(value, dict)
            or not SOURCE_ROW_REQUIRED_FIELDS.issubset(value)
            or not frozenset(value).issubset(SOURCE_ROW_FIELDS)
        ):
            raise MassiveAcquisitionError("A MASSIVE source row violates the property allowlist")
        seed_id = value["id"]
        partition = value["partition"]
        if not isinstance(seed_id, str) or SEED_ID_PATTERN.fullmatch(seed_id) is None:
            raise MassiveAcquisitionError("A MASSIVE source row has an invalid seed id")
        if value["locale"] != locale:
            raise MassiveAcquisitionError("A MASSIVE source row locale disagrees with its member")
        if not isinstance(partition, str) or partition not in SOURCE_TO_RESEARCH_SPLIT:
            raise MassiveAcquisitionError("A MASSIVE source row has an unsupported partition")
        utterance = _valid_text(value["utt"], maximum=MAX_TEXT_CODE_POINTS, name="utterance")
        annotated = _valid_text(
            value["annot_utt"], maximum=MAX_ANNOTATED_CODE_POINTS, name="annotated utterance"
        )
        records.append(
            SourceRecord(
                seed_id=seed_id,
                locale=locale,
                source_partition=partition,
                utterance=utterance,
                fragments=annotation_fragments(annotated),
            )
        )
    if not records:
        raise MassiveAcquisitionError("A MASSIVE locale member contains no rows")
    return records


def _read_locale(
    archive: tarfile.TarFile, members: Mapping[str, tarfile.TarInfo], locale: str
) -> Sequence[SourceRecord]:
    handle = archive.extractfile(members[f"1.1/data/{locale}.jsonl"])
    if handle is None:
        raise MassiveAcquisitionError("A MASSIVE locale member could not be opened")
    return _source_rows(handle, locale=locale)


def select_seed_ids(
    seed_partition: Mapping[str, str], count_per_source_split: int
) -> Mapping[str, tuple[str, ...]]:
    if (
        type(count_per_source_split) is not int
        or not 1 <= count_per_source_split <= MAX_SEEDS_PER_SOURCE_SPLIT
    ):
        raise MassiveAcquisitionError("The requested MASSIVE seed count is outside its boundary")
    candidates: dict[str, list[str]] = {split: [] for split in SOURCE_SPLITS}
    for seed_id, partition in seed_partition.items():
        if partition not in candidates:
            raise MassiveAcquisitionError("A MASSIVE seed has an unsupported source partition")
        candidates[partition].append(seed_id)
    selected: dict[str, tuple[str, ...]] = {}
    for partition in SOURCE_SPLITS:
        if len(candidates[partition]) < count_per_source_split:
            raise MassiveAcquisitionError("A MASSIVE source split has too few seed families")

        def ranking(seed_id: str, source_partition: str = partition) -> tuple[bytes, str]:
            payload = f"{SAMPLING_RULE_VERSION}\0{source_partition}\0{seed_id}".encode("ascii")
            return hashlib.sha256(payload).digest(), seed_id

        selected[partition] = tuple(
            sorted(candidates[partition], key=ranking)[:count_per_source_split]
        )
    return selected


def _scan_and_select(
    archive: tarfile.TarFile,
    members: Mapping[str, tarfile.TarInfo],
    pin: SourcePin,
    count_per_source_split: int,
) -> Mapping[str, tuple[str, ...]]:
    seed_partition: dict[str, str] = {}
    locale_count: Counter[str] = Counter()
    for locale in pin.expected_locales:
        local_ids: set[str] = set()
        for record in _read_locale(archive, members, locale):
            if record.seed_id in local_ids:
                raise MassiveAcquisitionError("A MASSIVE locale duplicates a seed id")
            local_ids.add(record.seed_id)
            prior = seed_partition.setdefault(record.seed_id, record.source_partition)
            if prior != record.source_partition:
                raise MassiveAcquisitionError("A MASSIVE seed crosses source partitions")
            locale_count[record.seed_id] += 1
    if not seed_partition or any(
        count != len(pin.expected_locales) for count in locale_count.values()
    ):
        raise MassiveAcquisitionError("MASSIVE seed families do not cover every pinned locale")
    return select_seed_ids(seed_partition, count_per_source_split)


def _collect_selected(
    archive: tarfile.TarFile,
    members: Mapping[str, tarfile.TarInfo],
    pin: SourcePin,
    selected: Mapping[str, tuple[str, ...]],
) -> Mapping[tuple[str, str], SourceRecord]:
    selected_partition = {
        seed_id: partition for partition, seed_ids in selected.items() for seed_id in seed_ids
    }
    records: dict[tuple[str, str], SourceRecord] = {}
    for locale in pin.expected_locales:
        for record in _read_locale(archive, members, locale):
            expected_partition = selected_partition.get(record.seed_id)
            if expected_partition is None:
                continue
            if record.source_partition != expected_partition:
                raise MassiveAcquisitionError("A selected MASSIVE seed crosses source partitions")
            key = (record.seed_id, locale)
            if key in records:
                raise MassiveAcquisitionError("A selected MASSIVE locale duplicates a seed id")
            records[key] = record
    expected_records = len(selected_partition) * len(pin.expected_locales)
    if len(records) != expected_records:
        raise MassiveAcquisitionError("A selected MASSIVE seed is missing a locale translation")
    return records


def _group_id(seed_id: str) -> str:
    return f"massive-1.1-seed-{seed_id}"


def _corpus_rows(records: Mapping[tuple[str, str], SourceRecord]) -> Sequence[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    ordered = sorted(
        records.values(),
        key=lambda item: (
            SOURCE_SPLITS.index(item.source_partition),
            item.seed_id,
            item.locale,
        ),
    )
    for record in ordered:
        base = {
            "schemaVersion": SCHEMA_VERSION,
            "corpusId": CORPUS_ID,
            "groupId": _group_id(record.seed_id),
            "sourceId": "MASSIVE-1.1",
            "sourceRecordId": record.seed_id,
            "sourceLocale": record.locale,
            "sourcePartition": record.source_partition,
            "split": SOURCE_TO_RESEARCH_SPLIT[record.source_partition],
            "label": "human-worthy",
            "containsHumanMaterial": True,
            "license": PINNED_LICENSE,
            "researchOnly": True,
            "promotionEligible": False,
        }
        stem = f"massive-1.1-{record.seed_id}-{record.locale}"
        rows.append(
            {
                **base,
                "id": f"{stem}-u",
                "derivation": "utterance",
                "fragmentOrdinal": None,
                "text": record.utterance,
            }
        )
        rows.extend(
            {
                **base,
                "id": f"{stem}-f{ordinal:03d}",
                "derivation": "annotated-slot-fragment",
                "fragmentOrdinal": ordinal,
                "text": fragment,
            }
            for ordinal, fragment in enumerate(record.fragments, start=1)
        )
    return rows


def _artifact(path: Path) -> ArtifactIdentity:
    return ArtifactIdentity(
        name=path.name, byte_count=path.stat().st_size, sha256=sha256_file(path)
    )


def _write_bytes(path: Path, raw: bytes) -> ArtifactIdentity:
    with path.open("xb") as handle:
        handle.write(raw)
        handle.flush()
        os.fsync(handle.fileno())
    return _artifact(path)


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


def _physical_output_parent(output: Path) -> tuple[Path, Path]:
    if output.exists() or output.is_symlink():
        raise MassiveAcquisitionError("The MASSIVE output directory must not already exist")
    try:
        parent = output.parent.resolve(strict=True)
        parent_stat = os.lstat(parent)
    except OSError as exc:
        raise MassiveAcquisitionError("The MASSIVE output parent is unavailable") from exc
    if not stat.S_ISDIR(parent_stat.st_mode) or _is_reparse(parent_stat):
        raise MassiveAcquisitionError("The MASSIVE output parent must be a physical directory")
    destination = parent / output.name
    if not output.name or destination == parent:
        raise MassiveAcquisitionError("The MASSIVE output directory name is invalid")
    return parent, destination


def _artifact_value(identity: ArtifactIdentity) -> dict[str, Any]:
    return {"name": identity.name, "bytes": identity.byte_count, "sha256": identity.sha256}


def materialize(
    archive_path: Path,
    output_directory: Path,
    *,
    count_per_source_split: int = DEFAULT_SEEDS_PER_SOURCE_SPLIT,
    pin: SourcePin | None = None,
) -> Mapping[str, Any]:
    pin = pin or load_source_pin()
    archive_path = physical_archive(archive_path)
    _require_external(archive_path, name="cached archive")
    parent, destination = _physical_output_parent(output_directory)
    _require_external(destination, name="materialized output")
    stage = Path(tempfile.mkdtemp(prefix=f".{destination.name}.", suffix=".partial", dir=parent))
    try:
        try:
            with archive_path.open("rb") as source_handle:
                _verify_archive_handle(source_handle, pin)
                with tarfile.open(fileobj=source_handle, mode="r:gz") as archive:
                    members = _archive_members(archive, pin)
                    notice = _member_bytes(
                        archive, members[pin.notice_member], MAX_NOTICE_BYTES, name="NOTICE"
                    )
                    license_bytes = _member_bytes(
                        archive, members[pin.license_member], MAX_LICENSE_BYTES, name="license"
                    )
                    notice.decode("utf-8", errors="strict")
                    license_bytes.decode("utf-8", errors="strict")
                    selected = _scan_and_select(archive, members, pin, count_per_source_split)
                    records = _collect_selected(archive, members, pin, selected)
        except (tarfile.TarError, EOFError, UnicodeDecodeError) as exc:
            raise MassiveAcquisitionError(
                "The MASSIVE archive could not be safely decoded"
            ) from exc

        rows = _corpus_rows(records)
        split_groups = {
            SOURCE_TO_RESEARCH_SPLIT[source_split]: sorted(
                _group_id(seed_id) for seed_id in selected[source_split]
            )
            for source_split in SOURCE_SPLITS
        }
        split_manifest = {
            "schemaVersion": SCHEMA_VERSION,
            "corpusId": CORPUS_ID,
            "researchOnly": True,
            "promotionEligible": False,
            "groupingUnit": "MASSIVE-English-seed-family",
            "sourceSplitMapping": SOURCE_TO_RESEARCH_SPLIT,
            "splits": {split: split_groups[split] for split in RESEARCH_SPLITS},
        }
        derivations = Counter(row["derivation"] for row in rows)
        row_splits = Counter(row["split"] for row in rows)
        report = {
            "schemaVersion": SCHEMA_VERSION,
            "reportType": "massive-clean-positive-materialization",
            "corpusId": CORPUS_ID,
            "status": "complete",
            "researchOnly": True,
            "promotionEligible": False,
            "counts": {
                "locales": len(pin.expected_locales),
                "groups": sum(len(groups) for groups in split_groups.values()),
                "sourceRecords": len(records),
                "corpusRecords": len(rows),
                "utterances": derivations["utterance"],
                "annotatedSlotFragments": derivations["annotated-slot-fragment"],
                "groupsBySplit": {split: len(split_groups[split]) for split in RESEARCH_SPLITS},
                "recordsBySplit": {split: row_splits[split] for split in RESEARCH_SPLITS},
            },
        }

        corpus_identity = _write_jsonl(stage / OUTPUT_CORPUS, rows)
        splits_identity = _write_json(stage / OUTPUT_SPLITS, split_manifest)
        notice_identity = _write_bytes(stage / OUTPUT_NOTICE, notice)
        license_identity = _write_bytes(stage / OUTPUT_LICENSE, license_bytes)
        report_identity = _write_json(stage / OUTPUT_REPORT, report)
        source_manifest = {
            "schemaVersion": SCHEMA_VERSION,
            "corpusId": CORPUS_ID,
            "researchOnly": True,
            "promotionEligible": False,
            "archive": {
                "url": pin.archive_url,
                "bytes": pin.archive_bytes,
                "sha256": pin.archive_sha256,
            },
            "dataset": {
                "version": "1.1",
                "huggingFaceRepository": pin.hugging_face_repository,
                "huggingFaceRevision": pin.hugging_face_revision,
            },
            "license": {
                "spdx": pin.license_spdx,
                "archiveMember": pin.license_member,
                "artifact": _artifact_value(license_identity),
            },
            "notice": {
                "archiveMember": pin.notice_member,
                "officialRepositoryUrl": pin.notice_repository_url,
                "officialRepositoryRevision": pin.notice_repository_revision,
                "artifact": _artifact_value(notice_identity),
            },
            "sampling": {
                "ruleVersion": SAMPLING_RULE_VERSION,
                "seedCountPerSourceSplit": count_per_source_split,
                "groupingUnit": "MASSIVE-English-seed-family",
                "sourceSplitMapping": SOURCE_TO_RESEARCH_SPLIT,
            },
            "artifacts": {
                "corpus": _artifact_value(corpus_identity),
                "splits": _artifact_value(splits_identity),
                "report": _artifact_value(report_identity),
            },
            "counts": report["counts"],
        }
        _write_json(stage / OUTPUT_SOURCE, source_manifest)
        os.replace(stage, destination)
        return report
    finally:
        if stage.exists() and stage.parent == parent and stage.name.endswith(".partial"):
            shutil.rmtree(stage)


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    parser.add_argument(
        "--seed-count-per-source-split",
        type=int,
        default=DEFAULT_SEEDS_PER_SOURCE_SPLIT,
    )
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    try:
        options = parse_arguments(arguments)
        report = materialize(
            options.archive,
            options.output_directory,
            count_per_source_split=options.seed_count_per_source_split,
        )
        sys.stdout.write(canonical_json(report) + "\n")
        return 0
    except MassiveAcquisitionError as exc:
        print(f"MASSIVE acquisition error: {exc}", file=sys.stderr)
        return 2
    except OSError:
        print("MASSIVE acquisition error: filesystem operation failed", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
