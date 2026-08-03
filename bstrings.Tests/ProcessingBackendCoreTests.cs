using System.Text;
using Xunit;

namespace bstrings.Tests;

public class ProcessingBackendCoreTests
{
    [Theory]
    [InlineData(null, ProcessingMode.Auto)]
    [InlineData("", ProcessingMode.Auto)]
    [InlineData("AUTO", ProcessingMode.Auto)]
    [InlineData("cpu", ProcessingMode.Cpu)]
    [InlineData("gpu", ProcessingMode.Gpu)]
    [InlineData("hybrid", ProcessingMode.Hybrid)]
    [InlineData("gpu+cpu", ProcessingMode.Hybrid)]
    public void TryParseMode_AcceptsSupportedNames(string? value, ProcessingMode expected)
    {
        var parsed = ProcessingBackendCore.TryParseMode(value, out var actual, out var error);

        Assert.True(parsed);
        Assert.Null(error);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParseMode_RejectsUnknownName()
    {
        var parsed = ProcessingBackendCore.TryParseMode(
            "rapids",
            out var actual,
            out var error
        );

        Assert.False(parsed);
        Assert.Equal(ProcessingMode.Auto, actual);
        Assert.Contains("auto, cpu, gpu, or hybrid", error);
    }

    [Theory]
    [InlineData(ProcessingMode.Cpu, 16L * 1024 * 1024 * 1024, true, 8, ProcessingMode.Cpu)]
    [InlineData(ProcessingMode.Gpu, 1, true, 3, ProcessingMode.Gpu)]
    [InlineData(ProcessingMode.Hybrid, 1, true, 3, ProcessingMode.Hybrid)]
    [InlineData(ProcessingMode.Auto, 1L * 1024 * 1024 * 1024, true, 8, ProcessingMode.Cpu)]
    [InlineData(ProcessingMode.Auto, 4L * 1024 * 1024 * 1024, false, 8, ProcessingMode.Cpu)]
    [InlineData(ProcessingMode.Auto, 4L * 1024 * 1024 * 1024, true, 3, ProcessingMode.Cpu)]
    [InlineData(ProcessingMode.Auto, 100L * 1024 * 1024 * 1024, true, 8, ProcessingMode.Cpu)]
    public void ResolveMode_KeepsAutoConservativeUntilCalibration(
        ProcessingMode requested,
        long fileSizeBytes,
        bool gpuAvailable,
        int minLength,
        ProcessingMode expected
    )
    {
        Assert.Equal(
            expected,
            ProcessingBackendCore.ResolveMode(
                requested,
                fileSizeBytes,
                gpuAvailable,
                minLength
            )
        );
    }

    [Theory]
    [InlineData(1.0, 0.5, 0.8, 0.1, ProcessingMode.Gpu)]
    [InlineData(1.0, 0.8, 0.5, 0.1, ProcessingMode.Hybrid)]
    [InlineData(1.0, 0.96, 0.99, 0.0, ProcessingMode.Cpu)]
    [InlineData(1.0, 0.5, 0.8, 60.0, ProcessingMode.Cpu)]
    [InlineData(double.NaN, 0.5, 0.4, 0.0, ProcessingMode.Cpu)]
    public void SelectCalibratedMode_RequiresProjectedFivePercentWin(
        double cpuSeconds,
        double gpuSeconds,
        double hybridSeconds,
        double gpuStartupSeconds,
        ProcessingMode expected
    )
    {
        Assert.Equal(
            expected,
            ProcessingBackendCore.SelectCalibratedMode(
                cpuSeconds,
                gpuSeconds,
                hybridSeconds,
                gpuStartupSeconds,
                fileSizeBytes: 1024,
                sampledBytes: 1024
            )
        );
    }

    [Theory]
    [InlineData(0, 3, 1)]
    [InlineData(16, 3, 5)]
    [InlineData(16, 1, 9)]
    [InlineData(1_000_000, 8, 111_112)]
    public void CalculateMaximumHitCount_IsSafeAndBounded(
        int unitCount,
        int minLength,
        int expected
    )
    {
        Assert.Equal(
            expected,
            GpuStringScanner.CalculateMaximumHitCount(unitCount, minLength)
        );
    }

    [Fact]
    public void CudaScanner_MatchesCpuAcrossRangesOffsetsAndBoundaries_WhenAvailable()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (!GpuStringScanner.TryCreate(out var scanner, out var failure))
        {
            Assert.Skip($"CUDA is unavailable on this test host: {failure}");
        }

        using (scanner)
        {
            var cases = new[]
            {
                new ScanCase(
                    [
                        0,
                        (byte)'A',
                        (byte)'B',
                        (byte)'C',
                        (byte)'D',
                        0,
                        (byte)'x',
                        0,
                        (byte)'y',
                        0,
                        (byte)'z',
                        0,
                    ],
                    0x100,
                    false,
                    3,
                    3,
                    true,
                    true,
                    true,
                    1252,
                    "[\\x20-\\x7E]",
                    "[\\u0020-\\u007E]"
                ),
                new ScanCase(
                    [0, 0x80, 0x80, 0x80, 0, 0xFF, 0xFF, 0xFF, 0],
                    0x200,
                    true,
                    3,
                    -1,
                    true,
                    false,
                    true,
                    1252,
                    "[\\x80-\\xFF]",
                    "[\\u0020-\\u007E]"
                ),
                new ScanCase(
                    Encoding.Unicode.GetBytes("\0αβγ\0δεζη\0"),
                    0x300,
                    true,
                    3,
                    3,
                    false,
                    true,
                    false,
                    1252,
                    "[\\x20-\\x7E]",
                    "[\\u03B1-\\u03C9]"
                ),
            };

            foreach (var scanCase in cases)
            {
                var expected = ChunkProcessingCore.ProcessChunk(
                    scanCase.Data,
                    scanCase.Offset,
                    scanCase.IsBoundary,
                    scanCase.MinLength,
                    scanCase.MaxLength,
                    scanCase.Ascii,
                    scanCase.Unicode,
                    scanCase.IncludeOffset,
                    scanCase.AsciiRange,
                    scanCase.UnicodeRange,
                    scanCase.CodePage
                );
                var actual = scanner!.ProcessChunk(
                    scanCase.Data,
                    scanCase.Offset,
                    scanCase.IsBoundary,
                    scanCase.MinLength,
                    scanCase.MaxLength,
                    scanCase.Ascii,
                    scanCase.Unicode,
                    scanCase.IncludeOffset,
                    scanCase.CodePage,
                    scanCase.AsciiRange,
                    scanCase.UnicodeRange
                );

                Assert.Equal(expected, actual);

                var expectedStructured = ChunkProcessingCore.ProcessStructuredChunk(
                    scanCase.Data,
                    scanCase.Offset,
                    scanCase.IsBoundary,
                    scanCase.MinLength,
                    scanCase.MaxLength,
                    scanCase.Ascii,
                    scanCase.Unicode,
                    scanCase.AsciiRange,
                    scanCase.UnicodeRange,
                    scanCase.CodePage
                );
                var actualStructured = scanner.ProcessStructuredChunk(
                    scanCase.Data,
                    scanCase.Offset,
                    scanCase.IsBoundary,
                    scanCase.MinLength,
                    scanCase.MaxLength,
                    scanCase.Ascii,
                    scanCase.Unicode,
                    scanCase.CodePage,
                    scanCase.AsciiRange,
                    scanCase.UnicodeRange
                );

                Assert.Equal(expectedStructured, actualStructured);
            }
        }
    }

    private sealed record ScanCase(
        byte[] Data,
        long Offset,
        bool IsBoundary,
        int MinLength,
        int MaxLength,
        bool Ascii,
        bool Unicode,
        bool IncludeOffset,
        int CodePage,
        string AsciiRange,
        string UnicodeRange
    );
}
