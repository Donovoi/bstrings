﻿using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alphaleonis.Win32.Filesystem;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Streams;
using Exceptionless;
using RawDiskLib;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Directory = Alphaleonis.Win32.Filesystem.Directory;
using File = Alphaleonis.Win32.Filesystem.File;
using FileInfo = Alphaleonis.Win32.Filesystem.FileInfo;
using Path = Alphaleonis.Win32.Filesystem.Path;
using ILGPU;
using ILGPU.Runtime;
using ILGPU.Runtime.OpenCL;
using Exceptionless.Models;
using ILGPU.Runtime.CPU;
using ILGPU.Runtime.Cuda;


namespace bstrings;

internal class Program
{
    private static Stopwatch _sw;
    private static readonly Dictionary<string, string> RegExPatterns = new Dictionary<string, string>();
    private static readonly Dictionary<string, string> RegExDesc = new Dictionary<string, string>();

    public static int ChunkSizeBytes;

    private static readonly string Header =
        $"bstrings version {Assembly.GetExecutingAssembly().GetName().Version}" +
        "\r\n\r\nAuthor: Eric Zimmerman (saericzimmerman@gmail.com)" +
        "\r\nhttps://github.com/EricZimmerman/bstrings";

    private static readonly string Footer = @"Examples: bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls URL" +
                                            "\r\n\t " +
                                            @"   bstrings.exe -f ""C:\Temp\someFile.txt"" --lr guid" + "\r\n\t " +
                                            @"   bstrings.exe -f ""C:\Temp\aBigFile.bin"" --fs c:\temp\searchStrings.txt --fr c:\temp\searchRegex.txt" +
                                            "\r\n\t " +
                                            @"   bstrings.exe -d ""C:\Temp"" --mask ""*.dll""" + "\r\n\t " +
                                            @"   bstrings.exe -d ""C:\Temp"" --ar ""[\x20-\x37]""" + "\r\n\t " +
                                            @"   bstrings.exe -d ""C:\Temp"" --cp 10007" + "\r\n\t " +
                                            @"   bstrings.exe -d ""C:\Temp"" --ls test" + "\r\n\t " +
                                            @"   bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc --sa" +
                                            "\r\n\t " +
                                            @"   bstrings.exe -f ""C:\Temp\someOtherFile.txt"" --lr cc --sa -m 15 -x 22" +
                                            "\r\n\t " +
                                            @"   bstrings.exe -f ""C:\Temp\UsrClass 1.dat"" --ls mui --sl";

    private static RootCommand _rootCommand;

    private static IFileSystem _fileSystem;

    private static readonly string BaseDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

    private static async Task Main(string[] args)
    {
        ExceptionlessClient.Default.Startup("Kruacm8p1B6RFAw2WMnKcEqkQcnWRkF3RmPSOzlW");

        SetupPatterns();

        _rootCommand = new RootCommand
        {
            new Option<string>(
                "-f",
                "File to search. Either this or -d is required"),

            new Option<string>(
                "-d",
                "Directory to recursively process. Either this or -f is required"),

            new Option<string>(
                "-o",
                "File to save results to"),

            new Option<bool>(
                "-a",
                () => true,
                "If set, look for ASCII strings. Use -a false to disable"),

            new Option<bool>(
                "-u",
                () => true,
                "If set, look for Unicode strings. Use -u false to disable"),

            new Option<int>(
                "-m",
                () => 3,
                "Minimum string length"),

            new Option<int>(
                "-b",
                () => 512,
                "Chunk size in MB. Valid range is 1 to 1024. Default is 512"),

            new Option<bool>(
                "-q",
                () => false,
                "Quiet mode (Do not show header or total number of hits)"),

            new Option<int>(
                "-x",
                () => -1,
                "Maximum string length. Default is unlimited"),

            new Option<bool>(
                "-p",
                () => false,
                "Display list of built in regular expressions"),

            new Option<string>(
                "--ls",
                "String to look for. When set, only matching strings are returned"),

            new Option<string>(
                "--lr",
                "Regex to look for. When set, only strings matching the regex are returned"),

            new Option<string>(
                "--fs",
                "File containing strings to look for. When set, only matching strings are returned"),

            new Option<string>(
                "--fr",
                "File containing regex patterns to look for. When set, only strings matching regex patterns are returned"),

            new Option<string>(
                "--ar",
                () => "[\x20-\x7E]",
                @"Range of characters to search for in 'Code page' strings. Specify as a range of characters in hex format and enclose in quotes. Default is [\x20 -\x7E]"),

            new Option<string>(
                "--ur",
                () => "[\u0020-\u007E]",
                @"Range of characters to search for in Unicode strings. Specify as a range of characters in hex format and enclose in quotes. Default is [\\u0020-\\u007E]"),

            new Option<int>(
                "--cp",
                () => 1252,
                "Code page to use. Default is 1252. Use the Identifier value for code pages at https://goo.gl/ig6DxW"),

            new Option<string>(
                "--mask",
                "When using -d, file mask to search for. * and ? are supported. This option has no effect when using -f"),

            new Option<int>(
                "--ms",
                () => -1,
                "When using -d, maximum file size in bytes to process. This option has no effect when using -f"),

            new Option<bool>(
                "--ro",
                () => false,
                "When true, list the string matched by regex pattern vs string the pattern was found in (This may result in duplicate strings in output. ~ denotes approx. offset)"),

            new Option<bool>(
                "--off",
                () => false,
                "Show offset to hit after string, followed by the encoding (A=1252, U=Unicode)"),

            new Option<bool>(
                "--sa",
                () => false,
                "Sort results alphabetically"),

            new Option<bool>(
                "--sl",
                () => false,
                "Sort results by length"),

            new Option<bool>(
                "--debug",
                () => false,
                "Show debug information during processing"),

            new Option<bool>(
                "--trace",
                () => false,
                "Show trace information during processing")
        };

        _rootCommand.Description = Header + "\r\n\r\n" + Footer;

        _rootCommand.Handler = CommandHandler.Create(DoWork);

        await _rootCommand.InvokeAsync(args);

        Log.CloseAndFlush();
    }


    private static void DoWork(string f, string d, string o, bool a, bool u, int m, int b, bool q, bool s, int x,
        bool p, string ls, string[] lr, string fs, string fr, string ar, string ur, int cp, string mask, int ms,
        bool ro,
        bool off, bool sa, bool sl, bool debug, bool trace)
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
            Log.Information("To use a built-in pattern, supply the Name to the --lr switch\r\n");
            return;
        }

        var cpTest = CodePagesEncodingProvider.Instance.GetEncoding(1252);

        if (cpTest == null)
        {
            Log.Warning(
                "Invalid codepage: '{Cp}'. Use the Identifier value for code pages at https://goo.gl/ig6DxW. Verify codepage value and try again",
                cp);
            return;
        }

        if (string.IsNullOrEmpty(f) && string.IsNullOrEmpty(d))
        {
            Log.Warning("Either -f or -d is required. Exiting");
            return;
        }

        if (!string.IsNullOrEmpty(f) && !File.Exists(f) && string.IsNullOrEmpty(mask))
        {
            Log.Warning("File '{F}' not found. Exiting", f);
            return;
        }

        if (!string.IsNullOrEmpty(d) && !Directory.Exists(d) && string.IsNullOrEmpty(mask))
        {
            Log.Warning("Directory '{D}' not found. Exiting", d);
            return;
        }

        if (!q)
        {
            Log.Information("{Header}", Header);
            Console.WriteLine();
        }

        var files = new List<string>();

        if (!string.IsNullOrEmpty(f))
        {
            files.Add(Path.GetFullPath(f));
        }
        else
        {
            try
            {
                var options = DirectoryEnumerationOptions.ContinueOnException | DirectoryEnumerationOptions.Recursive;

                files.AddRange(!string.IsNullOrEmpty(mask)
                    ? Directory.EnumerateFiles(Path.GetFullPath(d!), mask, options)
                    : Directory.EnumerateFiles(Path.GetFullPath(d!), "*", options));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error getting files in '{D}'. Error message: {Message}", d, ex.Message);
                return;
            }
        }

        if (!q)
        {
            Log.Information("Command line: {Args}", string.Join(" ", Environment.GetCommandLineArgs().Skip(1)));
            Console.WriteLine();
        }

        StreamWriter sw = null;
        var globalCounter = 0;
        var globalHits = 0;
        double globalTimespan = 0;
        var withBoundaryHits = false;

        if (!string.IsNullOrEmpty(o) && o.Length > 0)
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

        foreach (var file in files)
        {
            if (File.Exists(file) == false)
            {
                Log.Warning("'{File}' does not exist! Skipping", file);
            }

            _sw = new Stopwatch();
            _sw.Start();
            var counter = 0;
            var hits = new HashSet<string>();

            // Iterate over multiple regex patterns
            foreach (var pattern in lr)
            {
                var regPattern = pattern;

                if (regPattern != null && RegExPatterns.ContainsKey(pattern))
                {
                    regPattern = RegExPatterns[pattern];
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

                var chunkSizeMb = b < 1 ||
                                  b > 1024
                    ? 512
                    : b;
                int chunkSizeBytes = chunkSizeMb * 1024 * 1024;
                GpuAcceleratedSearch.ChunkSizeBytes = chunkSizeBytes;

                var fileSizeBytes = new FileInfo(file).Length;

                if (ms > 0)
                {
                    if (fileSizeBytes > ms)
                    {
                        Log.Warning("'{File}' is bigger than max file size of {Ms:N0} bytes! Skipping...", file, ms);
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
                            "Searching {TotalChunks:N0} chunk ({ChunkSizeMb} MB each) across {SizeReadable} in '{File}'",
                            totalChunks, chunkSizeMb, GetSizeReadable(fileSizeBytes), file);
                    }
                    else
                    {
                        Log.Information(
                            "Searching {TotalChunks:N0} chunks ({ChunkSizeMb} MB each) across {SizeReadable} in '{File}'",
                            totalChunks, chunkSizeMb, GetSizeReadable(fileSizeBytes), file);
                    }


                    Console.WriteLine();
                }

                try
                {
                    MappedStream mappedStream = null;

                    try
                    {
                        var fileStream =
                            //#if NET6_0
                            File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read);

                        //#else
                        //                    fileStream =
                        //                        File.Open(File.GetFileSystemEntryInfo(file).LongFullPath, FileMode.Open, FileAccess.Read,
                        //                            FileShare.Read, PathFormat.LongFullPath);
                        //#endif
                        mappedStream = MappedStream.FromStream(fileStream, Ownership.None);
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    if (mappedStream == null)
                    {
                        //raw mode
                        var ss = OpenFile(file);

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

                            mappedStream.Read(chunk, 0, chunkSizeBytes);

                            if (u)
                            {
                                var uh = GpuAcceleratedSearch.GetUnicodeHits(chunk, minLength, maxLength, offset,
                                    off, ur);
                                foreach (var h in uh)
                                {
                                    hits.Add(h);
                                }
                            }

                            if (a)
                            {
                                var ah = GpuAcceleratedSearch.GetAsciiHits(chunk, minLength, maxLength, offset,
                                    off, cp, ar);
                                foreach (var h in ah)
                                {
                                    hits.Add(h);
                                }
                            }

                            offset += chunkSizeBytes;
                            bytesRemaining -= chunkSizeBytes;

                            if (!q)
                            {
                                Log.Information(
                                    "Chunk {ChunkIndex:N0} of {TotalChunks:N0} finished. Total strings so far: {HitsCount:N0} Elapsed time: {TotalSeconds:N3} seconds. Average strings/sec: {Speed:N0}",
                                    chunkIndex, totalChunks, hits.Count, _sw.Elapsed.TotalSeconds,
                                    hits.Count / _sw.Elapsed.TotalSeconds);
                            }

                            chunkIndex += 1;
                        }

                        //do chunk boundary checks to make sure we get everything and not split things

                        if (!q)
                        {
                            Log.Information("Primary search complete. Looking for strings across chunk boundaries...");
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

                            mappedStream.Read(chunk, 0, boundaryChunkSize);

                            if (u)
                            {
                                var uh = GpuAcceleratedSearch.GetUnicodeHits(chunk, minLength, maxLength, offset,
                                    off, ur);
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
                                var ah = GpuAcceleratedSearch.GetAsciiHits(chunk, minLength, maxLength, offset,
                                    off, cp, ar);
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

                            if (hit.IndexOf(fileString, StringComparison.InvariantCultureIgnoreCase) < 0)
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
                                var reg1 = new Regex(regString,
                                    RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);

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
                                Log.Error(ex, "Error setting up regular expression '{RegString}': {Message}", regString,
                                    ex.Message);
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
                    Log.Information("** Strings prefixed with 2 spaces are hits found across chunk boundaries **");
                    Console.WriteLine();
                }

                if (counter == 1)
                {
                    Log.Information(
                        "Found {Counter:N0} string in {TotalSeconds:N3} seconds. Average strings/sec: {Hits:N0}",
                        counter,
                        _sw.Elapsed.TotalSeconds, hits.Count / _sw.Elapsed.TotalSeconds);
                }
                else
                {
                    Log.Information(
                        "Found {Counter:N0} strings in {TotalSeconds:N3} seconds. Average strings/sec: {Hits:N0}",
                        counter,
                        _sw.Elapsed.TotalSeconds, hits.Count / _sw.Elapsed.TotalSeconds);
                }

                globalCounter += counter;
                globalHits += hits.Count;
                globalTimespan += _sw.Elapsed.TotalSeconds;
                if (files.Count > 1)
                {
                    Log.Information(
                        "-------------------------------------------------------------------------------------");
                    Console.WriteLine();
                }
            }

            // if sw is not closed and not null, close it
            if (sw != null && sw.BaseStream != null && sw.BaseStream.CanWrite)
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
                    files.Count, globalCounter, globalTimespan, globalHits / globalTimespan);
            }
            else
            {
                Log.Information(
                    "Total across {FilesCount:N0} files: Found {GlobalCounter:N0} strings in {GlobalTimespan:N3} seconds. Average strings/sec: {GlobalAve:N0}",
                    files.Count, globalCounter, globalTimespan, globalHits / globalTimespan);
            }

            Console.WriteLine();
        }
    }

    private static SparseStream OpenFile(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new NotSupportedException("Raw disk access not supported on non-Windows systems. Exiting\r\n");
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


        RegExPatterns.Add("b64",
            @"^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=|[A-Za-z0-9+/]{4})$");

        RegExPatterns.Add("bitlocker", @"[0-9]{6}?-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}-[0-9]{6}");

        RegExPatterns.Add("reg_path", @"([a-z0-9]\\)*(software\\)|(sam\\)|(system\\)|(security\\)[a-z0-9\\]+");
        RegExPatterns.Add("var_set", @"^[a-z_0-9]+=[\\/:\*\?<>|;\- _a-z0-9]+");
        RegExPatterns.Add("win_path",
            @"(?:""?[a-zA-Z]\:|\\\\[^\\\/\:\*\?\<\>\|]+\\[^\\\/\:\*\?\<\>\|]*)\\(?:[^\\\/\:\*\?\<\>\|]+\\)*\w([^\\\/\:\*\?\<\>\|])*");
        RegExPatterns.Add("sid", @"^S-\d-\d+-(\d+-){1,14}\d+$");
        RegExPatterns.Add("xml", @"\A<([A-Z][A-Z0-9]*)\b[^>]*>(.*?)</\1>\z");
        RegExPatterns.Add("guid", @"\b[A-F0-9]{8}(?:-[A-F0-9]{4}){3}-[A-F0-9]{12}\b");
        RegExPatterns.Add("usPhone", @"\(?\b[2-9][0-9]{2}\)?[-. ]?[2-9][0-9]{2}[-. ]?[0-9]{4}\b");
        RegExPatterns.Add("unc", @"^\\\\(?<server>[a-z0-9 %._-]+)\\(?<share>[a-z0-9 $%._-]+)");
        RegExPatterns.Add("mac", "\\b[0-9A-F]{2}([-:]?)(?:[0-9A-F]{2}\\1){4}[0-9A-F]{2}\\b");
        RegExPatterns.Add("ssn", "\\b(?!000)(?!666)[0-8][0-9]{2}[- ](?!00)[0-9]{2}[- ](?!0000)[0-9]{4}\\b");
        // RegExPatterns.Add("cc","^(?:4[0-9]{12}(?:[0-9]{3})?|5[1-5][0-9]{14}|6(?:011|5[0-9][0-9])[0-9]{12}|3[47][0-9]{13}|3(?:0[0-5]|[68][0-9])[0-9]{11}|(?:2131|1800|35\\d{3})\\d{11})$");
        RegExPatterns.Add("cc",
            @"^[ -]*(?:4[ -]*(?:\d[ -]*){11}(?:(?:\d[ -]*){3})?\d|5[ -]*[1-5](?:[ -]*[0-9]){14}|6[ -]*(?:0[ -]*1[ -]*1|5[ -]*\d[ -]*\d)(?:[ -]*[0-9]){12}|3[ -]*[47](?:[ -]*[0-9]){13}|3[ -]*(?:0[ -]*[0-5]|[68][ -]*[0-9])(?:[ -]*[0-9]){11}|(?:2[ -]*1[ -]*3[ -]*1|1[ -]*8[ -]*0[ -]*0|3[ -]*5(?:[ -]*[0-9]){3})(?:[ -]*[0-9]){11})[ -]*$");
        RegExPatterns.Add("ipv4",
            @"\b(?:(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\.){3}(?:25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])\b");
        RegExPatterns.Add("ipv6", @"(?<![:.\w])(?:[A-F0-9]{1,4}:){7}[A-F0-9]{1,4}(?![:.\w])");
        //         RegExPatterns.Add("email",@"[a-z0-9!#$%&'*+/=?^_`{|}~-]+(?:\.[a-z0-9!#$%&'*+/=?^_`{|}~-]+)*@(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]*[a-z0-9])?");
        RegExPatterns.Add("email", @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,6}\b");
        RegExPatterns.Add("zip", @"\A\b[0-9]{5}(?:-[0-9]{4})?\b\z");
        RegExPatterns.Add("urlUser", @"^[a-z0-9+\-.]+://(?<user>[a-z0-9\-._~%!$&'()*+,;=]+)@");
        RegExPatterns.Add("url3986", @"^
		[a-z][a-z0-9+\-.]*://                       # Scheme
		([a-z0-9\-._~%!$&'()*+,;=]+@)?              # User
		(?<host>[a-z0-9\-._~%]+                     # Named host
		|\[[a-f0-9:.]+\]                            # IPv6 host
		|\[v[a-f0-9][a-z0-9\-._~%!$&'()*+,;=:]+\])  # IPvFuture host
		(:[0-9]+)?                                  # Port
		(/[a-z0-9\-._~%!$&'()*+,;=:@]+)*/?          # Path
		(\?[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Query
		(\#[a-z0-9\-._~%!$&'()*+,;=:@/?]*)?         # Fragment
		$");
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
    //     }
    // }

    private static IEnumerable<string> SortByLength(IEnumerable<string> e)
    {
        var sorted = from s in e
                     orderby s.Length ascending
                     select s;
        return sorted;
    }

    //private static List<string> GetUnicodeHits(byte[] bytes, int minSize, int maxSize, long currentOffset,
    //    bool withOffsets, string ur)
    //{
    //    var maxString = maxSize == -1 ? "" : maxSize.ToString();
    //    var mi2 = $"{"{"}{minSize}{","}{maxString}{"}"}";

    //    var uniRange = ur;
    //    var regUni = new Regex($"{uniRange}{mi2}", RegexOptions.Compiled);
    //    var uniString = Encoding.Unicode.GetString(bytes);

    //    var hits = new List<string>();

    //    foreach (Match match in regUni.Matches(uniString))
    //    {
    //        if (withOffsets)
    //        {
    //            var actualOffset = (currentOffset + match.Index) * 2;

    //            hits.Add($"{match.Value.Trim()}{'\t'}0x{actualOffset:X} (U)");
    //        }
    //        else
    //        {
    //            hits.Add(match.Value.Trim());
    //        }
    //    }

    //    return hits;
    //}

    //    private static List<string> GetAsciiHits(byte[] bytes, int minSize, int maxSize, long currentOffset,
    //        bool withOffsets, int cp, string ar)
    //    {
    //        var maxString = maxSize == -1 ? "" : maxSize.ToString();
    //        var mi2 = $"{"{"}{minSize}{","}{maxString}{"}"}";

    //        var ascRange = ar;
    //        var regAsc = new Regex($"{ascRange}{mi2}", RegexOptions.Compiled);

    //        var codePage = CodePagesEncodingProvider.Instance.GetEncoding(cp);
    //        var ascString = codePage!.GetString(bytes);

    //        var hits = new List<string>();

    //        foreach (Match match in regAsc.Matches(ascString))
    //        {
    //            if (withOffsets)
    //            {
    //                var matchBytes = codePage!
    //                    .GetBytes(match.Value);

    //                var pos = ByteSearch(bytes, matchBytes, match.Index);

    //                var actualOffset = currentOffset + pos;

    //                hits.Add($"{match.Value.Trim()}{'\t'}0x{actualOffset:X} (A)");
    //            }
    //            else
    //            {
    //                hits.Add(match.Value.Trim());
    //            }
    //        }

    //        return hits;
    //    }
    //}

    public static class GpuAcceleratedSearch
    {
        public static int ChunkSizeBytes { get; internal set; } = 1 << 20; // 1 MB default chunk size

        // Optimized ASCII Hits Function with GPU Integration
        public static HashSet<string> GetAsciiHits(byte[] bytes, int minSize, int maxSize, long currentOffset,
            bool withOffsets, int cp, string pattern)
        {
            var codePage = CodePagesEncodingProvider.Instance.GetEncoding(cp);
            var patternBytes = Encoding.ASCII.GetBytes(pattern);
            var hits = new HashSet<string>();

            // Prepare for GPU execution
            using var context = Context.Create(builder => builder.Cuda());
            using Accelerator accelerator = context.CreateCudaAccelerator(0);

            using var resultBuffer = accelerator.Allocate1D<int>(bytes.Length);
            using var patternBuffer = accelerator.Allocate1D<byte>(patternBytes.Length);
            using var searchInBuffer = accelerator.Allocate1D<byte>(ChunkSizeBytes);

            // Copy the pattern to GPU
            patternBuffer.CopyFromCPU(patternBytes);

            for (long i = 0; i < bytes.LongLength; i += ChunkSizeBytes)
            {
                var chunkSize = (i + ChunkSizeBytes > bytes.Length) ? (int)(bytes.Length - i) : ChunkSizeBytes;
                searchInBuffer.CopyFromCPU(bytes);

                // Load and execute the pattern matching kernel
                var kernel =
                    accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<int>, ArrayView<byte>>(
                        UnicodeOptimizedSearchKernel);
                kernel((int)searchInBuffer.Length, searchInBuffer.View, resultBuffer.View, patternBuffer.View);
                accelerator.Synchronize();

                var results = resultBuffer.GetAsArray1D();
                foreach (var result in results)
                {
                    if (result < 0) continue;
                    var actualOffset = currentOffset + i + result;
                    hits.Add($"Pattern found at offset: 0x{actualOffset:X}");
                }
            }

            return hits;
        }

        // Optimized Unicode Hits Function with GPU Integration
        public static List<string> GetUnicodeHits(byte[] bytes, int minSize, int maxSize, long currentOffset, bool withOffsets, string ur)
        {
            var maxString = maxSize == -1 ? "" : maxSize.ToString();
            var hits = new List<string>();
            var regexPattern = $"{ur}{{{minSize},{maxString}}}";

            // Create a context specifically targeting CUDA devices
            using var context = Context.Create(builder => builder.Cuda());

            // Select the first CUDA device available (assuming there is one)
            using var accelerator = context.CreateCudaAccelerator(0);
            if (accelerator == null)
                throw new InvalidOperationException(
                    "No CUDA device found. Please ensure a CUDA-compatible GPU is available.");

            // Allocate a result buffer to hold search results (int buffer)
            using var resultBuffer = accelerator.Allocate1D<int>(bytes.Length / 2); // Unicode uses 2 bytes per character

            // Iterate through byte array in chunks to avoid large memory accesses
            for (long i = 0; i < bytes.LongLength; i += ChunkSizeBytes)
            {
                // Calculate the remaining bytes and ensure chunkSize is valid
                var chunkSize = (int)Math.Min(ChunkSizeBytes, bytes.LongLength - i);

                // Ensure that the chunk is not empty or invalid
                if (chunkSize <= 0)
                {
                    Console.WriteLine($"Skipping chunk at offset {i} due to invalid chunk size.");
                    continue; // Skip this chunk
                }

                // Allocate and copy the current chunk to the GPU
                using var searchInBuffer = accelerator.Allocate1D<byte>(chunkSize);

                // Check if the chunk data is valid before copying
                var chunkData = new byte[chunkSize];
                Array.Copy(bytes, i, chunkData, 0, chunkSize);

                try
                {
                    searchInBuffer.CopyFromCPU(chunkData); // Copy only the chunk data
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    Console.WriteLine($"Error copying data to GPU: {ex.Message}. Skipping this chunk.");
                    continue; // Skip this chunk in case of an error
                }

                // Create and launch the kernel
                var kernel =
                    accelerator.LoadAutoGroupedStreamKernel<Index1D, ArrayView<byte>, ArrayView<int>>(
                        AsciiOptimizedSearchKernel);
                kernel(chunkSize, searchInBuffer.View, resultBuffer.View);
                accelerator.Synchronize();

                // Retrieve and process results
                var results = resultBuffer.GetAsArray1D();
                foreach (var result in results)
                {
                    if (result < 0) continue;
                    var actualOffset = currentOffset + i + result * 2; // Adjust for Unicode size
                    hits.Add($"Unicode pattern found at offset: 0x{actualOffset:X}");
                    Log.Information($"Unicode Match found at 0x{actualOffset:X} the match is: {result}");
                }
            }

            return hits;
        }

        // Kernel function for optimized pattern search using byte arrays
        private static void AsciiOptimizedSearchKernel(Index1D index, ArrayView<byte> searchIn, ArrayView<int> result)
        {
            // Check if the index is within the bounds of the data to avoid illegal memory access
            if (index >= searchIn.Length || index >= result.Length)
                return;

            // Clear the result array before starting
            result[index] = -1;

            // Perform a simple search for repeating patterns
            for (int i = index + 1; i < searchIn.Length; i++)
            {
                if (searchIn[index] != searchIn[i]) continue; // Simple pattern search logic (adjust based on requirements)
                result[index] = i; // Store the match index
                break;
            }
        }

        // Kernel function for optimized pattern search using byte arrays
        private static void UnicodeOptimizedSearchKernel(Index1D index, ArrayView<byte> searchIn, ArrayView<int> result,
            ArrayView<byte> pattern)
        {
            if (index >= searchIn.Length || index >= result.Length) return;

            result[index] = -1; // Default result is -1 (no match)

            // Check for pattern match starting at the current index
            if (index + pattern.Length > searchIn.Length) return;
            var match = true;
            for (var i = 0; i < pattern.Length; i++)
            {
                if (searchIn[index + i] == pattern[i]) continue;
                match = false;
                break;
            }

            if (match)
            {
                result[index] = index; // Store the starting index of the match
            }
        }
    }
}
