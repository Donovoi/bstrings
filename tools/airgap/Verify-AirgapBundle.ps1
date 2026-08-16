[CmdletBinding()]
param(
    [switch]$TranslationSmoke,
    [switch]$OcrSmoke,
    [ValidateSet('cpu', 'directml', 'hybrid')]
    [string[]]$OcrSmokeProviders = @('cpu', 'directml', 'hybrid'),
    [switch]$SkipExecutableProbes
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $OcrSmoke -and $PSBoundParameters.ContainsKey('OcrSmokeProviders')) {
    throw '-OcrSmokeProviders requires -OcrSmoke.'
}
$incompleteMarker = Join-Path $PSScriptRoot '.incomplete'
if ($null -ne (Get-Item -LiteralPath $incompleteMarker -Force -ErrorAction SilentlyContinue)) {
    throw 'The air-gap bundle has a lingering root .incomplete marker.'
}
. (Join-Path $PSScriptRoot 'Set-AirgapEnvironment.ps1')
Enable-BstringsAirgapEnvironment -BundleRoot $PSScriptRoot

$configPath = Join-Path $PSScriptRoot 'airgap-config.json'
$manifestPath = Join-Path $PSScriptRoot 'airgap-manifest.json'
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.schemaVersion -ne 2) {
    throw "Unsupported air-gap configuration schema: $($config.schemaVersion)"
}
function Resolve-BundlePath([string]$RelativePath) {
    $candidate = [IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot ($RelativePath -replace '/', '\'))
    )
    $root = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $candidate.StartsWith(
        $root + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Bundle configuration escapes its root: $RelativePath"
    }
    return $candidate
}

$python = Resolve-BundlePath $config.pythonExecutable
$manifestTool = Resolve-BundlePath $config.manifestTool
$bstrings = Resolve-BundlePath $config.bstringsExecutable
& $bstrings bundle verify --bundle-root $PSScriptRoot
if ($LASTEXITCODE -ne 0) {
    throw 'Native bstrings bundle verification failed.'
}
& $python $manifestTool verify --root $PSScriptRoot --manifest $manifestPath
if ($LASTEXITCODE -ne 0) {
    throw 'Air-gap bundle manifest verification failed.'
}
$markdownVerifier = Resolve-BundlePath 'tools/airgap/Verify-MarkdownLinks.ps1'
& $markdownVerifier -BundleDirectory $PSScriptRoot

$requiredNoticeFiles = @(
    'LICENSE.md',
    'README.md',
    'THIRD_PARTY_NOTICES.md',
    'licenses/bstrings-core-win-x64.tsv',
    'licenses/bstrings-managed-win-x64.json',
    'licenses/Apache-2.0.txt',
    'licenses/MIT.txt',
    'licenses/Unicode-3.0.txt',
    'licenses/DeviceIOControlLib-0.1.6-LICENSE.txt',
    'licenses/ILGPU-1.5.3-LICENSE.txt',
    'licenses/ILGPU-1.5.3-LICENSE-3RD-PARTY.txt',
    'licenses/dotnet-runtime-win-x64-10.0.10-LICENSE.TXT',
    'licenses/dotnet-runtime-win-x64-10.0.10-THIRD-PARTY-NOTICES.TXT',
    'licenses/Rust-1.95.0-COPYRIGHT-library.html',
    'licenses/magika-cli-1.1.0-redistribution.json',
    'tools/airgap/Verify-MarkdownLinks.ps1',
    'tools/licenses/Verify-MagikaRedistribution.ps1',
    'licenses/floss-v3.1.1-win-x64.json',
    'tools/licenses/Verify-FlossThirdPartyNotices.ps1',
    'licenses/Magika-Apache-2.0.txt',
    'licenses/FLOSS-Apache-2.0.txt',
    'licenses/llama.cpp-MIT.txt',
    'licenses/llama.cpp/LICENSE-llama.cpp.txt',
    'licenses/llama.cpp/LICENSE-jsonhpp.txt',
    'licenses/llama.cpp/LICENSE-cpp-httplib.txt',
    'licenses/llama.cpp/SOURCE-nlohmann-json.hpp',
    'licenses/llama.cpp/SOURCE-base64.hpp',
    'licenses/llama.cpp/SOURCE-miniaudio.h',
    'licenses/llama.cpp/SOURCE-stb_image.h',
    'licenses/llama.cpp/SOURCE-llamafile-sgemm.cpp',
    'licenses/llama.cpp/SOURCE-ggml-cpu-ops.cpp',
    'licenses/llama.cpp/SOURCE-llama-vocab.cpp',
    'licenses/llama.cpp/NOTICE-SCOPE.md',
    'licenses/NVIDIA-CUDA-12.4-EULA.pdf',
    'licenses/Hy-MT2-Apache-2.0.txt',
    'licenses/Hy-MT2-7B-Apache-2.0.txt',
    'ocr-components.lock.json',
    'licenses/ocr-runtime-win-x64.json',
    'licenses/ocr-runtime-files.json',
    'licenses/ocr-runtime/RapidOCR-3.9.2-LICENSE.txt',
    'licenses/ocr-runtime/ANTLR4-4.9.3-LICENSE.txt',
    'licenses/ocr-runtime/FlatBuffers-25.12.19-LICENSE.txt',
    'licenses/ocr-runtime/PaddleOCR-LICENSE.txt',
    'tools/airgap/Verify-OcrRuntime.ps1'
)
foreach ($relativePath in $requiredNoticeFiles) {
    $noticeFile = Resolve-BundlePath $relativePath
    if (-not [IO.File]::Exists($noticeFile)) {
        throw "Required bstrings third-party notice is missing: $relativePath"
    }
}
$offlineLockPath = Resolve-BundlePath $config.componentLock
$offlineLock = Get-Content -LiteralPath $offlineLockPath -Raw | ConvertFrom-Json
if (
    [int]$offlineLock.schemaVersion -ne 2 -or
    [string]$offlineLock.profile -cne 'windows-x64-offline-v3' -or
    [string]$config.bundleProfile -cne 'windows-x64-offline-v3'
) {
    throw 'The bundle does not use the exact one-kit offline contract.'
}
if ($config.PSObject.Properties.Name -ccontains 'translationProfile') {
    throw 'The bundle configuration must not contain a translation-profile selector.'
}
$translationModel = $offlineLock.components.translationModel
$translationLicense = Resolve-BundlePath 'licenses/Hy-MT2-Apache-2.0.txt'
if (
    $null -eq $translationModel -or
    (Get-Item -LiteralPath $translationLicense).Length -ne [long]$translationModel.license.bytes -or
    (Get-FileHash -LiteralPath $translationLicense -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        [string]$translationModel.license.sha256
) {
    throw 'The canonical Hy-MT2 license does not match the locked translation model.'
}

if (-not ($config.PSObject.Properties.Name -contains 'magikaRedistribution')) {
    throw 'Bundle configuration is missing the Magika redistribution closure.'
}
$magikaRedistribution = $config.magikaRedistribution
$magikaVerifier = Resolve-BundlePath $magikaRedistribution.verifier
$magikaInventory = Resolve-BundlePath $magikaRedistribution.inventory
$magikaVerifyResult = @(
    & $magikaVerifier `
        -BundleDirectory $PSScriptRoot `
        -InventoryPath $magikaInventory
)
if (
    $magikaVerifyResult.Count -ne 1 -or
    [long]$magikaVerifyResult[0].files -ne [long]$magikaRedistribution.stagedFiles -or
    [long]$magikaVerifyResult[0].totalOwnedBytes -ne [long]$magikaRedistribution.stagedBytes
) {
    throw 'Bundled Magika runtime, dependency inventory, notices, or corresponding sources did not verify.'
}
$null = Resolve-BundlePath $magikaRedistribution.runtimeDependency

if (-not ($config.PSObject.Properties.Name -contains 'flossRedistribution')) {
    throw 'Bundle configuration is missing the FLOSS redistribution closure.'
}
$flossRedistribution = $config.flossRedistribution
$flossVerifier = Resolve-BundlePath $flossRedistribution.verifier
$flossInventory = Resolve-BundlePath $flossRedistribution.inventory
$flossAssetDirectory = Resolve-BundlePath 'licenses/floss-v3.1.1'
$flossNoticeFiles = @(
    Get-Item -LiteralPath $flossInventory -Force
) + @(
    Get-ChildItem -LiteralPath $flossAssetDirectory -Recurse -File -Force
)
$flossNoticeMeasure = $flossNoticeFiles | Measure-Object -Property Length -Sum
if (
    [long]$flossNoticeMeasure.Count -ne [long]$flossRedistribution.stagedFiles -or
    [long]$flossNoticeMeasure.Sum -ne [long]$flossRedistribution.stagedBytes
) {
    throw 'Bundled FLOSS redistribution overlay does not match its locked file count and size.'
}
& $flossVerifier `
    -FlossExecutable (Resolve-BundlePath $config.flossExecutable) `
    -InventoryPath $flossInventory `
    -StagedDirectory $PSScriptRoot

$guardProbe = Resolve-BundlePath $config.networkGuardProbe
& $python $guardProbe
if ($LASTEXITCODE -ne 0) {
    throw 'Python air-gap network guard verification failed.'
}
$ocrVerifier = Resolve-BundlePath 'tools/airgap/Verify-OcrRuntime.ps1'
if ($OcrSmoke) {
    & $ocrVerifier `
        -BundleDirectory $PSScriptRoot `
        -Smoke `
        -SmokeProviders $OcrSmokeProviders
}
else {
    & $ocrVerifier -BundleDirectory $PSScriptRoot
}

if (-not ($config.PSObject.Properties.Name -contains 'llamaCppRuntime')) {
    throw 'Bundle configuration is missing custom llama.cpp source-build provenance.'
}
$llamaConfig = $config.llamaCppRuntime
$llamaProvenancePath = Resolve-BundlePath $llamaConfig.provenance
$llamaProvenance = Get-Content -LiteralPath $llamaProvenancePath -Raw | ConvertFrom-Json
if (
    $llamaConfig.version -ne 'b10248' -or
    [string]$llamaConfig.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    $llamaConfig.openMp -ne $false -or
    $llamaConfig.offlineArgument -ne '--offline' -or
    $llamaProvenance.schemaVersion -ne 1 -or
    $llamaProvenance.component -ne 'llama.cpp' -or
    $llamaProvenance.version -ne $llamaConfig.version -or
    $llamaProvenance.source.tag -ne $llamaConfig.sourceTag -or
    $llamaProvenance.source.commit -ne $llamaConfig.sourceCommit -or
    $llamaProvenance.source.archiveSha256 -ne $llamaConfig.sourceArchiveSha256 -or
    $llamaProvenance.build.compiler.id -ne 'MSVC' -or
    $llamaProvenance.build.networkGuarded -ne $true -or
    'GGML_OPENMP=OFF' -notin @($llamaProvenance.build.flags) -or
    'LLAMA_USE_PREBUILT_UI=OFF' -notin @($llamaProvenance.build.flags) -or
    'LLAMA_SUBPROCESS=OFF' -notin @($llamaProvenance.build.flags) -or
    'MTMD_VIDEO=OFF' -notin @($llamaProvenance.build.flags)
) {
    throw 'Custom llama.cpp runtime provenance is missing, inconsistent, or not offline-safe.'
}
$llamaDirectory = Split-Path -Parent (Resolve-BundlePath $config.llamaServer)
$forbiddenLlamaFiles = @(
    Get-ChildItem -LiteralPath $llamaDirectory -Recurse -File -Force | Where-Object {
        $_.Name -like 'libomp140*.dll' -or
        $_.FullName -match '(?i)(^|[\\/])debug_nonredist([\\/]|$)'
    }
)
if ($forbiddenLlamaFiles.Count -ne 0) {
    throw "Bundle contains a forbidden llama.cpp OpenMP/debug artifact: $($forbiddenLlamaFiles.FullName -join ', ')"
}
if (-not ($config.PSObject.Properties.Name -contains 'llamaCudaOverlay')) {
    throw 'Bundle configuration is missing the authenticated llama.cpp CUDA overlay.'
}
$cudaLock = $offlineLock.llamaCudaOverlay
$cudaConfig = $config.llamaCudaOverlay
if (
    $null -eq $cudaLock -or
    [string]$cudaLock.sourceTag -ne [string]$llamaConfig.sourceTag -or
    [string]$cudaLock.sourceCommit -ne [string]$llamaConfig.sourceCommit -or
    [string]$cudaConfig.version -ne [string]$cudaLock.version -or
    [string]$cudaConfig.sourceTag -ne [string]$cudaLock.sourceTag -or
    [string]$cudaConfig.sourceCommit -ne [string]$cudaLock.sourceCommit -or
    [string]$cudaConfig.platform -ne [string]$cudaLock.platform -or
    [string]$cudaConfig.acceptanceComputeCapability -ne
        [string]$cudaLock.acceptanceHardware.computeCapability -or
    $cudaConfig.cpuFallback -ne $true
) {
    throw 'Bundle CUDA configuration does not match its reviewed lock or CPU runtime.'
}
$cudaProvenancePath = Resolve-BundlePath $cudaConfig.provenance
$cudaProvenance = Get-Content -LiteralPath $cudaProvenancePath -Raw | ConvertFrom-Json
if (
    $cudaProvenance.schemaVersion -ne 1 -or
    [string]$cudaProvenance.component -cne 'llama.cpp-cuda-overlay' -or
    [string]$cudaProvenance.version -ne [string]$cudaLock.version -or
    [string]$cudaProvenance.sourceTag -ne [string]$cudaLock.sourceTag -or
    [string]$cudaProvenance.sourceCommit -ne [string]$cudaLock.sourceCommit -or
    $cudaProvenance.cpuFallback.sourceBuiltRuntimeRetained -ne $true
) {
    throw 'Bundled llama.cpp CUDA overlay provenance is missing or inconsistent.'
}
$lockedCudaRuntimePaths = @($cudaLock.runtimeFiles | ForEach-Object {
    "runtime/llama/$($_.path)"
})
if (
    (@($cudaConfig.runtimeFiles | Sort-Object) -join '|') -cne
    (@($lockedCudaRuntimePaths | Sort-Object) -join '|')
) {
    throw 'Bundle CUDA configuration does not name the exact reviewed runtime closure.'
}
foreach ($runtimeFile in @($cudaLock.runtimeFiles)) {
    $runtimePath = Resolve-BundlePath "runtime/llama/$($runtimeFile.path)"
    if (
        (Get-Item -LiteralPath $runtimePath).Length -ne [long]$runtimeFile.bytes -or
        (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne
            [string]$runtimeFile.sha256
    ) {
        throw "Bundled CUDA runtime does not match its lock: $($runtimeFile.path)"
    }
    $provenanceRows = @($cudaProvenance.runtimeFiles | Where-Object {
        [string]$_.path -ceq [string]$runtimeFile.path
    })
    $lockedImports = @($runtimeFile.imports | ForEach-Object {
        ([string]$_).ToLowerInvariant()
    } | Sort-Object)
    $recordedImports = if ($provenanceRows.Count -eq 1) {
        @($provenanceRows[0].imports | ForEach-Object {
            ([string]$_).ToLowerInvariant()
        } | Sort-Object)
    }
    else { @() }
    if (
        $provenanceRows.Count -ne 1 -or
        [string]$provenanceRows[0].archive -ne [string]$runtimeFile.archive -or
        [string]$provenanceRows[0].entry -ne [string]$runtimeFile.entry -or
        [long]$provenanceRows[0].bytes -ne [long]$runtimeFile.bytes -or
        [string]$provenanceRows[0].sha256 -ne [string]$runtimeFile.sha256 -or
        ($recordedImports -join '|') -cne ($lockedImports -join '|')
    ) {
        throw "Bundled CUDA runtime provenance differs from its lock: $($runtimeFile.path)"
    }
}
$cudaLicense = Resolve-BundlePath $cudaConfig.license
if (
    (Get-Item -LiteralPath $cudaLicense).Length -ne [long]$cudaLock.license.bytes -or
    (Get-FileHash -LiteralPath $cudaLicense -Algorithm SHA256).Hash.ToLowerInvariant() -ne
        [string]$cudaLock.license.sha256
) {
    throw 'Bundled NVIDIA CUDA 12.4 EULA does not match its lock.'
}
$expectedLlamaPeNames = @(
    $llamaProvenance.runtimeFiles | ForEach-Object { [string]$_.name }
) + @(
    $offlineLock.runtimeDlls | ForEach-Object { [string]$_ }
) + @(
    $cudaLock.runtimeFiles | ForEach-Object { [string]$_.path }
)
$actualLlamaPeNames = @(
    Get-ChildItem -LiteralPath $llamaDirectory -File | Where-Object {
        $_.Extension -in @('.exe', '.dll')
    } | ForEach-Object { $_.Name }
)
if (
    (@($expectedLlamaPeNames | Sort-Object) -join '|') -cne
    (@($actualLlamaPeNames | Sort-Object) -join '|')
) {
    throw 'Bundled llama.cpp directory contains a missing or unreviewed PE runtime file.'
}

if (-not $SkipExecutableProbes) {
    $probes = @(
        @($bstrings, '--version'),
        @((Resolve-BundlePath $config.magikaExecutable), '--version'),
        @((Resolve-BundlePath $config.flossExecutable), '--version'),
        @((Resolve-BundlePath $config.llamaServer), '--version')
    )
    foreach ($probe in $probes) {
        & $probe[0] $probe[1] | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Bundled executable probe failed: $($probe[0])"
        }
    }
    $llamaServer = Resolve-BundlePath $config.llamaServer
    $llamaHelp = (& $llamaServer --help 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $llamaHelp -notmatch '(?m)^--offline\s+') {
        throw 'Bundled llama.cpp runtime does not expose the required --offline guard.'
    }
    $llamaVersion = (& $llamaServer --version 2>&1 | Out-String)
    if (
        $LASTEXITCODE -ne 0 -or
        $llamaVersion.IndexOf([string]$llamaConfig.sourceCommit, [StringComparison]::Ordinal) -lt 0
    ) {
        throw 'Bundled llama.cpp runtime version does not match its pinned source commit.'
    }
    $deviceProbe = (& $llamaServer --list-devices 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Bundled llama.cpp CUDA/CPU device probe failed: $deviceProbe"
    }
    & $bstrings analyze --help | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'The bundled bstrings executable does not expose the integrated analyze command.'
    }
}

if ($TranslationSmoke) {
    $smokeRoot = Join-Path (
        [IO.Path]::GetTempPath()
    ) ("bstrings-airgap-smoke-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($smokeRoot) | Out-Null
    try {
        $inputPath = Resolve-BundlePath $config.smokeEvidence
        $resultsPath = Join-Path $smokeRoot 'results'
        $analysisArguments = @(
            'analyze'
            '-f', $inputPath
            '--full'
            '--bundle-root', $PSScriptRoot
            '--airgap'
            '--language-detection', 'accurate'
            '--translation-policy', 'high-recall'
            '--translation-target', 'en'
            '--translation-device', 'cpu'
            '--translation-parallelism', '1'
            '--translation-threads', '1'
            '--processor', 'cpu'
            '--cpu-engine', 'rust'
            '--maximum-length', '4096'
            '--translation-max-characters', '512'
            '--lr', 'email'
            '-o', $resultsPath
        )
        $quotedArguments = @($analysisArguments | ForEach-Object {
            $value = [string]$_
            if ($value.IndexOfAny([char[]]@(' ', "`t", '"')) -ge 0) {
                '"' + $value.Replace('"', '\"') + '"'
            }
            else {
                $value
            }
        })
        $smokeStdout = Join-Path $smokeRoot 'interrupted.stdout.log'
        $smokeStderr = Join-Path $smokeRoot 'interrupted.stderr.log'
        $analysisProcess = Start-Process `
            -FilePath $bstrings `
            -ArgumentList $quotedArguments `
            -RedirectStandardOutput $smokeStdout `
            -RedirectStandardError $smokeStderr `
            -PassThru `
            -NoNewWindow
        try {
            $selectionCheckpoint = Join-Path `
                $resultsPath `
                '.bstrings-resume\checkpoints\0007-translation-selection.json'
            $deadline = [DateTimeOffset]::UtcNow.AddMinutes(10)
            while (-not [IO.File]::Exists($selectionCheckpoint)) {
                $analysisProcess.Refresh()
                if ($analysisProcess.HasExited) {
                    throw 'Bundled Full smoke completed before its resume checkpoint could be interrupted.'
                }
                if ([DateTimeOffset]::UtcNow -ge $deadline) {
                    throw 'Timed out waiting for the bundled Full resume checkpoint.'
                }
                Start-Sleep -Milliseconds 5
            }
            & taskkill.exe /PID $analysisProcess.Id /T /F | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to stop the bundled Full smoke process: $LASTEXITCODE"
            }
            $analysisProcess.WaitForExit()
        }
        finally {
            $analysisProcess.Refresh()
            if (-not $analysisProcess.HasExited) {
                & taskkill.exe /PID $analysisProcess.Id /T /F 2>$null | Out-Null
                $analysisProcess.WaitForExit()
            }
            $analysisProcess.Dispose()
        }
        if (
            -not [IO.File]::Exists((Join-Path $resultsPath '.incomplete')) -or
            [IO.File]::Exists((Join-Path $resultsPath 'summary.json'))
        ) {
            throw 'Interrupted bundled Full smoke did not retain an incomplete resumable boundary.'
        }

        & $bstrings analyze -r -o $resultsPath --bundle-root $PSScriptRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Bundled bstrings.exe analyze resume smoke failed with exit code $LASTEXITCODE."
        }

        $run = Get-Content -LiteralPath (Join-Path $resultsPath 'run.json') -Raw |
            ConvertFrom-Json
        $summary = Get-Content -LiteralPath (Join-Path $resultsPath 'summary.json') -Raw |
            ConvertFrom-Json
        if ($run.status -ne 'complete' -or $summary.status -ne 'complete') {
            throw 'Integrated smoke did not produce complete run and summary records.'
        }
        $expectedReusedStages = @(
            'input-inventory'
            'content-routing'
            'native-extraction'
            'floss-recovery'
            'ocr'
            'raw-merge'
            'translation-selection'
        )
        $actualReusedStages = @($run.resume.reusedStages | ForEach-Object { [string]$_ })
        if (
            [string]$run.resume.attemptMode -cne 'resume' -or
            ($actualReusedStages -join "`n") -cne ($expectedReusedStages -join "`n")
        ) {
            throw 'Integrated smoke did not resume the exact committed Full-analysis stage prefix.'
        }
        if ($run.preservationFallbacks -ne 0 -or $summary.preservationFallbacks -ne 0) {
            throw 'Integrated smoke unexpectedly required a translation preservation fallback.'
        }
        if ($run.input.fileCount -ne 1 -or $summary.inputFiles -ne 1) {
            throw 'Integrated smoke did not freeze exactly one evidence input.'
        }

        $markers = @(Get-ChildItem -LiteralPath $resultsPath -Recurse -Force | Where-Object {
            $_.Name -eq '.incomplete' -or $_.Name.EndsWith('.incomplete', [StringComparison]::Ordinal)
        })
        if ($markers.Count -ne 0) {
            throw "Integrated smoke left incomplete markers: $($markers.FullName -join ', ')"
        }

        $inputManifestPath = Join-Path $resultsPath $summary.inputIdentity.manifest
        $manifestHash = (Get-FileHash -LiteralPath $inputManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if (
            $summary.inputIdentity.contentHashAlgorithm -ne 'sha256' -or
            $run.input.contentHashAlgorithm -ne 'sha256' -or
            $manifestHash -ne $summary.inputIdentity.manifestSha256 -or
            $manifestHash -ne $run.input.manifestSha256
        ) {
            throw 'Integrated smoke manifest identity does not match run.json and summary.json.'
        }
        $inputManifest = @(Get-Content -LiteralPath $inputManifestPath | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        } | ForEach-Object {
            $_ | ConvertFrom-Json
        })
        if ($inputManifest.Count -ne 1) {
            throw "Integrated smoke expected one input manifest row, found $($inputManifest.Count)."
        }
        $evidenceHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $evidenceLength = (Get-Item -LiteralPath $inputPath).Length
        if (
            -not [string]::Equals(
                [IO.Path]::GetFullPath($inputPath),
                [string]$inputManifest[0].path,
                [StringComparison]::OrdinalIgnoreCase
            ) -or
            $inputManifest[0].length -ne $evidenceLength -or
            $inputManifest[0].sha256 -ne $evidenceHash
        ) {
            throw 'Integrated smoke input manifest does not identify the exact synthetic evidence.'
        }

        $candidateLines = @(Get-Content -LiteralPath (Join-Path $resultsPath 'translation-candidates.jsonl') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        $translations = @(Get-Content -LiteralPath (Join-Path $resultsPath 'translated-strings.jsonl') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
                $_ | ConvertFrom-Json
            })
        $assessments = @(Get-Content -LiteralPath (Join-Path $resultsPath 'language-assessments.jsonl') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if (
            $candidateLines.Count -lt 1 -or
            $translations.Count -ne $candidateLines.Count -or
            $summary.translationCandidates -ne $candidateLines.Count -or
            $summary.translatedStrings -ne $translations.Count -or
            $assessments.Count -lt 1
        ) {
            throw 'Integrated smoke did not assess and translate exactly one child per candidate.'
        }
        foreach ($translation in $translations) {
            if (
                [string]::IsNullOrWhiteSpace($translation.parentRecordId) -or
                $translation.transform.kind -ne 'translation' -or
                $translation.transform.engine -ne 'llama.cpp' -or
                $translation.transform.execution.airgap -ne $true -or
                $translation.transform.execution.device -ne 'cpu' -or
                $translation.attributes.translationIntegrity -notin @(
                    'verified',
                    'source-retained-ambiguous',
                    'preservation-fallback'
                )
            ) {
                throw 'Integrated smoke translation is missing verified parent, CPU, air-gap, or integrity provenance.'
            }
        }
        $ambiguousTranslation = @($translations | Where-Object {
            $_.attributes.translationIntegrity -eq 'source-retained-ambiguous' -and
            $_.attributes.translationAmbiguousIdentifierCount -ge 1
        })
        if ($ambiguousTranslation.Count -lt 1) {
            throw 'The offline translation smoke did not expose its ordinary hyphenated term as advisory.'
        }
        $retainedIdentifier = @($translations | Where-Object {
            $_.text -match [regex]::Escape('analyst@example.com')
        })
        if ($retainedIdentifier.Count -lt 1) {
            throw 'The offline translation smoke did not retain its evidence identifier.'
        }

        $matches = @(Get-Content -LiteralPath (Join-Path $resultsPath 'regex-matches.jsonl') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
                $_ | ConvertFrom-Json
            } | Where-Object {
                $_.match -eq 'analyst@example.com'
            })
        if ($matches.Count -lt 1 -or $summary.regexMatches -lt $matches.Count) {
            throw 'Integrated smoke did not retain and match the synthetic evidence email.'
        }

        $findingsHeader = Get-Content -LiteralPath (Join-Path $resultsPath 'findings.tsv') -TotalCount 1
        $expectedFindingsHeader = @(
            'PatternName',
            'Match',
            'Context',
            'SourceFile',
            'ArtifactType',
            'Location',
            'MatchStart',
            'AttributesJson'
        ) -join "`t"
        if ($findingsHeader -cne $expectedFindingsHeader) {
            throw 'Integrated smoke findings report does not use the compact reviewed schema.'
        }
        $findingRows = @(Import-Csv -LiteralPath (Join-Path $resultsPath 'findings.tsv') -Delimiter "`t")
        $integrityRows = @($findingRows | Where-Object {
            $_.AttributesJson -match '"translationIntegrity":"[^" ]+"'
        })
        if ($integrityRows.Count -lt 1) {
            throw 'Integrated smoke findings report did not retain translation integrity in AttributesJson.'
        }
        $translationLog = Get-Content -LiteralPath (
            Join-Path $resultsPath 'logs\translation.stderr.log'
        ) -Raw
        if (
            $translationLog -notmatch 'Progress: offline translation: 0[.]0%' -or
            $translationLog -notmatch 'Progress: offline translation: 100[.]0%' -or
            $translationLog -notmatch 'eta=' -or
            $translationLog -notmatch 'Translation summary: .*cacheHits=.*modelInputs=.*fallbacks='
        ) {
            throw 'Integrated smoke translation log is missing percentage, ETA, or cache/integrity totals.'
        }

        if (-not ($config.PSObject.Properties.Name -contains 'smokeRecoveryFixture')) {
            throw 'Bundle configuration is missing smokeRecoveryFixture.'
        }
        $recoveryInput = Resolve-BundlePath $config.smokeRecoveryFixture
        $expectedFixtureHash = (
            Get-Content -LiteralPath (Resolve-BundlePath 'tools/airgap/fixtures/floss-recovery-smoke.sha256') -Raw
        ).Split([char[]]" `t`r`n", [StringSplitOptions]::RemoveEmptyEntries)[0].ToLowerInvariant()
        $actualFixtureHash = (Get-FileHash -LiteralPath $recoveryInput -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualFixtureHash -ne $expectedFixtureHash) {
            throw 'The synthetic FLOSS recovery fixture does not match its reviewed SHA-256.'
        }

        $recoveryResults = Join-Path $smokeRoot 'recovery-results'
        & $bstrings analyze `
            -f $recoveryInput `
            --recover-executable-strings force `
            --translation off `
            --bundle-root $PSScriptRoot `
            --airgap `
            --processor cpu `
            --cpu-engine rust `
            --maximum-length 4096 `
            --lr all `
            -o $recoveryResults
        if ($LASTEXITCODE -ne 0) {
            throw "Bundled FLOSS recovery smoke failed with exit code $LASTEXITCODE."
        }

        $recoverySummary = Get-Content -LiteralPath (Join-Path $recoveryResults 'summary.json') -Raw |
            ConvertFrom-Json
        if (
            $recoverySummary.status -ne 'complete' -or
            $recoverySummary.inputFiles -ne 1 -or
            $recoverySummary.recoveredStrings -lt 1
        ) {
            throw 'Integrated recovery smoke did not complete with at least one FLOSS-derived string.'
        }
        $recovered = @(Get-Content -LiteralPath (Join-Path $recoveryResults 'recovered-strings.jsonl') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object {
                $_ | ConvertFrom-Json
            } | Where-Object {
                $_.text -eq 'FLOSS_RECOVERY_OK' -and
                $_.origin.extractor -eq 'floss' -and
                $_.origin.kind -eq 'decoded' -and
                $_.location.kind -eq 'virtual_address'
            })
        if ($recovered.Count -ne 1) {
            throw "Integrated recovery smoke expected one attributable decoded marker, found $($recovered.Count)."
        }
    }
    finally {
        $resolvedSmoke = [IO.Path]::GetFullPath($smokeRoot)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (
            $resolvedSmoke.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
            [IO.Path]::GetFileName($resolvedSmoke).StartsWith(
                'bstrings-airgap-smoke-',
                [StringComparison]::Ordinal
            ) -and
            [IO.Directory]::Exists($resolvedSmoke)
        ) {
            [IO.Directory]::Delete($resolvedSmoke, $true)
        }
    }
}

Write-Host 'Air-gap bundle verification passed.'
