# Third-party notices

## Managed application and .NET runtime

The Windows x64 release contains these nine managed NuGet dependencies:
DeviceIOControlLib 0.1.6; DiscUtils.Core, DiscUtils.Ntfs, and DiscUtils.Streams
0.16.13; ILGPU 1.5.3; RawDiskLib 0.3.0; Serilog 4.4.0;
Serilog.Sinks.Console 6.1.1; and System.CommandLine 2.0.10. Their exact
versions, restored package hashes, declared license metadata, repositories,
commits where published, and shipped notice files are recorded in
[licenses/bstrings-managed-win-x64.json](licenses/bstrings-managed-win-x64.json).

The corresponding license material is distributed as
[licenses/MIT.txt](licenses/MIT.txt),
[licenses/Apache-2.0.txt](licenses/Apache-2.0.txt), the byte-exact
bundle-only paths `licenses/DeviceIOControlLib-0.1.6-LICENSE.txt`,
`licenses/ILGPU-1.5.3-LICENSE.txt`, and
`licenses/ILGPU-1.5.3-LICENSE-3RD-PARTY.txt`. The latter files are staged from
their verified upstream packages during release construction, so they are not
repo-local links.

The self-contained executable embeds
`Microsoft.NETCore.App.Runtime.win-x64` 10.0.10. Its exact upstream license
and consolidated attribution file are
`licenses/dotnet-runtime-win-x64-10.0.10-LICENSE.TXT`
and
`licenses/dotnet-runtime-win-x64-10.0.10-THIRD-PARTY-NOTICES.TXT` inside the
release archive.
`tools/licenses/Verify-ManagedThirdPartyNotices.ps1` fails when this dependency
closure, its NuGet metadata, package hashes, runtime-pack pin, or notice bytes
drift from the reviewed inventory.

## Rust native engine

`bstrings_core` includes [Lingua for Rust](https://github.com/pemistahl/lingua-rs)
and the normal dependencies compiled into its Windows x64 native library. The
complete locked package inventory records each package's version, declared
license, selected license, upstream authors, and repository in
[licenses/bstrings-core-win-x64.tsv](licenses/bstrings-core-win-x64.tsv).

The selected license texts are distributed with bstrings:

- [Apache License 2.0](licenses/Apache-2.0.txt)
- [MIT License and copyright notices](licenses/MIT.txt)
- [Unicode License v3](licenses/Unicode-3.0.txt)

Where an upstream package offers a choice, the inventory records the license
used for this distribution. `unicode-ident` additionally requires the Unicode
License v3. Run `tools/licenses/Verify-RustThirdPartyNotices.ps1` after changing
`native/bstrings_core/Cargo.lock`; it fails if the locked Windows x64 runtime
tree or its license metadata no longer matches the reviewed inventory.

`bstrings_core` is built with and statically links the Rust standard library 1.95.0.
Its complete upstream attribution is shipped byte-for-byte as
the bundle-only path `licenses/Rust-1.95.0-COPYRIGHT-library.html`.

## Complete offline bundle components

The complete offline bundle also carries these separately distributed
components under their own terms:

- CPython 3.14.6, with its license in `runtime/python/LICENSE.txt`;
- Magika CLI 1.1.0, its application-local DirectML 1.15.4 runtime, the
  reviewed 70-package Rust/ONNX Runtime dependency closure, and required
  corresponding source. The exact inventory, notices, and source-availability
  record begin at the complete-bundle path
  `licenses/magika-cli-1.1.0-redistribution.json`;
  MPL-covered `colored` and Eigen source is under
  `sources/magika-cli-1.1.0/`;
- FLOSS 3.1.1, with an inventory of the embedded Python/PyInstaller runtime,
  29 Python packages, 67 native entries, and both Rust dependency closures.
  Its notices and the required `tqdm` 4.66.4 source archive are under
  the complete-bundle paths `licenses/floss-v3.1.1-win-x64.json`
  and `licenses/floss-v3.1.1/`;
- llama.cpp b10248, built from pinned commit
  `e8e06f78e253a98a739b8ae4c6b661b357249ce4`, with its MIT license and the
  applicable cpp-httplib, nlohmann JSON, base64, miniaudio, stb_image,
  llamafile, YaRN, and ggllm-derived notices preserved under
  `licenses/llama.cpp/`;
- the Hy-MT2-7B Q8_0 model, with its Apache 2.0 license in `licenses/`; and
- the Microsoft Visual C++ x64 runtime files described below.

Those license and notice files must remain with the bundle. Building the
bundle does not change or replace their terms.

The large upstream Magika, ONNX Runtime, DirectML, and Rust source archives are
byte- and hash-verified build-cache inputs; they are not copied into the
examiner-facing archive. The archive carries the runtime, exact inventories,
license/notice material, and source that redistribution requires. The locked
Magika and FLOSS overlay verifiers reject missing, changed, or unexpected
component-owned files before release packaging. Complete-bundle maintenance
details ship at `docs/magika-cli-redistribution.md` and
`docs/floss-standalone-redistribution.md`.

The Windows x64 core and complete archives carry the four required Microsoft
Visual C++ x64 runtime DLL names in `tools/airgap/offline-components.lock.json`
as application-local redistributables. Their names and licensed Visual Studio
redist source policy are locked; their selected bytes can vary with the
licensed build toolchain. Each release records their exact version, byte length,
and SHA-256 in `airgap-config.json` and the complete bundle manifest. They remain
Microsoft components and are not licensed under the bstrings license. Release
builders must use an appropriately licensed redistributable directory and
comply with the applicable Microsoft terms.
