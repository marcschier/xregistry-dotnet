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
    throw 'The native bridge must execute on the selected OS architecture.'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sample = Join-Path $repository 'samples\XRegistry.FederationBridge'
$output = Join-Path $repository "artifacts\bridge-native\$RuntimeIdentifier"
$oldPath = $env:PATH
Push-Location $repository
try {
    if ($IsWindows) {
        $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
    }
    & dotnet publish (Join-Path $sample 'XRegistry.FederationBridge.csproj') -c Release `
        -r $RuntimeIdentifier -p:PublishAot=true -o $output --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Native federation bridge publication failed.' }
    $name = if ($IsWindows) { 'XRegistry.FederationBridge.exe' } else { 'XRegistry.FederationBridge' }
    $managed = Join-Path $sample "bin\Release\net10.0\$RuntimeIdentifier\XRegistry.FederationBridge.dll"
    & pwsh -NoProfile -File (Join-Path $sample 'verify-native.ps1') -NativeHost (Join-Path $output $name) `
        -ManagedHost $managed -OutputDirectory (Join-Path $output 'evidence')
    if ($LASTEXITCODE -ne 0) { throw 'Native federation bridge execution or JIT negative control failed.' }
}
finally {
    Pop-Location
    $env:PATH = $oldPath
}
