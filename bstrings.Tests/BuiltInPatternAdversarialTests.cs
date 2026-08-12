using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;

namespace bstrings.Tests;

public sealed class BuiltInPatternAdversarialTests
{
    private static readonly string SyntheticLei = CreateLei("TEST00ONLY00000000");
    private static readonly string SyntheticNpi = CreateNpi("199999998");

    private static readonly string[] ExpectedNewPatternNames =
    [
        "cpe23",
        "tlp_marking",
        "email_message_id",
        "lei",
        "npi",
        "itin",
        "uk_nino",
        "md5_labelled",
        "sha1_labelled",
        "sha384_labelled",
        "sha512_labelled",
    ];

    public static TheoryData<string, string, int> LabelledDigestCases =>
        new()
        {
            { "md5_labelled", "MD5", 32 },
            { "sha1_labelled", "SHA-1", 40 },
            { "sha384_labelled", "SHA-384", 96 },
            { "sha512_labelled", "SHA-512", 128 },
        };

    [Fact]
    public void Adr0009_CatalogContainsTheIntendedNames()
    {
        foreach (var name in ExpectedNewPatternNames)
        {
            Assert.True(
                BuiltInPatternCatalog.ByName.ContainsKey(name),
                $"ADR-0009 built-in is missing: {name}"
            );
        }
    }

    [Fact]
    public void Cpe23_AcceptsExactlyElevenBoundedComponentsAndEscapes()
    {
        var simple = BuildCpe(
            "a",
            "synthetic-vendor",
            "synthetic-product",
            "1.0",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*"
        );
        var escaped = BuildCpe(
            "a",
            @"synthetic\:vendor",
            @"product\:edition",
            "1.0",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*"
        );

        AssertAccepted("cpe23", simple, simple);
        AssertAccepted("cpe23", escaped, escaped);
    }

    [Fact]
    public void Cpe23_RejectsComponentCountTrailingEscapeAndOversize()
    {
        var tenComponents = BuildCpe(
            "a",
            "vendor",
            "product",
            "1",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*"
        );
        var twelveComponents = BuildCpe(
            "a",
            "vendor",
            "product",
            "1",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "extra"
        );
        var trailingEscape = BuildCpe(
            "a",
            "vendor",
            "product",
            "1",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "broken\\"
        );
        var invalidPart = BuildCpe(
            "aa",
            "vendor",
            "product",
            "1",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*"
        );
        var oversizedComponent = BuildCpe(
            "a",
            new string('x', 4097),
            "product",
            "1",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*"
        );

        AssertRejected("cpe23", tenComponents);
        AssertRejected("cpe23", twelveComponents);
        AssertRejected("cpe23", trailingEscape);
        AssertRejected("cpe23", invalidPart);
        AssertRejected("cpe23", oversizedComponent);
    }

    [Fact]
    public void Cpe23_RejectsSixteenMiBUnterminatedEscapeFloodWithoutTimeout()
    {
        var hostile = "cpe:2.3:a:" + new string('\\', 16 * 1024 * 1024);

        AssertRejected("cpe23", hostile);
    }

    [Fact]
    public void TlpMarking_AcceptsOnlyTheExactVersionTwoEnums()
    {
        string[] accepted =
        [
            "TLP:RED",
            "TLP:AMBER",
            "TLP:AMBER+STRICT",
            "TLP:GREEN",
            "TLP:CLEAR",
        ];
        string[] rejected =
        [
            "TLP:WHITE",
            "tlp:red",
            "TLP\uFF1ARED",
            "TLP:AMBER + STRICT",
            "TLP:REDISH",
        ];

        foreach (var value in accepted)
        {
            AssertAccepted("tlp_marking", value, value);
        }
        foreach (var value in rejected)
        {
            AssertRejected("tlp_marking", value);
        }
    }

    [Fact]
    public void EmailMessageId_RequiresARealHeaderAndBalancedAngles()
    {
        const string identifier = "fixture.0001@example.invalid";
        var ordinary = AssertAccepted(
            "email_message_id",
            $"Message-ID: <{identifier}>"
        );
        var folded = AssertAccepted(
            "email_message_id",
            $"Message-ID:\r\n\t<{identifier}>"
        );

        foreach (var record in new[] { ordinary, folded })
        {
            Assert.Contains(identifier, record.DataFound, StringComparison.Ordinal);
            Assert.DoesNotContain("Message-ID", record.DataFound, StringComparison.OrdinalIgnoreCase);
        }

        string[] rejected =
        [
            $"<{identifier}>",
            $"Subject: quoted Message-ID: <{identifier}>",
            $"Message-ID: {identifier}",
            $"Message-ID: <{identifier}",
            $"Message-ID: {identifier}>",
            "Message-ID: <@example.invalid>",
            "Message-ID: <fixture@>",
            $"Message-ID:\r\n<{identifier}>",
        ];

        foreach (var value in rejected)
        {
            AssertRejected("email_message_id", value);
        }
    }

    [Fact]
    public void Lei_RequiresExactWidthAndMod97ButNoLabel()
    {
        AssertAccepted("lei", SyntheticLei, SyntheticLei);

        AssertRejected("lei", "LEI: " + SyntheticLei[..^1]);
        AssertRejected("lei", SyntheticLei[..^1]);
        AssertRejected("lei", SyntheticLei + "0");
        AssertRejected("lei", MutateFinalDigit(SyntheticLei));
        AssertRejected("lei", new string('0', 20));
        AssertRejected("lei", SyntheticLei.ToLowerInvariant());
    }

    [Fact]
    public void Npi_RequiresLabelWidthFirstDigitAndChecksum()
    {
        AssertAccepted("npi", "NPI: " + SyntheticNpi, SyntheticNpi);

        AssertRejected("npi", SyntheticNpi);
        AssertRejected("npi", "NPI: " + SyntheticNpi[..^1]);
        AssertRejected("npi", "NPI: " + SyntheticNpi + "0");
        AssertRejected("npi", "NPI: " + MutateFinalDigit(SyntheticNpi));
        AssertRejected("npi", "NPI: " + new string('0', 10));
        AssertRejected("npi", "NPI: " + CreateNpi("999999998"));
    }

    [Fact]
    public void Itin_RequiresLabelPublishedRangeAndExactWidth()
    {
        string[] accepted =
        [
            "900-50-1234",
            "900-65-1234",
            "900-70-1234",
            "900-88-1234",
            "900-90-1234",
            "900-92-1234",
            "900-94-1234",
            "900-99-1234",
        ];
        foreach (var value in accepted)
        {
            AssertAccepted("itin", "ITIN: " + value, value);
            AssertRejected("itin", value);
        }

        string[] rejected =
        [
            "900-49-1234",
            "900-66-1234",
            "900-69-1234",
            "900-89-1234",
            "900-93-1234",
            "900-70-123",
            "900-70-12345",
        ];
        foreach (var value in rejected)
        {
            AssertRejected("itin", "ITIN: " + value);
        }
    }

    [Fact]
    public void UkNino_RequiresLabelValidPrefixWidthAndSuffix()
    {
        const string valid = "AA000000A";
        AssertAccepted("uk_nino", "NINO: " + valid, valid);

        AssertRejected("uk_nino", valid);
        AssertRejected("uk_nino", "NINO: AB12345C");
        AssertRejected("uk_nino", "NINO: AB1234567C");
        AssertRejected("uk_nino", "NINO: BG123456A");
        AssertRejected("uk_nino", "NINO: DA123456A");
        AssertRejected("uk_nino", "NINO: AB123456E");
    }

    [Theory]
    [MemberData(nameof(LabelledDigestCases))]
    public void LabelledDigests_RequireExactLabelAndWidth(
        string patternName,
        string label,
        int width
    )
    {
        var digest = new string('A', width);

        AssertAccepted(patternName, label + ": " + digest, digest);
        AssertRejected(patternName, digest);
        AssertRejected(patternName, label + ": " + digest[..^1]);
        AssertRejected(patternName, label + ": " + digest + "A");
        AssertRejected(patternName, label + "\uFF1A " + digest);
        AssertRejected(patternName, label + ": " + digest[..^1] + "G");
    }

    [Fact]
    public void NewPatterns_RejectUnicodeLetterAdjacentSubstrings()
    {
        var cpe = BuildCpe(
            "a",
            "vendor",
            "product",
            "1",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*",
            "*"
        );
        var digest32 = new string('A', 32);
        var digest40 = new string('A', 40);
        var digest96 = new string('A', 96);
        var digest128 = new string('A', 128);
        var identifier = "fixture.0001@example.invalid";

        (string Name, string Input)[] cases =
        [
            ("cpe23", "\u00E9" + cpe),
            ("tlp_marking", "\u00E9TLP:RED\u00E9"),
            ("email_message_id", $"\u00E9Message-ID: <{identifier}>"),
            ("lei", "\u00E9" + SyntheticLei + "\u00E9"),
            ("npi", "\u00E9NPI: " + SyntheticNpi),
            ("itin", "\u00E9ITIN: 900-70-1234"),
            ("uk_nino", "\u00E9NINO: AA000000A"),
            ("md5_labelled", "\u00E9MD5: " + digest32),
            ("sha1_labelled", "\u00E9SHA-1: " + digest40),
            ("sha384_labelled", "\u00E9SHA-384: " + digest96),
            ("sha512_labelled", "\u00E9SHA-512: " + digest128),
        ];

        foreach (var (name, input) in cases)
        {
            AssertRejected(name, input);
        }
    }

    [Fact]
    public void Cve_RejectsTwentyDigitSequencesWithoutTruncatingToNineteen()
    {
        var nineteenDigits = "CVE-2026-" + new string('1', 19);
        var twentyDigits = "CVE-2026-" + new string('1', 20);

        AssertAccepted("cve", nineteenDigits, nineteenDigits);
        AssertRejected("cve", twentyDigits);
    }

    private static RegexOutputRecord AssertAccepted(
        string patternName,
        string input,
        string? expectedDataFound = null
    )
    {
        var (regex, records) = Evaluate(patternName, input);
        var record = Assert.Single(records);
        Assert.True(
            RegexOutputCore.IsMatch(patternName, regex, input),
            $"{patternName} record/boolean path parity failed for an accepted witness"
        );
        if (expectedDataFound is not null)
        {
            Assert.Equal(expectedDataFound, record.DataFound);
        }
        return record;
    }

    private static void AssertRejected(string patternName, string input)
    {
        var (regex, records) = Evaluate(patternName, input);
        Assert.Empty(records);
        Assert.False(
            RegexOutputCore.IsMatch(patternName, regex, input),
            $"{patternName} record/boolean path parity failed for a rejected witness"
        );
    }

    private static (Regex Regex, IReadOnlyList<RegexOutputRecord> Records) Evaluate(
        string patternName,
        string input
    )
    {
        var definition = BuiltInPatternCatalog.ByName[patternName];
        var regex = RegexOutputCore.GetOrCreateRegex(definition.Name, definition.Pattern);
        var records = RegexOutputCore
            .CreateRecords(
                new ParsedHit(input, input, string.Empty),
                definition.Name,
                regex,
                regexOutput: true,
                sourceFile: "synthetic-adversarial-corpus.bin",
                patternType: "Regex"
            )
            .ToArray();
        return (regex, records);
    }

    private static string BuildCpe(params string[] components) =>
        "cpe:2.3:" + string.Join(':', components);

    private static string CreateLei(string body)
    {
        if (body.Length != 18)
        {
            throw new ArgumentException("A synthetic LEI body must contain 18 characters.", nameof(body));
        }

        var checkDigits = 98 - Mod97(body + "00");
        var lei = body + checkDigits.ToString("D2", CultureInfo.InvariantCulture);
        if (Mod97(lei) != 1)
        {
            throw new InvalidOperationException("The generated synthetic LEI failed MOD 97-10.");
        }
        return lei;
    }

    private static int Mod97(string value)
    {
        var remainder = 0;
        foreach (var character in value)
        {
            if (character is >= '0' and <= '9')
            {
                remainder = ((remainder * 10) + character - '0') % 97;
                continue;
            }
            if (character is < 'A' or > 'Z')
            {
                throw new ArgumentException("MOD 97 fixture input must be uppercase alphanumeric.", nameof(value));
            }

            remainder = ((remainder * 100) + character - 'A' + 10) % 97;
        }
        return remainder;
    }

    private static string CreateNpi(string firstNineDigits)
    {
        if (
            firstNineDigits.Length != 9
            || firstNineDigits.Any(character => character is < '0' or > '9')
        )
        {
            throw new ArgumentException("An NPI body must contain nine ASCII digits.", nameof(firstNineDigits));
        }

        var payload = "80840" + firstNineDigits;
        var sum = 0;
        var doubleDigit = true;
        for (var index = payload.Length - 1; index >= 0; index--)
        {
            var digit = payload[index] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }
            sum += digit;
            doubleDigit = !doubleDigit;
        }

        var checkDigit = (10 - (sum % 10)) % 10;
        return firstNineDigits + checkDigit.ToString(CultureInfo.InvariantCulture);
    }

    private static string MutateFinalDigit(string value) =>
        value[..^1] + (value[^1] == '9' ? '0' : (char)(value[^1] + 1));
}
