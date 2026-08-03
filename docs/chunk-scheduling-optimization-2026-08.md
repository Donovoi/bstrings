# CPU chunk scheduling optimization

## Outcome

The next measured optimization target was CPU pipeline scheduling, not another
Rust port. Sampled real-CLI traces showed the ASCII and UTF-16LE scanners each
using only a small share of total thread time while worker, I/O-completion, and
thread-pool waits dominated. The automatic policy was creating too few large
chunks to hide those stalls.

The CPU policy now targets 32 chunks per worker instead of two. On the reviewed
22-worker host this changes automatic sizing from 128 MiB to 16 MiB for the
10 GiB corpus, and from 24 MiB to 16 MiB for the 1 GiB corpus. GPU-only sizing
keeps its separate explicit policy.

## Correctness issue found during tuning

The first chunk-size sweep exposed a more important problem: an 8 MiB run
returned 1,280 rows where the same 10 GiB corpus should return 2,560. Every
missing record began exactly at an 8 MiB chunk boundary.

Primary chunks intentionally suppress strings touching their outer edges so a
clipped fragment is never emitted as complete evidence. Boundary windows then
recover the complete value. Their ownership check previously required a string
to cross strictly over the midpoint. A complete string beginning or ending
exactly at that midpoint was therefore owned by neither path.

Boundary ownership now accepts complete strings which cross or touch the
midpoint. It still rejects hits clipped by the boundary window and hits located
wholly to either side. Focused tests cover start-touching, end-touching,
strictly crossing, clipped, and non-boundary cases. The original 8 MiB/10 GiB
reproducer now returns all 2,560 rows and the same canonical SHA-256 as the
16 MiB reference.

## Benchmark method

Both comparisons used the real Release CLI with CPU processing, the strict Rust
ASCII engine, UTF-16LE enabled, streaming regex-only output, and source offsets.
Each old/new pair alternated execution order for five rounds. Timing included
process startup, reading, both string scanners, decoding, regex matching,
boundary recovery, and output.

The sparse corpus was the deterministic 10 GiB scale fixture with 2,560
expected URL/email rows. Its SHA-256 was
`97f97e9af9779178637c0bfd494fb6c6bff1f36803eaebecdf6ec7630ecc4d0a`.
The dense 1 GiB fixture contained 174,720 alternating ASCII and UTF-16LE
records of approximately 4 KiB and produced 262,080 email/URL rows.

Every run had to reproduce the complete expected row count and the reference
canonical output hash. Raw output order was not compared because parallel chunk
scheduling legitimately changes it.

## Results

| Corpus | Policy | Selected chunk | Runs | Median | Throughput | Exact runs |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Sparse 10 GiB | Previous automatic policy | 128 MiB | 5 | 3.3056 s | 3,097.7 MiB/s | 5/5 |
| Sparse 10 GiB | New automatic policy | 16 MiB | 5 | 2.9667 s | 3,451.6 MiB/s | 5/5 |
| Dense mixed 1 GiB | Previous automatic policy | 24 MiB | 5 | 2.8541 s | 358.8 MiB/s | 5/5 |
| Dense mixed 1 GiB | New automatic policy | 16 MiB | 5 | 2.7879 s | 367.3 MiB/s | 5/5 |

The new policy was 1.11x faster on the sparse 10 GiB corpus and 1.02x faster on
the dense mixed corpus. Machine-readable measurements are in
[`benchmarks/results/auto-chunk-sizing-2026-08.csv`](../benchmarks/results/auto-chunk-sizing-2026-08.csv).

These are warm-cache results from one 22-worker Windows host. The policy remains
bounded between 16 MiB and 128 MiB and still scales upward when an evidence item
is large enough to provide 32 chunks per worker. Explicit `-b` values remain
available when an examiner needs a fixed size or is validating different
hardware.

## Follow-up

The proposed dense all-pattern profile is complete. It confirmed that regex
transformation, rather than UTF-16LE discovery, was the next useful target. The
streaming path now buffers compact match ranges and formats rows directly,
avoiding the old per-candidate iterator, ordinary `Match` objects, and temporary
URL value lists. See the
[all-pattern streaming optimization](all-pattern-streaming-optimization-2026-08.md).
