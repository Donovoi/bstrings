using System.Text.RegularExpressions;
using Xunit;

namespace bstrings.Tests;

public sealed class CatalogPrecisionPolicyTests
{
    [Fact]
    public void AllAndCandidates_PartitionTheCompleteCatalogExactlyOnce()
    {
        var defaults = SearchCore.ParseRegexPatternsWithNames(
            "all",
            BuiltInPatternCatalog.Patterns,
            BuiltInPatternCatalog.Groups,
            BuiltInPatternCatalog.DefaultPatterns
        );
        var candidates = SearchCore.ParseRegexPatternsWithNames(
            "candidates",
            BuiltInPatternCatalog.Patterns,
            BuiltInPatternCatalog.Groups,
            BuiltInPatternCatalog.DefaultPatterns
        );
        var complete = SearchCore.ParseRegexPatternsWithNames(
            "all,candidates",
            BuiltInPatternCatalog.Patterns,
            BuiltInPatternCatalog.Groups,
            BuiltInPatternCatalog.DefaultPatterns
        );

        Assert.Equal(74, defaults.Count);
        Assert.Equal(8, candidates.Count);
        Assert.Equal(82, complete.Count);
        Assert.Empty(
            defaults
                .Select(pattern => pattern.name)
                .Intersect(candidates.Select(pattern => pattern.name), StringComparer.OrdinalIgnoreCase)
        );
        Assert.Equal(
            BuiltInPatternCatalog.Patterns.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            complete.Select(pattern => pattern.name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
        );
        Assert.Equal(
            complete.Count,
            complete.Select(pattern => pattern.name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
        );
    }

    [Fact]
    public void CandidateGroup_IsDerivedFromSelectedByAllMetadata()
    {
        Assert.Equal(
            BuiltInPatternCatalog.Definitions
                .Where(definition => !definition.SelectedByAll)
                .Select(definition => definition.Name),
            BuiltInPatternCatalog.Groups["candidates"]
        );
        Assert.Equal(
            [
                "mac_candidate",
                "ipv6_candidate",
                "email_candidate",
                "zip",
                "reg_path_candidate",
                "b64_candidate",
                "solana",
                "move_address",
            ],
            BuiltInPatternCatalog.Groups["candidates"]
        );
    }

    [Theory]
    [InlineData("00:11:22:AA:BB:CC", true, false)]
    [InlineData("00-11-22-AA-BB-CC", true, false)]
    [InlineData("001122AABBCC", false, true)]
    [InlineData("00:11-22:AA:BB:CC", false, false)]
    public void MacPartitions_AreDisjoint(string value, bool strict, bool candidate)
    {
        Assert.Equal(strict, Matches("mac", value));
        Assert.Equal(candidate, Matches("mac_candidate", value));
        Assert.False(strict && candidate);
    }

    [Theory]
    [InlineData("2001:db8::1", true, false)]
    [InlineData("::1", true, false)]
    [InlineData("::", false, true)]
    [InlineData("2001:::1", false, false)]
    public void Ipv6Partitions_AreDisjoint(string value, bool strict, bool candidate)
    {
        Assert.Equal(strict, Matches("ipv6", value));
        Assert.Equal(candidate, Matches("ipv6_candidate", value));
        Assert.False(strict && candidate);
    }

    [Theory]
    [InlineData(@"HKLM\SOFTWARE", true, false)]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Vendor\Product", true, false)]
    [InlineData(@"SOFTWARE\Vendor\Product", true, false)]
    [InlineData("SOFTWARE", false, true)]
    [InlineData("SYSTEM", false, true)]
    [InlineData("SYSTEM32", false, false)]
    [InlineData(@"C:\artifact\SOFTWARE", false, false)]
    [InlineData(@"C:\artifact\SOFTWARE\Vendor", false, false)]
    [InlineData(@"C:\artifact\HKLM\SOFTWARE\Vendor", false, false)]
    public void RegistryPartitions_AreDisjoint(string value, bool strict, bool candidate)
    {
        Assert.Equal(strict, Matches("reg_path", value));
        Assert.Equal(candidate, Matches("reg_path_candidate", value));
        Assert.False(strict && candidate);
    }

    [Fact]
    public void BroadExactNamesAndDomainGroupsRemainAvailable()
    {
        var defaults = BuiltInPatternCatalog.DefaultPatterns.Keys.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );

        Assert.DoesNotContain("zip", defaults);
        Assert.DoesNotContain("solana", defaults);
        Assert.DoesNotContain("move_address", defaults);
        Assert.Contains("zip", BuiltInPatternCatalog.Groups["pii"]);
        Assert.Contains("solana", BuiltInPatternCatalog.Groups["wallets"]);
        Assert.Contains("move_address", BuiltInPatternCatalog.Groups["wallets"]);
        Assert.Contains("reg_path_candidate", BuiltInPatternCatalog.Groups["registry"]);

        Assert.True(Matches("zip", "90210-1234"));
        Assert.True(Matches("solana", "11111111111111111111111111111111"));
        Assert.True(
            Matches(
                "move_address",
                "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            )
        );
    }

    [Fact]
    public void ValidatorsRemainOfflineAndDeterministicAcrossRepeatedCalls()
    {
        var cases = new[]
        {
            (Name: "mac", Value: "00:11:22:AA:BB:CC"),
            (Name: "mac_candidate", Value: "001122AABBCC"),
            (Name: "ipv6", Value: "2001:db8::1"),
            (Name: "ipv6_candidate", Value: "::"),
        };

        foreach (var testCase in cases)
        {
            var definition = BuiltInPatternCatalog.ByName[testCase.Name];
            for (var iteration = 0; iteration < 100; iteration++)
            {
                Assert.True(BuiltInSemanticValidator.IsValid(definition, testCase.Value));
            }
        }
    }

    private static bool Matches(string name, string value)
    {
        var definition = BuiltInPatternCatalog.ByName[name];
        var regex = RegexOutputCore.GetOrCreateRegex(name, definition.Pattern);
        return RegexOutputCore
            .CreateRecords(
                new ParsedHit(value, value, string.Empty),
                name,
                regex,
                regexOutput: true,
                sourceFile: "synthetic.bin",
                patternType: "Regex"
            )
            .Any(record => string.Equals(record.DataFound, value, StringComparison.Ordinal));
    }
}
