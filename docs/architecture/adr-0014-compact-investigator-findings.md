# ADR-0014: Use a compact investigator findings table

- **Status:** Accepted for the next major release
- **Date:** 2026-08-16
- **Scope:** `findings.tsv`, report documentation, packaged acceptance, and report benchmarks
- **Decision type:** public report schema and investigator workflow
- **Review method:** three independent reviews and rotated critiques under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source research; repository and runtime audit; adversarial detractor review of compatibility, provenance, and usability
- **Implementation state:** implemented and measured in current source; full acceptance in progress; v2.1.2 remains immutable

## Context

`findings.tsv` has 48 columns. It mixes a review table with pattern definitions,
browser hints, engine details, model details, transform lineage, record IDs, and
validation labels. Many fields are empty when an engine does not apply.

The report also repeats path data. `SourceDirectory`, `FileName`, and
`FileExtension` are calculated from `SourceFile` immediately before each row is
written. They do not identify separate evidence.

The authoritative `regex-matches.jsonl` record already retains the complete
pattern, location, origin, transform, parent, evidence-class, and attribute
data. `findings.tsv` is a projection for filtering and review.

The requested review fields are:

1. `PatternName`;
2. `Match`;
3. `Context`;
4. `SourceFile`;
5. `ArtifactType`;
6. `Location`;
7. `MatchStart`; and
8. `AttributesJson`.

`Location` and `MatchStart` are separate fields. `Location` identifies the
source location. `MatchStart` is the zero-based character position inside the
extracted string. Joining them would remove that distinction.

## Pre-registered claims and falsifiers

The review tests these claims:

1. The compact table keeps the fields needed for first-pass filtering.
2. Removing derived path columns does not remove independent evidence.
3. Rich provenance remains available in `regex-matches.jsonl`.
4. A fixed header is safer for tools than omitting columns dynamically.
5. The smaller projection does not increase report time or output size.

The decision is falsified if a compact row cannot be traced to its source
record, if a retained field changes meaning, if row widths vary, if the JSONL
loses data, or if report time or output size materially increases.

## Independent first-round reviews

### External practice and primary sources

The external review found three useful practices:

- `bulk_extractor` keeps feature output small: a feature is associated with an
  offset or forensic path, its value, and context. Histograms are separate.
- Plaso offers fixed-field formats and a dynamic output format. This separates
  compact review output from richer stored event data.
- The W3C tabular-data model requires each row in a table to have the same
  number of cells. Empty cells are valid. This favors one stable compact header
  over per-row or per-run column removal.

The review supports a compact table. It does not support deleting the rich
source record.

### Repository and runtime audit

The runtime review traced the current row writer. It confirmed that
`SourceDirectory`, `FileName`, and `FileExtension` are derived only from
`SourceFile`. It also confirmed that report generation validates the complete
JSONL record before projection and uses that record for the histograms.

The audit found 48 current columns. It recommended the eight selected columns
as a fixed schema. It rejected combining `Location` and `MatchStart` because
they describe different coordinate systems.

The synthetic pre-change benchmark at commit `577529b` used 100,000 attributed
matches, 10,000 distinct features, and five rounds. Its median elapsed time was
0.653 seconds. Each round wrote 69,787,696 output bytes. The first round was a
warm-up outlier and remains included in the
[raw result](../../benchmarks/results/forensic-report-compact-schema-2026-08.csv).

The compact candidate used the same command and fixture. Its median elapsed
time was 0.431 seconds, about 34 percent lower. Each round wrote 15,704,582
bytes, about 77.5 percent less. Every baseline and candidate round passed the
exact row and histogram checks.

### Adversarial detractor

The detractor identified the main risk: investigators may already filter the
wide table by evidence class, validation method, transform, or record ID.
`AttributesJson` does not contain every omitted field, so it cannot be described
as a replacement for complete provenance.

The detractor required:

- an exact, fixed header test;
- a plain-language column reference;
- a clear link to `regex-matches.jsonl` for omitted details;
- unchanged match validation and histogram counts;
- packaged smoke coverage; and
- a major version before publication because the public table schema changes.

## Rotated critiques

The external review challenged the runtime proposal to remove empty columns
dynamically. A table needs stable columns even when a retained cell is empty.
The synthesis therefore removes unselected columns from the schema, not from
individual rows or individual runs.

The runtime review challenged direct adoption of `bulk_extractor` feature-file
shape. Bstrings has parent-child and engine provenance that `bulk_extractor`
feature files do not represent. The synthesis keeps that provenance in the
authoritative JSONL instead of claiming that the TSV is complete evidence.

The detractor challenged both reviews on compatibility. Existing scripts can
depend on the 48-column header. The first release containing this schema must
therefore use a new major version. The source tree can retain its current
version until release preparation, as required by `VERSIONING.md`.

No material disagreement remains.

## Considered options

1. **Keep all 48 columns.** Rejected because it preserves the reported noise
   and redundant path fields.
2. **Omit columns only when every value is empty.** Rejected because the header
   would vary by evidence and engine selection.
3. **Add a second compact TSV and keep the wide TSV.** Rejected because users
   would still open the noisy primary findings file and two similar reports
   would create an unclear default.
4. **Replace the TSV with JSONL.** Rejected because JSONL is not the same simple
   filtering surface.
5. **Use one fixed compact TSV and retain rich JSONL.** Accepted.

## Decision

`findings.tsv` has this exact header:

```text
PatternName\tMatch\tContext\tSourceFile\tArtifactType\tLocation\tMatchStart\tAttributesJson
```

Every row has eight cells. A retained cell can be empty. Columns do not appear
or disappear based on the selected engines or the input.

`SourceFile` keeps the exact source path stored in the match record.
`ArtifactType` remains a review hint derived from the source path or matched
value. `Location` keeps the typed location value. `MatchStart` keeps the
zero-based character position inside the extracted string.

`AttributesJson` keeps the record's attribute object. It is not a replacement
for origin, transform, evidence-class, validation, or record-ID fields.

`regex-matches.jsonl` remains the authoritative match record. Histograms and
the HTML chart keep their current schemas and counts.

Documentation defines every compact column and points to the JSONL for pattern
expressions, descriptions, sources, validation, origin, transform, parent,
record ID, and other provenance.

The first public release with this header is a major release. Published v2.1.2
artifacts and documentation remain unchanged.

## Non-negotiable invariants

1. The compact header and order are exact and stable.
2. Every physical row has the same number of cells as the header.
3. Tabs, line breaks, and control characters remain escaped.
4. `SourceFile`, `Location`, `MatchStart`, and `AttributesJson` retain their
   existing values and meaning.
5. Match validation runs before projection.
6. `regex-matches.jsonl` retains complete provenance.
7. Pattern and feature histogram counts do not change.
8. Translation and decoding details remain in JSONL even when they are not TSV
   columns.
9. Packaged help and documentation explain the compact schema.
10. Tests and public fixtures contain no private case values.

## Acceptance gates

- **Schema gate:** Assert the exact eight-column header and exact row width for
  native, OCR, translation, and decoding records.
- **Value gate:** Assert every retained field, including escaped context,
  location, match start, artifact type, and attributes JSON.
- **Provenance gate:** Assert that omitted origin, transform, validation,
  evidence class, and IDs remain unchanged in `regex-matches.jsonl`.
- **Count gate:** Assert finding, pattern-histogram, and feature-histogram row
  counts before and after the projection change.
- **Package gate:** Run the integrated kit smoke and require the exact compact
  header plus retained translation integrity in `AttributesJson`.
- **Documentation gate:** Verify Markdown links and package the ADR and column
  reference.
- **Performance gate:** Run five synthetic 100,000-row rounds before and after
  the change. Candidate median time must not exceed baseline by more than five
  percent. Candidate output bytes must be lower in every round.
- **Compatibility gate:** Do not publish this schema under version 2.
- **Privacy gate:** Use only authored synthetic values in tests, benchmarks,
  documentation, commits, and release assets.

## Strongest detractor and resolution

A wide TSV lets a user filter engine, model, evidence class, validation, and
record IDs without parsing JSON. Removing those columns adds a second step for
deep provenance review.

That objection is valid. It does not outweigh the stated first-pass filtering
problem because the same complete fields remain in `regex-matches.jsonl`.
Documentation must make the boundary clear. If users need a second tabular
provenance view, it should have a distinct name and a separate measured design;
it should not silently widen `findings.tsv` again.

## Falsifiers and revisit triggers

Stop publication if any retained value changes, row widths vary, a JSONL field
is removed, histogram counts drift, the packaged smoke loses translation
integrity, or the performance gate fails.

Before publication, roll back by restoring the wide header and row writer. Do
not publish the compact schema under version 2.

After publication, correct a defect in a new version. Reintroducing a wide
table or adding another report needs a new decision because it changes the
accepted default.

## Consequences

Investigators get a smaller findings table with the fields used for first-pass
filtering. Redundant path columns and engine-specific empty columns no longer
obscure the match data.

Users who need complete pattern and provenance details read
`regex-matches.jsonl`. Existing scripts that depend on the 48-column header
must update for the next major release.

The findings output becomes smaller and should take less time to write. The
histograms and visualization remain unchanged.

## Primary references

- [W3C Model for Tabular Data and Metadata on the Web](https://www.w3.org/TR/tabular-data-model/), especially the fixed row-and-column model and allowance for empty cells.
- [`bulk_extractor` output feature files](https://github.com/simsong/bulk_extractor/wiki), which separate compact feature records from histograms and other processing.
- [Plaso output and formatting](https://plaso.readthedocs.io/en/latest/sources/user/Output-and-formatting.html), which documents fixed-field and dynamically selected output formats over richer stored events.
- [Current output and provenance contract](../output-and-provenance.md).
- [Current report implementation](https://github.com/Donovoi/bstrings/blob/master/bstrings/ForensicReportCore.cs).
- [Report benchmark](https://github.com/Donovoi/bstrings/blob/master/benchmarks/ForensicReportBenchmark/Program.cs).
