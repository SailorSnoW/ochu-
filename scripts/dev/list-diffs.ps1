<#
.SYNOPSIS
    Read-only: the entries of work/match/diffs.txt (match.ps1 -Dump) that have between
    -Min and -Max changed lines, smallest families first to look for a shared pattern.

.EXAMPLE
    .\scripts\dev\list-diffs.ps1 -Max 6 -Heads              # one line per method
    .\scripts\dev\list-diffs.ps1 -Max 12 -Category osu      # with their changed lines
    .\scripts\dev\list-diffs.ps1 -Max 40 -Like 'conv\.u4'   # those that mention an opcode
#>
[CmdletBinding()]
param(
    [int]$Min = 0,
    [int]$Max = 8,
    [switch]$Heads,
    [string]$Like = '',
    [string]$Category = ''
)

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path = Join-Path $Root 'work\match\diffs.txt'
if (-not (Test-Path -LiteralPath $path)) { throw 'No listing: run scripts\match.ps1 -Dump work\match\diffs.txt first.' }
$buffer = $null
$emit = {
    if (-not $buffer) { return }
    $n = $buffer.Count - 1
    if ($n -lt $Min -or $n -gt $Max) { return }
    if ($Category -ne '' -and ($buffer[0] -split "`t")[2] -ne $Category) { return }
    if ($Like -ne '' -and -not (($buffer -join "`n") -match $Like)) { return }
    if ($Heads) { ($buffer[0] -replace '\(.*$', '') + "`t" + $n } else { $buffer }
}
foreach ($line in Get-Content -LiteralPath $path) {
    if ($line.StartsWith('@@')) { & $emit; $buffer = New-Object System.Collections.Generic.List[string] }
    $buffer.Add($line)
}
& $emit
