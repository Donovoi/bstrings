using System.Security.Cryptography;
using System.Text.Json;

namespace bstrings.Tests;

internal static class BundleManifestTestFixture
{
    internal static void Write(string root)
    {
        var files = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path =>
                !string.Equals(
                    Path.GetFileName(path),
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .Select(path => new
            {
                path = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                bytes = new FileInfo(path).Length,
                sha256 = Convert
                    .ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                    .ToLowerInvariant(),
            })
            .OrderBy(entry => entry.path, StringComparer.Ordinal)
            .ToArray();
        File.WriteAllText(
            Path.Combine(root, BundleManifestVerifier.ManifestFileName),
            JsonSerializer.Serialize(new { schemaVersion = 1, files })
        );
    }
}
