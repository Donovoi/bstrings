# Offline release maintenance

This guide is for maintainers of the Windows x64 release. Examiners should use
the shorter [air-gapped deployment guide](air-gapped-deployment.md).

## Release outputs

A successful exact project-version tag is configured to publish three Windows
assets:

- `bstrings-win-x64.zip`: the self-contained core scanner;
- `bstrings-win-x64-offline-cpu.zip`: the complete CPU/Q4 archive; and
- `bstrings-win-x64-offline-cpu.zip.sha256`: the lowercase SHA-256 of the exact
  offline ZIP.

Manual workflow dispatches build and retain the same artifacts for review but
do not create an untagged GitHub release. The complete archive must remain
smaller than 2,000,000,000 bytes. That conservative project limit stays below
GitHub's strict 2 GiB per-release-asset limit, which is documented in
[About releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#about-releases).

## Pinned component lock

`tools/airgap/offline-components.lock.json` is the only component acquisition
plan used by the complete release builder. Its `windows-x64-cpu-q4` profile
pins, at minimum:

| Component | Pinned artifact |
| --- | --- |
| CPython | Official 3.14.6 Windows x64 embeddable ZIP |
| Magika | Official `cli/v1.1.0` Windows x64 CLI ZIP |
| FLOSS | Official v3.1.1 standalone Windows ZIP |
| llama.cpp | Source ZIP for tag `b10248`, commit `e8e06f78e253a98a739b8ae4c6b661b357249ce4` |
| Hy-MT2 | Revision `1cd5208700acedef4ef93019b6cfc148b8522d45`, Q4_K_M GGUF |

Each downloaded artifact and separately acquired license has a fixed URL, byte length,
and lowercase SHA-256. The builder rejects length or hash mismatches, missing
expected executables, unsafe archive paths, and absent license inputs. Redirects
cannot substitute different bytes without failing the locked length or digest.
A filename or version label is never sufficient.

The primary upstream locations are Python's
[embeddable package](https://docs.python.org/3/using/windows.html#the-embeddable-package),
the [Magika CLI](https://github.com/google/magika#command-line-tool),
[FLOSS releases](https://github.com/mandiant/flare-floss/releases),
[llama.cpp source repository](https://github.com/ggml-org/llama.cpp), and the
[Hy-MT2 GGUF repository](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF).

Review a lock change as executable supply-chain code. Confirm the official
source, redistribution terms, exact downloaded bytes, executable layout, and
third-party notices before committing it. Do not update a version and leave an
old digest or license pin behind.

## Build the core

The release uses the [.NET 10 LTS SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
and the repository's pinned Rust toolchain:

```powershell
cargo build --manifest-path native\bstrings_core\Cargo.toml --release --locked

$noticeSource = Join-Path $env:TEMP 'bstrings-release-notices'
dotnet restore bstrings.sln --runtime win-x64
.\tools\licenses\Stage-ManagedThirdPartyNotices.ps1 `
  -DestinationDirectory $noticeSource
.\tools\licenses\Verify-ManagedThirdPartyNotices.ps1 `
  -NoticeSourceDirectory $noticeSource

dotnet build bstrings.sln -c Release --no-restore
dotnet test bstrings.sln -c Release --no-build

dotnet publish bstrings\bstrings.csproj `
  -c Release `
  -f net10.0 `
  -r win-x64 `
  --self-contained true `
  -o publish\win-x64 `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:TreatWarningsAsErrors=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  "-p:ReleaseNoticeSourceDirectory=$noticeSource"

.\tools\licenses\Verify-ManagedThirdPartyNotices.ps1 `
  -NoticeSourceDirectory $noticeSource `
  -PublishedDirectory .\publish\win-x64

.\tools\airgap\Stage-VisualCppRuntime.ps1 `
  -DestinationDirectory .\publish\win-x64
```

The self-contained publish is why the user does not install .NET. Native
libraries may be extracted under `%TEMP%\.net` by the official single-file
host; Microsoft documents that behavior in
[single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#native-libraries).
`Stage-VisualCppRuntime.ps1` discovers or accepts the licensed x64
`Microsoft.VC*.CRT` redist directory, refuses `System32`, and atomically stages
and verifies the four lock-named DLLs beside `bstrings.exe`. Run it before the
core smoke and before creating `bstrings-win-x64.zip`.

The notice staging step takes the exact ILGPU files from its restored package,
the exact .NET runtime-pack files from `Microsoft.NETCore.App.Runtime.win-x64`
10.0.10, the Rust 1.95.0 standard-library attribution from the active pinned
sysroot, and the hash-pinned DeviceIOControlLib license. The verifier compares
the complete nine-package runtime graph, NuGet license metadata, nupkg hashes,
runtime-pack pin, and every staged/published notice byte with
`licenses/bstrings-managed-win-x64.json`. Both release ZIPs must pass this check.

## Validate and assemble the complete bundle

First validate PowerShell syntax and print the lock-resolved plan without
downloading or creating output:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\offline `
  -DryRun
```

Build on a connected staging host:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\offline `
  -WorkingDirectory D:\bstrings-release-staging
```

The output directory must not already exist. The builder downloads or reuses
only the exact lock-pinned inputs, validates their byte lengths and SHA-256
values, securely extracts archives, checks the required license material, and
then invokes the network-free directory assembler. Downloads are retained under
the working directory so a CI cache may speed later builds without weakening
hash validation. When a server supplies `Content-Length`, it must equal the
locked length; the streaming downloader also stops after at most the locked
length plus one sentinel byte, so an oversized response is rejected before it
can fill the staging disk.

### Magika and FLOSS redistribution closures

The complete builder stages and verifies two component-owned overlays before
calling the network-free assembler:

- `Stage-MagikaRedistribution.ps1` produces exactly 22 files and 49,497,911
  bytes. It supplies `tools/magika/magika.exe`, the required app-local
  `DirectML.dll`, a reviewed 70-package dependency inventory, notices, and the
  required MPL-covered source. The much larger Magika, ONNX Runtime, DirectML,
  and Rust source archives are exact hash-checked cache inputs, not duplicated
  in the examiner-facing archive.
- `Stage-FlossThirdPartyNotices.ps1` produces exactly 18 files and 2,045,571
  bytes. It records the embedded Python/PyInstaller runtime, 29 Python
  packages, 67 native entries, both Rust dependency closures, notices, and the
  required `tqdm` source archive.

Their machine-readable inventories are themselves byte- and hash-pinned by
`offline-components.lock.json`. Run the same fail-closed checks immediately
before packaging a completed bundle:

```powershell
.\tools\licenses\Verify-MagikaRedistribution.ps1 `
  -BundleDirectory .\publish\offline

.\tools\licenses\Verify-FlossThirdPartyNotices.ps1 `
  -FlossExecutable .\publish\offline\tools\floss\floss.exe `
  -StagedDirectory .\publish\offline
```

The release workflow performs these checks before ZIP creation, and
`Verify-AirgapBundle.ps1` repeats them for the completed/extracted payload.
See [Magika CLI redistribution](magika-cli-redistribution.md) and
[FLOSS standalone redistribution](floss-standalone-redistribution.md) before
changing either inventory or its generated notices.

The lock deliberately does not use llama.cpp's published Windows CPU ZIP:
that archive contains `libomp140.x86_64.dll` copied from Visual Studio's
`debug_nonredist` tree. Instead, `Build-LlamaCpuRuntime.ps1` builds the pinned
source commit with the installed Visual Studio x64 toolchain. It enables the
dynamic CPU backend variants but disables OpenMP, KleidiAI FetchContent, RPC,
the Web UI and prebuilt UI, OpenSSL/BoringSSL/LibreSSL, and subprocess/video
support. Only the `llama-server` target's reviewed PE closure is staged. The
builder rejects `libomp140*.dll`, any `debug_nonredist` path, an unexpected PE
import, a missing CPU backend, or a missing `--offline` option.

The staged runtime includes `llama-build-provenance.json`, recording the exact
source archive, tag, commit, CMake and compiler versions, flags, file hashes,
and recursive PE imports. Applicable upstream license and borrowed-code notice
sources are preserved byte-for-byte under `licenses/llama.cpp`. Visual Studio
and CMake are release-host requirements only; neither is needed on the offline
examiner workstation.

The bundle also needs the four x64 Visual C++ runtime DLL names fixed by the lock.
When `-VisualCppRuntimeDirectory` is omitted, the connected builder uses
`vswhere.exe` to select the newest Visual Studio installation with the latest
VC redistributable component, then selects an x64 `Microsoft.VC*.CRT` redist
directory containing every required DLL. It refuses `System32` as a source.
If auto-discovery is unavailable, pass an explicit licensed redist directory:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\offline `
  -WorkingDirectory D:\bstrings-release-staging `
  -VisualCppRuntimeDirectory 'C:\approved-redist\x64\Microsoft.VC14x.CRT'
```

The connected builder reuses `Stage-VisualCppRuntime.ps1`; the lower-level
assembler proves that the same bytes are staged beside the root
`bstrings.exe`, Python, Magika, FLOSS, and llama.cpp. It records each deployment
directory and each DLL's filename, version, length, and SHA-256 in
`airgap-config.json`. This makes the application runtime-local; it does not
expand Microsoft's redistribution rights. Build and publish only from an
appropriately licensed toolchain and retain the applicable terms.
Microsoft documents
[application-local deployment](https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170)
and the
[supported Visual C++ redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170).

The component lock freezes downloaded offline inputs. The published bstrings,
.NET, and Rust payload is release-built, while the Visual C++ payload is
selected from the licensed builder redist. The release records the selected VC
versions, lengths, and hashes in `airgap-config.json`; the finished manifest
then records and verifies the exact bytes of all of those release-selected
files. The four VC bytes are not themselves frozen in the component lock.

Use `-ComponentLockPath` only to test an intentional alternate lock. Use
`-ValidateOnly` when every expected file already exists in the working
directory's `downloads` folder. It disables download fallback, rechecks the
main component and license bytes, stages all 14 Magika redistribution inputs
with `-CacheOnly`, builds and verifies the exact Magika and FLOSS overlays in a
temporary directory, then removes that directory without creating bundle
output. `-KeepStaging` preserves extracted temporary inputs for maintainer
diagnostics during a real build; it is not part of the release path.

## Package and test the exact archive

Create the ZIP from the contents of the completed output directory so
`bstrings.exe` is at archive root. The assembler ships an explicit allowlist of
examiner/maintainer Markdown rather than the benchmark harness, and
`tools/airgap/Verify-MarkdownLinks.ps1` rejects any shipped relative link whose
target is absent or escapes the bundle. Then:

1. Fail if the ZIP length is greater than or equal to 2,000,000,000 bytes.
2. Write the SHA-256 for the exact ZIP bytes to the adjacent `.sha256` file.
3. Extract that ZIP into a new empty directory.
4. Run the extracted root `bstrings.exe bundle verify` for bundle integrity;
   do not substitute a separately installed verifier.
5. Run the extracted `Verify-AirgapBundle.ps1 -TranslationSmoke`. It invokes
   the root executable for the exact CPU translation fixture and a separate
   reviewed benign-PE Magika/FLOSS recovery fixture.
6. Require complete run and summary records, no incomplete marker, exactly one
   translated child per candidate, air-gap provenance, retained synthetic
   identifiers, the expected email match, and the exact attributable FLOSS
   decoded marker.

Testing the pre-compression directory is insufficient: the release gate must
exercise the archive that will actually be uploaded.

The workflow also runs Rust formatting, Clippy and tests; .NET restore, build,
tests and self-contained publish; Python unit tests, Ruff and `py_compile`; a
PowerShell parser pass; and a dry-run lock validation. Multi-gigabyte offline
acquisition, packaging, archive verification, and full smokes are limited to
version tags and manual dispatches, while ordinary pushes and pull requests
retain the faster core gates.

## Custom Q8 or GPU bundles

Q8 remains an optional local staging choice for environments where the single
GitHub-asset ceiling does not apply. It requires a separate reviewed component
lock whose translation-model filename, URL, revision, byte length, SHA-256,
and license fields describe the exact Q8 input. Do not edit or weaken the
published Q4 lock in place.

Use the lower-level `tools/airgap/Build-AirgapBundle.ps1` with already acquired,
independently verified component directories, the Q8 file, and that alternate
lock:

```powershell
.\tools\licenses\Stage-MagikaRedistribution.ps1 `
  -DestinationDirectory C:\staging\magika-redistribution `
  -DownloadCacheDirectory C:\staging\downloads

.\tools\licenses\Stage-FlossThirdPartyNotices.ps1 `
  -FlossExecutable C:\staging\floss-3.1.1\floss.exe `
  -DestinationDirectory C:\staging\floss-redistribution

.\tools\airgap\Stage-VisualCppRuntime.ps1 `
  -ComponentLockPath C:\staging\offline-components-q8.lock.json `
  -VisualCppRuntimeDirectory 'C:\approved-redist\x64\Microsoft.VC14x.CRT' `
  -DestinationDirectory @(
    'C:\staging\bstrings-publish',
    'C:\staging\python-embed-amd64',
    'C:\staging\magika',
    'C:\staging\floss-3.1.1',
    'C:\staging\llama-cpu'
  )

.\tools\airgap\Build-AirgapBundle.ps1 `
  -OutputDirectory E:\transfer\bstrings-airgap-q8 `
  -PublishedBstringsDirectory C:\staging\bstrings-publish `
  -PythonDirectory C:\staging\python-embed-amd64 `
  -MagikaDirectory C:\staging\magika `
  -MagikaRedistributionDirectory C:\staging\magika-redistribution `
  -FlossDirectory C:\staging\floss-3.1.1 `
  -FlossRedistributionDirectory C:\staging\floss-redistribution `
  -LlamaDirectory C:\staging\llama-cpu `
  -VisualCppRuntimeDirectory 'C:\approved-redist\x64\Microsoft.VC14x.CRT' `
  -TranslationModelDirectory C:\staging\hy-mt2-q8 `
  -TranslationModel Hy-MT2-1.8B-Q8_0.gguf `
  -TranslationModelRevision 1cd5208700acedef4ef93019b6cfc148b8522d45 `
  -ComponentLockPath C:\staging\offline-components-q8.lock.json
```

Without the alternate lock, the command fails because the default lock pins
Q4. The alternate lock's schema and profile must also be explicitly accepted by
the current builder; do not bypass that guard. This is not the standard release
profile. It needs its own manifest, transport hash, full CPU smoke, license
review, and archive/media procedure.

GPU translation additionally needs an approved llama.cpp GPU backend and
runtime files plus a compatible host driver. The driver is external system
software and cannot truthfully be described as bundled. Maintain CPU fallback
and test each GPU profile on the exact supported hardware image.

## Integrity, authenticity, and acceptance boundaries

The strict file manifest and adjacent SHA-256 checksum detect changes relative
to their recorded values. They do not establish publisher identity when the
manifest, archive, and checksum travel through the same trust channel.

Do not claim signed or authenticated releases until the workflow actually:

- Authenticode-signs and RFC 3161 timestamps the executable bytes;
- verifies a detached signed manifest against an independently trusted key;
- preserves that trust anchor outside the release payload; and
- publishes through a GitHub immutable-release policy.

Microsoft documents
[Authenticode timestamping](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures),
and GitHub documents
[immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).

Likewise, the hosted runner smoke uses the real extracted bundle and offline
application mode, but it is not evidence of a pristine disconnected Windows
installation. Before describing a release as clean-VM validated, record a pass
on a standard-user Windows 11 x64 24H2-or-newer VM with its virtual NIC disabled and without
separately installed .NET, Python, VC runtime, Git, Rust, or package managers.
Include paths with spaces and non-ASCII characters, read-only media,
insufficient disk, tampered/missing/extra files, and endpoint-control behavior
in that acceptance matrix.
