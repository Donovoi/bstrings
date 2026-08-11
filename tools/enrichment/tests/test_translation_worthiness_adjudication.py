from __future__ import annotations

import hashlib
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

TEST_ROOT = Path(__file__).resolve().parent
ENRICHMENT_ROOT = TEST_ROOT.parent
if str(ENRICHMENT_ROOT) not in sys.path:
    sys.path.insert(0, str(ENRICHMENT_ROOT))

import train_translation_worthiness as trainer  # noqa: E402
import translation_worthiness_adjudication as adjudication  # noqa: E402
from translation_worthiness_corpus import validate_corpus  # noqa: E402


def canonical(value: object) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    ).encode()


def queue_row(index: int, project: str, text: str, *, source_kind: str = "code") -> dict:
    return {
        "schemaVersion": 1,
        "id": f"row-{index:02d}",
        "groupId": f"group-{index:02d}",
        "text": text,
        "originFamily": "native-static",
        "scriptFamily": "latin",
        "lengthBand": "8-31",
        "sourceId": f"source-{index:02d}",
        "sourceProject": f"ExampleOrg/{project}",
        "sourceRevision": str(index % 10) * 40,
        "sourcePath": f"src/file-{index}.txt",
        "sourceOrdinal": index,
        "sourceKind": source_kind,
        "sourceProvenance": "upstream-repository-first-party-source-path",
        "licenseId": "MIT",
        "label": "UNLABELED",
        "reviewState": "pending",
        "requiredIndependentReviews": 2,
        "researchOnly": True,
        "promotionEligible": False,
    }


class TranslationWorthinessAdjudicationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.queue = self.root / "queue.jsonl"
        self.rows = [
            queue_row(1, "project-a", "human words"),
            queue_row(2, "project-a", "machine token"),
            queue_row(3, "project-b", "mixed message"),
            queue_row(4, "project-b", "another token"),
            queue_row(5, "project-c", "localized line"),
            queue_row(6, "project-c", "binary token"),
            queue_row(7, "project-d", "human message"),
            queue_row(8, "project-d", "opcode sequence"),
        ]
        self.queue.write_bytes(b"".join(canonical(row) for row in self.rows))

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def review(
        self,
        path: Path,
        reviewer: str,
        labels: list[str],
        *,
        confidences: list[str] | None = None,
        adjudicator: bool = False,
        ids: list[str] | None = None,
        reviewer_type: str = "human",
    ) -> None:
        selected = self.rows if ids is None else [row for row in self.rows if row["id"] in ids]
        confidence_values = confidences or ["high"] * len(selected)
        reviews = []
        for row, label, confidence in zip(selected, labels, confidence_values, strict=True):
            reviews.append(
                {
                    "id": row["id"],
                    "textSha256": hashlib.sha256(row["text"].encode()).hexdigest(),
                    "label": label,
                    "confidence": confidence,
                    "rationaleCodes": [
                        "mixed-human-machine" if label == "mixed" else "human-fragment"
                    ],
                }
            )
        value = {
            "schemaVersion": 1,
            "artifactType": (
                "translation-worthiness-adjudication"
                if adjudicator
                else "translation-worthiness-independent-review"
            ),
            "researchOnly": True,
            "promotionEligible": False,
            "queueSha256": hashlib.sha256(self.queue.read_bytes()).hexdigest(),
            "reviewerId": reviewer,
            "reviewerType": reviewer_type,
            "independent": not adjudicator,
            "otherReviewsVisible": adjudicator,
            "reviews": reviews,
        }
        path.write_bytes(canonical(value))

    def test_two_independent_agreements_publish_project_disjoint_bundle(self) -> None:
        review_a = self.root / "a.json"
        review_b = self.root / "b.json"
        labels = [
            "human-worthy",
            "machine",
            "mixed",
            "machine",
            "human-worthy",
            "machine",
            "human-worthy",
            "machine",
        ]
        self.review(review_a, "reviewer-a", labels)
        self.review(review_b, "reviewer-b", labels)
        output = self.root / "result"

        report = adjudication.build_bundle(self.queue, review_a, review_b, output)

        self.assertEqual("complete", report["status"])
        self.assertFalse(report["promotionEligible"])
        self.assertEqual(0, report["adjudicatedRecords"])
        self.assertEqual(
            {
                "corpus.jsonl",
                "dual-adjudicated-corpus.json",
                "splits.json",
                "provenance.json",
                "report.json",
            },
            {path.name for path in output.iterdir()},
        )
        fasttext_corpus = json.loads((output / "dual-adjudicated-corpus.json").read_bytes())
        self.assertEqual(
            "translation-worthiness-dual-adjudicated-corpus",
            fasttext_corpus["artifactType"],
        )
        self.assertTrue(
            all(
                row["adjudication"]["labels"] == [row["label"], row["label"]]
                for row in fasttext_corpus["rows"]
            )
        )
        provenance = json.loads((output / "provenance.json").read_bytes())
        self.assertIn("not technically proven", provenance["reviewEvidenceBoundary"])
        project_splits: dict[str, set[str]] = {}
        for row in provenance["records"]:
            project_splits.setdefault(row["sourceProject"], set()).add(row["split"])
        self.assertTrue(all(len(splits) == 1 for splits in project_splits.values()))
        self.assertEqual({"train", "calibration", "test"}, set().union(*project_splits.values()))
        validated = validate_corpus(
            output / "corpus.jsonl",
            output / "splits.json",
            corpus_id=adjudication.CORPUS_ID,
            license_id=adjudication.CORPUS_LICENSE,
        )
        self.assertEqual(8, len(validated.rows))
        self.assertEqual(adjudication.CORPUS_ID, validated.corpus_id)
        _, _, manifest_bytes = trainer.build_artifacts(
            output / "corpus.jsonl",
            output / "splits.json",
            ablation="combined",
            experiment_id="reviewed-public-smoke",
            bucket_count=64,
            epochs=1,
            learning_rate=0.1,
            trainer_path=Path(trainer.__file__),
            corpus_id=adjudication.CORPUS_ID,
            license_id=adjudication.CORPUS_LICENSE,
        )
        manifest = json.loads(manifest_bytes)
        self.assertEqual(adjudication.CORPUS_ID, manifest["corpusId"])
        self.assertEqual(adjudication.CORPUS_LICENSE, manifest["license"])

    def test_conflict_requires_distinct_third_human_adjudicator(self) -> None:
        review_a = self.root / "a.json"
        review_b = self.root / "b.json"
        third = self.root / "third.json"
        self.review(review_a, "reviewer-a", ["human-worthy"] * len(self.rows))
        self.review(
            review_b,
            "reviewer-b",
            ["machine", *("human-worthy" for _ in self.rows[1:])],
        )
        with self.assertRaisesRegex(adjudication.AdjudicationError, "require adjudication"):
            adjudication.build_bundle(self.queue, review_a, review_b, self.root / "missing")
        self.review(
            third,
            "reviewer-c",
            ["mixed"],
            adjudicator=True,
            ids=["row-01"],
        )
        report = adjudication.build_bundle(
            self.queue, review_a, review_b, self.root / "result", third
        )
        self.assertEqual(1, report["adjudicatedRecords"])

    def test_automated_or_reused_reviewers_are_rejected(self) -> None:
        review_a = self.root / "a.json"
        review_b = self.root / "b.json"
        labels = ["machine"] * len(self.rows)
        self.review(review_a, "reviewer-a", labels, reviewer_type="automated")
        self.review(review_b, "reviewer-b", labels)
        with self.assertRaisesRegex(adjudication.AdjudicationError, "review contract"):
            adjudication.build_bundle(self.queue, review_a, review_b, self.root / "automated")
        self.review(review_a, "reviewer-b", labels)
        with self.assertRaisesRegex(adjudication.AdjudicationError, "distinct human"):
            adjudication.build_bundle(self.queue, review_a, review_b, self.root / "reused")

    def test_review_must_bind_exact_text_and_complete_queue(self) -> None:
        review_a = self.root / "a.json"
        review_b = self.root / "b.json"
        labels = ["machine"] * len(self.rows)
        self.review(review_a, "reviewer-a", labels)
        self.review(review_b, "reviewer-b", labels)
        value = json.loads(review_a.read_bytes())
        value["reviews"][0]["textSha256"] = "0" * 64
        review_a.write_bytes(canonical(value))
        with self.assertRaisesRegex(adjudication.AdjudicationError, "exact queue text"):
            adjudication.build_bundle(self.queue, review_a, review_b, self.root / "stale")

    def test_low_confidence_requires_adjudication(self) -> None:
        review_a = self.root / "a.json"
        review_b = self.root / "b.json"
        labels = ["machine"] * len(self.rows)
        self.review(
            review_a,
            "reviewer-a",
            labels,
            confidences=["low", *("high" for _ in self.rows[1:])],
        )
        self.review(review_b, "reviewer-b", labels)
        with self.assertRaisesRegex(adjudication.AdjudicationError, "require adjudication"):
            adjudication.build_bundle(self.queue, review_a, review_b, self.root / "low")

    def test_existing_output_is_not_overwritten(self) -> None:
        review_a = self.root / "a.json"
        review_b = self.root / "b.json"
        labels = ["machine"] * len(self.rows)
        self.review(review_a, "reviewer-a", labels)
        self.review(review_b, "reviewer-b", labels)
        output = self.root / "result"
        output.mkdir()
        sentinel = output / "sentinel.txt"
        sentinel.write_text("previous", encoding="utf-8")
        with self.assertRaisesRegex(adjudication.AdjudicationError, "must be absent"):
            adjudication.build_bundle(self.queue, review_a, review_b, output)
        self.assertEqual("previous", sentinel.read_text(encoding="utf-8"))
        self.assertFalse(any("partial" in path.name for path in self.root.iterdir()))

    def test_strict_json_rejects_duplicate_properties_and_nonfinite_numbers(self) -> None:
        with self.assertRaisesRegex(adjudication.AdjudicationError, "duplicate"):
            adjudication._strict_json(b'{"value":1,"value":2}', name="fixture")
        with self.assertRaisesRegex(adjudication.AdjudicationError, "non-finite"):
            adjudication._strict_json(b'{"value":NaN}', name="fixture")

    def test_output_must_remain_external_and_reparse_points_are_detected(self) -> None:
        with self.assertRaisesRegex(adjudication.AdjudicationError, "outside the repository"):
            adjudication._require_external(
                adjudication.REPOSITORY_ROOT / "would-be-private-output",
                name="fixture output",
            )
        self.assertTrue(
            adjudication._is_reparse(
                SimpleNamespace(st_file_attributes=0x400)  # type: ignore[arg-type]
            )
        )
        blocked = self.root / "blocked"
        blocked.mkdir()
        real_lstat = os.lstat

        def fake_lstat(path: os.PathLike[str] | str) -> os.stat_result | SimpleNamespace:
            result = real_lstat(path)
            if Path(path) == blocked:
                return SimpleNamespace(st_mode=result.st_mode, st_file_attributes=0x400)
            return result

        with (
            patch.object(adjudication.os, "lstat", side_effect=fake_lstat),
            self.assertRaisesRegex(adjudication.AdjudicationError, "reparse component"),
        ):
            adjudication._reject_reparse_components(blocked, name="fixture")

    def test_duplicate_text_links_projects_into_one_split_component(self) -> None:
        linked_queue = self.root / "linked-queue.jsonl"
        rows = [
            queue_row(1, "project-a", "shared text"),
            queue_row(2, "project-b", "shared text"),
            queue_row(3, "project-b", "unique beta"),
            queue_row(4, "project-c", "unique gamma"),
            queue_row(5, "project-d", "unique delta"),
        ]
        linked_queue.write_bytes(b"".join(canonical(row) for row in rows))
        queue_rows, _, _ = adjudication.load_queue(linked_queue)

        project_split, component_group, groups_by_split = adjudication._split_projects(queue_rows)

        self.assertEqual(
            project_split["ExampleOrg/project-a"],
            project_split["ExampleOrg/project-b"],
        )
        self.assertEqual(
            component_group["ExampleOrg/project-a"],
            component_group["ExampleOrg/project-b"],
        )
        self.assertEqual(3, sum(len(groups) for groups in groups_by_split.values()))

    def test_text_only_challenger_excludes_context_conflicting_duplicate_labels(self) -> None:
        linked_queue = self.root / "context-queue.jsonl"
        rows = [
            queue_row(1, "project-a", "shared text"),
            queue_row(2, "project-b", "shared text"),
            queue_row(3, "project-b", "unique beta"),
            queue_row(4, "project-c", "unique gamma"),
            queue_row(5, "project-d", "unique delta"),
        ]
        linked_queue.write_bytes(b"".join(canonical(row) for row in rows))
        labels = {
            "row-01": "human-worthy",
            "row-02": "machine",
            "row-03": "machine",
            "row-04": "human-worthy",
            "row-05": "mixed",
        }

        def write_review(path: Path, reviewer: str) -> None:
            path.write_bytes(
                canonical(
                    {
                        "schemaVersion": 1,
                        "artifactType": "translation-worthiness-independent-review",
                        "researchOnly": True,
                        "promotionEligible": False,
                        "queueSha256": hashlib.sha256(linked_queue.read_bytes()).hexdigest(),
                        "reviewerId": reviewer,
                        "reviewerType": "human",
                        "independent": True,
                        "otherReviewsVisible": False,
                        "reviews": [
                            {
                                "id": row["id"],
                                "textSha256": hashlib.sha256(
                                    row["text"].encode("utf-8")
                                ).hexdigest(),
                                "label": labels[row["id"]],
                                "confidence": "high",
                                "rationaleCodes": ["human-fragment"],
                            }
                            for row in rows
                        ],
                    }
                )
            )

        review_a = self.root / "context-a.json"
        review_b = self.root / "context-b.json"
        write_review(review_a, "reviewer-a")
        write_review(review_b, "reviewer-b")

        report = adjudication.build_bundle(
            linked_queue, review_a, review_b, self.root / "context-result"
        )
        fasttext = json.loads(
            (self.root / "context-result" / "dual-adjudicated-corpus.json").read_bytes()
        )

        self.assertEqual(2, report["fastTextExcludedContextConflictRecords"])
        self.assertEqual(
            {"row-03", "row-04", "row-05"},
            {row["id"] for row in fasttext["rows"]},
        )


if __name__ == "__main__":
    unittest.main()
