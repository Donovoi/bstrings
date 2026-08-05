from __future__ import annotations

import argparse
import hashlib
import json
import sys
import tempfile
import unittest
from collections import defaultdict
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import generate_cord_selection as selection  # noqa: E402


class CordSelectionTests(unittest.TestCase):
    def test_duplicate_images_are_assigned_to_one_role(self) -> None:
        entries = [
            {"globalIndex": 0, "imageSha256": "a"},
            {"globalIndex": 1, "imageSha256": "a"},
            {"globalIndex": 2, "imageSha256": "b"},
            {"globalIndex": 3, "imageSha256": "c"},
        ]
        assigned, calibration, confirmatory = selection.assign_roles(
            entries,
            expected_rows=4,
            expected_unique_hashes=3,
            confirmatory_rows=2,
        )
        self.assertEqual(2, len(calibration))
        self.assertEqual(2, len(confirmatory))
        roles = defaultdict(set)
        for entry in assigned:
            roles[entry["imageSha256"]].add(entry["role"])
        self.assertTrue(all(len(values) == 1 for values in roles.values()))
        self.assertEqual({"confirmatory"}, roles["a"])

        with self.assertRaises(selection.SelectionError):
            selection.assign_roles(
                entries,
                expected_rows=4,
                expected_unique_hashes=3,
                confirmatory_rows=1,
            )

    def test_shard_validation_rejects_order_and_same_length_mutation(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            paths = tuple(root / f"shard-{index}.parquet" for index in range(4))
            for index, path in enumerate(paths):
                path.write_bytes(bytes([index + 1]) * 8)
            shards = tuple(
                (path.name, path.stat().st_size, selection.sha256_file(path)) for path in paths
            )
            with (
                patch.object(selection, "SHARDS", shards),
                patch.object(selection.importlib.metadata, "version", return_value="25.0.0"),
            ):
                self.assertEqual(paths, selection._validate_shards(paths))
                with self.assertRaises(selection.SelectionError):
                    selection._validate_shards(tuple(reversed(paths)))
                paths[2].write_bytes(b"changed!")
                self.assertEqual(8, paths[2].stat().st_size)
                with self.assertRaises(selection.SelectionError):
                    selection._validate_shards(paths)

    def test_output_creation_is_exclusive_and_verification_is_byte_exact(self) -> None:
        manifest = {"entries": [], "entriesSha256": {}, "schemaVersion": 1}
        encoded = (selection.canonical_json(manifest) + "\n").encode("utf-8")
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "selection.json"
            args = argparse.Namespace(parquet=[], output=output, verify=False)
            with patch.object(selection, "build_manifest", return_value=manifest):
                result = selection.run(args)
                self.assertEqual("created", result["status"])
                self.assertEqual(encoded, output.read_bytes())
                self.assertFalse(output.with_name(output.name + ".incomplete").exists())
                with self.assertRaises(selection.SelectionError):
                    selection.run(args)

                args.verify = True
                self.assertEqual("verified", selection.run(args)["status"])
                output.write_bytes(encoded + b" ")
                with self.assertRaises(selection.SelectionError):
                    selection.run(args)

                blocked = Path(directory) / "blocked.json"
                blocked.with_name(blocked.name + ".incomplete").write_bytes(b"incomplete")
                args.output = blocked
                args.verify = False
                with self.assertRaises(selection.SelectionError):
                    selection.run(args)

    def test_checked_in_manifest_matches_preregistration(self) -> None:
        path = Path(__file__).resolve().parents[1] / "cord-v2-train-selection-v1.json"
        raw = path.read_bytes()
        self.assertEqual(124_181, len(raw))
        self.assertEqual(
            "4deb7deec2a5ee69e182c9030ef0e1dee5bdf5960a2f9bec9ba5f404293fd6e1",
            hashlib.sha256(raw).hexdigest(),
        )
        manifest = json.loads(raw)
        entries = manifest["entries"]
        calibration = [entry for entry in entries if entry["role"] == "calibration"]
        confirmatory = [entry for entry in entries if entry["role"] == "confirmatory"]
        self.assertEqual(800, len(entries))
        self.assertEqual(600, len(calibration))
        self.assertEqual(200, len(confirmatory))
        self.assertEqual(
            selection.EXPECTED_SELECTION_HASHES["complete"], selection.selection_hash(entries)
        )
        self.assertEqual(
            selection.EXPECTED_SELECTION_HASHES["calibration"],
            selection.selection_hash(calibration),
        )
        self.assertEqual(
            selection.EXPECTED_SELECTION_HASHES["confirmatory"],
            selection.selection_hash(confirmatory),
        )
        roles = defaultdict(set)
        for entry in entries:
            roles[entry["imageSha256"]].add(entry["role"])
        self.assertEqual(798, len(roles))
        self.assertTrue(all(len(values) == 1 for values in roles.values()))


if __name__ == "__main__":
    unittest.main()
