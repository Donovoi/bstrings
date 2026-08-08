#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal static class NativeInputTrustCore
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const int MaximumManifestLineCharacters = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static IReadOnlyList<InputManifestEntry> ReadAndBindManifest(
        string manifestPath,
        IReadOnlyList<string> inventory
    )
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var fullPath = Path.GetFullPath(manifestPath);
        AnalysisOrchestrator.EnsureNoReparsePoints(fullPath, "evidence content manifest");
        var entries = new List<InputManifestEntry>(inventory.Count);
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new StreamReader(
                stream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: false
            );
            while (reader.ReadLine() is { } line)
            {
                var lineNumber = entries.Count + 1;
                if (line.Length == 0 || line.Length > MaximumManifestLineCharacters)
                {
                    throw new InvalidDataException(
                        $"Evidence manifest line {lineNumber:N0} is empty or exceeds the safety limit."
                    );
                }

                InputManifestEntry entry;
                try
                {
                    entry =
                        JsonSerializer.Deserialize<InputManifestEntry>(line, CompactJson)
                        ?? throw new JsonException("The manifest entry was null.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException(
                        $"Evidence manifest line {lineNumber:N0} is invalid: {ex.Message}",
                        ex
                    );
                }

                ValidateEntry(entry, lineNumber);
                if (
                    lineNumber > inventory.Count
                    || !string.Equals(entry.Path, inventory[lineNumber - 1], StringComparison.Ordinal)
                )
                {
                    throw new InvalidDataException(
                        $"Evidence manifest line {lineNumber:N0} does not match the input inventory path and order."
                    );
                }
                entries.Add(entry);
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException(
                $"Evidence manifest '{manifestPath}' is not valid UTF-8.",
                ex
            );
        }

        if (entries.Count != inventory.Count)
        {
            throw new InvalidDataException(
                $"Evidence manifest count does not match the input inventory: expected {inventory.Count:N0}, found {entries.Count:N0}."
            );
        }
        return entries;
    }

    internal static async Task<FileStream> OpenVerifiedSourceAsync(
        InputManifestEntry expected,
        CancellationToken cancellationToken = default
    )
    {
        ValidateEntry(expected, 1);
        AnalysisOrchestrator.EnsureNoReparsePoints(expected.Path, "native evidence source");
        var attributes = File.GetAttributes(expected.Path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidDataException(
                $"Native evidence source is not a regular file: '{expected.Path}'."
            );
        }

        var stream = new FileStream(
            expected.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        try
        {
            await VerifyIdentityAsync(stream, expected, cancellationToken);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    internal static async Task VerifyIdentityAsync(
        FileStream stream,
        InputManifestEntry expected,
        CancellationToken cancellationToken = default
    )
    {
        if (stream.Length != expected.Length)
        {
            throw IdentityMismatch(expected, stream.Length, null);
        }
        stream.Position = 0;
        var actualSha256 = Convert
            .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        var actualLength = stream.Length;
        if (
            actualLength != expected.Length
            || !string.Equals(actualSha256, expected.Sha256, StringComparison.Ordinal)
        )
        {
            throw IdentityMismatch(expected, actualLength, actualSha256);
        }
    }

    private static InvalidDataException IdentityMismatch(
        InputManifestEntry expected,
        long actualLength,
        string? actualSha256
    ) =>
        new(
            $"Native evidence identity mismatch for '{expected.Path}'. Expected {expected.Length:N0} bytes / SHA-256 {expected.Sha256}; found {actualLength:N0} bytes"
                + (actualSha256 is null ? "." : $" / SHA-256 {actualSha256}.")
        );

    private static void ValidateEntry(InputManifestEntry entry, int lineNumber)
    {
        if (
            entry.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(entry.Path)
            || !Path.IsPathFullyQualified(entry.Path)
            || entry.Length < 0
            || entry.Sha256.Length != 64
            || !IsLowerHex(entry.Sha256)
            || !string.Equals(Path.GetFullPath(entry.Path), entry.Path, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"Evidence manifest line {lineNumber:N0} has invalid identity fields."
            );
        }
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
        {
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
            {
                return false;
            }
        }
        return true;
    }
}
