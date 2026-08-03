# Offline translation selection gate — 2026-08-04

## Decision

Use Hy-MT2-1.8B Q8 through llama.cpp for languages Hy-MT2 officially supports.
On the reviewed laptop it produced better aggregate translations than the
existing MADLAD-400-3B-MT path, preserved every tested evidence identifier,
and was 13.1 times faster. Use Q4_K_M when speed or memory matters more than
the last 1–2 chrF++ points. Keep MADLAD as a fallback for languages outside
Hy-MT2's much smaller language set.

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

Decoding was deterministic. Hy-MT2 used temperature 0, top-k 1, top-p 1, and
seed 1. MADLAD used Transformers' greedy generation. A second full Q8 run
produced zero hypothesis mismatches across all 72 cases.

## Host and pinned inputs

- CPU: Intel Core Ultra 9 185H
- RAM: 64 GB
- GPU: NVIDIA GeForce RTX 4060 Laptop GPU, 8,188 MiB VRAM
- llama.cpp: release `b10243`, commit `563dec81c`, CUDA 12.4 build
- Transformers: `4.57.6`
- PyTorch: `2.13.0+cpu`
- WMT24++ revision: `fd7405c06494bc66a57b25f55d217a72f96e60dc`

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
  because its 419-language coverage is far broader than Hy-MT2's.

Primary research sources are the
[Hy-MT2 report](https://arxiv.org/abs/2605.22064),
[TranslateGemma report](https://arxiv.org/abs/2601.09012), and
[WMT24++ paper](https://arxiv.org/abs/2502.12404).

## Reproduce the gate

Install the benchmark-only dependency and download the same WMT24++ revision.
The benchmark runner never downloads a model and accepts only a loopback
OpenAI-compatible endpoint for the llama.cpp path.

```powershell
uv pip install "sacrebleu>=2.5,<3"

python tools\enrichment\benchmark_translation.py `
  --engine openai `
  --endpoint http://127.0.0.1:18089 `
  --model-id tencent/Hy-MT2-1.8B-GGUF `
  --model-revision 1cd5208700acedef4ef93019b6cfc148b8522d45 `
  --model-sha256 5C3FE0B1408A5CEB0143184EF247B11B579C525F4B02B060E6C851BB76FEF1A4 `
  --runtime "llama.cpp b10243; CUDA; Q8_0" `
  --wmt-root C:\bench\wmt24pp `
  --wmt-per-locale 5 `
  --output C:\bench\results\hy-mt2-q8.json
```

Generated result JSON/JSONL and downloaded corpora stay outside the repository.
Only the runner and synthetic forensic fixtures are versioned.
