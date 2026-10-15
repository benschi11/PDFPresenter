//! Prototyp B: PDF-Präsentation mit Crossfade und Stift-Annotationen (winit + wgpu + PDFium).

mod gpu;
mod ink;
mod pdf;

use std::collections::{HashMap, VecDeque};
use std::path::PathBuf;
use std::sync::Arc;
use std::time::{Duration, Instant};

use winit::application::ApplicationHandler;
use winit::cursor::CursorIcon;
use winit::dpi::LogicalSize;
use winit::event::{
    ButtonSource, ElementState, MouseButton, PointerKind, PointerSource, TabletToolButton, TabletToolData,
    TabletToolKind, WindowEvent,
};
use winit::event_loop::{ActiveEventLoop, EventLoop, EventLoopProxy};
use winit::keyboard::{Key, ModifiersState, NamedKey};
use winit::monitor::Fullscreen;
use winit::window::{Window, WindowAttributes, WindowId};

use gpu::{Composite, Gpu, PageTexture, Quad};
use ink::{InkPoint, Stroke, Tool, Vertex};
use pdf::{PdfWorker, Reply};

const PALM_REJECT: Duration = Duration::from_millis(1000);

struct Args {
    pdf: Option<PathBuf>,
    fade_ms: u32,
    fullscreen: bool,
    low_latency: bool,
    touch: bool,
    on_demand: bool,
}

impl Args {
    fn parse() -> Self {
        let mut a =
            Args { pdf: None, fade_ms: 400, fullscreen: false, low_latency: false, touch: false, on_demand: false };
        let mut it = std::env::args().skip(1);
        while let Some(arg) = it.next() {
            match arg.as_str() {
                "--fade" => a.fade_ms = it.next().and_then(|v| v.parse().ok()).unwrap_or(a.fade_ms),
                "--fullscreen" => a.fullscreen = true,
                "--low-latency" => a.low_latency = true,
                "--touch" => a.touch = true,
                "--on-demand" => a.on_demand = true,
                "-h" | "--help" => {
                    println!(
                        "pdfpresenter-rust <datei.pdf> [Optionen]\n  \
                         --fade <ms>     Dauer des Crossfades (Standard 400)\n  \
                         --fullscreen    im Vollbild starten (F11 schaltet um)\n  \
                         --low-latency   Mailbox/Immediate statt VSync-Fifo, Frame-Latenz 1\n  \
                         --touch         Finger dürfen zeichnen (Standard: nur Stift/Maus)\n  \
                         --on-demand     nur bei Bedarf rendern statt mit konstanter Bildrate"
                    );
                    std::process::exit(0);
                }
                _ => a.pdf = Some(PathBuf::from(arg)),
            }
        }
        a
    }
}

struct Active {
    kind: PointerKind,
    stroke: Option<Stroke>, // None = Radieren
}

#[derive(Default)]
struct Stats {
    frame_times: VecDeque<f32>,
    last_frame: Option<Instant>,
    pending_input: Option<Instant>,
    latencies: Vec<f32>,
    points: u32,
    frames: u32,
    last_report: Option<Instant>,
    pressure: f32,
    pointer: &'static str,
    report: String,
}

struct App {
    args: Args,
    proxy: EventLoopProxy,
    instance: wgpu::Instance,
    gpu: Option<Gpu>,

    pdf: Option<PdfWorker>,
    page_sizes: Vec<(f32, f32)>,
    queued: Vec<(usize, u32, u32)>,
    pages: HashMap<usize, PageTexture>,
    composites: HashMap<usize, Composite>,
    ink: HashMap<usize, Vec<Stroke>>,

    page: usize,
    from_page: Option<usize>,
    fade_start: Option<Instant>,
    fade_pending: bool,
    fade_ms: u32,

    tool: Tool,
    color_index: usize,
    active: Option<Active>,
    pointer_pos: Option<(f32, f32)>,
    barrel_held: bool,
    touch_draws: bool,
    last_pen_seen: Option<Instant>,
    modifiers: ModifiersState,

    hud: bool,
    stats: Stats,
}

impl App {
    fn new(args: Args, proxy: EventLoopProxy, instance: wgpu::Instance) -> Self {
        Self {
            fade_ms: args.fade_ms,
            touch_draws: args.touch,
            args,
            proxy,
            instance,
            gpu: None,
            pdf: None,
            page_sizes: Vec::new(),
            queued: Vec::new(),
            pages: HashMap::new(),
            composites: HashMap::new(),
            ink: HashMap::new(),
            page: 0,
            from_page: None,
            fade_start: None,
            fade_pending: false,
            tool: Tool::Pen,
            color_index: 0,
            active: None,
            pointer_pos: None,
            barrel_held: false,
            last_pen_seen: None,
            modifiers: ModifiersState::empty(),
            hud: true,
            stats: Stats::default(),
        }
    }

    fn window(&self) -> Option<&Arc<dyn Window>> {
        self.gpu.as_ref().map(|g| &g.window)
    }

    fn request_redraw(&self) {
        if let Some(w) = self.window() {
            w.request_redraw();
        }
    }

    fn update_title(&self) {
        let Some(w) = self.window() else { return };
        let name = self.args.pdf.as_ref().and_then(|p| p.file_name()).map(|n| n.to_string_lossy()).unwrap_or_default();
        let mut title = if self.page_sizes.is_empty() {
            "PDF-Presenter (Rust/wgpu)".to_string()
        } else {
            format!("{name} – Seite {}/{} – PDF-Presenter (Rust/wgpu)", self.page + 1, self.page_sizes.len())
        };
        if self.hud && !self.stats.report.is_empty() {
            let tool = match self.tool {
                Tool::Pen => format!("Stift ({})", ink::PEN_COLOR_NAMES[self.color_index]),
                Tool::Highlighter => "Textmarker".into(),
                Tool::Eraser => "Radierer".into(),
            };
            let present = self.gpu.as_ref().map(|g| g.present_mode);
            title = format!(
                "{} · {tool} · Fade {} ms · Touch {} · {present:?} · {title}",
                self.stats.report,
                self.fade_ms,
                if self.touch_draws { "an" } else { "aus" }
            );
        }
        w.set_title(&title);
    }

    // ───────────────────────────── Geometrie ───────────────────────────────

    /// Zielrechteck einer Seite in physischen Pixeln (x, y, w, h). Passt das Seitenverhältnis
    /// (±1 %), wird die Fläche komplett gefüllt – randlos. Sonst eingepasst und zentriert.
    fn dest_rect(&self, page: usize) -> (u32, u32, u32, u32) {
        let (vw, vh) = self.gpu.as_ref().map(|g| g.size()).unwrap_or((1, 1));
        let Some(&(pw, ph)) = self.page_sizes.get(page) else { return (0, 0, vw, vh) };
        let (page_aspect, view_aspect) = (pw / ph, vw as f32 / vh as f32);
        if ((page_aspect - view_aspect) / page_aspect).abs() < 0.01 {
            (0, 0, vw, vh)
        } else if page_aspect > view_aspect {
            let h = (vw as f32 / page_aspect).round() as u32;
            (0, (vh - h) / 2, vw, h)
        } else {
            let w = (vh as f32 * page_aspect).round() as u32;
            ((vw - w) / 2, 0, w, vh)
        }
    }

    fn to_page_units(&self, x: f32, y: f32) -> (f32, f32) {
        let (dx, dy, _, dh) = self.dest_rect(self.page);
        ((x - dx as f32) / dh as f32, (y - dy as f32) / dh as f32)
    }

    fn ink_transform(&self, page: usize) -> gpu::InkTransform {
        let (vw, vh) = self.gpu.as_ref().map(|g| g.size()).unwrap_or((1, 1));
        let (dx, dy, _, dh) = self.dest_rect(page);
        let (vw, vh, dx, dy, dh) = (vw as f32, vh as f32, dx as f32, dy as f32, dh as f32);
        [dh * 2.0 / vw, dh * 2.0 / vh, dx * 2.0 / vw - 1.0, 1.0 - dy * 2.0 / vh]
    }

    // ───────────────────────────── PDF-Seiten ──────────────────────────────

    fn open(&mut self, path: PathBuf) {
        self.pdf = Some(PdfWorker::spawn(path.clone(), self.proxy.clone()));
        self.args.pdf = Some(path);
        self.page_sizes.clear();
        self.queued.clear();
        self.pages.clear();
        self.composites.clear();
        self.ink.clear();
        self.page = 0;
        self.from_page = None;
    }

    /// Aktuelle Seite und Nachbarn in Zielgröße anfordern, Entferntes verwerfen.
    fn request_pages(&mut self) {
        let Some(pdf) = &self.pdf else { return };
        if self.page_sizes.is_empty() {
            return;
        }
        let n = self.page_sizes.len() as isize;
        let wanted: Vec<_> = [0isize, 1, -1]
            .iter()
            .map(|d| self.page as isize + d)
            .filter(|p| (0..n).contains(p))
            .map(|p| p as usize)
            .filter_map(|p| {
                let (_, _, w, h) = self.dest_rect(p);
                let have = self.pages.get(&p).is_some_and(|t| t.width == w && t.height == h);
                (!have).then_some((p, w, h))
            })
            .collect();
        pdf.request(&wanted, &mut self.queued);

        let (page, from) = (self.page, self.from_page);
        self.pages.retain(|&p, _| p.abs_diff(page) <= 2 || Some(p) == from);
        self.composites.retain(|&p, _| p == page || Some(p) == from);
    }

    fn handle_replies(&mut self) {
        let mut changed = false;
        while let Some(reply) = self.pdf.as_ref().and_then(|p| p.try_recv()) {
            match reply {
                Reply::Opened { page_sizes } => {
                    println!("PDF geöffnet: {} Seiten", page_sizes.len());
                    self.page_sizes = page_sizes;
                    self.request_pages();
                    self.update_title();
                }
                Reply::Page(p) => {
                    self.queued.retain(|&(i, w, h)| (i, w, h) != (p.index, p.width, p.height));
                    let Some(gpu) = &self.gpu else { continue };
                    let tex = gpu.upload_page(p.width, p.height, &p.rgba);
                    self.pages.insert(p.index, tex);
                    self.composites.remove(&p.index); // neu aufbauen
                    if self.fade_pending && p.index == self.page {
                        self.start_fade();
                    }
                    changed = true;
                }
                Reply::Error(e) => {
                    eprintln!("Fehler: {e}");
                    if let Some(w) = self.window() {
                        w.set_title(&format!("Fehler: {e}"));
                    }
                }
            }
        }
        if changed {
            self.request_redraw();
        }
    }

    fn ensure_composite(&mut self, page: usize) {
        let Some(tex) = self.pages.get(&page) else { return };
        let ok = self.composites.get(&page).is_some_and(|c| c.width == tex.width && c.height == tex.height);
        if !ok {
            let gpu = self.gpu.as_ref().unwrap();
            let strokes = self.ink.get(&page).map(Vec::as_slice).unwrap_or(&[]);
            self.composites.insert(page, gpu.build_composite(tex, strokes));
        }
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    fn go_to(&mut self, page: usize) {
        if self.page_sizes.is_empty() {
            return;
        }
        let page = page.min(self.page_sizes.len() - 1);
        if page == self.page {
            return;
        }
        self.commit_active();
        self.from_page = Some(self.page);
        self.page = page;
        if self.pages.contains_key(&page) {
            self.start_fade();
        } else {
            self.fade_pending = true; // startet, sobald die Seite gerendert ist
        }
        self.request_pages();
        self.update_title();
        self.request_redraw();
    }

    fn start_fade(&mut self) {
        self.fade_pending = false;
        self.fade_start = Some(Instant::now());
    }

    /// Überblend-Fortschritt 0..1 (smoothstep), None wenn keine Überblendung läuft.
    fn fade_progress(&mut self, now: Instant) -> Option<f32> {
        self.from_page?;
        if self.fade_pending {
            return Some(0.0);
        }
        let t = match self.fade_start {
            Some(s) if self.fade_ms > 0 => now.duration_since(s).as_secs_f32() * 1000.0 / self.fade_ms as f32,
            _ => 1.0,
        };
        if t >= 1.0 {
            self.from_page = None;
            return None;
        }
        Some(t * t * (3.0 - 2.0 * t))
    }

    // ─────────────────────────────── Ink ──────────────────────────────────

    fn begin(&mut self, kind: PointerKind, erase: bool, x: f32, y: f32, pressure: f32) {
        if self.page_sizes.is_empty() {
            return;
        }
        let pen = matches!(kind, PointerKind::TabletTool(_));
        if pen && matches!(self.active.as_ref().map(|a| a.kind), Some(PointerKind::Touch(_))) {
            self.active = None; // Stift setzt auf, während ein Handballen-/Fingerstrich läuft → verwerfen
        }
        if self.active.is_some() {
            return;
        }
        let erase = erase || self.tool == Tool::Eraser;
        let stroke = (!erase).then(|| Stroke::new(self.tool, self.color_index));
        self.active = Some(Active { kind, stroke });
        self.extend(x, y, pressure);
    }

    fn extend(&mut self, x: f32, y: f32, pressure: f32) {
        let (u, v) = self.to_page_units(x, y);
        let Some(active) = &mut self.active else { return };
        self.stats.pending_input.get_or_insert_with(Instant::now);
        self.stats.points += 1;
        match &mut active.stroke {
            Some(stroke) => stroke.push(InkPoint { x: u, y: v, pressure }),
            None => {
                let strokes = self.ink.entry(self.page).or_default();
                let before = strokes.len();
                strokes.retain(|s| !s.hit_test(u, v, ink::ERASER_RADIUS));
                if strokes.len() != before {
                    self.composites.remove(&self.page);
                }
            }
        }
    }

    fn commit_active(&mut self) {
        let Some(Active { stroke: Some(stroke), .. }) = self.active.take() else { return };
        if stroke.points.is_empty() {
            return;
        }
        if let (Some(gpu), Some(c)) = (&self.gpu, self.composites.get(&self.page)) {
            gpu.append_strokes(c, std::slice::from_ref(&stroke));
        }
        self.ink.entry(self.page).or_default().push(stroke);
    }

    fn accept(&self, kind: PointerKind) -> bool {
        match kind {
            PointerKind::Touch(_) => self.touch_draws && self.last_pen_seen.is_none_or(|t| t.elapsed() > PALM_REJECT),
            _ => true,
        }
    }

    fn note_pointer(&mut self, kind: PointerKind, pressure: f32) {
        self.stats.pressure = pressure;
        self.stats.pointer = match kind {
            PointerKind::Mouse => "Maus",
            PointerKind::Touch(_) => "Finger",
            PointerKind::TabletTool(TabletToolKind::Eraser) => "Stift (Radierer)",
            PointerKind::TabletTool(_) => "Stift",
            _ => "unbekannt",
        };
        if matches!(kind, PointerKind::TabletTool(_)) {
            self.last_pen_seen = Some(Instant::now());
        }
    }

    fn handle_pointer_button(&mut self, state: ElementState, x: f32, y: f32, button: ButtonSource) {
        let (kind, erase, pressure) = match button {
            ButtonSource::Mouse(MouseButton::Left) => (PointerKind::Mouse, false, 0.5),
            ButtonSource::Mouse(MouseButton::Right) => (PointerKind::Mouse, true, 0.5),
            ButtonSource::Touch { finger_id, force } => {
                (PointerKind::Touch(finger_id), false, force.map(|f| f.normalized(None) as f32).unwrap_or(0.5))
            }
            ButtonSource::TabletTool { kind, button: TabletToolButton::Contact, data } => {
                (PointerKind::TabletTool(kind), kind == TabletToolKind::Eraser || self.barrel_held, pen_pressure(&data))
            }
            ButtonSource::TabletTool { button: TabletToolButton::Barrel, .. } => {
                self.barrel_held = state.is_pressed();
                return;
            }
            _ => return,
        };
        self.note_pointer(kind, pressure);
        match state {
            ElementState::Pressed if self.accept(kind) => self.begin(kind, erase, x, y, pressure),
            ElementState::Released if self.active.as_ref().is_some_and(|a| a.kind == kind) => self.commit_active(),
            _ => {}
        }
        self.request_redraw();
    }

    fn handle_pointer_moved(&mut self, x: f32, y: f32, source: PointerSource) {
        let pressure = match &source {
            PointerSource::TabletTool { data, .. } => pen_pressure(data),
            PointerSource::Touch { force, .. } => force.map(|f| f.normalized(None) as f32).unwrap_or(0.5),
            _ => 0.5,
        };
        let kind = PointerKind::from(source);
        self.note_pointer(kind, pressure);
        self.pointer_pos = Some((x, y));
        if self.active.as_ref().is_some_and(|a| a.kind == kind) {
            self.extend(x, y, pressure);
            self.request_redraw();
        } else if self.tool == Tool::Eraser {
            self.request_redraw();
        }
    }

    // ───────────────────────────── Tastatur ───────────────────────────────

    fn handle_key(&mut self, key: &Key, event_loop: &dyn ActiveEventLoop) {
        let ctrl = self.modifiers.control_key();
        match key {
            Key::Named(NamedKey::ArrowRight | NamedKey::ArrowDown | NamedKey::PageDown) => self.go_to(self.page + 1),
            Key::Named(NamedKey::ArrowLeft | NamedKey::ArrowUp | NamedKey::PageUp | NamedKey::Backspace) => {
                self.go_to(self.page.saturating_sub(1))
            }
            Key::Named(NamedKey::Home) => self.go_to(0),
            Key::Named(NamedKey::End) => self.go_to(usize::MAX),
            Key::Named(NamedKey::F11) => self.toggle_fullscreen(),
            Key::Named(NamedKey::Escape) => {
                if self.window().is_some_and(|w| w.fullscreen().is_some()) {
                    self.toggle_fullscreen();
                }
            }
            Key::Named(NamedKey::F1) => {
                self.hud = !self.hud;
                self.update_title();
            }
            Key::Character(c) => match c.as_str() {
                "1" | "2" | "3" | "4" => {
                    self.tool = Tool::Pen;
                    self.color_index = c.parse::<usize>().unwrap() - 1;
                }
                "h" => self.tool = Tool::Highlighter,
                "e" => self.tool = Tool::Eraser,
                "c" => {
                    self.ink.remove(&self.page);
                    self.composites.remove(&self.page);
                }
                "z" if ctrl => {
                    if self.ink.get_mut(&self.page).and_then(|s| s.pop()).is_some() {
                        self.composites.remove(&self.page);
                    }
                }
                "q" if ctrl => event_loop.exit(),
                "t" => self.touch_draws = !self.touch_draws,
                " " | "n" => self.go_to(self.page + 1),
                "p" => self.go_to(self.page.saturating_sub(1)),
                "+" => self.fade_ms = (self.fade_ms + 50).min(3000),
                "-" => self.fade_ms = self.fade_ms.saturating_sub(50),
                _ => return,
            },
            _ => return,
        }
        self.request_redraw();
    }

    fn toggle_fullscreen(&self) {
        if let Some(w) = self.window() {
            let next = if w.fullscreen().is_some() { None } else { Some(Fullscreen::Borderless(None)) };
            w.set_fullscreen(next);
        }
    }

    // ────────────────────────────── Rendern ────────────────────────────────

    fn redraw(&mut self) {
        if self.gpu.is_none() {
            return;
        }
        let now = Instant::now();
        if let Some(last) = self.stats.last_frame {
            if self.stats.frame_times.len() >= 240 {
                self.stats.frame_times.pop_front();
            }
            self.stats.frame_times.push_back(now.duration_since(last).as_secs_f32() * 1000.0);
        }
        self.stats.last_frame = Some(now);

        let fade = self.fade_progress(now);
        let (page, from) = (self.page, if fade.is_some() { self.from_page } else { None });
        self.ensure_composite(page);
        if let Some(f) = from {
            self.ensure_composite(f);
        }

        let rect = |p: usize| {
            let (x, y, w, h) = self.dest_rect(p);
            [x as f32, y as f32, w as f32, h as f32]
        };
        let mut quads = Vec::new();
        if let Some(c) = from.and_then(|f| self.composites.get(&f)) {
            quads.push(Quad { composite: c, rect: rect(from.unwrap()), alpha: 1.0 });
        }
        if let Some(c) = self.composites.get(&page)
            && (!self.fade_pending || from.is_none())
        {
            quads.push(Quad { composite: c, rect: rect(page), alpha: fade.unwrap_or(1.0) });
        }

        let mut live = Vec::new();
        let mut highlighter = false;
        if let Some(Active { stroke: Some(s), .. }) = &self.active {
            s.tessellate(&mut live);
            highlighter = s.highlighter;
        }
        let overlay = self.overlay();
        let transform = self.ink_transform(page);
        let gpu = self.gpu.as_mut().unwrap();
        let presented = gpu.render(&quads, Some((&live, highlighter, transform)), &overlay);

        if presented {
            self.stats.frames += 1;
            if let Some(t) = self.stats.pending_input.take() {
                self.stats.latencies.push(t.elapsed().as_secs_f32() * 1000.0);
            }
        }
        self.report(now);

        if !self.args.on_demand || self.from_page.is_some() {
            self.request_redraw();
        }
    }

    /// Radierer-Ring und Frame-Zeit-Diagramm in Fensterpixeln.
    fn overlay(&self) -> Vec<Vertex> {
        let mut out = Vec::new();
        if let (Tool::Eraser, Some((x, y))) = (self.tool, self.pointer_pos) {
            let (_, _, _, dh) = self.dest_rect(self.page);
            ink::ring(&mut out, x, y, ink::ERASER_RADIUS * dh as f32, 1.5, [0.5, 0.5, 0.5, 1.0]);
        }
        if self.hud {
            // 240 Balken = letzte 240 Frames; Linien bei 16,7 ms (grün) und 33,3 ms (rot); 3 px pro ms
            let (x0, y0, h) = (12.0, 12.0, 110.0);
            ink::rect(&mut out, x0, y0, x0 + 252.0, y0 + h, [0.0, 0.0, 0.0, 0.7]);
            for (i, &ms) in self.stats.frame_times.iter().enumerate() {
                let bh = (ms * 3.0).min(h - 4.0);
                let color = if ms > 20.0 { [1.0, 0.35, 0.3, 1.0] } else { [0.85, 0.85, 0.85, 1.0] };
                let x = x0 + 6.0 + i as f32;
                ink::rect(&mut out, x, y0 + h - 2.0 - bh, x + 1.0, y0 + h - 2.0, color);
            }
            for (ms, color) in [(16.67, [0.3, 0.9, 0.3, 0.9]), (33.33, [0.95, 0.3, 0.3, 0.9])] {
                let y = y0 + h - 2.0 - ms * 3.0;
                ink::rect(&mut out, x0, y, x0 + 252.0, y + 1.0, color);
            }
            // Druckbalken rechts daneben
            ink::rect(&mut out, x0 + 258.0, y0, x0 + 272.0, y0 + h, [0.0, 0.0, 0.0, 0.7]);
            let ph = self.stats.pressure.clamp(0.0, 1.0) * (h - 4.0);
            ink::rect(&mut out, x0 + 261.0, y0 + h - 2.0 - ph, x0 + 269.0, y0 + h - 2.0, [0.3, 0.6, 1.0, 1.0]);
        }
        out
    }

    /// Einmal pro Sekunde Messwerte in Titelleiste und Konsole.
    fn report(&mut self, now: Instant) {
        let s = &mut self.stats;
        let last = *s.last_report.get_or_insert(now);
        let elapsed = now.duration_since(last).as_secs_f32();
        if elapsed < 1.0 {
            return;
        }
        let recent: Vec<f32> = s.frame_times.iter().rev().take(s.frames as usize).copied().collect();
        let avg = recent.iter().sum::<f32>() / recent.len().max(1) as f32;
        let max = recent.iter().copied().fold(0.0, f32::max);
        let lat_avg = s.latencies.iter().sum::<f32>() / s.latencies.len().max(1) as f32;
        let lat_max = s.latencies.iter().copied().fold(0.0, f32::max);
        s.report = format!(
            "{:.0} fps · Frame Ø {avg:.1} max {max:.1} ms · {} Punkte/s · Eingabe→Present Ø {lat_avg:.1} max {lat_max:.1} ms · {} Druck {:.2}",
            s.frames as f32 / elapsed,
            (s.points as f32 / elapsed).round(),
            if s.pointer.is_empty() { "–" } else { s.pointer },
            s.pressure,
        );
        if self.hud {
            println!("{}", s.report);
        }
        s.frames = 0;
        s.points = 0;
        s.latencies.clear();
        s.last_report = Some(now);
        self.update_title();
    }
}

fn pen_pressure(data: &TabletToolData) -> f32 {
    data.force.map(|f| f.normalized(None) as f32).unwrap_or(0.5)
}

impl ApplicationHandler for App {
    fn can_create_surfaces(&mut self, event_loop: &dyn ActiveEventLoop) {
        if self.gpu.is_some() {
            return;
        }
        let mut attrs = WindowAttributes::default()
            .with_title("PDF-Presenter (Rust/wgpu)")
            .with_surface_size(LogicalSize::new(1280.0, 720.0));
        if self.args.fullscreen {
            attrs = attrs.with_fullscreen(Some(Fullscreen::Borderless(None)));
        }
        let window: Arc<dyn Window> =
            Arc::from(event_loop.create_window(attrs).expect("Fenster konnte nicht erstellt werden"));
        window.set_cursor(CursorIcon::Crosshair.into());
        match Gpu::new(&self.instance, window, self.args.low_latency) {
            Ok(gpu) => self.gpu = Some(gpu),
            Err(e) => {
                eprintln!("GPU-Initialisierung fehlgeschlagen: {e}");
                event_loop.exit();
                return;
            }
        }
        if let Some(path) = self.args.pdf.clone() {
            self.open(path);
        } else {
            eprintln!("Kein PDF angegeben. Aufruf: pdfpresenter-rust <datei.pdf>");
        }
        self.update_title();
        self.request_redraw();
    }

    fn proxy_wake_up(&mut self, _event_loop: &dyn ActiveEventLoop) {
        self.handle_replies();
    }

    fn window_event(&mut self, event_loop: &dyn ActiveEventLoop, _id: WindowId, event: WindowEvent) {
        match event {
            WindowEvent::CloseRequested => event_loop.exit(),
            WindowEvent::SurfaceResized(size) => {
                if let Some(gpu) = &mut self.gpu {
                    gpu.resize(size.width, size.height);
                }
                self.request_pages();
                self.request_redraw();
            }
            WindowEvent::RedrawRequested => self.redraw(),
            WindowEvent::ModifiersChanged(m) => self.modifiers = m.state(),
            WindowEvent::KeyboardInput { event, .. } if event.state.is_pressed() => {
                self.handle_key(&event.logical_key, event_loop)
            }
            WindowEvent::PointerButton { state, position, button, .. } => {
                self.handle_pointer_button(state, position.x as f32, position.y as f32, button)
            }
            WindowEvent::PointerMoved { position, source, .. } => {
                self.handle_pointer_moved(position.x as f32, position.y as f32, source)
            }
            WindowEvent::PointerLeft { kind, .. } => {
                if self.active.as_ref().is_some_and(|a| a.kind == kind) {
                    self.commit_active();
                }
                self.pointer_pos = None;
            }
            _ => {}
        }
    }
}

fn main() {
    let args = Args::parse();
    let event_loop = EventLoop::new().expect("Event-Loop konnte nicht erstellt werden");
    let instance = wgpu::Instance::new(wgpu::InstanceDescriptor::new_with_display_handle_from_env(Box::new(
        event_loop.owned_display_handle(),
    )));
    let app = App::new(args, event_loop.create_proxy(), instance);
    event_loop.run_app(app).expect("Event-Loop beendet mit Fehler");
}
