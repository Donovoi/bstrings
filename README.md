# bstrings

`bstrings` finds useful text in files, disk images, and memory images.

It can recover hidden strings, read text in images, and translate text locally.
It also finds forensic patterns such as email addresses, keys, tokens, hashes,
payment data, and personal information.

## What bstrings does

- Finds text with native extraction, FLOSS, and OCR.
- Decodes strict Base64 text when you select this function.
- Detects languages and translates text on your computer.
- Finds and validates 77 forensic pattern types.
- Records the source and location of each result.
- Creates JSONL evidence, TSV reports, histograms, and an HTML report.
- Works offline after you install the kit.

There is one Windows kit. It contains all supported engines, models, runtimes,
licenses, and verification data. There is no second kit to select.

Pattern matches are candidates. A match does not prove identity, ownership,
compromise, or malicious activity.

## Requirements

- Windows 11 x64
- At least 30 GiB of free space
- Internet access on the computer that installs the kit

You do not need administrator rights.

## Install

Version 2.1.0 uses `Install-Bstrings.ps1`. It installs the complete kit in
`bstrings-kit` by default.

Open PowerShell in the directory that will contain `bstrings-kit`. Run this
command after the v2.1.0 release is published:

```powershell
& {
  Set-StrictMode -Version Latest
  $ErrorActionPreference = 'Stop'

  $tag = 'v2.1.0'
  $repo = 'Donovoi/bstrings'
  $name = 'Install-Bstrings.ps1'
  $headers = @{
    Accept = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent' = 'bstrings-installer-bootstrap'
  }
  $release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/tags/$tag" `
    -Headers $headers -UseBasicParsing
  $asset = @($release.assets | Where-Object { $_.name -CEQ $name })
  $url = "https://github.com/$repo/releases/download/$tag/$name"
  if ($release.tag_name -CNE $tag -or $release.draft -or $release.prerelease -or
      -not ($release.PSObject.Properties.Name -ccontains 'immutable') -or
      -not [bool]$release.immutable -or $asset.Count -ne 1 -or
      $asset[0].browser_download_url -CNE $url -or
      ([string]$asset[0].digest) -CNotMatch '^sha256:[0-9a-f]{64}$') {
    throw 'The exact immutable installer asset could not be authenticated.'
  }
  $installerPath = [IO.Path]::GetFullPath((Join-Path (Get-Location).Path $name))
  $parent = [IO.Path]::GetDirectoryName($installerPath)
  $downloadPath = Join-Path $parent `
    ('.Install-Bstrings.download-' + [Guid]::NewGuid().ToString('N') + '.partial')
  $backupPath = Join-Path $parent `
    ('.Install-Bstrings.backup-' + [Guid]::NewGuid().ToString('N') + '.tmp')
  $expected = ([string]$asset[0].digest).Substring(7)
  try {
    Invoke-WebRequest $url -OutFile $downloadPath -UseBasicParsing
    $stream = [IO.File]::OpenRead($downloadPath)
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

The command checks the immutable release and the installer SHA-256 before it
runs the installer.

Version 1.9.17 remains available with its historical names. Use the exact steps
in the [v1.9.17 README](https://github.com/Donovoi/bstrings/blob/v1.9.17/README.md#get-started).

See [Download and install](docs/download-and-install.md) for the full install,
upgrade, cache, and rollback procedure.

## Verify

Verify the kit before you analyze evidence:

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

Do not analyze evidence if this command returns a nonzero exit code.

## Run an analysis

Use a new or empty output directory for a new analysis:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence `
  -o D:\results\case-01 `
  --full
```

Replace the input and output paths with your paths.

A complete run has all these results:

- The command returns exit code 0.
- `run.json` has `status: complete`.
- `summary.json` has `status: complete`.
- The output directory does not contain `.incomplete`.

Keep incomplete output for diagnosis. You can resume a supported run after an
interruption.

The output can contain sensitive case data. Protect the complete output
directory.

## Resume an analysis

Use the same output directory with `-r`:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -r `
  -o D:\results\case-01
```

`--resume` is the long form of `-r`. Resume uses the saved input and settings.
It verifies the input, kit, checkpoints, and completed stages before it reuses
them. It refuses changed or damaged state and concurrent use.

Resume keeps files from the unfinished stage in its diagnostic log. It starts
that stage again. An interrupted translation stage starts again from the saved
list of translation candidates.

The final completion checks do not change. Do not use incomplete output as
completed evidence.

## Select engines

`--full` selects the normal complete analysis preset. You can exclude one or
more engines with `-e`:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence `
  -o D:\results\no-ocr-or-translation `
  --full -e ocr,translation
```

You can also select source engines directly.

Native extraction only:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -f D:\evidence\memory.raw `
  -o D:\results\native `
  --native-extraction on `
  --recover-executable-strings off `
  --ocr off --translation off
```

FLOSS only:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence\executables `
  -o D:\results\floss `
  --native-extraction off `
  --recover-executable-strings force `
  --ocr off --translation off
```

OCR only:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence\documents `
  -o D:\results\ocr `
  --native-extraction off `
  --recover-executable-strings off `
  --ocr force --translation off
```

Native extraction with Base64 decoding:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -f D:\evidence\memory.raw `
  -o D:\results\decoded `
  --decode auto --translation off
```

Decoding and translation need records from native extraction, FLOSS, or OCR.
You cannot disable all three source engines.

## Find patterns

The default analysis uses all built-in patterns. Use `-p` to list them:

```powershell
.\bstrings-kit\bstrings.exe -p
```

Use `--lr` to select names or groups:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -f D:\evidence\memory.raw `
  -o D:\results\focused `
  --lr "pii,credentials,browser,registry,wallets"
```

## Get help

```powershell
.\bstrings-kit\bstrings.exe help
.\bstrings-kit\bstrings.exe help analyze
.\bstrings-kit\bstrings.exe help bundle verify
```

Read these guides for more information:

- [Command reference](docs/command-reference.md)
- [Download and install](docs/download-and-install.md)
- [Air-gapped deployment](docs/air-gapped-deployment.md)
- [Analysis stages](docs/enrichment-pipeline.md)
- [Output and provenance](docs/output-and-provenance.md)
- [OCR and document analysis](docs/ocr-and-document-analysis.md)

Disk and memory images can contain files that FLOSS and OCR cannot read
directly. Mount or carve these images before you use file-level analysis.

The project uses [LICENSE.md](LICENSE.md). Component notices are in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
