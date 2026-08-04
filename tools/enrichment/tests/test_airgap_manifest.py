from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "airgap"))

from airgap_manifest import ManifestError, create_manifest, verify_manifest  # noqa: E402


class AirgapManifestTests(unittest.TestCase):
    def test_manifest_round_trip_covers_every_bundle_file(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "app").mkdir()
            (root / "app" / "bstrings.exe").write_bytes(b"portable-core")
            (root / "model.gguf").write_bytes(b"local-model")
            manifest_path = root / "airgap-manifest.json"

            created = create_manifest(root, manifest_path)
            verified = verify_manifest(root, manifest_path)

            self.assertEqual(2, len(created["files"]))
            self.assertEqual(2, verified["files"])
            self.assertEqual(len(b"portable-core") + len(b"local-model"), verified["bytes"])

    def test_modified_file_fails_verification(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            payload = root / "payload.bin"
            payload.write_bytes(b"before")
            manifest_path = root / "airgap-manifest.json"
            create_manifest(root, manifest_path)
            payload.write_bytes(b"after")

            with self.assertRaisesRegex(ManifestError, "mismatch"):
                verify_manifest(root, manifest_path)

    def test_unexpected_file_fails_verification(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "payload.bin").write_bytes(b"expected")
            manifest_path = root / "airgap-manifest.json"
            create_manifest(root, manifest_path)
            (root / "extra.bin").write_bytes(b"unexpected")

            with self.assertRaisesRegex(ManifestError, "unexpected"):
                verify_manifest(root, manifest_path)

    def test_manifest_rejects_parent_path_escape(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path = root / "airgap-manifest.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "files": [{"path": "../escape", "bytes": 0, "sha256": "0" * 64}],
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ManifestError, "unsafe relative path"):
                verify_manifest(root, manifest_path)

    def test_manifest_alias_link_is_rejected_before_self_exclusion(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path = root / "airgap-manifest.json"
            manifest_path.write_text("{}", encoding="utf-8")
            alias = root / "manifest-alias.json"
            try:
                alias.symlink_to(manifest_path)
            except OSError as exc:
                self.skipTest(f"symbolic links are unavailable: {exc}")

            with self.assertRaisesRegex(ManifestError, "link or reparse point"):
                create_manifest(root, manifest_path)


if __name__ == "__main__":
    unittest.main()
