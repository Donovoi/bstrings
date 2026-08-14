using System.Runtime.InteropServices;
using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class AnalysisResumeCoreTests
{
    [Fact]
    public async Task InitializeCommitReopenAndReuse_PreservesOwnerAndCheckpointIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("results");
        var specification = CreateSpecification(output);
        string runId;

        await using (var session = await AnalysisResumeCore.InitializeNewAsync(
            output,
            specification,
            cancellationToken
        ))
        {
            runId = session.RunId;
            Assert.Equal(AnalysisResumeAttemptMode.New, session.Mode);
            Assert.Equal(specification, AnalysisResumeCore.ReadSpecification(output));
            Assert.True(
                File.Exists(
                    Path.Combine(
                        output,
                        AnalysisResumeCore.ResumeDirectoryName,
                        AnalysisResumeCore.OwnerFileName
                    )
                )
            );
            await File.WriteAllTextAsync(
                Path.Combine(output, ".incomplete"),
                "incomplete\n",
                cancellationToken
            );
            await File.WriteAllTextAsync(
                Path.Combine(output, "inventory.jsonl"),
                "{\"path\":\"evidence.bin\"}\n",
                cancellationToken
            );
            await session.CommitStageAsync(
                AnalysisResumeStage.InputInventory,
                ["inventory.jsonl"],
                new StageStats(1, "inventory"),
                elapsedSeconds: 1.25,
                cancellationToken: cancellationToken
            );
            await session.RecordAttemptOutcomeAsync(
                "cancelled",
                "operator-cancelled",
                "new-run",
                cancellationToken
            );
        }

        await using var reopened = await AnalysisResumeCore.OpenExistingAsync(
            output,
            specification,
            cancellationToken: cancellationToken
        );
        var (reused, stats) = await reopened.TryReuseStageAsync<StageStats>(
            AnalysisResumeStage.InputInventory,
            cancellationToken
        );

        Assert.True(reused);
        Assert.Equal(new StageStats(1, "inventory"), stats);
        Assert.Equal(runId, reopened.RunId);
        Assert.NotEqual(string.Empty, reopened.AttemptId);
        Assert.Equal(AnalysisResumeAttemptMode.Resume, reopened.Mode);
        Assert.Equal(AnalysisResumeStage.InputInventory, reopened.LastCommittedStage);
        Assert.Equal([AnalysisResumeStage.InputInventory.Id], reopened.ReusedStages);
        var evidence = reopened.CreatePublicEvidence();
        Assert.Equal(runId, evidence.RunId);
        Assert.Equal("resume", evidence.AttemptMode);
        Assert.Equal(AnalysisResumeStage.InputInventory.Id, evidence.LastCommittedStage);
    }

    [Fact]
    public async Task OpenExisting_RejectsAnySpecificationDrift()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var mismatched = completed.Specification with { PatternSha256 = new string('c', 64) };
        var attemptsBefore = Directory.GetFiles(AttemptsPath(completed.Output)).Length;

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                mismatched,
                cancellationToken: cancellationToken
            )
        );
        Assert.Equal(attemptsBefore, Directory.GetFiles(AttemptsPath(completed.Output)).Length);
    }

    [Fact]
    public async Task OpenExisting_RejectsSameLengthArtifactTamper()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var artifact = Path.Combine(completed.Output, ArtifactName(AnalysisResumeStage.InputInventory));
        var original = await File.ReadAllTextAsync(artifact, cancellationToken);
        var tampered = original[..^2] + "X\n";
        Assert.Equal(original.Length, tampered.Length);
        await File.WriteAllTextAsync(artifact, tampered, cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );
        Assert.Contains("SHA-256", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenExisting_RejectsHardLinkedCommittedArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The resume hard-link contract is Windows-specific.");
        }
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var artifact = Path.Combine(
            completed.Output,
            ArtifactName(AnalysisResumeStage.InputInventory)
        );
        var outside = scope.PathFor("outside-artifact.jsonl");
        if (!CreateHardLink(outside, artifact, IntPtr.Zero))
        {
            Assert.Skip($"Hard links are unavailable on this host: {Marshal.GetLastWin32Error()}.");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );

        Assert.Contains("physical filesystem link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyCommittedState_RejectsHardLinkAddedAfterArtifactLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The resume hard-link contract is Windows-specific.");
        }
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        await using var session = await AnalysisResumeCore.OpenExistingAsync(
            completed.Output,
            completed.Specification,
            cancellationToken: cancellationToken
        );
        var artifact = Path.Combine(
            completed.Output,
            ArtifactName(AnalysisResumeStage.InputInventory)
        );
        var outside = scope.PathFor("late-hardlink.jsonl");
        if (!CreateHardLink(outside, artifact, IntPtr.Zero))
        {
            Assert.Skip($"Hard links are unavailable on this host: {Marshal.GetLastWin32Error()}.");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.VerifyCommittedStateAsync(cancellationToken: cancellationToken)
        );

        Assert.Contains("physical filesystem link", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenExisting_RejectsHardLinkedAttemptMetadata()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The resume hard-link contract is Windows-specific.");
        }
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var start = Directory.GetFiles(AttemptsPath(completed.Output), "*-start.json").Single();
        var outside = scope.PathFor("attempt-hardlink.json");
        if (!CreateHardLink(outside, start, IntPtr.Zero))
        {
            Assert.Skip($"Hard links are unavailable on this host: {Marshal.GetLastWin32Error()}.");
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );

        Assert.Contains("physical filesystem link", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("duplicate-property")]
    [InlineData("unknown-property")]
    [InlineData("missing-property")]
    [InlineData("duplicate-file")]
    [InlineData("unknown-stage")]
    public async Task OpenExisting_RejectsMalformedCheckpointSets(string mutation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var checkpoint = CheckpointPath(completed.Output, AnalysisResumeStage.InputInventory);
        var json = await File.ReadAllTextAsync(checkpoint, cancellationToken);

        switch (mutation)
        {
            case "corrupt":
                await File.WriteAllTextAsync(checkpoint, "{\n", cancellationToken);
                break;
            case "duplicate-property":
                await File.WriteAllTextAsync(
                    checkpoint,
                    json.Replace(
                        "\"schemaVersion\":1",
                        "\"schemaVersion\":1,\"schemaVersion\":1",
                        StringComparison.Ordinal
                    ),
                    cancellationToken
                );
                break;
            case "unknown-property":
                await File.WriteAllTextAsync(
                    checkpoint,
                    json.Replace(
                        "\"schemaVersion\":1",
                        "\"schemaVersion\":1,\"unknownField\":true",
                        StringComparison.Ordinal
                    ),
                    cancellationToken
                );
                break;
            case "missing-property":
                await File.WriteAllTextAsync(
                    checkpoint,
                    json.Replace(
                        "\"recordType\":\"analysis-stage-checkpoint\",",
                        string.Empty,
                        StringComparison.Ordinal
                    ),
                    cancellationToken
                );
                break;
            case "duplicate-file":
                File.Copy(
                    checkpoint,
                    Path.Combine(
                        Path.GetDirectoryName(checkpoint)!,
                        "0001-input-inventory-copy.json"
                    )
                );
                break;
            case "unknown-stage":
                File.Move(
                    checkpoint,
                    Path.Combine(Path.GetDirectoryName(checkpoint)!, "0001-unknown-stage.json")
                );
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation '{mutation}'.");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );
    }

    [Fact]
    public async Task OpenExisting_RejectsCheckpointGap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 3, cancellationToken);
        File.Delete(CheckpointPath(completed.Output, AnalysisResumeStage.ContentRouting));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );
    }

    [Fact]
    public async Task CommitStage_RejectsTraversalAndReparseArtifacts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("results");
        var outside = scope.PathFor("outside.txt");
        await File.WriteAllTextAsync(outside, "outside\n", cancellationToken);

        await using var session = await AnalysisResumeCore.InitializeNewAsync(
            output,
            CreateSpecification(output),
            cancellationToken
        );
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            session.CommitStageAsync(
                AnalysisResumeStage.InputInventory,
                ["../outside.txt"],
                new StageStats(1, "unsafe"),
                0,
                cancellationToken: cancellationToken
            )
        );

        var link = Path.Combine(output, "linked.txt");
        try
        {
            File.CreateSymbolicLink(link, outside);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException
        )
        {
            return;
        }
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            session.CommitStageAsync(
                AnalysisResumeStage.InputInventory,
                ["linked.txt"],
                new StageStats(1, "linked"),
                0,
                cancellationToken: cancellationToken
            )
        );
        Assert.Contains("reparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(session.LastCommittedStage);
    }

    [Fact]
    public async Task OpenExisting_AllowsExactlyOneOfThirtyTwoConcurrentLeases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);

        var opens = Enumerable.Range(0, 32).Select(async _ =>
        {
            try
            {
                return await AnalysisResumeCore.OpenExistingAsync(
                    completed.Output,
                    completed.Specification,
                    cancellationToken: cancellationToken
                );
            }
            catch (IOException)
            {
                return null;
            }
        });
        var results = await Task.WhenAll(opens);
        var winners = results.Where(session => session is not null).ToArray();
        try
        {
            Assert.Single(winners);
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
    public async Task CommitStage_CancellationBeforePublicationLeavesNoCheckpoint()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("results");
        await using var session = await AnalysisResumeCore.InitializeNewAsync(
            output,
            CreateSpecification(output),
            testCancellationToken
        );
        var artifact = Path.Combine(output, "artifact.bin");
        await File.WriteAllBytesAsync(
            artifact,
            new byte[2 * 1024 * 1024],
            testCancellationToken
        );
        using var cancelled = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.CommitStageAsync(
                AnalysisResumeStage.InputInventory,
                ["artifact.bin"],
                new StageStats(1, "cancelled"),
                0,
                (_, _) => cancelled.Cancel(),
                cancellationToken: cancelled.Token
            )
        );

        Assert.Null(session.LastCommittedStage);
        Assert.Empty(Directory.GetFiles(CheckpointsPath(output), "*.json"));
        Assert.Empty(Directory.GetFiles(CheckpointsPath(output), "*.partial.*"));
        Assert.Empty(Directory.GetFiles(PendingPath(output)));
    }

    [Fact]
    public async Task ImportExisting_CancelledPrecommitIsAtomicAndCanBeRetried()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("legacy");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, ".incomplete"),
            "incomplete\n",
            testCancellationToken
        );
        var artifact = Path.Combine(output, "inventory.jsonl");
        await File.WriteAllBytesAsync(artifact, new byte[2 * 1024 * 1024], testCancellationToken);
        var specification = CreateSpecification(output);
        var imported = new[]
        {
            AnalysisResumeImportedCheckpoint.Create(
                AnalysisResumeStage.InputInventory,
                new[] { "inventory.jsonl" },
                new StageStats(1, "legacy")
            ),
        };
        using var cancelled = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AnalysisResumeCore.ImportExistingAsync(
                output,
                specification,
                imported,
                (_, _) => cancelled.Cancel(),
                cancelled.Token
            )
        );

        Assert.False(AnalysisResumeCore.HasResumeMetadata(output));
        Assert.Empty(
            Directory.EnumerateDirectories(
                scope.DirectoryPath,
                ".bstrings-resume-init.*"
            )
        );

        await using var session = await AnalysisResumeCore.ImportExistingAsync(
            output,
            specification,
            imported,
            cancellationToken: testCancellationToken
        );
        var (reused, stats) = await session.TryReuseStageAsync<StageStats>(
            AnalysisResumeStage.InputInventory,
            testCancellationToken
        );
        Assert.True(reused);
        Assert.Equal(new StageStats(1, "legacy"), stats);
        Assert.True(session.LegacyImported);
        Assert.Equal(AnalysisResumeAttemptMode.LegacyImport, session.Mode);
    }

    [Fact]
    public async Task AttemptOutcomes_AreImmutableAndDisposeRecordsAbandonment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var firstOutput = scope.PathFor("first");
        string firstAttempt;
        await using (var session = await AnalysisResumeCore.InitializeNewAsync(
            firstOutput,
            CreateSpecification(firstOutput),
            cancellationToken
        ))
        {
            firstAttempt = session.AttemptId;
            await session.RecordAttemptOutcomeAsync(
                "failed",
                "artifact-invalid",
                "checkpoint-read",
                cancellationToken
            );
            await session.RecordAttemptOutcomeAsync(
                "complete",
                cancellationToken: cancellationToken
            );
        }
        using (var outcome = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AttemptsPath(firstOutput), $"{firstAttempt}-outcome.json"),
                cancellationToken
            )
        ))
        {
            Assert.Equal("failed", outcome.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "artifact-invalid",
                outcome.RootElement.GetProperty("failureCategory").GetString()
            );
        }

        var secondOutput = scope.PathFor("second");
        string secondAttempt;
        await using (var session = await AnalysisResumeCore.InitializeNewAsync(
            secondOutput,
            CreateSpecification(secondOutput),
            cancellationToken
        ))
        {
            secondAttempt = session.AttemptId;
        }
        using var abandoned = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AttemptsPath(secondOutput), $"{secondAttempt}-outcome.json"),
                cancellationToken
            )
        );
        Assert.Equal("abandoned", abandoned.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "session-disposed",
            abandoned.RootElement.GetProperty("failureCategory").GetString()
        );
    }

    [Theory]
    [InlineData("duplicate-property")]
    [InlineData("unknown-property")]
    [InlineData("missing-property")]
    [InlineData("wrong-status")]
    [InlineData("wrong-owner-mode")]
    [InlineData("outcome-without-start")]
    public async Task OpenExisting_RejectsInvalidAttemptHistoryWithoutPublishingANewAttempt(
        string mutation
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var attempts = AttemptsPath(completed.Output);
        var startPath = Directory.GetFiles(attempts, "*-start.json").Single();
        var start = await File.ReadAllTextAsync(startPath, cancellationToken);

        switch (mutation)
        {
            case "duplicate-property":
                start = start.Replace(
                    "\"schemaVersion\":1",
                    "\"schemaVersion\":1,\"schemaVersion\":1",
                    StringComparison.Ordinal
                );
                break;
            case "unknown-property":
                start = start.Replace(
                    "\"schemaVersion\":1",
                    "\"schemaVersion\":1,\"unknown\":true",
                    StringComparison.Ordinal
                );
                break;
            case "missing-property":
                start = start.Replace(
                    "\"recordType\":\"analysis-resume-attempt\",",
                    string.Empty,
                    StringComparison.Ordinal
                );
                break;
            case "wrong-status":
                start = start.Replace(
                    "\"status\":\"running\"",
                    "\"status\":\"complete\"",
                    StringComparison.Ordinal
                );
                break;
            case "wrong-owner-mode":
                start = start.Replace(
                    "\"mode\":\"new\"",
                    "\"mode\":\"legacy-import\"",
                    StringComparison.Ordinal
                );
                break;
            case "outcome-without-start":
                File.Delete(startPath);
                break;
            default:
                throw new InvalidOperationException($"Unknown mutation '{mutation}'.");
        }
        if (mutation != "outcome-without-start")
        {
            await File.WriteAllTextAsync(startPath, start, cancellationToken);
        }
        var filesBefore = Directory.GetFiles(attempts).Order().ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );

        Assert.Equal(filesBefore, Directory.GetFiles(attempts).Order().ToArray());
    }

    [Fact]
    public async Task AttemptPartials_DoNotMaskNewStartAndOutcomeRecordsWithExactSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var attempts = AttemptsPath(completed.Output);
        var pending = PendingPath(completed.Output);
        var staleStart = Path.Combine(
            pending,
            $"attempt-{Guid.NewGuid():N}.json"
        );
        var staleOutcome = Path.Combine(
            pending,
            $"attempt-{Guid.NewGuid():N}.json"
        );
        await File.WriteAllTextAsync(staleStart, "{", cancellationToken);
        await File.WriteAllTextAsync(staleOutcome, "partial", cancellationToken);

        string attemptId;
        string runId;
        await using (var resumed = await AnalysisResumeCore.OpenExistingAsync(
            completed.Output,
            completed.Specification,
            cancellationToken: cancellationToken
        ))
        {
            attemptId = resumed.AttemptId;
            runId = resumed.RunId;
            using var start = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(attempts, $"{attemptId}-start.json"),
                    cancellationToken
                )
            );
            AssertAttemptRecord(
                start.RootElement,
                resumed.RunId,
                attemptId,
                mode: "resume",
                status: "running",
                completed: false
            );
            await resumed.RecordAttemptOutcomeAsync(
                "cancelled",
                "operator-cancelled",
                "resume-test",
                cancellationToken
            );
        }

        using (var outcome = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(attempts, $"{attemptId}-outcome.json"),
                cancellationToken
            )
        ))
        {
            AssertAttemptRecord(
                outcome.RootElement,
                runId,
                attemptId,
                mode: "resume",
                status: "cancelled",
                completed: true
            );
            Assert.Equal(
                "operator-cancelled",
                outcome.RootElement.GetProperty("failureCategory").GetString()
            );
            Assert.Equal(
                "resume-test",
                outcome.RootElement.GetProperty("provenance").GetString()
            );
        }
        Assert.False(File.Exists(staleStart));
        Assert.False(File.Exists(staleOutcome));
    }

    [Fact]
    public async Task OpenExisting_RejectsCheckpointFilesBeyondTheKnownStageSet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(
            scope,
            AnalysisResumeStage.All.Count,
            cancellationToken
        );
        var extra = Path.Combine(CheckpointsPath(completed.Output), "9999-extra.json");
        File.Copy(
            CheckpointPath(completed.Output, AnalysisResumeStage.EngineLedger),
            extra
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );
        Assert.Contains("unexpected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(extra));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenExisting_RejectsUnknownCheckpointStorageEntriesWithoutChangingThem(
        bool directory
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var unknown = Path.Combine(CheckpointsPath(completed.Output), "analyst-note.bin");
        if (directory)
        {
            Directory.CreateDirectory(unknown);
        }
        else
        {
            await File.WriteAllTextAsync(unknown, "analyst note", cancellationToken);
        }

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                completed.Output,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );
        Assert.Contains("unexpected", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(directory ? Directory.Exists(unknown) : File.Exists(unknown));
    }

    [Fact]
    public async Task InitializeNew_AcceptsAnUnownedStaleLockAsTheOnlyExistingEntry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("stale-lock-results");
        Directory.CreateDirectory(output);
        var lockPath = Path.Combine(output, AnalysisResumeCore.LockFileName);
        await File.WriteAllTextAsync(lockPath, string.Empty, cancellationToken);

        await using var session = await AnalysisResumeCore.InitializeNewAsync(
            output,
            CreateSpecification(output),
            cancellationToken
        );

        Assert.Equal(AnalysisResumeAttemptMode.New, session.Mode);
        Assert.True(AnalysisResumeCore.HasResumeMetadata(output));
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public async Task OpenExisting_RecoversMetadataPublishedBeforeAttemptStart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("metadata-only-results");
        var specification = CreateSpecification(output);
        await using (var session = await AnalysisResumeCore.InitializeNewAsync(
            output,
            specification,
            cancellationToken
        ))
        {
            await session.RecordAttemptOutcomeAsync(
                "cancelled",
                "test-stop",
                cancellationToken: cancellationToken
            );
        }
        foreach (var attempt in Directory.GetFiles(AttemptsPath(output)))
        {
            File.Delete(attempt);
        }

        await using var resumed = await AnalysisResumeCore.OpenExistingAsync(
            output + Path.DirectorySeparatorChar,
            specification,
            cancellationToken: cancellationToken
        );

        Assert.Equal(AnalysisResumeAttemptMode.Resume, resumed.Mode);
        Assert.True(File.Exists(Path.Combine(output, AnalysisResumeCore.LockFileName)));
        Assert.Single(Directory.GetFiles(AttemptsPath(output), "*-start.json"));
    }

    [Fact]
    public async Task InitializeNew_RejectsDanglingLeaseSymlinkWithoutCreatingItsTarget()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("The dangling lease reparse contract is Windows-specific.");
        }
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("dangling-lock-results");
        Directory.CreateDirectory(output);
        var outside = scope.PathFor("outside-lock-target");
        var lockPath = Path.Combine(output, AnalysisResumeCore.LockFileName);
        try
        {
            File.CreateSymbolicLink(lockPath, outside);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException
        )
        {
            Assert.Skip($"Symbolic links are unavailable on this host: {ex.Message}");
        }

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AnalysisResumeCore.InitializeNewAsync(
                output,
                CreateSpecification(output),
                cancellationToken
            )
        );

        Assert.False(File.Exists(outside));
        Assert.False(AnalysisResumeCore.HasResumeMetadata(output));
    }

    [Fact]
    public async Task InitializeNew_RefusesAnUnknownExistingEntryWithoutChangingIt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var output = scope.PathFor("unknown-entry-results");
        Directory.CreateDirectory(output);
        var lockPath = Path.Combine(output, AnalysisResumeCore.LockFileName);
        var unknownPath = Path.Combine(output, "analyst-note.txt");
        await File.WriteAllTextAsync(lockPath, string.Empty, cancellationToken);
        await File.WriteAllTextAsync(unknownPath, "keep this note", cancellationToken);

        await Assert.ThrowsAsync<IOException>(() =>
            AnalysisResumeCore.InitializeNewAsync(
                output,
                CreateSpecification(output),
                cancellationToken
            )
        );

        Assert.False(AnalysisResumeCore.HasResumeMetadata(output));
        Assert.Equal("keep this note", await File.ReadAllTextAsync(unknownPath, cancellationToken));
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public async Task OpenExisting_RemovesAnExactInterruptedCheckpointPublicationAndCanRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 0, cancellationToken);
        var artifactName = ArtifactName(AnalysisResumeStage.InputInventory);
        await File.WriteAllTextAsync(
            Path.Combine(completed.Output, artifactName),
            "retry artifact\n",
            cancellationToken
        );
        var interrupted = Path.Combine(
            PendingPath(completed.Output),
            $"checkpoint-{Guid.NewGuid():N}.json"
        );
        await File.WriteAllTextAsync(interrupted, "interrupted publication", cancellationToken);

        await using var resumed = await AnalysisResumeCore.OpenExistingAsync(
            completed.Output,
            completed.Specification,
            cancellationToken: cancellationToken
        );
        Assert.False(File.Exists(interrupted));
        await resumed.CommitStageAsync(
            AnalysisResumeStage.InputInventory,
            [artifactName],
            new StageStats(1, "retry"),
            0,
            cancellationToken: cancellationToken
        );

        Assert.Equal(AnalysisResumeStage.InputInventory, resumed.LastCommittedStage);
        Assert.True(
            File.Exists(
                CheckpointPath(completed.Output, AnalysisResumeStage.InputInventory)
            )
        );
    }

    [Fact]
    public async Task OpenExisting_RefusesResultsMovedFromTheirRecordedOutputDirectory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var completed = await CreateCheckpointedRunAsync(scope, 1, cancellationToken);
        var attemptsBefore = Directory.GetFiles(
            AttemptsPath(completed.Output),
            "*-start.json"
        ).Length;
        var moved = scope.PathFor("moved-results");
        Directory.Move(completed.Output, moved);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            AnalysisResumeCore.OpenExistingAsync(
                moved,
                completed.Specification,
                cancellationToken: cancellationToken
            )
        );
        Assert.Contains("result directory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            attemptsBefore,
            Directory.GetFiles(AttemptsPath(moved), "*-start.json").Length
        );
    }

    private static async Task<(
        string Output,
        AnalysisResumeSpecification Specification
    )> CreateCheckpointedRunAsync(
        TemporaryDirectory scope,
        int checkpointCount,
        CancellationToken cancellationToken
    )
    {
        var output = scope.PathFor("results");
        var specification = CreateSpecification(output);
        await using (var session = await AnalysisResumeCore.InitializeNewAsync(
            output,
            specification,
            cancellationToken
        ))
        {
            await File.WriteAllTextAsync(
                Path.Combine(output, ".incomplete"),
                "incomplete\n",
                cancellationToken
            );
            for (var index = 0; index < checkpointCount; index++)
            {
                var stage = AnalysisResumeStage.All[index];
                var artifactName = ArtifactName(stage);
                await File.WriteAllTextAsync(
                    Path.Combine(output, artifactName),
                    $"artifact-{stage.Id}\n",
                    cancellationToken
                );
                await session.CommitStageAsync(
                    stage,
                    [artifactName],
                    new StageStats(index + 1, stage.Id),
                    index + 0.5,
                    cancellationToken: cancellationToken
                );
            }
            await session.RecordAttemptOutcomeAsync(
                "cancelled",
                "test-stop",
                cancellationToken: cancellationToken
            );
        }
        return (output, specification);
    }

    private static AnalysisResumeSpecification CreateSpecification(string? outputDirectory = null)
    {
        var options = new AnalysisOptions(
            FilePath: "evidence.bin",
            DirectoryPath: null,
            Mask: null,
            OutputDirectory: "ignored",
            Full: false,
            OcrMode: OcrWorkflowMode.Off,
            OcrProvider: OcrProvider.Auto,
            OcrThreads: 0,
            RecoveryMode: ExecutableRecoveryMode.Off,
            TranslationMode: TranslationWorkflowMode.Off,
            LanguageDetectionMode: LanguageDetectionMode.Accurate,
            TranslationPolicy: LanguageTriagePolicy.Balanced,
            LanguageConfidence: 0.8,
            LanguageMargin: 0.2,
            TranslationTarget: "en",
            TranslationDevice: "cpu",
            TranslationParallelism: 1,
            TranslationThreads: 1,
            TranslationGpuLayers: 0,
            TranslationStrictDeterminism: true,
            PatternSelection: "email",
            RegexFilePath: null,
            Processor: "cpu",
            CpuEngine: "dotnet",
            MinimumStringLength: 4,
            MaximumStringLength: 4096,
            TranslationMinimumCharacters: 8,
            TranslationMaximumCharacters: 512,
            BundleRoot: null,
            Airgap: true
        );
        return new AnalysisResumeSpecification(
            1,
            1,
            "2.0.0",
            new string('a', 64),
            new string('c', 64),
            Path.GetFullPath(outputDirectory ?? "synthetic-results"),
            null,
            AnalysisResumeOptionsSnapshot.FromOptions(options),
            1,
            new string('b', 64)
        );
    }

    private static string ArtifactName(AnalysisResumeStage stage) => $"{stage.Id}.jsonl";

    private static string CheckpointPath(string output, AnalysisResumeStage stage) =>
        Path.Combine(CheckpointsPath(output), $"{stage.Ordinal:D4}-{stage.Id}.json");

    private static string CheckpointsPath(string output) =>
        Path.Combine(output, AnalysisResumeCore.ResumeDirectoryName, "checkpoints");

    private static string AttemptsPath(string output) =>
        Path.Combine(output, AnalysisResumeCore.ResumeDirectoryName, "attempts");

    private static string PendingPath(string output) =>
        Path.Combine(output, AnalysisResumeCore.ResumeDirectoryName, "pending");

    private static void AssertAttemptRecord(
        JsonElement root,
        string runId,
        string attemptId,
        string mode,
        string status,
        bool completed
    )
    {
        var expectedProperties = new HashSet<string>(
            [
                "schemaVersion",
                "recordType",
                "runId",
                "attemptId",
                "mode",
                "startedUtc",
                "completedUtc",
                "status",
                "lastCommittedStage",
                "failureCategory",
                "provenance",
            ],
            StringComparer.Ordinal
        );
        var actualProperties = root.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(expectedProperties.Count, actualProperties.Length);
        Assert.True(
            expectedProperties.SetEquals(actualProperties),
            "Attempt record properties must match the exact schema."
        );
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("analysis-resume-attempt", root.GetProperty("recordType").GetString());
        Assert.Equal(runId, root.GetProperty("runId").GetString());
        Assert.Equal(attemptId, root.GetProperty("attemptId").GetString());
        Assert.Equal(mode, root.GetProperty("mode").GetString());
        Assert.Equal(status, root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("startedUtc").ValueKind);
        Assert.Equal(
            completed ? JsonValueKind.String : JsonValueKind.Null,
            root.GetProperty("completedUtc").ValueKind
        );
    }

    private sealed record StageStats(long Records, string Label);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName,
        string existingFileName,
        IntPtr securityAttributes
    );

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-analysis-resume-tests",
                Guid.NewGuid().ToString("N")
            );
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
