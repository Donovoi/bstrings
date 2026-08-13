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

Run this command before you analyze evidence. Run it again after you copy the
kit to another computer.

```powershell
.\bstrings-kit\bstrings.exe bundle verify
```

A nonzero exit code means that you must not use the kit.

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

The output directory must be new or empty.

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

Do not set an engine option and exclude the same engine. Empty, unknown, or
duplicate names cause an error.

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

Select names or groups:

```powershell
--lr "pii,credentials,browser,registry,wallets"
```

You can also supply a custom .NET regular expression with `--lr`.

Pattern matches are candidates. Validate each important result with other case
evidence.

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

Press `Ctrl+C` once to request cancellation. Keep cancelled or failed output for
diagnosis.

## Confirm completion

A complete run has all these results:

- Exit code 0
- `run.json` status `complete`
- `summary.json` status `complete`
- No `.incomplete` file

Do not treat partial reports as complete evidence.

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
