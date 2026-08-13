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

Do not transfer a kit that fails verification.

Run a small test before transfer:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\test-evidence `
  -o D:\test-results `
  --full
```

Use synthetic or approved test evidence. Do not mix test output with case data.

## Transfer the kit

1. Close all bstrings processes.
2. Copy the complete `bstrings-kit` directory to approved media.
3. Eject the media safely.
4. Move the directory to the disconnected computer.
5. Keep the directory contents unchanged.

Do not transfer only selected engines, models, or runtime files. The manifest
describes one complete kit.

## Verify on the disconnected computer

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

Do not analyze evidence if verification fails.

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

Confirm exit code 0, two complete status files, and no `.incomplete` file. Keep
failed output for diagnosis.

The result directory can contain sensitive case data. Store and transfer it as
evidence.

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

Mount or carve a disk or memory image before you expect file-level FLOSS or OCR
coverage. Keep the carved files and tool logs with the case.

## Update an offline computer

1. Install and verify the new kit on a connected staging computer.
2. Transfer the complete new directory.
3. Verify it on the disconnected computer.
4. Keep the old verified kit until the new kit passes an approved test.
5. Remove the old kit through your evidence-lab change process.

Do not merge files from two kit versions.

## Acceptance checks

Before operational use, confirm these facts:

- The exact release tag is approved.
- The release is immutable.
- The installer and assets match release checksums.
- `bundle verify` succeeds after transfer.
- A synthetic analysis completes.
- The result has complete status records.
- No network access occurs during the offline analysis.
- Operators can preserve failed and cancelled output.

Record the installed version and bundle identity in the case notes.

## Security boundary

An old cache, filename, timestamp, or success report does not prove file
integrity. Current manifest size and SHA-256 checks authorize the bytes.

Do not modify, add, or remove files inside the installed kit. Any such change
causes verification to fail.

See [Download and install](download-and-install.md) and
[Output and provenance](output-and-provenance.md) for related procedures.
