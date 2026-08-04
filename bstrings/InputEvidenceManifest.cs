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

internal sealed record InputManifestInfo(
    long FileCount,
    string InventoryFile,
    string InventorySha256,
    string ManifestFile,
    string ManifestSha256,
    string ContentHashAlgorithm = "sha256"
);

internal sealed record InputManifestEntry
{
    public int SchemaVersion { get; init; } = 1;
    public string Path { get; init; } = string.Empty;
    public long Length { get; init; }
    public string Sha256 { get; init; } = string.Empty;
}

internal static class InputEvidenceManifest
{
    private const int BufferSize = 4 * 1024 * 1024;
    private const int MaximumInventoryLineCharacters = 32_767;
    private const int MaximumManifestLineCharacters = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static async Task<InputManifestInfo> CreateAsync(
        string inventoryPath,
        string manifestPath,
        IEnumerable<string> inputs,
        CancellationToken cancellationToken = default
    )
    {
        var inventoryFullPath = Path.GetFullPath(inventoryPath);
        var manifestFullPath = Path.GetFullPath(manifestPath);
        var inventoryTemporaryPath = inventoryFullPath + ".partial." + Guid.NewGuid().ToString("N");
        var manifestTemporaryPath = manifestFullPath + ".partial." + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(Path.GetDirectoryName(inventoryFullPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(manifestFullPath)!);

        try
        {
            long count = 0;
            using var manifestDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (
                var inventoryWriter = CreateWriter(inventoryTemporaryPath)
            )
            await using (var manifestWriter = CreateWriter(manifestTemporaryPath))
            {
                foreach (var input in inputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(input);
                    ValidateRepresentablePath(fullPath);
                    var identity = await HashFileAsync(fullPath, cancellationToken);
                    var entry = new InputManifestEntry
                    {
                        Path = fullPath,
                        Length = identity.Length,
                        Sha256 = identity.Sha256,
                    };
                    var manifestLine = JsonSerializer.Serialize(entry, CompactJson);
                    if (manifestLine.Length > MaximumManifestLineCharacters)
                    {
                        throw new InvalidDataException(
                            $"Evidence manifest entry for '{fullPath}' exceeds the safety limit."
                        );
                    }

                    await inventoryWriter.WriteLineAsync(fullPath.AsMemory(), cancellationToken);
                    await manifestWriter.WriteLineAsync(manifestLine.AsMemory(), cancellationToken);
                    manifestDigest.AppendData(StrictUtf8.GetBytes(manifestLine));
                    manifestDigest.AppendData("\n"u8);
                    count++;
                }
                await inventoryWriter.FlushAsync(cancellationToken);
                await manifestWriter.FlushAsync(cancellationToken);
            }

            if (count == 0)
            {
                throw new InvalidOperationException(
                    "No evidence files matched the requested input and mask."
                );
            }

            var manifestSha256 = Convert
                .ToHexString(manifestDigest.GetHashAndReset())
                .ToLowerInvariant();
            var inventoryIdentity = await HashFileAsync(
                inventoryTemporaryPath,
                cancellationToken
            );
            File.Move(inventoryTemporaryPath, inventoryFullPath, overwrite: true);
            inventoryTemporaryPath = string.Empty;
            File.Move(manifestTemporaryPath, manifestFullPath, overwrite: true);
            manifestTemporaryPath = string.Empty;
            return new InputManifestInfo(
                count,
                Path.GetFileName(inventoryFullPath),
                inventoryIdentity.Sha256,
                Path.GetFileName(manifestFullPath),
                manifestSha256
            );
        }
        finally
        {
            DeleteTemporary(inventoryTemporaryPath);
            DeleteTemporary(manifestTemporaryPath);
        }
    }

    internal static async Task VerifyInventoryAsync(
        string inventoryPath,
        InputManifestInfo expected,
        CancellationToken cancellationToken = default
    )
    {
        await using var lease = await AcquireVerifiedInventoryLeaseAsync(
            inventoryPath,
            expected,
            cancellationToken
        );
    }

    internal static async Task<FileStream> AcquireVerifiedInventoryLeaseAsync(
        string inventoryPath,
        InputManifestInfo expected,
        CancellationToken cancellationToken = default
    )
    {
        var inventoryFullPath = Path.GetFullPath(inventoryPath);
        if (!File.Exists(inventoryFullPath))
        {
            throw new FileNotFoundException(
                "The evidence input inventory is missing.",
                inventoryFullPath
            );
        }

        AnalysisOrchestrator.EnsureNoReparsePoints(
            inventoryFullPath,
            "evidence input inventory"
        );
        var inventoryStream = new FileStream(
            inventoryFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        try
        {
            await VerifyDigestAsync(
                inventoryStream,
                expected.InventorySha256,
                "Evidence inventory",
                cancellationToken
            );
            inventoryStream.Position = 0;

            long count = 0;
            try
            {
                using var reader = new StreamReader(
                    inventoryStream,
                    StrictUtf8,
                    detectEncodingFromByteOrderMarks: false,
                    BufferSize,
                    leaveOpen: true
                );
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count++;
                    ValidateInventoryEntry(line, count);
                    AnalysisOrchestrator.EnsureNoReparsePoints(
                        line,
                        "evidence inventory path"
                    );
                }
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException(
                    "The evidence input inventory is not valid UTF-8.",
                    ex
                );
            }

            if (count != expected.FileCount)
            {
                throw new InvalidDataException(
                    $"Evidence inventory count changed: expected {expected.FileCount:N0}, found {count:N0}."
                );
            }

            inventoryStream.Position = 0;
            await VerifyDigestAsync(
                inventoryStream,
                expected.InventorySha256,
                "Evidence inventory",
                cancellationToken
            );
            inventoryStream.Position = 0;
            return inventoryStream;
        }
        catch
        {
            await inventoryStream.DisposeAsync();
            throw;
        }
    }

    internal static async Task VerifyAsync(
        string manifestPath,
        InputManifestInfo expected,
        CancellationToken cancellationToken = default
    )
    {
        var manifestFullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(manifestFullPath))
        {
            throw new FileNotFoundException(
                "The evidence content manifest is missing.",
                manifestFullPath
            );
        }

        await using var manifestStream = new FileStream(
            manifestFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        await VerifyDigestAsync(
            manifestStream,
            expected.ManifestSha256,
            "Evidence manifest",
            cancellationToken
        );
        manifestStream.Position = 0;

        long count = 0;
        using (
            var reader = new StreamReader(
                manifestStream,
                StrictUtf8,
                detectEncodingFromByteOrderMarks: true,
                BufferSize,
                leaveOpen: true
            )
        )
        {
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                count++;
                if (line.Length == 0 || line.Length > MaximumManifestLineCharacters)
                {
                    throw new InvalidDataException(
                        $"Evidence manifest line {count:N0} is empty or exceeds the safety limit."
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
                        $"Evidence manifest line {count:N0} is invalid: {ex.Message}",
                        ex
                    );
                }
                ValidateEntry(entry, count);
                AnalysisOrchestrator.EnsureNoReparsePoints(
                    entry.Path,
                    "evidence manifest path"
                );
                var current = await HashFileAsync(entry.Path, cancellationToken);
                if (
                    current.Length != entry.Length
                    || !string.Equals(current.Sha256, entry.Sha256, StringComparison.Ordinal)
                )
                {
                    throw new InvalidDataException(
                        $"Evidence content changed during analysis: '{entry.Path}'. "
                            + $"Expected {entry.Length:N0} bytes / SHA-256 {entry.Sha256}; "
                            + $"found {current.Length:N0} bytes / SHA-256 {current.Sha256}."
                    );
                }
            }
        }

        if (count != expected.FileCount)
        {
            throw new InvalidDataException(
                $"Evidence manifest count changed: expected {expected.FileCount:N0}, found {count:N0}."
            );
        }

        manifestStream.Position = 0;
        await VerifyDigestAsync(
            manifestStream,
            expected.ManifestSha256,
            "Evidence manifest",
            cancellationToken
        );
    }

    private static async Task<FileIdentity> HashFileAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        var lengthBefore = stream.Length;
        var digest = await SHA256.HashDataAsync(stream, cancellationToken);
        var lengthAfter = stream.Length;
        if (lengthAfter != lengthBefore)
        {
            throw new InvalidDataException(
                $"Evidence file changed size while it was being hashed: '{path}'."
            );
        }
        return new FileIdentity(
            lengthAfter,
            Convert.ToHexString(digest).ToLowerInvariant()
        );
    }

    private static async Task VerifyDigestAsync(
        Stream stream,
        string expectedSha256,
        string description,
        CancellationToken cancellationToken
    )
    {
        var actual = Convert
            .ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"{description} SHA-256 mismatch. Expected {expectedSha256}, found {actual}."
            );
        }
    }

    private static StreamWriter CreateWriter(string path)
    {
        var writer = new StreamWriter(
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ),
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true
            ),
            64 * 1024
        );
        writer.NewLine = "\n";
        return writer;
    }

    private static void ValidateRepresentablePath(string path)
    {
        if (path.IndexOfAny(['\r', '\n', '\0']) >= 0 || path.Length > 32_767)
        {
            throw new InvalidDataException(
                $"An evidence path cannot be represented safely in the input inventory: '{path}'."
            );
        }
    }

    private static void ValidateEntry(InputManifestEntry entry, long lineNumber)
    {
        if (
            entry.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(entry.Path)
            || !Path.IsPathFullyQualified(entry.Path)
            || entry.Length < 0
            || entry.Sha256.Length != 64
            || !IsLowerHex(entry.Sha256)
        )
        {
            throw new InvalidDataException(
                $"Evidence manifest line {lineNumber:N0} has invalid identity fields."
            );
        }
        ValidateRepresentablePath(entry.Path);
        if (!string.Equals(Path.GetFullPath(entry.Path), entry.Path, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evidence manifest line {lineNumber:N0} does not contain a canonical path."
            );
        }
    }

    private static void ValidateInventoryEntry(string path, long lineNumber)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || path.Length > MaximumInventoryLineCharacters
            || !Path.IsPathFullyQualified(path)
        )
        {
            throw new InvalidDataException(
                $"Evidence inventory line {lineNumber:N0} does not contain a valid absolute path."
            );
        }
        ValidateRepresentablePath(path);
        if (!string.Equals(Path.GetFullPath(path), path, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evidence inventory line {lineNumber:N0} does not contain a canonical path."
            );
        }
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static void DeleteTemporary(string path)
    {
        if (path.Length == 0)
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private readonly record struct FileIdentity(long Length, string Sha256);
}
