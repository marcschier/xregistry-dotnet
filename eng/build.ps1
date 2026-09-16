# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    & dotnet restore XRegistry.slnx --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }

    & dotnet build XRegistry.slnx -c $Configuration --no-restore --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    & python (Join-Path $PSScriptRoot 'check_packages.py') --evaluate
    if ($LASTEXITCODE -ne 0) { throw 'Package/project contract validation failed.' }
}
finally {
    Pop-Location
}
