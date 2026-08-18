#nullable enable

using System;
using System.CommandLine;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace bstrings;

internal readonly record struct AnalysisEngineModes(
    NativeExtractionMode Native,
    ExecutableRecoveryMode Floss,
    OcrWorkflowMode Ocr,
    TranslationWorkflowMode Translation,
    DecoderWorkflowMode Decode = DecoderWorkflowMode.Off
);

internal readonly record struct AnalysisEngineExclusions(
    bool Native,
    bool Floss,
    bool Ocr,
    bool Decode,
    bool Translation
)
{
    internal bool Any => Native || Floss || Ocr || Decode || Translation;
}

internal static class AnalysisCli
{
    internal const string DefaultTranslationPolicy = "high-recall";
    internal const string TranslationPolicyHelp =
        "Automatic translation gate used by --translation auto: high-recall keeps uncertain detections; balanced uses the configured confidence and margin; high-precision applies floors of 0.65 confidence and 0.15 margin";
    internal const string FullProfileHelp =
        "Run the Full analysis preset. It hashes inputs and runs native extraction, routing, FLOSS, OCR, language detection, local Q4_K_M translation, patterns, and reports. Full keeps fail-open shadow translation-worthiness routing. It does not yet remove candidates. Base64 decoding stays off because its performance gate did not pass. An explicit engine selector overrides its default. Use -e or --exclude-engine to subtract engines. Do not set and exclude the same engine";
    internal const string TranslationDeviceHelp =
        "Select auto, cpu, cuda, or hybrid translation hardware. This is separate from native --processor. Auto uses validated compute capability 8.9 for full-offload CUDA p2. Otherwise, it selects CPU before evidence inference. Explicit cuda fails closed";
    internal const int DefaultDecoderMaximumCandidateCharacters = 16_384;
    internal const int DefaultDecoderMaximumBytesPerRecord = 12_288;
    internal const long DefaultDecoderMaximumCandidates = 100_000;
    internal const long DefaultDecoderMaximumTotalBytes = 64L * 1024 * 1024;
    internal const int MaximumDecoderCandidateCharacters =
        EnrichmentRegexPipelineCore.MaxNativeTextCharacters;
    internal const int MaximumDecoderBytesPerRecord =
        MaximumDecoderCandidateCharacters / 4 * 3;
    internal const long MaximumDecoderCandidates = 1_000_000;
    internal const long MaximumDecoderTotalBytes = int.MaxValue;

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
            Description = "New or empty results directory, or an existing incomplete results directory with --resume",
            Required = true,
        };
        var resumeOption = new Option<bool>("--resume", "-r")
        {
            Description = "Resume a validated incomplete analysis from its last committed stage. Version 3.0.0 also accepts -e translation for an exact stopped 2.1.1 Full plan at stage 7; identify that saved source kit with --bundle-root",
        };
        var maskOption = new Option<string?>("--mask")
        {
            Description = "File mask used with -d. Supports * and ?",
        };
        var fullOption = new Option<bool>("--full")
        {
            Description = FullProfileHelp,
        };
        var nativeExtractionOption = new Option<string?>("--native-extraction")
        {
            Description = "Native byte-string extraction: on (default) or off; disabling it requires FLOSS or OCR",
        };
        var excludeEngineOption = new Option<string[]>("--exclude-engine", "-e")
        {
            Description =
                "Subtract native, floss, ocr, decode, or translation from --full. Repeat the option or use a comma-separated value. On resume, only translation can be excluded from the exact supported 2.1.1 stage-7 plan. Cannot be combined with that engine's direct selector; tuning options for an excluded engine are inert but still syntax-checked",
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.OneOrMore,
        };
        excludeEngineOption.Validators.Add(result =>
        {
            if (
                !TryParseEngineExclusions(
                    result.Tokens.Select(token => token.Value).ToArray(),
                    out _,
                    out var error
                )
            )
            {
                result.AddError(error!);
            }
        });
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
        var decoderOption = new Option<string?>("--decode")
        {
            Description = "Bounded Base64 text-child decoding: off (default), auto (strict low-ambiguity), or force (strict broader discovery); decoded content is never executed or translated",
        };
        var decoderMaximumCharactersOption = new Option<int>("--decode-max-characters")
        {
            Description =
                $"Maximum encoded candidate characters, from 8 through {MaximumDecoderCandidateCharacters:N0}",
            DefaultValueFactory = _ => DefaultDecoderMaximumCandidateCharacters,
        };
        var decoderMaximumCandidatesOption = new Option<long>("--decode-max-candidates")
        {
            Description =
                $"Maximum attempted decode candidates, from 1 through {MaximumDecoderCandidates:N0}",
            DefaultValueFactory = _ => DefaultDecoderMaximumCandidates,
        };
        var decoderMaximumBytesPerRecordOption = new Option<int>(
            "--decode-max-bytes-per-record"
        )
        {
            Description =
                $"Maximum decoded bytes per record, from 1 through {MaximumDecoderBytesPerRecord:N0}",
            DefaultValueFactory = _ => DefaultDecoderMaximumBytesPerRecord,
        };
        var decoderMaximumTotalBytesOption = new Option<long>("--decode-max-total-bytes")
        {
            Description =
                $"Maximum total successfully decoded bytes, from 1 through {MaximumDecoderTotalBytes:N0}",
            DefaultValueFactory = _ => DefaultDecoderMaximumTotalBytes,
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
            Description = "Pattern names, groups (pii, credentials, browser, registry, wallets, candidates), a custom regex, or the lower-noise all preset",
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
            resumeOption,
            maskOption,
            fullOption,
            excludeEngineOption,
            nativeExtractionOption,
            ocrOption,
            ocrProviderOption,
            ocrThreadsOption,
            recoveryOption,
            decoderOption,
            decoderMaximumCharactersOption,
            decoderMaximumBytesPerRecordOption,
            decoderMaximumCandidatesOption,
            decoderMaximumTotalBytesOption,
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
            + "  bstrings.exe analyze -f C:\\evidence\\memory.raw --decode auto --translation off --lr all -o C:\\results\\decoded\n"
            + "  bstrings.exe analyze -d C:\\evidence\\carved --full -e ocr,translation -o C:\\results\\without-ai\n"
            + "  bstrings.exe analyze -r -o C:\\results\\case-01\n"
            + "  bstrings.exe analyze -r -o C:\\results\\case-01 -e translation --bundle-root C:\\tools\\bstrings-kit-2.1.1\n"
            + "  bstrings.exe analyze -f C:\\evidence\\memory.raw --ocr off --translation off --lr all -o C:\\results\\memory\n\n"
            + "New analyses require a new or empty results directory. Use -r or --resume for a validated incomplete run. Failures retain .incomplete and diagnostic logs. "
            + "Long-running stages print measured percentage completion; percentages are work units, not an ETA. "
            + "Specialist runs record every routing signal and terminal per-input engine state. Native extraction is on by default and may be explicitly disabled for a specialist-only run.";
        command.Validators.Add(result =>
        {
            if (result.GetValue(resumeOption))
            {
                var allowed = new HashSet<Option>
                {
                    outputOption,
                    resumeOption,
                    bundleRootOption,
                    excludeEngineOption,
                };
                var incompatible = result.Command.Options
                    .Where(option => !allowed.Contains(option))
                    .Where(option => result.GetResult(option) is not null)
                    .Select(option => option.Name)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();
                if (incompatible.Length > 0)
                {
                    result.AddError(
                        "--resume loads the saved analysis contract and cannot be combined with: "
                            + string.Join(
                                ", ",
                                incompatible.Select(name => "--" + name.TrimStart('-'))
                            )
                    );
                }
                var resumeExclusionResult = result.GetResult(excludeEngineOption);
                if (
                    resumeExclusionResult is not null
                    && TryParseEngineExclusions(
                        resumeExclusionResult.Tokens.Select(token => token.Value).ToArray(),
                        out var resumeExclusions,
                        out _
                    )
                    && (
                        !resumeExclusions.Translation
                        || resumeExclusions.Native
                        || resumeExclusions.Floss
                        || resumeExclusions.Ocr
                        || resumeExclusions.Decode
                    )
                )
                {
                    result.AddError(
                        "--resume supports only '-e translation'; no other saved engine can be changed."
                    );
                }
                return;
            }

            var exclusionResult = result.GetResult(excludeEngineOption);
            if (
                exclusionResult is null
                || !TryParseEngineExclusions(
                    exclusionResult.Tokens.Select(token => token.Value).ToArray(),
                    out var exclusions,
                    out _
                )
            )
            {
                return;
            }
            try
            {
                ValidateEngineExclusionUse(
                    result.GetValue(fullOption),
                    exclusions,
                    nativeSelectorSpecified: result.GetResult(nativeExtractionOption) is not null,
                    flossSelectorSpecified: result.GetResult(recoveryOption) is not null,
                    ocrSelectorSpecified: result.GetResult(ocrOption) is not null,
                    decoderSelectorSpecified: result.GetResult(decoderOption) is not null,
                    translationSelectorSpecified: result.GetResult(translationOption) is not null
                );
            }
            catch (ArgumentException ex)
            {
                result.AddError(ex.Message);
            }
        });

        var actionExitCode = 0;
        command.SetAction(
            async result =>
            {
                try
                {
                    if (result.GetValue(resumeOption))
                    {
                        using var resumeCancellation = new CancellationTokenSource();
                        ConsoleCancelEventHandler resumeCancelHandler = (_, eventArgs) =>
                        {
                            eventArgs.Cancel = true;
                            resumeCancellation.Cancel();
                        };
                        Console.CancelKeyPress += resumeCancelHandler;
                        try
                        {
                            var resumeExclusions = ParseEngineExclusions(
                                result.GetValue(excludeEngineOption)
                            );
                            await AnalysisOrchestrator.ResumeAsync(
                                result.GetValue(outputOption)!,
                                result.GetValue(bundleRootOption),
                                resumeExclusions.Translation,
                                resumeCancellation.Token
                            );
                        }
                        finally
                        {
                            Console.CancelKeyPress -= resumeCancelHandler;
                        }
                        return;
                    }

                    var full = result.GetValue(fullOption);
                    var modes = ResolveEngineModes(
                        full,
                        result.GetValue(nativeExtractionOption),
                        result.GetValue(recoveryOption),
                        result.GetValue(ocrOption),
                        result.GetValue(translationOption),
                        result.GetValue(excludeEngineOption),
                        result.GetValue(decoderOption)
                    );
                    var nativeExtractionMode = modes.Native;
                    var ocrMode = modes.Ocr;
                    var ocrProvider = ResolveOcrProvider(
                        result.GetValue(ocrProviderOption),
                        full
                    );
                    var recoveryMode = modes.Floss;
                    var decoderMode = modes.Decode;
                    var translationMode = modes.Translation;
                    string? error;
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
                    var decoderMaximumCandidateCharacters = result.GetValue(
                        decoderMaximumCharactersOption
                    );
                    var decoderMaximumCandidates = result.GetValue(
                        decoderMaximumCandidatesOption
                    );
                    var decoderMaximumBytesPerRecord = result.GetValue(
                        decoderMaximumBytesPerRecordOption
                    );
                    var decoderMaximumTotalBytes = result.GetValue(
                        decoderMaximumTotalBytesOption
                    );
                    ValidateDecoderLimits(
                        decoderMaximumCandidateCharacters,
                        decoderMaximumBytesPerRecord,
                        decoderMaximumCandidates,
                        decoderMaximumTotalBytes
                    );

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
                        result.GetValue(airgapOption),
                        nativeExtractionMode,
                        decoderMode,
                        decoderMaximumCandidateCharacters,
                        decoderMaximumBytesPerRecord,
                        decoderMaximumCandidates,
                        decoderMaximumTotalBytes
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

    internal static AnalysisEngineModes ResolveEngineModes(
        bool full,
        string? nativeValue,
        string? flossValue,
        string? ocrValue,
        string? translationValue,
        IReadOnlyList<string>? rawExclusions,
        string? decoderValue = null
    )
    {
        var exclusions = ParseEngineExclusions(rawExclusions);
        ValidateEngineExclusionUse(
            full,
            exclusions,
            nativeSelectorSpecified: nativeValue is not null,
            flossSelectorSpecified: flossValue is not null,
            ocrSelectorSpecified: ocrValue is not null,
            decoderSelectorSpecified: decoderValue is not null,
            translationSelectorSpecified: translationValue is not null
        );

        var native = exclusions.Native
            ? NativeExtractionMode.Off
            : ResolveNativeExtractionMode(nativeValue);
        var flossText = flossValue ?? (full ? "auto" : "off");
        if (!TryParseRecoveryMode(flossText, out var floss, out var error))
        {
            throw new ArgumentException(error);
        }
        if (exclusions.Floss)
        {
            floss = ExecutableRecoveryMode.Off;
        }
        var ocr = exclusions.Ocr ? OcrWorkflowMode.Off : ResolveOcrMode(ocrValue, full);
        var decoderText = decoderValue ?? "off";
        if (!TryParseDecoderMode(decoderText, out var decoder, out error))
        {
            throw new ArgumentException(error);
        }
        if (exclusions.Decode)
        {
            decoder = DecoderWorkflowMode.Off;
        }
        var translationText = translationValue ?? (full ? "auto" : "off");
        if (!TryParseTranslationMode(translationText, out var translation, out error))
        {
            throw new ArgumentException(error);
        }
        if (exclusions.Translation)
        {
            translation = TranslationWorkflowMode.Off;
        }
        return new AnalysisEngineModes(native, floss, ocr, translation, decoder);
    }

    internal static AnalysisEngineExclusions ParseEngineExclusions(
        IReadOnlyList<string>? rawValues
    )
    {
        if (TryParseEngineExclusions(rawValues, out var exclusions, out var error))
        {
            return exclusions;
        }
        throw new ArgumentException(error);
    }

    private static bool TryParseEngineExclusions(
        IReadOnlyList<string>? rawValues,
        out AnalysisEngineExclusions exclusions,
        out string? error
    )
    {
        byte flags = 0;
        if (rawValues is not null)
        {
            for (var index = 0; index < rawValues.Count; index++)
            {
                if (!TryAddEngineExclusions(rawValues[index], ref flags, out error))
                {
                    exclusions = default;
                    return false;
                }
            }
        }
        exclusions = CreateEngineExclusions(flags);
        error = null;
        return true;
    }

    private static bool TryAddEngineExclusions(
        string rawValue,
        ref byte flags,
        out string? error
    )
    {
        var remaining = rawValue.AsSpan();
        while (true)
        {
            var comma = remaining.IndexOf(',');
            var name = (comma < 0 ? remaining : remaining[..comma]).Trim();
            if (name.Length == 0)
            {
                error =
                    "--exclude-engine contains an empty name; leading, trailing, and repeated commas are not allowed.";
                return false;
            }
            byte flag = 0;
            var ascii = !ContainsNonAscii(name);
            if (ascii && name.Equals("native", StringComparison.OrdinalIgnoreCase))
            {
                flag = 1;
            }
            else if (ascii && name.Equals("floss", StringComparison.OrdinalIgnoreCase))
            {
                flag = 2;
            }
            else if (ascii && name.Equals("ocr", StringComparison.OrdinalIgnoreCase))
            {
                flag = 4;
            }
            else if (ascii && name.Equals("translation", StringComparison.OrdinalIgnoreCase))
            {
                flag = 8;
            }
            else if (ascii && name.Equals("decode", StringComparison.OrdinalIgnoreCase))
            {
                flag = 16;
            }
            if (flag == 0)
            {
                error =
                    $"Unknown excluded engine '{name.ToString()}'. Expected native, floss, ocr, decode, or translation.";
                return false;
            }
            if ((flags & flag) != 0)
            {
                error = $"Engine '{name.ToString()}' is excluded more than once.";
                return false;
            }
            flags |= flag;
            if (comma < 0)
            {
                error = null;
                return true;
            }
            remaining = remaining[(comma + 1)..];
        }
    }

    private static AnalysisEngineExclusions CreateEngineExclusions(byte flags) =>
        new(
            Native: (flags & 1) != 0,
            Floss: (flags & 2) != 0,
            Ocr: (flags & 4) != 0,
            Decode: (flags & 16) != 0,
            Translation: (flags & 8) != 0
        );

    private static bool ContainsNonAscii(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character > 0x7f)
            {
                return true;
            }
        }
        return false;
    }

    private static void ValidateEngineExclusionUse(
        bool full,
        AnalysisEngineExclusions exclusions,
        bool nativeSelectorSpecified,
        bool flossSelectorSpecified,
        bool ocrSelectorSpecified,
        bool decoderSelectorSpecified,
        bool translationSelectorSpecified
    )
    {
        if (!exclusions.Any)
        {
            return;
        }
        if (!full)
        {
            throw new ArgumentException("--exclude-engine requires --full.");
        }
        if (exclusions.Native && nativeSelectorSpecified)
        {
            throw new ArgumentException(
                "--exclude-engine native cannot be combined with --native-extraction."
            );
        }
        if (exclusions.Floss && flossSelectorSpecified)
        {
            throw new ArgumentException(
                "--exclude-engine floss cannot be combined with --recover-executable-strings."
            );
        }
        if (exclusions.Ocr && ocrSelectorSpecified)
        {
            throw new ArgumentException("--exclude-engine ocr cannot be combined with --ocr.");
        }
        if (exclusions.Translation && translationSelectorSpecified)
        {
            throw new ArgumentException(
                "--exclude-engine translation cannot be combined with --translation."
            );
        }
        if (exclusions.Decode && decoderSelectorSpecified)
        {
            throw new ArgumentException(
                "--exclude-engine decode cannot be combined with --decode."
            );
        }
    }

    internal static void ValidateDecoderLimits(
        int maximumCandidateCharacters,
        int maximumBytesPerRecord,
        long maximumCandidates,
        long maximumTotalBytes
    )
    {
        if (maximumCandidateCharacters is < 8 or > MaximumDecoderCandidateCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCandidateCharacters),
                $"Decoder maximum candidate characters must be from 8 through {MaximumDecoderCandidateCharacters:N0}."
            );
        }
        if (maximumBytesPerRecord is < 1 or > MaximumDecoderBytesPerRecord)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumBytesPerRecord),
                $"Decoder maximum bytes per record must be from 1 through {MaximumDecoderBytesPerRecord:N0}."
            );
        }
        if (maximumCandidates is < 1 or > MaximumDecoderCandidates)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCandidates),
                $"Decoder maximum candidates must be from 1 through {MaximumDecoderCandidates:N0}."
            );
        }
        if (maximumTotalBytes is < 1 or > MaximumDecoderTotalBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTotalBytes),
                $"Decoder maximum total bytes must be from 1 through {MaximumDecoderTotalBytes:N0}."
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

    internal static NativeExtractionMode ResolveNativeExtractionMode(string? value)
    {
        switch ((value ?? "on").Trim().ToLowerInvariant())
        {
            case "on":
                return NativeExtractionMode.On;
            case "off":
                return NativeExtractionMode.Off;
            default:
                throw new ArgumentException("Native extraction must be on or off.");
        }
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

    private static bool TryParseDecoderMode(
        string value,
        out DecoderWorkflowMode mode,
        out string? error
    )
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "off":
                mode = DecoderWorkflowMode.Off;
                error = null;
                return true;
            case "auto":
                mode = DecoderWorkflowMode.Auto;
                error = null;
                return true;
            case "force":
                mode = DecoderWorkflowMode.Force;
                error = null;
                return true;
            default:
                mode = DecoderWorkflowMode.Off;
                error = "Decoding must be off, auto, or force.";
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
