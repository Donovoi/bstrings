# bstrings v1.9.7 Windows x64 core

This self-contained core is the native scanner component of the complete
v1.9.7 release. It includes the managed application, native Rust engine,
CPU/CUDA/hybrid backend selection, 66 built-in patterns, and the integrated
native-only forensic report workflow. Keep every extracted file together.

The core ZIP alone does not contain FLOSS, Magika, OCR runtimes/models, or local
translation assets. Normal users who want every feature should follow the
single `Install-BstringsQuality.ps1`
[v1.9.7 quality installer](https://github.com/Donovoi/bstrings/blob/v1.9.7/README.md#get-started),
which downloads, assembles, and verifies the complete version-matched kit.

For native extraction plus TSV reports and histograms using only this core:

```powershell
.\bstrings.exe analyze -f D:\evidence\memory.raw `
  -o D:\results\memory-strings `
  --recover-executable-strings off --ocr off --translation off `
  --lr all --processor auto
```

For the legacy flat-output interface:

```powershell
.\bstrings.exe -f D:\evidence\memory.raw --lr all --ro --off `
  -o D:\results\memory-hits.csv
```

Run `.\bstrings.exe --help` or `.\bstrings.exe analyze --help` for the complete
core command reference. Long-running extraction, bundle, and integrated
analysis operations print measured percentage completion; these percentages
represent completed work units rather than estimated remaining time. Download
details are in the
[online installation guide](https://github.com/Donovoi/bstrings/blob/v1.9.7/docs/download-and-install.md).

The bstrings licence is in `LICENSE.md`; dependency attribution is in
`THIRD_PARTY_NOTICES.md` and `licenses/`.
