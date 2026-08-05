#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace bstrings;

/// <summary>
/// Builds exact, externally sorted indexes for record identifiers and translated-parent
/// references. Identifier equality is always ordinal string equality; hashes only select a
/// disk partition and therefore cannot create a false duplicate or false parent match.
/// </summary>
internal sealed class DiskBackedProvenanceValidator : IDisposable
{
    internal const int MaxIdentifierCharacters = 4096;
    internal const int DefaultPartitionCount = 32;
    internal const int DefaultSortChunkBytes = 4 * 1024 * 1024;
    private const int MergeFanIn = 32;
    private const int StreamBufferBytes = 64 * 1024;
    private const int MaxIndexValueUtf8Bytes = 80 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly string _rootDirectory;
    private readonly int _partitionCount;
    private readonly int _sortChunkBytes;
    private readonly bool _requireEveryOriginalReferenced;
    private readonly Dictionary<(IndexKind Kind, int Partition), BinaryWriter> _writers = [];
    private bool _writersClosed;
    private bool _validated;
    private bool _disposed;

    internal DiskBackedProvenanceValidator(
        string workingDirectory,
        int partitionCount = DefaultPartitionCount,
        int sortChunkBytes = DefaultSortChunkBytes,
        bool requireEveryOriginalReferenced = false
    )
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A provenance-index working directory is required.");
        }
        if (partitionCount < 1 || partitionCount > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionCount));
        }
        if (sortChunkBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(sortChunkBytes));
        }

        _partitionCount = partitionCount;
        _sortChunkBytes = sortChunkBytes;
        _requireEveryOriginalReferenced = requireEveryOriginalReferenced;
        _rootDirectory = Path.Combine(
            Path.GetFullPath(workingDirectory),
            ".bstrings-provenance." + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_rootDirectory);
    }

    internal string WorkingDirectory => _rootDirectory;

    internal void AddOriginal(string recordId)
    {
        AddOriginal(recordId, lineage: null);
    }

    internal void AddOriginal(string recordId, TranslationLineageIdentity? lineage)
    {
        ThrowIfSealed();
        ValidateIdentifier(recordId, "recordId");
        var partition = GetPartition(recordId);
        Append(IndexKind.AllRecordIds, partition, recordId);
        Append(
            IndexKind.OriginalRecordIds,
            partition,
            BuildReferenceValue(recordId, lineage, childRecordId: null)
        );
    }

    internal void AddTranslation(string recordId, string parentRecordId)
    {
        AddTranslation(recordId, parentRecordId, lineage: null);
    }

    internal void AddTranslation(
        string recordId,
        string parentRecordId,
        TranslationLineageIdentity? lineage
    )
    {
        ThrowIfSealed();
        ValidateIdentifier(recordId, "recordId");
        ValidateIdentifier(parentRecordId, "parentRecordId");
        Append(IndexKind.AllRecordIds, GetPartition(recordId), recordId);
        Append(
            IndexKind.ParentReferences,
            GetPartition(parentRecordId),
            BuildReferenceValue(parentRecordId, lineage, recordId)
        );
    }

    internal void Validate(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_validated)
        {
            return;
        }

        CloseWriters();
        for (var partition = 0; partition < _partitionCount; partition++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = SortIndex(
                IndexPath(IndexKind.AllRecordIds, partition),
                rejectDuplicates: true,
                cancellationToken
            );
            var originals = SortIndex(
                IndexPath(IndexKind.OriginalRecordIds, partition),
                rejectDuplicates: false,
                cancellationToken
            );
            var parents = SortIndex(
                IndexPath(IndexKind.ParentReferences, partition),
                rejectDuplicates: false,
                cancellationToken
            );
            ValidateParentReferences(
                originals,
                parents,
                _requireEveryOriginalReferenced,
                cancellationToken
            );
        }
        _validated = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CloseWriters();
        try
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Append(IndexKind kind, int partition, string value)
    {
        var key = (kind, partition);
        if (!_writers.TryGetValue(key, out var writer))
        {
            writer = new BinaryWriter(
                new FileStream(
                    IndexPath(kind, partition),
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    StreamBufferBytes,
                    FileOptions.SequentialScan
                ),
                StrictUtf8,
                leaveOpen: false
            );
            _writers.Add(key, writer);
        }
        WriteValue(writer, value);
    }

    private string? SortIndex(
        string inputPath,
        bool rejectDuplicates,
        CancellationToken cancellationToken
    )
    {
        if (!File.Exists(inputPath) || new FileInfo(inputPath).Length == 0)
        {
            return null;
        }

        var runs = new List<string>();
        using (var reader = new BinaryStringReader(inputPath))
        {
            var reachedEnd = false;
            while (!reachedEnd)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new List<string>();
                long chunkBytes = 0;
                while (reader.TryRead(out var value))
                {
                    values.Add(value);
                    chunkBytes += StrictUtf8.GetByteCount(value) + sizeof(int);
                    if (chunkBytes >= _sortChunkBytes)
                    {
                        break;
                    }
                }
                reachedEnd = reader.EndOfStream;
                if (values.Count != 0)
                {
                    var runPath = NewRunPath();
                    WriteSortedRun(values, runPath, rejectDuplicates);
                    runs.Add(runPath);
                }
            }
        }

        while (runs.Count > 1)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextPass = new List<string>((runs.Count + MergeFanIn - 1) / MergeFanIn);
            for (var start = 0; start < runs.Count; start += MergeFanIn)
            {
                var count = Math.Min(MergeFanIn, runs.Count - start);
                if (count == 1)
                {
                    nextPass.Add(runs[start]);
                    continue;
                }
                var group = runs.GetRange(start, count);
                var mergedPath = NewRunPath();
                MergeRuns(group, mergedPath, rejectDuplicates, cancellationToken);
                nextPass.Add(mergedPath);
                foreach (var path in group)
                {
                    File.Delete(path);
                }
            }
            runs = nextPass;
        }

        return runs.Count == 0 ? null : runs[0];
    }

    private static void WriteSortedRun(
        List<string> values,
        string outputPath,
        bool rejectDuplicates
    )
    {
        values.Sort(StringComparer.Ordinal);
        using var writer = CreateBinaryWriter(outputPath);
        string? previous = null;
        foreach (var value in values)
        {
            if (previous is not null && string.Equals(previous, value, StringComparison.Ordinal))
            {
                if (rejectDuplicates)
                {
                    throw DuplicateRecordId(value);
                }
                continue;
            }
            WriteValue(writer, value);
            previous = value;
        }
    }

    private static void MergeRuns(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        bool rejectDuplicates,
        CancellationToken cancellationToken
    )
    {
        var readers = new List<BinaryStringReader>(inputPaths.Count);
        try
        {
            var queue = new PriorityQueue<MergeItem, string>(StringComparer.Ordinal);
            for (var index = 0; index < inputPaths.Count; index++)
            {
                var reader = new BinaryStringReader(inputPaths[index]);
                readers.Add(reader);
                if (reader.TryRead(out var value))
                {
                    queue.Enqueue(new MergeItem(index, value), value);
                }
            }

            using var writer = CreateBinaryWriter(outputPath);
            string? previous = null;
            while (queue.TryDequeue(out var item, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    previous is not null
                    && string.Equals(previous, item.Value, StringComparison.Ordinal)
                )
                {
                    if (rejectDuplicates)
                    {
                        throw DuplicateRecordId(item.Value);
                    }
                }
                else
                {
                    WriteValue(writer, item.Value);
                    previous = item.Value;
                }

                if (readers[item.ReaderIndex].TryRead(out var next))
                {
                    queue.Enqueue(new MergeItem(item.ReaderIndex, next), next);
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static void ValidateParentReferences(
        string? originalsPath,
        string? parentsPath,
        bool requireEveryOriginalReferenced,
        CancellationToken cancellationToken
    )
    {
        if (parentsPath is null)
        {
            if (requireEveryOriginalReferenced && originalsPath is not null)
            {
                using var originalsOnly = new BinaryStringReader(originalsPath);
                if (originalsOnly.TryRead(out var unmatchedOriginalValue))
                {
                    throw MissingTranslationChild(
                        ParseReferenceValue(unmatchedOriginalValue).RecordId
                    );
                }
            }
            return;
        }

        using var parents = new BinaryStringReader(parentsPath);
        if (!parents.TryRead(out var parentValue))
        {
            return;
        }
        var parent = ParseReferenceValue(parentValue);
        if (originalsPath is null)
        {
            throw MissingParent(parent.RecordId);
        }

        using var originals = new BinaryStringReader(originalsPath);
        var hasOriginal = originals.TryRead(out var originalValue);
        var original = hasOriginal ? ParseReferenceValue(originalValue) : default;
        while (hasOriginal || parentValue.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (parentValue.Length == 0)
            {
                if (requireEveryOriginalReferenced && hasOriginal)
                {
                    throw MissingTranslationChild(original.RecordId);
                }
                return;
            }
            if (!hasOriginal)
            {
                throw MissingParent(parent.RecordId);
            }

            var comparison = string.CompareOrdinal(original.Key, parent.Key);
            if (comparison < 0)
            {
                if (requireEveryOriginalReferenced)
                {
                    throw MissingTranslationChild(original.RecordId);
                }
                hasOriginal = originals.TryRead(out originalValue);
                original = hasOriginal ? ParseReferenceValue(originalValue) : default;
                continue;
            }
            if (comparison > 0)
            {
                throw MissingParent(parent.RecordId);
            }

            var parentCount = 0;
            var matchedParentKey = parent.Key;
            do
            {
                parentCount++;
                if (!string.Equals(original.Identity, parent.Identity, StringComparison.Ordinal))
                {
                    throw LineageMismatch(parent.RecordId, parent.ChildRecordId);
                }
                if (!parents.TryRead(out parentValue))
                {
                    parentValue = string.Empty;
                    break;
                }
                parent = ParseReferenceValue(parentValue);
            } while (string.Equals(parent.Key, matchedParentKey, StringComparison.Ordinal));

            if (requireEveryOriginalReferenced && parentCount != 1)
            {
                throw DuplicateTranslationChildren(original.RecordId, parentCount);
            }

            hasOriginal = originals.TryRead(out originalValue);
            original = hasOriginal ? ParseReferenceValue(originalValue) : default;
        }
    }

    private static string BuildReferenceValue(
        string recordId,
        TranslationLineageIdentity? lineage,
        string? childRecordId
    )
    {
        var key = Convert.ToBase64String(StrictUtf8.GetBytes(recordId));
        var identity = EncodeLineage(lineage);
        var child = childRecordId is null
            ? string.Empty
            : Convert.ToBase64String(StrictUtf8.GetBytes(childRecordId));
        return string.Concat(
            key,
            ":",
            identity.Length.ToString(CultureInfo.InvariantCulture),
            ":",
            identity,
            ":",
            child
        );
    }

    private static string EncodeLineage(TranslationLineageIdentity? lineage)
    {
        if (lineage is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        AppendIdentityComponent(builder, lineage.SourceFile);
        AppendIdentityComponent(builder, lineage.LocationKind);
        AppendIdentityComponent(builder, lineage.LocationValue);
        AppendIdentityComponent(builder, lineage.OriginExtractor);
        AppendIdentityComponent(builder, lineage.OriginVersion);
        AppendIdentityComponent(builder, lineage.OriginKind);
        AppendIdentityComponent(builder, lineage.OriginModel);
        AppendIdentityComponent(builder, lineage.OriginRevision);
        AppendIdentityComponent(builder, lineage.OriginModelSha256);
        AppendIdentityComponent(builder, lineage.OriginProvider);
        return builder.ToString();
    }

    private static void AppendIdentityComponent(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
    }

    private static ReferenceValue ParseReferenceValue(string value)
    {
        var keyEnd = value.IndexOf(':');
        var lengthEnd = keyEnd < 0 ? -1 : value.IndexOf(':', keyEnd + 1);
        if (
            keyEnd < 1
            || lengthEnd <= keyEnd + 1
            || !int.TryParse(
                value.AsSpan(keyEnd + 1, lengthEnd - keyEnd - 1),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var identityLength
            )
            || identityLength < 0
        )
        {
            throw new InvalidDataException("The temporary provenance index is corrupt.");
        }

        var identityStart = lengthEnd + 1;
        var childSeparator = identityStart + identityLength;
        if (childSeparator >= value.Length || value[childSeparator] != ':')
        {
            throw new InvalidDataException("The temporary provenance index is corrupt.");
        }

        try
        {
            var key = value[..keyEnd];
            var recordId = StrictUtf8.GetString(Convert.FromBase64String(key));
            var child = value[(childSeparator + 1)..];
            var childRecordId = child.Length == 0
                ? null
                : StrictUtf8.GetString(Convert.FromBase64String(child));
            return new ReferenceValue(
                key,
                value.Substring(identityStart, identityLength),
                recordId,
                childRecordId
            );
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The temporary provenance index is corrupt.", ex);
        }
    }

    private static BinaryWriter CreateBinaryWriter(string path) =>
        new(
            new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                StreamBufferBytes,
                FileOptions.SequentialScan
            ),
            StrictUtf8,
            leaveOpen: false
        );

    private static void WriteValue(BinaryWriter writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length < 1 || bytes.Length > MaxIndexValueUtf8Bytes)
        {
            throw new InvalidDataException(
                "A temporary provenance-index value exceeds its bounded safety limit."
            );
        }
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private int GetPartition(string value)
    {
        var hash = SHA256.HashData(StrictUtf8.GetBytes(value));
        return hash[0] % _partitionCount;
    }

    private string IndexPath(IndexKind kind, int partition) =>
        Path.Combine(_rootDirectory, $"{kind}-{partition:D3}.bin");

    private string NewRunPath() =>
        Path.Combine(_rootDirectory, "run-" + Guid.NewGuid().ToString("N") + ".bin");

    private void CloseWriters()
    {
        if (_writersClosed)
        {
            return;
        }
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }
        _writers.Clear();
        _writersClosed = true;
    }

    private void ThrowIfSealed()
    {
        ThrowIfDisposed();
        if (_writersClosed || _validated)
        {
            throw new InvalidOperationException("The provenance index has already been sealed.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"An enrichment {name} is empty.");
        }
        if (value.Length > MaxIdentifierCharacters)
        {
            throw new InvalidDataException(
                $"An enrichment {name} exceeds the {MaxIdentifierCharacters:N0}-character safety limit."
            );
        }
    }

    private static InvalidDataException DuplicateRecordId(string value) =>
        new($"Enrichment JSONL repeats recordId '{value}'.");

    private static InvalidDataException MissingParent(string value) =>
        new(
            $"Translated enrichment record references parentRecordId '{value}', which does not exist among the original records."
        );

    private static InvalidDataException MissingTranslationChild(string value) =>
        new(
            $"Translation candidate '{value}' does not have exactly one translated child record."
        );

    private static InvalidDataException DuplicateTranslationChildren(string value, int count) =>
        new(
            $"Translation candidate '{value}' has {count:N0} translated child records; exactly one is required."
        );

    private static InvalidDataException LineageMismatch(string parent, string? child) =>
        new(
            $"Translated enrichment record '{child ?? "<unknown>"}' does not retain the exact sourceFile, location, and origin lineage of parentRecordId '{parent}'."
        );

    private enum IndexKind
    {
        AllRecordIds,
        OriginalRecordIds,
        ParentReferences,
    }

    private readonly record struct MergeItem(int ReaderIndex, string Value);

    private readonly record struct ReferenceValue(
        string Key,
        string Identity,
        string RecordId,
        string? ChildRecordId
    );

    private sealed class BinaryStringReader : IDisposable
    {
        private readonly FileStream _stream;
        private readonly BinaryReader _reader;

        internal BinaryStringReader(string path)
        {
            _stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                StreamBufferBytes,
                FileOptions.SequentialScan
            );
            _reader = new BinaryReader(_stream, StrictUtf8, leaveOpen: true);
        }

        internal bool EndOfStream => _stream.Position >= _stream.Length;

        internal bool TryRead(out string value)
        {
            value = string.Empty;
            if (EndOfStream)
            {
                return false;
            }
            if (_stream.Length - _stream.Position < sizeof(int))
            {
                throw new InvalidDataException("The temporary provenance index is truncated.");
            }
            var byteLength = _reader.ReadInt32();
            if (byteLength < 1 || byteLength > MaxIndexValueUtf8Bytes)
            {
                throw new InvalidDataException("The temporary provenance index is corrupt.");
            }
            if (_stream.Length - _stream.Position < byteLength)
            {
                throw new InvalidDataException("The temporary provenance index is truncated.");
            }
            var bytes = _reader.ReadBytes(byteLength);
            if (bytes.Length != byteLength)
            {
                throw new InvalidDataException("The temporary provenance index is truncated.");
            }
            value = StrictUtf8.GetString(bytes);
            return true;
        }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
        }
    }
}
