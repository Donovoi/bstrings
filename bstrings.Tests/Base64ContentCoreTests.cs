using System.Text;
using Xunit;

namespace bstrings.Tests;

public sealed class Base64ContentCoreTests
{
    [Fact]
    public void IsHighConfidence_AcceptsCanonicalTextAndStrongBinaryEvidence()
    {
        var utf8 = Convert.ToBase64String(Encoding.UTF8.GetBytes("forensic command text"));
        var utf16 = Convert.ToBase64String(
            [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("multilingual evidence")]
        );
        var png = Convert.ToBase64String(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[16]]
        );

        Assert.True(Base64ContentCore.IsHighConfidence(utf8));
        Assert.True(Base64ContentCore.IsHighConfidence(utf16));
        Assert.True(Base64ContentCore.IsHighConfidence(png));
    }

    [Fact]
    public void IsHighConfidence_ValidatesTheCompleteDecodedPayload()
    {
        var payload = Encoding.UTF8.GetBytes(new string('A', 12_000)).Concat([byte.MinValue]).ToArray();
        var candidate = Convert.ToBase64String(payload);

        Assert.False(Base64ContentCore.IsHighConfidence(candidate));
    }

    [Fact]
    public void IsHighConfidence_EnforcesEncodedWorkBoundaries()
    {
        var atMinimum = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('A', 16)));
        var belowMinimum = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('A', 15)));
        var atMaximum = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('A', 12_288)));
        var aboveMaximum = Convert.ToBase64String(Encoding.UTF8.GetBytes(new string('A', 12_289)));

        Assert.Equal(Base64ContentCore.MinimumHighConfidenceEncodedCharacters, atMinimum.Length);
        Assert.Equal(Base64ContentCore.MaximumHighConfidenceEncodedCharacters, atMaximum.Length);
        Assert.True(Base64ContentCore.IsHighConfidence(atMinimum));
        Assert.False(Base64ContentCore.IsHighConfidence(belowMinimum));
        Assert.True(Base64ContentCore.IsHighConfidence(atMaximum));
        Assert.False(Base64ContentCore.IsHighConfidence(aboveMaximum));
    }

    [Fact]
    public void IsHighConfidence_RejectsAmbiguousOrNoncanonicalValues()
    {
        var opaque = Convert.ToBase64String(
            Enumerable.Range(0, 32).Select(index => (byte)(0x80 + index)).ToArray()
        );

        Assert.False(Base64ContentCore.IsHighConfidence("SoftwareDistribution"));
        Assert.False(Base64ContentCore.IsHighConfidence("0123456789ABCDEF0123456789ABCDEF"));
        Assert.False(Base64ContentCore.IsHighConfidence("AAAAAAAAAAAAAAAAAAAAAAAA"));
        Assert.False(Base64ContentCore.IsHighConfidence("AAAAAAAAAAAAAAAAAAAAAA__"));
        Assert.False(Base64ContentCore.IsHighConfidence(opaque));
    }

    [Theory]
    [InlineData("System32")]
    [InlineData("Microsoft")]
    [InlineData("Schedule")]
    [InlineData("SoftwareDistribution")]
    [InlineData("i8042prt")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    public void IsHighConfidence_RejectsOrdinaryWindowsNamesAndHexIdentifiers(string value)
    {
        Assert.False(Base64ContentCore.IsHighConfidence(value));
    }

    [Fact]
    public void StrictAndCandidatePartitionsAreDisjoint()
    {
        var strict = Convert.ToBase64String(Encoding.UTF8.GetBytes("forensic command text"));
        const string broadOnly = "SGVsbG8=";

        Assert.True(BuiltInSemanticValidator.IsValid(BuiltInPatternCatalog.ByName["b64"], strict));
        Assert.False(
            BuiltInSemanticValidator.IsValid(BuiltInPatternCatalog.ByName["b64_candidate"], strict)
        );
        Assert.False(
            BuiltInSemanticValidator.IsValid(BuiltInPatternCatalog.ByName["b64"], broadOnly)
        );
        Assert.True(
            BuiltInSemanticValidator.IsValid(
                BuiltInPatternCatalog.ByName["b64_candidate"],
                broadOnly
            )
        );
    }
}
