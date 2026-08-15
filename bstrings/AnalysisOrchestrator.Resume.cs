#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal static partial class AnalysisOrchestrator
{
    private const string LegacyV200Version = "2.0.0";
    private const string TranslationOffSourceVersion = "2.1.1";
    private const string TranslationOffTargetVersion = "2.1.2";

    private sealed record LegacyInputIdentity(
        long FileCount,
        string Inventory,
        string InventorySha256,
        string Manifest,
        string ManifestSha256,
        string ContentHashAlgorithm
    );

    private const string LegacyV200ManifestSha256 =
        "4bf9c7eb16fa3da2c2a30436cca0427eed5e77442072eeb67c37fbd3056f9a01";
    private const string LegacyV200ExecutableSha256 =
        "adb68b939552dd90a8a87be43e1cbdc89ef7f3ac9c577626545f7fec181335cf";

    private static readonly JsonSerializerOptions LegacyResumeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    private static readonly HashSet<string> LegacyRunProperties = new(
        [
            "schemaVersion",
            "status",
            "startedUtc",
            "completedUtc",
            "bstringsVersion",
            "input",
            "bundleIntegrity",
            "contentRouting",
            "engineStatuses",
            "translationRouting",
            "translationWork",
            "decoder",
            "preservationFallbacks",
            "options",
            "error",
        ],
        StringComparer.Ordinal
    );

    private static readonly HashSet<string> LegacyRootEntries = new(
        [
            ".incomplete",
            "content-routing.jsonl",
            "floss-input-files.txt",
            "floss-input-manifest.jsonl",
            "input-files.txt",
            "input-manifest.jsonl",
            "language-assessments.jsonl",
            "logs",
            "native-strings.jsonl",
            "ocr-assessments.jsonl",
            "ocr-input-files.txt",
            "ocr-input-manifest.jsonl",
            "ocr-strings.jsonl",
            "raw-strings.jsonl",
            "recovered-strings.jsonl",
            "run.json",
            "translation-candidates.jsonl",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    private static readonly HashSet<string> LegacyOptionalRootEntries = new(
        [AnalysisResumeCore.LockFileName],
        StringComparer.OrdinalIgnoreCase
    );

    internal static Task ResumeAsync(
        string outputDirectory,
        string? bundleRootOverride,
        CancellationToken cancellationToken = default
    ) => ResumeAsync(outputDirectory, bundleRootOverride, false, cancellationToken);

    internal static Task ResumeAsync(
        string outputDirectory,
        string? bundleRootOverride,
        bool excludeTranslation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var outputFullPath = Path.GetFullPath(outputDirectory);
        if (!Directory.Exists(outputFullPath))
        {
            throw new DirectoryNotFoundException(
                $"Resume results were not found: '{outputFullPath}'."
            );
        }
        if (!AnalysisResumeCore.HasResumeMetadata(outputFullPath))
        {
            if (excludeTranslation)
            {
                throw new InvalidDataException(
                    $"Translation exclusion requires a saved bstrings {TranslationOffSourceVersion} resume plan at stage 7."
                );
            }
            return ResumeLegacyV200Async(
                outputFullPath,
                bundleRootOverride,
                cancellationToken,
                validationOnly: false,
                executingExecutablePath: Environment.ProcessPath
            );
        }

        var saved = AnalysisResumeCore.ReadSpecification(outputFullPath);
        if (excludeTranslation)
        {
            return ResumeWithTranslationOffAsync(
                outputFullPath,
                bundleRootOverride,
                saved,
                cancellationToken
            );
        }
        var options = saved.Options.ToOptions(outputFullPath, bundleRootOverride);
        var executingPath = ResolveResumeExecutable(saved, bundleRootOverride);
        var derivedTranslationOffResume = AnalysisResumeTranslationOffCore.HasTransitionState(
            outputFullPath
        );
        return RunAsync(
            options,
            cancellationToken,
            executingExecutablePath: executingPath,
            resumeRequested: true,
            skipExternalToolchainPreflight: derivedTranslationOffResume
        );
    }

    private static async Task ResumeLegacyV200Async(
        string outputDirectory,
        string? bundleRootOverride,
        CancellationToken cancellationToken,
        bool validationOnly,
        string? executingExecutablePath
    )
    {
        Console.Error.WriteLine(
            $"Resume preflight: validating a stopped bstrings {LegacyV200Version} stage boundary. No output will change until validation succeeds."
        );
        EnsureNoReparsePoints(outputDirectory, "legacy analysis results path");
        RequireExactLegacyRootEntries(outputDirectory);

        var runPath = Path.Combine(outputDirectory, "run.json");
        using var runDocument = await ReadStrictLegacyRunAsync(runPath, cancellationToken);
        var root = runDocument.RootElement;
        if (
            RequiredLegacyInt32(root, "schemaVersion") != 1
            || !string.Equals(RequiredLegacyString(root, "status"), "incomplete", StringComparison.Ordinal)
            || !string.Equals(
                RequiredLegacyString(root, "bstringsVersion"),
                LegacyV200Version,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                $"Only an incomplete bstrings {LegacyV200Version} run with the legacy schema can use this importer."
            );
        }
        if (
            File.Exists(Path.Combine(outputDirectory, "summary.json"))
            || !File.Exists(Path.Combine(outputDirectory, ".incomplete"))
            || File.Exists(Path.Combine(outputDirectory, "native-strings.jsonl.incomplete"))
        )
        {
            throw new InvalidDataException(
                "The legacy result markers do not describe a stopped, committed extraction boundary."
            );
        }

        var recordedBundle = DeserializeLegacy<BundleIntegrity>(
            RequiredLegacyObject(root, "bundleIntegrity"),
            "legacy bundle identity"
        );
        if (
            !string.Equals(
                recordedBundle.ManifestSha256,
                LegacyV200ManifestSha256,
                StringComparison.Ordinal
            )
            || !string.Equals(
                recordedBundle.ExecutableSha256,
                LegacyV200ExecutableSha256,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                $"The legacy run does not match the immutable public bstrings {LegacyV200Version} bundle and executable identities."
            );
        }

        var recordedOptions = DeserializeLegacy<AnalysisOptions>(
            RequiredLegacyObject(root, "options"),
            "legacy analysis options"
        );
        ValidateLegacyResumeOptions(recordedOptions);
        var options = recordedOptions with
        {
            OutputDirectory = outputDirectory,
            BundleRoot = bundleRootOverride,
            PatternSelection = "all",
            RegexFilePath = null,
            DecoderMode = DecoderWorkflowMode.Off,
        };
        ValidateOptions(options);
        ValidateInputSource(options);
        var patterns = Program.ResolveAnalysisPatterns(
            options.PatternSelection,
            options.RegexFilePath
        );
        EnrichmentRegexPipelineCore.ValidatePatterns(patterns);

        var toolchain = AnalysisToolchainLocator.Locate(
            options.BundleRoot,
            options.Airgap,
            requireRecovery: true,
            requireTranslation: true,
            requireOcr: true,
            executingExecutablePath: executingExecutablePath
        );
        ValidateOutputOutsideBundle(outputDirectory, toolchain.BundleRoot);
        var specification = AnalysisResumeSpecification.Create(
            options,
            patterns,
            toolchain.BundleIntegrity,
            executingExecutablePath
        );

        var legacyInput = DeserializeLegacy<LegacyInputIdentity>(
            RequiredLegacyObject(root, "input"),
            "legacy input identity"
        );
        var inputIdentity = new InputManifestInfo(
            legacyInput.FileCount,
            legacyInput.Inventory,
            legacyInput.InventorySha256,
            legacyInput.Manifest,
            legacyInput.ManifestSha256,
            legacyInput.ContentHashAlgorithm
        );
        var inventoryPath = Path.Combine(outputDirectory, "input-files.txt");
        var inputManifestPath = Path.Combine(outputDirectory, "input-manifest.jsonl");
        var nativePath = Path.Combine(outputDirectory, "native-strings.jsonl");
        var recoveredPath = Path.Combine(outputDirectory, "recovered-strings.jsonl");
        var ocrPath = Path.Combine(outputDirectory, "ocr-strings.jsonl");
        var ocrAssessmentsPath = Path.Combine(outputDirectory, "ocr-assessments.jsonl");
        var rawPath = Path.Combine(outputDirectory, "raw-strings.jsonl");
        var assessmentsPath = Path.Combine(outputDirectory, "language-assessments.jsonl");
        var candidatesPath = Path.Combine(outputDirectory, "translation-candidates.jsonl");
        var triageOptions = new LanguageTriageOptions(
            options.TranslationTarget,
            options.LanguageDetectionMode,
            options.TranslationPolicy,
            options.LanguageConfidence,
            options.LanguageMargin,
            options.TranslationMinimumCharacters,
            options.TranslationMaximumCharacters,
            BatchSize: 2048,
            MaxDegreeOfParallelism: Math.Max(1, Environment.ProcessorCount)
        );
        async Task<IReadOnlyList<AnalysisResumeImportedCheckpoint>> ValidateBoundaryAsync(
            CancellationToken token
        )
        {
            RequireExactLegacyRootEntries(outputDirectory);
            using var currentRun = await ReadStrictLegacyRunAsync(runPath, token);
            var currentRoot = currentRun.RootElement;
            if (
                RequiredLegacyInt32(currentRoot, "schemaVersion") != 1
                || !string.Equals(
                    RequiredLegacyString(currentRoot, "status"),
                    "incomplete",
                    StringComparison.Ordinal
                )
                || !string.Equals(
                    RequiredLegacyString(currentRoot, "bstringsVersion"),
                    LegacyV200Version,
                    StringComparison.Ordinal
                )
                || DeserializeLegacy<BundleIntegrity>(
                    RequiredLegacyObject(currentRoot, "bundleIntegrity"),
                    "legacy bundle identity"
                ) != recordedBundle
                || DeserializeLegacy<AnalysisOptions>(
                    RequiredLegacyObject(currentRoot, "options"),
                    "legacy analysis options"
                ) != recordedOptions
                || DeserializeLegacy<LegacyInputIdentity>(
                    RequiredLegacyObject(currentRoot, "input"),
                    "legacy input identity"
                ) != legacyInput
                || File.Exists(Path.Combine(outputDirectory, "summary.json"))
                || !File.Exists(Path.Combine(outputDirectory, ".incomplete"))
                || File.Exists(Path.Combine(outputDirectory, "native-strings.jsonl.incomplete"))
            )
            {
                throw new InvalidDataException(
                    "The legacy run identity or committed-boundary markers changed during resume validation."
                );
            }

            Console.Error.WriteLine(
                "Resume preflight 1/6: verifying the input inventory and evidence hashes..."
            );
            await InputEvidenceManifest.VerifySelectionAsync(
                inventoryPath,
                EnumerateInputFiles(options),
                token
            );
            await InputEvidenceManifest.VerifyInventoryAsync(inventoryPath, inputIdentity, token);
            await InputEvidenceManifest.VerifyAsync(inputManifestPath, inputIdentity, token);

            Console.Error.WriteLine("Resume preflight 2/6: validating content routing...");
            var routing = await ValidateLegacyRoutingAsync(
                outputDirectory,
                inputManifestPath,
                inputIdentity,
                token
            );
            if (routing.FlossCandidates != 0 || routing.OcrCandidates != 0)
            {
                throw new InvalidDataException(
                    "Legacy import is limited to a boundary with zero routed FLOSS and OCR candidates."
                );
            }

            Console.Error.WriteLine(
                "Resume preflight 3/6: validating native extraction and record identities..."
            );
            var native = await ValidateLegacyNativeAsync(nativePath, inputManifestPath, token);
            await RequireEmptyFileAsync(recoveredPath, "recovered string output", token);
            await RequireEmptyFileAsync(ocrPath, "OCR string output", token);
            await RequireEmptyFileAsync(ocrAssessmentsPath, "OCR assessment output", token);

            Console.Error.WriteLine("Resume preflight 4/6: validating the exact raw merge...");
            var raw = await EnrichmentMergeCore.ValidateConcatenationAsync(
                [nativePath, recoveredPath, ocrPath],
                rawPath,
                token
            );

            Console.Error.WriteLine(
                "Resume preflight 5/6: replaying language assessments and candidate selection..."
            );
            var triage = await LanguageTriageCore.ValidateCompletedOutputsAsync(
                rawPath,
                candidatesPath,
                assessmentsPath,
                triageOptions,
                token
            );
            Console.Error.WriteLine(
                "Resume preflight 6/6: sealing stable artifact identities under the exclusive lease..."
            );

            return new[]
            {
                AnalysisResumeImportedCheckpoint.Create(
                    AnalysisResumeStage.InputInventory,
                    new[] { "input-files.txt", "input-manifest.jsonl" },
                    inputIdentity
                ),
                AnalysisResumeImportedCheckpoint.Create(
                    AnalysisResumeStage.ContentRouting,
                    new[]
                    {
                        "content-routing.jsonl",
                        "floss-input-files.txt",
                        "floss-input-manifest.jsonl",
                        "ocr-input-files.txt",
                        "ocr-input-manifest.jsonl",
                    },
                    routing
                ),
                AnalysisResumeImportedCheckpoint.Create(
                    AnalysisResumeStage.Native,
                    new[] { "native-strings.jsonl" },
                    native
                ),
                AnalysisResumeImportedCheckpoint.Create(
                    AnalysisResumeStage.Floss,
                    new[] { "recovered-strings.jsonl" },
                    new AnalysisResumeFlossStats(null, 0)
                ),
                AnalysisResumeImportedCheckpoint.Create(
                    AnalysisResumeStage.Ocr,
                    new[] { "ocr-strings.jsonl", "ocr-assessments.jsonl" },
                    new AnalysisResumeOcrStats(null, null, 0, 0)
                ),
                AnalysisResumeImportedCheckpoint.Create(
                    AnalysisResumeStage.RawMerge,
                    new[] { "raw-strings.jsonl" },
                    raw
                ),
                AnalysisResumeImportedCheckpoint.Create(
                    AnalysisResumeStage.TranslationSelection,
                    new[] { "language-assessments.jsonl", "translation-candidates.jsonl" },
                    new AnalysisResumeTranslationSelectionStats(
                        triage,
                        triage.TranslationCandidates
                    )
                ),
            };
        }

        if (validationOnly)
        {
            await ValidateBoundaryAsync(cancellationToken);
            Console.Error.WriteLine(
                "Resume preflight complete: the legacy boundary is valid and no output was changed."
            );
            return;
        }
        var verificationProgress = new ConsolePercentageProgress();
        var session = await AnalysisResumeCore.ImportExistingAsync(
            outputDirectory,
            specification,
            ValidateBoundaryAsync,
            (completed, total) =>
                verificationProgress.Report(
                    "legacy checkpoint import",
                    completed,
                    total,
                    "bytes"
                ),
            cancellationToken
        );
        Console.Error.WriteLine(
            "Resume preflight complete: validated extraction and language stages will be reused; translation will restart."
        );
        await using var importedSessionScope = session;
        await RunAsync(
            options,
            cancellationToken,
            executingExecutablePath: executingExecutablePath,
            existingResumeSession: session,
            resumeRequested: true
        );
    }

    private static async Task ResumeWithTranslationOffAsync(
        string outputDirectory,
        string? bundleRootOverride,
        AnalysisResumeSpecification saved,
        CancellationToken cancellationToken
    )
    {
        if (AnalysisResumeTranslationOffCore.HasTransitionState(outputDirectory))
        {
            throw new InvalidDataException(
                "This resume plan already excludes translation. Resume it without '-e translation'."
            );
        }
        if (
            !string.Equals(
                saved.BstringsVersion,
                TranslationOffSourceVersion,
                StringComparison.Ordinal
            )
            || !saved.Options.Full
            || saved.Options.TranslationMode != TranslationWorkflowMode.Auto
        )
        {
            throw new InvalidDataException(
                $"Resume can exclude translation only from a saved bstrings {TranslationOffSourceVersion} Full analysis with translation set to Auto."
            );
        }
        if (saved.BundleIntegrity is null)
        {
            throw new InvalidDataException(
                "The saved Auto plan has no verified source-kit identity. Translation cannot be excluded safely."
            );
        }

        Console.Error.WriteLine(
            "Resume change preflight: verifying the saved Auto plan before excluding translation. No output will change until validation succeeds."
        );
        var sourceExecutable = ResolveResumeExecutable(saved, bundleRootOverride);
        var sourceOptions = saved.Options.ToOptions(outputDirectory, bundleRootOverride);
        var sourceProgress = new ConsolePercentageProgress();
        var sourceToolchain = AnalysisToolchainLocator.Locate(
            bundleRootOverride,
            sourceOptions.Airgap || !string.IsNullOrWhiteSpace(bundleRootOverride),
            requireRecovery: sourceOptions.RecoveryMode != ExecutableRecoveryMode.Off,
            requireTranslation: true,
            requireOcr: sourceOptions.OcrMode != OcrWorkflowMode.Off,
            executingExecutablePath: sourceExecutable,
            verificationProgress: (completed, total) =>
                sourceProgress.Report("saved source-kit verification", completed, total, "bytes")
        );
        if (sourceToolchain.BundleIntegrity != saved.BundleIntegrity)
        {
            throw new InvalidDataException(
                "The source kit does not match the exact bundle recorded by the saved Auto plan. Supply that kit with --bundle-root."
            );
        }

        var targetExecutable = ResolveCurrentExecutable();
        var targetBundleRoot = Path.GetFullPath(AppContext.BaseDirectory);
        AnalysisToolchain targetToolchain;
        if (
            string.Equals(
                Path.TrimEndingDirectorySeparator(sourceToolchain.BundleRoot),
                Path.TrimEndingDirectorySeparator(targetBundleRoot),
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(
                AnalysisResumeCore.HashExecutingExecutable(targetExecutable),
                sourceToolchain.BundleIntegrity.ExecutableSha256,
                StringComparison.Ordinal
            )
        )
        {
            targetToolchain = sourceToolchain;
        }
        else
        {
            var targetProgress = new ConsolePercentageProgress();
            targetToolchain = AnalysisToolchainLocator.Locate(
                targetBundleRoot,
                requireExplicitBundle: true,
                requireRecovery: sourceOptions.RecoveryMode != ExecutableRecoveryMode.Off,
                requireTranslation: false,
                requireOcr: sourceOptions.OcrMode != OcrWorkflowMode.Off,
                executingExecutablePath: targetExecutable,
                verificationProgress: (completed, total) =>
                    targetProgress.Report("current target-kit verification", completed, total, "bytes")
            );
        }

        var targetOptions = saved.Options.ToOptions(outputDirectory, targetToolchain.BundleRoot) with
        {
            TranslationMode = TranslationWorkflowMode.Off,
        };
        ValidateOptions(targetOptions);
        ValidateInputSource(targetOptions);
        var patterns = Program.ResolveAnalysisPatterns(
            targetOptions.PatternSelection,
            targetOptions.RegexFilePath
        );
        EnrichmentRegexPipelineCore.ValidatePatterns(patterns);
        var targetSpecification = AnalysisResumeSpecification.Create(
            targetOptions,
            patterns,
            targetToolchain.BundleIntegrity,
            targetExecutable
        );
        if (
            !string.Equals(
                targetSpecification.BstringsVersion,
                TranslationOffTargetVersion,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                $"Translation exclusion must run from the bstrings {TranslationOffTargetVersion} kit."
            );
        }
        var transitionProgress = new ConsolePercentageProgress();
        var session = await AnalysisResumeTranslationOffCore.TransitionAsync(
            outputDirectory,
            saved,
            targetSpecification,
            (context, token) =>
                ValidateTranslationOffParentAsync(
                    context,
                    sourceOptions,
                    sourceToolchain,
                    token
                ),
            (completed, total) =>
                transitionProgress.Report(
                    "saved checkpoint verification",
                    completed,
                    total,
                    "bytes"
                ),
            cancellationToken: cancellationToken
        );
        Console.Error.WriteLine(
            "Resume change complete: stages 1-6 are inherited; translation selection will be replaced for translation Off."
        );
        await RunAsync(
            targetOptions,
            cancellationToken,
            executingExecutablePath: targetExecutable,
            existingResumeSession: session,
            resumeRequested: true,
            skipExternalToolchainPreflight: true,
            preverifiedToolchain: targetToolchain
        );
    }

    internal static async Task ValidateTranslationOffParentAsync(
        AnalysisResumeTranslationOffValidationContext context,
        AnalysisOptions sourceOptions,
        AnalysisToolchain sourceToolchain,
        CancellationToken cancellationToken
    )
    {
        var expectedStages = AnalysisResumeStage.All
            .Take(AnalysisResumeStage.TranslationSelection.Ordinal)
            .ToArray();
        if (!context.Stages.SequenceEqual(expectedStages))
        {
            throw new InvalidDataException(
                "Translation can be excluded only at the exact committed stage 7 boundary."
            );
        }

        var outputDirectory = context.OutputDirectory;
        var inventoryPath = Path.Combine(outputDirectory, "input-files.txt");
        var inputManifestPath = Path.Combine(outputDirectory, "input-manifest.jsonl");
        var routingManifestPath = Path.Combine(outputDirectory, "content-routing.jsonl");
        var flossInventoryPath = Path.Combine(outputDirectory, "floss-input-files.txt");
        var flossManifestPath = Path.Combine(outputDirectory, "floss-input-manifest.jsonl");
        var ocrInventoryPath = Path.Combine(outputDirectory, "ocr-input-files.txt");
        var ocrManifestPath = Path.Combine(outputDirectory, "ocr-input-manifest.jsonl");
        var nativePath = Path.Combine(outputDirectory, "native-strings.jsonl");
        var recoveredPath = Path.Combine(outputDirectory, "recovered-strings.jsonl");
        var ocrPath = Path.Combine(outputDirectory, "ocr-strings.jsonl");
        var ocrAssessmentsPath = Path.Combine(outputDirectory, "ocr-assessments.jsonl");
        var rawPath = Path.Combine(outputDirectory, "raw-strings.jsonl");
        var assessmentsPath = Path.Combine(outputDirectory, "language-assessments.jsonl");
        var candidatesPath = Path.Combine(outputDirectory, "translation-candidates.jsonl");
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "bstrings-translation-off-validation-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workingDirectory);
        try
        {
            Console.Error.WriteLine(
                "Resume change preflight 1/7: verifying the input inventory and evidence hashes..."
            );
            var input = context.GetStats<InputManifestInfo>(AnalysisResumeStage.InputInventory);
            await InputEvidenceManifest.VerifySelectionAsync(
                inventoryPath,
                EnumerateInputFiles(sourceOptions),
                cancellationToken
            );
            await InputEvidenceManifest.VerifyInventoryAsync(
                inventoryPath,
                input,
                cancellationToken
            );
            await InputEvidenceManifest.VerifyAsync(inputManifestPath, input, cancellationToken);

            Console.Error.WriteLine("Resume change preflight 2/7: validating content routing...");
            ContentRoutingStats? routing = null;
            var needsRouting =
                sourceOptions.RecoveryMode != ExecutableRecoveryMode.Off
                || sourceOptions.OcrMode != OcrWorkflowMode.Off;
            if (needsRouting)
            {
                routing = await ContentRoutingCore.ValidateAndProjectAsync(
                    inputManifestPath,
                    input,
                    routingManifestPath,
                    flossInventoryPath,
                    flossManifestPath,
                    ocrInventoryPath,
                    ocrManifestPath,
                    expectedNativeSelected:
                        sourceOptions.NativeExtractionMode == NativeExtractionMode.On,
                    cancellationToken,
                    expectedClassifierExecutable: context.LegacyImported
                        ? null
                        : sourceToolchain.MagikaExecutable,
                    writeProjections: false
                );
                RequireMatchingResumeStats(
                    AnalysisResumeStage.ContentRouting,
                    context.GetStats<ContentRoutingStats>(AnalysisResumeStage.ContentRouting),
                    routing.Value
                );
            }
            else
            {
                RequireMatchingResumeStats(
                    AnalysisResumeStage.ContentRouting,
                    context.GetStats<string>(AnalysisResumeStage.ContentRouting),
                    "disabled"
                );
            }

            Console.Error.WriteLine("Resume change preflight 3/7: validating native extraction...");
            var native = await NativeEnrichmentCompletionCore.ValidateAsync(
                nativePath,
                inputManifestPath,
                workingDirectory,
                context.LegacyImported
                    ? LegacyV200Version
                    : context.Specification.BstringsVersion,
                cancellationToken
            );
            RequireMatchingResumeStats(
                AnalysisResumeStage.Native,
                context.GetStats<NativeEnrichmentCompletionStats>(AnalysisResumeStage.Native),
                native
            );

            Console.Error.WriteLine("Resume change preflight 4/7: validating FLOSS output...");
            var floss = await ValidateFlossCheckpointAsync(
                recoveredPath,
                flossInventoryPath,
                flossManifestPath,
                routingManifestPath,
                routing,
                workingDirectory,
                cancellationToken
            );
            RequireMatchingResumeStats(
                AnalysisResumeStage.Floss,
                context.GetStats<AnalysisResumeFlossStats>(AnalysisResumeStage.Floss),
                floss
            );

            Console.Error.WriteLine("Resume change preflight 5/7: validating OCR output...");
            var ocrOutputCount = await CountStrictJsonlRecordsAsync(
                ocrPath,
                "OCR string output",
                cancellationToken
            );
            var ocrAssessmentCount = await CountStrictJsonlRecordsAsync(
                ocrAssessmentsPath,
                "OCR assessment output",
                cancellationToken
            );
            OcrCompletionStats? ocr = null;
            OcrValidationRequirements? ocrRequirements = null;
            if (
                sourceOptions.OcrMode != OcrWorkflowMode.Off
                && routing is { } routed
                && routed.OcrCandidates > 0
            )
            {
                ocrRequirements = OcrCompletionCore.CreateValidationRequirements(
                    sourceToolchain,
                    sourceOptions.OcrMode,
                    sourceOptions.OcrProvider,
                    sourceOptions.OcrThreads
                );
                ocr = await OcrCompletionCore.ValidateAsync(
                    ocrInventoryPath,
                    ocrManifestPath,
                    routed.OcrInput,
                    ocrPath,
                    ocrAssessmentsPath,
                    workingDirectory,
                    routed.OcrCandidates,
                    ocrRequirements,
                    cancellationToken
                );
            }
            var ocrStats = new AnalysisResumeOcrStats(
                ocr,
                ocrRequirements,
                ocrOutputCount.Records,
                ocrAssessmentCount.Records
            );
            RequireMatchingResumeStats(
                AnalysisResumeStage.Ocr,
                context.GetStats<AnalysisResumeOcrStats>(AnalysisResumeStage.Ocr),
                ocrStats
            );

            Console.Error.WriteLine("Resume change preflight 6/7: validating the raw merge...");
            var raw = await EnrichmentMergeCore.ValidateConcatenationAsync(
                [nativePath, recoveredPath, ocrPath],
                rawPath,
                cancellationToken
            );
            RequireMatchingResumeStats(
                AnalysisResumeStage.RawMerge,
                context.GetStats<EnrichmentMergeStats>(AnalysisResumeStage.RawMerge),
                raw
            );

            Console.Error.WriteLine(
                "Resume change preflight 7/7: replaying language assessments and candidate selection..."
            );
            var triageOptions = new LanguageTriageOptions(
                sourceOptions.TranslationTarget,
                sourceOptions.LanguageDetectionMode,
                sourceOptions.TranslationPolicy,
                sourceOptions.LanguageConfidence,
                sourceOptions.LanguageMargin,
                sourceOptions.TranslationMinimumCharacters,
                sourceOptions.TranslationMaximumCharacters,
                BatchSize: 2048,
                MaxDegreeOfParallelism: Math.Max(1, Environment.ProcessorCount)
            );
            var triage = await LanguageTriageCore.ValidateCompletedOutputsAsync(
                rawPath,
                candidatesPath,
                assessmentsPath,
                triageOptions,
                cancellationToken
            );
            RequireMatchingResumeStats(
                AnalysisResumeStage.TranslationSelection,
                context.GetStats<AnalysisResumeTranslationSelectionStats>(
                    AnalysisResumeStage.TranslationSelection
                ),
                new AnalysisResumeTranslationSelectionStats(
                    triage,
                    triage.TranslationCandidates
                )
            );
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void ValidateLegacyResumeOptions(AnalysisOptions recordedOptions)
    {
        if (
            !recordedOptions.Full
            || recordedOptions.NativeExtractionMode != NativeExtractionMode.On
            || recordedOptions.RecoveryMode == ExecutableRecoveryMode.Off
            || recordedOptions.OcrMode == OcrWorkflowMode.Off
            || recordedOptions.TranslationMode != TranslationWorkflowMode.Auto
            || !string.Equals(recordedOptions.PatternSelection, "all", StringComparison.Ordinal)
            || recordedOptions.RegexFilePath is not null
        )
        {
            throw new InvalidDataException(
                $"The legacy importer supports only the exact stopped {LegacyV200Version} Full analysis shape with built-in patterns."
            );
        }
    }

    internal static Task ValidateLegacyResumeAsync(
        string outputDirectory,
        string? bundleRootOverride,
        string? executingExecutablePath = null,
        CancellationToken cancellationToken = default
    ) =>
        ResumeLegacyV200Async(
            Path.GetFullPath(outputDirectory),
            bundleRootOverride,
            cancellationToken,
            validationOnly: true,
            executingExecutablePath: executingExecutablePath ?? Environment.ProcessPath
        );

    private static async Task<ContentRoutingStats> ValidateLegacyRoutingAsync(
        string outputDirectory,
        string inputManifestPath,
        InputManifestInfo inputIdentity,
        CancellationToken cancellationToken
    )
    {
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "bstrings-legacy-routing-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var routing = await ContentRoutingCore.ValidateAndProjectAsync(
                inputManifestPath,
                inputIdentity,
                Path.Combine(outputDirectory, "content-routing.jsonl"),
                Path.Combine(temporaryDirectory, "floss-input-files.txt"),
                Path.Combine(temporaryDirectory, "floss-input-manifest.jsonl"),
                Path.Combine(temporaryDirectory, "ocr-input-files.txt"),
                Path.Combine(temporaryDirectory, "ocr-input-manifest.jsonl"),
                expectedNativeSelected: true,
                cancellationToken
            );
            foreach (
                var name in new[]
                {
                    "floss-input-files.txt",
                    "floss-input-manifest.jsonl",
                    "ocr-input-files.txt",
                    "ocr-input-manifest.jsonl",
                }
            )
            {
                if (
                    !await FilesEqualAsync(
                        Path.Combine(temporaryDirectory, name),
                        Path.Combine(outputDirectory, name),
                        cancellationToken
                    )
                )
                {
                    throw new InvalidDataException(
                        $"Legacy content-routing projection '{name}' does not match its route manifest."
                    );
                }
            }
            return routing;
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<NativeEnrichmentCompletionStats> ValidateLegacyNativeAsync(
        string nativePath,
        string inputManifestPath,
        CancellationToken cancellationToken
    )
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            "bstrings-legacy-native-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(workingDirectory);
        try
        {
            return await NativeEnrichmentCompletionCore.ValidateAsync(
                nativePath,
                inputManifestPath,
                workingDirectory,
                LegacyV200Version,
                cancellationToken
            );
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<JsonDocument> ReadStrictLegacyRunAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Legacy run.json is invalid JSON.", ex);
        }
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("Legacy run.json is not a JSON object.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!LegacyRunProperties.Contains(property.Name) || !seen.Add(property.Name))
            {
                document.Dispose();
                throw new InvalidDataException(
                    $"Legacy run.json has an unexpected or duplicate property '{property.Name}'."
                );
            }
        }
        foreach (var required in new[] { "schemaVersion", "status", "bstringsVersion", "input", "bundleIntegrity", "options" })
        {
            if (!seen.Contains(required))
            {
                document.Dispose();
                throw new InvalidDataException(
                    $"Legacy run.json is missing required property '{required}'."
                );
            }
        }
        return document;
    }

    private static void RequireExactLegacyRootEntries(string outputDirectory)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(outputDirectory))
        {
            var name = Path.GetFileName(path);
            if (!LegacyRootEntries.Contains(name) && !LegacyOptionalRootEntries.Contains(name))
            {
                throw new InvalidDataException(
                    $"Legacy results contain unrecognized or downstream artifact '{name}'."
                );
            }
            EnsureNoReparsePoints(path, "legacy result artifact");
            if (
                string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase)
                    ? !Directory.Exists(path)
                    : !File.Exists(path)
            )
            {
                throw new InvalidDataException(
                    $"Legacy result artifact '{name}' has the wrong file type."
                );
            }
            if (
                LegacyOptionalRootEntries.Contains(name)
                && new FileInfo(path).Length != 0
            )
            {
                throw new InvalidDataException(
                    $"Legacy result artifact '{name}' is not an empty resume lease."
                );
            }
        }
        foreach (var name in LegacyRootEntries.Where(name => !string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase)))
        {
            if (!File.Exists(Path.Combine(outputDirectory, name)))
            {
                throw new InvalidDataException(
                    $"Legacy results are missing required boundary artifact '{name}'."
                );
            }
        }
    }

    private static JsonElement RequiredLegacyObject(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"Legacy run.json has invalid '{name}'.");
        }
        return value;
    }

    private static string RequiredLegacyString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Legacy run.json has invalid '{name}'.");
        }
        return value.GetString() ?? string.Empty;
    }

    private static int RequiredLegacyInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"Legacy run.json has invalid '{name}'.");
        }
        return result;
    }

    private static T DeserializeLegacy<T>(JsonElement value, string description)
    {
        try
        {
            return value.Deserialize<T>(LegacyResumeJsonOptions)
                ?? throw new InvalidDataException($"The {description} is missing.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The {description} is invalid.", ex);
        }
    }

    private static string ResolveResumeExecutable(
        AnalysisResumeSpecification saved,
        string? bundleRootOverride
    )
    {
        var candidates = new List<string?>();
        if (saved.BundleIntegrity is { } bundle && !string.IsNullOrWhiteSpace(bundleRootOverride))
        {
            var root = Path.GetFullPath(bundleRootOverride);
            var relative = bundle.Executable.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, relative));
            var containment = Path.GetRelativePath(root, candidate);
            if (
                Path.IsPathRooted(containment)
                || containment.Equals("..", StringComparison.Ordinal)
                || containment.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    "The saved executable path escapes the supplied source-kit root."
                );
            }
            candidates.Add(candidate);
        }
        candidates.Add(Environment.ProcessPath);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "bstrings.exe"));
        foreach (
            var candidate in candidates
        )
        {
            if (
                !string.IsNullOrWhiteSpace(candidate)
                && File.Exists(candidate)
                && string.Equals(
                    AnalysisResumeCore.HashExecutingExecutable(candidate),
                    saved.ExecutableSha256,
                    StringComparison.Ordinal
                )
            )
            {
                return candidate;
            }
        }
        throw new InvalidDataException(
            "The saved bstrings executable is unavailable. Supply the complete source kit recorded by this analysis with --bundle-root."
        );
    }

    private static string ResolveCurrentExecutable()
    {
        var executableName = OperatingSystem.IsWindows() ? "bstrings.exe" : "bstrings";
        var adjacent = Path.Combine(AppContext.BaseDirectory, executableName);
        if (File.Exists(adjacent))
        {
            return adjacent;
        }
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
        {
            return Environment.ProcessPath;
        }
        throw new FileNotFoundException(
            "The current adjacent bstrings executable is unavailable.",
            adjacent
        );
    }
}
