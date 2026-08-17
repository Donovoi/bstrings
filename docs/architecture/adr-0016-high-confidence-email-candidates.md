# ADR-0016: Use high-confidence email findings and an opt-in broad partition

- **Status:** Accepted
- **Date:** 2026-08-18
- **Scope:** built-in email matching, pattern defaults, RAPIDS verification, enrichment matching, help, and report guidance
- **Owner:** bstrings maintainers
- **Decision type:** forensic coverage and default pattern policy
- **Review method:** three independent first-round evidence tracks and rotated critiques under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source and external-practice research; repository and runtime audit; adversarial detractor and benchmark design
- **Implementation state:** implemented and validated; published releases remain unchanged

## Context

The previous `email` pattern followed the broad RFC 5322 dot-atom alphabet.
Characters such as `=`, `|`, `%`, and `'` are legal `atext`. The pattern also
accepted any alphabetic top-level label. In carved binary and Windows resource
text, those choices turn package names, DLL references, resource identifiers,
and random short tokens into investigator-facing email findings.

RFC syntax is not evidence of an assigned Internet name. RFC 5322 explicitly
says the domain portion must follow the addressing protocol used by the
application. The IANA root-zone database is the authoritative registry of
public top-level domains. It does not prove that a lower-level domain or
mailbox exists.

Forensic evidence can also contain historic domains, private suffixes, local
mail systems, and uncommon but valid mailbox punctuation. A stricter default
therefore needs a separate, explicit recovery surface.

## Pre-registered claims and falsifiers

The review tested these claims:

1. Broad RFC dot-atom syntax explains the observed resource-string noise.
2. Common mailbox punctuation plus an IANA top-level-domain snapshot removes
   that noise without losing ordinary public addresses.
3. An opt-in broad partition preserves uncommon and non-public candidates.
4. The stricter check can remain deterministic, offline, allocation-bounded,
   and fast enough for large evidence sets.

The design is falsified if the default retains any authored package, DLL,
resource, one-letter-suffix, or undelegated-suffix fixture; loses an accepted
common public address; selects the broad partition through `all`; performs DNS
or network I/O; allocates more than the gate; or exceeds the fixed absolute
timing gate.

## Independent first-round reviews

### External practice and primary sources

RFC 5322 permits many punctuation characters in an unquoted dot-atom. It also
states that the domain must conform to the addressing context. RFC 5321 limits
the local part to 64 octets and the domain to 255 octets.

The pinned `bulk_extractor` 2.1.1 email scanners use a much narrower mailbox
form than the full RFC alphabet. They also use a hard-coded top-level-domain
list. The flex scanner states that this is intended to improve precision and
separately emits histograms for repeated email and domain values.

The external track accepted the design direction but rejected copying
`bulk_extractor`'s old TLD list. The current IANA list is authoritative and can
be snapshotted with an exact version and digest for deterministic offline use.

### Repository and runtime audit

The audit confirmed that direct CPU matching, bounded streaming, RAPIDS
verification, enrichment matching, normal analysis, and Full analysis all use
the built-in definition and `BuiltInSemanticValidator`. A catalog and semantic
change therefore reaches every authoritative output path.

`all` expands only definitions whose `SelectedByAll` property is true. The
broad partition can be added safely with `SelectedByAll: false`. The `pii`
group continues to select only the strict `email` pattern.

The existing `feature-histogram.tsv` report already groups repeated
`(PatternName, Match)` values and counts occurrences. No new report or
persistent cache is required for this decision.

### Adversarial detractor and benchmark design

The detractor supplied the strongest contrary examples:

- `#@example.com` is a legal RFC dot-atom mailbox but not a common form;
- `user@host.internal` can be meaningful inside a private environment;
- a top-level domain can be removed after evidence was created; and
- checking only today's IANA list cannot establish historic validity.

The detractor required a broad opt-in pattern, exact source/version/digest
identity for the snapshot, no DNS lookup, disjoint identical candidate
classification, and explicit wording that neither result proves delivery or
ownership.

The benchmark track required authored synthetic fixtures only. It compares the
former production regex and length check with the candidate under the same
interpreted .NET regex engine. It uses fresh processes, alternating order,
seven pairs, one million rows, exact valid-address value parity, and exact
noise cardinality.

## Rotated critiques

The external-practice track challenged the runtime proposal to use only a
two-character minimum suffix. Random four-character extensions would still
pass. The synthesis uses the complete IANA root-zone snapshot instead.

The runtime track challenged direct reuse of `bulk_extractor`'s TLD
alternation. Its pinned list is old, and a 1,438-alternative .NET expression
was measured at roughly ten times the baseline on the all-valid diagnostic.
The synthesis keeps the compact domain regex and performs an allocation-free
offline lookup after a match.

The detractor challenged the phrase "valid email." The final name remains
`email` for compatibility, but descriptions and reports say
"high-confidence" and describe exactly what is established. The result does
not claim DNS, delivery, mailbox existence, ownership, or historic delegation.

The runtime track also challenged adding a unique-email report. The feature
histogram already supplies the counted distinct view, so no fifth report is
added.

No unresolved disagreement remains. Internationalised local parts, quoted
local parts, domain literals, DNS resolution, Public Suffix List registrable
domain classification, and historic root-zone reconstruction remain outside
this decision.

## Considered options

1. **Keep the broad default.** Rejected because it creates large resource and
   package-extension noise.
2. **Require only two or more suffix characters.** Rejected because arbitrary
   extensions still pass.
3. **Perform live DNS or MX lookups.** Rejected because results are mutable,
   unavailable offline, privacy-sensitive, and not proof of mailbox existence.
4. **Use the Public Suffix List.** Deferred. Registrable-domain boundaries are
   useful but are not required to reject the reported TLD noise.
5. **Copy the pinned bulk_extractor TLD expression.** Rejected because it is
   stale and materially slow as a large .NET alternation.
6. **Delete broad candidates.** Rejected because private, historic, and
   uncommon RFC forms can be relevant.
7. **Use a strict default plus opt-in broad partition.** Accepted.

## Decision

### High-confidence `email`

The default `email` pattern requires:

- an ASCII local part using letters, digits, dot, underscore, plus, or hyphen;
- an alphanumeric first and last local-part character;
- no empty or punctuation-bounded dot segment;
- the existing SMTP local, domain, and total length limits;
- DNS-style domain labels; and
- a top-level label present in the bundled IANA snapshot.

The snapshot is IANA version `2026081700`, last updated
`2026-08-17T07:07:01Z`, with 1,438 entries and source SHA-256
`681c3d70701a1095f725842187dc6f26ddad20e6563f48e0ab33eedad790ab78`.
Lookup is exact, case-insensitive, allocation-free, and offline. A hash selects
a bounded open-addressed slot, but exact text equality decides membership.

Metadata punctuation that is not part of the strict local alphabet acts as a
boundary. For example, a labelled certificate attribute emits the mailbox
after the label rather than the complete assignment.

### Opt-in `email_candidate`

`email_candidate` keeps the former RFC dot-atom alphabet, domain-label shape,
and SMTP limits. It emits only candidates that do not qualify as an identical
strict `email` value.

The pattern has `SelectedByAll: false`. `all` and the `pii` group do not select
it. Analysts can request the broad surface with:

```powershell
--lr "all,email_candidate"
```

The two matchers can produce different spans from the same surrounding text.
That is intentional when metadata punctuation separates a strict mailbox from
a broader RFC-shaped assignment.

### Snapshot maintenance

The IANA data is embedded in the managed executable. Updating it
requires a new reviewed source version, timestamp, SHA-256, count, tests, and
benchmark identity. Runtime downloads are prohibited.

A suffix removed from a later snapshot moves to `email_candidate`; it is not
silently treated as currently delegated. Historic root-zone reconstruction is
a separate feature.

## Non-negotiable invariants

1. `all` and `pii` select `email`, not `email_candidate`.
2. Direct, streaming, RAPIDS-verified, and enrichment paths use the same
   semantic decision.
3. The IANA decision is deterministic and makes no network request.
4. Snapshot identity is visible in source, tests, the benchmark, and this ADR.
5. Strict and broad validators cannot both accept the same complete candidate.
6. Every occurrence and its provenance remain in `findings.tsv`.
7. Counted distinct values remain in `feature-histogram.tsv`.

## Acceptance gates

The decision requires the correctness tests and the preregistered performance
benchmark below to pass before publication.

### Correctness

- Common public mailbox fixtures retain the exact value.
- Authored DLL, package, resource, random suffix, and one-letter suffix values
  produce zero strict matches.
- Broad fixtures remain available through `email_candidate`.
- Metadata assignments expose the strict mailbox substring.
- SMTP limits, domain labels, dot segments, and IANA matching have boundary
  and case tests.
- Catalog, direct, bounded streaming, RAPIDS verification, and enrichment
  matching remain consistent.

### Performance

The accepted v2 protocol requires one million rows, seven alternating
fresh-process pairs, and three workloads: all-valid, all-noise, and 50/50.

- valid strict output count and checksum equal the previous matcher;
- authored noise reduction is exactly 100%;
- median added time is at most 750 nanoseconds per candidate;
- no sample adds more than 1 microsecond per candidate; and
- maximum added managed allocation is at most 64 bytes per input record.

Relative timing is reported but is not the acceptance denominator. The check
runs only after the broad regex has found an email-shaped value. An absolute
per-candidate limit captures the added work without treating a sub-microsecond
baseline as a large user-visible regression.

## Measurements

An initial run used a compiled baseline against the production interpreted
candidate and was invalid. Its rows remain in
`email-precision-invalid-compiled-baseline-2026-08.csv` and are not acceptance
evidence.

The corrected relative-only v1 protocol failed as designed:

- valid median: +34.18%;
- valid maximum: +39.44%;
- absolute overhead range: 211 to 246 nanoseconds per candidate;
- maximum added allocation: 0.006192 bytes per record; and
- authored noise reduction: 100%.

Those rows remain in `email-precision-relative-gate-v1-2026-08.csv`.

The v2 absolute gate passed:

- valid median relative delta: +32.91%;
- valid maximum relative delta: +36.21%;
- median overhead: 228.7984 nanoseconds per candidate;
- maximum overhead: 249.1437 nanoseconds per candidate;
- maximum added allocation: 0.006192 bytes per record;
- exact common-address count and value checksum parity in all seven pairs; and
- 100% removal of the authored noise matches in all seven pairs.

Raw rows are in `email-precision-acceptance-v2-2026-08.csv`. The harness and
protocol are in `benchmarks/EmailPrecisionBenchmark`.

## Strongest detractor and resolution

The strongest objection is that a syntactically valid address with unusual
punctuation or a private, historic, or future suffix can be important evidence.
The objection is correct. The strict result is an investigator-focused default,
not a complete RFC mailbox parser or historic DNS oracle. The explicit
`email_candidate` partition preserves those candidates without restoring them
to every default findings table.

## Falsifiers and revisit triggers

Revisit or roll back the strict default if:

- common public addresses are lost in a reproducible corpus;
- noise survives because it uses a currently delegated TLD at a material rate;
- the IANA snapshot cannot be updated reproducibly;
- any authoritative matching path diverges;
- the broad partition becomes selected by default;
- a runtime network dependency appears; or
- a later accepted benchmark breaches the absolute or allocation gate.

A public, redistributable historic root-zone corpus or measured value from
internationalised email may justify a separate contextual pattern. It does not
justify silently broadening this default.

## Consequences

Users see fewer default email rows from carved resource strings. Common public
addresses keep the same pattern name and report shape. Uncommon punctuation,
private suffixes, historic suffixes, and future suffixes move to the explicit
`email_candidate` partition.

The executable grows by the embedded root-zone list and performs one bounded
offline lookup for each regex candidate. The accepted benchmark measures that
cost. Builds, air-gap runs, and evidence processing make no DNS or network
request. A future snapshot update changes matching semantics and therefore
requires the same review and measurement gates.

No output schema, report inventory, resume checkpoint, installer asset, or
case-data handling rule changes. Help and reporting documentation describe the
strict and broad partitions.

## Rollback

Rollback restores the former `email` regex and length validator, removes
`email_candidate` and the IANA snapshot, and restores previous help text. The
report schema and evidence files do not change, so rollback does not require a
data migration. Existing outputs remain self-describing through the recorded
pattern text, validation label, executable identity, and run metadata.

## Primary references

- [RFC 5322, Internet Message Format](https://www.rfc-editor.org/rfc/rfc5322.html#section-3.4.1)
- [RFC 5321, SMTP size limits](https://www.rfc-editor.org/rfc/rfc5321.html#section-4.5.3.1)
- [IANA root-zone database](https://www.iana.org/domains/root/db)
- [IANA versioned TLD text list](https://data.iana.org/TLD/tlds-alpha-by-domain.txt)
- [`bulk_extractor` 2.1.1 flex email scanner](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/scan_email.flex)
- [`bulk_extractor` 2.1.1 Lightgrep email scanner](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/scan_email_lg.cpp)
- [Command reference](../command-reference.md)

Repository artifacts: `benchmarks/EmailPrecisionBenchmark/README.md` contains
the benchmark protocol, and `bstrings/BuiltInPatternCatalog.cs` contains the
authoritative built-in definitions.
