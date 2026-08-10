# Download and install bstrings on Windows

## Current release

[bstrings v1.9.16](https://github.com/Donovoi/bstrings/releases/tag/v1.9.16)
is the complete Windows x64 release. Its quality installer assembles one
verified offline directory containing the current native scanner and reporting
code together with FLOSS, Magika, OCR, language detection, and local
translation assets. There is no model-tier choice: the one Full profile uses
the accepted Hy-MT2 7B Q8_0 translation model.

| Installation | Included | Intended use |
| --- | --- | --- |
| Complete `bstrings-quality` kit | Every v1.9.16 stage, runtime, model, licence, manifest, all 66 patterns, TSV reports, and histograms | Normal and air-gapped forensic analysis |
| Core ZIP only | Native CPU/Rust/CUDA/hybrid extraction, current patterns, native-only JSONL/TSV reports, and histograms | Small native-only installation or diagnostics |

Do not combine executables, manifests, packs, tools, or models from different
versions. The installer and bundle verifier intentionally reject mixed-version
installations.

## Install every feature

Requirements: Windows 11 x64, a connected staging machine, and at least
30 GiB free on the installation/cache volume. Administrator rights are not
required. DirectML OCR requires a compatible Windows GPU/driver stack; CPU OCR
and native CPU extraction remain available without a GPU. The standard local
translation runtime is CPU-only; CUDA translation needs a separate custom
runtime that is not part of this release.

Run the [safe pinned installer bootstrap from the v1.9.16
README](https://github.com/Donovoi/bstrings/blob/v1.9.16/README.md#get-started)
verbatim. Do not pipe a downloaded script into `Invoke-Expression`. The
bootstrap downloads to a unique temporary file, requires the exact GitHub
release to be published and immutable, authenticates the installer digest, and
then replaces any existing physical
`Install-BstringsQuality.ps1` before launching it. A failed download or digest
check preserves the previous installer. The installer then downloads, resumes,
assembles, and strictly verifies the complete `bstrings-quality` directory.
Every run refreshes an existing physical destination rather than accepting or
patching its old contents. It verifies a unique sibling replacement first,
keeps the previous directory as a rollback backup during the swap, and removes
that backup only after the installed executable verifies the new directory.
Stale files that are absent from the current release therefore do not survive.

The installer prints an overall percentage, while its bundle client prints
measured percentages for each pack download and hash plus assembly and final
verification. The default shared cache is retained beside the destination so a
retry or later release can reuse an unchanged exact pack. Every materialized
cache hit is length-checked and fully SHA-256 hashed against the current trust
manifest before use; cache metadata and prior success are never authoritative.
An interrupted retry resumes private partial data and begins a new displayed
attempt. Percentages describe bytes or other completed work units, not an ETA.

Verify the completed bundle before use:

```powershell
.\bstrings-quality\bstrings.exe bundle verify
```

Run every v1.9.16 stage over one file:

```powershell
.\bstrings-quality\bstrings.exe analyze `
  -f "C:\evidence\memory.raw" `
  -o "C:\results\memory-full" `
  --full
```

Or analyze a directory recursively:

```powershell
.\bstrings-quality\bstrings.exe analyze `
  -d "C:\evidence\carved-files" `
  -o "C:\results\case-01" `
  --full
```

The results directory must be new or empty. `--full` selects immutable input
hashing, one batched fail-open routing pass, universal native extraction,
routed FLOSS recovery and PDF/OCR processing, language assessment, local
translation, all built-in patterns, and the report projection. The completed
result includes the authoritative JSONL evidence graph, `content-routing.jsonl`,
the routed input projections, the per-input `engine-status.jsonl` terminal
coverage ledger, `findings.tsv`,
`pattern-histogram.tsv`, `feature-histogram.tsv`, and
`pattern-histogram.html`, plus input, run, summary, and completion records.
During the run, `Progress: analysis:` lines show overall stage completion.
Long-running stages also report their own byte, file, chunk, or record percentages,
so a quiet stage is distinguishable from a stalled process.

Full retains high-recall translation selection. Exact source/configuration
duplicates are inferred once through a bounded-memory, run-local SQLite cache,
but one child and provenance link are still emitted for every parent. The cache
is never reused across cases and is removed when the translation transaction
finishes or fails cleanly. Translation progress includes percentage, measured
rate, ETA, cache hits, model-input count, and integrity-fallback count. Dedup can
remove many redundant calls, but a mostly unique high-recall CPU run can still
take a long time. The explicit `high-precision` policy uses at least 0.65
confidence and 0.15 target margin if reduced candidate volume is acceptable.

Discover commands and current defaults without touching evidence:

```powershell
.\bstrings-quality\bstrings.exe help
.\bstrings-quality\bstrings.exe help analyze
.\bstrings-quality\bstrings.exe help bundle verify
```

See the [terminal help and command reference](command-reference.md) for the
workflow chooser, pattern groups, hardware controls, completion rules, and
legacy flat-output interface.

## Upgrade or retry safely

Rerun the pinned bootstrap in the same parent directory whenever the local
installer script is stale: a successfully authenticated download always
replaces that file. The bundle transaction then:

1. assembles and verifies a complete sibling replacement on every run;
2. can reuse an exact pack across releases only after reopening it and fully
   checking its current expected size and SHA-256; cache hits never reuse a
   verification decision;
3. swaps the verified replacement over a valid physical destination, removes
   all stale old files, and restores the prior directory if final verification
   fails;
4. rejects a linked destination tree rather than risking writes outside it; and
5. leaves analysis output rules unchanged: a new analysis still requires a new
   or empty result directory.

For a version upgrade, rerun the current pinned bootstrap from the same parent
directory. The installer keeps the persistent cache but creates a new complete
sibling installation, keeps the previous directory as an internal rollback
backup, and replaces it only after both staged and installed verification pass.
Use `-RemoveCacheAfterSuccess` only when the default script-owned cache should
be deleted after a successful fresh overwrite. `-KeepCache` remains a
compatible explicit spelling of the default, while a caller-supplied
`-InstallerCacheDirectory` is always retained and cannot be removed by that
cleanup switch. Do not merge release files by hand.
If analysis failed, retain its `.incomplete` result for diagnosis and rerun into
a different empty directory after correcting the cause.

`--full` does not mount filesystems or carve files from raw disk or memory
images. The direct native scanner can search raw bytes, but file-level FLOSS
and OCR coverage requires the relevant executable, image, or PDF to be mounted
or carved and supplied as an input file.

## Transfer the complete kit offline

After the connected installation succeeds, copy the entire verified
`bstrings-quality` directory to approved media. Preserve the adjacent tools,
runtimes, models, licences, configuration, and manifests. On the disconnected
workstation, run:

```powershell
.\bstrings.exe bundle verify
.\bstrings.exe analyze -d D:\evidence\carved-files --full -o D:\results\case-01
```

No package manager, Python installation, model hub, service installation, or
network connection is required during examination.

## Optional core-only installation

Use this smaller path only when native extraction and the current report set
are sufficient. Open PowerShell in the directory where the new
`bstrings-v1.9.16` directory should be created, then run this exact-tag,
API-digest-verified download:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v1.9.16'
  $repo = 'Donovoi/bstrings'
  $archiveName = 'bstrings-win-x64.zip'
  $destination = Join-Path (Get-Location) 'bstrings-v1.9.16'
  if ((Test-Path -LiteralPath $archiveName) -or
      (Test-Path -LiteralPath $destination)) {
    throw 'Refusing to overwrite the archive or destination.'
  }

  $headers = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'bstrings-core-bootstrap'
  }
  $release = Invoke-RestMethod `
    "https://api.github.com/repos/$repo/releases/tags/$tag" `
    -Headers $headers -UseBasicParsing
  $asset = @($release.assets | Where-Object { $_.name -CEQ $archiveName })
  $expectedUrl = "https://github.com/$repo/releases/download/$tag/$archiveName"
  if ($release.tag_name -CNE $tag -or $release.draft -or
      $release.prerelease -or
      -not ($release.PSObject.Properties.Name -ccontains 'immutable') -or
      -not [bool]$release.immutable -or $asset.Count -ne 1 -or
      $asset[0].browser_download_url -CNE $expectedUrl -or
      ([string]$asset[0].digest) -CNotMatch '^sha256:[0-9a-f]{64}$') {
    throw 'The exact immutable published core asset could not be authenticated.'
  }

  Invoke-WebRequest $expectedUrl -OutFile $archiveName -UseBasicParsing
  $expected = ([string]$asset[0].digest).Substring(7)
  $actual = (Get-FileHash -LiteralPath $archiveName -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -CNE $expected) { throw 'Core archive SHA-256 mismatch.' }

  Expand-Archive -LiteralPath $archiveName -DestinationPath $destination
  & (Join-Path $destination 'bstrings.exe') --help
}
```

The release also publishes `SHA256SUMS.txt` covering every public release
asset. Use the integrated native-only path when the filterable report set is
wanted:

```powershell
.\bstrings-v1.9.16\bstrings.exe analyze `
  -f "C:\evidence\memory.raw" `
  -o "C:\results\memory-native" `
  --recover-executable-strings off `
  --ocr off `
  --translation off `
  --lr all `
  --processor auto
```

The legacy command below writes one flat output file instead of the integrated
TSV/histogram report set:

```powershell
.\bstrings-v1.9.16\bstrings.exe `
  -f "C:\evidence\memory.raw" `
  --lr all --ro --off --trace `
  -o "C:\results\memory-hits.csv"
```

The core ZIP alone cannot run FLOSS, OCR, or translation. Run the quality
installer when those stages are required; do not copy their files manually
into the core directory.

See [air-gapped deployment](air-gapped-deployment.md),
[enrichment pipeline](enrichment-pipeline.md), and
[output and provenance](output-and-provenance.md) for operational details and
interpretation boundaries.
