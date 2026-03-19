#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace bstrings;

internal enum InputResolutionStatus
{
    Success,
    NoInput,
    NoFilesFound,
    EmptyRedirectedInput,
    Error,
}

internal sealed record InputResolutionResult(
    InputResolutionStatus Status,
    IReadOnlyList<string> Files,
    string? Message = null,
    Exception? Exception = null,
    string? TempFilePath = null
)
{
    public static InputResolutionResult Success(
        IEnumerable<string> files,
        string? tempFilePath = null
    ) => new(InputResolutionStatus.Success, files.ToList(), null, null, tempFilePath);

    public static InputResolutionResult NoInput(string message) =>
        new(InputResolutionStatus.NoInput, Array.Empty<string>(), message);

    public static InputResolutionResult NoFilesFound(string message) =>
        new(InputResolutionStatus.NoFilesFound, Array.Empty<string>(), message);

    public static InputResolutionResult EmptyRedirectedInput(string message) =>
        new(InputResolutionStatus.EmptyRedirectedInput, Array.Empty<string>(), message);

    public static InputResolutionResult Error(string message, Exception? exception = null) =>
        new(InputResolutionStatus.Error, Array.Empty<string>(), message, exception);
}

internal static class InputResolutionCore
{
    public static InputResolutionResult ResolveExplicitInputs(
        string? filePath,
        string? directoryPath,
        string? mask,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists,
        Func<string, string, IEnumerable<string>> enumerateFiles,
        Func<string, string> getFullPath
    )
    {
        if (!string.IsNullOrEmpty(filePath) && !string.IsNullOrEmpty(directoryPath))
        {
            return InputResolutionResult.Error(
                "Both -f (file) and -d (directory) options were specified. Please use only one. Exiting."
            );
        }

        if (!string.IsNullOrEmpty(filePath))
        {
            if (!fileExists(filePath))
            {
                return InputResolutionResult.Error(
                    $"File specified with -f not found: '{filePath}'. Exiting."
                );
            }

            return InputResolutionResult.Success([getFullPath(filePath)]);
        }

        if (!string.IsNullOrEmpty(directoryPath))
        {
            if (!directoryExists(directoryPath))
            {
                return InputResolutionResult.Error(
                    $"Directory specified with -d not found: '{directoryPath}'. Exiting."
                );
            }

            try
            {
                var fullDirectoryPath = getFullPath(directoryPath);
                var effectiveMask = string.IsNullOrEmpty(mask) ? "*" : mask;
                var files = enumerateFiles(fullDirectoryPath, effectiveMask).ToList();

                if (files.Count == 0)
                {
                    return InputResolutionResult.NoFilesFound(
                        $"No files found in directory '{directoryPath}' matching the specified criteria."
                    );
                }

                return InputResolutionResult.Success(files);
            }
            catch (Exception ex)
            {
                return InputResolutionResult.Error(
                    $"Error enumerating files in directory '{directoryPath}'. Message: {ex.Message}",
                    ex
                );
            }
        }

        return InputResolutionResult.NoInput(
            "A file (-f), directory (-d), or piped input is required. Exiting."
        );
    }

    public static InputResolutionResult CaptureRedirectedInput(
        Func<Stream> openStandardInput,
        Func<string> getTempFileName,
        Func<string, Stream> openFileForWrite,
        Func<string, long> getFileLength,
        Action<string> deleteFile,
        Func<string, string> getFullPath
    )
    {
        var tempFilePath = string.Empty;

        try
        {
            tempFilePath = getTempFileName();

            using (var stdinStream = openStandardInput())
            using (var tempFileStream = openFileForWrite(tempFilePath))
            {
                stdinStream.CopyTo(tempFileStream);
            }

            if (getFileLength(tempFilePath) == 0)
            {
                deleteFile(tempFilePath);
                return InputResolutionResult.EmptyRedirectedInput(
                    "Stdin was redirected, but no data was received. Exiting."
                );
            }

            return InputResolutionResult.Success([getFullPath(tempFilePath)], tempFilePath);
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
            {
                deleteFile(tempFilePath);
            }

            return InputResolutionResult.Error(
                $"Error reading from stdin or writing to temporary file. Message: {ex.Message}",
                ex
            );
        }
    }
}