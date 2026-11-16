<#
.SYNOPSIS
    Writes the decrypted stream of the obfuscator's virtual machine to work/vm/stream.bin.

.DESCRIPTION
    The 27 virtualized methods have their code in an embedded resource, encrypted. The VM's
    own loader (VirtualMachineFactory.GetCodeStream, token 0x0600223F) is called by reflection on
    a copy of work/deobf/osu!.strings.exe, in a 32-bit PowerShell (the assembly is x86). That
    file has its strings in clear, so the string decryptor and its caller checks are not
    involved. Code of the game that runs: the module initializer and the loader
    (resource decryption). The entry point of the game never runs; nothing under game/ is
    opened.

.EXAMPLE
    .\scripts\devirt\dump-stream.ps1
#>
[CmdletBinding()]
param(
    [string]$In = 'work\deobf\osu!.strings.exe',
    [uint32]$LoaderToken = 0x0600223F,
    [switch]$Inner
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir = Join-Path $Root 'work\vm'
if (-not $Inner) {
    $full = (Resolve-Path -LiteralPath (Join-Path $Root $In)).Path
    if ($full.StartsWith((Join-Path $Root 'game') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to load a file under game/. Copy it to work/ first.' }
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path $ps32)) { throw "32-bit PowerShell not found at $ps32" }
    & $ps32 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Inner -In $In -LoaderToken $LoaderToken
    if ($LASTEXITCODE -ne 0) { throw "the 32-bit child failed with exit code $LASTEXITCODE" }
    return
}

# 32-bit child: load a copy (never the file the pipeline reads) and call the loader.
if ([IntPtr]::Size -ne 4) { throw 'the child must run in a 32-bit process' }
$copy = Join-Path $outDir 'host.exe'
Copy-Item -LiteralPath (Join-Path $Root $In) -Destination $copy -Force
$asm = [Reflection.Assembly]::LoadFile($copy)
$method = $asm.ManifestModule.ResolveMethod([int]$LoaderToken)
$stream = $method.Invoke($null, @())
$ms = New-Object IO.MemoryStream
$buf = New-Object byte[] 65536
$stream.Position = 0
while (($n = $stream.Read($buf, 0, $buf.Length)) -gt 0) { $ms.Write($buf, 0, $n) }
if ($ms.Length -eq 0) { throw 'the loader returned an empty stream' }
[IO.File]::WriteAllBytes((Join-Path $outDir 'stream.bin'), $ms.ToArray())
'      VM stream: {0} bytes -> work\vm\stream.bin' -f $ms.Length
exit 0
