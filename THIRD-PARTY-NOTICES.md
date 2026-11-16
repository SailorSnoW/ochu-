# Third-party notices

`osu!.exe` is several assemblies merged into one: next to the game's own code (ppy Pty Ltd),
the generated `src/` holds the decompiled code of the libraries below. Each remains the work of its
authors, under its own license. Libraries are identified from what the binary shows (the
evidence is in `docs/names.tsv`); their exact versions are not established. Code under
`src/` that is in none of these folders is the game's, or not attributed yet.

## Open-source components

The license of each component applies to its files, and its text is at the address given.

| Path in `src/` | Component | Copyright | License |
|---|---|---|---|
| `Newtonsoft/Json` | [Json.NET](https://github.com/JamesNK/Newtonsoft.Json) | James Newton-King | MIT |
| `DiscordRPC` | [discord-rpc-csharp](https://github.com/Lachee/discord-rpc-csharp) | Lachee | MIT |
| `Org/BouncyCastle` | [Bouncy Castle for .NET](https://www.bouncycastle.org/csharp/) (a few classes, embedded with the obfuscator's runtime) | The Legion of the Bouncy Castle Inc. | MIT (Bouncy Castle licence) |
| `OpenTK` | [OpenTK](https://github.com/opentk/opentk) (two types; the library itself is a separate DLL of the game) | Stefanos Apostolopoulos and the Open Toolkit contributors | MIT (Open Toolkit licence) |
| `ICSharpCode/SharpZipLib` | [SharpZipLib](https://github.com/icsharpcode/SharpZipLib), a release from before 1.0 (namespace `Checksums`) | Mike Krueger, John Reilly | GPL 2.0 or later, with the library's linking exception |
| `Ionic/Zlib` | [DotNetZip](https://github.com/DinoChiesa/DotNetZip) (zlib part) | Dino Chiesa, Microsoft; zlib by Jean-loup Gailly and Mark Adler; jzlib by ymnk, JCraft Inc. | Ms-PL, with the zlib and jzlib (BSD-style) notices |
| `Amib/Threading` | [SmartThreadPool](https://github.com/amibar/SmartThreadPool) | Ami Bar | Ms-PL |
| `WiimoteLib` | [WiimoteLib](https://github.com/BrianPeek/WiimoteLib) | Brian Peek | Ms-PL |
| `SevenZip` | [LZMA SDK](https://www.7-zip.org/sdk.html) | Igor Pavlov | public domain |
| `SDILReader` | SDILReader, from the CodeProject article "Parsing the IL of a Method Body" | Sorin Serban | license of the article, not verified |

## Proprietary components

No license of their owners allows the publication of these in source form. They are here on
the same footing as the game's code: no right is granted on them (see [`LEGAL.md`](LEGAL.md)),
and they are removed at their owner's request.

| Path in `src/` | Component | Owner |
|---|---|---|
| `Obfuscator`, and the generic-named types `docs/units.tsv` describes as obfuscator code | runtime of Eazfuscator.NET (string decryptor, resource and assembly resolvers, virtual machine) | Gapotchenko |
| `Un4seen/Bass` | BASS.NET, the .NET wrapper of the BASS audio library | radio42 (wrapper), Un4seen Developments (BASS) |
| `Microsoft/Xna` | types of the Microsoft XNA Framework | Microsoft |

## Code that looks like published samples

Identified by shape and strings only, from memory of the samples: not compared with them.

| Path in `src/` | Looks like | Note |
|---|---|---|
| `osu/Helpers/DPAPI.cs` | the "DPAPI" C# sample class published by Obviex (wrapper of `CryptProtectData` / `CryptUnprotectData`) | license of the sample not verified |
| `osu/Helpers/Forms/SmoothProgressBar.cs` | the `SmoothProgressBar` control of Microsoft's knowledge base article 323116 | uncertain |

`osu/Helpers/NvApi.cs` and the FFmpeg binding under `src/` are the game's own P/Invoke
declarations for NVIDIA NVAPI and FFmpeg, which are separate native libraries; their struct
and function names follow the public headers of those libraries.

## Not library code

`Microsoft/CodeAnalysis` and `System/Runtime/CompilerServices` hold attributes the C#
compiler writes into an assembly by itself.

## Tools

The tools the scripts and the program download (`scripts/setup-tools.ps1`,
`app/src/tools.rs`, `scripts/decompiler/Decompile.csproj`) are not redistributed here. Each
is used under its own license: ICSharpCode.Decompiler (MIT), dnlib (MIT), de4dot (GPL 3.0),
Roslyn (MIT), the .NET SDK (MIT). The Rust crates the program is built from are
listed with their versions in `app/Cargo.lock`, each under its own license.
