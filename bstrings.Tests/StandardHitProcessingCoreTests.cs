using System.IO;
using Xunit;

namespace bstrings.Tests;

public class StandardHitProcessingCoreTests
{
    [Fact]
    public void CompileRegexTargets_UsesFriendlyNameWhenAvailable()
    {
        var compiled = StandardHitProcessingCore.CompileRegexTargets(
            ["Alpha\\d+", "Beta\\d+"],
            pattern => pattern == "Alpha\\d+" ? "alpha-id" : null
        );

        Assert.Collection(
            compiled,
            first =>
            {
                Assert.Equal("alpha-id", first.Name);
                Assert.Equal("Alpha\\d+", first.Pattern);
                Assert.Matches(first.Regex, "Alpha42");
            },
            second =>
            {
                Assert.Equal("Beta\\d+", second.Name);
                Assert.Equal("Beta\\d+", second.Pattern);
                Assert.Matches(second.Regex, "Beta9");
            }
        );
    }

    [Fact]
    public void CompileRegexTargets_SkipsInvalidPatternsAndReportsThem()
    {
        string? message = null;
        string? badPattern = null;

        var compiled = StandardHitProcessingCore.CompileRegexTargets(
            ["[", "Alpha"],
            _ => null,
            (pattern, error) =>
            {
                badPattern = pattern;
                message = error;
            }
        );

        Assert.Single(compiled);
        Assert.Equal("[", badPattern);
        Assert.Contains("invalid pattern", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryMatchHit_MatchesLiteralStringAgainstParsedDataAndPreservesOffset()
    {
        var matched = StandardHitProcessingCore.TryMatchHit(
            "0x201\tAlpha123",
            ["alpha"],
            [],
            includeOffset: true,
            currentFile: "sample.bin"
        );

        Assert.NotNull(matched);
        Assert.Equal("0x201\tAlpha123", matched!.RawHit);
        Assert.Equal("Alpha123", matched.DataFound);
        Assert.Equal("0x201", matched.Offset);
        Assert.Equal("alpha", matched.PatternName);
        Assert.Equal("String", matched.PatternType);
        Assert.Equal("sample.bin", matched.SourceFile);
    }

    [Fact]
    public void TryMatchHit_MatchesRegexTargetsLoadedFromFilePatterns()
    {
        var compiled = StandardHitProcessingCore.CompileRegexTargets(
            ["Alpha\\d+"],
            _ => null
        );

        var matched = StandardHitProcessingCore.TryMatchHit(
            "0x201\tAlpha123",
            [],
            compiled,
            includeOffset: true,
            currentFile: "sample.bin"
        );

        Assert.NotNull(matched);
        Assert.Equal("Alpha123", matched!.DataFound);
        Assert.Equal("0x201", matched.Offset);
        Assert.Equal("Alpha\\d+", matched.PatternName);
        Assert.Equal("Regex", matched.PatternType);
    }

    [Fact]
    public void TryMatchHit_ReturnsRawHitWhenNoPatternsAreSpecified()
    {
        var matched = StandardHitProcessingCore.TryMatchHit(
            "Alpha123",
            [],
            [],
            includeOffset: false,
            currentFile: null
        );

        Assert.NotNull(matched);
        Assert.Equal("Alpha123", matched!.RawHit);
        Assert.Equal("Alpha123", matched.DataFound);
        Assert.Equal(string.Empty, matched.Offset);
        Assert.Equal(string.Empty, matched.PatternName);
        Assert.Equal(string.Empty, matched.PatternType);
        Assert.Equal(string.Empty, matched.SourceFile);
    }

    [Fact]
    public void WriteMatchedHit_WritesCsvHeaderAndEscapedRecord()
    {
        using var writer = new StringWriter();

        var headerWritten = StandardHitProcessingCore.WriteMatchedHit(
            new MatchedHit(
                "0x201\tAlpha,\"Beta\"",
                "Alpha,\"Beta\"",
                "0x201",
                "alpha-pattern",
                "Regex",
                "sample.bin"
            ),
            isCsvOutput: true,
            csvHeaderWritten: false,
            writer
        );

        var lines = writer
            .ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.True(headerWritten);
        Assert.Equal(2, lines.Length);
        Assert.Equal(RegexOutputCore.CsvHeader, lines[0]);
        Assert.Equal(
            "\"alpha-pattern\",\"Alpha,\"\"Beta\"\"\",\"sample.bin\",\"0x201\",\"Regex\"",
            lines[1]
        );
    }

    [Fact]
    public void WriteMatchedHit_WritesRawHitForTextOutput()
    {
        using var writer = new StringWriter();

        var headerWritten = StandardHitProcessingCore.WriteMatchedHit(
            new MatchedHit("0x201\tAlpha123", "Alpha123", "0x201", "", "", "sample.bin"),
            isCsvOutput: false,
            csvHeaderWritten: false,
            writer
        );

        Assert.False(headerWritten);
        Assert.Equal("0x201\tAlpha123" + Environment.NewLine, writer.ToString());
    }
}
