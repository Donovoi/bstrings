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

The next release uses `Install-Bstrings.ps1`. It installs the complete kit in
`bstrings-kit` by default.

Use the authenticated install command from the release page. The command checks
the immutable release and the installer digest before it starts the installer.

The current public release is
[v1.9.17](https://github.com/Donovoi/bstrings/releases/tag/v1.9.17). That release
is immutable and uses its historical installer and directory names. Use the
exact steps in the [v1.9.17 README](https://github.com/Donovoi/bstrings/blob/v1.9.17/README.md#get-started).
The next major release removes those old names.

See [Download and install](docs/download-and-install.md) for the full install,
upgrade, cache, and rollback procedure.

## Verify

Verify the kit before you analyze evidence:

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

Do not analyze evidence if this command returns a nonzero exit code.

## Run an analysis

Use a new or empty output directory:

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

Keep incomplete output for diagnosis. Use a different new or empty directory
for the next attempt.

The output can contain sensitive case data. Protect the complete output
directory.

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
