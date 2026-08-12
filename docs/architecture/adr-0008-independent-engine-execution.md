# ADR-0008: Make analysis engines independently selectable

- **Status:** Accepted for runtime selection; componentized installation profiles
  are deferred
- **Date:** 2026-08-11
- **Scope:** `analyze` engine selection, routing, provenance, validation order,
  dependency startup, and compatibility
- **Decision type:** stage routing, forensic coverage, failure policy, provenance,
  performance, and command-line contract
- **Review method:** three independent first-round reviews and rotated critiques
  under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** Primary-source/External Practice Reviewer,
  Repository/Runtime Auditor, and Packaging/Dependency Detractor
- **Owner:** bstrings maintainers

## Context

The `analyze` command exposes independent controls for FLOSS, OCR, and
translation, but native extraction is unconditional. Content-routing records,
their C# validator, the FLOSS and OCR consumers, and the engine-status ledger
all assume that native extraction is both eligible and scheduled. This means a
request for FLOSS-only or OCR-only work still scans every input natively.

That coupling is especially visible in specialist runs. FLOSS normalization
normally omits its static-string category because native extraction already
provides a complete byte-oriented baseline. If native extraction is merely
skipped without changing the recovery arguments, a FLOSS-only run silently
loses static strings. Translation is different again: it transforms canonical
string records and cannot produce evidence records directly from raw bytes.

The released quality installation is one exact authenticated profile. Its
manifest is verified as a whole before selected tools are resolved. A missing
or corrupt unselected model therefore blocks a specialist run from that shared
profile. This is deliberate fail-closed behavior: selectively trusting files
inside the same loadable directory would weaken the existing bundle-integrity
claim. The core executable can already perform native-only work without a
quality bundle.

Three independent reviews agreed that upstream native strings, FLOSS, RapidOCR,
and llama.cpp are separately invocable technologies, but distinguished record
producers from transforms and distinguished runtime selection from separately
installable capability packs. The reviews also found adjacent correctness and
availability issues: invalid regexes are compiled only after expensive work,
invalid legacy processor selections can return success, and the integrated OCR
path does not exercise its inference self-test before reading evidence.

A later three-review round considered a convenience control for “Full minus
named engines.” The external-practice review recommended an exclusion modifier
that requires `--full`; the runtime review proposed a `--full-except` selector
that would imply Full and record a new exclusion projection; and the detractor
initially proposed a repeatable `--full-without` modifier because translation is
a transform rather than a source producer. Rotated critiques challenged the second Full
activator, bare collection-option arity, ambiguous precedence, token capture,
and invocation-specific provenance. The synthesis keeps a strict exclusion
modifier, does not add a second profile activator or provenance field, and
retains the explicit controls as the canonical rollback path. A subsequent UX
requirement accepted both repeat and comma forms under the exact grammar below;
the strongest comma-list objection and its fail-closed resolution are recorded
in this decision.

## Considered options

1. **Keep native extraction mandatory.** Rejected for runtime execution. It
   prevents a user from intentionally running only FLOSS or OCR and consumes
   substantial unnecessary work on large inputs.
2. **Add one comma-separated `--engines` option.** Rejected. A flat list cannot
   express the existing `auto`, `force`, `all`, and `detect-only` semantics and
   creates precedence ambiguity with the established flags.
3. **Add an orthogonal native on/off option and retain the existing specialist
   mode flags.** Accepted. It preserves backward compatibility and makes the
   effective execution contract explicit.
4. **Allow translation with every producer disabled.** Rejected. Translation
   consumes canonical records; it cannot translate a raw byte image without a
   selected producer or a separately designed canonical-record input command.
5. **Make pattern matching and report projection optional in this change.**
   Rejected. They are bounded finalization stages and the documented deliverable
   of `analyze`, not source-record engines. A future diagnostic/export command
   may expose raw engine output without weakening this pipeline.
6. **Verify only selected rows of the current complete bundle manifest.**
   Rejected. Unverified DLLs, Python packages, or models in the same physical
   tree could still influence loader resolution and would make
   `bundleIntegrity` misleading.
7. **Publish independently installable component packs now.** Deferred. Safe
   physical profiles require exact transitive dependency manifests, collision
   and cycle rejection, license closure, atomic per-profile installation, and
   separate public release acceptance.
8. **Add `--full-except` and make it imply Full.** Rejected. It creates a second
   Full activator, makes a bare or malformed collection option dangerously close
   to an expensive Full run, and complicates precedence with `--full`.
9. **Add no convenience control.** Credible but not selected. Existing explicit
   engine modes remain the clearest primitive and the complete rollback path,
   but excluding several Full engines is verbose and easy to mistype.
10. **Add `--full --exclude-engine <name>` as a repeatable modifier.** Accepted,
    with comma-separated names also accepted inside one value token. The option
    is reduced to the same effective modes as existing explicit controls before
    orchestration; it does not create a profile or execution path.
11. **Use `-x` as the short exclusion alias.** Rejected after collision audit:
    legacy bstrings already uses `-x` for maximum string length. `-e` is accepted
    as the non-colliding short alias.

## Decision

Add `--native-extraction on|off`, defaulting to `on` in both ordinary and Full
analysis. Explicit engine options continue to override Full defaults. The
effective source producers are:

- native extraction when `--native-extraction on`;
- FLOSS when `--recover-executable-strings` is `auto` or `force`; and
- OCR when `--ocr` is `auto` or `force`.

Add `--exclude-engine <list>` with short alias `-e` as a convenience modifier
that is valid only when `--full` is explicitly present. It never implies Full.
Each occurrence consumes exactly one value token, and occurrences accumulate:

```text
--full --exclude-engine translation --exclude-engine ocr
--full -e translation,ocr
--full -e "translation, ocr"
```

The value grammar is `segment ("," segment)*`. The parser splits on literal
commas, trims surrounding whitespace from each segment, compares names
case-insensitively, and canonicalizes accepted names to lowercase. The exact
names are `native`, `floss`, `ocr`, and `translation`. It does not accept whitespace-
separated multi-arguments: `-e native ocr` is invalid. Missing values, empty
segments (`ocr,`, `,ocr`, or `ocr,,floss`), unknown names, and duplicate or
case-duplicate names across the entire command are errors. Thus
`-e ocr --exclude-engine OCR` fails instead of silently deduplicating intent.

Resolution starts from the unchanged Full defaults and maps exclusions to the
existing effective modes:

- `native` -> `--native-extraction off`;
- `floss` -> `--recover-executable-strings off`;
- `ocr` -> `--ocr off`; and
- `translation` -> `--translation off`.

An exclusion conflicts with any explicitly supplied selector for the same
engine, including a selector that also says `off`; the command fails rather
than depending on order or accepting two sources of intent. Explicit selectors
for other engines remain valid. Provider, device, scheduling, threshold, and
other tuning options for an excluded engine are inert at runtime but retain
their deterministic validation, so malformed configuration still fails early
and a valid tuning option never starts the excluded worker.

The modifier is parser-level sugar only. It resolves into the existing
`AnalysisOptions`, which remain authoritative in `run.json` together with the
existing routing and engine-status records. No `excludedEngines`, raw command
line, or other invocation-specific provenance field is added; the shorthand
and its equivalent explicit-off command intentionally have the same semantic
provenance. The orchestrator, output schemas, stage order, and failure policy do
not branch on how the effective modes were requested.

Translation remains an independent transform controlled by `--translation`.
`auto`, `all`, and `detect-only` require at least one selected producer. More
generally, `analyze` rejects a configuration with no source producer before
bundle lookup, output-directory creation, or evidence access. A selected
specialist that finds no applicable input or emits zero records is a valid
complete result; selection is not the same as nonzero output.

Native-off content routing uses a new policy identity. Native remains an
eligible route for forensic coverage disclosure but is absent from scheduled
routes. Default and Full native-on routing retain the existing policy and
decision identities. All routing consumers must accept the new policy only
under the explicitly expected native-selection state and must continue to
reject scheduled routes that are not eligible.

When native is off:

- `native-strings.jsonl` is atomically published as an empty file;
- native extraction and its pre/post stages do not run;
- every input receives native status `disabled-by-user`, selected `false`, with
  zero output records;
- raw merge ordering remains native, FLOSS, OCR so downstream schemas and
  positional accounting stay stable; and
- FLOSS is invoked with static-string inclusion enabled. When native is on,
  current FLOSS omission/deduplication behavior remains unchanged.

Specialist analysis publishes truthful per-input engine status and continues to
bind Magika identity plus selected routes in the existing routed evidence
contract. Native-only analysis must not start Magika, FLOSS, OCR, or translation
merely to construct a ledger, and it does not fabricate classifier/routing
records. Its authoritative input manifest and native output remain unchanged.

An initial candidate added a native-only engine-status projection by rereading
the complete native JSONL. The preregistered paired benchmark measured a 10.8%
median wall-time regression, over twice the 5% ceiling. It also confirmed that
parallel native row order is not a byte-stable comparison surface. That
projection was removed before acceptance; native-only artifact parity is judged
by canonical record identity/offset sets and the exact histograms, while
specialist routing/status remains mandatory.

Pattern matching and forensic reports remain mandatory finalization stages for
`analyze`. “Engine used by itself” means it is the sole selected source producer
and no unselected worker/model is preflighted or started; it does not mean the
result omits integrity checks, canonical merging, matching, or reports.

The existing complete quality bundle remains one atomic trust profile. Whenever
it is explicitly or implicitly selected, its exact manifest remains fully
verified. Runtime optionality therefore does not mean independently installable
or fault-isolated components. A later ADR may define physically isolated
capability profiles; it must not reinterpret current transport packs as trusted
runtime components.

Validation order changes so deterministic configuration errors fail before
output creation or expensive work. Processor/CPU-engine values and the complete
resolved regex set are validated and frozen at the start of the run. OCR uses
the same selected runtime/session to run its inference self-test before reading
evidence. Invalid legacy processor selections return nonzero.

Selected-engine failure remains run-global and fail-closed. The command retains
`.incomplete`, does not publish a complete summary, and requires an explicit
rerun with a changed selection. It never silently continues and labels a partial
engine set complete.

## Non-negotiable invariants

1. Default and Full keep native extraction on and preserve their existing
   specialist defaults.
2. No record may originate from a disabled producer, and native-off output is
   exactly empty.
3. Translation never silently enables a producer and every translation child
   refers to a parent emitted by a selected producer.
4. FLOSS-only includes all required static and derived categories; normal
   native-plus-FLOSS behavior does not duplicate static coverage.
5. Effective modes, routing decisions, per-input engine status, record origins,
   run/summary aggregates, and child lineage agree.
6. Unselected workers and models are not preflighted, loaded, or started.
7. Existing complete-bundle exact-set/hash verification is not narrowed.
8. Input mutation, cancellation, selected-engine failure, and cleanup failure
   remain fail-closed and cannot publish a complete result.
9. Invalid deterministic configuration fails before result-directory creation,
   bundle verification, or evidence processing.
10. Private evidence strings, paths, hashes, and case-derived identifying totals
    never enter source, fixtures, docs, commits, or public CI output.
11. Full without exclusions is unchanged, and exclusion resolution is
    independent of command-line option order.
12. The shorthand and the equivalent explicit-off selectors resolve to the same
    `AnalysisOptions`; no orchestrator or provenance-schema branch depends on
    the shorthand.
13. An excluded engine's tuning options may be validated but cannot cause its
    runtime, provider, model, or worker to be resolved or started.

## Acceptance gates

The release candidate must pass all of the following:

- Default/native-only: native records only; specialists and translation are
  empty; no Python, Magika, FLOSS, OCR, llama.cpp, or model process starts.
- Full: selected modes, routing identity, output schemas, ordering, lineage, and
  packaged smoke behavior remain compatible.
- FLOSS-only force on a pinned synthetic PE: native/OCR/translation are empty;
  FLOSS static, stack, tight, decoded, and language categories are normalized;
  native status is disabled.
- FLOSS-only auto on unsupported input: complete zero-record specialist result
  with not-applicable status, not a false failure.
- OCR-only force on pinned synthetic image/PDF: native/FLOSS are empty; OCR
  strings and assessments have exact lineage; no FLOSS/translation runtime
  starts.
- OCR-only auto on unsupported input: complete zero-record result with truthful
  not-applicable status.
- Translation over native-only and OCR-only: candidates and children refer only
  to selected producer parents; work counters and cardinality reconcile.
- Detect-only: language assessments may be produced but no translation runtime
  or model is required.
- No-producer configurations fail before output creation and never silently
  re-enable native.
- `--exclude-engine` and `-e` require explicit `--full`; repeat and comma forms
  resolve identically, while a missing value, empty segment, unknown name,
  whitespace-separated extra value, duplicate/case-duplicate name, or
  same-engine explicit selector fails before output creation.
- Full-minus-native, FLOSS, OCR, or translation produces the same effective
  `AnalysisOptions`, child-process plan, canonical evidence, reports, and
  terminal engine states as the corresponding existing explicit-off command.
- Valid tuning options for an excluded engine remain accepted and validated but
  are inert; invalid values fail before evidence access and valid values do not
  initialize the excluded runtime.
- Native-on routing retains its existing policy/decision identity; native-off
  routing is accepted end-to-end by C#, FLOSS, OCR, and the engine ledger only
  when native-off was expected.
- An unexpected native record while disabled, an unexpected specialist record,
  or a route/status/count disagreement fails validation.
- Invalid literal/file regex, processor, or CPU-engine selections fail before
  evidence work with a nonzero exit; a valid regex file is frozen against later
  mutation.
- OCR self-test failure occurs before source reading and publishes no final OCR
  output; zero routed OCR candidates do not initialize the runtime.
- Measured benchmark gate: on a fixed public synthetic native-only corpus,
  seven rotated baseline/candidate pairs must produce identical canonical
  native/raw/enriched record identity-and-offset sets, match sets, and exact
  histogram/report values (parallel line ordering and executable-build identity
  are not comparison keys), while the
  candidate's median wall time and sampled peak working set may regress by at
  most 5%. On FLOSS-only and OCR-only fixtures, process telemetry must show zero
  native worker invocations and at least a 20% reduction in bytes scanned by
  native extraction relative to the native-on control.
  The accepted 64 MiB project-synthetic run used seven rotated pairs against
  the exact pre-change commit: canonical native/raw/enriched/match/report sets
  were identical, median wall regression was 0.85%, and a separate seven-pair
  20 ms coordinator-process sample measured peak working-set regression of
  -9.26%. Packaged FLOSS/OCR telemetry remains a release-candidate gate because
  the existing v1.9.17 bundle correctly refuses a mismatched executable.
- Exclusion-alias no-regression measurement gate: the first exploratory
  100,000-call/seven-pair protocol was rejected rather than relabeled as a
  pass. Its approximately 38%-110% elapsed differences were real but divided
  two tens-of-nanoseconds operations, and repeated trials exposed tiered-JIT
  nonstationarity and an initially nonequivalent baseline. Before the accepted
  run, the gate was revised to a fixed public 21-case matrix, exact per-case and
  per-sample parity with equivalent explicit-off resolution, 1,000,000 warm-up
  calls per variant, 5,000,000 measured calls per variant, and 15 independently
  started child processes with tiering and ReadyToRun disabled. Candidate
  median must be at most 250 ns/call, every sample at most 500 ns/call, and the
  measured candidate loop must allocate exactly zero bytes. Baseline timing is
  diagnostic only. The final acceptance run measured 72.206 ns/call median,
  76.724 ns/call maximum, and zero allocated bytes, with identical checksums in
  all 15 samples; the explicit-off diagnostic median was 37.990 ns/call. Static review
  also requires exactly one production resolver invocation per command, so this
  one-time cost cannot move into a file-, record-, route-, or candidate-level
  loop. A bounded candidate-build native-only `-e` versus explicit-off smoke
  must retain exact canonical native/raw/enriched/match identity-and-offset
  sets, histograms/reports, effective modes, and child-process selections. Full
  with no exclusion must retain the established packaged acceptance behavior;
  the existing multi-gigabyte bundle need not be reverified once per alias form.
- Air-gap, cancellation, input-mutation, cache/partial cleanup, and prior-output
  preservation tests remain green.
- Full .NET, Python, PowerShell decision, Markdown-link, formatting, and privacy
  gates pass on the exact commit proposed for merge.

## Strongest detractor and resolution

The strongest objection is that native extraction is the only universal
complete-byte baseline. FLOSS applies to executable formats and reports derived
locations; OCR applies to images/documents; translation cannot create source
records. Disabling native can therefore remove broad forensic coverage even
when the selected specialist succeeds.

The decision does not conceal that tradeoff. Native stays on by default and in
Full, remains visibly eligible in routing, and is recorded as explicitly
disabled per input. Only an explicit user choice disables it. The command
rejects a producerless run, preserves all selected-source parents and lineage,
and keeps matching/reports so specialist evidence remains directly usable.

A second objection is that a corrupt unselected component still blocks a run
inside the complete quality kit. This is accepted for this tranche because the
directory is one authenticated loadable trust profile. Specialist execution is
independent at runtime, while independently installable capability profiles are
honestly deferred rather than simulated with unsafe partial verification.

The strongest objection to the convenience syntax is that exclusions add no
capability and comma lists are easy to mistype: an empty segment, duplicated
name, or unquoted embedded space could otherwise produce an unintended Full
selection. The exact one-token grammar, explicit `--full` requirement, strict
duplicate/empty/unknown rejection, same-engine conflict rule, and pre-output
validation make those mistakes failures rather than partial interpretations.
The long explicit controls remain documented, supported, and sufficient if the
shorthand proves confusing.

## Falsifiers and revisit triggers

Revert the public native-off exposure while retaining safe validation fixes if:

- any unselected engine/model is started or required by runtime preflight;
- a disabled producer emits a source parent or a translation child references
  an unselected producer;
- FLOSS-only omits static strings, or native-plus-FLOSS starts duplicating them;
- effective flags, routing/status ledgers, output cardinality, or run/summary
  metadata disagree;
- default or Full behavior regresses;
- native-off can be enabled implicitly, or the coverage loss is not explicit;
- selected-engine failure can publish status `complete`; or
- existing whole-bundle verification is weakened;
- bare or malformed `--exclude-engine` can start Full or reach evidence access;
- exclusion parsing becomes order-dependent, silently ignores a segment, or
  accepts duplicate/conflicting intent;
- shorthand and explicit-off commands resolve to different effective options,
  process plans, outputs, or provenance; or
- an excluded runtime is resolved or started because one of its valid tuning
  options was present.

If an exclusion falsifier occurs, remove `--exclude-engine`/`-e` while retaining
the independently selectable engine controls and early-validation fixes. No
output migration or orchestrator rollback is required because the modifier has
no runtime branch or provenance field.

Revisit componentized installation profiles when measured full-manifest hashing,
download size, or unrelated profile corruption materially blocks specialist
operation. Any future design is falsified if one physical capability profile can
load a byte outside its exact transitive closure, mixes component versions,
omits license/provenance closure, or lacks independent public installer and
packaged-path acceptance.

## Consequences

Users gain explicit native-only, FLOSS-only, OCR-only, and translation-over-one-
producer workflows without changing Full. The implementation adds a routing
policy version, uniform engine-state handling, early validation, and additional
mode-matrix tests. Native-off intentionally trades universal coverage for
specialist speed and must be visibly recorded.

Full users also gain a compact, fail-closed way to remove named engines while
the resolved modes, evidence contract, and authoritative provenance remain the
same as the existing explicit selectors. This adds parser/help/conflict tests,
but does not add a profile, orchestrator path, output field, or bundle profile.

The complete quality bundle remains large and atomically verified. This change
does not reduce download size or make a damaged shared quality profile partly
usable. Componentized physical profiles remain a separate, larger release and
supply-chain project.

## Primary references

- [GNU strings manual](https://sourceware.org/binutils/docs/binutils/strings.html)
- [System.CommandLine syntax and collection arity](https://learn.microsoft.com/en-us/dotnet/standard/commandline/syntax)
- [GNU tar repeatable exclusion practice](https://www.gnu.org/software/tar/manual/html_node/exclude.html)
- [Git repeatable exclusion practice](https://git-scm.com/docs/git-rev-parse)
- [FLOSS v3.1.1 README](https://github.com/mandiant/flare-floss/blob/v3.1.1/README.md)
- [RapidOCR](https://github.com/RapidAI/RapidOCR)
- [RapidOCR CLI quickstart](https://rapidai.github.io/RapidOCRDocs/main/quickstart/)
- [llama.cpp b10248 server](https://github.com/ggml-org/llama.cpp/blob/b10248/tools/server/README.md)
- [analysis orchestrator](https://github.com/Donovoi/bstrings/blob/master/bstrings/AnalysisOrchestrator.cs)
- [content-routing validator](https://github.com/Donovoi/bstrings/blob/master/bstrings/ContentRoutingCore.cs)
- [engine-status ledger](https://github.com/Donovoi/bstrings/blob/master/bstrings/EngineStatusCore.cs)
- [offline enrichment adapter](https://github.com/Donovoi/bstrings/blob/master/tools/enrichment/bstrings_enrich.py)
- [command reference](../command-reference.md)
