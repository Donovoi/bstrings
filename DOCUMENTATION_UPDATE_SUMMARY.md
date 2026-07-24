# Fork review summary

This file replaces the earlier implementation notes, which had become
inaccurate as the fork evolved.

The fork retains its bounded parallel processing, streaming output, regex
enhancements, optional RAPIDS integration, benchmark tooling, and release
automation. The July 2026 review reconciled those changes with current upstream
and corrected several issues:

- output files no longer receive unfiltered or duplicate rows before
  post-processing;
- in-memory collection no longer stops silently at 100,000 results;
- chunk-boundary scanning continues across read-ahead batches;
- `--cp` and `--ur` now affect scanning as documented;
- long printable runs produce one correctly truncated result;
- comma-separated regex parsing preserves commas inside regex syntax;
- regexes are cached, compiled consistently, and have a timeout;
- pooled buffers are returned on cancellation and errors;
- chunk-size validation prevents integer overflow;
- obsolete or unused telemetry, packaging, GPU, and Python dependencies were
  removed;
- CI now tests every pull request and creates releases only from explicit
  version tags.

The active scanner now has three independently selectable paths: the optimized
SIMD CPU implementation, native ILGPU CUDA kernels, and a hybrid bounded queue
served by both. CUDA initialization includes an actual CPU/GPU result-parity
self-test. `auto` uses benchmark-informed size and minimum-length crossover
rules, while explicit `gpu` requests fail instead of silently pretending to use
the GPU. Automatic chunk sizing now targets enough chunks to keep workers busy.

RAPIDS remains optional and experimental. It invokes a separately installed
Python/cuDF environment for regex post-processing, not extraction. Its startup
probe exercises the requested case-insensitive API, individual pattern errors
abort the entire GPU result rather than returning partial evidence, and the
bridge verifies returned candidates with the .NET regex engine. Automatic
third-party installation remains disabled.

Performance claims should be accompanied by the corpus, command line, hardware,
run count, and raw measurements. Do not retain historical estimates as if they
were current benchmarks.
