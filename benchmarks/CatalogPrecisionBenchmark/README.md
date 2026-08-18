# Catalog precision benchmark

This harness compares the former 77-pattern default with the 74-pattern
lower-noise preset from ADR-0017.

It uses authored synthetic values only. The valid workload contains separated
MAC values, registry subpaths, and non-unspecified IPv6 values. The confusion
workload contains unseparated MAC values, a bare registry root, bare `::`, a
five-digit value, a checksum-free Solana-shaped value, and a Move-shaped value.
The mixed workload alternates both sets.

The confusion workload measures deterministic ambiguity removal from the
default preset. It is not a labelled real-world false-positive-rate or
precision measurement.

The acceptance run uses 100,000 records, seven alternating fresh-process pairs,
exact default cardinality and valid-value checksum checks, and a separate
former-versus-strict-plus-candidate value checksum in every workload. Median
wall time and peak working set may not regress by more than 5 percent in any
workload. The ambiguity workload measures default omission, not real-world
false-positive prevalence.

```powershell
.\Test-CatalogPrecisionBenchmark.ps1 `
  -Records 100000 `
  -Rounds 7 `
  -OutputPath ..\results\catalog-precision-acceptance-2026-08.csv
```
