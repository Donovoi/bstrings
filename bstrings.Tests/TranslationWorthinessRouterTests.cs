using Xunit;

namespace bstrings.Tests;

public sealed class TranslationWorthinessRouterTests
{
    [Theory]
    [InlineData("6F9619FF-8B86-D011-B42D-00C04FC964FF", "guid")]
    [InlineData("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "cryptographic-hash")]
    [InlineData("192.0.2.42", "ip-address")]
    [InlineData("2001:db8::1/64", "ip-network")]
    [InlineData("192.0.2.42:443", "network-endpoint")]
    public void Assess_OnlyPromotesExactSemanticallyValidatedSingleStructures(
        string text,
        string expectedClass
    )
    {
        var assessment = TranslationWorthinessRouter.Assess(text);

        Assert.Equal(
            TranslationRoutingOutcome.StructuredOnlyProspectiveBypass,
            assessment.Outcome
        );
        Assert.False(assessment.IsUnknown);
        Assert.Equal(1, assessment.CoverageScore);
        Assert.Equal("whole-record-structured-coverage", assessment.Reason);
        Assert.Equal([expectedClass], assessment.StructuredClasses);
        Assert.Equal(1, assessment.StructuredTokenCount);
    }

    [Theory]
    [InlineData("https://example.test/download?id=42", "absolute-uri")]
    [InlineData("C:\\Windows\\System32\\config\\SYSTEM", "absolute-file-path")]
    [InlineData("HKLM\\Software\\Example", "registry-path")]
    [InlineData("0x0123456789abcdef0123456789abcdef", "hex-blob")]
    [InlineData("VGhpcyBpcyBhIHRlc3QgcGF5bG9hZC4=", "base64-blob")]
    [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c", "jwt")]
    public void Assess_MachineLikeSignalsRemainRetainedUntilPromotionPrerequisitesExist(
        string text,
        string expectedClass
    )
    {
        var assessment = TranslationWorthinessRouter.Assess(text);

        Assert.Equal(TranslationRoutingOutcome.Retain, assessment.Outcome);
        Assert.False(assessment.IsUnknown);
        Assert.Equal(1, assessment.CoverageScore);
        Assert.Equal("machine-like-signal-not-bypass-eligible", assessment.Reason);
        Assert.Equal([expectedClass], assessment.StructuredClasses);
    }

    [Theory]
    [InlineData("Télécharger le dossier 6F9619FF-8B86-D011-B42D-00C04FC964FF maintenant")]
    [InlineData("if (token != null) return Decode(token);")]
    [InlineData("server=https://example.test/api")]
    [InlineData("docs/重要.txt")]
    [InlineData("Путь C:\\Windows\\Temp содержит доказательства")]
    [InlineData("ThisIsOrdinaryEnglishText")]
    public void Assess_FailsOpenForMixedUnicodeCodeConfigAndAmbiguousText(string text)
    {
        var assessment = TranslationWorthinessRouter.Assess(text);

        Assert.Equal(TranslationRoutingOutcome.Retain, assessment.Outcome);
        Assert.False(assessment.IsUnknown);
        Assert.False(assessment.CoverageScore == 1 && assessment.StructuredTokenCount > 0);
    }

    [Fact]
    public void Assess_FailsOpenWhenJwtLikeTextCannotBeValidated()
    {
        var assessment = TranslationWorthinessRouter.Assess("YWJjZA.YWJjZA.YWJjZA");

        Assert.Equal(TranslationRoutingOutcome.Retain, assessment.Outcome);
        Assert.True(assessment.IsUnknown);
        Assert.Equal("router-error", assessment.Reason);
    }

    [Fact]
    public void Assess_DoesNotPromoteCompositeOrWrappedStructuredTokens()
    {
        var composite = TranslationWorthinessRouter.Assess(
            "192.0.2.1; sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
        );
        var wrapped = TranslationWorthinessRouter.Assess(
            "[6F9619FF-8B86-D011-B42D-00C04FC964FF]"
        );

        Assert.Equal(TranslationRoutingOutcome.Retain, composite.Outcome);
        Assert.Equal("no-exact-structured-coverage", composite.Reason);
        Assert.Equal(TranslationRoutingOutcome.Retain, wrapped.Outcome);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.1")]
    [InlineData("127.1")]
    [InlineData("010.0.0.1")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("192.0.2.1/024")]
    [InlineData("192.0.2.1:0443")]
    [InlineData("2001:0db8:0:0:0:0:0:1")]
    public void Assess_RejectsNonCanonicalNetworkNearMisses(string text)
    {
        var assessment = TranslationWorthinessRouter.Assess(text);

        Assert.Equal(TranslationRoutingOutcome.Retain, assessment.Outcome);
    }

    [Fact]
    public void Assess_IsDeterministic()
    {
        const string text = "Esta evidencia necesita traducción.";
        var native = TranslationWorthinessRouter.Assess(text);
        var repeat = TranslationWorthinessRouter.Assess(text);

        Assert.Equal(TranslationRoutingOutcome.Retain, native.Outcome);
        Assert.Equal(native.Outcome, repeat.Outcome);
        Assert.Equal(native.StructuredClasses, repeat.StructuredClasses);
    }

    [Fact]
    public void Assess_FailsOpenForUnsupportedInputWithoutUnboundedMetadata()
    {
        var assessment = TranslationWorthinessRouter.Assess(
            new string('A', TranslationWorthinessRouter.MaximumTextCharacters + 1)
        );

        Assert.Equal(TranslationRoutingOutcome.Retain, assessment.Outcome);
        Assert.True(assessment.IsUnknown);
        Assert.Equal("unsupported-length", assessment.Reason);
        Assert.Empty(assessment.StructuredClasses);
    }
}
