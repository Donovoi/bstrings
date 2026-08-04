using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings.Rapids
{
    /// <summary>
    /// RAPIDS integration for enhanced GPU string processing using external Python process
    /// </summary>
    public static class RapidsProcessor
    {
        private static bool _rapidsAvailable;
        private static bool _checkedAvailability;
        private static readonly SemaphoreSlim AvailabilityLock = new(1, 1);
        private static readonly TimeSpan RapidsProbeTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DefaultRapidsProcessTimeout = TimeSpan.FromMinutes(10);
        private static readonly string TempDirectory = Path.Combine(
            Path.GetTempPath(),
            "bstrings_rapids"
        );

        /// <summary>
        /// Initialize RAPIDS integration by checking Python and cuDF availability
        /// </summary>
        public static async Task InitializeAsync()
        {
            await AvailabilityLock.WaitAsync();
            try
            {
                if (_checkedAvailability)
                    return;

                try
                {
                    Directory.CreateDirectory(TempDirectory);

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ResolvePythonExecutable(),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    startInfo.ArgumentList.Add("-c");
                    startInfo.ArgumentList.Add(
                        "import cudf, cupy; "
                            + "s = cudf.Series(['Alpha']); "
                            + "assert bool(s.str.contains('[Aa]lpha', flags=0, regex=True).iloc[0]); "
                            + "print('RAPIDS_OK')"
                    );
                    using var process = new Process { StartInfo = startInfo };

                    process.Start();
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    await WaitForExitWithDeadlineAsync(process, RapidsProbeTimeout);
                    var output = await outputTask;
                    var error = await errorTask;

                    _rapidsAvailable =
                        output.Contains("RAPIDS_OK", StringComparison.Ordinal)
                        && process.ExitCode == 0;
                    if (_rapidsAvailable)
                    {
                        CreateRapidsPythonScript();

                        Console.WriteLine("RAPIDS regex acceleration initialized successfully.");
                        if (Program._debug)
                        {
                            Console.WriteLine(
                                "[RAPIDS] Enhanced GPU processing available with cuDF and cuPy"
                            );
                        }
                    }
                    else
                    {
                        if (Program._debug)
                        {
                            Console.WriteLine($"[RAPIDS] Unavailable - {error.Trim()}");
                            Console.WriteLine(
                                "[RAPIDS] Point BSTRINGS_RAPIDS_PYTHON at a prepared local cuDF/cuPy runtime"
                            );
                            Console.WriteLine(
                                "[RAPIDS] Install and validate RAPIDS separately before using --use-rapids"
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Program._debug)
                    {
                        Console.WriteLine($"[RAPIDS] Initialization failed: {ex.Message}");
                    }
                    _rapidsAvailable = false;
                }
                finally
                {
                    _checkedAvailability = true;
                }
            }
            finally
            {
                AvailabilityLock.Release();
            }
        }

        /// <summary>
        /// Check if RAPIDS is available for enhanced processing
        /// </summary>
        public static bool IsAvailable => _rapidsAvailable;

        internal static string ResolvePythonExecutable()
        {
            var configured = Environment.GetEnvironmentVariable("BSTRINGS_RAPIDS_PYTHON");
            if (string.IsNullOrWhiteSpace(configured))
            {
                return "python";
            }

            var resolved = Path.GetFullPath(configured);
            if (!File.Exists(resolved))
            {
                throw new FileNotFoundException(
                    "BSTRINGS_RAPIDS_PYTHON does not point to a local Python executable.",
                    resolved
                );
            }
            return resolved;
        }

        /// <summary>
        /// Process strings using an existing RAPIDS cuDF installation
        /// </summary>
        public static async Task<List<RapidsResult>> ProcessStringsWithRapidsAsync(
            IEnumerable<string> strings,
            List<(string name, string pattern)> regexPatternsWithNames,
            string sourceFile = "",
            bool showOffset = false,
            TimeSpan? processTimeout = null,
            CancellationToken cancellationToken = default
        )
        {
            if (!_rapidsAvailable)
            {
                throw new InvalidOperationException(
                    "RAPIDS is not available. Use IsAvailable to check before calling."
                );
            }

            var results = new List<RapidsResult>();
            var stringList = strings.ToList();

            if (!stringList.Any())
                return results;

            string inputFile = null;
            string outputFile = null;
            try
            {
                // Create temporary files for communication
                inputFile = Path.Combine(TempDirectory, $"input_{Guid.NewGuid()}.json");
                outputFile = Path.Combine(TempDirectory, $"output_{Guid.NewGuid()}.json");
                var scriptFile = Path.Combine(TempDirectory, "rapids_processor.py");
                var patterns = regexPatternsWithNames
                    .Where(pattern => !string.IsNullOrWhiteSpace(pattern.name))
                    .GroupBy(pattern => pattern.name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First().pattern,
                        StringComparer.OrdinalIgnoreCase
                    );

                // Prepare input data
                var inputData = new
                {
                    strings = stringList,
                    patterns,
                    source_file = sourceFile,
                    show_offset = showOffset,
                };

                await File.WriteAllTextAsync(inputFile, JsonSerializer.Serialize(inputData));

                // Execute RAPIDS processing
                var startInfo = new ProcessStartInfo
                {
                    FileName = ResolvePythonExecutable(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = TempDirectory,
                };
                startInfo.ArgumentList.Add(scriptFile);
                startInfo.ArgumentList.Add(inputFile);
                startInfo.ArgumentList.Add(outputFile);
                using var process = new Process { StartInfo = startInfo };

                var stopwatch = Stopwatch.StartNew();
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                await WaitForExitWithDeadlineAsync(
                    process,
                    processTimeout ?? DefaultRapidsProcessTimeout,
                    cancellationToken
                );
                var output = await outputTask;
                var error = await errorTask;
                stopwatch.Stop();

                if (process.ExitCode == 0 && File.Exists(outputFile))
                {
                    var resultJson = await File.ReadAllTextAsync(outputFile);
                    var rapidsResults = JsonSerializer.Deserialize<
                        List<Dictionary<string, object>>
                    >(resultJson) ?? [];

                    foreach (var result in rapidsResults)
                    {
                        results.Add(
                            new RapidsResult
                            {
                                PatternName = result["pattern_name"].ToString(),
                                DataFound = result["data_found"].ToString(),
                                SourceFile = result["source_file"].ToString(),
                                Offset = result["offset"].ToString(),
                                PatternType = "RAPIDS-GPU",
                                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                            }
                        );
                    }

                    if (Program._debug)
                    {
                        Console.WriteLine(
                            $"[RAPIDS] Processed {stringList.Count} strings in {stopwatch.ElapsedMilliseconds}ms, found {results.Count} matches"
                        );
                    }
                }
                else
                {
                    if (Program._debug)
                    {
                        Console.WriteLine($"[RAPIDS] Processing failed: {error}");
                    }
                    throw new Exception($"RAPIDS processing failed: {error}");
                }

                return results;
            }
            catch (Exception ex)
            {
                if (Program._debug)
                {
                    Console.WriteLine($"[RAPIDS] Error: {ex.Message}");
                }
                throw;
            }
            finally
            {
                TryDeleteTemporaryFile(inputFile);
                TryDeleteTemporaryFile(outputFile);
            }
        }

        private static void TryDeleteTemporaryFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static async Task WaitForExitWithDeadlineAsync(
            Process process,
            TimeSpan timeout,
            CancellationToken cancellationToken = default
        )
        {
            if (timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    "The RAPIDS process timeout must be greater than zero."
                );
            }

            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken
            );
            timeoutSource.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                throw new TimeoutException(
                    $"RAPIDS processing exceeded its {timeout.TotalSeconds:N0}-second deadline."
                );
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw;
            }
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(milliseconds: 5_000);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
        }

        /// <summary>
        /// Analyze performance comparison between RAPIDS and standard processing
        /// </summary>
        public static async Task<ProcessingBenchmark> BenchmarkPerformanceAsync(
            IEnumerable<string> strings,
            List<(string name, string pattern)> patterns
        )
        {
            var benchmark = new ProcessingBenchmark();
            var stringList = strings.Distinct(StringComparer.Ordinal).ToList();
            var hitSet = new HashSet<string>(stringList, StringComparer.Ordinal);

            if (Program._debug)
            {
                Console.WriteLine(
                    $"[RAPIDS] Starting benchmark with {stringList.Count} strings and {patterns.Count} patterns"
                );
            }

            // Benchmark standard processing
            var standardStopwatch = Stopwatch.StartNew();
            var standardResults = await Program.ProcessRegexPatternsConcurrentlyAsync(
                hitSet,
                patterns,
                false,
                false,
                true,
                null,
                true,
                "",
                "",
                false,
                false
            );
            standardStopwatch.Stop();

            benchmark.StandardProcessingTimeMs = standardStopwatch.ElapsedMilliseconds;
            benchmark.StandardResultCount = standardResults;

            // Benchmark RAPIDS processing if available
            var (gpuPatterns, _) = RapidsRegexPolicy.PartitionPatterns(patterns);
            if (_rapidsAvailable && gpuPatterns.Count > 0)
            {
                try
                {
                    var rapidsStopwatch = Stopwatch.StartNew();
                    var rapidsResultCount = await ProcessRegexPatternsBridgeAsync(
                        hitSet,
                        patterns,
                        ro: false,
                        off: false,
                        s: true,
                        sw: null,
                        q: true,
                        o: string.Empty,
                        allowCpuFallback: false
                    );
                    rapidsStopwatch.Stop();

                    benchmark.RapidsProcessingTimeMs = rapidsStopwatch.ElapsedMilliseconds;
                    benchmark.RapidsResultCount = rapidsResultCount;
                    benchmark.RapidsAvailable = true;
                    benchmark.CountParity =
                        benchmark.StandardResultCount == benchmark.RapidsResultCount;
                    benchmark.SpeedupFactor =
                        benchmark.RapidsProcessingTimeMs > 0
                            ? (double)standardStopwatch.ElapsedMilliseconds
                                / rapidsStopwatch.ElapsedMilliseconds
                            : 1.0;
                }
                catch (Exception ex)
                {
                    if (Program._debug)
                    {
                        Console.WriteLine($"[RAPIDS] Benchmark failed: {ex.Message}");
                    }
                    benchmark.RapidsAvailable = false;
                }
            }

            return benchmark;
        }

        /// <summary>
        /// Create the Python script for RAPIDS processing
        /// </summary>
        private static void CreateRapidsPythonScript()
        {
            var scriptPath = Path.Combine(TempDirectory, "rapids_processor.py");
            var script =
                @"#!/usr/bin/env python3
import sys
import json
import cudf
import cupy as cp
from typing import List, Dict, Any

def process_strings_with_rapids(input_file: str, output_file: str):
    """"""Process strings using RAPIDS cuDF for GPU acceleration""""""
    
    try:
        # Load input data
        with open(input_file, 'r', encoding='utf-8') as f:
            data = json.load(f)
        
        strings = data['strings']
        patterns = data['patterns']
        source_file = data.get('source_file', '')
        
        if not strings:
            with open(output_file, 'w') as f:
                json.dump([], f)
            return
        
        # Create cuDF DataFrame with strings
        df = cudf.DataFrame({
            'strings': strings
        })
        
        results = []
        
        # Process each pattern using GPU acceleration
        for pattern_name, pattern in patterns.items():
            try:
                # Patterns reaching this worker are catalog-owned, explicit-
                # case ASCII supersets. Current cuDF Python releases do not
                # support re.IGNORECASE in str.contains().
                matches = df['strings'].str.contains(
                    pattern,
                    flags=0,
                    regex=True
                )
                matched_df = df[matches]
                
                if len(matched_df) > 0:
                    # Convert GPU results back to CPU for JSON serialization
                    matched_strings = matched_df['strings'].to_pandas().tolist()
                    
                    for string_match in matched_strings:
                        results.append({
                            'pattern_name': pattern_name,
                            'data_found': string_match,
                            'source_file': source_file,
                            'offset': '',
                            'pattern_type': 'RAPIDS-GPU'
                        })
                        
            except Exception as e:
                # Never return a partial result set. The .NET bridge treats a
                # non-zero exit as a signal to rerun every pattern on CPU.
                raise RuntimeError(
                    f'Pattern {pattern_name} is not compatible with cuDF: {str(e)}'
                ) from e
        
        # Save results
        with open(output_file, 'w', encoding='utf-8') as f:
            json.dump(results, f, ensure_ascii=False, indent=2)
            
        print(f'Processed {len(strings)} strings, found {len(results)} matches')
        
    except Exception as e:
        print(f'RAPIDS processing error: {str(e)}', file=sys.stderr)
        sys.exit(1)

if __name__ == '__main__':
    if len(sys.argv) != 3:
        print('Usage: rapids_processor.py <input_file> <output_file>', file=sys.stderr)
        sys.exit(1)
    
    process_strings_with_rapids(sys.argv[1], sys.argv[2])
";

            File.WriteAllText(scriptPath, script);
        }

        /// <summary>
        /// Cleanup RAPIDS resources
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                if (Directory.Exists(TempDirectory))
                {
                    Directory.Delete(TempDirectory, true);
                }
            }
            catch (Exception ex)
            {
                if (Program._debug)
                {
                    Console.WriteLine($"[RAPIDS] Cleanup error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Bridge method to process regex patterns with RAPIDS, compatible with existing pipeline
        /// </summary>
        public static async Task<int> ProcessRegexPatternsBridgeAsync(
            HashSet<string> hits,
            List<(string name, string pattern)> regexPatternsWithNames,
            bool ro,
            bool off,
            bool s,
            StreamWriter sw,
            bool q,
            string o,
            string currentFile = "",
            bool isCsvOutput = false,
            bool csvHeaderAlreadyWritten = false,
            bool allowCpuFallback = true
        )
        {
            if (!_rapidsAvailable)
            {
                if (!allowCpuFallback)
                {
                    throw new InvalidOperationException(
                        "RAPIDS is unavailable and CPU fallback is disabled."
                    );
                }

                // Fallback to standard processing
                var standardResults = await Program.ProcessRegexPatternsConcurrentlyAsync(
                    hits,
                    regexPatternsWithNames,
                    ro,
                    off,
                    s,
                    sw,
                    q,
                    o,
                    currentFile,
                    isCsvOutput,
                    csvHeaderAlreadyWritten
                );
                return standardResults;
            }

            var (gpuPatterns, cpuPatterns) = RapidsRegexPolicy.PartitionPatterns(
                regexPatternsWithNames
            );

            if (gpuPatterns.Count == 0)
            {
                if (!allowCpuFallback)
                {
                    throw new InvalidOperationException(
                        "No requested regex pattern has a reviewed RAPIDS prefilter."
                    );
                }

                return await Program.ProcessRegexPatternsConcurrentlyAsync(
                    hits,
                    regexPatternsWithNames,
                    ro,
                    off,
                    s,
                    sw,
                    q,
                    o,
                    currentFile,
                    isCsvOutput,
                    csvHeaderAlreadyWritten
                );
            }

            // A fallback is safe only before any GPU-derived output has been
            // emitted. Once acquisition succeeds, verification, output, and
            // CPU-partition failures must propagate so rows cannot be duplicated.
            List<RapidsResult> rapidsResults;
            try
            {
                // Convert hits to list for RAPIDS processing.
                var stringList = hits.ToList();

                // Process only catalog patterns with reviewed libcudf superset
                // prefilters. Custom or incompatible expressions stay on CPU.
                rapidsResults = await ProcessStringsWithRapidsAsync(
                    stringList,
                    gpuPatterns,
                    currentFile,
                    off
                );
            }
            catch (Exception ex)
            {
                if (!allowCpuFallback)
                {
                    throw new InvalidOperationException(
                        "RAPIDS execution failed while CPU fallback was disabled.",
                        ex
                    );
                }

                if (Program._debug)
                {
                    Console.WriteLine(
                        $"RAPIDS processing failed: {ex.Message}. Falling back to standard processing."
                    );
                }

                // Fallback to standard processing
                return await Program.ProcessRegexPatternsConcurrentlyAsync(
                    hits,
                    regexPatternsWithNames,
                    ro,
                    off,
                    s,
                    sw,
                    q,
                    o,
                    currentFile,
                    isCsvOutput,
                    csvHeaderAlreadyWritten
                );
            }

            var regexMap = RegexOutputCore.BuildRegexMap(regexPatternsWithNames);
            int totalMatches = 0;

            // Handle output similar to the original method
            if (isCsvOutput && !csvHeaderAlreadyWritten && sw != null)
            {
                await sw.WriteLineAsync(RegexOutputCore.CsvHeader);
            }

            foreach (var result in rapidsResults)
            {
                if (!regexMap.TryGetValue(result.PatternName, out var regex))
                {
                    continue;
                }

                var parsedHit = RegexOutputCore.ParseHit(result.DataFound, off);
                if (!RegexOutputCore.IsMatch(result.PatternName, regex, parsedHit.Data))
                {
                    continue;
                }

                // The Python row number is not a byte offset. Only an
                // offset embedded by the extractor is authoritative.
                totalMatches++;

                if (ro)
                {
                    foreach (
                        var record in RegexOutputCore.CreateRecords(
                            parsedHit,
                            result.PatternName,
                            regex,
                            regexOutput: true,
                            currentFile,
                            "RAPIDS-prefilter/.NET-verified"
                        )
                    )
                    {
                        if (isCsvOutput && sw != null)
                        {
                            await sw.WriteLineAsync(RegexOutputCore.BuildCsvLine(record));
                        }
                        else if (sw != null)
                        {
                            await sw.WriteLineAsync(RegexOutputCore.BuildRegexOnlyText(record));
                        }

                        if (!s && !q)
                        {
                            Console.WriteLine(RegexOutputCore.BuildRegexOnlyText(record));
                        }
                    }
                }
                else
                {
                    var record = RegexOutputCore.CreateRecords(
                        parsedHit,
                        result.PatternName,
                        regex,
                        regexOutput: false,
                        currentFile,
                        "RAPIDS-prefilter/.NET-verified"
                    ).Single();
                    var fullHitText = RegexOutputCore.BuildFullHitText(parsedHit);

                    if (isCsvOutput && sw != null)
                    {
                        await sw.WriteLineAsync(RegexOutputCore.BuildCsvLine(record));
                    }
                    else if (sw != null)
                    {
                        await sw.WriteLineAsync(fullHitText);
                    }

                    if (!s && !q)
                    {
                        Console.WriteLine(fullHitText);
                    }
                }
            }

            if (cpuPatterns.Count > 0)
            {
                totalMatches += await Program.ProcessRegexPatternsConcurrentlyAsync(
                    hits,
                    cpuPatterns,
                    ro,
                    off,
                    s,
                    sw,
                    q,
                    o,
                    currentFile,
                    isCsvOutput,
                    csvHeaderAlreadyWritten || (isCsvOutput && sw != null)
                );
            }

            if (Program._debug)
            {
                Console.WriteLine(
                    $"RAPIDS prefiltered {gpuPatterns.Count} patterns and CPU processed {cpuPatterns.Count}; "
                        + $"{totalMatches} authoritative matches from {hits.Count} strings"
                );
            }

            return totalMatches;
        }
    }

    /// <summary>
    /// Result from RAPIDS-enhanced string processing
    /// </summary>
    public class RapidsResult
    {
        public string PatternName { get; set; } = "";
        public string DataFound { get; set; } = "";
        public string SourceFile { get; set; } = "";
        public string Offset { get; set; } = "";
        public string PatternType { get; set; } = "";
        public long ProcessingTimeMs { get; set; }
    }

    /// <summary>
    /// Performance comparison results
    /// </summary>
    public class ProcessingBenchmark
    {
        public long StandardProcessingTimeMs { get; set; }
        public long RapidsProcessingTimeMs { get; set; }
        public int StandardResultCount { get; set; }
        public int RapidsResultCount { get; set; }
        public bool RapidsAvailable { get; set; }
        public bool CountParity { get; set; }
        public double SpeedupFactor { get; set; }

        public override string ToString()
        {
            if (!RapidsAvailable)
                return "RAPIDS not available for comparison";

            return $"Performance: Standard={StandardProcessingTimeMs}ms, RAPIDS={RapidsProcessingTimeMs}ms, "
                + $"Speedup={SpeedupFactor:F2}x, Results=Standard:{StandardResultCount}/RAPIDS:{RapidsResultCount}";
        }
    }
}
