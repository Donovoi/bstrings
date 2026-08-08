# ADR-0001: Early fail-open content routing

- **Status:** Accepted and implemented; publication remains release-gated
- **Date:** 2026-08-08
- **Scope:** integrated `analyze` pipeline
- **Decision type:** forensic coverage, stage ordering, and performance
- **Review method:** Robin round under the
  [high-level decision policy](decision-review-policy.md)
- **Perspectives:** repository audit, forensic detractor, vendor/upstream
  research, document/OCR research, and performance review

## Context

At the time of this decision, integrated analysis creates and hashes the input
inventory, runs native extraction over every supplied file, then invokes Magika
inside executable recovery solely to decide whether FLOSS should run. OCR makes
its own signature/extension decision, and language/translation decisions occur
after native, recovered, and OCR records are merged.

That arrangement preserves independent native and OCR coverage, but reloads or
repeats classification work and cannot use one early observation to avoid
unneeded heavy-component startup. It also gives the Magika PE label too much
authority inside recovery: a classifier miss can suppress FLOSS.

Magika is useful for triage, but it is not a forensic type oracle. Its upstream
documentation says that it examines a limited subset of a file, uses
per-content thresholds, can return generic labels, and reports average rather
than zero-error accuracy. A single-label gate is also insufficient for
polyglots, malformed files, nested containers, and raw images containing
embedded artifacts.

## Considered options

1. **Keep classification inside FLOSS recovery.** Lowest implementation cost,
   but repeats work and cannot guide other expensive stages.
2. **Make Magika an authoritative early gate.** Maximizes apparent pruning, but
   gives one probabilistic classification a pipeline-wide false-negative blast
   radius. Rejected.
3. **Create an early, shared, fail-open routing manifest.** Combine Magika with
   deterministic observations, retain independent worker checks, and route the
   union of plausible engines. Accepted.
4. **Run every specialist against every complete file.** High recall in theory,
   but unsupported formats, raw images, malformed inputs, and unbounded cost do
   not produce meaningful specialist coverage. Retained only as an explicit
   force/diagnostic option where an engine supports it.

## Decision

The integrated pipeline will create a shared content-routing manifest
immediately after the immutable input inventory and SHA-256 content manifest.
The router is advisory and multi-signal; it is not a new evidence extractor and
does not replace worker-level validation.

The stage relationship is:

```text
input inventory and content hashes
  -> early content observations and route manifest
  -> native/raw extraction of every supplied file
  -> routed specialist engines with independent applicability checks
  -> merge all original and derived records
  -> record-level language assessment and translation eligibility
  -> pattern matching and reports
```

### Mandatory routing behavior

- Native/raw extraction always receives every supplied file. No classifier
  label, confidence, extension, file size, error, or resource estimate may
  suppress it.
- The router records independent observations: Magika's raw model prediction,
  thresholded output, score and metadata; deterministic signatures and safe
  parser probes; extension; conflicts; and errors.
- Routes are **monotonic**. Adding a positive or conflicting observation may add
  routes but cannot remove a route already supported by another observation.
- Unknown, generic, low-confidence, conflicting, and classifier-error results
  fail open to deterministic worker probes. They never mean "nothing applies."
- FLOSS is eligible when Magika's raw or thresholded result identifies a PE,
  an independent structural probe validates a PE, or the examiner explicitly
  forces a supported format. Extension alone is not sufficient.
- OCR is eligible from the union of supported magic, safe parser probe,
  extension, and Magika observations. OCR still verifies source identity and
  applicability itself.
- Translation remains a record-level decision over the complete merged stream.
  File type must not suppress translation of native, FLOSS, OCR, decoded, or
  future parser records.
- File size controls bounded resources, not eligibility. There is no 1 GB or
  other size threshold for deciding whether evidence deserves an engine.
- Classification of an outer memory image, disk image, archive, or document
  container does not establish coverage of embedded artifacts. Native scanning
  remains mandatory. Any future carving or expansion creates derived children
  with parent hash, entry/offset, depth, and resource-limit provenance, then
  classifies each child independently.
- Explicit user `off` and `force` choices remain authoritative and are recorded
  as route reasons.

### Routing and execution provenance

The canonical, atomic routing record has exactly one row per inventory entry and
binds at least:

- source path, length, and SHA-256;
- routing schema and policy version;
- exact classifier executable, model/runtime identity, prediction mode, and
  relevant hashes;
- raw and thresholded classifier results;
- deterministic observations and disagreements;
- selected routes with machine-readable reason codes; and
- a deterministic routing-decision identifier.

Every engine also produces a terminal status such as `selected`,
`not-applicable`, `disabled-by-user`, `attempted`, `succeeded`, `failed`, or
`budget-exhausted`. Derived records resolve to the route decision and original
input identity. A failure may leave the run explicitly incomplete or degraded,
but cannot silently claim complete specialist coverage.

Heavy OCR, FLOSS, and translation component verification/loading should occur
only when the trusted route or record manifests show applicable work. Bundle
integrity remains a separate trust requirement; lazy runtime startup is not
permission to use an unverified component.

## Non-negotiable invariants

1. Routing cannot change the native record set for the same inputs and native
   options.
2. A Magika negative result alone cannot exclude a specialist route supported
   by another observation.
3. Workers retain their own applicability, input-identity, output-schema, and
   provenance validation.
4. Every input has one routing record and every enabled engine has a terminal,
   auditable status.
5. Routing, worker outputs, and publication fail atomically on identity,
   count/order, schema, mutation, or cancellation errors.
6. Cache reuse is invalidated by any change to source hash, routing policy,
   classifier/model/runtime identity, or prediction mode.
7. Performance acceptance cannot waive evidence coverage or provenance.

## Acceptance gates

Implementation is not conformant until automated tests establish:

- unchanged native output when the classifier is wrong, unknown, generic,
  malformed, timed out, or unavailable;
- union routing for renamed PE/PDF/image files, extension spoofing, malformed
  inputs, and PE/ZIP/PDF-style polyglots;
- no false claim that embedded artifacts in raw or container inputs received
  file-level FLOSS or OCR coverage;
- monotonic routes under added and conflicting observations;
- atomic failure for same-size changes, replace/rename, hard-link or reparse
  mutation, mutation during a stage, mutate-and-restore, manifest tampering,
  duplicate JSON fields, invalid UTF-8, and cancellation;
- stable route sets on accepted CPU/DirectML runtimes and byte-identical
  canonical manifests where the platform contract requires them;
- complete parent/hash/engine provenance and terminal statuses;
- one shared classifier/model initialization rather than one process startup per
  input; and
- a measured end-to-end benefit on representative small files, documents,
  executables, containers, and large raw inputs without a routing-caused loss
  against the exhaustive baseline.

## Strongest detractor and resolution

Central routing can create correlated false negatives: one sampled, single-label
prediction could suppress FLOSS, OCR, document parsing, and future engines at
once. The decision survives only because the router is advisory, route selection
is a fail-open union, native extraction is unconditional, and every specialist
retains an independent check. Removing any of those controls requires a new
Robin round and a superseding ADR.

## Falsifiers and revisit triggers

Reopen or reject this decision if:

- any record obtainable without routing disappears solely because of a route;
- uncertainty or a classifier failure produces fewer plausible routes;
- published output can be derived from bytes other than its recorded source
  hash;
- an outer type is used to claim coverage of nested content;
- accepted runtime changes produce unexplained route-set drift; or
- the implementation produces no material end-to-end benefit after preserving
  exhaustive coverage.

Re-evaluate the route map whenever Magika, FLOSS, OCR, a parser, the content-type
taxonomy, or the input/provenance schema changes.

## Consequences

The design classifies each input once in bounded shared batches, makes routing visible, and can defer
expensive component startup while increasing protection against classifier
misses. It also adds a trusted manifest, reason-code schema, adversarial corpus,
and validation burden. Some uncertain inputs deliberately receive more than one
specialist probe; high recall takes precedence over maximizing utilization.

## Implementation and measured verification

The implementation adds one bounded multi-path Magika invocation per command
line batch, canonical `content-routing.jsonl`, exact C# identity/order/schema
validation, routed FLOSS/OCR projections, source and manifest leases, terminal
engine-status rows, routing lineage on specialist records, and lazy specialist
model/runtime preflight. The .NET suite contains 606 passing tests and the
Python enrichment suite contains 359 passing tests with three
Windows privilege-only skips. Separate decision-policy tests pass under
PowerShell 7 and Windows PowerShell 5.1.

On the release workstation, a sanitized 50-file warm-cache text corpus took
39.701 seconds using the previous one-Magika-process-per-file loop and 0.932
seconds in one bounded batch: 42.62x lower wall time and 98% fewer process
starts. This is a routing microbenchmark, not an end-to-end evidence benchmark;
the release acceptance still has to prove the full bundle, route identity,
native invariance, and applicable FLOSS/OCR output.

This ADR records the accepted design and merge gates. User documentation,
installed help, and the independently verified public release remain
authoritative for shipped behavior.

## Primary references

- [Magika upstream repository and behavior](https://github.com/google/magika)
- [FLOSS upstream repository](https://github.com/mandiant/flare-floss)
- [bstrings document-reading research](../document-reading-research-2026-08.md)
- [bstrings output and provenance contract](../output-and-provenance.md)
