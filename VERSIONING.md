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

The GitHub workflow also publishes and packages the self-contained Windows x64
build. Create a release tag only from a commit whose full `master` workflow has
passed.

## Create a release

Use a `v`-prefixed tag that matches the project version:

```powershell
git tag v1.9.0
git push origin v1.9.0
```

Pull requests and ordinary pushes build a temporary artifact for validation.
Only a pushed `v*` tag creates a GitHub release. The release contains the
self-contained, single-file Windows x64 zip produced by the same workflow.
