use anyhow::{anyhow, bail, Context, Result};
use ochu::run::Stop;
use ochu::{generate, locate, manifest, payload, pipeline, Ctx, Event, REFERENCE_BUILD};
use std::path::{Path, PathBuf};
use std::process::ExitCode;

const SHOWN_DIFFERENCES: usize = 20;

const HELP: &str = "ochu-cli: the decompiled source of the legacy osu! client, from your own copy of the game

  ochu-cli generate --out <folder> [--game <folder>] [--check]
      The whole run in a folder of yours: checks the game copy, gets the tools, runs the
      pipeline, checks the source. --game defaults to the installation found on this
      machine. --check also rebuilds the source and compares it with the original.
  ochu-cli locate
      Lists the osu! installations found and whether each holds the reference build.
  ochu-cli verify-game <folder>
      Checks the six assemblies the pipeline reads against game.sha256.

In a checkout of the repository (--root, or the current directory):
  ochu-cli pipeline [--from copy|strings|clean|rename|names|decomp|src] [--game <folder>]
                    [--with <option>]... [--raw-dir <dir>] [--src-dir <dir>]
                    [--hints-file <file>] [--lenient-fixups]
  ochu-cli src-manifest [--verify] [--path <src folder>]
      Writes src.sha256 from src/, or checks src/ against it.
";

struct Args(Vec<String>);

impl Args {
    fn position(&self, name: &str) -> Option<usize> {
        self.0.iter().position(|a| a.eq_ignore_ascii_case(name))
    }

    fn flag(&mut self, name: &str) -> bool {
        self.position(name).map(|i| self.0.remove(i)).is_some()
    }

    fn value(&mut self, name: &str) -> Result<Option<String>> {
        let Some(i) = self.position(name) else { return Ok(None) };
        if i + 1 >= self.0.len() {
            bail!("{} needs a value", name);
        }
        self.0.remove(i);
        Ok(Some(self.0.remove(i)))
    }

    fn values(&mut self, name: &str) -> Result<Vec<String>> {
        let mut all = Vec::new();
        while let Some(v) = self.value(name)? {
            all.push(v);
        }
        Ok(all)
    }

    fn path(&mut self, name: &str) -> Result<Option<PathBuf>> {
        self.value(name)?.map(|p| absolute(&p)).transpose()
    }

    fn done(&self) -> Result<()> {
        match self.0.first() {
            Some(extra) => bail!("unexpected argument: {}", extra),
            None => Ok(()),
        }
    }
}

fn console(root: PathBuf) -> Ctx {
    Ctx::new(root, Stop::default(), |event| match event {
        Event::Line(line) => println!("{}", line),
        Event::Step(index, total, title) => println!("## {}/{} {}", index, total, title),
    })
}

fn absolute(path: &str) -> Result<PathBuf> {
    Ok(std::path::absolute(Path::new(path))?)
}

fn checkout(args: &mut Args) -> Result<PathBuf> {
    let root = match args.path("--root")? {
        Some(root) => root,
        None => std::env::current_dir()?,
    };
    if !root.join("scripts\\decompiler\\Decompile.csproj").is_file() {
        bail!("{} is not a checkout of the repository (--root)", root.display());
    }
    Ok(root)
}

fn differences(bad: &[String], limit: usize, verdict: &str) -> ExitCode {
    if bad.is_empty() {
        return ExitCode::SUCCESS;
    }
    bad.iter().take(limit).for_each(|line| println!("{}", line));
    println!("{} difference(s): {}", bad.len(), verdict);
    ExitCode::FAILURE
}

fn cmd_locate(args: Args) -> Result<ExitCode> {
    args.done()?;
    let found = locate::candidates();
    if found.is_empty() {
        println!("No osu! installation found.");
        return Ok(ExitCode::FAILURE);
    }
    for dir in found {
        let reference = manifest::verify_game(&dir, payload::GAME_SHA256).is_empty();
        let verdict = if reference { format!("reference build {}", REFERENCE_BUILD) } else { "another build".to_string() };
        println!("{}\t{}", dir.display(), verdict);
    }
    Ok(ExitCode::SUCCESS)
}

fn cmd_verify_game(mut args: Args) -> Result<ExitCode> {
    let dir = absolute(&args.0.pop().ok_or_else(|| anyhow!("verify-game <folder>"))?)?;
    args.done()?;
    let bad = manifest::verify_game(&dir, payload::GAME_SHA256);
    if bad.is_empty() {
        println!("OK: the six pipeline inputs match game.sha256");
    }
    Ok(differences(&bad, usize::MAX, &format!("{} does not hold the reference build.", dir.display())))
}

fn cmd_generate(mut args: Args) -> Result<ExitCode> {
    let out = args.path("--out")?.ok_or_else(|| anyhow!("generate needs --out <folder>"))?;
    let game = match args.path("--game")? {
        Some(game) => game,
        None => locate::best(payload::GAME_SHA256).ok_or_else(|| anyhow!("no osu! installation found: give --game <folder>"))?,
    };
    let check = args.flag("--check");
    args.done()?;
    let ctx = console(generate::workspace_for(&out, &game)?);
    let outcome = generate::generate(&ctx, &game, check)?;
    println!();
    println!("{} files in {}", outcome.files, outcome.src.display());
    if let Some(rate) = outcome.rate {
        println!("{}", rate);
    }
    Ok(ExitCode::SUCCESS)
}

fn cmd_pipeline(mut args: Args) -> Result<ExitCode> {
    let root = checkout(&mut args)?;
    let from = match args.value("--from")? {
        Some(name) => pipeline::stage_index(&name).ok_or_else(|| anyhow!("--from: one of {}", pipeline::stage_names()))?,
        None => 0,
    };
    let options = pipeline::Options {
        from,
        game: args.path("--game")?,
        with: args.values("--with")?,
        raw_dir: args.value("--raw-dir")?.map(PathBuf::from),
        src_dir: args.value("--src-dir")?.map(PathBuf::from),
        hints_file: args.value("--hints-file")?.map(PathBuf::from),
        lenient_fixups: args.flag("--lenient-fixups"),
        steps: None,
    };
    args.done()?;
    pipeline::run(&console(root), &options)?;
    Ok(ExitCode::SUCCESS)
}

fn cmd_src_manifest(mut args: Args) -> Result<ExitCode> {
    let root = checkout(&mut args)?;
    let verify = args.flag("--verify");
    let src = args.path("--path")?.unwrap_or_else(|| root.join("src"));
    args.done()?;
    let file = root.join("src.sha256");
    if !verify {
        println!("Wrote {} entries to src.sha256", manifest::write_src(&src, &file)?);
        return Ok(ExitCode::SUCCESS);
    }
    let expected = std::fs::read_to_string(&file).with_context(|| format!("cannot read {}", file.display()))?;
    let (count, bad) = manifest::verify_src(&src, &expected)?;
    if bad.is_empty() {
        println!("OK: {} source files match src.sha256", count);
    }
    Ok(differences(&bad, SHOWN_DIFFERENCES, "src/ is NOT the source of the reference machine."))
}

fn run() -> Result<ExitCode> {
    let mut args = Args(std::env::args().skip(1).collect());
    let command = if args.0.is_empty() { "help".to_string() } else { args.0.remove(0) };
    match command.as_str() {
        "help" | "--help" | "-h" | "/?" => {
            print!("{}", HELP);
            Ok(ExitCode::SUCCESS)
        }
        "locate" => cmd_locate(args),
        "verify-game" => cmd_verify_game(args),
        "generate" => cmd_generate(args),
        "pipeline" => cmd_pipeline(args),
        "src-manifest" => cmd_src_manifest(args),
        other => bail!("unknown command: {} (ochu-cli help)", other),
    }
}

fn main() -> ExitCode {
    run().unwrap_or_else(|error| {
        eprintln!("error: {:#}", error);
        ExitCode::FAILURE
    })
}
