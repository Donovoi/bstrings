[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory,
    [string]$DownloadCacheDirectory,
    [string]$LockPath,
    [string]$InventoryPath,
    [string]$PythonExecutable = 'python',
    [switch]$DryRun,
    [switch]$ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($DryRun -and $ValidateOnly) {
    throw '-DryRun and -ValidateOnly are mutually exclusive.'
}
if ([string]::IsNullOrWhiteSpace($LockPath)) {
    $LockPath = Join-Path $PSScriptRoot 'ocr-components.lock.json'
}
if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    $InventoryPath = Join-Path (Join-Path $PSScriptRoot '..\..\licenses') 'ocr-runtime-win-x64.json'
}
if ([string]::IsNullOrWhiteSpace($DownloadCacheDirectory)) {
    $DownloadCacheDirectory = Join-Path ([IO.Path]::GetTempPath()) 'bstrings-ocr-components-v1'
}

function Resolve-RequiredFile([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name was not found: $Path"
    }
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    return $resolved
}

function Assert-SafeLeafName([string]$Value, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($Value) -or
        [IO.Path]::IsPathRooted($Value) -or
        [IO.Path]::GetFileName($Value) -ne $Value -or
        $Value.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
    ) {
        throw "$Name is not a safe file name: '$Value'"
    }
}

function Assert-PathInside([string]$Root, [string]$Path, [string]$Name) {
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith(
        $fullRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "$Name escapes its controlled root: $fullPath"
    }
}

function Assert-LockedFile([object]$Spec, [string]$Name) {
    Assert-SafeLeafName ([string]$Spec.fileName) "$Name fileName"
    if ([long]$Spec.bytes -lt 1 -or [long]$Spec.bytes -eq [long]::MaxValue) {
        throw "$Name must have a bounded positive byte length."
    }
    if ([string]$Spec.sha256 -notmatch '^[0-9a-f]{64}$') {
        throw "$Name must have a lowercase SHA-256."
    }
    $uri = $null
    if (-not [Uri]::TryCreate([string]$Spec.url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -ne 'https') {
        throw "$Name must have an absolute HTTPS URL."
    }
}

function Assert-ExactFile([string]$Path, [object]$Spec, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name is missing: $Path"
    }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $Path"
    }
    if ($item.Length -ne [long]$Spec.bytes) {
        throw "$Name byte length mismatch: expected $($Spec.bytes), found $($item.Length)."
    }
    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne [string]$Spec.sha256) {
        throw "$Name SHA-256 mismatch: expected $($Spec.sha256), found $actualHash."
    }
}

function Get-VerifiedDownload(
    [object]$Spec,
    [string]$Destination,
    [string]$Name,
    [bool]$AllowNetwork
) {
    Assert-PathInside $script:cacheRoot $Destination "$Name cache path"
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        try {
            Assert-ExactFile $Destination $Spec $Name
            Write-Host "Using verified cached ${Name}: $Destination"
            return
        }
        catch {
            if (-not $AllowNetwork) { throw }
            [IO.File]::Delete([IO.Path]::GetFullPath($Destination))
        }
    }
    if (-not $AllowNetwork) {
        throw "$Name is not present in the validation cache: $Destination"
    }

    $partial = $Destination + '.partial.' + [Guid]::NewGuid().ToString('N')
    Assert-PathInside $script:cacheRoot $partial "$Name partial download"
    $response = $null
    $input = $null
    $output = $null
    try {
        Write-Host "Downloading $Name from $($Spec.url)"
        $response = $script:httpClient.GetAsync(
            [string]$Spec.url,
            [Net.Http.HttpCompletionOption]::ResponseHeadersRead
        ).GetAwaiter().GetResult()
        $response.EnsureSuccessStatusCode() | Out-Null
        if ($response.RequestMessage.RequestUri.Scheme -ne 'https') {
            throw "$Name redirected to a non-HTTPS URL: $($response.RequestMessage.RequestUri)"
        }
        $expectedBytes = [long]$Spec.bytes
        if (
            $null -ne $response.Content.Headers.ContentLength -and
            [long]$response.Content.Headers.ContentLength -ne $expectedBytes
        ) {
            throw "$Name Content-Length does not match its lock."
        }
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.FileStream]::new(
            $partial,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            1024 * 1024,
            [IO.FileOptions]::SequentialScan
        )
        $buffer = [byte[]]::new(1024 * 1024)
        $downloaded = [long]0
        while ($true) {
            $readSize = [int][Math]::Min(
                [long]$buffer.Length,
                ($expectedBytes - $downloaded) + 1
            )
            $read = $input.Read($buffer, 0, $readSize)
            if ($read -eq 0) { break }
            $output.Write($buffer, 0, $read)
            $downloaded += $read
            if ($downloaded -gt $expectedBytes) {
                throw "$Name exceeded its locked byte length."
            }
        }
        $output.Flush($true)
        $output.Dispose()
        $output = $null
        $input.Dispose()
        $input = $null
        $response.Dispose()
        $response = $null
        Assert-ExactFile $partial $Spec $Name
        [IO.File]::Move($partial, $Destination)
        Assert-ExactFile $Destination $Spec $Name
    }
    finally {
        if ($null -ne $output) { $output.Dispose() }
        if ($null -ne $input) { $input.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        if (Test-Path -LiteralPath $partial -PathType Leaf) {
            [IO.File]::Delete([IO.Path]::GetFullPath($partial))
        }
    }
}

$resolvedLock = Resolve-RequiredFile $LockPath 'OCR component lock'
$resolvedInventory = Resolve-RequiredFile $InventoryPath 'OCR license inventory'
$lock = Get-Content -LiteralPath $resolvedLock -Raw | ConvertFrom-Json
if ([int]$lock.schemaVersion -ne 1) {
    throw "Unsupported OCR component lock schemaVersion: $($lock.schemaVersion)"
}
if ([string]$lock.profile -ne 'windows-x64-ocr-cpu-directml-v1') {
    throw "Unsupported OCR component profile: $($lock.profile)"
}

$specs = [Collections.Generic.List[object]]::new()
foreach ($runtimeName in @('cpu', 'directml')) {
    $specs.Add([pscustomobject]@{
        Name = "$runtimeName embeddable Python"
        Spec = $lock.pythonRuntimes.$runtimeName
    })
}
foreach ($package in $lock.packages) {
    $specs.Add([pscustomobject]@{ Name = "Python package $($package.id)"; Spec = $package })
}
foreach ($componentName in @('detector', 'recognizer', 'classifier')) {
    $component = $lock.modelPack.$componentName
    if ($null -eq $component.PSObject.Properties['derivedFrom']) {
        $specs.Add([pscustomobject]@{
            Name = "OCR model $componentName"
            Spec = $component
        })
    }
}
$specs.Add([pscustomobject]@{
    Name = 'OCR recognizer dictionary source'
    Spec = $lock.modelPack.dictionary.derivedFrom
})
foreach ($license in $lock.supplementalLicenses) {
    $specs.Add([pscustomobject]@{ Name = "supplemental license $($license.id)"; Spec = $license })
}

$fileNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $specs) {
    Assert-LockedFile $entry.Spec $entry.Name
    if (-not $fileNames.Add([string]$entry.Spec.fileName)) {
        throw "OCR component lock reuses a cache file name: $($entry.Spec.fileName)"
    }
}

$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$script:cacheRoot = [IO.Path]::GetFullPath($DownloadCacheDirectory)
if ($destination -eq $script:cacheRoot) {
    throw 'DestinationDirectory and DownloadCacheDirectory must be different.'
}

Write-Host "OCR profile: $($lock.profile)"
Write-Host "Locked inputs: $($specs.Count)"
Write-Host "Destination: $destination"
Write-Host "Download cache: $script:cacheRoot"
if ($DryRun) {
    foreach ($entry in $specs) {
        Write-Host ("  {0}  {1} bytes  {2}" -f $entry.Spec.sha256, $entry.Spec.bytes, $entry.Spec.fileName)
    }
    Write-Host 'Dry run completed without creating or downloading files.'
    return
}
if (Test-Path -LiteralPath $destination) {
    throw "OCR component destination must not already exist: $destination"
}
[IO.Directory]::CreateDirectory($script:cacheRoot) | Out-Null
$cacheItem = Get-Item -LiteralPath $script:cacheRoot -Force
if (($cacheItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "Download cache is a link or reparse point: $script:cacheRoot"
}

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $true
$handler.MaxAutomaticRedirections = 5
$script:httpClient = [Net.Http.HttpClient]::new($handler)
$script:httpClient.Timeout = [TimeSpan]::FromMinutes(30)
try {
    foreach ($entry in $specs) {
        Get-VerifiedDownload `
            $entry.Spec `
            (Join-Path $script:cacheRoot ([string]$entry.Spec.fileName)) `
            $entry.Name `
            (-not $ValidateOnly)
    }
}
finally {
    $script:httpClient.Dispose()
    $handler.Dispose()
}

$stageScript = Resolve-RequiredFile (Join-Path $PSScriptRoot 'stage_ocr_components.py') 'OCR staging helper'
if ($ValidateOnly) {
    $validationParent = Join-Path ([IO.Path]::GetTempPath()) ('bstrings-ocr-validation-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($validationParent) | Out-Null
    $stageOutput = Join-Path $validationParent 'staged'
}
else {
    $parent = [IO.Directory]::GetParent($destination)
    if ($null -eq $parent) { throw "Destination has no parent directory: $destination" }
    [IO.Directory]::CreateDirectory($parent.FullName) | Out-Null
    $stageOutput = $destination
}

try {
    & $PythonExecutable -I -B $stageScript stage `
        --lock $resolvedLock `
        --inventory $resolvedInventory `
        --cache $script:cacheRoot `
        --output $stageOutput
    if ($LASTEXITCODE -ne 0) {
        throw "OCR staging helper failed with exit code $LASTEXITCODE."
    }
    if ($ValidateOnly) {
        Write-Host 'OCR cache, runtimes, models, and notices validated successfully.'
    }
    else {
        Write-Host "Staged verified offline OCR components: $destination"
    }
}
finally {
    if ($ValidateOnly -and (Test-Path -LiteralPath $validationParent -PathType Container)) {
        $resolvedValidationParent = [IO.Path]::GetFullPath($validationParent)
        if (
            [IO.Path]::GetFileName($resolvedValidationParent).StartsWith(
                'bstrings-ocr-validation-',
                [StringComparison]::Ordinal
            ) -and
            $resolvedValidationParent.StartsWith(
                [IO.Path]::GetFullPath([IO.Path]::GetTempPath()),
                [StringComparison]::OrdinalIgnoreCase
            )
        ) {
            [IO.Directory]::Delete($resolvedValidationParent, $true)
        }
        else {
            throw "Refusing to remove uncontrolled OCR validation directory: $resolvedValidationParent"
        }
    }
}
