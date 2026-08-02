using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;
using FileInfo = System.IO.FileInfo;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices; // Keep one instance
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Streams;
using RawDiskLib;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace bstrings;

public static partial class Program
{
    private static Stopwatch _sw;
    private static readonly Dictionary<string, string> RegExPatterns =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> RegExDesc =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly string Header =
        $"bstrings version {Assembly.GetExecutingAssembly().GetName().Version.ToString(3)}"
        + "\r\n\r\nOriginal author: Eric Zimmerman (saericzimmerman@gmail.com)"
        + "\r\nUpstream: https://github.com/EricZimmerman/bstrings"
        + "\r\nFork: https://github.com/Donovoi/bstrings";
    private static readonly string Footer =
        @"Examples:"
        + "\r\n\t "
        + @"bstrings.exe -f ""C:\evidence\image.bin"""
        + "\r\n\t "
        + @"bstrings.exe -d ""C:\evidence"" --mask ""*.bin"" -s -o ""C:\results\all.txt"""
        + "\r\n\t "
        + @"bstrings.exe -f ""C:\evidence\image.bin"" --lr ""email,url3986,ipv4"" --ro"
        + "\r\n\t "
        + @"bstrings.exe -f ""C:\evidence\memory.raw"" --processor hybrid -s"
        + "\r\n\t "
        + @"bstrings.exe -f ""C:\evidence\image.bin"" --lr all --use-rapids"
        + "\r\n"
        + "\r\n--processor controls string extraction. --use-rapids is a separate, optional regex prefilter.";

    private static RootCommand _rootCommand;

    private static IFileSystem _fileSystem;

    public static bool _debug = false;

    private sealed class OutputCompletionScope : IAsyncDisposable
    {
        private readonly StreamWriter _writer;
        private readonly string _incompleteMarker;
        private bool _completed;

        internal OutputCompletionScope(StreamWriter writer, string incompleteMarker)
        {
            _writer = writer;
            _incompleteMarker = incompleteMarker;
        }

        internal void MarkCompleted()
        {
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (_writer is not null)
            {
                await _writer.FlushAsync();
                await _writer.DisposeAsync();
            }

            if (!_completed || _incompleteMarker is null)
            {
                return;
            }

            try
            {
                File.Delete(_incompleteMarker);
            }
            catch (IOException ex)
            {
                Log.Warning(
                    ex,
                    "Could not remove completed-output marker '{Marker}'",
                    _incompleteMarker
                );
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warning(
                    ex,
                    "Could not remove completed-output marker '{Marker}'",
                    _incompleteMarker
                );
            }
        }
    }

    /// <summary>
    /// Represents a chunk of data to be processed
    /// </summary>
    public struct DataChunk
    {
        public byte[] Data;
        public int ValidBytes;
        public long FileOffset;
        public int ChunkIndex;
        public bool IsBoundaryChunk;
    }

    /// <summary>
    /// Simple, thread-safe object pool for reducing allocations
    /// </summary>
    public class SimpleObjectPool<T>
        where T : class, new()
    {
        private readonly ConcurrentQueue<T> _objects = new ConcurrentQueue<T>();
        private readonly Func<T> _objectGenerator;
        private readonly Action<T> _resetAction;
        private int _count = 0;
        private readonly int _maxSize;

        public SimpleObjectPool(
            Func<T> objectGenerator = null,
            Action<T> resetAction = null,
            int maxSize = 100
        )
        {
            _objectGenerator = objectGenerator ?? (() => new T());
            _resetAction = resetAction;
            _maxSize = maxSize;
        }

        public T Get()
        {
            if (_objects.TryDequeue(out T item))
            {
                Interlocked.Decrement(ref _count);
                return item;
            }

            return _objectGenerator();
        }

        public void Return(T item)
        {
            if (_count < _maxSize)
            {
                _resetAction?.Invoke(item);
                _objects.Enqueue(item);
                Interlocked.Increment(ref _count);
            }
        }
    }

    /// <summary>
    /// Specialized StringBuilder pool for string processing
    /// </summary>
    public static class StringBuilderPool
    {
        private static readonly SimpleObjectPool<StringBuilder> _pool =
            new SimpleObjectPool<StringBuilder>(
                () => new StringBuilder(256), // Pre-size for typical strings
                sb => sb.Clear(), // Reset action
                50 // Maximum pool size
            );

        public static StringBuilder Get() => _pool.Get();

        public static void Return(StringBuilder sb) => _pool.Return(sb);
    }

    /// <summary>
    /// Specialized List<string> pool for result collections
    /// </summary>
    public static class StringListPool
    {
        private static readonly SimpleObjectPool<List<string>> _pool = new SimpleObjectPool<
            List<string>
        >(
            () => new List<string>(100), // Pre-size for typical result count
            list => list.Clear(), // Reset action
            20 // Maximum pool size
        );

        public static List<string> Get() => _pool.Get();

        public static void Return(List<string> list) => _pool.Return(list);
    }

    /// <summary>
    /// Byte array pool for chunk processing - using ArrayPool<byte> from .NET
    /// </summary>
    public static class ByteArrayPool
    {
        private static readonly ArrayPool<byte> _pool = ArrayPool<byte>.Shared;

        public static byte[] Rent(int minimumLength) => _pool.Rent(minimumLength);

        public static void Return(byte[] array, bool clearArray = false) =>
            _pool.Return(array, clearArray);
    }

    /// <summary>
    /// Configuration for concurrent processing - optimized for memory efficiency and safety
    /// Based on ripgrep analysis but with conservative memory limits to prevent OOM
    /// </summary>
    public static class ConcurrentConfig
    {
        // Conservative chunk sizes to prevent memory exhaustion
        public static int RipgrepStyleChunkSizeKB => 64; // 64KB like ripgrep for maximum parallelism
        public static int SmallFileChunkSizeKB => 256; // 256KB for small files
        public static int DefaultChunkSizeKB => 1024; // 1MB for medium files

        // CONSERVATIVE parallelism settings to prevent OOM - much reduced from ripgrep-style
        public static int MaxConcurrentChunks => Math.Max(4, Environment.ProcessorCount); // 1x cores minimum 4 (SAFE)
        public static int ReadAheadChunks => Math.Min(8, MaxConcurrentChunks); // Conservative read-ahead to prevent memory buildup
        public static int OptimalDegreeOfParallelism => Environment.ProcessorCount; // Use ALL CPU cores but don't over-subscribe
        public static int ProducerConsumerBufferSize => Math.Max(16, MaxConcurrentChunks * 2); // Small buffer to prevent memory pressure

        // Conservative thread pool optimization
        public static int WorkStealingThreads => Environment.ProcessorCount; // 1x threading - no over-subscription to prevent memory pressure

        // Get optimal chunk size based on file size (conservative heuristics)
        public static int GetOptimalChunkSizeKB(long fileSizeBytes)
        {
            // For small files (< 1MB), use ripgrep's 64KB chunks for maximum parallelism
            if (fileSizeBytes < 1024 * 1024)
                return RipgrepStyleChunkSizeKB;

            // For medium files (1MB - 100MB), use 256KB chunks
            if (fileSizeBytes < 100 * 1024 * 1024)
                return SmallFileChunkSizeKB;

            // For large files (100MB - 1GB), use 1MB chunks
            if (fileSizeBytes < 1024 * 1024 * 1024)
                return DefaultChunkSizeKB;

            // For very large files (> 1GB), still use 1MB for good balance
            return DefaultChunkSizeKB;
        }

        // Memory safety checks
        public static bool IsMemorySafe(long fileSizeBytes, int chunkSizeKB)
        {
            // Estimate total memory usage: chunks * concurrent processing * safety factor
            long estimatedMemoryMB = (MaxConcurrentChunks * chunkSizeKB) / 1024 * 3; // 3x safety factor

            // Don't use more than 1GB of memory for chunk processing
            return estimatedMemoryMB < 1024;
        }
    }

    /// <summary>
    /// Thread-safe progress tracking for concurrent chunk processing
    /// </summary>
    public class ProgressTracker
    {
        private readonly object _lockObject = new object();
        private readonly Stopwatch _stopwatch;
        private readonly long _totalChunks;
        private readonly bool _quiet;
        private long _completedChunks = 0;
        private long _totalStrings = 0;
        private DateTime _lastUpdate = DateTime.MinValue;
        private bool _isCompleted = false;

        public ProgressTracker(long totalChunks, bool quiet)
        {
            _totalChunks = totalChunks;
            _quiet = quiet;
            _stopwatch = Stopwatch.StartNew();
        }

        public void MarkCompleted()
        {
            lock (_lockObject)
            {
                _isCompleted = true;
            }
        }

        public bool IsCompleted
        {
            get
            {
                lock (_lockObject)
                {
                    return _isCompleted || _completedChunks >= _totalChunks;
                }
            }
        }

        public void ReportChunkComplete(int stringCount)
        {
            lock (_lockObject)
            {
                _completedChunks++;
                _totalStrings += stringCount;

                // Update progress more frequently - every chunk completion or every 0.5 seconds, whichever comes first
                var now = DateTime.Now;
                var shouldUpdate =
                    (now - _lastUpdate).TotalSeconds >= 0.5 || _completedChunks == _totalChunks;

                if (!_quiet && shouldUpdate)
                {
                    _lastUpdate = now;
                    var elapsed = _stopwatch.Elapsed.TotalSeconds;
                    var stringsPerSec = elapsed > 0 ? _totalStrings / elapsed : 0;
                    var progressPercent = (_completedChunks * 100.0) / _totalChunks;

                    var progressBar = CreateProgressBar(progressPercent);

                    Console.Error.Write(
                        $"\r{progressBar} {progressPercent:F1}% | {_completedChunks:N0}/{_totalChunks:N0} chunks | {_totalStrings:N0} strings | {stringsPerSec:N0} strings/sec"
                    );

                    if (_completedChunks == _totalChunks)
                    {
                        Console.Error.WriteLine(); // New line when complete
                    }
                }
            }
        }

        private static string CreateProgressBar(double percent)
        {
            const int barLength = 30;
            var filled = (int)((percent / 100.0) * barLength);
            var bar = new StringBuilder("[");

            for (int i = 0; i < barLength; i++)
            {
                bar.Append(i < filled ? "#" : "-");
            }

            bar.Append("]");
            return bar.ToString();
        }

        public long TotalStrings => _totalStrings;
        public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;
        public bool HasCompletedChunks => _completedChunks > 0;

        /// <summary>
        /// Forces a progress update regardless of time elapsed
        /// </summary>
        public void ForceProgressUpdate()
        {
            lock (_lockObject)
            {
                if (!_quiet)
                {
                    _lastUpdate = DateTime.Now;
                    var elapsed = _stopwatch.Elapsed.TotalSeconds;
                    var stringsPerSec = elapsed > 0 ? _totalStrings / elapsed : 0;
                    var progressPercent = (_completedChunks * 100.0) / _totalChunks;

                    var progressBar = CreateProgressBar(progressPercent);

                    Console.Error.Write(
                        $"\r{progressBar} {progressPercent:F1}% | {_completedChunks:N0}/{_totalChunks:N0} chunks | {_totalStrings:N0} strings | {stringsPerSec:N0} strings/sec"
                    );

                    if (_completedChunks == _totalChunks)
                    {
                        Console.Error.WriteLine(); // New line when complete
                    }
                }
            }
        }
    }

    private static async Task<int> Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Set console encoding to support Unicode characters (including emojis)
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.InputEncoding = System.Text.Encoding.UTF8;
        }
        catch
        {
            // Ignore encoding setup errors on systems that don't support it
        }

        SetupPatterns();

        var fOpt = new Option<string>("-f")
        {
            Description = "File to search. Either this or -d is required",
        };
        var dOpt = new Option<string>("-d")
        {
            Description = "Directory to recursively process. Either this or -f is required",
        };
        var oOpt = new Option<string>("-o") { Description = "File to save results to" };
        var aOpt = new Option<bool>("-a")
        {
            Description = "If set, look for code-page strings. Use -a false to disable",
            DefaultValueFactory = _ => true,
        };
        var uOpt = new Option<bool>("-u")
        {
            Description = "If set, look for UTF-16LE strings. Use -u false to disable",
            DefaultValueFactory = _ => true,
        };
        var mOpt = new Option<int>("-m")
        {
            Description = "Minimum string length",
            DefaultValueFactory = _ => 3,
        };
        var bOpt = new Option<int>("-b")
        {
            Description =
                "Chunk size in MB. Use 0 for automatic sizing; explicit values must be 1 to 1024",
            DefaultValueFactory = _ => 0,
        };
        var qOpt = new Option<bool>("-q")
        {
            Description = "Do not show the header or final summary",
            DefaultValueFactory = _ => false,
        };
        var sOpt = new Option<bool>("-s")
        {
            Description = "Do not write hits to the console",
            DefaultValueFactory = _ => false,
        };
        var xOpt = new Option<int>("-x")
        {
            Description = "Maximum string length. Default is unlimited",
            DefaultValueFactory = _ => -1,
        };
        var pOpt = new Option<bool>("-p")
        {
            Description = "Display the built-in regular expressions",
            DefaultValueFactory = _ => false,
        };
        var lsOpt = new Option<string>("--ls")
        {
            Description = "Only return strings containing this text",
        };
        var lrOpt = new Option<string>("--lr")
        {
            Description =
                "Only return regex matches. Accepts built-in names separated by commas, a custom regex, or 'all'",
        };
        var fsOpt = new Option<string>("--fs")
        {
            Description = "File containing literal strings to find",
        };
        var frOpt = new Option<string>("--fr")
        {
            Description = "File containing regex patterns to find",
        };
        var arOpt = new Option<string>("--ar")
        {
            Description = @"Code-page byte range. Default is [\x20-\x7E]",
            DefaultValueFactory = _ => "[\\x20-\\x7E]",
        };
        var urOpt = new Option<string>("--ur")
        {
            Description = @"UTF-16LE character range. Default is [\u0020-\u007E]",
            DefaultValueFactory = _ => "[\\u0020-\\u007E]",
        };
        var cpOpt = new Option<int>("--cp")
        {
            Description = "Code page used to decode byte strings. Default is 1252",
            DefaultValueFactory = _ => 1252,
        };
        var maskOpt = new Option<string>("--mask")
        {
            Description = "File mask used with -d. Supports * and ?",
        };
        var msOpt = new Option<int>("--ms")
        {
            Description = "Maximum file size in bytes when using -d",
            DefaultValueFactory = _ => -1,
        };
        var roOpt = new Option<bool>("--ro")
        {
            Description = "Output only the regex match rather than the full extracted string",
            DefaultValueFactory = _ => false,
        };
        var offOpt = new Option<bool>("--off")
        {
            Description = "Include the source byte offset",
            DefaultValueFactory = _ => false,
        };
        var saOpt = new Option<bool>("--sa")
        {
            Description = "Sort results alphabetically",
            DefaultValueFactory = _ => false,
        };
        var slOpt = new Option<bool>("--sl")
        {
            Description = "Sort results by length",
            DefaultValueFactory = _ => false,
        };
        var debugOpt = new Option<bool>("--debug")
        {
            Description = "Show debug information",
            DefaultValueFactory = _ => false,
        };
        var traceOpt = new Option<bool>("--trace")
        {
            Description = "Show trace-level logging",
            DefaultValueFactory = _ => false,
        };
        var processorOpt = new Option<string>("--processor")
        {
            Description =
                "Extraction processor: auto, cpu, gpu, or hybrid. Default is auto",
            DefaultValueFactory = _ => "auto",
        };
        var useRapidsOpt = new Option<bool>("--use-rapids")
        {
            Description = "Use an existing NVIDIA RAPIDS installation for regex processing",
            DefaultValueFactory = _ => false,
        };
        var forceRapidsOpt = new Option<bool>("--force-rapids")
        {
            Description =
                "Deprecated compatibility flag. Third-party software is not installed automatically",
            DefaultValueFactory = _ => false,
        };

        _rootCommand = new RootCommand
        {
            fOpt,
            dOpt,
            oOpt,
            aOpt,
            uOpt,
            mOpt,
            bOpt,
            qOpt,
            sOpt,
            xOpt,
            pOpt,
            lsOpt,
            lrOpt,
            fsOpt,
            frOpt,
            arOpt,
            urOpt,
            cpOpt,
            maskOpt,
            msOpt,
            roOpt,
            offOpt,
            saOpt,
            slOpt,
            debugOpt,
            traceOpt,
            processorOpt,
            useRapidsOpt,
            forceRapidsOpt,
        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;
        _rootCommand.SetAction(
            async result =>
                await DoWork(
                    result.GetValue(fOpt),
                    result.GetValue(dOpt),
                    result.GetValue(oOpt),
                    result.GetValue(aOpt),
                    result.GetValue(uOpt),
                    result.GetValue(mOpt),
                    result.GetValue(bOpt),
                    result.GetValue(qOpt),
                    result.GetValue(sOpt),
                    result.GetValue(xOpt),
                    result.GetValue(pOpt),
                    result.GetValue(lsOpt),
                    result.GetValue(lrOpt),
                    result.GetValue(fsOpt),
                    result.GetValue(frOpt),
                    result.GetValue(arOpt),
                    result.GetValue(urOpt),
                    result.GetValue(cpOpt),
                    result.GetValue(maskOpt),
                    result.GetValue(msOpt),
                    result.GetValue(roOpt),
                    result.GetValue(offOpt),
                    result.GetValue(saOpt),
                    result.GetValue(slOpt),
                    result.GetValue(debugOpt),
                    result.GetValue(traceOpt),
                    result.GetValue(processorOpt),
                    result.GetValue(useRapidsOpt),
                    result.GetValue(forceRapidsOpt)
                )
        );

        try
        {
            return await _rootCommand.Parse(args).InvokeAsync();
        }
        finally
        {
            bstrings.Rapids.RapidsProcessor.Shutdown();
            Log.CloseAndFlush();
        }
    }

    private static async Task DoWork(
        string f,
        string d,
        string o,
        bool a, // ascii
        bool u, // unicode
        int m, // minLength
        int b, // chunkSizeMb
        bool q, // quiet
        bool s, // This was 's' for silent in original thinking, but seems unused or repurposed.
        int x, // maxLength
        bool p, // list patterns
        string ls, // literal string search
        string lr, // literal regex search
        string fs, // file string search
        string fr, // file regex search
        string ar, // ascii range
        string ur, // unicode range
        int cp, // codepage
        string mask,
        int ms, // max size
        bool ro, // regex output
        bool off, // show offset
        bool sa, // sort alphabetical
        bool sl, // sort by length
        bool debug,
        bool trace,
        string processor,
        bool useRapids, // use NVIDIA RAPIDS for GPU-accelerated regex processing
        bool forceRapids // deprecated compatibility flag
    )
    { // Set the global debug flag
        _debug = debug;

        if (!ProcessingBackendCore.TryParseMode(processor, out var requestedMode, out var modeError))
        {
            Console.Error.WriteLine(modeError);
            return;
        }

        // Initialize RAPIDS integration if requested
        if (forceRapids)
        {
            Console.Error.WriteLine(
                "Warning: --force-rapids is deprecated and will not install software. RAPIDS must be installed and validated separately."
            );
            useRapids = true;
        }

        if (useRapids)
        {
            if (!q) // Only show if not in quiet mode
            {
                Log.Information("Checking the configured NVIDIA RAPIDS environment...");
            }
            await bstrings.Rapids.RapidsProcessor.InitializeAsync();

            // Add extra line for readability after RAPIDS status
            if (!q)
            {
                Console.WriteLine();
            }
        }

        var levelSwitch = new LoggingLevelSwitch();

        var template = "{Message:lj}{NewLine}{Exception}";

        if (debug)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Debug;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }

        if (trace)
        {
            levelSwitch.MinimumLevel = LogEventLevel.Verbose;
            template = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}";
        }

        var conf = new LoggerConfiguration()
            .WriteTo.Console(outputTemplate: template)
            .MinimumLevel.ControlledBy(levelSwitch);

        Log.Logger = conf.CreateLogger();

        if (p)
        {
            Log.Information("Name \t\tDescription");
            foreach (var regExPattern in RegExPatterns.OrderBy(t => t.Key))
            {
                var desc = RegExDesc[regExPattern.Key];
                Log.Information("{Key}\t{Desc}", regExPattern.Key, desc);
            }

            Console.WriteLine();
            Log.Information("Pass a name from this list to --lr, for example: --lr email\r\n");

            return;
        }

        var cpTest = CodePagesEncodingProvider.Instance.GetEncoding(cp);

        if (cpTest == null)
        {
            Log.Warning(
                "Invalid codepage: '{Cp}'. Use the Identifier value for code pages at https://goo.gl/ig6DxW. Verify codepage value and try again",
                cp
            );
            return;
        }

        // ########################### EDITED ###########################
        var files = new List<string>(); // This is the main list of files to process
        var inputResolution = InputResolutionCore.ResolveExplicitInputs(
            f,
            d,
            mask,
            File.Exists,
            Directory.Exists,
            (directoryPath, searchMask) =>
                Directory.EnumerateFiles(
                    directoryPath,
                    searchMask,
                    SearchOption.AllDirectories
                ),
            Path.GetFullPath
        );

        if (inputResolution.Status == InputResolutionStatus.Success)
        {
            files.AddRange(inputResolution.Files);
        }
        else if (inputResolution.Status == InputResolutionStatus.NoFilesFound)
        {
            if (!q)
            {
                Log.Information("{Message}", inputResolution.Message);
            }

            return;
        }
        else if (inputResolution.Status == InputResolutionStatus.Error)
        {
            if (inputResolution.Exception is not null)
            {
                Log.Error(inputResolution.Exception, "{Message}", inputResolution.Message);
            }
            else
            {
                Log.Error("{Message}", inputResolution.Message);
            }

            return;
        }
        else if (Console.IsInputRedirected)
        {
            Log.Information("No -f or -d specified; attempting to read from stdin...");

            var redirectedInputResolution = InputResolutionCore.CaptureRedirectedInput(
                Console.OpenStandardInput,
                Path.GetTempFileName,
                path => new FileStream(path, FileMode.Create, FileAccess.Write),
                path => new FileInfo(path).Length,
                File.Delete,
                Path.GetFullPath
            );

            if (redirectedInputResolution.Status == InputResolutionStatus.Success)
            {
                files.AddRange(redirectedInputResolution.Files);
            }
            else if (
                redirectedInputResolution.Status == InputResolutionStatus.EmptyRedirectedInput
            )
            {
                Log.Warning("{Message}", redirectedInputResolution.Message);
                return;
            }
            else
            {
                if (redirectedInputResolution.Exception is not null)
                {
                    Log.Error(
                        redirectedInputResolution.Exception,
                        "{Message}",
                        redirectedInputResolution.Message
                    );
                }
                else
                {
                    Log.Error("{Message}", redirectedInputResolution.Message);
                }

                return;
            }
        }
        else // No -f, no -d, and no piped input
        {
            await _rootCommand.Parse(["--help"]).InvokeAsync();
            Log.Warning("{Message}", inputResolution.Message);
            return;
        }

        if (!q)
        {
            Log.Information("{Header}", Header);
            Console.WriteLine();
        }

        if (!q)
        {
            Log.Information(
                "Command line: {Args}",
                string.Join(" ", Environment.GetCommandLineArgs().Skip(1))
            );
            Console.WriteLine();
        }

        StreamWriter sw = null;

        var outputConfiguration = OutputConfigurationCore.Prepare(
            o,
            Path.GetFullPath,
            Path.GetDirectoryName,
            Directory.Exists,
            path => Directory.CreateDirectory(path)
        );

        bool isCsvOutput = outputConfiguration.IsCsvOutput;
        bool csvHeaderWritten = false;
        string outputIncompleteMarker = null;

        var globalCounter = 0;
        var globalHits = 0;
        double globalTimespan = 0;
        if (outputConfiguration.WarningMessage is not null)
        {
            Log.Warning("{Message}", outputConfiguration.WarningMessage);
            Console.WriteLine();
            o = string.Empty;
        }
        else if (outputConfiguration.IsEnabled)
        {
            o = outputConfiguration.OutputPath;

            if (!q)
            {
                Log.Information("Saving hits to '{O}'", o);
                Console.WriteLine();
            }

            outputIncompleteMarker = o + ".incomplete";
            File.WriteAllText(
                outputIncompleteMarker,
                $"bstrings output is incomplete; processing started {DateTimeOffset.UtcNow:O}.{Environment.NewLine}"
            );
            sw = new StreamWriter(o, true);
        }

        await using var outputCompletion = new OutputCompletionScope(sw, outputIncompleteMarker);
        var largestInputBytes = files
            .Where(File.Exists)
            .Select(path => new FileInfo(path).Length)
            .DefaultIfEmpty(0)
            .Max();

        ProcessingBackendSession processingBackend;
        try
        {
            processingBackend = ProcessingBackendSession.Create(
                requestedMode,
                largestInputBytes,
                m > 0 ? m : 3
            );
        }
        catch (Exception ex)
        {
            Log.Error("{Message}", ex.Message);
            throw;
        }

        using var processingBackendScope = processingBackend;
        if (!q)
        {
            Log.Information("Extraction processors: {Status}", processingBackend.StatusMessage);
            Console.WriteLine();
        }

        foreach (var currentFile in files) // Renamed 'file' to 'currentFile'
        {
            if (File.Exists(currentFile) == false) // Use currentFile
            {
                Log.Warning("'{CurrentFile}' does not exist! Skipping", currentFile); // Use currentFile
                continue;
            }

            _sw = new Stopwatch();
            _sw.Start();
            var counter = 0;
            var hits = new HashSet<string>();
            var rawResultsStreamed = false;
            var regexResultsStreamed = false;
            var withBoundaryHits = false;

            // Parse multiple patterns from lr parameter
            var regexPatternsWithNames = ParseRegexPatternsWithNames(lr);
            var regexPatterns = regexPatternsWithNames.Select(p => p.pattern).ToList();
            var searchTargets = SearchTargetConfigurationCore.Build(
                ls,
                lr,
                fs,
                fr,
                regexPatterns,
                File.Exists,
                File.ReadAllLines
            );
            var fileStrings = new HashSet<string>(searchTargets.FileStrings);
            var regexStrings = new HashSet<string>(searchTargets.RegexStrings);
            foreach (var missingFile in searchTargets.MissingFiles)
            {
                Log.Error("{Message}", missingFile);
            }

            // Literal targets take precedence in standard post-processing. Apply
            // the same predicate while each bounded extraction batch is still
            // local so rejected hits never enter the global deduplication set.
            var literalBatchFilter =
                regexPatterns.Count == 0 && fileStrings.Count > 0
                    ? new LiteralBatchFilterCore(fileStrings, off)
                    : null;
            Func<List<string>, List<string>> literalBatchTransform =
                literalBatchFilter is null ? null : literalBatchFilter.TransformBatch;
            var canStreamRegexResults =
                sw is not null
                && o.Length > 0
                && regexPatterns.Count > 0
                && off
                && s
                && q
                && !sa
                && !sl
                && string.IsNullOrWhiteSpace(ls)
                && string.IsNullOrWhiteSpace(fs)
                && string.IsNullOrWhiteSpace(fr)
                && !useRapids
                && !forceRapids;
            var requiresPostProcessing =
                isCsvOutput
                || sa
                || sl
                || !string.IsNullOrWhiteSpace(ls)
                || !string.IsNullOrWhiteSpace(fs)
                || !string.IsNullOrWhiteSpace(fr)
                || regexPatterns.Count > 0;
            var canStreamRawResults =
                sw is not null && o.Length > 0 && !requiresPostProcessing;
            var streamingRegexOutput = canStreamRegexResults
                ? new StreamingRegexOutputCore(
                    regexPatternsWithNames,
                    ro,
                    off,
                    isCsvOutput,
                    currentFile
                )
                : null;

            if (canStreamRegexResults && isCsvOutput && !csvHeaderWritten)
            {
                await sw.WriteLineAsync(RegexOutputCore.CsvHeader);
                csvHeaderWritten = true;
            }

            if (regexPatterns.Count > 0 && !q)
            {
                if (regexPatterns.Count == 1)
                {
                    Log.Information("Searching via RegEx pattern: {RegPattern}", regexPatterns[0]);
                }
                else
                {
                    Log.Information(
                        "Searching via {Count} RegEx patterns concurrently:",
                        regexPatterns.Count
                    );
                    foreach (var pattern in regexPatterns)
                    {
                        Log.Information("  - {Pattern}", pattern);
                    }
                }
                Console.WriteLine();
            }

            var minLength = 3;
            if (m > 0)
            {
                minLength = m;
            }

            var maxLength = -1;

            if (x > minLength)
            {
                maxLength = x;
            }

            var fileSizeBytes = new FileInfo(currentFile).Length; // Use currentFile
            var processingMode = processingBackend.ResolveForFile(fileSizeBytes, minLength);

            if (!q && _debug)
            {
                Console.Error.WriteLine(
                    $"Selected {processingMode.ToString().ToLowerInvariant()} extraction for '{currentFile}'."
                );
            }

            if (b < 0 || b > 1024)
            {
                Log.Error("Chunk size must be 0 (automatic) or between 1 and 1024 MB.");
                return;
            }

            var chunkSizeMb =
                b == 0 ? GetOptimalChunkSize(fileSizeBytes, processingMode) : b;
            var chunkSizeBytes = checked(chunkSizeMb * 1024 * 1024);

            if (ms > 0)
            {
                if (fileSizeBytes > ms)
                {
                    Log.Warning(
                        "'{File}' is bigger than max file size of {Ms:N0} bytes! Skipping...",
                        currentFile,
                        ms
                    );
                    continue;
                }
            } // Variables for progress reporting
            var totalChunks = Math.Max(
                1,
                (fileSizeBytes + chunkSizeBytes - 1L) / chunkSizeBytes
            );

            ProgressTracker progressTracker = new ProgressTracker(totalChunks, q);

            if (!q)
            {
                if (totalChunks == 1)
                {
                    Log.Information(
                        "Searching {TotalChunks:N0} chunk ({ChunkSizeMb} MB each) across {SizeReadable} in '{CurrentFile}'", // Use currentFile
                        totalChunks,
                        chunkSizeMb,
                        GetSizeReadable(fileSizeBytes),
                        currentFile // Use currentFile
                    );
                }
                else
                {
                    Log.Information(
                        "Searching {TotalChunks:N0} chunks ({ChunkSizeMb} MB each) across {SizeReadable} in '{CurrentFile}'", // Use currentFile
                        totalChunks,
                        chunkSizeMb,
                        GetSizeReadable(fileSizeBytes),
                        currentFile // Use currentFile
                    );
                }

                Console.WriteLine();
            }

            if (!q && _debug)
            {
                Console.Error.WriteLine("Starting chunk processing...");
            }

            // Progress updater: updates progress every second and shows heartbeat when no progress
            var progressCts = new CancellationTokenSource();
            var progressTask = Task.Run(
                async () =>
                {
                    while (
                        !progressCts.Token.IsCancellationRequested && !progressTracker.IsCompleted
                    )
                    {
                        await Task.Delay(1000, progressCts.Token);

                        if (
                            progressCts.Token.IsCancellationRequested || progressTracker.IsCompleted
                        )
                            break;
                        if (!progressTracker.HasCompletedChunks)
                        {
                            // Show heartbeat only if no chunks have completed yet and in debug mode
                            if (_debug)
                            {
                                Console.Error.Write(
                                    "\r[Working...] No chunks complete yet. Still processing..."
                                );
                            }
                        }
                        else
                        {
                            // Force a progress update every second once chunks start completing
                            progressTracker.ForceProgressUpdate();
                        }
                    }
                },
                progressCts.Token
            );

            try
            {
                var mappedStreamSetup = FileSetupCore.SetupMappedStreamForFile(
                    currentFile,
                    _ => FileSetupCore.CreateReadableFileStream(currentFile),
                    _ => OpenFile(currentFile),
                    !q && _debug ? Console.Error.WriteLine : null,
                    Console.Error.WriteLine,
                    stream => MappedStream.FromStream(stream, Ownership.Dispose)
                );
                var mappedStream = mappedStreamSetup.Stream;

                using (mappedStream)
                { // Process main chunks concurrently with streaming output for memory efficiency
                    if (sw != null && o.Length > 0)
                    {
                        StreamWriter outputWriter =
                            canStreamRawResults || canStreamRegexResults ? sw : null;
                        await ProcessFileChunksConcurrentlyStreamingAsync(
                            mappedStream,
                            fileSizeBytes,
                            chunkSizeBytes,
                            minLength,
                            maxLength,
                            a,
                            u,
                            off,
                            cp,
                            ar,
                            ur,
                            q,
                            totalChunks,
                            progressTracker,
                            outputWriter,
                            canStreamRegexResults ? null : hits,
                            processingBackend,
                            processingMode,
                            canStreamRegexResults
                                ? new Func<List<string>, List<string>>(
                                    streamingRegexOutput!.TransformMainBatch
                                  )
                                : literalBatchTransform
                        );
                        rawResultsStreamed = canStreamRawResults;
                        regexResultsStreamed = canStreamRegexResults;
                    }
                    else
                    {
                        // Keep the bounded producer/consumer path even without an output file.
                        // This avoids retaining every hit in one large List and then walking
                        // that list again to build the deduplicated result set.
                        await ProcessFileChunksConcurrentlyStreamingAsync(
                            mappedStream,
                            fileSizeBytes,
                            chunkSizeBytes,
                            minLength,
                            maxLength,
                            a,
                            u,
                            off,
                            cp,
                            ar,
                            ur,
                            q,
                            totalChunks,
                            progressTracker,
                            null,
                            hits,
                            processingBackend,
                            processingMode,
                            literalBatchTransform
                        );
                    } //do chunk boundary checks to make sure we get everything and not split things
                    if (!q)
                    {
                        Log.Information(
                            "Primary search complete. Looking for strings across chunk boundaries..."
                        );
                    }

                    var boundaryProcessingMode =
                        processingMode == ProcessingMode.Hybrid
                            ? ProcessingMode.Cpu
                            : processingMode;

                    // Process boundary chunks concurrently with streaming for memory efficiency
                    if (sw != null && o.Length > 0)
                    {
                        StreamWriter boundaryOutputWriter =
                            canStreamRawResults || canStreamRegexResults ? sw : null;
                        var boundaryResults = await ProcessBoundaryChunksConcurrentlyStreamingAsync(
                            mappedStream,
                            fileSizeBytes,
                            chunkSizeBytes,
                            checked(minLength * 40), // boundaryChunkSize
                            minLength,
                            maxLength,
                            a,
                            u,
                            off,
                            cp,
                            ar,
                            ur,
                            q,
                            boundaryOutputWriter,
                            canStreamRegexResults ? null : hits,
                            processingBackend,
                            boundaryProcessingMode,
                            canStreamRegexResults
                                ? new Func<List<string>, List<string>>(
                                    streamingRegexOutput!.TransformBoundaryBatch
                                  )
                                : literalBatchTransform
                        );
                        withBoundaryHits |= boundaryResults > 0;
                    }
                    else
                    {
                        withBoundaryHits |=
                            await ProcessBoundaryChunksConcurrentlyStreamingAsync(
                                mappedStream,
                                fileSizeBytes,
                                chunkSizeBytes,
                                checked(minLength * 40), // boundaryChunkSize
                                minLength,
                                maxLength,
                                a,
                                u,
                                off,
                                cp,
                                ar,
                                ur,
                                q,
                                null,
                                hits,
                                processingBackend,
                                boundaryProcessingMode,
                                literalBatchTransform
                            ) > 0;
                    }
                }

                // Mark progress as completed to stop the progress task
                progressTracker.MarkCompleted();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Log.Error(ex, "Error: {Message}", ex.Message);
                throw;
            }
            finally
            {
                // Clean up the progress updater task
                progressCts.Cancel();
                try
                {
                    await progressTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelling the task
                }
                progressCts.Dispose();
            }

            _sw.Stop();

            if (!q)
            {
                Log.Information("Search complete.");
                Console.WriteLine();
            }

            IReadOnlyList<string> orderedHits = null;
            if (sa)
            {
                Log.Information("Sorting alphabetically...");
                Console.WriteLine();
                orderedHits = OrderHits(hits, alphabetically: true, byLength: false, off);
            }
            else if (sl)
            {
                Log.Information("Sorting by length...");
                Console.WriteLine();
                orderedHits = OrderHits(hits, alphabetically: false, byLength: true, off);
            }

            //AddHighlightingRules(fileStrings.ToList());

            if (ro == false)
            {
                //  AddHighlightingRules(regexStrings.ToList(), true);
            }
            if (!q)
            {
                Log.Information("Processing strings...");
                Console.WriteLine();
            }

            // Skip expensive post-processing if results are already written to file and no console output needed
            bool hasPatternProcessing = fileStrings.Count > 0 || regexStrings.Count > 0;
            if (regexResultsStreamed)
            {
                counter = (int)Math.Min(streamingRegexOutput!.OutputRowCount, int.MaxValue);
                goto skipGeneralProcessing;
            }
            else if (regexPatterns.Count > 0)
            { // Try RAPIDS processing if enabled and available
                var rapidsRequested = useRapids || forceRapids;
                var canUseRapidsForRun =
                    rapidsRequested
                    && bstrings.Rapids.RapidsProcessor.IsAvailable
                    && orderedHits is null;
                if (canUseRapidsForRun)
                {
                    if (!q) // Show success message unless in quiet mode
                    {
                        Log.Information(
                            "Using NVIDIA RAPIDS to prefilter compatible regex patterns."
                        );
                    }

                    counter = await bstrings.Rapids.RapidsProcessor.ProcessRegexPatternsBridgeAsync(
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
                        csvHeaderWritten
                    );
                }
                else
                {
                    if (rapidsRequested)
                    {
                        Log.Warning(
                            orderedHits is null
                                ? "RAPIDS GPU acceleration is unavailable; using the optimized CPU regex engine."
                                : "RAPIDS processing is disabled when sorting is requested so output order remains deterministic."
                        );
                    }

                    if (!q)
                    {
                        Log.Information("Using parallel CPU regex processing.");
                    }

                    counter = await ProcessRegexPatternsConcurrentlyAsync(
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
                        csvHeaderWritten,
                        orderedHits
                    );
                }

                // Mark CSV header as written since regex processing handles its own CSV output
                if (isCsvOutput && sw != null)
                {
                    csvHeaderWritten = true;
                }

                if (!q)
                {
                    Log.Information("Regex pattern processing complete.");
                    Console.WriteLine();
                }

                // Skip general string processing when regex patterns are used - regex processing handles output
                goto skipGeneralProcessing;
            }
            else if (rawResultsStreamed && !hasPatternProcessing)
            {
                counter = hits.Count;
                if (!q)
                {
                    Log.Information(
                        "Results were written while scanning. Skipping redundant post-processing."
                    );
                    Console.WriteLine();
                }
            }
            else
            {
                // Add progress reporting for large datasets
                int processedCount = 0;
                DateTime lastProgressReport = DateTime.Now;
                bool isLargeDataset = hits.Count > 100000;
                if (isLargeDataset && !q)
                {
                    Log.Information(
                        "Processing {Count:N0} strings with parallel optimization...",
                        hits.Count
                    );
                    Console.WriteLine();
                }

                var compiledRegexes = StandardHitProcessingCore.CompileRegexTargets(
                    regexStrings,
                    pattern => GetRegexPatternName(pattern),
                    (pattern, message) =>
                        Log.Warning(
                            "Invalid regex pattern '{Pattern}': {Message}",
                            pattern,
                            message
                        )
                );

                // Use concurrent processing for large datasets
                var hitsList = orderedHits ?? hits.ToList();
                var outputLock = new object(); // For thread-safe output
                var progressLock = new object(); // For thread-safe progress tracking
                var matchCount = 0; // Track number of actual matches                // Configure maximum parallelism for all datasets - use all available power!
                var parallelOptions = new ParallelOptions();
                parallelOptions.MaxDegreeOfParallelism = Math.Max(
                    1,
                    Environment.ProcessorCount
                );
                void ProcessHit(string hit)
                {
                    if (hit.Length == 0)
                    {
                        return;
                    }

                    var matchedHit = StandardHitProcessingCore.TryMatchHit(
                        hit,
                        fileStrings,
                        compiledRegexes,
                        off,
                        currentFile
                    );

                    if (matchedHit is not null)
                    {
                        lock (outputLock)
                        {
                            matchCount++;

                            var suppressConsoleOutput = q && !string.IsNullOrEmpty(o);
                            if (s == false && !suppressConsoleOutput)
                            {
                                Log.Information("{Hit}", matchedHit.RawHit);
                            }

                            csvHeaderWritten = StandardHitProcessingCore.WriteMatchedHit(
                                matchedHit,
                                isCsvOutput,
                                csvHeaderWritten,
                                sw
                            );
                        }
                    }

                    if (isLargeDataset)
                    {
                        lock (progressLock)
                        {
                            processedCount++;
                            if (DateTime.Now.Subtract(lastProgressReport).TotalSeconds >= 10)
                            {
                                if (!q)
                                {
                                    Log.Information(
                                        "Post-processing progress: {Processed:N0} / {Total:N0} strings ({Percent:F1}%)",
                                        processedCount,
                                        hitsList.Count,
                                        (double)processedCount / hitsList.Count * 100
                                    );
                                }
                                lastProgressReport = DateTime.Now;
                            }
                        }
                    }
                }

                if (orderedHits is null)
                {
                    Parallel.ForEach(hitsList, parallelOptions, ProcessHit);
                }
                else
                {
                    foreach (var hit in hitsList)
                    {
                        ProcessHit(hit);
                    }
                }

                // Update counter with actual matches found
                counter = matchCount;
            } // End of conditional post-processing

            if (q)
            {
                continue;
            }

            Console.WriteLine();

            if (withBoundaryHits)
            {
                Log.Information(
                    "** Strings prefixed with 2 spaces are hits found across chunk boundaries **"
                );
                Console.WriteLine();
            }

            if (counter == 1)
            {
                Log.Information(
                    "Found {Counter:N0} string in {TotalSeconds:N3} seconds. Average strings/sec: {Hits:N0}",
                    counter,
                    _sw.Elapsed.TotalSeconds,
                    hits.Count / _sw.Elapsed.TotalSeconds
                );
            }
            else
            {
                Log.Information(
                    "Found {Counter:N0} strings in {TotalSeconds:N3} seconds. Average strings/sec: {Hits:N0}",
                    counter,
                    _sw.Elapsed.TotalSeconds,
                    hits.Count / _sw.Elapsed.TotalSeconds
                );
            }

        skipGeneralProcessing:
            globalCounter += counter;
            globalHits += hits.Count;
            globalTimespan += _sw.Elapsed.TotalSeconds;
            if (files.Count > 1)
            {
                Log.Information(
                    "-------------------------------------------------------------------------------------"
                );
                Console.WriteLine();
            }
        }

        if (_debug)
        {
            Console.Error.WriteLine(
                $"Extraction work split: CPU {processingBackend.CpuChunks:N0} chunk(s), CUDA {processingBackend.GpuChunks:N0} chunk(s)."
            );
        }

        if (q || files.Count <= 1)
        {
            outputCompletion.MarkCompleted();
            Console.WriteLine();
            return;
        }

        if (globalCounter == 1)
        {
            Log.Information(
                "Total across {FilesCount:N0} files: Found {GlobalCounter:N0} string in {GlobalTimespan:N3} seconds. Average strings/sec: {GlobalAve:N0}",
                files.Count,
                globalCounter,
                globalTimespan,
                globalHits / globalTimespan
            );
        }
        else
        {
            Log.Information(
                "Total across {FilesCount:N0} files: Found {GlobalCounter:N0} strings in {GlobalTimespan:N3} seconds. Average strings/sec: {GlobalAve:N0}",
                files.Count,
                globalCounter,
                globalTimespan,
                globalHits / globalTimespan
            );
        }

        outputCompletion.MarkCompleted();
        Console.WriteLine();
    }

    /// <summary>
    /// Processes a single chunk of data for string extraction
    /// </summary>
    private static List<string> ProcessChunk(
        DataChunk chunk,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool off,
        int cp,
        string ar,
        string ur
    )
    {
        if (_debug)
        {
            Console.Error.WriteLine(
                $"[Chunk {chunk.ChunkIndex}] Starting processing of {chunk.ValidBytes:N0} bytes at offset {chunk.FileOffset:N0}"
            );
        }
        var chunkStopwatch = Stopwatch.StartNew();
        var validChunk = chunk.Data.AsSpan(0, chunk.ValidBytes);
        var finalResults = ChunkProcessingCore.ProcessChunk(
            validChunk,
            chunk.FileOffset,
            chunk.IsBoundaryChunk,
            minLength,
            maxLength,
            asciiSearch,
            unicodeSearch,
            off,
            ar,
            ur,
            cp
        );
        chunkStopwatch.Stop();
        if (_debug)
        {
            Console.Error.WriteLine(
                $"[Chunk {chunk.ChunkIndex}] CPU completed in {chunkStopwatch.ElapsedMilliseconds}ms, found {finalResults.Count} strings"
            );
        }

        return finalResults;
    }

    /// <summary>
    /// Reads chunks from the stream in a producer-consumer pattern
    /// </summary>
    private static async Task<List<DataChunk>> ReadChunksAsync(
        MappedStream mappedStream,
        long totalBytes,
        int chunkSizeBytes,
        long startOffset = 0,
        bool isBoundaryMode = false,
        int boundaryChunkSize = 0,
        int maxLength = 0
    )
    {
        return await ChunkReadingCore.ReadChunksAsync(
            mappedStream,
            totalBytes,
            chunkSizeBytes,
            startOffset,
            isBoundaryMode,
            boundaryChunkSize,
            ConcurrentConfig.ReadAheadChunks,
            ByteArrayPool.Rent,
            array => ByteArrayPool.Return(array)
        );
    }

    private static SparseStream OpenFile(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new NotSupportedException(
                "Raw disk access not supported on non-Windows systems. Exiting\r\n"
            );
        }

        var rawPath = path.Substring(3);
        if (_fileSystem != null)
        {
            return _fileSystem.OpenFile(rawPath, FileMode.Open, FileAccess.Read);
        }

        var disk = new RawDisk(path.ToLowerInvariant().First());
        var rawDiskStream = disk.CreateDiskStream();
        _fileSystem = new NtfsFileSystem(rawDiskStream);

        return _fileSystem.OpenFile(rawPath, FileMode.Open, FileAccess.Read);
    }

    private static string GetSizeReadable(long i)
    {
        return RuntimeUtilityCore.GetSizeReadable(i);
    }

    private static void SetupPatterns()
    {
        RegExDesc.Clear();
        RegExPatterns.Clear();

        foreach (var description in BuiltInPatternCatalog.Descriptions)
        {
            RegExDesc[description.Key] = description.Value;
        }

        foreach (var pattern in BuiltInPatternCatalog.Patterns)
        {
            RegExPatterns[pattern.Key] = pattern.Value;
        }
    }

    /// <summary>
    /// Gets the friendly name for a regex pattern from the built-in patterns dictionary
    /// </summary>
    /// <param name="regexPattern">The regex pattern string</param>
    /// <returns>The friendly name if found, otherwise null</returns>
    private static string GetRegexPatternName(string regexPattern)
    {
        // Find the key in RegExPatterns that matches this pattern
        foreach (var kvp in RegExPatterns)
        {
            if (kvp.Value.Equals(regexPattern, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Key;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses a character range string like "[\x20-\x7E]" into min and max byte values.
    /// </summary>
    private static (byte minChar, byte maxChar) ParseCharRange(string ar)
    {
        return SearchCore.ParseCharRange(ar);
    }

    // private static void AddHighlightingRules(List<string> words, bool isRegEx = false)
    // {
    //     var target = (ColoredConsoleTarget)LogManager.Configuration.FindTargetByName("console");
    //     var rule = target.WordHighlightingRules.FirstOrDefault();
    //
    //     var bgColor = ConsoleOutputColor.Green;
    //     var fgColor = ConsoleOutputColor.Red;
    //
    //     if (rule != null)
    //     {
    //         bgColor = rule.BackgroundColor;
    //         fgColor = rule.ForegroundColor;
    //     }
    //
    //     foreach (var word in words)
    //     {
    //         var r = new ConsoleWordHighlightingRule { IgnoreCase = true };
    //         if (isRegEx)
    //         {
    //             r.Regex = word;
    //         }
    //         else
    //         {
    //             r.Text = word;
    //         }
    //
    //         r.ForegroundColor = fgColor;
    //         r.BackgroundColor = bgColor;
    //    //         r.WholeWords = false;
    //         target.WordHighlightingRules.Add(r);
    //     }
    // }

    internal static IReadOnlyList<string> OrderHits(
        IEnumerable<string> hits,
        bool alphabetically,
        bool byLength,
        bool includeOffset
    )
    {
        var ordered = hits.ToList();
        if (!alphabetically && !byLength)
        {
            return ordered;
        }

        static string DataForSort(string hit, bool includeOffset)
        {
            return RegexOutputCore.ParseHit(hit, includeOffset).Data;
        }

        ordered.Sort(
            (left, right) =>
            {
                var leftData = DataForSort(left, includeOffset);
                var rightData = DataForSort(right, includeOffset);
                if (byLength)
                {
                    var lengthComparison = leftData.Length.CompareTo(rightData.Length);
                    if (lengthComparison != 0)
                    {
                        return lengthComparison;
                    }
                }

                return StringComparer.OrdinalIgnoreCase.Compare(leftData, rightData);
            }
        );
        return ordered;
    }

#if LEGACY_ILGPU_EXPERIMENT
    private static List<string> GetUnicodeHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        bool originalOffBool,
        string ur
    )
    {
        return SearchCore.GetUnicodeHits(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            originalOffBool,
            ur
        );
    }

    private static void ProcessHits(
        List<Hit> hits,
        ReadOnlySpan<byte> chunk, // Changed from byte[]
        long fileOffset,
        bool ascii,
        bool unicode,
        int minLength,
        int maxLength,
        bool originalOffBool,
        string offSeparator,
        int cp,
        string ar,
        string ur,
        List<string> searchStrings,
        List<string> regexStrings,
        bool ro,
        StreamWriter sw,
        bool s // quiet mode for logging
    )
    {
        var enc = Encoding.GetEncoding(cp);

        foreach (var hit in hits)
        {
            string hitString;
            // string hitType; // Removed unused variable

            long actualHitOffsetInChunk = hit.Offset - fileOffset;

            if (
                actualHitOffsetInChunk < 0
                || actualHitOffsetInChunk >= chunk.Length
                || (actualHitOffsetInChunk + hit.Length) > chunk.Length
            )
            {
                if (_trace)
                    Log.Warning(
                        "Skipping hit with out-of-bounds chunk offset. HitOffset: {HitOffset}, FileOffset: {FileOffset}, ChunkLength: {ChunkLength}, HitLength: {HitLength}",
                        hit.Offset,
                        fileOffset,
                        chunk.Length,
                        hit.Length
                    );
                continue;
            }

            if (hit.Encoding == Hit.HitEncoding.Ascii)
            {
                // hitType = "A"; // Removed assignment to unused variable
                hitString = enc.GetString(chunk.Slice((int)actualHitOffsetInChunk, hit.Length));
            }
            else // Unicode
            {
                // hitType = "U"; // Removed assignment to unused variable
                if (hit.Length % 2 != 0)
                {
                    if (_trace)
                        Log.Warning(
                            "Skipping Unicode hit with odd length. Offset: {HitOffset}, Length: {HitLength}",
                            hit.Offset,
                            hit.Length
                        );
                    continue;
                }
                hitString = Encoding.Unicode.GetString(
                    chunk.Slice((int)actualHitOffsetInChunk, hit.Length)
                );
            } // Process the hitString as needed
        }
    }

    private static List<string> GetAsciiHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffsetInFile,
        bool originalOffBool,
        int cp,
        string ar
    )
    {
        return SearchCore.GetAsciiHits(
            chunk,
            minLength,
            maxLength,
            currentOffsetInFile,
            originalOffBool,
            ar
        );
    }

    /// <summary>
    /// Optimized ASCII string scanning - uses vectorized operations when possible
    /// </summary>
    private static List<string> GetAsciiHitsOptimized(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffsetInFile,
        bool originalOffBool,
        int cp,
        string ar
    )
    {
        var (minChar, maxChar) = ParseCharRange(ar);

        // PERFORMANCE OPTIMIZATION: Use hit-based extraction directly
        var hits = FindAsciiStringHits(
            chunk,
            minLength,
            maxLength,
            currentOffsetInFile,
            minChar,
            maxChar
        );
        return MaterializeStringHits(chunk, hits, originalOffBool);
    }

    /// <summary>
    /// Fast ASCII string scanning with optimized character validation
    /// </summary>
    private static List<string> GetAsciiHitsFast(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffsetInFile,
        bool originalOffBool,
        byte minChar,
        byte maxChar
    )
    {
        // PERFORMANCE OPTIMIZATION: Use hit-position detection for minimal allocations
        var hits = FindAsciiStringHits(
            chunk,
            minLength,
            maxLength,
            currentOffsetInFile,
            minChar,
            maxChar
        );
        return MaterializeStringHits(chunk, hits, originalOffBool);
    }

    private static List<string> GetAsciiHitsGpu(
        byte[] chunk,
        int bytesRead,
        int minLength,
        int maxLength,
        long currentOffsetInFile,
        string offSeparator,
        int cp,
        string ar,
        bool originalOffBool
    )
    {
        var results = new List<string>();

        // Check if GPU should be used for processing
        // GPU processing has significant overhead, so only use it for larger chunks
        const int MIN_GPU_CHUNK_SIZE = 1024 * 1024; // 1MB minimum for GPU processing

        if (
            GpuAccelerator == null
            || GpuContext == null
            || bytesRead == 0
            || bytesRead < MIN_GPU_CHUNK_SIZE
        )
        {
            // Fallback to CPU if GPU is not available/initialized, chunk is empty, or chunk is too small
            // Only log for debugging purposes if chunk is too small but GPU is available
            if (
                GpuAccelerator != null
                && GpuContext != null
                && bytesRead > 0
                && bytesRead < MIN_GPU_CHUNK_SIZE
            )
            {
                // Uncomment for debugging: Console.WriteLine($"[GPU] Chunk too small ({bytesRead} bytes < {MIN_GPU_CHUNK_SIZE}), using CPU");
            }

            return GetAsciiHits(
                chunk.AsSpan(0, bytesRead), // Pass as ReadOnlySpan with correct size
                minLength,
                maxLength,
                currentOffsetInFile,
                originalOffBool,
                cp,
                ar
            );
        }

        // Wait for GPU semaphore with timeout- if too many GPU operations are running, fallback to CPU
        if (!GpuSemaphore.Wait(TimeSpan.FromSeconds(1)))
        {
            Console.Error.WriteLine(
                $"[GPU] Too many concurrent GPU operations, falling back to CPU for chunk at offset {currentOffsetInFile}"
            );
            return GetAsciiHits(
                chunk.AsSpan(0, bytesRead),
                minLength,
                maxLength,
                currentOffsetInFile,
                originalOffBool,
                cp,
                ar
            );
        } // GPU processing enabled - add profiling information
        var gpuStopwatch = Stopwatch.StartNew();
        byte[] validChunk = null; // Declare here so it's accessible in finally block

        try
        {
            // Implement smart memory management for GPU processing
            // First, check if this chunk is too large for GPU processing
            const long MAX_GPU_CHUNK_SIZE = 512L * 1024 * 1024; // 512 MB max per chunk

            if (bytesRead > MAX_GPU_CHUNK_SIZE)
            {
                Console.Error.WriteLine(
                    $"[GPU] Chunk size ({bytesRead / (1024 * 1024)} MB) exceeds GPU processing limit ({MAX_GPU_CHUNK_SIZE / (1024 * 1024)} MB). Falling back to CPU."
                );
                return GetAsciiHits(
                    chunk.AsSpan(0, bytesRead),
                    minLength,
                    maxLength,
                    currentOffsetInFile,
                    originalOffBool,
                    cp,
                    ar
                );
            }

            // Limit hits buffer to a reasonable size to avoid memory issues
            // For large chunks, we don't expect every byte to be a hit
            int maxReasonableHits = Math.Min(1024 * 1024, bytesRead / 10); // At most 1M hits or 10% of chunk size
            int estimatedMaxHits = Math.Max(1024, maxReasonableHits);

            // Check if we can allocate the required GPU memory before attempting
            long requiredMemory =
                bytesRead + (estimatedMaxHits * Marshal.SizeOf<GpuHit>()) + (1024 * 1024); // data + hits + 1MB misc

            long availableMemory = GpuAccelerator.MemorySize - (GpuAccelerator.MemorySize / 5); // Leave 20% buffer

            if (requiredMemory > availableMemory)
            {
                Console.Error.WriteLine(
                    $"[GPU] Insufficient GPU memory ({requiredMemory / (1024 * 1024)} MB required, {availableMemory / (1024 * 1024)} MB available). Falling back to CPU."
                );
                return GetAsciiHits(
                    chunk.AsSpan(0, bytesRead),
                    minLength,
                    maxLength,
                    currentOffsetInFile,
                    originalOffBool,
                    cp,
                    ar
                );
            }

            // Additional validation for ILGPU buffer allocation requirements
            // ILGPU requires minimum buffer sizes to avoid allocation errors
            const int MIN_BUFFER_SIZE = 256; // Minimum buffer size for ILGPU
            if (bytesRead < MIN_BUFFER_SIZE || estimatedMaxHits < 1)
            {
                return GetAsciiHits(
                    chunk.AsSpan(0, bytesRead),
                    minLength,
                    maxLength,
                    currentOffsetInFile,
                    originalOffBool,
                    cp,
                    ar
                );
            }

            using var dataBuffer = GpuAccelerator.Allocate1D<byte>(bytesRead);
            using var hitsBuffer = GpuAccelerator.Allocate1D<GpuHit>(estimatedMaxHits);
            using var hitCountBuffer = GpuAccelerator.Allocate1D<int>(1);
            // Copy only the valid bytes to GPU
            validChunk = ByteArrayPool.Rent(bytesRead);
            Array.Copy(chunk, 0, validChunk, 0, bytesRead);
            dataBuffer.CopyFromCPU(validChunk);
            hitCountBuffer.MemSetToZero();

            // Optimize thread block configuration for better GPU utilization
            // For small data, use fewer threads to reduce overhead
            var threadsPerBlock = bytesRead < 1000 ? 32 : 256;

            // Parse ASCII range for GPU processing
            var (minChar, maxChar) = ParseCharRange(ar);

            // Load and compile the kernel with optimized grouping
            var kernel = GpuAccelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<byte>,
                int,
                int,
                long,
                byte,
                byte,
                ArrayView<GpuHit>,
                ArrayView<int>
            >(AsciiScanKernel);

            // Launch with single thread for now (can optimize later)
            kernel(
                1, // Launch just 1 thread since our kernel uses index == 0 anyway
                dataBuffer.View,
                minLength,
                maxLength,
                currentOffsetInFile,
                minChar,
                maxChar,
                hitsBuffer.View,
                hitCountBuffer.View
            );

            GpuAccelerator.Synchronize(); // Wait for the kernel to complete
            int numHitsFound = hitCountBuffer.GetAsArray1D()[0];
            int hitsToRetrieve = Math.Min(numHitsFound, estimatedMaxHits);

            if (hitsToRetrieve > 0)
            {
                // Allocate a CPU-side array for all potential hits copied from the GPU.
                var allGpuHitsArray = new GpuHit[estimatedMaxHits];
                hitsBuffer.CopyToCPU(allGpuHitsArray); // Simple overload to copy the whole buffer.

                // Now process only the actual 'hitsToRetrieve' from the copied array.
                for (int i = 0; i < hitsToRetrieve; ++i)
                {
                    var hit = allGpuHitsArray[i];
                    long chunkRelativeOffset = hit.Offset - currentOffsetInFile;

                    if (
                        chunkRelativeOffset >= 0
                        && chunkRelativeOffset < chunk.Length
                        && (chunkRelativeOffset + hit.Length) <= chunk.Length
                    )
                    {
                        string foundString = Encoding.ASCII.GetString(
                            chunk,
                            (int)chunkRelativeOffset,
                            hit.Length
                        );
                        // Only add offset if the user requested it (--off flag)
                        if (!string.IsNullOrEmpty(offSeparator))
                        {
                            var offsetString = originalOffBool
                                ? $"{hit.Offset}"
                                : $"0x{hit.Offset:X}";
                            results.Add($"{offsetString}{offSeparator}{foundString}");
                        }
                        else
                        {
                            // Just the string, no offset
                            results.Add(foundString);
                        }
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            $"Skipping GPU hit with invalid calculated chunk-relative offset. AbsoluteOffset: {hit.Offset}, CurrentFileOffset: {currentOffsetInFile}, ChunkRelOffset: {chunkRelativeOffset}, Length: {hit.Length}, ChunkSize: {chunk.Length}"
                        );
                    }
                }
            }
            else
            {
                // No hits found or buffer was too small (hitsToRetrieve is 0)
            }

            // Fallback or empty list if no GPU hits processed
            if (results.Count == 0 && numHitsFound > 0)
            {
                // This indicates an issue with retrieving or processing GPU results.
                // Consider logging this or falling back.
                // For now, returning empty and relying on CPU fallback if GpuAccelerator was null.
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"GPU processing error in GetAsciiHitsGpu: {ex.Message}. Falling back to CPU."
            );
            Console.Error.WriteLine(ex.StackTrace);
            return GetAsciiHits(
                chunk.AsSpan(), // Pass as ReadOnlySpan
                minLength,
                maxLength,
                currentOffsetInFile,
                originalOffBool,
                cp,
                ar
            ); // Fallback to CPU
        }
        finally
        {
            // Return rented array to pool if it was allocated
            if (validChunk != null)
            {
                ByteArrayPool.Return(validChunk);
            }

            // Always release the GPU semaphore
            GpuSemaphore.Release();
        }
        gpuStopwatch.Stop();
        Console.Error.WriteLine(
            $"[GPU] Completed processing {bytesRead:N0} bytes in {gpuStopwatch.ElapsedMilliseconds}ms, found {results.Count} strings"
        );

        return results;
    }

    /// <summary>
    /// GPU Kernel for scanning ASCII strings that matches CPU algorithm
    /// Uses state machine approach to build strings incrementally, just like CPU StringBuilder
    /// </summary>
    private static void AsciiScanKernel(
        Index1D index, // Thread index
        ArrayView<byte> data, // Chunk of data to scan
        int minLength, // Minimum string length
        int maxLength, // Maximum string length (0 = no limit)
        long fileChunkBaseOffset, // Base offset of this data chunk in the original file
        byte minChar, // Minimum ASCII character value
        byte maxChar, // Maximum ASCII character value
        ArrayView<GpuHit> hits, // Output buffer for hits
        ArrayView<int> hitCount // Single-element array to atomically count hits
    )
    {
        // Only process with first thread for simplicity and correctness
        if (index != 0)
            return;

        int dataLength = (int)data.Length;
        if (dataLength == 0)
            return; // State machine variables (like CPU StringBuilder approach)
        int stringStart = -1;
        int stringLength = 0;
        bool inString = false;
        bool emittedCurrentString = false; // Track if we've already emitted this string

        // Process each byte in the data
        for (int i = 0; i < dataLength; i++)
        {
            byte currentByte = data[i];
            bool isValidChar = currentByte >= minChar && currentByte <= maxChar;

            if (isValidChar)
            {
                if (!inString)
                {
                    // Start of a new string
                    stringStart = i;
                    stringLength = 1;
                    inString = true;
                    emittedCurrentString = false;
                }
                else
                {
                    // Continue building the string
                    stringLength++;

                    // Check if we've hit the maximum length limit
                    if (maxLength > 0 && stringLength > maxLength && !emittedCurrentString)
                    {
                        // Truncate to maxLength and emit (matching CPU behavior)
                        if (maxLength >= minLength)
                        {
                            int hitIndex = Atomic.Add(ref hitCount[0], 1);
                            if (hitIndex < hits.Length)
                            {
                                hits[hitIndex] = new GpuHit
                                {
                                    Offset = fileChunkBaseOffset + stringStart,
                                    Length = maxLength,
                                };
                            }
                            emittedCurrentString = true;
                        }

                        // Continue discarding characters in this overlength string
                        // Don't reset inString - keep discarding until we hit an invalid char
                        // This matches CPU behavior of truncating but not starting new strings
                    }
                }
            }
            else
            { // End of string - emit if long enough and not already emitted
                if (inString && stringLength >= minLength && !emittedCurrentString)
                {
                    int hitIndex = Atomic.Add(ref hitCount[0], 1);
                    if (hitIndex < hits.Length)
                    {
                        // Use actual string length or maxLength, whichever is smaller

                        int emitLength =
                            (maxLength > 0 && stringLength > maxLength) ? maxLength : stringLength;
                        hits[hitIndex] = new GpuHit
                        {
                            Offset = fileChunkBaseOffset + stringStart,
                            Length = emitLength,
                        };
                    }
                }

                // Reset state
                inString = false;
                stringStart = -1;
                stringLength = 0;
            }
        } // Handle string that extends to end of data
        if (inString && stringLength >= minLength && !emittedCurrentString)
        {
            int hitIndex = Atomic.Add(ref hitCount[0], 1);
            if (hitIndex < hits.Length)
            {
                // Use actual string length or maxLength, whichever is smaller
                int emitLength =
                    (maxLength > 0 && stringLength > maxLength) ? maxLength : stringLength;
                hits[hitIndex] = new GpuHit
                {
                    Offset = fileChunkBaseOffset + stringStart,
                    Length = emitLength,
                };
            }
        }
    }
#endif

    /// <summary>
    /// Processes main file chunks concurrently using enhanced pipeline parallelism with streaming output
    /// </summary>
    private static async Task<long> ProcessFileChunksConcurrentlyStreamingAsync(
        MappedStream mappedStream,
        long fileSizeBytes,
        int chunkSizeBytes,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool off,
        int cp,
        string ar,
        string ur,
        bool quiet,
        long totalChunks,
        ProgressTracker progressTracker,
        StreamWriter outputWriter = null,
        HashSet<string> resultsSet = null,
        ProcessingBackendSession processingBackend = null,
        ProcessingMode processingMode = ProcessingMode.Cpu,
        Func<List<string>, List<string>> resultTransform = null
    )
    {
        using var pipeline = new ChunkProcessingPipeline(
            processingBackend,
            processingMode
        );

        // Create async enumerable of chunks
        var chunks = ReadChunksAsyncEnumerable(
            mappedStream,
            fileSizeBytes,
            chunkSizeBytes,
            0,
            false
        );

        // Process chunks through the streaming pipeline
        var totalResults = await pipeline.ProcessChunksStreamingAsync(
            chunks,
            minLength,
            maxLength,
            asciiSearch,
            unicodeSearch,
            off,
            cp,
            ar,
            ur,
            progressTracker,
            outputWriter,
            resultsSet,
            resultTransform
        );

        return totalResults;
    }

    /// <summary>
    /// Processes main file chunks concurrently using enhanced pipeline parallelism (legacy - collects in memory)
    /// </summary>
    private static async Task ProcessFileChunksConcurrentlyAsync(
        MappedStream mappedStream,
        long fileSizeBytes,
        int chunkSizeBytes,
        HashSet<string> hits,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool off,
        int cp,
        string ar,
        string ur,
        bool quiet,
        long totalChunks,
        ProgressTracker progressTracker,
        ProcessingBackendSession processingBackend = null,
        ProcessingMode processingMode = ProcessingMode.Cpu
    )
    {
        using var pipeline = new ChunkProcessingPipeline(
            processingBackend,
            processingMode
        );

        // Create async enumerable of chunks
        var chunks = ReadChunksAsyncEnumerable(
            mappedStream,
            fileSizeBytes,
            chunkSizeBytes,
            0,
            false
        );

        // Process chunks through the pipeline
        var results = await pipeline.ProcessChunksAsync(
            chunks,
            minLength,
            maxLength,
            asciiSearch,
            unicodeSearch,
            off,
            cp,
            ar,
            ur,
            progressTracker
        );

        // Add results to the main collection
        lock (hits)
        {
            foreach (var result in results)
            {
                hits.Add(result);
            }
        }
    }

    /// <summary>
    /// Creates an async enumerable of DataChunks for processing
    /// </summary>
    internal static async IAsyncEnumerable<DataChunk> ReadChunksAsyncEnumerable(
        MappedStream mappedStream,
        long fileSizeBytes,
        int chunkSizeBytes,
        long startOffset = 0,
        bool isBoundaryMode = false,
        int boundaryChunkSize = 0
    )
    {
        long offset = startOffset;
        int chunkIndex = 0;

        while (offset < fileSizeBytes)
        {
            var chunks = await ReadChunksAsync(
                mappedStream,
                fileSizeBytes,
                chunkSizeBytes,
                offset,
                isBoundaryMode,
                boundaryChunkSize
            );

            if (chunks.Count == 0)
                break;

            foreach (var chunk in chunks)
            {
                var indexedChunk = chunk;
                indexedChunk.ChunkIndex = chunkIndex++;
                yield return indexedChunk;
            }

            var lastChunk = chunks.Last();
            offset = isBoundaryMode
                ? lastChunk.FileOffset + chunkSizeBytes
                : lastChunk.FileOffset + lastChunk.ValidBytes;
        }
    }

    /// <summary>
    /// Processes boundary chunks concurrently using enhanced pipeline parallelism with streaming output
    /// </summary>
    private static async Task<long> ProcessBoundaryChunksConcurrentlyStreamingAsync(
        MappedStream mappedStream,
        long fileSizeBytes,
        int chunkSizeBytes,
        int boundaryChunkSize,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool off,
        int cp,
        string ar,
        string ur,
        bool quiet,
        StreamWriter outputWriter = null,
        HashSet<string> resultsSet = null,
        ProcessingBackendSession processingBackend = null,
        ProcessingMode processingMode = ProcessingMode.Cpu,
        Func<List<string>, List<string>> resultTransform = null
    )
    {
        using var pipeline = new ChunkProcessingPipeline(
            processingBackend,
            processingMode
        );

        long offset = Math.Max(0, chunkSizeBytes - minLength * 20L);
        var boundaryChunkCount =
            offset + boundaryChunkSize > fileSizeBytes
                ? 1
                : ((fileSizeBytes - boundaryChunkSize - offset) / chunkSizeBytes) + 1;

        // Create async enumerable of boundary chunks
        var chunks = ReadChunksAsyncEnumerable(
            mappedStream,
            fileSizeBytes,
            chunkSizeBytes,
            offset,
            true, // boundary mode
            boundaryChunkSize
        );

        // Process chunks through the streaming pipeline
        var totalResults = await pipeline.ProcessChunksStreamingAsync(
            chunks,
            minLength,
            maxLength,
            asciiSearch,
            unicodeSearch,
            off,
            cp,
            ar,
            ur,
            new ProgressTracker(Math.Max(1, boundaryChunkCount), quiet),
            outputWriter,
            resultsSet,
            resultTransform
        );

        return totalResults;
    }

    /// <summary>
    /// Processes boundary chunks concurrently using enhanced pipeline parallelism (legacy - collects in memory)
    /// </summary>
    private static async Task<bool> ProcessBoundaryChunksConcurrentlyAsync(
        MappedStream mappedStream,
        long fileSizeBytes,
        int chunkSizeBytes,
        int boundaryChunkSize,
        HashSet<string> hits,
        int minLength,
        int maxLength,
        bool asciiSearch,
        bool unicodeSearch,
        bool off,
        int cp,
        string ar,
        string ur,
        bool quiet,
        ProcessingBackendSession processingBackend = null,
        ProcessingMode processingMode = ProcessingMode.Cpu
    )
    {
        using var pipeline = new ChunkProcessingPipeline(
            processingBackend,
            processingMode
        );

        long offset = Math.Max(0, chunkSizeBytes - minLength * 20L);
        var boundaryChunkCount =
            offset + boundaryChunkSize > fileSizeBytes
                ? 1
                : ((fileSizeBytes - boundaryChunkSize - offset) / chunkSizeBytes) + 1;

        // Create async enumerable of boundary chunks
        var chunks = ReadChunksAsyncEnumerable(
            mappedStream,
            fileSizeBytes,
            chunkSizeBytes,
            offset,
            true, // boundary mode
            boundaryChunkSize
        );

        // Process chunks through the pipeline
        var results = await pipeline.ProcessChunksAsync(
            chunks,
            minLength,
            maxLength,
            asciiSearch,
            unicodeSearch,
            off,
            cp,
            ar,
            ur,
            new ProgressTracker(Math.Max(1, boundaryChunkCount), quiet)
        );

        // Add results to the main collection
        lock (hits)
        {
            foreach (var result in results)
            {
                hits.Add(result);
            }
        }

        return results.Count > 0;
    }

    /// <summary>
    /// Calculates optimal CPU chunk size based on available memory and file characteristics - memory conservative
    /// </summary>
    private static int CalculateOptimalCpuChunkSizeMB()
    {
        try
        {
            // Get system memory information more conservatively
            var gcMemoryInfo = GC.GetGCMemoryInfo();
            long totalPhysicalMemory = gcMemoryInfo.TotalAvailableMemoryBytes;
            long currentlyUsed = GC.GetTotalMemory(false);

            // Calculate available memory much more conservatively
            long availableMemory = totalPhysicalMemory - currentlyUsed;

            // Use a much lower percentage of available memory to prevent OOM
            double memoryUsageFraction = 0.05; // Only use 5% of available memory for chunks
            int maxConcurrentChunks = ConcurrentConfig.MaxConcurrentChunks;
            int chunkSizeMB = ChunkSizingCore.CalculateCpuChunkSizeMBFromMemory(
                availableMemory,
                maxConcurrentChunks,
                memoryUsageFraction
            );

            if (_debug)
            {
                Console.Error.WriteLine(
                    $"Total Memory: {totalPhysicalMemory / (1024 * 1024)} MB, Available: {availableMemory / (1024 * 1024)} MB"
                );
                Console.Error.WriteLine(
                    $"Calculated conservative CPU chunk size: {chunkSizeMB} MB (max {maxConcurrentChunks} concurrent chunks, {memoryUsageFraction:P1} memory usage)"
                );
            }

            return chunkSizeMB;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Error calculating optimal CPU chunk size: {ex.Message}. Using conservative fallback."
            );
            // Conservative fallback - much smaller
            return Math.Min(64, Environment.ProcessorCount * 16); // At most 64MB, or 16MB per core
        }
    }

    /// <summary>
    /// Gets the optimal chunk size by choosing between GPU and CPU calculations, with file-size adaptation
    /// </summary>
    private static int GetOptimalChunkSize(
        long fileSizeBytes,
        ProcessingMode processingMode = ProcessingMode.Cpu
    )
    {
        try
        {
            int cpuChunkSize = CalculateOptimalCpuChunkSizeMB();
            var isGpuOnly = processingMode == ProcessingMode.Gpu;
            int adaptedChunkSize = isGpuOnly
                ? ChunkSizingCore.SelectParallelChunkSizeMB(
                    Math.Min(128, cpuChunkSize),
                    fileSizeBytes,
                    targetConcurrency: 2,
                    chunksPerWorker: 4
                )
                : ChunkSizingCore.SelectOptimalChunkSize(
                    gpuAvailable: false,
                    gpuChunkSizeMB: 0,
                    cpuChunkSizeMB: cpuChunkSize,
                    fileSizeBytes: fileSizeBytes
                );
            if (_debug)
            {
                Console.Error.WriteLine(
                    $"Using {processingMode.ToString().ToLowerInvariant()} chunk size: {adaptedChunkSize} MB (adapted for {GetSizeReadable(fileSizeBytes)} file)"
                );
            }
            return adaptedChunkSize;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error in GetOptimalChunkSize: {ex.Message}. Using fallback.");
            return 512; // Safe fallback
        }
    }

    /// <summary>
    /// Adapts chunk size based on file size for optimal PARALLELISM (smaller chunks = more parallel processing)
    /// </summary>
    private static int AdaptChunkSizeForFile(int baseChunkSizeMB, long fileSizeBytes)
    {
        return RuntimeUtilityCore.AdaptChunkSizeForFile(baseChunkSizeMB, fileSizeBytes);
    }

    /// <summary>
    /// Calculates optimal GPU chunk size based on available VRAM
    /// </summary>
#if LEGACY_ILGPU_EXPERIMENT
    private static int CalculateOptimalGpuChunkSizeMB()
    {
        try
        {
            if (GpuAccelerator == null)
            {
                return 0; // Indicate GPU not available
            }

            long totalGpuMemoryBytes = GpuAccelerator.MemorySize;
            long reserveMemoryBytes = 2L * 1024 * 1024 * 1024; // 2GB
            long usableGpuMemoryBytes = totalGpuMemoryBytes - reserveMemoryBytes;

            if (usableGpuMemoryBytes <= 0)
            {
                Console.Error.WriteLine(
                    "Not enough GPU VRAM to reserve 2GB. Will use CPU calculation."
                );
                return 0;
            }

            const int defaultMinStringLengthForCalc = 3; // Based on mOption default
            int sizeOfGpuHit = Marshal.SizeOf<GpuHit>(); // Should be 12 bytes
            double memoryFactor =
                1.0 + ((double)sizeOfGpuHit / defaultMinStringLengthForCalc);
            int finalMB = ChunkSizingCore.CalculateGpuChunkSizeMBFromMemory(
                usableGpuMemoryBytes,
                sizeOfGpuHit,
                defaultMinStringLengthForCalc
            );

            Console.Error.WriteLine(
                $"Total GPU VRAM: {totalGpuMemoryBytes / (1024 * 1024)} MB. Usable (Total - 2GB): {usableGpuMemoryBytes / (1024 * 1024)} MB."
            );
            Console.Error.WriteLine(
                $"Calculated dynamic chunk size for GPU: {finalMB} MB (using factor {memoryFactor:F2} for hits buffer)."
            );

            return finalMB;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Error calculating optimal GPU chunk size: {ex.Message}. Will use CPU calculation."
            );
            return 0;
        }
    }
#endif

    /// <summary>
    /// Enhanced producer-consumer pipeline for chunk processing with optimized concurrency
    /// </summary>
    public class ChunkProcessingPipeline : IDisposable
    {
        private readonly Channel<DataChunk> _chunkChannel;
        private readonly Channel<List<string>> _resultChannel;
        private readonly ChannelWriter<DataChunk> _chunkWriter;
        private readonly ChannelReader<DataChunk> _chunkReader;
        private readonly ChannelWriter<List<string>> _resultWriter;
        private readonly ChannelReader<List<string>> _resultReader;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly int _maxConcurrency;
        private readonly ProcessingBackendSession _processingBackend;
        private readonly ProcessingMode _processingMode;
        private volatile bool _disposed = false;

        public ChunkProcessingPipeline(int maxConcurrency = 0)
            : this(null, ProcessingMode.Cpu, maxConcurrency)
        {
        }

        internal ChunkProcessingPipeline(
            ProcessingBackendSession processingBackend,
            ProcessingMode processingMode,
            int maxConcurrency = 0
        )
        {
            _processingBackend = processingBackend;
            _processingMode = processingMode;
            _maxConcurrency =
                maxConcurrency > 0 ? maxConcurrency : ConcurrentConfig.OptimalDegreeOfParallelism;

            var chunkChannelOptions = new BoundedChannelOptions(
                ConcurrentConfig.ProducerConsumerBufferSize
            )
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            };

            var resultChannelOptions = new BoundedChannelOptions(
                Math.Max(
                    4,
                    Math.Min(16, ConcurrentConfig.ProducerConsumerBufferSize / 4)
                )
            )
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            };

            _chunkChannel = Channel.CreateBounded<DataChunk>(chunkChannelOptions);
            _resultChannel = Channel.CreateBounded<List<string>>(resultChannelOptions);

            _chunkWriter = _chunkChannel.Writer;
            _chunkReader = _chunkChannel.Reader;
            _resultWriter = _resultChannel.Writer;
            _resultReader = _resultChannel.Reader;

            _cancellationTokenSource = new CancellationTokenSource();
        }

        public async Task<long> ProcessChunksStreamingAsync(
            IAsyncEnumerable<DataChunk> chunks,
            int minLength,
            int maxLength,
            bool asciiSearch,
            bool unicodeSearch,
            bool off,
            int cp,
            string ar,
            string ur,
            ProgressTracker progressTracker,
            StreamWriter outputWriter = null,
            HashSet<string> resultsSet = null,
            Func<List<string>, List<string>> resultTransform = null
        )
        {
            var totalResultCount = 0L;
            var processingTask = StartProcessingWorkersAsync(
                minLength,
                maxLength,
                asciiSearch,
                unicodeSearch,
                off,
                cp,
                ar,
                ur,
                progressTracker,
                resultTransform
            );
            var resultCollectionTask = CollectResultsStreamingAsync(outputWriter, resultsSet);

            try
            {
                // Feed chunks into the pipeline
                await foreach (var chunk in chunks.WithCancellation(_cancellationTokenSource.Token))
                {
                    await _chunkWriter.WriteAsync(chunk, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                // A worker cancels the shared token before rethrowing its real failure.
                // Await it here so callers receive that failure instead of a masked
                // channel cancellation from the producer.
                await processingTask;
                throw;
            }
            finally
            {
                _chunkWriter.Complete();
            }

            // Wait for processing to complete
            await processingTask;
            _resultWriter.Complete();

            // Wait for result collection to complete
            totalResultCount = await resultCollectionTask;

            return totalResultCount;
        }

        // Legacy method for backward compatibility - now streams results
        public async Task<List<string>> ProcessChunksAsync(
            IAsyncEnumerable<DataChunk> chunks,
            int minLength,
            int maxLength,
            bool asciiSearch,
            bool unicodeSearch,
            bool off,
            int cp,
            string ar,
            string ur,
            ProgressTracker progressTracker
        )
        {
            var allResults = new List<string>();
            var processingTask = StartProcessingWorkersAsync(
                minLength,
                maxLength,
                asciiSearch,
                unicodeSearch,
                off,
                cp,
                ar,
                ur,
                progressTracker
            );
            var resultCollectionTask = CollectResultsLimitedAsync(allResults);

            try
            {
                // Feed chunks into the pipeline
                await foreach (var chunk in chunks.WithCancellation(_cancellationTokenSource.Token))
                {
                    await _chunkWriter.WriteAsync(chunk, _cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException) when (_cancellationTokenSource.IsCancellationRequested)
            {
                await processingTask;
                throw;
            }
            finally
            {
                _chunkWriter.Complete();
            }

            // Wait for processing to complete
            await processingTask;
            _resultWriter.Complete();

            // Wait for result collection to complete
            await resultCollectionTask;

            return allResults;
        }

        private async Task StartProcessingWorkersAsync(
            int minLength,
            int maxLength,
            bool asciiSearch,
            bool unicodeSearch,
            bool off,
            int cp,
            string ar,
            string ur,
            ProgressTracker progressTracker,
            Func<List<string>, List<string>> resultTransform = null
        )
        {
            var gpuWorkers =
                _processingMode is ProcessingMode.Gpu or ProcessingMode.Hybrid
                    ? Math.Min(2, _maxConcurrency)
                    : 0;
            var cpuWorkers = _processingMode switch
            {
                ProcessingMode.Gpu => 0,
                ProcessingMode.Hybrid => Math.Max(1, _maxConcurrency - gpuWorkers),
                _ => _maxConcurrency,
            };
            var workers = new List<Task>(cpuWorkers + gpuWorkers);

            // Register the CUDA reader first so a short hybrid run cannot be consumed
            // entirely by already-scheduled CPU workers before the GPU gets a chunk.
            for (int i = 0; i < gpuWorkers; i++)
            {
                workers.Add(
                    ProcessChunksWorkerAsync(
                        true,
                        minLength,
                        maxLength,
                        asciiSearch,
                        unicodeSearch,
                        off,
                        cp,
                        ar,
                        ur,
                        progressTracker,
                        resultTransform
                    )
                );
            }

            for (int i = 0; i < cpuWorkers; i++)
            {
                workers.Add(
                    ProcessChunksWorkerAsync(
                        false,
                        minLength,
                        maxLength,
                        asciiSearch,
                        unicodeSearch,
                        off,
                        cp,
                        ar,
                        ur,
                        progressTracker,
                        resultTransform
                    )
                );
            }

            await Task.WhenAll(workers);
        }

        private async Task ProcessChunksWorkerAsync(
            bool useGpu,
            int minLength,
            int maxLength,
            bool asciiSearch,
            bool unicodeSearch,
            bool off,
            int cp,
            string ar,
            string ur,
            ProgressTracker progressTracker,
            Func<List<string>, List<string>> resultTransform = null
        )
        {
            await foreach (var chunk in _chunkReader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                try
                {
                    List<string> results;
                    if (useGpu && _processingBackend?.IsGpuEnabled == true)
                    {
                        try
                        {
                            results = _processingBackend.ProcessGpuChunk(
                                chunk,
                                minLength,
                                maxLength,
                                asciiSearch,
                                unicodeSearch,
                                off,
                                cp,
                                ar,
                                ur
                            );

                            if (_debug)
                            {
                                Console.Error.WriteLine(
                                    $"[Chunk {chunk.ChunkIndex}] CUDA completed, found {results.Count} strings"
                                );
                            }
                        }
                        catch (Exception ex) when (_processingMode == ProcessingMode.Hybrid)
                        {
                            _processingBackend.DisableGpuAfterFailure(
                                ex,
                                Console.Error.WriteLine
                            );
                            results = ProcessChunk(
                                chunk,
                                minLength,
                                maxLength,
                                asciiSearch,
                                unicodeSearch,
                                off,
                                cp,
                                ar,
                                ur
                            );
                            _processingBackend.RecordCpuChunk();
                        }
                    }
                    else
                    {
                        results = ProcessChunk(
                            chunk,
                            minLength,
                            maxLength,
                            asciiSearch,
                            unicodeSearch,
                            off,
                            cp,
                            ar,
                            ur
                        );
                        _processingBackend?.RecordCpuChunk();
                    }

                    var extractedCount = results.Count;
                    if (resultTransform is not null)
                    {
                        results = resultTransform(results);
                    }

                    await _resultWriter.WriteAsync(results, _cancellationTokenSource.Token);
                    progressTracker.ReportChunkComplete(extractedCount);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await _cancellationTokenSource.CancelAsync();
                    throw;
                }
                finally
                {
                    ByteArrayPool.Return(chunk.Data);
                }
            }
        }

        private async Task CollectResultsAsync(List<string> allResults)
        {
            await foreach (
                var chunkResults in _resultReader.ReadAllAsync(_cancellationTokenSource.Token)
            )
            {
                allResults.AddRange(chunkResults);
            }
        }

        /// <summary>
        /// Streaming result collection that writes directly to output and flushes memory immediately
        /// </summary>
        private async Task<long> CollectResultsStreamingAsync(
            StreamWriter outputWriter,
            HashSet<string> resultsSet
        )
        {
            return await ResultCollectionCore.CollectStreamingAsync(
                _resultReader.ReadAllAsync(_cancellationTokenSource.Token),
                outputWriter,
                resultsSet
            );
        }

        /// <summary>
        /// Limited result collection with memory management for smaller files
        /// </summary>
        private async Task CollectResultsLimitedAsync(List<string> allResults)
        {
            await ResultCollectionCore.CollectLimitedAsync(
                _resultReader.ReadAllAsync(_cancellationTokenSource.Token),
                allResults,
                _debug
            );
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _cancellationTokenSource?.Cancel();
                while (_chunkReader.TryRead(out var unprocessedChunk))
                {
                    ByteArrayPool.Return(unprocessedChunk.Data);
                }
                _cancellationTokenSource?.Dispose();
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Partitioner for optimal load balancing across CPU cores
    /// </summary>
    public static class ChunkPartitioner
    {
        public static ParallelQuery<T> CreateOptimalPartitioner<T>(IEnumerable<T> source)
        {
            return source
                .AsParallel()
                .WithDegreeOfParallelism(ConcurrentConfig.OptimalDegreeOfParallelism)
                .WithExecutionMode(ParallelExecutionMode.ForceParallelism);
        }

        public static async IAsyncEnumerable<List<T>> CreateBatchedAsyncEnumerable<T>(
            IAsyncEnumerable<T> source,
            int batchSize
        )
        {
            await foreach (var batch in RuntimeUtilityCore.CreateBatchedAsyncEnumerable(source, batchSize))
            {
                yield return batch;
            }
        }
    }

    /// <summary>
    /// Finds ASCII string hit positions using the active CPU scanner.
    /// </summary>
    private static List<StringHitPosition> FindAsciiStringHits(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar = 32,
        byte maxChar = 126
    )
    {
        return SearchCore.FindAsciiStringHits(
            data,
            minLength,
            maxLength,
            fileOffset,
            minChar,
            maxChar
        );
    }

    /// <summary>
    /// Materialize string hits into actual strings - only called when needed
    /// </summary>
    private static List<string> MaterializeStringHits(
        ReadOnlySpan<byte> data,
        List<StringHitPosition> hits,
        bool includeOffset
    )
    {
        return SearchCore.MaterializeStringHits(data, hits, includeOffset);
    }

    /// <summary>
    /// Logs GPU initialization information if debug mode is enabled
    /// </summary>
#if LEGACY_ILGPU_EXPERIMENT
    private static void LogGpuInitializationInfo()
    {
        if (!_debug)
            return;

        if (GpuAccelerator != null)
        {
            Console.Error.WriteLine($"[DEBUG] GPU accelerator available: {GpuAccelerator.Name}");
            Console.Error.WriteLine(
                $"[DEBUG] GPU memory size: {GpuAccelerator.MemorySize / (1024 * 1024)} MB"
            );
            Console.Error.WriteLine($"[DEBUG] Dynamic chunk size: {DynamicChunkSizeMB} MB");
        }
        else
        {
            Console.Error.WriteLine("[DEBUG] GPU acceleration not available, using CPU only");
        }
    }
#endif

    /// <summary>
    /// Parses the lr parameter to handle comma-separated patterns and 'all' keyword
    /// </summary>
    /// <param name="lr">The lr parameter value</param>
    /// <returns>List of resolved regex patterns</returns>
    private static List<(string name, string pattern)> ParseRegexPatternsWithNames(string lr)
    {
        return SearchCore.ParseRegexPatternsWithNames(lr, RegExPatterns);
    }

    private static List<string> ParseRegexPatterns(string lr)
    {
        return SearchCore.ParseRegexPatterns(lr, RegExPatterns);
    }

    /// <summary>
    /// Processes hits concurrently against multiple regex patterns
    /// </summary>
    /// <param name="hits">The string hits to process</param>
    /// <param name="regexPatterns">List of regex patterns to match against</param>
    /// <param name="ro">Regex output mode</param>
    /// <param name="off">Show offset</param>
    /// <param name="s">Silent mode</param>
    /// <param name="sw">StreamWriter for output</param>
    /// <param name="q">Quiet mode</param>
    /// <param name="o">Output file path</param>
    /// <param name="currentFile">Current file being processed</param>    /// <param name="isCsvOutput">Whether output is CSV format</param>
    /// <param name="csvHeaderAlreadyWritten">Whether CSV header has already been written</param>
    /// <returns>Number of matches found</returns>
    public static async Task<int> ProcessRegexPatternsConcurrentlyAsync(
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
        IReadOnlyList<string> orderedHits = null,
        RegexWorkPartition? forcedPartition = null
    )
    {
        if (regexPatternsWithNames.Count == 0)
            return 0;

        // Write CSV header if this is CSV output, we have a StreamWriter, and header hasn't been written yet
        if (isCsvOutput && sw != null && !csvHeaderAlreadyWritten)
        {
            await sw.WriteLineAsync(RegexOutputCore.CsvHeader);
        }

        var lockObject = new object();
        var regexMap = RegexOutputCore.BuildRegexMap(regexPatternsWithNames);
        var suppressConsoleOutput = q && !string.IsNullOrEmpty(o);
        var regexTimeouts = 0;

        int ProcessHitForPattern(
            (string name, string pattern) patternInfo,
            string hit
        )
        {
            var (patternName, regString) = patternInfo;
            if (
                Volatile.Read(ref regexTimeouts) > 0
                || string.IsNullOrWhiteSpace(regString)
                || hit.Length == 0
            )
                return 0;

            try
            {
                var regex = regexMap[patternName];
                var parsedHit = RegexOutputCore.ParseHit(hit, off);
                if (ro)
                {
                    var records = RegexOutputCore
                        .CreateRecords(
                            parsedHit,
                            patternName,
                            regex,
                            regexOutput: true,
                            currentFile,
                            "Regex"
                        )
                        .ToList();
                    if (records.Count == 0)
                    {
                        return 0;
                    }

                    lock (lockObject)
                    {
                        foreach (var record in records)
                        {
                            if (!s && !suppressConsoleOutput)
                            {
                                Log.Information(
                                    "{Output}",
                                    RegexOutputCore.BuildRegexOnlyText(record)
                                );
                            }

                            if (isCsvOutput && sw != null)
                            {
                                sw.WriteLine(RegexOutputCore.BuildCsvLine(record));
                            }
                            else if (sw != null)
                            {
                                sw.WriteLine(RegexOutputCore.BuildRegexOnlyText(record));
                            }
                        }
                    }

                    return 1;
                }

                if (regex.IsMatch(parsedHit.Data))
                {
                    var record = RegexOutputCore.CreateRecords(
                        parsedHit,
                        patternName,
                        regex,
                        regexOutput: false,
                        currentFile,
                        "Regex"
                    ).Single();
                    var fullHitText = RegexOutputCore.BuildFullHitText(parsedHit);

                    lock (lockObject)
                    {
                        if (!s && !suppressConsoleOutput)
                        {
                            Log.Information("{Hit}", fullHitText);
                        }

                        if (isCsvOutput && sw != null)
                        {
                            sw.WriteLine(RegexOutputCore.BuildCsvLine(record));
                        }
                        else
                        {
                            sw?.WriteLine(fullHitText);
                        }
                    }

                    return 1;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                Interlocked.Increment(ref regexTimeouts);
            }

            return 0;
        }

        int ProcessPattern((string name, string pattern) patternInfo)
        {
            IEnumerable<string> sourceHits = orderedHits is not null ? orderedHits : hits;
            var localMatches = 0;
            foreach (var hit in sourceHits)
            {
                if (Volatile.Read(ref regexTimeouts) > 0)
                {
                    break;
                }

                localMatches += ProcessHitForPattern(patternInfo, hit);
            }

            return localMatches;
        }

        int totalMatches;
        if (orderedHits is not null)
        {
            totalMatches = regexPatternsWithNames.Sum(ProcessPattern);
        }
        else if (
            (
                forcedPartition
                ?? RegexParallelismPolicy.Choose(
                    hits.Count,
                    regexPatternsWithNames.Count,
                    Environment.ProcessorCount
                )
            ) == RegexWorkPartition.Hits
        )
        {
            totalMatches = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            };

            Parallel.ForEach(
                hits,
                parallelOptions,
                () => 0,
                (hit, loopState, localMatches) =>
                {
                    foreach (var patternInfo in regexPatternsWithNames)
                    {
                        if (Volatile.Read(ref regexTimeouts) > 0)
                        {
                            loopState.Stop();
                            break;
                        }

                        localMatches += ProcessHitForPattern(patternInfo, hit);
                    }

                    if (Volatile.Read(ref regexTimeouts) > 0)
                    {
                        loopState.Stop();
                    }

                    return localMatches;
                },
                localMatches => Interlocked.Add(ref totalMatches, localMatches)
            );
        }
        else
        {
            totalMatches = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
            };

            Parallel.ForEach(
                regexPatternsWithNames,
                parallelOptions,
                () => 0,
                (patternInfo, loopState, localMatches) =>
                {
                    var result = localMatches + ProcessPattern(patternInfo);
                    if (Volatile.Read(ref regexTimeouts) > 0)
                    {
                        loopState.Stop();
                    }

                    return result;
                },
                localMatches => Interlocked.Add(ref totalMatches, localMatches)
            );
        }

        if (regexTimeouts > 0)
        {
            throw new TimeoutException(
                $"{regexTimeouts:N0} regex evaluations timed out; the result set is incomplete."
            );
        }

        await Task.CompletedTask;
        return totalMatches;
    }
}
