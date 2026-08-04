# Offline translation selection gate — 2026-08-04

This is a developer/research reproduction record. Examiners normally use the
integrated `bstrings.exe analyze -d carved-files --full -o results` workflow;
they do not need to invoke Python or the benchmark runner. A raw image must be
mounted or carved first when filesystem or embedded-executable coverage is
required; bstrings does not imply that coverage from `--full`.

## Decision

Use Hy-MT2-1.8B Q8 through llama.cpp for languages Hy-MT2 officially supports.
On the reviewed laptop it produced better aggregate translations than the
existing MADLAD-400-3B-MT path, preserved every tested evidence identifier,
and was 13.1 times faster. Use Q4_K_M when speed or memory matters more than
the last 1–2 chrF++ points. Keep MADLAD as an advanced adapter and benchmark
fallback for languages outside Hy-MT2's much smaller language set. The
integrated `bstrings.exe analyze` workflow does not expose MADLAD selection.

This is a selection gate for `bstrings`, not a claim that one model is best in
every domain or on every machine.

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
  The weights required accepted Gemma access, and the test host was not
  authenticated or approved, so it was not ranked from paper results alone.
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
[Hy-MT2 GGUF model](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF).
Install the benchmark-only dependency and download the same WMT24++ revision.
The benchmark runner never downloads a model. It can own a short-lived,
loopback-only llama.cpp server so the benchmark exercises the same scheduler as
the production adapter.

```powershell
uv pip install "sacrebleu>=2.5,<3"

hf download google/wmt24pp `
  --repo-type dataset `
  --revision fd7405c06494bc66a57b25f55d217a72f96e60dc `
  --local-dir C:\bench\wmt24pp

python tools\enrichment\benchmark_translation.py `
  --engine llama-cpp `
  --llama-server C:\forensic-tools\llama.cpp\llama-server.exe `
  --model-path C:\forensic-models\hy-mt2-1.8b\Hy-MT2-1.8B-Q8_0.gguf `
  --model-id tencent/Hy-MT2-1.8B-GGUF `
  --model-revision 1cd5208700acedef4ef93019b6cfc148b8522d45 `
  --model-sha256 5C3FE0B1408A5CEB0143184EF247B11B579C525F4B02B060E6C851BB76FEF1A4 `
  --runtime "llama.cpp b10248; CUDA 12.4; Q8_0" `
  --device auto `
  --parallelism 0 `
  --wmt-root C:\bench\wmt24pp `
  --wmt-per-locale 5 `
  --output C:\bench\results\hy-mt2-q8.json
```

Generated result JSON/JSONL and downloaded corpora stay outside the repository.
Only the runner and synthetic forensic fixtures are versioned.
