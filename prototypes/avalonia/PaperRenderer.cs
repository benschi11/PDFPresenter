using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PdfPresenter;

/// <summary>
/// Geometrie und Darstellung der Papierhintergründe. Dieselben Linien (in pt, Ursprung oben
/// links) nutzt der PDF-Export, damit Bildschirm und Export übereinstimmen.
/// </summary>
public static class PaperRenderer
{
    public static readonly Color GridColor = Color.Parse("#BCD0E4");
    public static readonly Color LineColor = Color.Parse("#A3BCD8");
    public static readonly Color DotColor = Color.Parse("#8FA8C4");
    public const double LineWidthPt = 0.35;
    public const double DotRadiusPt = 0.7;

    public readonly record struct Line(Point A, Point B);

    /// <summary>Linien des Hintergrunds in pt. Das Karo ist mittig ausgerichtet (gleiche Ränder).</summary>
    public static List<Line> Lines(Paper paper)
    {
        var size = paper.SizePt;
        double s = paper.SpacingMm * Paper.PtPerMm;
        var lines = new List<Line>();
        if (s <= 0) return lines;
        switch (paper.Style)
        {
            case PaperStyle.Grid:
                for (double x = Offset(size.Width, s); x <= size.Width; x += s)
                    lines.Add(new Line(new Point(x, 0), new Point(x, size.Height)));
                for (double y = Offset(size.Height, s); y <= size.Height; y += s)
                    lines.Add(new Line(new Point(0, y), new Point(size.Width, y)));
                break;
            case PaperStyle.Lined:
                for (double y = s; y < size.Height - s / 2; y += s)
                    lines.Add(new Line(new Point(0, y), new Point(size.Width, y)));
                break;
        }
        return lines;
    }

    /// <summary>Punkte (Mittelpunkte in pt) für punktiertes Papier.</summary>
    public static List<Point> Dots(Paper paper)
    {
        var dots = new List<Point>();
        if (paper.Style != PaperStyle.Dotted || paper.SpacingMm <= 0) return dots;
        var size = paper.SizePt;
        double s = paper.SpacingMm * Paper.PtPerMm;
        for (double x = Offset(size.Width, s); x <= size.Width; x += s)
            for (double y = Offset(size.Height, s); y <= size.Height; y += s)
                dots.Add(new Point(x, y));
        return dots;
    }

    private static double Offset(double length, double spacing) => (length % spacing) / 2;

    /// <summary>Hintergrund exakt in <paramref name="size"/> Pixeln (wie eine gerenderte PDF-Seite).</summary>
    public static Bitmap Render(Paper paper, PixelSize size)
    {
        var bmp = new RenderTargetBitmap(size, new Vector(96, 96));
        using var ctx = bmp.CreateDrawingContext(true);
        ctx.FillRectangle(Brushes.White, new Rect(0, 0, size.Width, size.Height));
        double scale = size.Height / paper.SizePt.Height; // px pro pt

        // Linien mindestens 1 px breit und auf Pixelmitten ausgerichtet → gestochen scharf
        double width = Math.Max(1, Math.Round(LineWidthPt * scale));
        double Snap(double v) => Math.Round(v * scale - width / 2) + width / 2;
        var color = paper.Style == PaperStyle.Lined ? LineColor : GridColor;
        var pen = new Pen(new SolidColorBrush(color), width);
        foreach (var l in Lines(paper))
            ctx.DrawLine(pen, new Point(Snap(l.A.X), Snap(l.A.Y)), new Point(Snap(l.B.X), Snap(l.B.Y)));

        double r = Math.Max(1, DotRadiusPt * scale);
        var dotBrush = new SolidColorBrush(DotColor);
        foreach (var d in Dots(paper))
            ctx.DrawEllipse(dotBrush, null, new Point(d.X * scale, d.Y * scale), r, r);
        return bmp;
    }
}
