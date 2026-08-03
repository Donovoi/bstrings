from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bstrings_enrich import (  # noqa: E402
    Classification,
    EnrichmentError,
    MadladTranslator,
    add_translations,
    normalize_floss,
    read_normalized_jsonl,
    run_floss,
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

    def translate(self, texts: list[str], target_language: str) -> list[str]:
        self.target_language = target_language
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
        self.assertEqual("language text", parent["text"])
        self.assertEqual("translated language text", child["text"])

    def test_translation_batch_cardinality_mismatch_is_fatal(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]

        class BrokenTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                return []

        with self.assertRaisesRegex(EnrichmentError, "batch of 1"):
            add_translations([parent], BrokenTranslator(), "en", 4, 4, 200)

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

    @patch("bstrings_enrich.run_checked")
    def test_shellcode_format_is_explicitly_forwarded_to_floss(self, run_checked_mock) -> None:
        run_checked_mock.return_value.stdout = '{"strings":{}}'

        run_floss("floss.exe", Path("sample.bin"), 4, 30, "sc64")

        command = run_checked_mock.call_args.args[0]
        self.assertIn("--format", command)
        self.assertEqual("sc64", command[command.index("--format") + 1])


if __name__ == "__main__":
    unittest.main()
