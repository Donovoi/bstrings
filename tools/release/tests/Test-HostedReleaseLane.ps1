[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$workflowPath = Join-Path $repoRoot '.github\workflows\dotnet-desktop.yml'
$workflow = [IO.File]::ReadAllText($workflowPath)
$draftWorkflowPath = Join-Path $repoRoot '.github\workflows\publish-windows-release.yml'
$draftWorkflow = [IO.File]::ReadAllText($draftWorkflowPath)

function Assert-Matches([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -notmatch $Pattern) {
        throw $Message
    }
}

function Assert-DoesNotMatch([string]$Text, [string]$Pattern, [string]$Message) {
    if ($Text -match $Pattern) {
        throw $Message
    }
}

function Get-Step([string]$Name) {
    $escapedName = [Regex]::Escape($Name)
    $match = [Regex]::Match(
        $workflow,
        "(?ms)^      - name: $escapedName\r?\n(?<body>.*?)(?=^      - name: |^  [A-Za-z0-9_-]+:|\z)"
    )
    if (-not $match.Success) {
        throw "Workflow step not found: $Name"
    }
    return $match.Value
}

$dispatchPattern = '(?ms)^  workflow_dispatch:\r?\n    inputs:\r?\n      full_offline:\r?\n        description: .+\r?\n        required: false\r?\n        type: boolean\r?\n        default: false$'
Assert-Matches $workflow $dispatchPattern 'workflow_dispatch must define full_offline as a default-false boolean.'

$offlineSteps = @(
    'Restore exact default-branch offline component cache'
    'Build the complete highest-quality offline bundle'
    'Revalidate the warmed offline cache without network fallback'
    'Verify and split the offline release packs'
    'Upload complete offline split packs'
)
$fullOfflineCondition = "github.event_name == 'workflow_dispatch' && inputs.full_offline"
foreach ($stepName in $offlineSteps) {
    $step = Get-Step $stepName
    Assert-Matches $step "startsWith\(github\.ref, 'refs/tags/v'\)" "$stepName must always run for a version tag."
    Assert-Matches $step ([Regex]::Escape($fullOfflineCondition)) "$stepName must run only for an explicitly requested manual full-offline lane."
}

$restore = Get-Step 'Restore exact default-branch offline component cache'
Assert-Matches $restore 'uses: actions/cache/restore@v5' 'The hosted lane must use restore-only cache behavior.'
Assert-DoesNotMatch $restore '(?m)^\s+restore-keys:' 'Offline cache restoration must not use prefix fallback.'
Assert-DoesNotMatch $workflow '(?m)^\s+uses: actions/cache@v5\s*$' 'The workflow must not use the combined restore-and-save cache action.'

$save = Get-Step 'Save validated default-branch offline component cache'
Assert-Matches $save 'uses: actions/cache/save@v5' 'The hosted preparation lane must use an explicit cache save.'
Assert-Matches $save "github\.event_name == 'workflow_dispatch'" 'Only an explicit manual dispatch may save the shared cache.'
Assert-Matches $save "github\.ref == 'refs/heads/master'" 'Only the default branch may save the shared offline cache.'
Assert-Matches $save 'inputs\.full_offline' 'Only an explicit full-offline dispatch may save the shared cache.'
Assert-Matches $save "steps\.offline-cache\.outputs\.cache-hit != 'true'" 'An exact cache hit must not be written again.'
Assert-Matches $save 'key: \$\{\{ steps\.offline-cache\.outputs\.cache-primary-key \}\}' 'Cache save must use the exact restore primary key.'

$validationIndex = $workflow.IndexOf('- name: Revalidate the warmed offline cache without network fallback', [StringComparison]::Ordinal)
$saveIndex = $workflow.IndexOf('- name: Save validated default-branch offline component cache', [StringComparison]::Ordinal)
if ($validationIndex -lt 0 -or $saveIndex -le $validationIndex) {
    throw 'The shared offline cache must be saved only after ValidateOnly re-hashes all component bytes.'
}

foreach ($cacheStep in @($restore, $save)) {
    Assert-Matches $cacheStep '\$\{\{ runner\.temp \}\}\\bstrings-offline\\downloads' 'The cache must contain only component downloads.'
    Assert-Matches $cacheStep '\$\{\{ runner\.temp \}\}\\bstrings-offline\\ocr-downloads' 'The cache must contain only OCR component downloads.'
    Assert-DoesNotMatch $cacheStep '(publish/offline|release-packs|profile-acceptance|evidence)' 'Compiled bundles and acceptance evidence must never enter the shared cache.'
}

$acceptanceStart = $workflow.IndexOf("  profile-acceptance:", [StringComparison]::Ordinal)
$releaseStart = $workflow.IndexOf("  release:", $acceptanceStart, [StringComparison]::Ordinal)
if ($acceptanceStart -lt 0 -or $releaseStart -le $acceptanceStart) {
    throw 'Could not locate the self-hosted profile-acceptance job.'
}
$acceptance = $workflow.Substring($acceptanceStart, $releaseStart - $acceptanceStart)
Assert-DoesNotMatch $acceptance 'actions/cache' 'Self-hosted acceptance must remain cold and independent of the hosted cache.'
Assert-Matches $acceptance "if: startsWith\(github\.ref, 'refs/tags/v'\)" 'Self-hosted acceptance must continue to run for every version tag.'

Assert-Matches `
    $draftWorkflow `
    'this equal-version master run is ordinary CI and will not stage another release' `
    'An equal-version master build with an existing release must be an explicit no-op.'
Assert-Matches `
    $draftWorkflow `
    '(?s)\[string\]\$tagRef\.object\.sha -cne \$env:RELEASE_SHA.*?"stage=false" >> \$env:GITHUB_OUTPUT.*?return' `
    'The draft workflow must stop before staging when the current version belongs to an earlier commit.'
Assert-Matches `
    $draftWorkflow `
    'Tag \$tag exists without a release and does not identify the tested commit' `
    'A conflicting orphan tag must remain a hard failure.'
Assert-Matches `
    $draftWorkflow `
    'Existing draft \$tag has unexpected assets' `
    'An equal-version no-op must still reject a mutated preliminary draft.'
Assert-Matches `
    $draftWorkflow `
    'Published release \$tag must report immutable state' `
    'An equal-version no-op must still require an immutable published release.'

Write-Host 'Hosted release-lane policy tests passed.'
