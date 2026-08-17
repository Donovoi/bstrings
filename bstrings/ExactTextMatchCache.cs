#nullable enable

using System;
using System.Collections.Generic;

namespace bstrings;

internal readonly record struct MatchResultCacheOptions(
    int MaximumEntries,
    long MaximumLogicalBytes,
    int MaximumTextCharacters,
    int MaximumDescriptorsPerEntry,
    int MaximumProbationEntries = 2_048,
    int ProbationCooldownRecords = 524_288
)
{
    internal static MatchResultCacheOptions Disabled { get; } = new(0, 0, 0, 0, 0, 0);

    internal static MatchResultCacheOptions Experimental { get; } =
        new(100_000, 64L * 1024 * 1024, 65_536, 4_096, 2_048, 524_288);

    internal static MatchResultCacheOptions Default { get; } = Disabled;

    internal bool Enabled => MaximumEntries > 0 && MaximumLogicalBytes > 0;

    internal void Validate()
    {
        if (
            MaximumEntries < 0
            || MaximumLogicalBytes < 0
            || MaximumTextCharacters < 0
            || MaximumDescriptorsPerEntry < 0
            || MaximumProbationEntries < 0
            || ProbationCooldownRecords < 0
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(MatchResultCacheOptions),
                "Match-cache limits cannot be negative."
            );
        }
        if (
            Enabled
            && (MaximumTextCharacters == 0 || MaximumDescriptorsPerEntry == 0)
        )
        {
            throw new ArgumentException(
                "An enabled match cache requires positive text and descriptor limits."
            );
        }
    }
}

internal readonly record struct CachedRegexMatch(int PatternIndex, int Start, int Length);

internal readonly record struct MatchCacheStoreResult(
    bool Stored,
    int Evictions,
    long LogicalCharge
);

internal enum MatchCacheProbeResult
{
    MissBypassed,
    MissObserved,
    MissEligible,
    Hit,
}

internal sealed class ExactTextMatchCache
{
    private const long EntryLogicalOverhead = 384;
    private const long ProbationLogicalOverhead = 128;
    private const int DescriptorLogicalBytes = 16;

    private readonly MatchResultCacheOptions _options;
    private readonly Dictionary<string, CacheSlot> _entries;
    private readonly LinkedList<Entry> _lru = new();
    private readonly Queue<ProbationToken> _probation = new();
    private long _nextProbationGeneration;
    private int _probationCount;
    private int _probationCooldownRemaining;

    internal ExactTextMatchCache(
        MatchResultCacheOptions options,
        IEqualityComparer<string>? comparer = null
    )
    {
        options.Validate();
        _options = options;
        _entries = new Dictionary<string, CacheSlot>(
            Math.Min(options.MaximumEntries, 4096),
            comparer ?? StringComparer.Ordinal
        );
    }

    internal int Count => _entries.Count;
    internal long CurrentLogicalBytes { get; private set; }
    internal long PeakLogicalBytes { get; private set; }
    internal int PeakEntries { get; private set; }

    internal MatchCacheProbeResult Probe(
        string text,
        out IReadOnlyList<CachedRegexMatch> matches
    )
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_options.Enabled || text.Length > _options.MaximumTextCharacters)
        {
            matches = Array.Empty<CachedRegexMatch>();
            return MatchCacheProbeResult.MissObserved;
        }
        if (_probationCooldownRemaining > 0 && _lru.Count == 0)
        {
            _probationCooldownRemaining--;
            matches = Array.Empty<CachedRegexMatch>();
            return MatchCacheProbeResult.MissBypassed;
        }
        if (_entries.TryGetValue(text, out var slot))
        {
            if (slot.Node is not null)
            {
                _lru.Remove(slot.Node);
                _lru.AddFirst(slot.Node);
                matches = slot.Node.Value.Matches;
                return MatchCacheProbeResult.Hit;
            }

            matches = Array.Empty<CachedRegexMatch>();
            return MatchCacheProbeResult.MissEligible;
        }

        if (_probationCooldownRemaining > 0)
        {
            _probationCooldownRemaining--;
            matches = Array.Empty<CachedRegexMatch>();
            return MatchCacheProbeResult.MissBypassed;
        }
        var observed = ObserveFirstOccurrence(text);
        matches = Array.Empty<CachedRegexMatch>();
        return observed
            ? MatchCacheProbeResult.MissObserved
            : MatchCacheProbeResult.MissBypassed;
    }

    internal bool TryGet(string text, out IReadOnlyList<CachedRegexMatch> matches)
    {
        if (
            !_options.Enabled
            || !_entries.TryGetValue(text, out var slot)
            || slot.Node is null
        )
        {
            matches = Array.Empty<CachedRegexMatch>();
            return false;
        }

        _lru.Remove(slot.Node);
        _lru.AddFirst(slot.Node);
        matches = slot.Node.Value.Matches;
        return true;
    }

    internal MatchCacheStoreResult TryAdd(string text, CachedRegexMatch[] matches)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(matches);
        if (
            !_options.Enabled
            || text.Length > _options.MaximumTextCharacters
            || matches.Length > _options.MaximumDescriptorsPerEntry
        )
        {
            return default;
        }

        var charge = EstimateLogicalCharge(text.Length, matches.Length);
        if (charge > _options.MaximumLogicalBytes)
        {
            return new MatchCacheStoreResult(false, 0, charge);
        }

        if (_entries.TryGetValue(text, out var existing) && existing.Node is not null)
        {
            return new MatchCacheStoreResult(false, 0, charge);
        }
        if (existing.Node is null && existing.ProbationGeneration != 0)
        {
            _entries.Remove(text);
            _probationCount--;
            CurrentLogicalBytes = checked(
                CurrentLogicalBytes - EstimateProbationLogicalCharge(text.Length)
            );
        }

        var evictions = 0;
        while (
            _entries.Count >= _options.MaximumEntries
            || checked(CurrentLogicalBytes + charge) > _options.MaximumLogicalBytes
        )
        {
            if (EvictOldestProbation())
            {
                continue;
            }
            if (!EvictLeastRecentlyUsedResult())
            {
                return new MatchCacheStoreResult(false, evictions, charge);
            }
            evictions++;
        }

        var entry = new Entry(text, matches, charge);
        var node = _lru.AddFirst(entry);
        _entries.Add(text, new CacheSlot(node, 0));
        CurrentLogicalBytes = checked(CurrentLogicalBytes + charge);
        PeakLogicalBytes = Math.Max(PeakLogicalBytes, CurrentLogicalBytes);
        PeakEntries = Math.Max(PeakEntries, _entries.Count);
        return new MatchCacheStoreResult(true, evictions, charge);
    }

    internal static long EstimateLogicalCharge(int textCharacters, int descriptors)
    {
        if (textCharacters < 0 || descriptors < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(textCharacters),
                "Match-cache charge inputs cannot be negative."
            );
        }
        return checked(
            EntryLogicalOverhead
                + (long)textCharacters * sizeof(char)
                + (long)descriptors * DescriptorLogicalBytes
        );
    }

    private bool ObserveFirstOccurrence(string text)
    {
        if (_options.MaximumProbationEntries == 0)
        {
            return false;
        }
        var probationLimit = Math.Min(
            _options.MaximumEntries,
            _options.MaximumProbationEntries
        );
        if (_probationCount >= probationLimit)
        {
            ClearProbation();
            _probationCooldownRemaining = _options.ProbationCooldownRecords;
            return false;
        }
        var charge = EstimateProbationLogicalCharge(text.Length);
        if (charge > _options.MaximumLogicalBytes)
        {
            return false;
        }
        while (
            _entries.Count >= _options.MaximumEntries
            || checked(CurrentLogicalBytes + charge) > _options.MaximumLogicalBytes
        )
        {
            if (!EvictOldestProbation())
            {
                return false;
            }
        }

        var generation = checked(++_nextProbationGeneration);
        _entries.Add(text, new CacheSlot(null, generation));
        _probation.Enqueue(new ProbationToken(text, generation));
        _probationCount++;
        CurrentLogicalBytes = checked(CurrentLogicalBytes + charge);
        UpdatePeaks();
        return true;
    }

    private void ClearProbation()
    {
        while (EvictOldestProbation()) { }
    }

    private bool EvictOldestProbation()
    {
        while (_probation.TryDequeue(out var token))
        {
            if (
                !_entries.TryGetValue(token.Text, out var slot)
                || slot.Node is not null
                || slot.ProbationGeneration != token.Generation
            )
            {
                continue;
            }
            _entries.Remove(token.Text);
            _probationCount--;
            CurrentLogicalBytes = checked(
                CurrentLogicalBytes - EstimateProbationLogicalCharge(token.Text.Length)
            );
            return true;
        }
        return false;
    }

    private bool EvictLeastRecentlyUsedResult()
    {
        var last = _lru.Last;
        if (last is null)
        {
            return false;
        }
        _lru.RemoveLast();
        _entries.Remove(last.Value.Text);
        CurrentLogicalBytes = checked(CurrentLogicalBytes - last.Value.LogicalCharge);
        return true;
    }

    private void UpdatePeaks()
    {
        PeakLogicalBytes = Math.Max(PeakLogicalBytes, CurrentLogicalBytes);
        PeakEntries = Math.Max(PeakEntries, _entries.Count);
    }

    private static long EstimateProbationLogicalCharge(int textCharacters) =>
        checked(ProbationLogicalOverhead + (long)textCharacters * sizeof(char));

    private sealed record Entry(
        string Text,
        CachedRegexMatch[] Matches,
        long LogicalCharge
    );

    private readonly record struct CacheSlot(
        LinkedListNode<Entry>? Node,
        long ProbationGeneration
    );

    private readonly record struct ProbationToken(string Text, long Generation);
}
