<#
.SYNOPSIS
    Checks of the repository that need no copy of the game: what the CI runs on every pull
    request, and what a contributor can run before opening one.

.DESCRIPTION
    - no tracked file under game/, tools/, work/ or src/, no binary or account file;
    - no tracked text names a user folder of a machine;
    - every .ps1 is ASCII-only and parses in Windows PowerShell 5.1;
    - the tables of docs/ have their columns, and unique well-formed tokens where the first
      column is a token;
    - game.sha256 and src.sha256 are well-formed;
    - scripts/lib/*.cs compiles against dnlib (when tools/de4dot-cex is installed; -RequireLib
      makes its absence a failure).

    It does not say anything about the matching rate: that needs the game, see
    CONTRIBUTING.md.

.EXAMPLE
    .\scripts\dev\check-repo.ps1
#>
[CmdletBinding()]
param(
    [switch]$RequireLib
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$script:Failures = 0

function Fail([string]$message) {
    $script:Failures++
    Write-Host ("FAIL  {0}" -f $message)
}
function Pass([string]$message) { Write-Host ("ok    {0}" -f $message) }

$tracked = @(& git -C $Root ls-files)
if ($LASTEXITCODE -ne 0 -or $tracked.Count -eq 0) { throw 'git ls-files failed: run this from a clone of the repository' }

# --- Nothing of the game, of a tool or of a build in git -----------------------------------
$before = $script:Failures
foreach ($file in $tracked) {
    if ($file -match '^(game|tools|work|src)/') { Fail ("{0}: tracked file under an ignored folder" -f $file) }
    elseif ($file -match '\.(exe|dll|pdb|db|osr|osz|osk|resources|snk)$' -or $file -match '(^|/)osu![^/]*\.cfg$') {
        Fail ("{0}: binary or account file" -f $file)
    }
}
if ($script:Failures -eq $before) { Pass ("{0} tracked files, none from the game, the tools or a build" -f $tracked.Count) }

# --- No user folder of a machine -----------------------------------------------------------
$before = $script:Failures
$userFolder = New-Object regex '(?i)[a-z]:[\\/]+Users[\\/]+[a-z0-9]'
foreach ($file in $tracked) {
    if ($file -match '\.(png|jpg|ico)$') { continue }
    $path = Join-Path $Root $file
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $text = [IO.File]::ReadAllText($path)
    $found = $userFolder.Match($text)
    if ($found.Success) {
        $line = ($text.Substring(0, $found.Index) -split "`n").Count
        Fail ("{0}:{1}: names a user folder" -f $file, $line)
    }
}
if ($script:Failures -eq $before) { Pass 'no tracked text names a user folder' }

# --- PowerShell: ASCII-only, parses --------------------------------------------------------
$before = $script:Failures
$scripts = @($tracked | Where-Object { $_ -match '\.ps1$' })
foreach ($file in $scripts) {
    $path = Join-Path $Root $file
    $bytes = [IO.File]::ReadAllBytes($path)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        if ($bytes[$i] -gt 127) {
            $line = 1
            for ($j = 0; $j -lt $i; $j++) { if ($bytes[$j] -eq 10) { $line++ } }
            Fail ("{0}:{1}: non-ASCII byte (Windows PowerShell 5.1 reads .ps1 files as ANSI)" -f $file, $line)
            break
        }
    }
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    foreach ($e in $errors) {
        Fail ("{0}:{1}: {2}" -f $file, $e.Extent.StartLineNumber, $e.Message)
    }
}
if ($script:Failures -eq $before) { Pass ("{0} .ps1 files are ASCII-only and parse" -f $scripts.Count) }

# --- Tables --------------------------------------------------------------------------------
# Columns of each table; Token: the first column is a metadata token, one row per token.
$tables = @(
    @{ File = 'docs/names.tsv';            Columns = 3; Token = $true }
    @{ File = 'docs/decompiler-hints.tsv'; Columns = 2; Token = $true }
    @{ File = 'docs/units.tsv';            Columns = 3; Token = $true }
    @{ File = 'docs/fixups.tsv';           Columns = 4; Token = $false }
    @{ File = 'docs/vm-block-keys.tsv';    Columns = 4; Token = $false }
    @{ File = 'docs/vm-opcodes.tsv';       Columns = 9; Token = $false }
)
foreach ($table in $tables) {
    $before = $script:Failures
    $path = Join-Path $Root $table.File
    if (-not (Test-Path -LiteralPath $path)) { Fail ("{0}: missing" -f $table.File); continue }
    $seen = @{}
    $rows = 0
    $number = 0
    foreach ($row in [IO.File]::ReadAllLines($path)) {
        $number++
        if ($row -eq '' -or $row.StartsWith('#')) { continue }
        $rows++
        $cells = $row.Split("`t")
        if ($cells.Length -ne $table.Columns) {
            Fail ("{0}:{1}: {2} columns, {3} expected" -f $table.File, $number, $cells.Length, $table.Columns)
            continue
        }
        if ($table.Token) {
            $token = $cells[0]
            if ($token -cnotmatch '^0x[0-9A-F]{8}$') { Fail ("{0}:{1}: '{2}' is not a token (0x, eight upper-case hex digits)" -f $table.File, $number, $token) }
            elseif ($seen.ContainsKey($token)) { Fail ("{0}:{1}: token {2} already at line {3}" -f $table.File, $number, $token, $seen[$token]) }
            else { $seen[$token] = $number }
        }
    }
    if ($script:Failures -eq $before) { Pass ("{0}: {1} rows" -f $table.File, $rows) }
}

# --- Fingerprints --------------------------------------------------------------------------
foreach ($file in 'game.sha256', 'src.sha256') {
    $before = $script:Failures
    $path = Join-Path $Root $file
    if (-not (Test-Path -LiteralPath $path)) { Fail ("{0}: missing" -f $file); continue }
    $seen = @{}
    $number = 0
    foreach ($row in [IO.File]::ReadAllLines($path)) {
        $number++
        if ($row -cnotmatch '^[0-9a-f]{64} \*(\S.*)$') { Fail ("{0}:{1}: not '<sha-256> *<file>'" -f $file, $number); continue }
        $name = $Matches[1]
        if ($name -match '(^|[\\/])\.\.([\\/]|$)' -or $name -match '^[\\/]' -or $name -match ':') { Fail ("{0}:{1}: path leaves the folder" -f $file, $number) }
        elseif ($seen.ContainsKey($name)) { Fail ("{0}:{1}: {2} listed twice" -f $file, $number, $name) }
        else { $seen[$name] = $true }
    }
    if ($script:Failures -eq $before) { Pass ("{0}: {1} files" -f $file, $number) }
}

# --- scripts/lib compiles ------------------------------------------------------------------
$dnlib = Join-Path $Root 'tools\de4dot-cex\bin\dnlib.dll'
if (Test-Path -LiteralPath $dnlib) {
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $Root 'scripts\lib') -Filter '*.cs' | ForEach-Object { $_.FullName })
    [void][Reflection.Assembly]::LoadFrom($dnlib)
    try {
        # dnlib redefines ExtensionAttribute: the compiler warning is expected.
        Add-Type -IgnoreWarnings -WarningAction SilentlyContinue -ReferencedAssemblies $dnlib -Path $sources
        Pass ("scripts/lib: {0} files compile against dnlib" -f $sources.Count)
    } catch {
        Fail 'scripts/lib does not compile:'
        $_.Exception.Message -split "`n" | Select-Object -First 30 | ForEach-Object { Write-Host ("      {0}" -f $_.TrimEnd()) }
    }
} elseif ($RequireLib) {
    Fail 'scripts/lib not compiled: tools/de4dot-cex is missing (scripts/setup-tools.ps1 -Only de4dot-cex)'
} else {
    Write-Host 'skip  scripts/lib not compiled: tools/de4dot-cex is missing (scripts/setup-tools.ps1)'
}

if ($script:Failures -gt 0) {
    Write-Host ''
    Write-Host ("{0} problem(s)." -f $script:Failures)
    exit 1
}
Write-Host ''
Write-Host 'Repository checks passed. The matching rate is not part of them (CONTRIBUTING.md).'
