#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;

namespace bstrings;

public enum ProcessingMode
{
    Auto,
    Cpu,
    Gpu,
    Hybrid,
}

internal static class ProcessingBackendCore
{
    // Local benchmarks show CPU wins below this point once CUDA startup,
    // hit transfer, and string materialization are included.
    internal const long AutoGpuThresholdBytes = 4L * 1024 * 1024 * 1024;
    internal const int AutoGpuMinimumStringLength = 8;

    internal static bool TryParseMode(
        string? value,
        out ProcessingMode mode,
        out string? error
    )
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "auto":
                mode = ProcessingMode.Auto;
                error = null;
                return true;
            case "cpu":
                mode = ProcessingMode.Cpu;
                error = null;
                return true;
            case "gpu":
                mode = ProcessingMode.Gpu;
                error = null;
                return true;
            case "hybrid":
            case "gpu+cpu":
            case "cpu+gpu":
                mode = ProcessingMode.Hybrid;
                error = null;
                return true;
            default:
                mode = ProcessingMode.Auto;
                error =
                    $"Unknown processor mode '{value}'. Use auto, cpu, gpu, or hybrid.";
                return false;
        }
    }

    internal static ProcessingMode ResolveMode(
        ProcessingMode requestedMode,
        long fileSizeBytes,
        bool gpuAvailable,
        int minLength
    )
    {
        if (requestedMode != ProcessingMode.Auto)
        {
            return requestedMode;
        }

        return gpuAvailable
                && fileSizeBytes >= AutoGpuThresholdBytes
                && minLength >= AutoGpuMinimumStringLength
            ? ProcessingMode.Gpu
            : ProcessingMode.Cpu;
    }
}

internal sealed class ProcessingBackendSession : IDisposable
{
    private readonly GpuStringScanner? _gpuScanner;
    private int _gpuEnabled;
    private int _gpuFailureReported;
    private long _cpuChunks;
    private long _gpuChunks;

    private ProcessingBackendSession(
        ProcessingMode requestedMode,
        GpuStringScanner? gpuScanner,
        string statusMessage
    )
    {
        RequestedMode = requestedMode;
        _gpuScanner = gpuScanner;
        _gpuEnabled = gpuScanner is null ? 0 : 1;
        StatusMessage = statusMessage;
    }

    internal ProcessingMode RequestedMode { get; }

    internal string StatusMessage { get; }

    internal bool IsGpuEnabled => Volatile.Read(ref _gpuEnabled) != 0;

    internal string? GpuDeviceName => _gpuScanner?.DeviceName;

    internal long CpuChunks => Interlocked.Read(ref _cpuChunks);

    internal long GpuChunks => Interlocked.Read(ref _gpuChunks);

    internal static ProcessingBackendSession Create(
        ProcessingMode requestedMode,
        long largestInputBytes,
        int minLength = 3
    )
    {
        if (requestedMode == ProcessingMode.Cpu)
        {
            return new ProcessingBackendSession(
                requestedMode,
                null,
                "CPU: optimized SIMD scanner"
            );
        }

        var shouldProbeGpu =
            requestedMode is ProcessingMode.Gpu or ProcessingMode.Hybrid
            || (
                largestInputBytes >= ProcessingBackendCore.AutoGpuThresholdBytes
                && minLength >= ProcessingBackendCore.AutoGpuMinimumStringLength
            );

        if (!shouldProbeGpu)
        {
            return new ProcessingBackendSession(
                requestedMode,
                null,
                $"Auto selected CPU without CUDA startup; measured CUDA crossover requires at least {ProcessingBackendCore.AutoGpuThresholdBytes / (1024 * 1024 * 1024)} GiB and -m {ProcessingBackendCore.AutoGpuMinimumStringLength}"
            );
        }

        if (!GpuStringScanner.TryCreate(out var scanner, out var failure))
        {
            if (requestedMode is ProcessingMode.Gpu or ProcessingMode.Hybrid)
            {
                throw new InvalidOperationException(
                    $"The requested {requestedMode.ToString().ToLowerInvariant()} path is unavailable: {failure}"
                );
            }

            return new ProcessingBackendSession(
                requestedMode,
                null,
                $"Auto fell back to CPU because CUDA validation failed: {failure}"
            );
        }

        return new ProcessingBackendSession(
            requestedMode,
            scanner,
            $"CUDA validated on {scanner!.DeviceName}; CPU SIMD and GPU results matched"
        );
    }

    internal ProcessingMode ResolveForFile(long fileSizeBytes, int minLength)
    {
        return ProcessingBackendCore.ResolveMode(
            RequestedMode,
            fileSizeBytes,
            IsGpuEnabled,
            minLength
        );
    }

    internal List<string> ProcessGpuChunk(
        Program.DataChunk chunk,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool includeOffset,
        int codePage,
        string asciiRange,
        string unicodeRange
    )
    {
        if (!IsGpuEnabled || _gpuScanner is null)
        {
            throw new InvalidOperationException("The CUDA processing path is not available.");
        }

        var results = _gpuScanner.ProcessChunk(
            chunk.Data.AsSpan(0, chunk.ValidBytes),
            chunk.FileOffset,
            chunk.IsBoundaryChunk,
            minLength,
            maxLength,
            asciiSearch,
            unicodeSearch,
            includeOffset,
            codePage,
            asciiRange,
            unicodeRange,
            chunk.SuppressLeadingFragment,
            chunk.SuppressTrailingFragment,
            chunk.BoundaryCrossingOffset
        );
        Interlocked.Increment(ref _gpuChunks);
        return results;
    }

    internal void RecordCpuChunk()
    {
        Interlocked.Increment(ref _cpuChunks);
    }

    internal bool DisableGpuAfterFailure(Exception exception, Action<string>? report)
    {
        if (Interlocked.Exchange(ref _gpuEnabled, 0) == 0)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _gpuFailureReported, 1) == 0)
        {
            report?.Invoke(
                $"CUDA processing failed ({exception.GetType().Name}: {exception.Message}); remaining hybrid work will use CPU."
            );
        }

        return true;
    }

    public void Dispose()
    {
        _gpuScanner?.Dispose();
    }
}
