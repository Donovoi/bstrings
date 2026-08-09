# ADR-0003: Persist verified bytes and batch full releases

- **Status:** Accepted
- **Date:** 2026-08-09
- **Scope:** Windows quality installer, split-pack acquisition, GitHub Actions caching, version policy, and full quality release cadence
- **Decision type:** release architecture, supply-chain integrity, performance, and user installation
- **Review method:** Robin round under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source release and cache research, repository and runtime audit, adversarial forensic detractor, performance and operations measurement, rotated critiques, and synthesis
- **Implementation state:** bounded implementation complete and locally verified; release-duration measurements remain pending, and compiled-layer or acceptance-evidence reuse remains deferred

## Context

The v1.9.12 quality installer transfers 9,485,286,013 bytes on a cold run. The
locked Hy-MT2 model is 7,981,928,896 bytes, or 84.2 percent of that transfer.
The model, configuration, and licence were byte-identical between v1.9.11 and
v1.9.12, but the default installer cache was scoped to the release tag and
deleted after a successful installation. A normal upgrade therefore could not
reuse bytes it had already authenticated.

The release workflow had a separate scope error. v1.9.11 and v1.9.12 generated
two Actions caches with the same logical key and respective sizes of
8,710,891,470 and 8,710,891,438 bytes. Both missed because sibling tag caches
cannot restore one another. The v1.9.12 tag workflow took 98 minutes 17
seconds: 65 minutes 51 seconds in the hosted build and 20 minutes 46 seconds
in self-hosted acceptance, plus runner wait and final publication. The model
was downloaded in the hosted build and then deliberately downloaded again by
self-hosted acceptance.

Finally, the project required every product, test, build, installer, or
workflow change to advance the product version. Every small change therefore
prepared a tag even when maintainers wanted to accumulate several tested
changes into one release. Full tag gates remain necessary because the public
base pack includes the current executable and selected documentation, notices,
and release material; a path allowlist is not a safe substitute.

## Considered options

1. **Continue cold, per-tag acquisition and release every code change.** This
   is the simplest baseline but repeats transfers and consumes over an hour for
   each tagged batch. Rejected.
2. **Treat a cache hit or previous acceptance report as verification.** This
   would be fast but makes mutable cache metadata authoritative and can replay
   stale tag, commit, or run evidence. Rejected.
3. **Skip installation when the destination already verifies.** This avoids
   all pack work for an exact rerun, but conflicts with the user-visible
   refresh contract that the installer stages a complete replacement and
   overwrites the destination. Deferred; it is not part of this decision.
4. **Persist immutable input bytes, re-hash every use, and retain fresh
   current-tag assembly and acceptance.** Use digest-and-length identities,
   private partial downloads, atomic publication, exact verification, and a
   cold fallback. Accepted.
5. **Reuse compiled bundles, stable layers, or top-level acceptance evidence
   across tags.** The present base pack contains release-derived bytes and the
   evidence is bound to repository, tag, commit, and run. Deferred pending a
   separate material-input manifest, provenance schema, equivalence study, and
   Robin round.
6. **Batch ordinary changes without weakening tag gates.** Equal project
   versions are allowed on ordinary pull requests and master pushes. A
   deliberate forward version bump starts release preparation and the tag
   still executes every complete gate. Accepted.

## Decision

The installer retains authenticated download bytes by default. Release assets
remain in tag-specific storage, while bundle packs use an immutable
content-addressed identity containing schema, kind, byte length, and SHA-256.
Every cache hit is treated as hostile input: the exact bytes are opened,
length-checked, and fully SHA-256 hashed before assembly. Missing, corrupt,
linked, locked, or ambiguous entries take the normal authenticated download
path or fail closed. Downloads use invocation-private partial names and publish
an object atomically only after exact verification. The installer continues to
assemble a fresh sibling tree, verify it, replace any existing destination,
and verify the installed result. It does not hard-link installed evidence into
the cache.

The default cache is shared across release tags and survives success. An
explicit cleanup option removes only the invocation-owned verified cache after
success. Existing cache-selection options remain compatible. When a new
installer first encounters an older exact installed model and has no shared
object, it may import that physical file only after the current trust manifest
identifies the same target, byte length, and SHA-256; the copied object is
hashed again before publication. This permits the first upgrade after this
change to avoid re-downloading the unchanged model while still refreshing the
whole destination.

The hosted release build restores an exact default-branch cache of immutable
component bytes. Cache writers are limited to trusted default-branch/manual
workflows, cache keys have no prefix fallback, and tag jobs do not upload
duplicate tag-scoped caches. Restored bytes remain non-authoritative and pass
the existing current-lock and network-blocked validation. Ordinary manual
dispatch runs fast CI unless an explicit full-offline input is selected. The
self-hosted tag acceptance remains fresh per run and continues to download the
model from its canonical URL, assemble a new bundle, verify it, run translation
smoke, and generate new same-run evidence. This preserves upstream acquisition
coverage while the hosted build benefits from cache reuse.

Ordinary code, test, build, installer, and workflow changes may retain the
current project version. A version decrease is always rejected. A forward
version change is an explicit release-preparation change: its tag must not
already exist and all installer pins, release notes, documentation, and release
assets must agree. The automatic draft workflow remains a no-op while the
version is unchanged. Every actual version tag still performs the complete
quality build, verification, smoke, fresh acceptance, exact evidence binding,
and immutable publication.

## Non-negotiable invariants

1. Cache metadata, cache keys, filenames, timestamps, and prior success reports
   never authorize bytes. Exact current-manifest length and SHA-256 verification
   is mandatory on every materialized hit.
2. The installer authenticates the exact published, non-draft,
   non-prerelease, immutable GitHub release and binds the installer, checksums,
   core, trust manifest, and pack URLs to that release.
3. Pack objects are regular physical files under a bounded physical cache;
   reparse points, links, path escapes, and uncontrolled deletion are rejected.
4. Concurrent acquisitions cannot share a writable partial file. Verified
   content-addressed objects are never modified in place or silently replaced.
5. Installation always uses a new staging directory, complete verification,
   destination replacement, installed-path verification, and exact rollback on
   failure. Cache reuse cannot preserve stale destination files.
6. A cache miss, eviction, poison, interruption, or clean machine remains a
   supported path. It downloads and verifies exact inputs without reusing
   acceptance evidence.
7. Each release tag uses the current core and current release-owned packs and
   creates fresh top-level evidence bound to repository, tag, commit, workflow
   run, attempt, asset hashes, checksum file, and model identity.
8. No ordinary-change classifier may waive a tag gate. Batching controls when
   a version is released; it does not reduce what a release proves.
9. Cache reuse introduces no case-data egress, telemetry, mutable model URL,
   or online dependency into analysis. Only public release components are
   cached.
10. Installer, CLI/TUI help, progress, release maintenance instructions, and
    version policy describe the actual cache lifetime and release trigger.

## Acceptance gates

- **Installer cold/warm gate:** a cold installation and a warm installation
  produce the same verified bundle identity, file count, total bytes, and
  manifest hash. A same-release second run and an unchanged-model upgrade
  transfer zero model body bytes while still performing staging, replacement,
  and both verification passes.
- **Poison gate:** right-size/wrong-hash, truncated, stale-valid-digest,
  wrong-kind, linked, case-colliding, locked, and interrupted objects are
  rejected or reacquired before assembly. A one-byte installed-model mutation
  cannot seed the shared object.
- **Concurrency gate:** two releases acquired sequentially in both orders and
  concurrently against one shared cache produce their own exact outputs with
  no shared partial file, mixed generation, or cache thrash.
- **Compatibility gate:** the installer and bundle tests pass under Windows
  PowerShell 5.1 and PowerShell 7; explicit cache directories still work; the
  cleanup option removes only bounded owned cache state.
- **Release gate:** tag builds still perform current-tag complete assembly,
  full bundle verification, OCR and translation smoke, fresh self-hosted
  acceptance, exact eight-asset/evidence validation, and immutable publication.
- **Batching gate:** documentation-only and unversioned code changes pass CI;
  a lower version, a malformed version, or a forward version whose tag already
  exists fails; a valid unused forward version is recognized as release
  preparation.
- **Measured performance gate:** record network bytes and phase durations for
  three paired cold/warm runs. Warm installer runs must transfer zero unchanged
  model bytes. Retain CI cache complexity only if the paired median complete
  tagged workflow improves by at least 30 percent from the v1.9.12 98-minute
  baseline, or a separately recorded bandwidth/storage objective justifies it.
  Ordinary unversioned master changes must complete only the normal fast CI
  lane and must not create a tag or release draft.

## Strongest detractor and resolution

The strongest objection is that the model is 84.2 percent of transferred bytes
but not necessarily 84.2 percent of elapsed time. Full hashing, copying,
llama.cpp compilation, assembly, verification, inference, runner queueing, and
publication remain. A global mutable pack directory could also introduce
cross-version partial-file races, and a persistent acceptance cache could stop
proving that the upstream model URL works.

The decision does not claim byte savings as measured time savings. It requires
paired phase measurements and a retention threshold. It uses immutable
digest-addressed objects and invocation-private partials instead of a flat
shared mutable pack directory. It keeps the self-hosted acceptance acquisition
cold for every tag, so upstream model delivery remains on the release gate.
Most importantly, it never reuses assembly, smoke, top-level evidence, or a
verification decision. If measured end-to-end benefit is small, batching still
removes unnecessary releases and the CI cache portion is removed without
changing the forensic contract.

## Falsifiers and revisit triggers

Disable shared reuse immediately if a cached and cold run produces unexplained
differences in public asset hashes, final manifest rows, smoke outcomes, or
evidence fields other than run identity and time; if poisoned or linked content
reaches assembly; if concurrent releases interfere; if a cache hit avoids full
byte hashing; or if rollback no longer restores the exact previous kit.

Run three paired cold/warm tag builds and compare download, hash, compile,
assembly, split, verification, acceptance, inference, queue, total time, peak
disk, and network bytes. Remove or redesign the CI cache if it fails the
performance gate, exceeds repository cache capacity, repeatedly evicts itself,
or restores no faster than direct acquisition. Revisit content-store retention
if the required local free-space boundary becomes misleading or cache cleanup
cannot remain bounded.

Reopen stable compiled-layer reuse only after a separate ADR binds every
observed build input, toolchain, RID, licence/notice generator, pack schema, and
producer identity; survives replay and mutation corpora; and passes at least
three cold-versus-reuse shadow releases. Revisit per-tag cold upstream
acquisition only if a separately accepted liveness and durable provenance
contract provides equivalent evidence.

## Consequences

Users keep a verified public-component cache beside the installation and avoid
re-downloading the 7.98 GB model when it is unchanged. Setup still refreshes
and overwrites the destination from a completely verified staged bundle. The
tradeoff is roughly eight additional gigabytes of persistent disk use; an
explicit cleanup option is provided and documented.

Maintainers can merge and test several ordinary changes under one version,
then make one explicit release-preparation change. A real tag remains expensive
because its correctness work is preserved, but it no longer follows every
small change and avoids duplicate hosted model transfer/cache upload on an
exact warm cache. The self-hosted acceptance remains deliberately cold and may
still take about 20 minutes.

The implementation adds content-store and lifecycle tests, a default-branch
cache-warming path, an explicit full-offline dispatch input, and updated release
documentation. It does not change analysis output, evidence provenance,
hardware selection, or the single Full profile.

## Implementation and measured verification

The v1.9.13 implementation uses content-addressed pack objects with an
exclusive digest-bound lease, a parked resumable partial, an
invocation-private active partial, no-overwrite atomic publication, exact
legacy-cache import, and optional exact installed-file seeding. The PowerShell
installer retains the shared cache by default, scopes release assets by tag,
requires an immutable release, and keeps the fresh stage/swap/rollback path.
The hosted workflow has separate restore and trusted master-save steps, while
the project-version and draft workflows implement ordinary equal-version
batching.

Local verification passed 616 .NET tests with one host-capability link skip,
359 Python tests with three host-capability link skips, four Rust tests plus
format and Clippy, installer and README bootstrap suites under Windows
PowerShell 5.1 and PowerShell 7, workflow/version/decision gates, PowerShell
syntax checks, and 101 repository Markdown links. Focused cache tests cover
poisoned and truncated objects, interrupted cross-invocation resume, parallel
ownership with one download, exact and mutated installed seeds, legacy import,
and output verification.

The real 7.98 GB warm-upgrade transfer and three paired tag-duration benchmark
runs cannot occur before the v1.9.13 assets and default-branch cache exist.
Those measurements remain a post-merge release gate and follow-up retention
decision; no unmeasured wall-time saving is claimed here.

## Primary references

- [GitHub dependency caching reference](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching), including tag/default-branch scope, eviction, and the requirement to treat cache contents as untrusted.
- [GitHub dependency caching concepts](https://docs.github.com/en/actions/concepts/workflows-and-actions/dependency-caching), which distinguishes cache acceleration from workflow artifacts and warns about cache trust.
- [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases), which locks release tags and assets and generates release attestations.
- [GitHub workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax), including the fact that path filters are not evaluated for tag pushes.
- [SLSA provenance v1.1](https://slsa.dev/spec/v1.1/provenance), including digest-bound resolved dependencies and output subjects.
- [User-facing download and cache behavior](../download-and-install.md).
- [Offline build, acceptance, and release procedure](../offline-release-maintenance.md).
