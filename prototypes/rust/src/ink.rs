//! Annotationen: Striche in Seiteneinheiten und ihre Zerlegung in Dreiecke für die GPU.
//!
//! Seiteneinheiten: y läuft von 0 (oben) bis 1 (unten), x von 0 bis Seitenverhältnis.
//! Dadurch sind Annotationen unabhängig von Fenstergröße und Auflösung.

#[repr(C)]
#[derive(Clone, Copy, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct Vertex {
    pub pos: [f32; 2],
    pub color: [f32; 4],
}

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
pub enum Tool {
    Pen,
    Highlighter,
    Eraser,
}

#[derive(Clone, Copy, Debug)]
pub struct InkPoint {
    pub x: f32,
    pub y: f32,
    pub pressure: f32,
}

pub const PEN_COLORS: [[f32; 4]; 4] = [
    [0.063, 0.063, 0.063, 1.0], // 1 schwarz
    [0.122, 0.373, 0.839, 1.0], // 2 blau
    [0.839, 0.157, 0.157, 1.0], // 3 rot
    [0.122, 0.616, 0.290, 1.0], // 4 grün
];
pub const PEN_COLOR_NAMES: [&str; 4] = ["schwarz", "blau", "rot", "grün"];
pub const HIGHLIGHTER_COLOR: [f32; 4] = [1.0, 0.878, 0.0, 0.376];
pub const PEN_WIDTH: f32 = 0.0035; // Anteil der Seitenhöhe (≈ 3,8 px bei 1080p)
pub const HIGHLIGHTER_WIDTH: f32 = 0.025;
pub const ERASER_RADIUS: f32 = 0.012;

#[derive(Clone, Debug)]
pub struct Stroke {
    pub color: [f32; 4],
    pub width: f32,
    pub highlighter: bool,
    pub points: Vec<InkPoint>,
}

impl Stroke {
    pub fn new(tool: Tool, color_index: usize) -> Self {
        let highlighter = tool == Tool::Highlighter;
        Self {
            color: if highlighter { HIGHLIGHTER_COLOR } else { PEN_COLORS[color_index] },
            width: if highlighter { HIGHLIGHTER_WIDTH } else { PEN_WIDTH },
            highlighter,
            points: Vec::new(),
        }
    }

    /// Druck → Breite. Druck 0.5 (Maus) ergibt genau die Grundbreite.
    pub fn width_at(&self, pressure: f32) -> f32 {
        if self.highlighter { self.width } else { self.width * (0.3 + 1.4 * pressure) }
    }

    pub fn push(&mut self, p: InkPoint) {
        // exakte Duplikate (z. B. reine Druckänderung ohne Bewegung) ersetzen statt anhängen
        if let Some(last) = self.points.last_mut()
            && last.x == p.x
            && last.y == p.y
        {
            last.pressure = last.pressure.max(p.pressure);
            return;
        }
        self.points.push(p);
    }

    /// Zerlegt den Strich in Dreiecke: ein Kreis pro Punkt (runde Enden/Gelenke) und ein
    /// Trapez pro Segment (Breite interpoliert zwischen den Druckwerten der Endpunkte).
    pub fn tessellate(&self, out: &mut Vec<Vertex>) {
        let c = self.color;
        let v = |x: f32, y: f32| Vertex { pos: [x, y], color: c };
        for (i, p) in self.points.iter().enumerate() {
            let r = self.width_at(p.pressure) / 2.0;
            circle(out, p.x, p.y, r, c);
            if let Some(q) = self.points.get(i + 1) {
                let rq = self.width_at(q.pressure) / 2.0;
                let (dx, dy) = (q.x - p.x, q.y - p.y);
                let len = (dx * dx + dy * dy).sqrt();
                if len <= f32::EPSILON {
                    continue;
                }
                let (nx, ny) = (-dy / len, dx / len);
                let a1 = v(p.x + nx * r, p.y + ny * r);
                let a2 = v(p.x - nx * r, p.y - ny * r);
                let b1 = v(q.x + nx * rq, q.y + ny * rq);
                let b2 = v(q.x - nx * rq, q.y - ny * rq);
                out.extend_from_slice(&[a1, a2, b1, b1, a2, b2]);
            }
        }
    }

    /// Trifft der Radierer (Kreis um `(x, y)`) diesen Strich?
    pub fn hit_test(&self, x: f32, y: f32, radius: f32) -> bool {
        let n = self.points.len();
        (0..n).any(|i| {
            let a = self.points[i];
            let b = self.points[(i + 1).min(n - 1)];
            distance_to_segment(x, y, a.x, a.y, b.x, b.y) <= radius + self.width_at(a.pressure) / 2.0
        })
    }
}

pub fn circle(out: &mut Vec<Vertex>, cx: f32, cy: f32, r: f32, color: [f32; 4]) {
    const N: usize = 14;
    let pt = |k: usize| {
        let a = k as f32 / N as f32 * std::f32::consts::TAU;
        Vertex { pos: [cx + r * a.cos(), cy + r * a.sin()], color }
    };
    let center = Vertex { pos: [cx, cy], color };
    for k in 0..N {
        out.extend_from_slice(&[center, pt(k), pt(k + 1)]);
    }
}

/// Kreisring (für die Radierer-Anzeige).
pub fn ring(out: &mut Vec<Vertex>, cx: f32, cy: f32, r: f32, thickness: f32, color: [f32; 4]) {
    const N: usize = 32;
    let pt = |k: usize, rr: f32| {
        let a = k as f32 / N as f32 * std::f32::consts::TAU;
        Vertex { pos: [cx + rr * a.cos(), cy + rr * a.sin()], color }
    };
    for k in 0..N {
        let (o0, o1, i0, i1) = (pt(k, r), pt(k + 1, r), pt(k, r - thickness), pt(k + 1, r - thickness));
        out.extend_from_slice(&[o0, i0, o1, o1, i0, i1]);
    }
}

pub fn rect(out: &mut Vec<Vertex>, x0: f32, y0: f32, x1: f32, y1: f32, color: [f32; 4]) {
    let v = |x, y| Vertex { pos: [x, y], color };
    out.extend_from_slice(&[v(x0, y0), v(x1, y0), v(x0, y1), v(x0, y1), v(x1, y0), v(x1, y1)]);
}

fn distance_to_segment(px: f32, py: f32, ax: f32, ay: f32, bx: f32, by: f32) -> f32 {
    let (abx, aby) = (bx - ax, by - ay);
    let len2 = abx * abx + aby * aby;
    let t = if len2 == 0.0 { 0.0 } else { (((px - ax) * abx + (py - ay) * aby) / len2).clamp(0.0, 1.0) };
    let (dx, dy) = (px - (ax + abx * t), py - (ay + aby * t));
    (dx * dx + dy * dy).sqrt()
}
