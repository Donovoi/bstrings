#!/usr/bin/env python3
"""Route files with Magika, recover executable strings with FLOSS, and emit bstrings JSONL."""

from __future__ import annotations

import argparse
import atexit
import hashlib
import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from collections.abc import Iterable, Sequence
from contextlib import suppress
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

SCHEMA_VERSION = 1
DEFAULT_MODEL_ID = "google/madlad400-3b-mt"
DEFAULT_LLAMA_MODEL_ID = "tencent/Hy-MT2-1.8B-GGUF"
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
FLOSS_CATEGORIES = (
    "language_strings",
    "stack_strings",
    "tight_strings",
    "decoded_strings",
)


class EnrichmentError(RuntimeError):
    """Raised when an enrichment stage cannot produce complete, attributable output."""


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

    def translate(self, texts: Sequence[str], target_language: str) -> list[str]: ...


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
) -> dict[str, Any]:
    command = [floss, "-j", "-n", str(minimum_length)]
    if input_format != "auto":
        command.extend(["--format", input_format])
    command.extend(["--only", "static", "stack", "tight", "decoded", "--", str(path)])
    result = run_checked(command, timeout_seconds)
    try:
        payload = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise EnrichmentError(f"FLOSS returned invalid JSON for '{path}': {exc}") from exc
    if not isinstance(payload, dict) or not isinstance(payload.get("strings"), dict):
        raise EnrichmentError(f"FLOSS JSON for '{path}' has no strings object")
    return payload


def common_attributes(
    classification: Classification,
    floss_payload: dict[str, Any],
) -> dict[str, Any]:
    metadata = floss_payload.get("metadata") or {}
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


def normalize_floss(
    payload: dict[str, Any],
    source_path: Path,
    classification: Classification,
    floss_version: str,
    include_static: bool = False,
) -> list[dict[str, Any]]:
    strings = payload["strings"]
    source_file = str(source_path.resolve())
    shared = common_attributes(classification, payload)
    records: list[dict[str, Any]] = []

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
        for item in strings.get(category, []):
            records.append(
                make_string_record(
                    text=str(item["string"]),
                    source_file=source_file,
                    extractor_version=floss_version,
                    kind=kind,
                    location_kind="file_offset",
                    location_value=item["offset"],
                    attributes={**shared, "encoding": item.get("encoding")},
                )
            )

    for category, kind in (("stack_strings", "stack"), ("tight_strings", "tight")):
        for item in strings.get(category, []):
            records.append(
                make_string_record(
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
            )

    for item in strings.get("decoded_strings", []):
        records.append(
            make_string_record(
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
        )

    return records


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
) -> list[dict[str, Any]]:
    candidates = [
        record
        for record in records
        if should_translate(str(record["text"]), minimum_characters, maximum_characters)
    ]
    translated_records: list[dict[str, Any]] = []
    for start in range(0, len(candidates), batch_size):
        batch = candidates[start : start + batch_size]
        translated = translator.translate(
            [str(record["text"]) for record in batch], target_language
        )
        if len(translated) != len(batch):
            raise EnrichmentError(
                f"Translation engine returned {len(translated)} rows for a batch of {len(batch)}"
            )
        for parent, translated_text in zip(batch, translated, strict=True):
            translated_text = translated_text.strip()
            if not translated_text or translated_text == parent["text"]:
                continue
            derived = {
                key: value
                for key, value in parent.items()
                if key not in {"recordId", "text", "parentRecordId", "transform"}
            }
            derived.update(
                {
                    "text": translated_text,
                    "parentRecordId": parent["recordId"],
                    "transform": {
                        "kind": "translation",
                        "engine": translator.engine,
                        "engineVersion": translator.engine_version,
                        "model": translator.model_id,
                        "revision": translator.revision,
                        "modelSha256": translator.model_sha256,
                        "sourceLanguage": "auto",
                        "targetLanguage": target_language,
                    },
                }
            )
            translated_records.append(with_record_id(derived))
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


def translate_normalized_records(
    records: Iterable[dict[str, Any]],
    translator: Translator,
    target_language: str,
    batch_size: int,
    minimum_characters: int,
    maximum_characters: int,
) -> Iterable[dict[str, Any]]:
    pending: list[dict[str, Any]] = []

    def flush_pending() -> Iterable[dict[str, Any]]:
        translated = add_translations(
            pending,
            translator,
            target_language,
            batch_size,
            minimum_characters,
            maximum_characters,
        )
        pending.clear()
        return translated

    for record in records:
        yield record
        if record.get("transform") is None and should_translate(
            str(record["text"]), minimum_characters, maximum_characters
        ):
            pending.append(record)
            if len(pending) >= batch_size:
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
        self.engine_version = f"transformers={transformers.__version__};torch={torch.__version__}"

        self._torch = torch
        self.model_id = model_id
        self.revision = revision
        self.model_sha256 = actual_model_sha256
        self._max_input_tokens = max_input_tokens
        self._max_new_tokens = max_new_tokens
        self._device = self._choose_device(device)
        self._tokenizer = AutoTokenizer.from_pretrained(str(model_path), local_files_only=True)
        self._model = AutoModelForSeq2SeqLM.from_pretrained(
            str(model_path), local_files_only=True, dtype="auto"
        )
        self._model.to(self._device)
        self._model.eval()

    def _choose_device(self, requested: str) -> str:
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
            truncation=True,
            max_length=self._max_input_tokens,
        ).to(self._device)
        with self._torch.inference_mode():
            generated = self._model.generate(
                **encoded,
                max_new_tokens=self._max_new_tokens,
            )
        return self._tokenizer.batch_decode(generated, skip_special_tokens=True)


def llama_translation_prompt(text: str, target_language: str) -> str:
    language_name = LANGUAGE_NAMES.get(target_language.lower(), target_language)
    return (
        f"Translate the following text into {language_name}. Only output the translated result "
        "without explanation. Preserve every email address, username, IP address, URL, file "
        "path, file name, hash, CVE, registry path, host name, port, GUID, placeholder, and "
        "delimiter exactly as written:\n" + text
    )


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
        actual_device = "cuda" if device != "cpu" and cuda_available else "cpu"
        self.engine_version = f"{version}; device={actual_device}"
        self.model_id = model_id
        self.revision = revision
        self.model_sha256 = actual_model_sha256
        self._max_new_tokens = max_new_tokens
        self._request_timeout_seconds = request_timeout_seconds
        self._url_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        self._closed = False
        self._temporary_directory = tempfile.TemporaryDirectory(prefix="bstrings-llama-")
        temporary_path = Path(self._temporary_directory.name)
        self._stdout_path = temporary_path / "stdout.log"
        self._stderr_path = temporary_path / "stderr.log"
        self._stdout_handle = self._stdout_path.open("w", encoding="utf-8")
        self._stderr_handle = self._stderr_path.open("w", encoding="utf-8")
        self._port = self._available_loopback_port()
        context_size = max(2048, max_input_tokens + max_new_tokens + 512)
        gpu_layers = "all" if actual_device == "cuda" else "0"
        command = [
            self._server,
            "-m",
            str(model_path.resolve()),
            "-ngl",
            gpu_layers,
            "--device",
            cuda_device if actual_device == "cuda" else "none",
            "-c",
            str(context_size),
            "-np",
            "1",
            "--host",
            "127.0.0.1",
            "--port",
            str(self._port),
            "--jinja",
            "--reasoning",
            "off",
            "--no-warmup",
            "--no-webui",
            "--flash-attn",
            "on",
        ]
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
                with self._url_opener.open(health_url, timeout=2) as response:
                    payload = json.load(response)
                if payload.get("status") == "ok":
                    return
            except (OSError, TimeoutError, urllib.error.URLError, json.JSONDecodeError):
                pass
            time.sleep(0.2)
        raise EnrichmentError(f"llama.cpp did not become healthy within {timeout_seconds} seconds")

    def translate(self, texts: Sequence[str], target_language: str) -> list[str]:
        return [self._translate_one(text, target_language) for text in texts]

    def _translate_one(self, text: str, target_language: str) -> str:
        payload = json.dumps(
            {
                "messages": [
                    {
                        "role": "user",
                        "content": llama_translation_prompt(text, target_language),
                    }
                ],
                "temperature": 0,
                "top_p": 1,
                "top_k": 1,
                "seed": 1,
                "max_tokens": self._max_new_tokens,
                "stream": False,
            }
        ).encode("utf-8")
        request = urllib.request.Request(
            f"http://127.0.0.1:{self._port}/v1/chat/completions",
            data=payload,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with self._url_opener.open(request, timeout=self._request_timeout_seconds) as response:
                body = json.load(response)
            content = body["choices"][0]["message"]["content"]
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
        "--input-jsonl",
        type=Path,
        help=(
            "Translate an existing normalized string JSONL stream instead of invoking Magika/FLOSS"
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
    parser.add_argument("--translation-device", choices=("auto", "cpu", "cuda"), default="auto")
    parser.add_argument("--llama-server", default="llama-server")
    parser.add_argument("--translation-startup-timeout", type=int, default=120)
    parser.add_argument("--translation-request-timeout", type=int, default=120)
    parser.add_argument("--translation-batch-size", type=int, default=8)
    parser.add_argument("--translation-target", default="en")
    parser.add_argument("--translation-min-characters", type=int, default=8)
    parser.add_argument("--translation-max-characters", type=int, default=2048)
    parser.add_argument("--translation-max-input-tokens", type=int, default=512)
    parser.add_argument("--translation-max-new-tokens", type=int, default=512)
    return parser.parse_args(argv)


def selected_translation_engine(args: argparse.Namespace) -> str:
    if args.translation_engine != "auto":
        return str(args.translation_engine)
    model_path = args.translation_model_path
    if model_path is not None and model_path.is_file() and model_path.suffix.lower() == ".gguf":
        return "llama-cpp"
    return "madlad"


def validate_arguments(args: argparse.Namespace) -> None:
    if bool(args.paths) == bool(args.input_jsonl):
        raise EnrichmentError("Supply carved file paths or --input-jsonl, but not both")
    if args.input_jsonl is not None:
        if not args.input_jsonl.is_file():
            raise EnrichmentError(f"Normalized JSONL input was not found: {args.input_jsonl}")
        if args.input_jsonl.resolve() == args.output.resolve():
            raise EnrichmentError("--input-jsonl and --output must be different paths")
        if not args.translate:
            raise EnrichmentError("--input-jsonl requires --translate")
    if args.minimum_length < 3:
        raise EnrichmentError("--minimum-length must be at least 3")
    if args.magika_timeout <= 0 or args.floss_timeout <= 0:
        raise EnrichmentError("Tool timeouts must be positive")
    if args.translation_startup_timeout <= 0 or args.translation_request_timeout <= 0:
        raise EnrichmentError("Translation timeouts must be positive")
    if args.translation_batch_size <= 0:
        raise EnrichmentError("--translation-batch-size must be positive")
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
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as output:
            for record in records:
                output.write(json.dumps(record, ensure_ascii=False, separators=(",", ":")))
                output.write("\n")
                count += 1
            output.flush()
            os.fsync(output.fileno())
        os.replace(temporary_name, output_path)
    except BaseException:
        with suppress(FileNotFoundError):
            os.unlink(temporary_name)
        raise
    return count


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_arguments(argv)
    translator: Translator | None = None
    try:
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
                    args.llama_server,
                    args.translation_model_path.resolve(),
                    args.translation_model_id or DEFAULT_LLAMA_MODEL_ID,
                    args.translation_revision,
                    args.translation_model_sha256,
                    args.translation_device,
                    args.translation_max_input_tokens,
                    args.translation_max_new_tokens,
                    args.translation_startup_timeout,
                    args.translation_request_timeout,
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
                )

        processed = 0
        skipped = 0
        translated = 0

        def generate_records() -> Iterable[dict[str, Any]]:
            nonlocal processed, skipped
            if args.input_jsonl is not None:
                assert translator is not None
                yield from translate_normalized_records(
                    read_normalized_jsonl(args.input_jsonl),
                    translator,
                    args.translation_target,
                    args.translation_batch_size,
                    args.translation_min_characters,
                    args.translation_max_characters,
                )
                return
            for raw_path in args.paths:
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
                    continue
                payload = run_floss(
                    floss,
                    path,
                    args.minimum_length,
                    args.floss_timeout,
                    args.floss_format,
                )
                records = normalize_floss(
                    payload,
                    path,
                    classification,
                    floss_version,
                    include_static=args.include_floss_static,
                )
                yield from records
                if translator is not None:
                    translated_records = add_translations(
                        records,
                        translator,
                        args.translation_target,
                        args.translation_batch_size,
                        args.translation_min_characters,
                        args.translation_max_characters,
                    )
                    yield from translated_records
                processed += 1

        def count_unique_records() -> Iterable[dict[str, Any]]:
            nonlocal translated
            for record in iter_unique_records(generate_records()):
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
