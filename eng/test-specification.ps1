# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
    [ValidateRange(1, 900)]
    [int]$TimeoutSeconds = 600
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runner = Join-Path $PSScriptRoot 'specification\run_corrected_oracles.py'
Push-Location $root
try {
    $failed = @()
    foreach ($mode in @('original', 'corrected')) {
        Write-Output "Executing $mode independent Python oracles in an isolated artifact directory."
        & python -B $runner --mode $mode --timeout-seconds $TimeoutSeconds
        if ($LASTEXITCODE -ne 0) { $failed += $mode }
    }
    if ($failed.Count -ne 0) { throw "Independent oracle runs failed: $($failed -join ', ')." }
    Write-Output 'Original and corrected reports are separate; neither is .NET or native qualification evidence.'
}
finally {
    Pop-Location
}
