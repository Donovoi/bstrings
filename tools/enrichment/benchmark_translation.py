#!/usr/bin/env python3
"""Benchmark offline translation quality, evidence-token retention, and throughput."""

from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
import time
import urllib.error
import urllib.request
from collections.abc import Iterable, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any, Protocol

from bstrings_enrich import EnrichmentError, MadladTranslator, llama_translation_prompt

DEFAULT_LOCALES = (
    "ar_EG",
    "de_DE",
    "es_MX",
    "fa_IR",
    "fr_FR",
    "hi_IN",
    "ja_JP",
    "ko_KR",
    "ru_RU",
    "th_TH",
    "tr_TR",
    "zh_CN",
)


class BenchmarkError(RuntimeError):
    """Raised when a benchmark cannot produce comparable results."""


class TranslationBackend(Protocol):
    batch_size: int

    def translate(self, texts: Sequence[str]) -> list[str]: ...


@dataclass(frozen=True)
class Case:
    case_id: str
    suite: str
    language: str
    source: str
    reference: str
    identifiers: tuple[str, ...]


@dataclass(frozen=True)
class Result:
    case_id: str
    suite: str
    language: str
    source: str
    reference: str
    hypothesis: str
    latency_seconds: float
    identifiers: tuple[str, ...]
    retained_identifiers: tuple[str, ...]


class OpenAIChatBackend:
    """Deterministic loopback-only llama.cpp chat-completions client."""

    def __init__(self, endpoint: str, timeout_seconds: float, max_tokens: int) -> None:
        if not endpoint.startswith(("http://127.0.0.1:", "http://localhost:")):
            raise BenchmarkError("The benchmark endpoint must be loopback-only")
        self._url = endpoint.rstrip("/") + "/v1/chat/completions"
        self._timeout_seconds = timeout_seconds
        self._max_tokens = max_tokens
        self._url_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self.batch_size = 1

    def translate(self, texts: Sequence[str]) -> list[str]:
        return [self._translate_one(text) for text in texts]

    def _translate_one(self, text: str) -> str:
        payload = json.dumps(
            {
                "messages": [{"role": "user", "content": llama_translation_prompt(text, "en")}],
                "temperature": 0,
                "top_p": 1,
                "seed": 1,
                "max_tokens": self._max_tokens,
                "stream": False,
            }
        ).encode("utf-8")
        request = urllib.request.Request(
            self._url,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with self._url_opener.open(request, timeout=self._timeout_seconds) as response:
                body = json.load(response)
            content = body["choices"][0]["message"]["content"]
        except (urllib.error.URLError, TimeoutError, KeyError, IndexError, TypeError) as exc:
            raise BenchmarkError(f"Translation endpoint failed: {exc}") from exc
        if not isinstance(content, str) or not content.strip():
            raise BenchmarkError("Translation endpoint returned an empty result")
        return content.strip()


class MadladBackend:
    def __init__(self, translator: MadladTranslator, batch_size: int) -> None:
        self._translator = translator
        self.batch_size = batch_size

    def translate(self, texts: Sequence[str]) -> list[str]:
        return self._translator.translate(texts, "en")


def load_forensic_cases(path: Path) -> list[Case]:
    cases: list[Case] = []
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
                cases.append(
                    Case(
                        case_id=str(row["id"]),
                        suite="forensic",
                        language=str(row["language"]),
                        source=str(row["source"]),
                        reference=str(row["reference"]),
                        identifiers=tuple(str(value) for value in row["identifiers"]),
                    )
                )
            except (json.JSONDecodeError, KeyError, TypeError) as exc:
                raise BenchmarkError(f"Invalid forensic case at line {line_number}: {exc}") from exc
    if not cases:
        raise BenchmarkError("The forensic benchmark contains no cases")
    return cases


def load_wmt_cases(root: Path, locales: Sequence[str], limit: int) -> list[Case]:
    cases: list[Case] = []
    for locale in locales:
        path = root / f"en-{locale}.jsonl"
        if not path.is_file():
            raise BenchmarkError(f"WMT24++ locale file was not found: {path}")
        accepted = 0
        with path.open("r", encoding="utf-8") as handle:
            for line_number, line in enumerate(handle, start=1):
                if accepted >= limit:
                    break
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise BenchmarkError(
                        f"Invalid WMT JSON at {path}:{line_number}: {exc}"
                    ) from exc
                if row.get("is_bad_source"):
                    continue
                source = row.get("target")
                reference = row.get("source")
                if not isinstance(source, str) or not isinstance(reference, str):
                    continue
                cases.append(
                    Case(
                        case_id=f"wmt24pp-{locale}-{accepted + 1}",
                        suite="wmt24pp",
                        language=locale,
                        source=source,
                        reference=reference,
                        identifiers=(),
                    )
                )
                accepted += 1
        if accepted != limit:
            raise BenchmarkError(
                f"WMT24++ {locale} supplied {accepted} usable rows, expected {limit}"
            )
    return cases


def percentile(values: Sequence[float], percentile_value: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    position = (len(ordered) - 1) * percentile_value
    lower = math.floor(position)
    upper = math.ceil(position)
    if lower == upper:
        return ordered[lower]
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower)


def run_cases(
    backend: TranslationBackend, cases: Sequence[Case], *, progress: bool = True
) -> list[Result]:
    results: list[Result] = []
    for start in range(0, len(cases), backend.batch_size):
        batch = cases[start : start + backend.batch_size]
        started = time.perf_counter()
        translated = backend.translate([case.source for case in batch])
        batch_latency = time.perf_counter() - started
        if len(translated) != len(batch):
            raise BenchmarkError(
                f"Backend returned {len(translated)} outputs for {len(batch)} inputs"
            )
        average_latency = batch_latency / len(batch)
        for case, output in zip(batch, translated, strict=True):
            hypothesis = output.strip()
            retained = tuple(
                identifier for identifier in case.identifiers if identifier in hypothesis
            )
            results.append(
                Result(
                    case_id=case.case_id,
                    suite=case.suite,
                    language=case.language,
                    source=case.source,
                    reference=case.reference,
                    hypothesis=hypothesis,
                    latency_seconds=average_latency,
                    identifiers=case.identifiers,
                    retained_identifiers=retained,
                )
            )
        if progress:
            print(f"completed {len(results)}/{len(cases)} benchmark cases", file=sys.stderr)
    return results


def chrf_score(results: Iterable[Result]) -> float:
    try:
        from sacrebleu.metrics import CHRF
    except ImportError as exc:
        raise BenchmarkError("Install the benchmark extra to calculate chrF++") from exc
    selected = list(results)
    if not selected:
        return 0.0
    return float(
        CHRF(word_order=2)
        .corpus_score(
            [result.hypothesis for result in selected],
            [[result.reference for result in selected]],
        )
        .score
    )


def summarize(results: Sequence[Result], metadata: dict[str, Any]) -> dict[str, Any]:
    forensic = [result for result in results if result.suite == "forensic"]
    wmt = [result for result in results if result.suite == "wmt24pp"]
    identifier_total = sum(len(result.identifiers) for result in forensic)
    identifier_retained = sum(len(result.retained_identifiers) for result in forensic)
    latencies = [result.latency_seconds for result in results]
    elapsed = sum(latencies)
    languages = sorted({result.language for result in results})
    return {
        "metadata": metadata,
        "cases": len(results),
        "wmt24ppChrfPlusPlus": chrf_score(wmt),
        "forensicChrfPlusPlus": chrf_score(forensic),
        "identifierRetention": {
            "retained": identifier_retained,
            "total": identifier_total,
            "percent": 100.0 * identifier_retained / identifier_total,
            "failures": [
                {
                    "caseId": result.case_id,
                    "missing": sorted(set(result.identifiers) - set(result.retained_identifiers)),
                }
                for result in forensic
                if len(result.retained_identifiers) != len(result.identifiers)
            ],
        },
        "wmt24ppChrfPlusPlusByLanguage": {
            language: chrf_score(result for result in wmt if result.language == language)
            for language in languages
            if any(result.language == language for result in wmt)
        },
        "latencySeconds": {
            "total": elapsed,
            "p50": statistics.median(latencies),
            "p95": percentile(latencies, 0.95),
        },
        "stringsPerSecond": len(results) / elapsed if elapsed else 0.0,
    }


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    temporary.replace(path)


def write_jsonl(path: Path, values: Iterable[dict[str, Any]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8", newline="\n") as handle:
        for value in values:
            handle.write(json.dumps(value, ensure_ascii=False, sort_keys=True) + "\n")
    temporary.replace(path)


def build_backend(args: argparse.Namespace) -> TranslationBackend:
    if args.engine == "openai":
        return OpenAIChatBackend(args.endpoint, args.timeout_seconds, args.max_new_tokens)
    translator = MadladTranslator(
        args.model_path,
        args.model_id,
        args.model_revision,
        args.model_sha256,
        args.device,
        args.max_input_tokens,
        args.max_new_tokens,
    )
    return MadladBackend(translator, args.batch_size)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--engine", choices=("openai", "madlad"), required=True)
    parser.add_argument("--endpoint", default="http://127.0.0.1:18089")
    parser.add_argument("--model-path", type=Path)
    parser.add_argument("--model-id", required=True)
    parser.add_argument("--model-revision", required=True)
    parser.add_argument("--model-sha256", required=True)
    parser.add_argument("--runtime", required=True)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda"), default="auto")
    parser.add_argument("--wmt-root", required=True, type=Path)
    parser.add_argument(
        "--forensic-cases",
        type=Path,
        default=Path(__file__).with_name("forensic_translation_cases.jsonl"),
    )
    parser.add_argument("--locales", nargs="+", default=list(DEFAULT_LOCALES))
    parser.add_argument("--wmt-per-locale", type=int, default=5)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--max-input-tokens", type=int, default=512)
    parser.add_argument("--max-new-tokens", type=int, default=512)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--output", required=True, type=Path)
    return parser.parse_args()


def validate_arguments(args: argparse.Namespace) -> None:
    if args.wmt_per_locale <= 0 or args.batch_size <= 0:
        raise BenchmarkError("Benchmark limits and batch size must be positive")
    if args.engine == "madlad" and args.model_path is None:
        raise BenchmarkError("--model-path is required for the MADLAD engine")
    if len(args.model_sha256) != 64 or any(
        character not in "0123456789abcdefABCDEF" for character in args.model_sha256
    ):
        raise BenchmarkError("--model-sha256 must be a 64-character hexadecimal digest")


def main() -> int:
    args = parse_arguments()
    try:
        validate_arguments(args)
        cases = load_wmt_cases(args.wmt_root, args.locales, args.wmt_per_locale)
        cases.extend(load_forensic_cases(args.forensic_cases))
        results = run_cases(build_backend(args), cases)
        metadata = {
            "engine": args.engine,
            "model": args.model_id,
            "revision": args.model_revision,
            "modelSha256": args.model_sha256.lower(),
            "runtime": args.runtime,
            "wmtLocales": args.locales,
            "wmtRowsPerLocale": args.wmt_per_locale,
            "deterministicDecoding": True,
        }
        summary = summarize(results, metadata)
        write_json(args.output, summary)
        write_jsonl(args.output.with_suffix(".records.jsonl"), (asdict(row) for row in results))
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return 0
    except (BenchmarkError, EnrichmentError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
