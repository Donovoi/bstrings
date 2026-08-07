# Air-gapped deployment

The latest published complete Windows x64 quality kit is v1.9.2. It is prepared
on a connected staging machine, verified, then copied as a directory to the
disconnected workstation. During an examination, the user runs only the root
`bstrings.exe`: no package manager, Python command, model hub, service
installation, or network access is needed.
PowerShell is only the shell displaying the examples below; normal users do not
run a Python script or package-manager command.

The v1.9.4 release is a separate core-only channel with newer backend,
reporting, and pattern features but without the enrichment runtimes and models.
No complete v1.9.4 quality kit is currently published. See
[download and installation](https://github.com/Donovoi/bstrings/blob/master/docs/download-and-install.md) for the exact feature
boundary, and do not mix files or manifests between versions.

The conservative supported baseline is Windows 11 x64 24H2 or newer, following
Microsoft's [.NET supported-Windows table](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
CPU analysis needs no GPU. DirectML uses the host's D3D12/DXGI stack and a
compatible graphics driver; those operating-system components are not bundled.

## Install on a connected staging machine

On a connected staging machine, start in the directory where you want
`.\bstrings-quality`. The installer needs no administrator rights and requires
at least 30 GiB free on the volume holding its install and cache. It installs
the complete quality profile.

Use the [pinned, checksum-verified v1.9.2
bootstrap](https://github.com/Donovoi/bstrings/blob/v1.9.2/README.md#get-started)
for `Install-BstringsQuality.ps1`. The installer handles the downloads,
resumable cache, assembly, and final strict verification. A failed run keeps
verified cache data for the next attempt; a successful default run removes its
temporary cache.

```powershell
.\bstrings-quality\bstrings.exe bundle verify
.\bstrings-quality\bstrings.exe analyze -d D:\evidence\carved-files --full -o D:\results\case-01
```

Transfer the entire verified `bstrings-quality` directory to the disconnected
workstation, then run `bundle verify` there again before case work. “One
executable” means one user interface; keep the adjacent models, runtimes,
tools, licences, configuration, and manifests with it.

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
file. In the v1.9.2 quality kit, `analyze --full` runs native extraction,
executable recovery, OCR, language assessment, local translation, and every
built-in pattern published in that version. A requested stage fails instead of
being silently skipped. The command does not add v1.9.4 reports or patterns to
the older installation.

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

The installer adds the quality Hy-MT2-7B Q8_0 model and its canonical licence.
Exact URLs, lengths, hashes, revisions, runtime inventories, and
license inputs are frozen in `offline-components.lock.json` and
`ocr-components.lock.json` inside the bundle.

Nothing installs a service, driver, global runtime, or Python package.
llama.cpp is a private loopback child process with its offline guard. Offline
model variables are forced, the adapters reject non-loopback network use, and
the OCR worker performs no network request. Application-local payloads are not
system prerequisites.

## OCR hardware choices

The v1.9.2 OCR profile defines two runtime environments:

- a CPU-only ONNX Runtime environment, verified separately as a fallback; and
- the active DirectML ONNX Runtime environment, which exposes both DirectML and
  CPU execution providers. Normal CPU and hybrid requests use provider-specific
  sessions in this active environment.

CPU, DirectML, and DirectML+CPU hybrid paths passed per-path source-profile
smoke tests. Those checks show that each path can run; they do not establish
cross-provider equality or corpus-level OCR quality. CUDA OCR is not bundled or
claimed by this profile, even though the general CLI accepts
`--ocr-provider cuda` for future/custom profiles.

Three evidence types answer different questions:

- synthetic smoke demonstrates on fixed fixtures that the packaged paths run
  and recover expected text;
- the local v3 616-document CPU SROIE calibration provides bounded
  printed-receipt quality evidence for its frozen candidate; and
- release-specific DirectML acceptance demonstrates the packaged GPU path on
  the named hardware/driver.

The local CPU-only v3 calibration selected 616 of 626 training documents and
passed its frozen development-data gate. The exact metrics and limits are in
the [OCR benchmark record](ocr-benchmark-2026-08-05.md).

The separate 361-document one-shot test failed closed on one degenerate source
annotation before OCR or quality scoring. It was consumed and was not rerun,
so it does not establish independent acceptance. A later post-hoc diagnostic
audited all 361 rows, excluded eight exact train/test image overlaps, and
scored 353 rows, including repaired dataset row index 142 (zero-based). Every
backend met all 11 frozen numeric thresholds, and the aggregate and
per-document scored metrics matched. Evidence-record integrity did not, so the
diagnostic failed overall. It is not an acceptance or parity result. Raw
benchmark evidence remains in CI/internal evidence storage rather than GitHub
Releases; see the [OCR benchmark record](ocr-benchmark-2026-08-05.md) for the
public summary.

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

These are per-path packaging smokes, not cross-provider parity or SROIE quality
evidence.

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

- The internal manifest and release checksums prove byte consistency, not
  publisher identity. Authenticate the GitHub release through the
  organization's normal trusted process.
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
