using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisResumeTranslationOffAdversarialTests
{
    public static IEnumerable<object[]> AcceptedResumeExclusionOrders =>
    [
        [new[] { "-r", "-o", "{output}", "-e", "translation" }],
        [new[] { "-e", "translation", "-o", "{output}", "--resume" }],
        [new[] { "--resume", "--exclude-engine", "Translation", "-o", "{output}" }],
    ];

    [Theory]
    [MemberData(nameof(AcceptedResumeExclusionOrders))]
    public async Task Cli_ResumeTranslationExclusion_IsOrderIndependentAndReachesResume(
        string[] argumentTemplate
    )
    {
        using var scope = new TemporaryScope();
        var missingOutput = scope.PathFor("missing-output");
        var arguments = argumentTemplate
            .Select(value => value == "{output}" ? missingOutput : value)
            .ToArray();

        // Parser failures return 1. Reaching resume and rejecting the missing result returns 2.
        Assert.Equal(2, await AnalysisCli.RunAsync(arguments));
        Assert.False(Directory.Exists(missingOutput));
    }

    public static IEnumerable<object[]> RefusedResumeExclusionGrammar =>
    [
        [new[] { "-e", "translate" }],
        [new[] { "-e", "translation,native" }],
        [new[] { "-e", "translation", "-e", "ocr" }],
        [new[] { "-e", "translation", "-e", "TRANSLATION" }],
        [new[] { "-e", "translation,translation" }],
        [new[] { "--translation", "off" }],
        [new[] { "--full", "-e", "translation" }],
        [new[] { "-e", "translation", "--translation", "off" }],
    ];

    [Theory]
    [MemberData(nameof(RefusedResumeExclusionGrammar))]
    public async Task Cli_ResumeTranslationExclusion_RejectsAliasesDuplicatesOtherEnginesAndSelectors(
        string[] exclusionArguments
    )
    {
        using var scope = new TemporaryScope();
        var output = scope.PathFor("missing-output");
        var arguments = new[] { "-r", "-o", output }.Concat(exclusionArguments).ToArray();

        Assert.Equal(1, await AnalysisCli.RunAsync(arguments));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void TranslationExclusion_HasOneMeaning_Off()
    {
        var modes = AnalysisCli.ResolveEngineModes(
            full: true,
            nativeValue: null,
            flossValue: null,
            ocrValue: null,
            translationValue: null,
            rawExclusions: ["translation"]
        );

        Assert.Equal(TranslationWorkflowMode.Off, modes.Translation);
        Assert.NotEqual(TranslationWorkflowMode.DetectOnly, modes.Translation);
    }

    [Fact]
    public async Task LegacyImportedParent_AcceptsNativeOriginFromVersionTwoZeroZero()
    {
        using var scope = new TemporaryScope();
        var runnable = await CreateRunnableSourceAsync(scope);
        var nativePath = Path.Combine(runnable.Output, "native-strings.jsonl");
        var native = await File.ReadAllTextAsync(
            nativePath,
            TestContext.Current.CancellationToken
        );
        using var firstRecord = JsonDocument.Parse(native.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);
        var recordedVersion = firstRecord.RootElement
            .GetProperty("origin")
            .GetProperty("version")
            .GetString()!;
        Assert.Equal("2.1.2", recordedVersion);
        native = native.Replace(
            $"\"version\":{JsonSerializer.Serialize(recordedVersion)}",
            $"\"version\":{JsonSerializer.Serialize("2.0.0")}",
            StringComparison.Ordinal
        );
        await File.WriteAllTextAsync(
            nativePath,
            native,
            TestContext.Current.CancellationToken
        );

        // Stop validation immediately after native provenance. Before the legacy-version fix,
        // the same fixture fails one stage earlier with "invalid native origin provenance".
        File.Delete(Path.Combine(runnable.Output, "recovered-strings.jsonl"));
        var checkpoints = AnalysisResumeStage.All
            .Take(AnalysisResumeStage.TranslationSelection.Ordinal)
            .Select(stage =>
            {
                var checkpointPath = CheckpointPath(runnable.Output, stage);
                return (
                    AnalysisResumeCore.ReadStrictJson<AnalysisResumeCore.CheckpointRecord>(
                        checkpointPath
                    ),
                    Hash(File.ReadAllBytes(checkpointPath))
                );
            })
            .ToArray();
        var context = new AnalysisResumeTranslationOffValidationContext(
            runnable.Output,
            runnable.Parent,
            legacyImported: true,
            checkpoints
        );

        var error = await Record.ExceptionAsync(() =>
            AnalysisOrchestrator.ValidateTranslationOffParentAsync(
                context,
                runnable.Options with { TranslationMode = TranslationWorkflowMode.Auto },
                sourceToolchain: null!,
                TestContext.Current.CancellationToken
            )
        );

        Assert.NotNull(error);
        Assert.DoesNotContain("native origin provenance", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovered string output", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transition_ArchivesSourceStageSevenAndRecordsTrueInheritance()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var ownerBefore = File.ReadAllBytes(OwnerPath(source.Output));
        var inheritedBefore = AnalysisResumeStage.All
            .Take(6)
            .ToDictionary(stage => stage, stage => File.ReadAllBytes(CheckpointPath(source.Output, stage)));
        var inheritedHashes = inheritedBefore.ToDictionary(
            pair => pair.Key,
            pair => Hash(pair.Value)
        );
        var stageSevenBefore = File.ReadAllBytes(
            CheckpointPath(source.Output, AnalysisResumeStage.TranslationSelection)
        );
        var assessmentsBefore = File.ReadAllBytes(
            Path.Combine(source.Output, "language-assessments.jsonl")
        );
        var candidatesBefore = File.ReadAllBytes(
            Path.Combine(source.Output, "translation-candidates.jsonl")
        );
        var attemptsDirectory = Path.Combine(
            source.Output,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.AttemptsDirectoryName
        );
        var attemptsBefore = Directory.EnumerateFiles(attemptsDirectory)
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);

        await using var target = await TransitionAsync(source);

        Assert.NotEqual(source.ParentRunId, target.RunId);
        Assert.Equal(ownerBefore, File.ReadAllBytes(OwnerPath(source.Output)));
        foreach (var pair in inheritedBefore)
        {
            Assert.Equal(pair.Value, File.ReadAllBytes(CheckpointPath(source.Output, pair.Key)));
            using var checkpoint = JsonDocument.Parse(pair.Value);
            Assert.Equal(source.ParentRunId, checkpoint.RootElement.GetProperty("runId").GetString());
        }
        Assert.False(
            File.Exists(CheckpointPath(source.Output, AnalysisResumeStage.TranslationSelection))
        );
        Assert.False(File.Exists(Path.Combine(source.Output, "language-assessments.jsonl")));
        Assert.False(File.Exists(Path.Combine(source.Output, "translation-candidates.jsonl")));

        var derived = AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
            DerivedOwnerPath(source.Output)
        );
        Assert.Equal(target.RunId, derived.RunId);
        Assert.Equal(AnalysisResumeStage.RawMerge.Id, derived.Transition.InheritedThroughStage);
        Assert.Equal([AnalysisResumeStage.TranslationSelection.Id], derived.Transition.SupersededStages);
        Assert.Equal(
            derived.ArchiveMoves.Any(move => move.Kind == "abandoned-translation-work"),
            derived.Transition.AbandonedModelWork
        );
        Assert.Equal(
            derived.ParentAttempts,
            derived.Transition.SourceAttempts
        );
        Assert.Equal(
            attemptsBefore.Keys.Order(StringComparer.Ordinal),
            derived.Transition.SourceAttempts.Select(reference => reference.FileName)
                .Order(StringComparer.Ordinal)
        );
        Assert.Equal(
            inheritedHashes.OrderBy(pair => pair.Key.Ordinal).Select(pair => pair.Value),
            derived.InheritedCheckpoints.Select(reference => reference.Sha256)
        );
        Assert.All(
            derived.ArchiveMoves,
            move =>
            {
                Assert.False(File.Exists(ResolveRelative(source.Output, move.SourcePath)));
                var archive = ResolveRelative(source.Output, move.DestinationPath);
                Assert.True(File.Exists(archive), $"Missing archive '{move.DestinationPath}'.");
                Assert.Equal(move.Sha256, Hash(File.ReadAllBytes(archive)));
            }
        );
        Assert.Contains(
            derived.ArchiveMoves,
            move => File.ReadAllBytes(ResolveRelative(source.Output, move.DestinationPath))
                .SequenceEqual(stageSevenBefore)
        );
        Assert.Contains(
            derived.ArchiveMoves,
            move => File.ReadAllBytes(ResolveRelative(source.Output, move.DestinationPath))
                .SequenceEqual(assessmentsBefore)
        );
        Assert.Contains(
            derived.ArchiveMoves,
            move => File.ReadAllBytes(ResolveRelative(source.Output, move.DestinationPath))
                .SequenceEqual(candidatesBefore)
        );
        foreach (var sourceAttempt in attemptsBefore)
        {
            Assert.Equal(
                sourceAttempt.Value,
                File.ReadAllBytes(Path.Combine(attemptsDirectory, sourceAttempt.Key!))
            );
        }
        var targetAttempt = Directory.EnumerateFiles(attemptsDirectory, "*-start.json")
            .Where(path => !attemptsBefore.ContainsKey(Path.GetFileName(path)))
            .Single();
        using var targetAttemptDocument = JsonDocument.Parse(File.ReadAllBytes(targetAttempt));
        Assert.Equal(
            target.RunId,
            targetAttemptDocument.RootElement.GetProperty("runId").GetString()
        );
    }

    [Fact]
    public async Task Transition_StageEightOrLaterRefusesWithoutChangingAnyByte()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 8);
        var before = SnapshotFiles(source.Output);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await TransitionAsync(source);
        });

        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(before, SnapshotFiles(source.Output));
    }

    [Fact]
    public async Task Transition_RejectsParentIdentityDriftBeforePublishingAJournal()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var driftedParent = source.Parent with { PatternSha256 = new string('e', 64) };
        var before = SnapshotFiles(source.Output);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                source.Output,
                driftedParent,
                source.Target,
                ValidateSyntheticParentAsync,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(before, SnapshotFiles(source.Output));
    }

    [Fact]
    public async Task Transition_RejectsAnyTargetOptionDriftBeforePublishingAJournal()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var driftedTarget = source.Target with
        {
            Options = source.Target.Options with { TranslationTarget = "fr" },
        };
        var before = SnapshotFiles(source.Output);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                source.Output,
                source.Parent,
                driftedTarget,
                ValidateSyntheticParentAsync,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(before, SnapshotFiles(source.Output));
    }

    [Fact]
    public async Task Transition_RejectsSameVersionOrUnchangedCompletionIdentityBeforeJournal()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var invalidTargets = new[]
        {
            source.Target with { BstringsVersion = source.Parent.BstringsVersion },
            source.Target with { ExecutableSha256 = source.Parent.ExecutableSha256 },
            source.Target with { ManagedAssemblySha256 = source.Parent.ManagedAssemblySha256 },
        };
        var before = SnapshotFiles(source.Output);

        foreach (var invalidTarget in invalidTargets)
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                    source.Output,
                    source.Parent,
                    invalidTarget,
                    ValidateSyntheticParentAsync,
                    cancellationToken: TestContext.Current.CancellationToken
                );
            });
            Assert.IsType<InvalidDataException>(error);
            Assert.Equal(before, SnapshotFiles(source.Output));
        }
    }

    public static IEnumerable<object[]> UnknownTranslationWorkNames =>
    [
        ["translated-strings.jsonl.partial.NOT_OWNED"],
        ["translation-work-stats.json.backup.1234567"],
        [".bstrings-translation-cache-short.sqlite3"],
        [".bstrings-translation-cache-abcdefgh.sqlite3-unknown"],
        ["language-assessments.jsonl.partial.NOT_OWNED"],
    ];

    [Theory]
    [MemberData(nameof(UnknownTranslationWorkNames))]
    public async Task Transition_UnknownPartialOrCacheLikeNameRefusesBeforeAnyMove(string name)
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        File.WriteAllText(Path.Combine(source.Output, name), "ambiguous analyst-controlled file");
        var before = SnapshotFiles(source.Output);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await TransitionAsync(source);
        });

        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(before, SnapshotFiles(source.Output));
    }

    [Fact]
    public async Task Transition_RejectsHardLinkedAbandonedWorkBeforeAnyMove()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The resume hard-link contract is Windows-specific.");
        }
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var partial = Path.Combine(source.Output, "translated-strings.jsonl.partial.abcdefgh");
        File.WriteAllText(partial, "uncommitted model output");
        var outside = scope.PathFor("outside-hardlink.bin");
        if (!CreateHardLink(outside, partial, IntPtr.Zero))
        {
            Assert.Skip($"Hard links are unavailable on this host: {Marshal.GetLastWin32Error()}.");
        }
        var before = SnapshotFiles(source.Output);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await TransitionAsync(source);
        });

        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(before, SnapshotFiles(source.Output));
        Assert.Equal("uncommitted model output", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Transition_RejectsReparseAbandonedWorkWithoutChangingItsTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The resume reparse contract is Windows-specific.");
        }
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var outside = scope.PathFor("outside-reparse-target.bin");
        File.WriteAllText(outside, "outside sentinel");
        var partial = Path.Combine(source.Output, "translated-strings.jsonl.partial.abcdefgh");
        try
        {
            File.CreateSymbolicLink(partial, outside);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
        )
        {
            Assert.Skip($"Symbolic links are unavailable on this host: {ex.Message}");
        }
        var before = SnapshotFiles(source.Output);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await TransitionAsync(source);
        });

        Assert.True(error is ArgumentException or InvalidDataException, error?.ToString());
        Assert.Equal(before, SnapshotFiles(source.Output));
        Assert.Equal("outside sentinel", File.ReadAllText(outside));
    }

    [Fact]
    public async Task Transition_ArchivesEveryKnownPartialCacheAndSidecarWithExactIdentity()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var abandoned = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["translated-strings.jsonl.partial.abcdefgh"] = "partial translation",
            ["translation-work-stats.json.partial.12345678"] = "partial stats",
            [".bstrings-translation-cache-abcdefgh.sqlite3"] = "cache",
            [".bstrings-translation-cache-abcdefgh.sqlite3-wal"] = "cache wal",
            [".bstrings-translation-cache-abcdefgh.sqlite3-shm"] = "cache shm",
        };
        foreach (var pair in abandoned)
        {
            File.WriteAllText(Path.Combine(source.Output, pair.Key), pair.Value);
        }

        await using var target = await TransitionAsync(source);
        var derived = AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
            DerivedOwnerPath(source.Output)
        );
        var archived = derived.ArchiveMoves
            .Where(move => move.Kind == "abandoned-translation-work")
            .ToDictionary(move => Path.GetFileName(move.SourcePath), StringComparer.Ordinal);

        Assert.True(derived.Transition.AbandonedModelWork);
        Assert.Equal(abandoned.Keys.Order(), archived.Keys.Order());
        foreach (var pair in abandoned)
        {
            Assert.False(File.Exists(Path.Combine(source.Output, pair.Key)));
            var move = archived[pair.Key];
            var destination = ResolveRelative(source.Output, move.DestinationPath);
            Assert.Equal(pair.Value, File.ReadAllText(destination));
            Assert.Equal(move.Sha256, Hash(File.ReadAllBytes(destination)));
        }
    }

    public static IEnumerable<object[]> TransitionFaultPoints =>
        Enum.GetValues<AnalysisResumeTranslationOffFaultPoint>()
            .Select(point => new object[] { (int)point });

    [Theory]
    [MemberData(nameof(TransitionFaultPoints))]
    public async Task Transition_AfterEveryJournalBoundary_ReopensToOneRecoverableGeneration(
        int requestedFaultValue
    )
    {
        var requestedFault = (AnalysisResumeTranslationOffFaultPoint)requestedFaultValue;
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var sourceBefore = SnapshotFiles(source.Output);
        var injected = false;

        var failure = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                source.Output,
                source.Parent,
                source.Target,
                ValidateSyntheticParentAsync,
                faultInjector: (point, moveIndex, _) =>
                {
                    if (
                        !injected
                        && point == requestedFault
                        && (point != AnalysisResumeTranslationOffFaultPoint.AfterArchiveMove || moveIndex == 0)
                    )
                    {
                        injected = true;
                        throw new IOException($"injected fault at {point}:{moveIndex}");
                    }
                    return Task.CompletedTask;
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        Assert.True(injected);
        Assert.IsType<IOException>(failure);
        if (requestedFault == AnalysisResumeTranslationOffFaultPoint.AfterJournalPendingPublished)
        {
            var sourceAfter = SnapshotFiles(source.Output);
            Assert.True(
                sourceAfter.Remove(
                    $"{AnalysisResumeCore.ResumeDirectoryName}/{AnalysisResumeCore.PendingDirectoryName}/{AnalysisResumeTranslationOffCore.JournalPendingFileName}"
                )
            );
            Assert.Equal(sourceBefore, sourceAfter);
        }
        await using var recovered =
            requestedFault == AnalysisResumeTranslationOffFaultPoint.AfterJournalPendingPublished
                ? await TransitionAsync(source)
                : await AnalysisResumeCore.OpenExistingAsync(
                    source.Output,
                    source.Target,
                    cancellationToken: TestContext.Current.CancellationToken
                );
        Assert.NotEqual(source.ParentRunId, recovered.RunId);
        Assert.True(File.Exists(DerivedOwnerPath(source.Output)));
        Assert.False(File.Exists(JournalPath(source.Output)));
        Assert.False(
            File.Exists(CheckpointPath(source.Output, AnalysisResumeStage.TranslationSelection))
        );
        var derived = AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
            DerivedOwnerPath(source.Output)
        );
        Assert.All(
            derived.ArchiveMoves,
            move => Assert.True(File.Exists(ResolveRelative(source.Output, move.DestinationPath)))
        );
        Assert.Equal(
            derived.ArchiveMoves.Count,
            derived.ArchiveMoves.Select(move => move.DestinationPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()
        );
    }

    [Fact]
    public async Task Transition_TruncatedPreActivationJournalIsDiscardedBeforeAValidatedRetry()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var sourceBefore = SnapshotFiles(source.Output);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                source.Output,
                source.Parent,
                source.Target,
                ValidateSyntheticParentAsync,
                faultInjector: (point, _, _) =>
                    point == AnalysisResumeTranslationOffFaultPoint.AfterJournalPendingPublished
                        ? throw new IOException("stop before journal activation")
                        : Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });
        var pendingPath = Path.Combine(
            source.Output,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.PendingDirectoryName,
            AnalysisResumeTranslationOffCore.JournalPendingFileName
        );
        Assert.True(File.Exists(pendingPath));
        var sourceAfter = SnapshotFiles(source.Output);
        Assert.True(
            sourceAfter.Remove(
                $"{AnalysisResumeCore.ResumeDirectoryName}/{AnalysisResumeCore.PendingDirectoryName}/{AnalysisResumeTranslationOffCore.JournalPendingFileName}"
            )
        );
        Assert.Equal(sourceBefore, sourceAfter);
        File.WriteAllText(pendingPath, "{\"schemaVersion\":");

        await using var recovered = await TransitionAsync(source);

        Assert.False(File.Exists(pendingPath));
        Assert.False(File.Exists(JournalPath(source.Output)));
        Assert.True(File.Exists(DerivedOwnerPath(source.Output)));
        Assert.NotEqual(source.ParentRunId, recovered.RunId);
    }

    [Fact]
    public async Task Transition_PreActivationJournalPlainResumeKeepsTheOriginalAutoPlan()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        var ownerBefore = File.ReadAllBytes(OwnerPath(source.Output));
        var checkpointsBefore = AnalysisResumeStage.All
            .Take(AnalysisResumeStage.TranslationSelection.Ordinal)
            .ToDictionary(stage => stage, stage => File.ReadAllBytes(CheckpointPath(source.Output, stage)));
        var assessmentsBefore = File.ReadAllBytes(
            Path.Combine(source.Output, "language-assessments.jsonl")
        );
        var candidatesBefore = File.ReadAllBytes(
            Path.Combine(source.Output, "translation-candidates.jsonl")
        );
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                source.Output,
                source.Parent,
                source.Target,
                ValidateSyntheticParentAsync,
                faultInjector: (point, _, _) =>
                    point == AnalysisResumeTranslationOffFaultPoint.AfterJournalPendingPublished
                        ? throw new IOException("stop before journal activation")
                        : Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        await using var resumed = await AnalysisResumeCore.OpenExistingAsync(
            source.Output,
            source.Parent,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(source.ParentRunId, resumed.RunId);
        Assert.Equal(TranslationWorkflowMode.Auto, resumed.Specification.Options.TranslationMode);
        Assert.False(File.Exists(DerivedOwnerPath(source.Output)));
        Assert.False(File.Exists(JournalPath(source.Output)));
        Assert.False(
            File.Exists(
                Path.Combine(
                    source.Output,
                    AnalysisResumeCore.ResumeDirectoryName,
                    AnalysisResumeCore.PendingDirectoryName,
                    AnalysisResumeTranslationOffCore.JournalPendingFileName
                )
            )
        );
        Assert.Equal(ownerBefore, File.ReadAllBytes(OwnerPath(source.Output)));
        foreach (var pair in checkpointsBefore)
        {
            Assert.Equal(pair.Value, File.ReadAllBytes(CheckpointPath(source.Output, pair.Key)));
        }
        Assert.Equal(
            assessmentsBefore,
            File.ReadAllBytes(Path.Combine(source.Output, "language-assessments.jsonl"))
        );
        Assert.Equal(
            candidatesBefore,
            File.ReadAllBytes(Path.Combine(source.Output, "translation-candidates.jsonl"))
        );
    }

    [Fact]
    public async Task Transition_CancellationAfterAnArchiveMoveRecoversWithoutDuplicateOrLoss()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        using var cancelled = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                source.Output,
                source.Parent,
                source.Target,
                ValidateSyntheticParentAsync,
                faultInjector: (point, moveIndex, _) =>
                {
                    if (
                        point == AnalysisResumeTranslationOffFaultPoint.AfterArchiveMove
                        && moveIndex == 0
                    )
                    {
                        cancelled.Cancel();
                        cancelled.Token.ThrowIfCancellationRequested();
                    }
                    return Task.CompletedTask;
                },
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        await using var recovered = await AnalysisResumeCore.OpenExistingAsync(
            source.Output,
            source.Target,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var derived = AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
            DerivedOwnerPath(source.Output)
        );
        Assert.False(File.Exists(JournalPath(source.Output)));
        Assert.All(
            derived.ArchiveMoves,
            move =>
            {
                Assert.False(File.Exists(ResolveRelative(source.Output, move.SourcePath)));
                Assert.True(File.Exists(ResolveRelative(source.Output, move.DestinationPath)));
            }
        );
    }

    [Fact]
    public async Task DerivedOpen_RejectsSameLengthArchiveTamper()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        await using (var target = await TransitionAsync(source)) { }
        var derived = AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
            DerivedOwnerPath(source.Output)
        );
        var move = derived.ArchiveMoves.First(item => item.Kind == "superseded-stage-artifact");
        var archive = ResolveRelative(source.Output, move.DestinationPath);
        var bytes = File.ReadAllBytes(archive);
        bytes[0] ^= 1;
        File.WriteAllBytes(archive, bytes);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await AnalysisResumeCore.OpenExistingAsync(
                source.Output,
                source.Target,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public async Task DerivedOpen_RejectsSameLengthParentAttemptTamper()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        await using (var target = await TransitionAsync(source)) { }
        var derived = AnalysisResumeCore.ReadStrictJson<AnalysisResumeDerivedOwnerRecord>(
            DerivedOwnerPath(source.Output)
        );
        var parentAttempt = Assert.Single(
            derived.ParentAttempts,
            reference => reference.FileName.EndsWith(
                "-start.json",
                StringComparison.Ordinal
            )
        );
        var path = Path.Combine(
            source.Output,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.AttemptsDirectoryName,
            parentAttempt.FileName
        );
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 1;
        File.WriteAllBytes(path, bytes);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await AnalysisResumeCore.OpenExistingAsync(
                source.Output,
                source.Target,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        Assert.IsType<InvalidDataException>(error);
    }

    [Fact]
    public async Task JournalRecovery_UnjournaledRecognizedPartialRefusesWithoutFurtherMutation()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var unexpected = await AnalysisResumeTranslationOffCore.TransitionAsync(
                source.Output,
                source.Parent,
                source.Target,
                ValidateSyntheticParentAsync,
                faultInjector: (point, _, _) =>
                    point == AnalysisResumeTranslationOffFaultPoint.AfterJournalPublished
                        ? throw new IOException("stop after journal")
                        : Task.CompletedTask,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });
        var unjournaled = Path.Combine(
            source.Output,
            "translated-strings.jsonl.partial.abcdefgh"
        );
        File.WriteAllText(unjournaled, "appeared after the journal was fixed");
        var before = SnapshotFiles(source.Output);

        var error = await Record.ExceptionAsync(async () =>
        {
            await using var unexpected = await AnalysisResumeCore.OpenExistingAsync(
                source.Output,
                source.Target,
                cancellationToken: TestContext.Current.CancellationToken
            );
        });

        Assert.IsType<InvalidDataException>(error);
        Assert.Equal(before, SnapshotFiles(source.Output));
        Assert.True(File.Exists(JournalPath(source.Output)));
        Assert.False(File.Exists(DerivedOwnerPath(source.Output)));
    }

    [Fact]
    public async Task Transition_ConcurrentStartersPermitExactlyOneWriter()
    {
        using var scope = new TemporaryScope();
        var source = await CreateSyntheticSourceAsync(scope, checkpointCount: 7);
        using var gate = new ManualResetEventSlim(false);
        var starters = Enumerable.Range(0, 32).Select(async _ =>
        {
            await Task.Yield();
            gate.Wait(TestContext.Current.CancellationToken);
            try
            {
                return await TransitionAsync(source);
            }
            catch (IOException)
            {
                return null;
            }
        }).ToArray();
        gate.Set();
        var results = await Task.WhenAll(starters);
        var winners = results.Where(session => session is not null).ToArray();
        try
        {
            Assert.Single(winners);
            Assert.NotEqual(source.ParentRunId, winners[0]!.RunId);
        }
        finally
        {
            foreach (var winner in winners)
            {
                await winner!.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task DerivedTarget_CompletesWithoutCallingTheLanguageDetectorOrCreatingModelWork()
    {
        using var scope = new TemporaryScope();
        var runnable = await CreateRunnableSourceAsync(scope);
        await using var target = await AnalysisResumeTranslationOffCore.TransitionAsync(
            runnable.Output,
            runnable.Parent,
            runnable.Target,
            ValidateSyntheticParentAsync,
            cancellationToken: TestContext.Current.CancellationToken
        );
        var detectorCalls = 0;
        bool DetectorTrap(out string? error)
        {
            detectorCalls++;
            error = "the detector must not be initialized";
            return false;
        }

        await AnalysisOrchestrator.RunAsync(
            runnable.Options,
            TestContext.Current.CancellationToken,
            verifyLanguageDetector: DetectorTrap,
            executingExecutablePath: runnable.Executable,
            existingResumeSession: target,
            resumeRequested: true
        );

        Assert.Equal(0, detectorCalls);
        Assert.False(File.Exists(Path.Combine(runnable.Output, ".incomplete")));
        Assert.Equal(0, new FileInfo(Path.Combine(runnable.Output, "language-assessments.jsonl")).Length);
        Assert.Equal(0, new FileInfo(Path.Combine(runnable.Output, "translation-candidates.jsonl")).Length);
        Assert.Equal(0, new FileInfo(Path.Combine(runnable.Output, "translated-strings.jsonl")).Length);
        Assert.DoesNotContain(
            Directory.EnumerateFiles(runnable.Output, "*", SearchOption.TopDirectoryOnly),
            path => Path.GetFileName(path).StartsWith(
                ".bstrings-translation-cache-",
                StringComparison.Ordinal
            )
        );
        using var summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(runnable.Output, "summary.json"),
                TestContext.Current.CancellationToken
            )
        );
        using var run = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(runnable.Output, "run.json"),
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(
            "off",
            run.RootElement.GetProperty("options").GetProperty("translationMode").GetString()
        );
        Assert.Equal(
            "auto",
            summary.RootElement
                .GetProperty("resume")
                .GetProperty("transition")
                .GetProperty("fromTranslationMode")
                .GetString()
        );
        Assert.Equal(
            "off",
            summary.RootElement
                .GetProperty("resume")
                .GetProperty("transition")
                .GetProperty("toTranslationMode")
                .GetString()
        );
    }

    [Fact]
    public async Task DerivedTarget_InputDriftAfterTransitionRefusesBeforeTargetStageSeven()
    {
        using var scope = new TemporaryScope();
        var runnable = await CreateRunnableSourceAsync(scope);
        await using var target = await AnalysisResumeTranslationOffCore.TransitionAsync(
            runnable.Output,
            runnable.Parent,
            runnable.Target,
            ValidateSyntheticParentAsync,
            cancellationToken: TestContext.Current.CancellationToken
        );
        await File.AppendAllTextAsync(
            runnable.Input,
            "tampered after transition",
            TestContext.Current.CancellationToken
        );

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisOrchestrator.RunAsync(
                runnable.Options,
                TestContext.Current.CancellationToken,
                executingExecutablePath: runnable.Executable,
                existingResumeSession: target,
                resumeRequested: true
            )
        );

        Assert.False(
            File.Exists(
                CheckpointPath(runnable.Output, AnalysisResumeStage.TranslationSelection)
            )
        );
        Assert.False(File.Exists(Path.Combine(runnable.Output, "language-assessments.jsonl")));
        Assert.False(File.Exists(Path.Combine(runnable.Output, "translation-candidates.jsonl")));
        Assert.False(File.Exists(Path.Combine(runnable.Output, "translated-strings.jsonl")));
    }

    private static Task ValidateSyntheticParentAsync(
        AnalysisResumeTranslationOffValidationContext context,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assert.Equal(TranslationWorkflowMode.Auto, context.Specification.Options.TranslationMode);
        return Task.CompletedTask;
    }

    private static Task<AnalysisResumeCore.AnalysisResumeSession> TransitionAsync(
        TransitionSource source
    ) =>
        AnalysisResumeTranslationOffCore.TransitionAsync(
            source.Output,
            source.Parent,
            source.Target,
            ValidateSyntheticParentAsync,
            cancellationToken: TestContext.Current.CancellationToken
        );

    private static async Task<TransitionSource> CreateSyntheticSourceAsync(
        TemporaryScope scope,
        int checkpointCount
    )
    {
        var output = scope.PathFor("synthetic-output");
        var parentOptions = CreateOptions(
            scope.PathFor("synthetic-input.bin"),
            output,
            TranslationWorkflowMode.Auto
        );
        File.WriteAllText(parentOptions.FilePath!, "synthetic input is not opened by core tests");
        var parent = new AnalysisResumeSpecification(
            1,
            1,
            "2.1.1",
            new string('a', 64),
            new string('b', 64),
            Path.GetFullPath(output),
            null,
            AnalysisResumeOptionsSnapshot.FromOptions(parentOptions),
            1,
            new string('c', 64)
        );
        string parentRunId;
        await using (var session = await AnalysisResumeCore.InitializeNewAsync(
            output,
            parent,
            TestContext.Current.CancellationToken
        ))
        {
            parentRunId = session.RunId;
            await File.WriteAllTextAsync(
                Path.Combine(output, ".incomplete"),
                "incomplete\n",
                TestContext.Current.CancellationToken
            );
            for (var index = 0; index < checkpointCount; index++)
            {
                var stage = AnalysisResumeStage.All[index];
                var artifactNames = ArtifactNames(stage);
                foreach (var artifactName in artifactNames)
                {
                    await File.WriteAllTextAsync(
                        Path.Combine(output, artifactName),
                        $"source bytes for {stage.Id}/{artifactName}\n",
                        TestContext.Current.CancellationToken
                    );
                }
                await session.CommitStageAsync(
                    stage,
                    artifactNames,
                    new StageStats(stage.Ordinal, stage.Id),
                    stage.Ordinal,
                    cancellationToken: TestContext.Current.CancellationToken
                );
            }
            await session.RecordAttemptOutcomeAsync(
                "cancelled",
                "test-stop",
                cancellationToken: TestContext.Current.CancellationToken
            );
        }
        var target = parent with
        {
            BstringsVersion = "2.1.2",
            ExecutableSha256 = new string('d', 64),
            ManagedAssemblySha256 = new string('e', 64),
            Options = parent.Options with { TranslationMode = TranslationWorkflowMode.Off },
        };
        return new TransitionSource(output, parentRunId, parent, target);
    }

    private static async Task<RunnableSource> CreateRunnableSourceAsync(TemporaryScope scope)
    {
        var input = scope.PathFor("runnable-input.bin");
        var output = scope.PathFor("runnable-output");
        var executable = Path.Combine(AppContext.BaseDirectory, "bstrings.exe");
        await File.WriteAllTextAsync(
            input,
            "analyst@example.com and other synthetic evidence",
            TestContext.Current.CancellationToken
        );
        var targetOptions = CreateOptions(input, output, TranslationWorkflowMode.Off);
        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisOrchestrator.RunAsync(
                targetOptions,
                TestContext.Current.CancellationToken,
                executingExecutablePath: executable,
                afterResumeStageCommitted: (stage, _) =>
                    stage == AnalysisResumeStage.RawMerge
                        ? throw new IOException("stop after valid stage six")
                        : Task.CompletedTask
            )
        );

        var target = AnalysisResumeCore.ReadSpecification(output);
        var imported = AnalysisResumeStage.All
            .Take(6)
            .Select(stage =>
            {
                var checkpoint = AnalysisResumeCore.ReadStrictJson<AnalysisResumeCore.CheckpointRecord>(
                    CheckpointPath(output, stage)
                );
                return new AnalysisResumeImportedCheckpoint(
                    stage,
                    checkpoint.Artifacts.Select(artifact => artifact.Path).ToArray(),
                    checkpoint.Stats.Clone(),
                    checkpoint.ElapsedSeconds
                );
            })
            .ToArray();
        Directory.Delete(
            Path.Combine(output, AnalysisResumeCore.ResumeDirectoryName),
            recursive: true
        );
        var parent = target with
        {
            BstringsVersion = "2.1.1",
            ExecutableSha256 = new string('a', 64),
            ManagedAssemblySha256 = new string('b', 64),
            Options = target.Options with { TranslationMode = TranslationWorkflowMode.Auto },
        };
        await using (var importedSession = await AnalysisResumeCore.ImportExistingAsync(
            output,
            parent,
            imported,
            cancellationToken: TestContext.Current.CancellationToken
        ))
        {
            await File.WriteAllTextAsync(
                Path.Combine(output, "language-assessments.jsonl"),
                string.Empty,
                TestContext.Current.CancellationToken
            );
            await File.WriteAllTextAsync(
                Path.Combine(output, "translation-candidates.jsonl"),
                string.Empty,
                TestContext.Current.CancellationToken
            );
            await importedSession.CommitStageAsync(
                AnalysisResumeStage.TranslationSelection,
                ["language-assessments.jsonl", "translation-candidates.jsonl"],
                new AnalysisResumeTranslationSelectionStats(null, 0),
                0,
                cancellationToken: TestContext.Current.CancellationToken
            );
            await importedSession.RecordAttemptOutcomeAsync(
                "cancelled",
                "test-stop",
                cancellationToken: TestContext.Current.CancellationToken
            );
        }
        return new RunnableSource(input, output, executable, targetOptions, parent, target);
    }

    private static AnalysisOptions CreateOptions(
        string input,
        string output,
        TranslationWorkflowMode translationMode
    ) =>
        new(
            FilePath: input,
            DirectoryPath: null,
            Mask: null,
            OutputDirectory: output,
            Full: true,
            OcrMode: OcrWorkflowMode.Off,
            OcrProvider: OcrProvider.Auto,
            OcrThreads: 0,
            RecoveryMode: ExecutableRecoveryMode.Off,
            TranslationMode: translationMode,
            LanguageDetectionMode: LanguageDetectionMode.Accurate,
            TranslationPolicy: LanguageTriagePolicy.HighRecall,
            LanguageConfidence: 0.55,
            LanguageMargin: 0.10,
            TranslationTarget: "en",
            TranslationDevice: "cpu",
            TranslationParallelism: 0,
            TranslationThreads: 0,
            TranslationGpuLayers: -1,
            TranslationStrictDeterminism: false,
            PatternSelection: @"analyst@example\.com",
            RegexFilePath: null,
            Processor: "cpu",
            CpuEngine: "dotnet",
            MinimumStringLength: 3,
            MaximumStringLength: 4096,
            TranslationMinimumCharacters: 8,
            TranslationMaximumCharacters: 512,
            BundleRoot: null,
            Airgap: false,
            NativeExtractionMode: NativeExtractionMode.On,
            DecoderMode: DecoderWorkflowMode.Off
        );

    private static IReadOnlyList<string> ArtifactNames(AnalysisResumeStage stage) =>
        stage == AnalysisResumeStage.TranslationSelection
            ? ["language-assessments.jsonl", "translation-candidates.jsonl"]
            : stage == AnalysisResumeStage.Translation
                ? ["translated-strings.jsonl", "translation-work-stats.json"]
                : [$"stage-{stage.Ordinal:D2}-{stage.Id}.artifact"];

    private static SortedDictionary<string, string> SnapshotFiles(string output)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(output, path).Replace('\\', '/');
            result.Add(relative, Hash(File.ReadAllBytes(path)));
        }
        return result;
    }

    private static string OwnerPath(string output) =>
        Path.Combine(
            output,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.OwnerFileName
        );

    private static string CheckpointPath(string output, AnalysisResumeStage stage) =>
        Path.Combine(
            output,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.CheckpointsDirectoryName,
            AnalysisResumeCore.CheckpointFileName(stage)
        );

    private static string DerivedOwnerPath(string output) =>
        Path.Combine(
            output,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeTranslationOffCore.DerivedOwnerFileName
        );

    private static string JournalPath(string output) =>
        Path.Combine(
            output,
            AnalysisResumeCore.ResumeDirectoryName,
            AnalysisResumeCore.PendingDirectoryName,
            AnalysisResumeTranslationOffCore.JournalFileName
        );

    private static string ResolveRelative(string output, string relativePath) =>
        Path.Combine(output, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record StageStats(long Records, string Label);

    private sealed record TransitionSource(
        string Output,
        string ParentRunId,
        AnalysisResumeSpecification Parent,
        AnalysisResumeSpecification Target
    );

    private sealed record RunnableSource(
        string Input,
        string Output,
        string Executable,
        AnalysisOptions Options,
        AnalysisResumeSpecification Parent,
        AnalysisResumeSpecification Target
    );

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes
    );

    private sealed class TemporaryScope : IDisposable
    {
        internal TemporaryScope()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-resume-translation-off-adversarial",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(RootPath);
        }

        internal string RootPath { get; }
        internal string PathFor(string name) => Path.Combine(RootPath, name);

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
