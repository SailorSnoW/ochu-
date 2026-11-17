# ochu!!

Archival decompilation of the osu! stable client (reference build 1.3.3.8). See README.md for layout and toolchain.

Goal: a **matching** decompilation. `src/`, recompiled, must give back the original IL for every
method. Readability and naming are secondary. See `docs/matching.md`.

What is published is the tool, not the source: `src/` is generated on each machine from the
user's own copy of the game and is git-ignored. `src.sha256` (file names and SHA-256, no code)
is the fingerprint of the reference `src/`.

- `app/` (Rust) is the program users run: `ochu.exe` (window) and `ochu-cli.exe` (console),
  built by `scripts/build-app.ps1` into `work/app/release`. It finds and checks the game copy,
  downloads the tools pinned in `app/src/tools.rs`, runs the pipeline (`app/src/pipeline.rs`,
  `srcstep.rs`) and checks `src/` against `src.sha256`. It carries `scripts/`, `build/` and
  `docs/` inside it: build it again before a release. New orchestration goes there, in Rust;
  the stages that load `dnlib`, run the game's decryptor or are the decompiler stay in
  `scripts/`. It never starts the game.
- `scripts/pipeline.ps1` only runs `ochu-cli.exe pipeline`. After it,
  `ochu-cli.exe src-manifest --verify` must report no difference. When a change is meant to
  change `src/`, run the five checks below, then `ochu-cli.exe src-manifest` to write the new
  fingerprint, and commit it with the change.

- `scripts/build.ps1` recompiles `src/` into `work/build` and links one binary, `work/build/linked/osu!.exe`; `scripts/match.ps1` reports its matching rate.
- The rate covers method bodies read through equivalent encodings. `scripts/dev/strict-match.ps1` says how many methods match with no reading at all, `scripts/dev/outside-check.ps1` cross-checks that with ILSpy's disassembler, `scripts/match-meta.ps1` compares declarations and resources, `scripts/dev/verify-check.ps1` loads the linked build without running it and compares PEVerify's verdict with the original's. Run the five after a change, and state what each does not cover whenever the rate is reported ("What the rate does not cover" in `docs/matching.md`).
- The rebuilt program has only been run offline, in an isolated copy with no account; the repository keeps no tooling for those runs. Launching it (or the original) connects to the osu! servers with the user's account: never do it without telling the user first, and never take the rebuild online.
- Compiler: Roslyn 3.11.0, pinned in `build/osu.csproj`; `src/` is decompiled as C# 7.3. `scripts/try-compiler.ps1` compiles a tree with any other pinned version.
- IL reference is `work/deobf/osu!.clean.exe`: the original IL with strings decrypted (`osu!.strings.exe`), the virtualized methods written back as CIL (`scripts/devirt`) and the obfuscator's own code edits undone (`scripts/undo-obfuscator.ps1 -Devirtualize`), see `docs/pipeline.md`. `osu!.named.exe` has the same IL with the names: `match.ps1 -Candidate` on it must report 100%.
- Encrypted blocks of the virtual code are decrypted with the keys of `docs/vm-block-keys.tsv`, written by `scripts/devirt/find-block-keys.ps1`: never by hand.
- Decompilation goes through `scripts/decompiler` (ILSpy engine plus IL-faithful transforms). The transforms every method takes are listed in `scripts/decompiler/transforms.txt`. Per-method options live in `docs/decompiler-hints.tsv`, written by `scripts/tune-decompiler.ps1`: never by hand.
- The binary merges several compilations. `docs/units.tsv` gives the obfuscator's runtime types to units built apart by `build.ps1` and linked into the main build by `scripts/link.ps1`: `debugplus` (same compiler, `/debug+`), `csc40` (the .NET Framework's `csc.exe`), and `il` for the few methods with no C# form (`src/il/unit.il`, written by `scripts/make-il-unit.ps1`, assembled by `ilasm`). A type goes to a unit only when every one of its methods matches there; a method goes to `il` only when no C# compiles to it, with the reason in the table. Report the IL methods apart from the decompiled ones.

## Hard rules

- `game/` is the pristine reference copy: read-only. Never write, rename, or run in-place tools there. Copy to `work/` first.
  Check its binaries with `scripts/game-manifest.ps1 -Verify` (`-Verify -Core`: the six assemblies the pipeline reads).
- Never commit game binaries, the game's source or account data (`game/`, `tools/`, `work/`, `src/` are git-ignored; keep it that way). No file of `src/` goes into git, a document, an issue or a release.
- The repository is public. Nothing that names an account, a machine or a user folder goes into it, and no tooling whose purpose is to get around the game's anti-cheat.
- `LEGAL.md` is the legal notice and `THIRD-PARTY-NOTICES.md` lists the libraries merged into the binary: keep both true. A library newly identified in `docs/names.tsv` gets its line there. Names never come from leaked source code. `LICENSE` (MIT) covers the project's own work only (`app/`, `scripts/`, `build/`, `docs/`); no license is granted on `src/`: never add a license file or header that covers it.
- `work/` holds regenerable scratch output; `src/` holds the decompiled source.
- `src/` is generated by `scripts/pipeline.ps1`, which empties it first: never edit it by hand.
  Fix names in `docs/names.tsv` (see "Naming" in `docs/pipeline.md`), then run `scripts/pipeline.ps1 -From names`.
  Source-level fixes the decompiler cannot get right go in `docs/fixups.tsv` (one-line replacements, `-From src`).
- CI (`.github/workflows/ci.yml`) has no copy of the game: it runs `scripts/dev/check-repo.ps1`, `cargo fmt`/`clippy`/`test` and the builds of `app/` and `scripts/decompiler`, never the pipeline or the five checks. A green run says nothing about `src/`. Actions are pinned by commit.
- A release is the tag `vX.Y.Z` matching `app/Cargo.toml` (`.github/workflows/release.yml`, "Releases" in `CONTRIBUTING.md`); its notes come from the commit subjects (`type(scope): text`, `.github/cliff.toml`). Pushing a tag publishes: ask first.
- Tools are pinned in `scripts/setup-tools.ps1` (version + SHA-256), `scripts/decompiler/Decompile.csproj` (the decompiler engine), `app/src/tools.rs` (the two downloads of the program), `app/rust-toolchain.toml` and `app/Cargo.lock`. Do not install ad hoc.

## Environment notes

- New machine, or a toolchain that looks broken: follow the `setup-toolchain` skill (`.claude/skills/setup-toolchain`).
- Windows only: the scripts need Windows PowerShell 5.1 and the 32-bit .NET Framework.
- Shell is Windows PowerShell 5.1: keep `.ps1` files ASCII-only, no `&&`.
- File names contain `!` (`osu!.exe`): use `-LiteralPath` and quote paths.
- `osu!.exe` is x86 .NET: a 32-bit process is needed to load it.
- .NET SDK 8 only: ICSharpCode.Decompiler is pinned to 9.1.x (10+ needs a newer runtime).
