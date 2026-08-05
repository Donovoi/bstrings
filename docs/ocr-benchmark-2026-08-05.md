# OCR evaluation record: SROIE v3 calibration and post-hoc diagnostic

The earlier v2 one-shot attempt stopped during annotation parsing before OCR
and consumed its ledger. The later candidate passed the v3 CPU calibration
gate; no calibration row required the repair path. A post-hoc replay then
audited all 361 test rows and scored 353 after
excluding the eight known exact train-image overlaps. Every backend met all 11
frozen numeric thresholds, but the sealed report failed its cross-provider
integrity gate. This is useful diagnostic evidence, not acceptance.

Result scope: project-defined SROIE Task-2-style evaluation on a pinned
community derivative, not an official RRC leaderboard result.

## Outcome at a glance

| Evidence | Result |
| --- | --- |
| v3 calibration | **Passed.** 616 of 626 train rows were selected by the frozen duplicate policy; token F1 0.860229, CER 0.115610, and WER 0.218109 |
| Predeclared test attempt | **No quality result.** The 361-row one-shot attempt failed closed during annotation parsing; its ledger was consumed and the test was not rerun. Eight exact train/test image overlaps were identified later |
| Post-hoc diagnostic | All 361 test rows were audited, eight exact train/test image overlaps were excluded, and the remaining 353 were scored; repaired dataset row index 142 remained included (zero-based) |
| Post-hoc quality | CPU, DirectML, and hybrid produced identical aggregate and per-document metrics, and every backend met all 11 frozen numeric thresholds; the report-level diagnostic comparison remained false because integrity failed |
| Post-hoc integrity | **Failed.** Provider-neutral critical evidence was not exactly equal and confidence records did not align structurally across all providers |
| Release claim | No independent SROIE acceptance, full CPU/DirectML/hybrid parity, or official RRC result |

The current v3 calibration is published in the immutable
[`ocr-sroie-calibration-v3-20260805-e3f4567`](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-calibration-v3-20260805-e3f4567)
release, bound to source commit
`e3f456708517f0ca64baae0e925fdccbf0c3a4d2`. Its report SHA-256 is
`5e6be755e913ed0d73d634f25899fe2051b1e62706b461d0a88bfd8765bccd70`
and its policy SHA-256 is
`51c6e07d21d128b4886801a232ff3f62adcd06ae5bdd3c38002992c505262852`.

The final post-hoc report is published in the immutable
[`ocr-sroie-posthoc-v2-20260805-81c0fb2`](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-posthoc-v2-20260805-81c0fb2)
release, bound to candidate commit
`81c0fb2b6393d59564645b84422bcf8f7a78e3da`. Its report SHA-256 is
`4129295263007d9ff8fb4da3f4cd7bbb9134262f66b73dbdd43a3ffef6dee543`.
The report contains no OCR text or host paths. Its `acceptancePassed` value is
null and its final disposition is `diagnostic-integrity-failed`.

## Dataset and comparison boundary

The harness pins the community-maintained
[`jsdnrs/ICDAR2019-SROIE` derivative](https://huggingface.co/datasets/jsdnrs/ICDAR2019-SROIE/tree/bffe40c26759f3376ec2b3ae9031dbba54cd587c)
at revision `bffe40c26759f3376ec2b3ae9031dbba54cd587c`:

| Split | Rows | Bytes | SHA-256 |
| --- | ---: | ---: | --- |
| train | 626 | 318,620,215 | `b18c16b4d8481e5e4537a1700e4616907fe4acd92d6362a7e430b0e866213887` |
| test | 361 | 191,045,976 | `04f8f31b45944cc6e6459a7a95c851a721fc93ffec0a5c29ece9ded734a684c2` |

This is not the official RRC distribution. The
[pinned derivative card](https://huggingface.co/datasets/jsdnrs/ICDAR2019-SROIE/blob/bffe40c26759f3376ec2b3ae9031dbba54cd587c/README.md)
describes extensions and corrections; its generated files contain 626/361
rows, while its prose says 626/347. The
[official task page](https://rrc.cvc.uab.es/?ch=13&com=tasks) describes a
600/400 competition split. Only a result submitted to the
[official Task 2 evaluator](https://rrc.cvc.uab.es/?ch=13&com=evaluation&task=2)
is leaderboard-comparable. See also the organizer-authored
[paper](https://arxiv.org/abs/2103.10213) and
[downloads page](https://rrc.cvc.uab.es/?ch=13&com=downloads).

Published Task 2 requires recognized-word lists, splits ground truth on spaces,
preserves repeated words, and ranks precision/recall/F1; it does not require
localization. The public protocol does not specify Unicode or case
normalization, so this project does not claim evaluator equivalence. This
project additionally measures localization and exact end-to-end rows, so the
result is deliberately named “SROIE Task-2-style.” It is not a KIE score,
handwriting result, arbitrary-language guarantee, or proof that SROIE was
absent from upstream model training.

## Scoring profiles

The project gate compares text after Unicode NFC and Unicode casefold. The base
scorer also collapses whitespace; punctuation, digits, and token boundaries
remain significant. Stored OCR evidence is never casefolded or rewritten.

A full Unicode-NFC, case-sensitive rescore of the identical frozen OCR JSONL is
retained as a hash-bound diagnostic. The policy recomputes both profiles and
validates every per-document normalization tag. The strict view does not create
a second threshold-selection opportunity.

Casefolding was selected because the derivative card says annotations are
capitalized even when receipt pixels are mixed case. This is a project-defined
label-alignment choice, not a documented RRC evaluator rule. It prevents
capitalization alone from dominating recognition error and must not be
described as better raw case-sensitive OCR.

## v3 calibration result

The train split contains duplicate images with conflicting annotations. The
frozen policy groups by image SHA-256, includes 616 documents, excludes 10
conflicting duplicate rows, and records a text-free selection audit. No
threshold was changed after seeing the calibration result, and the repair path
was not used on the selected train rows.

| Metric | v3 calibration | Frozen test threshold |
| --- | ---: | ---: |
| Micro token F1 | 0.860229 | at least 0.850000 |
| Micro CER | 0.115610 | at most 0.120000 |
| Micro WER | 0.218109 | at most 0.248109 |
| Localization Hmean | 0.978661 | at least 0.948661 |
| Exact end-to-end Hmean | 0.634888 | at least 0.584888 |

The CPU calibration path processed 616 documents in 1,305.28 seconds (0.4719
documents/s on that host). Both repeated ten-document views were artifact-bound
and deterministic. These are host- and corpus-specific observations, not a
universal speed promise.

Calibration showed that the frozen candidate cleared the development-data gate.
It does not replace a genuinely untouched test result.

## One-shot chain and terminal failure

The one-shot protocol separated these evidence events:

1. Push a clean candidate and require green CI on that exact commit.
2. Run calibration, validate every document, and derive a fixed policy without
   opening the test Parquet.
3. Publish an immutable, path-free witness binding source, report, policy, and
   the expected test identity.
4. Verify that release and asset through an isolated GitHub CLI subprocess.
5. Create the stable machine ledger before accepting the test-split path over
   standard input or parsing any label.
6. Snapshot and hash the test artifact, then extract, run all backends, compare
   evidence, score, and seal—or fail closed and quarantine partials.
7. Publish a separate immutable, path-free terminal outcome.

The predeclared attempt used the earlier v2 witness chain. Steps 1–5 completed,
and the snapshot was exactly 191,045,976 bytes with the expected SHA-256. The
run then encountered one degenerate bounding box in dataset row 142 during
annotation parsing, before OCR quality evaluation. The harness stopped,
quarantined partials, and recorded `runSucceeded:false`,
`integrityPassed:false`, `acceptancePassed:false`, and
`finalDisposition:"failed"`.

The immutable
[pre-test witness](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-acceptance-v2-20260805-23992fc)
and
[terminal result](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-terminal-v2-20260805-23992fc)
preserve that outcome at source commit
`23992fc75b624a3c6dab5bfbd0a4b52949133525`. They do not bind the later v3
calibration or convert it into a fresh test.

This is not a model-quality rejection because no one-shot test metric was
created. It is also not acceptance. The failed attempt consumed the
dataset-stable ledger. A code, protocol, or model revision does not reset that
ledger, and deleting it or renaming the protocol would not restore independence.

The ledger is strong procedural/local evidence, not protection against an
administrator who can alter code or machine state. The immutable remote witness
and result make later changes to the published claims detectable.

## Source-annotation repair

Diagnosis of the preserved snapshot found one zero-height box among 19,386
regions across the 361 rows. The associated
[conversion script](https://github.com/jsdnrs/ICDAR2019-SROIE/blob/77c4c11490e3c4179ee22f86611569c71c04b5f9/to_jsonl.py#L27-L45)
selects two corners from the original eight-coordinate annotation; the source
record available for this item still provides no usable height.

The v3 repair is deliberately conservative: only a box with exactly one
zero-extent axis, an otherwise positive axis, and in-bounds integer coordinates
may gain one pixel. It increases the right or bottom bound by one when possible;
at the image edge it decreases the left or top bound instead. Reversed,
out-of-bounds, two-axis-degenerate, or non-integer boxes still fail.

The adapter records the source box, scoring box, repair direction, image
dimensions, source/scoring identities, count, order, and canonical digest in a
text-free audit. A repair never drops a transcript, row, or document. This
policy was created after the one-shot test-split failure, so any replay is
post-hoc development evidence—never a replacement confirmation. A new
independent claim requires a genuinely untouched holdout.

## Post-hoc diagnostic result

The post-hoc run kept the full 361-row corpus for repair, duplicate, provenance,
and source-integrity checks. It excluded eight test rows
whose images were byte-identical to train images—dataset row indices 152, 153,
155, 180, 356, 357, 359, and 360—from the scoring view. The repaired row 142 was
not an overlap and remained in the 353-document scoring population. These are
zero-based row indices.

This filters known exact overlaps in decoded image bytes only. The report does
not claim that perceptual or near-duplicate overlaps are absent.

The harness then scored the 353-row exact-overlap-filtered view. Before writing
the report, it re-hashed the nine output roots and recomputed both scoring
profiles. The report binds 48 artifacts. A separate ten-document view was run
twice per backend to check determinism; the complete 353-document runs were not
repeated.

| Metric | CPU, DirectML, and hybrid | Frozen threshold | Comparison |
| --- | ---: | ---: | --- |
| Micro token F1 | 0.855862 | at least 0.850000 | passed |
| Micro CER | 0.116711 | at most 0.120000 | passed |
| Micro WER | 0.224485 | at most 0.248109 | passed |
| Localization Hmean | 0.978510 | at least 0.948661 | passed |
| Exact end-to-end Hmean | 0.625437 | at least 0.584888 | passed |
| Document exact rate | 0.000000 | diagnostic only | n/a |

The aggregate and per-document quality metrics were identical across all three
paths. Each backend met all 11 frozen numeric thresholds, each repeated view
was deterministic, all backend provenance checks passed, and the hybrid run
used both CPU and non-CPU lanes. Nevertheless, `integrityPassed` and
`diagnosticThresholdComparisonPassed` are false because cross-provider critical
evidence was not equal and the confidence observations could not be
structurally aligned. Therefore
`acceptancePassed` is null and the only valid final disposition is
`diagnostic-integrity-failed`.

The formal confidence block reports zero compared records and zero deltas
because structural alignment failed before pairwise comparison. Those zeros are
not evidence of equal confidence values.

An exploratory, unsealed comparison of the private local outputs—not a field in
the immutable report and not release evidence—localized the observed critical
record differences to bounding-box and derived-location fields. Because that
follow-up is unpublished, no exact counts or deltas are used as a public claim.
It does not satisfy or weaken the frozen exact-equality gate.

## Runtime and provider boundary

| Path | Elapsed time | Throughput |
| --- | ---: | ---: |
| CPU | 768.43 s | 0.4594 documents/s |
| DirectML | 283.48 s | 1.2452 documents/s |
| DirectML + CPU hybrid | 286.54 s | 1.2319 documents/s |

The hybrid evidence contains 1,503 CPU-lane records and 17,050 non-CPU-lane
records. On this host and corpus, DirectML was fastest and hybrid was close;
neither observation is a universal hardware promise. Operationally, DirectML
can lose the device under GPU-memory contention, so `auto` remains the normal
choice and CPU the stable fallback.

Synthetic bundle smoke and release-specific hardware checks remain useful, but
answer different questions. Passing them cannot repair a failed evidence gate.

## Historical CORD design (retired)

CORD v2 `test` and `validation` became development data during earlier scorer
work. A proposed 600/200 split of CORD `train` was retired before confirmation
because a public dataset-viewer/API research step accessed annotation content
across an indeterminate set of rows. No CORD one-shot ledger, confirmatory OCR
output, or metric was created. The checked-in retirement marker makes the old
confirmation CLI fail before output.

The CORD work still produced useful parser, geometry, boundary-clipping,
determinism, runtime-inventory, and worker-manifest tests. It must not be
reported as independent quality evidence.

A separate component-only
[PaddleOCR-VL 1.6](https://huggingface.co/PaddlePaddle/PaddleOCR-VL-1.6)
experiment on 20 already-development CORD validation documents produced 82.6%
token recall but 7.3% precision (13.4% F1), with repeated/hallucinated output.
It did not run the official full pipeline and was not promoted.

## Release rule

Do not label an OCR release quality-accepted from calibration, synthetic smoke,
hardware-path evidence, an incomplete report, a failed ledger, or a post-hoc
replay. Preserve all terminal outcomes. For a materially changed model or
scorer, choose a genuinely untouched corpus, freeze the procedure before labels
are opened, and repeat the clean-commit, CI, calibration, immutable witness,
single-attempt, and immutable-result sequence.
