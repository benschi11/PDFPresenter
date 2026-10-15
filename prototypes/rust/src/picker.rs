//! Stift-Panel für den Rust-Prototyp, gezeichnet mit denselben Dreiecken wie die Annotationen
//! (kein UI-Toolkit). Aufbau wie im Avalonia-Prototyp: Vorschau, Stift/Textmarker, Palette,
//! Sättigung/Helligkeit + Farbton, Stärke, Presets mit Speichern/Löschen.

use crate::ink::{self, InkPoint, Stroke, Vertex};
use crate::presets::{self, PenPreset, PenSettingsFile};

const PANEL_X: f32 = 20.0;
const PAD: f32 = 14.0;
const INNER: f32 = 272.0;
const PANEL_W: f32 = INNER + 2.0 * PAD;
const SV: f32 = 160.0;
const SLIDER_W: f32 = 200.0;
const PRESET: f32 = 40.0;
const PRESET_STEP: f32 = 46.0;
const PRESETS_PER_ROW: usize = 6;

const PANEL_BG: [f32; 4] = [0.133, 0.145, 0.169, 0.95];
const BUTTON_BG: [f32; 4] = [0.25, 0.27, 0.30, 1.0];
const ACCENT: [f32; 4] = [0.24, 0.545, 1.0, 1.0];
const WHITE: [f32; 4] = [1.0, 1.0, 1.0, 1.0];
const MUTED: [f32; 4] = [0.6, 0.6, 0.6, 1.0];

#[derive(Clone, Copy, Debug, PartialEq)]
enum Hit {
    ModePen,
    ModeHighlighter,
    Palette(usize),
    SaturationValue,
    Hue,
    Width,
    Preset(usize),
    Add,
    Delete,
    Panel,
}

#[derive(Clone, Copy, Debug, PartialEq)]
enum Drag {
    SaturationValue,
    Hue,
    Width,
}

/// Was ein Klick bewirkt hat.
pub enum Outcome {
    /// Klick lag außerhalb: Panel wurde geschlossen.
    Closed,
    /// Klick wurde vom Panel verarbeitet (ggf. mit geänderter Einstellung).
    Consumed { changed: bool },
}

struct Rect {
    x: f32,
    y: f32,
    w: f32,
    h: f32,
}

impl Rect {
    fn contains(&self, px: f32, py: f32) -> bool {
        px >= self.x && px < self.x + self.w && py >= self.y && py < self.y + self.h
    }
}

/// Positionen aller Elemente; hängt von der Fensterhöhe und der Anzahl Presets ab.
struct Layout {
    panel: Rect,
    preview: Rect,
    pen: Rect,
    highlighter: Rect,
    palette: Vec<Rect>,
    sv: Rect,
    hue: Rect,
    swatch: Rect,
    slider: Rect,
    digits: (f32, f32),
    presets: Vec<Rect>,
    add: Rect,
    delete: Rect,
}

pub struct PenPicker {
    pub open: bool,
    settings: PenSettingsFile,
    hue: f32,
    sat: f32,
    val: f32,
    drag: Option<Drag>,
}

impl PenPicker {
    pub fn new(settings: PenSettingsFile) -> Self {
        let mut p = Self { open: false, settings, hue: 0.0, sat: 0.0, val: 0.0, drag: None };
        p.sync_hsv();
        p
    }

    pub fn current(&self) -> &PenPreset {
        &self.settings.current
    }

    pub fn select_preset(&mut self, index: usize) -> bool {
        let Some(p) = self.settings.presets.get(index).cloned() else { return false };
        self.apply(p, true);
        true
    }

    pub fn select_first_highlighter(&mut self) {
        let p = self.settings.presets.iter().find(|p| p.highlighter).cloned().unwrap_or_else(|| PenPreset {
            highlighter: true,
            width: 27.0,
            ..self.current().clone()
        });
        self.apply(p, true);
    }

    fn selected(&self) -> Option<usize> {
        self.settings.presets.iter().position(|p| *p == self.settings.current)
    }

    fn apply(&mut self, preset: PenPreset, update_hsv: bool) {
        self.settings.current = PenPreset { color: preset.color.to_uppercase(), ..preset };
        if update_hsv {
            self.sync_hsv();
        }
        if self.drag.is_none() {
            presets::save(&self.settings);
        }
    }

    /// HSV-Zustand aus der aktuellen Farbe; bei Grautönen bleibt der Farbton erhalten.
    fn sync_hsv(&mut self) {
        let (h, s, v) = presets::rgb_to_hsv(self.settings.current.rgb());
        if let Some(h) = h {
            self.hue = h;
        }
        self.sat = s;
        self.val = v;
    }

    fn set_hsv_color(&mut self) {
        let color = presets::to_hex(presets::hsv_to_rgb(self.hue, self.sat, self.val));
        self.apply(PenPreset { color, ..self.current().clone() }, false);
    }

    // ───────────────────────────── Layout ──────────────────────────────

    fn layout(&self, window_h: f32) -> Layout {
        let rows = self.settings.presets.len().div_ceil(PRESETS_PER_ROW).max(1);
        let presets_h = rows as f32 * PRESET_STEP - (PRESET_STEP - PRESET);
        let height =
            PAD + 64.0 + 10.0 + 34.0 + 12.0 + 64.0 + 10.0 + SV + 12.0 + 24.0 + 12.0 + presets_h + 10.0 + 34.0 + PAD;
        let x0 = PANEL_X;
        let y0 = ((window_h - height) / 2.0).max(10.0);
        let x = x0 + PAD;
        let mut y = y0 + PAD;

        let preview = Rect { x, y, w: INNER, h: 64.0 };
        y += 64.0 + 10.0;
        let pen = Rect { x, y, w: 132.0, h: 34.0 };
        let highlighter = Rect { x: x + 140.0, y, w: 132.0, h: 34.0 };
        y += 34.0 + 12.0;
        let palette = (0..presets::PALETTE.len())
            .map(|i| Rect { x: x + (i % 8) as f32 * 34.0, y: y + (i / 8) as f32 * 34.0, w: 30.0, h: 30.0 })
            .collect();
        y += 64.0 + 10.0;
        let sv = Rect { x, y, w: SV, h: SV };
        let hue = Rect { x: x + SV + 10.0, y, w: 22.0, h: SV };
        let swatch = Rect { x: x + SV + 44.0, y, w: INNER - SV - 44.0, h: 60.0 };
        y += SV + 12.0;
        let slider = Rect { x, y, w: SLIDER_W, h: 24.0 };
        let digits = (x + SLIDER_W + 22.0, y + 2.0);
        y += 24.0 + 12.0;
        let presets = (0..self.settings.presets.len())
            .map(|i| Rect {
                x: x + (i % PRESETS_PER_ROW) as f32 * PRESET_STEP,
                y: y + (i / PRESETS_PER_ROW) as f32 * PRESET_STEP,
                w: PRESET,
                h: PRESET,
            })
            .collect();
        y += presets_h + 10.0;
        let add = Rect { x, y, w: 40.0, h: 34.0 };
        let delete = Rect { x: x + 48.0, y, w: 40.0, h: 34.0 };

        Layout {
            panel: Rect { x: x0, y: y0, w: PANEL_W, h: height },
            preview,
            pen,
            highlighter,
            palette,
            sv,
            hue,
            swatch,
            slider,
            digits,
            presets,
            add,
            delete,
        }
    }

    fn hit(&self, l: &Layout, x: f32, y: f32) -> Option<Hit> {
        if !l.panel.contains(x, y) {
            return None;
        }
        let hit = if l.pen.contains(x, y) {
            Hit::ModePen
        } else if l.highlighter.contains(x, y) {
            Hit::ModeHighlighter
        } else if let Some(i) = l.palette.iter().position(|r| r.contains(x, y)) {
            Hit::Palette(i)
        } else if l.sv.contains(x, y) {
            Hit::SaturationValue
        } else if l.hue.contains(x, y) {
            Hit::Hue
        } else if grow(&l.slider, 10.0).contains(x, y) {
            Hit::Width
        } else if let Some(i) = l.presets.iter().position(|r| r.contains(x, y)) {
            Hit::Preset(i)
        } else if l.add.contains(x, y) {
            Hit::Add
        } else if l.delete.contains(x, y) {
            Hit::Delete
        } else {
            Hit::Panel
        };
        Some(hit)
    }

    // ───────────────────────────── Eingabe ─────────────────────────────

    pub fn pointer_down(&mut self, x: f32, y: f32, window_h: f32) -> Outcome {
        let l = self.layout(window_h);
        let Some(hit) = self.hit(&l, x, y) else {
            self.open = false;
            return Outcome::Closed;
        };
        let before = self.settings.current.clone();
        match hit {
            Hit::ModePen | Hit::ModeHighlighter => {
                let highlighter = hit == Hit::ModeHighlighter;
                let cur = self.current().clone();
                if cur.highlighter != highlighter {
                    // typische Stärke mitwechseln (wie im Avalonia-Prototyp)
                    let width = if highlighter { cur.width.max(20.0) } else { cur.width.min(8.0) };
                    self.apply(PenPreset { highlighter, width, ..cur }, false);
                }
            }
            Hit::Palette(i) => {
                self.apply(PenPreset { color: presets::PALETTE[i].to_string(), ..self.current().clone() }, true)
            }
            Hit::SaturationValue => self.start_drag(Drag::SaturationValue, &l, x, y),
            Hit::Hue => self.start_drag(Drag::Hue, &l, x, y),
            Hit::Width => self.start_drag(Drag::Width, &l, x, y),
            Hit::Preset(i) => {
                self.select_preset(i);
            }
            Hit::Add => {
                if self.selected().is_none() && self.settings.presets.len() < presets::MAX_PRESETS {
                    self.settings.presets.push(self.settings.current.clone());
                    presets::save(&self.settings);
                }
            }
            Hit::Delete => {
                if let Some(i) = self.selected() {
                    self.settings.presets.remove(i);
                    presets::save(&self.settings);
                }
            }
            Hit::Panel => {}
        }
        Outcome::Consumed { changed: self.settings.current != before }
    }

    fn start_drag(&mut self, drag: Drag, l: &Layout, x: f32, y: f32) {
        self.drag = Some(drag);
        self.drag_to(l, x, y);
    }

    /// Ziehen in Farbfeld, Farbton-Leiste oder Stärke-Regler. Liefert true bei Änderung.
    pub fn pointer_move(&mut self, x: f32, y: f32, window_h: f32) -> bool {
        if self.drag.is_none() {
            return false;
        }
        let l = self.layout(window_h);
        let before = self.settings.current.clone();
        self.drag_to(&l, x, y);
        self.settings.current != before
    }

    fn drag_to(&mut self, l: &Layout, x: f32, y: f32) {
        match self.drag {
            Some(Drag::SaturationValue) => {
                self.sat = ((x - l.sv.x) / l.sv.w).clamp(0.0, 1.0);
                self.val = 1.0 - ((y - l.sv.y) / l.sv.h).clamp(0.0, 1.0);
                self.set_hsv_color();
            }
            Some(Drag::Hue) => {
                self.hue = ((y - l.hue.y) / l.hue.h).clamp(0.0, 1.0) * 360.0;
                self.set_hsv_color();
            }
            Some(Drag::Width) => {
                let t = ((x - l.slider.x) / l.slider.w).clamp(0.0, 1.0);
                let width = (presets::MIN_WIDTH + t * (presets::MAX_WIDTH - presets::MIN_WIDTH)).round();
                self.apply(PenPreset { width, ..self.current().clone() }, false);
            }
            None => {}
        }
    }

    pub fn pointer_up(&mut self) {
        if self.drag.take().is_some() {
            presets::save(&self.settings);
        }
    }

    // ───────────────────────────── Zeichnen ────────────────────────────

    pub fn draw(&self, out: &mut Vec<Vertex>, window_h: f32) {
        let l = self.layout(window_h);
        let cur = self.current();
        let r = |out: &mut Vec<Vertex>, b: &Rect, c| ink::rect(out, b.x, b.y, b.x + b.w, b.y + b.h, c);

        r(out, &l.panel, PANEL_BG);

        // Vorschau: Beispielstrich in Originalgröße (bezogen auf 1080p) mit Druckverlauf
        r(out, &l.preview, WHITE);
        // Pixel-Koordinaten: Stärke direkt in px
        let mut stroke = Stroke { color: opaque_on_white(cur), width: cur.width, highlighter: false, points: vec![] };
        for i in 0..=60 {
            let t = i as f32 / 60.0;
            stroke.push(InkPoint {
                x: l.preview.x + 16.0 + t * (l.preview.w - 32.0),
                y: l.preview.y + l.preview.h / 2.0 + (t * std::f32::consts::TAU).sin() * l.preview.h * 0.25,
                pressure: if cur.highlighter { 0.5 } else { 0.15 + 0.85 * (t * std::f32::consts::PI).sin() },
            });
        }
        stroke.tessellate(out);

        // Modus-Schalter mit Symbolen
        for (b, active, highlighter) in [(&l.pen, !cur.highlighter, false), (&l.highlighter, cur.highlighter, true)] {
            r(out, b, if active { ACCENT } else { BUTTON_BG });
            let (cx, cy) = (b.x + b.w / 2.0, b.y + b.h / 2.0);
            if highlighter {
                ink::rect(out, cx - 26.0, cy - 6.0, cx + 26.0, cy + 6.0, [1.0, 0.88, 0.0, 0.8]);
            } else {
                let mut s = Stroke { color: WHITE, width: 3.0, highlighter: false, points: vec![] };
                for i in 0..=20 {
                    let t = i as f32 / 20.0;
                    s.push(InkPoint {
                        x: cx - 26.0 + 52.0 * t,
                        y: cy + (t * std::f32::consts::TAU).sin() * 6.0,
                        pressure: 0.5,
                    });
                }
                s.tessellate(out);
            }
        }

        // Palette
        for (i, b) in l.palette.iter().enumerate() {
            let rgb = presets::parse_hex(presets::PALETTE[i]).unwrap();
            outline(out, b, 1.0, [1.0, 1.0, 1.0, 0.35]);
            r(out, b, [rgb[0], rgb[1], rgb[2], 1.0]);
            if presets::PALETTE[i] == cur.color {
                outline(out, &grow(b, 3.0), 2.0, ACCENT);
            }
        }

        // Sättigung (x) / Helligkeit (y) als Raster mit Eckfarben → glatter Verlauf
        const N: usize = 16;
        for i in 0..N {
            for j in 0..N {
                let c = |a: usize, b: usize| {
                    let s = a as f32 / N as f32;
                    let v = 1.0 - b as f32 / N as f32;
                    let [r, g, bl] = presets::hsv_to_rgb(self.hue, s, v);
                    Vertex { pos: [l.sv.x + s * l.sv.w, l.sv.y + (1.0 - v) * l.sv.h], color: [r, g, bl, 1.0] }
                };
                let (a, b, cc, d) = (c(i, j), c(i + 1, j), c(i, j + 1), c(i + 1, j + 1));
                out.extend_from_slice(&[a, b, cc, cc, b, d]);
            }
        }
        let (mx, my) = (l.sv.x + self.sat * l.sv.w, l.sv.y + (1.0 - self.val) * l.sv.h);
        ink::ring(out, mx, my, 7.0, 2.0, if self.val > 0.5 { [0.0, 0.0, 0.0, 1.0] } else { WHITE });

        // Farbton-Leiste (senkrecht)
        for k in 0..12 {
            let h0 = k as f32 * 30.0;
            let h1 = h0 + 30.0;
            let col = |h: f32| {
                let [r, g, b] = presets::hsv_to_rgb(h, 1.0, 1.0);
                [r, g, b, 1.0]
            };
            let y0 = l.hue.y + h0 / 360.0 * l.hue.h;
            let y1 = l.hue.y + h1 / 360.0 * l.hue.h;
            let v = |x: f32, y: f32, c| Vertex { pos: [x, y], color: c };
            let (x0, x1) = (l.hue.x, l.hue.x + l.hue.w);
            out.extend_from_slice(&[
                v(x0, y0, col(h0)),
                v(x1, y0, col(h0)),
                v(x0, y1, col(h1)),
                v(x0, y1, col(h1)),
                v(x1, y0, col(h0)),
                v(x1, y1, col(h1)),
            ]);
        }
        let hy = l.hue.y + self.hue / 360.0 * l.hue.h;
        ink::rect(out, l.hue.x - 3.0, hy - 2.0, l.hue.x + l.hue.w + 3.0, hy + 2.0, WHITE);

        // Aktuelle Farbe groß
        let [cr, cg, cb] = cur.rgb();
        outline(out, &l.swatch, 1.0, [1.0, 1.0, 1.0, 0.35]);
        r(out, &l.swatch, [cr, cg, cb, 1.0]);

        // Stärke-Regler + Ziffern
        let track_y = l.slider.y + l.slider.h / 2.0;
        ink::rect(out, l.slider.x, track_y - 2.0, l.slider.x + l.slider.w, track_y + 2.0, MUTED);
        let t = (cur.width - presets::MIN_WIDTH) / (presets::MAX_WIDTH - presets::MIN_WIDTH);
        ink::rect(out, l.slider.x, track_y - 2.0, l.slider.x + t * l.slider.w, track_y + 2.0, ACCENT);
        ink::circle(out, l.slider.x + t * l.slider.w, track_y, 9.0, ACCENT);
        number(out, l.digits.0, l.digits.1, 20.0, cur.width.round() as u32, WHITE);
        // "px" angedeutet: kleiner Punkt in Strichstärke rechts daneben
        let d = cur.width.clamp(2.0, 20.0);
        ink::circle(out, l.panel.x + l.panel.w - PAD - 10.0, track_y, d / 2.0, [cr, cg, cb, 1.0]);

        // Presets
        let selected = self.selected();
        for (i, b) in l.presets.iter().enumerate() {
            let p = &self.settings.presets[i];
            if Some(i) == selected {
                outline(out, &grow(b, 3.0), 3.0, ACCENT);
            }
            r(out, b, WHITE);
            let (cx, cy) = (b.x + b.w / 2.0, b.y + b.h / 2.0 + 3.0);
            let d = p.width.clamp(3.0, 28.0);
            if p.highlighter {
                ink::rect(out, cx - 14.0, cy - d / 2.0, cx + 14.0, cy + d / 2.0, opaque_on_white(p));
            } else {
                ink::circle(out, cx, cy, d / 2.0, opaque_on_white(p));
            }
            if i < 9 {
                number(out, b.x + 3.0, b.y + 3.0, 9.0, i as u32 + 1, MUTED);
            }
        }

        // Speichern (+) / Löschen (×) – ausgegraut, wenn nicht möglich
        let can_add = selected.is_none() && self.settings.presets.len() < presets::MAX_PRESETS;
        let can_delete = selected.is_some();
        let icon_color = |enabled| if enabled { WHITE } else { [1.0, 1.0, 1.0, 0.3] };
        r(out, &l.add, BUTTON_BG);
        let (ax, ay) = (l.add.x + l.add.w / 2.0, l.add.y + l.add.h / 2.0);
        ink::rect(out, ax - 9.0, ay - 1.5, ax + 9.0, ay + 1.5, icon_color(can_add));
        ink::rect(out, ax - 1.5, ay - 9.0, ax + 1.5, ay + 9.0, icon_color(can_add));
        r(out, &l.delete, BUTTON_BG);
        let (dx, dy) = (l.delete.x + l.delete.w / 2.0, l.delete.y + l.delete.h / 2.0);
        line(out, dx - 7.0, dy - 7.0, dx + 7.0, dy + 7.0, 3.0, icon_color(can_delete));
        line(out, dx - 7.0, dy + 7.0, dx + 7.0, dy - 7.0, 3.0, icon_color(can_delete));
    }
}

/// Textmarker-Farbe auf Weiß vorab gemischt, damit Überlappungen in der Vorschau nicht abdunkeln.
fn opaque_on_white(p: &PenPreset) -> [f32; 4] {
    let [r, g, b, a] = p.rgba();
    [r * a + 1.0 - a, g * a + 1.0 - a, b * a + 1.0 - a, 1.0]
}

fn grow(b: &Rect, d: f32) -> Rect {
    Rect { x: b.x - d, y: b.y - d, w: b.w + 2.0 * d, h: b.h + 2.0 * d }
}

fn outline(out: &mut Vec<Vertex>, b: &Rect, t: f32, c: [f32; 4]) {
    let (x0, y0, x1, y1) = (b.x - t, b.y - t, b.x + b.w + t, b.y + b.h + t);
    ink::rect(out, x0, y0, x1, b.y, c);
    ink::rect(out, x0, b.y + b.h, x1, y1, c);
    ink::rect(out, x0, b.y, b.x, b.y + b.h, c);
    ink::rect(out, b.x + b.w, b.y, x1, b.y + b.h, c);
}

fn line(out: &mut Vec<Vertex>, x0: f32, y0: f32, x1: f32, y1: f32, w: f32, c: [f32; 4]) {
    let (dx, dy) = (x1 - x0, y1 - y0);
    let len = (dx * dx + dy * dy).sqrt().max(f32::EPSILON);
    let (nx, ny) = (-dy / len * w / 2.0, dx / len * w / 2.0);
    let v = |x, y| Vertex { pos: [x, y], color: c };
    out.extend_from_slice(&[
        v(x0 + nx, y0 + ny),
        v(x0 - nx, y0 - ny),
        v(x1 + nx, y1 + ny),
        v(x1 + nx, y1 + ny),
        v(x0 - nx, y0 - ny),
        v(x1 - nx, y1 - ny),
    ]);
}

/// Zahl als Siebensegment-Ziffern (Höhe h), damit das Panel ohne Schriftart auskommt.
fn number(out: &mut Vec<Vertex>, x: f32, y: f32, h: f32, n: u32, c: [f32; 4]) {
    // Segmente a b c d e f g als Bits
    const DIGITS: [u8; 10] = [0x3f, 0x06, 0x5b, 0x4f, 0x66, 0x6d, 0x7d, 0x07, 0x7f, 0x6f];
    let w = h * 0.55;
    let t = (h * 0.13).max(1.5);
    for (k, ch) in n.to_string().chars().enumerate() {
        let bits = DIGITS[ch.to_digit(10).unwrap() as usize];
        let x0 = x + k as f32 * (w + t * 1.8);
        let ym = y + h / 2.0;
        let segs = [
            (x0, y, x0 + w, y + t),                   // a
            (x0 + w - t, y, x0 + w, ym),              // b
            (x0 + w - t, ym, x0 + w, y + h),          // c
            (x0, y + h - t, x0 + w, y + h),           // d
            (x0, ym, x0 + t, y + h),                  // e
            (x0, y, x0 + t, ym),                      // f
            (x0, ym - t / 2.0, x0 + w, ym + t / 2.0), // g
        ];
        for (i, (a, b, cc, d)) in segs.into_iter().enumerate() {
            if bits & (1 << i) != 0 {
                ink::rect(out, a, b, cc, d, c);
            }
        }
    }
}
