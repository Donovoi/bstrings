# bstrings Windows x64 core package

This is the core scanner and acquisition client, not the complete offline kit.
It contains the self-contained bstrings scanner, native Rust engine, and
application-local Visual C++ runtime. Extract the whole ZIP, keep its files
together, and run:

```powershell
.\bstrings.exe --help
```

Core alone supports direct/native extraction and pattern matching. The
one-command [Magika](https://github.com/google/magika),
[FLOSS](https://github.com/mandiant/flare-floss), OCR, language-triage, and
local-translation workflow requires a complete kit. Download one
`bundle-packs-<profile>.json` from the same trusted release, then use the core.
These commands require the v1.9.0 acquisition client; older public core ZIPs
must not be combined with the new manifests.

```powershell
.\bstrings.exe bundle acquire `
  --manifest .\bundle-packs-quality.json `
  --output C:\Tools\bstrings-quality

C:\Tools\bstrings-quality\bstrings.exe bundle verify
```

Copy the entire assembled directory to the offline host and run its root
`bstrings.exe`; do not copy only the EXE. Full instructions are in the
[main README](https://github.com/Donovoi/bstrings/blob/v1.9.0/README.md#download-verify-run-then-enable-everything).

The bstrings license is in `LICENSE.md`; dependency attribution is in
`THIRD_PARTY_NOTICES.md` and `licenses/`.
