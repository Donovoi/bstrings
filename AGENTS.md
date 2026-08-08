# Repository decision policy

These instructions apply to every coding agent working in this repository.

## High-level changes

A change is high level when it changes any of the following:

- forensic coverage, evidence preservation, provenance, or report schemas;
- pipeline stage ordering, routing, defaults, or failure behavior;
- engine, model, dependency, or hardware-selection policy;
- security, air-gap, installer, release, or acceptance guarantees; or
- an architecture decision spanning three or more runtime/build subsystems.

Before implementing a new or changed high-level decision, create or update a
numbered record at `docs/architecture/adr-NNNN-short-title.md`. Do not treat agent
agreement as evidence. The record must contain primary-source evidence,
reproducible measurements where applicable, the strongest contrary case,
explicit falsifiers, and rollback criteria.

Use at least three independent first-round reviews:

1. primary-source and external-practice research;
2. repository/runtime implementation audit; and
3. adversarial detractor, benchmark, and falsifier design.

Keep the initial reviews independent. Then rotate critiques so each reviewer
challenges another review before synthesis. Record unresolved disagreement.
When evidence remains split, prefer a reversible experiment over changing the
default.

The implementing agent must run:

```powershell
./tools/decisions/tests/Test-DecisionRecords.ps1
./tools/decisions/Test-DecisionRecords.ps1 `
  -BaseRevision <base-sha> -HeadRevision <head-sha> `
  -PullRequestBody '<pull-request body>'
```

Ordinary fixes, tests, documentation corrections, dependency-preserving
refactors, and other changes that do not make a high-level decision do not need
an ADR. A changed numbered ADR or a checked `High-level decision` declaration in
the pull-request template activates the gate.
