[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$BundleDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $BundleDirectory -PathType Container)) {
    throw "BundleDirectory was not found: $BundleDirectory"
}
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $BundleDirectory).Path).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
# First-party user documentation lives at the bundle root and beneath docs/. Runtime
# and license trees contain upstream Markdown whose relative links target source-tree
# files that are intentionally not redistributed; their bytes are verified separately.
$markdownFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($markdownFile in Get-ChildItem -LiteralPath $root -File -Filter '*.md' -Force) {
    $markdownFiles.Add($markdownFile)
}
$documentationRoot = Join-Path $root 'docs'
if (Test-Path -LiteralPath $documentationRoot) {
    if (-not (Test-Path -LiteralPath $documentationRoot -PathType Container)) {
        throw "Bundled documentation path is not a directory: $documentationRoot"
    }
    foreach ($markdownFile in Get-ChildItem `
        -LiteralPath $documentationRoot `
        -Recurse `
        -File `
        -Filter '*.md' `
        -Force) {
        $markdownFiles.Add($markdownFile)
    }
}
$markdownFiles = @($markdownFiles | Sort-Object -Property FullName -Unique)
$checkedLinks = 0
$failures = [Collections.Generic.List[string]]::new()

foreach ($markdownFile in $markdownFiles) {
    $text = Get-Content -LiteralPath $markdownFile.FullName -Raw
    foreach ($match in [regex]::Matches($text, '!?\[[^\]]*\]\((?<target>[^)]+)\)')) {
        $rawTarget = $match.Groups['target'].Value.Trim()
        if ([string]::IsNullOrWhiteSpace($rawTarget)) {
            continue
        }
        $target = if ($rawTarget.StartsWith('<', [StringComparison]::Ordinal)) {
            $closing = $rawTarget.IndexOf('>')
            if ($closing -lt 2) {
                $failures.Add("$($markdownFile.FullName): malformed angle-bracket link '$rawTarget'")
                continue
            }
            $rawTarget.Substring(1, $closing - 1)
        }
        else {
            ($rawTarget -split '\s+', 2)[0]
        }
        if (
            $target.StartsWith('#', [StringComparison]::Ordinal) -or
            $target -match '^(?i:https?|mailto|tel|data):'
        ) {
            continue
        }
        $pathPart = ($target -split '[?#]', 2)[0]
        if ([string]::IsNullOrWhiteSpace($pathPart)) {
            continue
        }
        $checkedLinks++
        try {
            $decodedPath = [Uri]::UnescapeDataString($pathPart)
            if ([IO.Path]::IsPathRooted($decodedPath)) {
                throw 'absolute filesystem path'
            }
            $candidate = [IO.Path]::GetFullPath((Join-Path $markdownFile.DirectoryName $decodedPath))
            if (
                -not $candidate.Equals($root, [StringComparison]::OrdinalIgnoreCase) -and
                -not $candidate.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)
            ) {
                throw 'path escapes the bundle root'
            }
            if (-not (Test-Path -LiteralPath $candidate)) {
                throw 'target is missing'
            }
        }
        catch {
            $relativeSource = [IO.Path]::GetRelativePath($root, $markdownFile.FullName)
            $failures.Add("${relativeSource}: '$target' ($($_.Exception.Message))")
        }
    }
}

if ($failures.Count -ne 0) {
    throw "Bundled Markdown has invalid local links:`n$($failures -join "`n")"
}

Write-Host "Bundled Markdown verified: $($markdownFiles.Count) files and $checkedLinks local links."
