[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$CrateCacheDirectory,
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [string]$RuntimeInventoryPath,
    [string]$MagikaLicensePath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($RuntimeInventoryPath)) {
    $RuntimeInventoryPath = Join-Path $repoRoot 'licenses\magika-cli-1.1.0-win-x64.tsv'
}
if ([string]::IsNullOrWhiteSpace($MagikaLicensePath)) {
    throw 'MagikaLicensePath is required so the two workspace packages receive their exact upstream notice.'
}

function Assert-ExactFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Name was not found: $Path"
    }
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length)."
    }
    $hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found $hash."
    }
    return $resolved
}

function Get-TarEntryBytes([string]$TarPath, [string]$EntryName, [string]$Name) {
    $tar = Get-Command tar.exe -CommandType Application -ErrorAction Stop
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $tar.Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.ArgumentList.Add('-xOf')
    $startInfo.ArgumentList.Add($TarPath)
    $startInfo.ArgumentList.Add($EntryName)
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $memory = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) {
            throw "Could not start tar.exe for $Name."
        }
        $errorTask = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $errorText = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "tar.exe failed for ${Name}: $errorText"
        }
        return $memory.ToArray()
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

function Convert-LicenseBytesToText([byte[]]$Bytes, [string]$Name) {
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    try {
        $text = $utf8.GetString($Bytes)
    }
    catch {
        throw "$Name is not valid UTF-8: $($_.Exception.Message)"
    }
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xFEFF) {
        $text = $text.Substring(1)
    }
    return (($text -replace "`r`n", "`n") -replace "`r", "`n").TrimEnd("`n") + "`n"
}

$inventory = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RuntimeInventoryPath).Path)
$cacheRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $CrateCacheDirectory).Path)
$magikaLicense = Assert-ExactFile `
    $MagikaLicensePath `
    11357 `
    '58d1e17ffe5109a7ae296caafcadfdbe6a7d176f0bc4ab01e12a689b0499d8bd' `
    'Magika license'
$rows = @(Import-Csv -LiteralPath $inventory -Delimiter "`t")
$registryRows = @($rows | Where-Object { $_.checksum_sha256 -match '^[0-9a-f]{64}$' })
$workspaceRows = @($rows | Where-Object { $_.checksum_sha256 -like 'source-commit:*' })
if ($rows.Count -ne 70 -or $registryRows.Count -ne 68 -or $workspaceRows.Count -ne 2) {
    throw "Expected 70 runtime packages (68 registry and 2 workspace); found $($rows.Count), $($registryRows.Count), and $($workspaceRows.Count)."
}

$separator = '=' * 80
$builder = [Text.StringBuilder]::new()
$null = $builder.Append("Magika CLI 1.1.0 for Windows x64 - component notices`n")
$null = $builder.Append("Generated only from the 68 checksum-locked crates and the exact Magika license.`n")
$null = $builder.Append("License file line endings are normalized to LF; substantive text is unchanged.`n`n")

foreach ($row in $rows) {
    $null = $builder.Append("$separator`n")
    $null = $builder.Append("PACKAGE: $($row.package) $($row.version)`n")
    $null = $builder.Append("DECLARED LICENSE: $($row.declared_license)`n")
    $null = $builder.Append("REPOSITORY: $($row.repository)`n")
    $null = $builder.Append("SOURCE CHECKSUM: $($row.checksum_sha256)`n")
    if ($row.archive_bytes) {
        $null = $builder.Append("PUBLISHED ARCHIVE BYTES: $($row.archive_bytes)`n")
    }

    if ($row.checksum_sha256 -like 'source-commit:*') {
        $licenseText = Convert-LicenseBytesToText ([IO.File]::ReadAllBytes($magikaLicense)) 'Magika license'
        $null = $builder.Append("LICENSE FILE: Magika-LICENSE.txt`n`n")
        $null = $builder.Append($licenseText)
        $null = $builder.Append("`n")
        continue
    }

    $crateName = "$($row.package)-$($row.version).crate"
    $candidates = @(Get-ChildItem -LiteralPath $cacheRoot -Recurse -File -Filter $crateName)
    $matching = @()
    foreach ($candidate in $candidates) {
        if (
            $candidate.Length -eq [long]$row.archive_bytes -and
            (Get-FileHash -LiteralPath $candidate.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $row.checksum_sha256
        ) {
            $matching += $candidate
        }
    }
    if ($matching.Count -ne 1) {
        throw "Expected exactly one locked cache match for $crateName; found $($matching.Count)."
    }
    $cratePath = Assert-ExactFile `
        $matching[0].FullName `
        ([long]$row.archive_bytes) `
        ([string]$row.checksum_sha256) `
        "crate $crateName"
    $entries = @(& (Get-Command tar.exe -CommandType Application -ErrorAction Stop).Source -tf $cratePath)
    if ($LASTEXITCODE -ne 0) {
        throw "Could not list locked crate $crateName."
    }
    $rootName = "$($row.package)-$($row.version)"
    foreach ($entry in $entries) {
        $parts = $entry -split '/'
        if (
            $entry.Contains('\') -or
            $entry.StartsWith('/') -or
            $parts.Count -lt 2 -or
            $parts[0] -cne $rootName -or
            $parts -contains '..' -or
            $parts -contains '.'
        ) {
            throw "Unsafe entry in locked crate ${crateName}: $entry"
        }
    }
    $licenseEntries = @($entries | Where-Object {
            [IO.Path]::GetFileName($_) -match '(?i)^(LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT|UNLICENSE)([-._].*)?$'
        } | Sort-Object -CaseSensitive)
    if ($licenseEntries.Count -eq 0) {
        throw "Locked crate $crateName contains no license or notice file."
    }
    foreach ($entry in $licenseEntries) {
        $bytes = Get-TarEntryBytes $cratePath $entry "$crateName entry $entry"
        $licenseText = Convert-LicenseBytesToText $bytes "$crateName entry $entry"
        $relativeEntry = $entry.Substring($rootName.Length + 1)
        $null = $builder.Append("LICENSE FILE: $relativeEntry`n`n")
        $null = $builder.Append($licenseText)
        $null = $builder.Append("`n")
    }
}

$outputFull = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputFull)) | Out-Null
$utf8NoBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($outputFull, $builder.ToString(), $utf8NoBom)
$item = Get-Item -LiteralPath $outputFull
$hash = (Get-FileHash -LiteralPath $outputFull -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Generated 70-package Magika notices: $($item.Length) bytes, SHA-256 $hash"
[pscustomobject]@{
    path = $outputFull
    packages = $rows.Count
    bytes = $item.Length
    sha256 = $hash
}
