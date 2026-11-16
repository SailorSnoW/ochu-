<#
.SYNOPSIS
    One iteration of the matching loop: pipeline, build, match, then the methods gained
    and lost against a saved report. See docs/matching.md.

.DESCRIPTION
    Compares work/match/report.tsv with work/match/report.base.tsv by reference token.
    A rule that loses a method is wrong somewhere: read the losses before keeping it.
    -Accept saves the new report as the base when nothing was lost.

.EXAMPLE
    .\scripts\dev\cycle.ps1                 # after a change in scripts/decompiler
    .\scripts\dev\cycle.ps1 -From names     # after a change in apply-names.ps1 or names.tsv
    .\scripts\dev\cycle.ps1 -Accept
#>
[CmdletBinding()]
param(
    [ValidateSet('copy', 'strings', 'clean', 'rename', 'names', 'decomp', 'src')][string]$From = 'decomp',
    [switch]$Accept
)

$ErrorActionPreference = 'Continue'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$scripts = Join-Path $Root 'scripts'
$report = Join-Path $Root 'work\match\report.tsv'
$base = Join-Path $Root 'work\match\report.base.tsv'

$p = & (Join-Path $scripts 'pipeline.ps1') -From $From *>&1 | ForEach-Object { "$_" }
if (-not ($p -match '^Done')) { $p | Select-Object -Last 25; Write-Host 'PIPELINE FAILED'; return }
$p | Where-Object { $_ -match 'setters|sites|restored|fixups' }
$b = & (Join-Path $scripts 'build.ps1') *>&1 | ForEach-Object { "$_" }
if (-not ($b -match 'Build succeeded')) { $b | Where-Object { $_ -match 'error' } | Select-Object -First 30; Write-Host 'BUILD FAILED'; return }
$m = & (Join-Path $scripts 'match.ps1') -Dump 'work\match\diffs.txt' *>&1 | ForEach-Object { "$_" }
$m | Where-Object { $_ -match '^(osu|global|lib|obf|ALL) ' }
if (-not (Test-Path -LiteralPath $base)) { Copy-Item -LiteralPath $report -Destination $base; Write-Host 'no base yet: saved'; return }

$old = @{}
foreach ($r in Import-Csv -Delimiter "`t" -LiteralPath $base) { $old[$r.token] = $r.status }
$gained = 0
$lost = @()
foreach ($r in Import-Csv -Delimiter "`t" -LiteralPath $report) {
    $was = $old[$r.token]
    if ($r.status -eq 'match' -and $was -ne 'match') { $gained++ }
    if ($r.status -ne 'match' -and $was -eq 'match') { $lost += $r }
}
Write-Host ("gained {0}, lost {1}" -f $gained, $lost.Count)
foreach ($r in $lost) { Write-Host ("LOST {0} {1} {2} {3}" -f $r.token, $r.status, $r.cause, $r.name) }
if ($Accept -and $lost.Count -eq 0) { Copy-Item -LiteralPath $report -Destination $base -Force; Write-Host 'base updated' }
