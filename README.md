# PDFPresenter

Native Desktop-App zum Abspielen von PDF-Präsentationen mit Folienübergängen und
Freihand-Annotationen – gedacht für die Aufnahme von Lehrvideos (MatheVideos, Aufnahme extern mit OBS).
Anforderungen: [docs/WEITERGABE.md](docs/WEITERGABE.md).

## Stand: zwei Prototypen zum Vergleich

| | Prototyp A | Prototyp B |
|---|---|---|
| Ordner | [`prototypes/avalonia`](prototypes/avalonia) | [`prototypes/rust`](prototypes/rust) |
| Stack | Avalonia 12 / .NET 10 (Skia) | winit 0.31 (Beta) + wgpu 30 |
| PDF | PDFium (eigenes P/Invoke) | PDFium (`pdfium-render`) |

Beide haben denselben Funktionsumfang und dieselbe Bedienung, damit sich Stiftlatenz und
Flüssigkeit direkt vergleichen lassen. Details, Architektur und Testprotokoll:
**[docs/prototypen.md](docs/prototypen.md)**.

## Schnellstart

Fertige Builds für Windows und Linux erzeugt der Workflow *Prototypen* (GitHub → Actions →
letzter Lauf → *Artifacts*). Jedes Paket enthält `sample.pdf`.

Selbst bauen:

```sh
# A – Avalonia (.NET 10 SDK)
dotnet run --project prototypes/avalonia -- samples/sample.pdf

# B – Rust (stable, ≥ 1.88)
cd prototypes/rust
sh scripts/fetch-pdfium.sh            # Windows: powershell -File scripts\fetch-pdfium.ps1
cargo run --release -- ../../samples/sample.pdf
```

## Bedienung (beide Prototypen)

| Taste / Eingabe | Funktion |
|---|---|
| Leertaste, →, ↓, Bild ↓, N | nächste Folie (Crossfade) |
| ←, ↑, Bild ↑, Rücktaste, P | vorige Folie |
| Pos1 / Ende | erste / letzte Folie |
| Stift / linke Maustaste | zeichnen (Druck → Strichbreite) |
| Radierer-Ende, Stifttaste, rechte Maustaste | Striche löschen |
| Leiste oben | kompakte Stiftleiste für die Aufnahme: Presets, Radierer, ⚙ öffnet das Stift-Panel |
| Tab | Stift-Panel: Farbe, Stärke, Stift/Textmarker, Presets (Esc oder Klick daneben schließt) |
| F2 | kompakte Stiftleiste ein/aus |
| 1 – 9 | Preset 1–9 wählen |
| H / E | erster Textmarker-Preset / Radierer an/aus |
| C · Strg+Z | Seite leeren · letzten Strich zurücknehmen |
| + / − | Überblenddauer ±50 ms |
| T | Finger-Zeichnen an/aus (Standard: aus = Handballenschutz) |
| F1 | Messanzeige an/aus |
| F11 · Esc | Vollbild an/aus · Vollbild verlassen |
| Strg+Q | beenden |

### Aufnahme mit OBS

Die kompakte Stiftleiste liegt **oberhalb** der Folienfläche, nicht darüber. Für eine
pixelgenaue 1080p-Aufnahme den Prototyp mit `--canvas 1920x1080` starten: Das Fenster wird dann
so groß, dass die Folienfläche exakt 1920×1080 Pixel hat, zuzüglich 44 px Leiste (mal
Windows-Skalierung). In OBS die Fensteraufnahme oben um die Leistenhöhe zuschneiden
(Filter *Zuschneiden/Polster*, oben 44 px bei 100 % Skalierung). Den genauen Wert zeigt die
Messanzeige (F1) bei Avalonia als „Folienfläche … ab y = … px“. Auf einem 1080p-Monitor im
Vollbild die Leiste mit F2 ausblenden; dann bleiben nur Tastenkürzel (1–9, H, E).

Testfolien neu erzeugen: `python3 samples/make_sample.py` (benötigt `reportlab`).
