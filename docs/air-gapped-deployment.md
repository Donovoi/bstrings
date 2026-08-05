# Air-gapped deployment

The complete Windows x64 kit built from current source is prepared on a
connected staging machine, verified, then copied as a directory to the
disconnected workstation. During an examination, the user runs only the root
`bstrings.exe`: no package manager, Python command, model hub, service
installation, or network access is needed.
PowerShell is only the shell displaying the examples below; normal users do not
run a Python script or package-manager command.

The conservative supported baseline is Windows 11 x64 24H2 or newer, following
Microsoft's [.NET supported-Windows table](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
CPU analysis needs no GPU. DirectML uses the host's D3D12/DXGI stack and a
compatible graphics driver; those operating-system components are not bundled.

| Download | Purpose |
| --- | --- |
| Core ZIP | Scanner, Rust engine, and `bundle acquire` client. Useful alone for direct/native extraction and pattern search. |
| Complete offline kit | The directory created by `bundle acquire`. It adds [Magika](https://github.com/google/magika), [FLOSS](https://github.com/mandiant/flare-floss), OCR, language detection, and local translation. |

Release assets are the ingredients; the assembled directory is what you
transfer offline. “One executable” means one user interface. Its adjacent
models and runtimes are still required and must remain beside it.

## Choose a translation profile

Every profile gets the same scanner, OCR, FLOSS, Magika, and reporting tools.
Only the local translation model changes.

| Profile | Exact model | Bytes | Selection guidance |
| --- | --- | ---: | --- |
| `quality` | Hy-MT2-7B Q8_0 | 7,981,928,896 | Default; best measured translation quality |
| `balanced` | Hy-MT2-1.8B Q8_0 | 1,908,528,192 | Smaller transfer and memory footprint |
| `compact` | Hy-MT2-1.8B Q4_K_M | 1,133,080,448 | Smallest and fastest of the three |

The quality model is the default because it scored 62.6786 WMT24++ chrF++ and
92.8310 forensic chrF++ in the final strict gate, retained 22/22 protected
identifiers, and produced 10/10 expected pattern matches with no false positive
or false negative. The smaller models remain useful operational choices. See
the [translation benchmark](translation-benchmark-2026-08-04.md) for the exact
corpus, pins, results, and limits.

## Acquire and assemble on a connected machine

The v1.9.0 complete-kit assets are not in the current public
[GitHub releases](https://github.com/Donovoi/bstrings/releases) yet. Until they
are published, maintainers can build them from current source using
[offline release maintenance](offline-release-maintenance.md). An examiner
should not combine an older core ZIP with current manifests.

A complete-kit release contains:

- `bstrings-win-x64.zip`, a small self-contained core that provides the
  acquisition command;
- one shared `bstrings-win-x64-offline-base.zip`, kept below GitHub's 2 GB
  per-file limit;
- profile-specific configuration, license, manifest, and trust-manifest files;
  and
- `SHA256SUMS.txt`.

The large model is not mirrored into a GitHub asset. Its exact immutable
official URL, byte count, and SHA-256 are in the profile trust manifest.

On the connected staging machine:

1. Download and extract `bstrings-win-x64.zip`.
2. Download exactly one `bundle-packs-<profile>.json` from the same tagged
   release.
3. Preserve that trust manifest through your approved publisher-verification
   procedure.
4. Run `bundle acquire` from the extracted core:

```powershell
.\bstrings.exe bundle acquire `
  --manifest C:\Downloads\bundle-packs-quality.json `
  --output C:\Tools\bstrings-quality

C:\Tools\bstrings-quality\bstrings.exe bundle verify
```

`bundle acquire` supports resumed HTTPS downloads. It streams each pack into a
bounded temporary file, rejects overlong data, verifies exact length and
SHA-256, caches only verified bytes, safely extracts the shared ZIP, adds the
profile files, and checks the finished strict manifest. The output directory
must be new; an interrupted run leaves verified cached packs available for a
retry.

The default cache is beside the trust manifest under
`bundle-pack-cache/<profile>`. To place it elsewhere:

```powershell
.\bstrings.exe bundle acquire `
  --manifest C:\Downloads\bundle-packs-balanced.json `
  --cache D:\bstrings-pack-cache\balanced `
  --output D:\Tools\bstrings-balanced
```

For an organization that acquires packs through another approved downloader,
place the exact verified bytes under the cache names `base.zip`,
`configuration.file`, `translation-license.file`, `airgap-manifest.file`, and
`translation-model.file`, then assemble without networking:

```powershell
.\bstrings.exe bundle assemble `
  --manifest D:\transfer\bundle-packs-compact.json `
  --cache D:\transfer\verified-cache `
  --output D:\Tools\bstrings-compact
```

`assemble` still verifies every cached pack before use. It is not a bypass for
the trust manifest or hashes.

## Transfer and verify offline

Apply the organization's malware scanning, approved-media, and chain-of-custody
procedure to the complete output directory. Copy the whole directory; do not
move `bstrings.exe` away from its adjacent `runtime`, `tools`, `models`,
`licenses`, configuration, and manifest files.

On the disconnected workstation:

```powershell
.\bstrings.exe bundle verify
.\bstrings.exe analyze -d D:\evidence\carved-files --full -o D:\results\case-01
```

`bundle verify` rejects a missing, extra, linked, resized, or SHA-256-mismatched
file. `analyze --full` runs native extraction, executable recovery, OCR,
language assessment, local translation, and every built-in pattern. A requested
stage fails instead of being silently skipped.

`--full` does not parse filesystems or carve embedded files from a raw disk or
memory image. Mount or carve an image with an appropriate forensic tool when
that coverage is required. The direct scanner can still search the raw bytes,
but FLOSS sees an executable only when the complete executable is supplied as a
file, and OCR sees only supported image/PDF files in the input inventory.

## What is bundled

The `windows-x64-offline-v2` base carries:

- the self-contained Windows x64 .NET application and native Rust scanner;
- isolated official CPython embeddable runtimes;
- [Magika](https://github.com/google/magika) and its app-local DirectML
  dependency;
- standalone [FLOSS](https://github.com/mandiant/flare-floss);
- a CPU [llama.cpp](https://github.com/ggml-org/llama.cpp) runtime built from
  lock-pinned source with network-fetched build inputs disabled;
- CPU and DirectML OCR runtimes using
  [RapidOCR](https://github.com/RapidAI/RapidOCR),
  [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR),
  [ONNX Runtime](https://github.com/microsoft/onnxruntime),
  [PDFium](https://pdfium.googlesource.com/pdfium/), Pillow, and OpenCV;
- the enrichment/OCR workers, offline guards, documentation, smoke fixtures,
  dependency inventories, notices, corresponding source where required, and
  licenses; and
- application-local Visual C++ runtime DLLs and a strict file manifest.

The selected Hy-MT2 model and its canonical license are added during profile
assembly. Exact URLs, lengths, hashes, revisions, runtime inventories, and
license inputs are frozen in `offline-components.lock.json` and
`ocr-components.lock.json` inside the bundle.

Nothing installs a service, driver, global runtime, or Python package.
llama.cpp is a private loopback child process with its offline guard. Offline
model variables are forced, the adapters reject non-loopback network use, and
the OCR worker performs no network request. Application-local payloads are not
system prerequisites.

## OCR hardware choices

The v1.9.0 OCR profile in current source contains two packaged runtimes. Its
complete-kit release assets are still pending:

- a CPU-only ONNX Runtime environment, verified separately as a fallback; and
- the active DirectML ONNX Runtime environment, which exposes both DirectML and
  CPU execution providers. Normal CPU and hybrid requests use provider-specific
  sessions in this active environment.

CPU, DirectML, and DirectML+CPU hybrid paths passed live inference tests. CUDA
OCR is not bundled or claimed by this profile, even though the general CLI
accepts `--ocr-provider cuda` for future/custom profiles.

Three evidence types answer different questions:

- synthetic smoke proves that the packaged paths run and recover fixed text;
- the historical v2 616-document SROIE calibration provides bounded
  printed-receipt quality evidence for its frozen candidate; and
- release-specific DirectML acceptance proves the packaged GPU path on the
  named hardware/driver.

The historical v2 SROIE calibration, bound to source commit
`23992fc75b624a3c6dab5bfbd0a4b52949133525`, passed that candidate's
NFC-casefold gate. Its separate 361-document one-shot attempt failed closed on
one degenerate source annotation before producing quality metrics. The attempt
was consumed and was not rerun; the immutable
[terminal result](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-terminal-v2-20260805-23992fc)
therefore does not establish independent acceptance. The current v3 adapter
still requires fresh calibration. See the
[OCR benchmark record](ocr-benchmark-2026-08-05.md).

Hybrid does not guarantee higher throughput, and another GPU-heavy process can
exhaust graphics memory or cause a DirectML device-loss error. Use
`--ocr-provider cpu` to avoid GPU execution, or schedule GPU work so OCR and
other large models do not compete. The integrated pipeline completes OCR
before translation.

More detail is in [OCR and document analysis](ocr-and-document-analysis.md).

## Acceptance checks

Run `bundle verify` after every transfer and before evidence work. An
administrator can additionally run the bundled real-inference smoke tests:

```powershell
.\bstrings.exe bundle verify
.\Verify-AirgapBundle.ps1 -TranslationSmoke -OcrSmoke
```

The OCR smoke creates synthetic image and image-only PDF fixtures, exercises
the configured active and alternate runtimes, requires exact recovery of fixed
email, URL, IP/CVE, and Windows-path lines, and verifies that the output remains
air-gapped. The translation smoke loads the exact selected GGUF and requires
complete output plus protected-identifier retention. Recovery smoke sends a
reviewed benign executable through Magika and FLOSS and requires its fixed
marker.

This PowerShell verifier is an administrator/release acceptance tool. The
normal examiner interface remains `bstrings.exe`.

CI runs these checks with offline flags and dead external proxies after
extracting the complete bundle. Generic hosted CI exercises CPU OCR only;
DirectML and hybrid release acceptance is a separate manual job on an explicitly
labeled self-hosted Windows runner, with an evidence artifact tied to the source
build run. Those are strong regression checks, but they are not the same as an
independently prepared clean VM with its virtual NIC disabled. Organizations
should accept their actual workstation image under their own controls.

## Integrity, authenticity, and operational boundaries

- The internal manifest and adjacent `SHA256SUMS.txt` prove byte consistency,
  not publisher identity. Independently authenticate the selected
  `bundle-packs-*.json`; it anchors the hashes used by acquisition.
- A driver is host and operating-system software. It cannot be made a truthful
  application-local dependency. CPU remains the no-GPU path.
- The standard complete profile's llama.cpp translation runtime is CPU-only.
  Translation CUDA/hybrid requires a separately reviewed runtime profile and
  compatible host driver; selecting the option does not manufacture that
  dependency.
- Endpoint security, WDAC, AppLocker, or organizational policy may block a
  bundled upstream executable even when its hash matches. Approve exact hashes
  through the local process.
- Model output, language detection, and OCR are probabilistic. Verify
  consequential leads against the source evidence and untranslated parents.
- Editing an executable, model, configuration, document, notice, or license
  invalidates the strict manifest. Rebuild rather than patching a bundle.

Do not describe the release as signed unless it actually has Authenticode/RFC
3161 signatures and an independently verified signed manifest. Microsoft
documents [Authenticode timestamping](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures),
and GitHub documents [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).

Maintainers should continue with [offline release maintenance](offline-release-maintenance.md).
