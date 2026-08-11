#!/usr/bin/env python3
"""Route files with Magika, recover executable strings with FLOSS, and emit bstrings JSONL."""

from __future__ import annotations

import argparse
import atexit
import codecs
import ctypes
import hashlib
import ipaddress
import json
import math
import os
import re
import shutil
import socket
import sqlite3
import stat
import subprocess
import sys
import tempfile
import threading
import time
import unicodedata
import urllib.error
import urllib.request
from collections import Counter, OrderedDict
from collections.abc import Callable, Iterable, Sequence
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack, suppress
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol, TypeVar

SCHEMA_VERSION = 1
MAX_PATH_LIST_LINE_CHARACTERS = 32 * 1024
MAX_INPUT_MANIFEST_LINE_CHARACTERS = 256 * 1024
MAX_JSONL_LINE_CHARACTERS = 16 * 1024 * 1024
MAX_FLOSS_JSON_ITEM_BYTES = MAX_JSONL_LINE_CHARACTERS
MAX_JSON_DEPTH = 256
JSON_READ_CHUNK_BYTES = 64 * 1024
JSON_STRING_SPECIAL = re.compile(rb'[\x00-\x1f"\\]')
MAGIKA_BATCH_MAX_PATHS = 2_048
MAGIKA_BATCH_MAX_COMMAND_CHARACTERS = 24 * 1024
OCR_IMAGE_EXTENSIONS = frozenset(
    (".bmp", ".gif", ".ico", ".jfif", ".jpeg", ".jpg", ".png", ".tif", ".tiff", ".webp")
)
OCR_MAGIKA_LABELS = frozenset(("bmp", "gif", "ico", "jpeg", "jpg", "pdf", "png", "tiff", "webp"))
PE_EXTENSIONS = frozenset((".cpl", ".dll", ".efi", ".exe", ".ocx", ".scr", ".sys"))
DEFAULT_MODEL_ID = "google/madlad400-3b-mt"
DEFAULT_LLAMA_MODEL_ID = "tencent/Hy-MT2-7B-GGUF"
LANGUAGE_NAMES = {
    "ar": "Arabic",
    "bn": "Bengali",
    "bo": "Tibetan",
    "cs": "Czech",
    "de": "German",
    "en": "English",
    "es": "Spanish",
    "fa": "Persian",
    "gu": "Gujarati",
    "he": "Hebrew",
    "fr": "French",
    "hi": "Hindi",
    "id": "Indonesian",
    "it": "Italian",
    "ja": "Japanese",
    "kk": "Kazakh",
    "km": "Khmer",
    "ko": "Korean",
    "mn": "Mongolian",
    "mr": "Marathi",
    "ms": "Malay",
    "my": "Burmese",
    "nl": "Dutch",
    "pl": "Polish",
    "pt": "Portuguese",
    "ru": "Russian",
    "ta": "Tamil",
    "te": "Telugu",
    "th": "Thai",
    "tl": "Filipino",
    "tr": "Turkish",
    "ug": "Uyghur",
    "uk": "Ukrainian",
    "ur": "Urdu",
    "vi": "Vietnamese",
    "yue": "Cantonese",
    "zh": "Chinese",
    "zh-hant": "Traditional Chinese",
}
FLOSS_STRING_CATEGORIES = (
    "static_strings",
    "language_strings",
    "language_strings_missed",
    "stack_strings",
    "tight_strings",
    "decoded_strings",
)
FLOSS_STRING_CATEGORY_SET = frozenset(FLOSS_STRING_CATEGORIES)
FLOSS_STATIC_CATEGORIES = frozenset(
    ("static_strings", "language_strings", "language_strings_missed")
)
FLOSS_STACK_CATEGORIES = frozenset(("stack_strings", "tight_strings"))
FLOSS_ROOT_KEYS = frozenset(("analysis", "metadata", "strings"))
FLOSS_STRING_ENCODINGS = frozenset(("ASCII", "UTF-16LE", "UTF-8"))
FLOSS_ADDRESS_TYPES = frozenset(("STACK", "GLOBAL", "HEAP"))
FLOSS_STATIC_ITEM_FIELDS = frozenset(("string", "offset", "encoding"))
FLOSS_STACK_ITEM_FIELDS = frozenset(
    (
        "function",
        "string",
        "encoding",
        "program_counter",
        "stack_pointer",
        "original_stack_pointer",
        "offset",
        "frame_offset",
    )
)
FLOSS_DECODED_ITEM_FIELDS = frozenset(
    ("address", "address_type", "string", "encoding", "decoded_at", "decoding_routine")
)
UINT64_MAX = (1 << 64) - 1
INT64_MIN = -(1 << 63)
INT64_MAX = (1 << 63) - 1
HARD_IDENTIFIER_PATTERNS = tuple(
    re.compile(pattern, re.IGNORECASE)
    for pattern in (
        r"https?://[^\s<>\"']+",
        r"\b[A-Z]:\\[^\s<>\"']+",
        r"\b(?:HKLM|HKCU|HKCR|HKU|HKCC|HKEY_[A-Z_]+)\\[^\s<>\"']+",
        r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,63}\b",
        r"\b(?:[0-9A-F]{64}|[0-9A-F]{40}|[0-9A-F]{32})\b",
        r"\b[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}\b",
        r"\bCVE-[0-9]{4}-[0-9]{4,}\b",
        r"\b(?:[0-9]{1,3}\.){3}[0-9]{1,3}\b",
        r"\b(?:(?=[A-Z0-9.-]*[A-Z])[A-Z0-9](?:[A-Z0-9.-]*[A-Z0-9])?|(?:[0-9]{1,3}\.){3}[0-9]{1,3}):[0-9]{1,5}\b",
        r"\b[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?(?:\.[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?)+\b",
        r"\b[A-Z0-9_-]+\.(?:7Z|BAT|BIN|CAB|CFG|CONF|CSV|DLL|DOCX?|EXE|INI|JAR|JSONL?|LOG|PDF|PNG|PS1|RAR|RAW|SYS|TXT|XLSX?|XML|ZIP)\b",
        r"\$\{[A-Z0-9_.-]+\}|\{[A-Z0-9_.-]+\}|%[A-Z0-9_]+%",
    )
)
FIXED_ASCII_HARD_IDENTIFIER_PATTERNS = tuple(
    re.compile(pattern)
    for pattern in (
        r"(?<![0-9A-Fa-f])(?:[0-9A-Fa-f]{64}|[0-9A-Fa-f]{40}|[0-9A-Fa-f]{32})(?![0-9A-Fa-f])",
        r"(?<![0-9A-Fa-f])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f])",
        r"(?<![A-Za-z0-9])CVE-[0-9]{4}-[0-9]{4,}(?![A-Za-z0-9])",
        r"(?<![0-9.])(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?![0-9.])",
    )
)
HARD_PATH_IDENTIFIER_PATTERNS = tuple(
    re.compile(pattern, re.IGNORECASE)
    for pattern in (
        r"\b[A-Z]:/[^\s<>\"']+",
        r"\\\\[^\\\s<>\"']+\\[^\\\s<>\"']+(?:\\[^\\\s<>\"']+)*",
        r"/(?:[^/\\\s<>\"']+/)*[^/\\\s<>\"']+",
    )
)
PROTECTED_IDENTIFIER_PATTERNS = HARD_IDENTIFIER_PATTERNS
_IDENTIFIER_SEPARATORS = frozenset(
    ("-", "_", "\u2010", "\u2011", "\u2012", "\u2013", "\u2014", "\u2212", "\uff0d")
)
_UNCONDITIONAL_TRAILING_IDENTIFIER_PUNCTUATION = ".,;:!?\u3002\uff0c\uff1b\uff1a\uff01\uff1f"
_BALANCED_IDENTIFIER_CLOSERS = {")": "(", "]": "[", "}": "{"}
_BRACKETED_IPV6_PORT_PATTERN = re.compile(
    r"\[(?P<address>[0-9A-Fa-f:.]+(?:%[A-Za-z0-9_.-]+)?)\]:(?P<port>[0-9]{1,5})"
)
_BARE_IPV6_CANDIDATE_PATTERN = re.compile(r"[0-9A-Fa-f:.]+(?:%[A-Za-z0-9_.-]+)?")
TRANSLATION_PROMPT_VERSION = "llama-translation-prompt-v1"
TRANSLATION_PRESERVATION_POLICY_VERSION = "translation-integrity-v2"
TRANSLATION_CACHE_SCHEMA_VERSION = 1
TRANSLATION_FALLBACK_RATE_NUMERATOR = 1
TRANSLATION_FALLBACK_RATE_DENOMINATOR = 100
TRANSLATION_FALLBACK_MINIMUM_RESULTS = 100
TRANSLATION_FALLBACK_CONSECUTIVE_LIMIT = 100
TRANSLATION_CACHE_COMMIT_BATCH_SIZE = 4096
ACCEPTED_CUDA_COMPUTE_CAPABILITIES = frozenset(("8.9",))
MINIMUM_ACCEPTED_CUDA_FREE_MEMORY_MIB = 7000
T = TypeVar("T")
U = TypeVar("U")
_AIRGAP_ENABLED = False

AIRGAP_ENVIRONMENT = {
    "DO_NOT_TRACK": "1",
    "HF_DATASETS_OFFLINE": "1",
    "HF_HUB_DISABLE_TELEMETRY": "1",
    "HF_HUB_OFFLINE": "1",
    "PIP_DISABLE_PIP_VERSION_CHECK": "1",
    "PIP_NO_INDEX": "1",
    "PYTHONDONTWRITEBYTECODE": "1",
    "PYTHONNOUSERSITE": "1",
    "TRANSFORMERS_OFFLINE": "1",
    "UV_OFFLINE": "1",
}


class EnrichmentError(RuntimeError):
    """Raised when an enrichment stage cannot produce complete, attributable output."""


class TranslationRowError(EnrichmentError):
    """A bounded single-row model failure that can retain the exact source as evidence."""

    def __init__(self, reason: str, diagnostic: str) -> None:
        super().__init__(diagnostic)
        self.reason = reason


class AirgapNetworkError(EnrichmentError):
    """Raised when air-gap mode observes a non-loopback network attempt."""


def _is_loopback_host(host: object) -> bool:
    if isinstance(host, bytes):
        host = host.decode("ascii", errors="ignore")
    if not isinstance(host, str):
        return False
    normalized = host.strip().lower().rstrip(".")
    if normalized == "localhost":
        return True
    with suppress(ValueError):
        return ipaddress.ip_address(normalized.split("%", maxsplit=1)[0]).is_loopback
    return False


def _airgap_audit_hook(event: str, arguments: tuple[Any, ...]) -> None:
    if event == "socket.connect" and len(arguments) >= 2:
        address = arguments[1]
        if isinstance(address, tuple) and address and not _is_loopback_host(address[0]):
            raise AirgapNetworkError(
                f"Air-gap mode blocked a non-loopback connection to {address[0]!r}"
            )
    elif event == "socket.getaddrinfo" and arguments:
        host = arguments[0]
        if host is not None and not _is_loopback_host(host):
            raise AirgapNetworkError(f"Air-gap mode blocked DNS resolution for {host!r}")


def enable_airgap_mode() -> None:
    """Disable online package/model behavior and reject non-loopback Python sockets."""
    global _AIRGAP_ENABLED
    for name, value in AIRGAP_ENVIRONMENT.items():
        os.environ[name] = value
    no_proxy = "127.0.0.1,localhost,::1"
    dead_proxy = "http://127.0.0.1:9"
    for name in ("NO_PROXY", "no_proxy"):
        os.environ[name] = no_proxy
    for name in (
        "ALL_PROXY",
        "HTTPS_PROXY",
        "HTTP_PROXY",
        "all_proxy",
        "https_proxy",
        "http_proxy",
    ):
        os.environ[name] = dead_proxy
    if not _AIRGAP_ENABLED:
        sys.addaudithook(_airgap_audit_hook)
        _AIRGAP_ENABLED = True


def airgap_mode_enabled() -> bool:
    return _AIRGAP_ENABLED


@dataclass(frozen=True)
class Classification:
    label: str
    score: float
    is_text: bool
    mime_type: str
    group: str


@dataclass(frozen=True)
class InputIdentity:
    path: str
    length: int
    sha256: str


@dataclass(frozen=True)
class RoutingInput:
    path: Path
    identity: InputIdentity


class EvidenceReadLease:
    """Prevent write/delete sharing while an external Windows reader uses a source path."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self._handle: int | None = None
        self._portable_handle: Any | None = None

    def __enter__(self) -> EvidenceReadLease:
        if os.name != "nt":
            self._portable_handle = self.path.open("rb")
            return self
        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        create_file = kernel32.CreateFileW
        create_file.argtypes = [
            ctypes.c_wchar_p,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_void_p,
            ctypes.c_uint32,
            ctypes.c_uint32,
            ctypes.c_void_p,
        ]
        create_file.restype = ctypes.c_void_p
        handle = create_file(
            str(self.path),
            0x80000000,  # GENERIC_READ
            0x00000001,  # FILE_SHARE_READ only: deny write and delete sharing
            None,
            3,  # OPEN_EXISTING
            0x08000000,  # FILE_FLAG_SEQUENTIAL_SCAN
            None,
        )
        invalid_handle = ctypes.c_void_p(-1).value
        if handle in (None, invalid_handle):
            error = ctypes.get_last_error()
            raise EnrichmentError(
                f"Could not acquire an immutable read lease for routed evidence (WinError {error})"
            )
        self._handle = int(handle)
        return self

    def __exit__(self, *_: object) -> None:
        if self._portable_handle is not None:
            self._portable_handle.close()
            self._portable_handle = None
        if self._handle is not None:
            kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
            kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
            kernel32.CloseHandle.restype = ctypes.c_int
            kernel32.CloseHandle(ctypes.c_void_p(self._handle))
            self._handle = None


@dataclass(frozen=True)
class MagikaClassification:
    status: str
    output: Classification
    raw_prediction: Classification
    error_code: str | None = None


def _magika_batch_errors(
    inputs: Sequence[RoutingInput], error_code: str
) -> list[MagikaClassification]:
    unknown = Classification("unknown", 0.0, False, "application/octet-stream", "unknown")
    return [MagikaClassification("error", unknown, unknown, error_code) for _ in inputs]


class Translator(Protocol):
    engine: str
    engine_version: str
    model_id: str
    revision: str
    model_sha256: str
    parallelism: int
    execution_metadata: dict[str, Any]

    def translate(self, texts: Sequence[str], target_language: str) -> list[str]: ...


class TranslationCache:
    """Bounded LRU for exact translations within one examination run."""

    def __init__(self, capacity: int) -> None:
        if capacity < 0:
            raise ValueError("Translation cache capacity cannot be negative")
        self.capacity = capacity
        self._values: OrderedDict[tuple[str, str], str] = OrderedDict()

    def get(self, target_language: str, text: str) -> tuple[bool, str]:
        key = (target_language, text)
        value = self._values.get(key)
        if value is None:
            return False, ""
        self._values.move_to_end(key)
        return True, value

    def put(self, target_language: str, text: str, translated_text: str) -> None:
        if self.capacity == 0:
            return
        key = (target_language, text)
        self._values[key] = translated_text
        self._values.move_to_end(key)
        while len(self._values) > self.capacity:
            self._values.popitem(last=False)


@dataclass(frozen=True)
class TranslationOutcome:
    text: str
    integrity: str
    reason: str | None = None
    ambiguous_identifier_count: int = 0


@dataclass(frozen=True)
class TranslationAttempt:
    """One model attempt; failures deliberately carry no rejected model text."""

    text: str | None
    failure_reason: str | None = None


class TranslationOutcomeCache(Protocol):
    hits: int
    stores: int
    batch_commits: int

    def get(self, text: str) -> TranslationOutcome | None: ...

    def put(self, text: str, outcome: TranslationOutcome) -> None: ...


class BoundedTranslationOutcomeCache:
    """Bounded exact LRU that retains translation integrity metadata."""

    def __init__(self, capacity: int) -> None:
        if capacity < 0:
            raise ValueError("Translation outcome cache capacity cannot be negative")
        self.capacity = capacity
        self._values: OrderedDict[str, TranslationOutcome] = OrderedDict()
        self.hits = 0
        self.stores = 0
        self.batch_commits = 0

    def get(self, text: str) -> TranslationOutcome | None:
        outcome = self._values.get(text)
        if outcome is None:
            return None
        self._values.move_to_end(text)
        self.hits += 1
        return outcome

    def put(self, text: str, outcome: TranslationOutcome) -> None:
        if self.capacity == 0:
            return
        self._values[text] = outcome
        self._values.move_to_end(text)
        self.stores += 1
        while len(self._values) > self.capacity:
            self._values.popitem(last=False)


class TranslationIntegrityMonitor:
    """Count distinct model results and stop a systemically unsafe translation run."""

    def __init__(self) -> None:
        self.model_inputs = 0
        self.fallbacks = 0
        self.consecutive_fallbacks = 0

    def record(self, *, fallback: bool) -> None:
        self.model_inputs += 1
        if fallback:
            self.fallbacks += 1
            self.consecutive_fallbacks += 1
        else:
            self.consecutive_fallbacks = 0
        if self.consecutive_fallbacks >= TRANSLATION_FALLBACK_CONSECUTIVE_LIMIT:
            raise EnrichmentError(
                "Translation integrity circuit breaker opened after "
                f"{self.consecutive_fallbacks} consecutive preservation fallbacks"
            )
        if (
            self.model_inputs >= TRANSLATION_FALLBACK_MINIMUM_RESULTS
            and self.fallbacks * TRANSLATION_FALLBACK_RATE_DENOMINATOR
            > self.model_inputs * TRANSLATION_FALLBACK_RATE_NUMERATOR
        ):
            raise EnrichmentError(
                "Translation integrity circuit breaker opened: "
                f"{self.fallbacks:,} of {self.model_inputs:,} distinct model results "
                "required preservation fallback"
            )


@dataclass
class TranslationWorkStats:
    """Aggregate translation work counters; deliberately retains no record identifiers."""

    candidate_occurrences: int = 0
    text_decisions: int = 0
    protected_only_bypass_texts: int = 0
    run_cache_hits: int = 0
    translation_cache_hits: int = 0
    translator_requests: int = 0
    translator_input_texts: int = 0
    model_results: int = 0
    model_fallbacks: int = 0
    translated_child_occurrences: int = 0
    preservation_fallback_child_occurrences: int = 0

    def payload(self) -> dict[str, int]:
        counters = {
            "candidateOccurrences": self.candidate_occurrences,
            "textDecisions": self.text_decisions,
            "protectedOnlyBypassTexts": self.protected_only_bypass_texts,
            "runCacheHits": self.run_cache_hits,
            "translationCacheHits": self.translation_cache_hits,
            "translatorRequests": self.translator_requests,
            "translatorInputTexts": self.translator_input_texts,
            "modelResults": self.model_results,
            "modelFallbacks": self.model_fallbacks,
            "translatedChildOccurrences": self.translated_child_occurrences,
            "preservationFallbackChildOccurrences": (self.preservation_fallback_child_occurrences),
        }
        if any(
            not isinstance(value, int) or isinstance(value, bool) or value < 0
            for value in counters.values()
        ):
            raise EnrichmentError("Translation work statistics contain an invalid counter")
        if self.text_decisions != (
            self.run_cache_hits
            + self.protected_only_bypass_texts
            + self.translation_cache_hits
            + self.translator_input_texts
        ):
            raise EnrichmentError(
                "Translation work statistics failed decision-cardinality validation"
            )
        if self.model_results != self.translator_input_texts:
            raise EnrichmentError("Translation work statistics failed model-result validation")
        if self.model_fallbacks > self.model_results:
            raise EnrichmentError("Translation work statistics contain too many fallbacks")
        if self.translator_requests > self.translator_input_texts:
            raise EnrichmentError(
                "Translation work statistics contain too many translator requests"
            )
        if self.candidate_occurrences < self.text_decisions:
            raise EnrichmentError("Translation work statistics contain too many text decisions")
        if self.translated_child_occurrences > self.candidate_occurrences:
            raise EnrichmentError(
                "Translation work statistics contain too many translated children"
            )
        if self.preservation_fallback_child_occurrences > self.translated_child_occurrences:
            raise EnrichmentError("Translation work statistics contain too many fallback children")
        return {"schemaVersion": 1, **counters}


class RunLocalTranslationCache:
    """Run-scoped SQLite exact cache with a bounded in-memory hot set."""

    def __init__(self, directory: Path, identity: str, memory_capacity: int) -> None:
        if memory_capacity < 0:
            raise ValueError("Run-local cache memory capacity cannot be negative")
        directory.mkdir(parents=True, exist_ok=True)
        handle, raw_path = tempfile.mkstemp(
            prefix=".bstrings-translation-cache-",
            suffix=".sqlite3",
            dir=directory,
        )
        os.close(handle)
        self.path = Path(raw_path)
        self.identity = identity
        self.memory_capacity = memory_capacity
        self._memory: OrderedDict[str, TranslationOutcome] = OrderedDict()
        self._connection: sqlite3.Connection | None = None
        atexit.register(self.close)
        try:
            details = os.lstat(self.path)
        except OSError as exc:
            self.close(strict=True)
            raise EnrichmentError(f"Could not inspect run-local translation cache: {exc}") from exc
        if not stat.S_ISREG(details.st_mode) or self.path.is_symlink():
            self.close(strict=True)
            raise EnrichmentError("Run-local translation cache path is not a physical file")
        self.hot_hits = 0
        self.disk_hits = 0
        self.misses = 0
        self.stores = 0
        self.batch_commits = 0
        self._pending_writes = 0
        try:
            connection = sqlite3.connect(self.path)
            self._connection = connection
            connection.execute("PRAGMA journal_mode=MEMORY")
            connection.execute("PRAGMA synchronous=OFF")
            connection.execute("PRAGMA trusted_schema=OFF")
            connection.execute(
                """
                CREATE TABLE translations (
                    digest BLOB NOT NULL,
                    identity TEXT NOT NULL,
                    source_text TEXT NOT NULL,
                    translated_text TEXT NOT NULL,
                    integrity TEXT NOT NULL,
                    reason TEXT,
                    ambiguous_identifier_count INTEGER NOT NULL,
                    outcome_digest BLOB NOT NULL,
                    PRIMARY KEY (digest, identity, source_text)
                ) WITHOUT ROWID
                """
            )
            connection.commit()
            connection.execute("BEGIN IMMEDIATE")
        except (OSError, sqlite3.Error) as exc:
            self.close(strict=True)
            raise EnrichmentError(f"Could not create run-local translation cache: {exc}") from exc

    @staticmethod
    def _digest(text: str) -> bytes:
        return hashlib.sha256(text.encode("utf-8")).digest()

    def _outcome_digest(self, text: str, outcome: TranslationOutcome) -> bytes:
        serialized = json.dumps(
            (
                TRANSLATION_CACHE_SCHEMA_VERSION,
                self.identity,
                text,
                outcome.text,
                outcome.integrity,
                outcome.reason,
                outcome.ambiguous_identifier_count,
            ),
            ensure_ascii=False,
            separators=(",", ":"),
        ).encode("utf-8")
        return hashlib.sha256(serialized).digest()

    @property
    def hits(self) -> int:
        return self.hot_hits + self.disk_hits

    def _remember(self, text: str, outcome: TranslationOutcome) -> None:
        if self.memory_capacity == 0:
            return
        self._memory[text] = outcome
        self._memory.move_to_end(text)
        while len(self._memory) > self.memory_capacity:
            self._memory.popitem(last=False)

    def get(self, text: str) -> TranslationOutcome | None:
        cached = self._memory.get(text)
        if cached is not None:
            self._validate_outcome(text, cached)
            self._memory.move_to_end(text)
            self.hot_hits += 1
            return cached
        connection = self._connection
        if connection is None:
            raise EnrichmentError("Run-local translation cache is closed")
        try:
            rows = connection.execute(
                """
                SELECT source_text, translated_text, integrity, reason,
                       ambiguous_identifier_count, outcome_digest
                FROM translations
                WHERE digest = ? AND identity = ?
                """,
                (self._digest(text), self.identity),
            )
        except sqlite3.Error as exc:
            raise EnrichmentError(f"Could not read run-local translation cache: {exc}") from exc
        for (
            source_text,
            translated_text,
            integrity,
            reason,
            ambiguous_count,
            outcome_digest,
        ) in rows:
            if source_text != text:
                continue
            outcome = TranslationOutcome(
                str(translated_text),
                str(integrity),
                None if reason is None else str(reason),
                int(ambiguous_count),
            )
            if not isinstance(outcome_digest, bytes) or outcome_digest != self._outcome_digest(
                text, outcome
            ):
                raise EnrichmentError(
                    "Run-local translation cache failed outcome-integrity validation"
                )
            self._validate_outcome(text, outcome)
            self.disk_hits += 1
            self._remember(text, outcome)
            return outcome
        self.misses += 1
        return None

    def put(self, text: str, outcome: TranslationOutcome) -> None:
        self._validate_outcome(text, outcome)
        connection = self._connection
        if connection is None:
            raise EnrichmentError("Run-local translation cache is closed")
        try:
            connection.execute(
                """
                INSERT INTO translations (
                    digest, identity, source_text, translated_text, integrity, reason,
                    ambiguous_identifier_count, outcome_digest
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    self._digest(text),
                    self.identity,
                    text,
                    outcome.text,
                    outcome.integrity,
                    outcome.reason,
                    outcome.ambiguous_identifier_count,
                    self._outcome_digest(text, outcome),
                ),
            )
        except sqlite3.IntegrityError as exc:
            existing = self.get(text)
            if existing != outcome:
                raise EnrichmentError(
                    "Run-local translation cache contains conflicting exact text"
                ) from exc
        except sqlite3.Error as exc:
            raise EnrichmentError(f"Could not write run-local translation cache: {exc}") from exc
        else:
            self.stores += 1
            self._pending_writes += 1
            if self._pending_writes >= TRANSLATION_CACHE_COMMIT_BATCH_SIZE:
                try:
                    connection.commit()
                    connection.execute("BEGIN IMMEDIATE")
                except sqlite3.Error as exc:
                    raise EnrichmentError(
                        f"Could not commit run-local translation cache: {exc}"
                    ) from exc
                self.batch_commits += 1
                self._pending_writes = 0
        self._remember(text, outcome)

    @staticmethod
    def _validate_outcome(text: str, outcome: TranslationOutcome) -> None:
        if not outcome.text:
            raise EnrichmentError("Run-local translation cache cannot store empty text")
        if outcome.integrity not in {
            "verified",
            "source-retained-ambiguous",
            "preservation-fallback",
        }:
            raise EnrichmentError("Run-local translation cache has invalid integrity metadata")
        if outcome.ambiguous_identifier_count < 0:
            raise EnrichmentError("Run-local translation cache has a negative advisory count")
        if outcome.integrity == "preservation-fallback":
            if (
                outcome.text != text
                or not outcome.reason
                or outcome.ambiguous_identifier_count != 0
            ):
                raise EnrichmentError("Run-local translation cache has an invalid fallback")
        elif outcome.reason is not None:
            raise EnrichmentError("Run-local translation cache has an unexpected fallback reason")
        else:
            advisory_count = len(advisory_identifier_spans(text))
            expected_integrity = "source-retained-ambiguous" if advisory_count else "verified"
            if (
                outcome.integrity != expected_integrity
                or outcome.ambiguous_identifier_count != advisory_count
            ):
                raise EnrichmentError(
                    "Run-local translation cache has inconsistent advisory integrity metadata"
                )
        try:
            validate_identifier_retention(text, outcome.text)
        except EnrichmentError as exc:
            raise EnrichmentError(
                "Run-local translation cache failed hard-identifier validation"
            ) from exc

    def close(self, *, commit: bool = False, strict: bool = False) -> None:
        failures: list[str] = []
        connection = getattr(self, "_connection", None)
        self._connection = None
        if connection is not None:
            try:
                if commit:
                    connection.commit()
                else:
                    connection.rollback()
            except sqlite3.Error as exc:
                failures.append(f"transaction finalization failed: {exc}")
            finally:
                try:
                    connection.close()
                except sqlite3.Error as exc:
                    failures.append(f"database close failed: {exc}")
        path = getattr(self, "path", None)
        if path is not None:
            for candidate in (
                path,
                Path(str(path) + "-journal"),
                Path(str(path) + "-wal"),
                Path(str(path) + "-shm"),
            ):
                try:
                    candidate.unlink()
                except FileNotFoundError:
                    pass
                except OSError as exc:
                    failures.append(f"could not remove '{candidate.name}': {exc}")
        memory = getattr(self, "_memory", None)
        if memory is not None:
            memory.clear()
        if not failures:
            with suppress(Exception):
                atexit.unregister(self.close)
        if strict and failures:
            raise EnrichmentError(
                "Run-local translation cache cleanup failed: " + "; ".join(failures)
            )


def finalize_run_translation_cache(cache: RunLocalTranslationCache, output_path: Path) -> None:
    """Delete sensitive run-local cache before the staged output replaces prior evidence."""
    cache.close(commit=True, strict=True)


def map_ordered_parallel(function: Callable[[T], U], values: Sequence[T], workers: int) -> list[U]:
    """Run independent calls concurrently while retaining input order and fatal errors."""
    if workers <= 1 or len(values) <= 1:
        return [function(value) for value in values]
    with ThreadPoolExecutor(max_workers=min(workers, len(values))) as executor:
        return list(executor.map(function, values))


def _is_identifier_component(character: str) -> bool:
    return unicodedata.category(character)[:1] in {"L", "M", "N"}


def _has_complete_identifier_boundaries(text: str, start: int, end: int) -> bool:
    return not (
        (start > 0 and _is_identifier_component(text[start - 1]))
        or (end < len(text) and _is_identifier_component(text[end]))
    )


def _validated_ipv6_identifier_spans(text: str) -> tuple[tuple[int, int, str], ...]:
    """Find exact IPv6 evidence without treating arbitrary colon prose as an address."""
    spans: list[tuple[int, int, str]] = []
    suppressed_inner_ranges: list[tuple[int, int]] = []
    for match in _BRACKETED_IPV6_PORT_PATTERN.finditer(text):
        start, end = match.span()
        try:
            ipaddress.IPv6Address(match.group("address"))
        except ipaddress.AddressValueError:
            continue
        port = int(match.group("port"))
        if port > 65535:
            continue
        suppressed_inner_ranges.append((start, end))
        if not _has_complete_identifier_boundaries(text, start, end):
            continue
        spans.append((start, end, match.group(0)))

    for match in _BARE_IPV6_CANDIDATE_PATTERN.finditer(text):
        value = match.group(0).rstrip(".")
        if not value or ":" not in value:
            continue
        start = match.start()
        end = start + len(value)
        if any(
            bracket_start <= start and end <= bracket_end
            for bracket_start, bracket_end in suppressed_inner_ranges
        ):
            continue
        if not _has_complete_identifier_boundaries(text, start, end):
            continue
        try:
            ipaddress.IPv6Address(value)
        except ipaddress.AddressValueError:
            continue
        spans.append((start, end, value))
    return tuple(spans)


def _hard_path_identifier_spans(text: str) -> tuple[tuple[int, int, str], ...]:
    """Find complete conservative absolute path forms promised by the evidence contract."""
    spans: list[tuple[int, int, str]] = []
    for pattern in HARD_PATH_IDENTIFIER_PATTERNS:
        for match in pattern.finditer(text):
            value = _trim_identifier_trailing_punctuation(match.group(0))
            if not value:
                continue
            start = match.start()
            end = start + len(value)
            if start > 0 and (
                _is_identifier_component(text[start - 1]) or text[start - 1] in "._-/\\:"
            ):
                continue
            if end < len(text) and (_is_identifier_component(text[end]) or text[end] in "/\\"):
                continue
            spans.append((start, end, value))
    return tuple(spans)


def _separator_identifier_spans(
    text: str,
) -> tuple[tuple[int, int, str, bool], ...]:
    """Return complete Unicode separator tokens and whether each token is hard."""
    spans: list[tuple[int, int, str, bool]] = []
    index = 0
    while index < len(text):
        if not _is_identifier_component(text[index]):
            index += 1
            continue
        start = index
        while index < len(text) and _is_identifier_component(text[index]):
            index += 1
        separators: list[str] = []
        while (
            index < len(text)
            and text[index] in _IDENTIFIER_SEPARATORS
            and index + 1 < len(text)
            and _is_identifier_component(text[index + 1])
        ):
            separators.append(text[index])
            index += 1
            while index < len(text) and _is_identifier_component(text[index]):
                index += 1
        if not separators:
            continue
        if (start > 0 and text[start - 1] in _IDENTIFIER_SEPARATORS) or (
            index < len(text) and text[index] in _IDENTIFIER_SEPARATORS
        ):
            continue
        value = text[start:index]
        contains_underscore = "_" in separators
        uppercase_ascii = (
            value.isascii()
            and any("A" <= character <= "Z" for character in value)
            and not any("a" <= character <= "z" for character in value)
        )
        classification = contains_underscore or uppercase_ascii
        spans.append((start, index, value, classification))
    return tuple(spans)


def _merge_typed_identifier_spans(
    text: str, matches: Iterable[tuple[int, int, str]]
) -> tuple[tuple[int, int, str], ...]:
    """Union overlapping class-specific spans so no composite evidence is dropped."""
    ordered = sorted(matches, key=lambda item: (item[0], item[1]))
    merged_ranges: list[tuple[int, int]] = []
    for start, end, _ in ordered:
        if merged_ranges and start < merged_ranges[-1][1]:
            previous_start, previous_end = merged_ranges[-1]
            merged_ranges[-1] = (previous_start, max(previous_end, end))
        else:
            merged_ranges.append((start, end))
    return tuple((start, end, text[start:end]) for start, end in merged_ranges)


def _trim_identifier_trailing_punctuation(value: str) -> str:
    """Trim sentence punctuation while retaining balanced identifier delimiters."""
    while value:
        final = value[-1]
        if final in _UNCONDITIONAL_TRAILING_IDENTIFIER_PUNCTUATION:
            value = value[:-1]
            continue
        opener = _BALANCED_IDENTIFIER_CLOSERS.get(final)
        if opener is not None and value.count(final) > value.count(opener):
            value = value[:-1]
            continue
        break
    return value


def protected_identifier_spans(text: str) -> tuple[tuple[int, int, str], ...]:
    """Return non-overlapping hard structured-token spans that must be preserved."""
    typed_matches: list[tuple[int, int, str]] = []
    for pattern in HARD_IDENTIFIER_PATTERNS:
        for match in pattern.finditer(text):
            value = _trim_identifier_trailing_punctuation(match.group(0))
            if not value:
                continue
            start = match.start()
            end = start + len(value)
            if (
                start > 0
                and _is_identifier_component(value[0])
                and _is_identifier_component(text[start - 1])
            ):
                continue
            if (
                end < len(text)
                and _is_identifier_component(value[-1])
                and _is_identifier_component(text[end])
            ):
                continue
            typed_matches.append((start, end, value))
    for pattern in FIXED_ASCII_HARD_IDENTIFIER_PATTERNS:
        typed_matches.extend(
            (match.start(), match.end(), match.group(0)) for match in pattern.finditer(text)
        )
    typed_matches.extend(_validated_ipv6_identifier_spans(text))
    typed_matches.extend(_hard_path_identifier_spans(text))
    hard_generic_spans = tuple(
        (start, end, value) for start, end, value, hard in _separator_identifier_spans(text) if hard
    )
    return _merge_typed_identifier_spans(text, (*typed_matches, *hard_generic_spans))


def advisory_identifier_spans(text: str) -> tuple[tuple[int, int, str], ...]:
    """Return complete alphabetic-hyphen tokens that are advisory, not hard invariants."""
    hard_ranges = tuple((start, end) for start, end, _ in protected_identifier_spans(text))
    return tuple(
        (start, end, value)
        for start, end, value, hard in _separator_identifier_spans(text)
        if hard is False
        and any(character.isalpha() for character in value)
        and not any(start < hard_end and hard_start < end for hard_start, hard_end in hard_ranges)
    )


def protected_identifiers(text: str) -> tuple[str, ...]:
    """Return conservative, strongly structured tokens that translation must preserve."""
    return tuple(dict.fromkeys(value for _, _, value in protected_identifier_spans(text)))


def contains_only_protected_identifiers(text: str) -> bool:
    """Identify machine tokens that have no natural-language letters outside protected spans."""
    spans = protected_identifier_spans(text)
    if not spans:
        return False
    protected_positions = [False] * len(text)
    for start, end, _ in spans:
        protected_positions[start:end] = [True] * (end - start)
    return not any(
        character.isalpha() and not protected_positions[index]
        for index, character in enumerate(text)
    )


def validate_identifier_retention(source: str, translated: str) -> None:
    source_counts = Counter(value for _, _, value in protected_identifier_spans(source))
    translated_counts = Counter(value for _, _, value in protected_identifier_spans(translated))
    if source_counts != translated_counts:
        changed = sorted((source_counts - translated_counts) + (translated_counts - source_counts))
        preview = ", ".join(repr(value) for value in changed[:3])
        raise EnrichmentError(
            "Translation changed, removed, or duplicated protected evidence identifiers: " + preview
        )


def translation_cache_identity(
    translator: Translator,
    target_language: str,
    *,
    batch_size: int,
    max_input_tokens: int,
    max_new_tokens: int,
    strict_determinism: bool,
) -> str:
    """Build the full output-affecting identity for one run-local exact cache."""
    return canonical_json(
        {
            "schemaVersion": TRANSLATION_CACHE_SCHEMA_VERSION,
            "targetLanguage": target_language,
            "engine": translator.engine,
            "engineVersion": translator.engine_version,
            "model": translator.model_id,
            "revision": translator.revision,
            "modelSha256": translator.model_sha256,
            "promptVersion": TRANSLATION_PROMPT_VERSION,
            "preservationPolicyVersion": TRANSLATION_PRESERVATION_POLICY_VERSION,
            "batchSize": batch_size,
            "maxInputTokens": max_input_tokens,
            "maxNewTokens": max_new_tokens,
            "strictDeterminism": strict_determinism,
            "execution": dict(getattr(translator, "execution_metadata", {}) or {}),
        }
    )


def resolve_translation_parallelism(
    requested: int,
    *,
    strict_determinism: bool,
    has_cuda: bool,
    model_size_bytes: int,
    batch_size: int,
    logical_processors: int | None = None,
) -> int:
    """Choose a conservative number of shared-model inference slots."""
    if strict_determinism:
        return 1
    if requested > 0:
        return min(requested, batch_size)
    processors = logical_processors or os.cpu_count() or 1
    if has_cuda:
        # Two shared slots retained quality on the pinned forensic/WMT gate while
        # four slots introduced more schedule-dependent output variation.
        automatic = 1 if model_size_bytes > 8 * 1024**3 else 2
    else:
        automatic = 2 if processors >= 12 else 1
    return max(1, min(automatic, batch_size))


def canonical_json(value: dict[str, Any]) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def with_record_id(record: dict[str, Any]) -> dict[str, Any]:
    material = canonical_json(record).encode("utf-8")
    return {**record, "recordId": f"sha256:{hashlib.sha256(material).hexdigest()}"}


def hex_value(value: Any) -> str:
    if isinstance(value, bool):
        raise EnrichmentError("Boolean values are not valid evidence locations")
    if isinstance(value, int):
        return f"0x{value:X}"
    if isinstance(value, str):
        return value if value.lower().startswith("0x") else value
    raise EnrichmentError(f"Unsupported evidence location value: {value!r}")


def executable_path(command: str) -> str:
    candidate = Path(command)
    if candidate.parent != Path(".") or candidate.is_absolute():
        if candidate.is_file():
            return str(candidate.resolve())
        raise EnrichmentError(f"Executable was not found: {command}")
    resolved = shutil.which(command)
    if resolved is None:
        raise EnrichmentError(f"Executable '{command}' is not on PATH")
    return resolved


def run_checked(command: Sequence[str], timeout_seconds: int) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run(
            list(command),
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_seconds,
        )
    except subprocess.TimeoutExpired as exc:
        raise EnrichmentError(f"Command timed out after {timeout_seconds}s: {command[0]}") from exc
    except OSError as exc:
        raise EnrichmentError(f"Could not start '{command[0]}': {exc}") from exc
    if result.returncode != 0:
        detail = result.stderr.strip() or result.stdout.strip() or "no diagnostic output"
        raise EnrichmentError(
            f"Command failed with exit code {result.returncode}: {command[0]}: {detail}"
        )
    return result


def tool_version(command: str, timeout_seconds: int = 30) -> str:
    result = run_checked([command, "--version"], timeout_seconds)
    return (result.stdout.strip() or result.stderr.strip()).splitlines()[0]


def classify_file(magika: str, path: Path, timeout_seconds: int) -> Classification:
    result = run_checked([magika, "--json", str(path)], timeout_seconds)
    try:
        payload = json.loads(result.stdout)
        item = payload[0]
        result_object = item["result"]
        if result_object["status"] != "ok":
            raise EnrichmentError(f"Magika could not classify '{path}': {result_object}")
        value = result_object["value"]
        output = value["output"]
        return Classification(
            label=str(output["label"]),
            score=float(value["score"]),
            is_text=bool(output["is_text"]),
            mime_type=str(output["mime_type"]),
            group=str(output["group"]),
        )
    except (IndexError, KeyError, TypeError, ValueError, json.JSONDecodeError) as exc:
        raise EnrichmentError(f"Unexpected Magika JSON for '{path}': {exc}") from exc


def _classification_from_magika(value: Any, score: Any, description: str) -> Classification:
    if not isinstance(value, dict):
        raise EnrichmentError(f"{description} is not an object")
    label = value.get("label")
    is_text = value.get("is_text")
    mime_type = value.get("mime_type")
    group = value.get("group")
    if (
        not isinstance(label, str)
        or not label
        or type(is_text) is not bool
        or not isinstance(mime_type, str)
        or not mime_type
        or not isinstance(group, str)
        or not group
        or isinstance(score, bool)
        or not isinstance(score, (int, float))
        or not math.isfinite(float(score))
        or not 0 <= float(score) <= 1
    ):
        raise EnrichmentError(f"{description} contains invalid classification fields")
    return Classification(label, float(score), is_text, mime_type, group)


def _parse_magika_jsonl_item(value: Any, expected_path: Path) -> MagikaClassification:
    if not isinstance(value, dict) or set(value) != {"path", "result"}:
        raise EnrichmentError("Magika JSONL row has unsupported fields")
    reported_path = value.get("path")
    result = value.get("result")
    if not isinstance(reported_path, str) or not _path_literals_equal(
        reported_path, str(expected_path)
    ):
        raise EnrichmentError("Magika JSONL path does not match the requested inventory order")
    if not isinstance(result, dict) or not isinstance(result.get("status"), str):
        raise EnrichmentError("Magika JSONL row has an invalid result")
    if result["status"] != "ok":
        unknown = Classification("unknown", 0.0, False, "application/octet-stream", "unknown")
        return MagikaClassification("error", unknown, unknown, str(result["status"]))
    if set(result) != {"status", "value"} or not isinstance(result["value"], dict):
        raise EnrichmentError("Successful Magika JSONL result has unsupported fields")
    result_value = result["value"]
    if set(result_value) != {"dl", "output", "score"}:
        raise EnrichmentError("Successful Magika JSONL value has unsupported fields")
    score = result_value["score"]
    output = _classification_from_magika(result_value["output"], score, "Magika output")
    raw = _classification_from_magika(result_value["dl"], score, "Magika raw prediction")
    return MagikaClassification("ok", output, raw)


def _run_magika_batch(
    magika: str,
    inputs: Sequence[RoutingInput],
    timeout_seconds: int,
) -> list[MagikaClassification]:
    command = [magika, "--jsonl", "--", *(str(item.path) for item in inputs)]
    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_seconds,
        )
    except subprocess.TimeoutExpired:
        return _magika_batch_errors(inputs, "batch-timeout")
    except OSError:
        return _magika_batch_errors(inputs, "batch-start-failed")

    lines = result.stdout.splitlines()
    if result.returncode != 0 and not lines:
        return _magika_batch_errors(inputs, f"batch-exit-{result.returncode}")
    if len(lines) != len(inputs):
        return _magika_batch_errors(inputs, "invalid-row-cardinality")
    classifications: list[MagikaClassification] = []
    for line, item in zip(lines, inputs, strict=True):
        if not line or len(line) > MAX_JSONL_LINE_CHARACTERS:
            return _magika_batch_errors(inputs, "invalid-row-size")
        try:
            value = json.loads(line)
            classifications.append(_parse_magika_jsonl_item(value, item.path))
        except (json.JSONDecodeError, EnrichmentError):
            return _magika_batch_errors(inputs, "invalid-row-content")
    if result.returncode != 0:
        return _magika_batch_errors(inputs, f"batch-exit-{result.returncode}")
    return classifications


def _iter_magika_batches(
    inputs: Iterable[RoutingInput], magika: str
) -> Iterable[list[RoutingInput]]:
    prefix = [magika, "--jsonl", "--"]
    prefix_units = len(subprocess.list2cmdline(prefix).encode("utf-16-le")) // 2
    batch: list[RoutingInput] = []
    command_units = prefix_units
    for item in inputs:
        quoted_path = subprocess.list2cmdline([str(item.path)])
        added_units = 1 + len(quoted_path.encode("utf-16-le")) // 2
        if batch and (
            len(batch) >= MAGIKA_BATCH_MAX_PATHS
            or command_units + added_units > MAGIKA_BATCH_MAX_COMMAND_CHARACTERS
        ):
            yield batch
            batch = [item]
            command_units = prefix_units + added_units
        else:
            batch.append(item)
            command_units += added_units
    if batch:
        yield batch


def _path_literals_equal(left: str, right: str) -> bool:
    return os.path.normcase(os.path.abspath(left)) == os.path.normcase(os.path.abspath(right))


def _read_bounded_signals(path: Path) -> tuple[set[str], set[str]]:
    signals: set[str] = set()
    conflicts: set[str] = set()
    try:
        with path.open("rb") as handle:
            header = handle.read(4_096)
            if header.startswith(b"%PDF-"):
                signals.add("magic:pdf")
            elif b"%PDF-" in header[:1_024]:
                signals.add("probe:embedded-pdf-header")
                conflicts.add("nonzero-pdf-header")
            if header.startswith(b"\x89PNG\r\n\x1a\n"):
                signals.add("magic:png")
            elif header.startswith(b"\xff\xd8\xff"):
                signals.add("magic:jpeg")
            elif header.startswith((b"GIF87a", b"GIF89a")):
                signals.add("magic:gif")
            elif header.startswith((b"II*\x00", b"MM\x00*")):
                signals.add("magic:tiff")
            elif header.startswith(b"BM"):
                signals.add("magic:bmp")
            elif header.startswith(b"RIFF") and header[8:12] == b"WEBP":
                signals.add("magic:webp")
            elif header.startswith(b"\x00\x00\x01\x00"):
                signals.add("magic:ico")
            if header.startswith(b"MZ") and len(header) >= 64:
                pe_offset = int.from_bytes(header[60:64], "little")
                if pe_offset + 4 <= len(header):
                    pe_signature = header[pe_offset : pe_offset + 4]
                else:
                    handle.seek(pe_offset)
                    pe_signature = handle.read(4)
                if pe_signature == b"PE\x00\x00":
                    signals.add("magic:pe")
                else:
                    conflicts.add("invalid-pe-signature")
    except OSError as exc:
        raise EnrichmentError("An inventory item could not be inspected for routing") from exc

    suffix = path.suffix.lower()
    if suffix == ".pdf":
        signals.add("extension:pdf")
        if not signals.intersection({"magic:pdf", "probe:embedded-pdf-header"}):
            conflicts.add("extension-content-disagreement:pdf")
    elif suffix in OCR_IMAGE_EXTENSIONS:
        signals.add(f"extension:{suffix[1:]}")
        if not any(signal.startswith("magic:") and signal != "magic:pe" for signal in signals):
            conflicts.add("extension-content-disagreement:image")
    if suffix in PE_EXTENSIONS:
        signals.add(f"extension:{suffix[1:]}")
        if "magic:pe" not in signals:
            conflicts.add("extension-content-disagreement:pe")
    return signals, conflicts


def _classification_object(value: Classification) -> dict[str, Any]:
    return {
        "label": value.label,
        "mimeType": value.mime_type,
        "group": value.group,
        "isText": value.is_text,
    }


def _make_routing_record(
    item: RoutingInput,
    classification: MagikaClassification,
    magika_version: str,
    ordinal: int,
    *,
    enable_floss: bool,
    enable_ocr: bool,
    force_floss: bool,
    disable_native: bool = False,
    magika_executable: str | None = None,
    magika_sha256: str | None = None,
    magika_runtime_path: str | None = None,
    magika_runtime_sha256: str | None = None,
) -> dict[str, Any]:
    signals, conflicts = _read_bounded_signals(item.path)
    labels = {classification.output.label, classification.raw_prediction.label}
    if classification.output.label == "pebin":
        signals.add("magika-output:pebin")
    if classification.raw_prediction.label == "pebin":
        signals.add("magika-raw:pebin")
    for label in labels.intersection(OCR_MAGIKA_LABELS):
        prefix = "magika-output" if label == classification.output.label else "magika-raw"
        signals.add(f"{prefix}:{label}")
    if (
        classification.status == "ok"
        and classification.output.label != classification.raw_prediction.label
    ):
        conflicts.add("magika-output-raw-disagreement")
    if force_floss:
        signals.add("user-force:floss")

    floss_eligible = force_floss or any(
        signal in {"magic:pe", "magika-output:pebin", "magika-raw:pebin"} for signal in signals
    )
    ocr_eligible = any(
        signal == "magic:pdf"
        or signal == "probe:embedded-pdf-header"
        or signal == "extension:pdf"
        or signal.startswith("magika-")
        and signal.rsplit(":", maxsplit=1)[-1] in OCR_MAGIKA_LABELS
        or signal.startswith("magic:")
        and signal not in {"magic:pe"}
        or signal.startswith("extension:")
        and f".{signal.removeprefix('extension:')}" in OCR_IMAGE_EXTENSIONS
        for signal in signals
    )
    eligible_routes = {"native"}
    scheduled_routes = set() if disable_native else {"native"}
    if floss_eligible:
        eligible_routes.add("floss")
        if enable_floss:
            scheduled_routes.add("floss")
    if ocr_eligible:
        eligible_routes.add("ocr")
        if enable_ocr:
            scheduled_routes.add("ocr")

    classifier = {
        "engine": "magika",
        "version": magika_version,
        "predictionMode": "default-thresholded-output-plus-raw-dl",
        "status": classification.status,
        "score": classification.output.score,
        "output": _classification_object(classification.output),
        "rawPrediction": _classification_object(classification.raw_prediction),
    }
    if magika_executable is not None:
        classifier["executable"] = magika_executable
    if magika_sha256 is not None:
        classifier["executableSha256"] = magika_sha256
    if magika_runtime_path is not None and magika_runtime_sha256 is not None:
        classifier["runtime"] = {
            "path": magika_runtime_path,
            "sha256": magika_runtime_sha256,
        }
    if classification.error_code is not None:
        classifier["errorCode"] = classification.error_code
    record = {
        "schemaVersion": 2 if disable_native else SCHEMA_VERSION,
        "recordType": "content-route",
        "policyVersion": ("content-routing-v2" if disable_native else "content-routing-v1"),
        "ordinal": ordinal,
        "sourceFile": str(item.path),
        "sourceSize": item.identity.length,
        "sourceSha256": item.identity.sha256,
        "classifier": classifier,
        "signals": sorted(signals),
        "eligibleRoutes": sorted(eligible_routes),
        "scheduledRoutes": sorted(scheduled_routes),
        "conflicts": sorted(conflicts),
    }
    record["decisionId"] = (
        f"sha256:{hashlib.sha256(canonical_json(record).encode('utf-8')).hexdigest()}"
    )
    return record


def run_floss(
    floss: str,
    path: Path,
    minimum_length: int,
    timeout_seconds: int,
    input_format: str = "auto",
) -> FlossJsonDocument:
    command = [floss, "-j", "-n", str(minimum_length)]
    if input_format != "auto":
        command.extend(["--format", input_format])
    command.extend(["--only", "static", "stack", "tight", "decoded", "--", str(path)])
    # Ownership transfers to FlossJsonDocument on success and its close() owns cleanup.
    stdout_file = tempfile.TemporaryFile(mode="w+b")  # noqa: SIM115
    try:
        with tempfile.TemporaryFile(mode="w+b") as stderr_file:
            result = subprocess.run(
                command,
                check=False,
                stdout=stdout_file,
                stderr=stderr_file,
                timeout=timeout_seconds,
            )
            if result.returncode != 0:
                detail = _file_tail(stderr_file) or _file_tail(stdout_file)
                raise EnrichmentError(
                    f"FLOSS failed with exit code {result.returncode} for '{path}': "
                    + (detail or "no diagnostic output")
                )
            stdout_file.seek(0)
            try:
                document = FlossJsonDocument(stdout_file)
            except (_JsonSyntaxError, OSError) as exc:
                raise EnrichmentError(f"FLOSS returned invalid JSON for '{path}': {exc}") from exc
            return document
    except subprocess.TimeoutExpired as exc:
        raise EnrichmentError(f"FLOSS timed out after {timeout_seconds}s for '{path}'") from exc
    except OSError as exc:
        raise EnrichmentError(f"Could not run FLOSS for '{path}': {exc}") from exc
    finally:
        if "document" not in locals():
            stdout_file.close()


def _file_tail(handle: Any, maximum_bytes: int = 64 * 1024) -> str:
    handle.flush()
    length = handle.seek(0, os.SEEK_END)
    handle.seek(max(0, length - maximum_bytes))
    return handle.read(maximum_bytes).decode("utf-8", errors="replace").strip()


class _JsonSyntaxError(ValueError):
    """A strict JSON syntax or safety error with a byte location."""


@dataclass(frozen=True)
class _JsonValueSpan:
    start: int
    end: int


@dataclass(frozen=True)
class _JsonArraySpan(_JsonValueSpan):
    count: int


class _BoundedJsonReader:
    """Strict, seekable JSON scanner whose memory is independent of document size."""

    _whitespace = frozenset(b" \t\r\n")
    _value_delimiters = frozenset(b" \t\r\n,]}")
    _hexadecimal = frozenset(b"0123456789abcdefABCDEF")

    def __init__(self, handle: Any, start: int = 0) -> None:
        handle.seek(start)
        self._handle = handle
        self._buffer = bytearray()
        self._cursor = 0
        self._buffer_offset = start
        self._eof = False
        self._capture: bytearray | None = None
        self._capture_limit = 0

    @property
    def position(self) -> int:
        return self._buffer_offset + self._cursor

    def _fill(self) -> bool:
        if self._cursor < len(self._buffer):
            return True
        self._buffer_offset += self._cursor
        self._buffer.clear()
        self._cursor = 0
        if self._eof:
            return False
        chunk = self._handle.read(JSON_READ_CHUNK_BYTES)
        if not chunk:
            self._eof = True
            return False
        self._buffer.extend(chunk)
        return True

    def peek(self) -> int | None:
        return self._buffer[self._cursor] if self._fill() else None

    def take(self) -> int:
        value = self.peek()
        if value is None:
            self.fail("Unexpected end of JSON")
        assert value is not None
        self._cursor += 1
        if self._capture is not None:
            self._capture.append(value)
            if len(self._capture) > self._capture_limit:
                self.fail(
                    f"FLOSS JSON item exceeds the {MAX_FLOSS_JSON_ITEM_BYTES}-byte safety limit"
                )
        return value

    def fail(self, message: str) -> None:
        raise _JsonSyntaxError(f"{message} at byte {self.position}")

    def expect(self, expected: int) -> None:
        actual = self.take()
        if actual != expected:
            self.fail(f"Expected {chr(expected)!r}, found {chr(actual)!r}")

    def skip_whitespace(self) -> None:
        while self.peek() in self._whitespace:
            self.take()

    def read_string(self) -> str:
        token = bytearray()
        self._scan_string(token)
        try:
            value = json.loads(token.decode("utf-8"))
        except (UnicodeError, json.JSONDecodeError) as exc:
            self.fail(f"Invalid JSON string: {exc}")
        if not isinstance(value, str):
            self.fail("JSON object key is not a string")
        return value

    def _scan_string(self, token: bytearray | None = None) -> None:
        start = self.position

        def consume() -> int:
            value = self.take()
            if token is not None:
                token.append(value)
            if self.position - start > MAX_FLOSS_JSON_ITEM_BYTES:
                self.fail(f"JSON string exceeds the {MAX_FLOSS_JSON_ITEM_BYTES}-byte safety limit")
            return value

        if consume() != ord('"'):
            self.fail("Expected a JSON string")
        while True:
            if not self._fill():
                self.fail("Unexpected end of JSON string")
            match = JSON_STRING_SPECIAL.search(self._buffer, self._cursor)
            run_end = len(self._buffer) if match is None else match.start()
            if run_end > self._cursor:
                if token is not None:
                    token.extend(self._buffer[self._cursor : run_end])
                if self._capture is not None:
                    self._capture.extend(self._buffer[self._cursor : run_end])
                    if len(self._capture) > self._capture_limit:
                        self.fail(
                            "FLOSS JSON item exceeds the "
                            f"{MAX_FLOSS_JSON_ITEM_BYTES}-byte safety limit"
                        )
                self._cursor = run_end
                if self.position - start > MAX_FLOSS_JSON_ITEM_BYTES:
                    self.fail(
                        f"JSON string exceeds the {MAX_FLOSS_JSON_ITEM_BYTES}-byte safety limit"
                    )
            if match is None:
                continue
            value = consume()
            if value == ord('"'):
                return
            if value < 0x20:
                self.fail("Unescaped control byte in JSON string")
            if value != ord("\\"):
                continue
            escaped = consume()
            if escaped in b'"\\/bfnrt':
                continue
            if escaped != ord("u"):
                self.fail("Invalid JSON string escape")
            for _ in range(4):
                if consume() not in self._hexadecimal:
                    self.fail("Invalid JSON Unicode escape")

    def skip_value(self, depth: int = 0) -> None:
        if depth > MAX_JSON_DEPTH:
            self.fail(f"JSON nesting exceeds the {MAX_JSON_DEPTH}-level safety limit")
        value = self.peek()
        if value is None:
            self.fail("Expected a JSON value")
        if value == ord('"'):
            self._scan_string()
        elif value == ord("{"):
            self._skip_object(depth + 1)
        elif value == ord("["):
            self._skip_array(depth + 1)
        elif value == ord("t"):
            self._skip_literal(b"true")
        elif value == ord("f"):
            self._skip_literal(b"false")
        elif value == ord("n"):
            self._skip_literal(b"null")
        elif value == ord("-") or ord("0") <= value <= ord("9"):
            self._skip_number()
        else:
            self.fail("Expected a JSON value")

    def _skip_object(self, depth: int) -> None:
        self.expect(ord("{"))
        self.skip_whitespace()
        if self.peek() == ord("}"):
            self.take()
            return
        while True:
            if self.peek() != ord('"'):
                self.fail("Expected a JSON object key")
            self._scan_string()
            self.skip_whitespace()
            self.expect(ord(":"))
            self.skip_whitespace()
            self.skip_value(depth)
            self.skip_whitespace()
            delimiter = self.take()
            if delimiter == ord("}"):
                return
            if delimiter != ord(","):
                self.fail("Expected ',' or '}' in JSON object")
            self.skip_whitespace()

    def _skip_array(self, depth: int) -> None:
        self.expect(ord("["))
        self.skip_whitespace()
        if self.peek() == ord("]"):
            self.take()
            return
        while True:
            self.skip_value(depth)
            self.skip_whitespace()
            delimiter = self.take()
            if delimiter == ord("]"):
                return
            if delimiter != ord(","):
                self.fail("Expected ',' or ']' in JSON array")
            self.skip_whitespace()

    def _skip_literal(self, literal: bytes) -> None:
        for expected in literal:
            if self.take() != expected:
                self.fail(f"Invalid JSON literal; expected {literal.decode('ascii')}")
        if self.peek() is not None and self.peek() not in self._value_delimiters:
            self.fail("Invalid character after JSON literal")

    def _skip_number(self) -> None:
        if self.peek() == ord("-"):
            self.take()
        value = self.peek()
        if value == ord("0"):
            self.take()
            if self.peek() is not None and ord("0") <= self.peek() <= ord("9"):
                self.fail("Leading zero in JSON number")
        elif value is not None and ord("1") <= value <= ord("9"):
            while self.peek() is not None and ord("0") <= self.peek() <= ord("9"):
                self.take()
        else:
            self.fail("Invalid JSON number")
        if self.peek() == ord("."):
            self.take()
            if self.peek() is None or not ord("0") <= self.peek() <= ord("9"):
                self.fail("JSON fraction has no digits")
            while self.peek() is not None and ord("0") <= self.peek() <= ord("9"):
                self.take()
        if self.peek() in {ord("e"), ord("E")}:
            self.take()
            if self.peek() in {ord("+"), ord("-")}:
                self.take()
            if self.peek() is None or not ord("0") <= self.peek() <= ord("9"):
                self.fail("JSON exponent has no digits")
            while self.peek() is not None and ord("0") <= self.peek() <= ord("9"):
                self.take()
        if self.peek() is not None and self.peek() not in self._value_delimiters:
            self.fail("Invalid character after JSON number")

    def capture_value(self, maximum_bytes: int) -> bytearray:
        if self._capture is not None:
            raise RuntimeError("Nested JSON capture is not supported")
        self._capture = bytearray()
        self._capture_limit = maximum_bytes
        try:
            self.skip_value()
            return self._capture
        finally:
            self._capture = None
            self._capture_limit = 0


def _validate_utf8(handle: Any) -> None:
    handle.seek(0)
    decoder = codecs.getincrementaldecoder("utf-8")(errors="strict")
    try:
        while chunk := handle.read(JSON_READ_CHUNK_BYTES):
            decoder.decode(chunk)
        decoder.decode(b"", final=True)
    except UnicodeDecodeError as exc:
        raise _JsonSyntaxError(f"FLOSS JSON is not UTF-8 at byte {exc.start}") from exc


def _index_json_array(reader: _BoundedJsonReader) -> _JsonArraySpan:
    start = reader.position
    reader.expect(ord("["))
    reader.skip_whitespace()
    count = 0
    if reader.peek() == ord("]"):
        reader.take()
        return _JsonArraySpan(start, reader.position, count)
    while True:
        item_start = reader.position
        reader.skip_value()
        if reader.position - item_start > MAX_FLOSS_JSON_ITEM_BYTES:
            reader.fail(
                f"FLOSS JSON item exceeds the {MAX_FLOSS_JSON_ITEM_BYTES}-byte safety limit"
            )
        count += 1
        reader.skip_whitespace()
        delimiter = reader.take()
        if delimiter == ord("]"):
            return _JsonArraySpan(start, reader.position, count)
        if delimiter != ord(","):
            reader.fail("Expected ',' or ']' in FLOSS string array")
        reader.skip_whitespace()


def _index_floss_strings(reader: _BoundedJsonReader) -> dict[str, _JsonArraySpan]:
    indexed: dict[str, _JsonArraySpan] = {}
    reader.expect(ord("{"))
    reader.skip_whitespace()
    if reader.peek() == ord("}"):
        reader.take()
    else:
        while True:
            key = reader.read_string()
            if key not in FLOSS_STRING_CATEGORY_SET:
                reader.fail(f"Unknown FLOSS v3.1.1 strings category {key!r}")
            if key in indexed:
                reader.fail(f"Duplicate FLOSS strings category {key!r}")
            reader.skip_whitespace()
            reader.expect(ord(":"))
            reader.skip_whitespace()
            if reader.peek() != ord("["):
                reader.fail(f"FLOSS strings.{key} must be an array")
            indexed[key] = _index_json_array(reader)
            reader.skip_whitespace()
            delimiter = reader.take()
            if delimiter == ord("}"):
                break
            if delimiter != ord(","):
                reader.fail("Expected ',' or '}' in FLOSS strings object")
            reader.skip_whitespace()
    missing = FLOSS_STRING_CATEGORY_SET.difference(indexed)
    if missing:
        reader.fail(
            "FLOSS v3.1.1 strings object is missing required categories: "
            + ", ".join(sorted(missing))
        )
    return indexed


def _index_floss_document(
    handle: Any,
) -> tuple[_JsonValueSpan | None, dict[str, _JsonArraySpan]]:
    _validate_utf8(handle)
    reader = _BoundedJsonReader(handle)
    reader.skip_whitespace()
    if reader.peek() != ord("{"):
        reader.fail("FLOSS JSON root must be an object")
    reader.take()
    reader.skip_whitespace()
    metadata_span: _JsonValueSpan | None = None
    strings_index: dict[str, _JsonArraySpan] | None = None
    seen_root_keys: set[str] = set()
    if reader.peek() != ord("}"):
        while True:
            key = reader.read_string()
            if key not in FLOSS_ROOT_KEYS:
                reader.fail(f"Unknown FLOSS v3.1.1 root key {key!r}")
            if key in seen_root_keys:
                reader.fail(f"Duplicate FLOSS root key {key!r}")
            seen_root_keys.add(key)
            reader.skip_whitespace()
            reader.expect(ord(":"))
            reader.skip_whitespace()
            value_start = reader.position
            if key == "strings" and reader.peek() == ord("{"):
                strings_index = _index_floss_strings(reader)
            else:
                reader.skip_value()
                if key == "strings":
                    strings_index = None
            value_end = reader.position
            if key == "metadata":
                if value_end - value_start > MAX_FLOSS_JSON_ITEM_BYTES:
                    reader.fail(
                        f"FLOSS metadata exceeds the {MAX_FLOSS_JSON_ITEM_BYTES}-byte safety limit"
                    )
                metadata_span = _JsonValueSpan(value_start, value_end)
            reader.skip_whitespace()
            delimiter = reader.take()
            if delimiter == ord("}"):
                break
            if delimiter != ord(","):
                reader.fail("Expected ',' or '}' in FLOSS JSON root")
            reader.skip_whitespace()
    else:
        reader.take()
    reader.skip_whitespace()
    if reader.peek() is not None:
        reader.fail("Trailing data after FLOSS JSON document")
    if strings_index is None:
        reader.fail("FLOSS JSON has no strings object")
    if metadata_span is None:
        reader.fail("FLOSS JSON has no metadata object")
    return metadata_span, strings_index


def _reject_duplicate_object_pairs(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    value: dict[str, Any] = {}
    for key, item in pairs:
        if key in value:
            raise _JsonSyntaxError(f"Duplicate JSON object key {key!r}")
        value[key] = item
    return value


def _decode_json_value(handle: Any, span: _JsonValueSpan) -> Any:
    reader = _BoundedJsonReader(handle, span.start)
    raw = reader.capture_value(MAX_FLOSS_JSON_ITEM_BYTES)
    if reader.position != span.end:
        reader.fail("Indexed FLOSS JSON value changed during replay")
    try:
        return json.loads(raw, object_pairs_hook=_reject_duplicate_object_pairs)
    except json.JSONDecodeError as exc:
        raise _JsonSyntaxError(f"Could not decode indexed FLOSS JSON value: {exc}") from exc


def _require_integer(item: dict[str, Any], field: str, location: str) -> int:
    value = item[field]
    if not isinstance(value, int) or isinstance(value, bool):
        raise EnrichmentError(f"{location}.{field} must be an integer")
    return value


def _require_u64(item: dict[str, Any], field: str, location: str) -> None:
    value = _require_integer(item, field, location)
    if not 0 <= value <= UINT64_MAX:
        raise EnrichmentError(f"{location}.{field} must be an unsigned 64-bit integer")


def _require_i64(item: dict[str, Any], field: str, location: str) -> None:
    value = _require_integer(item, field, location)
    if not INT64_MIN <= value <= INT64_MAX:
        raise EnrichmentError(f"{location}.{field} must be a signed 64-bit integer")


def _validate_floss_item(category: str, item: dict[str, Any], index: int) -> None:
    location = f"FLOSS strings.{category}[{index}]"
    expected_fields = (
        FLOSS_STATIC_ITEM_FIELDS
        if category in FLOSS_STATIC_CATEGORIES
        else FLOSS_STACK_ITEM_FIELDS
        if category in FLOSS_STACK_CATEGORIES
        else FLOSS_DECODED_ITEM_FIELDS
    )
    actual_fields = frozenset(item)
    missing = expected_fields.difference(actual_fields)
    unknown = actual_fields.difference(expected_fields)
    if missing:
        raise EnrichmentError(
            f"{location} is missing required fields: {', '.join(sorted(missing))}"
        )
    if unknown:
        raise EnrichmentError(f"{location} has unknown fields: {', '.join(sorted(unknown))}")

    text = item["string"]
    if not isinstance(text, str):
        raise EnrichmentError(f"{location}.string must be a string")
    encoding = item["encoding"]
    if encoding not in FLOSS_STRING_ENCODINGS:
        raise EnrichmentError(
            f"{location}.encoding must be one of: {', '.join(sorted(FLOSS_STRING_ENCODINGS))}"
        )

    if category in FLOSS_STATIC_CATEGORIES:
        _require_u64(item, "offset", location)
    elif category in FLOSS_STACK_CATEGORIES:
        for field in (
            "function",
            "program_counter",
            "stack_pointer",
            "original_stack_pointer",
        ):
            _require_u64(item, field, location)
        for field in ("offset", "frame_offset"):
            _require_i64(item, field, location)
    else:
        for field in ("address", "decoded_at", "decoding_routine"):
            _require_u64(item, field, location)
        address_type = item["address_type"]
        if address_type not in FLOSS_ADDRESS_TYPES:
            raise EnrichmentError(
                f"{location}.address_type must be one of: " + ", ".join(sorted(FLOSS_ADDRESS_TYPES))
            )


class FlossJsonDocument:
    """Disk-backed, validated FLOSS JSON with one-record-at-a-time replay."""

    def __init__(self, handle: Any) -> None:
        self._handle = handle
        self._closed = False
        try:
            metadata_span, self._strings = _index_floss_document(handle)
            assert metadata_span is not None
            metadata = _decode_json_value(handle, metadata_span)
            if not isinstance(metadata, dict):
                raise _JsonSyntaxError("FLOSS metadata must be an object")
            missing_metadata = {"language", "imagebase"}.difference(metadata)
            if missing_metadata:
                raise _JsonSyntaxError(
                    "FLOSS metadata is missing required fields: "
                    + ", ".join(sorted(missing_metadata))
                )
            language = metadata["language"]
            imagebase = metadata["imagebase"]
            if not isinstance(language, str):
                raise _JsonSyntaxError("FLOSS metadata.language must be a string")
            if (
                not isinstance(imagebase, int)
                or isinstance(imagebase, bool)
                or not 0 <= imagebase <= UINT64_MAX
            ):
                raise _JsonSyntaxError(
                    "FLOSS metadata.imagebase must be an unsigned 64-bit integer"
                )
            self.metadata = {"language": language, "imagebase": imagebase}
        except BaseException:
            self.close()
            raise

    @property
    def closed(self) -> bool:
        return self._closed

    def iter_string_category(self, category: str) -> Iterable[dict[str, Any]]:
        if self._closed:
            raise EnrichmentError("FLOSS JSON document is closed")
        span = self._strings.get(category)
        if span is None:
            return
        reader = _BoundedJsonReader(self._handle, span.start)
        reader.expect(ord("["))
        reader.skip_whitespace()
        emitted = 0
        if reader.peek() == ord("]"):
            reader.take()
        else:
            while True:
                raw = reader.capture_value(MAX_FLOSS_JSON_ITEM_BYTES)
                try:
                    item = json.loads(
                        raw,
                        object_pairs_hook=_reject_duplicate_object_pairs,
                    )
                except (UnicodeError, json.JSONDecodeError, _JsonSyntaxError) as exc:
                    raise EnrichmentError(
                        f"Could not decode FLOSS strings.{category}[{emitted}]: {exc}"
                    ) from exc
                if not isinstance(item, dict):
                    raise EnrichmentError(f"FLOSS strings.{category}[{emitted}] must be an object")
                _validate_floss_item(category, item, emitted)
                emitted += 1
                yield item
                reader.skip_whitespace()
                delimiter = reader.take()
                if delimiter == ord("]"):
                    break
                if delimiter != ord(","):
                    reader.fail("Expected ',' or ']' in FLOSS string array replay")
                reader.skip_whitespace()
        if emitted != span.count or reader.position != span.end:
            raise EnrichmentError(
                f"FLOSS strings.{category} changed during replay "
                f"({emitted} of {span.count} indexed items)"
            )

    def close(self) -> None:
        if not self._closed:
            self._closed = True
            self._handle.close()

    def __enter__(self) -> FlossJsonDocument:
        return self

    def __exit__(self, *_: object) -> None:
        self.close()

    def __del__(self) -> None:
        with suppress(Exception):
            self.close()


def common_attributes(
    classification: Classification,
    floss_payload: dict[str, Any] | FlossJsonDocument,
) -> dict[str, Any]:
    metadata = (
        floss_payload.metadata
        if isinstance(floss_payload, FlossJsonDocument)
        else floss_payload.get("metadata") or {}
    )
    return {
        "magikaLabel": classification.label,
        "magikaScore": classification.score,
        "magikaMimeType": classification.mime_type,
        "magikaGroup": classification.group,
        "flossLanguage": metadata.get("language") or None,
        "flossImageBase": metadata.get("imagebase"),
    }


def make_string_record(
    *,
    text: str,
    source_file: str,
    extractor_version: str,
    kind: str,
    location_kind: str,
    location_value: Any,
    attributes: dict[str, Any],
) -> dict[str, Any]:
    record = {
        "schemaVersion": SCHEMA_VERSION,
        "recordType": "string",
        "text": text,
        "sourceFile": source_file,
        "location": {"kind": location_kind, "value": hex_value(location_value)},
        "origin": {"extractor": "floss", "version": extractor_version, "kind": kind},
        "attributes": {key: value for key, value in attributes.items() if value is not None},
    }
    return with_record_id(record)


def iter_normalized_floss(
    payload: dict[str, Any] | FlossJsonDocument,
    source_path: Path,
    classification: Classification,
    floss_version: str,
    include_static: bool = False,
    route_decision_id: str | None = None,
) -> Iterable[dict[str, Any]]:
    """Yield normalized FLOSS records without duplicating its monolithic JSON payload."""
    document = payload if isinstance(payload, FlossJsonDocument) else None
    strings = None if document is not None else payload["strings"]
    if document is None:
        metadata = payload.get("metadata")
        if not isinstance(metadata, dict):
            raise EnrichmentError("FLOSS metadata must be an object")
        if "language" not in metadata or not isinstance(metadata["language"], str):
            raise EnrichmentError("FLOSS metadata.language must be a string")
        if "imagebase" not in metadata:
            raise EnrichmentError("FLOSS metadata is missing required field: imagebase")
        _require_u64(metadata, "imagebase", "FLOSS metadata")
        if not isinstance(strings, dict):
            raise EnrichmentError("FLOSS strings must be an object")
        categories = frozenset(strings)
        missing = FLOSS_STRING_CATEGORY_SET.difference(categories)
        unknown = categories.difference(FLOSS_STRING_CATEGORY_SET)
        if missing:
            raise EnrichmentError(
                "FLOSS v3.1.1 strings object is missing required categories: "
                + ", ".join(sorted(missing))
            )
        if unknown:
            raise EnrichmentError(
                "FLOSS v3.1.1 strings object has unknown categories: " + ", ".join(sorted(unknown))
            )

    def category_items(category: str) -> Iterable[dict[str, Any]]:
        if document is not None:
            yield from document.iter_string_category(category)
            return
        assert isinstance(strings, dict)
        items = strings[category]
        if not isinstance(items, list):
            raise EnrichmentError(f"FLOSS strings.{category} must be an array")
        for index, item in enumerate(items):
            if not isinstance(item, dict):
                raise EnrichmentError(f"FLOSS strings.{category}[{index}] must be an object")
            _validate_floss_item(category, item, index)
            yield item

    try:
        source_file = str(source_path.resolve())
        shared = common_attributes(classification, payload)
        if route_decision_id is not None:
            shared["routeDecisionId"] = route_decision_id

        static_categories = ["language_strings"]
        if include_static:
            static_categories.insert(0, "static_strings")
            static_categories.append("language_strings_missed")
        for category in static_categories:
            kind = {
                "static_strings": "static",
                "language_strings": "language",
                "language_strings_missed": "language-missed",
            }[category]
            for item in category_items(category):
                yield make_string_record(
                    text=str(item["string"]),
                    source_file=source_file,
                    extractor_version=floss_version,
                    kind=kind,
                    location_kind="file_offset",
                    location_value=item["offset"],
                    attributes={**shared, "encoding": item.get("encoding")},
                )

        for category, kind in (("stack_strings", "stack"), ("tight_strings", "tight")):
            for item in category_items(category):
                yield make_string_record(
                    text=str(item["string"]),
                    source_file=source_file,
                    extractor_version=floss_version,
                    kind=kind,
                    location_kind="program_counter",
                    location_value=item["program_counter"],
                    attributes={
                        **shared,
                        "encoding": item.get("encoding"),
                        "function": item.get("function"),
                        "stackPointer": item.get("stack_pointer"),
                        "originalStackPointer": item.get("original_stack_pointer"),
                        "stackOffset": item.get("offset"),
                        "frameOffset": item.get("frame_offset"),
                    },
                )

        for item in category_items("decoded_strings"):
            yield make_string_record(
                text=str(item["string"]),
                source_file=source_file,
                extractor_version=floss_version,
                kind="decoded",
                location_kind="virtual_address",
                location_value=item["address"],
                attributes={
                    **shared,
                    "encoding": item.get("encoding"),
                    "addressType": item.get("address_type"),
                    "decodedAt": item.get("decoded_at"),
                    "decodingRoutine": item.get("decoding_routine"),
                },
            )
    finally:
        if document is not None:
            document.close()


def normalize_floss(
    payload: dict[str, Any] | FlossJsonDocument,
    source_path: Path,
    classification: Classification,
    floss_version: str,
    include_static: bool = False,
) -> list[dict[str, Any]]:
    """Materialize normalized FLOSS records for small callers and compatibility."""
    return list(
        iter_normalized_floss(
            payload,
            source_path,
            classification,
            floss_version,
            include_static,
        )
    )


def should_translate(text: str, minimum_characters: int, maximum_characters: int) -> bool:
    if not minimum_characters <= len(text) <= maximum_characters:
        return False
    return any(character.isalpha() for character in text)


def add_translations(
    records: Sequence[dict[str, Any]],
    translator: Translator,
    target_language: str,
    batch_size: int,
    minimum_characters: int,
    maximum_characters: int,
    cache: TranslationCache | None = None,
    run_cache: TranslationOutcomeCache | None = None,
    integrity_monitor: TranslationIntegrityMonitor | None = None,
    work_stats: TranslationWorkStats | None = None,
) -> list[dict[str, Any]]:
    candidates = [
        record
        for record in records
        if should_translate(str(record["text"]), minimum_characters, maximum_characters)
    ]
    if work_stats is not None:
        work_stats.candidate_occurrences += len(candidates)
    translations_by_text: dict[str, TranslationOutcome] = {}
    missing_texts: list[str] = []
    missing_text_set: set[str] = set()
    for record in candidates:
        text = str(record["text"])
        if text in translations_by_text or text in missing_text_set:
            continue
        if work_stats is not None:
            work_stats.text_decisions += 1
        if run_cache is not None:
            cached_outcome = run_cache.get(text)
            if cached_outcome is not None:
                if work_stats is not None:
                    work_stats.run_cache_hits += 1
                translations_by_text[text] = cached_outcome
                continue
        if contains_only_protected_identifiers(text):
            if work_stats is not None:
                work_stats.protected_only_bypass_texts += 1
            outcome = TranslationOutcome(text, "verified")
            translations_by_text[text] = outcome
            if run_cache is not None:
                run_cache.put(text, outcome)
            elif cache:
                cache.put(target_language, text, text)
            continue
        found, translated_text = cache.get(target_language, text) if cache else (False, "")
        if found:
            if work_stats is not None:
                work_stats.translation_cache_hits += 1
            try:
                validate_identifier_retention(text, translated_text)
            except EnrichmentError:
                translations_by_text[text] = TranslationOutcome(
                    text,
                    "preservation-fallback",
                    "hard-identifier-retention-mismatch",
                )
            else:
                ambiguous_count = len(advisory_identifier_spans(text))
                translations_by_text[text] = TranslationOutcome(
                    translated_text,
                    "source-retained-ambiguous" if ambiguous_count else "verified",
                    ambiguous_identifier_count=ambiguous_count,
                )
        else:
            missing_texts.append(text)
            missing_text_set.add(text)

    # Group similar lengths to reduce padding for batched encoder-decoder runtimes.
    missing_texts.sort(key=lambda value: (len(value), value))
    for start in range(0, len(missing_texts), batch_size):
        batch = missing_texts[start : start + batch_size]
        if work_stats is not None:
            work_stats.translator_requests += 1
            work_stats.translator_input_texts += len(batch)
        translate_attempts = getattr(translator, "translate_attempts", None)
        if callable(translate_attempts):
            attempts = translate_attempts(batch, target_language)
        else:
            attempts = [
                TranslationAttempt(text=value)
                for value in translator.translate(batch, target_language)
            ]
        if len(attempts) != len(batch):
            raise EnrichmentError(
                f"Translation engine returned {len(attempts)} rows for a batch of {len(batch)}"
            )
        if work_stats is not None:
            work_stats.model_results += len(attempts)
        for source_text, attempt in zip(batch, attempts, strict=True):
            if not isinstance(attempt, TranslationAttempt):
                raise EnrichmentError("Translation engine returned an invalid row attempt")
            if attempt.failure_reason is not None:
                if attempt.text is not None or not attempt.failure_reason:
                    raise EnrichmentError("Translation engine returned an invalid failed row")
                outcome = TranslationOutcome(
                    source_text,
                    "preservation-fallback",
                    attempt.failure_reason,
                )
                if integrity_monitor is not None:
                    integrity_monitor.record(fallback=True)
                if work_stats is not None:
                    work_stats.model_fallbacks += 1
                translations_by_text[source_text] = outcome
                if run_cache is not None:
                    run_cache.put(source_text, outcome)
                continue
            translated_text = attempt.text
            if not isinstance(translated_text, str):
                raise EnrichmentError("Translation engine returned a missing successful row")
            translated_text = translated_text.strip()
            if not translated_text:
                raise EnrichmentError("Translation engine returned an empty translation")
            try:
                validate_identifier_retention(source_text, translated_text)
            except EnrichmentError:
                outcome = TranslationOutcome(
                    source_text,
                    "preservation-fallback",
                    "hard-identifier-retention-mismatch",
                )
            else:
                ambiguous_count = len(advisory_identifier_spans(source_text))
                outcome = TranslationOutcome(
                    translated_text,
                    "source-retained-ambiguous" if ambiguous_count else "verified",
                    ambiguous_identifier_count=ambiguous_count,
                )
            if integrity_monitor is not None:
                integrity_monitor.record(fallback=outcome.integrity == "preservation-fallback")
            if work_stats is not None and outcome.integrity == "preservation-fallback":
                work_stats.model_fallbacks += 1
            translations_by_text[source_text] = outcome
            if run_cache is not None:
                run_cache.put(source_text, outcome)
            elif cache and outcome.integrity != "preservation-fallback":
                cache.put(target_language, source_text, outcome.text)

    translated_records: list[dict[str, Any]] = []
    execution_metadata = getattr(translator, "execution_metadata", None)
    for parent in candidates:
        source_text = str(parent["text"])
        outcome = translations_by_text[source_text]
        translated_text = outcome.text.strip()
        if not translated_text:
            raise EnrichmentError("Translation engine returned an empty translation")
        validate_identifier_retention(source_text, translated_text)
        derived = {
            key: value
            for key, value in parent.items()
            if key not in {"recordId", "text", "parentRecordId", "transform"}
        }
        transform = {
            "kind": "translation",
            "engine": translator.engine,
            "engineVersion": translator.engine_version,
            "model": translator.model_id,
            "revision": translator.revision,
            "modelSha256": translator.model_sha256,
            "sourceLanguage": "auto",
            "targetLanguage": target_language,
            "outcome": "unchanged" if translated_text == source_text else "translated",
        }
        if execution_metadata:
            transform["execution"] = dict(execution_metadata)
        attributes = dict(derived.get("attributes") or {})
        attributes["translationIntegrity"] = outcome.integrity
        if outcome.reason is not None:
            attributes["translationIntegrityReason"] = outcome.reason
        else:
            attributes.pop("translationIntegrityReason", None)
        if outcome.integrity == "source-retained-ambiguous":
            attributes["translationAmbiguousIdentifierCount"] = outcome.ambiguous_identifier_count
        else:
            attributes.pop("translationAmbiguousIdentifierCount", None)
        derived.update(
            {
                "text": translated_text,
                "parentRecordId": parent["recordId"],
                "transform": transform,
                "attributes": attributes,
            }
        )
        translated_records.append(with_record_id(derived))
    if len(translated_records) != len(candidates):
        raise EnrichmentError(
            "Translation outcome cardinality mismatch: "
            f"expected {len(candidates)}, produced {len(translated_records)}"
        )
    return translated_records


def read_normalized_jsonl(path: Path) -> Iterable[dict[str, Any]]:
    try:
        input_handle = path.open("r", encoding="utf-8")
    except OSError as exc:
        raise EnrichmentError(f"Could not open normalized JSONL '{path}': {exc}") from exc
    with input_handle:
        for line_number, line in enumerate(input_handle, start=1):
            if not line.strip():
                continue
            try:
                record = json.loads(line)
            except json.JSONDecodeError as exc:
                raise EnrichmentError(
                    f"Invalid normalized JSONL at line {line_number}: {exc}"
                ) from exc
            if not isinstance(record, dict):
                raise EnrichmentError(f"Normalized JSONL line {line_number} is not an object")
            required = ("recordId", "text", "sourceFile", "location", "origin")
            missing = [field for field in required if not record.get(field)]
            if record.get("schemaVersion") != SCHEMA_VERSION:
                raise EnrichmentError(
                    f"Normalized JSONL line {line_number} uses an unsupported schema version"
                )
            if record.get("recordType") != "string" or missing:
                raise EnrichmentError(
                    f"Normalized JSONL line {line_number} is missing required string fields: "
                    + ", ".join(missing)
                )
            yield record


def read_paths_from(path: Path) -> Iterable[Path]:
    """Yield literal UTF-8 path-list entries without materializing the inventory."""
    try:
        input_handle = path.open("r", encoding="utf-8-sig")
    except OSError as exc:
        raise EnrichmentError(f"Could not open path list '{path}': {exc}") from exc

    try:
        with input_handle:
            line_number = 0
            while True:
                line = input_handle.readline(MAX_PATH_LIST_LINE_CHARACTERS + 2)
                if line == "":
                    break
                line_number += 1
                value = line.rstrip("\r\n")
                if len(value) > MAX_PATH_LIST_LINE_CHARACTERS:
                    raise EnrichmentError(
                        f"Path list line {line_number} exceeds the "
                        f"{MAX_PATH_LIST_LINE_CHARACTERS}-character safety limit"
                    )
                if not value.strip():
                    continue
                if "\x00" in value:
                    raise EnrichmentError(f"Path list line {line_number} contains a NUL byte")
                yield Path(value)
    except UnicodeError as exc:
        raise EnrichmentError(f"Path list '{path}' is not valid UTF-8: {exc}") from exc
    except OSError as exc:
        raise EnrichmentError(f"Could not read path list '{path}': {exc}") from exc


def iter_extraction_paths(
    positional_paths: Sequence[Path], paths_from: Path | None
) -> Iterable[Path]:
    """Select the validated extraction input while keeping file lists streaming."""
    if positional_paths and paths_from is not None:
        raise EnrichmentError("Positional paths cannot be combined with --paths-from")
    if paths_from is not None:
        found_path = False
        for path in read_paths_from(paths_from):
            found_path = True
            yield path
        if not found_path:
            raise EnrichmentError(f"Path list '{paths_from}' contains no usable paths")
    else:
        yield from positional_paths


def read_input_manifest(path: Path) -> Iterable[InputIdentity]:
    try:
        input_handle = path.open("r", encoding="utf-8-sig", errors="strict")
    except OSError as exc:
        raise EnrichmentError("Could not open the evidence input manifest") from exc
    with input_handle:
        try:
            for line_number, line in enumerate(input_handle, 1):
                value = line.rstrip("\r\n")
                if not value or len(value) > MAX_INPUT_MANIFEST_LINE_CHARACTERS:
                    raise EnrichmentError(
                        f"Evidence input manifest line {line_number} is empty or too large"
                    )
                try:
                    record = json.loads(value)
                except json.JSONDecodeError as exc:
                    raise EnrichmentError(
                        f"Evidence input manifest line {line_number} is invalid"
                    ) from exc
                if not isinstance(record, dict) or set(record) != {
                    "schemaVersion",
                    "path",
                    "length",
                    "sha256",
                }:
                    raise EnrichmentError(
                        f"Evidence input manifest line {line_number} has unsupported fields"
                    )
                source_path = record.get("path")
                length = record.get("length")
                sha256 = record.get("sha256")
                if (
                    record.get("schemaVersion") != SCHEMA_VERSION
                    or not isinstance(source_path, str)
                    or not source_path
                    or not os.path.isabs(source_path)
                    or type(length) is not int
                    or length < 0
                    or not isinstance(sha256, str)
                    or len(sha256) != 64
                    or sha256 != sha256.lower()
                    or any(character not in "0123456789abcdef" for character in sha256)
                ):
                    raise EnrichmentError(
                        f"Evidence input manifest line {line_number} has invalid identity fields"
                    )
                yield InputIdentity(source_path, length, sha256)
        except UnicodeError as exc:
            raise EnrichmentError("The evidence input manifest is not valid UTF-8") from exc


def iter_routing_inputs(
    positional_paths: Sequence[Path],
    paths_from: Path | None,
    input_manifest: Path,
) -> Iterable[RoutingInput]:
    paths = iter(iter_extraction_paths(positional_paths, paths_from))
    identities = iter(read_input_manifest(input_manifest))
    sentinel = object()
    while True:
        raw_path = next(paths, sentinel)
        identity = next(identities, sentinel)
        if raw_path is sentinel and identity is sentinel:
            return
        if raw_path is sentinel or identity is sentinel:
            raise EnrichmentError("Input inventory and evidence manifest have different coverage")
        assert isinstance(raw_path, Path)
        assert isinstance(identity, InputIdentity)
        path = raw_path.resolve()
        if not _path_literals_equal(str(path), identity.path):
            raise EnrichmentError("Input inventory order does not match the evidence manifest")
        if not path.is_file():
            raise EnrichmentError("An input inventory item is not a regular file")
        try:
            source_size = path.stat().st_size
        except OSError as exc:
            raise EnrichmentError("An input inventory item is unavailable") from exc
        if source_size != identity.length:
            raise EnrichmentError("An input inventory item changed length before content triage")
        yield RoutingInput(path, identity)


def read_routing_manifest(path: Path) -> Iterable[dict[str, Any]]:
    try:
        input_handle = path.open("r", encoding="utf-8", errors="strict")
    except OSError as exc:
        raise EnrichmentError("Could not open the content routing manifest") from exc
    with input_handle:
        try:
            expected_ordinal = 1
            for line in input_handle:
                value = line.rstrip("\r\n")
                if not value or len(value) > MAX_JSONL_LINE_CHARACTERS:
                    raise EnrichmentError(
                        f"Content routing row {expected_ordinal} is empty or too large"
                    )
                try:
                    record = json.loads(value)
                except json.JSONDecodeError as exc:
                    raise EnrichmentError(
                        f"Content routing row {expected_ordinal} is invalid"
                    ) from exc
                required_properties = {
                    "schemaVersion",
                    "recordType",
                    "policyVersion",
                    "ordinal",
                    "decisionId",
                    "sourceFile",
                    "sourceSize",
                    "sourceSha256",
                    "classifier",
                    "signals",
                    "eligibleRoutes",
                    "scheduledRoutes",
                    "conflicts",
                }
                if not isinstance(record, dict):
                    raise EnrichmentError(
                        f"Content routing row {expected_ordinal} has invalid required fields"
                    )
                schema_version = record.get("schemaVersion")
                policy_version = record.get("policyVersion")
                eligible_value = record.get("eligibleRoutes")
                scheduled_value = record.get("scheduledRoutes")
                native_scheduled = isinstance(scheduled_value, list) and "native" in scheduled_value
                valid_policy = type(schema_version) is int and (
                    (
                        schema_version == SCHEMA_VERSION
                        and policy_version == "content-routing-v1"
                        and native_scheduled
                    )
                    or (
                        schema_version == 2
                        and policy_version == "content-routing-v2"
                        and not native_scheduled
                    )
                )
                if (
                    set(record) != required_properties
                    or not valid_policy
                    or record.get("recordType") != "content-route"
                    or record.get("ordinal") != expected_ordinal
                    or not isinstance(record.get("sourceFile"), str)
                    or not isinstance(record.get("classifier"), dict)
                    or not isinstance(eligible_value, list)
                    or not isinstance(scheduled_value, list)
                    or any(not isinstance(route, str) for route in eligible_value)
                    or any(not isinstance(route, str) for route in scheduled_value)
                    or eligible_value != sorted(set(eligible_value))
                    or scheduled_value != sorted(set(scheduled_value))
                    or "native" not in eligible_value
                    or any(route not in {"native", "floss", "ocr"} for route in eligible_value)
                    or any(route not in eligible_value for route in scheduled_value)
                ):
                    raise EnrichmentError(
                        f"Content routing row {expected_ordinal} has invalid required fields"
                    )
                decision_id = record.get("decisionId")
                material = {key: item for key, item in record.items() if key != "decisionId"}
                expected_decision_id = (
                    "sha256:" + hashlib.sha256(canonical_json(material).encode("utf-8")).hexdigest()
                )
                if decision_id != expected_decision_id:
                    raise EnrichmentError(
                        f"Content routing row {expected_ordinal} has an invalid decision identity"
                    )
                yield record
                expected_ordinal += 1
        except UnicodeError as exc:
            raise EnrichmentError("The content routing manifest is not valid UTF-8") from exc


def _classification_from_routing_record(record: dict[str, Any]) -> Classification:
    classifier = record["classifier"]
    output = classifier.get("output")
    score = classifier.get("score")
    if not isinstance(output, dict):
        raise EnrichmentError("A routed recovery input has no Magika output")
    normalized_output = {
        "label": output.get("label"),
        "is_text": output.get("isText"),
        "mime_type": output.get("mimeType"),
        "group": output.get("group"),
    }
    return _classification_from_magika(normalized_output, score, "Routed recovery Magika output")


def iter_routed_recovery_inputs(
    positional_paths: Sequence[Path],
    paths_from: Path | None,
    routing_manifest: Path,
) -> Iterable[tuple[Path, Classification, InputIdentity, str]]:
    targets = iter(iter_extraction_paths(positional_paths, paths_from))
    sentinel = object()
    raw_target = next(targets, sentinel)
    for route in read_routing_manifest(routing_manifest):
        if raw_target is sentinel:
            continue
        assert isinstance(raw_target, Path)
        target = raw_target.resolve()
        if not _path_literals_equal(str(target), str(route["sourceFile"])):
            continue
        if "floss" not in route["scheduledRoutes"]:
            raise EnrichmentError("A recovery input was not scheduled for FLOSS by content triage")
        if not target.is_file():
            raise EnrichmentError("A routed recovery input is not a regular file")
        source_size = route.get("sourceSize")
        source_sha256 = route.get("sourceSha256")
        if (
            type(source_size) is not int
            or source_size < 0
            or not isinstance(source_sha256, str)
            or len(source_sha256) != 64
            or any(character not in "0123456789abcdef" for character in source_sha256)
        ):
            raise EnrichmentError("A routed recovery input has an invalid source identity")
        identity = InputIdentity(str(target), source_size, source_sha256)
        decision_id = route.get("decisionId")
        if (
            not isinstance(decision_id, str)
            or not decision_id.startswith("sha256:")
            or len(decision_id) != 71
            or any(character not in "0123456789abcdef" for character in decision_id[7:])
        ):
            raise EnrichmentError("A routed recovery input has an invalid decision identity")
        with EvidenceReadLease(target):
            verify_routed_source_identity(target, identity)
            yield target, _classification_from_routing_record(route), identity, decision_id
            verify_routed_source_identity(target, identity)
        raw_target = next(targets, sentinel)
    if raw_target is not sentinel:
        raise EnrichmentError("The content routing manifest does not cover every recovery input")
    if next(targets, sentinel) is not sentinel:
        raise EnrichmentError("Recovery input coverage changed while reading content routes")


def verify_routed_source_identity(path: Path, identity: InputIdentity) -> None:
    try:
        length = path.stat().st_size
    except OSError as exc:
        raise EnrichmentError("A routed recovery input became unavailable") from exc
    digest = sha256_file(path)
    if length != identity.length or digest != identity.sha256:
        raise EnrichmentError(
            "A routed recovery input changed from the content-routing source identity"
        )


def translate_normalized_records(
    records: Iterable[dict[str, Any]],
    translator: Translator,
    target_language: str,
    batch_size: int,
    minimum_characters: int,
    maximum_characters: int,
    window_size: int | None = None,
    cache: TranslationCache | None = None,
    include_parents: bool = True,
    progress: Callable[[int], None] | None = None,
    run_cache: TranslationOutcomeCache | None = None,
    integrity_monitor: TranslationIntegrityMonitor | None = None,
    work_stats: TranslationWorkStats | None = None,
) -> Iterable[dict[str, Any]]:
    pending: list[dict[str, Any]] = []
    effective_window_size = window_size or batch_size
    completed = 0

    def flush_pending() -> Iterable[dict[str, Any]]:
        nonlocal completed
        pending_count = len(pending)
        translated = add_translations(
            pending,
            translator,
            target_language,
            batch_size,
            minimum_characters,
            maximum_characters,
            cache,
            run_cache,
            integrity_monitor,
            work_stats,
        )
        pending.clear()
        completed += pending_count
        if progress is not None:
            progress(completed)
        return translated

    for record in records:
        if include_parents:
            yield record
        if record.get("transform") is None and should_translate(
            str(record["text"]), minimum_characters, maximum_characters
        ):
            pending.append(record)
            if len(pending) >= effective_window_size:
                yield from flush_pending()
    if pending:
        yield from flush_pending()


def iter_unique_records(records: Iterable[dict[str, Any]]) -> Iterable[dict[str, Any]]:
    """Remove exact canonical duplicates without collapsing distinct provenance."""
    seen: set[str] = set()
    for record in records:
        record_id = str(record["recordId"])
        if record_id in seen:
            continue
        seen.add(record_id)
        yield record


def unique_records(records: Iterable[dict[str, Any]]) -> list[dict[str, Any]]:
    """Materialized helper used by tests and small callers."""
    return list(iter_unique_records(records))


def validate_transformers_version(version: str) -> None:
    try:
        major, minor = (int(part) for part in version.split(".")[:2])
    except (TypeError, ValueError) as exc:
        raise EnrichmentError(f"Could not parse Transformers version '{version}'") from exc
    if major != 4 or minor < 57:
        raise EnrichmentError(
            "MADLAD enrichment requires Transformers >=4.57,<5; "
            f"found {version}. Version 5 produced invalid repeated-token output in validation."
        )


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as input_handle:
            while chunk := input_handle.read(8 * 1024 * 1024):
                digest.update(chunk)
    except OSError as exc:
        raise EnrichmentError(f"Could not hash translation model '{path}': {exc}") from exc
    return digest.hexdigest()


class MadladTranslator:
    """Offline MADLAD-400 inference over an already downloaded local model snapshot."""

    engine = "huggingface-transformers"

    def __init__(
        self,
        model_path: Path,
        model_id: str,
        revision: str,
        expected_model_sha256: str,
        device: str,
        max_input_tokens: int,
        max_new_tokens: int,
        threads: int = 0,
    ) -> None:
        model_file = model_path / "model.safetensors"
        if not model_file.is_file():
            raise EnrichmentError(f"Local model snapshot has no model.safetensors: {model_path}")
        actual_model_sha256 = sha256_file(model_file)
        if actual_model_sha256 != expected_model_sha256.lower():
            raise EnrichmentError(
                "Translation model SHA-256 mismatch: "
                f"expected {expected_model_sha256.lower()}, got {actual_model_sha256}"
            )

        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        try:
            import torch
            import transformers
            from transformers import AutoModelForSeq2SeqLM, AutoTokenizer
        except ImportError as exc:
            raise EnrichmentError(
                "Offline translation requires the 'translation' dependencies; "
                "install this tool with the translation extra"
            ) from exc

        validate_transformers_version(transformers.__version__)
        self._torch = torch
        if threads > 0:
            self._torch.set_num_threads(threads)
        self.model_id = model_id
        self.revision = revision
        self.model_sha256 = actual_model_sha256
        self._max_input_tokens = max_input_tokens
        self._max_new_tokens = max_new_tokens
        self._device = self._choose_device(device)
        self.parallelism = 1
        self.execution_metadata = {
            "device": self._device,
            "airgap": airgap_mode_enabled(),
            "parallelism": self.parallelism,
            "batching": "length-bucketed",
            "decoding": "greedy",
            "threads": threads if threads > 0 else "runtime-auto",
        }
        self.engine_version = (
            f"transformers={transformers.__version__};torch={torch.__version__};"
            f"device={self._device}"
        )
        self._tokenizer = AutoTokenizer.from_pretrained(str(model_path), local_files_only=True)
        self._model = AutoModelForSeq2SeqLM.from_pretrained(
            str(model_path), local_files_only=True, dtype="auto"
        )
        self._model.to(self._device)
        self._model.eval()

    def _choose_device(self, requested: str) -> str:
        if requested == "hybrid":
            raise EnrichmentError(
                "The Transformers MADLAD engine cannot split this checkpoint across CPU and GPU; "
                "use cpu/cuda or the llama.cpp engine's adaptive offload"
            )
        if requested == "cpu":
            return "cpu"
        if requested == "cuda":
            if not self._torch.cuda.is_available():
                raise EnrichmentError("CUDA translation was requested but PyTorch cannot use CUDA")
            total_memory = self._torch.cuda.get_device_properties(0).total_memory
            if total_memory < 16 * 1024**3:
                raise EnrichmentError(
                    "The unquantized MADLAD checkpoint requires a CUDA device with at least "
                    "16 GiB; "
                    "use CPU or a separately validated quantized worker"
                )
            return "cuda"
        if self._torch.cuda.is_available():
            total_memory = self._torch.cuda.get_device_properties(0).total_memory
            if total_memory >= 16 * 1024**3:
                return "cuda"
        return "cpu"

    def translate(self, texts: Sequence[str], target_language: str) -> list[str]:
        prompts = [f"<2{target_language}> {text}" for text in texts]
        encoded = self._tokenizer(
            prompts,
            return_tensors="pt",
            padding=True,
            truncation=False,
        )
        attention_mask = encoded.get("attention_mask")
        if attention_mask is None or len(attention_mask) != len(texts):
            raise EnrichmentError(
                "MADLAD tokenizer did not return one attention mask per source; "
                "input completeness cannot be proven"
            )
        for index, row in enumerate(attention_mask):
            token_count = row.sum() if hasattr(row, "sum") else sum(row)
            if hasattr(token_count, "item"):
                token_count = token_count.item()
            if not isinstance(token_count, int) or isinstance(token_count, bool):
                raise EnrichmentError(
                    f"MADLAD tokenizer returned an invalid token count for row {index}"
                )
            if token_count > self._max_input_tokens:
                raise EnrichmentError(
                    f"MADLAD source row {index} requires {token_count} tokens, exceeding the "
                    f"{self._max_input_tokens}-token input safety limit; source was not truncated"
                )
        encoded = encoded.to(self._device)
        with self._torch.inference_mode():
            generated = self._model.generate(
                **encoded,
                max_new_tokens=self._max_new_tokens,
                return_dict_in_generate=True,
            )
        sequences = getattr(generated, "sequences", generated)
        generation_config = getattr(self._model, "generation_config", None)
        eos_token_id = getattr(generation_config, "eos_token_id", None)
        if eos_token_id is None:
            eos_token_id = getattr(self._tokenizer, "eos_token_id", None)
        if hasattr(eos_token_id, "tolist"):
            eos_token_id = eos_token_id.tolist()
        if isinstance(eos_token_id, int) and not isinstance(eos_token_id, bool):
            eos_token_ids = {eos_token_id}
        elif isinstance(eos_token_id, (list, tuple, set)):
            eos_token_ids = {
                value
                for value in eos_token_id
                if isinstance(value, int) and not isinstance(value, bool)
            }
        else:
            eos_token_ids = set()
        if not eos_token_ids:
            raise EnrichmentError(
                "MADLAD completion cannot be proven because no EOS token ID is configured"
            )
        for index, sequence in enumerate(sequences):
            token_ids = sequence.tolist() if hasattr(sequence, "tolist") else list(sequence)
            if not any(token_id in eos_token_ids for token_id in token_ids):
                raise EnrichmentError(
                    f"MADLAD translation row {index} reached the {self._max_new_tokens}-token "
                    "generation bound without EOS"
                )
        decoded = self._tokenizer.batch_decode(sequences, skip_special_tokens=True)
        if len(decoded) != len(texts):
            raise EnrichmentError(
                f"MADLAD returned {len(decoded)} rows for a batch of {len(texts)}"
            )
        translations: list[str] = []
        for index, value in enumerate(decoded):
            if not isinstance(value, str) or not value.strip():
                raise EnrichmentError(f"MADLAD returned an empty translation for row {index}")
            translations.append(value.strip())
        return translations


def llama_translation_prompt(text: str, target_language: str) -> str:
    language_name = LANGUAGE_NAMES.get(target_language.lower(), target_language)
    return (
        f"Translate the following text into {language_name}. Only output the translated result "
        "without explanation. Preserve every email address, username, IP address, URL, file "
        "path, file name, hash, CVE, registry path, host name, port, GUID, placeholder, and "
        "delimiter exactly as written:\n" + text
    )


def build_llama_server_command(
    server: str,
    model_path: Path,
    *,
    gpu_layers: str,
    device: str,
    context_size: int,
    parallelism: int,
    port: int,
    threads: int,
) -> list[str]:
    command = [
        server,
        "-lv",
        "4",
        "-m",
        str(model_path.resolve()),
        "-ngl",
        gpu_layers,
        "--device",
        device,
        "-c",
        str(context_size),
        "-np",
        str(parallelism),
        "-cb",
        "--host",
        "127.0.0.1",
        "--port",
        str(port),
        "--offline",
        "--jinja",
        "--reasoning",
        "off",
        "--no-warmup",
        "--no-webui",
        "--flash-attn",
        "on",
    ]
    if threads > 0:
        command.extend(("--threads", str(threads), "--threads-batch", str(threads)))
    return command


class LlamaCppTranslator:
    """Offline GGUF translation through a private, short-lived llama.cpp server."""

    engine = "llama.cpp"
    _SELF_TEST_SOURCE = "Bonjour. Preserve CVE-2099-99999 exactly."

    def __init__(
        self,
        server: str,
        model_path: Path,
        model_id: str,
        revision: str,
        expected_model_sha256: str,
        device: str,
        max_input_tokens: int,
        max_new_tokens: int,
        startup_timeout_seconds: int,
        request_timeout_seconds: int,
        batch_size: int = 8,
        parallelism: int = 0,
        strict_determinism: bool = False,
        threads: int = 0,
        gpu_layers: int = -1,
    ) -> None:
        if not model_path.is_file():
            raise EnrichmentError(f"Local GGUF model was not found: {model_path}")
        actual_model_sha256 = sha256_file(model_path)
        if actual_model_sha256 != expected_model_sha256.lower():
            raise EnrichmentError(
                "Translation model SHA-256 mismatch: "
                f"expected {expected_model_sha256.lower()}, got {actual_model_sha256}"
            )

        self._server = executable_path(server)
        runtime_sha256 = sha256_file(Path(self._server))
        version_result = run_checked([self._server, "--version"], 30)
        version_lines = (version_result.stdout + "\n" + version_result.stderr).splitlines()
        version = next(
            (line.strip() for line in version_lines if "version" in line.lower()),
            next((line.strip() for line in version_lines if line.strip()), "unknown"),
        )
        devices_result = run_checked([self._server, "--list-devices"], 30)
        devices = devices_result.stdout + "\n" + devices_result.stderr
        cuda_device = next(
            (
                line.strip().split(":", maxsplit=1)[0]
                for line in devices.splitlines()
                if line.strip().upper().startswith("CUDA")
            ),
            None,
        )
        cuda_available = cuda_device is not None
        if device == "cuda" and not cuda_available:
            raise EnrichmentError(
                "CUDA translation was requested but llama.cpp did not list a CUDA device"
            )
        if device == "hybrid" and not cuda_available:
            raise EnrichmentError(
                "Hybrid translation was requested but llama.cpp did not list a CUDA device"
            )
        if gpu_layers < -1:
            raise EnrichmentError("Translation GPU layers must be -1 (automatic) or non-negative")
        if device == "cpu" and gpu_layers not in {-1, 0}:
            raise EnrichmentError("CPU translation cannot offload layers to a GPU")
        if device == "auto" and gpu_layers != -1:
            raise EnrichmentError("Automatic translation cannot use an exact GPU layer count")
        if device == "cuda" and gpu_layers != -1:
            raise EnrichmentError(
                "CUDA translation always uses full offload; select hybrid for an exact layer count"
            )
        if device == "hybrid" and gpu_layers <= 0:
            raise EnrichmentError(
                "Hybrid translation requires an explicit positive GPU layer count"
            )
        if gpu_layers > 0 and not cuda_available:
            raise EnrichmentError("GPU layer offload was requested but CUDA is unavailable")
        self.model_id = model_id
        self.revision = revision
        self.model_sha256 = actual_model_sha256
        self._model_path = model_path.resolve()
        self._version = version
        self._runtime_sha256 = runtime_sha256
        self._requested_device = device
        self._requested_parallelism = parallelism
        self._batch_size = batch_size
        self._model_size_bytes = model_path.stat().st_size
        self._strict_determinism = strict_determinism
        self._threads = threads
        self._max_input_tokens = max_input_tokens
        self._max_new_tokens = max_new_tokens
        self._request_timeout_seconds = request_timeout_seconds
        self._health_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self._thread_state = threading.local()
        self._evidence_inference_started = False
        self._closed = False
        self._process: subprocess.Popen[Any] | None = None
        self._stdout_handle: Any = None
        self._stderr_handle: Any = None
        self._temporary_directory = tempfile.TemporaryDirectory(prefix="bstrings-llama-")
        try:
            auto_cuda_failure: str | None = None
            cuda_device_metadata = (
                self._nvidia_device_metadata(str(cuda_device))
                if cuda_available and device != "cpu"
                else {}
            )
            if cuda_available and device != "cpu":
                cuda_device_metadata = {
                    **cuda_device_metadata,
                    "gpuMinimumFreeMemoryMiB": MINIMUM_ACCEPTED_CUDA_FREE_MEMORY_MIB,
                }
            cuda_policy_failure = self._cuda_policy_failure(cuda_device_metadata)
            if device in {"cuda", "hybrid"} and cuda_policy_failure is not None:
                raise EnrichmentError(
                    self._cuda_policy_error(cuda_policy_failure, cuda_device_metadata)
                )
            if device == "auto" and cuda_available and cuda_policy_failure is None:
                try:
                    self._start_preflighted_runtime(
                        actual_device="cuda",
                        server_device=str(cuda_device),
                        gpu_layers="all",
                        cuda_device_description=self._cuda_device_description(
                            devices, str(cuda_device)
                        ),
                        cuda_device_metadata=cuda_device_metadata,
                        require_full_cuda=True,
                        startup_timeout_seconds=startup_timeout_seconds,
                    )
                except (EnrichmentError, OSError) as exc:
                    auto_cuda_failure = self._preflight_failure_reason(exc)
                    self._stop_server_attempt()
                    print(
                        "CUDA translation preflight failed before evidence processing; "
                        "using the CPU runtime.",
                        file=sys.stderr,
                    )
                    self._start_preflighted_runtime(
                        actual_device="cpu",
                        server_device="none",
                        gpu_layers="0",
                        cuda_device_description=None,
                        cuda_device_metadata=cuda_device_metadata,
                        require_full_cuda=False,
                        startup_timeout_seconds=startup_timeout_seconds,
                        auto_cuda_failure=auto_cuda_failure,
                    )
            elif device == "auto" and cuda_available:
                print(
                    "CUDA translation hardware is outside the accepted capability policy; "
                    "using the CPU runtime before evidence processing.",
                    file=sys.stderr,
                )
                self._start_preflighted_runtime(
                    actual_device="cpu",
                    server_device="none",
                    gpu_layers="0",
                    cuda_device_description=None,
                    cuda_device_metadata=cuda_device_metadata,
                    require_full_cuda=False,
                    startup_timeout_seconds=startup_timeout_seconds,
                    auto_cuda_failure=cuda_policy_failure,
                )
            elif device == "cuda":
                self._start_preflighted_runtime(
                    actual_device="cuda",
                    server_device=str(cuda_device),
                    gpu_layers="all",
                    cuda_device_description=self._cuda_device_description(
                        devices, str(cuda_device)
                    ),
                    cuda_device_metadata=cuda_device_metadata,
                    require_full_cuda=True,
                    startup_timeout_seconds=startup_timeout_seconds,
                )
            elif device == "hybrid":
                self._start_preflighted_runtime(
                    actual_device="hybrid-cuda-cpu",
                    server_device=str(cuda_device),
                    gpu_layers=str(gpu_layers),
                    cuda_device_description=self._cuda_device_description(
                        devices, str(cuda_device)
                    ),
                    cuda_device_metadata=cuda_device_metadata,
                    require_full_cuda=False,
                    startup_timeout_seconds=startup_timeout_seconds,
                )
            else:
                self._start_preflighted_runtime(
                    actual_device="cpu",
                    server_device="none",
                    gpu_layers="0",
                    cuda_device_description=None,
                    cuda_device_metadata={},
                    require_full_cuda=False,
                    startup_timeout_seconds=startup_timeout_seconds,
                )
        except BaseException:
            self.close()
            raise
        atexit.register(self.close)

    @staticmethod
    def _cuda_device_description(devices: str, device: str) -> str | None:
        for line in devices.splitlines():
            prefix, separator, description = line.strip().partition(":")
            if separator and prefix == device:
                normalized = description.strip()
                normalized = re.sub(
                    r"\((\d+)\s+MiB,\s*\d+\s+MiB\s+free\)\s*$",
                    r"(\1 MiB)",
                    normalized,
                    flags=re.IGNORECASE,
                )
                return normalized or None
        return None

    @staticmethod
    def _preflight_failure_reason(error: BaseException) -> str:
        if isinstance(error, TranslationRowError):
            return error.reason
        message = str(error).lower()
        if "offload" in message or "cuda placement" in message:
            return "cuda-placement-validation-failed"
        return "cuda-runtime-preflight-failed"

    @staticmethod
    def _cuda_policy_failure(metadata: dict[str, Any]) -> str | None:
        compute_capability = metadata.get("gpuComputeCapability")
        if not compute_capability:
            return "cuda-compute-capability-unavailable"
        if compute_capability not in ACCEPTED_CUDA_COMPUTE_CAPABILITIES:
            return "cuda-compute-capability-not-accepted"
        total_memory = metadata.get("gpuMemoryTotalMiB")
        free_memory = metadata.get("gpuMemoryFreePreflightMiB")
        if (
            not isinstance(total_memory, int)
            or isinstance(total_memory, bool)
            or not isinstance(free_memory, int)
            or isinstance(free_memory, bool)
            or total_memory <= 0
            or free_memory < 0
            or free_memory > total_memory
        ):
            return "cuda-free-memory-unavailable"
        if free_memory < MINIMUM_ACCEPTED_CUDA_FREE_MEMORY_MIB:
            return "cuda-insufficient-free-memory"
        return None

    @staticmethod
    def _cuda_policy_error(reason: str, metadata: dict[str, Any]) -> str:
        if reason in {
            "cuda-compute-capability-unavailable",
            "cuda-compute-capability-not-accepted",
        }:
            accepted = ", ".join(sorted(ACCEPTED_CUDA_COMPUTE_CAPABILITIES))
            observed = metadata.get("gpuComputeCapability", "unavailable")
            return (
                "CUDA translation requires an accepted compute capability "
                f"({accepted}); detected {observed}"
            )
        observed_free = metadata.get("gpuMemoryFreePreflightMiB", "unavailable")
        return (
            "CUDA translation requires authenticated preflight free VRAM of at least "
            f"{MINIMUM_ACCEPTED_CUDA_FREE_MEMORY_MIB} MiB; detected {observed_free} MiB"
        )

    def _start_preflighted_runtime(
        self,
        *,
        actual_device: str,
        server_device: str,
        gpu_layers: str,
        cuda_device_description: str | None,
        cuda_device_metadata: dict[str, Any],
        require_full_cuda: bool,
        startup_timeout_seconds: int,
        auto_cuda_failure: str | None = None,
    ) -> None:
        if self._evidence_inference_started:
            raise EnrichmentError("Translation runtime cannot change after evidence inference")
        self.parallelism = resolve_translation_parallelism(
            self._requested_parallelism,
            strict_determinism=self._strict_determinism,
            has_cuda=actual_device != "cpu",
            model_size_bytes=self._model_size_bytes,
            batch_size=self._batch_size,
        )
        temporary_path = Path(self._temporary_directory.name)
        attempt_name = "cuda" if actual_device != "cpu" else "cpu"
        self._stdout_path = temporary_path / f"{attempt_name}-stdout.log"
        self._stderr_path = temporary_path / f"{attempt_name}-stderr.log"
        self._stdout_handle = self._stdout_path.open("w", encoding="utf-8")
        self._stderr_handle = self._stderr_path.open("w", encoding="utf-8")
        self._port = self._available_loopback_port()
        per_slot_context = max(2048, self._max_input_tokens + self._max_new_tokens + 512)
        context_size = per_slot_context * self.parallelism
        command = build_llama_server_command(
            self._server,
            self._model_path,
            gpu_layers=gpu_layers,
            device=server_device,
            context_size=context_size,
            parallelism=self.parallelism,
            port=self._port,
            threads=self._threads,
        )
        creation_flags = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        try:
            self._process = subprocess.Popen(
                command,
                stdin=subprocess.DEVNULL,
                stdout=self._stdout_handle,
                stderr=self._stderr_handle,
                creationflags=creation_flags,
            )
            self._wait_until_healthy(startup_timeout_seconds)
            placement = self._runtime_placement_metadata(require_full_cuda=require_full_cuda)
            self._run_pre_evidence_self_test()
        except BaseException:
            self._stop_server_attempt()
            raise

        runtime_libraries = self._runtime_library_hashes(actual_device)
        self.execution_metadata = {
            "requestedDevice": self._requested_device,
            "device": actual_device,
            "resolvedDevice": server_device,
            "deviceDescription": cuda_device_description,
            "airgap": airgap_mode_enabled(),
            "gpuLayers": gpu_layers,
            "parallelism": self.parallelism,
            "continuousBatching": True,
            "promptCache": not self._strict_determinism,
            "decoding": "greedy-top1",
            "threads": self._threads if self._threads > 0 else "runtime-auto",
            "preEvidenceSelfTest": "passed",
            "runtimeExecutableSha256": self._runtime_sha256,
            "runtimeLibraries": runtime_libraries,
            **placement,
            **cuda_device_metadata,
        }
        if auto_cuda_failure is not None:
            self.execution_metadata["autoCudaFallback"] = auto_cuda_failure
        self.engine_version = (
            f"{self._version};device={actual_device};parallelism={self.parallelism};"
            f"gpu-layers={gpu_layers};"
            f"prompt-cache={'off' if self._strict_determinism else 'on'}"
        )

    def _run_pre_evidence_self_test(self) -> None:
        translated = self._translate_one(self._SELF_TEST_SOURCE, "en")
        validate_identifier_retention(self._SELF_TEST_SOURCE, translated)

    def _runtime_log_text(self) -> str:
        handle = self._stderr_handle
        if handle is not None:
            handle.flush()
        try:
            return self._stderr_path.read_text(encoding="utf-8", errors="replace")
        except OSError as exc:
            raise EnrichmentError(
                f"Could not inspect llama.cpp runtime placement log: {exc}"
            ) from exc

    def _runtime_placement_metadata(self, *, require_full_cuda: bool) -> dict[str, Any]:
        log_text = self._runtime_log_text()
        offload_matches = re.findall(
            r"offloaded\s+(\d+)/(\d+)\s+layers\s+to\s+GPU", log_text, re.IGNORECASE
        )
        observed_layers = int(offload_matches[-1][0]) if offload_matches else 0
        total_layers = int(offload_matches[-1][1]) if offload_matches else 0
        allocation_fallback_observed = bool(
            re.search(
                r"(?:failed to allocate|out of device memory|falling back to CPU)",
                log_text,
                re.IGNORECASE,
            )
        )
        if require_full_cuda and (
            total_layers <= 0 or observed_layers != total_layers or allocation_fallback_observed
        ):
            raise EnrichmentError(
                "llama.cpp CUDA placement did not prove complete model-layer offload"
            )

        def last_buffer(pattern: str) -> float | None:
            values = re.findall(pattern, log_text, re.IGNORECASE)
            return float(values[-1]) if values else None

        metadata: dict[str, Any] = {
            "observedGpuLayers": observed_layers,
            "observedTotalLayers": total_layers,
            "fullLayerOffloadValidated": bool(
                total_layers > 0
                and observed_layers == total_layers
                and not allocation_fallback_observed
            ),
            "cudaAllocationFallbackObserved": allocation_fallback_observed,
        }
        buffer_patterns = {
            "hostModelBufferMiB": r"(?:CPU(?:_Mapped)?)\s+model buffer size\s*=\s*([0-9.]+)\s+MiB",
            "gpuModelBufferMiB": r"CUDA\d+\s+model buffer size\s*=\s*([0-9.]+)\s+MiB",
            "gpuKvBufferMiB": r"CUDA\d+\s+KV buffer size\s*=\s*([0-9.]+)\s+MiB",
            "gpuComputeBufferMiB": r"CUDA\d+\s+compute buffer size\s*=\s*([0-9.]+)\s+MiB",
            "hostComputeBufferMiB": r"CUDA_Host\s+compute buffer size\s*=\s*([0-9.]+)\s+MiB",
        }
        for name, pattern in buffer_patterns.items():
            value = last_buffer(pattern)
            if value is not None:
                metadata[name] = value
        return metadata

    def _runtime_library_hashes(self, actual_device: str) -> dict[str, str]:
        log_text = self._runtime_log_text()
        paths: dict[str, Path] = {}
        for match in re.finditer(
            r"loaded\s+(?:CUDA|CPU)\s+backend\s+from\s+(.+?\.dll)\s*$",
            log_text,
            re.IGNORECASE | re.MULTILINE,
        ):
            path = Path(re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", match.group(1)).strip())
            if path.is_file():
                paths[path.name] = path
        runtime_directory = Path(self._server).parent
        for name in ("ggml.dll", "ggml-base.dll", "llama.dll"):
            path = runtime_directory / name
            if path.is_file():
                paths[name] = path
        for path in runtime_directory.glob("ggml-cpu-*.dll"):
            if path.is_file():
                paths[path.name] = path
        if actual_device != "cpu":
            for name in ("cublas64_12.dll", "cublasLt64_12.dll", "cudart64_12.dll"):
                path = runtime_directory / name
                if path.is_file():
                    paths[name] = path
        return {name: sha256_file(path) for name, path in sorted(paths.items())}

    @staticmethod
    def _nvidia_device_metadata(device: str) -> dict[str, Any]:
        nvidia_smi = shutil.which("nvidia-smi")
        device_match = re.fullmatch(r"CUDA(\d+)", device)
        if nvidia_smi is None or device_match is None:
            return {}
        try:
            result = run_checked(
                [
                    nvidia_smi,
                    "--query-gpu=index,driver_version,compute_cap,memory.total,memory.free",
                    "--format=csv,noheader,nounits",
                ],
                30,
            )
        except EnrichmentError:
            return {}
        expected_index = device_match.group(1)
        for line in result.stdout.splitlines():
            values = [value.strip() for value in line.split(",")]
            if len(values) >= 3 and values[0] == expected_index:
                metadata: dict[str, Any] = {
                    "gpuDriverVersion": values[1],
                    "gpuComputeCapability": values[2],
                }
                if len(values) == 5:
                    try:
                        metadata["gpuMemoryTotalMiB"] = int(values[3])
                        metadata["gpuMemoryFreePreflightMiB"] = int(values[4])
                    except ValueError:
                        pass
                return metadata
        return {}

    @staticmethod
    def _available_loopback_port() -> int:
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
            listener.bind(("127.0.0.1", 0))
            return int(listener.getsockname()[1])

    def _wait_until_healthy(self, timeout_seconds: int) -> None:
        deadline = time.monotonic() + timeout_seconds
        health_url = f"http://127.0.0.1:{self._port}/health"
        while time.monotonic() < deadline:
            if self._process.poll() is not None:
                self._stderr_handle.flush()
                diagnostic = self._stderr_path.read_text(encoding="utf-8", errors="replace")[-4000:]
                raise EnrichmentError(
                    f"llama.cpp exited before becoming healthy: {diagnostic.strip()}"
                )
            try:
                with self._health_opener.open(health_url, timeout=2) as response:
                    payload = json.load(response)
                if payload.get("status") == "ok":
                    return
            except (OSError, TimeoutError, urllib.error.URLError, json.JSONDecodeError):
                pass
            time.sleep(0.2)
        raise EnrichmentError(f"llama.cpp did not become healthy within {timeout_seconds} seconds")

    def translate(self, texts: Sequence[str], target_language: str) -> list[str]:
        self._evidence_inference_started = True
        return map_ordered_parallel(
            lambda text: self._translate_one(text, target_language),
            texts,
            self.parallelism,
        )

    def translate_attempts(
        self, texts: Sequence[str], target_language: str
    ) -> list[TranslationAttempt]:
        """Translate independent rows without ever publishing a rejected model completion."""
        self._evidence_inference_started = True

        def attempt(text: str) -> TranslationAttempt:
            try:
                return TranslationAttempt(self._translate_one(text, target_language))
            except TranslationRowError as exc:
                return TranslationAttempt(None, exc.reason)
            except EnrichmentError:
                return TranslationAttempt(None, "translation-engine-row-failure")

        return map_ordered_parallel(attempt, texts, self.parallelism)

    def _thread_opener(self) -> urllib.request.OpenerDirector:
        opener = getattr(self._thread_state, "opener", None)
        if opener is None:
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
            self._thread_state.opener = opener
        return opener

    def _post_json(self, path: str, payload: dict[str, Any]) -> dict[str, Any]:
        request = urllib.request.Request(
            f"http://127.0.0.1:{self._port}{path}",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with self._thread_opener().open(
                request, timeout=self._request_timeout_seconds
            ) as response:
                body = json.load(response)
        except (
            OSError,
            TimeoutError,
            urllib.error.URLError,
            json.JSONDecodeError,
        ) as exc:
            raise TranslationRowError(
                "translation-request-failure",
                f"llama.cpp request to {path} failed: {exc}",
            ) from exc
        if not isinstance(body, dict):
            raise TranslationRowError(
                "invalid-engine-response",
                f"llama.cpp request to {path} returned a non-object response",
            )
        return body

    def _validated_prompt_token_count(self, messages: list[dict[str, str]]) -> int:
        template_body = self._post_json("/apply-template", {"messages": messages})
        prompt = template_body.get("prompt")
        if not isinstance(prompt, str) or not prompt:
            raise TranslationRowError(
                "invalid-engine-response",
                "llama.cpp /apply-template returned an empty prompt",
            )
        token_body = self._post_json(
            "/tokenize",
            {
                "content": prompt,
                # Chat completions tokenizes the rendered prompt with special tokens enabled.
                # Match that path exactly so both the safety limit and usage proof include BOS.
                "add_special": True,
                "parse_special": True,
                "with_pieces": False,
            },
        )
        tokens = token_body.get("tokens")
        if not isinstance(tokens, list) or any(
            not isinstance(token, int) or isinstance(token, bool) for token in tokens
        ):
            raise TranslationRowError(
                "invalid-engine-response",
                "llama.cpp /tokenize returned invalid token IDs",
            )
        token_count = len(tokens)
        if token_count == 0:
            raise TranslationRowError(
                "invalid-engine-response",
                "llama.cpp /tokenize returned no prompt tokens",
            )
        if token_count > self._max_input_tokens:
            raise TranslationRowError(
                "input-token-limit-exceeded",
                f"llama.cpp prompt requires {token_count} tokens, exceeding the "
                f"{self._max_input_tokens}-token input safety limit; source was not truncated",
            )
        return token_count

    def _translate_one(self, text: str, target_language: str) -> str:
        messages = [
            {
                "role": "user",
                "content": llama_translation_prompt(text, target_language),
            }
        ]
        expected_prompt_tokens = self._validated_prompt_token_count(messages)
        try:
            body = self._post_json(
                "/v1/chat/completions",
                {
                    "messages": messages,
                    "temperature": 0,
                    "top_p": 1,
                    "top_k": 1,
                    "seed": 1,
                    "max_tokens": self._max_new_tokens,
                    "n": 1,
                    "stream": False,
                    "cache_prompt": not self._strict_determinism,
                },
            )
            choices = body.get("choices")
            if not isinstance(choices, list) or len(choices) != 1:
                raise TranslationRowError(
                    "invalid-engine-response",
                    "llama.cpp translation response must contain exactly one choice",
                )
            choice = choices[0]
            if not isinstance(choice, dict):
                raise TranslationRowError(
                    "invalid-engine-response",
                    "llama.cpp translation choice is not an object",
                )
            finish_reason = choice.get("finish_reason")
            if finish_reason != "stop":
                reason = (
                    "generation-limit-reached"
                    if finish_reason == "length"
                    else "non-terminal-generation"
                )
                raise TranslationRowError(
                    reason,
                    "llama.cpp returned a non-terminal translation finish_reason: "
                    f"{finish_reason!r}",
                )
            content = choice["message"]["content"]
        except (
            OSError,
            TimeoutError,
            urllib.error.URLError,
            json.JSONDecodeError,
            KeyError,
            IndexError,
            TypeError,
        ) as exc:
            raise TranslationRowError(
                "invalid-engine-response",
                f"llama.cpp translation request failed: {exc}",
            ) from exc
        usage = body.get("usage")
        observed_prompt_tokens = usage.get("prompt_tokens") if isinstance(usage, dict) else None
        if (
            not isinstance(observed_prompt_tokens, int)
            or isinstance(observed_prompt_tokens, bool)
            or observed_prompt_tokens != expected_prompt_tokens
        ):
            raise TranslationRowError(
                "incomplete-prompt-acceptance",
                "llama.cpp could not prove complete prompt acceptance: "
                f"expected {expected_prompt_tokens} prompt tokens, response reported "
                f"{observed_prompt_tokens!r}",
            )
        if not isinstance(content, str) or not content.strip():
            raise TranslationRowError(
                "empty-engine-response",
                "llama.cpp returned an empty translation",
            )
        return content.strip()

    def _stop_server_attempt(self) -> None:
        process = getattr(self, "_process", None)
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        self._process = None
        for handle_name in ("_stdout_handle", "_stderr_handle"):
            handle = getattr(self, handle_name, None)
            if handle is not None:
                handle.close()
                setattr(self, handle_name, None)

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        self._stop_server_attempt()
        temporary_directory = getattr(self, "_temporary_directory", None)
        if temporary_directory is not None:
            temporary_directory.cleanup()
        with suppress(Exception):
            atexit.unregister(self.close)


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Classify extracted files with Magika, recover additional executable strings "
            "with FLOSS, and write provenance-preserving JSONL for bstrings."
        )
    )
    parser.add_argument("paths", nargs="*", type=Path, help="Extracted/carved files to inspect")
    parser.add_argument(
        "--airgap",
        action="store_true",
        help=("Enforce offline environment settings and block non-loopback Python network access"),
    )
    parser.add_argument(
        "--input-jsonl",
        type=Path,
        help=(
            "Translate an existing normalized string JSONL stream instead of invoking Magika/FLOSS"
        ),
    )
    parser.add_argument(
        "--paths-from",
        type=Path,
        help=(
            "Read extracted/carved file paths one per line from a UTF-8 file; "
            "cannot be combined with positional paths or --input-jsonl"
        ),
    )
    parser.add_argument(
        "--triage-only",
        action="store_true",
        help="Write an early content-routing manifest without invoking FLOSS or translation",
    )
    parser.add_argument(
        "--disable-native",
        action="store_true",
        help=(
            "Omit native from scheduled routes while retaining native eligibility; "
            "valid only with --triage-only and at least one specialist route"
        ),
    )
    parser.add_argument(
        "--input-manifest",
        type=Path,
        help="Trusted input identity manifest required by --triage-only",
    )
    parser.add_argument(
        "--routing-manifest",
        type=Path,
        help="Reuse an already validated content-routing manifest for FLOSS recovery",
    )
    parser.add_argument(
        "--enable-floss",
        action="store_true",
        help="Schedule FLOSS-eligible inputs in --triage-only output",
    )
    parser.add_argument(
        "--enable-ocr",
        action="store_true",
        help="Schedule OCR-eligible inputs in --triage-only output",
    )
    parser.add_argument(
        "--translations-only",
        action="store_true",
        help=(
            "Write only derived translation children; intended for the integrated bstrings.exe "
            "workflow, which publishes the unchanged parents separately"
        ),
    )
    parser.add_argument(
        "--bounded-integrated-mode",
        action="store_true",
        help=(
            "Use the integrated bstrings.exe trust and streaming contract: input-JSONL "
            "translation uses run-local exact disk deduplication, while recovery uses bounded "
            "in-memory translation deduplication"
        ),
    )
    parser.add_argument("-o", "--output", required=True, type=Path, help="Output enrichment JSONL")
    parser.add_argument("--magika", default="magika", help="Magika executable or explicit path")
    parser.add_argument("--floss", default="floss", help="FLOSS executable or explicit path")
    parser.add_argument("--minimum-length", type=int, default=4)
    parser.add_argument("--magika-timeout", type=int, default=60)
    parser.add_argument("--floss-timeout", type=int, default=1800)
    parser.add_argument(
        "--floss-format",
        choices=("auto", "pe", "sc32", "sc64"),
        default="auto",
        help="FLOSS input format; use sc32/sc64 only for known shellcode",
    )
    parser.add_argument(
        "--include-floss-static",
        action="store_true",
        help="Include FLOSS static strings too (normally duplicates bstrings native extraction)",
    )
    parser.add_argument(
        "--force-floss",
        action="store_true",
        help=(
            "Run FLOSS despite a non-PE Magika result; useful for shellcode or a known "
            "classifier miss"
        ),
    )
    parser.add_argument("--translate", action="store_true", help="Add offline English translations")
    parser.add_argument(
        "--translation-engine",
        choices=("auto", "madlad", "llama-cpp"),
        default="auto",
        help="Select from the local model path automatically, or force one offline engine",
    )
    parser.add_argument("--translation-model-path", type=Path)
    parser.add_argument("--translation-model-id")
    parser.add_argument("--translation-revision")
    parser.add_argument("--translation-model-sha256")
    parser.add_argument(
        "--translation-device",
        choices=("auto", "cpu", "cuda", "hybrid"),
        default="auto",
        help=(
            "Translation hardware: auto uses adaptive llama.cpp offload, cuda requires full "
            "GPU offload, hybrid requires CUDA and allows partial offload"
        ),
    )
    parser.add_argument("--llama-server", default="llama-server")
    parser.add_argument("--translation-startup-timeout", type=int, default=120)
    parser.add_argument("--translation-request-timeout", type=int, default=120)
    parser.add_argument("--translation-batch-size", type=int, default=8)
    parser.add_argument(
        "--translation-parallelism",
        type=int,
        default=0,
        help="llama.cpp request slots; 0 chooses conservatively from model size and hardware",
    )
    parser.add_argument(
        "--translation-threads",
        type=int,
        default=0,
        help="CPU threads supplied to the runtime; 0 keeps its hardware-aware default",
    )
    parser.add_argument(
        "--translation-gpu-layers",
        type=int,
        default=-1,
        help=("Exact llama.cpp GPU layer count for hybrid mode; -1 lets the runtime choose"),
    )
    parser.add_argument(
        "--translation-window-size",
        type=int,
        default=0,
        help="Records buffered for deduplication and length bucketing; 0 selects automatically",
    )
    parser.add_argument(
        "--translation-cache-size",
        type=int,
        default=4096,
        help="Maximum exact source/translation pairs retained in memory; 0 disables caching",
    )
    parser.add_argument(
        "--translation-strict-determinism",
        action="store_true",
        help="Use one llama.cpp slot and disable prompt-cache reuse for maximum repeatability",
    )
    parser.add_argument("--translation-target", default="en")
    parser.add_argument(
        "--translation-stats-output",
        type=Path,
        help=(
            "Atomically write schema-1 aggregate translation work statistics after a "
            "successful --input-jsonl translation"
        ),
    )
    parser.add_argument("--translation-min-characters", type=int, default=8)
    parser.add_argument("--translation-max-characters", type=int, default=2048)
    parser.add_argument("--translation-max-input-tokens", type=int, default=512)
    parser.add_argument("--translation-max-new-tokens", type=int, default=512)
    parser.add_argument(
        "--progress-total-records",
        type=int,
        default=0,
        help="Expected translation-candidate records for measured percentage reporting",
    )
    parser.add_argument(
        "--progress-total-files",
        type=int,
        default=0,
        help="Expected recovery input files for measured percentage reporting",
    )
    return parser.parse_args(argv)


def selected_translation_engine(args: argparse.Namespace) -> str:
    if args.translation_engine != "auto":
        return str(args.translation_engine)
    model_path = args.translation_model_path
    if model_path is not None and model_path.is_file() and model_path.suffix.lower() == ".gguf":
        return "llama-cpp"
    return "madlad"


def paths_refer_to_same_file(left: Path, right: Path) -> bool:
    if left.resolve() == right.resolve():
        return True
    try:
        return left.exists() and right.exists() and os.path.samefile(left, right)
    except OSError:
        return False


def validate_arguments(args: argparse.Namespace) -> None:
    input_source_count = sum(
        (bool(args.paths), args.paths_from is not None, args.input_jsonl is not None)
    )
    if input_source_count != 1:
        raise EnrichmentError(
            "Supply positional carved file paths, --paths-from, or --input-jsonl; "
            "these input modes cannot be combined"
        )
    if args.paths_from is not None:
        if not args.paths_from.is_file():
            raise EnrichmentError(f"Path list was not found: {args.paths_from}")
        if args.paths_from.resolve() == args.output.resolve():
            raise EnrichmentError("--paths-from and --output must be different paths")
    if args.input_jsonl is not None:
        if not args.input_jsonl.is_file():
            raise EnrichmentError(f"Normalized JSONL input was not found: {args.input_jsonl}")
        if args.input_jsonl.resolve() == args.output.resolve():
            raise EnrichmentError("--input-jsonl and --output must be different paths")
        if not args.translate:
            raise EnrichmentError("--input-jsonl requires --translate")
    elif args.translations_only:
        raise EnrichmentError("--translations-only requires --input-jsonl")
    if args.translation_stats_output is not None:
        if args.input_jsonl is None or not args.translate:
            raise EnrichmentError(
                "--translation-stats-output requires --input-jsonl and --translate"
            )
        protected_paths = [args.output, args.input_jsonl]
        if args.translation_model_path is not None:
            protected_paths.append(args.translation_model_path)
        if any(
            paths_refer_to_same_file(args.translation_stats_output, protected_path)
            for protected_path in protected_paths
        ):
            raise EnrichmentError(
                "--translation-stats-output must differ from input, output, and model paths"
            )
        if args.translation_stats_output.is_symlink():
            raise EnrichmentError(
                "--translation-stats-output must be a physical file when it already exists"
            )
        if args.translation_stats_output.exists():
            details = os.lstat(args.translation_stats_output)
            if not stat.S_ISREG(details.st_mode):
                raise EnrichmentError(
                    "--translation-stats-output must be a physical file when it already exists"
                )
    if args.triage_only:
        if args.input_jsonl is not None or args.translate or args.translations_only:
            raise EnrichmentError("--triage-only cannot be combined with translation")
        if args.routing_manifest is not None:
            raise EnrichmentError("--triage-only cannot consume --routing-manifest")
        if args.input_manifest is None or not args.input_manifest.is_file():
            raise EnrichmentError("--triage-only requires an existing --input-manifest")
        if args.input_manifest.resolve() == args.output.resolve():
            raise EnrichmentError("--input-manifest and --output must be different paths")
    elif args.input_manifest is not None:
        raise EnrichmentError("--input-manifest requires --triage-only")
    if args.disable_native:
        if not args.triage_only:
            raise EnrichmentError("--disable-native requires --triage-only")
        if not (args.enable_floss or args.enable_ocr):
            raise EnrichmentError("--disable-native requires --enable-floss and/or --enable-ocr")
    if args.routing_manifest is not None:
        if args.input_jsonl is not None or args.translate:
            raise EnrichmentError("--routing-manifest is supported by recovery, not translation")
        if not args.routing_manifest.is_file():
            raise EnrichmentError("The content routing manifest was not found")
        if args.routing_manifest.resolve() == args.output.resolve():
            raise EnrichmentError("--routing-manifest and --output must be different paths")
    if (args.enable_floss or args.enable_ocr) and not args.triage_only:
        raise EnrichmentError("--enable-floss and --enable-ocr require --triage-only")
    if args.minimum_length < 3:
        raise EnrichmentError("--minimum-length must be at least 3")
    if args.magika_timeout <= 0 or args.floss_timeout <= 0:
        raise EnrichmentError("Tool timeouts must be positive")
    if args.translation_startup_timeout <= 0 or args.translation_request_timeout <= 0:
        raise EnrichmentError("Translation timeouts must be positive")
    if args.translation_batch_size <= 0:
        raise EnrichmentError("--translation-batch-size must be positive")
    if args.translation_parallelism < 0:
        raise EnrichmentError("--translation-parallelism cannot be negative")
    if args.translation_threads < 0:
        raise EnrichmentError("--translation-threads cannot be negative")
    if args.translation_gpu_layers < -1:
        raise EnrichmentError("--translation-gpu-layers must be -1 or non-negative")
    if args.translation_device == "cpu" and args.translation_gpu_layers not in {-1, 0}:
        raise EnrichmentError("CPU translation cannot use --translation-gpu-layers above zero")
    if args.translation_device == "auto" and args.translation_gpu_layers != -1:
        raise EnrichmentError(
            "Use --translation-device hybrid with an exact --translation-gpu-layers value"
        )
    if args.translation_device == "cuda" and args.translation_gpu_layers != -1:
        raise EnrichmentError(
            "Use --translation-device hybrid with an exact --translation-gpu-layers value"
        )
    if args.translation_device == "hybrid" and args.translation_gpu_layers <= 0:
        raise EnrichmentError(
            "Hybrid translation requires an explicit --translation-gpu-layers value above zero"
        )
    if args.translation_window_size < 0:
        raise EnrichmentError("--translation-window-size cannot be negative")
    if 0 < args.translation_window_size < args.translation_batch_size:
        raise EnrichmentError(
            "--translation-window-size must be zero or at least --translation-batch-size"
        )
    if args.translation_cache_size < 0:
        raise EnrichmentError("--translation-cache-size cannot be negative")
    if args.progress_total_records < 0:
        raise EnrichmentError("--progress-total-records cannot be negative")
    if args.progress_total_records > 0 and args.input_jsonl is None:
        raise EnrichmentError("--progress-total-records requires --input-jsonl")
    if args.progress_total_files < 0:
        raise EnrichmentError("--progress-total-files cannot be negative")
    if args.progress_total_files > 0 and args.input_jsonl is not None:
        raise EnrichmentError("--progress-total-files cannot be used with --input-jsonl")
    if args.triage_only and args.progress_total_files <= 0:
        raise EnrichmentError("--triage-only requires --progress-total-files for measured progress")
    if args.translation_strict_determinism and args.translation_parallelism > 1:
        raise EnrichmentError(
            "--translation-strict-determinism cannot be combined with parallelism above 1"
        )
    if args.translation_min_characters <= 0:
        raise EnrichmentError("--translation-min-characters must be positive")
    if args.translation_max_characters < args.translation_min_characters:
        raise EnrichmentError("Translation maximum characters must not be below the minimum")
    target_parts = args.translation_target.split("-")
    if not (
        2 <= len(args.translation_target) <= 16
        and args.translation_target.isascii()
        and all(part.isalnum() for part in target_parts)
    ):
        raise EnrichmentError("--translation-target must be a 2-16 character ASCII language code")
    if args.translate:
        if args.translation_model_path is None or not args.translation_model_path.exists():
            raise EnrichmentError(
                "--translate requires --translation-model-path pointing to an existing local model"
            )
        engine = selected_translation_engine(args)
        if engine == "madlad" and args.translation_device == "hybrid":
            raise EnrichmentError(
                "--translation-device hybrid is supported by llama.cpp, not MADLAD/Transformers"
            )
        if engine == "madlad" and not args.translation_model_path.is_dir():
            raise EnrichmentError("The MADLAD engine requires a local model snapshot directory")
        if engine == "llama-cpp" and (
            not args.translation_model_path.is_file()
            or args.translation_model_path.suffix.lower() != ".gguf"
        ):
            raise EnrichmentError("The llama.cpp engine requires a local GGUF model file")
        if not args.translation_revision:
            raise EnrichmentError(
                "--translate requires --translation-revision so derived evidence can identify "
                "exact model weights"
            )
        if (
            not args.translation_model_sha256
            or len(args.translation_model_sha256) != 64
            or any(
                character not in "0123456789abcdefABCDEF"
                for character in args.translation_model_sha256
            )
        ):
            raise EnrichmentError(
                "--translate requires the expected 64-character --translation-model-sha256"
            )


def write_jsonl_atomic(
    output_path: Path,
    records: Iterable[dict[str, Any]],
    *,
    before_publish: Callable[[], None] | None = None,
) -> int:
    output_path = output_path.resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_name = tempfile.mkstemp(
        prefix=output_path.name + ".partial.", dir=output_path.parent
    )
    count = 0
    iterator = iter(records)
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as output:
            for record in iterator:
                line = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
                if len(line) > MAX_JSONL_LINE_CHARACTERS:
                    record_id = str(record.get("recordId") or "<unknown>")
                    raise EnrichmentError(
                        f"Enrichment record '{record_id}' exceeds the "
                        f"{MAX_JSONL_LINE_CHARACTERS}-character JSONL safety limit"
                    )
                output.write(line)
                output.write("\n")
                count += 1
            output.flush()
            os.fsync(output.fileno())
        if before_publish is not None:
            before_publish()
        os.replace(temporary_name, output_path)
    except BaseException:
        with suppress(FileNotFoundError):
            os.unlink(temporary_name)
        raise
    finally:
        close = getattr(iterator, "close", None)
        if close is not None:
            close()
    return count


def write_translation_stats_atomic(output_path: Path, stats: TranslationWorkStats) -> None:
    """Atomically publish aggregate counters without retaining translation inputs."""

    output_path = output_path.resolve()
    output_path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary_name = tempfile.mkstemp(
        prefix=output_path.name + ".partial.", dir=output_path.parent
    )
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as output:
            json.dump(
                stats.payload(),
                output,
                ensure_ascii=True,
                separators=(",", ":"),
                sort_keys=True,
            )
            output.write("\n")
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary_name, output_path)
    except BaseException:
        with suppress(FileNotFoundError):
            os.unlink(temporary_name)
        raise


def verify_routing_input_identity(item: RoutingInput) -> None:
    try:
        length = item.path.stat().st_size
    except OSError as exc:
        raise EnrichmentError("A routing input became unavailable") from exc
    digest = sha256_file(item.path)
    if length != item.identity.length or digest != item.identity.sha256:
        raise EnrichmentError("A routing input changed from the immutable evidence manifest")


def run_content_triage(
    args: argparse.Namespace,
    magika: str,
    magika_version: str,
) -> int:
    completed = 0
    magika_sha256 = sha256_file(Path(magika))
    magika_runtime = Path(magika).resolve().with_name("DirectML.dll")
    magika_runtime_path: str | None = None
    magika_runtime_sha256: str | None = None
    if magika_runtime.is_file():
        magika_runtime_path = str(magika_runtime)
        magika_runtime_sha256 = sha256_file(magika_runtime)
    last_progress_percent = -1.0
    last_progress_time = 0.0

    def report_progress(*, force: bool = False) -> None:
        nonlocal last_progress_percent, last_progress_time
        total = args.progress_total_files
        bounded = min(max(completed, 0), total)
        percent = bounded * 100.0 / total
        now = time.monotonic()
        if (
            not force
            and percent < 100.0
            and percent - last_progress_percent < 1.0
            and now - last_progress_time < 5.0
        ):
            return
        print(
            f"Progress: content triage: {percent:.1f}% ({bounded:,}/{total:,} files)",
            file=sys.stderr,
            flush=True,
        )
        last_progress_percent = percent
        last_progress_time = now

    report_progress(force=True)

    def generate_routes() -> Iterable[dict[str, Any]]:
        nonlocal completed
        inputs = iter_routing_inputs(args.paths, args.paths_from, args.input_manifest)
        for batch in _iter_magika_batches(inputs, magika):
            with ExitStack() as leases:
                for item in batch:
                    leases.enter_context(EvidenceReadLease(item.path))
                    verify_routing_input_identity(item)
                try:
                    classifications = _run_magika_batch(magika, batch, args.magika_timeout)
                except EnrichmentError:
                    classifications = _magika_batch_errors(batch, "batch-classification-error")
                for item, classification in zip(batch, classifications, strict=True):
                    completed += 1
                    yield _make_routing_record(
                        item,
                        classification,
                        magika_version,
                        completed,
                        enable_floss=args.enable_floss,
                        enable_ocr=args.enable_ocr,
                        force_floss=args.force_floss,
                        disable_native=args.disable_native,
                        magika_executable=magika,
                        magika_sha256=magika_sha256,
                        magika_runtime_path=magika_runtime_path,
                        magika_runtime_sha256=magika_runtime_sha256,
                    )
                    report_progress()
                if os.name != "nt":
                    for item in batch:
                        verify_routing_input_identity(item)
        if completed != args.progress_total_files:
            raise EnrichmentError(
                "Content triage inventory count changed: expected "
                f"{args.progress_total_files:,} files but processed {completed:,}"
            )

    written = write_jsonl_atomic(args.output, generate_routes())
    report_progress(force=True)
    print(
        f"Wrote {written:,} content-routing records from one classified input inventory.",
        file=sys.stderr,
    )
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_arguments(argv)
    translator: Translator | None = None
    translation_cache: TranslationCache | None = None
    run_translation_cache: RunLocalTranslationCache | None = None
    translation_outcome_cache: TranslationOutcomeCache | None = None
    integrity_monitor = TranslationIntegrityMonitor()
    translation_work_stats = (
        TranslationWorkStats() if args.translation_stats_output is not None else None
    )
    translation_window_size = args.translation_batch_size
    try:
        if args.airgap:
            enable_airgap_mode()
        validate_arguments(args)
        magika = ""
        floss = ""
        magika_version = ""
        floss_version = ""
        if args.triage_only:
            magika = executable_path(args.magika)
            try:
                magika_version = tool_version(magika)
            except EnrichmentError:
                magika_version = "unavailable"
            return run_content_triage(args, magika, magika_version)
        if args.input_jsonl is None:
            floss = executable_path(args.floss)
            floss_version = tool_version(floss)
            if args.routing_manifest is None:
                magika = executable_path(args.magika)
                magika_version = tool_version(magika)
        if args.translate:
            engine = selected_translation_engine(args)
            if engine == "llama-cpp":
                translator = LlamaCppTranslator(
                    server=args.llama_server,
                    model_path=args.translation_model_path.resolve(),
                    model_id=args.translation_model_id or DEFAULT_LLAMA_MODEL_ID,
                    revision=args.translation_revision,
                    expected_model_sha256=args.translation_model_sha256,
                    device=args.translation_device,
                    max_input_tokens=args.translation_max_input_tokens,
                    max_new_tokens=args.translation_max_new_tokens,
                    startup_timeout_seconds=args.translation_startup_timeout,
                    request_timeout_seconds=args.translation_request_timeout,
                    batch_size=args.translation_batch_size,
                    parallelism=args.translation_parallelism,
                    strict_determinism=args.translation_strict_determinism,
                    threads=args.translation_threads,
                    gpu_layers=args.translation_gpu_layers,
                )
            else:
                translator = MadladTranslator(
                    args.translation_model_path.resolve(),
                    args.translation_model_id or DEFAULT_MODEL_ID,
                    args.translation_revision,
                    args.translation_model_sha256,
                    args.translation_device,
                    args.translation_max_input_tokens,
                    args.translation_max_new_tokens,
                    args.translation_threads,
                )
            translation_window_size = args.translation_window_size or (
                args.translation_batch_size * max(4, translator.parallelism)
            )
            if args.input_jsonl is not None and args.bounded_integrated_mode:
                identity = translation_cache_identity(
                    translator,
                    args.translation_target,
                    batch_size=args.translation_batch_size,
                    max_input_tokens=args.translation_max_input_tokens,
                    max_new_tokens=args.translation_max_new_tokens,
                    strict_determinism=args.translation_strict_determinism,
                )
                run_translation_cache = RunLocalTranslationCache(
                    args.output.resolve().parent,
                    identity,
                    args.translation_cache_size,
                )
                translation_outcome_cache = run_translation_cache
            else:
                translation_outcome_cache = BoundedTranslationOutcomeCache(
                    args.translation_cache_size
                )
            print(
                "Translation plan: "
                + json.dumps(
                    {
                        **translator.execution_metadata,
                        "batchSize": args.translation_batch_size,
                        "windowSize": translation_window_size,
                        "cacheSize": args.translation_cache_size,
                        "cache": {
                            "diskBacked": run_translation_cache is not None,
                            "examinationLocal": run_translation_cache is not None,
                            "memoryCapacity": args.translation_cache_size,
                            "commitBatchSize": (
                                TRANSLATION_CACHE_COMMIT_BATCH_SIZE
                                if run_translation_cache is not None
                                else 0
                            ),
                        },
                    },
                    separators=(",", ":"),
                    sort_keys=True,
                ),
                file=sys.stderr,
            )

        processed = 0
        skipped = 0
        translated = 0
        recovery_completed = 0
        last_progress_percent = -1.0
        last_progress_time = 0.0
        translation_progress_started = time.monotonic()
        final_cache_hits = 0
        final_cache_stores = 0
        final_cache_commits = 0

        def duration_text(seconds: float) -> str:
            if not math.isfinite(seconds) or seconds < 0:
                return "unknown"
            rounded = int(math.ceil(seconds))
            hours, remainder = divmod(rounded, 3600)
            minutes, remaining_seconds = divmod(remainder, 60)
            if hours:
                return f"{hours:d}h{minutes:02d}m{remaining_seconds:02d}s"
            if minutes:
                return f"{minutes:d}m{remaining_seconds:02d}s"
            return f"{remaining_seconds:d}s"

        def report_translation_progress(completed: int, *, force: bool = False) -> None:
            nonlocal last_progress_percent, last_progress_time
            total = args.progress_total_records
            if total <= 0:
                return
            bounded = min(max(completed, 0), total)
            percent = bounded * 100.0 / total
            now = time.monotonic()
            if (
                not force
                and percent < 100.0
                and percent - last_progress_percent < 1.0
                and now - last_progress_time < 5.0
            ):
                return
            elapsed = max(0.0, now - translation_progress_started)
            rate = bounded / elapsed if bounded > 0 and elapsed > 0 else 0.0
            eta = (total - bounded) / rate if rate > 0 else math.inf
            cache_hits = (
                translation_outcome_cache.hits
                if translation_outcome_cache is not None
                else final_cache_hits
            )
            print(
                f"Progress: offline translation: {percent:.1f}% "
                f"({bounded:,}/{total:,} records; rate={rate:.2f}/s; "
                f"eta={duration_text(eta)}; cacheHits={cache_hits:,}; "
                f"modelInputs={integrity_monitor.model_inputs:,}; "
                f"fallbacks={integrity_monitor.fallbacks:,})",
                file=sys.stderr,
                flush=True,
            )
            last_progress_percent = percent
            last_progress_time = now

        report_translation_progress(0, force=True)

        def report_recovery_progress(completed: int, *, force: bool = False) -> None:
            nonlocal last_progress_percent, last_progress_time
            total = args.progress_total_files
            if total <= 0:
                return
            bounded = min(max(completed, 0), total)
            percent = bounded * 100.0 / total
            now = time.monotonic()
            if (
                not force
                and percent < 100.0
                and percent - last_progress_percent < 1.0
                and now - last_progress_time < 5.0
            ):
                return
            progress_name = (
                "FLOSS recovery"
                if args.routing_manifest is not None
                else "Magika and FLOSS recovery"
            )
            print(
                "Progress: " + progress_name + f": {percent:.1f}% ({bounded:,}/{total:,} files)",
                file=sys.stderr,
                flush=True,
            )
            last_progress_percent = percent
            last_progress_time = now

        report_recovery_progress(0, force=True)

        def generate_records() -> Iterable[dict[str, Any]]:
            nonlocal processed, skipped, recovery_completed
            if args.input_jsonl is not None:
                assert translator is not None
                yield from translate_normalized_records(
                    read_normalized_jsonl(args.input_jsonl),
                    translator,
                    args.translation_target,
                    args.translation_batch_size,
                    args.translation_min_characters,
                    args.translation_max_characters,
                    translation_window_size,
                    translation_cache,
                    include_parents=not args.translations_only,
                    progress=report_translation_progress,
                    run_cache=translation_outcome_cache,
                    integrity_monitor=integrity_monitor,
                    work_stats=translation_work_stats,
                )
                return

            def recovery_inputs() -> Iterable[
                tuple[Path, Classification, InputIdentity | None, str | None]
            ]:
                nonlocal skipped, recovery_completed
                if args.routing_manifest is not None:
                    yield from iter_routed_recovery_inputs(
                        args.paths, args.paths_from, args.routing_manifest
                    )
                    return
                for raw_path in iter_extraction_paths(args.paths, args.paths_from):
                    path = raw_path.resolve()
                    if not path.is_file():
                        raise EnrichmentError(f"Input file was not found: {raw_path}")
                    classification = classify_file(magika, path, args.magika_timeout)
                    print(
                        f"Magika {magika_version}: {path} -> {classification.label} "
                        f"({classification.score:.3f})",
                        file=sys.stderr,
                    )
                    if classification.label != "pebin" and not args.force_floss:
                        print(
                            "Skipping FLOSS for non-PE Magika label "
                            f"'{classification.label}': {path}",
                            file=sys.stderr,
                        )
                        skipped += 1
                        recovery_completed += 1
                        report_recovery_progress(recovery_completed)
                        continue
                    yield path, classification, None, None

            for path, classification, routed_identity, route_decision_id in recovery_inputs():
                payload = run_floss(
                    floss,
                    path,
                    args.minimum_length,
                    args.floss_timeout,
                    args.floss_format,
                )
                records: Iterable[dict[str, Any]] = iter_normalized_floss(
                    payload,
                    path,
                    classification,
                    floss_version,
                    include_static=args.include_floss_static,
                    route_decision_id=route_decision_id,
                )
                if args.bounded_integrated_mode:
                    records = iter_unique_records(records)
                if translator is None:
                    yield from records
                    if routed_identity is not None:
                        verify_routed_source_identity(path, routed_identity)
                    processed += 1
                    recovery_completed += 1
                    report_recovery_progress(recovery_completed)
                    continue

                # Combined recovery/translation retains the historical parent-first
                # ordering. It needs one replayable list, but no second normalized or
                # deduplicated copy. The usual integrated recovery-only path stays
                # record-streaming above.
                replayable_records = list(records)
                del payload
                if routed_identity is not None:
                    verify_routed_source_identity(path, routed_identity)
                yield from replayable_records
                if translator is not None:
                    translated_records = add_translations(
                        replayable_records,
                        translator,
                        args.translation_target,
                        args.translation_batch_size,
                        args.translation_min_characters,
                        args.translation_max_characters,
                        cache=translation_cache,
                        run_cache=translation_outcome_cache,
                        integrity_monitor=integrity_monitor,
                        work_stats=translation_work_stats,
                    )
                    yield from translated_records
                processed += 1
                recovery_completed += 1
                report_recovery_progress(recovery_completed)
            if args.progress_total_files > 0 and recovery_completed != args.progress_total_files:
                raise EnrichmentError(
                    "Recovery inventory count changed: expected "
                    f"{args.progress_total_files:,} files but processed {recovery_completed:,}"
                )

        def count_unique_records() -> Iterable[dict[str, Any]]:
            nonlocal translated
            generated = generate_records()
            published = (
                generated if args.bounded_integrated_mode else iter_unique_records(generated)
            )
            for record in published:
                if (record.get("transform") or {}).get("kind") == "translation":
                    translated += 1
                    if translation_work_stats is not None:
                        translation_work_stats.translated_child_occurrences += 1
                        if (record.get("attributes") or {}).get(
                            "translationIntegrity"
                        ) == "preservation-fallback":
                            translation_work_stats.preservation_fallback_child_occurrences += 1
                yield record

        def finalize_translation_cache_before_publish() -> None:
            nonlocal final_cache_commits, final_cache_hits, final_cache_stores
            nonlocal run_translation_cache, translation_outcome_cache
            if args.input_jsonl is None:
                return
            if translation_outcome_cache is not None:
                final_cache_hits = translation_outcome_cache.hits
                final_cache_stores = translation_outcome_cache.stores
                final_cache_commits = translation_outcome_cache.batch_commits
            if run_translation_cache is not None:
                finalize_run_translation_cache(run_translation_cache, args.output.resolve())
                run_translation_cache = None
                translation_outcome_cache = None

        written = write_jsonl_atomic(
            args.output,
            count_unique_records(),
            before_publish=finalize_translation_cache_before_publish,
        )
        if args.translation_stats_output is not None:
            assert translation_work_stats is not None
            write_translation_stats_atomic(args.translation_stats_output, translation_work_stats)
        if args.input_jsonl is not None:
            if args.progress_total_records > 0:
                report_translation_progress(args.progress_total_records, force=True)
            print(
                "Translation summary: "
                f"cacheHits={final_cache_hits:,}; cacheStores={final_cache_stores:,}; "
                f"cacheBatchCommits={final_cache_commits:,}; "
                f"modelInputs={integrity_monitor.model_inputs:,}; "
                f"fallbacks={integrity_monitor.fallbacks:,}",
                file=sys.stderr,
                flush=True,
            )
        if args.input_jsonl is not None:
            print(
                f"Wrote {written} enrichment strings ({translated} new translations) "
                f"from normalized JSONL.",
                file=sys.stderr,
            )
        else:
            print(
                f"Wrote {written} enrichment strings ({translated} translations) from "
                f"{processed} files; {skipped} files were routed away from FLOSS.",
                file=sys.stderr,
            )
        return 0
    except EnrichmentError as exc:
        print(f"enrichment failed: {exc}", file=sys.stderr)
        return 2
    except (KeyError, OSError, RuntimeError, TypeError, ValueError) as exc:
        print(f"enrichment failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2
    finally:
        if run_translation_cache is not None:
            try:
                run_translation_cache.close(commit=False, strict=True)
            except EnrichmentError as cleanup_error:
                print(f"enrichment cache cleanup failed: {cleanup_error}", file=sys.stderr)
        close = getattr(translator, "close", None)
        if close is not None:
            close()


if __name__ == "__main__":
    raise SystemExit(main())
