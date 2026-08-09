#nullable enable

using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal static class BundleCli
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var bundleRootOption = new Option<string?>("--bundle-root")
        {
            Description = "Complete bundle directory; defaults to the directory containing bstrings.exe",
        };
        var allowIncompleteMarkerOption = new Option<bool>("--allow-incomplete-marker")
        {
            Description =
                "Builder-only: verify all manifested bytes while the reserved root marker exists",
        };
        var verifyCommand = new Command("verify")
        {
            bundleRootOption,
            allowIncompleteMarkerOption,
        };
        verifyCommand.Description =
            "Verify every manifested file, byte length, SHA-256, path, and link-safety rule. Reports measured byte progress from 0% to 100%.";

        var acquireManifestOption = CreateManifestOption();
        var acquireCacheOption = CreateCacheOption();
        var acquireOutputOption = CreateOutputOption();
        var acquireSeedBundleOption = new Option<string?>("--seed-bundle")
        {
            Description =
                "Existing physical bundle root whose exact current-manifest file packs may seed missing cache objects before network acquisition",
        };
        var acquireCommand = new Command("acquire")
        {
            acquireManifestOption,
            acquireCacheOption,
            acquireOutputOption,
            acquireSeedBundleOption,
        };
        acquireCommand.Description =
            "Download and hash exact split packs with resumable cache, then assemble and verify a new complete offline bundle. Preserves verified cache on cancellation.";

        var assembleManifestOption = CreateManifestOption();
        var assembleCacheOption = CreateCacheOption();
        var assembleOutputOption = CreateOutputOption();
        var assembleCommand = new Command("assemble")
        {
            assembleManifestOption,
            assembleCacheOption,
            assembleOutputOption,
        };
        assembleCommand.Description =
            "Hash already-cached packs, then assemble and verify a new complete offline bundle without downloading.";

        var actionExitCode = 0;
        verifyCommand.SetAction(result =>
        {
            try
            {
                var percentage = new ConsolePercentageProgress();
                var root = result.GetValue(bundleRootOption) ?? AppContext.BaseDirectory;
                var verification = BundleManifestVerifier.Verify(
                    root,
                    result.GetValue(allowIncompleteMarkerOption),
                    (completed, total) =>
                        percentage.Report("bundle verification", completed, total, "bytes")
                );
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

        async Task ExecutePackActionAsync(
            Func<CancellationToken, Task<BundlePackInstallationResult>> action
        )
        {
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                var result = await action(cancellation.Token);
                Console.WriteLine(
                    $"Bundle '{result.BundleIdentity}' assembled successfully: "
                        + $"{result.FileCount:N0} files, {result.TotalBytes:N0} bytes."
                );
                Console.WriteLine($"Output: {result.OutputDirectory}");
                Console.WriteLine($"Verified pack cache: {result.CacheDirectory}");
            }
            catch (OperationCanceledException)
            {
                Console.Error.WriteLine(
                    "Bundle operation was cancelled; verified cached packs were preserved."
                );
                actionExitCode = 130;
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
                Console.Error.WriteLine($"Bundle operation failed: {ex.Message}");
                actionExitCode = 2;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        acquireCommand.SetAction(async result =>
        {
            var percentage = new ConsolePercentageProgress();
            await ExecutePackActionAsync(cancellationToken =>
                BundlePackInstaller.AcquireAndAssembleAsync(
                    result.GetValue(acquireManifestOption),
                    result.GetValue(acquireCacheOption),
                    result.GetValue(acquireOutputOption)!,
                    cancellationToken,
                    progress: (activity, completed, total) =>
                        percentage.Report(activity, completed, total, "bytes"),
                    seedBundleDirectory: result.GetValue(acquireSeedBundleOption)
                )
            );
        }
        );
        assembleCommand.SetAction(async result =>
        {
            var percentage = new ConsolePercentageProgress();
            await ExecutePackActionAsync(cancellationToken =>
                Task.FromResult(
                    BundlePackInstaller.Assemble(
                        result.GetValue(assembleManifestOption),
                        result.GetValue(assembleCacheOption),
                        result.GetValue(assembleOutputOption)!,
                        cancellationToken,
                        (activity, completed, total) =>
                            percentage.Report(activity, completed, total, "bytes")
                    )
                )
            );
        }
        );

        var bundleCommand = new Command("bundle")
        {
            verifyCommand,
            acquireCommand,
            assembleCommand,
        };
        bundleCommand.Description =
            "Acquire, assemble, and verify complete offline bundles through bstrings.exe.\n\n"
            + "Examples:\n"
            + "  bstrings.exe bundle verify\n"
            + "  bstrings.exe help bundle acquire\n\n"
            + "Acquire and assemble require a new output directory. Progress percentages report completed bytes, not an ETA.";
        var rootCommand = new RootCommand { bundleCommand };
        var invocationArguments = new[] { "bundle" }.Concat(args).ToArray();
        var parserExitCode = await rootCommand.Parse(invocationArguments).InvokeAsync();
        return actionExitCode != 0 ? actionExitCode : parserExitCode;
    }

    private static Option<string?> CreateManifestOption() =>
        new("--manifest")
        {
            Description =
                $"Authenticated local split-pack trust manifest; defaults to adjacent {BundlePackInstaller.PackManifestFileName}",
        };

    private static Option<string?> CreateCacheOption() =>
        new("--cache")
        {
            Description =
                "Verified pack cache; defaults beside the trust manifest under bundle-pack-cache/<profile>",
        };

    private static Option<string> CreateOutputOption() =>
        new("--output")
        {
            Description = "New, nonexistent output directory for the complete verified offline bundle",
            Required = true,
        };
}
