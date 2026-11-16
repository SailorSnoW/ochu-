use std::env;
use std::path::{Path, PathBuf};
use winreg::enums::{HKEY_CLASSES_ROOT, HKEY_CURRENT_USER};
use winreg::RegKey;

const HANDLERS: [&str; 6] = ["osu", "osu!", "osustable.Uri.osu", "osustable.File.osz", "osustable.File.osu", "osustable.File.osk"];

pub fn has_game(dir: &Path) -> bool {
    dir.join("osu!.exe").is_file()
}

fn folder_of_command(command: &str) -> Option<PathBuf> {
    let command = command.trim();
    let exe = match command.strip_prefix('"') {
        Some(rest) => rest.split('"').next()?,
        None => &command[..command.to_ascii_lowercase().find(".exe")? + 4],
    };
    Path::new(exe).parent().map(Path::to_path_buf)
}

fn registered() -> Vec<PathBuf> {
    let mut found = Vec::new();
    for handler in HANDLERS {
        let class = format!("{}\\shell\\open\\command", handler);
        let keys = [
            RegKey::predef(HKEY_CLASSES_ROOT).open_subkey(&class),
            RegKey::predef(HKEY_CURRENT_USER).open_subkey(format!("Software\\Classes\\{}", class)),
        ];
        for key in keys.into_iter().flatten() {
            let command: Option<String> = key.get_value("").ok();
            found.extend(command.as_deref().and_then(folder_of_command));
        }
    }
    found
}

fn usual() -> Vec<PathBuf> {
    let mut found: Vec<PathBuf> = ["ProgramFiles", "ProgramFiles(x86)", "USERPROFILE"]
        .iter()
        .filter_map(|var| env::var(var).ok())
        .map(|base| Path::new(&base).join("osu!"))
        .collect();
    for drive in b'C'..=b'Z' {
        let root = format!("{}:\\", drive as char);
        if Path::new(&root).is_dir() {
            found.extend(["osu!", "Games\\osu!", "Jeux\\osu!"].map(|sub| Path::new(&root).join(sub)));
        }
    }
    found
}

pub fn candidates() -> Vec<PathBuf> {
    let installer_default = env::var("LOCALAPPDATA").ok().map(|local| Path::new(&local).join("osu!"));
    let mut found: Vec<PathBuf> = Vec::new();
    for dir in installer_default.into_iter().chain(registered()).chain(usual()) {
        let known = found.iter().any(|f| f.to_string_lossy().eq_ignore_ascii_case(&dir.to_string_lossy()));
        if !known && has_game(&dir) {
            found.push(dir);
        }
    }
    found
}

pub fn best(game_manifest: &str) -> Option<PathBuf> {
    let all = candidates();
    all.iter()
        .find(|dir| crate::manifest::verify_game(dir, game_manifest).is_empty())
        .cloned()
        .or_else(|| all.into_iter().next())
}
