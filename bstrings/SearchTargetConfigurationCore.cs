#nullable enable

using System;
using System.Collections.Generic;

namespace bstrings;

internal sealed record SearchTargetConfigurationResult(
    IReadOnlySet<string> FileStrings,
    IReadOnlySet<string> RegexStrings,
    IReadOnlyList<(string name, string pattern)> RegexPatterns,
    IReadOnlyList<string> MissingFiles
);

internal static class SearchTargetConfigurationCore
{
    public static SearchTargetConfigurationResult Build(
        string? literalString,
        string? literalRegex,
        string? stringsFilePath,
        string? regexFilePath,
        IEnumerable<(string name, string pattern)> parsedRegexPatterns,
        Func<string, bool> fileExists,
        Func<string, string[]> readAllLines
    )
    {
        var fileStrings = new HashSet<string>();
        var regexStrings = new HashSet<string>();
        var regexPatterns = new List<(string name, string pattern)>();
        var missingFiles = new List<string>();

        if (!string.IsNullOrEmpty(literalString))
        {
            fileStrings.Add(literalString);
        }

        if (!string.IsNullOrEmpty(literalRegex))
        {
            foreach (var pattern in parsedRegexPatterns)
            {
                regexPatterns.Add(pattern);
                regexStrings.Add(pattern.pattern);
            }
        }

        if (!string.IsNullOrEmpty(stringsFilePath))
        {
            if (fileExists(stringsFilePath))
            {
                fileStrings.UnionWith(readAllLines(stringsFilePath));
            }
            else
            {
                missingFiles.Add($"Strings file '{stringsFilePath}' not found");
            }
        }

        if (!string.IsNullOrEmpty(regexFilePath))
        {
            if (fileExists(regexFilePath))
            {
                foreach (var pattern in ParseRegexFilePatterns(readAllLines(regexFilePath)))
                {
                    regexPatterns.Add(pattern);
                    regexStrings.Add(pattern.pattern);
                }
            }
            else
            {
                missingFiles.Add($"Regex file '{regexFilePath}' not found");
            }
        }

        return new SearchTargetConfigurationResult(
            fileStrings,
            regexStrings,
            regexPatterns,
            missingFiles
        );
    }

    internal static IReadOnlyList<(string name, string pattern)> ParseRegexFilePatterns(
        IEnumerable<string> lines
    )
    {
        ArgumentNullException.ThrowIfNull(lines);

        var patterns = new List<(string name, string pattern)>();
        var lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            var pattern = line.Trim();
            if (pattern.Length == 0 || pattern.StartsWith('#'))
            {
                continue;
            }

            patterns.Add(($"file:{lineNumber}", pattern));
        }

        return patterns;
    }
}
