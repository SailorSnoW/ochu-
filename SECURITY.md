# Security

## What to report here

A defect of the project's own work that puts the person who runs it at risk:

- a download that is used without the check of its pinned version and SHA-256
  (`app/src/tools.rs`, `scripts/setup-tools.ps1`);
- a stage that writes outside `work/` and `src/`, writes into the game folder, or starts the
  game;
- a game folder, or a file in it, crafted so that the tool runs something other than the
  documented stages;
- a workflow of `.github/workflows` that a pull request could use to publish, to read a
  secret or to change a release;
- a release whose files are not the ones built by the workflow from the tagged commit.

Only the latest release and `main` are fixed.

## What not to report here

Weaknesses of the osu! client, of its servers or of its anti-cheat are not this project's to
receive: they go to the game's publisher, and they are not discussed in this repository, in
an issue or anywhere else. The project takes no tooling that gets around the anti-cheat
([`LEGAL.md`](LEGAL.md)).

## How

Privately: the "Report a vulnerability" button of the repository's Security tab. Do not open
a public issue for it. Say what you ran, on which version or commit, and what happened; do
not attach game files or code from `src/`.

You get an answer within a week. A fix is released first, and the report is published after
it, with your name if you want it there.

## Checking a release

Each release carries `SHA256SUMS.txt` and a build provenance attestation:

```
gh attestation verify ochu-X.Y.Z-windows-x64.zip --repo SailorSnoW/ochu-
```
