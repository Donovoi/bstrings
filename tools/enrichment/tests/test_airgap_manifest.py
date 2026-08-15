from __future__ import annotations

import json
import re
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

    def test_builder_marker_lifecycle_requires_explicit_preliminary_verification(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "payload.bin").write_bytes(b"expected")
            marker = root / ".incomplete"
            marker.write_text("building\n", encoding="utf-8")
            manifest_path = root / "airgap-manifest.json"

            created = create_manifest(root, manifest_path)

            self.assertNotIn(".incomplete", {entry["path"] for entry in created["files"]})
            with self.assertRaisesRegex(ManifestError, "lingering root \\.incomplete"):
                verify_manifest(root, manifest_path)
            preliminary = verify_manifest(
                root,
                manifest_path,
                allow_incomplete_marker=True,
            )
            self.assertEqual(1, preliminary["files"])

            marker.unlink()
            strict = verify_manifest(root, manifest_path)
            self.assertEqual(preliminary, strict)

    def test_builder_marker_override_requires_the_marker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "payload.bin").write_bytes(b"expected")
            manifest_path = root / "airgap-manifest.json"
            create_manifest(root, manifest_path)

            with self.assertRaisesRegex(ManifestError, "requires a root \\.incomplete"):
                verify_manifest(
                    root,
                    manifest_path,
                    allow_incomplete_marker=True,
                )

    def test_incomplete_marker_is_a_reserved_manifest_path(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            manifest_path = root / "airgap-manifest.json"
            manifest_path.write_text(
                json.dumps(
                    {
                        "schemaVersion": 1,
                        "files": [{"path": ".incomplete", "bytes": 0, "sha256": "0" * 64}],
                    }
                ),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ManifestError, "reserved root path"):
                verify_manifest(root, manifest_path)

    def test_manifest_creation_rejects_a_non_file_incomplete_marker(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "payload.bin").write_bytes(b"expected")
            (root / ".incomplete").mkdir()

            with self.assertRaisesRegex(ManifestError, "regular physical file"):
                create_manifest(root, root / "airgap-manifest.json")

    def test_manifest_api_rejects_a_reserved_or_renamed_manifest_target(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "payload.bin").write_bytes(b"expected")

            with self.assertRaisesRegex(ManifestError, "must be named"):
                create_manifest(root, root / ".incomplete")
            with self.assertRaisesRegex(ManifestError, "must be named"):
                create_manifest(root, root / "renamed-manifest.json")

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

    def test_powershell_builder_finalization_order_is_guarded(self) -> None:
        repo_root = Path(__file__).resolve().parents[3]
        builder = (repo_root / "tools" / "airgap" / "Build-AirgapBundle.ps1").read_text(
            encoding="utf-8"
        )
        create_index = builder.index("& $bundlePython -I $manifestTool create")
        python_allow_index = builder.index("--allow-incomplete-marker", create_index)
        native_index = builder.index("& $bundleBstrings bundle verify", python_allow_index)
        native_allow_index = builder.index("--allow-incomplete-marker", native_index)
        delete_index = builder.index("[IO.File]::Delete($incomplete)", native_allow_index)
        catch_index = builder.index("catch {", delete_index)
        restore_index = builder.index("[IO.File]::WriteAllText($incomplete", catch_index)

        self.assertLess(create_index, python_allow_index)
        self.assertLess(python_allow_index, native_index)
        self.assertLess(native_index, native_allow_index)
        self.assertLess(native_allow_index, delete_index)
        self.assertLess(catch_index, restore_index)

    def test_runtime_powershell_verifier_rejects_marker_before_native_probe(self) -> None:
        repo_root = Path(__file__).resolve().parents[3]
        verifier = (repo_root / "tools" / "airgap" / "Verify-AirgapBundle.ps1").read_text(
            encoding="utf-8"
        )

        marker_index = verifier.index("Join-Path $PSScriptRoot '.incomplete'")
        rejection_index = verifier.index("lingering root .incomplete marker", marker_index)
        native_index = verifier.index("& $bstrings bundle verify", rejection_index)
        self.assertLess(marker_index, rejection_index)
        self.assertLess(rejection_index, native_index)

    def test_translation_smoke_interrupts_and_resumes_full_analysis(self) -> None:
        repo_root = Path(__file__).resolve().parents[3]
        verifier = (repo_root / "tools" / "airgap" / "Verify-AirgapBundle.ps1").read_text(
            encoding="utf-8"
        )

        checkpoint = ".bstrings-resume\\checkpoints\\0007-translation-selection.json"
        self.assertIn(checkpoint, verifier)
        self.assertIn("& taskkill.exe /PID $analysisProcess.Id /T /F", verifier)
        self.assertIn(
            "& $bstrings analyze -r -o $resultsPath --bundle-root $PSScriptRoot", verifier
        )
        for stage_id in (
            "input-inventory",
            "content-routing",
            "native-extraction",
            "floss-recovery",
            "ocr",
            "raw-merge",
            "translation-selection",
        ):
            self.assertIn(f"'{stage_id}'", verifier)

    def test_tag_release_is_gated_on_bundle_acceptance(self) -> None:
        repo_root = Path(__file__).resolve().parents[3]
        workflow = (repo_root / ".github" / "workflows" / "dotnet-desktop.yml").read_text(
            encoding="utf-8"
        )
        acceptance_index = workflow.index("  bundle-acceptance:")
        release_index = workflow.index("  release:", acceptance_index)
        acceptance = workflow[acceptance_index:release_index]
        release = workflow[release_index:]

        self.assertIn("if: startsWith(github.ref, 'refs/tags/v')", acceptance)
        self.assertIn(
            "runs-on: [self-hosted, Windows, X64, bstrings-offline-release]",
            acceptance,
        )
        self.assertIn("Invoke-OfflineBundleAcceptance.ps1", acceptance)
        self.assertIn("name: bstrings-offline-bundle-acceptance", acceptance)
        self.assertIn("needs: [build, bundle-acceptance]", release)
        self.assertIn("name: bstrings-offline-bundle-acceptance", release)
        self.assertIn("Validate checked bundle-acceptance evidence", release)
        self.assertIn("Test-OfflineBundleReleaseEvidence.ps1", release)
        self.assertIn("path: release-gate-evidence", release)
        self.assertIn(
            "-EvidencePath release-gate-evidence/offline-bundle-acceptance.json",
            release,
        )
        self.assertIn("-ExpectedServerUrl $env:GITHUB_SERVER_URL", release)
        self.assertIn("-ComponentLockPath tools/airgap/offline-components.lock.json", release)
        self.assertIn("fail_on_unmatched_files: true", release)
        validation = release[
            release.index("Validate checked bundle-acceptance evidence") : release.index(
                "Create GitHub release"
            )
        ]
        self.assertNotIn("GITHUB_RUN_ATTEMPT", validation)
        publication = release[release.index("Create GitHub release") :]
        self.assertIn("body_path: docs/releases/${{ github.ref_name }}.md", publication)
        self.assertIn("generate_release_notes: false", publication)
        self.assertIn("overwrite_files: true", publication)
        self.assertIn("preserve_order: true", publication)
        self.assertIn("Verify the published immutable release", publication)
        self.assertIn("-not $release.isImmutable", publication)
        self.assertIn("Published release asset set mismatch", publication)
        self.assertNotIn("offline-bundle-acceptance.json", publication)

        script = (repo_root / "tools" / "airgap" / "Invoke-OfflineBundleAcceptance.ps1").read_text(
            encoding="utf-8"
        )
        acquire_index = script.index("& $baseExecutable bundle acquire")
        verify_index = script.index("& $bundleExecutable bundle verify", acquire_index)
        smoke_index = script.index("-TranslationSmoke", verify_index)
        result_index = script.index("$result =", smoke_index)
        cleanup_index = script.index("Remove-CompletedBundleDirectory", result_index)
        evidence_index = script.index("offline-bundle-acceptance.json", cleanup_index)

        self.assertLess(acquire_index, verify_index)
        self.assertLess(verify_index, smoke_index)
        self.assertLess(smoke_index, result_index)
        self.assertLess(result_index, cleanup_index)
        self.assertLess(cleanup_index, evidence_index)

        validator = (
            repo_root / "tools" / "airgap" / "Test-OfflineBundleReleaseEvidence.ps1"
        ).read_text(encoding="utf-8")
        self.assertIn("Acceptance evidence schemaVersion", validator)
        self.assertIn("positive acceptance run attempt", validator)
        self.assertNotIn("ExpectedRunAttempt", validator)
        self.assertIn("exact expected file set", validator)
        self.assertIn("Duplicate SHA256SUMS.txt row", validator)
        self.assertIn("exact expected server, repository, tag, path, and casing", validator)
        self.assertIn("exact checked component-lock URL", validator)
        self.assertIn("canonical Hugging Face repository ID", validator)

        self.assertIn("translationModelUrl = [string]$modelPack.url", script)

    def test_kit_installer_is_version_pinned_and_release_gated(self) -> None:
        repo_root = Path(__file__).resolve().parents[3]
        installer_path = repo_root / "Scripts" / "Install-Bstrings.ps1"
        installer_test_path = repo_root / "Scripts" / "tests" / "Test-Install-Bstrings.ps1"
        self.assertTrue(installer_path.is_file())
        self.assertTrue(installer_test_path.is_file())
        installer = installer_path.read_text(encoding="utf-8")
        default_tag_match = re.search(
            r"\[string\]\$ReleaseTag\s*=\s*['\"](v[0-9]+\.[0-9]+\.[0-9]+)['\"]",
            installer,
        )
        expected_tag_match = re.search(
            r"\$expectedReleaseTag\s*=\s*['\"](v[0-9]+\.[0-9]+\.[0-9]+)['\"]",
            installer,
        )
        self.assertIsNotNone(default_tag_match)
        self.assertIsNotNone(expected_tag_match)
        self.assertEqual(default_tag_match.group(1), expected_tag_match.group(1))
        self.assertEqual("v2.1.2", expected_tag_match.group(1))
        self.assertIn("Join-Path (Get-Location).Path 'bstrings-kit'", installer)
        self.assertIn("bundle-packs.json", installer)
        self.assertIn("SHA256SUMS.txt", installer)
        self.assertIn("bstrings-win-x64.zip", installer)

        workflow = (repo_root / ".github" / "workflows" / "dotnet-desktop.yml").read_text(
            encoding="utf-8"
        )
        self.assertIn("Test Windows kit installer with Windows PowerShell 5.1", workflow)
        self.assertIn("Test Windows kit installer with PowerShell 7", workflow)
        self.assertIn(
            "-InstallerScript (Join-Path $PWD 'Scripts\\Install-Bstrings.ps1')",
            workflow,
        )
        self.assertIn("release-assets/Install-Bstrings.ps1", workflow)
        self.assertIn("release-assets/bundle-packs.json", workflow)

        core_release_workflow = (
            repo_root / ".github" / "workflows" / "publish-windows-release.yml"
        ).read_text(encoding="utf-8")
        self.assertIn("bstrings/bstrings.csproj", core_release_workflow)
        self.assertIn('$tag = "v$($versions[0])"', core_release_workflow)
        self.assertIn("bstrings-win-x64.zip", core_release_workflow)
        self.assertNotIn("Install-Bstrings.ps1", core_release_workflow)
        self.assertIn("Create the exact tested version tag", core_release_workflow)
        self.assertIn('-f ref="refs/tags/$env:RELEASE_TAG"', core_release_workflow)
        self.assertIn("--verify-tag", core_release_workflow)
        self.assertIn("--draft", core_release_workflow)
        self.assertIn("-not $release.isDraft", core_release_workflow)
        self.assertIn("$release.isImmutable", core_release_workflow)
        prepared_index = core_release_workflow.index(
            'Write-Host "Prepared Windows release draft $tag for $env:RELEASE_SHA."'
        )
        success_index = core_release_workflow.index("exit 0", prepared_index)
        self.assertLess(prepared_index, success_index)

        pack_builder = (repo_root / "tools" / "airgap" / "New-BundlePackRelease.ps1").read_text(
            encoding="utf-8"
        )
        acceptance = (
            repo_root / "tools" / "airgap" / "Invoke-OfflineBundleAcceptance.ps1"
        ).read_text(encoding="utf-8")
        release_validator = (
            repo_root / "tools" / "airgap" / "Test-OfflineBundleReleaseEvidence.ps1"
        ).read_text(encoding="utf-8")
        for source in (pack_builder, acceptance, release_validator):
            self.assertIn("Install-Bstrings.ps1", source)

    def test_public_getting_started_path_is_installer_only(self) -> None:
        repo_root = Path(__file__).resolve().parents[3]
        active_paths = (
            repo_root / "README.md",
            repo_root / "BASE_PACK_NOTICE.md",
            repo_root / "docs" / "air-gapped-deployment.md",
        )
        public_sources = {path: path.read_text(encoding="utf-8") for path in active_paths}

        readme = public_sources[repo_root / "README.md"]
        self.assertIn("## What bstrings does", readme)
        self.assertIn("## Install", readme)
        self.assertIn("## Run an analysis", readme)
        installer = (repo_root / "Scripts" / "Install-Bstrings.ps1").read_text(encoding="utf-8")
        self.assertIn("$expectedReleaseTag = 'v2.1.2'", installer)
        self.assertIn(
            "4acf14c70fa5d6cdeef19ead06281e0ad1bf51ccd18a79fea02620baa05a0274",
            installer,
        )
        self.assertIn(
            "8c7791a0a28810264a411f0ca32d48793eeb0b30e35cebeb4721106d284c7d56",
            installer,
        )
        self.assertIn(
            "4bf9c7eb16fa3da2c2a30436cca0427eed5e77442072eeb67c37fbd3056f9a01",
            installer,
        )
        self.assertIn("v1.9.17", readme)
        self.assertIn("$tag = 'v2.1.2'", readme)
        self.assertIn("$name = 'Install-Bstrings.ps1'", readme)
        self.assertIn("/releases/download/$tag/$name", readme)
        self.assertIn("-ReleaseTag $tag", readme)

        for path, source in public_sources.items():
            self.assertIn("Install-Bstrings.ps1", source, path)
            self.assertNotIn("bundle acquire", source, path)
            self.assertNotIn("bundle assemble", source, path)
            self.assertNotIn("bundle-packs-", source, path)

        historical_release = (repo_root / "docs" / "releases" / "v1.9.17.md").read_text(
            encoding="utf-8"
        )
        self.assertIn("Install-BstringsQuality.ps1", historical_release)

    def test_visual_cpp_overlay_refreshes_ocr_inventory_before_bundle_validation(self) -> None:
        repo_root = Path(__file__).resolve().parents[3]
        script = (repo_root / "tools" / "airgap" / "Build-CompleteOfflineBundle.ps1").read_text(
            encoding="utf-8"
        )
        overlay_index = script.index("$runtimeStageResult = @(")
        refresh_index = script.index("refresh-inventory `", overlay_index)
        runtime_source_index = script.index(
            "--visual-cpp-runtime $resolvedVisualCppRuntime", refresh_index
        )
        reparse_check_index = script.index(
            "Assert-NoReparsePoints $stagingRoot 'Offline component staging directory'",
            refresh_index,
        )
        bundle_index = script.index("& $builder", reparse_check_index)

        self.assertLess(overlay_index, refresh_index)
        self.assertLess(refresh_index, runtime_source_index)
        self.assertLess(runtime_source_index, reparse_check_index)
        self.assertLess(reparse_check_index, bundle_index)


if __name__ == "__main__":
    unittest.main()
