<#
.SYNOPSIS
    Read-only: a second opinion on the matching rate, from code this project did not write.
    The disassembler of the ILSpy engine prints every method body of the reference (names
    stage) and of the linked build; bodies whose text is the same are counted, and the count
    is set against what scripts/match.ps1 says with every equivalent reading off.
    See docs/matching.md.

.DESCRIPTION
    scripts/match.ps1 reads both files with dnlib and compares them with scripts/lib/IlMatch.cs.
    Here the files are read with System.Reflection.Metadata and printed by ILSpy's
    MethodBodyDisassembler (scripts/decompiler --il-hash); the only code of the project in
    the path is the loop that hashes the printed text. Methods are paired by declaring type,
    name and signature.

    What the text contains: instructions with their operands by name, local types, handler
    table. Left out: the place of the body in the file, the size of its header, .maxstack.
    A method that refers to a member the compiler generated (a lambda, a closure) under
    another ordinal differs here although its code is the same: such methods are counted
    as different. The text is therefore hashed a second time with those ordinals masked
    (and the names of the data fields of <PrivateImplementationDetails>); the generated
    methods themselves stay unpaired, so the count is a lower bound.

    Needs work/match/strict-all-off.tsv (scripts/dev/strict-match.ps1).

.EXAMPLE
    .\scripts\dev\outside-check.ps1
#>
[CmdletBinding()]
param(
    [string]$Reference = 'work\deobf\osu!.named.exe',
    [string]$Candidate = 'work\build\linked\osu!.exe',
    [string]$Strict = 'work\match\strict-all-off.tsv',
    [string]$Out = 'work\match\outside.tsv'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function Rooted([string]$p) { if ([IO.Path]::IsPathRooted($p)) { return $p }; return (Join-Path $Root $p) }
$tool = Join-Path $Root 'work\tools\decompile\bin\decompile.dll'
& dotnet build (Join-Path $Root 'scripts\decompiler\Decompile.csproj') -c Release -nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'building scripts/decompiler failed' }
$refs = Join-Path $Root 'work\triage\in'
$a = Join-Path $Root 'work\match\outside.reference.tsv'
$b = Join-Path $Root 'work\match\outside.candidate.tsv'
& dotnet $tool (Rooted $Reference) -o $a --il-hash -r $refs
if ($LASTEXITCODE -ne 0) { throw 'hashing the reference failed' }
& dotnet $tool (Rooted $Candidate) -o $b --il-hash -r $refs
if ($LASTEXITCODE -ne 0) { throw 'hashing the candidate failed' }

$cand = New-Object 'System.Collections.Generic.Dictionary[string,string]'
$dupes = 0
foreach ($line in [IO.File]::ReadAllLines($b) | Select-Object -Skip 1) {
    $c = $line.Split("`t")
    if ($cand.ContainsKey($c[1])) { $dupes++; $cand[$c[1]] = '' } else { $cand[$c[1]] = $c[3] + ' ' + $c[4] }
}
$strictStatus = @{}
$strictPath = Rooted $Strict
if (Test-Path -LiteralPath $strictPath) { foreach ($r in Import-Csv -Delimiter "`t" -LiteralPath $strictPath) { $strictStatus[$r.token] = $r.status } }

$same = 0; $different = 0; $unpaired = 0; $total = 0; $sameRaw = 0
$both = 0; $onlyOutside = 0; $onlyStrict = 0; $neither = 0
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("token`toutside`tstrict`tname")
foreach ($line in [IO.File]::ReadAllLines($a) | Select-Object -Skip 1) {
    $c = $line.Split("`t")
    $total++
    $verdict = 'unpaired'
    if ($cand.ContainsKey($c[1]) -and $cand[$c[1]] -ne '') {
        $h = $cand[$c[1]].Split(' ')
        if ($h[0] -eq $c[3]) { $sameRaw++ }
        if ($h[1] -eq $c[4]) { $verdict = 'same'; $same++ } else { $verdict = 'different'; $different++ }
    }
    else { $unpaired++ }
    $s = $strictStatus[$c[0]]
    if ($verdict -eq 'same' -and $s -eq 'match') { $both++ }
    elseif ($verdict -eq 'same') { $onlyOutside++ }
    elseif ($s -eq 'match') { $onlyStrict++ }
    else { $neither++ }
    $lines.Add($c[0] + "`t" + $verdict + "`t" + $s + "`t" + $c[1])
}
[IO.File]::WriteAllLines((Rooted $Out), $lines, (New-Object Text.UTF8Encoding($false)))

Write-Host ("methods with a body in the reference : {0}" -f $total)
Write-Host ("same text in the linked build        : {0}  ({1} before the ordinals of generated names are masked)" -f $same, $sameRaw)
Write-Host ("different text                       : {0}" -f $different)
Write-Host ("not paired by name and signature     : {0}" -f $unpaired)
if ($strictStatus.Count -gt 0) {
    Write-Host ''
    Write-Host 'against match.ps1 with every reading off:'
    Write-Host ("  identical for both tools            : {0}" -f $both)
    Write-Host ("  identical for match.ps1 only        : {0}  (generated names with another ordinal, pairing)" -f $onlyStrict)
    Write-Host ("  identical for the outside tool only : {0}  (must be 0: match.ps1 would be too strict nowhere else)" -f $onlyOutside)
    Write-Host ("  different for both                  : {0}  (the methods that need an equivalent reading)" -f $neither)
}
Write-Host ("report: {0}" -f (Rooted $Out))
