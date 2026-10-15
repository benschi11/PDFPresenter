using Avalonia;
using Avalonia.Media;

namespace PdfPresenter;

public enum Tool { Draw, Eraser }

/// <summary>
/// Ein Stiftpunkt in Seiteneinheiten: y läuft von 0 (oben) bis 1 (unten), x von 0 bis
/// Seitenverhältnis. Damit sind Annotationen unabhängig von Fenstergröße und Auflösung.
/// </summary>
public readonly record struct InkPoint(double X, double Y, float Pressure);

public sealed class Stroke(Color color, double width, bool highlighter)
{
    public Color Color { get; } = color;
    /// <summary>Grundbreite in Seiteneinheiten (Anteil der Seitenhöhe).</summary>
    public double Width { get; } = width;
    public bool Highlighter { get; } = highlighter;
    public List<InkPoint> Points { get; } = [];

    /// <summary>Druck → Breite. Druck 0.5 (Maus) ergibt genau die Grundbreite.</summary>
    public double WidthAt(float pressure) => Highlighter ? Width : Width * (0.3 + 1.4 * pressure);

    public void Draw(DrawingContext ctx, Matrix pageToScreen)
    {
        if (Points.Count == 0) return;
        double scale = pageToScreen.M22;

        if (Highlighter)
        {
            // Ein einziger Pfad: Überlappungen innerhalb des Strichs dunkeln nicht nach.
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(Points[0].ToScreen(pageToScreen), false);
                for (int i = 1; i < Points.Count; i++) g.LineTo(Points[i].ToScreen(pageToScreen));
                if (Points.Count == 1) g.LineTo(Points[0].ToScreen(pageToScreen) + new Vector(0.01, 0));
                g.EndFigure(false);
            }
            var pen = new Pen(new SolidColorBrush(Color), Width * scale, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            ctx.DrawGeometry(null, pen, geo);
            return;
        }

        var brush = new SolidColorBrush(Color);
        if (Points.Count == 1)
        {
            var p = Points[0].ToScreen(pageToScreen);
            double r = WidthAt(Points[0].Pressure) * scale / 2;
            ctx.DrawEllipse(brush, null, p, r, r);
            return;
        }
        // Variable Breite: jedes Segment eigener Strich mit runden Enden.
        for (int i = 1; i < Points.Count; i++)
        {
            var a = Points[i - 1];
            var b = Points[i];
            double w = WidthAt((a.Pressure + b.Pressure) / 2) * scale;
            ctx.DrawLine(new Pen(brush, w, lineCap: PenLineCap.Round), a.ToScreen(pageToScreen), b.ToScreen(pageToScreen));
        }
    }

    /// <summary>Trifft der Radierer (Kreis um <paramref name="p"/>) diesen Strich?</summary>
    public bool HitTest(Point p, double radius)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            var a = new Point(Points[i].X, Points[i].Y);
            var b = i + 1 < Points.Count ? new Point(Points[i + 1].X, Points[i + 1].Y) : a;
            double r = radius + WidthAt(Points[i].Pressure) / 2;
            if (DistanceToSegment(p, a, b) <= r) return true;
        }
        return false;
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.X * ab.X + ab.Y * ab.Y;
        double t = len2 == 0 ? 0 : Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / len2, 0, 1);
        var d = p - (a + ab * t);
        return Math.Sqrt(d.X * d.X + d.Y * d.Y);
    }
}

public static class InkPointExtensions
{
    public static Point ToScreen(this InkPoint p, Matrix m) => new Point(p.X, p.Y).Transform(m);
}
