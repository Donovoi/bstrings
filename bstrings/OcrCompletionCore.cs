#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal sealed record OcrAssessmentRecord
{
    public int SchemaVersion { get; init; }
    public string RecordType { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long? Pages { get; init; }
    public long? StringRecords { get; init; }
    public string Engine { get; init; } = string.Empty;
    public string EngineVersion { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Revision { get; init; } = string.Empty;
    public string ModelSha256 { get; init; } = string.Empty;
    public string ModelPackSha256 { get; init; } = string.Empty;
    public string DetectorSha256 { get; init; } = string.Empty;
    public string RecognizerSha256 { get; init; } = string.Empty;
    public string ClassifierSha256 { get; init; } = string.Empty;
    public string DictionarySha256 { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public long? SourceSize { get; init; }
    public string RuntimeSha256 { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string RequestedProvider { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public int? RequestedThreads { get; init; }
    public Dictionary<string, int>? ResolvedThreadCounts { get; init; }
    public Dictionary<string, int>? ResolvedWorkerCounts { get; init; }
    public int? Dpi { get; init; }
    public long? RenderedPages { get; init; }
    public long? PdfTextRecords { get; init; }
    public long? OcrRecords { get; init; }
    public string? Error { get; init; }
}

internal sealed record OcrInputManifestRecord
{
    public int? SchemaVersion { get; init; }
    public string? Path { get; init; }
    public long? Length { get; init; }
    public string? Sha256 { get; init; }
}

internal readonly record struct OcrCompletionStats(
    long InputFiles,
    long ProcessedFiles,
    long NotApplicableFiles,
    long Pages,
    long StringRecords,
    string? ResolvedProvider,
    int RequestedThreads,
    IReadOnlyDictionary<string, int> ResolvedThreadCounts,
    IReadOnlyDictionary<string, int> ResolvedWorkerCounts
);

internal static class OcrCompletionCore
{
    internal const int MaximumSessionThreads = 256;
    private const int MaximumAssessmentLineCharacters = 256 * 1024;
    private const int MaximumInputManifestLineCharacters = 256 * 1024;
    private const long MaximumModelManifestBytes = 1024 * 1024;
    private const long MaximumPagesPerInput = 10_000;
    private const double MaximumRasterPixels = 100_000_000;
    private const double MaximumPdfPointArea = 1_000_000_000_000;
    private const double GeometryTolerance = 0.0001;
    private const double RasterBoundsTolerance = 2.0;
    private const int MaximumCanonicalNumberCharacters = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static readonly JsonSerializerOptions AssessmentJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };
    private static readonly JsonSerializerOptions InputManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
    };

    internal static OcrValidationRequirements CreateValidationRequirements(
        AnalysisToolchain toolchain,
        OcrWorkflowMode requestedMode,
        OcrProvider requestedProvider,
        int requestedThreads
    )
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        if (requestedMode is not (OcrWorkflowMode.Auto or OcrWorkflowMode.Force))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        }
        _ = ProviderArgument(requestedProvider);
        ValidateRequestedThreads(requestedThreads);

        var modelPath = RequireFile(toolchain.OcrModelPath!, "OCR model-pack manifest");
        var expectedModelSha256 = RequireLowerSha256(
            toolchain.OcrModelSha256,
            "configured OCR model-pack"
        );
        var modelManifestBytes = ReadBoundedFile(modelPath, MaximumModelManifestBytes);
        var actualModelSha256 = Convert
            .ToHexString(SHA256.HashData(modelManifestBytes))
            .ToLowerInvariant();
        if (!HashesEqual(actualModelSha256, expectedModelSha256))
        {
            throw new InvalidDataException(
                $"OCR model SHA-256 mismatch for '{modelPath}'. Expected {expectedModelSha256}, found {actualModelSha256}."
            );
        }

        using var document = JsonDocument.Parse(
            modelManifestBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8,
            }
        );
        var root = ReadUniqueProperties(
            document.RootElement,
            "OCR model-pack manifest",
            [
                "schemaVersion",
                "modelId",
                "revision",
                "detector",
                "recognizer",
                "classifier",
                "dictionary",
            ]
        );
        if (
            !root.TryGetValue("schemaVersion", out var schema)
            || schema.ValueKind != JsonValueKind.Number
            || !schema.TryGetInt32(out var schemaVersion)
            || schemaVersion != 1
        )
        {
            throw new InvalidDataException("OCR model-pack manifest must use schema version 1.");
        }
        var modelId = RequireJsonString(root, "modelId", "OCR model-pack manifest");
        var revision = RequireJsonString(root, "revision", "OCR model-pack manifest");
        if (
            !string.Equals(modelId, toolchain.OcrModelId, StringComparison.Ordinal)
            || !string.Equals(revision, toolchain.OcrModelRevision, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                "OCR model-pack identity does not match the verified bundle configuration."
            );
        }

        var modelRoot = Path.GetDirectoryName(modelPath)!;
        var detector = ReadAndVerifyComponent(root, "detector", ".onnx", modelRoot);
        var recognizer = ReadAndVerifyComponent(root, "recognizer", ".onnx", modelRoot);
        var classifier = ReadAndVerifyComponent(root, "classifier", ".onnx", modelRoot);
        var dictionary = ReadAndVerifyComponent(
            root,
            "dictionary",
            [".txt", ".dict"],
            modelRoot
        );
        var runtimePath = RequireFile(toolchain.OcrExecutable!, "OCR runtime executable");

        var requirements = new OcrValidationRequirements(
            RequireBoundedText(toolchain.OcrEngine, "OCR engine"),
            RequireBoundedText(toolchain.OcrEngineVersion, "OCR engine version"),
            modelId,
            revision,
            expectedModelSha256,
            HashFile(runtimePath),
            detector,
            recognizer,
            classifier,
            dictionary,
            requestedMode,
            requestedProvider,
            requestedThreads
        );
        ValidateRequirements(requirements);
        return requirements;
    }

    internal static async Task<OcrCompletionStats> ValidateAsync(
        string inventoryPath,
        string inputManifestPath,
        InputManifestInfo inputManifest,
        string stringsPath,
        string assessmentsPath,
        string workingDirectory,
        long expectedInputFiles,
        OcrValidationRequirements requirements,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(inputManifest);
        if (expectedInputFiles < 0 || expectedInputFiles != inputManifest.FileCount)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedInputFiles));
        }
        ArgumentNullException.ThrowIfNull(requirements);
        ValidateRequirements(requirements);

        var inventoryFullPath = RequireFile(inventoryPath, "OCR input inventory");
        var stringsFullPath = RequireFile(stringsPath, "OCR string output");
        var assessmentsFullPath = RequireFile(assessmentsPath, "OCR assessment output");
        var manifestReader = await OpenVerifiedManifestReaderAsync(
            inputManifestPath,
            inputManifest,
            cancellationToken
        );
        using (manifestReader)
        using (var inventoryReader = CreateReader(inventoryFullPath))
        using (var assessmentReader = CreateReader(assessmentsFullPath))
        await using (var stringEnumerator = EnrichmentJsonlReader
            .ReadAsync(stringsFullPath, cancellationToken)
            .GetAsyncEnumerator(cancellationToken))
        using (var provenanceValidator = new DiskBackedProvenanceValidator(
            Path.GetFullPath(workingDirectory)
        ))
        {
            long inputFiles = 0;
            long processedFiles = 0;
            long notApplicableFiles = 0;
            long pages = 0;
            long stringRecords = 0;
            string? resolvedProvider = null;
            IReadOnlyDictionary<string, int>? resolvedThreadCounts = null;
            IReadOnlyDictionary<string, int>? resolvedWorkerCounts = null;
            while (await inventoryReader.ReadLineAsync(cancellationToken) is { } sourceFile)
            {
                cancellationToken.ThrowIfCancellationRequested();
                inputFiles++;
                if (string.IsNullOrWhiteSpace(sourceFile))
                {
                    throw new InvalidDataException(
                        $"OCR input inventory line {inputFiles:N0} is empty."
                    );
                }

                var manifestLine = await manifestReader.ReadLineAsync(cancellationToken);
                if (manifestLine is null)
                {
                    throw new InvalidDataException(
                        $"Evidence manifest coverage ended before OCR input file {inputFiles:N0}."
                    );
                }
                var inputIdentity = ParseInputManifestEntry(manifestLine, inputFiles);
                if (!string.Equals(inputIdentity.Path, sourceFile, PathComparison))
                {
                    throw new InvalidDataException(
                        $"Evidence manifest line {inputFiles:N0} identifies '{inputIdentity.Path}', expected '{sourceFile}'."
                    );
                }

                var assessmentLine = await assessmentReader.ReadLineAsync(cancellationToken);
                if (assessmentLine is null)
                {
                    throw new InvalidDataException(
                        $"OCR assessment coverage ended before input file {inputFiles:N0} ('{sourceFile}')."
                    );
                }
                if (
                    string.IsNullOrWhiteSpace(assessmentLine)
                    || assessmentLine.Length > MaximumAssessmentLineCharacters
                )
                {
                    throw new InvalidDataException(
                        $"OCR assessment line {inputFiles:N0} is empty or exceeds the safety limit."
                    );
                }

                var assessment = ParseAssessment(assessmentLine, inputFiles);
                ValidateAssessment(
                    assessment,
                    sourceFile,
                    inputIdentity,
                    inputFiles,
                    requirements
                );
                var assessmentThreadCounts = ValidateThreadConfiguration(
                    assessment.RequestedThreads,
                    assessment.ResolvedThreadCounts,
                    assessment.Provider,
                    requirements.RequestedThreads,
                    $"OCR assessment line {inputFiles:N0}"
                );
                var assessmentWorkerCounts = ValidateWorkerConfiguration(
                    assessment.ResolvedWorkerCounts,
                    assessment.Provider,
                    $"OCR assessment line {inputFiles:N0}"
                );
                if (resolvedProvider is null)
                {
                    resolvedProvider = assessment.Provider;
                    resolvedThreadCounts = assessmentThreadCounts;
                    resolvedWorkerCounts = assessmentWorkerCounts;
                }
                else if (!string.Equals(resolvedProvider, assessment.Provider, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"OCR assessment line {inputFiles:N0} resolved provider '{assessment.Provider}', "
                            + $"but the run already resolved provider '{resolvedProvider}'."
                    );
                }
                else if (!ThreadCountsEqual(resolvedThreadCounts!, assessmentThreadCounts))
                {
                    throw new InvalidDataException(
                        $"OCR assessment line {inputFiles:N0} changed the run's resolved thread counts."
                    );
                }
                else if (!ThreadCountsEqual(resolvedWorkerCounts!, assessmentWorkerCounts))
                {
                    throw new InvalidDataException(
                        $"OCR assessment line {inputFiles:N0} changed the run's resolved worker counts."
                    );
                }

                if (assessment.Status == "processed")
                {
                    processedFiles++;
                }
                else
                {
                    notApplicableFiles++;
                }
                pages = checked(pages + assessment.Pages!.Value);

                long observedPdfText = 0;
                long observedOcr = 0;
                var ocrPages = new HashSet<long>();
                for (long index = 0; index < assessment.StringRecords!.Value; index++)
                {
                    if (!await stringEnumerator.MoveNextAsync())
                    {
                        throw new InvalidDataException(
                            $"OCR strings ended while validating assessment line {inputFiles:N0}; "
                                + $"expected {assessment.StringRecords:N0} records for '{sourceFile}'."
                        );
                    }
                    var record = stringEnumerator.Current.Record;
                    var validated = ValidateOcrRecord(
                        record,
                        sourceFile,
                        inputIdentity,
                        assessment,
                        assessmentThreadCounts,
                        assessmentWorkerCounts,
                        stringEnumerator.Current.Json,
                        stringEnumerator.Current.LineNumber,
                        requirements
                    );
                    if (validated.OriginKind == "pdf-text")
                    {
                        observedPdfText++;
                    }
                    else
                    {
                        observedOcr++;
                        ocrPages.Add(validated.PageNumber);
                    }
                    provenanceValidator.AddOriginal(
                        record.RecordId,
                        EnrichmentRegexPipelineCore.CreateLineageIdentity(record)
                    );
                    stringRecords++;
                }
                ValidateObservedCounts(
                    assessment,
                    observedPdfText,
                    observedOcr,
                    ocrPages.Count,
                    inputFiles
                );
            }

            if (inputFiles != expectedInputFiles)
            {
                throw new InvalidDataException(
                    $"OCR input count changed: expected {expectedInputFiles:N0}, found {inputFiles:N0}."
                );
            }
            if (await manifestReader.ReadLineAsync(cancellationToken) is not null)
            {
                throw new InvalidDataException(
                    "Evidence manifest contains records beyond the OCR input inventory."
                );
            }
            if (await assessmentReader.ReadLineAsync(cancellationToken) is not null)
            {
                throw new InvalidDataException(
                    "OCR assessment output contains records beyond the evidence input inventory."
                );
            }
            if (await stringEnumerator.MoveNextAsync())
            {
                throw new InvalidDataException(
                    "OCR string output contains records beyond the assessment-declared cardinality."
                );
            }

            provenanceValidator.Validate(cancellationToken);
            return new OcrCompletionStats(
                inputFiles,
                processedFiles,
                notApplicableFiles,
                pages,
                stringRecords,
                resolvedProvider,
                requirements.RequestedThreads,
                resolvedThreadCounts
                    ?? new SortedDictionary<string, int>(StringComparer.Ordinal),
                resolvedWorkerCounts
                    ?? new SortedDictionary<string, int>(StringComparer.Ordinal)
            );
        }
    }

    private static void ValidateRequirements(OcrValidationRequirements requirements)
    {
        if (
            string.IsNullOrWhiteSpace(requirements.Engine)
            || string.IsNullOrWhiteSpace(requirements.EngineVersion)
            || string.IsNullOrWhiteSpace(requirements.Model)
            || string.IsNullOrWhiteSpace(requirements.Revision)
            || !AllLowerSha256(
                requirements.ModelSha256,
                requirements.RuntimeSha256,
                requirements.DetectorSha256,
                requirements.RecognizerSha256,
                requirements.ClassifierSha256,
                requirements.DictionarySha256
            )
            || requirements.RequestedMode is not (OcrWorkflowMode.Auto or OcrWorkflowMode.Force)
            || !Enum.IsDefined(requirements.RequestedProvider)
        )
        {
            throw new ArgumentException(
                "OCR validation requirements are incomplete.",
                nameof(requirements)
            );
        }
        ValidateRequestedThreads(requirements.RequestedThreads);
    }

    internal static void ValidateRequestedThreads(int requestedThreads)
    {
        if (requestedThreads is < 0 or > MaximumSessionThreads)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestedThreads),
                $"OCR threads must be between 0 and {MaximumSessionThreads}."
            );
        }
    }

    private static OcrAssessmentRecord ParseAssessment(string line, long lineNumber)
    {
        try
        {
            return JsonSerializer.Deserialize<OcrAssessmentRecord>(line, AssessmentJsonOptions)
                ?? throw new JsonException("The assessment was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Invalid OCR assessment JSON at line {lineNumber:N0}: {ex.Message}",
                ex
            );
        }
    }

    private static InputManifestEntry ParseInputManifestEntry(string line, long lineNumber)
    {
        if (line.Length == 0 || line.Length > MaximumInputManifestLineCharacters)
        {
            throw new InvalidDataException(
                $"Evidence manifest line {lineNumber:N0} is empty or exceeds the safety limit."
            );
        }
        OcrInputManifestRecord parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<OcrInputManifestRecord>(
                line,
                InputManifestJsonOptions
            )
                ?? throw new JsonException("The input identity was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Evidence manifest line {lineNumber:N0} is invalid: {ex.Message}",
                ex
            );
        }
        if (
            parsed.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(parsed.Path)
            || !Path.IsPathFullyQualified(parsed.Path)
            || !string.Equals(Path.GetFullPath(parsed.Path), parsed.Path, StringComparison.Ordinal)
            || parsed.Length is not >= 0
            || parsed.Sha256 is null
            || !IsLowerSha256(parsed.Sha256)
        )
        {
            throw new InvalidDataException(
                $"Evidence manifest line {lineNumber:N0} has invalid identity fields."
            );
        }
        return new InputManifestEntry
        {
            SchemaVersion = parsed.SchemaVersion.Value,
            Path = parsed.Path,
            Length = parsed.Length.Value,
            Sha256 = parsed.Sha256,
        };
    }

    private static void ValidateAssessment(
        OcrAssessmentRecord assessment,
        string expectedSourceFile,
        InputManifestEntry inputIdentity,
        long lineNumber,
        OcrValidationRequirements requirements
    )
    {
        var description = $"OCR assessment line {lineNumber:N0}";
        if (
            assessment.SchemaVersion != EnrichmentRegexPipelineCore.CurrentSchemaVersion
            || !string.Equals(assessment.RecordType, "ocr-assessment", StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"{description} is not a schema-1 ocr-assessment record."
            );
        }
        if (!string.Equals(assessment.SourceFile, expectedSourceFile, PathComparison))
        {
            throw new InvalidDataException(
                $"{description} identifies '{assessment.SourceFile}', expected '{expectedSourceFile}'."
            );
        }
        ValidateIdentity(
            assessment.Engine,
            assessment.EngineVersion,
            assessment.Model,
            assessment.Revision,
            assessment.ModelSha256,
            requirements,
            description
        );
        ValidateArtifactIdentity(
            assessment.ModelPackSha256,
            assessment.RuntimeSha256,
            assessment.DetectorSha256,
            assessment.RecognizerSha256,
            assessment.ClassifierSha256,
            assessment.DictionarySha256,
            requirements,
            description
        );
        if (
            !string.Equals(assessment.SourceSha256, inputIdentity.Sha256, StringComparison.Ordinal)
            || assessment.SourceSize != inputIdentity.Length
        )
        {
            throw new InvalidDataException(
                $"{description} source hash or size does not match the trusted input manifest."
            );
        }
        var requestedProvider = ProviderArgument(requirements.RequestedProvider);
        if (!string.Equals(assessment.RequestedProvider, requestedProvider, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} requestedProvider does not match '{requestedProvider}'."
            );
        }
        var requestedMode = ModeArgument(requirements.RequestedMode);
        if (!string.Equals(assessment.Mode, requestedMode, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} mode does not match requested OCR mode '{requestedMode}'."
            );
        }
        ValidateResolvedProvider(assessment.Provider, requirements.RequestedProvider, description);
        if (!string.IsNullOrWhiteSpace(assessment.Error))
        {
            throw new InvalidDataException(
                $"{description} reports a failed examination: {assessment.Error}"
            );
        }
        if (
            assessment.Pages is not >= 0
            || assessment.Pages > MaximumPagesPerInput
            || assessment.StringRecords is not >= 0
            || assessment.RenderedPages is not >= 0
            || assessment.PdfTextRecords is not >= 0
            || assessment.OcrRecords is not >= 0
            || assessment.RenderedPages > assessment.Pages
            || assessment.Dpi is not (>= 72 and <= 600)
        )
        {
            throw new InvalidDataException(
                $"{description} contains missing or invalid OCR counts, DPI, or page limits."
            );
        }
        long classifiedRecords;
        try
        {
            classifiedRecords = checked(
                assessment.PdfTextRecords.Value + assessment.OcrRecords.Value
            );
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException($"{description} record counts overflow.", ex);
        }
        if (classifiedRecords != assessment.StringRecords)
        {
            throw new InvalidDataException(
                $"{description} pdfTextRecords and ocrRecords do not equal stringRecords."
            );
        }
        if (assessment.Status == "processed")
        {
            if (assessment.Pages == 0)
            {
                throw new InvalidDataException(
                    $"{description} marks a file processed without a page or image."
                );
            }
            return;
        }
        if (assessment.Status == "not-applicable")
        {
            if (
                assessment.Pages != 0
                || assessment.StringRecords != 0
                || assessment.RenderedPages != 0
                || assessment.PdfTextRecords != 0
                || assessment.OcrRecords != 0
            )
            {
                throw new InvalidDataException(
                    $"{description} marks a file not applicable but reports OCR output."
                );
            }
            return;
        }
        throw new InvalidDataException(
            $"{description} has unsupported status '{assessment.Status}'."
        );
    }

    private static ValidatedOcrRecord ValidateOcrRecord(
        EnrichmentStringRecord record,
        string expectedSourceFile,
        InputManifestEntry inputIdentity,
        OcrAssessmentRecord assessment,
        IReadOnlyDictionary<string, int> assessmentThreadCounts,
        IReadOnlyDictionary<string, int> assessmentWorkerCounts,
        string rawJson,
        long lineNumber,
        OcrValidationRequirements requirements
    )
    {
        var description = $"OCR string at line {lineNumber:N0}";
        ValidateContentAddressedRecordId(rawJson, record.RecordId, description);
        if (record.Transform is not null || !string.IsNullOrWhiteSpace(record.ParentRecordId))
        {
            throw new InvalidDataException(
                $"{description} must be an original extractor record."
            );
        }
        if (!string.Equals(record.SourceFile, expectedSourceFile, PathComparison))
        {
            throw new InvalidDataException(
                $"{description} belongs to '{record.SourceFile}', expected '{expectedSourceFile}'."
            );
        }
        var originKind = record.Origin!.Kind;
        if (
            originKind is not ("ocr" or "pdf-text")
            || record.Location!.Kind is not ("page_region" or "image_region")
            || (originKind == "pdf-text" && record.Location.Kind != "page_region")
        )
        {
            throw new InvalidDataException(
                $"{description} lacks an OCR/PDF-text origin or supported region location."
            );
        }
        ValidateIdentity(
            record.Origin.Extractor,
            record.Origin.Version,
            record.Origin.Model,
            record.Origin.Revision,
            record.Origin.ModelSha256,
            requirements,
            description
        );
        ValidateResolvedProvider(
            record.Origin.Provider ?? string.Empty,
            requirements.RequestedProvider,
            description
        );
        if (!string.Equals(record.Origin.Provider, assessment.Provider, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} resolved provider '{record.Origin.Provider}', expected '{assessment.Provider}' from its assessment."
            );
        }

        var attributes = record.Attributes
            ?? throw new InvalidDataException($"{description} has no forensic attributes.");
        RequireAttributeEquals(attributes, "sourceSha256", inputIdentity.Sha256, description);
        RequireAttributeEquals(attributes, "sourceSize", inputIdentity.Length, description);
        RequireAttributeEquals(
            attributes,
            "modelPackSha256",
            requirements.ModelSha256,
            description
        );
        RequireAttributeEquals(
            attributes,
            "runtimeSha256",
            requirements.RuntimeSha256,
            description
        );
        RequireAttributeEquals(
            attributes,
            "detectorSha256",
            requirements.DetectorSha256,
            description
        );
        RequireAttributeEquals(
            attributes,
            "recognizerSha256",
            requirements.RecognizerSha256,
            description
        );
        RequireAttributeEquals(
            attributes,
            "classifierSha256",
            requirements.ClassifierSha256,
            description
        );
        RequireAttributeEquals(
            attributes,
            "dictionarySha256",
            requirements.DictionarySha256,
            description
        );
        RequireAttributeEquals(
            attributes,
            "requestedProvider",
            ProviderArgument(requirements.RequestedProvider),
            description
        );
        RequireAttributeEquals(attributes, "provider", assessment.Provider, description);
        RequireAttributeEquals(
            attributes,
            "requestedThreads",
            requirements.RequestedThreads,
            description
        );
        var recordThreadCounts = RequireThreadCountsAttribute(
            attributes,
            "resolvedThreadCounts",
            assessment.Provider,
            requirements.RequestedThreads,
            description
        );
        if (!ThreadCountsEqual(recordThreadCounts, assessmentThreadCounts))
        {
            throw new InvalidDataException(
                $"{description} resolvedThreadCounts do not match its assessment."
            );
        }
        var recordWorkerCounts = RequireWorkerCountsAttribute(
            attributes,
            "resolvedWorkerCounts",
            assessment.Provider,
            description
        );
        if (!ThreadCountsEqual(recordWorkerCounts, assessmentWorkerCounts))
        {
            throw new InvalidDataException(
                $"{description} resolvedWorkerCounts do not match its assessment."
            );
        }

        var pageNumber = RequireInt64Attribute(attributes, "pageNumber", description);
        if (pageNumber < 1 || pageNumber > assessment.Pages || pageNumber > MaximumPagesPerInput)
        {
            throw new InvalidDataException($"{description} has an invalid page number.");
        }
        var box = RequireBox(attributes, description);
        var coordinateSpace = RequireStringAttribute(attributes, "coordinateSpace", description);
        var executionProvider = RequireStringAttribute(
            attributes,
            "executionProvider",
            description
        );

        if (originKind == "ocr")
        {
            if (coordinateSpace != "render-pixels")
            {
                throw new InvalidDataException(
                    $"{description} OCR geometry is not in render-pixels."
                );
            }
            var width = RequirePositiveNumberAttribute(attributes, "pixelWidth", description);
            var height = RequirePositiveNumberAttribute(attributes, "pixelHeight", description);
            ValidateArea(width, height, MaximumRasterPixels, description);
            ValidateBoxBounds(box, width, height, RasterBoundsTolerance, description);
            var confidence = RequireNumberAttribute(attributes, "confidence", description);
            if (confidence is < 0 or > 1)
            {
                throw new InvalidDataException($"{description} confidence is outside 0..1.");
            }
            var renderSha256 = RequireLowerShaAttribute(attributes, "renderSha256", description);
            var pageSha256 = RequireLowerShaAttribute(attributes, "pageSha256", description);
            if (!string.Equals(renderSha256, pageSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{description} renderSha256 and pageSha256 do not match."
                );
            }
            if (record.Location.Kind == "page_region")
            {
                var renderDpi = RequireInt64Attribute(attributes, "renderDpi", description);
                if (renderDpi is < 72 or > 600)
                {
                    throw new InvalidDataException($"{description} renderDpi is invalid.");
                }
            }
            else
            {
                RequireAbsent(attributes, "renderDpi", description);
            }
            RequireAbsent(attributes, "pageWidthPoints", description);
            RequireAbsent(attributes, "pageHeightPoints", description);
            RequireAbsent(attributes, "textLayerExtractor", description);
            RequireAbsent(attributes, "textLayerExtractorVersion", description);
            ValidateExecutionProvider(executionProvider, assessment.Provider, isPdfText: false, description);
        }
        else
        {
            if (coordinateSpace != "pdf-points")
            {
                throw new InvalidDataException(
                    $"{description} PDF-text geometry is not in pdf-points."
                );
            }
            var width = RequirePositiveNumberAttribute(
                attributes,
                "pageWidthPoints",
                description
            );
            var height = RequirePositiveNumberAttribute(
                attributes,
                "pageHeightPoints",
                description
            );
            ValidateArea(width, height, MaximumPdfPointArea, description);
            ValidateBoxBounds(box, width, height, GeometryTolerance, description);
            RequireAttributeEquals(
                attributes,
                "textLayerExtractor",
                "pypdfium2",
                description
            );
            _ = RequireStringAttribute(attributes, "textLayerExtractorVersion", description);
            RequireAbsent(attributes, "confidence", description);
            RequireAbsent(attributes, "renderSha256", description);
            RequireAbsent(attributes, "pageSha256", description);
            RequireAbsent(attributes, "renderDpi", description);
            RequireAbsent(attributes, "pixelWidth", description);
            RequireAbsent(attributes, "pixelHeight", description);
            ValidateExecutionProvider(executionProvider, assessment.Provider, isPdfText: true, description);
        }
        ValidateLocationValue(record.Location.Value, pageNumber, box, originKind, description);
        return new ValidatedOcrRecord(originKind, pageNumber);
    }

    private static void ValidateObservedCounts(
        OcrAssessmentRecord assessment,
        long observedPdfText,
        long observedOcr,
        int observedOcrPages,
        long assessmentLine
    )
    {
        if (
            observedPdfText != assessment.PdfTextRecords
            || observedOcr != assessment.OcrRecords
        )
        {
            throw new InvalidDataException(
                $"OCR assessment line {assessmentLine:N0} record-kind counts do not match its strings."
            );
        }
        if (observedOcrPages > assessment.RenderedPages)
        {
            throw new InvalidDataException(
                $"OCR assessment line {assessmentLine:N0} has OCR records on more pages than renderedPages."
            );
        }
    }

    internal static void ValidateResolvedProvider(
        string provider,
        OcrProvider requestedProvider,
        string description
    )
    {
        if (!IsAllowedResolvedProvider(provider))
        {
            throw new InvalidDataException(
                $"{description} has unsupported resolved provider '{provider}'."
            );
        }
        var matchesRequest = requestedProvider switch
        {
            OcrProvider.Auto => true,
            OcrProvider.Cpu => provider == "cpu",
            OcrProvider.Cuda => provider == "cuda",
            OcrProvider.DirectMl => provider == "directml",
            OcrProvider.Hybrid => IsHybridProvider(provider),
            _ => false,
        };
        if (!matchesRequest)
        {
            throw new InvalidDataException(
                $"{description} resolved provider '{provider}', which does not satisfy requested provider '{ProviderArgument(requestedProvider)}'."
            );
        }
    }

    internal static string ProviderArgument(OcrProvider provider) =>
        provider switch
        {
            OcrProvider.Auto => "auto",
            OcrProvider.Cpu => "cpu",
            OcrProvider.Cuda => "cuda",
            OcrProvider.DirectMl => "directml",
            OcrProvider.Hybrid => "hybrid",
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };

    private static IReadOnlyDictionary<string, int> ValidateThreadConfiguration(
        int? reportedRequestedThreads,
        IReadOnlyDictionary<string, int>? reportedResolvedThreadCounts,
        string resolvedProvider,
        int expectedRequestedThreads,
        string description
    )
    {
        if (reportedRequestedThreads != expectedRequestedThreads)
        {
            throw new InvalidDataException(
                $"{description} requestedThreads does not match the requested value {expectedRequestedThreads}."
            );
        }
        if (reportedResolvedThreadCounts is null)
        {
            throw new InvalidDataException($"{description} has no resolvedThreadCounts object.");
        }

        var expectedProviders = ExpectedThreadProviders(resolvedProvider);
        if (
            reportedResolvedThreadCounts.Count != expectedProviders.Count
            || reportedResolvedThreadCounts.Keys.Any(key => !expectedProviders.Contains(key))
        )
        {
            throw new InvalidDataException(
                $"{description} resolvedThreadCounts do not match resolved provider '{resolvedProvider}'."
            );
        }

        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (provider, threads) in reportedResolvedThreadCounts)
        {
            if (threads is < 1 or > MaximumSessionThreads)
            {
                throw new InvalidDataException(
                    $"{description} resolvedThreadCounts value for '{provider}' must be between 1 and {MaximumSessionThreads}."
                );
            }
            if (expectedRequestedThreads != 0 && threads != expectedRequestedThreads)
            {
                throw new InvalidDataException(
                    $"{description} changed the explicit OCR thread override for '{provider}'."
                );
            }
            if (expectedRequestedThreads == 0 && provider != "cpu" && threads != 1)
            {
                throw new InvalidDataException(
                    $"{description} automatic GPU OCR threads must resolve to 1 for '{provider}'."
                );
            }
            result.Add(provider, threads);
        }
        return result;
    }

    private static HashSet<string> ExpectedThreadProviders(string resolvedProvider) =>
        resolvedProvider switch
        {
            "cpu" => ["cpu"],
            "cuda" => ["cuda"],
            "directml" => ["directml"],
            "hybrid-cuda-cpu" => ["cpu", "cuda"],
            "hybrid-directml-cpu" => ["cpu", "directml"],
            _ => throw new InvalidDataException(
                $"Unsupported resolved OCR provider '{resolvedProvider}' for thread validation."
            ),
        };

    private static IReadOnlyDictionary<string, int> ValidateWorkerConfiguration(
        IReadOnlyDictionary<string, int>? reportedResolvedWorkerCounts,
        string resolvedProvider,
        string description
    )
    {
        if (reportedResolvedWorkerCounts is null)
        {
            throw new InvalidDataException($"{description} has no resolvedWorkerCounts object.");
        }

        var expectedProviders = ExpectedThreadProviders(resolvedProvider);
        if (
            reportedResolvedWorkerCounts.Count != expectedProviders.Count
            || reportedResolvedWorkerCounts.Keys.Any(key => !expectedProviders.Contains(key))
        )
        {
            throw new InvalidDataException(
                $"{description} resolvedWorkerCounts do not match resolved provider '{resolvedProvider}'."
            );
        }

        var result = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (provider, workers) in reportedResolvedWorkerCounts)
        {
            if (workers is < 1 or > MaximumSessionThreads)
            {
                throw new InvalidDataException(
                    $"{description} resolvedWorkerCounts value for '{provider}' must be between 1 and {MaximumSessionThreads}."
                );
            }
            if (provider != "cpu" && workers != 1)
            {
                throw new InvalidDataException(
                    $"{description} GPU OCR workers must resolve to 1 for '{provider}'."
                );
            }
            result.Add(provider, workers);
        }
        return result;
    }

    private static bool ThreadCountsEqual(
        IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right
    ) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static string ModeArgument(OcrWorkflowMode mode) =>
        mode switch
        {
            OcrWorkflowMode.Auto => "auto",
            OcrWorkflowMode.Force => "force",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    private static bool IsAllowedResolvedProvider(string provider) =>
        provider is "cpu" or "cuda" or "directml" || IsHybridProvider(provider);

    private static bool IsHybridProvider(string provider) =>
        provider is "hybrid-cuda-cpu" or "hybrid-directml-cpu";

    private static void ValidateExecutionProvider(
        string executionProvider,
        string resolvedProvider,
        bool isPdfText,
        string description
    )
    {
        if (isPdfText)
        {
            if (executionProvider != "cpu")
            {
                throw new InvalidDataException(
                    $"{description} PDF text must record the CPU execution lane."
                );
            }
            return;
        }
        var valid = resolvedProvider switch
        {
            "cpu" => executionProvider == "cpu",
            "cuda" => executionProvider == "cuda",
            "directml" => executionProvider == "directml",
            "hybrid-cuda-cpu" => executionProvider is "cuda" or "cpu",
            "hybrid-directml-cpu" => executionProvider is "directml" or "cpu",
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"{description} executionProvider '{executionProvider}' is not a valid lane for '{resolvedProvider}'."
            );
        }
    }

    private static void ValidateIdentity(
        string? engine,
        string? engineVersion,
        string? model,
        string? revision,
        string? modelSha256,
        OcrValidationRequirements requirements,
        string description
    )
    {
        if (
            !string.Equals(engine, requirements.Engine, StringComparison.Ordinal)
            || !string.Equals(engineVersion, requirements.EngineVersion, StringComparison.Ordinal)
            || !string.Equals(model, requirements.Model, StringComparison.Ordinal)
            || !string.Equals(revision, requirements.Revision, StringComparison.Ordinal)
            || !string.Equals(modelSha256, requirements.ModelSha256, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"{description} does not match the verified OCR engine and model identity."
            );
        }
    }

    private static void ValidateArtifactIdentity(
        string modelPackSha256,
        string runtimeSha256,
        string detectorSha256,
        string recognizerSha256,
        string classifierSha256,
        string dictionarySha256,
        OcrValidationRequirements requirements,
        string description
    )
    {
        if (
            !string.Equals(modelPackSha256, requirements.ModelSha256, StringComparison.Ordinal)
            || !string.Equals(runtimeSha256, requirements.RuntimeSha256, StringComparison.Ordinal)
            || !string.Equals(detectorSha256, requirements.DetectorSha256, StringComparison.Ordinal)
            || !string.Equals(recognizerSha256, requirements.RecognizerSha256, StringComparison.Ordinal)
            || !string.Equals(classifierSha256, requirements.ClassifierSha256, StringComparison.Ordinal)
            || !string.Equals(dictionarySha256, requirements.DictionarySha256, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"{description} does not match the verified OCR runtime or model-pack components."
            );
        }
    }

    private static void RequireAttributeEquals(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string expected,
        string description
    )
    {
        var actual = RequireStringAttribute(attributes, name, description);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' does not match its verified value."
            );
        }
    }

    private static void RequireAttributeEquals(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        long expected,
        string description
    )
    {
        if (RequireInt64Attribute(attributes, name, description) != expected)
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' does not match its verified value."
            );
        }
    }

    private static string RequireStringAttribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string description
    )
    {
        var value = RequireAttribute(attributes, name, description);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"{description} attribute '{name}' must be text.");
        }
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096)
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' is empty or exceeds its safety limit."
            );
        }
        return text;
    }

    private static string RequireLowerShaAttribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string description
    )
    {
        var value = RequireStringAttribute(attributes, name, description);
        if (!IsLowerSha256(value))
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' is not a lowercase SHA-256."
            );
        }
        return value;
    }

    private static long RequireInt64Attribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string description
    )
    {
        var value = RequireAttribute(attributes, name, description);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' must be an integer."
            );
        }
        return result;
    }

    private static IReadOnlyDictionary<string, int> RequireThreadCountsAttribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string resolvedProvider,
        int requestedThreads,
        string description
    )
    {
        var value = RequireAttribute(attributes, name, description);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' must be an object."
            );
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (
                property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out var threads)
                || !counts.TryAdd(property.Name, threads)
            )
            {
                throw new InvalidDataException(
                    $"{description} attribute '{name}' contains a duplicate or non-integer value."
                );
            }
        }
        return ValidateThreadConfiguration(
            requestedThreads,
            counts,
            resolvedProvider,
            requestedThreads,
            $"{description} attribute '{name}'"
        );
    }

    private static IReadOnlyDictionary<string, int> RequireWorkerCountsAttribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string resolvedProvider,
        string description
    )
    {
        var value = RequireAttribute(attributes, name, description);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' must be an object."
            );
        }
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (
                property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out var workers)
                || !counts.TryAdd(property.Name, workers)
            )
            {
                throw new InvalidDataException(
                    $"{description} attribute '{name}' contains a duplicate or non-integer value."
                );
            }
        }
        return ValidateWorkerConfiguration(
            counts,
            resolvedProvider,
            $"{description} attribute '{name}'"
        );
    }

    private static double RequirePositiveNumberAttribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string description
    )
    {
        var value = RequireNumberAttribute(attributes, name, description);
        if (value <= 0)
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' must be positive."
            );
        }
        return value;
    }

    private static double RequireNumberAttribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string description
    )
    {
        var value = RequireAttribute(attributes, name, description);
        if (
            value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var result)
            || !double.IsFinite(result)
        )
        {
            throw new InvalidDataException(
                $"{description} attribute '{name}' must be a finite number."
            );
        }
        return result;
    }

    private static double[] RequireBox(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string description
    )
    {
        var value = RequireAttribute(attributes, "box", description);
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
        {
            throw new InvalidDataException($"{description} box must contain four corners.");
        }
        var coordinates = new double[8];
        var coordinateIndex = 0;
        foreach (var point in value.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2)
            {
                throw new InvalidDataException(
                    $"{description} box contains an invalid corner."
                );
            }
            foreach (var coordinate in point.EnumerateArray())
            {
                if (
                    coordinate.ValueKind != JsonValueKind.Number
                    || !coordinate.TryGetDouble(out var number)
                    || !double.IsFinite(number)
                )
                {
                    throw new InvalidDataException(
                        $"{description} box contains a non-finite coordinate."
                    );
                }
                coordinates[coordinateIndex++] = number;
            }
        }
        var xs = new[] { coordinates[0], coordinates[2], coordinates[4], coordinates[6] };
        var ys = new[] { coordinates[1], coordinates[3], coordinates[5], coordinates[7] };
        if (xs.Max() <= xs.Min() || ys.Max() <= ys.Min())
        {
            throw new InvalidDataException($"{description} box is degenerate.");
        }
        return coordinates;
    }

    private static JsonElement RequireAttribute(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string description
    )
    {
        if (!attributes.TryGetValue(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException(
                $"{description} is missing required attribute '{name}'."
            );
        }
        return value;
    }

    private static void RequireAbsent(
        IReadOnlyDictionary<string, JsonElement> attributes,
        string name,
        string description
    )
    {
        if (attributes.ContainsKey(name))
        {
            throw new InvalidDataException(
                $"{description} must not contain attribute '{name}' for this origin."
            );
        }
    }

    private static void ValidateArea(
        double width,
        double height,
        double maximum,
        string description
    )
    {
        if (!double.IsFinite(width * height) || Math.Ceiling(width) * Math.Ceiling(height) > maximum)
        {
            throw new InvalidDataException($"{description} dimensions exceed the safety limit.");
        }
    }

    private static void ValidateBoxBounds(
        IReadOnlyList<double> box,
        double width,
        double height,
        double tolerance,
        string description
    )
    {
        for (var index = 0; index < box.Count; index += 2)
        {
            if (
                box[index] < -tolerance
                || box[index] > width + tolerance
                || box[index + 1] < -tolerance
                || box[index + 1] > height + tolerance
            )
            {
                throw new InvalidDataException($"{description} box is outside its page bounds.");
            }
        }
    }

    private static void ValidateLocationValue(
        string value,
        long pageNumber,
        IReadOnlyList<double> box,
        string originKind,
        string description
    )
    {
        var prefix = $"page={pageNumber:D8};box=";
        var suffix = $";source={originKind}";
        if (
            !value.StartsWith(prefix, StringComparison.Ordinal)
            || !value.EndsWith(suffix, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"{description} location value does not match its page and origin."
            );
        }
        var coordinateText = value[prefix.Length..^suffix.Length].Split(',');
        if (coordinateText.Length != box.Count)
        {
            throw new InvalidDataException(
                $"{description} location value has the wrong coordinate count."
            );
        }
        for (var index = 0; index < coordinateText.Length; index++)
        {
            if (
                !double.TryParse(
                    coordinateText[index],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var coordinate
                )
                || !double.IsFinite(coordinate)
                || Math.Abs(coordinate - box[index]) > GeometryTolerance
            )
            {
                throw new InvalidDataException(
                    $"{description} location value disagrees with its box geometry."
                );
            }
        }
    }

    private static async Task<StreamReader> OpenVerifiedManifestReaderAsync(
        string path,
        InputManifestInfo expected,
        CancellationToken cancellationToken
    )
    {
        var fullPath = RequireFile(path, "evidence input manifest");
        var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        try
        {
            var digest = Convert
                .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            if (!HashesEqual(digest, expected.ManifestSha256))
            {
                throw new InvalidDataException(
                    "Evidence input manifest SHA-256 changed before OCR provenance validation."
                );
            }
            stream.Position = 0;
            return new StreamReader(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false,
                1024 * 1024
            );
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    private static Dictionary<string, JsonElement> ReadUniqueProperties(
        JsonElement value,
        string description,
        IReadOnlyCollection<string> allowed
    )
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{description} must be a JSON object.");
        }
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException(
                    $"{description} contains unsupported property '{property.Name}'."
                );
            }
            if (!result.TryAdd(property.Name, property.Value))
            {
                throw new InvalidDataException(
                    $"{description} contains duplicate property '{property.Name}'."
                );
            }
        }
        return result;
    }

    private static string ReadAndVerifyComponent(
        IReadOnlyDictionary<string, JsonElement> root,
        string name,
        string requiredSuffix,
        string modelRoot
    ) => ReadAndVerifyComponent(root, name, [requiredSuffix], modelRoot);

    private static string ReadAndVerifyComponent(
        IReadOnlyDictionary<string, JsonElement> root,
        string name,
        IReadOnlyCollection<string> requiredSuffixes,
        string modelRoot
    )
    {
        if (!root.TryGetValue(name, out var value))
        {
            throw new InvalidDataException($"OCR model-pack manifest is missing '{name}'.");
        }
        var component = ReadUniqueProperties(
            value,
            $"OCR model-pack component '{name}'",
            ["path", "sha256"]
        );
        var relativePath = RequireJsonString(
            component,
            "path",
            $"OCR model-pack component '{name}'"
        );
        BundleManifestVerifier.ValidateRelativePath("model-pack-root/" + relativePath);
        if (!requiredSuffixes.Contains(Path.GetExtension(relativePath), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"OCR model-pack component '{name}' has an unsupported file type."
            );
        }
        var expectedSha256 = RequireLowerSha256(
            RequireJsonString(component, "sha256", $"OCR model-pack component '{name}'"),
            $"OCR model-pack component '{name}'"
        );
        var fullPath = Path.GetFullPath(
            Path.Combine(modelRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))
        );
        var rootPrefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelRoot))
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, PathComparison))
        {
            throw new InvalidDataException(
                $"OCR model-pack component '{name}' escapes its model directory."
            );
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(fullPath, $"OCR model component '{name}'");
        RequireFile(fullPath, $"OCR model component '{name}'");
        var actualSha256 = HashFile(fullPath);
        if (!HashesEqual(actualSha256, expectedSha256))
        {
            throw new InvalidDataException(
                $"OCR model-pack component '{name}' SHA-256 mismatch."
            );
        }
        return expectedSha256;
    }

    private static string RequireJsonString(
        IReadOnlyDictionary<string, JsonElement> properties,
        string name,
        string description
    )
    {
        if (
            !properties.TryGetValue(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
        )
        {
            throw new InvalidDataException($"{description} requires non-empty '{name}'.");
        }
        return value.GetString()!;
    }

    private static string RequireBoundedText(string? value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            throw new InvalidDataException($"{description} is missing or exceeds its safety limit.");
        }
        return value;
    }

    private static string RequireLowerSha256(string? value, string description)
    {
        if (value is null || !IsLowerSha256(value))
        {
            throw new InvalidDataException($"{description} is not a lowercase SHA-256.");
        }
        return value;
    }

    private static byte[] ReadBoundedFile(string path, long maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan
        );
        if (stream.Length <= 0 || stream.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"OCR artifact '{path}' is empty or exceeds its safety limit."
            );
        }
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Length != bytes.Length)
        {
            throw new InvalidDataException($"OCR artifact '{path}' changed while being read.");
        }
        return bytes;
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan
        );
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static StreamReader CreateReader(string path) =>
        new(
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ),
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            1024 * 1024
        );

    private static string RequireFile(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{description} was not found.", fullPath);
        }
        return fullPath;
    }

    private static void ValidateContentAddressedRecordId(
        string rawJson,
        string recordId,
        string description
    )
    {
        var expected = ComputeContentAddressedRecordId(rawJson, out var embeddedRecordId);
        if (!string.Equals(embeddedRecordId, recordId, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} recordId is not the exact top-level recordId string."
            );
        }
        if (
            !TryParseContentAddressedRecordId(recordId, out var actualDigest)
            || !TryParseContentAddressedRecordId(expected, out var expectedDigest)
            || !CryptographicOperations.FixedTimeEquals(actualDigest, expectedDigest)
        )
        {
            throw new InvalidDataException(
                $"{description} recordId is not the SHA-256 content address of its canonical record."
            );
        }
    }

    /// <summary>
    /// Computes the language-neutral OCR record identity used by the offline worker. The material is
    /// the schema-1 JSON object without its top-level recordId, with object keys sorted by Unicode
    /// scalar value, arrays kept in source order, Python-compatible JSON scalar spelling, and UTF-8
    /// encoding. The public identity is the lowercase SHA-256 prefixed with "sha256:".
    /// </summary>
    internal static string ComputeContentAddressedRecordId(string rawJson) =>
        ComputeContentAddressedRecordId(rawJson, out _);

    private static string ComputeContentAddressedRecordId(
        string rawJson,
        out string? embeddedRecordId
    )
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            throw new InvalidDataException("OCR record identity material is empty.");
        }
        using var document = JsonDocument.Parse(
            rawJson,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            }
        );
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("OCR record identity material must be a JSON object.");
        }

        var canonical = new StringBuilder(rawJson.Length);
        embeddedRecordId = null;
        WriteCanonicalObject(
            document.RootElement,
            canonical,
            omitTopLevelRecordId: true,
            ref embeddedRecordId,
            depth: 0
        );
        var digest = SHA256.HashData(StrictUtf8.GetBytes(canonical.ToString()));
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static void WriteCanonicalJson(
        JsonElement value,
        StringBuilder output,
        int depth
    )
    {
        if (depth > 64)
        {
            throw new InvalidDataException("OCR record identity JSON exceeds the depth limit.");
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                string? ignored = null;
                WriteCanonicalObject(value, output, false, ref ignored, depth);
                break;
            case JsonValueKind.Array:
                output.Append('[');
                var first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first)
                    {
                        output.Append(',');
                    }
                    first = false;
                    WriteCanonicalJson(item, output, depth + 1);
                }
                output.Append(']');
                break;
            case JsonValueKind.String:
                WritePythonJsonString(value.GetString()!, output);
                break;
            case JsonValueKind.Number:
                output.Append(RequireCanonicalPythonNumber(value.GetRawText()));
                break;
            case JsonValueKind.True:
                output.Append("true");
                break;
            case JsonValueKind.False:
                output.Append("false");
                break;
            case JsonValueKind.Null:
                output.Append("null");
                break;
            default:
                throw new InvalidDataException("OCR record identity JSON contains an invalid value.");
        }
    }

    private static void WriteCanonicalObject(
        JsonElement value,
        StringBuilder output,
        bool omitTopLevelRecordId,
        ref string? embeddedRecordId,
        int depth
    )
    {
        var properties = new List<KeyValuePair<string, JsonElement>>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"OCR record identity JSON repeats property '{property.Name}'."
                );
            }
            if (omitTopLevelRecordId && property.Name == "recordId")
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("OCR recordId must be a JSON string.");
                }
                embeddedRecordId = property.Value.GetString();
                continue;
            }
            properties.Add(new KeyValuePair<string, JsonElement>(property.Name, property.Value));
        }
        properties.Sort((left, right) => CompareUnicodeScalars(left.Key, right.Key));

        output.Append('{');
        for (var index = 0; index < properties.Count; index++)
        {
            if (index != 0)
            {
                output.Append(',');
            }
            WritePythonJsonString(properties[index].Key, output);
            output.Append(':');
            WriteCanonicalJson(properties[index].Value, output, depth + 1);
        }
        output.Append('}');
    }

    private static void WritePythonJsonString(string value, StringBuilder output)
    {
        output.Append('"');
        for (var index = 0; index < value.Length;)
        {
            var scalar = NextUnicodeScalar(value, ref index);
            switch (scalar)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case <= 0x1f:
                    output.Append("\\u");
                    output.Append(scalar.ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    output.Append(char.ConvertFromUtf32(scalar));
                    break;
            }
        }
        output.Append('"');
    }

    private static int CompareUnicodeScalars(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftScalar = NextUnicodeScalar(left, ref leftIndex);
            var rightScalar = NextUnicodeScalar(right, ref rightIndex);
            var comparison = leftScalar.CompareTo(rightScalar);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
    }

    private static int NextUnicodeScalar(string value, ref int index)
    {
        var first = value[index++];
        if (!char.IsSurrogate(first))
        {
            return first;
        }
        if (
            !char.IsHighSurrogate(first)
            || index >= value.Length
            || !char.IsLowSurrogate(value[index])
        )
        {
            throw new InvalidDataException("OCR record identity JSON contains invalid Unicode.");
        }
        return char.ConvertToUtf32(first, value[index++]);
    }

    private static string RequireCanonicalPythonNumber(string raw)
    {
        if (raw.Length == 0 || raw.Length > MaximumCanonicalNumberCharacters)
        {
            throw new InvalidDataException(
                "OCR record identity number is empty or exceeds the safety limit."
            );
        }
        string canonical;
        if (raw.IndexOfAny(['.', 'e', 'E']) < 0)
        {
            if (
                !BigInteger.TryParse(
                    raw,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out var integer
                )
            )
            {
                throw new InvalidDataException("OCR record identity contains an invalid integer.");
            }
            canonical = integer.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            if (
                !double.TryParse(
                    raw,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number
                )
                || !double.IsFinite(number)
            )
            {
                throw new InvalidDataException("OCR record identity contains an invalid float.");
            }
            canonical = FormatPythonFloat(number);
        }
        if (!string.Equals(raw, canonical, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"OCR record identity number '{raw}' is not in canonical Python JSON form."
            );
        }
        return canonical;
    }

    private static string FormatPythonFloat(double value)
    {
        var formatted = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentOffset = formatted.IndexOf('E');
        if (exponentOffset >= 0)
        {
            var mantissa = formatted[..exponentOffset];
            var exponent = int.Parse(
                formatted[(exponentOffset + 1)..],
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture
            );
            var sign = exponent >= 0 ? "+" : "-";
            return $"{mantissa}e{sign}{Math.Abs(exponent).ToString("D2", CultureInfo.InvariantCulture)}";
        }

        var signLength = formatted.Length > 0 && formatted[0] == '-' ? 1 : 0;
        var decimalOffset = formatted.IndexOf('.');
        var integerLength = (decimalOffset >= 0 ? decimalOffset : formatted.Length) - signLength;
        var digits = formatted[signLength..].Replace(".", string.Empty, StringComparison.Ordinal);
        var firstNonZero = -1;
        for (var index = 0; index < digits.Length; index++)
        {
            if (digits[index] != '0')
            {
                firstNonZero = index;
                break;
            }
        }
        var decimalExponent = firstNonZero < 0 ? 0 : integerLength - firstNonZero - 1;
        if (decimalExponent >= 16)
        {
            var significant = digits[firstNonZero..].TrimEnd('0');
            var mantissa = significant.Length == 1
                ? significant
                : $"{significant[0]}.{significant[1..]}";
            var negative = signLength == 1 ? "-" : string.Empty;
            return $"{negative}{mantissa}e+{decimalExponent.ToString("D2", CultureInfo.InvariantCulture)}";
        }
        return decimalOffset >= 0 ? formatted : $"{formatted}.0";
    }

    private static bool TryParseContentAddressedRecordId(string value, out byte[] digest)
    {
        const string prefix = "sha256:";
        if (
            value.Length == prefix.Length + 64
            && value.StartsWith(prefix, StringComparison.Ordinal)
            && IsLowerSha256(value[prefix.Length..])
        )
        {
            digest = Convert.FromHexString(value[prefix.Length..]);
            return true;
        }
        digest = [];
        return false;
    }

    private static bool AllLowerSha256(params string[] values) =>
        values.All(IsLowerSha256);

    private static bool IsLowerSha256(string value) =>
        value.Length == 64
        && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f')
        );

    private static bool HashesEqual(string left, string right) =>
        IsLowerSha256(left)
        && IsLowerSha256(right)
        && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right)
        );

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private readonly record struct ValidatedOcrRecord(string OriginKind, long PageNumber);
}
