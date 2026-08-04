#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct EnrichmentMergeStats(
    long OutputRecords,
    IReadOnlyList<long> InputRecords
);

internal static class EnrichmentMergeCore
{
    internal static async Task<EnrichmentMergeStats> ConcatenateAsync(
        IReadOnlyList<string> inputPaths,
        string outputPath,
        CancellationToken cancellationToken = default
    )
    {
        if (inputPaths.Count == 0)
        {
            throw new ArgumentException("At least one enrichment input is required.");
        }
        var outputFullPath = Path.GetFullPath(outputPath);
        var inputFullPaths = new List<string>(inputPaths.Count);
        foreach (var inputPath in inputPaths)
        {
            var fullPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Enrichment merge input was not found.", fullPath);
            }
            if (string.Equals(fullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Enrichment merge input and output paths must differ.");
            }
            inputFullPaths.Add(fullPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        var temporaryPath = outputFullPath + ".partial." + Guid.NewGuid().ToString("N");
        var inputCounts = new long[inputFullPaths.Count];
        long totalRecords = 0;
        try
        {
            await using (
                var output = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan
                )
            )
            {
                var buffer = new byte[1024 * 1024];
                for (var index = 0; index < inputFullPaths.Count; index++)
                {
                    await using var input = new FileStream(
                        inputFullPaths[index],
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.SequentialScan
                    );
                    var lastByte = -1;
                    int read;
                    while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        for (var offset = 0; offset < read; offset++)
                        {
                            if (buffer[offset] == (byte)'\n')
                            {
                                inputCounts[index]++;
                            }
                        }
                        lastByte = buffer[read - 1];
                    }
                    if (lastByte >= 0 && lastByte != (byte)'\n')
                    {
                        await output.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken);
                        inputCounts[index]++;
                    }
                    totalRecords += inputCounts[index];
                }
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, outputFullPath, overwrite: true);
            temporaryPath = string.Empty;
            return new EnrichmentMergeStats(totalRecords, inputCounts);
        }
        finally
        {
            if (temporaryPath.Length > 0)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
