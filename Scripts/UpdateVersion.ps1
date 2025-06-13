# PowerShell script to automatically increment the build number in bstrings.csproj
# Usage: .\Scripts\UpdateVersion.ps1 [patch|minor|major]

param(
    [Parameter(Position=0)]
    [ValidateSet("patch", "minor", "major")]
    [string]$VersionType = "patch"
)

$csprojPath = "bstrings/bstrings.csproj"

if (-not (Test-Path $csprojPath)) {
    Write-Error "Could not find $csprojPath"
    exit 1
}

try {
    # Read the .csproj file
    $csprojContent = Get-Content $csprojPath -Raw
    
    # Extract current version
    $versionMatch = [regex]::Match($csprojContent, '<Version>([^<]+)</Version>')
    if (-not $versionMatch.Success) {
        Write-Error "Could not find <Version> tag in $csprojPath"
        exit 1
    }
    
    $currentVersion = $versionMatch.Groups[1].Value
    Write-Host "Current version: $currentVersion"
    
    # Parse version components
    $versionParts = $currentVersion.Split('.')
    if ($versionParts.Length -lt 3) {
        Write-Error "Version format should be major.minor.patch (e.g., 1.7.0)"
        exit 1
    }
    
    $major = [int]$versionParts[0]
    $minor = [int]$versionParts[1]
    $patch = [int]$versionParts[2]
    
    # Increment based on type
    switch ($VersionType) {
        "major" { 
            $major++
            $minor = 0
            $patch = 0
        }
        "minor" { 
            $minor++
            $patch = 0
        }
        "patch" { 
            $patch++
        }
    }
    
    $newVersion = "$major.$minor.$patch"
    Write-Host "New version: $newVersion"
    
    # Update the version in the .csproj file
    $newCsprojContent = $csprojContent -replace '<Version>[^<]+</Version>', "<Version>$newVersion</Version>"
    
    # Write back to file
    Set-Content $csprojPath $newCsprojContent -Encoding UTF8
    
    Write-Host "✅ Successfully updated version from $currentVersion to $newVersion" -ForegroundColor Green
    
    # Also update the description to reflect the new version
    $timestamp = Get-Date -Format "yyyy-MM-dd"
    Write-Host "Build timestamp: $timestamp" -ForegroundColor Cyan
    
} catch {
    Write-Error "Failed to update version: $($_.Exception.Message)"
    exit 1
}
