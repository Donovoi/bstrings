# Version Management for bstrings

This document explains how version numbering works in the bstrings project and how to increment versions.

## Automatic Version Management

The project includes an automated version management system that can increment version numbers based on commit messages or manual script execution.

### Version Format

The project uses semantic versioning: `MAJOR.MINOR.PATCH`

- **MAJOR**: Breaking changes or major feature releases
- **MINOR**: New features that are backward compatible
- **PATCH**: Bug fixes and small improvements

### Current Version

The current version is stored in `bstrings/bstrings.csproj` in the `<Version>` tag.

### Manual Version Updates

Use the PowerShell script to manually increment versions:

```powershell
# Increment patch version (1.7.1 → 1.7.2)
./Scripts/UpdateVersion.ps1 patch

# Increment minor version (1.7.1 → 1.8.0)
./Scripts/UpdateVersion.ps1 minor

# Increment major version (1.7.1 → 2.0.0)
./Scripts/UpdateVersion.ps1 major
```

### Automatic Version Updates via Commit Messages

The GitHub Actions workflow can automatically increment versions based on commit message patterns:

```bash
# Auto-increment patch version
git commit -m "Fix memory leak in GPU processing [version:patch]"

# Auto-increment minor version
git commit -m "Add new parallel regex engine [version:minor]"

# Auto-increment major version
git commit -m "Redesign streaming architecture [version:major]"
```

When you include `[version:patch]`, `[version:minor]`, or `[version:major]` in your commit message, the build system will automatically:

1. Detect the version increment request
2. Update the version in `bstrings.csproj`
3. Build with the new version number
4. Create releases with the correct version tags

### Version Detection in Build

The GitHub Actions workflow reads the version from:

1. **Primary**: `<Version>` tag in `bstrings/bstrings.csproj`
2. **Fallback**: `AssemblyVersion` in `Properties/AssemblyInfo.cs` (if present)

### Release Naming

Releases are automatically tagged with the format:

```
v{VERSION}-{TIMESTAMP}-{COMMIT_SHA}
```

Example: `v1.7.1-20250613-142530-a1b2c3d`

### Best Practices

1. **Use patch increments** for bug fixes and performance improvements
2. **Use minor increments** for new features that don't break compatibility
3. **Use major increments** for breaking changes or major architectural updates
4. **Always test** after version updates to ensure builds work correctly
5. **Update documentation** when making minor or major version changes

### Troubleshooting

If version detection fails:

1. Check that `<Version>X.Y.Z</Version>` exists in `bstrings/bstrings.csproj`
2. Ensure the version format is `major.minor.patch` (three numbers)
3. Verify the PowerShell script has execution permissions
4. Check GitHub Actions logs for version detection messages

### Version History

The version history and release notes are maintained in the main `README.md` file under the release notes section.
