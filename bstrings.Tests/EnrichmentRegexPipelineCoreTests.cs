using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class EnrichmentRegexPipelineCoreTests
{
    [Fact]
    public async Task ProcessAsync_PreservesRawAndTranslatedLineage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("enriched.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        recordType = "string",
                        recordId = "raw-1",
                        text = "contact analyst@example.com",
                        sourceFile = "sample.exe",
                        location = new { kind = "file_offset", value = "0x2A" },
                        origin = new { extractor = "floss", version = "3.1.1", kind = "static" },
                    }
                ),
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        recordType = "string",
                        recordId = "translated-1",
                        text = "email owner@example.net",
                        sourceFile = "sample.exe",
                        location = new { kind = "virtual_address", value = "0x401000" },
                        origin = new { extractor = "floss", version = "3.1.1", kind = "decoded" },
                        parentRecordId = "raw-1",
                        transform = new
                        {
                            kind = "translation",
                            engine = "transformers",
                            engineVersion = "4.57.6",
                            model = "google/madlad400-3b-mt",
                            revision = "abc123",
                            modelSha256 = new string('a', 64),
                            targetLanguage = "en",
                        },
                    }
                ),
            ],
            cancellationToken
        );

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            cancellationToken: cancellationToken
        );

        Assert.Equal(new EnrichmentPipelineStats(2, 1, 2), stats);
        var rows = (await File.ReadAllLinesAsync(outputPath, cancellationToken))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal("analyst@example.com", rows[0].RootElement.GetProperty("match").GetString());
            Assert.Equal("byte-native", rows[0].RootElement.GetProperty("evidenceClass").GetString());
            Assert.Equal("raw-1", rows[0].RootElement.GetProperty("sourceRecordId").GetString());
            Assert.Equal("0x2A", rows[0].RootElement.GetProperty("location").GetProperty("value").GetString());

            Assert.Equal("owner@example.net", rows[1].RootElement.GetProperty("match").GetString());
            Assert.Equal("derived-translation", rows[1].RootElement.GetProperty("evidenceClass").GetString());
            Assert.Equal("raw-1", rows[1].RootElement.GetProperty("parentRecordId").GetString());
            Assert.Equal(
                "google/madlad400-3b-mt",
                rows[1].RootElement.GetProperty("transform").GetProperty("model").GetString()
            );
        }
        finally
        {
            foreach (var row in rows)
            {
                row.Dispose();
            }
        }
    }

    [Fact]
    public async Task ProcessAsync_LabelsFlossDecodedStringsAsDerivedExtractor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("decoded.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {"schemaVersion":1,"recordType":"string","recordId":"decoded-1","text":"10.1.2.3","sourceFile":"sample.exe","location":{"kind":"virtual_address","value":"0x401000"},"origin":{"extractor":"floss","version":"3.1.1","kind":"decoded"}}
            """,
            cancellationToken
        );
        var output = new StringWriter();

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [("ipv4", BuiltInPatternCatalog.Patterns["ipv4"])],
            output,
            cancellationToken
        );

        Assert.Equal(1, stats.MatchRecords);
        using var row = JsonDocument.Parse(output.ToString());
        Assert.Equal("derived-extractor", row.RootElement.GetProperty("evidenceClass").GetString());
        Assert.Equal("virtual_address", row.RootElement.GetProperty("location").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task ProcessAsync_UsesBuiltInCaptureSemantics()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("url.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            """
            {"schemaVersion":1,"recordType":"string","recordId":"url-1","text":"https://analyst:secret@example.com/path","sourceFile":"sample.exe","location":{"kind":"file_offset","value":"0x10"},"origin":{"extractor":"floss","version":"3.1.1","kind":"static"}}
            """,
            cancellationToken
        );
        var output = new StringWriter();

        await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [("urlUser", BuiltInPatternCatalog.Patterns["urlUser"])],
            output,
            cancellationToken
        );

        using var row = JsonDocument.Parse(output.ToString());
        Assert.Equal("analyst", row.RootElement.GetProperty("match").GetString());
    }

    [Fact]
    public async Task ProcessAsync_RejectsTranslatedRecordWithoutParentAndPreservesOldOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("invalid.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        await File.WriteAllTextAsync(outputPath, "previous-result", cancellationToken);
        await File.WriteAllTextAsync(
            inputPath,
            """
            {"schemaVersion":1,"recordType":"string","recordId":"translation-1","text":"owner@example.com","sourceFile":"sample.exe","location":{"kind":"file_offset","value":"0x10"},"origin":{"extractor":"floss","version":"3.1.1","kind":"static"},"transform":{"kind":"translation","engine":"test","targetLanguage":"en"}}
            """,
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                cancellationToken: cancellationToken
            )
        );

        Assert.Contains("parentRecordId", error.Message);
        Assert.Equal("previous-result", await File.ReadAllTextAsync(outputPath, cancellationToken));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task ProcessAsync_TrustedStreamRejectsMissingParentAndPreservesOldOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("invalid.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        await File.WriteAllTextAsync(outputPath, "previous-result", cancellationToken);
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("raw-1", "ordinary text", parentRecordId: null),
                CreateRecord("translation-1", "owner@example.com", "missing-parent"),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                cancellationToken: cancellationToken,
                trustedParentFirstInput: true
            )
        );

        Assert.Contains("does not exist", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("previous-result", await File.ReadAllTextAsync(outputPath, cancellationToken));
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-provenance.*"));
    }

    [Fact]
    public async Task ProcessAsync_TrustedStreamRejectsDuplicateRecordIds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("invalid.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                CreateRecord("duplicate", "first ordinary text", parentRecordId: null),
                CreateRecord("duplicate", "second ordinary text", parentRecordId: null),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                cancellationToken: cancellationToken,
                trustedParentFirstInput: true
            )
        );

        Assert.Contains("repeats recordId", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-provenance.*"));
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"a@b.com\",\"sourceFile\":\"x\",\"location\":{\"kind\":\"file_offset\",\"value\":\"0x0\"},\"origin\":{\"extractor\":\"floss\",\"kind\":\"static\"}}", "schema version")]
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"sourceFile\":\"x\",\"location\":{\"kind\":\"file_offset\",\"value\":\"0x0\"},\"origin\":{\"extractor\":\"floss\",\"kind\":\"static\"}}", "usable text")]
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"a@b.com\",\"sourceFile\":\"x\",\"location\":{\"kind\":\"file_offset\",\"value\":\"0x0\"},\"origin\":{\"extractor\":\"floss\",\"kind\":\"static\"},\"transform\":{\"kind\":\"unknown\"}}", "unsupported transform")]
    public async Task ProcessAsync_RejectsInvalidEvidenceRecords(string line, string expectedMessage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("invalid.jsonl");
        await File.WriteAllTextAsync(inputPath, line, cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath: null,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                new StringWriter(),
                cancellationToken
            )
        );

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateRecord(
        string recordId,
        string text,
        string? parentRecordId
    )
    {
        if (parentRecordId is null)
        {
            return JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    recordType = "string",
                    recordId,
                    text,
                    sourceFile = "sample.exe",
                    location = new { kind = "file_offset", value = "0x10" },
                    origin = new { extractor = "bstrings", version = "test", kind = "static" },
                }
            );
        }
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "sample.exe",
                location = new { kind = "file_offset", value = "0x10" },
                origin = new { extractor = "bstrings", version = "test", kind = "static" },
                parentRecordId,
                transform = new
                {
                    kind = "translation",
                    engine = "test",
                    engineVersion = "1",
                    model = "synthetic",
                    revision = "test",
                    modelSha256 = new string('a', 64),
                    targetLanguage = "en",
                },
            }
        );
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), "bstrings-enrichment-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
