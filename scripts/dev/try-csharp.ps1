<#
.SYNOPSIS
    Compiles one C# file with a pinned compiler and prints the IL of its methods: what the
    compiler emits for a source form, before a rule is written for it.

.DESCRIPTION
    Same options as build/osu.csproj where they change the IL: optimized, C# 7.3, unsafe.
    The file is a library of its own; declare what it needs. Output goes next to the file.

.EXAMPLE
    .\scripts\dev\try-csharp.ps1 work\exp\t1.cs
    .\scripts\dev\try-csharp.ps1 work\exp\t1.cs -Filter '^M[15]$' -Compiler 3.7.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Source,
    [string]$Filter = '.',
    [string]$Compiler = '3.11.0',
    # /debug+ with /optimize+: every declared local keeps a slot (docs/matching.md).
    [switch]$DebugPlus
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csc = Join-Path $Root "tools\roslyn-$Compiler\tools\csc.exe"
# Microsoft.Net.Compilers.Toolset (4.3 and later) keeps it elsewhere.
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $Root "tools\roslyn-$Compiler\tasks\net472\csc.exe" }
if (-not (Test-Path -LiteralPath $csc)) { throw "No compiler $Compiler under tools/: see scripts/try-compiler.ps1." }
$full = (Resolve-Path -LiteralPath $Source).Path
$out = [IO.Path]::ChangeExtension($full, '.dll')
$dbg = '/debug-'
if ($DebugPlus) { $dbg = '/debug+' }
$log = & $csc /nologo /langversion:7.3 /optimize+ $dbg /unsafe+ /target:library "/out:$out" $full
if ($LASTEXITCODE -ne 0) { $log | ForEach-Object { Write-Host $_ }; return }
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))
$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($out))
try {
    foreach ($t in $mod.GetTypes()) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody -or [string]$m.Name -notmatch $Filter) { continue }
            "== {0}::{1}" -f $t.Name, $m.Name
            foreach ($v in $m.Body.Variables) { "   local {0} {1}" -f $v.Index, $v.Type }
            foreach ($i in $m.Body.Instructions) { '   ' + $i.ToString() }
        }
    }
} finally { $mod.Dispose() }
