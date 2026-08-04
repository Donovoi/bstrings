#nullable enable

using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace bstrings;

internal static class BundleCli
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var bundleRootOption = new Option<string?>("--bundle-root")
        {
            Description = "Bundle directory; defaults to the directory containing bstrings.exe",
        };
        var verifyCommand = new Command("verify") { bundleRootOption };
        verifyCommand.Description =
            "Verify the exact bundle file set, lengths, SHA-256 values, paths, and link safety.";

        var actionExitCode = 0;
        verifyCommand.SetAction(result =>
        {
            try
            {
                var root = result.GetValue(bundleRootOption) ?? AppContext.BaseDirectory;
                var verification = BundleManifestVerifier.Verify(root);
                Console.WriteLine(
                    $"Bundle verification passed: {verification.FileCount:N0} files, "
                        + $"{verification.TotalBytes:N0} bytes."
                );
            }
            catch (Exception ex) when (
                ex is ArgumentException
                    or DirectoryNotFoundException
                    or FileNotFoundException
                    or InvalidDataException
                    or IOException
                    or JsonException
                    or UnauthorizedAccessException
            )
            {
                Console.Error.WriteLine($"Bundle verification failed: {ex.Message}");
                actionExitCode = 2;
            }
        });

        var bundleCommand = new Command("bundle") { verifyCommand };
        bundleCommand.Description = "Inspect and verify the complete offline bundle.";
        var rootCommand = new RootCommand { bundleCommand };
        var invocationArguments = new[] { "bundle" }.Concat(args).ToArray();
        var parserExitCode = await rootCommand.Parse(invocationArguments).InvokeAsync();
        return actionExitCode != 0 ? actionExitCode : parserExitCode;
    }
}
