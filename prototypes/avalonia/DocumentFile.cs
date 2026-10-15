using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;

namespace PdfPresenter;

/// <summary>
/// Speichern und Laden von Dokumenten.
/// <list type="bullet">
/// <item>Dokument mit PDF → Begleitdatei <c>&lt;datei&gt;.pdf.annot.json</c> neben dem PDF (das PDF
/// bleibt unverändert).</item>
/// <item>Dokument ohne PDF → eigene Datei <c>.pdfp</c>.</item>
/// </list>
/// Version 2 speichert die komplette Seitenliste (PDF- und Papierseiten). Version 1 (nur Striche
/// pro PDF-Seite, auch vom Rust-Prototyp verstanden) wird weiterhin gelesen.
/// Punkte stehen in Seiteneinheiten: y von 0 (oben) bis 1 (unten), x von 0 bis Seitenverhältnis;
/// Stärke in Pixeln bezogen auf 1080 px Seitenhöhe (wie bei den Presets).
/// </summary>
public static class DocumentFile
{
    public const string Extension = ".pdfp";

    public sealed record StrokeDto(string Color, double Width, bool Highlighter, double[][] Points);

    public sealed class PageDto
    {
        /// <summary>"pdf" oder "paper" (Version 2).</summary>
        public string? Type { get; set; }
        /// <summary>Seitennummer in der PDF-Datei, 1-basiert (Version 2).</summary>
        public int? PdfPage { get; set; }
        /// <summary>Seitennummer, 1-basiert (Version 1).</summary>
        public int? Page { get; set; }
        public string? Format { get; set; }
        public string? Style { get; set; }
        public double? SpacingMm { get; set; }
        public List<StrokeDto> Strokes { get; set; } = [];
    }

    public sealed class FileDto
    {
        public int Version { get; set; }
        public List<PageDto> Pages { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string SidecarPath(string pdfPath) => pdfPath + ".annot.json";

    // ─────────────────────────────── Speichern ───────────────────────────────

    /// <summary>Speichert das Dokument; liefert den geschriebenen Pfad.</summary>
    public static string Save(Deck deck)
    {
        if (deck.PdfPath is { } pdf)
        {
            var path = SidecarPath(pdf);
            if (IsUntouched(deck))
            {
                if (File.Exists(path)) File.Delete(path); // nichts zu merken → keine leere Datei liegen lassen
                return path;
            }
            File.WriteAllText(path, Serialize(deck));
            return path;
        }
        var target = deck.FilePath ?? throw new InvalidOperationException("Kein Speicherort für das Dokument.");
        File.WriteAllText(target, Serialize(deck));
        return target;
    }

    public static string Serialize(Deck deck) =>
        JsonSerializer.Serialize(new FileDto { Version = 2, Pages = deck.Pages.Select(ToDto).ToList() }, Json);

    /// <summary>Originalreihenfolge des PDFs, keine Papierseiten, keine Striche?</summary>
    private static bool IsUntouched(Deck deck) =>
        deck.Pdf is { } pdf
        && deck.Pages.Count == pdf.PageCount
        && deck.Pages.Select((p, i) => p is PdfPage pp && pp.PdfIndex == i && p.Strokes.Count == 0).All(ok => ok);

    private static PageDto ToDto(Page page)
    {
        var dto = new PageDto { Strokes = page.Strokes.Select(ToDto).ToList() };
        switch (page)
        {
            case PdfPage p:
                dto.Type = "pdf";
                dto.PdfPage = p.PdfIndex + 1;
                break;
            case PaperPage p:
                dto.Type = "paper";
                dto.Format = FormatName(p.Paper.Format);
                dto.Style = StyleName(p.Paper.Style);
                dto.SpacingMm = p.Paper.SpacingMm;
                break;
        }
        return dto;
    }

    private static StrokeDto ToDto(Stroke s) => new(
        PenPreset.Hex(s.Color),
        Math.Round(s.Width * 1080, 3),
        s.Highlighter,
        s.Points.Select(p => new[] { Math.Round(p.X, 5), Math.Round(p.Y, 5), Math.Round(p.Pressure, 3) }).ToArray());

    // ──────────────────────────────── Laden ─────────────────────────────────

    /// <summary>Wendet eine vorhandene Begleitdatei auf ein frisch geöffnetes PDF-Dokument an.</summary>
    public static void ApplySidecar(Deck deck)
    {
        if (deck.PdfPath is not { } pdf || deck.Pdf is not { } doc) return;
        var path = SidecarPath(pdf);
        if (!File.Exists(path)) return;
        var file = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Json)
                   ?? throw new InvalidDataException("leere Datei");
        if (file.Version <= 1)
        {
            foreach (var p in file.Pages)
                if (p.Page is { } n && n >= 1 && n <= deck.Pages.Count)
                    deck.Pages[n - 1].Strokes.AddRange(p.Strokes.Select(FromDto));
            return;
        }
        var pages = file.Pages.Select(p => FromDto(p, doc)).OfType<Page>().ToList();
        if (pages.Count == 0) return;
        deck.Pages.Clear();
        deck.Pages.AddRange(pages);
    }

    /// <summary>Lädt ein Dokument ohne PDF (.pdfp).</summary>
    public static Deck LoadPdfp(string path)
    {
        var file = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Json)
                   ?? throw new InvalidDataException("leere Datei");
        var deck = new Deck(null, null) { FilePath = path };
        deck.Pages.AddRange(file.Pages.Select(p => FromDto(p, null)).OfType<Page>());
        if (deck.Pages.Count == 0) deck.Pages.Add(new PaperPage(Paper.Default));
        return deck;
    }

    private static Page? FromDto(PageDto dto, PdfDocument? pdf)
    {
        Page? page = dto.Type switch
        {
            "pdf" when pdf is not null && dto.PdfPage is { } n && n >= 1 && n <= pdf.PageCount
                => new PdfPage(n - 1, pdf.PageSizes[n - 1]),
            "paper" => new PaperPage(ParsePaper(dto.Format, dto.Style, dto.SpacingMm)),
            _ => null, // unbekannter Typ oder PDF-Seite ohne PDF: überspringen
        };
        page?.Strokes.AddRange(dto.Strokes.Select(FromDto));
        return page;
    }

    private static Stroke FromDto(StrokeDto d)
    {
        var preset = new PenPreset(d.Color, d.Width, d.Highlighter);
        var s = new Stroke(preset.ToColor(), d.Width / 1080.0, d.Highlighter);
        foreach (var p in d.Points)
            if (p.Length >= 2) s.Points.Add(new InkPoint(p[0], p[1], p.Length > 2 ? (float)p[2] : 0.5f));
        return s;
    }

    public static Paper ParsePaper(string? format, string? style, double? spacingMm)
    {
        var st = ParseStyle(style);
        double spacing = spacingMm is > 0 ? spacingMm.Value : Paper.DefaultSpacing(st);
        return new Paper(ParseFormat(format), st, spacing);
    }

    public static string FormatName(PageFormat f) => f switch
    {
        PageFormat.A4Portrait => "a4-hoch",
        PageFormat.A4Landscape => "a4-quer",
        _ => "16:9",
    };

    private static PageFormat ParseFormat(string? s) => s switch
    {
        "a4-hoch" => PageFormat.A4Portrait,
        "a4-quer" => PageFormat.A4Landscape,
        _ => PageFormat.Wide16x9,
    };

    public static string StyleName(PaperStyle s) => s switch
    {
        PaperStyle.Grid => "grid",
        PaperStyle.Lined => "lined",
        PaperStyle.Dotted => "dotted",
        _ => "blank",
    };

    private static PaperStyle ParseStyle(string? s) => s switch
    {
        "grid" => PaperStyle.Grid,
        "lined" => PaperStyle.Lined,
        "dotted" => PaperStyle.Dotted,
        _ => PaperStyle.Blank,
    };

    // ─────────────────────────────── PDF-Export ──────────────────────────────

    /// <summary>
    /// Zerlegt einen Strich für den PDF-Export in Pfade (PDF-Koordinaten in pt, Ursprung unten links).
    /// Stiftstriche mit Druck: aufeinanderfolgende Segmente gleicher (gerundeter) Breite werden zu
    /// einem Pfad zusammengefasst, damit die Datei klein bleibt.
    /// </summary>
    public static IEnumerable<(List<Point> Points, double WidthPt)> ToPdfPaths(Stroke s, double pageHeightPt)
    {
        Point Map(InkPoint p) => new(p.X * pageHeightPt, pageHeightPt - p.Y * pageHeightPt);
        if (s.Points.Count == 0) yield break;
        if (s.Points.Count == 1)
        {
            var p = Map(s.Points[0]);
            yield return ([p, p + new Vector(0.01, 0)], s.WidthAt(s.Points[0].Pressure) * pageHeightPt);
            yield break;
        }
        if (s.Highlighter)
        {
            yield return (s.Points.Select(Map).ToList(), s.Width * pageHeightPt);
            yield break;
        }
        List<Point>? current = null;
        double currentWidth = 0;
        for (int i = 1; i < s.Points.Count; i++)
        {
            var a = s.Points[i - 1];
            var b = s.Points[i];
            double w = Math.Round(s.WidthAt((a.Pressure + b.Pressure) / 2) * pageHeightPt * 4) / 4; // 0,25-pt-Stufen
            if (current is null || w != currentWidth)
            {
                if (current is not null) yield return (current, currentWidth);
                current = [Map(a)];
                currentWidth = w;
            }
            current.Add(Map(b));
        }
        if (current is not null) yield return (current, currentWidth);
    }
}
