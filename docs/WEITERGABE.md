# WEITERGABE.md — PDF-Präsentations- und Annotations-App

## Ziel
Eine native Desktop-App zum Abspielen von PDF-Präsentationen mit Folienübergängen und Freihand-Annotationen, primär gedacht für die Aufnahme von Lehrvideos (MatheVideos). Die App zeigt nur – aufgenommen wird extern mit OBS.

## Plattform & Stack

### Variante A: Avalonia / .NET (pragmatischer Weg)
- **Framework:** Avalonia UI mit C# / .NET (plattformübergreifend)
- **Zielplattformen:** Windows (Priorität 1), Linux (Priorität 2 – von Anfang an mitdenken)
- Vorteile: reifes UI-Ökosystem, Entwickler ist mit .NET vertraut, realistisch fertigstellbar und wartbar.
- PDF-Rendering: PDFium (z. B. via `PDFtoImage` / `Docnet.Core`).

### Variante B: Rust (Performance-Weg)
- **Rendering/UI:** GPU-naher Stack mit `wgpu`, dazu ein UI-Framework wie `egui` oder `Iced`.
- **PDF-Rendering:** `pdfium` oder `mupdf`.
- Vorteile: niedrigste Stiftlatenz (Strich folgt dem Stift), absolut flüssige Crossfades, da Zeichnen direkt über die GPU.
- Nachteile: weniger reifes UI-Ökosystem, höherer Bauaufwand.

### Hinweis zur Entscheidung
- Stifteingabe selbst (Druck, Handballenerkennung) hängt in **beiden** Varianten an den Betriebssystem-APIs – das nimmt keine Sprache ab.
- **Empfehlung:** Von beiden Varianten einen schlanken Prototyp bauen (PDF laden + eine Seite anzeigen + mit Stift darauf zeichnen + ein Crossfade) und die reale Stiftlatenz und Flüssigkeit vergleichen. Danach entscheiden, welcher Weg weiterverfolgt wird.

## Kernfunktionen
1. **PDF laden & rendern**
   - PDF-Seiten als randlose Flächen darstellen, sauber skaliert auf Full-HD (1920×1080).
   - Keine Ränder/Letterboxing – bildschirmfüllend und pixelgenau (konkreter Schmerzpunkt bei Xournal++).

2. **Folienübergänge**
   - Übergang zwischen PDF-Seiten per Tastendruck (Leertaste / Pfeiltasten).
   - Mindestens Crossfade (Überblenden), Dauer konfigurierbar.
   - Flüssig bei konstanter Framerate (wichtig für OBS).

3. **Annotationsebene**
   - Transparente Zeichenebene über der PDF-Seite.
   - Werkzeuge: Stift (mehrere Farben), Textmarker (halbtransparent), Radierer.
   - **Druckempfindlichkeit** und **Handballenerkennung** (Palm Rejection).
   - Anmerkungen **pro Seite gespeichert**, beim Zurückblättern wieder angezeigt.

## Aufnahme
- Keine interne Aufnahme. Aufnahme extern über OBS.
- Daher wichtig: saubere, ruckelfreie Darstellung mit konstanter Framerate.

## Bekannte Knackpunkte / offene Fragen
- **Stift-APIs (beide Varianten):** Druckempfindlichkeit meist out-of-the-box, echte Handballenerkennung kann zickig sein. Zuerst prüfen, was das Framework über Pointer-/Pen-Events an Pressure- und Pointer-Typ-Daten liefert.
- Linux-Stiftunterstützung getrennt testen (libinput-Verhalten weicht ab).

## Kontext
- Folien entstehen aktuell in Typst und werden als PDF exportiert.
- Teil des MatheVideos-Projekts (GitHub-Repo „MatheVideos").
