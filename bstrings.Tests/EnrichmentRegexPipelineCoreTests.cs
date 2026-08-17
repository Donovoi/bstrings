using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class EnrichmentRegexPipelineCoreTests
{
    [Fact]
    public async Task ProcessAsync_ValidatesAndLabelsDecodingChildren()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("decoding.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                RawRecord("raw-1", "encoded parent"),
                DecodingRecord("decoded-1", "raw-1", "contact decoded@example.test"),
            ],
            cancellationToken
        );

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            cancellationToken: cancellationToken,
            trustedParentFirstInput: true
        );

        Assert.Equal(2, stats.InputRecords);
        Assert.Equal(1, stats.MatchRecords);
        Assert.Equal(0, stats.MatchCacheMisses);
        Assert.Equal(0, stats.MatchCacheStores);
        Assert.Equal(0, stats.MatchCacheProbationObservations);
        Assert.Equal(2, stats.MatchCachePreLookupBypasses);
        using var row = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath, cancellationToken));
        Assert.Equal("derived-decoding", row.RootElement.GetProperty("evidenceClass").GetString());
        Assert.Equal(
            BuiltInPatternCatalog.ByName["email"].Description,
            row.RootElement.GetProperty("patternDescription").GetString()
        );
        Assert.Equal(
            BuiltInPatternCatalog.ByName["email"].Source,
            row.RootElement.GetProperty("patternSource").GetString()
        );
        Assert.Equal(
            BuiltInPatternCatalog.GetValidationLabel(BuiltInPatternCatalog.ByName["email"]),
            row.RootElement.GetProperty("patternValidation").GetString()
        );
        Assert.Equal("raw-1", row.RootElement.GetProperty("parentRecordId").GetString());
        Assert.Equal(
            "decoding",
            row.RootElement.GetProperty("transform").GetProperty("kind").GetString()
        );
    }

    [Fact]
    public async Task ProcessAsync_TrustedStreamRejectsDecodingLineageMismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("decoding-lineage.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                RawRecord("raw-1", "encoded parent"),
                DecodingRecord(
                    "decoded-1",
                    "raw-1",
                    "contact decoded@example.test",
                    location: "0x11"
                ),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath: null,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                new StringWriter(),
                cancellationToken,
                trustedParentFirstInput: true
            )
        );

        Assert.Contains(
            "exact sourceFile, location, and origin",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task ProcessAsync_TrustedStreamRequiresTranslationsBeforeDecodingChildren()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("decoding-order.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                RawRecord("raw-1", "encoded parent"),
                DecodingRecord("decoded-1", "raw-1", "contact decoded@example.test"),
                CreateRecord("translated-1", "contact translated@example.test", "raw-1"),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath: null,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                new StringWriter(),
                cancellationToken,
                trustedParentFirstInput: true
            )
        );

        Assert.Contains("translated record after decoding", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("missing-attribute")]
    [InlineData("digest-mismatch")]
    [InlineData("wrong-depth")]
    [InlineData("record-limit")]
    [InlineData("profile-copy-mismatch")]
    public void ValidateRecord_RejectsIncompleteOrTamperedDecodingProvenance(string defect)
    {
        const string text = "contact decoded@example.test";
        var decodedBytes = Encoding.UTF8.GetBytes(text);
        var attributes = new Dictionary<string, JsonElement>
        {
            ["decoder"] = JsonSerializer.SerializeToElement("base64"),
            ["decoderProfile"] = JsonSerializer.SerializeToElement("rfc4648-base64-text-v1"),
            ["decoderPolicyVersion"] = JsonSerializer.SerializeToElement("decoder-policy-v1"),
            ["candidateStart"] = JsonSerializer.SerializeToElement(0),
            ["candidateLength"] = JsonSerializer.SerializeToElement(40),
            ["outerWhitespaceTreatment"] = JsonSerializer.SerializeToElement("none"),
            ["leadingWhitespaceCharacters"] = JsonSerializer.SerializeToElement(0),
            ["trailingWhitespaceCharacters"] = JsonSerializer.SerializeToElement(0),
            ["decodedByteLength"] = JsonSerializer.SerializeToElement(decodedBytes.Length),
            ["decodedSha256"] = JsonSerializer.SerializeToElement(
                Convert.ToHexString(SHA256.HashData(decodedBytes)).ToLowerInvariant()
            ),
            ["decodedCharset"] = JsonSerializer.SerializeToElement("utf-8"),
            ["decodeDepth"] = JsonSerializer.SerializeToElement(1),
            ["maxCandidateCharacters"] = JsonSerializer.SerializeToElement(16_384),
            ["maxDecodedBytesPerRecord"] = JsonSerializer.SerializeToElement(12_288),
            ["maxAttemptedCandidates"] = JsonSerializer.SerializeToElement(100_000),
            ["maxTotalDecodedBytes"] = JsonSerializer.SerializeToElement(67_108_864L),
        };
        switch (defect)
        {
            case "missing-attribute":
                attributes.Remove("decodedCharset");
                break;
            case "digest-mismatch":
                attributes["decodedSha256"] = JsonSerializer.SerializeToElement(new string('0', 64));
                break;
            case "wrong-depth":
                attributes["decodeDepth"] = JsonSerializer.SerializeToElement(2);
                break;
            case "record-limit":
                attributes["maxDecodedBytesPerRecord"] = JsonSerializer.SerializeToElement(1);
                break;
            case "profile-copy-mismatch":
                attributes["decoderProfile"] = JsonSerializer.SerializeToElement(
                    "powershell-encoded-command-v1"
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(defect));
        }
        var record = new EnrichmentStringRecord
        {
            SchemaVersion = 1,
            RecordType = "string",
            RecordId = "decoded-1",
            Text = text,
            SourceFile = "sample.exe",
            Location = new EnrichmentLocation { Kind = "file_offset", Value = "0x10" },
            Origin = new EnrichmentOrigin
            {
                Extractor = "bstrings",
                Version = "test",
                Kind = "static",
            },
            ParentRecordId = "raw-1",
            Transform = new EnrichmentTransform
            {
                Kind = "decoding",
                Engine = "bstrings",
                EngineVersion = "1.0.0",
                Profile = "rfc4648-base64-text-v1",
                PolicyVersion = "decoder-policy-v1",
                Outcome = "decoded-text",
            },
            Attributes = attributes,
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ValidateRecord(record, 1)
        );

        Assert.Contains("Decoding enrichment record", error.Message, StringComparison.Ordinal);
    }

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

        Assert.Equal(2, stats.InputRecords);
        Assert.Equal(1, stats.TranslatedRecords);
        Assert.Equal(2, stats.MatchRecords);
        Assert.Equal(0, stats.MatchCacheMisses);
        Assert.Equal(0, stats.MatchCacheStores);
        Assert.Equal(0, stats.MatchCacheProbationObservations);
        Assert.Equal(2, stats.MatchCachePreLookupBypasses);
        var rows = (await File.ReadAllLinesAsync(outputPath, cancellationToken))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal("analyst@example.com", rows[0].RootElement.GetProperty("match").GetString());
            Assert.Equal(8, rows[0].RootElement.GetProperty("matchStart").GetInt32());
            Assert.Equal(19, rows[0].RootElement.GetProperty("matchLength").GetInt32());
            Assert.Equal(1, rows[0].RootElement.GetProperty("matchLine").GetInt32());
            Assert.Equal(
                "contact analyst@example.com",
                rows[0].RootElement.GetProperty("context").GetString()
            );
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
    public async Task ProcessAsync_LabelsOcrOriginalsAndPreservesTheirModelIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("ocr.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            OcrRecord("ocr-1", "contact analyst@example.com", "revision-1"),
            cancellationToken
        );
        var output = new StringWriter();

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            output,
            cancellationToken
        );

        Assert.Equal(1, stats.MatchRecords);
        using var row = JsonDocument.Parse(output.ToString());
        Assert.Equal("derived-extractor", row.RootElement.GetProperty("evidenceClass").GetString());
        var origin = row.RootElement.GetProperty("origin");
        Assert.Equal("ocr", origin.GetProperty("kind").GetString());
        Assert.Equal("fixture/ocr-model", origin.GetProperty("model").GetString());
        Assert.Equal("revision-1", origin.GetProperty("revision").GetString());
        Assert.Equal(new string('a', 64), origin.GetProperty("modelSha256").GetString());
    }

    [Fact]
    public async Task ProcessAsync_TrustedStreamRejectsTranslationThatChangesOcrModelLineage()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("invalid-ocr-lineage.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                OcrRecord("ocr-1", "bonjour analyst", "revision-1"),
                OcrTranslation("translated-1", "ocr-1", "owner@example.com", "revision-2"),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath: null,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                new StringWriter(),
                cancellationToken,
                trustedParentFirstInput: true,
                translationRequirements: new TranslationValidationRequirements(
                    "llama.cpp",
                    "en",
                    "fixture/translation-model",
                    "translation-revision",
                    new string('b', 64)
                )
            )
        );

        Assert.Contains(
            "exact sourceFile, location, and origin",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Fact]
    public async Task ProcessAsync_CountsAndValidatesPreservationFallbacks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("fallback.jsonl");
        const string sourceText = "contact owner@example.com";
        await File.WriteAllLinesAsync(
            inputPath,
            [
                OcrRecord("ocr-1", sourceText, "revision-1"),
                OcrTranslation(
                    "translated-1",
                    "ocr-1",
                    sourceText,
                    "revision-1",
                    outcome: "unchanged",
                    integrity: "preservation-fallback",
                    integrityReason: "hard-identifier-retention-mismatch"
                ),
            ],
            cancellationToken
        );

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            new StringWriter(),
            cancellationToken,
            trustedParentFirstInput: true,
            translationRequirements: new TranslationValidationRequirements(
                "llama.cpp",
                "en",
                "fixture/translation-model",
                "translation-revision",
                new string('b', 64)
            )
        );

        Assert.Equal(1, stats.PreservationFallbackRecords);
    }

    [Fact]
    public async Task ProcessAsync_TrustedStreamRejectsFallbackThatDiffersFromParentText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("fallback-mismatch.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                OcrRecord("ocr-1", "retain CASE_TOKEN001 exactly", "revision-1"),
                OcrTranslation(
                    "translated-1",
                    "ocr-1",
                    "retain case_token001 exactly",
                    "revision-1",
                    outcome: "unchanged",
                    integrity: "preservation-fallback",
                    integrityReason: "hard-identifier-retention-mismatch"
                ),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath: null,
                [("token", "CASE_TOKEN001")],
                new StringWriter(),
                cancellationToken,
                trustedParentFirstInput: true,
                translationRequirements: new TranslationValidationRequirements(
                    "llama.cpp",
                    "en",
                    "fixture/translation-model",
                    "translation-revision",
                    new string('b', 64)
                )
            )
        );

        Assert.Contains("exact parent text", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-fallback-text.*"));
    }

    [Fact]
    public async Task ProcessAsync_FallbackIndexCleanupFailureBlocksOutputPublication()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("fallback-cleanup.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        const string sourceText = "retain CASE_TOKEN001 exactly";
        await File.WriteAllTextAsync(outputPath, "previous-result", cancellationToken);
        await File.WriteAllLinesAsync(
            inputPath,
            [
                OcrRecord("ocr-1", sourceText, "revision-1"),
                OcrTranslation(
                    "translated-1",
                    "ocr-1",
                    sourceText,
                    "revision-1",
                    outcome: "unchanged",
                    integrity: "preservation-fallback",
                    integrityReason: "hard-identifier-retention-mismatch"
                ),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath,
                [("token", "CASE_TOKEN001")],
                cancellationToken: cancellationToken,
                trustedParentFirstInput: true,
                translationRequirements: new TranslationValidationRequirements(
                    "llama.cpp",
                    "en",
                    "fixture/translation-model",
                    "translation-revision",
                    new string('b', 64)
                ),
                fallbackIndexDeleteDirectory: _ =>
                    throw new IOException("synthetic cleanup failure")
            )
        );

        Assert.Contains("not published", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("previous-result", await File.ReadAllTextAsync(outputPath, cancellationToken));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-fallback-text.*"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task ProcessAsync_TrustedStreamRequiresPositiveAmbiguousCount(int? count)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("ambiguous-count.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        await File.WriteAllTextAsync(outputPath, "previous-result", cancellationToken);
        await File.WriteAllLinesAsync(
            inputPath,
            [
                OcrRecord("ocr-1", "ordinary-hyphen language", "revision-1"),
                OcrTranslation(
                    "translated-1",
                    "ocr-1",
                    "ordinary language",
                    "revision-1",
                    integrity: "source-retained-ambiguous",
                    ambiguousIdentifierCount: count
                ),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath,
                [("ordinary", "ordinary")],
                cancellationToken: cancellationToken,
                trustedParentFirstInput: true,
                translationRequirements: new TranslationValidationRequirements(
                    "llama.cpp",
                    "en",
                    "fixture/translation-model",
                    "translation-revision",
                    new string('b', 64)
                )
            )
        );

        Assert.Contains("positive integer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("previous-result", await File.ReadAllTextAsync(outputPath, cancellationToken));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-fallback-text.*"));
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
    public async Task ProcessAsync_AssignsEndMatchAfterTrailingNewlineToEmptySecondLine()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("line.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            "{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"line-1\","
                + "\"text\":\"first\\n\",\"sourceFile\":\"sample.txt\","
                + "\"location\":{\"kind\":\"file_offset\",\"value\":\"0x0\"},"
                + "\"origin\":{\"extractor\":\"bstrings\",\"kind\":\"static\"}}",
            cancellationToken
        );
        var output = new StringWriter();

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [("end", @"\z")],
            output,
            cancellationToken
        );

        Assert.Equal(1, stats.MatchRecords);
        using var row = JsonDocument.Parse(output.ToString());
        Assert.Equal(6, row.RootElement.GetProperty("matchStart").GetInt32());
        Assert.Equal(0, row.RootElement.GetProperty("matchLength").GetInt32());
        Assert.Equal(2, row.RootElement.GetProperty("matchLine").GetInt32());
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
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"a@b.com\",\"sourceFile\":\"x\",\"location\":{\"kind\":\"page_region\",\"value\":\"page=1\"},\"origin\":{\"extractor\":\"ocr\",\"kind\":\"ocr\",\"model\":\"model-only\"}}", "complete origin model")]
    [InlineData("{\"schemaVersion\":1,\"recordType\":\"string\",\"recordId\":\"x\",\"text\":\"a@b.com\",\"sourceFile\":\"x\",\"location\":{\"kind\":\"page_region\",\"value\":\"page=1\"},\"origin\":{\"extractor\":\"ocr\",\"kind\":\"ocr\",\"model\":\"model\",\"revision\":\"rev\",\"modelSha256\":\"not-a-hash\"}}", "64-character modelSha256")]
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

    [Fact]
    public async Task ProcessAsync_MatchCachePreservesExactOutputAndRebuildsProvenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("duplicates.jsonl");
        var disabledPath = scope.PathFor("disabled.jsonl");
        var enabledPath = scope.PathFor("enabled.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        recordType = "string",
                        recordId = "raw-1",
                        text = "contact analyst@example.test",
                        sourceFile = "first.bin",
                        location = new { kind = "file_offset", value = "0x10" },
                        origin = new { extractor = "bstrings", version = "test", kind = "static" },
                        attributes = new { marker = "first" },
                    }
                ),
                JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        recordType = "string",
                        recordId = "raw-2",
                        text = "contact analyst@example.test",
                        sourceFile = "second.exe",
                        location = new { kind = "virtual_address", value = "0x402000" },
                        origin = new { extractor = "floss", version = "test", kind = "decoded" },
                        attributes = new { marker = "second" },
                    }
                ),
                CreateRecord(
                    "translated-1",
                    "contact analyst@example.test",
                    "raw-1"
                ),
            ],
            cancellationToken
        );

        var disabled = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            disabledPath,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            cancellationToken: cancellationToken,
            matchCacheOptions: MatchResultCacheOptions.Disabled
        );
        var enabled = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            enabledPath,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            cancellationToken: cancellationToken,
            matchCacheOptions: new MatchResultCacheOptions(8, 4096, 256, 8),
            matchCacheComparer: new ConstantHashOrdinalComparer()
        );

        Assert.Equal(
            await File.ReadAllBytesAsync(disabledPath, cancellationToken),
            await File.ReadAllBytesAsync(enabledPath, cancellationToken)
        );
        Assert.Equal(1, enabled.MatchCacheHits);
        Assert.Equal(2, enabled.MatchCacheMisses);
        Assert.Equal(1, enabled.MatchCacheStores);
        Assert.Equal(1, enabled.MatchCacheProbationObservations);
        Assert.Equal(1, enabled.ReusedPatternEvaluations);
        Assert.Equal(1, enabled.MatchRowsServedFromCache);
        Assert.Equal(2, enabled.MatchRowsComputed);
        Assert.Equal(3, disabled.MatchCachePreLookupBypasses);
        var rows = (await File.ReadAllLinesAsync(enabledPath, cancellationToken))
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            Assert.Equal("raw-1", rows[0].RootElement.GetProperty("sourceRecordId").GetString());
            Assert.Equal("raw-2", rows[1].RootElement.GetProperty("sourceRecordId").GetString());
            Assert.Equal("first.bin", rows[0].RootElement.GetProperty("sourceFile").GetString());
            Assert.Equal("second.exe", rows[1].RootElement.GetProperty("sourceFile").GetString());
            Assert.Equal(
                "0x402000",
                rows[1].RootElement.GetProperty("location").GetProperty("value").GetString()
            );
            Assert.Equal(
                "floss",
                rows[1].RootElement.GetProperty("origin").GetProperty("extractor").GetString()
            );
            Assert.Equal(
                "second",
                rows[1].RootElement.GetProperty("attributes").GetProperty("marker").GetString()
            );
            Assert.Equal(
                "translated-1",
                rows[2].RootElement.GetProperty("sourceRecordId").GetString()
            );
            Assert.Equal("raw-1", rows[2].RootElement.GetProperty("parentRecordId").GetString());
            Assert.Equal(
                "translation",
                rows[2].RootElement.GetProperty("transform").GetProperty("kind").GetString()
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
    public async Task ProcessAsync_MatchCacheStoresZeroMatchResults()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("zero-matches.jsonl");
        await File.WriteAllLinesAsync(
            inputPath,
            [
                RawRecord("raw-1", "ordinary text"),
                RawRecord("raw-2", "ordinary text"),
                RawRecord("raw-3", "ordinary text"),
            ],
            cancellationToken
        );

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            new StringWriter(),
            cancellationToken,
            matchCacheOptions: new MatchResultCacheOptions(8, 4096, 256, 8)
        );

        Assert.Equal(0, stats.MatchRecords);
        Assert.Equal(1, stats.MatchCacheHits);
        Assert.Equal(2, stats.MatchCacheMisses);
        Assert.Equal(1, stats.MatchCacheStores);
        Assert.Equal(1, stats.ReusedPatternEvaluations);
    }

    [Fact]
    public async Task ProcessAsync_MatchCacheDoesNotStoreDescriptorOverflow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("overflow.jsonl");
        const string text = "first@example.test second@example.test";
        await File.WriteAllLinesAsync(
            inputPath,
            [
                RawRecord("raw-1", text),
                RawRecord("raw-2", text),
                RawRecord("raw-3", text),
            ],
            cancellationToken
        );

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [("email", BuiltInPatternCatalog.Patterns["email"])],
            new StringWriter(),
            cancellationToken,
            matchCacheOptions: new MatchResultCacheOptions(8, 4096, 256, 1)
        );

        Assert.Equal(6, stats.MatchRecords);
        Assert.Equal(0, stats.MatchCacheHits);
        Assert.Equal(3, stats.MatchCacheMisses);
        Assert.Equal(0, stats.MatchCacheStores);
        Assert.Equal(3, stats.MatchCachePostComputationBypasses);
    }

    [Fact]
    public async Task ProcessAsync_MatchCacheReevaluatesTimeDependentDobPattern()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("mixed-cacheability.jsonl");
        const string text = "analyst@example.test DOB: 01/01/2000";
        await File.WriteAllLinesAsync(
            inputPath,
            [
                RawRecord("raw-1", text),
                RawRecord("raw-2", text),
                RawRecord("raw-3", text),
            ],
            cancellationToken
        );

        var stats = await EnrichmentRegexPipelineCore.ProcessAsync(
            inputPath,
            outputPath: null,
            [
                ("email", BuiltInPatternCatalog.Patterns["email"]),
                ("dob", BuiltInPatternCatalog.Patterns["dob"]),
            ],
            new StringWriter(),
            cancellationToken,
            matchCacheOptions: new MatchResultCacheOptions(8, 4096, 256, 8)
        );

        Assert.Equal(6, stats.MatchRecords);
        Assert.Equal(1, stats.MatchCacheHits);
        Assert.Equal(1, stats.ReusedPatternEvaluations);
        Assert.Equal(1, stats.MatchRowsServedFromCache);
        Assert.Equal(5, stats.MatchRowsComputed);
    }

    [Fact]
    public async Task ProcessAsync_PreCancelledCacheRunPreservesExistingOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var inputPath = scope.PathFor("cancelled.jsonl");
        var outputPath = scope.PathFor("matches.jsonl");
        await File.WriteAllTextAsync(
            inputPath,
            RawRecord("raw-1", "analyst@example.test"),
            cancellationToken
        );
        await File.WriteAllTextAsync(outputPath, "previous-result", cancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            EnrichmentRegexPipelineCore.ProcessAsync(
                inputPath,
                outputPath,
                [("email", BuiltInPatternCatalog.Patterns["email"])],
                cancellationToken: cancelled.Token
            )
        );

        Assert.Equal(
            "previous-result",
            await File.ReadAllTextAsync(outputPath, cancellationToken)
        );
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
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

    private sealed class ConstantHashOrdinalComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right) =>
            string.Equals(left, right, StringComparison.Ordinal);

        public int GetHashCode(string value) => 1;
    }

    private static string OcrRecord(string recordId, string text, string originRevision) =>
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "document.pdf",
                location = new { kind = "page_region", value = "page=1;x=10;y=20;w=30;h=40" },
                origin = new
                {
                    extractor = "fixture-ocr",
                    version = "1.0.0",
                    kind = "ocr",
                    model = "fixture/ocr-model",
                    revision = originRevision,
                    modelSha256 = new string('a', 64),
                },
            }
        );

    private static string RawRecord(string recordId, string text) =>
        JsonSerializer.Serialize(
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

    private static string DecodingRecord(
        string recordId,
        string parentRecordId,
        string text,
        string location = "0x10"
    )
    {
        var decodedBytes = Encoding.UTF8.GetBytes(text);
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "sample.exe",
                location = new { kind = "file_offset", value = location },
                origin = new { extractor = "bstrings", version = "test", kind = "static" },
                parentRecordId,
                transform = new
                {
                    kind = "decoding",
                    engine = "bstrings",
                    engineVersion = "1.0.0",
                    profile = "rfc4648-base64-text-v1",
                    policyVersion = "decoder-policy-v1",
                    outcome = "decoded-text",
                },
                attributes = new
                {
                    decoder = "base64",
                    decoderProfile = "rfc4648-base64-text-v1",
                    decoderPolicyVersion = "decoder-policy-v1",
                    candidateStart = 0,
                    candidateLength = 40,
                    outerWhitespaceTreatment = "none",
                    leadingWhitespaceCharacters = 0,
                    trailingWhitespaceCharacters = 0,
                    decodedByteLength = decodedBytes.Length,
                    decodedSha256 = Convert
                        .ToHexString(SHA256.HashData(decodedBytes))
                        .ToLowerInvariant(),
                    decodedCharset = "utf-8",
                    decodeDepth = 1,
                    maxCandidateCharacters = 16_384,
                    maxDecodedBytesPerRecord = 12_288,
                    maxAttemptedCandidates = 100_000,
                    maxTotalDecodedBytes = 67_108_864L,
                },
            }
        );
    }

    private static string OcrTranslation(
        string recordId,
        string parentRecordId,
        string text,
        string originRevision,
        string outcome = "translated",
        string integrity = "verified",
        string? integrityReason = null,
        int? ambiguousIdentifierCount = null
    )
    {
        var attributes = new Dictionary<string, object>
        {
            ["translationIntegrity"] = integrity,
        };
        if (integrityReason is not null)
        {
            attributes["translationIntegrityReason"] = integrityReason;
        }
        if (ambiguousIdentifierCount is not null)
        {
            attributes["translationAmbiguousIdentifierCount"] = ambiguousIdentifierCount.Value;
        }
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "document.pdf",
                location = new { kind = "page_region", value = "page=1;x=10;y=20;w=30;h=40" },
                origin = new
                {
                    extractor = "fixture-ocr",
                    version = "1.0.0",
                    kind = "ocr",
                    model = "fixture/ocr-model",
                    revision = originRevision,
                    modelSha256 = new string('a', 64),
                },
                parentRecordId,
                transform = new
                {
                    kind = "translation",
                    engine = "llama.cpp",
                    engineVersion = "fixture",
                    model = "fixture/translation-model",
                    revision = "translation-revision",
                    modelSha256 = new string('b', 64),
                    targetLanguage = "en",
                    outcome,
                },
                attributes,
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
