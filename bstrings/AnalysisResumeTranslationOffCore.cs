#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal enum AnalysisResumeTranslationOffFaultPoint
{
    AfterJournalPendingPublished,
    AfterJournalPublished,
    AfterArchiveMove,
    BeforeDerivedOwnerPublished,
    AfterDerivedOwnerPendingPublished,
    AfterDerivedOwnerPublished,
    AfterJournalRemoved,
}

internal sealed record AnalysisResumeInheritedCheckpointReference(
    int Ordinal,
    string StageId,
    string Sha256
);

internal sealed record AnalysisResumeArchivedMove(
    string SourcePath,
    string DestinationPath,
    long Bytes,
    string Sha256,
    string Kind
);

internal sealed record AnalysisResumeSupersededCheckpointReference(
    int Ordinal,
    string StageId,
    string Sha256,
    IReadOnlyList<AnalysisResumeCore.ArtifactRecord> Artifacts
);

internal sealed record AnalysisResumeParentAttemptReference(
    string FileName,
    long Bytes,
    string Sha256
);

internal sealed record AnalysisResumeTransitionEvidence(
    string State,
    string ParentRunId,
    string GenerationId,
    string TransitionId,
    string Kind,
    IReadOnlyList<string> ExcludedEngines,
    string FromTranslationMode,
    string ToTranslationMode,
    string InheritedThroughStage,
    IReadOnlyList<string> SupersededStages,
    string ArchiveRoot,
    IReadOnlyList<AnalysisResumeParentAttemptReference> SourceAttempts,
    bool AbandonedModelWork
);

internal sealed record AnalysisResumeDerivedOwnerRecord(
    int SchemaVersion,
    string RecordType,
    string RunId,
    string ParentRunId,
    string GenerationId,
    string TransitionId,
    DateTimeOffset CreatedUtc,
    string ParentOwnerSha256,
    string ParentSpecificationSha256,
    AnalysisResumeSpecification Specification,
    IReadOnlyList<AnalysisResumeParentAttemptReference> ParentAttempts,
    IReadOnlyList<AnalysisResumeInheritedCheckpointReference> InheritedCheckpoints,
    AnalysisResumeSupersededCheckpointReference SupersededCheckpoint,
    IReadOnlyList<AnalysisResumeArchivedMove> ArchiveMoves,
    AnalysisResumeTransitionEvidence Transition
);

internal sealed record AnalysisResumeTranslationOffJournalRecord(
    int SchemaVersion,
    string RecordType,
    AnalysisResumeDerivedOwnerRecord DerivedOwner
);

internal sealed class AnalysisResumeTranslationOffValidationContext
{
    private readonly IReadOnlyDictionary<AnalysisResumeStage, JsonElement> _stageStats;

    internal AnalysisResumeTranslationOffValidationContext(
        string outputDirectory,
        AnalysisResumeSpecification specification,
        bool legacyImported,
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)> checkpoints
    )
    {
        OutputDirectory = outputDirectory;
        Specification = specification;
        LegacyImported = legacyImported;
        _stageStats = checkpoints.ToDictionary(
            item => AnalysisResumeStage.FromOrdinalAndId(
                item.Record.Ordinal,
                item.Record.StageId
            ),
            item => item.Record.Stats.Clone()
        );
    }

    internal string OutputDirectory { get; }
    internal AnalysisResumeSpecification Specification { get; }
    internal bool LegacyImported { get; }
    internal IReadOnlyList<AnalysisResumeStage> Stages => _stageStats.Keys
        .OrderBy(stage => stage.Ordinal)
        .ToArray();

    internal T GetStats<T>(AnalysisResumeStage stage)
    {
        if (!_stageStats.TryGetValue(stage, out var stats))
        {
            throw new InvalidOperationException(
                $"The validated transition boundary has no '{stage.Id}' checkpoint."
            );
        }
        try
        {
            return stats.Deserialize<T>(AnalysisResumeCore.JsonOptions)
                ?? throw new InvalidDataException(
                    $"Checkpoint statistics for '{stage.Id}' are missing."
                );
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Checkpoint statistics for '{stage.Id}' are invalid.",
                ex
            );
        }
    }
}

internal static class AnalysisResumeTranslationOffCore
{
    internal const string DerivedOwnerFileName = "derived-owner.json";
    internal const string JournalFileName = "translation-off-transition.json";
    internal const string JournalPendingFileName =
        "translation-off-transition.pending.json";
    private const string DerivedOwnerRecordType = "analysis-resume-derived-owner";
    private const string JournalRecordType = "analysis-resume-translation-off-journal";
    private const string TransitionKind = "exclude-engine";
    private const string TranslationEngine = "translation";
    private const string FromMode = "auto";
    private const string ToMode = "off";
    private const string ArchivePrefix = "resume-transition-";

    internal static bool HasTransitionState(string outputDirectory)
    {
        var outputFullPath = AnalysisResumeCore.CanonicalizeOutputDirectory(outputDirectory);
        var resumeDirectory = Path.Combine(outputFullPath, AnalysisResumeCore.ResumeDirectoryName);
        return File.Exists(Path.Combine(resumeDirectory, DerivedOwnerFileName))
            || File.Exists(
                Path.Combine(
                    resumeDirectory,
                    AnalysisResumeCore.PendingDirectoryName,
                    JournalFileName
                )
            );
    }

    internal static AnalysisResumeSpecification ReadEffectiveSpecification(string outputDirectory)
    {
        var outputFullPath = AnalysisResumeCore.CanonicalizeOutputDirectory(outputDirectory);
        var derivedPath = DerivedOwnerPath(outputFullPath);
        if (File.Exists(derivedPath))
        {
            return ReadAndValidateDerivedOwner(outputFullPath).Specification;
        }
        var journal = ReadAndValidateJournal(outputFullPath);
        return journal.DerivedOwner.Specification;
    }

    internal static async Task<AnalysisResumeCore.AnalysisResumeSession> TransitionAsync(
        string outputDirectory,
        AnalysisResumeSpecification expectedParentSpecification,
        AnalysisResumeSpecification targetSpecification,
        Func<
            AnalysisResumeTranslationOffValidationContext,
            CancellationToken,
            Task
        > validateParentAsync,
        Action<long, long>? verificationProgress = null,
        Func<
            AnalysisResumeTranslationOffFaultPoint,
            int,
            CancellationToken,
            Task
        >? faultInjector = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(validateParentAsync);
        AnalysisResumeCore.ValidateSpecification(expectedParentSpecification);
        AnalysisResumeCore.ValidateSpecification(targetSpecification);
        ValidateSpecificationTransition(expectedParentSpecification, targetSpecification);
        var outputFullPath = AnalysisResumeCore.CanonicalizeOutputDirectory(outputDirectory);
        AnalysisResumeCore.RequireSpecificationOutputDirectory(
            expectedParentSpecification,
            outputFullPath
        );
        AnalysisResumeCore.RequireSpecificationOutputDirectory(targetSpecification, outputFullPath);
        AnalysisResumeCore.ValidateOwnedIncompleteDirectory(outputFullPath);

        if (File.Exists(DerivedOwnerPath(outputFullPath)))
        {
            var existing = ReadAndValidateDerivedOwner(outputFullPath);
            var parent = AnalysisResumeCore.ReadOwner(
                Path.Combine(
                    outputFullPath,
                    AnalysisResumeCore.ResumeDirectoryName,
                    AnalysisResumeCore.OwnerFileName
                )
            );
            AnalysisResumeCore.RequireMatchingSpecification(
                parent.Specification,
                expectedParentSpecification
            );
            ValidateParentIdentity(outputFullPath, parent, existing);
            AnalysisResumeCore.RequireMatchingSpecification(
                existing.Specification,
                targetSpecification
            );
            return await AnalysisResumeCore.OpenExistingAsync(
                outputFullPath,
                targetSpecification,
                verificationProgress,
                cancellationToken
            );
        }

        var lease = AnalysisResumeCore.AcquireLease(outputFullPath, createIfMissing: false);
        try
        {
            if (File.Exists(JournalPath(outputFullPath)))
            {
                var pending = ReadAndValidateJournal(outputFullPath);
                var parent = AnalysisResumeCore.ReadOwner(
                    Path.Combine(
                        outputFullPath,
                        AnalysisResumeCore.ResumeDirectoryName,
                        AnalysisResumeCore.OwnerFileName
                    )
                );
                AnalysisResumeCore.RequireMatchingSpecification(
                    parent.Specification,
                    expectedParentSpecification
                );
                ValidateParentIdentity(outputFullPath, parent, pending.DerivedOwner);
                AnalysisResumeCore.RequireMatchingSpecification(
                    pending.DerivedOwner.Specification,
                    targetSpecification
                );
                await CompleteJournaledTransitionAsync(
                    outputFullPath,
                    pending,
                    faultInjector,
                    cancellationToken
                );
            }
            else
            {
                AnalysisResumeCore.ValidateMetadataLayout(outputFullPath);
                RequireEmptyTransitionPendingDirectory(outputFullPath);
                var ownerPath = Path.Combine(
                    outputFullPath,
                    AnalysisResumeCore.ResumeDirectoryName,
                    AnalysisResumeCore.OwnerFileName
                );
                var owner = AnalysisResumeCore.ReadOwner(ownerPath);
                AnalysisResumeCore.RequireMatchingSpecification(
                    owner.Specification,
                    expectedParentSpecification
                );
                AnalysisResumeCore.ValidateAttemptRecords(outputFullPath, owner);
                var artifactLeases = new Dictionary<string, FileStream>(
                    StringComparer.OrdinalIgnoreCase
                );
                IReadOnlyList<(
                    AnalysisResumeCore.CheckpointRecord Record,
                    string Sha256
                )> checkpoints;
                try
                {
                    checkpoints = await AnalysisResumeCore.ReadAndVerifyCheckpointsAsync(
                        outputFullPath,
                        owner,
                        verificationProgress,
                        cancellationToken,
                        artifactLeases
                    );
                    await validateParentAsync(
                        new AnalysisResumeTranslationOffValidationContext(
                            outputFullPath,
                            owner.Specification,
                            owner.LegacyImported,
                            checkpoints
                        ),
                        cancellationToken
                    );
                }
                finally
                {
                    AnalysisResumeCore.DisposeArtifactLeases(artifactLeases);
                }
                var journal = await CreateJournalAsync(
                    outputFullPath,
                    owner,
                    ownerPath,
                    checkpoints,
                    targetSpecification,
                    cancellationToken
                );
                await PublishJournalAsync(
                    outputFullPath,
                    journal,
                    faultInjector,
                    cancellationToken
                );
                await InvokeFaultAsync(
                    faultInjector,
                    AnalysisResumeTranslationOffFaultPoint.AfterJournalPublished,
                    -1,
                    cancellationToken
                );
                await CompleteJournaledTransitionAsync(
                    outputFullPath,
                    journal,
                    faultInjector,
                    cancellationToken
                );
            }
        }
        finally
        {
            await lease.DisposeAsync();
        }

        return await AnalysisResumeCore.OpenExistingAsync(
            outputFullPath,
            targetSpecification,
            verificationProgress,
            cancellationToken
        );
    }

    internal static async Task<AnalysisResumeCore.AnalysisResumeSession> OpenExistingAsync(
        string outputFullPath,
        AnalysisResumeSpecification expectedSpecification,
        Action<long, long>? verificationProgress,
        CancellationToken cancellationToken
    )
    {
        var lease = AnalysisResumeCore.AcquireLease(outputFullPath, createIfMissing: false);
        var artifactLeases = new Dictionary<string, FileStream>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(JournalPath(outputFullPath)))
            {
                var journal = ReadAndValidateJournal(outputFullPath);
                AnalysisResumeCore.RequireMatchingSpecification(
                    journal.DerivedOwner.Specification,
                    expectedSpecification
                );
                await CompleteJournaledTransitionAsync(
                    outputFullPath,
                    journal,
                    faultInjector: null,
                    cancellationToken: cancellationToken
                );
            }
            ValidateDerivedMetadataLayout(outputFullPath);
            var owner = AnalysisResumeCore.ReadOwner(
                Path.Combine(
                    outputFullPath,
                    AnalysisResumeCore.ResumeDirectoryName,
                    AnalysisResumeCore.OwnerFileName
                )
            );
            var derived = ReadAndValidateDerivedOwner(outputFullPath);
            AnalysisResumeCore.RequireMatchingSpecification(
                derived.Specification,
                expectedSpecification
            );
            ValidateParentIdentity(outputFullPath, owner, derived);
            await ValidateDerivedAttemptRecordsAsync(
                outputFullPath,
                owner,
                derived,
                cancellationToken
            );
            AnalysisResumeCore.RemoveInterruptedPublications(outputFullPath);
            await VerifyDerivedArchiveAsync(
                outputFullPath,
                derived,
                cancellationToken
            );
            var validated = await ReadAndVerifyDerivedCheckpointsAsync(
                outputFullPath,
                owner,
                derived,
                verificationProgress,
                cancellationToken,
                artifactLeases
            );
            var session = new AnalysisResumeCore.AnalysisResumeSession(
                outputFullPath,
                derived.RunId,
                Guid.NewGuid().ToString("N"),
                derived.Specification,
                owner.LegacyImported,
                AnalysisResumeAttemptMode.Resume,
                lease,
                validated.Checkpoints,
                artifactLeases,
                validated.NextDependencySha256,
                derived.Transition
            );
            await session.WriteAttemptStartAsync(cancellationToken);
            return session;
        }
        catch
        {
            lease.Dispose();
            AnalysisResumeCore.DisposeArtifactLeases(artifactLeases);
            throw;
        }
    }

    internal static async Task<
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)>
    > VerifyOpenSessionAsync(
        AnalysisResumeCore.AnalysisResumeSession session,
        Action<long, long>? verificationProgress,
        CancellationToken cancellationToken,
        Dictionary<string, FileStream> artifactLeases,
        bool verifyArtifacts
    )
    {
        var outputFullPath = session.OutputDirectory;
        ValidateDerivedMetadataLayout(outputFullPath);
        var owner = AnalysisResumeCore.ReadOwner(
            Path.Combine(
                outputFullPath,
                AnalysisResumeCore.ResumeDirectoryName,
                AnalysisResumeCore.OwnerFileName
            )
        );
        var derived = ReadAndValidateDerivedOwner(outputFullPath);
        if (
            !string.Equals(derived.RunId, session.RunId, StringComparison.Ordinal)
            || owner.LegacyImported != session.LegacyImported
        )
        {
            throw new InvalidDataException(
                "Analysis resume ownership changed before final publication."
            );
        }
        AnalysisResumeCore.RequireMatchingSpecification(
            derived.Specification,
            session.Specification
        );
        ValidateParentIdentity(outputFullPath, owner, derived);
        await ValidateDerivedAttemptRecordsAsync(
            outputFullPath,
            owner,
            derived,
            cancellationToken
        );
        await VerifyDerivedArchiveAsync(outputFullPath, derived, cancellationToken);
        var validated = await ReadAndVerifyDerivedCheckpointsAsync(
            outputFullPath,
            owner,
            derived,
            verificationProgress,
            cancellationToken,
            artifactLeases,
            verifyArtifacts
        );
        return validated.Checkpoints;
    }

    private static async Task<AnalysisResumeTranslationOffJournalRecord> CreateJournalAsync(
        string outputFullPath,
        AnalysisResumeCore.OwnerRecord owner,
        string ownerPath,
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)> checkpoints,
        AnalysisResumeSpecification targetSpecification,
        CancellationToken cancellationToken
    )
    {
        if (checkpoints.Count != AnalysisResumeStage.TranslationSelection.Ordinal)
        {
            throw new InvalidDataException(
                "Translation can be excluded during resume only before the translation stage is committed."
            );
        }
        var superseded = checkpoints[^1];
        if (
            superseded.Record.Ordinal != AnalysisResumeStage.TranslationSelection.Ordinal
            || !string.Equals(
                superseded.Record.StageId,
                AnalysisResumeStage.TranslationSelection.Id,
                StringComparison.Ordinal
            )
            || superseded.Record.Artifacts.Count != 2
            || !string.Equals(
                superseded.Record.Artifacts[0].Path,
                "language-assessments.jsonl",
                StringComparison.Ordinal
            )
            || !string.Equals(
                superseded.Record.Artifacts[1].Path,
                "translation-candidates.jsonl",
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "The committed translation-selection checkpoint does not have the canonical artifact contract."
            );
        }
        ValidateTransitionRootEntries(outputFullPath, checkpoints);
        var transitionId = Guid.NewGuid().ToString("N");
        var derivedRunId = Guid.NewGuid().ToString("N");
        var generationId = Guid.NewGuid().ToString("N");
        var archiveRoot = $"logs/{ArchivePrefix}{transitionId}/superseded";
        var moves = new List<AnalysisResumeArchivedMove>();
        foreach (var artifact in superseded.Record.Artifacts)
        {
            moves.Add(
                new AnalysisResumeArchivedMove(
                    artifact.Path,
                    $"{archiveRoot}/artifacts/{artifact.Path}",
                    artifact.Bytes,
                    artifact.Sha256,
                    "superseded-stage-artifact"
                )
            );
        }
        moves.Add(
            new AnalysisResumeArchivedMove(
                $"{AnalysisResumeCore.ResumeDirectoryName}/{AnalysisResumeCore.CheckpointsDirectoryName}/{AnalysisResumeCore.CheckpointFileName(AnalysisResumeStage.TranslationSelection)}",
                $"{archiveRoot}/checkpoints/{AnalysisResumeCore.CheckpointFileName(AnalysisResumeStage.TranslationSelection)}",
                new FileInfo(
                    Path.Combine(
                        outputFullPath,
                        AnalysisResumeCore.ResumeDirectoryName,
                        AnalysisResumeCore.CheckpointsDirectoryName,
                        AnalysisResumeCore.CheckpointFileName(
                            AnalysisResumeStage.TranslationSelection
                        )
                    )
                ).Length,
                superseded.Sha256,
                "superseded-checkpoint"
            )
        );
        foreach (var relativePath in EnumerateAbandonedTranslationArtifacts(outputFullPath))
        {
            var fullPath = ResolveOwnedRelativePath(outputFullPath, relativePath);
            moves.Add(
                new AnalysisResumeArchivedMove(
                    relativePath,
                    $"{archiveRoot}/abandoned/{Path.GetFileName(relativePath)}",
                    new FileInfo(fullPath).Length,
                    await AnalysisResumeCore.HashFileAsync(
                        fullPath,
                        null,
                        cancellationToken
                    ),
                    "abandoned-translation-work"
                )
            );
        }
        RequireUniqueMoves(moves);
        var inherited = checkpoints
            .Take(AnalysisResumeStage.RawMerge.Ordinal)
            .Select(item =>
                new AnalysisResumeInheritedCheckpointReference(
                    item.Record.Ordinal,
                    item.Record.StageId,
                    item.Sha256
                )
            )
            .ToArray();
        var parentAttempts = await CreateParentAttemptReferencesAsync(
            outputFullPath,
            cancellationToken
        );
        var transition = new AnalysisResumeTransitionEvidence(
            $"{AnalysisResumeCore.ResumeDirectoryName}/{DerivedOwnerFileName}",
            owner.RunId,
            generationId,
            transitionId,
            TransitionKind,
            [TranslationEngine],
            FromMode,
            ToMode,
            AnalysisResumeStage.RawMerge.Id,
            [AnalysisResumeStage.TranslationSelection.Id],
            archiveRoot,
            parentAttempts,
            moves.Any(move => move.Kind == "abandoned-translation-work")
        );
        var derived = new AnalysisResumeDerivedOwnerRecord(
            2,
            DerivedOwnerRecordType,
            derivedRunId,
            owner.RunId,
            generationId,
            transitionId,
            DateTimeOffset.UtcNow,
            await AnalysisResumeCore.HashFileAsync(ownerPath, null, cancellationToken),
            HashSpecification(owner.Specification),
            targetSpecification,
            parentAttempts,
            inherited,
            new AnalysisResumeSupersededCheckpointReference(
                superseded.Record.Ordinal,
                superseded.Record.StageId,
                superseded.Sha256,
                superseded.Record.Artifacts
            ),
            moves,
            transition
        );
        ValidateDerivedOwner(outputFullPath, derived);
        var journal = new AnalysisResumeTranslationOffJournalRecord(
            2,
            JournalRecordType,
            derived
        );
        if (
            JsonSerializer.SerializeToUtf8Bytes(journal, AnalysisResumeCore.JsonOptions)
                .Length > AnalysisResumeCore.MaximumMetadataBytes
        )
        {
            throw new InvalidDataException(
                "Translation-off transition metadata exceeds the supported size limit."
            );
        }
        return journal;
    }

    private static async Task CompleteJournaledTransitionAsync(
        string outputFullPath,
        AnalysisResumeTranslationOffJournalRecord journal,
        Func<
            AnalysisResumeTranslationOffFaultPoint,
            int,
            CancellationToken,
            Task
        >? faultInjector,
        CancellationToken cancellationToken
    )
    {
        ValidateJournal(outputFullPath, journal);
        if (
            File.Exists(JournalPath(outputFullPath))
            && File.Exists(JournalPendingPath(outputFullPath))
        )
        {
            throw new InvalidDataException(
                "Translation-off transition has both pending and active journal publications."
            );
        }
        var owner = AnalysisResumeCore.ReadOwner(
            Path.Combine(
                outputFullPath,
                AnalysisResumeCore.ResumeDirectoryName,
                AnalysisResumeCore.OwnerFileName
            )
        );
        ValidateParentIdentity(outputFullPath, owner, journal.DerivedOwner);
        AnalysisResumeCore.ValidateAttemptRecords(outputFullPath, owner);
        await VerifyParentAttemptReferencesAsync(
            outputFullPath,
            journal.DerivedOwner,
            allowDerivedAttempts: false,
            cancellationToken: cancellationToken
        );
        var inheritedArtifactLeases = new Dictionary<string, FileStream>(
            StringComparer.OrdinalIgnoreCase
        );
        try
        {
            var inherited = await VerifyJournalParentBoundaryAsync(
                outputFullPath,
                owner,
                journal.DerivedOwner,
                inheritedArtifactLeases,
                cancellationToken
            );
            ValidateJournaledTransitionRootEntries(
                outputFullPath,
                inherited,
                journal.DerivedOwner
            );
            var archiveRoot = ResolveOwnedRelativePath(
                outputFullPath,
                journal.DerivedOwner.Transition.ArchiveRoot
            );
            Directory.CreateDirectory(archiveRoot);
            AnalysisOrchestrator.EnsureNoReparsePoints(
                archiveRoot,
                "translation-off resume archive"
            );
            for (
                var index = 0;
                index < journal.DerivedOwner.ArchiveMoves.Count;
                index++
            )
            {
                cancellationToken.ThrowIfCancellationRequested();
                var move = journal.DerivedOwner.ArchiveMoves[index];
                await CompleteMoveAsync(outputFullPath, move, cancellationToken);
                await InvokeFaultAsync(
                    faultInjector,
                    AnalysisResumeTranslationOffFaultPoint.AfterArchiveMove,
                    index,
                    cancellationToken
                );
            }
            await VerifyDerivedArchiveAsync(
                outputFullPath,
                journal.DerivedOwner,
                cancellationToken
            );
            await InvokeFaultAsync(
                faultInjector,
                AnalysisResumeTranslationOffFaultPoint.BeforeDerivedOwnerPublished,
                -1,
                cancellationToken
            );
            var derivedPath = DerivedOwnerPath(outputFullPath);
            if (!File.Exists(derivedPath))
            {
                var pendingPath = Path.Combine(
                    outputFullPath,
                    AnalysisResumeCore.ResumeDirectoryName,
                    AnalysisResumeCore.PendingDirectoryName,
                    "derived-owner-" + journal.DerivedOwner.GenerationId + ".json"
                );
                RemoveOwnedInterruptedDerivedPublication(pendingPath);
                await AnalysisResumeCore.WriteNewJsonAsync(
                    pendingPath,
                    journal.DerivedOwner,
                    CancellationToken.None
                );
                await InvokeFaultAsync(
                    faultInjector,
                    AnalysisResumeTranslationOffFaultPoint.AfterDerivedOwnerPendingPublished,
                    -1,
                    cancellationToken
                );
                File.Move(pendingPath, derivedPath, overwrite: false);
            }
            else
            {
                RequireEqualDerivedOwner(
                    journal.DerivedOwner,
                    AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
                        derivedPath
                    )
                );
            }
            await InvokeFaultAsync(
                faultInjector,
                AnalysisResumeTranslationOffFaultPoint.AfterDerivedOwnerPublished,
                -1,
                cancellationToken
            );
            var journalPath = JournalPath(outputFullPath);
            if (File.Exists(journalPath))
            {
                File.Delete(journalPath);
            }
            await InvokeFaultAsync(
                faultInjector,
                AnalysisResumeTranslationOffFaultPoint.AfterJournalRemoved,
                -1,
                cancellationToken
            );
        }
        finally
        {
            AnalysisResumeCore.DisposeArtifactLeases(inheritedArtifactLeases);
        }
    }

    private static async Task CompleteMoveAsync(
        string outputFullPath,
        AnalysisResumeArchivedMove move,
        CancellationToken cancellationToken
    )
    {
        var source = ResolveOwnedRelativePath(outputFullPath, move.SourcePath);
        var destination = ResolveOwnedRelativePath(outputFullPath, move.DestinationPath);
        var sourceExists = File.Exists(source);
        var destinationExists = File.Exists(destination);
        if (sourceExists && destinationExists)
        {
            throw new InvalidDataException(
                $"Translation-off transition has both source and archive copies for '{move.SourcePath}'."
            );
        }
        var current = sourceExists ? source : destinationExists ? destination : null;
        if (current is null)
        {
            throw new InvalidDataException(
                $"Translation-off transition lost '{move.SourcePath}' before archival completed."
            );
        }
        var info = new FileInfo(current);
        if (info.Length != move.Bytes)
        {
            throw new InvalidDataException(
                $"Translation-off transition artifact '{move.SourcePath}' changed size."
            );
        }
        var sha256 = await AnalysisResumeCore.HashFileAsync(
            current,
            null,
            cancellationToken
        );
        if (!AnalysisResumeCore.FixedEquals(sha256, move.Sha256))
        {
            throw new InvalidDataException(
                $"Translation-off transition artifact '{move.SourcePath}' failed SHA-256 validation."
            );
        }
        if (!sourceExists)
        {
            return;
        }
        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidDataException("Translation-off archive path has no parent.");
        Directory.CreateDirectory(parent);
        AnalysisOrchestrator.EnsureNoReparsePoints(
            parent,
            "translation-off resume archive directory"
        );
        AnalysisOrchestrator.EnsureNoReparsePoints(
            source,
            "translation-off resume source artifact"
        );
        File.Move(source, destination, overwrite: false);
    }

    private static async Task<
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)>
    > VerifyJournalParentBoundaryAsync(
        string outputFullPath,
        AnalysisResumeCore.OwnerRecord owner,
        AnalysisResumeDerivedOwnerRecord derived,
        Dictionary<string, FileStream> artifactLeases,
        CancellationToken cancellationToken
    )
    {
        var checkpointDirectory = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.CheckpointsDirectoryName
        );
        var entries = Directory
            .EnumerateFileSystemEntries(
                checkpointDirectory,
                "*",
                SearchOption.TopDirectoryOnly
            )
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var permittedNames = AnalysisResumeStage.All
            .Take(AnalysisResumeStage.TranslationSelection.Ordinal)
            .Select(AnalysisResumeCore.CheckpointFileName)
            .ToHashSet(StringComparer.Ordinal);
        if (
            entries.Any(entry =>
                !File.Exists(entry)
                || !permittedNames.Contains(Path.GetFileName(entry))
            )
        )
        {
            throw new InvalidDataException(
                "Journaled translation-off checkpoint storage changed before recovery."
            );
        }

        var results = new List<(
            AnalysisResumeCore.CheckpointRecord Record,
            string Sha256
        )>(AnalysisResumeStage.RawMerge.Ordinal);
        var dependencySha256 = derived.ParentOwnerSha256;
        for (var index = 0; index < AnalysisResumeStage.RawMerge.Ordinal; index++)
        {
            var stage = AnalysisResumeStage.All[index];
            var path = Path.Combine(
                checkpointDirectory,
                AnalysisResumeCore.CheckpointFileName(stage)
            );
            var record = AnalysisResumeCore.ReadStrictJson<AnalysisResumeCore.CheckpointRecord>(
                path
            );
            AnalysisResumeCore.ValidateCheckpoint(record, owner, stage, dependencySha256);
            var checkpointSha256 = await AnalysisResumeCore.HashFileAsync(
                path,
                null,
                cancellationToken
            );
            var inherited = derived.InheritedCheckpoints[index];
            if (
                inherited.Ordinal != record.Ordinal
                || !string.Equals(inherited.StageId, record.StageId, StringComparison.Ordinal)
                || !AnalysisResumeCore.FixedEquals(inherited.Sha256, checkpointSha256)
            )
            {
                throw new InvalidDataException(
                    $"Journaled inheritance for '{stage.Id}' changed before recovery."
                );
            }
            results.Add((record, checkpointSha256));
            dependencySha256 = checkpointSha256;
        }
        await VerifyCheckpointArtifactsAsync(
            outputFullPath,
            results,
            null,
            cancellationToken,
            artifactLeases,
            verifyArtifacts: true
        );
        return results;
    }

    private static void ValidateJournaledTransitionRootEntries(
        string outputFullPath,
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)> inherited,
        AnalysisResumeDerivedOwnerRecord derived
    )
    {
        var allowedFiles = inherited
            .SelectMany(item => item.Record.Artifacts)
            .Select(item => item.Path)
            .Concat(
                derived.ArchiveMoves
                    .Where(move => !move.SourcePath.Contains('/'))
                    .Select(move => move.SourcePath)
            )
            .Append(".incomplete")
            .Append(AnalysisResumeCore.LockFileName)
            .Append("run.json")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(outputFullPath))
        {
            var name = Path.GetFileName(path);
            if (
                string.Equals(
                    name,
                    AnalysisResumeCore.ResumeDirectoryName,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase)
            )
            {
                if (!Directory.Exists(path))
                {
                    throw new InvalidDataException(
                        $"Journaled transition directory '{name}' has the wrong file type."
                    );
                }
                AnalysisOrchestrator.EnsureNoReparsePoints(
                    path,
                    "journaled translation-off transition directory"
                );
                continue;
            }
            if (name is not null && allowedFiles.Contains(name) && File.Exists(path))
            {
                AnalysisOrchestrator.EnsureNoReparsePoints(
                    path,
                    "journaled translation-off transition artifact"
                );
                continue;
            }
            throw new InvalidDataException(
                $"Journaled translation-off transition found untracked result entry '{name}'."
            );
        }
    }

    private static async Task VerifyDerivedArchiveAsync(
        string outputFullPath,
        AnalysisResumeDerivedOwnerRecord derived,
        CancellationToken cancellationToken
    )
    {
        var archiveRoot = ResolveOwnedRelativePath(
            outputFullPath,
            derived.Transition.ArchiveRoot
        );
        AnalysisOrchestrator.EnsureNoReparsePoints(
            archiveRoot,
            "derived translation-off archive"
        );
        var expectedFiles = derived.ArchiveMoves.ToDictionary(
            move => ResolveOwnedRelativePath(outputFullPath, move.DestinationPath),
            move => move,
            StringComparer.OrdinalIgnoreCase
        );
        var actualFiles = Directory
            .EnumerateFiles(archiveRoot, "*", SearchOption.AllDirectories)
            .ToArray();
        var actualDirectories = Directory
            .EnumerateDirectories(archiveRoot, "*", SearchOption.AllDirectories)
            .Prepend(archiveRoot)
            .ToArray();
        var expectedDirectories = expectedFiles.Keys
            .Select(Path.GetDirectoryName)
            .Where(path => path is not null)
            .Cast<string>()
            .Append(archiveRoot)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (
            actualFiles.Length != expectedFiles.Count
            || actualFiles.Any(path => !expectedFiles.ContainsKey(path))
            || actualDirectories.Any(path => !expectedDirectories.Contains(path))
        )
        {
            throw new InvalidDataException(
                "Derived translation-off archive contains an unexpected or missing entry."
            );
        }
        foreach (var directory in actualDirectories)
        {
            AnalysisOrchestrator.EnsureNoReparsePoints(
                directory,
                "derived translation-off archive directory"
            );
        }
        foreach (var (path, move) in expectedFiles)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != move.Bytes)
            {
                throw new InvalidDataException(
                    $"Archived transition artifact '{move.SourcePath}' changed size or is missing."
                );
            }
            var sha256 = await AnalysisResumeCore.HashFileAsync(
                path,
                null,
                cancellationToken
            );
            if (!AnalysisResumeCore.FixedEquals(sha256, move.Sha256))
            {
                throw new InvalidDataException(
                    $"Archived transition artifact '{move.SourcePath}' failed SHA-256 validation."
                );
            }
        }
    }

    private static async Task<DerivedCheckpointValidation> ReadAndVerifyDerivedCheckpointsAsync(
        string outputFullPath,
        AnalysisResumeCore.OwnerRecord owner,
        AnalysisResumeDerivedOwnerRecord derived,
        Action<long, long>? verificationProgress,
        CancellationToken cancellationToken,
        Dictionary<string, FileStream> retainedArtifactLeases,
        bool verifyArtifacts = true
    )
    {
        var checkpointDirectory = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.CheckpointsDirectoryName
        );
        var files = Directory
            .EnumerateFiles(checkpointDirectory, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var allEntries = Directory
            .EnumerateFileSystemEntries(checkpointDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (
            allEntries.Length != files.Length
            || files.Length < AnalysisResumeStage.RawMerge.Ordinal
            || files.Length > AnalysisResumeStage.All.Count
        )
        {
            throw new InvalidDataException(
                "Derived analysis checkpoint storage is not a contiguous canonical prefix."
            );
        }
        var results = new List<(
            AnalysisResumeCore.CheckpointRecord Record,
            string Sha256
        )>(files.Length);
        var parentDependency = derived.ParentOwnerSha256;
        var derivedDependency = await AnalysisResumeCore.HashFileAsync(
            DerivedOwnerPath(outputFullPath),
            null,
            cancellationToken
        );
        var activeOwner = new AnalysisResumeCore.OwnerRecord(
            1,
            "analysis-resume-owner",
            derived.RunId,
            derived.CreatedUtc,
            derived.Specification,
            owner.LegacyImported
        );
        for (var index = 0; index < files.Length; index++)
        {
            var stage = AnalysisResumeStage.All[index];
            if (
                !string.Equals(
                    Path.GetFileName(files[index]),
                    AnalysisResumeCore.CheckpointFileName(stage),
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    "Derived analysis checkpoint files do not form a contiguous canonical prefix."
                );
            }
            var record = AnalysisResumeCore.ReadStrictJson<AnalysisResumeCore.CheckpointRecord>(
                files[index]
            );
            var dependency = index < AnalysisResumeStage.RawMerge.Ordinal
                ? parentDependency
                : derivedDependency;
            AnalysisResumeCore.ValidateCheckpoint(
                record,
                index < AnalysisResumeStage.RawMerge.Ordinal ? owner : activeOwner,
                stage,
                dependency
            );
            var checkpointSha256 = await AnalysisResumeCore.HashFileAsync(
                files[index],
                null,
                cancellationToken
            );
            results.Add((record, checkpointSha256));
            if (index < AnalysisResumeStage.RawMerge.Ordinal)
            {
                var inherited = derived.InheritedCheckpoints[index];
                if (
                    inherited.Ordinal != record.Ordinal
                    || !string.Equals(inherited.StageId, record.StageId, StringComparison.Ordinal)
                    || !AnalysisResumeCore.FixedEquals(inherited.Sha256, checkpointSha256)
                )
                {
                    throw new InvalidDataException(
                        $"Derived checkpoint inheritance for '{stage.Id}' is invalid."
                    );
                }
                parentDependency = checkpointSha256;
            }
            else
            {
                derivedDependency = checkpointSha256;
            }
        }
        await VerifyCheckpointArtifactsAsync(
            outputFullPath,
            results,
            verificationProgress,
            cancellationToken,
            retainedArtifactLeases,
            verifyArtifacts
        );
        return new DerivedCheckpointValidation(results, derivedDependency);
    }

    private static async Task VerifyCheckpointArtifactsAsync(
        string outputFullPath,
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)> checkpoints,
        Action<long, long>? verificationProgress,
        CancellationToken cancellationToken,
        Dictionary<string, FileStream> retainedArtifactLeases,
        bool verifyArtifacts
    )
    {
        if (!verifyArtifacts)
        {
            foreach (var artifact in checkpoints.SelectMany(item => item.Record.Artifacts))
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
                AnalysisResumeCore.RejectHardLinkedFile(
                    stream,
                    "derived analysis resume artifact"
                );
            }
            return;
        }
        long totalBytes;
        try
        {
            totalBytes = checkpoints
                .SelectMany(item => item.Record.Artifacts)
                .Aggregate(0L, (total, artifact) => checked(total + artifact.Bytes));
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "Derived committed artifact sizes exceed the resume verification limit.",
                ex
            );
        }
        long completedBytes = 0;
        foreach (var artifact in checkpoints.SelectMany(item => item.Record.Artifacts))
        {
            var fullPath = AnalysisResumeCore.ResolveArtifactPath(
                outputFullPath,
                artifact.Path
            );
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length != artifact.Bytes)
            {
                throw new InvalidDataException(
                    $"Committed artifact '{artifact.Path}' changed size or is missing."
                );
            }
            var progressBase = completedBytes;
            var actual = await AnalysisResumeCore.HashArtifactWithOptionalLeaseAsync(
                artifact.Path,
                fullPath,
                retainedArtifactLeases,
                verificationProgress is null
                    ? null
                    : (completed, _) =>
                        verificationProgress(progressBase + completed, totalBytes),
                cancellationToken
            );
            if (!AnalysisResumeCore.FixedEquals(actual, artifact.Sha256))
            {
                throw new InvalidDataException(
                    $"Committed artifact '{artifact.Path}' failed SHA-256 validation."
                );
            }
            completedBytes = checked(completedBytes + artifact.Bytes);
        }
    }

    private static AnalysisResumeDerivedOwnerRecord ReadAndValidateDerivedOwner(
        string outputFullPath
    )
    {
        var derived = AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
            DerivedOwnerPath(outputFullPath)
        );
        ValidateDerivedOwner(outputFullPath, derived);
        return derived;
    }

    private static AnalysisResumeTranslationOffJournalRecord ReadAndValidateJournal(
        string outputFullPath
    )
    {
        var journal = AnalysisResumeCore.ReadStrictJson<AnalysisResumeTranslationOffJournalRecord>(
            JournalPath(outputFullPath)
        );
        ValidateJournal(outputFullPath, journal);
        return journal;
    }

    private static void ValidateJournal(
        string outputFullPath,
        AnalysisResumeTranslationOffJournalRecord journal
    )
    {
        if (
            journal is null
            || journal.SchemaVersion != 2
            || journal.RecordType != JournalRecordType
            || journal.DerivedOwner is null
        )
        {
            throw new InvalidDataException("Translation-off transition journal is invalid.");
        }
        ValidateDerivedOwner(outputFullPath, journal.DerivedOwner);
    }

    private static void ValidateDerivedOwner(
        string outputFullPath,
        AnalysisResumeDerivedOwnerRecord derived
    )
    {
        if (
            derived.Specification is null
            || derived.ParentAttempts is null
            || derived.InheritedCheckpoints is null
            || derived.SupersededCheckpoint is null
            || derived.ArchiveMoves is null
            || derived.Transition is null
            || derived.SupersededCheckpoint.Artifacts is null
            || derived.Transition.ExcludedEngines is null
            || derived.Transition.SupersededStages is null
            || derived.Transition.SourceAttempts is null
        )
        {
            throw new InvalidDataException(
                "Derived translation-off resume ownership is incomplete."
            );
        }
        AnalysisResumeCore.ValidateSpecification(derived.Specification);
        AnalysisResumeCore.RequireSpecificationOutputDirectory(
            derived.Specification,
            outputFullPath
        );
        if (
            derived.SchemaVersion != 2
            || derived.RecordType != DerivedOwnerRecordType
            || !Guid.TryParseExact(derived.RunId, "N", out _)
            || !Guid.TryParseExact(derived.ParentRunId, "N", out _)
            || derived.RunId == derived.ParentRunId
            || !Guid.TryParseExact(derived.GenerationId, "N", out _)
            || !Guid.TryParseExact(derived.TransitionId, "N", out _)
            || derived.CreatedUtc == default
            || !AnalysisResumeCore.IsLowerSha256(derived.ParentOwnerSha256)
            || !AnalysisResumeCore.IsLowerSha256(derived.ParentSpecificationSha256)
            || derived.Specification.Options.TranslationMode != TranslationWorkflowMode.Off
            || derived.InheritedCheckpoints.Count != AnalysisResumeStage.RawMerge.Ordinal
            || derived.SupersededCheckpoint.Ordinal
                != AnalysisResumeStage.TranslationSelection.Ordinal
            || derived.SupersededCheckpoint.StageId
                != AnalysisResumeStage.TranslationSelection.Id
            || !AnalysisResumeCore.IsLowerSha256(derived.SupersededCheckpoint.Sha256)
            || derived.SupersededCheckpoint.Artifacts.Count != 2
            || derived.SupersededCheckpoint.Artifacts[0].Path
                != "language-assessments.jsonl"
            || derived.SupersededCheckpoint.Artifacts[1].Path
                != "translation-candidates.jsonl"
            || derived.Transition.State
                != $"{AnalysisResumeCore.ResumeDirectoryName}/{DerivedOwnerFileName}"
            || derived.Transition.GenerationId != derived.GenerationId
            || derived.Transition.TransitionId != derived.TransitionId
            || derived.Transition.ParentRunId != derived.ParentRunId
            || derived.Transition.Kind != TransitionKind
            || derived.Transition.ExcludedEngines.Count != 1
            || derived.Transition.ExcludedEngines[0] != TranslationEngine
            || derived.Transition.FromTranslationMode != FromMode
            || derived.Transition.ToTranslationMode != ToMode
            || derived.Transition.InheritedThroughStage != AnalysisResumeStage.RawMerge.Id
            || derived.Transition.SupersededStages.Count != 1
            || derived.Transition.SupersededStages[0]
                != AnalysisResumeStage.TranslationSelection.Id
        )
        {
            throw new InvalidDataException("Derived translation-off resume ownership is invalid.");
        }
        if (derived.ParentAttempts.Count is 0 or > 20_000)
        {
            throw new InvalidDataException(
                "Derived translation-off parent attempt history exceeds the supported limit."
            );
        }
        string? previousAttemptName = null;
        foreach (var reference in derived.ParentAttempts)
        {
            if (
                reference is null
                || !AnalysisResumeCore.TryParseAttemptFileName(
                    reference.FileName,
                    out _,
                    out _
                )
                || reference.Bytes < 0
                || !AnalysisResumeCore.IsLowerSha256(reference.Sha256)
                || previousAttemptName is not null
                    && string.CompareOrdinal(previousAttemptName, reference.FileName) >= 0
            )
            {
                throw new InvalidDataException(
                    "Derived translation-off parent attempt reference is invalid."
                );
            }
            previousAttemptName = reference.FileName;
        }
        for (var index = 0; index < derived.InheritedCheckpoints.Count; index++)
        {
            var expected = AnalysisResumeStage.All[index];
            var actual = derived.InheritedCheckpoints[index];
            if (
                actual.Ordinal != expected.Ordinal
                || actual.StageId != expected.Id
                || !AnalysisResumeCore.IsLowerSha256(actual.Sha256)
            )
            {
                throw new InvalidDataException(
                    "Derived translation-off checkpoint inheritance is invalid."
                );
            }
        }
        var expectedArchive = $"logs/{ArchivePrefix}{derived.TransitionId}/superseded";
        if (derived.Transition.ArchiveRoot != expectedArchive)
        {
            throw new InvalidDataException("Derived translation-off archive root is invalid.");
        }
        RequireUniqueMoves(derived.ArchiveMoves);
        if (
            derived.Transition.AbandonedModelWork
                != derived.ArchiveMoves.Any(move =>
                    move.Kind == "abandoned-translation-work"
                )
            || derived.Transition.SourceAttempts.Count != derived.ParentAttempts.Count
            || derived.Transition.SourceAttempts.Where((reference, index) =>
                    reference != derived.ParentAttempts[index]
                )
                .Any()
        )
        {
            throw new InvalidDataException(
                "Derived translation-off source attempt evidence is inconsistent."
            );
        }
        foreach (var move in derived.ArchiveMoves)
        {
            ValidateMove(outputFullPath, derived, move);
        }
        foreach (
            var expectedSource in new[]
            {
                "language-assessments.jsonl",
                "translation-candidates.jsonl",
                $"{AnalysisResumeCore.ResumeDirectoryName}/{AnalysisResumeCore.CheckpointsDirectoryName}/{AnalysisResumeCore.CheckpointFileName(AnalysisResumeStage.TranslationSelection)}",
            }
        )
        {
            if (
                !derived.ArchiveMoves.Any(move =>
                    string.Equals(
                        move.SourcePath,
                        expectedSource,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            )
            {
                throw new InvalidDataException(
                    "Derived translation-off archive omits a superseded stage file."
                );
            }
        }
    }

    private static void ValidateMove(
        string outputFullPath,
        AnalysisResumeDerivedOwnerRecord derived,
        AnalysisResumeArchivedMove move
    )
    {
        if (
            move.Bytes < 0
            || !AnalysisResumeCore.IsLowerSha256(move.Sha256)
            || move.Kind
                is not (
                    "superseded-stage-artifact"
                    or "superseded-checkpoint"
                    or "abandoned-translation-work"
                )
            || !move.DestinationPath.StartsWith(
                derived.Transition.ArchiveRoot + "/",
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException("Derived translation-off archive move is invalid.");
        }
        _ = ResolveOwnedRelativePath(outputFullPath, move.SourcePath);
        _ = ResolveOwnedRelativePath(outputFullPath, move.DestinationPath);
        var sourceName = Path.GetFileName(move.SourcePath);
        var validSource = move.Kind switch
        {
            "superseded-stage-artifact" => move.SourcePath == sourceName
                && sourceName
                    is "language-assessments.jsonl" or "translation-candidates.jsonl",
            "superseded-checkpoint" => move.SourcePath
                == $"{AnalysisResumeCore.ResumeDirectoryName}/{AnalysisResumeCore.CheckpointsDirectoryName}/{AnalysisResumeCore.CheckpointFileName(AnalysisResumeStage.TranslationSelection)}",
            _ => move.SourcePath == sourceName
                && IsAbandonedTranslationArtifactName(sourceName),
        };
        var expectedDestination = move.Kind switch
        {
            "superseded-stage-artifact" =>
                $"{derived.Transition.ArchiveRoot}/artifacts/{sourceName}",
            "superseded-checkpoint" =>
                $"{derived.Transition.ArchiveRoot}/checkpoints/{sourceName}",
            _ => $"{derived.Transition.ArchiveRoot}/abandoned/{sourceName}",
        };
        if (
            !validSource
            || !string.Equals(
                move.DestinationPath,
                expectedDestination,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidDataException(
                "Derived translation-off archive move has an unsupported source."
            );
        }
    }

    private static void ValidateParentIdentity(
        string outputFullPath,
        AnalysisResumeCore.OwnerRecord owner,
        AnalysisResumeDerivedOwnerRecord derived
    )
    {
        if (!string.Equals(owner.RunId, derived.ParentRunId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Derived resume belongs to a different parent run.");
        }
        var ownerPath = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.OwnerFileName
        );
        var ownerSha256 = AnalysisResumeCore
            .HashFileAsync(ownerPath, null, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (
            !AnalysisResumeCore.FixedEquals(ownerSha256, derived.ParentOwnerSha256)
            || !AnalysisResumeCore.FixedEquals(
                HashSpecification(owner.Specification),
                derived.ParentSpecificationSha256
            )
        )
        {
            throw new InvalidDataException("Derived resume parent ownership changed.");
        }
        ValidateSpecificationTransition(owner.Specification, derived.Specification);
    }

    private static void ValidateSpecificationTransition(
        AnalysisResumeSpecification parent,
        AnalysisResumeSpecification target
    )
    {
        if (
            parent.SchemaVersion != 1
            || target.SchemaVersion != 1
            || parent.PipelineContractVersion != 1
            || target.PipelineContractVersion != 1
            || !string.Equals(parent.BstringsVersion, "2.1.1", StringComparison.Ordinal)
            || !string.Equals(target.BstringsVersion, "2.1.2", StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                "Translation-off resume requires an exact bstrings 2.1.1 schema-1 source and bstrings 2.1.2 schema-1 target."
            );
        }
        if (
            !parent.Options.Full
            || parent.Options.TranslationMode != TranslationWorkflowMode.Auto
            || target.Options.TranslationMode != TranslationWorkflowMode.Off
        )
        {
            throw new InvalidDataException(
                "Translation-off resume requires a Full Auto source and an Off target."
            );
        }
        if (
            AnalysisResumeCore.FixedEquals(
                parent.ExecutableSha256,
                target.ExecutableSha256
            )
            || AnalysisResumeCore.FixedEquals(
                parent.ManagedAssemblySha256,
                target.ManagedAssemblySha256
            )
        )
        {
            throw new InvalidDataException(
                "Translation-off resume requires distinct verified 2.1.1 source and 2.1.2 target binary identities."
            );
        }
        if (
            !string.Equals(
                AnalysisResumeCore.CanonicalizeOutputDirectory(parent.OutputDirectory),
                AnalysisResumeCore.CanonicalizeOutputDirectory(target.OutputDirectory),
                StringComparison.OrdinalIgnoreCase
            )
            || parent.PatternCount != target.PatternCount
            || !AnalysisResumeCore.FixedEquals(parent.PatternSha256, target.PatternSha256)
        )
        {
            throw new InvalidDataException(
                "Translation-off resume cannot change the output or pattern contract."
            );
        }
        var expectedOptions = parent.Options with
        {
            TranslationMode = TranslationWorkflowMode.Off,
        };
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(
            expectedOptions,
            AnalysisResumeCore.JsonOptions
        );
        var targetBytes = JsonSerializer.SerializeToUtf8Bytes(
            target.Options,
            AnalysisResumeCore.JsonOptions
        );
        if (!expectedBytes.AsSpan().SequenceEqual(targetBytes))
        {
            throw new InvalidDataException(
                "Translation-off resume cannot change any other saved analysis option."
            );
        }
    }

    private static void ValidateDerivedMetadataLayout(string outputFullPath)
    {
        var resumeDirectory = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName
        );
        AnalysisOrchestrator.EnsureNoReparsePoints(
            resumeDirectory,
            "derived analysis resume metadata directory"
        );
        var expected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            [AnalysisResumeCore.OwnerFileName] = false,
            [DerivedOwnerFileName] = false,
            [AnalysisResumeCore.CheckpointsDirectoryName] = true,
            [AnalysisResumeCore.AttemptsDirectoryName] = true,
            [AnalysisResumeCore.PendingDirectoryName] = true,
        };
        var entries = Directory
            .EnumerateFileSystemEntries(resumeDirectory, "*", SearchOption.TopDirectoryOnly)
            .ToArray();
        if (entries.Length != expected.Count)
        {
            throw new InvalidDataException(
                "Derived analysis resume metadata contains an unexpected or missing entry."
            );
        }
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (name is null || !expected.TryGetValue(name, out var mustBeDirectory))
            {
                throw new InvalidDataException(
                    "Derived analysis resume metadata contains an unexpected entry."
                );
            }
            AnalysisOrchestrator.EnsureNoReparsePoints(
                entry,
                "derived analysis resume metadata entry"
            );
            if (mustBeDirectory ? !Directory.Exists(entry) : !File.Exists(entry))
            {
                throw new InvalidDataException(
                    $"Derived analysis resume metadata entry '{name}' has the wrong file type."
                );
            }
        }
    }

    private static async Task ValidateDerivedAttemptRecordsAsync(
        string outputFullPath,
        AnalysisResumeCore.OwnerRecord parentOwner,
        AnalysisResumeDerivedOwnerRecord derived,
        CancellationToken cancellationToken
    )
    {
        var attemptsDirectory = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.AttemptsDirectoryName
        );
        var records = new Dictionary<
            string,
            (AnalysisResumeCore.AttemptRecord? Start, AnalysisResumeCore.AttemptRecord? Outcome)
        >(StringComparer.Ordinal);
        var activeOwner = new AnalysisResumeCore.OwnerRecord(
            1,
            "analysis-resume-owner",
            derived.RunId,
            derived.CreatedUtc,
            derived.Specification,
            parentOwner.LegacyImported
        );
        var entries = Directory
            .EnumerateFileSystemEntries(
                attemptsDirectory,
                "*",
                SearchOption.TopDirectoryOnly
            )
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length > 20_000)
        {
            throw new InvalidDataException(
                "Derived analysis attempt history exceeds the supported file limit."
            );
        }
        foreach (var entry in entries)
        {
            if (!File.Exists(entry))
            {
                throw new InvalidDataException(
                    "Derived analysis attempt history contains an unexpected directory."
                );
            }
            if (
                !AnalysisResumeCore.TryParseAttemptFileName(
                    Path.GetFileName(entry),
                    out var attemptId,
                    out var isStart
                )
            )
            {
                throw new InvalidDataException(
                    "Derived analysis attempt history contains an unexpected file."
                );
            }
            var record = AnalysisResumeCore.ReadStrictJson<AnalysisResumeCore.AttemptRecord>(
                entry
            );
            var owner = record.RunId == parentOwner.RunId
                ? parentOwner
                : record.RunId == derived.RunId
                    ? activeOwner
                    : throw new InvalidDataException(
                        $"Derived analysis attempt '{attemptId}' belongs to an unknown generation."
                    );
            AnalysisResumeCore.ValidateAttemptRecord(record, owner, attemptId, isStart);
            records.TryGetValue(attemptId, out var pair);
            if (isStart)
            {
                if (pair.Start is not null)
                {
                    throw new InvalidDataException(
                        "Derived analysis attempt history repeats a start record."
                    );
                }
                pair.Start = record;
            }
            else
            {
                if (pair.Outcome is not null)
                {
                    throw new InvalidDataException(
                        "Derived analysis attempt history repeats an outcome record."
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
                    $"Derived analysis attempt '{attemptId}' has no start record."
                );
            }
            if (
                pair.Outcome is { } outcome
                && (
                    outcome.StartedUtc != pair.Start.StartedUtc
                    || !string.Equals(outcome.Mode, pair.Start.Mode, StringComparison.Ordinal)
                    || AnalysisResumeCore.StageOrdinal(outcome.LastCommittedStage)
                        < AnalysisResumeCore.StageOrdinal(pair.Start.LastCommittedStage)
                )
            )
            {
                throw new InvalidDataException(
                    $"Derived analysis attempt '{attemptId}' has inconsistent start and outcome records."
                );
            }
        }
        await VerifyParentAttemptReferencesAsync(
            outputFullPath,
            derived,
            allowDerivedAttempts: true,
            cancellationToken: cancellationToken
        );
    }

    private static async Task<IReadOnlyList<AnalysisResumeParentAttemptReference>>
        CreateParentAttemptReferencesAsync(
            string outputFullPath,
            CancellationToken cancellationToken
        )
    {
        var attemptsDirectory = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.AttemptsDirectoryName
        );
        var references = new List<AnalysisResumeParentAttemptReference>();
        foreach (
            var path in Directory
                .EnumerateFiles(attemptsDirectory, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
        )
        {
            var name = Path.GetFileName(path);
            if (
                !AnalysisResumeCore.TryParseAttemptFileName(
                    name,
                    out _,
                    out _
                )
            )
            {
                throw new InvalidDataException(
                    "Parent attempt history contains an unexpected file."
                );
            }
            references.Add(
                new AnalysisResumeParentAttemptReference(
                    name,
                    new FileInfo(path).Length,
                    await AnalysisResumeCore.HashFileAsync(
                        path,
                        null,
                        cancellationToken
                    )
                )
            );
        }
        return references;
    }

    private static async Task VerifyParentAttemptReferencesAsync(
        string outputFullPath,
        AnalysisResumeDerivedOwnerRecord derived,
        bool allowDerivedAttempts,
        CancellationToken cancellationToken
    )
    {
        var attemptsDirectory = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.AttemptsDirectoryName
        );
        var expected = derived.ParentAttempts.ToDictionary(
            reference => reference.FileName,
            reference => reference,
            StringComparer.Ordinal
        );
        var entries = Directory
            .EnumerateFileSystemEntries(
                attemptsDirectory,
                "*",
                SearchOption.TopDirectoryOnly
            )
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (entries.Length > 20_000)
        {
            throw new InvalidDataException(
                "Derived analysis attempt history exceeds the supported file limit."
            );
        }
        foreach (var path in entries)
        {
            var name = Path.GetFileName(path);
            if (!File.Exists(path) || name is null)
            {
                throw new InvalidDataException(
                    "Derived analysis attempt history contains an unexpected entry."
                );
            }
            if (expected.TryGetValue(name, out var reference))
            {
                var info = new FileInfo(path);
                if (info.Length != reference.Bytes)
                {
                    throw new InvalidDataException(
                        $"Parent attempt metadata '{name}' changed size."
                    );
                }
                var sha256 = await AnalysisResumeCore.HashFileAsync(
                    path,
                    null,
                    cancellationToken
                );
                if (!AnalysisResumeCore.FixedEquals(sha256, reference.Sha256))
                {
                    throw new InvalidDataException(
                        $"Parent attempt metadata '{name}' failed SHA-256 validation."
                    );
                }
                expected.Remove(name);
                continue;
            }
            if (!allowDerivedAttempts)
            {
                throw new InvalidDataException(
                    $"Parent attempt metadata '{name}' was not recorded by the transition journal."
                );
            }
            var record = AnalysisResumeCore.ReadStrictJson<AnalysisResumeCore.AttemptRecord>(
                path
            );
            if (!string.Equals(record.RunId, derived.RunId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Attempt metadata '{name}' does not belong to a recorded generation."
                );
            }
        }
        if (expected.Count != 0)
        {
            throw new InvalidDataException(
                "Derived analysis is missing recorded parent attempt metadata."
            );
        }
    }

    private static void RequireEmptyTransitionPendingDirectory(string outputFullPath)
    {
        var pendingDirectory = Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.PendingDirectoryName
        );
        AnalysisOrchestrator.EnsureNoReparsePoints(
            pendingDirectory,
            "translation-off transition pending directory"
        );
        var entries = Directory
            .EnumerateFileSystemEntries(
                pendingDirectory,
                "*",
                SearchOption.TopDirectoryOnly
            )
            .ToArray();
        if (
            entries.Length > 1
            || entries.Length == 1
                && !string.Equals(
                    Path.GetFileName(entries[0]),
                    JournalPendingFileName,
                    StringComparison.Ordinal
                )
        )
        {
            throw new InvalidDataException(
                "Translation-off transition requires empty pending metadata. No files were changed."
            );
        }
        if (entries.Length == 1)
        {
            ValidateOwnedInterruptedPublication(
                entries[0],
                "interrupted translation-off journal publication"
            );
        }
    }

    private static async Task PublishJournalAsync(
        string outputFullPath,
        AnalysisResumeTranslationOffJournalRecord journal,
        Func<
            AnalysisResumeTranslationOffFaultPoint,
            int,
            CancellationToken,
            Task
        >? faultInjector,
        CancellationToken cancellationToken
    )
    {
        var pendingPath = JournalPendingPath(outputFullPath);
        RemoveOwnedInterruptedPublication(
            pendingPath,
            "interrupted translation-off journal publication"
        );
        await AnalysisResumeCore.WriteNewJsonAsync(
            pendingPath,
            journal,
            CancellationToken.None
        );
        await InvokeFaultAsync(
            faultInjector,
            AnalysisResumeTranslationOffFaultPoint.AfterJournalPendingPublished,
            -1,
            cancellationToken
        );
        File.Move(pendingPath, JournalPath(outputFullPath), overwrite: false);
    }

    private static void RemoveOwnedInterruptedDerivedPublication(string pendingPath)
    {
        RemoveOwnedInterruptedPublication(
            pendingPath,
            "interrupted derived owner publication"
        );
    }

    private static void RemoveOwnedInterruptedPublication(
        string pendingPath,
        string description
    )
    {
        if (!File.Exists(pendingPath))
        {
            return;
        }
        ValidateOwnedInterruptedPublication(pendingPath, description);
        File.Delete(pendingPath);
    }

    private static void ValidateOwnedInterruptedPublication(
        string pendingPath,
        string description
    )
    {
        AnalysisOrchestrator.EnsureNoReparsePoints(pendingPath, description);
        using (
            var stream = new FileStream(
                pendingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.SequentialScan
            )
        )
        {
            AnalysisResumeCore.RejectHardLinkedFile(
                stream,
                description
            );
        }
    }

    private static void ValidateTransitionRootEntries(
        string outputFullPath,
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)> checkpoints
    )
    {
        var committed = checkpoints
            .SelectMany(item => item.Record.Artifacts)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedFiles = new HashSet<string>(committed, StringComparer.OrdinalIgnoreCase)
        {
            ".incomplete",
            AnalysisResumeCore.LockFileName,
            "run.json",
        };
        foreach (var path in Directory.EnumerateFileSystemEntries(outputFullPath))
        {
            var name = Path.GetFileName(path);
            if (
                string.Equals(
                    name,
                    AnalysisResumeCore.ResumeDirectoryName,
                    StringComparison.OrdinalIgnoreCase
                )
                || string.Equals(name, "logs", StringComparison.OrdinalIgnoreCase)
            )
            {
                if (!Directory.Exists(path))
                {
                    throw new InvalidDataException(
                        $"Transition-owned directory '{name}' has the wrong file type."
                    );
                }
                AnalysisOrchestrator.EnsureNoReparsePoints(
                    path,
                    "translation-off transition directory"
                );
                continue;
            }
            if (
                name is not null
                && (allowedFiles.Contains(name) || IsAbandonedTranslationArtifactName(name))
            )
            {
                if (!File.Exists(path) || Directory.Exists(path))
                {
                    throw new InvalidDataException(
                        $"Transition-owned artifact '{name}' has the wrong file type."
                    );
                }
                AnalysisOrchestrator.EnsureNoReparsePoints(
                    path,
                    "translation-off transition artifact"
                );
                continue;
            }
            throw new InvalidDataException(
                $"Translation-off transition found unrecognized result entry '{name}'. No files were changed."
            );
        }
    }

    private static IEnumerable<string> EnumerateAbandonedTranslationArtifacts(
        string outputFullPath
    )
    {
        foreach (
            var path in Directory
                .EnumerateFiles(outputFullPath, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        )
        {
            var name = Path.GetFileName(path);
            if (IsAbandonedTranslationArtifactName(name))
            {
                yield return name;
            }
        }
    }

    private static bool IsAbandonedTranslationArtifactName(string? name)
    {
        if (name is null)
        {
            return false;
        }
        if (name is "translated-strings.jsonl" or "translation-work-stats.json")
        {
            return true;
        }
        foreach (
            var prefix in new[]
            {
                "translated-strings.jsonl.partial.",
                "translated-strings.jsonl.backup.",
                "translation-work-stats.json.partial.",
                "translation-work-stats.json.backup.",
            }
        )
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return IsOwnedToken(name[prefix.Length..]);
            }
        }
        var databaseName = name;
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            if (databaseName.EndsWith(suffix, StringComparison.Ordinal))
            {
                databaseName = databaseName[..^suffix.Length];
                break;
            }
        }
        const string cachePrefix = ".bstrings-translation-cache-";
        const string cacheSuffix = ".sqlite3";
        return databaseName.StartsWith(cachePrefix, StringComparison.Ordinal)
            && databaseName.EndsWith(cacheSuffix, StringComparison.Ordinal)
            && IsShortOwnedToken(
                databaseName[cachePrefix.Length..^cacheSuffix.Length]
            );
    }

    private static bool IsOwnedToken(string value) =>
        Guid.TryParseExact(value, "N", out _) || IsShortOwnedToken(value);

    private static bool IsShortOwnedToken(string value) =>
        value.Length == 8
        && value.All(character =>
            character is >= 'a' and <= 'z'
            || character is >= '0' and <= '9'
            || character == '_'
        );

    private static string ResolveOwnedRelativePath(
        string outputFullPath,
        string relativePath
    )
    {
        if (
            string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || relativePath.Contains('\\')
            || relativePath.IndexOfAny(['\0', '\r', '\n']) >= 0
        )
        {
            throw new InvalidDataException("Translation-off archive path is unsafe.");
        }
        var fullPath = Path.GetFullPath(
            Path.Combine(outputFullPath, relativePath.Replace('/', Path.DirectorySeparatorChar))
        );
        var prefix = outputFullPath.EndsWith(Path.DirectorySeparatorChar)
            ? outputFullPath
            : outputFullPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Translation-off archive path escapes the result directory.");
        }
        return fullPath;
    }

    private static void RequireUniqueMoves(IReadOnlyList<AnalysisResumeArchivedMove> moves)
    {
        if (moves.Count < 3)
        {
            throw new InvalidDataException(
                "Translation-off transition is missing its superseded stage archive."
            );
        }
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var move in moves)
        {
            if (!sources.Add(move.SourcePath) || !destinations.Add(move.DestinationPath))
            {
                throw new InvalidDataException(
                    "Translation-off transition repeats an archive path."
                );
            }
        }
    }

    private static string HashSpecification(AnalysisResumeSpecification specification)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            specification,
            AnalysisResumeCore.JsonOptions
        );
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
    }

    private static void RequireEqualDerivedOwner(
        AnalysisResumeDerivedOwnerRecord expected,
        AnalysisResumeDerivedOwnerRecord actual
    )
    {
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(
            expected,
            AnalysisResumeCore.JsonOptions
        );
        var actualBytes = JsonSerializer.SerializeToUtf8Bytes(
            actual,
            AnalysisResumeCore.JsonOptions
        );
        if (!expectedBytes.AsSpan().SequenceEqual(actualBytes))
        {
            throw new InvalidDataException(
                "Published derived translation-off owner does not match its journal."
            );
        }
    }

    private static Task InvokeFaultAsync(
        Func<
            AnalysisResumeTranslationOffFaultPoint,
            int,
            CancellationToken,
            Task
        >? faultInjector,
        AnalysisResumeTranslationOffFaultPoint point,
        int moveIndex,
        CancellationToken cancellationToken
    ) => faultInjector?.Invoke(point, moveIndex, cancellationToken) ?? Task.CompletedTask;

    private static string DerivedOwnerPath(string outputFullPath) =>
        Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            DerivedOwnerFileName
        );

    private static string JournalPath(string outputFullPath) =>
        Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.PendingDirectoryName,
            JournalFileName
        );

    private static string JournalPendingPath(string outputFullPath) =>
        Path.Combine(
            outputFullPath,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.PendingDirectoryName,
            JournalPendingFileName
        );

    private sealed record DerivedCheckpointValidation(
        IReadOnlyList<(AnalysisResumeCore.CheckpointRecord Record, string Sha256)> Checkpoints,
        string NextDependencySha256
    );
}
