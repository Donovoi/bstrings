from __future__ import annotations

import hashlib
import io
import json
import os
import sqlite3
import subprocess
import sys
import tempfile
import threading
import unittest
from argparse import Namespace
from contextlib import nullcontext, redirect_stderr, redirect_stdout
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from bstrings_enrich import (  # noqa: E402
    JSON_READ_CHUNK_BYTES,
    BoundedTranslationOutcomeCache,
    Classification,
    EnrichmentError,
    EvidenceReadLease,
    FlossJsonDocument,
    InputIdentity,
    LlamaCppTranslator,
    MadladTranslator,
    MagikaClassification,
    RoutingInput,
    RunLocalTranslationCache,
    TranslationAttempt,
    TranslationCache,
    TranslationIntegrityMonitor,
    TranslationOutcome,
    TranslationWorkStats,
    _iter_magika_batches,
    _make_routing_record,
    _run_magika_batch,
    add_translations,
    advisory_identifier_spans,
    build_llama_server_command,
    finalize_run_translation_cache,
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
    read_routing_manifest,
    resolve_translation_parallelism,
    run_floss,
    selected_translation_engine,
    translate_normalized_records,
    translation_cache_identity,
    unique_records,
    validate_arguments,
    validate_identifier_retention,
    validate_transformers_version,
    write_jsonl_atomic,
    write_translation_stats_atomic,
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
    def write_input_manifest(path: Path, inputs: list[Path]) -> None:
        rows = []
        for source in inputs:
            content = source.read_bytes()
            rows.append(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "path": str(source.resolve()),
                        "length": len(content),
                        "sha256": hashlib.sha256(content).hexdigest(),
                    },
                    separators=(",", ":"),
                )
            )
        path.write_text("\n".join(rows) + "\n", encoding="utf-8")

    @staticmethod
    def unknown_magika(status: str = "ok") -> MagikaClassification:
        unknown = Classification(
            "unknown", 0.25 if status == "ok" else 0.0, False, "application/octet-stream", "unknown"
        )
        return MagikaClassification(status, unknown, unknown, None if status == "ok" else "test")

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
        self.assertEqual("verified", child["attributes"]["translationIntegrity"])
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

    def test_translation_work_stats_count_decisions_without_retaining_keys(self) -> None:
        cached = make_string_record(
            text="cached language text",
            source_file="first.exe",
            extractor_version="3.1.1",
            kind="decoded",
            location_kind="file-offset",
            location_value=1,
            attributes={},
        )
        protected = make_string_record(
            text="SYNTH_TOKEN 12345-67890",
            source_file="second.exe",
            extractor_version="3.1.1",
            kind="decoded",
            location_kind="file-offset",
            location_value=2,
            attributes={},
        )
        fallback = make_string_record(
            text="uncached language text",
            source_file="third.exe",
            extractor_version="3.1.1",
            kind="decoded",
            location_kind="file-offset",
            location_value=3,
            attributes={},
        )

        class FallbackTranslator(FakeTranslator):
            def translate_attempts(
                self, texts: list[str], target_language: str
            ) -> list[TranslationAttempt]:
                self.calls.append(list(texts))
                return [TranslationAttempt(None, "synthetic-failure") for _ in texts]

        cache = TranslationCache(4)
        cache.put("en", cached["text"], "cached translated language text")
        stats = TranslationWorkStats()

        children = add_translations(
            [cached, protected, fallback],
            FallbackTranslator(),
            "en",
            8,
            4,
            200,
            cache=cache,
            work_stats=stats,
        )

        self.assertEqual(3, len(children))
        self.assertEqual(
            {
                "schemaVersion": 1,
                "candidateOccurrences": 3,
                "textDecisions": 3,
                "protectedOnlyBypassTexts": 1,
                "runCacheHits": 0,
                "translationCacheHits": 1,
                "translatorRequests": 1,
                "translatorInputTexts": 1,
                "modelResults": 1,
                "modelFallbacks": 1,
                "translatedChildOccurrences": 0,
                "preservationFallbackChildOccurrences": 0,
            },
            stats.payload(),
        )

    def test_changed_structured_identifier_becomes_explicit_source_fallback(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        parent = {**parent, "text": "cuenta analyst@example.com"}

        class IdentifierBreakingTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                return ["translated account"]

        children = add_translations([parent], IdentifierBreakingTranslator(), "en", 4, 4, 200)

        self.assertEqual(1, len(children))
        self.assertEqual(parent["text"], children[0]["text"])
        self.assertEqual("unchanged", children[0]["transform"]["outcome"])
        self.assertEqual(
            "preservation-fallback",
            children[0]["attributes"]["translationIntegrity"],
        )
        self.assertEqual(
            "hard-identifier-retention-mismatch",
            children[0]["attributes"]["translationIntegrityReason"],
        )

    def test_hard_identifier_validation_rejects_surplus_duplicates(self) -> None:
        with self.assertRaisesRegex(EnrichmentError, "analyst@example.com"):
            validate_identifier_retention(
                "cuenta analyst@example.com",
                "account analyst@example.com analyst@example.com",
            )

    def test_hard_identifier_retention_is_case_and_unicode_normalization_exact(self) -> None:
        cases = (
            ("SYNTH-CODE", "synth-code"),
            ("Café_TOKEN", "Cafe\u0301_TOKEN"),
        )

        for source, changed in cases:
            with self.subTest(source=source), self.assertRaises(EnrichmentError):
                validate_identifier_retention(source, changed)

    def test_promised_structured_identifier_classes_reject_exact_mutations(self) -> None:
        cases = (
            (
                "guid",
                "550e8400-e29b-41d4-a716-446655440000",
                "550e8400-e29b-41d4-a716-446655440001",
            ),
            ("ipv4", "192.168.1.10", "192.168.1.11"),
            ("ipv4-port", "192.168.1.10:443", "192.168.1.10:8443"),
            ("registry", r"HKLM\Software\Vendor", r"HKLM\Software\Other"),
            ("filename", "report.pdf", "report.txt"),
            ("braced-placeholder", "${API_TOKEN}", "${API_KEY}"),
            ("percent-placeholder", "%TEMP_DIR%", "%TMP_DIR%"),
        )

        for identifier_class, source, changed in cases:
            with self.subTest(identifier_class=identifier_class):
                self.assertEqual((source,), protected_identifiers(source))
                with self.assertRaises(EnrichmentError):
                    validate_identifier_retention(source, changed)

    def test_hyphenated_words_are_advisory_with_unicode_complete_boundaries(self) -> None:
        text = "Software-Angebotsmesse prοd-api Cafe\u0301‐Profile служба‑резерв"

        self.assertEqual((), protected_identifiers(text))
        self.assertEqual(
            (
                "Software-Angebotsmesse",
                "prοd-api",
                "Cafe\u0301‐Profile",
                "служба‑резерв",
            ),
            tuple(value for _, _, value in advisory_identifier_spans(text)),
        )

    def test_generic_machine_signals_are_hard_without_numeric_language_false_positive(self) -> None:
        text = "svc_backup SYNTH-CODE 17-jährig server-01"

        self.assertEqual(("svc_backup", "SYNTH-CODE"), protected_identifiers(text))
        self.assertEqual(
            ("17-jährig", "server-01"),
            tuple(value for _, _, value in advisory_identifier_spans("17-jährig server-01")),
        )

    def test_numeric_hyphen_sequences_are_not_advisory_language_tokens(self) -> None:
        for text in ("12345-67890", "12345‐67890", "12345‑67890", "１２３４５－６７８９０"):
            with self.subTest(text=text):
                self.assertEqual((), advisory_identifier_spans(text))

    def test_identifier_only_bypass_with_numeric_hyphen_uses_a_consistent_run_cache(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        source = "SYNTH_TOKEN 12345-67890"
        parent = {**parent, "text": source}

        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 4)
            translator = FakeTranslator()
            try:
                first = add_translations([parent], translator, "en", 4, 4, 200, run_cache=cache)[0]
                second = add_translations([parent], translator, "en", 4, 4, 200, run_cache=cache)[0]

                self.assertEqual([], translator.calls)
                self.assertEqual(source, first["text"])
                self.assertEqual("verified", first["attributes"]["translationIntegrity"])
                self.assertEqual(first, second)
                self.assertEqual(1, cache.hits)
            finally:
                cache.close(commit=False)

    def test_advisory_subspan_is_suppressed_inside_hard_hostname(self) -> None:
        text = "server-alpha.example"

        self.assertEqual((text,), protected_identifiers(text))
        self.assertEqual((), advisory_identifier_spans(text))

    def test_typed_spans_take_precedence_over_overlapping_machine_prefixes(self) -> None:
        cases = (
            "FOO-HTTPS://example.com",
            r"FOO-C:\Temp\x.txt",
            r"FOO-HKLM\Software\Vendor",
        )

        for text in cases:
            with self.subTest(text=text):
                self.assertEqual((text,), protected_identifiers(text))
                with self.assertRaises(EnrichmentError):
                    validate_identifier_retention(text, text.replace("FOO", "BAR"))

    def test_posix_slash_drive_and_unc_paths_are_exact_hard_identifiers(self) -> None:
        posix = "/etc/passwd"
        slash_drive = "C:/Windows/System32/cmd.exe"
        unc = r"\\server\share\file.txt"
        source = f"Paths: {posix}, {slash_drive}; {unc}."

        self.assertEqual((posix, slash_drive, unc), protected_identifiers(source))
        for original, changed in (
            (posix, "/etc/shadow"),
            (slash_drive, "C:/Windows/System32/powershell.exe"),
            (unc, r"\\server\share\other.txt"),
        ):
            with self.subTest(original=original), self.assertRaises(EnrichmentError):
                validate_identifier_retention(source, source.replace(original, changed))

    def test_one_component_posix_paths_and_command_switches_are_hard(self) -> None:
        source = "Inspect /tmp and /root with switches /v /quiet."

        self.assertEqual(
            ("/tmp", "/root", "/v", "/quiet"),
            protected_identifiers(source),
        )
        for original, changed in (
            ("/tmp", "/var"),
            ("/root", "/home"),
            ("/v", "/x"),
            ("/quiet", "/silent"),
        ):
            with self.subTest(original=original), self.assertRaises(EnrichmentError):
                validate_identifier_retention(source, source.replace(original, changed))

    def test_path_scanner_rejects_partial_boundaries_and_ordinary_slash_prose(self) -> None:
        cases = (
            "prefix/etc/passwd",
            "./etc/passwd",
            "prefixC:/Windows/System32/cmd.exe",
            r"prefix\\server\share\file.txt",
            "Use and/or prose / one / two without an absolute path",
        )

        for text in cases:
            with self.subTest(text=text):
                self.assertFalse(
                    any("/" in value or "\\" in value for value in protected_identifiers(text))
                )

        unicode_path = "/etc/passwd界"
        self.assertEqual((unicode_path,), protected_identifiers(unicode_path))

    def test_balanced_url_closing_parenthesis_is_part_of_hard_identifier(self) -> None:
        url = "https://en.wikipedia.org/wiki/Function_(mathematics)"
        source = f"See {url} now"

        self.assertEqual((url,), protected_identifiers(source))
        with self.assertRaisesRegex(EnrichmentError, "Function_\\(mathematics\\)"):
            validate_identifier_retention(source, f"See {url[:-1]} now")

    def test_overlapping_email_and_port_spans_form_one_exact_composite(self) -> None:
        source = "Connect to user@example.com:443 now"

        self.assertEqual(("user@example.com:443",), protected_identifiers(source))
        with self.assertRaisesRegex(EnrichmentError, "user@example.com:443"):
            validate_identifier_retention(source, "Connect to user@example.com:8443 now")

    def test_validated_bare_and_bracketed_ipv6_are_exact_hard_identifiers(self) -> None:
        bare = "2001:0db8:85a3::8a2e:0370:7334"
        endpoint = "[2001:db8::1]:443"
        scoped = "fe80::1%eth0"
        source = f"Connect {bare} through {endpoint} on {scoped} now"

        self.assertEqual((bare, endpoint, scoped), protected_identifiers(source))
        with self.assertRaisesRegex(EnrichmentError, "\\[2001:db8::1\\]:443"):
            validate_identifier_retention(source, source.replace(":443", ":8443"))
        with self.assertRaisesRegex(EnrichmentError, "fe80::1%eth0"):
            validate_identifier_retention(source, source.replace("%eth0", "%eth1"))

    def test_invalid_bracketed_port_still_preserves_valid_inner_ipv6(self) -> None:
        source = "Endpoint [2001:db8::1]:99999 is invalid"

        self.assertEqual(("2001:db8::1",), protected_identifiers(source))

    def test_ipv6_validation_rejects_partial_unicode_boundaries_and_colon_prose(self) -> None:
        address = "2001:db8::1"
        cases = (
            f"α{address}",
            f"{address}界",
            f"α[{address}]:443",
            f"[{address}]:443界",
            "Time: 12:30 and ratio 1:2; namespace foo::bar",
            "Invalid 2001:db8::1::2 address",
        )

        for text in cases:
            with self.subTest(text=text):
                self.assertEqual((), protected_identifiers(text))

    def test_fixed_ascii_hash_is_hard_before_an_attached_unicode_suffix(self) -> None:
        digest = "0123456789abcdef" * 4
        text = digest + "입니다"

        self.assertEqual((digest,), protected_identifiers(text))

    def test_partial_separator_tokens_are_not_accepted(self) -> None:
        for text in ("tenant--prod", "-tenant-prod", "tenant-prod-"):
            with self.subTest(text=text):
                self.assertEqual((), protected_identifiers(text))
                self.assertEqual((), advisory_identifier_spans(text))

    def test_one_bad_row_falls_back_without_poisoning_good_sibling(self) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]
        first = {**first, "text": "cuenta analyst@example.com"}
        second = {**second, "text": "hola mundo"}

        class MixedTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                self.calls.append(list(texts))
                return [
                    "translated account" if "analyst@example.com" in text else "hello world"
                    for text in texts
                ]

        monitor = TranslationIntegrityMonitor()
        children = add_translations(
            [first, second],
            MixedTranslator(),
            "en",
            8,
            4,
            200,
            integrity_monitor=monitor,
        )

        self.assertEqual([first["text"], "hello world"], [child["text"] for child in children])
        self.assertEqual(
            ["preservation-fallback", "verified"],
            [child["attributes"]["translationIntegrity"] for child in children],
        )
        self.assertEqual(2, monitor.model_inputs)
        self.assertEqual(1, monitor.fallbacks)

    def test_generation_failure_retains_exact_source_without_publishing_rejected_text(self) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]
        first = {**first, "text": "synthetic source that reaches a generation bound"}
        second = {**second, "text": "hola mundo"}

        class AttemptTranslator(FakeTranslator):
            def translate_attempts(
                self, texts: list[str], target_language: str
            ) -> list[TranslationAttempt]:
                self.calls.append(list(texts))
                return [
                    (
                        TranslationAttempt(None, "generation-limit-reached")
                        if "generation bound" in text
                        else TranslationAttempt("hello world")
                    )
                    for text in texts
                ]

        monitor = TranslationIntegrityMonitor()
        cache = BoundedTranslationOutcomeCache(4)
        children = add_translations(
            [first, second],
            AttemptTranslator(),
            "en",
            8,
            4,
            200,
            run_cache=cache,
            integrity_monitor=monitor,
        )

        self.assertEqual(first["text"], children[0]["text"])
        self.assertEqual("unchanged", children[0]["transform"]["outcome"])
        self.assertEqual(
            "preservation-fallback",
            children[0]["attributes"]["translationIntegrity"],
        )
        self.assertEqual(
            "generation-limit-reached",
            children[0]["attributes"]["translationIntegrityReason"],
        )
        self.assertEqual("hello world", children[1]["text"])
        self.assertEqual((2, 1), (monitor.model_inputs, monitor.fallbacks))
        cached = cache.get(first["text"])
        self.assertEqual(first["text"], cached.text if cached is not None else None)

    def test_failed_translation_attempt_cannot_carry_rejected_model_text(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]

        class InvalidAttemptTranslator(FakeTranslator):
            def translate_attempts(
                self, texts: list[str], target_language: str
            ) -> list[TranslationAttempt]:
                return [TranslationAttempt("truncated output", "generation-limit-reached")]

        with self.assertRaisesRegex(EnrichmentError, "invalid failed row"):
            add_translations([parent], InvalidAttemptTranslator(), "en", 1, 4, 200)

    def test_successful_ambiguous_source_is_explicit_and_counted(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        parent = {**parent, "text": "Die Software-Angebotsmesse wurde geändert"}

        child = add_translations([parent], FakeTranslator(), "en", 4, 4, 200)[0]

        self.assertEqual(
            "source-retained-ambiguous",
            child["attributes"]["translationIntegrity"],
        )
        self.assertEqual(1, child["attributes"]["translationAmbiguousIdentifierCount"])

    def test_integrity_circuit_breaker_uses_exact_rate_and_consecutive_boundaries(self) -> None:
        rate_monitor = TranslationIntegrityMonitor()
        rate_monitor.record(fallback=True)
        for _ in range(99):
            rate_monitor.record(fallback=False)
        self.assertEqual((100, 1), (rate_monitor.model_inputs, rate_monitor.fallbacks))
        with self.assertRaisesRegex(EnrichmentError, "circuit breaker"):
            rate_monitor.record(fallback=True)

        consecutive_monitor = TranslationIntegrityMonitor()
        for _ in range(99):
            consecutive_monitor.record(fallback=True)
        with self.assertRaisesRegex(EnrichmentError, "100 consecutive"):
            consecutive_monitor.record(fallback=True)

    def test_bounded_outcome_cache_reuses_fallback_metadata_without_recounting(self) -> None:
        parent = normalize_floss(self.payload, Path("sample.exe"), self.classification, "3.1.1")[0]
        parent = {**parent, "text": "cuenta analyst@example.com"}

        class IdentifierBreakingTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                self.calls.append(list(texts))
                return ["translated account" for _ in texts]

        translator = IdentifierBreakingTranslator()
        monitor = TranslationIntegrityMonitor()
        cache = BoundedTranslationOutcomeCache(1)

        first = add_translations(
            [parent],
            translator,
            "en",
            1,
            4,
            200,
            run_cache=cache,
            integrity_monitor=monitor,
        )[0]
        second = add_translations(
            [parent],
            translator,
            "en",
            1,
            4,
            200,
            run_cache=cache,
            integrity_monitor=monitor,
        )[0]

        self.assertEqual([[parent["text"]]], translator.calls)
        self.assertEqual(1, monitor.model_inputs)
        self.assertEqual(1, monitor.fallbacks)
        self.assertEqual(1, cache.hits)
        self.assertEqual(
            ["preservation-fallback", "preservation-fallback"],
            [
                first["attributes"]["translationIntegrity"],
                second["attributes"]["translationIntegrity"],
            ],
        )

    def test_run_local_cache_uses_exact_text_under_digest_collision_and_cleans_up(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 1)
            cache._digest = lambda _text: b"collision"  # type: ignore[method-assign]
            cache.put("first source", TranslationOutcome("first result", "verified"))
            cache.put("second source", TranslationOutcome("second result", "verified"))
            cache_path = cache.path

            self.assertEqual("first result", cache.get("first source").text)
            self.assertEqual("second result", cache.get("second source").text)
            self.assertTrue(cache_path.is_file())
            cache.close(commit=True)
            self.assertFalse(cache_path.exists())

    def test_run_local_cache_setup_failure_closes_handle_and_removes_temporary_file(self) -> None:
        class FailingSetupConnection:
            def __init__(self, fail_fragment: str) -> None:
                self.fail_fragment = fail_fragment
                self.rolled_back = False
                self.closed = False

            def execute(self, statement: str):
                if self.fail_fragment in statement:
                    raise sqlite3.OperationalError("synthetic setup failure")
                return self

            def rollback(self) -> None:
                self.rolled_back = True

            def close(self) -> None:
                self.closed = True

        for fail_fragment in ("PRAGMA journal_mode", "CREATE TABLE"):
            with (
                self.subTest(fail_fragment=fail_fragment),
                tempfile.TemporaryDirectory() as directory,
            ):
                connection = FailingSetupConnection(fail_fragment)
                root = Path(directory)
                with (
                    patch("bstrings_enrich.sqlite3.connect", return_value=connection),
                    self.assertRaisesRegex(EnrichmentError, "Could not create run-local"),
                ):
                    RunLocalTranslationCache(root, "identity", 1)

                self.assertTrue(connection.rolled_back)
                self.assertTrue(connection.closed)
                self.assertEqual([], list(root.glob(".bstrings-translation-cache-*.sqlite3*")))

    def test_run_local_cache_commits_in_bounded_batches(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 2)
            try:
                for index in range(4097):
                    source = f"source text {index}"
                    cache.put(source, TranslationOutcome(f"translated text {index}", "verified"))

                self.assertEqual(1, cache.batch_commits)
                self.assertEqual(1, cache._pending_writes)
                self.assertLessEqual(len(cache._memory), 2)
            finally:
                cache.close(commit=False)

    def test_run_local_cache_uses_ephemeral_performance_pragmas(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 1)
            try:
                assert cache._connection is not None
                journal_mode = cache._connection.execute("PRAGMA journal_mode").fetchone()[0]
                synchronous = cache._connection.execute("PRAGMA synchronous").fetchone()[0]

                self.assertEqual("memory", str(journal_mode).lower())
                self.assertEqual(0, synchronous)
            finally:
                cache.close(commit=False)

    def test_cache_cleanup_failure_preserves_prior_output_and_removes_partial(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "translations.jsonl"
            output.write_text("previous result\n", encoding="utf-8")
            cache = RunLocalTranslationCache(root, "identity", 1)
            cache_path = cache.path
            original_unlink = Path.unlink

            def fail_cache_unlink(path: Path, *args, **kwargs) -> None:
                if path == cache_path:
                    raise PermissionError("synthetic cache lock")
                original_unlink(path, *args, **kwargs)

            with (
                patch.object(Path, "unlink", new=fail_cache_unlink),
                self.assertRaisesRegex(EnrichmentError, "cache cleanup failed"),
            ):
                write_jsonl_atomic(
                    output,
                    [{"new": "result"}],
                    before_publish=lambda: finalize_run_translation_cache(cache, output),
                )

            self.assertEqual("previous result\n", output.read_text(encoding="utf-8"))
            self.assertEqual([], list(root.glob("*.partial.*")))
            cache.close(commit=False)

    def test_run_local_cache_rejects_corrupt_translated_text_by_digest(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 0)
            source = "ordinary language text"
            cache.put(source, TranslationOutcome("translated language text", "verified"))
            assert cache._connection is not None
            cache._connection.execute(
                "UPDATE translations SET translated_text = ?",
                ("silently changed text",),
            )
            try:
                with self.assertRaisesRegex(EnrichmentError, "outcome-integrity validation"):
                    cache.get(source)
            finally:
                cache.close(commit=False)

    def test_run_local_cache_rejects_corrupt_integrity_status_and_count_by_digest(self) -> None:
        mutations = (
            ("integrity = ?", ("source-retained-ambiguous",)),
            ("ambiguous_identifier_count = ?", (1,)),
        )
        for assignment, parameters in mutations:
            with self.subTest(assignment=assignment), tempfile.TemporaryDirectory() as directory:
                cache = RunLocalTranslationCache(Path(directory), "identity", 0)
                source = "ordinary language text"
                cache.put(source, TranslationOutcome("translated language text", "verified"))
                assert cache._connection is not None
                cache._connection.execute(f"UPDATE translations SET {assignment}", parameters)
                try:
                    with self.assertRaisesRegex(EnrichmentError, "outcome-integrity validation"):
                        cache.get(source)
                finally:
                    cache.close(commit=False)

    def test_run_local_cache_recomputes_advisory_status_after_digest_verification(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 0)
            source = "translate server-01 label"
            translated = "translated server-01 label"
            cache.put(
                source,
                TranslationOutcome(translated, "source-retained-ambiguous", None, 1),
            )
            corrupted = TranslationOutcome(translated, "verified", None, 0)
            assert cache._connection is not None
            cache._connection.execute(
                """
                UPDATE translations
                SET integrity = ?, ambiguous_identifier_count = ?, outcome_digest = ?
                """,
                (
                    corrupted.integrity,
                    corrupted.ambiguous_identifier_count,
                    cache._outcome_digest(source, corrupted),
                ),
            )
            try:
                with self.assertRaisesRegex(EnrichmentError, "inconsistent advisory"):
                    cache.get(source)
            finally:
                cache.close(commit=False)

    def test_run_local_cache_deduplicates_beyond_hot_lru_without_collapsing_parents(self) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]
        third = normalize_floss(self.payload, Path("third.exe"), self.classification, "3.1.1")[0]
        first = {**first, "text": "repeated language text"}
        second = {**second, "text": "different language text"}
        third = {**third, "text": "repeated language text"}
        translator = FakeTranslator()
        monitor = TranslationIntegrityMonitor()
        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 1)
            try:
                children = list(
                    translate_normalized_records(
                        [first, second, third],
                        translator,
                        "en",
                        batch_size=1,
                        minimum_characters=4,
                        maximum_characters=200,
                        window_size=1,
                        include_parents=False,
                        run_cache=cache,
                        integrity_monitor=monitor,
                    )
                )
            finally:
                cache.close(commit=True)

        self.assertEqual(2, monitor.model_inputs)
        self.assertEqual(1, cache.disk_hits)
        self.assertEqual(3, len(children))
        self.assertEqual(
            [first["recordId"], second["recordId"], third["recordId"]],
            [child["parentRecordId"] for child in children],
        )

    def test_disk_cache_children_are_byte_exact_to_distinct_inference_reference(self) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]
        third = normalize_floss(self.payload, Path("third.exe"), self.classification, "3.1.1")[0]
        records = [
            {**first, "text": "repeated language text"},
            {**second, "text": "different language text"},
            {**third, "text": "repeated language text"},
        ]

        reference_translator = FakeTranslator()
        reference_children = add_translations(
            records,
            reference_translator,
            "en",
            8,
            4,
            200,
        )

        cached_translator = FakeTranslator()
        with tempfile.TemporaryDirectory() as directory:
            cache = RunLocalTranslationCache(Path(directory), "identity", 1)
            try:
                cached_children = list(
                    translate_normalized_records(
                        records,
                        cached_translator,
                        "en",
                        batch_size=1,
                        minimum_characters=4,
                        maximum_characters=200,
                        window_size=1,
                        include_parents=False,
                        run_cache=cache,
                        integrity_monitor=TranslationIntegrityMonitor(),
                    )
                )
                self.assertEqual(1, cache.disk_hits)
            finally:
                cache.close(commit=True)

        def serialized_jsonl(children: list[dict]) -> bytes:
            return b"".join(
                json.dumps(child, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"
                for child in children
            )

        self.assertCountEqual(
            ["repeated language text", "different language text"],
            [text for batch in reference_translator.calls for text in batch],
        )
        self.assertCountEqual(
            ["repeated language text", "different language text"],
            [text for batch in cached_translator.calls for text in batch],
        )
        self.assertEqual(serialized_jsonl(reference_children), serialized_jsonl(cached_children))

    def test_translation_cache_identity_changes_with_output_affecting_settings(self) -> None:
        translator = FakeTranslator()
        base = translation_cache_identity(
            translator,
            "en",
            batch_size=8,
            max_input_tokens=512,
            max_new_tokens=512,
            strict_determinism=False,
        )
        changed = translation_cache_identity(
            translator,
            "fr",
            batch_size=8,
            max_input_tokens=512,
            max_new_tokens=512,
            strict_determinism=False,
        )

        self.assertNotEqual(base, changed)

    def test_translation_cache_identity_covers_every_output_affecting_field(self) -> None:
        base_translator = {
            "engine": "engine-a",
            "engine_version": "1.0",
            "model_id": "model-a",
            "revision": "revision-a",
            "model_sha256": "a" * 64,
            "execution_metadata": {
                "device": "cpu",
                "gpuLayers": "0",
                "decoding": "greedy-top1",
            },
        }
        base_options = {
            "batch_size": 8,
            "max_input_tokens": 512,
            "max_new_tokens": 256,
            "strict_determinism": False,
        }
        base = translation_cache_identity(
            SimpleNamespace(**base_translator),
            "en",
            **base_options,
        )
        cases = (
            ("engine", {"engine": "engine-b"}, {}, None, None),
            ("engine-version", {"engine_version": "2.0"}, {}, None, None),
            ("model", {"model_id": "model-b"}, {}, None, None),
            ("revision", {"revision": "revision-b"}, {}, None, None),
            ("model-hash", {"model_sha256": "b" * 64}, {}, None, None),
            (
                "prompt-version",
                {},
                {},
                "TRANSLATION_PROMPT_VERSION",
                "changed-prompt",
            ),
            (
                "preservation-policy",
                {},
                {},
                "TRANSLATION_PRESERVATION_POLICY_VERSION",
                "changed-policy",
            ),
            ("batch-size", {}, {"batch_size": 9}, None, None),
            ("max-input-tokens", {}, {"max_input_tokens": 513}, None, None),
            ("max-new-tokens", {}, {"max_new_tokens": 257}, None, None),
            ("strict-determinism", {}, {"strict_determinism": True}, None, None),
            (
                "execution-device",
                {"execution_metadata": {**base_translator["execution_metadata"], "device": "cuda"}},
                {},
                None,
                None,
            ),
            (
                "execution-offload",
                {
                    "execution_metadata": {
                        **base_translator["execution_metadata"],
                        "gpuLayers": "-1",
                    }
                },
                {},
                None,
                None,
            ),
            (
                "execution-decoding",
                {
                    "execution_metadata": {
                        **base_translator["execution_metadata"],
                        "decoding": "beam",
                    }
                },
                {},
                None,
                None,
            ),
        )

        for label, translator_changes, option_changes, constant_name, constant_value in cases:
            translator = SimpleNamespace(**(base_translator | translator_changes))
            options = base_options | option_changes
            context = (
                patch(f"bstrings_enrich.{constant_name}", constant_value)
                if constant_name is not None
                else nullcontext()
            )
            with self.subTest(label=label), context:
                changed = translation_cache_identity(translator, "en", **options)
                self.assertNotEqual(base, changed)

    @patch("bstrings_enrich.LlamaCppTranslator")
    def test_integrated_translation_reports_disk_plan_progress_stats_and_cleans_cache(
        self, translator_type
    ) -> None:
        first = normalize_floss(self.payload, Path("first.exe"), self.classification, "3.1.1")[0]
        second = normalize_floss(self.payload, Path("second.exe"), self.classification, "3.1.1")[0]

        class OneFallbackTranslator(FakeTranslator):
            def translate_attempts(
                self, texts: list[str], target_language: str
            ) -> list[TranslationAttempt]:
                self.calls.append(list(texts))
                return [TranslationAttempt(None, "synthetic-failure") for _ in texts]

        translator = OneFallbackTranslator()
        translator_type.return_value = translator
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_path = root / "candidates.jsonl"
            output_path = root / "translations.jsonl"
            stats_path = root / "translation-stats.json"
            model_path = root / "model.gguf"
            model_path.write_bytes(b"model")
            stats_path.write_text('{"preserved":true}\n', encoding="utf-8")
            input_path.write_text(
                "\n".join(json.dumps(record) for record in (first, second)) + "\n",
                encoding="utf-8",
            )
            stderr = io.StringIO()
            output_at_stats_publish: list[str] = []

            def publish_stats(path: Path, stats: TranslationWorkStats) -> None:
                output_at_stats_publish.append(output_path.read_text(encoding="utf-8"))
                write_translation_stats_atomic(path, stats)

            with (
                patch(
                    "bstrings_enrich.write_translation_stats_atomic",
                    side_effect=publish_stats,
                ),
                redirect_stderr(stderr),
            ):
                result = main(
                    [
                        "--input-jsonl",
                        str(input_path),
                        "--translate",
                        "--translations-only",
                        "--bounded-integrated-mode",
                        "--translation-model-path",
                        str(model_path),
                        "--translation-model-id",
                        translator.model_id,
                        "--translation-revision",
                        translator.revision,
                        "--translation-model-sha256",
                        translator.model_sha256,
                        "--translation-batch-size",
                        "1",
                        "--translation-cache-size",
                        "1",
                        "--translation-stats-output",
                        str(stats_path),
                        "--translation-window-size",
                        "1",
                        "--progress-total-records",
                        "2",
                        "-o",
                        str(output_path),
                    ]
                )

            self.assertEqual(0, result)
            self.assertEqual(1, len(output_at_stats_publish))
            self.assertEqual(output_path.read_text(encoding="utf-8"), output_at_stats_publish[0])
            self.assertEqual(2, len(output_path.read_text(encoding="utf-8").splitlines()))
            self.assertEqual([["language text"]], translator.calls)
            stats_text = stats_path.read_text(encoding="utf-8")
            self.assertEqual(
                {
                    "schemaVersion": 1,
                    "candidateOccurrences": 2,
                    "textDecisions": 2,
                    "protectedOnlyBypassTexts": 0,
                    "runCacheHits": 1,
                    "translationCacheHits": 0,
                    "translatorRequests": 1,
                    "translatorInputTexts": 1,
                    "modelResults": 1,
                    "modelFallbacks": 1,
                    "translatedChildOccurrences": 2,
                    "preservationFallbackChildOccurrences": 2,
                },
                json.loads(stats_text),
            )
            self.assertNotIn("language text", stats_text)
            self.assertNotIn(translator.model_sha256, stats_text)
            self.assertNotIn(first["recordId"], stats_text)
            log = stderr.getvalue()
            self.assertIn('"diskBacked":true', log)
            self.assertIn('"examinationLocal":true', log)
            self.assertIn('"commitBatchSize":4096', log)
            self.assertIn("Progress: offline translation: 0.0%", log)
            self.assertIn("Progress: offline translation: 100.0%", log)
            self.assertIn("rate=", log)
            self.assertIn("eta=", log)
            self.assertIn("Translation summary: cacheHits=1", log)
            final_progress = [
                line for line in log.splitlines() if "Progress: offline translation: 100.0%" in line
            ][-1]
            self.assertIn("cacheHits=1", final_progress)
            self.assertIn("modelInputs=1", final_progress)
            self.assertFalse(any(root.glob(".bstrings-translation-cache-*.sqlite3*")))

    def test_bounded_integrated_help_describes_exact_disk_and_bounded_recovery_caches(self) -> None:
        stdout = io.StringIO()
        with redirect_stdout(stdout), self.assertRaises(SystemExit) as raised:
            parse_arguments(["--help"])

        self.assertEqual(0, raised.exception.code)
        normalized_help = " ".join(stdout.getvalue().split()).replace("in- memory", "in-memory")
        self.assertIn(
            "input-JSONL translation uses run-local exact disk deduplication", normalized_help
        )
        self.assertIn("recovery uses bounded in-memory translation deduplication", normalized_help)

    @patch("bstrings_enrich.LlamaCppTranslator")
    def test_translation_stats_are_not_published_when_evidence_output_fails(
        self, translator_type
    ) -> None:
        translator = FakeTranslator()
        translator_type.return_value = translator
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_path = root / "candidates.jsonl"
            output_path = root / "translations.jsonl"
            stats_path = root / "translation-stats.json"
            model_path = root / "model.gguf"
            model_path.write_bytes(b"model")
            parent = normalize_floss(
                self.payload, Path("sample.exe"), self.classification, "3.1.1"
            )[0]
            input_path.write_text(json.dumps(parent) + "\n", encoding="utf-8")
            output_path.write_text("preserved output\n", encoding="utf-8")
            stats_path.write_text('{"preserved":true}\n', encoding="utf-8")

            with (
                patch(
                    "bstrings_enrich.write_jsonl_atomic",
                    side_effect=EnrichmentError("synthetic output failure"),
                ),
                patch("bstrings_enrich.write_translation_stats_atomic") as stats_writer,
                redirect_stderr(io.StringIO()),
            ):
                result = main(
                    [
                        "--input-jsonl",
                        str(input_path),
                        "--translate",
                        "--translations-only",
                        "--bounded-integrated-mode",
                        "--translation-model-path",
                        str(model_path),
                        "--translation-revision",
                        translator.revision,
                        "--translation-model-sha256",
                        translator.model_sha256,
                        "--translation-stats-output",
                        str(stats_path),
                        "-o",
                        str(output_path),
                    ]
                )

            self.assertEqual(2, result)
            stats_writer.assert_not_called()
            self.assertEqual("preserved output\n", output_path.read_text(encoding="utf-8"))
            self.assertEqual('{"preserved":true}\n', stats_path.read_text(encoding="utf-8"))
            self.assertEqual([], list(root.glob(".bstrings-translation-cache-*.sqlite3*")))

    def test_combined_recovery_translation_reuses_outcome_aware_fallback(self) -> None:
        class IdentifierBreakingTranslator(FakeTranslator):
            def translate(self, texts: list[str], target_language: str) -> list[str]:
                self.calls.append(list(texts))
                return ["translated account" for _ in texts]

        translator = IdentifierBreakingTranslator()
        payload = json.loads(json.dumps(self.payload))
        for category in payload["strings"]:
            payload["strings"][category] = []
        payload["strings"]["language_strings"] = [
            {"string": "cuenta analyst@example.com", "offset": 24, "encoding": "UTF-8"}
        ]

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            first = root / "first.exe"
            second = root / "second.exe"
            first.write_bytes(b"first")
            second.write_bytes(b"second")
            model_path = root / "model.gguf"
            model_path.write_bytes(b"model")
            output = root / "combined.jsonl"

            with (
                patch("bstrings_enrich.executable_path", side_effect=lambda command: command),
                patch("bstrings_enrich.tool_version", return_value="3.1.1"),
                patch("bstrings_enrich.classify_file", return_value=self.classification),
                patch("bstrings_enrich.run_floss", return_value=payload),
                patch("bstrings_enrich.LlamaCppTranslator", return_value=translator),
            ):
                result = main(
                    [
                        str(first),
                        str(second),
                        "--force-floss",
                        "--translate",
                        "--bounded-integrated-mode",
                        "--translation-model-path",
                        str(model_path),
                        "--translation-model-id",
                        translator.model_id,
                        "--translation-revision",
                        translator.revision,
                        "--translation-model-sha256",
                        translator.model_sha256,
                        "--translation-cache-size",
                        "1",
                        "-o",
                        str(output),
                    ]
                )

            self.assertEqual(0, result)
            self.assertEqual([["cuenta analyst@example.com"]], translator.calls)
            records = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
            children = [
                record
                for record in records
                if (record.get("transform") or {}).get("kind") == "translation"
            ]
            self.assertEqual(2, len(children))
            self.assertEqual(
                {"preservation-fallback"},
                {child["attributes"]["translationIntegrity"] for child in children},
            )

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

    def test_translation_stats_writer_preserves_prior_file_and_removes_partial_on_failure(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "translation-stats.json"
            output.write_text('{"preserved":true}\n', encoding="utf-8")
            stats = TranslationWorkStats()

            with (
                patch("bstrings_enrich.os.replace", side_effect=PermissionError("synthetic lock")),
                self.assertRaisesRegex(PermissionError, "synthetic lock"),
            ):
                write_translation_stats_atomic(output, stats)

            self.assertEqual('{"preserved":true}\n', output.read_text(encoding="utf-8"))
            self.assertEqual([], list(root.glob("translation-stats.json.partial.*")))

    def test_translation_stats_are_strictly_limited_to_input_jsonl_translation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            input_path = root / "input.jsonl"
            input_path.write_text("{}\n", encoding="utf-8")
            output_path = root / "output.jsonl"
            model_path = root / "model.gguf"
            model_path.write_bytes(b"model")
            stats_path = root / "stats.json"
            common = [
                "--input-jsonl",
                str(input_path),
                "--translate",
                "--translation-model-path",
                str(model_path),
                "--translation-revision",
                "revision",
                "--translation-model-sha256",
                "a" * 64,
                "-o",
                str(output_path),
            ]

            with self.assertRaisesRegex(EnrichmentError, "requires --input-jsonl and --translate"):
                validate_arguments(
                    parse_arguments(
                        [
                            "sample.exe",
                            "--translation-stats-output",
                            str(stats_path),
                            "-o",
                            str(output_path),
                        ]
                    )
                )

            for alias in (input_path, output_path, model_path):
                with (
                    self.subTest(alias=alias),
                    self.assertRaisesRegex(EnrichmentError, "must differ"),
                ):
                    validate_arguments(
                        parse_arguments(common + ["--translation-stats-output", str(alias)])
                    )

            stats_path.mkdir()
            with self.assertRaisesRegex(EnrichmentError, "physical file"):
                validate_arguments(
                    parse_arguments(common + ["--translation-stats-output", str(stats_path)])
                )

            hard_link = root / "input-hard-link.jsonl"
            try:
                os.link(input_path, hard_link)
            except OSError:
                hard_link = None
            if hard_link is not None:
                with self.assertRaisesRegex(EnrichmentError, "must differ"):
                    validate_arguments(
                        parse_arguments(common + ["--translation-stats-output", str(hard_link)])
                    )

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

            stderr = io.StringIO()
            with (
                patch(
                    "bstrings_enrich.normalize_floss",
                    side_effect=AssertionError("integrated recovery must stay streaming"),
                ),
                redirect_stderr(stderr),
            ):
                exit_code = main(
                    [
                        "--bounded-integrated-mode",
                        "--paths-from",
                        str(inventory),
                        "--progress-total-files",
                        "2",
                        "-o",
                        str(output),
                    ]
                )

            self.assertEqual(0, exit_code)
            self.assertIn(
                "Progress: Magika and FLOSS recovery: 0.0% (0/2 files)",
                stderr.getvalue(),
            )
            self.assertIn(
                "Progress: Magika and FLOSS recovery: 100.0% (2/2 files)",
                stderr.getvalue(),
            )
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

    @patch("bstrings_enrich.run_floss", side_effect=AssertionError("triage invoked FLOSS"))
    @patch("bstrings_enrich._run_magika_batch")
    @patch(
        "bstrings_enrich.sha256_file",
        side_effect=lambda path: (
            hashlib.sha256(Path(path).read_bytes()).hexdigest()
            if Path(path).is_file()
            else "b" * 64
        ),
    )
    @patch("bstrings_enrich.tool_version", return_value="magika 1.1.0 model")
    @patch("bstrings_enrich.executable_path", side_effect=lambda command: command)
    def test_triage_only_batches_and_routes_conservative_union(
        self,
        _executable_path_mock,
        tool_version_mock,
        _sha256_file_mock,
        magika_batch_mock,
        _run_floss_mock,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            pe = root / "extensionless"
            pe_bytes = bytearray(256)
            pe_bytes[0:2] = b"MZ"
            pe_bytes[60:64] = (128).to_bytes(4, "little")
            pe_bytes[128:132] = b"PE\x00\x00"
            pe.write_bytes(pe_bytes)
            image = root / "picture.bin"
            image.write_bytes(b"\x89PNG\r\n\x1a\nfixture")
            text = root / "ordinary.txt"
            text.write_text("ordinary text", encoding="utf-8")
            inputs = [pe, image, text]
            inventory = root / "inventory.txt"
            inventory.write_text(
                "".join(f"{path.resolve()}\n" for path in inputs), encoding="utf-8"
            )
            manifest = root / "input-manifest.jsonl"
            self.write_input_manifest(manifest, inputs)
            output = root / "content-routing.jsonl"
            magika_batch_mock.side_effect = lambda _magika, batch, _timeout: [
                self.unknown_magika() for _ in batch
            ]

            stderr = io.StringIO()
            with redirect_stderr(stderr):
                exit_code = main(
                    [
                        "--triage-only",
                        "--enable-floss",
                        "--enable-ocr",
                        "--paths-from",
                        str(inventory),
                        "--input-manifest",
                        str(manifest),
                        "--progress-total-files",
                        "3",
                        "-o",
                        str(output),
                    ]
                )

            self.assertEqual(0, exit_code)
            self.assertEqual(1, magika_batch_mock.call_count)
            tool_version_mock.assert_called_once()
            records = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
            self.assertEqual([1, 2, 3], [record["ordinal"] for record in records])
            self.assertEqual("content-routing-v1", records[0]["policyVersion"])
            self.assertRegex(records[0]["decisionId"], r"^sha256:[0-9a-f]{64}$")
            self.assertEqual("b" * 64, records[0]["classifier"]["executableSha256"])
            self.assertEqual(["floss", "native"], records[0]["scheduledRoutes"])
            self.assertIn("magic:pe", records[0]["signals"])
            self.assertEqual(["native", "ocr"], records[1]["scheduledRoutes"])
            self.assertIn("magic:png", records[1]["signals"])
            self.assertEqual(["native"], records[2]["scheduledRoutes"])
            self.assertIn("Progress: content triage: 0.0% (0/3 files)", stderr.getvalue())
            self.assertIn("Progress: content triage: 100.0% (3/3 files)", stderr.getvalue())

    def test_raw_magika_pe_prediction_routes_floss_but_pe_extension_alone_does_not(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "misleading.exe"
            source.write_bytes(b"ordinary text")
            identity = InputIdentity(
                str(source.resolve()),
                len(b"ordinary text"),
                hashlib.sha256(b"ordinary text").hexdigest(),
            )
            item = RoutingInput(source.resolve(), identity)
            unknown = self.unknown_magika().output
            raw_pe = Classification("pebin", 0.41, False, "application/x-dosexec", "executable")
            routed = _make_routing_record(
                item,
                MagikaClassification("ok", unknown, raw_pe),
                "magika 1.1.0 model",
                1,
                enable_floss=True,
                enable_ocr=False,
                force_floss=False,
                magika_runtime_path="C:/bundle/DirectML.dll",
                magika_runtime_sha256="c" * 64,
            )
            extension_only = _make_routing_record(
                item,
                self.unknown_magika(),
                "magika 1.1.0 model",
                1,
                enable_floss=True,
                enable_ocr=False,
                force_floss=False,
            )

            self.assertIn("floss", routed["scheduledRoutes"])
            self.assertIn("magika-raw:pebin", routed["signals"])
            self.assertEqual(
                {"path": "C:/bundle/DirectML.dll", "sha256": "c" * 64},
                routed["classifier"]["runtime"],
            )
            self.assertNotIn("floss", extension_only["scheduledRoutes"])
            self.assertIn("extension-content-disagreement:pe", extension_only["conflicts"])
            self.assertEqual(
                extension_only["decisionId"],
                _make_routing_record(
                    item,
                    self.unknown_magika(),
                    "magika 1.1.0 model",
                    1,
                    enable_floss=True,
                    enable_ocr=False,
                    force_floss=False,
                )["decisionId"],
            )

    def test_routing_manifest_rejects_tampered_decision_identity(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "sample.bin"
            source.write_bytes(b"sample")
            identity = InputIdentity(
                str(source.resolve()), 6, hashlib.sha256(b"sample").hexdigest()
            )
            route = _make_routing_record(
                RoutingInput(source.resolve(), identity),
                self.unknown_magika(),
                "1.1.0",
                1,
                enable_floss=False,
                enable_ocr=False,
                force_floss=False,
            )
            route["decisionId"] = "sha256:" + "0" * 64
            manifest = root / "content-routing.jsonl"
            manifest.write_text(json.dumps(route) + "\n", encoding="utf-8")

            with self.assertRaisesRegex(EnrichmentError, "decision identity"):
                list(read_routing_manifest(manifest))

    @patch("bstrings_enrich.subprocess.run")
    def test_magika_batch_uses_one_ordered_jsonl_process(self, run_mock) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inputs = []
            rows = []
            for index in range(100):
                source = root / f"sample-{index}.bin"
                source.write_bytes(b"sample")
                identity = InputIdentity(str(source.resolve()), 6, "a" * 64)
                inputs.append(RoutingInput(source.resolve(), identity))
                type_info = {
                    "label": "unknown",
                    "is_text": False,
                    "mime_type": "application/octet-stream",
                    "group": "unknown",
                }
                rows.append(
                    json.dumps(
                        {
                            "path": str(source.resolve()),
                            "result": {
                                "status": "ok",
                                "value": {"dl": type_info, "output": type_info, "score": 0.2},
                            },
                        }
                    )
                )
            run_mock.return_value = subprocess.CompletedProcess(
                ["magika"], 0, stdout="\n".join(rows) + "\n", stderr=""
            )

            batches = list(_iter_magika_batches(inputs, "magika.exe"))
            self.assertEqual(1, len(batches))
            classifications = _run_magika_batch("magika.exe", batches[0], 60)

            self.assertEqual(100, len(classifications))
            self.assertEqual(1, run_mock.call_count)
            command = run_mock.call_args.args[0]
            self.assertEqual(["magika.exe", "--jsonl", "--"], command[:3])
            self.assertEqual([str(item.path) for item in inputs], command[3:])

    @patch("bstrings_enrich.subprocess.run", side_effect=subprocess.TimeoutExpired("magika", 60))
    def test_magika_batch_timeout_fails_open_with_auditable_errors(self, _run_mock) -> None:
        item = RoutingInput(Path("sample.bin").resolve(), InputIdentity("sample.bin", 1, "a" * 64))
        classifications = _run_magika_batch("magika.exe", [item], 60)
        self.assertEqual("error", classifications[0].status)
        self.assertEqual("batch-timeout", classifications[0].error_code)

    @patch("bstrings_enrich.subprocess.run")
    def test_magika_nonzero_and_unbound_output_fail_open_with_auditable_errors(
        self, run_mock
    ) -> None:
        item = RoutingInput(Path("sample.bin").resolve(), InputIdentity("sample.bin", 1, "a" * 64))
        run_mock.return_value = subprocess.CompletedProcess(
            ["magika"], 17, stdout="", stderr="crashed"
        )
        classifications = _run_magika_batch("magika.exe", [item], 60)
        self.assertEqual("error", classifications[0].status)
        self.assertEqual("batch-exit-17", classifications[0].error_code)

        run_mock.return_value = subprocess.CompletedProcess(
            ["magika"], 0, stdout=json.dumps({"path": "different", "result": {}}), stderr=""
        )
        classifications = _run_magika_batch("magika.exe", [item], 60)
        self.assertEqual("error", classifications[0].status)
        self.assertEqual("invalid-row-content", classifications[0].error_code)

    def test_classifier_error_still_uses_magic_and_force_routes_are_explicit(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "extensionless"
            pe_bytes = bytearray(256)
            pe_bytes[0:2] = b"MZ"
            pe_bytes[60:64] = (128).to_bytes(4, "little")
            pe_bytes[128:132] = b"PE\x00\x00"
            source.write_bytes(pe_bytes)
            item = RoutingInput(
                source.resolve(),
                InputIdentity(
                    str(source.resolve()), len(pe_bytes), hashlib.sha256(pe_bytes).hexdigest()
                ),
            )
            failed = _make_routing_record(
                item,
                self.unknown_magika("error"),
                "magika 1.1.0 model",
                1,
                enable_floss=True,
                enable_ocr=False,
                force_floss=False,
            )
            forced = _make_routing_record(
                item,
                self.unknown_magika(),
                "magika 1.1.0 model",
                1,
                enable_floss=True,
                enable_ocr=False,
                force_floss=True,
            )
            self.assertEqual("error", failed["classifier"]["status"])
            self.assertIn("floss", failed["scheduledRoutes"])
            self.assertIn("magic:pe", failed["signals"])
            self.assertIn("user-force:floss", forced["signals"])

    def test_large_dos_stub_pe_signature_still_routes_floss(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "extensionless"
            pe_offset = 16 * 1024 * 1024 + 4_096
            with source.open("wb") as handle:
                handle.write(b"MZ")
                handle.seek(60)
                handle.write(pe_offset.to_bytes(4, "little"))
                handle.seek(pe_offset)
                handle.write(b"PE\x00\x00")
            identity = InputIdentity(
                str(source.resolve()),
                source.stat().st_size,
                hashlib.sha256(source.read_bytes()).hexdigest(),
            )
            route = _make_routing_record(
                RoutingInput(source.resolve(), identity),
                self.unknown_magika("error"),
                "unavailable",
                1,
                enable_floss=True,
                enable_ocr=False,
                force_floss=False,
            )

            self.assertIn("magic:pe", route["signals"])
            self.assertIn("floss", route["scheduledRoutes"])

    def test_pe_pdf_polyglot_routes_both_specialists_monotonically(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "polyglot.bin"
            content = bytearray(1_024)
            content[0:2] = b"MZ"
            content[60:64] = (128).to_bytes(4, "little")
            content[128:132] = b"PE\x00\x00"
            content[512:517] = b"%PDF-"
            source.write_bytes(content)
            identity = InputIdentity(
                str(source.resolve()), len(content), hashlib.sha256(content).hexdigest()
            )
            route = _make_routing_record(
                RoutingInput(source.resolve(), identity),
                self.unknown_magika(),
                "1.1.0",
                1,
                enable_floss=True,
                enable_ocr=True,
                force_floss=False,
            )

            self.assertEqual(["floss", "native", "ocr"], route["scheduledRoutes"])
            self.assertIn("magic:pe", route["signals"])
            self.assertIn("probe:embedded-pdf-header", route["signals"])
            self.assertIn("nonzero-pdf-header", route["conflicts"])

    @unittest.skipUnless(os.name == "nt", "Windows sharing leases are release-platform specific")
    def test_evidence_read_lease_denies_write_and_delete_sharing(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "evidence.bin"
            alias = root / "hard-link.bin"
            replacement = root / "replacement.bin"
            source.write_bytes(b"original")
            os.link(source, alias)
            replacement.write_bytes(b"modified")
            with EvidenceReadLease(source):
                with self.assertRaises(OSError):
                    source.write_bytes(b"modified")
                with self.assertRaises(OSError):
                    alias.write_bytes(b"modified")
                with self.assertRaises(OSError):
                    source.unlink()
                with self.assertRaises(OSError):
                    os.replace(replacement, source)
            source.write_bytes(b"modified")
            self.assertEqual(b"modified", source.read_bytes())

    @patch("bstrings_enrich._run_magika_batch", side_effect=EnrichmentError("malformed"))
    @patch(
        "bstrings_enrich.sha256_file",
        side_effect=lambda path: (
            hashlib.sha256(Path(path).read_bytes()).hexdigest()
            if Path(path).is_file()
            else "b" * 64
        ),
    )
    @patch("bstrings_enrich.tool_version", return_value="magika 1.1.0 model")
    @patch("bstrings_enrich.executable_path", side_effect=lambda command: command)
    def test_unexpected_batch_failure_emits_fail_open_route_and_replaces_output_atomically(
        self, _executable_path_mock, _tool_version_mock, _sha256_file_mock, _batch_mock
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "sample.bin"
            source.write_bytes(b"sample")
            inventory = root / "inventory.txt"
            inventory.write_text(f"{source.resolve()}\n", encoding="utf-8")
            manifest = root / "input-manifest.jsonl"
            self.write_input_manifest(manifest, [source])
            output = root / "content-routing.jsonl"
            output.write_text("preserved\n", encoding="utf-8")

            exit_code = main(
                [
                    "--triage-only",
                    "--paths-from",
                    str(inventory),
                    "--input-manifest",
                    str(manifest),
                    "--progress-total-files",
                    "1",
                    "-o",
                    str(output),
                ]
            )

            self.assertEqual(0, exit_code)
            records = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
            self.assertEqual(1, len(records))
            self.assertEqual("error", records[0]["classifier"]["status"])
            self.assertEqual("batch-classification-error", records[0]["classifier"]["errorCode"])
            self.assertEqual(["native"], records[0]["scheduledRoutes"])

    @patch("bstrings_enrich.classify_file", side_effect=AssertionError("Magika was relaunched"))
    @patch("bstrings_enrich.run_floss")
    @patch("bstrings_enrich.tool_version", return_value="floss 3.1.1")
    @patch("bstrings_enrich.executable_path", side_effect=lambda command: command)
    def test_recovery_reuses_routing_manifest_without_relaunching_magika(
        self,
        _executable_path_mock,
        tool_version_mock,
        run_floss_mock,
        _classify_file_mock,
    ) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "sample.exe"
            pe_bytes = bytearray(256)
            pe_bytes[0:2] = b"MZ"
            pe_bytes[60:64] = (128).to_bytes(4, "little")
            pe_bytes[128:132] = b"PE\x00\x00"
            source.write_bytes(pe_bytes)
            identity = InputIdentity(
                str(source.resolve()), len(pe_bytes), hashlib.sha256(pe_bytes).hexdigest()
            )
            route = _make_routing_record(
                RoutingInput(source.resolve(), identity),
                self.unknown_magika(),
                "magika 1.1.0 model",
                1,
                enable_floss=True,
                enable_ocr=False,
                force_floss=False,
            )
            routing_manifest = root / "content-routing.jsonl"
            routing_manifest.write_text(json.dumps(route) + "\n", encoding="utf-8")
            inventory = root / "floss-inputs.txt"
            inventory.write_text(f"{source.resolve()}\n", encoding="utf-8")
            output = root / "recovered.jsonl"
            run_floss_mock.return_value = self.payload

            stderr = io.StringIO()
            with redirect_stderr(stderr):
                exit_code = main(
                    [
                        "--bounded-integrated-mode",
                        "--paths-from",
                        str(inventory),
                        "--routing-manifest",
                        str(routing_manifest),
                        "--progress-total-files",
                        "1",
                        "-o",
                        str(output),
                    ]
                )

            self.assertEqual(0, exit_code)
            tool_version_mock.assert_called_once_with("floss")
            run_floss_mock.assert_called_once()
            self.assertIn("Progress: FLOSS recovery: 100.0% (1/1 files)", stderr.getvalue())
            records = [json.loads(line) for line in output.read_text(encoding="utf-8").splitlines()]
            self.assertTrue(records)
            self.assertEqual(
                {route["decisionId"]},
                {record["attributes"]["routeDecisionId"] for record in records},
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
            translator._evidence_inference_started = False
            translator.parallelism = 1
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

        translator, _ = translator_for(
            {
                "choices": [
                    {
                        "finish_reason": "length",
                        "message": {"content": "rejected partial output"},
                    }
                ]
            }
        )
        attempts = translator.translate_attempts(["synthetic source"], "en")
        self.assertEqual(
            [TranslationAttempt(None, "generation-limit-reached")],
            attempts,
        )

        class TimeoutOpener:
            def open(self, request, timeout):
                raise TimeoutError("synthetic timeout")

        translator, _ = translator_for({})
        translator._thread_state = SimpleNamespace(opener=TimeoutOpener())
        attempts = translator.translate_attempts(["synthetic source"], "en")
        self.assertEqual(
            [TranslationAttempt(None, "translation-request-failure")],
            attempts,
        )

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
        self.assertEqual("4", command[command.index("-lv") + 1])
        self.assertEqual("16", command[command.index("--threads") + 1])

    def test_cuda_log_validation_requires_and_records_complete_layer_offload(self) -> None:
        log = """
load_tensors: offloaded 33/33 layers to GPU
load_tensors: CPU_Mapped model buffer size = 410.69 MiB
load_tensors: CUDA0 model buffer size = 4403.24 MiB
llama_kv_cache: CUDA0 KV buffer size = 512.00 MiB
sched_reserve: CUDA0 compute buffer size = 110.01 MiB
sched_reserve: CUDA_Host compute buffer size = 18.01 MiB
"""
        with tempfile.TemporaryDirectory() as directory:
            translator = object.__new__(LlamaCppTranslator)
            translator._stderr_path = Path(directory) / "stderr.log"
            translator._stderr_path.write_text(log, encoding="utf-8")
            translator._stderr_handle = None

            metadata = translator._runtime_placement_metadata(require_full_cuda=True)

        self.assertTrue(metadata["fullLayerOffloadValidated"])
        self.assertFalse(metadata["cudaAllocationFallbackObserved"])
        self.assertEqual(33, metadata["observedGpuLayers"])
        self.assertEqual(33, metadata["observedTotalLayers"])
        self.assertEqual(410.69, metadata["hostModelBufferMiB"])
        self.assertEqual(4403.24, metadata["gpuModelBufferMiB"])

    def test_cuda_log_validation_rejects_partial_or_allocation_fallback(self) -> None:
        cases = (
            "load_tensors: offloaded 28/33 layers to GPU\n",
            (
                "load_tensors: offloaded 33/33 layers to GPU\n"
                "ggml_cuda: failed to allocate device memory\n"
            ),
        )
        for log in cases:
            with self.subTest(log=log), tempfile.TemporaryDirectory() as directory:
                translator = object.__new__(LlamaCppTranslator)
                translator._stderr_path = Path(directory) / "stderr.log"
                translator._stderr_path.write_text(log, encoding="utf-8")
                translator._stderr_handle = None
                with self.assertRaisesRegex(EnrichmentError, "complete model-layer offload"):
                    translator._runtime_placement_metadata(require_full_cuda=True)

    def test_cuda_device_description_excludes_volatile_free_memory(self) -> None:
        devices = "CUDA0: Synthetic GPU (8187 MiB, 7109 MiB free)\n"

        self.assertEqual(
            "Synthetic GPU (8187 MiB)",
            LlamaCppTranslator._cuda_device_description(devices, "CUDA0"),
        )

    @patch("bstrings_enrich.run_checked")
    @patch("bstrings_enrich.shutil.which", return_value="C:/driver/nvidia-smi.exe")
    def test_cuda_provenance_includes_available_driver_and_compute_capability(
        self, _which_mock, run_checked_mock
    ) -> None:
        run_checked_mock.return_value = subprocess.CompletedProcess(
            [],
            0,
            "0, 610.62, 8.9, 8187, 7109\n1, 610.62, 8.6, 8192, 7000\n",
            "",
        )

        metadata = LlamaCppTranslator._nvidia_device_metadata("CUDA0")

        self.assertEqual("610.62", metadata["gpuDriverVersion"])
        self.assertEqual("8.9", metadata["gpuComputeCapability"])
        self.assertEqual(8187, metadata["gpuMemoryTotalMiB"])
        self.assertEqual(7109, metadata["gpuMemoryFreePreflightMiB"])

        run_checked_mock.return_value = subprocess.CompletedProcess(
            [],
            0,
            "0, 610.62, 8.9, malformed, 7109\n",
            "",
        )
        malformed = LlamaCppTranslator._nvidia_device_metadata("CUDA0")
        self.assertEqual("8.9", malformed["gpuComputeCapability"])
        self.assertNotIn("gpuMemoryTotalMiB", malformed)
        self.assertNotIn("gpuMemoryFreePreflightMiB", malformed)

    @patch(
        "bstrings_enrich.LlamaCppTranslator._nvidia_device_metadata",
        return_value={
            "gpuDriverVersion": "610.62",
            "gpuComputeCapability": "8.9",
            "gpuMemoryTotalMiB": 8187,
            "gpuMemoryFreePreflightMiB": 7109,
        },
    )
    @patch("bstrings_enrich.LlamaCppTranslator._start_preflighted_runtime")
    @patch("bstrings_enrich.run_checked")
    @patch("bstrings_enrich.executable_path", return_value="C:/runtime/llama-server.exe")
    @patch("bstrings_enrich.sha256_file", return_value="a" * 64)
    def test_auto_cuda_preflight_falls_back_to_cpu_before_evidence(
        self,
        _sha256_mock,
        _executable_mock,
        run_checked_mock,
        start_runtime_mock,
        _device_metadata_mock,
    ) -> None:
        run_checked_mock.side_effect = (
            subprocess.CompletedProcess([], 0, "version b10248", ""),
            subprocess.CompletedProcess(
                [],
                0,
                "CUDA0: Synthetic GPU (8187 MiB, 7109 MiB free)",
                "",
            ),
        )
        start_runtime_mock.side_effect = (
            EnrichmentError("complete model-layer offload was not proven"),
            None,
        )
        with tempfile.TemporaryDirectory() as directory:
            model = Path(directory) / "model.gguf"
            model.write_bytes(b"synthetic model")
            translator = LlamaCppTranslator(
                "llama-server",
                model,
                "synthetic/model",
                "revision",
                "a" * 64,
                "auto",
                16,
                16,
                30,
                30,
            )
            translator.close()

        self.assertEqual(2, start_runtime_mock.call_count)
        self.assertEqual("cuda", start_runtime_mock.call_args_list[0].kwargs["actual_device"])
        self.assertEqual("all", start_runtime_mock.call_args_list[0].kwargs["gpu_layers"])
        self.assertTrue(start_runtime_mock.call_args_list[0].kwargs["require_full_cuda"])
        self.assertEqual("cpu", start_runtime_mock.call_args_list[1].kwargs["actual_device"])
        self.assertEqual("0", start_runtime_mock.call_args_list[1].kwargs["gpu_layers"])
        self.assertFalse(start_runtime_mock.call_args_list[1].kwargs["require_full_cuda"])

    @patch(
        "bstrings_enrich.LlamaCppTranslator._nvidia_device_metadata",
        return_value={
            "gpuDriverVersion": "610.62",
            "gpuComputeCapability": "8.9",
            "gpuMemoryTotalMiB": 8187,
            "gpuMemoryFreePreflightMiB": 7109,
        },
    )
    @patch("bstrings_enrich.LlamaCppTranslator._start_preflighted_runtime")
    @patch("bstrings_enrich.run_checked")
    @patch("bstrings_enrich.executable_path", return_value="C:/runtime/llama-server.exe")
    @patch("bstrings_enrich.sha256_file", return_value="a" * 64)
    def test_explicit_cuda_preflight_failure_is_closed_without_cpu_retry(
        self,
        _sha256_mock,
        _executable_mock,
        run_checked_mock,
        start_runtime_mock,
        _device_metadata_mock,
    ) -> None:
        run_checked_mock.side_effect = (
            subprocess.CompletedProcess([], 0, "version b10248", ""),
            subprocess.CompletedProcess([], 0, "CUDA0: Synthetic GPU", ""),
        )
        start_runtime_mock.side_effect = EnrichmentError("synthetic CUDA startup failure")
        with tempfile.TemporaryDirectory() as directory:
            model = Path(directory) / "model.gguf"
            model.write_bytes(b"synthetic model")
            with self.assertRaisesRegex(EnrichmentError, "synthetic CUDA startup failure"):
                LlamaCppTranslator(
                    "llama-server",
                    model,
                    "synthetic/model",
                    "revision",
                    "a" * 64,
                    "cuda",
                    16,
                    16,
                    30,
                    30,
                )

        start_runtime_mock.assert_called_once()
        self.assertEqual("cuda", start_runtime_mock.call_args.kwargs["actual_device"])

    def test_cuda_compute_capability_policy_routes_before_runtime_start(self) -> None:
        def construct(
            device: str, metadata: dict[str, object]
        ) -> tuple[list, BaseException | None]:
            with tempfile.TemporaryDirectory() as directory:
                model = Path(directory) / "model.gguf"
                model.write_bytes(b"synthetic model")
                with (
                    patch("bstrings_enrich.sha256_file", return_value="a" * 64),
                    patch(
                        "bstrings_enrich.executable_path",
                        return_value="C:/runtime/llama-server.exe",
                    ),
                    patch("bstrings_enrich.run_checked") as run_checked_mock,
                    patch(
                        "bstrings_enrich.LlamaCppTranslator._nvidia_device_metadata",
                        return_value=metadata,
                    ),
                    patch(
                        "bstrings_enrich.LlamaCppTranslator._start_preflighted_runtime"
                    ) as start_runtime_mock,
                    redirect_stderr(io.StringIO()),
                ):
                    run_checked_mock.side_effect = (
                        subprocess.CompletedProcess([], 0, "version b10248", ""),
                        subprocess.CompletedProcess([], 0, "CUDA0: Synthetic GPU", ""),
                    )
                    error: BaseException | None = None
                    try:
                        translator = LlamaCppTranslator(
                            "llama-server",
                            model,
                            "synthetic/model",
                            "revision",
                            "a" * 64,
                            device,
                            16,
                            16,
                            30,
                            30,
                        )
                    except BaseException as exc:
                        error = exc
                    else:
                        translator.close()
                    return list(start_runtime_mock.call_args_list), error

        auto_cases = (
            (
                "accepted-sm89",
                {
                    "gpuDriverVersion": "610.62",
                    "gpuComputeCapability": "8.9",
                    "gpuMemoryTotalMiB": 8187,
                    "gpuMemoryFreePreflightMiB": 7109,
                },
                "cuda",
                None,
            ),
            (
                "accepted-free-memory-boundary",
                {
                    "gpuDriverVersion": "610.62",
                    "gpuComputeCapability": "8.9",
                    "gpuMemoryTotalMiB": 8187,
                    "gpuMemoryFreePreflightMiB": 7000,
                },
                "cuda",
                None,
            ),
            (
                "rejected-below-free-memory-boundary",
                {
                    "gpuDriverVersion": "610.62",
                    "gpuComputeCapability": "8.9",
                    "gpuMemoryTotalMiB": 8187,
                    "gpuMemoryFreePreflightMiB": 6999,
                },
                "cpu",
                "cuda-insufficient-free-memory",
            ),
            (
                "malformed-free-memory",
                {
                    "gpuDriverVersion": "610.62",
                    "gpuComputeCapability": "8.9",
                    "gpuMemoryTotalMiB": 8187,
                    "gpuMemoryFreePreflightMiB": "unknown",
                },
                "cpu",
                "cuda-free-memory-unavailable",
            ),
            (
                "unaccepted-sm86",
                {
                    "gpuDriverVersion": "610.62",
                    "gpuComputeCapability": "8.6",
                    "gpuMemoryTotalMiB": 8192,
                    "gpuMemoryFreePreflightMiB": 7000,
                },
                "cpu",
                "cuda-compute-capability-not-accepted",
            ),
            (
                "unknown-capability",
                {},
                "cpu",
                "cuda-compute-capability-unavailable",
            ),
        )
        for label, metadata, expected_device, expected_fallback in auto_cases:
            with self.subTest(label=label):
                calls, error = construct("auto", metadata)
                self.assertIsNone(error)
                self.assertEqual(1, len(calls))
                self.assertEqual(expected_device, calls[0].kwargs["actual_device"])
                self.assertEqual(expected_fallback, calls[0].kwargs.get("auto_cuda_failure"))
                self.assertEqual(
                    7000,
                    calls[0].kwargs["cuda_device_metadata"]["gpuMinimumFreeMemoryMiB"],
                )

        calls, error = construct(
            "cuda",
            {"gpuDriverVersion": "610.62", "gpuComputeCapability": "8.6"},
        )
        self.assertEqual([], calls)
        self.assertIsInstance(error, EnrichmentError)
        self.assertRegex(str(error), "accepted compute capability.*8.6")

        calls, error = construct("cuda", {})
        self.assertEqual([], calls)
        self.assertIsInstance(error, EnrichmentError)
        self.assertRegex(str(error), "accepted compute capability.*unavailable")

        calls, error = construct(
            "cuda",
            {
                "gpuDriverVersion": "610.62",
                "gpuComputeCapability": "8.9",
                "gpuMemoryTotalMiB": 8187,
                "gpuMemoryFreePreflightMiB": 6999,
            },
        )
        self.assertEqual([], calls)
        self.assertIsInstance(error, EnrichmentError)
        self.assertRegex(str(error), "at least 7000 MiB.*6999 MiB")

        calls, error = construct(
            "cuda",
            {
                "gpuDriverVersion": "610.62",
                "gpuComputeCapability": "8.9",
                "gpuMemoryTotalMiB": 8187,
                "gpuMemoryFreePreflightMiB": "unknown",
            },
        )
        self.assertEqual([], calls)
        self.assertIsInstance(error, EnrichmentError)
        self.assertRegex(str(error), "at least 7000 MiB.*unknown MiB")

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
