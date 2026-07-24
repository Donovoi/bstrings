namespace bstrings;

public enum RegexWorkPartition
{
    Patterns,
    Hits,
}

internal static class RegexParallelismPolicy
{
    internal const int HitParallelThreshold = 10_000;

    internal static RegexWorkPartition Choose(
        int hitCount,
        int patternCount,
        int processorCount
    )
    {
        return processorCount > 1
            && patternCount > 0
            && patternCount < processorCount
            && hitCount >= HitParallelThreshold
            ? RegexWorkPartition.Hits
            : RegexWorkPartition.Patterns;
    }
}
