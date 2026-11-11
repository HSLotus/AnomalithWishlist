[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GamePath,
    [string]$BepInExCoreDir,
    [string]$ArtifactsPath,
    [switch]$Package
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Local compile/package only. Never launch the game, install a plugin, or run tests.
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game = (Resolve-Path -LiteralPath $GamePath).Path
if (-not (Test-Path -LiteralPath (Join-Path $game 'Anomalith_Data\Managed\Assembly-CSharp.dll') -PathType Leaf)) {
    throw 'GamePath must be a lawful ANOMALITH game installation containing Anomalith_Data/Managed.'
}
if ([string]::IsNullOrWhiteSpace($BepInExCoreDir)) { $BepInExCoreDir = Join-Path $game 'BepInEx\core' }
$core = (Resolve-Path -LiteralPath $BepInExCoreDir).Path
foreach ($name in @('BepInEx.dll', '0Harmony.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $core $name) -PathType Leaf)) { throw "Missing local build reference: $name" }
}
$null = Get-Command dotnet -ErrorAction Stop
$artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) { Join-Path $root 'artifacts' } else { [IO.Path]::GetFullPath($ArtifactsPath) }
$output = Join-Path $artifacts 'plugin'
$intermediate = (Join-Path $artifacts 'obj') + '/'
$project = Join-Path $root 'src\AnomalithWishlist.csproj'
& dotnet build $project -c Release --nologo "-p:GameDir=$game" "-p:BepInExCoreDir=$core" "-p:BaseIntermediateOutputPath=$intermediate" -o $output
if ($LASTEXITCODE -ne 0) { throw "Compilation failed with exit code $LASTEXITCODE." }
$dll = Join-Path $output 'AnomalithWishlist.dll'
if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'Compiler did not produce the plugin DLL.' }

if (-not $Package) {
    [pscustomobject]@{ Plugin = $dll; SHA256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() }
    return
}

# Explicit release allowlist: plugin + documentation ONLY. No loader, game
# assemblies, PDBs, user config, saves, or source analysis are redistributed.
$version = ([Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion -split '\+')[0]
if ($version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([-.][A-Za-z0-9.-]+)?$') { throw 'Unexpected plugin version.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stage = Join-Path $artifacts "package-$version-$stamp"
$pluginFolder = Join-Path $stage 'BepInEx\plugins\AnomalithWishlist'
$null = New-Item -ItemType Directory -Path $pluginFolder -Force
Copy-Item -LiteralPath $dll -Destination (Join-Path $pluginFolder 'AnomalithWishlist.dll')
foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $stage $name)
}
$releases = Join-Path $artifacts 'releases'
$null = New-Item -ItemType Directory -Path $releases -Force
$zip = Join-Path $releases "AnomalithWishlist-$version-$stamp.zip"
$payload = @('BepInEx', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') | ForEach-Object { Join-Path $stage $_ }
Compress-Archive -LiteralPath $payload -DestinationPath $zip -CompressionLevel Optimal
[pscustomobject]@{
    Plugin = $dll
    ReleaseZip = $zip
    SHA256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
}
