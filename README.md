# bstrings

`bstrings` extracts code-page and UTF-16LE strings from files, directories, and
supported raw-disk targets. It can filter the extracted strings with literal or
regular-expression searches and write text or CSV output.

This fork preserves Eric Zimmerman's original `bstrings` functionality while
adding a bounded, parallel CPU pipeline, streaming output, expanded tests, and
an optional experimental NVIDIA RAPIDS regex path.

## Highlights

- Parallel, bounded chunk processing with ordered, offset-aware results
- Code-page selection and configurable byte and UTF-16LE character ranges
- Literal, file-backed, custom-regex, and built-in-regex filtering
- Text and CSV output, sorting, quiet modes, and optional source offsets
- Single-file Windows x64 publishing on .NET 9
- Optional RAPIDS/cuDF regex processing when a working installation already
  exists

The normal scanner uses the CPU. `--use-rapids` affects regex processing only;
it does not accelerate string extraction. RAPIDS support is experimental and
falls back to the CPU regex engine when initialization or processing fails.
`bstrings` does not install Python, CUDA, or RAPIDS.

## Quick start

```powershell
# Extract strings from one file
.\bstrings.exe -f C:\evidence\image.bin

# Save without printing every hit to the console
.\bstrings.exe -f C:\evidence\image.bin -s -o C:\results\strings.txt

# Search with a built-in regular expression
.\bstrings.exe -f C:\evidence\image.bin --lr email --ro -o C:\results\emails.txt

# Use several built-in expressions
.\bstrings.exe -f C:\evidence\image.bin --lr email,url3986,ipv4 --ro

# Preserve a comma inside a custom regex quantifier
.\bstrings.exe -f C:\evidence\image.bin --lr "\d{1,3}(?:,\d{3})*"

# Scan a directory recursively
.\bstrings.exe -d C:\evidence --mask "*.bin" -s -o C:\results\all.txt

# Decode byte strings with Windows-1252 and include the source offset
.\bstrings.exe -f C:\evidence\image.bin --cp 1252 --off

# Request experimental RAPIDS regex processing
.\bstrings.exe -f C:\evidence\image.bin --lr all --use-rapids
```

Run `bstrings.exe --help` for the complete command reference and
`bstrings.exe -p` for the built-in regex catalog.

## Important options

| Option | Purpose |
| --- | --- |
| `-f <path>` | Scan one file |
| `-d <path>` | Scan a directory recursively |
| `-o <path>` | Write results to text, or CSV when the extension is `.csv` |
| `-a <bool>` | Enable or disable code-page strings; enabled by default |
| `-u <bool>` | Enable or disable UTF-16LE strings; enabled by default |
| `-m <n>` | Minimum string length; default `3` |
| `-x <n>` | Maximum string length; unlimited by default |
| `-b <MB>` | Chunk size from 1 to 1024 MB; `0` selects automatically |
| `-q` | Hide the header and final summary |
| `-s` | Do not print hits to the console |
| `--ls <text>` | Return strings containing literal text |
| `--lr <value>` | Built-in names, a custom regex, comma-separated names, or `all` |
| `--fs <path>` | Read literal searches from a file |
| `--fr <path>` | Read regex searches from a file |
| `--ar <range>` | Code-page byte range, such as `[\x20-\x7E]` |
| `--ur <range>` | UTF-16LE range, such as `[\u0020-\u007E]` |
| `--cp <id>` | Code page used to decode byte strings; default `1252` |
| `--ro` | Output the regex match rather than the whole extracted string |
| `--off` | Include source byte offsets |
| `--sa` / `--sl` | Sort alphabetically or by length |
| `--use-rapids` | Try an existing RAPIDS/cuDF installation |

`--force-rapids` remains as a deprecated compatibility alias for
`--use-rapids`; it does not install third-party software.

## Output and memory behavior

Unfiltered, unsorted text output can stream directly to disk. Filtering,
sorting, regex-only output, and CSV generation require post-processing and are
therefore collected before the final output is written. Large result sets can
still require substantial memory when one of those modes is used.

No hidden result-count limit is applied. If a run cannot fit in available
memory, narrow the search, increase the minimum string length, or use
unfiltered text output.

## Build and test

Requirements:

- .NET 9 SDK
- Windows for the supported `win-x64` release artifact

```powershell
dotnet restore bstrings.sln
dotnet build bstrings.sln -c Release --no-restore
dotnet test bstrings.sln -c Release --no-build

dotnet publish bstrings\bstrings.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true
```

The benchmark corpus generator accepts a size in MiB and an output path:

```powershell
dotnet run --project dev-tools\benchmark-generator -- 256 .\benchmark-256mb.dmp
```

## Releases

Pull requests and pushes to `master` run restore, build, test, and publish
validation. A GitHub release is created only when a tag beginning with `v` is
pushed. See [VERSIONING.md](VERSIONING.md) for the release procedure.

## Attribution

Original project and continuing upstream development:
[EricZimmerman/bstrings](https://github.com/EricZimmerman/bstrings).

Fork enhancements:
[Donovoi/bstrings](https://github.com/Donovoi/bstrings).

Project documentation:
[Introducing bstrings, a Better Strings utility!](https://binaryforay.blogspot.com/2015/07/introducing-bstrings-better-strings.html).

Open-source development funding and support for the original project was
provided by [SANS Institute](https://www.sans.org/) and
[SANS DFIR](https://www.sans.org/digital-forensics-incident-response/).

See [LICENSE.md](LICENSE.md) for licensing terms.
