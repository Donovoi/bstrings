# Scale benchmark

This is developer and research documentation. Examiners do not need these
harnesses or comparison tools for a normal investigation. With a complete,
version-matched quality bundle, the full enrichment workflow is:

```powershell
bstrings.exe analyze -f evidence.raw --full -o results
```

The current complete quality bundle is v1.9.17. See
[download and installation](../docs/download-and-install.md) before treating
this example as a distribution command.

The PowerShell and build commands below reproduce synthetic performance and
correctness gates. They are not the evidence-analysis interface.

## FLOSS JSON normalization safety gate

A local synthetic probe generated 300,000 valid FLOSS records in a 19.635 MiB
document. The disk-backed parser completed strict indexing/validation in 4.844
seconds (4.05 MiB/s) and full one-item-at-a-time replay in 7.765 seconds (2.53
MiB/s). This was a bounded-memory regression probe, not a universal FLOSS speed
claim or a maintained cross-tool ranking. The checked-in Python tests are the
repeatable gate for 64 KiB reads, lazy item replay, fixed category order,
malformed/truncated input rejection, and temporary-file cleanup.

The parser's document memory is bounded, but exact per-file duplicate
suppression still retains one record ID for every unique normalized record from
that file. Treat that set as the next memory-scaling target for unusually
prolific binaries.

## Bounded language-detection reuse

`LanguageTriageBenchmark` generates synthetic attributed string records and
alternates the original one-detection-per-record path with bounded exact-text
reuse. Every pair must reproduce identical assessment and translation-candidate
files, logical statistics, and record counts before its timing is accepted.

```powershell
dotnet run --project .\benchmarks\LanguageTriageBenchmark -c Release -- `
  --records 100000 `
  --duplicate-percent 50 `
  --rounds 7 `
  --mode accurate `
  --output C:\bench\language-triage-accurate.csv
```

The reviewed run covered 0%, 50%, and 95% batch-local duplication in accurate
and fast modes. It also included a one-million-record scale pair and 20 rotated
determinism pairs. See the
[reviewed result](../docs/language-triage-performance-2026-08.md), the
[machine-readable summary](results/language-triage-reuse-2026-08.csv), and the
[decision record](../docs/architecture/adr-0004-bounded-language-detection-reuse.md).

### Translation-routing shadow overhead

`--experiment routing-shadow` is a separate, unmeasured A/B harness for the
shadow-only translation-worthiness metadata described by
[ADR-0007](../docs/architecture/adr-0007-translation-worthiness-routing.md).
Both variants enable the existing bounded detection reuse. Alternating variants
differ only in the optional `includeTranslationRouting` argument: one omits the
metadata and one embeds a single bounded routing `code` in each existing
language-assessment row.

```powershell
dotnet run --project .\benchmarks\LanguageTriageBenchmark -c Release -- `
  --experiment routing-shadow `
  --routing-corpus mixed `
  --records 100000 `
  --duplicate-percent 50 `
  --rounds 7 `
  --mode accurate `
  --output C:\bench\language-triage-routing-shadow.csv
```

`--routing-corpus` defaults to `mixed`. Its deterministic, synthetic options
are `natural` (multilingual prose), `machine` (valid and near-miss structured
tokens, code, and noise), `short` (short prose and machine fragments), `max`
(near the 2,048-character record limit), `encoded` (Base64, JWT, and hex
containers), and `provenance` (bounded synthetic origin kinds and attributes).
The all-unique `machine` corpus requires `--duplicate-percent 0`. The legacy
`reuse` experiment rejects `--routing-corpus` so its historical behavior and
CSV remain unambiguous. The routing-shadow CSV and summary identify the corpus
used for every measurement.

The harness refuses a timing unless candidate bytes and SHA-256 are identical,
pre-existing assessment fields are equal in order and value, and logical triage
statistics are unchanged. Lingua can vary its five diagnostic floating-point
scores by one unit at the published 12-decimal precision between independent
calls, so the paired comparison permits only that final-decimal quantum for
those score fields; identity, decisions, gates, errors, and all other fields
remain exact. The CSV retains both projected hashes so any such native score
jitter remains visible. It independently requires routing
metadata on every enabled assessment and none on the disabled variant. The CSV
reports both projection lengths and hashes, assessment-byte delta, managed
allocation, working set before/after and
at a 20 ms sampled peak, wall/CPU time, and retained/prospective/unknown routing
counts. It reports paired median wall-time, sampled-peak working-set, and total
candidate-plus-assessment byte regressions. The 20 ms working-set peak is an
approximate in-process sample: paired variants share the process heap, so it is
useful as a low-overhead regression signal rather than an exact isolated OS
peak. Runs with at least 100,000 records and seven rounds return nonzero if any
median exceeds ADR-0007's 5% shadow cap; smaller smoke runs exercise parity and
schema only so normal timing noise cannot fail them. This does not activate
bypass, call the translation model, or establish a performance win. Do not
check in local measurements without a separately reviewed, privacy-safe result
update.

Run the focused one-round synthetic smoke with:

```powershell
.\benchmarks\LanguageTriageBenchmark\Test-RoutingShadowBenchmark.ps1
```

Use `--help` to list both experiments and arguments. The default remains
`--experiment reuse`, with its historical CSV columns and assessment/candidate
byte-parity behavior.

### Translation-worthiness managed reference

`TranslationWorthinessBenchmark` is an offline, research-only C# reference for
the frozen sparse-linear model format in ADR-0007. It verifies the model and
manifest identities, reproduces the Python feature contract, and writes atomic
predictions. It is not part of `bstrings.exe`, does not change candidate
selection, and cannot authorize translation suppression.

Run the deterministic cross-language parity and path-safety smoke with:

```powershell
.\benchmarks\TranslationWorthinessBenchmark\Test-ManagedReference.ps1
```

The smoke trains only the project-authored synthetic seed, compares every C#
score and decision with the Python contract oracle, and rejects an output path
that aliases an input. The seed is a schema and reproducibility fixture, not an
accuracy corpus; every manifest and report remains `researchOnly: true` and
`promotionEligible: false`.

## Run-local translation deduplication

The v1.9.17 translation adapter combines a bounded in-memory hot set with an
exact run-local SQLite cache. A correct performance probe must include exact
duplicates separated by more than 4,096 other entries, prove one model call per
distinct complete source/configuration key, and still compare the emitted child
records byte-for-byte with the one-inference-per-key reference. It must also use
an all-unique workload to expose SQLite overhead and cache growth.

Run the private-free one-million-row cache gate with:

```powershell
python .\tools\enrichment\benchmark_translation_cache.py
```

The final reviewed Windows/Python 3.14.5 run reduced duplicate-cycle model
inputs from 1,000,000 to 10,000 and measured 133.08 microseconds per row, a
2,252,800-byte database, and an approximately 1,551,212-byte hot set. The
all-unique exact-cache path measured 169.79 microseconds per row, a
233,582,592-byte database, and an approximately 1,350,557-byte hot set. That is
0.004742% of the pinned CPU model's per-input time. The old LRU's subsecond
all-unique loop still proves that the cache is not intrinsically free; ADR-0005
therefore applies both a 250-microsecond absolute ceiling and a pinned-model
end-to-end ceiling, with a mandatory revisit for materially faster inference
providers.

Model-call reduction is the primary metric; cache-hit percentage alone is not
enough. Exact deduplication can materially help repetitive evidence while still
leaving a long CPU run when Full/high-recall selects millions of mostly unique
records. The standard release does not contain a CUDA translation runtime.
Progress rate and ETA must therefore be checked for truthful monotonic behavior,
not treated as a substitute for throughput measurement. See
[ADR-0005](../docs/architecture/adr-0005-translation-integrity-and-run-dedup.md)
for the frozen acceptance and privacy gates.

## Prerequisites and comparison tools

Acquire tools only from their official projects, pin the exact version used,
and record hashes alongside a result set.

| Dependency | Official source | Used for |
| --- | --- | --- |
| .NET 10 SDK | [Microsoft .NET 10 download](https://dotnet.microsoft.com/download/dotnet/10.0) | Corpus generators and managed benchmark harnesses |
| Current bstrings | [Donovoi/bstrings releases](https://github.com/Donovoi/bstrings/releases) or a local Release build | Candidate under test |
| Original bstrings | [Eric Zimmerman/bstrings](https://github.com/EricZimmerman/bstrings) | Upstream comparison |
| ripgrep | [Official ripgrep releases](https://github.com/BurntSushi/ripgrep/releases) | Raw-byte PCRE2 comparison; the reviewed result used 15.1.0 |
| bulk_extractor | [Official bulk_extractor releases](https://github.com/simsong/bulk_extractor/releases) | Feature-scanner comparison |
| Rust and Cargo | [Official Rust installation](https://www.rust-lang.org/tools/install/) | Optional native ASCII-engine benchmark |

This benchmark compares Donovoi/bstrings, original bstrings, ripgrep, and
bulk_extractor without using case data. It has two parts:

- `ScaleCorpusGenerator` writes deterministic binary files with known URL,
  email, and literal records.
- `Invoke-ScaleBenchmark.ps1` rotates tool order, retains every result, and
  fails immediately unless every expected interior, boundary, and terminal
  marker appears exactly twice.

The background is deterministic pseudo-random binary data with printable ASCII
bytes replaced by delimiters. Each 16 MiB segment contains an interior record;
every segment boundary contains a crossing record; and one final record ends a
single byte before EOF. The terminal record makes a matching count evidence
that the scanner reached the end of the file.

## Generate a corpus

Sizes must be exact multiples of the 16 MiB segment size.

```powershell
dotnet run --project .\benchmarks\ScaleCorpusGenerator -c Release -- `
  --output C:\bench\scale-10g.bin `
  --size-bytes 10737418240
```

The generator refuses to replace an existing file unless `--overwrite` is
explicitly supplied. Its adjacent `.manifest.json` contains the file size,
SHA-256, seed, record schedule, expected counts, and representative samples.

## Run the comparison

```powershell
.\benchmarks\Invoke-ScaleBenchmark.ps1 `
  -DataRoot C:\bench `
  -CurrentBstrings C:\tools\fork\bstrings.exe `
  -UpstreamBstrings C:\tools\upstream\bstrings.exe `
  -BulkExtractor C:\tools\bulk_extractor64.exe `
  -RunRoot C:\bench\run-001 `
  -VerifyHashes
```

The default tiers are 256 MiB, 1 GiB, 10 GiB, and 100 GiB. Repetition counts
are five, five, three, and one respectively. The 100 GiB tier is intentionally
one run because it is larger than the reviewed host's RAM and each complete
four-tool pass is expensive.

The shared expression finds the synthetic URL and email markers. Bstrings runs
with 16 MiB chunks, ASCII enabled, UTF-16LE disabled, and CPU extraction.
Ripgrep uses PCRE2. Bulk_extractor enables only its `email` scanner, which also
writes `url.txt`; its URL and email feature counts are summed.

Validation reconstructs the complete expected marker-ID set from the manifest.
It checks every output row, not only the total. Bulk_extractor must also report
the exact input byte count in its DFXML report.

Bulk_extractor does more than the other tools in this comparison: it writes
contexts, histograms, DFXML, and a source hash. Its time is therefore useful as
an operational comparison, not a claim that all four commands perform the same
job.

See the [reviewed results and limitations](../docs/scale-benchmark-2026-08.md).

## CPU/GPU backend crossover

`Invoke-BackendCrossoverBenchmark.ps1` runs the current Release CLI in CPU,
GPU, and hybrid modes against `ScaleCorpusGenerator` fixtures. Mode order is
rotated. Every run must reproduce the complete marker multiset, including the
terminal record, and all backends must have the same canonical output hash.
The harness also records process CPU time, peak working set, throughput, and
sampled NVIDIA utilization, memory, power, and SM clock data.

```powershell
.\benchmarks\Invoke-BackendCrossoverBenchmark.ps1 `
  -DataRoot C:\bench\backend-crossover `
  -BstringsDll .\bstrings\bin\Release\net10.0\bstrings.dll `
  -RunRoot C:\bench\backend-crossover-run `
  -Tiers 1g,2g,4g,8g,16g,32g `
  -Modes cpu,gpu,hybrid `
  -Repetitions 3 `
  -IncludeUnicode `
  -VerifyHashes
```

The run root must not already exist. Use an ASCII-only pass and a combined
ASCII/UTF-16LE pass before changing automatic backend policy. See the
[reviewed crossover result](../docs/backend-crossover-2026-08.md).

## Forensic report projection

`ForensicReportBenchmark` measures the bounded JSONL-to-TSV/histogram stage in
isolation. It generates synthetic attributed matches outside the timed region,
runs several rounds, and rejects a measurement unless finding rows, column
widths, zero-count pattern retention, and exact distinct-feature counts all
match expectations.

```powershell
dotnet run --project .\benchmarks\ForensicReportBenchmark -c Release -- `
  --records 100000 `
  --distinct-features 10000 `
  --rounds 5 `
  --output C:\bench\forensic-report.csv
```

The harness deletes its own per-round report files after validation. The CSV
retains elapsed time, rows/s, input/output bytes, process peak working set, and
the exactness result.

## Pattern-by-pattern benchmark

`PatternCorpusGenerator` creates a separate, deterministic file for every
built-in pattern. Each manifest records the authoritative regex, options,
witnesses, SHA-256, and every expected value/offset pair. Positive records are
placed inside segments, across each segment boundary, and next to EOF. Negative
witnesses are present but must never appear in tool output.

Generator version 5 covers the current 66-pattern catalog, including every
member of the `wallets`, `pii`, `credentials`, `browser`, and `registry`
groups. Older checked-in comparison reports remain explicitly labeled as
33- or 51-pattern snapshots so their totals are not mistaken for current
catalog coverage.

```powershell
dotnet run --project .\benchmarks\PatternCorpusGenerator -c Release -- `
  --output-dir C:\bench\patterns-256m `
  --size-mib 256 `
  --segment-mib 16 `
  --encoding ascii `
  --complexity sparse
```

Available encodings are `ascii` and `utf16le`. Complexity can be `sparse`,
`dense`, or `adversarial`; the last class fills each segment with as many as
4,096 non-overlapping, delimiter-separated authoritative near-misses to stress
rejection paths. Long witnesses are capped at the largest count that physically
fits, and the actual per-segment count is recorded in the manifest. The
generator refuses to overwrite an existing corpus unless `--overwrite` is
explicit.

Run the four-tool comparison with:

```powershell
.\benchmarks\Invoke-PatternBenchmark.ps1 `
  -DataRoot C:\bench\patterns-256m `
  -CurrentBstrings C:\tools\fork\bstrings.exe `
  -UpstreamBstrings C:\tools\upstream\bstrings.exe `
  -BulkExtractor C:\tools\bulk_extractor64.exe `
  -RunRoot C:\bench\patterns-run-001 `
  -Repetitions 3 `
  -VerifyHashes
```

The harness rotates process order and normalizes capture-group output before
comparing the complete value/offset multiset. A tool is timed but excluded from
the speed ranking when it rejects the syntax, searches a different semantic
unit, misses a boundary record, adds a duplicate, or returns an unexpected
value. Ripgrep uses PCRE2 over the byte stream. Bulk_extractor uses its current
case-sensitive find/RE2 scanner. Neither is silently given a weaker pattern.

## Regex engine crossover

`RegexEngineBenchmark` includes construction cost and alternates interpreted
and compiled order for every backtracking built-in. It injects one known
positive every 1,024 attempts and requires identical match counts.

```powershell
dotnet run --project .\benchmarks\RegexEngineBenchmark -c Release -- `
  --iterations 1,1000,10000,20000,40000,60000,80000,100000 `
  --rounds 5 `
  --output C:\bench\regex-engine-results.csv
```

Use it when changing a built-in expression, moving to another .NET runtime, or
retuning lazy compilation. The checked-in thresholds came from the reviewed
host, but the benchmark remains the authority when hardware or expressions
change.

See the [pattern and engine results](../docs/pattern-engine-benchmark-2026-08.md).

## All-pattern streaming output

The dense all-pattern benchmark exercises the real `--lr all --ro` streaming
path rather than one regex in isolation. Generate the mixed ASCII/UTF-16LE
fixtures with the development generator:

```powershell
dotnet run --project .\dev-tools\benchmark-generator -c Release -- `
  256 C:\bench\dense-all-256m.bin `
  --dense-output `
  --dense-record-length=512
```

The reviewed run used 64 MiB and 256 MiB fixtures with 512-character records,
plus a 1 GiB fixture with 4,096-character records. Baseline and candidate
processes alternated for five rounds at each size. Output lines were sorted
ordinally before hashing because parallel completion may change raw row order.
All 30 runs had to reproduce the complete reference multiset.

See the
[profiling decision, results, and limitations](../docs/all-pattern-streaming-optimization-2026-08.md)
and the
[`all-pattern-streaming-2026-08.csv`](results/all-pattern-streaming-2026-08.csv)
measurements.

The follow-up structured-hit benchmark reuses the same fixtures and CLI. It
validates the optimization that keeps numeric offsets separate until an
extracted string produces a regex result. See the
[structured-hit result](../docs/structured-hit-streaming-optimization-2026-08.md)
and
[`structured-hit-streaming-2026-08.csv`](results/structured-hit-streaming-2026-08.csv).

## Automatic CPU chunk sizing

The automatic CPU policy is validated with real CLI runs rather than timing the
size calculation itself. The reviewed comparison used the 10 GiB scale corpus
and a dense 1 GiB mixed ASCII/UTF-16LE fixture:

```powershell
dotnet run --project .\dev-tools\benchmark-generator -c Release -- `
  1024 C:\bench\dense-1g.bin `
  --dense-output `
  --dense-record-length=4096
```

Old and new policies were alternated for five rounds. Each run had to return the
same complete canonical output before its timing was accepted. See the
[scheduling result, boundary repair, and limitations](../docs/chunk-scheduling-optimization-2026-08.md)
and the
[`auto-chunk-sizing-2026-08.csv`](results/auto-chunk-sizing-2026-08.csv)
measurements.

## Rust ASCII engine crossover

`AsciiEngineBenchmark` compares the established C#/.NET ASCII span scanner
with the opt-in Rust engine through the real native boundary. It includes
native invocation and direct consumption of caller-owned pooled hit storage.
Each scenario must produce exactly the same ordered offsets and lengths before
timing begins.

```powershell
cargo build --manifest-path .\native\bstrings_core\Cargo.toml --release --locked

dotnet run --project .\benchmarks\AsciiEngineBenchmark -c Release -- `
  --rounds 7 `
  --target-mib 256 `
  --output .\benchmarks\results\ascii-engine-local.csv
```

The four deterministic workloads cover sparse binary input, uniform random
bytes, one dense printable run, and many short fragmented runs. Sizes of 64
KiB, 1 MiB, and 16 MiB expose native-call and chunk-size crossovers. The
[reviewed prototype result and limitations](../docs/rust-engine-prototype-2026-08.md)
should be read before changing the default engine.

`Invoke-RustEngineScaleBenchmark.ps1` compares both engines through the real
CLI. It alternates execution order, verifies corpus SHA-256 when requested,
checks the complete interior/boundary/terminal marker multiset, verifies byte
coverage, and requires the canonicalized output lines to match across engines.
Raw hashes are retained because parallel chunk scheduling can legitimately
change line order.

```powershell
.\benchmarks\Invoke-RustEngineScaleBenchmark.ps1 `
  -DataRoot C:\bench `
  -Bstrings C:\tools\bstrings.exe `
  -RunRoot C:\bench\rust-engine-run `
  -Tiers @('1g','10g') `
  -VerifyHashes
```
