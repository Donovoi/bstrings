using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace bstrings;

internal readonly record struct ParsedHit(string RawHit, string Data, string Offset);

internal readonly record struct RegexOutputRecord(
    string PatternName,
    string DataFound,
    string SourceFile,
    string Offset,
    string PatternType
);

internal static class RegexOutputCore
{
    internal const string CsvHeader = "Name of search pattern,Data found,Source file,Offset,Pattern type";
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
    private static readonly ConcurrentDictionary<RegexCacheKey, Regex> RegexCache = new();

    internal static ParsedHit ParseHit(string hit, bool includeOffset)
    {
        if (!includeOffset || string.IsNullOrEmpty(hit))
        {
            return new ParsedHit(hit, hit, string.Empty);
        }

        var tabIndex = hit.IndexOf('\t');
        if (tabIndex > 0 && hit.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedHit(hit, hit[(tabIndex + 1)..], hit[..tabIndex]);
        }

        return new ParsedHit(hit, hit, string.Empty);
    }

    internal static IEnumerable<RegexOutputRecord> CreateRecords(
        ParsedHit parsedHit,
        string patternName,
        Regex regex,
        bool regexOutput,
        string sourceFile,
        string patternType
    )
    {
        if (regexOutput)
        {
            var outputGroup =
                BuiltInPatternCatalog.TryGetDefinition(
                    patternName,
                    regex.ToString(),
                    out var definition
                )
                    ? definition.OutputGroup
                    : null;

            foreach (Match match in regex.Matches(parsedHit.Data))
            {
                var dataFound =
                    outputGroup is not null && match.Groups[outputGroup].Success
                        ? match.Groups[outputGroup].Value
                        : match.Value;
                yield return new RegexOutputRecord(
                    patternName,
                    dataFound,
                    sourceFile,
                    parsedHit.Offset,
                    patternType
                );
            }

            yield break;
        }

        yield return new RegexOutputRecord(
            patternName,
            parsedHit.Data,
            sourceFile,
            parsedHit.Offset,
            patternType
        );
    }

    internal static string BuildCsvLine(RegexOutputRecord record)
    {
        return string.Join(
            ",",
            CsvEscape(record.PatternName),
            CsvEscape(record.DataFound),
            CsvEscape(record.SourceFile),
            CsvEscape(record.Offset),
            CsvEscape(record.PatternType)
        );
    }

    internal static string BuildRegexOnlyText(RegexOutputRecord record)
    {
        return string.IsNullOrEmpty(record.Offset)
            ? record.DataFound
            : $"{record.DataFound}\t~{record.Offset}";
    }

    internal static string BuildFullHitText(ParsedHit parsedHit)
    {
        return string.IsNullOrEmpty(parsedHit.Offset) ? parsedHit.Data : parsedHit.RawHit;
    }

    internal static Dictionary<string, Regex> BuildRegexMap(
        IEnumerable<(string name, string pattern)> regexPatternsWithNames
    )
    {
        // Built-in aliases are deduplicated case-insensitively by SearchCore.
        // Custom regex text is case-sensitive, so its map key must be ordinal.
        var regexes = new Dictionary<string, Regex>(StringComparer.Ordinal);
        foreach (var (name, pattern) in regexPatternsWithNames)
        {
            if (string.IsNullOrWhiteSpace(name) || regexes.ContainsKey(name))
            {
                continue;
            }

            regexes.Add(name, GetOrCreateRegex(name, pattern));
        }

        return regexes;
    }

    internal static Regex GetOrCreateRegex(string name, string pattern)
    {
        var options = RegexOptions.CultureInvariant;
        if (BuiltInPatternCatalog.TryGetDefinition(name, pattern, out var definition))
        {
            options |= definition.Options;
            options |= definition.UseNonBacktracking
                ? RegexOptions.NonBacktracking
                : RegexOptions.Compiled;
        }
        else
        {
            // Preserve normal .NET regex semantics for user-supplied expressions.
            // Callers can request case-insensitive or free-spacing behavior with
            // explicit inline options such as (?i) and (?x).
            options |= RegexOptions.Compiled;
        }

        var cacheKey = new RegexCacheKey(pattern, options);
        return RegexCache.GetOrAdd(
            cacheKey,
            static key => new Regex(key.Pattern, key.Options, MatchTimeout)
        );
    }

    internal static bool TryGetRapidsSupersetPattern(
        string name,
        string pattern,
        out string rapidsPattern
    )
    {
        if (
            BuiltInPatternCatalog.TryGetDefinition(name, pattern, out var definition)
            && !string.IsNullOrWhiteSpace(definition.RapidsSupersetPattern)
            && RapidsRegexPolicy.IsSupportedSuperset(
                definition.RapidsSupersetPattern,
                out _
            )
        )
        {
            rapidsPattern = definition.RapidsSupersetPattern;
            return true;
        }

        rapidsPattern = string.Empty;
        return false;
    }

    private static string CsvEscape(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }

    private readonly record struct RegexCacheKey(string Pattern, RegexOptions Options);
}
