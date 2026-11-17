# Contributing

Read [`LEGAL.md`](LEGAL.md) first. The repository is public and holds the tool, not the game's
source: no file of `src/`, no game binary or asset, no account data and no path of your
machine goes into a commit, an issue or a pull request. Names never come from leaked source
code. Tooling that gets around the game's anti-cheat, or that takes a rebuilt client online,
is not accepted.

Setup is in the README ("Work on the project"). What each part does is in
[`docs/pipeline.md`](docs/pipeline.md) and [`docs/matching.md`](docs/matching.md).

How people are expected to behave is in [`CODE_OF_CONDUCT.md`](CODE_OF_CONDUCT.md). A defect
of the tool that puts its users at risk is reported privately: [`SECURITY.md`](SECURITY.md).
Questions go to the discussions; issues are for defects, names and proposals. A pull request
that touches a file of [`.github/CODEOWNERS`](.github/CODEOWNERS) (the pins, the
fingerprints, the workflows, the legal notices) waits for the maintainer's review.

## What the CI checks, and what it cannot

Every pull request runs `.github/workflows/ci.yml`:

| Job | What it runs |
|---|---|
| Repository checks | `scripts\dev\check-repo.ps1 -RequireLib`: no tracked file of the game, the tools or a build; no user folder in a text; every `.ps1` ASCII-only and parsing in Windows PowerShell 5.1; the tables of `docs/` well-formed (columns, unique tokens); `game.sha256` and `src.sha256` well-formed; `scripts/lib/*.cs` compiling against dnlib. |
| app (Rust) | `cargo fmt --check`, `cargo clippy -D warnings`, `cargo test`, then `scripts\build-app.ps1` and `ochu-cli.exe help`. The two programs are attached to the run for seven days. |
| Commit messages | `scripts\dev\check-commits.ps1` on the commits of the pull request and on its title: see "Commits" below. |
| scripts/decompiler (.NET) | `dotnet build` of the decompiler, with the command of the pipeline. |

Run the first one before you push: `.\scripts\dev\check-repo.ps1`.

**The CI has no copy of the game, so it never runs the pipeline and never measures the
matching rate.** A green run says that the tool builds, not that `src/` is still the
reference source. That part is yours, on your machine, and the pull request template asks for
its result:

- A change that is not meant to change `src/`: after `scripts\pipeline.ps1`,
  `ochu-cli.exe src-manifest --verify` reports no difference.
- A change that is meant to change it: run `scripts\build.ps1`, then the five checks
  (`scripts\match.ps1`, `scripts\dev\strict-match.ps1`, `scripts\dev\outside-check.ps1`,
  `scripts\match-meta.ps1`, `scripts\dev\verify-check.ps1`), write the new fingerprint with
  `ochu-cli.exe src-manifest`, commit `src.sha256` with the change and paste the output of
  the five in the pull request.

The maintainer runs the same on the branch before merging: a pull request whose `src.sha256`
cannot be reproduced is not merged.

## Commits

These rules hold for everyone who commits, people and coding agents alike, and the CI
enforces the first three on every pull request (`scripts\dev\check-commits.ps1`, which you
can run before you push).

1. **Conventional subject**: `type(scope): text`, with a type of the table below. The scope
   is optional. `!` after the type (`fix(decompiler)!: ...`) marks a change that breaks a use.
2. **Short**: the subject is 72 characters at most and says what changed for someone who
   uses the tool; the release notes are written from it.
3. **No credit to a language model or an assistant**: no `Co-Authored-By` trailer and no
   "Generated with" line that names one. The author of a commit is the person who answers
   for it, whatever was used to write it.
4. **A body only when the subject cannot carry the reason**, and then a few lines. No list
   of the files touched, no account of the work: the diff and the pull request hold those.

| Type | Section of the notes | For |
|---|---|---|
| `feat` | Added | something the tool could not do |
| `fix` | Fixed | a defect of the tool, or a method that did not match |
| `names` | Naming | rows of `docs/names.tsv` |
| `perf`, `refactor`, `style` | Changed | same result, other way |
| `docs` | Documentation | |
| `ci`, `build`, `chore`, `test`, `deps` | Maintenance | |

Usual scopes: `app`, `pipeline`, `decompiler`, `devirt`, `match`, `build`. Pull requests are
merged squashed: their title is the subject, and it is checked the same way.

## Releases

A release is the two programs, built by GitHub from a tagged commit. For the maintainer:

1. Set the version in `app/Cargo.toml`, run `scripts\build-app.ps1` (it updates
   `app/Cargo.lock`), commit as `chore(release): X.Y.Z`.
2. Optional rehearsal: start the "Release" workflow by hand on `main`. It builds the archive
   and writes the notes of what is not released yet on the run's page, and publishes nothing.
3. `git tag vX.Y.Z` and push the tag. `.github/workflows/release.yml` checks that the tag is
   the version of `app/Cargo.toml`, runs the repository checks and the tests, builds with
   `scripts\build-app.ps1`, and publishes the release: `ochu-X.Y.Z-windows-x64.zip`
   (`ochu.exe`, `ochu-cli.exe`, the legal notices), `SHA256SUMS.txt`, a build provenance
   attestation, and the notes. A version with a suffix (`v1.2.0-rc.1`) is published as a
   pre-release.

The notes list the commits since the previous tag (`.github/cliff.toml`) and say whether
`src.sha256` changed, that is, whether this release generates another source than the
previous one. The workflow does not run the pipeline: tag a commit only after the five checks
and `src-manifest --verify` pass on it.
