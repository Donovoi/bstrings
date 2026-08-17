#nullable enable

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace bstrings;

internal static class Base64ContentCore
{
    internal const int MinimumHighConfidenceEncodedCharacters = 24;
    internal const int MaximumHighConfidenceEncodedCharacters = 16 * 1024;

    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, false, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, false, true);

    internal static bool IsHighConfidence(ReadOnlySpan<char> candidate)
    {
        if (
            candidate.Length < MinimumHighConfidenceEncodedCharacters
            || candidate.Length > MaximumHighConfidenceEncodedCharacters
            || IsAllHexadecimal(candidate)
            || !TryGetExactDecodedLength(candidate, out var expectedLength)
        )
        {
            return false;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, expectedLength));
        try
        {
            if (
                !Convert.TryFromBase64Chars(candidate, buffer, out var decodedLength)
                || decodedLength != expectedLength
                || !Convert
                    .ToBase64String(buffer, 0, decodedLength)
                    .AsSpan()
                    .SequenceEqual(candidate)
            )
            {
                return false;
            }

            var decoded = buffer.AsSpan(0, decodedLength);
            if (ClassifyHighConfidenceBinary(decoded) is not null)
            {
                return true;
            }

            return TryDecodeText(decoded, powerShell: false, out var text, out var charset)
                && (
                    !string.Equals(charset, "utf-8", StringComparison.Ordinal)
                    || LooksLikeText(decoded)
                )
                && IsHighConfidenceText(text);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static bool TryDecodeCanonical(
        string candidate,
        byte[] destination,
        out int decodedLength
    )
    {
        decodedLength = 0;
        if (!TryGetExactDecodedLength(candidate.AsSpan(), out var expectedLength))
        {
            return false;
        }
        if (
            !Convert.TryFromBase64String(candidate, destination, out decodedLength)
            || decodedLength != expectedLength
        )
        {
            decodedLength = 0;
            return false;
        }
        return string.Equals(
            Convert.ToBase64String(destination, 0, decodedLength),
            candidate,
            StringComparison.Ordinal
        );
    }

    internal static bool TryGetExactDecodedLength(string candidate, out int decodedLength) =>
        TryGetExactDecodedLength(candidate.AsSpan(), out decodedLength);

    internal static bool TryGetExactDecodedLength(
        ReadOnlySpan<char> candidate,
        out int decodedLength
    )
    {
        decodedLength = 0;
        if (candidate.Length == 0 || candidate.Length % 4 != 0)
        {
            return false;
        }

        var padding = candidate.EndsWith("==", StringComparison.Ordinal)
            ? 2
            : candidate.EndsWith("=", StringComparison.Ordinal) ? 1 : 0;
        if (candidate[..^padding].Contains('='))
        {
            return false;
        }

        for (var index = 0; index < candidate.Length - padding; index++)
        {
            if (GetBase64Value(candidate[index]) < 0)
            {
                return false;
            }
        }
        if (
            padding == 2
            && (GetBase64Value(candidate[^3]) & 0x0F) != 0
            || padding == 1
            && (GetBase64Value(candidate[^2]) & 0x03) != 0
        )
        {
            return false;
        }

        decodedLength = checked((candidate.Length / 4) * 3 - padding);
        return true;
    }

    internal static bool TryDecodeText(
        ReadOnlySpan<byte> bytes,
        bool powerShell,
        out string text,
        out string charset
    )
    {
        try
        {
            if (powerShell)
            {
                if (bytes.Length == 0 || bytes.Length % 2 != 0)
                {
                    text = string.Empty;
                    charset = string.Empty;
                    return false;
                }
                text = StrictUtf16Le.GetString(bytes);
                charset = "utf-16le-powershell";
                return bytes.SequenceEqual(StrictUtf16Le.GetBytes(text));
            }
            if (StartsWith(bytes, 0xEF, 0xBB, 0xBF))
            {
                text = StrictUtf8.GetString(bytes[3..]);
                charset = "utf-8-bom";
                return bytes.SequenceEqual(Combine([0xEF, 0xBB, 0xBF], StrictUtf8.GetBytes(text)));
            }
            if (StartsWith(bytes, 0xFF, 0xFE))
            {
                text = StrictUtf16Le.GetString(bytes[2..]);
                charset = "utf-16le-bom";
                return bytes.SequenceEqual(Combine([0xFF, 0xFE], StrictUtf16Le.GetBytes(text)));
            }
            if (StartsWith(bytes, 0xFE, 0xFF))
            {
                text = StrictUtf16Be.GetString(bytes[2..]);
                charset = "utf-16be-bom";
                return bytes.SequenceEqual(Combine([0xFE, 0xFF], StrictUtf16Be.GetBytes(text)));
            }
            text = StrictUtf8.GetString(bytes);
            charset = "utf-8";
            return bytes.SequenceEqual(StrictUtf8.GetBytes(text));
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            charset = string.Empty;
            return false;
        }
    }

    internal static bool IsPublishableText(string text)
    {
        var visible = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == 0)
            {
                return false;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control)
            {
                if (rune.Value is not ('\t' or '\n' or '\r'))
                {
                    return false;
                }
                continue;
            }
            if (
                category
                is not UnicodeCategory.Format
                    and not UnicodeCategory.Surrogate
                    and not UnicodeCategory.PrivateUse
                    and not UnicodeCategory.LineSeparator
                    and not UnicodeCategory.ParagraphSeparator
                && !Rune.IsWhiteSpace(rune)
            )
            {
                visible++;
            }
        }
        return visible >= 4;
    }

    internal static bool LooksLikeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }
        var textBytes = 0;
        foreach (var value in bytes)
        {
            if (value is 0x09 or 0x0A or 0x0D || value is >= 0x20 and <= 0x7E || value >= 0x80)
            {
                textBytes++;
            }
        }
        return textBytes * 4 >= bytes.Length * 3;
    }

    internal static string? ClassifyKnownBinary(ReadOnlySpan<byte> bytes)
    {
        if (IsPortableExecutable(bytes)) return "pe";
        if (StartsWith(bytes, 0x7F, 0x45, 0x4C, 0x46)) return "elf";
        if (StartsWith(bytes, 0x25, 0x50, 0x44, 0x46, 0x2D)) return "pdf";
        if (StartsWith(bytes, 0x50, 0x4B, 0x03, 0x04)) return "zip";
        if (StartsWith(bytes, 0x1F, 0x8B, 0x08) && bytes.Length >= 10 && (bytes[3] & 0xE0) == 0) return "gzip";
        if (StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "png";
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF)) return "jpeg";
        if (StartsWith(bytes, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "7z";
        if (StartsWith(bytes, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07)) return "rar";
        return null;
    }

    private static string? ClassifyHighConfidenceBinary(ReadOnlySpan<byte> bytes)
    {
        if (IsPortableExecutable(bytes)) return "pe";
        if (IsElf(bytes)) return "elf";
        if (StartsWith(bytes, 0x25, 0x50, 0x44, 0x46, 0x2D)) return "pdf";
        if (IsZip(bytes)) return "zip";
        if (StartsWith(bytes, 0x1F, 0x8B, 0x08) && bytes.Length >= 10 && (bytes[3] & 0xE0) == 0) return "gzip";
        if (StartsWith(bytes, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return "png";
        if (StartsWith(bytes, 0xFF, 0xD8, 0xFF) && bytes.Length >= 4) return "jpeg";
        if (StartsWith(bytes, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return "7z";
        if (StartsWith(bytes, 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07)) return "rar";
        return null;
    }

    private static bool IsHighConfidenceText(string text)
    {
        var visible = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == 0)
            {
                return false;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control)
            {
                if (rune.Value is not ('\t' or '\n' or '\r'))
                {
                    return false;
                }
                continue;
            }
            if (
                category
                is UnicodeCategory.Format
                    or UnicodeCategory.Surrogate
                    or UnicodeCategory.PrivateUse
                    or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator
            )
            {
                return false;
            }
            if (!Rune.IsWhiteSpace(rune))
            {
                visible++;
            }
        }
        return visible >= 4;
    }

    private static bool IsAllHexadecimal(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (
                character is not (>= '0' and <= '9')
                    and not (>= 'A' and <= 'F')
                    and not (>= 'a' and <= 'f')
            )
            {
                return false;
            }
        }
        return value.Length > 0;
    }

    private static bool IsPortableExecutable(ReadOnlySpan<byte> bytes)
    {
        if (!StartsWith(bytes, 0x4D, 0x5A) || bytes.Length < 0x40)
        {
            return false;
        }
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(bytes[0x3C..0x40]);
        return peOffset >= 0x40
            && peOffset <= bytes.Length - 4
            && StartsWith(bytes[peOffset..], 0x50, 0x45, 0x00, 0x00);
    }

    private static bool IsElf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 16
        && StartsWith(bytes, 0x7F, 0x45, 0x4C, 0x46)
        && bytes[4] is 1 or 2
        && bytes[5] is 1 or 2
        && bytes[6] == 1;

    private static bool IsZip(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 30
        && StartsWith(bytes, 0x50, 0x4B, 0x03, 0x04)
        && BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..6]) >= 10;

    private static int GetBase64Value(char value) => value switch
    {
        >= 'A' and <= 'Z' => value - 'A',
        >= 'a' and <= 'z' => value - 'a' + 26,
        >= '0' and <= '9' => value - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };

    private static bool StartsWith(ReadOnlySpan<byte> value, params byte[] prefix) =>
        value.StartsWith(prefix.AsSpan());

    private static byte[] Combine(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> content)
    {
        var result = new byte[prefix.Length + content.Length];
        prefix.CopyTo(result);
        content.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }
}
