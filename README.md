# bstrings

`bstrings` finds and validates useful forensic strings. Depending on the
installed release channel it can also recover obfuscated strings with
[FLOSS](https://github.com/mandiant/flare-floss), identify files with
[Magika](https://github.com/google/magika), perform OCR, detect language, and
translate locally before applying the same pattern catalogue.

## Published Windows downloads

The two published Windows channels currently have different versions and
capabilities. There is not yet one supported download containing their combined
feature set.

| Channel | Published version | Included | Not included |
| --- | --- | --- | --- |
| Latest Windows x64 core | [v1.9.4](https://github.com/Donovoi/bstrings/releases/tag/v1.9.4) | Native CPU/Rust/CUDA/hybrid extraction, measured backend selection, 66 built-in patterns, expanded match provenance, TSV reports, and histograms | FLOSS, Magika, OCR runtimes/models, and local translation assets |
| Complete quality/offline kit | [v1.9.2](https://github.com/Donovoi/bstrings/releases/tag/v1.9.2) | Native extraction, FLOSS, Magika, OCR, language triage, local translation, runtimes, models, licences, and strict bundle verification | The v1.9.4 backend/reporting changes and patterns added after v1.9.2 |

Do not combine a core ZIP with manifests, packs, or tools from another version.
Use the [download and installation guide](https://github.com/Donovoi/bstrings/blob/master/docs/download-and-install.md) to
choose and verify the correct channel.

## Complete enrichment kit currently available

Requirements: Windows 11 x64, a connected staging machine, and at least
**30 GiB free**. Administrator rights are not required. Open PowerShell in the
directory where you want `bstrings-quality`, then run this explicitly v1.9.2,
checksum-verified installer bootstrap:

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

It creates and verifies `.\bstrings-quality`. Then run:

```powershell
.\bstrings-quality\bstrings.exe bundle verify
.\bstrings-quality\bstrings.exe analyze -d D:\evidence -o D:\results --full
```

`--full` enables native extraction, executable recovery, OCR, language
assessment, local translation, and all patterns present in that bundle. It does
not add the v1.9.4 reporting or pattern changes to a v1.9.2 installation.

For an air-gapped workstation, copy the whole verified `bstrings-quality`
directory and run `bundle verify` again before examining evidence.

## Latest core: native analysis and forensic reports

Download and verify `bstrings-win-x64.zip` from the
[v1.9.4 release](https://github.com/Donovoi/bstrings/releases/tag/v1.9.4), then
extract the whole archive into a new directory. A native-only integrated run is:

```powershell
.\bstrings.exe analyze -f D:\evidence\memory.raw `
  -o D:\results\memory-strings `
  --recover-executable-strings off --ocr off --translation off `
  --lr all --processor auto
```

This workflow writes the authoritative JSONL evidence graph plus
`findings.tsv`, `pattern-histogram.tsv`, `feature-histogram.tsv`, and
`pattern-histogram.html`. The legacy direct `-f`/`-d` interface remains
available when a flat strings or regex file is preferred.

Detailed guidance: [download and installation](https://github.com/Donovoi/bstrings/blob/master/docs/download-and-install.md),
[air-gapped deployment](docs/air-gapped-deployment.md),
[analysis and translation](docs/enrichment-pipeline.md), and
[outputs and provenance](docs/output-and-provenance.md). The forensic report
contract and its bulk_extractor/Timeline Explorer design evidence are recorded
in [forensic reporting](docs/forensic-reporting-2026-08.md).

The project remains under its upstream terms in [LICENSE.md](LICENSE.md), with
component attribution in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
