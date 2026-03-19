#nullable enable

using System;
using System.Collections.Generic;

namespace bstrings;

internal sealed record SearchTargetConfigurationResult(
    IReadOnlySet<string> FileStrings,
    IReadOnlySet<string> RegexStrings,
    IReadOnlyList<string> MissingFiles
);

internal static class SearchTargetConfigurationCore
{
    public static SearchTargetConfigurationResult Build(
        string? literalString,
        string? literalRegex,
        string? stringsFilePath,
        string? regexFilePath,
        IEnumerable<string> parsedRegexPatterns,
        Func<string, bool> fileExists,
        Func<string, string[]> readAllLines
    )
    {
        var fileStrings = new HashSet<string>();
        var regexStrings = new HashSet<string>();
        var missingFiles = new List<string>();

        if (!string.IsNullOrEmpty(literalString))
        {
            fileStrings.Add(literalString);
        }

        if (!string.IsNullOrEmpty(literalRegex))
        {
            regexStrings.UnionWith(parsedRegexPatterns);
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
                regexStrings.UnionWith(readAllLines(regexFilePath));
            }
            else
            {
                missingFiles.Add($"Regex file '{regexFilePath}' not found");
            }
        }

        return new SearchTargetConfigurationResult(fileStrings, regexStrings, missingFiles);
    }
}