# Forensic reporting review

Date: 7 August 2026

These projections are in current source. They work with native extraction,
FLOSS, OCR, translation, and bounded decoded text. See
[ADR-0010](architecture/adr-0010-bounded-reversible-decoding.md) and
[download and installation](download-and-install.md).

## Outcome

Integrated `bstrings analyze` now keeps `regex-matches.jsonl` as the
authoritative evidence graph and also writes four review-oriented projections:

| File | Purpose |
| --- | --- |
| `findings.tsv` | Compact, one-row-per-match table for Timeline Explorer, spreadsheet, or dataframe filtering |
| `pattern-histogram.tsv` | Counts for every requested pattern, including zero-count patterns and evidence-class splits |
| `feature-histogram.tsv` | Exact counts for each distinct `(pattern, matched value)` pair |
| `pattern-histogram.html` | Self-contained visual bar chart for rapid triage |

All four files are produced before the result transaction is marked complete.
`summary.json` records their names and row counts.

`findings.tsv` contains every occurrence and its source context.
`feature-histogram.tsv` is the smaller counted view for repeated values.
Matched values above 512 characters use a SHA-256 digest and bounded preview
in the histogram key.

The default `--lr all` preset selects 74 lower-noise definitions from the
82-pattern catalog. `--lr candidates` selects the eight wider definitions.
Use `--lr "all,candidates"` for the complete catalog. Exact domain groups keep
their documented coverage. `pii` includes `zip`, `registry` includes
`reg_path_candidate`, and `wallets` includes `solana` and `move_address`.

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

The default `b64` finding is separate from decoding. It requires canonical
standard Base64 and either validated decoded text or a recognised binary
signature. `--lr "all,b64_candidate"` adds the broader shape-only partition.
The two pattern names do not report the same span.

The default `email` finding uses a common mailbox form and a top-level domain
in the bundled IANA root-zone snapshot. `--lr "all,email_candidate"` adds the
broader RFC dot-atom partition for uncommon, internal, historic, or currently
undelegated suffixes. Neither pattern verifies DNS or mailbox state.

The default MAC, registry-path, and IPv6 patterns have disjoint wider
partitions named `mac_candidate`, `reg_path_candidate`, and
`ipv6_candidate`. See the
[lower-noise pattern preset decision](architecture/adr-0017-high-confidence-pattern-preset.md).

## Why this shape

[bulk_extractor](https://github.com/simsong/bulk_extractor) separates feature
records from histograms, records an offset or forensic path for features, and
recursively exposes data found through scanners/transformations. Its
[account/PII scanner](https://github.com/simsong/bulk_extractor/blob/main/src/scan_accts.flex)
also demonstrates the value of separate account, telephone, PII, SIN, and
credit-card feature families. Bstrings adopts those useful reporting ideas,
but does not claim byte-for-byte compatibility with bulk_extractor feature
files. Bstrings keeps richer engine and parent/child lineage in
`regex-matches.jsonl`.

The official [Timeline Explorer page](https://ericzimmerman.github.io/) says it is intended for viewing,
filtering, grouping, and sorting CSV/Excel data. For this review, the official
Timeline Explorer 2026.5.0 download was additionally inspected locally. The
generic plugin explicitly recognizes a `.TSV` filename and sets its delimiter
to a tab. The downloaded ZIP SHA-256 was
`4C79248786CC6EA342F66E105588F72FC8D571B5BC89ACDB5178A392416D93E9`.
This is why the review projection uses the `.tsv` extension rather than
putting tab-separated data in a misleading `.csv` file.

## Findings contract

`findings.tsv` uses one fixed header:

| Column | Meaning |
| --- | --- |
| `PatternName` | Name of the built-in or custom pattern that matched |
| `Match` | Exact value matched by the pattern |
| `Context` | Bounded text around the match |
| `SourceFile` | Full source path stored in the match record |
| `ArtifactType` | Review hint derived from the source path or matched value |
| `Location` | Source location, such as a file offset or page region |
| `MatchStart` | Zero-based character position inside the extracted string |
| `AttributesJson` | Source-record ID plus extra record attributes as one JSON object |

See [built-in pattern validity](pattern-validity-review-2026-08.md) for pattern
definitions, sources, validation checks, and interpretation limits.

The header stays the same for every run. A cell can be empty. Tabs and line
breaks inside values use visible escapes, so each finding stays on one physical
line. Windows path separators remain ordinary backslashes.

`SourceDirectory`, `FileName`, and `FileExtension` are not separate columns.
They were derived from `SourceFile` and repeated the same path information.

Every `AttributesJson` object has `sourceRecordId`. This value links the compact
row to the source record named by the authoritative match record.

Use `regex-matches.jsonl` for the complete match record. Each match includes the
pattern expression, description, source, validation method, evidence class,
typed location, origin, transform, model, language, source and parent record
IDs, encoding, confidence, and other provenance.

Translation integrity remains in `AttributesJson` and
`regex-matches.jsonl`. Values include `verified`,
`source-retained-ambiguous`, and `preservation-fallback`. Decoder profile,
policy, byte hash, charset, depth, and limits also remain in the JSONL and the
decoded child's attributes.

`findings.tsv` and `feature-histogram.tsv` contain matched values. This can
include tokens, credential assignments, personal information, and private-key
boundaries. The HTML visualization contains only aggregate pattern counts and
descriptions.

Decoder outputs can contain matched text. `decoded-strings.jsonl` contains
published text, `decoder-assessments.jsonl` records bounded attempted occurrences and
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
