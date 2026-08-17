# Match-reuse benchmark

This benchmark compares the enrichment matcher with the run-local exact-text
cache disabled and enabled. Every pair uses the same synthetic input and
requires an exact output SHA-256 match.

The cache is disabled in production. Two gate-eligible runs preserved exact
output and improved repeated workloads, but each exceeded a fixed worst-pair
overhead limit. The raw CSV files and decision are retained for future work.

Bounded smoke:

```powershell
./benchmarks/MatchReuseBenchmark/Test-MatchReuseBenchmark.ps1
```

The acceptance protocol, workloads, metrics, gates, and rollback rule are in
[ADR-0015](../../docs/architecture/adr-0015-high-confidence-base64-and-bounded-match-reuse.md).
