# Offline release maintenance

This guide is for maintainers of the Windows x64 release. Examiners should use
[download and installation](download-and-install.md) and
[air-gapped deployment](air-gapped-deployment.md); they do not need the build
tools, Python commands, or dependency details below.

Current publication status: v1.9.12 is the fully gated Windows x64
quality/offline release. Its automatic core artifact and complete asset set are
bound to the same tag and commit. Do not combine them with another version.

## v1.9.12 release process and asset set

The exact v1.9.12 project-version tag publishes the asset set below after every
required gate passes. Do not combine a core ZIP with manifests from another
version.

- `Install-BstringsQuality.ps1`, the quality-only, connected-stage installer;
- `bstrings-win-x64.zip`, the self-contained core scanner and split-pack
  acquisition client;
- `bstrings-win-x64-offline-base.zip`, the shared application/runtime/OCR base;
- `airgap-config-quality.json`;
- `airgap-manifest-quality.json`;
- `Hy-MT2-Apache-2.0-quality.txt`;
- `bundle-packs-quality.json`;
- `SHA256SUMS.txt`.

The tag- and commit-bound `offline-profile-acceptance.json` remains an internal
Actions gate artifact. The release job validates it, but does not publish it as
a user download.

The workflow uses [`releases/v1.9.12.md`](releases/v1.9.12.md) as the human
release body. Review it against the final filenames, profile identities, and
known boundaries before tagging.

| Item | Release asset? | Role |
| --- | --- | --- |
| Quality installer | Yes | Safely acquires and verifies the complete quality profile in one operation |
| Core ZIP | Yes | Scanner and acquisition client |
| Shared base ZIP | Yes | Common runtimes, OCR, recovery tools, and manifests |
| Quality files/trust manifest | Yes | Select and authenticate the one translation model |
| Profile acceptance evidence | No; internal Actions artifact | Blocks publication unless the quality profile passes |
| Immutable translation model | No; acquired from its pinned official source | Large external pack named by the trust manifest |
| Complete offline kit | No; assembled locally | Directory transferred to the disconnected host |

## Quality installer release contract

`Scripts/Install-BstringsQuality.ps1` is published unchanged as
`Install-BstringsQuality.ps1`. `SHA256SUMS.txt` must contain exactly one
lowercase SHA-256 row for it alongside every other public release asset. The
README bootstrap uses GitHub's exact-tag API for `v1.9.12`, rejects a draft or
prerelease, downloads the installer to a unique physical temporary file, and
verifies its API digest before replacing an existing physical installer and
launching `powershell.exe -File`. Failed authentication preserves the previous
installer. The installer independently authenticates `SHA256SUMS.txt`; the
release body links users to that canonical flow. Never document or offer a web
response piped into `Invoke-Expression`.

The installer is deliberately narrow:

- default release tag: `v1.9.12`;
- default destination: `.\bstrings-quality` under the caller's current
  directory;
- quality profile only;
- at least 30 GiB free on the install/cache volume;
- no administrator requirement;
- exact GitHub release metadata and canonical asset URLs;
- checksum verification for the installer, core ZIP, and quality trust
  manifest, followed by the existing strict pack and final-bundle verification;
- unconditional destination refresh through an authenticated sibling staging
  directory, with the complete previous physical directory retained until the
  replacement passes installed-path verification;
- rollback to the previous directory if publication or final verification
  fails, without ever merging old and new release files;
- visible overall installer progress plus measured pack download, hashing,
  assembly, and final-verification percentages;
- three acquisition attempts by default; and
- deletion of its script-owned versioned cache only after success, with failed
  cache state retained for a resumable retry.

Supported controls are `-ReleaseTag`, `-DestinationDirectory`,
`-InstallerCacheDirectory`, `-KeepCache`, and `-AcquireAttempts`. Do not weaken
the exact-tag, checksum, path-containment, free-space, or final-verification
checks for convenience. `-KeepCache` retains the default script-owned cache;
an explicitly supplied cache directory is user-owned and is always retained.

The shared base must remain smaller than 2,000,000,000 bytes. This conservative
project ceiling stays below GitHub's strict 2 GiB per-release-file limit,
documented in [About releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases#about-releases).

The translation model is an immutable external file pack. Its trust manifest
records the official HTTPS URL, exact byte length, SHA-256, target
path, profile configuration, canonical license, shared-base identity, and final
air-gap manifest. The integrated `bstrings.exe bundle acquire` command resumes,
verifies, caches, assembles, and verifies these parts. This keeps every GitHub
asset within the limit without asking examiners to manipulate files manually.

Manual workflow dispatches retain the same generated files as a workflow
artifact for review. They do not create an untagged GitHub release, so their
generated tagged-release URLs are not a public acquisition channel.

GitHub Releases are reserved for the usable application, acquisition and
installation inputs, licenses, checksums, manifests, and the bounded
release-verification record listed above. Do not attach benchmark datasets,
raw OCR/translation output, one-shot witnesses or ledgers, logs, host details,
or experimental reports. Keep those in access-controlled workflow artifacts or
internal evidence storage. Repository and release documentation may carry only
a concise, qualified summary.

## Pinned acquisition plans

`tools/airgap/offline-components.lock.json` is the complete release acquisition
plan. It uses profile `windows-x64-offline-v2`, defaults to `quality`, and pins:

| Component | Reviewed input |
| --- | --- |
| CPython | Official 3.14.6 Windows x64 embeddable ZIP |
| Magika | Official [`cli/v1.1.0`](https://github.com/google/magika/releases/tag/cli%2Fv1.1.0) Windows x64 CLI ZIP |
| FLOSS | Official [v3.1.1](https://github.com/mandiant/flare-floss/releases/tag/v3.1.1) standalone Windows ZIP |
| llama.cpp | Source ZIP for tag `b10248`, commit `e8e06f78e253a98a739b8ae4c6b661b357249ce4` |
| Quality translation | Hy-MT2-7B Q8_0, revision `707464294cf5b2a5a69982855020858ed58cf1d1` |

Exact model identities are:

| Profile | Filename | Bytes | SHA-256 |
| --- | --- | ---: | --- |
| `quality` | `HY-MT2-7B-Q8_0.gguf` | 7,981,928,896 | `58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0` |

Filename case matters: the official 7B repository uses uppercase
`HY-MT2-7B-Q8_0.gguf`. The lock uses immutable model revisions and a separately
verified immutable 7B license revision.

`tools/airgap/ocr-components.lock.json` independently pins profile
`windows-x64-ocr-cpu-directml-v1`: two CPython runtimes, 26 exact packages,
CPU and DirectML ONNX Runtime sets, immutable PP-OCRv6
[detector](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx/tree/61323801669c338b7891481ec7bac61ce31b576a)/[recognizer](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/tree/50c7eacafc52fa7bcf4194e8cd08e46f8558504b)
files,
orientation classifier, 18,708-entry dictionary, and all license inputs. Its
model-pack manifest SHA-256 is
`b3b683eb29ec09e9da835e09fb4470792af7702cfc6cee40f2725c232d258534`.
The classifier is extracted from the immutable
[RapidOCR 3.9.2](https://github.com/RapidAI/RapidOCR/releases/tag/v3.9.2)
wheel; an
independent immutable upstream location is retained as provenance rather than
used as an unverified fallback. The composite revision's `cls-390c78...` value
is an internal component identity token, not the upstream source revision. The
traceable source identities are RapidOCR tag commit
`095232a4c94f7f0e6600ba5bba1177010ad696d4`, wheel SHA-256
`04d6b8d151f823d930bd91910555f57bea897c0c44fa6794267b94cf9c1ef9a0`,
classifier SHA-256
`e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c`,
and independent mirror revision `7a679896b7722a2e346fd2dd3148b3faaabe5790`.

Every remote file has an HTTPS URL, positive bounded byte length, lowercase
SHA-256, safe leaf name, and exact license/source relationship. Builders reject
redirect substitution, overlong or short responses, digest mismatch, unsafe
archive paths, reparse points, absent notices, unexpected files, and runtime
inventory drift.

Review a lock change as executable supply-chain code. Verify the official
source, immutable revision, exact downloaded bytes, redistribution terms,
runtime imports, dependency closure, and notices before committing it. Never
change a version/filename without its size, digest, provenance, and license.

Primary upstreams are the [CPython embeddable package](https://docs.python.org/3/using/windows.html#the-embeddable-package),
[Magika](https://github.com/google/magika),
[FLOSS](https://github.com/mandiant/flare-floss),
[llama.cpp](https://github.com/ggml-org/llama.cpp),
[Hy-MT2 7B](https://huggingface.co/tencent/Hy-MT2-7B-GGUF),
[RapidOCR](https://github.com/RapidAI/RapidOCR),
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR), and
[ONNX Runtime](https://github.com/microsoft/onnxruntime).

## Build and test the self-contained core

The v1.9.12 release process uses
[.NET 10 LTS](https://dotnet.microsoft.com/download/dotnet/10.0) and the
repository-pinned Rust toolchain:

```powershell
cargo fmt --manifest-path native\bstrings_core\Cargo.toml -- --check
cargo clippy --manifest-path native\bstrings_core\Cargo.toml --all-targets -- -D warnings
cargo test --manifest-path native\bstrings_core\Cargo.toml --locked
cargo build --manifest-path native\bstrings_core\Cargo.toml --release --locked

$noticeCache = Join-Path $env:TEMP 'bstrings-release-notice-cache'
$noticeSource = Join-Path $env:TEMP ("bstrings-release-notices-" + [Guid]::NewGuid().ToString('N'))
dotnet restore bstrings.sln --runtime win-x64
.\tools\licenses\Stage-ManagedThirdPartyNotices.ps1 `
  -DestinationDirectory $noticeSource `
  -DownloadCacheDirectory $noticeCache
.\tools\licenses\Verify-ManagedThirdPartyNotices.ps1 `
  -NoticeSourceDirectory $noticeSource

dotnet build bstrings.sln -c Release --no-restore
dotnet test bstrings.sln -c Release --no-build

dotnet publish bstrings\bstrings.csproj `
  -c Release -f net10.0 -r win-x64 --self-contained true `
  -o publish\win-x64 `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:TreatWarningsAsErrors=true `
  -p:DebugType=None -p:DebugSymbols=false `
  "-p:ReleaseNoticeSourceDirectory=$noticeSource"

.\tools\licenses\Verify-ManagedThirdPartyNotices.ps1 `
  -NoticeSourceDirectory $noticeSource `
  -PublishedDirectory .\publish\win-x64

.\tools\airgap\Stage-VisualCppRuntime.ps1 `
  -DestinationDirectory .\publish\win-x64
```

The self-contained publish removes the examiner's .NET prerequisite. Native
libraries may still be extracted under `%TEMP%\.net` by the official
single-file host; Microsoft documents that in
[single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview#native-libraries).

The managed notice verifier compares the exact .NET runtime pack, NuGet graph,
Rust standard-library attribution, DeviceIOControlLib license, and published
notice bytes against `licenses/bstrings-managed-win-x64.json`.

## Validate the connected build plan

First parse the scripts and print the lock-resolved plan without downloads or
bundle output:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\offline-quality `
  -TranslationProfile quality `
  -DryRun
```

`-DryRun` validates both locks and every profile even though it selects one
profile for assembly. `-ValidateOnly` is different: it requires a fully warmed
exact cache, disables network fallback, rebuilds/verifies the temporary
component overlays, and creates no final output.

## Build the OCR overlay

The complete builder invokes this step automatically. It can be isolated for a
fresh-cache supply-chain or runtime review:

```powershell
.\tools\airgap\Build-OcrComponents.ps1 `
  -DestinationDirectory D:\bstrings-staging\ocr-components `
  -DownloadCacheDirectory D:\bstrings-staging\ocr-downloads
```

The stage builder safely expands the two embeddable runtimes and wheel/sdist
contents, derives the exact classifier/dictionary, writes complete runtime and
license inventories, and validates every staged byte against the reviewed
inventories. Fresh-cache builds must succeed; a warmed cache alone is not
evidence that official URLs remain usable.

After the complete assembler adds application-local VC files, bundle
configuration, verifier, and smoke fixtures, run
`.\tools\airgap\Verify-OcrRuntime.ps1 -BundleDirectory <complete-bundle> -Smoke`.
That smoke
performs real CPU/DirectML/hybrid inference by default. Pass
`-SmokeProviders cpu` for the hosted CPU gate. The assembler always requires
CPU inference and records `assemblySelfTestProviders` in the bundle; a custom
hardware build may add DirectML/hybrid, but the separate hardware-acceptance
workflow is the release-process evidence for those paths. Do not run GPU-path
acceptance while another large model owns most VRAM; that can turn a valid
DirectML runtime into device-loss error `887A0006`.

Any OCR model/runtime change also requires the full test suite:

```powershell
python -m unittest discover -s tools\enrichment\tests -v
```

`benchmark_ocr.py --help` is option discovery, not a quality run. Synthetic
fixtures remain packaging/regression tests. Develop and recalibrate on the
pinned SROIE train split, preserving its repair/duplicate audits, but never
reuse the consumed SROIE test split as independent confirmation. Before a new
quality claim, preregister the committed scorer, model, thresholds, runtimes,
and a genuinely untouched holdout, then preserve its immutable witness and
single terminal result. Compare exact identifier recall, CER, provider
identity, determinism, provenance, and throughput; never accept speed by
weakening correctness. See [OCR and document analysis](ocr-and-document-analysis.md)
and the [current OCR benchmark record](ocr-benchmark-2026-08-05.md).

## Build a complete profile

On a connected Windows release host:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\offline-quality `
  -WorkingDirectory D:\bstrings-release-staging `
  -TranslationProfile quality
```

`quality` is the only accepted value; the option remains explicit so generated
configuration and acceptance records retain a stable profile identity. The
output directory must not already exist. The working directory retains exact verified
downloads for resumable CI caching. `-KeepStaging` retains extracted temporary
inputs for diagnostics and is not the release default.

When `-VisualCppRuntimeDirectory` is omitted, the builder uses `vswhere.exe` to
select the newest licensed x64 Visual Studio redistributable directory that
contains every required DLL. It refuses `System32`. An approved explicit source
can be passed:

```powershell
.\tools\airgap\Build-CompleteOfflineBundle.ps1 `
  -PublishedBstringsDirectory .\publish\win-x64 `
  -OutputDirectory .\publish\offline-quality `
  -WorkingDirectory D:\bstrings-release-staging `
  -TranslationProfile quality `
  -VisualCppRuntimeDirectory 'C:\approved-redist\x64\Microsoft.VC14x.CRT'
```

The same exact VC DLL bytes are staged beside the root scanner and every
bundled native CLI/runtime that needs them. The configuration records filenames,
versions, lengths, hashes, and deployment directories. This does not expand
Microsoft's redistribution rights; publish only from an appropriately licensed
toolchain. Microsoft documents
[application-local deployment](https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method?view=msvc-170)
and the [supported redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170).

The llama.cpp runtime is built from pinned source instead of using an upstream
Windows archive containing `libomp140.x86_64.dll` from a `debug_nonredist`
tree. The builder disables OpenMP, network-fetched build inputs, RPC, web UI,
SSL stacks, subprocess/video support, and stages only the reviewed server PE
closure. It rejects `libomp140*.dll`, unexpected imports/files, missing CPU
backends, or a missing `--offline` option. Build provenance, compiler/CMake
versions, flags, hashes, imports, licenses, and borrowed-code notices travel in
the bundle.

The root `.incomplete` marker remains in place through final manifest creation.
That path is reserved and excluded from manifest entries. The builder performs
full Python and native file-set, hash, and link verification in their narrow
builder-only marker mode, then removes the marker as the final completion-state
change. Any later failure restores the marker. Do not use
`--allow-incomplete-marker` for runtime or release acceptance; default
verification rejects a lingering marker.

## Verify a completed bundle

From the finished bundle root:

```powershell
.\bstrings.exe bundle verify
.\Verify-AirgapBundle.ps1 -TranslationSmoke -OcrSmoke
```

The verifier repeats strict file-set/hash/link checks, Markdown-link checks,
managed/native notice closure, Magika/FLOSS redistribution checks, OCR runtime
and model checks, translation model/license identity, and real synthetic
recovery/OCR/translation runs under enforced offline settings. It requires
complete run/summary status, no `.incomplete` marker, protected identifiers,
expected pattern matches, exact OCR lines, and the attributable FLOSS marker.

On a CPU-only hosted runner, select the CPU OCR gate explicitly:

```powershell
.\Verify-AirgapBundle.ps1 `
  -TranslationSmoke `
  -OcrSmoke `
  -OcrSmokeProviders cpu
```

Do not describe that as DirectML/hybrid acceptance.

Use the same checks again after extracting a release pack. Testing only the
pre-package directory is insufficient.

## Create split release packs

CI builds the complete quality profile. After the full smokes pass:

```powershell
.\tools\airgap\New-BundlePackRelease.ps1 `
  -BundleDirectory .\publish\offline-quality `
  -OutputDirectory .\release-packs `
  -CoreReleaseArchive .\bstrings-win-x64.zip `
  -InstallerScript .\Scripts\Install-BstringsQuality.ps1 `
  -ReleaseAssetBaseUrl 'https://github.com/Donovoi/bstrings/releases/download/vX.Y.Z'
```

The script:

1. verifies the complete input bundle with its own root executable;
2. creates one deterministic no-compression shared-base ZIP excluding the
   selected configuration, canonical model license, final manifest, and all
   translation-model path;
3. generates the exact quality configuration, canonical license, final
   manifest, and trust manifest;
4. points each model file pack to its immutable official URL and exact hash;
5. stages the exact quality installer and writes `SHA256SUMS.txt` covering the
   installer, core ZIP, and every split-pack asset; and
6. locally assembles and verifies the template profile unless
   `-SkipAssemblyTest` is deliberately supplied.

The local assembly test must remain enabled in release CI. The tag-only profile
acceptance job separately acquires, assembles, verifies, and translation-smokes
the quality profile from its generated trust manifest before the release job
can start.

The pack generator refuses output inside the verified input bundle and leaves
an `.incomplete` marker if any generation, checksum, or local-assembly step
fails. Treat such a directory as diagnostic residue, not release assets; use a
new empty output for the next attempt.

Never edit a generated trust manifest by hand. Regenerate it from a verified
complete bundle and reviewed locks.

## Release CI gates

Ordinary pushes and pull requests run Rust format/Clippy/tests, .NET
restore/build/tests/publish, Python unit tests/Ruff/bytecode compilation,
PowerShell parser checks, the quality-installer suite under Windows PowerShell
5.1 and PowerShell 7, lock dry-runs, and core packaging. Version tags and manual
dispatches additionally:

- cache exact ordinary and OCR downloads keyed by both lock files;
- build the complete quality bundle from connected inputs;
- revalidate the warmed cache with no network fallback;
- run real translation and CPU OCR smokes under dead external proxies;
- generate and checksum split packs;
- locally assemble/verify the quality pack; and
- retain the generated artifacts for the next gate.

For a complete release, first merge only after the `master` build succeeds.
The automatic Windows release workflow creates the exact project-version tag,
verifies the tested core, and stages that core plus its checksum in a private
draft release. Then manually dispatch `Build and test` with that tag—not
`master`—as the selected ref. A tag-ref dispatch satisfies the tag-only
conditions below, rebuilds the core from the same commit, runs every offline
gate, replaces the preliminary draft assets with the accepted tagged build,
adds the complete asset set and release body, and publishes the draft exactly
once. GitHub then makes the tag and assets immutable. A branch-ref dispatch
builds packs only as review artifacts and cannot publish them.

An exact tag then queues `profile-acceptance` on
`[self-hosted, Windows, X64, bstrings-offline-release]`. That runner must have
GitHub Actions Runner **2.327.1 or newer** because the pinned checkout,
artifact upload, and artifact download actions use the Node 24 action runtime.
It must also have at least 30,000,000,000 free bytes and enough CPU/RAM for the
7B Q8 model. The connected acceptance phase needs outbound HTTPS to GitHub
Actions and the immutable official model URLs. The job uses the core and split
packs from the same workflow run, preloads the release-owned packs, and runs
`bundle acquire`, assembled `bundle verify`, and the full offline translation
smoke for the quality profile. Verified temporary model/bundle copies are
removed within a path-checked per-run work directory.

Only after it passes does the job write and upload
`offline-profile-acceptance.json` as an internal workflow artifact. It records
the tag, commit, build run and positive acceptance attempt, core archive
identity, exact release-pack inventory, checksum-file identity, and the
profile's trust manifest, final manifest, configuration, license, model ID,
revision, size, and hash. The release job depends on both `build` and
`profile-acceptance`. Before publishing,
`Test-OfflineProfileReleaseEvidence.ps1` rejects extra files, links/reparse
points, duplicate evidence/checksum/manifest rows, a non-exact checksum set, or
any byte/hash/configuration mismatch. Every release-owned base, configuration,
license, and manifest URL must equal
`GITHUB_SERVER_URL/GITHUB_REPOSITORY/releases/download/GITHUB_REF_NAME/<asset>`
byte-for-byte. Alternate hosts, repository/tag paths, casing, percent escapes,
credentials, queries, and fragments are rejected even when they would resolve
to equivalent content. Each translation-model URL is recorded by acceptance
and must equal both the trust manifest and the exact checked
`offline-components.lock.json` URL; the lock URL must also be the canonical
Hugging Face `model-id/resolve/revision/filename?download=true` form. The release
job validates the internal record against the exact public assets, then
publishes only the user-facing product, installation, verification, license,
checksum, and manifest files. The build run ID, repository, tag, and commit must
match exactly. The acceptance attempt
must be positive but is intentionally not required to equal the release job's
current attempt: GitHub can rerun only a failed downstream release job while
safely reusing immutable acceptance evidence from the same workflow run.
A missing runner, failed download, failed hash, failed assembly, failed strict
verification, or failed translation smoke blocks publication. This exact
quality-profile gate runs for every version tag.

Release artifacts use fixed names and become immutable only after the complete
draft is published. Never publish the preliminary core draft: GitHub does not
permit assets to be added to a release that was published while immutable
releases were enabled. For a failed tag run,
choose **Re-run failed jobs**, not **Re-run all jobs**. Re-running a successful
artifact-producing job under the same workflow run would try to upload an
already existing name and must fail rather than overwrite reviewed bytes. If an
artifact-producing job failed after completing an upload, preserve the old run
for diagnosis and start a clean release run instead of deleting or overwriting
its evidence.

DirectML/hybrid acceptance is intentionally separate. Manually dispatch
`.github/workflows/ocr-hardware-acceptance.yml` with the source build run ID.
It targets only `[self-hosted, Windows, X64, bstrings-directml]`, downloads the
core and split-pack artifacts from that run, preloads the release-owned quality
packs, acquires the immutable 7B Q8 model through the trust manifest,
assembles/verifies the exact bundle, and then requires full CPU, DirectML, and
hybrid image/PDF inference. It uploads a small synthetic hardware-acceptance
record containing the source run ID, manifest/lock hashes, resolved providers,
and display-adapter/driver identity.

The hardware workflow has only `workflow_dispatch`; it never auto-queues on a
push, pull request, or tag. A release claiming DirectML/hybrid acceptance must
retain a passing hardware artifact tied to its build run. The earlier local
benchmark remains useful path/performance evidence, but is not a substitute for
that release-specific record.

Evidence types are not interchangeable:

| Evidence | What it establishes |
| --- | --- |
| Internal `offline-profile-acceptance.json` artifact | Per-tag acquisition, assembly, strict verification, and translation smoke for the quality profile; never a public Release asset |
| OCR hardware acceptance artifact | CPU/DirectML/hybrid packaged-path behavior for one source build and named host/driver |
| Local v3 SROIE CPU calibration | Frozen-candidate printed-receipt quality on the selected training corpus |
| Internal SROIE terminal and post-hoc records | The consumed one-shot disposition and later diagnostic findings; neither establishes independent acceptance |

The local CPU-only v3 calibration selected 616 of 626 training documents and
passed its frozen development-data gate. The exact metrics and limits are kept
in the [OCR benchmark record](ocr-benchmark-2026-08-05.md).

The one-shot test still failed closed on a degenerate source annotation before
quality scoring. Its consumed ledger must not be replaced with another
one-shot run. The later post-hoc diagnostic audited all 361 rows, excluded
eight exact train/test image overlaps, and scored 353 rows, including repaired
dataset row index 142 (zero-based). Every backend met all 11 frozen numeric
thresholds, and the aggregate and per-document scored metrics matched.
Evidence-record integrity did not, so the diagnostic failed overall. It is not
an acceptance or parity result. Raw reports, witnesses, ledgers, outputs, and
logs remain CI/internal evidence; they are not GitHub Release assets.

Synthetic OCR tests remain packaging/regression evidence. A materially changed
candidate needs a genuinely untouched holdout for any new independent claim.

For a future untouched holdout, keep the maintainer sequence explicit: clean
remote commit and exact green CI; a calibration run that passed its frozen gate
and its derived policy; a path-free hash-bound pre-test witness; independent
verification of that witness; one ledger-claimed run; and preservation of the
private terminal evidence in CI/internal storage. Publish only a concise,
qualified result summary in documentation—never the benchmark evidence as a
GitHub Release.
Never make the one-shot command a normal tag CI job, reset its stable ledger
for a protocol revision,
or promote an unsealed output, failed ledger, stale calibration, or synthetic
smoke as quality acceptance.

Do not weaken the exactness gates to make CI faster. If a large transfer is the
bottleneck, preserve resumability and immutable caches rather than skipping
byte verification.

## Licenses and redistribution closure

The complete builder and verifier enforce component-owned overlays:

- Magika's CLI, app-local DirectML, 70-package runtime inventory, notices, and
  required MPL-covered source;
- FLOSS's embedded Python/PyInstaller/native/Rust dependency inventories,
  notices, and required corresponding source;
- llama.cpp build provenance and notice closure;
- OCR's two Python/ONNX Runtime closures, models/dictionary, inventories,
  notices, and licenses;
- the Hy-MT2 7B profile and canonical license; and
- exact .NET/Rust/VC runtime attribution.

Read [Magika redistribution](magika-cli-redistribution.md) and
[FLOSS redistribution](floss-standalone-redistribution.md) before changing
those inventories. A repository or package label never substitutes for a
byte-exact license/source check.

## Integrity and acceptance boundaries

The strict manifest, pack trust manifest, and checksums establish consistency
relative to the bytes they name. They do not authenticate a publisher if an
attacker can replace both data and manifests. Do not claim a signed release
until the workflow actually Authenticode-signs/RFC 3161-timestamps executables,
verifies a detached signed manifest against an independently trusted key, and
uses an immutable release policy. See Microsoft's
[timestamping guidance](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures)
and GitHub's [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases).

Hosted-runner smokes use the real bundle and enforced offline mode, but do not
prove a pristine disconnected Windows installation. Before making that claim,
record acceptance on a standard-user supported Windows VM with its virtual NIC
disabled and without separately installed .NET, Python, VC runtime, Git, Rust,
or package managers. Include paths with spaces/non-ASCII characters, read-only
media, insufficient disk, interrupted/resumed acquisition, tampered/missing/
extra files, GPU contention, and endpoint-control behavior.
