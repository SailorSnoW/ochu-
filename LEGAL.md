# Legal notice

This file states what the repository contains, whose work it is, and what you may and may
not do with it. It is a statement of facts and of the project's rules, not legal advice.

## Whose work this is

- **osu!** is the work of ppy Pty Ltd. The client decompiled here ("stable", build 1.3.3.8)
  is proprietary and closed source: its publisher distributes it under the
  [osu! terms of service](https://osu.ppy.sh/legal/en/Terms), which grant no right to copy,
  distribute or adapt the software. This project is not affiliated with, authorized by or
  endorsed by ppy Pty Ltd.
- **`src/`** is not in the repository. It is what the tools write on your machine from your
  own copy of the client: the output of a decompiler, that is the publisher's code, and the
  code of the third parties listed in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md), in
  another form. Their copyright is untouched. **No license is granted on `src/`**: this
  project gives nobody the right to copy it, modify it, redistribute it, or build a product
  from it. It is not open source and must not be presented as such.
- **The project's own work** is what the repository holds: `app/`, `scripts/`, `build/`, the
  documents and the tables of `docs/`. It is under the MIT license, within the scope written at the top of
  [`LICENSE`](LICENSE). That license does not extend to `src/`.
- "osu!" and "ppy" are names and brands of ppy Pty Ltd, protected by trademark law. They are
  used here only to say which program the work is about.

## What the repository contains

Text, and the project's own banner, logo and icon under `assets/` and `app/assets/`. It
contains no game binary, no source code of the game, no asset of the game (image, sound,
font, skin, beatmap, embedded resource), no account data and no credential. The toolchain
works on your own copy of the reference build, obtained from its publisher, and writes
everything it derives from it, the decompiled source included, to git-ignored folders.

Three kinds of file do describe the game's code without being it:

- `docs/names.tsv` gives names to its symbols, and `docs/decompiler-hints.tsv`,
  `docs/units.tsv` and the `vm-*.tsv` tables say how to read some of its methods;
- `docs/fixups.tsv` quotes single lines of the decompiled code, about a hundred, to correct
  what the decompiler gets wrong;
- `src.sha256` lists the file names of the generated source and their SHA-256.

The programs built from `app/` carry those same files and nothing else.

Names in `docs/names.tsv` come from what the binary itself shows (strings, surviving
namespaces, API calls) and from public, openly licensed code; each line gives its evidence.
Material from a leak of the game's source code is not used and is not accepted.

## Purpose, and what it is not for

The project exists for the preservation and study of the legacy client, which its publisher
has feature-locked and is replacing, and for research on interoperability (file formats,
protocol). It is non-commercial.

It is not for, and contributions must not serve:

- cheating, or any tool whose purpose is to get around the game's anti-cheat or its checks;
- connecting a rebuilt or modified client to the osu! servers. The rebuilt program is not
  the official client and must stay offline;
- distributing builds made from this repository, or the game's files;
- any use that competes with the game or passes itself off as it.

## Protection measures

The binary is protected by an obfuscator (encrypted strings and names, virtualized methods).
The scripts of this repository undo that protection in a local copy, to read the code. Using
such means unlawfully, in particular to infringe the rights in the software, is punishable as
copyright infringement (in France: articles L. 122-6-2 and L. 335-2 and following of the
Code de la propriété intellectuelle; other countries have equivalent rules).

## Requests from rights holders

ppy Pty Ltd, or any rights holder of a component listed in `THIRD-PARTY-NOTICES.md`, can ask
for the removal of material by opening an issue on the repository, or by contacting its
owner through the platform that hosts it. No formal notice is needed: the material named in
the request will be removed without delay and without argument.
