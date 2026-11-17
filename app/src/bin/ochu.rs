#![windows_subsystem = "windows"]

use eframe::egui;
use egui::{Color32, RichText};
use ochu::generate::{self, Outcome};
use ochu::run::Stop;
use ochu::{locate, manifest, payload, Ctx, Event, REFERENCE_BUILD};
use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, Sender};
use std::thread;

const ICON: &[u8] = include_bytes!("../../assets/icon-256.png");
const ACCENT: Color32 = Color32::from_rgb(0x4F, 0xC3, 0xF7);
const GOOD: Color32 = Color32::from_rgb(0x7B, 0xE0, 0xA4);
const BAD: Color32 = Color32::from_rgb(0xFF, 0x8A, 0x80);
const MUTED: Color32 = Color32::from_rgb(0x9F, 0xB6, 0xCC);
const BUTTON: Color32 = Color32::from_rgb(0x1E, 0x6F, 0xA8);
const LOG_LINES: usize = 4000;

enum Msg {
    Located(Option<PathBuf>),
    GameChecked(String, Vec<String>),
    Run(Event),
    Finished(Result<Outcome, String>),
}

#[derive(PartialEq)]
enum GameState {
    Searching,
    Empty,
    NoGame,
    Checking,
    Reference,
    Other(Vec<String>),
}

impl GameState {
    fn verdict(&self) -> (String, Color32) {
        match self {
            GameState::Searching => ("Looking for osu! on this machine...".into(), MUTED),
            GameState::Empty => ("No installation found: choose the folder that holds osu!.exe.".into(), MUTED),
            GameState::NoGame => ("There is no osu!.exe in this folder.".into(), BAD),
            GameState::Checking => ("Checking the build...".into(), MUTED),
            GameState::Reference => (
                format!("Reference build {}: every file the pipeline reads is the expected one.", REFERENCE_BUILD),
                GOOD,
            ),
            GameState::Other(bad) => {
                let files: Vec<&str> = bad.iter().filter_map(|l| l.split_whitespace().last()).collect();
                let text = format!(
                    "This is not build {} ({}). The tables of ochu!! describe that exact build: another one cannot be used.",
                    REFERENCE_BUILD,
                    files.join(", ")
                );
                (text, BAD)
            }
        }
    }
}

struct Progress {
    index: usize,
    total: usize,
    title: String,
}

impl Progress {
    fn fraction(&self) -> f32 {
        if self.total == 0 {
            return 0.0;
        }
        self.index.saturating_sub(1) as f32 / self.total as f32
    }
}

struct App {
    game: String,
    game_checked: String,
    game_state: GameState,
    out: String,
    check: bool,
    autostart: bool,
    running: bool,
    progress: Progress,
    log: Vec<String>,
    error: Option<String>,
    outcome: Option<Outcome>,
    stop: Stop,
    tx: Sender<Msg>,
    rx: Receiver<Msg>,
    logo: Option<egui::TextureHandle>,
}

fn argument(name: &str) -> Option<String> {
    std::env::args().skip_while(|a| a != name).nth(1)
}

fn flag(name: &str) -> bool {
    std::env::args().any(|a| a == name)
}

fn note(ui: &mut egui::Ui, text: impl Into<String>, color: Color32) {
    ui.label(RichText::new(text).color(color));
}

fn heading(ui: &mut egui::Ui, text: &str) {
    ui.label(RichText::new(text).strong());
}

fn style(ctx: &egui::Context) {
    let mut visuals = egui::Visuals::dark();
    visuals.panel_fill = Color32::from_rgb(0x0B, 0x1D, 0x30);
    visuals.extreme_bg_color = Color32::from_rgb(0x06, 0x12, 0x20);
    visuals.selection.bg_fill = BUTTON;
    visuals.hyperlink_color = ACCENT;
    visuals.widgets.inactive.weak_bg_fill = Color32::from_rgb(0x16, 0x33, 0x50);
    visuals.widgets.hovered.weak_bg_fill = Color32::from_rgb(0x1E, 0x4A, 0x73);
    ctx.set_visuals(visuals);
    ctx.style_mut(|style| {
        style.spacing.item_spacing = egui::vec2(8.0, 8.0);
        style.spacing.button_padding = egui::vec2(12.0, 6.0);
    });
}

fn folder_row(ui: &mut egui::Ui, value: &mut String, enabled: bool, hint: &str) {
    ui.add_enabled_ui(enabled, |ui| {
        // Right to left, the button first: the field then takes the width that is left.
        let size = egui::vec2(ui.available_width(), ui.spacing().interact_size.y + 4.0);
        ui.allocate_ui_with_layout(size, egui::Layout::right_to_left(egui::Align::Center), |ui| {
            if ui.button("Browse...").clicked() {
                let mut dialog = rfd::FileDialog::new();
                if Path::new(value.trim()).is_dir() {
                    dialog = dialog.set_directory(value.trim());
                }
                if let Some(dir) = dialog.pick_folder() {
                    *value = dir.to_string_lossy().into_owned();
                }
            }
            ui.add(egui::TextEdit::singleline(value).hint_text(hint).desired_width(f32::INFINITY));
        });
    });
}

impl App {
    fn new(cc: &eframe::CreationContext<'_>) -> App {
        let ctx = &cc.egui_ctx;
        style(ctx);
        let logo = eframe::icon_data::from_png_bytes(ICON).ok().map(|icon| {
            let image =
                egui::ColorImage::from_rgba_unmultiplied([icon.width as usize, icon.height as usize], &icon.rgba);
            ctx.load_texture("logo", image, egui::TextureOptions::LINEAR)
        });

        let (tx, rx) = mpsc::channel();
        let game = argument("--game").unwrap_or_default();
        let game_state = if game.is_empty() { GameState::Searching } else { GameState::Empty };
        if game.is_empty() {
            let (tx, ctx) = (tx.clone(), ctx.clone());
            thread::spawn(move || {
                let _ = tx.send(Msg::Located(locate::best(payload::GAME_SHA256)));
                ctx.request_repaint();
            });
        }
        let home = std::env::var("USERPROFILE").unwrap_or_else(|_| "C:\\".into());
        App {
            game,
            game_checked: String::new(),
            game_state,
            out: argument("--out").unwrap_or_else(|| Path::new(&home).join("ochu").to_string_lossy().into_owned()),
            check: flag("--check"),
            autostart: flag("--start"),
            running: false,
            progress: Progress { index: 0, total: 0, title: String::new() },
            log: Vec::new(),
            error: None,
            outcome: None,
            stop: Stop::default(),
            tx,
            rx,
            logo,
        }
    }

    fn check_game(&mut self, ctx: &egui::Context) {
        if self.game == self.game_checked || self.game_state == GameState::Searching {
            return;
        }
        self.game_checked = self.game.clone();
        let dir = PathBuf::from(self.game.trim());
        self.game_state = if self.game.trim().is_empty() {
            GameState::Empty
        } else if !locate::has_game(&dir) {
            GameState::NoGame
        } else {
            let (tx, ctx, asked) = (self.tx.clone(), ctx.clone(), self.game.clone());
            thread::spawn(move || {
                let bad = manifest::verify_game(&dir, payload::GAME_SHA256);
                let _ = tx.send(Msg::GameChecked(asked, bad));
                ctx.request_repaint();
            });
            GameState::Checking
        };
    }

    fn receive(&mut self) {
        while let Ok(msg) = self.rx.try_recv() {
            match msg {
                Msg::Located(found) => {
                    self.game_state = GameState::Empty;
                    if let Some(dir) = found {
                        self.game = dir.to_string_lossy().into_owned();
                    }
                }
                // The field may have changed while the check ran: an answer for an older value is dropped.
                Msg::GameChecked(asked, bad) if asked == self.game => {
                    self.game_state = if bad.is_empty() { GameState::Reference } else { GameState::Other(bad) };
                }
                Msg::GameChecked(..) => {}
                Msg::Run(Event::Line(line)) => {
                    self.log.push(line);
                    if self.log.len() > LOG_LINES {
                        self.log.drain(..LOG_LINES / 4);
                    }
                }
                Msg::Run(Event::Step(index, total, title)) => {
                    self.log.push(format!("-- {}/{} {}", index, total, title));
                    self.progress = Progress { index, total, title };
                }
                Msg::Finished(Ok(outcome)) => {
                    self.running = false;
                    self.progress =
                        Progress { index: self.progress.total, total: self.progress.total, title: "Done".into() };
                    self.outcome = Some(outcome);
                }
                Msg::Finished(Err(error)) => {
                    self.running = false;
                    self.log.push(format!("error: {}", error));
                    self.error = Some(error);
                }
            }
        }
    }

    fn start(&mut self, ctx: &egui::Context) {
        self.error = None;
        self.outcome = None;
        self.log.clear();
        let game = PathBuf::from(self.game.trim());
        let workspace = match generate::workspace_for(Path::new(self.out.trim()), &game) {
            Ok(workspace) => workspace,
            Err(error) => {
                self.error = Some(error.to_string());
                return;
            }
        };
        self.running = true;
        self.progress = Progress { index: 0, total: generate::steps(self.check), title: "Starting".into() };
        self.stop = Stop::default();
        let (tx, repaint, check, stop) = (self.tx.clone(), ctx.clone(), self.check, self.stop.clone());
        thread::spawn(move || {
            let (sink_tx, sink_repaint) = (tx.clone(), repaint.clone());
            let run = Ctx::new(workspace, stop, move |event| {
                let _ = sink_tx.send(Msg::Run(event));
                sink_repaint.request_repaint();
            });
            let result = generate::generate(&run, &game, check).map_err(|e| format!("{:#}", e));
            let _ = tx.send(Msg::Finished(result));
            repaint.request_repaint();
        });
    }

    fn header(&self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            if let Some(logo) = &self.logo {
                ui.add(egui::Image::new(logo).fit_to_exact_size(egui::vec2(56.0, 56.0)).corner_radius(12.0));
            }
            ui.vertical(|ui| {
                ui.label(RichText::new("ochu!!").size(26.0).strong().color(Color32::WHITE));
                note(
                    ui,
                    "The decompiled source of the legacy osu! client, generated from your own copy of the game.",
                    MUTED,
                );
            });
        });
        ui.add_space(6.0);
        ui.separator();
    }

    fn form(&mut self, ui: &mut egui::Ui) {
        heading(ui, "1. Your osu! installation");
        folder_row(ui, &mut self.game, !self.running, "The folder that holds osu!.exe");
        let (verdict, color) = self.game_state.verdict();
        note(ui, verdict, color);
        ui.add_space(4.0);

        heading(ui, "2. Where to work");
        folder_row(ui, &mut self.out, !self.running, "An empty folder");
        note(
            ui,
            "An empty folder: about 350 MB of tools and intermediate files, 1.2 GB if the .NET SDK has to be downloaded too. The source is written to its src subfolder.",
            MUTED,
        );
        ui.add_space(4.0);

        ui.add_enabled(
            !self.running,
            egui::Checkbox::new(
                &mut self.check,
                "Also rebuild the source and compare it with the original, method by method (a few more minutes)",
            ),
        );
        note(
            ui,
            "Your game folder is only read and the game is never started. The source is osu!'s code, the work of ppy Pty Ltd: it is for your own study and must not be redistributed.",
            MUTED,
        );
        ui.add_space(4.0);
    }

    fn actions(&mut self, ui: &mut egui::Ui) {
        ui.horizontal(|ui| {
            if self.running {
                if ui.button("Cancel").clicked() {
                    self.stop.stop();
                }
                let Progress { index, total, title } = &self.progress;
                ui.add(
                    egui::ProgressBar::new(self.progress.fraction())
                        .text(format!("{}/{}  {}", index, total, title))
                        .animate(true),
                );
                return;
            }
            let ready = self.game_state == GameState::Reference && !self.out.trim().is_empty();
            let button =
                egui::Button::new(RichText::new("Generate the source").strong().color(Color32::WHITE)).fill(BUTTON);
            if ui.add_enabled(ready, button).clicked() {
                self.start(ui.ctx());
            }
            if let Some(outcome) = &self.outcome {
                if ui.button("Open the source folder").clicked() {
                    let _ = std::process::Command::new("explorer.exe").arg(&outcome.src).spawn();
                }
            }
        });
    }

    fn result(&self, ui: &mut egui::Ui) {
        if let Some(outcome) = &self.outcome {
            let verdict = if outcome.verified {
                "identical to the reference source"
            } else {
                "not compared with the reference source"
            };
            note(ui, format!("Done: {} files in {}, {}.", outcome.files, outcome.src.display(), verdict), GOOD);
            // The ALL line of scripts/match.ps1: category, methods, match, diff, missing, rate.
            let fields: Vec<&str> = outcome.rate.as_deref().unwrap_or_default().split_whitespace().collect();
            if fields.len() >= 6 {
                note(
                    ui,
                    format!(
                        "Rebuilt and compared: {} of {} methods give back the original IL ({}).",
                        fields[2], fields[1], fields[5]
                    ),
                    GOOD,
                );
            }
        }
        if let Some(error) = &self.error {
            note(ui, error, BAD);
        }
        ui.add_space(4.0);
    }

    fn log_view(&self, ui: &mut egui::Ui) {
        let row_height = ui.text_style_height(&egui::TextStyle::Monospace);
        egui::Frame::new().fill(ui.visuals().extreme_bg_color).corner_radius(6.0).inner_margin(8.0).show(ui, |ui| {
            egui::ScrollArea::both().auto_shrink([false, false]).stick_to_bottom(true).show_rows(
                ui,
                row_height,
                self.log.len(),
                |ui, rows| {
                    for line in &self.log[rows] {
                        let color = if line.starts_with("-- ") {
                            ACCENT
                        } else if line.starts_with("error") {
                            BAD
                        } else {
                            MUTED
                        };
                        ui.add(
                            egui::Label::new(RichText::new(line).monospace().color(color))
                                .wrap_mode(egui::TextWrapMode::Extend),
                        );
                    }
                },
            );
        });
    }
}

impl eframe::App for App {
    fn update(&mut self, ctx: &egui::Context, _frame: &mut eframe::Frame) {
        self.receive();
        self.check_game(ctx);
        if self.autostart && self.game_state == GameState::Reference && !self.running {
            self.autostart = false;
            self.start(ctx);
        }
        if self.running && ctx.input(|i| i.viewport().close_requested()) {
            self.stop.stop();
        }
        egui::CentralPanel::default().frame(egui::Frame::central_panel(&ctx.style()).inner_margin(20.0)).show(
            ctx,
            |ui| {
                self.header(ui);
                self.form(ui);
                self.actions(ui);
                self.result(ui);
                self.log_view(ui);
            },
        );
    }
}

fn main() -> eframe::Result {
    let mut viewport = egui::ViewportBuilder::default()
        .with_title("ochu!!")
        .with_inner_size([820.0, 700.0])
        .with_min_inner_size([640.0, 560.0]);
    if let Ok(icon) = eframe::icon_data::from_png_bytes(ICON) {
        viewport = viewport.with_icon(icon);
    }
    let options = eframe::NativeOptions { viewport, ..Default::default() };
    eframe::run_native("ochu!!", options, Box::new(|cc| Ok(Box::new(App::new(cc)))))
}
