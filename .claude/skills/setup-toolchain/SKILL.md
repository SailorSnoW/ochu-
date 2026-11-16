---
name: setup-toolchain
description: Set up or repair the ochu!! decompilation toolchain on a machine (prerequisites, game/ copy, pinned tools, full pipeline run) and prove it works by reproducing the documented matching rate. Use on a fresh clone, on a new Windows or macOS machine, or when pipeline.ps1, build.ps1 or match.ps1 fail for environment reasons.
---

# Set up the ochu!! toolchain

The setup is finished when this machine reproduces the repository, not when the installers have
run. Three results prove it:

1. `scripts/pipeline.ps1` runs from `game/` to `src/`, and
   `work\app\release\ochu-cli.exe src-manifest --verify` reports no difference: `src/` is not
   in git, `src.sha256` is the fingerprint of the one the reference machine generates.
2. `scripts/build.ps1` prints `Build succeeded`.
3. `scripts/match.ps1` prints the matching rate given in the "Status" line of `README.md`.

Anything else means the machine differs from the reference one. Find out why before any other
work: a matching decompilation is only worth what its measurements are.

This is the setup of a working checkout. Someone who only wants the source needs none of
it: `ochu.exe` (`README.md`, "Get the source") does the whole run by itself.

Read `CLAUDE.md` first. Its hard rules apply during the setup too, in particular: `game/` is
read-only, tools are installed by `scripts/setup-tools.ps1` and nothing else, `src/` is never
edited by hand.

## Platform

The toolchain runs on **Windows 10 or 11** only. Three parts hold it there:

- `scripts/decrypt-strings.ps1` executes the game's own string decryptor in a 32-bit Windows
  PowerShell (`SysWOW64`), because `osu!.exe` is an x86 .NET Framework assembly.
- The scripts are written for Windows PowerShell 5.1 and load `dnlib` from `tools/de4dot-cex`
  through `Add-Type`. They have never been run under PowerShell 7.
- `app/` (Rust) is built for Windows only: it starts those scripts and reads the registry.
- `de4dot.exe` and the old `csc.exe` builds used by `scripts/try-compiler.ps1` are .NET
  Framework programs.

**On a Mac**, do not try to port the scripts as part of a setup. Tell the user that the toolchain
needs Windows and offer the two options that work:

- A Windows 11 virtual machine (UTM, Parallels, VMware Fusion). Clone the repository inside the
  virtual machine, on its own disk and not on a folder shared with macOS, and follow the Windows
  steps below there. On Apple silicon the virtual machine runs Windows for ARM, which emulates
  x86 programs. This project has not been run that way: the proof above tells whether it works.
- No toolchain at all. Reading `docs/` and `scripts/` needs only Git; `src/` is not in the repository and can only be generated on Windows. Edits
  to `docs/names.tsv` or to the decompiler can be written on the Mac, but nothing can be measured
  there: say so, and do not report such a change as verified.

## Steps on Windows

Run the commands from the repository root, in Windows PowerShell. If the execution policy blocks a
script, run it as `powershell -ExecutionPolicy Bypass -File <script>`; do not change the machine's
policy.

### 1. See what is already there

```powershell
git --version
dotnet --list-sdks
cargo --version
Test-Path game, tools, work
```

Skip every step below whose result is already in place. All the scripts are idempotent.

### 2. Prerequisites

| Needed | Why | Install |
|---|---|---|
| Git | the repository | `winget install --id Git.Git -e` |
| .NET SDK 8.0.4xx | `global.json` asks for 8.0.427 or a later 8.0 feature band | `winget install --id Microsoft.DotNet.SDK.8 -e` |
| Rust (rustup) | builds `app/`, which runs the pipeline; `app/rust-toolchain.toml` pins the version and rustup fetches it on the first build | `winget install --id Rustlang.Rustup -e` |
| Windows PowerShell 5.1, .NET Framework 4.x | the scripts, de4dot, the string decryptor; `build.ps1` also runs its `csc.exe` and `ilasm.exe` (`%WINDIR%\Microsoft.NET\Framework\v4.0.30319`) for two compilation units of `docs/units.tsv` | part of Windows |

These are system-wide installations: ask the user before running them. Open a new shell
afterwards so that `PATH` is up to date. `dotnet --version`, run in the repository root, must
print an 8.0.4xx version; an error about `global.json` means the SDK is missing or too old.

About 4 GB of free disk space are needed: 1 GB for `tools/`, the rest for `work/` and the NuGet
cache.

### 3. The game copy

`game/` is not in the repository and never will be. Only the user can provide it: their own
copy of the reference build (`README.md`), put there by their own means. Do not look for the
game on the internet, and do not accept another build: the tokens in `docs/names.tsv` and in
the other tables only mean something for this exact `osu!.exe`.

The game's DLLs carry the *hidden* attribute. A copy made with Explorer's default view, or an
archive made with `Compress-Archive`, leaves them out. `robocopy <source> game /E` and
`tar -a -cf game.zip game` keep them.

```powershell
.\scripts\game-manifest.ps1 -Verify -Core   # the six assemblies the pipeline reads
.\scripts\game-manifest.ps1 -Verify         # every binary of the build, third-party libraries included
```

`-Verify -Core` must pass. If it reports `MISSING` or `MODIFIED`, stop and tell the user which
files: nothing downstream can be trusted. Never run `game-manifest.ps1` without `-Verify` on a
new machine: that rewrites `game.sha256` from whatever is in `game/`.

### 4. The tools

```powershell
.\scripts\setup-tools.ps1
```

It downloads each pinned
archive from github.com and api.nuget.org into `tools/_cache`, checks its SHA-256 and extracts it
under `tools/`. A tool already there at the right version is skipped.

- A SHA-256 mismatch stops the script. Delete the named archive and run it again once. If it
  fails again, stop and report it: do not empty the pinned hash, and do not fetch the tool another
  way.
- If `tools/de4dot-cex/de4dot.exe` is missing after a run without errors, an
  antivirus has probably removed it. Tell the user; an exclusion for `tools/` is their decision.
### 5. Run everything once

```powershell
.\scripts\build-app.ps1   # app/ -> work/app/release; the first run downloads the crates
.\scripts\pipeline.ps1    # about 2 minutes; game/ -> work/ -> src/
.\work\app\release\ochu-cli.exe src-manifest --verify   # "OK: 1810 source files match"
.\scripts\build.ps1       # "Build succeeded"; the first run also restores NuGet packages
.\scripts\match.ps1       # the line that starts with ALL gives the rate
```

Compare the rate with the "Status" line of `README.md`. Then report to the user: the versions
found, what was installed, the rate obtained, and anything that differed.

## When the proof fails

- **`src-manifest --verify` lists differences after the pipeline.** The tools or the game copy
  differ from the reference. Check `game-manifest.ps1 -Verify -Core`, `dotnet --version`, and
  the `.version` files under `tools/`. Do not write a new `src.sha256` to make the check pass:
  that file changes only with a change of the tables or of the decompiler.
- **`build-app.ps1` fails.** Read cargo's output: a missing linker means the Visual Studio
  Build Tools (C++ workload) rustup asks for are not installed.
- **`decrypt-strings.ps1` fails.** It needs `%WINDIR%\SysWOW64\WindowsPowerShell\v1.0\powershell.exe`
  and a .NET Framework 4.x that can load x86 code. Its error output names the step.
- **The build fails on a machine where `src-manifest --verify` passes.** It is a restore or
  SDK problem, not a source problem: read `work/build/build.log`. The build downloads
  `Microsoft.Net.Compilers.Toolset` 3.11.0 and the .NET Framework 4.0 reference assemblies from
  nuget.org.
- **`try-compiler.ps1` cannot find the reference assemblies.** It reads them from the NuGet
  cache: run `build.ps1` once before it.
- **The rate differs from the README.** `docs/matching.md` gives the same numbers with their
  date. If this machine gives another one, treat it like the first case.

## What does not travel with the repository

- `game/`, `tools/`, `work/` and `src/` are git-ignored. `tools/`, `work/` and `src/` are
  rebuilt by the steps above; `game/` has to be carried over.
- Claude Code's memory of this project is kept per machine, outside the repository. What a new
  machine needs to know is in `CLAUDE.md` and `docs/`; `docs/matching.md` holds the method of
  the matching work.
