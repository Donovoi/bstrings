# Versioning and releases

The application version is the semantic version in
`bstrings/bstrings.csproj`:

```xml
<Version>1.9.0</Version>
```

Use `MAJOR.MINOR.PATCH`:

- increment `PATCH` for compatible fixes;
- increment `MINOR` for compatible features;
- increment `MAJOR` for breaking changes.

## Update a version

The helper changes the project file locally:

```powershell
.\Scripts\UpdateVersion.ps1 patch
.\Scripts\UpdateVersion.ps1 minor
.\Scripts\UpdateVersion.ps1 major
```

Review and commit that change normally. Commit-message tokens do not modify
versions, and CI does not commit changes back to the repository.

## Validate and release

```powershell
dotnet restore bstrings.sln
dotnet build bstrings.sln -c Release --no-restore
dotnet test bstrings.sln -c Release --no-build

git tag v1.9.0
git push origin v1.9.0
```

Pushes and pull requests validate the project and produce a temporary Windows
x64 artifact. Only a pushed tag whose name starts with `v` creates a GitHub
release. The release workflow publishes a self-contained, single-file Windows
x64 zip.

Create a version tag only from a commit that has passed the full validation
workflow.
