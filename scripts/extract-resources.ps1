<#
.SYNOPSIS
    Recovers the managed resources of the game, which the obfuscator moved out of osu!.exe
    into an assembly it embeds encrypted. See docs/pipeline.md.

.DESCRIPTION
    Input  : work/triage/in/osu!.exe
    Output : work/resources/raw/<n>.bin, one per resource of the embedded assembly, and
             work/resources/raw/index.tsv (n, name, length, sha1).

    The embedded assembly is decrypted with a key that depends on who asks (the assembly
    of the caller, its name): only the resolver of the binary itself can do it. So, as
    scripts/decrypt-strings.ps1 does for the strings, a 32-bit PowerShell child loads a
    copy of the exe (LoadFile, the entry point is never run), lets its module initializer
    subscribe the resolver to AssemblyResolve, and asks for the embedded assembly by name.
    The resolver loads it; its resources are then read as streams.

    This executes obfuscator runtime code from the game binary, locally: the module
    initializer and the resolver. Everything is read from and written to work/.

.EXAMPLE
    .\scripts\extract-resources.ps1
#>
[CmdletBinding()]
param(
    [string]$In = 'work\triage\in\osu!.exe',
    [string]$OutDir = 'work\resources\raw',
    # Simple name of the embedded assembly: the first entry of the resolver's table
    # (EmbeddedAssemblyLoader), which the resource resolver (ResourceAssemblyResolver) names as well.
    [string]$EmbeddedAssembly = 'zx_6cf3e95f19724fb3213a664dc47d6443',
    [ValidateSet('all', 'dump-child')][string]$Stage = 'all',
    [string]$Copy
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot

if ($Stage -eq 'dump-child') {
    # Runs inside 32-bit PowerShell.
    if ([IntPtr]::Size -ne 4) { throw 'dump-child must run in a 32-bit process' }
    $asm = [Reflection.Assembly]::LoadFile($Copy)
    [Runtime.CompilerServices.RuntimeHelpers]::RunModuleConstructor($asm.ManifestModule.ModuleHandle)
    $embedded = [Reflection.Assembly]::Load($EmbeddedAssembly + ', Culture=neutral, PublicKeyToken=null')
    if ($null -eq $embedded) { throw 'the resolver did not give the embedded assembly' }
    $sha = [Security.Cryptography.SHA1]::Create()
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("n`tname`tlength`tsha1")
    $lines.Add("#`tassembly`t" + $embedded.FullName + "`ttypes=" + @($embedded.GetTypes()).Count)
    $n = 0
    foreach ($name in $embedded.GetManifestResourceNames()) {
        $stream = $embedded.GetManifestResourceStream($name)
        $data = New-Object byte[] ([int]$stream.Length)
        $read = 0
        while ($read -lt $data.Length) { $got = $stream.Read($data, $read, $data.Length - $read); if ($got -le 0) { break }; $read += $got }
        $stream.Dispose()
        if ($read -ne $data.Length) { throw "short read of $name" }
        [IO.File]::WriteAllBytes((Join-Path $OutDir ("{0}.bin" -f $n)), $data)
        $lines.Add(("{0}`t{1}`t{2}`t{3}" -f $n, $name, $data.Length, [BitConverter]::ToString($sha.ComputeHash($data)).Replace('-', '')))
        $n++
    }
    [IO.File]::WriteAllLines((Join-Path $OutDir 'index.tsv'), $lines, (New-Object Text.UTF8Encoding($false)))
    exit 0
}

$inFull = (Resolve-Path -LiteralPath (Join-Path $Root $In)).Path
$gameDir = (Join-Path $Root 'game') + '\'
if ($inFull.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to open a file under game/. Copy it to work/ first.'
}
$outFull = Join-Path $Root $OutDir
if (Test-Path -LiteralPath $outFull) { Remove-Item -Recurse -Force -LiteralPath $outFull }
New-Item -ItemType Directory -Force $outFull | Out-Null
$ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $ps32)) { throw "32-bit PowerShell not found at $ps32" }
& $ps32 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Stage dump-child -Copy $inFull -OutDir $outFull -EmbeddedAssembly $EmbeddedAssembly
if ($LASTEXITCODE -ne 0) { throw "dump child failed with exit code $LASTEXITCODE" }
$index = Join-Path $outFull 'index.tsv'
if (-not (Test-Path -LiteralPath $index)) { throw 'the child wrote no index' }
$rows = @([IO.File]::ReadAllLines($index) | Where-Object { $_ -match '^\d+\t' })
$total = 0; foreach ($r in $rows) { $total += [int]$r.Split("`t")[2] }
Write-Host ("      {0} resources of the embedded assembly, {1} bytes -> {2}" -f $rows.Count, $total, $outFull)
