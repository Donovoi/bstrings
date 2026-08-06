# Versioning and releases

The application version lives in `bstrings/bstrings.csproj`:

```xml
<Version>1.9.1</Version>
```

The project uses `MAJOR.MINOR.PATCH`:

- bump `PATCH` for a compatible fix;
- bump `MINOR` for a compatible feature; and
- bump `MAJOR` for a breaking change.

## Change the version

The helper updates the project file locally:

```powershell
.\Scripts\UpdateVersion.ps1 patch
.\Scripts\UpdateVersion.ps1 minor
.\Scripts\UpdateVersion.ps1 major
```

Review the diff and commit it like any other change. Commit-message keywords do
not change the version, and CI never writes a version commit back to the
repository. The helper changes only the project file. Before a release, also
update the exact tag pinned by `Scripts/Install-BstringsQuality.ps1`, the
release document selected by `tools/airgap/Build-AirgapBundle.ps1`, and the
versioned user documentation. CI rejects an installer tag that differs from
the project version.

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
PowerShell 7, and the integrated offline smoke. A manual dispatch or release
tag also builds the complete CPU/Q4 archive, revalidates its warmed
cache without network fallback, enforces the archive-size/checksum boundary,
extracts the exact ZIP, verifies its manifest, and runs the CPU translation and
FLOSS recovery smokes. The exact procedure is in
[offline release maintenance](docs/offline-release-maintenance.md). Create a
release tag only from a commit whose full `master` workflow has passed.

## Create v1.9.1 when its gates pass

v1.9.1 is the version prepared by this source tree. Confirm that the exact
candidate commit is green and that the version has not already been tagged
before creating the release tag.

Use exactly `v<MAJOR.MINOR.PATCH>`, with no suffix, and make it match the one
`Version` value in `bstrings/bstrings.csproj`:

```powershell
git tag v1.9.1
git push origin v1.9.1
```

The workflow rejects a mismatched tag before installing build toolchains or
starting the multi-gigabyte release path. Pull requests and ordinary pushes
build a temporary core artifact. A manual workflow dispatch produces
reviewable build artifacts without creating a release. Only an exact matching
pushed tag can start the final release job, and publication remains blocked
until every required profile gate passes.

The v1.9.1 release job publishes:

- `Install-BstringsQuality.ps1`;
- `bstrings-win-x64.zip`;
- `bstrings-win-x64-offline-base.zip`;
- `airgap-config-{quality,balanced,compact}.json`;
- `airgap-manifest-{quality,balanced,compact}.json`;
- `Hy-MT2-Apache-2.0-{quality,balanced,compact}.txt`;
- `bundle-packs-{quality,balanced,compact}.json`;
- `SHA256SUMS.txt`, which covers the installer and every other public asset.

The workflow separately retains `offline-profile-acceptance.json` as an
internal Actions gate artifact. The release job validates it against the exact
tagged build, but it is not a public Release download.

The human release body is [`docs/releases/v1.9.1.md`](docs/releases/v1.9.1.md).
Keep its download names, commands, profiles, and boundaries synchronized with
the workflow before tagging. Keep
[`docs/releases/v1.9.0.md`](docs/releases/v1.9.0.md) unchanged as the historical
v1.9.0 note.

GitHub Releases are the product channel. Keep only usable program/download,
installation, license, checksum, manifest, and release-verification assets
there. Benchmark corpora, raw outputs, one-shot witnesses and ledgers, logs,
host details, and experimental test reports belong in workflow artifacts or
controlled internal evidence storage. Put only a concise, qualified benchmark
summary in repository documentation and release notes.
