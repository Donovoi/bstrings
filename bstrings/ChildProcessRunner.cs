#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal sealed record ChildProcessResult(
    int ExitCode,
    IReadOnlyList<string> StandardOutputTail,
    IReadOnlyList<string> StandardErrorTail
);

internal sealed class ChildProcessFailedException : InvalidOperationException
{
    internal ChildProcessFailedException(string message, ChildProcessResult result)
        : base(message)
    {
        Result = result;
    }

    internal ChildProcessResult Result { get; }
}

internal static class ChildProcessRunner
{
    private const int TailCapacity = 40;

    internal static async Task<ChildProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string stdoutLogPath,
        string stderrLogPath,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default
    )
    {
        return await RunAsync(
            executable,
            arguments,
            workingDirectory,
            stdoutLogPath,
            stderrLogPath,
            environment,
            CreateLogWriter,
            cancellationToken
        );
    }

    internal static async Task<ChildProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string stdoutLogPath,
        string stderrLogPath,
        IReadOnlyDictionary<string, string?>? environment,
        Func<string, StreamWriter> logWriterFactory,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(logWriterFactory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var (name, value) in environment)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(stdoutLogPath))!);
        await using var stdoutWriter = logWriterFactory(stdoutLogPath);
        await using var stderrWriter = logWriterFactory(stderrLogPath);
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start '{executable}'.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not start bundled executable '{executable}': {ex.Message}",
                ex
            );
        }
        process.StandardInput.Close();

        var stdoutTail = new Queue<string>(TailCapacity);
        var stderrTail = new Queue<string>(TailCapacity);
        var stdoutTask = DrainAsync(
            process.StandardOutput,
            stdoutWriter,
            stdoutTail,
            cancellationToken
        );
        var stderrTask = DrainAsync(
            process.StandardError,
            stderrWriter,
            stderrTail,
            cancellationToken
        );

        try
        {
            await WaitForExitAndDrainsAsync(
                process.WaitForExitAsync(cancellationToken),
                stdoutTask,
                stderrTask
            );
        }
        catch
        {
            // A failed log write must be observed before the child fills that pipe and blocks.
            // Killing closes both pipes; awaiting both drains then prevents background reads from
            // racing writer disposal without replacing the failure that brought us here.
            TryKill(process);
            await Task.WhenAll(IgnoreFailure(stdoutTask), IgnoreFailure(stderrTask));
            throw;
        }

        var result = new ChildProcessResult(
            process.ExitCode,
            stdoutTail.ToArray(),
            stderrTail.ToArray()
        );
        if (result.ExitCode != 0)
        {
            var diagnostic = result.StandardErrorTail.Count > 0
                ? string.Join(Environment.NewLine, result.StandardErrorTail)
                : string.Join(Environment.NewLine, result.StandardOutputTail);
            throw new ChildProcessFailedException(
                $"'{Path.GetFileName(executable)}' exited with code {result.ExitCode}. "
                    + $"See '{stderrLogPath}'."
                    + (diagnostic.Length == 0 ? string.Empty : Environment.NewLine + diagnostic),
                result
            );
        }
        return result;
    }

    private static async Task WaitForExitAndDrainsAsync(
        Task processExitTask,
        Task stdoutTask,
        Task stderrTask
    )
    {
        var activeDrains = new List<Task>(2) { stdoutTask, stderrTask };
        while (activeDrains.Count > 0 && !processExitTask.IsCompleted)
        {
            var waiters = new Task[activeDrains.Count + 1];
            waiters[0] = processExitTask;
            activeDrains.CopyTo(waiters, 1);
            var completed = await Task.WhenAny(waiters);
            if (ReferenceEquals(completed, processExitTask))
            {
                break;
            }

            activeDrains.Remove(completed);
            await completed;
        }

        await processExitTask;
        await Task.WhenAll(stdoutTask, stderrTask);
    }

    private static async Task DrainAsync(
        StreamReader reader,
        StreamWriter writer,
        Queue<string> tail,
        CancellationToken cancellationToken
    )
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            if (tail.Count == TailCapacity)
            {
                tail.Dequeue();
            }
            tail.Enqueue(line);
        }
        await writer.FlushAsync(cancellationToken);
    }

    private static async Task IgnoreFailure(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception) { }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static StreamWriter CreateLogWriter(string path) =>
        new(
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan
            ),
            new UTF8Encoding(false),
            64 * 1024
        );
}
