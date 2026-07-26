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
            ["cc"] = new("card 4111 1111 1111 1111 end", "card 4111 1111 end"),
            ["ipv4"] = new("peer=192.168.1.250:443", "peer=1.2.3.4.5"),
            ["ipv6"] = new("peer ::ffff:192.0.2.128 active", "peer 2001:::1"),
            ["email"] = new(
                [
                    "mail user.name+tag@example.technology now",
                    "mail #@example.com now",
                    "mail !foo@example.com now",
                ],
                ["mail user@-example.com"]
            ),
            ["zip"] = new("Sydney mirror 90210-1234 ready", "code 1234 ready"),
            ["urlUser"] = new(
                "proxy=https://analyst:secret@example.com/path",
                "https://example.com/path"
            ),
            ["url3986"] = new(
                "visit https://user@example.com:8443/a//b?x=1#fragment now",
                "not a url"
            ),
            ["xml"] = new("<Root id=\"1\">value</Root>", "<Root>value</root>"),
            ["sid"] = new("owner=S-1-5-21-1-2-3-1001;", "owner=S-1-x-21"),
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
            ["b64"] = new("token=SGVsbG8=", "token=abcd"),
            ["bitlocker"] = new(
                "key=123456-234567-345678-456789-567890-678901-789012-890123",
                "key=123456-234567-345678-456789-567890-678901-789012"
            ),
            ["bitcoin"] = new("wallet=1" + new string('A', 25), "wallet=1" + new string('O', 25)),
            ["aeon"] = new("Wms" + new string('A', 94), "WmS" + new string('A', 94)),
            ["bytecoin"] = new("2A" + new string('A', 93), "2O" + new string('A', 93)),
            ["dashcoin"] = new("D" + new string('A', 94), "d" + new string('A', 94)),
            ["dashcoin2"] = new("X" + new string('A', 33), "x" + new string('A', 33)),
            ["fantomcoin"] = new("6" + new string('A', 94), "6" + new string('O', 94)),
            ["monero"] = new("8" + new string('A', 94), "4O" + new string('A', 93)),
            ["sumokoin"] = new("Sumoo" + new string('A', 94), "sumoo" + new string('A', 94)),
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
                [new string('a', 56) + ".onion", new string('A', 56) + ".ONION:443"],
                [
                    new string('a', 55) + ".onion",
                    "8" + new string('a', 56) + ".onion",
                    new string('a', 56) + ".onion.com",
                    new string('a', 56) + ".onion-evil",
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
    public void EveryBuiltIn_CompilesMatchesPositiveRejectsNegativeAndDoesNotMatchEmpty()
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
            }
            foreach (var negative in corpus.Negatives)
            {
                Assert.False(
                    regex.IsMatch(negative),
                    $"{definition.Name} accepted negative witness: {negative}"
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
            var expected = regex.Matches(input).Select(match => match.Value).ToList();
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
}
