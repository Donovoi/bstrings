from __future__ import annotations

import io
import json
import subprocess
import sys
import tempfile
import threading
import unittest
from argparse import Namespace
from contextlib import nullcontext
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bstrings_enrich import (  # noqa: E402
    JSON_READ_CHUNK_BYTES,
    Classification,
    EnrichmentError,
    FlossJsonDocument,
    LlamaCppTranslator,
    MadladTranslator,
    TranslationCache,
    add_translations,
    build_llama_server_command,
    iter_extraction_paths,
    iter_normalized_floss,
    iter_unique_records,
    llama_translation_prompt,
    main,
    make_string_record,
    normalize_floss,
    parse_arguments,
    protected_identifiers,
    read_normalized_jsonl,
    read_paths_from,
    resolve_translation_parallelism,
    run_floss,
    selected_translation_engine,
    translate_normalized_records,
    unique_records,
    validate_arguments,
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


class TrackingBinaryFile:
    def __init__(self, handle) -> None:
        self.handle = handle
        self.read_sizes: list[int] = []

    @property
    def closed(self) -> bool:
        return self.handle.closed

    def read(self, size: int = -1) -> bytes:
        self.read_sizes.append(size)
        if size < 0:
            raise AssertionError("streaming parser attempted an unbounded read")
        return self.handle.read(size)

    def seek(self, offset: int, whence: int = 0) -> int:
        return self.handle.seek(offset, whence)

    def close(self) -> None:
        self.handle.close()


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

    @staticmethod
    def disk_document(payload: bytes) -> FlossJsonDocument:
        handle = tempfile.TemporaryFile(mode="w+b")  # noqa: SIM115 - ownership transfers
        handle.write(payload)
        handle.seek(0)
        return FlossJsonDocument(handle)

    @staticmethod
    def complete_document_bytes(
        category_items: dict[str, list[dict]] | None = None,
        metadata: dict | None = None,
    ) -> bytes:
        strings = {
            "static_strings": [],
            "language_strings": [],
            "language_strings_missed": [],
            "stack_strings": [],
            "tight_strings": [],
            "decoded_strings": [],
        }
        if category_items:
            strings.update(category_items)
        payload = {
            "metadata": metadata or {"language": "", "imagebase": 0x400000},
            "strings": strings,
        }
        return json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")

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

    def test_streamed_floss_normalization_is_lazy_and_exact(self) -> None:
        self.payload["strings"]["language_strings"].append(
            dict(self.payload["strings"]["language_strings"][0])
        )
        expected = unique_records(
            normalize_floss(
                self.payload,
                Path("sample.exe"),
                self.classification,
                "3.1.1",
            )
        )

        with patch("bstrings_enrich.make_string_record", wraps=make_string_record) as make_mock:
            streamed = iter_unique_records(
                iter_normalized_floss(
                    self.payload,
                    Path("sample.exe"),
                    self.classification,
                    "3.1.1",
                )
            )
            self.assertEqual(0, make_mock.call_count)
            first = next(streamed)
            self.assertEqual(1, make_mock.call_count)
            actual = [first, *streamed]

        self.assertEqual(expected, actual)

    def test_disk_backed_floss_parser_uses_bounded_reads_and_decodes_lazily(self) -> None:
        backing = tempfile.TemporaryFile(mode="w+b")  # noqa: SIM115 - ownership transfers
        backing.write(b'{"analysis":{"padding":"')
        padding = b"x" * JSON_READ_CHUNK_BYTES
        for _ in range(48):
            backing.write(padding)
        backing.write(
            b'"},"metadata":{"language":"","imagebase":4194304},'
            b'"strings":{"static_strings":[],"language_strings":['
        )
        for index in range(12000):
            if index:
                backing.write(b",")
            backing.write(
                json.dumps(
                    {
                        "string": f"language text {index}",
                        "offset": index,
                        "encoding": "UTF-8",
                    },
                    separators=(",", ":"),
                ).encode("utf-8")
            )
        backing.write(
            b'],"language_strings_missed":[],"stack_strings":[],"tight_strings":[],'
            b'"decoded_strings":[]}}'
        )
        backing.seek(0)
        tracking = TrackingBinaryFile(backing)
        document = FlossJsonDocument(tracking)

        self.assertTrue(tracking.read_sizes)
        self.assertLessEqual(max(tracking.read_sizes), JSON_READ_CHUNK_BYTES)
        with patch("bstrings_enrich.json.loads", wraps=json.loads) as loads_mock:
            records = iter_normalized_floss(
                document,
                Path("large.exe"),
                self.classification,
                "3.1.1",
            )
            first = next(records)
            self.assertEqual("language text 0", first["text"])
            self.assertEqual(1, loads_mock.call_count)
            records.close()

        self.assertTrue(document.closed)
        self.assertTrue(tracking.closed)

    def test_disk_backed_floss_preserves_semantic_category_order(self) -> None:
        shuffled = {
            "analysis": {"format": "pe"},
            "strings": {
                "decoded_strings": [self.payload["strings"]["decoded_strings"][0]],
                "tight_strings": [],
                "stack_strings": [self.payload["strings"]["stack_strings"][0]],
                "language_strings_missed": [],
                "language_strings": [self.payload["strings"]["language_strings"][0]],
                "static_strings": [self.payload["strings"]["static_strings"][0]],
            },
            "metadata": self.payload["metadata"],
        }
        document = self.disk_document(
            json.dumps(shuffled, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        )
        streamed = normalize_floss(
            document,
            Path("sample.exe"),
            self.classification,
            "3.1.1",
            include_static=True,
        )
        expected = normalize_floss(
            shuffled,
            Path("sample.exe"),
            self.classification,
            "3.1.1",
            include_static=True,
        )

        self.assertEqual(expected, streamed)
        self.assertTrue(document.closed)

    def test_disk_backed_floss_item_limit_is_exact(self) -> None:
        item = json.dumps(
            {"string": "x" * 256, "offset": 1, "encoding": "ASCII"},
            separators=(",", ":"),
        ).encode("utf-8")
        payload = (
            b'{"metadata":{"language":"","imagebase":4194304},'
            b'"strings":{"static_strings":[],"language_strings":['
            + item
            + b'],"language_strings_missed":[],"stack_strings":[],"tight_strings":[],'
            b'"decoded_strings":[]}}'
        )

        with patch("bstrings_enrich.MAX_FLOSS_JSON_ITEM_BYTES", len(item)):
            document = self.disk_document(payload)
            records = normalize_floss(
                document,
                Path("limit.exe"),
                self.classification,
                "3.1.1",
            )
            self.assertEqual(1, len(records))

        handle = tempfile.TemporaryFile(mode="w+b")  # noqa: SIM115 - constructor closes
        handle.write(payload)
        handle.seek(0)
        with (
            patch("bstrings_enrich.MAX_FLOSS_JSON_ITEM_BYTES", len(item) - 1),
            self.assertRaisesRegex(ValueError, "safety limit"),
        ):
            FlossJsonDocument(handle)
        self.assertTrue(handle.closed)

    @patch("bstrings_enrich.subprocess.run")
    def test_run_floss_rejects_malformed_or_truncated_json_and_closes_spool(self, run_mock) -> None:
        cases = (
            b'{"strings":{"language_strings":[{"string":"x","offset":1}]}',
            b'{"strings":{"language_strings":{}}}',
            b'{"strings":{}} trailing',
            b'{"strings":{},"metadata":{"language":"\xff"}}',
        )
        for payload in cases:
            captured = []

            def complete(command, _payload=payload, _captured=captured, **kwargs):
                _captured.append(kwargs["stdout"])
                kwargs["stdout"].write(_payload)
                return subprocess.CompletedProcess(command, 0)

            run_mock.side_effect = complete
            with (
                self.subTest(payload=payload),
                self.assertRaisesRegex(EnrichmentError, "invalid JSON"),
            ):
                run_floss("floss.exe", Path("sample.exe"), 4, 30)
            self.assertTrue(captured[0].closed)

    def test_disk_backed_floss_rejects_duplicate_evidence_keys(self) -> None:
        metadata = b'{"language":"","imagebase":4194304}'
        empty_categories = (
            b'"static_strings":[],"language_strings":[],"language_strings_missed":[],'
            b'"stack_strings":[],"tight_strings":[],"decoded_strings":[]'
        )
        duplicate_root = (
            b'{"metadata":'
            + metadata
            + b',"metadata":'
            + metadata
            + b',"strings":{'
            + empty_categories
            + b"}}"
        )
        duplicate_metadata = (
            b'{"metadata":{"language":"","language":"fr","imagebase":4194304},'
            b'"strings":{' + empty_categories + b"}}"
        )
        duplicate_category = (
            b'{"metadata":' + metadata + b',"strings":{"static_strings":[],"language_strings":[],'
            b'"language_strings":[],"language_strings_missed":[],"stack_strings":[],'
            b'"tight_strings":[],"decoded_strings":[]}}'
        )
        for payload, message in (
            (duplicate_root, "Duplicate FLOSS root key"),
            (duplicate_metadata, "Duplicate JSON object key"),
            (duplicate_category, "Duplicate FLOSS strings category"),
        ):
            with self.subTest(message=message), self.assertRaisesRegex(ValueError, message):
                self.disk_document(payload)

        duplicate_item = (
            b'{"metadata":' + metadata + b',"strings":{"static_strings":[],"language_strings":['
            b'{"string":"value","offset":1,"offset":2,"encoding":"ASCII"}],'
            b'"language_strings_missed":[],"stack_strings":[],"tight_strings":[],'
            b'"decoded_strings":[]}}'
        )
        document = self.disk_document(duplicate_item)
        with self.assertRaisesRegex(EnrichmentError, "Duplicate JSON object key 'offset'"):
            normalize_floss(document, Path("duplicate.exe"), self.classification, "3.1.1")
        self.assertTrue(document.closed)

    def test_disk_backed_floss_requires_exact_v311_string_schema(self) -> None:
        base = json.loads(self.complete_document_bytes())
        for category in tuple(base["strings"]):
            payload = json.loads(json.dumps(base))
            del payload["strings"][category]
            with (
                self.subTest(missing=category),
                self.assertRaisesRegex(ValueError, "missing required categories"),
            ):
                self.disk_document(json.dumps(payload, separators=(",", ":")).encode())

        unknown = json.loads(json.dumps(base))
        unknown["strings"]["future_strings"] = []
        with self.assertRaisesRegex(ValueError, "Unknown FLOSS v3.1.1 strings category"):
            self.disk_document(json.dumps(unknown, separators=(",", ":")).encode())

        del base["metadata"]
        with self.assertRaisesRegex(ValueError, "no metadata object"):
            self.disk_document(json.dumps(base, separators=(",", ":")).encode())

    def test_disk_backed_floss_enforces_v311_item_domains(self) -> None:
        valid_language = dict(self.payload["strings"]["language_strings"][0])
        valid_stack = dict(self.payload["strings"]["stack_strings"][0])
        valid_decoded = dict(self.payload["strings"]["decoded_strings"][0])
        cases = []

        for value in (-1, 1 << 64):
            cases.append(
                ("language_strings", dict(valid_language, offset=value), "unsigned 64-bit")
            )
        cases.append(
            ("language_strings", dict(valid_language, encoding="UTF-32"), "encoding must be")
        )
        missing_encoding = dict(valid_language)
        del missing_encoding["encoding"]
        cases.append(("language_strings", missing_encoding, "missing required fields"))
        cases.append(("language_strings", {**valid_language, "future": 1}, "unknown fields"))
        cases.append(("stack_strings", dict(valid_stack, function=-1), "unsigned 64-bit"))
        cases.append(("stack_strings", dict(valid_stack, offset=-(1 << 63) - 1), "signed 64-bit"))
        cases.append(
            ("decoded_strings", dict(valid_decoded, address_type="FILE"), "address_type must be")
        )
        cases.append(
            ("decoded_strings", dict(valid_decoded, decoded_at=1 << 64), "unsigned 64-bit")
        )

        for category, item, message in cases:
            document = self.disk_document(self.complete_document_bytes({category: [item]}))
            with (
                self.subTest(category=category, item=item),
                self.assertRaisesRegex(EnrichmentError, message),
            ):
                normalize_floss(document, Path("invalid.exe"), self.classification, "3.1.1")
            self.assertTrue(document.closed)

        signed_offsets = dict(valid_stack, offset=-1, frame_offset=-(1 << 63))
        document = self.disk_document(
            self.complete_document_bytes({"stack_strings": [signed_offsets]})
        )
        records = normalize_floss(
            document,
            Path("signed-offset.exe"),
            self.classification,
            "3.1.1",
        )
        self.assertEqual(-1, records[0]["attributes"]["stackOffset"])
        self.assertEqual(-(1 << 63), records[0]["attributes"]["frameOffset"])

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

    def test_unchanged_translations_emit_one_auditable_child_per_parent(self) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]

        class IdentityTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                self.calls.append(list(texts))
                return list(texts)

        translator = IdentityTranslator()
        children = add_translations([first, second], translator, "en", 8, 4, 200)

        self.assertEqual([["language text"]], translator.calls)
        self.assertEqual(2, len(children))
        self.assertEqual(
            [first["recordId"], second["recordId"]],
            [child["parentRecordId"] for child in children],
        )
        self.assertTrue(all(child["text"] == "language text" for child in children))
        self.assertTrue(all(child["transform"]["outcome"] == "unchanged" for child in children))

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
        progress: list[int] = []

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
                progress=progress.append,
            )
        )

        self.assertEqual([["language text"]], translator.calls)
        self.assertEqual(4, len(output))
        self.assertEqual([1, 2], progress)

    def test_changed_structured_identifier_is_fatal(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        parent = {**parent, "text": "cuenta analyst@example.com"}

        class IdentifierBreakingTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                return ["translated account"]

        with self.assertRaisesRegex(EnrichmentError, "analyst@example.com"):
            add_translations([parent], IdentifierBreakingTranslator(), "en", 4, 4, 200)

    def test_identifier_only_candidate_bypasses_translation_and_is_auditable(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        parent = {**parent, "text": "$SYNTH_TOKEN001 $SYNTH_TOKEN001"}
        translator = FakeTranslator()

        children = add_translations([parent], translator, "en", 4, 4, 200)

        self.assertEqual([], translator.calls)
        self.assertEqual(1, len(children))
        self.assertEqual("$SYNTH_TOKEN001 $SYNTH_TOKEN001", children[0]["text"])
        self.assertEqual("unchanged", children[0]["transform"]["outcome"])
        self.assertEqual(parent["recordId"], children[0]["parentRecordId"])

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

    def test_atomic_writer_rejects_records_above_the_matcher_line_limit(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "records.jsonl"
            output.write_text("old", encoding="utf-8")
            record = {"recordId": "large", "text": "x" * 100}

            with (
                patch("bstrings_enrich.MAX_JSONL_LINE_CHARACTERS", 64),
                self.assertRaisesRegex(EnrichmentError, "JSONL safety limit"),
            ):
                write_jsonl_atomic(output, [record])

            self.assertEqual("old", output.read_text(encoding="utf-8"))
            self.assertEqual([], list(Path(directory).glob("*.partial.*")))

    def test_positional_extraction_paths_remain_supported(self) -> None:
        args = parse_arguments(["first.exe", "second.exe", "-o", "records.jsonl"])

        validate_arguments(args)

        self.assertEqual(
            [Path("first.exe"), Path("second.exe")],
            list(iter_extraction_paths(args.paths, args.paths_from)),
        )

    def test_paths_from_streams_utf8_entries_without_materializing_them(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            inventory = Path(directory) / "inventory.txt"
            inventory.write_bytes(
                b"\xef\xbb\xbffirst.exe\r\n\r\nsecond file.exe\nthird.exe\x00tail\n"
            )

            paths = iter(read_paths_from(inventory))
            self.assertEqual(Path("first.exe"), next(paths))
            self.assertEqual(Path("second file.exe"), next(paths))
            with self.assertRaisesRegex(EnrichmentError, "line 4 contains a NUL"):
                next(paths)

    def test_paths_from_rejects_empty_and_overlong_inventories(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            empty = root / "empty.txt"
            empty.write_text("\n  \n", encoding="utf-8")
            with self.assertRaisesRegex(EnrichmentError, "no usable paths"):
                list(iter_extraction_paths([], empty))

            overlong = root / "overlong.txt"
            overlong.write_text("x" * (32 * 1024 + 1), encoding="utf-8")
            with self.assertRaisesRegex(EnrichmentError, "safety limit"):
                list(read_paths_from(overlong))

    def test_paths_from_input_modes_are_mutually_exclusive(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inventory = root / "inventory.txt"
            inventory.write_text("sample.exe\n", encoding="utf-8")
            normalized = root / "normalized.jsonl"
            normalized.write_text("{}\n", encoding="utf-8")
            output = root / "output.jsonl"

            for argv in (
                ["sample.exe", "--paths-from", str(inventory), "-o", str(output)],
                [
                    "--paths-from",
                    str(inventory),
                    "--input-jsonl",
                    str(normalized),
                    "--translate",
                    "-o",
                    str(output),
                ],
            ):
                with (
                    self.subTest(argv=argv),
                    self.assertRaisesRegex(EnrichmentError, "cannot be combined"),
                ):
                    validate_arguments(parse_arguments(argv))

    def test_paths_from_rejects_missing_list_and_output_alias(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            missing = root / "missing.txt"
            output = root / "output.jsonl"
            with self.assertRaisesRegex(EnrichmentError, "Path list was not found"):
                validate_arguments(
                    parse_arguments(["--paths-from", str(missing), "-o", str(output)])
                )

            inventory = root / "inventory.txt"
            inventory.write_text("sample.exe\n", encoding="utf-8")
            with self.assertRaisesRegex(EnrichmentError, "must be different"):
                validate_arguments(
                    parse_arguments(["--paths-from", str(inventory), "-o", str(inventory)])
                )

    @patch("bstrings_enrich.run_floss")
    @patch("bstrings_enrich.classify_file")
    @patch("bstrings_enrich.tool_version", return_value="test-version")
    @patch("bstrings_enrich.executable_path", side_effect=lambda command: command)
    def test_main_processes_paths_from_inventory(
        self,
        _executable_path_mock,
        _tool_version_mock,
        classify_file_mock,
        run_floss_mock,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inputs = [root / "first.exe", root / "second.exe"]
            for path in inputs:
                path.write_bytes(b"fixture")
            inventory = root / "inventory.txt"
            inventory.write_text(
                "".join(f"{path}\n" for path in inputs),
                encoding="utf-8",
            )
            output = root / "records.jsonl"
            classify_file_mock.return_value = self.classification
            self.payload["strings"]["language_strings"].append(
                dict(self.payload["strings"]["language_strings"][0])
            )
            run_floss_mock.return_value = self.payload

            with patch(
                "bstrings_enrich.normalize_floss",
                side_effect=AssertionError("integrated recovery must stay streaming"),
            ):
                exit_code = main(
                    [
                        "--bounded-integrated-mode",
                        "--paths-from",
                        str(inventory),
                        "-o",
                        str(output),
                    ]
                )

            self.assertEqual(0, exit_code)
            self.assertEqual(
                [path.resolve() for path in inputs],
                [call.args[1] for call in run_floss_mock.call_args_list],
            )
            records = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
            self.assertEqual(6, len(records))
            self.assertEqual(
                {str(path.resolve()) for path in inputs},
                {record["sourceFile"] for record in records},
            )

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

    def test_llama_translation_requires_one_terminal_nonempty_choice(self) -> None:
        class FakeOpener:
            def __init__(self, body: dict, tokens: list[int]) -> None:
                self.body = body
                self.tokens = tokens
                self.requests = []

            def open(self, request, timeout):
                self.requests.append((json.loads(request.data), timeout))
                if request.full_url.endswith("/apply-template"):
                    body = {"prompt": "templated prompt"}
                elif request.full_url.endswith("/tokenize"):
                    body = {"tokens": self.tokens}
                else:
                    body = dict(self.body)
                    body.setdefault("usage", {"prompt_tokens": len(self.tokens)})
                return io.BytesIO(json.dumps(body).encode("utf-8"))

        def translator_for(
            body: dict, tokens: list[int] | None = None
        ) -> tuple[LlamaCppTranslator, FakeOpener]:
            translator = object.__new__(LlamaCppTranslator)
            opener = FakeOpener(body, tokens or [1, 2, 3])
            translator._port = 1
            translator._max_input_tokens = 8
            translator._max_new_tokens = 32
            translator._strict_determinism = True
            translator._request_timeout_seconds = 5
            translator._thread_state = SimpleNamespace(opener=opener)
            return translator, opener

        for finish_reason in (None, "length", "content_filter", "tool_calls"):
            body = {
                "choices": [
                    {
                        "finish_reason": finish_reason,
                        "message": {"content": "translated"},
                    }
                ]
            }
            translator, _ = translator_for(body)
            with (
                self.subTest(finish_reason=finish_reason),
                self.assertRaisesRegex(EnrichmentError, "non-terminal.*finish_reason"),
            ):
                translator._translate_one("source", "en")

        translator, _ = translator_for(
            {
                "choices": [
                    {"finish_reason": "stop", "message": {"content": "one"}},
                    {"finish_reason": "stop", "message": {"content": "two"}},
                ]
            }
        )
        with self.assertRaisesRegex(EnrichmentError, "exactly one choice"):
            translator._translate_one("source", "en")

        translator, _ = translator_for(
            {"choices": [{"finish_reason": "stop", "message": {"content": "   "}}]}
        )
        with self.assertRaisesRegex(EnrichmentError, "empty translation"):
            translator._translate_one("source", "en")

        translator, opener = translator_for(
            {"choices": [{"finish_reason": "stop", "message": {"content": " result "}}]}
        )
        self.assertEqual("result", translator._translate_one("source", "en"))
        tokenize_request = next(
            request
            for request, _ in opener.requests
            if request.get("content") == "templated prompt"
        )
        self.assertTrue(tokenize_request["add_special"])
        self.assertTrue(tokenize_request["parse_special"])
        self.assertEqual(1, opener.requests[-1][0]["n"])

        translator, _ = translator_for(
            {"choices": [{"finish_reason": "stop", "message": {"content": "result"}}]},
            list(range(9)),
        )
        with self.assertRaisesRegex(EnrichmentError, "input safety limit.*not truncated"):
            translator._translate_one("source", "en")

        translator, _ = translator_for(
            {
                "choices": [{"finish_reason": "stop", "message": {"content": "result"}}],
                "usage": {"prompt_tokens": 2},
            }
        )
        with self.assertRaisesRegex(EnrichmentError, "complete prompt acceptance"):
            translator._translate_one("source", "en")

    def test_madlad_translation_requires_eos_and_nonempty_output(self) -> None:
        class Encoded(dict):
            def to(self, device):
                self.device = device
                return self

        class FakeTokenizer:
            eos_token_id = None

            def __init__(self, decoded: list[str], attention_masks: list[list[int]]) -> None:
                self.decoded = decoded
                self.attention_masks = attention_masks

            def __call__(self, prompts, **kwargs):
                self.prompts = prompts
                self.call_kwargs = kwargs
                return Encoded(
                    input_ids=[[1] * len(mask) for mask in self.attention_masks],
                    attention_mask=self.attention_masks,
                )

            def batch_decode(self, sequences, **kwargs):
                self.decode_kwargs = kwargs
                return list(self.decoded)

        class FakeModel:
            def __init__(self, sequences, eos_token_id=1) -> None:
                self.sequences = sequences
                self.generation_config = SimpleNamespace(eos_token_id=eos_token_id)

            def generate(self, **kwargs):
                self.generate_kwargs = kwargs
                return SimpleNamespace(sequences=self.sequences)

        def translator_for(
            sequences,
            decoded,
            eos_token_id=1,
            attention_masks: list[list[int]] | None = None,
        ) -> MadladTranslator:
            translator = object.__new__(MadladTranslator)
            translator._tokenizer = FakeTokenizer(
                decoded,
                attention_masks or [[1] for _ in decoded],
            )
            translator._model = FakeModel(sequences, eos_token_id)
            translator._torch = SimpleNamespace(inference_mode=lambda: nullcontext())
            translator._max_input_tokens = 64
            translator._max_new_tokens = 8
            translator._device = "cpu"
            return translator

        translator = translator_for([[0, 42, 1]], [" translated "])
        self.assertEqual(["translated"], translator.translate(["source"], "en"))
        self.assertTrue(translator._model.generate_kwargs["return_dict_in_generate"])

        translator = translator_for([[0, 42, 1], [0, 43, 44]], ["one", "two"])
        with self.assertRaisesRegex(EnrichmentError, "row 1.*without EOS"):
            translator.translate(["first", "second"], "en")

        translator = translator_for([[0, 42, 1]], ["translated"], eos_token_id=None)
        with self.assertRaisesRegex(EnrichmentError, "no EOS token ID"):
            translator.translate(["source"], "en")

        translator = translator_for([[0, 42, 1]], ["   "])
        with self.assertRaisesRegex(EnrichmentError, "empty translation for row 0"):
            translator.translate(["source"], "en")

        translator = translator_for(
            [[0, 42, 1]],
            ["translated"],
            attention_masks=[[1] * 65],
        )
        with self.assertRaisesRegex(EnrichmentError, "input safety limit.*not truncated"):
            translator.translate(["source"], "en")
        self.assertFalse(hasattr(translator._model, "generate_kwargs"))

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
        self.assertIn("--offline", command)
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

    @patch("bstrings_enrich.subprocess.run")
    def test_shellcode_format_is_explicitly_forwarded_to_floss(self, run_mock) -> None:
        def complete(command, **kwargs):
            kwargs["stdout"].write(self.complete_document_bytes())
            return subprocess.CompletedProcess(command, 0)

        run_mock.side_effect = complete
        document = run_floss("floss.exe", Path("sample.bin"), 4, 30, "sc64")
        document.close()

        command = run_mock.call_args.args[0]
        self.assertIn("--format", command)
        self.assertEqual("sc64", command[command.index("--format") + 1])
        self.assertNotIn("capture_output", run_mock.call_args.kwargs)


if __name__ == "__main__":
    unittest.main()
