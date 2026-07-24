using System;
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
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(10);

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
            foreach (Match match in regex.Matches(parsedHit.Data))
            {
                yield return new RegexOutputRecord(
                    patternName,
                    match.Value,
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
        var regexes = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, pattern) in regexPatternsWithNames)
        {
            if (string.IsNullOrWhiteSpace(name) || regexes.ContainsKey(name))
            {
                continue;
            }

            regexes.Add(
                name,
                new Regex(
                    pattern,
                    RegexOptions.IgnoreCase
                        | RegexOptions.IgnorePatternWhitespace
                        | RegexOptions.CultureInvariant
                        | RegexOptions.Compiled,
                    MatchTimeout
                )
            );
        }

        return regexes;
    }

    private static string CsvEscape(string value)
    {
        return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
    }
}
