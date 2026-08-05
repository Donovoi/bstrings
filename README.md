# bstrings

`bstrings` finds useful forensic strings, validates identifiers, recovers
obfuscated strings from executables, reads images and PDFs, detects language,
translates locally when needed, and runs the same pattern catalogue over every
result. A complete Windows kit built from current source works without internet
access. Examiners use one interface—`bstrings.exe`—without installing Python or
calling helper tools.

## Start here

| Your goal | What to use | Command |
| --- | --- | --- |
| Run every stage over carved files | Complete offline kit | `bstrings.exe analyze -d D:\carved --full -o D:\results\full` |
| OCR images and PDFs | Complete offline kit | `bstrings.exe analyze -d D:\documents --ocr auto --ocr-provider auto -o D:\results\ocr` |
| Inventory languages without translating | Complete offline kit | `bstrings.exe analyze -d D:\carved --translation detect-only -o D:\results\languages` |
| Search raw disk or memory bytes | Core ZIP or complete kit | `bstrings.exe -f D:\evidence\image.raw --lr all --ro --off -s -o D:\results\hits.csv` |

Directory analysis is recursive. Use a new output directory outside the input
tree so generated results cannot become input to the same run.

The executable is the interface, not the whole payload. Keep it beside the
verified `runtime`, `models`, `tools`, `licenses`, configuration, and manifest
files assembled for the selected profile.

## Why choose this fork?

| Decision | Donovoi/bstrings | [Original bstrings](https://github.com/EricZimmerman/bstrings) | [ripgrep](https://github.com/BurntSushi/ripgrep) | [bulk_extractor](https://github.com/simsong/bulk_extractor) |
| --- | --- | --- | --- | --- |
| 100 GiB sparse URL/email scan | **42.19 s / 2,427 MiB/s** | 187.66 s / 545.7 MiB/s | 180.77 s / 566.5 MiB/s | 319.34 s / 320.7 MiB/s |
| Reviewed 33-pattern exactness at 256 MiB | **33/33** | 15/33 | 25/33 stream-comparable | 0/33 through find/RE2 |
| Shape-correct invalid inputs rejected | **73,480/73,480** | Regex baseline retained all | Not measured | Scanner semantics differ |
| Integrated analysis | **51 validated patterns, FLOSS, OCR, language triage, offline translation, and provenance** | Native strings and a smaller legacy regex catalogue | Search only | Recursive decoding and carving |

These are synthetic warm-cache measurements, not universal rankings. Results
were accepted only after exact records, offsets, boundaries, and invalid cases
matched. Read the [scale](docs/scale-benchmark-2026-08.md),
[engine](docs/pattern-engine-benchmark-2026-08.md),
[validity](docs/pattern-validity-review-2026-08.md), and
[translation](docs/translation-benchmark-2026-08-04.md) reports before applying
them to another host or corpus.

## Choose what to download

| Package | What it does |
| --- | --- |
| `bstrings-win-x64.zip` | Core scanner, Rust engine, and acquisition client. The v1.9.0 build can extract and pattern-match directly, but does not contain every enrichment model/runtime. |
| Complete offline kit | A new directory created by the v1.9.0 `bundle acquire` command. It adds [Magika](https://github.com/google/magika), [FLOSS](https://github.com/mandiant/flare-floss), OCR, language detection, and local translation. Copy the whole directory offline. |

The complete-kit assets are prepared for v1.9.0 but are not in the current
public releases yet. Until that release exists, build from current source using
the [maintainer guide](docs/offline-release-maintenance.md). Do not combine an
older core ZIP with current manifests.

Once a [release](https://github.com/Donovoi/bstrings/releases) contains the core
ZIP, the offline base, and all `bundle-packs-*.json` assets, download the core
and one trust manifest from that same release. On a connected staging machine:

```powershell
.\bstrings.exe bundle acquire `
  --manifest .\bundle-packs-quality.json `
  --output C:\Tools\bstrings-quality

C:\Tools\bstrings-quality\bstrings.exe bundle verify
C:\Tools\bstrings-quality\bstrings.exe analyze -d D:\carved --full -o D:\results\full
```

Choose `quality` for the strongest measured local translation, `balanced` for
a much smaller Q8 model, or `compact` for the smallest and fastest reviewed
profile:

| Profile | Translation model | Model bytes |
| --- | --- | ---: |
| `quality` (default) | Hy-MT2-7B Q8_0 | 7,981,928,896 |
| `balanced` | Hy-MT2-1.8B Q8_0 | 1,908,528,192 |
| `compact` | Hy-MT2-1.8B Q4_K_M | 1,133,080,448 |

The selected model remains at its immutable, commit-pinned
[Hy-MT2 7B](https://huggingface.co/tencent/Hy-MT2-7B-GGUF) or
[Hy-MT2 1.8B](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) source.
`bundle acquire` resumes downloads, verifies exact sizes and SHA-256 values,
assembles the kit, and verifies the finished manifest. Nothing is downloaded
during examination. Authenticate the trust manifest through an independently
trusted release channel; checksums detect changed bytes but do not identify the
publisher by themselves.

## What `--full` does

The pipeline runs native extraction, Magika-guided executable handling, FLOSS,
OCR, language probability checks, selective local translation, every built-in
pattern, and completion/provenance reporting. OCR finishes before translation,
so translated OCR text is also searched.

`--full` does not mount filesystems or carve embedded files from a raw disk or
memory image. Mount or carve first when filesystem coverage is required. A
direct raw-image scan still finds byte patterns, while FLOSS needs the recovered
executable and OCR needs supported image/PDF files.

The v1.9.0 source profile supports CPU, DirectML, and DirectML+CPU hybrid paths;
its complete-kit assets are not published yet. CUDA is not bundled or claimed.
`auto` is the normal choice. Use CPU when GPU memory is busy or stability
matters. A compatible graphics driver is a host prerequisite and cannot be
bundled truthfully.

## Measured OCR evidence

We published the v3
[616-document SROIE calibration](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-calibration-v3-20260805-e3f4567)
as an immutable release.
It selected 616 of 626 train rows under the frozen duplicate policy and measured
token F1 0.8602, CER 0.1156, WER 0.2181, localization Hmean 0.9787, and exact
end-to-end Hmean 0.6349 on CPU. This is calibration evidence, not test
acceptance.

The predeclared 361-document one-shot attempt failed closed on one degenerate
source annotation before OCR, consumed the one-shot ledger, and was not rerun.
The immutable
[pre-test witness](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-acceptance-v2-20260805-23992fc)
and [terminal result](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-terminal-v2-20260805-23992fc)
preserve that outcome.

A later, explicitly post-hoc
[test-split diagnostic](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-posthoc-v2-20260805-81c0fb2)
audited all 361 test rows, excluded eight train/test overlaps with identical
decoded image bytes, and scored the remaining 353 while retaining repaired
dataset row index 142 (zero-based). CPU, DirectML, and hybrid produced identical
aggregate and per-document scored metrics, and every backend met all 11 frozen
numeric thresholds: token F1 0.8559, CER 0.1167, WER 0.2245, localization Hmean
0.9785, and exact end-to-end Hmean 0.6254. The metric artifacts matched under
both scoring profiles, but some provider-neutral OCR output records did not, so
backend parity was not established. The diagnostic did not pass overall and is
not an acceptance result or an official RRC leaderboard submission. See the
[OCR benchmark record](docs/ocr-benchmark-2026-08-05.md) for the evidence
chain, exact limits, and hashes.

## Results and evidence safety

Integrated runs retain input hashes, original and derived strings, OCR
coordinates/pages, language assessments, translations, regex matches,
`run.json`, and `summary.json`. A run is complete only when both status records
say `complete` and no `.incomplete` marker remains. OCR and translated matches
are leads: verify consequential findings against the original file,
untranslated parent, and surrounding evidence.

## Guides

- [Air-gapped deployment and verification](docs/air-gapped-deployment.md)
- [OCR and document analysis](docs/ocr-and-document-analysis.md)
- [Executable recovery, language triage, and translation](docs/enrichment-pipeline.md)
- [Output, completeness, and provenance](docs/output-and-provenance.md)
- [Offline release maintenance](docs/offline-release-maintenance.md)
- [Pattern validity and false-positive controls](docs/pattern-validity-review-2026-08.md)
- [Cryptocurrency address coverage](docs/crypto-address-coverage-2026-08.md)
- [Magika redistribution](docs/magika-cli-redistribution.md) and
  [FLOSS redistribution](docs/floss-standalone-redistribution.md)

Developer builds require the pinned .NET/Rust toolchains and release-host
utilities; normal users do not. The project remains under its upstream terms
in [LICENSE.md](LICENSE.md), with component attribution in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
