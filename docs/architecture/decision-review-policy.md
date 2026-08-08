# High-level architecture decision review policy

## Purpose

Changes that can alter forensic coverage, evidential meaning, provenance,
determinism, privacy, release composition, or large-workload behavior must not
be selected from one model response or one maintainer's intuition. bstrings
uses a **Robin round**: independent advocate, detractor, evidence, and
operational reviews are reconciled against the repository, primary sources,
and measurements before a high-level decision is accepted.

The round is an evidence gate, not a vote. Several agents repeating the same
claim do not make the claim independently verified, and a majority cannot
override an unresolved evidence-loss or provenance risk.

## Decisions that require a round

A round is required before implementing or promoting any change to:

- stage order, engine routing, parsing, carving, decoding, OCR, language
  detection, translation, or pattern semantics;
- the canonical evidence record, report columns, source identity, offsets,
  parent/child provenance, or completion contract;
- a model, runtime, classifier, threshold, confidence policy, or fallback;
- CPU/GPU assignment, workload-size thresholds, concurrency, batching, or a
  performance optimization that can change output;
- the offline trust boundary, external-service use, telemetry, case-data
  handling, or bundled dependency set; or
- a user-facing profile, default, compatibility promise, or release gate.

Mechanical refactors, typo corrections, and tests or documentation that do not
change those contracts do not need a new round. If that boundary is uncertain,
run the review.

## Required perspectives

Use at least three independent review passes plus a named synthesizer. More
perspectives may be added when the decision crosses several domains.

| Perspective | Required question |
| --- | --- |
| Advocate | What is the strongest implementation of the proposal and what measurable benefit should it deliver? |
| Forensic detractor | How could it suppress, alter, misattribute, or make evidence irreproducible? |
| Evidence researcher | What do primary specifications, upstream code, papers, and independently reproducible measurements actually establish? |
| Performance and operations | Does it improve the real workload after startup, I/O, memory, failure, air-gap, and support costs are counted? |
| Synthesizer | Which claims survive the detractor pass, what remains unknown, and which gates make the residual risk testable? |

Reviewers should form their initial findings independently before reading one
another's conclusions. One reviewer may cover more than one perspective only
when staffing makes that unavoidable; the ADR must disclose it.

## Review procedure

1. **Frame atomic claims.** State the problem, scope, current behavior, proposed
   alternatives, the do-nothing baseline, assumptions, and affected users.
2. **Pre-register the decision tests.** Define evidence that would support,
   weaken, or falsify each material claim before inspecting favorable results.
3. **Inspect the real implementation.** Trace the relevant code, tests,
   manifests, release gates, logs, and benchmark artifacts. Do not design from
   a summary alone.
4. **Run independent passes.** Gather the strongest support, strongest
   contradiction, serious alternatives, and missing evidence. Prefer primary
   sources and direct measurements.
5. **Build an evidence ledger.** For every decisive item record the claim,
   source or artifact, provenance, direction, entailment, independence,
   limitations, and date/version.
6. **Run the detractor round.** Answer the strongest good-faith objection. If a
   proposed safeguard is important, convert it into a mandatory invariant and
   falsifiable test rather than leaving it as prose.
7. **Synthesize without averaging.** Resolve conflicts claim by claim. Record
   confidence and unknowns; do not turn reviewer counts into probability.
8. **Write an ADR.** Record the chosen option, rejected alternatives,
   invariants, acceptance gates, falsifiers, consequences, and re-evaluation
   triggers.
9. **Implement and independently verify.** A different pass reviews the actual
   diff and executes the gates. The ADR is not proof that the implementation
   conforms to it.

## Non-negotiable decision gates

An architecture decision is not accepted while any applicable gate is
unresolved:

- **Coverage:** the change cannot silently remove baseline evidence or must
  explicitly define and test an approved coverage change.
- **Identity and provenance:** every derived value remains attributable to
  exact input bytes, engine/runtime identity, transformation, and location.
- **Fail-safe behavior:** errors, uncertainty, malformed inputs, resource caps,
  and cancellation produce explicit, atomic, reviewable outcomes.
- **Determinism:** repeatability requirements and permitted variance are stated
  and tested on pinned artifacts.
- **Air-gap and privacy:** no new case-data egress, telemetry, mutable download,
  or hidden external dependency is introduced.
- **Operational truth:** help, reports, progress, documentation, bundle
  manifests, and release tests agree with what the executable actually does.
- **Performance:** speed or utilization claims use the real end-to-end workload
  and cannot waive correctness gates.

If the product owner deliberately accepts a residual risk, the ADR must name
the risk, scope, evidence gap, compensating control, and revisit trigger.

## Decision outcomes

Use one of these explicit outcomes:

- **Accepted:** evidence and mandatory gates support implementation.
- **Experimental:** bounded implementation is allowed, but it cannot become the
  default or a release claim until the listed evidence is obtained.
- **Deferred:** the decision is blocked on named measurements or dependencies.
- **Rejected:** the detractor or acceptance tests show that the proposal does
  not meet the contract.
- **Superseded:** a later ADR replaces the decision and links back to it.

Emergency containment may precede a full round only to stop active evidence
loss, case-data exposure, or release corruption. Keep it minimal and reversible,
record the reason, and complete the round before treating the containment as
the permanent design.

## ADR minimum content

Every high-level ADR must include:

- status, date, scope, owner, review perspectives, and implementation state;
- current behavior and alternatives, including the baseline;
- decision and mandatory invariants;
- evidence for and against, with primary links or repository artifacts;
- acceptance tests, falsifiers, unknowns, and revisit triggers; and
- user, forensic, performance, security, release, and documentation
  consequences.

The first decision governed by this policy is
[ADR-0001: early fail-open content routing](adr-0001-early-fail-open-content-routing.md).
