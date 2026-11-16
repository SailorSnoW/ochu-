<#
.SYNOPSIS
    Regenerates everything under work/ and src/ from the pristine game/ copy.

.DESCRIPTION
    The pipeline itself is in app/src/pipeline.rs (ochu-cli.exe pipeline): seven steps, described
    in docs/pipeline.md: copy, strings, clean, rename, names, decomp, src. This script builds
    that program when needed (scripts/build-app.ps1) and passes its arguments on, so that
    the other scripts and the documents keep one command.

.EXAMPLE
    .\scripts\pipeline.ps1
    .\scripts\pipeline.ps1 -From names    # after editing docs/names.tsv
#>
[CmdletBinding()]
param(
    [ValidateSet('copy', 'strings', 'clean', 'rename','names', 'decomp', 'src')][string]$From = 'copy',
    # The three below are for scripts/tune-decompiler.ps1: a trial decompilation with one
    # more decompiler option, written somewhere else than work/decomp/raw and src/.
    [string[]]$With = @(),
    [string]$RawDir = '',
    [string]$SrcDir = '',
    # Another table than docs/decompiler-hints.tsv (trial of an option on some methods only).
    [string]$HintsFile = '',
    # Trial mode: a fixup that no longer applies is reported, not fatal.
    [switch]$LenientFixups
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build-app.ps1') -IfStale

$arguments = @('pipeline', '--root', $Root, '--from', $From)
foreach ($w in $With) { $arguments += '--with'; $arguments += $w }
if ($RawDir -ne '') { $arguments += '--raw-dir'; $arguments += $RawDir }
if ($SrcDir -ne '') { $arguments += '--src-dir'; $arguments += $SrcDir }
if ($HintsFile -ne '') { $arguments += '--hints-file'; $arguments += $HintsFile }
if ($LenientFixups) { $arguments += '--lenient-fixups' }

$ErrorActionPreference = 'Continue'
& (Join-Path $Root 'work\app\release\ochu-cli.exe') @arguments 2>&1 | ForEach-Object { Write-Host "$_" }
$code = $LASTEXITCODE
if ($code -ne 0) { Write-Host 'pipeline failed'; exit $code }
exit 0
