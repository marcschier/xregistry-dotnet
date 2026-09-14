[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$RuntimeIdentifier,
    [ValidateSet('net8.0', 'net10.0')]
    [string[]]$Framework = @('net8.0', 'net10.0'),
    [ValidateNotNullOrEmpty()]
    [string]$WorkRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$platform = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } else { throw 'Only Windows/Linux native hosts are supported.' }
$architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$hostRuntime = "$platform-$architecture"
if (!$RuntimeIdentifier) { $RuntimeIdentifier = $hostRuntime }
if ($RuntimeIdentifier -ne $hostRuntime -or
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne [Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    throw 'The embedding gate requires execution on the selected native OS/architecture; cross-compilation is not qualification.'
}
if ($Framework.Count -eq 0 -or @($Framework | Select-Object -Unique).Count -ne $Framework.Count) {
    throw 'Choose each target framework at most once.'
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run = Join-Path $root ('artifacts\embedding\' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$feed = Join-Path $run 'feed'
$cache = Join-Path $run 'cache'
$packCache = Join-Path $run 'pack-cache'
$scratch = Join-Path $run 'scratch'
$templates = Join-Path $root 'tests\PackageSmoke'
$evidenceTool = Join-Path $templates 'EmbeddingEvidence.py'
. (Join-Path $templates 'EmbeddingWorkDirectory.ps1')
$workSelection = $null
New-Item -ItemType Directory -Path $feed, $cache, $packCache, $scratch | Out-Null
$packageIds = @('XRegistry', 'XRegistry.Models', 'XRegistry.Validation', 'XRegistry.Server', 'XRegistry.Client', 'XRegistry.AspNetCore',
    'XRegistry.Storage.File', 'XRegistry.Federation', 'XRegistry.Bindings.Oci', 'XRegistry.Bindings.File', 'XRegistry.Bindings.Git')
$caseCount = @(Get-Content -LiteralPath (Join-Path $templates 'EmbeddingCases.json') -Raw | ConvertFrom-Json).Count
$oldPath = $env:PATH
$scratchEnvironment = @{
    TEMP = (Join-Path $scratch 'temp')
    TMP = (Join-Path $scratch 'temp')
    TMPDIR = (Join-Path $scratch 'temp')
    DOTNET_CLI_HOME = (Join-Path $scratch 'dotnet-home')
    NUGET_HTTP_CACHE_PATH = (Join-Path $scratch 'nuget-http')
    NUGET_SCRATCH = (Join-Path $scratch 'nuget-scratch')
}
$processControls = @{
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = '1'
    MSBUILDDISABLENODEREUSE = '1'
}
$oldEnvironment = @{}
foreach ($name in (@($scratchEnvironment.Keys) + @($processControls.Keys))) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
$summary = [ordered]@{
    schemaVersion = 1
    scope = 'development-package-embedding'
    releaseQualified = $false
    runtimeIdentifier = $RuntimeIdentifier
    outcome = 'running'
    frameworks = @()
    sourceManifest = 'source-before.json'
    packageVersions = 'versions.json'
}

function Invoke-Recorded {
    param([string]$Program, [string[]]$ArgumentList, [string]$Log, [int]$ExpectedExit = 0)
    & $Program @ArgumentList 2>&1 | Tee-Object -FilePath $Log
    $code = $LASTEXITCODE
    if ($code -ne $ExpectedExit) {
        throw "Command returned $code rather than $ExpectedExit; evidence: $Log"
    }
}

Push-Location $root
try {
    $workSelection = if ($PSBoundParameters.ContainsKey('WorkRoot')) {
        New-EmbeddingWorkDirectory -RunDirectory $run -WorkRoot $WorkRoot
    }
    else {
        New-EmbeddingWorkDirectory -RunDirectory $run
    }
    $summary.workRoot = $workSelection.Root
    $summary.workDirectory = $workSelection.Directory
    $summary.callerSuppliedWorkRoot = $workSelection.CallerSupplied
    foreach ($name in $scratchEnvironment.Keys) {
        New-Item -ItemType Directory -Path $scratchEnvironment[$name] -Force | Out-Null
        [Environment]::SetEnvironmentVariable($name, $scratchEnvironment[$name])
    }
    foreach ($name in $processControls.Keys) {
        [Environment]::SetEnvironmentVariable($name, $processControls[$name])
    }
    $summary.scratchPolicy = 'Run-owned producer/consumer caches and temporary directories on the artifact volume.'
    if ($IsWindows) {
        $env:PATH = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer;' + $env:PATH
    }
    & python $evidenceTool sources --root $root --output (Join-Path $run 'source-before.json')
    if ($LASTEXITCODE -ne 0) { throw 'Source fingerprinting failed.' }
    $planned = Get-Content -LiteralPath (Join-Path $root 'eng\packages.json') -Raw | ConvertFrom-Json
    if (@(Compare-Object ($planned.packages.id | Sort-Object) ($packageIds | Sort-Object)).Count -ne 0) {
        throw 'The consumer must include every runtime package declared in eng/packages.json.'
    }
    & python $evidenceTool fixtures --root $root --destination (Join-Path $run 'fixtures') --output (Join-Path $run 'fixture-inventory.json')
    if ($LASTEXITCODE -ne 0) { throw 'Independent fixture staging failed.' }
    $summary.fixtureInventory = 'fixture-inventory.json'
    $summary.packageStatuses = @($planned.packages | Where-Object { $_.id -in $packageIds } | Select-Object id, status)
    foreach ($id in $packageIds) {
        $project = Join-Path $root "src\$id\$id.csproj"
        Invoke-Recorded -Program dotnet -ArgumentList @(
            'pack', $project, '-c', 'Release', '--artifacts-path', (Join-Path $run 'pack-build'),
            "-p:RestorePackagesPath=$packCache", '-p:RestoreLockedMode=true', '-p:UseSharedCompilation=false',
            '--nologo', '-o', $feed, '-v', 'minimal'
        ) -Log (Join-Path $run ("pack-$id.log"))
    }
    & python $evidenceTool sources --root $root --output (Join-Path $run 'source-after-pack.json')
    if ($LASTEXITCODE -ne 0) { throw 'Post-pack source fingerprinting failed.' }
    $before = Get-Content -LiteralPath (Join-Path $run 'source-before.json') -Raw | ConvertFrom-Json
    $after = Get-Content -LiteralPath (Join-Path $run 'source-after-pack.json') -Raw | ConvertFrom-Json
    if ($before.sha256 -ne $after.sha256) {
        throw 'Source inputs changed while packing; discard the mixed feed and run a fresh gate.'
    }
    $summary.sourceSha256 = $before.sha256

    $versions = [ordered]@{}
    foreach ($package in Get-ChildItem -LiteralPath $feed -Filter '*.nupkg') {
        $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
        try {
            $entries = @($archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
            if ($entries.Count -ne 1) { throw 'Expected one nuspec in each development package.' }
            $reader = [IO.StreamReader]::new($entries[0].Open())
            try { [xml]$nuspec = $reader.ReadToEnd() }
            finally { $reader.Dispose() }
            $id = [string]$nuspec.package.metadata.id
            $version = [string]$nuspec.package.metadata.version
            if ($id -notin $packageIds -or $versions.Contains($id) -or $version -notmatch '^[0-9A-Za-z.+-]+$') {
                throw 'The fresh feed has unexpected package identities or versions.'
            }
            $versions[$id] = $version
        }
        finally { $archive.Dispose() }
    }
    if ($versions.Count -ne $packageIds.Count) { throw 'The development feed must contain all eleven selected runtime packages.' }
    $versionsPath = Join-Path $run 'versions.json'
    $versions | ConvertTo-Json | Set-Content -LiteralPath $versionsPath -Encoding utf8

    foreach ($tfm in $Framework) {
        $consumer = Join-Path $run "consumer\$tfm"
        $build = Join-Path $run "consumer-build\$tfm"
        $output = Join-Path $run "native\$tfm-$RuntimeIdentifier"
        New-Item -ItemType Directory -Path $consumer | Out-Null
        foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
            Copy-Item -LiteralPath (Join-Path $templates ("Embedding$name.template")) -Destination (Join-Path $consumer $name)
        }
        foreach ($source in Get-ChildItem -LiteralPath $templates -Filter 'Embedding*.cs') {
            Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $consumer $source.Name)
        }
        $projectText = Get-Content -LiteralPath (Join-Path $templates 'EmbeddingConsumer.csproj.template') -Raw
        $projectText = $projectText.Replace('@@FRAMEWORK@@', $tfm)
        foreach ($id in $packageIds) { $projectText = $projectText.Replace("@@$id@@", $versions[$id]) }
        [IO.File]::WriteAllText((Join-Path $consumer 'EmbeddingConsumer.csproj'), $projectText)
        $config = Get-Content -LiteralPath (Join-Path $templates 'EmbeddingNuGet.config.template') -Raw
        [IO.File]::WriteAllText((Join-Path $consumer 'NuGet.config'),
            $config.Replace('@@FEED@@', [Security.SecurityElement]::Escape($feed)))
        $publishLog = Join-Path $run "publish-$tfm.log"
        Invoke-Recorded -Program dotnet -ArgumentList @(
            'publish', (Join-Path $consumer 'EmbeddingConsumer.csproj'), '-c', 'Release',
            '-r', $RuntimeIdentifier, '--artifacts-path', $build, "-p:RestorePackagesPath=$cache",
            '-p:UseSharedCompilation=false',
            '-p:PublishAot=true', '-p:TrimmerSingleWarn=false', '-p:IlcTreatWarningsAsErrors=true',
            '-o', $output, '--nologo', '-v', 'minimal'
        ) -Log $publishLog
        if (Select-String -LiteralPath $publishLog -Pattern '\bwarning IL\d+\b' -Quiet) {
            throw "The $tfm embedding consumer emitted IL warnings."
        }
        if (!(Select-String -LiteralPath $publishLog -SimpleMatch 'Generating native code' -Quiet)) {
            throw "ILC did not execute for $tfm; reused output is not fresh native evidence."
        }
        $assets = Join-Path $build 'obj\EmbeddingConsumer\project.assets.json'
        $inventory = Join-Path $run "inventory-$tfm.json"
        & python $evidenceTool inventory --assets $assets --versions $versionsPath --feed $feed --cache $cache `
            --framework $tfm --rid $RuntimeIdentifier --destination (Join-Path $run "package-assemblies\$tfm") --output $inventory
        if ($LASTEXITCODE -ne 0) { throw "Package-only dependency/assembly verification failed for $tfm." }

        $fileName = if ($IsWindows) { 'EmbeddingConsumer.exe' } else { 'EmbeddingConsumer' }
        $executable = Join-Path $output $fileName
        $nativeReport = Join-Path $run "native-$tfm.json"
        $work = Join-Path $workSelection.Directory $tfm
        New-Item -ItemType Directory -Path $work | Out-Null
        Invoke-Recorded -Program $executable -ArgumentList @(
            '--rid', $RuntimeIdentifier, '--report', $nativeReport, '--assemblies', $inventory,
            '--fixtures', (Join-Path $run 'fixtures'), '--work', $work
        ) -Log (Join-Path $run "native-$tfm.log")
        & python $evidenceTool report --input $nativeReport --framework $tfm --rid $RuntimeIdentifier --mode native --inventory $inventory
        if ($LASTEXITCODE -ne 0) { throw "Native behavioral evidence failed for $tfm." }

        $managed = @(Get-ChildItem -LiteralPath (Join-Path $build 'bin') -Filter 'EmbeddingConsumer.dll' -Recurse)
        if ($managed.Count -ne 1) { throw 'The managed negative-control assembly must be unambiguous.' }
        $jitReport = Join-Path $run "jit-$tfm.json"
        Invoke-Recorded -Program dotnet -ArgumentList @(
            $managed[0].FullName, '--rid', $RuntimeIdentifier, '--report', $jitReport, '--assemblies', $inventory
        ) -Log (Join-Path $run "jit-$tfm.log") -ExpectedExit 2
        & python $evidenceTool report --input $jitReport --framework $tfm --rid $RuntimeIdentifier --mode jit
        if ($LASTEXITCODE -ne 0) { throw "JIT rejection was not proved for $tfm." }
        $runtimeConfig = Get-Content -LiteralPath (Join-Path $managed[0].DirectoryName 'EmbeddingConsumer.runtimeconfig.json') -Raw |
            ConvertFrom-Json -AsHashtable
        if (!$runtimeConfig.runtimeOptions.ContainsKey('configProperties')) {
            $runtimeConfig.runtimeOptions.configProperties = @{}
        }
        $runtimeConfig.runtimeOptions.configProperties['System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported'] = $false
        $runtimeConfig.runtimeOptions.configProperties['System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled'] = $false
        $maskedConfig = Join-Path $run "masked-$tfm.runtimeconfig.json"
        $runtimeConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $maskedConfig -Encoding utf8
        $maskedReport = Join-Path $run "masked-jit-$tfm.json"
        Invoke-Recorded -Program dotnet -ArgumentList @(
            'exec', '--runtimeconfig', $maskedConfig, $managed[0].FullName,
            '--rid', $RuntimeIdentifier, '--report', $maskedReport, '--assemblies', $inventory
        ) -Log (Join-Path $run "masked-jit-$tfm.log") -ExpectedExit 2
        & python $evidenceTool report --input $maskedReport --framework $tfm --rid $RuntimeIdentifier --mode masked-jit
        if ($LASTEXITCODE -ne 0) { throw "Feature-masked JIT rejection was not proved for $tfm." }

        $summary.frameworks += [ordered]@{
            framework = $tfm
            nativeCases = $caseCount
            nativeReport = [IO.Path]::GetFileName($nativeReport)
            jitReport = [IO.Path]::GetFileName($jitReport)
            maskedJitReport = [IO.Path]::GetFileName($maskedReport)
            packageInventory = [IO.Path]::GetFileName($inventory)
            nativeBinary = $executable
            nativeSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
            managedControlSha256 = (Get-FileHash -LiteralPath $managed[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $summary.outcome = 'passed'
    Write-Output "Development embedding package evidence retained at $run"
    Write-Output 'No release, full conformance, UA migration, durability or unexecuted-platform qualification is claimed.'
}
catch {
    $summary.outcome = 'failed'
    $summary.failure = $_.Exception.Message
    throw
}
finally {
    try {
        if ($null -ne $workSelection) {
            Remove-EmbeddingWorkDirectory -Work $workSelection
            $summary.workDirectoryRemoved = $true
        }
        foreach ($name in @('cache', 'pack-cache', 'scratch')) {
            $resolvedCache = [IO.Path]::GetFullPath((Join-Path $run $name))
            if (![StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetDirectoryName($resolvedCache), [IO.Path]::GetFullPath($run))) {
                throw 'Refusing to clean a directory outside this owned run.'
            }
            if (Test-Path -LiteralPath $resolvedCache) {
                if ((Get-Item -LiteralPath $resolvedCache).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw 'Refusing to recursively clean a replaced cache link.'
                }
                Remove-Item -LiteralPath $resolvedCache -Recurse -Force
            }
        }
        $summary.cacheRemoved = $true
        $summary.temporaryDirectoriesRemoved = @('cache', 'pack-cache', 'scratch')
    }
    catch {
        $summary.outcome = 'failed'
        $summary.cleanupFailure = $_.Exception.Message
        throw
    }
    finally {
        try {
            $summary | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $run 'summary.json') -Encoding utf8
        }
        finally {
            foreach ($name in $oldEnvironment.Keys) {
                [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name])
            }
            $env:PATH = $oldPath
            Pop-Location
        }
    }
}
