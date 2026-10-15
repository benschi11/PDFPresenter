using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace PdfPresenter;

/// <summary>
/// Lage des Geodreiecks: Mittelpunkt der Hypotenuse (= Nullpunkt von Lineal und Winkelmesser) in
/// Seiteneinheiten, Drehung in Grad (im Uhrzeigersinn, wie am Bildschirm) und Größenfaktor
/// (1 = Originalgröße in echten Millimetern der Seite).
/// </summary>
public readonly record struct GeoPose(Point Center, double AngleDeg, double Scale)
{
    public const double MinScale = 0.3, MaxScale = 3;

    /// <summary>Winkel der Hypotenuse wie in der Mathematik (gegen den Uhrzeigersinn), −180…180°.</summary>
    public double MathAngle => Normalize(-AngleDeg);

    public static double Normalize(double deg)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        if (deg <= -180) deg += 360;
        return deg;
    }
}

/// <summary>Reine Geometrie und Gestenrechnung (ohne UI, unit-getestet).</summary>
public static class GeoMath
{
    /// <summary>Halbe Hypotenuse = Höhe des Dreiecks in mm (Original: 16 cm Hypotenuse).</summary>
    public const double HalfMm = 80;

    /// <summary>Verschieben mit einem Finger (Positionen in Seiteneinheiten).</summary>
    public static GeoPose Translate(GeoPose pose, Point from, Point to) =>
        pose with { Center = pose.Center + (to - from) };

    /// <summary>
    /// Zwei-Finger-Geste: Das Dreieck folgt den Fingern, als wäre es an beiden festgeklebt –
    /// Verschiebung des Mittelpunkts, Drehung der Verbindungslinie, ggf. Abstandsänderung.
    /// <paramref name="rawAngle"/> ist der ungerundete Winkel, damit Einrasten nicht „klebt“.
    /// </summary>
    public static GeoPose TwoFinger(GeoPose pose, ref double rawAngle, Point a0, Point b0, Point a1, Point b1, bool allowScale)
    {
        var v0 = b0 - a0;
        var v1 = b1 - a1;
        double len0 = Length(v0), len1 = Length(v1);
        if (len0 < 1e-6 || len1 < 1e-6) return pose;

        double dAngle = (Math.Atan2(v1.Y, v1.X) - Math.Atan2(v0.Y, v0.X)) * 180 / Math.PI;
        double factor = allowScale ? len1 / len0 : 1;
        double newScale = Math.Clamp(pose.Scale * factor, GeoPose.MinScale, GeoPose.MaxScale);
        factor = newScale / pose.Scale;

        var pivot0 = new Point((a0.X + b0.X) / 2, (a0.Y + b0.Y) / 2);
        var pivot1 = new Point((a1.X + b1.X) / 2, (a1.Y + b1.Y) / 2);
        var rel = Rotate(pose.Center - pivot0, dAngle) * factor;

        rawAngle += dAngle;
        return new GeoPose(pivot1 + rel, rawAngle, newScale);
    }

    /// <summary>Rastet bei Vielfachen von <paramref name="step"/> ein, wenn näher als <paramref name="tolerance"/>.</summary>
    public static double Snap(double angleDeg, double step = 45, double tolerance = 2)
    {
        double nearest = Math.Round(angleDeg / step) * step;
        return Math.Abs(angleDeg - nearest) <= tolerance ? nearest : angleDeg;
    }

    public static Vector Rotate(Vector v, double deg)
    {
        double r = deg * Math.PI / 180, c = Math.Cos(r), s = Math.Sin(r);
        return new Vector(v.X * c - v.Y * s, v.X * s + v.Y * c);
    }

    private static double Length(Vector v) => Math.Sqrt(v.X * v.X + v.Y * v.Y);

    /// <summary>
    /// Abbildung lokale mm-Koordinaten → Seiteneinheiten. Lokal: Hypotenuse auf y = 0 von −80 bis
    /// +80 mm, Spitze bei (0, −80) – also „über“ der Hypotenuse, y wächst nach unten wie am Bildschirm.
    /// </summary>
    public static Matrix LocalToPage(GeoPose pose, double pageUnitsPerMm) =>
        Matrix.CreateScale(pageUnitsPerMm * pose.Scale, pageUnitsPerMm * pose.Scale)
        * Matrix.CreateRotation(pose.AngleDeg * Math.PI / 180)
        * Matrix.CreateTranslation(pose.Center.X, pose.Center.Y);

    /// <summary>Liegt der lokale Punkt (mm) im Dreieck (mit Rand <paramref name="marginMm"/>)?</summary>
    public static bool Contains(Point local, double marginMm = 0) =>
        local.Y <= marginMm && local.Y >= -HalfMm - marginMm && Math.Abs(local.X) <= HalfMm + local.Y + marginMm * 1.5;
}

/// <summary>Zeichnet das Geodreieck (Vektor, jeden Frame; Geometrie wird zwischengespeichert).</summary>
public static class GeoRenderer
{
    public static readonly Point LockCenterMm = new(0, -64);
    public const double LockRadiusMm = 6;

    private static readonly IBrush Fill = new SolidColorBrush(Color.FromArgb(0x55, 0xC8, 0xE6, 0xF5));
    private static readonly Color Ink = Color.FromArgb(0xE6, 0x16, 0x3A, 0x5C);
    private static readonly IBrush InkBrush = new SolidColorBrush(Ink);
    private static readonly IBrush FaintBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x16, 0x3A, 0x5C));

    private static Geometry? _outline, _ticks, _faint;
    private static readonly Dictionary<(string, double), FormattedText> TextCache = new();

    /// <param name="toScreen">lokale mm → Bildschirm (DIP)</param>
    /// <param name="mmToScreen">Bildschirm-DIP pro mm (für Linienstärken/Schrift)</param>
    public static void Draw(DrawingContext ctx, Matrix toScreen, double mmToScreen, bool scaleLocked)
    {
        BuildGeometry();
        using (ctx.PushTransform(toScreen))
        {
            // Linienstärken in mm, aber nie dünner als ~1 Bildschirmpixel
            double hair = Math.Max(0.25, 1 / mmToScreen);
            ctx.DrawGeometry(Fill, new Pen(InkBrush, Math.Max(0.35, 1.2 / mmToScreen)), _outline!);
            ctx.DrawGeometry(null, new Pen(FaintBrush, hair), _faint!);
            ctx.DrawGeometry(null, new Pen(InkBrush, hair), _ticks!);

            // Lineal-Beschriftung: 0 in der Mitte, 1…7 cm nach beiden Seiten
            for (int cm = -7; cm <= 7; cm++)
                if (Math.Abs(cm) != 5) // dort trifft der Winkelmesser-Bogen die Kante
                    DrawLabel(ctx, Math.Abs(cm).ToString(CultureInfo.InvariantCulture), new Point(cm * 10, -7.2), 3.2, mmToScreen);
            DrawLabel(ctx, "cm", new Point(0, -11.5), 2.4, mmToScreen);

            // Winkelmesser: äußere Skala gegen den Uhrzeigersinn (0 rechts), innere gegenläufig;
            // 10°/170° bleiben unbeschriftet, sonst kollidieren sie mit den cm-Zahlen
            for (int deg = 20; deg <= 160; deg += 10)
            {
                DrawLabel(ctx, deg.ToString(CultureInfo.InvariantCulture), Polar(44, deg), 2.6, mmToScreen);
                DrawLabel(ctx, (180 - deg).ToString(CultureInfo.InvariantCulture), Polar(37.5, deg), 2.1, mmToScreen);
            }

            DrawLock(ctx, scaleLocked, mmToScreen);
        }
    }

    private static Point Polar(double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(r * Math.Cos(a), -r * Math.Sin(a));
    }

    private static void DrawLabel(DrawingContext ctx, string text, Point center, double sizeMm, double mmToScreen)
    {
        // Schrift in Bildschirmgröße erzeugen und für die Ausgabe zurückskalieren → scharfe Glyphen
        double px = Math.Round(sizeMm * mmToScreen * 2) / 2;
        if (px < 4) return;
        if (!TextCache.TryGetValue((text, px), out var ft))
        {
            if (TextCache.Count > 2000) TextCache.Clear();
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Typeface.Default, px, InkBrush);
            TextCache[(text, px)] = ft;
        }
        double k = 1 / mmToScreen;
        using (ctx.PushTransform(Matrix.CreateScale(k, k) * Matrix.CreateTranslation(center.X - ft.Width * k / 2, center.Y - ft.Height * k / 2)))
            ctx.DrawText(ft, default);
    }

    private static void DrawLock(DrawingContext ctx, bool locked, double mmToScreen)
    {
        var c = LockCenterMm;
        var pen = new Pen(InkBrush, Math.Max(0.4, 1.4 / mmToScreen));
        ctx.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), new Pen(FaintBrush, 0.3), c, LockRadiusMm, LockRadiusMm);
        // Körper
        ctx.DrawRectangle(InkBrush, null, new Rect(c.X - 2.6, c.Y - 0.6, 5.2, 4), 0.6, 0.6);
        // Bügel: geschlossen mittig, offen nach rechts verschoben
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            double x0 = locked ? c.X - 1.7 : c.X + 0.2;
            g.BeginFigure(new Point(x0, c.Y - 0.6), false);
            g.LineTo(new Point(x0, c.Y - 2.6));
            g.ArcTo(new Point(x0 + 3.4, c.Y - 2.6), new Size(1.7, 1.7), 0, false, SweepDirection.Clockwise);
            g.LineTo(new Point(x0 + 3.4, c.Y - (locked ? 0.6 : 1.8)));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }

    private static void BuildGeometry()
    {
        if (_outline is not null) return;
        const double h = GeoMath.HalfMm;

        _outline = Polyline(true, new Point(-h, 0), new Point(h, 0), new Point(0, -h));

        var ticks = new StreamGeometry();
        using (var g = ticks.Open())
        {
            void Line(Point a, Point b) { g.BeginFigure(a, false); g.LineTo(b); g.EndFigure(false); }

            // Millimeter-Skala an der Hypotenuse
            for (int mm = -75; mm <= 75; mm++)
            {
                double len = mm % 10 == 0 ? 4.5 : mm % 5 == 0 ? 3 : 1.8;
                Line(new Point(mm, 0), new Point(mm, -len));
            }
            // Winkelmesser: Teilstriche innen am Bogen (r = 52 mm), 0…180°
            const double r = 52;
            for (int deg = 0; deg <= 180; deg++)
            {
                double len = deg % 10 == 0 ? 4 : deg % 5 == 0 ? 2.6 : 1.5;
                Line(Polar(r, deg), Polar(r - len, deg));
            }
            // Bogen und Mittelmarke
            g.BeginFigure(Polar(r, 0), false);
            g.ArcTo(Polar(r, 180), new Size(r, r), 0, false, SweepDirection.CounterClockwise);
            g.EndFigure(false);
            Line(new Point(0, 0), new Point(0, -5));
        }
        _ticks = ticks;

        // Parallelen zur Hypotenuse (alle 10 mm) und 90°-Linie, dezent
        var faint = new StreamGeometry();
        using (var g = faint.Open())
        {
            for (int d = 10; d < h; d += 10)
            {
                double half = h - d;
                g.BeginFigure(new Point(-half, -d), false);
                g.LineTo(new Point(half, -d));
                g.EndFigure(false);
            }
            g.BeginFigure(new Point(0, -6), false);
            g.LineTo(new Point(0, -h + 2));
            g.EndFigure(false);
        }
        _faint = faint;
    }

    private static Geometry Polyline(bool closed, params Point[] points)
    {
        var geo = new StreamGeometry();
        using var g = geo.Open();
        g.BeginFigure(points[0], true);
        foreach (var p in points.Skip(1)) g.LineTo(p);
        g.EndFigure(closed);
        return geo;
    }
}
