# PDFPresenter

Native Desktop-App zum Abspielen von PDF-Präsentationen mit Folienübergängen und
Freihand-Annotationen – gedacht für die Aufnahme von Lehrvideos (MatheVideos, Aufnahme extern mit OBS).
Anforderungen: [docs/WEITERGABE.md](docs/WEITERGABE.md).

## Entscheidung

Weiterentwickelt wird **Avalonia** ([`prototypes/avalonia`](prototypes/avalonia)). Der
Rust-Prototyp bleibt als Referenz im Repository, bekommt aber keine neuen Funktionen mehr
(z. B. fehlen dort Menüleiste, Speichern und PDF-Export). Eine iPad-Version ist angedacht,
aber zurückgestellt.

## Die zwei Prototypen

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

Menü (nur Avalonia):
- **Datei** → Neues Blatt (Strg+N), Öffnen (Strg+O, PDF oder `.pdfp`), Speichern (Strg+S),
  Als PDF exportieren (Strg+E), Beenden
- **Seite** → Leeres Blatt einfügen (Strg+Umschalt+N, auch „＋ Seite“ in der Stiftleiste),
  Seite löschen, Hintergrund (blanko / kariert / liniert / punktiert), Abstand (4–10 mm),
  Format (16:9, A4 hoch, A4 quer)
- **Ansicht** → Stift-Panel, Kopfbereich, Messanzeige, Vollbild

Papier und Tafel:
- Papierseiten lassen sich zwischen PDF-Folien einfügen (z. B. für Nebenrechnungen) oder als
  reines Tafel-Dokument ohne PDF nutzen (Strg+N). Im Tafel-Dokument hängt die Leertaste auf der
  letzten Seite automatisch ein neues Blatt an.
- Abstände sind echte Millimeter der Seite (Karo 5 mm, Linien 8 mm), das Karo ist mittig
  ausgerichtet. Hintergrund/Format gelten für die aktuelle Papierseite und als Vorgabe für neue.

Geodreieck (Taste G, Knopf „Geodreieck“ in der Stiftleiste, Ansicht → Geodreieck):
- Originalmaße (16 cm Hypotenuse, Skala ±7 cm, Winkelmesser 0–180° mit Gegenskala) in echten
  Millimetern der Seite, passt also zum Karopapier; Größe 50–150 % im Menü.
- **Ein Finger** auf dem Geodreieck verschiebt es, ein **zweiter Finger** (irgendwo) dreht es um
  die Mitte zwischen den Fingern. Pinch-Skalierung nur bei geöffnetem **Schloss** (auf dem
  Dreieck antippen). Beim Drehen erscheint der Winkel; bei 0°/45°/90° rastet es ein (±2°).
- Der **Stift bewegt es nie**; setzt der Stift auf, endet eine Geste. Berührungen, solange der
  Stift in der Nähe ist, gelten als Handballen.
- Maus: ziehen (im Inneren) verschiebt, Umschalt+Rad dreht (5°), Strg+Rad skaliert; an den
  Kanten und am Nullpunkt zeichnet die Maus (Lineal bzw. Winkel antragen).
- **Lineal:** Setzt der Stift an einer Kante an (Hypotenuse oder Schenkel, ≤ 10 px), wird der
  Strich exakt an der Kante geführt – gerade, knapp außen anliegend, Anfang und Ende auf ganze
  Millimeter eingerastet und nicht über die Kante hinaus. Ein Etikett zeigt die Länge
  („4,3 cm“). Abschaltbar unter Ansicht → Geodreieck → Am Lineal entlang zeichnen.
- **Winkel antragen:** Am Nullpunkt (kleiner Kreis in der Mitte der Hypotenuse) ansetzen →
  Strahl vom Nullpunkt, Richtung auf ganze Grad eingerastet (Außenskala, 0° rechts), Länge auf
  ganze mm. Winkelbogen und Etikett zeigen z. B. „37° (143°) · 5,6 cm“ (in Klammern die
  Innenskala). Abschaltbar unter Ansicht → Geodreieck → Winkel antragen.
- Lage, Drehung, Sichtbarkeit, Größe, Sperre, Einrasten, Lineal-Führung und Winkel antragen
  werden gemerkt (auch über einen Neustart); das Geodreieck selbst wird nicht
  gespeichert oder exportiert (die damit gezogenen Linien natürlich schon).

Wischen zum Blättern: mit **einem Finger** schnell waagrecht über die Folie wischen (nach
links = nächste Seite). Nur wenn Finger nicht zeichnen, nicht auf dem Geodreieck, nicht mit zwei
Fingern und nicht, solange der Stift in der Nähe ist (Handballen). Abschaltbar unter Ansicht.

Speichern und Export:
- **Speichern** – Dokument mit PDF: Begleitdatei `<datei>.pdf.annot.json` neben dem PDF
  (Striche, eingefügte/gelöschte Seiten; das PDF bleibt unverändert, beim nächsten Öffnen ist
  alles wieder da). Dokument ohne PDF: eigene Datei `.pdfp`. Ungespeicherte Änderungen zeigt ein
  ● im Fenstertitel; vor dem Schließen oder Öffnen fragt die App nach.
- **Exportieren** erzeugt ein neues PDF in der Seitenreihenfolge der App: PDF-Folien,
  Papierseiten (Raster als Vektorlinien) und alle Striche als Vektorgrafik. Ohne Oberfläche:
  `PdfPresenter.Avalonia folien.pdf --export folien-annotiert.pdf`.

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
so groß, dass die Folienfläche exakt 1920×1080 Pixel hat, zuzüglich Kopfbereich (Rust: 44 px Leiste,
Avalonia: Menü + Leiste, 76 px; jeweils mal Windows-Skalierung). In OBS die Fensteraufnahme oben
um die Höhe des Kopfbereichs zuschneiden (Filter *Zuschneiden/Polster*). Den genauen Wert zeigt die
Messanzeige (F1) bei Avalonia als „Folienfläche … ab y = … px“. Auf einem 1080p-Monitor im
Vollbild die Leiste mit F2 ausblenden; dann bleiben nur Tastenkürzel (1–9, H, E).

Testfolien neu erzeugen: `python3 samples/make_sample.py` (benötigt `reportlab`).
