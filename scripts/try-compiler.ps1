<#
.SYNOPSIS
    Compiles a source tree with one of the pinned C# compilers, with the options of
    build/osu.csproj, to compare compilers with scripts/match.ps1.

.DESCRIPTION
    Calls csc directly with a response file (no MSBuild): the old compilers do not know
    the switches a current SDK passes.

    Output: work/compilers/<Name>/osu!.exe, build.log, errors.tsv (code, count, sample).
    Exit code 0 when the compilation succeeds.

    -Compiler : 'sdk' (Roslyn of the .NET SDK) or a version installed by
                scripts/setup-tools.ps1 as tools/roslyn-<version>.
    -Src      : source tree, default src/. Any decompiler output under work/ works.

.EXAMPLE
    .\scripts\try-compiler.ps1 -Compiler 2.10.0 -Src work\exp\lv73 -LangVersion 7.3
    .\scripts\match.ps1 -Candidate 'work\compilers\2.10.0\osu!.exe' -Out work\match\2.10.0.tsv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Compiler,
    [string]$Src = 'src',
    [string]$LangVersion = '',
    [string]$Name = '',
    [int]$Top = 12
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
if ($Name -eq '') { $Name = $Compiler }
$OutDir = Join-Path $Root "work\compilers\$Name"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$srcDir = $Src
if (-not [IO.Path]::IsPathRooted($srcDir)) { $srcDir = Join-Path $Root $srcDir }
$srcDir = (Resolve-Path -LiteralPath $srcDir).Path

# Entry point and references: the ones of build/osu.csproj.
[xml]$proj = Get-Content -LiteralPath (Join-Path $Root 'build\osu.csproj')
$main = [string]($proj.Project.PropertyGroup.StartupObject | Where-Object { $_ } | Select-Object -First 1)
$refDir = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.netframework.referenceassemblies.net40\1.0.3\build\.NETFramework\v4.0'
if (-not (Test-Path -LiteralPath (Join-Path $refDir 'mscorlib.dll'))) { throw "net40 reference assemblies not found in $refDir. Run scripts\build.ps1 once: it restores them." }
$refs = New-Object System.Collections.Generic.List[string]
foreach ($n in 'mscorlib', 'System', 'System.Drawing', 'System.Windows.Forms') { $refs.Add((Join-Path $refDir "$n.dll")) }
foreach ($r in $proj.Project.ItemGroup.Reference) {
    if (-not $r) { continue }
    if ($r.HintPath) { $refs.Add([IO.Path]::GetFullPath((Join-Path (Join-Path $Root 'build') $r.HintPath))) }
    else { $refs.Add((Join-Path $refDir ($r.Include + '.dll'))) }
}

$exe = Join-Path $OutDir 'osu!.exe'
if (Test-Path -LiteralPath $exe) { Remove-Item -LiteralPath $exe -Force }
$rsp = New-Object System.Collections.Generic.List[string]
$rsp.Add('/noconfig'); $rsp.Add('/nostdlib+'); $rsp.Add('/unsafe+'); $rsp.Add('/checked-')
$rsp.Add('/platform:x86'); $rsp.Add('/target:winexe'); $rsp.Add('/optimize+'); $rsp.Add('/debug-')
$rsp.Add('/warn:0'); $rsp.Add('/filealign:512'); $rsp.Add('/highentropyva-'); $rsp.Add('/utf8output')
$rsp.Add('/define:TRACE;RELEASE'); $rsp.Add("/main:$main")
if ($LangVersion -ne '') { $rsp.Add("/langversion:$LangVersion") }
$manifest = Join-Path $srcDir 'app.manifest'
if (Test-Path -LiteralPath $manifest) { $rsp.Add("/win32manifest:`"$manifest`"") }
$rsp.Add("/out:`"$exe`"")
foreach ($r in $refs) { $rsp.Add("/reference:`"$r`"") }
$files = @(Get-ChildItem -LiteralPath $srcDir -Recurse -Filter *.cs)
foreach ($f in $files) { $rsp.Add('"' + $f.FullName + '"') }
$rspPath = Join-Path $OutDir 'csc.rsp'
[IO.File]::WriteAllLines($rspPath, $rsp, (New-Object Text.UTF8Encoding($false)))

$log = Join-Path $OutDir 'build.log'
$ErrorActionPreference = 'Continue'
if ($Compiler -eq 'sdk') {
    $sdk = (& dotnet --version).Trim()
    $csc = Join-Path (Split-Path -Parent (Get-Command dotnet).Source) "sdk\$sdk\Roslyn\bincore\csc.dll"
    & dotnet exec $csc "@$rspPath" > $log 2>&1
} else {
    # Microsoft.Net.Compilers: tools\csc.exe. Microsoft.Net.Compilers.Toolset: tasks\net472\csc.exe.
    $csc = $null
    foreach ($rel in 'tools\csc.exe', 'tasks\net472\csc.exe') {
        $c = Join-Path $Root "tools\roslyn-$Compiler\$rel"
        if (Test-Path -LiteralPath $c) { $csc = $c; break }
    }
    if (-not $csc) { throw "Compiler not installed: tools\roslyn-$Compiler (scripts\setup-tools.ps1)" }
    & $csc "@$rspPath" > $log 2>&1
}
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'

$rx = [regex]'^(?<file>.+?)\((?<line>\d+),\d+\): error (?<code>[A-Z]+\d+): (?<msg>.*)$'
$byCode = @{}
$count = 0
$fileSet = @{}
foreach ($line in [IO.File]::ReadAllLines($log)) {
    $m = $rx.Match($line)
    if (-not $m.Success) { if ($line -match '^error ') { Write-Host $line }; continue }
    $count++
    $fileSet[$m.Groups['file'].Value] = 1
    $c = $m.Groups['code'].Value
    if (-not $byCode.ContainsKey($c)) { $byCode[$c] = @{ N = 0; Sample = ($m.Groups['file'].Value.Replace($srcDir + '\', '') + '(' + $m.Groups['line'].Value + '): ' + $m.Groups['msg'].Value) } }
    $byCode[$c].N++
}
$tsv = New-Object System.Collections.Generic.List[string]
$tsv.Add("code`tcount`tsample")
foreach ($c in ($byCode.Keys | Sort-Object { -$byCode[$_].N })) { $tsv.Add($c + "`t" + $byCode[$c].N + "`t" + $byCode[$c].Sample) }
[IO.File]::WriteAllLines((Join-Path $OutDir 'errors.tsv'), $tsv, (New-Object Text.UTF8Encoding($false)))

if ($code -eq 0 -and (Test-Path -LiteralPath $exe)) {
    Write-Host ("{0}: compiled {1} files -> {2}" -f $Name, $files.Count, $exe)
    exit 0
}
Write-Host ("{0}: failed, {1} errors in {2} files (log: {3})" -f $Name, $count, $fileSet.Count, $log)
foreach ($c in ($byCode.Keys | Sort-Object { -$byCode[$_].N } | Select-Object -First $Top)) {
    $s = $byCode[$c].Sample
    if ($s.Length -gt 200) { $s = $s.Substring(0, 200) }
    Write-Host ('  {0,6}  {1}  {2}' -f $byCode[$c].N, $c, $s)
}
exit 1
