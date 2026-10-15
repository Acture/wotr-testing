# Requires PowerShell 7 on Windows. Runs a WotR.Testing.Offline test project against the real WotR assemblies and
# blueprint pack without starting the game. Fails on zero tests, missing reports, timeouts and any result that did not
# pass, except paths explicitly reported as requiring the Unity runtime, which are listed but never counted as passed.
# -Runtime mono (default) runs the tests on the game's own Unity Mono runtime through WotR.Testing.MonoHost and the
# xUnit console runner (xUnit v2 test projects only); -Runtime netfx runs them on .NET Framework through dotnet test.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [ValidateSet('mono', 'netfx')][string]$Runtime = 'mono',
    # Used for the reported source commit; defaults to the repository containing the test project.
    [string]$RepositoryRoot,
    # Game root: a local installation or a vendor/wotr snapshot. Defaults to vendor/wotr, then GamePath.props.
    [string]$WotrInputRoot,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [int]$TimeoutMinutes = 15,
    # Optional dotnet test filter, e.g. "FullyQualifiedName~MainAttribute".
    [string]$Filter,
    [string]$ResultsDirectory,
    # A run in which fewer tests pass (for example because every test was skipped as unity-runtime-required) fails.
    [ValidateRange(1, [int]::MaxValue)][int]$MinimumPassed = 1,
    # Skip the NuGet lookup that warns when a mod uses an older BlueprintCore than the latest release.
    [switch]$NoUpdateCheck,
    # The 0Harmony.dll the run loads: umm (UnityModManager's copy, the default), game (the game's own Wrath_Data/Managed
    # copy), a file path, or a Lib.Harmony version restored from NuGet (2.2.2, or 2.2.2/net35 for one target framework).
    [string]$Harmony,
    # Harmony copies the mods are compared with, in the same forms (an array or comma-separated); members they lack are
    # warned. Default: game.
    [string[]]$HarmonyReference
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Project = (Resolve-Path -LiteralPath $Project).Path
if (-not $RepositoryRoot) { $RepositoryRoot = (& git -C (Split-Path -Parent $Project) rev-parse --show-toplevel 2>$null) ?? (Split-Path -Parent $Project) }
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $RepositoryRoot 'artifacts/test-results/wotr-offline' }
if (Test-Path -LiteralPath $ResultsDirectory) { Remove-Item -LiteralPath $ResultsDirectory -Recurse -Force }
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$ResultsDirectory = (Resolve-Path -LiteralPath $ResultsDirectory).Path

$trxName = 'wotr-offline.trx'
$trxPath = Join-Path $ResultsDirectory $trxName
$xunitPath = Join-Path $ResultsDirectory 'wotr-offline.xunit.xml'
$toolRoot = Split-Path -Parent $PSScriptRoot
$environmentPath = Join-Path $ResultsDirectory 'environment.json'
$properties = @("-p:Configuration=$Configuration")
if ($WotrInputRoot) { $properties += "-p:WotrInputRoot=$((Resolve-Path -LiteralPath $WotrInputRoot).Path)" }

$problems = [System.Collections.Generic.List[string]]::new()
$startedAt = Get-Date

Write-Host "Building $(Split-Path -Leaf $project) ($Configuration)."
& dotnet build $project @properties -nologo -v minimal
$built = $LASTEXITCODE -eq 0
if (-not $built) { $problems.Add('Build failed (missing WotR inputs are reported as [ENV_MISSING] in the build output).') }

function Get-ProjectProperty([string]$Name) {
    $value = & dotnet msbuild $project @properties "-getProperty:$Name" -nologo
    if ($LASTEXITCODE -ne 0) { throw "Cannot read $Name from $project." }
    return "$value".Trim()
}

function Test-Facade([string]$Path) {
    # A facade only forwards types: no type definitions besides <Module>, at least one exported type.
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $pe = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        if (-not $pe.HasMetadata) { return $false }
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        return $metadata.TypeDefinitions.Count -le 1 -and $metadata.ExportedTypes.Count -gt 0
    }
    catch [System.BadImageFormatException] { return $false }
    finally { $stream.Dispose() }
}

# Class library folder for the game's Mono: the game's own framework assemblies, plus the type-forwarding facades of
# the local .NET Framework 4.8 installation that Unity does not ship (test frameworks reference them). Game, Unity and
# mod assemblies are deliberately absent, so they resolve from the rewritten runtime folder.
function New-MonoClassLibrary([string]$Managed, [string]$WorkDirectory) {
    $framework = @(Get-ChildItem -LiteralPath $Managed -Filter *.dll | Where-Object { $_.Name -match '^(mscorlib|netstandard|System|Mono\.|Microsoft\.|I18N|Accessibility|Novell\.)' })
    $netfx = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
    $names = [System.Collections.Generic.HashSet[string]]::new([string[]]($framework.Name), [System.StringComparer]::OrdinalIgnoreCase)
    $facades = @(Get-ChildItem -LiteralPath $netfx -Filter *.dll | Where-Object { -not $names.Contains($_.Name) -and (Test-Facade $_.FullName) })
    $key = (($framework + $facades) | ForEach-Object { "$($_.Name):$($_.Length)" }) -join '|'
    $hash = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($key))).Substring(0, 16).ToLowerInvariant()
    $directory = Join-Path $WorkDirectory "mono-classlib/$hash"
    if (-not (Test-Path -LiteralPath (Join-Path $directory 'mscorlib.dll'))) {
        $staging = "$directory.tmp-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        foreach ($file in $framework + $facades) { Copy-Item -LiteralPath $file.FullName -Destination $staging }
        if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
        Move-Item -LiteralPath $staging -Destination $directory
    }
    return $directory
}

# umm, game and file paths are passed on; a version is restored as the Lib.Harmony NuGet package (like any package
# reference, so NuGet verifies its signature) and its 0Harmony.dll used. Lib.Harmony builds per target framework
# differ; without one, the first of net48, net472, net45, net452 and net35 the package has is used.
function Resolve-HarmonyCopy([string]$Value) {
    if ($Value -in 'umm', 'game') { return $Value }
    if ($Value -notmatch '^(\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.]+)?)(?:/([0-9A-Za-z.]+))?$') {
        if (-not (Test-Path -LiteralPath $Value -PathType Leaf)) { throw "[ENV_MISSING] Harmony copy not found: $Value. Use umm, game, a 0Harmony.dll path or a Lib.Harmony version." }
        return (Resolve-Path -LiteralPath $Value).Path
    }
    $version = $Matches[1]
    $framework = $Matches[2]
    $package = Join-Path (Get-ProjectProperty 'NuGetPackageRoot') "lib.harmony/$($version.ToLowerInvariant())"
    if (-not (Test-Path -LiteralPath (Join-Path $package 'lib'))) {
        $restore = Join-Path ([System.IO.Path]::GetTempPath()) "wotr-harmony-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Force -Path $restore | Out-Null
        try {
            "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup><ItemGroup><PackageDownload Include=`"Lib.Harmony`" Version=`"[$version]`" /></ItemGroup></Project>" |
                Set-Content -LiteralPath (Join-Path $restore 'restore.csproj') -Encoding utf8
            & dotnet restore (Join-Path $restore 'restore.csproj') -nologo -v quiet | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "[ENV_MISSING] Lib.Harmony $version could not be restored from NuGet." }
        }
        finally { Remove-Item -LiteralPath $restore -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $frameworks = if ($framework) { @($framework) } else { @('net48', 'net472', 'net45', 'net452', 'net35') }
    foreach ($candidate in $frameworks) {
        $path = Join-Path $package "lib/$candidate/0Harmony.dll"
        if (Test-Path -LiteralPath $path) { return $path }
    }
    throw "[ENV_MISSING] Lib.Harmony $version has no 0Harmony.dll for $($frameworks -join ', ')."
}

# Passed to the test process as WOTR_HARMONY and WOTR_HARMONY_REFERENCES; restored after the run.
$ready = $built
$savedHarmony = @{ WOTR_HARMONY = $env:WOTR_HARMONY; WOTR_HARMONY_REFERENCES = $env:WOTR_HARMONY_REFERENCES }
if ($built) {
    try {
        if ($Harmony) { $env:WOTR_HARMONY = Resolve-HarmonyCopy $Harmony }
        # Comma-separated values too: pwsh -File passes an array argument as one string.
        if ($HarmonyReference) { $env:WOTR_HARMONY_REFERENCES = @($HarmonyReference -split ',' | Where-Object { $_.Trim() } | ForEach-Object { Resolve-HarmonyCopy $_.Trim() }) -join '|' }
    }
    catch {
        $problems.Add($_.Exception.Message)
        $ready = $false
    }
}

# Arguments are passed one by one (ProcessStartInfo.ArgumentList), so paths with spaces are quoted correctly.
$testExitCode = $null
function Invoke-Process([string]$FilePath, [string[]]$Arguments) {
    $info = [System.Diagnostics.ProcessStartInfo]::new($FilePath)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($info)
    if (-not $process.WaitForExit(($TimeoutMinutes + 1) * 60 * 1000)) {
        $process.Kill($true)
        $problems.Add("Timeout: the test run exceeded $TimeoutMinutes minutes.")
        return
    }
    $script:testExitCode = $process.ExitCode
}

if ($ready -and $Runtime -eq 'mono') {
    $env:WOTR_OFFLINE_REPORT_DIR = $ResultsDirectory
    try {
        $targetPath = Get-ProjectProperty 'TargetPath'
        $inputs = Get-Content -LiteralPath (Join-Path (Split-Path -Parent $targetPath) 'wotr-offline-inputs.json') -Raw | ConvertFrom-Json
        $inputRoot = $env:WOTR_INPUT_ROOT ? $env:WOTR_INPUT_ROOT : $inputs.inputRoot
        $monoRuntime = Join-Path $inputRoot 'MonoBleedingEdge/EmbedRuntime'
        $console = Join-Path (Get-ProjectProperty 'NuGetPackageRoot') "xunit.runner.console/$(Get-ProjectProperty 'WotrXunitConsoleVersion')/tools/net48/xunit.console.exe"
        if (-not (Test-Path -LiteralPath (Join-Path $monoRuntime 'mono-2.0-bdwgc.dll'))) { $problems.Add("[ENV_MISSING] The game's Mono runtime was not found in $monoRuntime.") }
        elseif (-not (Test-Path -LiteralPath $console)) { $problems.Add("[ENV_MISSING] xUnit console runner not found: $console. Restore the test project.") }
        else {
            # The NuGet package ships the host next to this script; the source repository builds it.
            $hostPath = Join-Path $PSScriptRoot 'wotr-mono-host.exe'
            if (-not (Test-Path -LiteralPath $hostPath)) {
                $hostProject = Join-Path $toolRoot 'src/WotR.Testing.MonoHost/WotR.Testing.MonoHost.csproj'
                & dotnet build $hostProject -c Release -nologo -v quiet | Out-Host
                $hostBuilt = $LASTEXITCODE -eq 0
                $hostPath = (& dotnet msbuild $hostProject -p:Configuration=Release -getProperty:TargetPath -nologo).Trim()
                if (-not $hostBuilt -or $LASTEXITCODE -ne 0) { $hostPath = '' }
            }
            if (-not $hostPath -or -not (Test-Path -LiteralPath $hostPath)) { $problems.Add('Build failed: WotR.Testing.MonoHost.') }
            else {
                $classLibrary = New-MonoClassLibrary (Join-Path $inputRoot 'Wrath_Data/Managed') $inputs.workDirectory
                $arguments = @('--runtime', $monoRuntime, '--config', (Join-Path $inputRoot 'MonoBleedingEdge/etc'), '--assemblies', $classLibrary,
                    '--', $console, $targetPath, '-noshadow', '-nologo', '-xml', $xunitPath)
                if ($Filter) {
                    if ($Filter -match '^(FullyQualifiedName|Name)~(.+)$') { $arguments += @('-method', "*$($Matches[2])*") }
                    else { $problems.Add("Filter '$Filter' is not supported with -Runtime mono; use FullyQualifiedName~text.") }
                }
                Write-Host "Running on the game's Unity Mono runtime ($monoRuntime)."
                Invoke-Process $hostPath $arguments
            }
        }
    }
    finally {
        Remove-Item Env:WOTR_OFFLINE_REPORT_DIR -ErrorAction SilentlyContinue
    }
}

if ($ready -and $Runtime -eq 'netfx') {
    $env:WOTR_OFFLINE_REPORT_DIR = $ResultsDirectory
    try {
        $arguments = @('test', $project, '--no-build', '--configuration', $Configuration,
            '--logger', "trx;LogFileName=$trxName", '--results-directory', $ResultsDirectory,
            '--blame-hang', '--blame-hang-timeout', "$($TimeoutMinutes)m")
        if ($Filter) { $arguments += @('--filter', $Filter) }
        Invoke-Process 'dotnet' $arguments
    }
    finally {
        Remove-Item Env:WOTR_OFFLINE_REPORT_DIR -ErrorAction SilentlyContinue
    }
}

foreach ($entry in $savedHarmony.GetEnumerator()) {
    if ($null -eq $entry.Value) { Remove-Item -Path "Env:$($entry.Key)" -ErrorAction SilentlyContinue }
    else { Set-Item -Path "Env:$($entry.Key)" -Value $entry.Value }
}

function Get-Classification([string]$Outcome, [string]$Message) {
    if ($Outcome -eq 'Passed') { return 'passed' }
    if ($Message -match '\[ENV_MISSING\]') { return 'environment-missing' }
    if ($Message -match '\[INIT_FAILED\]') { return 'initialization-failed' }
    if ($Message -match '\[UNITY_RUNTIME_REQUIRED\]') { return 'unity-runtime-required' }
    if ($Outcome -eq 'NotExecuted') { return 'skipped-without-reason' }
    if ($Message -match '^(Assert\.|\w+: expected|Precondition:)' -or $Message -match 'Xunit\.Sdk\.') { return 'assertion-failed' }
    return 'error'
}

$results = [System.Collections.Generic.List[object]]::new()
if ($Runtime -eq 'mono') {
    if (Test-Path -LiteralPath $xunitPath) {
        [xml]$xunit = Get-Content -LiteralPath $xunitPath -Raw
        foreach ($test in $xunit.SelectNodes('//test')) {
            $outcome = switch ($test.result) { 'Pass' { 'Passed' } 'Fail' { 'Failed' } default { 'NotExecuted' } }
            $message = @($test.SelectSingleNode('failure/message')?.InnerText, $test.SelectSingleNode('reason')?.InnerText,
                $test.SelectSingleNode('output')?.InnerText) -join "`n"
            $results.Add([pscustomobject]@{
                name = $test.name
                outcome = $outcome
                classification = Get-Classification $outcome $message.Trim()
                duration = $test.time
                message = $message.Trim()
            })
        }
        foreach ($failure in $xunit.SelectNodes('//errors/error')) { $problems.Add("xUnit $($failure.type) error: $($failure.SelectSingleNode('failure/message')?.InnerText)") }
    }
    elseif ($ready -and $problems.Count -eq 0) {
        $problems.Add('Missing report: the xUnit XML report was not written.')
    }
}
elseif (Test-Path -LiteralPath $trxPath) {
    [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
    $ns = [System.Xml.XmlNamespaceManager]::new($trx.NameTable)
    $ns.AddNamespace('t', 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    foreach ($result in $trx.SelectNodes('//t:UnitTestResult', $ns)) {
        $messageNode = $result.SelectSingleNode('t:Output/t:ErrorInfo/t:Message', $ns)
        $stdoutNode = $result.SelectSingleNode('t:Output/t:StdOut', $ns)
        $message = @(${messageNode}?.InnerText, ${stdoutNode}?.InnerText) -join "`n"
        $results.Add([pscustomobject]@{
            name = $result.testName
            outcome = $result.outcome
            classification = Get-Classification $result.outcome $message.Trim()
            duration = $result.duration
            message = $message.Trim()
        })
    }
}
elseif ($ready) {
    $problems.Add('Missing report: the TRX test report was not written.')
}

if ($ready -and $results.Count -eq 0) { $problems.Add('Zero tests: no test results were reported.') }
if ($ready -and -not (Test-Path -LiteralPath $environmentPath)) { $problems.Add('Missing report: environment.json was not written by the test fixture.') }

# Warnings never fail the run: they point at differences a player's game may have.
$warnings = [System.Collections.Generic.List[string]]::new()
$harmonyUsed = $null
if (Test-Path -LiteralPath $environmentPath) {
    $environment = Get-Content -LiteralPath $environmentPath -Raw | ConvertFrom-Json
    $harmonyUsed = $environment.harmony?.used
    foreach ($warning in @($environment.warnings)) { if ($warning) { $warnings.Add($warning) } }
    $blueprintCore = @($environment.libraries | Where-Object { $_.name -eq 'BlueprintCore.dll' -and $_.version }) | Select-Object -First 1
    if ($blueprintCore -and -not $NoUpdateCheck -and -not $env:WOTR_NO_UPDATE_CHECK) {
        try {
            $published = (Invoke-RestMethod -Uri 'https://api.nuget.org/v3-flatcontainer/ww-blueprint-core/index.json' -TimeoutSec 15).versions |
                Where-Object { $_ -notmatch '-' } | ForEach-Object { [version]$_ }
            $latest = $published | Sort-Object | Select-Object -Last 1
            $used = [version]$blueprintCore.version
            if ($latest -and $used -lt $latest) { $warnings.Add("BlueprintCore $used is used; $latest is the latest release (NuGet WW-Blueprint-Core).") }
        }
        catch { $warnings.Add("BlueprintCore update check skipped: $($_.Exception.Message)") }
    }
}

$counts = [ordered]@{}
foreach ($group in ($results | Group-Object classification)) { $counts[$group.Name] = $group.Count }
$blocking = @($results | Where-Object { $_.classification -notin 'passed', 'unity-runtime-required' })
# A non-zero exit without a failing result (for example a crash after the last result) still fails the run.
if ($null -ne $testExitCode -and $testExitCode -ne 0 -and $blocking.Count -eq 0) {
    $problems.Add("The test process exited with code $testExitCode although no failing result was reported.")
}
$passedCount = @($results | Where-Object { $_.classification -eq 'passed' }).Count
if ($ready -and $results.Count -gt 0 -and $passedCount -lt $MinimumPassed) {
    $problems.Add("Too few passed tests: $passedCount passed, at least $MinimumPassed required (skipped tests do not count).")
}
$passed = $problems.Count -eq 0 -and $blocking.Count -eq 0

$commit = (& git -C $RepositoryRoot rev-parse HEAD 2>$null)
$dirty = [bool](& git -C $RepositoryRoot status --porcelain --untracked-files=no 2>$null)
$summary = [ordered]@{
    passed = $passed
    startedAt = $startedAt.ToUniversalTime().ToString('o')
    finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    configuration = $Configuration
    runtime = $Runtime
    harmony = $harmonyUsed
    testExitCode = $testExitCode
    sourceCommit = $commit
    sourceHasUncommittedChanges = $dirty
    counts = $counts
    problems = $problems
    warnings = $warnings
    reports = [ordered]@{ tests = $(if ($Runtime -eq 'mono') { $xunitPath } else { $trxPath }); environment = $environmentPath }
    results = $results
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# WotR offline test summary')
$lines.Add('')
$harmonyText = if ($harmonyUsed) { " · Harmony $($harmonyUsed.version) ($($harmonyUsed.source))" } else { '' }
$lines.Add("Result: **$(if ($passed) { 'PASS' } else { 'FAIL' })** · runtime $Runtime$harmonyText · commit $commit$(if ($dirty) { ' (uncommitted changes)' })")
$lines.Add('')
$lines.Add('| Classification | Tests |')
$lines.Add('| --- | ---: |')
foreach ($entry in $counts.GetEnumerator()) { $lines.Add("| $($entry.Key) | $($entry.Value) |") }
foreach ($problem in $problems) { $lines.Add(''); $lines.Add("- $problem") }
if ($warnings.Count -gt 0) {
    $lines.Add('')
    $lines.Add('Warnings (do not fail the run):')
    $lines.Add('')
    foreach ($warning in $warnings) { $lines.Add("- $warning") }
}
$lines.Add('')
$lines.Add('| Test | Classification |')
$lines.Add('| --- | --- |')
foreach ($result in $results) { $lines.Add("| $($result.name) | $($result.classification) |") }
$lines | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.md') -Encoding utf8

Write-Host ''
foreach ($entry in $counts.GetEnumerator()) { Write-Host ("{0,-26} {1}" -f $entry.Key, $entry.Value) }
foreach ($warning in $warnings) { Write-Host "WARN: $warning" }
foreach ($problem in $problems) { Write-Host "FAIL: $problem" }
foreach ($result in $blocking) { Write-Host "FAIL: [$($result.classification)] $($result.name)" }
Write-Host "Reports: $ResultsDirectory"
if (-not $passed) { exit 1 }
Write-Host "PASS: WotR offline tests ($Runtime$harmonyText)"
