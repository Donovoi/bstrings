# ADR-0009: Expand forensic patterns only with bounded context and local validation

- **Status:** Accepted
- **Date:** 2026-08-13
- **Scope:** built-in pattern coverage, candidate extraction, semantic validation,
  report provenance, fixtures, and performance acceptance
- **Decision type:** forensic coverage, evidence interpretation, privacy, provenance,
  and release acceptance
- **Review method:** three independent first-round reviews and rotated critiques
  under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** Primary-source Research/Standards Reviewer, Repository/Runtime Auditor,
  and Adversarial Precision Detractor
- **Owner:** bstrings maintainers

## Context

bstrings ships 66 built-in patterns for network artifacts, identifiers, payment
and identity data, credentials, browser and Registry artifacts, cryptocurrency
addresses, CVEs, private-key boundaries, and SHA-256-shaped values. The catalog
already uses deterministic semantic validation for checksums, structured data,
addresses, and encodings. Users need broader coverage for captured security,
identity, payment, private-information, forensic, and indicator data.

Broad secret-scanner regex collections are not suitable evidence authorities.
They often use undocumented provider lengths, generic entropy, or weak numeric
shapes; they can collect unrelated private data, time out on hostile input, and
turn a formatted candidate into a claim of ownership, validity, compromise, or
maliciousness. This decision therefore admits only classes with current primary
syntax, bounded matching, offline validation, and explicit interpretation limits.

The independent runtime review also reproduced three existing defects which
would make any expansion difficult to defend:

1. record creation validates a named output capture, while the boolean
   `IsMatch` path validates the complete labelled match. A valid labelled SIN or
   date of birth can therefore be accepted by one path and rejected by another;
2. the legacy root command can silently omit `--fr` patterns when `--lr` is also
   present; and
3. report ingestion can attach selected built-in metadata to a record whose
   stored expression differs from the selected expression.

The detractor also demonstrated that Luhn and MOD-97 establish checksum
plausibility, not issuance, and that the current benchmark corpus primarily
proves planted-hit recovery rather than population precision. The strongest
examples were all-zero Luhn values and a checksum-valid IBAN with an unassigned
country code. Existing reports intentionally retain exact evidence and context;
the addition of new private or reusable-secret defaults would therefore require
a separate raw-versus-masked output decision.

## Primary evidence

The accepted tranche is based on the following public primary sources, reviewed
on 2026-08-13:

- [NISTIR 7695](https://nvlpubs.nist.gov/nistpubs/Legacy/IR/nistir7695.pdf)
  defines the CPE 2.3 formatted binding and its eleven ordered attributes.
- [FIRST TLP 2.0](https://www.first.org/tlp/) defines the exact current written
  markings RED, AMBER, AMBER+STRICT, GREEN, and CLEAR.
- [RFC 5322 section 3.6.4](https://www.rfc-editor.org/rfc/rfc5322.html#section-3.6.4)
  defines labelled Message-ID, In-Reply-To, and References identifier fields.
- [GLEIF LEI CDF 3.1](https://www.gleif.org/content/4_lei-data/1_access-and-use-lei-data/2_level-1-data-lei-cdf-3-1-format/lei-cdf_version_3.1-documentation.html)
  defines the 20-character LEI field; ISO 17442 uses MOD 97-10 check digits.
- [CMS NPI check-digit requirements](https://www.cms.gov/Regulations-and-Guidance/Administrative-Simplification/NationalProvidentStand/Downloads/NPIcheckdigit.pdf)
  define the ten-digit NPI check using the `80840` prefix.
- [IRS IRM 3.13.5](https://www.irs.gov/irm/part3/irm_03-013-005) defines ITINs
  as 9xx identifiers with the published fourth/fifth-digit allocation ranges.
- [HMRC NIM39110](https://www.gov.uk/hmrc-internal-manuals/national-insurance-manual/nim39110)
  defines National Insurance number layout, invalid letters and invalid prefixes.
- [RFC 1321](https://www.rfc-editor.org/rfc/rfc1321.html) and
  [FIPS 180-4](https://csrc.nist.gov/pubs/fips/180-4/upd1/final) define MD5 and
  SHA digest widths. MD5 and SHA-1 are evidence correlation formats here, not
  approved integrity algorithms.

The review rejected or deferred bare Aadhaar, D-U-N-S, CUSIP, Australian TFN,
director ID, ABA routing, and unlabelled BIC patterns because public validation,
licensing, confidentiality, or base-rate evidence was insufficient. It also
deferred provider-token prefixes, ATT&CK membership, syslog frames, NHS numbers,
and decoded private-key bodies until bounded versioning, sensitive-output, and
representative precision evidence exist.

## Considered options

1. **Import a large public secret/PII regex collection.** Rejected. Collection
   size is not evidence of current syntax, safety, precision, or forensic meaning.
2. **Add generic entropy, password, token, name, and attribution patterns.**
   Rejected. These create unbounded false positives and cannot establish identity
   or actor attribution from captured strings.
3. **Add provider prefixes without stable suffix bounds.** Rejected. Provider
   formats drift and unlimited captures can exfiltrate very large evidence spans.
4. **Create a disabled candidate-pack architecture first.** Credible and deferred.
   The current `all` selector includes the full built-in catalog; a stable versus
   candidate selection policy needs a separate command/provenance decision.
5. **Fix the common correctness invariants and add a small, bounded tranche.**
   Accepted. This provides useful coverage without claiming that a checksum or
   shape proves issuance, activity, ownership, authenticity, or maliciousness.

## Decision

First fix the common matching and reporting invariants:

- one candidate-range enumerator selects a built-in's named output group and
  applies semantic validation for both record and boolean paths;
- the legacy root resolver executes named `--lr` and file `--fr` expressions
  together using the same nonblank/comment-line rules as analysis; and
- forensic report ingestion rejects an unselected pattern name, expression
  mismatch, invalid range, or match-length mismatch instead of assigning
  authoritative built-in metadata to it.

Then add the following eleven bounded built-ins:

| Name | Evidence class | Required validation and interpretation |
|---|---|---|
| `cpe23` | security product identifier | exactly eleven CPE 2.3 components, bounded escapes and component sizes; product-class lead only |
| `tlp_marking` | handling marking | exact TLP 2.0 enum; never changes access controls automatically |
| `email_message_id` | communication identifier | RFC-labelled angle identifier; correlation lead, not sender authentication |
| `lei` | legal-entity identifier | 20 uppercase alphanumerics with MOD 97-10; candidate, not issuance/status proof |
| `npi` | provider identifier | labelled ten-digit value with `80840`-prefixed Luhn; candidate, not credential or enrollment proof |
| `itin` | private identity candidate | labelled IRS-published range; candidate, not allocation proof |
| `uk_nino` | private identity candidate | labelled HMRC syntax and invalid-prefix rules; candidate, not allocation proof |
| `md5_labelled` | hash correlation value | exact label and 32 hex characters; not an integrity recommendation |
| `sha1_labelled` | hash correlation value | exact label and 40 hex characters; not an integrity recommendation |
| `sha384_labelled` | hash correlation value | exact label and 96 hex characters |
| `sha512_labelled` | hash correlation value | exact label and 128 hex characters |

Labels are part of the confidence boundary for the numeric identity and digest
families, but only the named value is emitted as the match. No network registry,
provider, revocation, or authentication request is made. All fixtures are
synthetic, deliberately unissued, reserved, or generated locally and may not
contain case data or usable secrets.

The new identity candidates join the existing `pii` selection group. The
remaining new classes are individually selectable and included by `all`, as
every existing stable built-in is. Report categories distinguish handling
markings, hashes, communication identifiers, product identifiers, legal entities,
providers, and PII; they do not relabel a format hit as compromise or attribution.

Exact raw values remain in the restricted forensic evidence reports, consistent
with the existing SSN, card, IBAN, credential, and token contract. Documentation
must continue to warn that findings and contexts are sensitive evidence. This
decision adds no reusable-secret or full private-key body pattern. Masked analyst
views remain a separate future decision; silently replacing raw evidence would
break current evidentiary semantics.

The generator is revised so every pattern has multiple adversarial witnesses
where practical, including boundary, Unicode-adjacent, off-by-one, checksum,
label, separator, and long-near-miss cases. The checked benchmark manifest binds
the generator version, pattern expression, options, witnesses, expected offsets,
and input SHA-256.

## Review synthesis and unresolved disagreement

The standards reviewer proposed seventeen source-backed candidates. The runtime
reviewer agreed that bounded hashes, identifiers, and contextual artifacts are
feasible but found the path and report defects above. The detractor rejected
blanket promotion and required per-class bounds, no automatic external checks,
overlap awareness, hostile inputs, and privacy-safe fixtures.

Rotated critiques converged on fixing candidate-range and report identity first.
They disagreed on whether all new patterns require a new shadow-pack architecture
and whether universal false-positive-per-GiB thresholds are meaningful. The
synthesis does not create an unevidenced shadow status and does not use a single
universal precision number. Instead, it admits only contextual or locally
validated bounded patterns, requires zero unexpected hits on the frozen
high-confusion corpus, measures each class separately, and defers provider
secrets and ambiguous attribution classes.

## Non-negotiable invariants

1. The same input, expression, output group, and validator produce the same
   candidate set in boolean, record, streaming, enrichment, retry, and final-CPU
   validation paths.
2. A report never assigns built-in metadata to an unknown name or a different
   expression, and invalid match offsets/lengths fail closed.
3. `--lr` and `--fr` are additive in legacy, analysis, and enrichment commands;
   blank and comment lines never become accidental expressions.
4. No pattern performs a provider or registry network lookup.
5. A checksum or syntax result is described only as a candidate or correlation
   value, never proof of issuance, activity, ownership, compromise, or attribution.
6. New matches are bounded; long suffixes, delimiter storms, malformed escapes,
   and semantic-rejection floods cannot produce unbounded captures or timeouts.
7. Raw evidence and raw offsets are preserved without Unicode normalization.
8. Synthetic fixtures contain no private case material, live personal identifier,
   usable credential, reusable key, host-specific path, or case-derived hash.
9. Existing pattern names, expressions, default selection, and output ordering
   remain compatible except for separately tested correctness fixes.

## Acceptance gates

- Focused catalog, semantic-validator, report, legacy resolver, streaming,
  enrichment, and search-path tests pass.
- Every built-in with an output group has exact boolean-versus-record parity.
- A mixed root invocation with one `--lr` pattern and a synthetic `--fr` file
  returns both classes without duplicates on CPU and applicable accelerated paths.
- Tampered expression, unknown pattern name, negative range, and match-length
  mismatch records are rejected by report generation.
- Each new class accepts authoritative synthetic vectors and rejects final-symbol,
  length, boundary, label, Unicode-adjacent, and format mutations.
- CPE escaping/component-count/size, TLP enum, Message-ID header context,
  LEI MOD-97, NPI Luhn, ITIN ranges, NINO invalid prefixes, and digest label/width
  receive direct unit tests.
- ASCII and UTF-16LE boundary/EOF corpus output has exact expected value/offset
  multisets and no unexpected result.
- Dense and adversarial maximum-record inputs complete with zero regex timeout or
  incomplete output.
- On seven alternating runs of the authoritative catalog corpus, median elapsed
  throughput and peak working set may regress by at most 5% versus the exact base
  commit, while all expected pairs and output rows remain exact. The measured
  input and runner commands are recorded with the candidate evidence.
- The complete .NET suite, formatting, decision-record gates, Markdown-link
  checks, air-gap documentation inventory, and privacy/secret scans pass.
- The outgoing diff and post-merge commit contain no case strings, extracted
  values, local evidence paths, private aggregate counts, or credentials.

## Strongest detractor and resolution

Even a perfectly formatted NPI, ITIN, NINO, LEI, digest, CPE name, TLP marking,
or Message-ID can be synthetic, documentary, stale, spoofed, or unrelated to the
examined conduct. Labelled hashes can be benign build metadata; Message-IDs can
be forged; TLP text can be quoted; checksum-valid identifiers can be unissued.

This objection survives. The implementation returns bounded evidence candidates
and correlation leads, preserves their original location and context, and makes
no external validity or attribution claim. If representative evidence shows an
unacceptable class-specific background rate, that class is removed from `all`
or reverted pending a separately reviewed candidate-pack policy.

## Falsifiers and revisit triggers

Revert an individual new pattern if any of the following occurs:

- a valid primary-source vector is missed or a mandatory near-miss is accepted;
- a match depends on execution path, chunk size, input encoding, current culture,
  local time, network availability, or dictionary iteration order;
- a hostile bounded input causes a timeout, incomplete output, or more than the
  accepted performance/memory regression;
- the provider/standards source changes so the implemented syntax is no longer
  current;
- reports or fixtures expose private case data or a usable secret; or
- downstream metadata describes syntax as issuance, compromise, maliciousness,
  or attribution.

If common path or report identity parity fails, revert the catalog expansion as a
unit and retain only independently proven correctness fixes. The previous 66
names and explicit selectors remain the rollback surface; no output migration or
provider interaction is required.

## Consequences

Users gain bounded, source-backed coverage for product identifiers, handling
markings, email correlation, business and provider identifiers, two additional
jurisdictional identity formats, and labelled legacy/long digest values. The
change also closes pre-existing path and report-provenance defects.

The catalog becomes larger and `all` performs additional bounded checks. Exact
evidence reports remain sensitive. Provider secrets, full keys, broad attribution,
and ambiguous numeric identifiers remain deliberately deferred rather than being
advertised as supported on shape alone.

## Primary references

- [NIST CPE 2.3 formatted-string binding](https://nvlpubs.nist.gov/nistpubs/Legacy/IR/nistir7695.pdf)
- [FIRST Traffic Light Protocol 2.0](https://www.first.org/tlp/)
- [RFC 5322 Message-ID fields](https://www.rfc-editor.org/rfc/rfc5322.html#section-3.6.4)
- [GLEIF LEI CDF 3.1](https://www.gleif.org/content/4_lei-data/1_access-and-use-lei-data/2_level-1-data-lei-cdf-3-1-format/lei-cdf_version_3.1-documentation.html)
- [CMS NPI check digit](https://www.cms.gov/Regulations-and-Guidance/Administrative-Simplification/NationalProvidentStand/Downloads/NPIcheckdigit.pdf)
- [IRS ITIN ranges](https://www.irs.gov/irm/part3/irm_03-013-005)
- [HMRC National Insurance number format](https://www.gov.uk/hmrc-internal-manuals/national-insurance-manual/nim39110)
- [Repository pattern-validity record](../pattern-validity-review-2026-08.md)
- Catalog authority: `bstrings/BuiltInPatternCatalog.cs`.
- Semantic-validator authority: `bstrings/BuiltInSemanticValidator.cs`.
