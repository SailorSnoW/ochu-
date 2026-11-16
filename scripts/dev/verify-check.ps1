<#
.SYNOPSIS
    Read-only: two checks of the linked build that run none of its code.
    See docs/matching.md.

.DESCRIPTION
    1. Reflection-only load of work/build/linked/osu!.exe in a 32-bit process, next to the
       game's own assemblies: every type must load without a loader error.
    2. PEVerify /IL (the verifier of the .NET Framework SDK, when it is installed) on the
       original osu!.exe and on the linked build: the verifier follows the types on the
       stack through every method. The number of errors of each code must be the same.
       Offsets are not compared: the original still has its strings encrypted, and the
       obfuscator re-encoded bodies, so the same instruction is not at the same offset.

    Copies are made under work/verify. Nothing is executed: no entry point, no module
    initializer.

.EXAMPLE
    .\scripts\dev\verify-check.ps1
#>
[CmdletBinding()]
param(
    [string]$Original = 'work\triage\in\osu!.exe',
    [string]$Linked = 'work\build\linked\osu!.exe',
    [ValidateSet('all', 'load-child')][string]$Stage = 'all',
    [string]$Dir
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if ($Stage -eq 'load-child') {
    $handler = [ResolveEventHandler] {
        param($s, $e)
        $name = (New-Object Reflection.AssemblyName($e.Name)).Name
        $local = Join-Path $Dir ($name + '.dll')
        if (Test-Path -LiteralPath $local) { return [Reflection.Assembly]::ReflectionOnlyLoadFrom($local) }
        return [Reflection.Assembly]::ReflectionOnlyLoad($e.Name)
    }
    [AppDomain]::CurrentDomain.add_ReflectionOnlyAssemblyResolve($handler)
    $asm = [Reflection.Assembly]::ReflectionOnlyLoadFrom((Join-Path $Dir 'osu!.exe'))
    try {
        $types = $asm.GetTypes()
        Write-Host ("reflection-only load: {0} types, no loader error" -f $types.Count)
        exit 0
    } catch [Reflection.ReflectionTypeLoadException] {
        $bad = @($_.Exception.LoaderExceptions)
        Write-Host ("reflection-only load: {0} loader errors" -f $bad.Count)
        $bad | Select-Object -First 10 | ForEach-Object { Write-Host ('   ' + $_.Message) }
        exit 1
    }
}

$work = Join-Path $Root 'work\verify'
function New-Copy([string]$name, [string]$file) {
    $d = Join-Path $work $name
    if (Test-Path -LiteralPath $d) { Remove-Item -Recurse -Force -LiteralPath $d }
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item -LiteralPath (Join-Path $Root $file) -Destination (Join-Path $d 'osu!.exe')
    foreach ($n in 'osu!gameplay.dll', 'osu!ui.dll', 'osu!seasonal.dll', 'OpenTK.dll', 'Microsoft.Ink.dll') {
        Copy-Item -LiteralPath (Join-Path $Root "work\triage\in\$n") -Destination $d
    }
    return $d
}
$linkedDir = New-Copy 'linked' $Linked
$originalDir = New-Copy 'original' $Original

$ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
& $ps32 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Stage load-child -Dir $linkedDir
$loadOk = ($LASTEXITCODE -eq 0)

$pv = @(Get-ChildItem (Join-Path ${env:ProgramFiles(x86)} 'Microsoft SDKs\Windows') -Recurse -Filter PEVerify.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1)
if ($pv.Count -eq 0) { Write-Host 'PEVerify.exe not found (.NET Framework SDK): second check skipped'; return }
function Get-Errors([string]$dir) {
    $out = & $pv[0].FullName (Join-Path $dir 'osu!.exe') /IL /nologo /hresult 2>&1 | ForEach-Object { "$_" }
    $counts = @{}
    foreach ($line in $out) { if ($line -match 'Error: (0x[0-9A-Fa-f]+)\)') { $counts[$Matches[1]] = 1 + [int]$counts[$Matches[1]] } }
    return $counts
}
$a = Get-Errors $originalDir
$b = Get-Errors $linkedDir
$same = $true
Write-Host ('{0,-12} {1,9} {2,9}' -f 'PEVerify /IL', 'original', 'linked')
foreach ($code in (@($a.Keys) + @($b.Keys) | Sort-Object -Unique)) {
    Write-Host ('{0,-12} {1,9} {2,9}' -f $code, [int]$a[$code], [int]$b[$code])
    if ([int]$a[$code] -ne [int]$b[$code]) { $same = $false }
}
if ($same) { Write-Host 'same errors of each code in both files' } else { Write-Host 'DIFFERENT error counts' }
if (-not $loadOk -or -not $same) { exit 1 }
exit 0
