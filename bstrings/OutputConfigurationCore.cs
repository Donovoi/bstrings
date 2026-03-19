#nullable enable

using System;

namespace bstrings;

internal sealed record OutputConfigurationResult(
    bool IsEnabled,
    string OutputPath,
    bool IsCsvOutput,
    string? WarningMessage = null
);

internal static class OutputConfigurationCore
{
    public static OutputConfigurationResult Prepare(
        string? outputPath,
        Func<string, string> getFullPath,
        Func<string, string?> getDirectoryName,
        Func<string, bool> directoryExists,
        Action<string> createDirectory
    )
    {
        if (string.IsNullOrEmpty(outputPath))
        {
            return new OutputConfigurationResult(false, string.Empty, false);
        }

        var normalizedPath = getFullPath(outputPath).TrimEnd('\\');
        var directoryPath = getDirectoryName(normalizedPath);

        if (directoryPath is null)
        {
            return new OutputConfigurationResult(
                false,
                string.Empty,
                false,
                $"Invalid path: '{normalizedPath}'. Results will not be saved to a file"
            );
        }

        if (!directoryExists(directoryPath))
        {
            try
            {
                createDirectory(directoryPath);
            }
            catch
            {
                return new OutputConfigurationResult(
                    false,
                    string.Empty,
                    false,
                    $"Invalid path: '{normalizedPath}'. Results will not be saved to a file"
                );
            }
        }

        return new OutputConfigurationResult(
            true,
            normalizedPath,
            normalizedPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        );
    }
}