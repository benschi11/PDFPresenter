using Avalonia;
using Avalonia.Media;
using PdfPresenter;
using Xunit;

namespace PdfPresenter.Tests;

public sealed class CanvasTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("canvas-test-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static void Near(Point expected, Point actual, double tol = 1e-9)
    {
        Assert.Equal(expected.X, actual.X, tol);
        Assert.Equal(expected.Y, actual.Y, tol);
    }

    private static Stroke Line(double x0, double y0, double x1, double y1)
    {
        var s = new Stroke(Colors.Black, 4 / 1080.0, false);
        s.Points.Add(new InkPoint(x0, y0, 0.5f));
        s.Points.Add(new InkPoint(x1, y1, 0.5f));
        return s;
    }

    [Fact]
    public void World_and_frame_conversions_are_inverse()
    {
        var v = new CanvasView(new Vector(3, -2), 2.5);
        var w = new Point(4.2, 1.7);
        Near(w, v.ToWorld(v.ToFrame(w)));
    }

    [Fact]
    public void Pan_moves_content_with_the_finger()
    {
        var v = new CanvasView(default, 2);
        var w = new Point(1, 1);
        var before = v.ToFrame(w);
        var after = v.Pan(new Vector(0.3, -0.1)).ToFrame(w);
        Near(before + new Vector(0.3, -0.1), after);
    }

    [Fact]
    public void Zoom_keeps_the_point_under_the_anchor()
    {
        var v = new CanvasView(new Vector(1, 1), 1);
        var anchor = new Point(0.8, 0.3);
        var world = v.ToWorld(anchor);
        var zoomed = v.ZoomAt(anchor, 3);
        Assert.Equal(3, zoomed.Zoom, 9);
        Near(world, zoomed.ToWorld(anchor));
    }

    [Fact]
    public void Zoom_is_clamped()
    {
        Assert.Equal(CanvasView.MaxZoom, CanvasView.Identity.ZoomAt(default, 1000).Zoom);
        Assert.Equal(CanvasView.MinZoom, CanvasView.Identity.ZoomAt(default, 0.0001).Zoom);
    }

    [Fact]
    public void Fit_shows_the_whole_rectangle_centred()
    {
        var rect = new Rect(10, 20, 4, 1);
        var v = CanvasView.Fit(rect, aspect: 16.0 / 9, margin: 0);
        var tl = v.ToFrame(rect.TopLeft);
        var br = v.ToFrame(rect.BottomRight);
        Assert.True(tl.X >= -1e-9 && tl.Y >= -1e-9 && br.X <= 16.0 / 9 + 1e-9 && br.Y <= 1 + 1e-9);
        Near(new Point(8.0 / 9, 0.5), v.ToFrame(rect.Center));
    }

    [Fact]
    public void Infinite_page_roundtrip_keeps_view_and_world_strokes()
    {
        var deck = Deck.Blank(new Paper(PageFormat.Infinite, PaperStyle.Grid, 5));
        var page = (PaperPage)deck.Pages[0];
        page.View = new CanvasView(new Vector(-3.5, 12.25), 0.4);
        page.Strokes.Add(Line(-10, 30, 25, 31));
        deck.FilePath = Path.Combine(_dir, "leinwand.pdfp");
        DocumentFile.Save(deck);

        var back = (PaperPage)DocumentFile.LoadPdfp(deck.FilePath).Pages[0];
        Assert.True(back.Paper.IsInfinite);
        Assert.Equal(page.View, back.View);
        Assert.Equal(-10, back.Strokes[0].Points[0].X, 9);
    }

    [Fact]
    public async Task Export_sizes_the_canvas_page_to_its_content()
    {
        var deck = Deck.Blank(new Paper(PageFormat.Infinite, PaperStyle.Grid, 5));
        var page = (PaperPage)deck.Pages[0];
        page.Strokes.Add(Line(-2, -1, 3, 1)); // 5 × 2 Welteinheiten
        var area = PdfExporter.CanvasArea(page);
        var target = Path.Combine(_dir, "leinwand.pdf");

        await PdfExporter.ExportAsync(deck, target);
        var pdf = await PdfDocument.OpenAsync(target);

        Assert.Equal(area.Width * Paper.FrameHeightPt, pdf.PageSizes[0].Width, 0);
        Assert.Equal(area.Height * Paper.FrameHeightPt, pdf.PageSizes[0].Height, 0);
        // Inhalt (inkl. Strichbreite) + 1 cm Rand je Seite
        double margin = 10 * Paper.PtPerMm;
        double strokeWidth = page.Strokes[0].WidthAt(1) * Paper.FrameHeightPt;
        Assert.Equal(5 * Paper.FrameHeightPt + strokeWidth + 2 * margin, pdf.PageSizes[0].Width, 0);
        pdf.Dispose();
    }

    [Fact]
    public void Canvas_grid_is_anchored_to_the_world()
    {
        var paper = new Paper(PageFormat.Infinite, PaperStyle.Grid, 5);
        double s = PaperRenderer.CanvasSpacing(paper);
        // Bereich beginnt mitten zwischen zwei Rasterlinien → erste Linie bei halbem Abstand
        var (lines, _) = PaperRenderer.CanvasPattern(paper, new Rect(-s / 2, 0, 3 * s, s));
        var xs = lines.Where(l => l.A.X == l.B.X).Select(l => l.A.X).ToList();
        Assert.Equal(s / 2 * Paper.FrameHeightPt, xs[0], 6);
        Assert.Equal(s * Paper.FrameHeightPt, xs[1] - xs[0], 6);
        Assert.Equal(5 * Paper.PtPerMm, xs[1] - xs[0], 6); // 5 mm echte Länge
    }
}
