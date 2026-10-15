using System.Globalization;
using Avalonia;

namespace PdfPresenter;

/// <summary>Kommandozeilenoptionen des Prototyps.</summary>
public sealed record Options(
    string? PdfPath,
    int FadeMs,
    bool Fullscreen,
    bool Continuous,
    bool TouchDraws,
    PixelSize? Canvas)
{
    public static Options Parse(string[] args)
    {
        string? path = null;
        int fade = 400;
        bool fullscreen = false, continuous = false, touch = false;
        PixelSize? canvas = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--fade" when i + 1 < args.Length:
                    fade = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--fullscreen": fullscreen = true; break;
                case "--continuous": continuous = true; break;
                case "--touch": touch = true; break;
                case "--canvas" when i + 1 < args.Length:
                    var parts = args[++i].Split('x', 'X');
                    canvas = new PixelSize(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
                case "-h" or "--help":
                    Console.WriteLine("""
                        PdfPresenter.Avalonia [datei.pdf] [Optionen]
                          --fade <ms>     Dauer des Crossfades (Standard 400)
                          --fullscreen    im Vollbild starten (F11 schaltet um)
                          --continuous    durchgehend mit Bildwiederholrate rendern
                          --touch         Finger dürfen zeichnen (Standard: nur Stift/Maus)
                          --canvas WxH    Fenster so groß, dass die Folienfläche exakt WxH Pixel hat
                                          (zuzüglich Stiftleiste oben), z. B. --canvas 1920x1080
                        """);
                    Environment.Exit(0);
                    break;
                default:
                    path = args[i];
                    break;
            }
        }
        return new Options(path, fade, fullscreen, continuous, touch, canvas);
    }
}

internal static class Program
{
    public static Options Options { get; private set; } = null!;

    [STAThread]
    public static void Main(string[] args)
    {
        Options = Options.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
