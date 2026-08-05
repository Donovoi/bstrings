# OCR benchmark and acceptance design (2026-08-05)

This note records the acceptance design initially frozen before any OCR output
or annotation from the CORD v2 train split was examined, plus the parser and
runtime-contract repairs derived from the 600-row calibration role. It
also records why the original CORD confirmation role was retired before it was
run.

## Holdout retirement notice

The CORD train confirmation described below is now historical design, not an
available release gate. During first-party schema research, a public dataset
viewer/API search accessed CORD train annotation content across an indeterminate
set of rows. The local one-shot ledger was never created and no confirmatory
OCR output or metric was generated, but the claim that the 200-row role remained
unseen is no longer defensible. The checked-in
`cord-v2-train-confirmatory-retired-v1.json` marker makes the confirmation CLI
fail before creating output or a ledger. CORD train is development data for
this project from this point forward.

The replacement release gate is the previously untouched SROIE receipt test
split. Dataset repository `jsdnrs/ICDAR2019-SROIE` is frozen at commit
`bffe40c26759f3376ec2b3ae9031dbba54cd587c`. Its 626-row train Parquet is
development data; its 361-row test Parquet remains sealed while a separate
protocol is implemented and committed. The complete repository was downloaded
without reading either split's rows and verified against the Hub. The pinned
Parquet identities are:

| Split | Bytes | SHA-256 |
| --- | ---: | --- |
| train | 318,620,215 | `b18c16b4d8481e5e4537a1700e4616907fe4acd92d6362a7e430b0e866213887` |
| test | 191,045,976 | `04f8f31b45944cc6e6459a7a95c851a721fc93ffec0a5c29ece9ded734a684c2` |

## What is and is not a holdout

CORD v2 `test` is development data for this work. Its first benchmark exposed
invalid row-order and one-to-one-box assumptions, and rows 9, 12, and 73 are
hand-anchored scorer tests. CORD v2 `validation` is also development data: a
20-document PaddleOCR-VL experiment used rows 0-19, and its annotation schema
is covered by parser tests. Neither split may be described as independent or
confirmatory.

The original project-level confirmatory design used CORD v2 `train` at
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

[CORD](https://github.com/clovaai/cord) contains 1,000 Indonesian receipt
samples and is used under
[CC-BY-4.0](https://github.com/clovaai/cord/blob/master/LICENSE-CC-BY). Every
claim below remains scoped to that printed-receipt corpus.

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
also bound at exactly 124,181 bytes by its whole-file SHA-256,
`4deb7deec2a5ee69e182c9030ef0e1dee5bdf5960a2f9bec9ba5f404293fd6e1`.
Duplicate images count as separate rows for aggregate and tail metrics, but
remain in the same role. When ground truth is first opened, duplicate image
rows must have identical canonical annotation hashes or the corpus fails
closed.

Only the image column was read to freeze those identities. Ground-truth fields
and OCR output for both roles remained unread at design time; the later viewer
incident described above retired that status before confirmation.

## Fixed model and execution contract

The candidate is RapidOCR 3.9.2 with the immutable PP-OCRv6 medium detector and
recognizer plus the RapidOCR orientation classifier. The model-pack manifest
SHA-256 is
`b3b683eb29ec09e9da835e09fb4470792af7702cfc6cee40f2725c232d258534`.
The worker, scorer, protocol, scoring constants, model revisions, calibration
corpus/worker manifests, confirmatory image-input/worker manifests, full
runtime inventories, selection manifest, and the exact PyArrow, Shapely, and
GEOS versions must be frozen and hashed before calibration. The complete train
shards bind the still-blind confirmatory annotations. Before policy issuance,
the application layer must not convert confirmatory ground-truth values to
Python, parse them, score them, log or expose them, or publish annotation hashes
or label-derived counts. Each 200-row shard is one Parquet row group, so PyArrow
may transiently materialize interleaved ground-truth column pages while the
calibration labels are read. The harness skips those confirmatory rows without
Python conversion or parser access. Only after the project-global one-shot
ledger is claimed may it convert and parse those annotations, verify
duplicate-image annotations, and bind the resulting scoring-corpus manifest
into the final report. A change to a frozen input invalidates the policy and
requires a new calibration run, but it does not reset the global one-shot
ledger.

The frozen generic runner `tools/enrichment/benchmark_ocr.py` has SHA-256
`b79cd6997201585798a72af26ca1d5d886c0d1b2909d5c31b01e806259d0e49a`.
The frozen CORD scorer `tools/enrichment/benchmark_ocr_cord.py` has SHA-256
`e9faec03c4477c4b4f039803f270f5a76aebd5e16bd0eeedee4d30a21e6903bf`.
Its scoring-constants SHA-256 remains
`cf5671a4d144b544d406e180b1b5d1c62b0f8d1701819ba53dff1357388cd0b5`:
the calibration repairs changed annotation compatibility and the worker wire
contract, not metric thresholds or geometry constants.
The acceptance report also records and binds the wrapper and policy-module
hashes from the exact committed source used for the run.

The calibration role receives one full CPU quality pass with production
automatic thread policy plus the fixed repeated ten-document determinism view.
The confirmatory role receives one full quality pass per CPU, DirectML, and
DirectML+CPU hybrid backend plus that same per-backend determinism view.
Provider text, boxes,
normalized critical assessment fields, and aggregate metrics must agree
exactly; provider, runtime, thread, worker, record-ID, and confidence fields are
excluded from that normalized comparison. Confidence may differ by at most
`0.0001` per aligned record. Each backend must also be byte-deterministic across
its repeated ten-document determinism view.

### Runtime identity and isolation

"Full runtime inventory" has a literal scope here. For each benchmark, CPU,
and DirectML interpreter, the preimage covers the deduplicated `sys.prefix`,
`sys.base_prefix`, and every load-bearing entry returned by the isolated
interpreter's `sys.path`. Each root records its roles and a sorted relative
POSIX-path list containing the byte length and SHA-256 of every regular file.
The root digest is the SHA-256 of the canonical JSON files array without a
trailing newline. An ordered load-path map proves which inventoried root covers
each isolated `sys.path` entry, and the executable's relative path, length, and
SHA-256 must resolve to one of those rows. A load-path entry that does not exist
is recorded as `missing` only when it resolves inside an otherwise fixed,
inventoried root; it contributes no load-bearing role. An unreadable,
reparse-point, or uncovered root fails closed. The complete metadata preimage is
also hashed and persisted as one canonical JSON artifact per profile; backend
repetition records contain only compact digest references.

The DirectML profile additionally binds the provider probe, the System32
DirectML/D3D/DXGI DLL byte hashes and version identities, and privacy-normalized
display-adapter/driver identity. It fails closed if Windows, the required
provider, a required system DLL, CIM inventory, or a usable adapter is absent.
Absolute runtime roots, usernames, hostnames, and raw PnP identifiers are not
written to the canonical metadata.

The acceptance interpreter itself must be launched with `-I -B`. It loads the
frozen sibling modules by compiling one captured source-byte buffer per file,
binds those loaded-byte hashes, and never adds `tools/enrichment` or the caller's
working directory to `sys.path`. Before lazy scorer imports, it replaces the
process environment with a minimal offline allowlist, changes to a fresh empty
temporary directory, and on Windows enables safe default DLL search with only
the interpreter and System32 directories added explicitly. Worker processes use
the same isolated-interpreter, sanitized-environment, and controlled-working-
directory contract. The effective policy is recorded without local absolute
paths. Calibration persists the three complete preimages before OCR starts;
confirmation verifies them before claiming the global ledger, and both phases
regenerate and compare the complete inventories after their runs.

### Annotation boundary normalization

CORD does not require every quadrilateral coordinate to lie inside its declared
image. In an [official repository issue](https://github.com/clovaai/cord/issues/7#issuecomment-1189900218),
a principal CORD contributor explains that the labelling tool can produce
outside coordinates and that they normally project those coordinates onto the
image edge. This is useful first-party implementation guidance, not a formal
pixel limit: neither the [CORD schema](https://github.com/clovaai/cord/blob/327310ce58c1623255821d062b3a759ff3789e3c/README.md#json-hierarchy)
nor the v2 dataset publishes a tolerance or retained-area rule.

The first real calibration attempt stopped before OCR when the old 3-pixel
guard reached a valid-line word extending 5 pixels beyond the left edge. The
failure report and partial evidence were preserved. A calibration-only audit
then checked all 15,339 non-ROI quadrilaterals in the 600 allowed development
rows. Thirty polygons in 16 documents crossed an edge: 14 valid-line words, 13
`dontcare` regions, and 3 `repeating_symbol` regions. Every raw quad happened to
be finite, convex, non-self-crossing, and non-degenerate in its source order;
every exact image intersection was nonempty and convex; and all 600 embedded-
image dimensions matched their metadata. Maximum overshoot was 18 pixels, or
3.7656903766% of the corresponding axis. The smallest retained area was
80.4084704938%.
The local calibration process did not convert or parse confirmatory annotations,
and the one-shot ledger was not created. This does not undo the later public-
viewer exposure recorded in the retirement notice.

The boundary policy introduced in scorer protocol v4 and retained unchanged in
v5 therefore uses a rounded conjunctive envelope selected only from calibration
evidence. A non-degenerate `valid_line`, `dontcare`, or
`repeating_symbol` polygon derived from the four raw coordinates may be
intersected with `[0,width] x [0,height]` only when, on each axis, overshoot is
at most both 24 pixels and 5% of that image dimension, and the intersection
retains at least 75% of the original polygon area. The thresholds are
inclusive. The 18/4%/80%
observed envelope and the rounded 24/5%/75% envelope both admit exactly the same
30 calibration polygons; the rounded values avoid binding the protocol to a
single calibration maximum while the three simultaneous guards remain
fail-closed. Outside-vertex count is recorded but is not a gate because it is
not monotonic with damage: a harmless one-pixel corner truncation can put three
of four vertices outside while retaining more than 98% of the area.

Point order is not a gate: the four raw coordinates are canonicalized to their
convex hull, matching the prior scorer and avoiding an unsupported assumption
about the official schema. Receipt ROI polygons keep their separate,
intersection-only handling and are not subject to the non-ROI envelope.

Raw source annotations remain unchanged and are still covered by their
canonical SHA-256. Only derived scoring polygons are clipped. Each repair is
written to the corpus manifest with its annotation locator, crossed sides,
outside-vertex count, per-axis pixel and relative overshoot, original and
clipped area, and retained-area ratio. Acceptance protocol v3 uses schema
version 2 for its blind input, scoring-corpus, failed-ledger, and quarantine
records. The project-global attempt filename retains its original `v1` suffix
deliberately:
it names one stable dataset-level holdout namespace and cannot be reset by
changing the acceptance protocol. The report also records clip counts and the worst observed bounds. A boundary
failure preserves a path-free structured diagnostic containing the global row,
annotation locator, failed predicate, sides, and measured geometry in the
failure report, failed ledger, and quarantine marker. A degenerate, excessive,
empty, or low-retention result fails validation; the scorer never drops the
truth item or relaxes the rule after confirmation.

### Repeating-symbol text and worker-manifest compatibility

The second real calibration attempt ran from a clean, pushed commit after its
exact GitHub Actions run passed. It stopped before OCR quality scoring at
calibration global row 383 because three `repeating_symbol` entries contain an
empty `text` string. The failed report and partial evidence remain preserved.
A calibration-only scan then examined all 619 repeating-symbol entries in the
600 permitted rows: all entries used the exact `{quad,text}` shape, all text
values were strings without NUL characters, and only those three entries in
one document were empty. Confirmatory values were not converted or parsed, and
the one-shot ledger was not created.

The official CORD schema describes repeating symbols as cut-line annotations
with `quad` and `text`, but does not state that `text` must be nonempty. Scorer
protocol v5 therefore permits the empty string for this field while still
requiring the exact schema, a string value without NUL, a nonempty group, and a
valid bounded polygon. The original empty value remains covered by the
canonical annotation hash. The descriptive text is not used for scoring; the
validated polygon is retained as an ignored cut-line region. This is a
calibration-derived compatibility repair, not a claim that the published CORD
schema formally guarantees empty values.

After the CORD holdout was retired, an exhaustive scan of the pinned local
train bytes parsed all 800 documents successfully. All 845 repeating-symbol
entries used the exact `{quad,text}` shape; exactly the same three values were
empty, and none omitted `text`, used a non-string, or contained NUL. Apparent
quad-only objects returned by the public search API could not be reproduced in
the hash-pinned Parquet and were not used to broaden the parser. Missing keys
therefore still fail closed.

The same pre-OCR review found that scorer protocol versions had accidentally
been written into worker input manifests even though the integrated
`bstrings_ocr.py` executable has a separate schema-version-1 wire contract.
Those rows would have failed deterministically at worker startup. The two
contracts are now independent: scorer reports and scoring-corpus rows use
schema version 5, while every generated OCR worker input row uses the worker's
schema version 1. Cross-module tests feed generated rows through the real worker
parser so a future scorer revision cannot silently reintroduce this mismatch.

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

## Historical CORD one-shot rule

This section records the retired design and must not be used to authorize a
CORD confirmation. Under that design, confirmatory output could be generated
once after the policy was frozen. If it
fails, that is the result. A scorer, model, preprocessing, threshold, or worker
change prompted by its metrics makes the 200 rows development data; the same
rows cannot then be rerun and presented as independent confirmation. Execution
failure also consumes this 200-row role: the project-global ledger rejects
every later claim, including after a failed or quarantined attempt. Investigate
the recorded cause and preserve the quarantine evidence, but do not describe a
later run over these rows as this protocol's independent confirmation.

Run success, evidence-integrity/parity success, and quality acceptance are
separate report fields. A completed run is not automatically an accepted
release.

## Running the frozen protocol

Before calibration, the code, selection manifest, and preregistration document
must be in a clean committed state that is already anchored to the remote.
Record the immutable commit ID and preferably a signed release-candidate tag;
require the relevant CI checks to pass on that exact object. Do not calibrate
from an uncommitted worktree. The worker, wrapper, scorer, policy module,
selection, model pack, runtime files, and dependencies must not change between
calibration and confirmation. A regenerated policy or report does not authorize
any such change.

Treat publication as separate evidence events. First preserve the accepted
calibration report, derived policy, calibration evidence tree, recorded source
commit/tag, and their hashes as one immutable evidence commit or release
artifact. Only then authorize the one-shot confirmation. Preserve the global
attempt ledger, final confirmatory report, quarantine material if any, and their
hashes as a second evidence commit or release artifact. Do not fold a source
repair into either evidence publication.

Use absolute paths for every input and output. The wrapper captures relative
arguments before entering its controlled empty working directory, but absolute
paths remove that ambiguity. Replace only the example roots below; keep the
flags, four-shard order, and separate CPU/DirectML interpreters unchanged.

```powershell
$BenchmarkPython = 'C:\ABSOLUTE\benchmark-runtime\python.exe'
$CpuPython = 'C:\ABSOLUTE\cpu-runtime\Scripts\python.exe'
$DirectMlPython = 'C:\ABSOLUTE\directml-runtime\Scripts\python.exe'
$Repo = 'C:\ABSOLUTE\bstrings'
$Evidence = 'D:\ABSOLUTE\ocr-acceptance'
$Dataset = 'D:\ABSOLUTE\cord-v2-train'
$Wrapper = "$Repo\tools\enrichment\benchmark_ocr_acceptance.py"
$Selection = "$Repo\tools\enrichment\cord-v2-train-selection-v1.json"
$Worker = "$Repo\tools\enrichment\bstrings_ocr.py"
$ModelPack = 'D:\ABSOLUTE\ocr-model-pack\ocr-model-pack.json'
$Shard0 = "$Dataset\train-00000-of-00004-b4aaeceff1d90ecb.parquet"
$Shard1 = "$Dataset\train-00001-of-00004-7dbbe248962764c5.parquet"
$Shard2 = "$Dataset\train-00002-of-00004-688fe1305a55e5cc.parquet"
$Shard3 = "$Dataset\train-00003-of-00004-2d0cd200555ed7fd.parquet"
```

The calibration command remains useful only for CORD development diagnostics:

```powershell
& $BenchmarkPython -I -B $Wrapper calibration `
  --parquet $Shard0 --parquet $Shard1 --parquet $Shard2 --parquet $Shard3 `
  --selection-manifest $Selection --worker $Worker --model-pack $ModelPack `
  --cpu-python $CpuPython --directml-python $DirectMlPython `
  --work-directory "$Evidence\calibration-work" `
  --output "$Evidence\calibration-report.json" `
  --policy-output "$Evidence\ocr-acceptance-policy.json"
```

The report stores only stable artifact paths relative to the calibration
evidence root. The raw evidence tree still contains absolute host paths in its
worker manifests and inventories, so keep that tree private or in an encrypted
archive; publish only path-free reports, policies, and checksum manifests. For
the command above the required live evidence root is
`D:\ABSOLUTE\ocr-acceptance\calibration-work\calibration`; keep it with the
report and policy. The former confirmation command is intentionally omitted:
the checked-in retirement marker causes the CLI to fail before the ledger or
any confirmatory output can be created. Use the separate SROIE acceptance
protocol once it is frozen instead.

## Development observations

A component-only, third-party PaddleOCR-VL 1.6 GGUF path is not promoted. On
the already-development CORD validation rows 0-19 it produced 82.6% token
recall but only 7.3% precision (13.4% F1), with long repeated/hallucinated
output on many receipts. That experiment did not run the official
[PaddleOCR-VL pipeline](https://www.paddleocr.ai/latest/en/version3.x/pipeline_usage/PaddleOCR-VL.html),
which combines layout analysis, per-element VLM recognition, and ordered
merging; upstream explicitly warns that the VLM component alone can hallucinate
excess text. This is a component-screening result, not a comparison against the
confirmatory corpus. The official pipeline would require its own frozen
candidate, complete provenance, and clean holdout.
