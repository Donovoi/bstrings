# CPU and CUDA backend crossover review

## Outcome

A fixed 1 GiB CUDA cutoff is not appropriate for the reviewed system. The CPU
was faster than GPU and hybrid extraction at every tested sparse tier from
256 MiB through 32 GiB, and it also won the dense 256 MiB `--lr all` profile.
The existing automatic policy remains the safer design: a strong AVX2 CPU does
not pay CUDA startup below 128 GiB, then an exact-output calibration selects an
accelerator only when its projected total time is at least five percent lower.

The review made three bounded improvements:

- `--trace` now reports the selected backend for each input, the final CPU/CUDA
  chunk split, and the command-line conditions which disable bounded regex
  streaming;
- GPU lane count has one source of truth shared by scanner construction, GPU
  chunk sizing, and pipeline worker creation;
- `Invoke-BackendCrossoverBenchmark.ps1` provides a repeatable CPU/GPU/hybrid
  crossover and telemetry gate with exact marker and canonical-output checks.

## Reviewed hardware and workload

- Intel Core Ultra 9 185H, 16 physical cores and 22 logical processors
- NVIDIA GeForce RTX 4060 Laptop GPU, 8,188 MiB VRAM
- 64 GiB system RAM, Windows, .NET 10 Release build
- deterministic binary fixtures with interior, chunk-boundary, and terminal
  URL/email records every 16 MiB
- ASCII-only and combined ASCII/UTF-16LE extraction
- real CLI execution with offsets, regex-only streaming, and the managed CPU
  engine

Every process had to return the complete expected marker multiset. Canonical
output hashes had to match across CPU, GPU, and hybrid modes. GPU utilization
was sampled every 50 or 100 ms with `nvidia-smi`.

## Backend results

Combined ASCII and UTF-16LE medians were:

| Input | CPU | Hybrid | GPU | Fastest |
| ---: | ---: | ---: | ---: | --- |
| 1 GiB | 0.447 s | 0.874 s | 1.222 s | CPU |
| 2 GiB | 0.701 s | 1.143 s | 1.490 s | CPU |
| 4 GiB | 1.204 s | 2.004 s | 2.031 s | CPU |
| 8 GiB | 2.151 s | 2.914 s | 3.069 s | CPU |
| 16 GiB | 5.460 s | 6.017 s | 6.412 s | CPU |
| 32 GiB | 9.140 s | 9.672 s | 10.298 s | CPU |

The dense mixed-encoding 256 MiB `--lr all --ro --off` fixture produced
784,896 rows in every run with one canonical hash. Median elapsed time was
5.59 seconds on CPU, 6.07 seconds in hybrid mode, and 16.15 seconds on GPU.
This is the closest synthetic profile to a high-output all-pattern forensic
scan and rejects a 1 GiB GPU rule even more strongly.

The existing 100 GiB both-encoding result on the same host also favored CPU:
43.32 seconds CPU, 44.91 seconds hybrid, and 45.57 seconds GPU. See
`pattern-engine-benchmark-2026-08.md` for that calibration-policy result.

The GPU was not idle or missing. Explicit GPU runs reached peaks as high as 95
percent and used about 1.37 GiB with two lanes. Four lanes raised peak use to
about 2.65 GiB but gave mixed results, including regressions up to 10.5
percent. The limiting costs are host/device transfer, synchronization, and CPU
materialization of returned hits rather than CUDA availability.

## Parallelization experiments

The existing producer reads eight chunks into each sequential read-ahead
batch, then feeds a bounded 44-slot channel serviced by 22 CPU workers. It does
not limit the whole run to eight workers: the producer reads later batches
while earlier chunks are processed.

- yielding every chunk immediately regressed 256 MiB through 8 GiB by 8 to 28
  percent and improved only the 16 GiB point by 2.6 percent;
- increasing read-ahead from 8 to 22 regressed 4 to 16 GiB by about 5 percent
  and regressed 1 GiB by 26 percent;
- increasing GPU lanes from 2 to 4 was inconsistent and increased memory use;
- batching result-writer calls was output-exact but neutral at 0.99x on the
  dense all-pattern profile.

Those candidates were reverted. Low aggregate CPU use is expected when the
pipeline is bounded by sequential storage reads, memory bandwidth, result
transfer, or serialized output. Forcing every logical CPU and the GPU to 100
percent would add contention without improving completion time.

## Operational command

When output is being saved to a file, `-s -q` enables the bounded regex
streaming path by suppressing duplicate console hits and progress rendering:

```powershell
.\bstrings.exe -f <evidence> --lr all -o <output> --trace --off -s -q --processor auto
```

This retains the full containing string. Add `--ro` only when the desired
output is the matching substring rather than the complete extracted string.
With the trace diagnostics in this change, the command reports the automatic
selection reason and the actual CPU/CUDA chunk split without requiring an
explicit GPU run.

## Reproduction

Generate exact scale fixtures with `ScaleCorpusGenerator`, then run:

```powershell
.\benchmarks\Invoke-BackendCrossoverBenchmark.ps1 `
  -DataRoot C:\bench\backend-crossover `
  -BstringsDll .\bstrings\bin\Release\net10.0\bstrings.dll `
  -RunRoot C:\bench\backend-crossover-run `
  -Tiers 1g,2g,4g,8g,16g,32g `
  -Modes cpu,gpu,hybrid `
  -Repetitions 3 `
  -IncludeUnicode `
  -VerifyHashes
```

Treat the crossover as hardware- and corpus-specific. Revise the policy only
when another exact-output run repeatedly shows a material accelerator win,
not merely higher utilization.
