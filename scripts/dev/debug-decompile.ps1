<#
.SYNOPSIS
    Decompiles a few files into work/tune/debug and prints the tree of some methods once
    the transforms of scripts/decompiler have run. src/ and work/decomp are not touched.

.DESCRIPTION
    -Files: names of the .cs files to decompile (top-level type names, without extension).
    -Methods: full names as the decompiler prints them (namespace, declaring types and
    method separated by dots: 'osu.Online.ChatEngine.set_State', 'Class244.method_16').
    The tree is the decompiler's IL: what a rule of scripts/decompiler reads and rewrites.
    -Before prints it as the transforms find it as well. Long lines are cut at -Width.

.EXAMPLE
    .\scripts\dev\debug-decompile.ps1 -Files BlockTransformStream -Methods BlockTransformStream.EnsureHeaderRead
    .\scripts\dev\debug-decompile.ps1 -Files pDialog -Methods osu.Graphics.UserInterface.pDialog.method_9 -Match 'position'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Files,
    [string[]]$Methods = @(),
    [switch]$Before,
    [int]$Width = 260,
    [string]$Match = ''
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $Root 'work\tune\debug'
if (Test-Path -LiteralPath $out) { Remove-Item -Recurse -Force -LiteralPath $out }
New-Item -ItemType Directory -Force $out | Out-Null
& dotnet build (Join-Path $Root 'scripts\decompiler\Decompile.csproj') -c Release -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'building scripts/decompiler failed' }

# Same options as the pipeline: the transforms every method takes.
$withs = [IO.File]::ReadAllLines((Join-Path $Root 'scripts\decompiler\transforms.txt')) | Where-Object { $_.Trim() -ne '' -and -not $_.StartsWith('#') } | ForEach-Object { '--with ' + $_.Trim() }
$trace = Join-Path $out 'trees.txt'
$cmd = 'dotnet "{0}" "{1}" -o "{2}" -r "{3}" -lv CSharp7_3 {4} --hints "{5}" 2> "{6}"' -f `
    (Join-Path $Root 'work\tools\decompile\bin\decompile.dll'), (Join-Path $Root 'work\deobf\osu!.named.exe'), $out,
    (Join-Path $Root 'work\triage\in'), ($withs -join ' '), (Join-Path $Root 'docs\decompiler-hints.tsv'), $trace
$env:DECOMP_ONLY = $Files -join ';'
$env:DECOMP_DEBUG = $Methods -join ';'
try { cmd /c $cmd } finally { $env:DECOMP_ONLY = $null; $env:DECOMP_DEBUG = $null }
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $trace | Select-Object -Last 20; throw 'decompile failed' }

$seen = @{}
$on = $false
foreach ($l in Get-Content -LiteralPath $trace) {
    if ($l.StartsWith('== ')) {
        $after = $l.StartsWith('== after ==')
        $key = $l.Substring(0, [Math]::Min($l.Length, $l.IndexOf(' BlockContainer') + 1))
        $on = ($after -or $Before) -and -not $seen[$key]
        $seen[$key] = 1
    }
    if (-not $on -or $l.Trim() -eq '') { continue }
    if ($Match -ne '' -and -not $l.StartsWith('==') -and $l -notmatch $Match) { continue }
    if ($l.Length -gt $Width) { $l.Substring(0, $Width) + ' ...' } else { $l }
}
Write-Host ("sources: {0}" -f $out)
