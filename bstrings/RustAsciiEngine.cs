#nullable enable

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;

namespace bstrings;

internal enum CpuStringEngineMode
{
    DotNet,
    Rust,
    Auto,
}

internal static unsafe class RustAsciiEngine
{
    private const uint ExpectedAbiVersion = 2;
    private const int InitialHitCapacityLimit = 1_048_576;
    private const string NativeLibraryName = "bstrings_core";

    private static int _enabled;
    private static int _strict;
    private static string _kernelDescription = "unavailable";
    private static string? _lastFailure;

    internal static bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    internal static string KernelDescription => _kernelDescription;

    internal static string? LastFailure => _lastFailure;

    internal sealed class HitBatch : IDisposable
    {
        private NativeHit[]? _buffer;
        private int _disposed;

        internal HitBatch(NativeHit[]? buffer, int count, long fileOffset)
        {
            _buffer = buffer;
            Count = count;
            FileOffset = fileOffset;
        }

        internal int Count { get; }

        internal long FileOffset { get; }

        internal ReadOnlySpan<NativeHit> Hits
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                return _buffer is null ? [] : _buffer.AsSpan(0, Count);
            }
        }

        internal List<StringHitPosition> ToList()
        {
            var hits = new List<StringHitPosition>(Count);
            foreach (var hit in Hits)
            {
                hits.Add(new StringHitPosition((int)hit.Start, (int)hit.Length, FileOffset));
            }
            return hits;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                ArrayPool<NativeHit>.Shared.Return(buffer);
            }
        }
    }

    internal static bool TryParseMode(
        string? value,
        out CpuStringEngineMode mode,
        out string? error
    )
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "dotnet":
                mode = CpuStringEngineMode.DotNet;
                error = null;
                return true;
            case "rust":
                mode = CpuStringEngineMode.Rust;
                error = null;
                return true;
            case "auto":
                mode = CpuStringEngineMode.Auto;
                error = null;
                return true;
            default:
                mode = CpuStringEngineMode.DotNet;
                error = "Invalid --cpu-engine value. Use dotnet, rust, or auto.";
                return false;
        }
    }

    internal static bool Configure(
        CpuStringEngineMode requestedMode,
        out string status,
        out string? error
    )
    {
        Volatile.Write(ref _enabled, 0);
        Volatile.Write(ref _strict, 0);
        _lastFailure = null;

        if (requestedMode == CpuStringEngineMode.DotNet)
        {
            status = "C#/.NET engine selected";
            error = null;
            return true;
        }

        if (!TryValidateNative(out var kernel, out var validationError))
        {
            _lastFailure = validationError;
            if (requestedMode == CpuStringEngineMode.Rust)
            {
                status = "Rust engine validation failed";
                error = validationError;
                return false;
            }

            status = $"Rust engine unavailable; using C#/.NET: {validationError}";
            error = null;
            return true;
        }

        _kernelDescription = kernel;
        Volatile.Write(ref _strict, requestedMode == CpuStringEngineMode.Rust ? 1 : 0);
        Volatile.Write(ref _enabled, 1);
        status =
            requestedMode == CpuStringEngineMode.Rust
                ? $"Rust engine validated (ABI {ExpectedAbiVersion}, {kernel})"
                : $"Rust engine validated (ABI {ExpectedAbiVersion}, {kernel}); C#/.NET fallback armed";
        error = null;
        return true;
    }

    internal static bool TryFindConfigured(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar,
        byte maxChar,
        out List<StringHitPosition> hits
    )
    {
        if (
            !TryRentConfigured(
                data,
                minLength,
                maxLength,
                fileOffset,
                minChar,
                maxChar,
                out var batch
            )
        )
        {
            hits = null!;
            return false;
        }

        using (batch)
        {
            hits = batch.ToList();
            return true;
        }
    }

    internal static bool TryRentConfigured(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar,
        byte maxChar,
        out HitBatch batch
    )
    {
        if (!IsEnabled)
        {
            batch = null!;
            return false;
        }

        if (
            TryRentNative(
                data,
                minLength,
                maxLength,
                fileOffset,
                minChar,
                maxChar,
                out batch,
                out var error
            )
        )
        {
            return true;
        }

        _lastFailure = error;
        if (Volatile.Read(ref _strict) != 0)
        {
            throw new InvalidOperationException($"The Rust ASCII engine failed: {error}");
        }

        if (Interlocked.Exchange(ref _enabled, 0) != 0)
        {
            Log.Warning(
                "Rust ASCII engine failed at runtime; falling back to C#/.NET: {Error}",
                error
            );
        }
        batch = null!;
        return false;
    }

    internal static bool TryFindNativeForTesting(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar,
        byte maxChar,
        out List<StringHitPosition> hits,
        out string? error
    )
    {
        if (
            !TryRentNativeForTesting(
                data,
                minLength,
                maxLength,
                fileOffset,
                minChar,
                maxChar,
                out var batch,
                out error
            )
        )
        {
            hits = null!;
            return false;
        }

        using (batch)
        {
            hits = batch.ToList();
            return true;
        }
    }

    internal static bool TryRentNativeForTesting(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar,
        byte maxChar,
        out HitBatch batch,
        out string? error
    )
    {
        return TryRentNative(
            data,
            minLength,
            maxLength,
            fileOffset,
            minChar,
            maxChar,
            out batch,
            out error
        );
    }

    private static bool TryValidateNative(out string kernel, out string error)
    {
        kernel = "unavailable";
        try
        {
            var actualAbiVersion = NativeMethods.AbiVersion();
            if (actualAbiVersion != ExpectedAbiVersion)
            {
                error =
                    $"native ABI {actualAbiVersion} does not match expected ABI {ExpectedAbiVersion}";
                return false;
            }

            var kernelCode = NativeMethods.AsciiKernel();
            kernel = kernelCode switch
            {
                2 => "AVX2",
                1 => "SSE2",
                0 => "scalar",
                var value => $"unknown kernel {value}",
            };
            if (kernelCode > 2)
            {
                error = $"native library returned unsupported ASCII kernel {kernelCode}";
                return false;
            }

            var random = new Random(0x42535452);
            int[] lengths = [0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 257, 4096];
            (byte Minimum, byte Maximum)[] ranges =
            [
                (0x00, 0x00),
                (0x20, 0x7E),
                (0x30, 0x39),
                (0x80, 0xFF),
                (0x00, 0xFF),
            ];

            foreach (var length in lengths)
            {
                var data = new byte[length];
                random.NextBytes(data);
                foreach (var range in ranges)
                {
                    const int minLength = 4;
                    const int maxLength = 37;
                    const long fileOffset = 0x1234_5678;
                    var managed = SearchCore.FindAsciiStringHitsManaged(
                        data,
                        minLength,
                        maxLength,
                        fileOffset,
                        range.Minimum,
                        range.Maximum
                    );
                    if (
                        !TryRentNative(
                            data,
                            minLength,
                            maxLength,
                            fileOffset,
                            range.Minimum,
                            range.Maximum,
                            out var nativeBatch,
                            out var nativeError
                        )
                    )
                    {
                        error = nativeError ?? "native startup parity call failed";
                        return false;
                    }

                    using (nativeBatch)
                    {
                        if (!HitsEqual(managed, nativeBatch))
                        {
                            error =
                                $"startup parity check failed for {length} bytes and range "
                                + $"0x{range.Minimum:X2}-0x{range.Maximum:X2}";
                            return false;
                        }
                    }
                }
            }

            error = string.Empty;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            error = $"{NativeLibraryName} was not found: {ex.Message}";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"{NativeLibraryName} is missing a required ABI export: {ex.Message}";
            return false;
        }
        catch (BadImageFormatException ex)
        {
            error = $"{NativeLibraryName} has the wrong architecture or format: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryRentNative(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar,
        byte maxChar,
        out HitBatch batch,
        out string? error
    )
    {
        NativeHit[]? output = null;
        try
        {
            if (data.Length == 0 || minChar > maxChar || minLength > data.Length)
            {
                batch = new HitBatch(null, 0, fileOffset);
                error = null;
                return true;
            }

            var effectiveMinimum = Math.Max(minLength, 1);
            var maximumPossibleHits = (int)((data.Length + 1L) / (effectiveMinimum + 1L));
            var estimatedCapacity = Math.Min(
                InitialHitCapacityLimit,
                Math.Max(4096, data.Length / 32 + 4096)
            );
            var requestedCapacity = Math.Min(maximumPossibleHits, estimatedCapacity);
            output = ArrayPool<NativeHit>.Shared.Rent(Math.Max(requestedCapacity, 1));

            var status = InvokeNative(
                data,
                minLength,
                maxLength,
                minChar,
                maxChar,
                output,
                out var requiredLength
            );
            if (requiredLength > (nuint)maximumPossibleHits || requiredLength > int.MaxValue)
            {
                batch = null!;
                error = "native call returned an impossible hit count";
                return false;
            }

            if (status == 2)
            {
                if (requiredLength <= (nuint)output.Length)
                {
                    batch = null!;
                    error = "native call reported insufficient capacity for a fitting result";
                    return false;
                }

                ArrayPool<NativeHit>.Shared.Return(output);
                output = null;
                output = ArrayPool<NativeHit>.Shared.Rent((int)requiredLength);
                status = InvokeNative(
                    data,
                    minLength,
                    maxLength,
                    minChar,
                    maxChar,
                    output,
                    out requiredLength
                );
            }

            if (
                status != 0
                || requiredLength > (nuint)output.Length
                || requiredLength > (nuint)maximumPossibleHits
            )
            {
                batch = null!;
                error =
                    status == 0
                        ? "native call returned more hits than fit in the output buffer"
                        : $"native call returned status {status}";
                return false;
            }

            for (var index = 0; index < (int)requiredLength; index++)
            {
                var nativeHit = output[index];
                var start = (int)nativeHit.Start;
                var length = (int)nativeHit.Length;
                if (
                    nativeHit.Start > int.MaxValue
                    || nativeHit.Length > int.MaxValue
                    || start > data.Length
                    || length > data.Length - start
                )
                {
                    batch = null!;
                    error = "native call returned an out-of-range hit";
                    return false;
                }
            }

            batch = new HitBatch(output, (int)requiredLength, fileOffset);
            output = null;
            error = null;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            batch = null!;
            error = $"{NativeLibraryName} was not found: {ex.Message}";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            batch = null!;
            error = $"{NativeLibraryName} is missing a required ABI export: {ex.Message}";
            return false;
        }
        catch (BadImageFormatException ex)
        {
            batch = null!;
            error = $"{NativeLibraryName} has the wrong architecture or format: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            batch = null!;
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            if (output is not null)
            {
                ArrayPool<NativeHit>.Shared.Return(output);
            }
        }
    }

    private static int InvokeNative(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        byte minChar,
        byte maxChar,
        NativeHit[] output,
        out nuint requiredLength
    )
    {
        nuint required = 0;
        fixed (byte* dataPointer = data)
        fixed (NativeHit* outputPointer = output)
        {
            var status = NativeMethods.FindAsciiHits(
                dataPointer,
                (nuint)data.Length,
                minLength,
                maxLength,
                minChar,
                maxChar,
                outputPointer,
                (nuint)output.Length,
                &required
            );
            requiredLength = required;
            return status;
        }
    }

    private static bool HitsEqual(
        IReadOnlyList<StringHitPosition> expected,
        HitBatch actual
    )
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        var actualHits = actual.Hits;
        for (var index = 0; index < expected.Count; index++)
        {
            if (
                expected[index].Start != actualHits[index].Start
                || expected[index].Length != actualHits[index].Length
                || expected[index].FileOffset != actual.FileOffset
            )
            {
                return false;
            }
        }

        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct NativeHit
    {
        internal readonly uint Start;
        internal readonly uint Length;
    }

    private static class NativeMethods
    {
        [DllImport(
            NativeLibraryName,
            EntryPoint = "bstrings_abi_version",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        internal static extern uint AbiVersion();

        [DllImport(
            NativeLibraryName,
            EntryPoint = "bstrings_ascii_kernel",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        internal static extern uint AsciiKernel();

        [DllImport(
            NativeLibraryName,
            EntryPoint = "bstrings_find_ascii_hits",
            CallingConvention = CallingConvention.Cdecl,
            ExactSpelling = true
        )]
        internal static extern int FindAsciiHits(
            byte* data,
            nuint dataLength,
            int minLength,
            int maxLength,
            byte minChar,
            byte maxChar,
            NativeHit* output,
            nuint outputCapacity,
            nuint* requiredLength
        );
    }
}
