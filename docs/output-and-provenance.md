# Output, completion, and provenance

The complete v1.9.17 quality kit produces native, FLOSS, OCR, language, and
translation records together with the current JSONL, TSV, and histogram report
set. See [download and installation](download-and-install.md).

Current source after v1.9.17 lets an examiner select native extraction, FLOSS,
or OCR as the sole source producer; the already-published v1.9.17 binaries do
not contain `--native-extraction`. Translation remains a transform over records
emitted by at least one selected producer. Matching, reports, input
verification, and completion records remain part of every `analyze` result
regardless of engine selection.

With a complete version-matched quality bundle, the full enrichment workflow
writes a result set from one command:

```powershell
bstrings.exe analyze -d carved-files --full -o results
```

Treat the result directory as one examination artifact. It records the command,
program version, per-input content hashes, optional-tool versions, model
revision and hash, and completion status; preserve the directory together with
the final process exit status.

Console lines beginning `Progress: analysis:` report the overall completed
stage fraction. Native extraction, model and bundle hashing, language triage,
translation filtering, and offline translation also expose chunk, byte, or
record percentages. These are deterministic work-unit ratios rather than an
estimated time remaining, so throughput changes can make equal percentage
steps take different amounts of time.

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
When native extraction is selected, it consumes that complete fixed inventory,
hashes, calibrates, rewinds, and scans the same write-denying source handle,
rechecks it after scanning, and publishes native JSONL atomically. When native
is disabled, `native-strings.jsonl` is atomically empty and no native scan or
native pre/post stage runs.

When FLOSS or OCR is selected, content routing writes `content-routing.jsonl`,
exactly one identity-bound row per input, then projects ordered `floss-input-*`
and `ocr-input-*` inventory/manifest pairs. Specialist modes bind the Magika
classifier identity. A native-only run does not start Magika and retains the
verified input manifest plus native records without fabricating a classifier or
specialist ledger. Specialists consume only proven subsets, so a
recursive directory is not independently re-enumerated and a zero-candidate
stage does not load its runtime. `run.json` and `summary.json` record the
routing policy, manifest SHA-256, candidate counts, classifier errors, and
conflicts when routing ran. Specialist runs also bind `engine-status.jsonl` by SHA-256. That terminal ledger
has three rows per input, one each for native extraction, FLOSS, and OCR, keyed
by the routing decision and source identity.

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

The complete quality bundle is an atomic trust profile. Disabling an engine at
runtime does not exclude its bytes from whole-manifest verification: corruption
of an unselected component still blocks the run. This prevents unverified DLLs,
Python packages, or models in the same loadable tree from being mistaken for a
partially trusted installation. Independently installable physical capability
profiles are not part of v1.9.17 and remain deferred by
[ADR-0008](architecture/adr-0008-independent-engine-execution.md).

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

In current source and v1.9.17, every completed integrated `analyze` run
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
  --native-extraction on --recover-executable-strings off `
  --ocr off --translation off --lr all
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

## Engine terminal statuses

`engine-status.jsonl` records whether each engine was eligible and selected,
its terminal `succeeded`, `not-applicable`, or `disabled-by-user` state, and its
output-record count. Selected engines remain present when they succeed with
zero records, so an empty FLOSS or OCR result cannot be confused with an engine
that was never attempted. Native-off produces one `disabled-by-user`, selected
`false`, zero-output native row for every input. The file is written atomically
only after source identity, route lineage, OCR assessments, and specialist
output coverage agree. The `engineStatuses` objects in `run.json` and
`summary.json` record its filename, SHA-256, row count, per-engine terminal
counts, and output-record totals.

## OCR records and assessments

When OCR is enabled, `ocr-strings.jsonl` contains `pdf-text` and `ocr` string
records. PDF text-layer rows use PDF-point coordinates. Raster rows retain the
page/frame number, pixel bounding box, confidence, rendered-raster SHA-256 and
DPI, model-pack/component hashes, runtime hash, requested/resolved provider,
and source file length/SHA-256.

`ocr-assessments.jsonl` contains one row per routed OCR candidate. Inputs not
routed to OCR remain visible in `content-routing.jsonl`; the OCR worker still
re-sniffs every candidate and may record `not-applicable` when a conservative
extension or classifier signal is disproved. Each assessment records status,
pages, rendered pages,
PDF-text/OCR record counts, engine/model/revision/hash, component hashes,
runtime/provider, source identity, routing decision, mode, and enforced air-gap state. The .NET
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
- target-language margin, configured and effective thresholds, and the declared
  12-decimal report-score precision;
- explicit raw `confidenceGatePassed` and `marginGatePassed` outcomes;
- high-recall, balanced, or high-precision policy;
- decision and whether the source became a translation candidate;
- a compact `translationRouting` shadow observation containing one bounded
  routing code; and
- any detector error.

The assessment is useful even when a string is not translated: it explains why
the record was treated as target-language, ambiguous, non-linguistic, or a
translation candidate. High-recall policy sends uncertain detector failures to
translation rather than silently discarding them. Full remains high-recall.
The optional high-precision policy sets each effective threshold to the greater
of the configured value and 0.65 confidence/0.15 target margin; these scores are
not calibrated probabilities.

Shadow translation routing never removes a candidate in this implementation.
`prospective-*` is an auditable measurement, not an authoritative exclusion;
`shadow-*`, `retain`, and all router errors follow the existing fail-open path.
The routing object does not repeat source text,
location, origin, or arbitrary attributes; the enclosing assessment row binds
it to the canonical record. Existing assessment fields already state the
detector outcome and candidate selection, while `run.json` and `summary.json`
bind the routing codebook to its policy version and aggregate counts once per
run. Candidate and canonical-parent cardinality remain unchanged until a
separately accepted policy version enables a validated bypass.

The run-level `translationRouting` summary additionally reports:

- `routingEvaluations`: batch-unique records actually evaluated by the bounded
  router;
- `detectorEligibleRecords`: occurrence records that needed a Lingua result;
- `detectorExecutions`: actual Lingua invocations; and
- `detectorReuseHits`: eligible occurrences served by successful same-batch
  reuse.

`detectorExecutions + detectorReuseHits` must equal
`detectorEligibleRecords`, and the router counters must reconcile with triage
cardinality or publication fails. C# triage also classifies a bounded set of
origin flags for each pending record: native static, FLOSS, FLOSS-decoded, OCR,
PDF text, derived translation, or unknown. The structured shadow router does
not currently use those flags to change its code; any future scorer may use
them only to force retention, never suppression.

The Python worker atomically publishes `translation-work-stats.json` after the
translated output succeeds. Its privacy-safe schema 1 contains only aggregate
counters:

- `candidateOccurrences`: candidate parent occurrences;
- `textDecisions`: first exact-text decisions within each translation
  window/call, not globally distinct text;
- `protectedOnlyBypassTexts`, `runCacheHits`, `translationCacheHits`, and
  `translatorInputTexts`: the mutually exclusive outcomes of those decisions;
- `translatorRequests`: translator batch dispatches;
- `translatorInputTexts` and `modelResults`: texts dispatched and per-input
  outcomes;
- `modelFallbacks`: model outcomes rejected into preservation fallback;
- `translatedChildOccurrences`: emitted child occurrences; and
- `preservationFallbackChildOccurrences`: emitted fallback child occurrences,
  including repeated/cache-served occurrences.

Within-window duplicate occurrences do not add `textDecisions`. The first use
of the same exact text in a later window does add a decision, after which
`runCacheHits` exposes its cross-window reuse. Consequently `textDecisions` is
not a global unique-text total, and `translatorRequests` is not an occurrence
total or a claim about undocumented internal server retries.

Managed C# requires a bounded physical non-reparse file, an exact schema-1
property set, nonnegative integer counters, and a matching candidate/child,
decision-bucket, translator-input/model-result, and fallback cardinality. It
computes the artifact SHA-256 and projects the validated result under
`translationWork` in both `run.json` and `summary.json`, with `file`, `sha256`,
and every counter. Any missing file, schema/property error, hash/read failure,
or cardinality mismatch leaves the run incomplete. The artifact contains no
record identifiers or text; its case-derived counts and hash still remain
private examination data.

Classification and candidate membership use the detector's unrounded values.
Only the five confidence/margin numbers written to JSON are rounded to the
declared precision, bounding insignificant parallel floating-point tails. The
gate fields preserve the authoritative raw comparison when a displayed value
lies next to a threshold; independent detector calls may still differ by one
unit in the final published decimal without changing that decision.

## Translation lineage

A translated string is a new child record. It retains `parentRecordId` and a
`transform` object containing the engine and runtime version, model ID, exact
revision, verified model SHA-256, source-language mode, target language, and
execution details such as CPU/CUDA policy and enforced air-gap state. It also
records `outcome` as `translated` or `unchanged`; a successful identity result
still gets a child instead of disappearing from the audit trail.

Current Full execution provenance distinguishes the requested and resolved
translation device, the pre-evidence self-test, runtime/backend hashes, device
and available driver identity, requested and observed layer placement,
parallelism, decoding, and reported host/GPU buffers. On the accepted Windows
sm89 command, full offload is truthfully recorded as 33/33 layers plus a
410.69 MiB `CPU_Mapped` model buffer. `auto` may record a CUDA preflight failure
and resolved CPU only when that switch completed before evidence work. Explicit
CUDA fails closed, and a later failure cannot rewrite the provider provenance
by switching to CPU. Hybrid and p4 are not Full automatic plans. See
[ADR-0006](architecture/adr-0006-q4-cuda-full-translation.md).

Each child also records `attributes.translationIntegrity`:

- `verified`: hard structured identifiers have exact code-point and occurrence
  equality and no advisory alphabetic-hyphen token was present;
- `source-retained-ambiguous`: hard checks passed, but the source contained one
  or more advisory alphabetic-hyphen tokens; or
- `preservation-fallback`: a model result failed hard-identifier retention, was
  discarded, and the exact source text was retained instead.

`source-retained-ambiguous` children carry
`translationAmbiguousIdentifierCount` as a positive integer.
`preservation-fallback` children carry the non-empty
`translationIntegrityReason` and must record `outcome` as `unchanged`. The
rejected model output is never evidence and is not published.

The integrated workflow requires exactly one child for every candidate and
checks that its source file, location, origin, target, model, revision, and
model hash match the verified parent and run configuration. llama.cpp output
must contain one terminal `stop` choice and prove that the complete prompt was
accepted. Truncation, empty output, a missing candidate, or an extra child
leaves the run incomplete. One isolated hard-identifier mismatch instead yields
one explicit fallback child. More than 1% fallbacks after at least 100 distinct
model results, or 100 consecutive fallbacks, opens the run-level circuit breaker
and leaves the transaction incomplete.

Managed completion validation reads translation and candidate files once each
in lockstep. It requires every child to be in candidate order, retain exact
lineage, and compare fallback text to its current parent with ordinal equality;
no candidate text is indexed. Downstream pattern validation additionally replays
the translation file before the merged stream, avoiding another pass over the
much larger raw parent section. Its temporary disk index contains only fallback
parent/child identities and fallback text, creates a 2 MiB bucket table when
needed, and grows with fallback records/text rather than total candidate text.
The index is strictly removed before report output publication; cleanup failure
fails the stage and leaves prior output untouched.

The advanced MADLAD adapter separately requires the complete input and an
EOS-terminated row. Those outputs use the same record schema, but MADLAD is not
an engine selectable by the integrated `bstrings.exe analyze` workflow.

The translation stage preserves hard structured tokens such as emails, URLs,
IP addresses, hashes, paths, CVEs, GUIDs, host/port values, filenames,
placeholders, underscore-bearing tokens, and all-uppercase ASCII code forms.
Alphabetic hyphenation alone is advisory because ordinary language can use the
same shape. This protects important identifiers but does not make machine
translation authoritative. Review the parent whenever a finding matters to
attribution or reporting.

Identifier-only records are retained as source evidence and classified as
non-linguistic rather than being sent to the model. Mixed natural-language
records remain eligible, and the protected-token check compares occurrence
counts as well as values. `--translation-strict-determinism` selects the
single-slot, no-prompt-cache path for maximum repeatability on the same accepted
runtime; it does not promise identical output across different hardware or
drivers.

Exact source/configuration pairs are deduplicated through a run-local SQLite
cache with a bounded in-memory hot set. It emits one child per parent in original
order, never reuses text across examinations, and is removed on normal
completion and handled failure. Parent-side `finally` cleanup also covers a
killed or cancelled translation child while the managed process remains alive.
It removes only exact physical SQLite cache files and their standard sidecars
and exact `<translated-filename>.partial.*` staged-output siblings from the
translation output directory; reparse ambiguity or cleanup failure fails the
stage without replacing prior translated output. Console/log progress reports
percentage, record rate, ETA, cache hits, distinct model inputs, and fallback
count. Those statistics explain work avoided; they do not change record
provenance or imply that a large high-recall translation run will be short.

## Related guides

- [Terminal help and command reference](command-reference.md)
- [Extractor, language-triage, and translation enrichment](enrichment-pipeline.md)
- [OCR and document analysis](ocr-and-document-analysis.md)
- [Air-gapped deployment and verification](air-gapped-deployment.md)
- [Built-in pattern validity and false-positive controls](pattern-validity-review-2026-08.md)
