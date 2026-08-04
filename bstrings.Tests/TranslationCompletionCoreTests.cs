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

        Assert.Contains("2 translated child", error.Message, StringComparison.OrdinalIgnoreCase);
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
        string? outcome = "translated"
    ) =>
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
            }
        );

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
