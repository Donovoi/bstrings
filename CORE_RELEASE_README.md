# bstrings v1.9.1 Windows x64 core component

This ZIP is an installer component, not the complete offline kit. Normal users
should follow the single `Install-BstringsQuality.ps1`
[quality installer](https://github.com/Donovoi/bstrings/blob/v1.9.1/README.md#get-started),
which downloads, assembles, and verifies every required runtime, model, tool,
licence, and manifest.

The core contains the self-contained scanner, native Rust engine, and the
integrity-checking acquisition client used by that installer. Keep all extracted
files together. For core diagnostics only:

```powershell
.\bstrings.exe --help
```

The bstrings licence is in `LICENSE.md`; dependency attribution is in
`THIRD_PARTY_NOTICES.md` and `licenses/`.
