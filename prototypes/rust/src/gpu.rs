//! wgpu-Renderer.
//!
//! Pro sichtbarer Seite gibt es eine *Komposit-Textur* = PDF-Seite + alle fertigen Striche
//! (4× MSAA, aufgelöst). Fertige Striche werden inkrementell hineingezeichnet, nur Radieren
//! baut sie neu auf. Jeder Frame zeichnet dann nur noch: Komposit(e) mit Deckkraft
//! (Crossfade), den aktiven Strich und die Messanzeige.
//!
//! Textmarker-Striche nutzen den Stencil-Puffer, damit sich überlappende Dreiecke desselben
//! Strichs nicht doppelt abdunkeln.

use std::sync::Arc;

use wgpu::util::DeviceExt;
use winit::window::Window;

use crate::ink::{Stroke, Vertex};

pub const MSAA: u32 = 4;
const STENCIL_FORMAT: wgpu::TextureFormat = wgpu::TextureFormat::Stencil8;
const UNIFORM_SLOT: u64 = 256;
const UNIFORM_SLOTS: u64 = 64;

#[repr(C)]
#[derive(Clone, Copy, bytemuck::Pod, bytemuck::Zeroable)]
struct Uniforms {
    a: [f32; 4],
    b: [f32; 4],
}

/// Abbildung Seiteneinheiten → NDC: (scale_x, scale_y, offset_x, offset_y).
pub type InkTransform = [f32; 4];

pub struct PageTexture {
    pub width: u32,
    pub height: u32,
    bind_group: wgpu::BindGroup,
}

pub struct Composite {
    pub width: u32,
    pub height: u32,
    msaa_view: wgpu::TextureView,
    stencil_view: wgpu::TextureView,
    resolve_view: wgpu::TextureView,
    bind_group: wgpu::BindGroup,
}

pub struct Quad<'a> {
    pub composite: &'a Composite,
    /// Zielrechteck in physischen Pixeln (x, y, w, h)
    pub rect: [f32; 4],
    pub alpha: f32,
}

pub struct Gpu {
    pub window: Arc<dyn Window>,
    surface: wgpu::Surface<'static>,
    device: wgpu::Device,
    queue: wgpu::Queue,
    config: wgpu::SurfaceConfiguration,
    format: wgpu::TextureFormat,
    pub present_mode: wgpu::PresentMode,
    quad_pipeline: wgpu::RenderPipeline,
    ink_pipeline: wgpu::RenderPipeline,
    highlighter_pipeline: wgpu::RenderPipeline,
    uniform_layout: wgpu::BindGroupLayout,
    texture_layout: wgpu::BindGroupLayout,
    sampler: wgpu::Sampler,
    uniform_buffer: wgpu::Buffer,
    uniform_bind_group: wgpu::BindGroup,
    vertex_buffer: wgpu::Buffer,
    msaa_view: wgpu::TextureView,
    stencil_view: wgpu::TextureView,
}

impl Gpu {
    pub fn new(instance: &wgpu::Instance, window: Arc<dyn Window>, low_latency: bool) -> Result<Self, String> {
        let surface = instance.create_surface(window.clone()).map_err(|e| e.to_string())?;
        let adapter = pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions {
            power_preference: wgpu::PowerPreference::HighPerformance,
            compatible_surface: Some(&surface),
            ..Default::default()
        }))
        .map_err(|e| e.to_string())?;
        let (device, queue) = pollster::block_on(
            adapter.request_device(&wgpu::DeviceDescriptor { label: Some("presenter"), ..Default::default() }),
        )
        .map_err(|e| e.to_string())?;

        let size = window.surface_size();
        let caps = surface.get_capabilities(&adapter);
        let mut config = surface
            .get_default_config(&adapter, size.width.max(1), size.height.max(1))
            .ok_or("Oberfläche wird vom Grafikadapter nicht unterstützt")?;
        // Bewusst *kein* sRGB-Format: PDF-Pixel sollen 1:1 ankommen und der Crossfade soll
        // wie in Video-Software im Gamma-Raum überblenden.
        let format = caps.formats.iter().copied().find(|f| !f.is_srgb()).unwrap_or(config.format.remove_srgb_suffix());
        if caps.formats.contains(&format) {
            config.format = format;
        } else {
            config.view_formats.push(format);
        }

        let present_mode = if low_latency {
            [wgpu::PresentMode::Mailbox, wgpu::PresentMode::Immediate]
                .into_iter()
                .find(|m| caps.present_modes.contains(m))
                .unwrap_or(wgpu::PresentMode::Fifo)
        } else {
            wgpu::PresentMode::Fifo
        };
        config.present_mode = present_mode;
        config.desired_maximum_frame_latency = if low_latency { 1 } else { 2 };
        surface.configure(&device, &config);

        println!(
            "GPU: {} ({:?}) · Format {:?} · Present-Modus {:?} · Frame-Latenz {}",
            adapter.get_info().name,
            adapter.get_info().backend,
            format,
            present_mode,
            config.desired_maximum_frame_latency
        );

        let shader = device.create_shader_module(wgpu::include_wgsl!("shader.wgsl"));

        let uniform_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("uniforms"),
            entries: &[wgpu::BindGroupLayoutEntry {
                binding: 0,
                visibility: wgpu::ShaderStages::VERTEX_FRAGMENT,
                ty: wgpu::BindingType::Buffer {
                    ty: wgpu::BufferBindingType::Uniform,
                    has_dynamic_offset: true,
                    min_binding_size: wgpu::BufferSize::new(std::mem::size_of::<Uniforms>() as u64),
                },
                count: None,
            }],
        });
        let texture_layout = device.create_bind_group_layout(&wgpu::BindGroupLayoutDescriptor {
            label: Some("texture"),
            entries: &[
                wgpu::BindGroupLayoutEntry {
                    binding: 0,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Texture {
                        sample_type: wgpu::TextureSampleType::Float { filterable: true },
                        view_dimension: wgpu::TextureViewDimension::D2,
                        multisampled: false,
                    },
                    count: None,
                },
                wgpu::BindGroupLayoutEntry {
                    binding: 1,
                    visibility: wgpu::ShaderStages::FRAGMENT,
                    ty: wgpu::BindingType::Sampler(wgpu::SamplerBindingType::Filtering),
                    count: None,
                },
            ],
        });

        let quad_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("quad"),
            bind_group_layouts: &[Some(&uniform_layout), Some(&texture_layout)],
            ..Default::default()
        });
        let ink_layout = device.create_pipeline_layout(&wgpu::PipelineLayoutDescriptor {
            label: Some("ink"),
            bind_group_layouts: &[Some(&uniform_layout)],
            ..Default::default()
        });

        let stencil = |compare, pass_op| wgpu::DepthStencilState {
            format: STENCIL_FORMAT,
            depth_write_enabled: Some(false),
            depth_compare: Some(wgpu::CompareFunction::Always),
            stencil: wgpu::StencilState {
                front: wgpu::StencilFaceState {
                    compare,
                    fail_op: wgpu::StencilOperation::Keep,
                    depth_fail_op: wgpu::StencilOperation::Keep,
                    pass_op,
                },
                back: wgpu::StencilFaceState {
                    compare,
                    fail_op: wgpu::StencilOperation::Keep,
                    depth_fail_op: wgpu::StencilOperation::Keep,
                    pass_op,
                },
                read_mask: 0xff,
                write_mask: 0xff,
            },
            bias: Default::default(),
        };
        let target = [Some(wgpu::ColorTargetState {
            format,
            blend: Some(wgpu::BlendState::ALPHA_BLENDING),
            write_mask: wgpu::ColorWrites::ALL,
        })];
        let multisample = wgpu::MultisampleState { count: MSAA, mask: !0, alpha_to_coverage_enabled: false };

        let quad_pipeline = device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("quad"),
            layout: Some(&quad_layout),
            vertex: wgpu::VertexState {
                module: &shader,
                entry_point: Some("vs_quad"),
                compilation_options: Default::default(),
                buffers: &[],
            },
            fragment: Some(wgpu::FragmentState {
                module: &shader,
                entry_point: Some("fs_quad"),
                compilation_options: Default::default(),
                targets: &target,
            }),
            primitive: wgpu::PrimitiveState { topology: wgpu::PrimitiveTopology::TriangleStrip, ..Default::default() },
            depth_stencil: Some(stencil(wgpu::CompareFunction::Always, wgpu::StencilOperation::Keep)),
            multisample,
            multiview_mask: None,
            cache: None,
        });

        let vertex_layout = wgpu::VertexBufferLayout {
            array_stride: std::mem::size_of::<Vertex>() as u64,
            step_mode: wgpu::VertexStepMode::Vertex,
            attributes: &wgpu::vertex_attr_array![0 => Float32x2, 1 => Float32x4],
        };
        let ink_pipeline_with = |label, compare, pass_op| {
            device.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
                label: Some(label),
                layout: Some(&ink_layout),
                vertex: wgpu::VertexState {
                    module: &shader,
                    entry_point: Some("vs_ink"),
                    compilation_options: Default::default(),
                    buffers: &[Some(vertex_layout.clone())],
                },
                fragment: Some(wgpu::FragmentState {
                    module: &shader,
                    entry_point: Some("fs_ink"),
                    compilation_options: Default::default(),
                    targets: &target,
                }),
                primitive: wgpu::PrimitiveState::default(),
                depth_stencil: Some(stencil(compare, pass_op)),
                multisample,
                multiview_mask: None,
                cache: None,
            })
        };
        let ink_pipeline = ink_pipeline_with("ink", wgpu::CompareFunction::Always, wgpu::StencilOperation::Keep);
        // Jeder Pixel wird pro Textmarker-Strich nur einmal gezeichnet (Stencil-Referenz = Strichnummer).
        let highlighter_pipeline =
            ink_pipeline_with("highlighter", wgpu::CompareFunction::NotEqual, wgpu::StencilOperation::Replace);

        let sampler = device.create_sampler(&wgpu::SamplerDescriptor {
            label: Some("linear"),
            mag_filter: wgpu::FilterMode::Linear,
            min_filter: wgpu::FilterMode::Linear,
            ..Default::default()
        });
        let uniform_buffer = device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("uniforms"),
            size: UNIFORM_SLOT * UNIFORM_SLOTS,
            usage: wgpu::BufferUsages::UNIFORM | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let uniform_bind_group = Self::uniform_bind_group(&device, &uniform_layout, &uniform_buffer);
        let vertex_buffer = Self::vertex_buffer(&device, 64 * 1024);
        let (msaa_view, stencil_view) = Self::attachments(&device, format, config.width, config.height);

        Ok(Self {
            window,
            surface,
            device,
            queue,
            config,
            format,
            present_mode,
            quad_pipeline,
            ink_pipeline,
            highlighter_pipeline,
            uniform_layout,
            texture_layout,
            sampler,
            uniform_buffer,
            uniform_bind_group,
            vertex_buffer,
            msaa_view,
            stencil_view,
        })
    }

    fn uniform_bind_group(
        device: &wgpu::Device,
        layout: &wgpu::BindGroupLayout,
        buffer: &wgpu::Buffer,
    ) -> wgpu::BindGroup {
        device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("uniforms"),
            layout,
            entries: &[wgpu::BindGroupEntry {
                binding: 0,
                resource: wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                    buffer,
                    offset: 0,
                    size: wgpu::BufferSize::new(std::mem::size_of::<Uniforms>() as u64),
                }),
            }],
        })
    }

    fn vertex_buffer(device: &wgpu::Device, size: u64) -> wgpu::Buffer {
        device.create_buffer(&wgpu::BufferDescriptor {
            label: Some("vertices"),
            size,
            usage: wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        })
    }

    fn attachments(
        device: &wgpu::Device,
        format: wgpu::TextureFormat,
        width: u32,
        height: u32,
    ) -> (wgpu::TextureView, wgpu::TextureView) {
        let make = |label, format| {
            device
                .create_texture(&wgpu::TextureDescriptor {
                    label: Some(label),
                    size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
                    mip_level_count: 1,
                    sample_count: MSAA,
                    dimension: wgpu::TextureDimension::D2,
                    format,
                    usage: wgpu::TextureUsages::RENDER_ATTACHMENT,
                    view_formats: &[],
                })
                .create_view(&Default::default())
        };
        (make("msaa", format), make("stencil", STENCIL_FORMAT))
    }

    pub fn resize(&mut self, width: u32, height: u32) {
        if width == 0 || height == 0 {
            return;
        }
        self.config.width = width;
        self.config.height = height;
        self.surface.configure(&self.device, &self.config);
        (self.msaa_view, self.stencil_view) = Self::attachments(&self.device, self.format, width, height);
    }

    pub fn size(&self) -> (u32, u32) {
        (self.config.width, self.config.height)
    }

    // ───────────────────────────── Texturen ──────────────────────────────

    pub fn upload_page(&self, width: u32, height: u32, rgba: &[u8]) -> PageTexture {
        let texture = self.device.create_texture_with_data(
            &self.queue,
            &wgpu::TextureDescriptor {
                label: Some("pdf-seite"),
                size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
                mip_level_count: 1,
                sample_count: 1,
                dimension: wgpu::TextureDimension::D2,
                format: wgpu::TextureFormat::Rgba8Unorm,
                usage: wgpu::TextureUsages::TEXTURE_BINDING,
                view_formats: &[],
            },
            wgpu::util::TextureDataOrder::LayerMajor,
            rgba,
        );
        let view = texture.create_view(&Default::default());
        PageTexture { width, height, bind_group: self.texture_bind_group(&view) }
    }

    fn texture_bind_group(&self, view: &wgpu::TextureView) -> wgpu::BindGroup {
        self.device.create_bind_group(&wgpu::BindGroupDescriptor {
            label: Some("texture"),
            layout: &self.texture_layout,
            entries: &[
                wgpu::BindGroupEntry { binding: 0, resource: wgpu::BindingResource::TextureView(view) },
                wgpu::BindGroupEntry { binding: 1, resource: wgpu::BindingResource::Sampler(&self.sampler) },
            ],
        })
    }

    /// Baut die Komposit-Textur einer Seite komplett neu auf: PDF + alle Striche.
    pub fn build_composite(&self, page: &PageTexture, strokes: &[Stroke]) -> Composite {
        let (width, height) = (page.width, page.height);
        let make = |label, format, samples, usage| {
            self.device
                .create_texture(&wgpu::TextureDescriptor {
                    label: Some(label),
                    size: wgpu::Extent3d { width, height, depth_or_array_layers: 1 },
                    mip_level_count: 1,
                    sample_count: samples,
                    dimension: wgpu::TextureDimension::D2,
                    format,
                    usage,
                    view_formats: &[],
                })
                .create_view(&Default::default())
        };
        let msaa_view = make("komposit-msaa", self.format, MSAA, wgpu::TextureUsages::RENDER_ATTACHMENT);
        let stencil_view = make("komposit-stencil", STENCIL_FORMAT, MSAA, wgpu::TextureUsages::RENDER_ATTACHMENT);
        let resolve_view = make(
            "komposit",
            self.format,
            1,
            wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::TEXTURE_BINDING,
        );
        let bind_group = self.texture_bind_group(&resolve_view);
        let composite = Composite { width, height, msaa_view, stencil_view, resolve_view, bind_group };
        self.draw_into_composite(&composite, Some(page), strokes);
        composite
    }

    /// Zeichnet zusätzliche Striche in eine bestehende Komposit-Textur (inkrementell).
    pub fn append_strokes(&self, composite: &Composite, strokes: &[Stroke]) {
        self.draw_into_composite(composite, None, strokes);
    }

    fn draw_into_composite(&self, c: &Composite, page: Option<&PageTexture>, strokes: &[Stroke]) {
        // Seiteneinheiten (x ∈ [0, w/h], y ∈ [0, 1]) → NDC der gesamten Komposit-Textur
        let aspect = c.width as f32 / c.height as f32;
        let uniforms = [
            Uniforms { a: [-1.0, 1.0, 1.0, -1.0], b: [1.0, 0.0, 0.0, 0.0] },
            Uniforms { a: [2.0 / aspect, 2.0, -1.0, 1.0], b: [1.0, 0.0, 0.0, 0.0] },
        ];
        let mut uniform_bytes = vec![0u8; 2 * UNIFORM_SLOT as usize];
        for (i, u) in uniforms.iter().enumerate() {
            let at = i * UNIFORM_SLOT as usize;
            uniform_bytes[at..at + std::mem::size_of::<Uniforms>()].copy_from_slice(bytemuck::bytes_of(u));
        }
        let uniform_buffer = self.device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
            label: Some("komposit-uniforms"),
            contents: &uniform_bytes,
            usage: wgpu::BufferUsages::UNIFORM,
        });
        let uniform_bind_group = Self::uniform_bind_group(&self.device, &self.uniform_layout, &uniform_buffer);

        let mut vertices = Vec::new();
        let mut ranges = Vec::new();
        for s in strokes {
            let start = vertices.len() as u32;
            s.tessellate(&mut vertices);
            ranges.push((start..vertices.len() as u32, s.highlighter));
        }
        let vertex_buffer = (!vertices.is_empty()).then(|| {
            self.device.create_buffer_init(&wgpu::util::BufferInitDescriptor {
                label: Some("komposit-vertices"),
                contents: bytemuck::cast_slice(&vertices),
                usage: wgpu::BufferUsages::VERTEX,
            })
        });

        let mut encoder = self.device.create_command_encoder(&Default::default());
        // Stencil-Referenzen sind 8 Bit: nach 255 Textmarker-Strichen neuen Pass mit leerem Stencil.
        let mut chunks = ranges.chunks(255).peekable();
        let mut first = true;
        loop {
            let chunk = chunks.next().unwrap_or(&[]);
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("komposit"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &c.msaa_view,
                    depth_slice: None,
                    resolve_target: Some(&c.resolve_view),
                    ops: wgpu::Operations {
                        load: if first && page.is_some() {
                            wgpu::LoadOp::Clear(wgpu::Color::WHITE)
                        } else {
                            wgpu::LoadOp::Load
                        },
                        store: wgpu::StoreOp::Store,
                    },
                })],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: &c.stencil_view,
                    depth_ops: None,
                    stencil_ops: Some(wgpu::Operations { load: wgpu::LoadOp::Clear(0), store: wgpu::StoreOp::Discard }),
                }),
                ..Default::default()
            });
            if first && let Some(page) = page {
                pass.set_pipeline(&self.quad_pipeline);
                pass.set_bind_group(0, &uniform_bind_group, &[0]);
                pass.set_bind_group(1, &page.bind_group, &[]);
                pass.draw(0..4, 0..1);
            }
            if let Some(vb) = &vertex_buffer {
                pass.set_bind_group(0, &uniform_bind_group, &[UNIFORM_SLOT as u32]);
                pass.set_vertex_buffer(0, vb.slice(..));
                for (i, (range, highlighter)) in chunk.iter().enumerate() {
                    if *highlighter {
                        pass.set_pipeline(&self.highlighter_pipeline);
                        pass.set_stencil_reference(i as u32 + 1);
                    } else {
                        pass.set_pipeline(&self.ink_pipeline);
                    }
                    pass.draw(range.clone(), 0..1);
                }
            }
            drop(pass);
            first = false;
            if chunks.peek().is_none() {
                break;
            }
        }
        self.queue.submit([encoder.finish()]);
    }

    // ────────────────────────────── Frame ───────────────────────────────

    /// Zeichnet einen Frame. `live` = aktiver Strich in Seiteneinheiten samt Transformation,
    /// `overlay` = Dreiecke in Fensterpixeln (Radierer-Ring, Messanzeige).
    pub fn render(
        &mut self,
        quads: &[Quad],
        live: Option<(&[Vertex], bool, InkTransform)>,
        // Folienfläche (x, y, w, h) in Pixeln: der aktive Strich wird darauf beschnitten
        canvas: [u32; 4],
        overlay: &[Vertex],
    ) -> bool {
        let frame = match self.surface.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(f) | wgpu::CurrentSurfaceTexture::Suboptimal(f) => f,
            wgpu::CurrentSurfaceTexture::Outdated | wgpu::CurrentSurfaceTexture::Lost => {
                self.surface.configure(&self.device, &self.config);
                return false;
            }
            _ => return false,
        };
        let view =
            frame.texture.create_view(&wgpu::TextureViewDescriptor { format: Some(self.format), ..Default::default() });
        let (w, h) = (self.config.width as f32, self.config.height as f32);

        // Uniforms: ein Slot pro Quad, dann aktiver Strich, dann Overlay
        let mut uniforms: Vec<Uniforms> = quads
            .iter()
            .map(|q| {
                let [x, y, qw, qh] = q.rect;
                Uniforms {
                    a: [x / w * 2.0 - 1.0, 1.0 - y / h * 2.0, (x + qw) / w * 2.0 - 1.0, 1.0 - (y + qh) / h * 2.0],
                    b: [q.alpha, 0.0, 0.0, 0.0],
                }
            })
            .collect();
        let live_slot = uniforms.len();
        uniforms.push(Uniforms { a: live.map(|l| l.2).unwrap_or_default(), b: [1.0, 0.0, 0.0, 0.0] });
        let overlay_slot = uniforms.len();
        uniforms.push(Uniforms { a: [2.0 / w, 2.0 / h, -1.0, 1.0], b: [1.0, 0.0, 0.0, 0.0] });
        assert!(uniforms.len() as u64 <= UNIFORM_SLOTS);
        let mut bytes = vec![0u8; uniforms.len() * UNIFORM_SLOT as usize];
        for (i, u) in uniforms.iter().enumerate() {
            let at = i * UNIFORM_SLOT as usize;
            bytes[at..at + std::mem::size_of::<Uniforms>()].copy_from_slice(bytemuck::bytes_of(u));
        }
        self.queue.write_buffer(&self.uniform_buffer, 0, &bytes);

        let live_vertices = live.map(|l| l.0).unwrap_or(&[]);
        let needed = ((live_vertices.len() + overlay.len()) * std::mem::size_of::<Vertex>()) as u64;
        if needed > self.vertex_buffer.size() {
            self.vertex_buffer = Self::vertex_buffer(&self.device, needed.next_power_of_two());
        }
        self.queue.write_buffer(&self.vertex_buffer, 0, bytemuck::cast_slice(live_vertices));
        let overlay_offset = std::mem::size_of_val(live_vertices) as u64;
        self.queue.write_buffer(&self.vertex_buffer, overlay_offset, bytemuck::cast_slice(overlay));

        let mut encoder = self.device.create_command_encoder(&Default::default());
        {
            let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                label: Some("frame"),
                color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                    view: &self.msaa_view,
                    depth_slice: None,
                    resolve_target: Some(&view),
                    ops: wgpu::Operations {
                        load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                        store: wgpu::StoreOp::Discard,
                    },
                })],
                depth_stencil_attachment: Some(wgpu::RenderPassDepthStencilAttachment {
                    view: &self.stencil_view,
                    depth_ops: None,
                    stencil_ops: Some(wgpu::Operations { load: wgpu::LoadOp::Clear(0), store: wgpu::StoreOp::Discard }),
                }),
                ..Default::default()
            });
            let slot = |i: usize| (i as u64 * UNIFORM_SLOT) as u32;

            pass.set_pipeline(&self.quad_pipeline);
            for (i, q) in quads.iter().enumerate() {
                pass.set_bind_group(0, &self.uniform_bind_group, &[slot(i)]);
                pass.set_bind_group(1, &q.composite.bind_group, &[]);
                pass.draw(0..4, 0..1);
            }

            if let Some((vertices, highlighter, _)) = live
                && !vertices.is_empty()
            {
                pass.set_scissor_rect(canvas[0], canvas[1], canvas[2].max(1), canvas[3].max(1));
                pass.set_pipeline(if highlighter { &self.highlighter_pipeline } else { &self.ink_pipeline });
                pass.set_stencil_reference(1);
                pass.set_bind_group(0, &self.uniform_bind_group, &[slot(live_slot)]);
                pass.set_vertex_buffer(0, self.vertex_buffer.slice(..overlay_offset));
                pass.draw(0..vertices.len() as u32, 0..1);
            }
            if !overlay.is_empty() {
                pass.set_scissor_rect(0, 0, self.config.width, self.config.height);
                pass.set_pipeline(&self.ink_pipeline);
                pass.set_bind_group(0, &self.uniform_bind_group, &[slot(overlay_slot)]);
                pass.set_vertex_buffer(0, self.vertex_buffer.slice(overlay_offset..needed));
                pass.draw(0..overlay.len() as u32, 0..1);
            }
        }
        self.queue.submit([encoder.finish()]);
        self.window.pre_present_notify();
        self.queue.present(frame);
        true
    }
}
