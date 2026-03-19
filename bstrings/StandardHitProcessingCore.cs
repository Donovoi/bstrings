#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace bstrings;

internal sealed record CompiledSearchPattern(string Name, string Pattern, Regex Regex);

internal sealed record MatchedHit(
    string RawHit,
    string DataFound,
    string Offset,
    string PatternName,
    string PatternType,
    string SourceFile
);

internal static class StandardHitProcessingCore
{
    public static List<CompiledSearchPattern> CompileRegexTargets(
        IEnumerable<string> regexStrings,
        Func<string, string?> resolvePatternName,
        Action<string, string>? invalidRegexLogger = null
    )
    {
        var compiledRegexes = new List<CompiledSearchPattern>();

        foreach (var pattern in regexStrings)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            try
            {
                compiledRegexes.Add(
                    new CompiledSearchPattern(
                        resolvePatternName(pattern) ?? pattern,
                        pattern,
                        new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)
                    )
                );
            }
            catch (Exception ex)
            {
                invalidRegexLogger?.Invoke(pattern, ex.Message);
            }
        }

        return compiledRegexes;
    }

    public static MatchedHit? TryMatchHit(
        string hit,
        IReadOnlyCollection<string> fileStrings,
        IReadOnlyCollection<CompiledSearchPattern> compiledRegexes,
        bool includeOffset,
        string? currentFile
    )
    {
        if (string.IsNullOrEmpty(hit))
        {
            return null;
        }

        var parsedHit = RegexOutputCore.ParseHit(hit, includeOffset);
        var sourceFile = currentFile ?? string.Empty;

        if (fileStrings.Count > 0)
        {
            foreach (var fileString in fileStrings)
            {
                if (string.IsNullOrWhiteSpace(fileString))
                {
                    continue;
                }

                if (
                    parsedHit.Data.IndexOf(
                        fileString,
                        StringComparison.InvariantCultureIgnoreCase
                    ) >= 0
                )
                {
                    return new MatchedHit(
                        hit,
                        parsedHit.Data,
                        parsedHit.Offset,
                        fileString,
                        "String",
                        sourceFile
                    );
                }
            }

            return null;
        }

        if (compiledRegexes.Count > 0)
        {
            foreach (var regexTarget in compiledRegexes)
            {
                if (regexTarget.Regex.IsMatch(parsedHit.Data))
                {
                    return new MatchedHit(
                        hit,
                        parsedHit.Data,
                        parsedHit.Offset,
                        regexTarget.Name,
                        "Regex",
                        sourceFile
                    );
                }
            }

            return null;
        }

        return new MatchedHit(hit, parsedHit.Data, parsedHit.Offset, string.Empty, string.Empty, sourceFile);
    }

    public static bool WriteMatchedHit(
        MatchedHit matchedHit,
        bool isCsvOutput,
        bool csvHeaderWritten,
        TextWriter? writer
    )
    {
        if (writer is null)
        {
            return csvHeaderWritten;
        }

        if (isCsvOutput)
        {
            if (!csvHeaderWritten)
            {
                writer.WriteLine(RegexOutputCore.CsvHeader);
                csvHeaderWritten = true;
            }

            writer.WriteLine(
                RegexOutputCore.BuildCsvLine(
                    new RegexOutputRecord(
                        matchedHit.PatternName,
                        matchedHit.DataFound,
                        matchedHit.SourceFile,
                        matchedHit.Offset,
                        matchedHit.PatternType
                    )
                )
            );
        }
        else
        {
            writer.WriteLine(matchedHit.RawHit);
        }

        return csvHeaderWritten;
    }
}