#!/usr/bin/env python3
"""Route files with Magika, recover executable strings with FLOSS, and emit bstrings JSONL."""

from __future__ import annotations

import argparse
import atexit
import codecs
import hashlib
import ipaddress
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from collections import Counter, OrderedDict
from collections.abc import Callable, Iterable, Sequence
from concurrent.futures import ThreadPoolExecutor
from contextlib import suppress
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol, TypeVar

SCHEMA_VERSION = 1
MAX_PATH_LIST_LINE_CHARACTERS = 32 * 1024
MAX_JSONL_LINE_CHARACTERS = 16 * 1024 * 1024
MAX_FLOSS_JSON_ITEM_BYTES = MAX_JSONL_LINE_CHARACTERS
MAX_JSON_DEPTH = 256
JSON_READ_CHUNK_BYTES = 64 * 1024
JSON_STRING_SPECIAL = re.compile(rb'[\x00-\x1f"\\]')
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
PROTECTED_IDENTIFIER_PATTERNS = tuple(
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
        r"\b[A-Z0-9](?:[A-Z0-9.-]*[A-Z0-9])?:[0-9]{1,5}\b",
        r"\b[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?(?:\.[A-Z0-9](?:[A-Z0-9-]{0,61}[A-Z0-9])?)+\b",
        r"\b[A-Z0-9_-]+\.(?:7Z|BAT|BIN|CAB|CFG|CONF|CSV|DLL|DOCX?|EXE|INI|JAR|JSONL?|LOG|PDF|PNG|PS1|RAR|RAW|SYS|TXT|XLSX?|XML|ZIP)\b",
        r"(?<![A-Z0-9])[A-Z0-9]+(?:[-_][A-Z0-9]+)+(?![A-Z0-9])",
        r"\$\{[A-Z0-9_.-]+\}|\{[A-Z0-9_.-]+\}|%[A-Z0-9_]+%",
    )
)
_TRAILING_IDENTIFIER_PUNCTUATION = ".,;:!?)]}\u3002\uff0c\uff1b\uff1a\uff01\uff1f"
T = TypeVar("T")
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


def map_ordered_parallel(
    function: Callable[[T], str], values: Sequence[T], workers: int
) -> list[str]:
    """Run independent calls concurrently while retaining input order and fatal errors."""
    if workers <= 1 or len(values) <= 1:
        return [function(value) for value in values]
    with ThreadPoolExecutor(max_workers=min(workers, len(values))) as executor:
        return list(executor.map(function, values))


def protected_identifier_spans(text: str) -> tuple[tuple[int, int, str], ...]:
    """Return non-overlapping structured-token spans that translation must preserve."""
    matches: list[tuple[int, int, str]] = []
    for pattern in PROTECTED_IDENTIFIER_PATTERNS:
        for match in pattern.finditer(text):
            value = match.group(0).rstrip(_TRAILING_IDENTIFIER_PUNCTUATION)
            if value:
                matches.append((match.start(), match.start() + len(value), value))
    matches.sort(key=lambda item: (item[0], -len(item[2])))
    accepted: list[tuple[int, int, str]] = []
    accepted_ranges: list[tuple[int, int]] = []
    for start, end, value in matches:
        if any(
            parent_start <= start and end <= parent_end
            for parent_start, parent_end in accepted_ranges
        ):
            continue
        accepted.append((start, end, value))
        accepted_ranges.append((start, end))
    return tuple(accepted)


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
    missing = [
        value
        for value, required in source_counts.items()
        if translated_counts[value] < required
    ]
    if missing:
        preview = ", ".join(repr(value) for value in missing[:3])
        raise EnrichmentError(
            "Translation changed or removed protected evidence identifiers: " + preview
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
) -> list[dict[str, Any]]:
    candidates = [
        record
        for record in records
        if should_translate(str(record["text"]), minimum_characters, maximum_characters)
    ]
    translations_by_text: dict[str, str] = {}
    missing_texts: list[str] = []
    missing_text_set: set[str] = set()
    for record in candidates:
        text = str(record["text"])
        if text in translations_by_text or text in missing_text_set:
            continue
        if contains_only_protected_identifiers(text):
            translations_by_text[text] = text
            if cache:
                cache.put(target_language, text, text)
            continue
        found, translated_text = cache.get(target_language, text) if cache else (False, "")
        if found:
            translations_by_text[text] = translated_text
        else:
            missing_texts.append(text)
            missing_text_set.add(text)

    # Group similar lengths to reduce padding for batched encoder-decoder runtimes.
    missing_texts.sort(key=lambda value: (len(value), value))
    for start in range(0, len(missing_texts), batch_size):
        batch = missing_texts[start : start + batch_size]
        translated = translator.translate(batch, target_language)
        if len(translated) != len(batch):
            raise EnrichmentError(
                f"Translation engine returned {len(translated)} rows for a batch of {len(batch)}"
            )
        for source_text, translated_text in zip(batch, translated, strict=True):
            translated_text = translated_text.strip()
            if not translated_text:
                raise EnrichmentError("Translation engine returned an empty translation")
            validate_identifier_retention(source_text, translated_text)
            translations_by_text[source_text] = translated_text
            if cache:
                cache.put(target_language, source_text, translated_text)

    translated_records: list[dict[str, Any]] = []
    execution_metadata = getattr(translator, "execution_metadata", None)
    for parent in candidates:
        source_text = str(parent["text"])
        translated_text = translations_by_text[source_text].strip()
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
        derived.update(
            {
                "text": translated_text,
                "parentRecordId": parent["recordId"],
                "transform": transform,
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
        if device == "cpu" or not cuda_available:
            actual_device = "cpu"
            selected_gpu_layers = "0"
            server_device = "none"
        elif device == "cuda":
            actual_device = "cuda"
            selected_gpu_layers = "all"
            server_device = str(cuda_device)
        else:
            selected_gpu_layers = str(gpu_layers) if gpu_layers >= 0 else "auto"
            actual_device = (
                "hybrid-cuda-cpu" if selected_gpu_layers != "auto" else "adaptive-cuda-offload"
            )
            server_device = str(cuda_device)

        self.parallelism = resolve_translation_parallelism(
            parallelism,
            strict_determinism=strict_determinism,
            has_cuda=cuda_available and device != "cpu",
            model_size_bytes=model_path.stat().st_size,
            batch_size=batch_size,
        )
        self._strict_determinism = strict_determinism
        self.execution_metadata = {
            "device": actual_device,
            "airgap": airgap_mode_enabled(),
            "gpuLayers": selected_gpu_layers,
            "parallelism": self.parallelism,
            "continuousBatching": True,
            "promptCache": not strict_determinism,
            "decoding": "greedy-top1",
            "threads": threads if threads > 0 else "runtime-auto",
        }
        self.engine_version = (
            f"{version};device={actual_device};parallelism={self.parallelism};"
            f"gpu-layers={selected_gpu_layers};"
            f"prompt-cache={'off' if strict_determinism else 'on'}"
        )
        self.model_id = model_id
        self.revision = revision
        self.model_sha256 = actual_model_sha256
        self._max_input_tokens = max_input_tokens
        self._max_new_tokens = max_new_tokens
        self._request_timeout_seconds = request_timeout_seconds
        self._health_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self._thread_state = threading.local()
        self._closed = False
        self._temporary_directory = tempfile.TemporaryDirectory(prefix="bstrings-llama-")
        temporary_path = Path(self._temporary_directory.name)
        self._stdout_path = temporary_path / "stdout.log"
        self._stderr_path = temporary_path / "stderr.log"
        self._stdout_handle = self._stdout_path.open("w", encoding="utf-8")
        self._stderr_handle = self._stderr_path.open("w", encoding="utf-8")
        self._port = self._available_loopback_port()
        per_slot_context = max(2048, max_input_tokens + max_new_tokens + 512)
        context_size = per_slot_context * self.parallelism
        command = build_llama_server_command(
            self._server,
            model_path,
            gpu_layers=selected_gpu_layers,
            device=server_device,
            context_size=context_size,
            parallelism=self.parallelism,
            port=self._port,
            threads=threads,
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
        except BaseException:
            self.close()
            raise
        atexit.register(self.close)

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
        return map_ordered_parallel(
            lambda text: self._translate_one(text, target_language),
            texts,
            self.parallelism,
        )

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
            raise EnrichmentError(f"llama.cpp request to {path} failed: {exc}") from exc
        if not isinstance(body, dict):
            raise EnrichmentError(f"llama.cpp request to {path} returned a non-object response")
        return body

    def _validated_prompt_token_count(self, messages: list[dict[str, str]]) -> int:
        template_body = self._post_json("/apply-template", {"messages": messages})
        prompt = template_body.get("prompt")
        if not isinstance(prompt, str) or not prompt:
            raise EnrichmentError("llama.cpp /apply-template returned an empty prompt")
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
            raise EnrichmentError("llama.cpp /tokenize returned invalid token IDs")
        token_count = len(tokens)
        if token_count == 0:
            raise EnrichmentError("llama.cpp /tokenize returned no prompt tokens")
        if token_count > self._max_input_tokens:
            raise EnrichmentError(
                f"llama.cpp prompt requires {token_count} tokens, exceeding the "
                f"{self._max_input_tokens}-token input safety limit; source was not truncated"
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
                raise EnrichmentError(
                    "llama.cpp translation response must contain exactly one choice"
                )
            choice = choices[0]
            if not isinstance(choice, dict):
                raise EnrichmentError("llama.cpp translation choice is not an object")
            finish_reason = choice.get("finish_reason")
            if finish_reason != "stop":
                raise EnrichmentError(
                    "llama.cpp returned a non-terminal translation finish_reason: "
                    f"{finish_reason!r}"
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
            raise EnrichmentError(f"llama.cpp translation request failed: {exc}") from exc
        usage = body.get("usage")
        observed_prompt_tokens = usage.get("prompt_tokens") if isinstance(usage, dict) else None
        if (
            not isinstance(observed_prompt_tokens, int)
            or isinstance(observed_prompt_tokens, bool)
            or observed_prompt_tokens != expected_prompt_tokens
        ):
            raise EnrichmentError(
                "llama.cpp could not prove complete prompt acceptance: "
                f"expected {expected_prompt_tokens} prompt tokens, response reported "
                f"{observed_prompt_tokens!r}"
            )
        if not isinstance(content, str) or not content.strip():
            raise EnrichmentError("llama.cpp returned an empty translation")
        return content.strip()

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        process = getattr(self, "_process", None)
        if process is not None and process.poll() is None:
            process.terminate()
            try:
                process.wait(timeout=20)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=10)
        for handle_name in ("_stdout_handle", "_stderr_handle"):
            handle = getattr(self, handle_name, None)
            if handle is not None:
                handle.close()
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
            "Trust unique parent IDs supplied by bstrings.exe and use only bounded per-file "
            "deduplication; intended for the integrated large-dataset workflow"
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


def write_jsonl_atomic(output_path: Path, records: Iterable[dict[str, Any]]) -> int:
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


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_arguments(argv)
    translator: Translator | None = None
    translation_cache: TranslationCache | None = None
    translation_window_size = args.translation_batch_size
    try:
        if args.airgap:
            enable_airgap_mode()
        validate_arguments(args)
        magika = ""
        floss = ""
        magika_version = ""
        floss_version = ""
        if args.input_jsonl is None:
            magika = executable_path(args.magika)
            floss = executable_path(args.floss)
            magika_version = tool_version(magika)
            floss_version = tool_version(floss)
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
            translation_cache = TranslationCache(args.translation_cache_size)
            translation_window_size = args.translation_window_size or (
                args.translation_batch_size * max(4, translator.parallelism)
            )
            print(
                "Translation plan: "
                + json.dumps(
                    {
                        **translator.execution_metadata,
                        "batchSize": args.translation_batch_size,
                        "windowSize": translation_window_size,
                        "cacheSize": args.translation_cache_size,
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
            print(
                f"Progress: offline translation: {percent:.1f}% "
                f"({bounded:,}/{total:,} records)",
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
            print(
                f"Progress: Magika and FLOSS recovery: {percent:.1f}% "
                f"({bounded:,}/{total:,} files)",
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
                        f"Skipping FLOSS for non-PE Magika label '{classification.label}': {path}",
                        file=sys.stderr,
                    )
                    skipped += 1
                    recovery_completed += 1
                    report_recovery_progress(recovery_completed)
                    continue
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
                )
                if args.bounded_integrated_mode:
                    records = iter_unique_records(records)
                if translator is None:
                    yield from records
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
                yield from replayable_records
                if translator is not None:
                    translated_records = add_translations(
                        replayable_records,
                        translator,
                        args.translation_target,
                        args.translation_batch_size,
                        args.translation_min_characters,
                        args.translation_max_characters,
                        translation_cache,
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
                yield record

        written = write_jsonl_atomic(args.output, count_unique_records())
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
        close = getattr(translator, "close", None)
        if close is not None:
            close()


if __name__ == "__main__":
    raise SystemExit(main())
