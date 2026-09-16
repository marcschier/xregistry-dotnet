# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'linux-x64')]
    [string]$RuntimeIdentifier,

    [ValidateSet('net8.0', 'net10.0')]
    [string]$Framework = 'net10.0',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\git-interop')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { throw 'Windows or Linux is required.' }
if ($RuntimeIdentifier -ne "$platform-$architecture") {
    throw 'Git interoperability must execute on the selected native host, not cross-compile or emulate.'
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repository 'tests\XRegistry.Git.InteropProbe\XRegistry.Git.InteropProbe.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$nativeOutput = Join-Path $output "native\$RuntimeIdentifier-$Framework"
$oldPath = $env:PATH
Push-Location $repository
try {
    if ($IsWindows) {
        $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
    }
    & dotnet publish $project -c Release -f $Framework -r $RuntimeIdentifier `
        -o $nativeOutput --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'The native-only Git probe did not publish.' }
    $suffix = if ($IsWindows) { '.exe' } else { '' }
    $native = Join-Path $nativeOutput "XRegistry.Git.InteropProbe$suffix"
    $managedControl = Join-Path $repository "tests\XRegistry.Git.InteropProbe\bin\Release\$Framework\$RuntimeIdentifier\XRegistry.Git.InteropProbe.dll"
    & python (Join-Path $PSScriptRoot 'verify_git_http.py') --probe $native `
        --managed-control $managedControl `
        --framework $Framework --rid $RuntimeIdentifier --output (Join-Path $output 'runs')
    if ($LASTEXITCODE -ne 0) { throw 'Controlled reference Git smart-HTTP interoperability did not qualify.' }
}
finally {
    Pop-Location
    $env:PATH = $oldPath
}
