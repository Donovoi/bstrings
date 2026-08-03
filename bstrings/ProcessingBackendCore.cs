#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using System.Threading.Tasks;

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
    // Below this point, measuring CUDA costs more than it can recover on the
    // validated benchmark hardware. Larger files race all available backends
    // over representative samples instead of relying on a fixed crossover.
    internal static long AutoCalibrationThresholdBytes =>
        Avx2.IsSupported && Environment.ProcessorCount >= 8
            ? 128L * 1024 * 1024 * 1024
            : Sse41.IsSupported && Environment.ProcessorCount >= 4
                ? 32L * 1024 * 1024 * 1024
                : 8L * 1024 * 1024 * 1024;
    internal const int CalibrationSampleBytes = 16 * 1024 * 1024;
    internal const int CalibrationSampleCount = 8;
    internal const int MaximumCalibrationHitsPerSample = 250_000;
    internal const double RequiredAcceleratorMargin = 0.05;

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

        return ProcessingMode.Cpu;
    }

    internal static ProcessingMode SelectCalibratedMode(
        double cpuSeconds,
        double gpuSeconds,
        double hybridSeconds,
        double gpuStartupSeconds,
        long fileSizeBytes,
        long sampledBytes
    )
    {
        if (
            cpuSeconds <= 0
            || gpuSeconds <= 0
            || hybridSeconds <= 0
            || fileSizeBytes <= 0
            || sampledBytes <= 0
            || !double.IsFinite(cpuSeconds)
            || !double.IsFinite(gpuSeconds)
            || !double.IsFinite(hybridSeconds)
            || !double.IsFinite(gpuStartupSeconds)
        )
        {
            return ProcessingMode.Cpu;
        }

        var scale = (double)fileSizeBytes / sampledBytes;
        var cpuProjected = cpuSeconds * scale;
        var gpuProjected = gpuSeconds * scale + Math.Max(0, gpuStartupSeconds);
        var hybridProjected = hybridSeconds * scale + Math.Max(0, gpuStartupSeconds);
        var accelerated = gpuProjected <= hybridProjected
            ? (Mode: ProcessingMode.Gpu, Seconds: gpuProjected)
            : (Mode: ProcessingMode.Hybrid, Seconds: hybridProjected);

        return accelerated.Seconds < cpuProjected * (1 - RequiredAcceleratorMargin)
            ? accelerated.Mode
            : ProcessingMode.Cpu;
    }
}

internal sealed record BackendCalibrationResult(
    ProcessingMode Mode,
    double CpuSeconds,
    double GpuSeconds,
    double HybridSeconds,
    long SampledBytes,
    int MaximumHitsPerSample,
    string Reason
);

internal sealed class ProcessingBackendSession : IDisposable
{
    private readonly GpuStringScanner? _gpuScanner;
    private int _gpuEnabled;
    private int _gpuFailureReported;
    private long _cpuChunks;
    private long _gpuChunks;
    private readonly double _gpuStartupSeconds;

    private ProcessingBackendSession(
        ProcessingMode requestedMode,
        GpuStringScanner? gpuScanner,
        string statusMessage,
        double gpuStartupSeconds = 0
    )
    {
        RequestedMode = requestedMode;
        _gpuScanner = gpuScanner;
        _gpuEnabled = gpuScanner is null ? 0 : 1;
        StatusMessage = statusMessage;
        _gpuStartupSeconds = gpuStartupSeconds;
    }

    internal ProcessingMode RequestedMode { get; }

    internal string StatusMessage { get; }

    internal string? LastCalibrationMessage { get; private set; }

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
                $"CPU: {SearchCore.CpuKernelDescription}"
            );
        }

        var shouldProbeGpu =
            requestedMode is ProcessingMode.Gpu or ProcessingMode.Hybrid
            || (
                largestInputBytes >= ProcessingBackendCore.AutoCalibrationThresholdBytes
            );

        if (!shouldProbeGpu)
        {
            return new ProcessingBackendSession(
                requestedMode,
                null,
                $"Auto selected CPU ({SearchCore.CpuKernelDescription}) without CUDA startup; adaptive calibration starts at {ProcessingBackendCore.AutoCalibrationThresholdBytes / (1024 * 1024 * 1024)} GiB"
            );
        }

        var startup = Stopwatch.StartNew();
        if (!GpuStringScanner.TryCreate(out var scanner, out var failure))
        {
            startup.Stop();
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
        startup.Stop();

        return new ProcessingBackendSession(
            requestedMode,
            scanner,
            $"CUDA validated on {scanner!.DeviceName}; CPU ({SearchCore.CpuKernelDescription}) and GPU results matched",
            startup.Elapsed.TotalSeconds
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

    internal BackendCalibrationResult CalibrateForFile(
        string filePath,
        long fileSizeBytes,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        int codePage,
        string asciiRange,
        string unicodeRange
    )
    {
        if (RequestedMode != ProcessingMode.Auto)
        {
            return SetCalibration(
                new BackendCalibrationResult(
                    RequestedMode,
                    0,
                    0,
                    0,
                    0,
                    0,
                    $"Explicit {RequestedMode.ToString().ToLowerInvariant()} mode"
                )
            );
        }
        if (
            !IsGpuEnabled
            || _gpuScanner is null
            || fileSizeBytes < ProcessingBackendCore.AutoCalibrationThresholdBytes
            || (!asciiSearch && !unicodeSearch)
        )
        {
            return SetCalibration(
                new BackendCalibrationResult(
                    ProcessingMode.Cpu,
                    0,
                    0,
                    0,
                    0,
                    0,
                    IsGpuEnabled
                        ? "Input is below the adaptive CUDA calibration threshold"
                        : "CUDA is unavailable or was intentionally not started"
                )
            );
        }

        var samples = ReadCalibrationSamples(filePath, fileSizeBytes);
        var sampledBytes = samples.Sum(sample => (long)sample.Data.Length);
        var maximumHits = samples.Max(sample =>
        {
            var count = 0;
            if (asciiSearch)
            {
                var (minimum, maximum) = SearchCore.ParseCharRange(asciiRange);
                count += SearchCore
                    .FindAsciiStringHits(
                        sample.Data,
                        minLength,
                        maxLength,
                        sample.Offset,
                        minimum,
                        maximum
                    )
                    .Count;
            }
            if (unicodeSearch)
            {
                var (minimum, maximum) = SearchCore.ParseUnicodeRange(unicodeRange);
                count += SearchCore
                    .FindUnicodeStringHits(
                        sample.Data,
                        minLength,
                        maxLength,
                        sample.Offset,
                        minimum,
                        maximum
                    )
                    .Count;
            }
            return count;
        });
        if (maximumHits > ProcessingBackendCore.MaximumCalibrationHitsPerSample)
        {
            return SetCalibration(
                new BackendCalibrationResult(
                    ProcessingMode.Cpu,
                    0,
                    0,
                    0,
                    sampledBytes,
                    maximumHits,
                    "High extracted-run density makes accelerator result transfer unsafe to probe"
                )
            );
        }

        var expected = RunCpuSamples(
            samples,
            minLength,
            maxLength,
            asciiSearch,
            unicodeSearch,
            codePage,
            asciiRange,
            unicodeRange
        );
        var cpuTimings = new double[3];
        var gpuTimings = new double[3];
        var hybridTimings = new double[3];
        for (var round = 0; round < 3; round++)
        {
            var modes = new[]
            {
                ProcessingMode.Cpu,
                ProcessingMode.Gpu,
                ProcessingMode.Hybrid,
            };
            for (var order = 0; order < modes.Length; order++)
            {
                var mode = modes[(order + round) % modes.Length];
                var stopwatch = Stopwatch.StartNew();
                List<string>[] actual;
                try
                {
                    actual = mode switch
                    {
                        ProcessingMode.Cpu => RunCpuSamples(
                            samples,
                            minLength,
                            maxLength,
                            asciiSearch,
                            unicodeSearch,
                            codePage,
                            asciiRange,
                            unicodeRange
                        ),
                        ProcessingMode.Gpu => RunGpuSamples(
                            samples,
                            minLength,
                            maxLength,
                            asciiSearch,
                            unicodeSearch,
                            codePage,
                            asciiRange,
                            unicodeRange
                        ),
                        _ => RunHybridSamples(
                            samples,
                            minLength,
                            maxLength,
                            asciiSearch,
                            unicodeSearch,
                            codePage,
                            asciiRange,
                            unicodeRange
                        ),
                    };
                }
                catch (Exception exception) when (mode != ProcessingMode.Cpu)
                {
                    stopwatch.Stop();
                    DisableGpuAfterFailure(exception, null);
                    return SetCalibration(
                        new BackendCalibrationResult(
                            ProcessingMode.Cpu,
                            0,
                            0,
                            0,
                            sampledBytes,
                            maximumHits,
                            $"{mode} calibration failed with {exception.GetType().Name}; auto safely selected CPU"
                        )
                    );
                }
                stopwatch.Stop();
                if (!SamplesEqual(expected, actual))
                {
                    DisableGpuAfterFailure(
                        new InvalidOperationException(
                            $"{mode} calibration output did not match CPU"
                        ),
                        null
                    );
                    return SetCalibration(
                        new BackendCalibrationResult(
                            ProcessingMode.Cpu,
                            0,
                            0,
                            0,
                            sampledBytes,
                            maximumHits,
                            $"{mode} failed exact calibration parity"
                        )
                    );
                }

                switch (mode)
                {
                    case ProcessingMode.Cpu:
                        cpuTimings[round] = stopwatch.Elapsed.TotalSeconds;
                        break;
                    case ProcessingMode.Gpu:
                        gpuTimings[round] = stopwatch.Elapsed.TotalSeconds;
                        break;
                    case ProcessingMode.Hybrid:
                        hybridTimings[round] = stopwatch.Elapsed.TotalSeconds;
                        break;
                }
            }
        }

        var cpuSeconds = Median(cpuTimings);
        var gpuSeconds = Median(gpuTimings);
        var hybridSeconds = Median(hybridTimings);
        var selected = ProcessingBackendCore.SelectCalibratedMode(
            cpuSeconds,
            gpuSeconds,
            hybridSeconds,
            _gpuStartupSeconds,
            fileSizeBytes,
            sampledBytes
        );
        return SetCalibration(
            new BackendCalibrationResult(
                selected,
                cpuSeconds,
                gpuSeconds,
                hybridSeconds,
                sampledBytes,
                maximumHits,
                $"Selected {selected.ToString().ToLowerInvariant()} from three rotated exact-parity sample rounds"
            )
        );
    }

    private BackendCalibrationResult SetCalibration(BackendCalibrationResult result)
    {
        LastCalibrationMessage =
            $"{result.Reason}; sample={result.SampledBytes / (1024.0 * 1024):N1} MiB, "
            + $"hits/sample<={result.MaximumHitsPerSample:N0}, "
            + $"cpu={result.CpuSeconds * 1000:N1} ms, gpu={result.GpuSeconds * 1000:N1} ms, "
            + $"hybrid={result.HybridSeconds * 1000:N1} ms, "
            + $"CUDA startup={_gpuStartupSeconds * 1000:N1} ms";
        return result;
    }

    private static List<CalibrationSample> ReadCalibrationSamples(
        string filePath,
        long fileSizeBytes
    )
    {
        var sampleSize = (int)Math.Min(
            ProcessingBackendCore.CalibrationSampleBytes,
            fileSizeBytes
        );
        var sampleCount = (int)Math.Min(
            ProcessingBackendCore.CalibrationSampleCount,
            Math.Max(1, fileSizeBytes / Math.Max(1, sampleSize))
        );
        var samples = new List<CalibrationSample>(sampleCount);
        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            1024 * 1024,
            FileOptions.RandomAccess
        );
        for (var index = 0; index < sampleCount; index++)
        {
            var maximumOffset = Math.Max(0, fileSizeBytes - sampleSize);
            var offset = sampleCount == 1
                ? 0
                : maximumOffset * index / (sampleCount - 1);
            offset &= ~1L;
            stream.Position = offset;
            var data = new byte[sampleSize];
            stream.ReadExactly(data);
            samples.Add(new CalibrationSample(offset, data));
        }
        return samples;
    }

    private static List<string>[] RunCpuSamples(
        IReadOnlyList<CalibrationSample> samples,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        int codePage,
        string asciiRange,
        string unicodeRange
    )
    {
        return Task.WhenAll(
                samples.Select(sample =>
                    Task.Run(() =>
                        ChunkProcessingCore.ProcessChunk(
                            sample.Data,
                            sample.Offset,
                            false,
                            minLength,
                            maxLength,
                            asciiSearch,
                            unicodeSearch,
                            false,
                            asciiRange,
                            unicodeRange,
                            codePage
                        )
                    )
                )
            )
            .GetAwaiter()
            .GetResult();
    }

    private List<string>[] RunGpuSamples(
        IReadOnlyList<CalibrationSample> samples,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        int codePage,
        string asciiRange,
        string unicodeRange
    )
    {
        return Task.WhenAll(
                samples.Select(sample =>
                    Task.Run(() =>
                        _gpuScanner!.ProcessChunk(
                            sample.Data,
                            sample.Offset,
                            false,
                            minLength,
                            maxLength,
                            asciiSearch,
                            unicodeSearch,
                            false,
                            codePage,
                            asciiRange,
                            unicodeRange
                        )
                    )
                )
            )
            .GetAwaiter()
            .GetResult();
    }

    private List<string>[] RunHybridSamples(
        IReadOnlyList<CalibrationSample> samples,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        int codePage,
        string asciiRange,
        string unicodeRange
    )
    {
        var gpuSamples = Math.Min(2, samples.Count);
        return Task.WhenAll(
                samples.Select((sample, index) =>
                    Task.Run(() =>
                        index < gpuSamples
                            ? _gpuScanner!.ProcessChunk(
                                sample.Data,
                                sample.Offset,
                                false,
                                minLength,
                                maxLength,
                                asciiSearch,
                                unicodeSearch,
                                false,
                                codePage,
                                asciiRange,
                                unicodeRange
                            )
                            : ChunkProcessingCore.ProcessChunk(
                                sample.Data,
                                sample.Offset,
                                false,
                                minLength,
                                maxLength,
                                asciiSearch,
                                unicodeSearch,
                                false,
                                asciiRange,
                                unicodeRange,
                                codePage
                            )
                    )
                )
            )
            .GetAwaiter()
            .GetResult();
    }

    private static bool SamplesEqual(
        IReadOnlyList<List<string>> expected,
        IReadOnlyList<List<string>> actual
    )
    {
        return expected.Count == actual.Count
            && expected
                .Select((value, index) => value.SequenceEqual(actual[index]))
                .All(equal => equal);
    }

    private static double Median(double[] values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        return ordered[ordered.Length / 2];
    }

    private sealed record CalibrationSample(long Offset, byte[] Data);

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
