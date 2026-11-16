<#
.SYNOPSIS
    Read-only: writes a token index of an assembly (types, methods, fields, properties).

.DESCRIPTION
    One line per symbol: token<TAB>kind<TAB>declaring type<TAB>name<TAB>signature.
    Used to find the token of a symbol seen in the decompiled source when adding a
    line to docs/names.tsv.

.EXAMPLE
    .\scripts\dump-symbols.ps1 -Path 'work\deobf\osu!.renamed.exe' -Out work\decomp\symbols.tsv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$full = (Resolve-Path -LiteralPath $Path).Path
$gameDir = (Join-Path $Root 'game') + '\'
if ($full.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to open a file under game/. Copy it to work/ first.'
}
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))
$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($full))

$lines = New-Object System.Collections.Generic.List[string]
foreach ($t in $mod.GetTypes()) {
    $kind = 'class'
    if ($t.IsInterface) { $kind = 'interface' } elseif ($t.IsEnum) { $kind = 'enum' } elseif ($t.IsValueType) { $kind = 'struct' }
    $base = ''
    if ($t.BaseType) { $base = $t.BaseType.FullName }
    $lines.Add(("0x{0:X8}`ttype`t{1}`t{2}`t{3} : {4}" -f $t.MDToken.Raw, $t.FullName, $t.Name, $kind, $base))
    foreach ($f in $t.Fields) {
        $lines.Add(("0x{0:X8}`tfield`t{1}`t{2}`t{3}" -f $f.MDToken.Raw, $t.FullName, $f.Name, $f.FieldType.FullName))
    }
    foreach ($p in $t.Properties) {
        $lines.Add(("0x{0:X8}`tproperty`t{1}`t{2}`t{3}" -f $p.MDToken.Raw, $t.FullName, $p.Name, $p.PropertySig.ToString()))
    }
    foreach ($m in $t.Methods) {
        $lines.Add(("0x{0:X8}`tmethod`t{1}`t{2}`t{3}" -f $m.MDToken.Raw, $t.FullName, $m.Name, $m.MethodSig.ToString()))
    }
}
$mod.Dispose()
$outFull = $Out
if (-not [IO.Path]::IsPathRooted($outFull)) { $outFull = Join-Path (Get-Location) $Out }
[IO.File]::WriteAllLines($outFull, $lines, (New-Object Text.UTF8Encoding($false)))
Write-Host ("{0} symbols -> {1}" -f $lines.Count, $outFull)
