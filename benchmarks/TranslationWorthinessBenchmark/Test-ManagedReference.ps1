[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$python = if (Test-Path -LiteralPath 'C:\Python314\python.exe' -PathType Leaf) {
    'C:\Python314\python.exe'
}
else {
    'python'
}
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ('bstrings-worthiness-managed-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null

try {
    $model = Join-Path $temporaryRoot 'model.bin'
    $pythonPredictions = Join-Path $temporaryRoot 'python.jsonl'
    $managedPredictions = Join-Path $temporaryRoot 'managed.jsonl'
    $manifest = Join-Path $temporaryRoot 'manifest.json'
    & $python (Join-Path $root 'tools\enrichment\train_translation_worthiness.py') `
        --corpus (Join-Path $root 'tools\enrichment\translation_worthiness_synthetic_v1.jsonl') `
        --splits (Join-Path $root 'tools\enrichment\translation_worthiness_splits_v1.json') `
        --ablation combined `
        --model-output $model `
        --predictions-output $pythonPredictions `
        --manifest-output $manifest
    if ($LASTEXITCODE -ne 0) {
        throw "Python reference training failed with exit code $LASTEXITCODE."
    }

    & dotnet run --configuration Release `
        --project (Join-Path $PSScriptRoot 'TranslationWorthinessBenchmark.csproj') `
        -- `
        --model $model `
        --manifest $manifest `
        --corpus (Join-Path $root 'tools\enrichment\translation_worthiness_synthetic_v1.jsonl') `
        --output $managedPredictions
    if ($LASTEXITCODE -ne 0) {
        throw "Managed reference failed with exit code $LASTEXITCODE."
    }

    $pythonRows = @(Get-Content -LiteralPath $pythonPredictions | ForEach-Object { $_ | ConvertFrom-Json })
    $managedRows = @(Get-Content -LiteralPath $managedPredictions | ForEach-Object { $_ | ConvertFrom-Json })
    if ($pythonRows.Count -ne $managedRows.Count) {
        throw 'Managed prediction cardinality does not match the Python reference.'
    }
    for ($index = 0; $index -lt $pythonRows.Count; $index++) {
        if (
            [string]$pythonRows[$index].id -cne [string]$managedRows[$index].id -or
            [string]$pythonRows[$index].decision -cne [string]$managedRows[$index].decision
        ) {
            throw "Managed prediction identity or decision differs at ordinal $index."
        }
        $expected = [double]$pythonRows[$index].score
        $actual = [double]$managedRows[$index].score
        $tolerance = [Math]::Max(1e-12, [Math]::Abs($expected) * 1e-12)
        if ([Math]::Abs($expected - $actual) -gt $tolerance) {
            throw "Managed prediction score differs at ordinal $index."
        }
    }

    $corpusCopy = Join-Path $temporaryRoot 'corpus-copy.jsonl'
    [IO.File]::Copy(
        (Join-Path $root 'tools\enrichment\translation_worthiness_synthetic_v1.jsonl'),
        $corpusCopy
    )
    $corpusCopyHash = (Get-FileHash -LiteralPath $corpusCopy -Algorithm SHA256).Hash
    & dotnet run --no-build --configuration Release `
        --project (Join-Path $PSScriptRoot 'TranslationWorthinessBenchmark.csproj') `
        -- `
        --model $model `
        --manifest $manifest `
        --corpus $corpusCopy `
        --output $corpusCopy
    if ($LASTEXITCODE -eq 0) {
        throw 'Managed reference accepted a prediction output that aliases its corpus input.'
    }
    if (
        (Get-FileHash -LiteralPath $corpusCopy -Algorithm SHA256).Hash -cne
        $corpusCopyHash
    ) {
        throw 'Managed reference changed its corpus while rejecting an aliased output.'
    }

    Write-Host "Managed translation-worthiness reference matched $($managedRows.Count) frozen predictions."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        [IO.Directory]::Delete($temporaryRoot, $true)
    }
}
