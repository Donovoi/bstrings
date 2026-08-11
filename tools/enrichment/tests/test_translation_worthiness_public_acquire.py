from __future__ import annotations

import hashlib
import io
import json
import os
import socket
import stat
import sys
import tempfile
import unittest
from contextlib import redirect_stderr
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import translation_worthiness_adjudication as adjudication  # noqa: E402
import translation_worthiness_public_acquire as public  # noqa: E402


def canonical_json(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"), sort_keys=True)


def file_pin(raw: bytes, source_id: str, path: str, project: str, revision: str) -> public.FilePin:
    return public.FilePin(
        source_path=path,
        source_url=f"https://raw.githubusercontent.com/{project}/{revision}/{path}",
        cache_relative_path=f"{source_id}/content.txt",
        byte_count=len(raw),
        sha256=hashlib.sha256(raw).hexdigest(),
    )


def source_pin(
    source_id: str,
    project: str,
    source_raw: bytes,
    license_raw: bytes,
    *,
    kind: str,
) -> public.SourcePin:
    revision = hashlib.sha1(project.encode("ascii"), usedforsecurity=False).hexdigest()
    path = {"code": "src/example.rs", "config": "config/example.json", "log": "logs/app.log"}[kind]
    content = file_pin(source_raw, source_id, path, project, revision)
    license_path = "LICENSE"
    license_file = public.LicensePin(
        source_path=license_path,
        source_url=(f"https://raw.githubusercontent.com/{project}/{revision}/{license_path}"),
        cache_relative_path=f"{source_id}/LICENSE",
        byte_count=len(license_raw),
        sha256=hashlib.sha256(license_raw).hexdigest(),
        identifier="MIT",
    )
    return public.SourcePin(
        **content.__dict__,
        source_id=source_id,
        source_project=project,
        source_revision=revision,
        source_kind=kind,
        origin_family="native-static",
        asset_provenance="upstream-repository-first-party-source-path",
        licensing_limit=(
            "Synthetic fixture only; no production licensing conclusion is represented."
        ),
        license=license_file,
    )


class Fixture:
    def __init__(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.cache = self.root / "cache"
        self.cache.mkdir()
        self.license_raw = b"Synthetic permissive test license.\n"
        source_values = (
            (
                "synthetic-alpha",
                "example/alpha-project",
                b"shared exact line\nalpha-only-token\n",
                "code",
            ),
            (
                "synthetic-beta",
                "example/beta-project",
                b"shared exact line\nbeta-only-token\n",
                "config",
            ),
            (
                "synthetic-delta",
                "example/delta-project",
                b"delta-only-token\n",
                "code",
            ),
            (
                "synthetic-epsilon",
                "example/epsilon-project",
                b"epsilon-only-token\n",
                "config",
            ),
            (
                "synthetic-gamma",
                "example/gamma-project",
                b"gamma-only-token\n",
                "log",
            ),
        )
        self.source_raw = {source_id: raw for source_id, _project, raw, _kind in source_values}
        self.sources = tuple(
            source_pin(
                source_id,
                project,
                raw,
                self.license_raw,
                kind=kind,
            )
            for source_id, project, raw, kind in source_values
        )
        self.manifest = public.SourceManifest(sources=self.sources)
        for source in self.sources:
            directory = self.cache / source.source_id
            directory.mkdir()
            (directory / "content.txt").write_bytes(self.source_raw[source.source_id])
            (directory / "LICENSE").write_bytes(self.license_raw)

    def close(self) -> None:
        self.temporary.cleanup()


def manifest_value(source: public.SourcePin) -> dict[str, object]:
    return {
        "schemaVersion": public.SCHEMA_VERSION,
        "manifestId": public.MANIFEST_ID,
        "labelPolicy": "UNLABELED-dual-independent-review-only",
        "networkAccess": "forbidden",
        "researchOnly": True,
        "promotionEligible": False,
        "sources": [
            {
                "sourceId": source.source_id,
                "sourceProject": source.source_project,
                "sourceRevision": source.source_revision,
                "sourcePath": source.source_path,
                "sourceUrl": source.source_url,
                "cacheRelativePath": source.cache_relative_path,
                "bytes": source.byte_count,
                "sha256": source.sha256,
                "sourceKind": source.source_kind,
                "originFamily": source.origin_family,
                "assetProvenance": source.asset_provenance,
                "licensingLimit": source.licensing_limit,
                "license": {
                    "id": source.license.identifier,
                    "sourcePath": source.license.source_path,
                    "sourceUrl": source.license.source_url,
                    "cacheRelativePath": source.license.cache_relative_path,
                    "bytes": source.license.byte_count,
                    "sha256": source.license.sha256,
                },
            }
        ],
    }


class PublicWorthinessAcquisitionTests(unittest.TestCase):
    def test_committed_source_manifest_is_pinned_permissive_and_independent(self) -> None:
        manifest = public.load_source_manifest()

        self.assertEqual(5, len(manifest.sources))
        self.assertEqual(5, len({source.source_project for source in manifest.sources}))
        self.assertEqual(
            [
                "bat-assets-create",
                "fluent-bit-application-log",
                "prometheus-example-config",
                "ripgrep-logger",
                "windows-terminal-user-defaults",
            ],
            [source.source_id for source in manifest.sources],
        )
        for source in manifest.sources:
            self.assertRegex(source.source_revision, r"^[0-9a-f]{40}$")
            self.assertRegex(source.sha256, r"^[0-9a-f]{64}$")
            self.assertIn(source.license.identifier, {"Apache-2.0", "MIT"})
            self.assertIn(source.source_revision, source.source_url)
            self.assertGreaterEqual(len(source.licensing_limit), 20)

    def test_materializes_only_unlabeled_dual_review_rows_grouped_by_file(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        output = fixture.root / "queue"

        report = public.materialize_public_queue(fixture.cache, output, manifest=fixture.manifest)

        self.assertEqual("complete", report["status"])
        self.assertEqual("UNLABELED", report["label"])
        self.assertEqual(2, report["requiredIndependentReviews"])
        self.assertTrue(report["researchOnly"])
        self.assertFalse(report["promotionEligible"])
        rows = [
            json.loads(line)
            for line in (output / "annotation-queue.jsonl").read_text().splitlines()
        ]
        self.assertTrue(rows)
        self.assertEqual({"UNLABELED"}, {row["label"] for row in rows})
        self.assertEqual({"pending"}, {row["reviewState"] for row in rows})
        self.assertEqual({2}, {row["requiredIndependentReviews"] for row in rows})
        self.assertEqual({True}, {row["researchOnly"] for row in rows})
        self.assertEqual({False}, {row["promotionEligible"] for row in rows})
        self.assertEqual(
            {f"public-{source.source_id}-file" for source in fixture.sources},
            {row["groupId"] for row in rows},
        )
        for row in rows:
            self.assertIn(row["scriptFamily"], public.SCRIPT_FAMILIES)
            self.assertIn(row["lengthBand"], {name for name, _, _ in public.LENGTH_BANDS})
            self.assertIsInstance(row["sourceOrdinal"], int)
            self.assertIn(row["sourceKind"], public.SOURCE_KINDS)

        group_manifest = json.loads((output / "groups.json").read_text(encoding="utf-8"))
        groups = group_manifest["groups"]
        self.assertEqual(5, len(groups))
        self.assertEqual(len(rows), sum(group["records"] for group in groups))
        self.assertEqual(1, group_manifest["crossProjectDuplicateTexts"])
        self.assertEqual(4, len(group_manifest["duplicateLinkedProjectComponents"]))
        shared = [row for row in rows if row["text"] == "shared exact line"]
        self.assertEqual(2, len(shared))
        self.assertEqual(
            {"example/alpha-project", "example/beta-project"},
            {row["sourceProject"] for row in shared},
        )
        sources = json.loads((output / "sources.json").read_text(encoding="utf-8"))
        self.assertEqual("forbidden", sources["networkAccess"])
        self.assertEqual(5, len(sources["sources"]))
        self.assertEqual(
            fixture.license_raw,
            (output / "licenses" / "synthetic-alpha-LICENSE").read_bytes(),
        )
        serialized_report = canonical_json(report)
        self.assertNotIn("shared exact line", serialized_report)
        self.assertNotIn("alpha-only-token", serialized_report)

    def test_materialized_queue_is_accepted_by_adjudication_loader(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        output = fixture.root / "queue"

        public.materialize_public_queue(fixture.cache, output, manifest=fixture.manifest)
        rows, resolved, digest = adjudication.load_queue(output / "annotation-queue.jsonl")

        self.assertEqual(7, len(rows))
        self.assertEqual((output / "annotation-queue.jsonl").resolve(), resolved)
        self.assertEqual(
            hashlib.sha256((output / "annotation-queue.jsonl").read_bytes()).hexdigest(),
            digest,
        )

    def test_repeated_materialization_is_byte_deterministic(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        first = fixture.root / "first"
        second = fixture.root / "second"

        public.materialize_public_queue(fixture.cache, first, manifest=fixture.manifest)
        public.materialize_public_queue(fixture.cache, second, manifest=fixture.manifest)

        def files(root: Path) -> dict[str, bytes]:
            return {
                path.relative_to(root).as_posix(): path.read_bytes()
                for path in root.rglob("*")
                if path.is_file()
            }

        self.assertEqual(files(first), files(second))

    def test_manifest_rejects_traversal_mutable_urls_and_duplicate_projects(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        manifest_path = fixture.root / "sources.json"
        base = manifest_value(fixture.sources[0])
        cases = []
        traversal = json.loads(canonical_json(base))
        traversal["sources"][0]["sourcePath"] = "../escape.txt"
        cases.append(traversal)
        mutable = json.loads(canonical_json(base))
        mutable["sources"][0]["sourceUrl"] = "https://raw.githubusercontent.com/x/y/main/a"
        cases.append(mutable)
        duplicate = json.loads(canonical_json(base))
        duplicate["sources"].append(json.loads(canonical_json(duplicate["sources"][0])))
        duplicate["sources"][1]["sourceId"] = "another-id"
        duplicate["sources"][1]["cacheRelativePath"] = "another-id/content.txt"
        duplicate["sources"][1]["license"]["cacheRelativePath"] = "another-id/LICENSE"
        cases.append(duplicate)
        for value in cases:
            with self.subTest(value=value["sources"][0]["sourcePath"]):
                manifest_path.write_text(canonical_json(value) + "\n", encoding="utf-8")
                with self.assertRaises(public.PublicAcquisitionError):
                    public.load_source_manifest(manifest_path)

    def test_hash_length_and_control_text_fail_before_atomic_publish(self) -> None:
        cases = ("length", "hash", "control")
        for case in cases:
            with self.subTest(case=case):
                fixture = Fixture()
                self.addCleanup(fixture.close)
                source = fixture.sources[0]
                output = fixture.root / "queue"
                if case == "length":
                    bad = public.SourcePin(
                        **{**source.__dict__, "byte_count": source.byte_count + 1}
                    )
                elif case == "hash":
                    bad = public.SourcePin(**{**source.__dict__, "sha256": "0" * 64})
                else:
                    raw = b"safe\x07unsafe\n"
                    (fixture.cache / source.source_id / "content.txt").write_bytes(raw)
                    bad = public.SourcePin(
                        **{
                            **source.__dict__,
                            "byte_count": len(raw),
                            "sha256": hashlib.sha256(raw).hexdigest(),
                        }
                    )
                manifest = public.SourceManifest(sources=(bad,))

                with self.assertRaises(public.PublicAcquisitionError):
                    public.materialize_public_queue(fixture.cache, output, manifest=manifest)

                self.assertFalse(output.exists())
                self.assertFalse(list(fixture.root.glob("*.partial")))

    def test_reparse_cache_root_is_rejected(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        fake = SimpleNamespace(
            st_mode=stat.S_IFDIR | 0o700,
            st_file_attributes=getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
        )
        with (
            patch.object(public.os, "lstat", return_value=fake),
            self.assertRaisesRegex(public.PublicAcquisitionError, "link or reparse component"),
        ):
            public.materialize_public_queue(
                fixture.cache, fixture.root / "queue", manifest=fixture.manifest
            )

    def test_reparse_ancestor_of_cache_or_output_parent_is_rejected(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        real_lstat = os.lstat
        reparse_ancestor = fixture.root.parent.absolute()
        fake = SimpleNamespace(
            st_mode=stat.S_IFDIR | 0o700,
            st_file_attributes=getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400),
        )

        def ancestor_lstat(path: os.PathLike[str] | str) -> os.stat_result | SimpleNamespace:
            if Path(path).absolute() == reparse_ancestor:
                return fake
            return real_lstat(path)

        with (
            patch.object(public.os, "lstat", side_effect=ancestor_lstat),
            self.assertRaisesRegex(public.PublicAcquisitionError, "link or reparse component"),
        ):
            public.materialize_public_queue(
                fixture.cache, fixture.root / "queue", manifest=fixture.manifest
            )

    def test_no_network_is_attempted_during_materialization(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        with patch.object(
            socket,
            "create_connection",
            side_effect=AssertionError("network must remain unused"),
        ) as network:
            public.materialize_public_queue(
                fixture.cache, fixture.root / "queue", manifest=fixture.manifest
            )
        network.assert_not_called()

    def test_output_cannot_exist_or_enter_repository_or_cache(self) -> None:
        fixture = Fixture()
        self.addCleanup(fixture.close)
        existing = fixture.root / "existing"
        existing.mkdir()
        marker = existing / "keep.txt"
        marker.write_text("keep", encoding="utf-8")
        with self.assertRaisesRegex(public.PublicAcquisitionError, "must not already exist"):
            public.materialize_public_queue(fixture.cache, existing, manifest=fixture.manifest)
        self.assertEqual("keep", marker.read_text(encoding="utf-8"))

        with (
            patch.object(public, "REPOSITORY_ROOT", fixture.root),
            self.assertRaisesRegex(public.PublicAcquisitionError, "public-source cache"),
        ):
            public.materialize_public_queue(
                fixture.cache, fixture.root.parent / "outside", manifest=fixture.manifest
            )

        with self.assertRaisesRegex(public.PublicAcquisitionError, "inside its source cache"):
            public.materialize_public_queue(
                fixture.cache, fixture.cache / "queue", manifest=fixture.manifest
            )

    def test_cli_exposes_no_network_or_url_option(self) -> None:
        options = public.parse_arguments(
            ["--cache-directory", "cache", "--output-directory", "output"]
        )
        self.assertEqual(Path("cache"), options.cache_directory)
        with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            public.parse_arguments(
                [
                    "--cache-directory",
                    "cache",
                    "--output-directory",
                    "output",
                    "--url",
                    "https://example.invalid",
                ]
            )


if __name__ == "__main__":
    unittest.main()
