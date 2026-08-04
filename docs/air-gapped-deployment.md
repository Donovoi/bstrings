# Air-gapped deployment

The checked `bstrings-win-x64-offline-cpu.zip` is the normal deployment for
a disconnected Windows x64 workstation. It carries every application
dependency needed by the CPU workflow. The examiner extracts one archive and
uses the root `bstrings.exe`; they do not install or invoke Python, .NET,
Magika, FLOSS, llama.cpp, a model hub, or a package manager.

The conservative supported baseline for this release profile is Windows 11
x64 24H2 or newer, following Microsoft's [.NET supported-Windows
table](https://learn.microsoft.com/en-us/dotnet/core/install/windows).
Magika carries DirectML 1.15.4 application-local, but that runtime still uses
the operating system's D3D12/DXGI graphics interfaces; Microsoft's [DirectML
version history](https://learn.microsoft.com/en-us/windows/ai/directml/dml-version-history)
does not make an older or unsupported Windows image a supported target.

## Deploy the published CPU bundle

On a connected transfer workstation:

1. Until a matching version tag publishes the archive, run the [Windows build
   workflow](https://github.com/Donovoi/bstrings/actions/workflows/dotnet-desktop.yml)
   manually and download the `bstrings-win-x64-offline-cpu` artifact, which
   contains the ZIP and adjacent `.sha256`. A future matching tag publishes the
   same checked files on [bstrings releases](https://github.com/Donovoi/bstrings/releases).
2. Apply the organization's approved download, malware-scanning, media, and
   chain-of-custody procedure.
3. Copy both files to the approved transfer media. Do not modify the archive.

Inside the disconnected environment, extract the complete archive into a new
directory. Do not move `bstrings.exe` away from its adjacent `runtime`, `tools`,
`models`, `licenses`, configuration, and manifest files.

From that directory, use only the bundled executable:

```powershell
.\bstrings.exe bundle verify
.\bstrings.exe analyze -d D:\evidence\carved-files --full -o D:\results\evidence
```

`bundle verify` rejects a missing, extra, linked, resized, or SHA-256-mismatched
file before analysis. `analyze --full` then coordinates extraction, executable
string recovery, language assessment, local CPU translation, pattern matching,
and final reporting. A requested stage fails rather than being silently
omitted.

`--full` means every bstrings stage; it does not mean full forensic parsing of
a disk or memory image. bstrings does not mount filesystems or carve embedded
PEs. Mount or carve a raw image with an appropriate forensic tool before using
the directory workflow above. A raw image can instead be passed to the direct
extract/search interface for byte strings and patterns, but FLOSS receives a
PE only when that complete PE is supplied as a file.

The external `.sha256` is useful for detecting transfer corruption when it is
compared through an independently trusted procedure. Neither that checksum nor
the manifest inside the same archive authenticates the publisher by itself: an
attacker able to replace both data and checksums can make them agree. Release
signing and independently anchored provenance are described under
[hardening boundaries](#release-hardening-boundaries).

## What is bundled

The release profile is `windows-x64-cpu-q4` and includes:

- the self-contained Windows x64 .NET application and native Rust scanner;
- the isolated official CPython embeddable runtime;
- the standalone Windows Magika tool with its application-local DirectML
  runtime, and the standalone FLOSS tool;
- a Windows x64 CPU llama.cpp runtime built from the lock-pinned source commit
  with OpenMP and network-fetched build inputs disabled;
- pinned Hy-MT2-1.8B Q4_K_M GGUF weights;
- the four required x64 Visual C++ runtime DLL names, deployed application-local
  beside the root scanner and each bundled native CLI from a licensed Visual
  Studio redistributable directory;
- the enrichment adapter, offline guards, smoke evidence, documentation,
  dependency inventories, notices, required corresponding source, and
  licenses; and
- a strict manifest governing the exact allowed regular-file set.

Downloaded offline components, their source URLs, byte lengths, SHA-256 values,
executable paths, model revision, and license inputs are frozen in
`tools/airgap/offline-components.lock.json`. Published bstrings/.NET/Rust bytes
and the licensed release-selected Visual C++ runtime bytes are instead captured
by `airgap-config.json` and the finished strict manifest. The release builder
and archive verification reject any mismatch from those recorded bytes.

The bundle does not install services, drivers, Python packages, or global
runtimes. llama.cpp is started only as a private loopback child process with
its `--offline` guard. Offline model variables are forced and the adapter
rejects non-loopback sockets. `runtime/llama/llama-build-provenance.json`
records its pinned source, compiler, build flags, runtime hashes, and PE import
closure; the corresponding byte-exact notices are under `licenses/llama.cpp`.
Magika's 70-package runtime closure, app-local DirectML dependency, notices,
and required source are governed by
`licenses/magika-cli-1.1.0-redistribution.json`. FLOSS's embedded Python,
PyInstaller, native-runtime, Python-package, and Rust dependency closures are
governed by `licenses/floss-v3.1.1-win-x64.json`. The complete-bundle verifier
invokes both component verifiers, so a missing dependency, notice, source file,
or unexpected component-owned file fails before analysis.

These files are private application-local payloads, not prerequisites. The
examiner does not install DirectML, ONNX Runtime, Python packages, FLOSS, or
Magika separately. Detailed redistribution records are in
[Magika CLI redistribution](magika-cli-redistribution.md) and
[FLOSS standalone redistribution](floss-standalone-redistribution.md).

## Why the release carries Q4

GitHub requires every individual release asset to be smaller than 2 GiB.
[GitHub documents that release limit](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#about-releases).
The Q8 model alone is 1,908,528,192 bytes; after adding the application,
portable runtimes, FLOSS, Magika, llama.cpp, notices, and ZIP overhead, a
complete Q8 archive cannot fit below the release ceiling. Q4_K_M is
1,133,080,448 bytes and leaves enough room for the complete CPU toolchain.

The bounded translation gate measured Q4 at 1.8554 strings/s and Q8 at 1.2885
strings/s on the reviewed CUDA laptop. Both preserved 22/22 forensic
identifiers; Q4 scored 58.4248 versus 59.7396 WMT chrF++, and 86.3056 versus
87.5297 forensic chrF++. Those results support Q4 as the downloadable default,
not as a universal quality ranking. See the
[translation benchmark](translation-benchmark-2026-08-04.md) for the corpus,
hardware, pins, and limitations.

A maintainer may still produce a local Q8 bundle for approved media where the
GitHub per-asset limit does not apply. That is a separate custom build and must
be fully rehashed and re-tested; replacing the model inside a published bundle
invalidates its manifest.

## Administrator acceptance

Run `.\bstrings.exe bundle verify` after every transfer and before evidence work.
For stronger administrator acceptance, run the bundled verifier's complete
smoke on each target workstation image after the root executable passes:

```powershell
.\bstrings.exe bundle verify
.\Verify-AirgapBundle.ps1 -TranslationSmoke
```

The verifier runs two real `bstrings.exe analyze` examinations using only
manifest-covered synthetic inputs. The translation path loads the exact CPU
GGUF, requires complete output and air-gap provenance, and retains and matches
`analyst@example.com`. The recovery path sends a reviewed benign PE through
Magika and FLOSS, then requires the exact attributable decoded marker. It
removes its temporary results after either success or failure.

This PowerShell verifier is an administrator acceptance tool, not the normal
examiner interface. Evidence work still calls only `bstrings.exe`.

Release CI performs both complete smokes after extracting the finished ZIP,
with offline environment flags and dead external proxies. That is valuable
regression coverage, but it is not equivalent to an independently prepared
clean VM with its virtual NIC disabled. Perform the disconnected-machine test
under the organization's acceptance procedure before approving a workstation
image.

## Operational boundaries

- No GPU is needed by the published CPU bundle. GPU acceleration requires a
  separately prepared runtime profile plus a compatible host driver. A display
  or compute driver is hardware and operating-system software and is not
  bundled by bstrings.
- The CPU profile avoids CUDA redistributables and their additional driver and
  licensing constraints.
- The app-local DirectML runtime removes a separate DirectML package install;
  Windows D3D12/DXGI remain operating-system components.
- Endpoint security, WDAC, AppLocker, or organizational policy may block a
  bundled upstream executable even when its hash matches. Approve the recorded
  file hashes through the local control process.
- Model and language detection remain probabilistic. Consequential translated
  matches must be checked against the original parent string and surrounding
  evidence.
- Editing any executable, model, configuration, documentation, notice, or
  license invalidates the strict manifest. Rebuild the bundle instead of
  patching it in place.

## Maintainers and custom bundles

Connected acquisition, the pinned component lock, release automation, custom
Q8 staging, archive-size enforcement, and the current signing/clean-VM
boundaries are documented in
[offline release maintenance](offline-release-maintenance.md). The normal
examiner does not need those tools or instructions.

Primary upstream references: the official Python
[embeddable-package documentation](https://docs.python.org/3/using/windows.html#the-embeddable-package),
[Magika CLI](https://github.com/google/magika#command-line-tool), standalone
[FLOSS releases](https://github.com/mandiant/flare-floss/releases),
[llama.cpp source repository](https://github.com/ggml-org/llama.cpp), and the
[Hy-MT2 GGUF repository](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF).

## Release hardening boundaries

The current checksum and manifest provide exact-byte integrity checks. Do not
describe them as code signing or publisher authentication unless the release
actually adds and verifies those controls.

Before making that stronger claim, Authenticode-sign and RFC 3161 timestamp the
published executables, verify a detached signed manifest against an
independently trusted signer, and publish through an immutable release process.
Microsoft documents
[Authenticode timestamping](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures),
and GitHub documents
[immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).

Likewise, CI archive extraction and full-path smoke testing do not establish
that every supported clean Windows image is dependency-free. A pristine,
standard-user Windows x64 VM with no separately installed .NET, Python, VC
runtime, package manager, or development tool remains the acceptance boundary
until that matrix has been run and recorded.
