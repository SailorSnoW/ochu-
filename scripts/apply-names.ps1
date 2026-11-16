<#
.SYNOPSIS
    Applies the versioned rename table (docs/names.tsv) to the de4dot-renamed assembly.

.DESCRIPTION
    Table format, tab-separated, '#' starts a comment line:

        token<TAB>name<TAB>free-form note

    token : metadata token of a type (0x02), field (0x04), method (0x06) or property (0x17)
    name  : types take a full name ("osu.Graphics.SpriteManager"); nested types and
            members take a simple name. Generic arity suffixes are added automatically.

    Renaming a virtual method renames its whole override/implementation group.
    Renaming a property also renames its accessors (get_/set_).
    MemberRefs into generic instantiations are kept in sync. Tokens are preserved.

.EXAMPLE
    .\scripts\apply-names.ps1
    .\scripts\apply-names.ps1 -CheckOnly
#>
[CmdletBinding()]
param(
    [string]$In = 'work\deobf\osu!.renamed.exe',
    [string]$Out = 'work\deobf\osu!.named.exe',
    [string]$Table = 'docs\names.tsv',
    # Pre-rename stage with the same tokens: its names identify MemberRefs de4dot missed.
    [string]$Original = 'work\deobf\osu!.clean.exe',
    # What the compiler-artifact rules renamed (regenerated on every run).
    [string]$CompilerReport = 'work\naming\compiler-names.tsv',
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$inFull = (Resolve-Path -LiteralPath (Join-Path $Root $In)).Path
$gameDir = (Join-Path $Root 'game') + '\'
if ($inFull.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Refusing to open a file under game/.'
}
$tablePath = Join-Path $Root $Table
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))

# ---- parse table -------------------------------------------------------------
$entries = New-Object System.Collections.ArrayList
$seen = @{}
$lineNo = 0
$problems = New-Object System.Collections.Generic.List[string]
foreach ($line in [IO.File]::ReadAllLines($tablePath)) {
    $lineNo++
    if ($line.Trim() -eq '' -or $line.TrimStart().StartsWith('#')) { continue }
    $p = $line.Split("`t")
    if ($p.Count -lt 2 -or $p[0] -notmatch '^0x[0-9A-Fa-f]{8}$' -or $p[1].Trim() -eq '') {
        $problems.Add("line ${lineNo}: expected '0xTOKEN<TAB>name[<TAB>note]'"); continue
    }
    $tok = [Convert]::ToUInt32($p[0].Substring(2), 16)
    $name = $p[1].Trim()
    if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$') {
        $problems.Add("line ${lineNo}: '$name' is not a valid identifier"); continue
    }
    if ($seen.ContainsKey($tok)) { $problems.Add("line ${lineNo}: token $($p[0]) already defined on line $($seen[$tok])"); continue }
    $seen[$tok] = $lineNo
    [void]$entries.Add((New-Object psobject -Property @{ Token = $tok; Name = $name; Line = $lineNo }))
}

# ---- load --------------------------------------------------------------------
$ctx = [dnlib.DotNet.ModuleDef]::CreateModuleContext()
$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($inFull), $ctx)

# ---- original method bodies -----------------------------------------------------
# de4dot rewrites about 2700 bodies while renaming. Put the bodies of the pre-rename stage
# back (same tokens), so that what gets decompiled is the original IL
# (scripts/lib/BodyTransplant.cs).
if (-not $CheckOnly) {
    Add-Type -IgnoreWarnings -WarningAction SilentlyContinue `
        -ReferencedAssemblies (Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll') -Path (Join-Path $PSScriptRoot 'lib\BodyTransplant.cs')
    $bodySource = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath (Join-Path $Root $Original)).Path))
    $bt = New-Object BodyTransplant($mod, $bodySource)
    $bt.Run()
    Write-Host ("original bodies: {0} methods, {1} had been rewritten by de4dot; {2} operands compared by token, {3} mismatches; {4} operands not re-pointed" -f `
        $bt.Methods, $bt.Rewritten, $bt.Compared, $bt.TokenMismatches, $bt.Unresolved)
    foreach ($p in $bt.Problems) { Write-Host "  $p" }
    if ($bt.TokenMismatches -gt 0 -or $bt.Unresolved -gt 0) { throw 'method bodies cannot be re-pointed by token: the two stages do not share their tokens' }
}

# MemberRefs that point into this module (generic instantiations), keyed by target def token.
# TypeRefs that point back into this module (by name): they must follow the TypeDef.
$typeRefPairs = New-Object System.Collections.ArrayList
$localTypeRefs = @{}
function Test-LocalScope($tr) {
    $scope = $tr.ResolutionScope
    if ($scope -is [dnlib.DotNet.TypeRef]) { return (Test-LocalScope $scope) }
    if ($scope -is [dnlib.DotNet.ModuleDef]) { return $true }
    if ($scope -is [dnlib.DotNet.AssemblyRef]) { return ([string]$scope.Name -ceq [string]$mod.Assembly.Name) }
    return $false
}
foreach ($tr in $mod.GetTypeRefs()) {
    if (-not (Test-LocalScope $tr)) { continue }
    $td = $mod.Find($tr)
    if ($td) { [void]$typeRefPairs.Add(@($tr, $td)); $localTypeRefs[$tr.MDToken.Raw] = $td }
}
# The TypeDef of this module that a MemberRef points into, directly, through a generic
# instantiation or through an internal TypeRef. $null for anything external.
function Get-LocalOwner($mr) {
    $dt = $mr.DeclaringType
    if (-not $dt) { return $null }
    $s = $dt.ScopeType
    if ($s -is [dnlib.DotNet.TypeDef]) { return $s }
    if ($s -is [dnlib.DotNet.TypeRef]) { return $localTypeRefs[$s.MDToken.Raw] }
    return $null
}

$refsByDef = @{}
$refPairs = New-Object System.Collections.ArrayList   # (MemberRef, definition): names are synced again before writing
$unresolvedRefs = New-Object System.Collections.ArrayList
foreach ($mr in $mod.GetMemberRefs()) {
    $dt = $mr.DeclaringType
    $owner = Get-LocalOwner $mr
    if (-not $owner) { continue }
    # Array pseudo-methods (.ctor, Get, Set, Address) have no definition.
    if (($dt -is [dnlib.DotNet.TypeSpec]) -and ($dt.TypeSig -is [dnlib.DotNet.ArraySigBase])) { continue }
    $def = $null
    if ($mr.IsMethodRef) {
        $def = $mr.ResolveMethod()
        if (-not $def) { $def = $owner.FindMethod($mr.Name, $mr.MethodSig) }
    } elseif ($mr.IsFieldRef) {
        $def = $mr.ResolveField()
        if (-not $def) { $def = $owner.FindField($mr.Name, $mr.FieldSig) }
    }
    if (-not $def) { [void]$unresolvedRefs.Add($mr); continue }
    $k = $def.MDToken.Raw
    if (-not $refsByDef.ContainsKey($k)) { $refsByDef[$k] = New-Object System.Collections.ArrayList }
    [void]$refsByDef[$k].Add($mr)
    [void]$refPairs.Add(@($mr, $def))
}

# de4dot leaves the obfuscated name on some MemberRefs into generic instantiations while
# it renames the definition. Those still carry the name the definition has in the
# pre-rename stage: find the definition there (same tokens) by name and signature.
$stillUnresolved = 0; $rescued = 0
if ($unresolvedRefs.Count -gt 0) {
    $origFull = (Resolve-Path -LiteralPath (Join-Path $Root $Original)).Path
    $orig = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($origFull))
    $cmp = New-Object dnlib.DotNet.SigComparer
    foreach ($mr in $unresolvedRefs) {
        $owner = Get-LocalOwner $mr
        $otype = $orig.ResolveToken($owner.MDToken.Raw) -as [dnlib.DotNet.TypeDef]
        $hits = New-Object System.Collections.ArrayList
        if ($otype -and $mr.IsMethodRef) {
            foreach ($om in $otype.Methods) {
                if ([string]$om.Name -cne [string]$mr.Name) { continue }
                $cur = $mod.ResolveToken($om.MDToken.Raw) -as [dnlib.DotNet.MethodDef]
                if ($cur -and $cmp.Equals($mr.MethodSig, $cur.MethodSig)) { [void]$hits.Add($cur) }
            }
        } elseif ($otype -and $mr.IsFieldRef) {
            foreach ($of in $otype.Fields) {
                if ([string]$of.Name -cne [string]$mr.Name) { continue }
                $cur = $mod.ResolveToken($of.MDToken.Raw) -as [dnlib.DotNet.FieldDef]
                if ($cur -and $cmp.Equals($mr.FieldSig, $cur.FieldSig)) { [void]$hits.Add($cur) }
            }
        }
        if ($hits.Count -eq 1) {
            $def = $hits[0]; $k = $def.MDToken.Raw
            if (-not $refsByDef.ContainsKey($k)) { $refsByDef[$k] = New-Object System.Collections.ArrayList }
            [void]$refsByDef[$k].Add($mr)
            [void]$refPairs.Add(@($mr, $def))
            $rescued++
        } else { $stillUnresolved++ }
    }
    $orig.Dispose()
}
Write-Host ("member references into this module: {0} resolved, {1} more by pre-rename name, {2} unresolved" -f ($refPairs.Count - $rescued), $rescued, $stillUnresolved)

# Virtual methods indexed by "name/paramcount" for override-group discovery.
$virtIndex = @{}
function Build-VirtIndex {
    $virtIndex.Clear()
    foreach ($t in $mod.GetTypes()) {
        foreach ($m in $t.Methods) {
            if (-not $m.IsVirtual) { continue }
            $k = '{0}/{1}' -f $m.Name, $m.MethodSig.Params.Count
            if (-not $virtIndex.ContainsKey($k)) { $virtIndex[$k] = New-Object System.Collections.ArrayList }
            [void]$virtIndex[$k].Add($m)
        }
    }
}
Build-VirtIndex

$basesCache = @{}
function Get-Bases($type) {
    # All in-module ancestors and implemented interfaces of a TypeDef (tokens).
    $key = $type.MDToken.Raw
    if ($basesCache.ContainsKey($key)) { return $basesCache[$key] }
    $set = @{}
    $basesCache[$key] = $set
    $parents = New-Object System.Collections.ArrayList
    if ($type.BaseType) { [void]$parents.Add($type.BaseType) }
    foreach ($i in $type.Interfaces) { [void]$parents.Add($i.Interface) }
    foreach ($p in $parents) {
        $pd = $p.ScopeType -as [dnlib.DotNet.TypeDef]
        if (-not $pd) { continue }
        $set[$pd.MDToken.Raw] = 1
        foreach ($g in (Get-Bases $pd).Keys) { $set[$g] = 1 }
    }
    return $set
}
function Test-Related($a, $b) {
    $ta = $a.DeclaringType; $tb = $b.DeclaringType
    if ((Get-Bases $ta).ContainsKey($tb.MDToken.Raw)) { return $true }
    if ((Get-Bases $tb).ContainsKey($ta.MDToken.Raw)) { return $true }
    return $false
}
function Get-VirtualGroup($method) {
    $group = @{}
    $group[$method.MDToken.Raw] = $method
    if (-not $method.IsVirtual) { return $group }
    $cands = $virtIndex[('{0}/{1}' -f $method.Name, $method.MethodSig.Params.Count)]
    $grew = $true
    while ($grew) {
        $grew = $false
        foreach ($c in $cands) {
            if ($group.ContainsKey($c.MDToken.Raw)) { continue }
            foreach ($g in @($group.Values)) {
                if (Test-Related $c $g) { $group[$c.MDToken.Raw] = $c; $grew = $true; break }
            }
        }
    }
    return $group
}

$methodNames = @{}   # token -> new name (after group expansion)
$methodOrigin = @{}  # token -> table line
function Add-MethodRename($method, [string]$name, [int]$line) {
    $group = Get-VirtualGroup $method
    foreach ($m in $group.Values) {
        $k = $m.MDToken.Raw
        if ($methodNames.ContainsKey($k) -and $methodNames[$k] -ne $name) {
            $problems.Add(("line {0}: method 0x{1:X8} already named '{2}' by line {3} (same virtual group)" -f $line, $k, $methodNames[$k], $methodOrigin[$k]))
            continue
        }
        $methodNames[$k] = $name; $methodOrigin[$k] = $line
    }
}

$typeRenames = New-Object System.Collections.ArrayList
$fieldRenames = New-Object System.Collections.ArrayList
$propRenames = New-Object System.Collections.ArrayList

foreach ($e in $entries) {
    $table = $e.Token -shr 24
    $obj = $mod.ResolveToken($e.Token)
    if (-not $obj) { $problems.Add(("line {0}: token 0x{1:X8} does not resolve" -f $e.Line, $e.Token)); continue }
    switch ($table) {
        0x02 { [void]$typeRenames.Add(@($obj, $e)) }
        0x04 {
            if ($e.Name.Contains('.')) { $problems.Add("line $($e.Line): field name must be simple") } else { [void]$fieldRenames.Add(@($obj, $e)) }
        }
        0x06 {
            if ($e.Name.Contains('.')) { $problems.Add("line $($e.Line): method name must be simple") }
            elseif ($obj.IsConstructor) { $problems.Add("line $($e.Line): constructors cannot be renamed") }
            else { Add-MethodRename $obj $e.Name $e.Line }
        }
        0x17 {
            if ($e.Name.Contains('.')) { $problems.Add("line $($e.Line): property name must be simple"); break }
            [void]$propRenames.Add(@($obj, $e))
            if ($obj.GetMethod) { Add-MethodRename $obj.GetMethod ('get_' + $e.Name) $e.Line }
            if ($obj.SetMethod) { Add-MethodRename $obj.SetMethod ('set_' + $e.Name) $e.Line }
        }
        default { $problems.Add(("line {0}: unsupported token table 0x{1:X2}" -f $e.Line, $table)) }
    }
}

# Type full-name collisions (against renamed and untouched types alike).
$finalTypeNames = @{}
$typeTarget = @{}
foreach ($pair in $typeRenames) {
    $t = $pair[0]; $e = $pair[1]
    $ns = ''; $simple = $e.Name
    $dot = $e.Name.LastIndexOf('.')
    if ($dot -ge 0) { $ns = $e.Name.Substring(0, $dot); $simple = $e.Name.Substring($dot + 1) }
    if ($t.IsNested -and $ns -ne '') { $problems.Add("line $($e.Line): nested type takes a simple name"); continue }
    $old = [string]$t.Name
    $tick = $old.IndexOf('`')
    if ($tick -ge 0) { $simple += $old.Substring($tick) }
    $typeTarget[$t.MDToken.Raw] = @($ns, $simple)
}
foreach ($t in $mod.GetTypes()) {
    $ns = [string]$t.Namespace; $n = [string]$t.Name
    if ($typeTarget.ContainsKey($t.MDToken.Raw)) { $ns = $typeTarget[$t.MDToken.Raw][0]; $n = $typeTarget[$t.MDToken.Raw][1] }
    $scope = 'top'
    if ($t.IsNested) { $scope = '0x{0:X8}' -f $t.DeclaringType.MDToken.Raw }
    $k = "$scope|$ns|$n"
    if ($finalTypeNames.ContainsKey($k)) {
        $problems.Add(("type name collision: '{0}.{1}' for 0x{2:X8} and 0x{3:X8}" -f $ns, $n, $t.MDToken.Raw, $finalTypeNames[$k]))
    } else { $finalTypeNames[$k] = $t.MDToken.Raw }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "ERROR $_" }
    throw ("{0} problem(s) in {1}" -f $problems.Count, $Table)
}

Write-Host ("names.tsv: {0} entries -> {1} types, {2} fields, {3} properties, {4} methods (after group expansion)" -f `
    $entries.Count, $typeRenames.Count, $fieldRenames.Count, $propRenames.Count, $methodNames.Count)
if ($CheckOnly) { $mod.Dispose(); return }

# ---- apply -------------------------------------------------------------------
function Set-RefNames([uint32]$defToken, [string]$name) {
    if ($refsByDef.ContainsKey($defToken)) {
        foreach ($mr in $refsByDef[$defToken]) { $mr.Name = [dnlib.DotNet.UTF8String]$name }
    }
}
# de4dot derives placeholder field/parameter names from the type name
# (Class218 -> class218_0). Carry the new type name over: spriteManager_0.
$prefixMap = @{}
$prefixClash = @{}
foreach ($pair in $typeRenames) {
    $t = $pair[0]; $target = $typeTarget[$t.MDToken.Raw]
    $oldPrefix = ([string]$t.Name -replace '`\d+$', '').ToLowerInvariant()
    $new = $target[1] -replace '`\d+$', ''
    $camel = $new.Substring(0, 1).ToLowerInvariant() + $new.Substring(1)
    if ($prefixMap.ContainsKey($oldPrefix)) { $prefixClash[$oldPrefix] = 1 } else { $prefixMap[$oldPrefix] = $camel }
    if (-not $t.IsNested) { $t.Namespace = [dnlib.DotNet.UTF8String]$target[0] }
    $t.Name = [dnlib.DotNet.UTF8String]$target[1]
}
foreach ($k in $prefixClash.Keys) { $prefixMap.Remove($k) }
$explicitFields = @{}
foreach ($pair in $fieldRenames) { $explicitFields[$pair[0].MDToken.Raw] = 1 }
$derived = 0
foreach ($t in $mod.GetTypes()) {
    $taken = @{}
    foreach ($f in $t.Fields) { $taken[[string]$f.Name] = 1 }
    foreach ($f in $t.Fields) {
        if ($explicitFields.ContainsKey($f.MDToken.Raw)) { continue }
        if ([string]$f.Name -notmatch '^([a-z]+\d+)_(\d+)$') { continue }
        if (-not $prefixMap.ContainsKey($Matches[1])) { continue }
        $n = '{0}_{1}' -f $prefixMap[$Matches[1]], $Matches[2]
        if ($taken.ContainsKey($n)) { continue }
        $taken[$n] = 1
        $f.Name = [dnlib.DotNet.UTF8String]$n
        Set-RefNames $f.MDToken.Raw $n
        $derived++
    }
    foreach ($m in $t.Methods) {
        foreach ($pd in $m.ParamDefs) {
            if ([string]$pd.Name -notmatch '^([a-z]+\d+)_(\d+)$') { continue }
            if (-not $prefixMap.ContainsKey($Matches[1])) { continue }
            $pd.Name = [dnlib.DotNet.UTF8String]('{0}_{1}' -f $prefixMap[$Matches[1]], $Matches[2])
            $derived++
        }
    }
}
Write-Host ("derived {0} field/parameter names from renamed types" -f $derived)
foreach ($pair in $fieldRenames) {
    $pair[0].Name = [dnlib.DotNet.UTF8String]$pair[1].Name
    Set-RefNames $pair[0].MDToken.Raw $pair[1].Name
}
foreach ($pair in $propRenames) { $pair[0].Name = [dnlib.DotNet.UTF8String]$pair[1].Name }
foreach ($k in $methodNames.Keys) {
    $m = $mod.ResolveToken([uint32]$k)
    $m.Name = [dnlib.DotNet.UTF8String]$methodNames[$k]
    Set-RefNames ([uint32]$k) $methodNames[$k]
}

# ---- compiler artifacts --------------------------------------------------------
# Structural rules, not table entries: the real names are not C# identifiers.
# See docs/matching.md. Tokens and IL do not change.

# 1. <PrivateImplementationDetails>: a top-level type made only of static data fields
#    (array initializers), their explicit-layout structs and at most one uint(string)
#    method (ComputeStringHash). One per merged assembly. With the real names the
#    decompiler hides them and rebuilds array initializers and string switches.
$cgCtor = $null
foreach ($t in $mod.GetTypes()) {
    if ($cgCtor) { break }
    foreach ($m in $t.Methods) {
        foreach ($ca in $m.CustomAttributes) {
            if ($ca.TypeFullName -eq 'System.Runtime.CompilerServices.CompilerGeneratedAttribute') { $cgCtor = $ca.Constructor; break }
        }
        if ($cgCtor) { break }
    }
}
function Test-DataStruct($t) {
    return ($t.IsValueType -and $t.IsExplicitLayout -and $t.Fields.Count -eq 0 -and $t.Methods.Count -eq 0 -and $t.ClassLayout -and $t.ClassLayout.ClassSize -gt 0)
}
function Rename-DataStructs($owner) {
    $sizes = @{}
    foreach ($n in $owner.NestedTypes) { if (Test-DataStruct $n) { $sizes[[int]$n.ClassLayout.ClassSize] = 1 + [int]$sizes[[int]$n.ClassLayout.ClassSize] } }
    foreach ($n in $owner.NestedTypes) {
        if (-not (Test-DataStruct $n)) { continue }
        $size = [int]$n.ClassLayout.ClassSize
        if ($sizes[$size] -ne 1) { continue }
        $n.Name = [dnlib.DotNet.UTF8String]('__StaticArrayInitTypeSize={0}' -f $size)
    }
}
$pidTypes = New-Object System.Collections.ArrayList
foreach ($t in $mod.Types) {
    if ($t.IsGlobalModuleType -or $t.Fields.Count -eq 0 -or $t.Methods.Count -gt 1) { continue }
    $ok = $true
    foreach ($f in $t.Fields) { if (-not ($f.IsStatic -and $f.HasFieldRVA)) { $ok = $false; break } }
    foreach ($n in $t.NestedTypes) { if (-not (Test-DataStruct $n)) { $ok = $false; break } }
    foreach ($m in $t.Methods) {
        $s = $m.MethodSig
        if (-not ($m.IsStatic -and $s.Params.Count -eq 1 -and $s.Params[0].ElementType -eq [dnlib.DotNet.ElementType]::String -and
                $s.RetType.ElementType -eq [dnlib.DotNet.ElementType]::U4)) { $ok = $false }
    }
    if ($ok) { [void]$pidTypes.Add($t) }
}
$pidTokens = @{}
$first = $true
foreach ($t in $pidTypes) {
    $pidTokens[$t.MDToken.Raw] = 1
    $t.Namespace = [dnlib.DotNet.UTF8String]''
    # Only one type can carry the bare name: the others keep a token suffix, in the
    # style of the {GUID} suffix of older compilers.
    if ($first) { $t.Name = [dnlib.DotNet.UTF8String]'<PrivateImplementationDetails>'; $first = $false }
    # ('{{{0:X8}}}' -f token gives the text '{X8}': the braces are added apart.)
    else { $t.Name = [dnlib.DotNet.UTF8String]('<PrivateImplementationDetails>{' + ('{0:X8}' -f $t.MDToken.Raw) + '}') }
    foreach ($m in $t.Methods) { $m.Name = [dnlib.DotNet.UTF8String]'ComputeStringHash' }
    Rename-DataStructs $t
    $has = $false
    foreach ($ca in $t.CustomAttributes) { if ($ca.TypeFullName -eq 'System.Runtime.CompilerServices.CompilerGeneratedAttribute') { $has = $true } }
    if (-not $has -and $cgCtor) { $t.CustomAttributes.Add((New-Object dnlib.DotNet.CustomAttribute($cgCtor))) }
}

# 2. A private nested struct used by a non-private field or method of its declaring
#    type is legal IL but not legal C# (CS0051 / CS0052). Seen on array-initializer data
#    kept outside <PrivateImplementationDetails> and on local-function closure structs,
#    which the decompiler does not fold while their compiler names are missing.
#    Raise the struct to the accessibility of the member that exposes it.
function Get-InnerTypeDef($sig) {
    while ($sig -and $sig.Next) { $sig = $sig.Next }
    if (-not $sig) { return $null }
    return ([dnlib.DotNet.Extensions]::ToTypeDefOrRef($sig) -as [dnlib.DotNet.TypeDef])
}
$looseData = 0
$nestedVis = [dnlib.DotNet.TypeAttributes]
foreach ($t in $mod.GetTypes()) {
    if ($pidTokens.ContainsKey($t.MDToken.Raw)) { continue }
    # Data structs outside <PrivateImplementationDetails> get the compiler's type name too:
    # the obfuscator's own ones are named like the field that uses them (CS0102).
    foreach ($f in $t.Fields) { if ($f.IsStatic -and $f.HasFieldRVA) { Rename-DataStructs $t; break } }
    $want = @{}   # nested struct token -> 2 when a public/protected member exposes it, else 1
    foreach ($f in $t.Fields) {
        if ($f.IsPrivate) { continue }
        $n = Get-InnerTypeDef $f.FieldType
        if (-not $n -or $n.DeclaringType -ne $t -or -not $n.IsNestedPrivate -or -not $n.IsValueType) { continue }
        $level = 1; if ($f.IsPublic -or $f.IsFamily -or $f.IsFamilyOrAssembly) { $level = 2 }
        if ([int]$want[$n.MDToken.Raw] -lt $level) { $want[$n.MDToken.Raw] = $level }
    }
    foreach ($m in $t.Methods) {
        if ($m.IsPrivate) { continue }
        foreach ($p in $m.MethodSig.Params) {
            $n = Get-InnerTypeDef $p
            if (-not $n -or $n.DeclaringType -ne $t -or -not $n.IsNestedPrivate -or -not $n.IsValueType) { continue }
            $level = 1; if ($m.IsPublic -or $m.IsFamily -or $m.IsFamilyOrAssembly) { $level = 2 }
            if ([int]$want[$n.MDToken.Raw] -lt $level) { $want[$n.MDToken.Raw] = $level }
        }
    }
    foreach ($n in $t.NestedTypes) {
        if (-not $want.ContainsKey($n.MDToken.Raw)) { continue }
        if ($want[$n.MDToken.Raw] -eq 2) { $n.Visibility = $nestedVis::NestedPublic } else { $n.Visibility = $nestedVis::NestedAssembly }
        $looseData++
    }
}

# 3. The obfuscator seals every class that has no subclass. A sealed class that declares
#    a new overridable method cannot come from C#: undo the seal there.
$unsealed = 0
foreach ($t in $mod.GetTypes()) {
    if (-not $t.IsSealed -or $t.IsValueType -or $t.IsAbstract -or $t.IsInterface) { continue }
    if ($t.BaseType -and $t.BaseType.FullName -eq 'System.MulticastDelegate') { continue }
    foreach ($m in $t.Methods) {
        if ($m.IsVirtual -and $m.IsNewSlot -and -not $m.IsFinal -and -not $m.IsAbstract) { $t.IsSealed = $false; $unsealed++; break }
    }
}
Write-Host ("compiler artifacts: {0} <PrivateImplementationDetails> types, {1} private nested structs made accessible, {2} classes unsealed" -f $pidTypes.Count, $looseData, $unsealed)

# ---- setters known by their use ------------------------------------------------
# A void method called on the 'dup' of a 'newobj' in the middle of an expression is the
# setter of an object initializer (scripts/lib/AccessorUse.cs). Named 'set_X', the next
# step gives it a property and the decompiler writes 'new T { X = a }' again.
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue `
    -ReferencedAssemblies (Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll') -Path (Join-Path $PSScriptRoot 'lib\AccessorUse.cs')
$settersByUse = 0
Build-VirtIndex   # the names have changed since the first index
foreach ($m in [AccessorUse]::Find($mod)) {
    $old = [string]$m.Name
    if ($old -match '^(get|set|add|remove)_') { continue }   # renamed with its group
    $prop = $old.Substring(0, 1).ToUpperInvariant() + $old.Substring(1)
    $group = @((Get-VirtualGroup $m).Values)
    $clash = $false
    foreach ($g in $group) {
        foreach ($x in $g.DeclaringType.Methods) { if ([string]$x.Name -ceq ('set_' + $prop) -or [string]$x.Name -ceq ('get_' + $prop) -or [string]$x.Name -ceq $prop) { $clash = $true } }
        foreach ($x in $g.DeclaringType.Fields) { if ([string]$x.Name -ceq $prop) { $clash = $true } }
    }
    if ($clash) { continue }
    foreach ($g in $group) {
        $g.Name = [dnlib.DotNet.UTF8String]('set_' + $prop)
        Set-RefNames $g.MDToken.Raw ('set_' + $prop)
    }
    $settersByUse++
}
Write-Host ("setters recognised by their use in an object initializer: {0}" -f $settersByUse)

# A getter and a setter called on one 'dup' of their receiver are the two accessors of a
# compound assignment ('x.P |= 8'): named 'get_X' / 'set_X', X from the name of the getter.
$pairsByUse = 0
Build-VirtIndex
$pairs = [AccessorUse]::FindPairs($mod)
for ($pi = 0; $pi -lt $pairs.Count; $pi += 2) {
    $getter = $pairs[$pi]; $setter = $pairs[$pi + 1]
    $old = [string]$getter.Name
    if ($old -match '^(get|set|add|remove)_' -or [string]$setter.Name -match '^(get|set|add|remove)_') { continue }
    if ($old -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') { continue }
    $prop = $old.Substring(0, 1).ToUpperInvariant() + $old.Substring(1)
    $getGroup = @((Get-VirtualGroup $getter).Values)
    $setGroup = @((Get-VirtualGroup $setter).Values)
    $clash = $false
    foreach ($g in ($getGroup + $setGroup)) {
        foreach ($x in $g.DeclaringType.Methods) { if ([string]$x.Name -ceq ('set_' + $prop) -or [string]$x.Name -ceq ('get_' + $prop) -or [string]$x.Name -ceq $prop) { $clash = $true } }
        foreach ($x in $g.DeclaringType.Fields) { if ([string]$x.Name -ceq $prop) { $clash = $true } }
        foreach ($x in $g.DeclaringType.NestedTypes) { if ([string]$x.Name -ceq $prop) { $clash = $true } }
    }
    # The two groups must cover the same types: an accessor overridden alone would be left
    # without its other half.
    if ($getGroup.Count -ne $setGroup.Count) { $clash = $true }
    if ($clash) { continue }
    foreach ($g in $getGroup) { $g.Name = [dnlib.DotNet.UTF8String]('get_' + $prop); Set-RefNames $g.MDToken.Raw ('get_' + $prop) }
    foreach ($g in $setGroup) { $g.Name = [dnlib.DotNet.UTF8String]('set_' + $prop); Set-RefNames $g.MDToken.Raw ('set_' + $prop) }
    $pairsByUse++
}
Write-Host ("accessor pairs recognised by a compound assignment: {0}" -f $pairsByUse)

# ---- explicit interface implementations ----------------------------------------
# An explicit implementation is named '<interface>.<member>' and found through its
# MethodImpl row, so it is in no virtual group: give it the new name of the member it
# implements, and a property when that member is an accessor (the decompiler prints
# 'T I.P { get; }' from the property, 'T I.get_P()' otherwise, which does not compile).
# The interface as the compiler spells it in the name of an explicit implementation:
# 'Namespace.IName<System.Int32>', nested types joined by '.'.
function Get-CSharpTypeName($type) {
    return (([string]$type.FullName) -replace '`\d+', '' -replace '/', '.')
}
$explicitRenamed = 0; $explicitProps = 0
$declByRef = @{}
foreach ($pair in $refPairs) { $declByRef[$pair[0].MDToken.Raw] = $pair[1] }
foreach ($t in $mod.GetTypes()) {
    $owned = @{}
    foreach ($p in $t.Properties) { foreach ($m in @($p.GetMethods) + @($p.SetMethods)) { $owned[$m.MDToken.Raw] = 1 } }
    $made = New-Object System.Collections.Hashtable
    foreach ($m in $t.Methods) {
        if ($m.Overrides.Count -ne 1 -or -not ([string]$m.Name).Contains('.')) { continue }
        $decl = $m.Overrides[0].MethodDeclaration
        $def = $decl -as [dnlib.DotNet.MethodDef]
        if (-not $def) { $def = $declByRef[$decl.MDToken.Raw] }
        if (-not $def) { continue }
        $member = [string]$def.Name
        $prefix = Get-CSharpTypeName $decl.DeclaringType
        $want = $prefix + '.' + $member
        if ([string]$m.Name -cne $want) { $m.Name = [dnlib.DotNet.UTF8String]$want; $explicitRenamed++ }
        if ($owned.ContainsKey($m.MDToken.Raw) -or $member -notmatch '^(get|set)_(.+)$') { continue }
        $kind = $Matches[1]; $propName = $prefix + '.' + $Matches[2]
        $isVoid = $m.MethodSig.RetType.ElementType -eq [dnlib.DotNet.ElementType]::Void
        if (($kind -eq 'get' -and ($isVoid -or $m.MethodSig.Params.Count -ne 0)) -or ($kind -eq 'set' -and (-not $isVoid -or $m.MethodSig.Params.Count -ne 1))) { continue }
        $prop = $made[$propName]
        if (-not $prop) {
            if ($kind -eq 'get') { $propType = $m.MethodSig.RetType } else { $propType = $m.MethodSig.Params[0] }
            $prop = New-Object dnlib.DotNet.PropertyDefUser(([dnlib.DotNet.UTF8String]$propName), [dnlib.DotNet.PropertySig]::CreateInstance($propType, (New-Object 'dnlib.DotNet.TypeSig[]' 0)))
            $t.Properties.Add($prop); $made[$propName] = $prop; $explicitProps++
        }
        if ($kind -eq 'get') { $prop.GetMethods.Add($m) } else { $prop.SetMethods.Add($m) }
        $m.IsSpecialName = $true
    }
}
Write-Host ("explicit interface implementations: {0} renamed after their member, {1} properties made" -f $explicitRenamed, $explicitProps)

# ---- restore properties and events ---------------------------------------------
# The obfuscator stripped Property/Event metadata. Accessors named get_X / set_X /
# add_X / remove_X (by names.tsv, or surviving) get a PropertyDef / EventDef again so
# the decompiler prints properties and events. Method, field and type tokens do not change.
$identifier = '^[A-Za-z_][A-Za-z0-9_]*$'
$propsMade = 0; $eventsMade = 0; $accessorsSkipped = 0; $indexersMade = 0
foreach ($t in $mod.GetTypes()) {
    # A property or an event de4dot left in place keeps its generic name when only its
    # accessors were named: it takes the name its accessors agree on.
    foreach ($p in $t.Properties) {
        $x = $null; $agree = $true
        foreach ($m in @($p.GetMethods) + @($p.SetMethods)) {
            if ([string]$m.Name -match '^(?:get|set)_([A-Za-z_][A-Za-z0-9_]*)$') { if ($x -and $x -cne $Matches[1]) { $agree = $false }; $x = $Matches[1] } else { $agree = $false }
        }
        if ($agree -and $x -and [string]$p.Name -cne $x -and -not ([string]$p.Name).Contains('.')) { $p.Name = [dnlib.DotNet.UTF8String]$x }
    }
    foreach ($e in $t.Events) {
        if (-not $e.AddMethod -or -not $e.RemoveMethod) { continue }
        if ([string]$e.AddMethod.Name -match '^add_([A-Za-z_][A-Za-z0-9_]*)$' -and [string]$e.RemoveMethod.Name -ceq ('remove_' + $Matches[1]) -and [string]$e.Name -cne $Matches[1]) {
            $e.Name = [dnlib.DotNet.UTF8String]$Matches[1]
        }
    }
    # de4dot restores indexers as parameterless properties: give them the index
    # parameters of their accessors, and [DefaultMember] its real argument.
    $defaultMember = $null
    foreach ($p in $t.Properties) {
        $get = $p.GetMethod; $set = $p.SetMethod
        $index = $null
        if ($get -and $get.MethodSig.Params.Count -ge 1) { $index = @($get.MethodSig.Params) }
        elseif (-not $get -and $set -and $set.MethodSig.Params.Count -ge 2) {
            $ps = @($set.MethodSig.Params); $index = @($ps[0..($ps.Count - 2)])
        }
        if (-not $index) { continue }
        if ($p.PropertySig.Params.Count -ne $index.Count) {
            $first = $get; if (-not $first) { $first = $set }
            $indexSigs = [dnlib.DotNet.TypeSig[]]$index
            if ($first.IsStatic) { $p.PropertySig = [dnlib.DotNet.PropertySig]::CreateStatic($p.PropertySig.RetType, $indexSigs) }
            else { $p.PropertySig = [dnlib.DotNet.PropertySig]::CreateInstance($p.PropertySig.RetType, $indexSigs) }
        }
        # Explicit interface implementations ("IList.Item") are never the default member.
        if (-not $defaultMember -and -not ([string]$p.Name).Contains('.')) { $defaultMember = [string]$p.Name }
    }
    # The indexer was renamed (by de4dot or by the table) but [DefaultMember] still names
    # the obfuscated one: the decompiler then does not see an indexer.
    if ($defaultMember) {
        foreach ($ca in $t.CustomAttributes) {
            if ($ca.TypeFullName -eq 'System.Reflection.DefaultMemberAttribute' -and $ca.ConstructorArguments.Count -eq 1 -and
                [string]$ca.ConstructorArguments[0].Value -cne $defaultMember) {
                $ca.ConstructorArguments[0] = New-Object dnlib.DotNet.CAArgument($mod.CorLibTypes.String, ([dnlib.DotNet.UTF8String]$defaultMember))
                $indexersMade++
            }
        }
    }
    $attached = @{}
    $takenNames = New-Object System.Collections.Hashtable   # case-sensitive, unlike @{}
    $takenNames[([string]$t.Name -replace '`\d+$', '')] = 1
    foreach ($p in $t.Properties) {
        $takenNames[[string]$p.Name] = 1
        foreach ($m in $p.GetMethods) { $attached[$m.MDToken.Raw] = 1 }
        foreach ($m in $p.SetMethods) { $attached[$m.MDToken.Raw] = 1 }
    }
    foreach ($e in $t.Events) {
        $takenNames[[string]$e.Name] = 1
        foreach ($m in @($e.AddMethod, $e.RemoveMethod, $e.InvokeMethod)) { if ($m) { $attached[$m.MDToken.Raw] = 1 } }
    }
    $fieldNames = New-Object System.Collections.Hashtable
    foreach ($f in $t.Fields) { $fieldNames[[string]$f.Name] = 1 }
    foreach ($n in $t.NestedTypes) { $takenNames[([string]$n.Name -replace '`\d+$', '')] = 1 }
    $groups = New-Object System.Collections.Hashtable
    foreach ($m in $t.Methods) {
        $mn = [string]$m.Name
        if ($mn -match '^(get|set|add|remove)_(.+)$') {
            if ($attached.ContainsKey($m.MDToken.Raw)) { continue }
            if (-not $groups.ContainsKey($Matches[2])) { $groups[$Matches[2]] = @{ get = @(); set = @(); add = @(); remove = @() } }
            $groups[$Matches[2]][$Matches[1]] += $m
        } else { $takenNames[$mn] = 1 }
    }
    # In the order of the accessors (the order of the source): a hashtable order would move
    # the initializers of auto-properties in the constructor.
    $ordered = @($groups.Keys | Sort-Object { $g = $groups[$_]; (@($g.get) + @($g.set) + @($g.add) + @($g.remove) | ForEach-Object { $_.MDToken.Raw } | Measure-Object -Minimum).Minimum })
    foreach ($x in $ordered) {
        $g = $groups[$x]
        if ($x -notmatch $identifier -or $takenNames.ContainsKey($x)) { $accessorsSkipped++; continue }
        $isVoid = { param($m) $m.MethodSig.RetType.ElementType -eq [dnlib.DotNet.ElementType]::Void }
        $plain = { param($m) $m.MethodSig.GenParamCount -eq 0 }
        if ($x -ceq 'Item' -and $g.add.Count -eq 0 -and $g.remove.Count -eq 0 -and -not $fieldNames.ContainsKey($x)) {
            # Indexers: get_Item(index...) and set_Item(index..., value), one property per
            # index signature. Parameterless accessors named Item fall through to the
            # ordinary property rule below.
            $idxGet = @($g.get | Where-Object { $_.MethodSig.Params.Count -ge 1 -and -not (& $isVoid $_) -and (& $plain $_) })
            $idxSet = @($g.set | Where-Object { $_.MethodSig.Params.Count -ge 2 -and (& $isVoid $_) -and (& $plain $_) })
            if ($idxGet.Count + $idxSet.Count -gt 0) {
                $bySig = New-Object System.Collections.Hashtable
                foreach ($m in $idxGet) {
                    $k = ([string]$m.IsStatic) + '|' + (($m.MethodSig.Params | ForEach-Object { $_.FullName }) -join ',') + '|' + $m.MethodSig.RetType.FullName
                    if (-not $bySig.ContainsKey($k)) { $bySig[$k] = @{ get = $null; set = $null } }
                    $bySig[$k].get = $m
                }
                foreach ($m in $idxSet) {
                    $ps = @($m.MethodSig.Params)
                    $k = ([string]$m.IsStatic) + '|' + (($ps[0..($ps.Count - 2)] | ForEach-Object { $_.FullName }) -join ',') + '|' + $ps[$ps.Count - 1].FullName
                    if (-not $bySig.ContainsKey($k)) { $bySig[$k] = @{ get = $null; set = $null } }
                    $bySig[$k].set = $m
                }
                foreach ($e in $bySig.Values) {
                    if ($e.get) { $propType = $e.get.MethodSig.RetType; $index = @($e.get.MethodSig.Params) }
                    else { $ps = @($e.set.MethodSig.Params); $propType = $ps[$ps.Count - 1]; $index = @($ps[0..($ps.Count - 2)]) }
                    $first = $e.get; if (-not $first) { $first = $e.set }
                    $indexSigs = [dnlib.DotNet.TypeSig[]]$index
                    if ($first.IsStatic) { $sig = [dnlib.DotNet.PropertySig]::CreateStatic($propType, $indexSigs) }
                    else { $sig = [dnlib.DotNet.PropertySig]::CreateInstance($propType, $indexSigs) }
                    $prop = New-Object dnlib.DotNet.PropertyDefUser(([dnlib.DotNet.UTF8String]'Item'), $sig)
                    if ($e.get) { $prop.GetMethods.Add($e.get); $e.get.IsSpecialName = $true }
                    if ($e.set) { $prop.SetMethods.Add($e.set); $e.set.IsSpecialName = $true }
                    $t.Properties.Add($prop)
                    $indexersMade++
                }
                # [DefaultMember] still carries the obfuscated property name.
                foreach ($ca in $t.CustomAttributes) {
                    if ($ca.TypeFullName -eq 'System.Reflection.DefaultMemberAttribute' -and $ca.ConstructorArguments.Count -eq 1) {
                        $ca.ConstructorArguments[0] = New-Object dnlib.DotNet.CAArgument($mod.CorLibTypes.String, ([dnlib.DotNet.UTF8String]'Item'))
                    }
                }
                if ($idxGet.Count -eq $g.get.Count -and $idxSet.Count -eq $g.set.Count) { continue }
                $g = @{ get = @($g.get | Where-Object { $idxGet -notcontains $_ }); set = @($g.set | Where-Object { $idxSet -notcontains $_ }); add = @(); remove = @() }
            }
        }
        if ($g.add.Count -eq 1 -and $g.remove.Count -eq 1 -and $g.get.Count -eq 0 -and $g.set.Count -eq 0) {
            $a = $g.add[0]; $r = $g.remove[0]
            if ($a.MethodSig.Params.Count -ne 1 -or $r.MethodSig.Params.Count -ne 1 -or $a.IsStatic -ne $r.IsStatic -or
                -not (& $isVoid $a) -or -not (& $isVoid $r) -or -not (& $plain $a) -or -not (& $plain $r) -or
                $a.MethodSig.Params[0].FullName -ne $r.MethodSig.Params[0].FullName) { $accessorsSkipped++; continue }
            $evType = [dnlib.DotNet.Extensions]::ToTypeDefOrRef($a.MethodSig.Params[0])
            if (-not $evType) { $accessorsSkipped++; continue }
            $ev = New-Object dnlib.DotNet.EventDefUser(([dnlib.DotNet.UTF8String]$x), $evType)
            $ev.AddMethod = $a; $ev.RemoveMethod = $r
            $a.IsSpecialName = $true; $r.IsSpecialName = $true
            $t.Events.Add($ev)
            $eventsMade++
            continue
        }
        if ($g.add.Count -gt 0 -or $g.remove.Count -gt 0 -or $fieldNames.ContainsKey($x)) { $accessorsSkipped++; continue }
        $getters = @($g.get | Where-Object { $_.MethodSig.Params.Count -eq 0 -and -not (& $isVoid $_) -and (& $plain $_) })
        $setters = @($g.set | Where-Object { $_.MethodSig.Params.Count -eq 1 -and (& $isVoid $_) -and (& $plain $_) })
        if ($getters.Count -ne $g.get.Count -or $setters.Count -ne $g.set.Count -or $getters.Count -gt 1 -or $setters.Count -gt 1) { $accessorsSkipped++; continue }
        $getter = $null; $setter = $null
        if ($getters.Count -eq 1) { $getter = $getters[0] }
        if ($setters.Count -eq 1) { $setter = $setters[0] }
        if ($getter -and $setter) {
            if ($getter.IsStatic -ne $setter.IsStatic -or $getter.MethodSig.RetType.FullName -ne $setter.MethodSig.Params[0].FullName) { $setter = $null }
        }
        $first = $getter; if (-not $first) { $first = $setter }
        if (-not $first) { $accessorsSkipped++; continue }
        if ($getter) { $propType = $getter.MethodSig.RetType } else { $propType = $setter.MethodSig.Params[0] }
        $noParams = New-Object 'dnlib.DotNet.TypeSig[]' 0
        if ($first.IsStatic) { $sig = [dnlib.DotNet.PropertySig]::CreateStatic($propType, $noParams) }
        else { $sig = [dnlib.DotNet.PropertySig]::CreateInstance($propType, $noParams) }
        $prop = New-Object dnlib.DotNet.PropertyDefUser(([dnlib.DotNet.UTF8String]$x), $sig)
        if ($getter) { $prop.GetMethods.Add($getter); $getter.IsSpecialName = $true }
        if ($setter) { $prop.SetMethods.Add($setter); $setter.IsSpecialName = $true }
        $t.Properties.Add($prop)
        $propsMade++
    }
}
Write-Host ("restored {0} properties, {1} indexers and {2} events from accessor names ({3} accessor groups left as methods)" -f $propsMade, $indexersMade, $eventsMade, $accessorsSkipped)

# ---- compiler-generated members ------------------------------------------------
# Closure classes, lambdas, iterators, local functions and auto-property backing fields
# get their Roslyn names and [CompilerGenerated] back (scripts/lib/CompilerNames.cs).
# Runs after the property restoration: backing fields are named after their property.
Add-Type -IgnoreWarnings -WarningAction SilentlyContinue `
    -ReferencedAssemblies (Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll') -Path (Join-Path $PSScriptRoot 'lib\CompilerNames.cs')
$defMap = New-Object 'System.Collections.Generic.Dictionary[uint32,object]'
foreach ($pair in $refPairs) { $defMap[$pair[0].MDToken.Raw] = $pair[1] }
$cn = New-Object CompilerNames($mod, $defMap, $cgCtor)
$cn.Apply()
$cnReport = Join-Path $Root $CompilerReport
New-Item -ItemType Directory -Force (Split-Path -Parent $cnReport) | Out-Null
[IO.File]::WriteAllLines($cnReport, [string[]](@("token`tkind`told`tnew") + $cn.Report), (New-Object Text.UTF8Encoding($false)))
Write-Host ("compiler-generated members: " + (($cn.Counts.Keys | Sort-Object | ForEach-Object { '{0} {1}' -f $cn.Counts[$_], $_ }) -join ', '))
Write-Host ("      report: {0}" -f $cnReport)

# Every MemberRef into this module takes the final name of its definition, whatever
# renamed it (table, derived names, compiler-artifact rules, or de4dot before us).
$synced = 0
$defByRef = @{}
foreach ($pair in $refPairs) {
    $defByRef[$pair[0].MDToken.Raw] = $pair[1]
    if ([string]$pair[0].Name -cne [string]$pair[1].Name) { $pair[0].Name = $pair[1].Name; $synced++ }
}
# dnlib does not cache a MemberRef whose signature depends on a generic context: each
# instruction in a generic type or method holds its own copy, and the writer emits the
# copy. Renaming the cached row alone leaves the old name in the code (this is also why
# de4dot misses them). Walk the bodies and rename the copies, matched by token.
foreach ($t in $mod.GetTypes()) {
    foreach ($m in $t.Methods) {
        if (-not $m.HasBody) { continue }
        foreach ($i in $m.Body.Instructions) {
            $op = $i.Operand
            if ($null -eq $op) { continue }
            if ($op -is [dnlib.DotNet.MethodSpec]) { $op = $op.Method }
            if (-not ($op -is [dnlib.DotNet.MemberRef])) { continue }
            $def = $defByRef[$op.MDToken.Raw]
            if ($def -and ([string]$op.Name -cne [string]$def.Name)) { $op.Name = $def.Name; $synced++ }
        }
    }
}
$typeRefsSynced = 0
foreach ($pair in $typeRefPairs) {
    $tr = $pair[0]; $td = $pair[1]
    if ([string]$tr.Name -cne [string]$td.Name -or [string]$tr.Namespace -cne [string]$td.Namespace) {
        $tr.Name = $td.Name; $tr.Namespace = $td.Namespace; $typeRefsSynced++
    }
}
Write-Host ("synced {0} member reference names and {1} of {2} internal type references with their definitions" -f $synced, $typeRefsSynced, $typeRefPairs.Count)

# ---- resources -----------------------------------------------------------------
# The resources are those of the pre-rename stage, whatever de4dot made of them. One named
# after a type ('<full name>.resources', what ComponentResourceManager(typeof(T)) asks for)
# follows the name of that type, found by token.
$resourceSource = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Resolve-Path -LiteralPath (Join-Path $Root $Original)).Path))
$typeByOldName = @{}
foreach ($t in $resourceSource.GetTypes()) { $typeByOldName[[string]$t.ReflectionFullName] = $t.MDToken.Raw }
$mod.Resources.Clear()
$resourceCount = 0; $resourcesRenamed = 0
foreach ($r in $resourceSource.Resources) {
    $embedded = $r -as [dnlib.DotNet.EmbeddedResource]
    if ($null -eq $embedded) { throw "resource '$($r.Name)' is not embedded: not handled" }
    $name = [string]$r.Name
    if ($name.EndsWith('.resources')) {
        $token = $typeByOldName[$name.Substring(0, $name.Length - '.resources'.Length)]
        if ($token) {
            $now = [string]$mod.ResolveToken([uint32]$token).ReflectionFullName + '.resources'
            if ($now -cne $name) { $name = $now; $resourcesRenamed++ }
        }
    }
    $mod.Resources.Add((New-Object dnlib.DotNet.EmbeddedResource([dnlib.DotNet.UTF8String]$name, $embedded.GetResourceData(), $r.Attributes)))
    $resourceCount++
}
$resourceSource.Dispose()
Write-Host ("resources: {0} from the pre-rename stage, {1} renamed after their type" -f $resourceCount, $resourcesRenamed)

$outFull = Join-Path $Root $Out
$opts = New-Object dnlib.DotNet.Writer.ModuleWriterOptions($mod)
$opts.MetaDataOptions.Flags = $opts.MetaDataOptions.Flags -bor [dnlib.DotNet.Writer.MetaDataFlags]::PreserveAll
$opts.Logger = [dnlib.DotNet.DummyLogger]::NoThrowInstance
$mod.Write($outFull, $opts)
$mod.Dispose()
Write-Host ("-> {0}" -f $outFull)
