# Pattern, regex-engine, and backend benchmark

## Conclusion

The finished fork returned the exact expected value and byte offset for all 33
built-in patterns on sparse ASCII, dense ASCII, adversarial ASCII, and UTF-16LE
corpora. At 256 MiB, original bstrings remained exact on 15 patterns and the
fork was 2.02–2.39 times faster on every exact overlap. Ripgrep was exact on 25
stream-comparable patterns: the fork won 10 and ripgrep won 15. The tested
bulk_extractor find/RE2 route passed none of the complete pattern corpora, so it
has no per-pattern speed rank.

This does **not** establish that bstrings is universally faster than ripgrep or
bulk_extractor. They search different semantic units and solve different jobs.
It establishes exactness for the declared bstrings workload, a large advantage
over original bstrings where both are correct, and a pattern-dependent result
against ripgrep.

Confidence is **high for exactness on the generated corpora**, **high for the
rejected optimizations**, **moderate for the adaptive decisions on the reviewed
machine**, and **low for universal ranking across unknown evidence and
hardware**.

## Reviewed environment

- Windows 11 Pro build 26200
- Intel Core Ultra 9 185H, 16 cores / 22 logical processors
- 63.5 GiB RAM
- NVIDIA GeForce RTX 4060 Laptop GPU, 8,188 MiB, driver 610.62
- .NET 9 target
- ripgrep 15.1.0, PCRE2 10.45 with JIT and runtime AVX2
- bulk_extractor `2.2.0-DEVELOP`, commit
  `65adade7619d3a86d526b58d095e3f20429e7445`

The files were synthetic and the repeated 256 MiB runs were warm-cache tests.

## Exact pattern matrix

Every 256 MiB pattern file had 16 MiB segments and 32 expected records: one
interior positive per segment, a positive crossing every segment boundary, and
a terminal positive next to EOF. Each segment also contained an authoritative
negative. A run passed only when its complete value/offset multiset matched the
manifest.

| Tool | Exact patterns | Median of exact pattern medians | Fork result on exact overlap |
| --- | ---: | ---: | --- |
| Donovoi/bstrings | **33/33** | 0.29 s | baseline |
| Original bstrings | 15/33 | 0.65 s | fork won 15/15, 2.02–2.39x |
| ripgrep PCRE2 | 25/33 | 0.25 s | fork won 10; ripgrep won 15 |
| bulk_extractor find/RE2 | 0/33 | n/a | no speed claim |

The fork's largest wins over ripgrep were `email`, `sha256`, `mac`,
`onion_v3`, `b64`, `ipv6`, and `win_path` at 3.25–3.72x. Ripgrep's largest
wins were on simpler direct-stream patterns such as `bitcoin`, `unc`, `ssn`,
and several wallet shapes. Across all 25 exact overlaps, the median ripgrep
time divided by fork time was 0.90x.

Original bstrings' failed rows were retained. Failures included duplicate or
missed chunk-boundary records and extracted-string anchoring differences. The
bulk_extractor failures were primarily unsupported RE2 syntax; supported
patterns also had to survive every deliberate boundary crossing. Ripgrep's
failed rows were patterns where byte-stream matching was not equivalent to
applying the expression independently to each extracted string. The harness
does not weaken a pattern to manufacture comparability.

The final additional acceptance sets were:

| Corpus | Patterns exact | Expected records verified |
| --- | ---: | ---: |
| Dense ASCII | 33/33 | 4,290 |
| Adversarial ASCII | 33/33 | 132, with thousands of near-misses per segment |
| UTF-16LE | 33/33 | 132 |

CPU, GPU, and hybrid had already returned the same 132 ASCII and 132 UTF-16LE
records in their all-pattern backend parity passes.

## CPU instruction selection

UTF-16LE extraction was the largest accepted kernel change. The CPU path now
selects at runtime:

1. AVX2 unsigned subtraction and range clamp over 16 UTF-16 code units;
2. `MoveMask` to obtain the two-bit-per-code-unit validity mask;
3. BMI2 `PEXT` to compress it to one bit per code unit when BMI2 is available;
4. an SSE4.1 eight-code-unit path on older x86-64 CPUs;
5. a scalar, explicitly little-endian fallback.

Randomized scalar-reference tests passed with normal AVX2/BMI2, AVX2 disabled,
BMI2 disabled, and both AVX2 and SSE4.1 disabled. The existing ASCII AVX2/SSE2
min/max implementation was retained.

Against the preserved binary, UTF-16LE CPU medians improved by 1.12x at 256
MiB, 1.20x at 1 GiB, and 1.21x at 10 GiB. With both encodings enabled, the
measured gains were 1.16x, 1.19x, and 1.28x. AVX-512 was not added because the
reviewed host could not validate it; an untested runtime branch would not
support a performance claim. Microsoft documents the .NET hardware-intrinsic
surface and hardware-acceleration checks in the
[`System.Runtime.Intrinsics` API](https://learn.microsoft.com/dotnet/api/system.runtime.intrinsics).

## CPU, GPU, and hybrid selection

The old rule selected GPU from file size and minimum string length alone. The
new policy considers CPU instruction support and logical processors before it
even pays CUDA startup. On a strong AVX2 CPU with at least eight logical
processors, the reviewed CPU result keeps automatic runs on CPU below 128 GiB.
SSE4.1-class and scalar CPUs reach calibration sooner.

For calibration-eligible files, eight evenly spaced 16 MiB samples are read.
CPU, GPU, and hybrid each run three times in rotated order. Every GPU or hybrid
sample must equal the CPU list exactly. The selected mode must project at least
a five-percent total win after CUDA startup. A sample with more than 250,000
extracted runs stays on CPU rather than risking an oversized accelerator
result transfer. An accelerator exception during automatic calibration disables
that path and safely selects CPU; explicit `gpu` and `hybrid` requests still
fail clearly instead of silently changing the requested mode.

On the normal reviewed hardware, a 100 GiB both-encoding run completed in
43.32 s on CPU, 44.91 s in hybrid, and 45.57 s on GPU. File size alone would
have made the wrong choice. With AVX2 and SSE4.1 disabled to exercise the
weaker-CPU branch, the 10 GiB calibration measured 36.3 ms CPU, 20.3 ms GPU,
and 35.2 ms hybrid over 128 MiB, accounted for 381.1 ms CUDA startup, selected
GPU, and preserved exact output.

ILGPU already compiles the extraction kernels for the detected CUDA device at
runtime. Its dynamic-specialization facility similarly compiles an uncached
specialization on first use. NVIDIA's NVRTC also compiles CUDA C++ to PTX or a
device binary at runtime, but compilation and cache behavior are real costs.
The review therefore did not add a second regex-to-CUDA compiler merely to say
the product has JIT kernels; the existing runtime-compiled kernels plus measured
dispatch are the smaller, auditable design. See the
[ILGPU specialization documentation](https://ilgpu.net/docs/03-advanced/05-dynamically-specialized-kernels/)
and [NVIDIA NVRTC documentation](https://docs.nvidia.com/cuda/nvrtc/index.html).

## Adaptive regex compilation

.NET interpreted and compiled engines were compared for every backtracking
built-in at 1, 100, 1,000, 10,000, 20,000, 40,000, 60,000, 80,000, and 100,000
attempts. Construction was included, engine order was rotated, and one positive
was injected every 1,024 attempts. All counts were identical.

At 1,000 attempts, compilation lost on all 25 tested expressions. At 10,000 it
won materially for only `usPhone` and `ipv4` among regex-backed production
paths. At 100,000 it won on 14 and lost on 11; the largest compiled gains were
4.37x for `ipv4`, 4.24x for `ipv6`, and 2.30x for `email`.

The streaming path now keeps measured built-ins lazy. The first bounded batch
and remaining file chunk count project candidate volume. Sparse inputs
construct the interpreted engine once. Dense inputs whose projection crosses
the measured per-pattern threshold construct the compiled engine once. If
density rises later, promotion is thread-safe and happens at most once. Custom
regexes retain their compiled behavior. Microsoft's regex documentation notes
that compiled regex improves execution at the cost of startup, while the
source generator does not support `RegexOptions.NonBacktracking`; those
constraints are why one global engine was rejected. See
[regular expression options](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options)
and [the regex source generator](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-source-generators).

## Rejected optimizations

- Replacing the ASCII min/max range test with subtract-and-clamp looked faster
  at 256 MiB but fell to 0.91x at 10 GiB. It was reverted.
- A mandatory-literal/minimum-length prefilter stayed exact but had a 0.94x
  median and slowed 23 of 33 adversarial patterns. It was removed.
- Per-candidate regex promotion scored 0.98x at 128 MiB; batch-aware promotion
  scored 0.97x. Adding remaining-chunk density and lazy construction brought
  the dense comparison to a 1.00x median while retaining the small sparse win.

These detractors are important: aggregate counts or a single small tier would
have accepted two regressions.

## Reproduction and falsification

The generators and commands are documented in
[`benchmarks/README.md`](../benchmarks/README.md). Raw run directories are not
committed. The checked-in code contains only generic generators, harnesses,
tests, and synthetic aggregate results.

Revise the conclusions if another exact-ground-truth corpus finds a fork
value/offset mismatch, if a different CPU/GPU pairing repeatedly contradicts
the calibrated choice, or if cold-cache and public-real corpora reverse the
reported ranking. A fair future comparison should keep unsupported semantics
as unsupported rather than substituting a broader expression and calling the
outputs equivalent.
