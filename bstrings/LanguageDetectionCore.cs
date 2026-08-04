#nullable enable

using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace bstrings;

internal enum LanguageDetectionMode
{
    Adaptive,
    Accurate,
    Fast,
}

internal readonly record struct LanguageDetectionResult(
    string Language,
    double Confidence,
    double TargetConfidence,
    double SecondConfidence,
    bool UsedLowAccuracyMode
)
{
    internal double TargetMargin => Confidence - TargetConfidence;
    internal double TopLanguageMargin => Confidence - SecondConfidence;
}

internal static unsafe class LanguageDetectionCore
{
    private const string NativeLibraryName = "bstrings_core";
    private const uint ExpectedAbiVersion = 3;

    internal static bool TryParseMode(
        string? value,
        out LanguageDetectionMode mode,
        out string? error
    )
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "adaptive":
                mode = LanguageDetectionMode.Adaptive;
                error = null;
                return true;
            case "accurate":
            case "high":
                mode = LanguageDetectionMode.Accurate;
                error = null;
                return true;
            case "fast":
            case "low":
                mode = LanguageDetectionMode.Fast;
                error = null;
                return true;
            default:
                mode = LanguageDetectionMode.Adaptive;
                error = "Invalid language detection mode. Use adaptive, accurate, or fast.";
                return false;
        }
    }

    internal static bool TryVerifyAvailability(out string? error)
    {
        try
        {
            var abiVersion = NativeMethods.AbiVersion();
            if (abiVersion != ExpectedAbiVersion)
            {
                error = $"native ABI {abiVersion} does not match expected ABI {ExpectedAbiVersion}";
                return false;
            }

            var targetUtf8 = "en"u8.ToArray();
            NativeLanguageResult nativeResult = default;
            fixed (byte* targetPointer = targetUtf8)
            {
                var status = NativeMethods.DetectLanguage(
                    null,
                    0,
                    targetPointer,
                    (nuint)targetUtf8.Length,
                    0,
                    &nativeResult
                );
                if (status != 2)
                {
                    error = $"The native language-detector availability probe returned status {status}.";
                    return false;
                }
            }
            error = null;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            error = $"{NativeLibraryName} was not found: {ex.Message}";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"{NativeLibraryName} is missing language detection support: {ex.Message}";
            return false;
        }
        catch (BadImageFormatException ex)
        {
            error = $"{NativeLibraryName} has the wrong architecture or format: {ex.Message}";
            return false;
        }
    }

    internal static bool TryDetect(
        string text,
        LanguageDetectionMode mode,
        string targetLanguage,
        out LanguageDetectionResult result,
        out string? error
    )
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Text is empty.";
            return false;
        }
        if (!TryNormalizeTargetLanguage(targetLanguage, out var detectorTarget, out error))
        {
            return false;
        }

        try
        {
            var abiVersion = NativeMethods.AbiVersion();
            if (abiVersion != ExpectedAbiVersion)
            {
                error =
                    $"native ABI {abiVersion} does not match expected ABI {ExpectedAbiVersion}";
                return false;
            }

            var utf8 = Encoding.UTF8.GetBytes(text);
            var targetUtf8 = Encoding.ASCII.GetBytes(detectorTarget);
            var useLowAccuracy = mode switch
            {
                LanguageDetectionMode.Fast => true,
                _ => false,
            };
            NativeLanguageResult nativeResult = default;
            fixed (byte* textPointer = utf8)
            fixed (byte* targetPointer = targetUtf8)
            {
                var status = NativeMethods.DetectLanguage(
                    textPointer,
                    (nuint)utf8.Length,
                    targetPointer,
                    (nuint)targetUtf8.Length,
                    useLowAccuracy ? 1 : 0,
                    &nativeResult
                );
                if (status == 2)
                {
                    error = "The language could not be identified reliably.";
                    return false;
                }
                if (status != 0)
                {
                    error = $"The native language detector returned status {status}.";
                    return false;
                }
            }

            var language = ReadLanguageCode(nativeResult);
            if (string.IsNullOrWhiteSpace(language))
            {
                error = "The native language detector returned an empty language code.";
                return false;
            }
            if (
                !IsProbability(nativeResult.Confidence)
                || !IsProbability(nativeResult.TargetConfidence)
                || !IsProbability(nativeResult.SecondConfidence)
            )
            {
                error = "The native language detector returned an invalid confidence value.";
                return false;
            }

            result = new LanguageDetectionResult(
                language,
                nativeResult.Confidence,
                nativeResult.TargetConfidence,
                nativeResult.SecondConfidence,
                useLowAccuracy
            );
            error = null;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            error = $"{NativeLibraryName} was not found: {ex.Message}";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"{NativeLibraryName} is missing language detection support: {ex.Message}";
            return false;
        }
        catch (BadImageFormatException ex)
        {
            error = $"{NativeLibraryName} has the wrong architecture or format: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool IsProbability(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1;

    internal static bool TryNormalizeTargetLanguage(
        string? value,
        out string primaryLanguage,
        out string? error
    )
    {
        primaryLanguage = string.Empty;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = "A target language is required.";
            return false;
        }

        var subtags = trimmed.Split('-');
        if (subtags[0].Length != 2 || !IsAsciiLetters(subtags[0]))
        {
            error =
                "The target language must use a two-letter ISO 639-1 primary subtag, optionally followed by BCP-47 subtags.";
            return false;
        }
        for (var index = 1; index < subtags.Length; index++)
        {
            if (
                subtags[index].Length is < 1 or > 8
                || !subtags[index].All(character =>
                    character is >= 'A' and <= 'Z'
                        or >= 'a' and <= 'z'
                        or >= '0' and <= '9'
                )
            )
            {
                error = "The target language contains an invalid BCP-47 subtag.";
                return false;
            }
        }
        primaryLanguage = subtags[0].ToLowerInvariant();
        error = null;
        return true;
    }

    private static bool IsAsciiLetters(string value) =>
        value.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static string ReadLanguageCode(NativeLanguageResult result)
    {
        Span<byte> bytes = stackalloc byte[8];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = result.LanguageCode[index];
        }
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }
        return Encoding.ASCII.GetString(bytes[..length]).ToLowerInvariant();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeLanguageResult
    {
        internal fixed byte LanguageCode[8];
        internal double Confidence;
        internal double TargetConfidence;
        internal double SecondConfidence;
    }

    private static class NativeMethods
    {
        [DllImport(
            NativeLibraryName,
            EntryPoint = "bstrings_abi_version",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        internal static extern uint AbiVersion();

        [DllImport(
            NativeLibraryName,
            EntryPoint = "bstrings_detect_language",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        internal static extern int DetectLanguage(
            byte* text,
            nuint textLength,
            byte* targetLanguage,
            nuint targetLanguageLength,
            int lowAccuracy,
            NativeLanguageResult* output
        );
    }
}
