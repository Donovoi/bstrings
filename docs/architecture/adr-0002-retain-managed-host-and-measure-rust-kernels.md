# ADR-0002: Retain the managed host and measure Rust kernels

- **Status:** Accepted; no host rewrite or new production Rust slice approved
- **Date:** 2026-08-08
- **Scope:** Windows host, native extraction, reporting, and release architecture
- **Decision type:** runtime, engine-boundary, performance, and forensic compatibility
- **Review method:** Robin round under the
  [high-level decision policy](decision-review-policy.md)
- **Perspectives:** independent Rust advocate and primary-source research,
  repository/runtime audit, forensic detractor, performance and operations
  review, rotated critiques, and synthesizer measurement
- **Implementation state:** the accepted hybrid boundary already exists; this
  record adds promotion and revisit gates but does not change product code

## Context

bstrings is already a hybrid application rather than a purely managed one.
The C#/.NET host owns the CLI, input identity, Windows evidence leases, raw-disk
access, CPU/CUDA/hybrid scheduling, chunk ownership, decoding, .NET regex
semantics, child-process supervision, canonical JSONL, report projections,
bundle verification, progress, cancellation, and atomic completion. The Rust
`bstrings_core` library owns AVX2/SSE2/scalar ASCII span discovery and Lingua
language detection behind an ABI-versioned C interface.

The question was whether replacing all or part of the C# host with Rust would
make large forensic examinations faster, safer, smaller, or easier to support.
The decision tests were fixed before synthesis:

- no missing, additional, moved, or misattributed evidence;
- no weakening of source identity, mutation detection, failure atomicity,
  deterministic projections, air-gap behavior, or Windows support;
- a material benefit in a demonstrated end-to-end or dominant-stage
  bottleneck, not only an isolated language microbenchmark;
- a smaller audited unsafe/interop surface rather than more structured data
  crossing FFI; and
- migration, dual-implementation, test, packaging, and maintenance costs
  proportionate to the measured benefit.

At this revision the tracked implementation contains 29,265 physical C#
runtime lines across 54 files and 13,048 C# test lines across 38 files, with
359 `[Fact]` or `[Theory]` declarations. The Rust component contains 651 lines
including four unit tests. These counts are not an effort estimate by
themselves, but they show the amount of observable behavior for which a rewrite
would need an independent oracle.

## Considered options

1. **Rewrite the complete host and orchestrator in Rust.** This could remove
   the current C ABI only after also replacing or preserving .NET regex,
   ILGPU, raw NTFS access, code-page decoding, CLI behavior, Windows path and
   sharing semantics, report schemas, and release behavior. Rejected.
2. **Move a complete CPU extraction worker to Rust.** The proposed worker
   would own ASCII and UTF-16LE discovery, decoding, boundary ownership,
   offsets, and native JSONL, while C# retained the full pipeline. This could
   widen the fast ownership boundary, but it would create two native-record
   producers and reimplement a forensic transaction boundary. Not approved
   from current evidence.
3. **Add more leaf kernels such as UTF-16LE discovery.** This preserves the
   existing narrow caller-owned-buffer boundary and managed fallback. It is a
   valid experiment only after representative profiling shows that the leaf is
   material. No current profile does so, so no new slice is approved now.
4. **Retain the managed control plane and current measured Rust islands.** Add
   Rust only for independently selectable compute kernels that pass exact
   differential, failure, hardware, packaging, and end-to-end gates. Accepted.
5. **Use managed deployment/runtime improvements where they address the same
   hypothesis.** A local CPU-only Native AOT probe demonstrated that startup
   and CPU extraction can improve without a language rewrite, but the current
   full build emitted AOT warnings for dynamic JSON paths and ILGPU methods.
   This remains a separate benchmark candidate, not an accepted release mode.

## Decision

C#/.NET remains the bstrings control plane. There will be no full Rust rewrite
and no Rust host/orchestrator, regex, raw-disk, CUDA, provenance, reporting,
bundle, or installer port from the present evidence.

The existing partial architecture is retained:

- integrated `analyze` uses the ABI-checked Rust ASCII scanner through
  `--cpu-engine auto` when the library passes startup differential validation,
  with managed fallback;
- the legacy direct scanner keeps its explicit `dotnet`, `rust`, and `auto`
  choices and its current default;
- Lingua language detection remains a bounded Rust service behind validated
  fixed-layout results; and
- C# continues to own all evidence identity, offsets, chunk ownership,
  decoding, publication, and specialist-engine orchestration.

Future Rust work is profile-led and leaf-first. A prototype may begin without
changing the default only when a reproducible profile identifies a bounded
operation as material and defines an exact managed oracle. Promotion requires
the complete acceptance gates below. An experiment that does not clear them is
removed rather than kept as an unmaintained second implementation.

Splitting the scanner and Lingua into separate native artifacts is allowed only
as a no-ship measurement spike. Lazy Lingua initialization means the current
289 MB local DLL size does not prove equivalent working-set cost, and embedding
both split DLLs could leave release size unchanged while doubling ABI and
packaging states.

## Non-negotiable invariants

1. A language change cannot alter the canonical evidence contract. Text,
   encoding, absolute location, source and parent identity, engine lineage,
   record identifiers, terminal status, and completion state remain exact.
2. The C# host retains ownership of trusted source handles, pre/post hashes,
   Windows sharing rules, mutation detection, path containment, cancellation,
   and atomic publication unless a superseding ADR proves full parity.
3. A Rust component exposes only bounded primitive buffers and fixed-layout
   results through a versioned C ABI. Complex paths, JSON objects, cancellation
   graphs, report rows, or ownership of managed allocations do not cross that
   boundary without a new Robin round.
4. Every native call validates pointers, lengths, capacities, integer bounds,
   returned ranges, status codes, and ABI identity. Rust panic or memory faults
   cannot be presented as a completed examination.
5. Managed fallback remains available until a separately reviewed decision
   retires it. Explicit strict Rust selection fails visibly when validation or
   execution fails.
6. Performance acceptance never waives exact output, provenance, failure,
   air-gap, license, packaging, or clean-machine release gates.
7. A microbenchmark is evidence about its kernel only. It cannot justify a
   host rewrite or default promotion without representative end-to-end proof.

## Acceptance gates

Any proposed Rust expansion must pre-register its corpus, hardware, metrics,
equivalence rules, and rollback threshold before implementation results are
seen. Promotion requires all applicable gates:

- **Profile gate:** EventPipe/ETW or equivalent measurements on representative
  evidence show that the proposed operation is a material contributor rather
  than an I/O, wait, downstream inference, or output proxy.
- **Differential gate:** the managed oracle and Rust candidate produce the same
  ordered primitive results and canonical end-to-end records across scalar and
  SIMD boundaries, odd and terminal input lengths, dense and sparse output,
  minimum/maximum truncation, custom ranges, chunk boundaries, and large file
  offsets.
- **Forensic gate:** mutation, cancellation, invalid native results, missing or
  wrong-architecture libraries, bad ABI, output failure, and injected native
  failure preserve the existing nonzero/incomplete/atomic behavior.
- **Hardware gate:** at least two materially different Windows x64 hosts are
  measured, including AVX2 and non-AVX2 dispatch before a default change.
- **Performance gate:** seven or more rotated pairs show no representative
  workload regressing by more than 5 percent and either at least 15 percent
  end-to-end improvement in the affected real stage or at least 30 percent
  lower combined parent/child peak memory with no material time regression.
  A lower threshold needs a separately quantified multi-hour operational
  benefit and does not follow from a kernel-only speedup.
- **Boundary gate:** the candidate reduces or contains audited unsafe work and
  does not add complex structured FFI, allocator ownership transfer, or an
  unexplained process-abort path.
- **Release gate:** the Windows single-file/core artifact, offline Full bundle,
  license inventory, PowerShell 5.1 and 7 installers, network-blocked smokes,
  clean-PATH execution, and immutable release verification remain exact.
- **Maintenance gate:** a representative provenance, failure-policy, and
  dependency change can be made without maintaining two divergent record
  producers or retaining the old host indefinitely as an unshippable oracle.

A full host rewrite additionally requires zero unexplained differences across
the complete CLI, all 66 built-in patterns and representative custom .NET
regexes, raw-disk paths, CPU/GPU/hybrid modes, all 22 possible analysis stages,
bundle acquisition/verification, every report, and the full adversarial
failure matrix. It must show at least 15 percent complete-workflow improvement
on two Windows hosts or resolve a concrete unsupported/security constraint that
cannot be fixed within the managed host.

## Strongest detractor and resolution

The strongest pro-Rust case is not the host; it is a wider CPU extraction data
plane. The focused scanner already proves that Rust can be much faster, and its
gain increased sharply when the ABI stopped copying every hit into managed
records. A Rust worker owning scanning, decoding, boundary recovery, and JSONL
could remove more managed materialization and might reduce startup or memory.

That case does not currently survive the end-to-end evidence. On the reviewed
host, current ABI 3 Rust was 1.22x to 2.96x faster in all 12 focused scanner
cells, yet five rotated real-CLI pairs over the exact 256 MiB scale corpus
improved from a 0.2676-second managed median to a 0.2612-second Rust median:
1.0246x, or 2.40 percent. All ten runs returned the same 64 expected markers
and canonical output. Historical 1 GiB results were tied and 10 GiB improved
only 1.04x. Repository profiles also found scanner time small relative to
scheduling, I/O completion, thread-pool waits, regex, decoding, and output.

Moving the boundary far enough to chase the lost kernel gain would reimplement
trusted source handling, code-page decoding, chunk ownership, record IDs,
JSONL limits and escaping, atomic output, and incomplete behavior. That is a
large forensic fork for an unproven complete-stage gain. The proposal is
therefore retained as a falsifiable future experiment, not implemented now.

## Falsifiers and revisit triggers

Reopen this decision if any of the following is observed:

- a representative profile attributes at least 20 percent of a material real
  stage to a bounded managed operation that a Rust prototype can replace;
- a reversible CPU extraction worker clears the differential, forensic,
  Windows, release, and performance gates above;
- managed runtime, .NET regex, ILGPU, raw-disk, or Windows support develops a
  concrete limitation that cannot be repaired locally;
- combined Full-bundle or process-tree measurements show a material deployment
  or memory benefit unavailable through managed trimming, source generation,
  Native AOT separation, scheduling, batching, or record-sink changes; or
- sustained maintenance measurements show that a Rust implementation is less
  costly while preserving the same release velocity and forensic contract.

Reject a proposed Rust slice if exact output or failure parity is unexplained,
if the measured gain disappears end to end, if a second host regresses, if
unsafe/ABI states grow faster than the code removed, or if the candidate must
keep a feature-lagged managed twin indefinitely.

Re-evaluate this record when the native ABI, Rust toolchain, .NET runtime,
supported Windows architectures, regex engine, raw-disk stack, or default
hardware policy changes materially.

## Consequences

The project keeps a smaller migration and forensic risk while preserving the
Rust speedups already proven in bounded kernels. Users do not receive a
language-change claim that the end-to-end measurements do not support. The C#
test and release corpus remains authoritative, and new Rust work must earn its
scope with profiles and differential results.

The cost is that the host retains the .NET runtime and the current C ABI.
Native AOT, split native artifacts, UTF-16LE Rust, and a CPU extraction worker
remain possible experiments rather than roadmap promises. This can leave some
local SIMD performance unrealized, but avoids turning a 2.4-percent measured
real-CLI gain into a multi-subsystem rewrite.

The complete Full kit measured 9,284,392,323 bytes, while its complete
single-file `bstrings.exe` was 367,511,043 bytes, or 3.96 percent. Even the
impossible upper bound of removing the executable entirely would not change the
model/runtime-dominated deployment class. Package size is therefore not a
standalone rewrite justification.

No product code, default, help text, report schema, installer, bundle, or
release asset changes as a result of this decision. Documentation-only changes
do not require a version bump or new product release under the current
versioning policy.

## Primary references

- [Google: Rust in the Android platform](https://security.googleblog.com/2021/04/rust-in-android-platform.html), which distinguishes managed application code
  from lower-level systems code and says mature-code rewrites are not the
  primary memory-safety strategy.
- [Google: Memory Safe Languages in Android 13](https://security.googleblog.com/2022/12/memory-safe-languages-in-android-13.html), which reports Rust results for new
  native code while explicitly focusing on new code rather than wholesale
  conversion. Its C/C++ comparison is not evidence that Rust is safer than
  managed C#.
- [Microsoft Native AOT deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/), including startup/footprint benefits and dynamic-code,
  trimming, diagnostics, and platform limitations.
- [Microsoft source-generated P/Invoke](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation), an alternative for reducing managed/native invocation
  setup and improving AOT compatibility without a host rewrite.
- [Microsoft C# unsafe-code reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/unsafe-code), which distinguishes verifiably safe managed code from explicit
  unverifiable pointer operations.
- [Rust FFI and unwind behavior](https://doc.rust-lang.org/nomicon/ffi.html),
  including the process-abort consequence of `panic = "abort"`.
- [Rust linkage reference](https://doc.rust-lang.org/reference/linkage.html),
  including `cdylib`, mixed-code, and Windows C-runtime constraints.
- [Existing Rust scanner benchmark and limits](../rust-engine-prototype-2026-08.md)
- [CPU scheduling profile](../chunk-scheduling-optimization-2026-08.md)
- [Forensic output and provenance contract](../output-and-provenance.md)
- [Release and version policy](../../VERSIONING.md)
