# Scale benchmark

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
