<#
.SYNOPSIS
    Undoes, in the IL reference, code transformations the obfuscator applied after
    compilation. See docs/matching.md.

.DESCRIPTION
    Input  : work/deobf/osu!.strings.exe (original IL, strings decrypted)
    Output : work/deobf/osu!.clean.exe   (the IL reference of scripts/match.ps1)

    Existing metadata tokens are preserved; restored methods get new tokens after the
    last one. What is undone (scripts/lib/UndoInlining.cs):

    - mods-test: 'stloc ; stloc ; ldloc ; ldloc ; and ; ldc.i4.0 ; cgt' on two dedicated
      locals becomes a call to a restored static method bool (Mods, Mods);
    - mods-test-overload: 'stloc ; ldsfld ; ldloc' on a dedicated local in front of that
      call becomes a call to a restored one-argument overload;
    - md5: the MD5 code pasted wherever CryptoHelper's lock field is taken, with its
      locals, its two exception handlers and the parked caller stack, becomes a call to
      a restored static method, one per distinct body (string (byte[]), byte[] (byte[]));
    - md5-overload: 'stloc ; ldsfld ; ldloc ; callvirt GetBytes' in front of such a call
      becomes a call to a restored overload taking a string;
    - player-check: the score check of Player pasted in three methods, 'this' being a
      local, becomes a call to a restored instance method void (bool);
    - self: an instance method pasted once into a method of its own type, after
      'ldarg.0 ; stloc X', becomes a call to a restored instance method void ();
    - parked: a static method with protected blocks pasted in the middle of an expression,
      the caller's stack parked in locals around it, becomes a call to a restored static
      method, one per signature (the WMI property read of BanchoClient; with
      -Devirtualize, the AES encryption and the machine identifiers of Score.SubmitWorker);
    - dropped: 'pop ; pop ; pop' after three values, what pasting an empty static method
      leaves of a call, becomes a call to a restored empty static method;
    - literal: 'ldstr ; ... ; callvirt String::M', a literal called as the result of a call
      is, becomes a call to a restored static method without argument that returns it;
    - const-mutation: 'ldc.i4 K ; not ; neg ; not ...' in the obfuscator's own runtime
      becomes the constant it computes, edited in place;
    - method-handle: 'ldtoken <method> ; call GetMethodFromHandle ; castclass MethodInfo'
      becomes the lookup by name it replaced, 'typeof(T).GetMethod("Name")', with the name
      docs/names.tsv gives the method (run the pipeline from 'clean' after renaming it);
    - resources: the managed resources of the game, which the obfuscator moved into an
      assembly it embeds encrypted, come back into the module in place of that assembly
      (scripts/extract-resources.ps1 reads them through the binary's own resolver);
    - vm-stub: in the stubs of virtualised methods, 'call' on the interpreter becomes
      'callvirt' and 'unbox ; ldobj' becomes 'unbox.any', as a C# compiler writes them
      (without -Devirtualize only: with it the stubs are replaced by the methods).

    With -Devirtualize (the pipeline), the virtualized methods are first written back as
    CIL in the same module (scripts/devirt/devirtualize.ps1, docs/pipeline.md):
    the obfuscator pasted methods into the virtual code as well.

    The list of rewritten methods goes to work/deobf/undo-obfuscator.tsv.

.EXAMPLE
    .\scripts\undo-obfuscator.ps1 -Devirtualize
#>
[CmdletBinding()]
param(
    [string]$In = 'work\deobf\osu!.strings.exe',
    [string]$Out = 'work\deobf\osu!.clean.exe',
    [string]$Report = 'work\deobf\undo-obfuscator.tsv',
    # Where the inlined mods test goes back: ModManager. The binary no longer says which
    # type declared it; this is the type that owns the mod state (docs/matching.md).
    [uint32]$ModsTestHost = 0x02000287,
    [string]$ModsTestName = 'CheckActive',
    # The static MD5 instance of CryptoHelper: the inlined code locks on it, and its type
    # gets the method back.
    [uint32]$Md5LockField = 0x040015DE,
    # Name of each inlined body, by its return type.
    [hashtable]$Md5Names = @{ 'System.String' = 'GetMd5String'; 'System.Byte[]' = 'GetMd5ByteArray' },
    # The counter that the inlined score check of Player increments first: its copies
    # start at 'ldloc X ; ldloc X ; ldfld' on this field. Player gets the method back.
    [uint32]$PlayerCheckField = 0x04000A99,
    [string]$PlayerCheckName = 'HaxCheck',
    # Instance methods pasted once into a method of their own type ('ldarg.0 ; stloc X',
    # X standing for 'this'): a field each one reaches through X -> its name. In Player:
    # the audio clock against the game clock, and the time since the last frame.
    [hashtable]$SelfNames = @{ 0x04000A91 = 'CheckTimingError'; 0x04000A7F = 'CheckFrameLag' },
    # The static methods pasted around a parked stack, 'signature=host type:name', in the
    # order they are restored: the type that gets each one back is the type of a caller
    # (the binary does not say where they were declared). The first reads the first
    # non-null property of a list from the instances of a WMI class
    # (BanchoClient.InitializeClientHash, Score.SubmitWorker).
    [string[]]$Parked = @(
        'System.String(System.String,System.String[])=0x0200062A:GetWmiProperty'
    ),
    # The ones that are only in the virtualized code (-Devirtualize), both in
    # Score.SubmitWorker: AES encryption of a form field (three copies), and the two
    # identifiers of the machine sent as 'c1' (one copy).
    [string[]]$ParkedInVirtualCode = @(
        'System.String(System.String,System.Byte[],System.String&)=0x020008C1:EncryptString',
        'System.String()=0x020008C1:CreateUniqueId'
    ),
    # The method that holds the one call to an empty method (three values pushed, then
    # dropped), the type that gets the method back (the caller's own) and its name.
    [uint32]$DroppedCaller = 0x06002E02,
    [uint32]$DroppedHost = 0x0200062A,
    [string]$DroppedName = 'EmptyCall',
    # The methods that call a string literal with 'callvirt', and the name of the method
    # that returned the literal (a choice: nothing is left of it but the literal).
    [hashtable]$LiteralGetters = @{ 0x06003A3A = 'GetIgnoredFrames' },
    # The interpreter of the virtualised methods: their stubs call it.
    [uint32]$VmType = 0x02000055,
    # The resources scripts/extract-resources.ps1 read in the embedded assembly, and the
    # resource of the module that holds that assembly (the resolver's table names it).
    [string]$ResourcesDir = 'work\resources\raw',
    [string]$EmbeddedAssemblyResource = '4e422e1ecc2d4ca278fc67948614a11f',
    # The names of the source, by token: a method looked up by name is looked up by this name.
    [string]$NamesFile = 'docs\names.tsv',
    # First write the virtualized methods back as CIL (scripts/devirt/devirtualize.ps1, from
    # work/vm/stream.bin): what the obfuscator pasted into them is then undone with the rest.
    [switch]$Devirtualize
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$inFull = (Resolve-Path -LiteralPath (Join-Path $Root $In)).Path
$gameDir = (Join-Path $Root 'game') + '\'
if ($inFull.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to open a file under game/. Copy it to work/ first.'
}
$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
[void][Reflection.Assembly]::LoadFrom($dnlib)
# dnlib redefines ExtensionAttribute: the compiler warning is expected.
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib -Path (Join-Path $PSScriptRoot 'lib\UndoInlining.cs')

$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($inFull), [dnlib.DotNet.ModuleDef]::CreateModuleContext())
$before = $mod.TablesStream.MethodTable.Rows

# The virtualized methods first, in the same module: one write. An encrypted block that has
# no key in docs/vm-block-keys.tsv gets a stand-in, one new method for the type that has the
# block (none today: every block has its key). The methods restored below keep the tokens
# they have without it as long as that type comes after theirs (new rows go by type).
$standIns = 0
$devirtualized = @()
if ($Devirtualize) {
    function Measure-Methods { $n = 0; foreach ($t in $mod.GetTypes()) { $n += $t.Methods.Count }; return $n }
    $had = Measure-Methods
    & (Join-Path $PSScriptRoot 'devirt\devirtualize.ps1') -Module $mod -VmType $VmType | ForEach-Object { Write-Host $_ }
    $standIns = (Measure-Methods) - $had
    $devirtualized = @([IO.File]::ReadAllLines((Join-Path $Root 'work\vm\devirtualized.txt')))
}

$undo = New-Object UndoInlining($mod)
$test = $undo.UndoTest('mods-test', $ModsTestHost, $ModsTestName)
$undo.UndoWrappers('mods-test-overload', $test, $ModsTestName)
$names = New-Object 'System.Collections.Generic.Dictionary[string,string]'
foreach ($k in $Md5Names.Keys) { $names[$k] = $Md5Names[$k] }
foreach ($md5 in $undo.UndoBodies('md5', $Md5LockField, $names)) {
    $undo.UndoWrappers('md5-overload', $md5, [string]$md5.Name)
}
[void]$undo.UndoShared('player-check', $PlayerCheckField, $PlayerCheckName)
$byField = New-Object 'System.Collections.Generic.Dictionary[uint32,string]'
foreach ($k in $SelfNames.Keys) { $byField[[uint32]$k] = $SelfNames[$k] }
[void]$undo.UndoSelf('self', $byField)
# Last of the restored methods: the tokens of the ones above do not move.
$parkedOrder = New-Object 'System.Collections.Generic.List[string]'
$parkedHosts = New-Object 'System.Collections.Generic.Dictionary[string,uint32]'
$parkedNames = New-Object 'System.Collections.Generic.Dictionary[string,string]'
$parkedAll = @($Parked)
if ($Devirtualize) { $parkedAll += @($ParkedInVirtualCode) }
foreach ($entry in $parkedAll) {
    if ($entry -notmatch '^(.+)=(0x[0-9A-Fa-f]{8}):(\w+)$') { throw "not 'signature=host type:name': $entry" }
    $parkedOrder.Add($Matches[1]); $parkedHosts[$Matches[1]] = [Convert]::ToUInt32($Matches[2], 16); $parkedNames[$Matches[1]] = $Matches[3]
}
[void]$undo.UndoParked('parked', $parkedOrder, $parkedHosts, $parkedNames)
[void]$undo.UndoDropped('dropped', $DroppedCaller, $DroppedHost, $DroppedName, 3)
foreach ($left in $undo.FindDropped(3)) { $undo.Problems.Add('values pushed and dropped left in ' + $left) }
foreach ($left in $undo.FindParked()) { $undo.Problems.Add('pasted body around a parked stack left in ' + $left) }
$getters = New-Object 'System.Collections.Generic.Dictionary[uint32,string]'
foreach ($k in $LiteralGetters.Keys) { $getters[[uint32]$k] = $LiteralGetters[$k] }
[void]$undo.UndoLiteralReceivers('literal', $getters)
$undo.UndoConstantMutation('const-mutation')
$sourceNames = New-Object 'System.Collections.Generic.Dictionary[uint32,string]'
foreach ($line in [IO.File]::ReadAllLines((Join-Path $Root $NamesFile))) {
    if ($line -match '^(0x06[0-9A-Fa-f]{6})\t([^\t]+)') { $sourceNames[[Convert]::ToUInt32($Matches[1], 16)] = $Matches[2] }
}
$undo.UndoMethodHandles('method-handle', $sourceNames)
# The stubs are gone once the virtualized methods are written back.
if (-not $Devirtualize) { $undo.UndoStubs('vm-stub', $VmType) }
if ($undo.Problems.Count -gt 0) {
    foreach ($p in $undo.Problems) { Write-Host ("  " + $p) }
    throw 'some sites cannot be undone safely, nothing written'
}
$added = $undo.Results.Count

# The resources of the game back in the module. The compiler embeds a resource as public;
# what visibility the embedded assembly gave them cannot be read from a loaded assembly.
$resourceIndex = Join-Path (Join-Path $Root $ResourcesDir) 'index.tsv'
if (-not (Test-Path -LiteralPath $resourceIndex)) { throw "no ${resourceIndex}: run scripts/extract-resources.ps1" }
$holder = @($mod.Resources | Where-Object { [string]$_.Name -eq $EmbeddedAssemblyResource })
if ($holder.Count -ne 1) { throw "resource '$EmbeddedAssemblyResource' (the embedded assembly) not found in the module" }
[void]$mod.Resources.Remove($holder[0])
$resourcesBack = 0
foreach ($line in [IO.File]::ReadAllLines($resourceIndex)) {
    $c = $line.Split("`t")
    if ($c[0] -notmatch '^\d+$') { continue }
    $data = [IO.File]::ReadAllBytes((Join-Path (Join-Path $Root $ResourcesDir) ($c[0] + '.bin')))
    if ($data.Length -ne [int]$c[2]) { throw "resource $($c[0]) has not the length of the index" }
    $mod.Resources.Add((New-Object dnlib.DotNet.EmbeddedResource([dnlib.DotNet.UTF8String]$c[1], $data, [dnlib.DotNet.ManifestResourceAttributes]::Public)))
    $resourcesBack++
}
Write-Host ("{0,-20} {1,4} resources of the embedded assembly back in the module" -f 'resources', $resourcesBack)

$outFull = Join-Path $Root $Out
$opts = New-Object dnlib.DotNet.Writer.ModuleWriterOptions($mod)
$opts.MetaDataOptions.Flags = $opts.MetaDataOptions.Flags -bor [dnlib.DotNet.Writer.MetaDataFlags]::PreserveAll
$opts.Logger = [dnlib.DotNet.DummyLogger]::NoThrowInstance
$mod.Write($outFull, $opts)

# Read the result back: the restored methods must be the only new rows.
$check = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($outFull))
$after = $check.TablesStream.MethodTable.Rows
if ($after -ne $before + $added + $standIns) { throw "expected $($added + $standIns) new methods, found $($after - $before)" }
foreach ($r in $undo.Results) {
    $sig = $r.Method.MethodSig.ToString()
    $type = $check.ResolveToken($r.Method.DeclaringType.MDToken.Raw)
    $back = @($type.Methods | Where-Object { [string]$_.Name -eq [string]$r.Method.Name -and $_.MethodSig.ToString() -eq $sig })
    if ($back.Count -ne 1 -or $back[0].MDToken.Raw -le (0x06000000 + $before)) {
        throw ("restored method of '{0}' not found among the new rows" -f $r.Kind)
    }
    $back = $back[0]
    $token = $back.MDToken.Raw
    $kept = ''
    if ($r.Kept -gt 0) { $kept = (', {0} sites left as written (their local has other uses)' -f $r.Kept) }
    Write-Host ("{0,-20} {1,4} sites in {2,4} methods -> call 0x{3:X8} {4}{5}" -f $r.Kind, $r.Sites, $r.Methods, $token, $back.FullName, $kept)
}
foreach ($e in $undo.Edits) {
    $c = $e.Split("`t")
    Write-Host ("{0,-20} {1,4} sites in {2,4} methods, edited in place" -f $c[0], $c[1], $c[2])
}
$check.Dispose()
$mod.Dispose()

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("token`tsites`tundone")
foreach ($t in $devirtualized) { $lines.Add("$t`t1`tdevirtualized") }
foreach ($t in $undo.Touched) { $lines.Add($t) }
[IO.File]::WriteAllLines((Join-Path $Root $Report), $lines, (New-Object Text.UTF8Encoding($false)))
Write-Host ("-> {0}" -f $outFull)
