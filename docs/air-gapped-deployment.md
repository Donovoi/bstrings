# Air-gapped deployment

`bstrings` can run without an internet connection, package manager, model hub,
or system Python installation. Build the bundle on a connected staging machine,
transfer the finished directory on approved media, verify it inside the
air-gapped environment, and run only the included launchers.

The full bundle may be many gigabytes. Size is deliberately not optimized away:
the model weights, CUDA runtime files, standalone tools, portable Python, and
optional broad-language or RAPIDS environments are copied in full.

## Offline invariant

The air-gap path has these properties:

- the core Windows executable is a self-contained .NET publish;
- the enrichment adapter uses a bundled Python runtime and standard library;
- Magika, FLOSS, llama.cpp, CUDA runtime libraries, and model weights are local;
- model and package-manager offline variables are forced for every launcher;
- Python audit hooks reject DNS and socket connections outside loopback;
- llama.cpp may use `127.0.0.1` only, between the adapter and its private server;
- RAPIDS never installs Python, Conda, or packages and can use only the bundled
  environment named by `BSTRINGS_RAPIDS_PYTHON`;
- every regular file is covered by a strict SHA-256 manifest, with unexpected,
  missing, linked, resized, or modified files rejected; and
- the translated record records `"airgap": true` in its execution provenance.

URLs stored in pattern documentation are passive source references. The core
scanner never dereferences them.

## 1. Prepare local inputs on a connected staging machine

Acquire and verify every dependency before entering the secure environment.
Keep each tool's license and notice files in the directory supplied to the
builder; the builder copies whole directories rather than individual binaries.

Required inputs are:

1. A self-contained `win-x64` bstrings publish directory.
2. An extracted official CPython Windows embeddable distribution. Python
   documents this as a minimal, isolated runtime intended to be shipped inside
   another application.
3. A local Magika CLI directory. Google's current CLI is written in Rust and
   can be staged with `cargo install --locked magika-cli`.
4. The standalone Windows FLOSS release directory.
5. A complete llama.cpp Windows release directory, including its CUDA runtime
   DLLs when GPU translation is required.
6. A pinned Hy-MT2 GGUF directory containing the selected model and its license.

The bundle test used file `python-3.14.6-embed-amd64.zip` from the official
[Python 3.14.6 release](https://www.python.org/downloads/release/python-3146/),
SHA-256
`DF901E84A896FF1EE720AD03377E0C8D8C2244FDA79808AEEAFF6316DF1CB75C`.
Use the official `embed` archive, not the similarly named compatibility
archive. The builder requires exactly one `python*._pth` file with `import
site` disabled and probes the isolated standard library before copying.

The tested Q8 model inputs remain:

- model ID: `tencent/Hy-MT2-1.8B-GGUF`
- revision: `1cd5208700acedef4ef93019b6cfc148b8522d45`
- file: `Hy-MT2-1.8B-Q8_0.gguf`
- SHA-256: `5C3FE0B1408A5CEB0143184EF247B11B579C525F4B02B060E6C851BB76FEF1A4`

The builder recalculates the hash and writes the actual value into bundle
configuration; it does not trust the filename.

Publish the core first:

```powershell
cargo build --manifest-path native\bstrings_core\Cargo.toml --release --locked

dotnet publish bstrings\bstrings.csproj `
  -c Release `
  -f net9.0 `
  -r win-x64 `
  --self-contained true `
  --no-restore `
  -o C:\staging\bstrings-publish `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None `
  -p:DebugSymbols=false
```

Then assemble the bundle entirely from local paths:

```powershell
.\tools\airgap\Build-AirgapBundle.ps1 `
  -OutputDirectory E:\transfer\bstrings-airgap `
  -PublishedBstringsDirectory C:\staging\bstrings-publish `
  -PythonDirectory C:\staging\python-embed-amd64 `
  -MagikaDirectory C:\staging\magika `
  -FlossDirectory C:\staging\floss-3.1.1 `
  -LlamaDirectory C:\staging\llama-b10248-cuda12.4 `
  -TranslationModelDirectory C:\staging\hy-mt2-q8 `
  -TranslationModelRevision 1cd5208700acedef4ef93019b6cfc148b8522d45
```

The builder never downloads or installs anything. It refuses an existing output
directory, validates every executable and model path before copying, leaves an
`.incomplete` marker after failure, and verifies the finished manifest itself.

To carry the broader MADLAD fallback or a working RAPIDS environment, add:

```powershell
  -MadladModelDirectory C:\staging\madlad400-3b-mt `
  -RapidsPythonDirectory C:\staging\rapids-python
```

The full directories are copied. The connected machine is the only place where
`pip`, `uv`, `hf`, Cargo, NuGet, or any other downloader should be used.

## 2. Record transport integrity separately

At completion the builder prints the SHA-256 of `airgap-manifest.json`. Record
that value through a channel separate from the transfer media, or sign the
manifest under the organization's normal software-transfer procedure. The
inside-bundle manifest proves file integrity and completeness; it cannot prove
its own authenticity if an attacker can replace both files and manifest.

Do not add files to the bundle after manifest creation. The verifier rejects
unexpected files as well as missing or changed ones.

## 3. Verify inside the air gap

Disconnect the validation VM or workstation at the operating-system or virtual
switch level, transfer the directory, compare the separately recorded manifest
hash, and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
cd D:\tools\bstrings-airgap
.\Verify-AirgapBundle.ps1 -TranslationSmoke
```

Verification performs four independent checks:

1. Every bundled file's size and SHA-256 must match the manifest, with no extra
   files or links.
2. The Python runtime must block a synthetic external DNS request while still
   allowing loopback.
3. The four bundled executables must answer local version probes.
4. `-TranslationSmoke` hashes and loads the GGUF, starts llama.cpp on loopback,
   translates a synthetic fixture on CPU, retains `analyst@example.com`, records
   air-gap provenance, and removes its temporary output.

Run the smoke at least once on every target hardware image. A bundle should not
be accepted merely because its hashes match; local driver and runtime loading
must also work.

## 4. Use the bundled launchers

Core extraction:

```powershell
.\Invoke-BstringsAirgap.ps1 `
  -f D:\evidence\image.raw `
  --processor auto `
  --off `
  -s `
  -o D:\results\image-strings.txt
```

FLOSS recovery and local Hy-MT2 translation:

```powershell
.\Invoke-EnrichmentAirgap.ps1 `
  --input-jsonl D:\results\enriched-strings.jsonl `
  --translate `
  --translation-device auto `
  -o D:\results\enriched-translated.jsonl
```

The enrichment launcher supplies all executable, model, revision, and hash
arguments from `airgap-config.json`; the operator supplies only case inputs and
policy choices. For a bundled MADLAD snapshot, explicitly override the engine,
model directory, ID, revision, and weights hash with the values recorded in the
configuration.

## Operational boundaries

- NVIDIA drivers are host-level software. Stage and approve an offline driver
  installer separately, or use CPU mode.
- The portable Hy-MT2 path needs no third-party Python packages. MADLAD and
  RAPIDS require their complete prebuilt local Python environments.
- The Python guard controls the adapter process. The strongest acceptance test
  is still the complete bundle running in a VM whose virtual NIC is disconnected.
- Updating any executable, model, configuration, or documentation invalidates
  the manifest. Rebuild and re-verify the bundle rather than editing it in place.

Primary upstream references are Python's
[embeddable-package documentation](https://docs.python.org/3/using/windows.html#the-embeddable-package),
Google's [Magika CLI documentation](https://github.com/google/magika#command-line-tool),
Mandiant's [standalone FLOSS release guidance](https://github.com/mandiant/flare-floss),
Hugging Face's [offline-mode guidance](https://huggingface.co/docs/transformers/installation#offline-mode),
and llama.cpp's [installation documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/install.md).
