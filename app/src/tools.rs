use crate::run::{self, Echo};
use crate::{manifest, Ctx};
use anyhow::{bail, Context, Result};
use sha2::{Sha256, Sha512};
use std::ffi::OsStr;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

pub enum Hash {
    Sha256(&'static str),
    Sha512(&'static str),
}

impl Hash {
    fn check(&self, path: &Path) -> Result<()> {
        let (want, got) = match self {
            Hash::Sha256(want) => (want, manifest::digest_file::<Sha256>(path)?),
            Hash::Sha512(want) => (want, manifest::digest_file::<Sha512>(path)?),
        };
        if !got.eq_ignore_ascii_case(want) {
            bail!("expected {}, got {}", want, got);
        }
        Ok(())
    }
}

pub struct Pin {
    pub name: &'static str,
    pub version: &'static str,
    pub url: &'static str,
    pub hash: Hash,
}

// Same pin as scripts/setup-tools.ps1: change both together.
pub const DE4DOT: Pin = Pin {
    name: "de4dot-cex",
    version: "4.0.0",
    url: "https://github.com/ViRb3/de4dot-cex/releases/download/v4.0.0/de4dot-cex.zip",
    hash: Hash::Sha256("c726cbd18b894ca63b7f6a565c6c86ef512b96e68119c6502cdf64a51f6a1c78"),
};

// The SDK of global.json; the hash is the one of Microsoft's release metadata
// (builds.dotnet.microsoft.com/dotnet/release-metadata/8.0/releases.json).
pub const DOTNET_SDK: Pin = Pin {
    name: "dotnet-sdk",
    version: "8.0.427",
    url: "https://builds.dotnet.microsoft.com/dotnet/Sdk/8.0.427/dotnet-sdk-8.0.427-win-x64.zip",
    hash: Hash::Sha512("a4dd6eb2bf2a2f6fb10997d3e044a229b23ad96a5c86d20494687e34ee73b537d7bd14488ce14cded6917f6778da4d66360e2734020b7ef487d0f29ab144489d"),
};

fn receive(ctx: &Ctx, url: &str, part: &Path) -> Result<()> {
    let response = ureq::get(url).call().with_context(|| format!("download failed: {}", url))?;
    let total: u64 = response.header("Content-Length").and_then(|v| v.parse().ok()).unwrap_or(0);
    let mut reader = response.into_reader();
    let mut file = fs::File::create(part)?;
    let mut buffer = vec![0u8; 1 << 16];
    let (mut done, mut shown) = (0u64, 0u64);
    loop {
        ctx.stop.check()?;
        let n = reader.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        file.write_all(&buffer[..n])?;
        done += n as u64;
        if total > 0 && done * 10 / total > shown {
            shown = done * 10 / total;
            ctx.log(format!("       {} % of {} MB", shown * 10, total / 1_000_000));
        }
    }
    if total > 0 && done != total {
        bail!("download interrupted: {} of {} bytes", done, total);
    }
    Ok(())
}

fn download(ctx: &Ctx, url: &str, to: &Path) -> Result<()> {
    let part = to.with_extension("part");
    if let Err(error) = receive(ctx, url, &part) {
        let _ = fs::remove_file(&part);
        return Err(error);
    }
    fs::rename(&part, to)?;
    Ok(())
}

pub fn ensure(ctx: &Ctx, pin: &Pin) -> Result<PathBuf> {
    let tools = ctx.path("tools");
    let dest = tools.join(pin.name);
    let marker = dest.join(".version");
    if fs::read_to_string(&marker).is_ok_and(|v| v.trim() == pin.version) {
        ctx.log(format!("[skip] {} {}", pin.name, pin.version));
        return Ok(dest);
    }
    let cache = tools.join("_cache");
    fs::create_dir_all(&cache)?;
    let archive = cache.join(pin.url.rsplit('/').next().unwrap());
    if !archive.is_file() {
        ctx.log(format!("[get ] {} {}", pin.name, pin.version));
        download(ctx, pin.url, &archive)?;
    }
    pin.hash
        .check(&archive)
        .with_context(|| format!("hash mismatch for {}. Delete {} and try again.", pin.name, archive.display()))?;
    crate::files::empty_dir(&dest)?;
    // tar.exe of Windows is bsdtar: it reads zip.
    let tar = run::windows_dir().join("System32\\tar.exe");
    let args: [&OsStr; 4] = ["-xf".as_ref(), archive.as_ref(), "-C".as_ref(), dest.as_ref()];
    if run::command(ctx, &tar, &args, &tools, Echo::Log)?.code != 0 {
        bail!("extraction failed for {}", pin.name);
    }
    fs::write(&marker, pin.version)?;
    ctx.log(format!("[ ok ] {} {}", pin.name, pin.version));
    Ok(dest)
}

fn version(text: &str) -> Option<(u32, u32, u32)> {
    let mut parts = text.split('.');
    let (major, minor, patch) = (parts.next()?, parts.next()?, parts.next()?);
    Some((major.parse().ok()?, minor.parse().ok()?, patch.split('-').next()?.parse().ok()?))
}

// global.json rolls forward inside the minor: the pinned SDK or a later patch of it.
fn sdk_fits(sdks: &[String]) -> bool {
    let Some((major, minor, patch)) = version(DOTNET_SDK.version) else { return false };
    sdks.iter()
        .filter_map(|line| version(line.split_whitespace().next()?))
        .any(|found| found.0 == major && found.1 == minor && found.2 >= patch)
}

fn machine_sdk_fits(ctx: &Ctx) -> bool {
    run::command(ctx, Path::new("dotnet"), &["--list-sdks"], &run::windows_dir(), Echo::Quiet)
        .is_ok_and(|out| out.code == 0 && sdk_fits(&out.lines))
}

pub fn ensure_dotnet(ctx: &Ctx) -> Result<()> {
    let private = ctx.path("tools").join(DOTNET_SDK.name);
    // OCHU_PRIVATE_SDK=1 takes the private copy even when the machine has an SDK (tests).
    let forced = std::env::var("OCHU_PRIVATE_SDK").is_ok_and(|v| v == "1");
    if !forced && !private.join(".version").is_file() {
        if machine_sdk_fits(ctx) {
            ctx.log("[skip] .NET SDK 8 of this machine");
            return Ok(());
        }
        ctx.log("No .NET SDK 8.0.4xx on this machine: a private copy goes to tools/dotnet-sdk (about 290 MB).");
    }
    let dir = ensure(ctx, &DOTNET_SDK)?;
    ctx.set_env("PATH", format!("{};{}", dir.display(), std::env::var("PATH").unwrap_or_default()));
    ctx.set_env("DOTNET_ROOT", dir.to_string_lossy());
    ctx.set_env("DOTNET_MULTILEVEL_LOOKUP", "0");
    Ok(())
}

pub fn ensure_minimal(ctx: &Ctx) -> Result<()> {
    ensure(ctx, &DE4DOT)?;
    ensure_dotnet(ctx)?;
    Ok(())
}
