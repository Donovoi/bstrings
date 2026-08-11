#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace bstrings;

internal enum TranslationRoutingOutcome
{
    Retain,
    StructuredOnlyProspectiveBypass,
}

internal readonly record struct TranslationRoutingAssessment(
    TranslationRoutingOutcome Outcome,
    bool IsUnknown,
    double CoverageScore,
    string Reason,
    string[] StructuredClasses,
    int StructuredTokenCount
);

/// <summary>
/// Produces a conservative, deterministic shadow decision before language detection.
/// This router never suppresses a record. A prospective bypass is emitted only when
/// one semantically validated structure covers the complete trimmed record.
/// </summary>
internal static class TranslationWorthinessRouter
{
    internal const string PolicyVersion = "structured-shadow-v1";
    internal const string CodebookVersion = "translation-routing-codes-v1";
    internal static IReadOnlyDictionary<string, string> Codebook { get; } =
        new ReadOnlyDictionary<string, string>(
            new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["retain"] = "retain",
                ["unknown-length"] = "unknown",
                ["unknown-control"] = "unknown",
                ["unknown-validation"] = "unknown",
                ["prospective-guid"] = "prospective",
                ["prospective-digest"] = "prospective",
                ["prospective-ip"] = "prospective",
                ["prospective-network"] = "prospective",
                ["prospective-endpoint"] = "prospective",
                ["shadow-jwt"] = "retain",
                ["shadow-hex"] = "retain",
                ["shadow-uri"] = "retain",
                ["shadow-registry"] = "retain",
                ["shadow-path"] = "retain",
                ["shadow-base64"] = "retain",
            }
        );
    internal const int MaximumTextCharacters = 16 * 1024;

    internal static TranslationRoutingAssessment Assess(string text)
    {
        try
        {
            return AssessCore(text);
        }
        catch (Exception ex)
            when (ex is ArgumentException or FormatException or JsonException or OverflowException)
        {
            return Retain(
                isUnknown: true,
                coverageScore: 0,
                reason: "router-error",
                structuredClasses: [],
                structuredTokenCount: 0
            );
        }
    }

    private static TranslationRoutingAssessment AssessCore(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Retain(false, 0, "empty-or-whitespace", [], 0);
        }
        if (text.Length > MaximumTextCharacters)
        {
            return Retain(true, 0, "unsupported-length", [], 0);
        }
        foreach (var character in text)
        {
            if (char.IsControl(character) && !char.IsWhiteSpace(character))
            {
                return Retain(true, 0, "unsupported-control-character", [], 0);
            }
        }

        var value = text.Trim();
        if (TryClassifyToken(value, out var singleClass, out var prospectiveEligible))
        {
            return prospectiveEligible
                ? ProspectiveBypass([singleClass], 1)
                : Retain(
                    false,
                    1,
                    "machine-like-signal-not-bypass-eligible",
                    [singleClass],
                    1
                );
        }

        return Retain(false, 0, "no-exact-structured-coverage", [], 0);
    }

    private static TranslationRoutingAssessment ProspectiveBypass(
        string[] structuredClasses,
        int tokenCount
    ) =>
        new(
            TranslationRoutingOutcome.StructuredOnlyProspectiveBypass,
            IsUnknown: false,
            CoverageScore: 1,
            Reason: "whole-record-structured-coverage",
            structuredClasses,
            tokenCount
        );

    private static TranslationRoutingAssessment Retain(
        bool isUnknown,
        double coverageScore,
        string reason,
        string[] structuredClasses,
        int structuredTokenCount
    ) =>
        new(
            TranslationRoutingOutcome.Retain,
            isUnknown,
            coverageScore,
            reason,
            structuredClasses,
            structuredTokenCount
        );

    private static bool TryClassifyToken(
        string value,
        out string tokenClass,
        out bool prospectiveEligible
    )
    {
        tokenClass = string.Empty;
        prospectiveEligible = false;
        if (value.Length == 0)
        {
            return false;
        }
        if (TryClassifyGuid(value))
        {
            tokenClass = "guid";
            prospectiveEligible = true;
            return true;
        }
        if (TryClassifyHash(value))
        {
            tokenClass = "cryptographic-hash";
            prospectiveEligible = true;
            return true;
        }
        if (TryClassifyHexBlob(value))
        {
            tokenClass = "hex-blob";
            return true;
        }
        if (TryClassifyIpNetworkOrEndpoint(value, out tokenClass))
        {
            prospectiveEligible = true;
            return true;
        }
        if (TryClassifyJwt(value))
        {
            tokenClass = "jwt";
            return true;
        }
        if (TryClassifyRegistryPath(value))
        {
            tokenClass = "registry-path";
            return true;
        }
        if (TryClassifyAbsolutePath(value))
        {
            tokenClass = "absolute-file-path";
            return true;
        }
        if (TryClassifyUri(value))
        {
            tokenClass = "absolute-uri";
            return true;
        }
        if (TryClassifyBase64(value))
        {
            tokenClass = "base64-blob";
            return true;
        }
        return false;
    }

    private static bool TryClassifyGuid(string value) =>
        Guid.TryParseExact(value, "D", out _)
        || Guid.TryParseExact(value, "B", out _)
        || Guid.TryParseExact(value, "P", out _);

    private static bool TryClassifyHash(string value)
    {
        var separator = value.IndexOf(':');
        if (separator > 0)
        {
            var algorithm = value[..separator].ToLowerInvariant();
            var digits = value[(separator + 1)..];
            var expectedLength = algorithm switch
            {
                "md5" => 32,
                "sha1" => 40,
                "sha224" => 56,
                "sha256" => 64,
                "sha384" => 96,
                "sha512" => 128,
                _ => 0,
            };
            return expectedLength != 0 && digits.Length == expectedLength && IsAsciiHex(digits);
        }
        return false;
    }

    private static bool TryClassifyHexBlob(string value)
    {
        if (!value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var digits = value[2..];
        return digits.Length >= 32 && digits.Length % 2 == 0 && IsAsciiHex(digits);
    }

    private static bool TryClassifyJwt(string value)
    {
        if (value.Length < 20)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (
                character != '.'
                && character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'
            )
            {
                return false;
            }
        }
        var firstDot = value.IndexOf('.');
        if (firstDot <= 0)
        {
            return false;
        }
        var secondDot = value.IndexOf('.', firstDot + 1);
        if (
            secondDot <= firstDot + 1
            || secondDot == value.Length - 1
            || value.IndexOf('.', secondDot + 1) >= 0
        )
        {
            return false;
        }
        return TryDecodeBase64UrlJsonObject(value[..firstDot])
            && TryDecodeBase64UrlJsonObject(value[(firstDot + 1)..secondDot])
            && TryDecodeBase64Url(value[(secondDot + 1)..], out _);
    }

    private static bool TryDecodeBase64UrlJsonObject(string value)
    {
        if (!TryDecodeBase64Url(value, out var bytes))
        {
            return false;
        }
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.ValueKind == JsonValueKind.Object;
    }

    private static bool TryDecodeBase64Url(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Any(character =>
                !(character is >= 'A' and <= 'Z')
                && !(character is >= 'a' and <= 'z')
                && !(character is >= '0' and <= '9')
                && character is not '-' and not '_'))
        {
            return false;
        }
        var remainder = value.Length % 4;
        if (remainder == 1)
        {
            return false;
        }
        var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - remainder) % 4);
        bytes = Convert.FromBase64String(padded);
        return bytes.Length > 0;
    }

    private static bool TryClassifyIpNetworkOrEndpoint(string value, out string tokenClass)
    {
        tokenClass = string.Empty;
        if (!value.Contains('.', StringComparison.Ordinal) && !value.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }
        if (value.Any(char.IsWhiteSpace))
        {
            return false;
        }
        if (TryParseCanonicalIpAddress(value, out _))
        {
            tokenClass = "ip-address";
            return true;
        }
        var slash = value.LastIndexOf('/');
        if (
            slash > 0
            && int.TryParse(value[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)
            && prefix.ToString(CultureInfo.InvariantCulture) == value[(slash + 1)..]
            && TryParseCanonicalIpAddress(value[..slash], out var networkAddress)
            && prefix >= 0
            && prefix <= (networkAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128)
        )
        {
            tokenClass = "ip-network";
            return true;
        }
        if (value.Length > 0 && value[0] == '[' && value.Contains("]:"))
        {
            var close = value.IndexOf("]:", StringComparison.Ordinal);
            if (
                TryParseCanonicalIpAddress(value[1..close], out var ipv6EndpointAddress)
                && ipv6EndpointAddress.AddressFamily
                    == System.Net.Sockets.AddressFamily.InterNetworkV6
                && IsValidPort(value[(close + 2)..])
            )
            {
                tokenClass = "network-endpoint";
                return true;
            }
        }
        var colon = value.LastIndexOf(':');
        if (
            colon > 0
            && value.IndexOf(':') == colon
            && TryParseCanonicalIpAddress(value[..colon], out var ipv4EndpointAddress)
            && ipv4EndpointAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            && IsValidPort(value[(colon + 1)..])
        )
        {
            tokenClass = "network-endpoint";
            return true;
        }
        return false;
    }

    private static bool IsValidPort(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
        && port.ToString(CultureInfo.InvariantCulture) == value
        && port is >= 1 and <= 65535;

    private static bool TryParseCanonicalIpAddress(string value, out IPAddress address)
    {
        address = IPAddress.None;
        if (!IsAscii(value))
        {
            return false;
        }
        if (value.Contains('.', StringComparison.Ordinal))
        {
            var octets = value.Split('.');
            if (octets.Length != 4)
            {
                return false;
            }
            foreach (var octet in octets)
            {
                if (
                    octet.Length is < 1 or > 3
                    || (octet.Length > 1 && octet[0] == '0')
                    || !int.TryParse(
                        octet,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var valueOctet
                    )
                    || valueOctet > 255
                )
                {
                    return false;
                }
            }
            if (
                IPAddress.TryParse(value, out var parsedIpv4)
                && parsedIpv4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && string.Equals(parsedIpv4.ToString(), value, StringComparison.Ordinal)
            )
            {
                address = parsedIpv4;
                return true;
            }
            return false;
        }
        if (
            value.Contains(':', StringComparison.Ordinal)
            && !value.Contains('%', StringComparison.Ordinal)
            && IPAddress.TryParse(value, out var parsedIpv6)
            && parsedIpv6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            && string.Equals(parsedIpv6.ToString(), value, StringComparison.OrdinalIgnoreCase)
        )
        {
            address = parsedIpv6;
            return true;
        }
        return false;
    }

    private static bool TryClassifyUri(string value)
    {
        if (
            !value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }
        if (!IsAscii(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }
        return uri.Scheme switch
        {
            "http" or "https" or "ftp" => value.Contains("://", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(uri.Host),
            "file" => value.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
                && uri.IsFile
                && uri.AbsolutePath.Length > 1,
            _ => false,
        };
    }

    private static bool TryClassifyRegistryPath(string value)
    {
        if (
            value.Length < 5
            || (value[0] is not 'H' and not 'h')
            || !IsAscii(value)
            || value.Any(char.IsControl)
        )
        {
            return false;
        }
        ReadOnlySpan<string> roots =
        [
            "HKLM\\",
            "HKCU\\",
            "HKCR\\",
            "HKU\\",
            "HKCC\\",
            "HKEY_LOCAL_MACHINE\\",
            "HKEY_CURRENT_USER\\",
            "HKEY_CLASSES_ROOT\\",
            "HKEY_USERS\\",
            "HKEY_CURRENT_CONFIG\\",
        ];
        foreach (var root in roots)
        {
            if (value.StartsWith(root, StringComparison.OrdinalIgnoreCase) && value.Length > root.Length)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryClassifyAbsolutePath(string value)
    {
        var hasWindowsPrefix = value.Length >= 3
            && char.IsAsciiLetter(value[0])
            && value[1] == ':'
            && value[2] is '\\' or '/';
        var hasUncPrefix = value.StartsWith("\\\\", StringComparison.Ordinal);
        var hasPosixPrefix = value.Length > 1 && value[0] == '/';
        if (
            (!hasWindowsPrefix && !hasUncPrefix && !hasPosixPrefix)
            || !IsAscii(value)
            || value.Any(char.IsControl)
        )
        {
            return false;
        }
        if (
            value.Length >= 4
            && char.IsAsciiLetter(value[0])
            && value[1] == ':'
            && value[2] is '\\' or '/'
        )
        {
            return !value[3..].Any(character => character is '<' or '>' or '|' or '?' or '*');
        }
        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            var parts = value[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                && parts.All(part => !part.Any(character => character is '<' or '>' or '|' or '?' or '*'));
        }
        return value.Length > 1
            && value[0] == '/'
            && value.Contains('/', StringComparison.Ordinal)
            && !value.Any(char.IsWhiteSpace);
    }

    private static bool TryClassifyBase64(string value)
    {
        if (
            value.Length < 24
            || value.Length % 4 != 0
            || !value.Any(character => char.IsDigit(character) || character is '+' or '/' or '=')
        )
        {
            return false;
        }
        if (value.Any(character =>
                !(character is >= 'A' and <= 'Z')
                && !(character is >= 'a' and <= 'z')
                && !(character is >= '0' and <= '9')
                && character is not '+' and not '/' and not '='))
        {
            return false;
        }
        var padding = value.TakeLast(2).Count(character => character == '=');
        if (value[..(value.Length - padding)].Contains('=', StringComparison.Ordinal))
        {
            return false;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(value.Length);
        try
        {
            return Convert.TryFromBase64String(value, buffer, out var bytesWritten)
                && bytesWritten >= 12;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static bool IsAsciiHex(string value) =>
        value.All(character =>
            character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f'
            || character is >= 'A' and <= 'F');

    private static bool IsAscii(string value) => value.All(char.IsAscii);
}
