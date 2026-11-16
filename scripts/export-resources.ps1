<#
.SYNOPSIS
    Writes the managed resources of the names stage as files for the build, with the
    MSBuild items that embed them under their names. See docs/pipeline.md.

.DESCRIPTION
    Input  : work/deobf/osu!.named.exe
    Output : work/resources/build/<n>.bin and work/resources/build/resources.props,
             imported by build/osu.csproj; work/resources/build/renames.tsv for the names
             MSBuild cannot carry (scripts/link.ps1 gives them); work/resources/build/win32.res,
             the Win32 resources (icons, version, manifest) for the compiler.

    The resources are game data: they stay under work/ and are never versioned. Each one
    is embedded as it is in the reference, byte for byte, under the same name and with the
    same visibility.

.EXAMPLE
    .\scripts\export-resources.ps1
#>
[CmdletBinding()]
param(
    [string]$In = 'work\deobf\osu!.named.exe',
    [string]$OutDir = 'work\resources\build'
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$inFull = (Resolve-Path -LiteralPath (Join-Path $Root $In)).Path
$gameDir = (Join-Path $Root 'game') + '\'
if ($inFull.StartsWith($gameDir, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to open a file under game/.' }
$outFull = Join-Path $Root $OutDir
if (Test-Path -LiteralPath $outFull) { Remove-Item -Recurse -Force -LiteralPath $outFull }
New-Item -ItemType Directory -Force $outFull | Out-Null
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))

function Escape-Xml([string]$s) {
    # Every character outside plain ASCII as a numeric reference: some names are made of
    # Unicode spaces only.
    $sb = New-Object Text.StringBuilder
    foreach ($ch in $s.ToCharArray()) {
        $code = [int]$ch
        if ($ch -eq '&') { [void]$sb.Append('&amp;') }
        elseif ($ch -eq '<') { [void]$sb.Append('&lt;') }
        elseif ($ch -eq '>') { [void]$sb.Append('&gt;') }
        elseif ($ch -eq '"') { [void]$sb.Append('&quot;') }
        elseif ($code -lt 32 -or $code -gt 126) { [void]$sb.Append(('&#x{0:X};' -f $code)) }
        else { [void]$sb.Append($ch) }
    }
    return $sb.ToString()
}

$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes($inFull))
$items = New-Object System.Collections.Generic.List[string]
$renamed = New-Object System.Collections.Generic.List[string]
$n = 0
try {
    foreach ($r in $mod.Resources) {
        $embedded = $r -as [dnlib.DotNet.EmbeddedResource]
        if ($null -eq $embedded) { throw "resource '$($r.Name)' is not embedded: not handled" }
        $file = Join-Path $outFull ("{0}.bin" -f $n)
        [IO.File]::WriteAllBytes($file, $embedded.GetResourceData())
        $access = 'public'
        if (($r.Attributes -band [dnlib.DotNet.ManifestResourceAttributes]::VisibilityMask) -eq [dnlib.DotNet.ManifestResourceAttributes]::Private) { $access = 'private' }
        # MSBuild takes a name made of white space only for no name: such a resource is
        # embedded under a stand-in and named by the link step (renames.tsv).
        $logical = [string]$r.Name
        if ($logical.Trim() -eq '') {
            $logical = 'ochu.resource.{0}' -f $n
            $renamed.Add($logical + "`t" + ((([string]$r.Name).ToCharArray() | ForEach-Object { '{0:X4}' -f [int]$_ }) -join ' '))
        }
        $items.Add(('    <EmbeddedResource Include="$(MSBuildThisFileDirectory){0}.bin" LogicalName="{1}" Access="{2}" WithCulture="false" Type="Non-Resx" />' -f $n, (Escape-Xml $logical), $access))
        $n++
    }
    # The Win32 resources (icons, version, manifest) as a .res file for the compiler
    # (/win32res): the resource section of the reference, entry for entry.
    $win32 = 0
    if ($null -ne $mod.Win32Resources) {
        $res = New-Object IO.MemoryStream
        $bw = New-Object IO.BinaryWriter($res)
        function Write-Id($writer, $name) {
            # An ordinal is 0xFFFF then the number; a name is UTF-16, null-terminated.
            if ($name.HasId) { $writer.Write([uint16]0xFFFF); $writer.Write([uint16]$name.Id) }
            else { $writer.Write([Text.Encoding]::Unicode.GetBytes([string]$name.Name)); $writer.Write([uint16]0) }
        }
        function Get-IdSize($name) { if ($name.HasId) { return 4 } else { return 2 * ([string]$name.Name).Length + 2 } }
        # A .res file starts with an empty entry.
        $bw.Write([uint32]0); $bw.Write([uint32]32); $bw.Write([uint16]0xFFFF); $bw.Write([uint16]0); $bw.Write([uint16]0xFFFF); $bw.Write([uint16]0)
        $bw.Write([uint32]0); $bw.Write([uint16]0); $bw.Write([uint16]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        foreach ($type in $mod.Win32Resources.Root.Directories) {
            foreach ($name in $type.Directories) {
                foreach ($lang in $name.Data) {
                    $reader = $lang.Data
                    $reader.Position = 0
                    $bytes = $reader.ReadBytes([int]$reader.Length)
                    $ids = (Get-IdSize $type.Name) + (Get-IdSize $name.Name)
                    $pad = (4 - ($ids % 4)) % 4
                    $bw.Write([uint32]$bytes.Length)
                    $bw.Write([uint32](8 + $ids + $pad + 16))
                    Write-Id $bw $type.Name
                    Write-Id $bw $name.Name
                    for ($k = 0; $k -lt $pad; $k++) { $bw.Write([byte]0) }
                    $bw.Write([uint32]0)                       # data version
                    $bw.Write([uint16]0x1030)                  # memory flags: moveable, pure, discardable
                    $bw.Write([uint16]$lang.Name.Id)           # language
                    $bw.Write([uint32]0); $bw.Write([uint32]0) # version, characteristics
                    $bw.Write($bytes)
                    for ($k = 0; $k -lt ((4 - ($bytes.Length % 4)) % 4); $k++) { $bw.Write([byte]0) }
                    $win32++
                }
            }
        }
        $bw.Flush()
        [IO.File]::WriteAllBytes((Join-Path $outFull 'win32.res'), $res.ToArray())
    }
} finally { $mod.Dispose() }

$props = New-Object System.Collections.Generic.List[string]
$props.Add('<Project>')
$props.Add('  <!-- Written by scripts/export-resources.ps1: the resources of the reference, in its order. -->')
$props.Add('  <ItemGroup>')
$props.AddRange($items)
$props.Add('  </ItemGroup>')
$props.Add('</Project>')
[IO.File]::WriteAllLines((Join-Path $outFull 'resources.props'), $props, (New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllLines((Join-Path $outFull 'renames.tsv'), $renamed, (New-Object Text.UTF8Encoding($false)))
Write-Host ("      {0} resources for the build -> {1} ({2} under a stand-in name), {3} Win32 resources" -f $n, $outFull, $renamed.Count, $win32)
