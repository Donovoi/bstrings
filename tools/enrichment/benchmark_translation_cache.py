#!/usr/bin/env python3
"""Reproducible synthetic benchmark for run-local translation deduplication."""

from __future__ import annotations

import argparse
import json
import platform
import sys
import tempfile
import time
from dataclasses import asdict, dataclass
from pathlib import Path

from bstrings_enrich import RunLocalTranslationCache, TranslationCache, TranslationOutcome


@dataclass(frozen=True)
class Measurement:
    workload: str
    implementation: str
    rows: int
    distinct_keys: int
    model_inputs: int
    elapsed_seconds: float
    microseconds_per_row: float
    cache_bytes: int
    hot_set_bytes: int


def source_text(index: int) -> str:
    return f"synthetic translation source {index:08d}"


def measure_lru(rows: int, distinct_keys: int, cache_size: int, workload: str) -> Measurement:
    cache = TranslationCache(cache_size)
    model_inputs = 0
    started = time.perf_counter()
    for row in range(rows):
        index = row if workload == "all-unique" else row % distinct_keys
        text = source_text(index)
        found, _ = cache.get("en", text)
        if not found:
            model_inputs += 1
            cache.put("en", text, text + " translated")
    elapsed = time.perf_counter() - started
    return Measurement(
        workload,
        "v1.9.15-bounded-lru",
        rows,
        distinct_keys,
        model_inputs,
        elapsed,
        elapsed * 1_000_000 / rows,
        0,
        0,
    )


def approximate_hot_set_bytes(cache: RunLocalTranslationCache) -> int:
    """Report the retained Python objects in the bounded exact-cache hot set."""
    values: list[object] = [cache._memory]
    for text, outcome in cache._memory.items():
        values.extend(
            (
                text,
                outcome,
                outcome.text,
                outcome.integrity,
                outcome.reason,
                outcome.ambiguous_identifier_count,
            )
        )
    seen: set[int] = set()
    total = 0
    for value in values:
        identity = id(value)
        if identity in seen:
            continue
        seen.add(identity)
        total += sys.getsizeof(value)
    return total


def measure_exact(rows: int, distinct_keys: int, cache_size: int, workload: str) -> Measurement:
    model_inputs = 0
    with tempfile.TemporaryDirectory(prefix="bstrings-cache-benchmark-") as directory:
        cache = RunLocalTranslationCache(
            Path(directory),
            "synthetic-cache-benchmark-v1",
            cache_size,
        )
        started = time.perf_counter()
        for row in range(rows):
            index = row if workload == "all-unique" else row % distinct_keys
            text = source_text(index)
            outcome = cache.get(text)
            if outcome is None:
                model_inputs += 1
                cache.put(text, TranslationOutcome(text + " translated", "verified"))
        elapsed = time.perf_counter() - started
        cache_bytes = sum(
            candidate.stat().st_size
            for candidate in (
                cache.path,
                Path(str(cache.path) + "-journal"),
                Path(str(cache.path) + "-wal"),
                Path(str(cache.path) + "-shm"),
            )
            if candidate.exists()
        )
        hot_set_bytes = approximate_hot_set_bytes(cache)
        cache.close(commit=True, strict=True)
    return Measurement(
        workload,
        "v1.9.17-run-local-exact",
        rows,
        distinct_keys,
        model_inputs,
        elapsed,
        elapsed * 1_000_000 / rows,
        cache_bytes,
        hot_set_bytes,
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rows", type=int, default=1_000_000)
    parser.add_argument("--distinct-keys", type=int, default=10_000)
    parser.add_argument("--cache-size", type=int, default=4_096)
    parser.add_argument("--pinned-model-strings-per-second", type=float, default=0.2793)
    parser.add_argument("--output", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.rows < 1_000_000:
        raise SystemExit("--rows must be at least 1,000,000 for the accepted release gate")
    if args.distinct_keys <= args.cache_size or args.distinct_keys >= args.rows:
        raise SystemExit("--distinct-keys must be greater than --cache-size and less than --rows")
    if args.pinned_model_strings_per_second <= 0:
        raise SystemExit("--pinned-model-strings-per-second must be positive")

    measurements = []
    for workload, distinct_keys in (
        ("duplicate-cycle", args.distinct_keys),
        ("all-unique", args.rows),
    ):
        measurements.append(measure_lru(args.rows, distinct_keys, args.cache_size, workload))
        measurements.append(measure_exact(args.rows, distinct_keys, args.cache_size, workload))

    duplicate_lru, duplicate_exact, _, unique_exact = measurements
    call_reduction = 1.0 - duplicate_exact.model_inputs / duplicate_lru.model_inputs
    pinned_model_seconds_per_input = 1.0 / args.pinned_model_strings_per_second
    model_inclusive_cache_fraction = (
        unique_exact.microseconds_per_row / 1_000_000 / pinned_model_seconds_per_input
    )
    payload = {
        "schemaVersion": 1,
        "runtime": platform.python_version(),
        "platform": platform.platform(),
        "cacheSize": args.cache_size,
        "duplicateModelCallReduction": call_reduction,
        "pinnedModelStringsPerSecond": args.pinned_model_strings_per_second,
        "modelInclusiveCacheFraction": model_inclusive_cache_fraction,
        "measurements": [asdict(measurement) for measurement in measurements],
    }
    rendered = json.dumps(payload, indent=2, sort_keys=True) + "\n"
    if args.output is not None:
        args.output.write_text(rendered, encoding="utf-8")
    print(rendered, end="")

    if duplicate_exact.model_inputs != args.distinct_keys:
        raise SystemExit("Exact cache model inputs did not equal the duplicate workload key count")
    if call_reduction < 0.25:
        raise SystemExit("Exact cache reduced model inputs by less than 25 percent")
    if unique_exact.microseconds_per_row > 250.0:
        raise SystemExit("Exact cache exceeded the 250 microsecond per-row absolute ceiling")
    if model_inclusive_cache_fraction >= 0.01:
        raise SystemExit("Exact cache consumed at least one percent of pinned-model input time")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
