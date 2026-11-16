<#
.SYNOPSIS
    Installs the pinned decompilation toolchain into tools/ (git-ignored).

.DESCRIPTION
    Idempotent. Downloads are cached in tools/_cache and verified against the
    SHA-256 pinned below before extraction. A tool whose folder already exists
    is skipped unless -Force is given.

.EXAMPLE
    .\scripts\setup-tools.ps1
#>
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Root  = Split-Path -Parent $PSScriptRoot
$Tools = Join-Path $Root 'tools'
$Cache = Join-Path $Tools '_cache'
New-Item -ItemType Directory -Force $Cache | Out-Null

# Name = folder under tools/. Sha256 empty => computed hash is printed, not checked.
$Manifest = @(
    @{ Name = 'de4dot-cex'; Version = '4.0.0'
       Url    = 'https://github.com/ViRb3/de4dot-cex/releases/download/v4.0.0/de4dot-cex.zip'
       Sha256 = 'c726cbd18b894ca63b7f6a565c6c86ef512b96e68119c6502cdf64a51f6a1c78' }
    # C# compiler candidates for the matching build (docs/matching.md). .nupkg is a zip;
    # csc.exe is under tools/roslyn-<version>/tools.
    @{ Name = 'roslyn-2.0.1'; Version = '2.0.1'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/2.0.1/microsoft.net.compilers.2.0.1.nupkg'
       Sha256 = 'f999cff196e8a186631958572a4ecedc63da16531f79120f7eecda1a84faaf48' }
    @{ Name = 'roslyn-2.3.2'; Version = '2.3.2'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/2.3.2/microsoft.net.compilers.2.3.2.nupkg'
       Sha256 = 'd35811b3ae7d05c586a3e076f43458f47e26f2c92442127a072fa6fbef07806b' }
    @{ Name = 'roslyn-2.6.1'; Version = '2.6.1'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/2.6.1/microsoft.net.compilers.2.6.1.nupkg'
       Sha256 = '2e38aa455b5a1acce260a8ea30696804e6fa66aa366a04118f0cb4c8d7d08cf9' }
    @{ Name = 'roslyn-2.8.2'; Version = '2.8.2'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/2.8.2/microsoft.net.compilers.2.8.2.nupkg'
       Sha256 = '96684975e6934859e3a9f42536d2ffeb63f8e5761eb38838d0604d4cc31e0b47' }
    @{ Name = 'roslyn-2.10.0'; Version = '2.10.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/2.10.0/microsoft.net.compilers.2.10.0.nupkg'
       Sha256 = '854d162cbe3c90100922c970ba593631f2d106f7b757a99425d50dc5cdafbdc0' }
    @{ Name = 'roslyn-3.0.0'; Version = '3.0.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.0.0/microsoft.net.compilers.3.0.0.nupkg'
       Sha256 = '4d6f35ca8f24cf985da3a1218b26c0adb8bbeeaea9bda7d7dbc295880b473fbd' }
    @{ Name = 'roslyn-3.7.0'; Version = '3.7.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.7.0/microsoft.net.compilers.3.7.0.nupkg'
       Sha256 = '5a6609688aacd4b5f783d023c5dd35bf6ca4948270b0dce6fa209bccf1604d93' }
    @{ Name = 'roslyn-3.11.0'; Version = '3.11.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.11.0/microsoft.net.compilers.3.11.0.nupkg'
       Sha256 = 'e4a540a9cb41d15e6e1cc05fa2cadb847454df9ebe49d683cded2780506ef880' }
    @{ Name = 'roslyn-4.2.0'; Version = '4.2.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/4.2.0/microsoft.net.compilers.4.2.0.nupkg'
       Sha256 = 'd1a8d866d6806202b2f1f5e21cb77839cef2606de9c0d9e6939a7e1f1baa4244' }
    @{ Name = 'roslyn-3.1.1'; Version = '3.1.1'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.1.1/microsoft.net.compilers.3.1.1.nupkg'
       Sha256 = '5778b0de07ccc04754b7904fd028fd73f4c98b918f13b3396832b8681304d58f' }
    @{ Name = 'roslyn-3.2.1'; Version = '3.2.1'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.2.1/microsoft.net.compilers.3.2.1.nupkg'
       Sha256 = '2de6443718525179c43ca7c39e8d312f8e5108589c32ff642db35a0e530a8a1f' }
    @{ Name = 'roslyn-3.3.1'; Version = '3.3.1'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.3.1/microsoft.net.compilers.3.3.1.nupkg'
       Sha256 = 'fcd861e86ddfdb2c70bb4b1a399b6e10c87ff35658a7cb5319fb01a1ccdfd3ac' }
    @{ Name = 'roslyn-3.4.0'; Version = '3.4.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.4.0/microsoft.net.compilers.3.4.0.nupkg'
       Sha256 = 'e442975d7fcf7b44e41addc4bff87596efba66da9278097d717f71e363c9e9dc' }
    @{ Name = 'roslyn-3.5.0'; Version = '3.5.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.5.0/microsoft.net.compilers.3.5.0.nupkg'
       Sha256 = '02fd52fcc75fa7a96b79b63d78b7f7b936deddb4322db1622997fe7c9bccbfc5' }
    @{ Name = 'roslyn-3.6.0'; Version = '3.6.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.6.0/microsoft.net.compilers.3.6.0.nupkg'
       Sha256 = 'f4068ac441e2b35bf00e679835eaa4b86173473f3d6a119ce86c283332efe705' }
    @{ Name = 'roslyn-3.8.0'; Version = '3.8.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.8.0/microsoft.net.compilers.3.8.0.nupkg'
       Sha256 = '9a588329e93779f2aae3955d1f61ea74b41d4a4a307f85cab5acf925f4751fc8' }
    @{ Name = 'roslyn-3.9.0'; Version = '3.9.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.9.0/microsoft.net.compilers.3.9.0.nupkg'
       Sha256 = 'fc2cd54cf3ec2bc14b8ceb22f08f8aff0ad20b43e60f9df4cf368931ae4cb9fb' }
    @{ Name = 'roslyn-3.10.0'; Version = '3.10.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers/3.10.0/microsoft.net.compilers.3.10.0.nupkg'
       Sha256 = '5e9b52ad92f8e927c9e62633a0628630691b7b6749d63e7994e46b3126fe3975' }
    @{ Name = 'roslyn-4.4.0'; Version = '4.4.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.4.0/microsoft.net.compilers.toolset.4.4.0.nupkg'
       Sha256 = '024edad4154b96c3be22551b641ed518fe57d98972c823f1330fc6827d480347' }
    @{ Name = 'roslyn-4.6.0'; Version = '4.6.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.6.0/microsoft.net.compilers.toolset.4.6.0.nupkg'
       Sha256 = 'd2df516a2d0b120ef30fd43c4d653dd9f83604621c9971ad78b2795760543e1f' }
    @{ Name = 'roslyn-4.3.0'; Version = '4.3.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.3.0/microsoft.net.compilers.toolset.4.3.0.nupkg'
       Sha256 = '5052b8104622f2c253035b104883819a2349f5fffac9ce491a487a31e3f3e926' }
    @{ Name = 'roslyn-4.7.0'; Version = '4.7.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.7.0/microsoft.net.compilers.toolset.4.7.0.nupkg'
       Sha256 = 'a6bf3677be34b56c720c03549d24d5e2afa4db39eebf3548cd35b72f48066dbf' }
    @{ Name = 'roslyn-4.8.0'; Version = '4.8.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.8.0/microsoft.net.compilers.toolset.4.8.0.nupkg'
       Sha256 = '37333f4f1e2ce55e621355d6da651dc23d4cb5f94a8f76b9478816e87f110ad9' }
    @{ Name = 'roslyn-4.9.2'; Version = '4.9.2'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.9.2/microsoft.net.compilers.toolset.4.9.2.nupkg'
       Sha256 = '97c63db1e2b6b52fb59b086d45709a8af6a99a9b21cd454a29d6bc6f8925deeb' }
    @{ Name = 'roslyn-4.10.0'; Version = '4.10.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.10.0/microsoft.net.compilers.toolset.4.10.0.nupkg'
       Sha256 = 'eb18b8b9fe5021aeaa1d7c7c1b5041269b171cf1151703e14e5429f0053f7cfb' }
    @{ Name = 'roslyn-4.11.0'; Version = '4.11.0'
       Url    = 'https://api.nuget.org/v3-flatcontainer/microsoft.net.compilers.toolset/4.11.0/microsoft.net.compilers.toolset.4.11.0.nupkg'
       Sha256 = 'db165c75f0d0386d078d01448f96c93f8222320633e4fe828f5d1bf8c99b7d6f' }
)

function Install-Tool($t) {
    $dest   = Join-Path $Tools $t.Name
    $marker = Join-Path $dest '.version'

    if (-not $Force -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $t.Version)) {
        Write-Host ("[skip] {0} {1}" -f $t.Name, $t.Version)
        return
    }

    $archive = Join-Path $Cache (Split-Path -Leaf $t.Url)
    if (-not (Test-Path $archive)) {
        Write-Host ("[get ] {0} {1}" -f $t.Name, $t.Version)
        $tmp = "$archive.part"
        Invoke-WebRequest -Uri $t.Url -OutFile $tmp -UseBasicParsing
        Move-Item -Force $tmp $archive
    }

    $hash = (Get-FileHash -Algorithm SHA256 $archive).Hash.ToLowerInvariant()
    if ($t.Sha256) {
        if ($hash -ne $t.Sha256.ToLowerInvariant()) {
            throw ("SHA-256 mismatch for {0}: expected {1}, got {2}. Delete {3} and retry." -f $t.Name, $t.Sha256, $hash, $archive)
        }
    } else {
        Write-Warning ("{0}: no pinned hash, computed sha256 = {1}" -f $t.Name, $hash)
    }

    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Force $dest | Out-Null
    # bsdtar (ships with Windows) reads zip and is much faster than Expand-Archive
    & tar.exe -xf $archive -C $dest
    if ($LASTEXITCODE -ne 0) { throw "Extraction failed for $($t.Name)" }

    Set-Content -Path $marker -Value $t.Version -Encoding ascii
    Write-Host ("[ ok ] {0} {1}" -f $t.Name, $t.Version)
}

foreach ($t in $Manifest) { Install-Tool $t }

Write-Host 'Done.'
