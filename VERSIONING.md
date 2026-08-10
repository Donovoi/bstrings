# Versioning and releases

The application version lives in `bstrings/bstrings.csproj`:

```xml
<Version>1.9.17</Version>
```

The project uses `MAJOR.MINOR.PATCH`:

- bump `PATCH` for a compatible fix;
- bump `MINOR` for a compatible feature; and
- bump `MAJOR` for a breaking change.

## Current release

v1.9.17 is the complete Windows x64 quality/offline release. Its release assets
contain the current core plus the version-matched installer, offline base,
quality manifest, licence, and trust metadata needed to assemble and verify the
single advertised Full profile. See
[download and installation](docs/download-and-install.md) for the user-facing
feature matrix.

Tags and published assets remain immutable in identity. A new version and
successful build are required to publish a replacement core; a new complete
quality release additionally requires every offline acceptance gate.

## Change the version

The helper updates the project file locally:

```powershell
.\Scripts\UpdateVersion.ps1 patch
.\Scripts\UpdateVersion.ps1 minor
.\Scripts\UpdateVersion.ps1 major
```

Review the diff and commit it like any other change. Commit-message keywords do
not change the version, and CI never writes a version commit back to the
repository. The helper changes only the project file.

Ordinary product, test, build, packaging, installer, workflow, and documentation
changes may retain the current version so maintainers can batch several tested
changes into one release. CI compares the base and head with
`tools/release/Assert-CodeVersion.ps1`: it always rejects a decrease, accepts an
equal version as an ordinary change, and recognizes an unused forward version
as deliberate release preparation. A forward version whose tag already exists
is rejected.

The automatic Windows-core channel does not change the quality installer's
pinned tag. Update `Scripts/Install-BstringsQuality.ps1`, its tests, the
air-gap release document inventory, and versioned user documentation only when
preparing a new fully gated quality/offline release.

## Validate before tagging

Run the same core steps used by CI:

```powershell
dotnet restore bstrings.sln
dotnet build bstrings.sln -c Release --no-restore
dotnet test bstrings.sln -c Release --no-build
```

The workflow additionally checks Rust formatting/lints/tests, Python
lint/compilation/tests, PowerShell syntax, third-party inventories, the
self-contained publish, the quality installer under Windows PowerShell 5.1 and
PowerShell 7, and the integrated offline smoke. An ordinary manual dispatch
runs this fast lane. A manual dispatch on `master` with `full_offline=true`, or
any version tag, also builds the complete CPU/Q4 archive and authenticated CUDA
overlay, fully rehashes the exact restored component cache without network
fallback, enforces the
archive-size/checksum boundary, extracts the exact ZIP, verifies its manifest,
and runs the CPU translation and FLOSS recovery smokes. The exact procedure is in
[offline release maintenance](docs/offline-release-maintenance.md). Do not run
the tag workflow until the explicit full-offline `master` dispatch has passed
for the release-preparation commit.

## Automatic Windows x64 release draft

Every successful `Build and test` push run on `master` is followed by
`Stage Windows release draft`. The workflow checks out the exact tested
commit and reads the project version. Equal-version ordinary changes are a
no-op when that version already belongs to an earlier staged or published
release; they do not create another draft or tag. For a deliberate unused
forward version, the workflow creates the exact immutable-candidate tag,
downloads the `bstrings-win-x64` artifact from that successful run,
extracts and verifies its required files, creates a SHA-256 checksum list, and
stages a private draft containing only:

- `bstrings-win-x64.zip`; and
- `SHA256SUMS.txt`.

The workflow runs only for a successful same-repository `master` push. It does
not execute pull-request code with a write token, does not use a personal
access token, and refuses to retarget an existing tag. Reruns are idempotent
when the exact draft or published release already exists. The preliminary core
draft is not a user download channel; it remains mutable only until the full
workflow replaces its assets and publishes it once.

## Full quality/offline release

The larger quality/offline release remains separately gated. Its release job
publishes:

- `Install-BstringsQuality.ps1`;
- `bstrings-win-x64.zip`;
- `bstrings-win-x64-offline-base.zip`;
- `airgap-config-quality.json`;
- `airgap-manifest-quality.json`;
- `Hy-MT2-Apache-2.0-quality.txt`;
- `bundle-packs-quality.json`;
- `SHA256SUMS.txt`, which covers the installer and every other public asset.

The full workflow separately retains `offline-profile-acceptance.json` as an
internal Actions gate artifact. The release job validates it against the exact
tagged build, but it is not a public Release download.

After the deliberate version bump passes fast CI and the automatic channel
creates its tested tag and core draft, manually dispatch `Build and test` on
`master` with `full_offline=true`. This runs the complete hosted lane and, only
after the network-blocked `ValidateOnly` pass has fully rehashed every cached
component, may populate the exact default-branch component cache. It does not
publish a release. Then dispatch `Build and test` with the version tag as the
selected ref. The tag run restores only that exact cache key, never writes a
duplicate tag-scoped cache, rebuilds and verifies the complete release, and
runs fresh cold self-hosted acquisition, assembly, verification, translation
smoke, and evidence generation. It replaces the preliminary draft assets,
adds the full asset set, and publishes exactly once. GitHub then makes the tag
and assets immutable. Dispatching against a branch cannot publish a full
release.

For v1.9.17 the complete release body is
[`docs/releases/v1.9.17.md`](docs/releases/v1.9.17.md). Keep historical release
documents unchanged. Before promoting a version to the full quality/offline
asset set, update its installer pin, documentation inventory, human release
body, and the quality-profile acceptance evidence.

GitHub Releases are the product channel. Keep only usable program/download,
installation, license, checksum, manifest, and release-verification assets
there. Benchmark corpora, raw outputs, one-shot witnesses and ledgers, logs,
host details, and experimental test reports belong in workflow artifacts or
controlled internal evidence storage. Put only a concise, qualified benchmark
summary in repository documentation and release notes.
