#nullable enable

using System;
using System.IO;
using DiscUtils.Streams;

namespace bstrings;

internal sealed record FileSetupResult(
    MappedStream Stream,
    bool UsedFallback,
    Exception? PrimaryException = null
);

internal static class FileSetupCore
{
    internal static FileStream CreateReadableFileStream(string filePath)
    {
        return System.IO.File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    internal static FileSetupResult SetupMappedStreamForFile(
        string filePath,
        Func<string, Stream>? primaryFactory,
        Func<string, Stream>? fallbackFactory,
        Action<string>? debugLogger = null,
        Action<string>? errorLogger = null,
        Func<Stream, MappedStream>? mapFactory = null
    )
    {
        primaryFactory ??= CreateReadableFileStream;
        mapFactory ??= stream => MappedStream.FromStream(stream, Ownership.Dispose);

        Exception? primaryException = null;
        Stream? primaryStream = null;

        try
        {
            debugLogger?.Invoke("Creating memory map for file...");
            primaryStream = primaryFactory(filePath);
            var mappedStream = mapFactory(primaryStream);
            debugLogger?.Invoke("Memory map created successfully.");
            return new FileSetupResult(mappedStream, UsedFallback: false);
        }
        catch (Exception ex)
        {
            primaryException = ex;
            primaryStream?.Dispose();
            errorLogger?.Invoke($"Failed to create memory map: {ex.Message}");
        }

        if (fallbackFactory is null)
        {
            throw new InvalidOperationException(
                "Fallback stream factory is required when primary setup fails.",
                primaryException
            );
        }

        Stream? fallbackStream = null;

        try
        {
            debugLogger?.Invoke("Falling back to raw file access...");
            fallbackStream = fallbackFactory(filePath);
            debugLogger?.Invoke("Creating memory map from raw stream...");
            var mappedFallbackStream = mapFactory(fallbackStream);
            debugLogger?.Invoke("Raw stream memory map created successfully.");
            return new FileSetupResult(mappedFallbackStream, UsedFallback: true, primaryException);
        }
        catch (Exception fallbackException)
        {
            fallbackStream?.Dispose();
            throw new AggregateException(
                "Failed to set up mapped stream using both primary and fallback paths.",
                primaryException!,
                fallbackException
            );
        }
    }
}