using System.Text;
using Xunit;

namespace bstrings.Tests;

public sealed class ChildProcessRunnerTests
{
    private const string LogFailureMessage = "simulated log write failure";

    [Fact]
    public async Task RunAsync_StdoutLogFailureKillsFloodingChildAndFinishesStderrDrain()
    {
        using var scope = new TemporaryDirectory();
        var stdoutLogPath = scope.PathFor("stdout.log");
        var stderrLogPath = scope.PathFor("stderr.log");
        var (executable, arguments) = CreateFloodCommand(floodStandardOutput: true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ChildProcessRunner.RunAsync(
                executable,
                arguments,
                scope.DirectoryPath,
                stdoutLogPath,
                stderrLogPath,
                environment: null,
                path => CreateWriter(path, stdoutLogPath),
                cancellation.Token
            )
        );

        Assert.Contains(LogFailureMessage, error.Message, StringComparison.Ordinal);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Contains(
            "stderr-ready",
            await File.ReadAllTextAsync(stderrLogPath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task RunAsync_StderrLogFailureKillsFloodingChildAndFinishesStdoutDrain()
    {
        using var scope = new TemporaryDirectory();
        var stdoutLogPath = scope.PathFor("stdout.log");
        var stderrLogPath = scope.PathFor("stderr.log");
        var (executable, arguments) = CreateFloodCommand(floodStandardOutput: false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        cancellation.CancelAfter(TimeSpan.FromSeconds(15));

        var error = await Assert.ThrowsAsync<IOException>(() =>
            ChildProcessRunner.RunAsync(
                executable,
                arguments,
                scope.DirectoryPath,
                stdoutLogPath,
                stderrLogPath,
                environment: null,
                path => CreateWriter(path, stderrLogPath),
                cancellation.Token
            )
        );

        Assert.Contains(LogFailureMessage, error.Message, StringComparison.Ordinal);
        Assert.False(cancellation.IsCancellationRequested);
        Assert.Contains(
            "stdout-ready",
            await File.ReadAllTextAsync(stdoutLogPath, TestContext.Current.CancellationToken),
            StringComparison.Ordinal
        );
    }

    private static StreamWriter CreateWriter(string path, string failingPath)
    {
        if (Path.GetFullPath(path).Equals(Path.GetFullPath(failingPath), PathComparison))
        {
            return new StreamWriter(
                new FailOnceWriteStream(),
                new UTF8Encoding(false),
                bufferSize: 128
            )
            {
                AutoFlush = true,
            };
        }

        return new StreamWriter(
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous
            ),
            new UTF8Encoding(false),
            bufferSize: 4096
        )
        {
            AutoFlush = true,
        };
    }

    private static (string Executable, string[] Arguments) CreateFloodCommand(
        bool floodStandardOutput
    )
    {
        if (OperatingSystem.IsWindows())
        {
            var ready = floodStandardOutput ? "echo stderr-ready 1>&2" : "echo stdout-ready";
            var redirect = floodStandardOutput ? string.Empty : " 1>&2";
            var command = $"{ready} & for /L %i in (1,1,1000000) do @echo flood-line-%i{redirect}";
            return (
                Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                ["/d", "/s", "/c", command]
            );
        }

        var unixReady = floodStandardOutput
            ? "printf 'stderr-ready\\n' >&2"
            : "printf 'stdout-ready\\n'";
        var unixRedirect = floodStandardOutput ? string.Empty : " >&2";
        var unixCommand =
            $"{unixReady}; i=0; while [ \"$i\" -lt 1000000 ]; do "
            + $"printf 'flood-line-%s\\n' \"$i\"{unixRedirect}; i=$((i + 1)); done";
        return ("/bin/sh", ["-c", unixCommand]);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private sealed class FailOnceWriteStream : Stream
    {
        private int hasFailed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowOnFirstWrite();
        }

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            try
            {
                ThrowOnFirstWrite();
                return Task.CompletedTask;
            }
            catch (Exception error)
            {
                return Task.FromException(error);
            }
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default
        )
        {
            try
            {
                ThrowOnFirstWrite();
                return ValueTask.CompletedTask;
            }
            catch (Exception error)
            {
                return ValueTask.FromException(error);
            }
        }

        private void ThrowOnFirstWrite()
        {
            if (Interlocked.Exchange(ref hasFailed, 1) == 0)
            {
                throw new IOException(LogFailureMessage);
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "bstrings-child-process-tests",
                Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(DirectoryPath);
        }

        internal string DirectoryPath { get; }

        internal string PathFor(string name) => Path.Combine(DirectoryPath, name);

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
