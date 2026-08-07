# FLOSS v3.1.1 offline redistribution

Distribution status: this reviewed FLOSS payload is included in the complete
v1.9.5 quality kit. It is not included in the standalone core ZIP. Do not copy
it between versioned installations; use the verified quality installer
described in the [download guide](download-and-install.md).

The Windows x64 offline bundle carries
[Mandiant FLARE FLOSS v3.1.1](https://github.com/mandiant/flare-floss/releases/tag/v3.1.1)
as its unchanged upstream `floss.exe`, together with a byte-pinned inventory,
required notices, and the corresponding source snapshot required for the
embedded MPL-2.0 code.
No Python installation, package download, or network connection is required at
runtime.

## Reviewed payload

The accepted executable is exactly:

- command version: `floss.exe v3.1.1-0-g3cd3ee6`
- size: 31,866,819 bytes
- SHA-256: `3a208ab834b4791e81592d66e91a7f07b3458504484d87c44ed32bc7384df7ef`
- upstream commit: `3cd3ee6a74992d98e91463c2658d9af446504821`
- Authenticode status: unsigned

[`licenses/floss-v3.1.1-win-x64.json`](../licenses/floss-v3.1.1-win-x64.json)
is the machine-readable bill of materials. It records 29 Python runtime
packages, 67 native files, the embedded Python and PyInstaller provenance, and
two Rust dependency closures. Four upstream build pins (`click`, `dncil`,
`pyasn1`, and `PyYAML`) are recorded as build-only because recursive inspection
found no corresponding package roots in the executable.

The notice overlay contains 18 files and is 2,045,571 bytes. It includes the
exact `tqdm` 4.66.4 source archive because the embedded package is licensed
under MPL-2.0 and MIT. It also includes Python, PyInstaller, FLOSS, Python
package, Rust dependency, and embedded native-runtime notices.

## Stage and verify

Create a new or empty staging directory:

```powershell
pwsh -NoProfile -File tools/licenses/Stage-FlossThirdPartyNotices.ps1 `
  -FlossExecutable C:\path\to\floss.exe `
  -DestinationDirectory C:\path\to\notice-overlay
```

The result can be copied directly over the bundle root. Its layout is:

```text
licenses/
  floss-v3.1.1-win-x64.json
  floss-v3.1.1/
    ...17 reviewed assets...
```

Verify a completed bundle using its final executable and root directory:

```powershell
pwsh -NoProfile -File tools/licenses/Verify-FlossThirdPartyNotices.ps1 `
  -FlossExecutable C:\path\to\bundle\tools\floss\floss.exe `
  -StagedDirectory C:\path\to\bundle
```

The verifier fails closed on executable, inventory, notice, or source changes;
missing assets; unexpected files inside the FLOSS notice directory; package or
native inventory drift; and version-probe failure. The release test covers the
valid path and five negative cases:

```powershell
pwsh -NoProfile -File tools/licenses/Test-FlossThirdPartyNotices.ps1 `
  -FlossExecutable C:\path\to\floss.exe
```

## Known provenance limits

Two limitations are documented rather than hidden:

- `python-flirt` 0.8.10 did not publish a `Cargo.lock`. Its 65-entry Rust list
  is a conservative manifest-family resolution supported by exact binary
  markers, not a claim of an exact transitive build lock.
- Forty Microsoft-resource runtime files inside the official Mandiant artifact
  carry valid Eclipse Foundation signatures rather than Microsoft signatures.
  The bundle preserves the upstream executable byte-for-byte and records every
  native hash and signer. It does not describe those files as independently
  verified Microsoft REDIST bytes.

See
[`Microsoft-Embedded-Runtime-Notice.md`](../licenses/floss-v3.1.1/Microsoft-Embedded-Runtime-Notice.md)
for the redistribution boundary and primary Microsoft references. A future
release requiring stronger provenance should use a separately reviewed,
reproducibly locked FLOSS rebuild rather than silently replacing bytes inside
the reviewed upstream executable.
