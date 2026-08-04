#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace bstrings;

internal readonly record struct EnrichmentJsonlLine(
    long LineNumber,
    string Json,
    EnrichmentStringRecord Record
);

internal static class EnrichmentJsonlReader
{
    private const int BufferCharacters = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    internal static async IAsyncEnumerable<EnrichmentJsonlLine> ReadAsync(
        string path,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Enrichment JSONL input was not found.", fullPath);
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferCharacters,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        using var reader = new StreamReader(
            stream,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: true,
            BufferCharacters,
            leaveOpen: true
        );
        var buffer = ArrayPool<char>.Shared.Rent(BufferCharacters);
        var pending = new StringBuilder();
        long lineNumber = 0;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (read == 0)
                {
                    break;
                }
                var consumed = 0;
                while (consumed < read)
                {
                    var newlineOffset = buffer.AsSpan(consumed, read - consumed).IndexOf('\n');
                    var segmentLength = newlineOffset < 0 ? read - consumed : newlineOffset;
                    EnsureBounded(pending.Length, segmentLength, lineNumber + 1);
                    pending.Append(buffer, consumed, segmentLength);
                    consumed += segmentLength;
                    if (newlineOffset < 0)
                    {
                        continue;
                    }

                    consumed++;
                    lineNumber++;
                    if (pending.Length > 0 && pending[^1] == '\r')
                    {
                        pending.Length--;
                    }
                    var line = pending.ToString();
                    pending.Clear();
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        yield return Parse(line, lineNumber);
                    }
                }
            }

            if (pending.Length > 0)
            {
                lineNumber++;
                if (pending[^1] == '\r')
                {
                    pending.Length--;
                }
                var line = pending.ToString();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return Parse(line, lineNumber);
                }
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void EnsureBounded(int currentLength, int additionalLength, long lineNumber)
    {
        if (
            additionalLength > EnrichmentRegexPipelineCore.MaxJsonLineCharacters
            || currentLength
                > EnrichmentRegexPipelineCore.MaxJsonLineCharacters - additionalLength
        )
        {
            throw new InvalidDataException(
                $"Enrichment JSONL line {lineNumber:N0} exceeds the "
                    + $"{EnrichmentRegexPipelineCore.MaxJsonLineCharacters:N0}-character safety limit."
            );
        }
    }

    private static EnrichmentJsonlLine Parse(string line, long lineNumber)
    {
        EnrichmentStringRecord record;
        try
        {
            record =
                JsonSerializer.Deserialize<EnrichmentStringRecord>(line, JsonOptions)
                ?? throw new JsonException("The record was null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Invalid enrichment JSONL at line {lineNumber:N0}: {ex.Message}",
                ex
            );
        }

        EnrichmentRegexPipelineCore.ValidateRecord(record, lineNumber);
        return new EnrichmentJsonlLine(lineNumber, line, record);
    }
}
