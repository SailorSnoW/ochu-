<#
.SYNOPSIS
    Read-only: the IL diff of one method (match.ps1 -Show) cut down to the hunks that change
    something else than a branch distance, a handler bound or a local type: what to read
    in a large method, where one inserted instruction moves every jump.

.EXAMPLE
    .\scripts\dev\show-hunks.ps1 0x06002EB6 -Context 8
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][uint32]$Token,
    [int]$Context = 6,
    # Also pass over lines that only load or store a local (renumbered slots).
    [switch]$IgnoreLocals
)

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$writer = New-Object IO.StringWriter
$console = [Console]::Out
[Console]::SetOut($writer)
try { $text = & (Join-Path $Root 'scripts\match.ps1') -Show $Token | Out-String }
finally { [Console]::SetOut($console) }
$lines = ($writer.ToString() + $text) -split "`r?`n"
$keep = New-Object bool[] $lines.Count
for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    if ($l -notmatch '^[-+] ') { continue }
    if ($l -match '^[-+] (local|eh) ') { continue }
    if ($IgnoreLocals -and $l -match '^[-+] (ldloc|stloc|ldloca)(\.s)? ?V?_?\d*$') { continue }
    if ($l -match '^[-+] (br|leave|switch|beq|bne|bge|bgt|ble|blt)\S* [@(]') { continue }
    $from = [Math]::Max(0, $i - $Context); $to = [Math]::Min($lines.Count - 1, $i + $Context)
    for ($j = $from; $j -le $to; $j++) { $keep[$j] = $true }
}
$last = -2
for ($i = 0; $i -lt $lines.Count; $i++) {
    if (-not $keep[$i]) { continue }
    if ($i -ne $last + 1) { '  ... (line {0})' -f $i }
    $lines[$i]
    $last = $i
}
