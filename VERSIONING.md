# Versioning and releases

The application version is in `bstrings/bstrings.csproj`.

The project uses `MAJOR.MINOR.PATCH`:

- Change `PATCH` for a compatible fix.
- Change `MINOR` for a compatible feature.
- Change `MAJOR` for an incompatible public change.

## Version 2 release contract

Version 2 has one complete Windows kit. Version 2.1.0 uses these public names:

- `Install-Bstrings.ps1`
- `bstrings-kit`
- `bundle-packs.json`
- `airgap-config.json`
- `airgap-manifest.json`
- `Hy-MT2-Apache-2.0.txt`

The release can split the kit into download parts. These parts are installer
inputs. They are not separate products or installation choices.

The base archive contains `BASE_PACK_NOTICE.md`. This notice tells users to run
the installer instead of treating the archive as a separate installation.

## Change the version

Use the helper:

```powershell
.\Scripts\UpdateVersion.ps1 patch
.\Scripts\UpdateVersion.ps1 minor
.\Scripts\UpdateVersion.ps1 major
```

Review and commit the change. CI does not write a version commit.

Ordinary changes can keep the current source version. Maintainers can combine
several tested changes in one release. A forward version change starts release
preparation.

## Update release-owned files

For each new release, update all these items together:

- `bstrings/bstrings.csproj`
- the installer release pin
- the release notes
- README release links
- the supported trust identity
- the component lock
- release acceptance tests and expected assets

A release tag and all published assets are immutable. Do not replace an asset
in an existing release. Publish a new version.

## Validate before tagging

Run the normal build and test gates. Also run the complete Windows kit gates:

- Build the exact tested commit.
- Assemble the complete kit.
- Verify every manifest row.
- Test native extraction, FLOSS, OCR, and translation.
- Run the installer with Windows PowerShell 5.1 and PowerShell 7.
- Test a clean installation and a verified upgrade.
- Test old-install and old-cache migration.
- Run air-gapped bundle acceptance.
- Check the release asset inventory and checksums.
- Scan all public files for private case data.

The public release must contain the exact tested commit. Its tag, assets,
checksums, trust records, installer, and acceptance evidence must agree.

## Release workflow

A successful master build can stage a draft. The draft is not a second product.
It holds installer components until the complete release gates pass.

The release workflow must:

1. Use the exact tested commit.
2. Create the exact version tag once.
3. Build and verify the complete Windows kit.
4. Create the split installer parts.
5. Run independent bundle acceptance.
6. Replace the draft component set with the accepted public asset set.
7. Publish the immutable release once.

The internal acceptance file is `offline-bundle-acceptance.json`. Do not publish
this file as a release asset.

See [Offline release maintenance](docs/offline-release-maintenance.md) for the
full maintenance procedure.

## Historical records

Files in `docs/releases/` describe the names and commands for their releases.
Do not change those historical facts. Older architecture decisions can also
contain the former terms when they record earlier behavior.

[v1.9.17](https://github.com/Donovoi/bstrings/releases/tag/v1.9.17) is immutable.
Its historical asset and directory names cannot change.

The current one-kit decision is
[ADR-0011](docs/architecture/adr-0011-single-windows-kit-and-plain-documentation.md).
