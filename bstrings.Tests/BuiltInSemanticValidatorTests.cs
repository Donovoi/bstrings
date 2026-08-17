using System.Text;
using System.Runtime.InteropServices;
using Xunit;

namespace bstrings.Tests;

public class BuiltInSemanticValidatorTests
{
    [Theory]
    [InlineData("cc", "4111 1111 1111 1111")]
    [InlineData("email", "string@g.com")]
    [InlineData("b64", "VGhpcyBpcyBhIHRlc3QgbWVzc2FnZS4=")]
    [InlineData("b64_candidate", "SGVsbG8=")]
    [InlineData("bitlocker", "001155-002310-003465-004620-005775-006930-008085-009240")]
    [InlineData("bitcoin", "1BoatSLRHtKNngkdXEeobR76b53LETtpyT")]
    [InlineData("bitcoin", "3J98t1WpEZ73CNmQviecrnyiWrnqRhWNLy")]
    [InlineData("bitcoin_segwit", "BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4")]
    [InlineData("bitcoin_segwit", "bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0")]
    [InlineData("tron", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t")]
    [InlineData("solana", "11111111111111111111111111111111")]
    [InlineData("xrp", "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh")]
    [InlineData("dogecoin", "D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H1")]
    [InlineData("zcash", "t1Hxw6JqWMnhDK5jRCieg5bFHM2qt7UtQvu")]
    [InlineData("zcash", "zs1qqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f389q5j5ctfvp5")]
    [InlineData("cardano", "addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl8")]
    [InlineData("stellar", "GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFN")]
    [InlineData("bitcoin_cash", "bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy")]
    [InlineData("ton", "EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHF")]
    [InlineData("litecoin", "LKKHMBjCU89fyFNgSRprDoD8Jb25N8uWvd")]
    [InlineData("litecoin", "ltc1qqypqxpq9qcrsszg2pvxq6rs0zqg3yyc5dyg36p")]
    [InlineData("avalanche", "X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlp")]
    [InlineData("move_address", "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("near", "alice.sub.near")]
    [InlineData("bittensor", "5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRzV")]
    [InlineData("hedera", "0.0.123-vfmkw")]
    [InlineData("canton_party", "Alice::1220f2fe29866fd6a0009ecc8a64ccdc09f1958bd0f801166baaee469d1251b2eb72")]
    [InlineData("provenance_scope", "scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkwel")]
    [InlineData("dashcoin2", "Xgtyuk76vhuFW2iT7UAiHgNdWXCf3J34wh")]
    [InlineData("onion_v3", "pg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion")]
    [InlineData("ethereum", "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    [InlineData("ethereum", "0xde709f2102306220921060314715629080e2fb77")]
    [InlineData("sid", "S-1-0xFFFFFFFFFFFF-4294967295")]
    [InlineData("xml", "<Root id=\"1\">value &amp; more</Root>")]
    [InlineData("xml", "<Root expression='x=y'>value</Root>")]
    [InlineData("urlUser", "u%20s")]
    [InlineData("url3986", "https://[2001:db8::1]/a%20b")]
    [InlineData("url3986", "custom://[v1.future:test]/path")]
    public void OfficialOrStandardsDerivedPositiveVectorsPass(string name, string candidate)
    {
        AssertValid(name, candidate);
    }

    [Theory]
    [InlineData("cc", "4111 1111 1111 1112")]
    [InlineData("b64", "SGVsbG9=")]
    [InlineData("b64", "0123456789ABCDEF0123456789ABCDEF01234567")]
    [InlineData("b64", "AAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("b64_candidate", "VGhpcyBpcyBhIHRlc3QgbWVzc2FnZS4=")]
    [InlineData("b64_candidate", "!!!!!!!!")]
    [InlineData("bitlocker", "001156-002310-003465-004620-005775-006930-008085-009240")]
    [InlineData("bitcoin", "1BoatSLRHtKNngkdXEeobR76b53LETtpyU")]
    [InlineData("bitcoin_segwit", "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5")]
    [InlineData("bitcoin_segwit", "bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqh2y7hd")]
    [InlineData("tron", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj61")]
    [InlineData("solana", "1111111111111111111111111111111")]
    [InlineData("xrp", "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyT1")]
    [InlineData("dogecoin", "D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H2")]
    [InlineData("zcash", "t1Hxw6JqWMnhDK5jRCieg5bFHM2qt7UtQv1")]
    [InlineData("cardano", "addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl1")]
    [InlineData("stellar", "GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFA")]
    [InlineData("bitcoin_cash", "bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyq")]
    [InlineData("ton", "EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHA")]
    [InlineData("litecoin", "LKKHMBjCU89fyFNgSRprDoD8Jb25N8uWv1")]
    [InlineData("avalanche", "X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlq")]
    [InlineData("move_address", "0x0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("near", "alice..sub.near")]
    [InlineData("bittensor", "5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRz1")]
    [InlineData("hedera", "0.0.123-abcde")]
    [InlineData("canton_party", "Alice::1320f2fe29866fd6a0009ecc8a64ccdc09f1958bd0f801166baaee469d1251b2eb72")]
    [InlineData("provenance_scope", "scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkw1")]
    [InlineData("dashcoin2", "Xgtyuk76vhuFW2iT7UAiHgNdWXCf3J34wi")]
    [InlineData("onion_v3", "qg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion")]
    [InlineData("ethereum", "0x5AAeb6053F3E94C9b9A09f33669435E7Ef1BeAed")]
    [InlineData("sid", "S-2-5-21")]
    [InlineData("sid", "S-1-281474976710656-1")]
    [InlineData("sid", "S-1-5-4294967296")]
    [InlineData("xml", "<Root id=>value</Root>")]
    [InlineData("urlUser", "u%zz")]
    [InlineData("url3986", "https://example.com/a%zz")]
    [InlineData("url3986", "https://[::::]/")]
    [InlineData("url3986", "custom://[v.future]/path")]
    public void ShapeCorrectButSemanticallyInvalidVectorsFail(string name, string candidate)
    {
        AssertInvalid(name, candidate);
    }

    [Fact]
    public void NewChecksumBearingWalletsRejectEveryPossibleFinalSymbolSubstitution()
    {
        var cases = new (string Name, string Value, string Alphabet)[]
        {
            ("bitcoin_segwit", "bc1p0xlxvlhemja6c4dqv22uapctqupfhlxm9h8z3k2e72q4k9hcz7vqzk5jj0", "qpzry9x8gf2tvdw0s3jn54khce6mua7l"),
            ("tron", "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t", "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"),
            ("xrp", "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh", "rpshnaf39wBUDNEGHJKLM4PQRST7VWXYZ2bcdeCg65jkm8oFqi1tuvAxyz"),
            ("dogecoin", "D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H1", "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"),
            ("zcash", "zs1qqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f389q5j5ctfvp5", "qpzry9x8gf2tvdw0s3jn54khce6mua7l"),
            ("cardano", "addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl8", "qpzry9x8gf2tvdw0s3jn54khce6mua7l"),
            ("stellar", "GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFN", "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"),
            ("bitcoin_cash", "bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy", "qpzry9x8gf2tvdw0s3jn54khce6mua7l"),
            ("ton", "EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHF", "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"),
            ("litecoin", "ltc1qqypqxpq9qcrsszg2pvxq6rs0zqg3yyc5dyg36p", "qpzry9x8gf2tvdw0s3jn54khce6mua7l"),
            ("avalanche", "X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlp", "qpzry9x8gf2tvdw0s3jn54khce6mua7l"),
            ("bittensor", "5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRzV", "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"),
            ("hedera", "0.0.123-vfmkw", "abcdefghijklmnopqrstuvwxyz"),
            ("provenance_scope", "scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkwel", "qpzry9x8gf2tvdw0s3jn54khce6mua7l"),
        };

        foreach (var (name, value, alphabet) in cases)
        {
            AssertValid(name, value);
            foreach (var replacement in alphabet)
            {
                if (replacement == value[^1])
                {
                    continue;
                }
                AssertInvalid(name, value[..^1] + replacement);
            }
        }
    }

    [Theory]
    [InlineData("0.0.1-dfkxr")]
    [InlineData("0.0.4-cjcuq")]
    [InlineData("0.0.12-uuuup")]
    [InlineData("0.0.1234567890-zbhlt")]
    [InlineData("12.345.6789-aoyyt")]
    [InlineData("1.23.456-adpbr")]
    public void HederaMatchesHip15MainnetReferenceVectors(string candidate)
    {
        AssertValid("hedera", candidate);
    }

    [Fact]
    public void CardanoRequiresMainnetNetworkTagAndAcceptsCanonicalPointers()
    {
        AssertValid(
            "cardano",
            "addr1gx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzer5pnz75xxcrzqf96k"
        );
        AssertInvalid(
            "cardano",
            "addr1vpu5vlrf4xkxv2qpwngf6cjhtw542ayty80v8dyr49rf5eg0yu80w"
        );
    }

    [Fact]
    public void BitcoinCashAcceptsBothStandardTwentyByteAddressTypes()
    {
        AssertValid(
            "bitcoin_cash",
            "bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy"
        );
        AssertValid(
            "bitcoin_cash",
            "bitcoincash:pr0662zpd7vr936d83f64u629v886aan7c77r3j5v5"
        );
    }

    [Fact]
    public void EmailUsesStableSmtpLimitsAndCurrentIanaRootZone()
    {
        AssertValid("email", "string@example.com");
        AssertValid("email", new string('a', 64) + "@example.com");
        AssertInvalid("email", new string('a', 65) + "@example.com");
        AssertInvalid("email", "string@host.invalidtld");
        AssertInvalid("email", "string@-host.com");
        AssertValid("email_candidate", "string@host.invalidtld");
        AssertInvalid("email_candidate", "string@example.com");

        var labels = string.Join('.', Enumerable.Repeat(new string('a', 63), 4));
        Assert.True(labels.Length > 253);
        AssertInvalid("email", "x@" + labels);
    }

    [Theory]
    [InlineData("", "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")]
    [InlineData("abc", "4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45")]
    public void Keccak256MatchesPublishedVectors(string input, string expectedHex)
    {
        Span<byte> hash = stackalloc byte[32];
        BuiltInSemanticValidator.Keccak256(Encoding.ASCII.GetBytes(input), hash);

        Assert.Equal(expectedHex, Convert.ToHexString(hash).ToLowerInvariant());
    }

    [Theory]
    [InlineData(
        "aeon",
        "WmsSWgtT1JPg5e3cK41hKXSHVpKW7e47bjgiKmWZkYrhSS5LhRemNyqayaSBtAQ6517eo5PtH9wxHVmM78JDZSUu2W8PqRiNs"
    )]
    [InlineData(
        "bytecoin",
        "2AaF4qEmER6dNeM6dfiBFL7kqund3HYGvMBF3ttsNd9SfzgYB6L7ep1Yg1osYJzLdaKAYSLVh6e6jKnAuzj3bw1oGyd1x7Z"
    )]
    [InlineData(
        "monero",
        "4AdUndXHHZ6cfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2684Rge"
    )]
    [InlineData(
        "monero",
        "4LL9oSLmtpccfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2bYXZKKQePHES9khPK"
    )]
    [InlineData(
        "sumokoin",
        "Sumoo72D2v7KEGvfPzGH5qC5VHGnLmafaAhoMooPwRALNwm2oSyK3myTaFefvyg5bviMbBXUFWN8McswTRowHNYXfo34VD9oWr7"
    )]
    public void CryptoNoteOfficialVectorsPass(string name, string candidate)
    {
        AssertValid(name, candidate);
        AssertInvalid(name, MutateFinalBase58Character(candidate));
    }

    [Theory]
    [InlineData("dashcoin", 72UL)]
    [InlineData("fantomcoin", 34UL)]
    public void LegacyCryptoNoteNetworksValidatePrefixLengthAndChecksum(
        string name,
        ulong prefix
    )
    {
        var address = BuildCryptoNoteAddress(prefix);
        Assert.Matches(
            RegexOutputCore.GetOrCreateRegex(name, BuiltInPatternCatalog.Patterns[name]),
            address
        );
        AssertValid(name, address);
        AssertInvalid(name, MutateFinalBase58Character(address));
        AssertInvalid(name, BuildCryptoNoteAddress(prefix + 1));
    }

    [Fact]
    public void EverySemanticValidatorIsAppliedByRecordAndWholeHitPaths()
    {
        var cases = new[]
        {
            (Name: "cc", Valid: "4111111111111111", Invalid: "4111111111111112"),
            (
                Name: "bitcoin",
                Valid: "1BoatSLRHtKNngkdXEeobR76b53LETtpyT",
                Invalid: "1BoatSLRHtKNngkdXEeobR76b53LETtpyU"
            ),
            (
                Name: "onion_v3",
                Valid: "pg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion",
                Invalid: "qg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion"
            ),
        };

        foreach (var testCase in cases)
        {
            var definition = BuiltInPatternCatalog.ByName[testCase.Name];
            var regex = RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern);
            var data = $"valid={testCase.Valid} invalid={testCase.Invalid}";
            var records = RegexOutputCore
                .CreateRecords(
                    new ParsedHit(data, data, string.Empty),
                    definition.Name,
                    regex,
                    regexOutput: true,
                    sourceFile: "sample.bin",
                    patternType: "Regex"
                )
                .Select(record => record.DataFound)
                .ToList();

            Assert.Equal([testCase.Valid], records);
            Assert.True(RegexOutputCore.IsMatch(definition.Name, regex, data));
            Assert.False(RegexOutputCore.IsMatch(definition.Name, regex, testCase.Invalid));
        }
    }

    [Fact]
    public void GeneratedAndStreamingUriPathsApplySemanticValidation()
    {
        var definition = BuiltInPatternCatalog.ByName["url3986"];
        var regex = RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern);
        const string valid = "https://[2001:db8::1]/a%20b";
        const string invalid = "https://[::::]/a%zz";

        Assert.True(RegexOutputCore.IsMatch(definition.Name, regex, valid));
        Assert.False(RegexOutputCore.IsMatch(definition.Name, regex, invalid));
        Assert.True(
            RegexOutputCore.IsMatchWithGeneratedShortInput(
                definition.Name,
                definition.Pattern,
                regex,
                valid
            )
        );
        Assert.False(
            RegexOutputCore.IsMatchWithGeneratedShortInput(
                definition.Name,
                definition.Pattern,
                regex,
                invalid
            )
        );

        var output = new List<string>();
        RegexOutputCore.AppendStreamingRecords(
            new ParsedHit(invalid, invalid, string.Empty),
            definition.Name,
            regex,
            definition,
            isCsvOutput: false,
            sourceFile: "sample.bin",
            output
        );
        Assert.Empty(output);
    }

    private static void AssertValid(string name, string candidate)
    {
        Assert.True(
            BuiltInSemanticValidator.IsValid(BuiltInPatternCatalog.ByName[name], candidate),
            $"Expected valid {name}: {candidate}"
        );
    }

    private static void AssertInvalid(string name, string candidate)
    {
        Assert.False(
            BuiltInSemanticValidator.IsValid(BuiltInPatternCatalog.ByName[name], candidate),
            $"Expected invalid {name}: {candidate}"
        );
    }

    private static string MutateFinalBase58Character(string candidate)
    {
        var characters = candidate.ToCharArray();
        characters[^1] = characters[^1] == '1' ? '2' : '1';
        return new string(characters);
    }

    private static string BuildCryptoNoteAddress(ulong prefix)
    {
        var body = new List<byte>();
        do
        {
            var current = (byte)(prefix & 0x7F);
            prefix >>= 7;
            if (prefix != 0)
            {
                current |= 0x80;
            }
            body.Add(current);
        } while (prefix != 0);

        body.AddRange(Enumerable.Range(1, 64).Select(value => (byte)value));
        Span<byte> checksum = stackalloc byte[32];
        BuiltInSemanticValidator.Keccak256(CollectionsMarshal.AsSpan(body), checksum);
        body.AddRange(checksum[..4].ToArray());
        return EncodeCryptoNoteBase58(CollectionsMarshal.AsSpan(body));
    }

    private static string EncodeCryptoNoteBase58(ReadOnlySpan<byte> data)
    {
        const string alphabet =
            "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        ReadOnlySpan<int> encodedBlockSizes = [0, 2, 3, 5, 6, 7, 9, 10, 11];
        var builder = new StringBuilder((data.Length * 11 + 7) / 8);
        Span<char> encoded = stackalloc char[11];
        for (var start = 0; start < data.Length; start += 8)
        {
            var block = data.Slice(start, Math.Min(8, data.Length - start));
            ulong value = 0;
            foreach (var current in block)
            {
                value = (value << 8) | current;
            }

            var encodedSize = encodedBlockSizes[block.Length];
            encoded[..encodedSize].Fill('1');
            for (var index = encodedSize - 1; value > 0; index--)
            {
                encoded[index] = alphabet[(int)(value % 58)];
                value /= 58;
            }
            builder.Append(encoded[..encodedSize]);
        }
        return builder.ToString();
    }
}
