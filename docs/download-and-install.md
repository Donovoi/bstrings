# Download and install bstrings on Windows

## Choose the required feature set

The currently published channels are deliberately separate:

| Need | Use | Important limitation |
| --- | --- | --- |
| Current native scanner, CPU/GPU selection, all 66 current patterns, TSV findings, and histograms | [v1.9.4 Windows x64 core](https://github.com/Donovoi/bstrings/releases/tag/v1.9.4) | Does not include FLOSS, Magika, OCR, or translation assets |
| Complete offline FLOSS, Magika, OCR, language, and translation workflow | [v1.9.2 quality kit](https://github.com/Donovoi/bstrings/releases/tag/v1.9.2) | Does not include changes added in v1.9.4 |

No published package currently contains the union of those features. A full
v1.9.4 quality/offline release must pass its separate acceptance gates before
it can be advertised. Never combine the v1.9.4 core with v1.9.2 manifests,
packs, tools, or models; the installer and bundle verifier intentionally reject
mixed-version installations.

## Install the v1.9.4 Windows core

Requirements: Windows 11 x64. The ZIP is self-contained and requires neither
administrator rights nor a separately installed .NET runtime. CUDA extraction
requires a compatible NVIDIA GPU and driver; CPU extraction remains available
without a GPU.

Open PowerShell in the directory where the new `bstrings-v1.9.4` directory
should be created, then run this exact-tag, API-digest-verified download:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v1.9.4'
  $repo = 'Donovoi/bstrings'
  $archiveName = 'bstrings-win-x64.zip'
  $destination = Join-Path (Get-Location) 'bstrings-v1.9.4'
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

The published v1.9.4 archive SHA-256 is
`3545e4a922110fbfefde703ce7f57dd96cbceed6d17540d6328b20b5a7981e17`.
The release also provides the canonical
[`SHA256SUMS.txt`](https://github.com/Donovoi/bstrings/releases/download/v1.9.4/SHA256SUMS.txt).

Use the integrated native-only path when the filterable report set is wanted:

```powershell
.\bstrings-v1.9.4\bstrings.exe analyze `
  -f "C:\evidence\memory.raw" `
  -o "C:\results\memory-strings" `
  --recover-executable-strings off `
  --ocr off `
  --translation off `
  --lr all `
  --processor auto
```

The results directory must be new or empty. This command writes
`regex-matches.jsonl`, `findings.tsv`, `pattern-histogram.tsv`,
`feature-histogram.tsv`, and `pattern-histogram.html`, together with input,
run, summary, and completion records. `--processor auto` measures eligible
backends and selects an accelerator only when it projects a worthwhile win.

The legacy command below remains appropriate for a single flat output file,
but it does not create the integrated TSV/histogram report set:

```powershell
.\bstrings-v1.9.4\bstrings.exe `
  -f "C:\evidence\memory.raw" `
  --lr all --ro --off --trace `
  -o "C:\results\memory-hits.csv"
```

## Install the complete v1.9.2 quality kit

Requirements: Windows 11 x64, a connected staging machine, and at least
30 GiB free on the installation/cache volume. Administrator rights are not
required.

Run the [safe pinned installer bootstrap from the v1.9.2
README](https://github.com/Donovoi/bstrings/blob/v1.9.2/README.md#get-started)
verbatim. Do not pipe a downloaded script into `Invoke-Expression`. The
bootstrap authenticates the exact GitHub release and installer digest before
launching the installer. The installer then downloads, assembles, and verifies
the complete `bstrings-quality` directory.

Verify the completed bundle before use:

```powershell
.\bstrings-quality\bstrings.exe bundle verify
```

Run every stage available in that version:

```powershell
.\bstrings-quality\bstrings.exe analyze `
  -f "C:\evidence\memory.raw" `
  -o "C:\results\memory-full-v1.9.2" `
  --full
```

`--full` selects native extraction, executable recovery, OCR, language
assessment, local translation, and every built-in pattern in v1.9.2. It does
not install or activate v1.9.4 code. For an air-gapped examination, copy the
entire verified `bstrings-quality` directory and run `bundle verify` again on
the destination workstation.

## Current feature boundary

Use v1.9.4 core when current backend behavior, current patterns, or the
Timeline Explorer-friendly reports are required. Use v1.9.2 quality when a
single offline workflow with FLOSS, OCR, and local translation is required.
Until a full v1.9.4 kit is published, obtaining both feature sets requires two
separate installations and separate runs; their files must not be merged into
one installation.

See [air-gapped deployment](air-gapped-deployment.md),
[enrichment pipeline](enrichment-pipeline.md), and
[output and provenance](output-and-provenance.md) for operational details and
interpretation boundaries.
