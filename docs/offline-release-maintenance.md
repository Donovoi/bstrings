# Windows kit release maintenance

This guide is for maintainers. Users install one complete Windows kit.

The release can use several files to transfer the kit. These files are
installer components, not separate product choices.

## Public asset set

The current release uses these public names:

- `Install-Bstrings.ps1`
- `bstrings-win-x64.zip`
- `bstrings-win-x64-offline-base.zip`
- `bstrings-win-x64-offline-cuda.zip`
- `airgap-config.json`
- `airgap-manifest.json`
- `bundle-packs.json`
- `Hy-MT2-Apache-2.0.txt`
- `SHA256SUMS.txt`

`SHA256SUMS.txt` covers every public asset except itself.

The exact workflow inventory is authoritative. Update this list when the
accepted workflow changes it.

`offline-bundle-acceptance.json` is an internal Actions artifact. Do not publish
it as a release asset.

The public v1.9.17 release is immutable. Its former filenames remain on that
release and in its historical release notes.

## Installer contract

Publish `Scripts/Install-Bstrings.ps1` unchanged as `Install-Bstrings.ps1`.
Include its exact size and SHA-256 in release metadata and `SHA256SUMS.txt`.

The installer must:

- support Windows PowerShell 5.1 and PowerShell 7;
- require Windows x64;
- use `bstrings-kit` as the default destination;
- use `.bstrings-installer-cache` as the default cache;
- authenticate the exact immutable release;
- authenticate itself and every installer component;
- treat cached bytes as untrusted;
- build and verify a new sibling directory;
- replace only an identified bstrings installation;
- restore the prior installation after a failed replacement; and
- verify the installed path after the swap.

The installer must refuse to replace an unrelated `bstrings-kit` directory.
This includes a source checkout that contains `bstrings\bstrings.csproj` or
other unrelated content.

## Historical installation migration

The first next-major installer can migrate the former default installation.
Migration is a one-time compatibility path, not another supported kit name.

Apply all these rules:

1. Start migration only when the caller uses the default destination.
2. Require the new destination to be absent.
3. Require the old path to be a physical directory.
4. Stop if both old and new paths exist.
5. Authenticate each reused file against the current manifest.
6. Build and verify the complete new directory first.
7. Preserve rollback until installed-path verification succeeds.
8. Remove the old path only after complete success.

An explicit destination must not start automatic migration.

The installer can import unchanged objects from the former cache. It must check
the source size and digest, copy the object, and check the copy again.

## Pinned inputs

`tools/airgap/offline-components.lock.json` pins public component identity. Each
downloaded input needs an immutable URL or revision, exact byte length, SHA-256,
license source, and required runtime metadata.

There is one `translationModel` object. Do not add a one-item model selector or
duplicate the model under another label.

`tools/airgap/ocr-components.lock.json` pins OCR inputs and accepted runtime
closure.

Review dependency licenses before you update either lock.

## Build the application archive

Publish the exact tested commit:

```powershell
dotnet publish .\bstrings\bstrings.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -o .\publish\win-x64
```

Create `bstrings-win-x64.zip` from that directory. Treat this archive as an
installer component. Do not advertise it as a separate installation.

Run the normal .NET and native tests before you use the archive.

## Validate the connected build plan

Use a new output path:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\bstrings-kit `
  -DryRun
```

The dry run checks pinned inputs, tools, and required storage. It must not
create a release candidate.

## Build the complete kit

Use a new output path:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\bstrings-kit
```

The builder downloads locked public components, checks them, stages licenses,
builds required runtimes, writes manifests, and verifies the result.

Use `-ValidateOnly` only with an already acquired and fully checked working
set. A cache hit never replaces current size and digest checks.

## Verify the completed kit

```powershell
.\tools\airgap\Verify-AirgapBundle.ps1 `
  -BundleDirectory .\publish\bstrings-kit
```

Also run the installed executable:

```powershell
.\publish\bstrings-kit\bstrings.exe bundle verify
```

Verification must reject missing, added, changed, linked, or misplaced files.

## Create installer components

Use the complete verified kit as input:

```powershell
.\tools\airgap\New-BundlePackRelease.ps1 `
  -BundleDirectory .\publish\bstrings-kit `
  -OutputDirectory .\publish\release-assets `
  -ReleaseAssetBaseUrl https://github.com/Donovoi/bstrings/releases/download/vX.Y.Z `
  -BaseReleaseArchive .\publish\bstrings-win-x64.zip `
  -InstallerScript .\Scripts\Install-Bstrings.ps1
```

The command must create the exact accepted asset inventory. It must also test
that the parts reconstruct the verified source kit.

Generate `SHA256SUMS.txt` from the final staged asset bytes. Sort its entries
with the workflow's canonical rule.

## Run bundle acceptance

Acceptance must use fresh same-run evidence. It must:

- authenticate the exact source tag, commit, repository, run, and attempt;
- get the installer components through their release URLs;
- assemble a new kit;
- verify the exact manifest;
- run native, FLOSS, OCR, and translation smoke tests;
- record model and runtime identity; and
- write `offline-bundle-acceptance.json` atomically.

The release checker must bind the evidence to the final asset sizes and
digests. It must reject evidence from another run or asset set.

## Test the installer

Run the synthetic installer suite in both shells:

```powershell
powershell.exe -NoProfile -File .\Scripts\tests\Test-Install-Bstrings.ps1
pwsh -NoProfile -File .\Scripts\tests\Test-Install-Bstrings.ps1
```

Test at least these cases:

- clean installation;
- safe replacement of an identified old installation;
- unrelated destination refusal;
- source-directory collision refusal;
- both old and new default paths present;
- verified historical migration;
- failure during each move, verification, and rollback boundary;
- poisoned, truncated, linked, and stale cache entries;
- warm reuse without another unchanged model download;
- PowerShell interruption and retry; and
- optional bounded cache cleanup.

## Release gates

Do not publish until all gates pass:

1. The source version, tag, installer pin, and release notes agree.
2. The tag points to the exact tested commit.
3. All build and test jobs pass.
4. The complete kit verifies.
5. Engine smoke tests pass.
6. Bundle acceptance matches the final assets.
7. `SHA256SUMS.txt` covers every public asset exactly once.
8. The public diff contains no private case data.
9. The release draft has the exact expected asset set.
10. Independent install and offline-copy tests pass.

Create the release as a draft. Attach all assets before publication. Publish it
once, then confirm that GitHub reports the release as immutable.

The successful master build stages the version tag and preliminary draft. It
does not start another tag workflow automatically. Start the complete tagged
lane explicitly:

```powershell
gh workflow run dotnet-desktop.yml `
  --repo Donovoi/bstrings `
  --ref v3.0.0 `
  -f full_offline=true
```

Use the exact prepared tag. If the tagged candidate fails, do not move or reuse
the tag. Fix the problem and prepare a new patch version.

## Privacy check

Scan every public file and outgoing diff. Reject case paths, hostnames, IP
addresses, filenames, hashes, extracted values, and private report content.

Use only synthetic fixtures in tests and public documentation.

## Failure handling

Never publish a failed candidate. Keep failed build and acceptance evidence for
diagnosis.

Fix the product or release process. Build again from a new candidate. Do not
modify an immutable published release.

## Related records

- [Versioning and releases](../VERSIONING.md)
- [Download and install](download-and-install.md)
- [ADR-0011](architecture/adr-0011-single-windows-kit-and-plain-documentation.md)
- [Decision review policy](architecture/decision-review-policy.md)
