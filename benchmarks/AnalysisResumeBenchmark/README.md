# Analysis resume benchmark

This benchmark compares the resume implementation with the previous public
v2.1.1 new-run baseline. It uses a 64 MiB deterministic sparse-memory fixture. The
fixture exercises byte scanning without making report generation dominate the
measurement.

This is a release-regression comparison. It does not isolate checkpoint code
from every other change after v2.0.0, so a failed delta cannot identify its own
cause.

The first script revision ran three alternating pairs for each now-falsified
gate:

- New-run checkpoint overhead must have a median of 8 percent or less.
- Resume after stage 10 must save at least 65 percent of full rerun time.

Both percentage-only gates are superseded by ADR-0012. The corrected runner now
uses at least five alternating pairs and these gates:

- For sparse and match-heavy fixtures, median added new-run time must be no more
  than the larger of 1.0 second or 5 percent of baseline time.
- A long resume fixture must contain at least 60 seconds of reusable stage work,
  and that work must be at least half of the fresh run.
- Median resume saving must be no less than the larger of 30 seconds or half of
  the measured reusable stage time.

Every pair compares the evidence artifacts by SHA-256. Cross-release comparisons
normalize only the recorded bstrings release version; every other field remains
exact. Timing includes process
startup, input verification, stage validation, checkpoint hashing, and final
completion checks.

The 1 MiB match-heavy fixture recorded 23.39 percent median overhead, about
1.28 seconds, and 5.23 percent median resume saving, about 0.33 seconds. The
64 MiB sparse fixture recorded 15.61 percent median overhead, about 0.45
seconds, and 15.94 percent median resume saving, about 0.52 seconds. Canonical
output parity passed in both runs. Both CSVs remain failed evidence. Neither is
acceptance evidence under the corrected protocol.

The first corrected run used a frozen 8 GiB long fixture. New-run overhead,
canonical parity, and exact preservation of reused bytes passed. The fixture
contained only 34.820 seconds of reusable work, below the unchanged 60-second
qualification. Its 19.354-second saving was therefore also below the unchanged
30-second floor. That result remains in
`analysis-resume-corrected-8g-qualification-failed-2026-08.csv`.

Before another timing run, the long fixture was frozen at 24 GiB. This change
uses the measured scan rate to qualify the workload. It does not change the
one-second, five-percent, 60-second, 50-percent, or 30-second margins.

The preregistered 24 GiB acceptance run passed. Median added new-run time was
0.306 seconds for sparse input and 0.484 seconds for match-heavy input. Both
were below the 1.0-second allowance. Median reusable work was 104.863 seconds,
or 80.48 percent of fresh time. Median resume saving was 58.947 seconds, above
the required 52.432 seconds. Canonical parity and exact preservation of all
reused checkpoint and artifact bytes passed. The accepted rows are in
`analysis-resume-acceptance-2026-08.csv`.

The v2.1.2 rerun used the immutable v2.1.1 release as its baseline. Sparse
median overhead was 0.024 seconds and match-heavy median overhead was 0.002
seconds. Median reusable work was 112.102 seconds. Median resume saving was
61.339 seconds, above the required 56.051 seconds. Canonical parity and exact
reused-checkpoint hashes passed. The accepted rows are in
`analysis-resume-2.1.2-acceptance-2026-08.csv`.

The CSV records the measured candidate commit. Its adjacent provenance file
binds the complete `bstrings` runtime tree and benchmark script to Git object
IDs. A later candidate can use these results only when both object IDs still
match. Run the two recorded `git rev-parse` commands from that candidate before
accepting the evidence.

Run the corrected protocol from the repository root:

```powershell
pwsh -NoProfile -File benchmarks/AnalysisResumeBenchmark/Test-AnalysisResumeBenchmark.ps1
```

The script builds the baseline from the immutable `v2.1.1` tag. It builds the
candidate from the current source tree. Temporary evidence and results stay
under a unique system temporary directory and are removed after the run.
