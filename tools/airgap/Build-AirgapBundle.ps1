[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PublishedBstringsDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PythonDirectory,
    [Parameter(Mandatory = $true)]
    [string]$MagikaDirectory,
    [Parameter(Mandatory = $true)]
    [string]$MagikaRedistributionDirectory,
    [Parameter(Mandatory = $true)]
    [string]$FlossDirectory,
    [Parameter(Mandatory = $true)]
    [string]$FlossRedistributionDirectory,
    [Parameter(Mandatory = $true)]
    [string]$LlamaDirectory,
    [Parameter(Mandatory = $true)]
    [string]$TranslationModelDirectory,
    [Parameter(Mandatory = $true)]
    [string]$VisualCppRuntimeDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OcrComponentsDirectory,
    [Parameter(Mandatory = $true)]
    [string]$TranslationModelRevision,
    [ValidateSet('cpu', 'directml', 'hybrid')]
    [string[]]$OcrSelfTestProviders = @('cpu'),
    [string]$TranslationModelId = 'tencent/Hy-MT2-7B-GGUF',
    [string]$BstringsExecutable = 'bstrings.exe',
    [string]$PythonExecutable = 'python.exe',
    [string]$MagikaExecutable = 'magika.exe',
    [string]$FlossExecutable = 'floss.exe',
    [string]$LlamaServerExecutable = 'llama-server.exe',
    [string]$TranslationModel = 'Hy-MT2-7B-Q4_K_M.gguf',
    [string]$ComponentLockPath,
    [string]$RapidsPythonDirectory,
    [string]$RapidsPythonExecutable = 'python.exe',
    [string]$MadladModelDirectory,
    [string]$MadladModelId = 'google/madlad400-3b-mt',
    [string]$MadladModelRevision = 'fa184c675da0b5c9e1c8694fccd4e12e2d422094',
    [string]$MadladWeights = 'model.safetensors',
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$resolvedOcrSelfTestProviders = @(
    $OcrSelfTestProviders | ForEach-Object { $_.ToLowerInvariant() }
)
if (
    $resolvedOcrSelfTestProviders.Count -lt 1 -or
    @($resolvedOcrSelfTestProviders | Sort-Object -Unique).Count -ne
        $resolvedOcrSelfTestProviders.Count -or
    'cpu' -notin $resolvedOcrSelfTestProviders
) {
    throw 'OcrSelfTestProviders must contain cpu and may add each hardware provider once.'
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$defaultComponentLock = Join-Path $PSScriptRoot 'offline-components.lock.json'
if ([string]::IsNullOrWhiteSpace($ComponentLockPath)) {
    $ComponentLockPath = $defaultComponentLock
}

function Resolve-RequiredDirectory([string]$Value, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Value -PathType Container)) {
        throw "$Name was not found or is not a directory: $Value"
    }
    return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Value).Path)
}

function Resolve-ChildFile([string]$Directory, [string]$RelativePath, [string]$Name) {
    if ([IO.Path]::IsPathRooted($RelativePath) -or $RelativePath -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "$Name must be a safe path relative to its supplied directory."
    }
    $candidate = [IO.Path]::GetFullPath((Join-Path $Directory $RelativePath))
    $prefix = $Directory.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (
        -not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $candidate -PathType Leaf)
    ) {
        throw "$Name was not found beneath its supplied directory: $candidate"
    }
    return $candidate
}

function Copy-DirectoryContents([string]$Source, [string]$Destination) {
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        Copy-Item -LiteralPath $item.FullName -Destination $Destination -Recurse -Force
    }
}

function Merge-ExactOverlay([string]$Source, [string]$Destination, [string]$Name) {
    Assert-NoReparsePoints $Source $Name
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    foreach ($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Force) {
        $relative = $file.FullName.Substring($sourceRoot.Length + 1)
        $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relative))
        if (-not $target.StartsWith(
            $destinationRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase
        )) {
            throw "$Name contains a path that escapes the bundle root: $relative"
        }
        [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
        if (Test-Path -LiteralPath $target) {
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                throw "$Name collides with a non-file bundle path: $relative"
            }
            $targetItem = Get-Item -LiteralPath $target -Force
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($file.Length -ne $targetItem.Length -or $sourceHash -cne $targetHash) {
                throw "$Name collides with different bundle bytes: $relative"
            }
            continue
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Assert-NoReparsePoints([string]$Root, [string]$Name) {
    $rootItem = Get-Item -LiteralPath $Root -Force
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $Root"
    }
    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Name contains a link or reparse point: $($item.FullName)"
        }
    }
}

function Assert-ExactFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length): $Path"
    }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256.ToLowerInvariant()) {
        throw "$Name SHA-256 mismatch: expected $($ExpectedSha256.ToLowerInvariant()), found ${actualHash}: $Path"
    }
}

function Assert-VersionProbe(
    [string]$Executable,
    [string[]]$Arguments,
    [string]$ExpectedToken,
    [string]$Name,
    [string]$RuntimeDirectory
) {
    $originalPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    try {
        [Environment]::SetEnvironmentVariable(
            'PATH',
            "$RuntimeDirectory$([IO.Path]::PathSeparator)$(Join-Path $env:SystemRoot 'System32')",
            'Process'
        )
        $probeOutput = (& $Executable @Arguments 2>&1 | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) {
            throw "$Name version probe failed with exit code ${LASTEXITCODE}: $probeOutput"
        }
        if ($probeOutput.IndexOf($ExpectedToken, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "$Name version probe did not contain '$ExpectedToken': $probeOutput"
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('PATH', $originalPath, 'Process')
    }
}

$resolvedComponentLock = Resolve-ChildFile `
    (Split-Path -Parent ([IO.Path]::GetFullPath($ComponentLockPath))) `
    (Split-Path -Leaf $ComponentLockPath) `
    'Offline component lock'
$componentLock = Get-Content -LiteralPath $resolvedComponentLock -Raw | ConvertFrom-Json
if ($componentLock.schemaVersion -ne 2 -or $componentLock.profile -ne 'windows-x64-offline-v3') {
    throw "Unsupported offline component lock schema or profile: $resolvedComponentLock"
}
$requiredComponents = @('python', 'magika', 'floss', 'llamaCpp', 'translationModel')
foreach ($componentName in $requiredComponents) {
    if ($null -eq $componentLock.components.$componentName) {
        throw "Offline component lock is missing '$componentName': $resolvedComponentLock"
    }
}
$llamaLock = $componentLock.components.llamaCpp
if (
    $llamaLock.archiveType -ne 'source-zip' -or
    [string]$llamaLock.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    [string]$llamaLock.sourceTag -ne [string]$llamaLock.version
) {
    throw 'The offline component lock must pin a full llama.cpp source commit and matching tag.'
}
$cudaOverlayLock = $componentLock.llamaCudaOverlay
if (
    $null -eq $cudaOverlayLock -or
    [string]$cudaOverlayLock.sourceTag -ne [string]$llamaLock.sourceTag -or
    [string]$cudaOverlayLock.sourceCommit -ne [string]$llamaLock.sourceCommit -or
    [string]$cudaOverlayLock.platform -cne 'windows-x64' -or
    [string]$cudaOverlayLock.acceptanceHardware.computeCapability -cne '8.9' -or
    [string]$cudaOverlayLock.provenancePath -cne 'llama-cuda-overlay-provenance.json'
) {
    throw 'The offline component lock must pin the reviewed same-commit Windows CUDA overlay.'
}
$magikaRedistributionLock = $componentLock.components.magika.redistribution
if (
    $null -eq $magikaRedistributionLock -or
    [string]$magikaRedistributionLock.inventoryPath -notmatch '^licenses/magika-cli-1\.1\.0-redistribution\.json$' -or
    [long]$magikaRedistributionLock.inventoryBytes -lt 1 -or
    [string]$magikaRedistributionLock.inventorySha256 -notmatch '^[0-9a-f]{64}$' -or
    [string]$magikaRedistributionLock.stageScript -cne 'tools/licenses/Stage-MagikaRedistribution.ps1' -or
    [string]$magikaRedistributionLock.verifyScript -cne 'tools/licenses/Verify-MagikaRedistribution.ps1' -or
    [string]$magikaRedistributionLock.runtimeExecutable -cne 'tools/magika/magika.exe' -or
    [string]$magikaRedistributionLock.runtimeDependency -cne 'tools/magika/DirectML.dll' -or
    [long]$magikaRedistributionLock.stagedFiles -lt 1 -or
    [long]$magikaRedistributionLock.stagedBytes -lt 1
) {
    throw 'The offline component lock has an invalid Magika redistribution overlay.'
}
$magikaInventoryPath = Resolve-ChildFile `
    $repoRoot `
    ([string]$magikaRedistributionLock.inventoryPath) `
    'Magika redistribution inventory'
Assert-ExactFile `
    $magikaInventoryPath `
    ([long]$magikaRedistributionLock.inventoryBytes) `
    ([string]$magikaRedistributionLock.inventorySha256) `
    'Magika redistribution inventory'
$magikaStageScript = Resolve-ChildFile `
    $repoRoot `
    ([string]$magikaRedistributionLock.stageScript) `
    'Magika redistribution stager'
$magikaVerifyScript = Resolve-ChildFile `
    $repoRoot `
    ([string]$magikaRedistributionLock.verifyScript) `
    'Magika redistribution verifier'
$flossRedistributionLock = $componentLock.components.floss.redistribution
if (
    $null -eq $flossRedistributionLock -or
    [string]$flossRedistributionLock.inventoryPath -notmatch '^licenses/floss-v3\.1\.1-win-x64\.json$' -or
    [long]$flossRedistributionLock.inventoryBytes -lt 1 -or
    [string]$flossRedistributionLock.inventorySha256 -notmatch '^[0-9a-f]{64}$' -or
    [string]$flossRedistributionLock.stageScript -cne 'tools/licenses/Stage-FlossThirdPartyNotices.ps1' -or
    [string]$flossRedistributionLock.verifyScript -cne 'tools/licenses/Verify-FlossThirdPartyNotices.ps1' -or
    [long]$flossRedistributionLock.stagedFiles -lt 1 -or
    [long]$flossRedistributionLock.stagedBytes -lt 1
) {
    throw 'The offline component lock has an invalid FLOSS redistribution overlay.'
}
$flossInventoryPath = Resolve-ChildFile `
    $repoRoot `
    ([string]$flossRedistributionLock.inventoryPath) `
    'FLOSS redistribution inventory'
Assert-ExactFile `
    $flossInventoryPath `
    ([long]$flossRedistributionLock.inventoryBytes) `
    ([string]$flossRedistributionLock.inventorySha256) `
    'FLOSS redistribution inventory'
$flossStageScript = Resolve-ChildFile `
    $repoRoot `
    ([string]$flossRedistributionLock.stageScript) `
    'FLOSS redistribution stager'
$flossVerifyScript = Resolve-ChildFile `
    $repoRoot `
    ([string]$flossRedistributionLock.verifyScript) `
    'FLOSS redistribution verifier'
$modelLock = $componentLock.components.translationModel
if (
    $null -eq $modelLock -or
    $TranslationModel -ne $modelLock.fileName -or
    $TranslationModelId -ne $modelLock.modelId -or
    $TranslationModelRevision -ne $modelLock.revision
) {
    throw 'The requested translation model name, ID, or revision does not match the component lock.'
}
$componentLockHash = (Get-FileHash -LiteralPath $resolvedComponentLock -Algorithm SHA256).Hash.ToLowerInvariant()
$resolvedOcrComponentLock = Resolve-ChildFile `
    (Join-Path $repoRoot 'tools\airgap') `
    'ocr-components.lock.json' `
    'OCR component lock'
$ocrComponentLock = Get-Content -LiteralPath $resolvedOcrComponentLock -Raw | ConvertFrom-Json
if (
    [int]$ocrComponentLock.schemaVersion -ne 1 -or
    [string]$ocrComponentLock.profile -ne 'windows-x64-ocr-cpu-directml-v1'
) {
    throw "Unsupported OCR component lock schema or profile: $resolvedOcrComponentLock"
}

function Assert-OcrSelfTest(
    [string]$Python,
    [string]$Worker,
    [string]$ModelPack,
    [object]$OcrLock,
    [string]$RequestedProvider,
    [string]$ExpectedProvider,
    [string]$Name
) {
    $probeOutput = (& $Python -I -B $Worker `
        --airgap `
        --self-test `
        --ocr-executable $Python `
        --ocr-engine rapidocr `
        --ocr-engine-version 3.9.2 `
        --ocr-model-path $ModelPack `
        --ocr-model-id ([string]$OcrLock.modelPack.modelId) `
        --ocr-model-revision ([string]$OcrLock.modelPack.revision) `
        --ocr-model-sha256 ([string]$OcrLock.modelPack.sha256) `
        --provider $RequestedProvider 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "$Name self-test failed with exit code ${LASTEXITCODE}: $probeOutput"
    }
    try {
        $probe = $probeOutput | ConvertFrom-Json
    }
    catch {
        throw "$Name self-test did not emit one JSON result: $probeOutput"
    }
    if (
        [int]$probe.schemaVersion -ne 1 -or
        [string]$probe.status -ne 'ok' -or
        [string]$probe.engine -ne 'rapidocr' -or
        [string]$probe.engineVersion -ne '3.9.2' -or
        [string]$probe.model -ne [string]$OcrLock.modelPack.modelId -or
        [string]$probe.revision -ne [string]$OcrLock.modelPack.revision -or
        [string]$probe.modelSha256 -ne [string]$OcrLock.modelPack.sha256 -or
        [string]$probe.provider -ne $ExpectedProvider
    ) {
        throw "$Name self-test identity or resolved provider did not match its lock: $probeOutput"
    }
    Write-Host "$Name self-test passed as $ExpectedProvider."
}
$ocrComponentLockHash = (Get-FileHash -LiteralPath $resolvedOcrComponentLock -Algorithm SHA256).Hash.ToLowerInvariant()

$sources = [ordered]@{
    bstrings = Resolve-RequiredDirectory $PublishedBstringsDirectory 'Published bstrings directory'
    python = Resolve-RequiredDirectory $PythonDirectory 'Portable Python directory'
    magika = Resolve-RequiredDirectory $MagikaDirectory 'Magika directory'
    magikaRedistribution = Resolve-RequiredDirectory `
        $MagikaRedistributionDirectory `
        'Magika redistribution overlay directory'
    floss = Resolve-RequiredDirectory $FlossDirectory 'FLOSS directory'
    flossRedistribution = Resolve-RequiredDirectory `
        $FlossRedistributionDirectory `
        'FLOSS redistribution overlay directory'
    llama = Resolve-RequiredDirectory $LlamaDirectory 'llama.cpp directory'
    model = Resolve-RequiredDirectory $TranslationModelDirectory 'Translation model directory'
    visualCppRuntime = Resolve-RequiredDirectory `
        $VisualCppRuntimeDirectory `
        'Visual C++ x64 app-local runtime directory'
    ocr = Resolve-RequiredDirectory $OcrComponentsDirectory 'Staged OCR components directory'
}
$system32 = [IO.Path]::GetFullPath(
    (Join-Path ([Environment]::GetFolderPath('Windows')) 'System32')
).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
if (
    $sources.visualCppRuntime.Equals($system32, [StringComparison]::OrdinalIgnoreCase) -or
    $sources.visualCppRuntime.StartsWith(
        $system32 + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )
) {
    throw "VisualCppRuntimeDirectory must be a licensed Visual C++ redistributable directory, not System32: $($sources.visualCppRuntime)"
}
$sourceBstrings = Resolve-ChildFile $sources.bstrings $BstringsExecutable 'bstrings executable'
$sourcePython = Resolve-ChildFile $sources.python $PythonExecutable 'Python executable'
$sourceMagika = Resolve-ChildFile $sources.magika $MagikaExecutable 'Magika executable'
$sourceFloss = Resolve-ChildFile $sources.floss $FlossExecutable 'FLOSS executable'
$sourceLlama = Resolve-ChildFile $sources.llama $LlamaServerExecutable 'llama.cpp server'
$sourceModel = Resolve-ChildFile $sources.model $TranslationModel 'Translation model'
$sourceOcrLock = Resolve-ChildFile $sources.ocr 'ocr-components.lock.json' 'Staged OCR component lock'
$sourceOcrInventory = Resolve-ChildFile `
    $sources.ocr `
    'licenses/ocr-runtime-files.json' `
    'Staged OCR runtime file inventory'
$sourceOcrLicenseInventory = Resolve-ChildFile `
    $sources.ocr `
    'licenses/ocr-runtime-win-x64.json' `
    'Staged OCR license inventory'
$sourceOcrActivePython = Resolve-ChildFile `
    $sources.ocr `
    'runtime/ocr-directml/python.exe' `
    'Active DirectML OCR Python runtime'
$sourceOcrCpuPython = Resolve-ChildFile `
    $sources.ocr `
    'runtime/ocr-cpu/python.exe' `
    'Alternate CPU-only OCR Python runtime'
$sourceOcrModel = Resolve-ChildFile `
    $sources.ocr `
    'models/ocr/ocr-model-pack.json' `
    'OCR model-pack manifest'
$sourceOcrWorker = Resolve-ChildFile `
    (Join-Path $repoRoot 'tools\enrichment') `
    'bstrings_ocr.py' `
    'OCR worker'
if (
    (Get-FileHash -LiteralPath $sourceOcrLock -Algorithm SHA256).Hash.ToLowerInvariant() -ne
    $ocrComponentLockHash
) {
    throw 'The staged OCR component lock differs from the reviewed repository lock.'
}
Assert-ExactFile `
    $sourceOcrModel `
    ([long]$ocrComponentLock.modelPack.bytes) `
    ([string]$ocrComponentLock.modelPack.sha256) `
    'OCR model-pack manifest'
$ocrRuntimeInventory = Get-Content -LiteralPath $sourceOcrInventory -Raw | ConvertFrom-Json
if (
    [int]$ocrRuntimeInventory.schemaVersion -ne 1 -or
    [string]$ocrRuntimeInventory.profile -ne [string]$ocrComponentLock.profile -or
    [string]$ocrRuntimeInventory.componentLockSha256 -ne $ocrComponentLockHash
) {
    throw 'Staged OCR runtime inventory does not identify the reviewed OCR component lock.'
}
$repositoryOcrLicenseInventory = Resolve-ChildFile `
    (Join-Path $repoRoot 'licenses') `
    'ocr-runtime-win-x64.json' `
    'Reviewed OCR license inventory'
if (
    (Get-FileHash -LiteralPath $sourceOcrLicenseInventory -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $repositoryOcrLicenseInventory -Algorithm SHA256).Hash
) {
    throw 'Staged OCR license inventory differs from the reviewed repository inventory.'
}
$ocrRuntimeDllNames = @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll')
foreach ($runtimeName in @('cpu', 'directml')) {
    $rows = @($ocrRuntimeInventory.runtimeFiles.$runtimeName)
    if ($rows.Count -lt 1) {
        throw "Staged OCR runtime inventory contains no $runtimeName files."
    }
    $lockedRelativePaths = @($rows | ForEach-Object { [string]$_.path })
    foreach ($row in $rows) {
        $runtimeFile = Resolve-ChildFile `
            (Join-Path $sources.ocr "runtime\ocr-$runtimeName") `
            ([string]$row.path) `
            "Staged $runtimeName OCR runtime file"
        Assert-ExactFile `
            $runtimeFile `
            ([long]$row.bytes) `
            ([string]$row.sha256) `
            "Staged $runtimeName OCR runtime file"
    }
    $unexpectedRuntimeFiles = @(
        Get-ChildItem `
            -LiteralPath (Join-Path $sources.ocr "runtime\ocr-$runtimeName") `
            -Recurse `
            -File | Where-Object {
                $relative = $_.FullName.Substring(
                    (Join-Path $sources.ocr "runtime\ocr-$runtimeName").Length + 1
                ).Replace('\', '/')
                $lockedRelativePaths -notcontains $relative -and
                $ocrRuntimeDllNames -notcontains $_.Name
            }
    )
    if ($unexpectedRuntimeFiles.Count -ne 0) {
        throw "Staged $runtimeName OCR runtime contains unreviewed files: $($unexpectedRuntimeFiles.FullName -join ', ')"
    }
}
foreach ($license in $ocrComponentLock.supplementalLicenses) {
    $licensePath = Resolve-ChildFile `
        (Join-Path $sources.ocr 'licenses\ocr-runtime') `
        ([string]$license.fileName) `
        "Supplemental OCR license $($license.id)"
    Assert-ExactFile `
        $licensePath `
        ([long]$license.bytes) `
        ([string]$license.sha256) `
        "Supplemental OCR license $($license.id)"
}
foreach ($sourceName in $sources.Keys) {
    Assert-NoReparsePoints $sources[$sourceName] "$sourceName source directory"
}
$overlayMagika = Resolve-ChildFile `
    $sources.magikaRedistribution `
    ([string]$magikaRedistributionLock.runtimeExecutable) `
    'Magika redistribution executable'
$null = Resolve-ChildFile `
    $sources.magikaRedistribution `
    ([string]$magikaRedistributionLock.runtimeDependency) `
    'Magika DirectML runtime dependency'
$magikaVerifyResult = @(
    & $magikaVerifyScript `
        -BundleDirectory $sources.magikaRedistribution `
        -InventoryPath $magikaInventoryPath
)
$magikaOverlayMeasure = Get-ChildItem `
    -LiteralPath $sources.magikaRedistribution `
    -Recurse `
    -File | Measure-Object -Property Length -Sum
if (
    $magikaVerifyResult.Count -ne 1 -or
    [long]$magikaVerifyResult[0].files -ne [long]$magikaRedistributionLock.stagedFiles -or
    [long]$magikaVerifyResult[0].totalOwnedBytes -ne [long]$magikaRedistributionLock.stagedBytes -or
    [long]$magikaOverlayMeasure.Count -ne [long]$magikaRedistributionLock.stagedFiles -or
    [long]$magikaOverlayMeasure.Sum -ne [long]$magikaRedistributionLock.stagedBytes
) {
    throw 'Magika redistribution overlay did not match its locked size and dependency closure.'
}
if (
    (Get-FileHash -LiteralPath $sourceMagika -Algorithm SHA256).Hash -cne
    (Get-FileHash -LiteralPath $overlayMagika -Algorithm SHA256).Hash
) {
    throw 'The supplied Magika directory and redistribution overlay contain different executables.'
}
$flossOverlayMeasure = Get-ChildItem `
    -LiteralPath $sources.flossRedistribution `
    -Recurse `
    -File | Measure-Object -Property Length -Sum
if (
    [long]$flossOverlayMeasure.Count -ne [long]$flossRedistributionLock.stagedFiles -or
    [long]$flossOverlayMeasure.Sum -ne [long]$flossRedistributionLock.stagedBytes
) {
    throw 'FLOSS redistribution overlay does not match its locked file count and size.'
}
& $flossVerifyScript `
    -FlossExecutable $sourceFloss `
    -InventoryPath $flossInventoryPath `
    -StagedDirectory $sources.flossRedistribution
$forbiddenLlamaFiles = @(
    Get-ChildItem -LiteralPath $sources.llama -Recurse -File -Force | Where-Object {
        $_.Name -like 'libomp140*.dll' -or
        $_.FullName -match '(?i)(^|[\\/])debug_nonredist([\\/]|$)'
    }
)
if ($forbiddenLlamaFiles.Count -ne 0) {
    throw "llama.cpp input contains a non-redistributable Visual Studio OpenMP/debug artifact: $($forbiddenLlamaFiles.FullName -join ', ')"
}

$llamaProvenancePath = Resolve-ChildFile `
    $sources.llama `
    'llama-build-provenance.json' `
    'llama.cpp source-build provenance'
$llamaProvenance = Get-Content -LiteralPath $llamaProvenancePath -Raw | ConvertFrom-Json
$lockedLlamaFlags = @($llamaLock.build.flags | ForEach-Object { [string]$_ })
$recordedLlamaFlags = @($llamaProvenance.build.flags | ForEach-Object { [string]$_ })
if (
    $llamaProvenance.schemaVersion -ne 1 -or
    $llamaProvenance.component -ne 'llama.cpp' -or
    $llamaProvenance.version -ne [string]$llamaLock.version -or
    $llamaProvenance.source.tag -ne [string]$llamaLock.sourceTag -or
    $llamaProvenance.source.commit -ne [string]$llamaLock.sourceCommit -or
    $llamaProvenance.source.archiveUrl -ne [string]$llamaLock.url -or
    $llamaProvenance.source.archiveBytes -ne [long]$llamaLock.bytes -or
    $llamaProvenance.source.archiveSha256 -ne [string]$llamaLock.sha256 -or
    $llamaProvenance.build.target -ne [string]$llamaLock.build.target -or
    $llamaProvenance.build.configuration -ne [string]$llamaLock.build.configuration -or
    $llamaProvenance.build.architecture -ne [string]$llamaLock.build.architecture -or
    $llamaProvenance.build.compiler.id -ne 'MSVC' -or
    $llamaProvenance.build.networkGuarded -ne $true -or
    ($recordedLlamaFlags -join '|') -cne ($lockedLlamaFlags -join '|')
) {
    throw 'llama.cpp build provenance does not match the pinned source archive and build recipe.'
}
$expectedLlamaRuntimeNames = @(
    [string]$llamaLock.executable,
    'llama-server-impl.dll',
    'llama-common.dll',
    'llama.dll',
    'mtmd.dll',
    'ggml.dll',
    'ggml-base.dll'
) + @($llamaLock.build.requiredCpuBackends | ForEach-Object { [string]$_ })
$recordedLlamaRuntimeNames = @(
    $llamaProvenance.runtimeFiles | ForEach-Object { [string]$_.name }
)
if (
    (@($expectedLlamaRuntimeNames | Sort-Object) -join '|') -cne
    (@($recordedLlamaRuntimeNames | Sort-Object) -join '|')
) {
    throw 'llama.cpp build provenance does not contain the exact reviewed runtime closure.'
}
foreach ($runtimeFile in @($llamaProvenance.runtimeFiles)) {
    $runtimePath = Resolve-ChildFile `
        $sources.llama `
        ([string]$runtimeFile.name) `
        "llama.cpp runtime $($runtimeFile.name)"
    Assert-ExactFile `
        $runtimePath `
        ([long]$runtimeFile.bytes) `
        ([string]$runtimeFile.sha256) `
        "llama.cpp runtime $($runtimeFile.name)"
    if ($null -eq $runtimeFile.imports) {
        throw "llama.cpp runtime provenance is missing PE imports for $($runtimeFile.name)."
    }
}
$cudaProvenancePath = Resolve-ChildFile `
    $sources.llama `
    ([string]$cudaOverlayLock.provenancePath) `
    'llama.cpp CUDA overlay provenance'
$cudaProvenance = Get-Content -LiteralPath $cudaProvenancePath -Raw | ConvertFrom-Json
if (
    $cudaProvenance.schemaVersion -ne 1 -or
    [string]$cudaProvenance.component -cne 'llama.cpp-cuda-overlay' -or
    [string]$cudaProvenance.version -ne [string]$cudaOverlayLock.version -or
    [string]$cudaProvenance.sourceTag -ne [string]$cudaOverlayLock.sourceTag -or
    [string]$cudaProvenance.sourceCommit -ne [string]$cudaOverlayLock.sourceCommit -or
    [string]$cudaProvenance.platform -ne [string]$cudaOverlayLock.platform -or
    $cudaProvenance.cpuFallback.sourceBuiltRuntimeRetained -ne $true
) {
    throw 'llama.cpp CUDA overlay provenance is missing or inconsistent with its lock.'
}
foreach ($archive in @($cudaOverlayLock.archives)) {
    $recordedArchives = @($cudaProvenance.archives | Where-Object {
        [string]$_.id -ceq [string]$archive.id
    })
    if (
        $recordedArchives.Count -ne 1 -or
        [string]$recordedArchives[0].fileName -ne [string]$archive.fileName -or
        [string]$recordedArchives[0].url -ne [string]$archive.url -or
        [long]$recordedArchives[0].bytes -ne [long]$archive.bytes -or
        [string]$recordedArchives[0].sha256 -ne [string]$archive.sha256 -or
        [int]$recordedArchives[0].entries -ne [int]$archive.entries -or
        [long]$recordedArchives[0].expandedBytes -ne [long]$archive.expandedBytes
    ) {
        throw "llama.cpp CUDA archive provenance differs from its lock: $($archive.id)"
    }
}
$cudaRuntimeNames = @()
foreach ($runtimeFile in @($cudaOverlayLock.runtimeFiles)) {
    $cudaRuntimePath = Resolve-ChildFile `
        $sources.llama `
        ([string]$runtimeFile.path) `
        "llama.cpp CUDA runtime $($runtimeFile.path)"
    Assert-ExactFile `
        $cudaRuntimePath `
        ([long]$runtimeFile.bytes) `
        ([string]$runtimeFile.sha256) `
        "llama.cpp CUDA runtime $($runtimeFile.path)"
    $recordedRuntimeFiles = @($cudaProvenance.runtimeFiles | Where-Object {
        [string]$_.path -ceq [string]$runtimeFile.path
    })
    $lockedImports = @($runtimeFile.imports | ForEach-Object {
        ([string]$_).ToLowerInvariant()
    } | Sort-Object)
    $recordedImports = if ($recordedRuntimeFiles.Count -eq 1) {
        @($recordedRuntimeFiles[0].imports | ForEach-Object {
            ([string]$_).ToLowerInvariant()
        } | Sort-Object)
    }
    else { @() }
    if (
        $recordedRuntimeFiles.Count -ne 1 -or
        [string]$recordedRuntimeFiles[0].archive -ne [string]$runtimeFile.archive -or
        [string]$recordedRuntimeFiles[0].entry -ne [string]$runtimeFile.entry -or
        [long]$recordedRuntimeFiles[0].bytes -ne [long]$runtimeFile.bytes -or
        [string]$recordedRuntimeFiles[0].sha256 -ne [string]$runtimeFile.sha256 -or
        [string]$recordedRuntimeFiles[0].role -ne [string]$runtimeFile.role -or
        ($recordedImports -join '|') -cne ($lockedImports -join '|')
    ) {
        throw "llama.cpp CUDA runtime provenance differs from its lock: $($runtimeFile.path)"
    }
    $cudaRuntimeNames += [string]$runtimeFile.path
}
$cudaLicense = Resolve-ChildFile `
    $sources.llama `
    ([string]$cudaOverlayLock.license.path) `
    'NVIDIA CUDA 12.4 EULA'
Assert-ExactFile `
    $cudaLicense `
    ([long]$cudaOverlayLock.license.bytes) `
    ([string]$cudaOverlayLock.license.sha256) `
    'NVIDIA CUDA 12.4 EULA'
$actualLlamaPeNames = @(
    Get-ChildItem -LiteralPath $sources.llama -File | Where-Object {
        $_.Extension -in @('.exe', '.dll')
    } | ForEach-Object { $_.Name }
)
$expectedLlamaPeNames = @($expectedLlamaRuntimeNames) + @($cudaRuntimeNames) + @(
    $componentLock.runtimeDlls | ForEach-Object { [string]$_ }
)
if (
    (@($actualLlamaPeNames | Sort-Object) -join '|') -cne
    (@($expectedLlamaPeNames | Sort-Object) -join '|')
) {
    throw 'llama.cpp input contains missing or unreviewed executable/runtime files.'
}

Assert-ExactFile `
    $sourceModel `
    ([long]$modelLock.bytes) `
    ([string]$modelLock.sha256) `
    'Pinned Hy-MT2 translation model'

$componentSources = @{
    python = $sources.python
    magika = $sources.magika
    floss = $sources.floss
    llamaCpp = $sources.llama
    translationModel = $sources.model
}
foreach ($componentName in $requiredComponents) {
    $license = $componentLock.components.$componentName.license
    if ($license.source -eq 'archive') {
        $licensePath = [string]$license.path
    }
    elseif ($license.source -eq 'download') {
        $licensePath = [string]$license.fileName
    }
    else {
        throw "Offline component lock has an unsupported license source for '$componentName'."
    }
    $resolvedLicense = Resolve-ChildFile `
        $componentSources[$componentName] `
        $licensePath `
        "$componentName license"
    if (
        $license.PSObject.Properties.Name -contains 'bytes' -and
        $license.PSObject.Properties.Name -contains 'sha256'
    ) {
        Assert-ExactFile `
            $resolvedLicense `
            ([long]$license.bytes) `
            ([string]$license.sha256) `
            "$componentName license"
    }
}
foreach ($notice in @($llamaLock.notices)) {
    $noticePath = Resolve-ChildFile `
        $sources.llama `
        ([string]$notice.path) `
        "llama.cpp notice $($notice.path)"
    Assert-ExactFile `
        $noticePath `
        ([long]$notice.bytes) `
        ([string]$notice.sha256) `
        "llama.cpp notice $($notice.path)"
}
$null = Resolve-ChildFile `
    $sources.llama `
    'notices/llama.cpp/NOTICE-SCOPE.md' `
    'llama.cpp human-readable notice scope'

$pythonPathFiles = @(Get-ChildItem -LiteralPath $sources.python -Filter 'python*._pth' -File)
if ($pythonPathFiles.Count -ne 1) {
    throw 'Portable Python must be an official-style isolated embeddable distribution with one python*._pth file.'
}
$activeSiteImport = @(Get-Content -LiteralPath $pythonPathFiles[0].FullName | Where-Object {
    $_.Trim() -match '^import\s+site\s*$'
})
if ($activeSiteImport.Count -ne 0) {
    throw 'Portable Python must keep import site disabled in its python*._pth file.'
}
$runtimeDlls = @($componentLock.runtimeDlls | ForEach-Object { [string]$_ })
$expectedRuntimeDlls = @(
    'vcruntime140.dll',
    'vcruntime140_1.dll',
    'msvcp140.dll',
    'msvcp140_1.dll'
)
if (($runtimeDlls -join '|') -cne ($expectedRuntimeDlls -join '|')) {
    throw "Offline component lock must name the four required Visual C++ runtime DLLs in the expected order: $($expectedRuntimeDlls -join ', ')"
}
$runtimeInventory = @()
foreach ($runtimeDll in $runtimeDlls) {
    $runtimeSource = Resolve-ChildFile `
        $sources.visualCppRuntime `
        $runtimeDll `
        "Visual C++ runtime $runtimeDll"
    $runtimeItem = Get-Item -LiteralPath $runtimeSource
    $runtimeHash = (Get-FileHash -LiteralPath $runtimeSource -Algorithm SHA256).Hash.ToLowerInvariant()
    $runtimeVersion = [string]$runtimeItem.VersionInfo.FileVersion
    if ([string]::IsNullOrWhiteSpace($runtimeVersion)) {
        throw "Visual C++ runtime DLL does not expose a file version: $runtimeSource"
    }
    $runtimeInventory += [ordered]@{
        name = $runtimeDll
        bytes = $runtimeItem.Length
        sha256 = $runtimeHash
        fileVersion = $runtimeVersion
    }
    foreach ($componentDirectory in @(
        $sources.bstrings,
        $sources.python,
        $sources.magika,
        $sources.floss,
        $sources.llama,
        (Join-Path $sources.ocr 'runtime\ocr-cpu'),
        (Join-Path $sources.ocr 'runtime\ocr-directml')
    )) {
        $appLocalRuntime = Resolve-ChildFile `
            $componentDirectory `
            $runtimeDll `
            "App-local $runtimeDll"
        Assert-ExactFile `
            $appLocalRuntime `
            $runtimeItem.Length `
            $runtimeHash `
            "App-local $runtimeDll"
    }
}
$requiredBstringsNotices = @(
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
    'licenses/Rust-1.95.0-COPYRIGHT-library.html'
)
foreach ($relativePath in $requiredBstringsNotices) {
    $null = Resolve-ChildFile `
        $sources.bstrings `
        $relativePath `
        "Published bstrings release notice $relativePath"
}
$managedNoticeVerifier = Resolve-ChildFile `
    $repoRoot `
    'tools\licenses\Verify-ManagedThirdPartyNotices.ps1' `
    'Managed release-notice verifier'
& $managedNoticeVerifier -PublishedDirectory $sources.bstrings
Assert-VersionProbe `
    $sourceBstrings `
    @('bundle', '--help') `
    'verify' `
    'bstrings bundle command' `
    $sources.bstrings
$originalPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
try {
    [Environment]::SetEnvironmentVariable(
        'PATH',
        "$($sources.python)$([IO.Path]::PathSeparator)$(Join-Path $env:SystemRoot 'System32')",
        'Process'
    )
    $pythonImportOutput = (& $sourcePython -I -c 'import hashlib, ipaddress, json, pathlib, socket, urllib.request, sys; print(sys.version.split()[0]); print("PYTHON_EMBED_OK")' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Portable Python could not import the standard-library modules required by bstrings: $pythonImportOutput"
    }
    Write-Host $pythonImportOutput
    $pythonVersionOutput = (& $sourcePython -I -c 'import platform; print(platform.python_version())' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $pythonVersionOutput -ne [string]$componentLock.components.python.version) {
        throw "Portable Python version mismatch: expected $($componentLock.components.python.version), found '$pythonVersionOutput'."
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PATH', $originalPath, 'Process')
}
Assert-VersionProbe `
    $sourceMagika `
    @('--version') `
    ([string]$componentLock.components.magika.version) `
    'Magika' `
    $sources.magika
Assert-VersionProbe `
    $sourceFloss `
    @('--version') `
    ([string]$componentLock.components.floss.version) `
    'FLOSS' `
    $sources.floss
Assert-VersionProbe `
    $sourceLlama `
    @('--version') `
    '10248' `
    'llama.cpp' `
    $sources.llama
Assert-VersionProbe `
    $sourceLlama `
    @('--version') `
    ([string]$llamaLock.sourceCommit) `
    'llama.cpp source commit' `
    $sources.llama
Assert-VersionProbe `
    $sourceLlama `
    @('--help') `
    '--offline' `
    'llama.cpp offline guard' `
    $sources.llama
foreach ($provider in $resolvedOcrSelfTestProviders) {
    $expectedProvider = switch ($provider) {
        'cpu' { 'cpu' }
        'directml' { 'directml' }
        'hybrid' { 'hybrid-directml-cpu' }
    }
    Assert-OcrSelfTest `
        $sourceOcrActivePython `
        $sourceOcrWorker `
        $sourceOcrModel `
        $ocrComponentLock `
        $provider `
        $expectedProvider `
        "Active OCR $provider path"
}
if ('cpu' -in $resolvedOcrSelfTestProviders -or 'hybrid' -in $resolvedOcrSelfTestProviders) {
    Assert-OcrSelfTest `
        $sourceOcrCpuPython `
        $sourceOcrWorker `
        $sourceOcrModel `
        $ocrComponentLock `
        'cpu' `
        'cpu' `
        'Alternate CPU-only OCR runtime'
}

if ($RapidsPythonDirectory) {
    $sources.rapids = Resolve-RequiredDirectory $RapidsPythonDirectory 'RAPIDS Python directory'
    $null = Resolve-ChildFile $sources.rapids $RapidsPythonExecutable 'RAPIDS Python executable'
}
if ($MadladModelDirectory) {
    $sources.madlad = Resolve-RequiredDirectory $MadladModelDirectory 'MADLAD model directory'
    $null = Resolve-ChildFile $sources.madlad $MadladWeights 'MADLAD weights'
}
foreach ($optionalSource in @('rapids', 'madlad')) {
    if ($sources.Contains($optionalSource)) {
        Assert-NoReparsePoints $sources[$optionalSource] "$optionalSource source directory"
    }
}

$null = Resolve-ChildFile `
    $PSScriptRoot `
    'Verify-MarkdownLinks.ps1' `
    'Bundled Markdown-link verifier'
$bundleEnrichmentToolNames = @(
    'bstrings_enrich.py',
    'bstrings_ocr.py',
    'benchmark_ocr.py',
    'benchmark_translation_cache.py',
    'benchmark_translation.py'
)
$bundleDocumentNames = @(
    'air-gapped-deployment.md',
    'command-reference.md',
    'crypto-address-coverage-2026-08.md',
    'document-reading-research-2026-08.md',
    'download-and-install.md',
    'enrichment-pipeline.md',
    'floss-standalone-redistribution.md',
    'forensic-reporting-2026-08.md',
    'language-triage-performance-2026-08.md',
    'magika-cli-redistribution.md',
    'ocr-and-document-analysis.md',
    'ocr-benchmark-2026-08-05.md',
    'offline-release-maintenance.md',
    'output-and-provenance.md',
    'pattern-engine-benchmark-2026-08.md',
    'pattern-validity-review-2026-08.md',
    'scale-benchmark-2026-08.md',
    'translation-benchmark-2026-08-04.md'
)
$bundleArchitectureDocumentNames = @(
    'adr-0001-early-fail-open-content-routing.md',
    'adr-0003-persistent-verified-bytes-and-batched-releases.md',
    'adr-0004-bounded-language-detection-reuse.md',
    'adr-0005-translation-integrity-and-run-dedup.md',
    'adr-0006-q4-cuda-full-translation.md',
    'adr-0007-translation-worthiness-routing.md',
    'adr-0008-independent-engine-execution.md',
    'adr-0009-bounded-forensic-pattern-expansion.md',
    'adr-0010-bounded-reversible-decoding.md',
    'adr-0011-single-windows-kit-and-plain-documentation.md',
    'adr-0012-explicit-whole-stage-resume.md',
      'adr-0013-resume-with-translation-off.md',
      'adr-0014-compact-investigator-findings.md',
      'adr-0015-high-confidence-base64-and-bounded-match-reuse.md',
      'decision-review-policy.md'
)
$bundleBenchmarkResultNames = @(
    'forensic-pattern-catalog-2026-08.csv',
    'forensic-pattern-catalog-v6-2026-08.csv',
    'forensic-report-compact-schema-2026-08.csv',
    'forensic-report-projection-2026-08.csv',
    'language-triage-routing-shadow-2026-08.csv',
    'language-triage-reuse-2026-08.csv'
)
$bundleReleaseDocumentNames = @(
    'v1.9.17.md',
    'v2.0.0.md',
    'v2.1.0.md',
    'v2.1.1.md',
    'v2.1.2.md'
)
$bundleRootDocumentNames = @(
    'BASE_PACK_NOTICE.md',
    'VERSIONING.md'
)
foreach ($toolName in $bundleEnrichmentToolNames) {
    $null = Resolve-ChildFile `
        (Join-Path $repoRoot 'tools\enrichment') `
        $toolName `
        "Bundled enrichment tool $toolName"
}
foreach ($documentName in $bundleDocumentNames) {
    $null = Resolve-ChildFile `
        (Join-Path $repoRoot 'docs') `
        $documentName `
        "Bundled document $documentName"
}
foreach ($documentName in $bundleArchitectureDocumentNames) {
    $null = Resolve-ChildFile `
        (Join-Path $repoRoot 'docs\architecture') `
        $documentName `
        "Bundled architecture document $documentName"
}
foreach ($resultName in $bundleBenchmarkResultNames) {
    $null = Resolve-ChildFile `
        (Join-Path $repoRoot 'benchmarks\results') `
        $resultName `
        "Bundled benchmark result $resultName"
}
foreach ($documentName in $bundleReleaseDocumentNames) {
    $null = Resolve-ChildFile `
        (Join-Path $repoRoot 'docs\releases') `
        $documentName `
        "Bundled release document $documentName"
}
foreach ($documentName in $bundleRootDocumentNames) {
    $null = Resolve-ChildFile $repoRoot $documentName "Bundled root document $documentName"
}

$fixtureSourceDirectory = Join-Path $PSScriptRoot 'fixtures'
$fixtureExecutable = Join-Path $fixtureSourceDirectory 'floss-recovery-smoke.exe'
$fixtureHashFile = Join-Path $fixtureSourceDirectory 'floss-recovery-smoke.sha256'
if (
    -not (Test-Path -LiteralPath $fixtureExecutable -PathType Leaf) -or
    -not (Test-Path -LiteralPath $fixtureHashFile -PathType Leaf)
) {
    throw 'The checked-in FLOSS recovery smoke fixture or its SHA-256 file is missing.'
}
Assert-NoReparsePoints $fixtureSourceDirectory 'FLOSS recovery fixture directory'
$fixtureHashRecord = (Get-Content -LiteralPath $fixtureHashFile -Raw).Trim()
if ($fixtureHashRecord -notmatch '^(?<hash>[0-9a-fA-F]{64})  floss-recovery-smoke\.exe$') {
    throw "Invalid FLOSS recovery fixture SHA-256 record: $fixtureHashFile"
}
$fixtureExpectedHash = $Matches.hash.ToLowerInvariant()
$fixtureActualHash = (Get-FileHash -LiteralPath $fixtureExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
if ($fixtureActualHash -ne $fixtureExpectedHash) {
    throw "FLOSS recovery smoke fixture SHA-256 mismatch: expected $fixtureExpectedHash, found $fixtureActualHash."
}

if ($ValidateOnly) {
    Write-Host 'Air-gap bundle inputs validated successfully; no output was created.'
    return
}

$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar
)
if (Test-Path -LiteralPath $output) {
    throw "OutputDirectory must not already exist: $output"
}
foreach ($source in $sources.Values) {
    $prefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if ($output.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "OutputDirectory cannot be placed inside an input directory: $source"
    }
}

[IO.Directory]::CreateDirectory($output) | Out-Null
$incomplete = Join-Path $output '.incomplete'
[IO.File]::WriteAllText($incomplete, "Air-gap bundle creation did not complete.`n")
try {
    Copy-DirectoryContents $sources.bstrings $output
    Copy-DirectoryContents $sources.python (Join-Path $output 'runtime\python')
    Copy-DirectoryContents $sources.magika (Join-Path $output 'tools\magika')
    Merge-ExactOverlay `
        $sources.magikaRedistribution `
        $output `
        'Magika redistribution overlay'
    Copy-DirectoryContents $sources.floss (Join-Path $output 'tools\floss')
    Merge-ExactOverlay `
        $sources.flossRedistribution `
        $output `
        'FLOSS redistribution overlay'
    Copy-DirectoryContents $sources.llama (Join-Path $output 'runtime\llama')
    Copy-DirectoryContents $sources.model (Join-Path $output 'models\hy-mt2')
    Merge-ExactOverlay $sources.ocr $output 'OCR runtime, model, and notice overlay'
    foreach ($childRuntimeDirectory in @(
        $output,
        (Join-Path $output 'runtime\python'),
        (Join-Path $output 'tools\magika'),
        (Join-Path $output 'tools\floss'),
        (Join-Path $output 'runtime\llama'),
        (Join-Path $output 'runtime\ocr-cpu'),
        (Join-Path $output 'runtime\ocr-directml')
    )) {
        foreach ($runtimeDll in $runtimeDlls) {
            Copy-Item `
                -LiteralPath (Join-Path $sources.visualCppRuntime $runtimeDll) `
                -Destination (Join-Path $childRuntimeDirectory $runtimeDll) `
                -Force
        }
    }
    if ($sources.Contains('rapids')) {
        Copy-DirectoryContents $sources.rapids (Join-Path $output 'runtime\rapids-python')
    }
    if ($sources.Contains('madlad')) {
        Copy-DirectoryContents $sources.madlad (Join-Path $output 'models\madlad')
    }

    [IO.Directory]::CreateDirectory((Join-Path $output 'tools\enrichment')) | Out-Null
    foreach ($name in $bundleEnrichmentToolNames) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "tools\enrichment\$name") `
            -Destination (Join-Path $output "tools\enrichment\$name")
    }
    [IO.Directory]::CreateDirectory((Join-Path $output 'tools\airgap')) | Out-Null
    foreach ($name in @(
        'airgap_manifest.py',
        'generate_ocr_smoke_fixtures.py',
        'verify_network_guard.py',
        'Verify-OcrRuntime.ps1',
        'Verify-MarkdownLinks.ps1',
        'smoke-evidence.txt'
    )) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $output "tools\airgap\$name")
    }
    Copy-Item -LiteralPath $resolvedComponentLock `
        -Destination (Join-Path $output 'tools\airgap\offline-components.lock.json')
    Copy-DirectoryContents `
        $fixtureSourceDirectory `
        (Join-Path $output 'tools\airgap\fixtures')
    $ocrFixtureDirectory = Join-Path $output 'tools\airgap\fixtures\ocr'
    & (Join-Path $output 'runtime\ocr-directml\python.exe') -I -B `
        (Join-Path $output 'tools\airgap\generate_ocr_smoke_fixtures.py') `
        --output $ocrFixtureDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not generate the bundled synthetic OCR image and scanned-PDF fixtures.'
    }
    [IO.Directory]::CreateDirectory((Join-Path $output 'tools\licenses')) | Out-Null
    Copy-Item `
        -LiteralPath $magikaVerifyScript `
        -Destination (Join-Path $output 'tools\licenses\Verify-MagikaRedistribution.ps1')
    Copy-Item `
        -LiteralPath $flossVerifyScript `
        -Destination (Join-Path $output 'tools\licenses\Verify-FlossThirdPartyNotices.ps1')
    foreach ($name in @(
        'Invoke-BstringsAirgap.ps1',
        'Invoke-EnrichmentAirgap.ps1',
        'Set-AirgapEnvironment.ps1',
        'Verify-AirgapBundle.ps1'
    )) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) `
            -Destination (Join-Path $output $name)
    }
    $bundleDocs = Join-Path $output 'docs'
    [IO.Directory]::CreateDirectory($bundleDocs) | Out-Null
    foreach ($documentName in $bundleDocumentNames) {
        $document = Resolve-ChildFile `
            (Join-Path $repoRoot 'docs') `
            $documentName `
            "Bundled document $documentName"
        Copy-Item -LiteralPath $document -Destination $bundleDocs
    }
    $bundleArchitectureDocs = Join-Path $bundleDocs 'architecture'
    [IO.Directory]::CreateDirectory($bundleArchitectureDocs) | Out-Null
    foreach ($documentName in $bundleArchitectureDocumentNames) {
        $document = Resolve-ChildFile `
            (Join-Path $repoRoot 'docs\architecture') `
            $documentName `
            "Bundled architecture document $documentName"
        Copy-Item -LiteralPath $document -Destination $bundleArchitectureDocs
    }
    $bundleBenchmarkResults = Join-Path $output 'benchmarks\results'
    [IO.Directory]::CreateDirectory($bundleBenchmarkResults) | Out-Null
    foreach ($resultName in $bundleBenchmarkResultNames) {
        $result = Resolve-ChildFile `
            (Join-Path $repoRoot 'benchmarks\results') `
            $resultName `
            "Bundled benchmark result $resultName"
        Copy-Item -LiteralPath $result -Destination $bundleBenchmarkResults
    }
    $bundleReleaseDocs = Join-Path $bundleDocs 'releases'
    [IO.Directory]::CreateDirectory($bundleReleaseDocs) | Out-Null
    foreach ($documentName in $bundleReleaseDocumentNames) {
        $document = Resolve-ChildFile `
            (Join-Path $repoRoot 'docs\releases') `
            $documentName `
            "Bundled release document $documentName"
        Copy-Item -LiteralPath $document -Destination $bundleReleaseDocs
    }
    foreach ($documentName in $bundleRootDocumentNames) {
        $document = Resolve-ChildFile $repoRoot $documentName "Bundled root document $documentName"
        Copy-Item -LiteralPath $document -Destination $output
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') `
        -Destination (Join-Path $output 'README.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.md') `
        -Destination (Join-Path $output 'LICENSE-bstrings.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE.md') `
        -Destination (Join-Path $output 'LICENSE.md')
    Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD_PARTY_NOTICES.md') `
        -Destination (Join-Path $output 'THIRD_PARTY_NOTICES.md')
    $bundleLicenses = Join-Path $output 'licenses'
    [IO.Directory]::CreateDirectory($bundleLicenses) | Out-Null
    foreach ($license in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'licenses') -File) {
        Copy-Item -LiteralPath $license.FullName -Destination $bundleLicenses
    }
    $upstreamLicenseDestinations = [ordered]@{
        magika = 'Magika-Apache-2.0.txt'
        floss = 'FLOSS-Apache-2.0.txt'
        llamaCpp = 'llama.cpp-MIT.txt'
        translationModel = 'Hy-MT2-Apache-2.0.txt'
    }
    foreach ($componentName in $upstreamLicenseDestinations.Keys) {
        $license = $componentLock.components.$componentName.license
        $licenseRelativePath = if ($license.source -eq 'archive') {
            [string]$license.path
        }
        else {
            [string]$license.fileName
        }
        $sourceLicense = Resolve-ChildFile `
            $componentSources[$componentName] `
            $licenseRelativePath `
            "$componentName license"
        Copy-Item -LiteralPath $sourceLicense `
            -Destination (Join-Path $bundleLicenses $upstreamLicenseDestinations[$componentName])
    }
    $translationLicense = $componentLock.components.translationModel.license
    $sourceTranslationLicense = Resolve-ChildFile `
        $sources.model `
        ([string]$translationLicense.fileName) `
        'translation model license'
    Assert-ExactFile `
        $sourceTranslationLicense `
        ([long]$translationLicense.bytes) `
        ([string]$translationLicense.sha256) `
        'translation model license'
    Copy-Item -LiteralPath $sourceTranslationLicense `
        -Destination (Join-Path $bundleLicenses 'Hy-MT2-7B-Apache-2.0.txt')
    Copy-DirectoryContents `
        (Join-Path $sources.llama 'notices\llama.cpp') `
        (Join-Path $bundleLicenses 'llama.cpp')
    Copy-Item `
        -LiteralPath $cudaLicense `
        -Destination (Join-Path $bundleLicenses 'NVIDIA-CUDA-12.4-EULA.pdf')
    Copy-Item -LiteralPath $resolvedComponentLock `
        -Destination (Join-Path $output 'offline-components.lock.json')
    & (Join-Path $output 'tools\airgap\Verify-MarkdownLinks.ps1') `
        -BundleDirectory $output

    $modelPath = Join-Path (Join-Path $output 'models\hy-mt2') $TranslationModel
    $modelHash = (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $madladConfig = $null
    if ($sources.Contains('madlad')) {
        $madladWeightsPath = Join-Path (Join-Path $output 'models\madlad') $MadladWeights
        $madladHash = (Get-FileHash -LiteralPath $madladWeightsPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $madladConfig = [ordered]@{
            path = 'models/madlad'
            id = $MadladModelId
            revision = $MadladModelRevision
            sha256 = $madladHash
        }
    }
    $config = [ordered]@{
        schemaVersion = 2
        bundleProfile = [string]$componentLock.profile
        componentLock = 'offline-components.lock.json'
        componentLockSha256 = $componentLockHash
        visualCppRuntime = [ordered]@{
            source = [string]$componentLock.runtimePolicy.source
            deployment = 'app-local'
            directories = @(
                '.',
                'runtime/python',
                'tools/magika',
                'tools/floss',
                'runtime/llama',
                'runtime/ocr-cpu',
                'runtime/ocr-directml'
            )
            files = $runtimeInventory
        }
        llamaCppRuntime = [ordered]@{
            version = [string]$llamaLock.version
            sourceTag = [string]$llamaLock.sourceTag
            sourceCommit = [string]$llamaLock.sourceCommit
            sourceArchiveSha256 = [string]$llamaLock.sha256
            provenance = 'runtime/llama/llama-build-provenance.json'
            openMp = $false
            offlineArgument = '--offline'
        }
        llamaCudaOverlay = [ordered]@{
            version = [string]$cudaOverlayLock.version
            sourceTag = [string]$cudaOverlayLock.sourceTag
            sourceCommit = [string]$cudaOverlayLock.sourceCommit
            platform = [string]$cudaOverlayLock.platform
            acceptanceComputeCapability = [string]$cudaOverlayLock.acceptanceHardware.computeCapability
            provenance = "runtime/llama/$($cudaOverlayLock.provenancePath)"
            runtimeFiles = @($cudaRuntimeNames | ForEach-Object { "runtime/llama/$_" })
            license = 'licenses/NVIDIA-CUDA-12.4-EULA.pdf'
            cpuFallback = $true
        }
        magikaRedistribution = [ordered]@{
            inventory = [string]$magikaRedistributionLock.inventoryPath
            verifier = [string]$magikaRedistributionLock.verifyScript
            runtimeDependency = [string]$magikaRedistributionLock.runtimeDependency
            stagedFiles = [long]$magikaRedistributionLock.stagedFiles
            stagedBytes = [long]$magikaRedistributionLock.stagedBytes
        }
        flossRedistribution = [ordered]@{
            inventory = [string]$flossRedistributionLock.inventoryPath
            verifier = [string]$flossRedistributionLock.verifyScript
            stagedFiles = [long]$flossRedistributionLock.stagedFiles
            stagedBytes = [long]$flossRedistributionLock.stagedBytes
        }
        bstringsExecutable = $BstringsExecutable -replace '\\', '/'
        pythonExecutable = "runtime/python/$($PythonExecutable -replace '\\', '/')"
        enrichmentAdapter = 'tools/enrichment/bstrings_enrich.py'
        magikaExecutable = "tools/magika/$($MagikaExecutable -replace '\\', '/')"
        flossExecutable = "tools/floss/$($FlossExecutable -replace '\\', '/')"
        llamaServer = "runtime/llama/$($LlamaServerExecutable -replace '\\', '/')"
        manifestTool = 'tools/airgap/airgap_manifest.py'
        networkGuardProbe = 'tools/airgap/verify_network_guard.py'
        smokeEvidence = 'tools/airgap/smoke-evidence.txt'
        smokeRecoveryFixture = 'tools/airgap/fixtures/floss-recovery-smoke.exe'
        translationModel = [ordered]@{
            path = "models/hy-mt2/$($TranslationModel -replace '\\', '/')"
            id = $TranslationModelId
            revision = $TranslationModelRevision
            sha256 = $modelHash
        }
        ocr = [ordered]@{
            pythonExecutable = 'runtime/ocr-directml/python.exe'
            executable = 'runtime/ocr-directml/python.exe'
            adapter = 'tools/enrichment/bstrings_ocr.py'
            engine = 'rapidocr'
            engineVersion = '3.9.2'
            activeRuntime = 'directml-with-cpu-provider'
            alternateCpuRuntime = 'runtime/ocr-cpu/python.exe'
            assemblySelfTestProviders = @($resolvedOcrSelfTestProviders)
            model = [ordered]@{
                path = 'models/ocr/ocr-model-pack.json'
                id = [string]$ocrComponentLock.modelPack.modelId
                revision = [string]$ocrComponentLock.modelPack.revision
                sha256 = [string]$ocrComponentLock.modelPack.sha256
            }
        }
        rapidsPythonExecutable = if ($sources.Contains('rapids')) {
            "runtime/rapids-python/$($RapidsPythonExecutable -replace '\\', '/')"
        } else {
            $null
        }
        madladModel = $madladConfig
    }
    $config | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $output 'airgap-config.json') -Encoding utf8

    & (Join-Path $output 'tools\airgap\Verify-OcrRuntime.ps1') -BundleDirectory $output

    $manifest = Join-Path $output 'airgap-manifest.json'
    $bundlePython = Join-Path (Join-Path $output 'runtime\python') $PythonExecutable
    $manifestTool = Join-Path $output 'tools\airgap\airgap_manifest.py'
    $originalPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    try {
        [Environment]::SetEnvironmentVariable(
            'PATH',
            "$(Split-Path -Parent $bundlePython)$([IO.Path]::PathSeparator)$(Join-Path $env:SystemRoot 'System32')",
            'Process'
        )
        & $bundlePython -I $manifestTool create --root $output --manifest $manifest
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not create the air-gap bundle manifest.'
        }
        & $bundlePython -I $manifestTool verify `
            --root $output `
            --manifest $manifest `
            --allow-incomplete-marker
        if ($LASTEXITCODE -ne 0) {
            throw 'The newly created air-gap bundle did not pass builder-only Python verification.'
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('PATH', $originalPath, 'Process')
    }

    $bundleBstrings = Join-Path $output $BstringsExecutable
    $originalPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
    try {
        [Environment]::SetEnvironmentVariable(
            'PATH',
            "$output$([IO.Path]::PathSeparator)$(Join-Path $env:SystemRoot 'System32')",
            'Process'
        )
        & $bundleBstrings bundle verify `
            --bundle-root $output `
            --allow-incomplete-marker
        if ($LASTEXITCODE -ne 0) {
            throw 'The bundled bstrings executable did not pass builder-only bundle verification.'
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable('PATH', $originalPath, 'Process')
    }

    [IO.File]::Delete($incomplete)
    if ($null -ne (Get-Item -LiteralPath $incomplete -Force -ErrorAction SilentlyContinue)) {
        throw 'Could not remove the completed air-gap bundle marker.'
    }

    $totals = Get-ChildItem -LiteralPath $output -Recurse -File |
        Measure-Object -Property Length -Sum
    $manifestHash = (Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash
    Write-Host "Air-gap bundle created: $output"
    Write-Host "Files: $($totals.Count); bytes: $($totals.Sum)"
    Write-Host "Record this manifest SHA-256 separately: $manifestHash"
    Write-Host 'Transfer the directory and verify it before use with Verify-AirgapBundle.ps1.'
}
catch {
    if (-not (Test-Path -LiteralPath $incomplete -PathType Leaf)) {
        [IO.File]::WriteAllText($incomplete, "Air-gap bundle creation failed.`n")
    }
    throw
}
