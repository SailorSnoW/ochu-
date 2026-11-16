use std::{env, fs, io, path::Path};

// bin/ and obj/ are left out: their restore files name the machine's folders.
fn copy_tree(from: &Path, to: &Path) -> io::Result<()> {
    fs::create_dir_all(to)?;
    for entry in fs::read_dir(from)? {
        let entry = entry?;
        let name = entry.file_name();
        let path = entry.path();
        if !path.is_dir() {
            fs::copy(&path, to.join(&name))?;
        } else if name != "bin" && name != "obj" {
            copy_tree(&path, &to.join(&name))?;
        }
    }
    Ok(())
}

fn main() {
    for path in ["../scripts", "../build", "../docs", "../global.json", "../game.sha256", "../src.sha256", "assets/icon.ico"] {
        println!("cargo:rerun-if-changed={}", path);
    }
    let out = env::var("OUT_DIR").unwrap();
    let out = Path::new(&out);
    let payload = out.join("payload");
    if payload.exists() {
        fs::remove_dir_all(&payload).unwrap();
    }
    for dir in ["scripts", "build", "docs"] {
        copy_tree(&Path::new("..").join(dir), &payload.join(dir)).unwrap();
    }
    // src.sha256 does not exist on a tree where the pipeline never ran: an empty one is embedded.
    fs::write(out.join("src.sha256"), fs::read("../src.sha256").unwrap_or_default()).unwrap();

    let mut resource = winresource::WindowsResource::new();
    resource.set_icon("assets/icon.ico");
    resource.set("ProductName", "ochu!!");
    resource.set("FileDescription", "ochu!!");
    resource.set("LegalCopyright", "MIT license. Not affiliated with ppy Pty Ltd.");
    resource.compile().unwrap();
}
