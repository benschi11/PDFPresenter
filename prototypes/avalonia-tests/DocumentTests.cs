using Avalonia.Media;
using PdfPresenter;
using Xunit;

namespace PdfPresenter.Tests;

public sealed class DocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pdfp-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static Stroke MakeStroke(string color = "#1F5FD6", double widthPx = 4, bool highlighter = false)
    {
        var s = new Stroke(new PenPreset(color, widthPx, highlighter).ToColor(), widthPx / 1080, highlighter);
        s.Points.Add(new InkPoint(0.1, 0.2, 0.3f));
        s.Points.Add(new InkPoint(0.4, 0.5, 0.9f));
        return s;
    }

    private string CopySample()
    {
        var path = Path.Combine(_dir, "folien.pdf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "sample.pdf"), path);
        return path;
    }

    [Fact]
    public void Pdfp_roundtrip_keeps_pages_paper_and_strokes()
    {
        var deck = Deck.Blank(new Paper(PageFormat.A4Portrait, PaperStyle.Lined, 8));
        deck.Pages.Add(new PaperPage(new Paper(PageFormat.Wide16x9, PaperStyle.Dotted, 4)));
        deck.Pages[1].Strokes.Add(MakeStroke(highlighter: true, widthPx: 27));
        deck.FilePath = Path.Combine(_dir, "tafel.pdfp");

        DocumentFile.Save(deck);
        var back = DocumentFile.LoadPdfp(deck.FilePath);

        Assert.Equal(2, back.Pages.Count);
        Assert.Equal(new Paper(PageFormat.A4Portrait, PaperStyle.Lined, 8), ((PaperPage)back.Pages[0]).Paper);
        Assert.Equal(new Paper(PageFormat.Wide16x9, PaperStyle.Dotted, 4), ((PaperPage)back.Pages[1]).Paper);
        var s = Assert.Single(back.Pages[1].Strokes);
        Assert.True(s.Highlighter);
        Assert.Equal(27, s.Width * 1080, 3);
        Assert.Equal(0.9f, s.Points[1].Pressure, 3);
    }

    [Fact]
    public async Task Sidecar_v2_restores_inserted_paper_page_and_strokes()
    {
        var pdf = CopySample();
        var deck = Deck.FromPdf(await PdfDocument.OpenAsync(pdf), pdf);
        deck.Pages.Insert(1, new PaperPage(Paper.Default));
        deck.Pages[1].Strokes.Add(MakeStroke());
        deck.Pages[3].Strokes.Add(MakeStroke("#D62828"));
        DocumentFile.Save(deck);

        var reopened = Deck.FromPdf(await PdfDocument.OpenAsync(pdf), pdf);
        DocumentFile.ApplySidecar(reopened);

        Assert.Equal(7, reopened.Pages.Count);
        Assert.IsType<PaperPage>(reopened.Pages[1]);
        Assert.Equal(2, ((PdfPage)reopened.Pages[3]).PdfIndex);
        Assert.Equal(Color.Parse("#D62828"), reopened.Pages[3].Strokes[0].Color);
    }

    [Fact]
    public async Task Sidecar_v1_from_earlier_versions_is_still_read()
    {
        var pdf = CopySample();
        File.WriteAllText(DocumentFile.SidecarPath(pdf), """
            {"version":1,"pages":[{"page":2,"strokes":[{"color":"#D62828","width":4,"highlighter":false,"points":[[0.2,0.6,0.5],[0.3,0.7,0.5]]}]}]}
            """);
        var deck = Deck.FromPdf(await PdfDocument.OpenAsync(pdf), pdf);
        DocumentFile.ApplySidecar(deck);

        Assert.Equal(6, deck.Pages.Count);
        Assert.Single(deck.Pages[1].Strokes);
        Assert.Empty(deck.Pages[0].Strokes);
    }

    [Fact]
    public async Task Untouched_pdf_leaves_no_sidecar()
    {
        var pdf = CopySample();
        File.WriteAllText(DocumentFile.SidecarPath(pdf), "{}");
        var deck = Deck.FromPdf(await PdfDocument.OpenAsync(pdf), pdf);
        DocumentFile.Save(deck);
        Assert.False(File.Exists(DocumentFile.SidecarPath(pdf)));
    }

    [Fact]
    public async Task Export_combines_pdf_and_paper_pages()
    {
        var pdf = CopySample();
        var deck = Deck.FromPdf(await PdfDocument.OpenAsync(pdf), pdf);
        deck.Pages.Insert(0, new PaperPage(new Paper(PageFormat.A4Portrait, PaperStyle.Grid, 5)));
        deck.Pages[0].Strokes.Add(MakeStroke());
        deck.Pages.RemoveAt(6); // letzte PDF-Seite weglassen
        var target = Path.Combine(_dir, "export.pdf");

        await PdfExporter.ExportAsync(deck, target);
        var exported = await PdfDocument.OpenAsync(target);

        Assert.Equal(6, exported.PageCount);
        Assert.Equal(595.28, exported.PageSizes[0].Width, 0);
        Assert.Equal(841.89, exported.PageSizes[0].Height, 0);
        Assert.Equal(deck.Pages[1].SizePt.Width, exported.PageSizes[1].Width, 0);
        exported.Dispose();
    }

    [Fact]
    public void Grid_is_centered_and_uses_real_millimetres()
    {
        var paper = new Paper(PageFormat.A4Portrait, PaperStyle.Grid, 5);
        var lines = PaperRenderer.Lines(paper);
        var vertical = lines.Where(l => l.A.X == l.B.X).Select(l => l.A.X).ToList();
        double step = 5 * Paper.PtPerMm;

        Assert.Equal(step, vertical[1] - vertical[0], 6);
        // gleiche Ränder links und rechts
        Assert.Equal(vertical[0], paper.SizePt.Width - vertical[^1], 6);
        // A4-Breite 210 mm → 42 Kästchen → 43 senkrechte Linien
        Assert.Equal(43, vertical.Count);
    }

    [Fact]
    public void Pdf_paths_group_segments_of_equal_width()
    {
        var s = new Stroke(Colors.Black, 4 / 1080.0, false);
        for (int i = 0; i < 10; i++) s.Points.Add(new InkPoint(i * 0.01, 0.5, 0.5f));
        var paths = DocumentFile.ToPdfPaths(s, 540).ToList();

        var (points, _) = Assert.Single(paths);
        Assert.Equal(10, points.Count);
        Assert.Equal(270, points[0].Y, 6); // y gespiegelt (PDF-Ursprung unten links)
    }
}
