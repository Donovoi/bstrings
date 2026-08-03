# bstrings

`bstrings` pulls readable code-page and UTF-16LE strings from files,
directories, and supported raw-disk targets. You can keep the complete output
or narrow it with literal searches, custom regular expressions, and a catalog
of common forensic patterns.

This fork keeps Eric Zimmerman's original workflow and adds the pieces needed
for larger evidence sets: bounded parallel processing, streaming output,
validated CPU and CUDA paths, an opt-in Rust ASCII engine, safer regex handling,
and reproducible tests and benchmarks.

## Why choose this fork?

| Decision point | Donovoi/bstrings | Original bstrings | ripgrep | bulk_extractor |
| --- | --- | --- | --- | --- |
| 100 GiB sparse URL/email scan | **42.19 s / 2,427 MiB/s** | 187.66 s / 545.7 MiB/s | 180.77 s / 566.5 MiB/s | 319.34 s / 320.7 MiB/s |
| 33-pattern exactness at 256 MiB | **33/33** | 15/33 | 25/33 stream-comparable | 0/33 through find/RE2 |
| Speed on exact per-pattern overlaps | **15/15 wins vs original, 2.02–2.39x** | Slower on every exact overlap | Fork wins 10/25; ripgrep wins 15/25 | Not ranked: no pattern passed the complete boundary corpus |
| Readable-string extraction | Code-page and UTF-16LE, offsets, streaming filters | Code-page and UTF-16LE | Raw byte/text search, not a strings extractor | Structured feature scanners and carving, not generic strings output |
| Optional enrichment | Magika-routed FLOSS recovery and provenance-preserving offline translation input | None | None | Recursive decoding and feature scanners, but no FLOSS/translation lineage into this regex catalog |
| Chunk-boundary handling | Rejects clipped edge fragments and recovers complete crossing strings | Can emit clipped or duplicate boundary matches | Searcher-managed | Page margins managed by each scanner |
| Parallel hardware paths | Runtime-selected SIMD CPU, opt-in Rust AVX2/SSE2 ASCII scanning, validated CUDA GPU, and CPU+GPU hybrid | CPU | CPU | Multi-threaded CPU scanners |
| Best fit | Large evidence images when you need strings, forensic patterns, and auditable output completion | Compatibility with the original CLI | Very fast known-pattern triage over raw bytes | Broad feature extraction, recursive decoding, carving, and histograms |

The first row is the earlier scale benchmark, where all four commands returned
the exact marker set. The next two rows are a stricter pattern-by-pattern run:
positive, negative, chunk-crossing, and terminal records had to match by value
and byte offset. A failed semantic or accuracy gate is not used for a speed
claim. Both benchmarks are warm-cache results on deliberately synthetic data.
Read the [scale benchmark](docs/scale-benchmark-2026-08.md) and the
[pattern and engine benchmark](docs/pattern-engine-benchmark-2026-08.md)
before generalizing them. The
[CPU scheduling benchmark](docs/chunk-scheduling-optimization-2026-08.md)
explains the current automatic chunk-size policy and its boundary-correctness
gate. The
[all-pattern streaming benchmark](docs/all-pattern-streaming-optimization-2026-08.md)
shows how regex-only output now avoids per-candidate iterator and match-object
allocations. The Rust engine remains an opt-in prototype; its
[separate benchmark and design notes](docs/rust-engine-prototype-2026-08.md)
explain why it is not the default yet.

## What this fork adds

- Bounded, parallel scanning without a hidden result-count limit
- SIMD CPU, native CUDA GPU, and mixed CPU+GPU extraction
- An opt-in Rust ASCII span scanner with startup parity validation and managed fallback
- Cached, vectorized multi-string prefiltering for `--ls` and `--fs`
- Hardware- and workload-aware CPU/GPU/hybrid calibration for very large inputs
- Lazy regex compilation based on measured per-pattern candidate density
- Allocation-conscious match-range streaming for dense `--lr all --ro` runs
- Offset-aware text and CSV output
- Per-pattern regex options, timeouts, and boundary tests
- 33 built-in forensic pattern candidates
- Provenance-preserving regex input for Magika/FLOSS and offline translation enrichment
- Optional RAPIDS/cuDF prefiltering for compatible built-in regexes
- A self-contained Windows x64 build produced by CI

For a compact explanation of the safety and concurrency decisions behind these
changes, see the [engineering notes](ENGINEERING_NOTES.md).

## Choose the right acceleration path

There are two independent acceleration features. Most users only need the
first one.

| Feature | What it speeds up | When to use it |
| --- | --- | --- |
| `--processor auto|cpu|gpu|hybrid` | Extracting strings from bytes | Use `auto` unless you are testing or deliberately forcing a backend |
| `--cpu-engine dotnet|rust|auto` | Finding code-page/ASCII byte spans on CPU | Use `rust` for the validated opt-in path; keep `dotnet` when portability or the established default matters |
| `--use-rapids` | Applying compatible built-in regexes after extraction | Use only when RAPIDS/cuDF is already installed and working |

The CPU path reports the instructions it selected at runtime. On the reviewed
host that is AVX2 for code-page strings and AVX2 plus BMI2 `PEXT` mask
compression for UTF-16LE; SSE4.1 and scalar UTF-16LE fallbacks are tested too.

The Rust prototype currently replaces only code-page/ASCII span discovery.
Decoding, offsets, chunk ownership, regex matching, output, raw disks, CUDA,
and UTF-16LE extraction remain in C#. `--cpu-engine rust` requires the native
library and fails if its ABI or startup parity checks fail. `auto` prefers Rust
after those checks and falls back to C#/.NET if the library is unavailable or
later fails. In this option, `auto` means availability fallback; it is not yet
a workload-performance selector. The default remains `dotnet` while more
hardware and evidence-shaped workloads are measured.

The native CUDA extractor compiles its ILGPU kernels for the detected device
and validates output against the CPU implementation before accepting a
session. An explicit `gpu` request fails clearly when CUDA cannot be used.
`auto` avoids CUDA startup for workloads where the local CPU class has already
won, then races CPU, GPU, and hybrid over representative file samples on very
large inputs. Every calibration candidate must match the CPU sample exactly,
and an accelerated mode needs a projected five-percent win after CUDA startup.

RAPIDS is more conservative. It uses a reviewed cuDF pattern only as a broad
prefilter, then checks every candidate with the normal .NET regex. Custom
regexes and built-ins without a safe cuDF prefilter remain on CPU. If the GPU
step fails before writing output, the whole regex request is retried on CPU.
Failures after output begins are fatal so the tool cannot quietly duplicate or
mix results. `bstrings` never installs Python, CUDA, or RAPIDS for you.

## Quick start

```powershell
# Extract strings from one file
.\bstrings.exe -f C:\evidence\image.bin

# Save results without printing every hit
.\bstrings.exe -f C:\evidence\image.bin -s -o C:\results\strings.txt

# Scan a directory recursively
.\bstrings.exe -d C:\evidence --mask "*.bin" -s -o C:\results\all.txt

# Include source offsets and decode code-page strings as Windows-1252
.\bstrings.exe -f C:\evidence\image.bin --cp 1252 --off

# Return only email matches
.\bstrings.exe -f C:\evidence\image.bin --lr email --ro

# Run several built-in patterns
.\bstrings.exe -f C:\evidence\image.bin --lr email,url3986,ipv4 --ro

# Use a custom regex containing a comma
.\bstrings.exe -f C:\evidence\image.bin --lr "\d{1,3}(?:,\d{3})*"

# Custom regexes are case-sensitive unless you opt in to another mode
.\bstrings.exe -f C:\evidence\image.bin --lr "(?i)secret|password"

# Force native CUDA extraction
.\bstrings.exe -f C:\evidence\memory.raw --processor gpu -s

# Let CPU and CUDA workers share one queue
.\bstrings.exe -f C:\evidence\disk.img --processor hybrid -s

# Evaluate the parity-gated Rust ASCII engine without changing the GPU policy
.\bstrings.exe -f C:\evidence\memory.raw --processor cpu --cpu-engine rust -s

# Add experimental RAPIDS regex prefiltering
.\bstrings.exe -f C:\evidence\image.bin --lr all --use-rapids

# Apply the same regex catalog to FLOSS/translation enrichment records
.\bstrings.exe --enrich-jsonl C:\results\enriched-strings.jsonl --lr all -o C:\results\enriched-matches.jsonl
```

Run `bstrings.exe --help` for the full command reference. Run
`bstrings.exe -p` to see every built-in pattern and its current description.
The [extractor and translation guide](docs/enrichment-pipeline.md) explains how
to route carved files with Magika, normalize FLOSS output, run a pinned offline
MADLAD-400 translation pass, and interpret derived matches safely.

## Built-in regex catalog

The catalog contains 33 patterns:

- **People and identifiers:** `guid`, `usPhone`, `ssn`, `zip`, `email`,
  `urlUser`
- **Networks and addresses:** `ipv4`, `ipv6`, `mac`, `url3986`, `onion_v3`
- **Windows artifacts:** `unc`, `win_path`, `named_pipe`, `reg_path`, `sid`,
  `bitlocker`, `var_set`
- **Content and structured values:** `b64`, `xml`, `pem_private_key`, `sha256`,
  `cve`, `cc`
- **Wallet candidates:** `bitcoin`, `aeon`, `bytecoin`, `dashcoin`,
  `dashcoin2`, `fantomcoin`, `monero`, `sumokoin`, `ethereum`

These are search candidates, not verdicts. A 64-character hexadecimal string
might be a SHA-256 digest, but the regex cannot establish how it was produced.
The same limitation applies to checksums, account existence, key validity, and
payment-card validation.

`urlUser` is capture-aware: with `--ro`, it returns the username-shaped part of
URL user information, not the password or URL prefix.

The reasoning, sources, rejected candidates, benchmark data, and remaining
RAPIDS limitation are in
[the regex review](docs/regex-pattern-research-2026-07.md).

## Important options

| Option | Purpose |
| --- | --- |
| `-f <path>` | Scan one file |
| `-d <path>` | Scan a directory recursively |
| `--mask <glob>` | Limit a directory scan, for example `*.bin` |
| `--ms <bytes>` | Skip larger files during a directory scan |
| `-o <path>` | Write text output, or CSV when the filename ends in `.csv` |
| `-a <bool>` | Enable or disable code-page strings; enabled by default |
| `-u <bool>` | Enable or disable UTF-16LE strings; enabled by default |
| `-m <n>` | Minimum string length; default `3` |
| `-x <n>` | Maximum string length; unlimited by default |
| `-b <MB>` | Chunk size from 1 to 1024 MB; `0` chooses automatically |
| `-q` | Hide the header and final summary |
| `-s` | Do not print hits to the console |
| `--ls <text>` | Return strings containing literal text |
| `--lr <value>` | Use built-in names, a custom regex, a comma-separated list, or `all` |
| `--fs <path>` | Read literal searches from a file |
| `--fr <path>` | Read regex searches from a file |
| `--ar <range>` | Set the code-page byte range, such as `[\x20-\x7E]` |
| `--ur <range>` | Set the UTF-16LE range, such as `[\u0020-\u007E]` |
| `--cp <id>` | Choose the code page; default `1252` |
| `--ro` | Write the matched part instead of the full extracted string |
| `--off` | Include source byte offsets |
| `--sa` / `--sl` | Sort alphabetically or by length |
| `--processor <mode>` | Choose `auto`, `cpu`, `gpu`, or `hybrid` extraction |
| `--cpu-engine <mode>` | Choose `dotnet`, `rust`, or availability-fallback `auto` for ASCII CPU span discovery |
| `--use-rapids` | Try an existing RAPIDS/cuDF installation for regex prefiltering |
| `--enrich-jsonl <path>` | Apply `--lr`/`--fr` to normalized extractor or translation records and preserve their lineage in JSONL output |

`--force-rapids` is retained as a deprecated alias for `--use-rapids`. Despite
the old name, it does not install or force unsupported software.

## Output safety and memory use

Plain, unsorted output streams to disk as strings are found. Literal searches
from `--ls` and `--fs` are also applied inside each bounded extraction batch,
so rejected strings do not enter the global deduplication set. Sorting and
regex workflows that cannot use the streaming regex path may still retain
matches for post-processing and use considerably more memory on a large image.

When `-o` is active, bstrings creates a sibling
`<output>.incomplete` marker. The marker is removed only after the run
finishes successfully. If the process exits nonzero or the marker remains, do
not treat that output as a complete evidence set.

Regex matching uses a two-second per-evaluation timeout. The first timeout
stops new regex work and fails the run instead of silently dropping the
problematic evidence item.

## Build and test

You need the .NET 9 SDK. Rust 1.95 is needed only to build or test the optional
native engine. A CUDA-capable NVIDIA GPU and driver are optional and used only
for `gpu` or `hybrid` extraction. The supported release artifact is Windows
x64.

```powershell
cargo fmt --manifest-path native\bstrings_core\Cargo.toml --check
cargo clippy --manifest-path native\bstrings_core\Cargo.toml --release --all-targets --locked -- -D warnings
cargo test --manifest-path native\bstrings_core\Cargo.toml --release --locked
cargo build --manifest-path native\bstrings_core\Cargo.toml --release --locked

dotnet restore bstrings.sln
dotnet build bstrings.sln -c Release --no-restore
dotnet test bstrings.sln -c Release --no-build

dotnet publish bstrings\bstrings.csproj `
  -c Release `
  -f net9.0 `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

To generate a repeatable extraction corpus:

```powershell
dotnet run --project dev-tools\benchmark-generator -- 256 .\benchmark-256mb.dmp

# Build an output-heavy synthetic fixture with ordinary short records
dotnet run --project dev-tools\benchmark-generator -- 64 .\dense-output.dmp --dense-output

# Exercise the long-record regex path without using evidence data
dotnet run --project dev-tools\benchmark-generator -- 64 .\dense-4096.dmp --dense-output --dense-record-length=4096
```

For a sparse, ground-truth corpus that scales cleanly to 100 GiB and exercises
16 MiB chunk boundaries:

```powershell
dotnet run --project benchmarks\ScaleCorpusGenerator -c Release -- `
  --output C:\bench\scale-1g.bin `
  --size-bytes 1073741824
```

The generator writes a SHA-256 manifest with the expected literal, URL, and
email counts. The matching four-tool harness is documented in
[benchmarks/README.md](benchmarks/README.md).

To compare the managed and Rust ASCII span engines directly, including the
native call and direct consumption of the pooled hit buffer:

```powershell
dotnet run --project benchmarks\AsciiEngineBenchmark -c Release -- `
  --rounds 7 `
  --target-mib 256 `
  --output benchmarks\results\ascii-engine-local.csv
```

Every scenario is parity-checked before it is timed. The checked-in reviewed
result is
[`ascii-engine-rust-prototype-2026-08.csv`](benchmarks/results/ascii-engine-rust-prototype-2026-08.csv).

The real-CLI scale runner alternates both engines over the same corpus and
requires exact marker, byte-coverage, and canonical-output parity:

```powershell
.\benchmarks\Invoke-RustEngineScaleBenchmark.ps1 `
  -DataRoot C:\bench `
  -Bstrings C:\tools\bstrings.exe `
  -RunRoot C:\bench\rust-engine-run `
  -Tiers @('1g','10g') `
  -VerifyHashes
```

The reviewed 1 GiB and 10 GiB measurements are in
[`rust-engine-scale-2026-08.csv`](benchmarks/results/rust-engine-scale-2026-08.csv).

To exercise every built-in pattern, encoding, and complexity class:

```powershell
dotnet run --project benchmarks\PatternCorpusGenerator -c Release -- `
  --output-dir C:\bench\patterns `
  --size-mib 256 `
  --segment-mib 16 `
  --encoding ascii `
  --complexity adversarial

.\benchmarks\Invoke-PatternBenchmark.ps1 `
  -DataRoot C:\bench\patterns `
  -CurrentBstrings C:\tools\fork\bstrings.exe `
  -UpstreamBstrings C:\tools\upstream\bstrings.exe `
  -BulkExtractor C:\tools\bulk_extractor64.exe `
  -RunRoot C:\bench\pattern-run-001 `
  -Repetitions 3 `
  -VerifyHashes
```

`sparse`, `dense`, and `adversarial` corpora are supported, as are `ascii`
and `utf16le`. The harness records unsupported semantics instead of weakening
a pattern until another tool happens to accept it.

To measure interpreted-versus-compiled regex crossover points:

```powershell
dotnet run --project benchmarks\RegexEngineBenchmark -c Release -- `
  --iterations 1,1000,10000,100000 `
  --rounds 5 `
  --output C:\bench\regex-engines.csv
```

The main streaming path starts measured built-ins with the interpreted engine
when candidate density is low. It constructs a compiled engine once when the
first bounded batch and remaining chunk count project enough attempts to repay
compilation. Custom regex behavior is unchanged.

The reasoning and measurements behind the SIMD, bounded collection, and
literal-prefilter changes are in the
[ripgrep performance review](docs/ripgrep-performance-review-2026-08.md).

## Releases

Every pull request and push to `master` restores, builds, tests, publishes, and
packages the Windows x64 artifact. Only a pushed tag beginning with `v` creates
a GitHub release. See [VERSIONING.md](VERSIONING.md) for the release steps.

## Attribution

`bstrings` was created by
[Eric Zimmerman](https://github.com/EricZimmerman/bstrings). This fork is
maintained at [Donovoi/bstrings](https://github.com/Donovoi/bstrings).

The original project announcement is
[Introducing bstrings, a Better Strings utility!](https://binaryforay.blogspot.com/2015/07/introducing-bstrings-better-strings.html).
The original project also received support from
[SANS Institute](https://www.sans.org/) and
[SANS DFIR](https://www.sans.org/digital-forensics-incident-response/).

See [LICENSE.md](LICENSE.md) for licensing terms.
