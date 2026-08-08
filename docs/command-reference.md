# Terminal help and command reference

The normal examiner interface is `bstrings.exe`. It has three user-facing
paths:

| Goal | Command path | Output |
| --- | --- | --- |
| Complete forensic workflow | `bstrings.exe analyze ...` | New result directory with JSONL, TSV, histograms, logs, and completion records |
| Verify or assemble a complete offline kit | `bstrings.exe bundle ...` | Verification result or a new complete bundle directory |
| Fast native strings/search compatibility | `bstrings.exe -f ...` or `-d ...` | One text, CSV, or JSONL file plus its completion marker |

## Get help in the terminal

The conventional command-style and option-style forms are equivalent:

```powershell
.\bstrings.exe help
.\bstrings.exe --help
.\bstrings.exe help analyze
.\bstrings.exe analyze --help
.\bstrings.exe help bundle
.\bstrings.exe help bundle verify
.\bstrings.exe --version
```

Help is local and does not inspect evidence, create output, or use the network.
The displayed native `--processor auto` calibration threshold is calculated
for the current CPU. It is therefore allowed to differ between workstations.

## Recommended complete analysis

Use the complete quality kit and put the output outside the input tree:

```powershell
.\bstrings.exe analyze `
  -d "D:\evidence\carved-files" `
  --full `
  -o "D:\results\case-01"
```

For one file or raw byte image, replace `-d` with `-f`. Specify exactly one.
The result directory must be new or empty. Never reuse a directory from a
failed or cancelled run; retain it for diagnosis and choose a new output path.

`--full` supplies these defaults:

- native ASCII/Unicode extraction and all 66 built-in patterns;
- one batched, fail-open Magika/signature routing pass;
- automatic routed FLOSS recovery and PDF/image OCR;
- adaptive language detection and high-recall translation selection; and
- offline translation with the single installed 7B Q8_0 quality profile.

An explicit stage choice overrides the corresponding full default. For
example, this keeps reporting and the complete pattern catalogue but disables
FLOSS, OCR, and translation:

```powershell
.\bstrings.exe analyze `
  -f "D:\evidence\memory.raw" `
  --full `
  --recover-executable-strings off `
  --ocr off `
  --translation off `
  -o "D:\results\memory-native"
```

`--full` does not mount a filesystem or carve embedded files from a disk or
memory image. Native extraction can scan the raw bytes; file-level FLOSS and
OCR need carved or mounted executables, documents, and images.

## Patterns and reports

`analyze` defaults to `--lr all`. A comma-separated list or a named group can
narrow the catalogue:

```powershell
.\bstrings.exe analyze -d D:\evidence --lr "email,url3986,ipv4" -o D:\results\network
.\bstrings.exe analyze -d D:\evidence --lr credentials -o D:\results\credentials
.\bstrings.exe analyze -d D:\evidence --lr registry -o D:\results\registry
```

Groups are `pii`, `credentials`, `browser`, `registry`, and `wallets`. `--fr`
adds regular expressions from a file. Use `bstrings.exe -p` to list built-in
names, descriptions, and expressions.

Every completed `analyze` run writes the authoritative JSONL evidence graph
and these review surfaces:

- `content-routing.jsonl` and `engine-status.jsonl`, which explain each route
  and the terminal native/FLOSS/OCR coverage of every routed input;
- `findings.tsv`, suitable for Timeline Explorer and spreadsheet filtering;
- `pattern-histogram.tsv`, including zero-count requested patterns;
- `feature-histogram.tsv`, with exact matched-feature counts; and
- `pattern-histogram.html`, a self-contained pattern-volume chart.

## Hardware controls

The three hardware selectors control different work:

| Option | Controls | Normal advice |
| --- | --- | --- |
| `--processor` | Native byte-string extraction | Leave `auto`; it avoids CUDA startup below the displayed host threshold and calibrates larger eligible inputs |
| `--ocr-provider` | Raster/PDF OCR inference | Leave `auto`, or use `cpu`, `directml`, or `hybrid` when a specific accepted path is required |
| `--translation-device` | Local llama.cpp translation | Leave `auto`; the standard quality kit uses its accepted CPU runtime |

High CPU/GPU utilization is not the objective. Storage reads, memory
bandwidth, result transfer, and serialized output can be the limiting stage.
Use `--trace` on the legacy scanner to see native backend selection and final
CPU/GPU chunk totals. Explicit `gpu` or `hybrid` is a diagnostic/forced choice,
not a promise of lower wall time.

For a custom accepted CUDA translation runtime, `cuda` requests full offload.
`hybrid` additionally requires a positive, exact
`--translation-gpu-layers`. `--translation-strict-determinism` forces one
translation slot and disables prompt-cache reuse; do not combine it with
`--translation-parallelism` above 1.

## Progress, cancellation, and completion

Long-running user operations report percentage completion:

- `Progress: analysis:` is the overall planned-stage fraction;
- native extraction reports chunks and strings;
- content triage reports all fixed inputs;
- FLOSS recovery and OCR report their routed candidate files;
- language triage and translation filtering report bytes;
- offline translation reports completed candidate records; and
- downloads, pack hashing, assembly, and bundle verification report bytes or
  manifested files.

Percentages are completed work units, not an estimated time remaining. A stage
can spend unequal time between percentage points.

Press Ctrl+C once to request cancellation. A cancelled analysis exits with
code 130 and retains `.incomplete`; a cancelled bundle acquisition preserves
verified cached packs for a retry. Success requires exit code 0, `run.json` and
`summary.json` status `complete`, and no `.incomplete` marker.

## Bundle commands

The complete kit verifies itself by default:

```powershell
.\bstrings.exe bundle verify
```

Maintainers and advanced offline staging workflows can discover the exact
pack options locally:

```powershell
.\bstrings.exe help bundle acquire
.\bstrings.exe help bundle assemble
```

`acquire` resumes verified downloads and assembles a new complete bundle.
`assemble` uses already-cached packs without downloading. Both refuse to merge
into an existing output directory.

## Legacy flat-output scanner

Use the root options only when a single flat file is preferable to the
integrated report set:

```powershell
.\bstrings.exe `
  -f "D:\evidence\memory.raw" `
  --lr all --ro --off --trace `
  -o "D:\results\memory-hits.csv"
```

`--ro` writes the matched range instead of the containing string, `--off`
includes source byte offsets, and a `.csv` output name selects CSV. For the
same native-only scan with full provenance and reports, use `analyze` with
FLOSS, OCR, and translation explicitly disabled.

See [download and installation](download-and-install.md),
[analysis and translation](enrichment-pipeline.md), and
[output and provenance](output-and-provenance.md) for the complete operational
and interpretation boundaries.

## Architecture decisions versus installed help

Installed `bstrings.exe help` is authoritative for the behavior available in
that executable. Proposed or accepted target architectures do not become user
features until their implementation, tests, help, and release gates pass.

High-level changes that can alter forensic coverage, provenance, engine/model
selection, privacy, or performance defaults use the
[Robin-round decision policy](architecture/decision-review-policy.md). The early
Magika/content-triage design and its fail-open acceptance gates are recorded in
[ADR-0001](architecture/adr-0001-early-fail-open-content-routing.md).
