# ADR-0015: Use high-confidence Base64 findings and bounded match reuse

- **Status:** Accepted detector; match cache retained as a disabled experiment
- **Date:** 2026-08-18
- **Scope:** built-in pattern selection, Base64 matching, enrichment matching, and report guidance
- **Decision type:** forensic coverage, default pattern policy, and performance
- **Review method:** three independent reviews and rotated critiques under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source and external-practice research; repository and runtime audit; adversarial detractor and benchmark design
- **Implementation state:** detector implemented; cache gate failed and production capacity is zero; published releases remain unchanged

## Context

The built-in `b64` pattern currently accepts any standard-Base64 alphabet token
with at least eight characters, a length divisible by four, and superficially
canonical padding. An unpadded token passes semantic validation without being
decoded. Ordinary names and hexadecimal identifiers therefore create many
investigator-facing findings.

The current matching stage evaluates every selected pattern for every enriched
record. It caches compiled regular expressions, but it does not reuse results
for repeated complete record text. Every occurrence must remain in the evidence
because its record ID, source, location, origin, attributes, and lineage can be
different. Only the text-dependent pattern work is reusable.

`feature-histogram.tsv` already groups each pattern and matched value and gives
its occurrence count. Matches longer than 512 characters use a SHA-256 digest
and preview as the bounded grouping representation. Adding another unique-value
report would duplicate this output and expand report, resume, and privacy
surfaces.

## Pre-registered claims and falsifiers

The review tests these claims:

1. Base64 alphabet membership alone has a high background collision rate.
2. A bounded canonical decode plus decoded-content evidence reduces that noise.
3. A separate opt-in shape detector preserves ambiguous legacy candidates.
4. A run-local exact-text cache can avoid repeated matching without changing
   any occurrence, ordering, or provenance.
5. The existing feature histogram is sufficient for unique-value counts.

The decision is falsified if strict findings retain authored Windows-name or
hexadecimal noise, required accepted fixtures are lost without the candidate
fallback, `all` selects the broad candidate, cache-on output differs by one
byte, cache limits are exceeded, unique-input work regresses beyond the gate,
or the cache has no material benefit on repeated complete records.

## Independent first-round reviews

### External practice and primary sources

The external review found that mature tools do not treat every short
Base64-shaped token as a high-confidence finding.

- `bulk_extractor` looks for long, structured, constant-width blocks. Its first
  line must be at least 60 characters, and the scanner requires multiple lines.
  It decodes the block and recursively scans the decoded bytes instead of
  writing a generic Base64 feature file.
- YARA searches the Base64 encodings of known plaintext and warns that short
  Base64 searches are ambiguous.
- Suricata decodes inside an explicit rule and buffer. A decoded-data match is
  then required.
- CyberChef Magic ranks analyst-selected transformations with UTF-8 validity,
  file signatures, language signals, useful operations, and entropy. Entropy
  is not a validity rule.
- RFC 4648 defines a reversible encoding for arbitrary octets. Canonical syntax
  does not establish intent or usefulness.

The external review accepted a 24-character generic floor as a project policy,
not as an industry standard. It required contextual or candidate recovery for
short, letter-only, hexadecimal-looking, and opaque-binary values.

### Repository and runtime audit

The runtime audit confirmed that direct CPU, streaming, RAPIDS verification,
enrichment matching, normal analysis, and Full analysis converge on the same
linear Base64 matcher and semantic validator. One change can therefore keep the
surfaces consistent.

The audit recommended a per-invocation exact-text LRU. Its cached value contains
only ordered pattern ordinals and match spans. Every output row is rebuilt from
the current record. The cache must exclude any time-dependent validation. The
current `dob` validator reads the current UTC date, so that pattern is evaluated
for every occurrence and is never reused.

The audit also confirmed that `feature-histogram.tsv` is the existing bounded
unique-value count report. A new file would require new stage-12 artifacts,
checkpoint hashes, quarantine rules, summary fields, package checks, and a
separate source-file distinct-set design.

### Adversarial detractor

The detractor showed that strict filtering necessarily loses some valid
Base64. The strongest example is a long all-`A` value, which canonically decodes
to NUL bytes. Short encoded text, pure-hex-looking encodings, compressed data,
encrypted data, and folded MIME or PEM blocks are also valid counterexamples.

The detractor required:

- an explicit opt-in shape-only recovery surface;
- full-stream text validation with no NUL and only permitted controls;
- structurally strong and versioned binary-signature checks;
- a logical cache charge plus a separate peak-working-set gate;
- no partial cache insertion after a timeout or descriptor overflow;
- constant-hash, eviction, cancellation, ordering, and distinct-provenance
  tests; and
- separate rollback for the detector and cache.

## Rotated critiques

The external reviewer accepted the detractor's conclusion that
`feature-histogram.tsv` already provides the requested count view. It refined
the Base64 split so strict and candidate results are disjoint. Complete legacy
shape coverage is obtained by selecting both.

The runtime reviewer challenged a naive `b64_candidate` catalog entry. The
current `all` expansion selects every catalog item, and `all,b64_candidate` is
currently parsed incorrectly as a custom expression plus one name. The final
design adds explicit default-selection metadata and expands `all` at token
level.

The detractor challenged the fixed decode cap and the phrase "valid text." The
final design validates the complete decoded payload inside the cap; it does not
classify a prefix. Strict decoding alone is insufficient. Publishable text must
also reject NUL, disallowed controls, invalid scalars, and content with fewer
than four visible characters.

The initial external proposal rejected every unpadded letter-only value before
decoding. The detractor supplied a canonical letter-only value that decodes to
ordinary text. The synthesis therefore relies on complete decoded-content
validation instead of an unconditional letter-only rejection. An all-`A`
binary still fails the publishable-text rule and remains available through the
candidate pattern.

The cache remains a reversible experiment. External tools support conservative
Base64 classification, but they do not prove that a whole-record LRU will help
this workload. Promotion depends on the pre-registered paired benchmark.

No material disagreement remains. Folded MIME/PEM scanning and source-file
counts remain deferred.

## Considered options

1. **Keep the current broad `b64` default.** Rejected because it dominates the
   findings table with ambiguous words and identifiers.
2. **Raise only the minimum length.** Rejected because long hex identifiers and
   opaque canonical bytes remain investigator-facing findings.
3. **Use entropy as a validity threshold.** Rejected because text, secrets,
   compressed bytes, encrypted bytes, and repetitive binary overlap.
4. **Delete ambiguous candidates.** Rejected because some are valid and useful
   under external context.
5. **Add another unique findings report.** Rejected because the feature
   histogram already supplies the bounded occurrence-count view.
6. **Use a persistent or SQLite match cache.** Rejected because lookup cost and
   privacy surface can exceed the matching work.
7. **Use strict default findings, an opt-in candidate partition, and a bounded
   transient cache.** Accepted, subject to separate detector and cache gates.

## Decision

### High-confidence `b64`

The default `b64` pattern means high-confidence Base64 content evidence. It is
not a complete Base64 detector.

The generic profile requires:

1. one complete standard-Base64 token;
2. 24 through 16,384 encoded characters;
3. legal terminal padding and zero unused pad bits;
4. rejection of a token made only of hexadecimal characters;
5. successful bounded decoding and exact ordinal standard-Base64 re-encoding;
6. validation of every decoded byte; and
7. either strict publishable supported text or a versioned recognised binary
   signature or structure.

Supported generic text is strict UTF-8, UTF-8 with BOM, or UTF-16LE/BE with an
explicit BOM. BOM-less UTF-16LE remains limited to the contextual PowerShell
decoder. Publishable text has no NUL, allows only tab, CR, and LF controls,
contains no invalid scalar or disallowed Unicode category, and contains at
least four visible characters.

Recognised binary checks build on the decoder's deterministic signature list.
The stricter finding policy validates PE secondary headers, ELF identity
fields, ZIP local-header length and version, the complete PNG signature, gzip
method and reserved flags, and the complete signatures for other supported
formats. Decoder outcome semantics remain unchanged. A result means
signature-supported binary, not a fully parsed file. This decision does not
decompress or recurse.

Entropy does not affect matching, suppression, limits, or cache identity.

### Opt-in `b64_candidate`

`b64_candidate` retains canonical legacy-shape candidates that do not qualify
as high-confidence `b64`. The two built-ins are disjoint. Selecting
`all,b64_candidate` recovers the complete former syntactic result set, with the
same spans and occurrence provenance partitioned between the two names.

Built-in definitions carry explicit default-selection metadata.
`--lr all` means all default investigator patterns and excludes
`b64_candidate`. `all` expands at token level inside comma-separated pattern
selections. Explicit names, groups, and custom expressions retain their order,
and expanded built-ins are deduplicated case-insensitively.

Help and documentation label `b64_candidate` as broad and opt-in. Short,
hexadecimal-looking, letter-only binary, opaque, and oversized values can
appear there. MIME/PEM folded-block scanning remains a separate byte-oriented
decision.

### Bounded exact-text match reuse

Each `EnrichmentRegexPipelineCore.ProcessAsync` invocation creates one private
in-memory LRU. The key is the complete record text with ordinal equality. The
dictionary hash is an index only; exact equality decides identity. The value is
an ordered array of pattern ordinal, match start, and match length.

The experimental limits are:

- 100,000 entries;
- 64 MiB logical retained charge;
- 65,536 UTF-16 characters per key; and
- 4,096 descriptors per entry; and
- no more than 2,048 first-sighting probation entries inside the same entry
  and logical-byte limits, followed by a 524,288-record probation cooldown when
  that set fills without local promotion.

Logical charge conservatively includes fixed entry, dictionary, linked-list,
string, array, and descriptor allowances. Peak working set is gated separately.
Checked arithmetic is required. An entry that cannot fit is not retained.

The cache stores empty results. It inserts only after every cacheable selected
pattern finishes successfully. At descriptor overflow, matching and publication
continue once, but no partial entry is inserted. Oversized records bypass the
cache. The time-dependent `dob` pattern always executes in its original pattern
position and is not stored.

The exploratory 100,000-record run showed that admitting a complete result on
its first sight retained unique text without benefit. Before the acceptance
run, the implementation added an exact-key FIFO probation. A first sight stores
no match descriptors. A second exact sight becomes eligible for result storage,
and a third can reuse it. Probation and full entries share the 100,000-entry and
64 MiB limits. Hashes remain dictionary indexes only; ordinal string equality
decides probation, promotion, and reuse. When the probation set fills, it is
cleared and new first sightings bypass probation for 524,288 records. Sampling
then starts again. Existing full results remain available during the cooldown.
The first gate-eligible run used 4,096 probation entries and a 262,144-record
cooldown. It failed one unique no-match pair at +5.524% and one oversized pair
at +5.017%, while retaining exact output parity. Before the second
gate-eligible run, admission was reduced to 2,048 entries followed by a
524,288-record cooldown. The benchmark thresholds and workloads did not change.
This second amendment was frozen before its run and is independently reversible
with the cache.

Every hit recreates match text, context, line number, source record ID, source
file, location, origin, parent, transform, evidence class, and attributes from
the current occurrence. Record order, then selected-pattern order, then match
order remain unchanged.

The cache is never static, persistent, logged with values, checkpointed, or
resumed. Capacity zero is the exact rollback path. Pattern-stage validation
regenerates the evidence independently and requires byte equality.

Benchmark counters contain no record text. They report hits, misses, probation
observations, bypasses, stores, evictions, reused pattern evaluations, result
rows computed or served, and current and peak logical charge. Production
capacity is zero, so the experiment adds no `run.json`, `summary.json`, cache,
or checkpoint artifact.

### Existing unique-value report

`findings.tsv` remains the complete occurrence table. `feature-histogram.tsv`
remains the unique pattern-and-value count view. Documentation makes this use
prominent and states the 512-character digest-and-preview boundary. No new
report file or report schema is added.

## Non-negotiable invariants

1. Every occurrence and provenance field remains in the detailed evidence.
2. Strict and candidate Base64 spans are disjoint; their union reproduces the
   former broad syntactic coverage inside the former bounds.
3. `all` never selects `b64_candidate`.
4. Direct, streaming, RAPIDS-verified, and enrichment paths agree.
5. Base64 validation is linear and bounded.
6. Cache-on and cache-off evidence and reports are byte-identical.
7. Cache collision, eviction, capacity, or worker count cannot affect output.
8. No cache value is persisted or written to diagnostics.
9. Failure or cancellation publishes no partial match stage or cache artifact.
10. Resume never trusts an old pattern contract or transient cache state.
11. Existing findings and report headers do not change.
12. Tests, benchmarks, documentation, commits, and release assets contain only
    authored synthetic or redistributable public values.

## Acceptance gates

### Detector gates

- Reject authored Windows names, path components, pure-hex identifiers, hashes,
  GUID fragments, decimal identifiers, malformed padding, internal padding,
  non-zero pad bits, Base64URL, opaque random bytes, and disallowed controls
  from high-confidence `b64`.
- Accept required canonical UTF-8, BOM text, and supported binary-signature
  fixtures inside the declared bound.
- Test 24 and 16,384 encoded-character boundaries, the first rejected length,
  and multibyte text at the boundary.
- Test short text, all-`A`, pure-hex-looking, opaque, and oversized candidates
  through `b64_candidate`.
- Require strict results to be absent from candidate results and require the
  combined span set to equal the former broad result set.
- Require direct, streaming, RAPIDS verification, and enrichment parity.

### Cache correctness gates

- Compare capacity zero and experimental capacity. Require byte-identical
  `regex-matches.jsonl`, `findings.tsv`, both histograms, and HTML.
- Test exact duplicate text with different files, offsets, record IDs, origins,
  evidence classes, attributes, parents, and transforms.
- Inject constant dictionary hashes and require unequal strings to remain
  separate.
- Test hot hits, deterministic eviction, Unicode normalization differences,
  zero matches, zero-width matches, descriptor overflow, entry-charge bypass,
  regex timeout after earlier patterns, and capacity zero.
- Cancel before lookup, during computation, before and after insertion, during
  hit projection, and during flush. Require no published partial or cache file.
- Require all cache counters to reconcile and logical charge to stay at or
  below 64 MiB.

### Pre-registered paired benchmark

Run at least seven alternating Release cache-off/cache-on pairs after warm-up.
Use identical input digests and fixed worker counts. Each workload runs long
enough to exceed timing noise.

Workloads include:

1. one million all-unique mostly-no-match records;
2. one million all-unique match-heavy records;
3. at least 90 percent exact-text reuse inside the cache limits;
4. cold repeats beyond capacity;
5. repeated match values inside unique surrounding text;
6. Base64-confusion negatives and accepted positives; and
7. oversized text and more than 4,096 descriptors.

Record wall and CPU time, managed allocations, peak working set, cache counters,
logical charge, input/output bytes, and output hashes.

Required results:

- exact output parity in every pair;
- all-unique median wall regression at or below 3 percent and no run above
  5 percent;
- cold-repeat and bypass workloads no more than 5 percent slower;
- hot-repeat median wall improvement of at least 20 percent;
- cache logical charge at or below 64 MiB; and
- peak-working-set growth below the larger of 10 percent or 96 MiB.

If the cache fails, set its default capacity to zero and release the detector
change independently.

### Integration gates

- Focused catalog, semantic, streaming, enrichment, report, resume, and CLI
  tests pass.
- Full .NET and relevant Python, PowerShell, Markdown, decision, package, and
  privacy gates pass.
- The packaged single-file command shows the strict/default and broad/opt-in
  pattern descriptions.
- No private case value, path, hash, or aggregate appears in outgoing changes.

## Measurements and promotion result

The first implementation ran seven alternating pairs at 100,000 records per
workload on the accepted Windows host. The oversized workload used 20 records.
Every pair had exact output SHA-256 parity. This run was exploratory and did
not activate the one-million-record acceptance gate.

| Workload | Median wall delta | Maximum wall delta |
| --- | ---: | ---: |
| Base64 mix | -60.952% | -58.723% |
| Cold-labelled all-unique set at this scale | +3.532% | +10.600% |
| Hot repeat | -60.774% | -59.758% |
| Oversized bypass | +2.904% | +9.846% |
| Repeated feature inside unique text | +3.288% | +5.116% |
| Unique match-heavy | -0.450% | +2.931% |
| Unique no-match | +2.173% | +5.488% |

The hot workloads showed material value, but some short individual pairs
crossed the 5% limit. The cold-labelled fixture had no repeats at 100,000
records because its distinct set also contained 100,000 values. These results
do not promote the cache. The gate-eligible run uses one million records,
creates 125,000 cold-repeat values, and uses 2,000 oversized records. Raw rows
are in `benchmarks/results/match-reuse-exploratory-100k-2026-08.csv`.

The first gate-eligible run used one million records, seven alternating pairs,
4,096 probation entries, and a 262,144-record cooldown. All 49 pairs had exact
output SHA-256 parity. The cache stayed below its memory limits and delivered
the required repeated-text improvement. It did not pass because one
unique-no-match pair reached +5.524%, and one oversized pair reached +5.017%.

| First acceptance workload | Median wall delta | Maximum wall delta |
| --- | ---: | ---: |
| Base64 mix | -78.152% | -77.210% |
| Cold repeat | +2.794% | +4.040% |
| Hot repeat | -75.702% | -75.200% |
| Oversized bypass | +2.561% | +5.017% |
| Repeated feature inside unique text | +0.689% | +1.330% |
| Unique match-heavy | +0.411% | +2.290% |
| Unique no-match | +2.004% | +5.524% |

The second gate-eligible run kept the workload and thresholds fixed. It used
2,048 probation entries and a 524,288-record cooldown. All 49 pairs again had
exact output SHA-256 parity. Unique-input performance passed, but one oversized
bypass pair reached +6.384%. The cache therefore failed the unchanged gate a
second time and is disabled in production.

| Second acceptance workload | Median wall delta | Maximum wall delta |
| --- | ---: | ---: |
| Base64 mix | -77.905% | -77.700% |
| Cold repeat | +0.588% | +2.210% |
| Hot repeat | -75.521% | -75.290% |
| Oversized bypass | +2.804% | +6.384% |
| Repeated feature inside unique text | +2.490% | +2.780% |
| Unique match-heavy | +0.711% | +3.080% |
| Unique no-match | +1.908% | +4.790% |

Raw rows are in `benchmarks/results/match-reuse-acceptance-2026-08.csv`
and `benchmarks/results/match-reuse-acceptance-probation-v2-2026-08.csv`.
Production `MatchResultCacheOptions.Default` is the zero-capacity rollback.

## Strongest detractor and resolution

A short, all-letter, pure-hex-looking, opaque, encrypted, compressed, or very
large value can be valid Base64 and forensically useful. The strict default can
therefore reduce recall.

That objection is valid. The decision changes the meaning of `b64` from shape
membership to high-confidence decoded-content evidence. It preserves ambiguous
legacy candidates through the explicit `b64_candidate` partition and leaves
contextual decoding available. It does not claim complete Base64 discovery.

The cache has material value when complete record text repeats. It remains
independently reversible, but it is not promoted because the oversized bypass
workload exceeded the fixed worst-pair limit.

## Falsifiers and revisit triggers

Revisit the strict profile if the frozen negative corpus still produces
material noise, required text or binary fixtures are lost without candidate
recovery, or external context cannot promote short high-value values.

Roll back strict default selection if `all` includes the candidate, direct and
integrated paths disagree, bounds are exceeded, or the strict/candidate union
does not preserve the declared former coverage.

Roll back the cache independently by setting capacity to zero if any output
byte, ordering, count, context, or provenance changes; if memory exceeds its
bound; if values reach disk or logs; if cancellation leaves residue; if unique
work regresses beyond the gate; or if repeated work shows no material benefit.

Adding source-file counts, MIME/PEM block scanning, recursive decoding,
decompression, entropy ranking, or a persistent cache requires a new decision.

## Consequences

Default findings contain far fewer ordinary Base64-shaped names and identifiers.
Investigators can explicitly request ambiguous candidates when recall matters.

The cache experiment proves that repeated complete strings can avoid repeated
pattern evaluation while every occurrence remains visible. It is disabled in
production because its release gate did not pass.

The meaning of `all` becomes "all default investigator patterns." Explicit
opt-in built-ins remain available but do not silently widen default output.

The feature histogram remains the one unique-value count report. The detailed
findings schema remains stable.

## Primary references

- [RFC 4648: Base-N encodings](https://www.rfc-editor.org/rfc/rfc4648.html).
- [`bulk_extractor` v2.1.1 Base64 scanner](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/scan_base64.cpp).
- [CyberChef Magic implementation](https://github.com/gchq/CyberChef/blob/master/src/core/lib/Magic.mjs).
- [YARA Base64 string modifier](https://yara.readthedocs.io/en/stable/writingrules.html#base64-strings).
- [Suricata Base64 keywords](https://docs.suricata.io/en/suricata-8.0.2/rules/base64-keywords.html).
- [Microsoft PowerShell EncodedCommand](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_pwsh?view=powershell-7.6#-encodedcommand--e--ec).
- Current Base64 matcher: `bstrings/RegexOutputCore.cs`.
- Current semantic validator: `bstrings/BuiltInSemanticValidator.cs`.
- Current decoder: `bstrings/DecoderPipelineCore.cs`.
- Current report implementation: `bstrings/ForensicReportCore.cs`.
- [Current output contract](../output-and-provenance.md).
