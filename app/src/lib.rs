pub mod files;
pub mod generate;
pub mod locate;
pub mod manifest;
pub mod payload;
pub mod pipeline;
pub mod run;
pub mod srcstep;
pub mod tools;

use std::path::PathBuf;
use std::sync::Mutex;

pub const REFERENCE_BUILD: &str = "1.3.3.8";

pub const CORE_FILES: [&str; 6] = [
    "osu!.exe",
    "osu!gameplay.dll",
    "osu!ui.dll",
    "osu!seasonal.dll",
    "OpenTK.dll",
    "Microsoft.Ink.dll",
];

pub enum Event {
    Line(String),
    Step(usize, usize, String),
}

pub struct Ctx {
    pub root: PathBuf,
    pub stop: run::Stop,
    sink: Box<dyn Fn(Event) + Send + Sync>,
    env: Mutex<Vec<(String, String)>>,
}

impl Ctx {
    pub fn new(root: PathBuf, stop: run::Stop, sink: impl Fn(Event) + Send + Sync + 'static) -> Ctx {
        Ctx { root, stop, sink: Box::new(sink), env: Mutex::new(Vec::new()) }
    }

    pub fn log(&self, line: impl Into<String>) {
        (self.sink)(Event::Line(line.into()));
    }

    pub fn step(&self, index: usize, total: usize, title: &str) {
        (self.sink)(Event::Step(index, total, title.to_string()));
    }

    pub fn path(&self, relative: &str) -> PathBuf {
        self.root.join(relative)
    }

    pub fn set_env(&self, key: &str, value: impl Into<String>) {
        let mut env = self.env.lock().unwrap();
        env.retain(|(k, _)| k != key);
        env.push((key.to_string(), value.into()));
    }

    pub fn env(&self) -> Vec<(String, String)> {
        self.env.lock().unwrap().clone()
    }
}
