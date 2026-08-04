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
$markdownFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter '*.md' -Force)
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
