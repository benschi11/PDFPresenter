"""Erzeugt samples/sample.pdf: 6 Folien im 16:9-Format (wie Typst `presentation-16-9`).

Die Folien enthalten bewusst Haarlinien, Kanten bis an den Rand und Farbflächen,
damit man randlose, pixelgenaue Darstellung und die Überblendung beurteilen kann.

Aufruf:  python3 samples/make_sample.py
"""
from pathlib import Path

from reportlab.lib.colors import Color, HexColor, black, white
from reportlab.pdfgen import canvas

W, H = 960, 540  # 16:9, in pt
OUT = Path(__file__).with_name("sample.pdf")


def frame(c, n, title, bg=white, fg=black):
    c.setFillColor(bg)
    c.rect(0, 0, W, H, stroke=0, fill=1)
    # 1-pt-Rahmen exakt an der Seitenkante: muss im Presenter rundum sichtbar sein.
    c.setStrokeColor(HexColor("#d03030"))
    c.setLineWidth(1)
    c.rect(0.5, 0.5, W - 1, H - 1, stroke=1, fill=0)
    c.setFillColor(fg)
    c.setFont("Helvetica-Bold", 40)
    c.drawString(60, H - 90, title)
    c.setFont("Helvetica", 16)
    c.drawRightString(W - 30, 24, f"Folie {n}")


def slide_title(c):
    frame(c, 1, "PDF-Presenter – Testfolien")
    c.setFont("Helvetica", 26)
    lines = [
        "Leertaste / Pfeiltasten: blättern (Crossfade)",
        "Stift: zeichnen mit Druck, Radierer-Ende: löschen",
        "Tab Stift-Panel · 1–9 Presets · H Textmarker · E Radierer",
        "F11 Vollbild · F1 Messwerte · +/− Überblenddauer",
    ]
    for i, t in enumerate(lines):
        c.drawString(60, H - 170 - i * 48, t)


def slide_grid(c):
    frame(c, 2, "Raster & Haarlinien")
    c.setStrokeColor(HexColor("#999999"))
    for lw, y0 in ((0.25, 120), (0.5, 220), (1.0, 320)):
        c.setLineWidth(lw)
        for i in range(0, 41):
            x = 60 + i * 20
            c.line(x, y0, x, y0 + 80)
        c.setFont("Helvetica", 12)
        c.drawString(60, y0 - 16, f"Linienstärke {lw} pt")


def slide_function(c):
    import math

    frame(c, 3, "Funktionsgraph f(x) = sin(x) · x / 3")
    ox, oy, sx, sy = 120, 260, 70, 55
    c.setStrokeColor(black)
    c.setLineWidth(1.2)
    c.line(80, oy, W - 60, oy)
    c.line(ox, 90, ox, H - 120)
    c.setStrokeColor(HexColor("#2050c0"))
    c.setLineWidth(2.5)
    p = c.beginPath()
    for i in range(0, 1101):
        x = i / 100
        px, py = ox + x * sx, oy + math.sin(x) * x / 3 * sy
        (p.moveTo if i == 0 else p.lineTo)(px, py)
    c.drawPath(p, stroke=1, fill=0)


def slide_gradient(c):
    frame(c, 4, "Farbverlauf (Banding-Test)", bg=HexColor("#101820"), fg=white)
    steps = 256
    for i in range(steps):
        t = i / (steps - 1)
        c.setFillColor(Color(t, 0.4 * t, 1 - t))
        c.rect(60 + i * (W - 120) / steps, 120, (W - 120) / steps + 0.6, 260, stroke=0, fill=1)


def slide_dark(c):
    frame(c, 5, "Dunkle Folie", bg=HexColor("#1e1e2e"), fg=HexColor("#f0f0f0"))
    c.setFont("Helvetica", 24)
    c.drawString(60, H - 170, "Kontrast der Stiftfarben auf dunklem Grund prüfen.")


def slide_full_bleed(c):
    frame(c, 6, "")
    # Vollflächige Kacheln bis an jede Kante: hier fällt jeder Rand/Spalt sofort auf.
    cols, rows = 16, 9
    for i in range(cols):
        for j in range(rows):
            c.setFillColor(HexColor("#2a6fdb") if (i + j) % 2 else HexColor("#f2c14e"))
            c.rect(i * W / cols, j * H / rows, W / cols, H / rows, stroke=0, fill=1)
    c.setFillColor(black)
    c.setFont("Helvetica-Bold", 36)
    c.drawCentredString(W / 2, H / 2 - 12, "Randlos bis an die Kante")


def main():
    c = canvas.Canvas(str(OUT), pagesize=(W, H))
    c.setTitle("PDF-Presenter Testfolien")
    for s in (slide_title, slide_grid, slide_function, slide_gradient, slide_dark, slide_full_bleed):
        s(c)
        c.showPage()
    c.save()
    print(f"geschrieben: {OUT}")


if __name__ == "__main__":
    main()
