# Download and install bstrings on Windows

There is one Windows kit. It contains all supported engines, models, runtimes,
licenses, and verification data.

There is no second kit to select.

## Requirements

- Windows 11 x64
- At least 30 GiB of free space
- Internet access on the staging computer

You do not need administrator rights.

## Version 2.1.1

Version 2.1.1 uses:

- `Install-Bstrings.ps1` for the installer;
- `bstrings-kit` for the default installation; and
- `.bstrings-installer-cache` for verified download bytes.

## Install version 2.1.1

Use the authenticated command in the main README. It gets
`Install-Bstrings.ps1` from the immutable v2.1.1 release.

Run the authenticated installer from the directory that will contain
`bstrings-kit`:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Install-Bstrings.ps1
```

The installer performs these actions:

1. Authenticates the exact immutable release.
2. Authenticates its own release digest.
3. Downloads and checks each kit part.
4. Builds a new kit in a private sibling directory.
5. Verifies the new kit.
6. Replaces a verified old installation safely.
7. Verifies the installed path again.

The installer does not patch files in place. It restores the previous verified
installation if a replacement fails.

## Verify the installation

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

Exit code 0 confirms a verified kit. Any other exit code means verification
failed.

## Run the first analysis

Use a new or empty output directory:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence `
  -o D:\results\case-01 `
  --full
```

A complete first run has all these results:

- The command returns exit code 0.
- `run.json` has `status: complete`.
- `summary.json` has `status: complete`.
- The output directory does not contain `.incomplete`.

Failed output remains available for diagnosis. A normal retry uses another new
or empty directory.

## Upgrade safely

Run the authenticated installer for the new release. The installer can replace
only a directory that it identifies as an installed bstrings kit.

The installer refuses to replace an unrelated `bstrings-kit` directory. This
rule protects source trees and other user files.

The first version 2 upgrade can migrate the historical default installation.
Migration occurs only when all these conditions are true:

- You use the default destination.
- `bstrings-kit` does not exist.
- The historical installation is a physical directory.
- Its files match the current release manifest where they are reused.
- The complete new kit passes verification before the old path is removed.

The installer stops if both old and new default directories exist. It does not
select or change either directory automatically.

An explicit destination does not start automatic migration.

## Use the verified cache

The default cache is `.bstrings-installer-cache` beside the installation. The
cache can avoid another large model download.

The cache is not trusted. The installer checks the exact size and SHA-256 of
each object every time it uses that object.

The version 2 installer can import an unchanged object from the historical
cache. It verifies the source, copies it to the new cache, and verifies the copy.

Version 1.9.17 remains immutable. Use its
[historical README](https://github.com/Donovoi/bstrings/blob/v1.9.17/README.md#get-started)
when you install that release.

Use the installer cleanup option when you do not want to keep verified download
bytes:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Install-Bstrings.ps1 `
  -RemoveCacheAfterSuccess
```

## Install to another directory

Use an explicit physical path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\Install-Bstrings.ps1 `
  -DestinationDirectory C:\Tools\bstrings-kit
```

The destination and cache cannot overlap. The installer rejects a filesystem
root.

## Transfer the kit offline

1. Install and verify the kit on a connected staging computer.
2. Copy the complete `bstrings-kit` directory to transfer media.
3. Copy it to the disconnected computer.
4. Run `bundle verify` on the disconnected computer.

The manifest describes one complete authenticated kit. A copy with selected
engines or models missing fails verification.

See [Air-gapped deployment](air-gapped-deployment.md) for the full transfer
procedure.

## Installer parts are not separate kits

The release can contain a base archive, packs, manifests, and licenses. These
files let the installer transfer and verify the kit.

The base archive is an installer component, not a separate product. It does not
contain every supported engine and model.

## Security boundary

Release metadata, filenames, cache entries, and old success reports do not
authorize bytes. Exact current size and SHA-256 checks authorize each file.

The installer rejects links, reparse points, path escapes, malformed manifests,
unexpected assets, and unverified replacement directories.

For release design and migration evidence, see
[ADR-0011](architecture/adr-0011-single-windows-kit-and-plain-documentation.md).
