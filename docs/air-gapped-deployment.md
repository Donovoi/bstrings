# Air-gapped deployment

The bstrings Windows kit can run without internet access after installation.

Use one connected staging computer to install and verify the complete kit.
Then, transfer the complete `bstrings-kit` directory.

## Requirements

- Windows 11 x64 on the staging and analysis computers
- At least 30 GiB of free space during installation
- Approved transfer media

You do not need administrator rights.

## Install on the staging computer

Use the authenticated `Install-Bstrings.ps1` command from the immutable release
page. The installer creates `bstrings-kit` by default.

The current public v1.9.17 release uses historical names. Use the exact command
and paths on its release page.

Verify the staged kit:

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

A nonzero exit code means the staged kit failed verification.

Run a small test before transfer:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\test-evidence `
  -o D:\test-results `
  --full
```

The example uses `D:\test-evidence` and `D:\test-results` as placeholder paths.

## Transfer the kit

1. Close all bstrings processes.
2. Copy the complete `bstrings-kit` directory to transfer media.
3. Eject the media safely.
4. Move the directory to the disconnected computer.
5. Leave the directory contents unchanged.

The manifest describes one complete kit. A partial transfer fails verification.

## Verify on the disconnected computer

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

Exit code 0 confirms a verified kit. Any other exit code means verification
failed.

Verification checks the manifest, exact file set, file sizes, SHA-256 values,
configuration, licenses, runtimes, models, and tools.

## Analyze evidence

Use a new or empty results directory:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence\carved-files `
  -o D:\results\case-01 `
  --full
```

A complete run has exit code 0, two complete status files, and no `.incomplete`
file. Failed output remains available for diagnosis.

The result directory can contain source strings, decoded values, translations,
paths, hashes, and pattern matches.

## What the kit contains

The kit contains:

- `bstrings.exe`;
- native extraction support;
- FLOSS and its notices;
- OCR runtimes and models;
- the local translation runtime and model;
- optional CUDA runtime files for accepted hardware;
- pattern and report support;
- configuration and manifests; and
- licenses and third-party notices.

The user selects engines at run time. Engine selection does not change the kit
or its verification rules.

## Select engines offline

Use the complete preset:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence `
  -o D:\results\full `
  --full
```

Exclude engines with a comma-separated list:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence `
  -o D:\results\without-ocr `
  --full -e ocr
```

See the [Command reference](command-reference.md) for direct native, FLOSS, OCR,
decoding, and translation controls.

## OCR and translation hardware

Automatic mode tests supported backends before it processes evidence. It uses a
tested fallback when an automatic accelerator test fails.

An explicit unsupported hardware choice fails closed. The selected provider
does not change after evidence processing starts.

CPU remains the no-GPU path. See
[OCR and document analysis](ocr-and-document-analysis.md) for accepted OCR
hardware.

## Disk and memory images

Native extraction can read a raw image as a byte stream. FLOSS and OCR need
individual supported files.

FLOSS and OCR accept supported files, not files embedded inside raw disk or
memory image containers. Mounted or carved files can be passed separately.

## Update an offline computer

1. Install and verify the new kit on a connected staging computer.
2. Transfer the complete new directory.
3. Verify it on the disconnected computer.
4. The old and new kits remain separate during verification.
5. The old kit can be removed after the new kit passes verification.

Files from two kit versions do not form a valid verified kit.

## Technical acceptance signals

The release and offline workflow expose these signals:

- The installed version matches the immutable release tag.
- The release is immutable.
- The installer and assets match release checksums.
- `bundle verify` succeeds after transfer.
- A synthetic analysis completes.
- The result has complete status records.
- No network access occurs during the offline analysis.

## Security boundary

An old cache, filename, timestamp, or success report does not prove file
integrity. Current manifest size and SHA-256 checks authorize the bytes.

Any modified, added, or removed file inside the installed kit causes
verification to fail.

See [Download and install](download-and-install.md) and
[Output and provenance](output-and-provenance.md) for related procedures.
