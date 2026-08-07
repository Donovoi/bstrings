# Offline translation selection gate — 2026-08-04, updated 2026-08-06

Distribution status: the accepted translation profiles are published through
the complete v1.9.5 quality channel. They are not included in the standalone
core ZIP. This document preserves the dated selection evidence; current
acquisition instructions are in the
[download guide](download-and-install.md).

This is a developer/research reproduction record. Examiners normally use the
integrated `bstrings.exe analyze -d carved-files --full -o results` workflow;
they do not need to invoke Python or the benchmark runner. A raw image must be
mounted or carved first when filesystem or embedded-executable coverage is
required; bstrings does not imply that coverage from `--full`.

## Current decision — 2026-08-05

Use [Hy-MT2-7B Q8_0](https://huggingface.co/tencent/Hy-MT2-7B-GGUF)
through llama.cpp as the `quality` profile and default complete bundle. It
cleared the final strict gate with the strongest measured
translation quality while preserving every protected identifier and expected
regex result. Keep Hy-MT2-1.8B Q8_0 as `balanced` and Hy-MT2-1.8B Q4_K_M as
`compact`; they are substantially smaller and faster operational choices.

| Profile | Exact model | Bytes | WMT24++ chrF++ | Forensic chrF++ | Protected identifiers | Pattern precision/recall | Strings/s |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `quality` (default) | Hy-MT2-7B Q8_0 | 7,981,928,896 | **62.6786** | **92.8310** | 22/22 | 1.0 / 1.0 (10/10) | 0.2793 |
| `balanced` | Hy-MT2-1.8B Q8_0 | 1,908,528,192 | 58.6211 | 87.5297 | 22/22 | 1.0 / 1.0 (10/10) | 1.8870 |
| `compact` | Hy-MT2-1.8B Q4_K_M | 1,133,080,448 | 58.1829 | 85.6382 | 22/22 | 1.0 / 1.0 (10/10) | 2.7055 |

The final quality run used strict corpus SHA-256
`9b3ace5991ab616a9dab570b80eb1d6741e41f67c8272817861d6176ae27d213`,
took 257.7479 seconds for 72 strings, and reported no identifier omission,
addition, or duplication, plus zero pattern false positives or false negatives.
The exact promoted model file is `HY-MT2-7B-Q8_0.gguf`, revision
`707464294cf5b2a5a69982855020858ed58cf1d1`, SHA-256
`58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0`.

TranslateGemma remains a research challenger. Gated access was resolved and an
isolated run of Google's official
[BF16 TranslateGemma 4B](https://huggingface.co/google/translategemma-4b-it)
completed all 72 rows
under the air-gap guard. It scored 56.1631 WMT24++ chrF++ and 84.2550 forensic
chrF++, retained 22/22 identifiers with zero invariant deltas, and returned all
10 expected downstream matches with precision/recall/F1 of 1.0. It took
1,374.83 seconds (0.05237 strings/s): lower quality and about 51.7 times slower
than compact Hy-MT2 on this bounded corpus. It is gated, unbundled, and has not
cleared the complete one-executable runtime/packaging/redistribution gate, so it
is not promoted. Its gated Gemma Terms permit redistribution only with the
required terms, NOTICE, and downstream restrictions, so a release pack still
needs a terms-compliant distribution design and may require each builder to
accept access independently. MADLAD remains an advanced benchmark fallback
rather than an integrated CLI choice.

That challenger ran with PyTorch `2.10.0+cu130` (CUDA 13.0), Transformers
`5.14.1`, and Accelerate `1.13.0`, using hybrid CPU/GPU offload on the NVIDIA
GeForce RTX 4060 Laptop GPU with 8 GB VRAM. These versions describe only the
isolated challenger run; they are not dependencies of a shipped bstrings bundle.

Neither of the two
[Google Cloud Translation](https://docs.cloud.google.com/translate/docs/advanced/compare-models)
models was empirically run because the host had no configured Google Cloud
project or credentials. Specifically, neither
[`general/translation-llm`](https://docs.cloud.google.com/translate/docs/translation-llm)
nor [`general/nmt`](https://docs.cloud.google.com/translate/docs/advanced/compare-models)
was measured, and the consumer Google Translate product was not tested. These
local chrF++ results establish neither parity nor superiority versus Google.
Throughput is also specific to this 72-row corpus, runtime, and host.

The harness has a deliberately explicit networked comparison path, but accepts
only public or synthetic rows and requires the operator to acknowledge that
those rows leave the machine. A fair future comparison must use the same
corpus, direction, normalization, seed, and scorer. This standalone setup uses
[uv](https://docs.astral.sh/uv/getting-started/installation/) and the
[`hf` CLI](https://huggingface.co/docs/huggingface_hub/guides/cli):

```powershell
uv venv C:\bench\.venv
$benchPython = 'C:\bench\.venv\Scripts\python.exe'
uv pip install --python $benchPython `
  "sacrebleu>=2.5,<3" `
  "google-auth[requests]>=2,<3"

hf download google/wmt24pp `
  --repo-type dataset `
  --revision fd7405c06494bc66a57b25f55d217a72f96e60dc `
  --local-dir C:\bench\wmt24pp

$projectId = 'replace-with-your-google-cloud-project-id'
foreach ($model in @('general/translation-llm', 'general/nmt')) {
  $modelSlug = $model.Split('/')[-1]
  & $benchPython tools\enrichment\benchmark_translation.py `
    --engine google-cloud `
    --google-project $projectId `
    --google-location global `
    --google-model $model `
    --allow-google-cloud-public-benchmark `
    --wmt-root C:\bench\wmt24pp `
    --forensic-cases tools\enrichment\forensic_translation_cases.jsonl `
    --locales ar_EG de_DE es_MX fa_IR fr_FR hi_IN ja_JP ko_KR ru_RU th_TH tr_TR zh_CN `
    --wmt-direction locale-to-en-postedit `
    --sample-seed 20260805 `
    --wmt-per-locale 5 `
    --output "C:\bench\results\google-$modelSlug.json"
  if ($LASTEXITCODE -ne 0) { throw "Google benchmark failed for $model" }
}
```

Credentials are read from Google Application Default Credentials or
`GOOGLE_OAUTH_ACCESS_TOKEN`; they are never a command-line argument. Do not use
this networked backend with case evidence.

This is a selection gate for `bstrings`, not a claim that one model is best in
every domain, language, workload, or machine. The original 2026-08-04 1.8B and
MADLAD study remains below as historical evidence; where its recommendation
differs, this promotion update supersedes it.

## Original 2026-08-04 study

## What was tested

The benchmark reverses the English-to-language pairs in WMT24++, using each
human translation as source text and the English source as the reference. It
takes the first five non-bad rows from each of 12 locales: Arabic, German,
Mexican Spanish, Persian, French, Hindi, Japanese, Korean, Russian, Thai,
Turkish, and Simplified Chinese. That produces 60 general translation cases.

Twelve synthetic forensic cases add emails, usernames, IP addresses, URLs,
Windows paths, file names, a SHA-256 value, CVE, registry path, host and port,
GUID, and service account. The strings are generic fixtures; no examined case
data appears in the benchmark or repository.

The checked-in runner reports:

- corpus chrF++ for WMT24++ and the forensic fixtures;
- exact, case-sensitive identifier retention;
- total, median, and p95 translation latency;
- strings per second; and
- each hypothesis in a separate JSONL record for review.

The original selection gate used one sequential request at a time. Hy-MT2 used
temperature 0, top-k 1, top-p 1, and seed 1. MADLAD used Transformers' greedy
generation. A second full Q8 run produced zero hypothesis mismatches across all
72 cases. The later parallel gate below shows why greedy settings alone should
not be described as byte-deterministic under continuous batching.

## Host and pinned inputs

- CPU: Intel Core Ultra 9 185H
- RAM: 64 GB
- GPU: NVIDIA GeForce RTX 4060 Laptop GPU, 8,188 MiB VRAM
- [llama.cpp](https://github.com/ggml-org/llama.cpp/releases): release
  `b10243`, commit `563dec81c`, CUDA 12.4 build
- [Transformers](https://huggingface.co/docs/transformers/installation): `4.57.6`
- [PyTorch](https://pytorch.org/get-started/locally/): `2.13.0+cpu`
- [WMT24++](https://huggingface.co/datasets/google/wmt24pp) revision:
  `fd7405c06494bc66a57b25f55d217a72f96e60dc`

| Artifact | Bytes | SHA-256 |
| --- | ---: | --- |
| Hy-MT2 Q8_0 GGUF | 1,908,528,192 | `5C3FE0B1408A5CEB0143184EF247B11B579C525F4B02B060E6C851BB76FEF1A4` |
| Hy-MT2 Q4_K_M GGUF | 1,133,080,448 | `DC5F44FCF1FA496EE7AD725982C0C8C553A4DE00259B53AF84C4B89FB0C06699` |
| MADLAD-400 model.safetensors | 11,761,587,872 | `66FF5F8FCAF92291DA486FDFBD4D5233CEC90E1359348A56E3172C978B3A76D4` |
| llama.cpp CUDA archive | — | `F6BED06F6A03D25AFEB52EC6B13BBCA86F1B6252EE95D1725433CA109A821541` |
| llama.cpp CUDA runtime archive | — | `8C79A9B226DE4B3CACFD1F83D24F962D0773BE79F1E7B75C6AF4DED7E32AE1D6` |

## Results

Translation time excludes model hashing, process startup, and model loading so
the throughput comparison reflects steady examination work.

| Model/runtime | WMT24++ chrF++ | Forensic chrF++ | Identifiers | Translation time | Strings/s | Relative speed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Hy-MT2 Q8_0, llama.cpp/CUDA | 59.7396 | 87.5297 | 22/22 | 55.88 s | 1.2885 | 13.10× |
| Hy-MT2 Q4_K_M, llama.cpp/CUDA | 58.4248 | 86.3056 | 22/22 | 38.81 s | 1.8554 | 18.87× |
| MADLAD-400-3B-MT, Transformers/CPU | 54.6516 | 85.9408 | 22/22 | 732.12 s | 0.0983 | 1.00× |

Q8 beat MADLAD in eight of the 12 WMT language slices. MADLAD scored slightly
higher for Persian, French, Korean, and Turkish; its Arabic result was unusually weak
on this five-row slice. Five rows per language are useful for a bounded
selection gate but too few for broad language-quality claims.

| Locale | Hy-MT2 Q8 | MADLAD |
| --- | ---: | ---: |
| ar_EG | 47.59 | 11.56 |
| de_DE | 63.35 | 58.87 |
| es_MX | 70.52 | 70.17 |
| fa_IR | 56.62 | 56.92 |
| fr_FR | 63.16 | 66.51 |
| hi_IN | 57.97 | 56.67 |
| ja_JP | 57.96 | 47.54 |
| ko_KR | 60.65 | 60.80 |
| ru_RU | 58.92 | 55.88 |
| th_TH | 59.89 | 59.23 |
| tr_TR | 63.67 | 65.05 |
| zh_CN | 56.40 | 53.83 |

## Parallel scheduling optimization gate

The scheduler was re-tested on the same laptop with llama.cpp release `b10248`,
the same pinned Q8 model and WMT24++ revision, and the same 72 cases. The
official CUDA 12.4 archive had SHA-256
`A08EA218EA705C8961E82473044933B50AB4F82818FE974C556925FB5C150785`;
the official CUDA runtime archive retained SHA-256
`8C79A9B226DE4B3CACFD1F83D24F962D0773BE79F1E7B75C6AF4DED7E32AE1D6`.

Translation time again excludes model hashing and startup. “Strict” means one
slot with prompt-cache reuse disabled. Throughput mode uses shared-model slots,
continuous batching, exact prompt-cache reuse, greedy top-1 decoding, and
ordered result reconstruction.

| Q8/CUDA schedule | Runs | WMT24++ chrF++ | Forensic chrF++ | Identifiers | Strings/s | Strict-relative speed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| One slot, strict | 1 | 59.6211 | 87.5297 | 22/22 | 1.293 | 1.00× |
| Two slots | 2 | 59.6713–59.8486 | 87.5297 | 22/22 | 2.040–2.055 | 1.58–1.59× |
| Four slots | 1 | 59.4696 | 87.5297 | 22/22 | 2.440 | 1.89× |

Two slots are the automatic default for models up to 8 GiB. They raised
throughput by roughly 58–59% without a measured quality reduction. Four slots
remain an explicit override because the extra speed coincided with a 0.15
chrF++ reduction against strict mode. The corpus is too small to treat either
movement as a universal quality result, so the decision is conservative.

Parallel scheduling was not byte-reproducible even with greedy decoding. The
first two-slot run differed from strict on 11/72 WMT hypotheses; the repeated
two-slot run also differed from strict on 11/72, and the two parallel runs
differed from each other on 5/72. All differences were in the general WMT set:
the 12 forensic hypotheses and all 22 identifiers were unchanged. Strict mode
is therefore retained for regression gates and examinations where output
repeatability outranks throughput.

### CPU, hybrid, and adaptive paths

A common 16-case slice—one row each for Arabic, German, Mexican Spanish, and
Persian plus all 12 forensic fixtures—exercised every hardware mode.

| Q8 path, two slots unless noted | chrF++ WMT / forensic | Identifiers | Strings/s | CPU-serial relative |
| --- | ---: | ---: | ---: | ---: |
| CPU, one slot | 38.5677 / 87.5297 | 22/22 | 0.919 | 1.00× |
| CPU, two slots | 38.5677 / 87.5297 | 22/22 | 1.095 | 1.19× |
| Hybrid, 12 GPU layers | 38.5677 / 87.5297 | 22/22 | 1.613 | 1.75× |
| Adaptive CUDA | 38.5677 / 87.5297 | 22/22 | 6.217 | 6.76× |

All hypotheses were identical across these four slice runs. The short slice is
faster than the full corpus because its strings are shorter, so its absolute
rate must not be compared directly with the 72-case table. It establishes that
CPU-only, deliberate CPU+GPU, and adaptive GPU paths all function and preserve
the checked quality signals on the reviewed hardware.

### Optimizations accepted and rejected

The implementation follows llama.cpp's documented
[parallel slots and continuous batching](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md):
one loaded model serves ordered concurrent requests, context is budgeted per
slot, and `-ngl auto`, `all`, `0`, or an exact count selects adaptive, GPU,
CPU, or hybrid execution. Exact repeated source strings are translated once,
similar lengths are grouped in bounded windows, and a bounded LRU avoids work
across later windows without collapsing parent provenance.

The length and concurrency choices are also consistent with CTranslate2's
[performance](https://opennmt.net/CTranslate2/performance.html) and
[parallelism](https://opennmt.net/CTranslate2/parallel.html) guidance: favor
inter-request concurrency for volume and batch similarly sized sequences. No
CTranslate2 engine was added because it is not a drop-in runtime for the pinned
GGUF model; changing runtime or converting weights needs its own quality gate.

Transformers documents compilation, optimized attention, quantization,
caching, parallelism, and continuous batching in its
[inference optimization overview](https://huggingface.co/docs/transformers/main/en/optimization_overview).
Those ideas informed the scheduler, but `torch.compile` was not added to the
MADLAD fallback: its warm-up and shape sensitivity need a separate benchmark,
and the validated fast path now uses llama.cpp.

llama.cpp also documents
[speculative decoding](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md).
It was not enabled. A draft model or n-gram predictor changes resource use and
potential scheduling behavior, and no forensic quality gate for that path was
completed. This is the next falsifiable optimization candidate, not an assumed
free speed-up.

## Other candidates

- [TranslateGemma 4B](https://huggingface.co/google/translategemma-4b-it) is a
  serious candidate: Google's report evaluates it across WMT24++ and WMT25.
  Gemma access was subsequently accepted. The official BF16 challenger passed
  the 72-row air-gap/invariant run, but scored 56.1631/84.2550 chrF++ at only
  0.05237 strings/s, behind even compact Hy-MT2's 58.1829/85.6382 at 2.70545
  strings/s. It also has not cleared the integrated-runtime, offline-packaging,
  and redistribution gates. It is therefore not shipped merely on the strength
  of paper results or a standalone run.
- [NLLB-200 distilled 600M](https://huggingface.co/facebook/nllb-200-distilled-600M)
  and [SeamlessM4T v2](https://huggingface.co/facebook/seamless-m4t-v2-large)
  have broad coverage but use CC-BY-NC-4.0 model licenses. They fail the
  deployable-default gate for this open-source tool.
- [MADLAD-400](https://huggingface.co/google/madlad400-3b-mt) remains valuable
  as an advanced adapter and benchmark fallback because its 419-language
  coverage is far broader than Hy-MT2's; it is not an integrated CLI engine.

Primary research sources are the
[Hy-MT2 report](https://arxiv.org/abs/2605.22064),
[TranslateGemma report](https://arxiv.org/abs/2601.09012), and
[WMT24++ paper](https://arxiv.org/abs/2502.12404).

## Reproduce the gate

This advanced reproduction requires [Python](https://www.python.org/downloads/),
[uv](https://docs.astral.sh/uv/getting-started/installation/), the
[`hf` CLI](https://huggingface.co/docs/huggingface_hub/guides/cli),
[SacreBLEU](https://github.com/mjpost/sacrebleu), a pinned
[llama.cpp release](https://github.com/ggml-org/llama.cpp/releases), and the
[Hy-MT2 7B GGUF model](https://huggingface.co/tencent/Hy-MT2-7B-GGUF).
Install the benchmark-only dependency and download the same WMT24++ revision.
The benchmark runner never downloads a model. It can own a short-lived,
loopback-only llama.cpp server so the benchmark exercises the same scheduler as
the production adapter.

```powershell
uv venv C:\bench\.venv
$benchPython = 'C:\bench\.venv\Scripts\python.exe'
uv pip install --python $benchPython "sacrebleu>=2.5,<3"

hf download google/wmt24pp `
  --repo-type dataset `
  --revision fd7405c06494bc66a57b25f55d217a72f96e60dc `
  --local-dir C:\bench\wmt24pp

& $benchPython tools\enrichment\benchmark_translation.py `
  --engine llama-cpp `
  --llama-server C:\Tools\bstrings-quality\runtime\llama\llama-server.exe `
  --model-path C:\Tools\bstrings-quality\models\hy-mt2\HY-MT2-7B-Q8_0.gguf `
  --model-id tencent/Hy-MT2-7B-GGUF `
  --model-revision 707464294cf5b2a5a69982855020858ed58cf1d1 `
  --model-sha256 58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0 `
  --runtime "llama.cpp b10248; CPU; Q8_0" `
  --device cpu `
  --parallelism 1 `
  --strict-determinism `
  --wmt-root C:\bench\wmt24pp `
  --forensic-cases tools\enrichment\forensic_translation_cases.jsonl `
  --locales ar_EG de_DE es_MX fa_IR fr_FR hi_IN ja_JP ko_KR ru_RU th_TH tr_TR zh_CN `
  --wmt-direction locale-to-en-postedit `
  --sample-seed 20260805 `
  --wmt-per-locale 5 `
  --output C:\bench\results\hy-mt2-7b-quality-strict.json
```

Generated result JSON/JSONL and downloaded corpora stay outside the repository.
Only the runner and synthetic forensic fixtures are versioned. Raw summaries
can contain absolute corpus paths; Google runs can contain a project ID/model
resource; records JSONL contains sources, references, and hypotheses. Review
and sanitize any derived publication instead of committing raw benchmark
output or exposing it through public CI logs.
