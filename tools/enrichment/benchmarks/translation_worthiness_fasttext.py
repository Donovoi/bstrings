#!/usr/bin/env python3
"""Run the pinned, benchmark-only fastText translation-worthiness challenger.

Calibration and locked-test are deliberately separate commands.  The first
exports only train-labelled rows, selects a family and thresholds only from the
calibration split, and atomically publishes a selected model plus locked plan.
The second verifies every identity and scores the locked test input in exactly
one external batch without giving its labels to the scorer.  Nothing in this
module is imported by the product runtime or authorizes suppression.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import stat
import subprocess
import sys
import tempfile
from collections import Counter
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ENRICHMENT_ROOT = Path(__file__).resolve().parents[1]
if str(ENRICHMENT_ROOT) not in sys.path:
    sys.path.insert(0, str(ENRICHMENT_ROOT))

from translation_worthiness_corpus import (  # noqa: E402
    LABELS,
    LENGTH_BAND_NAMES,
    LENGTH_BANDS,
    ORIGIN_FAMILIES,
    SCRIPT_FAMILIES,
    SOURCE_FAMILIES,
    CorpusRow,
    Prediction,
    ValidatedCorpus,
    evaluation_report,
)

SCHEMA_VERSION = 1
CORPUS_ARTIFACT = "translation-worthiness-dual-adjudicated-corpus"
LOCK_ARTIFACT = "translation-worthiness-fasttext-source-lock"
PLAN_ARTIFACT = "translation-worthiness-fasttext-calibration-plan"
REPORT_LICENSE = "dual-adjudicated-public-corpus"
SCORE_ORIENTATION = "higher-contains-any-human"
EXPORT_CONTRACT = "fasttext-one-line-utf8-cr-lf-tab-to-space-v1"
SELECTION_OBJECTIVE = "zero-protected-suppress-zero-ambiguous-decisive-v1"
OFFICIAL_REPOSITORY = "https://github.com/facebookresearch/fastText"
REPOSITORY_ROOT = Path(__file__).resolve().parents[3]

MAX_ARTIFACT_BYTES = 64 * 1024 * 1024
MAX_MODEL_BYTES = 512 * 1024 * 1024
MAX_RECORDS = 100_000
MAX_IDENTIFIER_LENGTH = 96
MAX_TEXT_CODEPOINTS = 2_048
MAX_FAMILIES = 8
MAX_FIXED_ARGUMENTS = 4
PROCESS_TIMEOUT_SECONDS = 300
HASH_PATTERN = re.compile(r"^[0-9a-f]{64}$", re.ASCII)
REVISION_PATTERN = re.compile(r"^[0-9a-f]{40}(?:[0-9a-f]{24})?$", re.ASCII)
IDENTIFIER_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._-]{0,95}$", re.ASCII)

CORPUS_FIELDS = frozenset(
    {
        "schemaVersion",
        "artifactType",
        "researchOnly",
        "promotionEligible",
        "corpusId",
        "adjudicationPolicy",
        "rows",
    }
)
ADJUDICATION_POLICY_FIELDS = frozenset(
    {"method", "adjudicatorsPerRow", "independent", "agreementRequired"}
)
ROW_FIELDS = frozenset(
    {
        "id",
        "groupId",
        "split",
        "text",
        "label",
        "sourceFamily",
        "originFamily",
        "scriptFamily",
        "lengthBand",
        "license",
        "adjudication",
    }
)
ADJUDICATION_FIELDS = frozenset({"method", "labels"})
LOCK_FIELDS = frozenset(
    {
        "schemaVersion",
        "artifactType",
        "researchOnly",
        "promotionEligible",
        "upstream",
        "executable",
        "families",
    }
)
UPSTREAM_FIELDS = frozenset({"repository", "revision", "sourceSha256", "license"})
EXECUTABLE_FIELDS = frozenset({"sha256", "version", "fixedArguments", "fixedArgumentFileSha256"})
FAMILY_FIELDS = frozenset({"familyId", "parameters", "configSha256", "modelSha256"})
PARAMETER_FIELDS = frozenset(
    {
        "bucket",
        "dim",
        "epoch",
        "loss",
        "lr",
        "maxn",
        "minCount",
        "minn",
        "thread",
        "wordNgrams",
        "ws",
    }
)
PLAN_FIELDS = frozenset(
    {
        "schemaVersion",
        "artifactType",
        "researchOnly",
        "promotionEligible",
        "state",
        "corpusId",
        "corpusSha256",
        "sourceLockSha256",
        "sourceArchiveSha256",
        "executableSha256",
        "orchestratorSha256",
        "exportContract",
        "phaseHashes",
        "selected",
        "calibration",
    }
)
PHASE_HASH_FIELDS = frozenset(
    {"trainExportSha256", "calibrationInputSha256", "lockedTestInputSha256"}
)
SELECTED_FIELDS = frozenset(
    {
        "familyId",
        "configSha256",
        "modelSha256",
        "modelBytes",
        "suppressMax",
        "retainMin",
        "scoreOrientation",
    }
)
CALIBRATION_FIELDS = frozenset({"objective", "candidates"})
CANDIDATE_FIELDS = frozenset(
    {
        "familyId",
        "records",
        "knownHumanRecords",
        "machineRecords",
        "ambiguousRecords",
        "machineSuppressions",
        "positiveRetentions",
        "abstentions",
        "falseSuppressions",
        "ambiguousDecisive",
        "suppressMax",
        "retainMin",
    }
)


class FastTextChallengerError(RuntimeError):
    """Raised when the locked benchmark contract is not satisfied."""


@dataclass(frozen=True)
class ChallengerRow:
    identifier: str
    group_id: str
    split: str
    text: str
    label: str
    source_family: str
    origin_family: str
    script_family: str
    length_band: str

    def evaluator_row(self) -> CorpusRow:
        return CorpusRow(
            identifier=self.identifier,
            text=self.text,
            label=self.label,
            group_id=self.group_id,
            source_family=self.source_family,
            origin_family=self.origin_family,
            script_family=self.script_family,
            length_band=self.length_band,
        )


@dataclass(frozen=True)
class ChallengerCorpus:
    corpus_id: str
    rows: tuple[ChallengerRow, ...]
    sha256: str

    def split(self, name: str) -> tuple[ChallengerRow, ...]:
        return tuple(row for row in self.rows if row.split == name)


@dataclass(frozen=True)
class FastTextFamily:
    identifier: str
    parameters: Mapping[str, int | float | str]
    config_sha256: str
    model_sha256: str


@dataclass(frozen=True)
class SourceLock:
    sha256: str
    source_archive_sha256: str
    executable_sha256: str
    fixed_arguments: tuple[str, ...]
    fixed_argument_hashes: tuple[str | None, ...]
    families: tuple[FastTextFamily, ...]


def _unique_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise FastTextChallengerError("JSON contains a duplicate object property")
        result[key] = value
    return result


def _reject_constant(value: str) -> None:
    del value
    raise FastTextChallengerError("JSON contains a non-finite numeric constant")


def _load_json(raw: bytes, *, name: str) -> Any:
    try:
        return json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_constant,
        )
    except FastTextChallengerError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise FastTextChallengerError(f"{name} is not strict UTF-8 JSON") from exc


def _canonical_bytes(value: Any) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True) + "\n"
    ).encode("utf-8")


def _sha256(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def _is_reparse(path: Path) -> bool:
    try:
        if path.is_symlink() or getattr(path, "is_junction", lambda: False)():
            return True
        attributes = getattr(os.lstat(path), "st_file_attributes", 0)
        return bool(attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))
    except OSError:
        return False


def _reject_reparse_components(path: Path, *, name: str) -> None:
    absolute = Path(os.path.abspath(path))
    existing = absolute
    while not existing.exists() and existing != existing.parent:
        existing = existing.parent
    for component in (existing, *existing.parents):
        if _is_reparse(component):
            raise FastTextChallengerError(f"{name} must not traverse a reparse point")


def _physical_file(path: Path, *, name: str, maximum: int = MAX_ARTIFACT_BYTES) -> bytes:
    _reject_reparse_components(path, name=name)
    absolute = Path(os.path.abspath(path))
    if not absolute.is_file() or _is_reparse(absolute):
        raise FastTextChallengerError(f"{name} must be a physical file")
    size = absolute.stat().st_size
    if size <= 0 or size > maximum:
        raise FastTextChallengerError(f"{name} is empty or exceeds its size bound")
    try:
        return absolute.read_bytes()
    except OSError as exc:
        raise FastTextChallengerError(f"{name} could not be read") from exc


def _same_file(left: Path, right: Path) -> bool:
    left_absolute = Path(os.path.abspath(left))
    right_absolute = Path(os.path.abspath(right))
    if left_absolute == right_absolute:
        return True
    try:
        return (
            left_absolute.exists()
            and right_absolute.exists()
            and os.path.samefile(left_absolute, right_absolute)
        )
    except OSError:
        return False


def _safe_output(path: Path, inputs: Sequence[Path], *, name: str) -> Path:
    _reject_reparse_components(path, name=name)
    absolute = Path(os.path.abspath(path))
    repository = REPOSITORY_ROOT.resolve(strict=True)
    if absolute == repository or repository in absolute.parents:
        raise FastTextChallengerError(f"{name} must remain outside the repository")
    if any(_same_file(absolute, item) for item in inputs):
        raise FastTextChallengerError(f"{name} must not alias an input")
    if absolute.exists() and (not absolute.is_file() or _is_reparse(absolute)):
        raise FastTextChallengerError(f"{name} must be a physical file or absent")
    return absolute


def _publish(path: Path, payload: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    _reject_reparse_components(path.parent, name="output parent")
    descriptor, temporary_name = tempfile.mkstemp(
        prefix=f".{path.name}.", suffix=".partial", dir=path.parent
    )
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def _object(value: Any, fields: frozenset[str], *, name: str) -> Mapping[str, Any]:
    if not isinstance(value, dict) or frozenset(value) != fields:
        raise FastTextChallengerError(f"{name} does not use its exact property allowlist")
    return value


def _identifier(value: Any, *, name: str) -> str:
    if not isinstance(value, str) or IDENTIFIER_PATTERN.fullmatch(value) is None:
        raise FastTextChallengerError(f"{name} is invalid")
    return value


def _hash(value: Any, *, name: str) -> str:
    if not isinstance(value, str) or HASH_PATTERN.fullmatch(value) is None:
        raise FastTextChallengerError(f"{name} is not lowercase SHA-256")
    return value


def _finite(value: Any, *, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise FastTextChallengerError(f"{name} is not numeric")
    result = float(value)
    if not math.isfinite(result):
        raise FastTextChallengerError(f"{name} is not finite")
    return result


def _length_band(text: str) -> str:
    length = len(text)
    for name, minimum, maximum in LENGTH_BANDS:
        if minimum <= length <= maximum:
            return name
    raise FastTextChallengerError("Corpus text length is outside the supported bands")


def load_corpus(path: Path) -> ChallengerCorpus:
    raw = _physical_file(path, name="dual-adjudicated corpus")
    root = _object(_load_json(raw, name="dual-adjudicated corpus"), CORPUS_FIELDS, name="corpus")
    if (
        type(root["schemaVersion"]) is not int
        or root["schemaVersion"] != SCHEMA_VERSION
        or root["artifactType"] != CORPUS_ARTIFACT
        or root["researchOnly"] is not True
        or root["promotionEligible"] is not False
    ):
        raise FastTextChallengerError("Corpus identity is unsupported")
    corpus_id = _identifier(root["corpusId"], name="corpusId")
    policy = _object(
        root["adjudicationPolicy"], ADJUDICATION_POLICY_FIELDS, name="adjudicationPolicy"
    )
    if policy != {
        "method": "dual-independent-agreement",
        "adjudicatorsPerRow": 2,
        "independent": True,
        "agreementRequired": True,
    }:
        raise FastTextChallengerError("Corpus does not declare dual independent agreement")
    values = root["rows"]
    if not isinstance(values, list) or not 1 <= len(values) <= MAX_RECORDS:
        raise FastTextChallengerError("Corpus row count is outside bounds")
    rows: list[ChallengerRow] = []
    identifiers: set[str] = set()
    group_splits: dict[str, str] = {}
    text_groups: dict[str, str] = {}
    for ordinal, item in enumerate(values, start=1):
        row = _object(item, ROW_FIELDS, name=f"row {ordinal}")
        identifier = _identifier(row["id"], name=f"row {ordinal} id")
        group_id = _identifier(row["groupId"], name=f"row {ordinal} groupId")
        if identifier in identifiers:
            raise FastTextChallengerError("Corpus contains a duplicate row identifier")
        identifiers.add(identifier)
        split = row["split"]
        if split not in {"train", "calibration", "test"}:
            raise FastTextChallengerError(f"Row {ordinal} split is unsupported")
        prior_split = group_splits.setdefault(group_id, split)
        if prior_split != split:
            raise FastTextChallengerError("A corpus group crosses phase boundaries")
        text = row["text"]
        if (
            not isinstance(text, str)
            or not text
            or len(text) > MAX_TEXT_CODEPOINTS
            or "\x00" in text
            or any(0xD800 <= ord(character) <= 0xDFFF for character in text)
        ):
            raise FastTextChallengerError(f"Row {ordinal} text is invalid")
        prior_group = text_groups.setdefault(text, group_id)
        if prior_group != group_id:
            raise FastTextChallengerError("Identical corpus text crosses provenance groups")
        label = row["label"]
        if label not in LABELS:
            raise FastTextChallengerError(f"Row {ordinal} label is unsupported")
        adjudication = _object(
            row["adjudication"], ADJUDICATION_FIELDS, name=f"row {ordinal} adjudication"
        )
        if adjudication["method"] != "dual-independent-agreement" or adjudication["labels"] != [
            label,
            label,
        ]:
            raise FastTextChallengerError(f"Row {ordinal} lacks two agreeing labels")
        source_family = row["sourceFamily"]
        origin_family = row["originFamily"]
        script_family = row["scriptFamily"]
        length_band = row["lengthBand"]
        if source_family not in SOURCE_FAMILIES:
            raise FastTextChallengerError(f"Row {ordinal} source family is unsupported")
        if origin_family not in ORIGIN_FAMILIES:
            raise FastTextChallengerError(f"Row {ordinal} origin family is unsupported")
        if script_family not in SCRIPT_FAMILIES:
            raise FastTextChallengerError(f"Row {ordinal} script family is unsupported")
        if length_band not in LENGTH_BAND_NAMES or length_band != _length_band(text):
            raise FastTextChallengerError(f"Row {ordinal} length band is invalid")
        license_value = row["license"]
        if (
            not isinstance(license_value, str)
            or not 1 <= len(license_value) <= 64
            or any(ord(character) < 0x20 or ord(character) > 0x7E for character in license_value)
        ):
            raise FastTextChallengerError(f"Row {ordinal} license is invalid")
        rows.append(
            ChallengerRow(
                identifier=identifier,
                group_id=group_id,
                split=split,
                text=text,
                label=label,
                source_family=source_family,
                origin_family=origin_family,
                script_family=script_family,
                length_band=length_band,
            )
        )
    if [row.identifier for row in rows] != sorted(row.identifier for row in rows):
        raise FastTextChallengerError("Corpus rows must be sorted by identifier")
    for split in ("train", "calibration", "test"):
        labels = {row.label for row in rows if row.split == split}
        required = {"human-worthy", "machine", "mixed"}
        if split != "train":
            required.add("ambiguous")
        if not required <= labels:
            raise FastTextChallengerError(f"Corpus {split} split lacks required labels")
    return ChallengerCorpus(corpus_id=corpus_id, rows=tuple(rows), sha256=_sha256(raw))


def _parameter_integer(value: Any, minimum: int, maximum: int, *, name: str) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise FastTextChallengerError(f"{name} is outside its integer bounds")
    return value


def _parse_family(value: Any, ordinal: int) -> FastTextFamily:
    item = _object(value, FAMILY_FIELDS, name=f"family {ordinal}")
    identifier = _identifier(item["familyId"], name=f"family {ordinal} familyId")
    parameters = _object(item["parameters"], PARAMETER_FIELDS, name=f"family {ordinal} parameters")
    checked: dict[str, int | float | str] = {
        "bucket": _parameter_integer(parameters["bucket"], 1_000, 20_000_000, name="bucket"),
        "dim": _parameter_integer(parameters["dim"], 1, 1_024, name="dim"),
        "epoch": _parameter_integer(parameters["epoch"], 1, 1_000, name="epoch"),
        "loss": parameters["loss"],
        "lr": _finite(parameters["lr"], name="lr"),
        "maxn": _parameter_integer(parameters["maxn"], 1, 8, name="maxn"),
        "minCount": _parameter_integer(parameters["minCount"], 1, 1_000, name="minCount"),
        "minn": _parameter_integer(parameters["minn"], 1, 8, name="minn"),
        "thread": _parameter_integer(parameters["thread"], 1, 1, name="thread"),
        "wordNgrams": _parameter_integer(parameters["wordNgrams"], 1, 16, name="wordNgrams"),
        "ws": _parameter_integer(parameters["ws"], 1, 128, name="ws"),
    }
    if checked["loss"] != "softmax":
        raise FastTextChallengerError("fastText challenger loss must be softmax")
    if not 0 < checked["lr"] <= 10 or checked["minn"] > checked["maxn"]:
        raise FastTextChallengerError("fastText challenger parameters are invalid")
    config_sha256 = _hash(item["configSha256"], name="configSha256")
    expected_config = _sha256(_canonical_bytes({"familyId": identifier, "parameters": checked}))
    if config_sha256 != expected_config:
        raise FastTextChallengerError("fastText family configuration hash drifted")
    return FastTextFamily(
        identifier=identifier,
        parameters=checked,
        config_sha256=config_sha256,
        model_sha256=_hash(item["modelSha256"], name="modelSha256"),
    )


def load_source_lock(
    path: Path,
    expected_sha256: str,
    executable: Path,
    source_archive: Path,
) -> SourceLock:
    expected_sha256 = _hash(expected_sha256, name="expected source lock SHA-256")
    raw = _physical_file(path, name="fastText source lock")
    actual_sha256 = _sha256(raw)
    if actual_sha256 != expected_sha256:
        raise FastTextChallengerError("fastText source lock hash drifted")
    root = _object(_load_json(raw, name="fastText source lock"), LOCK_FIELDS, name="source lock")
    if (
        type(root["schemaVersion"]) is not int
        or root["schemaVersion"] != SCHEMA_VERSION
        or root["artifactType"] != LOCK_ARTIFACT
        or root["researchOnly"] is not True
        or root["promotionEligible"] is not False
    ):
        raise FastTextChallengerError("fastText source lock identity is unsupported")
    upstream = _object(root["upstream"], UPSTREAM_FIELDS, name="upstream")
    if (
        upstream["repository"] != OFFICIAL_REPOSITORY
        or upstream["license"] != "MIT"
        or not isinstance(upstream["revision"], str)
        or REVISION_PATTERN.fullmatch(upstream["revision"]) is None
    ):
        raise FastTextChallengerError("fastText upstream identity is unsupported")
    source_archive_sha256 = _hash(upstream["sourceSha256"], name="upstream sourceSha256")
    source_archive_bytes = _physical_file(
        source_archive,
        name="pinned fastText source archive",
        maximum=MAX_MODEL_BYTES,
    )
    if _sha256(source_archive_bytes) != source_archive_sha256:
        raise FastTextChallengerError("fastText source archive hash drifted")
    executable_value = _object(root["executable"], EXECUTABLE_FIELDS, name="executable")
    executable_sha256 = _hash(executable_value["sha256"], name="executable sha256")
    executable_bytes = _physical_file(
        executable, name="pinned fastText executable", maximum=MAX_MODEL_BYTES
    )
    if _sha256(executable_bytes) != executable_sha256:
        raise FastTextChallengerError("fastText executable hash drifted")
    version = executable_value["version"]
    if not isinstance(version, str) or not 1 <= len(version) <= 64:
        raise FastTextChallengerError("fastText executable version is invalid")
    fixed_arguments = executable_value["fixedArguments"]
    fixed_hashes = executable_value["fixedArgumentFileSha256"]
    if (
        not isinstance(fixed_arguments, list)
        or not isinstance(fixed_hashes, list)
        or len(fixed_arguments) != len(fixed_hashes)
        or len(fixed_arguments) > MAX_FIXED_ARGUMENTS
    ):
        raise FastTextChallengerError("fastText fixed arguments are invalid")
    checked_arguments: list[str] = []
    checked_hashes: list[str | None] = []
    for index, (argument, digest) in enumerate(zip(fixed_arguments, fixed_hashes, strict=True)):
        if (
            not isinstance(argument, str)
            or not argument
            or len(argument) > 1_024
            or "\x00" in argument
        ):
            raise FastTextChallengerError("fastText fixed argument is invalid")
        checked_arguments.append(argument)
        if digest is None:
            checked_hashes.append(None)
            continue
        checked_digest = _hash(digest, name=f"fixed argument {index} SHA-256")
        argument_bytes = _physical_file(Path(argument), name=f"fixed argument {index} file")
        if _sha256(argument_bytes) != checked_digest:
            raise FastTextChallengerError("fastText fixed argument file hash drifted")
        checked_hashes.append(checked_digest)
    family_values = root["families"]
    if not isinstance(family_values, list) or not 1 <= len(family_values) <= MAX_FAMILIES:
        raise FastTextChallengerError("fastText family count is outside bounds")
    families = tuple(_parse_family(item, index) for index, item in enumerate(family_values, 1))
    identifiers = [family.identifier for family in families]
    if identifiers != sorted(identifiers) or len(set(identifiers)) != len(identifiers):
        raise FastTextChallengerError("fastText families must have sorted unique identifiers")
    return SourceLock(
        sha256=actual_sha256,
        source_archive_sha256=source_archive_sha256,
        executable_sha256=executable_sha256,
        fixed_arguments=tuple(checked_arguments),
        fixed_argument_hashes=tuple(checked_hashes),
        families=families,
    )


def _line_text(text: str) -> str:
    return text.replace("\r", " ").replace("\n", " ").replace("\t", " ")


def _train_export(rows: Sequence[ChallengerRow]) -> bytes:
    lines = []
    for row in rows:
        if row.label == "ambiguous":
            continue
        label = "human" if row.label in {"human-worthy", "mixed"} else "machine"
        lines.append(f"__label__{label} {_line_text(row.text)}\n")
    if not lines:
        raise FastTextChallengerError("Training export contains no labelled rows")
    return "".join(lines).encode("utf-8", errors="strict")


def _score_input(rows: Sequence[ChallengerRow]) -> bytes:
    if not rows:
        raise FastTextChallengerError("Scoring input contains no rows")
    return "".join(f"{_line_text(row.text)}\n" for row in rows).encode("utf-8", errors="strict")


def _family_arguments(family: FastTextFamily) -> list[str]:
    parameters = family.parameters
    order = (
        "bucket",
        "dim",
        "epoch",
        "loss",
        "lr",
        "maxn",
        "minCount",
        "minn",
        "thread",
        "wordNgrams",
        "ws",
    )
    return [value for name in order for value in (f"-{name}", str(parameters[name]))]


def _environment() -> Mapping[str, str]:
    environment = {
        name: os.environ[name]
        for name in ("SYSTEMROOT", "WINDIR", "TEMP", "TMP")
        if name in os.environ
    }
    environment.update({"LC_ALL": "C", "LANG": "C"})
    return environment


def _run(
    executable: Path,
    source_lock: SourceLock,
    arguments: Sequence[str],
    *,
    cwd: Path,
    stdout: Any = subprocess.DEVNULL,
) -> None:
    command = [str(Path(os.path.abspath(executable))), *source_lock.fixed_arguments, *arguments]
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            env=_environment(),
            stdin=subprocess.DEVNULL,
            stdout=stdout,
            stderr=subprocess.DEVNULL,
            shell=False,
            check=False,
            timeout=PROCESS_TIMEOUT_SECONDS,
        )
    except (OSError, subprocess.SubprocessError) as exc:
        raise FastTextChallengerError("Pinned fastText process did not complete") from exc
    if completed.returncode != 0:
        raise FastTextChallengerError("Pinned fastText process returned failure")


def _train_family(
    executable: Path,
    source_lock: SourceLock,
    family: FastTextFamily,
    train_path: Path,
    root: Path,
) -> Path:
    prefix = root / family.identifier
    _run(
        executable,
        source_lock,
        [
            "supervised",
            "-input",
            str(train_path),
            "-output",
            str(prefix),
            *_family_arguments(family),
        ],
        cwd=root,
    )
    model_path = prefix.with_suffix(".bin")
    model_bytes = _physical_file(
        model_path, name=f"fastText model {family.identifier}", maximum=MAX_MODEL_BYTES
    )
    if _sha256(model_bytes) != family.model_sha256:
        raise FastTextChallengerError("Trained fastText model hash drifted")
    return model_path


def _parse_probabilities(raw: bytes, expected_rows: int) -> tuple[float, ...]:
    try:
        lines = raw.decode("utf-8", errors="strict").splitlines()
    except UnicodeDecodeError as exc:
        raise FastTextChallengerError("fastText prediction output is not strict UTF-8") from exc
    if len(lines) != expected_rows:
        raise FastTextChallengerError("fastText prediction cardinality is invalid")
    scores: list[float] = []
    for line in lines:
        tokens = line.split()
        if len(tokens) != 4:
            raise FastTextChallengerError("fastText prediction row is malformed")
        labels: dict[str, float] = {}
        for index in (0, 2):
            label = tokens[index]
            if label not in {"__label__human", "__label__machine"} or label in labels:
                raise FastTextChallengerError("fastText prediction labels are invalid")
            try:
                probability = float(tokens[index + 1])
            except ValueError as exc:
                raise FastTextChallengerError("fastText probability is invalid") from exc
            if not math.isfinite(probability) or not 0 <= probability <= 1:
                raise FastTextChallengerError("fastText probability is outside bounds")
            labels[label] = probability
        if set(labels) != {"__label__human", "__label__machine"}:
            raise FastTextChallengerError("fastText prediction labels are incomplete")
        if abs(sum(labels.values()) - 1.0) > 0.001:
            raise FastTextChallengerError("fastText prediction probabilities do not sum to one")
        scores.append(labels["__label__human"])
    return tuple(scores)


def _score(
    executable: Path,
    source_lock: SourceLock,
    model_path: Path,
    input_path: Path,
    row_count: int,
    root: Path,
) -> tuple[float, ...]:
    output_path = root / f"{input_path.stem}.probabilities"
    with output_path.open("wb") as output:
        _run(
            executable,
            source_lock,
            ["predict-prob", str(model_path), str(input_path), "2"],
            cwd=root,
            stdout=output,
        )
    raw = _physical_file(output_path, name="fastText prediction output")
    return _parse_probabilities(raw, row_count)


def _thresholds(rows: Sequence[ChallengerRow], scores: Sequence[float]) -> tuple[float, float]:
    protected = [
        score
        for row, score in zip(rows, scores, strict=True)
        if row.label in {"human-worthy", "mixed", "ambiguous"}
    ]
    ambiguous = [score for row, score in zip(rows, scores, strict=True) if row.label == "ambiguous"]
    if not protected or not ambiguous:
        raise FastTextChallengerError("Calibration lacks protected or ambiguous rows")
    suppress_max = math.nextafter(min(protected), -math.inf)
    retain_min = math.nextafter(max(ambiguous), math.inf)
    if (
        not math.isfinite(suppress_max)
        or not math.isfinite(retain_min)
        or suppress_max >= retain_min
        or retain_min > 1
    ):
        raise FastTextChallengerError("Calibration could not produce safe abstention thresholds")
    return suppress_max, retain_min


def _decision(score: float, suppress_max: float, retain_min: float) -> str:
    if score <= suppress_max:
        return "suppress"
    if score >= retain_min:
        return "retain"
    return "abstain"


def _candidate(
    family: FastTextFamily,
    rows: Sequence[ChallengerRow],
    scores: Sequence[float],
) -> Mapping[str, Any]:
    suppress_max, retain_min = _thresholds(rows, scores)
    decisions = [_decision(score, suppress_max, retain_min) for score in scores]
    known_human = {"human-worthy", "mixed"}
    false_suppressions = sum(
        decision == "suppress" and row.label in known_human
        for row, decision in zip(rows, decisions, strict=True)
    )
    ambiguous_decisive = sum(
        decision != "abstain" and row.label == "ambiguous"
        for row, decision in zip(rows, decisions, strict=True)
    )
    if false_suppressions or ambiguous_decisive:
        raise FastTextChallengerError("Calibration candidate violates protected semantics")
    return {
        "familyId": family.identifier,
        "records": len(rows),
        "knownHumanRecords": sum(row.label in known_human for row in rows),
        "machineRecords": sum(row.label == "machine" for row in rows),
        "ambiguousRecords": sum(row.label == "ambiguous" for row in rows),
        "machineSuppressions": sum(
            decision == "suppress" and row.label == "machine"
            for row, decision in zip(rows, decisions, strict=True)
        ),
        "positiveRetentions": sum(
            decision == "retain" and row.label in known_human
            for row, decision in zip(rows, decisions, strict=True)
        ),
        "abstentions": Counter(decisions)["abstain"],
        "falseSuppressions": false_suppressions,
        "ambiguousDecisive": ambiguous_decisive,
        "suppressMax": suppress_max,
        "retainMin": retain_min,
    }


def _candidate_rank(value: Mapping[str, Any]) -> tuple[int, int, int, str]:
    return (
        -int(value["machineSuppressions"]),
        -int(value["positiveRetentions"]),
        int(value["abstentions"]),
        str(value["familyId"]),
    )


def _phase_hashes(corpus: ChallengerCorpus) -> tuple[bytes, bytes, bytes, Mapping[str, str]]:
    train = _train_export(corpus.split("train"))
    calibration = _score_input(corpus.split("calibration"))
    test = _score_input(corpus.split("test"))
    return (
        train,
        calibration,
        test,
        {
            "trainExportSha256": _sha256(train),
            "calibrationInputSha256": _sha256(calibration),
            "lockedTestInputSha256": _sha256(test),
        },
    )


def calibrate(
    corpus_path: Path,
    source_lock_path: Path,
    expected_lock_sha256: str,
    executable: Path,
    source_archive: Path,
) -> tuple[bytes, bytes]:
    corpus = load_corpus(corpus_path)
    source_lock = load_source_lock(
        source_lock_path,
        expected_lock_sha256,
        executable,
        source_archive,
    )
    orchestrator_sha256 = _sha256(
        _physical_file(Path(__file__), name="fastText challenger orchestrator")
    )
    train, calibration_input, _, phase_hashes = _phase_hashes(corpus)
    calibration_rows = corpus.split("calibration")
    with tempfile.TemporaryDirectory(prefix="bstrings-fasttext-calibration-") as temporary_name:
        root = Path(temporary_name)
        train_path = root / "train.txt"
        calibration_path = root / "calibration.txt"
        train_path.write_bytes(train)
        calibration_path.write_bytes(calibration_input)
        candidates: list[Mapping[str, Any]] = []
        models: dict[str, bytes] = {}
        for family in source_lock.families:
            model_path = _train_family(executable, source_lock, family, train_path, root)
            scores = _score(
                executable,
                source_lock,
                model_path,
                calibration_path,
                len(calibration_rows),
                root,
            )
            candidates.append(_candidate(family, calibration_rows, scores))
            models[family.identifier] = model_path.read_bytes()
        selected_candidate = min(candidates, key=_candidate_rank)
        selected_family = next(
            family
            for family in source_lock.families
            if family.identifier == selected_candidate["familyId"]
        )
        model_bytes = models[selected_family.identifier]
    plan = {
        "schemaVersion": SCHEMA_VERSION,
        "artifactType": PLAN_ARTIFACT,
        "researchOnly": True,
        "promotionEligible": False,
        "state": "calibration-locked",
        "corpusId": corpus.corpus_id,
        "corpusSha256": corpus.sha256,
        "sourceLockSha256": source_lock.sha256,
        "sourceArchiveSha256": source_lock.source_archive_sha256,
        "executableSha256": source_lock.executable_sha256,
        "orchestratorSha256": orchestrator_sha256,
        "exportContract": EXPORT_CONTRACT,
        "phaseHashes": dict(phase_hashes),
        "selected": {
            "familyId": selected_family.identifier,
            "configSha256": selected_family.config_sha256,
            "modelSha256": selected_family.model_sha256,
            "modelBytes": len(model_bytes),
            "suppressMax": selected_candidate["suppressMax"],
            "retainMin": selected_candidate["retainMin"],
            "scoreOrientation": SCORE_ORIENTATION,
        },
        "calibration": {
            "objective": SELECTION_OBJECTIVE,
            "candidates": sorted(candidates, key=lambda value: str(value["familyId"])),
        },
    }
    return model_bytes, _canonical_bytes(plan)


def _load_plan(
    path: Path,
    expected_sha256: str,
    corpus: ChallengerCorpus,
    source_lock: SourceLock,
    model_path: Path,
) -> Mapping[str, Any]:
    raw = _physical_file(path, name="locked fastText calibration plan")
    if _sha256(raw) != _hash(expected_sha256, name="expected plan SHA-256"):
        raise FastTextChallengerError("Locked calibration plan hash drifted")
    plan = _object(
        _load_json(raw, name="locked fastText calibration plan"), PLAN_FIELDS, name="plan"
    )
    if (
        type(plan["schemaVersion"]) is not int
        or plan["schemaVersion"] != SCHEMA_VERSION
        or plan["artifactType"] != PLAN_ARTIFACT
        or plan["researchOnly"] is not True
        or plan["promotionEligible"] is not False
        or plan["state"] != "calibration-locked"
        or plan["corpusId"] != corpus.corpus_id
        or plan["corpusSha256"] != corpus.sha256
        or plan["sourceLockSha256"] != source_lock.sha256
        or plan["sourceArchiveSha256"] != source_lock.source_archive_sha256
        or plan["executableSha256"] != source_lock.executable_sha256
        or plan["orchestratorSha256"]
        != _sha256(_physical_file(Path(__file__), name="fastText challenger orchestrator"))
        or plan["exportContract"] != EXPORT_CONTRACT
    ):
        raise FastTextChallengerError("Locked calibration plan identity drifted")
    _, _, _, expected_phase_hashes = _phase_hashes(corpus)
    phase_hashes = _object(plan["phaseHashes"], PHASE_HASH_FIELDS, name="phaseHashes")
    if dict(phase_hashes) != expected_phase_hashes:
        raise FastTextChallengerError("Locked phase input hashes drifted")
    selected = _object(plan["selected"], SELECTED_FIELDS, name="selected")
    family_id = _identifier(selected["familyId"], name="selected familyId")
    family = next(
        (candidate for candidate in source_lock.families if candidate.identifier == family_id), None
    )
    if family is None:
        raise FastTextChallengerError("Locked family is absent from the source lock")
    suppress_max = _finite(selected["suppressMax"], name="suppressMax")
    retain_min = _finite(selected["retainMin"], name="retainMin")
    model_bytes = _physical_file(
        model_path, name="selected fastText model", maximum=MAX_MODEL_BYTES
    )
    if (
        selected["configSha256"] != family.config_sha256
        or selected["modelSha256"] != family.model_sha256
        or type(selected["modelBytes"]) is not int
        or selected["modelBytes"] != len(model_bytes)
        or selected["scoreOrientation"] != SCORE_ORIENTATION
        or _sha256(model_bytes) != family.model_sha256
        or not 0 <= suppress_max < retain_min <= 1
    ):
        raise FastTextChallengerError("Locked selected model or threshold identity drifted")
    calibration = _object(plan["calibration"], CALIBRATION_FIELDS, name="calibration")
    if calibration["objective"] != SELECTION_OBJECTIVE:
        raise FastTextChallengerError("Locked calibration objective drifted")
    candidates = calibration["candidates"]
    if not isinstance(candidates, list) or len(candidates) != len(source_lock.families):
        raise FastTextChallengerError("Locked calibration candidates are incomplete")
    candidate_ids: list[str] = []
    for ordinal, value in enumerate(candidates, start=1):
        candidate = _object(value, CANDIDATE_FIELDS, name=f"candidate {ordinal}")
        candidate_ids.append(_identifier(candidate["familyId"], name="candidate familyId"))
        for count_name in (
            "records",
            "knownHumanRecords",
            "machineRecords",
            "ambiguousRecords",
            "machineSuppressions",
            "positiveRetentions",
            "abstentions",
            "falseSuppressions",
            "ambiguousDecisive",
        ):
            if type(candidate[count_name]) is not int or candidate[count_name] < 0:
                raise FastTextChallengerError("Locked calibration count is invalid")
        _finite(candidate["suppressMax"], name="candidate suppressMax")
        _finite(candidate["retainMin"], name="candidate retainMin")
    if candidate_ids != [family.identifier for family in source_lock.families]:
        raise FastTextChallengerError("Locked calibration family order drifted")
    if min(candidates, key=_candidate_rank)["familyId"] != family_id:
        raise FastTextChallengerError("Locked family does not match the calibration objective")
    return plan


def locked_test(
    corpus_path: Path,
    source_lock_path: Path,
    expected_lock_sha256: str,
    executable: Path,
    source_archive: Path,
    model_path: Path,
    plan_path: Path,
    expected_plan_sha256: str,
) -> bytes:
    corpus = load_corpus(corpus_path)
    source_lock = load_source_lock(
        source_lock_path,
        expected_lock_sha256,
        executable,
        source_archive,
    )
    plan = _load_plan(
        plan_path,
        expected_plan_sha256,
        corpus,
        source_lock,
        model_path,
    )
    selected = plan["selected"]
    test_rows = corpus.split("test")
    test_input = _score_input(test_rows)
    with tempfile.TemporaryDirectory(prefix="bstrings-fasttext-locked-test-") as temporary_name:
        root = Path(temporary_name)
        input_path = root / "locked-test.txt"
        input_path.write_bytes(test_input)
        # The one external batch below receives no labels.  Its scores and
        # decisions therefore cannot depend on the locked-test labels.
        scores = _score(
            executable,
            source_lock,
            Path(os.path.abspath(model_path)),
            input_path,
            len(test_rows),
            root,
        )
    suppress_max = float(selected["suppressMax"])
    retain_min = float(selected["retainMin"])
    predictions = {
        row.identifier: Prediction(
            score=score,
            decision=_decision(score, suppress_max, retain_min),
        )
        for row, score in zip(test_rows, scores, strict=True)
    }
    evaluator_rows = tuple(row.evaluator_row() for row in corpus.rows)
    split_by_group = {row.group_id: row.split for row in corpus.rows}
    report = evaluation_report(
        ValidatedCorpus(rows=evaluator_rows, split_by_group=split_by_group),
        predictions,
        phase="locked-test",
    )
    report["corpusId"] = corpus.corpus_id
    report["license"] = REPORT_LICENSE
    report["confidenceUnit"] = "dual-adjudicated-group-diagnostic-only"
    report["experiment"] = {
        "experimentId": "fasttext-character-ngram-challenger-v1",
        "ablation": "fasttext-character-ngrams-only",
        "scoreOrientation": SCORE_ORIENTATION,
        "thresholds": {"suppressMax": suppress_max, "retainMin": retain_min},
        "hashes": {
            "corpusSha256": corpus.sha256,
            "sourceLockSha256": source_lock.sha256,
            "sourceArchiveSha256": source_lock.source_archive_sha256,
            "executableSha256": source_lock.executable_sha256,
            "orchestratorSha256": plan["orchestratorSha256"],
            "modelSha256": selected["modelSha256"],
            "configSha256": selected["configSha256"],
            "planSha256": _sha256(_physical_file(plan_path, name="locked plan")),
            "lockedTestInputSha256": _sha256(test_input),
        },
        "modelFamily": selected["familyId"],
    }
    report["calibrationSelection"] = plan["calibration"]
    return _canonical_bytes(report)


def _common_arguments(parser: argparse.ArgumentParser) -> None:
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--source-lock", type=Path, required=True)
    parser.add_argument("--source-lock-sha256", required=True)
    parser.add_argument("--fasttext-executable", type=Path, required=True)
    parser.add_argument("--fasttext-source-archive", type=Path, required=True)


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    calibration = commands.add_parser("calibrate")
    _common_arguments(calibration)
    calibration.add_argument("--model-output", type=Path, required=True)
    calibration.add_argument("--plan-output", type=Path, required=True)
    test = commands.add_parser("locked-test")
    _common_arguments(test)
    test.add_argument("--model", type=Path, required=True)
    test.add_argument("--plan", type=Path, required=True)
    test.add_argument("--plan-sha256", required=True)
    test.add_argument("--report-output", type=Path, required=True)
    return parser.parse_args(arguments)


def main(arguments: Sequence[str] | None = None) -> int:
    try:
        options = parse_arguments(arguments)
        common_inputs = [
            options.corpus,
            options.source_lock,
            options.fasttext_executable,
            options.fasttext_source_archive,
        ]
        if options.command == "calibrate":
            model_output = _safe_output(
                options.model_output, common_inputs, name="selected model output"
            )
            plan_output = _safe_output(
                options.plan_output, [*common_inputs, model_output], name="locked plan output"
            )
            model_bytes, plan_bytes = calibrate(
                options.corpus,
                options.source_lock,
                options.source_lock_sha256,
                options.fasttext_executable,
                options.fasttext_source_archive,
            )
            _publish(model_output, model_bytes)
            # The plan is the commit marker and is published after its model.
            _publish(plan_output, plan_bytes)
        else:
            inputs = [*common_inputs, options.model, options.plan]
            report_output = _safe_output(
                options.report_output, inputs, name="locked test report output"
            )
            report_bytes = locked_test(
                options.corpus,
                options.source_lock,
                options.source_lock_sha256,
                options.fasttext_executable,
                options.fasttext_source_archive,
                options.model,
                options.plan,
                options.plan_sha256,
            )
            _publish(report_output, report_bytes)
        return 0
    except FastTextChallengerError as exc:
        print(f"translation-worthiness fastText error: {exc}", file=sys.stderr)
        return 2
    except OSError:
        print("translation-worthiness fastText error: filesystem operation failed", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
