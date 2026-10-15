using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace PdfPresenter;

/// <summary>
/// Stifteinstellung: Farbe, Stärke (in Pixeln bezogen auf eine 1080 px hohe Folie) und ob es ein
/// Textmarker ist. Dasselbe JSON-Format nutzt der Rust-Prototyp, Presets gelten also für beide.
/// </summary>
public sealed record PenPreset(string Color, double Width, bool Highlighter)
{
    public const double MinWidth = 1, MaxWidth = 40;
    public const byte HighlighterAlpha = 0x60;

    public Color ToColor()
    {
        var c = Avalonia.Media.Color.TryParse(Color, out var parsed) ? parsed : Colors.Black;
        return Avalonia.Media.Color.FromArgb(Highlighter ? HighlighterAlpha : (byte)0xff, c.R, c.G, c.B);
    }

    /// <summary>Breite in Seiteneinheiten (Anteil der Seitenhöhe).</summary>
    [JsonIgnore]
    public double PageWidth => Math.Clamp(Width, MinWidth, MaxWidth) / 1080.0;

    public static string Hex(Color c) => string.Create(CultureInfo.InvariantCulture, $"#{c.R:X2}{c.G:X2}{c.B:X2}");
}

public sealed class PenSettingsFile
{
    public PenPreset Current { get; set; } = PenStore.DefaultPresets[1];
    public List<PenPreset> Presets { get; set; } = [.. PenStore.DefaultPresets];
}

/// <summary>Lädt/speichert <c>pen-presets.json</c> im Konfigurationsordner
/// (Windows: %APPDATA%\PDFPresenter, Linux: ~/.config/PDFPresenter).</summary>
public static class PenStore
{
    public const int MaxPresets = 12;

    public static readonly PenPreset[] DefaultPresets =
    [
        new("#101010", 4, false),
        new("#1F5FD6", 4, false),
        new("#D62828", 4, false),
        new("#1F9D4A", 4, false),
        new("#FFE000", 27, true),
        new("#6CE0FF", 27, true),
    ];

    /// <summary>Feste Palette im Stiftauswahl-Fenster (auch im Rust-Prototyp identisch).</summary>
    public static readonly string[] Palette =
    [
        "#101010", "#5A5A5A", "#A0A0A0", "#FFFFFF", "#1F5FD6", "#0B9BD6", "#1F9D4A", "#7CC242",
        "#D62828", "#F0642D", "#F2A900", "#FFE000", "#8E44AD", "#D6338A", "#8B5A2B", "#6CE0FF",
    ];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PDFPresenter", "pen-presets.json");

    public static PenSettingsFile Load()
    {
        try
        {
            if (File.Exists(FilePath) &&
                JsonSerializer.Deserialize<PenSettingsFile>(File.ReadAllText(FilePath), Json) is { } f &&
                f.Current is not null && f.Presets is not null)
                return f;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Presets konnten nicht geladen werden: {ex.Message}");
        }
        return new PenSettingsFile();
    }

    public static void Save(PenSettingsFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(file, Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Presets konnten nicht gespeichert werden: {ex.Message}");
        }
    }
}
