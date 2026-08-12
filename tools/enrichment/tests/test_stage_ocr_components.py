from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[3]
STAGING_HELPER = REPO_ROOT / "tools" / "airgap" / "stage_ocr_components.py"
VISUAL_CPP_RUNTIME_FILES = (
    "vcruntime140.dll",
    "vcruntime140_1.dll",
    "msvcp140.dll",
    "msvcp140_1.dll",
)


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def file_rows(root: Path, *, notices_only: bool = False) -> list[dict[str, object]]:
    rows = []
    for path in sorted(root.rglob("*"), key=lambda item: item.as_posix().casefold()):
        if not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if notices_only and ".dist-info/licenses/" not in relative.casefold():
            continue
        value = path.read_bytes()
        rows.append({"path": relative, "bytes": len(value), "sha256": sha256_bytes(value)})
    return rows


class OcrRuntimeInventoryRefreshTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name) / "ocr-components"
        (self.root / "licenses").mkdir(parents=True)
        self.runtime_source = Path(self.temporary.name) / "visual-cpp-runtime"
        self.runtime_source.mkdir()
        self.runtime_payloads = {
            filename: f"licensed-{filename}".encode() for filename in VISUAL_CPP_RUNTIME_FILES
        }
        for filename, payload in self.runtime_payloads.items():
            (self.runtime_source / filename).write_bytes(payload)

        runtime_files = {}
        notice_files = {}
        for runtime_name in ("cpu", "directml"):
            runtime = self.root / "runtime" / f"ocr-{runtime_name}"
            notices = runtime / "example.dist-info" / "licenses"
            notices.mkdir(parents=True)
            (runtime / "python.exe").write_bytes(f"{runtime_name}-python".encode())
            (runtime / "empty-package-marker").write_bytes(b"")
            (runtime / "vcruntime140.dll").write_bytes(b"python-embedded-runtime")
            (runtime / "vcruntime140_1.dll").write_bytes(b"python-embedded-runtime-one")
            (notices / "LICENSE.txt").write_text("synthetic license\n", encoding="utf-8")
            runtime_files[runtime_name] = file_rows(runtime)
            notice_files[runtime_name] = file_rows(runtime, notices_only=True)
        inventory = {
            "schemaVersion": 1,
            "profile": "windows-x64-ocr-cpu-directml-v1",
            "componentLockSha256": "a" * 64,
            "runtimes": {"cpu": {"provider": "cpu"}, "directml": {"provider": "directml"}},
            "modelPack": {"sha256": "b" * 64},
            "runtimeFiles": runtime_files,
            "noticeFiles": notice_files,
        }
        self.inventory_path = self.root / "licenses" / "ocr-runtime-files.json"
        self.inventory_path.write_text(json.dumps(inventory), encoding="utf-8")
        self.original_inventory = inventory
        for runtime_name in ("cpu", "directml"):
            runtime = self.root / "runtime" / f"ocr-{runtime_name}"
            for filename, payload in self.runtime_payloads.items():
                (runtime / filename).write_bytes(payload)

    def tearDown(self) -> None:
        self.temporary.cleanup()

    def run_refresh(self) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [
                sys.executable,
                "-I",
                "-B",
                str(STAGING_HELPER),
                "refresh-inventory",
                "--output",
                str(self.root),
                "--visual-cpp-runtime",
                str(self.runtime_source),
            ],
            check=False,
            capture_output=True,
            text=True,
        )

    def test_refresh_records_the_verified_app_local_runtime_bytes_deterministically(self) -> None:
        first = self.run_refresh()
        self.assertEqual(0, first.returncode, first.stderr)
        first_bytes = self.inventory_path.read_bytes()
        inventory = json.loads(first_bytes)
        for runtime_name in ("cpu", "directml"):
            rows = {row["path"]: row for row in inventory["runtimeFiles"][runtime_name]}
            for filename, payload in self.runtime_payloads.items():
                self.assertEqual(len(payload), rows[filename]["bytes"])
                self.assertEqual(sha256_bytes(payload), rows[filename]["sha256"])
            original_rows = {
                row["path"]: row for row in self.original_inventory["runtimeFiles"][runtime_name]
            }
            self.assertEqual(original_rows["python.exe"], rows["python.exe"])
            self.assertEqual(original_rows["empty-package-marker"], rows["empty-package-marker"])
            self.assertEqual(
                ["example.dist-info/licenses/LICENSE.txt"],
                [row["path"] for row in inventory["noticeFiles"][runtime_name]],
            )

        second = self.run_refresh()
        self.assertEqual(0, second.returncode, second.stderr)
        self.assertEqual(first_bytes, self.inventory_path.read_bytes())
        self.assertEqual([], list(self.inventory_path.parent.glob(".ocr-runtime-files.json.*.tmp")))

    def assert_refresh_fails_without_rewriting(self, message: str) -> None:
        original = self.inventory_path.read_bytes()
        result = self.run_refresh()
        self.assertEqual(2, result.returncode)
        self.assertIn(message, result.stderr)
        self.assertEqual(original, self.inventory_path.read_bytes())

    def test_refresh_rejects_same_size_non_runtime_mutation(self) -> None:
        python = self.root / "runtime" / "ocr-cpu" / "python.exe"
        python.write_bytes(b"x" * python.stat().st_size)
        self.assert_refresh_fails_without_rewriting("file changed outside the approved overlay")

    def test_refresh_rejects_extra_non_runtime_file(self) -> None:
        (self.root / "runtime" / "ocr-cpu" / "unexpected.bin").write_bytes(b"unexpected")
        self.assert_refresh_fails_without_rewriting(
            "changed outside the approved Visual C++ overlay"
        )

    def test_refresh_rejects_missing_non_runtime_file(self) -> None:
        (self.root / "runtime" / "ocr-directml" / "python.exe").unlink()
        self.assert_refresh_fails_without_rewriting(
            "changed outside the approved Visual C++ overlay"
        )

    def test_refresh_rejects_notice_mutation(self) -> None:
        notice = (
            self.root / "runtime" / "ocr-cpu" / "example.dist-info" / "licenses" / "LICENSE.txt"
        )
        notice_bytes = bytearray(notice.read_bytes())
        notice_bytes[0] ^= 1
        notice.write_bytes(notice_bytes)
        self.assert_refresh_fails_without_rewriting("file changed outside the approved overlay")

    def test_refresh_rejects_visual_cpp_runtime_divergence(self) -> None:
        runtime_dll = self.root / "runtime" / "ocr-directml" / "msvcp140_1.dll"
        runtime_dll.write_bytes(b"unverified-runtime")
        self.assert_refresh_fails_without_rewriting("differs from the verified source")

    def test_refresh_rejects_missing_visual_cpp_runtime_file(self) -> None:
        (self.root / "runtime" / "ocr-cpu" / "msvcp140.dll").unlink()
        self.assert_refresh_fails_without_rewriting(
            "changed outside the approved Visual C++ overlay"
        )


if __name__ == "__main__":
    unittest.main()
