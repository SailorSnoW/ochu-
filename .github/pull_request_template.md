<!-- The title becomes the commit subject and the line of the release notes: "type(scope): what changed",
     72 characters at most. No "Generated with" line or co-author credit for a language model (CONTRIBUTING.md). -->

## What and why


## Does it change the generated `src/`?

<!-- A change to docs/*.tsv, scripts/ (pipeline stages, decompiler) or build/ usually does.
     The CI has no copy of the game: it cannot check this, you can. -->

- [ ] No: `ochu-cli.exe src-manifest --verify` reports no difference after `scripts\pipeline.ps1`.
- [ ] Yes: `src.sha256` is updated in this pull request, and the five checks were run. Their output:

```
match.ps1:
dev\strict-match.ps1:
dev\outside-check.ps1:
match-meta.ps1:
dev\verify-check.ps1:
```

- [ ] Not concerned (`app/` alone, documentation, CI).

## Checklist

- [ ] `scripts\dev\check-commits.ps1` passes: conventional subjects, no credit to a language model.
- [ ] `scripts\dev\check-repo.ps1` passes.
- [ ] No file of `src/`, no game binary or asset, no account data, no path of my machine.
- [ ] New names in `docs/names.tsv` carry their evidence, and none comes from leaked source code.
- [ ] Nothing here helps to get around the game's anti-cheat or to take a rebuilt client online.
