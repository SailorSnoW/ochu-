<#
.SYNOPSIS
    Finds the keys of the encrypted blocks of the virtual code and writes them to
    docs/vm-block-keys.tsv, which scripts/devirt/devirtualize.ps1 reads.

.DESCRIPTION
    A virtualized method can hold blocks of code that the VM decrypts as it reaches them
    (docs/pipeline.md). The key of a block is a 32-bit value the
    code has at that point and that the binary does not hold: in Score.SubmitWorker, the hash
    the compiler gives a 'switch' on a string (FNV-1a), for the string of one 'case'.

    A key is right when the first four bytes of the block decrypt to the length of the block
    (scripts/lib/DevirtBlocks.cs), and the code then reads as instructions of the VM.

    Candidates: the hash of each word of -Words (answers a score server can give). With
    -Search, every 32-bit value is tried on the blocks that are still without a key, on all
    processors: about two million keys per second and per block on eight cores, forty
    minutes for one block.

    Input: work/vm/stream.bin (dump-stream.ps1) and the assembly that has the replacement
    bodies. Entries already in the table are kept.

.EXAMPLE
    .\scripts\devirt\find-block-keys.ps1
    .\scripts\devirt\find-block-keys.ps1 -Words pending, locked
    .\scripts\devirt\find-block-keys.ps1 -Search
#>
[CmdletBinding()]
param(
    [string]$In = 'work\deobf\osu!.strings.exe',
    [string]$KeyFile = 'docs\vm-block-keys.tsv',
    [uint32]$VmType = 0x02000055,
    [string[]]$Words = @(
        'reset', 'verify', 'nouser', 'pass', 'bancho', 'inactive', 'ban', 'beatmap', 'disabled', 'oldver', 'no',
        'unranked', 'missinginfo', 'checksum', 'dup', 'invalid', 'banned', 'maintenance', 'wrongver', 'version',
        'locked', 'restricted', 'needverify', 'verification', 'fail', 'error', 'unknown', 'mismatch', 'replay',
        'hash', 'time', 'mods', 'user', 'password', 'session', 'expired', 'limit', 'toomany', 'flagged', 'token',
        'timeout', 'outdated', 'update', 'needupdate', 'unverified', 'old', 'badreplay', 'security', 'ok',
        'success', 'retry', 'notsubmitted', 'nosupporter', 'supporter', 'needsupporter', 'ratelimit',
        'duplicate', 'wait', 'cooldown', 'server', 'offline', 'busy', 'frozen', 'silence', 'silenced',
        'login', 'auth', 'connect', 'connection', 'needlogin', 'nologin', 'online', 'presence'
    ),
    [switch]$Search,
    [uint32]$From = 0,
    [uint32]$To = [uint32]::MaxValue
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
$options = New-Object System.CodeDom.Compiler.CompilerParameters
$options.CompilerOptions = '/optimize+ /nowarn:1685'
$options.GenerateInMemory = $true
foreach ($r in 'System.dll', 'System.Core.dll', $dnlib) { [void]$options.ReferencedAssemblies.Add($r) }
Add-Type -CompilerParameters $options -Path (Join-Path $Root 'scripts\lib\Devirt.cs'), (Join-Path $Root 'scripts\lib\DevirtBlocks.cs')

$streamPath = Join-Path $Root 'work\vm\stream.bin'
if (-not (Test-Path -LiteralPath $streamPath)) { throw 'No work\vm\stream.bin: run scripts\devirt\dump-stream.ps1 first.' }
$file = New-Object VmStreamFile (, [IO.File]::ReadAllBytes($streamPath))
$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root $In)), [dnlib.DotNet.ModuleDef]::CreateModuleContext())

# What splitting a body needs: the id and the operand type of each opcode of the VM (the
# constructor that fills a type of more than two hundred fields with
# 'ldc.i4 id; ldc.i4 operand type; newobj; stfld'), and the string each replacement body
# gives the VM (devirtualize.ps1 reads both the same way).
$stubs = @{}
foreach ($t in $mod.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = $m.Body.Instructions
        if ($m.IsConstructor -and -not $m.IsStatic -and $ins.Count -gt 800 -and $t.Fields.Count -gt 200) {
            for ($i = 3; $i -lt $ins.Count; $i++) {
                if ([string]$ins[$i].OpCode.Name -eq 'stfld' -and [string]$ins[$i - 1].OpCode.Name -eq 'newobj' -and $ins[$i - 3].IsLdcI4() -and $ins[$i - 2].IsLdcI4()) {
                    $op = New-Object VmOpcode
                    $op.Field = '0x{0:X8}' -f $ins[$i].Operand.MDToken.Raw; $op.Id = $ins[$i - 3].GetLdcI4Value(); $op.OperandType = $ins[$i - 2].GetLdcI4Value()
                    $file.Opcodes[$op.Id] = $op
                }
            }
        }
        if ($t.MDToken.Raw -eq $VmType -or $t.DeclaringType -and $t.DeclaringType.MDToken.Raw -eq $VmType) { continue }
        for ($i = 1; $i -lt $ins.Count; $i++) {
            $md = $ins[$i].Operand -as [dnlib.DotNet.MethodDef]
            if ($null -eq $md -or $md.DeclaringType.MDToken.Raw -ne $VmType -or $md.MethodSig.Params.Count -ne 3 -or $md.IsStatic) { continue }
            for ($j = $i - 1; $j -ge 0; $j--) { if ([string]$ins[$j].OpCode.Name -eq 'ldstr') { $stubs[$m.MDToken.Raw] = [string]$ins[$j].Operand; break } }
        }
    }
}
$mod.Dispose()
if ($file.Opcodes.Count -lt 200 -or $stubs.Count -eq 0) { throw "the VM was not found in $In ($($file.Opcodes.Count) opcodes, $($stubs.Count) replacement bodies)" }

# The table as it is: 'method|block' -> key, string.
$keyPath = Join-Path $Root $KeyFile
$known = @{}
if (Test-Path -LiteralPath $keyPath) {
    foreach ($line in [IO.File]::ReadAllLines($keyPath)) {
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        $c = $line.Split("`t")
        if ($c[0] -eq 'method') { continue }
        $known[$c[0] + '|' + $c[1]] = @($c[2], $c[3])
    }
}

# The blocks, read without any key: each is one instruction that holds its bytes, after
# 'block id, key, salt, open'.
$blocks = New-Object System.Collections.ArrayList
foreach ($tok in ($stubs.Keys | Sort-Object)) {
    $vm = $file.ReadMethod($stubs[$tok])
    if ($vm.Error) { throw ('0x{0:X8}: {1}' -f $tok, $vm.Error) }
    for ($k = 3; $k -lt $vm.Instructions.Count; $k++) {
        $i = $vm.Instructions[$k]
        if ($i.Opcode -ne [VmStreamFile]::EncryptedBlock) { continue }
        $salt = $vm.Instructions[$k - 2].Operand
        if ($salt -isnot [long]) { throw ('0x{0:X8} {1:X4}: no salt before the block' -f $tok, $i.Offset) }
        [void]$blocks.Add([pscustomobject]@{ Name = ('0x{0:X8}|{1:X4}' -f $tok, $i.Offset); Bytes = [byte[]]$i.Operand; Salt = [long]$salt })
    }
}
'{0} encrypted blocks in {1} virtualized methods, {2} keys in {3}' -f $blocks.Count, $stubs.Count, $known.Count, $KeyFile

function Test-Key($block, [uint32]$key) {
    # The length is right: the code must also split into instructions of the VM.
    $code = [VmBlockCipher]::Decrypt($block.Bytes, $key, $block.Salt)
    if ($null -eq $code) { return $false }
    $probe = New-Object VmMethod
    $list = New-Object 'System.Collections.Generic.List[VmInstruction]'
    try { $file.Split($probe, $code, 0, $list) } catch { return $false }
    return -not $probe.Error
}

$found = 0
foreach ($b in $blocks) {
    if ($known.ContainsKey($b.Name)) { continue }
    foreach ($w in $Words) {
        $h = [VmBlockCipher]::StringHash($w)
        if (Test-Key $b $h) { $known[$b.Name] = @(('0x{0:X8}' -f $h), $w); $found++; break }
    }
}
if ($Search) {
    $left = @($blocks | Where-Object { -not $known.ContainsKey($_.Name) })
    if ($left.Count -gt 0) {
        $bytes = [byte[][]]@($left | ForEach-Object { , $_.Bytes })
        $salts = [long[]]@($left | ForEach-Object { $_.Salt })
        $slice = [uint64]1 -shl 26
        $watch = [Diagnostics.Stopwatch]::StartNew()
        for ($start = [uint64]$From; $start -le $To; $start += $slice) {
            $end = [Math]::Min([uint64]$To, $start + $slice - 1)
            foreach ($hit in [VmBlockCipher]::Search($bytes, $salts, [uint32]$start, [uint32]$end, $null)) {
                $b = $left[$hit.Key]
                if (-not $known.ContainsKey($b.Name) -and (Test-Key $b $hit.Value)) { $known[$b.Name] = @(('0x{0:X8}' -f $hit.Value), ''); $found++ }
            }
            Write-Host ('   searched up to {0:X8}, {1:N0} s' -f $end, $watch.Elapsed.TotalSeconds)
            if (@($left | Where-Object { -not $known.ContainsKey($_.Name) }).Count -eq 0) { break }
        }
    }
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Keys of the encrypted blocks of the virtual code (docs/pipeline.md).')
$lines.Add('# Written by scripts/devirt/find-block-keys.ps1: never by hand.')
$lines.Add('# method: token of the virtualized method. block: offset of the encrypted bytes in its body.')
$lines.Add('# key: the 32-bit value that decrypts the block. string: the text whose hash is the key (FNV-1a,')
$lines.Add("# the hash the compiler gives a 'switch' on a string); empty for a key found by search.")
$lines.Add("method`tblock`tkey`tstring")
foreach ($name in ($known.Keys | Sort-Object)) {
    $p = $name.Split('|')
    $lines.Add(("{0}`t{1}`t{2}`t{3}" -f $p[0], $p[1], $known[$name][0], $known[$name][1]))
}
[IO.File]::WriteAllLines($keyPath, $lines, (New-Object Text.UTF8Encoding($false)))
'{0} keys found, {1} in {2}' -f $found, $known.Count, $KeyFile
$missing = @($blocks | Where-Object { -not $known.ContainsKey($_.Name) })
foreach ($b in $missing) { '   no key for {0}' -f $b.Name }
if ($missing.Count -gt 0 -and -not $Search) { '   try other -Words, or -Search' }
