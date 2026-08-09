#nullable enable

using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace bstrings;

/// <summary>
/// Retains exact text only for preservation-fallback children. Parent lookups use an
/// on-disk hash table and always confirm the complete ordinal record identifier, so a
/// digest or bucket collision cannot authorize a match.
/// </summary>
internal sealed class DiskBackedFallbackTextValidator : IDisposable
{
    private const int BucketCount = 1 << 18;
    private const int BucketMask = BucketCount - 1;
    private const int StreamBufferBytes = 64 * 1024;
    private const int EntryHeaderBytes = sizeof(long) + (3 * sizeof(int)) + sizeof(byte);
    private const byte ParentMatchedFlag = 1;
    private const byte ChildReplayedFlag = 2;
    private const int MaximumTextUtf8Bytes =
        EnrichmentRegexPipelineCore.MaxJsonLineCharacters * 4;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true
    );

    private readonly string _rootDirectory;
    private readonly Action<string> _deleteDirectory;
    private FileStream? _bucketStream;
    private FileStream? _entryStream;
    private BinaryReader? _bucketReader;
    private BinaryWriter? _bucketWriter;
    private BinaryReader? _entryReader;
    private BinaryWriter? _entryWriter;
    private long _entryCount;
    private bool _cleaned;
    private bool _disposed;

    internal DiskBackedFallbackTextValidator(
        string workingDirectory,
        Action<string>? deleteDirectory = null
    )
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new ArgumentException("A fallback-text working directory is required.");
        }

        _rootDirectory = Path.Combine(
            Path.GetFullPath(workingDirectory),
            ".bstrings-fallback-text." + Guid.NewGuid().ToString("N")
        );
        _deleteDirectory = deleteDirectory ?? (path => Directory.Delete(path, recursive: true));
        Directory.CreateDirectory(_rootDirectory);
    }

    internal long Count => _entryCount;
    internal string WorkingDirectory => _rootDirectory;

    internal void AddFallback(
        string parentRecordId,
        string childRecordId,
        string childText
    )
    {
        ThrowIfDisposed();
        ValidateRecordIdentifier(parentRecordId, "parentRecordId");
        ValidateRecordIdentifier(childRecordId, "recordId");
        ArgumentNullException.ThrowIfNull(childText);
        EnsureStorage();

        if (TryFind(parentRecordId, out _))
        {
            throw new InvalidDataException(
                $"Preservation-fallback parent '{parentRecordId}' has more than one indexed child."
            );
        }

        var parentBytes = StrictUtf8.GetBytes(parentRecordId);
        var childIdBytes = StrictUtf8.GetBytes(childRecordId);
        var childTextBytes = StrictUtf8.GetBytes(childText);
        if (childTextBytes.Length > MaximumTextUtf8Bytes)
        {
            throw new InvalidDataException(
                "A preservation-fallback text exceeds the managed JSONL safety limit."
            );
        }

        var previousOffset = ReadBucketHead(parentRecordId);
        _entryWriter!.Flush();
        var entryOffset = _entryStream!.Length;
        _entryStream.Position = entryOffset;
        _entryWriter.Write(previousOffset);
        _entryWriter.Write(parentBytes.Length);
        _entryWriter.Write(childIdBytes.Length);
        _entryWriter.Write(childTextBytes.Length);
        _entryWriter.Write((byte)0);
        _entryWriter.Write(parentBytes);
        _entryWriter.Write(childIdBytes);
        _entryWriter.Write(childTextBytes);
        _entryWriter.Flush();

        WriteBucketHead(parentRecordId, entryOffset);
        _entryCount++;
    }

    internal bool ValidateParent(string recordId, string parentText)
    {
        ThrowIfDisposed();
        ValidateRecordIdentifier(recordId, "recordId");
        ArgumentNullException.ThrowIfNull(parentText);
        if (_entryCount == 0 || !TryFind(recordId, out var entry))
        {
            return false;
        }

        var expectedText = ReadUtf8(entry.ChildTextOffset, entry.ChildTextLength);
        if (!string.Equals(parentText, expectedText, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Preservation-fallback child for parent '{recordId}' does not contain the exact parent text."
            );
        }
        SetFlag(entry, ParentMatchedFlag);
        return true;
    }

    internal void ValidateFallbackChild(
        string parentRecordId,
        string childRecordId,
        string childText
    )
    {
        ThrowIfDisposed();
        ValidateRecordIdentifier(parentRecordId, "parentRecordId");
        ValidateRecordIdentifier(childRecordId, "recordId");
        ArgumentNullException.ThrowIfNull(childText);
        if (!TryFind(parentRecordId, out var entry))
        {
            throw new InvalidDataException(
                $"Preservation-fallback child '{childRecordId}' was absent from the selective integrity index."
            );
        }

        var expectedChildId = ReadUtf8(entry.ChildRecordIdOffset, entry.ChildRecordIdLength);
        var expectedText = ReadUtf8(entry.ChildTextOffset, entry.ChildTextLength);
        if (
            !string.Equals(childRecordId, expectedChildId, StringComparison.Ordinal)
            || !string.Equals(childText, expectedText, StringComparison.Ordinal)
        )
        {
            throw new InvalidDataException(
                $"Preservation-fallback child '{childRecordId}' changed after its selective integrity index was built."
            );
        }
        SetFlag(entry, ChildReplayedFlag);
    }

    internal void ValidateComplete(bool requireChildReplay)
    {
        ThrowIfDisposed();
        if (_entryCount == 0)
        {
            return;
        }

        _entryWriter!.Flush();
        long observed = 0;
        long offset = 0;
        while (offset < _entryStream!.Length)
        {
            var entry = ReadEntry(offset);
            if ((entry.Flags & ParentMatchedFlag) == 0)
            {
                throw new InvalidDataException(
                    "A preservation-fallback child did not match an exact parent text."
                );
            }
            if (requireChildReplay && (entry.Flags & ChildReplayedFlag) == 0)
            {
                throw new InvalidDataException(
                    "A preservation-fallback child was not replayed from the indexed translation source."
                );
            }
            observed++;
            offset = entry.NextOffset;
        }

        if (offset != _entryStream.Length || observed != _entryCount)
        {
            throw new InvalidDataException("The selective fallback-text index is corrupt.");
        }
    }

    internal void Cleanup()
    {
        ThrowIfDisposed();
        if (_cleaned)
        {
            return;
        }

        CloseStreams();
        try
        {
            _deleteDirectory(_rootDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The selective fallback-text index could not be removed; output was not published.",
                ex
            );
        }
        if (Directory.Exists(_rootDirectory))
        {
            throw new InvalidDataException(
                "The selective fallback-text index remained after cleanup; output was not published."
            );
        }
        _cleaned = true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        CloseStreams();
        try
        {
            if (Directory.Exists(_rootDirectory))
            {
                Directory.Delete(_rootDirectory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "The selective fallback-text index could not be removed; validation cannot complete.",
                ex
            );
        }
    }

    private bool TryFind(string parentRecordId, out Entry entry)
    {
        entry = default;
        if (_entryCount == 0)
        {
            return false;
        }

        var offset = ReadBucketHead(parentRecordId);
        while (offset >= 0)
        {
            entry = ReadEntry(offset);
            var indexedParent = ReadUtf8(entry.ParentRecordIdOffset, entry.ParentRecordIdLength);
            if (string.Equals(parentRecordId, indexedParent, StringComparison.Ordinal))
            {
                return true;
            }
            offset = entry.PreviousOffset;
        }
        entry = default;
        return false;
    }

    private Entry ReadEntry(long offset)
    {
        _entryWriter!.Flush();
        if (offset < 0 || offset > _entryStream!.Length - EntryHeaderBytes)
        {
            throw new InvalidDataException("The selective fallback-text index is corrupt.");
        }

        _entryStream.Position = offset;
        var previousOffset = _entryReader!.ReadInt64();
        var parentLength = _entryReader.ReadInt32();
        var childIdLength = _entryReader.ReadInt32();
        var childTextLength = _entryReader.ReadInt32();
        var flags = _entryReader.ReadByte();
        if (
            previousOffset < -1
            || previousOffset >= offset
            || parentLength < 1
            || parentLength > DiskBackedProvenanceValidator.MaxIdentifierCharacters * 4
            || childIdLength < 1
            || childIdLength > DiskBackedProvenanceValidator.MaxIdentifierCharacters * 4
            || childTextLength < 0
            || childTextLength > MaximumTextUtf8Bytes
        )
        {
            throw new InvalidDataException("The selective fallback-text index is corrupt.");
        }

        var parentOffset = checked(offset + EntryHeaderBytes);
        var childIdOffset = checked(parentOffset + parentLength);
        var childTextOffset = checked(childIdOffset + childIdLength);
        var nextOffset = checked(childTextOffset + childTextLength);
        if (nextOffset > _entryStream.Length)
        {
            throw new InvalidDataException("The selective fallback-text index is truncated.");
        }

        return new Entry(
            offset,
            previousOffset,
            flags,
            parentOffset,
            parentLength,
            childIdOffset,
            childIdLength,
            childTextOffset,
            childTextLength,
            nextOffset
        );
    }

    private string ReadUtf8(long offset, int length)
    {
        _entryWriter!.Flush();
        _entryStream!.Position = offset;
        var bytes = _entryReader!.ReadBytes(length);
        if (bytes.Length != length)
        {
            throw new InvalidDataException("The selective fallback-text index is truncated.");
        }
        return StrictUtf8.GetString(bytes);
    }

    private void SetFlag(Entry entry, byte flag)
    {
        var updated = (byte)(entry.Flags | flag);
        _entryWriter!.Flush();
        _entryStream!.Position = entry.Offset + EntryHeaderBytes - sizeof(byte);
        _entryWriter.Write(updated);
        _entryWriter.Flush();
    }

    private long ReadBucketHead(string parentRecordId)
    {
        var bucket = GetBucket(parentRecordId);
        _bucketWriter!.Flush();
        _bucketStream!.Position = checked((long)bucket * sizeof(long));
        var encoded = _bucketReader!.ReadInt64();
        return encoded == 0 ? -1 : encoded - 1;
    }

    private void WriteBucketHead(string parentRecordId, long entryOffset)
    {
        var bucket = GetBucket(parentRecordId);
        _bucketWriter!.Flush();
        _bucketStream!.Position = checked((long)bucket * sizeof(long));
        _bucketWriter.Write(checked(entryOffset + 1));
        _bucketWriter.Flush();
    }

    private static int GetBucket(string parentRecordId)
    {
        var digest = SHA256.HashData(StrictUtf8.GetBytes(parentRecordId));
        return (int)(BinaryPrimitives.ReadUInt32LittleEndian(digest) & BucketMask);
    }

    private void EnsureStorage()
    {
        if (_entryStream is not null)
        {
            return;
        }

        _bucketStream = new FileStream(
            Path.Combine(_rootDirectory, "buckets.bin"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            StreamBufferBytes,
            FileOptions.RandomAccess
        );
        _bucketStream.SetLength((long)BucketCount * sizeof(long));
        _bucketReader = new BinaryReader(_bucketStream, StrictUtf8, leaveOpen: true);
        _bucketWriter = new BinaryWriter(_bucketStream, StrictUtf8, leaveOpen: true);

        _entryStream = new FileStream(
            Path.Combine(_rootDirectory, "entries.bin"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            StreamBufferBytes,
            FileOptions.RandomAccess
        );
        _entryReader = new BinaryReader(_entryStream, StrictUtf8, leaveOpen: true);
        _entryWriter = new BinaryWriter(_entryStream, StrictUtf8, leaveOpen: true);
    }

    private static void ValidateRecordIdentifier(string value, string fieldName)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || value.Length > DiskBackedProvenanceValidator.MaxIdentifierCharacters
        )
        {
            throw new InvalidDataException(
                $"Selective fallback-text index field '{fieldName}' is invalid."
            );
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed || _cleaned, this);
    }

    private void CloseStreams()
    {
        _entryWriter?.Dispose();
        _entryWriter = null;
        _entryReader?.Dispose();
        _entryReader = null;
        _entryStream?.Dispose();
        _entryStream = null;
        _bucketWriter?.Dispose();
        _bucketWriter = null;
        _bucketReader?.Dispose();
        _bucketReader = null;
        _bucketStream?.Dispose();
        _bucketStream = null;
    }

    private readonly record struct Entry(
        long Offset,
        long PreviousOffset,
        byte Flags,
        long ParentRecordIdOffset,
        int ParentRecordIdLength,
        long ChildRecordIdOffset,
        int ChildRecordIdLength,
        long ChildTextOffset,
        int ChildTextLength,
        long NextOffset
    );
}
