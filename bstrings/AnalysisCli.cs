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
    internal const string DefaultTranslationPolicy = "high-recall";
    internal const string TranslationPolicyHelp =
        "Automatic translation gate used by --translation auto: high-recall keeps uncertain detections; balanced uses the configured confidence and margin; high-precision applies floors of 0.65 confidence and 0.15 margin";
    internal const string FullProfileHelp =
        "Run the single Full profile: hash inputs, classify each input in one early shared pass, always extract native strings, route applicable files to FLOSS/OCR, then fail-open shadow translation-worthiness routing and language triage, pinned 7B Q4_K_M offline translation (validated sm89 CUDA p2 or pre-evidence CPU selection), all patterns, and reports; shadow routing does not yet remove candidates, and an explicit stage 'off' overrides its default";
    internal const string TranslationDeviceHelp =
        "Translation hardware: auto, cpu, cuda, or hybrid; separate from native --processor (the quality kit promotes only validated compute capability 8.9 to full-offload CUDA p2, otherwise auto selects CPU before evidence inference; explicit cuda fails closed)";

    internal static async Task<int> RunAsync(string[] args)
    {
        var fileOption = new Option<string?>("-f")
        {
            Description = "Evidence file or raw byte image. Specify exactly one of -f or -d",
        };
        var directoryOption = new Option<string?>("-d")
        {
            Description = "Evidence directory to analyze recursively. Specify exactly one of -f or -d",
        };
        var outputOption = new Option<string>("-o")
        {
            Description = "New or empty results directory outside the evidence tree and installed bundle",
            Required = true,
        };
        var maskOption = new Option<string?>("--mask")
        {
            Description = "File mask used with -d. Supports * and ?",
        };
        var fullOption = new Option<bool>("--full")
        {
            Description = FullProfileHelp,
        };
        var ocrOption = new Option<string?>("--ocr")
        {
            Description = "OCR/PDF workflow: off, auto (early fail-open routing, text layers, then needed OCR), or force (OCR every page)",
        };
        var ocrProviderOption = new Option<string?>("--ocr-provider")
        {
            Description = "OCR hardware: auto, cpu, directml, hybrid, or custom-profile cuda; independent of --processor",
        };
        var ocrThreadsOption = new Option<int>("--ocr-threads")
        {
            Description =
                $"ONNX session threads; 0 selects a bounded provider-aware value, up to {OcrCompletionCore.MaximumSessionThreads}",
            DefaultValueFactory = _ => 0,
        };
        var recoveryOption = new Option<string?>("--recover-executable-strings")
        {
            Description = "FLOSS recovery: off, auto (batched Magika plus validated PE-signature routing), or force (every supplied file)",
        };
        var translationOption = new Option<string?>("--translation")
        {
            Description = "Language/translation workflow: off, auto (policy-selected), all eligible text, or detect-only",
        };
        var detectionOption = new Option<string>("--language-detection")
        {
            Description = "Offline language detector: adaptive samples the workload; accurate and fast force a profile",
            DefaultValueFactory = _ => "adaptive",
        };
        var policyOption = new Option<string>("--translation-policy")
        {
            Description = TranslationPolicyHelp,
            DefaultValueFactory = _ => DefaultTranslationPolicy,
        };
        var confidenceOption = new Option<double>("--language-confidence")
        {
            Description = "Configured minimum top-language confidence; high-precision raises values below 0.65",
            DefaultValueFactory = _ => 0.55,
        };
        var marginOption = new Option<double>("--language-margin")
        {
            Description = "Configured minimum confidence lead over the target language; high-precision raises values below 0.15",
            DefaultValueFactory = _ => 0.10,
        };
        var targetOption = new Option<string>("--translation-target")
        {
            Description = "Target language code used for triage and translation",
            DefaultValueFactory = _ => "en",
        };
        var translationDeviceOption = new Option<string>("--translation-device")
        {
            Description = TranslationDeviceHelp,
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
        var translationStrictDeterminismOption = new Option<bool>(
            "--translation-strict-determinism"
        )
        {
            Description =
                "Use one translation slot and disable prompt-cache reuse for maximum same-runtime repeatability",
        };
        var patternOption = new Option<string>("--lr")
        {
            Description = "Pattern names, groups (pii, credentials, browser, registry, wallets), a custom regex, or all",
            DefaultValueFactory = _ => "all",
        };
        var regexFileOption = new Option<string?>("--fr")
        {
            Description = "File containing additional regex patterns",
        };
        var processorOption = new Option<string>("--processor")
        {
            Description =
                $"Native extraction hardware: auto, cpu, gpu, or hybrid. Auto uses CPU below this host's {ProcessingBackendCore.AutoCalibrationThresholdBytes / (1024 * 1024 * 1024)} GiB calibration threshold",
            DefaultValueFactory = _ => "auto",
        };
        var cpuEngineOption = new Option<string>("--cpu-engine")
        {
            Description = "ASCII CPU engine: dotnet, rust, or auto; Rust is parity-checked before use",
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
            Description = "Complete bundle directory; defaults beside bstrings.exe or BSTRINGS_AIRGAP_BUNDLE",
        };
        var airgapOption = new Option<bool>("--airgap")
        {
            Description = "Require a complete offline bundle and block non-loopback worker network access",
        };

        var command = new Command("analyze")
        {
            fileOption,
            directoryOption,
            outputOption,
            maskOption,
            fullOption,
            ocrOption,
            ocrProviderOption,
            ocrThreadsOption,
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
            translationStrictDeterminismOption,
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
            "Run the provenance-preserving workflow and write JSONL evidence, findings.tsv, exact histograms, and an HTML chart.\n\n"
            + "Examples:\n"
            + "  bstrings.exe analyze -d C:\\evidence\\carved --full -o C:\\results\\case-01\n"
            + "  bstrings.exe analyze -f C:\\evidence\\memory.raw --ocr off --translation off --lr all -o C:\\results\\memory\n\n"
            + "The results directory must be new or empty. Failures retain .incomplete and diagnostic logs. "
            + "Long-running stages print measured percentage completion; percentages are work units, not an ETA. "
            + "content-routing.jsonl records every routing signal and decision; engine-status.jsonl records terminal per-input engine coverage; native extraction always covers every input.";

        var actionExitCode = 0;
        command.SetAction(
            async result =>
            {
                try
                {
                    var full = result.GetValue(fullOption);
                    var ocrMode = ResolveOcrMode(result.GetValue(ocrOption), full);
                    var ocrProvider = ResolveOcrProvider(
                        result.GetValue(ocrProviderOption),
                        full
                    );
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
                    var ocrThreads = result.GetValue(ocrThreadsOption);
                    OcrCompletionCore.ValidateRequestedThreads(ocrThreads);

                    var options = new AnalysisOptions(
                        result.GetValue(fileOption),
                        result.GetValue(directoryOption),
                        result.GetValue(maskOption),
                        result.GetValue(outputOption)!,
                        full,
                        ocrMode,
                        ocrProvider,
                        ocrThreads,
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
                        result.GetValue(translationStrictDeterminismOption),
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

    internal static void ValidateTranslationScheduling(
        int parallelism,
        int threads,
        bool strictDeterminism
    )
    {
        if (parallelism < 0 || threads < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parallelism),
                "Translation parallelism and thread counts cannot be negative."
            );
        }
        if (strictDeterminism && parallelism > 1)
        {
            throw new ArgumentException(
                "--translation-strict-determinism cannot be combined with --translation-parallelism above 1."
            );
        }
    }

    internal static OcrWorkflowMode ResolveOcrMode(string? value, bool full)
    {
        var effective = value ?? (full ? "auto" : "off");
        if (TryParseOcrMode(effective, out var mode, out var error))
        {
            return mode;
        }
        throw new ArgumentException(error);
    }

    internal static OcrProvider ResolveOcrProvider(string? value, bool full)
    {
        if (value is null && full)
        {
            return OcrProvider.Auto;
        }
        var effective = value ?? "auto";
        if (TryParseOcrProvider(effective, out var provider, out var error))
        {
            return provider;
        }
        throw new ArgumentException(error);
    }

    private static bool TryParseOcrProvider(
        string value,
        out OcrProvider provider,
        out string? error
    )
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                provider = OcrProvider.Auto;
                error = null;
                return true;
            case "cpu":
                provider = OcrProvider.Cpu;
                error = null;
                return true;
            case "cuda":
                provider = OcrProvider.Cuda;
                error = null;
                return true;
            case "directml":
                provider = OcrProvider.DirectMl;
                error = null;
                return true;
            case "hybrid":
                provider = OcrProvider.Hybrid;
                error = null;
                return true;
            default:
                provider = OcrProvider.Auto;
                error = "OCR provider must be auto, cpu, cuda, directml, or hybrid.";
                return false;
        }
    }

    private static bool TryParseOcrMode(
        string value,
        out OcrWorkflowMode mode,
        out string? error
    )
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
                mode = OcrWorkflowMode.Off;
                error = null;
                return true;
            case "auto":
                mode = OcrWorkflowMode.Auto;
                error = null;
                return true;
            case "force":
                mode = OcrWorkflowMode.Force;
                error = null;
                return true;
            default:
                mode = OcrWorkflowMode.Off;
                error = "OCR must be off, auto, or force.";
                return false;
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
