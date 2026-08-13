# ADR-0010: Publish bounded reversible-decoding children before considering recursive decoding

- **Status:** Accepted
- **Date:** 2026-08-13
- **Scope:** encoded-string discovery, reversible decoding, derived evidence,
  provenance, matching, reports, resource limits, CLI selection, and release acceptance
- **Decision type:** forensic coverage, evidence preservation, pipeline ordering,
  failure behavior, privacy, and performance acceptance
- **Review method:** three independent first-round reviews and rotated critiques
  under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** Primary-source External-Practice Reviewer,
  Repository/Runtime Auditor, and Adversarial Decoder Detractor
- **Owner:** bstrings maintainers

## Context

bstrings currently detects canonical Base64-shaped values, but it does not
publish their decoded bytes or decoded text. A longer Base64 value may be
decoded transiently by the translation-worthiness shadow router to confirm
shape, and JWT segments may be decoded transiently for structural validation,
but those bytes are discarded. Entropy is not calculated in the production
pipeline. Consequently, a pattern hit means "Base64-shaped evidence," not
"meaningful decoded evidence."

The distinction matters at raw-memory scale. The current broad pattern can find
millions of candidates in a large dump. RFC 4648 Base64 represents arbitrary
octets, so valid syntax does not imply text, usefulness, correctness of a
higher-level interpretation, or benign/malicious meaning. Decoding every
matching substring would multiply records and sensitive output without a
defensible base-rate or resource bound.

At the same time, refusing all decoding loses real evidence. A complete
canonical Base64 record can contain a URL, command, JSON object, credential
label, IOC, or multilingual text. A PowerShell command line containing the
documented `-EncodedCommand` switch has stronger context: its token is Base64
over UTF-16LE. A bounded decoder can recover those strings while preserving the
encoded parent.

## Primary evidence and external practice

The reviews inspected the following primary sources on 2026-08-13:

- [RFC 4648](https://www.rfc-editor.org/rfc/rfc4648.html) defines Base64
  alphabets, padding, non-alphabet handling, and canonical zero pad bits. It
  defines a reversible byte representation, not a semantic-validity or entropy
  test.
- [bulk_extractor's Base64 scanner](https://github.com/simsong/bulk_extractor/blob/main/src/scan_base64.cpp)
  uses a conservative MIME-like heuristic: long constant-width lines, multiple
  lines, alphabet/class checks, then a decoded byte buffer tagged `BASE64` is
  recursively scanned. It explicitly does not cover short blobs.
- [bulk_extractor's architecture](https://arxiv.org/abs/2208.01639) and
  [scanner core](https://github.com/simsong/bulk_extractor/blob/main/src/be20_api/scanner_set.cpp)
  preserve transform paths, bound recursion, and avoid repeat processing. This
  supports child provenance and resource controls, but does not establish a safe
  false-positive rate for arbitrary extracted strings.
- [CyberChef Magic](https://github.com/gchq/CyberChef/wiki/Automatic-detection-of-encoded-data-using-CyberChef-Magic),
  its [implementation](https://raw.githubusercontent.com/gchq/CyberChef/master/src/core/lib/Magic.mjs),
  and [operation UX](https://raw.githubusercontent.com/gchq/CyberChef/master/src/core/operations/Magic.mjs)
  speculatively execute whole-input recipes to a bounded depth, then combine
  UTF-8 validity, file magic, language-frequency evidence, further matching
  operations, usefulness, and entropy. Entropy is one ranking term; high
  entropy may correctly indicate compressed or encrypted data.
- CyberChef's [From Base64](https://raw.githubusercontent.com/gchq/CyberChef/master/src/core/operations/FromBase64.mjs)
  is intentionally convenient and manual: its ordinary defaults can remove
  non-alphabet characters and leave strict mode off. Those defaults are not
  suitable for unattended forensic publication.
- Microsoft documents that PowerShell
  [`-EncodedCommand`](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_pwsh?view=powershell-7.6)
  is Base64 over UTF-16LE. The
  [Velociraptor artifact](https://docs.velociraptor.app/artifact_references/pages/server.powershell.encodedcommand/)
  first identifies the switch, then decodes its token. Context is therefore a
  stronger rule than applying a PowerShell interpretation to every Base64 value.
- [RFC 3986](https://www.rfc-editor.org/rfc/rfc3986.html) requires component-aware
  percent decoding and warns against repeated decoding. Generic `%HH` handling
  is therefore not part of this release.
- [YARA's Base64 modifier](https://yara.readthedocs.io/en/stable/writingrules.html#base64-strings)
  searches alignment variants of known encoded needles and warns that short
  Base64 strings are ambiguous. Encoded-pattern search can complement, but does
  not replace, bounded child decoding.
- [MITRE CWE-409](https://cwe.mitre.org/data/definitions/409.html) documents
  resource amplification from highly compressed data. Decompression needs its
  own byte-oriented design and is not treated as merely another text encoding.

## Considered options

1. **Decode every Base64 pattern match and use entropy to keep plausible text.**
   Rejected. Canonical syntax applies to arbitrary bytes, short substrings have
   a high background rate, and entropy cannot distinguish bad decoding from
   valid compressed, encrypted, key, token, or compact multilingual content.
2. **Copy CyberChef Magic, including recursive speculative recipes.** Rejected
   for the first release. CyberChef is an analyst-selected interactive workflow;
   bstrings processes millions of unattended records and must prove deterministic
   cardinality, atomic publication, evidence identity, and global resource caps.
3. **Copy bulk_extractor's recursive kitchen-sink model.** Deferred. Its byte
   buffers, MIME-like discovery, carving scanners, and recursive forensic paths
   are a strong long-term reference, but bstrings currently operates on
   normalized extracted-string records rather than arbitrary byte children.
4. **Publish only strict text decoded from whole-record canonical Base64.** Safe
   but incomplete. It misses a high-value contextual PowerShell form and must
   not imply that decoded binary was invalid.
5. **Add a bounded Base64-first transform with contextual PowerShell support,
   explicit binary outcomes, additive matching, and no recursive expansion.**
   Accepted as a reversible first release.

## Decision

Add an independently selectable decoding transform:

```text
--decode off|auto|force
```

The ordinary default remains `off`. The implementation initially proposed that
`--full` select `auto`, with the existing repeatable/comma-separated `-e decode`
or `--exclude-engine decode` as subtraction controls. The preregistered
performance gate failed before publication, so the defined rollback was
applied: Full also leaves decoding off. Users explicitly opt in with
`--decode auto` or `--decode force`, including alongside `--full`. Direct
`--decode` and exclusion of `decode` remain mutually exclusive so the selection
grammar stays unambiguous.

The first release has two profiles, evaluated in this order, and emits at most
one decoded text child per raw parent:

1. **PowerShell EncodedCommand v1.** The complete parent must parse as one
   bounded `powershell`, `powershell.exe`, `pwsh`, or `pwsh.exe` command line
   containing exactly one full `-EncodedCommand` switch and exactly one following
   token. The token must be canonical standard Base64. Decoded bytes must be an
   even-length, strictly valid UTF-16LE sequence whose exact re-encoding matches
   the bytes. The command is never executed or normalized.
2. **RFC 4648 Base64 text v1.** `auto` considers only a complete record after
   removing explicitly defined outer ASCII whitespace. It requires at least 24
   encoded characters, length divisible by four, the standard alphabet, legal
   terminal padding, zero unused pad bits, a successful decode, and exact
   ordinal re-encoding. To reduce ordinary-word collisions, `auto` also requires
   at least one digit, `+`, `/`, or `=`. `force` removes only that ambiguity
   guard and lowers the minimum to the existing eight-character pattern floor;
   it does not relax canonicality or resource limits.

Decoded text publication is lossless. Supported encodings are strict UTF-8,
UTF-8 with BOM, and UTF-16LE/BE with an explicit BOM. The PowerShell profile is
the sole BOM-less UTF-16LE exception because Microsoft defines that context.
Replacement characters are never introduced. A text child must contain at
least four visible Unicode scalar values, no NUL, and no disallowed control
characters. A successful byte decode which is known binary, opaque binary, or
unsupported text receives a bounded assessment outcome but no fabricated string
child. The encoded parent is always retained.

Every text child is a schema-1 string record with:

- the exact parent `sourceFile`, `location`, and `origin` lineage;
- one existing `parentRecordId`;
- `transform.kind = decoding`, engine/version, profile, policy version, and
  `decoded-text` outcome;
- exact candidate start/length and outer-whitespace treatment;
- decoded byte length and SHA-256, selected charset, depth, and effective limits;
  and
- a deterministic child ID binding the parent, covered span, decoder/profile,
  policy, charset, and exact child text.

The decoder reads only `raw-strings.jsonl`. It never reads its own output, so
depth is structurally fixed at one. It atomically writes:

- `decoded-strings.jsonl` for published text children;
- `decoder-assessments.jsonl` for bounded attempted occurrences; and
- `decoder-work-stats.json` for schema-bound aggregate work and cardinality.

The stable final merge is `raw`, then existing translations, then decoded
children. This keeps every existing row and match as a byte-identical prefix;
decoded findings are additive. Decoded children re-enter the existing pattern
matcher and reports, but not language triage or translation in this release.
There is no translation bypass and no claim of translation-cost savings.

The default automatic policy limits each encoded candidate to 16,384
characters, each decoded payload to 12,288 bytes, attempted candidates to
100,000, and total decoded work to 64 MiB. Advanced bounded CLI overrides may
raise those limits up to existing normalized-record safety ceilings; all
effective limits are recorded. `force` never bypasses them. There is no decoded
content cache, persistent or otherwise.

Entropy is not a validity, publication, usefulness, bypass, resource, cache, or
recursion signal. The first runtime schema does not publish per-record entropy.
Research benchmarks may stratify public or synthetic fixtures by entropy to
ensure low- and high-entropy coverage. This avoids turning a weak descriptive
feature into a future evidence authority by accident.

Base64URL is accepted only by the existing JWT/JWS structural validator; no
generic Base64URL child is emitted in this release. Generic hex, Base32,
percent-decoding, MIME/PEM folding, compression, archive traversal, XOR,
recursive decoding, and byte-child carving are deferred. Recognized binary
signatures may be reported as bounded outcome classes, never decompressed.

## Review synthesis and unresolved disagreement

The external-practice reviewer initially proposed Base64, Base64URL, contextual
PowerShell, hex, percent, and header-gated compression with bounded recursion.
The runtime reviewer found that the current record validator accepts only
translation children and mapped the required transform, merge, provenance,
report, option, cancellation, and cardinality changes. The detractor showed
that millions of broad matches, recursive fan-out, decompression bombs,
sensitive caches, and entropy misuse make the broad proposal unsafe.

Rotated critiques converged on canonical round-trip, contextual PowerShell,
one child per parent, no cache, parent preservation, additive matching, and
deferred authority. They disagreed on whether a decoded binary payload must be
first-class immediately. bulk_extractor and CyberChef continue through binary
formats; the first bstrings release records a truthful binary assessment but
does not yet publish an arbitrary-byte artifact. The schema and terminology must
therefore call this release "bounded Base64 text-child decoding," not a complete
recursive decoder. A later byte-child ADR must solve carving, storage,
decompression, and recursive provenance before expanding that claim.

They also disagreed about runtime entropy metadata. The accepted decision keeps
entropy out of evidence and decisions until a public/synthetic benchmark proves
an analyst value that outweighs fingerprinting, output, reproducibility, and
future-misuse risks.

## Non-negotiable invariants

1. Decoder-off output is byte-identical to the pre-decision pipeline.
2. Decoder-on preserves every raw and translation record, order, match, and
   histogram contribution; decoded evidence is additive.
3. Every child references exactly one earlier raw parent and retains its exact
   source, location, and origin lineage.
4. Canonicality uses a named profile, legal alphabet/padding/pad bits, exact
   decode, and exact re-encoding. No arbitrary ignored characters or replacement
   text are permitted.
5. One raw parent produces at most one text child; decode depth is exactly one.
6. Parent identity, covered span, transform policy, bytes, charset, and child
   identity reconcile with assessments and aggregate stats.
7. Limit, invalid, binary, cancellation, and failure outcomes never suppress or
   mutate the canonical parent.
8. Decoder artifacts publish atomically; a failed run retains `.incomplete`,
   does not publish a complete summary, and leaves no `.partial`, cache, or
   backup artifact after controlled cleanup.
9. No decoder performs network access, executes decoded content, loads an
   external model, or requires an optional engine.
10. No private evidence string, local case path, case-derived hash, exact private
    aggregate, or reusable secret enters source, fixtures, docs, commits, or
    public benchmarks.

## Acceptance gates

- RFC 4648 vectors and adversarial padding, unused-pad-bit, alphabet, length,
  boundary, whitespace, prefix/suffix, and canonical-round-trip cases pass.
- Authored synthetic multilingual UTF-8, BOM text, IOC/config/credential-label,
  and Microsoft-defined PowerShell UTF-16LE fixtures produce exact children.
- Random canonical bytes, all-byte values, NUL/control-heavy output, hashes,
  IDs, compiler-like values, URL-safe tokens, malformed PowerShell, and known
  binary signatures produce no fabricated text child.
- Raw, native, FLOSS, OCR, language-assessment, translation-candidate,
  translated, and translation-work artifacts are byte-identical with decoding
  off and on for a fixed corpus. Prior enriched and match files are exact
  prefixes of decoder-enabled output.
- Every assessment/outcome/count equation reconciles; every child has one
  existing parent; duplicate IDs, missing parents, tampered lineage, transform,
  span, hash, charset, limits, order, or child text fail closed.
- Identical runs and 1/2/N worker tests produce the same ordered child and match
  multiset. Injected hash collisions cannot create a false duplicate or parent
  match.
- Candidate, decoded-byte, total-byte, child, and depth limits cannot be
  exceeded. Resource-limited candidates retain parents and are counted without
  unbounded assessment output.
- Cancellation and injected failures before each stage publication, during
  validation, and during merge leave the run incomplete and no abandoned
  transient decoder artifact.
- A reproducible benchmark runs seven rotated off/on pairs at 100,000 and
  1,000,000 rows. The natural and
  no-candidate workloads have at most 3% median wall and peak-working-set
  regression and no workload exceeds 5%. Candidate-heavy throughput, allocation,
  output growth, attempted/decoded bytes, and child cardinality are reported
  separately rather than hidden by the overhead gate.
- The complete .NET and enrichment suites, formatting, decision-record gates,
  Markdown and air-gap documentation inventories, CLI/help smokes, privacy scan,
  and secret scan pass.
- A packaged direct `--decode auto` smoke and a decoder-off Full regression
  smoke prove easy selection, engine independence, no network access, matching
  provenance, and unchanged Full output. The existing complete quality-bundle
  integrity boundary remains unchanged.

### Performance-gate result and rollback

The frozen seven-pair, 100,000/1,000,000-row, three-workload benchmark was run
twice on the same Windows host. Both runs passed every correctness, ordering,
cardinality, and deterministic-artifact assertion but failed every timing and
peak-working-set threshold. After an allocation-free early-rejection
optimization, the 1,000,000-row natural and no-candidate medians still measured
about +1,507% and +1,501% wall-time regression, with about +179% and +184%
peak-working-set regression. Candidate-heavy functional work was larger again. The complete
privacy-safe reports are retained under `benchmarks/results/`.

These results triggered the preregistered Full-default rollback. They do not
invalidate the explicit feature: decoder-off behavior is unchanged, while an
examiner who requests `--decode auto|force` knowingly authorizes the bounded
extra passes and additive evidence. Automatic Full promotion requires a future
architecture that removes the extra full-stream validation cost and then passes
this unchanged gate; the benchmark is not relabelled.

## Strongest detractor and resolution

A canonical Base64 value can be arbitrary binary, and a valuable transform
chain can be Base64 to gzip to UTF-16LE PowerShell. This release will classify
the first decoded bytes as binary and recover no final text, whereas CyberChef
and bulk_extractor can continue recursively. Conversely, broadly following
those tools on millions of extracted memory strings can create unbounded,
sensitive, low-value evidence and resource amplification.

Both objections survive. The release adds a useful, deterministic text-child
step without claiming complete recursive recovery. Recognized binary outcomes
make the miss visible. A later byte-child and decompression decision can use
these measurements and must pass expansion, total-byte, time, storage,
deduplication, and nested-provenance gates before promotion.

## Falsifiers and revisit triggers

Disable `auto` in Full and revert to decoder-off if any of the following occurs:

- a canonical parent or pre-existing finding changes, disappears, or reorders;
- a child is published from a noncanonical value, invalid text conversion,
  missing/tampered parent, incomplete transform, or exceeded limit;
- output cardinality, memory, CPU, temporary storage, or assessment growth is
  not bounded by the recorded policy;
- repeated runs or worker counts change child order or identity;
- cancellation/failure can publish an apparently complete run or leave decoded
  cache/spill residue;
- ordinary high-confusion strings produce an unacceptable child rate or useful
  encoded fixtures are missed without a documented policy reason;
- decoded content enters translation or suppresses a parent without a separate
  authority decision; or
- private evidence or case-derived measurements enter public artifacts.

The rollback surface is `--decode off` and removal of decoding from Full. Raw,
translation, match, and report schemas retain their prior records, and no output
migration or external dependency is required. Keep any independently proven
generic transform/provenance validator fixes only if decoder-off byte
compatibility remains exact.

## Consequences

Users gain one-command, offline recovery of canonical Base64 text and contextual
PowerShell EncodedCommand content, with additive pattern findings and exact
parent provenance. The transform is explicit opt-in, including with Full, until
the frozen automatic-mode performance gate passes.

The pipeline emits additional sensitive evidence and bounded metadata when
enabled. Generic Base64URL, hex, percent,
compression, archives, binary carving, recursion, and translation authority
remain explicit future work rather than being implied by syntax or entropy.

## Primary references

- [Current bstrings enrichment pipeline](../enrichment-pipeline.md)
- [Current bstrings Base64 semantic validator](https://github.com/Donovoi/bstrings/blob/master/bstrings/BuiltInSemanticValidator.cs)
- [Current bstrings normalized-record and transform contract](https://github.com/Donovoi/bstrings/blob/master/bstrings/EnrichmentRegexPipelineCore.cs)
- [RFC 4648: The Base16, Base32, and Base64 Data Encodings](https://www.rfc-editor.org/rfc/rfc4648.html)
- [RFC 3986: Uniform Resource Identifier Generic Syntax](https://www.rfc-editor.org/rfc/rfc3986.html)
- [bulk_extractor Base64 scanner](https://github.com/simsong/bulk_extractor/blob/main/src/scan_base64.cpp)
- [bulk_extractor architecture paper](https://arxiv.org/abs/2208.01639)
- [CyberChef Magic source](https://raw.githubusercontent.com/gchq/CyberChef/master/src/core/lib/Magic.mjs)
- [CyberChef automatic-detection explanation](https://github.com/gchq/CyberChef/wiki/Automatic-detection-of-encoded-data-using-CyberChef-Magic)
- [Microsoft PowerShell EncodedCommand documentation](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_pwsh?view=powershell-7.6)
- [Velociraptor PowerShell EncodedCommand artifact](https://docs.velociraptor.app/artifact_references/pages/server.powershell.encodedcommand/)
- [YARA Base64 string modifier](https://yara.readthedocs.io/en/stable/writingrules.html#base64-strings)
- [MITRE CWE-409: Improper Handling of Highly Compressed Data](https://cwe.mitre.org/data/definitions/409.html)
