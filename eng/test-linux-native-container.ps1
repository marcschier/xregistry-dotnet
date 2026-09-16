# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Image,
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+$')]
    [string]$Name,
    [Parameter(Mandatory)]
    [ValidateRange(1, 100000)]
    [int]$MinimumTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$engine = (& docker info --format '{{.OSType}} {{.Architecture}}').Trim()
if ($LASTEXITCODE -ne 0 -or $engine -ne 'linux x86_64') { throw 'A native Linux x64 Docker engine is required.' }
$suffix = [Guid]::NewGuid().ToString('N')
$volume = "xregistry-native-$suffix"
$container = "xregistry-native-$suffix"
$output = Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) "artifacts\linux-native\$Name-$suffix"
New-Item -ItemType Directory -Path $output | Out-Null
$volumeCreated = $false
try {
    & docker volume create --label 'xregistry.purpose=native-test' $volume | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The isolated test volume could not be created.' }
    $volumeCreated = $true
    & docker run --rm --network none --user 0 --mount "type=volume,source=$volume,target=/data" `
        --entrypoint chown $Image '1654:1654' /data
    if ($LASTEXITCODE -ne 0) { throw 'The isolated test volume could not be assigned to the non-root runner.' }
    & docker run --name $container --network none --read-only --pids-limit 256 --memory 3g `
        --mount "type=volume,source=$volume,target=/data" `
        --tmpfs '/tmp:rw,nosuid,nodev,size=128m' `
        --tmpfs '/native/ProducedLayouts:rw,nosuid,nodev,size=128m,uid=1654,gid=1654' `
        $Image --minimum-expected-tests $MinimumTests *> (Join-Path $output 'run.log')
    $testExit = $LASTEXITCODE
    & docker cp "${container}:/data/results" (Join-Path $output 'results')
    if ($LASTEXITCODE -ne 0) { throw "The native test report is missing; see $output." }
    Get-Content -LiteralPath (Join-Path $output 'run.log') -Tail 18
    if ($testExit -ne 0) { throw "Linux native tests failed with exit $testExit; evidence: $output" }
    Write-Output "Linux native evidence: $output"
}
finally {
    & docker rm -f $container 2>$null | Out-Null
    if ($volumeCreated) {
        & docker volume rm $volume | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "The exact owned test volume still needs cleanup: $volume" }
    }
}
