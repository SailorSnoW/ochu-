<#
.SYNOPSIS
    Read-only: the IL diff of methods (match.ps1 -Show), cut down to the changed lines and
    a few lines around them.

.EXAMPLE
    .\scripts\dev\show-diff.ps1 0x06000803, 0x06001B34 -Context 5
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][uint32[]]$Token,
    [int]$Context = 3
)

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach ($t in $Token) {
    # IlMatch.Show writes to the console: capture it.
    $writer = New-Object IO.StringWriter
    $console = [Console]::Out
    [Console]::SetOut($writer)
    try { & (Join-Path $Root 'scripts\match.ps1') -Show $t 6>&1 | ForEach-Object { $writer.WriteLine("$_") } } finally { [Console]::SetOut($console) }
    $lines = @($writer.ToString() -split "`r?`n")
    $keep = New-Object bool[] $lines.Count
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($i -eq 0 -or $lines[$i].StartsWith('+') -or $lines[$i].StartsWith('-')) {
            for ($j = [Math]::Max(0, $i - $Context); $j -le [Math]::Min($lines.Count - 1, $i + $Context); $j++) { $keep[$j] = $true }
        }
    }
    $gap = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($keep[$i]) { if ($gap) { '  ...' }; $lines[$i]; $gap = $false } else { $gap = $true }
    }
    ''
}
