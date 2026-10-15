using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace PdfPresenter;

/// <summary>
/// Kompakte Stiftleiste für die Aufnahme: liegt <b>oberhalb</b> der Folienfläche (nicht darüber),
/// zeigt nur die gespeicherten Presets, Radierer und den Zugang zur großen Stift-Ansicht.
/// In OBS wird dieser Streifen weggeschnitten bzw. nur die Folienfläche aufgenommen.
/// </summary>
public sealed class CompactBar : Border
{
    public const double BarHeight = 44;

    private readonly StackPanel _presets = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly ToggleButton _eraser = MakeToggle("Radierer");
    private readonly Button _settings = new() { Content = "⚙ Stift…", Focusable = false, Padding = new Thickness(10, 4) };
    private readonly Button _addPage = new() { Content = "＋ Seite", Focusable = false, Padding = new Thickness(10, 4) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 12 };

    public event Action<int>? PresetClicked;
    public event Action? EraserClicked;
    public event Action? SettingsClicked;
    public event Action? AddPageClicked;

    public CompactBar()
    {
        Height = BarHeight;
        Background = new SolidColorBrush(Color.FromRgb(0x1b, 0x1d, 0x21));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x36, 0x3c));
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(6, 3);

        _eraser.Click += (_, _) => EraserClicked?.Invoke();
        _settings.Click += (_, _) => SettingsClicked?.Invoke();
        _addPage.Click += (_, _) => AddPageClicked?.Invoke();
        ToolTip.SetTip(_addPage, "Leeres Blatt nach der aktuellen Seite einfügen");

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _addPage, _status } };
        DockPanel.SetDock(right, Dock.Right);
        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { _presets, new Border { Width = 1, Background = Brushes.Gray, Opacity = 0.4, Margin = new Thickness(2, 4) }, _eraser, _settings },
        };
        Child = new DockPanel { Children = { right, left } };
    }

    private static ToggleButton MakeToggle(string text) => new() { Content = text, Focusable = false, Padding = new Thickness(10, 4) };

    public void Refresh(IReadOnlyList<PenPreset> presets, PenPreset current, Tool tool, string status)
    {
        _presets.Children.Clear();
        for (int i = 0; i < presets.Count; i++)
        {
            var p = presets[i];
            int index = i;
            bool selected = tool == Tool.Draw && p == current;
            var b = new Button
            {
                Width = 36, Height = 36, Padding = new Thickness(0), Focusable = false,
                Background = Brushes.White,
                BorderBrush = selected ? new SolidColorBrush(Color.Parse("#3D8BFF")) : Brushes.Transparent,
                BorderThickness = new Thickness(3),
                Content = new Panel
                {
                    Children =
                    {
                        new PresetSwatch { Preset = p },
                        new TextBlock
                        {
                            Text = i < 9 ? $"{i + 1}" : "", FontSize = 9, Foreground = Brushes.Gray,
                            Margin = new Thickness(2, 0), HorizontalAlignment = HorizontalAlignment.Left,
                            VerticalAlignment = VerticalAlignment.Top,
                        },
                    },
                },
            };
            ToolTip.SetTip(b, $"{(p.Highlighter ? "Textmarker" : "Stift")} {p.Color} · {p.Width:0} px");
            b.Click += (_, _) => PresetClicked?.Invoke(index);
            _presets.Children.Add(b);
        }
        _eraser.IsChecked = tool == Tool.Eraser;
        _status.Text = status;
    }
}
