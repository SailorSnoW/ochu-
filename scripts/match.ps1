<#
.SYNOPSIS
    Read-only: compares the IL of every method of the reference with a candidate build
    and reports the matching rate. See docs/matching.md.

.DESCRIPTION
    A method matches when its instructions (opcodes and operands), local variable types
    and exception handlers are identical. Branch targets are compared as relative
    instruction counts. maxstack and initlocals differences are reported in the note
    column only.

    -Reference gives the IL. -Names is a later pipeline stage of the same binary (same
    metadata tokens): its type, method and field names are used to read the reference.
    The default pair is osu!.clean.exe (faithful IL) + osu!.named.exe (names): de4dot
    rewrites method bodies while renaming, so osu!.named.exe alone is not a valid reference.

    Default mode pairs methods by rendered name: the candidate is a recompilation of src/.
    -ByToken pairs methods by metadata token and compares member operands by token: use it
    to check one pipeline stage against another.

.EXAMPLE
    .\scripts\match.ps1
    .\scripts\match.ps1 -Show 0x060007EC
    .\scripts\match.ps1 -ByToken -Reference 'work\triage\in\osu!.exe' -Candidate 'work\deobf\osu!.strings.exe' -Out work\match\stage-strings.tsv
#>
[CmdletBinding()]
param(
    [string]$Reference = 'work\deobf\osu!.clean.exe',
    [string]$Names = 'work\deobf\osu!.named.exe',
    # The linked build: the main build with the bodies of the units (scripts/link.ps1).
    [string]$Candidate = 'work\build\linked\osu!.exe',
    [string]$Out = 'work\match\report.tsv',
    # The compilation units (docs/units.tsv): the report says which unit compiles each
    # method. With -Candidate 'work\build\bin\osu!.exe' (the main build, not linked) their
    # types are read in the unit's own output, work/build/units/<unit>. '' for none.
    [string]$Units = 'docs\units.tsv',
    # Equivalent readings to switch off, to measure what each absorbs
    # (docs/matching.md): slots, ldelem, ldobj, starg, branch-form, nops, pushes,
    # const-slots, dead-stores. Never for the rate of the project: use another -Out.
    [string[]]$Without = @(),
    [switch]$ByToken,
    [uint32]$Show = 0,
    # Also write the changed lines of every differing method to this file.
    [string]$Dump = ''
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$gameDir = (Join-Path $Root 'game') + '\'
function Resolve-Input([string]$p) {
    if (-not [IO.Path]::IsPathRooted($p)) { $p = Join-Path $Root $p }
    if (-not (Test-Path -LiteralPath $p)) { throw "File not found: $p" }
    $full = (Resolve-Path -LiteralPath $p).Path
    if ($full.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to open a file under game/. Copy it to work/ first.'
    }
    return $full
}
$refPath = Resolve-Input $Reference
$candPath = Resolve-Input $Candidate
$namesPath = $null
if ($Names -ne '') { $namesPath = Resolve-Input $Names }
if ($namesPath -eq $refPath) { $namesPath = $null }

$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
# dnlib redefines ExtensionAttribute: the compiler warning is expected.
# (Already compiled in this session by link.ps1 or match-meta.ps1: the same source.)
if (-not ('IlMatch' -as [type])) { Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib -Path (Join-Path $PSScriptRoot 'lib\IlMatch.cs') }

# The switches are static: a session that ran with some must not keep them.
[IlMatch]::Off.Clear()
# (-File passes a list as one string: split it.)
foreach ($w in @($Without | ForEach-Object { $_.Split(',') } | Where-Object { $_ -ne '' })) {
    if (@('slots', 'ldelem', 'ldobj', 'starg', 'branch-form', 'nops', 'pushes', 'const-slots', 'dead-stores') -notcontains $w) { throw "unknown reading: $w" }
    [void][IlMatch]::Off.Add($w)
}
$refBytes = [IO.File]::ReadAllBytes($refPath)
$candBytes = [IO.File]::ReadAllBytes($candPath)
[byte[]]$nameBytes = $null
if ($namesPath) { $nameBytes = [IO.File]::ReadAllBytes($namesPath) }

# Units: a type token gives a whole type to a compiler unit, a method token is a method
# of the unit 'il'. The names of the types are those of the names module.
$unitList = New-Object 'System.Collections.Generic.List[UnitModule]'
$mainBuild = (Join-Path $Root 'work\build\bin\osu!.exe')
$linkedBuild = (Join-Path $Root 'work\build\linked\osu!.exe')
# For the linked build: which unit compiles a method, by its top-level type or its token.
$unitOfType = @{}
$unitOfMethod = @{}
if ($Units -ne '' -and -not $ByToken -and ($candPath -eq $mainBuild -or $candPath -eq $linkedBuild) -and $nameBytes) {
    $unitsPath = $Units
    if (-not [IO.Path]::IsPathRooted($unitsPath)) { $unitsPath = Join-Path $Root $unitsPath }
    if (Test-Path -LiteralPath $unitsPath) {
        $namesModule = [dnlib.DotNet.ModuleDefMD]::Load($nameBytes)
        $byUnit = [ordered]@{}
        foreach ($line in [IO.File]::ReadAllLines($unitsPath)) {
            $c = $line.Split("`t")
            if ($c.Count -lt 2 -or $c[0] -notmatch '^0x0[26][0-9A-Fa-f]{6}$') { continue }
            if (-not $byUnit.Contains($c[1])) { $byUnit[$c[1]] = New-Object 'System.Collections.Generic.HashSet[string]' }
            if ($c[0].StartsWith('0x02')) {
                $type = $namesModule.ResolveToken([Convert]::ToUInt32($c[0], 16))
                if ($null -eq $type) { throw "units.tsv: no type $($c[0])" }
                [void]$byUnit[$c[1]].Add([string]$type.FullName)
                $unitOfType[[string]$type.FullName] = $c[1]
            } else { $unitOfMethod[[Convert]::ToUInt32($c[0], 16)] = $c[1] }
        }
        # The units that give whole types first, then 'il', whose methods replace theirs.
        # (The linked build already holds their bodies: nothing to read elsewhere.)
        $overlay = @()
        if ($candPath -eq $mainBuild) { $overlay = @($byUnit.Keys | Where-Object { $_ -ne 'il' }) + @($byUnit.Keys | Where-Object { $_ -eq 'il' }) }
        foreach ($name in $overlay) {
            $u = New-Object UnitModule
            $u.Name = $name
            if ($name -eq 'il') { $unitFile = Join-Path $Root 'work\build\units\il\il.dll' }
            elseif ($name -match '^csc\d+$') { $unitFile = Join-Path $Root ("work\build\units\{0}\bin\unit.dll" -f $name); $u.Types = $byUnit[$name] }
            else { $unitFile = Join-Path $Root ("work\build\units\{0}\bin\osu!.exe" -f $name); $u.Types = $byUnit[$name] }
            if (-not (Test-Path -LiteralPath $unitFile)) { throw "unit '$name' is not built: $unitFile (scripts/build.ps1)" }
            $u.Data = [IO.File]::ReadAllBytes($unitFile)
            $unitList.Add($u)
        }
        # Unit of every method of the names module, for the report on the linked build.
        if ($candPath -eq $linkedBuild) {
            foreach ($t in $namesModule.GetTypes()) {
                $top = $t; while ($null -ne $top.DeclaringType) { $top = $top.DeclaringType }
                $unitName = $unitOfType[[string]$top.FullName]
                if (-not $unitName) { continue }
                foreach ($m in $t.Methods) { if (-not $unitOfMethod.ContainsKey($m.MDToken.Raw)) { $unitOfMethod[$m.MDToken.Raw] = $unitName } }
            }
        } else { $unitOfMethod = @{} }
        $namesModule.Dispose()
    }
}

if ($Show -ne 0) {
    [IlMatch]::Show($refBytes, $nameBytes, $candBytes, [bool]$ByToken, $Show, $unitList)
    return
}

if ($Dump -ne '') {
    if (-not [IO.Path]::IsPathRooted($Dump)) { $Dump = Join-Path $Root $Dump }
    [IlMatch]::Dump($refBytes, $nameBytes, $candBytes, [bool]$ByToken, $Dump, $unitList)
    Write-Host ("diff listing: {0}" -f $Dump)
}

$results = [IlMatch]::Compare($refBytes, $nameBytes, $candBytes, [bool]$ByToken, $unitList)
foreach ($r in $results) { $unitName = $unitOfMethod[$r.Token]; if ($unitName) { $r.Unit = $unitName } }

if (-not [IO.Path]::IsPathRooted($Out)) { $Out = Join-Path $Root $Out }
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
$inv = [Globalization.CultureInfo]::InvariantCulture
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("token`tstatus`tscore`tcategory`tsize`tnote`tname`tcause`tshape`tunit")
foreach ($r in $results) {
    $lines.Add(('0x{0:X8}' -f $r.Token) + "`t" + $r.Status + "`t" + $r.Score.ToString('0.0000', $inv) + "`t" + $r.Category + "`t" + $r.Size + "`t" + $r.Note + "`t" + $r.Name + "`t" + $r.Cause + "`t" + $r.Shape + "`t" + $r.Unit)
}
[IO.File]::WriteAllLines($Out, $lines, (New-Object Text.UTF8Encoding($false)))

Write-Host ("reference : {0}" -f $refPath)
if ($namesPath) { Write-Host ("names     : {0}" -f $namesPath) }
Write-Host ("candidate : {0}" -f $candPath)
Write-Host ''
Write-Host ('{0,-8} {1,8} {2,8} {3,8} {4,8} {5,9}' -f 'category', 'methods', 'match', 'diff', 'missing', 'matching')
$cats = @('osu', 'global', 'lib', 'obf', 'ALL')
foreach ($c in $cats) {
    $set = if ($c -eq 'ALL') { $results } else { @($results | Where-Object { $_.Category -eq $c }) }
    $n = @($set).Count
    if ($n -eq 0) { continue }
    $m = @($set | Where-Object { $_.Status -eq 'match' }).Count
    $d = @($set | Where-Object { $_.Status -eq 'diff' }).Count
    $x = @($set | Where-Object { $_.Status -eq 'missing' }).Count
    Write-Host ('{0,-8} {1,8} {2,8} {3,8} {4,8} {5,8}%' -f $c, $n, $m, $d, $x, (100.0 * $m / $n).ToString('0.00', $inv))
}
Write-Host ''
$diffs = @($results | Where-Object { $_.Status -eq 'diff' })
if ($diffs.Count -gt 0) {
    Write-Host 'diff by cause (osu / all):'
    foreach ($g in ($diffs | Group-Object Cause | Sort-Object Count -Descending)) {
        $osu = @($g.Group | Where-Object { $_.Category -eq 'osu' }).Count
        Write-Host ('  {0,-14} {1,6} {2,6}' -f $g.Name, $osu, $g.Count)
    }
    Write-Host ''
}
if ($unitList.Count -gt 0 -or $unitOfMethod.Count -gt 0) {
    Write-Host 'matching methods by compilation unit:'
    foreach ($g in ($results | Where-Object { $_.Status -eq 'match' } | Group-Object Unit | Sort-Object Name)) {
        $label = if ($g.Name -eq '') { 'main' } else { $g.Name }
        Write-Host ('  {0,-14} {1,6}' -f $label, $g.Count)
    }
    Write-Host ''
}
Write-Host ("report    : {0}" -f $Out)
