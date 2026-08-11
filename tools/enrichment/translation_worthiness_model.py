#!/usr/bin/env python3
"""Deterministic research-only translation-worthiness model contract.

This module is a training and serialization reference. It is not imported by
the bstrings product runtime and never authorizes suppression.
"""

from __future__ import annotations

import hashlib
import json
import math
import struct
from collections import defaultdict
from collections.abc import Iterable, Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from translation_worthiness_corpus import CorpusRow, WorthinessCorpusError

MODEL_MAGIC = b"BSTRWFM1"
MODEL_FORMAT_VERSION = 1
MODEL_HEADER = struct.Struct("<8sIIIIIId")
MODEL_WEIGHT = struct.Struct("<d")
SCORE_ORIENTATION = "higher-contains-any-human"
FEATURE_CONTRACT_ID = "utf8-unicode-range-sparse-v1"
ASCII_WHITESPACE = " \t\r\n"
ASCII_PUNCTUATION_SYMBOLS = "{}[]()<>=:+-*/\\|&;,.!?_'\"`~@#$%^"
UNICODE_RANGES = (
    (0x0000, 0x007F, "basic-latin"),
    (0x0080, 0x024F, "extended-latin"),
    (0x0370, 0x03FF, "greek"),
    (0x0400, 0x052F, "cyrillic"),
    (0x0590, 0x05FF, "hebrew"),
    (0x0600, 0x06FF, "arabic"),
    (0x0900, 0x097F, "devanagari"),
    (0x3040, 0x309F, "hiragana"),
    (0x30A0, 0x30FF, "katakana"),
    (0x3400, 0x4DBF, "han-a"),
    (0x4E00, 0x9FFF, "han"),
    (0xAC00, 0xD7AF, "hangul"),
)
FEATURE_CONTRACT = {
    "contractId": FEATURE_CONTRACT_ID,
    "hash": "fnv1a64-modulo-bucket-count",
    "input": "strict-unicode-scalars-to-utf8-no-normalization",
    "accumulation": "ascending-bucket-index-float64",
    "bucketValue": "summed-count-clipped-to-255",
    "featureKey": "fnv-input-prefix-plus-payload",
    "engineeredKeyPrefix": "ascii-bytes-e-colon",
    "engineeredNames": (
        "ascii:alpha",
        "ascii:control",
        "ascii:digit",
        "ascii:punctuation-symbol",
        "ascii:whitespace",
        "length:unicode-scalars",
        "length:utf8-bytes",
        "run:ascii-alpha",
        "run:ascii-digit",
        "range:<allowlisted-range-name>",
    ),
    "asciiWhitespaceCodePoints": [ord(value) for value in ASCII_WHITESPACE],
    "asciiPunctuationSymbolCodePoints": [ord(value) for value in ASCII_PUNCTUATION_SYMBOLS],
    "unicodeRangesInclusive": [list(value) for value in UNICODE_RANGES],
    "unicodeRangeFallback": "other",
    "ngramKey": "ascii-bytes-n-colon + raw-width-byte + ascii-colon + raw-utf8-ngram",
    "ngramWidths": [2, 3, 4, 5],
    "fnvOffsetBasis": 0xCBF29CE484222325,
    "fnvPrime": 0x100000001B3,
    "fnvMask": 0xFFFFFFFFFFFFFFFF,
}
ABLATION_CODES = {
    "engineered-only": 1,
    "character-ngrams-only": 2,
    "combined": 3,
}
ABLATION_NAMES = {value: key for key, value in ABLATION_CODES.items()}
ALL_ABLATIONS = ("exact-only", *ABLATION_CODES)
DEFAULT_BUCKET_COUNT = 4096
DEFAULT_EPOCHS = 48
DEFAULT_LEARNING_RATE = 0.03125
MAX_BUCKET_COUNT = 1 << 22


@dataclass(frozen=True)
class SparseLinearModel:
    ablation: str
    bucket_count: int
    ngram_min: int
    ngram_max: int
    bias: float
    weights: tuple[float, ...]

    def score(self, text: str) -> float:
        features = extract_features(text, self.ablation, self.bucket_count)
        score = self.bias
        for index in sorted(features):
            score += self.weights[index] * features[index]
        if not math.isfinite(score):
            raise WorthinessCorpusError("Translation-worthiness model emitted a non-finite score")
        return score


def canonical_json_bytes(value: Any) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True) + "\n"
    ).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as handle:
            while chunk := handle.read(1024 * 1024):
                digest.update(chunk)
    except OSError as exc:
        raise WorthinessCorpusError("Could not hash a translation-worthiness input") from exc
    return digest.hexdigest()


def feature_contract_sha256() -> str:
    return sha256_bytes(canonical_json_bytes(FEATURE_CONTRACT))


def _validate_text(text: str) -> None:
    if any(0xD800 <= ord(character) <= 0xDFFF for character in text):
        raise WorthinessCorpusError("Translation-worthiness text contains an unpaired surrogate")


def _fnv1a64(value: bytes) -> int:
    result = 0xCBF29CE484222325
    for byte in value:
        result ^= byte
        result = (result * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return result


def _add_feature(features: dict[int, int], key: bytes, value: int, bucket_count: int) -> None:
    if value <= 0:
        return
    index = _fnv1a64(key) % bucket_count
    features[index] = min(255, features.get(index, 0) + value)


def _unicode_range(codepoint: int) -> str:
    for minimum, maximum, name in UNICODE_RANGES:
        if minimum <= codepoint <= maximum:
            return name
    return "other"


def _engineered_features(text: str, features: dict[int, int], bucket_count: int) -> None:
    counts: dict[str, int] = defaultdict(int)
    longest_alpha = 0
    longest_digit = 0
    current_alpha = 0
    current_digit = 0
    for character in text:
        codepoint = ord(character)
        counts[f"range:{_unicode_range(codepoint)}"] += 1
        if "A" <= character <= "Z" or "a" <= character <= "z":
            counts["ascii:alpha"] += 1
            current_alpha += 1
        else:
            longest_alpha = max(longest_alpha, current_alpha)
            current_alpha = 0
        if "0" <= character <= "9":
            counts["ascii:digit"] += 1
            current_digit += 1
        else:
            longest_digit = max(longest_digit, current_digit)
            current_digit = 0
        if character in ASCII_WHITESPACE:
            counts["ascii:whitespace"] += 1
        if codepoint < 0x20 or codepoint == 0x7F:
            counts["ascii:control"] += 1
        if character in ASCII_PUNCTUATION_SYMBOLS:
            counts["ascii:punctuation-symbol"] += 1
    longest_alpha = max(longest_alpha, current_alpha)
    longest_digit = max(longest_digit, current_digit)
    counts["length:unicode-scalars"] = len(text)
    counts["length:utf8-bytes"] = len(text.encode("utf-8", errors="strict"))
    counts["run:ascii-alpha"] = longest_alpha
    counts["run:ascii-digit"] = longest_digit
    for name in sorted(counts):
        _add_feature(features, b"e:" + name.encode("ascii"), counts[name], bucket_count)


def _ngram_features(text: str, features: dict[int, int], bucket_count: int) -> None:
    encoded = text.encode("utf-8", errors="strict")
    for width in range(2, 6):
        if len(encoded) < width:
            continue
        prefix = b"n:" + bytes([width]) + b":"
        for start in range(len(encoded) - width + 1):
            _add_feature(features, prefix + encoded[start : start + width], 1, bucket_count)


def extract_features(text: str, ablation: str, bucket_count: int) -> Mapping[int, int]:
    if ablation not in ABLATION_CODES:
        raise WorthinessCorpusError("Unsupported learned ablation")
    if bucket_count <= 0 or bucket_count > MAX_BUCKET_COUNT:
        raise WorthinessCorpusError("Translation-worthiness bucket count is outside bounds")
    _validate_text(text)
    features: dict[int, int] = {}
    if ablation in {"engineered-only", "combined"}:
        _engineered_features(text, features, bucket_count)
    if ablation in {"character-ngrams-only", "combined"}:
        _ngram_features(text, features, bucket_count)
    return features


def _training_label(row: CorpusRow) -> int | None:
    if row.label in {"human-worthy", "mixed"}:
        return 1
    if row.label == "machine":
        return -1
    return None


def train_model(
    rows: Iterable[CorpusRow],
    *,
    ablation: str,
    bucket_count: int = DEFAULT_BUCKET_COUNT,
    epochs: int = DEFAULT_EPOCHS,
    learning_rate: float = DEFAULT_LEARNING_RATE,
) -> SparseLinearModel:
    if ablation not in ABLATION_CODES:
        raise WorthinessCorpusError("The trainer supports only learned ablations")
    if bucket_count <= 0 or bucket_count > MAX_BUCKET_COUNT or epochs <= 0:
        raise WorthinessCorpusError("Translation-worthiness training parameters are invalid")
    if not math.isfinite(learning_rate) or learning_rate <= 0:
        raise WorthinessCorpusError("Translation-worthiness learning rate is invalid")
    examples = []
    for row in sorted(rows, key=lambda item: item.identifier):
        label = _training_label(row)
        if label is not None:
            examples.append((extract_features(row.text, ablation, bucket_count), label))
    if not examples or not {label for _, label in examples} == {-1, 1}:
        raise WorthinessCorpusError("Training split must contain human-positive and machine rows")
    weights = [0.0] * bucket_count
    bias = 0.0
    for _ in range(epochs):
        for features, label in examples:
            score = bias
            for index in sorted(features):
                score += weights[index] * features[index]
            if label * score <= 1.0:
                adjustment = learning_rate * label
                for index in sorted(features):
                    weights[index] += adjustment * features[index]
                bias += adjustment
    if not math.isfinite(bias) or any(not math.isfinite(weight) for weight in weights):
        raise WorthinessCorpusError("Translation-worthiness training produced invalid weights")
    return SparseLinearModel(
        ablation=ablation,
        bucket_count=bucket_count,
        ngram_min=2,
        ngram_max=5,
        bias=bias,
        weights=tuple(weights),
    )


def serialize_model(model: SparseLinearModel) -> bytes:
    if len(model.weights) != model.bucket_count:
        raise WorthinessCorpusError("Translation-worthiness model weight count is invalid")
    try:
        header = MODEL_HEADER.pack(
            MODEL_MAGIC,
            MODEL_FORMAT_VERSION,
            ABLATION_CODES[model.ablation],
            model.bucket_count,
            model.ngram_min,
            model.ngram_max,
            len(model.weights),
            model.bias,
        )
        return header + b"".join(MODEL_WEIGHT.pack(weight) for weight in model.weights)
    except (KeyError, OverflowError, struct.error) as exc:
        raise WorthinessCorpusError("Translation-worthiness model could not be serialized") from exc


def deserialize_model(raw: bytes) -> SparseLinearModel:
    if len(raw) < MODEL_HEADER.size:
        raise WorthinessCorpusError("Translation-worthiness model is truncated")
    try:
        magic, version, ablation_code, buckets, ngram_min, ngram_max, weight_count, bias = (
            MODEL_HEADER.unpack_from(raw)
        )
    except struct.error as exc:
        raise WorthinessCorpusError("Translation-worthiness model header is invalid") from exc
    if (
        magic != MODEL_MAGIC
        or version != MODEL_FORMAT_VERSION
        or ablation_code not in ABLATION_NAMES
        or buckets <= 0
        or buckets > MAX_BUCKET_COUNT
        or weight_count != buckets
        or ngram_min != 2
        or ngram_max != 5
        or not math.isfinite(bias)
        or len(raw) != MODEL_HEADER.size + weight_count * MODEL_WEIGHT.size
    ):
        raise WorthinessCorpusError("Translation-worthiness model identity is invalid")
    weights = tuple(
        MODEL_WEIGHT.unpack_from(raw, MODEL_HEADER.size + index * MODEL_WEIGHT.size)[0]
        for index in range(weight_count)
    )
    if any(not math.isfinite(weight) for weight in weights):
        raise WorthinessCorpusError("Translation-worthiness model contains an invalid weight")
    return SparseLinearModel(
        ablation=ABLATION_NAMES[ablation_code],
        bucket_count=buckets,
        ngram_min=ngram_min,
        ngram_max=ngram_max,
        bias=bias,
        weights=weights,
    )


def validate_model_manifest_identity(
    raw: bytes,
    *,
    ablation: str,
    bucket_count: int,
    ngram_min: int,
    ngram_max: int,
) -> SparseLinearModel:
    model = deserialize_model(raw)
    if (
        model.ablation != ablation
        or model.bucket_count != bucket_count
        or model.ngram_min != ngram_min
        or model.ngram_max != ngram_max
    ):
        raise WorthinessCorpusError("Translation-worthiness model disagrees with its manifest")
    return model


def choose_thresholds(
    model: SparseLinearModel, calibration_rows: Iterable[CorpusRow]
) -> tuple[float, float]:
    scored = [(model.score(row.text), row.label) for row in calibration_rows]
    protected = [
        score for score, label in scored if label in {"human-worthy", "mixed", "ambiguous"}
    ]
    machine = [score for score, label in scored if label == "machine"]
    positives = [score for score, label in scored if label in {"human-worthy", "mixed"}]
    if not protected or not positives or not machine:
        raise WorthinessCorpusError("Calibration split lacks required labels")
    suppress_max = math.nextafter(min(protected), -math.inf)
    retain_min = min(positives)
    if retain_min <= suppress_max:
        retain_min = math.nextafter(suppress_max, math.inf)
    if not math.isfinite(suppress_max) or not math.isfinite(retain_min):
        raise WorthinessCorpusError("Calibration produced invalid abstention thresholds")
    return suppress_max, retain_min


def decision_for_score(score: float, suppress_max: float, retain_min: float) -> str:
    if not all(math.isfinite(value) for value in (score, suppress_max, retain_min)):
        raise WorthinessCorpusError("Translation-worthiness score or threshold is invalid")
    if suppress_max >= retain_min:
        raise WorthinessCorpusError(
            "Translation-worthiness thresholds do not leave an abstention interval"
        )
    if score <= suppress_max:
        return "suppress"
    if score >= retain_min:
        return "retain"
    return "abstain"


def score_rows(
    model: SparseLinearModel,
    rows: Iterable[CorpusRow],
    suppress_max: float,
    retain_min: float,
) -> list[dict[str, Any]]:
    predictions = []
    for row in sorted(rows, key=lambda item: item.identifier):
        score = model.score(row.text)
        predictions.append(
            {
                "id": row.identifier,
                "score": score,
                "decision": decision_for_score(score, suppress_max, retain_min),
            }
        )
    return predictions


def ensure_all_finite(values: Sequence[float]) -> None:
    if any(not math.isfinite(value) for value in values):
        raise WorthinessCorpusError("Translation-worthiness values must be finite")
