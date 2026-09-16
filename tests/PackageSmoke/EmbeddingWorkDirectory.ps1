# Copyright (c) 2026 xregistry-dotnet contributors.
# SPDX-License-Identifier: MIT

function New-EmbeddingWorkDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RunDirectory,
        [string]$WorkRoot
    )

    if ($PSBoundParameters.ContainsKey('WorkRoot')) {
        if ([string]::IsNullOrWhiteSpace($WorkRoot) -or ![IO.Path]::IsPathFullyQualified($WorkRoot)) {
            throw 'WorkRoot must be an existing absolute directory owned/provisioned by the caller.'
        }
        $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($WorkRoot))
        if (!(Test-Path -LiteralPath $root -PathType Container)) {
            throw 'WorkRoot must already exist; the embedding gate does not provision the caller root.'
        }
        $callerSupplied = $true
    }
    else {
        $root = Join-Path ([IO.Path]::GetFullPath($RunDirectory)) 'work'
        New-Item -ItemType Directory -Path $root -Force -ErrorAction Stop | Out-Null
        $callerSupplied = $false
    }
    if ((Get-Item -LiteralPath $root -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The work root must be an ordinary directory, not a replaced link.'
    }
    $directory = Join-Path $root ('embedding-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
    return [pscustomobject]@{ Root = $root; Directory = $directory; CallerSupplied = $callerSupplied }
}

function Remove-EmbeddingWorkDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [psobject]$Work
    )

    $root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Work.Root))
    $directory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Work.Directory))
    $comparison = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
    if ($comparison.Equals($root, $directory) -or
        !$comparison.Equals([IO.Path]::GetDirectoryName($directory), $root) -or
        [IO.Path]::GetFileName($directory) -notmatch '^embedding-[0-9a-f]{32}$') {
        throw 'Refusing to remove anything except the unique direct work child.'
    }
    if (!(Test-Path -LiteralPath $root -PathType Container) -or
        ((Get-Item -LiteralPath $root -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing work cleanup because the caller root is missing or was replaced by a link.'
    }
    if (!(Test-Path -LiteralPath $directory)) {
        return
    }
    $entry = Get-Item -LiteralPath $directory -Force -ErrorAction Stop
    if (!$entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Refusing to recursively remove a replaced work child.'
    }
    Remove-Item -LiteralPath $directory -Recurse -Force -ErrorAction Stop
}
