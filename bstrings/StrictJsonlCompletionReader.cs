#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace bstrings;

internal readonly record struct StrictJsonlCompletionLine(long LineNumber, string Json);

internal static class StrictJsonlCompletionReader
{
    private const int BufferCharacters = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    internal static async IAsyncEnumerable<StrictJsonlCompletionLine> ReadAsync(
        string path,
        string description,
        int maximumLineCharacters,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (maximumLineCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLineCharacters));
        }

        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"{description} was not found.", fullPath);
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
            detectEncodingFromByteOrderMarks: false,
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
                int read;
                try
                {
                    read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidDataException($"{description} is not valid UTF-8.", ex);
                }
                if (read == 0)
                {
                    break;
                }

                var consumed = 0;
                while (consumed < read)
                {
                    var newlineOffset = buffer.AsSpan(consumed, read - consumed).IndexOf('\n');
                    var segmentLength = newlineOffset < 0 ? read - consumed : newlineOffset;
                    EnsureBounded(
                        pending.Length,
                        segmentLength,
                        maximumLineCharacters,
                        description,
                        lineNumber + 1
                    );
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
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        throw new InvalidDataException(
                            $"{description} line {lineNumber:N0} is empty."
                        );
                    }
                    yield return new StrictJsonlCompletionLine(lineNumber, line);
                }
            }

            if (pending.Length != 0)
            {
                throw new InvalidDataException(
                    $"{description} has an unterminated final JSONL record."
                );
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static void EnsureBounded(
        int currentLength,
        int additionalLength,
        int maximumLineCharacters,
        string description,
        long lineNumber
    )
    {
        if (
            additionalLength > maximumLineCharacters
            || currentLength > maximumLineCharacters - additionalLength
        )
        {
            throw new InvalidDataException(
                $"{description} line {lineNumber:N0} exceeds the "
                    + $"{maximumLineCharacters:N0}-character safety limit."
            );
        }
    }
}
