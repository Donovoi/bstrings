using System.Text.RegularExpressions;
using Xunit;

namespace bstrings.Tests;

public class BuiltInPatternCatalogTests
{
    private sealed record PatternCorpus(
        IReadOnlyList<string> Positives,
        IReadOnlyList<string> Negatives
    )
    {
        public PatternCorpus(string positive, string negative)
            : this([positive], [negative]) { }
    }

    private static readonly IReadOnlyDictionary<string, PatternCorpus> Corpus =
        new Dictionary<string, PatternCorpus>(StringComparer.OrdinalIgnoreCase)
        {
            ["guid"] = new(
                "id=00112233-4455-6677-8899-aabbccddeeff",
                "00112233-4455-6677-8899-aabbccddeezz"
            ),
            ["usPhone"] = new("call (212) 555-0199 now", "call (212555-0199"),
            ["unc"] = new(@"copy \\server-01\share$\folder", @"copy \server\share"),
            ["named_pipe"] = new(
                [
                    @"open \\.\pipe\svc-control",
                    @"\\SERVER-01\PIPE\remote-service",
                    @"open \\.\pipe\svc-control --out C:\temp\x",
                    "\"\\\\.\\pipe\\service control\"",
                    @"\\.\pipe\standalone service",
                ],
                [
                    @"open \\server\share\file",
                    @"open \\.\pipe\",
                    @"open \\.\pipe\svc\extra",
                ]
            ),
            ["mac"] = new("mac=00:11:22:aa:BB:cc", "00:11-22:33:44:55"),
            ["ssn"] = new(
                ["ssn 123-45-6789", "ssn 123 45 6789"],
                ["ssn 666-45-6789", "ssn 123-45 6789", "ssn 123 45-6789"]
            ),
            ["cc"] = new(
                ["card 4111 1111 1111 1111 end"],
                ["card 4111 1111 end", "card 4111 1111 1111 1112 end"]
            ),
            ["ipv4"] = new("peer=192.168.1.250:443", "peer=1.2.3.4.5"),
            ["ipv6"] = new("peer ::ffff:192.0.2.128 active", "peer 2001:::1"),
            ["email"] = new(
                [
                    "mail user.name+tag@example.technology now",
                    "mail #@example.com now",
                    "mail !foo@example.com now",
                    "mail string@g.com now",
                ],
                ["mail user@-example.com", "mail " + new string('a', 65) + "@g.com"]
            ),
            ["zip"] = new("Sydney mirror 90210-1234 ready", "code 1234 ready"),
            ["urlUser"] = new(
                [
                    "proxy=https://analyst:secret@example.com/path",
                    "proxy=https://u%20s@example.com/path",
                ],
                [
                    "https://example.com/path",
                    "proxy=https://u%zz@example.com/path",
                    "proxy=https://user:%zz@example.com/path",
                ]
            ),
            ["url3986"] = new(
                [
                    "visit https://user@example.com:8443/a//b?x=1#fragment now",
                    "visit https://[2001:db8::1]/a%20b now",
                ],
                [
                    "not a url",
                    "visit https://example.com/a%zz now",
                    "visit https://[::::]/ now",
                ]
            ),
            ["xml"] = new(
                ["<Root id=\"1\">value</Root>"],
                ["<Root>value</root>", "<Root id=>value</Root>"]
            ),
            ["sid"] = new(
                ["owner=S-1-5-21-1-2-3-1001;"],
                ["owner=S-1-x-21", "owner=S-2-5-21"]
            ),
            ["win_path"] = new(
                "open \"C:\\folder one\\file.txt\" now",
                "open C:relative.txt"
            ),
            ["var_set"] = new(
                [
                    "TEMP=C:\\Windows\\Temp",
                    "PATH=%SystemRoot%\\System32;%PATH%",
                    "ProgramFiles(x86)=C:\\Program Files (x86)",
                    "ENDPOINT=https://example.com/a?x=1&y=2",
                    "EMPTY=",
                ],
                ["=C:\\Windows", "9INVALID=value", "BAD\u0000=value", "BAD=line\r\nnext"]
            ),
            ["reg_path"] = new(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows",
                @"HKEY_LOCAL_MACHINE\NOT_A_HIVE\Microsoft"
            ),
            ["b64"] = new(["token=SGVsbG8="], ["token=abcd", "token=SGVsbG9="]),
            ["bitlocker"] = new(
                ["key=001155-002310-003465-004620-005775-006930-008085-009240"],
                [
                    "key=001155-002310-003465-004620-005775-006930-008085",
                    "key=001156-002310-003465-004620-005775-006930-008085-009240",
                ]
            ),
            ["bitcoin"] = new(
                ["wallet=1BoatSLRHtKNngkdXEeobR76b53LETtpyT"],
                ["wallet=1" + new string('O', 25), "wallet=1BoatSLRHtKNngkdXEeobR76b53LETtpyU"]
            ),
            ["bitcoin_segwit"] = new(
                ["wallet=BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4"],
                ["wallet=bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5"]
            ),
            ["tron"] = new(
                ["wallet=TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t"],
                ["wallet=TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj61"]
            ),
            ["solana"] = new(
                ["wallet=11111111111111111111111111111111"],
                ["wallet=" + new string('1', 31)]
            ),
            ["xrp"] = new(
                ["wallet=rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh"],
                ["wallet=rHb9CJAWyB4rj91VRWn96DkukG4bwdtyT1"]
            ),
            ["dogecoin"] = new(
                ["wallet=D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H1"],
                ["wallet=D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H2"]
            ),
            ["zcash"] = new(
                [
                    "wallet=t1Hxw6JqWMnhDK5jRCieg5bFHM2qt7UtQvu",
                    "wallet=zs1qqqsyqcyq5rqwzqfpg9scrgwpugpzysnzs23v9ccrydpk8qarc0jqgfzyvjz2f389q5j5ctfvp5",
                ],
                ["wallet=t1Hxw6JqWMnhDK5jRCieg5bFHM2qt7UtQv1"]
            ),
            ["cardano"] = new(
                ["wallet=addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl8"],
                ["wallet=addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl1"]
            ),
            ["stellar"] = new(
                ["wallet=GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFN"],
                ["wallet=GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFA"]
            ),
            ["bitcoin_cash"] = new(
                ["wallet=bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy"],
                ["wallet=bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyq"]
            ),
            ["ton"] = new(
                ["wallet=EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHF"],
                ["wallet=EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHA"]
            ),
            ["litecoin"] = new(
                [
                    "wallet=LKKHMBjCU89fyFNgSRprDoD8Jb25N8uWvd",
                    "wallet=ltc1qqypqxpq9qcrsszg2pvxq6rs0zqg3yyc5dyg36p",
                ],
                ["wallet=LKKHMBjCU89fyFNgSRprDoD8Jb25N8uWv1"]
            ),
            ["avalanche"] = new(
                ["wallet=X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlp"],
                ["wallet=X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlq"]
            ),
            ["move_address"] = new(
                ["wallet=0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"],
                ["wallet=0x0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef"]
            ),
            ["near"] = new(["wallet=alice.sub.near"], ["wallet=alice..sub.near"]),
            ["bittensor"] = new(
                ["wallet=5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRzV"],
                ["wallet=5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRz1"]
            ),
            ["hedera"] = new(["wallet=0.0.123-vfmkw"], ["wallet=0.0.123-abcde"]),
            ["canton_party"] = new(
                ["wallet=Alice::1220f2fe29866fd6a0009ecc8a64ccdc09f1958bd0f801166baaee469d1251b2eb72"],
                ["wallet=Alice::1320f2fe29866fd6a0009ecc8a64ccdc09f1958bd0f801166baaee469d1251b2eb72"]
            ),
            ["provenance_scope"] = new(
                ["wallet=scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkwel"],
                ["wallet=scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkw1"]
            ),
            ["aeon"] = new(
                ["WmsSWgtT1JPg5e3cK41hKXSHVpKW7e47bjgiKmWZkYrhSS5LhRemNyqayaSBtAQ6517eo5PtH9wxHVmM78JDZSUu2W8PqRiNs"],
                [
                    "WmS" + new string('A', 94),
                    "WmsSWgtT1JPg5e3cK41hKXSHVpKW7e47bjgiKmWZkYrhSS5LhRemNyqayaSBtAQ6517eo5PtH9wxHVmM78JDZSUu2W8PqRiN1",
                ]
            ),
            ["bytecoin"] = new(
                ["2AaF4qEmER6dNeM6dfiBFL7kqund3HYGvMBF3ttsNd9SfzgYB6L7ep1Yg1osYJzLdaKAYSLVh6e6jKnAuzj3bw1oGyd1x7Z"],
                [
                    "2O" + new string('A', 93),
                    "2AaF4qEmER6dNeM6dfiBFL7kqund3HYGvMBF3ttsNd9SfzgYB6L7ep1Yg1osYJzLdaKAYSLVh6e6jKnAuzj3bw1oGyd1x71",
                ]
            ),
            ["dashcoin"] = new(
                ["D3XeV6X3otr2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8EmnBXn"],
                [
                    "d" + new string('A', 94),
                    "D3XeV6X3otr2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8EmnBX1",
                ]
            ),
            ["dashcoin2"] = new(
                ["Xgtyuk76vhuFW2iT7UAiHgNdWXCf3J34wh"],
                ["x" + new string('A', 33), "Xgtyuk76vhuFW2iT7UAiHgNdWXCf3J34wi"]
            ),
            ["fantomcoin"] = new(
                ["6gt5xRQJhjC2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8KzEdBp"],
                [
                    "6" + new string('O', 94),
                    "6gt5xRQJhjC2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8KzEdB1",
                ]
            ),
            ["monero"] = new(
                ["4AdUndXHHZ6cfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2684Rge"],
                [
                    "4O" + new string('A', 93),
                    "4AdUndXHHZ6cfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2684Rg1",
                ]
            ),
            ["sumokoin"] = new(
                ["Sumoo72D2v7KEGvfPzGH5qC5VHGnLmafaAhoMooPwRALNwm2oSyK3myTaFefvyg5bviMbBXUFWN8McswTRowHNYXfo34VD9oWr7"],
                [
                    "sumoo" + new string('A', 94),
                    "Sumoo72D2v7KEGvfPzGH5qC5VHGnLmafaAhoMooPwRALNwm2oSyK3myTaFefvyg5bviMbBXUFWN8McswTRowHNYXfo34VD9oWr1",
                ]
            ),
            ["cve"] = new(
                ["fixed cve-2026-1234", "CVE-2026-" + new string('1', 19)],
                [
                    "CVE-2026-123",
                    "CVE-2026-" + new string('1', 20),
                    "XCVE-2026-1234",
                ]
            ),
            ["pem_private_key"] = new(
                [
                    "-----BEGIN PRIVATE KEY-----",
                    "-----BEGIN ENCRYPTED PRIVATE KEY-----",
                ],
                [
                    "-----begin private key-----",
                    "-----BEGIN RSA PRIVATE KEY-----",
                    "------BEGIN PRIVATE KEY-----",
                    "-----BEGIN PRIVATE KEY------",
                    "-----END PRIVATE KEY-----",
                ]
            ),
            ["onion_v3"] = new(
                [
                    "pg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion",
                    "PG6MMJIYJMCRSSLVYKFWNNTLARU7P5SVN6Y2YMMJU6NUBXNDF4PSCRYD.ONION:443",
                ],
                [
                    new string('a', 55) + ".onion",
                    "8" + new string('a', 56) + ".onion",
                    new string('a', 56) + ".onion.com",
                    new string('a', 56) + ".onion-evil",
                    "qg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion",
                ]
            ),
            ["ethereum"] = new(
                [
                    "to=0x5e97870f263700f46aa00d967821199b9bc5a120",
                    "0x5E97870F263700F46AA00D967821199B9BC5A120",
                ],
                [
                    "to=0x5e97870f263700f46aa00d967821199b9bc5a12",
                    "g0x5e97870f263700f46aa00d967821199b9bc5a120z",
                    "to=0x5AAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
                ]
            ),
            ["sha256"] = new(
                ["sha256=" + new string('a', 64), new string('F', 64) + ".bin"],
                [new string('a', 63), new string('a', 65), "g" + new string('a', 64) + "z"]
            ),
        };

    [Fact]
    public void Catalog_HasOneDescriptionPatternSourceAndCorpusEntryPerName()
    {
        Assert.Equal(BuiltInPatternCatalog.Definitions.Count, BuiltInPatternCatalog.ByName.Count);
        Assert.Equal(BuiltInPatternCatalog.Definitions.Count, BuiltInPatternCatalog.Patterns.Count);
        Assert.Equal(
            BuiltInPatternCatalog.Definitions.Count,
            BuiltInPatternCatalog.Descriptions.Count
        );
        Assert.Equal(BuiltInPatternCatalog.Definitions.Count, Corpus.Count);

        foreach (var definition in BuiltInPatternCatalog.Definitions)
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.Description));
            Assert.True(Uri.TryCreate(definition.Source, UriKind.Absolute, out _));
            Assert.True(Corpus.ContainsKey(definition.Name));
        }
    }

    [Fact]
    public void EveryBuiltIn_CompilesAndTheAuthoritativePipelineAcceptsPositivesRejectsNegatives()
    {
        foreach (var definition in BuiltInPatternCatalog.Definitions)
        {
            var regex = RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern);
            var corpus = Corpus[definition.Name];

            foreach (var positive in corpus.Positives)
            {
                Assert.True(
                    regex.IsMatch(positive),
                    $"{definition.Name} missed positive witness: {positive}"
                );
                Assert.NotEmpty(
                    RegexOutputCore.CreateRecords(
                        new ParsedHit(positive, positive, string.Empty),
                        definition.Name,
                        regex,
                        regexOutput: true,
                        sourceFile: "corpus.bin",
                        patternType: "Regex"
                    )
                );
            }
            foreach (var negative in corpus.Negatives)
            {
                Assert.Empty(
                    RegexOutputCore.CreateRecords(
                        new ParsedHit(negative, negative, string.Empty),
                        definition.Name,
                        regex,
                        regexOutput: true,
                        sourceFile: "corpus.bin",
                        patternType: "Regex"
                    )
                );
            }
            Assert.False(regex.IsMatch(string.Empty), $"{definition.Name} matches the empty string");
            Assert.Equal(definition.UseNonBacktracking, regex.Options.HasFlag(RegexOptions.NonBacktracking));
        }
    }

    [Fact]
    public void EveryRapidsSuperset_AcceptsTheAuthoritativePositiveWitness()
    {
        foreach (
            var definition in BuiltInPatternCatalog.Definitions.Where(definition =>
                definition.RapidsSupersetPattern is not null
            )
        )
        {
            var rapidsCandidate = new Regex(
                definition.RapidsSupersetPattern!,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)
            );

            foreach (var positive in Corpus[definition.Name].Positives)
            {
                Assert.True(
                    rapidsCandidate.IsMatch(positive),
                    $"{definition.Name} RAPIDS prefilter missed authoritative positive: {positive}"
                );
            }
        }
    }

    [Fact]
    public void EveryRapidsSuperset_PassesTheDocumentedLibcudfSyntaxGate()
    {
        foreach (
            var definition in BuiltInPatternCatalog.Definitions.Where(definition =>
                definition.RapidsSupersetPattern is not null
            )
        )
        {
            Assert.True(
                RapidsRegexPolicy.IsSupportedSuperset(
                    definition.RapidsSupersetPattern!,
                    out var reason
                ),
                $"{definition.Name}: {reason}"
            );
        }
    }

    [Fact]
    public void VarSetRapidsSuperset_AcceptsAnOffsetPrefixedAuthoritativePositive()
    {
        var definition = BuiltInPatternCatalog.ByName["var_set"];
        var rapidsCandidate = new Regex(
            definition.RapidsSupersetPattern!,
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1)
        );
        var parsed = RegexOutputCore.ParseHit(
            "0x201\tProgramFiles(x86)=C:\\Program Files (x86)",
            includeOffset: true
        );
        var authoritative = RegexOutputCore.GetOrCreateRegex(
            definition.Name,
            definition.Pattern
        );

        Assert.Matches(authoritative, parsed.Data);
        Assert.Matches(
            rapidsCandidate,
            "0x201\tProgramFiles(x86)=C:\\Program Files (x86)"
        );
    }

    [Fact]
    public void BacktrackingBuiltIns_HaveBoundedOrLinearLargeInputHandling()
    {
        var specializedLinearMatchers = new HashSet<string>(
            ["b64", "xml"],
            StringComparer.OrdinalIgnoreCase
        );

        foreach (
            var definition in BuiltInPatternCatalog.Definitions.Where(
                definition => !definition.UseNonBacktracking
            )
        )
        {
            Assert.True(
                definition.BoundedRetryOverlap is > 0
                    || specializedLinearMatchers.Contains(definition.Name),
                $"{definition.Name} has no safe large-input timeout strategy"
            );
        }
    }

    [Theory]
    [InlineData(@"(?<!x)y")]
    [InlineData(@"(?<name>x)")]
    [InlineData(@"(a)\1")]
    [InlineData(@"(?i)secret")]
    [InlineData(@"a{1,1000}")]
    public void RapidsSyntaxGate_RejectsUnsupportedOrUnboundedFeatures(string pattern)
    {
        Assert.False(RapidsRegexPolicy.IsSupportedSuperset(pattern, out _));
    }

    [Fact]
    public void CustomRegex_IsNeverSilentlySentToRapids()
    {
        Assert.False(
            RegexOutputCore.TryGetRapidsSupersetPattern(
                "custom",
                "[A-Z]+",
                out _
            )
        );
    }

    [Fact]
    public void RapidsPartition_KeepsCustomAndIneligiblePatternsOnCpu()
    {
        var requested = new List<(string name, string pattern)>
        {
            ("guid", BuiltInPatternCatalog.Patterns["guid"]),
            ("url3986", BuiltInPatternCatalog.Patterns["url3986"]),
            ("custom", "(?i)secret"),
        };

        var (gpu, cpu) = RapidsRegexPolicy.PartitionPatterns(requested);

        Assert.Collection(gpu, pattern => Assert.Equal("guid", pattern.name));
        Assert.Equal(["url3986", "custom"], cpu.Select(pattern => pattern.name));
    }

    [Fact]
    public void CryptoBase58Patterns_RemainCaseSensitiveAndRejectExcludedCharacters()
    {
        var bitcoin = RegexOutputCore.GetOrCreateRegex(
            "bitcoin",
            BuiltInPatternCatalog.Patterns["bitcoin"]
        );

        Assert.False(bitcoin.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.DoesNotMatch(bitcoin, "1" + new string('I', 25));
        Assert.DoesNotMatch(bitcoin, "1" + new string('O', 25));
        Assert.DoesNotMatch(bitcoin, "1" + new string('l', 25));
    }

    [Fact]
    public void UserRegex_UsesNormalDotNetSemanticsUnlessCallerRequestsOptions()
    {
        var literal = RegexOutputCore.GetOrCreateRegex("custom", "foo #bar");
        var freeSpacing = RegexOutputCore.GetOrCreateRegex("custom", "(?x)foo #bar");
        var caseInsensitive = RegexOutputCore.GetOrCreateRegex("custom", "(?i)abc");

        Assert.Matches(literal, "foo #bar");
        Assert.DoesNotMatch(literal, "FOO #BAR");
        Assert.DoesNotMatch(literal, "foo");
        Assert.Matches(freeSpacing, "foo");
        Assert.Matches(caseInsensitive, "ABC");
        Assert.False(literal.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.False(literal.Options.HasFlag(RegexOptions.IgnorePatternWhitespace));
    }

    [Fact]
    public void NearValidLongEmail_CompletesWithoutBacktrackingTimeout()
    {
        var email = RegexOutputCore.GetOrCreateRegex(
            "email",
            BuiltInPatternCatalog.Patterns["email"]
        );
        var nearValid = string.Concat(
            Enumerable.Repeat("a.", 12_000)
        ) + "example@invalid-domain";

        Assert.DoesNotMatch(email, nearValid);
        Assert.False(email.Options.HasFlag(RegexOptions.NonBacktracking));
    }

    [Fact]
    public void NamedPipe_DoesNotBacktrackIntoAPrefixBeforeAnotherSeparator()
    {
        var namedPipe = RegexOutputCore.GetOrCreateRegex(
            "named_pipe",
            BuiltInPatternCatalog.Patterns["named_pipe"]
        );

        Assert.False(namedPipe.Match(@"open \\.\pipe\svc\extra").Success);
        Assert.Equal(
            @"\\.\pipe\svc-control",
            namedPipe.Match(@"open \\.\pipe\svc-control").Value
        );
        Assert.Equal(
            @"\\.\pipe\svc-control",
            namedPipe.Match(@"open \\.\pipe\svc-control --out C:\temp\x").Value
        );
        Assert.Equal(
            @"\\.\pipe\service control",
            namedPipe.Match("\"\\\\.\\pipe\\service control\"").Value
        );
        Assert.Equal(
            @"\\.\pipe\standalone service",
            namedPipe.Match(@"\\.\pipe\standalone service").Value
        );
    }

    [Fact]
    public void Sid_DoesNotTruncateAnOverlongSubauthorityList()
    {
        var sid = RegexOutputCore.GetOrCreateRegex(
            "sid",
            BuiltInPatternCatalog.Patterns["sid"]
        );
        var overlong = "S-1-5" + string.Concat(Enumerable.Repeat("-1", 16));

        Assert.False(sid.Match(overlong).Success);
    }

    [Fact]
    public void Mac_DoesNotTruncateALongerDelimitedHardwareAddress()
    {
        var mac = RegexOutputCore.GetOrCreateRegex(
            "mac",
            BuiltInPatternCatalog.Patterns["mac"]
        );

        Assert.False(mac.Match("00:11:22:33:44:55:66:77").Success);
    }

    [Fact]
    public void BitLocker_DoesNotMatchAnyEightGroupWindowOfNineGroups()
    {
        var bitLocker = RegexOutputCore.GetOrCreateRegex(
            "bitlocker",
            BuiltInPatternCatalog.Patterns["bitlocker"]
        );
        var overlong = string.Join("-", Enumerable.Repeat("123456", 9));

        Assert.False(bitLocker.Match(overlong).Success);
    }

    [Fact]
    public void MoneroIntegratedAddress_IsReturnedWholeInsteadOfAsAStandardAddressPrefix()
    {
        var monero = RegexOutputCore.GetOrCreateRegex(
            "monero",
            BuiltInPatternCatalog.Patterns["monero"]
        );
        var integrated = "4" + new string('A', 105);

        var match = monero.Match(integrated);

        Assert.True(match.Success);
        Assert.Equal(106, match.Length);
        Assert.Equal(integrated, match.Value);
    }

    [Theory]
    [InlineData("4000000000000000006")]
    [InlineData("2221000000000009")]
    public void PaymentCardCandidate_AcceptsCurrentThirteenToNineteenDigitShapes(string value)
    {
        var paymentCard = RegexOutputCore.GetOrCreateRegex(
            "cc",
            BuiltInPatternCatalog.Patterns["cc"]
        );

        Assert.Matches(paymentCard, value);
    }

    [Fact]
    public void PaymentCardCandidate_DoesNotTruncateALongerSeparatedNumber()
    {
        var paymentCard = RegexOutputCore.GetOrCreateRegex(
            "cc",
            BuiltInPatternCatalog.Patterns["cc"]
        );
        var twentyDigits = string.Join(" ", Enumerable.Repeat("1", 20));

        Assert.DoesNotMatch(paymentCard, twentyDigits);
    }

    [Fact]
    public void Sid_AcceptsHexadecimalIdentifierAuthority()
    {
        var sid = RegexOutputCore.GetOrCreateRegex("sid", BuiltInPatternCatalog.Patterns["sid"]);

        Assert.Matches(sid, "S-1-0x28651FE848-12-72-9-110");
    }

    [Fact]
    public void EmbeddedCandidates_EmitTheCompleteIntendedValue()
    {
        var cases = new[]
        {
            (
                Name: "url3986",
                Input: "visit https://user:pass@example.com/path now",
                Expected: "https://user:pass@example.com/path"
            ),
            (
                Name: "email",
                Input: "mail !foo@example.com now",
                Expected: "!foo@example.com"
            ),
            (
                Name: "win_path",
                Input: "open \"C:\\folder one\\file.txt\" now",
                Expected: "\"C:\\folder one\\file.txt\""
            ),
            (
                Name: "unc",
                Input: "copy \\\\server\\share\\file.txt now",
                Expected: "\\\\server\\share\\file.txt"
            ),
        };

        foreach (var testCase in cases)
        {
            var regex = RegexOutputCore.GetOrCreateRegex(
                testCase.Name,
                BuiltInPatternCatalog.Patterns[testCase.Name]
            );
            var records = RegexOutputCore
                .CreateRecords(
                    new ParsedHit(testCase.Input, testCase.Input, string.Empty),
                    testCase.Name,
                    regex,
                    regexOutput: true,
                    sourceFile: "sample.bin",
                    patternType: "Regex"
                )
                .ToList();

            Assert.Equal(testCase.Expected, Assert.Single(records).DataFound);
        }
    }

    [Fact]
    public void Url3986_GeneratedRegexUsesTheBoundedShortInputPolicy()
    {
        var definition = BuiltInPatternCatalog.ByName["url3986"];
        var longInputRegex = RegexOutputCore.GetOrCreateRegex(
            definition.Name,
            definition.Pattern
        );

        Assert.Equal(
            BuiltInPatternCatalog.Url3986GeneratedInputLimit,
            definition.GeneratedShortInputLimit
        );
        Assert.True(longInputRegex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.True(
            RegexOutputCore.TryGetGeneratedShortInputRegex(
                definition,
                new string('a', BuiltInPatternCatalog.Url3986GeneratedInputLimit),
                out var shortInputRegex
            )
        );
        Assert.False(shortInputRegex.Options.HasFlag(RegexOptions.NonBacktracking));
        Assert.True(shortInputRegex.Options.HasFlag(RegexOptions.IgnorePatternWhitespace));
        Assert.Equal(RegexOutputCore.ShortInputMatchTimeout, shortInputRegex.MatchTimeout);
        Assert.False(
            RegexOutputCore.TryGetGeneratedShortInputRegex(
                definition,
                new string('a', BuiltInPatternCatalog.Url3986GeneratedInputLimit + 1),
                out _
            )
        );
    }

    [Fact]
    public void Url3986_GeneratedRegexMatchesLinearEngineOnReducedAlphabet()
    {
        var definition = BuiltInPatternCatalog.ByName["url3986"];
        var linearRegex = RegexOutputCore.GetOrCreateRegex(
            definition.Name,
            definition.Pattern
        );
        Assert.True(
            RegexOutputCore.TryGetGeneratedShortInputRegex(
                definition,
                string.Empty,
                out var compiledRegex
            )
        );

        foreach (var input in GenerateStrings(['a', ':', '/'], maximumLength: 8))
        {
            var expectedValues = linearRegex
                .Matches(input)
                .Select(match => match.Groups[definition.OutputGroup!].Value)
                .ToList();
            var actualValues = RegexOutputCore.GetUrlValuesWithFallback(
                input,
                compiledRegex,
                linearRegex
            );
            Assert.Equal(expectedValues, actualValues);
        }
    }

    [Fact]
    public void Url3986_GeneratedRegexMatchesLinearEngineAcrossUrlGrammarCorpus()
    {
        var definition = BuiltInPatternCatalog.ByName["url3986"];
        var linearRegex = RegexOutputCore.GetOrCreateRegex(
            definition.Name,
            definition.Pattern
        );
        Assert.True(
            RegexOutputCore.TryGetGeneratedShortInputRegex(
                definition,
                string.Empty,
                out var generatedRegex
            )
        );
        foreach (var input in GenerateUrlParityCorpus())
        {
            Assert.InRange(input.Length, 0, definition.GeneratedShortInputLimit!.Value);
            var expectedValues = linearRegex
                .Matches(input)
                .Select(match => match.Groups[definition.OutputGroup!].Value)
                .ToList();
            var actualValues = RegexOutputCore.GetUrlValuesWithFallback(
                input,
                generatedRegex,
                linearRegex
            );
            Assert.Equal(expectedValues, actualValues);
        }
    }

    [Fact]
    public void ShortInputUrlMatcher_TimeoutReplaysWithLinearEngineBeforeReturningValues()
    {
        const string catastrophicPattern = "^(a|aa)+$";
        var preferredRegex = new Regex(
            catastrophicPattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(1)
        );
        var definition = BuiltInPatternCatalog.ByName["url3986"];
        var fallbackRegex = RegexOutputCore.GetOrCreateRegex(
            definition.Name,
            definition.Pattern
        );
        var fallbackCountBefore = RegexOutputCore.ShortInputFallbackCount;
        var input = new string('a', 100_000) + "! https://example.test/fallback";

        var values = RegexOutputCore.GetUrlValuesWithFallback(
            input,
            preferredRegex,
            fallbackRegex
        );

        Assert.Equal(["https://example.test/fallback"], values);
        Assert.True(RegexOutputCore.ShortInputFallbackCount > fallbackCountBefore);
    }

    [Theory]
    [InlineData(BuiltInPatternCatalog.Url3986GeneratedInputLimit - 1)]
    [InlineData(BuiltInPatternCatalog.Url3986GeneratedInputLimit)]
    [InlineData(BuiltInPatternCatalog.Url3986GeneratedInputLimit + 1)]
    public void Url3986_AdaptiveRecordsPreserveOrderedValuesAtThreshold(int inputLength)
    {
        const string prefix = "prefix https://example.test/";
        var input = prefix + new string('a', inputLength - prefix.Length);
        var definition = BuiltInPatternCatalog.ByName["url3986"];
        var linearRegex = RegexOutputCore.GetOrCreateRegex(
            definition.Name,
            definition.Pattern
        );
        var expected = linearRegex
            .Matches(input)
            .Select(match => match.Groups[definition.OutputGroup!].Value)
            .ToList();

        var actual = RegexOutputCore
            .CreateRecords(
                new ParsedHit(input, input, string.Empty),
                definition.Name,
                linearRegex,
                regexOutput: true,
                sourceFile: "sample.bin",
                patternType: "Regex"
            )
            .Select(record => record.DataFound)
            .ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LinearBase64Matcher_PreservesRegexResultsAcrossRandomInputs()
    {
        const string alphabet =
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=!?.: ";
        var random = new Random(0xB64);
        var regex = RegexOutputCore.GetOrCreateRegex(
            "b64",
            BuiltInPatternCatalog.Patterns["b64"]
        );

        for (var sample = 0; sample < 2_000; sample++)
        {
            var length = random.Next(0, 160);
            var input = new string(
                Enumerable
                    .Range(0, length)
                    .Select(_ => alphabet[random.Next(alphabet.Length)])
                    .ToArray()
            );
            var definition = BuiltInPatternCatalog.ByName["b64"];
            var expected = regex
                .Matches(input)
                .Where(match => BuiltInSemanticValidator.IsValid(definition, match.Value))
                .Select(match => match.Value)
                .ToList();
            var actual = RegexOutputCore
                .CreateRecords(
                    new ParsedHit(input, input, string.Empty),
                    "b64",
                    regex,
                    regexOutput: true,
                    sourceFile: "sample.bin",
                    patternType: "Regex"
                )
                .Select(record => record.DataFound)
                .ToList();

            Assert.True(
                expected.SequenceEqual(actual),
                $"Input: {input}\nExpected: {string.Join(" | ", expected)}\n"
                    + $"Actual: {string.Join(" | ", actual)}"
            );
        }
    }

    [Fact]
    public void LinearBase64Matcher_HandlesVeryLargeToken()
    {
        var input = new string('A', 20 * 1024 * 1024);
        var regex = RegexOutputCore.GetOrCreateRegex(
            "b64",
            BuiltInPatternCatalog.Patterns["b64"]
        );
        var record = Assert.Single(
            RegexOutputCore.CreateRecords(
                new ParsedHit(input, input, string.Empty),
                "b64",
                regex,
                regexOutput: true,
                sourceFile: "sample.bin",
                patternType: "Regex"
            )
        );

        Assert.Equal(input.Length, record.DataFound.Length);
    }

    [Fact]
    public void LinearXmlMatcher_PreservesSimpleElementRegexSemantics()
    {
        var regex = RegexOutputCore.GetOrCreateRegex(
            "xml",
            BuiltInPatternCatalog.Patterns["xml"]
        );
        var inputs = new[]
        {
            "<Root>value</Root>",
            "<Root id=\"1\">value</Root>",
            "<R1></R1>",
            "<Root>one</Root><Root>two</Root>",
            "<Root>value</root>",
            "<Root>line\nnext</Root>",
            "<Root_>value</Root_>",
            "<1Root>value</1Root>",
            "<Root>",
            string.Empty,
        };

        foreach (var input in inputs)
        {
            Assert.Equal(
                regex.IsMatch(input),
                RegexOutputCore.IsSimpleXmlElementMatch(input)
            );
        }
    }

    [Fact]
    public void LinearXmlMatcher_HandlesVeryLargeElement()
    {
        var value = "<Root id=\"1\">" + new string('x', 20 * 1024 * 1024) + "</Root>";

        Assert.True(RegexOutputCore.IsSimpleXmlElementMatch(value));
    }

    [Fact]
    public void LegacyWalletCandidates_DoNotEmitPrefixesOfLongerBase58Tokens()
    {
        var longerTokens = new Dictionary<string, string>
        {
            ["aeon"] = "Wms" + new string('A', 95),
            ["bytecoin"] = "2A" + new string('A', 94),
            ["dashcoin"] = "D" + new string('A', 95),
            ["dashcoin2"] = "X" + new string('A', 34),
            ["fantomcoin"] = "6" + new string('A', 95),
            ["sumokoin"] = "Sumoo" + new string('A', 95),
        };

        foreach (var (name, value) in longerTokens)
        {
            var regex = RegexOutputCore.GetOrCreateRegex(
                name,
                BuiltInPatternCatalog.Patterns[name]
            );
            Assert.DoesNotMatch(regex, value);
        }
    }

    [Theory]
    [InlineData(9_999, 1, 22, (int)RegexWorkPartition.Patterns)]
    [InlineData(10_000, 1, 22, (int)RegexWorkPartition.Hits)]
    [InlineData(50_000, 5, 22, (int)RegexWorkPartition.Hits)]
    [InlineData(2_000_000, 22, 22, (int)RegexWorkPartition.Patterns)]
    [InlineData(2_000_000, 23, 22, (int)RegexWorkPartition.Patterns)]
    [InlineData(2_000_000, 1, 1, (int)RegexWorkPartition.Patterns)]
    public void RegexParallelismPolicy_SelectsMeasuredTopology(
        int hitCount,
        int patternCount,
        int processorCount,
        int expected
    )
    {
        Assert.Equal(
            (RegexWorkPartition)expected,
            RegexParallelismPolicy.Choose(hitCount, patternCount, processorCount)
        );
    }

    private static IEnumerable<string> GenerateStrings(
        IReadOnlyList<char> alphabet,
        int maximumLength
    )
    {
        yield return string.Empty;

        for (var length = 1; length <= maximumLength; length++)
        {
            var count = (int)Math.Pow(alphabet.Count, length);
            for (var value = 0; value < count; value++)
            {
                var remaining = value;
                var characters = new char[length];
                for (var index = length - 1; index >= 0; index--)
                {
                    characters[index] = alphabet[remaining % alphabet.Count];
                    remaining /= alphabet.Count;
                }

                yield return new string(characters);
            }
        }
    }

    private static IEnumerable<string> GenerateUrlParityCorpus()
    {
        string[] prefixes = ["", "prefix ", "(", "\u2603 ", "a."];
        string[] schemes = ["http", "https", "ftp", "git+ssh", "x"];
        string[] users = ["", "user@", "user:pass@", "u%20s@"];
        string[] hosts = [
            "example.test",
            "sub-domain.example.test",
            "127.0.0.1",
            "[2001:db8::1]",
            "[v1.alpha]",
            "bad host",
        ];
        string[] ports = ["", ":443", ":0", ":not-a-port"];
        string[] paths = ["", "/", "/one/two", "/a%20b", "/:@!$&'()*+,;="];
        string[] queries = ["", "?a=b", "?next=https://other.test/x", "?q=%20"];
        string[] fragments = ["", "#part", "#a/b?c"];
        string[] suffixes = ["", " suffix", ")", "\r\n", " https://second.test/z"];
        var random = new Random(3986);

        yield return string.Empty;
        yield return "not a URL";
        yield return "https://example.test/one https://second.test/two";

        for (var index = 0; index < 2_000; index++)
        {
            var input =
                prefixes[random.Next(prefixes.Length)]
                + schemes[random.Next(schemes.Length)]
                + "://"
                + users[random.Next(users.Length)]
                + hosts[random.Next(hosts.Length)]
                + ports[random.Next(ports.Length)]
                + paths[random.Next(paths.Length)]
                + queries[random.Next(queries.Length)]
                + fragments[random.Next(fragments.Length)]
                + suffixes[random.Next(suffixes.Length)];

            yield return input;
        }
    }

}
