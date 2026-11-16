<#
.SYNOPSIS
    Recompiles src/ with build/osu.csproj into work/build and summarizes compiler errors.

.DESCRIPTION
    Output: work/build/linked/osu!.exe (the candidate of scripts/match.ps1): the main build,
            work/build/bin/osu!.exe, with the bodies of the compilation units linked in
            (scripts/link.ps1); work/build/build.log, work/build/errors.tsv (file, line,
            code, message).
    Then the compilation units of docs/units.tsv. 'debugplus': the same source tree with
    its own options (-p:Unit=<name>, build/osu.csproj): work/build/units/<name>/bin/osu!.exe.
    'csc20', 'csc35', 'csc40': the files of the unit's types alone, compiled as a library by
    the C# compiler of that .NET Framework: work/build/units/<name>/bin/unit.dll.
    The unit 'il' is src/il/unit.il assembled by ilasm: work/build/units/il/il.dll.
    Exit code 0 when the build succeeds, 1 otherwise.

.EXAMPLE
    .\scripts\build.ps1
    .\scripts\build.ps1 -Top 30
#>
[CmdletBinding()]
param(
    [int]$Top = 15
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$OutDir = Join-Path $Root 'work\build'
$Log = Join-Path $OutDir 'build.log'
New-Item -ItemType Directory -Force $OutDir | Out-Null

Push-Location $Root
try {
    $ErrorActionPreference = 'Continue'
    & dotnet build (Join-Path $Root 'build\osu.csproj') -c Release -nologo -v q '-clp:NoSummary' > $Log 2>&1
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
} finally { Pop-Location }

# file(line,col): error CS0000: message [project]
$rx = [regex]'^(?<file>.+?)\((?<line>\d+),\d+\): error (?<code>[A-Z]+\d+): (?<msg>.*?)( \[[^\]]+\])?$'
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$errors = New-Object System.Collections.ArrayList
$other = New-Object System.Collections.ArrayList
foreach ($line in [IO.File]::ReadAllLines($Log)) {
    $m = $rx.Match($line)
    if ($m.Success) {
        if (-not $seen.Add($line)) { continue }
        $file = $m.Groups['file'].Value
        $srcDir = (Join-Path $Root 'src') + '\'
        if ($file.StartsWith($srcDir, [StringComparison]::OrdinalIgnoreCase)) { $file = $file.Substring($srcDir.Length) }
        [void]$errors.Add([pscustomobject]@{ File = $file; Line = [int]$m.Groups['line'].Value; Code = $m.Groups['code'].Value; Message = $m.Groups['msg'].Value })
    } elseif ($line -match ': error ' -and $seen.Add($line)) {
        [void]$other.Add($line)
    }
}

$tsv = New-Object System.Collections.Generic.List[string]
$tsv.Add("file`tline`tcode`tmessage")
foreach ($e in $errors) { $tsv.Add($e.File + "`t" + $e.Line + "`t" + $e.Code + "`t" + $e.Message) }
[IO.File]::WriteAllLines((Join-Path $OutDir 'errors.tsv'), $tsv, (New-Object Text.UTF8Encoding($false)))

if ($code -eq 0) {
    # The compiler units: the whole tree again, with the options of the unit.
    $unitsFile = Join-Path $Root 'docs\units.tsv'
    $units = @()
    $unitTypes = @{}
    if (Test-Path -LiteralPath $unitsFile) {
        foreach ($line in [IO.File]::ReadAllLines($unitsFile)) {
            $c = $line.Split("`t")
            if ($c.Count -ge 2 -and $c[0] -match '^0x02[0-9A-Fa-f]{6}$') {
                if ($units -notcontains $c[1]) { $units += $c[1]; $unitTypes[$c[1]] = @() }
                $unitTypes[$c[1]] += $c[0]
            }
        }
    }
    # Units of the C# compiler of the .NET Framework (the one from before Roslyn; a
    # prerequisite of the project): the files of their types alone, as a library.
    $frameworkCsc = @{ 'csc20' = 'v2.0.50727'; 'csc35' = 'v3.5'; 'csc40' = 'v4.0.30319' }
    foreach ($unit in @($units | Where-Object { $frameworkCsc.ContainsKey($_) })) {
        $csc = Join-Path $env:WINDIR ("Microsoft.NET\Framework\{0}\csc.exe" -f $frameworkCsc[$unit])
        if (-not (Test-Path -LiteralPath $csc)) { Write-Host "Build failed: unit $unit needs $csc"; exit 1 }
        [void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))
        $namesModule = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root 'work\deobf\osu!.named.exe')))
        $files = @()
        foreach ($token in $unitTypes[$unit]) {
            $type = $namesModule.ResolveToken([Convert]::ToUInt32($token, 16))
            if ($null -eq $type) { Write-Host "Build failed: units.tsv, no type $token"; exit 1 }
            $relative = [string]$type.Name + '.cs'
            if ([string]$type.Namespace -ne '') { $relative = Join-Path ([string]$type.Namespace).Replace('.', '\') $relative }
            $file = Join-Path (Join-Path $Root 'src') $relative
            if (-not (Test-Path -LiteralPath $file)) { Write-Host "Build failed: unit $unit, no file $file"; exit 1 }
            $files += $file
        }
        $namesModule.Dispose()
        $unitDir = Join-Path $OutDir ("units\{0}\bin" -f $unit)
        New-Item -ItemType Directory -Force $unitDir | Out-Null
        $unitOut = Join-Path $unitDir 'unit.dll'
        $unitLog = Join-Path $OutDir ("build.{0}.log" -f $unit)
        $ErrorActionPreference = 'Continue'
        & $csc /nologo /optimize+ /debug- /unsafe+ /target:library /warn:0 "/out:$unitOut" $files > $unitLog 2>&1
        $unitCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($unitCode -ne 0) {
            Write-Host ("Build failed: unit {0} (log: {1})" -f $unit, $unitLog)
            Get-Content -LiteralPath $unitLog | Select-Object -First $Top | ForEach-Object { Write-Host ("  " + $_) }
            exit 1
        }
        Write-Host ("unit {0} -> {1}" -f $unit, $unitOut)
    }
    foreach ($unit in @($units | Where-Object { -not $frameworkCsc.ContainsKey($_) })) {
        $unitLog = Join-Path $OutDir ("build.{0}.log" -f $unit)
        Push-Location $Root
        try {
            $ErrorActionPreference = 'Continue'
            & dotnet build (Join-Path $Root 'build\osu.csproj') -c Release -nologo -v q '-clp:NoSummary' "-p:Unit=$unit" > $unitLog 2>&1
            $unitCode = $LASTEXITCODE
            $ErrorActionPreference = 'Stop'
        } finally { Pop-Location }
        if ($unitCode -ne 0) {
            Write-Host ("Build failed: unit {0} (log: {1})" -f $unit, $unitLog)
            Get-Content -LiteralPath $unitLog | Where-Object { $_ -match ': error ' } | Select-Object -First $Top | ForEach-Object { Write-Host ("  " + $_) }
            exit 1
        }
        Write-Host ("unit {0} -> {1}" -f $unit, (Join-Path $OutDir ("units\{0}\bin\osu!.exe" -f $unit)))
    }
    # The unit 'il': src/il/unit.il assembled by the ilasm of the .NET Framework (a
    # prerequisite of the project, like its csc for scripts/dev/try-legacy.ps1).
    $ilSource = Join-Path $Root 'src\il\unit.il'
    if (Test-Path -LiteralPath $ilSource) {
        $ilasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\ilasm.exe'
        if (-not (Test-Path -LiteralPath $ilasm)) { Write-Host "Build failed: no ilasm at $ilasm"; exit 1 }
        $ilDir = Join-Path $OutDir 'units\il'
        New-Item -ItemType Directory -Force $ilDir | Out-Null
        $ilOut = Join-Path $ilDir 'il.dll'
        $ilLog = Join-Path $OutDir 'build.il.log'
        $ErrorActionPreference = 'Continue'
        & $ilasm /nologo /quiet /dll "/output=$ilOut" $ilSource > $ilLog 2>&1
        $ilCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'
        if ($ilCode -ne 0) {
            Write-Host ("Build failed: unit il (log: {0})" -f $ilLog)
            Get-Content -LiteralPath $ilLog | Select-Object -First $Top | ForEach-Object { Write-Host ("  " + $_) }
            exit 1
        }
        Write-Host ("unit il -> {0}" -f $ilOut)
    }
    # One binary: the bodies of the units linked into the main build (scripts/link.ps1).
    $linkLog = Join-Path $OutDir 'build.link.log'
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'link.ps1') > $linkLog 2>&1
    $linkCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($linkCode -ne 0) {
        Write-Host ("Build failed: link (log: {0})" -f $linkLog)
        Get-Content -LiteralPath $linkLog | Select-Object -First $Top | ForEach-Object { Write-Host ("  " + $_) }
        exit 1
    }
    Get-Content -LiteralPath $linkLog | Where-Object { $_ -match '^link ' } | ForEach-Object { Write-Host $_ }
    Write-Host ("Build succeeded -> {0}" -f (Join-Path $OutDir 'linked\osu!.exe'))
    exit 0
}

$files = @($errors | Group-Object File)
Write-Host ("Build failed: {0} errors in {1} files (log: {2})" -f $errors.Count, $files.Count, $Log)
foreach ($o in $other) { Write-Host ("  " + $o) }
Write-Host ''
Write-Host 'By error code:'
foreach ($g in ($errors | Group-Object Code | Sort-Object Count -Descending | Select-Object -First $Top)) {
    Write-Host ('  {0,6}  {1}  {2}' -f $g.Count, $g.Name, $g.Group[0].Message)
}
Write-Host ''
Write-Host 'By file:'
foreach ($g in ($files | Sort-Object Count -Descending | Select-Object -First $Top)) {
    Write-Host ('  {0,6}  {1}' -f $g.Count, $g.Name)
}
exit 1
