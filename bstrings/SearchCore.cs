using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
    private static readonly ConcurrentDictionary<int, Encoding> Encodings = new();

    internal static string CpuKernelDescription
    {
        get
        {
            var ascii = RustAsciiEngine.IsEnabled
                ? $"Rust {RustAsciiEngine.KernelDescription}"
                : Avx2.IsSupported
                    ? "C#/.NET AVX2"
                    : Sse2.IsSupported
                        ? "C#/.NET SSE2"
                        : "C#/.NET scalar";
            var unicode = Avx2.IsSupported
                ? Bmi2.IsSupported
                    ? "AVX2 + BMI2 PEXT"
                    : "AVX2"
                : Sse41.IsSupported
                    ? Bmi2.IsSupported
                        ? "SSE4.1 + BMI2 PEXT"
                        : "SSE4.1"
                    : "scalar";
            return $"ASCII {ascii}; UTF-16LE {unicode}";
        }
    }

    internal static List<string> GetUnicodeHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        bool includeOffset,
        string unicodeRange,
        ChunkHitOwnership ownership = default
    )
    {
        var (minChar, maxChar) = ParseUnicodeRange(unicodeRange);
        var hits = FindUnicodeStringHits(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            minChar,
            maxChar
        );
        if (!ownership.IsUnrestricted)
        {
            var chunkLength = chunk.Length;
            hits.RemoveAll(hit => !ownership.Accepts(hit.Start, hit.Length, chunkLength));
        }
        return MaterializeUnicodeStringHits(chunk, hits, includeOffset);
    }

    internal static List<ExtractedStringHit> GetUnicodeStructuredHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        string unicodeRange,
        ChunkHitOwnership ownership = default
    )
    {
        var (minChar, maxChar) = ParseUnicodeRange(unicodeRange);
        var hits = FindUnicodeStringHits(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            minChar,
            maxChar
        );
        if (!ownership.IsUnrestricted)
        {
            var chunkLength = chunk.Length;
            hits.RemoveAll(hit => !ownership.Accepts(hit.Start, hit.Length, chunkLength));
        }
        return MaterializeUnicodeStructuredHits(chunk, hits);
    }

    internal static List<string> GetAsciiHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        bool includeOffset,
        string asciiRange,
        int codePage = 1252,
        ChunkHitOwnership ownership = default
    )
    {
        var (minChar, maxChar) = ParseCharRange(asciiRange);
        if (
            RustAsciiEngine.TryRentConfigured(
                chunk,
                minLength,
                maxLength,
                currentOffset,
                minChar,
                maxChar,
                out var nativeHits
            )
        )
        {
            using (nativeHits)
            {
                return MaterializeStringHits(
                    chunk,
                    nativeHits,
                    includeOffset,
                    codePage,
                    ownership
                );
            }
        }

        var hits = FindAsciiStringHitsManaged(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            minChar,
            maxChar
        );
        if (!ownership.IsUnrestricted)
        {
            var chunkLength = chunk.Length;
            hits.RemoveAll(hit => !ownership.Accepts(hit.Start, hit.Length, chunkLength));
        }
        return MaterializeStringHits(chunk, hits, includeOffset, codePage);
    }

    internal static List<ExtractedStringHit> GetAsciiStructuredHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        string asciiRange,
        int codePage = 1252,
        ChunkHitOwnership ownership = default
    )
    {
        var (minChar, maxChar) = ParseCharRange(asciiRange);
        if (
            RustAsciiEngine.TryRentConfigured(
                chunk,
                minLength,
                maxLength,
                currentOffset,
                minChar,
                maxChar,
                out var nativeHits
            )
        )
        {
            using (nativeHits)
            {
                return MaterializeStructuredHits(chunk, nativeHits, codePage, ownership);
            }
        }

        var hits = FindAsciiStringHitsManaged(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            minChar,
            maxChar
        );
        if (!ownership.IsUnrestricted)
        {
            var chunkLength = chunk.Length;
            hits.RemoveAll(hit => !ownership.Accepts(hit.Start, hit.Length, chunkLength));
        }
        return MaterializeStructuredHits(chunk, hits, codePage);
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

    internal static (char minChar, char maxChar) ParseUnicodeRange(string range)
    {
        if (string.IsNullOrEmpty(range) || range == "[\\u0020-\\u007E]")
        {
            return (' ', '~');
        }

        var match = Regex.Match(range, @"\[\\u([0-9A-Fa-f]{4})-\\u([0-9A-Fa-f]{4})\]");
        if (
            match.Success
            && ushort.TryParse(
                match.Groups[1].Value,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var min
            )
            && ushort.TryParse(
                match.Groups[2].Value,
                System.Globalization.NumberStyles.HexNumber,
                null,
                out var max
            )
            && min <= max
        )
        {
            return ((char)min, (char)max);
        }

        return (' ', '~');
    }

    internal static List<(string name, string pattern)> ParseRegexPatternsWithNames(
        string input,
        IReadOnlyDictionary<string, string> builtInPatterns,
        IReadOnlyDictionary<string, IReadOnlyList<string>> builtInGroups = null
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

        var patternNames = SplitPatternList(input);
        var addedBuiltIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedCustomPatterns = new HashSet<string>(StringComparer.Ordinal);

        foreach (var patternName in patternNames)
        {
            var trimmedName = patternName.Trim();
            if (trimmedName.Length == 0)
            {
                continue;
            }

            var groupName = builtInGroups?.Keys.FirstOrDefault(name =>
                string.Equals(name, trimmedName, StringComparison.OrdinalIgnoreCase)
            );
            if (groupName is not null)
            {
                foreach (var memberName in builtInGroups![groupName])
                {
                    if (
                        builtInPatterns.TryGetValue(memberName, out var memberPattern)
                        && addedBuiltIns.Add(memberName)
                    )
                    {
                        patterns.Add((memberName, memberPattern));
                    }
                }
                continue;
            }

            var builtInName = builtInPatterns.Keys.FirstOrDefault(name =>
                string.Equals(name, trimmedName, StringComparison.OrdinalIgnoreCase)
            );
            if (builtInName is not null)
            {
                if (addedBuiltIns.Add(builtInName))
                {
                    patterns.Add((builtInName, builtInPatterns[builtInName]));
                }
            }
            else if (addedCustomPatterns.Add(trimmedName))
            {
                patterns.Add((trimmedName, trimmedName));
            }
        }

        return patterns;
    }

    private static IEnumerable<string> SplitPatternList(string input)
    {
        var start = 0;
        var escaped = false;
        var squareDepth = 0;
        var roundDepth = 0;
        var braceDepth = 0;

        for (var index = 0; index < input.Length; index++)
        {
            var current = input[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (current == '\\')
            {
                escaped = true;
                continue;
            }

            switch (current)
            {
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    squareDepth = Math.Max(0, squareDepth - 1);
                    break;
                case '(' when squareDepth == 0:
                    roundDepth++;
                    break;
                case ')' when squareDepth == 0:
                    roundDepth = Math.Max(0, roundDepth - 1);
                    break;
                case '{' when squareDepth == 0:
                    braceDepth++;
                    break;
                case '}' when squareDepth == 0:
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case ',' when squareDepth == 0 && roundDepth == 0 && braceDepth == 0:
                    yield return input[start..index];
                    start = index + 1;
                    break;
            }
        }

        yield return input[start..];
    }

    internal static List<string> ParseRegexPatterns(
        string input,
        IReadOnlyDictionary<string, string> builtInPatterns,
        IReadOnlyDictionary<string, IReadOnlyList<string>> builtInGroups = null
    )
    {
        return ParseRegexPatternsWithNames(input, builtInPatterns, builtInGroups)
            .Select(pattern => pattern.pattern)
            .ToList();
    }

    internal static List<StringHitPosition> FindAsciiStringHits(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar = 32,
        byte maxChar = 126
    )
    {
        if (
            RustAsciiEngine.TryFindConfigured(
                data,
                minLength,
                maxLength,
                fileOffset,
                minChar,
                maxChar,
                out var nativeHits
            )
        )
        {
            return nativeHits;
        }

        return FindAsciiStringHitsManaged(
            data,
            minLength,
            maxLength,
            fileOffset,
            minChar,
            maxChar
        );
    }

    internal static unsafe List<StringHitPosition> FindAsciiStringHitsManaged(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar = 32,
        byte maxChar = 126
    )
    {
        var hits = new List<StringHitPosition>(Math.Min(data.Length / 64, 4096));

        if (data.Length == 0 || minChar > maxChar)
        {
            return hits;
        }

        fixed (byte* dataPtr = data)
        {
            var stringStart = -1;
            var position = 0;

            if (Avx2.IsSupported && data.Length >= Vector256<byte>.Count)
            {
                var minVector = Vector256.Create(minChar);
                var maxVector = Vector256.Create(maxChar);

                for (; position <= data.Length - Vector256<byte>.Count; position += Vector256<byte>.Count)
                {
                    var block = Avx.LoadVector256(dataPtr + position);
                    var atLeastMin = Avx2.CompareEqual(Avx2.Max(block, minVector), block);
                    var atMostMax = Avx2.CompareEqual(Avx2.Min(block, maxVector), block);
                    var validMask = (uint)Avx2.MoveMask(
                        Avx2.And(atLeastMin, atMostMax).AsSByte()
                    );
                    ProcessValidityMask(
                        validMask,
                        Vector256<byte>.Count,
                        position,
                        ref stringStart,
                        minLength,
                        maxLength,
                        fileOffset,
                        hits
                    );
                }
            }

            if (Sse2.IsSupported && position <= data.Length - Vector128<byte>.Count)
            {
                var minVector = Vector128.Create(minChar);
                var maxVector = Vector128.Create(maxChar);

                for (; position <= data.Length - Vector128<byte>.Count; position += Vector128<byte>.Count)
                {
                    var block = Sse2.LoadVector128(dataPtr + position);
                    var atLeastMin = Sse2.CompareEqual(Sse2.Max(block, minVector), block);
                    var atMostMax = Sse2.CompareEqual(Sse2.Min(block, maxVector), block);
                    var validMask = (uint)Sse2.MoveMask(
                        Sse2.And(atLeastMin, atMostMax).AsSByte()
                    );
                    ProcessValidityMask(
                        validMask,
                        Vector128<byte>.Count,
                        position,
                        ref stringStart,
                        minLength,
                        maxLength,
                        fileOffset,
                        hits
                    );
                }
            }

            for (; position < data.Length; position++)
            {
                var currentByte = dataPtr[position];
                ProcessCharForStringHit(
                    currentByte >= minChar && currentByte <= maxChar,
                    position,
                    ref stringStart,
                    minLength,
                    maxLength,
                    fileOffset,
                    hits
                );
            }

            if (stringStart >= 0)
            {
                AddStringHit(stringStart, position, minLength, maxLength, fileOffset, hits);
            }
        }

        return hits;
    }

    internal static unsafe List<StringHitPosition> FindUnicodeStringHits(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        char minChar = ' ',
        char maxChar = '~'
    )
    {
        var unitCount = data.Length / sizeof(ushort);
        var hits = new List<StringHitPosition>(Math.Min(unitCount / 64, 4096));

        if (unitCount == 0 || minChar > maxChar)
        {
            return hits;
        }

        fixed (byte* dataPtr = data)
        {
            var stringStart = -1;
            var position = 0;

            if (Avx2.IsSupported && unitCount >= Vector256<ushort>.Count)
            {
                var minVector = Vector256.Create((ushort)minChar);
                var rangeVector = Vector256.Create((ushort)(maxChar - minChar));

                for (
                    ;
                    position <= unitCount - Vector256<ushort>.Count;
                    position += Vector256<ushort>.Count
                )
                {
                    var block = Avx.LoadVector256((ushort*)(dataPtr + position * sizeof(ushort)));
                    var normalized = Avx2.Subtract(block, minVector);
                    var valid = Avx2.CompareEqual(
                        Avx2.Min(normalized, rangeVector),
                        normalized
                    );
                    var byteMask = (uint)Avx2.MoveMask(valid.AsSByte());
                    var validMask = CompressUtf16ValidityMask(
                        byteMask,
                        Vector256<ushort>.Count
                    );
                    ProcessValidityMask(
                        validMask,
                        Vector256<ushort>.Count,
                        position,
                        ref stringStart,
                        minLength,
                        maxLength,
                        fileOffset,
                        hits
                    );
                }
            }

            if (Sse41.IsSupported && position <= unitCount - Vector128<ushort>.Count)
            {
                var minVector = Vector128.Create((ushort)minChar);
                var rangeVector = Vector128.Create((ushort)(maxChar - minChar));

                for (
                    ;
                    position <= unitCount - Vector128<ushort>.Count;
                    position += Vector128<ushort>.Count
                )
                {
                    var block = Sse2.LoadVector128((ushort*)(dataPtr + position * sizeof(ushort)));
                    var normalized = Sse2.Subtract(block, minVector);
                    var valid = Sse2.CompareEqual(
                        Sse41.Min(normalized, rangeVector),
                        normalized
                    );
                    var byteMask = (uint)Sse2.MoveMask(valid.AsSByte());
                    var validMask = CompressUtf16ValidityMask(
                        byteMask,
                        Vector128<ushort>.Count
                    );
                    ProcessValidityMask(
                        validMask,
                        Vector128<ushort>.Count,
                        position,
                        ref stringStart,
                        minLength,
                        maxLength,
                        fileOffset,
                        hits
                    );
                }
            }

            for (; position < unitCount; position++)
            {
                var byteIndex = position * sizeof(ushort);
                var current = (ushort)(dataPtr[byteIndex] | (dataPtr[byteIndex + 1] << 8));
                ProcessCharForStringHit(
                    current >= minChar && current <= maxChar,
                    position,
                    ref stringStart,
                    minLength,
                    maxLength,
                    fileOffset,
                    hits
                );
            }

            if (stringStart >= 0)
            {
                AddStringHit(stringStart, position, minLength, maxLength, fileOffset, hits);
            }
        }

        for (var index = 0; index < hits.Count; index++)
        {
            var hit = hits[index];
            hits[index] = new StringHitPosition(
                hit.Start * sizeof(ushort),
                hit.Length * sizeof(ushort),
                hit.FileOffset
            );
        }

        return hits;
    }

    internal static List<string> MaterializeUnicodeStringHits(
        ReadOnlySpan<byte> data,
        List<StringHitPosition> hits,
        bool includeOffset
    )
    {
        var results = new List<string>(hits.Count);
        foreach (var hit in hits)
        {
            if (hit.Start + hit.Length > data.Length)
            {
                continue;
            }

            var value = Encoding.Unicode.GetString(data.Slice(hit.Start, hit.Length));
            results.Add(includeOffset ? $"0x{hit.FileOffset + hit.Start:X}\t{value}" : value);
        }

        return results;
    }

    internal static List<ExtractedStringHit> MaterializeUnicodeStructuredHits(
        ReadOnlySpan<byte> data,
        List<StringHitPosition> hits
    )
    {
        var results = new List<ExtractedStringHit>(hits.Count);
        foreach (var hit in hits)
        {
            if (hit.Start + hit.Length > data.Length)
            {
                continue;
            }

            results.Add(
                new ExtractedStringHit(
                    Encoding.Unicode.GetString(data.Slice(hit.Start, hit.Length)),
                    hit.FileOffset + hit.Start
                )
            );
        }

        return results;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint CompressUtf16ValidityMask(uint byteMask, int width)
    {
        var evenByteBits = width == Vector256<ushort>.Count ? 0x55555555U : 0x5555U;
        if (Bmi2.IsSupported)
        {
            return Bmi2.ParallelBitExtract(byteMask, evenByteBits);
        }

        uint result = 0;
        for (var index = 0; index < width; index++)
        {
            result |= ((byteMask >> (index * 2)) & 1U) << index;
        }

        return result;
    }

    internal static List<string> MaterializeStringHits(
        ReadOnlySpan<byte> data,
        RustAsciiEngine.HitBatch hits,
        bool includeOffset,
        int codePage = 1252,
        ChunkHitOwnership ownership = default
    )
    {
        var results = new List<string>(hits.Count);
        var encoding = Encodings.GetOrAdd(
            codePage,
            static value =>
                CodePagesEncodingProvider.Instance.GetEncoding(value)
                ?? Encoding.GetEncoding(value)
        );

        foreach (var hit in hits.Hits)
        {
            var start = (int)hit.Start;
            var length = (int)hit.Length;
            if (!ownership.IsUnrestricted && !ownership.Accepts(start, length, data.Length))
            {
                continue;
            }

            var str = encoding.GetString(data.Slice(start, length));
            results.Add(includeOffset ? $"0x{hits.FileOffset + start:X}\t{str}" : str);
        }

        return results;
    }

    internal static List<ExtractedStringHit> MaterializeStructuredHits(
        ReadOnlySpan<byte> data,
        RustAsciiEngine.HitBatch hits,
        int codePage = 1252,
        ChunkHitOwnership ownership = default
    )
    {
        var results = new List<ExtractedStringHit>(hits.Count);
        var encoding = Encodings.GetOrAdd(
            codePage,
            static value =>
                CodePagesEncodingProvider.Instance.GetEncoding(value)
                ?? Encoding.GetEncoding(value)
        );

        foreach (var hit in hits.Hits)
        {
            var start = (int)hit.Start;
            var length = (int)hit.Length;
            if (!ownership.IsUnrestricted && !ownership.Accepts(start, length, data.Length))
            {
                continue;
            }

            results.Add(
                new ExtractedStringHit(
                    encoding.GetString(data.Slice(start, length)),
                    hits.FileOffset + start
                )
            );
        }

        return results;
    }

    internal static List<string> MaterializeStringHits(
        ReadOnlySpan<byte> data,
        List<StringHitPosition> hits,
        bool includeOffset,
        int codePage = 1252
    )
    {
        var results = new List<string>(hits.Count);
        var encoding = Encodings.GetOrAdd(
            codePage,
            static value =>
                CodePagesEncodingProvider.Instance.GetEncoding(value)
                ?? Encoding.GetEncoding(value)
        );

        foreach (var hit in hits)
        {
            if (hit.Start + hit.Length <= data.Length)
            {
                var stringBytes = data.Slice(hit.Start, hit.Length);
                var str = encoding.GetString(stringBytes);

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

    internal static List<ExtractedStringHit> MaterializeStructuredHits(
        ReadOnlySpan<byte> data,
        List<StringHitPosition> hits,
        int codePage = 1252
    )
    {
        var results = new List<ExtractedStringHit>(hits.Count);
        var encoding = Encodings.GetOrAdd(
            codePage,
            static value =>
                CodePagesEncodingProvider.Instance.GetEncoding(value)
                ?? Encoding.GetEncoding(value)
        );

        foreach (var hit in hits)
        {
            if (hit.Start + hit.Length <= data.Length)
            {
                results.Add(
                    new ExtractedStringHit(
                        encoding.GetString(data.Slice(hit.Start, hit.Length)),
                        hit.FileOffset + hit.Start
                    )
                );
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
        char minChar = ' ',
        char maxChar = '~',
        ChunkHitOwnership ownership = default
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
                isValidChar = c >= minChar && c <= maxChar;
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
                            includeOffset,
                            ownership
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
                            includeOffset,
                            ownership
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
                    includeOffset,
                    ownership
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
                    includeOffset,
                    ownership
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
        bool includeOffset,
        ChunkHitOwnership ownership
    )
    {
        var actualLength = maxLength > 0 && stringLength > maxLength ? maxLength : stringLength;
        if (!ownership.Accepts(stringStart, actualLength, chunk.Length))
        {
            return;
        }
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
        bool includeOffset,
        ChunkHitOwnership ownership
    )
    {
        var actualLength = maxLength > 0 && stringLength > maxLength ? maxLength : stringLength;
        var byteLength = actualLength * 2;
        if (stringStart + byteLength > chunk.Length)
        {
            return;
        }

        if (!ownership.Accepts(stringStart, byteLength, chunk.Length))
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
    private static void ProcessValidityMask(
        uint validMask,
        int width,
        int blockStart,
        ref int stringStart,
        int minLength,
        int maxLength,
        long fileOffset,
        List<StringHitPosition> hits
    )
    {
        var bit = 0;
        while (bit < width)
        {
            var remainingMask = validMask >> bit;
            if ((remainingMask & 1) == 0)
            {
                if (stringStart >= 0)
                {
                    AddStringHit(
                        stringStart,
                        blockStart + bit,
                        minLength,
                        maxLength,
                        fileOffset,
                        hits
                    );
                    stringStart = -1;
                }

                if (remainingMask == 0)
                {
                    return;
                }

                bit += BitOperations.TrailingZeroCount(remainingMask);
                continue;
            }

            if (stringStart < 0)
            {
                stringStart = blockStart + bit;
            }

            var runLength = Math.Min(
                BitOperations.TrailingZeroCount(~remainingMask),
                width - bit
            );
            bit += runLength;
            if (bit < width)
            {
                AddStringHit(
                    stringStart,
                    blockStart + bit,
                    minLength,
                    maxLength,
                    fileOffset,
                    hits
                );
                stringStart = -1;
            }
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
            if (stringStart < 0)
            {
                stringStart = position;
            }

            return;
        }

        if (stringStart >= 0)
        {
            AddStringHit(stringStart, position, minLength, maxLength, fileOffset, hits);
            stringStart = -1;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddStringHit(
        int stringStart,
        int stringEnd,
        int minLength,
        int maxLength,
        long fileOffset,
        List<StringHitPosition> hits
    )
    {
        var length = stringEnd - stringStart;
        if (length < minLength)
        {
            return;
        }

        var actualLength = maxLength > 0 && length > maxLength ? maxLength : length;
        hits.Add(new StringHitPosition(stringStart, actualLength, fileOffset));
    }

}
