# Prototypen A (Avalonia) und B (Rust/wgpu)

Ziel laut [WEITERGABE.md](WEITERGABE.md): beide Wege schlank umsetzen (PDF laden, Seite
anzeigen, mit dem Stift zeichnen, Crossfade), dann **reale Stiftlatenz und Flüssigkeit
vergleichen** und entscheiden.

## Gemeinsamer Funktionsumfang

- PDF-Seite **randlos und pixelgenau**: Die Seite wird von PDFium direkt in der physischen
  Zielauflösung gerendert (keine nachträgliche Skalierung). Passt das Seitenverhältnis bis auf
  1 %, füllt die Seite die Fläche komplett. Sonst wird eingepasst und zentriert, ohne Beschnitt.
- Rendern auf **eigenem Worker-Thread**, Nachbarseiten werden vorab gerendert. Ist die Zielseite
  noch nicht fertig (Sprung mit Ende), wartet der Crossfade, statt zu ruckeln.
- **Crossfade** mit konfigurierbarer Dauer (`--fade <ms>`, +/−), smoothstep-Verlauf, im
  Gamma-Raum (wie Videoschnitt). Annotationen blenden mit.
- **Stift** mit Druck → Breite, **Textmarker** (halbtransparent, ohne Abdunkeln bei
  Selbstüberlappung), **Radierer** (strichweise; auch Radierer-Ende und Stifttaste).
- **Stift-Panel** (Tab): Stift/Textmarker, Palette mit 16 Farben, freie Farbe
  (Sättigung/Helligkeit-Feld + Farbton-Leiste, in A zusätzlich Hex-Eingabe), Stärke 1–40 px
  (bezogen auf 1080p) mit Vorschau in Originalgröße, **Presets** (max. 12, Tasten 1–9) speichern
  und löschen. Ist das Panel offen, gehen Klicks ans Panel; ein Klick daneben schließt es, ohne
  zu zeichnen.
- **Kompakte Stiftleiste** (für die Aufnahme) **oberhalb** der Folienfläche: nur die Presets,
  Radierer, ⚙ für das große Stift-Panel und die Seitenzahl. Sie überdeckt die Folie nie und
  wird in OBS weggeschnitten. F2 blendet sie aus. `--canvas WxH` macht das Fenster so groß, dass
  die Folienfläche exakt WxH Pixel hat; auch beim Umschalten mit F2. Zeichnet man über den
  oberen Folienrand hinaus, wird der Strich an der Folienkante abgeschnitten.
- **Presets** liegen in `pen-presets.json` (Windows `%APPDATA%\PDFPresenter\`, Linux
  `~/.config/PDFPresenter/`). **Beide Prototypen nutzen dieselbe Datei**; die zuletzt gewählte
  Einstellung wird mitgespeichert.
- Annotationen **pro Seite** im Speicher, beim Zurückblättern wieder da. Koordinaten sind in
  Seiteneinheiten gespeichert, also unabhängig von Fenstergröße und Auflösung.
- **Handballenschutz**: Finger zeichnen standardmäßig gar nicht (`T` bzw. `--touch` schaltet um).
  Auch wenn Finger erlaubt sind, wird Touch bis 1 s nach dem letzten Stiftkontakt bzw.
  Stift-Hover ignoriert. Setzt der Stift auf, während ein Finger- oder Handballenstrich läuft,
  wird dieser verworfen.
- **Messanzeige** (F1): Frame-Zeiten, Stiftpunkte/s, Druck, Zeigertyp und die Zeit von der
  Eingabe bis zum Rendern bzw. Present.

Nicht enthalten (bewusst, Prototyp): Speichern der Annotationen auf Platte, Werkzeugleiste,
Einstellungsdialog.

## Architektur

### A – Avalonia ([`prototypes/avalonia`](../prototypes/avalonia))

| Datei | Inhalt |
|---|---|
| `Pdfium.cs` | ~15 P/Invoke-Funktionen auf PDFium, Worker-Thread, rendert BGRA direkt in eine Avalonia-`Bitmap` |
| `PresenterView.cs` | eigenes `Control`: Layout, Crossfade (`PushOpacity`), Zeiger-Events, Messwerte |
| `Ink.cs` | Strichmodell, Zeichnen (Segmente mit runden Kappen bzw. ein Pfad beim Textmarker), Hit-Test |
| `PenPicker.cs` | Stift-Panel aus Avalonia-Steuerelementen (`ColorSpectrum`/`ColorSlider` aus `Avalonia.Controls.ColorPicker`) |
| `PenSettings.cs` | Preset-Modell, Laden/Speichern der JSON-Datei |
| `CompactBar.cs` | kompakte Stiftleiste oberhalb der Folienfläche |

Ebenen: PDF-Bitmap → gecachte Ink-Ebene (`RenderTargetBitmap`, fertige Striche
inkrementell) → aktiver Strich. Gerendert wird bei Bedarf. Während des Crossfades (oder mit
`--continuous`) läuft eine `RequestAnimationFrame`-Schleife. Stiftdaten kommen über
`PointerPoint.Properties` (`Pressure`, `IsEraser`, `IsBarrelButtonPressed`) und
`e.Pointer.Type`. Gebündelte Zwischenpunkte liest `GetIntermediatePoints`.

Hinweis: Die Bindings sind bewusst selbst geschrieben (statt `PDFtoImage`), um
SkiaSharp-Versionskonflikte mit Avalonia zu vermeiden. Die Native-Binaries kommen aus
`bblanchon.PDFium.Win32/.Linux`.

### B – Rust ([`prototypes/rust`](../prototypes/rust))

| Datei | Inhalt |
|---|---|
| `src/pdf.rs` | PDFium-Thread (`pdfium-render`), Aufträge per Kanal, Weckruf über `EventLoopProxy` |
| `src/gpu.rs` | wgpu: Pipelines, Seitentexturen, Komposit-Texturen, Frame-Rendering |
| `src/ink.rs` | Strichmodell, Tessellierung (Trapez pro Segment + Kreis pro Punkt), Hit-Test |
| `src/picker.rs` | kompakte Stiftleiste und Stift-Panel, selbst gezeichnet aus Dreiecken (Farbfeld als Raster mit Eckfarben, Zahlen als Siebensegment-Ziffern) |
| `src/presets.rs` | Preset-Modell, JSON (serde), HSV-Umrechnung, Tests |
| `src/main.rs` | Fenster, Eingabe, Navigation, Messwerte |

Pro sichtbarer Seite gibt es eine **Komposit-Textur** (PDF und fertige Striche, 4× MSAA).
Fertige Striche werden inkrementell hineingezeichnet. Jeder Frame zeichnet nur zwei Quads
(Crossfade), den aktiven Strich und das Overlay. Textmarker nutzen den Stencil-Puffer gegen
Doppel-Abdunkeln. Standardmäßig wird **kontinuierlich mit VSync** (Fifo) gerendert, also mit
konstanter Bildrate für OBS. `--low-latency` nutzt Mailbox/Immediate mit Frame-Latenz 1,
`--on-demand` rendert nur bei Bedarf.

Die Messwerte stehen in der Titelleiste und in der Konsole (1×/s). Im Fenster ist ein
Frame-Zeit-Diagramm mit Linien bei 16,7 ms (grün) und 33,3 ms (rot) zu sehen, daneben ein
Druckbalken. Text-Rendering im Fenster fehlt bewusst, dafür wäre eine weitere Abhängigkeit nötig.

**Warum winit 0.31-beta:** Nur die neue Pointer-API meldet den Stift als `TabletTool` mit
Druck, Neigung, Radierer-Ende und Stifttaste. winit 0.30 liefert ihn unter Windows als
`Touch`, also ohne Unterscheidung zwischen Stift und Finger, und damit ist kein Handballenschutz
möglich. Bei 0.31 kann sich die API bis zum Release noch ändern.

## Erkenntnisse zu den Stift-APIs (aus Code und Doku, noch nicht auf Hardware getestet)

| | Avalonia 12 | winit 0.31-beta |
|---|---|---|
| Windows | `WM_POINTER`: Pen/Touch/Mouse, Druck, Radierer, Stifttaste | `WM_POINTER`: `TabletTool` (Pen/Eraser), Druck, Neigung, Stifttaste |
| Linux Wayland | zu prüfen | `zwp_tablet_v2`: `TabletTool` mit Druck |
| Linux X11 | zu prüfen (XInput2) | **kein Tablet-Support**: Stift kommt als Maus, ohne Druck |
| Zwischenpunkte | `GetIntermediatePoints` | jedes Event einzeln |

Linux-Stifteingabe muss separat getestet werden, am besten unter Wayland.

## Testprotokoll (auf dem Aufnahme-Rechner)

Beide Prototypen mit `sample.pdf` im Vollbild (F11) auf dem Zielmonitor (1920×1080) starten.
Für B zusätzlich einmal mit `--low-latency` testen.

1. **Stiftlatenz, subjektiv:** schnelle Kreise und Schrift. Folgt der Strich der Spitze?
2. **Stiftlatenz, objektiv:** Handy-Zeitlupe (240 fps) auf die Stiftspitze, schnell eine
   gerade Linie ziehen. Den Abstand zwischen Spitze und Strichende in Frames zählen
   (1 Frame ≈ 4,2 ms) und über 5 Versuche mitteln.
3. **Crossfade:** auf Folie 4 (Farbverlauf) und 6 (Kacheln) mehrfach hin und her blättern,
   auch mit 1000 ms (+). Auf Ruckler achten. Rote Balken im Diagramm (B) bzw. Max-Frame im HUD
   (A, nur während der Überblendung gemessen) notieren.
4. **OBS:** Fenster- bzw. Bildschirmaufnahme mit 60 fps. In *Statistiken* „Frames verpasst
   durch Rendering-Verzögerung“ ablesen und die Aufnahme auf Doppelbilder prüfen.
5. **Druck:** langsam mit steigendem Druck zeichnen. Ändert sich die Breite stufenlos?
   Druckbalken bzw. HUD-Wert beobachten.
6. **Handballenschutz:** mit aufgelegter Hand schreiben (Touch aus und an, `T`).
7. **Radierer-Ende und Stifttaste** ausprobieren.
8. **Last:** CPU/GPU im Task-Manager während Zeichnen und Crossfade.
9. **Linux:** dasselbe unter Wayland und, falls relevant, unter X11.

| Kriterium | A Avalonia | B Rust (Fifo) | B Rust (`--low-latency`) |
|---|---|---|---|
| Latenz Zeitlupe (Frames @240) | | | |
| subjektiv (1–5) | | | |
| Crossfade ruckelfrei? max. Frame-Zeit | | | |
| OBS: verpasste Frames | | | |
| Druck stufenlos? | | | |
| Handballenschutz ok? | | | |
| CPU/GPU-Last | | | |
| Linux Wayland / X11 | | | |

## Bisher geprüft (Linux, Xvfb, Software-Rendering)

Beide Prototypen bauen fehlerfrei (A: `dotnet build`, Cross-Publish für win-x64; B: `cargo
clippy -D warnings`). Per simulierter Maus und Tastatur wurden geprüft: randlose Darstellung
(der 1-pt-Rahmen der Testfolie ist an allen vier Kanten sichtbar), Zeichnen, Farben,
Textmarker, Crossfade mit Annotationen, Zurückblättern mit erhaltenen Strichen, Radierer, Sprung
auf eine nicht vorgerenderte Seite. **Nicht geprüft** sind echte Stifteingabe, Druck und
Handballenschutz sowie Messwerte auf echter GPU. Die Zahlen unter Xvfb/llvmpipe sagen über die
Zielhardware nichts aus.
