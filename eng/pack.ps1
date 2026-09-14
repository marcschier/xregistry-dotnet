[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Release
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Push-Location (Join-Path $PSScriptRoot '..')
try {
    if ($Release) {
        & python (Join-Path $PSScriptRoot 'check_packages.py') --release
        if ($LASTEXITCODE -ne 0) { throw 'Package release qualification is incomplete.' }
        & python (Join-Path $PSScriptRoot 'specification\manage.py') release
        if ($LASTEXITCODE -ne 0) { throw 'Specification release qualification is incomplete.' }
        $commit = & git rev-parse --verify HEAD
        if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
            throw 'A release requires a real source commit.'
        }
        $dirty = & git status --porcelain
        if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'A release requires a clean source worktree.' }
    }
    else {
        & python (Join-Path $PSScriptRoot 'check_packages.py') --evaluate
        if ($LASTEXITCODE -ne 0) { throw 'Package/project inventory is invalid.' }
        Write-Warning 'Creating local development packages, not qualified release artifacts.'
    }

    $manifest = Get-Content -LiteralPath eng\packages.json -Raw | ConvertFrom-Json
    $outputs = [Collections.Generic.List[object]]::new()
    foreach ($package in $manifest.packages) {
        & dotnet pack (Join-Path (Get-Location) $package.project) -c $Configuration --no-restore --nologo `
            -o (Join-Path $PSScriptRoot '..\artifacts\packages') -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Packing $($package.id) failed." }
        $outputs.Add([ordered]@{ id = $package.id; project = $package.project; status = $package.status })
    }
    [ordered]@{
        schemaVersion = 1
        releaseQualified = [bool]$Release
        packages = $outputs.ToArray()
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath artifacts\packages\inventory.json -Encoding utf8NoBOM
}
finally {
    Pop-Location
}
