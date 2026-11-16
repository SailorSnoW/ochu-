<#
.SYNOPSIS
    Read-only: ranks types that still have a placeholder name by how often the rest
    of the code refers to them, with the evidence needed to name them.

.DESCRIPTION
    Output (work/decomp/type-ranking.txt): one block per type with token, reference
    count, base type, member counts and a sample of its string literals.
    Naming the most referenced types first improves the whole decompiled tree.

.EXAMPLE
    .\scripts\rank-types.ps1 -Top 80
#>
[CmdletBinding()]
param(
    [string]$In = 'work\deobf\osu!.named.exe',
    [string]$Out = 'work\decomp\type-ranking.txt',
    [int]$Top = 80,
    [int]$Strings = 10
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$inFull = (Resolve-Path -LiteralPath (Join-Path $Root $In)).Path
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))
$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($inFull))
$placeholder = '^G?(Class|Struct|Interface|Delegate|Enum|Attribute|Exception|Stream|Form|Control|EventArgs|Dictionary|List|Component)\d+(`\d+)?$'

$refs = @{}
function Hit($typeRef, [uint32]$from) {
    if (-not $typeRef) { return }
    $td = $typeRef.ScopeType -as [dnlib.DotNet.TypeDef]
    if (-not $td) { return }
    while ($td.IsNested) { $td = $td.DeclaringType }
    $k = $td.MDToken.Raw
    if ($k -eq $from) { return }
    $refs[$k] = 1 + [int]$refs[$k]
}

foreach ($t in $mod.GetTypes()) {
    $outer = $t
    while ($outer.IsNested) { $outer = $outer.DeclaringType }
    $from = $outer.MDToken.Raw
    foreach ($f in $t.Fields) { Hit ([dnlib.DotNet.Extensions]::ToTypeDefOrRef($f.FieldType)) $from }
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if ($op -is [dnlib.DotNet.IMethod]) { Hit $op.DeclaringType $from }
            elseif ($op -is [dnlib.DotNet.IField]) { Hit $op.DeclaringType $from }
            elseif ($op -is [dnlib.DotNet.ITypeDefOrRef]) { Hit $op $from }
        }
    }
}

$sb = New-Object System.Collections.Generic.List[string]
$n = 0
foreach ($e in ($refs.GetEnumerator() | Sort-Object Value -Descending)) {
    $t = $mod.ResolveToken([uint32]$e.Key)
    if ([string]$t.Name -notmatch $placeholder -or [string]$t.Namespace -ne '') { continue }
    $n++
    if ($n -gt $Top) { break }
    $base = ''; if ($t.BaseType) { $base = $t.BaseType.FullName }
    $ifaces = ($t.Interfaces | ForEach-Object { $_.Interface.FullName }) -join ', '
    $sb.Add(("0x{0:X8} {1}  refs={2}  base={3}  fields={4} methods={5} nested={6}" -f $t.MDToken.Raw, $t.FullName, $e.Value, $base, $t.Fields.Count, $t.Methods.Count, $t.NestedTypes.Count))
    if ($ifaces) { $sb.Add("    implements: $ifaces") }
    $strs = New-Object System.Collections.Generic.List[string]
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            if ($i.OpCode.Code -eq [dnlib.DotNet.Emit.Code]::Ldstr) {
                $s = [string]$i.Operand
                if ($s.Length -ge 4 -and $s.Length -le 70 -and -not $strs.Contains($s)) { $strs.Add($s) }
            }
        }
        if ($strs.Count -ge $Strings) { break }
    }
    if ($strs.Count) { $sb.Add('    strings: ' + (($strs | Select-Object -First $Strings | ForEach-Object { '"' + ($_ -replace '\s+', ' ') + '"' }) -join ' ')) }
}
$mod.Dispose()
[IO.File]::WriteAllLines((Join-Path $Root $Out), $sb, (New-Object Text.UTF8Encoding($false)))
Write-Host ("{0} types ranked -> {1}" -f ([Math]::Min($n, $Top)), $Out)
