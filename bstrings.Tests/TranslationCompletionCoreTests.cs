using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class TranslationCompletionCoreTests
{
    private static readonly TranslationValidationRequirements Requirements = new(
        "llama.cpp",
        "en",
        "synthetic/model",
        "revision-1",
        new string('a', 64)
    );

    [Fact]
    public async Task ValidateAsync_AcceptsExactlyOneVerifiedChildPerCandidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllLinesAsync(
            candidates,
            [Original("raw-1", "bonjour", "0x10"), Original("raw-2", "salut", "0x20")],
            cancellationToken
        );
        await File.WriteAllLinesAsync(
            translations,
            [
                Translation("translated-1", "raw-1", "hello", "0x10"),
                Translation("translated-2", "raw-2", "hi", "0x20"),
            ],
            cancellationToken
        );

        var stats = await TranslationCompletionCore.ValidateAsync(
            candidates,
            translations,
            scope.DirectoryPath,
            2,
            Requirements,
            cancellationToken
        );

        Assert.Equal(new TranslationCompletionStats(2, 2), stats);
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-provenance.*"));
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-fallback-text.*"));
    }

    [Fact]
    public async Task ValidateAsync_RejectsACandidateWithoutAChild()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(translations, string.Empty, cancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("exactly one translated child", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsTwoChildrenForTheSameCandidate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllLinesAsync(
            candidates,
            [Original("raw-1", "bonjour", "0x10"), Original("raw-2", "salut", "0x20")],
            cancellationToken
        );
        await File.WriteAllLinesAsync(
            translations,
            [
                Translation("translated-1", "raw-1", "hello", "0x10"),
                Translation("translated-2", "raw-1", "hello again", "0x10"),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                2,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("ordered child", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsPermutedChildrenEvenWhenParentsAndCountsExist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllLinesAsync(
            candidates,
            [Original("raw-1", "bonjour", "0x10"), Original("raw-2", "salut", "0x20")],
            cancellationToken
        );
        await File.WriteAllLinesAsync(
            translations,
            [
                Translation("translated-2", "raw-2", "hi", "0x20"),
                Translation("translated-1", "raw-1", "hello", "0x10"),
            ],
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                2,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("ordered child", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsChangedEvidenceLocation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation("translated-1", "raw-1", "hello", "0x11"),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("exact sourceFile, location, and origin", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAConfiguredModelIdentityMismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation("translated-1", "raw-1", "hello", "0x10", model: "wrong/model"),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("model", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("verified setting", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsAMissingTranslationOutcome()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation("translated-1", "raw-1", "hello", "0x10", outcome: null),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("outcome", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unsupported")]
    public async Task ValidateAsync_RejectsMissingOrUnsupportedTranslationIntegrity(
        string? integrity
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                "hello",
                "0x10",
                integrity: integrity
            ),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("translationIntegrity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsWellFormedPreservationFallbackMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        const string sourceText = "token: test-user\nsecond line";
        await File.WriteAllTextAsync(candidates, Original("raw-1", sourceText, "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                sourceText,
                "0x10",
                outcome: "unchanged",
                integrity: "preservation-fallback",
                integrityReason: "hard-identifier-retention-mismatch"
            ),
            cancellationToken
        );

        var stats = await TranslationCompletionCore.ValidateAsync(
            candidates,
            translations,
            scope.DirectoryPath,
            1,
            Requirements,
            cancellationToken
        );

        Assert.Equal(new TranslationCompletionStats(1, 1), stats);
    }

    [Theory]
    [InlineData("token: TEST-user\nsecond line")]
    [InlineData("token: test-user\nsecond line ")]
    [InlineData("token: test-user\r\nsecond line")]
    public async Task ValidateAsync_RejectsFallbackTextThatIsNotTheExactParent(
        string fallbackText
    )
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        const string sourceText = "token: test-user\nsecond line";
        await File.WriteAllTextAsync(
            candidates,
            Original("raw-1", sourceText, "0x10"),
            cancellationToken
        );
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                fallbackText,
                "0x10",
                outcome: "unchanged",
                integrity: "preservation-fallback",
                integrityReason: "hard-identifier-retention-mismatch"
            ),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("exact parent text", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetDirectories(scope.DirectoryPath, ".bstrings-fallback-text.*"));
    }

    [Fact]
    public async Task ValidateAsync_RejectsFallbackTextThatOnlyMatchesAfterNormalization()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        const string sourceText = "caf\u00e9 test-user";
        const string normalizedDifferently = "cafe\u0301 test-user";
        await File.WriteAllTextAsync(
            candidates,
            Original("raw-1", sourceText, "0x10"),
            cancellationToken
        );
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                normalizedDifferently,
                "0x10",
                outcome: "unchanged",
                integrity: "preservation-fallback",
                integrityReason: "hard-identifier-retention-mismatch"
            ),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("exact parent text", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsFallbackWithoutReason()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                "bonjour",
                "0x10",
                outcome: "unchanged",
                integrity: "preservation-fallback"
            ),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("translationIntegrityReason", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateAsync_RejectsFallbackThatClaimsTranslatedOutcome()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                "bonjour",
                "0x10",
                outcome: "translated",
                integrity: "preservation-fallback",
                integrityReason: "hard-identifier-retention-mismatch"
            ),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("outcome 'unchanged'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1.5)]
    public async Task ValidateAsync_RejectsInvalidAmbiguousIdentifierCount(double count)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "bonjour", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                "hello",
                "0x10",
                integrity: "source-retained-ambiguous",
                ambiguousIdentifierCount: count
            ),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("positive integer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsMissingAmbiguousIdentifierCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(
            candidates,
            Original("raw-1", "Software-Angebotsmesse", "0x10"),
            cancellationToken
        );
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                "configuration profiles",
                "0x10",
                integrity: "source-retained-ambiguous"
            ),
            cancellationToken
        );

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCompletionCore.ValidateAsync(
                candidates,
                translations,
                scope.DirectoryPath,
                1,
                Requirements,
                cancellationToken
            )
        );

        Assert.Contains("positive integer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_AcceptsPositiveAmbiguousIdentifierCount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var candidates = scope.PathFor("candidates.jsonl");
        var translations = scope.PathFor("translations.jsonl");
        await File.WriteAllTextAsync(candidates, Original("raw-1", "Software-Angebotsmesse", "0x10"), cancellationToken);
        await File.WriteAllTextAsync(
            translations,
            Translation(
                "translated-1",
                "raw-1",
                "configuration profiles",
                "0x10",
                integrity: "source-retained-ambiguous",
                ambiguousIdentifierCount: 1
            ),
            cancellationToken
        );

        var stats = await TranslationCompletionCore.ValidateAsync(
            candidates,
            translations,
            scope.DirectoryPath,
            1,
            Requirements,
            cancellationToken
        );

        Assert.Equal(new TranslationCompletionStats(1, 1), stats);
    }

    [Fact]
    public void FallbackTextValidator_DoesNotIndexNonFallbackCandidateText()
    {
        using var scope = new TemporaryDirectory();
        using var validator = new DiskBackedFallbackTextValidator(scope.DirectoryPath);
        const string exactText = "retain CASE_TOKEN001 exactly";
        validator.AddFallback("fallback-parent", "fallback-child", exactText);
        var indexedBytes = Directory
            .GetFiles(validator.WorkingDirectory)
            .Sum(path => new FileInfo(path).Length);

        for (var index = 0; index < 10_000; index++)
        {
            Assert.False(
                validator.ValidateParent(
                    $"ordinary-parent-{index}",
                    new string('x', 4096)
                )
            );
        }

        var bytesAfterNonFallbacks = Directory
            .GetFiles(validator.WorkingDirectory)
            .Sum(path => new FileInfo(path).Length);
        Assert.Equal(indexedBytes, bytesAfterNonFallbacks);
        Assert.True(validator.ValidateParent("fallback-parent", exactText));
        validator.ValidateComplete(requireChildReplay: false);
    }

    private static string Original(string recordId, string text, string location) =>
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "sample.bin",
                location = new { kind = "file_offset", value = location },
                origin = new { extractor = "bstrings", version = "1.9.0", kind = "static" },
            }
        );

    private static string Translation(
        string recordId,
        string parentRecordId,
        string text,
        string location,
        string model = "synthetic/model",
        string? outcome = "translated",
        string? integrity = "verified",
        string? integrityReason = null,
        object? ambiguousIdentifierCount = null
    )
    {
        Dictionary<string, object>? attributes = null;
        if (integrity is not null)
        {
            attributes = new Dictionary<string, object>
            {
                ["translationIntegrity"] = integrity,
            };
            if (integrityReason is not null)
            {
                attributes["translationIntegrityReason"] = integrityReason;
            }
            if (ambiguousIdentifierCount is not null)
            {
                attributes["translationAmbiguousIdentifierCount"] = ambiguousIdentifierCount;
            }
        }
        return JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "sample.bin",
                location = new { kind = "file_offset", value = location },
                origin = new { extractor = "bstrings", version = "1.9.0", kind = "static" },
                parentRecordId,
                transform = new
                {
                    kind = "translation",
                    engine = "llama.cpp",
                    engineVersion = "synthetic",
                    model,
                    revision = "revision-1",
                    modelSha256 = new string('a', 64),
                    sourceLanguage = "auto",
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
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-translation-completion-tests",
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
