using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class NativeEnrichmentRecordTransformTests
{
    [Fact]
    public void TransformBatch_PreservesByteRangeAndStableIdentity()
    {
        var transform = new NativeEnrichmentRecordTransform("evidence.bin");
        var rows = transform.TransformBatch(
            [new ExtractedStringHit("important text", 0x1234, 28, "utf-16le")]
        );

        var row = Assert.Single(rows);
        using var document = JsonDocument.Parse(row);
        var root = document.RootElement;
        Assert.Equal("string", root.GetProperty("recordType").GetString());
        Assert.Equal("important text", root.GetProperty("text").GetString());
        Assert.Equal("0x1234", root.GetProperty("location").GetProperty("value").GetString());
        Assert.Equal("utf-16le", root.GetProperty("attributes").GetProperty("encoding").GetString());
        Assert.Equal(28, root.GetProperty("attributes").GetProperty("byteLength").GetInt32());
        Assert.Equal(
            NativeEnrichmentRecordTransform.CreateRecordId(
                "evidence.bin",
                "0x1234",
                "important text"
            ),
            root.GetProperty("recordId").GetString()
        );
        Assert.Equal(1, transform.RecordCount);
    }

    [Fact]
    public void TransformBoundaryBatch_EmitsOnlyStringsCrossingThePrimaryBoundary()
    {
        var transform = new NativeEnrichmentRecordTransform("evidence.bin");
        var rows = transform.TransformBoundaryBatch(
            [
                new ExtractedStringHit("before", 80, 6, "code-page-1252"),
                new ExtractedStringHit("crossing", 96, 12, "code-page-1252"),
                new ExtractedStringHit("after", 104, 5, "code-page-1252"),
            ],
            primaryChunkSize: 100
        );

        var row = Assert.Single(rows);
        using var document = JsonDocument.Parse(row);
        Assert.Equal("crossing", document.RootElement.GetProperty("text").GetString());
        Assert.Equal(1, transform.RecordCount);
    }

    [Fact]
    public void TransformBatch_RejectsTextAboveTheJsonlSafeAnalysisLimit()
    {
        var transform = new NativeEnrichmentRecordTransform("evidence.bin");
        var oversized = new string(
            'A',
            EnrichmentRegexPipelineCore.MaxNativeTextCharacters + 1
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            transform.TransformBatch([new ExtractedStringHit(oversized, 0, oversized.Length, "ascii")])
        );

        Assert.Contains("text limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, transform.RecordCount);
    }

    [Fact]
    public void TransformBatch_WorstCaseEscapingFitsTheMatcherLineLimit()
    {
        var transform = new NativeEnrichmentRecordTransform("evidence.bin");
        var text = new string('\u0001', EnrichmentRegexPipelineCore.MaxNativeTextCharacters);

        var row = Assert.Single(
            transform.TransformBatch([new ExtractedStringHit(text, 0, text.Length, "utf-8")])
        );

        Assert.True(row.Length <= EnrichmentRegexPipelineCore.MaxJsonLineCharacters);
    }
}
