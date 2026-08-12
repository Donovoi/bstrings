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
- Native extraction, FLOSS, and OCR are independently selectable source
  producers. Native stays on by default and in Full; an explicit
  `--native-extraction off` permits FLOSS-only or OCR-only analysis without
  starting the native scanner. The published v1.9.17 kit includes this option.
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
- Each language assessment also records a compact, deterministic shadow routing
  decision for records that appear to contain only validated structured data.
  Shadow routing is diagnostic: it does not yet suppress language detection or
  translation candidates, and uncertain, mixed, or failed decisions retain the
  existing high-recall path.
- Offline translation reuses an exact source/configuration result through a
  run-local SQLite cache while preserving one ordered child per parent. The
  cache is bounded in memory, never shared between cases, and removed after the
  transaction.
- The quality kit includes its runtimes, models, tools, licences, and strict
  manifest, so case work does not depend on Python, a package manager, or the
  internet.
- Downloads and the finished installation are checked by exact size and
  SHA-256 before use.

## Get started

The complete Windows x64 quality/offline release is
[v1.9.17](https://github.com/Donovoi/bstrings/releases/tag/v1.9.17).
There is one install and one Full profile rather than a user-facing model tier.
Full uses the accepted Hy-MT2 7B Q4_K_M model and a separately authenticated
CUDA overlay under [ADR-0006](docs/architecture/adr-0006-q4-cuda-full-translation.md).
Users do not choose among quality/size tiers.
Requirements: Windows 11 x64, a connected staging machine, and at least
**30 GiB free**. Administrator rights are not required.

Open PowerShell in the directory where you want `bstrings-quality`, then run
this pinned, checksum-verified installer bootstrap:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v1.9.17'
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
- Use `--full --exclude-engine <name>` (or `-e`) to keep Full's defaults except
  for explicitly named engines.
- Use `analyze` with FLOSS, OCR, and translation set to `off` for a native-only
  report directory.
- Use `--native-extraction off` with one or both specialist producers for a
  deliberate FLOSS-only or OCR-only run.
- Use the root `-f`/`-d` options only for the legacy single-file output.
- Use `bundle verify` before examination and after copying the kit offline.

The source-producer controls are orthogonal:

```powershell
# Native extraction only
.\bstrings-quality\bstrings.exe analyze -f D:\evidence\memory.raw `
  --native-extraction on --recover-executable-strings off `
  --ocr off --translation off -o D:\results\native

# FLOSS only; force deliberately attempts every supplied input
.\bstrings-quality\bstrings.exe analyze -d D:\evidence\executables `
  --native-extraction off --recover-executable-strings force `
  --ocr off --translation off -o D:\results\floss

# OCR only
.\bstrings-quality\bstrings.exe analyze -d D:\evidence\documents `
  --native-extraction off --recover-executable-strings off `
  --ocr force --translation off -o D:\results\ocr
```

Native extraction, FLOSS, and OCR produce source records. Translation is a
transform over records produced by one or more of them, so `auto`, `all`, and
`detect-only` never substitute for a producer. Disabling every producer is
invalid for any `analyze` run. Pattern matching and the JSONL/TSV/histogram
reports remain mandatory finalization for every `analyze` run; “only” refers to
the selected source producer, not to removing integrity checks or reports.
FLOSS-only output includes FLOSS static strings, while native-plus-FLOSS keeps
the existing static-string deduplication.

Full exclusions are strict shorthand for the existing explicit `off` modes:

```powershell
# Equivalent spellings: Full without OCR or translation
.\bstrings-quality\bstrings.exe analyze -d D:\evidence\carved `
  --full --exclude-engine ocr --exclude-engine translation `
  -o D:\results\without-ocr-translation

.\bstrings-quality\bstrings.exe analyze -d D:\evidence\carved `
  --full -e ocr,translation -o D:\results\without-ocr-translation
```

`--exclude-engine`/`-e` requires `--full`. Each occurrence consumes one token,
which may contain a comma-separated list of `native`, `floss`, `ocr`, and
`translation`; repeat and comma forms may be combined. Names are
case-insensitive and surrounding whitespace is trimmed, but empty, unknown,
duplicate (including case-duplicate), or whitespace-separated values fail.
Excluding an engine also conflicts with explicitly setting that engine's main
selector, even to `off`. Tuning options for an excluded engine remain validated
but cannot start it. The shorthand resolves to the same effective options as
the explicit controls and adds no separate runtime or provenance path.

Installer, bundle, direct extraction, and integrated-analysis commands print
percentage completion. Integrated analysis combines stage progress with
measured byte or record progress for long-running work. Percentages are
completed work units, not elapsed-time estimates.

Full remains the high-recall translation-selection profile. The optional
`--translation-policy high-precision` expert setting uses effective floors of
0.65 confidence and 0.15 target margin, but it is not a calibrated accuracy
claim. Translation reports its record percentage, rate, ETA, cache hits, model
inputs, and preservation fallbacks; exact deduplication avoids redundant calls
but mostly unique high-recall workloads can still be long-running. In current
source, Full `auto` first probes the authenticated Windows sm89 CUDA path with
complete Q4 model load, a synthetic request, observed 33/33 layer offload, and
p2. A failed probe closes CUDA and self-tests CPU before evidence work. Explicit
CUDA fails closed, and the provider never changes after the first evidence
request. This validation is scoped to the reviewed RTX 4060 Laptop/sm89 host,
not a universal CUDA claim. The accepted placement still reported a
410.69 MiB `CPU_Mapped` model buffer; 33/33 offload does not mean zero host
residency. Hybrid and p4 remain deferred.

`--full` freezes input hashes and defaults native extraction on, routes
applicable files to FLOSS/OCR, then performs language assessment, local
translation, all 66 built-in patterns, and the TSV/histogram reporting stage.
An explicit engine option still overrides its Full default. Specialist runs
retain `content-routing.jsonl` and the three-rows-per-input
`engine-status.jsonl` terminal coverage ledger with its hash/count summary,
including engines explicitly disabled by the user. Full does not mount
filesystems or carve embedded files from raw disk or memory images; mount or
carve those images first when file-level FLOSS and OCR coverage is required.

For an air-gapped workstation, copy the whole `bstrings-quality` directory and
run `bundle verify` again before examining evidence.

Runtime selection does not make the installed quality kit modular. It remains
one atomic authenticated profile, and any missing, extra, or corrupt manifested
file blocks every mode that selects that bundle, even when the damaged engine
was not requested. Smaller physically isolated capability profiles are deferred
under [ADR-0008](docs/architecture/adr-0008-independent-engine-execution.md).

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
The single Q4 model, scoped Windows sm89 CUDA p2 path, pre-evidence CPU
fallback, and release falsifiers are recorded in
[ADR-0006](docs/architecture/adr-0006-q4-cuda-full-translation.md).
Independent runtime engine selection and its unchanged atomic-bundle boundary
are recorded in
[ADR-0008](docs/architecture/adr-0008-independent-engine-execution.md).

The project remains under its upstream terms in [LICENSE.md](LICENSE.md), with
component attribution in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
