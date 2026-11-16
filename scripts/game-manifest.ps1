<#
.SYNOPSIS
    Generates or verifies game.sha256, the fingerprint of the pristine game/ copy.

.DESCRIPTION
    game/ is never committed. game.sha256 (committed) pins the exact build this
    repository documents, in sha256sum format ("<hash> *<file name>"). It lists the
    binaries of the build (the .exe and .dll files at the root of game/) and nothing of
    what a launch writes next to them (configuration, databases, logs, beatmaps).

    -Verify checks every binary of the manifest.
    -Verify -Core checks the six assemblies the pipeline reads and nothing else: enough
    for a game/ whose third-party libraries come from another install of the same build.

.EXAMPLE
    .\scripts\game-manifest.ps1                 # (re)generate game.sha256
    .\scripts\game-manifest.ps1 -Verify         # check the binaries of game/ against game.sha256
    .\scripts\game-manifest.ps1 -Verify -Core   # check the pipeline inputs only
#>
[CmdletBinding()]
param(
    [switch]$Verify,
    [switch]$Core
)

$ErrorActionPreference = 'Stop'
$Root     = Split-Path -Parent $PSScriptRoot
$Game     = Join-Path $Root 'game'
$Manifest = Join-Path $Root 'game.sha256'
# The inputs of scripts/pipeline.ps1, step 1.
$CoreFiles = 'osu!.exe', 'osu!gameplay.dll', 'osu!ui.dll', 'osu!seasonal.dll', 'OpenTK.dll', 'Microsoft.Ink.dll'

if (-not (Test-Path $Game)) { throw "game/ not found at $Game" }
if ($Core -and -not $Verify) { throw '-Core only applies to -Verify: the manifest always lists every binary.' }

function Get-Current {
    $prefix = $Game.TrimEnd('\') + '\'
    $map = [ordered]@{}
    if ($Core) {
        $files = foreach ($n in $CoreFiles) {
            $p = Join-Path $Game $n
            if (Test-Path -LiteralPath $p) { Get-Item -LiteralPath $p -Force }
        }
    } else {
        # The binaries of the build. -Force: the game's DLLs carry the hidden attribute.
        $files = Get-ChildItem -LiteralPath $Game -File -Force |
            Where-Object { $_.Extension -eq '.exe' -or $_.Extension -eq '.dll' } |
            Sort-Object { $_.FullName.Substring($prefix.Length).Replace('\', '/') } -Culture ''
    }
    foreach ($f in $files) {
        $rel = $f.FullName.Substring($prefix.Length).Replace('\', '/')
        $map[$rel] = (Get-FileHash -Algorithm SHA256 -LiteralPath $f.FullName).Hash.ToLowerInvariant()
    }
    return $map
}

$current = Get-Current

if (-not $Verify) {
    $lines = foreach ($k in $current.Keys) { "{0} *{1}" -f $current[$k], $k }
    # UTF-8 without BOM, LF line endings
    [IO.File]::WriteAllText($Manifest, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
    Write-Host ("Wrote {0} entries to game.sha256" -f $current.Count)
    return
}

if (-not (Test-Path $Manifest)) { throw 'game.sha256 not found; run without -Verify first.' }

$expected = @{}
foreach ($line in [IO.File]::ReadAllLines($Manifest, [Text.Encoding]::UTF8)) {
    if ($line -match '^([0-9a-f]{64}) \*(.+)$') {
        if ($Core -and $CoreFiles -notcontains $Matches[2]) { continue }
        $expected[$Matches[2]] = $Matches[1]
    }
}

$bad = 0
foreach ($k in $expected.Keys) {
    if (-not $current.Contains($k))        { Write-Host "MISSING  $k"; $bad++ }
    elseif ($current[$k] -ne $expected[$k]) { Write-Host "MODIFIED $k"; $bad++ }
}
foreach ($k in $current.Keys) {
    if (-not $expected.ContainsKey($k)) { Write-Host "EXTRA    $k"; $bad++ }
}

if ($Core) {
    if ($bad) { Write-Host "$bad difference(s): game/ does not hold the reference build."; exit 1 }
    Write-Host ("OK: the {0} pipeline inputs match game.sha256" -f $expected.Count)
    return
}
if ($bad) { Write-Host "$bad difference(s): the binaries of game/ are NOT those of the reference build."; exit 1 }
Write-Host ("OK: {0} binaries match game.sha256" -f $expected.Count)
