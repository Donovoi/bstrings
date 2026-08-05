[CmdletBinding()]
param(
    [string]$BundleDirectory,
    [switch]$Smoke,
    [ValidateSet('cpu', 'directml', 'hybrid')]
    [string[]]$SmokeProviders = @('cpu', 'directml', 'hybrid')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Smoke -and $PSBoundParameters.ContainsKey('SmokeProviders')) {
    throw '-SmokeProviders requires -Smoke.'
}
$requestedSmokeProviders = @(
    $SmokeProviders | ForEach-Object { $_.ToLowerInvariant() }
)
if (
    $requestedSmokeProviders.Count -lt 1 -or
    @($requestedSmokeProviders | Sort-Object -Unique).Count -ne $requestedSmokeProviders.Count
) {
    throw 'SmokeProviders must contain one or more unique provider names.'
}

if ([string]::IsNullOrWhiteSpace($BundleDirectory)) {
    $BundleDirectory = Join-Path $PSScriptRoot '..\..'
}
if (-not (Test-Path -LiteralPath $BundleDirectory -PathType Container)) {
    throw "OCR bundle directory was not found: $BundleDirectory"
}
$bundleRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $BundleDirectory).Path)
$bundleItem = Get-Item -LiteralPath $bundleRoot -Force
if (($bundleItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "OCR bundle directory is a link or reparse point: $bundleRoot"
}

function Resolve-BundlePath([string]$RelativePath, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)'
    ) {
        throw "$Name is not a safe bundle-relative path: '$RelativePath'"
    }
    $root = $script:bundleRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $resolved = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    if (-not $resolved.StartsWith(
        $root + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "$Name escapes the bundle root: $resolved"
    }
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "$Name is missing: $resolved"
    }
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    return $resolved
}

function Assert-ExactFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    if ($ExpectedBytes -lt 0 -or $ExpectedSha256 -notmatch '^[0-9a-f]{64}$') {
        throw "$Name has an invalid locked identity."
    }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length)."
    }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found $actualHash."
    }
}

$lockPath = Resolve-BundlePath 'ocr-components.lock.json' 'OCR component lock'
$runtimeInventoryPath = Resolve-BundlePath 'licenses/ocr-runtime-files.json' 'OCR runtime file inventory'
$licenseInventoryPath = Resolve-BundlePath 'licenses/ocr-runtime-win-x64.json' 'OCR license inventory'
$configurationPath = Resolve-BundlePath 'airgap-config.json' 'air-gap configuration'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$runtimeInventory = Get-Content -LiteralPath $runtimeInventoryPath -Raw | ConvertFrom-Json
$licenseInventory = Get-Content -LiteralPath $licenseInventoryPath -Raw | ConvertFrom-Json
$configuration = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
if (
    [int]$lock.schemaVersion -ne 1 -or
    [string]$lock.profile -ne 'windows-x64-ocr-cpu-directml-v1' -or
    [int]$runtimeInventory.schemaVersion -ne 1 -or
    [string]$runtimeInventory.profile -ne [string]$lock.profile -or
    [int]$licenseInventory.schemaVersion -ne 1 -or
    [string]$licenseInventory.profile -ne [string]$lock.profile
) {
    throw 'OCR lock and inventory profiles do not agree.'
}
$lockHash = (Get-FileHash -LiteralPath $lockPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ([string]$runtimeInventory.componentLockSha256 -ne $lockHash) {
    throw 'OCR runtime inventory does not identify the bundled component lock.'
}

$expectedActivePython = 'runtime/ocr-directml/python.exe'
$expectedWorker = 'tools/enrichment/bstrings_ocr.py'
$expectedModel = 'models/ocr/ocr-model-pack.json'
$assemblySelfTestProviders = @(
    $configuration.ocr.assemblySelfTestProviders |
        ForEach-Object { ([string]$_).ToLowerInvariant() }
)
if (
    [string]$configuration.ocr.pythonExecutable -ne $expectedActivePython -or
    [string]$configuration.ocr.executable -ne $expectedActivePython -or
    [string]$configuration.ocr.adapter -ne $expectedWorker -or
    [string]$configuration.ocr.engine -ne 'rapidocr' -or
    [string]$configuration.ocr.engineVersion -ne '3.9.2' -or
    [string]$configuration.ocr.model.path -ne $expectedModel -or
    [string]$configuration.ocr.model.id -ne [string]$lock.modelPack.modelId -or
    [string]$configuration.ocr.model.revision -ne [string]$lock.modelPack.revision -or
    [string]$configuration.ocr.model.sha256 -ne [string]$lock.modelPack.sha256 -or
    'cpu' -notin $assemblySelfTestProviders -or
    @($assemblySelfTestProviders | Sort-Object -Unique).Count -ne
        $assemblySelfTestProviders.Count -or
    @($assemblySelfTestProviders | Where-Object { $_ -notin @('cpu', 'directml', 'hybrid') }).Count -ne 0
) {
    throw 'airgap-config.json does not select the verified active DirectML OCR profile.'
}

$activePython = Resolve-BundlePath $expectedActivePython 'active OCR Python runtime'
$cpuPython = Resolve-BundlePath 'runtime/ocr-cpu/python.exe' 'alternate CPU-only OCR Python runtime'
$worker = Resolve-BundlePath $expectedWorker 'OCR worker'
$modelPackPath = Resolve-BundlePath $expectedModel 'OCR model-pack manifest'
Assert-ExactFile `
    $modelPackPath `
    ([long]$lock.modelPack.bytes) `
    ([string]$lock.modelPack.sha256) `
    'OCR model-pack manifest'
$modelPack = Get-Content -LiteralPath $modelPackPath -Raw | ConvertFrom-Json
if (
    [int]$modelPack.schemaVersion -ne 1 -or
    [string]$modelPack.modelId -ne [string]$lock.modelPack.modelId -or
    [string]$modelPack.revision -ne [string]$lock.modelPack.revision
) {
    throw 'OCR model-pack identity does not match its lock.'
}
foreach ($componentName in @('detector', 'recognizer', 'classifier', 'dictionary')) {
    $locked = $lock.modelPack.$componentName
    $manifestComponent = $modelPack.$componentName
    if (
        [string]$manifestComponent.path -ne [string]$locked.path -or
        [string]$manifestComponent.sha256 -ne [string]$locked.sha256
    ) {
        throw "OCR model-pack $componentName identity does not match its lock."
    }
    $componentPath = Resolve-BundlePath `
        ("models/ocr/" + [string]$locked.path) `
        "OCR model $componentName"
    Assert-ExactFile `
        $componentPath `
        ([long]$locked.bytes) `
        ([string]$locked.sha256) `
        "OCR model $componentName"
}

foreach ($runtimeName in @('cpu', 'directml')) {
    $rows = $runtimeInventory.runtimeFiles.$runtimeName
    if ($null -eq $rows -or $rows.Count -lt 1) {
        throw "OCR runtime inventory contains no $runtimeName files."
    }
    foreach ($row in $rows) {
        $path = Resolve-BundlePath `
            ("runtime/ocr-$runtimeName/" + [string]$row.path) `
            "$runtimeName OCR runtime file"
        Assert-ExactFile $path ([long]$row.bytes) ([string]$row.sha256) "$runtimeName OCR runtime file"
    }
    $notices = $runtimeInventory.noticeFiles.$runtimeName
    if ($null -eq $notices -or $notices.Count -lt 1) {
        throw "OCR runtime inventory contains no $runtimeName notices."
    }
}
foreach ($license in $lock.supplementalLicenses) {
    $path = Resolve-BundlePath `
        ("licenses/ocr-runtime/" + [string]$license.fileName) `
        "supplemental OCR license $($license.id)"
    Assert-ExactFile $path ([long]$license.bytes) ([string]$license.sha256) "supplemental OCR license $($license.id)"
}

if (-not $Smoke) {
    Write-Host 'OCR runtimes, model pack, configuration, inventories, and notices verified.'
    return
}

$fixtureManifestPath = Resolve-BundlePath 'tools/airgap/fixtures/ocr/expected.json' 'OCR fixture manifest'
$fixtureManifest = Get-Content -LiteralPath $fixtureManifestPath -Raw | ConvertFrom-Json
if ([int]$fixtureManifest.schemaVersion -ne 1 -or [string]$fixtureManifest.classification -ne 'synthetic') {
    throw 'OCR fixture manifest is not a synthetic schema-version-1 corpus.'
}
$fixturePaths = [Collections.Generic.List[string]]::new()
foreach ($fixture in $fixtureManifest.files) {
    $path = Resolve-BundlePath `
        ("tools/airgap/fixtures/ocr/" + [string]$fixture.path) `
        'OCR smoke fixture'
    Assert-ExactFile $path ([long]$fixture.bytes) ([string]$fixture.sha256) 'OCR smoke fixture'
    $fixturePaths.Add($path)
}
if ($fixturePaths.Count -ne 2) {
    throw "OCR smoke requires exactly one image and one scanned PDF; found $($fixturePaths.Count)."
}

$identityArguments = @(
    '--airgap',
    '--self-test',
    '--ocr-executable', $activePython,
    '--ocr-engine', 'rapidocr',
    '--ocr-engine-version', '3.9.2',
    '--ocr-model-path', $modelPackPath,
    '--ocr-model-id', [string]$lock.modelPack.modelId,
    '--ocr-model-revision', [string]$lock.modelPack.revision,
    '--ocr-model-sha256', [string]$lock.modelPack.sha256
)
foreach ($provider in $requestedSmokeProviders) {
    & $activePython -I -B $worker @identityArguments --provider $provider
    if ($LASTEXITCODE -ne 0) {
        throw "Active OCR runtime failed its $provider inference self-test."
    }
}
if ('cpu' -in $requestedSmokeProviders -or 'hybrid' -in $requestedSmokeProviders) {
    $cpuIdentityArguments = [Collections.Generic.List[string]]::new()
    $cpuIdentityArguments.AddRange([string[]]$identityArguments)
    $executableIndex = $cpuIdentityArguments.IndexOf($activePython)
    if ($executableIndex -lt 0) { throw 'Could not prepare the alternate CPU-only OCR self-test.' }
    $cpuIdentityArguments[$executableIndex] = $cpuPython
    & $cpuPython -I -B $worker @cpuIdentityArguments --provider cpu
    if ($LASTEXITCODE -ne 0) {
        throw 'Alternate CPU-only OCR runtime failed its inference self-test.'
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('bstrings-ocr-smoke-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $inventoryPath = Join-Path $temporaryRoot 'inventory.txt'
    [IO.File]::WriteAllLines(
        $inventoryPath,
        [string[]]$fixturePaths,
        [Text.UTF8Encoding]::new($false)
    )
    foreach ($provider in $requestedSmokeProviders) {
        $providerRoot = Join-Path $temporaryRoot $provider
        [IO.Directory]::CreateDirectory($providerRoot) | Out-Null
        $stringsPath = Join-Path $providerRoot 'strings.jsonl'
        $assessmentsPath = Join-Path $providerRoot 'assessments.jsonl'
        & $activePython -I -B $worker `
            --airgap `
            --paths-from $inventoryPath `
            --output $stringsPath `
            --assessments-output $assessmentsPath `
            --ocr-executable $activePython `
            --ocr-engine rapidocr `
            --ocr-engine-version 3.9.2 `
            --ocr-model-path $modelPackPath `
            --ocr-model-id ([string]$lock.modelPack.modelId) `
            --ocr-model-revision ([string]$lock.modelPack.revision) `
            --ocr-model-sha256 ([string]$lock.modelPack.sha256) `
            --provider $provider `
            --ocr-mode force `
            --threads 2 `
            --dpi 300
        if ($LASTEXITCODE -ne 0) {
            throw "Offline $provider image and scanned-PDF OCR smoke failed."
        }
        $records = @(
            Get-Content -LiteralPath $stringsPath | ForEach-Object { $_ | ConvertFrom-Json }
        )
        $assessments = @(
            Get-Content -LiteralPath $assessmentsPath | ForEach-Object { $_ | ConvertFrom-Json }
        )
        $expectedResolvedProvider = switch ($provider) {
            'cpu' { 'cpu' }
            'directml' { 'directml' }
            'hybrid' { 'hybrid-directml-cpu' }
        }
        if (
            $assessments.Count -ne 2 -or
            @(
                $assessments | Where-Object {
                    $_.status -ne 'processed' -or
                    [string]$_.provider -ne $expectedResolvedProvider
                }
            ).Count -ne 0
        ) {
            throw "OCR $provider smoke did not complete both synthetic evidence items with the expected provider."
        }
        foreach ($fixturePath in $fixturePaths) {
            $texts = @(
                $records | Where-Object { $_.sourceFile -eq $fixturePath } |
                    ForEach-Object { [string]$_.text }
            )
            foreach ($expectedLine in $fixtureManifest.expectedLines) {
                if ($texts -notcontains [string]$expectedLine) {
                    throw "OCR $provider smoke did not recover an exact expected line from $fixturePath."
                }
            }
        }
    }
}
finally {
    $resolvedTemporary = [IO.Path]::GetFullPath($temporaryRoot)
    if (
        [IO.Path]::GetFileName($resolvedTemporary).StartsWith('bstrings-ocr-smoke-', [StringComparison]::Ordinal) -and
        $resolvedTemporary.StartsWith(
            [IO.Path]::GetFullPath([IO.Path]::GetTempPath()),
            [StringComparison]::OrdinalIgnoreCase
        )
    ) {
        [IO.Directory]::Delete($resolvedTemporary, $true)
    }
    else {
        throw "Refusing to remove uncontrolled OCR smoke directory: $resolvedTemporary"
    }
}

Write-Host "OCR image and scanned-PDF offline smoke passed for: $($requestedSmokeProviders -join ', ')."
