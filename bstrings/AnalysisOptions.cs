#nullable enable

namespace bstrings;

internal enum ExecutableRecoveryMode
{
    Off,
    Auto,
    Force,
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
    string PatternSelection,
    string? RegexFilePath,
    string Processor,
    string CpuEngine,
    int MinimumStringLength,
    int MaximumStringLength,
    int TranslationMinimumCharacters,
    int TranslationMaximumCharacters,
    string? BundleRoot,
    bool Airgap
);
