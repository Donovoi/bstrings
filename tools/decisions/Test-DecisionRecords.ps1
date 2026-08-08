[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string]$BaseRevision,
    [string]$HeadRevision = 'HEAD',
    [AllowEmptyString()]
    [string]$PullRequestBody = '',
    [string[]]$ChangedPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$decisionPathPattern = '^docs/architecture/adr-[0-9]{4}-[a-z0-9][a-z0-9-]*\.md$'
$decisionLinkPattern = '(?i)\[[^\]\r\n]+\]\((?<path>docs/architecture/adr-[0-9]{4}-[a-z0-9][a-z0-9-]*\.md)(?:#[^)\s]+)?\)'
$requiredSections = @(
    'Context',
    'Considered options',
    'Decision',
    'Non-negotiable invariants',
    'Acceptance gates',
    'Strongest detractor and resolution',
    'Falsifiers and revisit triggers',
    'Consequences',
    'Primary references'
)

function Stop-DecisionGate {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw "Decision record gate failed: $Message"
}

function Normalize-RepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    $value = $Path.Trim().Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($value) -or $value.IndexOfAny([char[]]"`0`r`n") -ge 0) {
        Stop-DecisionGate "A changed path is empty or contains an invalid character."
    }
    return $value.TrimStart('./')
}

function Get-ChangedRepositoryPaths {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [string]$Base,
        [Parameter(Mandatory = $true)][string]$Head,
        [string[]]$ExplicitPaths,
        [bool]$HasExplicitPaths
    )

    if ($HasExplicitPaths) {
        return @($ExplicitPaths | ForEach-Object { Normalize-RepositoryPath $_ } | Select-Object -Unique)
    }
    if ([string]::IsNullOrWhiteSpace($Base)) {
        return @()
    }

    $output = @(& git -C $Root diff --name-only --diff-filter=ACMR $Base $Head -- 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Stop-DecisionGate "git diff failed for revisions '$Base' and '$Head': $($output -join ' ')"
    }
    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { Normalize-RepositoryPath ([string]$_) } | Select-Object -Unique)
}

function Get-SectionBody {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Heading,
        [Parameter(Mandatory = $true)][string]$DecisionPath
    )

    $escaped = [regex]::Escape($Heading)
    $matches = [regex]::Matches(
        $Content,
        "(?im)^##[ `t]+$escaped[ `t]*$"
    )
    if ($matches.Count -ne 1) {
        Stop-DecisionGate "'$DecisionPath' must contain exactly one '## $Heading' section."
    }
    $bodyMatch = [regex]::Match(
        $Content,
        "(?ims)^##[ `t]+$escaped[ `t]*$\r?\n(?<body>.*?)(?=^##[ `t]+|\z)"
    )
    $body = $bodyMatch.Groups['body'].Value.Trim()
    if ($body.Length -lt 3 -or $body -match '(?im)^\s*(?:TODO|TBD|replace me)\s*\.?\s*$') {
        Stop-DecisionGate "'$DecisionPath' has an empty or placeholder '## $Heading' section."
    }
    return $body
}

function Get-MarkdownLinks {
    param([Parameter(Mandatory = $true)][string]$Content)
    return @([regex]::Matches($Content, '(?m)\[[^\]\r\n]+\]\((?<target>[^)\r\n]+)\)'))
}

function Assert-LocalLinks {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$DecisionFullPath,
        [Parameter(Mandatory = $true)][string]$DecisionPath,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $rootPrefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    $decisionDirectory = Split-Path $DecisionFullPath -Parent
    foreach ($match in Get-MarkdownLinks $Content) {
        $target = $match.Groups['target'].Value.Trim().Trim('<', '>')
        if (
            $target.StartsWith('#', [StringComparison]::Ordinal) -or
            $target -match '^(?i:https?|mailto):'
        ) {
            continue
        }
        $pathPart = $target.Split('#', 2)[0]
        if ([string]::IsNullOrWhiteSpace($pathPart) -or $pathPart.Contains('?')) {
            Stop-DecisionGate "'$DecisionPath' contains an invalid local link '$target'."
        }
        $pathPart = [Uri]::UnescapeDataString($pathPart).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $resolved = [IO.Path]::GetFullPath((Join-Path $decisionDirectory $pathPart))
        if (
            -not $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $resolved)
        ) {
            Stop-DecisionGate "'$DecisionPath' contains a missing or out-of-repository link '$target'."
        }
    }
}

function Assert-DecisionRecord {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$DecisionPath
    )

    if ($DecisionPath -cnotmatch $decisionPathPattern) {
        Stop-DecisionGate "Decision path '$DecisionPath' is not canonical."
    }
    $fullPath = [IO.Path]::GetFullPath(
        (Join-Path $Root $DecisionPath.Replace('/', [IO.Path]::DirectorySeparatorChar))
    )
    if (-not [IO.File]::Exists($fullPath)) {
        Stop-DecisionGate "Required decision record '$DecisionPath' does not exist."
    }
    $item = Get-Item -LiteralPath $fullPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-DecisionGate "Decision record '$DecisionPath' cannot be a link or reparse point."
    }
    if ($item.Length -le 0 -or $item.Length -gt 1024 * 1024) {
        Stop-DecisionGate "Decision record '$DecisionPath' is empty or exceeds 1 MiB."
    }
    $content = Get-Content -LiteralPath $fullPath -Raw -Encoding UTF8
    if ($content.Contains([char]0)) {
        Stop-DecisionGate "Decision record '$DecisionPath' contains a NUL byte."
    }
    # Git may materialize this Markdown with CRLF on Windows runners. Normalize
    # before applying line-anchored policy expressions so validation is
    # identical for LF, CRLF, and legacy CR inputs.
    $content = $content.Replace("`r`n", "`n").Replace("`r", "`n")

    $fileName = [IO.Path]::GetFileName($DecisionPath)
    $number = $fileName.Substring(4, 4)
    if ($content -cnotmatch "(?m)^# ADR-${number}: .+\S[ `t]*$") {
        Stop-DecisionGate "'$DecisionPath' must start with '# ADR-${number}: <title>'."
    }

    $sections = @{}
    foreach ($heading in $requiredSections) {
        $sections[$heading] = Get-SectionBody $content $heading $DecisionPath
    }
    if ($content -notmatch '(?im)^\s*-\s*\*\*Status:\*\*\s*Accepted\b') {
        Stop-DecisionGate "'$DecisionPath' must have Accepted status before it can gate implementation."
    }
    foreach ($metadata in @('Date', 'Scope', 'Decision type', 'Review method', 'Perspectives')) {
        if ($content -notmatch ('(?im)^\s*-\s*\*\*' + [regex]::Escape($metadata) + ':\*\*\s*\S')) {
            Stop-DecisionGate "'$DecisionPath' is missing '$metadata' review metadata."
        }
    }
    $reviewMethodMatch = [regex]::Match(
        $content,
        '(?ims)^\s*-\s*\*\*Review method:\*\*\s*(?<value>.*?)(?=^\s*-\s*\*\*|^##|\z)'
    )
    if ($reviewMethodMatch.Groups['value'].Value -notmatch '\[[^\]]+\]\([^)]+\)') {
        Stop-DecisionGate "'$DecisionPath' review method must link the governing decision policy."
    }
    $perspectiveMatch = [regex]::Match(
        $content,
        '(?ims)^\s*-\s*\*\*Perspectives:\*\*\s*(?<value>.*?)(?=^\s*-\s*\*\*|^##|\z)'
    )
    $perspectiveValue = $perspectiveMatch.Groups['value'].Value
    $perspectiveSignals = @(
        'advocate', 'audit', 'detractor', 'evidence', 'performance',
        'operations', 'research', 'runtime', 'vendor'
    ) | Where-Object { $perspectiveValue -match [regex]::Escape($_) }
    if (@($perspectiveSignals).Count -lt 3) {
        Stop-DecisionGate "'$DecisionPath' must name at least three independent review perspectives."
    }
    if ($sections['Acceptance gates'] -notmatch '(?i)measur|benchmark') {
        Stop-DecisionGate "'$DecisionPath' acceptance gates must pre-register a measurement or benchmark gate."
    }
    if ($sections['Primary references'] -notmatch '(?i)\[[^\]]+\]\(https://[^)\s]+\)') {
        Stop-DecisionGate "'$DecisionPath' primary references must cite at least one HTTPS source."
    }
    if (
        $sections['Primary references'] -notmatch
            '\[[^\]]+\]\((?!https?://|mailto:|#)[^)]+\)'
    ) {
        Stop-DecisionGate "'$DecisionPath' primary references must link at least one repository artifact."
    }
    Assert-LocalLinks $Root $fullPath $DecisionPath $content
}

$root = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not [IO.Directory]::Exists($root)) {
    Stop-DecisionGate "Repository root was not found: '$root'."
}

$changedPaths = Get-ChangedRepositoryPaths `
    $root `
    $BaseRevision `
    $HeadRevision `
    $ChangedPath `
    $PSBoundParameters.ContainsKey('ChangedPath')
$changedDecisions = @($changedPaths | Where-Object { $_ -cmatch $decisionPathPattern })

$ordinaryDeclared = $PullRequestBody -match
    '(?im)^\s*-\s*\[[xX]\]\s*Ordinary change(?:\s|—|-|$)'
$highLevelDeclared = $PullRequestBody -match
    '(?im)^\s*-\s*\[[xX]\]\s*High-level decision(?:\s|—|-|$)'
if ($ordinaryDeclared -and $highLevelDeclared) {
    Stop-DecisionGate 'The pull request declares both ordinary and high-level decision impact.'
}
if ($ordinaryDeclared -and $changedDecisions.Count -gt 0) {
    Stop-DecisionGate 'The pull request declares an ordinary change but changes a numbered ADR.'
}

$linkedDecisions = @(
    [regex]::Matches($PullRequestBody, $decisionLinkPattern) |
        ForEach-Object { $_.Groups['path'].Value } |
        Select-Object -Unique
)
if ($highLevelDeclared -and $linkedDecisions.Count -eq 0) {
    Stop-DecisionGate 'A high-level decision declaration requires a canonical Markdown ADR link.'
}
if (
    -not [string]::IsNullOrWhiteSpace($PullRequestBody) -and
    $changedDecisions.Count -gt 0
) {
    foreach ($decision in $changedDecisions) {
        if ($linkedDecisions -cnotcontains $decision) {
            Stop-DecisionGate "Changed decision record '$decision' is not linked from the pull request body."
        }
    }
}

$recordsToValidate = @($changedDecisions + $linkedDecisions | Select-Object -Unique)
if (-not $highLevelDeclared -and $recordsToValidate.Count -eq 0) {
    Write-Host 'Decision record gate passed: ordinary change; no ADR required.'
    return
}
foreach ($decision in $recordsToValidate) {
    Assert-DecisionRecord $root $decision
}
Write-Host "Decision record gate passed: validated $($recordsToValidate.Count) accepted ADR(s)."
