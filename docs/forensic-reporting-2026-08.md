# Forensic reporting review

Date: 7 August 2026

Availability note: these projections are in current source and the complete
v1.9.17 quality release, including runs that also use FLOSS, OCR, and
translation. Current source also projects bounded decoded text children; see
[ADR-0010](architecture/adr-0010-bounded-reversible-decoding.md). See
[download and installation](download-and-install.md).

## Outcome

Integrated `bstrings analyze` now keeps `regex-matches.jsonl` as the
authoritative evidence graph and also writes four review-oriented projections:

| File | Purpose |
| --- | --- |
| `findings.tsv` | Wide, one-row-per-match table for Timeline Explorer, spreadsheet, or dataframe filtering |
| `pattern-histogram.tsv` | Counts for every requested pattern, including zero-count patterns and evidence-class splits |
| `feature-histogram.tsv` | Exact counts for each distinct `(pattern, matched value)` pair |
| `pattern-histogram.html` | Self-contained visual bar chart for rapid triage |

All four files are produced before the result transaction is marked complete.
`summary.json` records their names and row counts.

These projections belong to the integrated `analyze` workflow because that is
the path that has OCR, PDF, FLOSS, translation, parent/child, and engine
lineage. The legacy direct `-f/-d -o <file>` interface remains a flat-output
compatibility path. A native-only report run is:

```powershell
.\bstrings.exe analyze -f D:\evidence\memory.raw `
  -o D:\results\memory-strings `
  --recover-executable-strings off --ocr off --translation off --lr all
```

To include strict Base64 and contextual PowerShell text children without
translation, add `--decode auto`. The encoded parent remains in the evidence
graph; decoded children are appended after raw and translation records and use
the same matcher and report projection.

## Why this shape

[bulk_extractor](https://github.com/simsong/bulk_extractor) separates feature
records from histograms, records an offset or forensic path for features, and
recursively exposes data found through scanners/transformations. Its
[account/PII scanner](https://github.com/simsong/bulk_extractor/blob/main/src/scan_accts.flex)
also demonstrates the value of separate account, telephone, PII, SIN, and
credit-card feature families. Bstrings adopts those useful reporting ideas,
but does not claim byte-for-byte compatibility with bulk_extractor feature
files: its richer engine and parent/child lineage needs more columns.

The official [Timeline Explorer page](https://ericzimmerman.github.io/) says it is intended for viewing,
filtering, grouping, and sorting CSV/Excel data. For this review, the official
Timeline Explorer 2026.5.0 download was additionally inspected locally. The
generic plugin explicitly recognizes a `.TSV` filename and sets its delimiter
to a tab. The downloaded ZIP SHA-256 was
`4C79248786CC6EA342F66E105588F72FC8D571B5BC89ACDB5178A392416D93E9`.
This is why the review projection uses the `.tsv` extension rather than
putting tab-separated data in a misleading `.csv` file.

## Findings contract

`findings.tsv` contains the following column families:

- pattern: name, category, description, primary source, expression, exact
  match, bounded context, and semantic validation label;
- source: full path, directory, filename, extension, artifact type, browser,
  and browser profile;
- location: typed location, match start/length, extractor-supplied source line,
  record-relative line, page, region, and evidence class;
- engine: ordered extraction/transform chain, extractor/runtime/provider,
  model revision/hash, languages, transform outcome, and the dedicated
  `TranslationIntegrity` column;
- decoder: only an actual decoded/deobfuscated origin, explicit decoder
  attribute, or non-translation transform is named as a decoder; detecting a
  Base64-shaped value is not falsely reported as decoding it. A decoded child
  binds its parent, covered span, named profile/policy, charset, exact byte hash,
  outcome, depth, and effective limits; and
- identity: source/parent record IDs, encoding, confidence, and the complete
  attributes object.

`TranslationIntegrity` is filterable as `verified`,
`source-retained-ambiguous`, or `preservation-fallback`. For ambiguous and
fallback children, `AttributesJson` also retains the advisory count or fallback
reason. A preservation fallback is exact source text after rejected model output;
it is not a successful translation and should be reviewed with its parent.

Every TSV record occupies exactly one physical line. Embedded tabs and line
breaks are represented as visible escapes. Windows path separators remain
ordinary backslashes. This makes row counts and header widths deterministic
while keeping the file directly filterable.

`findings.tsv` and `feature-histogram.tsv` intentionally contain the matched
values. They can therefore repeat tokens, credential assignments, PII, and
private-key boundaries in clear text even when the source artifact was access
controlled. Protect, transfer, and dispose of the whole result directory as
sensitive case material. The HTML visualization contains aggregate pattern
counts/descriptions, not matched feature values.

Decoder outputs are sensitive too. `decoded-strings.jsonl` contains published
text, `decoder-assessments.jsonl` records bounded attempted occurrences and
binary/invalid/limit outcomes, and `decoder-work-stats.json` reconciles their
counts and hashes. Binary assessment does not imply malformed input: RFC 4648
Base64 represents arbitrary bytes. This release does not carve, decompress, or
recursively decode those bytes, and it never sends decoded children to the
language detector or translator.

## Pattern coverage and interpretation boundaries

The `pii` group includes email, US and international phone candidates, SSN,
checksum-valid labelled Canadian SIN, labelled/calendar-valid date of birth,
payment card, MOD-97-valid IBAN, ZIP code, VIN, labelled ITIN and UK National
Insurance number candidates, and labelled/checksum-valid NPI values.

The expanded catalogue also includes bounded CPE 2.3 product identifiers,
exact TLP 2.0 markings, labelled RFC 5322 Message-ID values, checksum-valid LEI
candidates, and labelled MD5, SHA-1, SHA-384, and SHA-512 correlation values.
These are format and correlation leads. A checksum or documented shape does not
prove allocation, activity, ownership, compromise, maliciousness, or sender
authenticity. No built-in performs a provider, registry, revocation, or
authentication network request. The research, rejected alternatives, drift
boundaries, and rollback gates are recorded in
[ADR-0009](architecture/adr-0009-bounded-forensic-pattern-expansion.md).

The `credentials` and `browser` groups include structurally validated compact
JWT candidates, credential assignments, URI userinfo, private-key boundaries,
BitLocker recovery keys, browser credential-field names, and Chromium/Firefox
profile artifact paths. JWT validation is structural only as required by the
compact JSON/Base64URL stages in [RFC 7519](https://www.rfc-editor.org/rfc/rfc7519):
it does not authenticate a signature or decrypt JWE ciphertext. Browser field
or path matches identify likely stores; they do not prove a password was
decrypted.

The `registry` group looks for string representations of common persistence,
user-activity, USB, execution, network, and system-identity paths, including
Microsoft's documented
[Run and RunOnce keys](https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys).
It does not parse hive cells, recover deleted keys, interpret binary values, or
provide Registry transaction-log semantics. A dedicated hive parser remains
necessary for those claims.

## Histogram implementation

Pattern counts include requested zeroes so an examiner can distinguish "asked
and absent" from "not searched." Feature counts are exact, not approximate.
Distinct values are accumulated in bounded chunks, sorted to temporary binary
files, and k-way merged. Values longer than 512 characters use a SHA-256 plus
preview key so a huge captured value is not repeatedly duplicated in memory
and output. Temporary chunks and `.partial` files are removed on success or
failure.

## Performance review points

The report stage streams JSONL once and writes findings once. Feature
cardinality is disk-bounded by the chunk limit. Exact per-pattern source-file
counts still retain distinct matched paths in memory; unusually large
multi-file collections should be profiled, and a disk-backed distinct counter
is the next memory-scaling target if those sets become material. During review,
record-relative line attribution was changed from rescanning the text
prefix for every hit to one per-record line index plus binary search. This
removes the previous quadratic worst case when one large extracted record has
many matches.

The separate JSONL-to-TSV pass costs additional sequential I/O and JSON
deserialization. Keeping it as a distinct stage makes failure/completion and
lineage checks auditable. If future large-corpus measurements show that this
pass dominates, the next safe optimization is a shared validated match sink
that writes JSONL and the report projection together, while retaining the
JSONL replay test as an equivalence gate.

### Reviewed host measurements

The Release benchmark generated its JSONL before timing, validated every
physical TSV row/header width after timing, and alternated only repeated runs
of the same implementation. These are local throughput measurements, not a
cross-tool comparison:

| Records | Distinct features | JSONL bytes | Report bytes | Warmed median | Rows/s | Spill path |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 100,000 | 10,000 | 93,282,523 | 69,687,675 | 0.643 s | 155,598 | No |
| 250,000 | 150,000 | 233,232,523 | 180,467,675 | 2.101 s | 118,992 | Yes |

All eight measured rounds passed exactness checks. The first round in each
process was slower (1.875 s and 3.132 s respectively) because it included cold
JIT/cache effects; it was retained in the CSV rather than hidden. Peak process
working set reached about 108 MB in the first run and 112 MB in the spill run,
but that is a process-wide high-water mark and includes fixture-generation
state, so it is not an allocation profile for the report stage alone.

The detailed measurements are in
[`forensic-report-projection-2026-08.csv`](../benchmarks/results/forensic-report-projection-2026-08.csv).

The v1.9.17 66-pattern catalog was exercised through the real Release direct CLI
over generator-v5's 66 one-MiB files. Each run used CPU extraction, ASCII,
`--lr all --ro --off`, quiet file-only output, and exact CSV validation of
every expected `(source file, pattern, value)` pair. All three runs returned
the same 235 rows and all 66 expected pairs exactly twice. Elapsed times were
0.390, 0.333, and 0.350 seconds (median 0.350 seconds, about 188 MiB/s over the
66 MiB cached corpus). This is a warm-cache, small-corpus regression smoke—not
a comparison to the prior 51-pattern catalog and not a large-image throughput
claim. The rows are in
[`forensic-pattern-catalog-2026-08.csv`](../benchmarks/results/forensic-pattern-catalog-2026-08.csv).

That CSV remains an immutable v1.9.17 baseline rather than being relabelled as
evidence for the later 77-pattern source catalogue. The generator-v6 acceptance
run must publish a separately named result after it has reproduced every new
value/offset witness, every legacy pair, and the ADR-0009 performance gates.

The ADR-0009 candidate subsequently passed that separate acceptance run. ASCII
adversarial and UTF-16LE sparse generator-v6 corpora reproduced the exact two
boundary/EOF records for every one of the 77 patterns; 16 MiB dense and
adversarial corpora for the eleven new classes also completed with exact output
and no timeout. Seven alternating base/candidate runs scanned the same 264 MiB
legacy corpus. Median elapsed time changed from 0.5101 to 0.5267 seconds
(+3.26%), and median sampled peak working set changed from 81,661,952 to
85,389,312 bytes (+4.56%). All fourteen measured runs produced the same 940-row
legacy multiset. The raw measurements are in
[`forensic-pattern-catalog-v6-2026-08.csv`](../benchmarks/results/forensic-pattern-catalog-v6-2026-08.csv).
The measured scanner arguments were identical except for executable path:
`-d <66-file-v5-corpus> --mask *.bin -a -u false -m 3 -b 16 --lr all
--ro --off -s -o <fresh-output> -q --processor cpu`. The 4 MiB-per-pattern
input was generated from base commit `88aec2e7e5de00971a5fd7a1269f2f2e13f107f9`
with `--size-mib 4 --segment-mib 1 --encoding ascii --complexity sparse`.
Each executable was warmed once; the seven measured pairs alternated order, and
working set was sampled every 5 ms in addition to reading the process peak.
