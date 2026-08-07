# Magika CLI in the offline bundle

Distribution status: this reviewed Magika payload is included in the complete
v1.9.6 quality kit. It is not included in the standalone core ZIP. Examiners
should use the version-matched installer in the
[download guide](download-and-install.md), not copy the runtime into another
release.

The complete offline bundle includes the reviewed
[Magika CLI 1.1.0](https://github.com/google/magika/releases/tag/cli%2Fv1.1.0)
Windows x64 executable and its app-local DirectML 1.15.4 runtime. An end user
does not install Magika, Python, ONNX Runtime, DirectML, Rust, or a model
package separately, and normal analysis does not require network access.

The final overlay places the two runtime files together:

- `tools/magika/magika.exe`
- `tools/magika/DirectML.dll`

Component-specific licenses, provenance, and the 70-package runtime inventory are under `licenses/magika-cli-1.1.0`. The exact MPL-covered source for `colored` and Eigen is under `sources/magika-cli-1.1.0`.

## Release staging and verification

From the repository root, create a new or empty overlay directory:

```powershell
pwsh -NoProfile -File tools/licenses/Stage-MagikaRedistribution.ps1 `
  -DestinationDirectory C:\release\magika-overlay `
  -DownloadCacheDirectory C:\release-cache\magika
```

The stager accepts only the HTTPS URLs, byte lengths, and SHA-256 hashes in `licenses/magika-cli-1.1.0-redistribution.json`. Redirects must also end on HTTPS. Large Magika, ONNX Runtime, DirectML, and Rust archives are verified cache inputs; they are not copied into the user-facing bundle. Only the runtime, required source, notices, and compact provenance extracts are staged.

Add `-CacheOnly` for a release-cache audit. In that mode every one of the 14
locked artifacts must already exist with its exact length and SHA-256; a
missing artifact fails immediately and never falls back to the network.

Verify either the standalone overlay or the root of the merged final bundle:

```powershell
pwsh -NoProfile -File tools/licenses/Verify-MagikaRedistribution.ps1 `
  -BundleDirectory C:\release\complete-offline
```

Verification checks every owned file's exact length and hash, rejects leaked cache archives and unexpected files in the component-owned license/source trees, checks the app-local PE dependency chain, reconciles all 68 registry checksums with Magika's Cargo lock, validates the Eigen and Rust pins, and requires one component-specific notice section for each of the 70 runtime packages. Other lock-governed files beside Magika, such as the app-local Visual C++ runtime, are allowed because the complete-bundle verifier owns those files.

## Updating package notices

`licenses/magika-cli-1.1.0-THIRD-PARTY-NOTICES.txt` is deterministic output derived from the 68 checksum-locked `.crate` archives plus Magika's exact upstream license. It preserves every license, notice, copying, copyright, and unlicense file published in those archives, grouped by package.

After intentionally updating the reviewed package graph, regenerate it from a populated Cargo cache:

```powershell
pwsh -NoProfile -File tools/licenses/New-MagikaThirdPartyNotices.ps1 `
  -CrateCacheDirectory $env:USERPROFILE\.cargo\registry\cache `
  -MagikaLicensePath C:\release-cache\magika\Magika-LICENSE.txt `
  -OutputPath licenses\magika-cli-1.1.0-THIRD-PARTY-NOTICES.txt
```

Review the graph and notices, then update the locked byte length and hash in the redistribution inventory. The release verifier deliberately fails if the inventory, lock file, notices, source artifacts, or runtime binaries drift independently.
