<#
.SYNOPSIS
  Builds the Topo release zips for GitHub Releases and GameBanana.

.DESCRIPTION
  Builds two editions, each self-contained and framework-dependent, into dist/:
	Topo-<ver>-win-x64.zip                  standard edition: no AI code at all (the main download)
	Topo-<ver>-win-x64-fxdependent.zip      same, needs the .NET 8+ Desktop Runtime
	Topo-AI-<ver>-win-x64.zip               Topo + AI: MCP server and agent panel (optional download)
	Topo-AI-<ver>-win-x64-fxdependent.zip   same, needs the .NET 8+ Desktop Runtime
	SHA256SUMS.txt, release-notes.md, gamebanana.txt
  The version comes from <Version> in CELMapViewer.csproj unless -Version is given.

.EXAMPLE
  ./publish.ps1                 # build all four zips
  ./publish.ps1 -SkipAi         # standard edition only
  ./publish.ps1 -SelfTest       # also run the published exe's --selftest (needs a local Celeste + CEL)
#>
param(
	[string]$Version,
	[switch]$SelfTest,
	[switch]$SkipFxDependent,
	[switch]$SkipAi
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$proj = Join-Path $here 'CELMapViewer.csproj'
$dist = Join-Path $here 'dist'

if (-not $Version) {
	$Version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "No <Version> in $proj" }
$Version = $Version.TrimStart('v')

# Clear only what this script generates; hand-made files (cover art, GameBanana texts) stay.
New-Item -ItemType Directory $dist -Force | Out-Null
Get-ChildItem $dist -Filter 'Topo-*.zip' | Remove-Item -Force
foreach ($g in 'stage', 'SHA256SUMS.txt', 'release-notes.md', 'gamebanana.txt') {
	$p = Join-Path $dist $g
	if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}

# "What's new" for this version: the "## <Version>" section of CHANGELOG.md.
$changes = ''
$changelog = Join-Path $here 'CHANGELOG.md'
if (Test-Path $changelog) {
	$m = [regex]::Match((Get-Content $changelog -Raw -Encoding UTF8), "(?ms)^## $([regex]::Escape($Version))\s*\r?\n(.*?)(?=^## |\z)")
	if ($m.Success) { $changes = $m.Groups[1].Value.Trim() }
}

$editions = @(@{ Prefix = 'Topo'; Ai = 'false' })
if (-not $SkipAi) { $editions += @{ Prefix = 'Topo-AI'; Ai = 'true' } }
$flavors = @()
foreach ($e in $editions) {
	$flavors += @{ Name = "$($e.Prefix)-$Version-win-x64"; SelfContained = 'true'; Ai = $e.Ai }
	if (-not $SkipFxDependent) { $flavors += @{ Name = "$($e.Prefix)-$Version-win-x64-fxdependent"; SelfContained = 'false'; Ai = $e.Ai } }
}

foreach ($f in $flavors) {
	$stage = Join-Path $dist ('stage\' + $f.Name)
	Write-Host "==> $($f.Name)"
	# Both editions share obj/, so never reuse objects compiled for the other edition
	# (dotnet publish has no --no-incremental switch; clearing the Release objects does the same).
	$objRelease = Join-Path $here 'obj\Release'
	if (Test-Path $objRelease) { Remove-Item $objRelease -Recurse -Force }
	dotnet publish $proj -c Release -r win-x64 --self-contained $f.SelfContained `
		-p:PublishSingleFile=true -p:Version=$Version -p:TopoAi=$($f.Ai) -o $stage -nologo
	if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($f.Name)" }

	Get-ChildItem $stage -Filter *.pdb | Remove-Item
	Copy-Item (Join-Path $here 'README.md') (Join-Path $stage 'README.txt')
	Copy-Item (Join-Path $here 'Topo.png') $stage
	Copy-Item (Join-Path $here 'LICENSE') (Join-Path $stage 'LICENSE.txt')
	Copy-Item (Join-Path $here 'THIRD-PARTY-NOTICES.txt') $stage
	# everest.yaml with this release's version, so Everest/Olympus/mod databases recognise the zip.
	(Get-Content (Join-Path $here 'everest.yaml')) -replace '^(\s*Version:\s*).*$', "`${1}$Version" |
		Set-Content (Join-Path $stage 'everest.yaml') -Encoding ascii
	if (Test-Path $changelog) { Copy-Item $changelog (Join-Path $stage 'CHANGELOG.txt') }

	if ($SelfTest) {
		$exe = Join-Path $stage 'Topo.exe'
		$p = Start-Process $exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
		if ($p.ExitCode -ne 0) { throw "$exe --selftest failed ($($p.ExitCode))" }
	}

	Compress-Archive -Path (Join-Path $stage '*') -DestinationPath (Join-Path $dist "$($f.Name).zip") -CompressionLevel Optimal
}
Remove-Item (Join-Path $dist 'stage') -Recurse -Force

$sums = Get-ChildItem $dist -Filter 'Topo-*.zip' | ForEach-Object {
	'{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
$sums | Set-Content (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii

$notes = @"
## Topo $Version

Map viewer/editor for Celeste ``.bin`` maps, built for the CEL (Desolo Zantas) mod, with a live game link and a PCG room generator.

$(if ($changes) { "### What's new`n`n$changes`n" })
**Downloads**
- ``Topo-$Version-win-x64.zip``: the standard edition, with **no AI features or AI code**. Self-contained: just unzip and run ``Topo.exe``.
- ``Topo-$Version-win-x64-fxdependent.zip``: the same, smaller, needs the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
$(if (-not $SkipAi) { "- ``Topo-AI-$Version-win-x64.zip`` / ``…-fxdependent.zip``: optional **Topo + AI** edition with the MCP server and agent panel. Only download this if you want AI tools.`n" })
Needs Celeste with Everest; Topo finds it automatically (Steam/Olympus/common paths) or via *File > Set Celeste folder...*. See README.txt in the zip.

**SHA-256**
``````
$($sums -join "`n")
``````
"@
$notes | Set-Content (Join-Path $dist 'release-notes.md') -Encoding utf8

$gb = @"
Topo ${Version}: map editor

Topo (formerly MT_Zenith / CEL Map Viewer) is a standalone Windows map viewer/editor for Celeste .bin maps, made for the CEL mod.

Features
- Renders rooms with the game's own autotiler, decals and Loenn entity plugins (vanilla, CEL and installed helpers)
- Room, tile, entity, trigger and decal editing with undo, copy/paste between windows, room resizing tiers
- Live link to a running Celeste (Everest debug mode): teleport to the selected room, reload the map on save, read log.txt
- PCG room generator with movement-model checks of every generated room
- Asset credits for every map, matched through Maddie480's mod database

Editions
- Topo (main download): no AI features, and no AI code in the exe.
- Topo + AI (optional, separate zip): adds an MCP server and agent panel. Skip it if you don't want AI.

Install
1. Install Celeste + Everest (via Olympus). Install CEL for CEL maps.
2. Unzip Topo anywhere and run Topo.exe.
3. If your game isn't found automatically: File > Set Celeste folder..., then restart.
The plain zip is self-contained; the "fxdependent" zip needs the .NET 8 Desktop Runtime.
Optional (AI edition only): ffmpeg on PATH for video frames / audio decoding.

This is a tool, not a mod: unzip it outside Celeste/Mods. The everest.yaml in the zip only names and versions the download; Everest loads nothing from it.
"@
$gb | Set-Content (Join-Path $dist 'gamebanana.txt') -Encoding utf8

Write-Host ""
Get-ChildItem $dist | Format-Table Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } -AutoSize
