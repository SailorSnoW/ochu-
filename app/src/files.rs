use anyhow::{Context, Result};
use std::fs;
use std::path::{Path, PathBuf};

pub fn walk(dir: &Path) -> Result<Vec<PathBuf>> {
    fn visit(dir: &Path, out: &mut Vec<PathBuf>) -> Result<()> {
        for entry in fs::read_dir(dir).with_context(|| format!("cannot list {}", dir.display()))? {
            let path = entry?.path();
            if path.is_dir() {
                visit(&path, out)?;
            } else {
                out.push(path);
            }
        }
        Ok(())
    }
    let mut out = Vec::new();
    visit(dir, &mut out)?;
    Ok(out)
}

pub fn empty_dir(dir: &Path) -> Result<()> {
    if dir.exists() {
        fs::remove_dir_all(dir).with_context(|| format!("cannot empty {}", dir.display()))?;
    }
    fs::create_dir_all(dir).with_context(|| format!("cannot create {}", dir.display()))
}

pub fn write(path: &Path, contents: impl AsRef<[u8]>) -> Result<()> {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)?;
    }
    fs::write(path, contents).with_context(|| format!("cannot write {}", path.display()))
}
