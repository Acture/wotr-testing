# Requires PowerShell 7. Copies the WotR files WotR.Testing.Offline reads into a snapshot directory
# (default ./vendor/wotr; keep it ignored by Git) with a manifest of the game version and SHA-256 hashes. The files are licensed game content:
# keep the snapshot local or on a controlled CI runner, never commit or publish it.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$WrathInstallDir,
    [string]$Destination = (Join-Path (Get-Location) 'vendor/wotr'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = (Resolve-Path -LiteralPath $WrathInstallDir).Path
$versionFile = Join-Path $source 'Wrath_Data/StreamingAssets/Version.info'
if (-not (Test-Path -LiteralPath $versionFile)) { throw "Not a WotR installation (Version.info missing): $source" }

# Never write into, or delete, the installation: both paths are made absolute (following links where they exist) and
# any overlap is refused before anything is removed.
function Get-FullDirectory([string]$Path) {
    $full = [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path, (Get-Location).ProviderPath))
    # Resolve every existing component, so a junction or symbolic link anywhere on the path is followed; components
    # that do not exist yet are appended unchanged.
    $current = [System.IO.Path]::GetPathRoot($full)
    foreach ($part in $full.Substring($current.Length).Split([System.IO.Path]::DirectorySeparatorChar, [System.StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item -and $item.LinkTarget) { $current = $item.ResolveLinkTarget($true).FullName }
    }
    return [System.IO.Path]::TrimEndingDirectorySeparator($current) + [System.IO.Path]::DirectorySeparatorChar
}
$sourceFull = Get-FullDirectory $source
$destinationFull = Get-FullDirectory $Destination
if ($sourceFull.StartsWith($destinationFull, [System.StringComparison]::OrdinalIgnoreCase) -or
    $destinationFull.StartsWith($sourceFull, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Destination $destinationFull overlaps the installation $sourceFull. Choose a separate directory."
}
$Destination = $destinationFull
if ((Test-Path -LiteralPath $Destination) -and (Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1)) {
    if (-not $Force) { throw "Destination is not empty: $Destination. Use -Force to replace it; versions are never mixed." }
    Remove-Item -LiteralPath $Destination -Recurse -Force
}

$files = [System.Collections.Generic.List[string]]::new()
Get-ChildItem -LiteralPath (Join-Path $source 'Wrath_Data/Managed') -Recurse -File -Filter '*.dll' |
    ForEach-Object { $files.Add([System.IO.Path]::GetRelativePath($source, $_.FullName).Replace('\', '/')) }
# The game's Unity Mono runtime and its configuration, for Invoke-WotrOfflineTests.ps1 -Runtime mono.
foreach ($directory in 'MonoBleedingEdge/EmbedRuntime', 'MonoBleedingEdge/etc') {
    if (-not (Test-Path -LiteralPath (Join-Path $source $directory))) { throw "Required directory missing from installation: $directory" }
    Get-ChildItem -LiteralPath (Join-Path $source $directory) -Recurse -File |
        ForEach-Object { $files.Add([System.IO.Path]::GetRelativePath($source, $_.FullName).Replace('\', '/')) }
}
foreach ($relative in @(
        'Bundles/blueprints-pack.bbp',
        'Bundles/blueprint.assets',
        'Wrath_Data/StreamingAssets/Version.info',
        'Wrath_Data/StreamingAssets/Localization/enGB.json',
        'Wrath_Data/StreamingAssets/Localization/Sound.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $relative))) { throw "Required file missing from installation: $relative" }
    $files.Add($relative)
}

$hashes = [ordered]@{}
foreach ($relative in ($files | Sort-Object)) {
    $target = Join-Path $Destination $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $relative) -Destination $target
    $hashes[$relative] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
}

$manifest = [ordered]@{
    description = 'Local WotR inputs for WotR.Testing.Offline. Licensed game files: do not commit or redistribute.'
    gameVersion = (Get-Content -LiteralPath $versionFile -Raw).Trim()
    sourceRoot = $source
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
    files = $hashes
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Destination 'manifest.json') -Encoding utf8
Write-Host "Snapshot of $($manifest.gameVersion): $($hashes.Count) files in $Destination"
