using Xunit;

namespace bstrings.Tests;

public class EmailPatternPrecisionTests
{
    private static readonly BuiltInPatternDefinition StrictDefinition =
        BuiltInPatternCatalog.ByName["email"];
    private static readonly BuiltInPatternDefinition CandidateDefinition =
        BuiltInPatternCatalog.ByName["email_candidate"];

    [Fact]
    public void IanaSnapshot_IsPinnedAndSupportsCaseInsensitiveSpanLookup()
    {
        Assert.Equal("2026081700", IanaTopLevelDomains.Version);
        Assert.Equal(1438, IanaTopLevelDomains.Count);
        Assert.Equal(
            "681c3d70701a1095f725842187dc6f26ddad20e6563f48e0ab33eedad790ab78",
            IanaTopLevelDomains.SourceSha256
        );
        Assert.True(IanaTopLevelDomains.Contains("COM"));
        Assert.True(IanaTopLevelDomains.Contains("technology"));
        Assert.True(IanaTopLevelDomains.Contains("XN--VERMGENSBERATER-CTB"));
        Assert.False(IanaTopLevelDomains.Contains("DLL"));
        Assert.False(IanaTopLevelDomains.Contains("XPI"));
        Assert.False(IanaTopLevelDomains.Contains("DRFM"));
        Assert.False(IanaTopLevelDomains.Contains("X"));
    }

    [Theory]
    [InlineData("security@example.net")]
    [InlineData("alpha.beta+2@example.com")]
    [InlineData("analyst_01@subdomain.example.org")]
    [InlineData("user@sample.technology")]
    [InlineData("USER@SAMPLE.COM")]
    public void StrictEmail_AcceptsCommonMailboxFormsWithIanaTlds(string value)
    {
        Assert.True(BuiltInSemanticValidator.IsValid(StrictDefinition, value));
        Assert.False(BuiltInSemanticValidator.IsValid(CandidateDefinition, value));
    }

    [Theory]
    [InlineData("Q7%abc@random.drfm")]
    [InlineData("%QA@q.o")]
    [InlineData("xsxbxQx@x.x")]
    [InlineData("Ks@h.f")]
    [InlineData("screenshots@vendor.org.xpi")]
    [InlineData("er@vendor.org.xpi")]
    [InlineData("sy@module.dll")]
    [InlineData("-32769|Desc=@Resource.dll")]
    [InlineData("svchost.exe|Svc=Sample|Name=@Resource.dll")]
    public void StrictEmail_RejectsRepresentativeBinaryAndResourceNoise(string value)
    {
        Assert.Empty(Match("email", value));
    }

    [Fact]
    public void StrictEmail_ExtractsTheMailboxAfterMetadataAssignment()
    {
        Assert.Equal(
            ["security@example.net"],
            Match("email", "X509_emailAddress=security@example.net")
        );
    }

    [Theory]
    [InlineData("#@example.com")]
    [InlineData("label=security@example.net")]
    [InlineData("user@component.dll")]
    [InlineData("%QA@q.o")]
    public void CandidateEmail_PreservesBroaderOptInCoverage(string value)
    {
        Assert.Equal([value], Match("email_candidate", value));
    }

    [Fact]
    public void StrictAndCandidateEmail_AreDisjointForIdenticalCandidateText()
    {
        var values = new[]
        {
            "owner@example.com",
            "#@example.com",
            "user@component.dll",
            "alpha.beta+tag@sample.technology",
        };

        foreach (var value in values)
        {
            Assert.NotEqual(
                BuiltInSemanticValidator.IsValid(StrictDefinition, value),
                BuiltInSemanticValidator.IsValid(CandidateDefinition, value)
            );
        }
    }

    private static IReadOnlyList<string> Match(string patternName, string text)
    {
        var regex = RegexOutputCore.GetOrCreateRegex(
            patternName,
            BuiltInPatternCatalog.Patterns[patternName]
        );
        return RegexOutputCore
            .CreateRecords(
                new ParsedHit(text, text, string.Empty),
                patternName,
                regex,
                regexOutput: true,
                sourceFile: "synthetic.bin",
                patternType: "Regex"
            )
            .Select(record => record.DataFound)
            .ToArray();
    }
}
