# bstrings

`bstrings` pulls readable code-page and UTF-16LE strings from files,
directories, and supported raw-disk targets. You can keep the complete output
or narrow it with literal searches, custom regular expressions, and a catalog
of common forensic patterns.

This fork keeps Eric Zimmerman's original workflow and adds the pieces needed
for larger evidence sets: bounded parallel processing, streaming output,
validated CPU and CUDA paths, safer regex handling, and reproducible tests and
benchmarks.

## What this fork adds

- Bounded, parallel scanning without a hidden result-count limit
- SIMD CPU, native CUDA GPU, and mixed CPU+GPU extraction
- Automatic backend selection based on measured crossover points
- Offset-aware text and CSV output
- Per-pattern regex options, timeouts, and boundary tests
- 33 built-in forensic pattern candidates
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
| `--use-rapids` | Applying compatible built-in regexes after extraction | Use only when RAPIDS/cuDF is already installed and working |

The native CUDA extractor validates its output against the CPU implementation
before accepting a session. An explicit `gpu` request fails clearly when CUDA
cannot be used. `auto` stays on CPU until the input size and minimum string
length reach a measured GPU crossover.

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

# Add experimental RAPIDS regex prefiltering
.\bstrings.exe -f C:\evidence\image.bin --lr all --use-rapids
```

Run `bstrings.exe --help` for the full command reference. Run
`bstrings.exe -p` to see every built-in pattern and its current description.

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
| `--use-rapids` | Try an existing RAPIDS/cuDF installation for regex prefiltering |

`--force-rapids` is retained as a deprecated alias for `--use-rapids`. Despite
the old name, it does not install or force unsupported software.

## Output safety and memory use

Plain, unsorted output can stream to disk as strings are found. Sorting,
filtering, regex-only output, and CSV generation need post-processing and can
use considerably more memory on a large image.

When `-o` is active, bstrings creates a sibling
`<output>.incomplete` marker. The marker is removed only after the run
finishes successfully. If the process exits nonzero or the marker remains, do
not treat that output as a complete evidence set.

Regex matching uses a two-second per-evaluation timeout. The first timeout
stops new regex work and fails the run instead of silently dropping the
problematic evidence item.

## Build and test

You need the .NET 9 SDK. A CUDA-capable NVIDIA GPU and driver are optional and
used only for `gpu` or `hybrid` extraction. The supported release artifact is
Windows x64.

```powershell
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
```

To compare adaptive, forced hit-major, and forced pattern-major regex
scheduling:

```powershell
dotnet run --project dev-tools\regex-benchmark -c Release -- 2000000 7 5
```

The three arguments are hit count, repetitions, and number of representative
patterns. On the reviewed 22-logical-processor host, the adaptive policy uses
hit-major scheduling when there are at least 10,000 hits and fewer patterns
than logical processors. Other workloads use pattern-major scheduling. The
benchmark is included so that policy can be checked on different hardware
rather than treated as universal.

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
