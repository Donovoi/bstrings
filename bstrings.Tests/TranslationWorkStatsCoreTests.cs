using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace bstrings.Tests;

public sealed class TranslationWorkStatsCoreTests
{
    private const string ValidPayload =
        "{\"candidateOccurrences\":2,\"modelFallbacks\":0,\"modelResults\":1,"
        + "\"preservationFallbackChildOccurrences\":0,\"protectedOnlyBypassTexts\":0,"
        + "\"runCacheHits\":1,\"schemaVersion\":1,\"textDecisions\":2,"
        + "\"translatedChildOccurrences\":2,\"translationCacheHits\":0,"
        + "\"translatorInputTexts\":1,\"translatorRequests\":1}\n";

    [Fact]
    public async Task ValidateAsync_AcceptsAndHashesReconciledCounters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var path = scope.PathFor("translation-work-stats.json");
        await File.WriteAllTextAsync(path, ValidPayload, new UTF8Encoding(false), cancellationToken);

        var stats = await TranslationWorkStatsCore.ValidateAsync(path, 2, cancellationToken);

        Assert.Equal("translation-work-stats.json", stats.File);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ValidPayload)))
                .ToLowerInvariant(),
            stats.Sha256
        );
        Assert.Equal(2, stats.CandidateOccurrences);
        Assert.Equal(2, stats.TextDecisions);
        Assert.Equal(1, stats.RunCacheHits);
        Assert.Equal(1, stats.TranslatorRequests);
        Assert.Equal(1, stats.TranslatorInputTexts);
        Assert.Equal(1, stats.ModelResults);
        Assert.Equal(2, stats.TranslatedChildOccurrences);
    }

    [Fact]
    public async Task ValidateAsync_RejectsCandidateCardinalityMismatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var path = scope.PathFor("translation-work-stats.json");
        await File.WriteAllTextAsync(path, ValidPayload, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationWorkStatsCore.ValidateAsync(path, 3, cancellationToken)
        );
        Assert.Contains("candidate cardinality", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":1}", "missing")]
    [InlineData("{\"schemaVersion\":1,\"schemaVersion\":1}", "duplicate")]
    [InlineData("{\"schemaVersion\":1,\"unexpected\":0}", "unknown")]
    public async Task ValidateAsync_RejectsNonExactPropertySets(string payload, string expected)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var path = scope.PathFor("translation-work-stats.json");
        await File.WriteAllTextAsync(path, payload, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationWorkStatsCore.ValidateAsync(path, 0, cancellationToken)
        );
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateAsync_RejectsUnreconciledDecisionBuckets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var scope = new TemporaryDirectory();
        var path = scope.PathFor("translation-work-stats.json");
        var payload = ValidPayload.Replace(
            "\"runCacheHits\":1",
            "\"runCacheHits\":0",
            StringComparison.Ordinal
        );
        await File.WriteAllTextAsync(path, payload, cancellationToken);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            TranslationWorkStatsCore.ValidateAsync(path, 2, cancellationToken)
        );
        Assert.Contains("decisions", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateAtomicDestination_RejectsAnExistingDirectory()
    {
        using var scope = new TemporaryDirectory();
        var path = scope.PathFor("translation-work-stats.json");
        Directory.CreateDirectory(path);

        var exception = Assert.Throws<InvalidDataException>(() =>
            TranslationWorkStatsCore.ValidateAtomicDestination(path)
        );
        Assert.Contains("physical file", exception.Message, StringComparison.Ordinal);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(),
            "bstrings-translation-work-tests-" + Guid.NewGuid().ToString("N")
        );

        internal TemporaryDirectory() => Directory.CreateDirectory(_path);

        internal string PathFor(string name) => Path.Combine(_path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_path, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
