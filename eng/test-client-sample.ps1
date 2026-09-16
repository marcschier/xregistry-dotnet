# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { throw 'Windows or Linux is required.' }
if ($RuntimeIdentifier -ne "$platform-$architecture") {
    throw 'Native sample qualification must execute on the selected OS architecture.'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = Join-Path $repository "artifacts\client-native\$RuntimeIdentifier"
$project = Join-Path $repository 'samples\XRegistry.Client\XRegistry.Client.csproj'
$oldPath = $env:PATH
Push-Location $repository
try {
    if ($IsWindows) {
        $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
    }
    & dotnet publish $project -c Release -r $RuntimeIdentifier -p:PublishAot=true -o $output --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native client sample publication failed.' }
    $name = if ($IsWindows) { 'XRegistry.Sample.Client.exe' } else { 'XRegistry.Sample.Client' }
    $managedControl = Join-Path $repository "samples\XRegistry.Client\bin\Release\net10.0\$RuntimeIdentifier\XRegistry.Sample.Client.dll"
    & python (Join-Path $PSScriptRoot 'verify_client_sample.py') --client (Join-Path $output $name) `
        --managed-control $managedControl --report (Join-Path $output 'qualification.json')
    if ($LASTEXITCODE -ne 0) { throw 'Native client sample fixture qualification failed.' }
}
finally {
    Pop-Location
    $env:PATH = $oldPath
}
