using Avalonia;

namespace PdfPresenter;

public enum PageFormat { Wide16x9, A4Portrait, A4Landscape, Infinite }

public enum PaperStyle { Blank, Grid, Lined, Dotted }

/// <summary>Hintergrund einer Papierseite. Abstand in Millimetern (echte Maße der Seite).</summary>
public sealed record Paper(PageFormat Format, PaperStyle Style, double SpacingMm)
{
    public const double PtPerMm = 72 / 25.4;

    /// <summary>
    /// Höhe des 16:9-Rahmens in pt. Bei der unendlichen Leinwand ist das die „Welteinheit“:
    /// Weltkoordinate 1 = Rahmenhöhe bei Zoom 1.
    /// </summary>
    public const double FrameHeightPt = 473.56;

    public static Paper Default { get; } = new(PageFormat.Wide16x9, PaperStyle.Grid, 5);

    /// <summary>Seitengröße in pt. 16:9 wie Typst <c>presentation-16-9</c> (29,7 × 16,7 cm).</summary>
    public Size SizePt => Format switch
    {
        PageFormat.A4Portrait => new Size(595.28, 841.89),
        PageFormat.A4Landscape => new Size(841.89, 595.28),
        _ => new Size(841.89, FrameHeightPt), // 16:9 und Rahmen der unendlichen Leinwand
    };

    public static double DefaultSpacing(PaperStyle style) => style == PaperStyle.Lined ? 8 : 5;

    public string Describe() =>
        $"{Style switch { PaperStyle.Grid => "kariert", PaperStyle.Lined => "liniert", PaperStyle.Dotted => "punktiert", _ => "blanko" }}, " +
        Format switch { PageFormat.A4Portrait => "A4 hoch", PageFormat.A4Landscape => "A4 quer", PageFormat.Infinite => "unendlich", _ => "16:9" };

    public bool IsInfinite => Format == PageFormat.Infinite;

}

/// <summary>
/// Eine Seite des Dokuments. Striche hängen an der Seite (nicht am Index), damit Einfügen und
/// Löschen von Seiten nichts verschiebt. Die Identität (Referenz) dient als Cache-Schlüssel.
/// </summary>
public abstract class Page
{
    public List<Stroke> Strokes { get; } = [];
    /// <summary>Größe in pt.</summary>
    public abstract Size SizePt { get; }
}

public sealed class PdfPage(int pdfIndex, Size sizePt) : Page
{
    /// <summary>Seitennummer in der PDF-Datei (0-basiert).</summary>
    public int PdfIndex { get; } = pdfIndex;
    public override Size SizePt { get; } = sizePt;
}

public sealed class PaperPage(Paper paper) : Page
{
    public Paper Paper { get; set; } = paper;
    public override Size SizePt => Paper.SizePt;

    /// <summary>Sichtbarer Ausschnitt der unendlichen Leinwand (bei festen Formaten ohne Bedeutung).</summary>
    public CanvasView View { get; set; } = CanvasView.Identity;
}

/// <summary>
/// Ausschnitt der unendlichen Leinwand: Weltpunkt <see cref="Offset"/> liegt links oben im Rahmen,
/// <see cref="Zoom"/> 1 = Welt- und Rahmeneinheiten gleich groß. Rahmen = (Welt − Offset) · Zoom.
/// </summary>
public readonly record struct CanvasView(Vector Offset, double Zoom)
{
    public const double MinZoom = 0.1, MaxZoom = 8;

    public static CanvasView Identity { get; } = new(default, 1);

    public Point ToWorld(Point frame) => new Point(frame.X / Zoom, frame.Y / Zoom) + Offset;

    public Point ToFrame(Point world) => new((world.X - Offset.X) * Zoom, (world.Y - Offset.Y) * Zoom);

    /// <summary>Inhalt um <paramref name="frameDelta"/> (Rahmeneinheiten) mitziehen.</summary>
    public CanvasView Pan(Vector frameDelta) => this with { Offset = Offset - frameDelta / Zoom };

    /// <summary>Zoomen, wobei der Weltpunkt unter <paramref name="frameAnchor"/> stehen bleibt.</summary>
    public CanvasView ZoomAt(Point frameAnchor, double factor)
    {
        double zoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        var world = ToWorld(frameAnchor);
        return new CanvasView(world - new Vector(frameAnchor.X / zoom, frameAnchor.Y / zoom), zoom);
    }

    /// <summary>Ausschnitt, der <paramref name="worldRect"/> mittig (mit Rand) in einen Rahmen mit Seitenverhältnis <paramref name="aspect"/> einpasst.</summary>
    public static CanvasView Fit(Rect worldRect, double aspect, double margin = 0.06)
    {
        // Rand relativ zur Inhaltsgröße (bezogen auf den Rahmen), damit nichts am Rand klebt
        double pad = Math.Max(worldRect.Width / aspect, worldRect.Height) * margin;
        var r = worldRect.Inflate(pad);
        double zoom = Math.Clamp(Math.Min(aspect / r.Width, 1 / r.Height), MinZoom, MaxZoom);
        var center = r.Center;
        return new CanvasView(center - new Vector(aspect / 2 / zoom, 0.5 / zoom), zoom);
    }
}

/// <summary>
/// Geöffnetes Dokument: optional ein PDF plus die Seitenliste. Ohne PDF ist es ein reines
/// Tafel-Dokument, das als <c>.pdfp</c> gespeichert wird.
/// </summary>
public sealed class Deck : IDisposable
{
    public PdfDocument? Pdf { get; }
    /// <summary>Pfad der PDF-Datei (falls vorhanden).</summary>
    public string? PdfPath { get; }
    /// <summary>Pfad der .pdfp-Datei, sobald gespeichert (nur Dokumente ohne PDF).</summary>
    public string? FilePath { get; set; }
    public List<Page> Pages { get; } = [];

    public Deck(PdfDocument? pdf, string? pdfPath)
    {
        Pdf = pdf;
        PdfPath = pdfPath;
    }

    /// <summary>Alle Seiten des PDFs in Originalreihenfolge.</summary>
    public static Deck FromPdf(PdfDocument pdf, string path)
    {
        var deck = new Deck(pdf, path);
        for (int i = 0; i < pdf.PageCount; i++) deck.Pages.Add(new PdfPage(i, pdf.PageSizes[i]));
        return deck;
    }

    public static Deck Blank(Paper paper)
    {
        var deck = new Deck(null, null);
        deck.Pages.Add(new PaperPage(paper));
        return deck;
    }

    /// <summary>Anzeigename für Titel und Rückfragen.</summary>
    public string Name => Path.GetFileName(PdfPath ?? FilePath) is { Length: > 0 } n ? n : "Unbenannt";

    public void Dispose() => Pdf?.Dispose();
}

/// <summary>Allgemeine App-Einstellungen (z. B. zuletzt gewähltes Papier) in app-settings.json.</summary>
public sealed class AppSettings
{
    public string Format { get; set; } = DocumentFile.FormatName(Paper.Default.Format);
    public string Style { get; set; } = "grid";
    public double SpacingMm { get; set; } = Paper.Default.SpacingMm;
    public double GeoScale { get; set; } = 1;
    public bool GeoScaleLocked { get; set; } = true;
    public bool GeoSnap { get; set; } = true;
    public bool GeoRuler { get; set; } = true;
    public bool GeoAngleTool { get; set; } = true;
    public bool GeoVisible { get; set; }
    /// <summary>Lage des Geodreiecks (Seiteneinheiten, Grad); null = noch nie platziert.</summary>
    public double[]? GeoPose { get; set; }
    public bool SwipeToPage { get; set; } = true;

    private static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDFPresenter", "app-settings.json");

    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public Paper Paper
    {
        get => DocumentFile.ParsePaper(Format, Style, SpacingMm);
        set
        {
            Format = DocumentFile.FormatName(value.Format);
            Style = DocumentFile.StyleName(value.Style);
            SpacingMm = value.SpacingMm;
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Einstellungen nicht lesbar: {ex.Message}");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, System.Text.Json.JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Einstellungen nicht gespeichert: {ex.Message}");
        }
    }
}
