# ADR-0005: Bound translation integrity failures and deduplicate inference per examination

- **Status:** Accepted
- **Date:** 2026-08-10
- **Scope:** Offline translation candidate selection, protected-token semantics,
  translation failure handling, progress, and run-local inference reuse
- **Decision type:** forensic semantics, availability, privacy, performance, and
  user-facing defaults
- **Review method:** Robin round under the
  [high-level decision policy](decision-review-policy.md)
- **Perspectives:** independent primary-source localization research,
  repository/runtime audit, forensic detractor, rotated critiques, and
  synthesizer measurements
- **Owner:** bstrings maintainers
- **Implementation state:** approved for a bounded implementation; release is
  blocked until every applicable gate below passes

### Post-implementation Robin amendment

The implementation review falsified the original relative all-unique cache
planning gate. On the final private-free one-million-row synthetic run, the
v1.9.15 bounded LRU completed its all-unique cache loop in 0.9209 seconds, while
the exact SQLite cache took 169.7911 seconds (169.79 microseconds per row), used
a 233,582,592-byte database, and retained an approximately 1,350,557-byte hot
set. The duplicate cycle placed 10,000 keys more than 4,096 entries apart:
model inputs fell from 1,000,000 to exactly 10,000 (99 percent), and the
exact-cache loop took 133.0827 seconds (133.08 microseconds per row), with a
2,252,800-byte database and approximately 1,551,212-byte hot set.

The review explicitly rejected hiding that relative regression behind model
latency, sampled/adaptive selection that would miss late repeats, and moving to
an unbounded in-memory cache. It retained unconditional run-local exact reuse
for the accepted CPU bundle, but replaced the incompatible relative-only gate
with both an absolute cache-cost ceiling and a model-inclusive gate. The
published quality model measures 0.2793 strings per second on the accepted CPU
benchmark; 169.79 microseconds is 0.004742 percent of that per-input time.
A materially faster future provider must re-run the break-even gate rather than
inherit this decision. The reproducer is
[`benchmark_translation_cache.py`](../../tools/enrichment/benchmark_translation_cache.py).

## Context

The v1.9.15 translation adapter compiles every protected-token expression with
case-insensitive matching. Its last generic expression therefore treats an
ordinary alphabetic hyphenated word as immutable machine data. If a translation
legitimately changes that word, exact-retention validation raises an error and
aborts the complete translation transaction. Earlier source, routing, native,
recovery, OCR, merge, and language-assessment files remain atomic and
attributable, but downstream matching and reports never run.

The same validator documents exact occurrence retention but implements only a
lower bound. A model can duplicate a protected identifier and pass. Mixed-script
tokens can also be partially matched because ASCII character ranges and global
case folding do not define a Unicode token boundary.

A representative multi-gigabyte memory examination also produced a
multi-million-row high-recall translation population. The adapter deduplicates
only inside a small window and a 4,096-entry in-memory LRU. A private aggregate
sample showed that exact repeats outside that bound materially increased model
calls. No case text, path, hash, host, or private count is part of this decision
record.

The current `high-precision` policy is only a label: all decision branches treat
it exactly like `balanced`. A proposed conservative 0.65 confidence and 0.15
target-margin floor reduced a private workload sharply, but a direct run over
the repository's 12 synthetic multilingual forensic cases selected only six.
The existing 0.55/0.10 balanced gate selected the same six. Aggregate volume is
not an accuracy measurement, and this falsified changing Full away from its
high-recall default in this decision.

The tests were fixed before implementation:

- ordinary linguistic hyphenation must not be a hard machine-identifier signal;
- hard structured identifiers must retain exact code points and exact counts;
- one rejected derived translation cannot suppress canonical source evidence or
  the remaining report;
- a repeated exact source/configuration pair must be inferred once per run while
  still producing one ordered child per parent;
- case text cannot enter a shared or cross-examination cache; and
- Full candidate recall cannot be reduced to obtain a better ETA.

## Considered options

1. **Keep the v1.9.15 behavior.** Rejected. A normal linguistic compound can
   prevent the complete final report, and duplicated protected tokens pass.
2. **Keep every alphabetic hyphen token hard-protected but fall back per row.**
   Rejected as the general fix. It restores availability but systematically
   turns valid translated prose into unchanged children and hides the classifier
   error.
3. **Remove the broad rule without a compensating control.** Rejected. A value
   such as a lowercase account or service name can resemble natural prose.
4. **Separate hard structured tokens from ambiguous alphabetic hyphenation.**
   Accepted. Class-specific formats and independent machine signals are exact
   invariants. Ambiguous hyphenation is recorded as such, the prompt still asks
   the model to preserve usernames, and the canonical parent remains available.
5. **Abort the whole run for any hard-token mismatch.** Rejected. The model
   output is a derived child; discarding one rejected output and publishing an
   explicit exact-source fallback is safer than losing all downstream reports.
6. **Allow unlimited silent fallbacks.** Rejected. A systemic model or prompt
   failure must trip a measured circuit breaker.
7. **Use an unbounded in-memory or shared translation cache.** Rejected because
   of memory growth, cross-case disclosure, stale model identity, and prior-run
   dependence.
8. **Use a bounded-memory, run-local disk exact cache.** Accepted. It is created
   inside the selected examination output, uses complete text equality and the
   complete translation identity, and is removed after the transaction.
9. **Make the provisional conservative language gate the Full default.**
   Rejected by the synthetic forensic recall gate. `high-recall` remains Full's
   default. `high-precision` may become behaviorally distinct as an explicit
   expert option, but it cannot be described as calibrated accuracy.
10. **Add CUDA or hybrid translation to this release.** Deferred. The quality
     bundle contains the accepted CPU llama.cpp runtime. A new GPU runtime,
     partial-offload rule, VRAM boundary, and hardware acceptance require a
     separate measured decision.
11. **Drop exact reuse after the isolated planner regression.** Rejected after
    the post-implementation Robin amendment. It would preserve the subsecond
    LRU loop but repeat days of CPU inference on duplicate-heavy examinations.
    Exact reuse remains subject to an absolute 250-microsecond-per-row ceiling,
    a one-percent model-inclusive ceiling on the pinned CPU runtime, and a
    mandatory revisit for a provider sustaining 500 or more unique strings per
    second.

## Decision

Protected identifiers are split into two classes.

**Hard structured identifiers** are class-specific formats such as URLs,
emails, IP/port values, paths, registry paths, filenames, hashes, GUIDs, CVEs,
hostnames, and explicit placeholders, plus separator tokens with an independent
machine signal such as an underscore or an all-uppercase ASCII code form. A
bare alphabetic hyphen does not create hard status. Generic token candidates use
Unicode word boundaries and cannot accept a suffix from a larger mixed-script
word.

**Ambiguous alphabetic hyphen tokens** remain advisory. Their presence is
recorded on the derived child; the original parent remains canonical and the
translation prompt continues to request username preservation. Later typed
span metadata may promote an exact occurrence to hard status, but that design is
deferred until offsets are bound to record identity, coordinate system, and
expected source text.

Hard-token validation compares the complete source and child occurrence
`Counter` values. Deletion, mutation, case change, normalization, or surplus
duplication is a mismatch. The rejected model text is never published. Instead,
that distinct source text receives exactly one derived child whose text is the
exact source and whose machine-readable integrity status is
`preservation-fallback`. A run-level circuit breaker aborts atomically after at
least 100 distinct model results when more than 1 percent are fallbacks, or
after 100 consecutive fallbacks.

Integrated translation uses a run-local SQLite exact cache backed by a random,
new physical file under the examination output. Its identity includes exact
source text, target, engine and version, model/revision/hash, prompt and
preservation-policy version, decoding policy, device/offload policy, and strict
determinism settings. A digest may index a row, but complete ordinal source
equality decides a hit. Only nonempty, cardinality-checked, integrity-checked
successes or explicit source fallbacks are inserted. The cache is never shared
between examinations and is removed on normal completion and failure cleanup.
It is deliberately non-resumable: an in-memory SQLite journal, synchronous-off
writes, and 4,096-row commits reduce temporary-cache cost, while the staged
translation output cannot replace prior output until the cache is closed and
strictly removed. A killed child cannot authorize reuse; the managed parent
removes exact physical cache, sidecar, and staged-output names or fails closed.

The adapter retains bounded in-memory hot entries over the disk cache, emits one
child per parent in original order, reports cache hits, unique model calls,
fallbacks, elapsed rate, percentage, and ETA, and keeps atomic final output.

`high-recall` remains the Full default. `high-precision` becomes a real explicit
policy with effective floors of 0.65 confidence and 0.15 target margin, records
those effective floors in every assessment, and is described as a conservative
operating gate rather than a calibrated probability or the most accurate mode.

## Non-negotiable invariants

1. Canonical parent text, record ID, input identity, source location, extraction
   engine, and provenance are never replaced or modified by translation.
2. Every translation candidate has exactly one ordered derived child. A
   fallback child uses exact source code points, is visibly distinct from a
   successful unchanged translation, and contains no rejected model text.
3. Every hard identifier has exact source-to-child value and occurrence-count
   equality. No partial mixed-script match, normalization, case fold, or extra
   occurrence can satisfy the invariant.
4. Alphabetic hyphenation alone is not a hard identifier. An advisory ambiguous
   token cannot be silently presented as a typed username or credential.
5. Cache identity uses complete exact text and every output-affecting runtime,
   model, prompt, policy, decoding, and hardware setting. Hash-only identity is
   insufficient.
6. Cache reuse cannot change child text, record ID, order, cardinality,
   attributes, parent linkage, or report matches relative to one inference per
   distinct exact key.
7. Cache state is scoped to one selected examination output, remains bounded on
   heap, is never telemetry or a global user cache, and is removed by all normal
   completion and handled-failure paths.
8. Cache corruption, schema mismatch, link/reparse ambiguity, cancellation, or
   write failure cannot publish partial translated output or silently reuse a
   value.
9. Candidate policy and effective thresholds remain machine-readable in the
   assessment records, and fallback totals remain machine-readable in
   `run.json` and `summary.json`. Cache statistics, progress percentage, rate,
   and ETA are truthful in the retained translation stderr log; they are not
   duplicated into run metadata in this release.
10. Full remains high-recall until a labeled, stratified corpus proves an
    alternative recall contract. Performance cannot waive forensic coverage.

## Acceptance gates

Automated tests must cover ordinary lower-, title-, and mixed-case linguistic
hyphenation; underscores and uppercase codes; every class-specific format;
Unicode scripts, combining forms, confusables, separators, and partial-token
boundaries. Deletion, mutation, case change, normalization, and surplus
duplicates must fail exact validation. One bad row in a mixed batch must produce
one explicit fallback while good siblings remain translated; the circuit breaker
must fail atomically at its exact boundaries.

Cache tests must force repeats beyond the in-memory window and LRU, digest
collisions, model/prompt/configuration changes, corruption, cancellation, and
output failure. Candidate and child files must be byte-identical to the uncached
reference apart from the declared integrity metadata. Model calls must equal the
number of distinct exact cache keys and heap retention must remain bounded.

Language-policy tests must prove `balanced` and `high-precision` differ at the
effective-floor boundaries and that assessments report both configured and
effective thresholds. The bundled synthetic multilingual forensic set must
continue to be selected completely by Full/high-recall. A conservative policy
cannot become default until a larger labeled benchmark establishes a
pre-registered recall floor and candidate precision with uncertainty bounds.

A reproducible synthetic benchmark of at least one million candidate rows must
include repeats separated beyond 4,096 entries. The candidate implementation
must reduce model calls to the global exact-key count and improve model-call
count by at least 25 percent over the v1.9.15 LRU on the duplicate workload.
The exact-cache all-unique cost must remain at or below 250 microseconds per row,
and its cache file plus bounded hot set must be reported. On the pinned standard
CPU runtime, model-inclusive all-unique regression attributable to the cache
must remain below one percent. Any accepted provider that sustains 500 or more
unique strings per second must re-run a break-even and resource review before it
can inherit this cache policy. Progress must begin at 0 percent, remain
monotonic, reach 100 percent, and include a bounded ETA after sufficient work
has completed.

The complete Python, .NET, Rust, PowerShell 5.1/7, decision, installer, bundle,
and release suites remain mandatory. The public immutable release must be
installed and verified independently. Private examination text or identifiers
cannot enter fixtures, logs, commits, release notes, or benchmark artifacts.

## Strongest detractor and resolution

The strongest objection is that an untyped lowercase value such as
`client-secret` can be a real username, service, campaign, or credential even
though a hyphen is also normal linguistic punctuation. Removing broad hard
protection can let the derived translation alter that value.

No lexical rule resolves this ambiguity. Keeping every hyphenated word hard
creates a demonstrated open-ended linguistic false-positive class and can make
the translation feature systematically return source text. The decision keeps
the parent canonical, retains the prompt instruction, records ambiguous-token
presence on the child, never promotes the child to byte-native evidence, and
requires typed exact spans as the long-term resolution. This is an explicit
residual risk, not evidence that the ambiguous token is safe to change.

The strongest operational objection is that run-wide exact dedup does not make
a multi-million-candidate CPU run fast. That is also true. The cache removes
provably redundant inference without reducing recall, adds an ETA so the cost is
visible, and does not claim that CPU translation is now short. GPU translation
and faster provider comparisons remain separate measured work rather than an
unverified release promise.

## Falsifiers and revisit triggers

Reject or roll back the implementation if:

- any canonical source value or provenance changes;
- a hard identifier is missing, changed, normalized, duplicated, or only
  partially matched without a visible fallback;
- a fallback is indistinguishable from a successful translation or contains the
  rejected model text;
- one isolated hard mismatch still aborts the complete translation transaction;
- the circuit breaker permits systemic degradation or trips below its declared
  boundary;
- cache reuse changes output bytes, ordering, cardinality, record identity,
  provenance, or matches;
- a collision, model/prompt/settings mismatch, corrupt database, link, or stale
  file produces a cache hit;
- private text survives outside the examination output or after cache cleanup;
- high-precision remains behaviorally equal to balanced or its effective gates
  are not reported;
- Full loses any bundled required multilingual forensic candidate;
- the duplicate benchmark improves model calls by less than 25 percent, the
  exact cache exceeds 250 microseconds per all-unique row, its pinned-CPU
  model-inclusive cost exceeds one percent, a provider at 500 or more unique
  strings per second ships without a new break-even review, or ETA/progress is
  false; or
- final documentation, CLI/TUI help, metadata, or the public bundle contradicts
  the executable.

Revisit the ambiguous-token boundary when upstream structural parsers can bind
typed spans to exact record IDs, text hashes, coordinate systems, and expected
substrings. Revisit Full's candidate policy only with labeled forensic recall
evidence. Revisit CUDA/hybrid translation with a separately pinned runtime,
VRAM/offload calibration, CPU baseline, output-parity gates, and clean-machine
hardware acceptance.

## Consequences

An isolated model integrity error no longer destroys the final report. Users can
filter explicit preservation fallbacks and advisory ambiguous-token children,
and exact duplicate identifiers can no longer pass validation. Repeated strings
outside the old LRU avoid redundant inference without collapsing parent
provenance or sharing evidence across cases.

The cost is a temporary case-local SQLite file, additional I/O and schema tests,
new integrity metadata, and a circuit breaker. High-recall Full can still be
long-running on noisy memory images; the release must say so and expose a real
ETA rather than imply that dedup alone solves model throughput.

The product keeps one Full profile, the accepted CPU translation runtime, and
all existing expert policy overrides. The optional high-precision policy becomes
truthful but does not become the default. Typed spans, masking, cross-run caches,
and GPU translation remain explicit follow-up decisions.

## Primary references

- [W3C Internationalization Tag Set 2.0 translate data category](https://www.w3.org/TR/its20/#trans-datacat)
- [OASIS XLIFF 2.1 core specification](https://docs.oasis-open.org/xliff/xliff-core/v2.1/xliff-core-v2.1.html)
- [Official German orthographic rules for hyphenated compounds](https://grammis.ids-mannheim.de/rechtschreibung/6159)
- [Microsoft Custom Translator dictionaries](https://learn.microsoft.com/en-us/azure/ai-services/translator/custom-translator/concepts/dictionaries)
- [Microsoft neural dictionary behavior](https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/how-to/use-neural-dictionary)
- [Python `sqlite3` module](https://docs.python.org/3/library/sqlite3.html)
- [Forensic output and provenance contract](../output-and-provenance.md)
- [Language-detection reuse decision](adr-0004-bounded-language-detection-reuse.md)
- [High-level decision review policy](decision-review-policy.md)
