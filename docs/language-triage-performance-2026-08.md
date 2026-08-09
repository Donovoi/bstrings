# Language-triage performance review

## Outcome

The useful C# optimization is bounded language-detection reuse, not a complete
Native AOT release or additional hand-written Windows I/O. When identical
eligible text occurs more than once in the existing 2,048-record/8 MiB batch,
bstrings now reuses the first successful bundled Lingua result. Exact ordinal
text equality is required, failures still retry per record, and all assessment,
candidate, provenance, ordering, cancellation, and atomic-publication behavior
remains per record.

Seven alternating baseline/candidate pairs over 100,000 synthetic records
reduced median triage time by 45.9% in accurate mode and 47.7% in fast mode at
50% batch-local duplication. At 0% duplication, median time changed by +0.0%
to -0.4%, so the all-unique regression gate passed. A one-million-record
accurate-mode pair retained exact output and reduced time by 44.9%.

These are language-triage-stage results on synthetic input, not a promise that
every Full examination will improve by the same percentage. The saved share is
governed by the exact eligible-text duplication inside each bounded batch.

## Forensic determinism

The first real-Lingua comparison exposed a pre-existing last-bit difference in
parallel floating-point reductions. Decisions, candidates, record counts, and
language identities were unchanged, but raw assessment JSON could differ by
approximately 1e-16.

All policy decisions continue to use the raw detector values. Only the five
reported confidence/margin fields are rounded to 12 decimal places using
round-to-even, with negative zero normalized to zero. Each assessment reports
`scoreDecimalPlaces`, `confidenceGatePassed`, and `marginGatePassed`, so the
authoritative raw gate outcome remains explicit even when displayed values
round alike. A 20-round real-Lingua probe produced one identical assessment
SHA-256 across all 40 baseline/reuse outputs.

## Benchmark method

The benchmark uses the pinned `bstrings_core` Lingua implementation and
synthetic attributed English/Spanish records. Each round alternates baseline
and candidate order, forces a full managed collection before timing, and
records wall time, process CPU time, managed allocation, collection counts,
working set, output sizes, logical statistics, and output SHA-256 values. A
pair fails immediately unless both output files and statistics are exact.

The reviewed host used Windows 11 Pro build 26200, .NET SDK 10.0.302, and an
Intel Core Ultra 9 185H with 16 cores and 22 logical processors. No private case
text, case-derived hash, filename, or identifier was retained in the fixtures
or checked-in results.

| Records | Duplicate text | Mode | Rounds | Baseline median | Reuse median | Reduction | Speedup |
| ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 100,000 | 0% | accurate | 7 | 13.061 s | 13.006 s | 0.42% | 1.004x |
| 100,000 | 50% | accurate | 7 | 12.960 s | 7.007 s | 45.94% | 1.850x |
| 100,000 | 95% | accurate | 7 | 13.744 s | 1.249 s | 90.91% | 11.006x |
| 100,000 | 0% | fast | 7 | 29.547 s | 29.539 s | 0.03% | 1.000x |
| 100,000 | 50% | fast | 7 | 29.423 s | 15.399 s | 47.66% | 1.911x |
| 100,000 | 95% | fast | 7 | 29.658 s | 2.234 s | 92.47% | 13.276x |
| 1,000,000 | 50% | accurate | 1 | 129.354 s | 71.312 s | 44.87% | 1.814x |

Median managed allocation on the all-unique workloads increased by 3.6% from
the bounded grouping table. It decreased by 1.6% at 50% duplication and 6.4%
at 95%. Median working-set changes across the six seven-round workloads ranged
from -0.44% to +1.48%; the one-million-record pair was +3.60%. All stayed
inside the preregistered 5% limit.

The fast Lingua mode was slower than accurate mode on this bilingual synthetic
fixture. That result does not establish a universal ranking; it is why mode
results are reported separately rather than inferred from their names.

Machine-readable aggregate measurements are in
[`benchmarks/results/language-triage-reuse-2026-08.csv`](../benchmarks/results/language-triage-reuse-2026-08.csv).
The implementation and rollback rules are governed by
[ADR-0004](architecture/adr-0004-bounded-language-detection-reuse.md).

## Native AOT, P/Invoke, and lower-level I/O

A separate seven-pair direct CPU scan probe found that Native AOT improved
short invocations, but its benefit narrowed to 5.8% at 10 GiB. The complete
publish emitted 91 trimming/AOT warnings involving dynamic JSON, ILGPU, and
other runtime behavior, so it did not meet Full-profile compatibility or
forensic-diagnostics gates. CoreCLR remains the release runtime.

The extraction path already uses vector intrinsics, memory mapping, pooled
buffers, bounded channels, generated regex where suitable, and a blittable
ABI-checked Rust library. .NET's supported `RandomAccess` APIs already map to
overlapped Windows I/O. More raw `ReadFile`/IOCP P/Invoke would duplicate the
runtime and risk breaking the verified-handle/lease design without evidence of
a bottleneck. `SuppressGCTransition` is prohibited for scanner, detector, and
I/O calls because they are not trivial sub-microsecond nonblocking functions.

Source-generated `LibraryImport`, ReadyToRun, Server GC, additional generated
fixed regexes, and managed `RandomAccess` remain separate, attributable
experiments. None should be bundled into this result or promoted without its
own profile, exact-output gate, and rollback threshold.

## Reproduce

```powershell
dotnet run --project .\benchmarks\LanguageTriageBenchmark -c Release -- `
  --records 100000 `
  --duplicate-percent 50 `
  --rounds 7 `
  --mode accurate `
  --output C:\bench\language-triage-100k-50-accurate.csv
```

The output path must not already exist. The harness deletes its generated JSONL
and reports after a successful run and retains them only when parity fails.
