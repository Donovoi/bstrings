#nullable enable

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct TranslationCandidateStats(long InputRecords, long CandidateRecords);

internal static class TranslationCandidateCore
{
    internal static async Task<TranslationCandidateStats> FilterEligibleAsync(
        string inputPath,
        string outputPath,
        int minimumCharacters,
        int maximumCharacters,
        CancellationToken cancellationToken = default,
        Action<long, long>? progress = null
    )
    {
        if (minimumCharacters < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumCharacters));
        }
        if (maximumCharacters < minimumCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        var inputFullPath = Path.GetFullPath(inputPath);
        var outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(inputFullPath, outputFullPath, PathComparison))
        {
            throw new ArgumentException("The candidate input and output paths must differ.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputFullPath)!);
        var temporaryPath = outputFullPath + ".partial." + Guid.NewGuid().ToString("N");
        var inputBytes = new FileInfo(inputFullPath).Length;
        progress?.Invoke(0, inputBytes);
        long inputRecords = 0;
        long candidateRecords = 0;
        try
        {
            await using (
                var writer = new StreamWriter(
                    new FileStream(
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        1024 * 1024,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    ),
                    new UTF8Encoding(false),
                    1024 * 1024
                )
            )
            {
                writer.NewLine = "\n";
                await foreach (
                    var item in EnrichmentJsonlReader.ReadAsync(inputFullPath, cancellationToken)
                )
                {
                    inputRecords++;
                    if (
                        item.Record.Transform is not null
                        || !string.IsNullOrWhiteSpace(item.Record.ParentRecordId)
                    )
                    {
                        throw new InvalidDataException(
                            $"Raw translation input at line {item.LineNumber:N0} is already transformed."
                        );
                    }

                    var text = item.Record.Text!;
                    if (
                        TranslationTextEligibility.ShouldTranslate(
                            text,
                            minimumCharacters,
                            maximumCharacters
                        )
                    )
                    {
                        await writer.WriteLineAsync(item.Json.AsMemory(), cancellationToken);
                        candidateRecords++;
                    }
                    progress?.Invoke(Math.Min(inputBytes, item.StreamPosition), inputBytes);
                }
                await writer.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, outputFullPath, overwrite: true);
            progress?.Invoke(inputBytes, inputBytes);
            temporaryPath = string.Empty;
            return new TranslationCandidateStats(inputRecords, candidateRecords);
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

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
