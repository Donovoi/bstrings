#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace bstrings;

internal sealed record BundleVerificationResult(
    long FileCount,
    long TotalBytes,
    string ManifestPath,
    string ManifestSha256,
    IReadOnlyDictionary<string, string> FileSha256
);

internal static class BundleManifestVerifier
{
    internal const string ManifestFileName = "airgap-manifest.json";
    internal const string IncompleteMarkerFileName = ".incomplete";
    private const int SupportedSchemaVersion = 1;
    internal const long MaximumManifestBytes = 128L * 1024 * 1024;
    private static readonly HashSet<string> ReservedWindowsDeviceNames = new(
        [
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1",
            "COM2",
            "COM3",
            "COM4",
            "COM5",
            "COM6",
            "COM7",
            "COM8",
            "COM9",
            "LPT1",
            "LPT2",
            "LPT3",
            "LPT4",
            "LPT5",
            "LPT6",
            "LPT7",
            "LPT8",
            "LPT9",
        ],
        StringComparer.OrdinalIgnoreCase
    );

    internal sealed record ExpectedFile(string RelativePath, long Length, string Sha256);
    internal sealed record ParsedManifest(
        Dictionary<string, ExpectedFile> Files,
        string Sha256
    );

    internal static BundleVerificationResult Verify(
        string bundleRoot,
        bool allowIncompleteMarker = false
    ) => VerifyCore(bundleRoot, File.GetAttributes, allowIncompleteMarker);

    internal static BundleVerificationResult VerifyForTesting(
        string bundleRoot,
        Func<string, FileAttributes> getAttributes,
        bool allowIncompleteMarker = false
    ) => VerifyCore(bundleRoot, getAttributes, allowIncompleteMarker);

    private static BundleVerificationResult VerifyCore(
        string bundleRoot,
        Func<string, FileAttributes> getAttributes,
        bool allowIncompleteMarker
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleRoot);
        ArgumentNullException.ThrowIfNull(getAttributes);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Bundle directory was not found: '{root}'.");
        }
        RejectReparsePoint(root, getAttributes(root));

        var manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"Bundle manifest was not found at '{manifestPath}'.",
                manifestPath
            );
        }
        RejectReparsePoint(manifestPath, getAttributes(manifestPath));

        var parsedManifest = ReadManifest(manifestPath);
        var expected = parsedManifest.Files;
        var actual = EnumerateBundleFiles(root, getAttributes);
        if (allowIncompleteMarker)
        {
            if (!actual.Remove(IncompleteMarkerFileName))
            {
                throw new InvalidDataException(
                    "Builder-only incomplete-marker verification requires a root .incomplete file."
                );
            }
        }
        else if (actual.ContainsKey(IncompleteMarkerFileName))
        {
            throw new InvalidDataException(
                "Bundle has a lingering root .incomplete marker."
            );
        }

        var missing = expected.Keys.Except(actual.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        var unexpected = actual.Keys
            .Except(expected.Keys, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length != 0 || unexpected.Length != 0)
        {
            throw new InvalidDataException(
                "Bundle file set differs from its manifest; "
                    + $"missing={FormatPaths(missing)}, unexpected={FormatPaths(unexpected)}."
            );
        }

        long totalBytes = 0;
        foreach (var entry in expected.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            var path = actual[entry.RelativePath];
            RejectLinkedPath(root, path, getAttributes);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.SequentialScan
            );
            if (stream.Length != entry.Length)
            {
                throw new InvalidDataException(
                    $"Bundle file size mismatch for '{entry.RelativePath}': "
                        + $"expected {entry.Length}, got {stream.Length}."
                );
            }

            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(entry.Sha256),
                    Convert.FromHexString(digest)
                ))
            {
                throw new InvalidDataException(
                    $"Bundle SHA-256 mismatch for '{entry.RelativePath}': "
                        + $"expected {entry.Sha256}, got {digest}."
                );
            }
            if (stream.Length != entry.Length)
            {
                throw new InvalidDataException(
                    $"Bundle file changed length while it was being verified: '{entry.RelativePath}'."
                );
            }

            try
            {
                totalBytes = checked(totalBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("Bundle byte total exceeds the supported range.", ex);
            }
        }

        return new BundleVerificationResult(
            expected.Count,
            totalBytes,
            manifestPath,
            parsedManifest.Sha256,
            new ReadOnlyDictionary<string, string>(
                expected.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Sha256,
                    StringComparer.OrdinalIgnoreCase
                )
            )
        );
    }

    private static ParsedManifest ReadManifest(string manifestPath)
    {
        using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan
        );
        return ReadManifest(stream, stream.Length);
    }

    internal static ParsedManifest ReadManifest(Stream stream, long declaredLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new InvalidDataException("Bundle manifest stream must be readable.");
        }
        if (declaredLength <= 0 || declaredLength > MaximumManifestBytes)
        {
            throw new InvalidDataException(
                $"Bundle manifest length must be between 1 and {MaximumManifestBytes:N0} bytes."
            );
        }

        var manifestBytes = new byte[checked((int)declaredLength)];
        stream.ReadExactly(manifestBytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException(
                "Bundle manifest stream exceeded its declared length."
            );
        }
        var manifestSha256 = Convert
            .ToHexString(SHA256.HashData(manifestBytes))
            .ToLowerInvariant();
        using var document = JsonDocument.Parse(
            manifestBytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            }
        );
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Bundle manifest root must be an object.");
        }

        JsonElement files = default;
        var schemaVersion = -1;
        var rootProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!rootProperties.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Bundle manifest contains duplicate root property '{property.Name}'."
                );
            }
            switch (property.Name)
            {
                case "schemaVersion":
                    if (
                        property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out schemaVersion)
                    )
                    {
                        throw new InvalidDataException(
                            "Bundle manifest schemaVersion must be an integer."
                        );
                    }
                    break;
                case "files":
                    files = property.Value;
                    break;
                default:
                    throw new InvalidDataException(
                        $"Bundle manifest contains unknown root property '{property.Name}'."
                    );
            }
        }
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported or missing bundle manifest schema version '{schemaVersion}'."
            );
        }
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Bundle manifest must contain a non-empty files array.");
        }

        var expected = new Dictionary<string, ExpectedFile>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in files.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Bundle manifest file entry {index} must be an object."
                );
            }

            string? relativePath = null;
            string? sha256 = null;
            long length = -1;
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Bundle manifest file entry {index} contains duplicate property '{property.Name}'."
                    );
                }
                switch (property.Name)
                {
                    case "path":
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidDataException(
                                $"Bundle manifest file entry {index} path must be text."
                            );
                        }
                        relativePath = property.Value.GetString();
                        break;
                    case "bytes":
                        if (
                            property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out length)
                            || length < 0
                        )
                        {
                            throw new InvalidDataException(
                                $"Bundle manifest file entry {index} bytes must be a non-negative integer."
                            );
                        }
                        break;
                    case "sha256":
                        if (property.Value.ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidDataException(
                                $"Bundle manifest file entry {index} sha256 must be text."
                            );
                        }
                        sha256 = property.Value.GetString();
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Bundle manifest file entry {index} contains unknown property '{property.Name}'."
                        );
                }
            }

            relativePath = ValidateRelativePath(relativePath);
            if (length < 0)
            {
                throw new InvalidDataException(
                    $"Bundle manifest file entry {index} is missing bytes."
                );
            }
            if (!IsLowercaseSha256(sha256))
            {
                throw new InvalidDataException(
                    $"Bundle manifest file entry {index} contains an invalid lowercase SHA-256."
                );
            }
            if (!expected.TryAdd(relativePath, new ExpectedFile(relativePath, length, sha256!)))
            {
                throw new InvalidDataException(
                    $"Bundle manifest contains a case-insensitive duplicate path: '{relativePath}'."
                );
            }
            index++;
        }
        return new ParsedManifest(expected, manifestSha256);
    }

    internal static string ValidateRelativePath(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidDataException("Bundle manifest paths must be non-empty strings.");
        }
        if (value.Contains('\\', StringComparison.Ordinal) || value.StartsWith('/'))
        {
            throw new InvalidDataException(
                $"Bundle manifest contains an unsafe relative path: '{value}'."
            );
        }

        var segments = value.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                throw new InvalidDataException(
                    $"Bundle manifest contains a non-canonical relative path: '{value}'."
                );
            }
            if (segment.EndsWith(' ') || segment.EndsWith('.'))
            {
                throw new InvalidDataException(
                    $"Bundle manifest contains a Windows-ambiguous relative path: '{value}'."
                );
            }
            if (segment.Any(IsUnsafeWindowsPathCharacter))
            {
                throw new InvalidDataException(
                    $"Bundle manifest contains an unsafe Windows path character: '{value}'."
                );
            }
            var deviceStem = segment.Split('.', 2)[0];
            if (ReservedWindowsDeviceNames.Contains(deviceStem))
            {
                throw new InvalidDataException(
                    $"Bundle manifest contains a reserved Windows device path: '{value}'."
                );
            }
        }
        if (
            string.Equals(value, ManifestFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                value,
                IncompleteMarkerFileName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidDataException(
                "The bundle manifest cannot list a reserved root path."
            );
        }
        return value;
    }

    private static bool IsUnsafeWindowsPathCharacter(char character) =>
        character < ' ' || character is '<' or '>' or ':' or '"' or '|' or '?' or '*';

    private static bool IsLowercaseSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is >= '0' and <= '9' || character is >= 'a' and <= 'f'
        );

    private static Dictionary<string, string> EnumerateBundleFiles(
        string root,
        Func<string, FileAttributes> getAttributes
    )
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);
        var enumerationOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        while (pending.Count != 0)
        {
            var directory = pending.Pop();
            RejectReparsePoint(directory, getAttributes(directory));
            foreach (
                var candidate in Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    enumerationOptions
                )
            )
            {
                var attributes = getAttributes(candidate);
                RejectReparsePoint(candidate, attributes);
                var relativePath = Path.GetRelativePath(root, candidate)
                    .Replace(Path.DirectorySeparatorChar, '/');
                relativePath = ValidateActualRelativePath(relativePath);
                if (
                    string.Equals(
                        relativePath,
                        IncompleteMarkerFileName,
                        StringComparison.Ordinal
                    )
                    && (attributes & FileAttributes.Directory) != 0
                )
                {
                    throw new InvalidDataException(
                        "The root .incomplete marker must be a physical regular file."
                    );
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(candidate);
                    continue;
                }
                if ((attributes & FileAttributes.Device) != 0 || !File.Exists(candidate))
                {
                    throw new InvalidDataException(
                        $"Bundle entry is not a regular file: '{relativePath}'."
                    );
                }
                if (string.Equals(relativePath, ManifestFileName, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!files.TryAdd(relativePath, candidate))
                {
                    throw new InvalidDataException(
                        $"Bundle contains case-insensitive duplicate paths: '{relativePath}'."
                    );
                }
            }
        }
        return files;
    }

    private static string ValidateActualRelativePath(string value)
    {
        if (
            string.Equals(value, ManifestFileName, StringComparison.Ordinal)
            || string.Equals(value, IncompleteMarkerFileName, StringComparison.Ordinal)
        )
        {
            return value;
        }
        return ValidateRelativePath(value);
    }

    private static void RejectLinkedPath(
        string root,
        string path,
        Func<string, FileAttributes> getAttributes
    )
    {
        var current = root;
        RejectReparsePoint(current, getAttributes(current));
        foreach (
            var segment in Path.GetRelativePath(root, path).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current, getAttributes(current));
        }
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"Bundle contains a link or reparse point: '{path}'."
            );
        }
    }

    private static string FormatPaths(IEnumerable<string> paths)
    {
        var selected = paths.OrderBy(path => path, StringComparer.Ordinal).Take(5).ToArray();
        return selected.Length == 0 ? "[]" : $"[{string.Join(", ", selected)}]";
    }
}
