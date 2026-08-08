using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class OcrCompletionCoreTests
{
    private static readonly OcrValidationRequirements Requirements = new(
        "fixture-ocr",
        "1.0.0",
        "fixture/ocr-model",
        "revision-1",
        new string('a', 64),
        new string('b', 64),
        new string('c', 64),
        new string('d', 64),
        new string('e', 64),
        new string('f', 64),
        OcrWorkflowMode.Auto,
        OcrProvider.Auto,
        0
    );

    [Fact]
    public async Task ValidateAsync_AcceptsCompleteManifestBoundCoverageAndForensicRecords()
    {
        using var scope = new TemporaryDirectory();
        var document = scope.WriteSource("document.pdf", "pdf evidence");
        var unsupported = scope.WriteSource("sample.bin", "binary evidence");
        var input = await CreateInputAsync(scope, document, unsupported);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllLinesAsync(
            strings,
            [
                Original("ocr-1", document, "ocr", pageNumber: 1),
                Original("ocr-2", document, "pdf-text", pageNumber: 2),
            ],
            TestContext.Current.CancellationToken
        );
        await File.WriteAllLinesAsync(
            assessments,
            [
                Assessment(
                    document,
                    "processed",
                    pages: 2,
                    stringRecords: 2,
                    renderedPages: 1,
                    pdfTextRecords: 1,
                    ocrRecords: 1
                ),
                Assessment(
                    unsupported,
                    "not-applicable",
                    pages: 0,
                    stringRecords: 0,
                    renderedPages: 0,
                    pdfTextRecords: 0,
                    ocrRecords: 0
                ),
            ],
            TestContext.Current.CancellationToken
        );

        var stats = await ValidateAsync(scope, input, strings, assessments, Requirements);

        Assert.Equal(2, stats.InputFiles);
        Assert.Equal(1, stats.ProcessedFiles);
        Assert.Equal(1, stats.NotApplicableFiles);
        Assert.Equal(2, stats.Pages);
        Assert.Equal(2, stats.StringRecords);
        Assert.Equal("cpu", stats.ResolvedProvider);
        Assert.Equal(0, stats.RequestedThreads);
        Assert.Equal(1, stats.ResolvedThreadCounts["cpu"]);
        Assert.Equal(1, stats.ResolvedWorkerCounts["cpu"]);
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-provenance.*"));
    }

    [Fact]
    public async Task ValidateAsync_RejectsMissingAssessmentCoverage()
    {
        using var scope = new TemporaryDirectory();
        var first = scope.WriteSource("first.png", "first");
        var second = scope.WriteSource("second.png", "second");
        var input = await CreateInputAsync(scope, first, second);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllTextAsync(strings, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            assessments,
            Assessment(first, "not-applicable", 0, 0, 0, 0, 0),
            TestContext.Current.CancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, Requirements)
        );

        Assert.Contains("coverage ended", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsATransformedRecord()
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("image.png", "image");
        var input = await CreateInputAsync(scope, source);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllTextAsync(
            strings,
            Original(
                "ocr-1",
                source,
                "ocr",
                mutateRecord: record =>
                {
                    record["parentRecordId"] = "raw-1";
                    record["transform"] = new
                    {
                        kind = "translation",
                        engine = "fixture",
                        engineVersion = "1",
                        model = "fixture/model",
                        revision = "r1",
                        modelSha256 = new string('9', 64),
                        targetLanguage = "en",
                    };
                }
            ),
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            assessments,
            Assessment(source, "processed", 1, 1, 1, 0, 1),
            TestContext.Current.CancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, Requirements)
        );

        Assert.Contains("original extractor record", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsDuplicateRecordIds()
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("image.png", "image");
        var input = await CreateInputAsync(scope, source);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllLinesAsync(
            strings,
            [
                Original("duplicate", source, "ocr", pageNumber: 1),
                Original("duplicate", source, "ocr", pageNumber: 1),
            ],
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            assessments,
            Assessment(source, "processed", 1, 2, 1, 0, 2),
            TestContext.Current.CancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, Requirements)
        );

        Assert.Contains("repeats recordId", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContentAddress_ExactlyMatchesThePythonCrossLanguageFixture()
    {
        const string fixture =
            """{"numbers":[1.0,-0.0,1e-05,1e+16,0.95],"recordType":"string","schemaVersion":1,"sourceFile":"C:\\\\evidence\\\\画像.png","text":"café 東京 😀\nline","unicodeKeys":{"":"bmp","𐀀":"supplementary"}}""";

        Assert.Equal(
            "sha256:9269c0e134954a79ef092dbda4cc0345ebb26262496d4ba9b4d5b00918acf1b3",
            OcrCompletionCore.ComputeContentAddressedRecordId(fixture)
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsAStaleRecordIdAfterTextChanges()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: null,
            recordMutation: null,
            mutateRecord: null,
            mutateAfterRecordId: record => record["text"] = "changed after identity"
        );

        Assert.Contains("content address", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAnArbitraryUniqueSha256RecordId()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: null,
            recordMutation: null,
            mutateRecord: null,
            recordIdOverride: $"sha256:{new string('7', 64)}"
        );

        Assert.Contains("content address", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAssessmentThreadProvenanceThatChangesTheRequest()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: assessment => assessment["requestedThreads"] = 2,
            recordMutation: null
        );

        Assert.Contains("requestedThreads", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_RejectsRecordThreadCountsThatDifferFromTheAssessment()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: null,
            recordMutation: attributes =>
                attributes["resolvedThreadCounts"] = new Dictionary<string, int>
                {
                    ["cpu"] = 2,
                }
        );

        Assert.Contains("resolvedThreadCounts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAssessmentWithInvalidWorkerCounts()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: assessment =>
                assessment["resolvedWorkerCounts"] = new Dictionary<string, int>
                {
                    ["cpu"] = 0,
                },
            recordMutation: null
        );

        Assert.Contains("resolvedWorkerCounts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_RejectsRecordWorkerCountsThatDifferFromTheAssessment()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: null,
            recordMutation: attributes =>
                attributes["resolvedWorkerCounts"] = new Dictionary<string, int>
                {
                    ["cpu"] = 2,
                }
        );

        Assert.Contains("resolvedWorkerCounts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAssessmentReportedFailure()
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("image.png", "image");
        var input = await CreateInputAsync(scope, source);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllTextAsync(strings, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            assessments,
            Assessment(
                source,
                "failed",
                0,
                0,
                0,
                0,
                0,
                error: "decoder crashed"
            ),
            TestContext.Current.CancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, Requirements)
        );

        Assert.Contains("failed examination", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("decoder crashed", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("metal")]
    [InlineData("CUDA")]
    [InlineData("hybrid-")]
    [InlineData("hybrid-cuda--cpu")]
    [InlineData("hybrid-metal-cpu")]
    public async Task ValidateAsync_RejectsInvalidResolvedProviders(string provider)
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: assessment => assessment["provider"] = provider,
            recordMutation: attributes =>
            {
                attributes["provider"] = provider;
                attributes["executionProvider"] = "cpu";
            },
            originProvider: provider
        );

        Assert.Contains("unsupported resolved provider", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, "cuda")]
    [InlineData(2, "cpu")]
    [InlineData(3, "cuda")]
    [InlineData(4, "cuda")]
    public async Task ValidateAsync_RejectsProviderThatDoesNotSatisfyExplicitRequest(
        int requested,
        string resolved
    )
    {
        var requirements = Requirements with { RequestedProvider = (OcrProvider)requested };
        var error = await ValidateInvalidSingleAsync(
            requirements,
            assessmentMutation: assessment =>
            {
                assessment["provider"] = resolved;
                assessment["requestedProvider"] = OcrCompletionCore.ProviderArgument(
                    (OcrProvider)requested
                );
            },
            recordMutation: attributes =>
            {
                attributes["provider"] = resolved;
                attributes["requestedProvider"] = OcrCompletionCore.ProviderArgument(
                    (OcrProvider)requested
                );
                attributes["executionProvider"] = resolved;
            },
            originProvider: resolved
        );

        Assert.Contains("does not satisfy requested", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public async Task ValidateAsync_AcceptsDescriptiveHybridForAutoOrHybrid(int requested)
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("image.png", "image");
        var input = await CreateInputAsync(scope, source);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        var requestedProvider = (OcrProvider)requested;
        var requirements = Requirements with { RequestedProvider = requestedProvider };
        await File.WriteAllTextAsync(
            strings,
            Original(
                "ocr-1",
                source,
                "ocr",
                provider: "hybrid-cuda-cpu",
                requestedProvider: OcrCompletionCore.ProviderArgument(requestedProvider),
                mutateAttributes: attributes => attributes["executionProvider"] = "cuda"
            ),
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            assessments,
            Assessment(
                source,
                "processed",
                1,
                1,
                1,
                0,
                1,
                provider: "hybrid-cuda-cpu",
                requestedProvider: OcrCompletionCore.ProviderArgument(requestedProvider)
            ),
            TestContext.Current.CancellationToken
        );

        var stats = await ValidateAsync(scope, input, strings, assessments, requirements);

        Assert.Equal("hybrid-cuda-cpu", stats.ResolvedProvider);
    }

    [Fact]
    public async Task ValidateAsync_RejectsRecordProviderDifferentFromAssessment()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: assessment =>
            {
                assessment["provider"] = "cuda";
                assessment["resolvedThreadCounts"] = ThreadCounts("cuda", 0);
                assessment["resolvedWorkerCounts"] = WorkerCounts("cuda");
            },
            recordMutation: attributes =>
            {
                attributes["provider"] = "cpu";
                attributes["executionProvider"] = "cpu";
            },
            originProvider: "cpu"
        );

        Assert.Contains("expected 'cuda'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("sourceSha256")]
    [InlineData("sourceSize")]
    [InlineData("runtimeSha256")]
    [InlineData("modelPackSha256")]
    [InlineData("detectorSha256")]
    [InlineData("recognizerSha256")]
    [InlineData("classifierSha256")]
    [InlineData("dictionarySha256")]
    [InlineData("requestedProvider")]
    [InlineData("mode")]
    public async Task ValidateAsync_RejectsMismatchedAssessmentProvenance(string field)
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: assessment =>
                assessment[field] = field == "sourceSize" ? 999L : "invalid",
            recordMutation: null
        );

        Assert.True(
            error.Message.Contains("trusted input manifest", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("verified OCR runtime", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("requestedProvider", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("mode", StringComparison.OrdinalIgnoreCase),
            error.Message
        );
    }

    [Theory]
    [InlineData("sourceSha256")]
    [InlineData("sourceSize")]
    [InlineData("runtimeSha256")]
    [InlineData("modelPackSha256")]
    [InlineData("detectorSha256")]
    [InlineData("recognizerSha256")]
    [InlineData("classifierSha256")]
    [InlineData("dictionarySha256")]
    [InlineData("requestedProvider")]
    [InlineData("provider")]
    public async Task ValidateAsync_RejectsMismatchedRecordProvenance(string field)
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: null,
            recordMutation: attributes =>
                attributes[field] = field == "sourceSize" ? 999L : "invalid"
        );

        Assert.Contains("attribute", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsMismatchedOriginModelIdentity()
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: null,
            recordMutation: null,
            mutateRecord: record =>
                ((Dictionary<string, object?>)record["origin"]!)["model"] =
                    "unverified/model"
        );

        Assert.Contains("verified OCR", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("classified-total")]
    [InlineData("observed-kind")]
    [InlineData("rendered-pages")]
    public async Task ValidateAsync_RejectsInconsistentAssessmentCounts(string scenario)
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: assessment =>
            {
                if (scenario == "classified-total")
                {
                    assessment["pdfTextRecords"] = 1L;
                }
                else if (scenario == "observed-kind")
                {
                    assessment["pdfTextRecords"] = 1L;
                    assessment["ocrRecords"] = 0L;
                }
                else
                {
                    assessment["renderedPages"] = 0L;
                }
            },
            recordMutation: null
        );

        Assert.Contains(
            scenario == "observed-kind" ? "record-kind counts" : "Records",
            error.Message,
            StringComparison.OrdinalIgnoreCase
        );
    }

    [Theory]
    [InlineData("outside-box")]
    [InlineData("degenerate-box")]
    [InlineData("bad-confidence")]
    [InlineData("bad-page")]
    [InlineData("bad-location")]
    [InlineData("bad-render-hash")]
    [InlineData("bad-lane")]
    [InlineData("bad-area")]
    public async Task ValidateAsync_RejectsInvalidOcrGeometryOrLane(string scenario)
    {
        var error = await ValidateInvalidSingleAsync(
            Requirements,
            assessmentMutation: null,
            recordMutation: attributes =>
            {
                switch (scenario)
                {
                    case "outside-box":
                        attributes["box"] = Box(0, 0, 200, 100);
                        break;
                    case "degenerate-box":
                        attributes["box"] = Box(1, 1, 1, 1);
                        break;
                    case "bad-confidence":
                        attributes["confidence"] = 1.1;
                        break;
                    case "bad-page":
                        attributes["pageNumber"] = 2L;
                        break;
                    case "bad-render-hash":
                        attributes["pageSha256"] = new string('8', 64);
                        break;
                    case "bad-lane":
                        attributes["executionProvider"] = "cuda";
                        break;
                    case "bad-area":
                        attributes["pixelWidth"] = 100_001L;
                        attributes["pixelHeight"] = 100_001L;
                        break;
                }
            },
            mutateRecord: scenario == "bad-location"
                ? record =>
                    ((Dictionary<string, object?>)record["location"]!)["value"] =
                        "page=00000001;box=0,0,1,0,1,1,0,1;source=ocr"
                : null
        );

        Assert.True(
            error.Message.Contains("box", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("confidence", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("page", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("location", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("executionProvider", StringComparison.OrdinalIgnoreCase)
                || error.Message.Contains("dimensions", StringComparison.OrdinalIgnoreCase),
            error.Message
        );
    }

    [Theory]
    [InlineData("missing-extractor")]
    [InlineData("confidence")]
    [InlineData("bad-coordinate-space")]
    public async Task ValidateAsync_RejectsInvalidPdfTextProvenance(string scenario)
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("document.pdf", "pdf");
        var input = await CreateInputAsync(scope, source);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllTextAsync(
            strings,
            Original(
                "pdf-1",
                source,
                "pdf-text",
                mutateAttributes: attributes =>
                {
                    if (scenario == "missing-extractor")
                    {
                        attributes.Remove("textLayerExtractor");
                    }
                    else if (scenario == "confidence")
                    {
                        attributes["confidence"] = 0.5;
                    }
                    else
                    {
                        attributes["coordinateSpace"] = "render-pixels";
                    }
                }
            ),
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            assessments,
            Assessment(source, "processed", 1, 1, 0, 1, 0),
            TestContext.Current.CancellationToken
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, Requirements)
        );
    }

    [Fact]
    public async Task ValidateAsync_RejectsChangedInputManifestWithoutRehashingEvidence()
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("image.png", "image");
        var input = await CreateInputAsync(scope, source);
        await File.AppendAllTextAsync(
            input.ManifestPath,
            " ",
            TestContext.Current.CancellationToken
        );
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllTextAsync(strings, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            assessments,
            Assessment(source, "not-applicable", 0, 0, 0, 0, 0),
            TestContext.Current.CancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, Requirements)
        );

        Assert.Contains("manifest SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsManifestRowMissingExplicitSchemaVersion()
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("image.png", "image");
        var input = await CreateInputAsync(scope, source);
        var text = await File.ReadAllTextAsync(
            input.ManifestPath,
            TestContext.Current.CancellationToken
        );
        text = text.Replace("\"schemaVersion\":1,", string.Empty, StringComparison.Ordinal);
        await File.WriteAllTextAsync(
            input.ManifestPath,
            text,
            TestContext.Current.CancellationToken
        );
        input = input with
        {
            Info = input.Info with { ManifestSha256 = FileSha256(input.ManifestPath) },
        };
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllTextAsync(strings, string.Empty, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            assessments,
            Assessment(source, "not-applicable", 0, 0, 0, 0, 0),
            TestContext.Current.CancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, Requirements)
        );

        Assert.Contains("invalid identity fields", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateValidationRequirements_BindsRuntimeAndEveryModelComponent()
    {
        using var scope = new TemporaryDirectory();
        var toolchain = CreateOcrToolchain(scope);

        var requirements = OcrCompletionCore.CreateValidationRequirements(
            toolchain,
            OcrWorkflowMode.Force,
            OcrProvider.DirectMl,
            3
        );

        Assert.Equal(FileSha256(toolchain.OcrExecutable!), requirements.RuntimeSha256);
        Assert.Equal(FileSha256(scope.PathFor("model/detector.onnx")), requirements.DetectorSha256);
        Assert.Equal(FileSha256(scope.PathFor("model/recognizer.onnx")), requirements.RecognizerSha256);
        Assert.Equal(FileSha256(scope.PathFor("model/classifier.onnx")), requirements.ClassifierSha256);
        Assert.Equal(FileSha256(scope.PathFor("model/dictionary.txt")), requirements.DictionarySha256);
        Assert.Equal(OcrWorkflowMode.Force, requirements.RequestedMode);
        Assert.Equal(OcrProvider.DirectMl, requirements.RequestedProvider);
        Assert.Equal(3, requirements.RequestedThreads);
    }

    [Theory]
    [InlineData(false, "SHA-256 mismatch")]
    [InlineData(true, "non-canonical relative path")]
    public void CreateValidationRequirements_RejectsComponentHashOrTraversal(
        bool traversal,
        string expectedMessage
    )
    {
        using var scope = new TemporaryDirectory();
        var toolchain = CreateOcrToolchain(
            scope,
            detectorPath: traversal ? "../outside.onnx" : "detector.onnx",
            detectorSha256: traversal ? null : new string('0', 64)
        );

        var error = Assert.Throws<InvalidDataException>(() =>
            OcrCompletionCore.CreateValidationRequirements(
                toolchain,
                OcrWorkflowMode.Auto,
                OcrProvider.Auto,
                0
            )
        );

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateOcrSummary_RecordsRequestedAndResolvedProvider()
    {
        var options = CreateOptions(OcrProvider.DirectMl, ocrThreads: 3);
        var requirements = Requirements with
        {
            RequestedProvider = OcrProvider.DirectMl,
            RequestedThreads = 3,
        };
        var summary = AnalysisOrchestrator.CreateOcrSummary(
            options,
            requirements,
            new OcrCompletionStats(
                3,
                2,
                1,
                4,
                5,
                "directml",
                3,
                new SortedDictionary<string, int>(StringComparer.Ordinal)
                {
                    ["directml"] = 3,
                },
                new SortedDictionary<string, int>(StringComparer.Ordinal)
                {
                    ["directml"] = 1,
                }
            )
        );

        Assert.Equal(OcrProvider.DirectMl, summary.RequestedProvider);
        Assert.Equal("directml", summary.ResolvedProvider);
        Assert.Equal(3, summary.RequestedThreads);
        Assert.Equal(3, summary.ResolvedThreadCounts["directml"]);
        Assert.Equal(1, summary.ResolvedWorkerCounts["directml"]);
        Assert.Equal(5, summary.StringRecords);
    }

    private static async Task<InvalidDataException> ValidateInvalidSingleAsync(
        OcrValidationRequirements requirements,
        Action<Dictionary<string, object?>>? assessmentMutation,
        Action<Dictionary<string, object?>>? recordMutation,
        string originProvider = "cpu",
        Action<Dictionary<string, object?>>? mutateRecord = null,
        string? recordIdOverride = null,
        Action<Dictionary<string, object?>>? mutateAfterRecordId = null
    )
    {
        using var scope = new TemporaryDirectory();
        var source = scope.WriteSource("image.png", "image");
        var input = await CreateInputAsync(scope, source);
        var strings = scope.PathFor("ocr-strings.jsonl");
        var assessments = scope.PathFor("ocr-assessments.jsonl");
        await File.WriteAllTextAsync(
            strings,
            Original(
                "ocr-1",
                source,
                "ocr",
                provider: originProvider,
                requestedProvider: OcrCompletionCore.ProviderArgument(
                    requirements.RequestedProvider
                ),
                requestedThreads: requirements.RequestedThreads,
                mutateAttributes: recordMutation,
                mutateRecord: mutateRecord,
                recordIdOverride: recordIdOverride,
                mutateAfterRecordId: mutateAfterRecordId
            ),
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            assessments,
            Assessment(
                source,
                "processed",
                1,
                1,
                1,
                0,
                1,
                provider: originProvider,
                requestedProvider: OcrCompletionCore.ProviderArgument(
                    requirements.RequestedProvider
                ),
                requestedThreads: requirements.RequestedThreads,
                mutate: assessmentMutation
            ),
            TestContext.Current.CancellationToken
        );

        return await Assert.ThrowsAsync<InvalidDataException>(() =>
            ValidateAsync(scope, input, strings, assessments, requirements)
        );
    }

    private static async Task<OcrCompletionStats> ValidateAsync(
        TemporaryDirectory scope,
        InputFixture input,
        string strings,
        string assessments,
        OcrValidationRequirements requirements
    ) =>
        await OcrCompletionCore.ValidateAsync(
            input.InventoryPath,
            input.ManifestPath,
            input.Info,
            strings,
            assessments,
            scope.DirectoryPath,
            input.Info.FileCount,
            requirements,
            TestContext.Current.CancellationToken
        );

    private static async Task<InputFixture> CreateInputAsync(
        TemporaryDirectory scope,
        params string[] sources
    )
    {
        var inventory = scope.PathFor("input-files.txt");
        var manifest = scope.PathFor("input-manifest.jsonl");
        var info = await InputEvidenceManifest.CreateAsync(
            inventory,
            manifest,
            sources,
            TestContext.Current.CancellationToken
        );
        return new InputFixture(inventory, manifest, info);
    }

    private static string Assessment(
        string sourceFile,
        string status,
        long pages,
        long stringRecords,
        long renderedPages,
        long pdfTextRecords,
        long ocrRecords,
        string? error = null,
        string provider = "cpu",
        string requestedProvider = "auto",
        int requestedThreads = 0,
        Action<Dictionary<string, object?>>? mutate = null
    )
    {
        var row = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["recordType"] = "ocr-assessment",
            ["sourceFile"] = sourceFile,
            ["status"] = status,
            ["pages"] = pages,
            ["stringRecords"] = stringRecords,
            ["engine"] = Requirements.Engine,
            ["engineVersion"] = Requirements.EngineVersion,
            ["model"] = Requirements.Model,
            ["revision"] = Requirements.Revision,
            ["modelSha256"] = Requirements.ModelSha256,
            ["modelPackSha256"] = Requirements.ModelSha256,
            ["detectorSha256"] = Requirements.DetectorSha256,
            ["recognizerSha256"] = Requirements.RecognizerSha256,
            ["classifierSha256"] = Requirements.ClassifierSha256,
            ["dictionarySha256"] = Requirements.DictionarySha256,
            ["sourceSha256"] = FileSha256(sourceFile),
            ["sourceSize"] = new FileInfo(sourceFile).Length,
            ["runtimeSha256"] = Requirements.RuntimeSha256,
            ["mode"] = "auto",
            ["requestedProvider"] = requestedProvider,
            ["provider"] = provider,
            ["requestedThreads"] = requestedThreads,
            ["resolvedThreadCounts"] = ThreadCounts(provider, requestedThreads),
            ["resolvedWorkerCounts"] = WorkerCounts(provider),
            ["dpi"] = 300,
            ["renderedPages"] = renderedPages,
            ["pdfTextRecords"] = pdfTextRecords,
            ["ocrRecords"] = ocrRecords,
        };
        if (error is not null)
        {
            row["error"] = error;
        }
        mutate?.Invoke(row);
        return JsonSerializer.Serialize(row);
    }

    private static string Original(
        string _recordId,
        string sourceFile,
        string originKind,
        long pageNumber = 1,
        string provider = "cpu",
        string requestedProvider = "auto",
        int requestedThreads = 0,
        Action<Dictionary<string, object?>>? mutateAttributes = null,
        Action<Dictionary<string, object?>>? mutateRecord = null,
        string? recordIdOverride = null,
        Action<Dictionary<string, object?>>? mutateAfterRecordId = null
    )
    {
        var isPdfText = originKind == "pdf-text";
        var box = Box(0, 0, 100, 100);
        var attributes = new Dictionary<string, object?>
        {
            ["sourceSha256"] = FileSha256(sourceFile),
            ["sourceSize"] = new FileInfo(sourceFile).Length,
            ["pageNumber"] = pageNumber,
            ["box"] = box,
            ["modelPackSha256"] = Requirements.ModelSha256,
            ["runtimeSha256"] = Requirements.RuntimeSha256,
            ["requestedProvider"] = requestedProvider,
            ["provider"] = provider,
            ["executionProvider"] = "cpu",
            ["requestedThreads"] = requestedThreads,
            ["resolvedThreadCounts"] = ThreadCounts(provider, requestedThreads),
            ["resolvedWorkerCounts"] = WorkerCounts(provider),
            ["detectorSha256"] = Requirements.DetectorSha256,
            ["recognizerSha256"] = Requirements.RecognizerSha256,
            ["classifierSha256"] = Requirements.ClassifierSha256,
            ["dictionarySha256"] = Requirements.DictionarySha256,
        };
        if (isPdfText)
        {
            attributes["coordinateSpace"] = "pdf-points";
            attributes["pageWidthPoints"] = 100;
            attributes["pageHeightPoints"] = 100;
            attributes["textLayerExtractor"] = "pypdfium2";
            attributes["textLayerExtractorVersion"] = "4.30.0";
        }
        else
        {
            attributes["confidence"] = 0.95;
            attributes["renderSha256"] = new string('1', 64);
            attributes["pageSha256"] = new string('1', 64);
            attributes["coordinateSpace"] = "render-pixels";
            attributes["pixelWidth"] = 100;
            attributes["pixelHeight"] = 100;
        }
        mutateAttributes?.Invoke(attributes);
        var coordinates = "0,0,100,0,100,100,0,100";
        var location = new Dictionary<string, object?>
        {
            ["kind"] = isPdfText ? "page_region" : "image_region",
            ["value"] = $"page={pageNumber:D8};box={coordinates};source={originKind}",
        };
        var row = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["recordType"] = "string",
            ["text"] = "contact analyst@example.com",
            ["sourceFile"] = sourceFile,
            ["location"] = location,
            ["origin"] = new Dictionary<string, object?>
            {
                ["extractor"] = Requirements.Engine,
                ["version"] = Requirements.EngineVersion,
                ["kind"] = originKind,
                ["model"] = Requirements.Model,
                ["revision"] = Requirements.Revision,
                ["modelSha256"] = Requirements.ModelSha256,
                ["provider"] = provider,
            },
            ["attributes"] = attributes,
        };
        mutateRecord?.Invoke(row);
        row["recordId"] =
            recordIdOverride
            ?? OcrCompletionCore.ComputeContentAddressedRecordId(JsonSerializer.Serialize(row));
        mutateAfterRecordId?.Invoke(row);
        return JsonSerializer.Serialize(row);
    }

    private static Dictionary<string, int> ThreadCounts(string provider, int requestedThreads)
    {
        var resolved = requestedThreads == 0 ? 1 : requestedThreads;
        return provider switch
        {
            "cpu" => new(StringComparer.Ordinal) { ["cpu"] = resolved },
            "cuda" => new(StringComparer.Ordinal) { ["cuda"] = resolved },
            "directml" => new(StringComparer.Ordinal) { ["directml"] = resolved },
            "hybrid-cuda-cpu" => new(StringComparer.Ordinal)
            {
                ["cpu"] = resolved,
                ["cuda"] = requestedThreads == 0 ? 1 : resolved,
            },
            "hybrid-directml-cpu" => new(StringComparer.Ordinal)
            {
                ["cpu"] = resolved,
                ["directml"] = requestedThreads == 0 ? 1 : resolved,
            },
            _ => new(StringComparer.Ordinal) { [provider] = resolved },
        };
    }

    private static Dictionary<string, int> WorkerCounts(string provider) =>
        provider switch
        {
            "cpu" => new(StringComparer.Ordinal) { ["cpu"] = 1 },
            "cuda" => new(StringComparer.Ordinal) { ["cuda"] = 1 },
            "directml" => new(StringComparer.Ordinal) { ["directml"] = 1 },
            "hybrid-cuda-cpu" => new(StringComparer.Ordinal) { ["cpu"] = 1, ["cuda"] = 1 },
            "hybrid-directml-cpu" => new(StringComparer.Ordinal)
            {
                ["cpu"] = 1,
                ["directml"] = 1,
            },
            _ => new(StringComparer.Ordinal) { [provider] = 1 },
        };

    private static double[][] Box(double left, double top, double right, double bottom) =>
        [[left, top], [right, top], [right, bottom], [left, bottom]];

    private static string FileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static AnalysisToolchain CreateOcrToolchain(
        TemporaryDirectory scope,
        string detectorPath = "detector.onnx",
        string? detectorSha256 = null
    )
    {
        var modelDirectory = scope.PathFor("model");
        Directory.CreateDirectory(modelDirectory);
        var detector = scope.PathFor("model/detector.onnx");
        var recognizer = scope.PathFor("model/recognizer.onnx");
        var classifier = scope.PathFor("model/classifier.onnx");
        var dictionary = scope.PathFor("model/dictionary.txt");
        File.WriteAllText(detector, "detector");
        File.WriteAllText(recognizer, "recognizer");
        File.WriteAllText(classifier, "classifier");
        File.WriteAllText(dictionary, "dictionary");
        File.WriteAllText(scope.PathFor("outside.onnx"), "outside");
        var modelManifest = scope.PathFor("model/model.pack.json");
        File.WriteAllText(
            modelManifest,
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    modelId = Requirements.Model,
                    revision = Requirements.Revision,
                    detector = new
                    {
                        path = detectorPath,
                        sha256 = detectorSha256
                            ?? (detectorPath == "../outside.onnx"
                                ? FileSha256(scope.PathFor("outside.onnx"))
                                : FileSha256(detector)),
                    },
                    recognizer = new
                    {
                        path = "recognizer.onnx",
                        sha256 = FileSha256(recognizer),
                    },
                    classifier = new
                    {
                        path = "classifier.onnx",
                        sha256 = FileSha256(classifier),
                    },
                    dictionary = new
                    {
                        path = "dictionary.txt",
                        sha256 = FileSha256(dictionary),
                    },
                }
            )
        );
        var runtime = scope.PathFor("ocr-runtime.exe");
        File.WriteAllText(runtime, "runtime");
        return new AnalysisToolchain(
            BundleRoot: scope.DirectoryPath,
            BundleIntegrity: new BundleIntegrity(
                "airgap-manifest.json",
                new string('1', 64),
                1,
                1,
                "bstrings.exe",
                new string('2', 64)
            ),
            PythonExecutable: "python.exe",
            EnrichmentAdapter: "enrich.py",
            OcrPythonExecutable: "python.exe",
            OcrExecutable: runtime,
            OcrAdapter: "ocr.py",
            OcrEngine: Requirements.Engine,
            OcrEngineVersion: Requirements.EngineVersion,
            OcrModelPath: modelManifest,
            OcrModelId: Requirements.Model,
            OcrModelRevision: Requirements.Revision,
            OcrModelSha256: FileSha256(modelManifest),
            MagikaExecutable: null,
            FlossExecutable: null,
            LlamaServer: null,
            TranslationModelPath: null,
            TranslationModelId: null,
            TranslationModelRevision: null,
            TranslationModelSha256: null
        );
    }

    private static AnalysisOptions CreateOptions(OcrProvider provider, int ocrThreads = 0) =>
        new(
            FilePath: "evidence.bin",
            DirectoryPath: null,
            Mask: null,
            OutputDirectory: "results",
            Full: false,
            OcrMode: OcrWorkflowMode.Auto,
            OcrProvider: provider,
            OcrThreads: ocrThreads,
            RecoveryMode: ExecutableRecoveryMode.Off,
            TranslationMode: TranslationWorkflowMode.Off,
            LanguageDetectionMode: LanguageDetectionMode.Adaptive,
            TranslationPolicy: LanguageTriagePolicy.HighRecall,
            LanguageConfidence: 0.55,
            LanguageMargin: 0.10,
            TranslationTarget: "en",
            TranslationDevice: "cpu",
            TranslationParallelism: 0,
            TranslationThreads: 0,
            TranslationGpuLayers: -1,
            TranslationStrictDeterminism: false,
            PatternSelection: "all",
            RegexFilePath: null,
            Processor: "cpu",
            CpuEngine: "dotnet",
            MinimumStringLength: 3,
            MaximumStringLength: 4096,
            TranslationMinimumCharacters: 8,
            TranslationMaximumCharacters: 512,
            BundleRoot: null,
            Airgap: false
        );

    private sealed record InputFixture(
        string InventoryPath,
        string ManifestPath,
        InputManifestInfo Info
    );

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-ocr-completion-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

        internal string WriteSource(string name, string content)
        {
            var path = PathFor(name);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
