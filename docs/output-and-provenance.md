# Output, completion, and provenance

The complete v1.9.6 quality kit produces native, FLOSS, OCR, language, and
translation records together with the current JSONL, TSV, and histogram report
set. See [download and installation](download-and-install.md).

With a complete version-matched quality bundle, the full enrichment workflow
writes a result set from one command:

```powershell
bstrings.exe analyze -d carved-files --full -o results
```

Treat the result directory as one examination artifact. It records the command,
program version, per-input content hashes, optional-tool versions, model
revision and hash, and completion status; preserve the directory together with
the final process exit status.

## Know when a run is complete

The existence of an output file is not proof that a scan finished.

- Direct core extraction creates a sibling `<output>.incomplete` marker before
  writing and removes it only after the writer flushes and the complete run
  succeeds.
- Enrichment JSONL is written to a sibling temporary file and atomically moved
  into place only after every record, external-tool call, translation, and
  regex operation succeeds.
- A nonzero exit, a remaining `.incomplete` marker, an adapter error, or a model
  hash mismatch makes the affected result incomplete. Retain it for diagnosis,
  but do not report it as a completed examination.
- Copy or archive a completed integrated result directory as a unit so OCR
  assessments, language assessments, parents, translated children, and regex
  hits do not become separated.

The workflow writes `input-files.txt` and `input-manifest.jsonl` once, before
extraction. The manifest records each canonical path, byte length, and SHA-256;
`run.json` and `summary.json` record the manifest filename, its own SHA-256, and
the content-hash algorithm instead of embedding a potentially huge path array.
Native extraction, executable recovery, and OCR consume the same fixed
inventory, so a recursive directory is not independently re-enumerated by each
stage.

When analysis uses the complete offline bundle, `run.json` and `summary.json`
also contain the same `bundleIntegrity` object. It records the bundle manifest
path, the manifest's SHA-256, the verified file and byte counts, the configured
executable path, and the running executable's SHA-256. The running executable
must exactly match the manifest-governed `bstrings.exe`. The native verifier
checks the exact bundle file set before the results directory is created;
missing, extra, linked, resized, hash-mismatched, or executable-mismatched files
stop the run.
This proves which manifest governed the toolchain used for the examination. It
is an integrity record, not publisher authentication or code signing.

Input content is hashed while the manifest is created and verified around and
after requested external stages. OCR also compares applicable input length and
SHA-256 with that manifest and checks that the file did not change while it was
open. The additional reads are visible in `stageSeconds`; they are the price of
refusing to combine results from different file versions. Existing junctions
and other reparse points in the evidence or result path are refused, aliases
are canonicalized, results inside the evidence or verified bundle are refused,
and recursive inventory creation does not traverse reparse-point children.

This protection assumes a controlled examination host. It does not attempt to
defend against another local process that can replace already checked
directories with junctions while a run is active. Run inside the isolated
forensic VM and restrict write access to the evidence parent and result parent;
hostile concurrent filesystem mutation requires operating-system
handle-relative I/O beyond this workflow's threat model.

## Output formats

| Format | Intended use | Important limitation |
| --- | --- | --- |
| Text | Reading native strings quickly | Cannot carry full enrichment lineage |
| CSV | Native strings or regex hits with familiar columns | A filename ending in `.csv` selects it; it cannot represent the complete parent/child record graph |
| JSONL | Enrichment, OCR, translation, language assessments, and attributed regex matches | Use this when provenance matters; one JSON object is stored per line |
| TSV | Filtering completed findings and histograms in spreadsheet/forensic viewers | A projection of `regex-matches.jsonl`; escaped text must be interpreted using the rules below |
| HTML | Viewing the pattern-count chart without another application | Summary visualization only; it is not an evidence record |

On the original direct extraction/search interface, `--off` retains source byte
offsets and `--ro` returns the regex-matched range rather than the complete
surrounding string. Parallel extraction may change row order, so compare
canonical records and offsets rather than assuming two valid runs will have
byte-identical line ordering.

In current source and v1.9.6, every completed integrated `analyze` run
also projects these review files:

- `findings.tsv`: one physical row per regex match with pattern metadata,
  match and bounded context, source path, artifact/browser classification,
  typed location, record-relative line, engine/model lineage, decoder chain,
  evidence class, validation method, IDs, and retained attributes;
- `pattern-histogram.tsv`: one row for every requested pattern, including
  zero-count patterns, with evidence-class and source-file counts;
- `feature-histogram.tsv`: exact `(pattern, matched feature)` counts using a
  bulk_extractor-style `n=<count>` display column; and
- `pattern-histogram.html`: a self-contained visual comparison of requested
  pattern volume.

TSV files are UTF-8 with a byte-order mark and a header row. Tabs, carriage
returns, line feeds, and other C0 controls inside values are escaped as `\t`,
`\r`, `\n`, or `\uXXXX`, so every finding remains on exactly one physical
line. Ordinary backslashes are preserved, which keeps Windows paths readable.
The `.tsv` extension is intentional: the current Timeline Explorer generic
CSV/TSV plugin selects a tab delimiter for that extension. JSONL remains the
authoritative parent/child evidence graph; TSV is the denormalized filtering
surface.

The legacy direct form (`bstrings.exe -f ... --lr ... -o <file>`) still writes
one flat extraction file for scripting compatibility. To obtain the enriched
report set for the same native-only examination, use a result directory:

```powershell
.\bstrings.exe analyze -f D:\evidence\memory.raw `
  -o D:\results\memory-strings `
  --recover-executable-strings off --ocr off --translation off --lr all
```

`analyze` records native byte offsets automatically; its structured stage log
replaces the legacy `--trace` console diagnostics. Use `--full` when FLOSS,
PDF/OCR, language, and translation enrichment are wanted as well.

`RecordLineNumber` is relative to the extracted string record, not necessarily
the source file. `MatchStart` is likewise a UTF-16 character index inside that
record, while the typed `Location` retains the source byte/address/region
coordinate. `SourceLineNumber` is populated only when an extractor
explicitly supplies one. PDF/OCR page and region coordinates remain in their
dedicated/location columns. Browser credential fields and profile paths are
artifact candidates, not a claim that an encrypted browser password was
decrypted. Registry hits are string candidates; bstrings does not structurally
parse binary Registry hives.

The integrated JSONL path caps native text at 2 Mi characters and rejects any
serialized JSONL line above 16 Mi characters. Language triage batches by both
record count and UTF-8 bytes. Before completion, exact record-ID uniqueness,
candidate cardinality, translated-parent existence, and source/location/origin
inheritance are checked with bounded-memory external sorting in temporary disk
partitions. Hash values select partitions but never decide identity.

## String records and locations

Normalized JSONL string records use schema version 1. A record carries a stable
`recordId`, its text, source file, origin, and a typed location.

| Location kind | Interpretation |
| --- | --- |
| `file_offset` | A byte position in the source file or image |
| `program_counter` | A FLOSS stack/tight-string recovery location in executable code |
| `virtual_address` | A FLOSS decoded-string address in the executable's address space |
| `image_region` | A page/frame number and pixel-space OCR bounding box |
| `page_region` | A PDF page and either PDF-point or rendered-pixel region |

Do not present a program counter or virtual address as a raw file offset. A
translated child inherits its parent's location for attribution; that does not
mean the translated characters existed at that location in the evidence bytes.

## Evidence classes

| Evidence class | Meaning |
| --- | --- |
| `byte-native` | The text maps to source bytes and a file offset |
| `derived-extractor` | FLOSS reconstructed the text, OCR recognized it from pixels, or PDFium extracted a document text layer |
| `derived-translation` | A local translation model produced the text from an identified parent record |

Derived evidence can create strong leads, but it is not interchangeable with a
byte-native finding. Confirm consequential OCR and translated matches against
their page/parent and surrounding source evidence.

## OCR records and assessments

When OCR is enabled, `ocr-strings.jsonl` contains `pdf-text` and `ocr` string
records. PDF text-layer rows use PDF-point coordinates. Raster rows retain the
page/frame number, pixel bounding box, confidence, rendered-raster SHA-256 and
DPI, model-pack/component hashes, runtime hash, requested/resolved provider,
and source file length/SHA-256.

`ocr-assessments.jsonl` contains one row per fixed-inventory input, including
`not-applicable` rows. Each assessment records status, pages, rendered pages,
PDF-text/OCR record counts, engine/model/revision/hash, component hashes,
runtime/provider, source identity, mode, and enforced air-gap state. The .NET
orchestrator independently checks record cardinality, source identity, page and
coordinate bounds, model/runtime/provider identity, parent ordering, and the
assessment totals before merging OCR strings into downstream processing.

OCR is not byte recovery. A box points to the visual location from which a
model inferred text; inspect that page when a finding matters. See
[OCR and document analysis](ocr-and-document-analysis.md).

## Language assessments

Full analysis can assess eligible text before translation. Each
`language-assessment` record identifies the source record and records:

- detector name and version;
- accurate, fast, or adaptive profile;
- target and predicted language;
- predicted, target-language, and second-place confidence scores;
- target-language margin and configured thresholds;
- high-recall, balanced, or high-precision policy;
- decision and whether the source became a translation candidate; and
- any detector error.

The assessment is useful even when a string is not translated: it explains why
the record was treated as target-language, ambiguous, non-linguistic, or a
translation candidate. High-recall policy sends uncertain detector failures to
translation rather than silently discarding them.

## Translation lineage

A translated string is a new child record. It retains `parentRecordId` and a
`transform` object containing the engine and runtime version, model ID, exact
revision, verified model SHA-256, source-language mode, target language, and
execution details such as CPU/CUDA policy and enforced air-gap state. It also
records `outcome` as `translated` or `unchanged`; a successful identity result
still gets a child instead of disappearing from the audit trail.

The integrated workflow requires exactly one child for every candidate and
checks that its source file, location, origin, target, model, revision, and
model hash match the verified parent and run configuration. llama.cpp output
must contain one terminal `stop` choice and prove that the complete prompt was
accepted. Truncation, empty output, a missing candidate, or an extra child
leaves the run incomplete.

The advanced MADLAD adapter separately requires the complete input and an
EOS-terminated row. Those outputs use the same record schema, but MADLAD is not
an engine selectable by the integrated `bstrings.exe analyze` workflow.

The translation stage fails closed if it changes a protected structured token,
including an email, URL, IP address, hash, path, CVE, GUID, host/port value,
file name, or placeholder. This protects important identifiers but does not
make machine translation authoritative. Review the parent whenever a finding
matters to attribution or reporting.

## Related guides

- [Extractor, language-triage, and translation enrichment](enrichment-pipeline.md)
- [OCR and document analysis](ocr-and-document-analysis.md)
- [Air-gapped deployment and verification](air-gapped-deployment.md)
- [Built-in pattern validity and false-positive controls](pattern-validity-review-2026-08.md)
