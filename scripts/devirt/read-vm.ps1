<#
.SYNOPSIS
    Read-only: lists the virtualized methods found in work/vm/stream.bin (dump-stream.ps1),
    their header, their virtual instructions and the references they hold. Writes
    work/vm/opcodes.tsv (the virtual opcodes, their handlers and how often each is used).

.DESCRIPTION
    The reading is done by scripts/lib/Devirt.cs and DevirtResolver.cs, which follow the
    decompiled VM (docs/pipeline.md). References are named as in the original
    binary: they are resolved in work/deobf/osu!.clean.exe, whose tokens are those of every
    other output; types and members are printed with the names of osu!.named.exe.

    When docs/vm-opcodes.tsv exists (handler, CIL instruction), each instruction is printed
    with the CIL instruction its handler stands for.

.EXAMPLE
    .\scripts\devirt\read-vm.ps1                              # one line per method, opcode counts
    .\scripts\devirt\read-vm.ps1 -Bodies -Only 0x0600052F     # its instructions and references
#>
[CmdletBinding()]
param(
    [switch]$Bodies,
    [string]$Only = '',
    # Leave the encrypted blocks as their bytes (without it, the blocks that have a key in
    # docs/vm-block-keys.tsv are printed decrypted, as devirtualize.ps1 reads them).
    [switch]$Encrypted,
    # The assembly that still has the replacement bodies of the virtualized methods.
    [string]$StubsFrom = 'work\deobf\osu!.strings.exe'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib `
    -Path (Join-Path $Root 'scripts\lib\Devirt.cs'), (Join-Path $Root 'scripts\lib\DevirtBlocks.cs'), (Join-Path $Root 'scripts\lib\DevirtResolver.cs')

$streamPath = Join-Path $Root 'work\vm\stream.bin'
if (-not (Test-Path -LiteralPath $streamPath)) { throw 'No work\vm\stream.bin: run scripts\devirt\dump-stream.ps1 first.' }
$file = New-Object VmStreamFile (, [IO.File]::ReadAllBytes($streamPath))

# Names: the opcode table and the handlers are read in the named assembly (same tokens).
$ctx = [dnlib.DotNet.ModuleDef]::CreateModuleContext()
$named = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root 'work\deobf\osu!.named.exe')), $ctx)
$clean = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root 'work\deobf\osu!.clean.exe')), $ctx)
$opInfo = @{}; $handlerOf = @{}; $stubs = @{}
foreach ($t in $named.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = $m.Body.Instructions
        if ($t.FullName -eq 'Obfuscator.VmOpCodes' -and $m.IsConstructor) {
            # ldarg.0; ldc.i4 id; ldc.i4 operand type; newobj VmOpCode; stfld field
            for ($i = 3; $i -lt $ins.Count; $i++) {
                if ([string]$ins[$i].OpCode.Name -eq 'stfld' -and [string]$ins[$i - 1].OpCode.Name -eq 'newobj') {
                    $opInfo[[string]$ins[$i].Operand.Name] = @($ins[$i - 3].GetLdcI4Value(), $ins[$i - 2].GetLdcI4Value())
                }
            }
        }
        elseif ($t.FullName -eq 'Obfuscator.VirtualMachine' -and [string]$m.Name -eq 'CreateHandlerTable') {
            # ... ldfld field; ldnull; ldftn handler; newobj delegate ...
            $field = $null
            foreach ($i in $ins) {
                $n = [string]$i.OpCode.Name
                if ($n -eq 'ldfld') { $field = [string]$i.Operand.Name }
                elseif ($n -eq 'ldftn') { $handlerOf[$field] = [string]$i.Operand.Name }
            }
        }
    }
}
# The replacement bodies ('ldstr id ... call' of an instance method of the VM with three
# parameters) are read where they still are: the reference has the methods themselves.
$vmToken = ($named.Types | Where-Object { $_.FullName -eq 'Obfuscator.VirtualMachine' }).MDToken.Raw
$withStubs = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root $StubsFrom)), $ctx)
foreach ($t in $withStubs.GetTypes()) {
    if ($t.MDToken.Raw -eq $vmToken -or ($t.DeclaringType -and $t.DeclaringType.MDToken.Raw -eq $vmToken)) { continue }
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        $ins = $m.Body.Instructions
        for ($i = 1; $i -lt $ins.Count; $i++) {
            $md = $ins[$i].Operand -as [dnlib.DotNet.MethodDef]
            if ($null -eq $md -or $md.DeclaringType.MDToken.Raw -ne $vmToken -or $md.MethodSig.Params.Count -ne 3 -or $md.IsStatic) { continue }
            for ($j = $i - 1; $j -ge 0; $j--) { if ([string]$ins[$j].OpCode.Name -eq 'ldstr') { $stubs[$m.MDToken.Raw] = @([string]$named.ResolveToken($m.MDToken.Raw).FullName, [string]$ins[$j].Operand); break } }
        }
    }
}
$withStubs.Dispose()
foreach ($f in $opInfo.Keys) {
    $op = New-Object VmOpcode
    $op.Field = $f; $op.Id = [int]$opInfo[$f][0]; $op.OperandType = [int]$opInfo[$f][1]; $op.Handler = $handlerOf[$f]
    $file.Opcodes[$op.Id] = $op
}
$cil = @{}
$tablePath = Join-Path $Root 'docs\vm-opcodes.tsv'
if (Test-Path -LiteralPath $tablePath) {
    foreach ($line in [IO.File]::ReadAllLines($tablePath)) {
        if ($line -eq '' -or $line.StartsWith('#')) { continue }
        $c = $line.Split("`t")
        if ($c.Count -ge 2 -and $c[0] -ne 'handler') { $cil[$c[0]] = $c[1] }
    }
}
'{0} replacement bodies, {1} virtual opcodes, {2} with a handler, {3} handlers in docs\vm-opcodes.tsv' -f $stubs.Count, $opInfo.Count, $handlerOf.Count, $cil.Count

function Show-Name($o) {
    # The same member in the named assembly, by token.
    $p = $o -as [dnlib.DotNet.IMDTokenProvider]
    if ($null -ne $p -and $p.MDToken.Rid -ne 0 -and ($o -is [dnlib.DotNet.TypeDef] -or $o -is [dnlib.DotNet.MethodDef] -or $o -is [dnlib.DotNet.FieldDef])) {
        $n = $named.ResolveToken($p.MDToken.Raw)
        if ($null -ne $n) { return [string]$n.FullName }
    }
    return [string]$o
}
function Show-Reference([int]$id) {
    try { $o = $resolver.Resolve($id) } catch { return '!! ' + $_.Exception.InnerException.Message }
    if ($o -is [string]) { return '"' + $o + '"' }
    if ($o -is [dnlib.DotNet.TypeSig]) {
        $td = [dnlib.DotNet.Extensions]::ToTypeDefOrRef($o)
        if ($td -is [dnlib.DotNet.TypeDef]) { return 'type ' + (Show-Name $td) }
        return 'type ' + $o.FullName
    }
    return Show-Name $o
}

$resolver = New-Object VmResolver $clean, $file
$blockKeys = Join-Path $Root 'docs\vm-block-keys.tsv'
if (-not $Encrypted -and (Test-Path -LiteralPath $blockKeys)) {
    $idOf = New-Object 'System.Collections.Generic.Dictionary[uint32,string]'
    foreach ($tok in $stubs.Keys) { $idOf[[uint32]$tok] = [string]$stubs[$tok][1] }
    '{0} keys of encrypted blocks in docs\vm-block-keys.tsv' -f $file.ReadBlockKeys([IO.File]::ReadAllLines($blockKeys), $idOf)
}
$uses = @{}; $unresolved = 0
foreach ($tok in ($stubs.Keys | Sort-Object)) {
    $m = $file.ReadMethod($stubs[$tok][1])
    foreach ($i in $m.Instructions) { $uses[$i.Opcode.Field] = 1 + [int]$uses[$i.Opcode.Field] }
    if ($Only -ne '' -and ('0x{0:X8}' -f $tok) -ne $Only) { continue }
    '== 0x{0:X8} {1}' -f $tok, $stubs[$tok][0]
    '   at {0}, flags {1:X2}, {2} parameters, {3} locals, {4} handlers, body {5} bytes, {6} instructions{7}' -f `
        $m.Position, $m.Flags, $m.ParameterTypes.Length, $m.Locals.Length, $m.Handlers.Length, $m.Body.Length, $m.Instructions.Count, $(if ($m.Error) { ': ' + $m.Error } else { '' })
    if (-not $Bodies) { continue }
    '   returns {0}' -f (Show-Reference $m.ReturnType)
    for ($i = 0; $i -lt $m.ParameterTypes.Length; $i++) { '   parameter {0}: {1}{2}' -f $i, (Show-Reference $m.ParameterTypes[$i]), $(if ($m.ParameterFlags[$i]) { ' (flag)' } else { '' }) }
    for ($i = 0; $i -lt $m.Locals.Length; $i++) { '   local {0}: {1}' -f $i, (Show-Reference $m.Locals[$i]) }
    foreach ($h in $m.Handlers) {
        '   handler kind {0}: try {1:X4}+{2:X}, handler {3:X4}, filter {4:X4}{5}' -f $h.Kind, $h.TryStart, $h.TryLength, $h.HandlerStart, $h.FilterStart, $(if ($h.Kind -eq 0) { ', catches ' + (Show-Reference $h.CatchType) } else { '' })
    }
    foreach ($i in $m.Instructions) {
        $name = $i.Opcode.Handler
        if ($cil.ContainsKey($name)) { $name = $cil[$name] }
        $text = ''
        if ($i.Operand -is [int[]]) { $text = '[' + (($i.Operand | ForEach-Object { '{0:X4}' -f $_ }) -join ', ') + ']' }
        elseif ($i.Operand -is [byte[]]) { $text = (($i.Operand | ForEach-Object { '{0:X2}' -f $_ }) -join '') }
        elseif ($i.Opcode.OperandType -eq 4) { $text = '{0:X4}' -f $i.Operand }
        elseif ($i.Opcode.OperandType -eq 11) {
            $text = Show-Reference ([int]$i.Operand)
            if ($text.StartsWith('!!')) { $unresolved++ }
        }
        elseif ($null -ne $i.Operand) { $text = [Convert]::ToString($i.Operand, [Globalization.CultureInfo]::InvariantCulture) }
        '   {0:X4}: {1,-14} {2}' -f $i.Offset, $name, $text
    }
}
if ($Bodies -and $unresolved -gt 0) { "$unresolved references not resolved" }

$rows = New-Object System.Collections.Generic.List[string]
$rows.Add("handler`tfield`tid`toperandType`tuses")
foreach ($f in ($opInfo.Keys | Sort-Object { [int]($_ -replace '[^0-9]', '') })) {
    $h = $handlerOf[$f]; if (-not $h) { $h = '-' }
    $rows.Add(("{0}`t{1}`t{2}`t{3}`t{4}" -f $h, $f, $opInfo[$f][0], $opInfo[$f][1], [int]$uses[$f]))
}
[IO.File]::WriteAllLines((Join-Path $Root 'work\vm\opcodes.tsv'), $rows)
if (-not $Bodies) {
    ''
    'virtual opcodes in use: {0} (work\vm\opcodes.tsv)' -f @($uses.Keys).Count
}
$named.Dispose(); $clean.Dispose()
