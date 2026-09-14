[CmdletBinding()]
param(
    [string[]]$Filter = @('*ParseMetadata*'),
    [ValidateSet('Dry', 'Short', 'Default')]
    [string]$Job = 'Short',
    [switch]$CompareRuntimes,
    [string]$Artifacts = 'artifacts\benchmarks'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($CompareRuntimes -and $Job -ne 'Short') {
    throw 'Matched runtime comparisons require -Job Short; use the benchmark CLI for other explicit iteration counts.'
}
if ($Filter.Count -eq 0 -or ($Filter | Where-Object { [string]::IsNullOrWhiteSpace($_) })) {
    throw 'At least one non-empty benchmark filter is required.'
}

Push-Location (Join-Path $PSScriptRoot '..')
try {
    & dotnet build tests\XRegistry.Benchmarks\XRegistry.Benchmarks.csproj -c Release --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed.' }

    $output = New-Item -ItemType Directory -Path $Artifacts -Force
    $log = Join-Path $output.FullName ("run-{0:yyyyMMdd-HHmmss-fffffff}.log" -f [DateTime]::UtcNow)
    $arguments = @(
        'run', '--project', 'tests\XRegistry.Benchmarks\XRegistry.Benchmarks.csproj',
        '-c', 'Release', '-f', 'net10.0', '--no-build', '--', '--filter'
    ) + $Filter + @('--noOverwrite', '--artifacts', $output.FullName, '--exporters', 'json')
    if ($Job -ne 'Default') { $arguments += @('--job', $Job) }
    if ($CompareRuntimes) { $arguments += @('--runtimes', 'net8.0', 'net10.0', '--apples') }

    & dotnet @arguments *> $log
    if ($LASTEXITCODE -ne 0) { throw "Benchmark selection, validation or execution failed. See $log" }
    Write-Output "Benchmark reports: $($output.FullName)"
    Write-Output "Execution log: $log"
}
finally {
    Pop-Location
}
