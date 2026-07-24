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

The normal scanner is a parallel CPU implementation. The old ILGPU experiment
was not connected to the active scanning pipeline and must not be described as
a working acceleration path.

RAPIDS remains optional and experimental. It invokes a separately installed
Python/cuDF environment for regex processing and falls back to the CPU path on
failure. Automatic third-party installation was intentionally disabled because
the earlier installer was not reliable or verifiable.

Performance claims should be accompanied by the corpus, command line, hardware,
run count, and raw measurements. Do not retain historical estimates as if they
were current benchmarks.
