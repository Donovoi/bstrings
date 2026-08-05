#!/usr/bin/env python3
"""Benchmark translation quality, evidence-token retention, and throughput."""

from __future__ import annotations

import argparse
import hashlib
import ipaddress
import json
import math
import os
import re
import statistics
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter, defaultdict, deque
from collections.abc import Callable, Iterable, Sequence
from contextlib import suppress
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Protocol

from bstrings_enrich import (
    EnrichmentError,
    LlamaCppTranslator,
    MadladTranslator,
    airgap_mode_enabled,
    enable_airgap_mode,
    llama_translation_prompt,
    map_ordered_parallel,
)

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
DEFAULT_FORENSIC_CASES = Path(__file__).with_name("forensic_translation_cases.jsonl")
DEFAULT_MAX_SOURCE_CHARACTERS = 2048
DEFAULT_MAX_INPUT_TOKENS = 4096
DEFAULT_SAMPLE_SEED = 20260805
WMT_DIRECTION_LOCALE_TO_EN = "locale-to-en-postedit"
WMT_DIRECTION_EN_TO_LOCALE = "en-to-locale-original"
WMT_DIRECTIONS = (WMT_DIRECTION_LOCALE_TO_EN, WMT_DIRECTION_EN_TO_LOCALE)
GOOGLE_MODELS = ("general/translation-llm", "general/nmt")
GOOGLE_TRANSLATION_SCOPE = "https://www.googleapis.com/auth/cloud-translation"
TRANSLATEGEMMA_MODEL_ID = "google/translategemma-4b-it"


class BenchmarkError(RuntimeError):
    """Raised when a benchmark cannot produce comparable results."""


class TranslationBackend(Protocol):
    batch_size: int

    def translate(
        self,
        texts: Sequence[str],
        source_language: str,
        target_language: str,
    ) -> list[str]: ...


@dataclass(frozen=True)
class ExpectedHit:
    pattern_id: str
    regex: str
    expected_values: tuple[str, ...]


@dataclass(frozen=True)
class InvariantDelta:
    identifier: str
    expected_count: int
    observed_count: int
    omitted_count: int
    added_count: int
    duplicate_count: int


@dataclass(frozen=True)
class ExpectedHitObservation:
    pattern_id: str
    expected_values: tuple[str, ...]
    observed_values: tuple[str, ...]


@dataclass(frozen=True)
class Case:
    case_id: str
    suite: str
    language: str
    source: str
    reference: str
    identifiers: tuple[str, ...]
    source_language: str = ""
    target_language: str = "en"
    corpus_direction: str = "forensic-to-en"
    corpus_classification: str = "unclassified"
    domain: str = "unknown"
    document_id: str = ""
    segment_id: str = ""
    expected_hits: tuple[ExpectedHit, ...] = ()


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
    source_language: str = ""
    target_language: str = "en"
    corpus_direction: str = "forensic-to-en"
    corpus_classification: str = "unclassified"
    domain: str = "unknown"
    document_id: str = ""
    segment_id: str = ""
    invariant_deltas: tuple[InvariantDelta, ...] = ()
    expected_hit_observations: tuple[ExpectedHitObservation, ...] = ()


def utc_timestamp() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def language_code(locale: str) -> str:
    """Return the base language code expected by the benchmark engines."""
    value = locale.strip().replace("_", "-")
    if not value:
        raise BenchmarkError("Language codes cannot be empty")
    return value.split("-", maxsplit=1)[0].lower()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def sha256_directory(path: Path) -> str:
    """Hash a local model snapshot, including relative names and file contents."""
    if not path.is_dir():
        raise BenchmarkError(f"Local model snapshot was not found: {path}")
    files = sorted(
        (
            candidate
            for candidate in path.rglob("*")
            if candidate.is_file()
            and ".git" not in candidate.relative_to(path).parts
            and ".cache" not in candidate.relative_to(path).parts
        ),
        key=lambda candidate: candidate.relative_to(path).as_posix(),
    )
    if not files:
        raise BenchmarkError(f"Local model snapshot contains no files: {path}")
    digest = hashlib.sha256()
    for candidate in files:
        relative = candidate.relative_to(path).as_posix().encode("utf-8")
        digest.update(len(relative).to_bytes(8, "big"))
        digest.update(relative)
        digest.update(candidate.stat().st_size.to_bytes(8, "big"))
        with candidate.open("rb") as handle:
            for chunk in iter(lambda: handle.read(1024 * 1024), b""):
                digest.update(chunk)
    return digest.hexdigest()


def canonical_cases_sha256(cases: Sequence[Case]) -> str:
    digest = hashlib.sha256()
    for case in cases:
        row = {
            "caseId": case.case_id,
            "suite": case.suite,
            "language": case.language,
            "source": case.source,
            "reference": case.reference,
            "sourceLanguage": case.source_language,
            "targetLanguage": case.target_language,
            "direction": case.corpus_direction,
            "classification": case.corpus_classification,
            "domain": case.domain,
            "documentId": case.document_id,
            "segmentId": case.segment_id,
            "identifiers": list(case.identifiers),
            "expectedHits": [asdict(expected_hit) for expected_hit in case.expected_hits],
        }
        digest.update(
            json.dumps(row, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode(
                "utf-8"
            )
        )
        digest.update(b"\n")
    return digest.hexdigest()


def stable_rank(seed: int, *values: object) -> str:
    digest = hashlib.sha256()
    digest.update(str(seed).encode("ascii"))
    for value in values:
        digest.update(b"\x00")
        digest.update(str(value).encode("utf-8"))
    return digest.hexdigest()


def _string_metadata(row: dict[str, Any], names: Sequence[str], fallback: str) -> str:
    for name in names:
        value = row.get(name)
        if isinstance(value, (str, int)) and str(value).strip():
            return str(value).strip()
    return fallback


def parse_expected_hits(value: object, *, line_number: int) -> tuple[ExpectedHit, ...]:
    if value is None:
        return ()
    if not isinstance(value, list):
        raise BenchmarkError(
            f"Invalid forensic expectedHits at line {line_number}: expected a list"
        )
    parsed: list[ExpectedHit] = []
    seen_ids: set[str] = set()
    for index, item in enumerate(value, start=1):
        if not isinstance(item, dict):
            raise BenchmarkError(f"Invalid forensic expectedHits[{index}] at line {line_number}")
        pattern_id = item.get("id")
        expression = item.get("regex")
        expected_values = item.get("expectedValues")
        if (
            not isinstance(pattern_id, str)
            or not pattern_id.strip()
            or not isinstance(expression, str)
            or not expression
            or len(expression) > 4096
            or not isinstance(expected_values, list)
            or not expected_values
            or not all(isinstance(expected, str) and bool(expected) for expected in expected_values)
        ):
            raise BenchmarkError(f"Invalid forensic expectedHits[{index}] at line {line_number}")
        if pattern_id in seen_ids:
            raise BenchmarkError(f"Duplicate expected-hit id {pattern_id!r} at line {line_number}")
        try:
            compiled = re.compile(expression)
        except re.error as exc:
            raise BenchmarkError(
                f"Invalid regex for expected-hit {pattern_id!r} at line {line_number}: {exc}"
            ) from exc
        if compiled.search("") is not None:
            raise BenchmarkError(f"Expected-hit regex {pattern_id!r} can match an empty string")
        seen_ids.add(pattern_id)
        parsed.append(
            ExpectedHit(pattern_id, expression, tuple(str(item) for item in expected_values))
        )
    return tuple(parsed)


def validate_expected_hit_fixture(case: Case, *, line_number: int) -> None:
    """Prove that fixture-owned expectations match both source and reference."""
    for expected_hit in case.expected_hits:
        expected = Counter(expected_hit.expected_values)
        source = Counter(regex_values(expected_hit.regex, case.source))
        reference = Counter(regex_values(expected_hit.regex, case.reference))
        if source != expected or reference != expected:
            raise BenchmarkError(
                f"Forensic expected-hit {expected_hit.pattern_id!r} at line {line_number} "
                "does not exactly match both source and reference"
            )


def regex_values(expression: str, text: str) -> tuple[str, ...]:
    compiled = re.compile(expression)
    values: list[str] = []
    for match in compiled.finditer(text):
        if "value" in compiled.groupindex:
            value = match.group("value")
        elif compiled.groups == 1:
            value = match.group(1)
        else:
            value = match.group(0)
        if value is not None:
            values.append(value)
    return tuple(values)


def validate_source_capacity(cases: Sequence[Case], max_characters: int) -> dict[str, int]:
    if max_characters < DEFAULT_MAX_SOURCE_CHARACTERS:
        raise BenchmarkError(
            "--max-source-characters must be at least 2048 to cover the product input contract"
        )
    too_long = [case for case in cases if len(case.source) > max_characters]
    if too_long:
        first = too_long[0]
        raise BenchmarkError(
            f"Benchmark source {first.case_id!r} contains {len(first.source)} characters, "
            f"exceeding the {max_characters}-character limit; source was not truncated"
        )
    return {
        "configuredMaximumCharacters": max_characters,
        "observedMaximumCharacters": max((len(case.source) for case in cases), default=0),
        "observedMaximumUtf8Bytes": max(
            (len(case.source.encode("utf-8")) for case in cases), default=0
        ),
    }


class OpenAIChatBackend:
    """Deterministic loopback-only llama.cpp chat-completions client."""

    def __init__(
        self,
        endpoint: str,
        timeout_seconds: float,
        max_tokens: int,
        parallelism: int = 1,
        strict_determinism: bool = False,
    ) -> None:
        try:
            parsed = urllib.parse.urlsplit(endpoint)
            port = parsed.port
        except ValueError as exc:
            raise BenchmarkError("The benchmark endpoint must be loopback-only") from exc
        host = (parsed.hostname or "").rstrip(".").lower()
        is_loopback = host == "localhost"
        with suppress(ValueError):
            is_loopback = is_loopback or ipaddress.ip_address(host).is_loopback
        if (
            parsed.scheme != "http"
            or not is_loopback
            or port is None
            or parsed.username is not None
            or parsed.password is not None
            or parsed.query
            or parsed.fragment
        ):
            raise BenchmarkError("The benchmark endpoint must be loopback-only")
        self._url = endpoint.rstrip("/") + "/v1/chat/completions"
        self._timeout_seconds = timeout_seconds
        self._max_tokens = max_tokens
        self._thread_state = threading.local()
        self.batch_size = 1 if strict_determinism else parallelism
        self._strict_determinism = strict_determinism

    def translate(
        self,
        texts: Sequence[str],
        source_language: str = "",
        target_language: str = "en",
    ) -> list[str]:
        del source_language
        return map_ordered_parallel(
            lambda text: self._translate_one(text, target_language), texts, self.batch_size
        )

    def _thread_opener(self) -> urllib.request.OpenerDirector:
        opener = getattr(self._thread_state, "opener", None)
        if opener is None:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            self._thread_state.opener = opener
        return opener

    def _translate_one(self, text: str, target_language: str) -> str:
        payload = json.dumps(
            {
                "messages": [
                    {
                        "role": "user",
                        "content": llama_translation_prompt(text, language_code(target_language)),
                    }
                ],
                "temperature": 0,
                "top_p": 1,
                "top_k": 1,
                "seed": 1,
                "max_tokens": self._max_tokens,
                "stream": False,
                "cache_prompt": not self._strict_determinism,
            }
        ).encode("utf-8")
        request = urllib.request.Request(
            self._url,
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with self._thread_opener().open(request, timeout=self._timeout_seconds) as response:
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

    def translate(
        self,
        texts: Sequence[str],
        source_language: str = "",
        target_language: str = "en",
    ) -> list[str]:
        del source_language
        return self._translator.translate(texts, language_code(target_language))


class LlamaCppBackend:
    def __init__(self, translator: LlamaCppTranslator, batch_size: int) -> None:
        self._translator = translator
        self.batch_size = batch_size
        self.execution_metadata = translator.execution_metadata

    def translate(
        self,
        texts: Sequence[str],
        source_language: str = "",
        target_language: str = "en",
    ) -> list[str]:
        del source_language
        return self._translator.translate(texts, language_code(target_language))

    def close(self) -> None:
        self._translator.close()


def google_access_token() -> str:
    """Load an OAuth token without accepting or emitting one on the command line."""
    direct_token = os.environ.get("GOOGLE_OAUTH_ACCESS_TOKEN", "").strip()
    if direct_token:
        return direct_token
    try:
        import google.auth
        from google.auth.transport.requests import Request
    except ImportError as exc:
        raise BenchmarkError(
            "Google Cloud benchmarking requires google-auth or GOOGLE_OAUTH_ACCESS_TOKEN"
        ) from exc
    try:
        credentials, _ = google.auth.default(scopes=[GOOGLE_TRANSLATION_SCOPE])
        credentials.refresh(Request())
        token = credentials.token
    except Exception as exc:
        raise BenchmarkError("Google Application Default Credentials could not be loaded") from exc
    if not isinstance(token, str) or not token.strip():
        raise BenchmarkError("Google Application Default Credentials returned no access token")
    return token.strip()


def validate_google_corpus(cases: Sequence[Case], explicitly_allowed: bool) -> None:
    if not explicitly_allowed:
        raise BenchmarkError(
            "Google Cloud benchmarking is disabled until "
            "--allow-google-cloud-public-benchmark is supplied"
        )
    disallowed = sorted(
        {
            case.corpus_classification
            for case in cases
            if case.corpus_classification not in {"public", "synthetic"}
        }
    )
    if disallowed:
        raise BenchmarkError(
            "Google Cloud benchmarking accepts only public or synthetic corpus rows; "
            f"found: {', '.join(disallowed)}"
        )


class GoogleCloudBackend:
    """Benchmark-only Cloud Translation v3 client.

    This backend is intentionally not used by the product enrichment path. The caller must
    separately prove that every submitted row is public or synthetic.
    """

    def __init__(
        self,
        project: str,
        location: str,
        model: str,
        timeout_seconds: float,
        batch_size: int,
        token_provider: Callable[[], str] = google_access_token,
        request_opener: Callable[..., Any] | None = None,
        timestamp_provider: Callable[[], str] = utc_timestamp,
    ) -> None:
        if not re.fullmatch(r"[a-z][a-z0-9-]{4,61}[a-z0-9]", project):
            raise BenchmarkError("--google-project is not a valid Google Cloud project ID")
        if not re.fullmatch(r"[a-z0-9-]+", location):
            raise BenchmarkError("--google-location is invalid")
        if model not in GOOGLE_MODELS:
            raise BenchmarkError(f"Unsupported Google Cloud translation model: {model}")
        if batch_size <= 0 or batch_size > 1024:
            raise BenchmarkError("Google Cloud batches must contain between 1 and 1024 strings")
        self._project = project
        self._location = location
        self._model = model
        self._model_resource = f"projects/{project}/locations/{location}/models/{model}"
        project_part = urllib.parse.quote(project, safe="")
        location_part = urllib.parse.quote(location, safe="")
        self._url = (
            "https://translation.googleapis.com/v3/"
            f"projects/{project_part}/locations/{location_part}:translateText"
        )
        self._timeout_seconds = timeout_seconds
        self._token_provider = token_provider
        self._timestamp_provider = timestamp_provider
        if request_opener is None:
            opener = urllib.request.build_opener()
            self._request_opener = opener.open
        else:
            self._request_opener = request_opener
        self.batch_size = batch_size
        self.execution_metadata: dict[str, Any] = {
            "provider": "Google Cloud Translation v3",
            "project": project,
            "location": location,
            "model": model,
            "modelResource": self._model_resource,
            "benchmarkOnly": True,
            "parallelism": 1,
            "requestCount": 0,
        }

    def translate(
        self,
        texts: Sequence[str],
        source_language: str,
        target_language: str,
    ) -> list[str]:
        if not texts:
            return []
        if len(texts) > 1024:
            raise BenchmarkError("Google Cloud Translation accepts at most 1024 strings per call")
        if any(not isinstance(text, str) or not text for text in texts):
            raise BenchmarkError("Google Cloud benchmark sources must be non-empty strings")
        source_code = language_code(source_language)
        target_code = language_code(target_language)
        requested_at = self._timestamp_provider()
        payload = json.dumps(
            {
                "sourceLanguageCode": source_code,
                "targetLanguageCode": target_code,
                "contents": list(texts),
                "mimeType": "text/plain",
                "model": self._model_resource,
            },
            ensure_ascii=False,
        ).encode("utf-8")
        token = self._token_provider()
        if not isinstance(token, str) or not token.strip():
            raise BenchmarkError("Google credentials returned no access token")
        request = urllib.request.Request(
            self._url,
            data=payload,
            headers={
                "Authorization": f"Bearer {token.strip()}",
                "Content-Type": "application/json; charset=utf-8",
            },
            method="POST",
        )
        try:
            with self._request_opener(request, timeout=self._timeout_seconds) as response:
                body = json.load(response)
            translations = body["translations"]
            outputs = [item["translatedText"] for item in translations]
        except (
            urllib.error.URLError,
            TimeoutError,
            KeyError,
            TypeError,
            json.JSONDecodeError,
        ) as exc:
            raise BenchmarkError(f"Google Cloud Translation request failed: {exc}") from exc
        if len(outputs) != len(texts) or any(
            not isinstance(output, str) or not output.strip() for output in outputs
        ):
            raise BenchmarkError("Google Cloud Translation returned an incomplete or empty batch")
        completed_at = self._timestamp_provider()
        if self.execution_metadata["requestCount"] == 0:
            self.execution_metadata["firstRequestAtUtc"] = requested_at
        self.execution_metadata["lastRequestAtUtc"] = completed_at
        self.execution_metadata["requestCount"] += 1
        return [output.strip() for output in outputs]


def validate_translategemma_snapshot(path: Path, expected_sha256: str) -> str:
    if not path.is_dir():
        raise BenchmarkError(f"Local TranslateGemma snapshot was not found: {path}")
    required_files = ("config.json", "tokenizer_config.json")
    missing = [name for name in required_files if not (path / name).is_file()]
    weights = sorted(path.rglob("*.safetensors"))
    if missing or not weights:
        detail = ", ".join(missing + ([] if weights else ["*.safetensors"]))
        raise BenchmarkError(f"TranslateGemma snapshot is incomplete; missing {detail}")
    actual_sha256 = sha256_directory(path)
    if actual_sha256 != expected_sha256.lower():
        raise BenchmarkError(
            "TranslateGemma snapshot SHA-256 mismatch: "
            f"expected {expected_sha256.lower()}, got {actual_sha256}"
        )
    return actual_sha256


def load_translategemma_runtime(
    model_path: Path, device: str, threads: int
) -> tuple[Any, Any, Any, Any, str]:
    """Lazy, local-only runtime loader kept separate so baseline tests need no PyTorch."""
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    os.environ["HF_DATASETS_OFFLINE"] = "1"
    try:
        import torch
        import transformers
        from transformers import AutoModelForImageTextToText, AutoProcessor
    except ImportError as exc:
        raise BenchmarkError(
            "TranslateGemma benchmarking requires the offline translation dependencies"
        ) from exc
    if threads > 0:
        torch.set_num_threads(threads)
    use_device_map = False
    if device == "cuda":
        if not torch.cuda.is_available():
            raise BenchmarkError("CUDA TranslateGemma was requested but PyTorch cannot use CUDA")
        selected_device = "cuda:0"
    elif device == "cpu":
        selected_device = "cpu"
    elif device == "hybrid":
        if not torch.cuda.is_available():
            raise BenchmarkError("Hybrid TranslateGemma was requested but PyTorch cannot use CUDA")
        try:
            import accelerate  # noqa: F401
        except ImportError as exc:
            raise BenchmarkError(
                "Hybrid TranslateGemma placement requires the offline accelerate package"
            ) from exc
        selected_device = "hybrid-auto"
        use_device_map = True
    else:
        selected_device = "cuda:0" if torch.cuda.is_available() else "cpu"
    try:
        processor = AutoProcessor.from_pretrained(
            str(model_path), local_files_only=True, trust_remote_code=False
        )
        model_arguments: dict[str, Any] = {
            "local_files_only": True,
            "trust_remote_code": False,
            "dtype": torch.bfloat16,
        }
        if use_device_map:
            model_arguments["device_map"] = "auto"
        model = AutoModelForImageTextToText.from_pretrained(str(model_path), **model_arguments)
        if not use_device_map:
            model.to(selected_device)
    except Exception as exc:
        raise BenchmarkError(
            "TranslateGemma could not be loaded from the verified local snapshot"
        ) from exc
    if (
        bool(getattr(model, "is_quantized", False))
        or getattr(model, "hf_quantizer", None) is not None
        or getattr(model, "quantization_method", None) is not None
    ):
        raise BenchmarkError("TranslateGemma benchmark forbids quantized model weights")
    model_dtype = getattr(model, "dtype", None)
    if model_dtype != torch.bfloat16:
        raise BenchmarkError(
            f"TranslateGemma must load as BF16, but the runtime reported {model_dtype}"
        )
    if not getattr(processor, "chat_template", None):
        raise BenchmarkError("TranslateGemma snapshot has no official processor chat template")
    model.eval()
    return torch, transformers, processor, model, selected_device


class TranslateGemmaBackend:
    """Offline BF16 TranslateGemma inference through its official chat template."""

    def __init__(
        self,
        model_path: Path,
        model_id: str,
        revision: str,
        expected_model_sha256: str,
        device: str,
        max_input_tokens: int,
        max_new_tokens: int,
        batch_size: int,
        threads: int = 0,
        runtime_loader: Callable[..., tuple[Any, Any, Any, Any, str]] = (
            load_translategemma_runtime
        ),
    ) -> None:
        if model_id != TRANSLATEGEMMA_MODEL_ID:
            raise BenchmarkError(
                f"TranslateGemma benchmark requires --model-id {TRANSLATEGEMMA_MODEL_ID}"
            )
        if not re.fullmatch(r"[0-9a-fA-F]{40,64}", revision):
            raise BenchmarkError(
                "TranslateGemma requires an immutable 40-64 character hexadecimal revision"
            )
        actual_sha256 = validate_translategemma_snapshot(model_path, expected_model_sha256)
        self._torch, transformers, self._processor, self._model, selected_device = runtime_loader(
            model_path, device, threads
        )
        self._max_input_tokens = max_input_tokens
        self._max_new_tokens = max_new_tokens
        self.batch_size = batch_size
        self.execution_metadata = {
            "device": selected_device,
            "dtype": "bfloat16",
            "quantized": False,
            "localFilesOnly": True,
            "airgap": airgap_mode_enabled(),
            "model": model_id,
            "revision": revision,
            "snapshotSha256": actual_sha256,
            "torchVersion": getattr(self._torch, "__version__", "unknown"),
            "cudaVersion": getattr(getattr(self._torch, "version", None), "cuda", None),
            "transformersVersion": getattr(transformers, "__version__", "unknown"),
            "threads": threads if threads > 0 else "runtime-auto",
            "decoding": "greedy",
            "parallelism": 1,
        }

    def translate(
        self,
        texts: Sequence[str],
        source_language: str,
        target_language: str,
    ) -> list[str]:
        if not texts:
            return []
        source_code = language_code(source_language)
        target_code = language_code(target_language)
        conversations = [
            [
                {
                    "role": "user",
                    "content": [
                        {
                            "type": "text",
                            "source_lang_code": source_code,
                            "target_lang_code": target_code,
                            "text": text,
                        }
                    ],
                }
            ]
            for text in texts
        ]
        try:
            encoded = self._processor.apply_chat_template(
                conversations,
                tokenize=True,
                add_generation_prompt=True,
                return_dict=True,
                return_tensors="pt",
                processor_kwargs={"padding": True, "truncation": False},
            )
        except Exception as exc:
            raise BenchmarkError("TranslateGemma official chat template failed") from exc
        attention_mask = encoded.get("attention_mask")
        input_ids = encoded.get("input_ids")
        if attention_mask is None or input_ids is None or len(attention_mask) != len(texts):
            raise BenchmarkError("TranslateGemma processor did not return a complete input batch")
        for index, row in enumerate(attention_mask):
            token_count = row.sum() if hasattr(row, "sum") else sum(row)
            if hasattr(token_count, "item"):
                token_count = token_count.item()
            if not isinstance(token_count, int) or isinstance(token_count, bool):
                raise BenchmarkError(
                    f"TranslateGemma returned an invalid token count for row {index}"
                )
            if token_count > self._max_input_tokens:
                raise BenchmarkError(
                    f"TranslateGemma source row {index} requires {token_count} tokens, "
                    f"exceeding the {self._max_input_tokens}-token limit; source was not truncated"
                )
        prompt_width = input_ids.shape[-1]
        context_limit = getattr(self._model.config, "max_position_embeddings", None)
        if isinstance(context_limit, int) and prompt_width + self._max_new_tokens > context_limit:
            raise BenchmarkError(
                f"TranslateGemma needs {prompt_width + self._max_new_tokens} context tokens, "
                f"but the verified model limit is {context_limit}; source was not truncated"
            )
        model_device = getattr(self._model, "device", None)
        if hasattr(encoded, "to") and model_device is not None:
            encoded = encoded.to(model_device)
        try:
            with self._torch.inference_mode():
                generated = self._model.generate(
                    **encoded,
                    max_new_tokens=self._max_new_tokens,
                    do_sample=False,
                    return_dict_in_generate=True,
                )
        except Exception as exc:
            detail = f"{type(exc).__name__}: {exc}".strip()
            if len(detail) > 500:
                detail = detail[:497] + "..."
            raise BenchmarkError(f"TranslateGemma generation failed ({detail})") from exc
        sequences = getattr(generated, "sequences", generated)
        completion_sequences = sequences[:, prompt_width:]
        eos_token_id = getattr(self._model.generation_config, "eos_token_id", None)
        if isinstance(eos_token_id, int):
            eos_token_ids = {eos_token_id}
        elif isinstance(eos_token_id, (list, tuple, set)):
            eos_token_ids = {value for value in eos_token_id if isinstance(value, int)}
        else:
            eos_token_ids = set()
        if not eos_token_ids:
            raise BenchmarkError("TranslateGemma has no configured EOS token ID")
        for index, sequence in enumerate(completion_sequences):
            token_ids = sequence.tolist() if hasattr(sequence, "tolist") else list(sequence)
            if not any(token_id in eos_token_ids for token_id in token_ids):
                raise BenchmarkError(
                    f"TranslateGemma row {index} reached the generation bound without EOS"
                )
        decoded = self._processor.batch_decode(completion_sequences, skip_special_tokens=True)
        if len(decoded) != len(texts) or any(
            not isinstance(value, str) or not value.strip() for value in decoded
        ):
            raise BenchmarkError("TranslateGemma returned an incomplete or empty batch")
        return [value.strip() for value in decoded]


def load_forensic_cases(path: Path) -> list[Case]:
    cases: list[Case] = []
    is_bundled_fixture = path.resolve() == DEFAULT_FORENSIC_CASES.resolve()
    seen_ids: set[str] = set()
    with path.open("r", encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            if not line.strip():
                continue
            try:
                row = json.loads(line)
                case_id = row["id"]
                locale = row["language"]
                source = row["source"]
                reference = row["reference"]
                identifiers = row["identifiers"]
            except (json.JSONDecodeError, KeyError, TypeError) as exc:
                raise BenchmarkError(f"Invalid forensic case at line {line_number}: {exc}") from exc
            if (
                not isinstance(row, dict)
                or not isinstance(case_id, str)
                or not case_id.strip()
                or not isinstance(locale, str)
                or not locale.strip()
                or not isinstance(source, str)
                or not source
                or not isinstance(reference, str)
                or not reference
                or not isinstance(identifiers, list)
                or not all(isinstance(value, str) and value for value in identifiers)
            ):
                raise BenchmarkError(f"Invalid forensic case at line {line_number}")
            if case_id in seen_ids:
                raise BenchmarkError(f"Duplicate forensic case id {case_id!r}")
            classification = row.get(
                "corpusClassification", "synthetic" if is_bundled_fixture else "unclassified"
            )
            if classification not in {"public", "synthetic", "unclassified", "restricted"}:
                raise BenchmarkError(f"Invalid corpusClassification at forensic line {line_number}")
            source_language = row.get("sourceLanguage", locale)
            target_language = row.get("targetLanguage", "en")
            if not isinstance(source_language, str) or not isinstance(target_language, str):
                raise BenchmarkError(f"Invalid forensic language metadata at line {line_number}")
            seen_ids.add(case_id)
            case = Case(
                case_id=case_id,
                suite="forensic",
                language=locale,
                source=source,
                reference=reference,
                identifiers=tuple(identifiers),
                source_language=language_code(source_language),
                target_language=language_code(target_language),
                corpus_direction=str(row.get("corpusDirection", "forensic-to-en")),
                corpus_classification=classification,
                domain=str(row.get("domain", "forensic-synthetic")),
                document_id=str(row.get("documentId", case_id)),
                segment_id=str(row.get("segmentId", case_id)),
                expected_hits=parse_expected_hits(row.get("expectedHits"), line_number=line_number),
            )
            validate_expected_hit_fixture(case, line_number=line_number)
            cases.append(case)
    if not cases:
        raise BenchmarkError("The forensic benchmark contains no cases")
    return cases


def select_stratified_cases(candidates: Sequence[Case], limit: int, seed: int) -> list[Case]:
    """Balance domains and documents, with all ties broken by a stable seeded hash."""
    if limit <= 0:
        raise BenchmarkError("The WMT selection limit must be positive")
    if len(candidates) < limit:
        raise BenchmarkError(
            f"WMT24++ supplied {len(candidates)} usable rows, expected at least {limit}"
        )
    grouped: dict[str, dict[str, list[Case]]] = defaultdict(lambda: defaultdict(list))
    for case in candidates:
        grouped[case.domain][case.document_id].append(case)
    domains = sorted(grouped, key=lambda domain: stable_rank(seed, "domain", domain))
    queues: dict[str, deque[tuple[str, deque[Case]]]] = {}
    for domain in domains:
        documents: list[tuple[str, deque[Case]]] = []
        for document_id, rows in grouped[domain].items():
            ordered_rows = sorted(
                rows,
                key=lambda case: stable_rank(
                    seed,
                    "row",
                    case.language,
                    case.domain,
                    case.document_id,
                    case.segment_id,
                    case.source,
                ),
            )
            documents.append((document_id, deque(ordered_rows)))
        documents.sort(key=lambda item: stable_rank(seed, "document", domain, item[0]))
        queues[domain] = deque(documents)

    selected: list[Case] = []
    while len(selected) < limit:
        progressed = False
        for domain in domains:
            document_queue = queues[domain]
            while document_queue and not document_queue[0][1]:
                document_queue.popleft()
            if not document_queue:
                continue
            document_id, row_queue = document_queue.popleft()
            selected.append(row_queue.popleft())
            progressed = True
            if row_queue:
                document_queue.append((document_id, row_queue))
            if len(selected) == limit:
                break
        if not progressed:
            raise BenchmarkError("WMT24++ stratified selection exhausted unexpectedly")
    return selected


def load_wmt_cases(
    root: Path,
    locales: Sequence[str],
    limit: int,
    direction: str = WMT_DIRECTION_LOCALE_TO_EN,
    seed: int = DEFAULT_SAMPLE_SEED,
) -> list[Case]:
    if direction not in WMT_DIRECTIONS:
        raise BenchmarkError(f"Unsupported WMT24++ direction: {direction}")
    cases: list[Case] = []
    for locale in locales:
        path = root / f"en-{locale}.jsonl"
        if not path.is_file():
            raise BenchmarkError(f"WMT24++ locale file was not found: {path}")
        candidates: list[Case] = []
        with path.open("r", encoding="utf-8") as handle:
            for line_number, line in enumerate(handle, start=1):
                if not line.strip():
                    continue
                try:
                    row = json.loads(line)
                except json.JSONDecodeError as exc:
                    raise BenchmarkError(
                        f"Invalid WMT JSON at {path}:{line_number}: {exc}"
                    ) from exc
                if not isinstance(row, dict):
                    raise BenchmarkError(f"Invalid WMT object at {path}:{line_number}")
                if row.get("is_bad_source"):
                    continue
                original_english = row.get("source")
                locale_post_edit = row.get("target")
                if (
                    not isinstance(original_english, str)
                    or not original_english.strip()
                    or not isinstance(locale_post_edit, str)
                    or not locale_post_edit.strip()
                ):
                    continue
                if direction == WMT_DIRECTION_EN_TO_LOCALE:
                    source = original_english
                    reference = locale_post_edit
                    source_language = "en"
                    target_language = language_code(locale)
                else:
                    source = locale_post_edit
                    reference = original_english
                    source_language = language_code(locale)
                    target_language = "en"
                domain = _string_metadata(row, ("domain", "text_domain", "category"), "unknown")
                document_id = _string_metadata(
                    row,
                    ("document_id", "documentId", "doc_id", "docid", "document"),
                    f"line-{line_number}",
                )
                segment_id = _string_metadata(
                    row,
                    ("segment_id", "segmentId", "seg_id", "id"),
                    str(line_number),
                )
                candidates.append(
                    Case(
                        case_id=f"wmt24pp-{locale}-{segment_id}-{line_number}",
                        suite="wmt24pp",
                        language=locale,
                        source=source,
                        reference=reference,
                        identifiers=(),
                        source_language=source_language,
                        target_language=target_language,
                        corpus_direction=direction,
                        corpus_classification="public",
                        domain=domain,
                        document_id=document_id,
                        segment_id=segment_id,
                    )
                )
        if len(candidates) < limit:
            raise BenchmarkError(
                f"WMT24++ {locale} supplied {len(candidates)} usable rows, expected {limit}"
            )
        cases.extend(select_stratified_cases(candidates, limit, seed))
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
    if backend.batch_size <= 0:
        raise BenchmarkError("Translation backend batch size must be positive")
    buckets: dict[tuple[str, str], list[tuple[int, Case]]] = defaultdict(list)
    for index, case in enumerate(cases):
        source_language = language_code(case.source_language or case.language)
        target_language = language_code(case.target_language)
        buckets[(source_language, target_language)].append((index, case))

    indexed_results: list[tuple[int, Result]] = []
    completed = 0
    for (source_language, target_language), indexed_cases in buckets.items():
        for start in range(0, len(indexed_cases), backend.batch_size):
            indexed_batch = indexed_cases[start : start + backend.batch_size]
            batch = [case for _, case in indexed_batch]
            started = time.perf_counter()
            translated = backend.translate(
                [case.source for case in batch], source_language, target_language
            )
            batch_latency = time.perf_counter() - started
            if len(translated) != len(batch):
                raise BenchmarkError(
                    f"Backend returned {len(translated)} outputs for {len(batch)} inputs"
                )
            average_latency = batch_latency / len(batch)
            for (original_index, case), output in zip(indexed_batch, translated, strict=True):
                if not isinstance(output, str) or not output.strip():
                    raise BenchmarkError(f"Backend returned an empty output for {case.case_id!r}")
                hypothesis = output.strip()
                retained = tuple(
                    identifier for identifier in case.identifiers if identifier in hypothesis
                )
                invariant_deltas: list[InvariantDelta] = []
                for identifier in case.identifiers:
                    expected_count = case.source.count(identifier)
                    observed_count = hypothesis.count(identifier)
                    invariant_deltas.append(
                        InvariantDelta(
                            identifier=identifier,
                            expected_count=expected_count,
                            observed_count=observed_count,
                            omitted_count=max(expected_count - observed_count, 0),
                            added_count=(observed_count if expected_count == 0 else 0),
                            duplicate_count=(
                                max(observed_count - expected_count, 0) if expected_count > 0 else 0
                            ),
                        )
                    )
                expected_hit_observations = tuple(
                    ExpectedHitObservation(
                        pattern_id=expected_hit.pattern_id,
                        expected_values=expected_hit.expected_values,
                        observed_values=regex_values(expected_hit.regex, hypothesis),
                    )
                    for expected_hit in case.expected_hits
                )
                indexed_results.append(
                    (
                        original_index,
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
                            source_language=source_language,
                            target_language=target_language,
                            corpus_direction=case.corpus_direction,
                            corpus_classification=case.corpus_classification,
                            domain=case.domain,
                            document_id=case.document_id,
                            segment_id=case.segment_id,
                            invariant_deltas=tuple(invariant_deltas),
                            expected_hit_observations=expected_hit_observations,
                        ),
                    )
                )
            completed += len(batch)
            if progress:
                print(
                    f"completed {completed}/{len(cases)} benchmark cases",
                    file=sys.stderr,
                )
    indexed_results.sort(key=lambda item: item[0])
    return [result for _, result in indexed_results]


def chrf_score(results: Iterable[Result]) -> float:
    CHRF = require_chrf()
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


def require_chrf() -> Any:
    """Fail before model startup when the benchmark-only scorer is unavailable."""
    try:
        from sacrebleu.metrics import CHRF
    except ImportError as exc:
        raise BenchmarkError("Install the benchmark extra to calculate chrF++") from exc
    return CHRF


def distribution(values: Iterable[str]) -> dict[str, int]:
    return dict(sorted(Counter(values).items()))


def corpus_metadata(
    cases: Sequence[Case], input_files: Sequence[Path], sample_seed: int
) -> dict[str, Any]:
    unique_paths = sorted({path.resolve() for path in input_files}, key=str)
    return {
        "selection": {
            "strategy": "seeded-domain-document-round-robin",
            "seed": sample_seed,
        },
        "selectedCaseSha256": canonical_cases_sha256(cases),
        "inputFiles": [
            {
                "path": str(path),
                "sha256": sha256_file(path),
                "bytes": path.stat().st_size,
            }
            for path in unique_paths
        ],
        "distribution": {
            "suite": distribution(case.suite for case in cases),
            "language": distribution(case.language for case in cases),
            "direction": distribution(case.corpus_direction for case in cases),
            "classification": distribution(case.corpus_classification for case in cases),
            "domain": distribution(case.domain for case in cases),
            "document": distribution(case.document_id for case in cases),
            "languagePair": distribution(
                f"{case.source_language}->{case.target_language}" for case in cases
            ),
        },
    }


def expected_hit_score(results: Sequence[Result]) -> dict[str, Any]:
    true_positives = 0
    false_positives = 0
    false_negatives = 0
    observations = 0
    failures: list[dict[str, Any]] = []
    for result in results:
        for observation in result.expected_hit_observations:
            observations += 1
            expected = Counter(observation.expected_values)
            observed = Counter(observation.observed_values)
            true_positives += sum((expected & observed).values())
            missing = expected - observed
            unexpected = observed - expected
            false_negatives += sum(missing.values())
            false_positives += sum(unexpected.values())
            if missing or unexpected:
                failures.append(
                    {
                        "caseId": result.case_id,
                        "patternId": observation.pattern_id,
                        "missing": list(missing.elements()),
                        "unexpected": list(unexpected.elements()),
                    }
                )
    precision_denominator = true_positives + false_positives
    recall_denominator = true_positives + false_negatives
    precision = true_positives / precision_denominator if precision_denominator else 1.0
    recall = true_positives / recall_denominator if recall_denominator else 1.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {
        "enabled": observations > 0,
        "patterns": observations,
        "truePositives": true_positives,
        "falsePositives": false_positives,
        "falseNegatives": false_negatives,
        "precision": precision,
        "recall": recall,
        "f1": f1,
        "failures": failures,
    }


def summarize(results: Sequence[Result], metadata: dict[str, Any]) -> dict[str, Any]:
    forensic = [result for result in results if result.suite == "forensic"]
    wmt = [result for result in results if result.suite == "wmt24pp"]
    identifier_total = sum(len(result.identifiers) for result in forensic)
    identifier_retained = sum(len(result.retained_identifiers) for result in forensic)
    invariant_deltas = [delta for result in forensic for delta in result.invariant_deltas]
    latencies = [result.latency_seconds for result in results]
    elapsed = sum(latencies)
    languages = sorted({result.language for result in wmt})
    return {
        "metadata": metadata,
        "cases": len(results),
        "wmt24ppChrfPlusPlus": chrf_score(wmt),
        "forensicChrfPlusPlus": chrf_score(forensic),
        "identifierRetention": {
            "retained": identifier_retained,
            "total": identifier_total,
            "percent": (
                100.0 * identifier_retained / identifier_total if identifier_total else 100.0
            ),
            "failures": [
                {
                    "caseId": result.case_id,
                    "missing": sorted(set(result.identifiers) - set(result.retained_identifiers)),
                }
                for result in forensic
                if len(result.retained_identifiers) != len(result.identifiers)
            ],
        },
        "invariantIntegrity": {
            "identifiers": len(invariant_deltas),
            "expectedOccurrences": sum(delta.expected_count for delta in invariant_deltas),
            "observedOccurrences": sum(delta.observed_count for delta in invariant_deltas),
            "omittedOccurrences": sum(delta.omitted_count for delta in invariant_deltas),
            "addedOccurrences": sum(delta.added_count for delta in invariant_deltas),
            "duplicateOccurrences": sum(delta.duplicate_count for delta in invariant_deltas),
            "exactIdentifiers": sum(
                delta.omitted_count == 0 and delta.added_count == 0 and delta.duplicate_count == 0
                for delta in invariant_deltas
            ),
            "failures": [
                {
                    "caseId": result.case_id,
                    "deltas": [
                        asdict(delta)
                        for delta in result.invariant_deltas
                        if delta.omitted_count or delta.added_count or delta.duplicate_count
                    ],
                }
                for result in forensic
                if any(
                    delta.omitted_count or delta.added_count or delta.duplicate_count
                    for delta in result.invariant_deltas
                )
            ],
        },
        "downstreamExpectedHits": expected_hit_score(forensic),
        "wmt24ppChrfPlusPlusByLanguage": {
            language: chrf_score(result for result in wmt if result.language == language)
            for language in languages
        },
        "latencySeconds": {
            "total": elapsed,
            "p50": statistics.median(latencies) if latencies else 0.0,
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
        return OpenAIChatBackend(
            args.endpoint,
            args.timeout_seconds,
            args.max_new_tokens,
            args.parallelism,
            args.strict_determinism,
        )
    if args.engine == "llama-cpp":
        translator = LlamaCppTranslator(
            server=args.llama_server,
            model_path=args.model_path,
            model_id=args.model_id,
            revision=args.model_revision,
            expected_model_sha256=args.model_sha256,
            device=args.device,
            max_input_tokens=args.max_input_tokens,
            max_new_tokens=args.max_new_tokens,
            startup_timeout_seconds=args.startup_timeout_seconds,
            request_timeout_seconds=int(args.timeout_seconds),
            batch_size=args.batch_size,
            parallelism=args.parallelism,
            strict_determinism=args.strict_determinism,
            threads=args.threads,
            gpu_layers=args.gpu_layers,
        )
        return LlamaCppBackend(translator, args.batch_size)
    if args.engine == "google-cloud":
        return GoogleCloudBackend(
            project=args.google_project,
            location=args.google_location,
            model=args.google_model,
            timeout_seconds=args.timeout_seconds,
            batch_size=args.batch_size,
        )
    if args.engine == "translategemma":
        return TranslateGemmaBackend(
            model_path=args.model_path,
            model_id=args.model_id,
            revision=args.model_revision,
            expected_model_sha256=args.model_sha256,
            device=args.device,
            max_input_tokens=args.max_input_tokens,
            max_new_tokens=args.max_new_tokens,
            batch_size=args.batch_size,
            threads=args.threads,
        )
    if args.engine != "madlad":
        raise BenchmarkError(f"Unsupported translation benchmark engine: {args.engine}")
    translator = MadladTranslator(
        args.model_path,
        args.model_id,
        args.model_revision,
        args.model_sha256,
        args.device,
        args.max_input_tokens,
        args.max_new_tokens,
        args.threads,
    )
    return MadladBackend(translator, args.batch_size)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--engine",
        choices=("openai", "madlad", "llama-cpp", "translategemma", "google-cloud"),
        required=True,
    )
    parser.add_argument("--endpoint", default="http://127.0.0.1:18089")
    parser.add_argument("--llama-server", default="llama-server")
    parser.add_argument("--model-path", type=Path)
    parser.add_argument("--model-id")
    parser.add_argument("--model-revision")
    parser.add_argument("--model-sha256")
    parser.add_argument("--runtime")
    parser.add_argument("--device", choices=("auto", "cpu", "cuda", "hybrid"), default="auto")
    parser.add_argument("--google-project")
    parser.add_argument("--google-location", default="global")
    parser.add_argument("--google-model", choices=GOOGLE_MODELS, default="general/translation-llm")
    parser.add_argument(
        "--allow-google-cloud-public-benchmark",
        action="store_true",
        help="Acknowledge that public/synthetic benchmark rows leave this machine",
    )
    parser.add_argument("--wmt-root", required=True, type=Path)
    parser.add_argument(
        "--forensic-cases",
        type=Path,
        default=DEFAULT_FORENSIC_CASES,
    )
    parser.add_argument("--locales", nargs="+", default=list(DEFAULT_LOCALES))
    parser.add_argument("--wmt-per-locale", type=int, default=5)
    parser.add_argument(
        "--wmt-direction",
        choices=WMT_DIRECTIONS,
        default=WMT_DIRECTION_LOCALE_TO_EN,
    )
    parser.add_argument("--sample-seed", type=int, default=DEFAULT_SAMPLE_SEED)
    parser.add_argument("--batch-size", type=int, default=8)
    parser.add_argument("--parallelism", type=int, default=1)
    parser.add_argument("--strict-determinism", action="store_true")
    parser.add_argument("--threads", type=int, default=0)
    parser.add_argument("--gpu-layers", type=int, default=-1)
    parser.add_argument("--max-input-tokens", type=int, default=DEFAULT_MAX_INPUT_TOKENS)
    parser.add_argument("--max-source-characters", type=int, default=DEFAULT_MAX_SOURCE_CHARACTERS)
    parser.add_argument("--max-new-tokens", type=int, default=512)
    parser.add_argument("--timeout-seconds", type=float, default=120)
    parser.add_argument("--startup-timeout-seconds", type=int, default=120)
    parser.add_argument("--output", required=True, type=Path)
    return parser.parse_args()


def validate_arguments(args: argparse.Namespace) -> None:
    engine = args.engine
    wmt_per_locale = getattr(args, "wmt_per_locale", 0)
    batch_size = getattr(args, "batch_size", 0)
    parallelism = getattr(args, "parallelism", 1)
    strict_determinism = getattr(args, "strict_determinism", False)
    model_path = getattr(args, "model_path", None)
    threads = getattr(args, "threads", 0)
    startup_timeout = getattr(args, "startup_timeout_seconds", 120)
    gpu_layers = getattr(args, "gpu_layers", -1)
    device = getattr(args, "device", "auto")
    model_sha256 = getattr(args, "model_sha256", None)
    max_input_tokens = getattr(args, "max_input_tokens", DEFAULT_MAX_INPUT_TOKENS)
    max_new_tokens = getattr(args, "max_new_tokens", 512)
    max_source_characters = getattr(args, "max_source_characters", DEFAULT_MAX_SOURCE_CHARACTERS)
    if wmt_per_locale <= 0 or batch_size <= 0 or parallelism < 0:
        raise BenchmarkError("Benchmark limits and batch size must be positive")
    if max_input_tokens < DEFAULT_MAX_SOURCE_CHARACTERS:
        raise BenchmarkError(
            "--max-input-tokens must be at least 2048 for the product input contract"
        )
    if max_new_tokens <= 0 or max_source_characters < DEFAULT_MAX_SOURCE_CHARACTERS:
        raise BenchmarkError("Translation input and output limits are too small")
    if engine != "llama-cpp" and parallelism == 0:
        raise BenchmarkError("Only the llama.cpp benchmark supports automatic parallelism")
    if strict_determinism and parallelism > 1:
        raise BenchmarkError("Strict determinism requires --parallelism 0 or 1")
    if engine in {"madlad", "llama-cpp", "translategemma"} and model_path is None:
        raise BenchmarkError("--model-path is required for local model engines")
    if threads < 0 or startup_timeout <= 0:
        raise BenchmarkError("Threads cannot be negative and startup timeout must be positive")
    if gpu_layers < -1:
        raise BenchmarkError("--gpu-layers must be -1 or non-negative")
    if engine == "llama-cpp":
        if device == "cpu" and gpu_layers not in {-1, 0}:
            raise BenchmarkError("CPU translation cannot use --gpu-layers above zero")
        if device in {"auto", "cuda"} and gpu_layers != -1:
            raise BenchmarkError("Use hybrid mode with an exact --gpu-layers value")
        if device == "hybrid" and gpu_layers <= 0:
            raise BenchmarkError(
                "Hybrid translation requires an explicit --gpu-layers value above zero"
            )
    elif gpu_layers != -1:
        raise BenchmarkError("--gpu-layers is available only for llama.cpp")
    if engine == "madlad" and device == "hybrid":
        raise BenchmarkError("Hybrid device selection is available only for llama.cpp")
    if engine in {"openai", "madlad", "llama-cpp", "translategemma"}:
        if not getattr(args, "model_id", None) or not getattr(args, "model_revision", None):
            raise BenchmarkError("--model-id and --model-revision are required")
        if (
            not isinstance(model_sha256, str)
            or len(model_sha256) != 64
            or any(character not in "0123456789abcdefABCDEF" for character in model_sha256)
        ):
            raise BenchmarkError("--model-sha256 must be a 64-character hexadecimal digest")
    if engine == "translategemma" and getattr(args, "model_id", None) != TRANSLATEGEMMA_MODEL_ID:
        raise BenchmarkError(
            f"TranslateGemma benchmark requires --model-id {TRANSLATEGEMMA_MODEL_ID}"
        )
    if engine == "google-cloud":
        if not getattr(args, "google_project", None):
            raise BenchmarkError("--google-project is required for Google Cloud benchmarking")
        if getattr(args, "google_model", None) not in GOOGLE_MODELS:
            raise BenchmarkError("A supported --google-model is required")


def main() -> int:
    args = parse_arguments()
    backend: TranslationBackend | None = None
    try:
        validate_arguments(args)
        require_chrf()
        benchmark_started_at = utc_timestamp()
        cases = load_wmt_cases(
            args.wmt_root,
            args.locales,
            args.wmt_per_locale,
            args.wmt_direction,
            args.sample_seed,
        )
        cases.extend(load_forensic_cases(args.forensic_cases))
        source_capacity = validate_source_capacity(cases, args.max_source_characters)
        if args.engine == "google-cloud":
            validate_google_corpus(cases, args.allow_google_cloud_public_benchmark)
        else:
            enable_airgap_mode()
        backend = build_backend(args)
        results = run_cases(backend, cases)
        model_id = args.google_model if args.engine == "google-cloud" else args.model_id
        metadata = {
            "engine": args.engine,
            "model": model_id,
            "revision": args.model_revision,
            "modelSha256": args.model_sha256.lower() if args.model_sha256 else None,
            "runtime": args.runtime or args.engine,
            "benchmarkStartedAtUtc": benchmark_started_at,
            "benchmarkCompletedAtUtc": utc_timestamp(),
            "wmtLocales": args.locales,
            "wmtRowsPerLocale": args.wmt_per_locale,
            "wmtDirection": args.wmt_direction,
            "wmtDirectionDefinition": (
                {
                    "source": "original English source",
                    "reference": "locale post-edit/translationese",
                }
                if args.wmt_direction == WMT_DIRECTION_EN_TO_LOCALE
                else {
                    "source": "locale post-edit/translationese",
                    "reference": "original English source",
                }
            ),
            "greedyDecoding": True,
            "strictDeterminism": args.strict_determinism,
            "parallelism": 1 if args.strict_determinism else args.parallelism,
            "promptCache": not args.strict_determinism,
            "sourceTruncation": False,
            "maxInputTokens": args.max_input_tokens,
            "maxNewTokens": args.max_new_tokens,
            "sourceCapacity": source_capacity,
            "corpus": corpus_metadata(
                cases,
                [
                    *(args.wmt_root / f"en-{locale}.jsonl" for locale in args.locales),
                    args.forensic_cases,
                ],
                args.sample_seed,
            ),
        }
        execution_metadata = getattr(backend, "execution_metadata", None)
        if execution_metadata:
            metadata["execution"] = dict(execution_metadata)
            if "parallelism" in execution_metadata:
                metadata["parallelism"] = execution_metadata["parallelism"]
        summary = summarize(results, metadata)
        write_json(args.output, summary)
        write_jsonl(args.output.with_suffix(".records.jsonl"), (asdict(row) for row in results))
        print(json.dumps(summary, ensure_ascii=False, indent=2))
        return 0
    except (BenchmarkError, EnrichmentError, OSError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        return 2
    finally:
        close = getattr(backend, "close", None)
        if close is not None:
            close()


if __name__ == "__main__":
    raise SystemExit(main())
