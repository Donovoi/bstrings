#nullable enable

using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal static class AnalysisCli
{
    internal static async Task<int> RunAsync(string[] args)
    {
        var fileOption = new Option<string?>("-f")
        {
            Description = "Evidence file to analyze. Either this or -d is required",
        };
        var directoryOption = new Option<string?>("-d")
        {
            Description = "Directory to analyze recursively. Either this or -f is required",
        };
        var outputOption = new Option<string>("-o")
        {
            Description = "New or empty results directory",
            Required = true,
        };
        var maskOption = new Option<string?>("--mask")
        {
            Description = "File mask used with -d. Supports * and ?",
        };
        var fullOption = new Option<bool>("--full")
        {
            Description =
                "Run native extraction, executable recovery, language triage, offline translation, and all pattern matching",
        };
        var recoveryOption = new Option<string?>("--recover-executable-strings")
        {
            Description = "Executable string recovery: off, auto, or force",
        };
        var translationOption = new Option<string?>("--translation")
        {
            Description = "Translation workflow: off, auto, all, or detect-only",
        };
        var detectionOption = new Option<string>("--language-detection")
        {
            Description = "Offline detector profile: adaptive, accurate, or fast",
            DefaultValueFactory = _ => "adaptive",
        };
        var policyOption = new Option<string>("--translation-policy")
        {
            Description = "Automatic translation gate: high-recall, balanced, or high-precision",
            DefaultValueFactory = _ => "high-recall",
        };
        var confidenceOption = new Option<double>("--language-confidence")
        {
            Description = "Minimum top-language confidence for balanced/precision policies",
            DefaultValueFactory = _ => 0.55,
        };
        var marginOption = new Option<double>("--language-margin")
        {
            Description = "Minimum confidence lead over the target language",
            DefaultValueFactory = _ => 0.10,
        };
        var targetOption = new Option<string>("--translation-target")
        {
            Description = "Target language code used for triage and translation",
            DefaultValueFactory = _ => "en",
        };
        var translationDeviceOption = new Option<string>("--translation-device")
        {
            Description = "Translation hardware: auto, cpu, cuda, or hybrid",
            DefaultValueFactory = _ => "auto",
        };
        var translationParallelismOption = new Option<int>("--translation-parallelism")
        {
            Description = "llama.cpp request slots; 0 selects automatically",
            DefaultValueFactory = _ => 0,
        };
        var translationThreadsOption = new Option<int>("--translation-threads")
        {
            Description = "Translation CPU threads; 0 keeps the runtime default",
            DefaultValueFactory = _ => 0,
        };
        var translationGpuLayersOption = new Option<int>("--translation-gpu-layers")
        {
            Description =
                "Exact llama.cpp GPU layer count for hybrid mode; -1 selects automatically elsewhere",
            DefaultValueFactory = _ => -1,
        };
        var patternOption = new Option<string>("--lr")
        {
            Description = "Built-in pattern names, a group, a custom regex, or all",
            DefaultValueFactory = _ => "all",
        };
        var regexFileOption = new Option<string?>("--fr")
        {
            Description = "File containing additional regex patterns",
        };
        var processorOption = new Option<string>("--processor")
        {
            Description = "Extraction processor: auto, cpu, gpu, or hybrid",
            DefaultValueFactory = _ => "auto",
        };
        var cpuEngineOption = new Option<string>("--cpu-engine")
        {
            Description = "ASCII CPU engine: dotnet, rust, or auto",
            DefaultValueFactory = _ => "auto",
        };
        var minimumLengthOption = new Option<int>("--minimum-length")
        {
            Description = "Minimum extracted string length",
            DefaultValueFactory = _ => 3,
        };
        var maximumLengthOption = new Option<int>("--maximum-length")
        {
            Description =
                $"Maximum extracted string length for JSONL analysis (up to {EnrichmentRegexPipelineCore.MaxNativeTextCharacters:N0})",
            DefaultValueFactory = _ => EnrichmentRegexPipelineCore.MaxNativeTextCharacters,
        };
        var translationMinimumOption = new Option<int>("--translation-min-characters")
        {
            Description = "Shortest string eligible for language triage and translation",
            DefaultValueFactory = _ => 8,
        };
        var translationMaximumOption = new Option<int>("--translation-max-characters")
        {
            Description = "Longest string eligible for language triage and translation",
            DefaultValueFactory = _ => 2048,
        };
        var bundleRootOption = new Option<string?>("--bundle-root")
        {
            Description = "Directory containing airgap-config.json and bundled tools",
        };
        var airgapOption = new Option<bool>("--airgap")
        {
            Description = "Require the explicit or BSTRINGS_AIRGAP_BUNDLE directory",
        };

        var command = new Command("analyze")
        {
            fileOption,
            directoryOption,
            outputOption,
            maskOption,
            fullOption,
            recoveryOption,
            translationOption,
            detectionOption,
            policyOption,
            confidenceOption,
            marginOption,
            targetOption,
            translationDeviceOption,
            translationParallelismOption,
            translationThreadsOption,
            translationGpuLayersOption,
            patternOption,
            regexFileOption,
            processorOption,
            cpuEngineOption,
            minimumLengthOption,
            maximumLengthOption,
            translationMinimumOption,
            translationMaximumOption,
            bundleRootOption,
            airgapOption,
        };
        command.Description =
            "Run the provenance-preserving bstrings analysis workflow through one executable.";

        var actionExitCode = 0;
        command.SetAction(
            async result =>
            {
                try
                {
                    var full = result.GetValue(fullOption);
                    var recoveryText = result.GetValue(recoveryOption) ?? (full ? "auto" : "off");
                    var translationText =
                        result.GetValue(translationOption) ?? (full ? "auto" : "off");
                    if (!TryParseRecoveryMode(recoveryText, out var recoveryMode, out var error))
                    {
                        throw new ArgumentException(error);
                    }
                    if (!TryParseTranslationMode(translationText, out var translationMode, out error))
                    {
                        throw new ArgumentException(error);
                    }
                    if (
                        !LanguageDetectionCore.TryParseMode(
                            result.GetValue(detectionOption),
                            out var detectionMode,
                            out error
                        )
                    )
                    {
                        throw new ArgumentException(error);
                    }
                    if (
                        !LanguageTriageCore.TryParsePolicy(
                            result.GetValue(policyOption),
                            out var policy,
                            out error
                        )
                    )
                    {
                        throw new ArgumentException(error);
                    }

                    var minimumStringLength = result.GetValue(minimumLengthOption);
                    var maximumStringLength = result.GetValue(maximumLengthOption);
                    ValidateStringLengthBounds(minimumStringLength, maximumStringLength);

                    var options = new AnalysisOptions(
                        result.GetValue(fileOption),
                        result.GetValue(directoryOption),
                        result.GetValue(maskOption),
                        result.GetValue(outputOption)!,
                        full,
                        recoveryMode,
                        translationMode,
                        detectionMode,
                        policy,
                        result.GetValue(confidenceOption),
                        result.GetValue(marginOption),
                        result.GetValue(targetOption)!,
                        result.GetValue(translationDeviceOption)!,
                        result.GetValue(translationParallelismOption),
                        result.GetValue(translationThreadsOption),
                        result.GetValue(translationGpuLayersOption),
                        result.GetValue(patternOption)!,
                        result.GetValue(regexFileOption),
                        result.GetValue(processorOption)!,
                        result.GetValue(cpuEngineOption)!,
                        minimumStringLength,
                        maximumStringLength,
                        result.GetValue(translationMinimumOption),
                        result.GetValue(translationMaximumOption),
                        result.GetValue(bundleRootOption),
                        result.GetValue(airgapOption)
                    );

                    using var cancellation = new CancellationTokenSource();
                    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        cancellation.Cancel();
                    };
                    Console.CancelKeyPress += cancelHandler;
                    try
                    {
                        await AnalysisOrchestrator.RunAsync(options, cancellation.Token);
                    }
                    finally
                    {
                        Console.CancelKeyPress -= cancelHandler;
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.Error.WriteLine("Analysis was cancelled; the results remain marked incomplete.");
                    actionExitCode = 130;
                }
                catch (Exception ex) when (
                    ex is ArgumentException
                    or FileNotFoundException
                    or DirectoryNotFoundException
                    or InvalidDataException
                    or InvalidOperationException
                    or IOException
                    or JsonException
                    or UnauthorizedAccessException
                )
                {
                    Console.Error.WriteLine($"Analysis failed: {ex.Message}");
                    actionExitCode = 2;
                }
            }
        );

        var root = new RootCommand { command };
        var invocationArguments = new[] { "analyze" }.Concat(args).ToArray();
        var parserExitCode = await root.Parse(invocationArguments).InvokeAsync();
        return actionExitCode != 0 ? actionExitCode : parserExitCode;
    }

    internal static void ValidateStringLengthBounds(int minimumLength, int maximumLength)
    {
        if (minimumLength < 3)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumLength),
                "Minimum string length must be at least 3."
            );
        }
        if (maximumLength < minimumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength),
                "Maximum string length must be at least the minimum. Unlimited output is not supported by analyze."
            );
        }
        if (maximumLength > EnrichmentRegexPipelineCore.MaxNativeTextCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumLength),
                $"Maximum string length cannot exceed {EnrichmentRegexPipelineCore.MaxNativeTextCharacters:N0} in the JSONL analysis workflow."
            );
        }
    }

    private static bool TryParseRecoveryMode(
        string value,
        out ExecutableRecoveryMode mode,
        out string? error
    )
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
                mode = ExecutableRecoveryMode.Off;
                error = null;
                return true;
            case "auto":
                mode = ExecutableRecoveryMode.Auto;
                error = null;
                return true;
            case "force":
                mode = ExecutableRecoveryMode.Force;
                error = null;
                return true;
            default:
                mode = ExecutableRecoveryMode.Off;
                error = "Executable recovery must be off, auto, or force.";
                return false;
        }
    }

    private static bool TryParseTranslationMode(
        string value,
        out TranslationWorkflowMode mode,
        out string? error
    )
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
                mode = TranslationWorkflowMode.Off;
                error = null;
                return true;
            case "auto":
                mode = TranslationWorkflowMode.Auto;
                error = null;
                return true;
            case "all":
            case "translate-all":
                mode = TranslationWorkflowMode.All;
                error = null;
                return true;
            case "detect-only":
            case "detect":
                mode = TranslationWorkflowMode.DetectOnly;
                error = null;
                return true;
            default:
                mode = TranslationWorkflowMode.Off;
                error = "Translation must be off, auto, all, or detect-only.";
                return false;
        }
    }
}
