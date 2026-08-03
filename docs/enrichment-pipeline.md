# Extractor and translation enrichment

`bstrings` can now apply its regex catalog to strings recovered by other tools.
The first adapter uses Magika to route extracted files, FLOSS to recover
executable strings that ordinary byte scanning cannot see, and an optional
offline translation pass to translate candidate text before matching it again.
Hy-MT2 through llama.cpp is the recommended path for its supported languages;
MADLAD-400 remains available when much wider language coverage matters more
than speed.

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
| Hy-MT2 or MADLAD-400 | Produces an optional offline English child record from a recovered string | It does not prove that a translated identifier existed in the source bytes |
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

## Offline translation

The current recommendation is the Apache-2.0
[Hy-MT2-1.8B GGUF](https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF), using Q8
when translation quality is the priority and Q4_K_M when memory or speed is
tighter. The adapter owns a short-lived llama.cpp server, binds it to
`127.0.0.1`, disables the web UI and reasoning output, uses deterministic
decoding, and tears the server down before returning. It hashes the GGUF before
startup and records the exact model, revision, hash, llama.cpp version, and
actual CPU/CUDA path on every translated child.

Download a pinned model revision and a pinned llama.cpp release before moving
the examination environment offline. The model files are not bundled with
`bstrings`:

```powershell
hf download tencent/Hy-MT2-1.8B-GGUF `
  --revision 1cd5208700acedef4ef93019b6cfc148b8522d45 `
  --include Hy-MT2-1.8B-Q8_0.gguf `
  --include LICENSE.txt `
  --local-dir C:\forensic-models\hy-mt2-1.8b
```

This example translates an existing normalized string stream. A `.gguf` model
file selects the llama.cpp engine automatically:

```powershell
python tools\enrichment\bstrings_enrich.py `
  --input-jsonl C:\case\results\other-normalized-strings.jsonl `
  --translate `
  --llama-server C:\forensic-tools\llama.cpp\llama-server.exe `
  --translation-model-path C:\forensic-models\hy-mt2-1.8b\Hy-MT2-1.8B-Q8_0.gguf `
  --translation-model-id tencent/Hy-MT2-1.8B-GGUF `
  --translation-revision 1cd5208700acedef4ef93019b6cfc148b8522d45 `
  --translation-model-sha256 5C3FE0B1408A5CEB0143184EF247B11B579C525F4B02B060E6C851BB76FEF1A4 `
  --translation-device auto `
  --translation-target en `
  -o C:\case\results\other-normalized-strings-translated.jsonl
```

`auto` uses CUDA when the supplied llama.cpp binary lists a CUDA device and
otherwise uses CPU. `cuda` fails before examination if no CUDA device is
visible; `cpu` forces zero GPU layers. The Q8 and Q4 CPU/GPU paths were both
exercised during integration. The server executable itself is an explicit,
local dependency so an examiner can pin and hash the build used in a case.

Hy-MT2 does not replace MADLAD everywhere. Its model card describes support
for 33 languages (Hugging Face metadata currently exposes 36 language tags),
whereas [MADLAD-400](https://huggingface.co/google/madlad400-3b-mt) exposes 419.
Keep MADLAD as a fallback for languages outside Hy-MT2's supported set:

```powershell
uv pip install --python C:\forensic-tools\translate\Scripts\python.exe `
  "torch>=2.7,<3" "transformers>=4.57,<5" "sentencepiece>=0.2,<1"

C:\forensic-tools\translate\Scripts\python.exe `
  tools\enrichment\bstrings_enrich.py `
  --input-jsonl C:\case\results\other-normalized-strings.jsonl `
  --translate `
  --translation-engine madlad `
  --translation-model-path C:\forensic-models\madlad400-3b-mt `
  --translation-model-id google/madlad400-3b-mt `
  --translation-revision fa184c675da0b5c9e1c8694fccd4e12e2d422094 `
  --translation-model-sha256 66FF5F8FCAF92291DA486FDFBD4D5233CEC90E1359348A56E3172C978B3A76D4 `
  --translation-device cpu `
  -o C:\case\results\other-normalized-strings-translated.jsonl
```

The MADLAD runner sets `HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1`, and
loads local files only. Its `auto` device gate still requires at least 16 GiB
of CUDA memory for the unquantized 11.8 GB checkpoint; otherwise it selects
CPU. Transformers 5 remains rejected because it produced destructive
repeated-token output with this checkpoint during validation.

Google ML Kit's compact offline models remain mobile SDK components, not a
supported Windows/server engine. TranslateGemma 4B is a strong 2026 candidate,
but its weights required accepted Gemma access and were unavailable to the
unauthenticated test host, so it was not promoted without a hands-on result.
NLLB-200 and SeamlessM4T were excluded as defaults because their model cards
use CC-BY-NC-4.0. See the
[full benchmark record](translation-benchmark-2026-08-04.md) for the research
gate and measured comparison.

Only strings containing letters and within the configured character limits
are translated. Empty or unchanged translations are not emitted. Translation
and regex matching remain separate steps so model policy, hardware choice,
and regex policy stay independently auditable.

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

The 2026-08-04 model-selection gate then ran 60 deterministic WMT24++ rows
(five each across 12 languages) and 12 synthetic forensic strings. Hy-MT2 Q8
beat the existing unquantized MADLAD CPU path on both quality suites, preserved
all 22 identifiers, and translated 13.1 times as many strings per second. A
second Q8 run produced the same 72 hypotheses byte for byte. The Q4 build was
18.9 times faster than MADLAD and also retained every identifier, but Q8 kept
the stronger quality score and is the recommendation when it fits.

| Model/runtime | WMT24++ chrF++ | Forensic chrF++ | Exact identifiers | Strings/s |
| --- | ---: | ---: | ---: | ---: |
| Hy-MT2-1.8B Q8, llama.cpp/CUDA | 59.74 | 87.53 | 22/22 | 1.289 |
| Hy-MT2-1.8B Q4_K_M, llama.cpp/CUDA | 58.42 | 86.31 | 22/22 | 1.855 |
| MADLAD-400-3B-MT, Transformers/CPU | 54.65 | 85.94 | 22/22 | 0.098 |

These figures describe one laptop and a deliberately small selection gate;
they are not universal model rankings. They are sufficient to choose the
adapter's preferred path on the reviewed hardware, while the checked-in
benchmark makes later model or runtime changes falsifiable.

## Tests

```powershell
dotnet test bstrings.Tests\bstrings.Tests.csproj --configuration Release
python -m unittest discover -s tools\enrichment\tests -v
```

The tests cover FLOSS category normalization, distinct location types, stable
parent/child translation lineage, built-in capture semantics, malformed
records, translation batch cardinality, and atomic output replacement.
