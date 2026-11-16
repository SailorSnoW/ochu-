<#
.SYNOPSIS
    Compiles one C# file with a compiler of the .NET Framework (csc 2.0, 3.5 or 4.x, the
    compilers from before Roslyn) and prints the IL of its methods.

.DESCRIPTION
    The counterpart of try-csharp.ps1 for the code of the binary that does not come from
    Roslyn (docs/matching.md). The compilers are those of the
    32-bit .NET Framework, a prerequisite of the project: nothing is installed.

.EXAMPLE
    .\scripts\dev\try-legacy.ps1 work\exp\t1.cs
    .\scripts\dev\try-legacy.ps1 work\exp\t1.cs -Filter '^M[15]$' -Compiler v3.5 -NoOptimize
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$Source,
    [string]$Filter = '.',
    [ValidateSet('v2.0.50727', 'v3.5', 'v4.0.30319')][string]$Compiler = 'v4.0.30319',
    [switch]$NoOptimize
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\$Compiler\csc.exe"
if (-not (Test-Path -LiteralPath $csc)) { throw "No compiler at $csc." }
$full = (Resolve-Path -LiteralPath $Source).Path
$out = [IO.Path]::ChangeExtension($full, '.dll')
$opt = '/optimize+'
if ($NoOptimize) { $opt = '/optimize-' }
$log = & $csc /nologo $opt /unsafe+ /target:library "/out:$out" $full
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
            foreach ($h in $m.Body.ExceptionHandlers) { "   eh {0} {1}" -f $h.HandlerType, $h.CatchType }
        }
    }
} finally { $mod.Dispose() }
