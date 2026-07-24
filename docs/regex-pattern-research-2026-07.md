# Regex pattern and hardware research — 2026-07-24

## Conclusion

The built-in catalog is now metadata-driven. Each entry has one authoritative
.NET pattern, its own .NET options and backtracking policy, an authoritative
source, and—only where defensible—an ASCII-only cuDF superset prefilter.
GPU negatives are never accepted as authoritative for custom or ineligible
patterns. GPU candidates are rechecked with the .NET pattern.

The work corrected false negatives, truncation, option leakage, and runaway
backtracking risks in the inherited catalog. It also added six deliberately
small, source-backed categories: CVE IDs, RFC 7468 PKCS#8 private-key
boundaries, Windows named pipes, Tor v3 onion hostnames, Ethereum address
candidates, and SHA-256-shaped digest candidates.

## Acceptance criteria

An accepted built-in must:

1. have a stable first-party specification or maintained upstream definition;
2. compile with an explicit timeout and never match the empty string;
3. have positive, negative, boundary, and near-valid witnesses;
4. use `RegexOptions.NonBacktracking` unless a documented construct prevents it;
5. state when syntax alone cannot validate a checksum, identity, or secret;
6. keep case sensitivity local to the pattern;
7. stay CPU-only unless a manually reviewed cuDF pattern is a superset of the
   authoritative .NET matches.

## Evidence ledger

| Claim | Supporting primary evidence | Counter-evidence or limit | Decision |
| --- | --- | --- | --- |
| Non-backtracking is suitable for most catalog patterns | [.NET regex options](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options) guarantees linear input-time and lists unsupported constructs | Lookarounds and matching backreferences still require the backtracking engine | Select per pattern; keep a two-second timeout |
| Regex objects should be reused and compiled mode has startup cost | [.NET regex best practices](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-regex) recommends reuse and benchmarking interpreted/compiled/source-generated forms | The data-driven catalog is not a natural source-generator input | Cache by pattern and options; use non-backtracking where possible and compiled fallback otherwise |
| Global free-spacing changes user patterns | [.NET option documentation](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options#ignore-white-space) states that spaces are ignored and `#` starts comments | The old catalog used a verbose URI pattern | Restore normal custom-regex semantics; apply free-spacing only to `url3986` |
| cuDF regex is a different dialect | [libcudf regex features](https://docs.rapids.ai/api/cudf/stable/libcudf_docs/md_regex/) documents its supported groups, quantifiers, boundaries, and restricted case folding | A .NET recheck can remove GPU false positives, but cannot recover rows omitted by a non-superset prefilter | Use separate reviewed supersets; custom and unsupported patterns stay CPU-only |
| cuDF Python flags cannot be treated like Python `re` | [`Series.str.contains`](https://docs.rapids.ai/api/cudf/stable/user_guide/api_docs/api/cudf.core.accessors.string.stringmethods.contains/) documents only DOTALL and MULTILINE support in the current Python API | Older releases and libcudf expose a different low-level flag surface | Use explicit ASCII case classes and `flags=0`; probe the installed runtime |
| CVE IDs are bounded in current records | [CVE Record Format schema](https://github.com/CVEProject/cve-schema/blob/main/schema/docs/CVE_Record_Format_bundled.json) defines `CVE-`, four year digits, and 4–19 sequence digits | The public process prose historically described an unbounded sequence | Follow the production schema and label syntax-only matches as candidates |
| PKCS#8 boundaries are exact and case-sensitive | [RFC 7468](https://www.rfc-editor.org/rfc/rfc7468) requires five hyphens, one space, uppercase labels, and defines `PRIVATE KEY` and `ENCRYPTED PRIVATE KEY` | Legacy RSA/EC/OpenSSH key labels are different formats | Match only the two RFC 7468 PKCS#8 labels |
| Tor v3 hostnames have a stable encoded shape | [Tor onion-address specification](https://spec.torproject.org/rend-spec/encoding-onion-addresses.html) defines base32 of key, checksum, and version plus `.onion` | Regex cannot verify the embedded checksum/version | Match 56 base32 characters plus `.onion`; describe as a candidate |
| Ethereum addresses have a stable text shape | [Ethereum account documentation](https://ethereum.org/developers/docs/accounts/) defines `0x` plus 40 hexadecimal characters | Regex does not validate EIP-55 checksum casing or account existence | Accept as a 20-byte hexadecimal address candidate |
| SHA-256 output is 256 bits | [NIST FIPS 180-4](https://csrc.nist.gov/pubs/fips/180-4/upd1/final) defines SHA-256 | Any unrelated 64-hex identifier has the same shape | Name and describe it as SHA-256-shaped, not validated |
| Windows named pipes have a stable path prefix | [Microsoft pipe names](https://learn.microsoft.com/windows/win32/ipc/pipe-names) defines `\\ServerName\pipe\PipeName` | Named pipes are normal IPC and not malicious by themselves | Add a classification pattern, not a threat verdict |

## Corrected inherited behavior

- Base58 wallet alphabets are case-sensitive and reject `0`, `O`, `I`, and `l`.
- Monero standard/subaddresses and integrated-address candidates are
  longest-first and bounded so output is not truncated.
- Email candidates accept long TLDs and use atom-aware boundaries; those
  lookarounds require the compiled backtracking engine under the shared timeout.
- IPv4 rejects suffixes carved from longer dotted strings.
- IPv6 covers compressed and IPv4-embedded forms.
- SID covers authority-only well-known forms and hexadecimal identifier
  authorities without truncating overlong subauthority lists.
- MAC addresses and BitLocker recovery keys reject windows carved from longer
  structured tokens; SSNs require consistent separators.
- Environment-variable candidates accept common expansions, URLs, spaces,
  parentheses, and empty values while excluding NUL and line breaks.
- URLs may be embedded, userinfo may contain `:`, and empty path segments are
  retained. They remain URI candidates; no regex claims full RFC validation.
- `urlUser` emits only the username-shaped portion before an optional
  `:password`, so credentials are not mislabeled as identity output.
- Named pipes support complete standalone strings, quoted names containing
  spaces, and practical unquoted whitespace-delimited command arguments.
  This is an explicit forensic-token policy because Windows itself permits
  spaces and regex cannot infer arbitrary command-line argument boundaries.
- Credit-card matching is a high-recall 13–19 digit candidate search. Luhn and
  issuer validation remain separate.
- Base64 no longer matches the empty string or four-character noise.
- Custom regexes use normal .NET case-sensitive, whitespace-significant
  behavior; case-distinct expressions are retained, and callers opt into
  `(?i)` or `(?x)`.
- The first regex timeout stops further scheduling, fails the CLI, and leaves
  an `.incomplete` marker instead of silently discarding evidence.

## New-pattern detractor decisions

Rejected:

- **JWT** — [RFC 7519 validation](https://www.rfc-editor.org/rfc/rfc7519#section-7.2)
  permits JWS, JWE, and nested forms. Regex cannot validate a JWT without
  Base64URL decoding plus JOSE and JSON validation.
- **AWS/GitHub tokens** — provider formats evolve, and first-party
  documentation does not promise the fixed lengths commonly copied into regex
  lists.
- **MD5/SHA-1 and generic secrets** — the shapes are too common or have no
  stable grammar for a default forensic catalog.
- **PowerShell encoded commands** — useful, but the regex must be followed by
  base64 decoding and UTF-16LE validation. It belongs in a semantic detector,
  not this regex-only catalog.

## Hardware result

On the review host (`.NET 9.0.18`, 22 logical processors), seven-repeat
microbenchmarks over deterministic corpora showed:

| Workload | Adaptive median | Forced hit-major | Forced pattern-major |
| --- | ---: | ---: | ---: |
| 10,000 hits, one pattern | 4.888 ms | 6.524 ms | 79.286 ms |
| 10,000 hits, five patterns | 7.422 ms | 7.348 ms | 80.194 ms |
| 50,000 hits, five patterns | 19.398 ms | 20.498 ms | 52.163 ms |
| 250,000 hits, three patterns | 60.745 ms | 84.948 ms | 252.855 ms |
| 1,000,000 hits, one pattern | 294.840 ms | 283.315 ms | 2,215.047 ms |
| 1,000,000 hits, five patterns | 559.254 ms | 543.847 ms | 1,823.770 ms |

The production crossover now uses hit-major at 10,000 or more hits when the
pattern count is below the logical-processor count; otherwise it uses
pattern-major. Every path is capped at `Environment.ProcessorCount`. Adaptive
and forced-hit timings can differ due to thread-pool and GC noise even though
they select the same topology. Results are machine- and corpus-specific, so
the benchmark rotates measurement order and is committed for reruns elsewhere.

## Validation and unknowns

Local validation:

```powershell
dotnet test bstrings.sln -c Release --no-restore
dotnet build dev-tools\regex-benchmark\regex-benchmark.csproj -c Release
dotnet run --project dev-tools\regex-benchmark -c Release -- 2000000 7 1
```

The current review machine has no working cuDF installation. The static policy
gate, bounded startup probe, pre-emission whole-run fallback, and .NET superset
witnesses are tested, but exact live GPU/CPU differential parity remains a
hardware-gated test. A release claiming live RAPIDS parity must compare exact
`(row, pattern)` sets on an installed supported cuDF version; count equality is
insufficient.
The built-in benchmark now deduplicates one shared row set and routes the
accelerated measurement through the same partitioned, .NET-verified bridge; it
does not claim a GPU comparison when none of the requested patterns is eligible.

## Research provenance

The hardware/dialect research round ran through
`Donovoi/robin@001a84f43f79c073c25660f8364e4416ce03d358` using
`openai/gpt-5.6-sol` at `xhigh`. Robin recommended per-pattern .NET/cudf
dialects, non-backtracking where compatible, bounded CPU parallelism, and a
GPU-superset gate. Independent detractors then challenged correctness,
hardware parity, and candidate value. The first broad candidate round timed
out and was rejected rather than counted as evidence; the narrowed rerun is
recorded with the final validation output.
