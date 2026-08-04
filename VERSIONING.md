# Versioning and releases

The application version lives in `bstrings/bstrings.csproj`:

```xml
<Version>1.9.0</Version>
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
repository.

## Validate before tagging

Run the same core steps used by CI:

```powershell
dotnet restore bstrings.sln
dotnet build bstrings.sln -c Release --no-restore
dotnet test bstrings.sln -c Release --no-build
```

The workflow additionally checks Rust formatting/lints/tests, Python
lint/compilation/tests, PowerShell syntax, third-party inventories, the
self-contained publish, and the integrated offline smoke. A manual dispatch or
release tag also builds the complete CPU/Q4 archive, revalidates its warmed
cache without network fallback, enforces the archive-size/checksum boundary,
extracts the exact ZIP, verifies its manifest, and runs the CPU translation and
FLOSS recovery smokes. The exact procedure is in
[offline release maintenance](docs/offline-release-maintenance.md). Create a
release tag only from a commit whose full `master` workflow has passed.

## Create a release

Use exactly `v<MAJOR.MINOR.PATCH>`, with no suffix, and make it match the one
`Version` value in `bstrings/bstrings.csproj`:

```powershell
git tag v1.9.0
git push origin v1.9.0
```

The workflow rejects a mismatched tag before installing build toolchains or
starting the multi-gigabyte release path. Pull requests and ordinary pushes
build the temporary core artifact. A manual workflow dispatch also produces a
reviewable complete offline artifact without creating a release. Only an exact
matching pushed tag creates a GitHub release with all three Windows assets:

- `bstrings-win-x64.zip`;
- `bstrings-win-x64-offline-cpu.zip`; and
- `bstrings-win-x64-offline-cpu.zip.sha256`.
