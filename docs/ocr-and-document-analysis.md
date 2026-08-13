# OCR and document analysis

OCR is part of the bstrings kit. The commands in this guide require a verified,
version-matched installation. See [download and installation](download-and-install.md).
Do not copy OCR assets between releases.

The integrated OCR stage turns text from supported images and PDFs into normal
bstrings child records. Those records retain source hashes, page/frame numbers,
coordinates, confidence, render identity, engine/model identity, and execution
provider. They then enter the same language-detection, optional translation,
and regex-matching pipeline as native and FLOSS-recovered strings.

OCR hardware selection is independent of translation hardware. The bstrings kit
can use DirectML for OCR. Full translation separately probes the accepted
Windows sm89 CUDA p2 closure and falls back to tested CPU only before evidence
work. Explicit
CUDA fails closed and cannot switch provider mid-run. OCR still finishes before
translation, so their device policies and progress counters are not simultaneous
utilization targets. See
[ADR-0006](architecture/adr-0006-q4-cuda-full-translation.md).

Examiners use `bstrings.exe`; the Python worker documented here is an internal,
manifest-covered component and is not a separate user command.

## Use it

In the published v1.9.17 kit, `--full` enables OCR in automatic mode and asks the
verified bundle to select a provider:

```powershell
.\bstrings.exe analyze -d D:\evidence\carved --full -o D:\results\full
```

OCR can also be requested without the other optional stages:

```powershell
# OCR images; extract PDF text layers and OCR suspicious/image-only pages
.\bstrings.exe analyze -d D:\documents --ocr auto --ocr-provider auto -o D:\results\auto

# OCR every PDF page even when it already has an apparently usable text layer
.\bstrings.exe analyze -f D:\documents\report.pdf --ocr force -o D:\results\forced

# Explicit CPU execution-provider path (no GPU inference)
.\bstrings.exe analyze -d D:\documents --ocr auto --ocr-provider cpu -o D:\results\cpu

# Explicit DirectML path on a compatible Windows GPU host
.\bstrings.exe analyze -d D:\documents --ocr auto --ocr-provider directml -o D:\results\dml

# Schedule raster jobs over separate DirectML and CPU sessions
.\bstrings.exe analyze -d D:\documents --ocr auto --ocr-provider hybrid -o D:\results\hybrid
```

`--ocr` accepts `off`, `auto`, or `force`. `--ocr-provider` accepts `auto`,
`cpu`, `directml`, `hybrid`, or `cuda`; however, the v1.9.17 source profile
`windows-x64-ocr-cpu-directml-v1` contains and claims only CPU, DirectML, and
DirectML+CPU hybrid. CUDA requires a separately built and validated custom
runtime profile.

The analysis console first prints measured content-triage completion for every
fixed input. OCR and routed FLOSS then report their smaller candidate totals,
for example `Progress: offline OCR: 37.5% (3/8 files)`. These are completed work
units, not elapsed-time estimates; one scanned PDF can take much longer than
one small image.

## File and PDF behavior

Early routing recognizes PDFs by Magika, header, or `.pdf` suffix and images by
Magika, common file signatures, or these suffixes: BMP, GIF, ICO, JFIF/JPEG,
PNG, TIFF, and WebP. The OCR worker independently re-sniffs every routed
candidate before loading its pages. Multi-frame images are processed frame by
frame. Non-candidates remain explicit in `content-routing.jsonl`; conservative
false-positive candidates receive a `not-applicable` OCR assessment.

For every PDF page, bstrings first extracts the born-digital text layer through
[PDFium](https://pdfium.googlesource.com/pdfium/) and records non-empty text with
PDF-point coordinates. In `auto` mode, it renders a page at 300 DPI only when
the text layer is short or suspicious: fewer than 32 non-whitespace characters,
more than 1% replacement characters, or more than 1% unexpected control
characters. In `force` mode it also renders and OCRs pages with a usable text
layer. Image files are always raster OCR inputs once OCR is enabled.

Automatic mode is the sensible default for large mixed datasets: it avoids
paying the raster cost for ordinary searchable PDFs while still finding text
in image-only or damaged pages. Force mode is appropriate when the visual page
may disagree with, conceal, or supplement the embedded text layer.

File size is not the OCR/GPU routing rule. A large born-digital PDF may need no
raster inference, while a small photographed page may be expensive. Routing is
therefore based on format, page text-layer quality, and the explicitly selected
provider—not a 1 GiB threshold.

## Bundled engine and immutable model pack

The v1.9.17 source profile uses
[RapidOCR 3.9.2](https://github.com/RapidAI/RapidOCR/releases/tag/v3.9.2)
as the local orchestration engine, immutable
[PP-OCRv6 medium](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html)
[detector](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx/tree/61323801669c338b7891481ec7bac61ce31b576a)
and [recognizer](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/tree/50c7eacafc52fa7bcf4194e8cd08e46f8558504b)
ONNX models, and RapidOCR's legacy mobile orientation classifier. This is a
versioned composite assembled by bstrings, not an official monolithic
PP-OCRv6 pipeline.
The exact model-pack identity is:

- model ID: `PaddlePaddle/PP-OCRv6-medium-onnx+RapidAI/RapidOCR-classifier`;
- detector revision: `61323801669c338b7891481ec7bac61ce31b576a`;
- recognizer revision: `50c7eacafc52fa7bcf4194e8cd08e46f8558504b`;
- classifier component identity token:
  `390c78b5e9a2e69bd9e33be9cd77f7b2622ce55b`;
- model manifest SHA-256:
  `b3b683eb29ec09e9da835e09fb4470792af7702cfc6cee40f2725c232d258534`.

The worker verifies that manifest and every model component before inference.
It does not allow an opaque model directory or an online model lookup to
substitute different bytes. Runtime packages, models, dictionaries, official
URLs, exact lengths, hashes, inventories, and license materials are frozen in
`tools/airgap/ocr-components.lock.json` and
`licenses/ocr-runtime-win-x64.json`.

The classifier bytes are extracted from the hash-pinned RapidOCR 3.9.2 wheel
(wheel SHA-256 `04d6b8d151f823d930bd91910555f57bea897c0c44fa6794267b94cf9c1ef9a0`).
Their own SHA-256 is
`e47acedf663230f8863ff1ab0e64dd2d82b838fceb5957146dab185a89d6215c`;
the lock also records the independent upstream mirror revision
`7a679896b7722a2e346fd2dd3148b3faaabe5790`. The 40-character classifier token
above is part of the frozen composite revision string, not an upstream source
commit.

The base pack carries two portable Python environments:

- an [ONNX Runtime CPU](https://github.com/microsoft/onnxruntime) environment,
  packaged and verified as a separate fallback; and
- an [ONNX Runtime DirectML](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html)
  environment exposing both DirectML and CPU providers. The integrated CLI uses
  provider-specific sessions in this active environment for CPU, DirectML, and
  hybrid requests.

[Pillow](https://python-pillow.github.io/),
[OpenCV](https://github.com/opencv/opencv), and
[pypdfium2](https://github.com/pypdfium2-team/pypdfium2) are included. The
examiner does not install or call them.

## Language scope

PaddlePaddle's PP-OCRv6 medium recognition model reports 83.2 weighted-average
recognition accuracy on its internal multilingual set, while the medium
detection model reports 86.2 Hmean. Those figures are upstream internal-v6
results and are not directly comparable to older PP-OCR releases or this
project's synthetic identifier gate. See the official
[PP-OCRv6 report](https://www.paddleocr.ai/latest/en/version3.x/algorithm/PP-OCRv6/PP-OCRv6.html)
and immutable [model card](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/tree/50c7eacafc52fa7bcf4194e8cd08e46f8558504b).

The upstream model describes Simplified Chinese, Traditional Chinese, English,
Japanese, and 46 Latin-script languages. Do not turn that into a claim of universal Arabic,
Cyrillic, Devanagari, or Korean coverage. For an unsupported script, retain the
visual original and use a separately validated model/profile. Offline
translation can only work with characters OCR recovered correctly.

## Current v3 calibration and test limits

Result scope: this is a commit-bound, project-defined SROIE Task-2-style
printed-receipt evaluation on the pinned community
[`jsdnrs/ICDAR2019-SROIE`](https://huggingface.co/datasets/jsdnrs/ICDAR2019-SROIE/tree/bffe40c26759f3376ec2b3ae9031dbba54cd587c)
derivative. It is not an official RRC submission, KIE result, universal OCR
score, handwriting test, or proof that the upstream model never trained on
SROIE.

| Evidence | Result |
| --- | --- |
| v3 calibration population | 616 selected train documents from 626 raw rows; 10 conflicting duplicate rows excluded by the frozen image policy |
| v3 calibration | token F1 0.860229; CER 0.115610; WER 0.218109; localization Hmean 0.978661; exact end-to-end Hmean 0.634888 |
| v3 CPU calibration path | 616 documents in 1,305.28 s (0.4719 documents/s on that host); output and metrics were deterministic in both repeated ten-document runs |
| Predeclared test attempt | **No quality result.** The 361-document one-shot attempt failed closed during annotation parsing on one degenerate source box; the ledger was consumed and the test was not rerun. Eight exact train/test image overlaps were identified later. |
| Post-hoc diagnostic population | All 361 test rows audited; eight exact train/test image overlaps excluded; 353 scored; repaired dataset row index 142 retained (zero-based) |
| Post-hoc diagnostic | token F1 0.855862; CER 0.116711; WER 0.224485; localization Hmean 0.978510; exact end-to-end Hmean 0.625437; every backend met all 11 numeric thresholds, but the overall diagnostic failed its integrity gate |
| Post-hoc backend speed | CPU 0.4594 documents/s; DirectML 1.2452 documents/s; hybrid 1.2319 documents/s on that host |
| Post-hoc integrity | **Failed.** Metrics and repeated-run outputs were deterministic, but provider-neutral critical evidence was not exactly equal and confidence observations could not be structurally aligned across providers. This is not acceptance or a parity claim. |

The project gate uses Unicode NFC plus casefold before exact comparison; whitespace
is normalized, while punctuation and token boundaries still matter. The report
also embeds a full NFC, case-sensitive diagnostic computed from the same frozen
OCR output. Casefolding avoids treating capitalization alone as a recognition
failure; it is not evidence that raw case-sensitive recognition improved.

The calibration report, frozen policy, pre-test witness, consumed ledger,
terminal record, post-hoc report, raw OCR output, and host logs are retained as
CI/internal evidence. They are deliberately not product assets on GitHub
Releases. The public summary remains in this guide and the
[OCR benchmark record](ocr-benchmark-2026-08-05.md). The predeclared run stopped
before OCR and produced no model-quality result; the post-hoc report's final
disposition is `diagnostic-integrity-failed`. Neither may be paraphrased as
independent acceptance or full backend parity.

Synthetic fixtures remain useful packaging tests, while release-specific
DirectML acceptance remains hardware/runtime evidence. Neither substitutes for
independent corpus quality evidence. A later DirectML run returned device-loss
error `887A0006` while another model occupied roughly 6.2 of 8.2 GB graphics
memory; the same OCR path passed after that process exited. Avoid competing GPU
work or choose CPU. The integrated pipeline completes OCR before translation.

The primary-source comparison with Microsoft, Google, OpenAI, PaddlePaddle,
Hugging Face models, and independent document benchmarks is recorded in
[document-reading research and roadmap](document-reading-research-2026-08.md).
It supports the current native-text-first design, but also identifies missing
document formats, layout grouping, text-layer reconciliation, and bounded
render/inference pipelining as the next benchmark-gated improvements.

## Output and forensic interpretation

The stage writes:

- `ocr-strings.jsonl`, one normalized text child per PDF text layer or OCR hit;
- `ocr-assessments.jsonl`, one completion/provenance assessment per routed OCR
  candidate;
- `content-routing.jsonl` plus the `ocr-input-*` projection that explains the
  candidate set;
- `engine-status.jsonl`, whose OCR row records selection, terminal status, and
  output count for every routed input; and
- the usual downstream `language-assessments.jsonl`,
  `translated-strings.jsonl`, `regex-matches.jsonl`, `run.json`, and
  `summary.json` when those stages are enabled.

OCR records distinguish `pdf-text` from `ocr`, retain the verified source
length/SHA-256, page number, coordinate space, bounding box, confidence where
available, rendered-raster SHA-256 and DPI, engine/model revisions, runtime
hash, and resolved provider. The orchestrator independently validates record
counts, identities, coordinates, provider selection, source attribution, and
completion before merging OCR strings into downstream matching.

OCR is interpretation, not a replacement for evidence. False recognition and
missed text remain possible. Treat a regex hit on OCR or translated OCR as an
investigative lead, then inspect the page image and original evidence.

## Offline and release verification

The normal integrity check is:

```powershell
.\bstrings.exe bundle verify
```

Administrators and maintainers can also exercise real inference without case
data:

```powershell
.\Verify-AirgapBundle.ps1 -OcrSmoke
```

The smoke regenerates fixed PNG/PDF fixtures, verifies both runtime inventories
and all model/license bytes, runs CPU/DirectML/hybrid self-tests as selected,
and requires exact recovery of the fixed forensic lines from the active
runtime. Air-gap audit hooks and offline environment guards remain enabled.
This is per-path packaging smoke, not cross-provider parity or SROIE quality
evidence.

The default `-OcrSmoke` provider set is CPU, DirectML, and hybrid. A generic
hosted runner must use `-OcrSmokeProviders cpu`; DirectML/hybrid release
acceptance runs manually on the explicitly labeled self-hosted hardware
workflow and produces a build-run-linked evidence artifact. Hosted CPU success
must not be reported as GPU acceptance.

Maintainers build the OCR overlay on a connected host with:

```powershell
.\tools\airgap\Build-OcrComponents.ps1 `
  -DestinationDirectory D:\staging\ocr-components `
  -DownloadCacheDirectory D:\staging\ocr-downloads
```

`Build-OcrComponents.ps1 -DryRun` validates and prints the plan without
downloading. `-ValidateOnly` requires a fully populated exact cache and performs
no network fallback. Any dependency/model change requires a reviewed lock
update, fresh-cache build, structural verification, real CPU and hardware-path
smokes, benchmark review, license/inventory review, and complete offline-bundle
verification before release.
