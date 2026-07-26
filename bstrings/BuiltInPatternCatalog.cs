#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace bstrings;

internal sealed record BuiltInPatternDefinition(
    string Name,
    string Description,
    string Pattern,
    string Source,
    RegexOptions Options = RegexOptions.None,
    bool UseNonBacktracking = true,
    string? RapidsSupersetPattern = null,
    string? OutputGroup = null,
    int? BoundedRetryOverlap = null
);

internal static class BuiltInPatternCatalog
{
    private const string Base58 = "1-9A-HJ-NP-Za-km-z";

    internal static IReadOnlyList<BuiltInPatternDefinition> Definitions { get; } =
    [
        new(
            "guid",
            "Finds GUIDs",
            @"\b[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\b",
            "https://learn.microsoft.com/windows/win32/api/guiddef/ns-guiddef-guid",
            RapidsSupersetPattern: @"[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}"
        ),
        new(
            "usPhone",
            "Finds North American Numbering Plan phone-number candidates",
            @"(?<![0-9(])(?:\([2-9][0-9]{2}\)|[2-9][0-9]{2})[-. ]?[2-9][0-9]{2}[-. ]?[0-9]{4}(?![0-9])",
            "https://www.nationalnanpa.com/reports/reports_npa.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"\(?[2-9][0-9]{2}\)?[-. ]?[2-9][0-9]{2}[-. ]?[0-9]{4}",
            BoundedRetryOverlap: 32
        ),
        new(
            "unc",
            "Finds complete UNC path candidates",
            """(?:"\\\\[A-Za-z0-9%._-]+\\[A-Za-z0-9$%._ -]+(?:\\[^\\/:*?"<>|]+)*")|(?:\\\\[A-Za-z0-9%._-]+\\[A-Za-z0-9$%._-]+(?:\\[^\s\\/:*?"<>|]+)*)""",
            "https://learn.microsoft.com/windows/win32/fileio/naming-a-file",
            UseNonBacktracking: false,
            BoundedRetryOverlap: 32 * 1024
        ),
        new(
            "named_pipe",
            "Finds standalone, quoted, or whitespace-delimited Windows named-pipe path candidates",
            """(?:\A\\\\[^\\\x00"]+\\[Pp][Ii][Pp][Ee]\\(?>[^\\\x00"]+)\z|(?<=")(?<!\\)\\\\[^\\\x00"]+\\[Pp][Ii][Pp][Ee]\\(?>[^\\\x00"]+)(?!\\)(?=")|(?<!\\)\\\\[^\s\\\x00"]+\\[Pp][Ii][Pp][Ee]\\(?>[^\s\\\x00"]+)(?!\\))""",
            "https://learn.microsoft.com/windows/win32/ipc/pipe-names",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"\\\\[^\\]+\\[Pp][Ii][Pp][Ee]\\[^\\]+",
            BoundedRetryOverlap: 512
        ),
        new(
            "mac",
            "Finds 48-bit MAC address candidates",
            @"(?<![0-9A-Fa-f])(?<![0-9A-Fa-f]{2}[-:])[0-9A-Fa-f]{2}([-:]?)(?:[0-9A-Fa-f]{2}\1){4}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])(?![-:][0-9A-Fa-f]{2})",
            "https://standards.ieee.org/products-programs/regauth/",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[0-9A-Fa-f]{2}(?:[-:]?[0-9A-Fa-f]{2}){5}",
            BoundedRetryOverlap: 32
        ),
        new(
            "ssn",
            "Finds structurally possible US Social Security Numbers",
            @"\b(?!000|666|9[0-9]{2})[0-9]{3}([- ])(?!00)[0-9]{2}\1(?!0000)[0-9]{4}\b",
            "https://www.ssa.gov/history/ssn/geocard.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[0-9]{3}[- ][0-9]{2}[- ][0-9]{4}",
            BoundedRetryOverlap: 32
        ),
        new(
            "cc",
            "Finds payment-card number candidates; checksum validation is not performed",
            @"(?<![0-9])(?<![0-9][ -])(?:[0-9][ -]?){12,18}[0-9](?![ -]?[0-9])",
            "https://www.iso.org/standard/70484.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"(?:[0-9][ -]*){12,18}[0-9]",
            BoundedRetryOverlap: 64
        ),
        new(
            "ipv4",
            "Finds IPv4 address candidates",
            @"(?<![0-9.])(?:(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(?![0-9.])",
            "https://www.rfc-editor.org/rfc/rfc3986#section-3.2.2",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"(?:(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])",
            BoundedRetryOverlap: 32
        ),
        new(
            "ipv6",
            "Finds full and compressed IPv6 address candidates",
            @"(?<![0-9A-Fa-f:.])(?:(?:[0-9A-Fa-f]{1,4}:){7}[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,7}:|(?:[0-9A-Fa-f]{1,4}:){1,6}:[0-9A-Fa-f]{1,4}|(?:[0-9A-Fa-f]{1,4}:){1,5}(?::[0-9A-Fa-f]{1,4}){1,2}|(?:[0-9A-Fa-f]{1,4}:){1,4}(?::[0-9A-Fa-f]{1,4}){1,3}|(?:[0-9A-Fa-f]{1,4}:){1,3}(?::[0-9A-Fa-f]{1,4}){1,4}|(?:[0-9A-Fa-f]{1,4}:){1,2}(?::[0-9A-Fa-f]{1,4}){1,5}|[0-9A-Fa-f]{1,4}:(?:(?::[0-9A-Fa-f]{1,4}){1,6})|:(?:(?::[0-9A-Fa-f]{1,4}){1,7}|:)|(?:(?:[0-9A-Fa-f]{1,4}:){6}|::(?:[Ff]{4}(?::0{1,4})?:)?)(?:(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])|(?:[0-9A-Fa-f]{1,4}:){1,4}:(?:(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9]))(?![0-9A-Fa-f:.])",
            "https://www.rfc-editor.org/rfc/rfc4291#section-2.2",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[0-9A-Fa-f:.]{2,45}",
            BoundedRetryOverlap: 64
        ),
        new(
            "email",
            "Finds practical RFC-style email address candidates, including long TLDs",
            @"(?<![A-Za-z0-9!#$%&'*+/=?^_`{|}~.-])[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?![A-Za-z0-9.-])",
            "https://www.rfc-editor.org/rfc/rfc5322#section-3.4.1",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[A-Za-z0-9!#$%&'*+/=?^_`{|}~.-]+@[A-Za-z0-9.-]+\.[A-Za-z0-9-]+",
            BoundedRetryOverlap: 384
        ),
        new(
            "zip",
            "Finds US ZIP and ZIP+4 code candidates",
            @"\b[0-9]{5}(?:-[0-9]{4})?\b",
            "https://postalpro.usps.com/ZIP_Locale_Detail",
            RapidsSupersetPattern: @"[0-9]{5}(?:-[0-9]{4})?"
        ),
        new(
            "urlUser",
            "Finds username candidates embedded in hierarchical URL userinfo",
            @"(?:\A|[^A-Za-z0-9+\-.])[A-Za-z][A-Za-z0-9+\-.]*://(?<user>[A-Za-z0-9\-._~%!$&'()*+,;=]+)(?::[A-Za-z0-9\-._~%!$&'()*+,;=:]*)?@",
            "https://www.rfc-editor.org/rfc/rfc3986#section-3.2.1",
            OutputGroup: "user"
        ),
        new(
            "url3986",
            "Finds absolute hierarchical URI candidates using RFC 3986 syntax",
            @"(?:\A|[^A-Za-z0-9+\-.])
        (?<uri>
        [A-Za-z][A-Za-z0-9+\-.]*://                  # Scheme
        ([A-Za-z0-9\-._~%!$&'()*+,;=:]+@)?           # User information
        (?<host>[A-Za-z0-9\-._~%]+                   # Registered name
        |\[[A-Fa-f0-9:.]+\]                          # IPv6 candidate
        |\[v[A-Fa-f0-9][A-Za-z0-9\-._~%!$&'()*+,;=:]+\])
        (:[0-9]+)?                                   # Port
        (/[A-Za-z0-9\-._~%!$&'()*+,;=:@]*)*           # Path
        (\?[A-Za-z0-9\-._~%!$&'()*+,;=:@/?]*)?       # Query
        (\#[A-Za-z0-9\-._~%!$&'()*+,;=:@/?]*)?       # Fragment
        )
        ",
            "https://www.rfc-editor.org/rfc/rfc3986",
            RegexOptions.IgnorePatternWhitespace,
            OutputGroup: "uri"
        ),
        new(
            "xml",
            "Finds a complete simple case-sensitive XML element candidate",
            @"\A<([A-Za-z][A-Za-z0-9]*)\b[^>]*>(.*?)</\1>\z",
            "https://www.w3.org/TR/xml/#sec-starttags",
            UseNonBacktracking: false
        ),
        new(
            "sid",
            "Finds Microsoft Security Identifier strings",
            @"\bS-[0-9]+-(?:[0-9]+|0[xX][0-9A-Fa-f]+)(?:-[0-9]+){0,15}\b(?!-[0-9])",
            "https://learn.microsoft.com/windows-server/identity/ad-ds/manage/understand-security-identifiers",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"S-[0-9]+-(?:[0-9]+|0[xX][0-9A-Fa-f]+)(?:-[0-9]+){0,15}",
            BoundedRetryOverlap: 256
        ),
        new(
            "win_path",
            @"Finds Windows-style paths such as C:\folder\file.txt",
            """(?:"(?:[A-Za-z]:|\\\\[^\\/:*?"<>|]+\\[^\\/:*?"<>|]+)\\(?:[^\\/:*?"<>|]+\\)*[^\\/:*?"<>|]+")|(?:(?:[A-Za-z]:|\\\\[^\s\\/:*?"<>|]+\\[^\s\\/:*?"<>|]+)\\(?:[^\s\\/:*?"<>|]+\\)*[^\s\\/:*?"<>|]+)""",
            "https://learn.microsoft.com/windows/win32/fileio/naming-a-file"
        ),
        new(
            "var_set",
            "Finds environment-variable assignments",
            @"^[A-Za-z_][A-Za-z_0-9()]*=[^\x00\r\n]*$",
            "https://learn.microsoft.com/windows/win32/procthread/environment-variables",
            RapidsSupersetPattern: @"[A-Za-z_][A-Za-z_0-9()]*=.*"
        ),
        new(
            "reg_path",
            "Finds Windows Registry hive path candidates",
            @"\b(?:(?:HKEY_LOCAL_MACHINE|HKLM|HKEY_CURRENT_USER|HKCU|HKEY_CLASSES_ROOT|HKCR|HKEY_USERS|HKU|HKEY_CURRENT_CONFIG|HKCC)\\)?(?:SAM|SECURITY|SOFTWARE|SYSTEM)(?:\\[A-Za-z0-9_. (){}-]+)*\b",
            "https://learn.microsoft.com/windows/win32/sysinfo/registry-hives",
            Options: RegexOptions.IgnoreCase
        ),
        new(
            "b64",
            "Finds base64 candidates of at least eight encoded characters; very short unpadded values are excluded",
            @"(?<![A-Za-z0-9+/])(?:(?:[A-Za-z0-9+/]{4}){2,}|(?:[A-Za-z0-9+/]{4})+(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=))(?![A-Za-z0-9+/=])",
            "https://www.rfc-editor.org/rfc/rfc4648#section-4",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"(?:[A-Za-z0-9+/]{4}){1,}(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?"
        ),
        new(
            "bitlocker",
            "Finds BitLocker 48-digit recovery-password candidates",
            @"(?<![0-9])(?<![0-9]{6}-)[0-9]{6}(?:-[0-9]{6}){7}(?!-[0-9]{6})(?![0-9])",
            "https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[0-9]{6}(?:-[0-9]{6}){7}",
            BoundedRetryOverlap: 64
        ),
        new(
            "bitcoin",
            "Finds legacy Bitcoin address candidates; checksum validation is not performed",
            $@"\b[13][{Base58}]{{25,34}}\b",
            "https://en.bitcoin.it/wiki/Base58Check_encoding",
            RapidsSupersetPattern: $@"[13][{Base58}]{{25,34}}"
        ),
        new(
            "aeon",
            "Finds legacy Aeon wallet-address candidates",
            $@"(?<![{Base58}])Wm[st][{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/aeonix/aeon",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"Wm[st][{Base58}]{{94}}",
            BoundedRetryOverlap: 128
        ),
        new(
            "bytecoin",
            "Finds legacy Bytecoin wallet-address candidates",
            $@"(?<![{Base58}])2[1-9A-HJ-NP-Za-km-z][{Base58}]{{93}}(?![{Base58}])",
            "https://github.com/bcndev/bytecoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"2[1-9A-HJ-NP-Za-km-z][{Base58}]{{93}}",
            BoundedRetryOverlap: 128
        ),
        new(
            "dashcoin",
            "Finds legacy Dashcoin CryptoNote address candidates",
            $@"(?<![{Base58}])D[{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/dashcoin/dashcoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"D[{Base58}]{{94}}",
            BoundedRetryOverlap: 128
        ),
        new(
            "dashcoin2",
            "Finds Dash address candidates beginning with 7 or X",
            $@"(?<![{Base58}])[7X][{Base58}]{{33}}(?![{Base58}])",
            "https://docs.dash.org/en/stable/docs/user/introduction/features.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"[7X][{Base58}]{{33}}",
            BoundedRetryOverlap: 64
        ),
        new(
            "fantomcoin",
            "Finds legacy Fantomcoin wallet-address candidates",
            $@"(?<![{Base58}])6[{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/amjuarez/fantomcoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"6[{Base58}]{{94}}",
            BoundedRetryOverlap: 128
        ),
        new(
            "monero",
            "Finds Monero address candidates; checksum validation is not performed",
            $@"(?<![{Base58}])(?:4[{Base58}]{{105}}|[48][{Base58}]{{94}})(?![{Base58}])",
            "https://docs.getmonero.org/public-address/",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"(?:4[{Base58}]{{105}}|[48][{Base58}]{{94}})",
            BoundedRetryOverlap: 128
        ),
        new(
            "sumokoin",
            "Finds SumoKoin wallet-address candidates",
            $@"(?<![{Base58}])Sumoo[{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/sumoprojects/sumokoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"Sumoo[{Base58}]{{94}}",
            BoundedRetryOverlap: 128
        ),
        new(
            "cve",
            "Finds CVE identifier candidates with 4-to-19-digit sequence numbers",
            @"(?<![A-Za-z0-9])[Cc][Vv][Ee]-[0-9]{4}-[0-9]{4,19}(?![A-Za-z0-9])",
            "https://github.com/CVEProject/cve-schema/blob/main/schema/docs/CVE_Record_Format_bundled.json",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[Cc][Vv][Ee]-[0-9]{4}-[0-9]{4,19}",
            BoundedRetryOverlap: 64
        ),
        new(
            "pem_private_key",
            "Finds RFC 7468 PKCS#8 BEGIN private-key boundaries",
            @"(?<!-)-----BEGIN (?:ENCRYPTED )?PRIVATE KEY-----(?!-)",
            "https://www.rfc-editor.org/rfc/rfc7468",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"-----BEGIN (?:ENCRYPTED )?PRIVATE KEY-----",
            BoundedRetryOverlap: 64
        ),
        new(
            "onion_v3",
            "Finds Tor v3 onion-service hostname candidates",
            @"(?<![A-Za-z0-9-])[A-Za-z2-7]{56}\.[Oo][Nn][Ii][Oo][Nn](?![A-Za-z0-9.-])",
            "https://spec.torproject.org/rend-spec/encoding-onion-addresses.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[A-Za-z2-7]{56}\.[Oo][Nn][Ii][Oo][Nn]",
            BoundedRetryOverlap: 128
        ),
        new(
            "ethereum",
            "Finds 20-byte hexadecimal Ethereum address candidates",
            @"(?<![A-Za-z0-9])0x[0-9A-Fa-f]{40}(?![A-Za-z0-9])",
            "https://ethereum.org/developers/docs/accounts/",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"0x[0-9A-Fa-f]{40}",
            BoundedRetryOverlap: 64
        ),
        new(
            "sha256",
            "Finds 64-hex-character SHA-256-shaped digest candidates",
            @"(?<![A-Za-z0-9])[0-9A-Fa-f]{64}(?![A-Za-z0-9])",
            "https://csrc.nist.gov/pubs/fips/180-4/upd1/final",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[0-9A-Fa-f]{64}",
            BoundedRetryOverlap: 128
        ),
    ];

    internal static IReadOnlyDictionary<string, BuiltInPatternDefinition> ByName { get; } =
        new ReadOnlyDictionary<string, BuiltInPatternDefinition>(
            Definitions.ToDictionary(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
        );

    internal static IReadOnlyDictionary<string, string> Descriptions { get; } =
        new ReadOnlyDictionary<string, string>(
            Definitions.ToDictionary(
                definition => definition.Name,
                definition => definition.Description,
                StringComparer.OrdinalIgnoreCase
            )
        );

    internal static IReadOnlyDictionary<string, string> Patterns { get; } =
        new ReadOnlyDictionary<string, string>(
            Definitions.ToDictionary(
                definition => definition.Name,
                definition => definition.Pattern,
                StringComparer.OrdinalIgnoreCase
            )
        );

    internal static bool TryGetDefinition(
        string name,
        string pattern,
        out BuiltInPatternDefinition definition
    )
    {
        if (
            ByName.TryGetValue(name, out var candidate)
            && string.Equals(candidate.Pattern, pattern, StringComparison.Ordinal)
        )
        {
            definition = candidate;
            return true;
        }

        definition = null!;
        return false;
    }
}
