# ADR-0013: Resume a stopped translation plan with translation off

- **Status:** Accepted experiment
- **Date:** 2026-08-15
- **Scope:** resume-time translation exclusion, checkpoint generations, superseded artifacts, cross-version continuation, and provenance
- **Decision type:** forensic completion, failure recovery, engine policy, command-line behavior, and evidence lineage
- **Review method:** Robin round under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source workflow research; repository and stopped-run runtime audit; adversarial detractor review of provenance, crash, compatibility, and rollback
- **Implementation state:** implemented for v2.1.2; code, fault, parity, normal-resume performance, and release gates accepted
- **Extends:** [ADR-0012](adr-0012-explicit-whole-stage-resume.md). Normal resume keeps exact saved-plan checks.

## Context

`--full` enables offline language selection and local translation. A new run can
disable both with `-e translation`. Option order does not change that result.

A stopped result can belong to an earlier effective plan even when a later
command names the same directory with translation excluded. Before resume
support, the later command refuses the non-empty directory. The directory keeps
the plan that first created it.

ADR-0012 restores that saved plan exactly. Rewriting its `owner.json` is not
valid because its hash anchors checkpoint 1, and every checkpoint hashes its
predecessor.

Translation mode first changes canonical evidence at stage 7. `auto` creates
language assessments and candidates. `off` creates empty files. Stage 8 creates
translated children for `auto` and an empty translated file for `off`. Stages 1
through 6 do not use translation mode.

## Pre-registered claims and falsifiers

The review tests these claims:

1. A derived Off generation can inherit stages 1 through 6 and remain
   canonically equivalent to a fresh Full-minus-translation run.
2. Source stage 7 and abandoned translation work can remain independently
   attributable after the target reuses their fixed root filenames.
3. A recoverable journal can survive every process-stop boundary without two
   active generations or an untracked artifact.
4. An older binary refuses generation-aware state before it starts work.
5. `bstrings analyze -r -o <dir> -e translation` stays simple and keeps the
   established meaning of `-e translation`.

Any false complete state, missing source artifact, untracked move, model start,
silent option change, broken source-generation verification, or fresh-Off
mismatch falsifies the experiment.

## Independent first-round reviews

### External practice and primary sources

The external review found that workflow systems reuse only compatible work:

- [Nextflow cache and resume](https://docs.seqera.io/nextflow/cache-and-resume)
  binds cached work to task inputs, scripts, environments, and referenced
  values.
- [Snakemake rerun triggers](https://snakemake.readthedocs.io/en/v8.4.8/executing/cli.html)
  treat parameter, code, input, and software changes as rerun causes.
- [W3C PROV-O](https://www.w3.org/TR/prov-o/) models revision and derivation as
  new entities with explicit lineage.
- [`bulk_extractor` restart](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/bulk_extractor_restarter.h)
  is useful UX precedent, but its page state does not bind a complete scanner
  configuration.

The review recommended a derived plan that keeps unaffected upstream work. It
rejected changing the old owner or treating old stage 7 as Off evidence.

### Repository and runtime audit

The runtime review confirmed that `-e translation` means `off`, not
`detect-only`; the first affected checkpoint is stage 7; stage 8 and later must
be absent; and stages 1 through 6 can be revalidated without a language detector
or translation model. The current schema cannot express a safe branch.

It proposed an append-only generation with inherited checkpoint references,
new empty stages 7 and 8, and explicit source-to-derived lineage.

### Adversarial detractor

The detractor first rejected any same-directory override. Keeping old stage 7
would make `translationMode: off` false, while changing the owner would break
the checkpoint chain. It accepted only a generation design that leaves the old
owner and checkpoints unchanged, archives and maps superseded artifacts,
creates a new chain, and passes cross-version, parity, crash, and rollback gates.

## Rotated critiques

The external reviewer challenged the runtime design. It required inherited
checkpoint records, not copied ordinary checkpoints. It also found that an
active-generation pointer is insufficient while stage files use root names.

The runtime reviewer challenged a `detect-only` compromise. That would give
`-e translation` a second meaning and retain artifacts that a fresh Off run
cannot create. It kept the branch after stage 6 and rejected general engine
changes.

The detractor challenged the external evidence. None of the tools proves that a
forensic plan mutation is safe. The evidence supports explicit identity,
invalidation, and versioned migration rules only. The feature therefore remains
experimental until bstrings' gates pass.

## Considered options

1. **Resume saved Auto.** Retained for plain `-r`; rejected for explicit
   exclusion because it runs the model.
2. **Map exclusion to detect-only.** Rejected because the flag would have two
   meanings.
3. **Rewrite the owner or stage 7.** Rejected because it rewrites committed
   history.
4. **Require a new result directory.** Safest and retained as fallback, but it
   repeats expensive translation-independent work.
5. **Create a derived Off generation after stage 6.** Selected as a narrow,
   reversible experiment.

## Decision

### Command and scope

The only accepted resume-time change is:

```powershell
bstrings.exe analyze --resume -o <incomplete-results-directory> -e translation
```

Resume accepts exactly one canonical exclusion: `translation`. Other resume
options retain ADR-0012 restrictions. The saved mode must be `Auto`. The
highest checkpoint must be stage 7. Stage 8 and later must be absent. A complete
run, another mode, another exclusion, or any other option change refuses before
evidence mutation. Plain `-r` keeps exact-plan behavior.

### Generation and lineage

The source owner, checkpoints, attempts, and committed artifacts are immutable.
The target has a new run and generation identifier. Its transition records:

- source owner and checkpoint hashes;
- source and target option hashes;
- source and completion executable and kit identities;
- source `auto` and target `off` modes;
- branch point `raw-merge`, stage 6;
- every inherited checkpoint and artifact identity;
- every superseded committed artifact; and
- every abandoned uncommitted translation artifact.

Target checkpoints 1 through 6 are inherited-reference records. They identify
the source run and checkpoint that produced the bytes. They do not claim that
the target executable produced them.

The source stage-7 checkpoint and artifacts move to generation history with an
exact path map. Translation partials, caches, sidecars, statistics, and logs
move to abandoned-attempt history. None becomes target evidence or cache input.

The target creates empty language-assessment, translation-candidate, and
translated-string files. It commits stages 7 and 8, then continues normally. It
must not initialize the language detector, Python translation adapter, model,
or `llama-server`.

Final evidence records both plans. It states that model work occurred in an
abandoned source attempt and that no model-derived child was committed to the
target. The effective target mode is `off`.

### Recoverable migration

Windows does not provide one transaction for all required moves. The
implementation promises recoverable migration, not an atomic whole migration.

A flushed write-ahead journal records every path, length, hash, and step before
the first move. Each step is idempotent. The new binary completes or reverses an
interrupted transition before it opens a generation. One flushed active-state
publication is the final step. The generation-aware root schema makes older
binaries fail closed.

### Compatibility boundary

The first implementation is limited to an exact public source version and
checkpoint schema. It accepts the recorded `legacyImported` lineage without
claiming that the later importer produced the original bytes.

The reviewers disagree on whether the complete source kit must remain
available. The experiment follows the stricter rule: keep and verify the source
kit during transition. Removing that requirement needs a later decision.

## Non-negotiable invariants

1. `-e translation` always means `TranslationMode.Off`.
2. Source owner, checkpoints, attempts, and artifacts remain independently
   attributable.
3. The target inherits no translation-dependent checkpoint.
4. Source stage 8 or later makes the transition ineligible.
5. No detector or model worker starts during the target run.
6. Every inherited, superseded, abandoned, and target artifact has one explicit
   disposition and identity.
7. The transition holds the exclusive lease and rejects linked, reparse,
   changing, outside-root, or unknown paths.
8. Input, pattern, kit, executable, checkpoint, policy, or artifact drift
   refuses before the first move.
9. Recovery reaches either the untouched source or one valid target generation.
10. Old binaries refuse generation-aware state without changing it.
11. Metadata contains no extracted values and remains inside the result.

## Acceptance gates

### Semantic and provenance

- Compare fresh Full-minus-translation and derived runs. Active payloads,
  counts, ordering rules, lineage, matches, and reports must be canonically
  equal. Only declared transition, attempt, and timing fields may differ.
- Assert byte identity for inherited artifacts and exact source checkpoint
  references.
- Validate archived source stage 7 through its path map.
- Assert target stages 7 and 8 are empty and newly checkpointed.
- Report source Auto, target Off, branch stage, inherited stages, and abandoned
  model attempt in run and summary evidence.

### Fault and concurrency

- Inject cancellation, process termination, short writes, access denial, and
  disk-full behavior around every journal, move, record, empty-file,
  checkpoint, and activation publication.
- Reopen after each fault. Recover to one generation without false completion,
  duplicate files, untracked files, or lost source files.
- Race at least 32 starters. Exactly one writer can mutate the result.
- Prove that no detector, Python, or llama child survives or starts.

### Drift and path safety

- Change each input, pattern, option, executable, source kit, target kit,
  checkpoint, artifact, attempt, and journal field separately. Refuse before
  the first move.
- Test hard links, reparse points, dangling links, moved paths, case aliases,
  unknown files, open files, and changing files.
- Test low-free-space migration. Large source artifacts move on the same volume
  and are not copied.

### CLI, compatibility, privacy, and release

- Test short and long forms, option order, normalization, comma and repeated
  forms, duplicates, aliases, unknown engines, and prohibited resume options.
- Run the exact public source binary against migrated state. It must fail before
  a worker or evidence write.
- Validate the public source-to-import-to-derived lineage using complete kits.
- Scan fixtures, logs, metadata, docs, commits, and assets for private values.
- Run the complete Windows kit offline and prove zero translation worker starts.
- Rerun the established public 24 GiB resume benchmark with five alternating
  pairs against v2.1.1. Keep normal new-run overhead within the larger of one
  second or five percent. Require at least 60 seconds of reusable work and a
  resume saving above the larger of 30 seconds or half that reusable work.
- Prove with the transition fault suite that superseded stage-7 artifacts move
  on the same volume through `File.Move`; no artifact-copy path is permitted.

## Strongest detractor and resolution

A new directory with `-e translation` has one plan and one checkpoint chain.
The generation importer adds journal recovery, archive mappings, and version
compatibility. One missed crash window can make the convenience unsafe.

This objection controls rollout. The feature stays disabled until every gate
passes. If source stage 7 cannot remain verifiable while the active root matches
a fresh Off run, bstrings keeps refusing the override.

The decision resolves the objection by limiting the change to one dependency
boundary, retaining the source generation, and requiring fault-injected recovery
and fresh-Off parity before release. It does not accept a general plan editor.

## Falsifiers and revisit triggers

Reject or disable the experiment if any state reuses Auto stage 7, changes or
loses a source byte, starts a translation worker, lets an older binary continue,
accepts drift or a second writer, yields two active generations, differs from a
fresh Off run, or publishes misleading provenance.

Rollback removes the resume-exclusion reader but keeps transition and generation
metadata readable. It never deletes or converts an existing generation. Plain
exact-plan resume remains available for untransitioned results.

Revisit the decision if a later checkpoint design removes fixed root artifact
names, if a verified component contract makes more engine boundaries safe, or if
measurements show that the transition does not save material work.

## Acceptance evidence

The v2.1.2 full Release suite passed 1,112 tests. Four link tests skipped because
the host did not grant link creation. The transition suite covered each journal,
move, owner, recovery, drift, concurrency, and old-lineage boundary.

The five-pair 24 GiB benchmark passed. Sparse median new-run overhead was 0.024
seconds. Match-heavy overhead was 0.002 seconds. Median reusable work was
112.102 seconds. Median resume saving was 61.339 seconds, above the 56.051-second
gate. Canonical evidence and every reused checkpoint hash matched. Native output
was measured at candidate commit
`76c3abe6c3c4214be1fcf60cb785eb2c6429cdd5`. The adjacent provenance file binds
the tested `bstrings` tree to Git object
`845d494aa75ecd4985fb30cfdefc996589c25f30` and the benchmark script to Git
object `70c019086b3aeddf0efa5f22e6a65ad6b92fdcb4`. The release candidate must produce
the same two objects before these results count as acceptance evidence.
Ordering was not byte-stable across fresh runs and remains recorded as a known
ordering limitation.

The first ADR draft proposed comparing a derived run with a fresh Off run by a
fixed 50-percent ratio. Before that timing was executed, review found that the
ratio mixed transition validation with unrelated upstream extraction cost and
had no stable public stage-7 fixture. The accepted protocol uses the existing
measured-work gate from ADR-0012 and retains a separate no-copy transition gate.

## Consequences

The experiment can preserve expensive extraction work when model translation
has not committed. It adds a checkpoint schema, generation lineage, archive
mappings, and crash recovery for one bounded transition. It does not authorize
other engine changes, mid-stage translation reuse, a general dependency DAG, or
automatic resume.

## Primary references

- [Nextflow cache and resume](https://docs.seqera.io/nextflow/cache-and-resume)
- [Snakemake rerun triggers](https://snakemake.readthedocs.io/en/v8.4.8/executing/cli.html)
- [W3C PROV-O](https://www.w3.org/TR/prov-o/)
- [`bulk_extractor` restart implementation](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/bulk_extractor_restarter.h)
- [ADR-0012](adr-0012-explicit-whole-stage-resume.md)
