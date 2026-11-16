<#
.SYNOPSIS
    Read-only: the IL diff of one method with every local named by its type instead of its
    slot, and jump distances left out. In a large method one more temporary renumbers the
    slots and moves every jump: this listing shows where the instructions really differ.

.EXAMPLE
    .\scripts\dev\show-typed.ps1 0x06002EB6 -Context 8
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][uint32]$Token,
    [int]$Context = 6
)

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$writer = New-Object IO.StringWriter
$console = [Console]::Out
[Console]::SetOut($writer)
try { $text = & (Join-Path $Root 'scripts\match.ps1') -Show $Token | Out-String }
finally { [Console]::SetOut($console) }
$lines = ($writer.ToString() + $text) -split "`r?`n"

function Side([string]$mark) {
    $types = @{}
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($l in $lines) {
        if ($l.Length -lt 2) { continue }
        $m = $l.Substring(0, 1)
        if ($m -ne ' ' -and $m -ne $mark) { continue }
        $body = $l.Substring(2)
        if ($body -match '^local (\d+) (.*)$') { $types[[int]$Matches[1]] = $Matches[2]; continue }
        if ($body -match '^(eh|reference:) ') { continue }
        if ($body -match '^(ldloc|stloc|ldloca)(?:\.s)?[ .](?:V_)?(\d+)$') {
            $body = $Matches[1] + ' <' + $types[[int]$Matches[2]] + '>'
        }
        elseif ($body -match '^(br|leave|beq|bne|bge|bgt|ble|blt)(\S*) @') { $body = $Matches[1] + ($Matches[2] -replace '\.s$', '') }
        elseif ($body -match '^switch \(') { $body = 'switch' }
        $out.Add($body)
    }
    return $out
}

$dir = Join-Path $Root 'work\match'
$a = Join-Path $dir 'typed.ref.txt'; $b = Join-Path $dir 'typed.cand.txt'
[IO.File]::WriteAllLines($a, (Side '-'))
[IO.File]::WriteAllLines($b, (Side '+'))
& git --no-pager diff --no-index --no-color "-U$Context" -- $a $b | Select-Object -Skip 4
