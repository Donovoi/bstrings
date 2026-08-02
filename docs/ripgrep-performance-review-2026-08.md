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

1. Benchmark block-buffered UTF-8 output on a representative high-match case.
   The real 80 GiB runs showed that output density can dominate total time.
2. Prototype lazy decoding: retain byte offset and length descriptors until a
   literal or regex candidate actually needs a managed string. This is a larger
   change because code pages, UTF-16LE, offsets, maximum lengths, and boundary
   recovery all need exact parity.
3. Compare ordinary sequential reads with a true read-only OS memory map on
   Windows and Linux, under both warm and cold cache. Do not infer a win from
   the `MappedStream` type name; it is a DiscUtils stream abstraction, not an OS
   memory mapping.
4. Add an output-heavy benchmark fixture before changing writer buffering.
   Small quiet benchmarks cannot validate improvements to 100+ GB CSV runs.
