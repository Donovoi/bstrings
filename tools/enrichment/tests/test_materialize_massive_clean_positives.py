from __future__ import annotations

import hashlib
import io
import json
import os
import stat
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import materialize_massive_clean_positives as massive  # noqa: E402


def canonical_json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def source_row(
    seed_id: str,
    locale: str,
    partition: str,
    *,
    utterance: str | None = None,
    annotated: str | None = None,
) -> dict[str, object]:
    text = utterance or f"message {seed_id} in {locale}"
    return {
        "id": seed_id,
        "locale": locale,
        "partition": partition,
        "scenario": "alarm",
        "intent": "alarm_set",
        "utt": text,
        "annot_utt": annotated or text,
        "worker_id": "1",
    }


def tar_bytes(
    locale_rows: dict[str, list[dict[str, object]]],
    *,
    extra_members: list[tuple[str, bytes, str]] | None = None,
    reverse_members: bool = False,
) -> bytes:
    files: list[tuple[str, bytes, str]] = [
        ("1.1/CITATION.md", b"citation\n", "file"),
        ("1.1/NOTICE.md", b"official notice\n", "file"),
        ("1.1/CHANGELOG.md", b"changes\n", "file"),
        ("1.1/LICENSE", b"CC-BY-4.0 license\n", "file"),
    ]
    for locale, rows in locale_rows.items():
        raw = "".join(canonical_json(row) + "\n" for row in rows).encode("utf-8")
        files.append((f"1.1/data/{locale}.jsonl", raw, "file"))
    files.extend(extra_members or [])
    if reverse_members:
        files.reverse()
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w:gz", format=tarfile.PAX_FORMAT) as archive:
        for directory in ("1.1", "1.1/data"):
            info = tarfile.TarInfo(directory)
            info.type = tarfile.DIRTYPE
            info.mode = 0o755
            info.mtime = 0
            archive.addfile(info)
        for name, raw, kind in files:
            info = tarfile.TarInfo(name)
            info.mode = 0o644
            info.mtime = 0
            if kind == "hardlink":
                info.type = tarfile.LNKTYPE
                info.linkname = "1.1/LICENSE"
                info.size = 0
                archive.addfile(info)
            else:
                info.size = len(raw)
                archive.addfile(info, io.BytesIO(raw))
    return output.getvalue()


def fixture_rows(*, reverse: bool = False) -> dict[str, list[dict[str, object]]]:
    result: dict[str, list[dict[str, object]]] = {}
    for locale in ("en-US", "es-ES"):
        rows = [
            source_row(
                "1",
                locale,
                "train",
                utterance=f"alarm five {locale}",
                annotated=f"alarm [time : five] {locale}",
            ),
            source_row("2", locale, "dev"),
            source_row("3", locale, "test"),
            source_row("4", locale, "train"),
            source_row("5", locale, "dev"),
            source_row("6", locale, "test"),
        ]
        result[locale] = list(reversed(rows)) if reverse else rows
    return result


def test_pin(raw: bytes) -> massive.SourcePin:
    return massive.SourcePin(
        archive_url="https://example.invalid/massive.tar.gz",
        archive_bytes=len(raw),
        archive_sha256=hashlib.sha256(raw).hexdigest(),
        hugging_face_repository=massive.PINNED_HF_REPOSITORY,
        hugging_face_revision=massive.PINNED_HF_REVISION,
        expected_locales=("en-US", "es-ES"),
        notice_member="1.1/NOTICE.md",
        notice_repository_url=(
            "https://github.com/alexa/massive/blob/"
            "f966f21846043aabef9b0f974fa7970027f43738/NOTICE.md"
        ),
        notice_repository_revision="f966f21846043aabef9b0f974fa7970027f43738",
        license_member="1.1/LICENSE",
        license_spdx=massive.PINNED_LICENSE,
    )


class Fixture:
    def __init__(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.archive = self.root / "massive.tar.gz"

    def close(self) -> None:
        self.temporary.cleanup()

    def write_archive(self, raw: bytes) -> massive.SourcePin:
        self.archive.write_bytes(raw)
        return test_pin(raw)


class MassiveCleanPositiveMaterializationTests(unittest.TestCase):
    def test_committed_source_manifest_has_the_required_immutable_pins(self) -> None:
        pin = massive.load_source_pin()

        self.assertEqual(massive.PINNED_ARCHIVE_URL, pin.archive_url)
        self.assertEqual(40_251_390, pin.archive_bytes)
        self.assertEqual(massive.PINNED_ARCHIVE_SHA256, pin.archive_sha256)
        self.assertEqual(massive.PINNED_HF_REVISION, pin.hugging_face_revision)
        self.assertEqual("CC-BY-4.0", pin.license_spdx)
        self.assertEqual(52, len(pin.expected_locales))
        self.assertIn(pin.notice_repository_revision, pin.notice_repository_url)

    def test_materialization_groups_locales_and_fragments_and_preserves_splits(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        pin = fixture.write_archive(tar_bytes(fixture_rows()))
        output = fixture.root / "materialized"

        report = massive.materialize(fixture.archive, output, count_per_source_split=1, pin=pin)

        self.assertEqual("complete", report["status"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        rows = [json.loads(line) for line in (output / "corpus.jsonl").read_text().splitlines()]
        self.assertTrue(rows)
        self.assertEqual({"human-worthy"}, {row["label"] for row in rows})
        self.assertEqual({True}, {row["containsHumanMaterial"] for row in rows})
        self.assertEqual({True}, {row["researchOnly"] for row in rows})
        self.assertEqual({False}, {row["promotionEligible"] for row in rows})
        self.assertIn("annotated-slot-fragment", {row["derivation"] for row in rows})

        groups: dict[str, list[dict[str, object]]] = {}
        for row in rows:
            groups.setdefault(row["groupId"], []).append(row)
        self.assertEqual(3, len(groups))
        for group_rows in groups.values():
            self.assertEqual({"en-US", "es-ES"}, {row["sourceLocale"] for row in group_rows})
            self.assertEqual(1, len({row["split"] for row in group_rows}))
            self.assertEqual(1, len({row["sourcePartition"] for row in group_rows}))

        splits = json.loads((output / "splits.json").read_text(encoding="utf-8"))
        self.assertEqual({"train", "calibration", "test"}, set(splits["splits"]))
        self.assertEqual(
            {"train": "train", "dev": "calibration", "test": "test"},
            splits["sourceSplitMapping"],
        )
        self.assertEqual(b"official notice\n", (output / "NOTICE.md").read_bytes())
        self.assertEqual(b"CC-BY-4.0 license\n", (output / "LICENSE").read_bytes())
        source = json.loads((output / "source.json").read_text(encoding="utf-8"))
        self.assertTrue(source["researchOnly"])
        self.assertFalse(source["promotionEligible"])
        self.assertEqual(massive.SAMPLING_RULE_VERSION, source["sampling"]["ruleVersion"])
        self.assertNotIn("text", canonical_json(report))
        self.assertNotIn("alarm five", canonical_json(report))

    def test_repeated_materialization_of_one_archive_is_byte_deterministic(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        pin = fixture.write_archive(tar_bytes(fixture_rows(reverse=True), reverse_members=True))
        first = fixture.root / "first"
        second = fixture.root / "second"

        massive.materialize(fixture.archive, first, count_per_source_split=1, pin=pin)
        massive.materialize(fixture.archive, second, count_per_source_split=1, pin=pin)

        self.assertEqual(
            {path.name: path.read_bytes() for path in first.iterdir()},
            {path.name: path.read_bytes() for path in second.iterdir()},
        )

    def test_seed_selection_is_independent_of_input_mapping_order(self) -> None:
        forward = {
            str(index): SOURCE_SPLIT
            for index, SOURCE_SPLIT in enumerate(
                ["train", "dev", "test", "train", "dev", "test"], start=1
            )
        }
        reverse = dict(reversed(tuple(forward.items())))

        self.assertEqual(
            massive.select_seed_ids(forward, 1),
            massive.select_seed_ids(reverse, 1),
        )

    def test_source_reader_accepts_the_official_no_final_newline_layout(self) -> None:
        raw = canonical_json(source_row("1", "en-US", "train")).encode("utf-8")

        records = massive._source_rows(io.BytesIO(raw), locale="en-US")

        self.assertEqual(1, len(records))
        self.assertEqual("1", records[0].seed_id)

    def test_archive_verification_rejects_length_and_hash_mismatch(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        raw = tar_bytes(fixture_rows())
        pin = fixture.write_archive(raw)

        with self.assertRaisesRegex(massive.MassiveAcquisitionError, "byte length"):
            massive.verify_archive(
                fixture.archive,
                massive.SourcePin(**{**pin.__dict__, "archive_bytes": len(raw) + 1}),
            )
        with self.assertRaisesRegex(massive.MassiveAcquisitionError, "SHA-256"):
            massive.verify_archive(
                fixture.archive,
                massive.SourcePin(**{**pin.__dict__, "archive_sha256": "0" * 64}),
            )

    def test_archive_path_rejects_reparse_points(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        fixture.archive.write_bytes(b"archive")
        real = os.lstat(fixture.archive)
        fake = SimpleNamespace(
            st_mode=stat.S_IFREG | 0o600,
            st_file_attributes=getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
        )
        with (
            patch.object(massive.os, "lstat", side_effect=[fake, real]),
            self.assertRaisesRegex(massive.MassiveAcquisitionError, "physical file"),
        ):
            massive.physical_archive(fixture.archive)

    def test_archive_rejects_traversal_and_non_regular_members_without_output(self) -> None:
        for name, kind in (("../escape", "file"), ("1.1/extra-link", "hardlink")):
            with self.subTest(name=name):
                fixture = Fixture()
                self.addCleanup(fixture.close)
                raw = tar_bytes(fixture_rows(), extra_members=[(name, b"bad", kind)])
                pin = fixture.write_archive(raw)
                output = fixture.root / "materialized"

                with self.assertRaises(massive.MassiveAcquisitionError):
                    massive.materialize(fixture.archive, output, count_per_source_split=1, pin=pin)

                self.assertFalse(output.exists())
                self.assertFalse(list(fixture.root.glob("*.partial")))

    def test_missing_locale_or_cross_partition_seed_family_is_rejected_atomically(self) -> None:
        cases = []
        missing = fixture_rows()
        missing["es-ES"] = [row for row in missing["es-ES"] if row["id"] != "2"]
        cases.append(missing)
        crossed = fixture_rows()
        next(row for row in crossed["es-ES"] if row["id"] == "1")["partition"] = "test"
        cases.append(crossed)
        for locale_rows in cases:
            with self.subTest(case=len(locale_rows["es-ES"])):
                fixture = Fixture()
                self.addCleanup(fixture.close)
                raw = tar_bytes(locale_rows)
                pin = fixture.write_archive(raw)
                output = fixture.root / "materialized"

                with self.assertRaises(massive.MassiveAcquisitionError):
                    massive.materialize(fixture.archive, output, count_per_source_split=1, pin=pin)

                self.assertFalse(output.exists())

    def test_existing_output_is_never_overwritten(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        pin = fixture.write_archive(tar_bytes(fixture_rows()))
        output = fixture.root / "materialized"
        output.mkdir()
        marker = output / "keep.txt"
        marker.write_text("keep", encoding="utf-8")

        with self.assertRaisesRegex(massive.MassiveAcquisitionError, "must not already exist"):
            massive.materialize(fixture.archive, output, count_per_source_split=1, pin=pin)

        self.assertEqual("keep", marker.read_text(encoding="utf-8"))

    def test_archive_and_materialized_text_must_remain_outside_repository(self) -> None:
        fixture = Fixture()
        source = Fixture()
        self.addCleanup(fixture.close)
        self.addCleanup(source.close)
        raw = tar_bytes(fixture_rows())
        inside_pin = fixture.write_archive(raw)
        outside_pin = source.write_archive(raw)

        with (
            patch.object(massive, "REPOSITORY_ROOT", fixture.root),
            self.assertRaisesRegex(massive.MassiveAcquisitionError, "cached archive"),
        ):
            massive.materialize(
                fixture.archive,
                source.root / "outside-output",
                count_per_source_split=1,
                pin=inside_pin,
            )

        with (
            patch.object(massive, "REPOSITORY_ROOT", fixture.root),
            self.assertRaisesRegex(massive.MassiveAcquisitionError, "materialized output"),
        ):
            massive.materialize(
                source.archive,
                fixture.root / "inside-output",
                count_per_source_split=1,
                pin=outside_pin,
            )


if __name__ == "__main__":
    unittest.main()
