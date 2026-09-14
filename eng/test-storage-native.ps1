[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier,
    [ValidateSet('net8.0', 'net10.0')]
    [string]$Framework = 'net10.0',
    [Parameter(Mandatory)]
    [string]$DataRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { throw 'Windows or Linux is required.' }
if ($RuntimeIdentifier -ne "$platform-$architecture") {
    throw 'This gate must execute on the selected native OS architecture, not cross-compile or emulate.'
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$data = [IO.Path]::GetFullPath($DataRoot)
[IO.Directory]::CreateDirectory($data) | Out-Null
$output = Join-Path $repository "artifacts\native-storage\$RuntimeIdentifier-$Framework"
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$oldTmpDir = $env:TMPDIR
$oldPath = $env:PATH
Push-Location $repository
try {
    if ($IsWindows) {
        $env:PATH = (Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer') + ';' + $env:PATH
    }

    foreach ($name in @('XRegistry.Storage.File.Tests', 'XRegistry.Storage.CrashProbe')) {
        $project = Join-Path $repository "tests\$name\$name.csproj"
        & dotnet publish $project -c Release -f $Framework -r $RuntimeIdentifier -p:PublishAot=true -o (Join-Path $output $name) --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Native publishing failed for $name." }
    }
    $env:TEMP = $data
    $env:TMP = $data
    $env:TMPDIR = $data
    $suffix = if ($IsWindows) { '.exe' } else { '' }
    $tests = Join-Path $output "XRegistry.Storage.File.Tests\XRegistry.Storage.File.Tests$suffix"
    $probe = Join-Path $output "XRegistry.Storage.CrashProbe\XRegistry.Storage.CrashProbe$suffix"
    if (!(Test-Path -LiteralPath $tests -PathType Leaf) -or !(Test-Path -LiteralPath $probe -PathType Leaf)) {
        throw 'A required native executable is missing.'
    }
    & $tests --minimum-expected-tests 20 --zero-tests-policy strict --timeout 3m --no-ansi --results-directory (Join-Path $output 'results')
    if ($LASTEXITCODE -ne 0) { throw 'Native storage behavior tests failed.' }
    $managedControl = Join-Path $repository "tests\XRegistry.Storage.CrashProbe\bin\Release\$Framework\$RuntimeIdentifier\XRegistry.Storage.CrashProbe.dll"
    & python (Join-Path $PSScriptRoot 'verify_storage_crashes.py') --probe $probe --root $data `
        --managed-control $managedControl --report (Join-Path $output 'crashes.json')
    if ($LASTEXITCODE -ne 0) { throw 'Native process-crash recovery qualification failed.' }
}
finally {
    Pop-Location
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    $env:TMPDIR = $oldTmpDir
    $env:PATH = $oldPath
}
