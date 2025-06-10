using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices; // Keep one instance
using System.Security.AccessControl;
using System.Text;
using System.Text.RegularExpressions;
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

    private static readonly string BaseDirectory = Path.GetDirectoryName(
        Assembly.GetExecutingAssembly().Location
    );

    // ILGPU specific fields
    private static readonly Context GpuContext;
    private static readonly Accelerator GpuAccelerator;

    private static int DynamicChunkSizeMB = 0; // Will be calculated, 0 means not yet or failed    // Removed unused fields _quiet and _debug 
    // private static bool _quiet;
    // private static bool _debug;
    private static bool _trace; // Keep _trace as it might be used for tracing

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

    static Program()
    {
        Context tempContext = null;
        Accelerator tempAccelerator = null;

        try
        {
            Console.Error.WriteLine("Attempting to initialize ILGPU with Cuda backend...");
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
                    Console.Error.WriteLine(
                        $"ILGPU Initialized with Cuda. Using: {tempAccelerator.Name}"
                    );
                    Console.Error.WriteLine(
                        $"Type: {tempAccelerator.AcceleratorType}, Max Threads: {tempAccelerator.MaxNumThreads}"
                    );
                }
                else
                {
                    // This case might not be reached if GetCudaDevice(0) throws when no device is found.
                    Console.Error.WriteLine(
                        "Cuda backend initialized, but no Cuda device found by GetCudaDevice(0)."
                    );
                    tempContext.Dispose();
                    tempContext = null;
                }
            }
            catch (Exception cudaEx)
            {
                Console.Error.WriteLine($"Failed to initialize ILGPU with Cuda: {cudaEx.Message}");
                if (tempContext != null)
                {
                    tempContext.Dispose();
                    tempContext = null;
                }
                // tempAccelerator remains null, so we will fall through to the default initialization
            }

            if (tempAccelerator == null)
            {
                Console.Error.WriteLine("Falling back to default ILGPU initialization...");
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
                    Console.Error.WriteLine(
                        $"ILGPU Initialized with Default. Using: {tempAccelerator.Name}"
                    );
                    Console.Error.WriteLine(
                        $"Type: {tempAccelerator.AcceleratorType}, Max Threads: {tempAccelerator.MaxNumThreads}"
                    );
                }
                else
                {
                    Console.Error.WriteLine(
                        "ILGPU: No suitable GPU device found with default backend."
                    );
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
                        const int maxPracticalMB = 6144; // 6GB, adjustable

                        DynamicChunkSizeMB = Math.Max(
                            minPracticalMB,
                            Math.Min(maxPracticalMB, calculatedMB)
                        );

                        Console.Error.WriteLine(
                            $"Total GPU VRAM: {totalGpuMemoryBytes / (1024 * 1024)} MB. Usable (Total - 2GB): {usableGpuMemoryBytes / (1024 * 1024)} MB."
                        );
                        Console.Error.WriteLine(
                            $"Calculated dynamic chunk size for GPU: {DynamicChunkSizeMB} MB (using factor {memoryFactor:F2} for hits buffer)."
                        );
                    }
                    else
                    {
                        Console.Error.WriteLine(
                            "Not enough GPU VRAM to reserve 2GB and calculate dynamic chunk size. Using standard default."
                        );
                        DynamicChunkSizeMB = 512; // Fallback to a standard default
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(
                        $"Error calculating dynamic GPU chunk size: {ex.Message}. Using standard default."
                    );
                    DynamicChunkSizeMB = 512; // Fallback
                }
            }
            else
            {
                Console.Error.WriteLine(
                    "GPU not available. Dynamic chunk size calculation skipped. Using standard default for chunk size if not specified."
                );
                DynamicChunkSizeMB = 512; // Standard default if no GPU
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"ILGPU General Initialization Error: {ex.Message}. GPU acceleration will be disabled."
            );
            if (tempContext != null)
                tempContext.Dispose();
            GpuContext = null;
            GpuAccelerator = null;
        }
    }

    private static async Task Main(string[] args)
    {
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
                () => 512,
                "Chunk size in MB. Valid range is 1 to 8192. Default is 512 MB, or dynamically calculated for GPU."
            )
            {
                ArgumentHelpName = "sizeMB",
            },
            new Option<bool>(
                "-q",
                () => false,
                "Quiet mode (Do not show header or total number of hits)"
            ),
            new Option<int>("-x", () => -1, "Maximum string length. Default is unlimited"),
            new Option<bool>("-p", () => false, "Display list of built in regular expressions"),
            new Option<string>(
                "--ls",
                "String to look for. When set, only matching strings are returned"
            ),
            new Option<string>(
                "--lr",
                "Regex to look for. When set, only strings matching the regex are returned"
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

        _rootCommand.Handler = CommandHandler.Create(DoWork);

        await _rootCommand.InvokeAsync(args);

        Log.CloseAndFlush();
    }

    private static void DoWork(
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
    {
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

            var regPattern = lr;

            if (regPattern != null && RegExPatterns.ContainsKey(lr))
            {
                regPattern = RegExPatterns[lr];
            }

            if (regPattern?.Length > 0 && !q)
            {
                Log.Information("Searching via RegEx pattern: {RegPattern}", regPattern);
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

            var chunkSizeMb = b < 1 || b > 8192 ? 512 : b; // Increased upper limit
            var chunkSizeBytes = chunkSizeMb * 1024 * 1024;

            var fileSizeBytes = new FileInfo(currentFile).Length; // Use currentFile

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
            }

            var bytesRemaining = fileSizeBytes;
            long offset = 0;

            var chunkIndex = 1;
            var totalChunks = fileSizeBytes / chunkSizeBytes + 1;

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
                    mappedStream = MappedStream.FromStream(fileStream, Ownership.None);
                }
                catch (Exception)
                {
                    // ignored
                }

                if (mappedStream == null)
                {
                    //raw mode
                    var ss = OpenFile(currentFile); // Use currentFile

                    mappedStream = MappedStream.FromStream(ss, Ownership.None);
                }

                using (mappedStream)
                {
                    while (bytesRemaining > 0)
                    {
                        if (bytesRemaining <= chunkSizeBytes)
                        {
                            chunkSizeBytes = (int)bytesRemaining;
                        }

                        var chunk = new byte[chunkSizeBytes];

                        var bytesRead = mappedStream.Read(chunk, 0, chunkSizeBytes);
                        if (bytesRead == 0)
                        {
                            // End of stream reached
                            break;
                        }                        var validChunk = chunk.AsSpan(0, bytesRead);

                        if (u)
                        {
                            var uh = GetUnicodeHits(validChunk, minLength, maxLength, offset, off, ur);
                            foreach (var h in uh)
                            {
                                hits.Add(h);
                            }
                        }

                        if (a)
                        {
                            string offsetStringSeparator = off ? "\\t" : "";
                            var ah = GetAsciiHitsGpu(
                                chunk,
                                bytesRead,
                                minLength,
                                maxLength,
                                offset,
                                offsetStringSeparator,
                                cp,
                                ar,
                                off
                            );

                            foreach (var h in ah)
                            {
                                hits.Add(h);
                            }
                        }

                        offset += bytesRead;
                        bytesRemaining -= bytesRead;

                        if (!q)
                        {
                            Log.Information(
                                "Chunk {ChunkIndex:N0} of {TotalChunks:N0} finished. Total strings so far: {HitsCount:N0} Elapsed time: {TotalSeconds:N3} seconds. Average strings/sec: {Speed:N0}",
                                chunkIndex,
                                totalChunks,
                                hits.Count,
                                _sw.Elapsed.TotalSeconds,
                                hits.Count / _sw.Elapsed.TotalSeconds
                            );
                        }

                        chunkIndex += 1;
                    }

                    //do chunk boundary checks to make sure we get everything and not split things

                    if (!q)
                    {
                        Log.Information(
                            "Primary search complete. Looking for strings across chunk boundaries..."
                        );
                    }

                    bytesRemaining = fileSizeBytes;
                    chunkSizeBytes = chunkSizeMb * 1024 * 1024;
                    offset = chunkSizeBytes - m * 10 * 2;
                    //move starting point backwards for our starting point
                    chunkIndex = 0;

                    var boundaryChunkSize = m * 10 * 2 * 2;
                    //grab the same # of bytes on both sides of the boundary

                    while (bytesRemaining > 0)
                    {
                        if (offset + boundaryChunkSize > fileSizeBytes)
                        {
                            break;
                        }

                        var chunk = new byte[boundaryChunkSize];

                        var bytesReadBoundary = mappedStream.Read(chunk, 0, boundaryChunkSize);
                        if (bytesReadBoundary == 0)
                        {
                            // End of stream reached
                            break;                        }
                        
                        var validBoundaryChunk = chunk.AsSpan(0, bytesReadBoundary);

                        if (u)
                        {
                            var uh = GetUnicodeHits(validBoundaryChunk, minLength, maxLength, offset, off, ur);
                            foreach (var h in uh)
                            {
                                hits.Add("  " + h);
                            }

                            if (withBoundaryHits == false && uh.Count > 0)
                            {
                                withBoundaryHits = uh.Count > 0;
                            }
                        }

                        if (a)
                        {
                            // Original CPU boundary call
                            var ah = GetAsciiHits(validBoundaryChunk, minLength, maxLength, offset, off, cp, ar);
                            foreach (var h in ah)
                            {
                                hits.Add("  " + h);
                            }

                            if (withBoundaryHits == false && ah.Count > 0)
                            {
                                withBoundaryHits = true;
                            }
                        }

                        offset += chunkSizeBytes;
                        bytesRemaining -= chunkSizeBytes;

                        chunkIndex += 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                Log.Error(ex, "Error: {Message}", ex.Message);
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
                regexStrings.Add(regPattern);
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

                if (fileStrings.Count > 0 || regexStrings.Count > 0)
                {
                    foreach (var fileString in fileStrings)
                    {
                        if (fileString.Trim().Length == 0)
                        {
                            continue;
                        }

                        if (
                            hit.IndexOf(fileString, StringComparison.InvariantCultureIgnoreCase) < 0
                        )
                        {
                            continue;
                        }

                        counter += 1;

                        if (s == false)
                        {
                            Log.Information("{Hit}", hit);
                        }

                        sw?.WriteLine(hit);
                    }

                    var hitOffset = "";
                    if (off)
                    {
                        hitOffset = $"~{hit.Split('\t').Last()}";
                    }

                    foreach (var regString in regexStrings)
                    {
                        if (regString.Trim().Length == 0)
                        {
                            continue;
                        }

                        try
                        {
                            var reg1 = new Regex(
                                regString,
                                RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace
                            );

                            if (reg1.IsMatch(hit) == false)
                            {
                                continue;
                            }

                            counter += 1;

                            if (ro)
                            {
                                foreach (var match in reg1.Matches(hit))
                                {
                                    if (s == false)
                                    {
                                        Log.Information("{Match}\t{HitOffset}", match, hitOffset);
                                    }

                                    sw?.WriteLine($"{match}\t{hitOffset}");
                                }
                            }
                            else
                            {
                                if (s == false)
                                {
                                    Log.Information("{Hit}", hit);
                                }

                                sw?.WriteLine(hit);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(
                                ex,
                                "Error setting up regular expression '{RegString}': {Message}",
                                regString,
                                ex.Message
                            );
                        }
                    }
                }
                else
                {
                    //dump all strings
                    counter += 1;

                    if (s == false)
                    {
                        Log.Information("{Hit}", hit);
                    }

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
                  if (byte.TryParse(minHex, System.Globalization.NumberStyles.HexNumber, null, out byte min) &&
            byte.TryParse(maxHex, System.Globalization.NumberStyles.HexNumber, null, out byte max))
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
    //
    //         r.WholeWords = false;
    //         target.WordHighlightingRules.Add(r);
    //     }    // }

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
        var results = new List<string>();
        var sb = new StringBuilder();
        
        // Process Unicode strings (2 bytes per character)
        for (var i = 0; i < chunk.Length - 1; i += 2)
        {
            if (i + 1 >= chunk.Length)
                break;
            
            char c = (char)(chunk[i] | (chunk[i + 1] << 8));
            
            // Check if character is printable Unicode (basic ASCII range for simplicity)
            if (c >= 32 && c <= 126)
            {
                sb.Append(c);
            }
            else
            {
                // End of string - check if we have a valid string to add
                if (sb.Length >= minLength)
                {
                    var stringToAdd = sb.ToString();
                    if (maxLength > 0 && stringToAdd.Length > maxLength)
                    {
                        stringToAdd = stringToAdd.Substring(0, maxLength);
                    }
                    var hitOffset = currentOffset + i - sb.Length * 2;
                    var offsetOut = originalOffBool
                        ? $"{hitOffset}"
                        : $"0x{hitOffset:X}";
                    results.Add($"{offsetOut}\\t{stringToAdd}");
                }
                sb.Clear();
            }
        }
        
        // Handle string at end of buffer
        if (sb.Length >= minLength)
        {
            var stringToAdd = sb.ToString();
            if (maxLength > 0 && stringToAdd.Length > maxLength)
            {
                stringToAdd = stringToAdd.Substring(0, maxLength);
            }
            var hitOffset = currentOffset + chunk.Length - sb.Length * 2;
            var offsetOut = originalOffBool
                ? $"{hitOffset}"
                : $"0x{hitOffset:X}";
            results.Add($"{offsetOut}\\t{stringToAdd}");
        }
        
        return results;
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
            }

            // Process the hitString as needed
        }
    }    private static List<string> GetAsciiHits(
        ReadOnlySpan<byte> chunk,
        int minLength,
        int maxLength,
        long currentOffsetInFile,
        bool originalOffBool,
        int cp,
        string ar
    )
    {
        var results = new List<string>();
        var sb = new StringBuilder();

        // Parse ASCII range if provided
        var (minChar, maxChar) = ParseCharRange(ar);

        for (var i = 0; i < chunk.Length; i++)
        {
            var currentByte = chunk[i];

            // Check if current byte is in the specified ASCII range
            if (currentByte >= minChar && currentByte <= maxChar)
            {
                sb.Append((char)currentByte);
            }
            else
            {
                // End of string - check if we have a valid string to add
                if (sb.Length >= minLength)
                {
                    var stringToAdd = sb.ToString();
                    
                    // Apply max length limit
                    if (maxLength > 0 && stringToAdd.Length > maxLength)
                    {
                        stringToAdd = stringToAdd.Substring(0, maxLength);
                    }
                    
                    var hitOffset = currentOffsetInFile + i - sb.Length;
                    var offsetOut = originalOffBool ? $"{hitOffset}" : $"0x{hitOffset:X}";
                    results.Add($"{offsetOut}\\t{stringToAdd}");
                }
                sb.Clear();
            }
        }

        // Handle string at end of buffer
        if (sb.Length >= minLength)
        {
            var stringToAdd = sb.ToString();
            
            // Apply max length limit
            if (maxLength > 0 && stringToAdd.Length > maxLength)
            {
                stringToAdd = stringToAdd.Substring(0, maxLength);
            }
            
            var hitOffset = currentOffsetInFile + chunk.Length - sb.Length;
            var offsetOut = originalOffBool ? $"{hitOffset}" : $"0x{hitOffset:X}";
            results.Add($"{offsetOut}\\t{stringToAdd}");
        }

        return results;
    }private static List<string> GetAsciiHitsGpu(
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
    {        var results = new List<string>();
        
        // TODO: GPU kernel needs to be fixed to avoid overlapping substring matches
        // For now, forcing CPU fallback until GPU kernel logic is corrected
        if (true || GpuAccelerator == null || GpuContext == null || bytesRead == 0)
        {
            // Fallback to CPU if GPU is not available/initialized or chunk is empty
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

        try
        {
            int estimatedMaxHits = Math.Max(1024, bytesRead); // Base on bytesRead

            using var dataBuffer = GpuAccelerator.Allocate1D<byte>(bytesRead);
            using var hitsBuffer = GpuAccelerator.Allocate1D<GpuHit>(estimatedMaxHits);
            using var hitCountBuffer = GpuAccelerator.Allocate1D<int>(1);

            // Copy only the valid bytes to GPU
            var validChunk = new byte[bytesRead];
            Array.Copy(chunk, 0, validChunk, 0, bytesRead);
            dataBuffer.CopyFromCPU(validChunk);
            hitCountBuffer.MemSetToZero();

            // Load and compile the kernel (ILGPU caches compiled kernels)
            var kernel = GpuAccelerator.LoadAutoGroupedStreamKernel<
                Index1D,
                ArrayView<byte>,
                int,
                int,
                long,
                ArrayView<GpuHit>,
                ArrayView<int>
            >(AsciiScanKernel);            // Launch configuration: one thread per valid byte
            kernel(
                bytesRead,
                dataBuffer.View,
                minLength,
                maxLength,
                currentOffsetInFile,
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
                    // GpuHit.Offset is the absolute offset in the file
                    var offsetString = originalOffBool ? $"{hit.Offset}" : $"0x{hit.Offset:X}";

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
                        results.Add($"{offsetString}{offSeparator}{foundString}");
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
        return results;
    }

    /// <summary>
    /// GPU Kernel for scanning ASCII strings.
    /// Each thread processes one potential starting byte.
    /// </summary>
    private static void AsciiScanKernel(
        Index1D index, // Current thread index, maps to starting byte in the data
        ArrayView<byte> data, // Chunk of data to scan
        int minLength, // Minimum string length
        int maxLength, // Maximum string length
        long fileChunkBaseOffset, // Base offset of this data chunk in the original file
        ArrayView<GpuHit> hits, // Output buffer for hits
        ArrayView<int> hitCount
    ) // Single-element array to atomically count hits
    {
        int startByteIndex = index;

        // Boundary check for the thread's starting position
        if (startByteIndex >= data.Length)
            return;

        // Check if the current byte is a printable ASCII character
        if (data[startByteIndex] >= 32 && data[startByteIndex] <= 126)
        {
            for (int currentLen = minLength; currentLen <= maxLength; ++currentLen)
            {
                // Ensure the string of currentLen does not exceed data boundaries
                if (startByteIndex + currentLen > data.Length)
                    break;

                bool isValidString = true;
                // Check if all characters in the potential string are printable ASCII
                for (int k = 0; k < currentLen; ++k)
                {
                    if (!(data[startByteIndex + k] >= 32 && data[startByteIndex + k] <= 126))
                    {
                        isValidString = false;
                        break;
                    }
                }

                if (isValidString)
                {
                    // Check termination condition:
                    // String must end either at the end of the data chunk
                    // or be followed by a non-printable character.
                    bool terminationConditionMet = false;
                    if (
                        startByteIndex + currentLen == data.Length
                        || !(
                            data[startByteIndex + currentLen] >= 32
                            && data[startByteIndex + currentLen] <= 126
                        )
                    )
                    {
                        terminationConditionMet = true;
                    }

                    if (terminationConditionMet)
                    {
                        // Atomically increment hit counter and get the index for this hit
                        int currentHitStoredIndex = Atomic.Add(ref hitCount[0], 1);

                        // Store the hit if there's space in the output buffer
                        // Note: If hitCount exceeds hits.Length, hits will be dropped.
                        // A more robust solution might involve resizing or multiple passes.
                        if (currentHitStoredIndex < hits.Length)
                        {
                            hits[currentHitStoredIndex] = new GpuHit
                            {
                                Offset = fileChunkBaseOffset + startByteIndex,
                                Length = currentLen,
                            };
                        }
                        // else: Hit is dropped due to full buffer. Consider logging or handling.
                    }
                }
            }
        }
    }
}
