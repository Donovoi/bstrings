from __future__ import annotations

import hashlib
import json
import os
import struct
import subprocess
import sys
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from benchmark_ocr import (  # noqa: E402
    OFFLINE_ENVIRONMENT,
    Backend,
    BenchmarkError,
    canonical_evidence_sha256,
    canonical_json,
)
from benchmark_ocr_cord import (  # noqa: E402
    CONFIDENCE_PARITY_MAX_ABS_DELTA,
    CORD_V2_COMMIT,
    CORD_V2_FILES,
    CORD_V2_TEST_BYTES,
    CORD_V2_TEST_SHA256,
    CORD_V2_VALIDATION_BYTES,
    CORD_V2_VALIDATION_SHA256,
    CordDocument,
    CordLine,
    CordPhysicalRow,
    CordWord,
    ExtractedCorpus,
    Prediction,
    _cluster_prediction_rows,
    _confidence_by_critical_record,
    _embedded_image_dimensions,
    _ignored_prediction,
    _maximum_matches,
    _reference_rows,
    _segmentation_covered_fraction,
    _shapely_runtime,
    _verify_embedded_image_dimensions,
    aggregate_metrics,
    assemble_report,
    benchmark_backend,
    confidence_parity,
    convex_hull,
    create_corpus_view,
    critical_evidence_sha256,
    parse_arguments,
    parse_cord_annotation,
    polygon_iou,
    run,
    run_backend_once,
    score_document,
    validate_run_provenance,
    validate_scorer_oracle,
    verify_cord_parquet,
)


def quad(left: int, top: int, right: int, bottom: int) -> dict[str, int]:
    return {
        "x1": left,
        "y1": top,
        "x2": right,
        "y2": top,
        "x3": right,
        "y3": bottom,
        "x4": left,
        "y4": bottom,
    }


def polygon(left: int, top: int, right: int, bottom: int):
    return convex_hull(((left, top), (right, top), (right, bottom), (left, bottom)))


def cord_word(
    row_id: int,
    line_index: int,
    word_index: int,
    text: str,
    left: int,
    top: int,
    right: int,
    bottom: int,
) -> CordWord:
    return CordWord(line_index, word_index, row_id, text, polygon(left, top, right, bottom))


def annotation_fixture() -> dict[str, object]:
    return {
        "meta": {
            "version": "2.0.0",
            "split": "test",
            "image_id": 17,
            "image_size": {"width": 100, "height": 100},
        },
        "valid_line": [
            {
                "category": "menu.nm",
                "group_id": 2,
                "sub_group_id": 0,
                "words": [
                    {
                        "text": "Cafe\u0301:",
                        "quad": quad(0, 0, 40, 10),
                        "is_key": 0,
                        "row_id": 100,
                    },
                    {
                        "text": "10,000",
                        "quad": quad(45, 0, 90, 10),
                        "is_key": 0,
                        "row_id": 100,
                    },
                ],
            },
            {
                "category": "total.total_price",
                "group_id": 3,
                "sub_group_id": 0,
                "words": [
                    {
                        "text": "TOTAL",
                        "quad": quad(0, 20, 50, 30),
                        "is_key": 1,
                        "row_id": 101,
                    }
                ],
            },
        ],
        "dontcare": [[quad(70, 70, 80, 90), quad(80, 70, 90, 90)]],
        "repeating_symbol": [[{"quad": quad(0, 70, 20, 80), "text": "-----"}]],
        "roi": {},
        "gt_parse": {},
    }


class CordBenchmarkTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.source = self.root / "row-0000.png"
        self.source.write_bytes(b"synthetic")
        parsed = parse_cord_annotation(0, annotation_fixture())
        self.document = CordDocument(
            row_index=0,
            image_id=parsed.image_id,
            relative_path="images/row-0000.png",
            path=self.source,
            length=self.source.stat().st_size,
            sha256="a" * 64,
            annotation_sha256="b" * 64,
            lines=parsed.lines,
            dontcare_polygons=parsed.dontcare_polygons,
            repeating_symbol_polygons=parsed.repeating_symbol_polygons,
            clipped_valid_lines=parsed.clipped_valid_lines,
            clipped_dontcare_regions=parsed.clipped_dontcare_regions,
            clipped_repeating_symbol_regions=parsed.clipped_repeating_symbol_regions,
            words=parsed.words,
            rows=parsed.rows,
            roi_polygon=parsed.roi_polygon,
        )

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def corpus(self, *, split: str = "test") -> ExtractedCorpus:
        placeholder = self.root / f"{split}-placeholder"
        placeholder.write_text("fixture\n", encoding="utf-8")
        return ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=placeholder,
            worker_manifest=placeholder,
            inventory=placeholder,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="f" * 64,
            split=split,
        )

    def test_cord_worker_launch_is_isolated_from_environment_and_cwd_poison(self) -> None:
        worker = self.root / "worker.py"
        worker.write_text("pass\n", encoding="utf-8")
        model_pack = self.root / "model-pack.json"
        model_pack.write_text("{}\n", encoding="utf-8")
        output_root = self.root / "isolated-run"
        observed: dict[str, object] = {}

        def capture(command, **kwargs):
            observed["command"] = command
            observed.update(kwargs)
            cwd = Path(kwargs["cwd"])
            self.assertEqual(output_root / "process-cwd", cwd)
            self.assertTrue(cwd.is_dir())
            self.assertEqual([], list(cwd.iterdir()))
            return subprocess.CompletedProcess(command, 1, "", "expected fixture stop")

        with (
            patch.dict(
                os.environ,
                {
                    "PATH": str(self.root / "poison-path"),
                    "PYTHONPATH": str(self.root / "poison-pythonpath"),
                    "PYTHONHOME": str(self.root / "poison-home"),
                    "OMP_NUM_THREADS": "999",
                },
                clear=False,
            ),
            patch("benchmark_ocr_cord.subprocess.run", side_effect=capture),
            self.assertRaisesRegex(BenchmarkError, "non-zero status"),
        ):
            run_backend_once(
                backend=Backend("cpu", Path(sys.executable)),
                worker=worker,
                model_pack=model_pack,
                model_id="synthetic/model",
                revision="abc123",
                model_pack_sha256="2" * 64,
                worker_sha256=hashlib.sha256(worker.read_bytes()).hexdigest(),
                engine_version="3.9.2",
                corpus=self.corpus(),
                output_root=output_root,
                threads=0,
                timeout_seconds=1.0,
                thresholds={
                    "minimumDetectionHmean": None,
                    "minimumEndToEndHmean": None,
                    "minimumWordAccuracy": None,
                    "maximumPageCer": None,
                    "maximumPageWer": None,
                },
            )

        command = observed["command"]
        self.assertEqual(str(Path(sys.executable).resolve()), command[0])
        self.assertEqual(["-I", "-B"], command[1:3])
        self.assertEqual(str(worker.resolve()), command[3])
        environment = observed["env"]
        for name in ("PYTHONPATH", "PYTHONHOME", "OMP_NUM_THREADS"):
            self.assertNotIn(name, environment)
        self.assertNotIn(str(self.root / "poison-path"), environment["PATH"])
        self.assertEqual(
            OFFLINE_ENVIRONMENT,
            {name: environment[name] for name in OFFLINE_ENVIRONMENT},
        )

    def document_with_rows(
        self,
        rows: tuple[CordPhysicalRow, ...],
        *,
        words: tuple[CordWord, ...] = (),
        roi=None,
        dontcare=(),
        repeating=(),
    ) -> CordDocument:
        lines = tuple(CordLine(index, row.text, row.polygon) for index, row in enumerate(rows))
        return replace(
            self.document,
            lines=lines,
            words=words,
            rows=rows,
            roi_polygon=roi or polygon(0, 0, 100, 100),
            dontcare_polygons=dontcare,
            repeating_symbol_polygons=repeating,
        )

    def assert_perfect_metrics(self, result: dict[str, object]) -> None:
        detection = result["detection"]
        recognition = result["endToEndExact"]
        self.assertIsInstance(detection, dict)
        self.assertIsInstance(recognition, dict)
        for metrics in (detection, recognition):
            self.assertEqual(1.0, metrics["precision"])
            self.assertEqual(1.0, metrics["recall"])
            self.assertEqual(1.0, metrics["hmean"])
        for name in (
            "tokenPrecision",
            "tokenRecall",
            "tokenF1",
            "charPrecision",
            "charRecall",
            "oneMinusNed",
        ):
            self.assertEqual(1.0, result[name], name)
        self.assertEqual(0.0, result["pageCer"])
        self.assertEqual(0.0, result["pageWer"])

    def test_annotation_parser_preserves_punctuation_and_normalizes_nfc(self) -> None:
        parsed = parse_cord_annotation(0, canonical_json(annotation_fixture()))
        self.assertEqual(17, parsed.image_id)
        self.assertEqual((100, 100), (parsed.width, parsed.height))
        self.assertEqual("Café: 10,000", parsed.lines[0].text)
        self.assertEqual(2, len(parsed.lines))
        self.assertEqual(2, len(parsed.dontcare_polygons))
        self.assertEqual(1, len(parsed.repeating_symbol_polygons))
        self.assertEqual(3, len(parsed.ignored_polygons))
        self.assertEqual(0, parsed.clipped_valid_lines)
        self.assertEqual(0, parsed.clipped_dontcare_regions)
        self.assertEqual(0, parsed.clipped_repeating_symbol_regions)
        self.assertEqual(3, len(parsed.words))
        self.assertEqual((100, 100, 101), tuple(word.row_id for word in parsed.words))
        self.assertEqual(2, len(parsed.rows))
        self.assertEqual("Café: 10,000", parsed.rows[0].text)
        self.assertEqual(polygon(0, 0, 100, 100), parsed.roi_polygon)

    def test_pinned_validation_split_identity_and_cli_are_explicit(self) -> None:
        self.assertEqual("7f0115a4b758a71d6473b8d085751692da2fef98", CORD_V2_COMMIT)
        self.assertEqual(234_202_795, CORD_V2_TEST_BYTES)
        self.assertEqual(242_080_800, CORD_V2_VALIDATION_BYTES)
        self.assertEqual(
            "0d0f6dac11fdcc549de2746aa9f53136a3bc22a2a1aff2b0b847f7622ad60c15",
            CORD_V2_VALIDATION_SHA256,
        )
        validation = dict(annotation_fixture())
        validation["meta"] = dict(validation["meta"], split="valid")  # type: ignore[arg-type]
        parsed = parse_cord_annotation(0, validation, expected_split="validation")
        self.assertEqual(17, parsed.image_id)
        with self.assertRaises(BenchmarkError):
            parse_cord_annotation(0, validation)

        args = parse_arguments(
            [
                "--cord-parquet",
                "cord.parquet",
                "--cord-split",
                "validation",
                "--worker",
                "worker.py",
                "--model-pack",
                "models",
                "--backend",
                f"cpu={sys.executable}",
                "--output",
                "report.json",
                "--work-directory",
                "work",
            ]
        )
        self.assertEqual("validation", args.cord_split)
        self.assertIn("validation-00000-of-00001", CORD_V2_FILES["validation"]["url"])

    def test_annotation_parser_rejects_duplicate_members_and_wrong_split(self) -> None:
        duplicated = '{"meta":{},"meta":{},"valid_line":[],"dontcare":[]}'
        with self.assertRaises(BenchmarkError):
            parse_cord_annotation(0, duplicated)
        changed = annotation_fixture()
        changed["meta"] = dict(changed["meta"], split="train")  # type: ignore[arg-type]
        with self.assertRaises(BenchmarkError):
            parse_cord_annotation(0, changed)

    def test_annotation_parser_enforces_nested_ignore_group_schemas(self) -> None:
        flat_dontcare = annotation_fixture()
        flat_dontcare["dontcare"] = [quad(70, 70, 90, 90)]
        with self.assertRaises(BenchmarkError):
            parse_cord_annotation(0, flat_dontcare)

        multi_entry_repeating = annotation_fixture()
        symbol = {"quad": quad(0, 70, 20, 80), "text": "-----"}
        second_symbol = {"quad": quad(30, 70, 50, 80), "text": "====="}
        multi_entry_repeating["repeating_symbol"] = [[symbol, second_symbol]]
        parsed = parse_cord_annotation(0, multi_entry_repeating)
        self.assertEqual(2, len(parsed.repeating_symbol_polygons))

        four_entry_dontcare = annotation_fixture()
        four_entry_dontcare["dontcare"] = [
            [quad(index * 10, 70, index * 10 + 8, 80) for index in range(4)]
        ]
        parsed = parse_cord_annotation(0, four_entry_dontcare)
        self.assertEqual(4, len(parsed.dontcare_polygons))

        extra_quad_member = annotation_fixture()
        malformed_quad = dict(quad(0, 70, 20, 80), extra=1)
        extra_quad_member["repeating_symbol"] = [[{"quad": malformed_quad, "text": "-----"}]]
        with self.assertRaises(BenchmarkError):
            parse_cord_annotation(0, extra_quad_member)

        for field in ("dontcare", "repeating_symbol"):
            empty_group = annotation_fixture()
            empty_group[field] = [[]]
            with self.assertRaises(BenchmarkError):
                parse_cord_annotation(0, empty_group)

    def test_annotation_boundary_clipping_is_bounded_and_roi_is_not_bounds_gated(self) -> None:
        clipped = annotation_fixture()
        first_word = clipped["valid_line"][0]["words"][0]  # type: ignore[index]
        first_word["quad"] = quad(-1, 0, 40, 10)
        clipped["dontcare"] = [[quad(-3, 70, 20, 90)]]
        clipped["repeating_symbol"] = [[{"quad": quad(80, 70, 103, 80), "text": "-----"}]]
        clipped["roi"] = quad(-19, -10, 119, 130)
        parsed = parse_cord_annotation(0, clipped)
        self.assertEqual(1, parsed.clipped_valid_lines)
        self.assertEqual(1, parsed.clipped_dontcare_regions)
        self.assertEqual(1, parsed.clipped_repeating_symbol_regions)
        self.assertEqual(0.0, min(point[0] for point in parsed.lines[0].polygon))
        self.assertEqual(100.0, max(point[0] for point in parsed.repeating_symbol_polygons[0]))

        excessive = annotation_fixture()
        excessive["dontcare"] = [[quad(-4, 70, 20, 90)]]
        with self.assertRaises(BenchmarkError):
            parse_cord_annotation(0, excessive)

    def test_polygon_iou_uses_true_polygon_intersection(self) -> None:
        left = polygon(0, 0, 10, 10)
        self.assertEqual(1.0, polygon_iou(left, left))
        self.assertEqual(0.0, polygon_iou(left, polygon(20, 0, 30, 10)))
        self.assertAlmostEqual(1 / 3, polygon_iou(left, polygon(5, 0, 15, 10)))

    def test_matching_is_one_to_one_and_recovers_maximum_cardinality(self) -> None:
        truths = (
            CordLine(0, "first", polygon(0, 0, 10, 10)),
            CordLine(1, "second", polygon(0, 0, 6, 10)),
        )
        predictions = (
            Prediction("second", polygon(0, 0, 6, 10), 1.0, "p0"),
            Prediction("first", polygon(4, 0, 10, 10), 1.0, "p1"),
        )
        detection = _maximum_matches(truths, predictions, exact_text=False)
        exact = _maximum_matches(truths, predictions, exact_text=True)
        self.assertEqual(2, len(detection))
        self.assertEqual(2, len(exact))
        self.assertEqual({0, 1}, {match.prediction_index for match in detection})

    def test_document_metrics_penalize_unmatched_truth_and_predictions(self) -> None:
        predictions = (
            Prediction(
                self.document.lines[0].text,
                self.document.lines[0].polygon,
                0.99,
                "exact",
            ),
            Prediction("UNRELATED", polygon(0, 40, 40, 50), 0.75, "extra"),
        )
        result = score_document(self.document, predictions)
        self.assertEqual(1, result["detection"]["truePositive"])
        self.assertEqual(1, result["detection"]["falsePositive"])
        self.assertEqual(1, result["detection"]["falseNegative"])
        self.assertEqual(1, result["endToEndExact"]["truePositive"])
        self.assertFalse(result["documentExact"])
        self.assertGreater(result["pageCer"], 0.0)
        self.assertGreater(result["pageWer"], 0.0)
        self.assertLess(result["charPrecision"], 1.0)
        self.assertLess(result["charRecall"], 1.0)

    def test_scorer_oracles_are_exact_for_shuffled_rows_and_word_splits(self) -> None:
        row_predictions = tuple(
            reversed(
                tuple(
                    Prediction(line.text, line.polygon, 1.0, f"row-{index}")
                    for index, line in enumerate(self.document.lines)
                )
            )
        )
        word_predictions = tuple(
            reversed(
                tuple(
                    Prediction(word.text, word.polygon, 1.0, f"word-{index}")
                    for index, word in enumerate(self.document.words)
                )
            )
        )
        self.assert_perfect_metrics(score_document(self.document, row_predictions))
        self.assert_perfect_metrics(score_document(self.document, word_predictions))
        oracle = validate_scorer_oracle(self.corpus())
        self.assertTrue(oracle["passed"])
        self.assertEqual(["physical-row", "word-split"], oracle["variants"])
        self.assertGreaterEqual(
            oracle["minimumWordSplitPredictionHullMemberUnionCoverage"],
            0.0,
        )
        self.assertGreaterEqual(
            oracle["minimumWordSplitTruthMemberUnionCoverage"],
            0.0,
        )

    def test_hand_anchored_cord_rowid_repairs_never_steal_words(self) -> None:
        row9_words = (
            cord_word(2140534, 0, 0, "Bumbu Kaldu Ayam 1", 31, 318, 238, 351),
            cord_word(2140534, 1, 0, "36000", 264, 336, 328, 358),
            cord_word(2140540, 5, 0, "Tunai", 28, 486, 90, 508),
            cord_word(2140541, 5, 1, "50000", 264, 496, 328, 520),
        )
        row9 = replace(self.document, words=row9_words, rows=(), lines=())
        self.assertEqual(
            ["Bumbu Kaldu Ayam 1", "36000", "Tunai 50000"],
            [row.text for row in _reference_rows(row9)],
        )

        row12_words = (
            cord_word(2248071, 0, 0, "0571-1854", 70, 354, 185, 386),
            cord_word(2248071, 1, 0, "BLUS", 190, 360, 238, 388),
            cord_word(2248071, 1, 1, "WANITA", 240, 360, 298, 386),
            cord_word(2248072, 2, 0, "1", 130, 388, 144, 404),
            cord_word(2248072, 3, 0, "@120,000", 172, 386, 265, 414),
            cord_word(2248072, 4, 0, "0%", 284, 386, 312, 410),
            cord_word(2248072, 5, 0, "120,000", 342, 372, 428, 408),
            cord_word(2248073, 6, 0, "1002-0060 SHOPPING BAG", 68, 396, 314, 439),
            cord_word(2248074, 8, 0, "1 @880 100% 0", 124, 434, 430, 464),
            cord_word(2248075, 12, 0, "TOTAL", 57, 482, 123, 515),
            cord_word(2248075, 13, 0, "(2 item)", 130, 490, 221, 520),
            cord_word(2248075, 12, 1, "120,000", 344, 484, 432, 516),
            cord_word(2248076, 14, 0, "CC.Visa.BCA 120,000", 52, 510, 433, 550),
            cord_word(2248077, 15, 0, "Total Kembalian 0", 51, 545, 432, 580),
        )
        row12 = replace(self.document, words=row12_words, rows=(), lines=())
        self.assertEqual(
            [
                "0571-1854 BLUS WANITA",
                "1 @120,000 0% 120,000",
                "1002-0060 SHOPPING BAG",
                "1 @880 100% 0",
                "TOTAL (2 item) 120,000",
                "CC.Visa.BCA 120,000",
                "Total Kembalian 0",
            ],
            [row.text for row in _reference_rows(row12)],
        )

        row73_words = (
            cord_word(2242176, 9, 0, "22.000", 620, 963, 729, 1004),
            cord_word(2242177, 9, 1, "Total.", 122, 984, 233, 1025),
            cord_word(2242178, 10, 0, "Cash", 122, 1030, 201, 1071),
            cord_word(2242179, 10, 1, "25.000", 682, 1053, 794, 1097),
            cord_word(2242180, 10, 2, "Tendered:", 121, 1069, 282, 1117),
            cord_word(2242181, 11, 0, "3.000", 698, 1096, 790, 1138),
            cord_word(2242182, 11, 1, "Change:", 121, 1123, 247, 1161),
        )
        row73 = replace(self.document, words=row73_words, rows=(), lines=())
        self.assertEqual(
            ["Total. 22.000", "Cash", "Tendered: 25.000", "Change: 3.000"],
            [row.text for row in _reference_rows(row73)],
        )

    def test_roi_excludes_background_without_weakening_receipt_metrics(self) -> None:
        scoped = annotation_fixture()
        scoped["roi"] = quad(0, 0, 100, 40)
        parsed = parse_cord_annotation(0, scoped)
        document = CordDocument(
            row_index=self.document.row_index,
            image_id=parsed.image_id,
            relative_path=self.document.relative_path,
            path=self.document.path,
            length=self.document.length,
            sha256=self.document.sha256,
            annotation_sha256=self.document.annotation_sha256,
            lines=parsed.lines,
            dontcare_polygons=parsed.dontcare_polygons,
            repeating_symbol_polygons=parsed.repeating_symbol_polygons,
            clipped_valid_lines=parsed.clipped_valid_lines,
            clipped_dontcare_regions=parsed.clipped_dontcare_regions,
            clipped_repeating_symbol_regions=parsed.clipped_repeating_symbol_regions,
            words=parsed.words,
            rows=parsed.rows,
            roi_polygon=parsed.roi_polygon,
        )
        predictions = tuple(
            Prediction(line.text, line.polygon, 1.0, f"exact-{index}")
            for index, line in enumerate(document.lines)
        ) + (Prediction("BACKGROUND", polygon(40, 50, 90, 60), 1.0, "outside"),)
        result = score_document(document, predictions)
        self.assert_perfect_metrics(result)
        self.assertEqual(1, result["ignoredPredictions"])
        self.assertEqual(1, result["ignoredByRoi"])

    def test_adversarial_drop_corruption_and_extra_token_have_analytic_scores(self) -> None:
        first, second = self.document.lines
        corrupted = score_document(
            self.document,
            (
                Prediction("Café: 99,999", first.polygon, 1.0, "corrupt"),
                Prediction(second.text, second.polygon, 1.0, "total"),
            ),
        )
        self.assertEqual(1.0, corrupted["detection"]["hmean"])
        self.assertAlmostEqual(2 / 3, corrupted["tokenPrecision"])
        self.assertAlmostEqual(2 / 3, corrupted["tokenRecall"])
        self.assertAlmostEqual(2 / 3, corrupted["tokenF1"])
        self.assertGreater(corrupted["pageCer"], 0.0)

        dropped = score_document(
            self.document,
            (Prediction(first.text, first.polygon, 1.0, "first"),),
        )
        self.assertEqual(1.0, dropped["detection"]["precision"])
        self.assertEqual(0.5, dropped["detection"]["recall"])
        self.assertEqual(1.0, dropped["tokenPrecision"])
        self.assertAlmostEqual(2 / 3, dropped["tokenRecall"])
        self.assertAlmostEqual(0.8, dropped["tokenF1"])

        extra = score_document(
            self.document,
            (
                Prediction(first.text, first.polygon, 1.0, "first"),
                Prediction(second.text, second.polygon, 1.0, "total"),
                Prediction("EXTRA", polygon(0, 40, 40, 50), 1.0, "extra"),
            ),
        )
        self.assertEqual(1, extra["detection"]["falsePositive"])
        self.assertAlmostEqual(0.75, extra["tokenPrecision"])
        self.assertEqual(1.0, extra["tokenRecall"])
        self.assertAlmostEqual(6 / 7, extra["tokenF1"])

    def test_giant_box_cannot_claim_perfect_localization(self) -> None:
        prediction = Prediction(
            "Café: 10,000 TOTAL",
            polygon(0, 0, 100, 100),
            1.0,
            "full-page",
        )
        result = score_document(self.document, (prediction,))
        self.assertEqual(0.0, result["detection"]["precision"])
        self.assertEqual(0.0, result["detection"]["hmean"])
        self.assertEqual(0.0, result["endToEndExact"]["hmean"])
        self.assertFalse(result["documentExact"])
        self.assertEqual(1.0, result["tokenF1"])

    def test_exact_union_rejects_overlapping_fragment_double_count(self) -> None:
        truth = CordPhysicalRow(0, "A B", polygon(0, 0, 100, 10), ())
        document = self.document_with_rows((truth,))
        predictions = (
            Prediction("A", polygon(0, 0, 40, 10), 1.0, "p1"),
            Prediction("B", polygon(0, 5.1, 40, 13.1), 1.0, "p2"),
        )
        self.assertAlmostEqual(
            0.4,
            _segmentation_covered_fraction(
                truth.polygon,
                tuple(prediction.polygon for prediction in predictions),
            ),
        )
        result = score_document(document, predictions)
        self.assertEqual(0.0, result["detection"]["recall"])
        self.assertEqual(0.0, result["endToEndExact"]["hmean"])
        self.assertFalse(result["documentExact"])

    def test_exact_union_dual_shapes_accept_rows_and_words_but_reject_slivers(self) -> None:
        words = (
            cord_word(0, 0, 0, "A", 0, 0, 20, 10),
            cord_word(0, 0, 1, "B", 80, 0, 100, 10),
        )
        truth = CordPhysicalRow(0, "A B", polygon(0, 0, 100, 10), words)
        document = self.document_with_rows((truth,), words=words)

        self.assert_perfect_metrics(
            score_document(
                document,
                (Prediction("A B", truth.polygon, 1.0, "whole-row"),),
            )
        )
        self.assert_perfect_metrics(
            score_document(
                document,
                tuple(
                    Prediction(word.text, word.polygon, 1.0, f"word-{index}")
                    for index, word in enumerate(words)
                ),
            )
        )

        endpoint_slivers = (
            Prediction("A", polygon(0, 0, 1, 10), 1.0, "left-sliver"),
            Prediction("B", polygon(99, 0, 100, 10), 1.0, "right-sliver"),
        )
        gamed = score_document(document, endpoint_slivers)
        self.assertEqual(0.0, gamed["detection"]["recall"])
        self.assertEqual(0.0, gamed["endToEndExact"]["hmean"])
        self.assertFalse(gamed["documentExact"])

        whitespace_only = score_document(
            document,
            (Prediction("A B", polygon(25, 0, 75, 10), 1.0, "blank-middle"),),
        )
        self.assertEqual(0.0, whitespace_only["detection"]["recall"])
        self.assertEqual(0.0, whitespace_only["endToEndExact"]["hmean"])
        self.assertFalse(whitespace_only["documentExact"])

        duplicated_shape = score_document(
            document,
            (
                Prediction("A", words[0].polygon, 1.0, "a-original"),
                Prediction("A", words[0].polygon, 1.0, "a-duplicate"),
                Prediction("B", words[1].polygon, 1.0, "b-original"),
            ),
        )
        self.assertAlmostEqual(0.8, duplicated_shape["tokenF1"])
        self.assertEqual(0.0, duplicated_shape["endToEndExact"]["hmean"])
        self.assertFalse(duplicated_shape["documentExact"])

    def test_real_cord_validation4_and_test96_word_split_regressions(self) -> None:
        def word(
            line_index: int,
            word_index: int,
            row_id: int,
            text: str,
            points,
        ) -> CordWord:
            return CordWord(line_index, word_index, row_id, text, convex_hull(points))

        # Pinned test row 96: two annotated rows form one spatial component
        # because the final zero is geometrically closer to the following row.
        test96_words = (
            word(0, 0, 2136582, "1", ((136, 890), (150, 890), (150, 912), (136, 912))),
            word(1, 0, 2136582, "BBQ", ((182, 884), (240, 884), (240, 910), (182, 910))),
            word(
                1,
                1,
                2136582,
                "Chicken",
                ((242, 882), (334, 882), (334, 908), (242, 908)),
            ),
            word(
                2,
                0,
                2136582,
                "41,000",
                ((491, 870), (567, 866), (569, 895), (492, 899)),
            ),
            word(3, 0, 2136582, "0", ((552, 890), (570, 890), (570, 914), (552, 914))),
            word(4, 0, 2136583, "1", ((136, 912), (150, 912), (150, 936), (136, 936))),
            word(
                5,
                0,
                2136583,
                "- Tidak",
                ((182, 908), (258, 908), (258, 934), (182, 934)),
            ),
            word(
                5,
                1,
                2136583,
                "Pedas",
                ((262, 906), (334, 906), (334, 932), (262, 932)),
            ),
        )
        test96 = self.document_with_rows((), words=test96_words, roi=polygon(0, 0, 800, 1200))

        # Pinned validation row 4: real left/right amount spacing leaves the
        # word union at only 0.2026732981 of the reference-row polygon.
        validation4_words = (
            word(3, 0, 2260454, "1", ((52, 544), (68, 544), (68, 568), (52, 568))),
            word(4, 0, 2260454, "0", ((544, 530), (564, 530), (564, 554), (544, 554))),
            word(
                5,
                0,
                2260454,
                "Sedang",
                ((121, 531), (213, 527), (214, 553), (122, 557)),
            ),
        )
        validation4 = self.document_with_rows(
            (), words=validation4_words, roi=polygon(0, 0, 800, 1200)
        )

        for document in (test96, validation4):
            predictions = tuple(
                Prediction(item.text, item.polygon, 1.0, f"word-{index}")
                for index, item in enumerate(document.words)
            )
            self.assert_perfect_metrics(score_document(document, predictions))
            self.assert_perfect_metrics(score_document(document, tuple(reversed(predictions))))

    def test_reference_order_cannot_be_rewritten_by_prediction_slope(self) -> None:
        top_word = CordWord(10, 0, 1, "A", polygon(0, 0, 100, 10))
        bottom_word = CordWord(0, 0, 2, "B", polygon(0, 20, 100, 30))
        rows = (
            CordPhysicalRow(0, "A", top_word.polygon, (top_word,)),
            CordPhysicalRow(1, "B", bottom_word.polygon, (bottom_word,)),
        )
        document = self.document_with_rows(rows)
        correct = score_document(
            document,
            (Prediction("A B", polygon(0, 0, 100, 30), 1.0, "correct"),),
        )
        wrong_horizontal = score_document(
            document,
            (Prediction("B A", polygon(0, 0, 100, 30), 1.0, "wrong-flat"),),
        )
        wrong_slanted = score_document(
            document,
            (
                Prediction(
                    "B A",
                    convex_hull(((0, 0), (100, 20), (100, 50), (0, 30))),
                    1.0,
                    "wrong-slanted",
                ),
            ),
        )
        self.assertEqual(1.0, correct["endToEndExact"]["hmean"])
        for wrong in (wrong_horizontal, wrong_slanted):
            self.assertEqual(1.0, wrong["tokenF1"])
            self.assertEqual(0.0, wrong["endToEndExact"]["hmean"])
            self.assertGreater(wrong["pageWer"], 0.0)

    def test_geometry_ties_preserve_raw_output_order_without_text_sorting(self) -> None:
        rows = (
            CordPhysicalRow(0, "A", polygon(0, 0, 100, 10), ()),
            CordPhysicalRow(1, "B", polygon(0, 10, 100, 20), ()),
        )
        document = self.document_with_rows(rows)
        combined = polygon(0, 0, 100, 20)
        correct = score_document(
            document,
            (
                Prediction("A", combined, 1.0, "z-record"),
                Prediction("B", combined, 1.0, "a-record"),
            ),
        )
        wrong = score_document(
            document,
            (
                Prediction("B", combined, 1.0, "a-record"),
                Prediction("A", combined, 1.0, "z-record"),
            ),
        )
        self.assertEqual(1.0, correct["endToEndExact"]["hmean"])
        self.assertEqual(1.0, wrong["tokenF1"])
        self.assertEqual(0.0, wrong["endToEndExact"]["hmean"])
        self.assertGreater(wrong["pageWer"], 0.0)

    def test_long_slanted_thin_rows_do_not_merge(self) -> None:
        first = convex_hull(((0, 0), (500, 100), (500, 110), (0, 10)))
        second = convex_hull(((0, 30), (500, 130), (500, 140), (0, 40)))
        rows, slope = _cluster_prediction_rows(
            (
                Prediction("A", first, 1.0, "a"),
                Prediction("B", second, 1.0, "b"),
            )
        )
        self.assertAlmostEqual(0.2, slope, places=2)
        self.assertEqual(["A", "B"], [row.text for row in rows])

    def test_swapped_and_repeated_tokens_fail_order_or_precision(self) -> None:
        rows = (
            CordPhysicalRow(0, "A", polygon(0, 0, 20, 10), ()),
            CordPhysicalRow(1, "B", polygon(0, 20, 20, 30), ()),
        )
        document = self.document_with_rows(rows)
        swapped = score_document(
            document,
            (
                Prediction("B", rows[0].polygon, 1.0, "top"),
                Prediction("A", rows[1].polygon, 1.0, "bottom"),
            ),
        )
        self.assertEqual(1.0, swapped["tokenF1"])
        self.assertEqual(0.0, swapped["endToEndExact"]["hmean"])
        self.assertGreater(swapped["pageWer"], 0.0)

        repeated = score_document(
            document,
            (
                Prediction("A A", rows[0].polygon, 1.0, "top-repeat"),
                Prediction("B", rows[1].polygon, 1.0, "bottom-exact"),
            ),
        )
        self.assertAlmostEqual(0.8, repeated["tokenF1"])
        self.assertGreater(repeated["pageCer"], 0.0)

    def test_correct_text_in_the_wrong_location_fails_end_to_end(self) -> None:
        row = CordPhysicalRow(0, "A", polygon(0, 0, 20, 10), ())
        document = self.document_with_rows((row,))
        result = score_document(
            document,
            (Prediction("A", polygon(50, 0, 70, 10), 1.0, "displaced"),),
        )
        self.assertEqual(0.0, result["detection"]["hmean"])
        self.assertEqual(0.0, result["endToEndExact"]["hmean"])
        self.assertEqual(1.0, result["tokenF1"])

    def test_multi_quad_ignore_uses_union_without_convex_hull_bridging(self) -> None:
        target = Prediction("noise", polygon(0, 0, 100, 10), 1.0, "noise")
        combined = (polygon(0, 0, 30, 10), polygon(30, 0, 60, 10))
        bridged_gap = (polygon(0, 0, 20, 10), polygon(80, 0, 100, 10))
        self.assertTrue(_ignored_prediction(target, combined))
        self.assertFalse(_ignored_prediction(target, bridged_gap))
        gap_prediction = Prediction("gap", polygon(40, 0, 60, 10), 1.0, "gap")
        self.assertFalse(_ignored_prediction(gap_prediction, bridged_gap))

    def test_valid_text_outside_roi_is_not_removed(self) -> None:
        row = CordPhysicalRow(0, "A", polygon(0, 70, 20, 80), ())
        document = self.document_with_rows(
            (row,),
            roi=polygon(0, 0, 100, 50),
        )
        result = score_document(
            document,
            (Prediction("A", row.polygon, 1.0, "valid-outside-roi"),),
        )
        self.assert_perfect_metrics(result)
        self.assertEqual(0, result["ignoredByRoi"])

    def test_exact_half_boundaries_are_explicit(self) -> None:
        truth = CordPhysicalRow(0, "A", polygon(0, 0, 100, 10), ())
        document = self.document_with_rows((truth,))
        half = Prediction("A", polygon(0, 0, 50, 10), 1.0, "half")
        result = score_document(document, (half,))
        self.assertEqual(1.0, result["detection"]["hmean"])

        roi_document = self.document_with_rows(
            (),
            roi=polygon(0, 0, 50, 100),
        )
        roi_result = score_document(
            roi_document,
            (Prediction("noise", polygon(0, 0, 100, 10), 1.0, "half-roi"),),
        )
        self.assertEqual(1, roi_result["ignoredByRoi"])
        self.assertEqual(0, roi_result["predictedLines"])

    def test_dontcare_prediction_is_not_counted_as_false_positive(self) -> None:
        predictions = (Prediction("noise", polygon(72, 72, 88, 88), 0.8, "ignored"),)
        result = score_document(self.document, predictions)
        self.assertEqual(1, result["ignoredPredictions"])
        self.assertEqual(0, result["predictedLines"])
        self.assertEqual(0, result["detection"]["falsePositive"])
        self.assertEqual(1, result["ignoredByDontcare"])
        self.assertEqual(0, result["counts"]["predictedCharacters"])
        self.assertEqual(0, result["counts"]["predictedWords"])

    def test_repeating_symbol_prediction_is_not_counted_as_false_positive(self) -> None:
        predictions = (Prediction("-----", polygon(2, 72, 18, 78), 0.8, "ignored"),)
        result = score_document(self.document, predictions)
        self.assertEqual(1, result["ignoredPredictions"])
        self.assertEqual(1, result["ignoredByRepeatingSymbol"])
        self.assertEqual(0, result["predictedLines"])
        self.assertEqual(0, result["detection"]["falsePositive"])

    def test_aggregate_reports_micro_macro_and_worst_documents(self) -> None:
        exact_predictions = tuple(
            Prediction(line.text, line.polygon, 1.0, f"p{index}")
            for index, line in enumerate(self.document.lines)
        )
        exact = score_document(self.document, exact_predictions)
        second = dict(score_document(self.document, ()))
        second["rowIndex"] = 1
        second["imageSha256"] = "c" * 64
        result = aggregate_metrics((exact, second))
        self.assertEqual(2, result["micro"]["documents"])
        self.assertLess(result["micro"]["detection"]["recall"], 1.0)
        self.assertEqual(4, result["micro"]["dontcareRegions"])
        self.assertEqual(2, result["micro"]["repeatingSymbolRegions"])
        self.assertEqual(1, result["worstDocuments"][0]["rowIndex"])
        self.assertIn("pageCer", result["macro"])

    def test_report_is_deterministic_for_fixed_inputs(self) -> None:
        corpus_manifest = self.root / "manifest.jsonl"
        worker_manifest = self.root / "worker.jsonl"
        inventory = self.root / "inventory.txt"
        for path in (corpus_manifest, worker_manifest, inventory):
            path.write_text("fixture\n", encoding="utf-8")
        corpus = ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=corpus_manifest,
            worker_manifest=worker_manifest,
            inventory=inventory,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="9" * 64,
        )
        metrics = aggregate_metrics((score_document(self.document, ()),))
        backend = {
            "resolvedProvider": "cpu",
            "canonicalEvidenceSha256": "f" * 64,
            "metricsSha256": "1" * 64,
            "provenancePassed": True,
            "byteDeterministic": True,
            "canonicalEvidenceDeterministic": True,
            "metricsDeterministic": True,
            "stableResolvedProvider": True,
            "stableRuntime": True,
            "stableResolvedThreadCounts": True,
            "stableResolvedWorkerCounts": True,
            "executionProviderCountsStable": True,
            "throughputComparable": True,
            "qualityGatePassed": True,
            "metrics": metrics,
        }
        kwargs = {
            "generated_at_utc": "2026-08-05T00:00:00Z",
            "parquet_sha256": CORD_V2_TEST_SHA256,
            "corpus": corpus,
            "engine": {"name": "rapidocr", "modelPackSha256": "2" * 64},
            "settings": {"threads": 0},
            "thresholds": {
                "minimumDetectionHmean": 0.0,
                "minimumEndToEndHmean": None,
                "minimumWordAccuracy": None,
                "maximumPageCer": None,
                "maximumPageWer": None,
            },
            "backends": (backend,),
            "release_matrix": False,
        }
        first = assemble_report(**kwargs)
        second = assemble_report(**kwargs)
        self.assertEqual(canonical_json(first), canonical_json(second))
        self.assertEqual(3, first["schemaVersion"])
        self.assertEqual("2.1.2", first["benchmarkDependencies"]["shapely"])
        self.assertTrue(first["benchmarkDependencies"]["geos"])
        self.assertEqual(
            0.5,
            first["protocolConstants"]["segmentationOverlapThreshold"],
        )
        self.assertTrue(first["integrityPassed"])
        self.assertTrue(first["passed"])
        without_thresholds = dict(kwargs)
        without_thresholds["thresholds"] = {key: None for key in kwargs["thresholds"]}
        integrity_only = assemble_report(**without_thresholds)
        self.assertTrue(integrity_only["integrityPassed"])
        self.assertIsNone(integrity_only["qualityGate"]["passed"])
        self.assertFalse(integrity_only["passed"])

    def test_cross_backend_critical_hash_and_confidence_tolerance_are_explicit(self) -> None:
        def evidence(provider: str, confidence: float, *, text: str = "TOTAL"):
            record = {
                "recordId": f"{provider}-record",
                "sourceFile": str(self.document.path),
                "text": text,
                "origin": {"kind": "ocr", "provider": provider},
                "attributes": {
                    "box": [[0, 20], [50, 20], [50, 30], [0, 30]],
                    "confidence": confidence,
                    "coordinateSpace": "render-pixels",
                    "executionProvider": provider,
                    "pageNumber": 1,
                    "provider": provider,
                    "requestedProvider": provider,
                    "requestedThreads": 0,
                    "resolvedThreadCounts": {provider: 1},
                    "resolvedWorkerCounts": {provider: 1},
                    "runtimeSha256": provider,
                },
            }
            assessment = {
                "sourceFile": str(self.document.path),
                "provider": provider,
                "requestedProvider": provider,
                "requestedThreads": 0,
                "resolvedThreadCounts": {provider: 1},
                "resolvedWorkerCounts": {provider: 1},
                "runtimeSha256": provider,
                "status": "processed",
            }
            return record, assessment

        cpu_record, cpu_assessment = evidence("cpu", 0.9)
        gpu_record, gpu_assessment = evidence("directml", 0.90005)
        self.assertNotEqual(
            canonical_evidence_sha256((cpu_record,), (cpu_assessment,)),
            canonical_evidence_sha256((gpu_record,), (gpu_assessment,)),
        )
        self.assertEqual(
            critical_evidence_sha256((cpu_record,), (cpu_assessment,)),
            critical_evidence_sha256((gpu_record,), (gpu_assessment,)),
        )
        parity = confidence_parity(
            (
                _confidence_by_critical_record((cpu_record,)),
                _confidence_by_critical_record((gpu_record,)),
            )
        )
        self.assertTrue(parity["passed"])
        self.assertLessEqual(parity["maximumAbsoluteDelta"], CONFIDENCE_PARITY_MAX_ABS_DELTA)

        high_delta_record, _ = evidence("directml", 0.901)
        high_delta = confidence_parity(
            (
                _confidence_by_critical_record((cpu_record,)),
                _confidence_by_critical_record((high_delta_record,)),
            )
        )
        self.assertFalse(high_delta["passed"])

        changed_record, changed_assessment = evidence("directml", 0.9, text="SUBTOTAL")
        self.assertNotEqual(
            critical_evidence_sha256((cpu_record,), (cpu_assessment,)),
            critical_evidence_sha256((changed_record,), (changed_assessment,)),
        )
        structural = confidence_parity(
            (
                _confidence_by_critical_record((cpu_record,)),
                _confidence_by_critical_record((changed_record,)),
            )
        )
        self.assertFalse(structural["passed"])
        self.assertFalse(structural["structurallyAligned"])

    def test_release_matrix_uses_critical_content_plus_bounded_confidence(self) -> None:
        metrics = aggregate_metrics(
            (
                score_document(
                    self.document,
                    tuple(
                        Prediction(line.text, line.polygon, 1.0, f"p-{index}")
                        for index, line in enumerate(self.document.lines)
                    ),
                ),
            )
        )
        confidence_maps = (
            {"same-critical-record": [0.9]},
            {"same-critical-record": [0.90005]},
            {"same-critical-record": [0.89996]},
        )
        providers = ("cpu", "directml", "hybrid-directml-cpu")
        backends = []
        for index, provider in enumerate(providers):
            backends.append(
                {
                    "resolvedProvider": provider,
                    "canonicalEvidenceSha256": str(index + 1) * 64,
                    "criticalEvidenceSha256": "a" * 64,
                    "_confidenceByCriticalRecord": confidence_maps[index],
                    "metricsSha256": "b" * 64,
                    "provenancePassed": True,
                    "byteDeterministic": True,
                    "canonicalEvidenceDeterministic": True,
                    "criticalEvidenceDeterministic": True,
                    "metricsDeterministic": True,
                    "stableResolvedProvider": True,
                    "stableRuntime": True,
                    "stableResolvedThreadCounts": True,
                    "stableResolvedWorkerCounts": True,
                    "executionProviderCountsStable": True,
                    "throughputComparable": True,
                    "qualityGatePassed": True,
                    "metrics": metrics,
                }
            )
        report = assemble_report(
            generated_at_utc="2026-08-05T00:00:00Z",
            parquet_sha256=CORD_V2_TEST_SHA256,
            corpus=self.corpus(),
            engine={"name": "rapidocr"},
            settings={"threads": 0},
            thresholds={
                "minimumDetectionHmean": 0.0,
                "minimumEndToEndHmean": None,
                "minimumWordAccuracy": None,
                "maximumPageCer": None,
                "maximumPageWer": None,
            },
            backends=tuple(backends),
            release_matrix=True,
        )
        self.assertFalse(report["parity"]["crossBackendCanonicalEvidence"])
        self.assertTrue(report["parity"]["crossBackendCriticalEvidence"])
        self.assertTrue(report["parity"]["confidence"]["passed"])
        self.assertTrue(report["releaseMatrix"]["passed"])
        self.assertTrue(report["integrityPassed"])
        self.assertTrue(report["passed"])

    def test_corpus_view_manifest_is_row_and_hash_stable(self) -> None:
        placeholder = self.root / "placeholder"
        placeholder.write_text("fixture\n", encoding="utf-8")
        corpus = ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=placeholder,
            worker_manifest=placeholder,
            inventory=placeholder,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="f" * 64,
        )
        first = create_corpus_view(corpus, (0,), self.root / "view", name="fixed")
        second = create_corpus_view(corpus, (0,), self.root / "view", name="fixed")
        self.assertEqual(first.corpus_manifest_sha256, second.corpus_manifest_sha256)
        self.assertEqual(first.selection_sha256, second.selection_sha256)
        row = json.loads(first.corpus_manifest.read_text(encoding="utf-8"))
        self.assertEqual(0, row["rowIndex"])
        self.assertEqual(self.document.sha256, row["sha256"])
        self.assertEqual(self.document.relative_path, row["path"])
        self.assertEqual(2, row["dontcareRegions"])
        self.assertEqual(1, row["repeatingSymbolRegions"])
        self.assertEqual(0, row["clippedValidLines"])
        self.assertEqual(0, row["clippedDontcareRegions"])
        self.assertEqual(0, row["clippedRepeatingSymbolRegions"])

    def test_backend_uses_full_quality_once_and_subset_for_determinism(self) -> None:
        placeholder = self.root / "placeholder"
        placeholder.write_text("fixture\n", encoding="utf-8")
        corpus = ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=placeholder,
            worker_manifest=placeholder,
            inventory=placeholder,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="f" * 64,
        )
        metrics = aggregate_metrics((score_document(self.document, ()),))

        def result(pair_hash: str, evidence_hash: str, metrics_hash: str):
            return {
                "resolvedProvider": "cpu",
                "requestedThreads": 0,
                "resolvedThreadCounts": {"cpu": 16},
                "resolvedWorkerCounts": {"cpu": 4},
                "workerSha256": "9" * 64,
                "runtimeSha256": "a" * 64,
                "elapsedSeconds": 1.0,
                "documentsPerSecond": 1.0,
                "rawOutputHashes": {"pairSha256": pair_hash},
                "canonicalEvidenceSha256": evidence_hash,
                "metricsSha256": metrics_hash,
                "provenancePassed": True,
                "qualityGatePassed": True,
                "executionProviderRecordCounts": {"cpu": 2},
                "hybridLaneRecordCoverage": {
                    "bothLanesProducedRecords": False,
                    "cpuLaneRecords": 2,
                    "nonCpuLaneRecords": 0,
                },
                "throughputComparable": True,
                "metrics": metrics,
            }

        quality = result("1" * 64, "2" * 64, "3" * 64)
        repeated = result("4" * 64, "5" * 64, "6" * 64)
        with patch(
            "benchmark_ocr_cord.run_backend_once",
            side_effect=[quality, repeated, dict(repeated)],
        ) as runner:
            backend = benchmark_backend(
                backend=Backend("cpu", Path(sys.executable)),
                quality_corpus=corpus,
                determinism_corpus=corpus,
                determinism_repetitions=2,
                output_root=self.root / "results",
            )
        self.assertEqual(3, runner.call_count)
        self.assertTrue(backend["byteDeterministic"])
        self.assertTrue(backend["canonicalEvidenceDeterministic"])
        self.assertEqual("2" * 64, backend["canonicalEvidenceSha256"])
        self.assertEqual("5" * 64, backend["determinism"]["runs"][0]["canonicalEvidenceSha256"])

    def test_physical_parquet_size_is_a_hard_gate(self) -> None:
        candidate = self.root / "cord.parquet"
        candidate.write_bytes(b"not the pinned corpus")
        with self.assertRaises(BenchmarkError):
            verify_cord_parquet(candidate)

    def test_provenance_rejects_duplicate_assessments(self) -> None:
        placeholder = self.root / "placeholder"
        placeholder.write_text("fixture\n", encoding="utf-8")
        corpus = ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=placeholder,
            worker_manifest=placeholder,
            inventory=placeholder,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="f" * 64,
        )
        assessment = {
            "sourceFile": str(self.document.path),
            "provider": "cpu",
            "status": "processed",
            "pages": 1,
            "sourceSha256": self.document.sha256,
            "modelPackSha256": "9" * 64,
            "requestedThreads": 0,
            "resolvedThreadCounts": {"cpu": 16},
            "resolvedWorkerCounts": {"cpu": 4},
        }
        passed, errors, provider, thread_counts, worker_counts = validate_run_provenance(
            (), (assessment, dict(assessment)), corpus, "9" * 64, 0
        )
        self.assertFalse(passed)
        self.assertEqual("cpu", provider)
        self.assertEqual({"cpu": 16}, thread_counts)
        self.assertEqual({"cpu": 4}, worker_counts)
        self.assertIn("the run emitted a duplicate or malformed assessment source", errors)

    def test_embedded_png_dimensions_must_match_the_annotation(self) -> None:
        raw_image = b"\x89PNG\r\n\x1a\n" + b"\x00\x00\x00\rIHDR" + struct.pack(">II", 100, 100)
        annotation = parse_cord_annotation(0, annotation_fixture())

        self.assertEqual((100, 100), _embedded_image_dimensions(raw_image))
        self.assertEqual((100, 100), _verify_embedded_image_dimensions(raw_image, annotation))
        mismatched = raw_image[:-4] + struct.pack(">I", 99)
        with self.assertRaises(BenchmarkError):
            _verify_embedded_image_dimensions(mismatched, annotation)

    def test_shapely_and_native_geos_identity_are_pinned(self) -> None:
        _shapely_runtime.cache_clear()
        _, _, version, geos_version = _shapely_runtime()
        self.assertEqual("2.1.2", version)
        self.assertTrue(geos_version)
        try:
            with patch("benchmark_ocr_cord._distribution_version", return_value="2.1.1"):
                _shapely_runtime.cache_clear()
                with self.assertRaises(BenchmarkError):
                    _shapely_runtime()
            with patch(
                "benchmark_ocr_cord._distribution_version",
                return_value="not-installed",
            ):
                _shapely_runtime.cache_clear()
                with self.assertRaises(BenchmarkError):
                    _shapely_runtime()
        finally:
            _shapely_runtime.cache_clear()

    def test_auto_thread_provenance_rejects_nonconservative_gpu_threads(self) -> None:
        placeholder = self.root / "thread-placeholder"
        placeholder.write_text("fixture\n", encoding="utf-8")
        corpus = ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=placeholder,
            worker_manifest=placeholder,
            inventory=placeholder,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="f" * 64,
        )
        assessment = {
            "sourceFile": str(self.document.path),
            "provider": "directml",
            "status": "processed",
            "pages": 1,
            "sourceSha256": self.document.sha256,
            "modelPackSha256": "9" * 64,
            "requestedThreads": 0,
            "resolvedThreadCounts": {"directml": 2},
            "resolvedWorkerCounts": {"directml": 1},
        }

        passed, errors, _, _, _ = validate_run_provenance((), (assessment,), corpus, "9" * 64, 0)

        self.assertFalse(passed)
        self.assertIn("row 0 assessment used non-conservative automatic GPU threads", errors)

    def test_worker_provenance_rejects_missing_and_nonconservative_counts(self) -> None:
        placeholder = self.root / "worker-placeholder"
        placeholder.write_text("fixture\n", encoding="utf-8")
        corpus = ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=placeholder,
            worker_manifest=placeholder,
            inventory=placeholder,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="f" * 64,
        )
        assessment = {
            "sourceFile": str(self.document.path),
            "provider": "directml",
            "status": "processed",
            "pages": 1,
            "sourceSha256": self.document.sha256,
            "modelPackSha256": "9" * 64,
            "requestedThreads": 0,
            "resolvedThreadCounts": {"directml": 1},
        }
        passed, errors, _, _, _ = validate_run_provenance((), (assessment,), corpus, "9" * 64, 0)
        self.assertFalse(passed)
        self.assertIn("row 0 assessment reported invalid resolved worker-count keys", errors)

        assessment["resolvedWorkerCounts"] = {"directml": 2}
        passed, errors, _, _, _ = validate_run_provenance((), (assessment,), corpus, "9" * 64, 0)
        self.assertFalse(passed)
        self.assertIn(
            "row 0 assessment used non-conservative GPU worker counts",
            errors,
        )

    def test_backend_rejects_unstable_resolved_worker_counts(self) -> None:
        placeholder = self.root / "worker-stability-placeholder"
        placeholder.write_text("fixture\n", encoding="utf-8")
        corpus = ExtractedCorpus(
            documents=(self.document,),
            corpus_manifest=placeholder,
            worker_manifest=placeholder,
            inventory=placeholder,
            corpus_manifest_sha256="d" * 64,
            worker_manifest_sha256="e" * 64,
            selection_sha256="f" * 64,
        )
        metrics = aggregate_metrics((score_document(self.document, ()),))

        def result(worker_count: int) -> dict[str, object]:
            return {
                "resolvedProvider": "cpu",
                "requestedThreads": 0,
                "resolvedThreadCounts": {"cpu": 16},
                "resolvedWorkerCounts": {"cpu": worker_count},
                "workerSha256": "9" * 64,
                "runtimeSha256": "a" * 64,
                "elapsedSeconds": 1.0,
                "documentsPerSecond": 1.0,
                "rawOutputHashes": {"pairSha256": "b" * 64},
                "canonicalEvidenceSha256": "c" * 64,
                "metricsSha256": "d" * 64,
                "provenancePassed": True,
                "qualityGatePassed": True,
                "executionProviderRecordCounts": {"cpu": 2},
                "hybridLaneRecordCoverage": {
                    "bothLanesProducedRecords": False,
                    "cpuLaneRecords": 2,
                    "nonCpuLaneRecords": 0,
                },
                "throughputComparable": True,
                "metrics": metrics,
            }

        with patch(
            "benchmark_ocr_cord.run_backend_once",
            side_effect=[result(4), result(4), result(3)],
        ):
            backend = benchmark_backend(
                backend=Backend("cpu", Path(sys.executable)),
                quality_corpus=corpus,
                determinism_corpus=corpus,
                determinism_repetitions=2,
                output_root=self.root / "worker-stability-results",
            )

        self.assertFalse(backend["stableResolvedWorkerCounts"])
        report_backend = dict(backend)
        report_backend.update(
            {
                "byteDeterministic": True,
                "canonicalEvidenceDeterministic": True,
                "metricsDeterministic": True,
                "stableResolvedProvider": True,
                "stableRuntime": True,
                "stableResolvedThreadCounts": True,
                "executionProviderCountsStable": True,
                "throughputComparable": True,
            }
        )
        report = assemble_report(
            generated_at_utc="2026-08-05T00:00:00Z",
            parquet_sha256=CORD_V2_TEST_SHA256,
            corpus=corpus,
            engine={"name": "rapidocr"},
            settings={"threads": 0},
            thresholds={
                "minimumDetectionHmean": 0.0,
                "minimumEndToEndHmean": None,
                "minimumWordAccuracy": None,
                "maximumPageCer": None,
                "maximumPageWer": None,
            },
            backends=(report_backend,),
            release_matrix=False,
        )
        self.assertFalse(report["integrityPassed"])

    def test_failed_rerun_marks_a_prior_report_incomplete(self) -> None:
        output = self.root / "cord-report.json"
        output.write_text('{"passed":true}\n', encoding="utf-8")
        args = parse_arguments(
            [
                "--cord-parquet",
                str(self.root / "missing.parquet"),
                "--worker",
                str(Path(__file__)),
                "--model-pack",
                str(Path(__file__)),
                "--backend",
                f"cpu={sys.executable}",
                "--output",
                str(output),
                "--work-directory",
                str(self.root / "work"),
            ]
        )

        with self.assertRaises(BenchmarkError):
            run(args)

        self.assertEqual('{"passed":true}\n', output.read_text(encoding="utf-8"))
        self.assertTrue(output.with_name(output.name + ".incomplete").is_file())


if __name__ == "__main__":
    unittest.main()
