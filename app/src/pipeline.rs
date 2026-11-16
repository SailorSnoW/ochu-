use crate::run::{self, Echo};
use crate::{files, srcstep, Ctx, CORE_FILES};
use anyhow::{bail, Context, Result};
use std::ffi::OsStr;
use std::fs;
use std::path::{Path, PathBuf};

pub struct Stage {
    pub name: &'static str,
    pub title: &'static str,
    run: fn(&Ctx, &Options, &Paths) -> Result<()>,
}

pub const STAGES: [Stage; 7] = [
    Stage { name: "copy", title: "Copying the game assemblies", run: copy },
    Stage { name: "strings", title: "Decrypting the strings", run: strings },
    Stage { name: "clean", title: "Undoing the obfuscator", run: clean },
    Stage { name: "rename", title: "Renaming the symbols", run: rename },
    Stage { name: "names", title: "Applying the names", run: names },
    Stage { name: "decomp", title: "Decompiling", run: decompile },
    Stage { name: "src", title: "Writing the source", run: write_src },
];

#[derive(Default)]
pub struct Options {
    pub from: usize,
    pub game: Option<PathBuf>,
    pub with: Vec<String>,
    pub raw_dir: Option<PathBuf>,
    pub src_dir: Option<PathBuf>,
    pub hints_file: Option<PathBuf>,
    pub lenient_fixups: bool,
    pub steps: Option<(usize, usize)>,
}

struct Paths {
    inputs: PathBuf,
    deobf: PathBuf,
    decomp: PathBuf,
    raw: PathBuf,
    src: PathBuf,
    hints: PathBuf,
}

impl Paths {
    fn new(ctx: &Ctx, o: &Options) -> Paths {
        let or = |given: &Option<PathBuf>, default: &str| given.as_ref().map_or_else(|| ctx.path(default), |p| ctx.root.join(p));
        Paths {
            inputs: ctx.path("work\\triage\\in"),
            deobf: ctx.path("work\\deobf"),
            decomp: ctx.path("work\\decomp"),
            raw: or(&o.raw_dir, "work\\decomp\\raw"),
            src: or(&o.src_dir, "src"),
            hints: or(&o.hints_file, "docs\\decompiler-hints.tsv"),
        }
    }
}

pub fn stage_index(name: &str) -> Option<usize> {
    STAGES.iter().position(|stage| stage.name.eq_ignore_ascii_case(name))
}

pub fn stage_names() -> String {
    STAGES.map(|stage| stage.name).join(", ")
}

pub fn run(ctx: &Ctx, o: &Options) -> Result<()> {
    let paths = Paths::new(ctx, o);
    for dir in [&paths.inputs, &paths.deobf, &paths.decomp] {
        fs::create_dir_all(dir)?;
    }
    for (index, stage) in STAGES.iter().enumerate().skip(o.from) {
        ctx.log(format!("== {}. {}", index + 1, stage.name));
        if let Some((base, total)) = o.steps {
            ctx.step(base + index + 1, total, stage.title);
        }
        (stage.run)(ctx, o, &paths)?;
    }
    ctx.log("Done.");
    Ok(())
}

fn text(path: &Path) -> String {
    path.to_string_lossy().into_owned()
}

fn copy(ctx: &Ctx, o: &Options, paths: &Paths) -> Result<()> {
    let game = o.game.clone().unwrap_or_else(|| ctx.path("game"));
    for name in CORE_FILES {
        let to = paths.inputs.join(name);
        // A copy keeps the attributes of the game's file, read-only included: the next copy would fail.
        if to.exists() {
            let mut permissions = fs::metadata(&to)?.permissions();
            #[allow(clippy::permissions_set_readonly_false)]
            permissions.set_readonly(false);
            fs::set_permissions(&to, permissions)?;
        }
        fs::copy(game.join(name), &to).with_context(|| format!("cannot copy {} from {}", name, game.display()))?;
    }
    Ok(())
}

fn strings(ctx: &Ctx, _: &Options, _: &Paths) -> Result<()> {
    run::script(ctx, "decrypt-strings.ps1", &[])?;
    run::script(ctx, "extract-resources.ps1", &[])?;
    Ok(())
}

// Order matters: the obfuscator also pasted methods into the virtualized code, so that code is
// written back before the rest is undone.
fn clean(ctx: &Ctx, _: &Options, _: &Paths) -> Result<()> {
    run::script(ctx, "devirt\\dump-stream.ps1", &["-In", "work\\deobf\\osu!.strings.exe"])?;
    run::script(ctx, "undo-obfuscator.ps1", &["-Devirtualize"])?;
    Ok(())
}

fn rename(ctx: &Ctx, _: &Options, paths: &Paths) -> Result<()> {
    let clean = paths.deobf.join("osu!.clean.exe");
    let renamed = paths.deobf.join("osu!.renamed.exe");
    let log = paths.deobf.join("de4dot-rename.log");
    let _ = fs::remove_file(&renamed);
    let args: [&OsStr; 9] = [
        "-f".as_ref(),
        clean.as_ref(),
        "-o".as_ref(),
        renamed.as_ref(),
        "-p".as_ref(),
        "un".as_ref(),
        "--strtyp".as_ref(),
        "none".as_ref(),
        "--preserve-tokens".as_ref(),
    ];
    let out = run::command(ctx, &ctx.path("tools\\de4dot-cex\\de4dot.exe"), &args, &ctx.root, Echo::Quiet)?;
    fs::write(&log, out.lines.join("\r\n"))?;
    if out.code != 0 || !renamed.is_file() {
        bail!("de4dot rename failed, see {}", log.display());
    }
    run::script(ctx, "dump-symbols.ps1", &["-Path", &text(&renamed), "-Out", &text(&paths.decomp.join("symbols.tsv"))])?;
    Ok(())
}

fn names(ctx: &Ctx, _: &Options, _: &Paths) -> Result<()> {
    run::script(ctx, "apply-names.ps1", &[])?;
    run::script(ctx, "export-resources.ps1", &[])?;
    Ok(())
}

fn decompile(ctx: &Ctx, o: &Options, paths: &Paths) -> Result<()> {
    let raw = &paths.raw;
    files::empty_dir(raw)?;

    let dotnet = Path::new("dotnet");
    let project = ctx.path("scripts\\decompiler\\Decompile.csproj");
    let build: [&OsStr; 7] = ["build".as_ref(), project.as_ref(), "-c".as_ref(), "Release".as_ref(), "-nologo".as_ref(), "-v".as_ref(), "q".as_ref()];
    let built = run::command(ctx, dotnet, &build, &ctx.root, Echo::Quiet)?;
    if built.code != 0 {
        built.lines.iter().take(30).for_each(|line| ctx.log(format!("      {}", line)));
        bail!("building scripts/decompiler failed");
    }

    let decompiler = ctx.path("work\\tools\\decompile\\bin\\decompile.dll");
    let named = paths.deobf.join("osu!.named.exe");
    let family = raw.parent().unwrap_or(&paths.decomp).join("family.tsv");
    let units = ctx.path("docs\\units.tsv");
    let transforms = fs::read_to_string(ctx.path("scripts\\decompiler\\transforms.txt")).context("scripts/decompiler/transforms.txt")?;
    let transforms = transforms.lines().map(str::trim).filter(|l| !l.is_empty() && !l.starts_with('#'));

    let mut args: Vec<&OsStr> = vec![
        decompiler.as_ref(),
        named.as_ref(),
        "-o".as_ref(),
        raw.as_ref(),
        "-r".as_ref(),
        paths.inputs.as_ref(),
        "-lv".as_ref(),
        "CSharp7_3".as_ref(),
    ];
    for name in transforms {
        args.extend(["--with".as_ref(), OsStr::new(name)]);
    }
    args.extend(["--map".as_ref(), family.as_os_str()]);
    if paths.hints.is_file() {
        args.extend(["--hints".as_ref(), paths.hints.as_os_str()]);
    }
    if units.is_file() {
        args.extend(["--units".as_ref(), units.as_os_str()]);
    }
    for name in &o.with {
        args.extend(["--with".as_ref(), OsStr::new(name)]);
    }
    if run::command(ctx, dotnet, &args, &ctx.root, Echo::Log)?.code != 0 {
        bail!("decompile failed");
    }
    run::script(ctx, "make-il-unit.ps1", &["-Out", &text(&raw.join("il\\unit.il"))])?;
    Ok(())
}

fn write_src(ctx: &Ctx, o: &Options, paths: &Paths) -> Result<()> {
    srcstep::run(ctx, &paths.raw, &paths.src, o.lenient_fixups)
}
