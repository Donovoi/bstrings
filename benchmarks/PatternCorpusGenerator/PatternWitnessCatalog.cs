namespace bstrings.Benchmarks;

internal sealed record PatternWitness(
    string Positive,
    string Negative,
    string ExpectedOutput
);

internal static class PatternWitnessCatalog
{
    internal static IReadOnlyDictionary<string, PatternWitness> ByName { get; } =
        new Dictionary<string, PatternWitness>(StringComparer.OrdinalIgnoreCase)
        {
            ["guid"] = new(
                "00112233-4455-6677-8899-aabbccddeeff",
                "00112233-4455-6677-8899-aabbccddeezz",
                "00112233-4455-6677-8899-aabbccddeeff"
            ),
            ["usPhone"] = new("(212) 555-0199", "(212555-0199", "(212) 555-0199"),
            ["unc"] = new(
                @"\\server-01\share$\folder",
                @"\server\share",
                @"\\server-01\share$\folder"
            ),
            ["named_pipe"] = new(
                @"\\.\pipe\svc-control",
                @"\\.\pipe\",
                @"\\.\pipe\svc-control"
            ),
            ["mac"] = new("00:11:22:aa:BB:cc", "00:11-22:33:44:55", "00:11:22:aa:BB:cc"),
            ["ssn"] = new("123-45-6789", "666-45-6789", "123-45-6789"),
            ["cc"] = new("4111 1111 1111 1111", "4111 1111", "4111 1111 1111 1111"),
            ["ipv4"] = new("198.51.100.250", "1.2.3.4.5", "198.51.100.250"),
            ["ipv6"] = new("::ffff:192.0.2.128", "2001:::1", "::ffff:192.0.2.128"),
            ["email"] = new(
                "user.name+tag@example.technology",
                "user@-example.com",
                "user.name+tag@example.technology"
            ),
            ["zip"] = new("90210-1234", "1234", "90210-1234"),
            ["urlUser"] = new(
                "https://analyst:secret@example.com/path",
                "https://example.com/path",
                "analyst"
            ),
            ["url3986"] = new(
                "https://user@example.com:8443/a//b?x=1#fragment",
                "not-a-url",
                "https://user@example.com:8443/a//b?x=1#fragment"
            ),
            ["xml"] = new("<Root id=\"1\">value</Root>", "<Root>value</root>", "<Root id=\"1\">value</Root>"),
            ["sid"] = new("S-1-5-21-1-2-3-1001", "S-1-x-21", "S-1-5-21-1-2-3-1001"),
            ["win_path"] = new(
                "\"C:\\folder one\\file.txt\"",
                "C:relative.txt",
                "\"C:\\folder one\\file.txt\""
            ),
            ["var_set"] = new(
                "TEMP=C:\\Windows\\Temp",
                "9INVALID=value",
                "TEMP=C:\\Windows\\Temp"
            ),
            ["reg_path"] = new(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows",
                @"HKEY_LOCAL_MACHINE\NOT_A_HIVE\Microsoft",
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows"
            ),
            ["b64"] = new("SGVsbG8=", "abcd", "SGVsbG8="),
            ["bitlocker"] = new(
                "123456-234567-345678-456789-567890-678901-789012-890123",
                "123456-234567-345678-456789-567890-678901-789012",
                "123456-234567-345678-456789-567890-678901-789012-890123"
            ),
            ["bitcoin"] = new("1" + new string('A', 25), "1" + new string('O', 25), "1" + new string('A', 25)),
            ["aeon"] = new("Wms" + new string('A', 94), "WmS" + new string('A', 94), "Wms" + new string('A', 94)),
            ["bytecoin"] = new("2A" + new string('A', 93), "2O" + new string('A', 93), "2A" + new string('A', 93)),
            ["dashcoin"] = new("D" + new string('A', 94), "d" + new string('A', 94), "D" + new string('A', 94)),
            ["dashcoin2"] = new("X" + new string('A', 33), "x" + new string('A', 33), "X" + new string('A', 33)),
            ["fantomcoin"] = new("6" + new string('A', 94), "6" + new string('O', 94), "6" + new string('A', 94)),
            ["monero"] = new("8" + new string('A', 94), "4O" + new string('A', 93), "8" + new string('A', 94)),
            ["sumokoin"] = new("Sumoo" + new string('A', 94), "sumoo" + new string('A', 94), "Sumoo" + new string('A', 94)),
            ["cve"] = new("CVE-2026-1234", "CVE-2026-123", "CVE-2026-1234"),
            ["pem_private_key"] = new(
                "-----BEGIN PRIVATE KEY-----",
                "-----BEGIN RSA PRIVATE KEY-----",
                "-----BEGIN PRIVATE KEY-----"
            ),
            ["onion_v3"] = new(
                new string('a', 56) + ".onion",
                new string('a', 55) + ".onion",
                new string('a', 56) + ".onion"
            ),
            ["ethereum"] = new(
                "0x5e97870f263700f46aa00d967821199b9bc5a120",
                "0x5e97870f263700f46aa00d967821199b9bc5a12",
                "0x5e97870f263700f46aa00d967821199b9bc5a120"
            ),
            ["sha256"] = new(new string('a', 64), new string('a', 63), new string('a', 64)),
        };
}
