# bstrings

`bstrings` finds and validates useful forensic strings, recovers obfuscated
strings with [FLOSS](https://github.com/mandiant/flare-floss), identifies files
with [Magika](https://github.com/google/magika), performs OCR, detects language,
and translates locally before applying the same pattern catalogue. The complete
Windows kit runs offline through one interface: `bstrings.exe`.

## Why use it?

- One recursive workflow combines native extraction, validated patterns,
  FLOSS, OCR, language triage, offline translation, and provenance.
- The quality kit includes its runtimes, models, tools, licences, and strict
  manifest, so case work does not depend on Python, a package manager, or the
  internet.
- Downloads and the finished installation are checked by exact size and
  SHA-256 before use.

## Get started

Requirements: Windows 11 x64, a connected staging machine, and at least
**30 GiB free**. Administrator rights are not required.

Open PowerShell in the directory where you want `bstrings-quality`, then run
this pinned, checksum-verified installer bootstrap:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v1.9.2'
  $repo = 'Donovoi/bstrings'
  $headers = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'bstrings-installer-bootstrap'
  }
  $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/$tag" `
    -Headers $headers -UseBasicParsing
  $asset = @($release.assets | Where-Object { $_.name -CEQ 'Install-BstringsQuality.ps1' })
  $url = "https://github.com/$repo/releases/download/$tag/Install-BstringsQuality.ps1"
  if ($release.tag_name -CNE $tag -or $release.draft -or $release.prerelease -or
      $asset.Count -ne 1 -or $asset[0].browser_download_url -CNE $url -or
      ([string]$asset[0].digest) -CNotMatch '^sha256:[0-9a-f]{64}$') {
    throw 'The exact published installer asset could not be authenticated.'
  }
  if (Test-Path .\Install-BstringsQuality.ps1) {
    throw 'Refusing to overwrite the existing installer file.'
  }
  Invoke-WebRequest $url -OutFile .\Install-BstringsQuality.ps1 -UseBasicParsing
  $expected = ([string]$asset[0].digest).Substring(7)
  $installer = Get-Item -LiteralPath .\Install-BstringsQuality.ps1 -Force
  $stream = [IO.File]::OpenRead($installer.FullName)
  try {
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
      $actual = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
    }
    finally { $hasher.Dispose() }
  }
  finally { $stream.Dispose() }
  if ($actual -CNE $expected) { throw 'Installer SHA-256 mismatch.' }
  powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\Install-BstringsQuality.ps1 -ReleaseTag $tag
  if ($LASTEXITCODE -ne 0) { throw "Installer failed with exit code $LASTEXITCODE" }
}
```

The installer creates and verifies `.\bstrings-quality`. Run a complete analysis
with:

```powershell
.\bstrings-quality\bstrings.exe bundle verify
.\bstrings-quality\bstrings.exe analyze -d D:\evidence -o D:\results --full
```

For an air-gapped workstation, copy the whole `bstrings-quality` directory and
run `bundle verify` again before examining evidence.

Detailed guidance: [air-gapped deployment](docs/air-gapped-deployment.md),
[analysis and translation](docs/enrichment-pipeline.md), and
[outputs and provenance](docs/output-and-provenance.md).

The project remains under its upstream terms in [LICENSE.md](LICENSE.md), with
component attribution in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
