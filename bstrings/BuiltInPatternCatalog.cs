#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace bstrings;

internal enum BuiltInValidationKind
{
    None,
    Email,
    PaymentCard,
    Base64,
    BitLocker,
    BitcoinBase58Check,
    BitcoinSegwitAddress,
    NetworkBase58Address,
    NetworkBech32Address,
    SolanaAddress,
    StellarAddress,
    BitcoinCashAddress,
    TonAddress,
    SuiAddress,
    NearAccount,
    BittensorAddress,
    HederaAddress,
    CantonParty,
    CryptoNoteAddress,
    DashBase58Check,
    OnionV3,
    EthereumAddress,
    SecurityIdentifier,
    XmlElement,
    UriUserInfo,
    AbsoluteUri,
    Jwt,
    Iban,
    CanadianSin,
    DateOfBirth,
    Cpe23,
    EmailMessageId,
    Lei,
    Npi,
    Itin,
    UkNino,
}

internal sealed record BuiltInPatternDefinition(
    string Name,
    string Description,
    string Pattern,
    string Source,
    RegexOptions Options = RegexOptions.None,
    bool UseNonBacktracking = true,
    string? RapidsSupersetPattern = null,
    string? OutputGroup = null,
    int? BoundedRetryOverlap = null,
    int? GeneratedShortInputLimit = null,
    BuiltInValidationKind Validation = BuiltInValidationKind.None
);

internal static class BuiltInPatternCatalog
{
    private const string Base58 = "1-9A-HJ-NP-Za-km-z";
    internal const int Url3986GeneratedInputLimit = 2048;
    internal const string Url3986Pattern = """
        (?:\A|[^A-Za-z0-9+\-.])
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
        """;

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
            "Finds payment-card numbers with a valid Luhn checksum",
            @"(?<![0-9])(?<![0-9][ -])(?:[0-9][ -]?){12,18}[0-9](?![ -]?[0-9])",
            "https://www.iso.org/standard/70484.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"(?:[0-9][ -]*){12,18}[0-9]",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.PaymentCard
        ),
        new(
            "ipv4",
            "Finds IPv4 address candidates",
            @"(?<![0-9.])(?:(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9]{2}|[1-9]?[0-9])(?![0-9]|\.[0-9])",
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
            "Finds practical RFC-style email address candidates, including long TLDs, within SMTP length limits",
            @"(?<![A-Za-z0-9!#$%&'*+/=?^_`{|}~.-])[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[A-Za-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?(?![A-Za-z0-9.-])",
            "https://www.rfc-editor.org/rfc/rfc5322#section-3.4.1",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[A-Za-z0-9!#$%&'*+/=?^_`{|}~.-]+@[A-Za-z0-9.-]+\.[A-Za-z0-9-]+",
            BoundedRetryOverlap: 384,
            Validation: BuiltInValidationKind.Email
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
            "Finds username candidates embedded in hierarchical URL userinfo with valid percent encoding",
            @"(?:\A|[^A-Za-z0-9+\-.])[A-Za-z][A-Za-z0-9+\-.]*://(?<user>(?:[A-Za-z0-9\-._~!$&'()*+,;=]|%[A-Fa-f0-9]{2})+)(?::(?:[A-Za-z0-9\-._~!$&'()*+,;=:]|%[A-Fa-f0-9]{2})*)?@",
            "https://www.rfc-editor.org/rfc/rfc3986#section-3.2.1",
            OutputGroup: "user",
            Validation: BuiltInValidationKind.UriUserInfo
        ),
        new(
            "url3986",
            "Finds absolute hierarchical URI candidates with valid RFC 3986 percent encoding and IP literals",
            Url3986Pattern,
            "https://www.rfc-editor.org/rfc/rfc3986",
            RegexOptions.IgnorePatternWhitespace,
            OutputGroup: "uri",
            GeneratedShortInputLimit: Url3986GeneratedInputLimit,
            Validation: BuiltInValidationKind.AbsoluteUri
        ),
        new(
            "xml",
            "Finds a complete, well-formed simple case-sensitive XML element",
            @"\A<([A-Za-z][A-Za-z0-9]*)\b[^>]*>(.*?)</\1>\z",
            "https://www.w3.org/TR/xml/#sec-starttags",
            UseNonBacktracking: false,
            Validation: BuiltInValidationKind.XmlElement
        ),
        new(
            "sid",
            "Finds structurally valid Microsoft Security Identifier strings",
            @"\bS-[0-9]+-(?:[0-9]+|0[xX][0-9A-Fa-f]+)(?:-[0-9]+){0,15}\b(?!-[0-9])",
            "https://learn.microsoft.com/windows-server/identity/ad-ds/manage/understand-security-identifiers",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"S-[0-9]+-(?:[0-9]+|0[xX][0-9A-Fa-f]+)(?:-[0-9]+){0,15}",
            BoundedRetryOverlap: 256,
            Validation: BuiltInValidationKind.SecurityIdentifier
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
            "intlPhone",
            "Finds international E.164-style phone-number candidates with 8 to 15 digits",
            @"(?<![0-9+])\+[1-9][0-9](?:[ .()/-]?[0-9]){6,13}(?![0-9])",
            "https://www.itu.int/rec/T-REC-E.164",
            UseNonBacktracking: false,
            BoundedRetryOverlap: 64
        ),
        new(
            "canadian_sin",
            "Finds labelled Canadian Social Insurance Numbers that pass the Luhn checksum",
            @"(?<![A-Za-z0-9])[Ss][Ii][Nn]\s*[:=]?\s*(?<sin>(?:[0-9][ -]?){8}[0-9])(?![ -]?[0-9])",
            "https://www.canada.ca/en/employment-social-development/services/sin/reports/code-of-practice.html",
            UseNonBacktracking: false,
            OutputGroup: "sin",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.CanadianSin
        ),
        new(
            "dob",
            "Finds labelled date-of-birth values with calendar validation",
            @"(?<![A-Za-z0-9])(?:[Dd][Oo][Bb]|[Dd]ate[ _-]?[Oo]f[ _-]?[Bb]irth|[Bb]irth[ _-]?[Dd]ate)\s*[:=]\s*(?<dob>(?:(?:19|20)[0-9]{2}[-/](?:0?[1-9]|1[0-2])[-/](?:0?[1-9]|[12][0-9]|3[01])|(?:0?[1-9]|1[0-2])[-/](?:0?[1-9]|[12][0-9]|3[01])[-/](?:19|20)[0-9]{2}))(?![0-9])",
            "https://pages.nist.gov/800-63-4/sp800-63a.html",
            UseNonBacktracking: false,
            OutputGroup: "dob",
            BoundedRetryOverlap: 96,
            Validation: BuiltInValidationKind.DateOfBirth
        ),
        new(
            "iban",
            "Finds IBAN candidates that pass the ISO 13616 MOD-97 check",
            @"(?<![A-Za-z0-9])(?<iban>(?:[A-Za-z]{2}[0-9]{2}[A-Za-z0-9]{11,30}|[A-Za-z]{2}[0-9]{2}(?: [A-Za-z0-9]{4}){2,7}(?: [A-Za-z0-9]{1,4})?))(?![A-Za-z0-9])",
            "https://www.iso.org/standard/81090.html",
            UseNonBacktracking: false,
            OutputGroup: "iban",
            BoundedRetryOverlap: 96,
            Validation: BuiltInValidationKind.Iban
        ),
        new(
            "vin",
            "Finds 17-character vehicle identification number candidates excluding I, O, and Q",
            @"(?<![A-Za-z0-9])[A-HJ-NPR-Za-hj-npr-z0-9]{17}(?![A-Za-z0-9])",
            "https://www.nhtsa.gov/sites/nhtsa.gov/files/documents/vin_final_rule_april_2008.pdf",
            UseNonBacktracking: false,
            BoundedRetryOverlap: 32
        ),
        new(
            "jwt",
            "Finds structurally valid compact JWT candidates; signatures and decryption are not verified",
            @"(?<![A-Za-z0-9_-])(?:[A-Za-z0-9_-]{2,2048}\.[A-Za-z0-9_-]{2,16384}\.[A-Za-z0-9_-]{0,8192}|[A-Za-z0-9_-]{2,2048}\.[A-Za-z0-9_-]{0,8192}\.[A-Za-z0-9_-]{2,8192}\.[A-Za-z0-9_-]{2,32768}\.[A-Za-z0-9_-]{2,8192})(?![A-Za-z0-9_.-])",
            "https://www.rfc-editor.org/rfc/rfc7519#section-7.2",
            UseNonBacktracking: false,
            BoundedRetryOverlap: 72 * 1024,
            Validation: BuiltInValidationKind.Jwt
        ),
        new(
            "credential_assignment",
            "Finds plaintext credential-like assignment candidates; surrounding context is required for interpretation",
            """(?<![A-Za-z0-9_])(?:[Pp]assword|[Pp]asswd|[Pp]wd|[Aa][Pp][Ii][_-]?[Kk]ey|[Ss]ecret|[Aa]ccess[_-]?[Tt]oken|[Rr]efresh[_-]?[Tt]oken)\s*[:=]\s*["']?(?<credential>[^\s"',;}{]{4,2048})""",
            "https://owasp.org/www-project-web-security-testing-guide/latest/4-Web_Application_Security_Testing/09-Testing_for_Weak_Cryptography/04-Testing_for_Weak_Encryption",
            UseNonBacktracking: false,
            OutputGroup: "credential",
            BoundedRetryOverlap: 4096
        ),
        new(
            "browser_credential_field",
            "Finds browser credential-store field names; this indicates an artifact schema, not a decrypted password",
            @"(?<![A-Za-z0-9_])(?:origin_url|action_url|signon_realm|username_value|password_value|encryptedUsername|encryptedPassword|httpRealm|formSubmitURL)(?![A-Za-z0-9_])",
            "https://source.chromium.org/chromium/chromium/src/+/main:components/password_manager/core/browser/login_database.cc",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            BoundedRetryOverlap: 64
        ),
        new(
            "browser_profile_path",
            "Finds Chromium and Firefox credential/profile artifact path candidates",
            """(?<![A-Za-z0-9])(?<browser_path>[A-Za-z]:\\Users\\[^\\\x00\r\n]{1,128}\\AppData\\(?:Local\\(?:Google\\Chrome|Microsoft\\Edge|BraveSoftware\\Brave-Browser|Chromium)\\User Data\\(?:Default|Profile [0-9]+)\\(?:Login Data|Local State|History|Cookies|Web Data)|Roaming\\Mozilla\\Firefox\\Profiles\\[^\\\x00\r\n]{1,128}\\(?:logins\.json|key4\.db|places\.sqlite|cookies\.sqlite)))(?![A-Za-z0-9])""",
            "https://source.chromium.org/chromium/chromium/src/+/main:docs/user_data_dir.md",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            OutputGroup: "browser_path",
            BoundedRetryOverlap: 1024
        ),
        new(
            "reg_persistence",
            "Finds Windows Registry persistence key path candidates for Run, Winlogon, and services",
            @"(?<![A-Za-z0-9_])(?:(?:HKEY_LOCAL_MACHINE|HKLM|HKEY_CURRENT_USER|HKCU)\\)?(?:(?:SOFTWARE\\Microsoft\\Windows(?: NT)?\\CurrentVersion\\(?:Run|RunOnce|Winlogon))|(?:SYSTEM\\CurrentControlSet\\Services))(?:\\[^\\\x00\r\n\t=;|]{0,127}[^\s\\\x00=;|]){0,8}(?![A-Za-z0-9_])",
            "https://learn.microsoft.com/windows/win32/setupapi/run-and-runonce-registry-keys",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            BoundedRetryOverlap: 1024
        ),
        new(
            "reg_user_activity",
            "Finds high-value Windows Registry user-activity key path candidates",
            @"(?<![A-Za-z0-9_])(?:(?:HKEY_CURRENT_USER|HKCU)\\)?Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\(?:UserAssist|RecentDocs|TypedPaths|RunMRU|ComDlg32|WordWheelQuery|MountPoints2|BagMRU|Bags)(?:\\[^\\\x00\r\n\t=;|]{0,127}[^\s\\\x00=;|]){0,8}(?![A-Za-z0-9_])",
            "https://learn.microsoft.com/windows/win32/shell/known-folders",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            BoundedRetryOverlap: 1024
        ),
        new(
            "reg_usb",
            "Finds Windows Registry USBSTOR and mounted-device key path candidates",
            @"(?<![A-Za-z0-9_])(?:(?:HKEY_LOCAL_MACHINE|HKLM)\\)?SYSTEM\\(?:CurrentControlSet\\Enum\\USBSTOR|MountedDevices)(?:\\[^\\\x00\r\n\t=;|]{0,127}[^\s\\\x00=;|]){0,8}(?![A-Za-z0-9_])",
            "https://learn.microsoft.com/windows-hardware/drivers/install/identifiers-for-usb-devices",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            BoundedRetryOverlap: 1024
        ),
        new(
            "reg_execution",
            "Finds Windows Registry execution-evidence key path candidates for AppCompatCache, BAM/DAM, and Amcache",
            @"(?<![A-Za-z0-9_])(?:(?:HKEY_LOCAL_MACHINE|HKLM)\\)?(?:(?:SYSTEM\\CurrentControlSet\\Control\\Session Manager\\AppCompatCache)|(?:SYSTEM\\CurrentControlSet\\Services\\[BbDd]am\\State\\UserSettings)|(?:SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\AppCompatFlags)|(?:Amcache\\Root))(?:\\[^\\\x00\r\n\t=;|]{0,127}[^\s\\\x00=;|]){0,8}(?![A-Za-z0-9_])",
            "https://learn.microsoft.com/windows/win32/devnotes/application-compatibility-database",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            BoundedRetryOverlap: 1024
        ),
        new(
            "reg_network",
            "Finds Windows Registry network-profile and interface key path candidates",
            @"(?<![A-Za-z0-9_])(?:(?:HKEY_LOCAL_MACHINE|HKLM)\\)?SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\NetworkList\\(?:Profiles|Signatures)(?:\\[^\\\x00\r\n\t=;|]{0,127}[^\s\\\x00=;|]){0,8}(?![A-Za-z0-9_])",
            "https://learn.microsoft.com/windows/win32/api/netlistmgr/",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            BoundedRetryOverlap: 1024
        ),
        new(
            "reg_system_identity",
            "Finds Windows Registry host, timezone, and user-profile identity key path candidates",
            @"(?<![A-Za-z0-9_])(?:(?:HKEY_LOCAL_MACHINE|HKLM)\\)?(?:SYSTEM\\CurrentControlSet\\Control\\(?:ComputerName|TimeZoneInformation)|SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ProfileList)(?:\\[^\\\x00\r\n\t=;|]{0,127}[^\s\\\x00=;|]){0,8}(?![A-Za-z0-9_])",
            "https://learn.microsoft.com/windows/win32/sysinfo/registry-hives",
            Options: RegexOptions.IgnoreCase,
            UseNonBacktracking: false,
            BoundedRetryOverlap: 1024
        ),
        new(
            "b64",
            "Finds canonical base64 candidates of at least eight encoded characters; very short unpadded values are excluded",
            @"(?<![A-Za-z0-9+/])(?:(?:[A-Za-z0-9+/]{4}){2,}|(?:[A-Za-z0-9+/]{4})+(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=))(?![A-Za-z0-9+/=])",
            "https://www.rfc-editor.org/rfc/rfc4648#section-4",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"(?:[A-Za-z0-9+/]{4}){1,}(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?",
            Validation: BuiltInValidationKind.Base64
        ),
        new(
            "bitlocker",
            "Finds BitLocker 48-digit recovery passwords whose eight blocks pass Microsoft's arithmetic checks",
            @"(?<![0-9])(?<![0-9]{6}-)[0-9]{6}(?:-[0-9]{6}){7}(?!-[0-9]{6})(?![0-9])",
            "https://learn.microsoft.com/windows/security/operating-system-security/data-protection/bitlocker/recovery-overview",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[0-9]{6}(?:-[0-9]{6}){7}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.BitLocker
        ),
        new(
            "bitcoin",
            "Finds legacy mainnet Bitcoin P2PKH and P2SH addresses with valid Base58Check checksums",
            $@"\b[13][{Base58}]{{25,34}}\b",
            "https://en.bitcoin.it/wiki/Base58Check_encoding",
            RapidsSupersetPattern: $@"[13][{Base58}]{{25,34}}",
            Validation: BuiltInValidationKind.BitcoinBase58Check
        ),
        new(
            "bitcoin_segwit",
            "Finds Bitcoin mainnet SegWit and Taproot addresses with valid Bech32 or Bech32m checksums",
            @"(?<![A-Za-z0-9])(?:bc1[ac-hj-np-z02-9]{11,71}|BC1[AC-HJ-NP-Z02-9]{11,71})(?![A-Za-z0-9])",
            "https://bips.dev/350/",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"(?:bc1[ac-hj-np-z02-9]{11,71}|BC1[AC-HJ-NP-Z02-9]{11,71})",
            BoundedRetryOverlap: 96,
            Validation: BuiltInValidationKind.BitcoinSegwitAddress
        ),
        new(
            "tron",
            "Finds TRON account addresses with a valid network byte and Base58Check checksum",
            $@"(?<![{Base58}])T[{Base58}]{{33}}(?![{Base58}])",
            "https://developers.tron.network/docs/account",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"T[{Base58}]{{33}}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.NetworkBase58Address
        ),
        new(
            "solana",
            "Finds canonical 32-byte Solana addresses encoded in Base58",
            $@"(?<![{Base58}])[{Base58}]{{32,44}}(?![{Base58}])",
            "https://solana.com/docs/core/accounts",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"[{Base58}]{{32,44}}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.SolanaAddress
        ),
        new(
            "xrp",
            "Finds XRPL classic account addresses with a valid type byte and checksum",
            $@"(?<![{Base58}])r[{Base58}]{{24,34}}(?![{Base58}])",
            "https://xrpl.org/docs/references/protocol/data-types/base58-encodings",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"r[{Base58}]{{24,34}}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.NetworkBase58Address
        ),
        new(
            "dogecoin",
            "Finds Dogecoin mainnet P2PKH and P2SH addresses with valid Base58Check checksums",
            $@"(?<![{Base58}])[DA9][{Base58}]{{25,33}}(?![{Base58}])",
            "https://github.com/dogecoin/dogecoin/blob/master/src/kernel/chainparams.cpp",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"[DA9][{Base58}]{{25,33}}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.NetworkBase58Address
        ),
        new(
            "zcash",
            "Finds Zcash transparent and Sapling mainnet addresses with valid Base58Check or Bech32 checksums",
            $@"(?<![A-Za-z0-9])(?:t[13][{Base58}]{{33}}|zs1[ac-hj-np-z02-9]{{75}})(?![A-Za-z0-9])",
            "https://zips.z.cash/protocol/protocol.pdf",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"(?:t[13][{Base58}]{{33}}|zs1[ac-hj-np-z02-9]{{75}})",
            BoundedRetryOverlap: 96,
            Validation: BuiltInValidationKind.NetworkBech32Address
        ),
        new(
            "cardano",
            "Finds canonical Cardano mainnet Shelley payment addresses with valid Bech32 checksums and payload structure",
            @"(?<![A-Za-z0-9])addr1[ac-hj-np-z02-9]{53,120}(?![A-Za-z0-9])",
            "https://cips.cardano.org/cip/CIP-19",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"addr1[ac-hj-np-z02-9]{53,120}",
            BoundedRetryOverlap: 160,
            Validation: BuiltInValidationKind.NetworkBech32Address
        ),
        new(
            "stellar",
            "Finds Stellar Ed25519 account addresses with a valid StrKey version and CRC16-XModem checksum",
            @"(?<![A-Z2-7])G[A-Z2-7]{55}(?![A-Z2-7])",
            "https://developers.stellar.org/docs/build/guides/conversions/address-conversions",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"G[A-Z2-7]{55}",
            BoundedRetryOverlap: 80,
            Validation: BuiltInValidationKind.StellarAddress
        ),
        new(
            "bitcoin_cash",
            "Finds explicit-prefix Bitcoin Cash P2PKH and P2SH CashAddr addresses with valid checksums",
            @"(?<![A-Za-z0-9])bitcoincash:[qp][ac-hj-np-z02-9]{41}(?![A-Za-z0-9])",
            "https://documentation.cash/protocol/blockchain/encoding/cashaddr.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"bitcoincash:[qp][ac-hj-np-z02-9]{41}",
            BoundedRetryOverlap: 80,
            Validation: BuiltInValidationKind.BitcoinCashAddress
        ),
        new(
            "ton",
            "Finds TON mainnet user-friendly addresses with a valid tag, workchain, and CRC16 checksum",
            @"(?<![A-Za-z0-9_-])[EU][A-Za-z0-9_-]{47}(?![A-Za-z0-9_-])",
            "https://docs.ton.org/foundations/addresses/formats",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[EU][A-Za-z0-9_-]{47}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.TonAddress
        ),
        new(
            "litecoin",
            "Finds Litecoin mainnet legacy and SegWit addresses with valid Base58Check, Bech32, or Bech32m checksums",
            $@"(?<![A-Za-z0-9])(?:[LM][{Base58}]{{25,33}}|ltc1[ac-hj-np-z02-9]{{11,71}}|LTC1[AC-HJ-NP-Z02-9]{{11,71}})(?![A-Za-z0-9])",
            "https://github.com/litecoin-project/litecoin/blob/master/src/kernel/chainparams.cpp",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"(?:[LM][{Base58}]{{25,33}}|ltc1[ac-hj-np-z02-9]{{11,71}}|LTC1[AC-HJ-NP-Z02-9]{{11,71}})",
            BoundedRetryOverlap: 96,
            Validation: BuiltInValidationKind.NetworkBech32Address
        ),
        new(
            "avalanche",
            "Finds Avalanche X-Chain and P-Chain mainnet addresses with valid Bech32 checksums",
            @"(?<![A-Za-z0-9-])[XP]-avax1[ac-hj-np-z02-9]{38}(?![A-Za-z0-9])",
            "https://build.avax.network/docs/rpcs/other/standards/cryptographic-primitives",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[XP]-avax1[ac-hj-np-z02-9]{38}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.NetworkBech32Address
        ),
        new(
            "move_address",
            "Finds canonical full-length 32-byte Move-family addresses used by Sui and Aptos",
            @"(?<![A-Za-z0-9])0x[0-9a-f]{64}(?![A-Za-z0-9])",
            "https://github.com/MystenLabs/sui/blob/main/crates/sui-types/src/base_types.rs",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"0x[0-9a-f]{64}",
            BoundedRetryOverlap: 80,
            Validation: BuiltInValidationKind.SuiAddress
        ),
        new(
            "near",
            "Finds canonical named NEAR mainnet accounts ending in .near",
            @"(?<![a-z0-9._-])[a-z0-9](?:[a-z0-9._-]{0,57}[a-z0-9])?\.near(?![a-z0-9._-])",
            "https://docs.near.org/protocol/account-id",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[a-z0-9][a-z0-9._-]{0,58}\.near",
            BoundedRetryOverlap: 80,
            Validation: BuiltInValidationKind.NearAccount
        ),
        new(
            "bittensor",
            "Finds Bittensor SS58 account addresses with the Substrate prefix and Blake2b checksum",
            $@"(?<![{Base58}])5[{Base58}]{{47}}(?![{Base58}])",
            "https://github.com/paritytech/polkadot-sdk/blob/master/substrate/primitives/core/src/crypto.rs",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"5[{Base58}]{{47}}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.BittensorAddress
        ),
        new(
            "hedera",
            "Finds mainnet Hedera entity addresses carrying a valid HIP-15 checksum",
            @"(?<![0-9.])(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-[a-z]{5}(?![A-Za-z0-9.-])",
            "https://hips.hedera.com/hip/hip-15",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[0-9]+\.[0-9]+\.[0-9]+-[a-z]{5}",
            BoundedRetryOverlap: 96,
            Validation: BuiltInValidationKind.HederaAddress
        ),
        new(
            "canton_party",
            "Finds canonical Canton party identifiers with SHA-256 multihash namespace fingerprints",
            @"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{1,185}::1220[0-9a-f]{64}(?![A-Za-z0-9])",
            "https://docs.digitalasset.com/build/3.4/explanations/parties-users.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[A-Za-z0-9_-]{1,185}::1220[0-9a-f]{64}",
            BoundedRetryOverlap: 280,
            Validation: BuiltInValidationKind.CantonParty
        ),
        new(
            "provenance_scope",
            "Finds Provenance scope identifiers with a valid Bech32 checksum and scope type byte",
            @"(?<![A-Za-z0-9])scope1[ac-hj-np-z02-9]{34}(?![A-Za-z0-9])",
            "https://docs.provenance.io/build/sdk/metadata-module",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"scope1[ac-hj-np-z02-9]{34}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.NetworkBech32Address
        ),
        new(
            "aeon",
            "Finds legacy Aeon standard addresses with valid network prefix and CryptoNote checksum",
            $@"(?<![{Base58}])Wm[st][{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/aeonix/aeon",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"Wm[st][{Base58}]{{94}}",
            BoundedRetryOverlap: 128,
            Validation: BuiltInValidationKind.CryptoNoteAddress
        ),
        new(
            "bytecoin",
            "Finds legacy Bytecoin standard addresses with valid network prefix and CryptoNote checksum",
            $@"(?<![{Base58}])2[1-9A-HJ-NP-Za-km-z][{Base58}]{{93}}(?![{Base58}])",
            "https://github.com/bcndev/bytecoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"2[1-9A-HJ-NP-Za-km-z][{Base58}]{{93}}",
            BoundedRetryOverlap: 128,
            Validation: BuiltInValidationKind.CryptoNoteAddress
        ),
        new(
            "dashcoin",
            "Finds legacy Dashcoin CryptoNote standard addresses with valid network prefix and checksum",
            $@"(?<![{Base58}])D[{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/dashcoin/dashcoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"D[{Base58}]{{94}}",
            BoundedRetryOverlap: 128,
            Validation: BuiltInValidationKind.CryptoNoteAddress
        ),
        new(
            "dashcoin2",
            "Finds Dash mainnet P2PKH and P2SH addresses with valid Base58Check checksums",
            $@"(?<![{Base58}])[7X][{Base58}]{{33}}(?![{Base58}])",
            "https://docs.dash.org/en/stable/docs/user/introduction/features.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"[7X][{Base58}]{{33}}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.DashBase58Check
        ),
        new(
            "fantomcoin",
            "Finds legacy Fantomcoin standard addresses with valid network prefix and CryptoNote checksum",
            $@"(?<![{Base58}])6[{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/xdn-project/fantomcoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"6[{Base58}]{{94}}",
            BoundedRetryOverlap: 128,
            Validation: BuiltInValidationKind.CryptoNoteAddress
        ),
        new(
            "monero",
            "Finds Monero mainnet standard, integrated, and subaddresses with valid network prefixes and checksums",
            $@"(?<![{Base58}])(?:4[{Base58}]{{105}}|[48][{Base58}]{{94}})(?![{Base58}])",
            "https://docs.getmonero.org/public-address/",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"(?:4[{Base58}]{{105}}|[48][{Base58}]{{94}})",
            BoundedRetryOverlap: 128,
            Validation: BuiltInValidationKind.CryptoNoteAddress
        ),
        new(
            "sumokoin",
            "Finds SumoKoin standard addresses with a valid network prefix and CryptoNote checksum",
            $@"(?<![{Base58}])Sumoo[{Base58}]{{94}}(?![{Base58}])",
            "https://github.com/sumoprojects/sumokoin",
            UseNonBacktracking: false,
            RapidsSupersetPattern: $@"Sumoo[{Base58}]{{94}}",
            BoundedRetryOverlap: 128,
            Validation: BuiltInValidationKind.CryptoNoteAddress
        ),
        new(
            "cpe23",
            "Finds bounded CPE 2.3 formatted-string product identifier candidates",
            @"(?<![\p{L}\p{N}_])cpe:2[.]3:(?:\\[^\r\n]|[^:\\\s]){1,256}(?::(?:\\[^\r\n]|[^:\\\s]){1,256}){10}(?![:\\\p{L}\p{N}_])",
            "https://nvlpubs.nist.gov/nistpubs/Legacy/IR/nistir7695.pdf",
            UseNonBacktracking: false,
            BoundedRetryOverlap: 8192,
            Validation: BuiltInValidationKind.Cpe23
        ),
        new(
            "tlp_marking",
            "Finds exact FIRST Traffic Light Protocol 2.0 written marking candidates",
            @"(?<![\p{L}\p{N}_])TLP:(?:RED|AMBER(?:[+]STRICT)?|GREEN|CLEAR)(?![ \t]*[+])(?![\p{L}\p{N}_])",
            "https://www.first.org/tlp/",
            UseNonBacktracking: false,
            BoundedRetryOverlap: 32
        ),
        new(
            "email_message_id",
            "Finds labelled RFC 5322 message identifier candidates for email correlation",
            @"(?m)^(?:Message-ID|In-Reply-To|References):[ \t]*(?:\r?\n[ \t]+)?(?<message_id><[A-Za-z0-9!#$%&'*+/=?^_`{|}~.-]{1,64}@[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?>)(?![A-Za-z0-9])",
            "https://www.rfc-editor.org/rfc/rfc5322.html#section-3.6.4",
            UseNonBacktracking: false,
            OutputGroup: "message_id",
            BoundedRetryOverlap: 512,
            Validation: BuiltInValidationKind.EmailMessageId
        ),
        new(
            "lei",
            "Finds 20-character Legal Entity Identifier candidates that pass MOD 97-10",
            @"(?<![\p{L}\p{N}])[A-Z0-9]{18}[0-9]{2}(?![\p{L}\p{N}])",
            "https://www.gleif.org/content/4_lei-data/1_access-and-use-lei-data/2_level-1-data-lei-cdf-3-1-format/lei-cdf_version_3.1-documentation.html",
            UseNonBacktracking: false,
            BoundedRetryOverlap: 32,
            Validation: BuiltInValidationKind.Lei
        ),
        new(
            "npi",
            "Finds labelled US National Provider Identifier candidates with a valid check digit",
            @"(?<![\p{L}\p{N}_])[Nn][Pp][Ii][ \t]*[:=][ \t]*(?<npi>[12][0-9]{9})(?![0-9])",
            "https://www.cms.gov/Regulations-and-Guidance/Administrative-Simplification/NationalProvidentStand/Downloads/NPIcheckdigit.pdf",
            UseNonBacktracking: false,
            OutputGroup: "npi",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.Npi
        ),
        new(
            "itin",
            "Finds labelled US Individual Taxpayer Identification Number candidates in published ranges",
            @"(?<![\p{L}\p{N}_])[Ii][Tt][Ii][Nn][ \t]*[:=][ \t]*(?<itin>9[0-9]{2}(?<itin_sep>[- ])[0-9]{2}\k<itin_sep>[0-9]{4})(?![0-9])",
            "https://www.irs.gov/irm/part3/irm_03-013-005",
            UseNonBacktracking: false,
            OutputGroup: "itin",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.Itin
        ),
        new(
            "uk_nino",
            "Finds labelled UK National Insurance number candidates under HMRC syntax rules",
            @"(?<![\p{L}\p{N}_])[Nn][Ii][Nn][Oo][ \t]*[:=][ \t]*(?<nino>[A-Z]{2}[0-9]{6}[A-D])(?![A-Za-z0-9])",
            "https://www.gov.uk/hmrc-internal-manuals/national-insurance-manual/nim39110",
            UseNonBacktracking: false,
            OutputGroup: "nino",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.UkNino
        ),
        new(
            "md5_labelled",
            "Finds explicitly labelled 32-hex-character MD5 correlation values",
            @"(?<![\p{L}\p{N}_])[Mm][Dd]5[ \t]*[:=][ \t]*(?<digest>[0-9A-Fa-f]{32})(?![\p{L}\p{N}])",
            "https://www.rfc-editor.org/rfc/rfc1321.html",
            UseNonBacktracking: false,
            OutputGroup: "digest",
            BoundedRetryOverlap: 64
        ),
        new(
            "sha1_labelled",
            "Finds explicitly labelled 40-hex-character SHA-1 correlation values",
            @"(?<![\p{L}\p{N}_])[Ss][Hh][Aa]-1[ \t]*[:=][ \t]*(?<digest>[0-9A-Fa-f]{40})(?![\p{L}\p{N}])",
            "https://csrc.nist.gov/pubs/fips/180-4/upd1/final",
            UseNonBacktracking: false,
            OutputGroup: "digest",
            BoundedRetryOverlap: 80
        ),
        new(
            "sha384_labelled",
            "Finds explicitly labelled 96-hex-character SHA-384 correlation values",
            @"(?<![\p{L}\p{N}_])[Ss][Hh][Aa]-384[ \t]*[:=][ \t]*(?<digest>[0-9A-Fa-f]{96})(?![\p{L}\p{N}])",
            "https://csrc.nist.gov/pubs/fips/180-4/upd1/final",
            UseNonBacktracking: false,
            OutputGroup: "digest",
            BoundedRetryOverlap: 160
        ),
        new(
            "sha512_labelled",
            "Finds explicitly labelled 128-hex-character SHA-512 correlation values",
            @"(?<![\p{L}\p{N}_])[Ss][Hh][Aa]-512[ \t]*[:=][ \t]*(?<digest>[0-9A-Fa-f]{128})(?![\p{L}\p{N}])",
            "https://csrc.nist.gov/pubs/fips/180-4/upd1/final",
            UseNonBacktracking: false,
            OutputGroup: "digest",
            BoundedRetryOverlap: 192
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
            "Finds Tor v3 onion-service hostnames with valid version and checksum bytes",
            @"(?<![A-Za-z0-9-])[A-Za-z2-7]{56}\.[Oo][Nn][Ii][Oo][Nn](?![A-Za-z0-9.-])",
            "https://spec.torproject.org/rend-spec/encoding-onion-addresses.html",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"[A-Za-z2-7]{56}\.[Oo][Nn][Ii][Oo][Nn]",
            BoundedRetryOverlap: 128,
            Validation: BuiltInValidationKind.OnionV3
        ),
        new(
            "ethereum",
            "Finds 20-byte Ethereum addresses, requiring EIP-55 for mixed-case hexadecimal",
            @"(?<![A-Za-z0-9])0x[0-9A-Fa-f]{40}(?![A-Za-z0-9])",
            "https://ethereum.org/developers/docs/accounts/",
            UseNonBacktracking: false,
            RapidsSupersetPattern: @"0x[0-9A-Fa-f]{40}",
            BoundedRetryOverlap: 64,
            Validation: BuiltInValidationKind.EthereumAddress
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

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> Groups { get; } =
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["wallets"] =
                [
                    "bitcoin", "bitcoin_segwit", "ethereum", "tron", "solana", "xrp",
                    "dogecoin", "zcash", "cardano", "monero", "stellar", "bitcoin_cash",
                    "ton", "litecoin", "avalanche", "move_address", "near", "bittensor", "hedera",
                    "canton_party", "provenance_scope", "dashcoin2", "aeon", "bytecoin",
                    "dashcoin", "fantomcoin", "sumokoin",
                ],
                ["pii"] =
                [
                    "email", "usPhone", "intlPhone", "ssn", "canadian_sin", "dob",
                    "cc", "iban", "zip", "vin", "npi", "itin", "uk_nino",
                ],
                ["credentials"] =
                [
                    "urlUser", "jwt", "credential_assignment", "browser_credential_field",
                    "bitlocker", "pem_private_key",
                ],
                ["browser"] =
                [
                    "browser_profile_path", "browser_credential_field",
                ],
                ["registry"] =
                [
                    "reg_path", "reg_persistence", "reg_user_activity", "reg_usb",
                    "reg_execution", "reg_network", "reg_system_identity",
                ],
            }
        );

    internal static string GetValidationLabel(BuiltInPatternDefinition definition) =>
        definition.Validation switch
        {
            BuiltInValidationKind.None => "regex-only",
            BuiltInValidationKind.PaymentCard => "luhn",
            BuiltInValidationKind.Base64 => "canonical-base64",
            BuiltInValidationKind.BitLocker => "bitlocker-arithmetic",
            BuiltInValidationKind.Jwt => "jwt-compact-structure",
            BuiltInValidationKind.Iban => "iban-mod97",
            BuiltInValidationKind.CanadianSin => "canadian-sin-luhn",
            BuiltInValidationKind.DateOfBirth => "calendar-date",
            BuiltInValidationKind.Cpe23 => "cpe23-binding",
            BuiltInValidationKind.EmailMessageId => "rfc5322-message-id",
            BuiltInValidationKind.Lei => "lei-mod97",
            BuiltInValidationKind.Npi => "npi-luhn",
            BuiltInValidationKind.Itin => "itin-published-range",
            BuiltInValidationKind.UkNino => "hmrc-nino-syntax",
            _ => definition.Validation.ToString(),
        };

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
