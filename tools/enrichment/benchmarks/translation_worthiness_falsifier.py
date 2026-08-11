#!/usr/bin/env python3
"""Adversarial, benchmark-only checks for translation-worthiness experiments.

This module has no product authority.  It validates locked research artifacts,
reports aggregate split-contamination diagnostics, evaluates mandatory synthetic
counterexamples, and replays a paired fake-translator counterfactual through the
existing Python translation worker.  It never emits row IDs or source text.
"""

from __future__ import annotations

import json
import math
import re
import sys
import unicodedata
from collections import Counter, defaultdict
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ENRICHMENT_ROOT = Path(__file__).resolve().parents[1]
if str(ENRICHMENT_ROOT) not in sys.path:
    sys.path.insert(0, str(ENRICHMENT_ROOT))

from bstrings_enrich import (  # noqa: E402
    TranslationOutcome,
    TranslationWorkStats,
    translate_normalized_records,
)
from translation_worthiness_corpus import (  # noqa: E402
    DECISIONS,
    Prediction,
    ValidatedCorpus,
)

SCHEMA_VERSION = 1
LOCKED_ARTIFACT_TYPE = "translation-worthiness-locked-predictions"
LOCKED_PHASE = "locked-test"
HASH_NAMES = ("corpus", "features", "model", "policy", "splits")
HASH_PATTERN = re.compile(r"^[0-9a-f]{64}$", re.ASCII)
IDENTIFIER_PATTERN = re.compile(r"^[a-z0-9][a-z0-9._|-]{0,159}$", re.ASCII)
MAX_ARTIFACT_BYTES = 16 * 1024 * 1024
MAX_PREDICTIONS = 100_000

MANDATORY_COUNTEREXAMPLE_FAMILIES = (
    "bidi-zero-width",
    "chunk-boundary-human",
    "code-comment",
    "code-docstring",
    "config-human-value",
    "encoded-decoded-child",
    "floss-decoded-human",
    "format-string",
    "localized-url-path",
    "machine-control",
    "machine-wrapper-middle",
    "machine-wrapper-prefix",
    "machine-wrapper-suffix",
    "normalization-variant",
    "ocr-damage",
    "registry-human-value",
    "short-arabic",
    "short-cyrillic",
    "short-han",
    "unsupported-ambiguous",
)
COUNTEREXAMPLE_LABELS = ("ambiguous", "human-worthy", "machine", "mixed")
COUNTEREXAMPLE_FIELDS = frozenset({"schemaVersion", "id", "text", "label", "family", "license"})
DEFAULT_COUNTEREXAMPLES = Path(__file__).with_name(
    "translation_worthiness_mandatory_counterexamples_v1.jsonl"
)

ARTIFACT_FIELDS = frozenset(
    {
        "schemaVersion",
        "artifactType",
        "researchOnly",
        "promotionEligible",
        "phase",
        "frozenBeforeTest",
        "identities",
        "thresholds",
        "preregistration",
        "sources",
        "privateVeto",
        "predictions",
    }
)
THRESHOLD_FIELDS = frozenset({"retainMax", "suppressMin"})
PREREGISTRATION_FIELDS = frozenset({"cells", "k", "familyWiseAlpha", "method", "perCellAlpha"})
SOURCE_FIELDS = frozenset({"kind", "license", "use", "manualLabels", "revision"})
PRIVATE_VETO_FIELDS = frozenset({"status", "influence"})
PREDICTION_FIELDS = frozenset({"id", "score", "decision"})


class FalsifierError(RuntimeError):
    """Raised when benchmark evidence violates a frozen safety contract."""


@dataclass(frozen=True)
class ContaminationPolicy:
    """Optional explicit gates; ``None`` leaves a metric diagnostic-only."""

    maximum_normalized_exact_pairs: int | None = None
    maximum_token_shingle_jaccard: float | None = None
    maximum_char_ngram_jaccard: float | None = None


@dataclass(frozen=True)
class Counterexample:
    identifier: str
    text: str
    label: str
    family: str


@dataclass(frozen=True)
class LockedPredictions:
    thresholds: tuple[float, float]
    predictions: Mapping[str, Prediction]
    preregistered_k: int
    source_count: int
    private_veto_status: str


def _unique_object(pairs: Sequence[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise FalsifierError("JSON contains a duplicate object property")
        value[key] = item
    return value


def _reject_constant(value: str) -> None:
    del value
    raise FalsifierError("JSON contains a non-finite numeric constant")


def _load_json_bytes(raw: bytes, *, name: str) -> Any:
    try:
        return json.loads(
            raw.decode("utf-8", errors="strict"),
            object_pairs_hook=_unique_object,
            parse_constant=_reject_constant,
        )
    except FalsifierError:
        raise
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise FalsifierError(f"{name} is not strict UTF-8 JSON") from exc


def _physical_bounded_file(path: Path, *, name: str) -> bytes:
    resolved = path.resolve(strict=False)
    if not path.is_file() or path.is_symlink() or resolved.is_symlink():
        raise FalsifierError(f"{name} must be a physical file")
    size = resolved.stat().st_size
    if size <= 0 or size > MAX_ARTIFACT_BYTES:
        raise FalsifierError(f"{name} is empty or exceeds the bounded size")
    return resolved.read_bytes()


def _finite_unit_interval(value: Any, *, name: str) -> float:
    if (
        isinstance(value, bool)
        or not isinstance(value, (int, float))
        or not math.isfinite(float(value))
        or not 0 <= float(value) <= 1
    ):
        raise FalsifierError(f"{name} must be a finite number from zero through one")
    return float(value)


def _normalized_text(text: str) -> str:
    normalized = unicodedata.normalize("NFKC", text).casefold()
    return " ".join(normalized.split())


def _tokens(text: str) -> tuple[str, ...]:
    return tuple(re.findall(r"\w+|[^\w\s]", _normalized_text(text), flags=re.UNICODE))


def _shingles(values: Sequence[str], width: int) -> set[tuple[str, ...]]:
    if not values:
        return set()
    if len(values) < width:
        return {tuple(values)}
    return {tuple(values[index : index + width]) for index in range(len(values) - width + 1)}


def _character_ngrams(text: str, width: int) -> set[str]:
    value = _normalized_text(text)
    if not value:
        return set()
    if len(value) < width:
        return {value}
    return {value[index : index + width] for index in range(len(value) - width + 1)}


def _jaccard(left: set[Any], right: set[Any]) -> float:
    if not left or not right:
        return 0.0
    return len(left & right) / len(left | right)


def contamination_diagnostics(
    corpus: ValidatedCorpus,
    policy: ContaminationPolicy | None = None,
) -> dict[str, Any]:
    """Return aggregate cross-split relatedness diagnostics.

    The bundled seed remains diagnostic-only.  A caller must pass explicit
    thresholds to turn any of these heuristics into a failing policy gate.
    """

    normalized_by_group: dict[str, set[str]] = defaultdict(set)
    token_by_group: dict[str, set[tuple[str, ...]]] = defaultdict(set)
    chars_by_group: dict[str, set[str]] = defaultdict(set)
    for row in corpus.rows:
        normalized_by_group[row.group_id].add(_normalized_text(row.text))
        token_by_group[row.group_id].update(_shingles(_tokens(row.text), 3))
        chars_by_group[row.group_id].update(_character_ngrams(row.text, 5))

    groups = sorted(corpus.split_by_group)
    split_pair_counts: Counter[str] = Counter()
    compared_pairs = 0
    normalized_exact_pairs = 0
    token_overlap_pairs = 0
    char_overlap_pairs = 0
    maximum_token = 0.0
    maximum_chars = 0.0
    for left_index, left_group in enumerate(groups):
        left_split = corpus.split_by_group[left_group]
        for right_group in groups[left_index + 1 :]:
            right_split = corpus.split_by_group[right_group]
            if left_split == right_split:
                continue
            compared_pairs += 1
            split_pair_counts["|".join(sorted((left_split, right_split)))] += 1
            if normalized_by_group[left_group] & normalized_by_group[right_group]:
                normalized_exact_pairs += 1
            token_score = _jaccard(token_by_group[left_group], token_by_group[right_group])
            char_score = _jaccard(chars_by_group[left_group], chars_by_group[right_group])
            maximum_token = max(maximum_token, token_score)
            maximum_chars = max(maximum_chars, char_score)
            token_overlap_pairs += token_score > 0
            char_overlap_pairs += char_score > 0

    violations: list[str] = []
    if policy is not None:
        if (
            policy.maximum_normalized_exact_pairs is not None
            and normalized_exact_pairs > policy.maximum_normalized_exact_pairs
        ):
            violations.append("normalized-exact")
        if (
            policy.maximum_token_shingle_jaccard is not None
            and maximum_token > policy.maximum_token_shingle_jaccard
        ):
            violations.append("token-shingle")
        if (
            policy.maximum_char_ngram_jaccard is not None
            and maximum_chars > policy.maximum_char_ngram_jaccard
        ):
            violations.append("character-ngram")
        if violations:
            raise FalsifierError("Cross-split contamination exceeds the explicit policy")

    return {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-contamination-diagnostics",
        "researchOnly": True,
        "promotionEligible": False,
        "policyMode": "diagnostic-only" if policy is None else "explicit-gate",
        "crossSplitGroupPairs": compared_pairs,
        "splitPairCounts": dict(sorted(split_pair_counts.items())),
        "normalizedExactOverlapPairs": normalized_exact_pairs,
        "tokenShingleOverlapPairs": token_overlap_pairs,
        "characterNgramOverlapPairs": char_overlap_pairs,
        "maximumTokenShingleJaccard": round(maximum_token, 12),
        "maximumCharacterNgramJaccard": round(maximum_chars, 12),
    }


def load_counterexamples(path: Path = DEFAULT_COUNTEREXAMPLES) -> tuple[Counterexample, ...]:
    raw = _physical_bounded_file(path, name="mandatory counterexample corpus")
    if not raw.endswith(b"\n"):
        raise FalsifierError("Mandatory counterexample corpus must end with a newline")
    rows: list[Counterexample] = []
    identifiers: set[str] = set()
    families: Counter[str] = Counter()
    for ordinal, line in enumerate(raw.splitlines(), start=1):
        value = _load_json_bytes(line, name=f"mandatory counterexample row {ordinal}")
        if not isinstance(value, dict) or frozenset(value) != COUNTEREXAMPLE_FIELDS:
            raise FalsifierError("Mandatory counterexample row does not use the allowlist")
        if type(value["schemaVersion"]) is not int or value["schemaVersion"] != SCHEMA_VERSION:
            raise FalsifierError("Mandatory counterexample row has an invalid schema version")
        identifier = value["id"]
        family = value["family"]
        label = value["label"]
        text = value["text"]
        if (
            not isinstance(identifier, str)
            or IDENTIFIER_PATTERN.fullmatch(identifier) is None
            or identifier in identifiers
        ):
            raise FalsifierError("Mandatory counterexample row has an invalid or duplicate id")
        if family not in MANDATORY_COUNTEREXAMPLE_FAMILIES:
            raise FalsifierError("Mandatory counterexample row has an invalid family")
        if label not in COUNTEREXAMPLE_LABELS:
            raise FalsifierError("Mandatory counterexample row has an invalid label")
        if value["license"] != "project-synthetic":
            raise FalsifierError("Mandatory counterexample row has an invalid licence")
        if not isinstance(text, str) or not text or "\x00" in text or len(text) > 2_048:
            raise FalsifierError("Mandatory counterexample row has invalid text")
        identifiers.add(identifier)
        families[family] += 1
        rows.append(Counterexample(identifier, text, label, family))
    if set(families) != set(MANDATORY_COUNTEREXAMPLE_FAMILIES):
        raise FalsifierError("Mandatory counterexample families are incomplete")
    if any(families[family] < 1 for family in MANDATORY_COUNTEREXAMPLE_FAMILIES):
        raise FalsifierError("Mandatory counterexample family is empty")
    return tuple(rows)


def counterexample_report(
    rows: Sequence[Counterexample], predictions: Mapping[str, Prediction]
) -> dict[str, Any]:
    expected = {row.identifier for row in rows}
    if set(predictions) != expected:
        raise FalsifierError("Counterexample predictions do not exactly cover the corpus")
    protected = [row for row in rows if row.label != "machine"]
    false_suppressions = sum(
        predictions[row.identifier].decision == "suppress" for row in protected
    )
    if false_suppressions:
        raise FalsifierError("A mandatory human, mixed, or ambiguous counterexample was suppressed")
    decisions = Counter(predictions[row.identifier].decision for row in rows)
    return {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-mandatory-counterexamples",
        "researchOnly": True,
        "promotionEligible": False,
        "records": len(rows),
        "families": len({row.family for row in rows}),
        "protectedRecords": len(protected),
        "falseSuppressions": false_suppressions,
        "decisions": {decision: decisions[decision] for decision in DECISIONS},
    }


def _validate_source(source: Any) -> None:
    if not isinstance(source, dict) or frozenset(source) != SOURCE_FIELDS:
        raise FalsifierError("Source provenance does not use the allowlist")
    kind = source["kind"]
    license_name = source["license"]
    use = source["use"]
    manual = source["manualLabels"]
    revision = source["revision"]
    if not all(isinstance(item, str) and item for item in (kind, license_name, use, revision)):
        raise FalsifierError("Source provenance contains an invalid string")
    if type(manual) is not bool:
        raise FalsifierError("Source provenance manual-label state is invalid")
    if kind == "project-synthetic":
        valid = license_name == "project-synthetic" and manual
    elif kind == "massive":
        valid = license_name == "CC-BY-4.0" and use == "clean-positive" and manual
    elif kind in {"commonlid", "glotocr"}:
        valid = use == "evaluation-only" and manual
    elif kind == "codesearchnet":
        valid = (
            use == "manually-labelled"
            and manual
            and license_name not in {"MIT", "unknown", "unrecorded"}
        )
    else:
        valid = False
    if not valid or kind == "nlon":
        raise FalsifierError("Source provenance violates the frozen licence or usage policy")


def load_locked_predictions(
    path: Path,
    *,
    expected_ids: set[str],
    expected_identities: Mapping[str, str],
) -> LockedPredictions:
    """Validate a preregistered locked-test prediction artifact.

    Identity values are checked but are intentionally not returned in reports.
    """

    value = _load_json_bytes(
        _physical_bounded_file(path, name="locked prediction artifact"),
        name="locked prediction artifact",
    )
    if not isinstance(value, dict) or frozenset(value) != ARTIFACT_FIELDS:
        raise FalsifierError("Locked prediction artifact does not use the allowlist")
    if (
        type(value["schemaVersion"]) is not int
        or value["schemaVersion"] != SCHEMA_VERSION
        or value["artifactType"] != LOCKED_ARTIFACT_TYPE
        or value["researchOnly"] is not True
        or value["promotionEligible"] is not False
        or value["phase"] != LOCKED_PHASE
        or value["frozenBeforeTest"] is not True
    ):
        raise FalsifierError("Locked prediction artifact has invalid research or phase identity")

    identities = value["identities"]
    if not isinstance(identities, dict) or tuple(sorted(identities)) != HASH_NAMES:
        raise FalsifierError("Locked prediction identities do not use the allowlist")
    if tuple(sorted(expected_identities)) != HASH_NAMES:
        raise FalsifierError("Expected identity set does not use the allowlist")
    for name in HASH_NAMES:
        identity = identities[name]
        expected = expected_identities[name]
        if (
            not isinstance(identity, str)
            or HASH_PATTERN.fullmatch(identity) is None
            or not isinstance(expected, str)
            or HASH_PATTERN.fullmatch(expected) is None
            or identity != expected
        ):
            raise FalsifierError("Locked prediction identity is malformed or mismatched")

    thresholds = value["thresholds"]
    if not isinstance(thresholds, dict) or frozenset(thresholds) != THRESHOLD_FIELDS:
        raise FalsifierError("Locked thresholds do not use the allowlist")
    retain_max = _finite_unit_interval(thresholds["retainMax"], name="retainMax")
    suppress_min = _finite_unit_interval(thresholds["suppressMin"], name="suppressMin")
    if retain_max >= suppress_min:
        raise FalsifierError("Locked thresholds do not define an abstention interval")

    preregistration = value["preregistration"]
    if (
        not isinstance(preregistration, dict)
        or frozenset(preregistration) != PREREGISTRATION_FIELDS
    ):
        raise FalsifierError("Preregistration does not use the allowlist")
    cells = preregistration["cells"]
    k = preregistration["k"]
    if (
        not isinstance(cells, list)
        or not cells
        or not all(
            isinstance(cell, str) and IDENTIFIER_PATTERN.fullmatch(cell) is not None
            for cell in cells
        )
        or cells != sorted(cells)
        or len(cells) != len(set(cells))
        or type(k) is not int
        or k != len(cells)
    ):
        raise FalsifierError("Preregistered cells or K are invalid")
    family_alpha = _finite_unit_interval(preregistration["familyWiseAlpha"], name="familyWiseAlpha")
    per_cell_alpha = _finite_unit_interval(preregistration["perCellAlpha"], name="perCellAlpha")
    if (
        preregistration["method"] != "bonferroni"
        or not math.isclose(family_alpha, 0.05, rel_tol=0, abs_tol=1e-15)
        or not math.isclose(per_cell_alpha, family_alpha / k, rel_tol=0, abs_tol=1e-15)
    ):
        raise FalsifierError("Preregistration lacks the frozen simultaneous confidence control")

    sources = value["sources"]
    if not isinstance(sources, list) or not sources:
        raise FalsifierError("Source provenance is empty")
    for source in sources:
        _validate_source(source)

    private_veto = value["privateVeto"]
    if not isinstance(private_veto, dict) or frozenset(private_veto) != PRIVATE_VETO_FIELDS:
        raise FalsifierError("Private-veto evidence does not use the allowlist")
    veto_status = private_veto["status"]
    influence = private_veto["influence"]
    if (veto_status, influence) not in {
        ("not-run", "none"),
        ("pass", "none"),
        ("fail", "veto"),
    }:
        raise FalsifierError("Private evidence has approval, tuning, ranking, or rescue influence")

    predictions_value = value["predictions"]
    if (
        not isinstance(predictions_value, list)
        or not predictions_value
        or len(predictions_value) > MAX_PREDICTIONS
    ):
        raise FalsifierError("Locked predictions are empty or over the row bound")
    predictions: dict[str, Prediction] = {}
    for row in predictions_value:
        if not isinstance(row, dict) or frozenset(row) != PREDICTION_FIELDS:
            raise FalsifierError("Locked prediction row does not use the allowlist")
        identifier = row["id"]
        if not isinstance(identifier, str) or identifier not in expected_ids:
            raise FalsifierError("Locked prediction row has an unknown id")
        if identifier in predictions:
            raise FalsifierError("Locked prediction row duplicates an id")
        score = _finite_unit_interval(row["score"], name="prediction score")
        expected_decision = (
            "retain" if score <= retain_max else "suppress" if score >= suppress_min else "abstain"
        )
        if row["decision"] != expected_decision:
            raise FalsifierError("Locked prediction decision is inconsistent with its score")
        predictions[identifier] = Prediction(score, expected_decision)
    if set(predictions) != expected_ids:
        raise FalsifierError("Locked predictions do not exactly cover the expected rows")
    return LockedPredictions(
        thresholds=(retain_max, suppress_min),
        predictions=predictions,
        preregistered_k=k,
        source_count=len(sources),
        private_veto_status=veto_status,
    )


def locked_prediction_report(artifact: LockedPredictions) -> dict[str, Any]:
    decisions = Counter(item.decision for item in artifact.predictions.values())
    return {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-locked-prediction-validation",
        "researchOnly": True,
        "promotionEligible": False,
        "phase": LOCKED_PHASE,
        "identityChecks": len(HASH_NAMES),
        "thresholdsValid": True,
        "preregisteredK": artifact.preregistered_k,
        "simultaneousMethod": "bonferroni",
        "sourceRecords": artifact.source_count,
        "privateVetoStatus": artifact.private_veto_status,
        "predictions": len(artifact.predictions),
        "decisions": {decision: decisions[decision] for decision in DECISIONS},
    }


class _FakeTranslator:
    engine = "synthetic-fake"
    engine_version = "1"
    model_id = "synthetic/fake"
    revision = "synthetic"
    model_sha256 = "0" * 64
    execution_metadata: Mapping[str, Any] = {}

    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    def translate(self, texts: list[str], target_language: str) -> list[str]:
        del target_language
        self.calls.append(list(texts))
        return [f"English result {index}" for index, _ in enumerate(texts)]


class _FakePersistentCache:
    def __init__(self, initial: Mapping[str, str]) -> None:
        self._values = dict(initial)
        self.initial_snapshot = tuple(sorted(initial.items()))

    def get(self, target_language: str, text: str) -> tuple[bool, str]:
        del target_language
        return (text in self._values, self._values.get(text, ""))

    def put(self, target_language: str, text: str, translated: str) -> None:
        del target_language
        self._values[text] = translated


class _FakeRunCache:
    def __init__(self) -> None:
        self._values: dict[str, TranslationOutcome] = {}

    def get(self, text: str) -> TranslationOutcome | None:
        return self._values.get(text)

    def put(self, text: str, value: TranslationOutcome) -> None:
        self._values[text] = value


def _synthetic_record(ordinal: int, text: str) -> dict[str, Any]:
    return {
        "recordId": f"synthetic-record-{ordinal:02d}",
        "text": text,
        "transform": None,
        "attributes": {"origin": "project-synthetic"},
    }


def _run_fake_translation(
    texts: Sequence[str], persistent_values: Mapping[str, str]
) -> tuple[
    TranslationWorkStats,
    _FakeTranslator,
    list[dict[str, Any]],
    tuple[tuple[str, str], ...],
]:
    records = [_synthetic_record(index, text) for index, text in enumerate(texts)]
    translator = _FakeTranslator()
    persistent = _FakePersistentCache(persistent_values)
    work = TranslationWorkStats()
    output = list(
        translate_normalized_records(
            records,
            translator,
            "en",
            batch_size=4,
            minimum_characters=1,
            maximum_characters=2_048,
            window_size=4,
            cache=persistent,
            include_parents=False,
            run_cache=_FakeRunCache(),
            work_stats=work,
        )
    )
    work.translated_child_occurrences += len(output)
    work.payload()
    return work, translator, output, persistent.initial_snapshot


def paired_fake_translator_report() -> dict[str, Any]:
    """Replay one deterministic paired counterfactual through production worker logic."""

    protected = "01234567-89ab-cdef-0123-456789abcdef"
    persistent_hit = "Mensaje sintético ya almacenado"
    model_machine = "mov rax rbp xor rcx synthetic opcode sequence"
    model_human = "Bonjour, ceci est un message synthétique"
    baseline_texts = (
        protected,
        persistent_hit,
        model_machine,
        model_human,
        protected,
        persistent_hit,
        model_machine,
        model_human,
    )
    initial_persistent = {persistent_hit: "Synthetic cached English result"}

    baseline, baseline_translator, baseline_children, baseline_initial = _run_fake_translation(
        baseline_texts, dict(initial_persistent)
    )
    # The learned-only counterfactual removes the text that the baseline proves
    # reaches model input.  Proposals on protected/cache paths are observed but
    # deliberately receive no model-call credit.
    learned_model_input_texts = {model_machine}
    candidate_texts = tuple(
        text for text in baseline_texts if text not in learned_model_input_texts
    )
    candidate, candidate_translator, candidate_children, candidate_initial = _run_fake_translation(
        candidate_texts, dict(initial_persistent)
    )

    baseline_inputs = sum(len(batch) for batch in baseline_translator.calls)
    candidate_inputs = sum(len(batch) for batch in candidate_translator.calls)
    learned_input_savings = baseline_inputs - candidate_inputs
    if baseline_initial != candidate_initial or baseline_initial != tuple(
        sorted(initial_persistent.items())
    ):
        raise FalsifierError("Paired replay did not begin with identical persistent cache state")
    if (
        baseline.protected_only_bypass_texts != 1
        or baseline.translation_cache_hits != 2
        or baseline.run_cache_hits != 3
        or baseline.translator_input_texts != 2
        or candidate.protected_only_bypass_texts != 1
        or candidate.translation_cache_hits != 2
        or candidate.run_cache_hits != 1
        or candidate.translator_input_texts != 1
    ):
        raise FalsifierError("Paired replay no longer exercises every preregistered work path")
    if learned_input_savings != len(learned_model_input_texts):
        raise FalsifierError("Paired replay cannot attribute translator-input savings")
    if (
        baseline.protected_only_bypass_texts != candidate.protected_only_bypass_texts
        or baseline.translation_cache_hits != candidate.translation_cache_hits
    ):
        raise FalsifierError("Learned-only replay changed a protected or persistent-cache bucket")
    if len(baseline_children) - len(candidate_children) != 2:
        raise FalsifierError("Paired replay occurrence cardinality is inconsistent")

    def aggregate(stats: TranslationWorkStats) -> dict[str, int]:
        return {
            "candidateOccurrences": stats.candidate_occurrences,
            "textDecisions": stats.text_decisions,
            "protectedOnlyBypassTexts": stats.protected_only_bypass_texts,
            "runCacheHits": stats.run_cache_hits,
            "persistentCacheHits": stats.translation_cache_hits,
            "translatorInputTexts": stats.translator_input_texts,
            "translatorRequests": stats.translator_requests,
            "modelResults": stats.model_results,
            "translatedChildOccurrences": stats.translated_child_occurrences,
        }

    return {
        "schemaVersion": SCHEMA_VERSION,
        "reportType": "translation-worthiness-paired-fake-translator",
        "researchOnly": True,
        "promotionEligible": False,
        "cacheStatesIdenticalAtStart": True,
        "runCacheStatesIdenticalAtStart": True,
        "baseline": aggregate(baseline),
        "candidate": aggregate(candidate),
        "attribution": {
            "proposalsOnProtectedOnlyPath": 1,
            "proposalsOnRunCachePath": 2,
            "proposalsOnPersistentCachePath": 2,
            "proposalsOnModelInputPath": 1,
            "learnedOnlyTranslatorInputSavings": learned_input_savings,
            "protectedOnlySavingsCredited": 0,
            "runCacheSavingsCredited": 0,
            "persistentCacheSavingsCredited": 0,
            "removedChildOccurrences": len(baseline_children) - len(candidate_children),
        },
        "reconciliation": {
            "baselineValid": True,
            "candidateValid": True,
            "protectedBucketUnchanged": True,
            "persistentCacheBucketUnchanged": True,
            "translatorDeltaEqualsLearnedOnly": True,
        },
    }


def canonical_json(value: Any) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
