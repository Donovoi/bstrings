# OCR benchmark and acceptance design (2026-08-05)

This note freezes the acceptance design before any OCR output or annotation from
the CORD v2 train split is examined. It separates development evidence from the
one result that is allowed to confirm a release claim.

## What is and is not a holdout

CORD v2 `test` is development data for this work. Its first benchmark exposed
invalid row-order and one-to-one-box assumptions, and rows 9, 12, and 73 are
hand-anchored scorer tests. CORD v2 `validation` is also development data: a
20-document PaddleOCR-VL experiment used rows 0-19, and its annotation schema
is covered by parser tests. Neither split may be described as independent or
confirmatory.

The only project-level confirmatory data comes from CORD v2 `train` at
immutable dataset revision `7f0115a4b758a71d6473b8d085751692da2fef98`. All
four parquet shards are required in this exact order:

| Shard | Bytes | SHA-256 |
| --- | ---: | --- |
| `train-00000-of-00004-b4aaeceff1d90ecb.parquet` | 490,224,630 | `da3994eee1bf9bd3c57f0d53a72c3a6812c8696c5ba26245987949ddf73483cc` |
| `train-00001-of-00004-7dbbe248962764c5.parquet` | 441,418,432 | `cce4def16a0d6a6c75f80be712f7494c56c318a8829b712f5c62650155c9e58e` |
| `train-00002-of-00004-688fe1305a55e5cc.parquet` | 443,802,181 | `591e2db8fe8b1d364b054f46e8c375b7f00e72578914ba46a573b6858162cab2` |
| `train-00003-of-00004-2d0cd200555ed7fd.parquet` | 455,555,434 | `1ffd9de8d6fbcee7630fd4cdfedff05b9b7fabc0fae4fc17557c5fe7cf178748` |

This is not claimed to be model-unseen data. The official PP-OCRv6 material
does not publish a complete training-corpus inventory, so this project cannot
establish that CORD was absent from upstream private training data. The result
tests this project's frozen integration and generalization procedure, not
upstream dataset non-contamination.

Image-column-only checks found no shared image SHA-256 between train, test, and
validation. The 800 train rows contain 798 unique image hashes. Identical images
must never be divided across roles. The split algorithm groups rows by the
SHA-256 of embedded image bytes, sorts the unique hashes lexicographically,
and assigns the shortest prefix whose row multiplicities total at least 200 to
`confirmatory`. The pinned corpus must make that total exactly 200; otherwise
selection fails. The other 600 rows are `calibration`.

The frozen selection manifest is
`tools/enrichment/cord-v2-train-selection-v1.json`. It is a schema-version-1
object whose `entries` array is ordered by pinned shard, then row. Each entry
contains shard ordinal, row index, global index, image SHA-256, and role. Its
canonical JSON identities are:

- complete selection: `b7cd1b3b9ee19a6c617adffcec4cfb3e5e0a3567e1e08b377ea525f0dd012a8c`;
- calibration selection: `ece7bf666fccc109f0d65449bcd0f6fdbdb9b8f326df9dc07418553025de85e9`;
- confirmatory selection: `c1415ecc2cd56cff29b9896edcf753200de16c256f41f62edcead0a1236579b0`.

For those three identities, the relevant entries array is serialized as UTF-8
JSON with keys sorted, separators `,` and `:`, no insignificant whitespace,
and no trailing newline, then hashed with SHA-256. The manifest file itself is
also bound by its whole-file SHA-256,
`4deb7deec2a5ee69e182c9030ef0e1dee5bdf5960a2f9bec9ba5f404293fd6e1`.
Duplicate images count as separate rows for aggregate and tail metrics, but
remain in the same role. When ground truth is first opened, duplicate image
rows must have identical canonical annotation hashes or the corpus fails
closed.

Only the image column was read to freeze those identities. Ground-truth fields
and OCR output for both roles remained unread at design time.

## Fixed model and execution contract

The candidate is RapidOCR 3.9.2 with the immutable PP-OCRv6 medium detector and
recognizer plus the RapidOCR orientation classifier. The model-pack manifest
SHA-256 is
`b3b683eb29ec09e9da835e09fb4470792af7702cfc6cee40f2725c232d258534`.
The worker, scorer, protocol, scoring constants, model revisions, corpus and
worker manifests, full runtime inventories, selection manifest, and the exact
PyArrow, Shapely, and GEOS versions must be frozen and hashed before
calibration. A change to any of them invalidates the policy and requires a new
calibration run.

The calibration role is run once with the CPU backend and production automatic
thread policy. The confirmatory role is run once as the complete CPU,
DirectML, and DirectML+CPU hybrid release matrix. Provider text, boxes,
normalized critical assessment fields, and aggregate metrics must agree
exactly; provider, runtime, thread, worker, record-ID, and confidence fields are
excluded from that normalized comparison. Confidence may differ by at most
`0.0001` per aligned record. Each backend must also be byte-deterministic across
its repeated ten-document determinism view.

## Absolute quality floors

These are product-quality floors, not values chosen to make this candidate
pass. They establish quality only for the printed CORD receipt scope. All are
required:

| Metric | Required value |
| --- | ---: |
| Micro localization-coverage Hmean | at least 0.85 |
| Micro exact end-to-end row Hmean | at least 0.50 |
| Micro exact-token F1 | at least 0.85 |
| Micro character error rate | at most 0.12 |
| Micro word error rate | at most 0.25 |
| Macro document token F1 | at least 0.80 |
| Macro document character error rate | at most 0.20 |
| Macro document word error rate | at most 0.35 |
| 10th-percentile document token F1 | at least 0.65 |
| 90th-percentile document character error rate | at most 0.30 |
| Documents with token F1 below 0.50 | at most 5% |

Quantiles use the nearest-rank definition on the sorted per-document metric:
rank `ceil(p * n)`, with one-based ranks. Duplicate image rows retain their row
weight. The confirmatory denominator is 200, so the 5% tail limit permits at
most 10 rows whose token F1 is strictly less than 0.50. A metric exactly on any
other boundary passes.

## Calibration-derived non-inferiority limits

The signed limits below are applied to the final calibration metrics, then
combined with the absolute floors using the stricter value. Higher-is-better
thresholds subtract the margin; lower-is-better thresholds add it.

| Metric | Allowed degradation from calibration |
| --- | ---: |
| Micro localization-coverage Hmean | 0.03 |
| Micro exact end-to-end row Hmean | 0.05 |
| Micro exact-token F1 | 0.02 |
| Micro character error rate | 0.02 |
| Micro word error rate | 0.03 |
| Macro document token F1 | 0.03 |
| Macro document character error rate | 0.03 |
| Macro document word error rate | 0.05 |
| 10th-percentile document token F1 | 0.05 |
| 90th-percentile document character error rate | 0.05 |
| Fraction of documents below 0.50 token F1 | 0.02 |

For a higher-is-better metric, the derived threshold is
`max(absolute floor, calibration metric - margin)`. For a lower-is-better
metric, it is `min(absolute ceiling, calibration metric + margin)`. The
below-0.50 document fraction is lower-is-better and uses a strict `< 0.50`
document predicate. Calibration must itself meet every absolute micro, macro,
and tail floor before a policy may be issued.

These fixed tolerances are a regression/non-inferiority rule, not a statistical
significance claim. The emitted policy must contain the exact calibration
report SHA-256, corpus and selection identities, scorer/worker/model identities,
calibration metrics, absolute floors, margins, and resulting thresholds. The
confirmatory report must bind the policy SHA-256 and reject a partial,
overridden, mismatched, or tampered policy.

## One-shot rule

Confirmatory output may be generated once after the policy is frozen. If it
fails, that is the result. A scorer, model, preprocessing, threshold, or worker
change prompted by its metrics makes the 200 rows development data; the same
rows cannot then be rerun and presented as independent confirmation. Execution
failure may be retried only if the attempt is recorded, all frozen input and
executable hashes are identical, no evidence-derived record or metric from the
failed attempt was inspected or used, and partial artifacts are quarantined
rather than accepted. The recorded cause must be unrelated to evidence content.

Run success, evidence-integrity/parity success, and quality acceptance are
separate report fields. A completed run is not automatically an accepted
release.

## Development observations

The bare PaddleOCR-VL 1.6 GGUF path is not promoted. On the already-development
CORD validation rows 0-19 it produced 82.6% token recall but only 7.3%
precision (13.4% F1), with long repeated/hallucinated output on many receipts.
That experiment is a model-screening result, not a comparison against the
confirmatory corpus. The full official PaddleOCR-VL pipeline may behave
differently and would require its own frozen candidate and clean holdout.
