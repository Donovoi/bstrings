# ADR-0004: Reuse successful language detections within bounded batches

- **Status:** Accepted
- **Date:** 2026-08-09
- **Scope:** Offline language triage, managed/native language-detector boundary,
  and C# deployment-performance experiments
- **Decision type:** runtime, batching, performance, and forensic determinism
- **Review method:** Robin round under the
  [high-level decision policy](decision-review-policy.md)
- **Perspectives:** independent primary-source and vendor research,
  repository/runtime audit, performance advocate, forensic detractor, rotated
  critiques, and synthesizer measurements
- **Owner:** bstrings maintainers
- **Implementation state:** bounded implementation and benchmark are approved;
  promotion requires every acceptance gate below

## Context

A representative Full examination spent 2,230 seconds in offline language
triage and 295 seconds in native extraction. The triage stage currently reads
up to 2,048 records or 8 MiB per batch, calls the deterministic bundled
Lingua detector once for every eligible record, serializes one assessment per
record, and writes results in input order.

Aggregate-only sampling was performed without retaining, printing, or adding
case text to the repository. Five windows of 200,000 records found that
42.7% through 59.7% of language-eligible texts were exact duplicates within
the existing batch boundary. This directly supports testing detector-result
reuse; it does not prove the final time saving by itself.

A separate Native AOT probe was also measured over seven rotated direct CPU
scan pairs. Median CoreCLR versus Native AOT times were 261 versus 131 ms for
256 MiB, 431 versus 310 ms for 1 GiB, and 2.779 versus 2.618 seconds for
10 GiB. Every run returned the expected record count. The full AOT publish,
however, emitted 91 AOT or trimming warnings involving dynamic JSON paths,
ILGPU, and related runtime behavior. The largest scan therefore improved by
only 5.8% while complete Full-profile compatibility remained unproved.

The current application is already lower-level in its measured extraction
paths: it uses AVX2/SSE intrinsics, memory mapping, pooled buffers, bounded
channels, generated regex where suitable, and a blittable ABI-checked Rust
interface. Reducing the number of expensive detector calls has stronger local
evidence than adding more P/Invoke, raw Win32 I/O, or unsafe code.

The first real-detector parity run exposed a pre-existing reproducibility
problem: repeated parallel Lingua evaluations produced the same language,
decision, candidates, and statistics but occasionally differed in the final
floating-point bit of a confidence score. On a retained synthetic 2,048-record
probe, rounding the five reported confidence and margin fields to 15 decimal
places still left 490 differing rows; 14, 13, and 12 places removed the observed
differences. These engine scores are not calibrated probabilities, and the
unstable tail does not carry a defensible forensic distinction.

The decision tests were fixed before implementation results are inspected:

- byte-identical ordered assessments and translation candidates;
- unchanged per-record provenance, decisions, errors, counters, cancellation,
  and atomic publication;
- bounded retention that ends with the existing batch;
- no caching of exceptions, cancellation, or unsuccessful detections;
- a material measured triage-stage gain on duplicate-bearing input; and
- no material regression on all-unique input.

## Considered options

1. **Keep one detector call per eligible record.** This is the compatibility
   baseline. It is retained as an internal benchmark path, not the production
   default when reuse clears the gates.
2. **Use an unbounded or cross-batch cache.** Rejected. It could retain
   credential-bearing evidence for the life of the process, grow with input,
   and make future context-sensitive detectors unsafe.
3. **Reuse successful results only within the existing batch.** Accepted for
   implementation and measurement. Exact text is grouped with ordinal
   equality; mode, target language, detector version, and native ABI are fixed
   for the batch. Records retain independent assessment and provenance.
4. **Cache failures as well as successes.** Rejected. A transient or injected
   failure must not poison later records or suppress the baseline retry and
   diagnostic behavior.
5. **Publish the complete Full application as Native AOT.** Rejected now. The
   probe establishes a startup and short-scan opportunity but not compatible
   ILGPU, custom regex, JSON, raw-disk, reporting, or diagnostic behavior.
6. **Add more raw P/Invoke, IOCP, unbuffered I/O, or
   `SuppressGCTransition`.** Rejected without a separate profile-led decision.
   Managed `RandomAccess` already reaches overlapped Windows I/O, while the
   current native signatures are blittable. Detector and scan calls are not
   sub-microsecond nonblocking functions and cannot safely suppress GC
   transitions.
7. **Convert `DllImport` to source-generated `LibraryImport`, use
   ReadyToRun, change GC mode, or test managed `RandomAccess`.** Retained as
   separate experiments. They must be measured independently so a small
   deployment or interop effect is not credited with detector-call savings.

## Decision

The production Lingua path will reuse only successful immutable
`LanguageDetectionResult` values for identical eligible text within one
existing triage batch. Equality is `StringComparer.Ordinal`; no normalization,
hash-only identity, fuzzy matching, or cross-batch state is permitted.

The implementation groups record indices without copying text. Each group is
processed in original record order. Its first successful detector result may
be reused by later records in that group. Unsuccessful calls are not retained,
so later records retry exactly as the baseline does. Assessments, errors,
candidate rows, provenance, counters, and final output order remain per-record.
Different text groups may still execute in parallel.

All classifications, candidate decisions, and policy comparisons use the raw
detector values. Only the five numeric score fields serialized into assessment
JSON are canonicalized with `Math.Round(value, 12,
MidpointRounding.ToEven)`; negative zero is normalized to positive zero and
`null` remains `null`. Each assessment declares `scoreDecimalPlaces: 12` and
emits `confidenceGatePassed` and `marginGatePassed` from the raw comparisons.
The authoritative decision therefore cannot be inferred incorrectly from a
displayed value whose insignificant tail was removed.

The default production detector is declared deterministic for fixed text,
effective mode, target language, detector version, and native ABI. Injected
test detectors keep baseline one-call-per-record behavior unless a test or
benchmark explicitly opts into reuse. The internal baseline switch is not a
user-facing profile.

Native AOT will not become the Full release mode. Source-generated P/Invoke is
permitted only as an independently measured interop cleanup; it is not part of
this optimization and cannot use `SuppressGCTransition` on scanning,
detection, or I/O calls.

## Non-negotiable invariants

1. After score canonicalization, every assessment and translation-candidate
   line remains byte-identical and in the same order as the baseline for the
   same pinned detector and input. Historical assessment bytes may change only
   in the insignificant score tail and the new explicit score-policy fields.
2. Exact source record ID, source file, location, target, detector identity,
   confidence values, decision, error, and translation-candidate state remain
   independently emitted for every record.
3. Reuse is scoped to at most the existing 2,048 records and 8 MiB. All group
   tables and retained text references are released before reading the next
   batch.
4. The identity relation is ordinal string equality. A digest without exact
   equality, normalization, case folding, truncation, or approximate matching
   cannot establish reuse.
5. Only successful complete detector results are reusable. Exceptions,
   cancellation, unavailable libraries, invalid ABI/results, unidentifiable
   text, timeouts, and other unsuccessful outcomes are retried and reported
   under the baseline policy.
6. Cancellation remains observable between records and batches. No partial
   candidate or assessment output becomes final; existing atomic completion
   and cleanup remain authoritative.
7. No case text, hash derived from case text, or aggregate tied to a private
   case is written to source control, logs, telemetry, or benchmark fixtures.
8. AOT, ReadyToRun, GC, I/O, regex, and interop variants are attributed and
   benchmarked separately. Combining unmeasured variants cannot justify a
   default change.
9. Raw detector values, never rounded report values, drive language identity,
   confidence and margin gates, decisions, candidate membership, and counters.
   Score canonicalization is serialization-only and applies consistently to
   confidence, target confidence, second confidence, top-language margin, and
   target margin.

## Acceptance gates

Implementation requires automated tests for 0%, representative, and highly
duplicated batches; successful, unsuccessful, throwing, and cancelled
detectors; mixed derived/non-linguistic records; batch and byte limits; output
ordering; atomic rollback; and explicit baseline-versus-reuse output parity.
Tests must also cover score jitter, negative zero, exact midpoint rounding,
threshold values immediately above and below each gate, and values on both
sides of half the quantization unit. Gate booleans and decisions must agree with
raw comparisons even when above- and below-threshold values serialize to the
same displayed score.

A reproducible benchmark must run at least seven rotated baseline/candidate
pairs using the real bundled detector on synthetic, non-case JSONL with:

- 100,000 and 1,000,000 records where practical;
- 0%, 50%, and 95% exact eligible-text duplication;
- accurate and fast detector modes;
- short, representative, Unicode, escaped, and near-2,048-character text;
- successful and deliberately unidentifiable records; and
- the production 2,048-record and 8 MiB limits.

For promotion, every pair must produce byte-identical candidate and assessment
files and equal logical statistics. The representative duplicate workload must
improve median triage wall time by at least 25%. An all-unique workload must
not regress by more than 5% after excluding startup and measurement noise.
Peak working set must not increase by more than 5%, retained state must not
grow across batches, and no measured workload may regress by more than 5%.
At least 20 repeated real-Lingua runs over a fixed synthetic parallel workload
must also produce byte-identical assessment JSON after canonicalization.

The complete .NET, Rust, Python, PowerShell 5.1/7, decision, installer, bundle,
and published-release acceptance gates remain mandatory before distribution.
The previous representative Full input may be rerun locally as a post-release
operational check, but its contents or derived values cannot enter the
repository or public release evidence.

Native AOT additionally requires zero unsuppressed AOT/trimming warnings and
complete GPU, custom-regex, raw-disk, Rust, JSON/reporting, air-gap, installer,
and diagnostic acceptance. A separate feature-reduced executable does not
satisfy the single Full-profile requirement.

## Strongest detractor and resolution

The strongest objection is that duplicate counts do not measure avoided CPU
time. Duplicates may be cheap, while dictionary/group construction, retained
strings, reduced parallelism inside a large group, and garbage collection may
consume the apparent saving. Reusing a failure could also alter retries and
diagnostics, and an injected or future stateful detector might legitimately
produce different results for identical text.

The decision survives only as a bounded measured implementation. It reuses
successful results, retains exact-text identity, disables implicit reuse for
injected detectors, releases state per batch, measures all-unique overhead,
and requires byte-identical outputs plus at least 25% representative-stage
improvement. A future context-sensitive or adaptive detector disables reuse
until all inference state is explicitly scoped and reviewed.

During critique rotation, one evidence review discussed caching regex match
ranges rather than language classifications. Those recommendations are not
treated as evidence for this decision because the semantic unit and failure
behavior differ. Its general warnings about bounded retention, cancellations,
and unsuccessful-result caching were independently retained where applicable.

Reviewers agreed that tolerance-only comparison was insufficient and that raw
values must remain authoritative for decisions. One reviewer preferred 14
decimal places to retain the greatest resolution; two preferred 12 as a wider
stability margin. Because 15 places already failed locally, cross-host
reduction order may vary, and the operational gates are far coarser, the
decision adopts 12 places. A detector or platform revision must revalidate that
policy rather than silently widening a test tolerance.

## Falsifiers and revisit triggers

Reject or roll back successful-result reuse if any of the following occurs:

- identical text under fixed detector inputs produces different successful
  results;
- any ordered assessment, candidate row, provenance value, statistic, error,
  or atomic completion outcome differs from baseline;
- score canonicalization changes a decision or candidate, gate metadata
  disagrees with the raw comparison, or repeated canonical JSON is unstable;
- a detector revision produces meaningful score differences below the chosen
  12-decimal reporting resolution;
- the representative benchmark improves by less than 25%, all-unique input
  regresses by more than 5%, or peak working set grows by more than 5%;
- retained group state grows across batches or survives cancellation cleanup;
- a detector becomes contextual, adaptive, randomized, remotely mutable, or
  stateful in a way not represented by the batch scope;
- tracing shows detector calls are no longer a material part of triage; or
- release or clean-machine acceptance differs from the CoreCLR baseline.

Revisit Native AOT when current dynamic JSON paths are source-generated,
ILGPU and custom regex behavior is proved compatible, all AOT warnings are
resolved without suppression, and representative Full runs show a material
end-to-end gain. Revisit managed `RandomAccess`, ReadyToRun, GC mode, and
`LibraryImport` only with independent profiles and rollback thresholds.

## Consequences

Duplicate-bearing evidence can avoid a large share of expensive language
detections without changing user commands, the single Full profile,
provenance, or specialist engines. Language-assessment rows gain explicit score
precision and raw-gate outcome fields; their decision remains authoritative.
The optimization remains entirely within the managed orchestration boundary
and does not widen FFI ownership.

The cost is a bounded dictionary and index arrays per triage batch, additional
tests, and an internal baseline mode for attributable benchmarks. All-unique
input receives grouping overhead, so the regression gate is a release
requirement rather than an assumption.

The product keeps CoreCLR and its current GPU, regex, JSON, raw-disk,
diagnostic, and installer behavior. Lower-level APIs remain available as
profile-led experiments, but P/Invoke or AOT is not presented as a general
throughput solution when the measured dominant stage is repeated inference.

This is a runtime code change and therefore requires a product version bump,
updated user and maintenance documentation, the normal Windows release, and
independent public installer/bundle verification.

## Primary references

- [Microsoft Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/), including startup, trimming, dynamic-code,
  loading, and diagnostic constraints.
- [Microsoft source-generated P/Invoke](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation), describing compile-time interop stubs and AOT compatibility.
- [Microsoft native interop best practices](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/best-practices), including blittable signatures and generated marshalling guidance.
- [Microsoft `SuppressGCTransitionAttribute` contract](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.suppressgctransitionattribute?view=net-10.0), restricting use to trivial sub-microsecond nonblocking calls.
- [Microsoft `RandomAccess.ReadAsync`](https://learn.microsoft.com/en-us/dotnet/api/system.io.randomaccess.readasync?view=net-10.0), the supported explicit-offset managed I/O surface.
- [Current language-triage implementation](https://github.com/Donovoi/bstrings/blob/v1.9.15/bstrings/LanguageTriageCore.cs)
- [Managed-host and measured-Rust decision](https://github.com/Donovoi/bstrings/blob/v1.9.15/docs/architecture/adr-0002-retain-managed-host-and-measure-rust-kernels.md)
- [Forensic output and provenance contract](../output-and-provenance.md)
- [Release and version policy](https://github.com/Donovoi/bstrings/blob/v1.9.15/VERSIONING.md)
