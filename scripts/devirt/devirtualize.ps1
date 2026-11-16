<#
.SYNOPSIS
    Writes the CIL of the virtualized methods back in place of their replacement bodies.
    Prints the methods it could not translate, and why.

.DESCRIPTION
    Input: an assembly that still has the replacement bodies (-In, work/deobf/osu!.strings.exe),
    work/vm/stream.bin (dump-stream.ps1) and the table of handlers, docs/vm-opcodes.tsv.
    Handlers are found by metadata token: the script needs no name and runs before the
    renaming steps of the pipeline.

    In the pipeline it is called by scripts/undo-obfuscator.ps1 -Devirtualize with -Module,
    on the module that script has loaded: the methods are written back first, then what the
    obfuscator pasted into them is undone with the rest, and the reference
    (work/deobf/osu!.clean.exe) is written once.

    On its own it writes a trial to work/vm/osu!.devirt.exe (-Out), with the pasted code
    still in. -Show prints the CIL written for one method, by token.

    Encrypted blocks of virtual code (docs/pipeline.md): a
    block whose key is in docs/vm-block-keys.tsv (find-block-keys.ps1) is decrypted, its
    code written in its place and the test that led to it written back as the comparison
    the compiler emits. The fields that held the encrypted image of the keys stay where
    they are, without use: removing rows would renumber the fields after them.
    A block without a key is written as a call to a method without a body (-BlockName),
    created in the type of the method that has the block, followed by a jump to the places
    of the body nothing else reaches.

.EXAMPLE
    .\scripts\devirt\devirtualize.ps1
    .\scripts\devirt\devirtualize.ps1 -Show 0x06001A1B
#>
[CmdletBinding()]
param(
    [string]$In = 'work\deobf\osu!.strings.exe',
    [string]$Out = 'work\vm\osu!.devirt.exe',
    [string]$Table = 'docs\vm-opcodes.tsv',
    [string]$KeyFile = 'docs\vm-block-keys.tsv',
    [string]$Show = '',
    [uint32]$DecryptorToken = 0x06003EA0,
    [uint32]$VmType = 0x02000055,
    [string]$BlockName = 'EncryptedBlock',
    # A module already loaded (with a module context): it is edited in memory, nothing is
    # read from -In nor written to -Out. For scripts/undo-obfuscator.ps1.
    [object]$Module = $null
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib `
    -Path (Join-Path $Root 'scripts\lib\Devirt.cs'), (Join-Path $Root 'scripts\lib\DevirtBlocks.cs'), (Join-Path $Root 'scripts\lib\DevirtResolver.cs'), (Join-Path $Root 'scripts\lib\DevirtEmitter.cs')

$streamPath = Join-Path $Root 'work\vm\stream.bin'
if (-not (Test-Path -LiteralPath $streamPath)) { throw 'No work\vm\stream.bin: run scripts\devirt\dump-stream.ps1 first.' }
$file = New-Object VmStreamFile (, [IO.File]::ReadAllBytes($streamPath))
if ($null -ne $Module) { $mod = $Module }
else {
    $ctx = [dnlib.DotNet.ModuleDef]::CreateModuleContext()
    $mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root $In)), $ctx)
}

# The VM by its tokens (names differ between the outputs): the type, its opcode table
# (the only type whose constructor stores more than a hundred 'newobj' results) and the
# method that pairs each opcode with its handler (the one with the most 'ldftn').
$machine = $mod.ResolveToken($VmType)
$opInfo = @{}; $handlerOf = @{}; $stubs = @{}
$pairing = $null; $most = 0
foreach ($m in $machine.Methods) {
    if (-not $m.HasBody) { continue }
    $n = @($m.Body.Instructions | Where-Object { [string]$_.OpCode.Name -eq 'ldftn' }).Count
    if ($n -gt $most) { $most = $n; $pairing = $m }
}
$field = $null
foreach ($i in $pairing.Body.Instructions) {
    $n = [string]$i.OpCode.Name
    if ($n -eq 'ldfld') { $field = $i.Operand.MDToken.Raw }
    elseif ($n -eq 'ldftn') { $handlerOf[$field] = $i.Operand.MDToken.Raw }
}
foreach ($t in $mod.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = $m.Body.Instructions
        if ($m.IsConstructor -and -not $m.IsStatic -and $ins.Count -gt 800) {
            for ($i = 3; $i -lt $ins.Count; $i++) {
                if ([string]$ins[$i].OpCode.Name -eq 'stfld' -and [string]$ins[$i - 1].OpCode.Name -eq 'newobj' -and $handlerOf.ContainsKey($ins[$i].Operand.MDToken.Raw) -or
                    ([string]$ins[$i].OpCode.Name -eq 'stfld' -and [string]$ins[$i - 1].OpCode.Name -eq 'newobj' -and $ins[$i - 3].IsLdcI4() -and $ins[$i - 2].IsLdcI4() -and $t.Fields.Count -gt 200)) {
                    $opInfo[$ins[$i].Operand.MDToken.Raw] = @($ins[$i - 3].GetLdcI4Value(), $ins[$i - 2].GetLdcI4Value())
                }
            }
        }
        if ($t.MDToken.Raw -eq $VmType -or $t.DeclaringType -and $t.DeclaringType.MDToken.Raw -eq $VmType) { continue }
        for ($i = 1; $i -lt $ins.Count; $i++) {
            $md = $ins[$i].Operand -as [dnlib.DotNet.MethodDef]
            if ($null -eq $md -or $md.DeclaringType.MDToken.Raw -ne $VmType -or $md.MethodSig.Params.Count -ne 3 -or $md.IsStatic) { continue }
            for ($j = $i - 1; $j -ge 0; $j--) { if ([string]$ins[$j].OpCode.Name -eq 'ldstr') { $stubs[$m.MDToken.Raw] = @($m, [string]$ins[$j].Operand); break } }
        }
    }
}
# A handler is known by its token, in the table and here.
foreach ($f in $opInfo.Keys) {
    $op = New-Object VmOpcode
    $op.Field = '0x{0:X8}' -f $f; $op.Id = [int]$opInfo[$f][0]; $op.OperandType = [int]$opInfo[$f][1]
    if ($handlerOf.ContainsKey($f)) { $op.Handler = '0x{0:X8}' -f $handlerOf[$f] }
    $file.Opcodes[$op.Id] = $op
}
$cil = New-Object 'System.Collections.Generic.Dictionary[string,string]'
$column = -1
foreach ($line in [IO.File]::ReadAllLines((Join-Path $Root $Table))) {
    if ($line -eq '' -or $line.StartsWith('#')) { continue }
    $c = $line.Split("`t")
    if ($c[0] -eq 'handler') { $column = [Array]::IndexOf($c, 'token'); continue }
    if ($column -lt 0) { throw "$Table has no 'token' column" }
    if ($c[1] -ne '') { $cil[$c[$column]] = $c[1] }
}
'{0} replacement bodies, {1} virtual opcodes, {2} handlers in the table' -f $stubs.Count, $file.Opcodes.Count, $cil.Count

# Encrypted blocks: the ones that have a key are read decrypted (find-block-keys.ps1).
$blockKeys = Join-Path $Root $KeyFile
if (Test-Path -LiteralPath $blockKeys) {
    $idOf = New-Object 'System.Collections.Generic.Dictionary[uint32,string]'
    foreach ($tok in $stubs.Keys) { $idOf[[uint32]$tok] = [string]$stubs[$tok][1] }
    '{0} keys of encrypted blocks in {1}' -f $file.ReadBlockKeys([IO.File]::ReadAllLines($blockKeys), $idOf), $KeyFile
}

$resolver = New-Object VmResolver $mod, $file
$emitter = New-Object VmEmitter $resolver, $cil
$emitter.DecryptorToken = $DecryptorToken
# A block of virtual code that is still encrypted is written as a call to a method without a
# body, created in the type of the first method that has one (docs/pipeline.md).
$emitter.BlockName = $BlockName
foreach ($tok in $stubs.Keys) { $emitter.Virtualized[[VmStreamFile]::PositionOf($stubs[$tok][1])] = $stubs[$tok][0] }

# Strings: the virtual code calls the string decryptor with ids the IL never held. They are
# decrypted with the harness decrypt-strings.ps1 left in work/strings (same child stage).
$ids = New-Object System.Collections.Generic.HashSet[int]
foreach ($tok in $stubs.Keys) {
    $vm = $file.ReadMethod($stubs[$tok][1])
    for ($k = 1; $k -lt $vm.Instructions.Count; $k++) {
        $call = $vm.Instructions[$k]
        if ($call.Opcode.OperandType -ne 11 -or -not ($vm.Instructions[$k - 1].Operand -is [int])) { continue }
        $o = $null; try { $o = $resolver.Resolve([int]$call.Operand) } catch { }
        $d = $o -as [dnlib.DotNet.MethodDef]
        if ($null -ne $d -and $d.MDToken.Raw -eq $DecryptorToken) { [void]$ids.Add([int]$vm.Instructions[$k - 1].Operand) }
    }
}
if ($ids.Count -gt 0) {
    $harness = Join-Path $Root 'work\strings\harness\osu!.exe'
    if (-not (Test-Path -LiteralPath $harness)) { throw 'No work\strings\harness: run scripts\decrypt-strings.ps1 first.' }
    $idFile = Join-Path $Root 'work\vm\string-ids.txt'
    $raw = Join-Path $Root 'work\vm\strings.raw.tsv'
    [IO.File]::WriteAllLines($idFile, [string[]]($ids | Sort-Object | ForEach-Object { [string]$_ }))
    $ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    & $ps32 -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Root 'scripts\decrypt-strings.ps1') -Stage dump-child -Harness $harness -IdFile $idFile -DumpFile $raw
    if ($LASTEXITCODE -ne 0) { throw "string dump failed with exit code $LASTEXITCODE" }
    $values = New-Object System.Collections.Generic.HashSet[string]
    foreach ($line in [IO.File]::ReadAllLines($raw)) {
        $p = $line.Split("`t")
        if ($p[1] -ne 'OK') { throw "string id $($p[0]) not decrypted: $($p[2])" }
        $value = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($p[2]))
        $emitter.Strings[[int]$p[0]] = $value
        [void]$values.Add($value)
    }
    # The decryptor answers a decoy when it does not like its caller: one value for all ids.
    if ($ids.Count -gt 3 -and $values.Count -eq 1) { throw 'every id decrypts to the same string: decoy' }
    '{0} string ids of the virtual code decrypted' -f $emitter.Strings.Count
}
$done = 0; $failed = 0
$written = New-Object System.Collections.ArrayList
foreach ($tok in ($stubs.Keys | Sort-Object)) {
    $target = $stubs[$tok][0]
    $name = [string]$target.FullName
    try {
        $vm = $file.ReadMethod($stubs[$tok][1])
        $emitter.BlockHost = $target.DeclaringType
        $body = $emitter.Emit($vm, $target)
        $target.Body = $body
        $done++
        [void]$written.Add('0x{0:X8}' -f $tok)
        if ($Show -ne '' -and ('0x{0:X8}' -f $tok) -eq $Show) {
            "== $name"
            foreach ($v in $body.Variables) { '   local {0} {1}' -f $v.Index, $v.Type }
            foreach ($h in $body.ExceptionHandlers) { '   {0} try {1}..{2} handler {3}..{4}' -f $h.HandlerType, $h.TryStart, $h.TryEnd, $h.HandlerStart, $h.HandlerEnd }
            $body.UpdateInstructionOffsets() | Out-Null
            foreach ($i in $body.Instructions) { '   ' + $i.ToString() }
        }
    } catch {
        $failed++
        $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }
        '   0x{0:X8} {1}: {2}' -f $tok, $name, $e.Message
    }
}
'{0} methods written, {1} not translated' -f $done, $failed
if ($emitter.DecryptedBlocks -gt 0) { '{0} encrypted blocks decrypted, the tests that led to them written back ({1} fields left without use)' -f $emitter.DecryptedBlocks, $emitter.KeyFields.Count }
if ($emitter.EncryptedBlocks -gt 0) { '{0} encrypted blocks left as calls to {1}' -f $emitter.EncryptedBlocks, $BlockName }
if ($null -ne $Module) {
    if ($failed -gt 0) { throw "$failed virtualized methods not translated" }
    # For the caller's report: the methods written back.
    [IO.File]::WriteAllLines((Join-Path $Root 'work\vm\devirtualized.txt'), [string[]]($written | Sort-Object))
    return
}
if ($done -gt 0) {
    $opts = New-Object dnlib.DotNet.Writer.ModuleWriterOptions($mod)
    $opts.MetaDataOptions.Flags = $opts.MetaDataOptions.Flags -bor [dnlib.DotNet.Writer.MetaDataFlags]::PreserveAll
    $opts.Logger = [dnlib.DotNet.DummyLogger]::NoThrowInstance
    $mod.Write((Join-Path $Root $Out), $opts)
    "written $Out"
}
$mod.Dispose()
