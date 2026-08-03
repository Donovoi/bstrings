# All-pattern streaming optimization

## Outcome

Profiling the real `--lr all --ro` CLI path identified regex-result
materialization as the next useful optimization target. The streaming path now
collects compact match ranges and formats output directly. It no longer creates
a `CreateRecords` iterator for every pattern/candidate pair, ordinary `Match`
objects when no capture group is needed, or an intermediate URL-value list for
the generated short-input URL path.

Five alternating baseline/candidate pairs at each of three sizes produced the
same complete canonical output in all 30 runs. Median elapsed time improved by
1.16x at 64 MiB, 1.08x at 256 MiB, and 1.02x at 1 GiB.

## Why this target

The baseline was collected with .NET's sampled-thread-time and verbose GC
EventPipe profiles over a deterministic 256 MiB mixed ASCII/UTF-16LE fixture.
The sampled trace attributed 31.18 percent of exclusive samples to the legacy
record-iterator current path. In the allocation-stack trace,
`CreateRecords` appeared on 42.37 percent of samples and the temporary URL
value-list path appeared on 30.85 percent.

After the change, those paths no longer appear in the streaming profile.
Regex execution itself is now the main transformation cost. The measured
end-to-end improvement, rather than the normalized trace percentages, is the
performance acceptance result.

## Implementation

The optimized path:

- caches the resolved built-in definition with each adaptive matcher;
- uses `Regex.EnumerateMatches` when no capture group is required;
- keeps up to four match ranges in stack storage and allocates overflow storage
  only for strings containing more matches;
- delays row emission until matching finishes, so a timeout cannot duplicate
  records already emitted before a bounded retry;
- retains capture-aware handling for `urlUser`, the generated URL fallback,
  the custom Base64 validator, XML validation, CSV output, and text offsets.

The established `CreateRecords` API remains available for non-streaming paths.
Parity tests compare legacy and optimized output for email, URL captures,
generated URLs, Base64, XML, custom patterns, and the overflow-range case.

## Benchmark method

The real Release CLI used CPU extraction, the managed ASCII engine, both
encodings, 16 MiB chunks, all 33 built-ins, regex-only output, and byte offsets:

```powershell
dotnet bstrings.dll -f <fixture> -m 3 -b 16 --lr all --ro --off `
  -s -o <output.txt> -q --processor cpu --cpu-engine dotnet
```

Each baseline/candidate pair alternated process order for five rounds. Timing
included startup, file reading, extraction, decoding, regex work, boundary
recovery, and output. Raw row order was allowed to vary with parallel
completion; lines were sorted ordinally and hashed before a run was accepted.

The synthetic fixture details were:

| Size | Record length | Fixture SHA-256 | Output rows | Canonical output SHA-256 |
| ---: | ---: | --- | ---: | --- |
| 64 MiB | 512 | `b1d17a93435fd0d5855b8b5e5096dd708dc76b597e2c8d99f2de27d2f56c801b` | 196,224 | `8bcad070598c0e34e583cf225562a53d522d7cb3c37c890fb196733009b25c57` |
| 256 MiB | 512 | `d7d455dc0b6596018f84e416fb0b1bfd5b989150c9930636ed4a1850da9c0716` | 784,896 | `c00191657557a88101ae0b20182168d981d8da46ab63a6e0e04f48e0b4a8040f` |
| 1 GiB | 4,096 | `bde5d3a4fcee3d5fbbb96fceb20c2425a5353ee216076989a99b0ebd77dc7ce4` | 393,120 | `1d5b27a0f1c5bec1ad994c144bd766e298e2881e7726a52af19a36354e3b21a4` |

## Results

| Corpus | Baseline median | Optimized median | Speedup | Time reduction | Exact runs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Dense mixed 64 MiB | 2.0555 s | 1.7682 s | 1.16x | 13.97% | 10/10 |
| Dense mixed 256 MiB | 3.7300 s | 3.4380 s | 1.08x | 7.83% | 10/10 |
| Dense mixed 1 GiB | 6.4819 s | 6.3337 s | 1.02x | 2.29% | 10/10 |

Machine-readable measurements are in
[`benchmarks/results/all-pattern-streaming-2026-08.csv`](../benchmarks/results/all-pattern-streaming-2026-08.csv).

These are warm-cache results from one Windows host and synthetic fixtures.
They show a repeatable benefit for dense all-pattern output, not a universal
speed claim. At 1 GiB, extraction and I/O make the regex allocation saving a
smaller share of total elapsed time.

## Next target

The new allocation profile makes offset parsing the next bounded managed-code
candidate. `ParseHit` accounts for 5.28 percent of exclusive allocation-stack
samples because extraction materializes `offset + tab + data`, then regex
processing slices it back into separate strings. Carrying a structured hit
through the streaming pipeline could remove that round trip. It should be
attempted before a wider Rust port and accepted only if end-to-end exact-output
benchmarks improve; an earlier general regex prefilter was correctly rejected
because it slowed the complete pattern workload.
