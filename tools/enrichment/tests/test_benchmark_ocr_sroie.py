from __future__ import annotations

import hashlib
import json
import struct
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import benchmark_ocr_sroie as sroie  # noqa: E402
import bstrings_ocr as ocr_worker  # noqa: E402
from benchmark_ocr import BenchmarkError, sha256_file  # noqa: E402
from benchmark_ocr_cord import CordDocument, Prediction, convex_hull, score_document  # noqa: E402


def synthetic_jpeg(width: int = 100, height: int = 80, marker: bytes = b"") -> bytes:
    # The benchmark's header reader needs only a structurally valid SOF segment.
    return (
        b"\xff\xd8\xff\xc0\x00\x11\x08"
        + struct.pack(">HH", height, width)
        + b"\x03\x01\x11\x00\x02\x11\x00\x03\x11\x00\xff\xd9"
        + marker
    )


def row_fixture(
    *,
    raw_image: bytes | None = None,
    path: str = "receipt.jpg",
    words: list[str] | None = None,
    bboxes: list[list[int]] | None = None,
    width: int = 100,
    height: int = 80,
    secret: str = "SECRET ENTITY MUST NOT BE READ",
) -> dict[str, object]:
    return {
        "image": {
            "bytes": raw_image if raw_image is not None else synthetic_jpeg(width, height),
            "path": path,
        },
        "key": secret,
        "image_size": {"width": width, "height": height},
        "entities": {
            "company": secret,
            "date": secret,
            "address": secret,
            "total": secret,
        },
        "words": words if words is not None else ["Cafe\u0301   total", "$10.00"],
        "bboxes": bboxes if bboxes is not None else [[1, 2, 40, 12], [50, 2, 90, 12]],
    }


class SroieAdapterTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name).resolve()

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def write_parquet(
        self,
        rows: list[dict[str, object]],
        *,
        name: str = "synthetic.parquet",
        schema=None,
    ) -> Path:
        import pyarrow as pa
        import pyarrow.parquet as pq

        resolved_schema = schema or sroie._expected_arrow_schema(pa)
        table = pa.Table.from_pylist(rows, schema=resolved_schema)
        path = self.root / name
        pq.write_table(table, path, row_group_size=max(len(rows), 1))
        return path

    def config_for(self, path: Path, rows: int) -> dict[str, object]:
        import pyarrow.parquet as pq

        return {
            "filename": path.name,
            "bytes": path.stat().st_size,
            "sha256": sha256_file(path),
            "rows": rows,
            "rowGroups": pq.ParquetFile(path).metadata.num_row_groups,
            "url": "https://example.invalid/synthetic.parquet",
        }

    def selected_row(self, row: dict[str, object]) -> dict[str, object]:
        return {name: row[name] for name in ("image", "image_size", "words", "bboxes")}

    def test_pinned_identity_constants_are_exact(self) -> None:
        self.assertEqual("bffe40c26759f3376ec2b3ae9031dbba54cd587c", sroie.SROIE_COMMIT)
        self.assertEqual(
            {
                "filename": "train-00000-of-00001.parquet",
                "bytes": 318_620_215,
                "sha256": "b18c16b4d8481e5e4537a1700e4616907fe4acd92d6362a7e430b0e866213887",
                "rows": 626,
                "rowGroups": 7,
            },
            {key: sroie.SROIE_FILES["train"][key] for key in self.expected_keys()},
        )
        self.assertEqual(
            {
                "filename": "test-00000-of-00001.parquet",
                "bytes": 191_045_976,
                "sha256": "04f8f31b45944cc6e6459a7a95c851a721fc93ffec0a5c29ece9ded734a684c2",
                "rows": 361,
                "rowGroups": 4,
            },
            {key: sroie.SROIE_FILES["test"][key] for key in self.expected_keys()},
        )

    @staticmethod
    def expected_keys() -> tuple[str, ...]:
        return "filename", "bytes", "sha256", "rows", "rowGroups"

    def test_verifier_requires_the_exact_physical_file(self) -> None:
        candidate = self.root / "source.parquet"
        candidate.write_bytes(b"synthetic pinned bytes")
        config = {
            "filename": candidate.name,
            "bytes": candidate.stat().st_size,
            "sha256": hashlib.sha256(candidate.read_bytes()).hexdigest(),
            "rows": 1,
            "rowGroups": 1,
        }
        with patch.dict(sroie.SROIE_FILES, {"train": config}):
            self.assertEqual(config["sha256"], sroie.verify_sroie_parquet(candidate))
            with (
                patch.dict(config, {"bytes": candidate.stat().st_size + 1}),
                self.assertRaisesRegex(BenchmarkError, "byte length"),
            ):
                sroie.verify_sroie_parquet(candidate)
            with (
                patch.dict(config, {"sha256": "0" * 64}),
                self.assertRaisesRegex(BenchmarkError, "SHA-256"),
            ):
                sroie.verify_sroie_parquet(candidate)
            with (
                patch.dict(config, {"filename": "different.parquet"}),
                self.assertRaisesRegex(BenchmarkError, "basename"),
            ):
                sroie.verify_sroie_parquet(candidate)
        directory_config = {**config, "filename": self.root.name}
        with (
            patch.dict(sroie.SROIE_FILES, {"train": directory_config}),
            self.assertRaisesRegex(BenchmarkError, "physical regular file"),
        ):
            sroie.verify_sroie_parquet(self.root, split="train")

    def test_parser_normalizes_text_and_preserves_each_source_region(self) -> None:
        parsed = sroie.parse_sroie_row(7, self.selected_row(row_fixture()))
        self.assertEqual(("Café total", "$10.00"), tuple(line.text for line in parsed.lines))
        self.assertEqual((0, 1), tuple(word.row_id for word in parsed.words))
        self.assertEqual((0, 1), tuple(row.row_id for row in parsed.rows))
        self.assertEqual(parsed.words[0].polygon, parsed.rows[0].polygon)
        self.assertEqual(
            ((0.0, 0.0), (100.0, 0.0), (100.0, 80.0), (0.0, 80.0)),
            parsed.roi_polygon,
        )
        self.assertEqual("unicode-nfc-collapse-whitespace-v1", parsed.document["normalization"])

    def test_source_regions_score_perfectly_when_the_worker_merges_them(self) -> None:
        source_row = row_fixture(words=["A", "B"], bboxes=[[1, 2, 40, 12], [50, 2, 90, 12]])
        parsed = sroie.parse_sroie_row(0, self.selected_row(source_row))
        image_sha256 = hashlib.sha256(parsed.raw_image).hexdigest()
        document = CordDocument(
            row_index=0,
            image_id=0,
            relative_path="images/row-0000.jpg",
            path=self.root / "row-0000.jpg",
            length=len(parsed.raw_image),
            sha256=image_sha256,
            annotation_sha256="a" * 64,
            lines=parsed.lines,
            dontcare_polygons=(),
            repeating_symbol_polygons=(),
            clipped_valid_lines=0,
            clipped_dontcare_regions=0,
            clipped_repeating_symbol_regions=0,
            words=parsed.words,
            rows=parsed.rows,
            roi_polygon=parsed.roi_polygon,
        )
        merged_polygon = convex_hull(((1, 2), (90, 2), (90, 12), (1, 12)))
        metrics = score_document(
            document,
            (Prediction("A B", merged_polygon, 1.0, "merged-prediction"),),
        )
        self.assertEqual(1.0, metrics["detection"]["hmean"])
        self.assertEqual(1.0, metrics["endToEndExact"]["hmean"])
        self.assertEqual(1.0, metrics["tokenF1"])

    def test_parser_rejects_hidden_paths_bad_text_and_bad_boxes(self) -> None:
        invalid_rows = {
            "nested path": row_fixture(path="folder/receipt.jpg"),
            "drive path": row_fixture(path="C:receipt.jpg"),
            "wrong suffix": row_fixture(path="receipt.png"),
            "control path": row_fixture(path="receipt\n.jpg"),
            "unicode control path": row_fixture(path="receipt\u0085.jpg"),
            "empty text": row_fixture(words=[" \t\n"], bboxes=[[1, 2, 10, 12]]),
            "nul text": row_fixture(words=["A\x00B"], bboxes=[[1, 2, 10, 12]]),
            "boolean coordinate": row_fixture(words=["A"], bboxes=[[True, 2, 10, 12]]),
            "outside box": row_fixture(words=["A"], bboxes=[[1, 2, 101, 12]]),
            "misaligned lists": row_fixture(words=["A"], bboxes=[]),
        }
        for label, row in invalid_rows.items():
            with self.subTest(label=label), self.assertRaises(BenchmarkError):
                sroie.parse_sroie_row(0, self.selected_row(row))

    def test_exact_arrow_schema_is_required(self) -> None:
        import pyarrow as pa

        row = row_fixture()
        bad_schema = pa.schema(
            [field for field in sroie._expected_arrow_schema(pa) if field.name != "entities"]
        )
        row_without_entities = {key: value for key, value in row.items() if key != "entities"}
        path = self.write_parquet([row_without_entities], schema=bad_schema)
        config = self.config_for(path, 1)
        with (
            patch.dict(sroie.SROIE_FILES, {"train": config}),
            self.assertRaisesRegex(BenchmarkError, "exact expected schema"),
        ):
            sroie.extract_sroie_corpus(path, self.root / "extract")
        self.assertFalse((self.root / "extract").exists())

    def test_duplicate_policy_audit_worker_contract_and_column_boundary(self) -> None:
        import pyarrow.parquet as pq

        first_image = synthetic_jpeg(marker=b"first")
        second_image = synthetic_jpeg(marker=b"second")
        singleton_image = synthetic_jpeg(marker=b"singleton")
        rows = [
            row_fixture(raw_image=first_image, path="zero.jpg", words=["A  B"]),
            row_fixture(raw_image=first_image, path="one.jpg", words=["A B"]),
            row_fixture(raw_image=second_image, path="two.jpg", words=["C"]),
            row_fixture(raw_image=second_image, path="three.jpg", words=["DIFFERENT"]),
            row_fixture(raw_image=singleton_image, path="four.jpg", words=["E"]),
        ]
        for row in rows:
            row["bboxes"] = [[1, 2, 40, 12]]
        parquet_path = self.write_parquet(rows)
        config = self.config_for(parquet_path, len(rows))
        real_parquet_file = pq.ParquetFile
        columns_seen: list[tuple[str, ...]] = []

        class RecordingParquet:
            def __init__(self, path):
                self.inner = real_parquet_file(path)
                self.metadata = self.inner.metadata
                self.schema_arrow = self.inner.schema_arrow

            def iter_batches(self, **kwargs):
                columns_seen.append(tuple(kwargs.get("columns", ())))
                return self.inner.iter_batches(**kwargs)

        output_root = self.root / "extract"
        with (
            patch.dict(sroie.SROIE_FILES, {"train": config}),
            patch.object(pq, "ParquetFile", RecordingParquet),
        ):
            corpus = sroie.extract_sroie_corpus(parquet_path, output_root)

        selected_columns = ("image", "image_size", "words", "bboxes")
        self.assertEqual([selected_columns, selected_columns], columns_seen)
        self.assertEqual((0, 4), tuple(document.row_index for document in corpus.documents))
        self.assertEqual((1, 2, 3), corpus.excluded_row_indices)
        self.assertEqual(5, corpus.source_row_count)
        self.assertEqual(5, len(corpus.source_image_sha256s))
        self.assertEqual(ocr_worker.SCHEMA_VERSION, sroie.OCR_WORKER_INPUT_MANIFEST_SCHEMA_VERSION)

        worker_rows = [
            json.loads(line)
            for line in corpus.worker_manifest.read_text(encoding="utf-8").splitlines()
        ]
        self.assertEqual(2, len(worker_rows))
        for worker_row in worker_rows:
            parsed_worker = ocr_worker._parse_input_manifest_entry(
                json.dumps(worker_row, separators=(",", ":"), sort_keys=True)
            )
            self.assertTrue(Path(parsed_worker.path).is_absolute())
            self.assertEqual(1, worker_row["schemaVersion"])

        audit_bytes = corpus.duplicate_audit.read_bytes()
        self.assertEqual(corpus.duplicate_audit_sha256, hashlib.sha256(audit_bytes).hexdigest())
        audit = json.loads(audit_bytes)
        self.assertEqual(5, audit["sourceRows"])
        self.assertEqual(2, audit["includedRows"])
        self.assertEqual(3, audit["excludedRows"])
        self.assertEqual(1, audit["identicalAnnotationDuplicateGroups"])
        self.assertEqual(1, audit["conflictingAnnotationDuplicateGroups"])
        self.assertEqual([1, 2, 3], audit["excludedRowIndices"])
        self.assertNotIn("SECRET ENTITY MUST NOT BE READ", audit_bytes.decode("utf-8"))
        self.assertNotIn("DIFFERENT", audit_bytes.decode("utf-8"))
        self.assertEqual(corpus.selection_sha256, audit["selectionSha256"])
        self.assertFalse(any(output_root.rglob("*.incomplete-*")))

    def test_test_split_scores_every_row_and_only_audits_duplicates(self) -> None:
        first_image = synthetic_jpeg(marker=b"first")
        second_image = synthetic_jpeg(marker=b"second")
        rows = [
            row_fixture(raw_image=first_image, path="zero.jpg", words=["A  B"]),
            row_fixture(raw_image=first_image, path="one.jpg", words=["A B"]),
            row_fixture(raw_image=second_image, path="two.jpg", words=["C"]),
            row_fixture(raw_image=second_image, path="three.jpg", words=["DIFFERENT"]),
        ]
        for row in rows:
            row["bboxes"] = [[1, 2, 40, 12]]
        parquet_path = self.write_parquet(rows)
        config = self.config_for(parquet_path, len(rows))
        output_root = self.root / "test-extract"

        with patch.dict(sroie.SROIE_FILES, {"test": config}):
            corpus = sroie.extract_sroie_corpus(
                parquet_path,
                output_root,
                split="test",
            )

        self.assertEqual((0, 1, 2, 3), tuple(item.row_index for item in corpus.documents))
        self.assertEqual((), corpus.excluded_row_indices)
        audit_bytes = corpus.duplicate_audit.read_bytes()
        audit = json.loads(audit_bytes)
        self.assertEqual(4, audit["sourceRows"])
        self.assertEqual(4, audit["includedRows"])
        self.assertEqual(0, audit["excludedRows"])
        self.assertEqual([], audit["excludedRowIndices"])
        self.assertEqual(1, audit["identicalAnnotationDuplicateGroups"])
        self.assertEqual(1, audit["conflictingAnnotationDuplicateGroups"])
        self.assertEqual(
            ["score-all-test-rows", "score-all-test-rows"],
            [group["decision"] for group in audit["duplicateGroupAudit"]],
        )
        self.assertTrue(
            all(
                group["includedRowIndices"] == group["rowIndices"]
                and group["excludedRowIndices"] == []
                for group in audit["duplicateGroupAudit"]
            )
        )
        self.assertNotIn("SECRET ENTITY MUST NOT BE READ", audit_bytes.decode("utf-8"))
        self.assertNotIn("DIFFERENT", audit_bytes.decode("utf-8"))

    def test_extraction_refuses_a_reused_output_root(self) -> None:
        row = row_fixture(words=["A"], bboxes=[[1, 2, 10, 12]])
        parquet_path = self.write_parquet([row])
        config = self.config_for(parquet_path, 1)
        output_root = self.root / "existing"
        output_root.mkdir()
        sentinel = output_root / "sentinel.txt"
        sentinel.write_text("preserve", encoding="utf-8")
        with (
            patch.dict(sroie.SROIE_FILES, {"train": config}),
            self.assertRaisesRegex(BenchmarkError, "fresh and non-existent"),
        ):
            sroie.extract_sroie_corpus(parquet_path, output_root)
        self.assertEqual("preserve", sentinel.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
