use crate::files;
use anyhow::{bail, Context, Result};
use sha2::{Digest, Sha256};
use std::collections::BTreeMap;
use std::fs;
use std::io::Read;
use std::path::Path;

pub fn digest_file<D: Digest>(path: &Path) -> Result<String> {
    let mut file = fs::File::open(path).with_context(|| format!("cannot read {}", path.display()))?;
    let mut hasher = D::new();
    let mut buffer = vec![0u8; 1 << 20];
    loop {
        let n = file.read(&mut buffer)?;
        if n == 0 {
            break;
        }
        hasher.update(&buffer[..n]);
    }
    Ok(hasher.finalize().iter().map(|b| format!("{:02x}", b)).collect())
}

pub fn hash_file(path: &Path) -> Result<String> {
    digest_file::<Sha256>(path)
}

pub fn parse(text: &str) -> BTreeMap<String, String> {
    text.lines()
        .filter_map(|line| line.trim_start_matches('\u{feff}').trim_end().split_once(" *"))
        .filter(|(hash, _)| hash.len() == 64 && hash.bytes().all(|b| b.is_ascii_hexdigit()))
        .map(|(hash, name)| (name.to_string(), hash.to_ascii_lowercase()))
        .collect()
}

pub fn verify_game(dir: &Path, manifest: &str) -> Vec<String> {
    let expected = parse(manifest);
    let mut bad = Vec::new();
    for name in crate::CORE_FILES {
        let path = dir.join(name);
        if !path.is_file() {
            bad.push(format!("MISSING  {}", name));
            continue;
        }
        match (hash_file(&path), expected.get(name)) {
            (Ok(hash), Some(want)) if &hash == want => {}
            (Ok(_), Some(_)) => bad.push(format!("MODIFIED {}", name)),
            (Err(e), _) => bad.push(format!("UNREADABLE {} ({})", name, e)),
            (_, None) => bad.push(format!("NOT IN game.sha256: {}", name)),
        }
    }
    bad
}

pub fn hash_tree(src: &Path) -> Result<BTreeMap<String, String>> {
    if !src.is_dir() {
        bail!("no source folder at {}", src.display());
    }
    let mut tree = BTreeMap::new();
    for path in files::walk(src)? {
        let relative = path.strip_prefix(src)?.to_string_lossy().replace('\\', "/");
        tree.insert(relative, hash_file(&path)?);
    }
    Ok(tree)
}

pub fn write_src(src: &Path, manifest: &Path) -> Result<usize> {
    let tree = hash_tree(src)?;
    let text: String = tree.iter().map(|(name, hash)| format!("{} *{}\n", hash, name)).collect();
    fs::write(manifest, text)?;
    Ok(tree.len())
}

pub fn verify_src(src: &Path, manifest: &str) -> Result<(usize, Vec<String>)> {
    let expected = parse(manifest);
    let current = hash_tree(src)?;
    let mut bad = Vec::new();
    for (name, hash) in &expected {
        match current.get(name) {
            None => bad.push(format!("MISSING  {}", name)),
            Some(h) if h != hash => bad.push(format!("MODIFIED {}", name)),
            _ => {}
        }
    }
    bad.extend(current.keys().filter(|name| !expected.contains_key(*name)).map(|name| format!("EXTRA    {}", name)));
    Ok((expected.len(), bad))
}
