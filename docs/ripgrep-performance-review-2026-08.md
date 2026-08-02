# What bstrings can borrow from ripgrep

Date: 2 August 2026

## Short version

The most useful ripgrep idea was not “use Rust” or “add more threads.” It was
to spend as much time as possible in precomputed vectorized searches, reject
non-candidates early, reuse bounded buffers, and keep scanning separate from
ordered output.

Three changes survived parity tests and interleaved benchmarks:

1. the byte scanner now handles 32-byte AVX2 masks (16-byte SSE2 when needed)
   as contiguous valid and invalid runs instead of branching once per byte;
2. the bounded producer/consumer collector is used even when there is no output
   file, avoiding a temporary all-results list followed by a second full walk;
3. `--ls` and `--fs` targets are compiled once into .NET 9
   `SearchValues<string>` and applied inside each extraction batch, before
   rejected strings can enter the global deduplication set.

The third change produced the material user-facing gain: 2.59x for a fixed
literal that was present and 3.79x for a 162-keyword list on the test corpus.

## Evidence from ripgrep and the runtime

| Claim | Primary evidence | What we used |
| --- | --- | --- |
| Fast literal routines should reject candidates before the full matcher | ripgrep 15.1.0's [inner-literal extractor](https://github.com/BurntSushi/ripgrep/blob/15.1.0/crates/regex/src/literal.rs) constructs cheaper literal searches and only runs the original regex on candidate lines | Apply fixed-string targets to each extracted batch before global storage and post-processing |
| Buffers should be bounded and reused | ripgrep's [line buffer](https://github.com/BurntSushi/ripgrep/blob/15.1.0/crates/searcher/src/line_buffer.rs) explicitly recommends creating buffers sparingly and reusing them | Keep the existing bounded channels and pooled byte arrays active for in-memory runs too |
| Multi-string search needs a compiled search structure, not a nested loop | Microsoft's [.NET 9 performance review](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-9/) documents `SearchValues<string>` implementations including specialized single-string search, Teddy, Rabin-Karp, and Aho-Corasick | Cache one ordinal case-insensitive matcher for `--ls` or every usable line in `--fs` |
| Memory mapping is a heuristic, not a universal win | ripgrep enables mmap automatically only for a few explicit files in its [high-level argument policy](https://github.com/BurntSushi/ripgrep/blob/15.1.0/crates/core/flags/hiargs.rs), and its [searcher fallback](https://github.com/BurntSushi/ripgrep/blob/15.1.0/crates/searcher/src/searcher/mmap.rs) uses normal reads when mmap is unavailable or unhelpful | Do not replace the current reader without a cold-cache, platform-specific benchmark and mutation-safety review |

ripgrep also uses a single search thread for one explicit file. bstrings should
not copy that rule blindly: it extracts code-page and UTF-16LE strings, checks
chunk boundaries, records offsets, and can share work with CUDA. The correct
thread policy therefore remains workload-specific.

## Benchmark method

- Windows 11 Pro, Intel Core Ultra 9 185H, 22 logical processors
- .NET 9 target, 16 MiB chunks, CPU processor mode
- original build: `af9e8d7e4f20c2966f5c8e974a5be0d3ce433f09`
- ripgrep reference: 15.1.0, `af60c2de9d85e7f3d81c78601669468cf02dabab`
- deterministic 256 MiB fixture, SHA-256
  `CB3E4FA48D21523C85DD01F5A01E78D4CAE233E1AC447482BAD3BE824C3365D4`
- warm-cache old/new runs alternated to reduce thermal and background-load bias
- medians reported; console output suppressed

| Workload | Original | Candidate | Change |
| --- | ---: | ---: | ---: |
| ASCII + UTF-16LE extraction | 1.56 s | 1.48 s | 1.05x faster |
| Present fixed literal (`root`), 7 runs | 1.544 s | 0.595 s | 2.59x faster |
| 162 usable literals with no output matches, 5 runs | 2.179 s | 0.575 s | 3.79x faster |
| All 33 built-in regexes | 2.03 s | 2.03 s | no material wall-time change |

The present-literal run reduced median process CPU time from 4.36 to 1.72
seconds (60.6%). The 162-literal run reduced it from 18.52 to 1.86 seconds
(90.0%). These figures describe this host and synthetic fixture, not every
evidence image.

For scale, raw `rg -a -F root` took 0.160 seconds on the same warm fixture.
That is still 3.72x faster than the optimized bstrings run, but it is not the
same job: it does not concurrently extract code-page and UTF-16LE strings,
recover cross-chunk strings, deduplicate hits, or preserve bstrings offsets and
output semantics.

## Correctness and detractor checks

- The complete test suite passes with AVX2 enabled, AVX2 disabled, and all .NET
  hardware intrinsics disabled.
- A deterministic randomized scanner test compares the optimized result to a
  scalar reference across 250 inputs, six byte ranges, variable offsets, and
  variable minimum and maximum lengths.
- Fixed-literal output containing real matches had the same line multiset in
  old and new builds. The parallel order differed for two equal sort keys, as
  it already can in the original pipeline.
- An earlier attempt to replace the scanner with repeated
  `IndexOfAnyInRange`/`IndexOfAnyExceptInRange` calls regressed the mixed binary
  corpus and was discarded.
- We did not add a second regex-literal analyzer. ripgrep itself skips its
  outer optimization when the underlying regex is already accelerated, and
  .NET 9's compiled and non-backtracking regex engines already use
  `SearchValues`-based starting-position searches. A duplicate prefilter would
  need pattern-level proof and benchmarks before it could be trusted.

## Next candidates

The second optimization round profiled and tested the output-heavy path before
choosing another change. Its results are recorded below, so the remaining list
has been reordered.

1. Profile the `RegexOutputRecord` enumeration and construction hotspot seen in
   the latest trace. Confirm that it is not a sampling artifact before changing
   the record representation.
2. Prototype lazy decoding: retain byte offset and length descriptors until a
   literal or regex candidate actually needs a managed string. This is a larger
   change because code pages, UTF-16LE, offsets, maximum lengths, and boundary
   recovery all need exact parity.
3. Compare ordinary sequential reads with a true read-only OS memory map on
   Windows and Linux, under both warm and cold cache. Do not infer a win from
   the `MappedStream` type name; it is a DiscUtils stream abstraction, not an OS
   memory mapping.
4. Revisit output batching only if a different design clears a 15% end-to-end
   gate. The first attempt made the isolated writer 1.82x faster but improved
   real output-heavy runs by only 5–9%, so it was removed.

## Second round: source-generated short URL matching

The research loop used `Donovoi/robin` at commit
`001a84f43f79c073c25660f8364e4416ce03d358`. Its first recommendation was to
batch complete output records. That prototype was rejected by the gate above.
A [`dotnet-trace`](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace)
CPU sample then showed that output formatting was no longer the largest
actionable cost: the URL regular expression and `Match`/capture materialization
dominated the hot path.

The accepted design is intentionally specific:

- `url3986` uses .NET's
  [source-generated regex engine](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-source-generators)
  for extracted strings up to 2,048 characters;
- it uses allocation-light
  [`Regex.EnumerateMatches`](https://learn.microsoft.com/en-us/dotnet/api/system.text.regularexpressions.regex.enumeratematches?view=net-9.0)
  ranges and derives the URI range from the pattern's single optional leading
  delimiter;
- all values are buffered before output, with a 10 ms timeout that discards the
  partial buffer and replays the whole string through the existing
  non-backtracking matcher; and
- longer strings bypass the generated path.

The 2,048-character boundary came from an isolated engine sweep at 128, 256,
512, 1,024, and 2,048 characters, not a guess. Across seven rotated runs per
size, the generated path was 12.6x to 119.6x faster than the non-backtracking
engine in that matcher-only test. It fell back 21 times in 3,555,328 valid
matches (0.0006%) and did not fall back in 400 deliberately awkward
2,048-character near-misses. The timeout remains necessary because those
samples cannot prove that every possible input is cheap.

### End-to-end acceptance result

The final test used a deterministic 64 MiB output-heavy fixture containing
alternating ASCII and UTF-16LE records. Each process searched `email,url3986`,
wrote regex-only results and offsets, used the CPU extractor, and ran in an
alternating old/new order. Nine-run medians were:

| Build | Median | IQR | Output bytes |
| --- | ---: | ---: | ---: |
| Previous `master` | 2,228.21 ms | 27.44 ms | 142,859,898 |
| Source-generated range path | 1,851.09 ms | 55.26 ms | 142,859,898 |

That is a 16.93% median wall-time reduction, or 1.204x throughput. A
single-worker run produced byte-identical 142,859,307-byte files with SHA-256
`F2F4AF168233157319D754482AEC6E75A3EF3926B297FC90A63026CB3BE8AE82`.

The detractor's regression cases also passed:

| Workload | Candidate change |
| --- | ---: |
| 1,024-character dense URL records | 2.25% faster |
| 4,096-character bypass records | 0.22% faster |
| Multi-megabyte bypass records | 0.41% faster |
| Sparse 256 MiB fixture with all built-ins | 0.68% slower |
| Sparse all-built-in search without `--ro` | 2.63% faster |

The last figure is below the 2% rejection threshold and inside the run-to-run
dispersion. The CPU trace also showed the sampled regex execution share falling
from roughly 25% to roughly 9%; the earlier symbolic non-backtracking URL
hotspot disappeared. Differential tests cover the dispatch boundary, a reduced
exhaustive alphabet, 2,000 generated URL-grammar cases, multiple matches,
Unicode context, and a forced timeout/replay. The full suite has 186 passing
tests.
