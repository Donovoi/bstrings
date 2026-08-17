# Command reference

This guide gives common commands. Terminal help is the exact reference for your
installed version.

```powershell
.\bstrings-kit\bstrings.exe help
.\bstrings-kit\bstrings.exe help analyze
.\bstrings-kit\bstrings.exe help bundle verify
```

The current public v1.9.17 release uses historical install names. Use the paths
shown on its release page.

## Verify the kit

This command checks the installed kit. It can also check a copied kit on
another computer.

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

Exit code 0 confirms a verified kit. Any other exit code means verification
failed.

## Analyze a file or directory

Use `-f` for one file:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -f D:\evidence\memory.raw `
  -o D:\results\memory `
  --full
```

Use `-d` for a directory:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence\carved `
  -o D:\results\carved `
  --full
```

The output directory must be new or empty for a new analysis. Use explicit
resume for a supported incomplete directory.

## Resume an incomplete analysis

Resume reads the existing incomplete output directory:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -r `
  -o D:\results\memory
```

`-r` is the short form of `--resume`. A normal resume accepts only the output
directory and an optional `--bundle-root`. It restores the saved input and
effective analysis options. Additional input or engine settings are rejected.

Use `--bundle-root` only when the same verified kit moved to another path:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -r `
  -o D:\results\memory `
  --bundle-root D:\tools\bstrings-kit
```

The kit bytes and identity must still match the saved run. The output directory
must be on a physical local filesystem. Resume refuses a network result share.

Before it reuses a completed stage, resume checks:

- The input inventory and content hashes
- The saved analysis options
- The executable, kit, models, engines, and policies
- Each completed stage and its checkpoint
- The output file set
- Exclusive access to the output directory

A saved-contract or checkpoint identity mismatch stops before evidence output
changes. Resume moves recognized but uncommitted files from the interrupted
stage to that attempt's diagnostic log. It then reruns the whole stage. An
unknown file causes refusal. A complete run cannot be resumed or overwritten.

Version 2.1.2 accepts one saved-plan change:

```powershell
.\bstrings-kit\bstrings.exe analyze -r -o D:\results\memory `
  -e translation --bundle-root D:\tools\bstrings-kit-2.1.1
```

This path requires an exact incomplete v2.1.1 Full run with checkpoints 1
through 7 and no later checkpoint. The supplied path is the saved source kit.
The current 2.1.2 kit stays beside the running executable. The command archives
the saved Auto selection as superseded history, commits empty Off stages 7 and
8, and continues. It does not start language detection or model translation.
Other resume-time exclusions are rejected.

Resume works at whole-stage boundaries. It repeats the stage that was active
when the run stopped. It does not reuse partial translation output or the
temporary translation cache.

A narrow compatibility path can import one supported incomplete v2.0.0 shape
that predates checkpoints. It requires the exact published v2.0.0 executable
and kit identities, the known artifact prefix, built-in `all` patterns, and
full input and record validation. The saved routing must have no FLOSS or OCR
candidates, and their outputs must be empty. It refuses translation or
later-stage output. The import records decoding as `off` and starts translation
again. Other old output is not supported.

## Use the complete preset

`--full` selects the normal complete analysis preset. It enables native
extraction, routed FLOSS and OCR, language checks, local translation, all
patterns, and reports.

Base64 decoding remains optional. Add `--decode auto` when you need it.

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -f D:\evidence\memory.raw `
  -o D:\results\full-with-decoding `
  --full --decode auto
```

## Exclude engines

Use `-e` or `--exclude-engine` with `--full`.

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence `
  -o D:\results\without-ocr-or-translation `
  --full -e ocr,translation
```

You can repeat the option:

```powershell
--full -e ocr -e translation
```

Valid names are `native`, `floss`, `ocr`, `decode`, and `translation`.

Combining an engine option with an exclusion for the same engine causes an
error. Empty, unknown, or duplicate names also cause an error.

## Select engines directly

Native extraction only:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -f D:\evidence\memory.raw `
  -o D:\results\native `
  --native-extraction on `
  --recover-executable-strings off `
  --ocr off --translation off
```

FLOSS only:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence\executables `
  -o D:\results\floss `
  --native-extraction off `
  --recover-executable-strings force `
  --ocr off --translation off
```

OCR only:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -d D:\evidence\documents `
  -o D:\results\ocr `
  --native-extraction off `
  --recover-executable-strings off `
  --ocr force --translation off
```

Native extraction with decoding:

```powershell
.\bstrings-kit\bstrings.exe analyze `
  -f D:\evidence\memory.raw `
  -o D:\results\decoded `
  --decode auto --translation off
```

Translation and decoding transform source records. They do not replace native
extraction, FLOSS, or OCR. You cannot disable all three source engines.

## Important engine options

| Function | Option | Values |
| --- | --- | --- |
| Native extraction | `--native-extraction` | `on`, `off` |
| FLOSS | `--recover-executable-strings` | `off`, `auto`, `force` |
| OCR | `--ocr` | `off`, `auto`, `force` |
| Decoding | `--decode` | `off`, `auto`, `force` |
| Translation | `--translation` | `off`, `auto`, `force` |
| Full exclusions | `-e`, `--exclude-engine` | comma-separated engine names |

`force` broadens attempts. It does not bypass integrity checks.

## Patterns

List built-in patterns:

```powershell
.\bstrings-kit\bstrings.exe -p
```

Select all patterns:

```powershell
--lr all
```

`all` selects the 77 default patterns. Its `b64` result requires canonical
Base64 plus validated decoded text or a recognised binary signature.

Add the broad Base64-shaped candidate pattern when required:

```powershell
--lr "all,b64_candidate"
```

`b64_candidate` is disjoint from `b64`. It can include short, hexadecimal,
opaque, and oversized values that only satisfy the canonical Base64 shape.

Select names or groups:

```powershell
--lr "pii,credentials,browser,registry,wallets"
```

You can also supply a custom .NET regular expression with `--lr`.

Pattern matches are candidates. They do not establish identity, ownership,
compromise, or malicious activity.

## Hardware controls

`--processor auto` selects a tested extraction backend. Use `cpu`, `gpu`, or
`hybrid` only when you need an explicit choice.

OCR supports `--ocr-device auto`, `cpu`, `directml`, and accepted specialist
settings. Translation supports `--translation-device auto`, `cpu`, and the
packaged accepted CUDA path.

An automatic preflight failure selects a tested fallback before evidence work.
An explicit unsupported device fails closed.

## Progress and cancellation

Long stages show percentage, rate, and other available progress data. A
percentage shows completed work. It is not elapsed time.

Press `Ctrl+C` once to request cancellation. Cancelled or failed output remains
available for diagnosis. Resume starts only after the process has stopped.

## Completion status

A complete run has all these results:

- Exit code 0
- `run.json` status `complete`
- `summary.json` status `complete`
- No `.incomplete` file

Partial reports do not meet this completion contract.

## Legacy flat output

The root command without `analyze` writes one flat output file. Use it only when
you need compatibility with an older workflow.

```powershell
.\bstrings-kit\bstrings.exe `
  -f D:\evidence\memory.raw `
  --lr all --ro --off --trace `
  -o D:\results\matches.csv
```

The integrated `analyze` command gives stronger completion and provenance
records.

See [Analysis stages](enrichment-pipeline.md) and
[Output and provenance](output-and-provenance.md) for more detail.
