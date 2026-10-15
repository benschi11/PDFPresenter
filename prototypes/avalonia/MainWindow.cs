using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace PdfPresenter;

public sealed class MainWindow : Window
{
    private readonly PresenterView _view;
    private readonly PenPicker _picker;
    private readonly CompactBar _bar = new();
    private readonly Options _options;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public MainWindow(Options options)
    {
        _options = options;
        Width = 1280;
        Height = 720;
        Background = Brushes.Black;
        _view = new PresenterView(options);
        _picker = new PenPicker(PenStore.Load());
        _view.Pen = _picker.Current;
        _picker.Changed += () =>
        {
            _view.Pen = _picker.Current;
            _view.Tool = Tool.Draw;
        };
        _picker.PresetsChanged += RefreshBar;
        _picker.CloseRequested += ClosePicker;
        _bar.PresetClicked += i => _picker.SelectPreset(i);
        _bar.EraserClicked += () => _view.Tool = _view.Tool == Tool.Eraser ? Tool.Draw : Tool.Eraser;
        _bar.SettingsClicked += () => _picker.IsVisible = !_picker.IsVisible;

        // Stiftleiste oben, darunter die Folienfläche; das große Stift-Panel liegt über der Folie.
        DockPanel.SetDock(_bar, Dock.Top);
        Content = new DockPanel { Children = { _bar, new Panel { Children = { _view, _picker } } } };

        Title = _view.Title;
        _view.StatusChanged += () =>
        {
            Title = _view.Title;
            RefreshBar();
        };
        RefreshBar();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Tunnel-Handler: laufen vor den Steuerelementen, damit z. B. Leertaste immer blättert
        // und Tab nicht als Fokuswechsel verbraucht wird.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

        if (options.Fullscreen) WindowState = WindowState.FullScreen;
        Opened += async (_, _) =>
        {
            if (options.Canvas is { } canvas && !options.Fullscreen) SizeForCanvas(canvas);
            _view.Focus();
            if (options.PdfPath is { } path) await _view.OpenAsync(path);
        };
    }

    /// <summary>Fenstergröße so wählen, dass die Folienfläche exakt <paramref name="canvas"/> Pixel hat.</summary>
    private void SizeForCanvas(PixelSize canvas)
    {
        double s = RenderScaling;
        double bar = _bar.IsVisible ? CompactBar.BarHeight : 0;
        Width = canvas.Width / s;
        Height = canvas.Height / s + bar;
    }

    private void RefreshBar() => _bar.Refresh(_picker.Presets, _picker.Current, _view.Tool, _view.PageStatus);

    private void ClosePicker()
    {
        _picker.IsVisible = false;
        _view.Focus();
    }

    /// <summary>Klick neben das offene Stift-Panel schließt es (ohne zu zeichnen).</summary>
    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_picker.IsVisible) return;
        if (e.Source is Visual v && (v == _picker || _picker.IsVisualAncestorOf(v))) return;
        ClosePicker();
        e.Handled = true;
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Eingaben im Hex-Feld nicht als Tastenkürzel auswerten
        if (e.Source is TextBox && e.Key is not (Key.Escape or Key.Tab)) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Tab:
                if (_picker.IsVisible) ClosePicker();
                else _picker.IsVisible = true;
                break;
            case Key.Escape when _picker.IsVisible:
                ClosePicker();
                break;
            case Key.F11:
                ToggleFullscreen();
                break;
            case Key.F2:
                _bar.IsVisible = !_bar.IsVisible;
                if (_options.Canvas is { } canvas && WindowState != WindowState.FullScreen) SizeForCanvas(canvas);
                break;
            case Key.Escape when WindowState == WindowState.FullScreen:
                ToggleFullscreen();
                break;
            case Key.Q when ctrl:
                Close();
                break;
            case Key.O when ctrl:
                await OpenWithDialog();
                break;
            case >= Key.D1 and <= Key.D9 when !ctrl:
                _picker.SelectPreset(e.Key - Key.D1);
                break;
            case >= Key.NumPad1 and <= Key.NumPad9 when !ctrl:
                _picker.SelectPreset(e.Key - Key.NumPad1);
                break;
            case Key.H when !ctrl:
                _picker.SelectFirstHighlighter();
                break;
            default:
                if (!_view.HandleKey(e)) return;
                break;
        }
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _stateBeforeFullscreen;
        else
        {
            _stateBeforeFullscreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private async Task OpenWithDialog()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "PDF öffnen",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await _view.OpenAsync(path);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path) await _view.OpenAsync(path);
    }
}
