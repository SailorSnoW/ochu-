use crate::Ctx;
use anyhow::{bail, Context, Result};
use std::ffi::OsStr;
use std::io::{BufRead, BufReader, Read};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc::{self, Sender};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(Clone, Default)]
pub struct Stop {
    cancelled: Arc<AtomicBool>,
    child: Arc<Mutex<Option<u32>>>,
}

impl Stop {
    pub fn check(&self) -> Result<()> {
        if self.cancelled.load(Ordering::SeqCst) {
            bail!("cancelled");
        }
        Ok(())
    }

    pub fn stop(&self) {
        self.cancelled.store(true, Ordering::SeqCst);
        if let Some(pid) = *self.child.lock().unwrap() {
            let _ = Command::new(windows_dir().join("System32\\taskkill.exe"))
                .args(["/T", "/F", "/PID", &pid.to_string()])
                .stdin(Stdio::null())
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .creation_flags(CREATE_NO_WINDOW)
                .status();
        }
    }

    fn running(&self, pid: Option<u32>) {
        *self.child.lock().unwrap() = pid;
    }
}

pub enum Echo {
    Log,
    Quiet,
}

pub struct Output {
    pub code: i32,
    pub lines: Vec<String>,
}

pub fn windows_dir() -> PathBuf {
    PathBuf::from(std::env::var("WINDIR").unwrap_or_else(|_| "C:\\Windows".into()))
}

// Sysnative is how a 32-bit process reaches the 64-bit System32; it does not exist for a 64-bit one.
fn powershell() -> PathBuf {
    let windir = windows_dir();
    let native = windir.join("Sysnative\\WindowsPowerShell\\v1.0\\powershell.exe");
    if native.is_file() {
        return native;
    }
    windir.join("System32\\WindowsPowerShell\\v1.0\\powershell.exe")
}

fn forward(stream: impl Read + Send + 'static, tx: Sender<String>) -> JoinHandle<()> {
    thread::spawn(move || {
        for raw in BufReader::new(stream).split(b'\n') {
            let Ok(raw) = raw else { break };
            let line = String::from_utf8_lossy(&raw).trim_end_matches('\r').to_string();
            if tx.send(line).is_err() {
                break;
            }
        }
    })
}

pub fn command(ctx: &Ctx, program: &Path, args: &[impl AsRef<OsStr>], cwd: &Path, echo: Echo) -> Result<Output> {
    ctx.stop.check()?;
    let mut child = Command::new(program)
        .args(args)
        .envs(ctx.env())
        .current_dir(cwd)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .creation_flags(CREATE_NO_WINDOW)
        .spawn()
        .with_context(|| format!("cannot start {}", program.display()))?;
    ctx.stop.running(Some(child.id()));

    let (tx, rx) = mpsc::channel();
    let readers = [
        child.stdout.take().map(|stream| forward(stream, tx.clone())),
        child.stderr.take().map(|stream| forward(stream, tx.clone())),
    ];
    drop(tx);
    let mut lines = Vec::new();
    for line in rx {
        if let Echo::Log = echo {
            ctx.log(line.as_str());
        }
        lines.push(line);
    }
    for reader in readers.into_iter().flatten() {
        let _ = reader.join();
    }
    let status = child.wait()?;
    ctx.stop.running(None);
    ctx.stop.check()?;
    Ok(Output { code: status.code().unwrap_or(-1), lines })
}

pub fn script(ctx: &Ctx, name: &str, args: &[&str]) -> Result<Output> {
    let file = ctx.root.join("scripts").join(name);
    let mut all: Vec<&OsStr> = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File"].map(OsStr::new).to_vec();
    all.push(file.as_os_str());
    all.extend(args.iter().map(OsStr::new));
    let out = command(ctx, &powershell(), &all, &ctx.root, Echo::Log)?;
    if out.code != 0 {
        bail!("{} failed (exit code {})", name, out.code);
    }
    Ok(out)
}
