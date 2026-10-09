# Requires PowerShell 7 on Windows. Runs a WotR.Testing.Offline test project against the real WotR assemblies and
# blueprint pack without starting the game. Fails on zero tests, missing reports, timeouts and any result that did not
# pass, except paths explicitly reported as requiring the Unity runtime, which are listed but never counted as passed.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Project,
    # Used for the reported source commit; defaults to the repository containing the test project.
    [string]$RepositoryRoot,
    # Game root: a local installation or a vendor/wotr snapshot. Defaults to vendor/wotr, then GamePath.props.
    [string]$WotrInputRoot,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [int]$TimeoutMinutes = 15,
    # Optional dotnet test filter, e.g. "FullyQualifiedName~MainAttribute".
    [string]$Filter,
    [string]$ResultsDirectory
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
$environmentPath = Join-Path $ResultsDirectory 'environment.json'
$properties = @("-p:Configuration=$Configuration")
if ($WotrInputRoot) { $properties += "-p:WotrInputRoot=$((Resolve-Path -LiteralPath $WotrInputRoot).Path)" }

$problems = [System.Collections.Generic.List[string]]::new()
$startedAt = Get-Date

Write-Host "Building $(Split-Path -Leaf $project) ($Configuration)."
& dotnet build $project @properties -nologo -v minimal
$built = $LASTEXITCODE -eq 0
if (-not $built) { $problems.Add('Build failed (missing WotR inputs are reported as [ENV_MISSING] in the build output).') }

if ($built) {
    $env:WOTR_OFFLINE_REPORT_DIR = $ResultsDirectory
    try {
        $arguments = @('test', $project, '--no-build', '--configuration', $Configuration,
            '--logger', "trx;LogFileName=$trxName", '--results-directory', $ResultsDirectory,
            '--blame-hang', '--blame-hang-timeout', "$($TimeoutMinutes)m")
        if ($Filter) { $arguments += @('--filter', $Filter) }
        $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -NoNewWindow -PassThru
        if (-not $process.WaitForExit(($TimeoutMinutes + 1) * 60 * 1000)) {
            $process | Stop-Process -Force
            $problems.Add("Timeout: the test run exceeded $TimeoutMinutes minutes.")
        }
    }
    finally {
        Remove-Item Env:WOTR_OFFLINE_REPORT_DIR -ErrorAction SilentlyContinue
    }
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
if (Test-Path -LiteralPath $trxPath) {
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
elseif ($built) {
    $problems.Add('Missing report: the TRX test report was not written.')
}

if ($built -and $results.Count -eq 0) { $problems.Add('Zero tests: no test results were reported.') }
if ($built -and -not (Test-Path -LiteralPath $environmentPath)) { $problems.Add('Missing report: environment.json was not written by the test fixture.') }

$counts = [ordered]@{}
foreach ($group in ($results | Group-Object classification)) { $counts[$group.Name] = $group.Count }
$blocking = @($results | Where-Object { $_.classification -notin 'passed', 'unity-runtime-required' })
$passed = $problems.Count -eq 0 -and $blocking.Count -eq 0

$commit = (& git -C $RepositoryRoot rev-parse HEAD 2>$null)
$dirty = [bool](& git -C $RepositoryRoot status --porcelain --untracked-files=no 2>$null)
$summary = [ordered]@{
    passed = $passed
    startedAt = $startedAt.ToUniversalTime().ToString('o')
    finishedAt = (Get-Date).ToUniversalTime().ToString('o')
    configuration = $Configuration
    sourceCommit = $commit
    sourceHasUncommittedChanges = $dirty
    counts = $counts
    problems = $problems
    reports = [ordered]@{ trx = $trxPath; environment = $environmentPath }
    results = $results
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.json') -Encoding utf8

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# WotR offline test summary')
$lines.Add('')
$lines.Add("Result: **$(if ($passed) { 'PASS' } else { 'FAIL' })** · commit $commit$(if ($dirty) { ' (uncommitted changes)' })")
$lines.Add('')
$lines.Add('| Classification | Tests |')
$lines.Add('| --- | ---: |')
foreach ($entry in $counts.GetEnumerator()) { $lines.Add("| $($entry.Key) | $($entry.Value) |") }
foreach ($problem in $problems) { $lines.Add(''); $lines.Add("- $problem") }
$lines.Add('')
$lines.Add('| Test | Classification |')
$lines.Add('| --- | --- |')
foreach ($result in $results) { $lines.Add("| $($result.name) | $($result.classification) |") }
$lines | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'summary.md') -Encoding utf8

Write-Host ''
foreach ($entry in $counts.GetEnumerator()) { Write-Host ("{0,-26} {1}" -f $entry.Key, $entry.Value) }
foreach ($problem in $problems) { Write-Host "FAIL: $problem" }
foreach ($result in $blocking) { Write-Host "FAIL: [$($result.classification)] $($result.name)" }
Write-Host "Reports: $ResultsDirectory"
if (-not $passed) { exit 1 }
Write-Host 'PASS: WotR offline tests'
