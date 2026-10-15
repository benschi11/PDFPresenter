using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Media;

namespace PdfPresenter;

/// <summary>
/// Begleitdatei <c>&lt;datei&gt;.pdf.annot.json</c> mit den Annotationen. Das PDF selbst bleibt
/// unverändert. Dasselbe Format liest und schreibt der Rust-Prototyp.
/// Punkte stehen in Seiteneinheiten: y von 0 (oben) bis 1 (unten), x von 0 bis Seitenverhältnis;
/// Stärke in Pixeln bezogen auf 1080 px Seitenhöhe (wie bei den Presets).
/// </summary>
public static class AnnotationFile
{
    public sealed record StrokeDto(string Color, double Width, bool Highlighter, double[][] Points);
    public sealed record PageDto(int Page, List<StrokeDto> Strokes);
    public sealed record FileDto(int Version, List<PageDto> Pages);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string PathFor(string pdfPath) => pdfPath + ".annot.json";

    public static void Save(string pdfPath, IReadOnlyDictionary<int, List<Stroke>> ink)
    {
        var pages = ink
            .Where(kv => kv.Value.Count > 0)
            .OrderBy(kv => kv.Key)
            .Select(kv => new PageDto(kv.Key + 1, kv.Value.Select(ToDto).ToList()))
            .ToList();
        var path = PathFor(pdfPath);
        if (pages.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path); // nichts annotiert → keine leere Datei liegen lassen
            return;
        }
        File.WriteAllText(path, JsonSerializer.Serialize(new FileDto(1, pages), Json));
    }

    /// <summary>Liest die Begleitdatei, falls vorhanden (Seitenindex 0-basiert).</summary>
    public static Dictionary<int, List<Stroke>> Load(string pdfPath)
    {
        var result = new Dictionary<int, List<Stroke>>();
        var path = PathFor(pdfPath);
        if (!File.Exists(path)) return result;
        var dto = JsonSerializer.Deserialize<FileDto>(File.ReadAllText(path), Json);
        foreach (var page in dto?.Pages ?? [])
            result[page.Page - 1] = page.Strokes.Select(FromDto).ToList();
        return result;
    }

    private static StrokeDto ToDto(Stroke s) => new(
        PenPreset.Hex(s.Color),
        Math.Round(s.Width * 1080, 3),
        s.Highlighter,
        s.Points.Select(p => new[] { Math.Round(p.X, 5), Math.Round(p.Y, 5), Math.Round(p.Pressure, 3) }).ToArray());

    private static Stroke FromDto(StrokeDto d)
    {
        var preset = new PenPreset(d.Color, d.Width, d.Highlighter);
        var s = new Stroke(preset.ToColor(), d.Width / 1080.0, d.Highlighter);
        foreach (var p in d.Points)
            if (p.Length >= 2) s.Points.Add(new InkPoint(p[0], p[1], p.Length > 2 ? (float)p[2] : 0.5f));
        return s;
    }

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
