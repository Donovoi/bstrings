using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace bstrings;

internal static class ResultCollectionCore
{
    internal static async Task<long> CollectStreamingAsync(
        IAsyncEnumerable<List<string>> chunkResults,
        StreamWriter outputWriter,
        HashSet<string> resultsSet,
        int flushBatchSize = 1000
    )
    {
        int batchCount = 0;
        long totalResultCount = 0;

        await foreach (var results in chunkResults)
        {
            foreach (var result in results)
            {
                if (resultsSet != null)
                {
                    if (!resultsSet.Add(result))
                    {
                        continue;
                    }
                }

                totalResultCount++;

                if (outputWriter != null)
                {
                    await outputWriter.WriteLineAsync(result);
                    batchCount++;

                    if (batchCount >= flushBatchSize)
                    {
                        await outputWriter.FlushAsync();
                        batchCount = 0;
                    }
                }
            }

            results.Clear();
        }

        if (outputWriter != null && batchCount > 0)
        {
            await outputWriter.FlushAsync();
        }

        return totalResultCount;
    }

    internal static async Task CollectLimitedAsync(
        IAsyncEnumerable<List<string>> chunkResults,
        List<string> allResults,
        bool debug,
        int maxResultsInMemory = int.MaxValue,
        Action<string> debugLogger = null
    )
    {
        var truncated = false;

        await foreach (var results in chunkResults)
        {
            if (
                !truncated
                && allResults.Count <= maxResultsInMemory
                && results.Count <= maxResultsInMemory - allResults.Count
            )
            {
                allResults.AddRange(results);
            }
            else if (!truncated)
            {
                var availableSpace = maxResultsInMemory - allResults.Count;
                if (availableSpace > 0)
                {
                    allResults.AddRange(results.Take(availableSpace));
                }

                if (debug)
                {
                    (debugLogger ?? Console.Error.WriteLine)(
                        $"Result collection truncated at {maxResultsInMemory} results to prevent excessive memory usage"
                    );
                }
                truncated = true;
            }

            results.Clear();
        }
    }
}
