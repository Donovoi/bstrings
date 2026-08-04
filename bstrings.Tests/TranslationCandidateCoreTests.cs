using System.Text.Json;
using Xunit;

namespace bstrings.Tests;

public sealed class TranslationCandidateCoreTests
{
    [Fact]
    public async Task FilterEligibleAsync_UsesConfiguredLengthAndUnicodeLetters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var input = scope.PathFor("raw.jsonl");
        var output = scope.PathFor("candidates.jsonl");
        await File.WriteAllLinesAsync(
            input,
            [
                Original("short", "ab"),
                Original("digits", "12345"),
                Original("latin", "hello"),
                Original("cyrillic", "Привет"),
                Original("astral", "\U00010400ab"),
                Original("long", "abcdefghijk"),
            ],
            cancellationToken
        );

        var stats = await TranslationCandidateCore.FilterEligibleAsync(
            input,
            output,
            minimumCharacters: 3,
            maximumCharacters: 10,
            cancellationToken
        );

        Assert.Equal(new TranslationCandidateStats(6, 3), stats);
        var ids = new List<string>();
        foreach (var line in await File.ReadAllLinesAsync(output, cancellationToken))
        {
            using var document = JsonDocument.Parse(line);
            ids.Add(document.RootElement.GetProperty("recordId").GetString()!);
        }
        Assert.Equal(["latin", "cyrillic", "astral"], ids);
    }

    [Fact]
    public async Task FilterEligibleAsync_PreservesOldOutputWhenInputIsInvalid()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var input = scope.PathFor("raw.jsonl");
        var output = scope.PathFor("candidates.jsonl");
        await File.WriteAllLinesAsync(
            input,
            [Original("valid", "hello"), "{not-json"],
            cancellationToken
        );
        await File.WriteAllTextAsync(output, "previous", cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationCandidateCore.FilterEligibleAsync(input, output, 1, 100, cancellationToken)
        );

        Assert.Equal("previous", await File.ReadAllTextAsync(output, cancellationToken));
        Assert.Empty(Directory.GetFiles(scope.DirectoryPath, "*.partial.*"));
    }

    [Fact]
    public async Task FilterEligibleAsync_CountsUnicodeScalarsLikeTheTranslationAdapter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var input = scope.PathFor("raw.jsonl");
        var output = scope.PathFor("candidates.jsonl");
        await File.WriteAllTextAsync(
            input,
            Original("astral", "\U00010400ab"),
            cancellationToken
        );

        var stats = await TranslationCandidateCore.FilterEligibleAsync(
            input,
            output,
            minimumCharacters: 3,
            maximumCharacters: 3,
            cancellationToken
        );

        Assert.Equal(1, stats.CandidateRecords);
    }

    private static string Original(string recordId, string text) =>
        JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                recordType = "string",
                recordId,
                text,
                sourceFile = "sample.bin",
                location = new { kind = "file_offset", value = "0x10" },
                origin = new { extractor = "bstrings", version = "1.9.0", kind = "static" },
            }
        );

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-candidate-tests",
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
