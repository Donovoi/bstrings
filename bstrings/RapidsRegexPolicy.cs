using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace bstrings;

internal static partial class RapidsRegexPolicy
{
    private static readonly string[] UnsupportedTokens =
    [
        "(?=",
        "(?!",
        "(?<=",
        "(?<!",
        "(?<",
        "(?>",
        "(?(",
        "(?i",
        "(?m",
        "(?n",
        "(?s",
        "(?x",
        @"\k",
        @"\p{",
        @"\P{",
        @"\z",
        @"\G",
    ];

    internal static bool IsSupportedSuperset(string pattern, out string reason)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            reason = "pattern is empty";
            return false;
        }

        foreach (var token in UnsupportedTokens)
        {
            if (pattern.Contains(token, StringComparison.Ordinal))
            {
                reason = $"unsupported libcudf token '{token}'";
                return false;
            }
        }

        if (MatchingBackreference().IsMatch(pattern))
        {
            reason = "matching backreferences are not supported by libcudf";
            return false;
        }

        foreach (Match match in BoundedQuantifier().Matches(pattern))
        {
            if (
                int.Parse(match.Groups["minimum"].Value) > 999
                || (
                    match.Groups["maximum"].Success
                    && int.Parse(match.Groups["maximum"].Value) > 999
                )
            )
            {
                reason = "libcudf quantifier bounds cannot exceed 999";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    internal static (
        List<(string name, string pattern)> gpu,
        List<(string name, string pattern)> cpu
    ) PartitionPatterns(IEnumerable<(string name, string pattern)> patterns)
    {
        var gpuPatterns = new List<(string name, string pattern)>();
        var cpuPatterns = new List<(string name, string pattern)>();

        foreach (var patternInfo in patterns)
        {
            if (
                RegexOutputCore.TryGetRapidsSupersetPattern(
                    patternInfo.name,
                    patternInfo.pattern,
                    out var rapidsSuperset
                )
            )
            {
                gpuPatterns.Add((patternInfo.name, rapidsSuperset));
            }
            else
            {
                cpuPatterns.Add(patternInfo);
            }
        }

        return (gpuPatterns, cpuPatterns);
    }

    [GeneratedRegex(@"(?<!\\)(?:\\\\)*\\[1-9][0-9]?")]
    private static partial Regex MatchingBackreference();

    [GeneratedRegex(@"\{(?<minimum>[0-9]+)(?:,(?<maximum>[0-9]+)?)?\}")]
    private static partial Regex BoundedQuantifier();
}
