# Regex review: what changed and why

Date: 24 July 2026

## Short version

The old catalog mixed pattern text, descriptions, and global regex options in
different places. That made it easy for one change to affect every pattern and
hard to tell whether CPU and RAPIDS searches meant the same thing.

The catalog now has one definition per built-in. Each definition records:

- the .NET pattern used for the final decision;
- its description and primary source;
- case, whitespace, and backtracking options;
- an optional, separately reviewed cuDF superset; and
- an optional capture to emit for patterns such as `urlUser`; and
- an optional deterministic semantic validator for checks that regex cannot
  establish safely.

The review fixed several real false negatives and prefix-truncation bugs, added
six useful patterns, and deliberately rejected several tempting but unreliable
ones. It also changed timeout handling so incomplete regex output cannot be
reported as a successful run.

## What a built-in pattern has to prove

A pattern belongs in the default catalog only when:

1. its shape comes from a stable first-party specification or maintained
   upstream definition;
2. it compiles with an explicit timeout and cannot match an empty string;
3. it has positive, negative, boundary, and near-valid tests;
4. it uses .NET's non-backtracking engine when its syntax allows it;
5. stable offline checksums, lengths, ranges, versions, and canonical encodings
   are validated after the regex candidate stage where applicable;
6. case and whitespace rules are local to that pattern; and
7. it stays CPU-only unless its cuDF expression is demonstrably a superset of
   the .NET matches.

That last point matters. A broad GPU prefilter may return extra rows because
.NET can remove them afterwards. A narrow GPU pattern can permanently hide a
real match, so it is not acceptable.

## Decisions that shaped the implementation

| Question | What the source says | What we chose |
| --- | --- | --- |
| Should every pattern use non-backtracking? | [.NET documents](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options) linear-time behavior, but lookarounds and matching backreferences are unsupported | Use it where compatible; use compiled matching and the same two-second timeout elsewhere |
| Should regex objects be recreated for every hit? | [.NET's guidance](https://learn.microsoft.com/dotnet/standard/base-types/best-practices-regex) recommends reuse and measuring compiled forms | Cache by pattern and options |
| Should custom regexes inherit the old global free-spacing option? | In [.NET free-spacing mode](https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options#ignore-white-space), spaces are ignored and `#` begins a comment | No. Custom regexes use normal .NET semantics; callers can request `(?i)` or `(?x)` themselves |
| Can the same expression be sent to .NET and cuDF? | [libcudf supports a different regex subset](https://docs.rapids.ai/api/cudf/stable/libcudf_docs/md_regex/), and Python cuDF exposes only selected flags through [`Series.str.contains`](https://docs.rapids.ai/api/cudf/stable/user_guide/api_docs/api/cudf.core.accessors.string.stringmethods.contains/) | Keep separate reviewed cuDF supersets, use explicit ASCII case classes and `flags=0`, then verify candidates with .NET |
| Is equal result count enough for CPU/GPU parity? | Equal totals can still contain different rows | No. A live parity claim must compare exact `(row, pattern)` pairs |

## Patterns added in this review

| Name | What it finds | Important limitation | Primary source |
| --- | --- | --- | --- |
| `cve` | `CVE-YYYY-NNNN...` candidates with a 4-to-19-digit sequence | It recognizes the identifier shape, not whether a record exists | [CVE production schema](https://github.com/CVEProject/cve-schema/blob/main/schema/docs/CVE_Record_Format_bundled.json) |
| `pem_private_key` | PKCS#8 `BEGIN PRIVATE KEY` and `BEGIN ENCRYPTED PRIVATE KEY` boundaries | It does not parse or validate the key body | [RFC 7468](https://www.rfc-editor.org/rfc/rfc7468) |
| `named_pipe` | Standalone, quoted, and practical whitespace-delimited Windows pipe paths | Windows permits spaces in pipe names, so embedded command-line boundaries require an explicit policy | [Microsoft pipe names](https://learn.microsoft.com/windows/win32/ipc/pipe-names) |
| `onion_v3` | 56-character Tor v3 `.onion` hostnames | The semantic stage verifies the version and checksum, not service existence, reachability, or ownership | [Tor onion-address encoding](https://spec.torproject.org/rend-spec/encoding-onion-addresses.html) |
| `ethereum` | `0x` followed by a 20-byte hexadecimal address | Mixed case must pass EIP-55; no casing proves account existence, use, or ownership | [EIP-55](https://eips.ethereum.org/EIPS/eip-55) |
| `sha256` | 64-character hexadecimal values shaped like SHA-256 output | Any unrelated 64-hex identifier has the same shape | [NIST FIPS 180-4](https://csrc.nist.gov/pubs/fips/180-4/upd1/final) |

## Useful fixes to older patterns

The review did more than add names to the catalog:

- Base58 wallet patterns are case-sensitive and reject `0`, `O`, `I`, and `l`.
- Monero integrated addresses no longer get shortened to a standard-address
  prefix.
- MAC addresses, SIDs, payment-card candidates, and BitLocker keys reject
  windows carved from longer structured values.
- SSNs require the same separator throughout.
- Email candidates accept long top-level domains and use atom-aware
  boundaries.
- IPv4 rejects addresses carved from a longer dotted token, while IPv6 now
  covers compressed and IPv4-embedded forms.
- Windows environment assignments accept common expansions, URLs,
  parentheses, spaces, and empty values while still rejecting NUL and line
  breaks.
- URL paths preserve empty segments, user information can contain `:`, and
  embedded absolute URLs are recognized without turning the pattern into a
  claim of full URI validation.
- With `--ro`, `urlUser` emits the username-shaped capture before an optional
  password instead of printing a credential-bearing URL prefix.
- Base64 no longer matches an empty string or common four-character noise.
- Case-distinct custom patterns such as `secret` and `SECRET` are both kept.
- Payment cards pass Luhn, Base58Check and CryptoNote wallet candidates pass
  their offline checksum and network rules, and BitLocker recovery passwords
  pass Microsoft's arithmetic checks.
- Tor v3 version/checksum, mixed-case Ethereum EIP-55, SID field widths,
  canonical Base64 pad bits, practical email length limits, and simple XML
  well-formedness are enforced uniformly across output paths.
- URI userinfo and full URI candidates reject malformed RFC 3986 percent
  escapes; full URIs also reject malformed bracketed IPv6 and IPvFuture hosts.

## 7 August forensic-reporting follow-up

The later reporting review added PII and forensic-artifact candidates for
international phone numbers, checksum-valid Canadian SINs, labelled and
calendar-valid dates of birth, MOD-97-valid IBANs, VINs, credential
assignments, browser credential-store fields/profile paths, and high-value
Registry paths for persistence, user activity, USB, execution, network, and
system identity.

JWT is no longer regex-only. The regex supplies a bounded compact-token
candidate and the semantic validator requires canonical Base64URL segments,
JSON object claims, a nonempty `alg`, and the applicable three-part JWS or
five-part JWE structure. It deliberately does not verify a signature, key,
issuer, audience, lifetime, or decrypt ciphertext, so the report calls it a
structurally valid candidate rather than an authenticated token. This follows
the decoding/validation sequence in [RFC 7519 section 7.2](https://www.rfc-editor.org/rfc/rfc7519#section-7.2).

The browser and Registry patterns are discovery aids for extracted strings.
They do not replace a browser-database parser, DPAPI/key-store processing, or a
binary Registry-hive parser. Their descriptions and report classifications
preserve that boundary.

## 13 August bounded-coverage follow-up

A three-review standards, runtime, and adversarial round added eleven bounded
classes: CPE 2.3, TLP 2.0, labelled Message-ID, LEI, labelled NPI, labelled ITIN,
labelled UK National Insurance number, and labelled MD5/SHA-1/SHA-384/SHA-512.
The same review rejected unlimited provider-token tails, generic entropy and
attribution patterns, and bare private identifiers whose public validation or
licensing was insufficient. It also closed named-capture path parity, mixed
`--lr`/`--fr` resolution, and report expression-binding defects before accepting
the expansion. See
[ADR-0009](architecture/adr-0009-bounded-forensic-pattern-expansion.md) for the
primary sources, strongest counterexamples, acceptance gates, and rollback.

## Patterns still deliberately left out

Some shapes are useful as a first-pass hunt but too weak for a default
"this is what the value is" pattern.

- **AWS and GitHub tokens:** provider formats evolve, and the fixed lengths
  copied into many public regex lists are not stable contracts.
- **MD5, SHA-1, and generic secrets:** the shapes are common enough to create
  noisy defaults without proving provenance.
- **PowerShell encoded commands:** a useful detector needs Base64 decoding and
  UTF-16LE validation. That belongs in a semantic detector rather than the
  regex-only catalog.

This does not mean those searches are forbidden. They are better supplied as
case-specific custom regexes or implemented as parsers that can validate the
content.

## CPU scheduling results

The regex path can divide work by hit or by pattern. Dividing by pattern is
simple, but a request containing only a few patterns leaves most cores idle on
a large machine.

On the review host (.NET 9.0.18, Windows, 22 logical processors), the
deterministic benchmark produced these seven-to-nine-repeat medians:

| Workload | Adaptive | Forced hit-major | Forced pattern-major |
| --- | ---: | ---: | ---: |
| 10,000 hits, one pattern | 4.888 ms | 6.524 ms | 79.286 ms |
| 10,000 hits, five patterns | 7.422 ms | 7.348 ms | 80.194 ms |
| 50,000 hits, five patterns | 19.398 ms | 20.498 ms | 52.163 ms |
| 250,000 hits, three patterns | 60.745 ms | 84.948 ms | 252.855 ms |
| 1,000,000 hits, one pattern | 294.840 ms | 283.315 ms | 2,215.047 ms |
| 1,000,000 hits, five patterns | 559.254 ms | 543.847 ms | 1,823.770 ms |

The production policy uses hit-major work when there are at least 10,000 hits
and fewer patterns than logical processors. It uses pattern-major work for
smaller inputs and for requests with enough patterns to fill the machine.
Concurrency never exceeds `Environment.ProcessorCount`.

Adaptive and forced-hit measurements can differ even when they choose the same
layout because thread-pool state, CPU frequency, and garbage collection add
noise. The benchmark rotates measurement order and is committed so the
crossover can be checked on other hardware.

Run it with:

```powershell
dotnet run --project dev-tools\regex-benchmark -c Release -- 2000000 7 5
```

The arguments are hit count, repetition count, and number of representative
patterns.

## Failure handling

Regex matching has a two-second per-evaluation timeout. After the first timeout,
the concurrent path stops scheduling new evaluations and fails the command.
When output is being written, the `.incomplete` marker remains in place.

RAPIDS startup has a 30-second deadline. The processing child has a
configurable deadline that defaults to ten minutes. A timed-out process is
killed with its descendants. CPU fallback is allowed only before any
GPU-derived output has been emitted.

## What was validated

At merge time:

- 158 Release tests passed;
- the Windows x64 self-contained single-file publish passed locally;
- the pull-request and post-merge GitHub Actions workflows passed;
- the catalog listed 33 patterns;
- adaptive and forced scheduling returned the same match counts; and
- independent correctness, hardware, and pattern-value detractors reported no
  remaining reproducible blocker.

The review machine did **not** have a working cuDF installation. Static syntax
gates, superset witnesses, offset-prefixed rows, timeout behavior, fallback
ordering, and .NET verification are tested. Exact live cuDF-to-CPU comparison
is still hardware-gated and should not be described as completed.

The 4 August follow-up added the semantic validation stage and expanded the
suite to 281 tests. Its 33-pattern adversarial corpus returned every expected
witness and rejected all 73,480 injected shape-correct semantic near-misses.
Those results, the complete per-pattern policy, performance measurements, and
the distinction between offline validity and live allocation are in the
[pattern validity review](pattern-validity-review-2026-08.md).

## Research trail

The hardware and regex-dialect round used
`Donovoi/robin@001a84f43f79c073c25660f8364e4416ce03d358` with
`openai/gpt-5.6-sol` at `xhigh`. It recommended per-pattern engine choices,
bounded CPU parallelism, and treating the GPU expression as a superset
prefilter.

Two broader candidate-discovery runs did not finish within their time or step
budgets. Their partial output was rejected rather than presented as research.
The final six additions were supported by the primary sources above and then
tested through independent detractor rounds.
