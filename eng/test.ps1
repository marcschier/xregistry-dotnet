[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Project,
    [ValidateRange(1, 32)]
    [int]$MaxParallelTestModules = 2,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & python -m unittest discover -s (Join-Path $PSScriptRoot '..\tests\Tooling') -v
    if ($LASTEXITCODE -ne 0) { throw 'Repository tooling tests failed.' }

    & python (Join-Path $PSScriptRoot 'specification\manage.py') check
    if ($LASTEXITCODE -ne 0) { throw 'Pinned specification consistency failed.' }
    & python (Join-Path $PSScriptRoot 'sync_models.py') --check
    if ($LASTEXITCODE -ne 0) { throw 'Pinned model resource consistency failed.' }

    $arguments = @(
        'test', '-c', $Configuration, '--minimum-expected-tests', '1',
        '--zero-tests-policy', 'strict', '--timeout', '5m', '--no-ansi',
        '--max-parallel-test-modules', $MaxParallelTestModules
    )
    if ($Project) { $arguments += @('--project', $Project) }
    else { $arguments += @('--solution', 'XRegistry.slnx') }
    if ($NoBuild) { $arguments += '--no-build' }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Managed tests failed or did not execute required cases.' }
}
finally {
    Pop-Location
}
