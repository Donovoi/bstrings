# Enrichment pipeline

The integrated workflow finds useful text through several complementary paths,
then applies one pattern catalog without losing where each string came from:

```text
input inventory and SHA-256 identity
  -> native string extraction
  -> Magika routing and FLOSS executable recovery
  -> PDF text extraction and OCR
  -> language assessment
  -> selected local translation
  -> built-in and custom regex matching
  -> completion and provenance validation
```

The normal examiner interface is one command:

```powershell
.\bstrings.exe analyze -d D:\evidence\carved --full -o D:\results\case-01
```

The complete bundle contains every worker, runtime, model, and dependency. It
does not ask the user to install or invoke Python, [Magika](https://github.com/google/magika),
[FLOSS](https://github.com/mandiant/flare-floss),
[RapidOCR](https://github.com/RapidAI/RapidOCR), or
[llama.cpp](https://github.com/ggml-org/llama.cpp) separately. It downloads
nothing during examination.

`--full` means every bstrings stage. It does not parse a filesystem or carve
embedded files from a raw disk or memory image. Mount or carve the image first
when filesystem-level or embedded-executable/document coverage is required.
The direct scanner can search raw image bytes, but FLOSS requires a complete
supplied executable and OCR requires a supported image/PDF file.

## What each stage contributes

| Stage | Contribution | Important boundary |
| --- | --- | --- |
| Native extractor | ASCII/Unicode strings with byte offsets, using the selected CPU/Rust/GPU path | It does not reconstruct runtime-decoded values |
| Magika | Probabilistically classifies supplied files and routes likely PE files to FLOSS | It samples file content; it is not complete-byte validation, carving, or malware detection |
| FLOSS | Recovers stack, tight-loop, decoded, and selected language strings from supported PE files | Recovered addresses may be program/virtual locations, not file offsets |
| PDF/OCR | Extracts born-digital PDF text and reads raster text from images/pages | OCR remains probabilistic and script/model dependent |
| Language assessment | Scores whether eligible text likely needs translation | Scores are ranking/gating signals, not calibrated certainty |
| Hy-MT2 | Creates an offline English child while protecting structured identifiers | A translated token is not proof those bytes existed in the source |
| Pattern matcher | Applies the same validated catalog/custom patterns to normalized parents and children | Consequential derived hits must be checked against source evidence |

The orchestrator hashes and inventories inputs before extraction, holds a
verified inventory lease around external stages, verifies inputs again after
processing, validates every child record and count, and publishes results only
after all requested stages complete.

## Useful command combinations

```powershell
# Everything, with automatic OCR and translation decisions
.\bstrings.exe analyze -d D:\carved --full -o D:\results\full

# Recover executable strings and match them, but do not translate
.\bstrings.exe analyze -d D:\executables `
  --recover-executable-strings auto --translation off --lr all `
  -o D:\results\recovered

# OCR images/PDFs, assess language, translate selected records, then match
.\bstrings.exe analyze -d D:\documents `
  --ocr auto --ocr-provider auto `
  --translation auto --translation-policy high-recall `
  -o D:\results\documents

# Inventory likely languages without running translation
.\bstrings.exe analyze -d D:\carved `
  --translation detect-only -o D:\results\languages

# Translate every eligible text record rather than using the confidence gate
.\bstrings.exe analyze -d D:\carved `
  --translation all -o D:\results\translate-all
```

Directory analysis is recursive. Put the output outside the input tree.

## Executable recovery

[Magika](https://github.com/google/magika) probabilistically classifies each
supplied file from sampled content. Automatic recovery invokes
[FLOSS](https://github.com/mandiant/flare-floss) only for Magika's PE
classification, reducing unnecessary expensive attempts. This routing decision
does not prove that every byte was inspected and is not a polyglot, carving, or
malware-detection result.
The bstrings adapter consumes FLOSS JSON from a temporary disk file, validates
its pinned result schema incrementally, normalizes supported categories, and
keeps distinct evidence locations even when the text is identical.

FLOSS static strings are omitted from enrichment by default because native
bstrings already captures them. This avoids duplicate records while retaining
FLOSS's genuinely derived strings. The output transaction fails closed on
unknown categories, duplicate JSON keys, missing required fields, invalid
addresses/encodings, overlong items, malformed UTF-8/JSON, timeout, or
unexpected process failure.

Known 32- or 64-bit shellcode is not guessed from arbitrary data. Advanced
users can deliberately force the corresponding FLOSS format through the
source-tree adapter, but that is a custom workflow and must be documented in
the case. The bundled normal route remains complete-file PE recovery.

The functional integration gate used Mandiant's open
[`flare-floss-testfiles`](https://github.com/mandiant/flare-floss-testfiles)
fixtures. Magika routed the reviewed PE, native extraction did not contain the
test decoded marker, FLOSS recovered the marker at two distinct virtual
addresses, and the regex stage preserved both derived records and locations.
This proves added recovery and lineage on that fixture, not universal malware
recall or performance.

## OCR before translation

OCR is integrated before language assessment so an examiner does not need to
know in advance which image or scanned page contains important non-English
text. PDF text-layer records and OCR records join native/FLOSS records in the
same normalized stream; language triage and regex processing therefore operate
on all of them.

Automatic OCR extracts every non-empty PDF text layer and renders only pages
whose layer is absent, very short, or suspicious. Force mode renders every
page. Images are always OCR inputs when the stage is enabled. The v1.9.0 profile
defines CPU, DirectML, and DirectML+CPU hybrid paths, and each has passed a
per-path inference smoke test. Those smokes do not establish cross-provider
parity or corpus-level quality. CUDA OCR is not part of the profile. See
[OCR and document analysis](ocr-and-document-analysis.md) for formats, exact
models, language scope, performance, output coordinates, and GPU-contention
guidance.

## Automatic language assessment

The bundled [lingua-rs](https://github.com/pemistahl/lingua-rs) detector runs
locally. Eligible records receive the most likely language, confidence,
target-language confidence, next-best confidence, and margin from the target.
That means a massive dataset can be screened probabilistically without the
examiner knowing which strings need translation.

Assessment decisions are explicit:

| Decision | Meaning |
| --- | --- |
| `target-language` | Likely already in the requested target language |
| `translate` | Selected as a non-target-language candidate |
| `ambiguous` | Did not clear the configured confidence/margin policy |
| `non-linguistic` | Outside text/letter bounds for useful language inference |
| `already-derived` | Already a translated child; recursion is prevented |
| `detector-failed` | Detector could not decide; high-recall policy keeps it as a candidate |

`--language-detection adaptive` samples the workload and selects the accurate
or fast local detector profile. `--translation-policy high-recall` is the
default and is appropriate when missing a foreign-language sentence costs more
than translating extra candidates. `balanced` and `high-precision` apply the
configured `--language-confidence` and `--language-margin` thresholds.

These normalized confidences are not universally calibrated probabilities.
Short strings, names, mixed-language text, OCR errors, and transliteration are
hard cases. Use `--translation detect-only` to review the distribution, or
`--translation all` when the cost is acceptable and the gate should not decide.

Every assessment records detector version/profile, policy, thresholds,
confidence values, decision, and source record ID.

## Offline translation profiles

The complete bundle uses the official
[Hy-MT2 7B](https://huggingface.co/tencent/Hy-MT2-7B-GGUF) and
[Hy-MT2 1.8B](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF) GGUF repositories
through a private local llama.cpp server. Three profile manifests are defined
in current source:

| Profile | Model | Bytes | WMT24++ chrF++ | Forensic chrF++ | Identifiers | Strings/s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `quality` | Hy-MT2-7B Q8_0 | 7,981,928,896 | **62.6786** | **92.8310** | 22/22 | 0.2793 |
| `balanced` | Hy-MT2-1.8B Q8_0 | 1,908,528,192 | 58.6211 | 87.5297 | 22/22 | 1.8870 |
| `compact` | Hy-MT2-1.8B Q4_K_M | 1,133,080,448 | 58.1829 | 85.6382 | 22/22 | 2.7055 |

Those results use the strict synthetic/attribution-safe gate described in the
[translation report](translation-benchmark-2026-08-04.md). Quality is the
default because it produced the strongest scores, not because it is fastest or
best for every language/domain. Balanced and compact keep the same protected
identifier gate with much smaller files and higher throughput.

TranslateGemma remains a research challenger, not the production one-executable
engine. After gated access was accepted, the official BF16 4B model completed
the same 72-row air-gap gate with 22/22 identifiers and 10/10 pattern matches,
but scored 56.1631/84.2550 chrF++ at 0.05237 strings/s. That was lower quality
and about 51.7 times slower than compact Hy-MT2 on this bounded corpus. It also
has not cleared the complete integrated-runtime and offline-packaging gates.
It is distributed under the gated
[Gemma Terms](https://ai.google.dev/gemma/terms), so a redistributable pack must
carry the terms and NOTICE and preserve their downstream restrictions; each
builder may need to accept access independently. MADLAD-400 remains a
developer/advanced benchmark
fallback for language-coverage research, but it is not selectable through the
integrated examiner CLI.

### Hardware selection and scheduling

`--translation-device` accepts `auto`, `cpu`, `cuda`, or `hybrid`:

The standard split-pack bundle contains the reviewed CPU llama.cpp runtime, so
its normal resolved path is CPU. The remaining choices describe supported
custom runtime profiles; they require a separately built/accepted CUDA-capable
llama.cpp closure and a compatible host driver.

- `auto` uses adaptive GPU offload when the bundled runtime sees compatible
  CUDA support; otherwise it uses CPU;
- `cpu` forces zero GPU layers and needs no graphics hardware;
- `cuda` requires a validated CUDA runtime/driver and full model offload; and
- `hybrid` requires an explicit positive `--translation-gpu-layers` count so
  the CPU/GPU split is auditable.

`--translation-parallelism 0` selects conservative slots from hardware and
model size. The adapter processes bounded windows, groups similar lengths,
deduplicates exact source text for inference, and reuses a bounded cache, while
still writing a separate child for every parent. One model server is shared
across ordered concurrent requests.

Hy-MT2's model card recommends stochastic decoding (`temperature 0.7`,
`top-p 0.6`, `top-k 20`) for general use. Bstrings deliberately uses the
forensic benchmark's greedy profile (`temperature 0`, `top-k 1`) to reduce
run-to-run variation and protect evidence attribution; this is a project
choice, not the upstream default.

Parallel greedy inference is not promised to be byte-identical because
continuous-batching schedules can alter floating-point accumulation at close
token choices. `--translation-strict-determinism` forces one slot and disables
prompt-cache reuse for the maximum-repeatability path. It does not promise
identical output across different drivers or hardware.

OCR completes before translation in the integrated pipeline. This prevents
bstrings' own OCR DirectML runtime and a large translation model from fighting
for VRAM. Other GPU-heavy processes can still cause device loss or allocation
failure; choose CPU or isolate the workstation workload when needed.

### Identifier and completion gates

Before a translated child is committed, the adapter checks exact retention of
structured evidence tokens including emails, URLs, IP addresses, hashes,
Windows/registry paths, CVEs, GUIDs, host/port values, common filenames,
hyphenated/underscored identifiers, and placeholders. A missing or changed
protected token aborts the output transaction.

The local server must return exactly one terminal `stop` choice and the expected
prompt-token count. Empty, truncated, missing, extra, or duplicated translations
fail. An unchanged successful translation stays in the audit trail with
`outcome: "unchanged"`.

The server binds only to loopback, the web UI/reasoning output is disabled, the
exact model is hashed before loading, Hugging Face/Transformers/package-manager
offline variables are forced, and the adapter's audit hook rejects non-loopback
DNS/socket use. Each child records the model ID/revision/hash, llama.cpp
version, device path, GPU-layer policy, slots/threads, and `execution.airgap`.

## Matching and lineage

Native, recovered, OCR, PDF-text, and translated records are merged in a stable
order before matching. Built-in `--lr` groups and custom `--fr` patterns use
the same validation semantics on each normalized record. The matcher does not
rewrite or discard parent records.

Each record has a stable SHA-256-based ID. Derived records identify their
parent, source file, location kind, extractor/model/runtime identity, and
evidence class. Regex matches copy that lineage. Parents must precede children;
duplicate, missing, out-of-order, unattributed, or structurally inconsistent
records fail the run.

A translated match can normalize punctuation or create a token that resembles
an email, path, hash, or wallet. An OCR match can contain recognition errors.
Treat both as leads. Verify consequential results against the untranslated
parent, page/executable context, and original evidence.

The important result files are:

- `native-strings.jsonl`, `recovered-strings.jsonl`, and `ocr-strings.jsonl`;
- `ocr-assessments.jsonl` and `language-assessments.jsonl`;
- `translated-strings.jsonl`, `enriched-strings.jsonl`, and
  `regex-matches.jsonl`;
- `input-manifest.jsonl`, `run.json`, and `summary.json`; and
- `.incomplete` while work is still in progress.

A result is complete only when both status documents say `complete` and the
`.incomplete` marker has been removed. See [output and provenance](output-and-provenance.md)
for schema and interpretation detail.

## Maintainer and regression entry points

Examiners should not call the Python adapters. Maintainers can inspect them
under `tools/enrichment/`, and must use pinned disposable environments plus
exact model/package/license records when changing them. Official acquisition
links are:

- [CPython embeddable package](https://docs.python.org/3/using/windows.html#the-embeddable-package);
- [Magika CLI](https://github.com/google/magika#command-line-tool);
- [FLOSS releases](https://github.com/mandiant/flare-floss/releases);
- [llama.cpp source/releases](https://github.com/ggml-org/llama.cpp);
- [RapidOCR](https://github.com/RapidAI/RapidOCR) and
  [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR); and
- [Hugging Face `hf` CLI](https://huggingface.co/docs/huggingface_hub/guides/cli).

Run source-tree regression gates with:

```powershell
dotnet test bstrings.Tests\bstrings.Tests.csproj --configuration Release
python -m unittest discover -s tools\enrichment\tests -v
```

Release acceptance additionally builds from empty component caches, verifies
all inventories/licenses, exercises real OCR and translation, assembles the
split packs locally, and verifies the finished bundle without network fallback.
