//! PDF-Rendering mit PDFium auf einem eigenen Thread.
//!
//! PDFium ist nicht threadsicher und das Rendern einer Full-HD-Seite dauert einige zehn
//! Millisekunden. Deshalb besitzt ein Worker-Thread Bibliothek und Dokument; der UI-Thread
//! schickt Aufträge über einen Kanal und wird per `EventLoopProxy::wake_up` benachrichtigt.

use std::path::{Path, PathBuf};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread;

use pdfium_render::prelude::*;
use winit::event_loop::EventLoopProxy;

pub struct RenderedPage {
    pub index: usize,
    pub width: u32,
    pub height: u32,
    pub rgba: Vec<u8>,
}

pub enum Reply {
    Opened { page_sizes: Vec<(f32, f32)> },
    Page(RenderedPage),
    Error(String),
}

enum Job {
    Render { index: usize, width: u32, height: u32 },
}

pub struct PdfWorker {
    jobs: Sender<Job>,
    replies: Receiver<Reply>,
    /// Zielgröße, die der UI-Thread aktuell braucht; veraltete Aufträge werden übersprungen.
    wanted: Arc<Mutex<Vec<(usize, u32, u32)>>>,
}

impl PdfWorker {
    pub fn spawn(path: PathBuf, proxy: EventLoopProxy) -> Self {
        let (job_tx, job_rx) = mpsc::channel::<Job>();
        let (reply_tx, reply_rx) = mpsc::channel::<Reply>();
        let wanted = Arc::new(Mutex::new(Vec::new()));
        let wanted_worker = wanted.clone();

        thread::Builder::new()
            .name("pdfium".into())
            .spawn(move || {
                let send = |r: Reply| {
                    let _ = reply_tx.send(r);
                    proxy.wake_up();
                };
                let pdfium = match bind_pdfium() {
                    Ok(p) => p,
                    Err(e) => return send(Reply::Error(e)),
                };
                let doc = match pdfium.load_pdf_from_file(&path, None) {
                    Ok(d) => d,
                    Err(e) => return send(Reply::Error(format!("{}: {e}", path.display()))),
                };
                let page_sizes = doc.pages().iter().map(|p| (p.width().value, p.height().value)).collect();
                send(Reply::Opened { page_sizes });

                for job in job_rx {
                    let Job::Render { index, width, height } = job;
                    if !wanted_worker.lock().unwrap().contains(&(index, width, height)) {
                        continue;
                    }
                    let config = PdfRenderConfig::new()
                        .set_target_size(width as i32, height as i32)
                        .set_clear_color(PdfColor::WHITE)
                        .render_form_data(true);
                    let result = doc
                        .pages()
                        .get(index as i32)
                        .and_then(|page| page.render_with_config(&config).map(|b| b.as_rgba_bytes()));
                    match result {
                        Ok(rgba) => send(Reply::Page(RenderedPage { index, width, height, rgba })),
                        Err(e) => send(Reply::Error(format!("Seite {}: {e}", index + 1))),
                    }
                }
            })
            .expect("PDFium-Thread konnte nicht gestartet werden");

        Self { jobs: job_tx, replies: reply_rx, wanted }
    }

    /// Setzt die Liste der gewünschten Seiten (Index, Breite, Höhe) und reiht fehlende ein.
    pub fn request(&self, pages: &[(usize, u32, u32)], already_queued: &mut Vec<(usize, u32, u32)>) {
        *self.wanted.lock().unwrap() = pages.to_vec();
        already_queued.retain(|j| pages.contains(j));
        for &(index, width, height) in pages {
            if !already_queued.contains(&(index, width, height)) {
                already_queued.push((index, width, height));
                let _ = self.jobs.send(Job::Render { index, width, height });
            }
        }
    }

    pub fn try_recv(&self) -> Option<Reply> {
        self.replies.try_recv().ok()
    }
}

/// Sucht die PDFium-Bibliothek neben der EXE, in `./pdfium/`, im Arbeitsverzeichnis
/// und zuletzt systemweit.
fn bind_pdfium() -> Result<Pdfium, String> {
    let mut dirs: Vec<PathBuf> = Vec::new();
    if let Ok(exe) = std::env::current_exe()
        && let Some(dir) = exe.parent()
    {
        dirs.push(dir.to_path_buf());
        dirs.push(dir.join("pdfium"));
    }
    dirs.push(PathBuf::from("pdfium"));
    dirs.push(PathBuf::from("."));
    if let Ok(dir) = std::env::var("PDFIUM_DIR") {
        dirs.insert(0, PathBuf::from(dir));
    }

    for dir in &dirs {
        let lib = Pdfium::pdfium_platform_library_name_at_path(dir);
        if Path::new(&lib).exists()
            && let Ok(bindings) = Pdfium::bind_to_library(&lib)
        {
            return Ok(Pdfium::new(bindings));
        }
    }
    Pdfium::bind_to_system_library().map(Pdfium::new).map_err(|e| {
        format!(
            "PDFium-Bibliothek nicht gefunden ({e}). Bitte scripts/fetch-pdfium.(ps1|sh) ausführen \
             oder PDFIUM_DIR setzen."
        )
    })
}
