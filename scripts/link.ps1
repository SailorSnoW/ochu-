<#
.SYNOPSIS
    Links the compilation units of docs/units.tsv into the main build: one osu!.exe with
    every method body. Run by scripts/build.ps1. See docs/matching.md.

.DESCRIPTION
    Input  : work/build/bin/osu!.exe (main build) and the unit outputs under work/build/units
    Output : work/build/linked/osu!.exe, the candidate of scripts/match.ps1, and
             work/build/linked/link.tsv (what was moved).

    scripts/lib/Linker.cs replaces, in the main build, the body of each method a unit gives
    by the unit's body, its operands re-pointed by name at the main build's definitions.
    The resources embedded under a stand-in name (work/resources/build/renames.tsv) get
    their name. Nothing is written when a body cannot be moved whole.

.EXAMPLE
    .\scripts\link.ps1
#>
[CmdletBinding()]
param(
    [string]$Main = 'work\build\bin\osu!.exe',
    [string]$Units = 'docs\units.tsv',
    [string]$Names = 'work\deobf\osu!.named.exe',
    [string]$Out = 'work\build\linked\osu!.exe'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
function Rooted([string]$p) { if ([IO.Path]::IsPathRooted($p)) { return $p }; return (Join-Path $Root $p) }
# IlMatch.cs may already be compiled in this session without Linker.cs: run in a child then.
if ('IlMatch' -as [type]) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Main $Main -Units $Units -Names $Names -Out $Out
    if ($LASTEXITCODE -ne 0) { throw 'link failed' }
    return
}
$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
# dnlib redefines ExtensionAttribute: the compiler warning is expected.
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib -Path (Join-Path $PSScriptRoot 'lib\IlMatch.cs'), (Join-Path $PSScriptRoot 'lib\Linker.cs')

$mainModule = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Rooted $Main)))
$linker = New-Object Linker($mainModule)

# Units: a type token gives a whole type, a method token is a method of the unit 'il'.
$byUnit = [ordered]@{}
$unitsPath = Rooted $Units
if (Test-Path -LiteralPath $unitsPath) {
    $namesModule = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Rooted $Names)))
    foreach ($line in [IO.File]::ReadAllLines($unitsPath)) {
        $c = $line.Split("`t")
        if ($c.Count -lt 2 -or $c[0] -notmatch '^0x0[26][0-9A-Fa-f]{6}$') { continue }
        if (-not $byUnit.Contains($c[1])) { $byUnit[$c[1]] = New-Object 'System.Collections.Generic.HashSet[string]' }
        if ($c[0].StartsWith('0x02')) {
            $type = $namesModule.ResolveToken([Convert]::ToUInt32($c[0], 16))
            if ($null -eq $type) { throw "units.tsv: no type $($c[0])" }
            [void]$byUnit[$c[1]].Add([string]$type.FullName)
        }
    }
    $namesModule.Dispose()
}
$opened = @()
foreach ($name in @($byUnit.Keys | Where-Object { $_ -ne 'il' }) + @($byUnit.Keys | Where-Object { $_ -eq 'il' })) {
    if ($name -eq 'il') { $unitFile = Join-Path $Root 'work\build\units\il\il.dll' }
    elseif ($name -match '^csc\d+$') { $unitFile = Join-Path $Root ("work\build\units\{0}\bin\unit.dll" -f $name) }
    else { $unitFile = Join-Path $Root ("work\build\units\{0}\bin\osu!.exe" -f $name) }
    if (-not (Test-Path -LiteralPath $unitFile)) { throw "unit '$name' is not built: $unitFile" }
    $unitModule = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($unitFile))
    $opened += $unitModule
    if ($name -eq 'il') { [void]$linker.LinkMethods($name, $unitModule) }
    else { [void]$linker.LinkTypes($name, $unitModule, $byUnit[$name]) }
}

# Resources embedded under a stand-in name: stand-in <TAB> code points of the name.
$renames = Join-Path $Root 'work\resources\build\renames.tsv'
if (Test-Path -LiteralPath $renames) {
    foreach ($line in [IO.File]::ReadAllLines($renames)) {
        $c = $line.Split("`t")
        if ($c.Count -lt 2 -or $c[0] -eq '') { continue }
        $real = -join ($c[1].Split(' ') | ForEach-Object { [char][Convert]::ToInt32($_, 16) })
        $linker.RenameResource($c[0], $real)
    }
}

if ($linker.Problems.Count -gt 0) {
    $linker.Problems | Select-Object -First 30 | ForEach-Object { Write-Host ("  " + $_) }
    Write-Host ("link failed: {0} problems, nothing written" -f $linker.Problems.Count)
    exit 1
}

$outFull = Rooted $Out
New-Item -ItemType Directory -Force (Split-Path -Parent $outFull) | Out-Null
$opts = New-Object dnlib.DotNet.Writer.ModuleWriterOptions($mainModule)
# The bodies the linker did not touch keep what the compiler wrote, maxstack included.
$opts.MetaDataOptions.Flags = $opts.MetaDataOptions.Flags -bor [dnlib.DotNet.Writer.MetaDataFlags]::KeepOldMaxStack
$opts.Logger = [dnlib.DotNet.DummyLogger]::NoThrowInstance
$mainModule.Write($outFull, $opts)
[IO.File]::WriteAllLines((Join-Path (Split-Path -Parent $outFull) 'link.tsv'), $linker.Log, (New-Object Text.UTF8Encoding($false)))
foreach ($m in $opened) { $m.Dispose() }
$mainModule.Dispose()
foreach ($l in $linker.Log) { $c = $l.Split("`t"); Write-Host ("link {0,-10} {1}" -f $c[0], $c[1]) }
Write-Host ("linked -> {0}" -f $outFull)
exit 0
