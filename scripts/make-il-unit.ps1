<#
.SYNOPSIS
    Writes the IL source of the unit 'il': the methods docs/units.tsv lists by token
    (0x06......), disassembled from the names stage. See docs/matching.md.

.DESCRIPTION
    Input  : work/deobf/osu!.named.exe, docs/units.tsv
    Output : <Out> (work/decomp/raw/il/unit.il in the pipeline, mirrored to src/il/unit.il)

    scripts/lib/IlUnit.cs builds a module that holds those methods alone, every mention of
    the program being a reference to the assembly of the main build; the disassembler of
    scripts/decompiler prints it as IL source. scripts/build.ps1 assembles it with ilasm
    and scripts/match.ps1 reads those methods there.

    Without any method of the unit 'il' in docs/units.tsv, nothing is written.

.EXAMPLE
    .\scripts\make-il-unit.ps1 -Out work\decomp\raw\il\unit.il
#>
[CmdletBinding()]
param(
    [string]$Names = 'work\deobf\osu!.named.exe',
    [string]$Units = 'docs\units.tsv',
    [Parameter(Mandatory = $true)][string]$Out
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
function Rooted([string]$p) { if ([IO.Path]::IsPathRooted($p)) { return $p }; return (Join-Path $Root $p) }
$namesPath = Rooted $Names
$unitsPath = Rooted $Units
$outPath = Rooted $Out
if (Test-Path -LiteralPath $outPath) { Remove-Item -LiteralPath $outPath -Force }

$tokens = New-Object 'System.Collections.Generic.List[uint32]'
$notes = @{}
if (Test-Path -LiteralPath $unitsPath) {
    foreach ($line in [IO.File]::ReadAllLines($unitsPath)) {
        $c = $line.Split("`t")
        if ($c.Count -lt 2 -or $c[0] -notmatch '^0x06[0-9A-Fa-f]{6}$') { continue }
        if ($c[1] -ne 'il') { throw "units.tsv: a method ($($c[0])) can only be given to the unit 'il'" }
        $tokens.Add([Convert]::ToUInt32($c[0], 16))
    }
}
if ($tokens.Count -eq 0) { Write-Host '      no method in the unit il'; return }

$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
# dnlib redefines ExtensionAttribute: the compiler warning is expected.
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib -Path (Join-Path $PSScriptRoot 'lib\IlUnit.cs')

$work = Join-Path $Root 'work\decomp\il-unit.dll'
$source = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($namesPath))
try { $report = [IlUnit]::Build($source, $tokens, $work) } finally { $source.Dispose() }

$tool = Join-Path $Root 'work\tools\decompile\bin\decompile.dll'
if (-not (Test-Path -LiteralPath $tool)) { throw "scripts/decompiler is not built: $tool" }
$body = "$outPath.body"
& dotnet $tool $work -o $body --il
if ($LASTEXITCODE -ne 0) { throw 'disassembling the unit il failed' }

$head = New-Object System.Collections.Generic.List[string]
$head.Add('// The methods of the unit ''il'' (docs/units.tsv): what no C# compiler of the project gives')
$head.Add('// back, kept as IL. Written by scripts/make-il-unit.ps1 from the names stage and assembled')
$head.Add('// by scripts/build.ps1 (ilasm): never edit by hand.')
$head.Add('//')
foreach ($r in $report) { $c = $r.Split("`t"); $head.Add('//   ' + $c[0] + '  ' + $c[1]) }
$head.Add('')
# The disassembler prints a handler table entry as '.try IL_a-IL_b <kind> IL_c-IL_d';
# ilasm reads '.try IL_a to IL_b <kind> handler IL_c to IL_d'.
$rxHandler = New-Object Text.RegularExpressions.Regex('(?m)^(\s*)\.try (IL_[0-9a-f]+)-(IL_[0-9a-f]+) (.+?) (IL_[0-9a-f]+)-(IL_[0-9a-f]+)(\r?)$')
$listing = $rxHandler.Replace([IO.File]::ReadAllText($body), '$1.try $2 to $3 $4 handler $5 to $6$7')
$text = ($head -join "`r`n") + "`r`n" + $listing
[IO.File]::WriteAllText($outPath, $text, (New-Object Text.UTF8Encoding($false)))
Remove-Item -LiteralPath $body -Force
Write-Host ("      {0} methods of the unit il -> {1}" -f $tokens.Count, $outPath)
