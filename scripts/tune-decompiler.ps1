<#
.SYNOPSIS
    Measures a per-method decompiler option and records the methods it helps in
    docs/decompiler-hints.tsv.

.DESCRIPTION
    Some decompiler choices are right for one method and wrong for the next (nesting
    heuristics, loop forms). For each option:

      1. the whole assembly is decompiled with the option on, into work/tune/<option>;
      2. it is compiled with the pinned compiler and compared with the reference;
      3. a method takes the option when it and its lambdas match more with it than in the
         current work/match/report.tsv, and nothing of them stops matching.

    A method is judged together with the lambdas and local functions decompiled with it
    (work/decomp/family.tsv). Run scripts/pipeline.ps1 -From decomp, build.ps1 and match.ps1
    before (baseline) and after (to apply the new hints).

.EXAMPLE
    .\scripts\tune-decompiler.ps1 -Options NoReduceNestingTransform
#>
[CmdletBinding()]
param(
    [string[]]$Options = @('NoReduceNestingTransform', 'NoHighLevelLoopTransform'),
    [string]$Compiler = '3.11.0',
    # Try the option on the methods that do not match yet, not on the whole assembly: an
    # option that breaks the compilation somewhere else can then still be measured.
    [switch]$OnlyDiff
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$hintsPath = Join-Path $Root 'docs\decompiler-hints.tsv'
$baseline = Join-Path $Root 'work\match\report.tsv'
if (-not (Test-Path -LiteralPath $baseline)) { throw 'No baseline: run build.ps1 and match.ps1 first.' }

function Read-Report([string]$path) {
    $d = @{}
    foreach ($line in [IO.File]::ReadAllLines($path) | Select-Object -Skip 1) {
        $c = $line.Split("`t")
        if ($c.Count -ge 3) { $d[$c[0]] = @($c[1], [double]::Parse($c[2], [Globalization.CultureInfo]::InvariantCulture)) }
    }
    return $d
}
function Read-Family([string]$path) {
    $d = @{}
    if (Test-Path -LiteralPath $path) {
        foreach ($line in [IO.File]::ReadAllLines($path) | Select-Object -Skip 1) { $c = $line.Split("`t"); if ($c.Count -ge 2) { $d[$c[0]] = $c[1] } }
    }
    return $d
}

$base = Read-Report $baseline
foreach ($opt in $Options) {
    Write-Host "== $opt"
    $dir = Join-Path $Root "work\tune\$opt"
    if ($OnlyDiff) {
        # Current table, plus the option for every method that has a diff in its family.
        $current = Read-Family (Join-Path $Root 'work\decomp\family.tsv')
        $owners = @{}
        foreach ($t in $base.Keys) {
            if ($base[$t][0] -eq 'match') { continue }
            $o = $t; if ($current.ContainsKey($t)) { $o = $current[$t] }
            $owners[$o] = $true
        }
        $trial = @{}
        if (Test-Path -LiteralPath $hintsPath) {
            foreach ($line in [IO.File]::ReadAllLines($hintsPath)) {
                if ($line.StartsWith('#') -or $line.Trim() -eq '') { continue }
                $c = $line.Split("`t"); $trial[$c[0]] = $c[1]
            }
        }
        foreach ($o in $owners.Keys) { if ($trial.ContainsKey($o)) { $trial[$o] = $trial[$o] + ',' + $opt } else { $trial[$o] = $opt } }
        New-Item -ItemType Directory -Force $dir | Out-Null
        $trialPath = Join-Path $dir 'hints.tsv'
        # Declaring type of each method, to drop the option where the trial does not compile.
        $typeOf = @{}
        foreach ($line in [IO.File]::ReadAllLines($baseline) | Select-Object -Skip 1) {
            $c = $line.Split("`t")
            if ($c.Count -ge 7 -and $c[6] -match '(\S+)::[^:]+$') { $typeOf[$c[0]] = ($Matches[1] -replace '/.*$', '') }
        }
        for ($attempt = 1; $attempt -le 4; $attempt++) {
            [IO.File]::WriteAllLines($trialPath, [string[]]@($trial.Keys | Sort-Object | ForEach-Object { $_ + "`t" + $trial[$_] }), (New-Object Text.UTF8Encoding($false)))
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'pipeline.ps1') -From decomp -HintsFile $trialPath `
                -RawDir (Join-Path $dir 'raw') -SrcDir (Join-Path $dir 'src') -LenientFixups | Select-String 'skipped|failed' | ForEach-Object { Write-Host $_.Line }
            if ($LASTEXITCODE -ne 0) { break }
            $log = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'try-compiler.ps1') -Compiler $Compiler -Src (Join-Path $dir 'src') -LangVersion 7.3 -Name "tune-$opt"
            if ($LASTEXITCODE -eq 0) { break }
            # Files with errors -> types -> owners that lose the option.
            $bad = @{}
            foreach ($l in $log) { if ("$l" -match '\\src\\(.+?)\.cs\(\d+\)') { $bad[($Matches[1] -replace '\\', '.')] = $true } }
            $dropped = 0
            foreach ($o in @($owners.Keys)) {
                if ($typeOf.ContainsKey($o) -and $bad.ContainsKey($typeOf[$o])) {
                    $owners.Remove($o); $dropped++
                    $rest = @($trial[$o].Split(',') | Where-Object { $_ -ne $opt })
                    if ($rest.Count -eq 0) { $trial.Remove($o) } else { $trial[$o] = $rest -join ',' }
                }
            }
            Write-Host ("   attempt {0}: errors in {1} types, option dropped for {2} methods" -f $attempt, $bad.Count, $dropped)
            if ($dropped -eq 0) { break }
        }
    } else {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'pipeline.ps1') -From decomp -With $opt `
            -RawDir (Join-Path $dir 'raw') -SrcDir (Join-Path $dir 'src') -LenientFixups | Select-String 'skipped|failed' | ForEach-Object { Write-Host $_.Line }
    }
    if ($LASTEXITCODE -ne 0) { Write-Host "   decompilation failed, option skipped"; continue }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'try-compiler.ps1') -Compiler $Compiler -Src (Join-Path $dir 'src') -LangVersion 7.3 -Name "tune-$opt" | ForEach-Object { Write-Host "   $_" }
    if ($LASTEXITCODE -ne 0) { Write-Host "   does not compile, option skipped"; continue }
    $report = Join-Path $Root "work\match\tune-$opt.tsv"
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'match.ps1') -Candidate "work\compilers\tune-$opt\osu!.exe" -Out $report | Select-String '^ALL' | ForEach-Object { Write-Host "   $($_.Line)" }
    $trial = Read-Report $report
    $family = Read-Family (Join-Path $dir 'family.tsv')

    # owner token -> members (itself and its lambdas)
    $members = @{}
    foreach ($t in $base.Keys) {
        $owner = $t; if ($family.ContainsKey($t)) { $owner = $family[$t] }
        if (-not $members.ContainsKey($owner)) { $members[$owner] = New-Object System.Collections.ArrayList }
        [void]$members[$owner].Add($t)
    }
    $picked = New-Object System.Collections.Generic.List[string]
    foreach ($owner in $members.Keys) {
        $gain = 0; $loss = 0
        foreach ($t in $members[$owner]) {
            $b = $base[$t][0] -eq 'match'
            $n = $trial.ContainsKey($t) -and $trial[$t][0] -eq 'match'
            if ($n -and -not $b) { $gain++ } elseif ($b -and -not $n) { $loss++ }
        }
        if ($gain -gt 0 -and $loss -eq 0) { $picked.Add($owner) }
    }
    # What this option gained is part of the baseline of the next one.
    foreach ($owner in $picked) { foreach ($t in $members[$owner]) { if ($trial.ContainsKey($t)) { $base[$t] = $trial[$t] } } }
    Write-Host ("   {0} methods take the option" -f $picked.Count)
    if ($picked.Count -eq 0) { continue }

    # Merge into the hints table: token<TAB>option[,option]
    $table = @{}
    $header = @('# Per-method decompiler options, measured by scripts/tune-decompiler.ps1. Do not edit by hand.', "# token`toptions")
    if (Test-Path -LiteralPath $hintsPath) {
        foreach ($line in [IO.File]::ReadAllLines($hintsPath)) {
            if ($line.StartsWith('#') -or $line.Trim() -eq '') { continue }
            $c = $line.Split("`t"); $table[$c[0]] = New-Object System.Collections.Generic.List[string]
            foreach ($o in $c[1].Split(',')) { $table[$c[0]].Add($o) }
        }
    }
    foreach ($owner in $picked) {
        if (-not $table.ContainsKey($owner)) { $table[$owner] = New-Object System.Collections.Generic.List[string] }
        if (-not $table[$owner].Contains($opt)) { $table[$owner].Add($opt) }
    }
    $out = New-Object System.Collections.Generic.List[string]
    $out.AddRange([string[]]$header)
    foreach ($k in ($table.Keys | Sort-Object)) { $out.Add($k + "`t" + (($table[$k] | Sort-Object) -join ',')) }
    [IO.File]::WriteAllLines($hintsPath, $out, (New-Object Text.UTF8Encoding($false)))
}
Write-Host 'Done. Run pipeline.ps1 -From decomp, build.ps1 and match.ps1 to apply the hints.'
