# Structured regex-streaming hits

## Outcome

Regex-only streaming now carries extracted text and its numeric byte offset as
separate values from extraction through pattern matching. The previous path
formatted `offset + tab + data` during extraction and immediately split that
string back into offset and data strings before matching.

The improvement is workload-sensitive because it saves work per extracted
string, not per input byte. On the dense 256 MiB fixture, the candidate won all
nine alternating pairs: median elapsed time fell from 3.9671 to 3.6950 seconds,
a 1.074x speedup and 6.86 percent reduction. The 64 MiB and 1 GiB fixtures were
effectively neutral by mean time; their median swings were within 2.4 percent
in opposite directions.

All 38 measured processes reproduced the complete canonical output for their
fixture. The optimized path is therefore enabled only for the bounded
regex-only streaming mode that benefits from the structured contract. Raw,
literal, CSV, sorted, non-regex-only, and in-memory compatibility paths retain
their established behavior.

## Implementation

`ExtractedStringHit` holds the decoded string and an absolute numeric offset.
CPU, Rust-backed ASCII, CUDA, and hybrid extraction can all produce this form.
The existing string-returning APIs remain intact for compatibility.

During regex-only streaming:

- no combined `0xOFFSET<TAB>data` string is created;
- `ParseHit` is skipped;
- the hexadecimal offset is formatted at most once per extracted string;
- strings that match no requested pattern never allocate a formatted offset;
- main and boundary chunks use the same structured representation because the
  old two-space boundary prefix was presentation state, not evidence data;
- timeout retries still buffer and format records with the same offsets.

Focused tests compare legacy and structured record output, omitted-offset
behavior, pipeline ownership, ASCII and UTF-16LE offsets, and live CUDA/CPU
parity when CUDA is available.

## Benchmark method

The benchmark used the real Release CLI with CPU extraction, the managed ASCII
engine, both encodings, 16 MiB chunks, all 33 built-in patterns, regex-only
output, and byte offsets:

```powershell
dotnet bstrings.dll -f <fixture> -m 3 -b 16 --lr all --ro --off `
  -s -o <output.txt> -q --processor cpu --cpu-engine dotnet
```

Baseline and candidate processes alternated order. The 64 MiB and 1 GiB
fixtures used five pairs; the initially noisy 256 MiB result was expanded to
nine pairs. Timing included startup, mapping, extraction, decoding, matching,
boundary recovery, and output. Every output was sorted with the C locale and
hashed to validate the complete multiset.

| Corpus | Extracted record length | Baseline median | Candidate median | Median speedup | Mean change | Exact runs |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Dense mixed 64 MiB | 512 | 2.1181 s | 2.1677 s | 0.977x | -0.11% | 10/10 |
| Dense mixed 256 MiB | 512 | 3.9671 s | 3.6950 s | 1.074x | +6.98% | 18/18 |
| Dense mixed 1 GiB | 4,096 | 7.0665 s | 7.1949 s | 0.982x | +1.00% | 10/10 |

Positive mean change means less elapsed time. Machine-readable measurements
are in
[`benchmarks/results/structured-hit-streaming-2026-08.csv`](../benchmarks/results/structured-hit-streaming-2026-08.csv).

These are warm-cache measurements from one Windows host and deterministic
synthetic fixtures. The clear gain is for workloads with many extracted
strings; long-record input has fewer extraction and parsing events per byte,
so regex and I/O dominate and the change is neutral.
