# bstrings

`bstrings` is a fast forensic string extractor and pattern matcher with one-command executable recovery, offline language triage, local translation, and provenance-preserving results.

## Why choose this fork?

| Decision point | Donovoi/bstrings | Original bstrings | [ripgrep](https://github.com/BurntSushi/ripgrep) | [bulk_extractor](https://github.com/simsong/bulk_extractor) |
| --- | --- | --- | --- | --- |
| 100 GiB sparse URL/email scan | **42.19 s / 2,427 MiB/s** | 187.66 s / 545.7 MiB/s | 180.77 s / 566.5 MiB/s | 319.34 s / 320.7 MiB/s |
| Reviewed 33-pattern exactness at 256 MiB | **33/33** | 15/33 | 25/33 stream-comparable | 0/33 through find/RE2 |
| Shape-correct invalid corpus | **73,480/73,480 rejected** | Regex baseline retained all | Not measured | Scanner semantics differ |
| Pattern catalog | **51 patterns; 27-pattern `wallets` group with semantic validation** | Smaller legacy catalog | User expressions | Purpose-built scanners |
| Enrichment and lineage | Native + FLOSS + language assessment + offline translation | None | None | Recursive decoding/carving, without this parent/child catalog pipeline |
| Hardware paths | SIMD CPU, Rust, CUDA, and CPU+GPU hybrid | CPU | CPU | Multi-threaded CPU |

These are synthetic warm-cache measurements, not universal rankings. Timings counted only after exact records and offsets passed boundary and terminal tests. Read the [scale](docs/scale-benchmark-2026-08.md), [pattern/engine](docs/pattern-engine-benchmark-2026-08.md), [validity](docs/pattern-validity-review-2026-08.md), and [translation](docs/translation-benchmark-2026-08-04.md) reports before generalising them.

## Download, verify, run

The complete Windows x64 CPU build is currently delivered by the [Windows build workflow](https://github.com/Donovoi/bstrings/actions/workflows/dotnet-desktop.yml). After this work is merged, choose **Run workflow**, wait for all offline checks, and download the `bstrings-win-x64-offline-cpu` artifact. It contains the ZIP and its SHA-256 file. A future `v<project-version>` tag will publish those same checked files on [Releases](https://github.com/Donovoi/bstrings/releases); no new tag or release is created by this change.

Extract the whole ZIP and keep its directories together. The examiner normally invokes only the root executable:

```powershell
.\bstrings.exe bundle verify
.\bstrings.exe analyze -d D:\carved-files --full -o D:\results\case-01
```

`bundle verify` fails on a missing, extra, linked, resized, or changed file. `--full` runs every bstrings analysis stage: native extraction, Magika routing, FLOSS recovery for supplied PE files, offline language assessment, selected local translation, and pattern matching. A requested stage fails instead of being silently skipped.

Important scope boundary: bstrings does not parse a filesystem or carve embedded files from a raw disk or memory image. Scan an image directly for byte strings and patterns, or mount/carve it with an appropriate forensic tool first and give bstrings the recovered files. FLOSS sees a PE only when that complete PE is supplied as a file.

## Common goals

| Goal | Command |
| --- | --- |
| Run every bstrings stage over carved files | `bstrings.exe analyze -d D:\carved --full -o D:\results\full` |
| Run every stage over one executable | `bstrings.exe analyze -f D:\sample.exe --full -o D:\results\sample` |
| Detect likely non-English text, translate locally, then search | `bstrings.exe analyze -d D:\carved --translation auto --translation-policy high-recall -o D:\results\translated` |
| Inventory language without translating | `bstrings.exe analyze -d D:\carved --translation detect-only -o D:\results\languages` |
| Scan a raw disk or memory image for all built-in patterns | `bstrings.exe -f D:\evidence\image.raw --lr all --ro --off -s -o D:\results\image-hits.csv` |
| Search wallet and ledger identifiers | `bstrings.exe -f D:\evidence\image.raw --lr wallets --ro --off -s -o D:\results\wallets.csv` |

Run `.\bstrings.exe analyze --help` for the integrated workflow or `.\bstrings.exe --help` for direct extraction/search. Directory analysis is recursive; keep the output directory outside the input tree.

## One archive, no dependency installation

The `windows-x64-cpu-q4` archive carries the self-contained .NET application, native Rust scanner, isolated CPython runtime, Magika plus app-local DirectML, standalone FLOSS, a source-built CPU llama.cpp runtime, Hy-MT2 Q4_K_M weights, and application-local Visual C++ runtime DLLs. It also carries exact dependency inventories, notices, required corresponding source, provenance, and a strict file manifest.

Nothing downloads or installs during examination. No separate .NET, Python, PowerShell module, package manager, model, Magika, FLOSS, ONNX Runtime, DirectML package, or Visual C++ runtime installation is required. The private adapter blocks non-loopback networking, model/package-manager offline modes are forced, and llama.cpp binds only to loopback with its `--offline` guard.

The conservative supported baseline is Windows 11 x64 24H2 or newer. The CPU path needs no GPU. CUDA/hybrid acceleration remains optional and requires a compatible host GPU driver and a separately validated GPU runtime profile; a hardware driver cannot be truthfully bundled with the application. Reduced-PATH, extracted-archive, and hosted-runner checks exist, but pristine disconnected-VM acceptance is still pending and is not claimed here.

The default Q4 model keeps the complete archive below the 2 GiB GitHub asset limit. In the bounded benchmark it preserved the same 22/22 protected identifiers as Q8, ran about 44% faster, and scored about 1.2–1.3 chrF++ points lower. That is a packaging decision, not a universal quality claim.

## Results and evidence safety

An integrated run writes immutable input identity, original strings, language assessments, recovered/translated children, regex matches, `run.json`, and `summary.json`. A result is complete only when both status records say `complete` and no `.incomplete` marker remains. Translated matches are investigative leads; confirm consequential findings against the untranslated parent and source evidence.

## Detailed guides

- [Air-gapped deployment and verification](docs/air-gapped-deployment.md)
- [Offline release maintenance](docs/offline-release-maintenance.md)
- [Executable recovery, language triage, and translation](docs/enrichment-pipeline.md)
- [Output, completeness, and provenance](docs/output-and-provenance.md)
- [Magika CLI redistribution and dependency closure](docs/magika-cli-redistribution.md)
- [FLOSS standalone redistribution and dependency closure](docs/floss-standalone-redistribution.md)
- [Pattern validity and false-positive controls](docs/pattern-validity-review-2026-08.md)
- [Cryptocurrency address coverage](docs/crypto-address-coverage-2026-08.md)

Developer builds require the pinned .NET/Rust toolchains and release-host tooling; examiners do not. See [offline release maintenance](docs/offline-release-maintenance.md) for build, test, packaging, and licensing steps. The project remains under its upstream terms in [LICENSE.md](LICENSE.md), with component attribution in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
