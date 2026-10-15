// a/b sind je nach Pipeline unterschiedlich belegt (siehe gpu.rs):
//   Quad: a = Zielrechteck in NDC (x0, y0, x1, y1), b.x = Deckkraft
//   Ink:  a = (scale_x, scale_y, offset_x, offset_y) für Seiteneinheiten → NDC, b.x = Deckkraft
struct Uniforms {
    a: vec4<f32>,
    b: vec4<f32>,
};

@group(0) @binding(0) var<uniform> u: Uniforms;
@group(1) @binding(0) var tex: texture_2d<f32>;
@group(1) @binding(1) var samp: sampler;

struct QuadOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_quad(@builtin(vertex_index) i: u32) -> QuadOut {
    // Triangle-Strip: (0,0) (1,0) (0,1) (1,1)
    let uv = vec2<f32>(f32(i & 1u), f32(i >> 1u));
    let x = mix(u.a.x, u.a.z, uv.x);
    let y = mix(u.a.y, u.a.w, uv.y);
    var out: QuadOut;
    out.pos = vec4<f32>(x, y, 0.0, 1.0);
    out.uv = uv;
    return out;
}

@fragment
fn fs_quad(in: QuadOut) -> @location(0) vec4<f32> {
    let c = textureSample(tex, samp, in.uv);
    return vec4<f32>(c.rgb, c.a * u.b.x);
}

struct InkOut {
    @builtin(position) pos: vec4<f32>,
    @location(0) color: vec4<f32>,
};

@vertex
fn vs_ink(@location(0) p: vec2<f32>, @location(1) color: vec4<f32>) -> InkOut {
    var out: InkOut;
    out.pos = vec4<f32>(u.a.z + p.x * u.a.x, u.a.w - p.y * u.a.y, 0.0, 1.0);
    out.color = vec4<f32>(color.rgb, color.a * u.b.x);
    return out;
}

@fragment
fn fs_ink(in: InkOut) -> @location(0) vec4<f32> {
    return in.color;
}
