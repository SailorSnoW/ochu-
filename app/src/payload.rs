use crate::files;
use anyhow::Result;
use include_dir::{include_dir, Dir, DirEntry};
use std::path::Path;

static PAYLOAD: Dir = include_dir!("$OUT_DIR/payload");

pub const GLOBAL_JSON: &str = include_str!("../../global.json");
pub const GAME_SHA256: &str = include_str!("../../game.sha256");
// Empty when the program was built on a tree without src.sha256 (build.rs).
pub const SRC_SHA256: &str = include_str!(concat!(env!("OUT_DIR"), "/src.sha256"));

fn write_dir(dir: &Dir, to: &Path) -> Result<()> {
    for entry in dir.entries() {
        match entry {
            DirEntry::Dir(sub) => write_dir(sub, to)?,
            DirEntry::File(file) => files::write(&to.join(file.path()), file.contents())?,
        }
    }
    Ok(())
}

pub fn extract(root: &Path) -> Result<()> {
    write_dir(&PAYLOAD, root)?;
    for (name, text) in [("global.json", GLOBAL_JSON), ("game.sha256", GAME_SHA256), ("src.sha256", SRC_SHA256)] {
        files::write(&root.join(name), text)?;
    }
    Ok(())
}
