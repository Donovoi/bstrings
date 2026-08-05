from __future__ import annotations

import json
import sys
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from benchmark_translation import (  # noqa: E402
    DEFAULT_MAX_INPUT_TOKENS,
    DEFAULT_MAX_SOURCE_CHARACTERS,
    TRANSLATEGEMMA_MODEL_ID,
    WMT_DIRECTION_EN_TO_LOCALE,
    WMT_DIRECTION_LOCALE_TO_EN,
    BenchmarkError,
    Case,
    ExpectedHit,
    GoogleCloudBackend,
    OpenAIChatBackend,
    TranslateGemmaBackend,
    corpus_metadata,
    expected_hit_score,
    load_forensic_cases,
    load_wmt_cases,
    run_cases,
    sha256_directory,
    validate_arguments,
    validate_expected_hit_fixture,
    validate_google_corpus,
    validate_source_capacity,
)


class FakeBackend:
    batch_size = 2

    def translate(self, texts: list[str], source_language: str, target_language: str) -> list[str]:
        del source_language, target_language
        return [text.replace("contraseña", "password") for text in texts]


class RecordingBackend:
    batch_size = 8

    def __init__(self, outputs: list[str] | None = None) -> None:
        self.outputs = outputs
        self.seen: list[str] = []

    def translate(self, texts: list[str], source_language: str, target_language: str) -> list[str]:
        del source_language, target_language
        self.seen.extend(texts)
        return list(self.outputs) if self.outputs is not None else list(texts)


class FakeResponse:
    def __init__(self, value: dict[str, object]) -> None:
        self._payload = json.dumps(value).encode("utf-8")

    def __enter__(self) -> FakeResponse:
        self._offset = 0
        return self

    def __exit__(self, *_: object) -> None:
        return None

    def read(self, size: int = -1) -> bytes:
        if size < 0:
            result = self._payload[self._offset :]
            self._offset = len(self._payload)
            return result
        result = self._payload[self._offset : self._offset + size]
        self._offset += len(result)
        return result


class FakeMatrix:
    def __init__(self, rows: list[list[int]]) -> None:
        self.rows = rows
        self.shape = (len(rows), len(rows[0]) if rows else 0)

    def __len__(self) -> int:
        return len(self.rows)

    def __iter__(self):
        return iter(self.rows)

    def __getitem__(self, key):
        if isinstance(key, tuple):
            row_key, column_key = key
            selected_rows = self.rows[row_key]
            if selected_rows and isinstance(selected_rows[0], int):
                selected_rows = [selected_rows]
            return FakeMatrix([row[column_key] for row in selected_rows])
        return self.rows[key]


class FakeEncoded(dict):
    def to(self, device: str) -> FakeEncoded:
        self["moved_to"] = device
        return self


class FakeProcessor:
    chat_template = "official"

    def __init__(self) -> None:
        self.conversations = None
        self.template_arguments = None

    def apply_chat_template(self, conversations, **arguments):
        self.conversations = conversations
        self.template_arguments = arguments
        return FakeEncoded(
            input_ids=FakeMatrix([[1, 2, 0] for _ in conversations]),
            attention_mask=FakeMatrix([[1, 1, 0] for _ in conversations]),
        )

    def batch_decode(self, sequences, skip_special_tokens: bool):
        self.decoded = (sequences, skip_special_tokens)
        return ["translated" for _ in sequences]


class FakeModel:
    def __init__(self) -> None:
        self.config = SimpleNamespace(max_position_embeddings=4096)
        self.generation_config = SimpleNamespace(eos_token_id=2)
        self.device = "cpu"
        self.generate_arguments = None

    def generate(self, **arguments):
        self.generate_arguments = arguments
        rows = [row + [10, 2] for row in arguments["input_ids"].rows]
        return SimpleNamespace(sequences=FakeMatrix(rows))


class FailingFakeModel(FakeModel):
    def generate(self, **arguments):
        del arguments
        raise RuntimeError("synthetic kernel failure")


class FakeInferenceMode:
    def __enter__(self):
        return self

    def __exit__(self, *_: object) -> None:
        return None


class FakeTorch:
    __version__ = "test-torch"
    version = SimpleNamespace(cuda=None)

    @staticmethod
    def inference_mode() -> FakeInferenceMode:
        return FakeInferenceMode()


class TranslationBenchmarkTests(unittest.TestCase):
    def test_forensic_cases_require_expected_schema(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "cases.jsonl"
            path.write_text(
                json.dumps(
                    {
                        "id": "test",
                        "language": "es_MX",
                        "source": "contraseña analyst@example.com",
                        "reference": "password analyst@example.com",
                        "identifiers": ["analyst@example.com"],
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            cases = load_forensic_cases(path)

        self.assertEqual("test", cases[0].case_id)
        self.assertEqual(("analyst@example.com",), cases[0].identifiers)

    def test_wmt_loader_reverses_to_english_and_skips_bad_source(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "en-es_MX.jsonl"
            rows = [
                {
                    "is_bad_source": True,
                    "source": "bad English",
                    "target": "mal español",
                },
                {
                    "is_bad_source": False,
                    "source": "English reference",
                    "target": "fuente española",
                },
            ]
            path.write_text(
                "".join(json.dumps(row) + "\n" for row in rows),
                encoding="utf-8",
            )

            cases = load_wmt_cases(Path(directory), ["es_MX"], 1)

        self.assertEqual("fuente española", cases[0].source)
        self.assertEqual("English reference", cases[0].reference)
        self.assertEqual("es", cases[0].source_language)
        self.assertEqual("en", cases[0].target_language)
        self.assertEqual(WMT_DIRECTION_LOCALE_TO_EN, cases[0].corpus_direction)
        self.assertEqual("public", cases[0].corpus_classification)

    def test_wmt_loader_can_measure_original_english_to_locale(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "en-es_MX.jsonl"
            path.write_text(
                json.dumps(
                    {
                        "source": "Original English",
                        "target": "Edición española",
                        "domain": "news",
                        "document_id": "doc-1",
                        "segment_id": "segment-1",
                    }
                )
                + "\n",
                encoding="utf-8",
            )

            cases = load_wmt_cases(
                Path(directory),
                ["es_MX"],
                1,
                WMT_DIRECTION_EN_TO_LOCALE,
                seed=17,
            )

        self.assertEqual("Original English", cases[0].source)
        self.assertEqual("Edición española", cases[0].reference)
        self.assertEqual("en", cases[0].source_language)
        self.assertEqual("es", cases[0].target_language)
        self.assertEqual("news", cases[0].domain)

    def test_wmt_selection_is_seeded_and_domain_document_stratified(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "en-de_DE.jsonl"
            rows = [
                {
                    "source": f"English {index}",
                    "target": f"Deutsch {index}",
                    "domain": domain,
                    "document_id": f"{domain}-doc-{index % 2}",
                    "segment_id": f"segment-{index}",
                }
                for index, domain in enumerate(["news", "legal"] * 4)
            ]
            path.write_text("".join(json.dumps(row) + "\n" for row in rows), encoding="utf-8")

            first = load_wmt_cases(Path(directory), ["de_DE"], 4, seed=99)
            second = load_wmt_cases(Path(directory), ["de_DE"], 4, seed=99)
            metadata = corpus_metadata(first, [path], 99)

        self.assertEqual([case.case_id for case in first], [case.case_id for case in second])
        self.assertEqual({"legal": 2, "news": 2}, metadata["distribution"]["domain"])
        self.assertEqual(99, metadata["selection"]["seed"])
        self.assertEqual(64, len(metadata["selectedCaseSha256"]))
        self.assertEqual(64, len(metadata["inputFiles"][0]["sha256"]))

    def test_identifier_retention_is_measured_exactly(self) -> None:
        cases = [
            Case(
                case_id="one",
                suite="forensic",
                language="es_MX",
                source="contraseña analyst@example.com",
                reference="password analyst@example.com",
                identifiers=("analyst@example.com",),
            ),
            Case(
                case_id="two",
                suite="forensic",
                language="es_MX",
                source="contraseña 192.0.2.1",
                reference="password 192.0.2.1",
                identifiers=("192.0.2.1",),
            ),
        ]

        results = run_cases(FakeBackend(), cases, progress=False)

        self.assertEqual(
            [("analyst@example.com",), ("192.0.2.1",)],
            [result.retained_identifiers for result in results],
        )
        self.assertTrue(all(result.latency_seconds >= 0 for result in results))

    def test_sources_are_never_truncated_and_2048_characters_are_supported(self) -> None:
        source = "界" * DEFAULT_MAX_SOURCE_CHARACTERS
        case = Case(
            case_id="capacity",
            suite="forensic",
            language="ja_JP",
            source=source,
            reference=source,
            identifiers=(),
            source_language="ja",
            target_language="en",
        )
        backend = RecordingBackend()

        capacity = validate_source_capacity([case], DEFAULT_MAX_SOURCE_CHARACTERS)
        results = run_cases(backend, [case], progress=False)

        self.assertEqual(DEFAULT_MAX_INPUT_TOKENS, 4096)
        self.assertEqual(DEFAULT_MAX_SOURCE_CHARACTERS, capacity["observedMaximumCharacters"])
        self.assertEqual([source], backend.seen)
        self.assertEqual(source, results[0].hypothesis)

        oversized = Case(
            case_id="oversized",
            suite="forensic",
            language="ja_JP",
            source=source + "界",
            reference=source,
            identifiers=(),
        )
        with self.assertRaisesRegex(BenchmarkError, "source was not truncated"):
            validate_source_capacity([oversized], DEFAULT_MAX_SOURCE_CHARACTERS)

    def test_invariant_deltas_and_fixture_owned_regex_scoring(self) -> None:
        case = Case(
            case_id="integrity",
            suite="forensic",
            language="en",
            source="ID ID DROP",
            reference="irrelevant",
            identifiers=("ID", "DROP", "NEW"),
            source_language="en",
            target_language="en",
            expected_hits=(ExpectedHit("token", r"token:(\w+)", ("alpha", "beta")),),
        )
        backend = RecordingBackend(["ID ID ID NEW token:alpha token:extra"])

        result = run_cases(backend, [case], progress=False)[0]
        score = expected_hit_score([result])

        deltas = {delta.identifier: delta for delta in result.invariant_deltas}
        self.assertEqual(1, deltas["ID"].duplicate_count)
        self.assertEqual(1, deltas["DROP"].omitted_count)
        self.assertEqual(1, deltas["NEW"].added_count)
        self.assertEqual(1, score["truePositives"])
        self.assertEqual(1, score["falsePositives"])
        self.assertEqual(1, score["falseNegatives"])

    def test_bundled_forensic_fixture_exercises_downstream_patterns(self) -> None:
        cases = load_forensic_cases(
            Path(__file__).resolve().parents[1] / "forensic_translation_cases.jsonl"
        )
        expected_hits = [hit for case in cases for hit in case.expected_hits]

        self.assertGreaterEqual(len(expected_hits), 10)
        self.assertEqual(
            {"cve", "email", "guid", "ipv4", "sha256", "url3986"},
            {hit.pattern_id for hit in expected_hits},
        )
        for line_number, case in enumerate(cases, start=1):
            validate_expected_hit_fixture(case, line_number=line_number)

    def test_google_cloud_guard_rejects_private_or_unacknowledged_rows(self) -> None:
        public = Case("public", "wmt24pp", "de_DE", "a", "b", (), corpus_classification="public")
        private = Case(
            "private", "forensic", "de_DE", "a", "b", (), corpus_classification="restricted"
        )

        with self.assertRaisesRegex(BenchmarkError, "disabled"):
            validate_google_corpus([public], False)
        with self.assertRaisesRegex(BenchmarkError, "public or synthetic"):
            validate_google_corpus([public, private], True)
        validate_google_corpus([public], True)

    def test_google_cloud_v3_request_is_complete_and_token_is_not_metadata(self) -> None:
        captured: dict[str, object] = {}
        timestamps = iter(("2026-08-05T00:00:00Z", "2026-08-05T00:00:01Z"))

        def open_request(request, timeout: float):
            captured["body"] = json.loads(request.data)
            captured["authorization"] = request.get_header("Authorization")
            captured["timeout"] = timeout
            return FakeResponse({"translations": [{"translatedText": "vollständige Übersetzung"}]})

        backend = GoogleCloudBackend(
            project="bench-project-123",
            location="global",
            model="general/translation-llm",
            timeout_seconds=9,
            batch_size=8,
            token_provider=lambda: "benchmark-secret-token",
            request_opener=open_request,
            timestamp_provider=lambda: next(timestamps),
        )
        source = "x" * DEFAULT_MAX_SOURCE_CHARACTERS

        output = backend.translate([source], "en", "de")

        self.assertEqual(["vollständige Übersetzung"], output)
        self.assertEqual([source], captured["body"]["contents"])
        self.assertEqual("en", captured["body"]["sourceLanguageCode"])
        self.assertEqual("de", captured["body"]["targetLanguageCode"])
        self.assertEqual("Bearer benchmark-secret-token", captured["authorization"])
        self.assertEqual("2026-08-05T00:00:00Z", backend.execution_metadata["firstRequestAtUtc"])
        self.assertNotIn("benchmark-secret-token", json.dumps(backend.execution_metadata))

    def test_translategemma_is_local_bf16_and_uses_official_chat_fields(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            model_path = Path(directory)
            (model_path / "config.json").write_text("{}", encoding="utf-8")
            (model_path / "tokenizer_config.json").write_text("{}", encoding="utf-8")
            (model_path / "model.safetensors").write_bytes(b"synthetic weights")
            digest = sha256_directory(model_path)
            processor = FakeProcessor()
            model = FakeModel()

            def runtime_loader(path: Path, device: str, threads: int):
                self.assertEqual(model_path, path)
                self.assertEqual("cpu", device)
                self.assertEqual(2, threads)
                return FakeTorch(), SimpleNamespace(__version__="test"), processor, model, "cpu"

            backend = TranslateGemmaBackend(
                model_path=model_path,
                model_id=TRANSLATEGEMMA_MODEL_ID,
                revision="b" * 40,
                expected_model_sha256=digest,
                device="cpu",
                max_input_tokens=DEFAULT_MAX_INPUT_TOKENS,
                max_new_tokens=128,
                batch_size=2,
                threads=2,
                runtime_loader=runtime_loader,
            )

            output = backend.translate(["complete source"], "en_US", "de_DE")

        content = processor.conversations[0][0]["content"][0]
        self.assertEqual(["translated"], output)
        self.assertEqual("complete source", content["text"])
        self.assertEqual("en", content["source_lang_code"])
        self.assertEqual("de", content["target_lang_code"])
        self.assertEqual(
            {"padding": True, "truncation": False},
            processor.template_arguments["processor_kwargs"],
        )
        self.assertFalse(model.generate_arguments["do_sample"])
        self.assertTrue(backend.execution_metadata["localFilesOnly"])
        self.assertFalse(backend.execution_metadata["quantized"])
        self.assertEqual("test-torch", backend.execution_metadata["torchVersion"])
        self.assertIsNone(backend.execution_metadata["cudaVersion"])

    def test_translategemma_fails_before_runtime_when_snapshot_is_incomplete(self) -> None:
        with (
            tempfile.TemporaryDirectory() as directory,
            self.assertRaisesRegex(BenchmarkError, "incomplete"),
        ):
            TranslateGemmaBackend(
                model_path=Path(directory),
                model_id=TRANSLATEGEMMA_MODEL_ID,
                revision="b" * 40,
                expected_model_sha256="a" * 64,
                device="cpu",
                max_input_tokens=DEFAULT_MAX_INPUT_TOKENS,
                max_new_tokens=128,
                batch_size=1,
                runtime_loader=lambda *_: self.fail("runtime must not load"),
            )

    def test_translategemma_generation_error_includes_bounded_root_cause(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            model_path = Path(directory)
            (model_path / "config.json").write_text("{}", encoding="utf-8")
            (model_path / "tokenizer_config.json").write_text("{}", encoding="utf-8")
            (model_path / "model.safetensors").write_bytes(b"synthetic weights")
            digest = sha256_directory(model_path)
            backend = TranslateGemmaBackend(
                model_path=model_path,
                model_id=TRANSLATEGEMMA_MODEL_ID,
                revision="b" * 40,
                expected_model_sha256=digest,
                device="cpu",
                max_input_tokens=DEFAULT_MAX_INPUT_TOKENS,
                max_new_tokens=128,
                batch_size=1,
                runtime_loader=lambda *_: (
                    FakeTorch(),
                    SimpleNamespace(__version__="test"),
                    FakeProcessor(),
                    FailingFakeModel(),
                    "cpu",
                ),
            )

            with self.assertRaisesRegex(
                BenchmarkError,
                r"RuntimeError: synthetic kernel failure",
            ):
                backend.translate(["complete source"], "en_US", "de_DE")

    def test_remote_endpoint_is_rejected(self) -> None:
        with self.assertRaisesRegex(BenchmarkError, "loopback-only"):
            OpenAIChatBackend("https://example.com", 30, 128)

        with self.assertRaisesRegex(BenchmarkError, "loopback-only"):
            OpenAIChatBackend("http://localhost:18089@198.51.100.10", 30, 128)

    def test_ipv6_loopback_endpoint_is_accepted(self) -> None:
        backend = OpenAIChatBackend("http://[::1]:18089", 30, 128)

        self.assertEqual("http://[::1]:18089/v1/chat/completions", backend._url)

    def test_local_llama_auto_parallelism_accepts_zero(self) -> None:
        validate_arguments(
            Namespace(
                wmt_per_locale=1,
                batch_size=8,
                parallelism=0,
                strict_determinism=False,
                engine="llama-cpp",
                model_path=Path("model.gguf"),
                model_id="test/model",
                model_revision="revision",
                threads=0,
                startup_timeout_seconds=30,
                gpu_layers=-1,
                device="auto",
                model_sha256="a" * 64,
                max_input_tokens=DEFAULT_MAX_INPUT_TOKENS,
                max_new_tokens=512,
                max_source_characters=DEFAULT_MAX_SOURCE_CHARACTERS,
            )
        )

    def test_hybrid_benchmark_requires_explicit_gpu_layer_count(self) -> None:
        args = Namespace(
            wmt_per_locale=1,
            batch_size=8,
            parallelism=2,
            strict_determinism=False,
            engine="llama-cpp",
            model_path=Path("model.gguf"),
            model_id="test/model",
            model_revision="revision",
            threads=0,
            startup_timeout_seconds=30,
            gpu_layers=-1,
            device="hybrid",
            model_sha256="a" * 64,
            max_input_tokens=DEFAULT_MAX_INPUT_TOKENS,
            max_new_tokens=512,
            max_source_characters=DEFAULT_MAX_SOURCE_CHARACTERS,
        )

        with self.assertRaisesRegex(BenchmarkError, "explicit --gpu-layers"):
            validate_arguments(args)


if __name__ == "__main__":
    unittest.main()
