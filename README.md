# bstrings

`bstrings` extracts forensic strings, validates useful identifiers, recovers
obfuscated strings from executables, reads text from images and PDFs, and can
translate likely non-English text before matching it. The complete Windows
bundle is designed for disconnected examinations: the examiner runs one
`bstrings.exe`, with no separate Python commands or dependency installation.

## Why choose this fork?

| Decision point | Donovoi/bstrings | Original bstrings | [ripgrep](https://github.com/BurntSushi/ripgrep) | [bulk_extractor](https://github.com/simsong/bulk_extractor) |
| --- | --- | --- | --- | --- |
| 100 GiB sparse URL/email scan | **42.19 s / 2,427 MiB/s** | 187.66 s / 545.7 MiB/s | 180.77 s / 566.5 MiB/s | 319.34 s / 320.7 MiB/s |
| Reviewed 33-pattern exactness at 256 MiB | **33/33** | 15/33 | 25/33 stream-comparable | 0/33 through find/RE2 |
| Shape-correct invalid corpus | **73,480/73,480 rejected** | Regex baseline retained all | Not measured | Scanner semantics differ |
| Pattern catalog | **51 patterns; 27-pattern `wallets` group with semantic validation** | Smaller legacy catalog | User expressions | Purpose-built scanners |
| Enrichment | Native + [FLOSS](https://github.com/mandiant/flare-floss) + OCR + language triage + local translation | None | None | Recursive decoding/carving, without this parent/child pipeline |
| Extraction hardware paths | SIMD CPU, Rust, optional CUDA, and CPU+GPU hybrid | CPU | CPU | Multi-threaded CPU |

These are synthetic warm-cache measurements, not universal rankings. Timings
were accepted only after exact records and offsets passed boundary tests. Read
the [scale](docs/scale-benchmark-2026-08.md),
[pattern/engine](docs/pattern-engine-benchmark-2026-08.md),
[validity](docs/pattern-validity-review-2026-08.md), and
[translation](docs/translation-benchmark-2026-08-04.md) reports before
generalising them.

## Get a complete offline bundle

From a tagged [release](https://github.com/Donovoi/bstrings/releases), download
the small core ZIP and one trust manifest:

- `bundle-packs-quality.json` — default, best measured translation quality;
- `bundle-packs-balanced.json` — smaller Q8 model; or
- `bundle-packs-compact.json` — smallest and fastest model.

Extract the core ZIP on a connected staging machine, then let the same
executable resume, hash-check, and assemble every required pack:

```powershell
.\bstrings.exe bundle acquire `
  --manifest .\bundle-packs-quality.json `
  --output C:\Tools\bstrings-quality

C:\Tools\bstrings-quality\bstrings.exe bundle verify
```

Transfer the complete output directory to the air-gapped machine. Nothing is
downloaded during examination.

| Profile | Translation model | Model bytes | Intended use |
| --- | --- | ---: | --- |
| `quality` (default) | Hy-MT2-7B Q8_0 | 7,981,928,896 | Highest quality measured by this project |
| `balanced` | Hy-MT2-1.8B Q8_0 | 1,908,528,192 | Much smaller, with higher fidelity than compact |
| `compact` | Hy-MT2-1.8B Q4_K_M | 1,133,080,448 | Smallest transfer and lower memory use |

GitHub receives one shared base pack below its 2 GB per-file limit. The
translation model remains an immutable, size- and SHA-256-gated file from its
official [Hy-MT2 7B](https://huggingface.co/tencent/Hy-MT2-7B-GGUF) or
[Hy-MT2 1.8B](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) repository.
`bundle acquire`
combines those parts into an ordinary complete bundle; the examiner still uses
one executable. A version-tag release is held until quality, balanced, and
compact have each been acquired, assembled, strictly verified, and
translation-smoked; the release includes the
[checked acceptance record](docs/offline-release-maintenance.md#release-ci-gates).

Treat the selected `bundle-packs-*.json` as a trust input and obtain it through
an independently trusted release channel. A checksum published beside the
files detects corruption, but does not authenticate a publisher if an attacker
can replace both.

## Common goals

```powershell
# Native strings, FLOSS, OCR, language triage, local translation, all patterns
.\bstrings.exe analyze -d D:\carved --full -o D:\results\full

# OCR supported images/PDFs only when useful; choose the best bundled provider
.\bstrings.exe analyze -d D:\documents --ocr auto --ocr-provider auto -o D:\results\ocr

# Render and OCR every supported PDF page, including pages with a text layer
.\bstrings.exe analyze -f D:\evidence\scan.pdf --ocr force -o D:\results\scan

# Detect likely language without translating
.\bstrings.exe analyze -d D:\carved --translation detect-only -o D:\results\languages

# Direct byte-pattern scan of a raw disk or memory image
.\bstrings.exe -f D:\evidence\image.raw --lr all --ro --off -s -o D:\results\image-hits.csv
```

Run `.\bstrings.exe analyze --help` for the integrated workflow or
`.\bstrings.exe --help` for direct extraction/search. Directory analysis is
recursive; keep the output directory outside the input tree.

`--full` means every bstrings stage. It does **not** mount filesystems or carve
embedded files from raw disk or memory images. Use an appropriate forensic
mounting/carving tool first when that coverage is required. FLOSS receives an
executable only when the complete executable is supplied as a file.

## What is already bundled

The complete Windows x64 bundle carries the self-contained .NET application,
native Rust scanner, portable CPython runtimes, [Magika](https://github.com/google/magika),
[FLOSS](https://github.com/mandiant/flare-floss), a CPU
[llama.cpp](https://github.com/ggml-org/llama.cpp) runtime, the selected Hy-MT2
model, and an OCR stack built from [RapidOCR](https://github.com/RapidAI/RapidOCR),
[PP-OCRv6](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html),
[PDFium](https://pdfium.googlesource.com/pdfium/),
[OpenCV](https://github.com/opencv/opencv), and
[ONNX Runtime](https://github.com/microsoft/onnxruntime). Models, notices,
licenses, app-local Visual C++ runtime DLLs, dependency inventories, and the
strict file manifest travel with it.

The shipped OCR profile has live-validated CPU, DirectML, and DirectML+CPU
hybrid paths. CUDA OCR is accepted by the general interface but is not bundled
or claimed by this profile. An earlier bounded run found DirectML fastest, but
current release metrics require the hardened benchmark. Other GPU-heavy work
can exhaust graphics memory and make DirectML fail, so use `--ocr-provider cpu`
when stability or resource isolation matters. Hosted CI checks CPU; a release
claims DirectML and hybrid only with its separate self-hosted
hardware-acceptance record. OCR completes before translation starts.

The conservative supported baseline is Windows 11 x64 24H2 or newer. No GPU is
required. A compatible Windows graphics driver is a host prerequisite for
DirectML and cannot be bundled truthfully with the application.

The standard complete profile deliberately ships the reviewed CPU llama.cpp
translation runtime. Translation `cuda`/`hybrid` options require a separately
built and accepted llama.cpp GPU profile plus a compatible host driver; they
are not silently claimed by this bundle.

## Results and evidence safety

Integrated runs keep input hashes, original strings, OCR coordinates and page
numbers, language assessments, recovered/translated children, regex matches,
`run.json`, and `summary.json`. A run is complete only when both status records
say `complete` and no `.incomplete` marker remains. OCR and translated matches
are investigative leads; verify consequential findings against the original
file, untranslated parent, and surrounding evidence.

## Detailed guides

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
tools; examiners do not. The project remains under its upstream terms in
[LICENSE.md](LICENSE.md), with component attribution in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
