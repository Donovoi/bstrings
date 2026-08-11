from __future__ import annotations

import hashlib
import json
import os
import stat
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

ENRICHMENT_ROOT = Path(__file__).resolve().parents[1]
if str(ENRICHMENT_ROOT) not in sys.path:
    sys.path.insert(0, str(ENRICHMENT_ROOT))

import translation_worthiness_adjudication as adjudication  # noqa: E402
import translation_worthiness_review_packet as packets  # noqa: E402


def canonical(value: object) -> bytes:
    return (
        json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")) + "\n"
    ).encode("utf-8")


def queue_row(index: int, text: str) -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "id": f"row-{index:02d}",
        "groupId": f"group-{index:02d}",
        "text": text,
        "originFamily": "native-static",
        "scriptFamily": "latin",
        "lengthBand": "8-31",
        "sourceId": f"source-{index:02d}",
        "sourceProject": f"ExampleOrg/project-{index:02d}",
        "sourceRevision": f"{index % 10}" * 40,
        "sourcePath": f"src/file-{index:02d}.txt",
        "sourceOrdinal": index,
        "sourceKind": "code",
        "sourceProvenance": "upstream-repository-first-party-source-path",
        "licenseId": "MIT",
        "label": "UNLABELED",
        "reviewState": "pending",
        "requiredIndependentReviews": 2,
        "researchOnly": True,
        "promotionEligible": False,
    }


class TranslationWorthinessReviewPacketTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.queue = self.root / "queue.jsonl"
        self.rows = [
            queue_row(1, "human words"),
            queue_row(2, "machine token"),
            queue_row(3, "mixed message"),
        ]
        self.queue.write_bytes(b"".join(canonical(row) for row in self.rows))
        self.first = self.root / "packet-one"
        self.second = self.root / "packet-two"

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def test_creates_two_isolated_blank_exact_queue_review_templates(self) -> None:
        report = packets.create_packets(self.queue, self.first, self.second)

        queue_sha256 = hashlib.sha256(self.queue.read_bytes()).hexdigest()
        self.assertEqual("blank-packets-created", report["status"])
        self.assertFalse(report["humanReviewCompleted"])
        self.assertFalse(report["labelsAssigned"])
        self.assertEqual(queue_sha256, report["queueSha256"])
        self.assertEqual(2, report["packetCount"])
        for destination, own_placeholder, other_placeholder in (
            (
                self.first,
                "reviewer-one-pseudonym-placeholder",
                "reviewer-two-pseudonym-placeholder",
            ),
            (
                self.second,
                "reviewer-two-pseudonym-placeholder",
                "reviewer-one-pseudonym-placeholder",
            ),
        ):
            self.assertEqual(
                set(packets.PACKET_FILES), {path.name for path in destination.iterdir()}
            )
            packet = json.loads((destination / "packet.json").read_bytes())
            template = json.loads((destination / "review-template.blank.json").read_bytes())
            self.assertEqual("blank-unassigned-template", packet["packetState"])
            self.assertFalse(packet["humanReviewCompleted"])
            self.assertFalse(packet["labelsAssigned"])
            self.assertEqual("forbidden", packet["networkAccess"])
            self.assertEqual(queue_sha256, packet["queueSha256"])
            self.assertEqual(own_placeholder, packet["reviewerIdPlaceholder"])
            self.assertEqual(set(adjudication.LABELS), set(packet["taxonomy"]))
            self.assertEqual(set(adjudication.RATIONALE_CODES), set(packet["rationaleCodes"]))
            self.assertEqual(
                [row["id"] for row in self.rows], [item["id"] for item in packet["items"]]
            )
            for source, item, review in zip(
                self.rows, packet["items"], template["reviews"], strict=True
            ):
                text_sha256 = hashlib.sha256(source["text"].encode("utf-8")).hexdigest()
                self.assertEqual(source["text"], item["text"])
                self.assertEqual(text_sha256, item["textSha256"])
                self.assertEqual(text_sha256, review["textSha256"])
                self.assertIsNone(review["label"])
                self.assertIsNone(review["confidence"])
                self.assertEqual([], review["rationaleCodes"])
            self.assertIsNone(template["reviewerType"])
            self.assertTrue(template["independent"])
            self.assertFalse(template["otherReviewsVisible"])
            serialized = b"".join(path.read_bytes() for path in destination.iterdir()).decode(
                "utf-8"
            )
            self.assertIn(own_placeholder, serialized)
            self.assertNotIn(other_placeholder, serialized)
            self.assertNotIn(str(self.first), serialized)
            self.assertNotIn(str(self.second), serialized)

            with self.assertRaisesRegex(adjudication.AdjudicationError, "review contract"):
                adjudication.load_review_document(
                    destination / "review-template.blank.json",
                    name="blank review template",
                    queue_rows=adjudication.load_queue(self.queue)[0],
                    queue_sha256=queue_sha256,
                    adjudication=False,
                    required_ids={row["id"] for row in self.rows},
                )

    def test_requires_distinct_absent_external_physical_destinations(self) -> None:
        self.assertEqual(
            2,
            packets.main(
                [
                    "--queue",
                    str(self.queue),
                    "--packet-one",
                    str(self.first),
                    "--packet-two",
                    str(self.first),
                ]
            ),
        )
        self.assertFalse(self.first.exists())

        packets.create_packets(self.queue, self.first, self.second)
        with self.assertRaisesRegex(packets.ReviewPacketError, "must be absent"):
            packets.create_packets(self.queue, self.first, self.root / "third")

        repository_destination = adjudication.REPOSITORY_ROOT / "packet-must-never-be-created"
        self.assertFalse(repository_destination.exists())
        with self.assertRaisesRegex(adjudication.AdjudicationError, "outside the repository"):
            packets.create_packets(self.queue, repository_destination, self.root / "fourth")
        self.assertFalse(repository_destination.exists())

    def test_rejects_reparse_parent_and_malformed_queue_through_canonical_loader(self) -> None:
        original_lstat = packets.os.lstat
        blocked = self.root / "blocked"

        def fake_lstat(path: os.PathLike[str] | str) -> os.stat_result | SimpleNamespace:
            result = original_lstat(path)
            if Path(path) == self.root:
                return SimpleNamespace(
                    st_mode=result.st_mode,
                    st_file_attributes=getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
                )
            return result

        with (
            mock.patch.object(packets.os, "lstat", side_effect=fake_lstat),
            self.assertRaisesRegex(packets.ReviewPacketError, "reparse component"),
        ):
            packets._prepare_destination(blocked, name="fixture packet")

        malformed = self.root / "malformed.jsonl"
        malformed.write_bytes(self.queue.read_bytes() + b'{"id":"extra"}\n')
        with self.assertRaisesRegex(adjudication.AdjudicationError, "allowlist"):
            packets.create_packets(malformed, self.first, self.second)
        self.assertFalse(self.first.exists())
        self.assertFalse(self.second.exists())

    def test_second_publish_failure_rolls_back_both_packets_without_partials(self) -> None:
        original_replace = packets.os.replace
        replacements = 0

        def fail_second(
            source: os.PathLike[str] | str, destination: os.PathLike[str] | str
        ) -> None:
            nonlocal replacements
            replacements += 1
            if replacements == 2:
                raise OSError("synthetic second publish failure")
            original_replace(source, destination)

        with (
            mock.patch.object(packets.os, "replace", side_effect=fail_second),
            self.assertRaises(OSError),
        ):
            packets.create_packets(self.queue, self.first, self.second)

        self.assertFalse(self.first.exists())
        self.assertFalse(self.second.exists())
        self.assertFalse(list(self.root.glob(".bstrings-review-packet-*")))


if __name__ == "__main__":
    unittest.main()
