# bstrings Windows x64 core package

This smaller package contains the self-contained bstrings scanner, its native
Rust engine, and application-local Visual C++ runtime. Extract the whole ZIP,
keep its files together, and run:

```powershell
.\bstrings.exe --help
```

The complete offline CPU artifact is required for the one-command Magika,
FLOSS, and local-translation workflow. Current download and verification
instructions are at https://github.com/Donovoi/bstrings.

The bstrings license is in `LICENSE.md`; dependency attribution is in
`THIRD_PARTY_NOTICES.md` and `licenses/`.
