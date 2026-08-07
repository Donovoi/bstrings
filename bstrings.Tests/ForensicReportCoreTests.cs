using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class ForensicReportCoreTests
{
    [Fact]
    public async Task WriteAsync_ProducesLineSafeTimelineExplorerReportsAndZeroCountPatterns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var matchesPath = scope.PathFor("matches.jsonl");
        var findingsPath = scope.PathFor("findings.tsv");
        var patternHistogramPath = scope.PathFor("pattern-histogram.tsv");
        var featureHistogramPath = scope.PathFor("feature-histogram.tsv");
        var visualizationPath = scope.PathFor("pattern-histogram.html");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var records = new[]
        {
            new EnrichmentRegexMatchRecord
            {
                PatternName = "email",
                Pattern = BuiltInPatternCatalog.Patterns["email"],
                Match = "analyst@example.com",
                MatchStart = 6,
                MatchLength = 19,
                MatchLine = 1,
                ContextStart = 0,
                Context = "login analyst@example.com\tactive",
                SourceRecordId = "native-1",
                SourceFile = @"C:\Users\Case\AppData\Local\Google\Chrome\User Data\Default\Login Data",
                Location = new EnrichmentLocation { Kind = "file_offset", Value = "0x2A" },
                Origin = new EnrichmentOrigin
                {
                    Extractor = "bstrings",
                    Version = "1.9.2",
                    Kind = "static",
                },
                EvidenceClass = "byte-native",
                Attributes = new Dictionary<string, JsonElement>
                {
                    ["encoding"] = JsonSerializer.SerializeToElement("UTF-8"),
                },
            },
            new EnrichmentRegexMatchRecord
            {
                PatternName = "email",
                Pattern = BuiltInPatternCatalog.Patterns["email"],
                Match = "owner@example.net",
                MatchStart = 5,
                MatchLength = 17,
                MatchLine = 2,
                ContextStart = 0,
                Context = "OCR\nowner@example.net",
                SourceRecordId = "translated-1",
                ParentRecordId = "ocr-1",
                SourceFile = @"C:\Evidence\statement.pdf",
                Location = new EnrichmentLocation
                {
                    Kind = "page_region",
                    Value = "page=3;x=10;y=20;w=30;h=10",
                },
                Origin = new EnrichmentOrigin
                {
                    Extractor = "paddleocr",
                    Version = "3.2.0",
                    Kind = "ocr",
                    Provider = "cuda",
                },
                Transform = new EnrichmentTransform
                {
                    Kind = "translation",
                    Engine = "llama.cpp",
                    EngineVersion = "1.0",
                    TargetLanguage = "en",
                },
                EvidenceClass = "derived-translation",
            },
        };
        await File.WriteAllLinesAsync(
            matchesPath,
            records.Select(record => JsonSerializer.Serialize(record, options)),
            cancellationToken
        );

        var stats = await ForensicReportCore.WriteAsync(
            matchesPath,
            findingsPath,
            patternHistogramPath,
            featureHistogramPath,
            visualizationPath,
            [
                ("email", BuiltInPatternCatalog.Patterns["email"]),
                ("jwt", BuiltInPatternCatalog.Patterns["jwt"]),
            ],
            cancellationToken
        );

        Assert.Equal(new ForensicReportStats(2, 2, 2), stats);
        var findings = await File.ReadAllLinesAsync(findingsPath, cancellationToken);
        Assert.Equal(3, findings.Length);
        var columnCount = findings[0].Split('\t').Length;
        Assert.All(findings, line => Assert.Equal(columnCount, line.Split('\t').Length));
        Assert.Contains("login analyst@example.com\\tactive", findings[1]);
        Assert.Contains("browser-credential-store", findings[1]);
        Assert.Contains("Google Chrome", findings[1]);
        Assert.Contains("Default", findings[1]);
        Assert.Contains("paddleocr -> llama.cpp:translation", findings[2]);
        Assert.Contains("page=3", findings[2]);

        var patternHistogram = await File.ReadAllTextAsync(patternHistogramPath, cancellationToken);
        Assert.Contains("email\tpii", patternHistogram);
        Assert.Contains("jwt\tcredential", patternHistogram);
        Assert.Contains("\t0\t0.0000\t", patternHistogram);
        var featureHistogram = await File.ReadAllTextAsync(featureHistogramPath, cancellationToken);
        Assert.Contains("n=1\t1\temail\tpii\tanalyst@example.com\tfalse", featureHistogram);
        Assert.Contains("n=1\t1\temail\tpii\towner@example.net\tfalse", featureHistogram);
        var visualization = await File.ReadAllTextAsync(visualizationPath, cancellationToken);
        Assert.Contains("Pattern histogram", visualization);
        Assert.Contains("analyst@example.com", findings[1]);
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.chunk"));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public void EscapeTsv_EscapesDelimiterLineBreakAndControlCharactersButPreservesPaths()
    {
        Assert.Equal(
            @"one\ttwo\r\nthree\four\u0001",
            ForensicReportCore.EscapeTsv("one\ttwo\r\nthree\\four\u0001")
        );
    }

    [Fact]
    public async Task WriteAsync_MergesRepeatedFeaturesAcrossSpilledHistogramChunks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var matchesPath = scope.PathFor("matches.jsonl");
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var records = new[] { "repeat@example.com", "other@example.com", "repeat@example.com" }
            .Select((match, index) => new EnrichmentRegexMatchRecord
            {
                PatternName = "email",
                Pattern = BuiltInPatternCatalog.Patterns["email"],
                Match = match,
                MatchStart = 0,
                MatchLength = match.Length,
                MatchLine = 1,
                SourceRecordId = $"record-{index}",
                SourceFile = $@"C:\Evidence\file-{index}.txt",
                EvidenceClass = "byte-native",
            });
        await File.WriteAllLinesAsync(
            matchesPath,
            records.Select(record => JsonSerializer.Serialize(record, options)),
            cancellationToken
        );

        var featureHistogramPath = scope.PathFor("feature-histogram.tsv");
        var stats = await ForensicReportCore.WriteAsync(
            matchesPath,
            scope.PathFor("findings.tsv"),
            scope.PathFor("pattern-histogram.tsv"),
            featureHistogramPath,
            scope.PathFor("pattern-histogram.html"),
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            cancellationToken,
            histogramChunkEntryLimit: 1
        );

        Assert.Equal(2, stats.FeatureRows);
        var histogram = await File.ReadAllTextAsync(featureHistogramPath, cancellationToken);
        Assert.Contains("n=2\t2\temail\tpii\trepeat@example.com\tfalse", histogram);
        Assert.Contains("n=1\t1\temail\tpii\tother@example.com\tfalse", histogram);
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.chunk"));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-report-tests-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string fileName) => Path.Combine(DirectoryPath, fileName);

        public void Dispose()
        {
            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
