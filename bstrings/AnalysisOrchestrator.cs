#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace bstrings;

internal delegate bool LanguageDetectorAvailability(out string? error);

internal sealed record OcrAnalysisSummary(
    OcrWorkflowMode Mode,
    OcrProvider RequestedProvider,
    string? ResolvedProvider,
    int RequestedThreads,
    IReadOnlyDictionary<string, int> ResolvedThreadCounts,
    IReadOnlyDictionary<string, int> ResolvedWorkerCounts,
    string Engine,
    string EngineVersion,
    string Model,
    string Revision,
    string ModelSha256,
    string RuntimeSha256,
    string DetectorSha256,
    string RecognizerSha256,
    string ClassifierSha256,
    string DictionarySha256,
    long InputFiles,
    long ProcessedFiles,
    long NotApplicableFiles,
    long Pages,
    long StringRecords
);

internal sealed record TranslationRoutingSummary(
    string Mode,
    string PolicyVersion,
    string CodebookVersion,
    IReadOnlyDictionary<string, string> Codebook,
    string Assessments,
    int AssessmentSchemaVersion,
    long? Retained,
    long? ProspectiveBypasses,
    long? Unknown,
    long? RoutingEvaluations,
    long? DetectorEligibleRecords,
    long? DetectorExecutions,
    long? DetectorReuseHits
);

internal static class AnalysisOrchestrator
{
    private const string TranslationCachePrefix = ".bstrings-translation-cache-";
    private const string TranslationCacheDatabaseSuffix = ".sqlite3";
    private static readonly string[] TranslationCacheSidecarSuffixes =
        ["-wal", "-shm", "-journal"];
    private const uint FileReadAttributes = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private static readonly AsyncLocal<AnalysisStageProgress?> ActiveAnalysisProgress = new();

    private sealed class AnalysisStageProgress
    {
        private readonly object _gate = new();
        private readonly int _totalStages;
        private int _completedStages;
        private string? _currentStage;
        private double _lastOverallPercent = -1;
        private long _lastTimestamp;

        internal AnalysisStageProgress(int totalStages)
        {
            _totalStages = totalStages;
        }

        internal void Start(string name)
        {
            lock (_gate)
            {
                _currentStage = name;
                ReportLocked(0, "starting", force: true);
            }
        }

        internal void Report(long completed, long total)
        {
            if (total <= 0)
            {
                return;
            }
            lock (_gate)
            {
                ReportLocked(Math.Clamp(completed * 100.0 / total, 0, 100), null, force: false);
            }
        }

        internal void ReportChildLine(string line)
        {
            if (!TryExtractPercent(line, out var percent))
            {
                return;
            }
            lock (_gate)
            {
                ReportLocked(percent, line.Trim(), force: false);
            }
        }

        internal void Complete(string name, double elapsedSeconds)
        {
            lock (_gate)
            {
                _currentStage = name;
                ReportLocked(100, $"complete in {elapsedSeconds:N2} s", force: true);
                _completedStages++;
                _currentStage = null;
            }
        }

        internal void Fail(string name)
        {
            lock (_gate)
            {
                _currentStage = name;
                ReportLocked(0, "failed", force: true, retainLastFraction: true);
            }
        }

        private void ReportLocked(
            double stagePercent,
            string? detail,
            bool force,
            bool retainLastFraction = false
        )
        {
            var fraction = retainLastFraction
                ? Math.Max(0, _lastOverallPercent * _totalStages / 100.0 - _completedStages)
                : stagePercent / 100.0;
            var overall = Math.Clamp(
                (_completedStages + fraction) * 100.0 / _totalStages,
                0,
                100
            );
            var now = Stopwatch.GetTimestamp();
            var elapsed = _lastTimestamp == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(_lastTimestamp, now);
            if (
                !force
                && overall < 100
                && overall - _lastOverallPercent < 0.25
                && elapsed < TimeSpan.FromSeconds(5)
            )
            {
                return;
            }

            var stageNumber = Math.Min(_completedStages + 1, _totalStages);
            var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"; {detail}";
            Console.Error.WriteLine(
                string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"Progress: analysis: {overall:F1}% (stage {stageNumber:N0}/{_totalStages:N0}, {_currentStage}: {stagePercent:F1}%{suffix})"
                )
            );
            _lastOverallPercent = overall;
            _lastTimestamp = now;
        }

        private static bool TryExtractPercent(string line, out double percent)
        {
            percent = 0;
            var marker = line.IndexOf('%', StringComparison.Ordinal);
            if (marker <= 0)
            {
                return false;
            }
            var start = marker - 1;
            while (
                start >= 0
                && (char.IsDigit(line[start]) || line[start] is '.' or ',')
            )
            {
                start--;
            }
            var value = line.AsSpan(start + 1, marker - start - 1);
            return (
                    double.TryParse(
                        value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out percent
                    )
                    || double.TryParse(
                        value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.CurrentCulture,
                        out percent
                    )
                )
                && percent is >= 0 and <= 100;
        }
    }

    private sealed class AnalysisProgressScope : IDisposable
    {
        private readonly AnalysisStageProgress? _previous;

        internal AnalysisProgressScope(AnalysisStageProgress current)
        {
            _previous = ActiveAnalysisProgress.Value;
            ActiveAnalysisProgress.Value = current;
        }

        public void Dispose()
        {
            ActiveAnalysisProgress.Value = _previous;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal static async Task RunAsync(
        AnalysisOptions options,
        CancellationToken cancellationToken = default,
        LanguageDetectorAvailability? verifyLanguageDetector = null,
        string? executingExecutablePath = null,
        Func<string, CancellationToken, Task>? afterInputInventoryCreated = null,
        Func<CancellationToken, Task>? beforeFinalInputVerification = null
    )
    {
        ValidateOptions(options);
        ValidateInputSource(options);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        ValidateOutputLocation(options, outputDirectory);

        AnalysisToolchain? toolchain = null;
        var needsExternalToolchain =
            options.RecoveryMode != ExecutableRecoveryMode.Off
            || options.OcrMode != OcrWorkflowMode.Off
            || options.TranslationMode is TranslationWorkflowMode.Auto or TranslationWorkflowMode.All;
        var needsContentRouting =
            options.RecoveryMode != ExecutableRecoveryMode.Off
            || options.OcrMode != OcrWorkflowMode.Off;
        var stageProgress = new AnalysisStageProgress(
            CountPlannedStages(options, needsExternalToolchain)
        );
        using var progressScope = new AnalysisProgressScope(stageProgress);
        var hasImplicitBundle = AnalysisToolchainLocator.HasImplicitBundleConfiguration();
        if (
            needsExternalToolchain
            || !string.IsNullOrWhiteSpace(options.BundleRoot)
            || options.Airgap
            || hasImplicitBundle
        )
        {
            var bundleProgress = new ConsolePercentageProgress();
            toolchain = AnalysisToolchainLocator.Locate(
                options.BundleRoot,
                options.Airgap,
                requireRecovery: options.RecoveryMode != ExecutableRecoveryMode.Off,
                requireTranslation: options.TranslationMode
                    is TranslationWorkflowMode.Auto or TranslationWorkflowMode.All,
                requireOcr: options.OcrMode != OcrWorkflowMode.Off,
                executingExecutablePath: executingExecutablePath,
                verificationProgress: (completed, total) =>
                    bundleProgress.Report(
                        "bundle verification",
                        completed,
                        total,
                        "bytes"
                    )
            );
        }
        if (toolchain is not null)
        {
            ValidateOutputOutsideBundle(outputDirectory, toolchain.BundleRoot);
        }
        var bundleIntegrity = toolchain?.BundleIntegrity;
        verifyLanguageDetector ??= LanguageDetectionCore.TryVerifyAvailability;
        if (
            options.TranslationMode
                is TranslationWorkflowMode.Auto or TranslationWorkflowMode.DetectOnly
            && !verifyLanguageDetector(out var detectorError)
        )
        {
            throw new InvalidOperationException(
                "Offline language detection was requested but its bundled native engine is unavailable: "
                    + detectorError
            );
        }
        TranslationValidationRequirements? translationRequirements = null;
        if (options.TranslationMode is TranslationWorkflowMode.Auto or TranslationWorkflowMode.All)
        {
            translationRequirements = new TranslationValidationRequirements(
                "llama.cpp",
                options.TranslationTarget,
                toolchain!.TranslationModelId!,
                toolchain.TranslationModelRevision!,
                toolchain.TranslationModelSha256!
            );
        }
        OcrValidationRequirements? ocrRequirements = null;

        PrepareOutputDirectory(outputDirectory);
        var logsDirectory = Path.Combine(outputDirectory, "logs");
        Directory.CreateDirectory(logsDirectory);
        var incompleteMarker = Path.Combine(outputDirectory, ".incomplete");
        await File.WriteAllTextAsync(
            incompleteMarker,
            $"bstrings analysis is incomplete; processing started {DateTimeOffset.UtcNow:O}.{Environment.NewLine}",
            new UTF8Encoding(false),
            cancellationToken
        );

        var started = DateTimeOffset.UtcNow;
        var stageSeconds = new Dictionary<string, double>(StringComparer.Ordinal);
        var runPath = Path.Combine(outputDirectory, "run.json");
        var summaryPath = Path.Combine(outputDirectory, "summary.json");
        var inventoryPath = Path.Combine(outputDirectory, "input-files.txt");
        var inputManifestPath = Path.Combine(outputDirectory, "input-manifest.jsonl");
        var routingManifestPath = Path.Combine(outputDirectory, "content-routing.jsonl");
        var flossInventoryPath = Path.Combine(outputDirectory, "floss-input-files.txt");
        var flossManifestPath = Path.Combine(outputDirectory, "floss-input-manifest.jsonl");
        var ocrInventoryPath = Path.Combine(outputDirectory, "ocr-input-files.txt");
        var ocrInputManifestPath = Path.Combine(outputDirectory, "ocr-input-manifest.jsonl");
        var engineStatusPath = Path.Combine(outputDirectory, "engine-status.jsonl");
        long inputFileCount = 0;
        InputManifestInfo? inputManifest = null;
        ContentRoutingStats? routing = null;
        EngineStatusStats? engineStatuses = null;
        LanguageTriageStats? triage = null;
        TranslationWorkStats? translationWork = null;
        long completedMatchCount = 0;
        long completedStringCount = 0;

        try
        {
            var nativePath = Path.Combine(outputDirectory, "native-strings.jsonl");
            var recoveredPath = Path.Combine(outputDirectory, "recovered-strings.jsonl");
            var ocrPath = Path.Combine(outputDirectory, "ocr-strings.jsonl");
            var ocrAssessmentsPath = Path.Combine(outputDirectory, "ocr-assessments.jsonl");
            var rawPath = Path.Combine(outputDirectory, "raw-strings.jsonl");
            var candidatesPath = Path.Combine(outputDirectory, "translation-candidates.jsonl");
            var assessmentsPath = Path.Combine(outputDirectory, "language-assessments.jsonl");
            var translationsPath = Path.Combine(outputDirectory, "translated-strings.jsonl");
            var translationWorkStatsPath = Path.Combine(
                outputDirectory,
                "translation-work-stats.json"
            );
            var enrichedPath = Path.Combine(outputDirectory, "enriched-strings.jsonl");
            var matchesPath = Path.Combine(outputDirectory, "regex-matches.jsonl");
            var findingsPath = Path.Combine(outputDirectory, "findings.tsv");
            var patternHistogramPath = Path.Combine(outputDirectory, "pattern-histogram.tsv");
            var featureHistogramPath = Path.Combine(outputDirectory, "feature-histogram.tsv");
            var visualizationPath = Path.Combine(outputDirectory, "pattern-histogram.html");

            await RunStageAsync(
                "input inventory",
                stageSeconds,
                async () =>
                {
                    inputManifest = await InputEvidenceManifest.CreateAsync(
                        inventoryPath,
                        inputManifestPath,
                        EnumerateInputFiles(options),
                        cancellationToken
                    );
                    inputFileCount = inputManifest.FileCount;
                }
            );
            if (afterInputInventoryCreated is not null)
            {
                await afterInputInventoryCreated(inventoryPath, cancellationToken);
            }
            await WriteRunAsync(
                runPath,
                "incomplete",
                started,
                null,
                options,
                inputManifest,
                bundleIntegrity,
                routing,
                engineStatuses,
                triage,
                translationWork,
                null,
                null,
                cancellationToken
            );

            if (needsExternalToolchain)
            {
                await RunStageAsync(
                    "bundled tool preflight",
                    stageSeconds,
                    () => RunToolchainPreflightAsync(
                        options,
                        toolchain!,
                        outputDirectory,
                        logsDirectory,
                        cancellationToken
                    )
                );
            }

            if (needsContentRouting)
            {
                await RunStageAsync(
                    "content routing",
                    stageSeconds,
                    async () =>
                    {
                        await InputEvidenceManifest.VerifyInventoryAsync(
                            inventoryPath,
                            inputManifest!,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyAsync(
                            inputManifestPath,
                            inputManifest!,
                            cancellationToken
                        );
                        await RunContentRoutingAsync(
                            options,
                            toolchain!,
                            inventoryPath,
                            inputManifestPath,
                            routingManifestPath,
                            inputFileCount,
                            outputDirectory,
                            logsDirectory,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyInventoryAsync(
                            inventoryPath,
                            inputManifest!,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyAsync(
                            inputManifestPath,
                            inputManifest!,
                            cancellationToken
                        );
                        routing = await ContentRoutingCore.ValidateAndProjectAsync(
                            inputManifestPath,
                            inputManifest!,
                            routingManifestPath,
                            flossInventoryPath,
                            flossManifestPath,
                            ocrInventoryPath,
                            ocrInputManifestPath,
                            cancellationToken,
                            expectedClassifierExecutable: toolchain!.MagikaExecutable
                        );
                    }
                );
            }

            FileStream? nativeInventoryLease = null;
            FileStream? nativeManifestLease = null;
            try
            {
                await RunStageAsync(
                    "pre-native input inventory verification",
                    stageSeconds,
                    async () =>
                    {
                        nativeInventoryLease = await InputEvidenceManifest
                            .AcquireVerifiedInventoryLeaseAsync(
                                inventoryPath,
                                inputManifest!,
                                cancellationToken
                            );
                        nativeManifestLease = await ContentRoutingCore
                            .AcquireVerifiedFileLeaseAsync(
                                inputManifestPath,
                                inputManifest!.ManifestSha256,
                                "evidence content manifest",
                                cancellationToken
                            );
                    }
                );
                await RunStageAsync(
                    "native extraction",
                    stageSeconds,
                    () =>
                        RunNativeExtractionAsync(
                            options,
                            inventoryPath,
                            inputManifestPath,
                            nativePath,
                            outputDirectory,
                            logsDirectory,
                            executingExecutablePath,
                            cancellationToken
                        )
                );
            }
            finally
            {
                if (nativeInventoryLease is not null)
                {
                    await nativeInventoryLease.DisposeAsync();
                }
                if (nativeManifestLease is not null)
                {
                    await nativeManifestLease.DisposeAsync();
                }
            }
            await RunStageAsync(
                "post-native input verification",
                stageSeconds,
                async () =>
                {
                    await InputEvidenceManifest.VerifyInventoryAsync(
                        inventoryPath,
                        inputManifest!,
                        cancellationToken
                    );
                    await InputEvidenceManifest.VerifyAsync(
                        inputManifestPath,
                        inputManifest!,
                        cancellationToken
                    );
                }
            );

            if (options.RecoveryMode == ExecutableRecoveryMode.Off)
            {
                await CreateEmptyFileAtomicAsync(recoveredPath, cancellationToken);
            }
            else
            {
                FileStream? recoveryInventoryLease = null;
                FileStream? recoveryProjectionManifestLease = null;
                FileStream? recoveryRoutingManifestLease = null;
                await RunStageAsync(
                    "pre-recovery input inventory verification",
                    stageSeconds,
                    async () =>
                    {
                        try
                        {
                            recoveryInventoryLease = await InputEvidenceManifest
                                .AcquireVerifiedInventoryLeaseAsync(
                                    flossInventoryPath,
                                    routing!.Value.FlossInput,
                                    cancellationToken
                                );
                            recoveryProjectionManifestLease = await ContentRoutingCore
                                .AcquireVerifiedFileLeaseAsync(
                                    flossManifestPath,
                                    routing.Value.FlossInput.ManifestSha256,
                                    "FLOSS projection manifest",
                                    cancellationToken
                                );
                            recoveryRoutingManifestLease = await ContentRoutingCore
                                .AcquireVerifiedFileLeaseAsync(
                                    routingManifestPath,
                                    routing.Value.ManifestSha256,
                                    "content-routing manifest",
                                    cancellationToken
                                );
                        }
                        catch
                        {
                            if (recoveryRoutingManifestLease is not null)
                            {
                                await recoveryRoutingManifestLease.DisposeAsync();
                                recoveryRoutingManifestLease = null;
                            }
                            if (recoveryProjectionManifestLease is not null)
                            {
                                await recoveryProjectionManifestLease.DisposeAsync();
                                recoveryProjectionManifestLease = null;
                            }
                            if (recoveryInventoryLease is not null)
                            {
                                await recoveryInventoryLease.DisposeAsync();
                                recoveryInventoryLease = null;
                            }
                            throw;
                        }
                    }
                );
                try
                {
                    await RunStageAsync(
                        "routed FLOSS recovery",
                        stageSeconds,
                        async () =>
                        {
                            if (routing!.Value.FlossCandidates == 0)
                            {
                                Console.Error.WriteLine(
                                    "Stage skipped: routed FLOSS recovery (0 routed files)."
                                );
                                await CreateEmptyFileAtomicAsync(recoveredPath, cancellationToken);
                                return;
                            }
                            await ContentRoutingCore.VerifyManifestAsync(
                                routingManifestPath,
                                routing.Value.ManifestSha256,
                                cancellationToken
                            );
                            await RunFlossPreflightAsync(
                                toolchain!,
                                outputDirectory,
                                logsDirectory,
                                cancellationToken
                            );
                            await RunRecoveryAsync(
                                options,
                                toolchain!,
                                flossInventoryPath,
                                routingManifestPath,
                                recoveredPath,
                                routing.Value.FlossCandidates,
                                outputDirectory,
                                logsDirectory,
                                cancellationToken
                            );
                        }
                    );
                }
                finally
                {
                    if (recoveryRoutingManifestLease is not null)
                    {
                        await recoveryRoutingManifestLease.DisposeAsync();
                    }
                    if (recoveryProjectionManifestLease is not null)
                    {
                        await recoveryProjectionManifestLease.DisposeAsync();
                    }
                    if (recoveryInventoryLease is not null)
                    {
                        await recoveryInventoryLease.DisposeAsync();
                    }
                }
                await RunStageAsync(
                    "post-recovery input verification",
                    stageSeconds,
                    async () =>
                    {
                        await InputEvidenceManifest.VerifyInventoryAsync(
                            inventoryPath,
                            inputManifest!,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyAsync(
                            inputManifestPath,
                            inputManifest!,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyInventoryAsync(
                            flossInventoryPath,
                            routing!.Value.FlossInput,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyAsync(
                            flossManifestPath,
                            routing.Value.FlossInput,
                            cancellationToken
                        );
                        await ContentRoutingCore.VerifyManifestAsync(
                            routingManifestPath,
                            routing.Value.ManifestSha256,
                            cancellationToken
                        );
                    }
                );
            }

            OcrCompletionStats? ocr = null;
            if (options.OcrMode == OcrWorkflowMode.Off)
            {
                await CreateEmptyFileAtomicAsync(ocrPath, cancellationToken);
                await CreateEmptyFileAtomicAsync(ocrAssessmentsPath, cancellationToken);
            }
            else
            {
                FileStream? ocrInventoryLease = null;
                FileStream? ocrProjectionManifestLease = null;
                FileStream? ocrRoutingManifestLease = null;
                await RunStageAsync(
                    "pre-OCR input inventory verification",
                    stageSeconds,
                    async () =>
                    {
                        try
                        {
                            ocrInventoryLease = await InputEvidenceManifest
                                .AcquireVerifiedInventoryLeaseAsync(
                                    ocrInventoryPath,
                                    routing!.Value.OcrInput,
                                    cancellationToken
                                );
                            ocrProjectionManifestLease = await ContentRoutingCore
                                .AcquireVerifiedFileLeaseAsync(
                                    ocrInputManifestPath,
                                    routing.Value.OcrInput.ManifestSha256,
                                    "OCR projection manifest",
                                    cancellationToken
                                );
                            ocrRoutingManifestLease = await ContentRoutingCore
                                .AcquireVerifiedFileLeaseAsync(
                                    routingManifestPath,
                                    routing.Value.ManifestSha256,
                                    "content-routing manifest",
                                    cancellationToken
                                );
                        }
                        catch
                        {
                            if (ocrRoutingManifestLease is not null)
                            {
                                await ocrRoutingManifestLease.DisposeAsync();
                                ocrRoutingManifestLease = null;
                            }
                            if (ocrProjectionManifestLease is not null)
                            {
                                await ocrProjectionManifestLease.DisposeAsync();
                                ocrProjectionManifestLease = null;
                            }
                            if (ocrInventoryLease is not null)
                            {
                                await ocrInventoryLease.DisposeAsync();
                                ocrInventoryLease = null;
                            }
                            throw;
                        }
                    }
                );
                try
                {
                    await RunStageAsync(
                        "offline OCR",
                        stageSeconds,
                        async () =>
                        {
                            if (routing!.Value.OcrCandidates == 0)
                            {
                                Console.Error.WriteLine(
                                    "Stage skipped: offline OCR (0 routed files)."
                                );
                                await CreateEmptyFileAtomicAsync(ocrPath, cancellationToken);
                                await CreateEmptyFileAtomicAsync(
                                    ocrAssessmentsPath,
                                    cancellationToken
                                );
                                return;
                            }
                            Console.Error.WriteLine("Progress: OCR model verification: 0.0%");
                            ocrRequirements = OcrCompletionCore.CreateValidationRequirements(
                                toolchain!,
                                options.OcrMode,
                                options.OcrProvider,
                                options.OcrThreads
                            );
                            Console.Error.WriteLine("Progress: OCR model verification: 100.0%");
                            await RunOcrAsync(
                                options,
                                toolchain!,
                                ocrInventoryPath,
                                ocrInputManifestPath,
                                routingManifestPath,
                                ocrPath,
                                ocrAssessmentsPath,
                                routing.Value.OcrCandidates,
                                outputDirectory,
                                logsDirectory,
                                cancellationToken
                            );
                        }
                    );
                }
                finally
                {
                    if (ocrRoutingManifestLease is not null)
                    {
                        await ocrRoutingManifestLease.DisposeAsync();
                    }
                    if (ocrProjectionManifestLease is not null)
                    {
                        await ocrProjectionManifestLease.DisposeAsync();
                    }
                    if (ocrInventoryLease is not null)
                    {
                        await ocrInventoryLease.DisposeAsync();
                    }
                }
                await RunStageAsync(
                    "post-OCR input verification",
                    stageSeconds,
                    async () =>
                    {
                        await InputEvidenceManifest.VerifyInventoryAsync(
                            inventoryPath,
                            inputManifest!,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyAsync(
                            inputManifestPath,
                            inputManifest!,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyInventoryAsync(
                            ocrInventoryPath,
                            routing!.Value.OcrInput,
                            cancellationToken
                        );
                        await InputEvidenceManifest.VerifyAsync(
                            ocrInputManifestPath,
                            routing.Value.OcrInput,
                            cancellationToken
                        );
                    }
                );
                await RunStageAsync(
                    "OCR provenance validation",
                    stageSeconds,
                    async () =>
                    {
                        if (routing!.Value.OcrCandidates == 0)
                        {
                            return;
                        }
                        ocr = await OcrCompletionCore.ValidateAsync(
                            ocrInventoryPath,
                            ocrInputManifestPath,
                            routing.Value.OcrInput,
                            ocrPath,
                            ocrAssessmentsPath,
                            outputDirectory,
                            routing.Value.OcrCandidates,
                            ocrRequirements!,
                            cancellationToken
                        );
                    }
                );
            }

            EnrichmentMergeStats rawMerge = default;
            await RunStageAsync(
                "raw record merge",
                stageSeconds,
                async () =>
                    rawMerge = await EnrichmentMergeCore.ConcatenateAsync(
                        [nativePath, recoveredPath, ocrPath],
                        rawPath,
                        cancellationToken
                    )
            );

            long translationCandidateCount = 0;
            if (
                options.TranslationMode
                is TranslationWorkflowMode.Auto
                    or TranslationWorkflowMode.DetectOnly
            )
            {
                await RunStageAsync(
                    "offline language triage",
                    stageSeconds,
                    async () =>
                        triage = await LanguageTriageCore.ProcessAsync(
                            rawPath,
                            candidatesPath,
                            assessmentsPath,
                            new LanguageTriageOptions(
                                options.TranslationTarget,
                                options.LanguageDetectionMode,
                                options.TranslationPolicy,
                                options.LanguageConfidence,
                                options.LanguageMargin,
                                options.TranslationMinimumCharacters,
                                options.TranslationMaximumCharacters,
                                BatchSize: 2048,
                                MaxDegreeOfParallelism: Math.Max(1, Environment.ProcessorCount)
                            ),
                            cancellationToken,
                            detector: null,
                            progress: static (completed, total) =>
                                ActiveAnalysisProgress.Value?.Report(completed, total)
                        )
                );
                translationCandidateCount = triage!.Value.TranslationCandidates;
            }
            else
            {
                await CreateEmptyFileAtomicAsync(assessmentsPath, cancellationToken);
                if (options.TranslationMode == TranslationWorkflowMode.All)
                {
                    TranslationCandidateStats allCandidates = default;
                    await RunStageAsync(
                        "translation eligibility filtering",
                        stageSeconds,
                        async () =>
                            allCandidates = await TranslationCandidateCore.FilterEligibleAsync(
                                rawPath,
                                candidatesPath,
                                options.TranslationMinimumCharacters,
                                options.TranslationMaximumCharacters,
                                cancellationToken,
                                static (completed, total) =>
                                    ActiveAnalysisProgress.Value?.Report(completed, total)
                            )
                    );
                    translationCandidateCount = allCandidates.CandidateRecords;
                }
                else
                {
                    await CreateEmptyFileAtomicAsync(candidatesPath, cancellationToken);
                }
            }

            if (
                options.TranslationMode
                is TranslationWorkflowMode.Auto or TranslationWorkflowMode.All
            )
            {
                await RunStageAsync(
                    "offline translation",
                    stageSeconds,
                    async () =>
                    {
                        if (translationCandidateCount > 0)
                        {
                            translationWork = await RunTranslationAsync(
                                options,
                                toolchain!,
                                candidatesPath,
                                translationsPath,
                                translationWorkStatsPath,
                                translationCandidateCount,
                                outputDirectory,
                                logsDirectory,
                                cancellationToken
                            );
                        }
                        else
                        {
                            Console.Error.WriteLine(
                                "Stage skipped: offline translation (0 candidate records)."
                            );
                            await CreateEmptyFileAtomicAsync(
                                translationsPath,
                                cancellationToken
                            );
                            TranslationWorkStatsCore.ValidateAtomicDestination(
                                translationWorkStatsPath
                            );
                            await WriteJsonAtomicAsync(
                                translationWorkStatsPath,
                                new
                                {
                                    schemaVersion = TranslationWorkStatsCore.SchemaVersion,
                                    candidateOccurrences = 0,
                                    textDecisions = 0,
                                    protectedOnlyBypassTexts = 0,
                                    runCacheHits = 0,
                                    translationCacheHits = 0,
                                    translatorRequests = 0,
                                    translatorInputTexts = 0,
                                    modelResults = 0,
                                    modelFallbacks = 0,
                                    translatedChildOccurrences = 0,
                                    preservationFallbackChildOccurrences = 0,
                                },
                                cancellationToken
                            );
                            translationWork = await TranslationWorkStatsCore.ValidateAsync(
                                translationWorkStatsPath,
                                expectedCandidateOccurrences: 0,
                                cancellationToken
                            );
                        }
                    }
                );
            }
            else
            {
                await CreateEmptyFileAtomicAsync(translationsPath, cancellationToken);
            }

            if (
                options.TranslationMode
                is TranslationWorkflowMode.Auto or TranslationWorkflowMode.All
            )
            {
                await RunStageAsync(
                    "translation provenance validation",
                    stageSeconds,
                    () =>
                        TranslationCompletionCore.ValidateAsync(
                            candidatesPath,
                            translationsPath,
                            outputDirectory,
                            translationCandidateCount,
                            translationRequirements!,
                            cancellationToken
                        )
                );
            }

            EnrichmentMergeStats enrichedMerge = default;
            await RunStageAsync(
                "enriched record merge",
                stageSeconds,
                async () =>
                    enrichedMerge = await EnrichmentMergeCore.ConcatenateAsync(
                        [rawPath, translationsPath],
                        enrichedPath,
                        cancellationToken
                    )
            );

            var patterns = Program.ResolveAnalysisPatterns(
                options.PatternSelection,
                options.RegexFilePath
            );
            EnrichmentPipelineStats matches = default;
            await RunStageAsync(
                "pattern matching",
                stageSeconds,
                async () =>
                    matches = await EnrichmentRegexPipelineCore.ProcessAsync(
                        enrichedPath,
                        matchesPath,
                        patterns,
                        cancellationToken: cancellationToken,
                        trustedParentFirstInput: true,
                        translationRequirements: translationRequirements,
                        translationIntegritySourcePath: translationRequirements is null
                            ? null
                            : translationsPath
                    )
            );

            ForensicReportStats report = default;
            await RunStageAsync(
                "forensic report projection",
                stageSeconds,
                async () =>
                    report = await ForensicReportCore.WriteAsync(
                        matchesPath,
                        findingsPath,
                        patternHistogramPath,
                        featureHistogramPath,
                        visualizationPath,
                        patterns,
                        cancellationToken
                    )
            );

            if (routing is not null)
            {
                await RunStageAsync(
                    "engine status ledger",
                    stageSeconds,
                    async () =>
                        engineStatuses = await EngineStatusCore.WriteAsync(
                            routingManifestPath,
                            routing.Value.ManifestSha256,
                            routing.Value.InputFiles,
                            nativePath,
                            recoveredPath,
                            ocrAssessmentsPath,
                            engineStatusPath,
                            cancellationToken
                        )
                );
            }

            if (beforeFinalInputVerification is not null)
            {
                await beforeFinalInputVerification(cancellationToken);
            }
            await RunStageAsync(
                "final input verification",
                stageSeconds,
                async () =>
                {
                    await InputEvidenceManifest.VerifyInventoryAsync(
                        inventoryPath,
                        inputManifest!,
                        cancellationToken
                    );
                    await InputEvidenceManifest.VerifyAsync(
                        inputManifestPath,
                        inputManifest!,
                        cancellationToken
                    );
                    if (routing is not null)
                    {
                        await ContentRoutingCore.VerifyManifestAsync(
                            routingManifestPath,
                            routing.Value.ManifestSha256,
                            cancellationToken
                        );
                    }
                }
            );

            var completed = DateTimeOffset.UtcNow;
            var summary = new
            {
                schemaVersion = 1,
                status = "complete",
                startedUtc = started,
                completedUtc = completed,
                durationSeconds = (completed - started).TotalSeconds,
                inputFiles = inputFileCount,
                inputIdentity = new
                {
                    inventory = inputManifest!.InventoryFile,
                    inventorySha256 = inputManifest.InventorySha256,
                    manifest = inputManifest.ManifestFile,
                    manifestSha256 = inputManifest.ManifestSha256,
                    contentHashAlgorithm = inputManifest.ContentHashAlgorithm,
                },
                bundleIntegrity,
                contentRouting = routing is null
                    ? null
                    : new
                    {
                        manifest = routing.Value.Manifest,
                        manifestSha256 = routing.Value.ManifestSha256,
                        policyVersion = routing.Value.PolicyVersion,
                        inputFiles = routing.Value.InputFiles,
                        flossCandidates = routing.Value.FlossCandidates,
                        ocrCandidates = routing.Value.OcrCandidates,
                        classifierErrors = routing.Value.ClassifierErrors,
                        conflicts = routing.Value.Conflicts,
                    },
                engineStatuses = CreateEngineStatusSummary(engineStatuses),
                nativeStrings = rawMerge.InputRecords.Count > 0 ? rawMerge.InputRecords[0] : 0,
                recoveredStrings = rawMerge.InputRecords.Count > 1 ? rawMerge.InputRecords[1] : 0,
                ocrStrings = rawMerge.InputRecords.Count > 2 ? rawMerge.InputRecords[2] : 0,
                ocr = ocr is null
                    ? null
                    : CreateOcrSummary(options, ocrRequirements!, ocr.Value),
                rawStrings = rawMerge.OutputRecords,
                translationCandidates = translationCandidateCount,
                language = triage is null
                    ? null
                    : new
                    {
                        detector = "lingua-rs 1.8.0",
                        effectiveProfile = triage.Value.EffectiveMode.ToString().ToLowerInvariant(),
                        targetLanguage = options.TranslationTarget,
                        policy = options.TranslationPolicy,
                        targetLanguageRecords = triage.Value.TargetLanguageRecords,
                        translationCandidates = triage.Value.TranslationCandidates,
                        ambiguousRecords = triage.Value.AmbiguousRecords,
                        nonLinguisticRecords = triage.Value.NonLinguisticRecords,
                        detectorFailures = triage.Value.DetectorFailures,
                        translationRouting = CreateTranslationRoutingSummary(
                            options.TranslationMode,
                            triage
                        ),
                    },
                translatedStrings = enrichedMerge.InputRecords.Count > 1
                    ? enrichedMerge.InputRecords[1]
                    : 0,
                translationWork,
                enrichedStrings = enrichedMerge.OutputRecords,
                regexPatterns = patterns.Count,
                regexMatches = matches.MatchRecords,
                preservationFallbacks = matches.PreservationFallbackRecords,
                forensicReports = new
                {
                    findings = Path.GetFileName(findingsPath),
                    findingRows = report.FindingRows,
                    patternHistogram = Path.GetFileName(patternHistogramPath),
                    patternRows = report.PatternRows,
                    featureHistogram = Path.GetFileName(featureHistogramPath),
                    featureRows = report.FeatureRows,
                    visualization = Path.GetFileName(visualizationPath),
                },
                stageSeconds,
            };
            await WriteJsonAtomicAsync(
                summaryPath,
                summary,
                cancellationToken
            );
            await WriteRunAsync(
                runPath,
                "complete",
                started,
                completed,
                options,
                inputManifest,
                bundleIntegrity,
                routing,
                engineStatuses,
                triage,
                translationWork,
                matches,
                null,
                cancellationToken
            );
            File.Delete(incompleteMarker);
            completedMatchCount = matches.MatchRecords;
            completedStringCount = enrichedMerge.OutputRecords;
        }
        catch (Exception ex)
        {
            EnsureIncompleteMarker(incompleteMarker, started, ex.Message);
            DeleteTemporaryFile(summaryPath);
            try
            {
                await WriteRunAsync(
                    runPath,
                    "failed",
                    started,
                    DateTimeOffset.UtcNow,
                    options,
                    inputManifest,
                    bundleIntegrity,
                    routing,
                    engineStatuses,
                    triage,
                    translationWork,
                    null,
                    ex.Message,
                    CancellationToken.None
                );
            }
            catch (Exception writeError) when (
                writeError is IOException or UnauthorizedAccessException or JsonException
            )
            {
                Console.Error.WriteLine($"Could not update run.json after failure: {writeError.Message}");
            }
            throw;
        }

        Console.Error.WriteLine(
            $"Analysis complete: {completedMatchCount:N0} matches from "
                + $"{completedStringCount:N0} provenance-preserving strings."
        );
        Console.Error.WriteLine($"Results: {outputDirectory}");
    }

    private static async Task RunNativeExtractionAsync(
        AnalysisOptions options,
        string inventoryPath,
        string inputManifestPath,
        string outputPath,
        string workingDirectory,
        string logsDirectory,
        string? executingExecutablePath,
        CancellationToken cancellationToken
    )
    {
        var invocation = CurrentExecutableInvocation(executingExecutablePath);
        var arguments = new List<string>(invocation.PrefixArguments);
        arguments.Add("--paths-from");
        arguments.Add(inventoryPath);
        arguments.Add("--input-manifest");
        arguments.Add(inputManifestPath);
        arguments.Add("-o");
        arguments.Add(outputPath);
        arguments.Add("-m");
        arguments.Add(options.MinimumStringLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("-x");
        arguments.Add(options.MaximumStringLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--processor");
        arguments.Add(options.Processor);
        arguments.Add("--cpu-engine");
        arguments.Add(options.CpuEngine);
        arguments.Add("--emit-enrichment-jsonl");
        arguments.Add("-s");

        await ChildProcessRunner.RunAsync(
            invocation.Executable,
            arguments,
            workingDirectory,
            Path.Combine(logsDirectory, "native.stdout.log"),
            Path.Combine(logsDirectory, "native.stderr.log"),
            cancellationToken: cancellationToken,
            lineObserver: static line => ActiveAnalysisProgress.Value?.ReportChildLine(line)
        );
        if (!File.Exists(outputPath))
        {
            throw new InvalidDataException("Native extraction completed without its JSONL output.");
        }
        ValidateNativeOutputCompletion(outputPath);
    }

    internal static void ValidateNativeOutputCompletion(string outputPath)
    {
        var incompletePath = Path.GetFullPath(outputPath) + ".incomplete";
        if (File.Exists(incompletePath))
        {
            throw new InvalidDataException(
                $"Native extraction left its incomplete marker '{incompletePath}'."
            );
        }
    }

    private static async Task RunToolchainPreflightAsync(
        AnalysisOptions options,
        AnalysisToolchain toolchain,
        string workingDirectory,
        string logsDirectory,
        CancellationToken cancellationToken
    )
    {
        await ChildProcessRunner.RunAsync(
            toolchain.PythonExecutable,
            ["-I", "-B", toolchain.EnrichmentAdapter, "--help"],
            workingDirectory,
            Path.Combine(logsDirectory, "adapter-preflight.stdout.log"),
            Path.Combine(logsDirectory, "adapter-preflight.stderr.log"),
            OfflineEnvironment(),
            cancellationToken
        );
    }

    private static async Task RunContentRoutingAsync(
        AnalysisOptions options,
        AnalysisToolchain toolchain,
        string inventoryPath,
        string inputManifestPath,
        string outputPath,
        long totalFiles,
        string workingDirectory,
        string logsDirectory,
        CancellationToken cancellationToken
    )
    {
        var arguments = PythonPrefix(toolchain);
        arguments.Add("--airgap");
        arguments.Add("--triage-only");
        arguments.Add("--paths-from");
        arguments.Add(inventoryPath);
        arguments.Add("--input-manifest");
        arguments.Add(inputManifestPath);
        arguments.Add("-o");
        arguments.Add(outputPath);
        arguments.Add("--magika");
        arguments.Add(toolchain.MagikaExecutable!);
        arguments.Add("--progress-total-files");
        arguments.Add(totalFiles.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.RecoveryMode != ExecutableRecoveryMode.Off)
        {
            arguments.Add("--enable-floss");
        }
        if (options.OcrMode != OcrWorkflowMode.Off)
        {
            arguments.Add("--enable-ocr");
        }
        if (options.RecoveryMode == ExecutableRecoveryMode.Force)
        {
            arguments.Add("--force-floss");
        }
        await ChildProcessRunner.RunAsync(
            toolchain.PythonExecutable,
            arguments,
            workingDirectory,
            Path.Combine(logsDirectory, "content-routing.stdout.log"),
            Path.Combine(logsDirectory, "content-routing.stderr.log"),
            OfflineEnvironment(),
            cancellationToken,
            static line => ActiveAnalysisProgress.Value?.ReportChildLine(line)
        );
        if (!File.Exists(outputPath))
        {
            throw new InvalidDataException(
                "Content triage completed without its required routing manifest."
            );
        }
    }

    private static Task RunFlossPreflightAsync(
        AnalysisToolchain toolchain,
        string workingDirectory,
        string logsDirectory,
        CancellationToken cancellationToken
    ) =>
        ChildProcessRunner.RunAsync(
            toolchain.FlossExecutable!,
            ["--version"],
            workingDirectory,
            Path.Combine(logsDirectory, "floss-preflight.stdout.log"),
            Path.Combine(logsDirectory, "floss-preflight.stderr.log"),
            OfflineEnvironment(),
            cancellationToken
        );

    private static async Task RunRecoveryAsync(
        AnalysisOptions options,
        AnalysisToolchain toolchain,
        string inventoryPath,
        string routingManifestPath,
        string outputPath,
        long totalFiles,
        string workingDirectory,
        string logsDirectory,
        CancellationToken cancellationToken
    )
    {
        var arguments = PythonPrefix(toolchain);
        arguments.Add("--airgap");
        arguments.Add("--bounded-integrated-mode");
        arguments.Add("--paths-from");
        arguments.Add(inventoryPath);
        arguments.Add("--routing-manifest");
        arguments.Add(routingManifestPath);
        arguments.Add("-o");
        arguments.Add(outputPath);
        arguments.Add("--floss");
        arguments.Add(toolchain.FlossExecutable!);
        arguments.Add("--minimum-length");
        arguments.Add(options.MinimumStringLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--progress-total-files");
        arguments.Add(totalFiles.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.RecoveryMode == ExecutableRecoveryMode.Force)
        {
            arguments.Add("--force-floss");
        }
        await ChildProcessRunner.RunAsync(
            toolchain.PythonExecutable,
            arguments,
            workingDirectory,
            Path.Combine(logsDirectory, "recovery.stdout.log"),
            Path.Combine(logsDirectory, "recovery.stderr.log"),
            OfflineEnvironment(),
            cancellationToken,
            static line => ActiveAnalysisProgress.Value?.ReportChildLine(line)
        );
    }

    private static async Task RunOcrAsync(
        AnalysisOptions options,
        AnalysisToolchain toolchain,
        string inventoryPath,
        string inputManifestPath,
        string routingManifestPath,
        string stringsPath,
        string assessmentsPath,
        long totalFiles,
        string workingDirectory,
        string logsDirectory,
        CancellationToken cancellationToken
    )
    {
        var arguments = BuildOcrAnalysisArguments(
            toolchain,
            options.OcrMode,
            options.OcrProvider,
            options.OcrThreads,
            inventoryPath,
            inputManifestPath,
            routingManifestPath,
            stringsPath,
            assessmentsPath,
            totalFiles
        );

        await ChildProcessRunner.RunAsync(
            toolchain.OcrPythonExecutable!,
            arguments,
            workingDirectory,
            Path.Combine(logsDirectory, "ocr.stdout.log"),
            Path.Combine(logsDirectory, "ocr.stderr.log"),
            OfflineEnvironment(),
            cancellationToken,
            static line => ActiveAnalysisProgress.Value?.ReportChildLine(line)
        );
        if (!File.Exists(stringsPath) || !File.Exists(assessmentsPath))
        {
            throw new InvalidDataException(
                "Offline OCR completed without both required JSONL outputs."
            );
        }
    }

    private static async Task<TranslationWorkStats> RunTranslationAsync(
        AnalysisOptions options,
        AnalysisToolchain toolchain,
        string inputPath,
        string outputPath,
        string statsPath,
        long totalRecords,
        string workingDirectory,
        string logsDirectory,
        CancellationToken cancellationToken
    )
    {
        var arguments = PythonPrefix(toolchain);
        arguments.Add("--airgap");
        arguments.Add("--bounded-integrated-mode");
        arguments.Add("--input-jsonl");
        arguments.Add(inputPath);
        arguments.Add("-o");
        arguments.Add(outputPath);
        arguments.Add("--translate");
        arguments.Add("--translations-only");
        arguments.Add("--translation-engine");
        arguments.Add("llama-cpp");
        arguments.Add("--translation-model-path");
        arguments.Add(toolchain.TranslationModelPath!);
        arguments.Add("--translation-model-id");
        arguments.Add(toolchain.TranslationModelId!);
        arguments.Add("--translation-revision");
        arguments.Add(toolchain.TranslationModelRevision!);
        arguments.Add("--translation-model-sha256");
        arguments.Add(toolchain.TranslationModelSha256!);
        arguments.Add("--llama-server");
        arguments.Add(toolchain.LlamaServer!);
        arguments.Add("--translation-target");
        arguments.Add(options.TranslationTarget);
        arguments.Add("--translation-device");
        arguments.Add(options.TranslationDevice);
        arguments.Add("--translation-parallelism");
        arguments.Add(options.TranslationParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--translation-threads");
        arguments.Add(options.TranslationThreads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--translation-gpu-layers");
        arguments.Add(options.TranslationGpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (options.TranslationStrictDeterminism)
        {
            arguments.Add("--translation-strict-determinism");
        }
        arguments.Add("--translation-min-characters");
        arguments.Add(options.TranslationMinimumCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--translation-max-characters");
        arguments.Add(options.TranslationMaximumCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--progress-total-records");
        arguments.Add(totalRecords.ToString(System.Globalization.CultureInfo.InvariantCulture));
        arguments.Add("--translation-stats-output");
        arguments.Add(statsPath);

        await RunWithTranslationCacheCleanupAsync(
            outputPath,
            statsPath,
            () =>
                ChildProcessRunner.RunAsync(
                    toolchain.PythonExecutable,
                    arguments,
                    workingDirectory,
                    Path.Combine(logsDirectory, "translation.stdout.log"),
                    Path.Combine(logsDirectory, "translation.stderr.log"),
                    OfflineEnvironment(),
                    cancellationToken,
                    static line => ActiveAnalysisProgress.Value?.ReportChildLine(line)
                )
        );
        return await TranslationWorkStatsCore.ValidateAsync(
            statsPath,
            totalRecords,
            cancellationToken
        );
    }

    internal static async Task RunWithTranslationCacheCleanupAsync(
        string translationOutputPath,
        Func<Task> runChild
    ) =>
        await RunWithTranslationCacheCleanupAsync(
            translationOutputPath,
            translationStatsPath: null,
            runChild
        );

    internal static async Task RunWithTranslationCacheCleanupAsync(
        string translationOutputPath,
        string? translationStatsPath,
        Func<Task> runChild
    )
    {
        ArgumentNullException.ThrowIfNull(runChild);
        try
        {
            await runChild();
        }
        finally
        {
            CleanupTranslationCacheArtifacts(translationOutputPath);
            if (!string.IsNullOrWhiteSpace(translationStatsPath))
            {
                CleanupTranslationCacheArtifacts(translationStatsPath);
            }
        }
    }

    internal static void CleanupTranslationCacheArtifacts(
        string translationOutputPath,
        Func<string, FileAttributes>? getAttributes = null,
        Action<string>? deleteFile = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(translationOutputPath);
        getAttributes ??= File.GetAttributes;
        deleteFile ??= File.Delete;

        var outputPath = Path.GetFullPath(translationOutputPath);
        var outputDirectory = Path.GetDirectoryName(outputPath);
        var outputFileName = Path.GetFileName(outputPath);
        if (string.IsNullOrEmpty(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            throw new DirectoryNotFoundException(
                $"The translation output directory does not exist: '{outputDirectory}'."
            );
        }
        if (string.IsNullOrEmpty(outputFileName))
        {
            throw new ArgumentException(
                $"The translation output path does not identify a file: '{translationOutputPath}'.",
                nameof(translationOutputPath)
            );
        }

        EnsureNoReparsePoints(
            outputDirectory,
            "translation output directory",
            getAttributes: getAttributes
        );
        var outputDirectoryAttributes = getAttributes(outputDirectory);
        if (
            (outputDirectoryAttributes & FileAttributes.Directory) == 0
            || (outputDirectoryAttributes & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new IOException(
                $"The translation output directory is not a physical directory: '{outputDirectory}'."
            );
        }

        var artifacts = Directory
            .EnumerateFileSystemEntries(
                outputDirectory,
                "*",
                SearchOption.TopDirectoryOnly
            )
            .Where(path =>
                IsTranslationRunArtifactName(Path.GetFileName(path), outputFileName)
            )
            .Select(Path.GetFullPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var artifact in artifacts)
        {
            ValidateTranslationCacheArtifact(artifact, outputDirectory, getAttributes);
        }

        foreach (var artifact in artifacts)
        {
            if (!File.Exists(artifact) && !Directory.Exists(artifact))
            {
                continue;
            }
            ValidateTranslationCacheArtifact(artifact, outputDirectory, getAttributes);
            deleteFile(artifact);
        }

        var remaining = Directory
            .EnumerateFileSystemEntries(
                outputDirectory,
                "*",
                SearchOption.TopDirectoryOnly
            )
            .Where(path =>
                IsTranslationRunArtifactName(Path.GetFileName(path), outputFileName)
            )
            .ToArray();
        if (remaining.Length != 0)
        {
            throw new IOException(
                "Translation cleanup did not remove all exact cache or staged-output artifacts: "
                    + string.Join(", ", remaining.Select(Path.GetFileName))
            );
        }
    }

    private static void ValidateTranslationCacheArtifact(
        string artifact,
        string outputDirectory,
        Func<string, FileAttributes> getAttributes
    )
    {
        var parent = Path.GetDirectoryName(artifact);
        if (!string.Equals(parent, outputDirectory, FileSystemPathComparison))
        {
            throw new IOException(
                $"Translation cache cleanup escaped the exact output directory: '{artifact}'."
            );
        }

        var attributes = getAttributes(artifact);
        if (
            (attributes & FileAttributes.Directory) != 0
            || (attributes & FileAttributes.ReparsePoint) != 0
        )
        {
            throw new IOException(
                $"Refusing to remove an ambiguous or reparse translation cache artifact: '{artifact}'."
            );
        }
    }

    private static bool IsTranslationRunArtifactName(
        string name,
        string translationOutputFileName
    )
    {
        if (IsTranslationCacheArtifactName(name))
        {
            return true;
        }

        var partialPrefix = translationOutputFileName + ".partial.";
        return name.StartsWith(partialPrefix, StringComparison.Ordinal)
            && name.Length > partialPrefix.Length;
    }

    private static bool IsTranslationCacheArtifactName(string name)
    {
        if (!name.StartsWith(TranslationCachePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var databaseName = name;
        foreach (var sidecarSuffix in TranslationCacheSidecarSuffixes)
        {
            if (name.EndsWith(sidecarSuffix, StringComparison.Ordinal))
            {
                databaseName = name[..^sidecarSuffix.Length];
                break;
            }
        }
        if (!databaseName.EndsWith(TranslationCacheDatabaseSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        var tokenLength =
            databaseName.Length
            - TranslationCachePrefix.Length
            - TranslationCacheDatabaseSuffix.Length;
        return tokenLength > 0;
    }

    private static StringComparison FileSystemPathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static List<string> PythonPrefix(AnalysisToolchain toolchain) =>
        ["-I", "-B", toolchain.EnrichmentAdapter];

    private static List<string> OcrPythonPrefix(AnalysisToolchain toolchain) =>
        ["-I", "-B", toolchain.OcrAdapter!];

    internal static List<string> BuildOcrPreflightArguments(
        AnalysisToolchain toolchain,
        OcrProvider provider,
        int threads
    )
    {
        var arguments = OcrPythonPrefix(toolchain);
        arguments.Add("--airgap");
        arguments.Add("--self-test");
        AddOcrIdentityArguments(arguments, toolchain);
        AddOcrProviderArgument(arguments, provider);
        AddOcrThreadsArgument(arguments, threads);
        return arguments;
    }

    internal static List<string> BuildOcrAnalysisArguments(
        AnalysisToolchain toolchain,
        OcrWorkflowMode mode,
        OcrProvider provider,
        int threads,
        string inventoryPath,
        string inputManifestPath,
        string routingManifestPath,
        string stringsPath,
        string assessmentsPath,
        long totalFiles
    )
    {
        if (mode is not (OcrWorkflowMode.Auto or OcrWorkflowMode.Force))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        if (totalFiles < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalFiles));
        }

        var arguments = OcrPythonPrefix(toolchain);
        arguments.Add("--airgap");
        arguments.Add("--paths-from");
        arguments.Add(inventoryPath);
        arguments.Add("--input-manifest");
        arguments.Add(inputManifestPath);
        arguments.Add("--routing-manifest");
        arguments.Add(routingManifestPath);
        arguments.Add("--output");
        arguments.Add(stringsPath);
        arguments.Add("--assessments-output");
        arguments.Add(assessmentsPath);
        AddOcrIdentityArguments(arguments, toolchain);
        AddOcrProviderArgument(arguments, provider);
        AddOcrThreadsArgument(arguments, threads);
        arguments.Add("--ocr-mode");
        arguments.Add(mode.ToString().ToLowerInvariant());
        arguments.Add("--progress-total-files");
        arguments.Add(totalFiles.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return arguments;
    }

    private static void AddOcrProviderArgument(
        List<string> arguments,
        OcrProvider provider
    )
    {
        arguments.Add("--provider");
        arguments.Add(OcrCompletionCore.ProviderArgument(provider));
    }

    private static void AddOcrThreadsArgument(List<string> arguments, int threads)
    {
        OcrCompletionCore.ValidateRequestedThreads(threads);
        arguments.Add("--threads");
        arguments.Add(threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AddOcrIdentityArguments(
        List<string> arguments,
        AnalysisToolchain toolchain
    )
    {
        arguments.Add("--ocr-executable");
        arguments.Add(toolchain.OcrExecutable!);
        arguments.Add("--ocr-engine");
        arguments.Add(toolchain.OcrEngine!);
        arguments.Add("--ocr-engine-version");
        arguments.Add(toolchain.OcrEngineVersion!);
        arguments.Add("--ocr-model-path");
        arguments.Add(toolchain.OcrModelPath!);
        arguments.Add("--ocr-model-id");
        arguments.Add(toolchain.OcrModelId!);
        arguments.Add("--ocr-model-revision");
        arguments.Add(toolchain.OcrModelRevision!);
        arguments.Add("--ocr-model-sha256");
        arguments.Add(toolchain.OcrModelSha256!);
    }

    internal static OcrAnalysisSummary CreateOcrSummary(
        AnalysisOptions options,
        OcrValidationRequirements requirements,
        OcrCompletionStats stats
    ) =>
        new(
            options.OcrMode,
            options.OcrProvider,
            stats.ResolvedProvider,
            stats.RequestedThreads,
            stats.ResolvedThreadCounts,
            stats.ResolvedWorkerCounts,
            requirements.Engine,
            requirements.EngineVersion,
            requirements.Model,
            requirements.Revision,
            requirements.ModelSha256,
            requirements.RuntimeSha256,
            requirements.DetectorSha256,
            requirements.RecognizerSha256,
            requirements.ClassifierSha256,
            requirements.DictionarySha256,
            stats.InputFiles,
            stats.ProcessedFiles,
            stats.NotApplicableFiles,
            stats.Pages,
            stats.StringRecords
        );

    private static IReadOnlyDictionary<string, string?> OfflineEnvironment() =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["HF_HUB_OFFLINE"] = "1",
            ["TRANSFORMERS_OFFLINE"] = "1",
            ["HF_DATASETS_OFFLINE"] = "1",
            ["TOKENIZERS_PARALLELISM"] = "false",
            ["NO_PROXY"] = "127.0.0.1,localhost",
        };

    private static (string Executable, IReadOnlyList<string> PrefixArguments) CurrentExecutableInvocation(
        string? executingExecutablePath = null
    )
    {
        var processPath = string.IsNullOrWhiteSpace(executingExecutablePath)
            ? Environment.ProcessPath
            : Path.GetFullPath(executingExecutablePath);
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("The current bstrings executable path is unavailable.");
        }
        var executableName = Path.GetFileNameWithoutExtension(processPath);
        if (string.Equals(executableName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assemblyName = typeof(AnalysisOrchestrator).Assembly.GetName().Name;
            if (string.IsNullOrWhiteSpace(assemblyName))
            {
                throw new InvalidOperationException("The bstrings assembly name is unavailable.");
            }
            var assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            if (!File.Exists(assemblyPath))
            {
                throw new FileNotFoundException(
                    "The framework-dependent bstrings assembly is unavailable.",
                    assemblyPath
                );
            }
            return (processPath, [assemblyPath]);
        }
        return (processPath, Array.Empty<string>());
    }

    private static void ValidateInputSource(AnalysisOptions options)
    {
        var hasFile = !string.IsNullOrWhiteSpace(options.FilePath);
        var hasDirectory = !string.IsNullOrWhiteSpace(options.DirectoryPath);
        if (hasFile == hasDirectory)
        {
            throw new ArgumentException("Specify exactly one evidence file (-f) or directory (-d).");
        }
        if (hasFile && !File.Exists(options.FilePath))
        {
            throw new FileNotFoundException("The evidence file was not found.", options.FilePath);
        }
        if (hasDirectory && !Directory.Exists(options.DirectoryPath))
        {
            throw new DirectoryNotFoundException(
                $"The evidence directory was not found: '{options.DirectoryPath}'."
            );
        }
    }

    private static void ValidateOptions(AnalysisOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new ArgumentException("A results directory is required with -o.");
        }
        if (!Enum.IsDefined(options.OcrMode) || !Enum.IsDefined(options.OcrProvider))
        {
            throw new ArgumentException("OCR mode or provider is invalid.");
        }
        OcrCompletionCore.ValidateRequestedThreads(options.OcrThreads);
        AnalysisCli.ValidateStringLengthBounds(
            options.MinimumStringLength,
            options.MaximumStringLength
        );
        if (
            options.TranslationMinimumCharacters < 1
            || options.TranslationMaximumCharacters < options.TranslationMinimumCharacters
        )
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Invalid translation character bounds.");
        }
        AnalysisCli.ValidateTranslationScheduling(
            options.TranslationParallelism,
            options.TranslationThreads,
            options.TranslationStrictDeterminism
        );
        if (options.TranslationGpuLayers < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Translation GPU layers must be -1 or non-negative.");
        }
        var device = options.TranslationDevice.Trim().ToLowerInvariant();
        if (device is not ("auto" or "cpu" or "cuda" or "hybrid"))
        {
            throw new ArgumentException("Translation device must be auto, cpu, cuda, or hybrid.");
        }
        if (device == "hybrid" && options.TranslationGpuLayers <= 0)
        {
            throw new ArgumentException("Hybrid translation requires --translation-gpu-layers above zero.");
        }
        if (device != "hybrid" && options.TranslationGpuLayers > 0)
        {
            throw new ArgumentException("Exact positive GPU layers require --translation-device hybrid.");
        }
        if (
            !LanguageDetectionCore.TryNormalizeTargetLanguage(
                options.TranslationTarget,
                out _,
                out var targetError
            )
        )
        {
            throw new ArgumentException(targetError);
        }
        if (!File.Exists(options.RegexFilePath) && !string.IsNullOrWhiteSpace(options.RegexFilePath))
        {
            throw new FileNotFoundException("The regex file was not found.", options.RegexFilePath);
        }
    }

    private static void ValidateOutputLocation(AnalysisOptions options, string outputDirectory)
    {
        EnsureNoReparsePoints(outputDirectory, "results path");
        if (!string.IsNullOrWhiteSpace(options.DirectoryPath))
        {
            EnsureNoReparsePoints(Path.GetFullPath(options.DirectoryPath), "evidence directory");
            var inputDirectory = CanonicalPathForContainment(options.DirectoryPath);
            var output = CanonicalPathForContainment(outputDirectory);
            if (
                string.Equals(inputDirectory, output, StringComparison.OrdinalIgnoreCase)
                || output.StartsWith(inputDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            )
            {
                throw new ArgumentException("The results directory cannot be inside the evidence directory.");
            }
        }
        else
        {
            EnsureNoReparsePoints(Path.GetFullPath(options.FilePath!), "evidence file");
        }
    }

    private static void ValidateOutputOutsideBundle(
        string outputDirectory,
        string bundleRoot
    )
    {
        var canonicalOutput = CanonicalPathForContainment(outputDirectory);
        var canonicalBundle = CanonicalPathForContainment(bundleRoot);
        if (
            string.Equals(canonicalOutput, canonicalBundle, StringComparison.OrdinalIgnoreCase)
            || canonicalOutput.StartsWith(
                canonicalBundle + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new ArgumentException(
                "The results directory cannot be equal to or inside the verified bundle directory."
            );
        }
    }

    private static string CanonicalPathForContainment(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        var missingSegments = new Stack<string>();
        var existing = fullPath;
        while (!Directory.Exists(existing))
        {
            if (File.Exists(existing))
            {
                throw new IOException(
                    $"A directory path resolves through a regular file: '{fullPath}'."
                );
            }
            var parent = Directory.GetParent(existing)?.FullName;
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException(
                    $"No existing directory ancestor could be resolved for '{fullPath}'."
                );
            }
            var segment = Path.GetFileName(existing);
            if (string.IsNullOrEmpty(segment))
            {
                throw new InvalidDataException(
                    $"A non-canonical directory path cannot be compared safely: '{fullPath}'."
                );
            }
            missingSegments.Push(segment);
            existing = parent;
        }

        var canonical = FinalWindowsPath(existing);
        while (missingSegments.Count != 0)
        {
            canonical = Path.Combine(canonical, missingSegments.Pop());
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonical));
    }

    private static string FinalWindowsPath(string existingDirectory)
    {
        using var handle = CreateFileW(
            existingDirectory,
            FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                $"Could not resolve the final directory path for '{existingDirectory}'."
            );
        }

        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    $"Could not resolve the final directory path for '{existingDirectory}'."
                );
            }
            if (length < buffer.Capacity)
            {
                var resolved = buffer.ToString();
                if (resolved.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    return @"\\" + resolved[8..];
                }
                if (resolved.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    return resolved[4..];
                }
                return resolved;
            }
            capacity = checked((int)length + 1);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags
    );

    private static void PrepareOutputDirectory(string outputDirectory)
    {
        if (Directory.Exists(outputDirectory))
        {
            if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            {
                throw new IOException(
                    $"Results directory must be new or empty; refusing to overwrite '{outputDirectory}'."
                );
            }
        }
        else
        {
            Directory.CreateDirectory(outputDirectory);
        }
        EnsureNoReparsePoints(outputDirectory, "results path");
    }

    internal static void EnsureNoReparsePoints(
        string path,
        string description,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null,
        Func<string, FileAttributes>? getAttributes = null
    )
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;
        getAttributes ??= File.GetAttributes;
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException($"The {description} has no filesystem root: '{path}'.");
        }

        var current = root;
        var relative = fullPath[root.Length..];
        foreach (
            var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            current = Path.Combine(current, segment);
            if (!fileExists(current) && !directoryExists(current))
            {
                break;
            }
            if ((getAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ArgumentException(
                    $"The {description} traverses the reparse point '{current}'. "
                        + "Use a direct physical path so evidence containment can be enforced."
                );
            }
        }
    }

    private static IEnumerable<string> EnumerateInputFiles(AnalysisOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.FilePath))
        {
            yield return Path.GetFullPath(options.FilePath);
            yield break;
        }

        var enumerationOptions = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchType = MatchType.Simple,
        };
        var directory = Path.GetFullPath(options.DirectoryPath!);
        var mask = string.IsNullOrWhiteSpace(options.Mask) ? "*" : options.Mask;
        foreach (var input in Directory.EnumerateFiles(directory, mask, enumerationOptions))
        {
            yield return input;
        }
    }

    private static async Task CreateEmptyFileAtomicAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var temporaryPath = path + ".partial." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                string.Empty,
                new UTF8Encoding(false),
                cancellationToken
            );
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                DeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private static async Task RunStageAsync(
        string name,
        IDictionary<string, double> timings,
        Func<Task> action
    )
    {
        var progress = ActiveAnalysisProgress.Value;
        progress?.Start(name);
        Console.Error.WriteLine($"Stage: {name}...");
        var started = Stopwatch.GetTimestamp();
        try
        {
            await action();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            timings[name] = elapsed;
            Console.Error.WriteLine($"Stage complete: {name} ({elapsed:N2} s)");
            progress?.Complete(name, elapsed);
        }
        catch
        {
            progress?.Fail(name);
            throw;
        }
    }

    internal static int CountPlannedStages(AnalysisOptions options, bool needsExternalToolchain)
    {
        var count = 9;
        if (needsExternalToolchain)
        {
            count++;
        }
        if (
            options.RecoveryMode != ExecutableRecoveryMode.Off
            || options.OcrMode != OcrWorkflowMode.Off
        )
        {
            count += 2;
        }
        if (options.RecoveryMode != ExecutableRecoveryMode.Off)
        {
            count += 3;
        }
        if (options.OcrMode != OcrWorkflowMode.Off)
        {
            count += 4;
        }
        count += options.TranslationMode switch
        {
            TranslationWorkflowMode.Auto or TranslationWorkflowMode.All => 3,
            TranslationWorkflowMode.DetectOnly => 1,
            _ => 0,
        };
        return count;
    }

    private static async Task WriteRunAsync(
        string path,
        string status,
        DateTimeOffset started,
        DateTimeOffset? completed,
        AnalysisOptions options,
        InputManifestInfo? inputManifest,
        BundleIntegrity? bundleIntegrity,
        ContentRoutingStats? routing,
        EngineStatusStats? engineStatuses,
        LanguageTriageStats? triage,
        TranslationWorkStats? translationWork,
        EnrichmentPipelineStats? enrichmentStats,
        string? error,
        CancellationToken cancellationToken
    )
    {
        var record = new
        {
            schemaVersion = 1,
            status,
            startedUtc = started,
            completedUtc = completed,
            bstringsVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
            input = new
            {
                fileCount = inputManifest?.FileCount ?? 0,
                inventory = inputManifest?.InventoryFile ?? "input-files.txt",
                inventorySha256 = inputManifest?.InventorySha256,
                manifest = inputManifest?.ManifestFile,
                manifestSha256 = inputManifest?.ManifestSha256,
                contentHashAlgorithm = inputManifest?.ContentHashAlgorithm,
            },
            bundleIntegrity,
            contentRouting = routing is null
                ? null
                : new
                {
                    manifest = routing.Value.Manifest,
                    manifestSha256 = routing.Value.ManifestSha256,
                    policyVersion = routing.Value.PolicyVersion,
                    inputFiles = routing.Value.InputFiles,
                    flossCandidates = routing.Value.FlossCandidates,
                    ocrCandidates = routing.Value.OcrCandidates,
                    classifierErrors = routing.Value.ClassifierErrors,
                    conflicts = routing.Value.Conflicts,
                },
            engineStatuses = CreateEngineStatusSummary(engineStatuses),
            translationRouting = CreateTranslationRoutingSummary(options.TranslationMode, triage),
            translationWork,
            preservationFallbacks = enrichmentStats?.PreservationFallbackRecords,
            options,
            error,
        };
        await WriteJsonAtomicAsync(path, record, cancellationToken);
    }

    internal static TranslationRoutingSummary? CreateTranslationRoutingSummary(
        TranslationWorkflowMode translationMode,
        LanguageTriageStats? triage
    )
    {
        if (
            translationMode
            is not TranslationWorkflowMode.Auto and not TranslationWorkflowMode.DetectOnly
        )
        {
            return null;
        }
        if (
            triage is { } completedTriage
            && (
                completedTriage.TranslationRoutingRetained < 0
                || completedTriage.TranslationRoutingProspectiveBypasses < 0
                || completedTriage.TranslationRoutingUnknown < 0
                || completedTriage.InputRecords < 0
                || completedTriage.TranslationRoutingRetained > completedTriage.InputRecords
                || completedTriage.TranslationRoutingProspectiveBypasses
                    > completedTriage.InputRecords
                        - completedTriage.TranslationRoutingRetained
                || completedTriage.TranslationRoutingUnknown
                    != completedTriage.InputRecords
                        - completedTriage.TranslationRoutingRetained
                        - completedTriage.TranslationRoutingProspectiveBypasses
            )
        )
        {
            throw new InvalidDataException(
                "Translation-routing aggregates do not match the language-triage input cardinality."
            );
        }
        if (
            triage is { } measuredTriage
            && (
                measuredTriage.TranslationRoutingEvaluations < 0
                || measuredTriage.TranslationRoutingEvaluations > measuredTriage.InputRecords
                || (
                    measuredTriage.InputRecords > 0
                    && measuredTriage.TranslationRoutingEvaluations == 0
                )
                || measuredTriage.DetectorEligibleRecords < 0
                || measuredTriage.DetectorEligibleRecords > measuredTriage.InputRecords
                || measuredTriage.DetectorExecutions < 0
                || measuredTriage.DetectorReuseHits < 0
                || measuredTriage.DetectorFailures < 0
                || measuredTriage.DetectorExecutions > measuredTriage.DetectorEligibleRecords
                || measuredTriage.DetectorReuseHits
                    != measuredTriage.DetectorEligibleRecords
                        - measuredTriage.DetectorExecutions
                || measuredTriage.DetectorFailures > measuredTriage.DetectorExecutions
                || !string.Equals(
                    measuredTriage.TranslationRoutingPolicyVersion,
                    TranslationWorthinessRouter.PolicyVersion,
                    StringComparison.Ordinal
                )
            )
        )
        {
            throw new InvalidDataException(
                "Translation-routing work counters do not reconcile with language-triage cardinality."
            );
        }
        return new TranslationRoutingSummary(
            Mode: "shadow",
            PolicyVersion: triage?.TranslationRoutingPolicyVersion
                ?? TranslationWorthinessRouter.PolicyVersion,
            CodebookVersion: TranslationWorthinessRouter.CodebookVersion,
            Codebook: TranslationWorthinessRouter.Codebook,
            Assessments: "language-assessments.jsonl",
            AssessmentSchemaVersion: 1,
            Retained: triage?.TranslationRoutingRetained,
            ProspectiveBypasses: triage?.TranslationRoutingProspectiveBypasses,
            Unknown: triage?.TranslationRoutingUnknown,
            RoutingEvaluations: triage?.TranslationRoutingEvaluations,
            DetectorEligibleRecords: triage?.DetectorEligibleRecords,
            DetectorExecutions: triage?.DetectorExecutions,
            DetectorReuseHits: triage?.DetectorReuseHits
        );
    }

    private static object? CreateEngineStatusSummary(EngineStatusStats? stats)
    {
        if (stats is null)
        {
            return null;
        }
        return new
        {
            manifest = stats.Value.Manifest,
            manifestSha256 = stats.Value.ManifestSha256,
            records = stats.Value.Records,
            native = new
            {
                succeeded = stats.Value.Native.Succeeded,
                notApplicable = stats.Value.Native.NotApplicable,
                disabledByUser = stats.Value.Native.DisabledByUser,
                outputRecords = stats.Value.Native.OutputRecords,
            },
            floss = new
            {
                succeeded = stats.Value.Floss.Succeeded,
                notApplicable = stats.Value.Floss.NotApplicable,
                disabledByUser = stats.Value.Floss.DisabledByUser,
                outputRecords = stats.Value.Floss.OutputRecords,
            },
            ocr = new
            {
                succeeded = stats.Value.Ocr.Succeeded,
                notApplicable = stats.Value.Ocr.NotApplicable,
                disabledByUser = stats.Value.Ocr.DisabledByUser,
                outputRecords = stats.Value.Ocr.OutputRecords,
            },
        };
    }

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken
    )
    {
        var temporaryPath = path + ".partial." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(value, JsonOptions) + Environment.NewLine,
                new UTF8Encoding(false),
                cancellationToken
            );
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = string.Empty;
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                DeleteTemporaryFile(temporaryPath);
            }
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void EnsureIncompleteMarker(
        string path,
        DateTimeOffset started,
        string error
    )
    {
        try
        {
            File.WriteAllText(
                path,
                $"bstrings analysis is incomplete; processing started {started:O}. "
                    + $"Failure: {error}{Environment.NewLine}",
                new UTF8Encoding(false)
            );
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
