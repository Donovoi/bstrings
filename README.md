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
- Language triage reuses successful detections for exact duplicate text only
  within its bounded batch; reviewed 50%-duplicate workloads were 1.85-1.91x
  faster without changing ordered report bytes.
- The quality kit includes its runtimes, models, tools, licences, and strict
  manifest, so case work does not depend on Python, a package manager, or the
  internet.
- Downloads and the finished installation are checked by exact size and
  SHA-256 before use.

## Get started

The complete Windows x64 quality/offline release is
[v1.9.15](https://github.com/Donovoi/bstrings/releases/tag/v1.9.15).
There is one install and one Full profile: the largest, highest-scoring accepted
Hy-MT2 7B Q8_0 translation model is included instead of asking examiners to
choose among quality/size tiers.
Requirements: Windows 11 x64, a connected staging machine, and at least
**30 GiB free**. Administrator rights are not required.

Open PowerShell in the directory where you want `bstrings-quality`, then run
this pinned, checksum-verified installer bootstrap:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v1.9.15'
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
      -not ($release.PSObject.Properties.Name -ccontains 'immutable') -or
      -not [bool]$release.immutable -or
      $asset.Count -ne 1 -or $asset[0].browser_download_url -CNE $url -or
      ([string]$asset[0].digest) -CNotMatch '^sha256:[0-9a-f]{64}$') {
    throw 'The exact immutable published installer asset could not be authenticated.'
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
`Install-BstringsQuality.ps1`, but only after the release reports immutable
state and the new download matches its published SHA-256. The installer also
always refreshes an existing
physical `bstrings-quality` directory with the complete authenticated release;
it never patches old files in place. The replacement is assembled and verified
beside the destination first, and the previous directory is restored if the
swap or installed-path verification fails. A retry or later release may reuse
only fully size- and SHA-256-reverified bytes. The default shared cache is
retained beside the installation and can reuse an unchanged exact pack across
releases, but its filename or prior presence never authorizes its bytes: every
use reopens and fully hashes the pack against the current release manifest. Add
`-RemoveCacheAfterSuccess` to the authenticated installer's final invocation
when bounded cache cleanup is preferred over later reuse. The installer still
creates and verifies a fresh sibling replacement on every run.

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

`--full` freezes input hashes, batch-classifies each supplied file once, always
runs native extraction, routes applicable files to FLOSS/OCR, then performs
language assessment, local translation, all 66 built-in patterns, and the
TSV/histogram reporting stage. The result retains `content-routing.jsonl`, the
three-rows-per-input `engine-status.jsonl` terminal coverage ledger, and their
hash/count summaries. It does not mount filesystems or carve embedded files from raw disk or
memory images; mount or carve those images first when file-level FLOSS and OCR
coverage is required.

For an air-gapped workstation, copy the whole `bstrings-quality` directory and
run `bundle verify` again before examining evidence.

Detailed guidance: [terminal help and command reference](docs/command-reference.md),
[download and installation](docs/download-and-install.md),
[air-gapped deployment](docs/air-gapped-deployment.md),
[analysis and translation](docs/enrichment-pipeline.md), and
[OCR and document analysis](docs/ocr-and-document-analysis.md),
[document-reading research and roadmap](docs/document-reading-research-2026-08.md),
the [C#/Native AOT/P/Invoke performance review](docs/language-triage-performance-2026-08.md),
and [outputs and provenance](docs/output-and-provenance.md). The forensic report
contract and its bulk_extractor/Timeline Explorer design evidence are recorded
in [forensic reporting](docs/forensic-reporting-2026-08.md).

High-level changes that can affect evidence coverage, provenance, model/tool
selection, routing, privacy, or performance defaults use the
[Robin-round architecture decision policy](docs/architecture/decision-review-policy.md).
The evidence, detractor review, falsifiers, implementation, and release gates
for the early shared fail-open routing stage are recorded in
[ADR-0001](docs/architecture/adr-0001-early-fail-open-content-routing.md).
The hostile-cache trust boundary, fresh-overwrite guarantee, and batched
release policy are recorded in
[ADR-0003](docs/architecture/adr-0003-persistent-verified-bytes-and-batched-releases.md).

The project remains under its upstream terms in [LICENSE.md](LICENSE.md), with
component attribution in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
