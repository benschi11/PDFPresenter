using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PdfPresenter;

public sealed class MainWindow : Window
{
    private readonly PresenterView _view;
    private readonly PenPicker _picker;
    private readonly CompactBar _bar = new();
    private readonly Options _options;
    private readonly StackPanel _header = new();
    private readonly TextBlock _notice = new()
    {
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Margin = new Thickness(12, 0),
        FontSize = 12,
        Opacity = 0.8,
    };
    private CancellationTokenSource? _noticeTimer;
    private bool _closeConfirmed;
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

        // Kopfbereich (Menü + Stiftleiste) oben, darunter die Folienfläche; das große Stift-Panel
        // liegt über der Folie. In OBS wird der Kopfbereich weggeschnitten.
        var menuRow = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(0x22, 0x25, 0x2b)) };
        DockPanel.SetDock(_notice, Dock.Right);
        menuRow.Children.Add(_notice);
        menuRow.Children.Add(BuildMenu());
        _header.Children.Add(menuRow);
        _header.Children.Add(_bar);
        DockPanel.SetDock(_header, Dock.Top);
        Content = new DockPanel { Children = { _header, new Panel { Children = { _view, _picker } } } };
        _view.Notice += ShowNotice;

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
            Closing += OnClosingAsync;
            _view.Focus();
            if (options.PdfPath is { } path) await _view.OpenAsync(path);
            if (options.ExportTo is { } target && _view.Document is { } doc)
            {
                try
                {
                    await doc.ExportAsync(target, _view.Ink);
                    Console.WriteLine($"Exportiert: {target}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Export fehlgeschlagen: {ex.Message}");
                    Environment.ExitCode = 1;
                }
                Close();
            }
        };
    }

    /// <summary>Fenstergröße so wählen, dass die Folienfläche exakt <paramref name="canvas"/> Pixel hat.</summary>
    private void SizeForCanvas(PixelSize canvas)
    {
        // nach dem Layout messen, wie hoch der Kopfbereich tatsächlich ist
        Dispatcher.UIThread.Post(() =>
        {
            double s = RenderScaling;
            double top = _header.IsVisible ? _header.Bounds.Height : 0;
            Width = canvas.Width / s;
            Height = canvas.Height / s + top;
        }, DispatcherPriority.Background);
    }

    // ─────────────────────────────── Menü ────────────────────────────────

    private Menu BuildMenu()
    {
        MenuItem Item(string header, string? gesture, Action action)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null) item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => action();
            return item;
        }
        return new Menu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "_Datei",
                    Items =
                    {
                        Item("PDF _öffnen …", "Ctrl+O", async () => await OpenWithDialog()),
                        Item("Annotationen _speichern", "Ctrl+S", () => SaveAnnotations()),
                        Item("Als PDF _exportieren …", "Ctrl+E", async () => await ExportPdf()),
                        new Separator(),
                        Item("_Beenden", "Ctrl+Q", Close),
                    },
                },
                new MenuItem
                {
                    Header = "_Ansicht",
                    Items =
                    {
                        Item("Stift-Panel", "Tab", () => _picker.IsVisible = !_picker.IsVisible),
                        Item("Kopfbereich ein/aus", "F2", ToggleHeader),
                        Item("Messanzeige", "F1", _view.ToggleHud),
                        Item("Vollbild", "F11", ToggleFullscreen),
                    },
                },
            },
        };
    }

    private void ShowNotice(string text)
    {
        _noticeTimer?.Cancel();
        _noticeTimer = new CancellationTokenSource();
        var token = _noticeTimer.Token;
        _notice.Text = text;
        _ = Task.Delay(5000, token).ContinueWith(_ => Dispatcher.UIThread.Post(() => _notice.Text = ""),
            token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void ToggleHeader()
    {
        _header.IsVisible = !_header.IsVisible;
        if (_options.Canvas is { } canvas && WindowState != WindowState.FullScreen) SizeForCanvas(canvas);
    }

    /// <summary>Speichert in die Begleitdatei neben dem PDF. Liefert false bei Fehler.</summary>
    private bool SaveAnnotations()
    {
        if (_view.CurrentPath is not { } pdf) return false;
        try
        {
            AnnotationFile.Save(pdf, _view.Ink);
            _view.MarkSaved();
            ShowNotice($"Gespeichert: {Path.GetFileName(AnnotationFile.PathFor(pdf))}");
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice($"Speichern fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    private async Task ExportPdf()
    {
        if (_view.CurrentPath is not { } pdf || _view.Document is not { } doc) return;
        var folder = await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(pdf)!);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Als PDF exportieren",
            SuggestedFileName = Path.GetFileNameWithoutExtension(pdf) + "-annotiert.pdf",
            SuggestedStartLocation = folder,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (file?.TryGetLocalPath() is not { } target) return;
        if (Path.GetFullPath(target) == Path.GetFullPath(pdf))
        {
            ShowNotice("Bitte nicht über das geöffnete PDF exportieren.");
            return;
        }
        try
        {
            await doc.ExportAsync(target, _view.Ink);
            ShowNotice($"Exportiert: {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            ShowNotice($"Export fehlgeschlagen: {ex.Message}");
        }
    }

    /// <summary>Bei ungespeicherten Annotationen nachfragen. false = Vorgang abbrechen.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_view.IsDirty) return true;
        var choice = await ConfirmDialog.AskAsync(this,
            $"Die Annotationen in „{Path.GetFileName(_view.CurrentPath)}“ wurden geändert. Speichern?");
        return choice switch
        {
            SaveChoice.Save => SaveAnnotations(),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    private async void OnClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || !_view.IsDirty) return;
        e.Cancel = true;
        if (await ConfirmDiscardAsync())
        {
            _closeConfirmed = true;
            Close();
        }
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
        // Eingaben im Hex-Feld und Navigation im Menü nicht als Tastenkürzel auswerten
        if (e.Source is TextBox && e.Key is not (Key.Escape or Key.Tab)) return;
        if (e.Source is MenuItem) return;

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
                ToggleHeader();
                break;
            case Key.S when ctrl:
                SaveAnnotations();
                break;
            case Key.E when ctrl:
                await ExportPdf();
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
        if (!await ConfirmDiscardAsync()) return;
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
        if (file?.TryGetLocalPath() is { } path && await ConfirmDiscardAsync()) await _view.OpenAsync(path);
    }
}
