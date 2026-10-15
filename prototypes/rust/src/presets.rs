//! Stift-Presets: gleiches JSON-Format und gleicher Speicherort wie im Avalonia-Prototyp
//! (Windows: %APPDATA%\PDFPresenter\pen-presets.json, Linux: ~/.config/PDFPresenter/…),
//! beide Prototypen teilen sich also die Presets.

use std::path::PathBuf;

use serde::{Deserialize, Serialize};

pub const MIN_WIDTH: f32 = 1.0;
pub const MAX_WIDTH: f32 = 40.0;
pub const MAX_PRESETS: usize = 12;
pub const HIGHLIGHTER_ALPHA: f32 = 0x60 as f32 / 255.0;

/// Farbe als "#RRGGBB", Stärke in Pixeln bezogen auf eine 1080 px hohe Folie.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct PenPreset {
    pub color: String,
    pub width: f32,
    pub highlighter: bool,
}

impl PenPreset {
    pub fn new(color: &str, width: f32, highlighter: bool) -> Self {
        Self { color: color.to_uppercase(), width, highlighter }
    }

    /// RGB ohne Deckkraft (0..1).
    pub fn rgb(&self) -> [f32; 3] {
        parse_hex(&self.color).unwrap_or([0.0, 0.0, 0.0])
    }

    /// RGBA inkl. Textmarker-Transparenz.
    pub fn rgba(&self) -> [f32; 4] {
        let [r, g, b] = self.rgb();
        [r, g, b, if self.highlighter { HIGHLIGHTER_ALPHA } else { 1.0 }]
    }

    /// Breite in Seiteneinheiten (Anteil der Seitenhöhe).
    pub fn page_width(&self) -> f32 {
        self.width.clamp(MIN_WIDTH, MAX_WIDTH) / 1080.0
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PenSettingsFile {
    pub current: PenPreset,
    pub presets: Vec<PenPreset>,
}

impl Default for PenSettingsFile {
    fn default() -> Self {
        let presets = default_presets();
        Self { current: presets[1].clone(), presets }
    }
}

pub fn default_presets() -> Vec<PenPreset> {
    vec![
        PenPreset::new("#101010", 4.0, false),
        PenPreset::new("#1F5FD6", 4.0, false),
        PenPreset::new("#D62828", 4.0, false),
        PenPreset::new("#1F9D4A", 4.0, false),
        PenPreset::new("#FFE000", 27.0, true),
        PenPreset::new("#6CE0FF", 27.0, true),
    ]
}

/// Feste Palette im Stift-Panel (identisch zum Avalonia-Prototyp).
pub const PALETTE: [&str; 16] = [
    "#101010", "#5A5A5A", "#A0A0A0", "#FFFFFF", "#1F5FD6", "#0B9BD6", "#1F9D4A", "#7CC242", "#D62828", "#F0642D",
    "#F2A900", "#FFE000", "#8E44AD", "#D6338A", "#8B5A2B", "#6CE0FF",
];

pub fn file_path() -> Option<PathBuf> {
    let base = if cfg!(windows) {
        std::env::var_os("APPDATA").map(PathBuf::from)
    } else {
        std::env::var_os("XDG_CONFIG_HOME")
            .map(PathBuf::from)
            .or_else(|| std::env::var_os("HOME").map(|h| PathBuf::from(h).join(".config")))
    };
    base.map(|b| b.join("PDFPresenter").join("pen-presets.json"))
}

pub fn load() -> PenSettingsFile {
    let Some(path) = file_path() else { return PenSettingsFile::default() };
    match std::fs::read_to_string(&path) {
        Ok(text) => serde_json::from_str(&text).unwrap_or_else(|e| {
            eprintln!("Presets in {} unlesbar ({e}), nehme Standardwerte", path.display());
            PenSettingsFile::default()
        }),
        Err(_) => PenSettingsFile::default(),
    }
}

pub fn save(settings: &PenSettingsFile) {
    let Some(path) = file_path() else { return };
    let result = path
        .parent()
        .map_or(Ok(()), std::fs::create_dir_all)
        .and_then(|_| std::fs::write(&path, serde_json::to_string_pretty(settings).unwrap_or_default()));
    if let Err(e) = result {
        eprintln!("Presets konnten nicht gespeichert werden: {e}");
    }
}

pub fn parse_hex(s: &str) -> Option<[f32; 3]> {
    let s = s.trim().trim_start_matches('#');
    if s.len() != 6 {
        return None;
    }
    let v = u32::from_str_radix(s, 16).ok()?;
    Some([((v >> 16) & 0xff) as f32 / 255.0, ((v >> 8) & 0xff) as f32 / 255.0, (v & 0xff) as f32 / 255.0])
}

pub fn to_hex([r, g, b]: [f32; 3]) -> String {
    let c = |x: f32| (x.clamp(0.0, 1.0) * 255.0).round() as u8;
    format!("#{:02X}{:02X}{:02X}", c(r), c(g), c(b))
}

/// HSV (h in Grad, s/v 0..1) → RGB
pub fn hsv_to_rgb(h: f32, s: f32, v: f32) -> [f32; 3] {
    let h = h.rem_euclid(360.0) / 60.0;
    let c = v * s;
    let x = c * (1.0 - (h % 2.0 - 1.0).abs());
    let (r, g, b) = match h as u32 {
        0 => (c, x, 0.0),
        1 => (x, c, 0.0),
        2 => (0.0, c, x),
        3 => (0.0, x, c),
        4 => (x, 0.0, c),
        _ => (c, 0.0, x),
    };
    let m = v - c;
    [r + m, g + m, b + m]
}

/// RGB → HSV; bei Grautönen ist der Farbton unbestimmt (None).
pub fn rgb_to_hsv([r, g, b]: [f32; 3]) -> (Option<f32>, f32, f32) {
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let d = max - min;
    let s = if max > 0.0 { d / max } else { 0.0 };
    if d <= f32::EPSILON {
        return (None, s, max);
    }
    let h = if max == r {
        60.0 * ((g - b) / d).rem_euclid(6.0)
    } else if max == g {
        60.0 * ((b - r) / d + 2.0)
    } else {
        60.0 * ((r - g) / d + 4.0)
    };
    (Some(h), s, max)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hex_roundtrip() {
        assert_eq!(to_hex(parse_hex("#1f5fd6").unwrap()), "#1F5FD6");
    }

    #[test]
    fn hsv_roundtrip() {
        for hex in PALETTE {
            let rgb = parse_hex(hex).unwrap();
            let (h, s, v) = rgb_to_hsv(rgb);
            assert_eq!(to_hex(hsv_to_rgb(h.unwrap_or(0.0), s, v)), hex);
        }
    }

    #[test]
    fn reads_avalonia_format() {
        // Avalonia schreibt dieselben Felder (camelCase); unbekannte Felder werden ignoriert.
        let json = r##"{"current":{"color":"#8B5A2B","width":21,"highlighter":false},
                       "presets":[{"color":"#FFE000","width":27,"highlighter":true,"extra":1}]}"##;
        let f: PenSettingsFile = serde_json::from_str(json).unwrap();
        assert_eq!(f.current.width, 21.0);
        assert!(f.presets[0].highlighter);
    }
}
