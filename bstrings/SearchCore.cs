using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.RegularExpressions;

namespace bstrings;

internal readonly struct StringHitPosition
{
    public int Start { get; }
    public int Length { get; }
    public long FileOffset { get; }

    public StringHitPosition(int start, int length, long fileOffset)
    {
        Start = start;
        Length = length;
        FileOffset = fileOffset;
    }
}

internal static class SearchCore
{
    internal static List<string> GetUnicodeHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        bool includeOffset,
        string unicodeRange
    )
    {
        return GetStringHitsUnified(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            includeOffset,
            isUnicode: true
        );
    }

    internal static List<string> GetAsciiHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        bool includeOffset,
        string asciiRange
    )
    {
        var (minChar, maxChar) = ParseCharRange(asciiRange);
        var hits = FindAsciiStringHits(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            minChar,
            maxChar
        );
        return MaterializeStringHits(chunk, hits, includeOffset);
    }

    internal static (byte minChar, byte maxChar) ParseCharRange(string range)
    {
        if (string.IsNullOrEmpty(range) || range == "[\\x20-\\x7E]")
        {
            return (32, 126);
        }

        var match = Regex.Match(range, @"\[\\x([0-9A-Fa-f]+)-\\x([0-9A-Fa-f]+)\]");
        if (match.Success)
        {
            var minHex = match.Groups[1].Value;
            var maxHex = match.Groups[2].Value;

            if (
                byte.TryParse(
                    minHex,
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out byte min
                )
                && byte.TryParse(
                    maxHex,
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out byte max
                )
            )
            {
                return (min, max);
            }
        }

        return (32, 126);
    }

    internal static List<(string name, string pattern)> ParseRegexPatternsWithNames(
        string input,
        IReadOnlyDictionary<string, string> builtInPatterns
    )
    {
        var patterns = new List<(string name, string pattern)>();

        if (string.IsNullOrWhiteSpace(input))
        {
            return patterns;
        }

        if (input.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kvp in builtInPatterns)
            {
                patterns.Add((kvp.Key, kvp.Value));
            }

            return patterns;
        }

        var patternNames = input.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var patternName in patternNames)
        {
            var trimmedName = patternName.Trim();

            if (builtInPatterns.TryGetValue(trimmedName, out var resolvedPattern))
            {
                patterns.Add((trimmedName, resolvedPattern));
            }
            else
            {
                patterns.Add((trimmedName, trimmedName));
            }
        }

        return patterns;
    }

    internal static List<string> ParseRegexPatterns(
        string input,
        IReadOnlyDictionary<string, string> builtInPatterns
    )
    {
        return ParseRegexPatternsWithNames(input, builtInPatterns)
            .Select(pattern => pattern.pattern)
            .ToList();
    }

    internal static unsafe List<StringHitPosition> FindAsciiStringHits(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar = 32,
        byte maxChar = 126
    )
    {
        var hits = new List<StringHitPosition>(data.Length / 20);

        if (data.Length == 0)
        {
            return hits;
        }

        fixed (byte* dataPtr = data)
        {
            int stringStart = -1;
            int i = 0;

            if (Sse2.IsSupported && data.Length >= 16)
            {
                var minVecSigned = Vector128.Create((sbyte)(minChar - 128));
                var maxVecSigned = Vector128.Create((sbyte)(maxChar - 128));

                for (; i <= data.Length - 16; i += 16)
                {
                    var chunk = Sse2.LoadVector128(dataPtr + i);
                    var chunkSigned = Sse2.Subtract(
                        chunk.AsSByte(),
                        Vector128.Create(unchecked((sbyte)128))
                    );

                    var geMin = Sse2.CompareGreaterThan(
                        chunkSigned,
                        Sse2.Subtract(minVecSigned, Vector128.Create((sbyte)1))
                    );
                    var leMax = Sse2.CompareGreaterThan(
                        Sse2.Add(maxVecSigned, Vector128.Create((sbyte)1)),
                        chunkSigned
                    );
                    var isValid = Sse2.And(geMin, leMax);

                    uint mask = (uint)Sse2.MoveMask(isValid);

                    for (int bit = 0; bit < 16; bit++)
                    {
                        bool charValid = (mask & (1u << bit)) != 0;
                        ProcessCharForStringHit(
                            charValid,
                            i + bit,
                            ref stringStart,
                            minLength,
                            maxLength,
                            fileOffset,
                            hits
                        );
                    }
                }
            }

            for (; i < data.Length; i++)
            {
                byte currentByte = dataPtr[i];
                bool charValid = currentByte >= minChar && currentByte <= maxChar;
                ProcessCharForStringHit(
                    charValid,
                    i,
                    ref stringStart,
                    minLength,
                    maxLength,
                    fileOffset,
                    hits
                );
            }

            if (stringStart != -1)
            {
                int length = i - stringStart;
                if (length >= minLength)
                {
                    int actualLength = maxLength > 0 && length > maxLength ? maxLength : length;
                    hits.Add(new StringHitPosition(stringStart, actualLength, fileOffset));
                }
            }
        }

        return hits;
    }

    internal static List<string> MaterializeStringHits(
        ReadOnlySpan<byte> data,
        List<StringHitPosition> hits,
        bool includeOffset
    )
    {
        var results = new List<string>(hits.Count);

        foreach (var hit in hits)
        {
            if (hit.Start + hit.Length <= data.Length)
            {
                var stringBytes = data.Slice(hit.Start, hit.Length);
                var str = Encoding.ASCII.GetString(stringBytes);

                if (includeOffset)
                {
                    results.Add($"0x{hit.FileOffset + hit.Start:X}\t{str}");
                }
                else
                {
                    results.Add(str);
                }
            }
        }

        return results;
    }

    private static List<string> GetStringHitsUnified(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffsetInFile,
        bool includeOffset,
        bool isUnicode,
        byte minChar = 32,
        byte maxChar = 126
    )
    {
        var results = new List<string>();
        var stringStart = -1;
        var stringLength = 0;
        var stepSize = isUnicode ? 2 : 1;

        for (var i = 0; i < chunk.Length - (stepSize - 1); i += stepSize)
        {
            bool isValidChar;

            if (isUnicode)
            {
                if (i + 1 >= chunk.Length)
                {
                    break;
                }

                char c = (char)(chunk[i] | (chunk[i + 1] << 8));
                isValidChar = c >= 32 && c <= 126;
            }
            else
            {
                var currentByte = chunk[i];
                isValidChar = currentByte >= minChar && currentByte <= maxChar;
            }

            if (isValidChar)
            {
                if (stringStart == -1)
                {
                    stringStart = i;
                    stringLength = 1;
                }
                else
                {
                    stringLength++;
                }
            }
            else
            {
                if (stringStart != -1 && stringLength >= minLength)
                {
                    if (isUnicode)
                    {
                        AddUnicodeStringResult(
                            results,
                            chunk,
                            stringStart,
                            stringLength,
                            maxLength,
                            currentOffsetInFile,
                            includeOffset
                        );
                    }
                    else
                    {
                        AddAsciiStringResult(
                            results,
                            chunk,
                            stringStart,
                            stringLength,
                            maxLength,
                            currentOffsetInFile,
                            includeOffset
                        );
                    }
                }

                stringStart = -1;
                stringLength = 0;
            }
        }

        if (stringStart != -1 && stringLength >= minLength)
        {
            if (isUnicode)
            {
                AddUnicodeStringResult(
                    results,
                    chunk,
                    stringStart,
                    stringLength,
                    maxLength,
                    currentOffsetInFile,
                    includeOffset
                );
            }
            else
            {
                AddAsciiStringResult(
                    results,
                    chunk,
                    stringStart,
                    stringLength,
                    maxLength,
                    currentOffsetInFile,
                    includeOffset
                );
            }
        }

        return results;
    }

    private static void AddAsciiStringResult(
        List<string> results,
        ReadOnlySpan<byte> chunk,
        int stringStart,
        int stringLength,
        int maxLength,
        long currentOffsetInFile,
        bool includeOffset
    )
    {
        var actualLength = maxLength > 0 && stringLength > maxLength ? maxLength : stringLength;
        var value = Encoding.ASCII.GetString(chunk.Slice(stringStart, actualLength));

        if (includeOffset)
        {
            results.Add($"0x{currentOffsetInFile + stringStart:X}\t{value}");
        }
        else
        {
            results.Add(value);
        }
    }

    private static void AddUnicodeStringResult(
        List<string> results,
        ReadOnlySpan<byte> chunk,
        int stringStart,
        int stringLength,
        int maxLength,
        long currentOffsetInFile,
        bool includeOffset
    )
    {
        var actualLength = maxLength > 0 && stringLength > maxLength ? maxLength : stringLength;
        var byteLength = actualLength * 2;
        if (stringStart + byteLength > chunk.Length)
        {
            return;
        }

        var value = Encoding.Unicode.GetString(chunk.Slice(stringStart, byteLength));
        if (includeOffset)
        {
            results.Add($"0x{currentOffsetInFile + stringStart:X}\t{value}");
        }
        else
        {
            results.Add(value);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ProcessCharForStringHit(
        bool charValid,
        int position,
        ref int stringStart,
        int minLength,
        int maxLength,
        long fileOffset,
        List<StringHitPosition> hits
    )
    {
        if (charValid)
        {
            if (stringStart == -1)
            {
                stringStart = position;
            }
            else if (maxLength > 0 && (position - stringStart + 1) > maxLength)
            {
                hits.Add(new StringHitPosition(stringStart, maxLength, fileOffset));
                stringStart = -1;
            }
        }
        else if (stringStart != -1)
        {
            int length = position - stringStart;
            if (length >= minLength)
            {
                int actualLength = maxLength > 0 && length > maxLength ? maxLength : length;
                hits.Add(new StringHitPosition(stringStart, actualLength, fileOffset));
            }

            stringStart = -1;
        }
    }
}
