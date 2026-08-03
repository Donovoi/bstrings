# Rust ASCII engine prototype

## Decision

The first Rust component is worth keeping as an opt-in engine, but it should
not replace the C#/.NET default yet.

Rust owns one narrow operation: finding inclusive-range ASCII/code-page byte
runs and returning their start and length. The real extraction path now reads
those hits directly from a caller-owned pooled buffer. C# still owns decoding,
file offsets, chunk-boundary ownership, regex matching, output, raw-disk access,
UTF-16LE extraction, and CUDA orchestration.

Removing the last pooled-buffer-to-`List` copy changed the focused result from
9 Rust wins in 12 cells to 12 wins in 12. Speedups ranged from 1.06x to 2.77x.
The deliberately fragmented cases, previously Rust's three losses, improved to
2.13x through 2.77x because the hot path no longer allocates and populates one
managed record per native hit.

The real CLI result is more restrained. On fresh, validated scale corpora, the
1 GiB medians were effectively tied and Rust was 1.04x faster at 10 GiB. Every
run returned the exact expected marker multiset and the engines' canonicalized
outputs were identical. These are encouraging single-host results, not a
universal performance claim. The default remains `--cpu-engine dotnet`;
`rust` is explicit, and `auto` remains an availability fallback rather than a
workload-based performance selector.

## What was built

- A Rust 1.95 `cdylib` under `native/bstrings_core`.
- Runtime AVX2, SSE2, and scalar dispatch.
- Uniform all-valid and all-invalid SIMD mask fast paths.
- A versioned C ABI using caller-owned hit storage.
- Pooled C# buffers with an exact-capacity retry for unusually dense results.
- Direct pooled-hit materialization in the production ASCII extraction path.
- An ABI check and deterministic managed-versus-Rust startup parity gate.
- Strict `rust` failure and safe `auto` fallback behavior.
- Rust unit tests, C# interop tests, real CLI output parity, CI build steps, and
  deterministic focused and scale benchmarks.
- A single-file Windows x64 publish that embeds the native library and loads it
  through the runtime extraction path.

The first ABI transferred a Rust `Vec` and then copied every hit into a C#
`List`; it won only 3 of 12 focused cells. ABI 2 changed to caller-owned pooled
storage and won 9 of 12. The current production path consumes that storage
directly and wins all 12. This history is why the benchmark includes the native
boundary and managed consumption rather than timing only an isolated Rust loop.

## Focused benchmark

Each cell scanned at least 256 MiB per timed sample for seven alternating-order
rounds. Before timing, the engines had to return the same ordered start, length,
and file-offset records. The table reports median throughput.

| Workload | Chunk | C#/.NET | Rust | Rust speedup |
| --- | ---: | ---: | ---: | ---: |
| Sparse binary | 64 KiB | 12,888.8 MiB/s | 29,061.5 MiB/s | 2.25x |
| Sparse binary | 1 MiB | 22,455.9 MiB/s | 32,130.5 MiB/s | 1.43x |
| Sparse binary | 16 MiB | 12,978.7 MiB/s | 13,741.7 MiB/s | 1.06x |
| Random bytes | 64 KiB | 533.9 MiB/s | 865.0 MiB/s | 1.62x |
| Random bytes | 1 MiB | 487.1 MiB/s | 629.4 MiB/s | 1.29x |
| Random bytes | 16 MiB | 465.9 MiB/s | 616.1 MiB/s | 1.32x |
| Dense ASCII | 64 KiB | 13,340.9 MiB/s | 35,571.4 MiB/s | 2.67x |
| Dense ASCII | 1 MiB | 18,109.0 MiB/s | 36,297.1 MiB/s | 2.00x |
| Dense ASCII | 16 MiB | 10,217.4 MiB/s | 13,185.5 MiB/s | 1.29x |
| Fragmented ASCII | 64 KiB | 4,621.8 MiB/s | 10,362.0 MiB/s | 2.24x |
| Fragmented ASCII | 1 MiB | 4,009.0 MiB/s | 11,112.6 MiB/s | 2.77x |
| Fragmented ASCII | 16 MiB | 3,553.3 MiB/s | 7,555.6 MiB/s | 2.13x |

The machine-readable results are in
[`benchmarks/results/ascii-engine-rust-prototype-2026-08.csv`](../benchmarks/results/ascii-engine-rust-prototype-2026-08.csv).

## End-to-end validation

The scale-corpus generator created fresh 1 GiB and 10 GiB binaries with known
interior, 16 MiB boundary-crossing, and terminal URL/email records. Runs were
alternated between engines. The 1 GiB tier used five runs per engine and the
10 GiB tier used three.

| Corpus | Engine | Runs | Median | Median throughput | Rust speedup | Exact runs |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 1 GiB | C#/.NET | 5 | 0.4953 s | 2,067.4 MiB/s | 1.00x | 5/5 |
| 1 GiB | Rust ABI 2 | 5 | 0.4949 s | 2,069.1 MiB/s | 1.00x | 5/5 |
| 10 GiB | C#/.NET | 3 | 3.0385 s | 3,370.0 MiB/s | 1.00x | 3/3 |
| 10 GiB | Rust ABI 2 | 3 | 2.9284 s | 3,496.8 MiB/s | 1.04x | 3/3 |

Every run exited zero, verified the full input byte length, returned exactly
256 or 2,560 rows as appropriate, and reproduced every expected marker ID with
the right multiplicity. Parallel chunk scheduling changes raw line order, so
raw output hashes vary. Sorting the complete output produces one canonical hash
per tier shared by every C#/.NET and Rust run, which also verifies equal values
and reported offsets across engines.

The corpus hashes were
`6b83d7acc6030a323a4374bf83e9b55e6f49dcff6a32116b9af114d2690c24f5`
for 1 GiB and
`97f97e9af9779178637c0bfd494fb6c6bff1f36803eaebecdf6ec7630ecc4d0a`
for 10 GiB. The raw measurements are in
[`benchmarks/results/rust-engine-scale-2026-08.csv`](../benchmarks/results/rust-engine-scale-2026-08.csv),
and the reusable runner is
[`benchmarks/Invoke-RustEngineScaleBenchmark.ps1`](../benchmarks/Invoke-RustEngineScaleBenchmark.ps1).

The scale test is sparse and warm-cache on the reviewed host. Process startup,
file reading, string decoding, regex work, scheduling, and output dominate more
of the result than they do in the focused scanner benchmark.

## Correctness gates

- Rust's dispatched implementation matches a scalar Rust reference for every
  input length from 0 through 4,096 across five byte ranges.
- C# interop tests compare Rust with the established managed engine at scalar,
  SSE2, and AVX2 boundaries through 65,537-byte inputs, including invalid
  ranges, maximum-length truncation, unlimited lengths, and large offsets.
- Direct materialization is tested for chunk ownership, absolute offsets,
  decoding, pooled-buffer disposal, and exact-capacity retry behavior.
- Application startup checks ABI 2 and repeats deterministic cross-engine
  parity before enabling Rust.
- The real CLI passed strict Rust startup and exact 1 GiB/10 GiB output gates.
- The end-to-end scale runner checks every expected interior, boundary, and
  terminal marker rather than trusting exit status or aggregate count alone.

## Next sensible slice

Do not port regexes, raw disks, or CUDA next. Validate this component on another
x64 CPU, ideally covering a non-AVX2 dispatch path, before changing the default.
If that remains exact and useful, UTF-16LE span discovery is the next narrow
Rust experiment. It should use the same pooled ownership boundary, remain
independently selectable, and pass the same scalar, chunk-boundary, and real-CLI
parity gates before it is retained.
