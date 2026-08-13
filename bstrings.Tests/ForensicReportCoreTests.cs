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
                    Outcome = "translated",
                },
                EvidenceClass = "derived-translation",
                Attributes = new Dictionary<string, JsonElement>
                {
                    ["translationIntegrity"] = JsonSerializer.SerializeToElement("verified"),
                },
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
        var header = findings[0].Split('\t');
        var translatedRow = findings[2].Split('\t');
        Assert.Equal(
            "verified",
            translatedRow[Array.IndexOf(header, "TranslationIntegrity")]
        );
        Assert.Contains(
            "\"translationIntegrity\":\"verified\"",
            translatedRow[Array.IndexOf(header, "AttributesJson")]
        );

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
    public async Task WriteAsync_ReportsDerivedDecodingAndDecoderChainWhenEnabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var matchesPath = scope.PathFor("decoded-matches.jsonl");
        var record = new EnrichmentRegexMatchRecord
        {
            PatternName = "email",
            Pattern = BuiltInPatternCatalog.Patterns["email"],
            Match = "decoded@example.test",
            MatchStart = 0,
            MatchLength = 20,
            MatchLine = 1,
            SourceRecordId = "decoded-1",
            ParentRecordId = "raw-1",
            SourceFile = "synthetic.bin",
            Location = new EnrichmentLocation { Kind = "file_offset", Value = "0x10" },
            Origin = new EnrichmentOrigin
            {
                Extractor = "bstrings",
                Version = "test",
                Kind = "static",
            },
            Transform = new EnrichmentTransform
            {
                Kind = "decoding",
                Engine = "bstrings",
                EngineVersion = "1.0.0",
                Profile = "rfc4648-base64-text-v1",
                PolicyVersion = "decoder-policy-v1",
                Outcome = "decoded-text",
            },
            EvidenceClass = "derived-decoding",
            Attributes = new Dictionary<string, JsonElement>
            {
                ["decoder"] = JsonSerializer.SerializeToElement("base64"),
                ["decoderProfile"] = JsonSerializer.SerializeToElement("rfc4648-base64-text-v1"),
            },
        };
        await File.WriteAllTextAsync(
            matchesPath,
            JsonSerializer.Serialize(
                record,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            ),
            cancellationToken
        );
        var findingsPath = scope.PathFor("findings.tsv");
        var histogramPath = scope.PathFor("pattern-histogram.tsv");

        var disabledError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ForensicReportCore.WriteAsync(
                matchesPath,
                scope.PathFor("disabled-findings.tsv"),
                scope.PathFor("disabled-pattern-histogram.tsv"),
                scope.PathFor("disabled-feature-histogram.tsv"),
                scope.PathFor("disabled-pattern-histogram.html"),
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                cancellationToken
            )
        );
        Assert.Contains("decoder report projection is disabled", disabledError.Message);

        await ForensicReportCore.WriteAsync(
            matchesPath,
            findingsPath,
            histogramPath,
            scope.PathFor("feature-histogram.tsv"),
            scope.PathFor("pattern-histogram.html"),
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            cancellationToken,
            includeDecodingEvidence: true
        );

        var findings = await File.ReadAllLinesAsync(findingsPath, cancellationToken);
        var header = findings[0].Split('\t');
        var row = findings[1].Split('\t');
        Assert.Equal("derived-decoding", row[Array.IndexOf(header, "EvidenceClass")]);
        Assert.Contains(
            "base64 -> bstrings:rfc4648-base64-text-v1",
            row[Array.IndexOf(header, "DecoderChain")]
        );
        var histogram = await File.ReadAllLinesAsync(histogramPath, cancellationToken);
        var histogramHeader = histogram[0].Split('\t');
        var histogramRow = histogram[1].Split('\t');
        Assert.Equal("1", histogramRow[Array.IndexOf(histogramHeader, "DerivedDecodingCount")]);
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

    [Theory]
    [InlineData("unselected-pattern")]
    [InlineData("expression-mismatch")]
    [InlineData("negative-range")]
    [InlineData("length-mismatch")]
    public async Task WriteAsync_RejectsRecordsThatDoNotMatchSelectedPatternAndRange(
        string defect
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var matchesPath = scope.PathFor("matches.jsonl");
        var record = new EnrichmentRegexMatchRecord
        {
            PatternName = "email",
            Pattern = BuiltInPatternCatalog.Patterns["email"],
            Match = "analyst@example.test",
            MatchStart = 4,
            MatchLength = 20,
            MatchLine = 1,
            SourceRecordId = "synthetic-1",
            SourceFile = "synthetic.txt",
            EvidenceClass = "byte-native",
        };
        record = defect switch
        {
            "unselected-pattern" => record with { PatternName = "jwt" },
            "expression-mismatch" => record with { Pattern = "not-the-selected-expression" },
            "negative-range" => record with { MatchStart = -1 },
            "length-mismatch" => record with { MatchLength = record.Match.Length - 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        await File.WriteAllTextAsync(
            matchesPath,
            JsonSerializer.Serialize(
                record,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
            ),
            cancellationToken
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ForensicReportCore.WriteAsync(
                matchesPath,
                scope.PathFor("findings.tsv"),
                scope.PathFor("pattern-histogram.tsv"),
                scope.PathFor("feature-histogram.tsv"),
                scope.PathFor("pattern-histogram.html"),
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                cancellationToken
            )
        );
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
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
