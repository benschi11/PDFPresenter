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
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public MainWindow(Options options)
    {
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
        _picker.CloseRequested += ClosePicker;
        Content = new Panel { Children = { _view, _picker } };

        Title = _view.Title;
        _view.StatusChanged += () => Title = _view.Title;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // Tunnel-Handler: laufen vor den Steuerelementen, damit z. B. Leertaste immer blättert
        // und Tab nicht als Fokuswechsel verbraucht wird.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

        if (options.Fullscreen) WindowState = WindowState.FullScreen;
        Opened += async (_, _) =>
        {
            _view.Focus();
            if (options.PdfPath is { } path) await _view.OpenAsync(path);
        };
    }

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
