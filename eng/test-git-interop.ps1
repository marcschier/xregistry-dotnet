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
$restoreRoot = Join-Path $output ('restore-' + [Guid]::NewGuid().ToString('N'))
$restoreProps = Join-Path $repository 'tests\XRegistry.Git.InteropProbe\InteropRestore.props'
$probeLock = Join-Path $repository 'tests\XRegistry.Git.InteropProbe\packages.lock.json'
if (-not (Test-Path -LiteralPath $restoreProps -PathType Leaf)) {
    throw 'The isolated Git interoperability restore settings are missing.'
}
New-Item -ItemType Directory -Path $restoreRoot | Out-Null
$oldPath = $env:PATH
Push-Location $repository
try {
    Copy-Item -LiteralPath $probeLock -Destination (Join-Path $restoreRoot 'XRegistry.Git.InteropProbe.lock.json')
    if ($IsWindows) {
        $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
    }
    & dotnet publish $project -c Release -f $Framework -r $RuntimeIdentifier `
        "-p:CustomBeforeMicrosoftCommonProps=$restoreProps" "-p:GitInteropLockRoot=$restoreRoot" `
        -o $nativeOutput --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'The native-only Git probe did not publish with the locked dependency graph.' }
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
    foreach ($file in Get-ChildItem -LiteralPath $restoreRoot -File) {
        Remove-Item -LiteralPath $file.FullName
    }
    Remove-Item -LiteralPath $restoreRoot
}
