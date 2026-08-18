#nullable enable

using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace bstrings;

/// <summary>
/// Applies cheap, deterministic validity checks after a built-in regex has found a
/// candidate. These checks deliberately avoid network state and allocation lists:
/// forensic evidence can be historic, private, expired, or currently unresolvable.
/// </summary>
internal static class BuiltInSemanticValidator
{
    private const string Base58Alphabet =
        "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string RippleBase58Alphabet =
        "rpshnaf39wBUDNEGHJKLM4PQRST7VWXYZ2bcdeCg65jkm8oFqi1tuvAxyz";
    private const string Bech32Alphabet = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly XmlReaderSettings SafeXmlReaderSettings = new()
    {
        ConformanceLevel = ConformanceLevel.Document,
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };
    private static ReadOnlySpan<int> CryptoNoteDecodedBlockSizes =>
        [0, -1, 1, 2, -1, 3, 4, 5, -1, 6, 7, 8];

    internal static bool IsValid(
        BuiltInPatternDefinition definition,
        ReadOnlySpan<char> candidate
    )
    {
        return definition.Validation switch
        {
            BuiltInValidationKind.None => true,
            BuiltInValidationKind.Email => IsValidEmail(candidate),
            BuiltInValidationKind.EmailCandidate =>
                IsValidEmailCandidate(candidate) && !IsValidEmail(candidate),
            BuiltInValidationKind.PaymentCard => HasValidLuhnChecksum(candidate),
            BuiltInValidationKind.Base64 => Base64ContentCore.IsHighConfidence(candidate),
            BuiltInValidationKind.Base64Candidate =>
                Base64ContentCore.TryGetExactDecodedLength(candidate, out _)
                && !Base64ContentCore.IsHighConfidence(candidate),
            BuiltInValidationKind.MacAddress => IsSeparatedMacAddress(candidate),
            BuiltInValidationKind.MacAddressCandidate =>
                IsUnseparatedMacAddress(candidate),
            BuiltInValidationKind.Ipv6Address =>
                IsIpv6Address(candidate) && !candidate.SequenceEqual("::"),
            BuiltInValidationKind.Ipv6AddressCandidate => candidate.SequenceEqual("::"),
            BuiltInValidationKind.BitLocker => IsValidBitLockerRecoveryPassword(candidate),
            BuiltInValidationKind.BitcoinBase58Check => IsValidBase58Check(
                candidate,
                0x00,
                0x05
            ),
            BuiltInValidationKind.BitcoinSegwitAddress => IsValidWitnessAddress(
                candidate,
                "bc"
            ),
            BuiltInValidationKind.NetworkBase58Address => IsValidNetworkBase58Address(
                definition.Name,
                candidate
            ),
            BuiltInValidationKind.NetworkBech32Address => IsValidNetworkBech32Address(
                definition.Name,
                candidate
            ),
            BuiltInValidationKind.SolanaAddress => IsValidSolanaAddress(candidate),
            BuiltInValidationKind.StellarAddress => IsValidStellarAddress(candidate),
            BuiltInValidationKind.BitcoinCashAddress => IsValidBitcoinCashAddress(candidate),
            BuiltInValidationKind.TonAddress => IsValidTonAddress(candidate),
            BuiltInValidationKind.SuiAddress => IsValidSuiAddress(candidate),
            BuiltInValidationKind.NearAccount => IsValidNearAccount(candidate),
            BuiltInValidationKind.BittensorAddress => IsValidBittensorAddress(candidate),
            BuiltInValidationKind.HederaAddress => IsValidHederaAddress(candidate),
            BuiltInValidationKind.CantonParty => IsValidCantonParty(candidate),
            BuiltInValidationKind.CryptoNoteAddress => IsValidCryptoNoteAddress(
                definition.Name,
                candidate
            ),
            BuiltInValidationKind.DashBase58Check => IsValidBase58Check(
                candidate,
                76,
                16
            ),
            BuiltInValidationKind.OnionV3 => IsValidOnionV3(candidate),
            BuiltInValidationKind.EthereumAddress => IsValidEthereumAddress(candidate),
            BuiltInValidationKind.SecurityIdentifier => IsValidSecurityIdentifier(candidate),
            BuiltInValidationKind.XmlElement => IsWellFormedXmlElement(candidate),
            BuiltInValidationKind.UriUserInfo => HasValidPercentEncoding(candidate),
            BuiltInValidationKind.AbsoluteUri => IsValidAbsoluteUri(candidate),
            BuiltInValidationKind.Jwt => IsValidJwt(candidate),
            BuiltInValidationKind.Iban => IsValidIban(candidate),
            BuiltInValidationKind.CanadianSin => HasValidLuhnChecksum(candidate, 9, 9),
            BuiltInValidationKind.DateOfBirth => IsValidDateOfBirth(candidate),
            BuiltInValidationKind.Cpe23 => IsValidCpe23(candidate),
            BuiltInValidationKind.EmailMessageId => IsValidEmailMessageId(candidate),
            BuiltInValidationKind.Lei => IsValidLei(candidate),
            BuiltInValidationKind.Npi => IsValidNpi(candidate),
            BuiltInValidationKind.Itin => IsValidItin(candidate),
            BuiltInValidationKind.UkNino => IsValidUkNino(candidate),
            _ => false,
        };
    }

    private static bool IsSeparatedMacAddress(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != 17 || candidate[2] is not (':' or '-'))
        {
            return false;
        }

        var separator = candidate[2];
        for (var index = 0; index < candidate.Length; index++)
        {
            if (index % 3 == 2)
            {
                if (candidate[index] != separator)
                {
                    return false;
                }
            }
            else if (!Uri.IsHexDigit(candidate[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUnseparatedMacAddress(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != 12)
        {
            return false;
        }

        foreach (var character in candidate)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIpv6Address(ReadOnlySpan<char> candidate) =>
        IPAddress.TryParse(candidate, out var address)
        && address.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool IsValidCpe23(ReadOnlySpan<char> candidate)
    {
        const string prefix = "cpe:2.3:";
        if (
            candidate.Length is <= 8 or > 8192
            || !candidate.StartsWith(prefix, StringComparison.Ordinal)
        )
        {
            return false;
        }

        var componentCount = 0;
        var componentLength = 0;
        var escaped = false;
        var firstComponent = '\0';
        var firstComponentLength = 0;
        for (var index = prefix.Length; index < candidate.Length; index++)
        {
            var character = candidate[index];
            if (escaped)
            {
                if (character is '\r' or '\n' || char.IsControl(character))
                {
                    return false;
                }
                escaped = false;
                componentLength += 2;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }
            if (character == ':')
            {
                if (componentLength is <= 0 or > 512)
                {
                    return false;
                }
                componentCount++;
                componentLength = 0;
                continue;
            }
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                return false;
            }
            if (componentCount == 0 && componentLength == 0)
            {
                firstComponent = character;
            }
            componentLength++;
            if (componentCount == 0)
            {
                firstComponentLength++;
            }
        }

        if (escaped || componentLength is <= 0 or > 512)
        {
            return false;
        }
        componentCount++;

        return componentCount == 11
            && firstComponentLength == 1
            && firstComponent is 'a' or 'h' or 'o' or '*' or '-';
    }

    private static bool IsValidEmailMessageId(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length is < 5 or > 320 || candidate[0] != '<' || candidate[^1] != '>')
        {
            return false;
        }

        var content = candidate[1..^1];
        var at = content.IndexOf('@');
        if (
            at is < 1 or > 64
            || at != content.LastIndexOf('@')
            || content.Length - at - 1 is < 1 or > 253
        )
        {
            return false;
        }

        var local = content[..at];
        if (local[0] == '.' || local[^1] == '.' || local.IndexOf("..", StringComparison.Ordinal) >= 0)
        {
            return false;
        }

        var domain = content[(at + 1)..];
        var labelLength = 0;
        for (var index = 0; index < domain.Length; index++)
        {
            var character = domain[index];
            if (character == '.')
            {
                if (labelLength == 0 || domain[index - 1] == '-')
                {
                    return false;
                }
                labelLength = 0;
                continue;
            }
            if (
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                || (labelLength == 0 && character == '-')
                || ++labelLength > 63
            )
            {
                return false;
            }
        }
        return labelLength > 0 && domain[^1] != '-';
    }

    private static bool IsValidLei(ReadOnlySpan<char> candidate)
    {
        if (
            candidate.Length != 20
            || candidate[4] != '0'
            || candidate[5] != '0'
            || candidate[^2] is < '0' or > '9'
            || candidate[^1] is < '0' or > '9'
        )
        {
            return false;
        }

        var remainder = 0;
        foreach (var character in candidate)
        {
            if (character is >= '0' and <= '9')
            {
                remainder = ((remainder * 10) + character - '0') % 97;
            }
            else if (character is >= 'A' and <= 'Z')
            {
                remainder = ((remainder * 100) + character - 'A' + 10) % 97;
            }
            else
            {
                return false;
            }
        }
        return remainder == 1;
    }

    private static bool IsValidNpi(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != 10 || candidate[0] is not ('1' or '2'))
        {
            return false;
        }

        Span<char> luhnInput = stackalloc char[15];
        "80840".AsSpan().CopyTo(luhnInput);
        candidate.CopyTo(luhnInput[5..]);
        return HasValidLuhnChecksum(luhnInput, 15, 15);
    }

    private static bool IsValidItin(ReadOnlySpan<char> candidate)
    {
        Span<char> digits = stackalloc char[9];
        var digitCount = 0;
        foreach (var character in candidate)
        {
            if (character is ' ' or '-')
            {
                continue;
            }
            if (character is < '0' or > '9' || digitCount == digits.Length)
            {
                return false;
            }
            digits[digitCount++] = character;
        }
        if (digitCount != 9 || digits[0] != '9')
        {
            return false;
        }

        var group = ((digits[3] - '0') * 10) + digits[4] - '0';
        return group is >= 50 and <= 65
            or >= 70 and <= 88
            or >= 90 and <= 92
            or >= 94 and <= 99;
    }

    private static bool IsValidUkNino(ReadOnlySpan<char> candidate)
    {
        if (
            candidate.Length != 9
            || candidate[0] is < 'A' or > 'Z'
            || candidate[1] is < 'A' or > 'Z'
            || candidate[8] is < 'A' or > 'D'
        )
        {
            return false;
        }
        for (var index = 2; index < 8; index++)
        {
            if (candidate[index] is < '0' or > '9')
            {
                return false;
            }
        }

        const string invalidFirst = "DFIQUV";
        const string invalidSecond = "DFIOQUV";
        if (invalidFirst.Contains(candidate[0]) || invalidSecond.Contains(candidate[1]))
        {
            return false;
        }

        return !(
            candidate[..2].SequenceEqual("BG")
            || candidate[..2].SequenceEqual("GB")
            || candidate[..2].SequenceEqual("KN")
            || candidate[..2].SequenceEqual("NK")
            || candidate[..2].SequenceEqual("NT")
            || candidate[..2].SequenceEqual("TN")
            || candidate[..2].SequenceEqual("ZZ")
        );
    }

    private static bool IsValidAbsoluteUri(ReadOnlySpan<char> candidate)
    {
        if (!HasValidPercentEncoding(candidate))
        {
            return false;
        }

        var schemeEnd = candidate.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return false;
        }

        var authority = candidate[(schemeEnd + 3)..];
        var authorityEnd = authority.IndexOfAny('/', '?', '#');
        if (authorityEnd >= 0)
        {
            authority = authority[..authorityEnd];
        }
        var userInfoEnd = authority.LastIndexOf('@');
        if (userInfoEnd >= 0)
        {
            authority = authority[(userInfoEnd + 1)..];
        }

        if (authority.IsEmpty || authority[0] != '[')
        {
            return true;
        }

        var closingBracket = authority.IndexOf(']');
        if (closingBracket < 2)
        {
            return false;
        }
        var literal = authority[1..closingBracket];
        if (literal[0] is 'v' or 'V')
        {
            return IsValidIpvFuture(literal);
        }

        return IPAddress.TryParse(literal, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IsValidIpvFuture(ReadOnlySpan<char> literal)
    {
        var period = literal.IndexOf('.');
        if (period < 2 || period == literal.Length - 1)
        {
            return false;
        }
        for (var index = 1; index < period; index++)
        {
            if (!IsHexDigit(literal[index]))
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasValidPercentEncoding(ReadOnlySpan<char> candidate)
    {
        for (var index = 0; index < candidate.Length; index++)
        {
            if (candidate[index] != '%')
            {
                continue;
            }
            if (
                index + 2 >= candidate.Length
                || !IsHexDigit(candidate[index + 1])
                || !IsHexDigit(candidate[index + 2])
            )
            {
                return false;
            }
            index += 2;
        }
        return true;
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    private static bool IsValidEmail(ReadOnlySpan<char> candidate)
    {
        if (
            !TryValidateEmailCandidate(candidate, out var at, out var lastDomainDot)
            || !IsCommonEmailLocalPart(candidate[..at])
        )
        {
            return false;
        }

        return IanaTopLevelDomains.Contains(candidate[(lastDomainDot + 1)..]);
    }

    private static bool IsValidEmailCandidate(ReadOnlySpan<char> candidate) =>
        TryValidateEmailCandidate(candidate, out _, out _);

    private static bool TryValidateEmailCandidate(
        ReadOnlySpan<char> candidate,
        out int at,
        out int lastDomainDot
    )
    {
        at = candidate.IndexOf('@');
        lastDomainDot = -1;
        if (
            at is < 1 or > 64
            || at != candidate.LastIndexOf('@')
            || candidate.Length > 254
        )
        {
            return false;
        }

        var domain = candidate[(at + 1)..];
        if (domain.Length is < 3 or > 253 || !IsRfcDotAtom(candidate[..at]))
        {
            return false;
        }

        var labelStart = 0;
        var labelCount = 0;
        for (var index = 0; index <= domain.Length; index++)
        {
            if (index < domain.Length && domain[index] != '.')
            {
                continue;
            }

            var label = domain[labelStart..index];
            if (
                label.Length is < 1 or > 63
                || !IsAsciiAlphaNumeric(label[0])
                || !IsAsciiAlphaNumeric(label[^1])
            )
            {
                return false;
            }

            for (var labelIndex = 1; labelIndex < label.Length - 1; labelIndex++)
            {
                if (!IsAsciiAlphaNumeric(label[labelIndex]) && label[labelIndex] != '-')
                {
                    return false;
                }
            }

            labelCount++;
            if (index < domain.Length)
            {
                lastDomainDot = at + 1 + index;
            }
            labelStart = index + 1;
        }

        return labelCount >= 2 && lastDomainDot > at + 1;
    }

    private static bool IsRfcDotAtom(ReadOnlySpan<char> localPart)
    {
        var previousWasDot = true;
        foreach (var character in localPart)
        {
            if (character == '.')
            {
                if (previousWasDot)
                {
                    return false;
                }
                previousWasDot = true;
                continue;
            }

            if (!IsRfcAtext(character))
            {
                return false;
            }
            previousWasDot = false;
        }

        return !previousWasDot;
    }

    private static bool IsCommonEmailLocalPart(ReadOnlySpan<char> localPart)
    {
        if (
            localPart.IsEmpty
            || !IsAsciiAlphaNumeric(localPart[0])
            || !IsAsciiAlphaNumeric(localPart[^1])
        )
        {
            return false;
        }

        for (var index = 0; index < localPart.Length; index++)
        {
            var character = localPart[index];
            if (
                !IsAsciiAlphaNumeric(character)
                && character is not '_' and not '+' and not '-' and not '.'
            )
            {
                return false;
            }

            if (
                character == '.'
                && (
                    index == 0
                    || index + 1 == localPart.Length
                    || !IsAsciiAlphaNumeric(localPart[index - 1])
                    || !IsAsciiAlphaNumeric(localPart[index + 1])
                )
            )
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsRfcAtext(char value) =>
        IsAsciiAlphaNumeric(value)
        || value
            is '!'
                or '#'
                or '$'
                or '%'
                or '&'
                or '\''
                or '*'
                or '+'
                or '-'
                or '/'
                or '='
                or '?'
                or '^'
                or '_'
                or '`'
                or '{'
                or '|'
                or '}'
                or '~';

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool HasValidLuhnChecksum(ReadOnlySpan<char> candidate) =>
        HasValidLuhnChecksum(candidate, 13, 19);

    private static bool HasValidLuhnChecksum(
        ReadOnlySpan<char> candidate,
        int minimumDigits,
        int maximumDigits
    )
    {
        var sum = 0;
        var digitCount = 0;
        var doubleDigit = false;

        for (var index = candidate.Length - 1; index >= 0; index--)
        {
            var character = candidate[index];
            if (character is ' ' or '-')
            {
                continue;
            }
            if (character is < '0' or > '9')
            {
                return false;
            }

            var digit = character - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            digitCount++;
            doubleDigit = !doubleDigit;
        }

        return digitCount >= minimumDigits && digitCount <= maximumDigits && sum % 10 == 0;
    }

    private static bool IsValidJwt(ReadOnlySpan<char> candidate)
    {
        var segments = candidate.ToString().Split('.', StringSplitOptions.None);
        if (segments.Length == 3)
        {
            if (
                !TryDecodeCanonicalBase64Url(segments[0], allowEmpty: false, out var header)
                || !TryDecodeCanonicalBase64Url(segments[1], allowEmpty: false, out var payload)
                || !TryGetJsonStringProperty(header, "alg", out var algorithm)
                || !IsJsonObject(payload)
            )
            {
                return false;
            }

            var unsecured = algorithm.Equals("none", StringComparison.Ordinal);
            return unsecured
                ? segments[2].Length == 0
                : TryDecodeCanonicalBase64Url(
                    segments[2],
                    allowEmpty: false,
                    out _
                );
        }

        if (segments.Length != 5)
        {
            return false;
        }
        if (
            !TryDecodeCanonicalBase64Url(segments[0], allowEmpty: false, out var protectedHeader)
            || !TryGetJsonStringProperty(protectedHeader, "alg", out _)
            || !TryGetJsonStringProperty(protectedHeader, "enc", out _)
        )
        {
            return false;
        }

        return TryDecodeCanonicalBase64Url(segments[1], allowEmpty: true, out _)
            && TryDecodeCanonicalBase64Url(segments[2], allowEmpty: false, out _)
            && TryDecodeCanonicalBase64Url(segments[3], allowEmpty: false, out _)
            && TryDecodeCanonicalBase64Url(segments[4], allowEmpty: false, out _);
    }

    private static bool TryDecodeCanonicalBase64Url(
        string value,
        bool allowEmpty,
        out byte[] decoded
    )
    {
        decoded = [];
        if (value.Length == 0)
        {
            return allowEmpty;
        }
        if (value.Length % 4 == 1)
        {
            return false;
        }
        foreach (var character in value)
        {
            if (
                character is not (>= 'A' and <= 'Z')
                    and not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '-'
                    and not '_'
            )
            {
                return false;
            }
        }

        var standard = value.Replace('-', '+').Replace('_', '/');
        standard += new string('=', (4 - standard.Length % 4) % 4);
        try
        {
            decoded = Convert.FromBase64String(standard);
        }
        catch (FormatException)
        {
            return false;
        }

        var canonical = Convert.ToBase64String(decoded)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return string.Equals(canonical, value, StringComparison.Ordinal);
    }

    private static bool TryGetJsonStringProperty(
        ReadOnlySpan<byte> json,
        string name,
        out string value
    )
    {
        value = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            if (
                document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty(name, out var property)
                || property.ValueKind != JsonValueKind.String
            )
            {
                return false;
            }
            value = property.GetString() ?? string.Empty;
            return value.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsJsonObject(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidIban(ReadOnlySpan<char> candidate)
    {
        Span<char> compact = stackalloc char[34];
        var length = 0;
        foreach (var character in candidate)
        {
            if (character == ' ')
            {
                continue;
            }
            if (length >= compact.Length)
            {
                return false;
            }
            compact[length++] = char.ToUpperInvariant(character);
        }
        var iban = compact[..length];
        if (
            iban.Length is < 15 or > 34
            || iban[0] is < 'A' or > 'Z'
            || iban[1] is < 'A' or > 'Z'
            || iban[2] is < '0' or > '9'
            || iban[3] is < '0' or > '9'
        )
        {
            return false;
        }

        var remainder = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            var start = pass == 0 ? 4 : 0;
            var end = pass == 0 ? iban.Length : 4;
            for (var index = start; index < end; index++)
            {
                var character = iban[index];
                if (character is >= '0' and <= '9')
                {
                    remainder = ((remainder * 10) + character - '0') % 97;
                }
                else if (character is >= 'A' and <= 'Z')
                {
                    var value = character - 'A' + 10;
                    remainder = ((remainder * 10) + (value / 10)) % 97;
                    remainder = ((remainder * 10) + (value % 10)) % 97;
                }
                else
                {
                    return false;
                }
            }
        }
        return remainder == 1;
    }

    private static bool IsValidDateOfBirth(ReadOnlySpan<char> candidate)
    {
        string[] formats =
        [
            "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d",
            "MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy", "M-d-yyyy",
            "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy",
        ];
        DateOnly date = default;
        var parsed = false;
        foreach (var format in formats)
        {
            if (
                DateOnly.TryParseExact(
                    candidate,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out date
                )
            )
            {
                parsed = true;
                break;
            }
        }
        if (!parsed)
        {
            return false;
        }
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return date.Year >= 1900 && date <= today;
    }

    private static bool IsValidBitLockerRecoveryPassword(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != 55)
        {
            return false;
        }

        for (var block = 0; block < 8; block++)
        {
            var start = block * 7;
            var value = 0;
            for (var index = 0; index < 6; index++)
            {
                var character = candidate[start + index];
                if (character is < '0' or > '9')
                {
                    return false;
                }
                value = (value * 10) + character - '0';
            }

            if (value >= 720_896 || value % 11 != 0)
            {
                return false;
            }
            if (block < 7 && candidate[start + 6] != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidBase58Check(
        ReadOnlySpan<char> candidate,
        byte versionA,
        byte versionB
    )
    {
        Span<byte> decoded = stackalloc byte[25];
        decoded.Clear();

        foreach (var character in candidate)
        {
            var digit = Base58Alphabet.IndexOf(character);
            if (digit < 0)
            {
                return false;
            }

            var carry = digit;
            for (var index = decoded.Length - 1; index >= 0; index--)
            {
                carry += decoded[index] * 58;
                decoded[index] = (byte)carry;
                carry >>= 8;
            }
            if (carry != 0)
            {
                return false;
            }
        }

        var leadingOnes = 0;
        while (leadingOnes < candidate.Length && candidate[leadingOnes] == '1')
        {
            leadingOnes++;
        }
        var leadingZeros = 0;
        while (leadingZeros < decoded.Length && decoded[leadingZeros] == 0)
        {
            leadingZeros++;
        }
        if (leadingOnes != leadingZeros || (decoded[0] != versionA && decoded[0] != versionB))
        {
            return false;
        }

        Span<byte> firstHash = stackalloc byte[32];
        Span<byte> secondHash = stackalloc byte[32];
        SHA256.HashData(decoded[..21], firstHash);
        SHA256.HashData(firstHash, secondHash);
        return CryptographicOperations.FixedTimeEquals(decoded[21..], secondHash[..4]);
    }

    private enum Bech32Encoding
    {
        Invalid,
        Bech32,
        Bech32m,
    }

    private static bool IsValidNetworkBase58Address(
        string patternName,
        ReadOnlySpan<char> candidate
    )
    {
        Span<byte> decoded = stackalloc byte[64];
        var alphabet = patternName == "xrp" ? RippleBase58Alphabet : Base58Alphabet;
        if (!TryDecodeBase58(candidate, alphabet, decoded, out var decodedLength))
        {
            return false;
        }

        var value = decoded[..decodedLength];
        return patternName switch
        {
            "tron" =>
                value.Length == 25
                && value[0] == 0x41
                && HasDoubleSha256Checksum(value),
            "xrp" =>
                value.Length == 25
                && value[0] == 0x00
                && HasDoubleSha256Checksum(value),
            "dogecoin" =>
                value.Length == 25
                && value[0] is 30 or 22
                && HasDoubleSha256Checksum(value),
            _ => false,
        };
    }

    private static bool IsValidNetworkBech32Address(
        string patternName,
        ReadOnlySpan<char> candidate
    )
    {
        if (patternName == "zcash" && candidate.StartsWith("t", StringComparison.Ordinal))
        {
            Span<byte> transparent = stackalloc byte[32];
            return TryDecodeBase58(
                    candidate,
                    Base58Alphabet,
                    transparent,
                    out var transparentLength
                )
                && transparentLength == 26
                && transparent[0] == 0x1C
                && transparent[1] is 0xB8 or 0xBD
                && HasDoubleSha256Checksum(transparent[..transparentLength]);
        }

        if (
            patternName == "litecoin"
            && !candidate.StartsWith("ltc1", StringComparison.OrdinalIgnoreCase)
        )
        {
            Span<byte> legacy = stackalloc byte[32];
            return TryDecodeBase58(
                    candidate,
                    Base58Alphabet,
                    legacy,
                    out var legacyLength
                )
                && legacyLength == 25
                && legacy[0] is 48 or 50
                && HasDoubleSha256Checksum(legacy[..legacyLength]);
        }

        var encoded = patternName == "avalanche" ? candidate[2..] : candidate;
        Span<byte> values = stackalloc byte[192];
        if (
            !TryDecodeBech32(
                encoded,
                values,
                out var valueCount,
                out var separator,
                out var encoding
            )
        )
        {
            return false;
        }

        var hrp = encoded[..separator];
        if (patternName == "litecoin")
        {
            return IsValidWitnessPayload(
                hrp,
                "ltc",
                values[..valueCount],
                encoding
            );
        }

        Span<byte> decoded = stackalloc byte[128];
        if (
            !TryConvertBits(values[..valueCount], 5, 8, pad: false, decoded, out var decodedLength)
        )
        {
            return false;
        }

        return patternName switch
        {
            "zcash" =>
                hrp.Equals("zs", StringComparison.OrdinalIgnoreCase)
                && encoding == Bech32Encoding.Bech32
                && decodedLength == 43,
            "cardano" =>
                hrp.SequenceEqual("addr")
                && encoding == Bech32Encoding.Bech32
                && IsValidCardanoPayload(decoded[..decodedLength]),
            "avalanche" =>
                hrp.SequenceEqual("avax")
                && encoding == Bech32Encoding.Bech32
                && decodedLength == 20,
            "provenance_scope" =>
                hrp.SequenceEqual("scope")
                && encoding == Bech32Encoding.Bech32
                && decodedLength == 17
                && decoded[0] == 0x00,
            _ => false,
        };
    }

    private static bool IsValidSolanaAddress(ReadOnlySpan<char> candidate)
    {
        Span<byte> decoded = stackalloc byte[48];
        return TryDecodeBase58(
                candidate,
                Base58Alphabet,
                decoded,
                out var decodedLength
            )
            && decodedLength == 32;
    }

    private static bool IsValidWitnessAddress(
        ReadOnlySpan<char> candidate,
        ReadOnlySpan<char> expectedHrp
    )
    {
        if (candidate.Length > 90)
        {
            return false;
        }

        Span<byte> values = stackalloc byte[96];
        return TryDecodeBech32(
                candidate,
                values,
                out var valueCount,
                out var separator,
                out var encoding
            )
            && IsValidWitnessPayload(
                candidate[..separator],
                expectedHrp,
                values[..valueCount],
                encoding
            );
    }

    private static bool IsValidWitnessPayload(
        ReadOnlySpan<char> actualHrp,
        ReadOnlySpan<char> expectedHrp,
        ReadOnlySpan<byte> values,
        Bech32Encoding encoding
    )
    {
        if (
            !actualHrp.Equals(expectedHrp, StringComparison.OrdinalIgnoreCase)
            || values.Length < 2
            || values[0] > 16
        )
        {
            return false;
        }

        Span<byte> program = stackalloc byte[40];
        if (!TryConvertBits(values[1..], 5, 8, pad: false, program, out var programLength))
        {
            return false;
        }

        var version = values[0];
        return programLength is >= 2 and <= 40
            && (version != 0 || programLength is 20 or 32)
            && (version == 0
                ? encoding == Bech32Encoding.Bech32
                : encoding == Bech32Encoding.Bech32m);
    }

    private static bool IsValidCardanoPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty || (payload[0] & 0x0F) != 1)
        {
            return false;
        }

        var addressType = payload[0] >> 4;
        return addressType switch
        {
            <= 3 => payload.Length == 57,
            4 or 5 => payload.Length > 29 && HasCanonicalCardanoPointer(payload[29..]),
            6 or 7 => payload.Length == 29,
            _ => false,
        };
    }

    private static bool HasCanonicalCardanoPointer(ReadOnlySpan<byte> pointer)
    {
        for (var coordinate = 0; coordinate < 3; coordinate++)
        {
            if (pointer.IsEmpty)
            {
                return false;
            }
            var bytesRead = 0;
            var firstPayload = pointer[0] & 0x7F;
            while (true)
            {
                if (pointer.IsEmpty || ++bytesRead > 10)
                {
                    return false;
                }
                var current = pointer[0];
                pointer = pointer[1..];
                if ((current & 0x80) == 0)
                {
                    break;
                }
            }
            if (bytesRead > 1 && firstPayload == 0)
            {
                return false;
            }
        }
        return pointer.IsEmpty;
    }

    private static bool IsValidStellarAddress(ReadOnlySpan<char> candidate)
    {
        Span<byte> decoded = stackalloc byte[35];
        if (!TryDecodeBase32(candidate, decoded, out var decodedLength) || decodedLength != 35)
        {
            return false;
        }

        var expected = Crc16Xmodem(decoded[..33]);
        return decoded[0] == (6 << 3)
            && decoded[33] == (byte)expected
            && decoded[34] == (byte)(expected >> 8);
    }

    private static bool IsValidBitcoinCashAddress(ReadOnlySpan<char> candidate)
    {
        const string prefix = "bitcoincash";
        if (!candidate.StartsWith(prefix + ":", StringComparison.Ordinal))
        {
            return false;
        }

        var payload = candidate[(prefix.Length + 1)..];
        Span<byte> values = stackalloc byte[80];
        if (payload.Length <= 8)
        {
            return false;
        }
        for (var index = 0; index < payload.Length; index++)
        {
            var value = Bech32Alphabet.IndexOf(payload[index]);
            if (value < 0)
            {
                return false;
            }
            values[index] = (byte)value;
        }
        if (!HasValidCashAddressChecksum(prefix, values[..payload.Length]))
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[64];
        return TryConvertBits(
                values[..(payload.Length - 8)],
                5,
                8,
                pad: false,
                decoded,
                out var decodedLength
            )
            && decodedLength == 21
            && decoded[0] is 0x00 or 0x08;
    }

    private static bool IsValidTonAddress(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != 48)
        {
            return false;
        }

        Span<char> base64 = stackalloc char[48];
        for (var index = 0; index < candidate.Length; index++)
        {
            base64[index] = candidate[index] switch
            {
                '-' => '+',
                '_' => '/',
                var character => character,
            };
        }

        Span<byte> decoded = stackalloc byte[36];
        if (!Convert.TryFromBase64Chars(base64, decoded, out var bytesWritten) || bytesWritten != 36)
        {
            return false;
        }

        var checksum = Crc16Xmodem(decoded[..34]);
        return decoded[0] is 0x11 or 0x51
            && decoded[1] is 0x00 or 0xFF
            && decoded[34] == (byte)(checksum >> 8)
            && decoded[35] == (byte)checksum;
    }

    private static bool IsValidSuiAddress(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != 66 || !candidate.StartsWith("0x", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in candidate[2..])
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsValidNearAccount(ReadOnlySpan<char> candidate)
    {
        if (
            candidate.Length is < 2 or > 64
            || !candidate.EndsWith(".near", StringComparison.Ordinal)
            || !IsLowerAsciiLetterOrDigit(candidate[0])
            || !IsLowerAsciiLetterOrDigit(candidate[^1])
        )
        {
            return false;
        }

        var previousWasSeparator = false;
        foreach (var character in candidate)
        {
            var isSeparator = character is '.' or '-' or '_';
            if (!IsLowerAsciiLetterOrDigit(character) && !isSeparator)
            {
                return false;
            }
            if (isSeparator && previousWasSeparator)
            {
                return false;
            }
            previousWasSeparator = isSeparator;
        }
        return !previousWasSeparator;
    }

    private static bool IsValidBittensorAddress(ReadOnlySpan<char> candidate)
    {
        Span<byte> decoded = stackalloc byte[40];
        if (
            !TryDecodeBase58(candidate, Base58Alphabet, decoded, out var decodedLength)
            || decodedLength != 35
            || decoded[0] != 42
        )
        {
            return false;
        }

        ReadOnlySpan<byte> domain = "SS58PRE"u8;
        Span<byte> input = stackalloc byte[domain.Length + 33];
        domain.CopyTo(input);
        decoded[..33].CopyTo(input[domain.Length..]);
        Span<byte> hash = stackalloc byte[64];
        Blake2b512(input, hash);
        return CryptographicOperations.FixedTimeEquals(decoded[33..35], hash[..2]);
    }

    private static bool IsValidHederaAddress(ReadOnlySpan<char> candidate)
    {
        var dash = candidate.LastIndexOf('-');
        if (dash <= 0 || candidate.Length - dash - 1 != 5)
        {
            return false;
        }

        var address = candidate[..dash];
        Span<char> expected = stackalloc char[5];
        var sd0 = 0;
        var sd1 = 0;
        var sd = 0;
        var digitIndex = 0;
        foreach (var character in address)
        {
            var digit = character == '.' ? 10 : character - '0';
            if (digit is < 0 or > 10)
            {
                return false;
            }
            if ((digitIndex & 1) == 0)
            {
                sd0 = (sd0 + digit) % 11;
            }
            else
            {
                sd1 = (sd1 + digit) % 11;
            }
            sd = ((sd * 31) + digit) % 17_576;
            digitIndex++;
        }

        const int p3 = 17_576;
        const int p5 = 11_881_376;
        long checksumValue = (((digitIndex % 5) * 11 + sd0) * 11 + sd1) * p3 + sd;
        checksumValue = (checksumValue * 1_000_003L) % p5;
        for (var index = 4; index >= 0; index--)
        {
            expected[index] = (char)('a' + (checksumValue % 26));
            checksumValue /= 26;
        }
        return candidate[(dash + 1)..].SequenceEqual(expected);
    }

    private static bool IsValidCantonParty(ReadOnlySpan<char> candidate)
    {
        var separator = candidate.IndexOf("::", StringComparison.Ordinal);
        if (separator is < 1 or > 185 || candidate.Length - separator - 2 != 68)
        {
            return false;
        }
        foreach (var character in candidate[..separator])
        {
            if (
                !IsLowerAsciiLetterOrDigit(char.ToLowerInvariant(character))
                && character is not '_' and not '-'
            )
            {
                return false;
            }
        }

        var fingerprint = candidate[(separator + 2)..];
        if (!fingerprint.StartsWith("1220", StringComparison.Ordinal))
        {
            return false;
        }
        foreach (var character in fingerprint[4..])
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLowerAsciiLetterOrDigit(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool TryDecodeBase58(
        ReadOnlySpan<char> encoded,
        ReadOnlySpan<char> alphabet,
        Span<byte> destination,
        out int bytesWritten
    )
    {
        Span<byte> littleEndian = stackalloc byte[128];
        var significantLength = 0;
        var leadingZeros = 0;
        while (leadingZeros < encoded.Length && encoded[leadingZeros] == alphabet[0])
        {
            leadingZeros++;
        }

        foreach (var character in encoded)
        {
            var digit = alphabet.IndexOf(character);
            if (digit < 0)
            {
                bytesWritten = 0;
                return false;
            }

            var carry = digit;
            for (var index = 0; index < significantLength; index++)
            {
                carry += littleEndian[index] * 58;
                littleEndian[index] = (byte)carry;
                carry >>= 8;
            }
            while (carry != 0)
            {
                if (significantLength == littleEndian.Length)
                {
                    bytesWritten = 0;
                    return false;
                }
                littleEndian[significantLength++] = (byte)carry;
                carry >>= 8;
            }
        }

        bytesWritten = leadingZeros + significantLength;
        if (bytesWritten > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }
        destination[..leadingZeros].Clear();
        for (var index = 0; index < significantLength; index++)
        {
            destination[leadingZeros + index] = littleEndian[significantLength - index - 1];
        }
        return true;
    }

    private static bool HasDoubleSha256Checksum(ReadOnlySpan<byte> decoded)
    {
        if (decoded.Length < 5)
        {
            return false;
        }
        Span<byte> first = stackalloc byte[32];
        Span<byte> second = stackalloc byte[32];
        SHA256.HashData(decoded[..^4], first);
        SHA256.HashData(first, second);
        return CryptographicOperations.FixedTimeEquals(decoded[^4..], second[..4]);
    }

    private static bool TryDecodeBech32(
        ReadOnlySpan<char> encoded,
        Span<byte> data,
        out int dataLength,
        out int separator,
        out Bech32Encoding encoding
    )
    {
        dataLength = 0;
        separator = encoded.LastIndexOf('1');
        encoding = Bech32Encoding.Invalid;
        if (separator < 1 || separator + 7 > encoded.Length || encoded.Length > 196)
        {
            return false;
        }

        var hasLower = false;
        var hasUpper = false;
        foreach (var character in encoded)
        {
            if (character is < (char)33 or > (char)126)
            {
                return false;
            }
            hasLower |= character is >= 'a' and <= 'z';
            hasUpper |= character is >= 'A' and <= 'Z';
        }
        if (hasLower && hasUpper)
        {
            return false;
        }

        uint polymod = 1;
        for (var index = 0; index < separator; index++)
        {
            polymod = Bech32PolymodStep(polymod) ^ (uint)(char.ToLowerInvariant(encoded[index]) >> 5);
        }
        polymod = Bech32PolymodStep(polymod);
        for (var index = 0; index < separator; index++)
        {
            polymod = Bech32PolymodStep(polymod) ^ (uint)(char.ToLowerInvariant(encoded[index]) & 31);
        }

        var encodedDataLength = encoded.Length - separator - 1;
        if (encodedDataLength > data.Length + 6)
        {
            return false;
        }
        for (var index = 0; index < encodedDataLength; index++)
        {
            var value = Bech32Alphabet.IndexOf(char.ToLowerInvariant(encoded[separator + 1 + index]));
            if (value < 0)
            {
                return false;
            }
            polymod = Bech32PolymodStep(polymod) ^ (uint)value;
            if (index < encodedDataLength - 6)
            {
                data[dataLength++] = (byte)value;
            }
        }

        encoding = polymod switch
        {
            1 => Bech32Encoding.Bech32,
            0x2BC830A3 => Bech32Encoding.Bech32m,
            _ => Bech32Encoding.Invalid,
        };
        return encoding != Bech32Encoding.Invalid;
    }

    private static uint Bech32PolymodStep(uint value)
    {
        var high = value >> 25;
        var result = (value & 0x1FFFFFF) << 5;
        ReadOnlySpan<uint> generators =
            [0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3];
        for (var bit = 0; bit < 5; bit++)
        {
            if (((high >> bit) & 1) != 0)
            {
                result ^= generators[bit];
            }
        }
        return result;
    }

    private static bool TryConvertBits(
        ReadOnlySpan<byte> input,
        int fromBits,
        int toBits,
        bool pad,
        Span<byte> output,
        out int outputLength
    )
    {
        var accumulator = 0;
        var bitCount = 0;
        var maxValue = (1 << toBits) - 1;
        var maxAccumulator = (1 << (fromBits + toBits - 1)) - 1;
        outputLength = 0;

        foreach (var value in input)
        {
            if ((value >> fromBits) != 0)
            {
                outputLength = 0;
                return false;
            }
            accumulator = ((accumulator << fromBits) | value) & maxAccumulator;
            bitCount += fromBits;
            while (bitCount >= toBits)
            {
                bitCount -= toBits;
                if (outputLength == output.Length)
                {
                    outputLength = 0;
                    return false;
                }
                output[outputLength++] = (byte)((accumulator >> bitCount) & maxValue);
            }
        }

        if (pad)
        {
            if (bitCount > 0)
            {
                output[outputLength++] = (byte)((accumulator << (toBits - bitCount)) & maxValue);
            }
            return true;
        }
        return bitCount < fromBits
            && ((accumulator << (toBits - bitCount)) & maxValue) == 0;
    }

    private static bool TryDecodeBase32(
        ReadOnlySpan<char> encoded,
        Span<byte> destination,
        out int bytesWritten
    )
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var accumulator = 0;
        var bitCount = 0;
        bytesWritten = 0;
        foreach (var character in encoded)
        {
            var value = alphabet.IndexOf(character);
            if (value < 0)
            {
                bytesWritten = 0;
                return false;
            }
            accumulator = (accumulator << 5) | value;
            bitCount += 5;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                if (bytesWritten == destination.Length)
                {
                    bytesWritten = 0;
                    return false;
                }
                destination[bytesWritten++] = (byte)(accumulator >> bitCount);
                accumulator &= (1 << bitCount) - 1;
            }
        }
        return bitCount == 0 || accumulator == 0;
    }

    private static ushort Crc16Xmodem(ReadOnlySpan<byte> data)
    {
        ushort crc = 0;
        foreach (var value in data)
        {
            crc ^= (ushort)(value << 8);
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
        }
        return crc;
    }

    private static bool HasValidCashAddressChecksum(
        ReadOnlySpan<char> prefix,
        ReadOnlySpan<byte> payload
    )
    {
        ulong checksum = 1;
        foreach (var character in prefix)
        {
            checksum = CashAddressPolymodStep(checksum) ^ (uint)(character & 31);
        }
        checksum = CashAddressPolymodStep(checksum);
        foreach (var value in payload)
        {
            checksum = CashAddressPolymodStep(checksum) ^ value;
        }
        return checksum == 1;
    }

    private static ulong CashAddressPolymodStep(ulong value)
    {
        var high = value >> 35;
        var result = (value & 0x07FFFFFFFF) << 5;
        ReadOnlySpan<ulong> generators =
            [0x98F2BC8E61, 0x79B76D99E2, 0xF33E5FB3C4, 0xAE2EABE2A8, 0x1E4F43E470];
        for (var bit = 0; bit < 5; bit++)
        {
            if (((high >> bit) & 1) != 0)
            {
                result ^= generators[bit];
            }
        }
        return result;
    }

    private static void Blake2b512(ReadOnlySpan<byte> input, Span<byte> destination)
    {
        if (input.Length > 128 || destination.Length < 64)
        {
            throw new ArgumentOutOfRangeException(nameof(input));
        }

        Span<ulong> state = stackalloc ulong[8]
        {
            0x6A09E667F3BCC908 ^ 0x01010040,
            0xBB67AE8584CAA73B,
            0x3C6EF372FE94F82B,
            0xA54FF53A5F1D36F1,
            0x510E527FADE682D1,
            0x9B05688C2B3E6C1F,
            0x1F83D9ABFB41BD6B,
            0x5BE0CD19137E2179,
        };
        Span<byte> block = stackalloc byte[128];
        block.Clear();
        input.CopyTo(block);
        Blake2bCompress(state, block, (ulong)input.Length, isFinal: true);
        for (var index = 0; index < state.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(destination[(index * 8)..], state[index]);
        }
    }

    private static void Blake2bCompress(
        Span<ulong> hash,
        ReadOnlySpan<byte> block,
        ulong byteCount,
        bool isFinal
    )
    {
        ReadOnlySpan<ulong> iv =
        [
            0x6A09E667F3BCC908, 0xBB67AE8584CAA73B,
            0x3C6EF372FE94F82B, 0xA54FF53A5F1D36F1,
            0x510E527FADE682D1, 0x9B05688C2B3E6C1F,
            0x1F83D9ABFB41BD6B, 0x5BE0CD19137E2179,
        ];
        ReadOnlySpan<byte> sigma =
        [
             0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15,
            14,10, 4, 8, 9,15,13, 6, 1,12, 0, 2,11, 7, 5, 3,
            11, 8,12, 0, 5, 2,15,13,10,14, 3, 6, 7, 1, 9, 4,
             7, 9, 3, 1,13,12,11,14, 2, 6, 5,10, 4, 0,15, 8,
             9, 0, 5, 7, 2, 4,10,15,14, 1,11,12, 6, 8, 3,13,
             2,12, 6,10, 0,11, 8, 3, 4,13, 7, 5,15,14, 1, 9,
            12, 5, 1,15,14,13, 4,10, 0, 7, 6, 3, 9, 2, 8,11,
            13,11, 7,14,12, 1, 3, 9, 5, 0,15, 4, 8, 6, 2,10,
             6,15,14, 9,11, 3, 0, 8,12, 2,13, 7, 1, 4,10, 5,
            10, 2, 8, 4, 7, 6, 1, 5,15,11, 9,14, 3,12,13, 0,
             0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15,
            14,10, 4, 8, 9,15,13, 6, 1,12, 0, 2,11, 7, 5, 3,
        ];
        Span<ulong> message = stackalloc ulong[16];
        Span<ulong> work = stackalloc ulong[16];
        for (var index = 0; index < 16; index++)
        {
            message[index] = BinaryPrimitives.ReadUInt64LittleEndian(block[(index * 8)..]);
        }
        hash.CopyTo(work);
        iv.CopyTo(work[8..]);
        work[12] ^= byteCount;
        if (isFinal)
        {
            work[14] = ~work[14];
        }

        for (var round = 0; round < 12; round++)
        {
            var offset = round * 16;
            Blake2bMix(work, 0, 4, 8, 12, message[sigma[offset]], message[sigma[offset + 1]]);
            Blake2bMix(work, 1, 5, 9, 13, message[sigma[offset + 2]], message[sigma[offset + 3]]);
            Blake2bMix(work, 2, 6, 10, 14, message[sigma[offset + 4]], message[sigma[offset + 5]]);
            Blake2bMix(work, 3, 7, 11, 15, message[sigma[offset + 6]], message[sigma[offset + 7]]);
            Blake2bMix(work, 0, 5, 10, 15, message[sigma[offset + 8]], message[sigma[offset + 9]]);
            Blake2bMix(work, 1, 6, 11, 12, message[sigma[offset + 10]], message[sigma[offset + 11]]);
            Blake2bMix(work, 2, 7, 8, 13, message[sigma[offset + 12]], message[sigma[offset + 13]]);
            Blake2bMix(work, 3, 4, 9, 14, message[sigma[offset + 14]], message[sigma[offset + 15]]);
        }
        for (var index = 0; index < 8; index++)
        {
            hash[index] ^= work[index] ^ work[index + 8];
        }
    }

    private static void Blake2bMix(
        Span<ulong> work,
        int a,
        int b,
        int c,
        int d,
        ulong x,
        ulong y
    )
    {
        work[a] = work[a] + work[b] + x;
        work[d] = BitOperations.RotateRight(work[d] ^ work[a], 32);
        work[c] += work[d];
        work[b] = BitOperations.RotateRight(work[b] ^ work[c], 24);
        work[a] = work[a] + work[b] + y;
        work[d] = BitOperations.RotateRight(work[d] ^ work[a], 16);
        work[c] += work[d];
        work[b] = BitOperations.RotateRight(work[b] ^ work[c], 63);
    }

    private static bool IsValidCryptoNoteAddress(
        string patternName,
        ReadOnlySpan<char> candidate
    )
    {
        Span<byte> decoded = stackalloc byte[80];
        if (!TryDecodeCryptoNoteBase58(candidate, decoded, out var decodedLength))
        {
            return false;
        }

        var address = decoded[..decodedLength];
        if (address.Length < 5)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[32];
        Keccak256(address[..^4], hash);
        if (!CryptographicOperations.FixedTimeEquals(address[^4..], hash[..4]))
        {
            return false;
        }

        if (!TryReadCanonicalVarInt(address[..^4], out var prefix, out var prefixLength))
        {
            return false;
        }

        var payloadLength = address.Length - prefixLength - 4;
        return patternName switch
        {
            "aeon" => prefix == 0xB2 && payloadLength == 64,
            "bytecoin" => prefix == 6 && payloadLength == 64,
            "dashcoin" => prefix == 72 && payloadLength == 64,
            "fantomcoin" => prefix == 34 && payloadLength == 64,
            "monero" =>
                (payloadLength == 64 && prefix is 18 or 42)
                || (payloadLength == 72 && prefix == 19),
            "sumokoin" => prefix == 0x2BB39A && payloadLength == 64,
            _ => false,
        };
    }

    private static bool TryDecodeCryptoNoteBase58(
        ReadOnlySpan<char> encoded,
        Span<byte> destination,
        out int bytesWritten
    )
    {
        const int fullEncodedBlockSize = 11;
        const int fullDecodedBlockSize = 8;
        var fullBlockCount = encoded.Length / fullEncodedBlockSize;
        var finalEncodedSize = encoded.Length % fullEncodedBlockSize;
        var decodedSizes = CryptoNoteDecodedBlockSizes;
        if (finalEncodedSize >= decodedSizes.Length || decodedSizes[finalEncodedSize] < 0)
        {
            bytesWritten = 0;
            return false;
        }

        var finalDecodedSize = decodedSizes[finalEncodedSize];
        bytesWritten = (fullBlockCount * fullDecodedBlockSize) + finalDecodedSize;
        if (bytesWritten > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        for (var block = 0; block < fullBlockCount; block++)
        {
            if (
                !TryDecodeCryptoNoteBlock(
                    encoded.Slice(block * fullEncodedBlockSize, fullEncodedBlockSize),
                    destination.Slice(block * fullDecodedBlockSize, fullDecodedBlockSize)
                )
            )
            {
                bytesWritten = 0;
                return false;
            }
        }

        if (
            finalEncodedSize > 0
            && !TryDecodeCryptoNoteBlock(
                encoded[^finalEncodedSize..],
                destination.Slice(fullBlockCount * fullDecodedBlockSize, finalDecodedSize)
            )
        )
        {
            bytesWritten = 0;
            return false;
        }

        return true;
    }

    private static bool TryDecodeCryptoNoteBlock(
        ReadOnlySpan<char> encoded,
        Span<byte> decoded
    )
    {
        ulong value = 0;
        foreach (var character in encoded)
        {
            var digit = Base58Alphabet.IndexOf(character);
            if (digit < 0 || value > (ulong.MaxValue - (uint)digit) / 58)
            {
                return false;
            }
            value = (value * 58) + (uint)digit;
        }

        if (decoded.Length < 8 && value >= (1UL << (decoded.Length * 8)))
        {
            return false;
        }

        for (var index = decoded.Length - 1; index >= 0; index--)
        {
            decoded[index] = (byte)value;
            value >>= 8;
        }
        return value == 0;
    }

    private static bool TryReadCanonicalVarInt(
        ReadOnlySpan<byte> data,
        out ulong value,
        out int bytesRead
    )
    {
        value = 0;
        bytesRead = 0;
        var shift = 0;
        foreach (var current in data)
        {
            if (bytesRead == 10 || (shift == 63 && (current & 0xFE) != 0))
            {
                return false;
            }

            value |= (ulong)(current & 0x7F) << shift;
            bytesRead++;
            if ((current & 0x80) == 0)
            {
                var canonicalLength = 1;
                for (var copy = value; copy >= 0x80; copy >>= 7)
                {
                    canonicalLength++;
                }
                return bytesRead == canonicalLength;
            }
            shift += 7;
        }

        return false;
    }

    private static bool IsValidOnionV3(ReadOnlySpan<char> candidate)
    {
        const int encodedLength = 56;
        if (candidate.Length != 62 || candidate[encodedLength] != '.')
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[35];
        var accumulator = 0;
        var bits = 0;
        var outputIndex = 0;
        for (var index = 0; index < encodedLength; index++)
        {
            var character = candidate[index];
            var value = character switch
            {
                >= 'a' and <= 'z' => character - 'a',
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => -1,
            };
            if (value < 0)
            {
                return false;
            }

            accumulator = (accumulator << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                decoded[outputIndex++] = (byte)(accumulator >> bits);
                accumulator &= (1 << bits) - 1;
            }
        }
        if (outputIndex != decoded.Length || bits != 0 || decoded[34] != 3)
        {
            return false;
        }

        ReadOnlySpan<byte> checksumPrefix = ".onion checksum"u8;
        Span<byte> checksumInput = stackalloc byte[checksumPrefix.Length + 33];
        checksumPrefix.CopyTo(checksumInput);
        decoded[..32].CopyTo(checksumInput[checksumPrefix.Length..]);
        checksumInput[^1] = decoded[34];
        Span<byte> checksum = stackalloc byte[32];
        SHA3_256.HashData(checksumInput, checksum);
        return CryptographicOperations.FixedTimeEquals(decoded.Slice(32, 2), checksum[..2]);
    }

    private static bool IsValidEthereumAddress(ReadOnlySpan<char> candidate)
    {
        if (candidate.Length != 42 || candidate[0] != '0' || candidate[1] != 'x')
        {
            return false;
        }

        var hexadecimal = candidate[2..];
        var hasLower = false;
        var hasUpper = false;
        foreach (var character in hexadecimal)
        {
            hasLower |= character is >= 'a' and <= 'f';
            hasUpper |= character is >= 'A' and <= 'F';
        }
        if (!hasLower || !hasUpper)
        {
            return true;
        }

        Span<byte> lowerAscii = stackalloc byte[40];
        for (var index = 0; index < hexadecimal.Length; index++)
        {
            lowerAscii[index] = (byte)char.ToLowerInvariant(hexadecimal[index]);
        }
        Span<byte> hash = stackalloc byte[32];
        Keccak256(lowerAscii, hash);

        for (var index = 0; index < hexadecimal.Length; index++)
        {
            var character = hexadecimal[index];
            if (character is not (>= 'A' and <= 'F') and not (>= 'a' and <= 'f'))
            {
                continue;
            }
            var nibble =
                (index & 1) == 0 ? hash[index / 2] >> 4 : hash[index / 2] & 0x0F;
            if ((nibble >= 8) != char.IsUpper(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidSecurityIdentifier(ReadOnlySpan<char> candidate)
    {
        if (!candidate.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        candidate = candidate[4..];
        var separator = candidate.IndexOf('-');
        var authorityText = separator < 0 ? candidate : candidate[..separator];
        var authorityStyle = NumberStyles.None;
        if (authorityText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            authorityText = authorityText[2..];
            authorityStyle = NumberStyles.AllowHexSpecifier;
        }
        if (
            authorityText.IsEmpty
            || !ulong.TryParse(
                authorityText,
                authorityStyle,
                CultureInfo.InvariantCulture,
                out var authority
            )
            || authority > 0xFFFF_FFFF_FFFF
        )
        {
            return false;
        }

        var subAuthorityCount = 0;
        while (separator >= 0)
        {
            candidate = candidate[(separator + 1)..];
            separator = candidate.IndexOf('-');
            var valueText = separator < 0 ? candidate : candidate[..separator];
            if (
                valueText.IsEmpty
                || !uint.TryParse(
                    valueText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _
                )
            )
            {
                return false;
            }
            subAuthorityCount++;
            if (subAuthorityCount > 15)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWellFormedXmlElement(ReadOnlySpan<char> candidate)
    {
        if (!HasQuotedXmlAttributeValues(candidate))
        {
            return false;
        }

        try
        {
            using var reader = XmlReader.Create(
                new StringReader(candidate.ToString()),
                SafeXmlReaderSettings
            );
            while (reader.Read()) { }
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool HasQuotedXmlAttributeValues(ReadOnlySpan<char> candidate)
    {
        var quote = '\0';
        for (var index = 1; index < candidate.Length; index++)
        {
            var character = candidate[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                continue;
            }
            if (character == '>')
            {
                return true;
            }
            if (character != '=')
            {
                continue;
            }

            do
            {
                index++;
            } while (
                index < candidate.Length
                && candidate[index] is ' ' or '\t' or '\r' or '\n'
            );
            if (
                index >= candidate.Length
                || candidate[index] is not ('\'' or '"')
            )
            {
                return false;
            }
            quote = candidate[index];
        }

        return false;
    }

    internal static void Keccak256(ReadOnlySpan<byte> input, Span<byte> destination)
    {
        if (destination.Length < 32)
        {
            throw new ArgumentException("Keccak-256 needs a 32-byte destination.", nameof(destination));
        }

        const int rate = 136;
        Span<ulong> state = stackalloc ulong[25];
        state.Clear();
        while (input.Length >= rate)
        {
            XorIntoState(input[..rate], state);
            KeccakF1600(state);
            input = input[rate..];
        }

        XorIntoState(input, state);
        state[input.Length / 8] ^= 0x01UL << ((input.Length % 8) * 8);
        state[(rate - 1) / 8] ^= 0x80UL << (((rate - 1) % 8) * 8);
        KeccakF1600(state);

        for (var index = 0; index < 32; index++)
        {
            destination[index] = (byte)(state[index / 8] >> ((index % 8) * 8));
        }
    }

    private static void XorIntoState(ReadOnlySpan<byte> input, Span<ulong> state)
    {
        for (var index = 0; index < input.Length; index++)
        {
            state[index / 8] ^= (ulong)input[index] << ((index % 8) * 8);
        }
    }

    private static void KeccakF1600(Span<ulong> state)
    {
        ReadOnlySpan<ulong> roundConstants =
        [
            0x0000000000000001, 0x0000000000008082, 0x800000000000808A,
            0x8000000080008000, 0x000000000000808B, 0x0000000080000001,
            0x8000000080008081, 0x8000000000008009, 0x000000000000008A,
            0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
            0x000000008000808B, 0x800000000000008B, 0x8000000000008089,
            0x8000000000008003, 0x8000000000008002, 0x8000000000000080,
            0x000000000000800A, 0x800000008000000A, 0x8000000080008081,
            0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
        ];
        ReadOnlySpan<int> rotations =
        [
             0,  1, 62, 28, 27,
            36, 44,  6, 55, 20,
             3, 10, 43, 25, 39,
            41, 45, 15, 21,  8,
            18,  2, 61, 56, 14,
        ];
        Span<ulong> columnParity = stackalloc ulong[5];
        Span<ulong> rotated = stackalloc ulong[25];

        foreach (var roundConstant in roundConstants)
        {
            for (var x = 0; x < 5; x++)
            {
                columnParity[x] =
                    state[x]
                    ^ state[x + 5]
                    ^ state[x + 10]
                    ^ state[x + 15]
                    ^ state[x + 20];
            }
            for (var x = 0; x < 5; x++)
            {
                var delta = columnParity[(x + 4) % 5] ^ RotateLeft(columnParity[(x + 1) % 5], 1);
                for (var y = 0; y < 5; y++)
                {
                    state[x + (5 * y)] ^= delta;
                }
            }

            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    rotated[y + (5 * ((2 * x + 3 * y) % 5))] =
                        RotateLeft(state[x + (5 * y)], rotations[x + (5 * y)]);
                }
            }
            for (var x = 0; x < 5; x++)
            {
                for (var y = 0; y < 5; y++)
                {
                    state[x + (5 * y)] =
                        rotated[x + (5 * y)]
                        ^ (~rotated[(x + 1) % 5 + (5 * y)]
                            & rotated[(x + 2) % 5 + (5 * y)]);
                }
            }
            state[0] ^= roundConstant;
        }
    }

    private static ulong RotateLeft(ulong value, int count) =>
        count == 0 ? value : (value << count) | (value >> (64 - count));
}
