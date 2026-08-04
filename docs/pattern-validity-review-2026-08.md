# Built-in pattern validity review

Date: 4 August 2026

Scope note: this report preserves the original 33-pattern review and its exact
benchmark totals. The catalog was subsequently expanded to 51 patterns. See the
[top-50 crypto address coverage review](crypto-address-coverage-2026-08.md) for
the 18 additions, their validators, current research, and new test evidence.

## Short version

All 33 built-in patterns were reviewed against their defining standards or a
maintained first-party implementation. Eighteen patterns now have a deterministic
semantic validator after the regex candidate stage. The validator rejects a hit
when the evidence bytes themselves prove it is invalid: a failed checksum,
impossible length or numeric field, non-canonical encoding, wrong network prefix,
or malformed structure.

One proposed example needs correcting: `string@g.com` is a syntactically valid
mailbox. DNS labels may be one character long. Rejecting it because `g` is short,
unknown, private, or currently unresolvable would confuse syntax with live DNS
state and could discard historical evidence. The default validator instead
enforces the local-part and domain limits from
[RFC 5321](https://www.rfc-editor.org/rfc/inline-errata/rfc5321.html), while the
catalog regex supplies the practical dot and label rules.

The default policy is deliberately offline and time-stable. DNS, current TLD and
telephone allocations, issuer tables, account balances, ownership, and network
reachability may be useful corroboration in a case-specific second pass, but
they are not safe reasons to suppress raw forensic hits.

## Pattern-by-pattern decision

| Pattern | Default decision | What the hit proves | What it does not prove |
| --- | --- | --- | --- |
| `guid` | Shape | Canonical hyphenated GUID shape | Allocation or object existence |
| `usPhone` | Shape and NANP digit constraints | A possible NANP number | Current assignment or subscriber |
| `unc` | Shape | A complete UNC-path candidate | Share existence or access |
| `named_pipe` | Shape | A Windows named-pipe path candidate | That the pipe existed or was open |
| `mac` | Shape | A 48-bit colon- or hyphen-delimited MAC candidate | IEEE allocation, device identity, or observation time |
| `ssn` | Shape and structural exclusions | A structurally possible US SSN | Issuance or identity |
| `cc` | **Semantic** | A 13-to-19 digit candidate with a valid Luhn checksum | Issuer allocation, active account, or ownership |
| `ipv4` | Shape and octet ranges | Four decimal octets in the IPv4 range | Assignment, routability, or observation |
| `ipv6` | Shape | A full, compressed, or IPv4-embedded IPv6 candidate | Assignment or routability |
| `email` | **Semantic** | A practical RFC-style address within SMTP local/domain limits | DNS, deliverability, mailbox existence, or ownership |
| `zip` | Shape | A US ZIP or ZIP+4 candidate | Current USPS allocation or location |
| `urlUser` | **Semantic** | A username-shaped URL userinfo field with valid percent escapes | Identity, password validity, or URL reachability |
| `url3986` | **Semantic** | An absolute hierarchical URI with valid percent escapes and bracketed IP literals | DNS, reachability, safety, or resource existence |
| `xml` | **Semantic** | A complete, well-formed simple XML element, parsed with DTDs prohibited | Schema validity or trusted content |
| `sid` | **Semantic** | SID revision, 48-bit authority, count, and 32-bit subauthorities are structurally valid | Principal existence or ownership |
| `win_path` | Shape | A Windows path candidate | File existence or accessibility |
| `var_set` | Shape | A Windows environment assignment candidate | That it was executed or persisted |
| `reg_path` | Shape | A Windows Registry hive path candidate | Key existence or access |
| `b64` | **Semantic** | Canonical RFC 4648 Base64 syntax and pad bits | Meaning, successful higher-level parsing, or benign content |
| `bitlocker` | **Semantic** | All eight recovery-password blocks satisfy Microsoft's range and divisibility rules | That the password unlocks this evidence item |
| `bitcoin` | **Semantic** | Canonical mainnet P2PKH/P2SH Base58Check version, length, and checksum | Address use, balance, ownership, or key possession |
| `aeon` | **Semantic** | Legacy Aeon standard-address prefix, payload length, encoding, and CryptoNote checksum | Curve-point validity, address use, balance, or ownership |
| `bytecoin` | **Semantic** | Bytecoin standard-address prefix, payload length, encoding, and CryptoNote checksum | Curve-point validity, use, balance, or ownership |
| `dashcoin` | **Semantic** | Legacy Dashcoin CryptoNote prefix, payload length, encoding, and checksum | Curve-point validity, use, balance, or ownership |
| `dashcoin2` | **Semantic** | Dash mainnet P2PKH/P2SH Base58Check version, length, and checksum | Address use, balance, ownership, or key possession |
| `fantomcoin` | **Semantic** | Legacy Fantomcoin prefix, payload length, encoding, and CryptoNote checksum | Curve-point validity, use, balance, or ownership |
| `monero` | **Semantic** | Mainnet standard, integrated, or subaddress prefix, payload length, encoding, and checksum | Curve-point validity, address use, balance, or ownership |
| `sumokoin` | **Semantic** | SumoKoin standard-address prefix, payload length, encoding, and checksum | Curve-point validity, use, balance, or ownership |
| `cve` | Shape | A CVE identifier candidate with a valid sequence-number shape | That a CVE record exists |
| `pem_private_key` | Shape | An RFC 7468 PKCS#8 private-key boundary | A complete, parseable, unencrypted, or usable key |
| `onion_v3` | **Semantic** | Tor v3 Base32 encoding, version byte, and checksum | Service existence, reachability, or ownership |
| `ethereum` | **Semantic** | A 20-byte hex address; mixed case must pass EIP-55 | Account existence, use, balance, ownership, or key possession |
| `sha256` | Shape | Exactly 64 hexadecimal characters | That the value was produced by SHA-256 |

Ethereum deliberately accepts all-lowercase and all-uppercase addresses, as
documented by [EIP-2304](https://eips.ethereum.org/EIPS/eip-2304); only
mixed-case candidates make a checksum claim and therefore must satisfy
[EIP-55](https://eips.ethereum.org/EIPS/eip-55).

## What was learned from `bulk_extractor`

The comparison used `bulk_extractor` commit
[`952700ab`](https://github.com/simsong/bulk_extractor/tree/952700ab09c3ed35dcbea7259e8dc3d46123e140),
not an unversioned snapshot.

- Its credit-card scanner combines Luhn validation with issuer/prefix tables,
  context heuristics, and histograms. This fork adopts the stable Luhn invariant.
  It does not silently suppress evidence using allocation tables that can age.
- Its Bitcoin scanner decodes a 25-byte address and verifies the double-SHA-256
  checksum. This fork performs the same core check and additionally requires a
  canonical Base58 representation and a supported mainnet version.
- Its email scanner rejects double dots and multiple `@` characters and applies
  the 64-byte local and 253-byte domain presentation limits. This fork enforces
  the same limits across every output path.
- Its BitLocker scanner recognizes the textual shape. This fork also applies
  Microsoft's documented arithmetic test to every one of the eight blocks.

`bulk_extractor` also uses scanner-specific surrounding context and recursive
decoding. Those capabilities are valuable, but they are not interchangeable
with this catalog's exact candidate semantics. A context score may be exposed
later as evidence metadata; it should not make an otherwise valid raw hit vanish.

## Standards and implementation sources

The semantic checks are grounded in primary sources:

- email limits and label syntax: [RFC 5321](https://www.rfc-editor.org/rfc/inline-errata/rfc5321.html)
- canonical Base64: [RFC 4648](https://www.rfc-editor.org/rfc/rfc4648#section-4)
- URI percent encoding and IP literals: [RFC 3986](https://www.rfc-editor.org/rfc/rfc3986#section-3.2.2)
- BitLocker recovery-password arithmetic: [Microsoft `IsNumericalPasswordValid`](https://learn.microsoft.com/windows/win32/secprov/isnumericalpasswordvalid-win32-encryptablevolume)
- Tor v3 address version and checksum: [Tor rendezvous specification](https://spec.torproject.org/rend-spec/encoding-onion-addresses.html)
- Microsoft SID field widths: [MS-DTYP SID packet](https://learn.microsoft.com/openspecs/windows_protocols/ms-dtyp/f992ad60-0fe4-4b87-9fed-beb478836861)
- Monero address layouts and Keccak checksum: [standard](https://docs.getmonero.org/public-address/standard-address/), [integrated](https://docs.getmonero.org/public-address/integrated-address/), [subaddress](https://docs.getmonero.org/public-address/subaddress/), and [Keccak-256](https://docs.getmonero.org/cryptography/keccak-256/)
- Dash mainnet Base58 versions: [Dash `chainparams.cpp`](https://github.com/dashpay/dash/blob/master/src/chainparams.cpp)
- legacy CryptoNote prefixes: maintained or recovered first-party source trees for [Aeon](https://github.com/aeonix/aeon), [Bytecoin](https://github.com/bcndev/bytecoin), [Dashcoin](https://github.com/dashcoin/dashcoin), [Fantomcoin](https://github.com/xdn-project/fantomcoin), and [SumoKoin](https://github.com/sumoprojects/sumokoin)

## Adversarial validation

The deterministic 1 MiB-per-pattern corpus contains two real witnesses for
each of all 33 patterns. For each semantic pattern it also injects thousands of
shape-correct near-misses: single-character checksum mutations, wrong versions
or network prefixes, non-canonical Base64 pad bits, invalid BitLocker arithmetic,
over-range SID fields, malformed XML, and email length violations.

| Result | Observed |
| --- | ---: |
| Patterns returning the exact expected witness IDs | **33/33** |
| Legitimate expected semantic hits returned | **36/36** |
| Shape-correct semantic near-misses injected | **73,480** |
| Near-misses rejected | **73,480/73,480** |
| Near-misses leaked | **0** |
| Regex-only semantic hits before validation | 73,516 |
| Validated semantic hits | 36 |

This measures a hostile synthetic corpus, not a real-world false-positive rate.
It proves rejection of the invalid classes represented by the corpus; it cannot
prove that every future evidence source has the same distribution.

The corrected adversarial corpus was also encoded as UTF-16LE and scanned with
the ASCII path disabled. It returned the exact expected **66/66 records across
33/33 patterns**, including byte offsets, without an extra or missing hit.

The same validator is applied to ordinary CPU output, streaming output, bounded
timeout retries, generated-regex fallbacks, enrichment records, and the final
.NET authority pass after a RAPIDS candidate prefilter. Backend parity tests
exercise these routes rather than trusting aggregate counts.

On a separate 16 MiB-per-pattern, candidate-dense corpus, the median runtime
ratio across the 18 semantic patterns was **1.020x** compared with the old
regex-only executable. Per-pattern ratios ranged from **0.985x to 1.094x** over
seven rotated warm-cache runs. This is a deliberately unfriendly validation
workload and is not a universal throughput forecast.

## Remaining limits and falsifiers

High confidence is warranted for the implemented offline invariants and their
tested output paths. Confidence is lower for whether every possible real-world
false-positive class has been anticipated. A new official vector that is
rejected, an invalid checksum that is emitted, a valid hit lost at a chunk
boundary, or a CPU/RAPIDS record mismatch would falsify the current conclusion
and must block publication of an accuracy claim.

Good future additions are optional corroboration fields—DNS state captured with
a timestamp, known-allocation lookup provenance, contextual entropy, or nearby
artifact type—not default suppression. That preserves historical evidence while
giving analysts a way to rank candidates.
