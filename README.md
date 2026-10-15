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
| 1 – 4 | Stift schwarz / blau / rot / grün |
| H / E | Textmarker / Radierer |
| C · Strg+Z | Seite leeren · letzten Strich zurücknehmen |
| + / − | Überblenddauer ±50 ms |
| T | Finger-Zeichnen an/aus (Standard: aus = Handballenschutz) |
| F1 | Messanzeige an/aus |
| F11 · Esc | Vollbild an/aus · Vollbild verlassen |
| Strg+Q | beenden |

Testfolien neu erzeugen: `python3 samples/make_sample.py` (benötigt `reportlab`).
