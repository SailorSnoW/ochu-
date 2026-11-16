# Matching

The goal is a source that, recompiled, gives back **the original IL method by method**.
`osu!.exe` went through an obfuscator after compilation, so the file cannot be reproduced
byte for byte: the target is the IL of each method, and this document says exactly what the
comparison reads and what it leaves out.

```powershell
.\scripts\build.ps1                    # src/ -> work/build/linked/osu!.exe
.\scripts\match.ps1                    # the rate -> work/match/report.tsv
.\scripts\match.ps1 -Show 0x060007EC   # the diff of one method (token of the reference)
```

State on 2026-11-16: 19672 methods out of 19672 match.

| Compilation unit | Compiler | Methods |
|---|---|---|
| main | Roslyn 3.11.0, `/optimize+`, the whole game and every library | 19579 |
| `debugplus` | the same with `/debug+` | 56 |
| `csc40` | `csc.exe` of the .NET Framework 4.x | 27 |
| `il` | `ilasm`, on `src/il/unit.il` | 10 |

19662 methods are decompiled to C# (99.95 %). The 10 methods of the unit `il` are in the
build, identical, without being decompiled: report them apart.

## What is compared

The reference is `work/deobf/osu!.clean.exe`: the original IL with the strings decrypted, the
virtualized methods written back as CIL and the obfuscator's edits undone
([`pipeline.md`](pipeline.md)). Names come from `osu!.named.exe`, which has the same IL:
`match.ps1 -Candidate 'work\deobf\osu!.named.exe'` must report 100 %. The candidate is the
single linked file `work/build/linked/osu!.exe`.

A method **matches** when these are identical (`scripts/lib/IlMatch.cs`):

- the instructions, opcodes and operands: members and types by rendered name, branch targets
  as relative instruction counts, constants and strings as they are;
- the types of the local variables, in order;
- the exception handlers (kind, bounds, caught type).

`maxstack` and `initlocals` are only noted: the obfuscator rewrote them.

Compiler-generated members (lambdas, closure classes, iterators) carry ordinals the naming
stage cannot predict: both sides are brought to a canonical name before pairing. Only the
pairing ignores the ordinals; two bodies still have to be identical to match.

### Equivalent readings

The obfuscator re-encoded part of the IL in ways no C# compiler emits. For those, both sides
are read in one canonical encoding. Every reading is about two encodings that execute the
same way, and each can be switched off (`match.ps1 -Without`) to measure what the rate owes
it:

| Reading (`-Without` name) | What is read as the same | Methods that depend on it |
|---|---|---|
| `slots` | the numbering of local variables: the obfuscator renumbers them by first use, then by use count | 1195 |
| `ldelem` | `ldelem T` / `stelem T` on a reference type, read as `ldelem.ref` / `stelem.ref` | 387 |
| `branch-form` | short or long form of a branch, recomputed from the canonical sizes | 174 |
| `starg` | long `starg` on a parameter below 256, read as `starg.s` | 41 |
| `const-slots` | a constant stored once in a slot and read once | 13 |
| `nops` | free `nop`s | 5 |
| `pushes` | a value pushed and popped at once | 4 |
| `ldobj` | `ldobj IntPtr`, `ldobj` / `stobj Byte` | 1 |
| `dead-stores` | a variable that is never read | 1 |
| all of them | | 1504 |

So **18168 methods (92.35 %) are identical with no reading at all**, 937 more differ only by
the numbers of their locals, and 1504 in total are identical *up to a reading*. None of the
readings can hide a difference of behaviour; what they hide is a difference of bytes, in
methods whose original bytes are no longer in the binary.

## Compilation units

`osu!.exe` is not the output of one compilation: the obfuscator merged several assemblies and
added its own runtime, built with other compilers and options. One compilation of `src/`
cannot give everything back. [`units.tsv`](units.tsv) gives types (or, for `il`, methods) to
a unit built apart; `build.ps1` compiles each unit and `link.ps1` moves their method bodies
into the main build, so that the result is one binary.

- **main**: the compiler of the game's code is a Roslyn between 3.7.0 and 4.3.0 (every
  version in that range gives the same IL for the whole tree; earlier and later ones match
  less). `build/osu.csproj` pins 3.11.0. The source is decompiled as C# 7.3, the level that
  matches best. `scripts/try-compiler.ps1` compiles a tree with any other pinned version.
- **`debugplus`**: four types of the obfuscator's runtime keep every declared local in a
  slot, which the same compiler does with `/optimize+ /debug+`.
- **`csc40`**: two types have forms only the compiler from before Roslyn emits.
- **`il`**: ten methods no C# compiles to (a module initializer, a `catch` and a `throw` of a
  plain `object`, iterators over interfaces the obfuscator copied into the program, the
  machine-assembled string decryptor). The reason for each is in `units.tsv`.

A type goes to a unit only when every one of its methods matches there, and a method goes to
`il` only when no C# compiles to it. All three units are obfuscator code: the game and the
libraries come out of the main compilation.

## What the rate does not cover

The rate says the source gives back the IL of each method. It does not say the build is the
program. Five checks are run after every change; state what each leaves out whenever the rate
is reported.

| Check | What it establishes | What it does not cover |
|---|---|---|
| `scripts/match.ps1` | 19672 / 19672 method bodies match | bodies are read through the equivalent encodings above; declarations are not compared |
| `scripts/dev/strict-match.ps1` | 18168 methods match with every reading switched off | the 1504 others: their proof is the measure of each reading |
| `scripts/dev/outside-check.ps1` | another tool (ILSpy's disassembler over System.Reflection.Metadata) confirms 16274 strict matches and contradicts none | it pairs by name, so compiler-generated members are out of its reach; it says nothing of the equivalent readings |
| `scripts/match-meta.ps1` | declarations: the 21409 methods, the signatures, base types, interfaces, constants, P/Invoke data, the 32 managed and 20 Win32 resources are identical | known differences, none with an effect at run time: nullable attributes removed, flags and attributes the obfuscator had stripped and the rebuild has again, the order of members inside types |
| `scripts/dev/verify-check.ps1` | the linked build loads for reflection without error, and PEVerify gives the same verdict as for the original (671 errors of the same codes in both) | nothing is executed; equal counts are not identity |

The five checks are those of the machine that ran them. `src/` is not in the repository: a
user generates it, and `src.sha256` tells whether that source is, byte for byte, the one the
checks above were run on (`ochu-cli.exe src-manifest --verify`, and the last step of
`ochu.exe`). It has been verified on one machine only, in three places: the checkout, an empty
folder with the machine's .NET SDK, an empty folder with the private SDK the program
downloads. A machine where it differs has not been seen, so what would make it differ is not
known; the option "rebuild and compare" of the program runs `build.ps1` and `match.ps1`
there, which is the first check of the table and none of the four others.

What remains between this build and "the same program":

- **Original bytes.** For 1504 methods the obfuscator rewrote the encoding; the compiler's
  bytes cannot be recovered and are not claimed.
- **Ten methods in IL**, copied from the reference by construction.
- **Execution.** The game checks who runs it, and the rebuild is not the official client: as
  built, it does not start. It has only been run offline, in an isolated copy with no
  account, where it behaved as the original at start-up (same window, same messages, same
  log lines, the configuration file rewritten byte for byte the same) and reached the main
  menu. Nothing that needs a connection has been exercised, and how many methods actually
  ran was not measured. The repository has no tooling for these runs.

Launching the game connects to the osu! servers: the rebuild is not the official client and
must stay offline.

## Working on a difference

After a change of the reference, of the decompiler or of a table, a method may stop matching.
The loop, with the scripts of `scripts/dev/`:

| Script | Role |
|---|---|
| `cycle.ps1` | pipeline, build, match, then the methods gained and lost against a saved report; `-Accept` saves the new base when nothing is lost |
| `list-diffs.ps1` | the differences by size, to find a family |
| `show-diff.ps1`, `show-hunks.ps1`, `show-typed.ps1`, `show-slots.ps1` | the difference of one method, reduced to what really differs |
| `show-source.ps1` | the decompiled source of a method, by token |
| `try-csharp.ps1`, `try-legacy.ps1` | what the pinned compiler (or the .NET Framework's) emits for a given C# form |
| `debug-decompile.ps1` | the decompiler's tree for a method, before and after the project's transforms |

Decide from the raw IL and a test compile, not from the look of the source: see what the
compiler emits for the supposed form, then write a rule in `scripts/decompiler` when a family
of methods shows it, or an entry of `fixups.tsv` for a single method. After a change of the
decompiler, run `scripts/tune-decompiler.ps1 -OnlyDiff`: methods a new rule fixes no longer
need their entry in `decompiler-hints.tsv`. A rule that loses a method has a flaw: read the
losses before keeping it.
