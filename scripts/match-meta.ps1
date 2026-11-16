<#
.SYNOPSIS
    Read-only: compares the declarations of the reference with a candidate build, which
    scripts/match.ps1 does not look at: types, fields, methods, their flags, base types,
    interfaces, custom attributes, constants, P/Invoke data, member order, resources.
    See docs/matching.md.

.DESCRIPTION
    Members are paired by rendered name, as match.ps1 pairs methods. Flags, attributes and
    order are read in the names stage (osu!.named.exe): the reference with the properties,
    events and [CompilerGenerated] marks that stage puts back.

    Output: work/match/meta.tsv (kind, category, item, reference, candidate) and a count
    by kind. The candidate is the linked build (scripts/link.ps1).

.EXAMPLE
    .\scripts\match-meta.ps1
    .\scripts\match-meta.ps1 -Kind 'method flags' -First 20
#>
[CmdletBinding()]
param(
    [string]$Reference = 'work\deobf\osu!.clean.exe',
    [string]$Names = 'work\deobf\osu!.named.exe',
    [string]$Candidate = 'work\build\linked\osu!.exe',
    [string]$Out = 'work\match\meta.tsv',
    # Print the first differences of one kind.
    [string]$Kind = '',
    [int]$First = 15
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$gameDir = (Join-Path $Root 'game') + '\'
function Resolve-Input([string]$p) {
    if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path $Root $p }
    if (-not (Test-Path -LiteralPath $p)) { throw "File not found: $p" }
    $full = (Resolve-Path -LiteralPath $p).Path
    if ($full.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to open a file under game/. Copy it to work/ first.' }
    return $full
}
# A session that already ran match.ps1 holds IlMatch.cs compiled without MetaMatch.cs:
# the two cannot be compiled together a second time there. Run in a child process then.
if ('IlMatch' -as [type]) {
    $childArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Reference', $Reference, '-Names', $Names, '-Candidate', $Candidate, '-Out', $Out, '-First', $First)
    if ($Kind -ne '') { $childArgs += '-Kind'; $childArgs += $Kind }
    & powershell.exe @childArgs
    return
}
$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
# dnlib redefines ExtensionAttribute: the compiler warning is expected.
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib -Path (Join-Path $PSScriptRoot 'lib\IlMatch.cs'), (Join-Path $PSScriptRoot 'lib\MetaMatch.cs')

$diffs = [MetaMatch]::Compare([IO.File]::ReadAllBytes((Resolve-Input $Reference)), [IO.File]::ReadAllBytes((Resolve-Input $Names)), [IO.File]::ReadAllBytes((Resolve-Input $Candidate)))

if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $Root $Out }
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("kind`tcategory`titem`treference`tcandidate")
foreach ($d in $diffs) { $lines.Add($d.Kind + "`t" + $d.Category + "`t" + $d.Item + "`t" + $d.Reference + "`t" + $d.Candidate) }
[IO.File]::WriteAllLines($Out, $lines, (New-Object Text.UTF8Encoding($false)))

foreach ($d in ($diffs | Where-Object { $_.Kind.StartsWith('(count)') })) { Write-Host ('{0,-18} reference {1,6}   candidate {2,6}' -f $d.Kind.Substring(8), $d.Reference, $d.Candidate) }
Write-Host ''
Write-Host ('{0,-42} {1,6} {2,6} {3,6} {4,6} {5,6}' -f 'difference', 'all', 'osu', 'global', 'lib', 'obf')
foreach ($g in ($diffs | Where-Object { -not $_.Kind.StartsWith('(count)') } | Group-Object Kind | Sort-Object Count -Descending)) {
    $by = @{}; foreach ($d in $g.Group) { $by[$d.Category] = 1 + [int]$by[$d.Category] }
    Write-Host ('{0,-42} {1,6} {2,6} {3,6} {4,6} {5,6}' -f $g.Name, $g.Count, [int]$by['osu'], [int]$by['global'], [int]$by['lib'], [int]$by['obf'])
}
Write-Host ''
Write-Host ("report: {0}" -f $Out)
if ($Kind -ne '') {
    Write-Host ''
    foreach ($d in ($diffs | Where-Object { $_.Kind -eq $Kind } | Select-Object -First $First)) {
        Write-Host $d.Item
        Write-Host ('   reference: ' + $d.Reference)
        Write-Host ('   candidate: ' + $d.Candidate)
    }
}
