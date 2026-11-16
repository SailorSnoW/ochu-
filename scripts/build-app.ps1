<#
.SYNOPSIS
    Builds the two programs of app/ (Rust): work/app/release/ochu.exe (the window) and
    work/app/release/ochu-cli.exe (the console, which runs the pipeline).

.DESCRIPTION
    Needs the Rust toolchain pinned in app/rust-toolchain.toml (rustup installs it on first
    use); the crates are pinned by app/Cargo.lock.
    The programs carry scripts/, build/, docs/, game.sha256 and src.sha256 as they are at
    build time: build again before a release.
    -IfStale builds only when a source of app/ is newer than the programs (what the other
    scripts call before they run ochu-cli.exe).

.EXAMPLE
    .\scripts\build-app.ps1
#>
[CmdletBinding()]
param(
    [switch]$IfStale
)

$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $PSScriptRoot
$App = Join-Path $Root 'app'
$Target = Join-Path $Root 'work\app'
$Cli = Join-Path $Target 'release\ochu-cli.exe'

if ($IfStale -and (Test-Path -LiteralPath $Cli)) {
    $built = (Get-Item -LiteralPath $Cli).LastWriteTimeUtc
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $App 'src') -Recurse -File) +
        @(Get-Item -LiteralPath (Join-Path $App 'Cargo.toml'), (Join-Path $App 'Cargo.lock'), (Join-Path $App 'build.rs'))
    if (-not ($sources | Where-Object { $_.LastWriteTimeUtc -gt $built })) { return }
}
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    throw 'cargo not found: install Rust with rustup (https://rustup.rs), then run this script again.'
}

$env:CARGO_TARGET_DIR = $Target
# The programs are published: no path of this machine in them (panic messages name the
# source files of the crates, which are under the user's folder).
$cargoHome = $env:CARGO_HOME
if (-not $cargoHome) { $cargoHome = Join-Path $env:USERPROFILE '.cargo' }
$rustupHome = $env:RUSTUP_HOME
if (-not $rustupHome) { $rustupHome = Join-Path $env:USERPROFILE '.rustup' }
$env:RUSTFLAGS = ('--remap-path-prefix={0}=cargo --remap-path-prefix={1}=rustup --remap-path-prefix={2}=ochu --remap-path-prefix={3}=home' -f $cargoHome, $rustupHome, $Root, $env:USERPROFILE)
Push-Location $App
try {
    $ErrorActionPreference = 'Continue'
    & cargo build --release --locked
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
} finally { Pop-Location }
if ($code -ne 0) { throw 'building app/ failed' }
if (-not $IfStale) { Write-Host ("Built {0} and ochu.exe" -f $Cli) }
