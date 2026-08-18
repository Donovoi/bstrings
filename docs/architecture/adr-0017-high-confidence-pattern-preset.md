# ADR-0017: Use a lower-noise default pattern preset

- **Status:** Accepted
- **Date:** 2026-08-18
- **Scope:** built-in pattern defaults, candidate partitions, pattern groups, matching parity, help, and report guidance
- **Owner:** bstrings maintainers
- **Decision type:** forensic coverage and default pattern policy
- **Review method:** three independent first-round evidence tracks and rotated critiques under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source and external-practice research; repository and runtime audit; adversarial detractor and benchmark design
- **Implementation state:** implemented and validated for v3.0.0; published v2 releases remain unchanged

## Context

The built-in catalog previously contained 79 patterns. The `all` selector used
77 of them. Some default patterns established only a broad textual shape. For
example, any five digits could be called a ZIP code, any unseparated 12-digit
hexadecimal value could be called a MAC address, and any Base58 value that
decoded to 32 bytes could be called a Solana address.

These forms can be useful leads. They are not equally useful in the default
findings table. A raw memory image contains identifiers, code, resource text,
random values, and fragments that share these shapes.

No deterministic offline matcher can guarantee zero false positives. This
decision instead makes `all` a lower-noise preset. "High-confidence" in this
decision is a relative preset name. It does not mean that all 74 definitions
perform semantic validation or that a result proves identity or ownership.
Wider forms remain available through exact pattern names, domain groups, or a
new `candidates` group.

The existing `feature-histogram.tsv` already gives one row for each distinct
pattern and match value, with an occurrence count. `findings.tsv` keeps each
occurrence and its provenance. This decision does not add another report.

## Pre-registered claims and falsifiers

The review tested these claims:

1. Several default patterns have a clear stricter partition that removes
   common ambiguity while retaining useful wider coverage explicitly.
2. ZIP, Solana, and Move address shapes are too broad for the generic `all`
   preset but remain useful when explicitly requested.
3. The MAC and IPv6 strict/candidate partitions can be disjoint while their
   union preserves the former complete-value matcher.
4. Direct CPU, bounded streaming, RAPIDS verification, and enrichment matching
   can keep one authoritative decision.
5. The change can reduce authored confusion matches without a material default
   performance regression.

The decision is falsified if a former MAC or IPv6 complete value is available
through neither partition; a strict and candidate partition emit the same
complete value; `all` selects a candidate-only pattern; an
authoritative matching path diverges; explicit `pii`, `registry`, or `wallets` group
coverage changes; or the paired benchmark exceeds the performance gate.

## Independent first-round reviews

### External practice and primary sources

The external review found that mature tools combine syntax, checksums,
context, filters, and counted views. The pinned `bulk_extractor` scanners use
context and special-case suppression for phone and account patterns. They keep
histograms and separate stop-list behavior rather than claiming that every
shape is an identity.

Microsoft Presidio combines regexes, context, and checksums, and explicitly
states that automated detection cannot guarantee correctness. Yelp
`detect-secrets` combines detector plugins, filters, allowlists, and baselines,
and describes heuristic findings as material that still needs review.

The standards sources also show where deterministic validation is possible.
NHTSA documents the modern VIN check digit, SWIFT defines IBAN validation, and
EIP-55 defines an Ethereum checksum. Those checks do not cover every historic,
unlabelled, or deliberately lower-case value in forensic data. The review
therefore rejected a single global rule that deletes every non-checksummed
form.

### Repository and runtime audit

The audit found 79 definitions, 77 selected by `all`, 48 semantic validators,
and 31 regex-only patterns. Only `email_candidate` and `b64_candidate` were
opt-in.

Direct CPU matching, bounded streaming, RAPIDS-prefiltered matching, and
enrichment matching converge on `RegexOutputCore` and
`BuiltInSemanticValidator`. RAPIDS remains a superset prefilter; managed code
makes the authoritative decision.

The audit identified the clearest default ambiguity in `zip`, `mac`,
`reg_path`, exact `::`, `solana`, and `move_address`. It also found broad
patterns such as phones, SSNs, cards, VINs, Ethereum addresses, SHA-256 values,
and assignments. Narrowing those without a representative corpus could remove
valid standalone evidence.

### Adversarial detractor and benchmark design

The detractor challenged the phrase "no false positives." A ZIP-shaped value,
an unchecked address, or a registry-root word can be relevant even when the
default confidence is low. It also showed that one global context or checksum
rule would create false negatives.

The detractor initially recommended changing only registry paths and wording.
It considered the MAC split useful but asked for measurement before changing
the default. It rejected silent narrowing of phones, cards, VINs, hashes,
SSNs, IBANs, and cryptocurrency addresses.

The benchmark design therefore measures only the accepted reversible changes.
It uses authored synthetic valid, confusion, and mixed workloads; alternating
fresh-process order; seven pairs; exact strict-plus-candidate coverage; and a
fixed default performance gate.

## Rotated critiques

The runtime track challenged a proposal to make every broad pattern opt-in.
`var_set`, phones, cards, VINs, Ethereum addresses, and hashes can be strong
forensic leads in their existing unlabelled forms. The synthesis keeps those
defaults and corrects descriptions where the pattern establishes only a
candidate shape.

The external track challenged removing bare `::` from IPv6 coverage. RFC 4291
defines it as the valid unspecified address. The synthesis keeps it under
`ipv6_candidate`, because the exact two-character token has very little
default context.

The detractor challenged moving Solana and Move address shapes out of `all`.
The objection is valid for cryptocurrency investigations. The synthesis keeps
both exact names and the `wallets` group unchanged, while removing them only
from the generic preset.

The runtime and detractor tracks both challenged a fifth unique-findings
report. `feature-histogram.tsv` already provides the distinct counted view, so
no new report is added.

The remaining disagreement is recorded: the detractor preferred to defer more
default changes until a public corpus exists. The accepted candidate
partitions are reversible, preserve former coverage, and have explicit parity
and benchmark gates. Broader irreversible narrowing remains deferred.

## Considered options

1. **Keep every former default.** Rejected because the generic preset remains
   dominated by several low-context shapes.
2. **Delete the broad forms.** Rejected because they can be useful evidence.
3. **Require online registry, chain, DNS, or identity lookups.** Rejected
   because results are mutable, unavailable offline, and not proof of historic
   ownership.
4. **Apply every available checksum.** Rejected as a global rule because it
   removes historic, unlabelled, deliberately unchecked, or partial evidence.
5. **Use strict defaults plus explicit candidate partitions and groups.**
   Accepted.

## Decision

The catalog contains 82 patterns. The `all` selector uses 74 lower-noise
defaults. The `candidates` group contains all eight definitions that are not
selected by `all`.

### Partition former coverage

- `mac` accepts colon-separated or hyphen-separated six-octet addresses.
  `mac_candidate` accepts the former unseparated 12-hex form.
- `reg_path` requires a supported HKEY or HK prefix followed by `SAM`,
  `SECURITY`, `SOFTWARE`, or `SYSTEM`, or one of those four names followed by
  a subpath. `reg_path_candidate` accepts only standalone bare root words.
- `ipv6` keeps the former IPv6 coverage except exact `::`.
  `ipv6_candidate` accepts exact `::`.

Each strict and candidate pair is disjoint. The MAC and IPv6 unions equal
their former accepted complete-value sets. Registry-root substrings adjacent
to a backslash in a generic non-registry path are intentionally removed. For
example, neither partition reports `SOFTWARE` inside
`C:\artifact\SOFTWARE\Vendor`. Restoring that prefix/suffix match would restore
the reported path noise.

### Remove broad shapes from the generic preset

`zip`, `solana`, and `move_address` set `SelectedByAll` to false. Their exact
pattern names remain available. The `pii` group still includes `zip`. The
`registry` group includes `reg_path_candidate`. The `wallets` group still
includes `solana` and `move_address`.

### Keep other patterns and describe their limits

`var_set` remains in `all`. Its description states that it finds whole-record
assignment candidates accepted by Windows environment-variable syntax. It
does not claim that the value came from a live environment block.

Phone, SSN, card, VIN, Ethereum, hash, IBAN, and other existing definitions
remain unchanged in this decision. Their outputs are pattern matches, not
claims of current assignment, ownership, delivery, or account existence.

### Candidate selection

The `candidates` group is derived from catalog metadata. It contains every
definition where `SelectedByAll` is false. Users can combine the complete
lower-noise preset and the wider surface with:

```powershell
--lr "all,candidates"
```

Exact names and existing domain groups remain supported.

## Non-negotiable invariants

1. `all` selects exactly the catalog definitions marked `SelectedByAll`.
2. `candidates` selects exactly the remaining definitions.
3. `all,candidates` selects every catalog definition exactly once.
4. Each new strict/candidate pair is disjoint. MAC and IPv6 preserve their
   former complete-value unions. Registry paths enforce the documented
   context boundary.
5. `pii`, `registry`, and `wallets` keep their explicit domain coverage.
6. Direct, streaming, RAPIDS-verified, and enrichment paths use the same
   semantic decision.
7. Every occurrence remains in `findings.tsv`; counted distinct values remain
   in `feature-histogram.tsv`.
8. No validator performs network I/O or live registry, chain, DNS, or identity
   lookup.

## Acceptance gates

### Correctness

- Catalog tests prove the 82 total, 74 default, and eight candidate counts.
- `all,candidates` equals the complete catalog without duplicates.
- MAC and IPv6 fixtures prove disjoint union parity with the former matcher.
- Registry fixtures prove the strict path boundary, the standalone candidate
  boundary, and the intentional generic-path exclusion.
- Authored confusion fixtures produce no strict default match and remain
  available through the candidate name where coverage was partitioned.
- ZIP, Solana, and Move fixtures are absent from `all` but present through
  their exact name and domain group.
- CPU, bounded streaming, RAPIDS verification, and enrichment outputs agree.
- Cache-on and cache-off output values, order, identities, and provenance
  agree.
- Resume with an older executable or pattern contract refuses before output
  mutation; committed pattern and report stages remain whole-stage artifacts.

### Performance

The preregistered benchmark uses authored synthetic data only. It runs valid,
confusion, and 50/50 mixed workloads with at least 100,000 records. Here,
"confusion" means authored default-ambiguity values; it is not a labelled
real-world false-positive-rate measurement. The benchmark uses seven
alternating fresh-process baseline/candidate pairs after warm-up.

- former strict-plus-candidate coverage and checksums are exact;
- authored confusion reduction for the accepted default changes is exact;
- median default wall-time regression is at most 5%;
- no individual workload median exceeds 5%;
- peak working-set regression is at most 5%; and
- output cardinality growth is reported separately from matching overhead.

The gate is not acceptance evidence until raw rows, the immutable workload
identity, and the harness are checked in.

## Measurements

The accepted run used 100,000 authored synthetic records, seven alternating
fresh-process pairs, and all three preregistered workloads. Every cardinality
and valid-value checksum check passed.

| Workload | Median wall delta | Median peak working-set delta | Default reduction |
| --- | ---: | ---: | ---: |
| Valid | +3.48% | -3.23% | 0% |
| Confusion | -2.65% | -5.11% | 100% |
| Mixed | -6.97% | -4.79% | 50% |

The baseline selected 77 patterns. The candidate selected 74. Median managed
allocation also fell in every workload. The benchmark gate passed.

Raw rows are in
`benchmarks/results/catalog-precision-acceptance-2026-08.csv` with SHA-256
`9bb25ac6e2371a2d88f8d042f4a7a493b171657a317f1aed6b4f623cbde21c04`.
The harness and immutable authored workload are in
`benchmarks/CatalogPrecisionBenchmark`.

## Strongest detractor and resolution

The strongest objection is that a standalone five-digit value, bare registry
root, unchecked cryptocurrency address, or unseparated MAC can be meaningful
forensic evidence. The objection is correct. The decision changes only the
generic preset. Exact names, candidate partitions, and domain groups keep the
wider surface available.

The second objection is that a lower-noise name may imply certainty. It does
not. The preset reduces common ambiguity; it does not verify ownership,
assignment, historic existence, or intent.

## Falsifiers and revisit triggers

Revisit or roll back a default change if:

- a former MAC or IPv6 complete value is available through neither strict nor
  candidate coverage; the documented generic registry-path removal is excluded;
- a strict/candidate pair overlaps;
- a domain group loses its documented coverage;
- a representative public corpus shows material loss of useful default
  evidence;
- any authoritative matching path diverges;
- the candidates group becomes selected implicitly;
- a validator gains a mutable runtime dependency; or
- the accepted benchmark exceeds a correctness or performance gate.

A redistributable labelled corpus may support later changes to phone, VIN,
card, SSN, IBAN, Ethereum, hash, or assignment patterns. Such a change requires
a separate measured decision.

## Consequences

Default findings contain fewer low-context values. Wider coverage remains one
explicit selector away. Pattern and report schemas do not change. Existing
outputs remain readable, while executable and pattern identities prevent an
old committed matching stage from being silently reused under the new rules.

Help and reporting documentation describe `all` as the lower-noise preset
and `candidates` as the wider candidate surface. The documentation describes
what each pattern establishes without telling investigators how to conduct an
investigation.

## Release boundary

The published v2.1.2 release is immutable. This decision first ships in
v3.0.0 with a new tag and asset set. Release review retained the exact v2.1.1
source boundary for translation-off resume and ported only its verified target
to v3.0.0. ADR-0013 records that bounded compatibility decision.

## Rollback

Rollback restores the former MAC, registry-path, and IPv6 definitions; removes
their candidate definitions and the `candidates` group; and restores ZIP,
Solana, and Move address selection by `all`. Report schemas and evidence files
do not require migration.

## Primary references

- [bulk_extractor 2.1.1 account scanner](https://raw.githubusercontent.com/simsong/bulk_extractor/b2279ce/src/scan_accts.flex)
- [bulk_extractor feature histograms and stop lists](https://github.com/simsong/bulk_extractor/wiki)
- [Microsoft Presidio](https://microsoft.github.io/presidio/)
- [Yelp detect-secrets design](https://raw.githubusercontent.com/Yelp/detect-secrets/master/docs/design.md)
- [RFC 4291, IPv6 addressing architecture](https://www.rfc-editor.org/rfc/rfc4291)
- [IEEE MAC address tutorial](https://standards.ieee.org/wp-content/uploads/import/documents/tutorials/macgrp.pdf)
- [NHTSA VIN validation](https://www.nhtsa.gov/importing-vehicle/importation-and-certification-faqs-8)
- [SWIFT IBAN registry](https://www.swift.com/standards/data-standards/iban-international-bank-account-number)
- [EIP-55 mixed-case checksum](https://eips.ethereum.org/EIPS/eip-55)
- [Solana account model](https://solana.com/docs/core/accounts)
- [Command reference](../command-reference.md)

Repository artifacts: `bstrings/BuiltInPatternCatalog.cs` contains the
authoritative definitions, `bstrings.Tests/CatalogPrecisionPolicyTests.cs`
contains the policy tests, and `benchmarks/CatalogPrecisionBenchmark` contains
the accepted benchmark protocol and harness.
