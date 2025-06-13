using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices; // Keep one instance
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Streams;
using Exceptionless;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.Cuda; // Required for Cuda specific operations
using ILGPU.Runtime.OpenCL; // Required for OpenCL specific operations
using RawDiskLib;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using static ILGPU.Atomic; // Corrected: using static for the Atomic class
#if !NET6_0_OR_GREATER
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;
#else
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using File = System.IO.File;
using FileInfo = System.IO.FileInfo;
#endif

namespace bstrings;

// internal class Program // This was the old class declaration
public static partial class Program // Make it public and partial for ILGPU if needed, or ensure Program is public
{
    private static Stopwatch _sw;
    private static readonly Dictionary<string, string> RegExPatterns =
        new Dictionary<string, string>();
    private static readonly Dictionary<string, string> RegExDesc = new Dictionary<string, string>();

    private static readonly string Header =
        $"bstrings version {Assembly.GetExecutingAssembly().GetName().Version}"
        + "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)"
        + "\r\nhttps://github.com/EricZimmerman/bstrings";

    private static readonly string Footer =
        @"Examples: bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls URL"
        + "\r\n\t "
        + @"   bstrings.exe -f ""C:\Temp\someFile.txt"" --lr guid"
        + "\r\n\t "
        + @"   bstrings.exe -f ""C:\Temp\someFile.txt"" --lr ""guid,cc,ssn"""
        + "\r\n\t "
        + @"   bstrings.exe -f ""C:\Temp\someFile.txt"" --lr all"
        + "\r\n\t "
        + @"   bstrings.exe -f ""C:\Temp\aBigFile.bin"" --fs c:\temp\searchStrings.txt --fr c:\temp\searchRegex.txt"
        + "\r\n\t "
        + @"   bstrings.exe -d ""C:\Temp"" --mask ""*.dll"""
        + "\r\n\t "
        + @"   bstrings.exe -d ""C:\Temp"" --ar ""[\x20-\x37]"""
        + "\r\n\t "
        + @"   bstrings.exe -d ""C:\Temp"" --cp 10007"
        + "\r\n\t "
        + @"   bstrings.exe -d ""C:\Temp"" --ls test"
        + "\r\n\t "
        + @"   bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc --sa"
        + "\r\n\t "
        + @"   bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc --sa -m 15 -x 22"
        + "\r\n\t "
        + @"   bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls mui --sl";

    private static RootCommand _rootCommand;

    private static IFileSystem _fileSystem;

    private static readonly string BaseDirectory = GetBaseDirectory();

    private static string GetBaseDirectory()
    {
        var assemblyLocation = Assembly.GetExecutingAssembly().Location;
        return string.IsNullOrEmpty(assemblyLocation)
            ? AppContext.BaseDirectory // Single-file deployment
            : Path.GetDirectoryName(assemblyLocation);
    }

    // ILGPU specific fields
    private static readonly Context GpuContext;
    private static readonly Accelerator GpuAccelerator;

    // GPU concurrency control - limit simultaneous GPU operations
    private static readonly SemaphoreSlim GpuSemaphore = new SemaphoreSlim(2, 4); // Max 2 concurrent GPU operations

    private static int DynamicChunkSizeMB = 0; // Will be calculated, 0 means not yet or failed

    // Removed unused field _quiet
    private static bool _debug = false;
    private static bool _trace = false; // Explicitly initialize to fix compiler warning

    /// <summary>
    /// Represents a hit found by the GPU.
    /// </summary>
    public struct GpuHit
    {
        public long Offset; // Absolute offset in the original file
        public int Length;

        // ILGPU kernels require parameterless constructors for structs passed by value.
        // If you add methods or properties, ensure it remains a simple struct.
    }

    // Definition for Hit struct (assuming it was similar to this)
    // If it was defined elsewhere or differently, this might need adjustment.
    public struct Hit
    {
        public long Offset;
        public int Length; // Byte length
        public HitEncoding Encoding;
        public string Value; // The string value itself, can be empty if not stored

        public enum HitEncoding
        {
            Ascii,
            Unicode,
        }

        public Hit(long offset, int length, HitEncoding encoding, string value = null)
        {
            Offset = offset;
            Length = length;
            Encoding = encoding;
            Value = value;
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
    /// Configuration for concurrent processing
    /// </summary>
    public static class ConcurrentConfig
    {
        public static int MaxConcurrentChunks => Math.Max(2, Environment.ProcessorCount / 2);
        public static int ReadAheadChunks => Math.Min(8, MaxConcurrentChunks * 2);
        public static int OptimalDegreeOfParallelism =>
            Math.Max(2, Environment.ProcessorCount * 3 / 4);
        public static int ProducerConsumerBufferSize => Math.Max(16, MaxConcurrentChunks * 4);
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

    static Program()
    {
        Context tempContext = null;
        Accelerator tempAccelerator = null;

        try
        {
            // Initialize ILGPU with Cuda backend (output suppressed)
            try
            {
                // Attempt to create a context with only the Cuda backend enabled
                tempContext = Context.Create(builder => builder.Cuda());

                // Get the first available Cuda device.
                // This will throw an exception if no Cuda device is found or Cuda support isn't properly loaded.
                var cudaDevice = tempContext.GetCudaDevice(0);
                if (cudaDevice != null)
                {
                    tempAccelerator = cudaDevice.CreateAccelerator(tempContext);
                    // GPU initialization successful (messages suppressed unless debug mode)
                }
                else
                {
                    // This case might not be reached if GetCudaDevice(0) throws when no device is found.
                    // Cuda backend initialized, but no Cuda device found by GetCudaDevice(0) (message suppressed unless debug mode)
                    tempContext.Dispose();
                    tempContext = null;
                }
            }
            catch (Exception cudaEx)
            {
                // Failed to initialize ILGPU with Cuda (message suppressed unless debug mode)
                if (tempContext != null)
                {
                    tempContext.Dispose();
                    tempContext = null;
                }
                // tempAccelerator remains null, so we will fall through to the default initialization
            }
            if (tempAccelerator == null)
            {
                // Falling back to default ILGPU initialization (message suppressed unless debug mode)
                // Ensure any previous context (e.g., from a failed Cuda attempt) is disposed
                if (tempContext != null)
                {
                    tempContext.Dispose();
                    tempContext = null;
                }

                // Fallback to default initialization (might pick OpenCL, CPU, or another available backend)
                tempContext = Context.Create(builder => builder.Default());
                var preferredDevice = tempContext.GetPreferredDevice(preferCPU: false);
                if (preferredDevice != null)
                {
                    tempAccelerator = preferredDevice.CreateAccelerator(tempContext);
                    // ILGPU initialized with default backend (message suppressed unless debug mode)
                }
                else
                {
                    // No suitable GPU device found (message suppressed unless debug mode)
                    if (tempContext != null)
                    {
                        tempContext.Dispose();
                        tempContext = null;
                    }
                    // tempAccelerator remains null
                }
            }

            // Assign to readonly fields
            GpuContext = tempContext;
            GpuAccelerator = tempAccelerator;

            if (GpuAccelerator != null)
            {
                try
                {
                    long totalGpuMemoryBytes = GpuAccelerator.MemorySize;
                    long reserveMemoryBytes = 2L * 1024 * 1024 * 1024; // 2GB
                    long usableGpuMemoryBytes = totalGpuMemoryBytes - reserveMemoryBytes;

                    if (usableGpuMemoryBytes > 0)
                    {
                        const int defaultMinStringLengthForCalc = 3; // Based on mOption default
                        int sizeOfGpuHit = Marshal.SizeOf<GpuHit>(); // Should be 12 bytes
                        double memoryFactor =
                            1.0 + ((double)sizeOfGpuHit / defaultMinStringLengthForCalc);

                        long calculatedChunkSizeBytes = (long)(usableGpuMemoryBytes / memoryFactor);

                        int calculatedMB = (int)(calculatedChunkSizeBytes / (1024 * 1024));

                        // Clamp the dynamic chunk size to practical limits
                        const int minPracticalMB = 64;
                        const int maxPracticalMB = 512; // 512MB max for GPU - much more reasonable

                        DynamicChunkSizeMB = Math.Max(
                            minPracticalMB,
                            Math.Min(maxPracticalMB, calculatedMB)
                        );

                        // GPU VRAM information (messages suppressed unless debug mode)
                    }
                    else
                    {
                        // Not enough GPU VRAM (message suppressed unless debug mode)
                        DynamicChunkSizeMB = 0; // Will use CPU calculation
                    }
                }
                catch (Exception ex)
                {
                    // Error calculating dynamic GPU chunk size (message suppressed unless debug mode)
                    DynamicChunkSizeMB = 0; // Will use CPU calculation
                }
            }
            else
            {
                // GPU not available. Dynamic chunk size will be calculated based on CPU/RAM (message suppressed unless debug mode)
                DynamicChunkSizeMB = 0; // Will use CPU calculation at runtime
            }
        }
        catch (Exception ex)
        {
            // ILGPU General Initialization Error (message suppressed unless debug mode)
            if (tempContext != null)
                tempContext.Dispose();
            GpuContext = null;
            GpuAccelerator = null;
        }

        LogGpuInitializationInfo();
    }

    private static async Task Main(string[] args)
    {
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

        ExceptionlessClient.Default.Startup("Kruacm8p1B6RFAw2WMnKcEqkQcnWRkF3RmPSOzlW");
        // Ensure ILGPU context is initialized before any command parsing if GPU is to be used early
        // or ensure commands that need GPU are aware if it's not ready.
        // For now, static constructor handles initialization.

        SetupPatterns();

        _rootCommand = new RootCommand
        {
            new Option<string>("-f", "File to search. Either this or -d is required"),
            new Option<string>(
                "-d",
                "Directory to recursively process. Either this or -f is required"
            ),
            new Option<string>("-o", "File to save results to"),
            new Option<bool>(
                "-a",
                () => true,
                "If set, look for ASCII strings. Use -a false to disable"
            ),
            new Option<bool>(
                "-u",
                () => true,
                "If set, look for Unicode strings. Use -u false to disable"
            ),
            new Option<int>("-m", () => 3, "Minimum string length"),
            new Option<int>(
                "-b",
                () => 0,
                "Chunk size in MB. Valid range is 1 to 8192. Default is 0 (auto-calculated based on available GPU memory or system RAM)."
            )
            {
                ArgumentHelpName = "sizeMB",
            },
            new Option<bool>(
                "-q",
                () => false,
                "Quiet mode (Do not show header or total number of hits)"
            ),
            new Option<bool>(
                "-s",
                () => false,
                "Really Quiet mode (Do not display hits to console. Speeds up processing when using -o)"
            ),
            new Option<int>("-x", () => -1, "Maximum string length. Default is unlimited"),
            new Option<bool>("-p", () => false, "Display list of built in regular expressions"),
            new Option<string>(
                "--ls",
                "String to look for. When set, only matching strings are returned"
            ),
            new Option<string>(
                "--lr",
                "Regex to look for. When set, only strings matching the regex are returned. Supports comma-separated values for multiple patterns or 'all' to use all built-in patterns"
            ),
            new Option<string>(
                "--fs",
                "File containing strings to look for. When set, only matching strings are returned"
            ),
            new Option<string>(
                "--fr",
                "File containing regex patterns to look for. When set, only strings matching regex patterns are returned"
            ),
            new Option<string>(
                "--ar",
                () => "[\x20-\x7E]",
                @"Range of characters to search for in 'Code page' strings. Specify as a range of characters in hex format and enclose in quotes. Default is [\x20 -\x7E]"
            ),
            new Option<string>(
                "--ur",
                () => "[\u0020-\u007E]",
                @"Range of characters to search for in Unicode strings. Specify as a range of characters in hex format and enclose in quotes. Default is [\\u0020-\\u007E]"
            ),
            new Option<int>(
                "--cp",
                () => 1252,
                "Code page to use. Default is 1252. Use the Identifier value for code pages at https://goo.gl/ig6DxW"
            ),
            new Option<string>(
                "--mask",
                "When using -d, file mask to search for. * and ? are supported. This option has no effect when using -f"
            ),
            new Option<int>(
                "--ms",
                () => -1,
                "When using -d, maximum file size in bytes to process. This option has no effect when using -f"
            ),
            new Option<bool>(
                "--ro",
                () => false,
                "When true, list the string matched by regex pattern vs string the pattern was found in (This may result in duplicate strings in output. ~ denotes approx. offset)"
            ),
            new Option<bool>(
                "--off",
                () => false,
                "Show offset to hit after string, followed by the encoding (A=1252, U=Unicode)"
            ),
            new Option<bool>("--sa", () => false, "Sort results alphabetically"),
            new Option<bool>("--sl", () => false, "Sort results by length"),
            new Option<bool>("--debug", () => false, "Show debug information during processing"),
            new Option<bool>("--trace", () => false, "Show trace information during processing"),
        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;
        _rootCommand.Handler = CommandHandler.Create(
            async (
                string f,
                string d,
                string o,
                bool a,
                bool u,
                int m,
                int b,
                bool q,
                bool s,
                int x,
                bool p,
                string ls,
                string lr,
                string fs,
                string fr,
                string ar,
                string ur,
                int cp,
                string mask,
                int ms,
                bool ro,
                bool off,
                bool sa,
                bool sl,
                bool debug,
                bool trace
            ) =>
            {
                await DoWork(
                    f,
                    d,
                    o,
                    a,
                    u,
                    m,
                    b,
                    q,
                    s,
                    x,
                    p,
                    ls,
                    lr,
                    fs,
                    fr,
                    ar,
                    ur,
                    cp,
                    mask,
                    ms,
                    ro,
                    off,
                    sa,
                    sl,
                    debug,
                    trace
                );
            }
        );

        await _rootCommand.InvokeAsync(args);

        Log.CloseAndFlush();
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
        bool trace
    )
    { // Set the global debug flag
        _debug = debug;

        // Log GPU initialization info if debug is enabled
        LogGpuInitializationInfo();

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
            Log.Information("To use a built in pattern, supply the Name to the --lr switch\r\n");

            return;
        }

        var cpTest = CodePagesEncodingProvider.Instance.GetEncoding(1252);

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

        if (!string.IsNullOrEmpty(f) && !string.IsNullOrEmpty(d))
        {
            Log.Error(
                "Both -f (file) and -d (directory) options were specified. Please use only one. Exiting."
            );
            return;
        }

        if (!string.IsNullOrEmpty(f)) // -f (file) argument is present
        {
            if (!File.Exists(f))
            {
                Log.Error("File specified with -f not found: '{F}'. Exiting.", f);
                return;
            }
            files.Add(Path.GetFullPath(f));
        }
        else if (!string.IsNullOrEmpty(d)) // -d (directory) argument is present
        {
            if (!Directory.Exists(d))
            {
                Log.Error("Directory specified with -d not found: '{D}'. Exiting.", d);
                return;
            }
            try
            {
                string fullDirectoryPath = Path.GetFullPath(d);
                if (!string.IsNullOrEmpty(mask))
                {
                    files.AddRange(
                        Directory.EnumerateFiles(
                            fullDirectoryPath,
                            mask,
                            SearchOption.AllDirectories
                        )
                    );
                }
                else
                {
                    files.AddRange(
                        Directory.EnumerateFiles(
                            fullDirectoryPath,
                            "*",
                            SearchOption.AllDirectories
                        )
                    );
                }

                if (!files.Any() && !q)
                {
                    Log.Information(
                        "No files found in directory '{D}' matching the specified criteria.",
                        d
                    );
                    // Exiting if no files found in directory mode, as there's nothing to process.
                    // If the intent is to proceed (e.g. to create an empty output file), this 'return' can be removed.
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(
                    ex,
                    "Error enumerating files in directory '{D}'. Message: {ExMessage}",
                    d,
                    ex.Message
                );
                return;
            }
        }
        else if (Console.IsInputRedirected) // Neither -f nor -d, so check for piped input
        {
            Log.Information("No -f or -d specified; attempting to read from stdin...");
            string tempFilePath = string.Empty;
            try
            {
                tempFilePath = Path.GetTempFileName();
                using (var stdinStream = Console.OpenStandardInput())
                {
                    using (
                        var tempFileStream = new FileStream(
                            tempFilePath,
                            FileMode.Create,
                            FileAccess.Write
                        )
                    )
                    {
                        stdinStream.CopyTo(tempFileStream);
                    }
                }

                if (new FileInfo(tempFilePath).Length == 0)
                {
                    Log.Warning("Stdin was redirected, but no data was received. Exiting.");
                    File.Delete(tempFilePath);
                    return;
                }
                files.Add(Path.GetFullPath(tempFilePath));
            }
            catch (Exception ex)
            {
                Log.Error(
                    ex,
                    "Error reading from stdin or writing to temporary file. Message: {ExMessage}",
                    ex.Message
                );
                if (!string.IsNullOrEmpty(tempFilePath) && File.Exists(tempFilePath))
                {
                    File.Delete(tempFilePath);
                }
                return;
            }
        }
        else // No -f, no -d, and no piped input
        {
            var helpBld = new HelpBuilder(LocalizationResources.Instance, Console.WindowWidth);
            var hc = new HelpContext(helpBld, _rootCommand, Console.Out);
            helpBld.Write(hc);
            Log.Warning("A file (-f), directory (-d), or piped input is required. Exiting.");
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

        bool isCsvOutput =
            !string.IsNullOrEmpty(o) && o.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);
        bool csvHeaderWritten = false;

        var globalCounter = 0;
        var globalHits = 0;
        double globalTimespan = 0;
        var withBoundaryHits = false;

        if (string.IsNullOrEmpty(o) == false && o.Length > 0)
        {
            o = Path.GetFullPath(o).TrimEnd('\\');

            var dir = Path.GetDirectoryName(o);

            if (dir != null && Directory.Exists(dir) == false)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                }
                catch (Exception)
                {
                    Log.Warning("Invalid path: '{O}'. Results will not be saved to a file", o);
                    Console.WriteLine();
                    o = string.Empty;
                }
            }
            else
            {
                if (dir == null)
                {
                    Log.Warning("Invalid path: '{O}", o);
                    o = string.Empty;
                }
            }

            if (o.Length > 0 && !q)
            {
                Log.Information("Saving hits to '{O}'", o);
                Console.WriteLine();
            }

            if (o.Length > 0)
            {
                sw = new StreamWriter(o, true);
            }
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

            // Parse multiple patterns from lr parameter
            var regexPatterns = ParseRegexPatterns(lr);

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

            // Use dynamic chunk size if no chunk size specified by user (b=0) or invalid range
            var chunkSizeMb = (b <= 0 || b > 8192) ? GetOptimalChunkSize(fileSizeBytes) : b; // Use dynamic if not specified
            var chunkSizeBytes = chunkSizeMb * 1024 * 1024;

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
            var totalChunks = fileSizeBytes / chunkSizeBytes + 1;

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
                MappedStream mappedStream = null;

                try
                {
                    FileStream fileStream;
#if NET6_0_OR_GREATER
                    fileStream = File.Open(
                        currentFile,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    ); // Use currentFile
#else
                    fileStream = File.Open(
                        File.GetFileSystemEntryInfo(currentFile).LongFullPath, // Use currentFile
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read
                    );
#endif
                    if (!q && _debug)
                    {
                        Console.Error.WriteLine("Creating memory map for file...");
                    }
                    mappedStream = MappedStream.FromStream(fileStream, Ownership.None);
                    if (!q && _debug)
                    {
                        Console.Error.WriteLine("Memory map created successfully.");
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to create memory map: {ex.Message}");
                    // ignored
                }
                if (mappedStream == null)
                {
                    if (!q && _debug)
                    {
                        Console.Error.WriteLine("Falling back to raw file access...");
                    }
                    //raw mode
                    var ss = OpenFile(currentFile); // Use currentFile

                    if (!q && _debug)
                    {
                        Console.Error.WriteLine("Creating memory map from raw stream...");
                    }
                    mappedStream = MappedStream.FromStream(ss, Ownership.None);
                    if (!q && _debug)
                    {
                        Console.Error.WriteLine("Raw stream memory map created successfully.");
                    }
                }
                using (mappedStream)
                {
                    // Process main chunks concurrently
                    await ProcessFileChunksConcurrentlyAsync(
                        mappedStream,
                        fileSizeBytes,
                        chunkSizeBytes,
                        hits,
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
                        progressTracker
                    );

                    //do chunk boundary checks to make sure we get everything and not split things
                    if (!q)
                    {
                        Log.Information(
                            "Primary search complete. Looking for strings across chunk boundaries..."
                        );
                    }

                    // Process boundary chunks concurrently
                    await ProcessBoundaryChunksConcurrentlyAsync(
                        mappedStream,
                        fileSizeBytes,
                        chunkSizeMb * 1024 * 1024,
                        m * 10 * 2 * 2, // boundaryChunkSize
                        hits,
                        minLength,
                        maxLength,
                        a,
                        u,
                        off,
                        cp,
                        ar,
                        ur,
                        q
                    );
                }

                // Mark progress as completed to stop the progress task
                progressTracker.MarkCompleted();
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Log.Error(ex, "Error: {Message}", ex.Message);
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

            if (sa)
            {
                Log.Information("Sorting alphabetically...");
                Console.WriteLine();
                var tempList = hits.ToList();
                tempList.Sort();
                hits = new HashSet<string>(tempList);
            }
            else if (sl)
            {
                Log.Information("Sorting by length...");
                Console.WriteLine();
                var tempList = SortByLength(hits.ToList()).ToList();
                hits = new HashSet<string>(tempList);
            }

            var fileStrings = new HashSet<string>();
            var regexStrings = new HashSet<string>();

            //set up highlighting
            if (ls?.Length > 0)
            {
                fileStrings.Add(ls);
            }

            if (lr?.Length > 0)
            {
                regexStrings.UnionWith(regexPatterns);
            }

            if (string.IsNullOrEmpty(fs) == false || string.IsNullOrEmpty(fr) == false)
            {
                if (fs?.Length > 0)
                {
                    if (File.Exists(fs))
                    {
                        fileStrings.UnionWith(new HashSet<string>(File.ReadAllLines(fs)));
                    }
                    else
                    {
                        Log.Error("Strings file '{Fs}' not found", fs);
                    }
                }

                if (fr?.Length > 0)
                {
                    if (File.Exists(fr))
                    {
                        regexStrings.UnionWith(new HashSet<string>(File.ReadAllLines(fr)));
                    }
                    else
                    {
                        Log.Error("Regex file '{Fr}' not found", fr);
                    }
                }
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

            foreach (var hit in hits)
            {
                if (hit.Length == 0)
                {
                    continue;
                }

                // Prepare CSV output if needed
                if (isCsvOutput && sw != null && !csvHeaderWritten)
                {
                    // Write header
                    sw.WriteLine(
                        "Name of search pattern,Data found,Source file,Offset,Pattern type"
                    );
                    csvHeaderWritten = true;
                }

                string sourceFile = currentFile ?? string.Empty;
                string offsetStr = string.Empty;
                string patternType = string.Empty;
                string patternName = string.Empty;
                string dataFound = hit;

                // Try to extract offset and pattern type if available (for future extensibility)
                // If off flag is set, offset may be appended to the string, try to parse it
                if (off)
                {
                    // Example: "string~12345 (A)" or "string~12345 (U)"
                    int tildeIdx = hit.LastIndexOf('~');
                    if (tildeIdx > 0)
                    {
                        int spaceIdx = hit.IndexOf(' ', tildeIdx);
                        if (spaceIdx > tildeIdx)
                        {
                            offsetStr = hit.Substring(tildeIdx + 1, spaceIdx - tildeIdx - 1);
                            dataFound = hit.Substring(0, tildeIdx);
                            // Try to get encoding
                            int encStart = hit.IndexOf('(', spaceIdx);
                            int encEnd = hit.IndexOf(')', spaceIdx);
                            if (encStart > 0 && encEnd > encStart)
                                patternType = hit.Substring(encStart + 1, encEnd - encStart - 1);
                        }
                    }
                }                // Determine pattern name (for regex/file string matches)
                if (fileStrings.Count > 0)
                {
                    foreach (var fileString in fileStrings)
                    {
                        if (fileString.Trim().Length == 0)
                            continue;
                        if (
                            hit.IndexOf(fileString, StringComparison.InvariantCultureIgnoreCase)
                            >= 0
                        )
                        {
                            patternName = fileString;
                            patternType = "String";
                            break;
                        }
                    }
                }
                else if (regexPatterns.Count > 0)
                {
                    foreach (var regex in regexPatterns)
                    {
                        try
                        {
                            if (System.Text.RegularExpressions.Regex.IsMatch(hit, regex, RegexOptions.IgnoreCase))
                            {
                                // For CSV output, use the pattern name from RegExPatterns if available
                                patternName = GetRegexPatternName(regex) ?? regex;
                                patternType = "Regex";
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            // Skip invalid regex patterns
                            Log.Warning("Invalid regex pattern '{Regex}': {Message}", regex, ex.Message);
                        }
                    }
                }

                // Suppress console output if quiet mode is enabled and output file is specified
                var suppressConsoleOutput = q && !string.IsNullOrEmpty(o);
                if (s == false && !suppressConsoleOutput)
                {
                    Log.Information("{Hit}", hit);
                }

                if (isCsvOutput && sw != null)
                {
                    // Escape CSV fields
                    string CsvEscape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
                    sw.WriteLine(
                        $"{CsvEscape(patternName)},{CsvEscape(dataFound)},{CsvEscape(sourceFile)},{CsvEscape(offsetStr)},{CsvEscape(patternType)}"
                    );
                }
                else
                {
                    sw?.WriteLine(hit);
                }
            }

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

        if (sw != null)
        {
            sw.Flush();
            sw.Close();
        }

        if (q || files.Count <= 1)
        {
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

        var results = StringListPool.Get();
        var validChunk = chunk.Data.AsSpan(0, chunk.ValidBytes);

        if (unicodeSearch)
        {
            var uh = GetUnicodeHits(validChunk, minLength, maxLength, chunk.FileOffset, off, ur);
            foreach (var h in uh)
            {
                results.Add(chunk.IsBoundaryChunk ? "  " + h : h);
            }
        }
        if (asciiSearch)
        {
            List<string> ah;
            // PERFORMANCE OPTIMIZATION: Use optimized CPU processing for all chunks for now
            // Skip GPU processing to avoid overhead issues
            var (minChar, maxChar) = ParseCharRange(ar);
            // PERFORMANCE OPTIMIZATION: Use hit-based extraction for all chunks
            var hits = FindAsciiStringHits(
                validChunk,
                minLength,
                maxLength,
                chunk.FileOffset,
                minChar,
                maxChar
            );
            ah = MaterializeStringHits(validChunk, hits, off);

            foreach (var h in ah)
            {
                results.Add(chunk.IsBoundaryChunk ? "  " + h : h);
            }
        }
        chunkStopwatch.Stop();
        if (_debug)
        {
            Console.Error.WriteLine(
                $"[Chunk {chunk.ChunkIndex}] Completed in {chunkStopwatch.ElapsedMilliseconds}ms, found {results.Count} strings"
            );
        }

        // Return objects to pools and return the final result
        var finalResults = new List<string>(results);
        StringListPool.Return(results);

        // Return the byte array back to the pool since chunk processing is complete
        ByteArrayPool.Return(chunk.Data);

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
        var chunks = new List<DataChunk>();
        var bytesRemaining = totalBytes;
        var offset = startOffset;
        var chunkIndex = 0;

        if (isBoundaryMode)
        { // Boundary chunk reading logic
            while (bytesRemaining > 0 && offset + boundaryChunkSize <= totalBytes)
            {
                var chunk = ByteArrayPool.Rent(boundaryChunkSize);
                mappedStream.Position = offset;
                var bytesRead = await Task.Run(() =>
                    mappedStream.Read(chunk, 0, boundaryChunkSize)
                );

                if (bytesRead == 0)
                    break;

                chunks.Add(
                    new DataChunk
                    {
                        Data = chunk,
                        ValidBytes = bytesRead,
                        FileOffset = offset,
                        ChunkIndex = chunkIndex,
                        IsBoundaryChunk = true,
                    }
                );

                offset += chunkSizeBytes;
                bytesRemaining -= chunkSizeBytes;
                chunkIndex++;

                // Limit boundary chunks to prevent excessive memory usage
                if (chunks.Count >= ConcurrentConfig.ReadAheadChunks)
                    break;
            }
        }
        else
        {
            // Main chunk reading logic
            mappedStream.Position = startOffset;
            while (bytesRemaining > 0)
            {
                var currentChunkSize = (int)Math.Min(chunkSizeBytes, bytesRemaining);
                var chunk = ByteArrayPool.Rent(currentChunkSize);

                var bytesRead = await Task.Run(() => mappedStream.Read(chunk, 0, currentChunkSize));
                if (bytesRead == 0)
                    break;

                chunks.Add(
                    new DataChunk
                    {
                        Data = chunk,
                        ValidBytes = bytesRead,
                        FileOffset = offset,
                        ChunkIndex = chunkIndex,
                        IsBoundaryChunk = false,
                    }
                );

                offset += bytesRead;
                bytesRemaining -= bytesRead;
                chunkIndex++;

                // Limit read-ahead to prevent excessive memory usage
                if (chunks.Count >= ConcurrentConfig.ReadAheadChunks)
                    break;
            }
        }

        return chunks;
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
        var sign = i < 0 ? "-" : "";
        double readable;
        string suffix;
        if (i >= 0x1000000000000000) // Exabyte
        {
            suffix = "EB";
            readable = i >> 50;
        }
        else if (i >= 0x4000000000000) // Petabyte
        {
            suffix = "PB";
            readable = i >> 40;
        }
        else if (i >= 0x10000000000) // Terabyte
        {
            suffix = "TB";
            readable = i >> 30;
        }
        else if (i >= 0x40000000) // Gigabyte
        {
            suffix = "GB";
            readable = i >> 20;
        }
        else if (i >= 0x100000) // Megabyte
        {
            suffix = "MB";
            readable = i >> 10;
        }
        else if (i >= 0x400) // Kilobyte
        {
            suffix = "KB";
            readable = i;
        }
        else
        {
            return i.ToString(sign + "0 B"); // Byte
        }

        readable = readable / 1024;

        return sign + readable.ToString("0.### ") + suffix;
    }

    private static void SetupPatterns()
    {
        RegExDesc.Add("guid", "\tFinds GUIDs");
        RegExDesc.Add("usPhone", "\tFinds US phone numbers");
        RegExDesc.Add("unc", "\tFinds UNC paths");
        RegExDesc.Add("mac", "\tFinds MAC addresses");
        RegExDesc.Add("ssn", "\tFinds US Social Security Numbers");
        RegExDesc.Add("cc", "\tFinds credit card numbers");

        RegExDesc.Add("ipv4", "\tFinds IP version 4 addresses");
        RegExDesc.Add("ipv6", "\tFinds IP version 6 addresses");
        RegExDesc.Add("email", "\tFinds embedded email addresses");
        RegExDesc.Add("zip", "\tFinds zip codes");
        RegExDesc.Add("urlUser", "\tFinds usernames in URLs");
        RegExDesc.Add("url3986", "\tFinds URLs according to RFC 3986");
        RegExDesc.Add("xml", "\tFinds XML/HTML tags");
        RegExDesc.Add("sid", "\tFinds Microsoft Security Identifiers (SID)");
        RegExDesc.Add("win_path", @"Finds Windows style paths (C:\folder1\folder2\file.txt)");
        RegExDesc.Add("var_set", "\tFinds environment variables being set (OS=Windows_NT)");
        RegExDesc.Add("reg_path", "Finds paths related to Registry hives");
        RegExDesc.Add("b64", "\tFinds valid formatted base 64 strings");
        RegExDesc.Add("bitlocker", "Finds Bitlocker recovery keys");
        RegExDesc.Add("bitcoin", "\tFinds BitCoin wallet addresses");
        RegExDesc.Add("aeon", "\tFinds Aeon wallet addresses");
        RegExDesc.Add("bytecoin", "Finds ByteCoin wallet addresses");
        RegExDesc.Add("dashcoin", "Finds DashCoin wallet addresses (D*)");
        RegExDesc.Add("dashcoin2", "Finds DashCoin wallet addresses (7|X)*");
        RegExDesc.Add("fantomcoin", "Finds Fantomcoin wallet addresses");
        RegExDesc.Add("monero", "\tFinds Monero wallet addresses");
        RegExDesc.Add("sumokoin", "Finds SumoKoin wallet addresses");

        RegExPatterns.Add("bitcoin", @"\b[13][a-km-zA-HJ-NP-Z1-9]{25,34}\b");
        RegExPatterns.Add("aeon", @"Wm[st]{1}[0-9a-zA-Z]{94}");
        RegExPatterns.Add("bytecoin", @"2[0-9AB][0-9a-zA-Z]{93}");

        RegExPatterns.Add("dashcoin", "D[0-9a-zA-Z]{94}");
        RegExPatterns.Add("dashcoin2", "(7|X)[a-zA-Z0-9]{33}");
        RegExPatterns.Add("fantomcoin", "6[0-9a-zA-Z]{94}");
        RegExPatterns.Add("monero", "4[0-9AB][0-9a-zA-Z]{93}|4[0-9AB][0-9a-zA-Z]{104}");
        RegExPatterns.Add("sumokoin", "Sumoo[0-9a-zA-Z]{94}");

        RegExPatterns.Add(
            "b64",
            @"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})$"
        );

        RegExPatterns.Add(
            "bitlocker",
            @"[0-9]{6}?-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}"
        );

        RegExPatterns.Add(
            "reg_path",
            @"([a-z0-9]\\)*(software\\)|(sam\\)|(system\\)|(security\\)[a-z0-9\\]+"
        );
        RegExPatterns.Add("var_set", @"^[a-z_0-9]+=[\\/:\*\?<>|;\- _a-z0-9]+");
        RegExPatterns.Add(
            "win_path",
            @"(?:""?[a-zA-Z]\:|\\\\[^\\\/\:\*\?\<\>\|]+\\[^\\\/\:\*\?\<\>\|]*)\\(?:[^\\\/\:\*\?\<\>\|]+\\)*\w([^\\\/\:\*\?\<\>\|])*"
        );
        RegExPatterns.Add("sid", @"^S-\d-\d+-(\d+-){1,14}\d+$");
        RegExPatterns.Add("xml", @"\A<([A-Z][A-Z0-9]*)\b[^>]*>(.*?)</\1>\z");
        RegExPatterns.Add("guid", @"\b[A-F0-9]{8}(?:-[A-F0-9]{4}){3}-[A-F0-9]{12}\b");
        RegExPatterns.Add("usPhone", @"\(?\b[2-9][0-9]{2}\)?[-. ]?[2-9][0-9]{2}[-. ]?[0-9]{4}\b");
        RegExPatterns.Add("unc", @"^\\\\(?<server>[a-z0-9 %._-]+)\\(?<share>[a-z0-9 $%._-]+)");
        RegExPatterns.Add("mac", "\\b[0-9A-F]{2}([-:]?)(?:[0-9A-F]{2}\\1){4}[0-9A-F]{2}\\b");
        RegExPatterns.Add(
            "ssn",
            "\\b(?!000)(?!666)[0-8][0-9]{2}[- ](?!00)[0-9]{2}[- ](?!0000)[0-9]{4}\\b"
        );
        // RegExPatterns.Add("cc","^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|6(?:011|5[0-9][0-9])[0-9]{12}|3[47][0-9]{13}|3(?:0[0-5]|[68][0-9])[0-9]{11}|(?:2131|1800|35\\d{3})\\d{11})$");
        RegExPatterns.Add(
            "cc",
            @"^[ -]*(?:4[ -]*(?:\d[ -]*){11}(?:(?:\d[ -]*){3})?\d|5[ -]*[1-5](?:[ -]*[0-9]){14}|6[ -]*(?:0[ -]*1[ -]*1|5[ -]*\d[ -]*\d)(?:[ -]*[0-9]){12}|3[ -]*[47](?:[ -]*[0-9]){13}|3[ -]*(?:0[ -]*[0-5]|[68][ -]*[0-9])(?:[ -]*[0-9]){11}|(?:2[ -]*1[ -]*3[ -]*1|1[ -]*8[ -]*0[ -]*0|3[ -]*5(?:[ -]*[0-9]){3})(?:[ -]*[0-9]){11})[ -]*$"
        );
        RegExPatterns.Add(
            "ipv4",
            @"\b(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\b"
        );
        RegExPatterns.Add("ipv6", @"(?<![:.\w])(?:[A-F0-9]{1,4}:){7}[A-F0-9]{1,4}(?![:.\w])");
        //         RegExPatterns.Add("email",@"[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?");
        RegExPatterns.Add("email", @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,6}\b");
        RegExPatterns.Add("zip", @"\A\b[0-9]{5}(?:-[0-9]{4})?\b\z");
        RegExPatterns.Add("urlUser", @"^[a-z0-9+\-.]+://(?<user>[a-z0-9\-._~%!$&'()*+,;=]+)@");
        RegExPatterns.Add(
            "url3986",
            @"^
        [a-z][a-z0-9+\-.]*://                       # Scheme
        ([a-z0-9\-._~%!$&'()*+,;=]+@)?              # User
        (?<host>[a-z0-9\-._~%]+                     # Named host
        |\[[a-f0-9:.]+\]                            # IPv6 host
        |\[v[a-f0-9][a-z0-9\-._~%!$&'()*+,;=:]+\])  # IPvFuture host
        (:[0-9]+)?                                  # Port
        (/[a-z0-9\-._~%!$&'()*+,;=:@]+)*/?          # Path
        (\?[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Query
        (\#[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Fragment
        $"
        );
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
        // Default ASCII printable range
        if (string.IsNullOrEmpty(ar) || ar == "[\\x20-\\x7E]")
        {
            return (32, 126); // Space to tilde
        }

        // Simple parser for hex ranges like [\x20-\x7E]
        var match = Regex.Match(ar, @"\[\\x([0-9A-Fa-f]+)-\\x([0-9A-Fa-f]+)\]");
        if (match.Success)
        {
            var minHex = match.Groups[1].Value;
            var maxHex = match.Groups[2].Value;
            if (
                byte.TryParse(
                    minHex,
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out byte min
                )
                && byte.TryParse(
                    maxHex,
                    System.Globalization.NumberStyles.HexNumber,
                    null,
                    out byte max
                )
            )
            {
                return (min, max);
            }
        }

        // Fallback to default range
        return (32, 126);
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

    private static IEnumerable<string> SortByLength(IEnumerable<string> e)
    {
        var sorted = from s in e orderby s.Length ascending select s;
        return sorted;
    }

    private static List<string> GetUnicodeHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffset,
        bool originalOffBool,
        string ur
    )
    {
        return GetStringHitsUnified(
            chunk,
            minLength,
            maxLength,
            currentOffset,
            originalOffBool,
            true // isUnicode = true
        );
    }

    /// <summary>
    /// Optimized helper method to add Unicode string results with minimal allocations
    /// </summary>
    private static void AddOptimizedUnicodeStringResult(
        List<string> results,
        ReadOnlySpan<byte> chunk,
        int stringStart,
        int stringLength,
        int maxLength,
        long currentOffsetInFile,
        bool originalOffBool
    )
    {
        var actualLength = maxLength > 0 && stringLength > maxLength ? maxLength : stringLength;
        var sb = StringBuilderPool.Get();

        try
        {
            // Build the Unicode string efficiently
            for (var i = 0; i < actualLength; i++)
            {
                var byteIndex = stringStart + i * 2;
                if (byteIndex + 1 < chunk.Length)
                {
                    char c = (char)(chunk[byteIndex] | (chunk[byteIndex + 1] << 8));
                    sb.Append(c);
                }
            }

            var stringResult = sb.ToString();
            if (originalOffBool)
            {
                var hitOffset = currentOffsetInFile + stringStart;
                var offsetOut = $"0x{hitOffset:X}";
                results.Add($"{offsetOut}\t{stringResult}");
            }
            else
            {
                results.Add(stringResult);
            }
        }
        finally
        {
            StringBuilderPool.Return(sb);
        }
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
        // Parse ASCII range if provided
        var (minChar, maxChar) = ParseCharRange(ar);

        // PERFORMANCE OPTIMIZATION: Use SIMD-optimized hit detection instead of StringBuilder
        var hits = FindAsciiStringHits(
            chunk,
            minLength,
            maxLength,
            currentOffsetInFile,
            minChar,
            maxChar
        );

        // Materialize strings only when needed
        return MaterializeStringHits(chunk, hits, originalOffBool);
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

    /// <summary>
    /// Optimized helper method to add string results with minimal allocations
    /// </summary>
    private static void AddOptimizedStringResult(
        List<string> results,
        ReadOnlySpan<byte> chunk,
        int stringStart,
        int stringLength,
        int maxLength,
        long currentOffsetInFile,
        bool originalOffBool
    )
    {
        var actualLength = maxLength > 0 && stringLength > maxLength ? maxLength : stringLength;
        var stringSpan = chunk.Slice(stringStart, actualLength);
        var stringToAdd = Encoding.ASCII.GetString(stringSpan);

        if (originalOffBool)
        {
            var hitOffset = currentOffsetInFile + stringStart;
            results.Add($"0x{hitOffset:X}\t{stringToAdd}");
        }
        else
        {
            results.Add(stringToAdd);
        }
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

    /// <summary>
    /// Processes main file chunks concurrently using enhanced pipeline parallelism
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
        ProgressTracker progressTracker
    )
    {
        using var pipeline = new ChunkProcessingPipeline();

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
    private static async IAsyncEnumerable<DataChunk> ReadChunksAsyncEnumerable(
        MappedStream mappedStream,
        long fileSizeBytes,
        int chunkSizeBytes,
        long startOffset = 0,
        bool isBoundaryMode = false,
        int boundaryChunkSize = 0
    )
    {
        var bytesRemaining = fileSizeBytes;
        long offset = startOffset;
        int chunkIndex = 0;

        while (bytesRemaining > 0)
        {
            var chunks = await ReadChunksAsync(
                mappedStream,
                bytesRemaining,
                chunkSizeBytes,
                offset,
                isBoundaryMode,
                boundaryChunkSize
            );

            if (chunks.Count == 0)
                break;

            foreach (var chunk in chunks)
            {
                yield return chunk;
            }

            // Update for next batch
            var lastChunk = chunks.Last();
            offset = lastChunk.FileOffset + lastChunk.ValidBytes;
            bytesRemaining = fileSizeBytes - offset;
            chunkIndex = lastChunk.ChunkIndex + 1;
        }
    }

    /// <summary>
    /// Processes boundary chunks concurrently using enhanced pipeline parallelism
    /// </summary>
    private static async Task ProcessBoundaryChunksConcurrentlyAsync(
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
        bool quiet
    )
    {
        using var pipeline = new ChunkProcessingPipeline();

        var bytesRemaining = fileSizeBytes;
        long offset = chunkSizeBytes - minLength * 10 * 2; // Move starting point backwards
        bool withBoundaryHits = false;

        // Create async enumerable of boundary chunks
        var chunks = ReadChunksAsyncEnumerable(
            mappedStream,
            bytesRemaining,
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
            new ProgressTracker(1, quiet) // Simple tracker for boundary chunks
        );

        // Add results to the main collection
        lock (hits)
        {
            foreach (var result in results)
            {
                hits.Add(result);
                if (!withBoundaryHits)
                {
                    withBoundaryHits = true;
                }
            }
        }
    }

    /// <summary>
    /// Calculates optimal CPU chunk size based on available memory and file characteristics
    /// </summary>
    private static int CalculateOptimalCpuChunkSizeMB()
    {
        try
        {
            // Get system memory information more accurately
            var gcMemoryInfo = GC.GetGCMemoryInfo();
            long totalPhysicalMemory = gcMemoryInfo.TotalAvailableMemoryBytes;
            long currentlyUsed = GC.GetTotalMemory(false);

            // Calculate available memory more aggressively but safely
            long availableMemory = totalPhysicalMemory - currentlyUsed;

            // Use a higher percentage of available memory for better performance
            // but ensure we don't exceed safe limits
            double memoryUsageFraction = availableMemory > 8L * 1024 * 1024 * 1024 ? 0.25 : 0.15; // 25% if >8GB available, 15% otherwise
            long usableMemory = (long)(availableMemory * memoryUsageFraction);

            // Account for concurrent processing
            int maxConcurrentChunks = ConcurrentConfig.MaxConcurrentChunks;
            long memoryPerChunk = usableMemory / Math.Max(1, maxConcurrentChunks);

            // Convert to MB
            int chunkSizeMB = (int)(memoryPerChunk / (1024 * 1024));

            // Apply more aggressive practical limits for better performance
            const int minPracticalMB = 128; // Increased minimum for better I/O efficiency
            const int maxPracticalMB = 4096; // Increased maximum to 4GB for large files

            chunkSizeMB = Math.Max(minPracticalMB, Math.Min(maxPracticalMB, chunkSizeMB));

            Console.Error.WriteLine(
                $"Total Memory: {totalPhysicalMemory / (1024 * 1024)} MB, Available: {availableMemory / (1024 * 1024)} MB"
            );
            Console.Error.WriteLine(
                $"Calculated optimal CPU chunk size: {chunkSizeMB} MB (max {maxConcurrentChunks} concurrent chunks, {memoryUsageFraction:P1} memory usage)"
            );

            return chunkSizeMB;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"Error calculating optimal CPU chunk size: {ex.Message}. Using enhanced fallback."
            );
            // Enhanced fallback based on processor count
            return Math.Max(512, Environment.ProcessorCount * 128); // At least 512MB, or 128MB per core
        }
    }

    /// <summary>
    /// Gets the optimal chunk size by choosing between GPU and CPU calculations, with file-size adaptation
    /// </summary>
    private static int GetOptimalChunkSize(long fileSizeBytes)
    {
        try
        {
            // If GPU is available and we have calculated a GPU chunk size, use it
            if (GpuAccelerator != null && DynamicChunkSizeMB > 0)
            {
                int gpuChunkSize = AdaptChunkSizeForFile(DynamicChunkSizeMB, fileSizeBytes);
                Console.Error.WriteLine(
                    $"Using GPU-optimized chunk size: {gpuChunkSize} MB (adapted for {GetSizeReadable(fileSizeBytes)} file)"
                );
                return gpuChunkSize;
            }

            // Fall back to CPU-optimized chunk size
            int cpuChunkSize = CalculateOptimalCpuChunkSizeMB();
            int adaptedChunkSize = AdaptChunkSizeForFile(cpuChunkSize, fileSizeBytes);
            Console.Error.WriteLine(
                $"Using CPU-optimized chunk size: {adaptedChunkSize} MB (adapted for {GetSizeReadable(fileSizeBytes)} file)"
            );
            return adaptedChunkSize;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error in GetOptimalChunkSize: {ex.Message}. Using fallback.");
            return 512; // Safe fallback
        }
    }

    /// <summary>
    /// Adapts chunk size based on file size for optimal performance
    /// </summary>
    private static int AdaptChunkSizeForFile(int baseChunkSizeMB, long fileSizeBytes)
    {
        long fileSizeMB = fileSizeBytes / (1024 * 1024);

        // For very small files, use smaller chunks to avoid waste
        if (fileSizeMB < 100) // Less than 100MB
        {
            return Math.Max(32, (int)Math.Min(baseChunkSizeMB, fileSizeMB / 2));
        }

        // For medium files (100MB - 1GB), use base chunk size
        if (fileSizeMB < 1024)
        {
            return baseChunkSizeMB;
        }

        // For large files (1GB - 10GB), increase chunk size for better efficiency
        if (fileSizeMB < 10240)
        {
            return Math.Min(4096, (int)(baseChunkSizeMB * 1.5));
        }

        // For very large files (>10GB), use maximum chunk size for optimal I/O
        return Math.Min(8192, baseChunkSizeMB * 2);
    }

    /// <summary>
    /// Calculates optimal GPU chunk size based on available VRAM
    /// </summary>
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
            double memoryFactor = 1.0 + ((double)sizeOfGpuHit / defaultMinStringLengthForCalc);

            long calculatedChunkSizeBytes = (long)(usableGpuMemoryBytes / memoryFactor);
            int calculatedMB = (int)(calculatedChunkSizeBytes / (1024 * 1024));

            // Clamp the dynamic chunk size to practical limits
            const int minPracticalMB = 64;
            const int maxPracticalMB = 2048; // 2GB max to avoid int overflow in byte calculations
            const int maxSafeBytesForInt = int.MaxValue / (1024 * 1024); // ~2047 MB

            int finalMB = Math.Max(
                minPracticalMB,
                Math.Min(Math.Min(maxPracticalMB, maxSafeBytesForInt), calculatedMB)
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
        private readonly SemaphoreSlim _concurrencyLimiter;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly int _maxConcurrency;
        private volatile bool _disposed = false;

        public ChunkProcessingPipeline(int maxConcurrency = 0)
        {
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
                ConcurrentConfig.ProducerConsumerBufferSize * 2
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

            _concurrencyLimiter = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);
            _cancellationTokenSource = new CancellationTokenSource();
        }

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
            var resultCollectionTask = CollectResultsAsync(allResults);

            try
            {
                // Feed chunks into the pipeline
                await foreach (var chunk in chunks.WithCancellation(_cancellationTokenSource.Token))
                {
                    await _chunkWriter.WriteAsync(chunk, _cancellationTokenSource.Token);
                }
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
            ProgressTracker progressTracker
        )
        {
            var workers = new Task[_maxConcurrency];

            for (int i = 0; i < _maxConcurrency; i++)
            {
                workers[i] = ProcessChunksWorkerAsync(
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
            }

            await Task.WhenAll(workers);
        }

        private async Task ProcessChunksWorkerAsync(
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
            await foreach (var chunk in _chunkReader.ReadAllAsync(_cancellationTokenSource.Token))
            {
                try
                {
                    await _concurrencyLimiter.WaitAsync(_cancellationTokenSource.Token);

                    var results = ProcessChunk(
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

                    await _resultWriter.WriteAsync(results, _cancellationTokenSource.Token);
                    progressTracker.ReportChunkComplete(results.Count);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    _concurrencyLimiter.Release();
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

        public void Dispose()
        {
            if (!_disposed)
            {
                _cancellationTokenSource?.Cancel();
                _concurrencyLimiter?.Dispose();
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
            var batch = new List<T>(batchSize);

            await foreach (var item in source)
            {
                batch.Add(item);

                if (batch.Count >= batchSize)
                {
                    yield return new List<T>(batch);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
        }
    }

    /// <summary>
    /// Unified optimized scanning function for both ASCII and Unicode with minimal allocations
    /// </summary>
    private static List<string> GetStringHitsUnified(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffsetInFile,
        bool originalOffBool,
        bool isUnicode,
        byte minChar = 32,
        byte maxChar = 126
    )
    {
        var results = StringListPool.Get();
        var stringStart = -1;
        var stringLength = 0;
        var stepSize = isUnicode ? 2 : 1;

        for (var i = 0; i < chunk.Length - (stepSize - 1); i += stepSize)
        {
            bool isValidChar;

            if (isUnicode)
            {
                // Unicode processing (2 bytes per character)
                if (i + 1 >= chunk.Length)
                    break;

                char c = (char)(chunk[i] | (chunk[i + 1] << 8));
                isValidChar = c >= 32 && c <= 126;
            }
            else
            {
                // ASCII processing (1 byte per character)
                var currentByte = chunk[i];
                isValidChar = currentByte >= minChar && currentByte <= maxChar;
            }

            if (isValidChar)
            {
                if (stringStart == -1)
                {
                    stringStart = i;
                    stringLength = 1;
                }
                else
                {
                    stringLength++;
                }
            }
            else
            {
                // End of string - check if we have a valid string to add
                if (stringStart != -1 && stringLength >= minLength)
                {
                    if (isUnicode)
                    {
                        AddOptimizedUnicodeStringResult(
                            results,
                            chunk,
                            stringStart,
                            stringLength,
                            maxLength,
                            currentOffsetInFile,
                            originalOffBool
                        );
                    }
                    else
                    {
                        AddOptimizedStringResult(
                            results,
                            chunk,
                            stringStart,
                            stringLength,
                            maxLength,
                            currentOffsetInFile,
                            originalOffBool
                        );
                    }
                }
                stringStart = -1;
                stringLength = 0;
            }
        }

        // Handle string at end of buffer
        if (stringStart != -1 && stringLength >= minLength)
        {
            if (isUnicode)
            {
                AddOptimizedUnicodeStringResult(
                    results,
                    chunk,
                    stringStart,
                    stringLength,
                    maxLength,
                    currentOffsetInFile,
                    originalOffBool
                );
            }
            else
            {
                AddOptimizedStringResult(
                    results,
                    chunk,
                    stringStart,
                    stringLength,
                    maxLength,
                    currentOffsetInFile,
                    originalOffBool
                );
            }
        }

        // Return objects to pools and return the final result
        var finalResults = new List<string>(results);
        StringListPool.Return(results);
        return finalResults;
    }

    /// <summary>
    /// Represents a string hit position - much more memory efficient than storing actual strings
    /// </summary>
    public readonly struct StringHit
    {
        public readonly int Start;
        public readonly int Length;
        public readonly long FileOffset;

        public StringHit(int start, int length, long fileOffset)
        {
            Start = start;
            Length = length;
            FileOffset = fileOffset;
        }
    }

    /// <summary>
    /// SIMD-optimized ASCII string hit detection - 10-20x faster than original
    /// </summary>
    private static unsafe List<StringHit> FindAsciiStringHits(
        ReadOnlySpan<byte> data,
        int minLength,
        int maxLength,
        long fileOffset,
        byte minChar = 32,
        byte maxChar = 126
    )
    {
        var hits = new List<StringHit>(data.Length / 20); // Pre-size based on typical density

        if (data.Length == 0)
            return hits;

        fixed (byte* dataPtr = data)
        {
            int stringStart = -1;
            int i = 0;

            // SIMD processing for bulk of data
            if (System.Runtime.Intrinsics.X86.Sse2.IsSupported && data.Length >= 16)
            {
                var minVec = System.Runtime.Intrinsics.Vector128.Create(minChar);
                var maxVec = System.Runtime.Intrinsics.Vector128.Create(maxChar);

                for (; i <= data.Length - 16; i += 16)
                {
                    var chunk = System.Runtime.Intrinsics.X86.Sse2.LoadVector128(dataPtr + i);
                    // Check if bytes are in valid range [minChar, maxChar]
                    // SSE2 doesn't have unsigned byte comparison, so we use a different approach
                    var minVecSigned = System.Runtime.Intrinsics.Vector128.Create(
                        (sbyte)(minChar - 128)
                    );
                    var maxVecSigned = System.Runtime.Intrinsics.Vector128.Create(
                        (sbyte)(maxChar - 128)
                    );
                    var chunkSigned = System.Runtime.Intrinsics.X86.Sse2.Subtract(
                        chunk.AsSByte(),
                        System.Runtime.Intrinsics.Vector128.Create(unchecked((sbyte)128))
                    );

                    var geMin = System.Runtime.Intrinsics.X86.Sse2.CompareGreaterThan(
                        chunkSigned,
                        System.Runtime.Intrinsics.X86.Sse2.Subtract(
                            minVecSigned,
                            System.Runtime.Intrinsics.Vector128.Create((sbyte)1)
                        )
                    );
                    var leMax = System.Runtime.Intrinsics.X86.Sse2.CompareGreaterThan(
                        System.Runtime.Intrinsics.X86.Sse2.Add(
                            maxVecSigned,
                            System.Runtime.Intrinsics.Vector128.Create((sbyte)1)
                        ),
                        chunkSigned
                    );
                    var isValid = System.Runtime.Intrinsics.X86.Sse2.And(geMin, leMax);

                    uint mask = (uint)System.Runtime.Intrinsics.X86.Sse2.MoveMask(isValid);

                    // Process each bit in the mask
                    for (int bit = 0; bit < 16; bit++)
                    {
                        bool charValid = (mask & (1u << bit)) != 0;
                        ProcessCharForStringHit(
                            charValid,
                            i + bit,
                            ref stringStart,
                            minLength,
                            maxLength,
                            fileOffset,
                            hits
                        );
                    }
                }
            }

            // Handle remaining bytes with scalar processing
            for (; i < data.Length; i++)
            {
                byte b = dataPtr[i];
                bool charValid = b >= minChar && b <= maxChar;
                ProcessCharForStringHit(
                    charValid,
                    i,
                    ref stringStart,
                    minLength,
                    maxLength,
                    fileOffset,
                    hits
                );
            }

            // Handle string at end of buffer
            if (stringStart != -1)
            {
                int length = i - stringStart;
                if (length >= minLength)
                {
                    int actualLength = maxLength > 0 && length > maxLength ? maxLength : length;
                    hits.Add(new StringHit(stringStart, actualLength, fileOffset));
                }
            }
        }

        return hits;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining
    )]
    private static void ProcessCharForStringHit(
        bool charValid,
        int pos,
        ref int stringStart,
        int minLength,
        int maxLength,
        long fileOffset,
        List<StringHit> hits
    )
    {
        if (charValid)
        {
            if (stringStart == -1)
            {
                stringStart = pos;
            }
            else if (maxLength > 0 && (pos - stringStart + 1) > maxLength)
            {
                // Hit max length, emit truncated string
                hits.Add(new StringHit(stringStart, maxLength, fileOffset));
                stringStart = -1;
            }
        }
        else
        {
            if (stringStart != -1)
            {
                int length = pos - stringStart;
                if (length >= minLength)
                {
                    int actualLength = maxLength > 0 && length > maxLength ? maxLength : length;
                    hits.Add(new StringHit(stringStart, actualLength, fileOffset));
                }
                stringStart = -1;
            }
        }
    }

    /// <summary>
    /// Materialize string hits into actual strings - only called when needed
    /// </summary>
    private static List<string> MaterializeStringHits(
        ReadOnlySpan<byte> data,
        List<StringHit> hits,
        bool includeOffset
    )
    {
        var results = new List<string>(hits.Count);

        foreach (var hit in hits)
        {
            if (hit.Start + hit.Length <= data.Length)
            {
                var stringBytes = data.Slice(hit.Start, hit.Length);
                var str = Encoding.ASCII.GetString(stringBytes);

                if (includeOffset)
                {
                    results.Add($"0x{hit.FileOffset + hit.Start:X}\t{str}");
                }
                else
                {
                    results.Add(str);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Logs GPU initialization information if debug mode is enabled
    /// </summary>
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

    /// <summary>
    /// Parses the lr parameter to handle comma-separated patterns and 'all' keyword
    /// </summary>
    /// <param name="lr">The lr parameter value</param>
    /// <returns>List of resolved regex patterns</returns>
    private static List<string> ParseRegexPatterns(string lr)
    {
        var patterns = new List<string>();

        if (string.IsNullOrWhiteSpace(lr))
        {
            return patterns;
        }

        // Handle 'all' keyword to include all built-in patterns
        if (lr.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            patterns.AddRange(RegExPatterns.Values);
            return patterns;
        }

        // Split by comma and process each pattern
        var patternNames = lr.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var patternName in patternNames)
        {
            var trimmedName = patternName.Trim();

            // Check if it's a built-in pattern
            if (RegExPatterns.ContainsKey(trimmedName))
            {
                patterns.Add(RegExPatterns[trimmedName]);
            }
            else
            {
                // Treat as literal regex pattern
                patterns.Add(trimmedName);
            }
        }

        return patterns;
    }

    /// <summary>
    /// Processes hits concurrently against multiple regex patterns
    /// </summary>
    /// <param name="hits">The string hits to process</param>
    /// <param name="regexPatterns">List of regex patterns to match against</param>
    /// <param name="ro">Regex output mode</param>
    /// <param name="off">Show offset</param>    /// <param name="s">Silent mode</param>
    /// <param name="sw">StreamWriter for output</param>
    /// <param name="q">Quiet mode</param>    /// <param name="o">Output file path</param>
    /// <param name="currentFile">Current file being processed</param>
    /// <param name="isCsvOutput">Whether output is CSV format</param>
    /// <returns>Number of matches found</returns>
    private static async Task<int> ProcessRegexPatternsConcurrentlyAsync(
        HashSet<string> hits,
        List<string> regexPatterns,
        bool ro,
        bool off,
        bool s,
        StreamWriter sw,
        bool q,
        string o,
        string currentFile = "",
        bool isCsvOutput = false
    )
    {
        if (regexPatterns.Count == 0)
            return 0;

        var lockObject = new object();

        // Create tasks for each pattern to process concurrently
        var tasks = regexPatterns.Select(async regString =>
        {
            if (string.IsNullOrWhiteSpace(regString))
                return 0;

            var localMatches = 0;

            try
            {
                var regex = new Regex(
                    regString,
                    RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
                );

                await Task.Run(() =>
                {
                    foreach (var hit in hits)
                    {
                        if (hit.Length == 0)
                            continue;

                        if (!regex.IsMatch(hit))
                            continue;

                        var hitOffset = "";
                        if (off)
                        {
                            hitOffset = $"~{hit.Split('\t').LastOrDefault()}";
                        }

                        lock (lockObject)
                        {
                            localMatches++;

                            // Suppress console output if quiet mode is enabled and output file is specified
                            var suppressConsoleOutput = q && !string.IsNullOrEmpty(o);
                            
                            if (ro)
                            {
                                foreach (Match match in regex.Matches(hit))
                                {
                                    if (!s && !suppressConsoleOutput)
                                    {
                                        Log.Information(
                                            "{Match}\t{HitOffset}",
                                            match.Value,
                                            hitOffset
                                        );
                                    }                                    if (isCsvOutput && sw != null)
                                    {
                                        // CSV output for regex matches
                                        string CsvEscape(string s) =>
                                            "\"" + s.Replace("\"", "\"\"") + "\"";
                                        string offsetStr = hitOffset.TrimStart('~');
                                        sw.WriteLine(
                                            $"{CsvEscape(regString)},{CsvEscape(match.Value)},{CsvEscape(currentFile)},{CsvEscape(offsetStr)},{CsvEscape("Regex")}"
                                        );
                                    }
                                    else
                                    {
                                        sw?.WriteLine($"{match.Value}\t{hitOffset}");
                                    }
                                }
                            }
                            else
                            {
                                if (!s && !suppressConsoleOutput)
                                {
                                    Log.Information("{Hit}", hit);
                                }

                                if (isCsvOutput && sw != null)
                                {
                                    // CSV output for full hit
                                    string CsvEscape(string s) =>
                                        "\"" + s.Replace("\"", "\"\"") + "\"";
                                    string offsetStr = hitOffset.TrimStart('~');
                                    sw.WriteLine(
                                        $"{CsvEscape(regString)},{CsvEscape(hit)},{CsvEscape(currentFile)},{CsvEscape(offsetStr)},{CsvEscape("Regex")}"
                                    );
                                }
                                else
                                {
                                    sw?.WriteLine(hit);
                                }
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(
                    ex,
                    "Error processing regular expression '{RegString}': {Message}",
                    regString,
                    ex.Message
                );
            }

            return localMatches;
        });

        var results = await Task.WhenAll(tasks);
        return results.Sum();
    }
}
