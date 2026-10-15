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

    /// <summary>Rasterabstand der unendlichen Leinwand in Welteinheiten.</summary>
    public static double CanvasSpacing(Paper paper) => paper.SpacingMm * Paper.PtPerMm / Paper.FrameHeightPt;

    /// <summary>
    /// Raster der unendlichen Leinwand innerhalb von <paramref name="world"/> (Welteinheiten),
    /// als Linien/Punkte in pt relativ zur linken oberen Ecke. Das Raster ist an der Welt
    /// verankert (Linien bei Vielfachen des Abstands), damit es beim Verschieben mitläuft.
    /// </summary>
    public static (List<Line> Lines, List<Point> Dots) CanvasPattern(Paper paper, Rect world)
    {
        var lines = new List<Line>();
        var dots = new List<Point>();
        double s = CanvasSpacing(paper);
        if (s <= 0 || paper.Style == PaperStyle.Blank) return (lines, dots);
        const double k = Paper.FrameHeightPt;
        double w = world.Width * k, h = world.Height * k;
        IEnumerable<double> Steps(double from, double to)
        {
            for (double v = Math.Ceiling(from / s) * s; v <= to + 1e-9; v += s) yield return v;
        }
        switch (paper.Style)
        {
            case PaperStyle.Grid:
                foreach (var x in Steps(world.Left, world.Right))
                    lines.Add(new Line(new Point((x - world.Left) * k, 0), new Point((x - world.Left) * k, h)));
                foreach (var y in Steps(world.Top, world.Bottom))
                    lines.Add(new Line(new Point(0, (y - world.Top) * k), new Point(w, (y - world.Top) * k)));
                break;
            case PaperStyle.Lined:
                foreach (var y in Steps(world.Top, world.Bottom))
                    lines.Add(new Line(new Point(0, (y - world.Top) * k), new Point(w, (y - world.Top) * k)));
                break;
            case PaperStyle.Dotted:
                foreach (var x in Steps(world.Left, world.Right))
                    foreach (var y in Steps(world.Top, world.Bottom))
                        dots.Add(new Point((x - world.Left) * k, (y - world.Top) * k));
                break;
        }
        return (lines, dots);
    }

    /// <summary>
    /// Zeichnet Hintergrund und Raster der unendlichen Leinwand direkt auf den Bildschirm.
    /// Wird das Raster zu eng (&lt; 6 DIP), wird jede 5. Linie gezeigt usw.
    /// </summary>
    public static void DrawCanvas(DrawingContext ctx, Paper paper, CanvasView view, Rect dest, double renderScaling)
    {
        ctx.FillRectangle(Brushes.White, dest);
        if (paper.Style == PaperStyle.Blank) return;
        double unitToDip = dest.Height * view.Zoom; // Welteinheit → DIP
        double s = CanvasSpacing(paper);
        while (s * unitToDip < 6) s *= 5;
        double px = 1 / renderScaling;
        double Snap(double v) => Math.Round(v * renderScaling) / renderScaling + px / 2;

        double aspect = dest.Width / dest.Height;
        var world = new Rect(view.Offset.X, view.Offset.Y, aspect / view.Zoom, 1 / view.Zoom);
        double ToX(double wx) => dest.X + (wx - world.X) * unitToDip;
        double ToY(double wy) => dest.Y + (wy - world.Y) * unitToDip;
        var color = paper.Style == PaperStyle.Lined ? LineColor : GridColor;
        var pen = new Pen(new SolidColorBrush(color), px);

        using (ctx.PushClip(dest))
        {
            if (paper.Style is PaperStyle.Grid)
                for (double x = Math.Ceiling(world.Left / s) * s; x <= world.Right; x += s)
                    ctx.DrawLine(pen, new Point(Snap(ToX(x)), dest.Top), new Point(Snap(ToX(x)), dest.Bottom));
            if (paper.Style is PaperStyle.Grid or PaperStyle.Lined)
                for (double y = Math.Ceiling(world.Top / s) * s; y <= world.Bottom; y += s)
                    ctx.DrawLine(pen, new Point(dest.Left, Snap(ToY(y))), new Point(dest.Right, Snap(ToY(y))));
            if (paper.Style is PaperStyle.Dotted)
            {
                var brush = new SolidColorBrush(DotColor);
                double r = Math.Max(px, DotRadiusPt / Paper.FrameHeightPt * unitToDip);
                for (double x = Math.Ceiling(world.Left / s) * s; x <= world.Right; x += s)
                    for (double y = Math.Ceiling(world.Top / s) * s; y <= world.Bottom; y += s)
                        ctx.DrawEllipse(brush, null, new Point(ToX(x), ToY(y)), r, r);
            }
        }
    }

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
