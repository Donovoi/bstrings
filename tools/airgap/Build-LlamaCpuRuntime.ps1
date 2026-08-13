[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,
    [Parameter(Mandatory = $true)]
    [string]$BuildDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$ComponentLockPath,
    [string]$VisualCppRuntimeDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ComponentLockPath)) {
    $ComponentLockPath = Join-Path $PSScriptRoot 'offline-components.lock.json'
}

function Resolve-ExistingFile([string]$Path, [string]$Name) {
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

function Resolve-ExistingDirectory([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Name was not found: $Path"
    }
    $resolved = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
    $item = Get-Item -LiteralPath $resolved -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Name is a link or reparse point: $resolved"
    }
    return $resolved
}

function Assert-NoReparsePoints([string]$Root, [string]$Name) {
    foreach ($item in @(Get-Item -LiteralPath $Root -Force) + @(
        Get-ChildItem -LiteralPath $Root -Recurse -Force
    )) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Name contains a link or reparse point: $($item.FullName)"
        }
    }
}

function Resolve-SafeChildFile([string]$Root, [string]$RelativePath, [string]$Name) {
    if (
        [string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath -match '(^|[\\/])\.\.([\\/]|$)'
    ) {
        throw "$Name must be a safe relative path: '$RelativePath'"
    }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath))
    if (-not $candidate.StartsWith(
        $rootFull + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "$Name escapes its source root: $candidate"
    }
    return Resolve-ExistingFile $candidate $Name
}

function Assert-ExactFile(
    [string]$Path,
    [long]$ExpectedBytes,
    [string]$ExpectedSha256,
    [string]$Name
) {
    $resolved = Resolve-ExistingFile $Path $Name
    $item = Get-Item -LiteralPath $resolved
    if ($item.Length -ne $ExpectedBytes) {
        throw "$Name byte length mismatch: expected $ExpectedBytes, found $($item.Length): $resolved"
    }
    $hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $ExpectedSha256) {
        throw "$Name SHA-256 mismatch: expected $ExpectedSha256, found ${hash}: $resolved"
    }
    return $resolved
}

function Get-CMakeSetValue([string]$Path, [string]$Name) {
    $escaped = [Regex]::Escape($Name)
    $pattern = '^set\(' + $escaped + ' "(?<value>.*)"\)$'
    $match = Select-String -LiteralPath $Path -Pattern $pattern |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "CMake did not record $Name in $Path"
    }
    return [string]$match.Matches[0].Groups['value'].Value
}

function Get-PeImports([string]$Dumpbin, [string]$Path) {
    $output = & $Dumpbin /nologo /dependents $Path 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "dumpbin dependency inspection failed for ${Path}: $($output | Out-String)"
    }
    return @(
        $output |
            ForEach-Object { $_.ToString().Trim() } |
            Where-Object { $_ -match '^[A-Za-z0-9_.-]+\.dll$' } |
            Sort-Object -Unique
    )
}

$lockPath = Resolve-ExistingFile $ComponentLockPath 'Offline component lock'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 2 -or $lock.profile -ne 'windows-x64-offline-v3') {
    throw "Unsupported offline component lock schema or profile: $lockPath"
}
$component = $lock.components.llamaCpp
if ($null -eq $component -or $component.archiveType -ne 'source-zip') {
    throw 'The component lock must describe a pinned llama.cpp source ZIP.'
}
if (
    [string]$component.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
    [string]$component.sourceTag -ne [string]$component.version
) {
    throw 'The llama.cpp source tag and full commit pin are invalid.'
}

$source = Resolve-ExistingDirectory $SourceDirectory 'Pinned llama.cpp source directory'
if ([IO.Path]::GetFileName($source) -cne [string]$component.sourceRoot) {
    throw "Pinned llama.cpp source root mismatch: expected '$($component.sourceRoot)', found '$([IO.Path]::GetFileName($source))'."
}
Assert-NoReparsePoints $source 'Pinned llama.cpp source directory'
$null = Resolve-SafeChildFile $source 'CMakeLists.txt' 'llama.cpp root CMakeLists.txt'

$build = [IO.Path]::GetFullPath($BuildDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar
)
if (Test-Path -LiteralPath $build) {
    throw "BuildDirectory must not already exist: $build"
}
if (Test-Path -LiteralPath $output) {
    throw "OutputDirectory must not already exist: $output"
}
foreach ($candidate in @($build, $output)) {
    if ($candidate.StartsWith(
        $source.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "Build and output directories must not be placed inside the pinned source tree: $candidate"
    }
}

$noticeSpecs = @($component.license) + @($component.notices)
$noticeSources = @()
foreach ($notice in $noticeSpecs) {
    if (
        [string]$notice.sourcePath -eq '' -or
        [string]$notice.path -notmatch '^notices/llama\.cpp/[A-Za-z0-9._-]+$' -or
        [long]$notice.bytes -lt 1 -or
        [string]$notice.sha256 -notmatch '^[0-9a-f]{64}$'
    ) {
        throw 'The llama.cpp lock contains an invalid source notice record.'
    }
    $sourceNotice = Resolve-SafeChildFile `
        $source `
        ([string]$notice.sourcePath) `
        "llama.cpp notice $($notice.sourcePath)"
    $null = Assert-ExactFile `
        $sourceNotice `
        ([long]$notice.bytes) `
        ([string]$notice.sha256) `
        "llama.cpp notice $($notice.sourcePath)"
    $noticeSources += [pscustomobject]@{
        spec = $notice
        source = $sourceNotice
    }
}

$vswhereCandidates = @(
    @(
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft Visual Studio\Installer\vswhere.exe')
    ) | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        (Test-Path -LiteralPath $_ -PathType Leaf)
    }
)
if ($vswhereCandidates.Count -eq 0) {
    throw 'vswhere.exe is required to locate a licensed Visual Studio C++ build toolchain.'
}
$vswhere = Resolve-ExistingFile $vswhereCandidates[0] 'vswhere'
$vsJson = & $vswhere `
    -latest `
    -products '*' `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -format json
if ($LASTEXITCODE -ne 0) {
    throw "vswhere failed with exit code $LASTEXITCODE."
}
$vsInstances = @($vsJson | ConvertFrom-Json)
if ($vsInstances.Count -ne 1) {
    throw 'A complete Visual Studio C++ x64 build toolchain was not found.'
}
$vs = $vsInstances[0]
$vsPath = Resolve-ExistingDirectory ([string]$vs.installationPath) 'Visual Studio installation'
$vsMajor = ([string]$vs.installationVersion).Split('.')[0]

$cmakeCommand = Get-Command cmake.exe -ErrorAction Stop
$cmake = Resolve-ExistingFile $cmakeCommand.Source 'CMake executable'
$cmakeHelp = & $cmake --help 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw 'cmake --help failed.'
}
$generatorMatch = [Regex]::Match(
    $cmakeHelp,
    "Visual Studio $([Regex]::Escape($vsMajor)) [0-9]{4}"
)
if (-not $generatorMatch.Success) {
    throw "CMake does not advertise a generator for installed Visual Studio major version $vsMajor."
}
$generator = $generatorMatch.Value
$cmakeVersionOutput = (& $cmake --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $cmakeVersionOutput -notmatch '^cmake version (?<version>[0-9.]+)') {
    throw "Could not determine the CMake version: $cmakeVersionOutput"
}
$cmakeVersion = $Matches.version

$buildFlags = @($component.build.flags | ForEach-Object { [string]$_ })
if ($buildFlags.Count -eq 0 -or $buildFlags.Count -ne @($buildFlags | Sort-Object -Unique).Count) {
    throw 'The llama.cpp build flag lock is empty or contains duplicate flags.'
}
foreach ($requiredFlag in @(
    'GGML_OPENMP=OFF',
    'GGML_NATIVE=OFF',
    'GGML_BACKEND_DL=ON',
    'GGML_CPU_ALL_VARIANTS=ON',
    'GGML_CPU_KLEIDIAI=OFF',
    'GGML_RPC=OFF',
    'GGML_CCACHE=OFF',
    'LLAMA_BUILD_TOOLS=ON',
    'LLAMA_BUILD_SERVER=ON',
    'LLAMA_BUILD_UI=OFF',
    'LLAMA_USE_PREBUILT_UI=OFF',
    'LLAMA_OPENSSL=OFF',
    'LLAMA_BUILD_BORINGSSL=OFF',
    'LLAMA_BUILD_LIBRESSL=OFF',
    'LLAMA_SUBPROCESS=OFF',
    'MTMD_VIDEO=OFF'
)) {
    if ($requiredFlag -notin $buildFlags) {
        throw "The llama.cpp build lock is missing required offline/redistribution flag '$requiredFlag'."
    }
}

$cmakeArguments = @(
    '-S', $source,
    '-B', $build,
    '-G', $generator,
    '-A', ([string]$component.build.architecture)
)
$cmakeArguments += @($buildFlags | ForEach-Object { "-D$_" })
$cmakeArguments += @(
    "-DLLAMA_BUILD_COMMIT=$($component.sourceCommit)",
    "-DLLAMA_BUILD_NUMBER=$(([string]$component.version).TrimStart('b'))",
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded$<$<CONFIG:Debug>:Debug>DLL'
)

$guardedEnvironment = @{
    HTTP_PROXY = 'http://127.0.0.1:9'
    HTTPS_PROXY = 'http://127.0.0.1:9'
    ALL_PROXY = 'http://127.0.0.1:9'
    NO_PROXY = ''
    HF_HUB_OFFLINE = '1'
    TRANSFORMERS_OFFLINE = '1'
    HF_DATASETS_OFFLINE = '1'
}
$savedEnvironment = @{}
foreach ($name in $guardedEnvironment.Keys) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $guardedEnvironment[$name], 'Process')
}
try {
    Write-Host "Configuring pinned llama.cpp $($component.version) at $($component.sourceCommit)."
    & $cmake @cmakeArguments 2>&1 | Out-Host
    $configureExitCode = $LASTEXITCODE
    if ($configureExitCode -ne 0) {
        throw "Pinned llama.cpp CMake configuration failed with exit code $configureExitCode."
    }
    $serverProjects = @(Get-ChildItem -LiteralPath $build -Filter 'llama-server.vcxproj' -File -Recurse)
    if ($serverProjects.Count -ne 1) {
        throw "Configured build must expose exactly one llama-server target; found $($serverProjects.Count)."
    }
    $cachePath = Resolve-ExistingFile (Join-Path $build 'CMakeCache.txt') 'llama.cpp CMake cache'
    foreach ($flag in $buildFlags) {
        $parts = $flag.Split('=', 2)
        $record = Select-String -LiteralPath $cachePath -Pattern "^$([Regex]::Escape($parts[0])):[^=]+=$([Regex]::Escape($parts[1]))$"
        if ($null -eq $record) {
            throw "Configured llama.cpp cache does not contain locked flag '$flag'."
        }
    }

    Write-Host "Building only the $($component.build.target) target."
    & $cmake `
        --build $build `
        --config ([string]$component.build.configuration) `
        --target ([string]$component.build.target) `
        --parallel 2>&1 | Out-Host
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        throw "Pinned llama.cpp target build failed with exit code $buildExitCode."
    }
}
finally {
    foreach ($name in $guardedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process')
    }
}

$configuration = [string]$component.build.configuration
$bin = Resolve-ExistingDirectory (Join-Path $build "bin\$configuration") 'llama.cpp build output'
$fixedRuntimeNames = @(
    [string]$component.executable,
    'llama-server-impl.dll',
    'llama-common.dll',
    'llama.dll',
    'mtmd.dll',
    'ggml.dll',
    'ggml-base.dll'
)
$backendNames = @($component.build.requiredCpuBackends | ForEach-Object { [string]$_ })
$expectedRuntimeNames = @($fixedRuntimeNames + $backendNames)
$actualRuntimeFiles = @(
    Get-ChildItem -LiteralPath $bin -File |
        Where-Object { $_.Extension -in @('.exe', '.dll') } |
        Sort-Object Name
)
$actualRuntimeNames = @($actualRuntimeFiles | ForEach-Object { $_.Name })
$missingRuntime = @($expectedRuntimeNames | Where-Object { $_ -notin $actualRuntimeNames })
$unexpectedRuntime = @($actualRuntimeNames | Where-Object { $_ -notin $expectedRuntimeNames })
if ($missingRuntime.Count -ne 0 -or $unexpectedRuntime.Count -ne 0) {
    throw "llama.cpp runtime file set mismatch. Missing: $($missingRuntime -join ', '); unexpected: $($unexpectedRuntime -join ', ')."
}

$forbiddenBuildFiles = @(
    Get-ChildItem -LiteralPath $build -Recurse -File -Force | Where-Object {
        $_.Name -like 'libomp140*.dll' -or
        $_.FullName -match '(?i)(^|[\\/])debug_nonredist([\\/]|$)'
    }
)
if ($forbiddenBuildFiles.Count -ne 0) {
    throw "llama.cpp build contains a forbidden OpenMP/debug_nonredist artifact: $($forbiddenBuildFiles.FullName -join ', ')"
}

$compilerDescriptionFiles = @(Get-ChildItem -LiteralPath (Join-Path $build 'CMakeFiles') -Filter 'CMakeCXXCompiler.cmake' -File -Recurse)
if ($compilerDescriptionFiles.Count -ne 1) {
    throw "Expected one CMake C++ compiler record; found $($compilerDescriptionFiles.Count)."
}
$compilerRecord = $compilerDescriptionFiles[0].FullName
$compilerPath = Resolve-ExistingFile (Get-CMakeSetValue $compilerRecord 'CMAKE_CXX_COMPILER') 'C++ compiler'
$compilerId = Get-CMakeSetValue $compilerRecord 'CMAKE_CXX_COMPILER_ID'
$compilerVersion = Get-CMakeSetValue $compilerRecord 'CMAKE_CXX_COMPILER_VERSION'
$dumpbin = Resolve-ExistingFile (Join-Path (Split-Path -Parent $compilerPath) 'dumpbin.exe') 'dumpbin'

$runtimeNameSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $expectedRuntimeNames) { $null = $runtimeNameSet.Add($name) }
$vcRuntimeSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @($lock.runtimeDlls | ForEach-Object { [string]$_ })) { $null = $vcRuntimeSet.Add($name) }
$systemImportSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @($component.build.allowedSystemImports | ForEach-Object { [string]$_ })) { $null = $systemImportSet.Add($name) }
$systemPatterns = @($component.build.allowedSystemImportPatterns | ForEach-Object { [Regex]::new([string]$_, [Text.RegularExpressions.RegexOptions]::IgnoreCase) })

$importsByName = @{}
$runtimeInventory = @()
foreach ($file in $actualRuntimeFiles) {
    $imports = @(Get-PeImports $dumpbin $file.FullName)
    foreach ($import in $imports) {
        $allowedPattern = @($systemPatterns | Where-Object { $_.IsMatch($import) }).Count -gt 0
        if (
            -not $runtimeNameSet.Contains($import) -and
            -not $vcRuntimeSet.Contains($import) -and
            -not $systemImportSet.Contains($import) -and
            -not $allowedPattern
        ) {
            throw "Unreviewed PE import '$import' in $($file.Name)."
        }
    }
    $importsByName[$file.Name] = $imports
    $runtimeInventory += [ordered]@{
        name = $file.Name
        bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        role = if ($file.Name -eq [string]$component.executable) {
            'server'
        } elseif ($file.Name -in $backendNames) {
            'cpu-backend'
        } else {
            'runtime-dependency'
        }
        imports = $imports
    }
}

$reachable = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$queue = [Collections.Generic.Queue[string]]::new()
$queue.Enqueue([string]$component.executable)
while ($queue.Count -gt 0) {
    $name = $queue.Dequeue()
    if (-not $reachable.Add($name)) { continue }
    foreach ($import in @($importsByName[$name])) {
        if ($runtimeNameSet.Contains($import)) {
            $queue.Enqueue($import)
        }
    }
}
$unreachableFixedRuntime = @($fixedRuntimeNames | Where-Object { -not $reachable.Contains($_) })
if ($unreachableFixedRuntime.Count -ne 0) {
    throw "The llama-server recursive PE closure does not reach: $($unreachableFixedRuntime -join ', ')."
}

[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.File]::WriteAllText((Join-Path $output '.incomplete'), "llama.cpp runtime staging did not complete.`n")
foreach ($file in $actualRuntimeFiles) {
    [IO.File]::Copy($file.FullName, (Join-Path $output $file.Name), $false)
}
foreach ($noticeSource in $noticeSources) {
    $relative = ([string]$noticeSource.spec.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
    $destination = [IO.Path]::GetFullPath((Join-Path $output $relative))
    if (-not $destination.StartsWith(
        $output + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase
    )) {
        throw "llama.cpp notice destination escapes runtime staging: $destination"
    }
    [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
    [IO.File]::Copy([string]$noticeSource.source, $destination, $false)
    $null = Assert-ExactFile `
        $destination `
        ([long]$noticeSource.spec.bytes) `
        ([string]$noticeSource.spec.sha256) `
        "staged llama.cpp notice $relative"
}

$runtimeStager = Resolve-ExistingFile `
    (Join-Path $PSScriptRoot 'Stage-VisualCppRuntime.ps1') `
    'Visual C++ runtime staging helper'
$runtimeParameters = @{
    ComponentLockPath = $lockPath
    DestinationDirectory = @($output)
}
if (-not [string]::IsNullOrWhiteSpace($VisualCppRuntimeDirectory)) {
    $runtimeParameters.VisualCppRuntimeDirectory = $VisualCppRuntimeDirectory
}
$vcRuntimeResult = @(& $runtimeStager @runtimeParameters)
if ($vcRuntimeResult.Count -ne 1 -or @($vcRuntimeResult[0].destinations).Count -ne 1) {
    throw 'Visual C++ runtime staging did not verify the custom llama.cpp runtime.'
}

$originalPath = [Environment]::GetEnvironmentVariable('PATH', 'Process')
try {
    [Environment]::SetEnvironmentVariable(
        'PATH',
        "$output$([IO.Path]::PathSeparator)$(Join-Path $env:SystemRoot 'System32')",
        'Process'
    )
    $server = Join-Path $output ([string]$component.executable)
    $versionOutput = (& $server --version 2>&1 | Out-String).Trim()
    if (
        $LASTEXITCODE -ne 0 -or
        $versionOutput.IndexOf([string]$component.sourceCommit, [StringComparison]::Ordinal) -lt 0 -or
        $versionOutput.IndexOf(([string]$component.version).TrimStart('b'), [StringComparison]::Ordinal) -lt 0
    ) {
        throw "Custom llama.cpp reduced-PATH version probe failed: $versionOutput"
    }
    $helpOutput = (& $server --help 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $helpOutput -notmatch '(?m)^--offline\s+') {
        throw 'Custom llama.cpp does not expose the required --offline network guard.'
    }
}
finally {
    [Environment]::SetEnvironmentVariable('PATH', $originalPath, 'Process')
}

$noticeInventory = foreach ($noticeSource in $noticeSources) {
    [ordered]@{
        sourcePath = [string]$noticeSource.spec.sourcePath
        path = [string]$noticeSource.spec.path
        bytes = [long]$noticeSource.spec.bytes
        sha256 = [string]$noticeSource.spec.sha256
    }
}
$cache = Get-Content -LiteralPath (Join-Path $build 'CMakeCache.txt')
$sdkRecord = $cache | Where-Object { $_ -match '^CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION:' } | Select-Object -First 1
$sdkVersion = if ($null -ne $sdkRecord) { ($sdkRecord -split '=', 2)[1] } else { $null }
$provenance = [ordered]@{
    schemaVersion = 1
    component = 'llama.cpp'
    version = [string]$component.version
    source = [ordered]@{
        tag = [string]$component.sourceTag
        commit = [string]$component.sourceCommit
        archiveUrl = [string]$component.url
        archiveBytes = [long]$component.bytes
        archiveSha256 = [string]$component.sha256
    }
    build = [ordered]@{
        configuration = $configuration
        target = [string]$component.build.target
        architecture = [string]$component.build.architecture
        generator = $generator
        cmakeVersion = $cmakeVersion
        visualStudio = [ordered]@{
            displayName = [string]$vs.displayName
            installationVersion = [string]$vs.installationVersion
        }
        compiler = [ordered]@{
            id = $compilerId
            version = $compilerVersion
            fileName = [IO.Path]::GetFileName($compilerPath)
        }
        windowsSdkVersion = $sdkVersion
        flags = $buildFlags
        networkGuarded = $true
    }
    runtimeFiles = $runtimeInventory
    visualCppRuntime = @($vcRuntimeResult[0].files)
    notices = $noticeInventory
    excluded = @(
        'OpenMP runtime: GGML_OPENMP=OFF; libomp140*.dll and debug_nonredist are forbidden.',
        'Network-fetched UI: LLAMA_BUILD_UI=OFF and LLAMA_USE_PREBUILT_UI=OFF.',
        'OpenSSL, BoringSSL, and LibreSSL: all corresponding build flags are OFF.',
        'KleidiAI FetchContent path: GGML_CPU_KLEIDIAI=OFF.',
        'Subprocess/video path: LLAMA_SUBPROCESS=OFF and MTMD_VIDEO=OFF.'
    )
}
$provenancePath = Join-Path $output 'llama-build-provenance.json'
$provenance | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $provenancePath -Encoding utf8

$scopeNotice = @"
# llama.cpp b10248 runtime notice scope

This runtime was built from tag $($component.sourceTag) at commit
$($component.sourceCommit) with OpenMP, network-fetched UI assets, TLS
libraries, KleidiAI FetchContent, RPC, and subprocess/video support disabled.

The exact upstream files that carry applicable licenses and borrowed-code
notices are preserved byte-for-byte in this directory. They cover llama.cpp,
cpp-httplib, nlohmann JSON (including the Loitsch and Hoehrmann MIT
attributions), base64.hpp (Unlicense), miniaudio (MIT-0 selection), stb_image
(MIT selection), Mozilla's llamafile matrix code, the YaRN implementation, and
the ggllm-derived vocabulary code. See llama-build-provenance.json for their
source paths, byte lengths, SHA-256 values, compiler, flags, and PE import audit.
"@
[IO.File]::WriteAllText(
    (Join-Path $output 'notices\llama.cpp\NOTICE-SCOPE.md'),
    $scopeNotice.Replace("`r`n", "`n"),
    [Text.UTF8Encoding]::new($false)
)

Assert-NoReparsePoints $output 'Custom llama.cpp runtime'
$remainingForbidden = @(
    Get-ChildItem -LiteralPath $output -Recurse -File -Force | Where-Object {
        $_.Name -like 'libomp140*.dll' -or
        $_.FullName -match '(?i)(^|[\\/])debug_nonredist([\\/]|$)'
    }
)
if ($remainingForbidden.Count -ne 0) {
    throw "Staged llama.cpp runtime contains a forbidden artifact: $($remainingForbidden.FullName -join ', ')"
}
[IO.File]::Delete((Join-Path $output '.incomplete'))

[pscustomobject]@{
    sourceDirectory = $source
    buildDirectory = $build
    outputDirectory = $output
    executable = Join-Path $output ([string]$component.executable)
    provenance = $provenancePath
    files = @(
        Get-ChildItem -LiteralPath $output -Recurse -File |
            ForEach-Object { $_.FullName.Substring($output.Length + 1).Replace('\', '/') }
    )
}
