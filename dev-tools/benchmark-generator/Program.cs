using System.Text;

namespace benchmark_generator;

internal static class Program
{
    private const int DefaultSizeMb = 512;
    private const int BufferSize = 1024 * 1024;

    private static void Main(string[] args)
    {
        var sizeMb =
            args.Length > 0 && int.TryParse(args[0], out var requestedSizeMb)
                ? requestedSizeMb
                : DefaultSizeMb;
        if (sizeMb < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Size must be at least 1 MB.");
        }

        var outputPath = args.Length > 1 ? Path.GetFullPath(args[1]) : "benchmark.dmp";
        var fileSize = checked((long)sizeMb * 1024 * 1024);
        var random = new Random(0x42);
        var buffer = new byte[BufferSize];

        using var stream = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            BufferSize,
            FileOptions.SequentialScan
        );

        var remaining = fileSize;
        while (remaining > 0)
        {
            var bytesToWrite = (int)Math.Min(buffer.Length, remaining);
            random.NextBytes(buffer.AsSpan(0, bytesToWrite));

            for (var index = 0; index < bytesToWrite; index++)
            {
                if (buffer[index] is >= 0x20 and <= 0x7E)
                {
                    buffer[index] = (byte)(buffer[index] - 0x20);
                }
            }

            stream.Write(buffer, 0, bytesToWrite);
            remaining -= bytesToWrite;
        }

        if (fileSize >= 21L * 1024 * 1024)
        {
            var boundaryText = Encoding.Unicode.GetBytes("DFIR with bstrings rocks");
            stream.Position = 20L * 1024 * 1024 - boundaryText.Length / 2;
            stream.Write(boundaryText);
        }

        if (fileSize > 0x0400_0000 + 8)
        {
            var uncPathText = Encoding.ASCII.GetBytes(@"\\.\root");
            stream.Position = 0x0400_0000;
            stream.Write(uncPathText);
        }

        Console.WriteLine($"Created {outputPath} ({sizeMb} MB).");
    }
}
