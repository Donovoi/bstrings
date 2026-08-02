# Scale benchmark: 256 MiB to 100 GiB

## Conclusion

On the reviewed sparse binary workload, Donovoi/bstrings was the fastest of the
four tested commands at every size. At 100 GiB it completed in 42.19 seconds,
4.45 times faster than original bstrings and 4.28 times faster than ripgrep.
All tools returned the exact expected marker set, including every deliberate
boundary crossing and the terminal record one byte before EOF. Bulk_extractor
also reported the exact input byte count in its DFXML report.

Confidence is **high for this corpus and machine**, **moderate for the boundary
correctness repair**, and **low for general performance ranking across unknown
real evidence**. The 100 GiB figures are single runs, and synthetic data cannot
stand in for every disk image, memory capture, storage device, or pattern set.

## Results

Times are medians for the repeated tiers. IQR is the interquartile range. The
100 GiB tier has one complete run per tool, so it has no dispersion estimate.

| Size | Tool | Runs | Median | IQR | Throughput | Time vs fork |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 256 MiB | Donovoi/bstrings | 5 | 0.307 s | 0.022 s | 833.8 MiB/s | 1.00x |
| 256 MiB | Original bstrings | 5 | 0.722 s | 0.070 s | 354.5 MiB/s | 2.35x |
| 256 MiB | ripgrep | 5 | 0.347 s | 0.006 s | 737.3 MiB/s | 1.13x |
| 256 MiB | bulk_extractor | 5 | 1.305 s | 0.072 s | 196.1 MiB/s | 4.25x |
| 1 GiB | Donovoi/bstrings | 5 | 0.498 s | 0.028 s | 2,055.7 MiB/s | 1.00x |
| 1 GiB | Original bstrings | 5 | 2.105 s | 0.045 s | 486.5 MiB/s | 4.23x |
| 1 GiB | ripgrep | 5 | 1.251 s | 0.035 s | 818.3 MiB/s | 2.51x |
| 1 GiB | bulk_extractor | 5 | 3.644 s | 0.124 s | 281.0 MiB/s | 7.32x |
| 10 GiB | Donovoi/bstrings | 3 | 2.909 s | 0.066 s | 3,519.7 MiB/s | 1.00x |
| 10 GiB | Original bstrings | 3 | 19.866 s | 0.737 s | 515.5 MiB/s | 6.83x |
| 10 GiB | ripgrep | 3 | 12.523 s | 0.100 s | 817.7 MiB/s | 4.30x |
| 10 GiB | bulk_extractor | 3 | 33.096 s | 1.239 s | 309.4 MiB/s | 11.38x |
| 100 GiB | Donovoi/bstrings | 1 | 42.192 s | n/a | 2,427.0 MiB/s | 1.00x |
| 100 GiB | Original bstrings | 1 | 187.663 s | n/a | 545.7 MiB/s | 4.45x |
| 100 GiB | ripgrep | 1 | 180.769 s | n/a | 566.5 MiB/s | 4.28x |
| 100 GiB | bulk_extractor | 1 | 319.339 s | n/a | 320.7 MiB/s | 7.57x |

The machine-readable aggregate is
[`benchmarks/results/scale-2026-08.csv`](../benchmarks/results/scale-2026-08.csv).

## Why this corpus

NIST's [Computer Forensic Reference Data Sets](https://cfreds.nist.gov/) are
the right model for correctness testing: documented content lets a user compare
tool output with known placement. NIST's CFTT program similarly emphasizes
test methods, criteria, sets, and hardware rather than relying on a successful
exit code. Research on
[digital-forensic dataset construction](https://www.nist.gov/publications/dataset-construction-challenges-digital-forensics)
warns that a poorly documented corpus undermines the result built on it.

Real public corpora remain useful for realism. Digital Corpora's
[Govdocs1](https://digitalcorpora.org/corpora/file-corpora/files/) provides
nearly a million redistributable heterogeneous files, while its
[nps-2010-emails](https://digitalcorpora.org/corpora/disk-images/nps-2010-emails/)
image was designed for email finding and string search across encodings. They
were not selected as the primary scaling corpus because neither gives four
exact, comparable 256 MiB/1 GiB/10 GiB/100 GiB tiers with a simple complete
ground truth. Govdocs1 also warns that some files contain malware.

The selected corpus is a tool-evaluation dataset rather than a scenario image:

- deterministic pseudo-random binary background;
- printable ASCII background bytes replaced with delimiters, preventing
  uncontrolled strings and output-volume distortion;
- one interior URL/email record per 16 MiB segment;
- one record deliberately crossing every 16 MiB boundary;
- one unique terminal record ending a byte before EOF;
- an adjacent manifest containing SHA-256, exact size, seed, record schedule,
  samples, and expected counts.

The four generated SHA-256 values were verified again immediately before the
measurements:

| Size | SHA-256 | Expected URL + email matches |
| --- | --- | ---: |
| 256 MiB | `8e67e902484c4bf15f0c2a7b00b12c8538bb370efb85de2184f9b9567f7969f6` | 64 |
| 1 GiB | `6b83d7acc6030a323a4374bf83e9b55e6f49dcff6a32116b9af114d2690c24f5` | 256 |
| 10 GiB | `97f97e9af9779178637c0bfd494fb6c6bff1f36803eaebecdf6ec7630ecc4d0a` | 2,560 |
| 100 GiB | `bb927db71b510e77294367aa94ccf787dc68ca7061f4224ef200fe6690b83016` | 25,600 |

## Commands and validation

All tools searched the same ASCII URL-or-email expression. Donovoi/bstrings
and original bstrings extracted strings in 16 MiB CPU chunks before applying
the expression. Ripgrep 15.1.0 searched raw bytes with PCRE2.
Bulk_extractor's `email` scanner produced both `email.txt` and `url.txt`.

Every run had to satisfy all of these gates:

1. exit status zero;
2. exact manifest count;
3. every expected interior, boundary-crossing, and terminal ID present twice;
4. input length equal to the manifest;
5. bulk_extractor `report.xml` `total_bytes` equal to the input length;
6. corpus SHA-256 equal to the manifest before measurement.

The harness rotated tool order at repeated tiers and retained each output. The
exact commands are in [`benchmarks/Invoke-ScaleBenchmark.ps1`](../benchmarks/Invoke-ScaleBenchmark.ps1).

The upstream comparison used commit
`553c1efc29bfb051841fa78e48f5a7a657bf5ae7`. Its benchmark-only build gives an
explicit `-f` argument priority over redirected standard input; without that
one-line harness repair, upstream exits successfully without scanning the file
in this non-interactive runner. No search or extraction code was changed.

Bulk_extractor was the official Windows MinGW artifact from the successful
`mingw.yml` run for commit
`65adade7619d3a86d526b58d095e3f20429e7445` (`2.2.0-DEVELOP`). Its current
Windows build can stall during shutdown when both standard streams are
anonymous pipes, so the harness runs its own `-q` mode in a hidden child
process. The same artifact and scanner code are timed. Bulk_extractor also
computes feature contexts, histograms, DFXML, and a source hash, so its timing
is not a like-for-like regex-engine comparison.

## Boundary repair

The earlier dense 64 MiB fixture exposed two inherited artifacts: bstrings
returned 235,472 URL rows while ripgrep returned 235,470. One result was a
clipped `ps://...` suffix and another was a truncated `https://example.test`
prefix at 16 MiB boundaries.

The repair assigns ownership to chunk results:

- primary chunks suppress unfinished leading and trailing runs;
- boundary windows return only complete strings that genuinely cross the
  split;
- explicit maximum string length expands the recovery window when necessary;
- CPU and CUDA materialization use the same ownership rule.

After the repair, the fork returned exactly 235,470 complete URLs on the dense
fixture. The new scale corpus also places a record across every split, and all
CPU measurements returned the exact count. Dedicated tests cover complete,
non-crossing, leading-clipped, and trailing-clipped cases.

With unlimited maximum length, the default recovery context is 128 KiB on each
side of a split. Clipped runs beyond that window are suppressed rather than
published as false partial strings. If a workflow expects exceptionally long
strings, set a realistic `-x` maximum so the boundary window can be sized to
that bound.

## Environment

- Windows 11 Pro build 26200
- Intel Core Ultra 9 185H, 16 cores / 22 logical processors
- 63.5 GiB RAM
- 4 TB WD PC SN820 NVMe SSD
- NVIDIA GeForce RTX 4060 Laptop GPU, 8,188 MiB, driver 610.62
- .NET 9 target
- ripgrep 15.1.0 with PCRE2 10.45

The 256 MiB, 1 GiB, and 10 GiB tiers fit in RAM and are warm-cache results.
The 100 GiB file is larger than physical memory, but Windows still controls
cache and read-ahead; it is not a laboratory cold-cache measurement.

## Detractor review

The strongest evidence for the fork is not speed alone. It is the combination
of an exact marker set, a terminal marker, bulk_extractor's reported byte
count, a reproduced and fixed boundary defect, and a performance advantage
that persists past RAM size.

The strongest contradiction is the corpus design itself. Replacing printable
background bytes creates an ideal sparse-rejection workload for bstrings' SIMD
scanner. A real memory image may contain long printable regions, compressed
containers, file-system structure, fragmented data, or a very different match
density. Bulk_extractor is intentionally doing more forensic work, while
ripgrep is intentionally doing less string interpretation.

Plausible alternatives are that the ranking is driven partly by this byte
distribution, the NVMe/controller path, Windows caching, .NET/runtime versions,
or the chosen expression. A public-real corpus and a cold-cache removable or
raw-device run would test those alternatives.

The conclusion should be revised if an independent, exact-ground-truth corpus
shows the fork losing parity, producing boundary omissions within a declared
`-x` bound, or losing its throughput advantage across repeated hardware and
storage conditions.
