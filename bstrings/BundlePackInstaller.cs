#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal enum BundlePackKind
{
    Zip,
    File,
}

internal sealed record BundlePackDefinition(
    string Id,
    Uri Url,
    long Bytes,
    string Sha256,
    BundlePackKind Kind = BundlePackKind.Zip,
    string? Target = null
);

internal sealed record BundlePackTrustManifest(
    string ManifestPath,
    string Profile,
    string BundleIdentity,
    string AirgapManifestSha256,
    IReadOnlyList<BundlePackDefinition> Packs
);

internal sealed record BundlePackInstallationResult(
    string Profile,
    string BundleIdentity,
    string OutputDirectory,
    string CacheDirectory,
    long FileCount,
    long TotalBytes,
    string AirgapManifestSha256
);

internal static class BundlePackInstaller
{
    internal const string PackManifestFileName = "bundle-packs.json";
    internal const long MaximumPackBytes = 2_000_000_000;
    internal const long MaximumFilePackBytes = 64L * 1024 * 1024 * 1024;
    internal const int MaximumAssemblyEntries = 250_000;
    internal const long MaximumExpandedBundleBytes = 256L * 1024 * 1024 * 1024;

    private const int SupportedSchemaVersion = 1;
    private const int MaximumPackCount = 128;
    private const int MaximumRedirects = 8;
    private const long MaximumTrustManifestBytes = 4L * 1024 * 1024;
    private const int CopyBufferBytes = 1024 * 1024;
    private const string ContentStoreSchema = "v1";
    private const string ContentStoreDirectoryName = "objects";
    private const string PartialStoreDirectoryName = "partials";
    private static readonly TimeSpan ObjectLeaseTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ObjectLeasePollInterval = TimeSpan.FromMilliseconds(200);

    internal static string DefaultManifestPath =>
        Path.Combine(AppContext.BaseDirectory, PackManifestFileName);

    internal static async Task<BundlePackInstallationResult> AcquireAndAssembleAsync(
        string? manifestPath,
        string? cacheDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        HttpMessageHandler? messageHandler = null,
        Action<string, long, long>? progress = null,
        string? seedBundleDirectory = null
    )
    {
        var manifest = ReadTrustManifest(manifestPath);
        var cache = ResolveCacheDirectory(manifest, cacheDirectory);
        var output = NormalizeAbsentOutput(outputDirectory);
        ValidateCacheDoesNotCreateOutput(cache, output);
        EnsurePhysicalDirectory(
            Path.GetDirectoryName(output)
                ?? throw new ArgumentException("Bundle output must have a parent directory."),
            "bundle output parent"
        );
        EnsurePhysicalDirectory(cache, "bundle pack cache");
        var seedBundle = ResolveSeedBundleDirectory(seedBundleDirectory);
        var invocationDirectory = CreateInvocationDirectory(cache);

        try
        {
            var ownsHandler = messageHandler is null;
            messageHandler ??= CreateDefaultHandler();
            using var client = new HttpClient(messageHandler, disposeHandler: ownsHandler)
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            foreach (var pack in manifest.Packs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await AcquirePackAsync(
                    client,
                    cache,
                    invocationDirectory,
                    seedBundle,
                    pack,
                    cancellationToken,
                    progress
                );
            }

            return AssembleCore(manifest, cache, output, cancellationToken, progress);
        }
        finally
        {
            DeleteInvocationDirectory(invocationDirectory);
        }
    }

    internal static BundlePackInstallationResult Assemble(
        string? manifestPath,
        string? cacheDirectory,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        Action<string, long, long>? progress = null
    )
    {
        var manifest = ReadTrustManifest(manifestPath);
        var cache = ResolveCacheDirectory(manifest, cacheDirectory);
        var output = NormalizeAbsentOutput(outputDirectory);
        ValidateCacheDoesNotCreateOutput(cache, output);
        if (!Directory.Exists(cache))
        {
            throw new DirectoryNotFoundException(
                $"Bundle pack cache directory was not found: '{cache}'."
            );
        }
        RejectExistingReparsePoints(cache, "bundle pack cache");
        var invocationDirectory = CreateInvocationDirectory(cache);
        try
        {
            ImportLegacyPacksForAssembly(
                manifest,
                cache,
                invocationDirectory,
                cancellationToken,
                progress
            );
            return AssembleCore(manifest, cache, output, cancellationToken, progress);
        }
        finally
        {
            DeleteInvocationDirectory(invocationDirectory);
        }
    }

    internal static BundlePackTrustManifest ReadTrustManifest(string? manifestPath)
    {
        var fullPath = Path.GetFullPath(manifestPath ?? DefaultManifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Bundle pack trust manifest was not found at '{fullPath}'.",
                fullPath
            );
        }
        RejectExistingReparsePoints(fullPath, "bundle pack trust manifest");
        var attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InvalidDataException("Bundle pack trust manifest is not a regular file.");
        }

        byte[] bytes;
        using (
            var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan
            )
        )
        {
            if (stream.Length == 0 || stream.Length > MaximumTrustManifestBytes)
            {
                throw new InvalidDataException(
                    $"Bundle pack trust manifest length must be between 1 and {MaximumTrustManifestBytes:N0} bytes."
                );
            }
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.Length != bytes.Length)
            {
                throw new InvalidDataException(
                    "Bundle pack trust manifest changed length while it was being read."
                );
            }
        }

        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12,
            }
        );
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Bundle pack trust manifest root must be an object.");
        }

        var schemaVersion = -1;
        string? profile = null;
        string? bundleIdentity = null;
        string? airgapManifestSha256 = null;
        JsonElement packsElement = default;
        var rootProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            RejectDuplicateProperty(rootProperties, property.Name, "root");
            switch (property.Name)
            {
                case "schemaVersion":
                    if (
                        property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out schemaVersion)
                    )
                    {
                        throw new InvalidDataException(
                            "Bundle pack trust manifest schemaVersion must be an integer."
                        );
                    }
                    break;
                case "profile":
                    profile = RequireText(property.Value, "profile");
                    break;
                case "bundleIdentity":
                    bundleIdentity = RequireText(property.Value, "bundleIdentity");
                    break;
                case "airgapManifestSha256":
                    airgapManifestSha256 = RequireText(
                        property.Value,
                        "airgapManifestSha256"
                    );
                    break;
                case "packs":
                    packsElement = property.Value;
                    break;
                default:
                    throw new InvalidDataException(
                        $"Bundle pack trust manifest contains unknown root property '{property.Name}'."
                    );
            }
        }

        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported or missing bundle pack schema version '{schemaVersion}'."
            );
        }
        profile = ValidateIdentifier(profile, "profile", 128);
        bundleIdentity = ValidateIdentifier(bundleIdentity, "bundleIdentity", 256);
        airgapManifestSha256 = ValidateLowercaseSha256(
            airgapManifestSha256,
            "airgapManifestSha256"
        );
        if (
            packsElement.ValueKind != JsonValueKind.Array
            || packsElement.GetArrayLength() is < 1 or > MaximumPackCount
        )
        {
            throw new InvalidDataException(
                $"Bundle pack trust manifest must contain between 1 and {MaximumPackCount} packs."
            );
        }

        var packs = new List<BundlePackDefinition>(packsElement.GetArrayLength());
        var packIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in packsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Bundle pack entry {index} must be an object."
                );
            }

            string? id = null;
            string? urlText = null;
            string? sha256 = null;
            string? kindText = null;
            string? target = null;
            long length = -1;
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                RejectDuplicateProperty(properties, property.Name, $"pack entry {index}");
                switch (property.Name)
                {
                    case "id":
                        id = RequireText(property.Value, $"pack entry {index} id");
                        break;
                    case "url":
                        urlText = RequireText(property.Value, $"pack entry {index} url");
                        break;
                    case "kind":
                        kindText = RequireText(property.Value, $"pack entry {index} kind");
                        break;
                    case "target":
                        target = RequireText(property.Value, $"pack entry {index} target");
                        break;
                    case "bytes":
                        if (
                            property.Value.ValueKind != JsonValueKind.Number
                            || !property.Value.TryGetInt64(out length)
                        )
                        {
                            throw new InvalidDataException(
                                $"Bundle pack entry {index} bytes must be an integer."
                            );
                        }
                        break;
                    case "sha256":
                        sha256 = RequireText(
                            property.Value,
                            $"pack entry {index} sha256"
                        );
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Bundle pack entry {index} contains unknown property '{property.Name}'."
                        );
                }
            }

            id = ValidateIdentifier(id, $"pack entry {index} id", 128);
            if (!packIds.Add(id))
            {
                throw new InvalidDataException(
                    $"Bundle pack trust manifest contains duplicate pack id '{id}'."
                );
            }
            if (length < 0)
            {
                throw new InvalidDataException($"Bundle pack entry {index} is missing bytes.");
            }
            var kind = ParsePackKind(kindText, index);
            if (kind == BundlePackKind.Zip)
            {
                if (length <= 0 || length >= MaximumPackBytes)
                {
                    throw new InvalidDataException(
                        $"ZIP bundle pack entry {index} bytes must be an integer from 1 through {MaximumPackBytes - 1:N0}."
                    );
                }
                if (target is not null)
                {
                    throw new InvalidDataException(
                        $"ZIP bundle pack entry {index} must not contain target."
                    );
                }
            }
            else
            {
                if (length <= 0 || length > MaximumFilePackBytes)
                {
                    throw new InvalidDataException(
                        $"File bundle pack entry {index} bytes must be an integer from 1 through {MaximumFilePackBytes:N0}."
                    );
                }
                target = ValidateFileTarget(target, index);
            }
            sha256 = ValidateLowercaseSha256(sha256, $"pack entry {index} sha256");
            var url = ValidateHttpsUri(urlText, $"pack entry {index}");
            packs.Add(new BundlePackDefinition(id, url, length, sha256, kind, target));
            index++;
        }

        return new BundlePackTrustManifest(
            fullPath,
            profile,
            bundleIdentity,
            airgapManifestSha256,
            packs
        );
    }

    private static async Task AcquirePackAsync(
        HttpClient client,
        string cacheDirectory,
        string invocationDirectory,
        string? seedBundleDirectory,
        BundlePackDefinition pack,
        CancellationToken cancellationToken,
        Action<string, long, long>? progress
    )
    {
        var objectPath = ContentObjectPath(cacheDirectory, pack);
        EnsureContentObjectParent(cacheDirectory, objectPath);
        ValidateCacheEntry(objectPath, "cached bundle pack object");
        if (File.Exists(objectPath))
        {
            RejectHardLinkedPath(objectPath, $"cached bundle pack object '{pack.Id}'");
            if (TryVerifyPack(objectPath, pack, progress))
            {
                return;
            }
        }

        await using var lease = await AcquireObjectLeaseAsync(
            ObjectLeasePath(objectPath),
            cancellationToken
        );
        if (
            TryUseOrQuarantineContentObject(
                objectPath,
                invocationDirectory,
                pack,
                progress
            )
        )
        {
            return;
        }

        if (
            TryImportVerifiedSource(
                LegacyPackPath(cacheDirectory, pack),
                "legacy cached bundle pack",
                objectPath,
                invocationDirectory,
                pack,
                cancellationToken,
                progress
            )
        )
        {
            return;
        }
        if (
            pack.Kind == BundlePackKind.File
            && seedBundleDirectory is not null
            && TryImportVerifiedSource(
                ResolveSeedPackPath(seedBundleDirectory, pack),
                "seed bundle file pack",
                objectPath,
                invocationDirectory,
                pack,
                cancellationToken,
                progress
            )
        )
        {
            return;
        }

        var parkedPartialPath = ParkedPartialPath(objectPath);
        var offset = InspectParkedPartial(parkedPartialPath, pack, progress);
        try
        {
            if (offset == pack.Bytes)
            {
                PublishVerifiedObject(
                    parkedPartialPath,
                    objectPath,
                    invocationDirectory,
                    pack,
                    progress
                );
                return;
            }
            await DownloadPackOnceAsync(
                client,
                pack,
                parkedPartialPath,
                offset,
                cancellationToken,
                progress
            );
            if (!TryVerifyPack(parkedPartialPath, pack, progress))
            {
                if (offset == 0)
                {
                    File.Delete(parkedPartialPath);
                    throw new InvalidDataException(
                        $"Downloaded bundle pack '{pack.Id}' failed its exact SHA-256 verification."
                    );
                }

                File.Delete(parkedPartialPath);
                await DownloadPackOnceAsync(
                    client,
                    pack,
                    parkedPartialPath,
                    offset: 0,
                    cancellationToken,
                    progress
                );
                try
                {
                    VerifyPack(parkedPartialPath, pack, progress);
                }
                catch
                {
                    File.Delete(parkedPartialPath);
                    throw;
                }
            }
            PublishVerifiedObject(
                parkedPartialPath,
                objectPath,
                invocationDirectory,
                pack,
                progress
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' could not be acquired because its HTTPS request failed.",
                ex
            );
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' could not be acquired and verified safely: {ex.Message}",
                ex
            );
        }
        finally
        {
            NormalizeParkedPartial(parkedPartialPath, pack, progress);
        }
    }

    private static async Task DownloadPackOnceAsync(
        HttpClient client,
        BundlePackDefinition pack,
        string partialPath,
        long offset,
        CancellationToken cancellationToken,
        Action<string, long, long>? progress
    )
    {
        using var response = await SendWithSafeRedirectsAsync(
            client,
            pack,
            offset,
            cancellationToken
        );
        long writeOffset;
        if (response.StatusCode == HttpStatusCode.OK)
        {
            writeOffset = 0;
        }
        else if (response.StatusCode == HttpStatusCode.PartialContent && offset > 0)
        {
            writeOffset = offset;
            ValidateContentRange(response.Content.Headers.ContentRange, pack, offset);
        }
        else
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' returned HTTP status {(int)response.StatusCode}; expected 200 or a valid resumed 206 response."
            );
        }

        var expectedResponseBytes = pack.Bytes - writeOffset;
        var contentLength = response.Content.Headers.ContentLength;
        if (
            contentLength is null
            || contentLength <= 0
            || contentLength != expectedResponseBytes
        )
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' returned an invalid Content-Length."
            );
        }
        if (
            response.Content.Headers.ContentEncoding.Any(encoding =>
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' returned an encoded body despite the identity-only request."
            );
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            partialPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
        RejectHardLinkedFile(output, $"partial bundle pack '{pack.Id}'");
        if (writeOffset == 0)
        {
            output.SetLength(0);
        }
        else if (output.Length != writeOffset)
        {
            throw new InvalidDataException(
                $"Partial bundle pack '{pack.Id}' changed before its resumed download began."
            );
        }
        output.Position = writeOffset;
        progress?.Invoke($"bundle download ({pack.Id})", writeOffset, pack.Bytes);

        var buffer = new byte[CopyBufferBytes];
        var remaining = expectedResponseBytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"Bundle pack '{pack.Id}' response ended before its declared length."
                );
            }
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
            progress?.Invoke(
                $"bundle download ({pack.Id})",
                checked(pack.Bytes - remaining),
                pack.Bytes
            );
        }
        if (await input.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 0)
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' response exceeded its declared length."
            );
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(flushToDisk: true);
        if (output.Length != pack.Bytes)
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' partial file has an unexpected final length."
            );
        }
    }

    private static async Task<HttpResponseMessage> SendWithSafeRedirectsAsync(
        HttpClient client,
        BundlePackDefinition pack,
        long offset,
        CancellationToken cancellationToken
    )
    {
        var current = pack.Url;
        for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
            }

            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            if (!IsRedirect(response.StatusCode))
            {
                return response;
            }
            if (redirect == MaximumRedirects)
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"Bundle pack '{pack.Id}' exceeded the redirect safety limit."
                );
            }

            var location = response.Headers.Location;
            response.Dispose();
            if (location is null)
            {
                throw new InvalidDataException(
                    $"Bundle pack '{pack.Id}' returned a redirect without a destination."
                );
            }
            try
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
            }
            catch (UriFormatException ex)
            {
                throw new InvalidDataException(
                    $"Bundle pack '{pack.Id}' returned an invalid redirect destination.",
                    ex
                );
            }
            ValidateHttpsUri(current, $"redirect for pack '{pack.Id}'");
        }
        throw new InvalidOperationException("Unreachable redirect state.");
    }

    private static BundlePackInstallationResult AssembleCore(
        BundlePackTrustManifest manifest,
        string cacheDirectory,
        string outputDirectory,
        CancellationToken cancellationToken,
        Action<string, long, long>? progress
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(cacheDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Bundle pack cache directory was not found: '{cacheDirectory}'."
            );
        }
        RejectExistingReparsePoints(cacheDirectory, "bundle pack cache");
        NormalizeAbsentOutput(outputDirectory);
        var parent = Path.GetDirectoryName(outputDirectory)
            ?? throw new ArgumentException("Bundle output must have a parent directory.");
        EnsurePhysicalDirectory(parent, "bundle output parent");

        var outputName = Path.GetFileName(outputDirectory);
        var temporaryDirectory = Path.Combine(
            parent,
            $".{outputName}.assemble.{Guid.NewGuid():N}.partial"
        );
        if (Path.Exists(temporaryDirectory) || IsLinkOrReparsePoint(temporaryDirectory))
        {
            throw new IOException("Fresh bundle assembly workspace already exists.");
        }
        var temporaryExists = false;
        try
        {
            using (var verifiedPacks = OpenVerifiedPacks(
                manifest,
                cacheDirectory,
                cancellationToken,
                progress
            ))
            {
                var plans = ReadAndValidateAssemblyPlans(
                    verifiedPacks.Handles,
                    manifest.AirgapManifestSha256,
                    cancellationToken
                );
                Directory.CreateDirectory(temporaryDirectory);
                temporaryExists = true;
                RejectExistingReparsePoints(
                    temporaryDirectory,
                    "bundle assembly workspace"
                );
                var assemblyBytes = checked(
                    plans.ZipEntries.Where(plan => !plan.IsDirectory).Sum(plan => plan.Entry.Length)
                        + plans.Files.Sum(plan => plan.Handle.Definition.Bytes)
                );
                var assemblyTotal = Math.Max(1, assemblyBytes);
                long assembledBytes = 0;
                progress?.Invoke("bundle assembly", 0, assemblyTotal);
                ExtractPlans(
                    plans.ZipEntries,
                    temporaryDirectory,
                    cancellationToken,
                    bytes =>
                    {
                        assembledBytes = checked(assembledBytes + bytes);
                        progress?.Invoke("bundle assembly", assembledBytes, assemblyTotal);
                    }
                );
                StageFilePlans(
                    plans.Files,
                    temporaryDirectory,
                    cancellationToken,
                    bytes =>
                    {
                        assembledBytes = checked(assembledBytes + bytes);
                        progress?.Invoke("bundle assembly", assembledBytes, assemblyTotal);
                    }
                );
                progress?.Invoke("bundle assembly", assemblyTotal, assemblyTotal);
            }

            var verification = BundleManifestVerifier.Verify(
                temporaryDirectory,
                progress: (completed, total) =>
                    progress?.Invoke("bundle verification", completed, total)
            );
            if (!HashesEqual(verification.ManifestSha256, manifest.AirgapManifestSha256))
            {
                throw new InvalidDataException(
                    "Assembled airgap-manifest.json does not match the trusted final manifest SHA-256."
                );
            }

            NormalizeAbsentOutput(outputDirectory);
            Directory.Move(temporaryDirectory, outputDirectory);
            temporaryExists = false;
            return new BundlePackInstallationResult(
                manifest.Profile,
                manifest.BundleIdentity,
                outputDirectory,
                cacheDirectory,
                verification.FileCount,
                verification.TotalBytes,
                verification.ManifestSha256
            );
        }
        finally
        {
            if (temporaryExists && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }
    }

    private static VerifiedPackCollection OpenVerifiedPacks(
        BundlePackTrustManifest manifest,
        string cacheDirectory,
        CancellationToken cancellationToken,
        Action<string, long, long>? progress
    )
    {
        var handles = new List<VerifiedPackHandle>(manifest.Packs.Count);
        try
        {
            foreach (var pack in manifest.Packs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = ContentObjectPath(cacheDirectory, pack);
                ValidateCacheEntry(path, "cached bundle pack object");
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"Verified bundle pack object '{pack.Id}' is missing from the cache.",
                        path
                    );
                }
                var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    CopyBufferBytes,
                    FileOptions.SequentialScan
                );
                try
                {
                    VerifyPack(stream, pack, progress);
                    handles.Add(new VerifiedPackHandle(pack, stream));
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            return new VerifiedPackCollection(handles);
        }
        catch
        {
            foreach (var handle in handles)
            {
                handle.Dispose();
            }
            throw;
        }
    }

    private static AssemblyPlans ReadAndValidateAssemblyPlans(
        IReadOnlyList<VerifiedPackHandle> handles,
        string trustedManifestSha256,
        CancellationToken cancellationToken
    )
    {
        var zipPlans = new List<ZipEntryPlan>();
        var filePlans = new List<FileEntryPlan>();
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;
        var expandedBytes = 0L;
        foreach (var handle in handles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (handle.Definition.Kind == BundlePackKind.File)
            {
                var target = handle.Definition.Target
                    ?? throw new InvalidDataException(
                        $"File bundle pack '{handle.Definition.Id}' has no target."
                    );
                if (!paths.TryAdd(target, false))
                {
                    throw new InvalidDataException(
                        $"Bundle packs contain a case-insensitive duplicate path '{target}'."
                    );
                }
                RegisterAssemblyEntry(
                    ref entryCount,
                    ref expandedBytes,
                    handle.Definition.Bytes
                );
                filePlans.Add(new FileEntryPlan(handle, target));
                continue;
            }

            handle.OpenArchive();
            foreach (var entry in handle.Archive!.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var plan = CreateZipPlan(handle, entry);
                if (!paths.TryAdd(plan.RelativePath, plan.IsDirectory))
                {
                    throw new InvalidDataException(
                        $"Bundle packs contain a case-insensitive duplicate path '{plan.RelativePath}'."
                    );
                }
                RegisterAssemblyEntry(
                    ref entryCount,
                    ref expandedBytes,
                    plan.IsDirectory ? 0 : plan.Entry.Length
                );
                zipPlans.Add(plan);
            }
        }

        foreach (var path in paths)
        {
            var cursor = path.Key.IndexOf('/');
            while (cursor >= 0)
            {
                var ancestor = path.Key[..cursor];
                if (paths.TryGetValue(ancestor, out var ancestorIsDirectory) && !ancestorIsDirectory)
                {
                    throw new InvalidDataException(
                        $"Bundle packs contain a file/directory collision at '{ancestor}'."
                    );
                }
                cursor = path.Key.IndexOf('/', cursor + 1);
            }
        }

        var manifestZipCandidates = zipPlans
            .Where(plan =>
                !plan.IsDirectory
                && string.Equals(
                    plan.RelativePath,
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        var manifestFileCandidates = filePlans
            .Where(plan =>
                string.Equals(
                    plan.RelativePath,
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        if (
            manifestZipCandidates.Length + manifestFileCandidates.Length != 1
            || manifestZipCandidates.Any(plan =>
                !string.Equals(
                    plan.RelativePath,
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.Ordinal
                )
            )
            || manifestFileCandidates.Any(plan =>
                !string.Equals(
                    plan.RelativePath,
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.Ordinal
                )
            )
        )
        {
            throw new InvalidDataException(
                $"Verified bundle packs must contain exactly one canonical '{BundleManifestVerifier.ManifestFileName}'."
            );
        }

        BundleManifestVerifier.ParsedManifest finalManifest;
        if (manifestZipCandidates.Length == 1)
        {
            using var manifestStream = manifestZipCandidates[0].Entry.Open();
            finalManifest = BundleManifestVerifier.ReadManifest(
                manifestStream,
                manifestZipCandidates[0].Entry.Length
            );
        }
        else
        {
            var manifestHandle = manifestFileCandidates[0].Handle;
            manifestHandle.Stream.Position = 0;
            finalManifest = BundleManifestVerifier.ReadManifest(
                manifestHandle.Stream,
                manifestHandle.Definition.Bytes
            );
            manifestHandle.Stream.Position = 0;
        }
        if (!HashesEqual(finalManifest.Sha256, trustedManifestSha256))
        {
            throw new InvalidDataException(
                "Packed airgap-manifest.json does not match the trusted final manifest SHA-256."
            );
        }
        ValidatePlansAgainstFinalManifest(zipPlans, filePlans, finalManifest);
        return new AssemblyPlans(zipPlans, filePlans);
    }

    private static void RegisterAssemblyEntry(
        ref int entryCount,
        ref long expandedBytes,
        long length
    )
    {
        if (length < 0)
        {
            throw new InvalidDataException(
                "Bundle pack contains an entry with a negative expanded length."
            );
        }
        entryCount = checked(entryCount + 1);
        if (entryCount > MaximumAssemblyEntries)
        {
            throw new InvalidDataException(
                $"Bundle packs exceed the {MaximumAssemblyEntries:N0}-entry assembly safety limit."
            );
        }
        try
        {
            expandedBytes = checked(expandedBytes + length);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException(
                "Bundle pack expanded byte total exceeds the supported range.",
                ex
            );
        }
        if (expandedBytes > MaximumExpandedBundleBytes)
        {
            throw new InvalidDataException(
                $"Bundle packs exceed the {MaximumExpandedBundleBytes:N0}-byte expanded assembly safety limit."
            );
        }
    }

    private static void ValidatePlansAgainstFinalManifest(
        IReadOnlyList<ZipEntryPlan> zipPlans,
        IReadOnlyList<FileEntryPlan> filePlans,
        BundleManifestVerifier.ParsedManifest finalManifest
    )
    {
        if (finalManifest.Files.Count > MaximumAssemblyEntries)
        {
            throw new InvalidDataException(
                $"Final bundle manifest exceeds the {MaximumAssemblyEntries:N0}-file assembly safety limit."
            );
        }

        var remaining = new HashSet<string>(
            finalManifest.Files.Keys,
            StringComparer.OrdinalIgnoreCase
        );
        var declaredBytes = 0L;
        foreach (var expected in finalManifest.Files.Values)
        {
            try
            {
                declaredBytes = checked(declaredBytes + expected.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException(
                    "Final bundle manifest byte total exceeds the supported range.",
                    ex
                );
            }
            if (declaredBytes > MaximumExpandedBundleBytes)
            {
                throw new InvalidDataException(
                    $"Final bundle manifest exceeds the {MaximumExpandedBundleBytes:N0}-byte expanded assembly safety limit."
                );
            }
        }

        foreach (var plan in zipPlans)
        {
            if (
                plan.IsDirectory
                || string.Equals(
                    plan.RelativePath,
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            ValidatePlannedFile(
                plan.RelativePath,
                plan.Entry.Length,
                finalManifest.Files,
                remaining
            );
        }
        foreach (var plan in filePlans)
        {
            if (
                string.Equals(
                    plan.RelativePath,
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.Ordinal
                )
            )
            {
                continue;
            }
            ValidatePlannedFile(
                plan.RelativePath,
                plan.Handle.Definition.Bytes,
                finalManifest.Files,
                remaining
            );
        }
        if (remaining.Count != 0)
        {
            var missing = remaining.OrderBy(path => path, StringComparer.Ordinal).Take(5);
            throw new InvalidDataException(
                "Verified bundle packs are missing files declared by the final manifest: "
                    + $"[{string.Join(", ", missing)}]."
            );
        }
    }

    private static void ValidatePlannedFile(
        string relativePath,
        long length,
        IReadOnlyDictionary<string, BundleManifestVerifier.ExpectedFile> expectedFiles,
        HashSet<string> remaining
    )
    {
        if (!expectedFiles.TryGetValue(relativePath, out var expected))
        {
            throw new InvalidDataException(
                $"Verified bundle packs contain '{relativePath}', which is not declared by the final manifest."
            );
        }
        if (length != expected.Length)
        {
            throw new InvalidDataException(
                $"Packed file length differs from the final manifest for '{relativePath}': "
                    + $"expected {expected.Length}, got {length}."
            );
        }
        if (!remaining.Remove(relativePath))
        {
            throw new InvalidDataException(
                $"Verified bundle packs contain duplicate content for '{relativePath}'."
            );
        }
    }

    private static ZipEntryPlan CreateZipPlan(
        VerifiedPackHandle handle,
        ZipArchiveEntry entry
    )
    {
        var fullName = entry.FullName;
        var hasDirectorySuffix = fullName.EndsWith("/", StringComparison.Ordinal);
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windowsAttributes = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        if (
            unixType == 0xA000
            || (windowsAttributes & FileAttributes.ReparsePoint) != 0
            || unixType is not (0 or 0x4000 or 0x8000)
        )
        {
            throw new InvalidDataException(
                $"Bundle pack '{handle.Definition.Id}' contains a link or special entry."
            );
        }
        var attributesSayDirectory =
            unixType == 0x4000 || (windowsAttributes & FileAttributes.Directory) != 0;
        if (attributesSayDirectory && !hasDirectorySuffix)
        {
            throw new InvalidDataException(
                $"Bundle pack '{handle.Definition.Id}' contains an ambiguous directory entry."
            );
        }

        var relativePath = hasDirectorySuffix ? fullName[..^1] : fullName;
        relativePath = ValidateZipRelativePath(relativePath);
        if (hasDirectorySuffix && entry.Length != 0)
        {
            throw new InvalidDataException(
                $"Bundle pack '{handle.Definition.Id}' contains a non-empty directory entry."
            );
        }
        return new ZipEntryPlan(handle, entry, relativePath, hasDirectorySuffix);
    }

    private static void ExtractPlans(
        IReadOnlyList<ZipEntryPlan> plans,
        string temporaryDirectory,
        CancellationToken cancellationToken,
        Action<long>? progress
    )
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(temporaryDirectory)
            ) + Path.DirectorySeparatorChar;
        var buffer = new byte[CopyBufferBytes];
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(
                Path.Combine(
                    temporaryDirectory,
                    plan.RelativePath.Replace('/', Path.DirectorySeparatorChar)
                )
            );
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Bundle pack entry escaped the temporary output: '{plan.RelativePath}'."
                );
            }
            if (plan.IsDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = plan.Entry.Open();
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.SequentialScan
            );
            var remaining = plan.Entry.Length;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"Bundle pack entry '{plan.RelativePath}' ended before its declared length."
                    );
                }
                output.Write(buffer, 0, read);
                remaining -= read;
                progress?.Invoke(read);
            }
            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"Bundle pack entry '{plan.RelativePath}' exceeded its declared length."
                );
            }
            output.Flush(flushToDisk: true);
            if (output.Length != plan.Entry.Length)
            {
                throw new InvalidDataException(
                    $"Bundle pack entry '{plan.RelativePath}' changed length during extraction."
                );
            }
        }
    }

    private static void StageFilePlans(
        IReadOnlyList<FileEntryPlan> plans,
        string temporaryDirectory,
        CancellationToken cancellationToken,
        Action<long>? progress
    )
    {
        var rootPrefix = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(temporaryDirectory)
            ) + Path.DirectorySeparatorChar;
        var buffer = new byte[CopyBufferBytes];
        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = Path.GetFullPath(
                Path.Combine(
                    temporaryDirectory,
                    plan.RelativePath.Replace('/', Path.DirectorySeparatorChar)
                )
            );
            if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"File bundle pack target escaped the temporary output: '{plan.RelativePath}'."
                );
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var input = plan.Handle.Stream;
            input.Position = 0;
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferBytes,
                FileOptions.SequentialScan
            );
            var remaining = plan.Handle.Definition.Bytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                {
                    throw new InvalidDataException(
                        $"File bundle pack '{plan.Handle.Definition.Id}' ended before its verified length."
                    );
                }
                output.Write(buffer, 0, read);
                remaining -= read;
                progress?.Invoke(read);
            }
            if (input.ReadByte() != -1)
            {
                throw new InvalidDataException(
                    $"File bundle pack '{plan.Handle.Definition.Id}' exceeded its verified length."
                );
            }
            output.Flush(flushToDisk: true);
            if (output.Length != plan.Handle.Definition.Bytes)
            {
                throw new InvalidDataException(
                    $"File bundle pack '{plan.Handle.Definition.Id}' changed length while it was staged."
                );
            }
            input.Position = 0;
        }
    }

    private static string ResolveCacheDirectory(
        BundlePackTrustManifest manifest,
        string? cacheDirectory
    )
    {
        var path = cacheDirectory;
        if (string.IsNullOrWhiteSpace(path))
        {
            var manifestDirectory = Path.GetDirectoryName(manifest.ManifestPath)!;
            path = Path.Combine(manifestDirectory, "bundle-pack-cache", manifest.Profile);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static string? ResolveSeedBundleDirectory(string? seedBundleDirectory)
    {
        if (string.IsNullOrWhiteSpace(seedBundleDirectory))
        {
            return null;
        }
        var path = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(seedBundleDirectory)
        );
        if (IsLinkOrReparsePoint(path))
        {
            throw new InvalidDataException(
                "The seed bundle directory is a link or reparse point."
            );
        }
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"Seed bundle directory was not found: '{path}'."
            );
        }
        RejectExistingReparsePoints(path, "seed bundle directory");
        return path;
    }

    private static string ResolveSeedPackPath(
        string seedBundleDirectory,
        BundlePackDefinition pack
    )
    {
        var target = pack.Target
            ?? throw new InvalidDataException(
                $"File bundle pack '{pack.Id}' has no seed target."
            );
        var path = Path.GetFullPath(
            Path.Combine(
                seedBundleDirectory,
                target.Replace('/', Path.DirectorySeparatorChar)
            )
        );
        EnsurePathWithinRoot(seedBundleDirectory, path, "seed bundle file pack");
        return path;
    }

    private static string CreateInvocationDirectory(string cacheDirectory)
    {
        var partialRoot = Path.Combine(cacheDirectory, PartialStoreDirectoryName);
        EnsurePathWithinRoot(cacheDirectory, partialRoot, "bundle pack partial root");
        EnsurePhysicalDirectory(partialRoot, "bundle pack partial root");
        var invocationDirectory = Path.Combine(partialRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(invocationDirectory);
        RejectExistingReparsePoints(
            invocationDirectory,
            "bundle pack invocation workspace"
        );
        return invocationDirectory;
    }

    private static void DeleteInvocationDirectory(string invocationDirectory)
    {
        if (!Directory.Exists(invocationDirectory))
        {
            return;
        }
        RejectExistingReparsePoints(
            invocationDirectory,
            "bundle pack invocation workspace"
        );
        Directory.Delete(invocationDirectory, recursive: true);
    }

    private static void ImportLegacyPacksForAssembly(
        BundlePackTrustManifest manifest,
        string cacheDirectory,
        string invocationDirectory,
        CancellationToken cancellationToken,
        Action<string, long, long>? progress
    )
    {
        foreach (var pack in manifest.Packs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var objectPath = ContentObjectPath(cacheDirectory, pack);
            EnsureContentObjectParent(cacheDirectory, objectPath);
            ValidateCacheEntry(objectPath, "cached bundle pack object");
            if (File.Exists(objectPath) && TryVerifyPack(objectPath, pack, progress))
            {
                continue;
            }
            using var lease = AcquireObjectLeaseAsync(
                    ObjectLeasePath(objectPath),
                    cancellationToken
                )
                .GetAwaiter()
                .GetResult();
            if (
                TryUseOrQuarantineContentObject(
                    objectPath,
                    invocationDirectory,
                    pack,
                    progress
                )
            )
            {
                continue;
            }
            TryImportVerifiedSource(
                LegacyPackPath(cacheDirectory, pack),
                "legacy cached bundle pack",
                objectPath,
                invocationDirectory,
                pack,
                cancellationToken,
                progress,
                rejectInvalidSource: true
            );
        }
    }

    private static string NormalizeAbsentOutput(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        if (Path.Exists(output) || IsLinkOrReparsePoint(output))
        {
            throw new IOException(
                $"Bundle output already exists; refusing to overwrite '{output}'."
            );
        }
        return output;
    }

    private static void ValidateCacheDoesNotCreateOutput(
        string cacheDirectory,
        string outputDirectory
    )
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            string.Equals(cacheDirectory, outputDirectory, comparison)
            || cacheDirectory.StartsWith(
                outputDirectory + Path.DirectorySeparatorChar,
                comparison
            )
        )
        {
            throw new ArgumentException(
                "The bundle pack cache cannot be equal to or inside the new bundle output."
            );
        }
    }

    private static void EnsurePhysicalDirectory(string path, string description)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        RejectExistingReparsePoints(fullPath, description);
        Directory.CreateDirectory(fullPath);
        RejectExistingReparsePoints(fullPath, description);
    }

    private static void RejectExistingReparsePoints(string path, string description)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidDataException($"The {description} has no filesystem root.");
        var current = root;
        foreach (
            var segment in fullPath[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            current = Path.Combine(current, segment);
            if (IsLinkOrReparsePoint(current))
            {
                throw new InvalidDataException(
                    $"The {description} traverses a link or reparse point."
                );
            }
            if (!Path.Exists(current))
            {
                break;
            }
        }
    }

    private static void ValidateCacheEntry(string path, string description)
    {
        if (IsLinkOrReparsePoint(path))
        {
            throw new InvalidDataException(
                $"The {description} is a link or reparse point."
            );
        }
        if (!Path.Exists(path))
        {
            return;
        }
        RejectExistingReparsePoints(path, description);
        var attributes = File.GetAttributes(path);
        if (
            (attributes & (FileAttributes.Directory | FileAttributes.Device)) != 0
            || !File.Exists(path)
        )
        {
            throw new InvalidDataException($"The {description} is not a regular file.");
        }
    }

    private static bool IsLinkOrReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        return new FileInfo(path).LinkTarget is not null
            || new DirectoryInfo(path).LinkTarget is not null;
    }

    internal static string ContentObjectPath(
        string cacheDirectory,
        BundlePackDefinition pack
    )
    {
        var cache = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        var kind = pack.Kind == BundlePackKind.Zip ? "zip" : "file";
        var path = Path.GetFullPath(
            Path.Combine(
                cache,
                ContentStoreDirectoryName,
                ContentStoreSchema,
                kind,
                pack.Bytes.ToString(CultureInfo.InvariantCulture),
                pack.Sha256[..2],
                pack.Sha256 + ".object"
            )
        );
        EnsurePathWithinRoot(cache, path, "bundle pack content object");
        return path;
    }

    private static string LegacyPackPath(
        string cacheDirectory,
        BundlePackDefinition pack
    ) =>
        Path.Combine(
            cacheDirectory,
            pack.Id + (pack.Kind == BundlePackKind.Zip ? ".zip" : ".file")
        );

    private static string ObjectLeasePath(string objectPath) => objectPath + ".lock";

    private static string ParkedPartialPath(string objectPath) => objectPath + ".partial";

    private static void EnsureContentObjectParent(
        string cacheDirectory,
        string objectPath
    )
    {
        EnsurePathWithinRoot(cacheDirectory, objectPath, "bundle pack content object");
        EnsurePhysicalDirectory(
            Path.GetDirectoryName(objectPath)
                ?? throw new InvalidDataException(
                    "Bundle pack content object has no parent directory."
                ),
            "bundle pack content object parent"
        );
    }

    private static void EnsurePathWithinRoot(
        string rootDirectory,
        string path,
        string description
    )
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        var candidate = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (
            string.Equals(root, candidate, comparison)
            || !candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
        )
        {
            throw new InvalidDataException(
                $"The {description} escapes its bounded physical root."
            );
        }
    }

    private static async Task<FileStream> AcquireObjectLeaseAsync(
        string leasePath,
        CancellationToken cancellationToken
    )
    {
        var timer = Stopwatch.StartNew();
        IOException? lastError = null;
        while (timer.Elapsed < ObjectLeaseTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateCacheEntry(leasePath, "bundle pack object lease");
            try
            {
                var lease = new FileStream(
                    leasePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous
                );
                try
                {
                    ValidateCacheEntry(leasePath, "bundle pack object lease");
                    return lease;
                }
                catch
                {
                    lease.Dispose();
                    throw;
                }
            }
            catch (IOException ex)
            {
                lastError = ex;
                var remaining = ObjectLeaseTimeout - timer.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                await Task.Delay(
                    remaining < ObjectLeasePollInterval
                        ? remaining
                        : ObjectLeasePollInterval,
                    cancellationToken
                );
            }
        }
        throw new IOException(
            $"Timed out waiting for exclusive ownership of bundle pack object lease '{leasePath}'.",
            lastError
        );
    }

    private static bool TryUseOrQuarantineContentObject(
        string objectPath,
        string invocationDirectory,
        BundlePackDefinition pack,
        Action<string, long, long>? progress
    )
    {
        ValidateCacheEntry(objectPath, "cached bundle pack object");
        if (!File.Exists(objectPath))
        {
            return false;
        }
        RejectHardLinkedPath(objectPath, $"cached bundle pack object '{pack.Id}'");
        if (TryVerifyPack(objectPath, pack, progress))
        {
            return true;
        }

        var quarantinePath = Path.Combine(
            invocationDirectory,
            $"quarantine.{Guid.NewGuid():N}.object"
        );
        try
        {
            File.Move(objectPath, quarantinePath, overwrite: false);
            return false;
        }
        catch (IOException ex)
        {
            if (File.Exists(objectPath) && TryVerifyPack(objectPath, pack, progress))
            {
                return true;
            }
            throw new InvalidDataException(
                $"Corrupt bundle pack object '{pack.Id}' could not be quarantined safely.",
                ex
            );
        }
    }

    private static bool TryImportVerifiedSource(
        string sourcePath,
        string sourceDescription,
        string objectPath,
        string invocationDirectory,
        BundlePackDefinition pack,
        CancellationToken cancellationToken,
        Action<string, long, long>? progress,
        bool rejectInvalidSource = false
    )
    {
        ValidateCacheEntry(sourcePath, sourceDescription);
        if (!File.Exists(sourcePath))
        {
            return false;
        }

        var candidatePath = Path.Combine(
            invocationDirectory,
            $"import.{Guid.NewGuid():N}.partial"
        );
        try
        {
            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.SequentialScan
            );
            try
            {
                VerifyPack(source, pack, progress);
            }
            catch (InvalidDataException ex)
            {
                if (rejectInvalidSource)
                {
                    throw new InvalidDataException(
                        $"The {sourceDescription} '{pack.Id}' failed exact verification: {ex.Message}",
                        ex
                    );
                }
                return false;
            }

            using (
                var candidate = new FileStream(
                    candidatePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.SequentialScan
                )
            )
            {
                source.Position = 0;
                CopyExactPack(
                    source,
                    candidate,
                    pack,
                    cancellationToken,
                    progress
                );
                candidate.Flush(flushToDisk: true);
            }
            PublishVerifiedObject(
                candidatePath,
                objectPath,
                invocationDirectory,
                pack,
                progress
            );
            return true;
        }
        catch (InvalidDataException)
        {
            if (rejectInvalidSource)
            {
                throw;
            }
            return false;
        }
        finally
        {
            if (File.Exists(candidatePath))
            {
                File.Delete(candidatePath);
            }
        }
    }

    private static void CopyExactPack(
        FileStream source,
        FileStream destination,
        BundlePackDefinition pack,
        CancellationToken cancellationToken,
        Action<string, long, long>? progress
    )
    {
        var buffer = new byte[CopyBufferBytes];
        var copied = 0L;
        progress?.Invoke($"bundle pack import ({pack.Id})", 0, pack.Bytes);
        while (copied < pack.Bytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = (int)Math.Min(buffer.Length, pack.Bytes - copied);
            var read = source.Read(buffer, 0, requested);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"Bundle pack import source '{pack.Id}' ended before its verified length."
                );
            }
            destination.Write(buffer, 0, read);
            copied = checked(copied + read);
            progress?.Invoke($"bundle pack import ({pack.Id})", copied, pack.Bytes);
        }
        if (source.ReadByte() != -1)
        {
            throw new InvalidDataException(
                $"Bundle pack import source '{pack.Id}' exceeded its verified length."
            );
        }
        if (destination.Length != pack.Bytes)
        {
            throw new InvalidDataException(
                $"Bundle pack import candidate '{pack.Id}' has an unexpected length."
            );
        }
    }

    private static long InspectParkedPartial(
        string parkedPartialPath,
        BundlePackDefinition pack,
        Action<string, long, long>? progress
    )
    {
        ValidateCacheEntry(parkedPartialPath, "parked partial bundle pack object");
        if (!File.Exists(parkedPartialPath))
        {
            return 0;
        }
        var length = new FileInfo(parkedPartialPath).Length;
        if (length <= 0 || length > pack.Bytes)
        {
            File.Delete(parkedPartialPath);
            return 0;
        }
        if (length == pack.Bytes && !TryVerifyPack(parkedPartialPath, pack, progress))
        {
            File.Delete(parkedPartialPath);
            return 0;
        }
        return length;
    }

    private static void NormalizeParkedPartial(
        string parkedPartialPath,
        BundlePackDefinition pack,
        Action<string, long, long>? progress
    )
    {
        if (!File.Exists(parkedPartialPath))
        {
            return;
        }
        ValidateCacheEntry(parkedPartialPath, "parked partial bundle pack object");
        var length = new FileInfo(parkedPartialPath).Length;
        if (
            length <= 0
            || length > pack.Bytes
            || (length == pack.Bytes && !TryVerifyPack(parkedPartialPath, pack, progress))
        )
        {
            File.Delete(parkedPartialPath);
        }
    }

    private static void PublishVerifiedObject(
        string candidatePath,
        string objectPath,
        string invocationDirectory,
        BundlePackDefinition pack,
        Action<string, long, long>? progress
    )
    {
        ValidateCacheEntry(candidatePath, "verified bundle pack publication candidate");
        VerifyPack(candidatePath, pack, progress);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (
                TryUseOrQuarantineContentObject(
                    objectPath,
                    invocationDirectory,
                    pack,
                    progress
                )
            )
            {
                File.Delete(candidatePath);
                return;
            }
            try
            {
                RejectExistingReparsePoints(
                    Path.GetDirectoryName(objectPath)!,
                    "bundle pack content object parent"
                );
                File.Move(candidatePath, objectPath, overwrite: false);
                return;
            }
            catch (IOException) when (File.Exists(objectPath))
            {
            }
        }
        throw new IOException(
            $"Verified bundle pack object '{pack.Id}' could not be published atomically."
        );
    }

    private static bool TryVerifyPack(
        string path,
        BundlePackDefinition pack,
        Action<string, long, long>? progress
    )
    {
        try
        {
            VerifyPack(path, pack, progress);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void VerifyPack(
        string path,
        BundlePackDefinition pack,
        Action<string, long, long>? progress
    )
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.SequentialScan
        );
        VerifyPack(stream, pack, progress);
    }

    private static void VerifyPack(
        FileStream stream,
        BundlePackDefinition pack,
        Action<string, long, long>? progress
    )
    {
        RejectHardLinkedFile(stream, $"bundle pack '{pack.Id}'");
        if (stream.Length != pack.Bytes)
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' size mismatch: expected {pack.Bytes}, got {stream.Length}."
            );
        }
        stream.Position = 0;
        var activity = $"bundle pack verification ({pack.Id})";
        var actual = ProgressHashing.ComputeSha256(
            stream,
            progress is null
                ? null
                : (completed, total) => progress(activity, completed, total)
        );
        if (!HashesEqual(actual, pack.Sha256))
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' SHA-256 mismatch."
            );
        }
        if (stream.Length != pack.Bytes)
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' changed length while it was verified."
            );
        }
        stream.Position = 0;
    }

    private static void RejectHardLinkedFile(FileStream stream, string description)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information))
        {
            throw new InvalidDataException(
                $"Could not inspect the physical link identity of {description}.",
                new Win32Exception(Marshal.GetLastWin32Error())
            );
        }
        if (information.NumberOfLinks != 1)
        {
            throw new InvalidDataException(
                $"The {description} must have exactly one physical filesystem link."
            );
        }
    }

    private static void RejectHardLinkedPath(string path, string description)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1,
            FileOptions.SequentialScan
        );
        RejectHardLinkedFile(stream, description);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        out ByHandleFileInformation information
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private static string ValidateZipRelativePath(string value)
    {
        if (!string.Equals(value, value.Normalize(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Bundle pack contains a non-normalized Unicode path."
            );
        }
        BundleManifestVerifier.ValidateRelativePath("bundle-root/" + value);
        return value;
    }

    private static BundlePackKind ParsePackKind(string? value, int index)
    {
        if (value is null || string.Equals(value, "zip", StringComparison.Ordinal))
        {
            return BundlePackKind.Zip;
        }
        if (string.Equals(value, "file", StringComparison.Ordinal))
        {
            return BundlePackKind.File;
        }
        throw new InvalidDataException(
            $"Bundle pack entry {index} kind must be 'zip' or 'file'."
        );
    }

    private static string ValidateFileTarget(string? value, int index)
    {
        if (
            value is null
            || value.Length > 1024
            || !string.Equals(value, value.Normalize(), StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"File bundle pack entry {index} target must be a bounded, normalized bundle-relative path."
            );
        }
        if (
            string.Equals(
                value,
                BundleManifestVerifier.ManifestFileName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            if (
                !string.Equals(
                    value,
                    BundleManifestVerifier.ManifestFileName,
                    StringComparison.Ordinal
                )
            )
            {
                throw new InvalidDataException(
                    $"File bundle pack entry {index} target must use the canonical '{BundleManifestVerifier.ManifestFileName}' casing."
                );
            }
            return value;
        }
        try
        {
            return BundleManifestVerifier.ValidateRelativePath(value);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException(
                $"File bundle pack entry {index} target is not a safe bundle-relative path: {ex.Message}",
                ex
            );
        }
    }

    private static string ValidateIdentifier(string? value, string description, int maximumLength)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !char.IsAsciiLetterOrDigit(value[0])
            || !char.IsAsciiLetterOrDigit(value[^1])
            || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')
            )
        )
        {
            throw new InvalidDataException(
                $"Bundle pack {description} must be a bounded ASCII identifier."
            );
        }
        return value;
    }

    private static string ValidateLowercaseSha256(string? value, string description)
    {
        if (
            value is not { Length: 64 }
            || value.Any(character =>
                character is not (>= '0' and <= '9')
                    && character is not (>= 'a' and <= 'f')
            )
        )
        {
            throw new InvalidDataException(
                $"Bundle pack {description} must be a lowercase SHA-256."
            );
        }
        return value;
    }

    private static Uri ValidateHttpsUri(string? value, string description)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > 4096
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
        )
        {
            throw new InvalidDataException(
                $"Bundle pack {description} must contain an absolute HTTPS URL."
            );
        }
        return ValidateHttpsUri(uri, description);
    }

    private static Uri ValidateHttpsUri(Uri uri, string description)
    {
        if (
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
        )
        {
            throw new InvalidDataException(
                $"Bundle pack {description} must use an HTTPS URL without user information or a fragment."
            );
        }
        return uri;
    }

    private static string RequireText(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Bundle pack trust manifest {description} must be non-empty text."
            );
        }
        return value.GetString()!;
    }

    private static void RejectDuplicateProperty(
        HashSet<string> properties,
        string property,
        string description
    )
    {
        if (!properties.Add(property))
        {
            throw new InvalidDataException(
                $"Bundle pack trust manifest {description} contains duplicate property '{property}'."
            );
        }
    }

    private static void ValidateContentRange(
        ContentRangeHeaderValue? range,
        BundlePackDefinition pack,
        long offset
    )
    {
        if (
            range is null
            || !range.HasRange
            || !range.HasLength
            || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || range.From != offset
            || range.To != pack.Bytes - 1
            || range.Length != pack.Bytes
        )
        {
            throw new InvalidDataException(
                $"Bundle pack '{pack.Id}' returned an invalid Content-Range."
            );
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode
            is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect
        || (int)statusCode == 308;

    private static bool HashesEqual(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right)
        );

    private static HttpMessageHandler CreateDefaultHandler() =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            UseCookies = false,
        };

    private sealed class VerifiedPackHandle : IDisposable
    {
        internal VerifiedPackHandle(BundlePackDefinition definition, FileStream stream)
        {
            Definition = definition;
            Stream = stream;
        }

        internal BundlePackDefinition Definition { get; }
        internal FileStream Stream { get; }
        internal ZipArchive? Archive { get; private set; }

        internal void OpenArchive()
        {
            if (Definition.Kind != BundlePackKind.Zip)
            {
                throw new InvalidOperationException(
                    $"File bundle pack '{Definition.Id}' cannot be opened as a ZIP archive."
                );
            }
            Stream.Position = 0;
            Archive = new ZipArchive(Stream, ZipArchiveMode.Read, leaveOpen: true);
        }

        public void Dispose()
        {
            Archive?.Dispose();
            Stream.Dispose();
        }
    }

    private sealed class VerifiedPackCollection : IDisposable
    {
        internal VerifiedPackCollection(IReadOnlyList<VerifiedPackHandle> handles)
        {
            Handles = handles;
        }

        internal IReadOnlyList<VerifiedPackHandle> Handles { get; }

        public void Dispose()
        {
            foreach (var handle in Handles)
            {
                handle.Dispose();
            }
        }
    }

    private sealed record ZipEntryPlan(
        VerifiedPackHandle Handle,
        ZipArchiveEntry Entry,
        string RelativePath,
        bool IsDirectory
    );

    private sealed record FileEntryPlan(
        VerifiedPackHandle Handle,
        string RelativePath
    );

    private sealed record AssemblyPlans(
        IReadOnlyList<ZipEntryPlan> ZipEntries,
        IReadOnlyList<FileEntryPlan> Files
    );
}
