use crate::{files, manifest, payload, pipeline, run, tools, Ctx, REFERENCE_BUILD};
use anyhow::{bail, Result};
use std::fs;
use std::path::{Path, PathBuf};

const MARKER: &str = ".ochu";
const STEPS_BEFORE: usize = 3;
const SHOWN_DIFFERENCES: usize = 20;

// Nothing may outlive the run and hold the workspace's files: no MSBuild node, no compiler server.
const CHILD_ENV: [(&str, &str); 4] = [
    ("MSBUILDDISABLENODEREUSE", "1"),
    ("UseSharedCompilation", "false"),
    ("DOTNET_CLI_TELEMETRY_OPTOUT", "1"),
    ("DOTNET_NOLOGO", "1"),
];

pub struct Outcome {
    pub src: PathBuf,
    pub files: usize,
    pub verified: bool,
    pub rate: Option<String>,
}

fn same_or_inside(path: &Path, base: &Path) -> bool {
    let (p, b) = (path.to_string_lossy().to_lowercase(), base.to_string_lossy().to_lowercase());
    let (p, b) = (p.trim_end_matches('\\'), b.trim_end_matches('\\'));
    p == b || p.starts_with(&format!("{}\\", b))
}

fn usable(dir: &Path) -> Result<bool> {
    if !dir.exists() {
        return Ok(true);
    }
    Ok(dir.is_dir() && (dir.join(MARKER).is_file() || fs::read_dir(dir)?.next().is_none()))
}

// A folder that holds other things is never taken over: the workspace is its `ochu` subfolder then.
pub fn workspace_for(out: &Path, game: &Path) -> Result<PathBuf> {
    if !out.is_absolute() {
        bail!("the output folder must be a full path");
    }
    if same_or_inside(out, game) || same_or_inside(game, out) {
        bail!("the output folder cannot be the game's folder, inside it or around it: the game copy is only read");
    }
    if usable(out)? {
        return Ok(out.to_path_buf());
    }
    let sub = out.join("ochu");
    if usable(&sub)? {
        return Ok(sub);
    }
    bail!("{} holds other files: choose an empty folder", sub.display());
}

pub fn steps(check: bool) -> usize {
    STEPS_BEFORE + pipeline::STAGES.len() + if check { 3 } else { 1 }
}

fn check_game(ctx: &Ctx, game: &Path) -> Result<()> {
    let bad = manifest::verify_game(game, payload::GAME_SHA256);
    if !bad.is_empty() {
        bad.iter().for_each(|line| ctx.log(line.as_str()));
        bail!(
            "{} does not hold the reference build ({}). The tables of this project only mean something for that exact build.",
            game.display(),
            REFERENCE_BUILD
        );
    }
    ctx.log(format!("OK: the six pipeline inputs of {} are those of build {}", game.display(), REFERENCE_BUILD));
    Ok(())
}

fn check_source(ctx: &Ctx, src: &Path) -> Result<bool> {
    if payload::SRC_SHA256.trim().is_empty() {
        ctx.log("This program carries no fingerprint of the source: the result is not checked.");
        return Ok(false);
    }
    let (count, bad) = manifest::verify_src(src, payload::SRC_SHA256)?;
    if !bad.is_empty() {
        bad.iter().take(SHOWN_DIFFERENCES).for_each(|line| ctx.log(line.as_str()));
        bail!(
            "{} difference(s): the generated source is not the one of the reference machine. It is left in {} for inspection.",
            bad.len(),
            src.display()
        );
    }
    ctx.log(format!("OK: {} source files match src.sha256", count));
    Ok(true)
}

pub fn generate(ctx: &Ctx, game: &Path, check: bool) -> Result<Outcome> {
    let total = steps(check);
    let root = &ctx.root;

    ctx.step(1, total, "Checking the game copy");
    check_game(ctx, game)?;

    ctx.step(2, total, "Preparing the workspace");
    files::write(
        &root.join(MARKER),
        "Workspace of ochu!!. Everything here is regenerated: do not keep your own files in it.\n",
    )?;
    payload::extract(root)?;
    ctx.log(format!("workspace: {}", root.display()));

    ctx.step(3, total, "Getting the tools");
    tools::ensure_minimal(ctx)?;
    for (key, value) in CHILD_ENV {
        ctx.set_env(key, value);
    }

    let options =
        pipeline::Options { game: Some(game.to_path_buf()), steps: Some((STEPS_BEFORE, total)), ..Default::default() };
    pipeline::run(ctx, &options)?;

    let after = STEPS_BEFORE + pipeline::STAGES.len();
    ctx.step(after + 1, total, "Checking the source");
    let src = root.join("src");
    let files = manifest::hash_tree(&src)?.len();
    let verified = check_source(ctx, &src)?;

    let mut rate = None;
    if check {
        ctx.step(after + 2, total, "Rebuilding the source");
        run::script(ctx, "build.ps1", &[])?;
        ctx.step(after + 3, total, "Comparing with the original");
        let out = run::script(ctx, "match.ps1", &[])?;
        rate = out.lines.into_iter().find(|l| l.starts_with("ALL"));
    }
    Ok(Outcome { src, files, verified, rate })
}
