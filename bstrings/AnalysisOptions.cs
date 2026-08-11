#nullable enable

using System.Text.Json.Serialization;

namespace bstrings;

internal enum ExecutableRecoveryMode
{
    Off,
    Auto,
    Force,
}

internal enum OcrWorkflowMode
{
    Off,
    Auto,
    Force,
}

internal enum NativeExtractionMode
{
    On,
    Off,
}

internal enum OcrProvider
{
    [JsonStringEnumMemberName("auto")]
    Auto,
    [JsonStringEnumMemberName("cpu")]
    Cpu,
    [JsonStringEnumMemberName("cuda")]
    Cuda,
    [JsonStringEnumMemberName("directml")]
    DirectMl,
    [JsonStringEnumMemberName("hybrid")]
    Hybrid,
}

internal enum TranslationWorkflowMode
{
    Off,
    Auto,
    All,
    DetectOnly,
}

internal sealed record AnalysisOptions(
    string? FilePath,
    string? DirectoryPath,
    string? Mask,
    string OutputDirectory,
    bool Full,
    OcrWorkflowMode OcrMode,
    OcrProvider OcrProvider,
    int OcrThreads,
    ExecutableRecoveryMode RecoveryMode,
    TranslationWorkflowMode TranslationMode,
    LanguageDetectionMode LanguageDetectionMode,
    LanguageTriagePolicy TranslationPolicy,
    double LanguageConfidence,
    double LanguageMargin,
    string TranslationTarget,
    string TranslationDevice,
    int TranslationParallelism,
    int TranslationThreads,
    int TranslationGpuLayers,
    bool TranslationStrictDeterminism,
    string PatternSelection,
    string? RegexFilePath,
    string Processor,
    string CpuEngine,
    int MinimumStringLength,
    int MaximumStringLength,
    int TranslationMinimumCharacters,
    int TranslationMaximumCharacters,
    string? BundleRoot,
    bool Airgap,
    NativeExtractionMode NativeExtractionMode = NativeExtractionMode.On
);
