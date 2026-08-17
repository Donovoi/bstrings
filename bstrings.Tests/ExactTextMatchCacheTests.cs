using Xunit;

namespace bstrings.Tests;

public sealed class ExactTextMatchCacheTests
{
    [Fact]
    public void Probe_RequiresASecondExactObservationBeforeResultAdmission()
    {
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(4, 4096, 128, 8, 2)
        );

        Assert.Equal(
            MatchCacheProbeResult.MissObserved,
            cache.Probe("alpha", out var first)
        );
        Assert.Empty(first);
        Assert.Equal(
            MatchCacheProbeResult.MissEligible,
            cache.Probe("alpha", out var second)
        );
        Assert.Empty(second);
        Assert.True(
            cache.TryAdd("alpha", [new CachedRegexMatch(0, 1, 2)]).Stored
        );
        Assert.Equal(MatchCacheProbeResult.Hit, cache.Probe("alpha", out var hit));
        Assert.Single(hit);
    }

    [Fact]
    public void Probe_ClearsFullProbationWithoutConfusingAReobservedValue()
    {
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(2, 4096, 128, 8, 1)
        );

        Assert.Equal(MatchCacheProbeResult.MissObserved, cache.Probe("alpha", out _));
        Assert.Equal(MatchCacheProbeResult.MissBypassed, cache.Probe("bravo", out _));
        Assert.Equal(MatchCacheProbeResult.MissBypassed, cache.Probe("alpha", out _));
        Assert.Equal(MatchCacheProbeResult.MissBypassed, cache.Probe("bravo", out _));
    }

    [Fact]
    public void Probe_ResamplesAfterTheBoundedProbationCooldown()
    {
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(4, 4096, 128, 8, 1, 1)
        );

        Assert.Equal(MatchCacheProbeResult.MissObserved, cache.Probe("alpha", out _));
        Assert.Equal(MatchCacheProbeResult.MissBypassed, cache.Probe("bravo", out _));
        Assert.Equal(MatchCacheProbeResult.MissBypassed, cache.Probe("charlie", out _));
        Assert.Equal(MatchCacheProbeResult.MissObserved, cache.Probe("delta", out _));
    }

    [Fact]
    public void TryAddAndGet_UsesExactOrdinalIdentityUnderHashCollisions()
    {
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(4, 4096, 128, 8),
            new ConstantHashOrdinalComparer()
        );
        var first = new[] { new CachedRegexMatch(0, 1, 2) };
        var second = new[] { new CachedRegexMatch(1, 3, 4) };

        Assert.True(cache.TryAdd("alpha", first).Stored);
        Assert.True(cache.TryAdd("bravo", second).Stored);
        Assert.True(cache.TryGet("alpha", out var foundFirst));
        Assert.True(cache.TryGet("bravo", out var foundSecond));
        Assert.Equal(first, foundFirst);
        Assert.Equal(second, foundSecond);
        Assert.False(cache.TryGet("charlie", out _));
    }

    [Fact]
    public void TryAdd_EvictsLeastRecentlyUsedEntryWithinBothBounds()
    {
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(2, 4096, 128, 8)
        );

        Assert.True(cache.TryAdd("alpha", []).Stored);
        Assert.True(cache.TryAdd("bravo", []).Stored);
        Assert.True(cache.TryGet("alpha", out _));
        var result = cache.TryAdd("charlie", []);

        Assert.True(result.Stored);
        Assert.Equal(1, result.Evictions);
        Assert.True(cache.TryGet("alpha", out _));
        Assert.False(cache.TryGet("bravo", out _));
        Assert.True(cache.TryGet("charlie", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TryAdd_CachesZeroMatchesAndRejectsOversizedKeysOrResults()
    {
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(4, 4096, 5, 1)
        );

        Assert.True(cache.TryAdd("short", []).Stored);
        Assert.True(cache.TryGet("short", out var empty));
        Assert.Empty(empty);
        Assert.False(cache.TryAdd("longer", []).Stored);
        Assert.False(
            cache.TryAdd(
                "other",
                [new CachedRegexMatch(0, 0, 1), new CachedRegexMatch(0, 1, 1)]
            ).Stored
        );
    }

    [Fact]
    public void TryAdd_KeepsUnicodeNormalizationFormsDistinct()
    {
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(4, 4096, 128, 8)
        );

        Assert.True(cache.TryAdd("caf\u00e9", [new CachedRegexMatch(0, 3, 1)]).Stored);
        Assert.True(cache.TryAdd("cafe\u0301", [new CachedRegexMatch(0, 3, 2)]).Stored);
        Assert.True(cache.TryGet("caf\u00e9", out var composed));
        Assert.True(cache.TryGet("cafe\u0301", out var decomposed));
        Assert.NotEqual(composed[0].Length, decomposed[0].Length);
    }

    [Fact]
    public void TryAdd_RejectsAnEntryThatExceedsTheLogicalByteLimit()
    {
        var charge = ExactTextMatchCache.EstimateLogicalCharge(16, 1);
        var cache = new ExactTextMatchCache(
            new MatchResultCacheOptions(4, charge - 1, 128, 8)
        );

        var result = cache.TryAdd(
            new string('x', 16),
            [new CachedRegexMatch(0, 0, 1)]
        );

        Assert.False(result.Stored);
        Assert.Equal(charge, result.LogicalCharge);
        Assert.Equal(0, cache.Count);
    }

    private sealed class ConstantHashOrdinalComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right) =>
            string.Equals(left, right, StringComparison.Ordinal);

        public int GetHashCode(string value) => 1;
    }
}
