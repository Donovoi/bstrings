# Output, completion, and provenance

The normal complete workflow writes a result set from one command:

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
- Copy or archive a completed integrated result directory as a unit so
  language assessments, parents, translated children, and regex hits do not
  become separated.

The workflow writes `input-files.txt` and `input-manifest.jsonl` once, before
extraction. The manifest records each canonical path, byte length, and SHA-256;
`run.json` and `summary.json` record the manifest filename, its own SHA-256, and
the content-hash algorithm instead of embedding a potentially huge path array.
Both native extraction and executable recovery consume the fixed inventory, so
a recursive directory is not independently re-enumerated by each stage.

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

Input content is hashed while the manifest is created and verified again after
native extraction. A recovery run verifies it once more after Magika/FLOSS.
That means two complete input-hash reads without recovery and three with it.
The cost is visible in `stageSeconds`; it is the price of refusing to combine
results from different file versions. Existing junctions and other reparse
points in the evidence or result path are refused, aliases are canonicalized,
results inside the evidence or verified bundle are refused, and recursive inventory
creation does not traverse reparse-point children.

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
| JSONL | Enrichment, translation, language assessments, and attributed regex matches | Use this when provenance matters; one JSON object is stored per line |

On the original direct extraction/search interface, `--off` retains source byte
offsets and `--ro` returns the regex-matched range rather than the complete
surrounding string. Parallel extraction may change row order, so compare
canonical records and offsets rather than assuming two valid runs will have
byte-identical line ordering.

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

Do not present a program counter or virtual address as a raw file offset. A
translated child inherits its parent's location for attribution; that does not
mean the translated characters existed at that location in the evidence bytes.

## Evidence classes

| Evidence class | Meaning |
| --- | --- |
| `byte-native` | The text maps to source bytes and a file offset |
| `derived-extractor` | FLOSS reconstructed the text through language, stack, tight-loop, or decoding analysis |
| `derived-translation` | A local translation model produced the text from an identified parent record |

Derived evidence can create strong leads, but it is not interchangeable with a
byte-native finding. Confirm consequential translated matches against their
untranslated parent and surrounding source evidence.

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
- [Air-gapped deployment and verification](air-gapped-deployment.md)
- [Built-in pattern validity and false-positive controls](pattern-validity-review-2026-08.md)
