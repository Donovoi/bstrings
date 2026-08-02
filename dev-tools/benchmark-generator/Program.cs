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
        var denseOutput = args.Skip(2).Any(
            argument => argument.Equals("--dense-output", StringComparison.OrdinalIgnoreCase)
        );
        var denseRecordLength = args
            .Skip(2)
            .Select(argument =>
                argument.StartsWith("--dense-record-length=", StringComparison.OrdinalIgnoreCase)
                    ? argument[(argument.IndexOf('=') + 1)..]
                    : null
            )
            .Where(value => value is not null)
            .Select(value => int.Parse(value!))
            .SingleOrDefault();
        if (denseRecordLength is > 0 and < 96)
        {
            throw new ArgumentOutOfRangeException(
                nameof(args),
                "Dense record length must be at least 96 characters."
            );
        }
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

        if (denseOutput)
        {
            var recordCount = WriteDenseOutputFixture(stream, fileSize, denseRecordLength);
            Console.WriteLine(
                $"Created {outputPath} ({sizeMb} MB, {recordCount:N0} dense output records)."
            );
            return;
        }

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

    private static long WriteDenseOutputFixture(
        FileStream stream,
        long fileSize,
        int denseRecordLength
    )
    {
        long recordCount = 0;

        while (stream.Position < fileSize)
        {
            var record =
                $"record-{recordCount:D10} contact=user{recordCount:D10}@example.test "
                + $"url=https://example.test/item/{recordCount:X10}";
            if (denseRecordLength > record.Length)
            {
                record += " " + new string('x', denseRecordLength - record.Length - 1);
            }
            var recordBytes =
                recordCount % 2 == 0
                    ? Encoding.ASCII.GetBytes(record)
                    : Encoding.Unicode.GetBytes(record);
            var separatorLength = recordCount % 2 == 0 ? 1 : 2;

            if (recordBytes.Length + separatorLength > fileSize - stream.Position)
            {
                break;
            }

            stream.Write(recordBytes);
            stream.WriteByte(0);
            if (separatorLength == 2)
            {
                stream.WriteByte(0);
            }
            recordCount++;
        }

        stream.SetLength(fileSize);
        return recordCount;
    }
}
