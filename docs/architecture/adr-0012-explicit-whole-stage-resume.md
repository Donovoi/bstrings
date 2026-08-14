# ADR-0012: Resume incomplete analysis at verified stage boundaries

- **Status:** Accepted
- **Date:** 2026-08-14
- **Scope:** integrated analysis progress, checkpoints, cancellation, crash recovery, concurrent access, provenance, and one narrow v2.0.0 import path
- **Decision type:** forensic completion, failure recovery, provenance, performance, and command-line behavior
- **Review method:** Robin round under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source external research; repository and stopped-run audit; adversarial forensic, concurrency, migration, and performance review
- **Implementation state:** runtime and documentation implementation complete; the original performance gates failed twice and the first corrected long fixture failed qualification; performance acceptance, public-kit engine coverage, privacy, and public-kit acceptance remain release blockers
- **Supersedes:** only the broader run-resume deferral in [ADR-0005](adr-0005-translation-integrity-and-run-dedup.md) and [ADR-0006](adr-0006-q4-cuda-full-translation.md); their translation atomicity, cache, and integrity rules remain in force

## Context

An integrated analysis can spend a long time in extraction, OCR, language
assessment, or translation. Version 2.0.0 keeps failed or cancelled output and
marks it incomplete, but it cannot continue that run. The user must create a
new result directory and repeat every stage.

The pipeline already publishes major stage outputs atomically and retains a
root `.incomplete` marker until final validation succeeds. It does not have a
durable stage checkpoint, an exclusive run lease, or a contract that lets each
feature prove that its own output is complete. Progress text and the existence
of a JSONL file are not completion records.

One maintainer-held v2.0.0 run stopped during translation before this decision.
It contains complete candidate stage outputs but no checkpoint or final
summary. It motivates a narrow compatibility importer. Private paths, hashes,
record counts, and extracted values from that run are not repository evidence
and are not recorded here.

The first performance protocol used one percentage for new-run overhead and one
percentage for resume saving. Two privacy-safe, three-pair synthetic runs
falsified those gates while preserving canonical output parity:

- A 1 MiB match-heavy fixture measured 23.39 percent median overhead and 5.23
  percent median resume saving. The median added new-run time was about 1.28
  seconds, and the median resume saving was about 0.33 seconds.
- A sparse 64 MiB fixture measured 15.61 percent median overhead and 15.94
  percent median resume saving. The median added new-run time was about 0.45
  seconds, and the median resume saving was about 0.52 seconds.

Both results are failed evidence, not acceptance evidence. They show two flaws
in the protocol. A small fixed cost becomes a large percentage of a short run,
and a checkpoint ordinal does not measure how much expensive work it can reuse.
The corrected gates below use both absolute and relative significance and select
the resume point by measured reusable work.

The first corrected five-pair run kept those margins unchanged. Sparse and
match-heavy new-run overhead passed at median additions of 0.275 and 0.431
seconds. Canonical output parity and exact preservation of every reused byte
also passed. The frozen 8 GiB long fixture contained only 34.820 seconds of
reusable work, below the required 60 seconds, and saved 19.354 seconds, below
the 30-second floor. It is failed workload-qualification evidence, not a resume
failure or acceptance evidence. Before a new run, the long fixture was frozen
at 24 GiB using the observed scan rate. All timing and correctness margins stay
unchanged.

## Pre-registered claims and tests

The round tested these claims before selecting a design:

1. Whole-stage checkpoints can save substantial work without accepting a
   partial stage as evidence.
2. An explicit resume command is clear enough for examiners and scripts.
3. Identity and drift checks can refuse a different input, configuration, kit,
   output set, or concurrent writer before a resumed stage writes data.
4. The stopped v2.0.0 run can be imported only if its known completed prefix is
   fully validated and translation starts again.
5. Checkpoint work does not impose material cost on a new uninterrupted run.

The first pre-registration required no more than 8 percent new-run overhead and
at least 65 percent saved wall time in the late-resume benchmark. Both margins
were falsified and are retained here as failed history. Support now requires
the corrected performance gates in this record plus every correctness gate.
Any silent drift, checkpoint advancement before stage publication, concurrent
writer, evidence mismatch, case-data egress, or false complete result falsifies
the decision.

## Considered options

1. **Keep new-run-only behavior.** This is simple but repeats all completed
   work after a local interruption. Rejected.
2. **Resume automatically when an output directory looks incomplete.** This is
   convenient, but it can turn an ordinary command into a mutating continuation
   without a clear request. Rejected.
3. **Resume individual records or translation cache rows.** This can save more
   work, but it expands the commit protocol into every inner loop and weakens
   the present atomic translation boundary. Rejected.
4. **Use explicit whole-stage resume with feature-owned validators.** This
   retains atomic stage semantics and makes the user's intent visible.
   Accepted.
5. **Re-run native extraction before importing the one legacy run.** This can
   detect a consistently truncated prefix, but it repeats the main recovered
   work and does not authenticate unsigned local state against an actor who can
   alter the run and its software. Rejected for the stated local-interruption
   threat model.

## Decision

### Explicit command

Resume is never automatic. The user runs:

```powershell
bstrings.exe analyze --resume -o <incomplete-results-directory>
```

`-r` is the short form of `--resume`. A normal resume accepts only its output
directory and an optional `--bundle-root`. The optional path can locate the
same verified kit after a move; it cannot change kit identity. Resume restores
the saved input and effective options. It does not accept a new input or a
changed analysis configuration. The output directory must be on a physical
local filesystem; a network result share is refused.

A new or empty output directory still starts a new run. A complete directory
cannot be resumed or overwritten.

### Whole-stage checkpoints

The orchestrator creates a checkpoint only after a stage publishes its full
atomic output set and the owning feature validates that set. The checkpoint
records the stage contract, artifact identities, and the next stage. A generic
file-exists test cannot authorize reuse.

Each resumable feature owns:

- its stage contract and version;
- schema, cardinality, ordering, and provenance checks; and
- cleanup rules for its own temporary files.

The orchestrator registers each exact stage output set. The shared resume core
owns path safety, artifact size and SHA-256 checks, checkpoint dependencies,
and the exclusive lease.

The checkpoint itself uses a flushed temporary file and a same-directory move
to a new final name. A crash can leave the prior checkpoint or an uncommitted
temporary file. It cannot leave a valid checkpoint that claims an unpublished
stage.

Resume validates every completed stage in order. It starts at the first stage
without a valid committed checkpoint. It reruns that whole stage. In
particular, interrupted translation output and its run-local cache are never
reused. Translation starts again from its candidate input.

### Identity and drift

Before reusing a committed stage, resume verifies:

- the supported checkpoint and run schemas;
- the canonical result path and physical-path safety;
- the original input inventory, lengths, and content hashes;
- the saved effective analysis options;
- the exact executable, bundle manifest, model, engine, and policy identities;
- the checkpoint chain and every committed stage artifact; and
- the identity of every committed artifact; and
- whether other entries are recognized uncommitted stage files or unknown
  output.

The verifier re-enumerates and rehashes the input. It does not accept matching
paths, timestamps, or lengths as content identity. A changed input, option,
kit, stage contract, or committed output refuses resume. Unknown output also
refuses resume. Recognized uncommitted stage, cache, backup, and partial entries
move into that attempt's diagnostic quarantine before the stage restarts.

### Lease, attempts, and progress

One process holds an exclusive lease for the result directory. A second
process fails before it changes run state. The implementation relies on the
live exclusive file handle, not a process identifier alone. A crash can leave
the lease file, but it releases the operating-system handle.

Every start and resume has a new attempt identifier. Attempt provenance records
start and stop times, the last committed stage, completion, cancellation, or
failure. Earlier attempts remain visible.

Progress reports the stage, completed work, and whether prior stages were
reused. Attempt metadata identifies the current attempt. Progress is advisory.
It does not authorize evidence or replace the final completion checks.

### Narrow v2.0.0 import

The explicit resume command has one exceptional importer for the known
pre-checkpoint v2.0.0 output shape. It is not a general legacy promise. The
importer runs only when all these conditions hold:

- `run.json` is schema 1, identifies bstrings 2.0.0, and says `incomplete`;
- `.incomplete` exists, while `summary.json` and resume checkpoints do not;
- the exact recorded executable and full kit identities are available and
  verify;
- saved options use supported built-in patterns and no custom regular
  expression;
- the directory has only the known artifact prefix and allowed diagnostic
  logs;
- full input re-enumeration and content-hash verification succeed;
- routing, native, OCR, FLOSS, raw concatenation, language assessments, and
  translation-candidate projection pass their complete streaming validators;
  and
- no translation output, translation statistics, cache, partial file,
  decoding output, report, or later-stage artifact exists.

The importer makes no change until every check succeeds. It then records an
immutable import event and atomically creates checkpoints only for the proven
completed prefix. Translation starts from zero.

The old run did not persist the later decoder fields. Its import fixes decoding
to `off`, which was the effective v2.0 complete-preset behavior after the
performance rollback. The import event and final provenance disclose this
import-time choice. Any decoder or later-stage artifact refuses import.

## Non-negotiable invariants

1. Only `-r` or `--resume` can request resume or legacy import.
2. Validation and lease acquisition complete before resumed analysis writes an
   evidence artifact.
3. A checkpoint can name only a feature-validated, atomically published whole
   stage.
4. An interrupted stage reruns in full. No progress row, log line, temporary
   file, or translation-cache row authorizes reuse.
5. Input, configuration, executable, kit, model, policy, schema, or committed
   artifact drift fails closed.
6. One result directory has at most one live writer.
7. Attempts are append-only provenance. Resume never hides a prior failure or
   cancellation.
8. Exit code 0, complete `run.json`, complete `summary.json`, and no
   `.incomplete` marker remain the only full-run completion contract.
9. Resume metadata can contain case paths, saved options, custom pattern text,
   and content hashes. It does not copy extracted strings, decoded values,
   translations, or pattern hits. The complete result directory remains
   sensitive case data.
10. No checkpoint, progress, or resume data leaves the local result directory.
11. The legacy importer is bounded to the exact v2.0.0 shape above and cannot
    become a generic schema-repair path.

## Evidence ledger

| Claim | Evidence | Direction and entailment | Limit |
| --- | --- | --- | --- |
| Users value continuation without another mode-specific command | [bulk_extractor v2.1.1 manual](https://github.com/simsong/bulk_extractor/blob/v2.1.1/man/bulk_extractor.1) documents automatic restart from a partial report | Supports visible continuation as established forensic-tool practice | It does not establish safe identity checks |
| A prior report alone is unsafe resume identity | [bulk_extractor startup](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/bulk_extractor.cpp), [restarter](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/bulk_extractor_restarter.h), and [phase-one skip logic](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/phase1.cpp) parse old page identifiers and skip them without binding the current input and full configuration | Weakens automatic or marker-only resume; supports exact drift checks | The upstream design has a different page-processing contract |
| Explicit checkpoint state can survive interruption | [GNU ddrescue manual](https://www.gnu.org/software/ddrescue/manual/ddrescue_manual.html) requires a mapfile for continuation, updates it periodically, synchronizes it, and keeps a backup | Supports an explicit durable state file and clear user intent | A block-copy map does not prove a multi-engine forensic stage |
| Checkpoint compatibility must be narrow | [Spark Structured Streaming guide](https://spark.apache.org/docs/latest/streaming/apis-on-dataframes-and-datasets.html) documents checkpoint-location identity, batch identifiers, compatibility limits, and sink-dependent guarantees | Supports versioned state and refusal of incompatible changes | Distributed streaming uses a different output and failure model |
| Existing-storage append needs stronger validation than file presence | [Plaso extraction CLI](https://github.com/log2timeline/plaso/blob/main/plaso/cli/extraction_tool.py) permits append for an aborted session while retaining a validation TODO | Supports the detractor's demand for feature-owned full validation | A current-source TODO is cautionary, not a completed mechanism |
| Live ownership must not rely on a stale process identifier | [.NET `FileShare` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare) defines `None` as declining sharing until the file is closed | Supports an operating-system-held exclusive lease | Network filesystems and hostile handle replacement remain outside scope |
| A durability request must include buffered intermediate data | [.NET `FileStream.Flush(Boolean)` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush) distinguishes flushing intermediate buffers to disk | Supports flushing a temporary checkpoint before its same-directory move | Hardware and filesystem failure can still exceed application guarantees |
| Current stages already use incomplete markers and atomic final publication | [Output and provenance](../output-and-provenance.md) and the audited `bstrings/AnalysisOrchestrator.cs` implementation describe current failure boundaries | Supports adding whole-stage checkpoints instead of record reuse | Existing output has no cross-run checkpoint contract |
| The stopped v2.0.0 run has a structurally valid candidate prefix | Read-only local audit on 2026-08-14 verified safe structural fields, boundary JSON parsing, known artifact names, and matching installed identities | Supports attempting the narrow importer | Private local evidence is not published and does not prove resistance to coordinated tampering |
| Short-run percentages do not measure practical checkpoint cost | Three-pair match-heavy and sparse synthetic CSVs under `benchmarks/results` measured 23.39 and 15.61 percent median overhead but only about 1.28 and 0.45 seconds of added time; canonical parity passed | Falsifies the original eight-percent-only gate and supports an absolute-or-relative margin | The corrected five-pair run passed both overhead margins |
| A fixed byte size does not prove one minute of reusable work | The first corrected 8 GiB run measured 34.820 reusable seconds, 79.40 percent of fresh work, and 19.354 seconds saved; parity and reused-byte identity passed | Fails workload qualification without weakening the 60-second or 30-second margins | Freeze 24 GiB before the next run, based on the measured scan rate |
| Stage count does not measure reusable work | The same CSVs measured only 5.23 and 15.94 percent median resume saving, about 0.33 and 0.52 seconds, after interruption at checkpoint 10 | Falsifies the original 65-percent promise and supports selecting an interruption by measured committed-stage time | Short synthetic runs do not prove that resume saves useful time on a long run |

## Strongest detractor and resolution

The strongest usability case is safe automatic resume. A user could repeat the
original command, and exact identity checks could refuse every mismatch.
`bulk_extractor` shows that automatic restart is familiar in forensic work.

The detractor found that automatic behavior still changes the meaning of an
ordinary analysis command from “start” to “continue and mutate old output.” A
single short `-r` makes that intent explicit and gives scripts a stable
must-resume-or-fail operation. The synthesizer therefore selected explicit
resume even though the external reviewer preferred a validated hybrid.

The strongest importer objection is a mutually consistent truncated prefix.
An actor could truncate native, raw, assessment, and candidate files together
so their relationships still pass. Re-extracting native data could detect that
case, but it would repeat the work that recovery is meant to preserve. It would
also not authenticate unsigned local state against an actor who can alter the
run, its importer, or its software environment.

The accepted threat model is accidental local interruption on a controlled
forensic host. It is not coordinated post-run tampering by another local
writer. Full input verification, exact installed-kit identity, stage atomicity,
the known artifact prefix, and relational streaming checks address that
bounded threat. Examiners who cannot trust the result directory must preserve
it for diagnosis and start a new run.

The strongest performance objection is that an absolute allowance can hide a
large percentage regression on a small job, while a relative allowance can
hide a long delay on a large job. The corrected new-run gate therefore uses the
larger of a one-second human-visible allowance and a five-percent batch-cost
allowance, and it applies independently to sparse and match-heavy fixtures.
This does not excuse the observed 1.28-second match-heavy result. The strongest
resume objection is that a low-cost late checkpoint can manufacture a poor or
excellent percentage. The corrected resume gate first requires at least one
minute of measured reusable stage work and then requires preservation of at
least half of that work as wall-clock saving.

The public v2.0.0-to-candidate comparison is also a release-regression test,
not a causal measurement of checkpoint code alone. Other candidate changes can
affect its delta. A failure still blocks the candidate, but it must not be
attributed to resume without a same-source isolation measurement. The remaining
countercase is that five percent can be a long absolute delay on a multi-hour
run. The gate therefore requires reporting seconds as well as percentages; the
five-percent margin is an explicit proportional-cost decision, not a claim that
the absolute delay is invisible.

## Acceptance gates

### Correctness and forensic gates

- Compare uninterrupted and resumed runs for every engine combination. All
  evidence payloads, ordering, lineage, and reports must agree. Only declared
  attempt and timing provenance may differ.
- Fault-inject before output publication, after publication, before checkpoint
  flush, and after checkpoint publication for every stage. Resume must never
  skip the interrupted stage or expose a false complete result.
- Kill the parent and each child process during active work. No abandoned
  cache, partial, or later-stage artifact can authorize reuse.
- Change one input byte, option, engine, model, executable, kit file, policy,
  checkpoint, and committed artifact in separate tests. Every case must refuse
  before evidence output changes.
- Test cancellation during preflight and each resumed stage. The last valid
  checkpoint and all earlier committed artifacts must remain unchanged.

### Concurrency and path gates

- Start two resume processes and a resume process beside a new analysis for the
  same result directory. Exactly one process can hold the lease; every other
  process fails before mutation.
- Test a stale lease file, reused process identifier, hard link, reparse point,
  case alias, moved result directory, and result path inside evidence or the
  kit. Ambiguity must fail closed.
- Test checkpoint temporary-file interruption and final-name move failure. The
  prior checkpoint chain must remain usable or the run must refuse without
  cleanup outside its exact owned paths.

### Legacy importer gates

- Validate the exact supported v2.0.0 fixture with streaming, bounded-memory
  validators and confirm that translation reruns from zero with decoding off.
- Remove, add, truncate, reorder, duplicate, or alter one row or artifact at a
  time. Each defect must refuse import before any original byte changes.
- Refuse a custom regular expression, missing exact kit identity, summary,
  checkpoint, translation output, translation cache or partial, decoder output,
  report, unknown file, and every unsupported version or schema.
- Cancel each importer validation pass. The original directory must remain
  byte-for-byte unchanged.

### Performance and progress gates

- Run at least five alternating pairs for both a sparse scan-heavy fixture and
  a match-heavy output fixture. Compare the pre-resume baseline and candidate,
  and require canonical output parity before using any timing. For each fixture,
  median added new-run wall time must not exceed the larger of 1.0 second or 5
  percent of its median baseline wall time. Report seconds and percentages.
- Use a separate long synthetic workload for resume value. Select the
  interruption by measured stage time, not stage count: the stages that resume
  will reuse must account for at least 60 seconds and at least 50 percent of the
  median fresh candidate run. Run at least five alternating fresh/resume pairs,
  including validation and input rehashing.
- Require canonical output parity and unchanged committed artifact bytes. The
  median wall-clock saving, `fresh - resume`, must be at least the larger of 30
  seconds or 50 percent of the median measured time spent in the reused stages.
  Record the interruption checkpoint, reused-stage names and timings, fresh and
  resume seconds, absolute saving, and percentage saving.
- Verify that progress never decreases within an attempt, clearly marks reused
  stages, resets the interrupted stage, and does not present advisory progress
  as completion.

### Privacy, documentation, and release gates

- Scan checkpoints, leases, progress, logs, fixtures, documentation, and the
  outgoing diff. Repository content must contain only synthetic case data and
  no private path, hostname, filename, hash, extracted value, or report.
- Confirm no network access or telemetry during new-run checkpointing, resume,
  validation, or legacy import.
- Verify terminal help, README, command reference, completion guide, schemas,
  and tests use the same explicit command and failure rules.
- Build and independently test the complete Windows kit. Resume must bind the
  same verified bundle identity as a new analysis.

## Falsifiers and revisit triggers

Reject or roll back resume if any crash can advance past an uncommitted stage,
if any drift or second writer reaches mutation, if resumed evidence differs
from uninterrupted evidence, or if incomplete output can become complete
without every final gate.

Reject the legacy importer if its full validators exceed bounded memory, accept
an unknown artifact, reuse any translation work, or cannot preserve the old
directory on refusal and cancellation.

Revisit record-level resume only if translation-stage reruns still dominate
measured recovery time and a separate decision proves an authenticated commit
protocol with unchanged ordering and lineage.

Revisit automatic resume only if usability measurements show repeated
explicit-command failures and a new review proves that automatic mutation is
clear to examiners and scripts.

Do not weaken a failed performance margin to fit an observed result. Revisit
the one-second, five-percent, one-minute, and one-half margins only with a new
decision that names a user-visible cost model and new fixtures before timing.

Revisit the tamper threat model if bstrings adds signed result state, remote
writers, shared result storage, or operation on a host that is not controlled
by the examiner.

## Rollback

Before publication, remove the resume option and checkpoint writes, restore the
new-or-empty output rule, and keep all incomplete directories untouched. Do not
convert or delete a checkpoint during rollback.

After publication, disable resume and the importer in a new patch release if a
correctness or safety gate fails. The patch must refuse existing checkpoints
and direct the examiner to preserve the old directory and start a new run. An
immutable release is never rewritten.

## Consequences

Users can preserve an interrupted result directory and resume it with one short
option. Completed stages can save substantial time. The current interrupted
translation stage still starts again.

The runtime gains checkpoint schemas, feature-owned validators, an exclusive
lease, attempt provenance, progress reconciliation, compatibility code, fault
injection, and performance gates. These mechanisms add code and validation I/O
to every analysis.

The first two performance runs passed correctness but failed their registered
timing gates. The first corrected run passed overhead and correctness but its
8 GiB long fixture did not contain the required minute of reusable work. The
architecture remains accepted, but performance acceptance is pending a fresh
run with the preregistered 24 GiB fixture and unchanged margins.

The narrow importer can recover one known v2.0.0 prefix without creating a
general promise to repair old output. Its residual protection matches the
existing controlled-host threat model and is disclosed.

## Review record and rotations

The three first-round reviews were independent:

1. The external reviewer inspected `bulk_extractor`, Plaso, GNU ddrescue, and
   Spark behavior for restart, identity, checkpoint durability, and output
   consistency.
2. The runtime reviewer traced CLI requirements, stage order, atomic outputs,
   translation cleanup, input and bundle identity, current schemas, and the
   safe structure of the stopped run.
3. The detractor tested automatic mutation, partial-stage reuse, consistent
   truncation, concurrent writers, configuration drift, cancellation, privacy,
   benchmark value, and rollback.

The external reviewer rotated onto the runtime proposal. It first preferred a
hybrid automatic design, then required exact recognition and a deterministic
native re-extraction for legacy import.

The runtime reviewer rotated onto the external evidence. It confirmed that
`bulk_extractor` is a usability precedent but rejected its missing input and
configuration binding. It retained whole-stage boundaries and non-resumable
translation work.

The detractor rotated onto both proposals. It selected explicit `-r` intent,
required an operating-system-held lease, full drift validation, immutable
attempt history, crash windows, and the strict legacy allowlist.

The synthesizer accepted explicit whole-stage resume. It rejected native
re-extraction because it defeats the bounded recovery goal and does not solve
unsigned coordinated local tampering. That disagreement is resolved by naming
the narrower accidental-interruption threat model and requiring a new run when
the result directory is not trusted.

After implementation, the performance detractor rotated onto two failed
synthetic runs. It rejected both percentage-only margins: their short
denominators exaggerated small absolute costs, while their fixed checkpoint did
not prove reuse of expensive work. The external reviewer selected the corrected
absolute-or-relative new-run margin and a work-normalized resume margin before
another benchmark run. No failed result was reclassified as acceptance.

The first corrected run then passed both new-run overhead margins and all
correctness checks. Its 8 GiB long fixture failed the registered work-duration
qualification. The result was retained as failed evidence. The next 24 GiB
fixture and the unchanged gates were recorded before another timing run.

## Primary references

- [bulk_extractor v2.1.1 manual](https://github.com/simsong/bulk_extractor/blob/v2.1.1/man/bulk_extractor.1)
- [bulk_extractor v2.1.1 restart implementation](https://github.com/simsong/bulk_extractor/blob/v2.1.1/src/bulk_extractor_restarter.h)
- [GNU ddrescue manual](https://www.gnu.org/software/ddrescue/manual/ddrescue_manual.html)
- [Plaso extraction CLI](https://github.com/log2timeline/plaso/blob/main/plaso/cli/extraction_tool.py)
- [Spark Structured Streaming programming guide](https://spark.apache.org/docs/latest/streaming/apis-on-dataframes-and-datasets.html)
- [.NET `FileShare` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.fileshare)
- [.NET `FileStream.Flush(Boolean)` documentation](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush)
- [Current output and completion contract](../output-and-provenance.md)
- [Translation integrity decision](adr-0005-translation-integrity-and-run-dedup.md)
- [CUDA translation decision](adr-0006-q4-cuda-full-translation.md)
