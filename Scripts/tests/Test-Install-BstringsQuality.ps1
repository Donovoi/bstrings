[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerPath = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\Install-BstringsQuality.ps1')
)
$installerSource = [IO.File]::ReadAllText($installerPath)
if ($installerSource -cmatch '(?m)\bGet-FileHash\b') {
    throw 'The quality installer must not depend on Get-FileHash.'
}
if ($installerSource -cnotmatch '\[Security\.Cryptography\.SHA256\]::Create\(\)') {
    throw 'The quality installer must hash through the .NET SHA-256 API.'
}
$releaseTag = 'v1.9.5'
$testBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $testBase (
    'bstrings-quality-installer-test-' + [Guid]::NewGuid().ToString('N')
)
$neighborRoot = Join-Path $testBase (
    'bstrings-quality-installer-neighbor-' + [Guid]::NewGuid().ToString('N')
)
$serverProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal([object]$Actual, [object]$Expected, [string]$Message) {
    if (-not [object]::Equals($Actual, $Expected)) {
        throw "$Message Expected '$Expected', found '$Actual'."
    }
}

function Assert-PathAbsent([string]$Path, [string]$Message) {
    if (Test-Path -LiteralPath $Path) {
        throw "$Message Unexpected path: $Path"
    }
}

function Assert-PathWithin([string]$Candidate, [string]$Parent, [string]$Message) {
    $resolvedCandidate = [IO.Path]::GetFullPath($Candidate)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    )
    $prefix = $resolvedParent + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedCandidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Message Candidate '$resolvedCandidate' is not beneath '$resolvedParent'."
    }
}

function Write-Utf8File([string]$Path, [string]$Content) {
    $parent = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        [IO.Directory]::CreateDirectory($parent) | Out-Null
    }
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Write-JsonFile([string]$Path, [object]$Value) {
    Write-Utf8File $Path (($Value | ConvertTo-Json -Depth 20) + "`n")
}

function Get-LowerSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-PhysicalFile([string]$Path, [string]$Name) {
    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if (
        $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        [long]$item.Length -lt 1
    ) {
        throw "$Name is not a non-empty physical file: $Path"
    }
    return $item
}

function New-StubCoreArchive([string]$Root) {
    $dotnetCandidates = @(
        Get-Command dotnet -CommandType Application -ErrorAction Stop |
            Where-Object { Test-Path -LiteralPath $_.Source -PathType Leaf }
    )
    if ($dotnetCandidates.Count -lt 1) {
        throw 'Could not resolve a physical dotnet executable for the synthetic stub build.'
    }
    $dotnet = $dotnetCandidates[0]
    $sourceRoot = Join-Path $Root 'stub-source'
    $publishRoot = Join-Path $Root 'stub-publish'
    [IO.Directory]::CreateDirectory($sourceRoot) | Out-Null

    $projectPath = Join-Path $sourceRoot 'bstrings-stub.csproj'
    Write-Utf8File $projectPath @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <AssemblyName>bstrings</AssemblyName>
    <RootNamespace>BstringsInstallerStub</RootNamespace>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
'@

    Write-Utf8File (Join-Path $sourceRoot 'Program.cs') @'
using System.Text.Json;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            LogInvocation(args);
            if (args.Length >= 2 && args[0] == "bundle" && args[1] == "acquire")
            {
                return Acquire(args);
            }
            if (args.Length >= 2 && args[0] == "bundle" && args[1] == "verify")
            {
                return Verify(args);
            }
            return 90;
        }
        catch (Exception ex)
        {
            var errorPath = Environment.GetEnvironmentVariable("BSTRINGS_INSTALLER_STUB_ERROR");
            if (!string.IsNullOrWhiteSpace(errorPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(errorPath))!);
                File.AppendAllText(errorPath, ex + Environment.NewLine);
            }
            return 99;
        }
    }

    private static int Acquire(string[] args)
    {
        var cache = RequiredOption(args, "--cache");
        var output = RequiredOption(args, "--output");
        _ = RequiredOption(args, "--manifest");
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(cache, "stub-partial.cache"), "partial");

        if (IntEnvironment("BSTRINGS_INSTALLER_STUB_PUBLISH_FAILURE") != 0)
        {
            if (Directory.Exists(output) || File.Exists(output))
            {
                return 24;
            }
            Directory.CreateDirectory(output);
            File.WriteAllText(
                Path.Combine(output, "unowned-sentinel.txt"),
                "preserve unowned destination exactly"
            );
            return 29;
        }

        var attempt = IncrementAttempt();
        var failures = IntEnvironment("BSTRINGS_INSTALLER_STUB_ACQUIRE_FAILURES");
        if (attempt <= failures)
        {
            return 23;
        }
        if (Directory.Exists(output) || File.Exists(output))
        {
            return 24;
        }

        Directory.CreateDirectory(output);
        foreach (var source in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            File.Copy(source, Path.Combine(output, Path.GetFileName(source)), overwrite: false);
        }
        var airgapSource = Environment.GetEnvironmentVariable(
            "BSTRINGS_INSTALLER_STUB_AIRGAP_MANIFEST"
        );
        if (string.IsNullOrWhiteSpace(airgapSource) || !File.Exists(airgapSource))
        {
            return 25;
        }
        File.Copy(airgapSource, Path.Combine(output, "airgap-manifest.json"), overwrite: false);
        File.WriteAllText(Path.Combine(output, "quality-profile.txt"), "quality");
        File.WriteAllText(Path.Combine(cache, "stub-verified.cache"), "verified");
        return 0;
    }

    private static int Verify(string[] args)
    {
        var requestedExit = IntEnvironment("BSTRINGS_INSTALLER_STUB_VERIFY_EXIT");
        if (requestedExit != 0)
        {
            return requestedExit;
        }
        var root = OptionalOption(args, "--bundle-root") ?? AppContext.BaseDirectory;
        return File.Exists(Path.Combine(root, "bstrings.exe")) &&
            File.Exists(Path.Combine(root, "airgap-manifest.json")) &&
            File.Exists(Path.Combine(root, "quality-profile.txt"))
                ? 0
                : 26;
    }

    private static int IncrementAttempt()
    {
        var statePath = Environment.GetEnvironmentVariable("BSTRINGS_INSTALLER_STUB_STATE");
        if (string.IsNullOrWhiteSpace(statePath))
        {
            throw new InvalidOperationException("The stub state path is missing.");
        }
        var fullPath = Path.GetFullPath(statePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var current = File.Exists(fullPath) ? int.Parse(File.ReadAllText(fullPath)) : 0;
        current++;
        File.WriteAllText(fullPath, current.ToString());
        return current;
    }

    private static int IntEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? 0 : int.Parse(value);
    }

    private static string RequiredOption(string[] args, string name)
    {
        return OptionalOption(args, name) ??
            throw new InvalidOperationException($"Missing required stub option {name}.");
    }

    private static string? OptionalOption(string[] args, string name)
    {
        var matches = Enumerable.Range(0, args.Length)
            .Where(index => args[index] == name)
            .ToArray();
        if (matches.Length == 0)
        {
            return null;
        }
        if (matches.Length != 1 || matches[0] == args.Length - 1)
        {
            throw new InvalidOperationException($"Invalid stub option {name}.");
        }
        return Path.GetFullPath(args[matches[0] + 1]);
    }

    private static void LogInvocation(string[] args)
    {
        var logPath = Environment.GetEnvironmentVariable("BSTRINGS_INSTALLER_STUB_LOG");
        if (string.IsNullOrWhiteSpace(logPath))
        {
            throw new InvalidOperationException("The stub invocation log path is missing.");
        }
        var fullPath = Path.GetFullPath(logPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var row = JsonSerializer.Serialize(new
        {
            processPath = Environment.ProcessPath,
            arguments = args,
        });
        File.AppendAllText(fullPath, row + "\n");
    }
}
'@

    $publishOutput = @(
        & $dotnet.Source publish $projectPath `
            --nologo `
            --configuration Release `
            --framework net10.0 `
            --runtime win-x64 `
            --self-contained false `
            --output $publishRoot `
            -p:UseAppHost=true `
            --verbosity quiet 2>&1
    )
    $publishExit = $LASTEXITCODE
    if ($publishExit -ne 0) {
        throw "Could not build the synthetic bstrings.exe (exit $publishExit): $($publishOutput -join [Environment]::NewLine)"
    }
    Get-PhysicalFile (Join-Path $publishRoot 'bstrings.exe') 'Synthetic bstrings.exe' | Out-Null
    Write-Utf8File (Join-Path $publishRoot 'CORE_RELEASE_README.md') "Synthetic installer test core.`n"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath = Join-Path $Root 'bstrings-win-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $publishRoot,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    Get-PhysicalFile $archivePath 'Synthetic core archive' | Out-Null
    return $archivePath
}

function New-FixtureServerScript([string]$Path) {
    Write-Utf8File $Path @'
import http.server
import pathlib
import sys
import urllib.parse

root = pathlib.Path(sys.argv[1]).resolve()
port_path = pathlib.Path(sys.argv[2])
request_log = pathlib.Path(sys.argv[3])

class Handler(http.server.BaseHTTPRequestHandler):
    def do_HEAD(self):
        self._serve(False)

    def do_GET(self):
        self._serve(True)

    def _serve(self, include_body):
        request_path = urllib.parse.unquote(urllib.parse.urlsplit(self.path).path)
        with request_log.open("a", encoding="utf-8", newline="\n") as stream:
            stream.write(request_path + "\n")
        if request_path.endswith("/repos/Donovoi/bstrings/releases/tags/v1.9.5"):
            candidate = root / "release.json"
        elif "/Donovoi/bstrings/releases/download/v1.9.5/" in request_path:
            name = pathlib.PurePosixPath(request_path).name
            candidate = root / name
        else:
            self.send_error(404)
            return
        try:
            resolved = candidate.resolve(strict=True)
            resolved.relative_to(root)
        except (FileNotFoundError, ValueError):
            self.send_error(404)
            return
        if not resolved.is_file():
            self.send_error(404)
            return
        data = resolved.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", "application/json" if resolved.suffix == ".json" else "application/octet-stream")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Connection", "close")
        self.end_headers()
        if include_body:
            self.wfile.write(data)

    def log_message(self, *_args):
        pass

server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
port_path.write_text(str(server.server_address[1]), encoding="ascii")
server.serve_forever()
'@
}

function ConvertTo-QuotedProcessArgument([string]$Value) {
    if ($Value.Contains('"') -or $Value.IndexOf([char]0) -ge 0) {
        throw 'Fixture-server process arguments must not contain a quote or NUL.'
    }
    # ProcessStartInfo.Arguments is used instead of ArgumentList so this test
    # also runs on Windows PowerShell 5.1 and the .NET Framework. Doubling
    # trailing backslashes preserves them before the closing quote.
    $escaped = [regex]::Replace($Value, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Start-FixtureServer([string]$AssetRoot, [string]$Name) {
    $pythonCandidates = @(
        Get-Command python -CommandType Application -ErrorAction Stop |
            Where-Object { Test-Path -LiteralPath $_.Source -PathType Leaf }
    )
    if ($pythonCandidates.Count -lt 1) {
        throw 'Could not resolve a physical Python executable for the local fixture server.'
    }
    $python = $pythonCandidates[0]
    $serverScript = Join-Path $testRoot 'fixture_server.py'
    $portPath = Join-Path $AssetRoot 'server.port'
    $requestLog = Join-Path $AssetRoot 'requests.log'

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $python.Source
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $quotedArguments = @(
        @('-u', $serverScript, $AssetRoot, $portPath, $requestLog) |
            ForEach-Object { ConvertTo-QuotedProcessArgument ([string]$_) }
    )
    $startInfo.Arguments = $quotedArguments -join ' '
    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Could not start the local fixture server for $Name."
    }
    $serverProcesses.Add($process)

    for ($attempt = 0; $attempt -lt 200 -and -not (Test-Path -LiteralPath $portPath); $attempt++) {
        if ($process.HasExited) {
            throw "The local fixture server for $Name exited before publishing its port."
        }
        Start-Sleep -Milliseconds 50
    }
    if (-not (Test-Path -LiteralPath $portPath -PathType Leaf)) {
        throw "The local fixture server for $Name did not publish its port."
    }
    $portText = [IO.File]::ReadAllText($portPath, [Text.Encoding]::ASCII)
    $port = 0
    if (-not [int]::TryParse($portText, [ref]$port) -or $port -lt 1 -or $port -gt 65535) {
        throw "The local fixture server for $Name published an invalid port."
    }
    return [pscustomobject]@{
        Process = $process
        Port = $port
        ApiUri = [uri]"http://127.0.0.1:$port/repos/Donovoi/bstrings/releases/tags/$releaseTag"
        RequestLog = $requestLog
    }
}

function New-ReleaseFixture(
    [string]$Name,
    [string]$CoreArchive,
    [string]$AirgapManifest,
    [switch]$WrongInstallerChecksum,
    [switch]$WrongCoreChecksum,
    [switch]$WrongTrustChecksum,
    [switch]$WrongCoreApiDigest,
    [switch]$NoncanonicalCoreUrl
) {
    $assetRoot = Join-Path $testRoot "fixture-$Name"
    [IO.Directory]::CreateDirectory($assetRoot) | Out-Null
    $installerAsset = Join-Path $assetRoot 'Install-BstringsQuality.ps1'
    $coreAsset = Join-Path $assetRoot 'bstrings-win-x64.zip'
    $trustAsset = Join-Path $assetRoot 'bundle-packs-quality.json'
    Copy-Item -LiteralPath $installerPath -Destination $installerAsset
    Copy-Item -LiteralPath $CoreArchive -Destination $coreAsset

    $airgapHash = Get-LowerSha256 $AirgapManifest
    $trust = [ordered]@{
        schemaVersion = 1
        profile = 'windows-x64-offline-v2-quality'
        bundleIdentity = "synthetic-quality-$($airgapHash.Substring(0, 24))"
        airgapManifestSha256 = $airgapHash
        packs = @(
            [ordered]@{
                id = 'synthetic-base'
                url = 'https://example.invalid/bstrings-quality-test.zip'
                bytes = 1
                sha256 = ('1' * 64)
            }
        )
    }
    Write-JsonFile $trustAsset $trust

    $installerHash = Get-LowerSha256 $installerAsset
    $coreHash = Get-LowerSha256 $coreAsset
    $trustHash = Get-LowerSha256 $trustAsset
    $checksums = [ordered]@{
        'Install-BstringsQuality.ps1' = if ($WrongInstallerChecksum) { '0' * 64 } else { $installerHash }
        'bstrings-win-x64.zip' = if ($WrongCoreChecksum) { '0' * 64 } else { $coreHash }
        'bundle-packs-quality.json' = if ($WrongTrustChecksum) { '0' * 64 } else { $trustHash }
    }
    $checksumRows = @(
        $checksums.Keys |
            Sort-Object |
            ForEach-Object { "$($checksums[$_])  $_" }
    )
    $checksumAsset = Join-Path $assetRoot 'SHA256SUMS.txt'
    Write-Utf8File $checksumAsset (($checksumRows -join "`n") + "`n")

    $server = Start-FixtureServer $assetRoot $Name
    $assets = [Collections.Generic.List[object]]::new()
    $assetId = 1000
    foreach ($fileName in @(
        'Install-BstringsQuality.ps1',
        'SHA256SUMS.txt',
        'bstrings-win-x64.zip',
        'bundle-packs-quality.json'
    )) {
        $path = Join-Path $assetRoot $fileName
        $item = Get-PhysicalFile $path "Synthetic release asset $fileName"
        $digest = Get-LowerSha256 $path
        if ($WrongCoreApiDigest -and $fileName -ceq 'bstrings-win-x64.zip') {
            $digest = '0' * 64
        }
        $downloadUrl = "http://127.0.0.1:$($server.Port)/Donovoi/bstrings/releases/download/$releaseTag/$fileName"
        if ($NoncanonicalCoreUrl -and $fileName -ceq 'bstrings-win-x64.zip') {
            $downloadUrl += '?noncanonical=1'
        }
        $assets.Add([ordered]@{
            id = $assetId
            name = $fileName
            size = [long]$item.Length
            digest = 'sha256:' + $digest
            content_type = 'application/octet-stream'
            browser_download_url = $downloadUrl
        })
        $assetId++
    }
    Write-JsonFile (Join-Path $assetRoot 'release.json') ([ordered]@{
        id = 999
        tag_name = $releaseTag
        draft = $false
        prerelease = $false
        assets = @($assets)
    })
    return [pscustomobject]@{
        Name = $Name
        AssetRoot = $assetRoot
        ApiUri = $server.ApiUri
        RequestLog = $server.RequestLog
        TrustManifest = $trustAsset
    }
}

function Invoke-WithEnvironment([hashtable]$Variables, [scriptblock]$Action) {
    $previous = @{}
    foreach ($name in $Variables.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, [string]$Variables[$name], 'Process')
    }
    try {
        & $Action
    }
    finally {
        foreach ($name in $Variables.Keys) {
            [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
        }
    }
}

function Invoke-Installer(
    [hashtable]$Arguments,
    [hashtable]$Environment
) {
    $execution = Invoke-WithEnvironment $Environment {
        $innerRecords = @()
        $innerCaught = $null
        $innerNativeExit = 0
        try {
            $innerRecords = @(& $installerPath @Arguments *>&1)
            $innerNativeExit = $LASTEXITCODE
        }
        catch {
            $innerCaught = $_
            $innerNativeExit = $LASTEXITCODE
        }
        [pscustomobject]@{
            Records = $innerRecords
            Caught = $innerCaught
            NativeExit = $innerNativeExit
        }
    }
    $records = @($execution.Records)
    $caught = $execution.Caught
    $nativeExit = [int]$execution.NativeExit
    $textParts = [Collections.Generic.List[string]]::new()
    foreach ($record in $records) {
        $textParts.Add([string]$record)
    }
    if ($null -ne $caught) {
        $textParts.Add([string]$caught)
        if (-not [string]::IsNullOrWhiteSpace([string]$caught.ScriptStackTrace)) {
            $textParts.Add([string]$caught.ScriptStackTrace)
        }
        if (-not [string]::IsNullOrWhiteSpace([string]$caught.InvocationInfo.PositionMessage)) {
            $textParts.Add([string]$caught.InvocationInfo.PositionMessage)
        }
    }
    return [pscustomobject]@{
        Succeeded = ($null -eq $caught -and $nativeExit -eq 0)
        NativeExitCode = $nativeExit
        ErrorRecord = $caught
        Text = $textParts -join [Environment]::NewLine
    }
}

function New-InstallerArguments(
    [object]$Fixture,
    [string]$Destination,
    [AllowEmptyString()]
    [string]$Cache,
    [int]$AcquireAttempts,
    [switch]$KeepCache,
    [switch]$UseDefaultReleaseTag
) {
    $arguments = @{
        DestinationDirectory = $Destination
        AcquireAttempts = $AcquireAttempts
        ReleaseApiUri = $Fixture.ApiUri
        AllowLoopbackHttpForTesting = $true
        MinimumFreeBytes = [long]1
    }
    if (-not [string]::IsNullOrWhiteSpace($Cache)) {
        $arguments.InstallerCacheDirectory = $Cache
    }
    if (-not $UseDefaultReleaseTag) {
        $arguments.ReleaseTag = $releaseTag
    }
    if ($KeepCache) {
        $arguments.KeepCache = $true
    }
    return $arguments
}

function New-StubEnvironment(
    [string]$Name,
    [string]$AirgapManifest,
    [int]$AcquireFailures = 0,
    [int]$VerifyExit = 0,
    [bool]$PublishFailure = $false
) {
    $stateRoot = Join-Path $testRoot "stub-state-$Name"
    return @{
        BSTRINGS_INSTALLER_STUB_LOG = Join-Path $stateRoot 'invocations.jsonl'
        BSTRINGS_INSTALLER_STUB_STATE = Join-Path $stateRoot 'acquire-count.txt'
        BSTRINGS_INSTALLER_STUB_ERROR = Join-Path $stateRoot 'errors.txt'
        BSTRINGS_INSTALLER_STUB_AIRGAP_MANIFEST = $AirgapManifest
        BSTRINGS_INSTALLER_STUB_ACQUIRE_FAILURES = [string]$AcquireFailures
        BSTRINGS_INSTALLER_STUB_VERIFY_EXIT = [string]$VerifyExit
        BSTRINGS_INSTALLER_STUB_PUBLISH_FAILURE = if ($PublishFailure) { '1' } else { '0' }
        HTTP_PROXY = 'http://127.0.0.1:9'
        HTTPS_PROXY = 'http://127.0.0.1:9'
        ALL_PROXY = 'http://127.0.0.1:9'
        NO_PROXY = '127.0.0.1,localhost,::1'
    }
}

function Read-StubInvocations([string]$Path) {
    $results = [Collections.Generic.List[object]]::new()
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }
    foreach ($line in Get-Content -LiteralPath $Path) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $row = $line | ConvertFrom-Json
        $results.Add([pscustomobject]@{
            ProcessPath = [string]$row.processPath
            Arguments = @($row.arguments | ForEach-Object { [string]$_ })
        })
    }
    return @($results)
}

function Get-InvocationOption([object]$Invocation, [string]$Name) {
    $matches = @()
    for ($index = 0; $index -lt $Invocation.Arguments.Count; $index++) {
        if ($Invocation.Arguments[$index] -ceq $Name) {
            $matches += $index
        }
    }
    if ($matches.Count -ne 1 -or $matches[0] -eq $Invocation.Arguments.Count - 1) {
        throw "Invocation did not contain exactly one value for $Name."
    }
    return [string]$Invocation.Arguments[$matches[0] + 1]
}

function Assert-CommandPrefix([object]$Invocation, [string]$Command) {
    Assert-True ($Invocation.Arguments.Count -ge 2) "The stub invocation did not contain a command."
    Assert-Equal $Invocation.Arguments[0] 'bundle' 'The stub command group differed.'
    Assert-Equal $Invocation.Arguments[1] $Command 'The stub bundle command differed.'
}

function Assert-InstallerFailed([object]$Result, [string]$ExpectedPattern, [string]$Name) {
    Assert-True (-not $Result.Succeeded) "$Name unexpectedly succeeded."
    if ($Result.Text -notmatch $ExpectedPattern) {
        throw "$Name failed for an unexpected reason. Output: $($Result.Text)"
    }
}

function Remove-ValidatedTestTree([string]$Path, [string]$LeafPattern) {
    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    $leaf = [IO.Path]::GetFileName($resolved)
    $basePrefix = $testBase.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar
    ) + [IO.Path]::DirectorySeparatorChar
    if (
        $leaf -notmatch $LeafPattern -or
        -not $resolved.StartsWith($basePrefix, [StringComparison]::OrdinalIgnoreCase)
    ) {
        throw "Refusing to remove an unexpected test path: $resolved"
    }
    $rootItem = Get-Item -LiteralPath $resolved -Force
    if (
        -not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    ) {
        throw "Refusing to remove a linked or non-directory test path: $resolved"
    }
    $linked = @(
        Get-ChildItem -LiteralPath $resolved -Recurse -Force |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }
    )
    if ($linked.Count -ne 0) {
        throw "Refusing to remove a test tree containing a reparse point: $resolved"
    }
    [IO.Directory]::Delete($resolved, $true)
}

$testLeaf = [IO.Path]::GetFileName($testRoot)
$neighborLeaf = [IO.Path]::GetFileName($neighborRoot)
if ($testLeaf -notmatch '^bstrings-quality-installer-test-[0-9a-f]{32}$') {
    throw "Unexpected installer test path: $testRoot"
}
if ($neighborLeaf -notmatch '^bstrings-quality-installer-neighbor-[0-9a-f]{32}$') {
    throw "Unexpected installer neighbor path: $neighborRoot"
}
Get-PhysicalFile $installerPath 'Quality installer under test' | Out-Null
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
[IO.Directory]::CreateDirectory($neighborRoot) | Out-Null
$neighborSentinel = Join-Path $neighborRoot 'must-survive.txt'
Write-Utf8File $neighborSentinel 'outside the installer test root'

try {
    $serverScript = Join-Path $testRoot 'fixture_server.py'
    New-FixtureServerScript $serverScript
    $coreArchive = New-StubCoreArchive $testRoot
    $airgapManifest = Join-Path $testRoot 'synthetic-airgap-manifest.json'
    Write-JsonFile $airgapManifest ([ordered]@{
        schemaVersion = 1
        files = @()
    })

    # A complete default-profile run must acquire quality, verify through the
    # installed executable, use exact argument values, and clean its cache.
    $successFixture = New-ReleaseFixture 'success' $coreArchive $airgapManifest
    $successDestination = Join-Path $testRoot ('destination success ' + [char]0x00b5)
    $successCache = Join-Path $testRoot ".bstrings-quality-installer-cache\$releaseTag"
    $successEnvironment = New-StubEnvironment 'success' $airgapManifest
    $successArguments = New-InstallerArguments `
        $successFixture `
        $successDestination `
        '' `
        3 `
        -UseDefaultReleaseTag
    $success = Invoke-Installer $successArguments $successEnvironment
    Assert-True $success.Succeeded "The synthetic quality installation failed: $($success.Text)"
    Assert-True (Test-Path -LiteralPath (Join-Path $successDestination 'quality-profile.txt') -PathType Leaf) `
        'The successful installation did not publish the quality marker.'
    Assert-PathAbsent $successCache 'A successful default installation did not clean its installer cache.'
    $successInvocations = @(Read-StubInvocations $successEnvironment.BSTRINGS_INSTALLER_STUB_LOG)
    Assert-Equal $successInvocations.Count 2 'A successful installation used an unexpected command count.'
    Assert-CommandPrefix $successInvocations[0] 'acquire'
    Assert-CommandPrefix $successInvocations[1] 'verify'
    $successManifestArgument = Get-InvocationOption $successInvocations[0] '--manifest'
    Assert-Equal ([IO.Path]::GetFileName($successManifestArgument)) 'bundle-packs-quality.json' `
        'The installer did not pass the quality trust manifest to bundle acquire.'
    Assert-PathWithin $successManifestArgument $successCache `
        'The installer staged its trust manifest outside its bounded cache.'
    $successPackCacheArgument = Get-InvocationOption $successInvocations[0] '--cache'
    Assert-Equal `
        ([IO.Path]::GetFullPath($successPackCacheArgument)) `
        ([IO.Path]::GetFullPath((Join-Path $successCache 'bundle-packs'))) `
        'The installer passed the wrong owned pack cache to bundle acquire.'
    Assert-Equal `
        ([IO.Path]::GetFullPath((Get-InvocationOption $successInvocations[0] '--output'))) `
        ([IO.Path]::GetFullPath($successDestination)) `
        'The bundle acquire output differed from DestinationDirectory.'
    Assert-Equal `
        ([IO.Path]::GetFullPath((Get-InvocationOption $successInvocations[1] '--bundle-root'))) `
        ([IO.Path]::GetFullPath($successDestination)) `
        'The final bundle verification root differed from DestinationDirectory.'
    Assert-Equal `
        ([IO.Path]::GetFullPath($successInvocations[1].ProcessPath)) `
        ([IO.Path]::GetFullPath((Join-Path $successDestination 'bstrings.exe'))) `
        'Final verification did not run through the installed bstrings.exe.'

    # With no DestinationDirectory argument, the one-command path must remain
    # relative to the caller, install only the quality profile, verify it, and
    # remove the complete default cache tree.
    $defaultCallerRoot = Join-Path $testRoot (
        'default-caller-' + [Guid]::NewGuid().ToString('N')
    )
    [IO.Directory]::CreateDirectory($defaultCallerRoot) | Out-Null
    $defaultCallerFixture = New-ReleaseFixture `
        'default-caller' `
        $coreArchive `
        $airgapManifest
    $defaultCallerDestination = Join-Path $defaultCallerRoot 'bstrings-quality'
    $defaultCallerCacheParent = Join-Path `
        $defaultCallerRoot `
        '.bstrings-quality-installer-cache'
    $defaultCallerEnvironment = New-StubEnvironment 'default-caller' $airgapManifest
    $defaultCallerArguments = New-InstallerArguments `
        $defaultCallerFixture `
        $defaultCallerDestination `
        '' `
        3 `
        -UseDefaultReleaseTag
    $defaultCallerArguments.Remove('DestinationDirectory')
    Assert-True `
        (-not $defaultCallerArguments.ContainsKey('DestinationDirectory')) `
        'The default-destination scenario unexpectedly passed DestinationDirectory.'
    Assert-PathAbsent $defaultCallerDestination `
        'The default caller-relative destination existed before the test.'

    Push-Location -LiteralPath $defaultCallerRoot
    try {
        Assert-Equal `
            ([IO.Path]::GetFullPath((Get-Location).Path)) `
            ([IO.Path]::GetFullPath($defaultCallerRoot)) `
            'Push-Location did not select the bounded default-destination caller.'
        $defaultCaller = Invoke-Installer `
            $defaultCallerArguments `
            $defaultCallerEnvironment
    }
    finally {
        Pop-Location
    }

    Assert-True $defaultCaller.Succeeded (
        "The caller-relative default installation failed: $($defaultCaller.Text)"
    )
    Assert-True `
        (Test-Path -LiteralPath (Join-Path $defaultCallerDestination 'quality-profile.txt') -PathType Leaf) `
        'The caller-relative default did not install the quality profile.'
    Assert-PathAbsent $defaultCallerCacheParent `
        'The caller-relative default left its installer cache behind.'
    $defaultCallerChildren = @(Get-ChildItem -LiteralPath $defaultCallerRoot -Force)
    Assert-Equal $defaultCallerChildren.Count 1 `
        'The caller-relative default published content outside bstrings-quality.'
    Assert-Equal `
        ([IO.Path]::GetFullPath($defaultCallerChildren[0].FullName)) `
        ([IO.Path]::GetFullPath($defaultCallerDestination)) `
        'The caller-relative default published an unexpected top-level path.'
    $defaultCallerInvocations = @(
        Read-StubInvocations $defaultCallerEnvironment.BSTRINGS_INSTALLER_STUB_LOG
    )
    Assert-Equal $defaultCallerInvocations.Count 2 `
        'The caller-relative default used an unexpected command count.'
    Assert-CommandPrefix $defaultCallerInvocations[0] 'acquire'
    Assert-CommandPrefix $defaultCallerInvocations[1] 'verify'
    Assert-Equal `
        ([IO.Path]::GetFileName((Get-InvocationOption $defaultCallerInvocations[0] '--manifest'))) `
        'bundle-packs-quality.json' `
        'The caller-relative default did not acquire the quality-only manifest.'
    Assert-Equal `
        ([IO.Path]::GetFullPath((Get-InvocationOption $defaultCallerInvocations[0] '--output'))) `
        ([IO.Path]::GetFullPath($defaultCallerDestination)) `
        'The omitted destination did not resolve to caller-relative bstrings-quality.'
    Assert-Equal `
        ([IO.Path]::GetFullPath((Get-InvocationOption $defaultCallerInvocations[1] '--bundle-root'))) `
        ([IO.Path]::GetFullPath($defaultCallerDestination)) `
        'The caller-relative default verification used the wrong bundle root.'
    Assert-Equal `
        ([IO.Path]::GetFullPath($defaultCallerInvocations[1].ProcessPath)) `
        ([IO.Path]::GetFullPath((Join-Path $defaultCallerDestination 'bstrings.exe'))) `
        'The caller-relative default was not verified by its installed executable.'

    # KeepCache retains only installer-owned cache material after a verified run.
    $keptFixture = New-ReleaseFixture 'kept-cache' $coreArchive $airgapManifest
    $keptDestination = Join-Path $testRoot 'destination-kept'
    $keptCache = Join-Path $testRoot ".bstrings-quality-installer-cache\$releaseTag"
    $keptEnvironment = New-StubEnvironment 'kept-cache' $airgapManifest
    $keptArguments = New-InstallerArguments `
        $keptFixture `
        $keptDestination `
        '' `
        3 `
        -KeepCache
    $kept = Invoke-Installer $keptArguments $keptEnvironment
    Assert-True $kept.Succeeded "The KeepCache installation failed: $($kept.Text)"
    Assert-True (Test-Path -LiteralPath $keptCache -PathType Container) `
        'KeepCache did not retain the installer cache.'
    $keptInvocations = @(Read-StubInvocations $keptEnvironment.BSTRINGS_INSTALLER_STUB_LOG)
    Assert-Equal $keptInvocations.Count 2 'The KeepCache installation used an unexpected command count.'
    $keptPackCache = Get-InvocationOption $keptInvocations[0] '--cache'
    Assert-Equal `
        ([IO.Path]::GetFullPath($keptPackCache)) `
        ([IO.Path]::GetFullPath((Join-Path $keptCache 'bundle-packs'))) `
        'KeepCache passed the wrong owned pack cache to bundle acquire.'
    Assert-True (Test-Path -LiteralPath (Join-Path $keptPackCache 'stub-verified.cache') -PathType Leaf) `
        'KeepCache did not retain the verified synthetic pack marker.'

    # An already-valid destination is idempotent: verify it without reacquiring
    # or modifying unrelated content in the destination.
    $idempotentSentinel = Join-Path $keptDestination 'user-sentinel.txt'
    Write-Utf8File $idempotentSentinel 'preserve me'
    $idempotent = Invoke-Installer $keptArguments $keptEnvironment
    Assert-True $idempotent.Succeeded "The idempotent installation failed: $($idempotent.Text)"
    Assert-Equal ([IO.File]::ReadAllText($idempotentSentinel)) 'preserve me' `
        'The idempotent installation changed an existing destination file.'
    $idempotentInvocations = @(Read-StubInvocations $keptEnvironment.BSTRINGS_INSTALLER_STUB_LOG)
    Assert-Equal $idempotentInvocations.Count 3 'The idempotent run unexpectedly reacquired the bundle.'
    Assert-CommandPrefix $idempotentInvocations[2] 'verify'

    # A transient acquire failure must retry against the same manifest, cache,
    # and output paths before performing one installed verification.
    $retryFixture = New-ReleaseFixture 'retry' $coreArchive $airgapManifest
    $retryDestination = Join-Path $testRoot 'destination-retry'
    $retryCache = Join-Path $testRoot 'cache-retry'
    $retryDestinationInput = $retryDestination + [IO.Path]::DirectorySeparatorChar
    $retryCacheInput = $retryCache + [IO.Path]::DirectorySeparatorChar
    $retryEnvironment = New-StubEnvironment 'retry' $airgapManifest 1 0
    $retryArguments = New-InstallerArguments `
        $retryFixture `
        $retryDestinationInput `
        $retryCacheInput `
        2
    $retry = Invoke-Installer $retryArguments $retryEnvironment
    $retryInvocations = @(Read-StubInvocations $retryEnvironment.BSTRINGS_INSTALLER_STUB_LOG)
    $retryStubErrors = if (Test-Path -LiteralPath $retryEnvironment.BSTRINGS_INSTALLER_STUB_ERROR) {
        [IO.File]::ReadAllText($retryEnvironment.BSTRINGS_INSTALLER_STUB_ERROR)
    }
    else {
        '<none>'
    }
    $retryInvocationText = @(
        $retryInvocations | ForEach-Object {
            "[$($_.ProcessPath)] $($_.Arguments -join ' ')"
        }
    ) -join [Environment]::NewLine
    Assert-True $retry.Succeeded (
        "The bounded acquire retry did not recover: $($retry.Text)" +
        "`nStub invocations:`n$retryInvocationText`nStub errors:`n$retryStubErrors"
    )
    Assert-Equal $retryInvocations.Count 3 'The retry scenario used an unexpected command count.'
    Assert-CommandPrefix $retryInvocations[0] 'acquire'
    Assert-CommandPrefix $retryInvocations[1] 'acquire'
    Assert-CommandPrefix $retryInvocations[2] 'verify'
    Assert-Equal `
        ($retryInvocations[0].Arguments -join [char]31) `
        ($retryInvocations[1].Arguments -join [char]31) `
        'Acquire retry arguments changed between attempts.'
    Assert-Equal `
        ([string](Get-InvocationOption $retryInvocations[0] '--cache')) `
        ([IO.Path]::GetFullPath((Join-Path $retryCache 'bundle-packs'))) `
        'The installer did not use the exact explicit cache root for bundle packs.'
    Assert-Equal `
        ([string](Get-InvocationOption $retryInvocations[0] '--output')) `
        ([IO.Path]::GetFullPath($retryDestination)) `
        'The installer did not normalize the trailing destination separator.'
    Assert-Equal `
        ([string](Get-InvocationOption $retryInvocations[2] '--bundle-root')) `
        ([IO.Path]::GetFullPath($retryDestination)) `
        'The verifier did not receive the normalized destination path.'
    Assert-True (Test-Path -LiteralPath $retryCache -PathType Container) `
        'A successful run unexpectedly removed the user-owned explicit cache.'

    # A native acquire can lose a race after the initial absence check. If it
    # publishes a destination and then fails, the installer cannot prove that
    # it owns the path and must preserve every byte instead of rolling it back.
    $publishFailureFixture = New-ReleaseFixture `
        'publish-failure' `
        $coreArchive `
        $airgapManifest
    $publishFailureDestination = Join-Path $testRoot 'destination-publish-failure'
    $publishFailureCache = Join-Path $testRoot 'cache-publish-failure'
    $publishFailureEnvironment = New-StubEnvironment `
        'publish-failure' `
        $airgapManifest `
        -PublishFailure $true
    $publishFailureArguments = New-InstallerArguments `
        $publishFailureFixture `
        $publishFailureDestination `
        $publishFailureCache `
        2
    $publishFailure = Invoke-Installer `
        $publishFailureArguments `
        $publishFailureEnvironment
    Assert-InstallerFailed `
        $publishFailure `
        'exit code 29|preserved|ownership' `
        'Failed acquire with a newly published destination'
    $unownedSentinel = Join-Path `
        $publishFailureDestination `
        'unowned-sentinel.txt'
    Assert-True (Test-Path -LiteralPath $unownedSentinel -PathType Leaf) `
        'The installer deleted the unowned destination sentinel after acquire failed.'
    Assert-Equal `
        ([IO.File]::ReadAllText($unownedSentinel)) `
        'preserve unowned destination exactly' `
        'The installer changed the unowned destination sentinel after acquire failed.'
    Assert-Equal `
        (@(Get-ChildItem -LiteralPath $publishFailureDestination -Force).Count) `
        1 `
        'The failed acquire destination was not preserved exactly.'
    $publishFailureInvocations = @(
        Read-StubInvocations $publishFailureEnvironment.BSTRINGS_INSTALLER_STUB_LOG
    )
    Assert-Equal $publishFailureInvocations.Count 1 `
        'The publish-then-fail acquire was unexpectedly retried.'
    Assert-CommandPrefix $publishFailureInvocations[0] 'acquire'
    Assert-True (Test-Path -LiteralPath $publishFailureCache -PathType Container) `
        'The failed acquire unexpectedly removed the user-owned resumable cache.'

    # The checksum file is authoritative for both the physical installer and
    # downloaded assets. Neither mismatch may reach bstrings.exe.
    $selfHashFixture = New-ReleaseFixture `
        'wrong-self-hash' `
        $coreArchive `
        $airgapManifest `
        -WrongInstallerChecksum
    $selfHashDestination = Join-Path $testRoot 'destination-wrong-self-hash'
    $selfHashCache = Join-Path $testRoot 'cache-wrong-self-hash'
    $selfHashEnvironment = New-StubEnvironment 'wrong-self-hash' $airgapManifest
    $selfHashArguments = New-InstallerArguments `
        $selfHashFixture `
        $selfHashDestination `
        $selfHashCache `
        1
    $selfHash = Invoke-Installer $selfHashArguments $selfHashEnvironment
    Assert-InstallerFailed $selfHash 'SHA-?256|checksum|hash' 'Installer self-hash mismatch'
    Assert-PathAbsent $selfHashDestination 'A self-hash mismatch created the destination.'
    Assert-PathAbsent $selfHashEnvironment.BSTRINGS_INSTALLER_STUB_LOG `
        'A self-hash mismatch executed bstrings.exe.'

    $coreHashFixture = New-ReleaseFixture `
        'wrong-core-hash' `
        $coreArchive `
        $airgapManifest `
        -WrongCoreChecksum
    $coreHashDestination = Join-Path $testRoot 'destination-wrong-core-hash'
    $coreHashCache = Join-Path $testRoot 'cache-wrong-core-hash'
    $coreHashEnvironment = New-StubEnvironment 'wrong-core-hash' $airgapManifest
    $coreHashArguments = New-InstallerArguments `
        $coreHashFixture `
        $coreHashDestination `
        $coreHashCache `
        1
    $coreHash = Invoke-Installer $coreHashArguments $coreHashEnvironment
    Assert-InstallerFailed $coreHash 'SHA-?256|checksum|hash' 'Core archive hash mismatch'
    Assert-PathAbsent $coreHashDestination 'A core archive hash mismatch created the destination.'
    Assert-PathAbsent $coreHashEnvironment.BSTRINGS_INSTALLER_STUB_LOG `
        'A core archive hash mismatch executed bstrings.exe.'

    $trustHashFixture = New-ReleaseFixture `
        'wrong-trust-hash' `
        $coreArchive `
        $airgapManifest `
        -WrongTrustChecksum
    $trustHashDestination = Join-Path $testRoot 'destination-wrong-trust-hash'
    $trustHashCache = Join-Path $testRoot 'cache-wrong-trust-hash'
    $trustHashEnvironment = New-StubEnvironment 'wrong-trust-hash' $airgapManifest
    $trustHashArguments = New-InstallerArguments `
        $trustHashFixture `
        $trustHashDestination `
        $trustHashCache `
        1
    $trustHash = Invoke-Installer $trustHashArguments $trustHashEnvironment
    Assert-InstallerFailed $trustHash 'SHA-?256|checksum|hash' 'Quality trust-manifest hash mismatch'
    Assert-PathAbsent $trustHashDestination 'A trust-manifest hash mismatch created the destination.'
    Assert-PathAbsent $trustHashEnvironment.BSTRINGS_INSTALLER_STUB_LOG `
        'A trust-manifest hash mismatch executed bstrings.exe.'

    # GitHub release metadata is an independent size/digest and URL binding.
    # A bad API digest or noncanonical tagged URL must fail before extraction.
    $apiDigestFixture = New-ReleaseFixture `
        'wrong-api-digest' `
        $coreArchive `
        $airgapManifest `
        -WrongCoreApiDigest
    $apiDigestDestination = Join-Path $testRoot 'destination-wrong-api-digest'
    $apiDigestCache = Join-Path $testRoot 'cache-wrong-api-digest'
    $apiDigestEnvironment = New-StubEnvironment 'wrong-api-digest' $airgapManifest
    $apiDigestArguments = New-InstallerArguments `
        $apiDigestFixture `
        $apiDigestDestination `
        $apiDigestCache `
        1
    $apiDigest = Invoke-Installer $apiDigestArguments $apiDigestEnvironment
    Assert-InstallerFailed $apiDigest 'digest|SHA-?256|metadata|hash' 'Release API digest mismatch'
    Assert-PathAbsent $apiDigestDestination 'A release API digest mismatch created the destination.'
    Assert-PathAbsent $apiDigestEnvironment.BSTRINGS_INSTALLER_STUB_LOG `
        'A release API digest mismatch executed bstrings.exe.'

    $urlFixture = New-ReleaseFixture `
        'noncanonical-url' `
        $coreArchive `
        $airgapManifest `
        -NoncanonicalCoreUrl
    $urlDestination = Join-Path $testRoot 'destination-noncanonical-url'
    $urlCache = Join-Path $testRoot 'cache-noncanonical-url'
    $urlEnvironment = New-StubEnvironment 'noncanonical-url' $airgapManifest
    $urlArguments = New-InstallerArguments `
        $urlFixture `
        $urlDestination `
        $urlCache `
        1
    $urlResult = Invoke-Installer $urlArguments $urlEnvironment
    Assert-InstallerFailed $urlResult 'URL|canonical|query|release' 'Noncanonical release asset URL'
    Assert-PathAbsent $urlDestination 'A noncanonical release asset URL created the destination.'
    Assert-PathAbsent $urlEnvironment.BSTRINGS_INSTALLER_STUB_LOG `
        'A noncanonical release asset URL executed bstrings.exe.'

    # An invalid pre-existing destination must remain byte-for-byte untouched.
    $existingFixture = New-ReleaseFixture 'existing-invalid' $coreArchive $airgapManifest
    $existingDestination = Join-Path $testRoot 'destination-existing-invalid'
    [IO.Directory]::CreateDirectory($existingDestination) | Out-Null
    $existingSentinel = Join-Path $existingDestination 'original.txt'
    Write-Utf8File $existingSentinel 'original destination bytes'
    $existingCache = Join-Path $testRoot 'cache-existing-invalid'
    $existingEnvironment = New-StubEnvironment 'existing-invalid' $airgapManifest
    $existingArguments = New-InstallerArguments `
        $existingFixture `
        $existingDestination `
        $existingCache `
        1
    $existing = Invoke-Installer $existingArguments $existingEnvironment
    Assert-InstallerFailed $existing 'existing|destination|verify|manifest' 'Invalid existing destination'
    Assert-Equal ([IO.File]::ReadAllText($existingSentinel)) 'original destination bytes' `
        'The installer changed the invalid existing destination sentinel.'
    Assert-Equal (@(Get-ChildItem -LiteralPath $existingDestination -Force).Count) 1 `
        'The installer added content to an invalid existing destination.'

    # A failed final verification must never be reported as success. Only the
    # destination created by this invocation is rolled back; its resumable
    # user-owned cache remains available for diagnosis.
    $verifyFixture = New-ReleaseFixture 'verify-failure' $coreArchive $airgapManifest
    $verifyDestination = Join-Path $testRoot 'destination-verify-failure'
    $verifyCache = Join-Path $testRoot 'cache-verify-failure'
    $verifyEnvironment = New-StubEnvironment 'verify-failure' $airgapManifest 0 37
    $verifyArguments = New-InstallerArguments `
        $verifyFixture `
        $verifyDestination `
        $verifyCache `
        1
    $verify = Invoke-Installer $verifyArguments $verifyEnvironment
    Assert-InstallerFailed $verify 'verif|exit|37' 'Nonzero final bundle verification'
    Assert-PathAbsent $verifyDestination `
        'A failed final verification left an invocation-created destination behind.'
    Assert-True (Test-Path -LiteralPath $verifyCache -PathType Container) `
        'A failed final verification unexpectedly removed the resumable cache.'

    Assert-Equal ([IO.File]::ReadAllText($neighborSentinel)) 'outside the installer test root' `
        'Installer cleanup crossed the bounded test directory.'
    Write-Host 'Quality installer synthetic integration tests passed.'
}
finally {
    foreach ($process in $serverProcesses) {
        try {
            if (-not $process.HasExited) {
                # The fixture server does not create child processes. Kill()
                # is available on both .NET Framework and modern .NET.
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
        }
        catch {
            Write-Warning "Could not stop fixture server process $($process.Id): $($_.Exception.Message)"
        }
        finally {
            $process.Dispose()
        }
    }
    Remove-ValidatedTestTree `
        $testRoot `
        '^bstrings-quality-installer-test-[0-9a-f]{32}$'
    Remove-ValidatedTestTree `
        $neighborRoot `
        '^bstrings-quality-installer-neighbor-[0-9a-f]{32}$'
}
