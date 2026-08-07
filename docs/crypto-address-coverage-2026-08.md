# Crypto address coverage review — 4 August 2026

Historical scope note: this review records the 51-pattern catalog as it existed
on 4 August. The v1.9.4 forensic-reporting follow-up expanded the catalog to 66
patterns; the measurements and top-50 mapping below intentionally remain tied
to their dated 51-pattern corpus. See
[forensic reporting](forensic-reporting-2026-08.md) for the later additions.

## Result

At the time of this review, the built-in catalog had 51 patterns, including a
27-pattern `wallets` group. Eighteen new address or on-ledger identifier
families were added after
mapping the current market-cap top 50 to the networks that actually serialize
their accounts and assets. This is deliberately not “one regex per token”:
USDT, USDC, DAI, SHIB, PAXG, and most other tokens use the address format of
their host chain.

The high-confidence result is that every asset in the dated top-50 snapshot has
at least one native or primary distribution rail covered. Multi-chain token
coverage is not exhaustive, and a syntactically valid address is never evidence
that its owner committed a crime.

## Dated top-50 mapping

Ranking source: CoinGecko's public
[`/coins/markets`](https://api.coingecko.com/api/v3/coins/markets?vs_currency=usd&order=market_cap_desc&per_page=50&page=1&sparkline=false)
and [`/coins/list?include_platform=true`](https://api.coingecko.com/api/v3/coins/list?include_platform=true)
endpoints, captured at `2026-08-04T00:15:03Z`. Market-cap order changes, so this
table is a snapshot rather than a permanent definition of “top 50.”

| Rank | Asset | Relevant built-in family or families |
| ---: | --- | --- |
| 1 | Bitcoin (BTC) | `bitcoin`, `bitcoin_segwit` |
| 2 | Ethereum (ETH) | `ethereum` (EVM) |
| 3 | Tether (USDT) | `ethereum`, `tron`, `solana`, `ton`, `near`, `move_address` |
| 4 | BNB (BNB) | `ethereum` (EVM/BSC account form) |
| 5 | USDC (USDC) | `ethereum`, `tron`, `solana`, `stellar`, `xrp`, `move_address` |
| 6 | XRP (XRP) | `xrp` |
| 7 | Solana (SOL) | `solana` |
| 8 | TRON (TRX) | `tron` |
| 9 | Figure Heloc (FIGR_HELOC) | `provenance_scope` for the asset scope identifier |
| 10 | Hyperliquid (HYPE) | `ethereum` for user/EVM-style account identifiers |
| 11 | Dogecoin (DOGE) | `dogecoin` |
| 12 | USDS (USDS) | `ethereum`, `solana` |
| 13 | LEO Token (LEO) | `ethereum` |
| 14 | Rain (RAIN) | `ethereum` (Arbitrum/EVM) |
| 15 | Zcash (ZEC) | `zcash` |
| 16 | Cardano (ADA) | `cardano` |
| 17 | Monero (XMR) | `monero` |
| 18 | WhiteBIT Coin (WBT) | `ethereum`, `tron` |
| 19 | Chainlink (LINK) | `ethereum`, `solana` |
| 20 | Stellar (XLM) | `stellar` |
| 21 | Dai (DAI) | `ethereum` |
| 22 | Canton (CC) | `canton_party` |
| 23 | Bitcoin Cash (BCH) | `bitcoin_cash` |
| 24 | USD1 (USD1) | `ethereum`, `tron`, `solana`, `move_address` |
| 25 | Gram / Toncoin (GRAM) | `ton`, `ethereum` |
| 26 | Ethena USDe (USDE) | `ethereum`, `ton`, `solana`, `move_address` |
| 27 | Global Dollar (USDG) | `ethereum`, `solana` |
| 28 | Litecoin (LTC) | `litecoin` |
| 29 | Hedera (HBAR) | `hedera` for HIP-15 checksummed account IDs |
| 30 | Circle USYC (USYC) | `ethereum`, `solana` |
| 31 | Shiba Inu (SHIB) | `ethereum` |
| 32 | Avalanche (AVAX) | `avalanche` for X/P chains; `ethereum` for C-Chain |
| 33 | Sui (SUI) | `move_address` |
| 34 | PayPal USD (PYUSD) | `ethereum`, `solana`, `stellar` |
| 35 | BlackRock BUIDL (BUIDL) | `ethereum`, `solana`, `move_address` |
| 36 | Cronos (CRO) | `ethereum` (current EVM-style rails) |
| 37 | Tether Gold (XAUT) | `ethereum` |
| 38 | Uniswap (UNI) | `ethereum`, `near` |
| 39 | NEAR Protocol (NEAR) | `near` for named mainnet accounts |
| 40 | Ondo US Dollar Yield (USDY) | `ethereum`, `solana`, `stellar`, `move_address` |
| 41 | Bittensor (TAO) | `bittensor` |
| 42 | OKB (OKB) | `ethereum` (EVM/X Layer account form) |
| 43 | Ondo (ONDO) | `ethereum` |
| 44 | PAX Gold (PAXG) | `ethereum` |
| 45 | World Liberty Financial (WLFI) | `ethereum`, `solana` |
| 46 | Aster (ASTER) | `ethereum` (BSC/EVM) |
| 47 | HTX DAO (HTX) | `tron`, `ethereum` |
| 48 | MemeCore (M) | `ethereum` (BSC/EVM) |
| 49 | USDD (USDD) | `tron`, `ethereum` |
| 50 | Ripple USD (RLUSD) | `xrp` for issuer accounts; `ethereum` for the EVM token |

For multi-chain tokens, the table lists useful high-volume families rather than
every bridge and wrapper CoinGecko reports. Contract IDs are useful evidence,
but a contract ID is not necessarily a recipient wallet.

## What current crime reporting changes

The evidence does not support labeling a particular coin as a “criminal coin.”
It supports prioritizing several liquid rails during triage:

- [Chainalysis' 2026 overview](https://www.chainalysis.com/blog/2026-crypto-crime-report-introduction/)
  estimates stablecoins represented 84% of identified illicit transaction
  volume in 2025. That includes sanctions and other illicit categories, not just
  cybercrime.
- [Chainalysis' 2025 overview](https://www.chainalysis.com/blog/2025-crypto-crime-report-introduction/)
  says Bitcoin remained dominant in ransomware and darknet markets, while
  Monero was increasingly important for darknet-market activity and is omitted
  from ordinary on-chain measurement.
- [Chainalysis' darknet-market review](https://www.chainalysis.com/blog/darknet-markets-2025/)
  documents some operators moving to Monero-only payment policies.
- [TRM Labs' 2025 Crypto Crime Report](https://www.trmlabs.com/reports-and-whitepapers/2025-crypto-crime-report)
  reports that TRON represented 58% of the illicit volume it measured across
  the analyzed chains in 2024. Much of that figure involved sanctioned or
  blocklisted entities, so it must not be restated as “58% of cybercrime.”
- [Chainalysis' seizable-assets analysis](https://www.chainalysis.com/blog/landscape-of-seizable-crypto-assets-2025/)
  found Bitcoin still represented 75% of balances held by identified illicit
  entities, even as stablecoin and Ether balances grew.

That evidence is why the practical priority set is Bitcoin (legacy, SegWit and
Taproot), EVM accounts, TRON, Monero, Solana, XRP, TON, Litecoin, Zcash, and
Bitcoin Cash. It is a search-order decision, not an attribution conclusion.

## Validation actually performed

| Built-in | Offline acceptance checks |
| --- | --- |
| `bitcoin_segwit` | Mainnet HRP, witness version/program length, Bech32 for v0, Bech32m for v1–v16 |
| `tron` | 21-byte payload, `0x41` network byte, double-SHA-256 Base58Check |
| `solana` | Canonical Base58 decoding to exactly 32 bytes |
| `xrp` | XRPL alphabet, account type `0x00`, 20-byte payload, double-SHA-256 checksum |
| `dogecoin` | Mainnet P2PKH/P2SH version, payload length, Base58Check |
| `zcash` | Transparent mainnet prefix/Base58Check; Sapling HRP, 43-byte payload and Bech32 checksum |
| `cardano` | Mainnet network tag, Shelley payment type, payload length, canonical pointer encoding, Bech32 checksum |
| `stellar` | `G` StrKey version, 32-byte Ed25519 payload, canonical Base32, little-endian CRC16-XModem |
| `bitcoin_cash` | Explicit `bitcoincash:` prefix, CashAddr checksum, supported P2PKH/P2SH version and 20-byte hash size |
| `ton` | Mainnet tag, base/master workchain, 36-byte form, CRC16-XModem |
| `litecoin` | Mainnet P2PKH/current P2SH Base58Check or `ltc` witness Bech32/Bech32m |
| `avalanche` | X/P chain prefix, `avax` HRP, 20-byte payload, Bech32 checksum |
| `move_address` | Canonical lowercase `0x` plus 32 bytes used by Sui/Aptos |
| `near` | Mainnet `.near` suffix, 2–64-character account grammar, canonical separators |
| `bittensor` | 32-byte SS58 account, prefix 42, Blake2b-512 `SS58PRE` checksum |
| `hedera` | Canonical numeric components plus the HIP-15 checksum for mainnet ledger ID `0x00` |
| `canton_party` | Canonical party hint plus SHA-256 multihash namespace fingerprint (`1220` + 32 bytes) |
| `provenance_scope` | `scope` HRP, 17-byte payload, scope type `0x00`, Bech32 checksum |

The existing `bitcoin`, `monero`, `ethereum`, and other wallet patterns retain
their earlier semantic checks. `ethereum` is also the EVM-family pattern:
mixed-case candidates must pass EIP-55, while all-lowercase and all-uppercase
forms remain valid under the standard.

### Primary format sources

The implementation was checked against first-party protocol material rather
than address examples copied from aggregator pages:

- Bitcoin witness rules: [BIP-173](https://bips.dev/173/) and
  [BIP-350](https://bips.dev/350/)
- [TRON account format](https://developers.tron.network/docs/account),
  [Solana accounts](https://solana.com/docs/core/accounts), and
  [XRPL Base58 encodings](https://xrpl.org/docs/references/protocol/data-types/base58-encodings)
- Dogecoin and Litecoin mainnet version bytes from their official
  [`chainparams.cpp`](https://github.com/dogecoin/dogecoin/blob/master/src/kernel/chainparams.cpp)
  and
  [`chainparams.cpp`](https://github.com/litecoin-project/litecoin/blob/master/src/kernel/chainparams.cpp)
- [Zcash protocol specification](https://zips.z.cash/protocol/protocol.pdf),
  [Cardano CIP-19](https://cips.cardano.org/cip/CIP-19), and
  [Stellar address conversion](https://developers.stellar.org/docs/build/guides/conversions/address-conversions)
- [Bitcoin Cash CashAddr](https://documentation.cash/protocol/blockchain/encoding/cashaddr.html),
  [TON address formats](https://docs.ton.org/foundations/addresses/formats), and
  [Avalanche cryptographic primitives](https://build.avax.network/docs/rpcs/other/standards/cryptographic-primitives)
- [Sui address source](https://github.com/MystenLabs/sui/blob/main/crates/sui-types/src/base_types.rs),
  [NEAR account IDs](https://docs.near.org/protocol/account-id), and the
  [Polkadot SDK SS58 implementation](https://github.com/paritytech/polkadot-sdk/blob/master/substrate/primitives/core/src/crypto.rs)
- [Hedera HIP-15](https://hips.hedera.com/hip/hip-15),
  [Canton party identifiers](https://docs.digitalasset.com/build/3.4/explanations/parties-users.html),
  and the
  [Provenance metadata module](https://docs.provenance.io/build/sdk/metadata-module)

## Test evidence

- All 51 built-ins have a positive witness, a negative witness, a compilable
  authoritative regex, and a RAPIDS-compatible broad prefilter where one is
  declared.
- The .NET suite has 331 passing tests after this change.
- Checksum-bearing additions reject every alternate final symbol in their
  encoding alphabets; the suite also includes the published BIP-173/BIP-350,
  CIP-19, HIP-15, XRPL, Stellar, CashAddr, TON, and Provenance examples.
- The deterministic pattern-corpus generator is version 4 and carries witnesses
  for all 51 built-ins so boundary, EOF, ASCII, UTF-16LE, dense, and adversarial
  runs remain reproducible.
- An end-to-end Release-build run checked all 51 patterns against both a 2 MiB
  adversarial ASCII corpus and a 2 MiB UTF-16LE corpus. All 102 corpus runs
  matched their SHA-256 manifests and returned exactly 408 expected
  value/byte-offset pairs, with no missing or extra records and no incomplete
  outputs.

## Limits that matter in an examination

- A valid checksum proves only that a string is internally well formed. It does
  not prove allocation, activity, balance, ownership, time, or criminal use.
- Solana and Move-family addresses have no embedded network checksum. A
  canonical 32-byte value can be valid on more than one system; report it as a
  candidate until context or a ledger query establishes the network.
- Prefix-42 SS58 is shared by Substrate-family systems. `bittensor` is a useful
  search label, not exclusive network attribution.
- Zcash Sapling validation covers encoding, length and checksum; it does not do
  an elliptic-curve point check. Unified `u1` addresses are not yet included.
- Hedera deliberately requires the optional HIP-15 suffix. This sharply reduces
  false positives but does not find legacy unchecksummed `0.0.N` strings.
- Bitcoin Cash requires the explicit `bitcoincash:` prefix to avoid treating a
  short prefixless CashAddr as an unrelated token.
- Litecoin's deprecated version-5 P2SH form is byte-for-byte indistinguishable
  from Bitcoin P2SH. It is found by `bitcoin`; surrounding evidence is needed to
  decide which network was intended.
- Canton and Provenance identifiers can identify parties or assets without
  being ordinary transferable wallet accounts.

For casework, retain byte offsets and surrounding context, deduplicate exact
values after extraction, and then enrich candidates with chain-aware ledger
queries in a separate, logged step. Never make identity or culpability claims
from address shape alone.
