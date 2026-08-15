#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
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

namespace bstrings;

internal enum AnalysisResumeAttemptMode
{
    New,
    Resume,
    LegacyImport,
}

internal readonly record struct AnalysisResumeStage(int Ordinal, string Id)
{
    internal static readonly AnalysisResumeStage InputInventory = new(1, "input-inventory");
    internal static readonly AnalysisResumeStage ContentRouting = new(2, "content-routing");
    internal static readonly AnalysisResumeStage Native = new(3, "native-extraction");
    internal static readonly AnalysisResumeStage Floss = new(4, "floss-recovery");
    internal static readonly AnalysisResumeStage Ocr = new(5, "ocr");
    internal static readonly AnalysisResumeStage RawMerge = new(6, "raw-merge");
    internal static readonly AnalysisResumeStage TranslationSelection = new(7, "translation-selection");
    internal static readonly AnalysisResumeStage Translation = new(8, "translation");
    internal static readonly AnalysisResumeStage Decoder = new(9, "decoder");
    internal static readonly AnalysisResumeStage EnrichedMerge = new(10, "enriched-merge");
    internal static readonly AnalysisResumeStage PatternMatching = new(11, "pattern-matching");
    internal static readonly AnalysisResumeStage Reports = new(12, "reports");
    internal static readonly AnalysisResumeStage EngineLedger = new(13, "engine-ledger");

    internal static readonly IReadOnlyList<AnalysisResumeStage> All =
    [
        InputInventory,
        ContentRouting,
        Native,
        Floss,
        Ocr,
        RawMerge,
        TranslationSelection,
        Translation,
        Decoder,
        EnrichedMerge,
        PatternMatching,
        Reports,
        EngineLedger,
    ];

    internal static AnalysisResumeStage FromOrdinalAndId(int ordinal, string id)
    {
        foreach (var stage in All)
        {
            if (stage.Ordinal == ordinal && string.Equals(stage.Id, id, StringComparison.Ordinal))
            {
                return stage;
            }
        }
        throw new InvalidDataException($"Unsupported analysis checkpoint stage {ordinal:N0} '{id}'.");
    }
}

internal sealed record AnalysisResumeOptionsSnapshot(
    string? FilePath,
    string? DirectoryPath,
    string? Mask,
    bool Full,
    OcrWorkflowMode OcrMode,
    OcrProvider OcrProvider,
    int OcrThreads,
    ExecutableRecoveryMode RecoveryMode,
    TranslationWorkflowMode TranslationMode,
    LanguageDetectionMode LanguageDetectionMode,
    LanguageTriagePolicy TranslationPolicy,
    double LanguageConfidence,
    double LanguageMargin,
    string TranslationTarget,
    string TranslationDevice,
    int TranslationParallelism,
    int TranslationThreads,
    int TranslationGpuLayers,
    bool TranslationStrictDeterminism,
    string PatternSelection,
    string? RegexFilePath,
    string Processor,
    string CpuEngine,
    int MinimumStringLength,
    int MaximumStringLength,
    int TranslationMinimumCharacters,
    int TranslationMaximumCharacters,
    bool Airgap,
    NativeExtractionMode NativeExtractionMode,
    DecoderWorkflowMode DecoderMode,
    int DecoderMaximumCandidateCharacters,
    int DecoderMaximumBytesPerRecord,
    long DecoderMaximumCandidates,
    long DecoderMaximumTotalBytes
)
{
    internal static AnalysisResumeOptionsSnapshot FromOptions(AnalysisOptions options) =>
        new(
            options.FilePath,
            options.DirectoryPath,
            options.Mask,
            options.Full,
            options.OcrMode,
            options.OcrProvider,
            options.OcrThreads,
            options.RecoveryMode,
            options.TranslationMode,
            options.LanguageDetectionMode,
            options.TranslationPolicy,
            options.LanguageConfidence,
            options.LanguageMargin,
            options.TranslationTarget,
            options.TranslationDevice,
            options.TranslationParallelism,
            options.TranslationThreads,
            options.TranslationGpuLayers,
            options.TranslationStrictDeterminism,
            options.PatternSelection,
            options.RegexFilePath,
            options.Processor,
            options.CpuEngine,
            options.MinimumStringLength,
            options.MaximumStringLength,
            options.TranslationMinimumCharacters,
            options.TranslationMaximumCharacters,
            options.Airgap,
            options.NativeExtractionMode,
            options.DecoderMode,
            options.DecoderMaximumCandidateCharacters,
            options.DecoderMaximumBytesPerRecord,
            options.DecoderMaximumCandidates,
            options.DecoderMaximumTotalBytes
        );

    internal AnalysisOptions ToOptions(string outputDirectory, string? bundleRoot) =>
        new(
            FilePath,
            DirectoryPath,
            Mask,
            outputDirectory,
            Full,
            OcrMode,
            OcrProvider,
            OcrThreads,
            RecoveryMode,
            TranslationMode,
            LanguageDetectionMode,
            TranslationPolicy,
            LanguageConfidence,
            LanguageMargin,
            TranslationTarget,
            TranslationDevice,
            TranslationParallelism,
            TranslationThreads,
            TranslationGpuLayers,
            TranslationStrictDeterminism,
            PatternSelection,
            RegexFilePath,
            Processor,
            CpuEngine,
            MinimumStringLength,
            MaximumStringLength,
            TranslationMinimumCharacters,
            TranslationMaximumCharacters,
            bundleRoot,
            Airgap,
            NativeExtractionMode,
            DecoderMode,
            DecoderMaximumCandidateCharacters,
            DecoderMaximumBytesPerRecord,
            DecoderMaximumCandidates,
            DecoderMaximumTotalBytes
        );
}

internal sealed record AnalysisResumeSpecification(
    int SchemaVersion,
    int PipelineContractVersion,
    string BstringsVersion,
    string ExecutableSha256,
    string ManagedAssemblySha256,
    string OutputDirectory,
    BundleIntegrity? BundleIntegrity,
    AnalysisResumeOptionsSnapshot Options,
    int PatternCount,
    string PatternSha256
)
{
    internal static AnalysisResumeSpecification Create(
        AnalysisOptions options,
        IReadOnlyList<(string name, string pattern)> patterns,
        BundleIntegrity? bundleIntegrity,
        string? executingExecutablePath = null
    )
    {
        var executableSha256 = bundleIntegrity?.ExecutableSha256
            ?? AnalysisResumeCore.HashExecutingExecutable(executingExecutablePath);
        return new AnalysisResumeSpecification(
            1,
            1,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown",
            executableSha256,
            AnalysisResumeCore.HashManagedAssembly(executableSha256),
            AnalysisResumeCore.CanonicalizeOutputDirectory(options.OutputDirectory),
            bundleIntegrity,
            AnalysisResumeOptionsSnapshot.FromOptions(options),
            patterns.Count,
            AnalysisResumeCore.HashPatterns(patterns)
        );
    }
}

internal sealed record AnalysisResumeImportedCheckpoint(
    AnalysisResumeStage Stage,
    IReadOnlyList<string> ArtifactRelativePaths,
    JsonElement Stats,
    double ElapsedSeconds
)
{
    internal static AnalysisResumeImportedCheckpoint Create<TStats>(
        AnalysisResumeStage stage,
        IReadOnlyList<string> artifactRelativePaths,
        TStats stats,
        double elapsedSeconds = 0
    ) =>
        new(
            stage,
            artifactRelativePaths,
            JsonSerializer.SerializeToElement(stats, AnalysisResumeCore.JsonOptions),
            elapsedSeconds
        );
}

internal sealed record AnalysisResumePublicEvidence(
    string State,
    string RunId,
    string AttemptId,
    string AttemptMode,
    string? LastCommittedStage,
    IReadOnlyList<string> ReusedStages,
    bool LegacyImported,
    AnalysisResumeTransitionEvidence? Transition = null
);

internal static class AnalysisResumeCore
{
    internal const string ResumeDirectoryName = ".bstrings-resume";
    internal const string LockFileName = ".bstrings-run.lock";
    internal const string OwnerFileName = "owner.json";
    internal const string CheckpointsDirectoryName = "checkpoints";
    internal const string AttemptsDirectoryName = "attempts";
    internal const string PendingDirectoryName = "pending";
    internal const int MaximumMetadataBytes = 4 * 1024 * 1024;
    private const int MaximumAttemptFiles = 20_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    internal sealed record OwnerRecord(
        int SchemaVersion,
        string RecordType,
        string RunId,
        DateTimeOffset CreatedUtc,
        AnalysisResumeSpecification Specification,
        bool LegacyImported
    );

    internal sealed record ArtifactRecord(string Path, long Bytes, string Sha256);

    internal sealed record CheckpointRecord(
        int SchemaVersion,
        string RecordType,
        string RunId,
        int Ordinal,
        string StageId,
        string DependencySha256,
        DateTimeOffset CommittedUtc,
        double ElapsedSeconds,
        IReadOnlyList<ArtifactRecord> Artifacts,
        JsonElement Stats
    );

    internal sealed record AttemptRecord(
        int SchemaVersion,
        string RecordType,
        string RunId,
        string AttemptId,
        string Mode,
        DateTimeOffset StartedUtc,
        DateTimeOffset? CompletedUtc,
        string Status,
        string? LastCommittedStage,
        string? FailureCategory,
        string? Provenance
    );

    internal static bool HasResumeMetadata(string outputDirectory) =>
        Directory.Exists(
            Path.Combine(CanonicalizeOutputDirectory(outputDirectory), ResumeDirectoryName)
        );

    internal static AnalysisResumeSpecification ReadSpecification(string outputDirectory)
    {
        var outputFullPath = CanonicalizeOutputDirectory(outputDirectory);
        if (AnalysisResumeTranslationOffCore.HasTransitionState(outputFullPath))
        {
            return AnalysisResumeTranslationOffCore.ReadEffectiveSpecification(outputFullPath);
        }
        var owner = ReadOwner(Path.Combine(outputFullPath, ResumeDirectoryName, OwnerFileName));
        return owner.Specification;
    }

    internal static async Task<AnalysisResumeSession> InitializeNewAsync(
        string outputDirectory,
        AnalysisResumeSpecification specification,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSpecification(specification);
        var outputFullPath = CanonicalizeOutputDirectory(outputDirectory);
        RequireSpecificationOutputDirectory(specification, outputFullPath);
        var createdDirectory = !Directory.Exists(outputFullPath);
        if (createdDirectory)
        {
            Directory.CreateDirectory(outputFullPath);
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(outputFullPath, "analysis results path");
        RefuseNetworkFilesystem(outputFullPath);
        var initialEntries = Directory.EnumerateFileSystemEntries(outputFullPath).ToArray();
        if (
            initialEntries.Any(path =>
                !string.Equals(
                    Path.GetFileName(path),
                    LockFileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        )
        {
            if (createdDirectory)
            {
                TryDeleteEmptyDirectory(outputFullPath);
            }
            if (
                HasResumeMetadata(outputFullPath)
                && File.Exists(Path.Combine(outputFullPath, ".incomplete"))
            )
            {
                throw new IOException(
                    $"Results contain an incomplete resumable analysis. Run 'bstrings.exe analyze -r -o \"{outputFullPath}\"'."
                );
            }
            throw new IOException(
                $"Results directory must be new or empty for a new analysis: '{outputFullPath}'."
            );
        }

        FileStream? lease = null;
        var metadataPublished = false;
        try
        {
            lease = AcquireLease(outputFullPath, createIfMissing: true);
            var leasedEntries = Directory.EnumerateFileSystemEntries(outputFullPath).ToArray();
            if (
                leasedEntries.Length != 1
                || !string.Equals(
                    Path.GetFileName(leasedEntries[0]),
                    LockFileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                throw new IOException(
                    $"Results directory changed while it was reserved: '{outputFullPath}'."
                );
            }
            var runId = Guid.NewGuid().ToString("N");
            var owner = new OwnerRecord(
                1,
                "analysis-resume-owner",
                runId,
                DateTimeOffset.UtcNow,
                specification,
                LegacyImported: false
            );
            await PublishNewMetadataDirectoryAsync(
                outputFullPath,
                owner,
                Array.Empty<AnalysisResumeImportedCheckpoint>(),
                cancellationToken
            );
            metadataPublished = true;
            return await CreateSessionAsync(
                outputFullPath,
                owner,
                lease,
                AnalysisResumeAttemptMode.New,
                Array.Empty<(CheckpointRecord Record, string Sha256)>(),
                new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase),
                cancellationToken
            );
        }
        catch
        {
            lease?.Dispose();
            if (!metadataPublished)
            {
                TryDeleteFile(Path.Combine(outputFullPath, LockFileName));
                if (createdDirectory)
                {
                    TryDeleteEmptyDirectory(outputFullPath);
                }
            }
            throw;
        }
    }

    internal static async Task<AnalysisResumeSession> OpenExistingAsync(
        string outputDirectory,
        AnalysisResumeSpecification expectedSpecification,
        Action<long, long>? verificationProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        ValidateSpecification(expectedSpecification);
        var outputFullPath = CanonicalizeOutputDirectory(outputDirectory);
        RequireSpecificationOutputDirectory(expectedSpecification, outputFullPath);
        ValidateOwnedIncompleteDirectory(outputFullPath);
        if (AnalysisResumeTranslationOffCore.HasTransitionState(outputFullPath))
        {
            return await AnalysisResumeTranslationOffCore.OpenExistingAsync(
                outputFullPath,
                expectedSpecification,
                verificationProgress,
                cancellationToken
            );
        }
        var ownerPath = Path.Combine(outputFullPath, ResumeDirectoryName, OwnerFileName);
        var ownerBeforeLease = ReadOwner(ownerPath);
        RequireMatchingSpecification(ownerBeforeLease.Specification, expectedSpecification);

        var lease = AcquireLease(outputFullPath, createIfMissing: false);
        var artifactLeases = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            ValidateMetadataLayout(outputFullPath);
            RemoveInterruptedPublications(outputFullPath);
            var owner = ReadOwner(ownerPath);
            if (owner != ownerBeforeLease)
            {
                throw new InvalidDataException("Analysis resume ownership changed while the lease was acquired.");
            }
            ValidateAttemptRecords(outputFullPath, owner);
            var checkpoints = await ReadAndVerifyCheckpointsAsync(
                outputFullPath,
                owner,
                verificationProgress,
                cancellationToken,
                artifactLeases
            );
            return await CreateSessionAsync(
                outputFullPath,
                owner,
                lease,
                AnalysisResumeAttemptMode.Resume,
                checkpoints,
                artifactLeases,
                cancellationToken
            );
        }
        catch
        {
            lease.Dispose();
            DisposeArtifactLeases(artifactLeases);
            throw;
        }
    }

    internal static async Task<AnalysisResumeSession> ImportExistingAsync(
        string outputDirectory,
        AnalysisResumeSpecification specification,
        IReadOnlyList<AnalysisResumeImportedCheckpoint> orderedPrevalidatedStages,
        Action<long, long>? verificationProgress = null,
        CancellationToken cancellationToken = default
    ) =>
        await ImportExistingAsync(
            outputDirectory,
            specification,
            _ => Task.FromResult(orderedPrevalidatedStages),
            verificationProgress,
            cancellationToken
        );

    internal static async Task<AnalysisResumeSession> ImportExistingAsync(
        string outputDirectory,
        AnalysisResumeSpecification specification,
        Func<CancellationToken, Task<IReadOnlyList<AnalysisResumeImportedCheckpoint>>> validateAndBuild,
        Action<long, long>? verificationProgress = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(validateAndBuild);
        ValidateSpecification(specification);
        var outputFullPath = CanonicalizeOutputDirectory(outputDirectory);
        RequireSpecificationOutputDirectory(specification, outputFullPath);
        AnalysisOrchestrator.EnsureNoReparsePoints(outputFullPath, "legacy analysis results path");
        RefuseNetworkFilesystem(outputFullPath);
        if (!Directory.Exists(outputFullPath))
        {
            throw new DirectoryNotFoundException($"Legacy analysis results were not found: '{outputFullPath}'.");
        }
        if (HasResumeMetadata(outputFullPath))
        {
            throw new InvalidDataException("Legacy import cannot replace existing resume metadata.");
        }

        var lease = AcquireLease(outputFullPath, createIfMissing: true);
        var artifactLeases = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        var metadataPublished = false;
        try
        {
            var orderedPrevalidatedStages = await validateAndBuild(cancellationToken);
            var owner = new OwnerRecord(
                1,
                "analysis-resume-owner",
                Guid.NewGuid().ToString("N"),
                DateTimeOffset.UtcNow,
                specification,
                LegacyImported: true
            );
            await PublishNewMetadataDirectoryAsync(
                outputFullPath,
                owner,
                orderedPrevalidatedStages,
                cancellationToken,
                verificationProgress,
                artifactLeases
            );
            metadataPublished = true;
            var checkpoints = await ReadAndVerifyCheckpointsAsync(
                outputFullPath,
                owner,
                verificationProgress,
                cancellationToken,
                artifactLeases,
                verifyArtifacts: false
            );
            return await CreateSessionAsync(
                outputFullPath,
                owner,
                lease,
                AnalysisResumeAttemptMode.LegacyImport,
                checkpoints,
                artifactLeases,
                cancellationToken
            );
        }
        catch
        {
            lease.Dispose();
            DisposeArtifactLeases(artifactLeases);
            if (!metadataPublished)
            {
                TryDeleteFile(Path.Combine(outputFullPath, LockFileName));
            }
            throw;
        }
    }

    internal static string HashPatterns(IReadOnlyList<(string name, string pattern)> patterns)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var (name, pattern) in patterns)
        {
            AppendUtf8(hash, name, length);
            AppendUtf8(hash, pattern, length);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static string CanonicalizeOutputDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static string HashExecutingExecutable(string? path = null)
    {
        path ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            var executableName = OperatingSystem.IsWindows() ? "bstrings.exe" : "bstrings";
            path = Path.Combine(AppContext.BaseDirectory, executableName);
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new InvalidOperationException("The executing bstrings binary cannot be hashed for resume identity.");
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static string HashManagedAssembly(string? singleFileExecutableSha256 = null)
    {
        var assemblyName = Assembly.GetExecutingAssembly().GetName().Name;
        var path = string.IsNullOrWhiteSpace(assemblyName)
            ? null
            : Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
        if (path is not null && File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        if (singleFileExecutableSha256 is not null && IsLowerSha256(singleFileExecutableSha256))
        {
            return singleFileExecutableSha256;
        }
        throw new InvalidOperationException(
            "The managed bstrings assembly cannot be hashed for resume identity."
        );
    }

    private static async Task<AnalysisResumeSession> CreateSessionAsync(
        string outputFullPath,
        OwnerRecord owner,
        FileStream lease,
        AnalysisResumeAttemptMode mode,
        IReadOnlyList<(CheckpointRecord Record, string Sha256)> checkpoints,
        Dictionary<string, FileStream> artifactLeases,
        CancellationToken cancellationToken
    )
    {
        ValidateMetadataLayout(outputFullPath);
        ValidateAttemptRecords(outputFullPath, owner);
        var attemptId = Guid.NewGuid().ToString("N");
        var session = new AnalysisResumeSession(
            outputFullPath,
            owner.RunId,
            attemptId,
            owner.Specification,
            owner.LegacyImported,
            mode,
            lease,
            checkpoints,
            artifactLeases
        );
        await session.WriteAttemptStartAsync(cancellationToken);
        return session;
    }

    private static async Task PublishNewMetadataDirectoryAsync(
        string outputFullPath,
        OwnerRecord owner,
        IReadOnlyList<AnalysisResumeImportedCheckpoint> imported,
        CancellationToken cancellationToken,
        Action<long, long>? verificationProgress = null,
        Dictionary<string, FileStream>? retainedArtifactLeases = null
    )
    {
        var finalDirectory = Path.Combine(outputFullPath, ResumeDirectoryName);
        var outputParent = Path.GetDirectoryName(outputFullPath)
            ?? throw new InvalidOperationException("Analysis results have no physical parent directory.");
        var temporaryDirectory = Path.Combine(
            outputParent,
            ".bstrings-resume-init." + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(temporaryDirectory);
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, CheckpointsDirectoryName));
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, AttemptsDirectoryName));
        Directory.CreateDirectory(Path.Combine(temporaryDirectory, PendingDirectoryName));
        try
        {
            var ownerPath = Path.Combine(temporaryDirectory, OwnerFileName);
            await WriteNewJsonAsync(ownerPath, owner, cancellationToken);
            var dependencySha256 = await HashFileAsync(ownerPath, null, cancellationToken);
            var expectedOrdinal = 1;
            foreach (var importedCheckpoint in imported)
            {
                if (importedCheckpoint.Stage.Ordinal != expectedOrdinal)
                {
                    throw new InvalidDataException("Imported checkpoints must form one contiguous stage prefix.");
                }
                var artifacts = await CreateArtifactRecordsAsync(
                    outputFullPath,
                    importedCheckpoint.ArtifactRelativePaths,
                    verificationProgress,
                    cancellationToken,
                    retainedArtifactLeases
                );
                var checkpoint = new CheckpointRecord(
                    1,
                    "analysis-stage-checkpoint",
                    owner.RunId,
                    importedCheckpoint.Stage.Ordinal,
                    importedCheckpoint.Stage.Id,
                    dependencySha256,
                    DateTimeOffset.UtcNow,
                    importedCheckpoint.ElapsedSeconds,
                    artifacts,
                    importedCheckpoint.Stats.Clone()
                );
                var checkpointPath = Path.Combine(
                    temporaryDirectory,
                    CheckpointsDirectoryName,
                    CheckpointFileName(importedCheckpoint.Stage)
                );
                await WriteNewJsonAsync(checkpointPath, checkpoint, cancellationToken);
                dependencySha256 = await HashFileAsync(checkpointPath, null, cancellationToken);
                expectedOrdinal++;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(temporaryDirectory, finalDirectory);
            temporaryDirectory = string.Empty;
        }
        finally
        {
            if (temporaryDirectory.Length > 0)
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }
    }

    internal static void ValidateMetadataLayout(string outputFullPath)
    {
        var resumeDirectory = Path.Combine(outputFullPath, ResumeDirectoryName);
        AnalysisOrchestrator.EnsureNoReparsePoints(
            resumeDirectory,
            "analysis resume metadata directory"
        );
        var expected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [OwnerFileName] = false,
            [CheckpointsDirectoryName] = true,
            [AttemptsDirectoryName] = true,
            [PendingDirectoryName] = true,
        };
        var entries = Directory
            .EnumerateFileSystemEntries(resumeDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (entries.Length != expected.Count)
        {
            throw new InvalidDataException(
                "Analysis resume metadata contains an unexpected or missing entry."
            );
        }
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name is null || !expected.TryGetValue(name, out var mustBeDirectory))
            {
                throw new InvalidDataException(
                    "Analysis resume metadata contains an unexpected entry."
                );
            }
            AnalysisOrchestrator.EnsureNoReparsePoints(
                entry,
                "analysis resume metadata entry"
            );
            if (mustBeDirectory ? !Directory.Exists(entry) : !File.Exists(entry))
            {
                throw new InvalidDataException(
                    $"Analysis resume metadata entry '{name}' has the wrong file type."
                );
            }
        }
    }

    internal static void RemoveInterruptedPublications(string outputFullPath)
    {
        var pendingDirectory = Path.Combine(
            outputFullPath,
            ResumeDirectoryName,
            PendingDirectoryName
        );
        var entries = Directory
            .EnumerateFileSystemEntries(pendingDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToList();
        foreach (var entry in entries)
        {
            if (
                !File.Exists(entry)
                || !IsPendingPublicationName(Path.GetFileName(entry))
            )
            {
                throw new InvalidDataException(
                    "Analysis resume pending storage contains an unexpected entry."
                );
            }
            AnalysisOrchestrator.EnsureNoReparsePoints(
                entry,
                "analysis resume pending publication"
            );
            using var stream = new FileStream(
                entry,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            RejectHardLinkedFile(stream, "analysis resume pending publication");
        }
        foreach (
            var entry in Directory.EnumerateFileSystemEntries(
                Path.Combine(outputFullPath, ResumeDirectoryName, CheckpointsDirectoryName),
                "*",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            var name = Path.GetFileName(entry);
            if (!File.Exists(entry))
            {
                throw new InvalidDataException(
                    "Analysis checkpoint storage contains an unexpected directory."
                );
            }
            if (IsLegacyCheckpointPartialName(name))
            {
                entries.Add(entry);
            }
            else if (!IsCanonicalCheckpointFileName(name))
            {
                throw new InvalidDataException(
                    "Analysis checkpoint storage contains an unexpected file."
                );
            }
        }
        foreach (
            var entry in Directory.EnumerateFileSystemEntries(
                Path.Combine(outputFullPath, ResumeDirectoryName, AttemptsDirectoryName),
                "*",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            var name = Path.GetFileName(entry);
            if (!File.Exists(entry))
            {
                throw new InvalidDataException(
                    "Analysis resume attempt history contains an unexpected directory."
                );
            }
            if (IsLegacyAttemptPartialName(name))
            {
                entries.Add(entry);
            }
            else if (!TryParseAttemptFileName(name, out _, out _))
            {
                throw new InvalidDataException(
                    "Analysis resume attempt history contains an unexpected file."
                );
            }
        }
        foreach (var entry in entries.Where(entry =>
            !string.Equals(
                Path.GetDirectoryName(entry),
                pendingDirectory,
                StringComparison.OrdinalIgnoreCase
            )
        ))
        {
            AnalysisOrchestrator.EnsureNoReparsePoints(
                entry,
                "analysis resume interrupted publication"
            );
            using var stream = new FileStream(
                entry,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            RejectHardLinkedFile(stream, "analysis resume interrupted publication");
        }
        foreach (var entry in entries)
        {
            File.Delete(entry);
        }
    }

    private static bool IsPendingPublicationName(string? name)
    {
        if (
            string.Equals(
                name,
                AnalysisResumeTranslationOffCore.JournalPendingFileName,
                StringComparison.Ordinal
            )
        )
        {
            return true;
        }
        if (name is null || !name.EndsWith(".json", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var prefix in new[] { "checkpoint-", "attempt-" })
        {
            if (
                name.StartsWith(prefix, StringComparison.Ordinal)
                && TryParseCanonicalGuid(
                    name.AsSpan(prefix.Length, name.Length - prefix.Length - ".json".Length),
                    out _
                )
            )
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsLegacyCheckpointPartialName(string? name)
    {
        if (name is null)
        {
            return false;
        }
        foreach (var stage in AnalysisResumeStage.All)
        {
            var prefix = CheckpointFileName(stage) + ".partial.";
            if (
                name.StartsWith(prefix, StringComparison.Ordinal)
                && TryParseCanonicalGuid(name.AsSpan(prefix.Length), out _)
            )
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsCanonicalCheckpointFileName(string? name) =>
        name is not null
        && AnalysisResumeStage.All.Any(stage =>
            string.Equals(name, CheckpointFileName(stage), StringComparison.Ordinal)
        );

    private static bool IsLegacyAttemptPartialName(string? name)
    {
        if (name is null)
        {
            return false;
        }
        const string marker = ".json.partial.";
        var markerIndex = name.IndexOf(marker, StringComparison.Ordinal);
        if (
            markerIndex < 0
            || !TryParseCanonicalGuid(
                name.AsSpan(markerIndex + marker.Length),
                out _
            )
        )
        {
            return false;
        }
        return TryParseAttemptFileName(
            name[..(markerIndex + ".json".Length)],
            out _,
            out _
        );
    }

    internal static void ValidateAttemptRecords(string outputFullPath, OwnerRecord owner)
    {
        var attemptsDirectory = Path.Combine(
            outputFullPath,
            ResumeDirectoryName,
            AttemptsDirectoryName
        );
        var entries = Directory
            .EnumerateFileSystemEntries(attemptsDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length > MaximumAttemptFiles)
        {
            throw new InvalidDataException(
                "Analysis resume attempt history exceeds its bounded file count."
            );
        }
        var records = new Dictionary<
            string,
            (AttemptRecord? Start, AttemptRecord? Outcome)
        >(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!File.Exists(entry))
            {
                throw new InvalidDataException(
                    "Analysis resume attempt history contains an unexpected directory."
                );
            }
            var fileName = Path.GetFileName(entry);
            if (!TryParseAttemptFileName(fileName, out var attemptId, out var isStart))
            {
                throw new InvalidDataException(
                    "Analysis resume attempt history contains an unexpected file."
                );
            }
            var record = ReadStrictJson<AttemptRecord>(entry);
            ValidateAttemptRecord(record, owner, attemptId, isStart);
            records.TryGetValue(attemptId, out var pair);
            if (isStart)
            {
                if (pair.Start is not null)
                {
                    throw new InvalidDataException(
                        "Analysis resume attempt history repeats a start record."
                    );
                }
                pair.Start = record;
            }
            else
            {
                if (pair.Outcome is not null)
                {
                    throw new InvalidDataException(
                        "Analysis resume attempt history repeats an outcome record."
                    );
                }
                pair.Outcome = record;
            }
            records[attemptId] = pair;
        }
        foreach (var (attemptId, pair) in records)
        {
            if (pair.Start is null)
            {
                throw new InvalidDataException(
                    $"Analysis resume attempt '{attemptId}' has no start record."
                );
            }
            if (
                pair.Outcome is { } outcome
                && (
                    outcome.StartedUtc != pair.Start.StartedUtc
                    || !string.Equals(outcome.Mode, pair.Start.Mode, StringComparison.Ordinal)
                    || StageOrdinal(outcome.LastCommittedStage)
                        < StageOrdinal(pair.Start.LastCommittedStage)
                )
            )
            {
                throw new InvalidDataException(
                    $"Analysis resume attempt '{attemptId}' has inconsistent start and outcome records."
                );
            }
        }
    }

    internal static bool TryParseAttemptFileName(
        string? fileName,
        out string attemptId,
        out bool isStart
    )
    {
        attemptId = string.Empty;
        isStart = false;
        if (fileName is null)
        {
            return false;
        }
        string suffix;
        if (fileName.EndsWith("-start.json", StringComparison.Ordinal))
        {
            suffix = "-start.json";
            isStart = true;
        }
        else if (fileName.EndsWith("-outcome.json", StringComparison.Ordinal))
        {
            suffix = "-outcome.json";
        }
        else
        {
            return false;
        }
        var candidate = fileName[..^suffix.Length];
        if (!TryParseCanonicalGuid(candidate.AsSpan(), out _))
        {
            return false;
        }
        attemptId = candidate;
        return true;
    }

    private static bool TryParseCanonicalGuid(ReadOnlySpan<char> value, out Guid guid)
    {
        if (!Guid.TryParseExact(value, "N", out guid))
        {
            return false;
        }
        return value.SequenceEqual(guid.ToString("N").AsSpan());
    }

    internal static void ValidateAttemptRecord(
        AttemptRecord record,
        OwnerRecord owner,
        string attemptId,
        bool isStart
    )
    {
        var validMode = record.Mode is "new" or "resume" or "legacy-import" or "legacyimport";
        var validOwnerMode = owner.LegacyImported
            ? record.Mode is "legacy-import" or "legacyimport" or "resume"
            : record.Mode is "new" or "resume";
        var validLastStage = record.LastCommittedStage is null
            || AnalysisResumeStage.All.Any(stage =>
                string.Equals(stage.Id, record.LastCommittedStage, StringComparison.Ordinal)
            );
        if (
            record.SchemaVersion != 1
            || record.RecordType != "analysis-resume-attempt"
            || !string.Equals(record.RunId, owner.RunId, StringComparison.Ordinal)
            || !string.Equals(record.AttemptId, attemptId, StringComparison.Ordinal)
            || !validMode
            || !validOwnerMode
            || record.StartedUtc == default
            || !validLastStage
            || !IsTokenOrNull(record.FailureCategory)
            || !IsTokenOrNull(record.Provenance)
        )
        {
            throw new InvalidDataException(
                $"Analysis resume attempt '{attemptId}' is invalid."
            );
        }
        if (
            isStart
                ? record.Status != "running"
                    || record.CompletedUtc is not null
                    || record.FailureCategory is not null
                : record.Status is not ("complete" or "failed" or "cancelled" or "abandoned")
                    || record.CompletedUtc is null
                    || record.CompletedUtc < record.StartedUtc
                    || record.Status == "complete"
                        && (
                            record.FailureCategory is not null
                            || record.LastCommittedStage != AnalysisResumeStage.EngineLedger.Id
                        )
                    || record.Status != "complete" && record.FailureCategory is null
        )
        {
            throw new InvalidDataException(
                $"Analysis resume attempt '{attemptId}' has an invalid lifecycle state."
            );
        }
    }

    internal static int StageOrdinal(string? stageId) =>
        stageId is null
            ? 0
            : AnalysisResumeStage.All.Single(stage =>
                string.Equals(stage.Id, stageId, StringComparison.Ordinal)
            ).Ordinal;

    private static bool IsTokenOrNull(string? value) =>
        value is null
        || (
            value.Length is > 0 and <= 64
            && value.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
            )
        );

    internal static async Task<IReadOnlyList<(CheckpointRecord Record, string Sha256)>> ReadAndVerifyCheckpointsAsync(
        string outputFullPath,
        OwnerRecord owner,
        Action<long, long>? verificationProgress,
        CancellationToken cancellationToken,
        Dictionary<string, FileStream>? retainedArtifactLeases = null,
        bool verifyArtifacts = true
    )
    {
        var checkpointDirectory = Path.Combine(
            outputFullPath,
            ResumeDirectoryName,
            CheckpointsDirectoryName
        );
        var files = Directory
            .EnumerateFiles(checkpointDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var allEntries = Directory
            .EnumerateFileSystemEntries(checkpointDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (allEntries.Length != files.Length || files.Length > AnalysisResumeStage.All.Count)
        {
            throw new InvalidDataException(
                "Analysis checkpoint storage contains an unexpected file, directory, or stage."
            );
        }
        var results = new List<(CheckpointRecord Record, string Sha256)>(files.Length);
        var dependencySha256 = await HashFileAsync(
            Path.Combine(outputFullPath, ResumeDirectoryName, OwnerFileName),
            null,
            cancellationToken
        );
        for (var index = 0; index < files.Length; index++)
        {
            var expectedStage = AnalysisResumeStage.All[index];
            if (!string.Equals(Path.GetFileName(files[index]), CheckpointFileName(expectedStage), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Analysis checkpoint files do not form a contiguous canonical prefix.");
            }
            var record = ReadStrictJson<CheckpointRecord>(files[index]);
            ValidateCheckpoint(record, owner, expectedStage, dependencySha256);
            var checkpointSha256 = await HashFileAsync(files[index], null, cancellationToken);
            results.Add((record, checkpointSha256));
            dependencySha256 = checkpointSha256;
        }
        if (!verifyArtifacts)
        {
            if (retainedArtifactLeases is null)
            {
                throw new InvalidOperationException(
                    "Final checkpoint validation requires retained artifact leases."
                );
            }
            foreach (var artifact in results.SelectMany(item => item.Record.Artifacts))
            {
                if (
                    !retainedArtifactLeases.TryGetValue(artifact.Path, out var stream)
                    || stream.SafeFileHandle.IsClosed
                    || stream.Length != artifact.Bytes
                )
                {
                    throw new InvalidDataException(
                        $"Committed artifact '{artifact.Path}' lost its finalization lease."
                    );
                }
                RejectHardLinkedFile(stream, "analysis resume artifact");
            }
            return results;
        }

        long totalBytes;
        try
        {
            totalBytes = results
                .SelectMany(item => item.Record.Artifacts)
                .Aggregate(0L, (total, artifact) => checked(total + artifact.Bytes));
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "Committed artifact sizes exceed the resume verification limit.",
                ex
            );
        }
        long verifiedBytes = 0;
        foreach (var result in results)
        {
            foreach (var artifact in result.Record.Artifacts)
            {
                var fullPath = ResolveArtifactPath(outputFullPath, artifact.Path);
                var info = new FileInfo(fullPath);
                if (!info.Exists || info.Length != artifact.Bytes)
                {
                    throw new InvalidDataException($"Committed artifact '{artifact.Path}' changed size or is missing.");
                }
                var progressBase = verifiedBytes;
                var actualSha256 = await HashArtifactWithOptionalLeaseAsync(
                    artifact.Path,
                    fullPath,
                    retainedArtifactLeases,
                    verificationProgress is null
                        ? null
                        : (completed, _) =>
                            verificationProgress(progressBase + completed, totalBytes),
                    cancellationToken
                );
                if (!FixedEquals(actualSha256, artifact.Sha256))
                {
                    throw new InvalidDataException($"Committed artifact '{artifact.Path}' failed SHA-256 validation.");
                }
                verifiedBytes = checked(verifiedBytes + artifact.Bytes);
            }
        }
        return results;
    }

    internal static void ValidateCheckpoint(
        CheckpointRecord record,
        OwnerRecord owner,
        AnalysisResumeStage stage,
        string dependencySha256
    )
    {
        if (
            record.SchemaVersion != 1
            || record.RecordType != "analysis-stage-checkpoint"
            || record.RunId != owner.RunId
            || record.Ordinal != stage.Ordinal
            || record.StageId != stage.Id
            || !FixedEquals(record.DependencySha256, dependencySha256)
            || record.ElapsedSeconds < 0
            || !double.IsFinite(record.ElapsedSeconds)
            || record.Artifacts.Count == 0
        )
        {
            throw new InvalidDataException($"Analysis checkpoint '{stage.Id}' is invalid.");
        }
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in record.Artifacts)
        {
            if (
                !paths.Add(artifact.Path)
                || artifact.Bytes < 0
                || !IsLowerSha256(artifact.Sha256)
            )
            {
                throw new InvalidDataException($"Analysis checkpoint '{stage.Id}' has invalid artifact identity.");
            }
        }
    }

    internal static OwnerRecord ReadOwner(string path)
    {
        var owner = ReadStrictJson<OwnerRecord>(path);
        if (
            owner.SchemaVersion != 1
            || owner.RecordType != "analysis-resume-owner"
            || !Guid.TryParseExact(owner.RunId, "N", out _)
        )
        {
            throw new InvalidDataException("Analysis resume ownership is invalid.");
        }
        ValidateSpecification(owner.Specification);
        return owner;
    }

    internal static void ValidateSpecification(AnalysisResumeSpecification specification)
    {
        if (
            specification.SchemaVersion != 1
            || specification.PipelineContractVersion != 1
            || string.IsNullOrWhiteSpace(specification.BstringsVersion)
            || !IsLowerSha256(specification.ExecutableSha256)
            || !IsLowerSha256(specification.ManagedAssemblySha256)
            || string.IsNullOrWhiteSpace(specification.OutputDirectory)
            || !Path.IsPathFullyQualified(specification.OutputDirectory)
            || specification.PatternCount < 1
            || !IsLowerSha256(specification.PatternSha256)
            || specification.BundleIntegrity is { } bundle
                && (
                    !IsLowerSha256(bundle.ManifestSha256)
                    || !IsLowerSha256(bundle.ExecutableSha256)
                    || !FixedEquals(bundle.ExecutableSha256, specification.ExecutableSha256)
                )
        )
        {
            throw new InvalidDataException("Analysis resume specification is invalid.");
        }
    }

    internal static void RequireMatchingSpecification(
        AnalysisResumeSpecification actual,
        AnalysisResumeSpecification expected
    )
    {
        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(actual, JsonOptions);
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(expected, JsonOptions);
        if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new InvalidDataException(
                "The saved analysis contract does not match the current input, settings, patterns, executable, or kit. Start a new analysis."
            );
        }
    }

    internal static void RequireSpecificationOutputDirectory(
        AnalysisResumeSpecification specification,
        string outputFullPath
    )
    {
        if (
            !string.Equals(
                CanonicalizeOutputDirectory(specification.OutputDirectory),
                outputFullPath,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidDataException(
                "The saved analysis belongs to a different output/result directory."
            );
        }
    }

    internal static void ValidateOwnedIncompleteDirectory(string outputFullPath)
    {
        if (!Directory.Exists(outputFullPath))
        {
            throw new DirectoryNotFoundException($"Analysis results were not found: '{outputFullPath}'.");
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(outputFullPath, "analysis resume path");
        RefuseNetworkFilesystem(outputFullPath);
        var incompleteMarker = Path.Combine(outputFullPath, ".incomplete");
        RejectLeafReparsePointIfPresent(incompleteMarker, "analysis incomplete marker");
        if (Directory.Exists(incompleteMarker))
        {
            throw new InvalidDataException(
                "The analysis incomplete marker has the wrong file type."
            );
        }
        var incompleteMarkerExists = File.Exists(incompleteMarker);
        if (!incompleteMarkerExists)
        {
            var allowedInitializationEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ResumeDirectoryName,
                LockFileName,
            };
            if (
                Directory.EnumerateFileSystemEntries(outputFullPath)
                    .Select(Path.GetFileName)
                    .Any(name => name is null || !allowedInitializationEntries.Contains(name))
            )
            {
                throw new InvalidDataException("Only an incomplete analysis can be resumed.");
            }
            if (File.Exists(Path.Combine(outputFullPath, "summary.json")))
            {
                throw new InvalidDataException("A complete analysis cannot be resumed.");
            }
        }
    }

    internal static FileStream AcquireLease(string outputFullPath, bool createIfMissing)
    {
        var lockPath = Path.Combine(outputFullPath, LockFileName);
        try
        {
            RejectLeafReparsePointIfPresent(lockPath, "analysis resume lease");
            var lease = new FileStream(
                lockPath,
                createIfMissing ? FileMode.OpenOrCreate : FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.WriteThrough
            );
            try
            {
                AnalysisOrchestrator.EnsureNoReparsePoints(
                    lockPath,
                    "analysis resume lease"
                );
                RejectHardLinkedFile(lease, "analysis resume lease");
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }
        catch (IOException ex)
        {
            throw new IOException("Another process owns this analysis result directory.", ex);
        }
    }

    private static void RejectLeafReparsePointIfPresent(
        string path,
        string description
    )
    {
        if (!OperatingSystem.IsWindows())
        {
            if (
                (File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
            )
            {
                throw new ArgumentException(
                    $"The {description} is a reparse point. Use a direct physical path."
                );
            }
            return;
        }
        var attributes = GetFileAttributesW(path);
        if (attributes == InvalidFileAttributes)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                return;
            }
            throw new Win32Exception(
                error,
                $"Could not inspect the physical identity of {description}."
            );
        }
        if ((attributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw new ArgumentException(
                $"The {description} is a reparse point. Use a direct physical path."
            );
        }
    }

    private static async Task<IReadOnlyList<ArtifactRecord>> CreateArtifactRecordsAsync(
        string outputFullPath,
        IReadOnlyList<string> relativePaths,
        Action<long, long>? progress,
        CancellationToken cancellationToken,
        Dictionary<string, FileStream>? retainedArtifactLeases = null
    )
    {
        if (relativePaths.Count == 0)
        {
            throw new ArgumentException("A committed stage must contain at least one artifact.");
        }
        var records = new List<ArtifactRecord>(relativePaths.Count);
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relativePath in relativePaths)
        {
            var fullPath = ResolveArtifactPath(outputFullPath, relativePath);
            var canonicalRelative = Path.GetRelativePath(outputFullPath, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            if (!unique.Add(canonicalRelative))
            {
                throw new InvalidDataException("A committed stage repeats an artifact path.");
            }
            var sha256 = await HashArtifactWithOptionalLeaseAsync(
                canonicalRelative,
                fullPath,
                retainedArtifactLeases,
                progress,
                cancellationToken
            );
            var length = retainedArtifactLeases is not null
                ? retainedArtifactLeases[canonicalRelative].Length
                : new FileInfo(fullPath).Length;
            records.Add(new ArtifactRecord(canonicalRelative, length, sha256));
        }
        return records;
    }

    internal static async Task<string> HashArtifactWithOptionalLeaseAsync(
        string canonicalRelativePath,
        string fullPath,
        Dictionary<string, FileStream>? retainedArtifactLeases,
        Action<long, long>? progress,
        CancellationToken cancellationToken
    )
    {
        if (
            retainedArtifactLeases is not null
            && retainedArtifactLeases.TryGetValue(canonicalRelativePath, out var retained)
        )
        {
            retained.Position = 0;
            return await HashOpenStreamAsync(retained, fullPath, progress, cancellationToken);
        }

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
            RejectHardLinkedFile(stream, "analysis resume artifact");
            var sha256 = await HashOpenStreamAsync(
                stream,
                fullPath,
                progress,
                cancellationToken
            );
            if (retainedArtifactLeases is not null)
            {
                retainedArtifactLeases.Add(canonicalRelativePath, stream);
                stream = null!;
            }
            return sha256;
        }
        finally
        {
            if (stream is not null)
            {
                await stream.DisposeAsync();
            }
        }
    }

    internal static string ResolveArtifactPath(string outputFullPath, string relativePath)
    {
        if (
            string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.IndexOfAny(['\0', '\r', '\n']) >= 0
            || relativePath.Contains('\\')
        )
        {
            throw new InvalidDataException("A checkpoint artifact path is unsafe.");
        }
        var fullPath = Path.GetFullPath(
            Path.Combine(outputFullPath, relativePath.Replace('/', Path.DirectorySeparatorChar))
        );
        var prefix = outputFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? outputFullPath
            : outputFullPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A checkpoint artifact escapes the result directory.");
        }
        AnalysisOrchestrator.EnsureNoReparsePoints(fullPath, "analysis checkpoint artifact");
        return fullPath;
    }

    internal static T ReadStrictJson<T>(string path)
    {
        AnalysisOrchestrator.EnsureNoReparsePoints(path, "analysis resume metadata");
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read
        );
        RejectHardLinkedFile(stream, "analysis resume metadata");
        var length = stream.Length;
        if (length <= 0 || length > MaximumMetadataBytes)
        {
            throw new InvalidDataException($"Analysis resume metadata '{path}' is missing or outside its size limit.");
        }
        var bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.Length != length)
        {
            throw new InvalidDataException(
                $"Analysis resume metadata '{path}' changed while it was read."
            );
        }
        EnsureNoDuplicateProperties(bytes, path);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new JsonException("The JSON value is null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Analysis resume metadata '{path}' is invalid: {ex.Message}", ex);
        }
    }

    private static void EnsureNoDuplicateProperties(ReadOnlySpan<byte> bytes, string path)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            var objectProperties = new Stack<HashSet<string>>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartObject)
                {
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                }
                else if (reader.TokenType == JsonTokenType.EndObject)
                {
                    objectProperties.Pop();
                }
                else if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString() ?? string.Empty;
                    if (objectProperties.Count == 0 || !objectProperties.Peek().Add(name))
                    {
                        throw new InvalidDataException($"Analysis resume metadata '{path}' has duplicate property '{name}'.");
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Analysis resume metadata '{path}' is malformed JSON.", ex);
        }
    }

    internal static async Task WriteNewJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken
    )
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length <= 0 || bytes.Length + 1 > MaximumMetadataBytes)
        {
            throw new InvalidDataException(
                "Analysis resume metadata exceeds its bounded size."
            );
        }
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough
        );
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    internal static async Task<string> HashFileAsync(
        string path,
        Action<long, long>? progress,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        RejectHardLinkedFile(stream, "analysis resume artifact");
        return await HashOpenStreamAsync(stream, path, progress, cancellationToken);
    }

    private static async Task<string> HashOpenStreamAsync(
        FileStream stream,
        string path,
        Action<long, long>? progress,
        CancellationToken cancellationToken
    )
    {
        stream.Position = 0;
        var length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long completed = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }
            hash.AppendData(buffer, 0, read);
            completed += read;
            progress?.Invoke(completed, length);
        }
        if (stream.Length != length)
        {
            throw new InvalidDataException($"Analysis artifact changed while it was hashed: '{path}'.");
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    internal static void DisposeArtifactLeases(
        Dictionary<string, FileStream> artifactLeases
    )
    {
        foreach (var stream in artifactLeases.Values)
        {
            stream.Dispose();
        }
        artifactLeases.Clear();
    }

    internal static string CheckpointFileName(AnalysisResumeStage stage) =>
        $"{stage.Ordinal:D4}-{stage.Id}.json";

    internal static bool IsLowerSha256(string value)
    {
        if (value.Length != 64)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    internal static bool FixedEquals(string left, string right)
    {
        if (!IsLowerSha256(left) || !IsLowerSha256(right))
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right)
        );
    }

    private static void AppendUtf8(IncrementalHash hash, string value, Span<byte> length)
    {
        var bytes = StrictUtf8.GetBytes(value);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    internal static void RefuseNetworkFilesystem(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType == DriveType.Network)
        {
            throw new IOException("Resumable analysis requires a physical local filesystem.");
        }
    }

    internal static void RejectHardLinkedFile(FileStream stream, string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
        {
            throw new InvalidDataException(
                $"Could not inspect the physical link identity of {description}.",
                new Win32Exception(Marshal.GetLastWin32Error())
            );
        }
        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                $"The {description} must have exactly one physical filesystem link."
            );
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        out ByHandleFileInformation information
    );

    private const uint InvalidFileAttributes = 0xFFFFFFFF;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string fileName);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try { Directory.Delete(path, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    internal sealed class AnalysisResumeSession : IAsyncDisposable
    {
        private readonly FileStream _lease;
        private readonly Dictionary<string, FileStream> _artifactLeases;
        private readonly Dictionary<AnalysisResumeStage, (CheckpointRecord Record, string Sha256)> _checkpoints;
        private string _dependencySha256;
        private bool _outcomeWritten;

        internal AnalysisResumeSession(
            string outputDirectory,
            string runId,
            string attemptId,
            AnalysisResumeSpecification specification,
            bool legacyImported,
            AnalysisResumeAttemptMode mode,
            FileStream lease,
            IReadOnlyList<(CheckpointRecord Record, string Sha256)> checkpoints,
            Dictionary<string, FileStream> artifactLeases,
            string? dependencySha256 = null,
            AnalysisResumeTransitionEvidence? transition = null
        )
        {
            OutputDirectory = outputDirectory;
            RunId = runId;
            AttemptId = attemptId;
            Specification = specification;
            LegacyImported = legacyImported;
            Mode = mode;
            _lease = lease;
            _artifactLeases = artifactLeases;
            _checkpoints = checkpoints.ToDictionary(
                item => AnalysisResumeStage.FromOrdinalAndId(item.Record.Ordinal, item.Record.StageId),
                item => item
            );
            _dependencySha256 = dependencySha256 ?? (checkpoints.Count > 0
                ? checkpoints[^1].Sha256
                : HashFileAsync(
                    Path.Combine(OutputDirectory, ResumeDirectoryName, OwnerFileName),
                    null,
                    CancellationToken.None
                ).GetAwaiter().GetResult());
            Transition = transition;
        }

        internal string OutputDirectory { get; }
        internal string RunId { get; }
        internal string AttemptId { get; }
        internal AnalysisResumeSpecification Specification { get; }
        internal bool LegacyImported { get; }
        internal AnalysisResumeAttemptMode Mode { get; }
        internal AnalysisResumeTransitionEvidence? Transition { get; }
        internal IReadOnlyList<string> ReusedStages { get; private set; } = Array.Empty<string>();
        internal Func<AnalysisResumeStage, CancellationToken, Task>?
            AfterStageCommittedAsync
        { get; set; }
        internal AnalysisResumeStage? LastCommittedStage => _checkpoints.Count == 0
            ? null
            : _checkpoints.Keys.OrderBy(stage => stage.Ordinal).Last();

        internal IReadOnlySet<string> CommittedArtifactPaths => _checkpoints.Values
            .SelectMany(item => item.Record.Artifacts)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        internal bool IsPrevalidatedLegacyImportStage(AnalysisResumeStage stage) =>
            Mode == AnalysisResumeAttemptMode.LegacyImport && _checkpoints.ContainsKey(stage);

        internal void RequireMatchingSpecification(AnalysisResumeSpecification expected) =>
            AnalysisResumeCore.RequireMatchingSpecification(Specification, expected);

        internal async Task VerifyCommittedStateAsync(
            Action<long, long>? verificationProgress = null,
            CancellationToken cancellationToken = default
        )
        {
            if (Transition is not null)
            {
                var verifiedDerived = await AnalysisResumeTranslationOffCore.VerifyOpenSessionAsync(
                    this,
                    verificationProgress,
                    cancellationToken,
                    _artifactLeases,
                    verifyArtifacts: false
                );
                RequireMatchingCheckpointHashes(verifiedDerived);
                return;
            }
            var owner = ReadOwner(
                Path.Combine(OutputDirectory, ResumeDirectoryName, OwnerFileName)
            );
            if (
                !string.Equals(owner.RunId, RunId, StringComparison.Ordinal)
                || owner.LegacyImported != LegacyImported
            )
            {
                throw new InvalidDataException(
                    "Analysis resume ownership changed before final publication."
                );
            }
            AnalysisResumeCore.RequireMatchingSpecification(
                owner.Specification,
                Specification
            );
            var verified = await ReadAndVerifyCheckpointsAsync(
                OutputDirectory,
                owner,
                verificationProgress,
                cancellationToken,
                _artifactLeases,
                verifyArtifacts: false
            );
            RequireMatchingCheckpointHashes(verified);
        }

        private void RequireMatchingCheckpointHashes(
            IReadOnlyList<(CheckpointRecord Record, string Sha256)> verified
        )
        {
            var current = _checkpoints
                .OrderBy(item => item.Key.Ordinal)
                .Select(item => item.Value.Sha256)
                .ToArray();
            if (
                verified.Count != current.Length
                || verified.Where((item, index) =>
                        !FixedEquals(item.Sha256, current[index])
                    )
                    .Any()
            )
            {
                throw new InvalidDataException(
                    "The committed checkpoint chain changed before final publication."
                );
            }
        }

        internal async Task<(bool Reused, TStats? Stats)> TryReuseStageAsync<TStats>(
            AnalysisResumeStage stage,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_checkpoints.TryGetValue(stage, out var checkpoint))
            {
                return (false, default);
            }
            TStats? stats;
            try
            {
                stats = checkpoint.Record.Stats.Deserialize<TStats>(JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"Checkpoint statistics for '{stage.Id}' are invalid.", ex);
            }
            if (stats is null)
            {
                throw new InvalidDataException($"Checkpoint statistics for '{stage.Id}' are missing.");
            }
            var reused = ReusedStages.ToList();
            if (!reused.Contains(stage.Id, StringComparer.Ordinal))
            {
                reused.Add(stage.Id);
                ReusedStages = reused;
            }
            await Task.CompletedTask;
            return (true, stats);
        }

        internal async Task CommitStageAsync<TStats>(
            AnalysisResumeStage stage,
            IReadOnlyList<string> artifactRelativePaths,
            TStats stats,
            double elapsedSeconds,
            Action<long, long>? verificationProgress = null,
            CancellationToken cancellationToken = default
        )
        {
            var expectedOrdinal = (_checkpoints.Count == 0 ? 0 : _checkpoints.Keys.Max(item => item.Ordinal)) + 1;
            if (stage.Ordinal != expectedOrdinal)
            {
                throw new InvalidOperationException(
                    $"Stage '{stage.Id}' cannot commit before stage ordinal {expectedOrdinal:N0}."
                );
            }
            var artifacts = await CreateArtifactRecordsAsync(
                OutputDirectory,
                artifactRelativePaths,
                verificationProgress,
                cancellationToken,
                _artifactLeases
            );
            var checkpoint = new CheckpointRecord(
                1,
                "analysis-stage-checkpoint",
                RunId,
                stage.Ordinal,
                stage.Id,
                _dependencySha256,
                DateTimeOffset.UtcNow,
                elapsedSeconds,
                artifacts,
                JsonSerializer.SerializeToElement(stats, JsonOptions)
            );
            var finalPath = Path.Combine(
                OutputDirectory,
                ResumeDirectoryName,
                CheckpointsDirectoryName,
                CheckpointFileName(stage)
            );
            var temporaryPath = Path.Combine(
                OutputDirectory,
                ResumeDirectoryName,
                PendingDirectoryName,
                "checkpoint-" + Guid.NewGuid().ToString("N") + ".json"
            );
            try
            {
                await WriteNewJsonAsync(temporaryPath, checkpoint, CancellationToken.None);
                File.Move(temporaryPath, finalPath, overwrite: false);
                temporaryPath = string.Empty;
                var sha256 = await HashFileAsync(finalPath, null, CancellationToken.None);
                _checkpoints.Add(stage, (checkpoint, sha256));
                _dependencySha256 = sha256;
                if (AfterStageCommittedAsync is not null)
                {
                    await AfterStageCommittedAsync(stage, cancellationToken);
                }
            }
            finally
            {
                if (temporaryPath.Length > 0)
                {
                    TryDeleteFile(temporaryPath);
                }
            }
        }

        internal AnalysisResumePublicEvidence CreatePublicEvidence() =>
            new(
                Transition?.State ?? $"{ResumeDirectoryName}/{OwnerFileName}",
                RunId,
                AttemptId,
                Mode.ToString().ToLowerInvariant(),
                LastCommittedStage?.Id,
                ReusedStages,
                LegacyImported,
                Transition
            );

        internal async Task RecordAttemptOutcomeAsync(
            string status,
            string? failureCategory = null,
            string? provenance = null,
            CancellationToken cancellationToken = default
        )
        {
            if (_outcomeWritten)
            {
                return;
            }
            ValidateToken(status, nameof(status));
            if (failureCategory is not null) ValidateToken(failureCategory, nameof(failureCategory));
            if (provenance is not null) ValidateToken(provenance, nameof(provenance));
            if (
                status is not ("complete" or "failed" or "cancelled" or "abandoned")
                || status == "complete"
                    && (
                        failureCategory is not null
                        || LastCommittedStage != AnalysisResumeStage.EngineLedger
                    )
                || status != "complete" && failureCategory is null
            )
            {
                throw new InvalidOperationException(
                    "The attempt outcome does not match the committed analysis stage."
                );
            }
            var record = new AttemptRecord(
                1,
                "analysis-resume-attempt",
                RunId,
                AttemptId,
                Mode.ToString().ToLowerInvariant(),
                StartedUtc,
                DateTimeOffset.UtcNow,
                status,
                LastCommittedStage?.Id,
                failureCategory,
                provenance
            );
            await PublishAttemptRecordAsync(AttemptPath("outcome"), record, cancellationToken);
            _outcomeWritten = true;
        }

        internal DateTimeOffset StartedUtc { get; private set; }

        internal async Task WriteAttemptStartAsync(CancellationToken cancellationToken)
        {
            StartedUtc = DateTimeOffset.UtcNow;
            var record = new AttemptRecord(
                1,
                "analysis-resume-attempt",
                RunId,
                AttemptId,
                Mode.ToString().ToLowerInvariant(),
                StartedUtc,
                null,
                "running",
                LastCommittedStage?.Id,
                null,
                LegacyImported ? "validated-v2-import" : null
            );
            await PublishAttemptRecordAsync(AttemptPath("start"), record, cancellationToken);
        }

        private static async Task PublishAttemptRecordAsync(
            string finalPath,
            AttemptRecord record,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resumeDirectory = Directory.GetParent(
                Path.GetDirectoryName(finalPath)
                    ?? throw new InvalidOperationException(
                        "Analysis attempt path has no parent directory."
                    )
            )?.FullName
                ?? throw new InvalidOperationException(
                    "Analysis attempt path has no resume metadata directory."
                );
            var temporaryPath = Path.Combine(
                resumeDirectory,
                PendingDirectoryName,
                "attempt-" + Guid.NewGuid().ToString("N") + ".json"
            );
            try
            {
                await WriteNewJsonAsync(temporaryPath, record, CancellationToken.None);
                File.Move(temporaryPath, finalPath, overwrite: false);
                temporaryPath = string.Empty;
            }
            finally
            {
                if (temporaryPath.Length > 0)
                {
                    TryDeleteFile(temporaryPath);
                }
            }
        }

        private string AttemptPath(string suffix) =>
            Path.Combine(
                OutputDirectory,
                ResumeDirectoryName,
                AttemptsDirectoryName,
                $"{AttemptId}-{suffix}.json"
            );

        public async ValueTask DisposeAsync()
        {
            if (!_outcomeWritten)
            {
                try
                {
                    await RecordAttemptOutcomeAsync("abandoned", "session-disposed");
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            foreach (var stream in _artifactLeases.Values)
            {
                await stream.DisposeAsync();
            }
            _artifactLeases.Clear();
            await _lease.DisposeAsync();
        }

        private static void ValidateToken(string value, string name)
        {
            if (
                string.IsNullOrWhiteSpace(value)
                || value.Length > 64
                || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            )
            {
                throw new ArgumentException("Attempt provenance values must be short ASCII tokens.", name);
            }
        }
    }
}
