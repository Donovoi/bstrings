# bstrings

`bstrings` finds and validates useful forensic strings, recovers obfuscated
strings with [FLOSS](https://github.com/mandiant/flare-floss), identifies files
with [Magika](https://github.com/google/magika), performs OCR, detects language,
and translates locally before applying the same pattern catalogue. The complete
Windows kit runs offline through one interface: `bstrings.exe`.

## Why use it?

- One recursive workflow combines native CPU/Rust/CUDA/hybrid extraction,
  validated patterns, FLOSS, OCR, language triage, offline translation, and
  provenance.
- Completed analysis writes a filterable `findings.tsv`, exact pattern and
  feature histograms, and a self-contained HTML pattern visualization while
  retaining the authoritative JSONL evidence graph.
- The 66-pattern catalogue includes PII, credentials, structurally validated
  JWT candidates, browser artifacts, high-value Registry paths, and crypto
  address families alongside the original forensic patterns.
- Automatic extraction measures eligible CPU, GPU, and hybrid backends and
  selects an accelerator only when it projects a worthwhile win.
- The quality kit includes its runtimes, models, tools, licences, and strict
  manifest, so case work does not depend on Python, a package manager, or the
  internet.
- Downloads and the finished installation are checked by exact size and
  SHA-256 before use.

## Get started

The complete Windows x64 quality/offline release is
[v1.9.9](https://github.com/Donovoi/bstrings/releases/tag/v1.9.9).
Requirements: Windows 11 x64, a connected staging machine, and at least
**30 GiB free**. Administrator rights are not required.

Open PowerShell in the directory where you want `bstrings-quality`, then run
this pinned, checksum-verified installer bootstrap:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v1.9.9'
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
  $installerPath = [IO.Path]::GetFullPath(
    (Join-Path (Get-Location).Path 'Install-BstringsQuality.ps1')
  )
  $downloadPath = Join-Path ([IO.Path]::GetDirectoryName($installerPath)) `
    ('.Install-BstringsQuality.download-' + [Guid]::NewGuid().ToString('N') + '.partial')
  $backupPath = Join-Path ([IO.Path]::GetDirectoryName($installerPath)) `
    ('.Install-BstringsQuality.backup-' + [Guid]::NewGuid().ToString('N') + '.tmp')
  $expected = ([string]$asset[0].digest).Substring(7)
  try {
    Invoke-WebRequest $url -OutFile $downloadPath -UseBasicParsing
    $download = Get-Item -LiteralPath $downloadPath -Force
    $stream = [IO.File]::OpenRead($download.FullName)
    try {
      $hasher = [Security.Cryptography.SHA256]::Create()
      try {
        $actual = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant()
      }
      finally { $hasher.Dispose() }
    }
    finally { $stream.Dispose() }
    if ($actual -CNE $expected) { throw 'Installer SHA-256 mismatch.' }

    $existing = Get-Item -LiteralPath $installerPath -Force -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
      if ($existing.PSIsContainer -or
          ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The existing installer path is not a physical file.'
      }
      [IO.File]::Replace($downloadPath, $installerPath, $backupPath)
      [IO.File]::Delete($backupPath)
    }
    else {
      [IO.File]::Move($downloadPath, $installerPath)
    }
  }
  finally {
    if ([IO.File]::Exists($downloadPath)) { [IO.File]::Delete($downloadPath) }
  }
  powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File $installerPath -ReleaseTag $tag
  if ($LASTEXITCODE -ne 0) { throw "Installer failed with exit code $LASTEXITCODE" }
}
```

The installer creates and verifies `.\bstrings-quality`. Run a complete
analysis with:

```powershell
.\bstrings-quality\bstrings.exe bundle verify
.\bstrings-quality\bstrings.exe analyze -d D:\evidence -o D:\results --full
```

The bootstrap always replaces an existing physical
`Install-BstringsQuality.ps1`, but only after the new download matches the
published release SHA-256. A valid same-version `bstrings-quality` directory is
reverified without redownloading it. An invalid or mixed-version kit is never
patched in place; move it aside or select another destination, then rerun.

## Choose a command

```powershell
.\bstrings-quality\bstrings.exe help
.\bstrings-quality\bstrings.exe help analyze
.\bstrings-quality\bstrings.exe help bundle verify
```

- Use `analyze --full` for the complete provenance-preserving workflow and
  filterable reports.
- Use `analyze` with explicit stages set to `off` for a native-only report
  directory.
- Use the root `-f`/`-d` options only for the legacy single-file output.
- Use `bundle verify` before examination and after copying the kit offline.

Installer, bundle, direct extraction, and integrated-analysis commands print
percentage completion. Integrated analysis combines stage progress with
measured byte or record progress for long-running work. Percentages are
completed work units, not elapsed-time estimates.

`--full` runs native extraction, executable recovery, OCR, language assessment,
local translation, all 66 built-in patterns, and the TSV/histogram reporting
stage. It does not mount filesystems or carve embedded files from raw disk or
memory images; mount or carve those images first when file-level FLOSS and OCR
coverage is required.

For an air-gapped workstation, copy the whole `bstrings-quality` directory and
run `bundle verify` again before examining evidence.

Detailed guidance: [terminal help and command reference](docs/command-reference.md),
[download and installation](docs/download-and-install.md),
[air-gapped deployment](docs/air-gapped-deployment.md),
[analysis and translation](docs/enrichment-pipeline.md), and
[outputs and provenance](docs/output-and-provenance.md). The forensic report
contract and its bulk_extractor/Timeline Explorer design evidence are recorded
in [forensic reporting](docs/forensic-reporting-2026-08.md).

The project remains under its upstream terms in [LICENSE.md](LICENSE.md), with
component attribution in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
