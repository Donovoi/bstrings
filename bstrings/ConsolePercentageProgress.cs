#nullable enable

using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace bstrings;

internal sealed class ConsolePercentageProgress
{
    private readonly object _gate = new();
    private readonly TextWriter _writer;
    private readonly double _minimumPercentStep;
    private readonly TimeSpan _maximumSilence;
    private string? _activity;
    private double _lastPercent = -1;
    private long _lastTimestamp;

    internal ConsolePercentageProgress(
        TextWriter? writer = null,
        double minimumPercentStep = 1.0,
        TimeSpan? maximumSilence = null
    )
    {
        _writer = writer ?? Console.Error;
        _minimumPercentStep = minimumPercentStep;
        _maximumSilence = maximumSilence ?? TimeSpan.FromSeconds(5);
    }

    internal void Report(
        string activity,
        long completed,
        long total,
        string unit,
        string? detail = null,
        bool force = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activity);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        if (total <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(total));
        }

        completed = Math.Clamp(completed, 0, total);
        var percent = completed * 100.0 / total;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var activityChanged = !string.Equals(_activity, activity, StringComparison.Ordinal);
            var elapsed = _lastTimestamp == 0
                ? TimeSpan.MaxValue
                : Stopwatch.GetElapsedTime(_lastTimestamp, now);
            if (
                !force
                && !activityChanged
                && Math.Abs(percent - _lastPercent) < double.Epsilon
            )
            {
                return;
            }
            if (
                !force
                && !activityChanged
                && percent < 100
                && percent - _lastPercent < _minimumPercentStep
                && elapsed < _maximumSilence
            )
            {
                return;
            }

            _activity = activity;
            _lastPercent = percent;
            _lastTimestamp = now;
            var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $"; {detail}";
            _writer.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Progress: {activity}: {percent:F1}% ({completed:N0}/{total:N0} {unit}{suffix})"
                )
            );
            _writer.Flush();
        }
    }
}

internal static class ProgressHashing
{
    private const int BufferBytes = 4 * 1024 * 1024;

    internal static string ComputeSha256(Stream stream, Action<long, long>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new ArgumentException("Progress hashing requires a readable, seekable stream.", nameof(stream));
        }

        var total = stream.Length;
        var completed = stream.Position;
        progress?.Invoke(completed, total);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        try
        {
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer, 0, read);
                completed = checked(completed + read);
                progress?.Invoke(completed, total);
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
