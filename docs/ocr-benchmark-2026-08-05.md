# OCR evaluation record: historical SROIE v2 result and retired CORD design

This is the historical v2 [SROIE](https://rrc.cvc.uab.es/?ch=13) OCR quality
record, bound to source commit `23992fc75b624a3c6dab5bfbd0a4b52949133525`.
It leads with the terminal result so a passed calibration cannot be mistaken
for independent acceptance. Current source uses the v3 adapter, policy, and
acceptance schema; fresh v3 calibration is still pending, so this document does
not assign it a metric or acceptance result.

## Outcome at a glance

Result scope: confirmatory evaluation on the pinned community derivative test
split; not an official RRC leaderboard result.

| Question | Answer |
| --- | --- |
| Candidate | [RapidOCR 3.9.2](https://github.com/RapidAI/RapidOCR/releases/tag/v3.9.2) with immutable PP-OCRv6 medium [detector](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_det_onnx/tree/61323801669c338b7891481ec7bac61ce31b576a) and [recognizer](https://huggingface.co/PaddlePaddle/PP-OCRv6_medium_rec_onnx/tree/50c7eacafc52fa7bcf4194e8cd08e46f8558504b), plus the RapidOCR classifier |
| Frozen source | `23992fc75b624a3c6dab5bfbd0a4b52949133525`; [CI run 31009863474](https://github.com/Donovoi/bstrings/actions/runs/31009863474) passed |
| Historical v2 calibration | **Accepted for the frozen v2 candidate** on 616 selected train documents; a repeated ten-document CPU view was byte- and metric-deterministic |
| Independent test | **Failed before quality scoring.** The public terminal result records a degenerate-bounding-box parsing failure; the one-shot ledger was consumed and the test was not rerun |
| Release claim | No independent SROIE acceptance and no CPU/DirectML/hybrid test-parity claim |

The immutable
[pre-test witness](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-acceptance-v2-20260805-23992fc)
binds the source, calibration, policy, and expected test identity. Its asset
SHA-256 is
`ed23a78890a53ef4faa67fea900e3ab468aa74a5b4690c55baa0a8bb93868ec1`.
The immutable
[terminal result](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-terminal-v2-20260805-23992fc)
records the consumed failure without host paths, OCR text, or receipt content;
its asset SHA-256 is
`10d13e0e93c13c31be6515953cc8563bd576f71537200f642e08fbd050d099b3`.
Both releases resolve to the frozen source commit, and GitHub release and asset
verification completed successfully.

The exact path-free historical v2
[calibration report and policy](https://github.com/Donovoi/bstrings/releases/tag/ocr-sroie-calibration-v2-20260805-23992fc)
are also published as an immutable release. Their release asset digests match
the values below, making the metric tables independently inspectable without
publishing OCR text, host paths, or private evidence trees.

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

## Historical v2 scoring profiles

The v2 project gate compares text after Unicode NFC and Unicode casefold. The base
scorer also collapses whitespace; punctuation, digits, and token boundaries
remain significant. Stored OCR evidence is never casefolded or rewritten.

A full Unicode-NFC, case-sensitive rescore of the identical frozen OCR JSONL is
embedded as a hash-bound diagnostic. The policy recomputes both profiles and
validates every per-document normalization tag. The strict view does not create
a second threshold-selection opportunity.

The v2 project gate selected casefolding because this derivative's card says
annotations are capitalized even when receipt pixels are mixed case. This is a
project-defined label-alignment choice, not a documented RRC evaluator rule. It
prevents capitalization alone from dominating recognition error and must not
be described as better raw case-sensitive OCR.

## Historical v2 calibration result

The train split contains duplicate images with conflicting annotations. The
predeclared policy groups by image SHA-256, includes 616 documents, excludes 10
conflicting duplicate rows, and records a text-free selection audit. No
threshold was changed after seeing calibration results.

| Metric | Primary casefold | Case-sensitive diagnostic | Frozen test threshold |
| --- | ---: | ---: | ---: |
| Micro token F1 | 0.860229 | 0.601075 | at least 0.850000 |
| Micro CER | 0.115610 | 0.335252 | at most 0.120000 |
| Micro WER | 0.218109 | 0.476079 | at most 0.248109 |
| Localization Hmean | 0.978661 | 0.978661 | at least 0.948661 |
| Exact end-to-end Hmean | 0.634888 | 0.336611 | at least 0.584888 |
| Macro token F1 | 0.857929 | 0.598485 | at least 0.827929 |
| Macro CER | 0.119497 | 0.337166 | at most 0.149497 |
| Macro WER | 0.220384 | 0.479502 | at most 0.270384 |
| p10 document token F1 | 0.784000 | retained in report | at least 0.734000 |
| p90 document CER | 0.184987 | retained in report | at most 0.234987 |
| Documents below 0.50 token F1 | 0.1623% | retained in report | at most 2.1623% |

The CPU quality pass processed 616 documents in 1,292.02 seconds (0.4768
documents/s on that host). The repeated ten-document view was byte- and
metric-deterministic. These are host/corpus-specific observations, not a
universal speed promise.

Artifact identities:

- calibration report SHA-256:
  `3b436109523d9a5caff04d662bff8c5e32ec63b1f0717297bc2acbd3574f4775`;
- policy SHA-256:
  `234b4fc4ddacf27c3358f74d0725cf77a997f32201058a54958d9ed7c8bd56f3`;
- candidate identity SHA-256:
  `0598d900ecfedc1caa8c341a324d6b2f5f820125727efd4962a4f5cac255c5f6`;
- strict aggregate metrics SHA-256:
  `c31d161db2e8c09a2ccfbebcc4995ad775dc8b330d91f31dbacd208b310aa9ea`;
- strict per-document metrics SHA-256:
  `5e7bb9d65555192ec3f67212300343a9601d5f92b8ac555676e8c83754a29b28`.

## One-shot chain and terminal failure

The v2 protocol separated these evidence events:

1. Push a clean candidate and require green CI on that exact commit.
2. Run calibration, validate every document, and derive a fixed policy without
   opening the test Parquet.
3. Publish an immutable, path-free witness binding source, report, policy, and
   the expected test size/hash.
4. Verify that release and asset through an isolated GitHub CLI subprocess.
5. Create the stable machine ledger before accepting the held-out path over
   standard input or parsing any label.
6. Snapshot and hash the test artifact, then extract, run all backends, compare
   evidence, score, and seal—or fail closed and quarantine partials.
7. Publish a separate immutable, path-free terminal outcome.

Steps 1–5 completed. The snapshot was exactly 191,045,976 bytes with the
expected SHA-256. The public terminal result records a degenerate-bounding-box
failure during annotation parsing. The harness stopped before OCR quality
evaluation, quarantined the snapshot, and recorded `runSucceeded:false`,
`integrityPassed:false`,
`acceptancePassed:false`, and `finalDisposition:"failed"`.

This is not a model-quality rejection because no test metric exists. It is also
not acceptance. The failed attempt consumed the dataset-stable ledger. A code,
protocol, or model revision does not reset that ledger, and deleting it or
renaming the protocol would not restore independence.

The ledger is strong procedural/local evidence, not protection against an
administrator who can alter code or machine state. The immutable remote witness
and result make later changes to the published claims detectable.

## Current v3 source-annotation repair after failure

Subsequent local diagnosis of the preserved snapshot found one zero-height box
among 19,386 regions across the 361 rows. The associated
[conversion script](https://github.com/jsdnrs/ICDAR2019-SROIE/blob/77c4c11490e3c4179ee22f86611569c71c04b5f9/to_jsonl.py#L27-L45)
selects two corners from the original eight-coordinate annotation; the source
record available for this item still provides no usable height.

The v3 development repair is deliberately conservative: only a box with exactly
one zero-extent axis, an otherwise positive axis, and in-bounds integer
coordinates may gain one pixel. It increases the right or bottom bound by one
when possible; at the image edge it decreases the left or top bound instead.
Reversed, out-of-bounds, two-axis-degenerate, or non-integer boxes still fail.

The v3 adapter records the source box, scoring box, direction, image dimensions,
source/scoring identities, count, order, and canonical digest in a text-free
repair audit. A repair never drops a transcript, row, or document. This policy
was created after the held-out failure, so any replay is post-hoc development
evidence—never a replacement confirmation. A new independent claim requires a
genuinely untouched holdout.

## Runtime and provider boundary

The historical v2 candidate model-pack SHA-256 is
`b3b683eb29ec09e9da835e09fb4470792af7702cfc6cee40f2725c232d258534`.
The calibration used the packaged CPU runtime. The intended confirmatory matrix
was one complete CPU, DirectML, and DirectML+CPU hybrid pass plus a repeated
determinism view for each provider. Because parsing failed before inference,
the test provides no backend equality or throughput evidence.

Synthetic bundle smoke and release-specific hardware acceptance remain useful,
but answer different questions. DirectML can lose the device under GPU-memory
contention; `auto` is the normal choice and CPU is the stable fallback.

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

A separate component-only [PaddleOCR-VL 1.6](https://huggingface.co/PaddlePaddle/PaddleOCR-VL-1.6)
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
