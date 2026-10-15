using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace PdfPresenter;

/// <summary>
/// Stiftauswahl: Modus (Stift/Textmarker), Palette, freie Farbe (Sättigung/Helligkeit + Farbton),
/// Hex-Eingabe, Stärke und gespeicherte Presets. Jede Änderung wird sofort in
/// pen-presets.json geschrieben.
/// </summary>
public sealed class PenPicker : Border
{
    private readonly PenSettingsFile _settings;
    private readonly StrokePreview _preview = new() { Height = 64 };
    private readonly ToggleButton _penMode = new() { Content = "Stift", MinWidth = 120 };
    private readonly ToggleButton _highlighterMode = new() { Content = "Textmarker", MinWidth = 120 };
    private readonly ColorSpectrum _spectrum = new()
    {
        Components = ColorSpectrumComponents.SaturationValue,
        Width = 160,
        Height = 160,
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly ColorSlider _hue = new()
    {
        ColorModel = ColorModel.Hsva,
        ColorComponent = ColorComponent.Component1,
        IsAlphaVisible = false,
        IsPerceptive = true, // Farbton-Leiste immer voll gesättigt
        Orientation = Orientation.Horizontal,
        Height = 22,
    };
    private readonly TextBox _hex = new() { Width = 100 };
    private readonly Slider _width = new()
    {
        Minimum = PenPreset.MinWidth,
        Maximum = PenPreset.MaxWidth,
        TickFrequency = 1,
        IsSnapToTickEnabled = true,
        Width = 190,
    };
    private readonly TextBlock _widthLabel = new() { VerticalAlignment = VerticalAlignment.Center, Width = 70 };
    private readonly WrapPanel _presetPanel = new() { MaxWidth = 290 };
    private readonly Button _addPreset = new() { Content = "Als Preset speichern" };
    private readonly Button _deletePreset = new() { Content = "Löschen" };
    private bool _updating;

    /// <summary>Aktuelle Einstellung wurde geändert (Klick, Preset, Taste).</summary>
    public event Action? Changed;
    public event Action? CloseRequested;
    /// <summary>Preset hinzugefügt oder gelöscht.</summary>
    public event Action? PresetsChanged;

    public PenPicker(PenSettingsFile settings)
    {
        _settings = settings;
        Background = new SolidColorBrush(Color.FromArgb(0xf2, 0x22, 0x25, 0x2b));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xff, 0xff, 0xff));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(14);
        Margin = new Thickness(20, 10);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        BoxShadow = BoxShadows.Parse("0 4 24 0 #80000000");
        IsVisible = false;

        var close = new Button { Content = "✕", HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8, 2) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Stift", FontSize = 18, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

        _penMode.Click += (_, _) => SetMode(false);
        _highlighterMode.Click += (_, _) => SetMode(true);

        var palette = new WrapPanel { MaxWidth = 290 };
        foreach (var hex in PenStore.Palette)
        {
            var color = Color.Parse(hex);
            var b = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xff, 0xff, 0xff)),
                BorderThickness = new Thickness(1),
            };
            ToolTip.SetTip(b, hex);
            b.Click += (_, _) => Apply(Current with { Color = hex });
            palette.Children.Add(b);
        }

        _spectrum.ColorChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            _hue.HsvColor = _spectrum.HsvColor;
            _updating = false;
            Apply(Current with { Color = PenPreset.Hex(_spectrum.Color) }, keepSpectrum: true);
        };
        _hue.ColorChanged += (_, _) =>
        {
            if (_updating) return;
            var hsv = _spectrum.HsvColor;
            _updating = true;
            _spectrum.HsvColor = new HsvColor(1, _hue.HsvColor.H, hsv.S, hsv.V);
            _updating = false;
            Apply(Current with { Color = PenPreset.Hex(_spectrum.Color) }, keepSpectrum: true);
        };
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyHex(); };
        _hex.LostFocus += (_, _) => ApplyHex();

        _width.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updating)
                Apply(Current with { Width = Math.Round(_width.Value) });
        };

        _addPreset.Click += (_, _) =>
        {
            if (_settings.Presets.Count >= PenStore.MaxPresets || _settings.Presets.Contains(Current)) return;
            _settings.Presets.Add(Current);
            Persist();
            Sync();
            PresetsChanged?.Invoke();
        };
        _deletePreset.Click += (_, _) =>
        {
            int i = _settings.Presets.IndexOf(Current);
            if (i < 0) return;
            _settings.Presets.RemoveAt(i);
            Persist();
            Sync();
            PresetsChanged?.Invoke();
        };

        // Scrollbar, falls die Folienfläche niedriger ist als das Panel (z. B. 720p-Fenster).
        Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
        {
            Spacing = 10,
            Width = 290,
            Margin = new Thickness(0, 0, 12, 0),
            Children =
            {
                header,
                _preview,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _penMode, _highlighterMode } },
                Caption("Farbe"),
                palette,
                _spectrum,
                _hue,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center }, _hex },
                },
                Caption("Stärke (px bei 1080p)"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _width, _widthLabel } },
                Caption("Presets (Tasten 1–9)"),
                _presetPanel,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _addPreset, _deletePreset } },
                new TextBlock
                {
                    Text = "Tab/Esc schließt · H = erster Textmarker",
                    FontSize = 11, Opacity = 0.6,
                },
            },
        },
        };
        Sync();
    }

    public PenPreset Current => _settings.Current;
    public IReadOnlyList<PenPreset> Presets => _settings.Presets;

    public void Toggle() => IsVisible = !IsVisible;

    /// <summary>Preset Nummer <paramref name="index"/> (0-basiert) übernehmen.</summary>
    public void SelectPreset(int index)
    {
        if (index >= 0 && index < _settings.Presets.Count) Apply(_settings.Presets[index]);
    }

    public void SelectFirstHighlighter()
    {
        var p = _settings.Presets.FirstOrDefault(p => p.Highlighter);
        Apply(p ?? Current with { Highlighter = true, Width = 27 });
    }

    private void SetMode(bool highlighter)
    {
        if (highlighter == Current.Highlighter) { Sync(); return; }
        // Typische Stärke mitwechseln, damit ein 4-px-Textmarker nicht überrascht.
        double width = highlighter ? Math.Max(Current.Width, 20) : Math.Min(Current.Width, 8);
        Apply(Current with { Highlighter = highlighter, Width = width });
    }

    private void ApplyHex()
    {
        var text = _hex.Text?.Trim() ?? "";
        if (!text.StartsWith('#')) text = "#" + text;
        if (Color.TryParse(text, out var c)) Apply(Current with { Color = PenPreset.Hex(c) });
        else Sync();
    }

    private void Apply(PenPreset preset, bool keepSpectrum = false)
    {
        _settings.Current = preset with { Color = preset.Color.ToUpperInvariant() };
        Persist();
        Sync(keepSpectrum);
        Changed?.Invoke();
    }

    private void Persist() => PenStore.Save(_settings);

    /// <summary>Alle Bedienelemente an die aktuelle Einstellung angleichen.</summary>
    private void Sync(bool keepSpectrum = false)
    {
        _updating = true;
        var cur = Current;
        var color = Color.Parse(cur.Color);
        _preview.Preset = cur;
        _penMode.IsChecked = !cur.Highlighter;
        _highlighterMode.IsChecked = cur.Highlighter;
        if (!keepSpectrum)
        {
            _spectrum.Color = color;
            _hue.HsvColor = _spectrum.HsvColor;
        }
        _hex.Text = cur.Color;
        _width.Value = cur.Width;
        _widthLabel.Text = $"{cur.Width:0} px";

        int selected = _settings.Presets.IndexOf(cur);
        _presetPanel.Children.Clear();
        for (int i = 0; i < _settings.Presets.Count; i++)
        {
            var p = _settings.Presets[i];
            int index = i;
            var b = new Button
            {
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(0),
                Background = Brushes.White,
                BorderBrush = i == selected ? new SolidColorBrush(Color.Parse("#3D8BFF")) : Brushes.Transparent,
                BorderThickness = new Thickness(3),
                Content = new Panel
                {
                    Children =
                    {
                        new PresetSwatch { Preset = p },
                        new TextBlock
                        {
                            Text = i < 9 ? $"{i + 1}" : "", FontSize = 10, Foreground = Brushes.Gray,
                            Margin = new Thickness(3, 1), HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                        },
                    },
                },
            };
            ToolTip.SetTip(b, $"{(p.Highlighter ? "Textmarker" : "Stift")} {p.Color} · {p.Width:0} px");
            b.Click += (_, _) => SelectPreset(index);
            _presetPanel.Children.Add(b);
        }
        _addPreset.IsEnabled = selected < 0 && _settings.Presets.Count < PenStore.MaxPresets;
        _deletePreset.IsEnabled = selected >= 0;
        _updating = false;
    }

    private static TextBlock Caption(string text) => new() { Text = text, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 4, 0, -4) };
}

/// <summary>Beispielstrich in Originalgröße (bezogen auf 1080p) mit Druckverlauf.</summary>
internal sealed class StrokePreview : Control
{
    private PenPreset? _preset;
    public PenPreset? Preset { get => _preset; set { _preset = value; InvalidateVisual(); } }

    public override void Render(DrawingContext ctx)
    {
        var r = new Rect(Bounds.Size);
        ctx.DrawRectangle(Brushes.White, null, r, 6, 6);
        if (_preset is null) return;
        // Seiteneinheiten mit Maßstab 1080 → 1 px Stärke = 1 Bildschirmpixel bei Full-HD
        const double scale = 1080;
        var stroke = new Stroke(_preset.ToColor(), _preset.PageWidth, _preset.Highlighter);
        int n = 60;
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            double x = (16 + t * (r.Width - 32)) / scale;
            double y = (r.Height / 2 + Math.Sin(t * Math.PI * 2) * r.Height * 0.25) / scale;
            stroke.Points.Add(new InkPoint(x, y, (float)(0.15 + 0.85 * Math.Sin(t * Math.PI))));
        }
        using (ctx.PushClip(r)) stroke.Draw(ctx, Matrix.CreateScale(scale, scale));
    }
}

/// <summary>Kleine Vorschau eines Presets: Punkt in Strichstärke bzw. Textmarker-Balken.</summary>
internal sealed class PresetSwatch : Control
{
    public PenPreset? Preset { get; set; }

    public override void Render(DrawingContext ctx)
    {
        if (Preset is null) return;
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2 + 3);
        var brush = new SolidColorBrush(Preset.ToColor());
        double d = Math.Clamp(Preset.Width, 3, 28);
        if (Preset.Highlighter)
            ctx.DrawRectangle(brush, null, new Rect(c.X - 14, c.Y - d / 2, 28, d), 3, 3);
        else
            ctx.DrawEllipse(brush, null, c, d / 2, d / 2);
    }
}
