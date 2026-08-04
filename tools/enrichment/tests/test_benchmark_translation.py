from __future__ import annotations

import json
import sys
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from benchmark_translation import (  # noqa: E402
    BenchmarkError,
    Case,
    OpenAIChatBackend,
    load_forensic_cases,
    load_wmt_cases,
    run_cases,
    validate_arguments,
)


class FakeBackend:
    batch_size = 2

    def translate(self, texts: list[str]) -> list[str]:
        return [text.replace("contraseña", "password") for text in texts]


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
                threads=0,
                startup_timeout_seconds=30,
                gpu_layers=-1,
                device="auto",
                model_sha256="a" * 64,
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
            threads=0,
            startup_timeout_seconds=30,
            gpu_layers=-1,
            device="hybrid",
            model_sha256="a" * 64,
        )

        with self.assertRaisesRegex(BenchmarkError, "explicit --gpu-layers"):
            validate_arguments(args)


if __name__ == "__main__":
    unittest.main()
