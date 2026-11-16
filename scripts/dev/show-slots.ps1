<#
.SYNOPSIS
    Read-only: for one method whose instructions are the same on both sides but whose local
    slots are not, which slots of the candidate each slot of the reference stands for.

.DESCRIPTION
    A slot of the reference that maps to several slots of the candidate is one variable of the
    source that src/ declares several times (or a temporary the compiler reused); several
    slots of the reference for one of the candidate is the reverse. Each part is printed with
    the offsets (in the reference) of its first and last use and the number of uses, which
    says where in the method each variable lives.

    The candidate method is found by its full name (type, name and signature): methods the
    compiler generates (lambdas, local functions) are not found.

.EXAMPLE
    .\scripts\dev\show-slots.ps1 0x060027B8
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][uint32]$Token,
    [string]$Candidate = 'work\build\bin\osu!.exe'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))
$refMod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root 'work\deobf\osu!.named.exe')))
$candMod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root $Candidate)))
try {
    $r = $refMod.ResolveToken($Token)
    if ($null -eq $r -or -not $r.HasBody) { throw ('no method body at token 0x{0:X8}' -f $Token) }
    $c = $null
    foreach ($t in $candMod.GetTypes()) {
        if ($t.FullName -ne $r.DeclaringType.FullName) { continue }
        foreach ($m in $t.Methods) { if ($m.HasBody -and $m.FullName -eq $r.FullName) { $c = $m } }
    }
    if ($null -eq $c) { throw "not found in the candidate: $($r.FullName)" }

    function Get-Uses($m) {
        $list = New-Object System.Collections.ArrayList
        foreach ($i in $m.Body.Instructions) {
            $l = $i.GetLocal($m.Body.Variables)
            if ($null -ne $l) { [void]$list.Add(@($i.Offset, $l.Index, [string]$l.Type)) }
        }
        return ,$list
    }
    $ru = Get-Uses $r
    $cu = Get-Uses $c
    $r.FullName
    'reference: {0} locals, {1} uses; candidate: {2} locals, {3} uses' -f $r.Body.Variables.Count, $ru.Count, $c.Body.Variables.Count, $cu.Count
    if ($ru.Count -ne $cu.Count) {
        'The uses of locals differ in number: the instructions are not the same yet (show-typed.ps1).'
        return
    }
    $map = @{}
    for ($k = 0; $k -lt $ru.Count; $k++) {
        $key = '{0,3} {1}' -f $ru[$k][1], $ru[$k][2]
        if (-not $map.ContainsKey($key)) { $map[$key] = New-Object System.Collections.ArrayList }
        $parts = $map[$key]
        $last = $null
        if ($parts.Count -gt 0) { $last = $parts[$parts.Count - 1] }
        if ($null -ne $last -and $last.Slot -eq $cu[$k][1]) { $last.To = $ru[$k][0]; $last.N++ }
        else { [void]$parts.Add([pscustomobject]@{ Slot = $cu[$k][1]; From = $ru[$k][0]; To = $ru[$k][0]; N = 1 }) }
    }
    foreach ($key in ($map.Keys | Sort-Object)) {
        $text = $map[$key] | ForEach-Object { 'c{0}[{1:X4}-{2:X4} x{3}]' -f $_.Slot, $_.From, $_.To, $_.N }
        $mark = ' '
        if (@($map[$key] | Select-Object -ExpandProperty Slot -Unique).Count -gt 1) { $mark = '*' }
        '{0} ref {1} -> {2}' -f $mark, $key, ($text -join ' ')
    }
    # The reverse: one slot of the candidate for several of the reference.
    $back = @{}
    for ($k = 0; $k -lt $ru.Count; $k++) {
        $key = [int]$cu[$k][1]
        if (-not $back.ContainsKey($key)) { $back[$key] = New-Object System.Collections.Generic.HashSet[int] }
        [void]$back[$key].Add([int]$ru[$k][1])
    }
    foreach ($key in ($back.Keys | Sort-Object)) {
        if ($back[$key].Count -gt 1) { '* candidate c{0} stands for reference slots {1}' -f $key, (($back[$key] | Sort-Object) -join ', ') }
    }
} finally { $refMod.Dispose(); $candMod.Dispose() }
