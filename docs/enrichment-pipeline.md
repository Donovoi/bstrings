# Extractor and translation enrichment

`bstrings` can now apply its regex catalog to strings recovered by other tools.
The first adapter uses Magika to route extracted files, FLOSS to recover
executable strings that ordinary byte scanning cannot see, and an optional
offline MADLAD-400 pass to translate candidate text before matching it again.

This is intentionally a companion pipeline. Magika, FLOSS, Python, PyTorch,
and model weights are not installed or loaded by the core `bstrings`
executable. Large evidence images should still be scanned directly with
`bstrings`; carved or filesystem-extracted files are the inputs to this
adapter.

## What each stage does

| Stage | Role | What it does not do |
| --- | --- | --- |
| Magika | Classifies a complete extracted file and decides whether FLOSS is appropriate | It does not extract strings and should not be treated as a classifier for arbitrary disk blocks |
| FLOSS | Recovers Go/Rust language strings plus stack, tight-loop, and decoded strings from supported executables | It does not replace filesystem parsing, carving, or normal byte-string extraction |
| MADLAD-400 | Produces an optional offline English child record from a recovered string | It does not prove that a translated identifier existed in the source bytes |
| `bstrings --enrich-jsonl` | Applies the same built-in or custom regex semantics to every normalized record | It never rewrites or discards the parent evidence record |

FLOSS is only selected automatically for Magika's `pebin` result. Known 32-
or 64-bit shellcode can be forced with `--force-floss --floss-format sc32` or
`sc64`. That override is explicit because treating arbitrary data as shellcode
is expensive and produces weak provenance.

## Install the optional tools

Pin the adapter dependencies in a disposable environment or forensic VM. The
versions validated for this integration were Magika Python package `1.0.3`
(its bundled Rust CLI reports `magika 1.1.0 standard_v3_3`) and the FLOSS
`3.1.1` Windows release.

```powershell
uv venv --python 3.14 C:\forensic-tools\magika
uv pip install --python C:\forensic-tools\magika\Scripts\python.exe magika==1.0.3

# Download floss-v3.1.1-windows.zip from the official Mandiant release,
# verify it, and extract floss.exe into C:\forensic-tools\floss.
```

The Windows FLOSS archive downloaded from the official `v3.1.1` GitHub
release during this review had SHA-256
`6C71089B8C629C69424B042769F1565F71ADC6CD24B2F8D3713C96FA7FDAC2FB`.
Record and verify the hash of the asset you actually acquire; do not treat this
note as a general software-signing mechanism.

## Recover strings and run regexes

Run the adapter over files already extracted or carved from the evidence:

```powershell
python tools\enrichment\bstrings_enrich.py `
  --magika C:\forensic-tools\magika\Scripts\magika.exe `
  --floss C:\forensic-tools\floss\floss.exe `
  --floss-timeout 1800 `
  -o C:\case\results\enriched-strings.jsonl `
  C:\case\extracted\sample.exe
```

FLOSS static strings are omitted by default because native `bstrings` has
already recovered most of them. Add `--include-floss-static` when a
single-source FLOSS export is more useful than avoiding duplicates.

Then apply any built-in or custom bstrings pattern:

```powershell
bstrings.exe `
  --enrich-jsonl C:\case\results\enriched-strings.jsonl `
  --lr all `
  -o C:\case\results\enriched-regex-matches.jsonl
```

`--fr` is supported too. Blank lines and lines beginning with `#` are ignored;
the remaining lines are named `file:<line-number>` in output. Enrichment
output is always JSONL because the five-column CSV format cannot carry the
required lineage.

When `-o` is supplied, the writer uses a sibling temporary file and only
replaces the requested output after every input record has parsed and every
regex has completed. A bad schema, missing parent, or regex timeout therefore
leaves an existing result untouched. Console output cannot provide that
rollback guarantee.

## Offline Google research translation

Google ML Kit exposes the same compact models used by Google Translate's
offline mode, but its supported SDKs are Android and iOS. It is not a
supported Windows or server-side engine. For a desktop forensic workflow this
adapter uses MADLAD-400-3B-MT instead: a Google Research multilingual T5 model
with a Hugging Face conversion. It is an open research model, not the
proprietary Google Translate service, and its model card warns that quality
varies by language and domain.

Download and pin a snapshot before moving the analysis environment offline.
This reviewed snapshot is about 11.8 GB in its unquantized safetensors form:

```powershell
hf download google/madlad400-3b-mt `
  --revision fa184c675da0b5c9e1c8694fccd4e12e2d422094 `
  --local-dir C:\forensic-models\madlad400-3b-mt
```

Install the translation dependencies into the adapter environment, then pass
only the local snapshot. The runner sets `HF_HUB_OFFLINE=1`, sets
`TRANSFORMERS_OFFLINE=1`, and asks Transformers for local files only, so it
cannot silently fetch model data during examination.

```powershell
uv pip install --python C:\forensic-tools\translate\Scripts\python.exe `
  "torch>=2.7,<3" "transformers>=4.57,<5" "sentencepiece>=0.2,<1"

C:\forensic-tools\translate\Scripts\python.exe `
  tools\enrichment\bstrings_enrich.py `
  --magika C:\forensic-tools\magika\Scripts\magika.exe `
  --floss C:\forensic-tools\floss\floss.exe `
  --translate `
  --translation-model-path C:\forensic-models\madlad400-3b-mt `
  --translation-model-id google/madlad400-3b-mt `
  --translation-revision fa184c675da0b5c9e1c8694fccd4e12e2d422094 `
  --translation-model-sha256 66FF5F8FCAF92291DA486FDFBD4D5233CEC90E1359348A56E3172C978B3A76D4 `
  --translation-target en `
  -o C:\case\results\enriched-and-translated.jsonl `
  C:\case\extracted\sample.exe
```

The same translator can enrich normalized strings produced by another
extractor without invoking Magika or FLOSS again:

```powershell
C:\forensic-tools\translate\Scripts\python.exe `
  tools\enrichment\bstrings_enrich.py `
  --input-jsonl C:\case\results\other-normalized-strings.jsonl `
  --translate `
  --translation-model-path C:\forensic-models\madlad400-3b-mt `
  --translation-revision fa184c675da0b5c9e1c8694fccd4e12e2d422094 `
  --translation-model-sha256 66FF5F8FCAF92291DA486FDFBD4D5233CEC90E1359348A56E3172C978B3A76D4 `
  -o C:\case\results\other-normalized-strings-translated.jsonl
```

`--translation-device auto` uses CUDA only when PyTorch reports CUDA and the
GPU has at least 16 GiB of memory; otherwise it uses CPU. This conservative
gate avoids loading the 11.8 GB checkpoint onto an 8 GiB GPU and failing after
analysis has begun. `cpu` and `cuda` can be forced for a separately validated
environment. A CUDA run also requires a CUDA-enabled PyTorch wheel selected
from the [official PyTorch installer](https://pytorch.org/get-started/locally/);
the ordinary package source may provide a CPU build. Quantized Candle or
CTranslate2 workers are promising future
paths, but they should not become defaults until their translations match the
pinned reference model on a multilingual forensic corpus.

Only strings containing letters and within the configured character limits
are translated. Empty or unchanged translations are not emitted. Translation
is batched, but regex matching remains a separate bstrings step so CPU, GPU,
and regex policy remain independently auditable.

## Provenance and interpretation

Every input string has a stable SHA-256 record identifier. A translated record
is a child with `parentRecordId`, the engine and runtime version, model
identifier, exact revision and verified weights SHA-256, source-language mode,
and target language. Regex results copy that lineage. Parents must appear
before children; missing, out-of-order, or duplicate record identifiers fail
the run. The model file is hashed before model loading; a mismatch fails before
the output transaction begins.

| `evidenceClass` | Meaning |
| --- | --- |
| `byte-native` | The string maps to a file offset and came from native/static/language extraction |
| `derived-extractor` | FLOSS reconstructed it during stack emulation or decoding; its location may be a program counter or virtual address |
| `derived-translation` | A model produced the searched text from a parent record |

A match in translated text is an investigative lead. Translation can
normalize punctuation, alter identifiers, or create text that happens to look
like an email address, path, hash, or wallet. Confirm consequential findings
against the untranslated parent and surrounding evidence. The location on a
translated row belongs to the parent; it is not a claim that the translated
characters exist at that byte offset.

## Validation result

The functional gate used Mandiant's open FLOSS test fixture
`test-decode-in-place.exe` from `flare-floss-testfiles` commit
`53e910192ea6f3f4c825370389393bdd9631580c`. The fixture SHA-256 was
`378C3C25C7D844F51B29624EECCAE6626F3BDEA9D3EED5AB33E7F7162AC7329E`.

- Magika classified it as `pebin` at `0.999` confidence.
- Native bstrings returned zero `hello world` matches.
- FLOSS recovered two decoded `hello world` records at distinct virtual
  addresses.
- `bstrings --enrich-jsonl --lr "hello world"` returned both records with
  `derived-extractor` provenance.

This proves additional-string recovery and end-to-end lineage. It is not a
speed comparison or a claim about recall on unrelated binaries.

A second live gate processed three Mandiant fixtures in one invocation. Magika
routed all three as PE files, FLOSS emitted eight unique strings (six decoded
and two tight-loop records), and a custom `hello|goodbye world` regex returned
all eight with the correct extractor kind. The adapter invocation took about
24.4 seconds on the reviewed host. That is a small functional workload, not a
throughput benchmark.

The offline translation gate used the pinned model revision above. Its
`model.safetensors` SHA-256 matched the repository metadata:
`66FF5F8FCAF92291DA486FDFBD4D5233CEC90E1359348A56E3172C978B3A76D4`.
On the reviewed Core Ultra 9 185H laptop, a two-record CPU batch completed in
about 25.8 seconds including the full 11.8 GB SHA-256 check, process startup,
and model loading from a warm file cache. Spanish `contraseña` became
`password`; the raw record did not match
`(?i)\bpassword\b`, while its translated child did and retained the parent
identifier and original offset.

Transformers `5.14.1` was rejected during this validation. It loaded the same
weights but generated repeated `e` tokens until the output limit, destroying
the test identifier. Transformers `4.57.6` generated the correct translations
and preserved the identifier, so the adapter now enforces `>=4.57,<5` at both
installation and runtime. This is why dependency version is evidence
provenance rather than incidental environment detail.

## Tests

```powershell
dotnet test bstrings.Tests\bstrings.Tests.csproj --configuration Release
python -m unittest discover -s tools\enrichment\tests -v
```

The tests cover FLOSS category normalization, distinct location types, stable
parent/child translation lineage, built-in capture semantics, malformed
records, translation batch cardinality, and atomic output replacement.
