<#
.SYNOPSIS
    Read-only: the decompiled source of methods given by reference token.

.DESCRIPTION
    Finds the file of the top-level type in src/ and prints the declarations that carry
    the name of the method (overloads are all printed). -Find prints only the lines that
    match a pattern, with three lines around each.

.EXAMPLE
    .\scripts\dev\show-source.ps1 0x06000803 -Find 'CheckState = '
    .\scripts\dev\show-source.ps1 0x06002B80, 0x06000435 -Lines 20
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)][uint32[]]$Token,
    [int]$Lines = 40,
    [string]$Find = ''
)

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
[void][Reflection.Assembly]::LoadFrom((Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'))
$mod = [dnlib.DotNet.ModuleDefMD]::Load([IO.File]::ReadAllBytes((Join-Path $Root 'work\deobf\osu!.named.exe')))
try {
    foreach ($t in $Token) {
        $m = $mod.ResolveToken($t)
        if (-not $m) { "no method 0x{0:X8}" -f $t; continue }
        $top = $m.DeclaringType
        while ($top.IsNested) { $top = $top.DeclaringType }
        $rel = ([string]$top.Name -replace '`\d+$', '') + '.cs'
        if ([string]$top.Namespace -ne '') { $rel = Join-Path (([string]$top.Namespace) -replace '\.', '\') $rel }
        $file = Join-Path (Join-Path $Root 'src') $rel
        $name = [string]$m.Name
        if ($name -eq '.ctor' -or $name -eq '.cctor') { $name = ([string]$m.DeclaringType.Name -replace '`\d+$', '') }
        $name = $name -replace '^(get|set|add|remove)_', ''
        # A lambda is printed inside the method that creates it.
        if ($name -match '^<(.+)>[bg]__') { $name = $Matches[1] }
        "== 0x{0:X8} {1}  [{2}]" -f $t, $m.FullName, $rel
        if (-not (Test-Path -LiteralPath $file)) { '   file not found'; continue }
        $text = Get-Content -LiteralPath $file
        $decl = '^\s*(\[.*\]\s*)?((public|private|internal|protected|static|override|virtual|sealed|unsafe|extern|new|abstract)\s+)+[^=;(]*\b' + [regex]::Escape($name) + '\s*(<[^>]*>)?\s*(\(|$|\{)'
        for ($i = 0; $i -lt $text.Count; $i++) {
            if ($text[$i] -notmatch $decl) { continue }
            $indent = ($text[$i] -replace '^(\s*).*$', '$1')
            $end = $i
            for ($j = $i + 1; $j -lt $text.Count; $j++) { if ($text[$j] -eq ($indent + '}')) { $end = $j; break } }
            if ($Find -ne '') {
                for ($j = $i; $j -le $end; $j++) {
                    if ($text[$j] -notmatch $Find) { continue }
                    for ($k = [Math]::Max($i, $j - 3); $k -le [Math]::Min($end, $j + 3); $k++) { '{0,5}: {1}' -f ($k + 1), $text[$k] }
                    '   --'
                }
            } else {
                for ($j = $i; $j -le [Math]::Min($end, $i + $Lines - 1); $j++) { '{0,5}: {1}' -f ($j + 1), $text[$j] }
                if ($end -gt $i + $Lines - 1) { '   ... ({0} more lines)' -f ($end - $i - $Lines + 1) }
            }
        }
    }
} finally { $mod.Dispose() }
