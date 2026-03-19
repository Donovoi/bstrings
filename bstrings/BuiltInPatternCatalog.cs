using System.Collections.Generic;

namespace bstrings;

internal static class BuiltInPatternCatalog
{
    internal static IReadOnlyDictionary<string, string> Descriptions { get; } =
        new Dictionary<string, string>
        {
            ["guid"] = "\tFinds GUIDs",
            ["usPhone"] = "\tFinds US phone numbers",
            ["unc"] = "\tFinds UNC paths",
            ["mac"] = "\tFinds MAC addresses",
            ["ssn"] = "\tFinds US Social Security Numbers",
            ["cc"] = "\tFinds credit card numbers",
            ["ipv4"] = "\tFinds IP version 4 addresses",
            ["ipv6"] = "\tFinds IP version 6 addresses",
            ["email"] = "\tFinds embedded email addresses",
            ["zip"] = "\tFinds zip codes",
            ["urlUser"] = "\tFinds usernames in URLs",
            ["url3986"] = "\tFinds URLs according to RFC 3986",
            ["xml"] = "\tFinds XML/HTML tags",
            ["sid"] = "\tFinds Microsoft Security Identifiers (SID)",
            ["win_path"] = @"Finds Windows style paths (C:\folder1\folder2\file.txt)",
            ["var_set"] = "\tFinds environment variables being set (OS=Windows_NT)",
            ["reg_path"] = "Finds paths related to Registry hives",
            ["b64"] = "\tFinds valid formatted base 64 strings",
            ["bitlocker"] = "Finds Bitlocker recovery keys",
            ["bitcoin"] = "\tFinds BitCoin wallet addresses",
            ["aeon"] = "\tFinds Aeon wallet addresses",
            ["bytecoin"] = "Finds ByteCoin wallet addresses",
            ["dashcoin"] = "Finds DashCoin wallet addresses (D*)",
            ["dashcoin2"] = "Finds DashCoin wallet addresses (7|X)*",
            ["fantomcoin"] = "Finds Fantomcoin wallet addresses",
            ["monero"] = "\tFinds Monero wallet addresses",
            ["sumokoin"] = "Finds SumoKoin wallet addresses",
        };

    internal static IReadOnlyDictionary<string, string> Patterns { get; } =
        new Dictionary<string, string>
        {
            ["bitcoin"] = @"\b[13][a-km-zA-HJ-NP-Z1-9]{25,34}\b",
            ["aeon"] = @"Wm[st]{1}[0-9a-zA-Z]{94}",
            ["bytecoin"] = @"2[0-9AB][0-9a-zA-Z]{93}",
            ["dashcoin"] = "D[0-9a-zA-Z]{94}",
            ["dashcoin2"] = "(7|X)[a-zA-Z0-9]{33}",
            ["fantomcoin"] = "6[0-9a-zA-Z]{94}",
            ["monero"] = "4[0-9AB][0-9a-zA-Z]{93}|4[0-9AB][0-9a-zA-Z]{104}",
            ["sumokoin"] = "Sumoo[0-9a-zA-Z]{94}",
            ["b64"] = @"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})$",
            ["bitlocker"] = @"[0-9]{6}?-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}",
            ["reg_path"] = @"([a-z0-9]\\)*(software\\)|(sam\\)|(system\\)|(security\\)[a-z0-9\\]+",
            ["var_set"] = @"^[a-z_0-9]+=[\\/:\*\?<>|;\- _a-z0-9]+",
            ["win_path"] = @"(?:""?[a-zA-Z]\:|\\\\[^\\\/\:\*\?\<\>\|]+\\[^\\\/\:\*\?\<\>\|]*)\\(?:[^\\\/\:\*\?\<\>\|]+\\)*\w([^\\\/\:\*\?\<\>\|])*",
            ["sid"] = @"^S-\d-\d+-(\d+-){1,14}\d+$",
            ["xml"] = @"\A<([A-Z][A-Z0-9]*)\b[^>]*>(.*?)</\1>\z",
            ["guid"] = @"\b[A-F0-9]{8}(?:-[A-F0-9]{4}){3}-[A-F0-9]{12}\b",
            ["usPhone"] = @"\(?\b[2-9][0-9]{2}\)?[-. ]?[2-9][0-9]{2}[-. ]?[0-9]{4}\b",
            ["unc"] = @"^\\\\(?<server>[a-z0-9 %._-]+)\\(?<share>[a-z0-9 $%._-]+)",
            ["mac"] = "\\b[0-9A-F]{2}([-:]?)(?:[0-9A-F]{2}\\1){4}[0-9A-F]{2}\\b",
            ["ssn"] = "\\b(?!000)(?!666)[0-8][0-9]{2}[- ](?!00)[0-9]{2}[- ](?!0000)[0-9]{4}\\b",
            ["cc"] = @"^[ -]*(?:4[ -]*(?:\d[ -]*){11}(?:(?:\d[ -]*){3})?\d|5[ -]*[1-5](?:[ -]*[0-9]){14}|6[ -]*(?:0[ -]*1[ -]*1|5[ -]*\d[ -]*\d)(?:[ -]*[0-9]){12}|3[ -]*[47](?:[ -]*[0-9]){13}|3[ -]*(?:0[ -]*[0-5]|[68][ -]*[0-9])(?:[ -]*[0-9]){11}|(?:2[ -]*1[ -]*3[ -]*1|1[ -]*8[ -]*0[ -]*0|3[ -]*5(?:[ -]*[0-9]){3})(?:[ -]*[0-9]){11})[ -]*$",
            ["ipv4"] = @"\b(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\b",
            ["ipv6"] = @"(?<![:.\w])(?:[A-F0-9]{1,4}:){7}[A-F0-9]{1,4}(?![:.\w])",
            ["email"] = @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,6}\b",
            ["zip"] = @"\A\b[0-9]{5}(?:-[0-9]{4})?\b\z",
            ["urlUser"] = @"^[a-z0-9+\-.]+://(?<user>[a-z0-9\-._~%!$&'()*+,;=]+)@",
            ["url3986"] = @"^
        [a-z][a-z0-9+\-.]*://                       # Scheme
        ([a-z0-9\-._~%!$&'()*+,;=]+@)?              # User
        (?<host>[a-z0-9\-._~%]+                     # Named host
        |\[[a-f0-9:.]+\]                            # IPv6 host
        |\[v[a-f0-9][a-z0-9\-._~%!$&'()*+,;=:]+\])  # IPvFuture host
        (:[0-9]+)?                                  # Port
        (/[a-z0-9\-._~%!$&'()*+,;=:@]+)*/?          # Path
        (\?[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Query
        (\#[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Fragment
        $",
        };
}