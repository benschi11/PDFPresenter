# Plan: Papier, Zeichnen ohne PDF, Geodreieck (Avalonia)

Stand der Abstimmung (Antworten des Auftraggebers):

| Frage | Entscheidung |
|---|---|
| Geodreieck in der Aufnahme sichtbar? | ja, unbedingt |
| Größe | Pinch mit zwei Fingern **und** feste Einstellung; Pinch nur mit Sperre, damit sich die Größe beim Verschieben nicht verstellt |
| Format leerer Seiten | 16:9, A4 hoch, A4 quer; unendliche Leinwand als spätere Phase |
| Hintergrund | nur weiß: blanko, kariert, liniert (punktiert als Zugabe) |
| Reihenfolge | Phase 0 → 1 → 2 → 3 → 4 |

## Phase 0 – Dokumentmodell ✅

Ein Dokument ist eine Liste von Seiten. Jede Seite ist entweder eine **PDF-Seite**
(Verweis auf eine Seite der geöffneten PDF-Datei) oder eine **Papierseite** (Format +
Hintergrund). Striche hängen an der Seite, nicht am Index, damit Einfügen/Löschen nichts
verschiebt. Seitengrößen sind in pt definiert; Papierabstände in mm (1 mm = 72/25,4 pt).
Das Modell ist so angelegt, dass „unendliche Leinwand“ später ein weiterer Seitentyp wird.

## Phase 1 – Papier und Zeichnen ohne PDF ✅

- Datei → Neues Blatt (Strg+N): Dokument ohne PDF.
- Seite → Leere Seite einfügen (nach der aktuellen), Seite löschen, Hintergrund und Format
  der aktuellen Papierseite ändern; die Auswahl gilt auch als Vorgabe für neue Seiten.
- Hintergrund als Vektorlinien: Karo 5 mm, Linien 8 mm, Punkte 5 mm (einstellbar).
- Leertaste auf der letzten Seite eines Dokuments ohne PDF hängt ein neues Blatt an.
- „＋ Seite“ in der kompakten Stiftleiste (Bedienung ohne Tastatur während der Aufnahme).
- Speichern: PDF-Dokumente → Begleitdatei `.annot.json` (Version 2 mit Seitenliste,
  Version 1 bleibt lesbar); Dokumente ohne PDF → eigene Datei `.pdfp`.
- PDF-Export setzt PDF-Seiten und Papierseiten (Raster als Vektorlinien) zusammen.

## Phase 1b – Unendliche Leinwand (später)

Seitentyp mit verschiebbarem/zoombarem Ausschnitt, Striche in Weltkoordinaten, endloses
Raster; Export als Ausschnitt um den Inhalt. Gestenkonflikt mit dem Geodreieck klären
(zwei Finger auf dem Geodreieck = drehen, daneben = Leinwand verschieben).

## Phase 2 – Geodreieck (Grundfunktion) ✅

- Darstellung wie das echte Geodreieck (Lineal mit Skala ab Mitte, Winkelmesser 0–180°,
  Parallelen), halbtransparent, sichtbar in der Aufnahme.
- Ein Finger verschiebt, zwei Finger drehen; Pinch-Skalierung nur bei geöffneter Sperre.
- Maus-Ersatz: ziehen = verschieben, Shift+Rad = drehen, Strg+Rad = skalieren.
- Winkelanzeige beim Drehen, Einrasten bei 0°/45°/90° (±2°, abschaltbar).
- Ein-/Ausblenden: Taste G und Knopf in der Kompaktleiste. Der Stift bewegt es nie.

## Phase 3 – Geodreieck als Werkzeug ✅ (ohne „Winkel antragen“)

- Lineal-Funktion: Stift nahe einer Kante → Strich wird exakt an der Kante geführt.
- Skala in echten Zentimetern der Seite (passt auf Karopapier: 1 cm = 2 Kästchen).
- Optional: Winkel antragen mit Live-Anzeige.

## Phase 4 – Feinschliff

Position/Größe/Sichtbarkeit merken, Geodreieck wird nie gespeichert oder exportiert,
optional Wischen zum Blättern.

## Tests

Gestenmathematik, Einrasten, Kantenprojektion, Seitenmodell und Dateiformat als
Unit-Tests (Testprojekt, läuft in der CI). Darstellung per Screenshot; echte
Mehrfinger-Gesten müssen am Gerät getestet werden.
