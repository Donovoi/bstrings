Set-StrictMode -Version Latest

function Enable-BstringsAirgapEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BundleRoot
    )

    $resolvedRoot = [IO.Path]::GetFullPath($BundleRoot)
    $cacheRoot = Join-Path ([IO.Path]::GetTempPath()) 'bstrings-airgap-cache'
    [Environment]::SetEnvironmentVariable('BSTRINGS_AIRGAP_BUNDLE', $resolvedRoot, 'Process')
    [Environment]::SetEnvironmentVariable('DO_NOT_TRACK', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Process')
    [Environment]::SetEnvironmentVariable('DOTNET_SKIP_FIRST_TIME_EXPERIENCE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('HF_DATASETS_OFFLINE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('HF_HOME', (Join-Path $cacheRoot 'huggingface'), 'Process')
    [Environment]::SetEnvironmentVariable('HF_HUB_DISABLE_TELEMETRY', '1', 'Process')
    [Environment]::SetEnvironmentVariable('HF_HUB_OFFLINE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('PIP_DISABLE_PIP_VERSION_CHECK', '1', 'Process')
    [Environment]::SetEnvironmentVariable('PIP_NO_INDEX', '1', 'Process')
    [Environment]::SetEnvironmentVariable('PYTHONDONTWRITEBYTECODE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('PYTHONNOUSERSITE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('TRANSFORMERS_OFFLINE', '1', 'Process')
    [Environment]::SetEnvironmentVariable('UV_OFFLINE', '1', 'Process')

    $noProxy = '127.0.0.1,localhost,::1'
    $deadProxy = 'http://127.0.0.1:9'
    foreach ($name in @('NO_PROXY', 'no_proxy')) {
        [Environment]::SetEnvironmentVariable($name, $noProxy, 'Process')
    }
    foreach ($name in @(
        'ALL_PROXY',
        'HTTPS_PROXY',
        'HTTP_PROXY',
        'all_proxy',
        'https_proxy',
        'http_proxy'
    )) {
        [Environment]::SetEnvironmentVariable($name, $deadProxy, 'Process')
    }
}
