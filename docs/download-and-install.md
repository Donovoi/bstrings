# Download and install bstrings on Windows

## Current release

[bstrings v1.9.6](https://github.com/Donovoi/bstrings/releases/tag/v1.9.6)
is the complete Windows x64 release. Its quality installer assembles one
verified offline directory containing the current native scanner and reporting
code together with FLOSS, Magika, OCR, language detection, and local
translation assets.

| Installation | Included | Intended use |
| --- | --- | --- |
| Complete `bstrings-quality` kit | Every v1.9.6 stage, runtime, model, licence, manifest, all 66 patterns, TSV reports, and histograms | Normal and air-gapped forensic analysis |
| Core ZIP only | Native CPU/Rust/CUDA/hybrid extraction, current patterns, native-only JSONL/TSV reports, and histograms | Small native-only installation or diagnostics |

Do not combine executables, manifests, packs, tools, or models from different
versions. The installer and bundle verifier intentionally reject mixed-version
installations.

## Install every feature

Requirements: Windows 11 x64, a connected staging machine, and at least
30 GiB free on the installation/cache volume. Administrator rights are not
required. DirectML OCR requires a compatible Windows GPU/driver stack; CPU OCR
and native CPU extraction remain available without a GPU.

Run the [safe pinned installer bootstrap from the v1.9.6
README](https://github.com/Donovoi/bstrings/blob/v1.9.6/README.md#get-started)
verbatim. Do not pipe a downloaded script into `Invoke-Expression`. The
bootstrap authenticates the exact GitHub release and installer digest before
launching the installer. The installer then downloads, resumes, assembles, and
strictly verifies the complete `bstrings-quality` directory.

Verify the completed bundle before use:

```powershell
.\bstrings-quality\bstrings.exe bundle verify
```

Run every v1.9.6 stage over one file:

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

The results directory must be new or empty. `--full` selects native extraction,
FLOSS executable recovery, PDF/OCR processing, language assessment, local
translation, all built-in patterns, and the report projection. The completed
result includes the authoritative JSONL evidence graph, `findings.tsv`,
`pattern-histogram.tsv`, `feature-histogram.tsv`, and
`pattern-histogram.html`, plus input, run, summary, and completion records.

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
`bstrings-v1.9.6` directory should be created, then run this exact-tag,
API-digest-verified download:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v1.9.6'
  $repo = 'Donovoi/bstrings'
  $archiveName = 'bstrings-win-x64.zip'
  $destination = Join-Path (Get-Location) 'bstrings-v1.9.6'
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
      $release.prerelease -or $asset.Count -ne 1 -or
      $asset[0].browser_download_url -CNE $expectedUrl -or
      ([string]$asset[0].digest) -CNotMatch '^sha256:[0-9a-f]{64}$') {
    throw 'The exact published core asset could not be authenticated.'
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
.\bstrings-v1.9.6\bstrings.exe analyze `
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
.\bstrings-v1.9.6\bstrings.exe `
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
