# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier,
    [Parameter(Mandatory)]
    [string]$DataRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { throw 'Windows or Linux is required.' }
if ($RuntimeIdentifier -ne "$platform-$architecture") {
    throw 'The native server must execute on the selected OS architecture.'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repository 'samples\XRegistry.FileServer\XRegistry.FileServer.csproj'
$output = Join-Path $repository "artifacts\file-server-native\$RuntimeIdentifier"
$data = [IO.Path]::GetFullPath($DataRoot)
$oldPath = $env:PATH
Push-Location $repository
try {
    if ($IsWindows) {
        $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
    }
    & dotnet publish $project -c Release -r $RuntimeIdentifier -p:PublishAot=true -o $output --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native file-server publication failed.' }
    $name = if ($IsWindows) { 'XRegistry.Sample.FileServer.exe' } else { 'XRegistry.Sample.FileServer' }
    $control = Join-Path $repository "samples\XRegistry.FileServer\bin\Release\net10.0\$RuntimeIdentifier\XRegistry.Sample.FileServer.dll"
    & python (Join-Path $PSScriptRoot 'verify_file_server.py') --server (Join-Path $output $name) `
        --managed-control $control --data-root $data --output (Join-Path $output 'evidence')
    if ($LASTEXITCODE -ne 0) { throw 'Native durable HTTP server qualification failed.' }
}
finally {
    Pop-Location
    $env:PATH = $oldPath
}
