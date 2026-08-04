from __future__ import annotations

import json
import subprocess
import sys
import tempfile
import threading
import unittest
from argparse import Namespace
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bstrings_enrich import (  # noqa: E402
    Classification,
    EnrichmentError,
    LlamaCppTranslator,
    MadladTranslator,
    TranslationCache,
    add_translations,
    build_llama_server_command,
    llama_translation_prompt,
    normalize_floss,
    protected_identifiers,
    read_normalized_jsonl,
    resolve_translation_parallelism,
    run_floss,
    selected_translation_engine,
    translate_normalized_records,
    unique_records,
    validate_transformers_version,
    write_jsonl_atomic,
)


class FakeTranslator:
    engine = "fake-offline"
    engine_version = "1.0"
    model_id = "google/test-model"
    revision = "deadbeef"
    model_sha256 = "a" * 64
    parallelism = 1
    execution_metadata = {
        "device": "cpu",
        "parallelism": 1,
        "decoding": "greedy",
    }

    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    def translate(self, texts: list[str], target_language: str) -> list[str]:
        self.target_language = target_language
        self.calls.append(list(texts))
        return [f"translated {text}" for text in texts]


class EnrichmentTests(unittest.TestCase):
    def setUp(self) -> None:
        self.classification = Classification(
            "pebin", 0.99, False, "application/x-dosexec", "executable"
        )
        self.payload = {
            "metadata": {"language": "", "imagebase": 0x400000},
            "strings": {
                "static_strings": [
                    {"string": "static@example.com", "offset": 12, "encoding": "ASCII"}
                ],
                "language_strings": [
                    {"string": "language text", "offset": 24, "encoding": "UTF-8"}
                ],
                "language_strings_missed": [],
                "stack_strings": [
                    {
                        "function": 0x401000,
                        "string": "stack text",
                        "encoding": "ASCII",
                        "program_counter": 0x401005,
                        "stack_pointer": 100,
                        "original_stack_pointer": 120,
                        "offset": 4,
                        "frame_offset": 20,
                    }
                ],
                "tight_strings": [],
                "decoded_strings": [
                    {
                        "address": 0x402000,
                        "address_type": "GLOBAL",
                        "string": "decoded text",
                        "encoding": "ASCII",
                        "decoded_at": 0x401100,
                        "decoding_routine": 0x401080,
                    }
                ],
            },
        }

    def test_normalize_floss_preserves_each_location_kind(self) -> None:
        records = normalize_floss(
            self.payload,
            Path("sample.exe"),
            self.classification,
            "floss 3.1.1",
        )
        self.assertEqual(
            ["language", "stack", "decoded"],
            [row["origin"]["kind"] for row in records],
        )
        self.assertEqual(
            ["file_offset", "program_counter", "virtual_address"],
            [row["location"]["kind"] for row in records],
        )
        self.assertEqual(
            ["0x18", "0x401005", "0x402000"],
            [row["location"]["value"] for row in records],
        )
        self.assertTrue(all(row["recordId"].startswith("sha256:") for row in records))

    def test_static_strings_are_opt_in_to_avoid_native_duplicates(self) -> None:
        default_records = normalize_floss(
            self.payload, Path("sample.exe"), self.classification, "3.1.1"
        )
        complete_records = normalize_floss(
            self.payload,
            Path("sample.exe"),
            self.classification,
            "3.1.1",
            include_static=True,
        )
        self.assertNotIn("static", [row["origin"]["kind"] for row in default_records])
        self.assertIn("static", [row["origin"]["kind"] for row in complete_records])

    def test_translation_is_a_child_record_not_a_replacement(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        translator = FakeTranslator()
        translated = add_translations([parent], translator, "en", 4, 4, 200)
        self.assertEqual(1, len(translated))
        child = translated[0]
        self.assertEqual(parent["recordId"], child["parentRecordId"])
        self.assertEqual("translation", child["transform"]["kind"])
        self.assertEqual("1.0", child["transform"]["engineVersion"])
        self.assertEqual("deadbeef", child["transform"]["revision"])
        self.assertEqual("a" * 64, child["transform"]["modelSha256"])
        self.assertEqual("cpu", child["transform"]["execution"]["device"])
        self.assertEqual("language text", parent["text"])
        self.assertEqual("translated language text", child["text"])

    def test_translation_batch_cardinality_mismatch_is_fatal(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]

        class BrokenTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                return []

        with self.assertRaisesRegex(EnrichmentError, "batch of 1"):
            add_translations([parent], BrokenTranslator(), "en", 4, 4, 200)

    def test_translation_deduplicates_text_without_collapsing_provenance(self) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]
        translator = FakeTranslator()

        translated = add_translations(
            [first, second],
            translator,
            "en",
            8,
            2,
            200,
            TranslationCache(16),
        )

        self.assertEqual([["language text"]], translator.calls)
        self.assertEqual(2, len(translated))
        self.assertEqual(
            [first["recordId"], second["recordId"]],
            [record["parentRecordId"] for record in translated],
        )

    def test_translation_cache_reuses_results_across_windows(self) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]
        translator = FakeTranslator()
        cache = TranslationCache(16)

        output = list(
            translate_normalized_records(
                [first, second],
                translator,
                "en",
                batch_size=1,
                minimum_characters=4,
                maximum_characters=200,
                window_size=1,
                cache=cache,
            )
        )

        self.assertEqual([["language text"]], translator.calls)
        self.assertEqual(4, len(output))

    def test_changed_structured_identifier_is_fatal(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        parent = {**parent, "text": "cuenta analyst@example.com"}

        class IdentifierBreakingTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                return ["translated account"]

        with self.assertRaisesRegex(EnrichmentError, "analyst@example.com"):
            add_translations([parent], IdentifierBreakingTranslator(), "en", 4, 4, 200)

    def test_deduplication_keeps_distinct_provenance(self) -> None:
        records = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")
        repeated = unique_records([records[0], records[0], records[1]])
        self.assertEqual(
            [records[0]["recordId"], records[1]["recordId"]],
            [row["recordId"] for row in repeated],
        )

    def test_existing_jsonl_can_feed_offline_translation(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        with tempfile.TemporaryDirectory() as directory:
            input_path = Path(directory) / "input.jsonl"
            input_path.write_text(json.dumps(parent) + "\n", encoding="utf-8")

            output = list(
                translate_normalized_records(
                    read_normalized_jsonl(input_path),
                    FakeTranslator(),
                    "en",
                    batch_size=8,
                    minimum_characters=4,
                    maximum_characters=200,
                )
            )

        self.assertEqual(2, len(output))
        self.assertEqual(parent["recordId"], output[1]["parentRecordId"])
        self.assertEqual("translated language text", output[1]["text"])

    def test_atomic_writer_replaces_only_after_complete_serialization(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "records.jsonl"
            output.write_text("old", encoding="utf-8")

            def broken_records():
                yield {"one": 1}
                raise EnrichmentError("stop")

            with self.assertRaisesRegex(EnrichmentError, "stop"):
                write_jsonl_atomic(output, broken_records())
            self.assertEqual("old", output.read_text(encoding="utf-8"))
            self.assertEqual([], list(Path(directory).glob("*.partial.*")))

            count = write_jsonl_atomic(output, [{"two": 2}])
            self.assertEqual(1, count)
            self.assertEqual({"two": 2}, json.loads(output.read_text(encoding="utf-8")))

    def test_transformers_v5_is_rejected_after_invalid_output_regression(self) -> None:
        validate_transformers_version("4.57.6")
        with self.assertRaisesRegex(EnrichmentError, "repeated-token"):
            validate_transformers_version("5.14.1")

    def test_model_hash_mismatch_fails_before_runtime_loading(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            model_path = Path(directory)
            (model_path / "model.safetensors").write_bytes(b"not-the-pinned-model")
            with self.assertRaisesRegex(EnrichmentError, "SHA-256 mismatch"):
                MadladTranslator(
                    model_path,
                    "google/test-model",
                    "revision",
                    "0" * 64,
                    "cpu",
                    16,
                    16,
                )

    def test_gguf_hash_mismatch_fails_before_starting_llama_server(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            model_path = Path(directory) / "model.gguf"
            model_path.write_bytes(b"not-the-pinned-model")
            with self.assertRaisesRegex(EnrichmentError, "SHA-256 mismatch"):
                LlamaCppTranslator(
                    "missing-llama-server",
                    model_path,
                    "test/model",
                    "revision",
                    "0" * 64,
                    "cpu",
                    16,
                    16,
                    30,
                    30,
                )

    def test_translation_engine_is_selected_from_local_model_shape(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            gguf = root / "model.gguf"
            gguf.write_bytes(b"model")
            self.assertEqual(
                "llama-cpp",
                selected_translation_engine(
                    Namespace(translation_engine="auto", translation_model_path=gguf)
                ),
            )
            self.assertEqual(
                "madlad",
                selected_translation_engine(
                    Namespace(translation_engine="auto", translation_model_path=root)
                ),
            )

    def test_llama_prompt_names_language_and_protects_identifiers(self) -> None:
        prompt = llama_translation_prompt("cuenta analyst@example.com", "en")
        self.assertIn("into English", prompt)
        self.assertIn("analyst@example.com", prompt)
        self.assertIn("Preserve every email address", prompt)
        self.assertIn("into Traditional Chinese", llama_translation_prompt("evidence", "zh-Hant"))

    def test_protected_identifier_extraction_is_conservative_and_ordered(self) -> None:
        text = (
            "CVE-2025-12345 from analyst@example.com used C:\\Evidence\\memory.raw and "
            "https://example.org/a?id=42."
        )
        self.assertEqual(
            (
                "CVE-2025-12345",
                "analyst@example.com",
                "C:\\Evidence\\memory.raw",
                "https://example.org/a?id=42",
            ),
            protected_identifiers(text),
        )

    def test_llama_parallel_translation_preserves_input_order(self) -> None:
        translator = object.__new__(LlamaCppTranslator)
        translator.parallelism = 3
        barrier = threading.Barrier(3)

        def translate_one(text: str, target_language: str) -> str:
            barrier.wait(timeout=2)
            return f"{target_language}:{text}"

        translator._translate_one = translate_one  # type: ignore[method-assign]

        self.assertEqual(
            ["en:first", "en:second", "en:third"],
            translator.translate(["first", "second", "third"], "en"),
        )

    def test_auto_parallelism_scales_with_hardware_and_work(self) -> None:
        self.assertEqual(
            2,
            resolve_translation_parallelism(
                0,
                strict_determinism=False,
                has_cuda=True,
                model_size_bytes=2 * 1024**3,
                batch_size=8,
                logical_processors=22,
            ),
        )
        self.assertEqual(
            1,
            resolve_translation_parallelism(
                0,
                strict_determinism=True,
                has_cuda=True,
                model_size_bytes=2 * 1024**3,
                batch_size=8,
                logical_processors=22,
            ),
        )
        self.assertEqual(
            2,
            resolve_translation_parallelism(
                0,
                strict_determinism=False,
                has_cuda=False,
                model_size_bytes=12 * 1024**3,
                batch_size=8,
                logical_processors=22,
            ),
        )
        self.assertEqual(
            1,
            resolve_translation_parallelism(
                0,
                strict_determinism=False,
                has_cuda=True,
                model_size_bytes=12 * 1024**3,
                batch_size=8,
                logical_processors=22,
            ),
        )

    def test_llama_command_allocates_context_per_parallel_slot(self) -> None:
        command = build_llama_server_command(
            "llama-server",
            Path("model.gguf"),
            gpu_layers="auto",
            device="CUDA0",
            context_size=8192,
            parallelism=4,
            port=18089,
            threads=16,
        )

        self.assertEqual("4", command[command.index("-np") + 1])
        self.assertEqual("8192", command[command.index("-c") + 1])
        self.assertEqual("auto", command[command.index("-ngl") + 1])
        self.assertIn("-cb", command)
        self.assertEqual("16", command[command.index("--threads") + 1])

    def test_airgap_mode_blocks_external_network_and_allows_loopback(self) -> None:
        enrichment_root = Path(__file__).resolve().parents[1]
        probe = """
import os
import socket
import sys

sys.path.insert(0, sys.argv[1])
from bstrings_enrich import AirgapNetworkError, enable_airgap_mode

enable_airgap_mode()
assert os.environ["HF_HUB_OFFLINE"] == "1"
assert os.environ["PIP_NO_INDEX"] == "1"
try:
    socket.getaddrinfo("example.com", 443)
except AirgapNetworkError:
    pass
else:
    raise AssertionError("external DNS was not blocked")

with socket.socket() as listener:
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    with socket.create_connection(listener.getsockname(), timeout=2):
        connection, _ = listener.accept()
        connection.close()
"""

        result = subprocess.run(
            [sys.executable, "-c", probe, str(enrichment_root)],
            capture_output=True,
            check=False,
            text=True,
            timeout=10,
        )

        self.assertEqual(0, result.returncode, result.stderr)

    @patch("bstrings_enrich.run_checked")
    def test_shellcode_format_is_explicitly_forwarded_to_floss(self, run_checked_mock) -> None:
        run_checked_mock.return_value.stdout = '{"strings":{}}'

        run_floss("floss.exe", Path("sample.bin"), 4, 30, "sc64")

        command = run_checked_mock.call_args.args[0]
        self.assertIn("--format", command)
        self.assertEqual("sc64", command[command.index("--format") + 1])


if __name__ == "__main__":
    unittest.main()
