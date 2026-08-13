using System.Text.Json.Nodes;
using Xunit;

namespace bstrings.Tests;

public sealed class DecoderCompletionCoreTests
{
    [Fact]
    public async Task ValidateAsync_ReproducesArtifactsCardinalityAndLineage()
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync(
            "VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==",
            "ThisLooksLikeEnglishLettersOnly"
        );
        await scope.ProcessAsync(TestContext.Current.CancellationToken);

        var result = await DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, result.RawRecords);
        Assert.Equal(1, result.AssessmentRecords);
        Assert.Equal(1, result.DecodedRecords);
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-provenance.*"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("location")]
    [InlineData("span")]
    [InlineData("outcome")]
    public async Task ValidateAsync_RejectsTamperedAssessmentEvidence(string defect)
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        var assessment = JsonNode.Parse(await File.ReadAllTextAsync(scope.AssessmentsPath, TestContext.Current.CancellationToken))!.AsObject();
        switch (defect)
        {
            case "source":
                assessment["sourceFile"] = "other.bin";
                break;
            case "location":
                assessment["location"]!["value"] = "0xBAD";
                break;
            case "span":
                assessment["candidateStart"] = 1;
                break;
            case "outcome":
                assessment["outcome"] = "text-rejected";
                assessment["reason"] = "unsupported-or-disallowed-text";
                assessment.Remove("decodedCharset");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }
        await File.WriteAllTextAsync(scope.AssessmentsPath, assessment.ToJsonString() + "\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));
    }

    [Fact]
    public async Task ValidateAsync_RejectsMissingOrderedAssessmentEvenWithPlausibleFiles()
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(scope.AssessmentsPath, string.Empty, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));

        Assert.Contains("no ordered assessment", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task ValidateAsync_RejectsUnknownOrDuplicateAssessmentProperties(string defect)
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(scope.AssessmentsPath, TestContext.Current.CancellationToken);
        json = defect == "unknown"
            ? json.Replace("{", "{\"unexpected\":true,", StringComparison.Ordinal)
            : json.Replace("{", "{\"mode\":\"auto\",", StringComparison.Ordinal);
        await File.WriteAllTextAsync(scope.AssessmentsPath, json, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("negative")]
    public async Task ValidateAsync_RejectsMalformedWorkStatsSchemaAndCounters(string defect)
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(scope.StatsPath, TestContext.Current.CancellationToken);
        json = defect switch
        {
            "unknown" => json.Replace("{", "{\"unexpected\":true,", StringComparison.Ordinal),
            "duplicate" => json.Replace("{", "{\"mode\":\"auto\",", StringComparison.Ordinal),
            "negative" => json.Replace("\"inputRecords\":1", "\"inputRecords\":-1", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        await File.WriteAllTextAsync(scope.StatsPath, json, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));
    }

    [Theory]
    [InlineData("outerWhitespaceTreatment")]
    [InlineData("leadingWhitespaceCharacters")]
    [InlineData("trailingWhitespaceCharacters")]
    public async Task ValidateAsync_RejectsTamperedChildWhitespaceProvenance(string attribute)
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("  VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==  ");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        var child = JsonNode.Parse(await File.ReadAllTextAsync(scope.DecodedPath, TestContext.Current.CancellationToken))!.AsObject();
        child["attributes"]![attribute] = attribute == "outerWhitespaceTreatment"
            ? JsonValue.Create("none")
            : JsonValue.Create(0);
        await File.WriteAllTextAsync(scope.DecodedPath, child.ToJsonString() + "\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("engine")]
    [InlineData("engineVersion")]
    [InlineData("profile")]
    [InlineData("policyVersion")]
    [InlineData("outcome")]
    [InlineData("model")]
    public async Task ValidateAsync_RejectsTamperedChildTransform(string property)
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        var child = JsonNode.Parse(await File.ReadAllTextAsync(scope.DecodedPath, TestContext.Current.CancellationToken))!.AsObject();
        child["transform"]![property] = property == "kind" ? "Decoding" : "tampered";
        await File.WriteAllTextAsync(scope.DecodedPath, child.ToJsonString() + "\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));
    }

    [Theory]
    [InlineData("schemaVersion")]
    [InlineData("candidateStart")]
    [InlineData("leadingWhitespaceCharacters")]
    [InlineData("decodeDepth")]
    public async Task ValidateAsync_RejectsMissingRequiredDefaultValuedAssessmentFields(string property)
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        var assessment = JsonNode.Parse(await File.ReadAllTextAsync(scope.AssessmentsPath, TestContext.Current.CancellationToken))!.AsObject();
        assessment.Remove(property);
        await File.WriteAllTextAsync(scope.AssessmentsPath, assessment.ToJsonString() + "\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));
    }

    [Fact]
    public async Task ValidateAsync_RejectsMissingWorkStatsCounter()
    {
        using var scope = new DecoderScope();
        await scope.WriteRawAsync("VGhpcyBpcyBhIHVzZWZ1bCB0ZXh0Lg==");
        await scope.ProcessAsync(TestContext.Current.CancellationToken);
        var stats = JsonNode.Parse(await File.ReadAllTextAsync(scope.StatsPath, TestContext.Current.CancellationToken))!.AsObject();
        stats.Remove("canonicalRejected");
        await File.WriteAllTextAsync(scope.StatsPath, stats.ToJsonString() + "\n", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => DecoderCompletionCore.ValidateAsync(
            scope.RawPath,
            scope.DecodedPath,
            scope.AssessmentsPath,
            scope.StatsPath,
            scope.Options,
            TestContext.Current.CancellationToken
        ));
    }
}
