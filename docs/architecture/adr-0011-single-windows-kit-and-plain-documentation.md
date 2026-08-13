# ADR-0011: Use one Windows kit and plain public documentation

- **Status:** Accepted
- **Date:** 2026-08-13
- **Scope:** Windows installer, installation and cache paths, release assets, bundle identity, migration, public documentation, and release acceptance
- **Decision type:** release architecture, compatibility, installation safety, and user experience
- **Review method:** Robin round under the [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source external research and documentation review; repository and runtime audit; adversarial installer-safety detractor and compatibility review
- **Implementation state:** implementation and documentation in progress for the next major release; public v1.9.17 remains immutable

## Context

bstrings had one advertised complete Windows installation, but its public names
used `quality`, `core`, and `profile`. These names implied that users could
choose another kit size or capability tier. The repository also described the
base application archive as a usable installation even though that archive did
not contain all supported engines, models, and licenses.

The complete installer used `Install-BstringsQuality.ps1`, the default directory
`bstrings-quality`, and several `*-quality.json` assets. The component lock had
one translation choice named `quality` and also stored the same model under
`components.translationModel`. The duplicate objects were byte-for-byte equal.
Thus, the selector did not represent a real user or maintainer choice.

The README had 355 lines and approximately 2,205 words. Its `Why use it?`
section had 329 words. It mixed first-run instructions with internal routing,
cache, CUDA, schema, and benchmark detail.

The public v1.9.17 release is immutable. Its tag and assets cannot be renamed.
Its historical commands must remain accurate. The new names therefore require
a new major release.

## Considered options

1. **Change only descriptive text.** Keep all existing filenames, paths, and
   schemas. This leaves tier signals in normal commands and release assets.
   Rejected.
2. **Rename `quality` to `full`.** This replaces one tier word with another and
   keeps a one-item model selector. Rejected.
3. **Publish old and new installer aliases.** This preserves download URLs but
   leaves two installer names and weakens the one-kit message. Rejected.
4. **Use `bstrings` as the default directory.** This is short, but the source
   repository already has a `bstrings` project directory. The existing
   replacement logic could move and later delete unrelated source content.
   Rejected.
5. **Use one neutral kit identity and no aliases.** Rename public files, remove
   the one-item model selector, protect destination ownership, and retain a
   bounded legacy migration path. Accepted.
6. **Rewrite historical release notes and old decisions.** This would make old
   commands inaccurate and alter the evidence record. Rejected.

## Decision

The next major release has one complete Windows kit. Public text does not call
it a quality, core, small, large, lite, or full kit.

The public installer is `Install-Bstrings.ps1`. Its default destination is
`bstrings-kit`. Its default cache is `.bstrings-installer-cache`.

The public trust files use neutral names:

- `bundle-packs.json`;
- `airgap-config.json`;
- `airgap-manifest.json`; and
- `Hy-MT2-Apache-2.0.txt`.

The neutral trust identity is `windows-x64-offline-v3`. A generic `profile`
field can remain in the trust manifest because it gives cache and schema
identity. It does not select a tier.

Schema versions change only when the document structure or meaning changes.
The component lock and generated configuration use schema 2. The bundle
acceptance record uses schema 3. These documents remove profile collections or
selectors and replace profile rows with one bundle record.

The pack trust manifest and final file manifest keep schema 1. Their structures
do not change. The exact `windows-x64-offline-v3` trust identity rejects the old
pack set, and the final manifest still contains only path, size, and SHA-256
rows. Increasing either schema would add consumer changes without identifying
a new document structure.

The component lock contains one `translationModel` object. Build scripts do not
accept a translation-profile selector. Generated configuration does not contain
`translationProfile`. Model ID, revision, URL, size, SHA-256, and license remain
mandatory.

The release can contain a base archive and split packs. These files are
installer components. They are not separate installations. The base archive
contains [BASE_PACK_NOTICE.md](../../BASE_PACK_NOTICE.md) instead of a user
installation guide.

`--full` remains a command option. Documentation calls it the complete analysis
preset. It controls engines in a run and does not select an installed kit.

The internal release evidence is `offline-bundle-acceptance.json`. Its schema
records one bundle instead of a list of named installation profiles. Workflow,
artifact, checker, and field names change together.

The README uses principles from ASD-STE100 without claiming formal compliance.
It uses active voice, short sentences, one topic per paragraph, direct commands,
and vertical lists. Technical terms remain when they are necessary.

Historical release notes and accepted decisions keep their original names and
facts. Current documents can state that v1.9.17 uses historical names and link
to its immutable release page.

## Non-negotiable invariants

1. There is one supported Windows installation. Installer parts are never
   advertised as another product.
2. The installer never replaces an arbitrary physical directory. It requires
   unambiguous installer-owned or verified-installation identity.
3. The default `bstrings-kit` path cannot destroy a source checkout or unrelated
   user directory.
4. Migration starts only for the default destination when the new destination
   is absent. It stops if old and new destinations both exist.
5. Every reused old-install or old-cache object passes current size and SHA-256
   checks. A filename, timestamp, cache hit, or old success does not authorize
   bytes.
6. Migration builds and verifies the new kit before removing the old path. All
   swap failures retain an exact rollback path.
7. An explicit destination never starts implicit legacy migration.
8. v1.9.17 remains unchanged. Historical instructions retain the commands that
   worked for their release.
9. Model and bundle provenance retain exact identity after selector removal.
10. Public documentation retains verification, completion, evidence-safety,
    and image-carving boundaries.
11. The release remains private-case-data free.

## Acceptance gates

- **Terminology gate:** Scan current README, CLI help, installer output, active
  guides, workflow labels, assets, and schemas. No retired kit-tier name can
  remain. Unrelated uses such as OCR quality measurements can remain.
- **README measurement gate:** The README has no more than 900 words. A
  descriptive sentence has no more than 25 words. A paragraph has no more than
  six sentences. Required first-run headings and safety statements remain.
- **Installer safety gate:** Test a clean install, verified replacement,
  arbitrary-directory refusal, and a repository-like `bstrings` directory.
  Every refusal occurs before a destructive move.
- **Migration gate:** Test old-only, new-only, both-present, explicit path,
  linked path, poisoned cache, changed model, interruption, each swap failure,
  rollback, and bounded cleanup.
- **Reuse measurement gate:** A warm migration of an unchanged model transfers
  zero model body bytes. Both source and copied cache objects still receive full
  size and SHA-256 checks.
- **Shell gate:** Run installer tests with Windows PowerShell 5.1 and PowerShell
  7.
- **Bundle gate:** Build, assemble, verify, and smoke the one kit. Confirm that
  the trust identity, model identity, file count, byte count, and manifest hash
  agree across the installer and acceptance evidence.
- **Release gate:** Confirm exact commit, new major tag, immutable state, asset
  inventory, checksums, public installer, and independently downloaded kit.
- **Documentation gate:** Check every first-party Markdown link and confirm that
  current examples use `bstrings-kit` and the neutral installer name.
- **Privacy gate:** Scan all outgoing content for private paths, hostnames, IP
  addresses, filenames, hashes, reports, and extracted values.

## Strongest detractor and resolution

The strongest objection is that renaming the installer breaks scripts that
download its old asset URL. Renaming the directory can also leave two
installations on an upgraded computer. A compatibility alias would reduce that
breakage.

The alias would preserve the exact tier term that this decision removes. It
would also create two public installer assets for one product. The project
therefore makes the break explicit in a major release and does not publish an
alias.

The immutable v1.9.17 release remains available with its exact old commands.
The next installer has one bounded migration path for the former default
installation and cache. It stops on ambiguity and never treats old bytes as
trusted.

A second objection is that `bstrings` is a simpler directory name than
`bstrings-kit`. Repository audit showed that `bstrings` collides with the source
project directory. `bstrings-kit` is slightly longer but removes this direct
destructive-path risk.

## Falsifiers and revisit triggers

Stop publication if the installer can replace an unrelated directory, if a
legacy object reaches assembly without full current verification, or if any
failed migration loses the prior installation.

Revisit the migration design if both old and new paths cannot be resolved
without user choice, if warm migration downloads unchanged model bytes, or if
cache import crosses a link or path boundary.

Revisit the schema change if removing `translationProfile` loses model identity
or makes two different bundles share one cache identity.

Reopen an installer alias only if measured support failures show that the major
release notice and historical release page are insufficient. Any alias needs a
new decision because it conflicts with the one-name rule.

Reject the README rewrite if users cannot complete install, verification, first
analysis, engine exclusion, or completion checks from the short guide and its
first-level links.

Roll back before publication by restoring the former current-source names and
schema. Do not roll back by modifying v1.9.17. After publication, correct a
material fault in a new version.

## Consequences

Users see one installer, one directory, and one kit. They can still select or
exclude engines for each analysis.

Maintainers no longer choose a one-item translation tier. Release scripts and
acceptance evidence become simpler, but the major cutover changes many exact
asset and schema names at once.

Existing unattended download scripts must change for the new major release.
The old immutable release remains reproducible. Verified old installations and
caches can reduce migration time without weakening current byte checks.

The README becomes a short onboarding guide. Detailed architecture, benchmarks,
provenance, hardware, and maintenance information remains in linked documents.

## Review record

The three first-round reviews were independent:

1. External research reviewed ASD-STE100, Microsoft technical-writing guidance,
   immutable release behavior, and the current public release.
2. Runtime audit traced installer, component-lock, configuration, trust,
   workflow, acceptance, and cache dependencies.
3. The detractor tested naming compatibility, destructive destination paths,
   ambiguous migration, historical accuracy, and missing README safety facts.

The external reviewer then challenged the runtime proposal. It accepted removal
of the duplicate selector but required schema revisions for changed documents,
full model identity, and coordinated acceptance changes. Integration review
then corrected an over-broad reading of that requirement: the unchanged pack
trust and final file-manifest structures remain at schema 1. Their exact v3
trust identity provides the required discriminator. The component lock,
configuration, and acceptance record receive their explicit schema revisions.

The runtime reviewer challenged the documentation proposal. It required the
short README to retain completion, sensitive-output, failed-output, and raw-image
limits.

The detractor challenged both reviews. It rejected `bstrings` as the default
directory, rejected public aliases, and required fail-closed migration plus
rollback tests.

No material disagreement remains. The main tradeoff is deliberate public-name
breakage in exchange for one clear product identity.

## Primary references

- [ASD-STE100 Issue 9](https://www.asd-ste100.org/assets/files/ASD-STE100_ISSUE9.pdf), including active voice, one topic, vertical lists, 20-word procedural sentences, and 25-word descriptive sentences.
- [ASD-STE100 official FAQ](https://www.asd-ste100.org/STE_faq.html), which distinguishes useful STE principles from a claim of full standard compliance.
- [Microsoft guidance for scannable content](https://learn.microsoft.com/en-us/style-guide/scannable-content/), which recommends short headings, sentences, paragraphs, and simple words.
- [Microsoft Learn style quick start](https://learn.microsoft.com/en-us/contribute/content/style-quick-start), which recommends short and consistent sentence structures.
- [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases), which states that published immutable tags and assets cannot change.
- [Semantic Versioning 2.0.0](https://semver.org/), which requires a major version for incompatible public API changes and forbids changing released contents.
- [Public v1.9.17 release](https://github.com/Donovoi/bstrings/releases/tag/v1.9.17), which shows the immutable historical release and its published names.
- [Current installer procedure](../download-and-install.md).
- [Release maintenance procedure](../offline-release-maintenance.md).
- [Version policy](../../VERSIONING.md).
