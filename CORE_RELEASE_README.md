# bstrings v1.9.4 Windows x64 core

This self-contained core is the current native scanner release. It includes the
managed application, native Rust engine, CPU/CUDA/hybrid backend selection, 66
built-in patterns, and the integrated native-only forensic report workflow.
Keep every extracted file together.

It is not the complete offline enrichment kit. FLOSS, Magika, OCR
runtimes/models, and local translation assets are not in this ZIP, so
`analyze --full` is not a supported command for this package alone. The latest
published complete quality kit remains
[v1.9.2](https://github.com/Donovoi/bstrings/blob/v1.9.2/README.md#get-started)
and does not contain changes introduced in v1.9.4. Do not mix files or manifests
between the two versions.

For native extraction plus the v1.9.4 TSV reports and histograms:

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
core command reference. Download/channel details are in the
[online installation guide](https://github.com/Donovoi/bstrings/blob/master/docs/download-and-install.md).

The bstrings licence is in `LICENSE.md`; dependency attribution is in
`THIRD_PARTY_NOTICES.md` and `licenses/`.
