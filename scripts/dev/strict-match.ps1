<#
.SYNOPSIS
    Read-only: measures what the equivalent readings of scripts/match.ps1 absorb. Runs the
    comparison with every reading off, then with each one off alone, and prints how many
    methods still match. See docs/matching.md.

.DESCRIPTION
    Reports go to work/match/strict-<name>.tsv: work/match/report.tsv is not touched.
    Each run is a child process (the readings are static switches of scripts/lib/IlMatch.cs).

.EXAMPLE
    .\scripts\dev\strict-match.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$match = Join-Path $Root 'scripts\match.ps1'
$all = 'slots', 'ldelem', 'ldobj', 'starg', 'branch-form', 'nops', 'pushes', 'const-slots', 'dead-stores'

function Measure-Run([string]$name, [string[]]$without) {
    $out = Join-Path $Root ("work\match\strict-{0}.tsv" -f $name)
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $match, '-Out', $out)
    if ($without.Count -gt 0) { $arguments += '-Without'; $arguments += ($without -join ',') }
    & powershell.exe @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "match.ps1 failed for $name" }
    $rows = Import-Csv -Delimiter "`t" -LiteralPath $out
    return [pscustomobject]@{ Name = $name; Total = @($rows).Count; Match = @($rows | Where-Object { $_.status -eq 'match' }).Count }
}

$base = Measure-Run 'none-off' @()
Write-Host ('{0,-22} {1,6} of {2}' -f 'every reading on', $base.Match, $base.Total)
foreach ($r in $all) {
    $m = Measure-Run $r @($r)
    Write-Host ('{0,-22} {1,6}   ({2} methods need it)' -f ("without $r"), $m.Match, ($base.Match - $m.Match))
}
$strict = Measure-Run 'all-off' $all
Write-Host ('{0,-22} {1,6}   ({2} methods need at least one reading)' -f 'every reading off', $strict.Match, ($base.Match - $strict.Match))
$noSlots = Measure-Run 'all-but-slots' @($all | Where-Object { $_ -ne 'slots' })
Write-Host ('{0,-22} {1,6}   (only the numbering of locals is read)' -f 'all off but slots', $noSlots.Match)
