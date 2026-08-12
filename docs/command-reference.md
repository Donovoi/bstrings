# Terminal help and command reference

This page documents current source. The latest published release is v1.9.17;
its installed `help` output is authoritative for that binary. In particular,
v1.9.17 includes `--native-extraction` but predates the
`-e`/`--exclude-engine` shorthand described below.

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

- `--native-extraction on` for native ASCII/Unicode extraction and all 66
  built-in patterns;
- one batched, fail-open Magika/signature routing pass;
- automatic routed FLOSS recovery and PDF/image OCR;
- fail-open shadow translation-worthiness routing, adaptive language detection,
  and high-recall translation selection; and
- offline translation with the single installed Hy-MT2 7B Q4_K_M Full model.

Full deliberately stays `high-recall`. Examiners who accept lower candidate
volume can explicitly select `--translation-policy high-precision`; its
effective gates are the greater of the configured values and 0.65 confidence /
0.15 target margin. The assessment records both configured and effective
thresholds. High precision is a conservative operating gate, not a calibrated
probability or an accuracy guarantee.

The shadow router records whether a complete record is a prospective
structured-only bypass, but it does not skip language detection or remove a
translation candidate. Ambiguous, mixed, unsupported, and failed router
decisions retain the existing Full path. This diagnostic rollout must pass the
forensic recall and measured-overhead gates in
[ADR-0007](architecture/adr-0007-translation-worthiness-routing.md) before any
authoritative suppression is enabled.

An explicit stage choice overrides the corresponding full default. For
example, this keeps reporting and the complete pattern catalogue but disables
FLOSS, OCR, and translation:

```powershell
.\bstrings.exe analyze `
  -f "D:\evidence\memory.raw" `
  --full `
  --native-extraction on `
  --recover-executable-strings off `
  --ocr off `
  --translation off `
  -o "D:\results\memory-native"
```

`--full` does not mount a filesystem or carve embedded files from a disk or
memory image. Native extraction can scan the raw bytes; file-level FLOSS and
OCR need carved or mounted executables, documents, and images.

## Exclude named engines from Full

`--exclude-engine` (short alias `-e`) is strict shorthand for starting from
Full's defaults and setting named engines to their existing `off` modes. It
requires an explicit `--full` and never enables Full by itself.

This shorthand is available in current source after v1.9.17. With the published
v1.9.17 binary, use the equivalent explicit selectors such as
`--ocr off --translation off`.

```powershell
# Repeat form
.\bstrings.exe analyze -d D:\evidence\carved --full `
  --exclude-engine ocr --exclude-engine translation `
  -o D:\results\without-ocr-translation

# Equivalent comma form
.\bstrings.exe analyze -d D:\evidence\carved --full `
  -e ocr,translation -o D:\results\without-ocr-translation
```

Each occurrence consumes exactly one value token. That token is split on
commas, surrounding whitespace is trimmed, and every segment must be one of
`native`, `floss`, `ocr`, or `translation` (case-insensitive). Repeating the
option and using commas may be combined. Quote a token that contains spaces,
for example `-e "ocr, translation"`.

The command rejects a missing value, an empty segment such as `ocr,`, an
unknown name, and any duplicate or case-duplicate across all occurrences. It
does not accept whitespace-separated multi-arguments, so `-e native ocr` is an
error. An exclusion also conflicts with explicitly supplying that engine's
selector, including a matching `off` value:

| Exclusion | Conflicting selector |
| --- | --- |
| `native` | `--native-extraction` |
| `floss` | `--recover-executable-strings` |
| `ocr` | `--ocr` |
| `translation` | `--translation` |

Selectors for other engines remain valid. Provider, device, scheduling,
threshold, and other tuning options for an excluded engine are still parsed and
validated, but they are inert and do not initialize that engine. All grammar,
conflict, and no-producer errors fail before bundle lookup, result-directory
creation, or evidence access.

The shorthand resolves into the same effective analysis options as the
equivalent explicit `off` command. It creates no new profile, orchestrator path,
or provenance field; the effective modes in `run.json` and the existing routing
and engine-status records remain authoritative. Whole-bundle trust verification
is unchanged.

## Select source producers independently

The published v1.9.17 binaries already include `--native-extraction`.

`analyze` has three source-record producers and one downstream transform:

| Control | Role | Modes |
| --- | --- | --- |
| `--native-extraction` | Native ASCII/Unicode source records | `on` or `off`; default `on`, including Full |
| `--recover-executable-strings` | FLOSS source records | `off`, `auto`, or `force` |
| `--ocr` | PDF text-layer and OCR source records | `off`, `auto`, or `force` |
| `--translation` | Language assessment and/or translated children of selected source records | `off`, `auto`, `all`, or `detect-only` |

Explicit choices override the corresponding Full defaults. At least one source
producer must remain selected. Translation never silently enables one, so a
producerless configuration fails before bundle lookup, output creation, or
evidence access.

```powershell
# Native only
.\bstrings.exe analyze -f D:\evidence\memory.raw `
  --native-extraction on --recover-executable-strings off `
  --ocr off --translation off -o D:\results\native

# FLOSS only; include all FLOSS static and derived categories
.\bstrings.exe analyze -d D:\evidence\executables `
  --native-extraction off --recover-executable-strings force `
  --ocr off --translation off -o D:\results\floss

# OCR only
.\bstrings.exe analyze -d D:\evidence\documents `
  --native-extraction off --recover-executable-strings off `
  --ocr force --translation off -o D:\results\ocr

# Translate selected OCR records without native or FLOSS parents
.\bstrings.exe analyze -d D:\evidence\documents `
  --native-extraction off --recover-executable-strings off `
  --ocr force --translation auto -o D:\results\ocr-translated
```

`auto` still routes conservatively; `force` means attempt every supplied input
for that specialist. A selected specialist with no applicable input or zero
records is a valid completed result. When native is off, FLOSS-only output
includes FLOSS static strings. When native is on, those static FLOSS records
remain omitted to avoid duplicating native coverage.

Pattern matching and report projection are not engines in this contract. They
remain mandatory finalization stages, so every successful specialist-only run
still has the same reviewable JSONL, TSV, histogram, completion, and provenance
surfaces.

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

- specialist `engine-status.jsonl`, which records terminal native/FLOSS/OCR
  coverage of every input, including engines explicitly disabled by the user,
  plus `content-routing.jsonl` to explain each route;
- `findings.tsv`, suitable for Timeline Explorer and spreadsheet filtering,
  including a dedicated `TranslationIntegrity` column;
- `pattern-histogram.tsv`, including zero-count requested patterns;
- `feature-histogram.tsv`, with exact matched-feature counts; and
- `pattern-histogram.html`, a self-contained pattern-volume chart.

## Hardware controls

The three hardware selectors control different work:

| Option | Controls | Normal advice |
| --- | --- | --- |
| `--processor` | Native byte-string extraction | Leave `auto`; it avoids CUDA startup below the displayed host threshold and calibrates larger eligible inputs |
| `--ocr-provider` | Raster/PDF OCR inference | Leave `auto`, or use `cpu`, `directml`, or `hybrid` when a specific accepted path is required |
| `--translation-device` | Local llama.cpp translation | Leave `auto`; current Full source probes the accepted Windows sm89 CUDA p2 path and otherwise selects CPU before evidence work |

High CPU/GPU utilization is not the objective. Storage reads, memory
bandwidth, result transfer, and serialized output can be the limiting stage.
Use `--trace` on the legacy scanner to see native backend selection and final
CPU/GPU chunk totals. Explicit `gpu` or `hybrid` is a diagnostic/forced choice,
not a promise of lower wall time.

Current Full `auto` treats CUDA as accepted only after the bundled backend has
loaded the exact Q4 model, completed a synthetic request, and reported full
33/33 layer offload at parallelism two. The reviewed placement also reported a
410.69 MiB `CPU_Mapped` model buffer, so full layer offload is not described as
zero CPU residency. If this transaction-free probe fails, CUDA is closed and
CPU is started and tested before evidence inference, cache insertion, or
translation output. Explicit `cuda` fails closed. The selected provider is
frozen after the first evidence request; a later failure leaves the stage
incomplete rather than switching providers.

This CUDA acceptance is scoped to Windows and the reviewed RTX 4060
Laptop/sm89 host. It is not a general NVIDIA compatibility claim. The automatic
schedule is p2; p4 and `hybrid` remain expert/experimental boundaries and have
not inherited Full acceptance. `--translation-strict-determinism` forces one
translation slot and disables prompt-cache reuse; do not combine it with
`--translation-parallelism` above 1. Native extraction and OCR keep their own
independent GPU policies. See
[ADR-0006](architecture/adr-0006-q4-cuda-full-translation.md).

## Progress, cancellation, and completion

Long-running user operations report percentage completion:

- `Progress: analysis:` is the overall planned-stage fraction;
- native extraction reports chunks and strings;
- content triage reports all fixed inputs;
- FLOSS recovery and OCR report their routed candidate files;
- language triage and translation filtering report bytes;
- offline translation reports completed candidate records plus record rate,
  ETA, run-local cache hits, distinct model inputs, and preservation fallbacks;
  and
- downloads, pack hashing, assembly, and bundle verification report bytes or
  manifested files.

Percentages are completed work units. Translation additionally calculates its
ETA from observed record throughput; it is an estimate and can change as input
lengths change. Exact run-local deduplication reduces redundant model calls but
can still leave long CPU runs when Full selects many mostly unique records.

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

The quality bundle remains one atomic trust profile regardless of runtime
selection. `bundle verify` and `analyze` do not ignore a corrupt unselected
component in that shared directory. Runtime engine independence therefore does
not promise smaller downloads, partial installation, or operation from a
damaged complete kit. Physically separate capability profiles are deferred by
[ADR-0008](architecture/adr-0008-independent-engine-execution.md).

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
[ADR-0001](architecture/adr-0001-early-fail-open-content-routing.md). Translation
identifier semantics, isolated fallbacks, run-local exact deduplication, and the
decision to keep Full high-recall are recorded in
[ADR-0005](architecture/adr-0005-translation-integrity-and-run-dedup.md).
The pre-Lingua shadow router and its promotion gates are recorded in
[ADR-0007](architecture/adr-0007-translation-worthiness-routing.md).
