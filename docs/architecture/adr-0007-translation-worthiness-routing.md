# ADR-0007: Route translation-worthy records before language detection

- **Status:** Accepted target architecture; the first implementation is
  shadow-only and every authoritative suppression remains benchmark-gated
- **Date:** 2026-08-11
- **Scope:** Full-profile record-level language triage, translation candidacy,
  provenance, reporting, privacy-safe evaluation, and performance accounting
- **Decision type:** forensic coverage, stage ordering, runtime, performance,
  provenance, and failure-policy decision
- **Review method:** three completed independent and rotated Robin rounds under the
  [high-level decision policy](decision-review-policy.md)
- **Perspectives:** Primary-source Researcher, Repository/Runtime Auditor,
  Forensic Detractor, Performance and Measurement Reviewer, and Privacy and
  Provenance Reviewer
- **Owner:** bstrings maintainers
- **Implementation state:** bounded structured shadow observation, bounded
  record-origin provenance, routing/Lingua counters, and the validated
  translation-work ledger are implemented. The third Robin round authorizes a
  managed, dependency-free, sparse-linear float-reference experiment and
  benchmark-only challengers. It does not authorize a learned runtime path:
  `promotionEligible` remains `false`, while fuzzy suppression and every
  authoritative bypass remain separately gated below

## Context

Full analysis currently asks whether a record meets simple length and Unicode
letter-count requirements and is not an obvious machine identifier. Eligible
records then reach Lingua, which chooses among human languages; it has no
`code`, `binary`, `random`, or `not-language` class. Under Full's high-recall
policy, non-target results, ambiguous results, and detector failures can become
translation candidates. The relevant boundaries are visible in
[`TranslationTextEligibility`](https://github.com/Donovoi/bstrings/blob/master/bstrings/TranslationTextEligibility.cs),
[`LanguageTriageCore`](https://github.com/Donovoi/bstrings/blob/master/bstrings/LanguageTriageCore.cs),
and the bundled
[`lingua-rs` adapter](https://github.com/Donovoi/bstrings/blob/master/native/bstrings_core/src/lib.rs).

This ordering confuses two different questions:

1. Is this record human-language material that could benefit from translation?
2. If so, which language is it?

A language identifier cannot reliably answer the first question outside its
label set. `Language ID in the Wild` found that held-out language-identification
quality did not transfer to uncontrolled web data because of domain mismatch,
class imbalance, and related error modes. NLoN demonstrates that a small
feature and character-trigram model can distinguish natural-language from
software-engineering text, but its cross-source results also degrade and its
authors recommend source-specific labeling. Those results support a separate
translation-worthiness stage; they do not establish that an off-the-shelf
classifier is safe to suppress forensic records.

Short and mixed records are especially hazardous. Published short-text work
describes language identification over 5--21 characters as challenging, and
Lingua itself documents that confidence separation depends on input length.
Code comments, configuration values, log messages, OCR fragments, decoded
payloads, and FLOSS output can contain short foreign-language evidence inside
otherwise machine-like syntax.

Magika does not close this gap. It classifies file/byte content types and is
already an advisory input to early file routing under
[ADR-0001](adr-0001-early-fail-open-content-routing.md). One classification of
a memory image, container, executable, or document cannot determine which
individual extracted records are natural language. Magika and other source
provenance can make a record more likely to be retained, but initially cannot
justify translation suppression.

Private diagnostic runs indicate that misrouted and duplicate work can make
translation operationally impractical. Those runs are useful for diagnosis,
not public evidence: case strings, extracted values, filenames, hashes, exact
case-derived totals, and uniquely identifying aggregates must not enter this
repository, fixtures, benchmark logs, release notes, or public CI artifacts.

The tests and measurements below were fixed before implementation:

- every canonical parent record remains present and byte-identifiable by the
  existing provenance contract;
- all first-implementation decisions, including exact whole-record structured
  tokens, are observable but cannot suppress Lingua or translation work;
- only an exact, whole-record, semantically validated structured token is
  eligible for later authoritative activation under this ADR;
- fuzzy code/noise classification cannot be activated without a new decision;
- `retain` and `unknown` fail open to the existing Full high-recall policy;
- Magika, extractor kind, and other provenance may only promote retention;
- model-work measurements distinguish exact-unique inference from occurrence
  records and report/I/O work; and
- any claimed saving must exceed the router's own CPU, memory, and I/O cost.

## Considered options

1. **Keep the current eligibility check and tune Lingua confidence.** Rejected.
   A higher language-confidence threshold still forces machine material into a
   human-language label space and risks rejecting short genuine language.
2. **Treat file-level Magika labels as translation authority.** Rejected.
   Outer content type does not describe heterogeneous records or embedded
   material, and this would contradict the fail-open routing contract.
3. **Skip anything with high entropy, punctuation, digits, long tokens, no
   whitespace, or code-like syntax.** Rejected as an authoritative rule.
   Credentials, transliterated text, compact scripts, URLs with human text,
   code comments, and damaged OCR can share those features.
4. **Use a pretrained natural-language/code classifier as an immediate gate.**
   Rejected for initial authority. NLoN supplies a useful model shape, but its
   cross-source degradation is direct contrary evidence for forensic transfer.
5. **Add an auditable pre-Lingua router with prospective exact structured
   bypass and a fail-open shadow classifier.** Accepted. It builds
   representative cross-runtime correctness and performance evidence without
   claiming or taking immediate savings.
6. **Aggregate adjacent records or surrounding bytes before routing.**
   Deferred. Context can improve classification, but reconstructing fragments
   changes semantic units, cardinality, resource bounds, and provenance and
   therefore requires a separate decision and acceptance corpus.
7. **Translate machine-like records on the GPU because capacity is available.**
   Rejected. Faster inference does not make irrelevant inference correct, and
   consuming GPU capacity on misrouted records delays genuine language work.

## Decision

Add a deterministic, versioned translation-worthiness router after the merged
canonical string stream is validated and before Lingua. It does not delete,
rewrite, normalize, concatenate, or replace source records. Every canonical
parent continues through pattern matching and reporting whether or not it is a
language or translation candidate.

The router proposes one of:

- `prospective-bypass`: the entire trimmed record is accepted by an approved
  semantic validator and no uncovered text remains;
- `retain`: the record reaches the existing language-triage policy; or
- `unknown`: routing was inconclusive or failed, so the record reaches the
  existing language-triage policy.

The bounded first implementation operates only in `shadow` policy mode.
`prospective-bypass`, `retain`, and `unknown` all execute the unchanged Lingua,
candidate-emission, exact-cache, and translation paths. No proposed decision
can change candidacy or child output. A later `structured-authoritative` policy
version may make only approved `prospective-bypass` results suppress work after
all activation gates pass. Changing that mode is an explicit versioned policy
switch, not an automatic threshold or model update.

`retain` and `unknown` remain equivalent for Full candidacy after activation.
This ADR does not weaken or replace Full's high-recall confidence, ambiguity,
or detector-failure behavior.

### Prospective structured bypass

Structured bypass is a positive whitelist, not a score or a collection of
negative heuristics. A validator must:

1. consume the whole record after removal of outer Unicode whitespace only;
2. parse a specific structure rather than merely match a permissive shape;
3. enforce that structure's length, alphabet, separators, canonical encoding,
   and semantic or integrity constraints where the format defines them;
4. return a stable validator ID, version, reason code, and covered span; and
5. leave zero non-whitespace characters outside the accepted span.

Initial prospective classes may include standards-conformant UUID/GUID values,
fixed-width cryptographic digests whose algorithm/width is known, and other
opaque identifiers already proved by a strict repository validator. The
current C# eligibility check and Python protected-identifier logic already
exclude or model-bypass some of these records; therefore their shadow labels
do not imply new Lingua or translation-model savings. URLs, paths, registry
paths, assignment expressions, generic hex, and values that merely resemble
identifiers do not bypass by shape alone because they can contain meaningful
natural-language components.

JWT, Base64, Base32, hexadecimal, and other reversible encodings may bypass as
containers only when validation is canonical and their decoded content is
atomically emitted as a derived child that re-enters this same router. If the
decoder is unavailable, fails, exceeds a bound, cannot establish a unique
canonical decoding, or does not emit complete parent/transform provenance, the
source record is `retain` or `unknown`. Pattern detection and credential/token
reporting remain independent of translation routing, so a bypassed JWT, GUID,
or digest remains discoverable as evidence.

No checksum, entropy threshold, language confidence, character ratio, regular
expression alone, extension, Magika label, or extractor kind is sufficient for
authoritative bypass. Adding a validator class or weakening whole-record
coverage is a high-level change and must update this ADR or supersede it.

### Shadow natural-language routing

A future bounded local classifier may use Unicode character/script/category
features and byte/character n-grams, but the second and third Robin rounds
freeze the following annotation taxonomy before any model is implemented:

- `HUMAN_WORTHY`: the record contains human-language material that could
  benefit from translation;
- `MIXED`: human-worthy material is embedded in code, configuration, a log,
  identifier-like syntax, or other machine material;
- `MACHINE`: the complete record contains no human-worthy material; and
- `AMBIGUOUS`: the available context cannot support a reliable label.

`MIXED` is an evaluation facet, not a suppressible runtime class. The model
target is binary: `HUMAN_WORTHY` and `MIXED` map to `contains-any-human`, while
only `MACHINE` maps to `machine-only`. `AMBIGUOUS`, low margin, out-of-
distribution input, unsupported features, and all model/schema/runtime errors
abstain. An abstention always retains the record.

The learned scorer has no authority under this ADR. Its first possible use is
offline or runtime shadow observation only:

- a proposed `machine-only` result cannot remove a Lingua call or translation
  candidate;
- disagreement, unsupported script, absent model, model error, invalid score,
  or schema drift records `unknown` and retains the record;
- source provenance may change `unknown` or proposed `machine-only` to `retain`,
  never the reverse. Every origin/script/length cell not independently
  validated under the gates below converts a fuzzy `machine-only` proposal to
  `retain`; and
- no training, feedback, or threshold update occurs during an examination.

The first learned experiment is an offline managed C# reference, not a runtime
router. It uses dependency-free sparse-linear inference with a floating-point
reference score and writes only the allowlisted research prediction artifact
consumed by the corpus evaluator. No learned score, threshold, model byte, or
dependency enters the product executable, air-gap bundle, quality installer,
assessment stream, or run/summary schema during this experiment. Every report
continues to declare `promotionEligible: false`.

The reference experiment runs four preregistered ablations over the identical
grouped splits: approved exact validators only, engineered Unicode/category
features only, character n-grams only, and the combined engineered-plus-n-gram
scorer. The unchanged pipeline is the operational baseline. These ablations
separate deterministic-whitelist value, feature-family value, and learned-model
value; an aggregate model score or a benchmark counter alone cannot attribute a
saving.

Training, calibration, and test are isolated phases. Deduplication, provenance
grouping, licence validation, and the split manifest are frozen before any
feature fitting. Training may fit features and weights only on `train` groups.
Calibration may choose the abstention thresholds, the finite set of candidate
origin/script/length cells, and its count `K`, but may not change weights or
splits. Those choices and all acceptance tests are frozen before the locked
`test` predictions are opened. A test failure creates a new experiment/model
version with a new untouched test set; it cannot be repaired by tuning against
the failed test. Evaluation-only external corpora remain outside all three
selection phases.

Exact whole-record validators remain a separate positive whitelist. Their
outcomes do not train, calibrate, override, or share a threshold with the fuzzy
scorer. Conversely, a fuzzy score cannot weaken an exact validator or turn a
shape-only match into an exact proposal.

No private examination may supply training, calibration, threshold selection,
cell selection, or model-selection data. After public thresholds and gates are
frozen, a private examination may supply only a non-published veto: a failure
blocks promotion, while a pass cannot approve, tune, rank, or rescue a model.
Private text, features, embeddings, labels, per-cell statistics, exact counts,
and derivatives must not enter a model, corpus, repository, or public artifact.

GlotLID v3 may be evaluated offline only as a pinned, licence-recorded
disagreement comparator for sampling records that need independent annotation.
It is not a teacher, ground truth, runtime dependency, or routing vote.
OpenLID-v3 is excluded from runtime and teacher use: its web-language-ID task is
not a forensic translation-worthiness validation, and its GPL-3.0 distribution
boundary would add an unnecessary product risk. Neither comparator may label
training data automatically.

Public corpus construction must preserve source and licence boundaries:

- CommonLID and GlotOCR are evaluation-only and cannot supply training or
  calibration rows;
- MASSIVE's CC-BY-4.0 utterances may supply clean multilingual positives only,
  with attribution and change records. They cannot supply machine negatives or
  forensic-transfer proof, and all localizations of an English seed and their
  derived fragments stay in one source group;
- the NLoN paper remains architectural evidence, but its GPL-3 package, bundled
  corpus, features, serialized assets, and code are excluded from training,
  calibration, runtime, and distributed artifacts;
- CodeSearchNet may supply manually labelled candidates only after the original
  source repository's licence and revision are recorded for each row. The
  benchmark repository's MIT licence is not treated as a blanket data licence.
  Comments, docstrings, user-facing literals, localized messages, and
  credential labels are `HUMAN_WORTHY` or `MIXED`, not machine negatives; and
- every other code, configuration, and log source requires the same compatible
  per-source licence, revision, attribution, privacy review, and manual label; and
- deduplication and source/repository/document/family grouping occur before
  train/calibration/test splitting. Related originals, translations, forks,
  fragments, or synthetic variants cannot cross those splits. Synthetic rows
  cannot satisfy the independent-positive statistical gate.

Actual-call instrumentation precedes any learned-model implementation. Current
C# summaries provide `routingEvaluations`, `detectorEligibleRecords`,
`detectorExecutions`, and `detectorReuseHits`, with reconciliation against
language-triage cardinality. The C# triage input also classifies a bounded set
of origin flags for native-static, FLOSS, FLOSS-decoded, OCR, PDF-text,
derived-translation, and unknown records. The current structured shadow router
does not use those flags to change a code; any future fuzzy scorer may use them
only to force retention. These counters and flags are the foundation, not a
saving claim.

The Python worker now atomically writes `translation-work-stats.json` after
successful translated-output publication. Its privacy-safe schema 1 contains
only aggregate nonnegative integers and no record IDs, text, paths, or per-row
values:

- `candidateOccurrences` and `translatedChildOccurrences` count candidate and
  child occurrences and must be equal;
- `textDecisions` counts the first exact-text decision within each translation
  window/call. It is not a run-global distinct-text count. A text repeated in a
  later window creates another decision, and `runCacheHits` exposes the
  cross-window exact-result reuse;
- `protectedOnlyBypassTexts`, `runCacheHits`, `translationCacheHits`, and
  `translatorInputTexts` are mutually exclusive decision buckets whose sum
  must equal `textDecisions`;
- `translatorRequests` counts translator batch dispatches,
  `translatorInputTexts` counts exact texts submitted by those dispatches, and
  `modelResults` counts per-input outcomes; and
- `modelFallbacks` and `preservationFallbackChildOccurrences` distinguish
  rejected model outcomes from fallback child occurrences after reuse.

Managed C# accepts only the exact schema-1 property set from a bounded physical
non-reparse file, computes its SHA-256, checks every counter and the decision,
candidate/child, input/result, and fallback cardinalities, and fails the run on
any mismatch. Completed `run.json` and `summary.json` project the validated
artifact as `translationWork`, including `file`, `sha256`, and every counter.
The aggregate schema is safe to retain with the examination, but private case
counts and hashes remain prohibited from public fixtures, logs, and release
evidence. These counters describe worker-level dispatch; they do not claim to
enumerate undocumented internal server retries.

The first scorer is the dependency-free managed float reference above. A pinned
custom fastText build is the next benchmark-only challenger because it supplies
a separately implemented sparse character-n-gram classifier; its predictions
cannot become labels, votes, or runtime decisions. ONNX, fixed-point/integer
inference, Rust/P/Invoke, and NativeAOT are deferred until a scorer has passed
the locked public safety gates and become a safe finalist. Only then may the
same frozen feature and score contract be compared across managed JIT,
NativeAOT-compatible managed code, and a bounded batched Rust interface. The
comparison must include model plus dependency/binary bytes, startup, throughput,
allocation, peak working set, output equivalence, build time, bundle cost,
licences, notices, and air-gap maintenance. Runtime language cannot cure corpus
licence or transfer risk, and this ADR does not assume Rust, AOT, integer
arithmetic, or a particular model-size ceiling is superior.

The router does not create a second per-record file. Each existing
`language-assessment` row gains exactly one compact `translationRouting` object
containing exactly one field: `code`. The code is a bounded deterministic enum,
not an arbitrary string, and is at most 32 printable ASCII characters. It
encodes the proposed routing outcome and reason through the run's codebook. The
assessment row already binds the source record ID and carries detector outcome
and `translationCandidate`; the nested object does not repeat those values or
text, origin, attributes, source path, location, validator/model identity,
scores, policy version, mode, or attempted flags.

The existing assessment temporary-file, atomic publication, cardinality,
identity, and order contract covers `translationRouting`. When routing metadata
is enabled, `run.json` and `summary.json` define the router/policy version,
shadow/authoritative mode, complete codebook, and aggregate
retained/prospective/unknown counts once per run. Existing assessment and
translated-child fields remain authoritative for detector outcome, candidate
selection, and emitted child provenance. Because bounded reuse can attach one
detector result to several rows after one invocation, exact-unique detector
evaluations and translation-model requests are authoritative only in the
separate runtime/benchmark counters required below. A missing object, unknown
or over-length code, extra nested field, aggregate mismatch, or nondeterministic
assessment row makes the run incomplete. There is no new
`translation-routing.jsonl` artifact.

### Deferred context

The router operates on exactly one existing canonical record at a time.
Adjacent-offset aggregation, page/paragraph reconstruction, token segmentation,
translation of only a substring, and inferred conversation context are
deferred. A later context system must emit explicit derived children with
bounded source spans rather than mutate or replace canonical parents.

## Non-negotiable invariants

1. Canonical parent count, order, record ID, text, source file, location,
   origin, attributes, and parent/transform lineage are unchanged relative to
   the no-router baseline.
2. Shadow routing changes no language-detection, candidate, translation, or
   child work. After an authoritative policy is activated, routing changes only
   those language/translation paths. Native, FLOSS, OCR, decoding, canonical-
   parent pattern matching, credential/token detection, and evidence reporting
   remain independently complete.
3. Full remains high-recall. `retain`, `unknown`, router failure, unsupported
   script, and provenance conflict follow the existing Full path.
4. Only approved whole-record semantic-validator `prospective-*` codes from the
   first implementation may be considered for authority after an explicit
   policy-version activation. `shadow-*` codes remain observations. Learned,
   fuzzy, entropy-based, or provenance-based suppression is forbidden until a
   later accepted decision promotes it.
5. Reversible encoded containers bypass only with an atomically published,
   bounded decoded child that re-enters routing and retains exact decoder and
   parent provenance.
6. Magika, extension, source type, extractor, OCR confidence, and file-level
   routing may promote retention but cannot suppress translation by themselves.
7. The embedded `translationRouting.code` is deterministic for fixed text,
   provenance, validator/model bytes, policy, and runtime. Its meaning is fixed
   by the run-level versioned codebook. Model and validator identities are
   bundle-locked; no other per-row routing fields or second routing stream
   exist.
8. Cancellation, parser/validator/model exceptions, invalid output, or resource
   exhaustion cannot silently drop work. Existing atomic publication and
   `.incomplete` behavior remain authoritative.
9. Runtime reports may describe the examination, but public source, fixtures,
   CI logs, and benchmarks contain no private case text, paths, hashes, exact
   case aggregates, or values derived closely enough to identify a case.
10. Performance claims report occurrence and unique work separately. A lower
    occurrence candidate count is not presented as an equal number of avoided
    detector or translation-model invocations.
11. A learned proposal has no authority. `MIXED`, `AMBIGUOUS`, abstention, and
    every unvalidated origin/script/length cell retain the existing Full path.
    Exact validators remain a separately reviewed whitelist.

## Acceptance gates

### Correctness and forensic gates

Automated tests must compare the router with the no-router baseline and prove:

- byte-identical canonical parent rows and equal parent cardinality/order for
  empty, cancelled, malformed, mixed, and successful inputs;
- exactly one valid `translationRouting` object containing only one bounded
  enum `code` inside every enabled assessment row, unchanged assessment
  cardinality/identity/order, matching run/summary policy/codebook/aggregates,
  atomic rollback, and no abandoned `.partial`, cache, or backup artifacts;
- byte-identical shadow Lingua-attempt, candidate, cache, translation-child,
  pattern, and histogram outputs relative to the no-router baseline;
- zero authoritative bypasses for short foreign text, mixed-language records,
  code comments, message strings inside source code, localized registry/config
  values, OCR damage, FLOSS-decoded text, decoded payloads, credentials with
  human fields, URLs/paths with linguistic components, and unsupported scripts;
- strict positive and adversarial negative fixtures for every approved
  validator, including near-miss width, alphabet, padding, separator, version,
  canonical round-trip, embedded-prefix/suffix, and trailing-text cases;
- decoded-child publication and re-entry before Base-N or JWT container bypass,
  with exact parent, decoder, span, resource-limit, and failure provenance;
- unchanged canonical-parent pattern/token/credential findings and histograms
  for prospectively bypassed parents; and, after authoritative activation,
  every finding or histogram contribution removed only with an unchanged
  translation child must have an identical canonical-parent counterpart;
- fail-open `unknown` behavior for missing or changed model bytes, validator
  errors, invalid numeric scores, unsupported scripts, cancellation, and every
  injected failure point; and
- a frozen cross-runtime corpus proving that C# eligibility, embedded routing,
  Python protected-identifier checks, candidate filtering, exact caching, and
  actual model-call decisions agree on the expected boundary. Similar-looking
  C# and Python predicates are not assumed semantically equivalent.

Before a learned-model prototype is accepted, tests must also prove:

- every learned path is benchmark-only, emits only the allowlisted aggregate
  report and prediction input, declares `promotionEligible: false`, and is
  absent from the product, installer, air-gap bundle, and runtime schemas;
- one independently adjudicated taxonomy across `HUMAN_WORTHY`, `MIXED`,
  `MACHINE`, and `AMBIGUOUS`, with `MIXED` counted as a positive
  `contains-any-human` record for every suppression-safety metric;
- exact-only, engineered-feature-only, character-n-gram-only, and combined
  ablations run over identical group manifests and attribute work separately
  from existing C# eligibility, Python bypass, and exact-cache reuse;
- training, calibration, and locked test access obey the phase isolation above,
  including a new untouched test set after any test-informed revision;
- explicit abstention and forced retention for every unsupported or
  insufficiently validated origin/script/length cell;
- zero automatic teacher labels, zero private-case training/calibration/model
  selection, and complete public source, licence, deduplication, and grouping
  manifests;
- every MASSIVE row is a clean-positive CC-BY-4.0 use with its seed family and
  attribution retained, no NLoN GPL-3 asset enters any experiment, and every
  CodeSearchNet-derived row has a compatible original-source licence and a
  manual label;
- a separately pinned optional GlotLID disagreement experiment cannot affect
  labels or routing, and no OpenLID artifact enters training, the bundle, or
  runtime; and
- an authoritative, atomically published and managed-reconciled work ledger
  exists for routing evaluations, Lingua executions/reuse, translation
  decisions/cache paths, translator requests and input texts, model results,
  preservation fallbacks, and translated-child occurrences.

The frozen privacy-safe corpus must contain `HUMAN_WORTHY` positives, `MIXED`
positives, `MACHINE` negatives, and adjudicated `AMBIGUOUS` records across every
supported script, every origin kind used by native/OCR/PDF/FLOSS/decoder paths,
lengths 1--2,048, and deliberately mixed records. Evaluation reports confusion
matrices separately by script, origin, length band, and mixed-content facet; an
aggregate score cannot promote a subgroup. Fuzzy suppression remains shadow
unless it records zero false suppression of `contains-any-human` on all
mandatory forensic fixtures. Calibration must pre-register the finite promoted
cell set and `K` before test access. Test evaluation must use simultaneous
one-sided confidence bounds with at least 95% family-wise coverage across those
`K` cells, and each cell's upper bound for false suppression must be at most
0.5%. Bonferroni allocation of `0.05 / K` is an accepted conservative method;
another method requires primary evidence and an ADR update. A source group with
any false-suppressed positive row is one failed independent unit. Translations,
forks, files from one repository, document fragments, OCR/FLOSS/decoded
variants, and synthetic derivatives cannot inflate the independent-unit count.
If independence between remaining groups is not defensible, or a cell lacks
enough independent positives, that cell retains. The current research
evaluator's unadjusted per-cell group bound is diagnostic only and cannot make a
promotion eligible.

Runtime selection is deferred until the managed float reference or fastText
challenger passes every locked public safety gate as a benchmark-only finalist.
Only then is the frozen finalist compared across managed JIT, compatible
NativeAOT, and batched Rust at multiple measured model sizes. No current model
or runtime is promotion eligible.

### Reproducible performance gates

Run at least seven rotated baseline/candidate pairs after warm-up on synthetic
or redistributable open data, with 100,000 and 1,000,000 occurrence records
where practical. Include all-natural, all-unique machine, duplicate-heavy
machine, 50/50 mixed, short-record, near-2,048-character, encoded-container,
and provenance-mixed workloads. Pin the router/model/validator bytes, Lingua,
translator, hardware plan, batch sizes, and cache state for each pair.

Every benchmark must report at least:

- input and canonical-parent occurrence counts;
- exact-unique texts entering the router;
- occurrence records proposed and, in activation experiments only,
  authoritatively bypassed by reason;
- exact-unique records separated into current C#-rejected, Python-bypassed, and
  actual translation-model-called buckets before applying the proposed policy;
- exact-unique Lingua evaluations, not just Lingua-eligible occurrences;
- occurrence translation candidates before and after routing;
- exact-unique translation-model requests after the existing run-local cache;
- translated child occurrences emitted from cached and uncached results;
- bytes read and written for canonical, assessment-with-routing, candidate,
  cache, and translated streams;
- router, Lingua, translation, and end-to-end wall/CPU time; and
- peak working set, GPU memory, GPU utilization, and temporary/cache bytes.

The shadow implementation may ship without a saving because it intentionally
changes no work. Paired scale validation requires exact candidate bytes and
exact assessment identity, order, decisions, gates, errors, and non-score
fields. Because independent Lingua calls can differ by one unit in the final
published 12-decimal place, only the five diagnostic score fields may differ by
at most that single quantum; their projected hashes remain reported so this
jitter is visible. Embedded metadata must not regress median end-to-end time,
total result bytes, or peak working set by more than 5%; per-row codes must obey
the fixed 32-character ASCII bound and all correctness gates must pass.

Authoritative structured bypass may be activated only if all correctness gates
pass and the baseline bucket proves which work the class actually reaches. A
class already rejected in C# or bypassed in Python must explicitly claim zero
translation-model-call savings; it cannot satisfy or be credited toward a
model-call gate. For a class that reaches Lingua but not the translator, require
at least a 20% reduction in exact-unique Lingua evaluations for its preregistered
representative workload and a measured net I/O, child/report, stage, or
end-to-end benefit after metadata cost. For a class proven to reach the
translator today, require at least a 20% reduction in exact-unique
translation-model requests and at least a 10% median end-to-end improvement on
that workload. The all-natural workload may not regress by more than 3% in
median end-to-end time or peak working set, and no workload may regress by more
than 5%. Results within measurement noise are unresolved, not a win.

Promoting fuzzy suppression requires a new Robin round using the accumulated
corpus and measurements; satisfying this ADR's statistical gate is necessary
but not sufficient.

All .NET, Rust, Python, PowerShell 5.1/7, decision-record, installer, bundle,
air-gap, report/provenance, privacy-scan, exact-command, and independently
installed public-release gates remain mandatory before distribution.

### Privacy-safe evidence gate

Committed fixtures must be authored synthetic data or redistributable open
corpora with recorded provenance and licences. Public benchmark summaries use
only those fixtures. Private cases may be used locally as a final operational
check, but neither samples nor exact case-derived counts, rates, hashes, paths,
hostnames, timings tied to identifiable inputs, or per-class distributions may
be committed or published. Release evidence may state only that the private
check passed or failed its pre-registered gates.

## Strongest detractor and resolution

The strongest objection is that a natural-language router creates a second
false-negative gate before translation. Machine syntax often contains the most
forensically useful prose: commands, error messages, localized configuration,
code comments, credential labels, and attacker instructions. Even a classifier
with excellent average AUC can fail under a new malware family, script, OCR
engine, or memory-residue distribution, while an apparently safe Base64/JWT
bypass can hide human text in its payload.

The decision survives only in its asymmetric form. Canonical parents and all
non-translation analysis remain intact; exact validators require whole-record
semantic coverage; reversible containers require decoded-child re-entry;
unknown and all errors fail open; provenance can only retain; and fuzzy
classification is shadow-only until a separate promotion decision. The first
implementation suppresses nothing, including exact structured proposals.
These controls defer performance gains in exchange for measured public
evidence and a much smaller false-negative blast radius.

### Robin review and rotated critiques

- **Primary-source Researcher:** language ID and natural-language-worthiness are
  separate tasks; NLoN supports a lightweight classifier architecture, while
  `Language ID in the Wild` and NLoN cross-source results require
  domain-specific evaluation.
- **Repository/Runtime Auditor:** the current Unicode-letter/identifier check
  precedes Lingua, but Full intentionally retains ambiguous and failed
  detections; canonical records and exact-cache behavior make a pre-Lingua,
  per-record router the narrowest integration point.
- **Forensic Detractor:** any heuristic that treats punctuation, entropy,
  provenance, or code syntax as proof of non-language can suppress real
  evidence; mixed-content and encoded payloads are the decisive adversarial
  cases.
- **Performance and Measurement Reviewer:** occurrence candidate totals
  overstate model work because exact deduplication already collapses repeated
  texts, while Python already model-bypasses some protected identifiers;
  acceptance must separate C# rejection, Python bypass, and actual exact-unique
  Lingua/translator work plus embedded-metadata I/O cost.
- **Privacy and Provenance Reviewer:** public evidence must be synthetic/open,
  while an assessment's compact routing object binds to its existing parent
  without copying case text or adding a second per-record stream.

In rotation, the Forensic Detractor challenged the Researcher's use of NLoN:
its lower cross-source results prevent direct threshold adoption. The
Performance Reviewer challenged the Runtime Auditor's raw candidate counts:
only post-dedup unique inference establishes saved model work, and existing C#
and Python suppression must be bucketed before attributing a saving. The
Repository/Runtime Auditor in turn challenged treating those two predicates as
equivalent and required a frozen cross-runtime boundary corpus. The Researcher
challenged using Magika per string: its documented task is file content-type
classification, not natural-language-worthiness. The Privacy Reviewer
challenged publishing private aggregate improvements because sparse totals can
still fingerprint a case.

The second Robin round and its rotated critiques resolved the next bounded
implementation step:

- **Research:** proposed a compact three-class human/machine/mixed model with
  GlotLID/OpenLID comparisons. Rotation narrowed this to the four-class human
  annotation taxonomy above, a binary `contains-any-human` scorer with
  abstention, optional GlotLID disagreement research only, and no runtime or
  teacher model.
- **Repository/runtime:** proposed a deterministic sparse model in Rust.
  Rotation required the already implemented C# provenance and actual
  routing/Lingua counters first, followed by an equal managed-versus-Rust and
  multi-size comparison. Rust, fixed point, and a 2 MiB ceiling are hypotheses,
  not decisions.
- **Forensic detractor:** demonstrated that localized messages, credentials,
  registry/browser material, OCR/FLOSS/decoded text, and code comments can be
  embedded in machine syntax. The decisive counterexample class is a mixed
  code/config record whose only valuable span is short localized prose;
  therefore `MIXED` is positive and every unvalidated provenance/script/length
  cell retains.
- **Performance/measurement:** required actual calls rather than candidate
  occurrences. The C# ledger now distinguishes batch-unique routing work,
  Lingua-eligible occurrences, Lingua executions, and reuse hits. The Python
  schema-1 translation ledger is now atomically published, SHA/cardinality
  validated by managed C#, and projected into both status documents; learned-
  model work remains blocked on the corpus and shadow gates above.
- **Privacy/provenance:** rejected private-case calibration and automatic
  teacher labels. Public corpora must preserve licence and source-family groups;
  private examinations can provide only a pre-registered non-published veto.

The second round therefore stops at instrumentation, provenance, public-corpus
governance, and shadow experiment design. It grants no learned-model or fuzzy
suppression authority. Neighbor aggregation also remains unresolved because it
changes semantic units and provenance, so it is deferred rather than silently
included.

The third independent Robin round reviewed the learned experiment before any
runtime scorer was implemented:

- **Primary-source and corpus reviewer:** recommended a managed,
  dependency-free sparse-linear float reference, with MASSIVE clean positives,
  manually labelled and individually licensed CodeSearchNet candidates, and
  evaluation-only CommonLID/GlotOCR. It excluded the GPL-3 NLoN assets while
  retaining the paper as evidence, and made fastText the next independently
  implemented benchmark challenger.
- **Repository/runtime auditor:** required a bounded prediction artifact,
  frozen feature/model identities, exact-only, engineered-only, n-gram-only,
  and combined ablations, plus work-ledger attribution against existing C#,
  Python, and cache suppression. It initially proposed an integer contract and
  early managed-versus-Rust comparison for deterministic deployment.
- **Forensic/statistical detractor:** showed that test reuse, related-source
  rows, many unadjusted subgroup intervals, and a private pass could each create
  a false safety claim despite zero observed misses. It required locked phase
  isolation, source-family units, simultaneous confidence, and private veto-only
  semantics.

In rotation, the corpus reviewer challenged the runtime auditor: integer
arithmetic and Rust do not establish transfer safety, data rights, or causal
savings, and no primary evidence showed that quantization preserves the
asymmetric false-suppression boundary. The runtime auditor challenged the
corpus plan: a mixed-source model score without ablations and actual-call
buckets cannot attribute saved work. The detractor challenged both: 95% bounds
computed separately for many cells do not preserve 95% confidence for the
combined policy, while translations, fragments, and synthetic variants are not
independent trials. The rotated synthesis therefore starts with the managed
float reference, fixes `K` during calibration, requires simultaneous test
bounds, keeps every learned path benchmark-only, and defers fastText runtime
use, ONNX, integer inference, Rust/P/Invoke, and NativeAOT until a safe finalist
exists.

The unresolved question is whether any public, independently grouped cell can
simultaneously meet the false-suppression and material-work gates under
forensic transfer. Neither model family nor implementation language can resolve
that question in advance. Therefore every learned report remains
`promotionEligible: false`, a private pass has no approving force, and fuzzy
runtime integration requires another Robin decision.

## Falsifiers and revisit triggers

Reject, disable, or roll back authoritative routing if any of the following
occurs:

- a canonical parent, canonical-parent pattern finding, token/credential
  finding, or provenance value differs; translated-child finding or histogram
  cardinality may fall only for an expected unchanged child omitted by a
  validated bypass and only when an identical canonical-parent counterpart is
  retained;
- a required multilingual, mixed, OCR, FLOSS, PDF, decoded, or short-text
  fixture is authoritatively bypassed;
- a validator accepts a noncanonical, partial, ambiguous, prefix/suffix, or
  merely shape-matching value;
- an encoded container bypasses without its complete decoded child entering
  the same router;
- `unknown`, model/validator failure, unsupported script, or provenance
  conflict suppresses existing Full work;
- embedded routing objects are missing, duplicated by schema, inconsistent
  with their assessment or run/summary aggregates, nondeterministic, partially
  published, or leave abandoned artifacts;
- private case material or identifying aggregates enter public evidence;
- train, calibration, locked test, external evaluation, or source-family groups
  leak across phases, or a failed test influences a revised model without a new
  untouched test set;
- a promotion uses an unadjusted per-cell interval, changes the preregistered
  `K` after test access, treats related rows as independent, or cannot defend
  the independence assumption for its claimed simultaneous bound;
- MASSIVE supplies anything other than attributed clean positives, an NLoN
  GPL-3 asset enters an experiment, a CodeSearchNet row lacks its original
  compatible source licence/manual label, or an evaluation-only corpus supplies
  training or calibration data;
- a private pass approves, ranks, tunes, or rescues a public candidate, rather
  than only a private failure vetoing it;
- a learned scorer enters runtime, the bundle, installer, or product schemas
  while `promotionEligible` is `false`, or Rust, NativeAOT, ONNX, or integer
  inference is selected before a benchmark-only scorer becomes a safe finalist;
- an activation claims translator savings for a class already rejected by C#
  or bypassed by Python, misses its applicable preregistered Lingua/model-work
  gate, fails to produce a net measured benefit, or causes the all-natural
  regression to exceed 3%; or
- the translator, detector, decoder, origin schema, model, validator, or
  canonical-record contract changes without revalidation.

Rollback disables authoritative bypass as one policy-versioned switch, treats
every router result as `retain`, and restores the previous Full candidate
stream while preserving the failed run's assessment metadata for diagnosis.
The release must not reuse caches across the router-policy identity change.
Reopen this ADR before promoting fuzzy suppression, using context aggregation
or substring translation, allowing provenance to suppress, or adding a
structured class.

## Consequences

Full gains a transparent proposed answer to “is translation useful?” before
asking “which language is this?” The first implementation changes no Lingua,
candidate, cache, or translation work. It records one bounded code per existing
assessment row, bounded origin provenance, and reconciled routing/Lingua work
counters. After explicit activation, strict opaque structures can avoid work
without discarding their parent evidence. Examiners interpret each proposal
through the run-level version/mode/codebook and audit attempted results through
the pre-existing assessment and translated-child fields. The validated
`translationWork` projection now separates window-local decisions, cache paths,
translator dispatches/inputs, results, fallbacks, and child occurrences. No
learned-model saving is yet claimed.

The cost is one compact code object in every language-assessment row,
run/summary policy metadata and aggregates, validators, decoder re-entry
guarantees, an evaluation corpus, subgroup statistics, and bounded per-record
CPU/I/O. There are no immediate work savings and no second per-record report.
Machine-like records continue through the unchanged pipeline until evidence
justifies an explicit safe activation. Context-aware reconstruction and
substring translation remain separate work.

Implementation is a runtime and report-schema change. It requires a product
version bump, updated user/help/maintenance documentation, a Windows quality
release, and independent public installer/bundle verification after the exact
command completes successfully.

## Primary references

- [NLoN: Natural Language or Not](https://arxiv.org/abs/1803.07292), including
  lightweight character-trigram results and lower cross-source performance.
- [NLoN package metadata](https://raw.githubusercontent.com/M3SOulu/NLoN/master/DESCRIPTION),
  recording its GPL-3 licence and bundled software-engineering-domain training
  data; the package and assets are excluded while the paper remains evidence.
- [Language ID in the Wild](https://aclanthology.org/2020.coling-main.579/),
  documenting domain-mismatch and class-imbalance failures outside held-out
  language-identification evaluation.
- [Language Identification of Short Text Segments](https://aclanthology.org/L10-1193/),
  evaluating the distinct difficulty of 5--21-character language samples.
- [`lingua-rs` upstream](https://github.com/pemistahl/lingua-rs), documenting
  its human-language label task and length-dependent confidence separation.
- [Magika upstream](https://github.com/google/magika), a file content-type
  classifier rather than a per-record natural-language authority.
- [GlotLID v3 model card](https://huggingface.co/cis-lmu/glotlid) and
  [paper](https://arxiv.org/abs/2310.16248), used only to define an optional
  pinned disagreement-comparison boundary, not a teacher or runtime gate.
- [OpenLID-v3](https://aclanthology.org/2026.vardial-1.23/) and its
  [GPL-3.0 model card](https://huggingface.co/HPLT/OpenLID-v3), documenting the
  web-LID/noise task considered and excluded from teacher/runtime use.
- [MASSIVE](https://arxiv.org/abs/2204.08582) and its
  [CC-BY-4.0 dataset card](https://huggingface.co/datasets/AmazonScience/massive),
  a source-grouped clean multilingual-positive candidate rather than forensic
  calibration evidence.
- [CommonLID](https://huggingface.co/datasets/commoncrawl/CommonLID) and the
  [GlotOCR evaluation licence](https://huggingface.co/datasets/cis-lmu/GlotOCR-bench/blob/main/LICENSE),
  both restricted here to evaluation use.
- [CodeSearchNet](https://github.com/github/CodeSearchNet), whose comments and
  code pairs illustrate why whole code sources are not machine-only negatives
  and whose `_licenses.pkl` boundary illustrates why per-source licences and
  repository grouping must be preserved.
- [fastText upstream](https://github.com/facebookresearch/fastText), documenting
  its MIT-licensed supervised character-n-gram and quantized model paths; it is
  a benchmark challenger rather than a teacher or runtime dependency.
- [NIST Bonferroni guidance](https://itl.nist.gov/div898/handbook/prc/section4/prc473.htm),
  documenting simultaneous confidence control across a finite preselected set.
- [.NET NativeAOT deployment guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
  and [source-generated P/Invoke guidance](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation),
  documenting the benefits and compatibility/boundary costs that require a
  finalist benchmark rather than an assumed runtime choice.
- [RFC 4648: Base-N encodings](https://datatracker.ietf.org/doc/html/rfc4648),
  including canonical Base64 and Base64url alphabets and padding rules.
- [RFC 7519: JSON Web Token](https://datatracker.ietf.org/doc/html/rfc7519),
  defining compact JWT serialization over Base64url-encoded values.
- [RFC 9562: UUIDs](https://datatracker.ietf.org/doc/html/rfc9562), defining
  current UUID layout and textual representation.
- [Current translation eligibility](https://github.com/Donovoi/bstrings/blob/master/bstrings/TranslationTextEligibility.cs)
- [Current language triage](https://github.com/Donovoi/bstrings/blob/master/bstrings/LanguageTriageCore.cs)
- [Existing semantic validators](https://github.com/Donovoi/bstrings/blob/master/bstrings/BuiltInSemanticValidator.cs)
- [Translation integrity and exact run-local deduplication](adr-0005-translation-integrity-and-run-dedup.md)
- [Output and provenance contract](../output-and-provenance.md)
- [High-level decision review policy](decision-review-policy.md)
