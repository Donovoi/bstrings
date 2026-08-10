# ADR-0006: Use one Q4 translation model with validated CUDA p2 and pre-evidence CPU fallback

- **Status:** Accepted
- **Date:** 2026-08-10
- **Scope:** Full-profile translation model, Windows llama.cpp CPU/CUDA runtime
  closure, hardware selection, inference scheduling, provenance, packaging, and
  release acceptance
- **Decision type:** model, hardware-selection, air-gap, performance, release,
  and failure-policy decision
- **Review method:** independent and rotated Robin review under the
  [high-level decision policy](decision-review-policy.md)
- **Perspectives:** primary-source evidence research, repository/runtime audit,
  performance and operations review, forensic detractor, rotated advocate and
  detractor critiques, and synthesizer measurements
- **Owner:** bstrings maintainers
- **Implementation state:** approved for bounded implementation; public
  promotion remains blocked until every applicable acceptance gate passes

## Context

The v1.9.16 Full profile uses the pinned 7B `Q8_0` Hy-MT2 model and a CPU-only
llama.cpp runtime. A large private memory-image examination produced a
multi-million-row high-recall translation population. An exact aggregate scan
found that fewer than one row in ten was a distinct source string, so the
run-local exact cache removes more than nine model calls in ten without
reducing candidate recall. Even after that reduction, the accepted CPU rate
projects a multi-week translation stage. That is correct but not a practical
Full workflow for the reviewed host.

The same 7B model family publishes a `Q4_K_M` GGUF at revision
`ab8472660ac61fac25f1af43fac2599d52a8a775`. The measured artifact is
4,624,648,896 bytes with SHA-256
`9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b`,
42 percent smaller than the Q8 artifact. With llama.cpp b10248 and a pinned
official CUDA 12.4 backend on a Windows RTX 4060 Laptop GPU (compute capability
8.9), full layer offload at parallelism two fit inside the measured 8 GiB VRAM
envelope.

The existing 60-row WMT24++ and 12-row synthetic forensic benchmark is useful
but small. Q4 strict scored 62.6525 WMT chrF++ and 93.6923 forensic chrF++, with
22/22 protected identifiers and 10/10 expected downstream patterns. The
source-built CPU server plus the official same-release CUDA overlay at p2
scored 62.6129 and 93.6923 with the same integrity results and 1.3085 strings
per second. The CPU-only Q4 fallback scored 62.4386 and 93.6923 with the same
integrity results at 0.1086 strings per second. These results establish bounded
quality compatibility, not universal model equivalence.

A small positional sample of distinct strings from the private examination ran
materially faster at p2 than the accepted CPU baseline. One output violated
protected-identifier retention and was safely replaced by the exact source
fallback. The sample covered substantially less than one percent of the
distinct population, was not random, and proves neither the projected duration
nor the population fallback rate. Exact private counts and records remain
outside this decision and the repository.

The first implementation audit also found that current `auto` behavior treats
any listed CUDA device as usable, requests adaptive offload, and has no CPU
retry if model loading or the first kernel fails. That can turn an unsupported,
busy, or low-VRAM GPU into a new Full failure. The measured p4 challenger is
faster but changes scheduled output more and is not needed to obtain the main
benefit.

## Considered options

1. **Keep Q8 and CPU only.** Rejected as the Full default on accepted CUDA
   hardware because the measured large workload remains a multi-week inference
   job. It remains the rollback baseline until this decision clears release
   acceptance.
2. **Reduce Full candidate recall or default to high-precision.** Rejected.
   The bundled 12-case forensic corpus selects 12/12 under high recall but only
   6/12 under the balanced and high-precision gates. Candidate volume is not
   evidence of recall.
3. **Cap translation or publish a partial report as final.** Rejected. A cap is
   order-biased and violates the one-child-per-candidate completion contract.
4. **Promote Q4 with adaptive or hybrid offload.** Rejected. The accepted
   measurements used full layer offload; partial placement introduces an
   unmeasured quality, memory, and performance boundary.
5. **Promote Q4 full offload at p4.** Deferred. The bounded benefit over p2 is
   modest and the repository already records greater schedule-dependent output
   variation at four slots.
6. **Ship separate user-visible CPU and GPU profiles or both Q8 and Q4.**
   Rejected. It increases transfer, trust, maintenance, and user choice while
   contradicting the single Full profile.
7. **Use one Q4 model, the accepted CPU server, and a separate authenticated
   CUDA overlay selected by a real pre-evidence probe.** Accepted. CUDA uses
   full offload and p2 only. Automatic selection may choose CPU only before the
   first evidence inference, cache insertion, or translation output. Explicit
   CUDA remains fail-closed.

## Decision

Full uses the single pinned Hy-MT2 7B `Q4_K_M` artifact. The quality bundle
retains the source-built, multi-variant b10248 CPU server and adds a separately
pinned official b10248 CUDA 12.4 backend and redistributable closure as an
authenticated content-addressed pack. The CUDA pack is not folded into the
base asset when that would threaten GitHub's asset-size boundary, and a warm
installer does not redownload already verified model or runtime objects.

`--translation-device auto` performs a transaction-free hardware preflight.
It may attempt CUDA only when the packaged backend loads and reports a supported
device. It starts the pinned Q4 model with every model layer offloaded to the
selected CUDA device at parallelism two, performs a synthetic translation
request, validates terminal output and observed layer placement, and records
the runtime buffers actually reported. If any CUDA probe step fails, the CUDA
process is closed and CPU is started and tested before evidence translation.
No cache or translation output exists at that point.

After the first evidence request, the selected provider, device, layer
placement, and parallelism are frozen. A later server, driver, GPU, timeout, or
resource failure leaves the translation transaction incomplete; it cannot
silently switch to CPU. Explicit `cuda` never falls back. Explicit `cpu`
continues to bypass CUDA. Hybrid and p4 remain expert/experimental boundaries
and are not the Full automatic plan.

The full model hash, llama.cpp version and executable hash, CUDA backend and
redistributable hashes, selected device identity, driver information available
from the runtime, requested and observed layer counts, reported CPU/GPU model
buffers, parallelism, decoding settings, and fallback totals enter execution
provenance. Every output-affecting field enters the run-local exact-cache
identity. The product must not claim zero host residency merely because every
transformer layer is offloaded: any input, mapped, or host buffer reported by
the accepted command remains explicit.

A non-terminal model response such as an output-token limit is a rejected
derived row, not permission to publish truncated text or abort unrelated rows.
It produces the exact source as an explicit preservation fallback, participates
in the existing consecutive/rate circuit breaker, and cannot hide a systemic
runtime failure.

## Non-negotiable invariants

1. Full/high-recall candidate cardinality, order, canonical parent text,
   provenance, and downstream coverage do not change to obtain acceleration.
2. Full contains one model identity. CPU and CUDA execute the exact same Q4
   bytes, prompt, target, decoding policy, and integrity validation.
3. Automatic CUDA selection completes model load, one synthetic inference, and
   observed full-layer-placement validation before any evidence inference,
   cache insertion, or translation output.
4. Automatic CPU fallback is allowed only before evidence work. Explicit CUDA
   fails closed, and no selected provider changes mid-run.
5. A CUDA success means every model layer is observed on the selected CUDA
   device. Adaptive, hybrid, partial offload, system-memory spill hidden by the
   plan, and unreported runtime buffers are not accepted behavior.
6. p2 is the automatic CUDA schedule. p4 cannot inherit this acceptance without
   a separate repeated quality and performance gate.
7. Every pinned model, server, backend, CUDA redistributable, licence, notice,
   and configuration byte is authenticated by the offline manifest and bundle
   verifier. No developer CUDA installation, network, telemetry, or mutable
   download is required at examination time.
8. A rejected, truncated, malformed, or identifier-breaking model row never
   publishes rejected model text. The exact-source fallback is explicit and
   counted, and systemic degradation still trips the circuit breaker.
9. Progress, rate, cache statistics, ETA, selected hardware, layer placement,
   and model/runtime identity agree across stderr, provenance, configuration,
   run metadata, help, and documentation.
10. A public release is not complete until the exact large command exits zero,
    reports complete, contains no `.incomplete`, and passes terminal
    cardinality, lineage, provenance, input, and report validation.

## Acceptance gates

The pinned Q4 artifact must pass the same strict CPU and CUDA forensic/WMT
benchmark. CPU and every accepted p2 run require WMT chrF++ at least 62.17,
forensic chrF++ at least 92.33, 22/22 protected identifiers, 10/10 downstream
patterns, no truncation published, and no missing or extra child. At least three
order-rotated p2 runs must pass. A larger deterministic stratified examination
sample must put the 95 percent upper bound for preservation fallbacks below one
percent; every observed failure must become exact-source fallback without
losing a good sibling.

A packaged-path CUDA soak of at least 10,000 stratified distinct synthetic or
sanitized private records must sustain at least 4.0 strings per second on the
reviewed sm89 host, remain inside a six-GiB peak-VRAM gate unless a prospectively
amended measurement justifies otherwise, show full observed layer offload, and
produce no OOM, device loss, server restart, or provider switch. ETA after
warm-up must remain within 20 percent of the eventual duration. Private rows
remain outside fixtures, logs, commits, and release artifacts.

Clean Windows acceptance must cover: a supported sm89 device and driver; no
NVIDIA device/driver; a listed but incompatible or old-driver device; and
insufficient free VRAM. Compatible auto selects CUDA p2. All unsupported auto
cases select and self-test CPU before evidence inference. Explicit CUDA fails
closed. CPU translation must work with no CUDA toolkit installed. Removing or
mutating any CUDA/backend/redist/licence byte must fail bundle verification.

Focused Python tests must inject device-list, load, health, synthetic-request,
offload-log, token-limit, timeout, cancellation, and mid-run failure outcomes.
They must prove pre-evidence fallback, explicit-CUDA failure, provider freeze,
exact source fallback, sibling continuation, and circuit-breaker boundaries.
Pack, installer, and workflow tests must prove cold acquisition, warm zero-body
reuse, corrupt-object recovery, exact asset-size limits, CPU-only assembly, GPU
overlay assembly, PE/import closure, and current-tag evidence identity.

The complete Python, .NET, Rust, PowerShell 5.1/7, decision, bundle, installer,
release, and public clean-install suites remain mandatory. Before tagging, the
exact requested Full command must finish with one ordered translation child per
candidate, model calls equal to the exact distinct cache-key count, cache hits
reconciling every repeated row, exit code zero, complete `run.json` and
`summary.json`, no `.incomplete`, monotonic percentage/ETA, and valid final
reports. The private exact totals remain outside the repository. The
predeclared reviewed-host target is 48 hours; missing it blocks an availability
claim and requires a recorded re-evaluation before promotion.

## Strongest detractor and resolution

The strongest model objection is that Q4 may preserve aggregate translation
scores while reducing instruction following. The small public corpus cannot
establish broad non-inferiority, and one real sampled row already required an
identifier fallback. Tencent's own Q4 measurements report a larger
instruction-following drop than general translation-score drop.

The decision does not treat quantization as lossless. It keeps canonical parent
evidence, validates every derived row, refuses rejected model text, exposes
fallbacks, retains the systemic circuit breaker, requires a larger population
gate, and makes the exact end-to-end run a release blocker. The bounded CPU and
CUDA tests show that the measured artifact clears the existing forensic gate;
the fallback design contains the remaining model error rather than assuming it
away.

The strongest runtime objection is that a visible CUDA device is not proof of
compatible kernels, sufficient VRAM, a complete redistributable closure, or
successful model placement. That objection defeats the old `--list-devices`
heuristic. It does not defeat a separately authenticated overlay with a real
pre-output load/inference/offload probe, CPU selection before evidence, and
fail-closed behavior afterward.

The strongest operational objection is that a projected 50-hour translation
stage remains long and a non-resumable run can lose substantial work. This
decision is a measured acceleration, not a claim that large Full examinations
are short or resumable. Durable authenticated checkpoints and separately named
source-complete preview reports require their own decision. The exact run and
48-hour target remain falsifiers rather than being inferred from the sample.

## Falsifiers and revisit triggers

Reject or roll back Q4/CUDA automatic promotion if:

- CPU or CUDA misses a quality, identifier, pattern, cardinality, ordering, or
  provenance gate;
- the population fallback upper bound is at least one percent, a fallback
  contains rejected text, or one rejected row aborts good siblings;
- CUDA auto reaches evidence work before self-test and observed placement,
  silently uses adaptive/partial/hybrid offload, or changes provider mid-run;
- an unsupported, busy, low-VRAM, or old-driver auto case fails instead of
  selecting CPU before work;
- a packaged runtime depends on the developer CUDA toolkit, a mutable URL, an
  unverified DLL, missing licence, external network, or hidden telemetry;
- p2 misses its measured quality or throughput floor, exceeds its VRAM boundary,
  or the exact command misses the accepted duration and completion contract;
- installer warm reuse redownloads an already verified Q4 or CUDA object; or
- help, provenance, reports, configuration, acceptance evidence, bundle
  contents, or release assets disagree with actual execution.

Roll back hardware auto independently by selecting CPU while retaining Q4 only
if CPU remains accepted. Roll back Q4 independently to the Q8 artifact if model
quality/integrity fails. An immutable release is never rewritten; rollback is a
new patch release. Revisit p4 only after repeated rotated p2/p4 runs establish a
material lower-bound gain with unchanged quality and fallback behavior. Revisit
resumability or source-preview publication under a separate authenticated state
and report-generation decision.

## Consequences

The Full bundle becomes smaller despite adding a separately reusable CUDA
overlay, and the reviewed sm89 host receives a large measured translation
speedup without reducing high-recall eligibility or weakening evidence
validation. Users retain one Full profile and do not choose between models.
CPU-only systems remain correct but may be slower than the former Q8 runtime;
the documentation and preflight must state the resolved plan and ETA plainly.

The build, installer, bundle manifest, PE/import verifier, licences, hardware
acceptance, runtime provenance, cache identity, and release asset set become
more complex. GPU support is intentionally bounded to the validated Windows
closure and must not be marketed as universal CUDA support. p4, hybrid routing,
durable resume, and preview reports remain separate work.

## Primary references

- [Hy-MT2 7B GGUF repository and pinned Q4 artifact](https://huggingface.co/tencent/Hy-MT2-7B-GGUF/blob/ab8472660ac61fac25f1af43fac2599d52a8a775/Hy-MT2-7B-Q4_K_M.gguf)
- [Hy-MT2 paper quantization and instruction-following results](https://arxiv.org/abs/2605.22064)
- [llama.cpp Windows and CUDA build guidance](https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md)
- [NVIDIA CUDA compatibility guidance](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/index.html)
- [NVIDIA CUDA 12.4 release notes and driver requirements](https://docs.nvidia.com/cuda/archive/12.4.1/cuda-toolkit-release-notes/index.html)
- [Translation integrity and run-local dedup decision](adr-0005-translation-integrity-and-run-dedup.md)
- [Offline component lock](../../tools/airgap/offline-components.lock.json)
- [Translation benchmark runner](../../tools/enrichment/benchmark_translation.py)
- [Offline release maintenance](../offline-release-maintenance.md)
- [High-level decision review policy](decision-review-policy.md)
