[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$osPrefix = if ($IsWindows) { 'win-' } elseif ($IsLinux) { 'linux-' } else { throw 'Unsupported native probe OS.' }
$hostRuntime = $osPrefix + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if (-not $RuntimeIdentifier) { $RuntimeIdentifier = $hostRuntime }
if ($RuntimeIdentifier -ne $hostRuntime) {
    throw 'This qualification probe must execute on the requested native architecture, not cross-compile only.'
}
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = Join-Path $root ('artifacts\package-smoke\' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $run 'feed'
$consumer = Join-Path $run 'consumer'
$cache = Join-Path $run 'cache'
New-Item -ItemType Directory -Path $feed, $consumer, $cache | Out-Null
$originalPath = $env:PATH
Push-Location $root
try {
    if ($IsWindows) {
        $installer = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
        if (Test-Path -LiteralPath (Join-Path $installer 'vswhere.exe')) {
            $env:PATH = $installer + [IO.Path]::PathSeparator + $env:PATH
        }
    }
    & python (Join-Path $PSScriptRoot 'sync_models.py') --check
    if ($LASTEXITCODE -ne 0) { throw 'Model assets are not the pinned source bytes.' }

    foreach ($project in @('src\XRegistry\XRegistry.csproj', 'src\XRegistry.Models\XRegistry.Models.csproj')) {
        & dotnet pack (Join-Path $root $project) -c Release --no-restore --nologo -o $feed -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Packing the local probe dependency failed: $project" }
    }
    $packages = @(Get-ChildItem -LiteralPath $feed -Filter 'XRegistry.Models.*.nupkg')
    if ($packages.Count -ne 1) { throw 'The isolated feed must contain exactly one model package version.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
    try {
        $entry = $archive.GetEntry('XRegistry.Models.nuspec')
        if ($null -eq $entry) { throw 'The model package is missing its nuspec.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml]$nuspec = $reader.ReadToEnd() }
        finally { $reader.Dispose() }
        $version = $nuspec.package.metadata.version
    }
    finally { $archive.Dispose() }
    if ($version -notmatch '^[0-9A-Za-z.+-]+$') { throw 'Unexpected package version in the probe feed.' }

    $templates = Join-Path $root 'tests\PackageSmoke'
    foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        Copy-Item -LiteralPath (Join-Path $templates ($name + '.template')) -Destination (Join-Path $consumer $name)
    }
    Copy-Item -LiteralPath (Join-Path $templates 'ModelConsumer.csproj.template') `
        -Destination (Join-Path $consumer 'ModelConsumer.csproj')
    Copy-Item -LiteralPath (Join-Path $templates 'ModelConsumer.cs') -Destination (Join-Path $consumer 'Program.cs')
    $escapedFeed = [Security.SecurityElement]::Escape($feed)
    $configuration = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="probe" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="probe"><package pattern="XRegistry*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@
    [IO.File]::WriteAllText((Join-Path $consumer 'NuGet.config'), $configuration)

    foreach ($framework in @('net8.0', 'net10.0')) {
        $output = Join-Path $run ($framework + '-' + $RuntimeIdentifier)
        & dotnet publish (Join-Path $consumer 'ModelConsumer.csproj') -c Release -r $RuntimeIdentifier `
            "-p:ProbeFramework=$framework" "-p:XRegistryPackageVersion=$version" `
            "-p:RestorePackagesPath=$cache" -o $output --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Native clean-package publication failed for $framework." }
        $name = if ($IsWindows) { 'ModelConsumer.exe' } else { 'ModelConsumer' }
        $executable = Join-Path $output $name
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The native executable is missing.' }
        & $executable
        if ($LASTEXITCODE -ne 0) { throw "Native clean-package execution failed for $framework." }
        $managedControl = Join-Path $consumer "bin\Release\$framework\$RuntimeIdentifier\ModelConsumer.dll"
        & dotnet $managedControl
        if ($LASTEXITCODE -ne 2) { throw "The $framework package consumer failed to reject JIT execution." }
    }
    Write-Output "Clean package-model native evidence retained at $run"
    Write-Output 'Other packages, full protocol conformance and unexecuted native platforms remain unqualified.'
}
finally {
    $env:PATH = $originalPath
    Pop-Location
}
