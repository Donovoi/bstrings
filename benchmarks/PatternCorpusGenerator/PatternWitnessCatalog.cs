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
            ["cc"] = new("4111 1111 1111 1111", "4111 1111 1111 1112", "4111 1111 1111 1111"),
            ["ipv4"] = new("198.51.100.250", "1.2.3.4.5", "198.51.100.250"),
            ["ipv6"] = new("::ffff:192.0.2.128", "2001:::1", "::ffff:192.0.2.128"),
            ["email"] = new(
                "string@g.com",
                new string('a', 65) + "@g.com",
                "string@g.com"
            ),
            ["zip"] = new("90210-1234", "1234", "90210-1234"),
            ["urlUser"] = new(
                "https://analyst:secret@example.com/path",
                "https://analyst%zz@example.com/path",
                "analyst"
            ),
            ["url3986"] = new(
                "https://user@example.com:8443/a//b?x=1#fragment",
                "https://example.com/a%zz",
                "https://user@example.com:8443/a//b?x=1#fragment"
            ),
            ["xml"] = new("<Root id=\"1\">value</Root>", "<Root id=>value</Root>", "<Root id=\"1\">value</Root>"),
            ["sid"] = new("S-1-5-21-1-2-3-1001", "S-2-5-21", "S-1-5-21-1-2-3-1001"),
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
            ["intlPhone"] = new(
                "+61 412 345 678",
                "+01 234 567 890",
                "+61 412 345 678"
            ),
            ["canadian_sin"] = new(
                "SIN: 046 454 286",
                "SIN: 046 454 287",
                "046 454 286"
            ),
            ["dob"] = new("DOB: 1990-02-28", "DOB: 1990-02-30", "1990-02-28"),
            ["iban"] = new(
                "DE89 3704 0044 0532 0130 00",
                "DE89 3704 0044 0532 0130 01",
                "DE89 3704 0044 0532 0130 00"
            ),
            ["vin"] = new(
                "1HGCM82633A004352",
                "1HGCM82633I004352",
                "1HGCM82633A004352"
            ),
            ["jwt"] = new(
                "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c",
                "abc.def.ghi",
                "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c"
            ),
            ["credential_assignment"] = new(
                "password=correct-horse-battery",
                "password=x",
                "correct-horse-battery"
            ),
            ["browser_credential_field"] = new(
                "encryptedPassword",
                "displayName",
                "encryptedPassword"
            ),
            ["browser_profile_path"] = new(
                @"C:\Users\Case\AppData\Local\Google\Chrome\User Data\Default\Login Data",
                @"C:\Users\Case\Documents\Login Data",
                @"C:\Users\Case\AppData\Local\Google\Chrome\User Data\Default\Login Data"
            ),
            ["reg_persistence"] = new(
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Updater",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer",
                @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\Updater"
            ),
            ["reg_user_activity"] = new(
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies",
                @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}"
            ),
            ["reg_usb"] = new(
                @"HKLM\SYSTEM\CurrentControlSet\Enum\USBSTOR\Disk&Ven_Test",
                @"HKLM\SYSTEM\CurrentControlSet\Enum\PCI",
                @"HKLM\SYSTEM\CurrentControlSet\Enum\USBSTOR\Disk&Ven_Test"
            ),
            ["reg_execution"] = new(
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Lsa",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache"
            ),
            ["reg_network"] = new(
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{GUID}",
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts",
                @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles\{GUID}"
            ),
            ["reg_system_identity"] = new(
                @"HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation",
                @"HKLM\SYSTEM\CurrentControlSet\Control\Power",
                @"HKLM\SYSTEM\CurrentControlSet\Control\TimeZoneInformation"
            ),
            ["b64"] = new("SGVsbG8=", "SGVsbG9=", "SGVsbG8="),
            ["bitlocker"] = new(
                "001155-002310-003465-004620-005775-006930-008085-009240",
                "001156-002310-003465-004620-005775-006930-008085-009240",
                "001155-002310-003465-004620-005775-006930-008085-009240"
            ),
            ["bitcoin"] = new(
                "1BoatSLRHtKNngkdXEeobR76b53LETtpyT",
                "1BoatSLRHtKNngkdXEeobR76b53LETtpyU",
                "1BoatSLRHtKNngkdXEeobR76b53LETtpyT"
            ),
            ["bitcoin_segwit"] = new(
                "BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4",
                "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t5",
                "BC1QW508D6QEJXTDG4Y5R3ZARVARY0C5XW7KV8F3T4"
            ),
            ["tron"] = new(
                "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t",
                "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj61",
                "TR7NHqjeKQxGTCi8q8ZY4pL8otSzgjLj6t"
            ),
            ["solana"] = new(
                "11111111111111111111111111111111",
                new string('1', 31),
                "11111111111111111111111111111111"
            ),
            ["xrp"] = new(
                "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh",
                "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyT1",
                "rHb9CJAWyB4rj91VRWn96DkukG4bwdtyTh"
            ),
            ["dogecoin"] = new(
                "D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H1",
                "D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H2",
                "D5ERdEN1gsouFSs7zsq7VYJxyWP6dP28H1"
            ),
            ["zcash"] = new(
                "t1Hxw6JqWMnhDK5jRCieg5bFHM2qt7UtQvu",
                "t1Hxw6JqWMnhDK5jRCieg5bFHM2qt7UtQv1",
                "t1Hxw6JqWMnhDK5jRCieg5bFHM2qt7UtQvu"
            ),
            ["cardano"] = new(
                "addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl8",
                "addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl1",
                "addr1vx2fxv2umyhttkxyxp8x0dlpdt3k6cwng5pxj3jhsydzers66hrl8"
            ),
            ["stellar"] = new(
                "GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFN",
                "GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFA",
                "GCM5WPR4DDR24FSAX5LIEM4J7AI3KOWJYANSXEPKYXCSZOTAYXE75AFN"
            ),
            ["bitcoin_cash"] = new(
                "bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy",
                "bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyq",
                "bitcoincash:qp3wjpa3tjlj042z2wv7hahsldgwhwy0rq9sywjpyy"
            ),
            ["ton"] = new(
                "EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHF",
                "EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHA",
                "EQDKbjIcfM6ezt8KjKJJLshZJJSqX7XOA4ff-W72r5gqPrHF"
            ),
            ["litecoin"] = new(
                "LKKHMBjCU89fyFNgSRprDoD8Jb25N8uWvd",
                "LKKHMBjCU89fyFNgSRprDoD8Jb25N8uWv1",
                "LKKHMBjCU89fyFNgSRprDoD8Jb25N8uWvd"
            ),
            ["avalanche"] = new(
                "X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlp",
                "X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlq",
                "X-avax1qypqxpq9qcrsszg2pvxq6rs0zqg3yyc52qphlp"
            ),
            ["move_address"] = new(
                "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                "0x0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef",
                "0x0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
            ),
            ["near"] = new("alice.sub.near", "alice..sub.near", "alice.sub.near"),
            ["bittensor"] = new(
                "5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRzV",
                "5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRz1",
                "5DfhGyQdFobKM8NsWvEeAKk5EQQgYe9AydgJ7rMB6E1EqRzV"
            ),
            ["hedera"] = new("0.0.123-vfmkw", "0.0.123-abcde", "0.0.123-vfmkw"),
            ["canton_party"] = new(
                "Alice::1220f2fe29866fd6a0009ecc8a64ccdc09f1958bd0f801166baaee469d1251b2eb72",
                "Alice::1320f2fe29866fd6a0009ecc8a64ccdc09f1958bd0f801166baaee469d1251b2eb72",
                "Alice::1220f2fe29866fd6a0009ecc8a64ccdc09f1958bd0f801166baaee469d1251b2eb72"
            ),
            ["provenance_scope"] = new(
                "scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkwel",
                "scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkw1",
                "scope1qzge0zaztu65tx5x5llv5xc9ztsqxlkwel"
            ),
            ["aeon"] = new(
                "WmsSWgtT1JPg5e3cK41hKXSHVpKW7e47bjgiKmWZkYrhSS5LhRemNyqayaSBtAQ6517eo5PtH9wxHVmM78JDZSUu2W8PqRiNs",
                "WmsSWgtT1JPg5e3cK41hKXSHVpKW7e47bjgiKmWZkYrhSS5LhRemNyqayaSBtAQ6517eo5PtH9wxHVmM78JDZSUu2W8PqRiN1",
                "WmsSWgtT1JPg5e3cK41hKXSHVpKW7e47bjgiKmWZkYrhSS5LhRemNyqayaSBtAQ6517eo5PtH9wxHVmM78JDZSUu2W8PqRiNs"
            ),
            ["bytecoin"] = new(
                "2AaF4qEmER6dNeM6dfiBFL7kqund3HYGvMBF3ttsNd9SfzgYB6L7ep1Yg1osYJzLdaKAYSLVh6e6jKnAuzj3bw1oGyd1x7Z",
                "2AaF4qEmER6dNeM6dfiBFL7kqund3HYGvMBF3ttsNd9SfzgYB6L7ep1Yg1osYJzLdaKAYSLVh6e6jKnAuzj3bw1oGyd1x71",
                "2AaF4qEmER6dNeM6dfiBFL7kqund3HYGvMBF3ttsNd9SfzgYB6L7ep1Yg1osYJzLdaKAYSLVh6e6jKnAuzj3bw1oGyd1x7Z"
            ),
            ["dashcoin"] = new(
                "D3XeV6X3otr2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8EmnBXn",
                "D3XeV6X3otr2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8EmnBX1",
                "D3XeV6X3otr2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8EmnBXn"
            ),
            ["dashcoin2"] = new(
                "Xgtyuk76vhuFW2iT7UAiHgNdWXCf3J34wh",
                "Xgtyuk76vhuFW2iT7UAiHgNdWXCf3J34wi",
                "Xgtyuk76vhuFW2iT7UAiHgNdWXCf3J34wh"
            ),
            ["fantomcoin"] = new(
                "6gt5xRQJhjC2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8KzEdBp",
                "6gt5xRQJhjC2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8KzEdB1",
                "6gt5xRQJhjC2LxFSMtsQ5k3gsHPkECmXt52nKM8ZY8z26NhMJWtsWSA7icPFuECstJ94XRDHZYFLSAQSTAftscna8KzEdBp"
            ),
            ["monero"] = new(
                "4AdUndXHHZ6cfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2684Rge",
                "4AdUndXHHZ6cfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2684Rg1",
                "4AdUndXHHZ6cfufTMvppY6JwXNouMBzSkbLYfpAV5Usx3skxNgYeYTRj5UzqtReoS44qo9mtmXCqY45DJ852K5Jv2684Rge"
            ),
            ["sumokoin"] = new(
                "Sumoo72D2v7KEGvfPzGH5qC5VHGnLmafaAhoMooPwRALNwm2oSyK3myTaFefvyg5bviMbBXUFWN8McswTRowHNYXfo34VD9oWr7",
                "Sumoo72D2v7KEGvfPzGH5qC5VHGnLmafaAhoMooPwRALNwm2oSyK3myTaFefvyg5bviMbBXUFWN8McswTRowHNYXfo34VD9oWr1",
                "Sumoo72D2v7KEGvfPzGH5qC5VHGnLmafaAhoMooPwRALNwm2oSyK3myTaFefvyg5bviMbBXUFWN8McswTRowHNYXfo34VD9oWr7"
            ),
            ["cve"] = new("CVE-2026-1234", "CVE-2026-123", "CVE-2026-1234"),
            ["pem_private_key"] = new(
                "-----BEGIN PRIVATE KEY-----",
                "-----BEGIN RSA PRIVATE KEY-----",
                "-----BEGIN PRIVATE KEY-----"
            ),
            ["onion_v3"] = new(
                "pg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion",
                "qg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion",
                "pg6mmjiyjmcrsslvykfwnntlaru7p5svn6y2ymmju6nubxndf4pscryd.onion"
            ),
            ["ethereum"] = new(
                "0x5e97870f263700f46aa00d967821199b9bc5a120",
                "0x5AAeb6053F3E94C9b9A09f33669435E7Ef1BeAed",
                "0x5e97870f263700f46aa00d967821199b9bc5a120"
            ),
            ["sha256"] = new(new string('a', 64), new string('a', 63), new string('a', 64)),
        };
}
