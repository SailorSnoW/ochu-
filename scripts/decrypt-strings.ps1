<#
.SYNOPSIS
    Recovers the encrypted string table of osu!.exe and writes a copy with plain ldstr.

.DESCRIPTION
    The Eazfuscator string decryptor (token 0x06003EA0, string(int)) inspects its
    caller through StackTrace and returns a decoy ("X0X") when called from another
    assembly or a DynamicMethod. So:

      1. harness : copy of the exe with one extra static method on <Module>
                   that forwards to the decryptor.
      2. dump    : a 32-bit PowerShell child loads the harness (LoadFile, the
                   entry point is never run) and calls the forwarder for every id
                   found in the IL.
      3. patch   : "ldc.i4 id; call decryptor" becomes "ldstr value" in a fresh
                   copy of the original. Metadata tokens are preserved. This copy
                   is the IL reference of scripts/match.ps1.

    Stage 2 executes obfuscator runtime code from the game binary, locally.
    Everything is read from and written to work/. game/ is never touched.

.EXAMPLE
    .\scripts\decrypt-strings.ps1
#>
[CmdletBinding()]
param(
    [string]$In = 'work\triage\in\osu!.exe',
    [string]$OutDir = 'work\strings',
    [uint32]$DecryptorToken = 0x06003EA0,
    [ValidateSet('all', 'dump-child')][string]$Stage = 'all',
    [string]$Harness,
    [string]$IdFile,
    [string]$DumpFile
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$ForwarderName = 'OsuDecomp_Forward'

if ($Stage -eq 'dump-child') {
    # Runs inside 32-bit PowerShell. Loads the harness and calls the forwarder.
    if ([IntPtr]::Size -ne 4) { throw 'dump-child must run in a 32-bit process' }
    $asm = [Reflection.Assembly]::LoadFile($Harness)
    $flags = [Reflection.BindingFlags]'Static,Public,NonPublic,DeclaredOnly'
    $fwd = $asm.ManifestModule.GetMethods($flags) | Where-Object { $_.Name -eq $ForwarderName } | Select-Object -First 1
    if (-not $fwd) { throw 'forwarder not found in harness' }
    $sw = New-Object IO.StreamWriter($DumpFile, $false, (New-Object Text.UTF8Encoding($false)))
    try {
        foreach ($line in [IO.File]::ReadAllLines($IdFile)) {
            $id = [int]$line
            $args1 = New-Object object[] 1
            $args1[0] = $id
            try {
                $s = [string]$fwd.Invoke($null, $args1)
                $sw.WriteLine(("{0}`tOK`t{1}" -f $id, [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($s))))
            } catch {
                $msg = $_.Exception.GetBaseException().GetType().FullName
                $sw.WriteLine(("{0}`tERR`t{1}" -f $id, $msg))
            }
        }
    } finally { $sw.Dispose() }
    exit 0
}

$inFull = (Resolve-Path -LiteralPath (Join-Path $Root $In)).Path
$gameDir = (Join-Path $Root 'game') + '\'
if ($inFull.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to open a file under game/. Copy it to work/ first.'
}
$outFull = Join-Path $Root $OutDir
$harnessDir = Join-Path $outFull 'harness'
New-Item -ItemType Directory -Force $harnessDir | Out-Null
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))

function Open-Module { return [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($inFull)) }
function Save-Module($mod, [string]$path) {
    $opts = New-Object dnlib.DotNet.Writer.ModuleWriterOptions($mod)
    $opts.MetaDataOptions.Flags = $opts.MetaDataOptions.Flags -bor [dnlib.DotNet.Writer.MetaDataFlags]::PreserveAll
    $opts.Logger = [dnlib.DotNet.DummyLogger]::NoThrowInstance
    $mod.Write($path, $opts)
}
function Find-Sites($mod) {
    # Returns objects: Method, Index (of the call), Id (or $null when the argument is not a constant)
    $sites = New-Object System.Collections.ArrayList
    foreach ($t in $mod.GetTypes()) {
        foreach ($m in $t.Methods) {
            if (-not $m.HasBody) { continue }
            $ins = $m.Body.Instructions
            for ($i = 0; $i -lt $ins.Count; $i++) {
                $md = $ins[$i].Operand -as [dnlib.DotNet.MethodDef]
                if (-not $md -or $md.MDToken.Raw -ne $DecryptorToken) { continue }
                $id = $null
                if ($i -gt 0 -and $ins[$i - 1].IsLdcI4()) { $id = $ins[$i - 1].GetLdcI4Value() }
                [void]$sites.Add((New-Object psobject -Property @{ Method = $m; Index = $i; Id = $id }))
            }
        }
    }
    return ,$sites
}

# ---- 1. harness -------------------------------------------------------------
Write-Host '[1/3] building harness'
$mod = Open-Module
$dec = $mod.ResolveToken($DecryptorToken) -as [dnlib.DotNet.MethodDef]
if (-not $dec) { throw 'decryptor token does not resolve to a method' }

$sites = Find-Sites $mod
$ids = @($sites | Where-Object { $null -ne $_.Id } | ForEach-Object { $_.Id } | Sort-Object -Unique)
$nonConst = @($sites | Where-Object { $null -eq $_.Id }).Count
Write-Host ("      call sites: {0}, distinct ids: {1}, non-constant argument: {2}" -f $sites.Count, $ids.Count, $nonConst)

$sig = [dnlib.DotNet.MethodSig]::CreateStatic($mod.CorLibTypes.String, $mod.CorLibTypes.Int32)
$fwd = New-Object dnlib.DotNet.MethodDefUser(
    [dnlib.DotNet.UTF8String]$ForwarderName, $sig,
    ([dnlib.DotNet.MethodImplAttributes]'IL,Managed,NoInlining'),
    ([dnlib.DotNet.MethodAttributes]'Public,Static,HideBySig'))
$fwd.ParamDefs.Add((New-Object dnlib.DotNet.ParamDefUser([dnlib.DotNet.UTF8String]'id', 1)))
$body = New-Object dnlib.DotNet.Emit.CilBody
$body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ldarg_0))
$body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Call, [dnlib.DotNet.IMethod]$dec))
$body.Instructions.Add([dnlib.DotNet.Emit.Instruction]::Create([dnlib.DotNet.Emit.OpCodes]::Ret))
$fwd.Body = $body
# Must NOT live in the decryptor's own type: its static constructor records who
# triggered it, and a reflection-triggered initialisation locks the decoy path.
$mod.GlobalType.Methods.Add($fwd)

$harnessPath = Join-Path $harnessDir 'osu!.exe'
Save-Module $mod $harnessPath
$mod.Dispose()

$idFilePath = Join-Path $outFull 'ids.txt'
[IO.File]::WriteAllLines($idFilePath, [string[]]($ids | ForEach-Object { [string]$_ }))

# ---- 2. dump (32-bit child) -------------------------------------------------
Write-Host '[2/3] dumping strings in a 32-bit child process'
$ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path $ps32)) { throw "32-bit PowerShell not found at $ps32" }
$rawPath = Join-Path $outFull 'strings.raw.tsv'
if (Test-Path -LiteralPath $rawPath) { Remove-Item -LiteralPath $rawPath }
& $ps32 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Stage dump-child -Harness $harnessPath -IdFile $idFilePath -DumpFile $rawPath
if ($LASTEXITCODE -ne 0) { throw "dump child failed with exit code $LASTEXITCODE" }

$map = @{}
$errors = 0
# Generic dictionary: a hashtable would be case-insensitive and shadow .Count with a "Count" key.
$tally = New-Object 'System.Collections.Generic.Dictionary[string,int]'
$readable = New-Object System.Collections.Generic.List[string]
foreach ($line in [IO.File]::ReadAllLines($rawPath)) {
    $p = $line.Split("`t")
    if ($p[1] -ne 'OK') { $errors++; continue }
    $s = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p[2]))
    $map[[int]$p[0]] = $s
    $c = 0
    [void]$tally.TryGetValue($s, [ref]$c)
    $tally[$s] = $c + 1
    $esc = $s.Replace('\', '\\').Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')
    $readable.Add(("{0}`t{1}" -f $p[0], $esc))
}
[IO.File]::WriteAllLines((Join-Path $outFull 'strings.txt'), $readable, (New-Object Text.UTF8Encoding($false)))
$worst = $tally.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 1
Write-Host ("      decrypted: {0}, errors: {1}, distinct values: {2}" -f $map.Count, $errors, $tally.Count)
Write-Host ("      most frequent value: '{0}' x{1}" -f $worst.Key, $worst.Value)
if ($map.Count -eq 0 -or ($worst.Value -gt ($map.Count / 2))) {
    throw 'Output looks like a decoy (one value dominates). Not patching.'
}

# ---- 3. patch ---------------------------------------------------------------
Write-Host '[3/3] writing patched copy'
$mod = Open-Module
$patched = 0; $skipped = 0; $keptNop = 0
# The call is first turned into a nop (indexes from Find-Sites stay valid), then the nops
# are removed per method so that the body is what the compiler emitted: a single ldstr.
$dead = @{}
foreach ($s in (Find-Sites $mod)) {
    if ($null -eq $s.Id -or -not $map.ContainsKey([int]$s.Id)) { $skipped++; continue }
    $ins = $s.Method.Body.Instructions
    $ins[$s.Index - 1].OpCode = [dnlib.DotNet.Emit.OpCodes]::Ldstr
    $ins[$s.Index - 1].Operand = [string]$map[[int]$s.Id]
    $ins[$s.Index].OpCode = [dnlib.DotNet.Emit.OpCodes]::Nop
    $ins[$s.Index].Operand = $null
    $tok = $s.Method.MDToken.Raw
    if (-not $dead.ContainsKey($tok)) { $dead[$tok] = @{ Method = $s.Method; List = (New-Object System.Collections.ArrayList) } }
    [void]$dead[$tok].List.Add($ins[$s.Index])
    $patched++
}
foreach ($e in $dead.Values) {
    $body = $e.Method.Body
    # A nop that is a branch target or an exception handler boundary stays.
    $pinned = New-Object 'System.Collections.Generic.HashSet[object]'
    foreach ($i in $body.Instructions) {
        if ($i.Operand -is [dnlib.DotNet.Emit.Instruction]) { [void]$pinned.Add($i.Operand) }
        elseif ($i.Operand -is [dnlib.DotNet.Emit.Instruction[]]) { foreach ($t in $i.Operand) { [void]$pinned.Add($t) } }
    }
    foreach ($eh in $body.ExceptionHandlers) {
        foreach ($b in $eh.TryStart, $eh.TryEnd, $eh.HandlerStart, $eh.HandlerEnd, $eh.FilterStart) {
            if ($null -ne $b) { [void]$pinned.Add($b) }
        }
    }
    foreach ($nop in $e.List) {
        if ($pinned.Contains($nop)) { $keptNop++ } else { [void]$body.Instructions.Remove($nop) }
    }
}
$deobfDir = Join-Path $Root 'work\deobf'
New-Item -ItemType Directory -Force $deobfDir | Out-Null
$patchedPath = Join-Path $deobfDir 'osu!.strings.exe'
Save-Module $mod $patchedPath
$mod.Dispose()
Write-Host ("      patched sites: {0}, left untouched: {1}, nop kept (referenced): {2}" -f $patched, $skipped, $keptNop)
Write-Host ("      -> {0}" -f $patchedPath)
