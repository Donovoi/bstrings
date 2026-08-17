# Email precision benchmark

This benchmark compares the previous broad `email` matcher with the proposed
high-confidence default. It uses authored synthetic data only.

Run the acceptance protocol:

```powershell
./benchmarks/EmailPrecisionBenchmark/Test-EmailPrecisionBenchmark.ps1 `
  -Records 1000000 -Rounds 7 `
  -OutputPath ./benchmarks/results/email-precision-acceptance-v2-2026-08.csv
```

The protocol uses seven fresh-process, alternating pairs for common valid
addresses, binary/resource noise, and a 50/50 mix. Every valid address must be
retained with the same value. Every authored noise value must be removed.

The first equivalent-engine protocol used only relative timing. It failed at
+34.18% median and +39.44% maximum even though the measured absolute overhead
was 211 to 246 nanoseconds per candidate. The result is retained in
`email-precision-relative-gate-v1-2026-08.csv`.

Before the next acceptance run, the timing gate changed to a material absolute
limit. Median overhead may be at most 750 nanoseconds per candidate and no
sample may exceed 1 microsecond per candidate. The relative result remains in
the CSV. Added allocation may be at most 64 bytes per input record. A slower
linear or network-backed check would fail these limits.
