[CmdletBinding()]
param(
    [string]$NativeHost,
    [string]$ManagedHost,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\artifacts\bridge-build\host-smoke')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { throw 'Native verification requires Windows or Linux.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($architecture -notin @('x64', 'arm64')) { throw 'Native verification requires x64 or ARM64.' }
$rid = "$platform-$architecture"
if (-not $NativeHost) {
    $suffix = if ($IsWindows) { '.exe' } else { '' }
    $NativeHost = Join-Path $repository "artifacts\bridge-build\native-host\XRegistry.FederationBridge$suffix"
}
if (-not $ManagedHost) {
    $ManagedHost = Join-Path $repository "artifacts\bridge-build\bin\XRegistry.FederationBridge\release_$rid\XRegistry.FederationBridge.dll"
}
$native = [IO.Path]::GetFullPath($NativeHost)
$managed = [IO.Path]::GetFullPath($ManagedHost)
if (-not (Test-Path -LiteralPath $native -PathType Leaf)) { throw 'Publish the native bridge host before running this check.' }
if (-not (Test-Path -LiteralPath $managed -PathType Leaf)) { throw 'The corresponding managed assembly is required for the real JIT negative control.' }
if ($native.Contains('"') -or $managed.Contains('"') -or $native.Contains("`n") -or $managed.Contains("`n")) {
    throw 'Runtime verification paths contain unsupported argument delimiters.'
}
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output | Out-Null
$configPath = Join-Path $output 'runtime-config.json'
$evidence = [ordered]@{
    schemaVersion = 1
    scope = 'bounded-federation-bridge'
    status = 'running'
    nativeHostSha256 = (Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant()
    managedHostSha256 = (Get-FileHash -LiteralPath $managed -Algorithm SHA256).Hash.ToLowerInvariant()
    runtimeIdentifier = $rid
    passedHttpCases = 0
}
$process = $null
$handler = $null
$client = $null

function Read-RuntimeInfo {
    param([string]$Executable, [string[]]$Arguments, [string]$Name)
    $stdout = Join-Path $output "$Name.stdout.json"
    $stderr = Join-Path $output "$Name.stderr.log"
    $child = Start-Process -FilePath $Executable -ArgumentList $Arguments -WorkingDirectory $repository `
        -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    try {
        $until = [DateTime]::UtcNow.AddSeconds(10)
        while (-not $child.WaitForExit(100)) {
            if ([DateTime]::UtcNow -ge $until) { throw 'Runtime-info process exceeded ten seconds.' }
            foreach ($path in @($stdout, $stderr)) {
                if ((Get-Item -LiteralPath $path).Length -gt 16384) { throw 'Runtime-info output exceeded its byte budget.' }
            }
        }
        if ($child.ExitCode -ne 0 -or (Get-Item -LiteralPath $stderr).Length -ne 0) {
            throw 'Runtime-info did not complete successfully and cleanly.'
        }
        if ((Get-Item -LiteralPath $stdout).Length -gt 16384) { throw 'Runtime-info output exceeded its byte budget.' }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($stdout))
        $json = [Text.Json.JsonDocument]::Parse($text)
        try {
            $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($property in $json.RootElement.EnumerateObject()) {
                if (-not $names.Add($property.Name)) { throw 'Duplicate runtime-info property.' }
            }
            if ($names.Count -ne 11 -or $json.RootElement.GetProperty('schemaVersion').GetInt32() -ne 1 -or
                $json.RootElement.GetProperty('application').GetString() -ne 'XRegistry.FederationBridge' -or
                $json.RootElement.GetProperty('framework').GetString() -ne 'net10.0') {
                throw 'Runtime-info identifies a different application/schema/framework.'
            }
            $null = $json.RootElement.GetProperty('dynamicCodeSupported').GetBoolean()
            $null = $json.RootElement.GetProperty('dynamicCodeCompiled').GetBoolean()
            $null = $json.RootElement.GetProperty('nativeAot').GetBoolean()
            $null = $json.RootElement.GetProperty('jitCompiledMethods').GetInt64()
            foreach ($property in @('runtimeIdentifier', 'processArchitecture', 'osArchitecture', 'runtimeDescription')) {
                if ([string]::IsNullOrWhiteSpace($json.RootElement.GetProperty($property).GetString())) { throw 'Incomplete runtime identity.' }
            }
        }
        finally { $json.Dispose() }
        return $text | ConvertFrom-Json
    }
    finally {
        if (-not $child.HasExited) { Stop-Process -Id $child.Id; $child.WaitForExit(5000) | Out-Null }
        $child.Dispose()
    }
}

function Test-NativeInfo {
    param($Info)
    return $Info.nativeAot -eq $true -and $Info.dynamicCodeSupported -eq $false -and
        $Info.dynamicCodeCompiled -eq $false -and $Info.jitCompiledMethods -eq 0 -and
        $Info.runtimeIdentifier -ceq $rid -and $Info.processArchitecture -ceq $architecture -and
        $Info.osArchitecture -ceq $architecture
}

try {
    $nativeInfo = Read-RuntimeInfo -Executable $native -Arguments @('--runtime-info') -Name 'native-runtime'
    $evidence['nativeRuntime'] = $nativeInfo
    if (-not (Test-NativeInfo $nativeInfo)) { throw 'The candidate is not the matching native runtime; JIT/feature switches or emulation cannot qualify it.' }
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
    $jitInfo = Read-RuntimeInfo -Executable $dotnet -Arguments @("`"$managed`"", '--runtime-info') -Name 'jit-runtime'
    $evidence['jitNegativeControl'] = $jitInfo
    if ((Test-NativeInfo $jitInfo) -or $jitInfo.nativeAot -ne $false -or $jitInfo.jitCompiledMethods -le 0) {
        throw 'The actual JIT negative control did not demonstrate rejected JIT execution.'
    }
    $config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'bridge.demo.json') -Raw | ConvertFrom-Json
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $root = "http://127.0.0.1:$port/registry"
    $config.hosting.listenPort = $port
    $config.hosting.publicRoot = $root
    $config.modelFile = Join-Path $PSScriptRoot 'bridge-model.json'
    $fixture = Join-Path $repository 'tests\Conformance\Sources\workingdrafts\bindings\samples\mapping'
    $config.sources[0].directory = $fixture
    $config.sources[0].authorizedDirectory = $fixture
    $config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $configPath -Encoding utf8NoBOM
    $process = Start-Process -FilePath $native -ArgumentList @('--config', "`"$configPath`"") `
        -WorkingDirectory $repository -RedirectStandardOutput (Join-Path $output 'stdout.log') `
        -RedirectStandardError (Join-Path $output 'stderr.log') -PassThru
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $handler.AllowAutoRedirect = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(3)
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Native host exited with code $($process.ExitCode)." }
        try {
            $response = $client.GetAsync($root).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -eq 200) {
                    $data = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                    if ($data.registryid -ne 'federation-bridge' -or $data.self -ne $root -or
                        $data.PSObject.Properties.Name -contains 'capabilities' -or
                        $data.PSObject.Properties.Name -contains 'model' -or
                        $data.PSObject.Properties.Name -contains 'modelsource') {
                        throw 'Unexpected native producer root representation.'
                    }
                    $ready = $true
                    break
                }
            }
            finally { $response.Dispose() }
        }
        catch [Net.Http.HttpRequestException] {
            # Refused connections are expected only during this bounded readiness window.
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Native host did not become ready in twenty seconds.' }
    $expandedRoot = $client.GetAsync($root + '?inline=capabilities').GetAwaiter().GetResult()
    try {
        $data = $expandedRoot.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ([int]$expandedRoot.StatusCode -ne 200 -or $data.registryid -ne 'federation-bridge' -or
            $data.self -ne $root -or $data.capabilities.federation.resolution -ne 'producer' -or
            $data.PSObject.Properties.Name -contains 'model' -or
            $data.PSObject.Properties.Name -contains 'modelsource') {
            throw 'The native producer root did not honor explicit capabilities inlining.'
        }
    }
    finally { $expandedRoot.Dispose() }
    $evidence.passedHttpCases++
    $metadata = $client.GetAsync($root + '/categories/main/registries/site').GetAwaiter().GetResult()
    try {
        $entity = $metadata.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
        if ([int]$metadata.StatusCode -ne 200 -or $entity.registryid -ne 'site' -or
            $entity.weburl -ne 'https://example.com/cataloged-registry' -or
            $entity.self -ne ($root + '/categories/main/registries/site')) {
            throw 'Native metadata-only view failed.'
        }
        $evidence.passedHttpCases++
    }
    finally { $metadata.Dispose() }
    $document = $client.GetAsync($root + '/documents/main/assets/item').GetAwaiter().GetResult()
    try {
        $bytes = $document.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        if ([int]$document.StatusCode -ne 200 -or [Convert]::ToHexString($bytes) -ne '7B2268656C6C6F223A22776F726C64227D0A') {
            throw 'The native host did not preserve the literal document bytes.'
        }
        $evidence.passedHttpCases++
    }
    finally { $document.Dispose() }
    $body = [Net.Http.ByteArrayContent]::new([byte[]]@(1))
    try {
        $write = $client.PutAsync($root + '/documents/main/assets/item', $body).GetAwaiter().GetResult()
        try {
            if ([int]$write.StatusCode -ne 405) { throw 'The aggregate accepted a mutation.' }
            $evidence.passedHttpCases++
        }
        finally { $write.Dispose() }
    }
    finally { $body.Dispose() }
    $cases = @(
        [pscustomobject]@{ Uri = "http://127.0.0.1:$port/registries/missing"; Status = 404 },
        [pscustomobject]@{ Uri = $root + '?doc'; Status = 200 }
    )
    foreach ($case in $cases) {
        $response = $client.GetAsync($case.Uri).GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -ne $case.Status) {
                throw "Native rejection returned $([int]$response.StatusCode), expected $($case.Status)."
            }
            if ($case.Status -eq 200) {
                $view = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
                if ($view.self -ne '#/' -or $view.PSObject.Properties.Name -contains 'model' -or
                    $view.PSObject.Properties.Name -contains 'modelsource') {
                    throw 'The native Core document view did not return its actual response-local shape.'
                }
            }
            $evidence.passedHttpCases++
        }
        finally { $response.Dispose() }
    }
    if ($evidence.passedHttpCases -ne 6) { throw 'The principal-host case set is incomplete.' }
    if ((Get-FileHash -LiteralPath $native -Algorithm SHA256).Hash.ToLowerInvariant() -ne $evidence.nativeHostSha256 -or
        (Get-FileHash -LiteralPath $managed -Algorithm SHA256).Hash.ToLowerInvariant() -ne $evidence.managedHostSha256) {
        throw 'A runtime artifact changed during qualification.'
    }
    $evidence.status = 'passed'
    Write-Output "Native principal FederationBridge host: 6 HTTP cases passed. Evidence: $output"
}
catch {
    $evidence.status = 'failed'
    $evidence['error'] = $_.Exception.Message
    throw
}
finally {
    if ($null -ne $client) { $client.Dispose() }
    if ($null -ne $handler) { $handler.Dispose() }
    if ($null -ne $process) {
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id
            $process.WaitForExit(5000) | Out-Null
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $configPath -PathType Leaf) { Remove-Item -LiteralPath $configPath }
    $evidence['verifiedAtUtc'] = [DateTime]::UtcNow.ToString('O')
    $evidence | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'evidence.json') -Encoding utf8NoBOM
}
