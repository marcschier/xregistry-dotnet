[CmdletBinding()]
param(
    [ValidateRange(10, 300)]
    [int]$StartupTimeoutSeconds = 120,

    [string]$NativeClient,

    [string]$NativeClientImage,

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\interop')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($NativeClient -and $NativeClientImage) {
    throw 'Select a native client executable or a native client container image, not both.'
}

$lockPath = Join-Path $PSScriptRoot '..\interop\upstream-lock.json'
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or
    $lock.image -notmatch '^ghcr\.io/xregistry/xrserver-all@sha256:[0-9a-f]{64}$' -or
    $lock.platform -ne 'linux/amd64' -or
    $lock.commit -notmatch '^[0-9a-f]{40}$') {
    throw 'Invalid immutable upstream lock.'
}

$name = 'xregistry-qualify-' + [Guid]::NewGuid().ToString('N')
$output = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) $name
New-Item -ItemType Directory -Path $output | Out-Null
$container = $null
$failure = $null
$client = $null
$handler = $null

function Invoke-CheckedRequest {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][int]$Status,
        [byte[]]$Body,
        [string]$ContentType
    )

    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method), $Path)
    try {
        if ($null -ne $Body) {
            $request.Content = [Net.Http.ByteArrayContent]::new($Body)
            if ($ContentType) {
                $request.Content.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::Parse($ContentType)
            }
        }
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        try {
            $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
            if ([int]$response.StatusCode -ne $Status) {
                $detail = [Text.Encoding]::UTF8.GetString($bytes)
                throw "$Method $Path returned $([int]$response.StatusCode), expected ${Status}: $detail"
            }
            return ,$bytes
        }
        finally {
            $response.Dispose()
        }
    }
    finally {
        $request.Dispose()
    }
}

try {
    foreach ($entry in @('/xrserver', '/xr')) {
        $version = (& docker run --rm --platform $lock.platform --entrypoint $entry $lock.image --version)
        if ($LASTEXITCODE -ne 0 -or ($version -join "`n").Trim() -ne $lock.executableVersion) {
            throw "Unexpected pinned executable version for ${entry}: $version"
        }
    }

    $created = & docker run --detach --platform $lock.platform --name $name `
        --publish '127.0.0.1::8080' $lock.image --db interop --registry interop --rootapp=xreg
    if ($LASTEXITCODE -ne 0 -or ($created -join '').Trim() -notmatch '^[0-9a-f]{64}$') {
        throw 'Could not start the isolated upstream fixture.'
    }
    $container = ($created -join '').Trim()
    $port = & docker port $container '8080/tcp'
    if ($LASTEXITCODE -ne 0 -or ($port -join '').Trim() -notmatch '^127\.0\.0\.1:(\d+)$') {
        throw 'The fixture did not publish exactly one loopback port.'
    }
    $base = "http://127.0.0.1:$($Matches[1])/xreg/"
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler, $false)
    $client.BaseAddress = [Uri]::new($base)
    $client.Timeout = [TimeSpan]::FromSeconds(10)

    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $response = $null
        try {
            $response = $client.GetAsync('').GetAwaiter().GetResult()
            if ([int]$response.StatusCode -eq 200) {
                $rootBytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
                $root = [Text.Encoding]::UTF8.GetString($rootBytes) | ConvertFrom-Json
                if ($root.registryid -ne 'interop' -or $root.specversion -ne $lock.specVersion -or $root.xid -ne '/') {
                    throw 'The ready fixture returned an unexpected registry identity.'
                }
                [IO.File]::WriteAllBytes((Join-Path $output 'root.json'), $rootBytes)
                $ready = $true
                break
            }
        }
        catch [Net.Http.HttpRequestException] {
            # A refused/reset connection is expected only during bounded startup.
        }
        finally {
            if ($null -ne $response) { $response.Dispose() }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'The upstream API did not become ready within the startup deadline.' }

    $capabilities = Invoke-CheckedRequest -Path 'capabilities' -Method GET -Status 200
    [IO.File]::WriteAllBytes((Join-Path $output 'capabilities.json'), $capabilities)
    $caps = [Text.Encoding]::UTF8.GetString($capabilities) | ConvertFrom-Json
    if ($caps.specversions -notcontains $lock.specVersion) {
        throw 'The peer does not advertise the pinned specification version.'
    }

    $modelBytes = [Text.Encoding]::UTF8.GetBytes(
        '{"groups":{"dirs":{"singular":"dir","resources":{"files":{"singular":"file"}}}}}'
    )
    $null = Invoke-CheckedRequest -Path 'modelsource' -Method PUT -Status 200 `
        -Body $modelBytes -ContentType 'application/json'
    $document = [byte[]]@(0, 255, 13, 10, 65)
    $null = Invoke-CheckedRequest -Path 'dirs/d1/files/f1/versions/v1' -Method PUT -Status 201 `
        -Body $document -ContentType 'application/octet-stream'
    $readBack = Invoke-CheckedRequest -Path 'dirs/d1/files/f1/versions/v1' -Method GET -Status 200
    if ([Convert]::ToHexString($readBack) -ne '00FF0D0A41') {
        throw 'The upstream fixture did not preserve the exact independently chosen document bytes.'
    }
    $details = Invoke-CheckedRequest -Path 'dirs/d1/files/f1/versions/v1$details' -Method GET -Status 200
    $metadata = [Text.Encoding]::UTF8.GetString($details) | ConvertFrom-Json
    if ($metadata.versionid -ne 'v1' -or $metadata.xid -ne '/dirs/d1/files/f1/versions/v1') {
        throw 'The peer returned incorrect explicit-Version metadata.'
    }
    [IO.File]::WriteAllBytes((Join-Path $output 'version-details.json'), $details)

    $clientBase = 'http://127.0.0.1:8080/xreg/'
    $checker = & docker run --rm --platform $lock.platform --network "container:$container" --entrypoint /xr $lock.image `
        --server $clientBase conform --logs --warns --skips --depth 0 --nowrap 2>&1
    $checkerCode = $LASTEXITCODE
    $checker | Set-Content -LiteralPath (Join-Path $output 'xr-conform.log') -Encoding utf8NoBOM
    if ($checkerCode -ne 0) {
        throw "The upstream checker failed its own seeded peer fixture with exit code $checkerCode."
    }
    & python (Join-Path $PSScriptRoot 'check_upstream_output.py') `
        (Join-Path $output 'xr-conform.log') --expected-passes $lock.environmentFixtureCheckerPasses
    if ($LASTEXITCODE -ne 0) {
        throw 'The upstream checker did not execute the expected clean fixture coverage.'
    }
    if ($NativeClient) {
        $native = [IO.Path]::GetFullPath($NativeClient)
        if (-not (Test-Path -LiteralPath $native -PathType Leaf)) { throw 'The native client executable is missing.' }
        & $native --server $base | Tee-Object -FilePath (Join-Path $output 'native-client.log')
        if ($LASTEXITCODE -ne 0) { throw 'The native .NET client failed its pinned-peer contract slice.' }
    }
    elseif ($NativeClientImage) {
        $imageId = & docker image inspect $NativeClientImage --format '{{.Id}}'
        if ($LASTEXITCODE -ne 0 -or ($imageId -join '').Trim() -notmatch '^sha256:[0-9a-f]{64}$') {
            throw 'The local native client image could not be pinned to one content digest.'
        }
        & docker run --rm --platform $lock.platform --network "container:$container" `
            ($imageId -join '').Trim() --server $clientBase |
            Tee-Object -FilePath (Join-Path $output 'native-client.log')
        if ($LASTEXITCODE -ne 0) { throw 'The native Linux client container failed its pinned-peer contract slice.' }
    }

    [ordered]@{
        schemaVersion = 1
        image = $lock.image
        commit = $lock.commit
        executableVersion = $lock.executableVersion
        verifiedAtUtc = [DateTime]::UtcNow.ToString('O')
        apiReadiness = 'passed'
        exactBinaryDocument = 'passed'
        explicitVersionMetadata = 'passed'
        peerCheckerExitCode = $checkerCode
        checkerCoverageQualified = $true
        dotnetClientInteroperability = $(if ($NativeClient -or $NativeClientImage) { 'initial-slice-passed' } else { 'not-executed' })
        dotnetServerInteroperability = 'not-executed'
        note = 'Peer environment qualification only. The fixed checker fixture is accounted for; actual .NET interoperability requires separate evidence.'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'environment-evidence.json') -Encoding utf8NoBOM
    Write-Output "Pinned peer environment qualified at $output"
    Write-Output 'This does not claim full bidirectional interoperability or full specification coverage.'
}
catch {
    $failure = $_
}
finally {
    if ($null -ne $client) { $client.Dispose() }
    if ($null -ne $handler) { $handler.Dispose() }
    if ($null -ne $container) {
        & docker logs $container 2>&1 | Set-Content -LiteralPath (Join-Path $output 'server.log') -Encoding utf8NoBOM
        $logCode = $LASTEXITCODE
        & docker rm --force --volumes $container | Out-Null
        $removeCode = $LASTEXITCODE
        if ($logCode -ne 0 -or $removeCode -ne 0) {
            if ($null -eq $failure) {
                $failure = "Upstream fixture cleanup failed (logs=$logCode, remove=$removeCode)."
            }
            else {
                Write-Warning "Additional cleanup failure (logs=$logCode, remove=$removeCode)."
            }
        }
    }
}
if ($null -ne $failure) { throw $failure }
