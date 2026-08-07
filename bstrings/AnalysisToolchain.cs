#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace bstrings;

internal sealed record BundleIntegrity(
    string Manifest,
    string ManifestSha256,
    long FileCount,
    long ByteCount,
    string Executable,
    string ExecutableSha256
);

internal sealed record AnalysisToolchain(
    string BundleRoot,
    BundleIntegrity BundleIntegrity,
    string PythonExecutable,
    string EnrichmentAdapter,
    string? OcrPythonExecutable,
    string? OcrExecutable,
    string? OcrAdapter,
    string? OcrEngine,
    string? OcrEngineVersion,
    string? OcrModelPath,
    string? OcrModelId,
    string? OcrModelRevision,
    string? OcrModelSha256,
    string? MagikaExecutable,
    string? FlossExecutable,
    string? LlamaServer,
    string? TranslationModelPath,
    string? TranslationModelId,
    string? TranslationModelRevision,
    string? TranslationModelSha256
);

internal static class AnalysisToolchainLocator
{
    private const string OfflineBundleAcquisition =
        "Use the checked Install-BstringsQuality.ps1 asset from the latest GitHub Release "
        + "to install and verify the complete quality kit: "
        + "https://github.com/Donovoi/bstrings/blob/v1.9.7/README.md#get-started";

    private const string ConfigurationFileName = "airgap-config.json";
    private const string MagikaUrl = "https://github.com/google/magika#command-line-tool";
    private const string FlossUrl = "https://github.com/mandiant/flare-floss/releases";
    private const string LlamaUrl = "https://github.com/ggml-org/llama.cpp/releases";
    private const string ModelUrl = "https://huggingface.co/tencent/Hy-MT2-1.8B-GGUF";
    private const string OcrUrl =
        "https://github.com/Donovoi/bstrings/actions/workflows/dotnet-desktop.yml";

    internal static bool HasImplicitBundleConfiguration() =>
        HasImplicitBundleConfiguration(
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable("BSTRINGS_AIRGAP_BUNDLE")
        );

    internal static bool HasImplicitBundleConfiguration(
        string applicationBaseDirectory,
        string? environmentBundleRoot
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBaseDirectory);
        return !string.IsNullOrWhiteSpace(environmentBundleRoot)
            || Path.Exists(
                Path.Combine(Path.GetFullPath(applicationBaseDirectory), ConfigurationFileName)
            )
            || Path.Exists(
                Path.Combine(
                    Path.GetFullPath(applicationBaseDirectory),
                    BundleManifestVerifier.ManifestFileName
                )
            );
    }

    internal static AnalysisToolchain Locate(
        string? explicitRoot,
        bool requireExplicitBundle,
        bool requireRecovery = true,
        bool requireTranslation = true,
        bool requireOcr = false,
        string? executingExecutablePath = null
    )
    {
        var candidates = CandidateRoots(explicitRoot, requireExplicitBundle).ToList();
        var bundleRoot = candidates.FirstOrDefault(
            path => File.Exists(Path.Combine(path, ConfigurationFileName))
        );
        if (bundleRoot is null)
        {
            var searched = string.Join(", ", candidates.Select(path => $"'{path}'"));
            throw new DirectoryNotFoundException(
                $"No {ConfigurationFileName} was found. Supply --bundle-root, set "
                    + $"BSTRINGS_AIRGAP_BUNDLE, or run from a complete offline bundle. Searched: {searched}. "
                    + OfflineBundleAcquisition
            );
        }

        var verification = BundleManifestVerifier.Verify(bundleRoot);
        var manifest = Path.GetRelativePath(bundleRoot, verification.ManifestPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        var configurationPath = Path.Combine(bundleRoot, ConfigurationFileName);
        using var document = JsonDocument.Parse(File.ReadAllText(configurationPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
        {
            throw new InvalidDataException(
                $"'{configurationPath}' does not use supported schema version 1."
            );
        }

        var configuredExecutable = RequiredFile(
            bundleRoot,
            RequiredText(root, "bstringsExecutable", configurationPath),
            "bstrings executable",
            "https://github.com/Donovoi/bstrings/releases"
        );
        var executableRelativePath = Path.GetRelativePath(bundleRoot, configuredExecutable)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!verification.FileSha256.TryGetValue(executableRelativePath, out var expectedExecutableSha256))
        {
            throw new InvalidDataException(
                $"The configured bstrings executable is not governed by {BundleManifestVerifier.ManifestFileName}: '{executableRelativePath}'."
            );
        }
        executingExecutablePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executingExecutablePath))
        {
            throw new InvalidOperationException(
                "The running bstrings executable path is unavailable; bundle provenance cannot be established."
            );
        }
        var runningExecutable = Path.GetFullPath(executingExecutablePath);
        if (!File.Exists(runningExecutable))
        {
            throw new FileNotFoundException(
                "The running bstrings executable could not be read for bundle provenance.",
                runningExecutable
            );
        }
        var runningExecutableSha256 = Sha256File(runningExecutable);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(expectedExecutableSha256),
                Convert.FromHexString(runningExecutableSha256)
            ))
        {
            throw new InvalidDataException(
                "The running bstrings executable does not match the manifest-governed "
                    + $"'{executableRelativePath}' in the selected complete bundle. "
                    + $"expected SHA-256 {expectedExecutableSha256}, found {runningExecutableSha256}."
            );
        }
        var bundleIntegrity = new BundleIntegrity(
            manifest,
            verification.ManifestSha256,
            verification.FileCount,
            verification.TotalBytes,
            executableRelativePath,
            runningExecutableSha256
        );

        var model = requireTranslation
            ? RequiredObject(root, "translationModel", configurationPath)
            : default;
        var ocr = requireOcr ? RequiredObject(root, "ocr", configurationPath) : default;
        var ocrModel = requireOcr
            ? RequiredObject(ocr, "model", configurationPath)
            : default;
        return new AnalysisToolchain(
            bundleRoot,
            bundleIntegrity,
            RequiredFile(bundleRoot, RequiredText(root, "pythonExecutable", configurationPath), "portable Python", "https://www.python.org/downloads/windows/"),
            RequiredFile(bundleRoot, RequiredText(root, "enrichmentAdapter", configurationPath), "bstrings enrichment adapter", "https://github.com/Donovoi/bstrings"),
            requireOcr
                ? RequiredFile(bundleRoot, RequiredText(ocr, "pythonExecutable", configurationPath), "OCR Python runtime", OcrUrl)
                : null,
            requireOcr
                ? RequiredFile(bundleRoot, RequiredText(ocr, "executable", configurationPath), "OCR executable", OcrUrl)
                : null,
            requireOcr
                ? RequiredFile(bundleRoot, RequiredText(ocr, "adapter", configurationPath), "OCR adapter", OcrUrl)
                : null,
            requireOcr ? RequiredText(ocr, "engine", configurationPath) : null,
            requireOcr ? RequiredText(ocr, "engineVersion", configurationPath) : null,
            requireOcr
                ? RequiredFile(bundleRoot, RequiredText(ocrModel, "path", configurationPath), "OCR model pack", OcrUrl)
                : null,
            requireOcr ? RequiredText(ocrModel, "id", configurationPath) : null,
            requireOcr ? RequiredText(ocrModel, "revision", configurationPath) : null,
            requireOcr
                ? ValidateSha256(RequiredText(ocrModel, "sha256", configurationPath), configurationPath)
                : null,
            requireRecovery
                ? RequiredFile(bundleRoot, RequiredText(root, "magikaExecutable", configurationPath), "Magika", MagikaUrl)
                : null,
            requireRecovery
                ? RequiredFile(bundleRoot, RequiredText(root, "flossExecutable", configurationPath), "FLOSS", FlossUrl)
                : null,
            requireTranslation
                ? RequiredFile(bundleRoot, RequiredText(root, "llamaServer", configurationPath), "llama.cpp server", LlamaUrl)
                : null,
            requireTranslation
                ? RequiredFile(bundleRoot, RequiredText(model, "path", configurationPath), "offline translation model", ModelUrl)
                : null,
            requireTranslation ? RequiredText(model, "id", configurationPath) : null,
            requireTranslation ? RequiredText(model, "revision", configurationPath) : null,
            requireTranslation
                ? ValidateSha256(RequiredText(model, "sha256", configurationPath), configurationPath)
                : null
        );
    }

    private static IEnumerable<string> CandidateRoots(
        string? explicitRoot,
        bool requireExplicitBundle
    )
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<string?> initial =
        [
            explicitRoot,
            Environment.GetEnvironmentVariable("BSTRINGS_AIRGAP_BUNDLE"),
        ];
        foreach (var candidate in initial)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                var fullPath = Path.GetFullPath(candidate);
                if (!Directory.Exists(fullPath))
                {
                    throw new DirectoryNotFoundException(
                        $"The configured bstrings bundle directory was not found: '{fullPath}'. "
                            + OfflineBundleAcquisition
                    );
                }
                if (yielded.Add(fullPath))
                {
                    yield return fullPath;
                }
            }
        }

        if (requireExplicitBundle)
        {
            if (yielded.Count == 0)
            {
                throw new DirectoryNotFoundException(
                    "--airgap requires --bundle-root or BSTRINGS_AIRGAP_BUNDLE. "
                        + OfflineBundleAcquisition
                );
            }
            yield break;
        }

        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; cursor is not null && depth < 8; depth++, cursor = cursor.Parent)
        {
            if (yielded.Add(cursor.FullName))
            {
                yield return cursor.FullName;
            }
        }
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string property,
        string configurationPath
    )
    {
        if (
            !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Object
        )
        {
            throw new InvalidDataException(
                $"'{configurationPath}' is missing object '{property}'."
            );
        }
        return value;
    }

    private static string RequiredText(
        JsonElement parent,
        string property,
        string configurationPath
    )
    {
        if (
            !parent.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString())
        )
        {
            throw new InvalidDataException(
                $"'{configurationPath}' is missing text field '{property}'."
            );
        }
        return value.GetString()!;
    }

    private static string RequiredFile(
        string bundleRoot,
        string relativePath,
        string component,
        string acquisitionUrl
    )
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException(
                $"The {component} path in {ConfigurationFileName} must be relative to the bundle."
            );
        }
        var fullRoot = Path.GetFullPath(bundleRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar
        );
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"The {component} path escapes the configured bundle directory."
            );
        }
        RejectReparsePoints(fullRoot, resolved, component);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException(
                $"Bundled {component} was not found at '{resolved}'. Acquisition instructions: {acquisitionUrl}",
                resolved
            );
        }
        return resolved;
    }

    private static void RejectReparsePoints(string bundleRoot, string resolvedPath, string component)
    {
        var cursor = bundleRoot;
        if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"The configured bundle root is a linked or reparse-point path; refusing {component}."
            );
        }
        foreach (var segment in Path.GetRelativePath(bundleRoot, resolvedPath).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        ))
        {
            cursor = Path.Combine(cursor, segment);
            if (
                (File.Exists(cursor) || Directory.Exists(cursor))
                && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0
            )
            {
                throw new InvalidDataException(
                    $"The configured {component} path contains a link or reparse point: '{cursor}'."
                );
            }
        }
    }

    private static string ValidateSha256(string value, string configurationPath)
    {
        if (
            value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character))
        )
        {
            throw new InvalidDataException(
                $"'{configurationPath}' contains an invalid model SHA-256."
            );
        }
        return value.ToLowerInvariant();
    }

    private static string Sha256File(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan
        );
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
