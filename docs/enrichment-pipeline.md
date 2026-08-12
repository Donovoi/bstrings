# Enrichment pipeline

This document describes the integrated workflow in current source and the
complete v1.9.17 quality release. See
[download and installation](download-and-install.md) before choosing a command,
and never combine assets from different versions.

The integrated workflow finds useful text through several complementary paths,
then applies one pattern catalog without losing where each string came from:

```text
input inventory and SHA-256 identity
  -> selected source producers: native, FLOSS, and/or PDF/OCR
  -> language assessment
  -> selected local translation
  -> built-in and custom regex matching
  -> filterable TSV reports and exact histograms
  -> completion and provenance validation
```

When a complete version-matched quality bundle is installed, the examiner
interface is one command:

```powershell
.\bstrings.exe analyze -d D:\evidence\carved --full -o D:\results\case-01
```

The complete v1.9.17 bundle contains every worker, runtime, model, and dependency
published for that version. It does not ask the user to install or invoke
Python, [Magika](https://github.com/google/magika),
[FLOSS](https://github.com/mandiant/flare-floss),
[RapidOCR](https://github.com/RapidAI/RapidOCR), or
[llama.cpp](https://github.com/ggml-org/llama.cpp) separately. It downloads
nothing during examination.

`--full` means every bstrings stage available in the installed version. It does
not upgrade an older bundle or import features from another channel. It also
does not parse a filesystem or carve
embedded files from a raw disk or memory image. Mount or carve the image first
when filesystem-level or embedded-executable/document coverage is required.
The direct scanner can search raw image bytes, but FLOSS requires a complete
supplied executable and OCR requires a supported image/PDF file.

Full defaults native extraction on and enables routed FLOSS/OCR plus automatic
translation. Each producer remains independently selectable, and an explicit
mode overrides its Full default. The published v1.9.17 binaries include
`--native-extraction`. Runtime selection does not split the installed quality
bundle: that shared physical profile is still verified atomically.

## What each stage contributes

| Stage | Contribution | Important boundary |
| --- | --- | --- |
| Native extractor | ASCII/Unicode strings with byte offsets, using the selected CPU/Rust/GPU path | It is on by default but can be explicitly disabled; it does not reconstruct runtime-decoded values |
| Magika | Probabilistically classifies supplied files and routes likely PE files to FLOSS | It samples file content; it is not complete-byte validation, carving, or malware detection |
| FLOSS | Recovers stack, tight-loop, decoded, and selected language strings from supported PE files | Recovered addresses may be program/virtual locations, not file offsets |
| PDF/OCR | Extracts born-digital PDF text and reads raster text from images/pages | OCR remains probabilistic and script/model dependent |
| Language assessment | Scores whether eligible text likely needs translation | Scores are ranking/gating signals, not calibrated certainty |
| Hy-MT2 | Creates an offline English child while protecting structured identifiers | A translated token is not proof those bytes existed in the source |
| Pattern matcher | Applies the same validated catalog/custom patterns to normalized parents and children | Consequential derived hits must be checked against source evidence |

The orchestrator hashes and inventories inputs before extraction, holds a
verified inventory lease around external stages, verifies inputs again after
processing, validates every child record and count, and publishes results only
after all requested stages complete.

## Useful command combinations

```powershell
# Everything, with automatic OCR and translation decisions
.\bstrings.exe analyze -d D:\carved --full -o D:\results\full

# Full without OCR or translation; repeat and comma forms are equivalent
.\bstrings.exe analyze -d D:\carved --full `
  --exclude-engine ocr --exclude-engine translation `
  -o D:\results\without-ocr-translation
.\bstrings.exe analyze -d D:\carved --full `
  -e ocr,translation -o D:\results\without-ocr-translation

# Native extraction only
.\bstrings.exe analyze -f D:\evidence\memory.raw `
  --native-extraction on --recover-executable-strings off `
  --ocr off --translation off --lr all -o D:\results\native

# FLOSS only: include FLOSS static and derived categories, then match
.\bstrings.exe analyze -d D:\executables `
  --native-extraction off --recover-executable-strings force `
  --ocr off --translation off --lr all -o D:\results\floss

# OCR only
.\bstrings.exe analyze -d D:\documents `
  --native-extraction off --recover-executable-strings off `
  --ocr force --ocr-provider auto --translation off `
  -o D:\results\ocr

# OCR source records, then language assessment and selected translation
.\bstrings.exe analyze -d D:\documents `
  --native-extraction off --recover-executable-strings off `
  --ocr force --translation auto --translation-policy high-recall `
  -o D:\results\ocr-translated

# Inventory likely languages without running translation
.\bstrings.exe analyze -d D:\carved `
  --translation detect-only -o D:\results\languages

# Translate every eligible text record rather than using the confidence gate
.\bstrings.exe analyze -d D:\carved `
  --translation all -o D:\results\translate-all
```

Directory analysis is recursive. Put the output outside the input tree.

Native, FLOSS, and OCR are source producers. Translation is a transform over
their canonical records, not a raw-byte extractor. `--translation auto`,
`all`, and `detect-only` therefore require at least one selected producer; a
producerless configuration fails before the bundle, output path, or evidence is
opened. Pattern matching and reports remain mandatory in every `analyze` mode.
A selected specialist that has no applicable input or emits zero records can
still complete successfully with truthful terminal status.

`--exclude-engine`/`-e` is a Full-only convenience modifier, not another
profile. Each occurrence consumes one token containing one or more
comma-separated names from `native`, `floss`, `ocr`, and `translation`.
Occurrences accumulate; surrounding whitespace is trimmed, but empty, unknown,
duplicate/case-duplicate, and whitespace-separated extra values fail before
output or evidence access. An exclusion conflicts with explicitly supplying
the same engine's main selector. Valid tuning options for an excluded engine
remain validated but are inert. Resolution produces the same `AnalysisOptions`
as the equivalent explicit-off command, so no runtime or provenance branch is
added and the complete bundle remains atomically verified when selected.

## Executable recovery

[Magika](https://github.com/google/magika) now runs once in bounded multi-file
batches immediately after the SHA-256 input manifest is frozen. Its raw and
thresholded predictions are combined with deterministic PE/PDF/image signatures
and conservative extension hints in `content-routing.jsonl`. The union of
positive signals routes candidates to [FLOSS](https://github.com/mandiant/flare-floss)
and OCR. Native remains eligible for coverage disclosure, but an explicit
`--native-extraction off` leaves it unscheduled. A valid PE signature, either
Magika PE prediction, or the explicit force override can schedule FLOSS. An
executable extension by itself cannot.

Unknown or failed classification remains auditable and fails open to the
deterministic probes. Magika samples content, so routing does not prove that
every byte was inspected and is not a polyglot, carving, or malware-detection
result. The non-negotiable coverage, mutation, provenance, and performance
gates are recorded in
[ADR-0001: early fail-open content routing](architecture/adr-0001-early-fail-open-content-routing.md).

The bstrings adapter consumes FLOSS JSON from a temporary disk file, validates
its pinned result schema incrementally, normalizes supported categories, and
keeps distinct evidence locations even when the text is identical.

When native extraction is selected, FLOSS static strings are omitted because
native bstrings already captures them. This avoids duplicate records while
retaining FLOSS's genuinely derived strings. When native is off and FLOSS is a
selected producer, the adapter includes FLOSS static strings so specialist-only
coverage is not silently reduced. The output transaction fails closed on
unknown categories, duplicate JSON keys, missing required fields, invalid
addresses/encodings, overlong items, malformed UTF-8/JSON, timeout, or
unexpected process failure.

Known 32- or 64-bit shellcode is not guessed from arbitrary data. Advanced
users can deliberately force the corresponding FLOSS format through the
source-tree adapter, but that is a custom workflow and must be documented in
the case. The bundled normal route remains complete-file PE recovery.

The functional integration gate used Mandiant's open
[`flare-floss-testfiles`](https://github.com/mandiant/flare-floss-testfiles)
fixtures. Magika routed the reviewed PE, native extraction did not contain the
test decoded marker, FLOSS recovered the marker at two distinct virtual
addresses, and the regex stage preserved both derived records and locations.
This proves added recovery and lineage on that fixture, not universal malware
recall or performance.

## OCR before translation

OCR is integrated before language assessment so an examiner does not need to
know in advance which image or scanned page contains important non-English
text. PDF text-layer and OCR records join the records from whichever other
producers were selected in the same normalized stream; language triage and
regex processing therefore operate on the exact selected-source union.

Automatic OCR extracts every non-empty PDF text layer and renders only pages
whose layer is absent, very short, or suspicious. Force mode renders every
page. Images are always OCR inputs when the stage is enabled. The v1.9.17 profile
defines CPU, DirectML, and DirectML+CPU hybrid paths, and each has passed a
per-path inference smoke test. Those smokes do not establish cross-provider
parity or corpus-level quality. CUDA OCR is not part of the profile. See
[OCR and document analysis](ocr-and-document-analysis.md) for formats, exact
models, language scope, performance, output coordinates, and GPU-contention
guidance.

## Automatic language assessment

The bundled [lingua-rs](https://github.com/pemistahl/lingua-rs) detector runs
locally. Eligible records receive the most likely language, confidence,
target-language confidence, next-best confidence, and margin from the target.
That means a massive dataset can be screened probabilistically without the
examiner knowing which strings need translation.

Assessment decisions are explicit:

| Decision | Meaning |
| --- | --- |
| `target-language` | Likely already in the requested target language |
| `translate` | Selected as a non-target-language candidate |
| `ambiguous` | Did not clear the configured confidence/margin policy |
| `non-linguistic` | Outside text/letter bounds for useful language inference |
| `already-derived` | Already a translated child; recursion is prevented |
| `detector-failed` | Detector could not decide; high-recall policy keeps it as a candidate |

`--language-detection adaptive` samples the workload and selects the accurate
or fast local detector profile. `--translation-policy high-recall` is the
default and is appropriate when missing a foreign-language sentence costs more
than translating extra candidates. `balanced` applies the configured
`--language-confidence` and `--language-margin` thresholds. The optional
`high-precision` policy uses the greater of each configured value and its
conservative floor: 0.65 confidence and 0.15 target margin. It is an expert
volume-control policy, not the Full default and not a calibrated accuracy
claim.

Before language detection, the triage stage computes a compact, versioned
translation-worthiness observation. The first implementation is deliberately
shadow-only: a `prospective-*` code means that a strict validator consumed the
complete trimmed record, but the record still follows the same Lingua and
high-recall candidate path. `shadow-*` records other validated machine-like
signals that are not bypass-eligible, and `retain` covers natural, mixed,
unsupported, or inconclusive records and every router failure. File-level
Magika labels and extractor provenance may support retention but never suppress
translation by themselves.

The observation is stored in the record's existing
`language-assessments.jsonl` row under `translationRouting`; it does not create
a second copy of the evidence text or repeat existing detector/candidate
fields. Each row contains one bounded routing code; `run.json` and
`summary.json` bind the policy version and aggregate counts once per run. This
shadow rollout measures classification quality and overhead without changing
canonical parents, candidate order, or translated-child cardinality.
Authoritative bypass remains disabled until the multilingual forensic recall
and end-to-end performance gates in
[ADR-0007](architecture/adr-0007-translation-worthiness-routing.md) pass.

The current C# foundation also classifies a bounded set of origin flags for
each pending triage record and publishes reconciled work counters for
batch-unique routing evaluations, Lingua-eligible occurrences, actual Lingua
executions, and same-batch Lingua reuse. The structured shadow router does not
currently use origin to change its code; a future scorer may use origin only to
strengthen retention. A second Robin review froze any
future learned experiment as a binary `contains-any-human` versus
`machine-only` shadow scorer with explicit abstention; mixed records count as
human-positive, and an unvalidated origin/script/length cell retains. No learned
scorer is active, no runtime or model-size choice has been made, and none of
these observations currently avoids work.

The translation worker atomically publishes privacy-safe schema-1 aggregates
to `translation-work-stats.json` after publishing translated output. Managed
C# hashes the physical stats file, validates its exact schema and candidate,
decision, model-result, fallback, and child cardinalities, then projects it as
`translationWork` in `run.json` and `summary.json`. A mismatch fails the run.

`textDecisions` is the first exact-text decision within each translation
window/call, not a globally distinct-text count. Exact text repeated in a later
window creates another decision; `runCacheHits` reports the resulting cross-
window reuse. `translatorRequests` counts translator batch dispatches and
`translatorInputTexts` counts submitted texts, so neither candidate occurrences
nor the console's `modelInputs` display should be substituted for a different
work unit. The artifact contains counters only, but private case aggregates and
its SHA-256 still must not be published.

These normalized confidences are not universally calibrated probabilities.
Short strings, names, mixed-language text, OCR errors, and transliteration are
hard cases. Use `--translation detect-only` to review the distribution, or
`--translation all` when the cost is acceptable and the gate should not decide.

Every assessment records detector version/profile, policy, configured and
effective thresholds, confidence values, decision, and source record ID.
`configuredMinimumConfidence`, `configuredMinimumTargetMargin`,
`effectiveMinimumConfidence`, and `effectiveMinimumTargetMargin` make an
optional high-precision floor auditable. Policy decisions use the raw detector
values. The five displayed score fields are serialized to 12 decimal places
with round-to-even to bound insignificant parallel-reduction tails; a large
independent-call scale run still observed a one-unit difference in the final
published decimal, without any decision or candidate change.
`scoreDecimalPlaces`, `confidenceGatePassed`, and
`marginGatePassed` make that reporting policy and each raw gate outcome
explicit. These are detector confidence scores, not calibrated probabilities.

Successful detections for ordinally identical eligible text are reused only
inside the existing 2,048-record/8 MiB batch. Unsuccessful detections still
retry per record; no text or result cache survives the batch. On the reviewed
synthetic host, 50% batch-local duplication reduced median language-triage time
by 45.9% in accurate mode and 47.7% in fast mode, while all-unique input did not
regress. See the [performance review](language-triage-performance-2026-08.md)
for the exact-output gates and limitations.

## Offline translation profile

The complete bundle uses the official
[Hy-MT2 7B](https://huggingface.co/tencent/Hy-MT2-7B-GGUF) GGUF repository
through a private local llama.cpp server. Current source publishes one profile:

| Profile | Model | Bytes | WMT24++ chrF++ | Forensic chrF++ | Identifiers | Strings/s |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `quality` | Hy-MT2-7B Q4_K_M | 4,624,648,896 | **62.4386 CPU / 62.6129 CUDA p2** | **93.6923** | 22/22 | 0.1086 CPU / 1.3085 CUDA p2 |

The strict synthetic/attribution-safe gates are described in the
[translation report](translation-benchmark-2026-08-04.md). CPU Q4 and the
mixed source-built CPU server plus official CUDA overlay both retained 22/22
identifiers and 10/10 downstream patterns. These small-corpus results are a
bounded acceptance result, not a universal quality or hardware claim. The
model/runtime choice, exact bytes, failure policy, and release falsifiers are
recorded in [ADR-0006](architecture/adr-0006-q4-cuda-full-translation.md).
`balanced` still names a language-triage policy above—it is not an install or
model profile.

TranslateGemma remains a research challenger, not the production one-executable
engine. After gated access was accepted, the official BF16 4B model completed
the same 72-row air-gap gate with 22/22 identifiers and 10/10 pattern matches,
but scored 56.1631/84.2550 chrF++ at 0.05237 strings/s. That was lower quality
and about 51.7 times slower than the retired 1.8B Q4_K_M candidate on this
bounded corpus. It also
has not cleared the complete integrated-runtime and offline-packaging gates.
It is distributed under the gated
[Gemma Terms](https://ai.google.dev/gemma/terms), so a redistributable pack must
carry the terms and NOTICE and preserve their downstream restrictions; each
builder may need to accept access independently. MADLAD-400 remains a
developer/advanced benchmark
fallback for language-coverage research, but it is not selectable through the
integrated examiner CLI.

### Hardware selection and scheduling

`--translation-device` accepts `auto`, `cpu`, `cuda`, or `hybrid`:

The current Full split-pack design contains the reviewed CPU llama.cpp server
plus a separately authenticated official CUDA overlay. CUDA validation is
scoped to Windows RTX 4060 Laptop/sm89 rather than all NVIDIA hardware.

- `auto` tries full Q4 model load, one synthetic request, and observed 33/33
  layer offload at p2. Any failure closes CUDA and self-tests CPU before the
  first evidence inference, cache insertion, or output;
- `cpu` forces zero GPU layers and needs no graphics hardware;
- `cuda` requires the validated CUDA runtime/driver and full model offload and
  fails closed rather than selecting CPU; and
- `hybrid` requires an explicit positive `--translation-gpu-layers` count so
  the CPU/GPU split is auditable, but hybrid and p4 remain unaccepted
  expert/experimental boundaries.

After the first evidence request, device, provider, placement, and parallelism
are frozen. A later device/server failure leaves the translation transaction
incomplete. Full observed offload is reported as 33/33 layers together with
the real runtime buffers: the accepted command retained a 410.69 MiB
`CPU_Mapped` model buffer and therefore does not claim zero host residency.

`--translation-parallelism 0` selects conservative slots from hardware and
model size. The adapter processes bounded windows, groups similar lengths, and
uses a bounded in-memory hot set over a run-local SQLite exact cache. An exact
source/configuration pair is inferred at most once during that examination,
including repeats farther apart than the memory window, while one ordered child
is still written for every parent. The database uses complete source equality,
is never shared between cases, and is removed on normal completion and handled
failure. It uses an in-memory journal, synchronous-off writes, and 4,096-row
commit batches because it is ephemeral and never resumed; translated output is
staged and cannot replace prior output until strict cache cleanup succeeds. If
the translation child is killed or cancelled while the managed parent remains
alive, the parent's `finally` cleanup removes only the exact SQLite cache,
its `-wal`, `-shm`, or `-journal` sidecars, and exact
`<translated-filename>.partial.*` staged-output siblings from the physical
output directory. A link, reparse point, or cleanup failure fails the stage and
leaves prior translated output untouched. One model server is shared across
ordered concurrent requests.

This exact deduplication can remove many redundant model calls without reducing
Full's candidate recall. It does not make every noisy or mostly unique workload
fast: CPU fallback and large high-recall runs can still take a long time.
Translation progress therefore reports completed
record percentage, rate, ETA, cache hits, distinct model inputs, and preservation
fallbacks rather than promising a fixed completion time.

Hy-MT2's model card recommends stochastic decoding (`temperature 0.7`,
`top-p 0.6`, `top-k 20`) for general use. Bstrings deliberately uses the
forensic benchmark's greedy profile (`temperature 0`, `top-k 1`) to reduce
run-to-run variation and protect evidence attribution; this is a project
choice, not the upstream default.

Parallel greedy inference is not promised to be byte-identical because
continuous-batching schedules can alter floating-point accumulation at close
token choices. `--translation-strict-determinism` forces one slot and disables
prompt-cache reuse for the maximum-repeatability path. It does not promise
identical output across different drivers or hardware.

OCR completes before translation in the integrated pipeline. This prevents
bstrings' own OCR DirectML runtime and a large translation model from fighting
for VRAM. Other GPU-heavy processes can still cause device loss or allocation
failure; choose CPU or isolate the workstation workload when needed.

### Identifier and completion gates

Before a translated child is committed, the adapter checks exact code-point and
occurrence-count retention of hard structured evidence tokens including
emails, URLs, IP addresses, hashes, Windows/registry paths, CVEs, GUIDs,
host/port values, common filenames, placeholders, underscore-bearing tokens,
and all-uppercase ASCII code forms. Alphabetic hyphenation by itself is
advisory because it can be ordinary language, not a machine identifier.

A record containing only protected identifiers is classified as
`non-linguistic` and does not enter model inference. The adapter independently
applies the same bypass if such a record reaches its input. Mixed natural
language containing hard protected identifiers is still translated. If one
model result changes, removes, normalizes, changes the case of, or duplicates a
hard occurrence, the rejected model text is discarded and that source receives
one exact-source fallback child. Other candidates continue. The complete
translation transaction aborts only when the integrity circuit breaker sees
more than 1% fallbacks after at least 100 distinct model results, or 100
consecutive fallbacks.

Every translated child records `attributes.translationIntegrity` as
`verified`, `source-retained-ambiguous`, or `preservation-fallback`. An advisory
child also records a positive `translationAmbiguousIdentifierCount`; a fallback
records `translationIntegrityReason` and has `transform.outcome` equal to
`unchanged`.
These fields distinguish a successful translation, a result containing an
ambiguous alphabetic-hyphen token, and exact source retained after a rejected
model result.

Managed completion validation streams candidates and translations in lockstep:
each child must immediately correspond to the current candidate, retain exact
lineage, and compare fallback text with ordinal equality to that parent. It does
not build a candidate-text index. Pattern matching replays the translation file
once to build a selective disk index before consuming the parent-first merged
stream. That index contains only fallback parent/child IDs and fallback text,
has a 2 MiB bucket table when a fallback exists, grows only with fallback
records/text, and uses bounded heap memory. Strict cleanup runs before report
output publication; cleanup failure fails the stage and preserves prior output.

The local server must return exactly one terminal `stop` choice and the expected
prompt-token count. Empty, truncated, missing, extra, or duplicated translations
fail. An unchanged successful translation stays in the audit trail with
`outcome: "unchanged"`.

The server binds only to loopback, the web UI/reasoning output is disabled, the
exact model is hashed before loading, Hugging Face/Transformers/package-manager
offline variables are forced, and the adapter's audit hook rejects non-loopback
DNS/socket use. Each child records the model ID/revision/hash, llama.cpp
version, device path, GPU-layer policy, slots/threads, and `execution.airgap`.

## Matching and lineage

Selected native, recovered, OCR, PDF-text, and translated records are merged in
a stable order before matching. Disabled producer files remain atomically empty
so positional accounting stays stable. Built-in `--lr` groups and custom
`--fr` patterns use the same validation semantics on each normalized record.
The matcher does not rewrite or discard parent records.

Each record has a stable SHA-256-based ID. Derived records identify their
parent, source file, location kind, extractor/model/runtime identity, and
evidence class. Regex matches copy that lineage. Parents must precede children;
duplicate, missing, out-of-order, unattributed, or structurally inconsistent
records fail the run.

A translated match can normalize punctuation or create a token that resembles
an email, path, hash, or wallet. An OCR match can contain recognition errors.
Treat both as leads. Verify consequential results against the untranslated
parent, page/executable context, and original evidence.

The important result files are:

- `native-strings.jsonl`, `recovered-strings.jsonl`, and `ocr-strings.jsonl`;
- `ocr-assessments.jsonl` and `language-assessments.jsonl`;
- `translated-strings.jsonl`, `enriched-strings.jsonl`, and
  `regex-matches.jsonl`;
- `translation-work-stats.json`, with its validated projection in `run.json`
  and `summary.json`;
- specialist `content-routing.jsonl` and routed input projections, plus its
  three-rows-per-input `engine-status.jsonl` terminal coverage ledger,
  including `disabled-by-user` producers;
- `findings.tsv`, `pattern-histogram.tsv`, `feature-histogram.tsv`, and
  `pattern-histogram.html`;
- `input-manifest.jsonl`, `run.json`, and `summary.json`; and
- `.incomplete` while work is still in progress.

A result is complete only when both status documents say `complete` and the
`.incomplete` marker has been removed. See [output and provenance](output-and-provenance.md)
for schema and interpretation detail.

Use `bstrings.exe help analyze` for the installed option set, or see the
[terminal help and command reference](command-reference.md) for a task-oriented
workflow guide.

The complete quality installation remains one exact trust profile. A missing,
extra, or corrupt manifested file blocks any analysis that selects that bundle,
even when the affected engine was disabled. Runtime optionality does not create
slim or independently repairable component packs; physically isolated
capability profiles are deferred by
[ADR-0008](architecture/adr-0008-independent-engine-execution.md).

## Maintainer and regression entry points

Examiners should not call the Python adapters. Maintainers can inspect them
under `tools/enrichment/`, and must use pinned disposable environments plus
exact model/package/license records when changing them. Official acquisition
links are:

- [CPython embeddable package](https://docs.python.org/3/using/windows.html#the-embeddable-package);
- [Magika CLI](https://github.com/google/magika#command-line-tool);
- [FLOSS releases](https://github.com/mandiant/flare-floss/releases);
- [llama.cpp source/releases](https://github.com/ggml-org/llama.cpp);
- [RapidOCR](https://github.com/RapidAI/RapidOCR) and
  [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR); and
- [Hugging Face `hf` CLI](https://huggingface.co/docs/huggingface_hub/guides/cli).

Changes to stage order, routing, evidence semantics, models, or performance
defaults must follow the
[high-level architecture decision policy](architecture/decision-review-policy.md)
before implementation is promoted.

Run source-tree regression gates with:

```powershell
dotnet test bstrings.Tests\bstrings.Tests.csproj --configuration Release
python -m unittest discover -s tools\enrichment\tests -v
```

Release acceptance additionally builds from empty component caches, verifies
all inventories/licenses, exercises real OCR and translation, assembles the
split packs locally, and verifies the finished bundle without network fallback.
