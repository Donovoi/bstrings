# Engineering notes for this fork

This is the short version of how the fork differs from upstream and why those
differences exist. For normal usage, start with the [README](README.md).

## The priority is complete, explainable output

Several changes came from a simple rule: a forensic tool must not make partial
output look complete.

- There is no silent 100,000-result ceiling.
- Chunk-boundary strings survive read-ahead and batching.
- Code-page and UTF-16LE range options affect the scanner as documented.
- Long printable runs produce one correctly bounded result.
- Buffers are returned when work is cancelled or fails.
- Chunk sizes are validated before arithmetic can overflow.
- An output file keeps an `.incomplete` sibling marker until the run succeeds.
- Regex timeouts and output failures produce a nonzero exit instead of quietly
  discarding matches.

Plain, unsorted extraction can stream directly to disk. Literal targets are
compiled once into a .NET 9 multi-string `SearchValues` matcher and applied to
each bounded batch, before global deduplication. Operations that need a complete
view of the remaining hits—such as sorting and regex workflows outside the
streaming path—may still need more memory.

## Extraction has three real backends

The CPU scanner selects kernels from the host's actual instruction set. ASCII
uses AVX2 or SSE2 when available; UTF-16LE uses AVX2 with BMI2 PEXT, AVX2
without PEXT, or SSE4.1, and both paths keep exact scalar tails and fallbacks.
The scanners consume SIMD validity masks by contiguous runs instead of
branching once per unit. The GPU scanner runs native ILGPU CUDA kernels. In
`hybrid` mode, CPU and CUDA workers take chunks from the same bounded queue.

CUDA is not accepted merely because a device exists. Each session runs a small
CPU/GPU parity check first. If an explicit `gpu` request cannot pass that
check, the command fails. On large inputs, `auto` races CPU, GPU, and hybrid
over evenly spaced samples, requires exact parity, includes CUDA startup cost,
and only selects an accelerator after a projected five-percent win. A failed
automatic accelerator measurement safely returns to CPU. Automatic chunk
sizing aims to leave enough work for all available workers.

## Regex handling is deliberately stricter

The built-in catalog now keeps each pattern, description, source, .NET options,
backtracking policy, optional cuDF prefilter, and optional output capture in one
definition. That removes the old global ignore-case and free-spacing behavior.
Custom regexes now behave like normal .NET regexes unless the caller requests
inline options such as `(?i)` or `(?x)`.

Regex objects are cached. Compatible built-ins use the non-backtracking engine.
Patterns that need lookarounds or backreferences start interpreted and promote
once to compiled code only when measured batch density and remaining work
justify the construction cost. CPU scheduling switches between hit-major and
pattern-major work according to the number of hits, patterns, and logical
processors.

One deliberately narrow exception speeds up the heavily used `url3986`
pattern. Extracted strings up to 2,048 characters use a source-generated .NET
matcher and allocation-light match ranges. The complete set of ranges is
buffered before any row is emitted. If the generated matcher reaches its 10 ms
deadline, the input is replayed from the beginning with the non-backtracking
engine, so the output is complete and contains no retry duplicates. Longer
strings go directly to the non-backtracking engine.

The catalog contains 33 patterns. The 2026 review added `cve`,
`pem_private_key`, `named_pipe`, `onion_v3`, `ethereum`, and `sha256`, and
tightened boundaries in many older patterns. Details and primary sources are in
[the regex review](docs/regex-pattern-research-2026-07.md).

## RAPIDS is optional and narrow by design

RAPIDS/cuDF is a regex prefilter, not a string extractor. It must already be
installed and working.

Only built-ins with a separately reviewed cuDF superset can use it. The final
decision still comes from the .NET pattern, which rechecks every GPU candidate.
Custom regexes and incompatible built-ins stay on CPU. A GPU failure can fall
back to CPU only before output begins; later failures stop the run to avoid
duplicated rows.

The startup probe has a 30-second deadline and a processing child has a
configurable deadline that defaults to ten minutes. A wedged child is killed
with its process tree. Live row-for-row cuDF parity still needs to be tested on
a machine with a supported RAPIDS installation.

## CI and performance claims

Pull requests and pushes to `master` restore, build, test, publish a
self-contained Windows x64 executable, package it, and upload the zip as a
temporary artifact. A release is created only for an explicit `v*` tag.

Benchmarks should always include the corpus, command, hardware, runtime, and
repeat count. The repository includes generators for extraction, exact
per-pattern competitor checks, and regex scheduling so results can be
reproduced instead of repeated as folklore.
The benchmark generator's `--dense-output` mode creates a synthetic mix of
ASCII and UTF-16LE records containing email and URL matches;
`--dense-record-length=<characters>` is useful for checking dispatch boundaries
without using case data.
