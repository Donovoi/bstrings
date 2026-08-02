#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda;

namespace bstrings;

public struct GpuHitPosition
{
    public int Start;
    public int Length;
}

internal sealed class GpuStringScanner : IDisposable
{
    private readonly Context _context;
    private readonly Accelerator _accelerator;
    private readonly Action<
        AcceleratorStream,
        Index1D,
        ArrayView1D<byte, Stride1D.Dense>,
        int,
        int,
        int,
        byte,
        byte,
        ArrayView1D<GpuHitPosition, Stride1D.Dense>,
        ArrayView1D<int, Stride1D.Dense>,
        int
    > _asciiKernel;
    private readonly Action<
        AcceleratorStream,
        Index1D,
        ArrayView1D<byte, Stride1D.Dense>,
        int,
        int,
        int,
        ushort,
        ushort,
        ArrayView1D<GpuHitPosition, Stride1D.Dense>,
        ArrayView1D<int, Stride1D.Dense>,
        int
    > _unicodeKernel;
    private readonly ConcurrentQueue<GpuLane> _availableLanes = new();
    private readonly List<GpuLane> _lanes = [];
    private readonly SemaphoreSlim _laneSemaphore;
    private bool _disposed;

    private GpuStringScanner(Context context, Accelerator accelerator)
    {
        _context = context;
        _accelerator = accelerator;
        DeviceName = accelerator.Name;
        _asciiKernel = accelerator.LoadAutoGroupedKernel<
            Index1D,
            ArrayView1D<byte, Stride1D.Dense>,
            int,
            int,
            int,
            byte,
            byte,
            ArrayView1D<GpuHitPosition, Stride1D.Dense>,
            ArrayView1D<int, Stride1D.Dense>,
            int
        >(FindAsciiHitsKernel);
        _unicodeKernel = accelerator.LoadAutoGroupedKernel<
            Index1D,
            ArrayView1D<byte, Stride1D.Dense>,
            int,
            int,
            int,
            ushort,
            ushort,
            ArrayView1D<GpuHitPosition, Stride1D.Dense>,
            ArrayView1D<int, Stride1D.Dense>,
            int
        >(FindUnicodeHitsKernel);
        var laneCount = Math.Min(2, Math.Max(1, Environment.ProcessorCount));
        _laneSemaphore = new SemaphoreSlim(laneCount, laneCount);
        for (var index = 0; index < laneCount; index++)
        {
            var lane = new GpuLane(accelerator);
            _lanes.Add(lane);
            _availableLanes.Enqueue(lane);
        }
    }

    internal string DeviceName { get; }

    internal static bool TryCreate(
        out GpuStringScanner? scanner,
        out string? failure
    )
    {
        Context? context = null;
        Accelerator? accelerator = null;
        GpuStringScanner? candidate = null;

        try
        {
            context = Context.Create(builder =>
                builder.Cuda().PageLocking(PageLockingMode.Auto)
            );
            foreach (var device in context.GetCudaDevices())
            {
                accelerator = device.CreateAccelerator(context);
                break;
            }

            if (accelerator is null)
            {
                throw new InvalidOperationException("ILGPU did not find a CUDA device.");
            }

            candidate = new GpuStringScanner(context, accelerator);
            context = null;
            accelerator = null;
            candidate.ValidateAgainstCpu();
            scanner = candidate;
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            candidate?.Dispose();
            accelerator?.Dispose();
            context?.Dispose();
            scanner = null;
            failure = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    internal List<string> ProcessChunk(
        ReadOnlySpan<byte> chunk,
        long fileOffset,
        bool isBoundaryChunk,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool includeOffset,
        int codePage,
        string asciiRange,
        string unicodeRange,
        bool suppressLeadingFragment = false,
        bool suppressTrailingFragment = false,
        int boundaryCrossingOffset = 0
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var results = new List<string>();

        if (chunk.IsEmpty || (!asciiSearch && !unicodeSearch))
        {
            return results;
        }

        List<GpuHitPosition> unicodePositions = [];
        List<GpuHitPosition> asciiPositions = [];
        var ownership = new ChunkHitOwnership(
            suppressLeadingFragment,
            suppressTrailingFragment,
            isBoundaryChunk
                ? (boundaryCrossingOffset > 0 ? boundaryCrossingOffset : chunk.Length / 2)
                : -1
        );

        _laneSemaphore.Wait();
        if (!_availableLanes.TryDequeue(out var lane))
        {
            _laneSemaphore.Release();
            throw new InvalidOperationException("A CUDA processing lane was not available.");
        }

        try
        {
            lane.EnsureInputCapacity(chunk.Length);
            lane.InputBuffer!.View.BaseView
                .SubView(0, chunk.Length)
                .CopyFromCPU(lane.Stream, chunk);

            if (unicodeSearch)
            {
                var (minChar, maxChar) = SearchCore.ParseUnicodeRange(unicodeRange);
                var unitCount = chunk.Length / 2;
                unicodePositions = FindUnicodeHits(
                    lane,
                    unitCount,
                    chunk.Length,
                    minLength,
                    maxLength,
                    minChar,
                    maxChar
                );
            }

            if (asciiSearch)
            {
                var (minChar, maxChar) = SearchCore.ParseCharRange(asciiRange);
                asciiPositions = FindAsciiHits(
                    lane,
                    chunk.Length,
                    minLength,
                    maxLength,
                    minChar,
                    maxChar
                );
            }
        }
        finally
        {
            _availableLanes.Enqueue(lane);
            _laneSemaphore.Release();
        }

        MaterializeUnicodeHits(
            results,
            chunk,
            unicodePositions,
            fileOffset,
            includeOffset,
            isBoundaryChunk,
            ownership
        );
        MaterializeAsciiHits(
            results,
            chunk,
            asciiPositions,
            fileOffset,
            includeOffset,
            isBoundaryChunk,
            codePage,
            ownership
        );

        return results;
    }

    private List<GpuHitPosition> FindAsciiHits(
        GpuLane lane,
        int byteCount,
        int minLength,
        int maxLength,
        byte minChar,
        byte maxChar
    )
    {
        var capacity = CalculateMaximumHitCount(byteCount, minLength);
        lane.EnsureHitCapacity(capacity);
        ResetHitCount(lane);
        _asciiKernel(
            lane.Stream,
            byteCount,
            lane.InputBuffer!.View,
            byteCount,
            minLength,
            maxLength,
            minChar,
            maxChar,
            lane.HitBuffer!.View,
            lane.HitCountBuffer.View,
            capacity
        );
        return ReadOrderedHits(lane, capacity);
    }

    private List<GpuHitPosition> FindUnicodeHits(
        GpuLane lane,
        int unitCount,
        int byteCount,
        int minLength,
        int maxLength,
        char minChar,
        char maxChar
    )
    {
        var capacity = CalculateMaximumHitCount(unitCount, minLength);
        lane.EnsureHitCapacity(capacity);
        ResetHitCount(lane);
        _unicodeKernel(
            lane.Stream,
            unitCount,
            lane.InputBuffer!.View,
            byteCount,
            minLength,
            maxLength,
            minChar,
            maxChar,
            lane.HitBuffer!.View,
            lane.HitCountBuffer.View,
            capacity
        );
        return ReadOrderedHits(lane, capacity);
    }

    private static void ResetHitCount(GpuLane lane)
    {
        lane.HitCount[0] = 0;
        lane.HitCountBuffer.CopyFromCPU(lane.Stream, lane.HitCount);
    }

    private static List<GpuHitPosition> ReadOrderedHits(GpuLane lane, int capacity)
    {
        lane.HitCountBuffer.CopyToCPU(lane.Stream, lane.HitCount);
        lane.Stream.Synchronize();
        var count = lane.HitCount[0];
        if (count > capacity)
        {
            throw new InvalidOperationException(
                $"CUDA hit buffer overflow: kernel produced {count:N0} hits for capacity {capacity:N0}."
            );
        }

        if (count == 0)
        {
            return [];
        }

        var positions = new GpuHitPosition[count];
        lane.HitBuffer!.View.BaseView
            .SubView(0, count)
            .CopyToCPU(lane.Stream, positions);
        lane.Stream.Synchronize();
        Array.Sort(positions, static (left, right) => left.Start.CompareTo(right.Start));
        return new List<GpuHitPosition>(positions);
    }

    private static int GrowCapacity(int current, int required)
    {
        var capacity = Math.Max(1024, current);
        while (capacity < required)
        {
            capacity = capacity > int.MaxValue / 2 ? required : capacity * 2;
        }

        return capacity;
    }

    internal static int CalculateMaximumHitCount(int unitCount, int minLength)
    {
        if (unitCount <= 0)
        {
            return 1;
        }

        var safeMinimum = Math.Max(1, minLength);
        return checked((unitCount / (safeMinimum + 1)) + 1);
    }

    private static void MaterializeAsciiHits(
        List<string> results,
        ReadOnlySpan<byte> data,
        IEnumerable<GpuHitPosition> hits,
        long fileOffset,
        bool includeOffset,
        bool isBoundaryChunk,
        int codePage,
        ChunkHitOwnership ownership
    )
    {
        var encoding =
            CodePagesEncodingProvider.Instance.GetEncoding(codePage)
            ?? Encoding.GetEncoding(codePage);

        foreach (var hit in hits)
        {
            if (!ownership.Accepts(hit.Start, hit.Length, data.Length))
            {
                continue;
            }

            var value = encoding.GetString(data.Slice(hit.Start, hit.Length));
            AddResult(
                results,
                value,
                fileOffset + hit.Start,
                includeOffset,
                isBoundaryChunk
            );
        }
    }

    private static void MaterializeUnicodeHits(
        List<string> results,
        ReadOnlySpan<byte> data,
        IEnumerable<GpuHitPosition> hits,
        long fileOffset,
        bool includeOffset,
        bool isBoundaryChunk,
        ChunkHitOwnership ownership
    )
    {
        foreach (var hit in hits)
        {
            if (!ownership.Accepts(hit.Start, hit.Length * 2, data.Length))
            {
                continue;
            }

            var value = Encoding.Unicode.GetString(data.Slice(hit.Start, hit.Length * 2));
            AddResult(
                results,
                value,
                fileOffset + hit.Start,
                includeOffset,
                isBoundaryChunk
            );
        }
    }

    private static void AddResult(
        List<string> results,
        string value,
        long absoluteOffset,
        bool includeOffset,
        bool isBoundaryChunk
    )
    {
        var result = includeOffset ? $"0x{absoluteOffset:X}\t{value}" : value;
        results.Add(isBoundaryChunk ? "  " + result : result);
    }

    private void ValidateAgainstCpu()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var data = new byte[]
        {
            0x00,
            (byte)'A',
            (byte)'B',
            (byte)'C',
            0x00,
            (byte)'x',
            0x00,
            (byte)'y',
            0x00,
            (byte)'z',
            0x00,
            0x80,
            0x81,
            0x82,
            0x00,
        };
        var expected = ChunkProcessingCore.ProcessChunk(
            data,
            0x100,
            false,
            3,
            8,
            true,
            true,
            true,
            "[\\x20-\\x7E]",
            "[\\u0020-\\u007E]",
            1252
        );
        var actual = ProcessChunk(
            data,
            0x100,
            false,
            3,
            8,
            true,
            true,
            true,
            1252,
            "[\\x20-\\x7E]",
            "[\\u0020-\\u007E]"
        );

        if (!expected.SequenceEqual(actual))
        {
            throw new InvalidOperationException(
                $"CUDA parity self-test failed. CPU=[{string.Join(" | ", expected)}], GPU=[{string.Join(" | ", actual)}]"
            );
        }
    }

    private static void FindAsciiHitsKernel(
        Index1D index,
        ArrayView1D<byte, Stride1D.Dense> data,
        int byteCount,
        int minLength,
        int maxLength,
        byte minChar,
        byte maxChar,
        ArrayView1D<GpuHitPosition, Stride1D.Dense> hits,
        ArrayView1D<int, Stride1D.Dense> hitCount,
        int hitCapacity
    )
    {
        var start = (int)index;
        var current = data[start];
        if (current < minChar || current > maxChar)
        {
            return;
        }

        if (start > 0)
        {
            var previous = data[start - 1];
            if (previous >= minChar && previous <= maxChar)
            {
                return;
            }
        }

        var end = start + 1;
        while (end < byteCount)
        {
            current = data[end];
            if (current < minChar || current > maxChar)
            {
                break;
            }

            end++;
        }

        var length = end - start;
        if (length < minLength)
        {
            return;
        }

        if (maxLength > 0 && length > maxLength)
        {
            length = maxLength;
        }

        var slot = Atomic.Add(ref hitCount[0], 1);
        if (slot < hitCapacity)
        {
            hits[slot] = new GpuHitPosition { Start = start, Length = length };
        }
    }

    private static void FindUnicodeHitsKernel(
        Index1D index,
        ArrayView1D<byte, Stride1D.Dense> data,
        int byteCount,
        int minLength,
        int maxLength,
        ushort minChar,
        ushort maxChar,
        ArrayView1D<GpuHitPosition, Stride1D.Dense> hits,
        ArrayView1D<int, Stride1D.Dense> hitCount,
        int hitCapacity
    )
    {
        var unitIndex = (int)index;
        var start = unitIndex * 2;
        if (start + 1 >= byteCount)
        {
            return;
        }

        var current = (ushort)(data[start] | (data[start + 1] << 8));
        if (current < minChar || current > maxChar)
        {
            return;
        }

        if (unitIndex > 0)
        {
            var previousStart = start - 2;
            var previous = (ushort)(
                data[previousStart] | (data[previousStart + 1] << 8)
            );
            if (previous >= minChar && previous <= maxChar)
            {
                return;
            }
        }

        var length = 1;
        var nextStart = start + 2;
        while (nextStart + 1 < byteCount)
        {
            current = (ushort)(data[nextStart] | (data[nextStart + 1] << 8));
            if (current < minChar || current > maxChar)
            {
                break;
            }

            length++;
            nextStart += 2;
        }

        if (length < minLength)
        {
            return;
        }

        if (maxLength > 0 && length > maxLength)
        {
            length = maxLength;
        }

        var slot = Atomic.Add(ref hitCount[0], 1);
        if (slot < hitCapacity)
        {
            hits[slot] = new GpuHitPosition { Start = start, Length = length };
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var lane in _lanes)
        {
            lane.Dispose();
        }
        _laneSemaphore.Dispose();
        _accelerator.Dispose();
        _context.Dispose();
        _disposed = true;
    }

    private sealed class GpuLane : IDisposable
    {
        private readonly Accelerator _accelerator;
        private int _inputCapacity;
        private int _hitCapacity;

        internal GpuLane(Accelerator accelerator)
        {
            _accelerator = accelerator;
            Stream = accelerator.CreateStream();
            HitCountBuffer = accelerator.Allocate1D<int>(1);
        }

        internal AcceleratorStream Stream { get; }

        internal int[] HitCount { get; } = new int[1];

        internal MemoryBuffer1D<byte, Stride1D.Dense>? InputBuffer { get; private set; }

        internal MemoryBuffer1D<GpuHitPosition, Stride1D.Dense>? HitBuffer
        {
            get;
            private set;
        }

        internal MemoryBuffer1D<int, Stride1D.Dense> HitCountBuffer { get; }

        internal void EnsureInputCapacity(int required)
        {
            if (required <= _inputCapacity)
            {
                return;
            }

            var capacity = GrowCapacity(_inputCapacity, required);
            InputBuffer?.Dispose();
            InputBuffer = _accelerator.Allocate1D<byte>(capacity);
            _inputCapacity = capacity;
        }

        internal void EnsureHitCapacity(int required)
        {
            if (required <= _hitCapacity)
            {
                return;
            }

            var capacity = GrowCapacity(_hitCapacity, required);
            HitBuffer?.Dispose();
            HitBuffer = _accelerator.Allocate1D<GpuHitPosition>(capacity);
            _hitCapacity = capacity;
        }

        public void Dispose()
        {
            HitCountBuffer.Dispose();
            HitBuffer?.Dispose();
            InputBuffer?.Dispose();
            Stream.Dispose();
        }
    }
}
