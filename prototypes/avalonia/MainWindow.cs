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
    private readonly AppSettings _settings = AppSettings.Load();
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
        _view.DefaultPaper = _settings.Paper;
        _view.GeoScale = _settings.GeoScale;
        _view.GeoScaleLocked = _settings.GeoScaleLocked;
        _view.GeoSnap = _settings.GeoSnap;
        _view.GeoRuler = _settings.GeoRuler;
        _view.GeoAngleTool = _settings.GeoAngleTool;
        _view.SwipeToPage = _settings.SwipeToPage;
        if (_settings.GeoPose is [var gx, var gy, var ga])
            _view.RestoreGeo(new GeoPose(new Point(gx, gy), ga, _settings.GeoScale), _settings.GeoVisible);
        _view.GeoPoseChanged += SaveGeoPose;
        _view.StatusChanged += SaveGeoSettings;
        _bar.GeoClicked += () => _view.GeoVisible = !_view.GeoVisible;
        _bar.AddPageClicked += () => _view.InsertPaperPage();

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
            if (options.ExportTo is { } target && _view.Deck is { } deck)
            {
                try
                {
                    await PdfExporter.ExportAsync(deck, target);
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
                        Item("_Neues Blatt", "Ctrl+N", async () => await NewBlankAsync()),
                        Item("_Öffnen …", "Ctrl+O", async () => await OpenWithDialog()),
                        Item("_Speichern", "Ctrl+S", async () => await SaveAsync()),
                        Item("Als PDF _exportieren …", "Ctrl+E", async () => await ExportPdf()),
                        new Separator(),
                        Item("_Beenden", "Ctrl+Q", Close),
                    },
                },
                BuildPageMenu(),
                new MenuItem
                {
                    Header = "_Ansicht",
                    Items =
                    {
                        Item("Stift-Panel", "Tab", () => _picker.IsVisible = !_picker.IsVisible),
                        BuildGeoMenu(),
                        Item("Kopfbereich ein/aus", "F2", ToggleHeader),
                        BuildSwipeItem(),
                        Item("Messanzeige", "F1", _view.ToggleHud),
                        Item("Vollbild", "F11", ToggleFullscreen),
                    },
                },
            },
        };
    }

    private MenuItem BuildSwipeItem()
    {
        var item = new MenuItem { Header = "Wischen zum Blättern (1 Finger)", ToggleType = MenuItemToggleType.CheckBox };
        // Häkchen beim Aufklappen aktualisieren (Menü entsteht vor dem Laden der Einstellungen)
        item.AttachedToVisualTree += (_, _) => item.IsChecked = _view.SwipeToPage;
        item.Click += (_, _) =>
        {
            _view.SwipeToPage = !_view.SwipeToPage;
            item.IsChecked = _view.SwipeToPage;
        };
        return item;
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

    /// <summary>
    /// Menü „Seite“: Blatt einfügen/löschen, Hintergrund, Abstand und Format. Die Auswahl gilt für
    /// die aktuelle Papierseite und als Vorgabe für neue Blätter.
    /// </summary>
    private MenuItem BuildPageMenu()
    {
        var menu = new MenuItem { Header = "_Seite" };
        var insert = new MenuItem { Header = "Leeres Blatt _einfügen", InputGesture = KeyGesture.Parse("Ctrl+Shift+N") };
        insert.Click += (_, _) => _view.InsertPaperPage();
        var delete = new MenuItem { Header = "Seite _löschen" };
        delete.Click += async (_, _) => await DeletePageAsync();

        var radios = new List<(MenuItem Item, Func<Paper, bool> IsChecked)>();
        MenuItem Radio(string header, string group, Func<Paper, Paper> apply, Func<Paper, bool> isChecked)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, GroupName = group };
            item.Click += (_, _) => ApplyPaper(apply(CurrentPaper()));
            radios.Add((item, isChecked));
            return item;
        }
        MenuItem Sub(string header, params MenuItem[] items)
        {
            var sub = new MenuItem { Header = header };
            foreach (var i in items) sub.Items.Add(i);
            return sub;
        }

        var styles = new[] { (PaperStyle.Blank, "Blanko"), (PaperStyle.Grid, "Kariert"), (PaperStyle.Lined, "Liniert"), (PaperStyle.Dotted, "Punktiert") }
            .Select(t => Radio(t.Item2, "style",
                p => p with { Style = t.Item1, SpacingMm = p.Style == t.Item1 ? p.SpacingMm : Paper.DefaultSpacing(t.Item1) },
                p => p.Style == t.Item1))
            .ToArray();
        var spacings = new[] { 4.0, 5, 6, 8, 10 }
            .Select(mm => Radio($"{mm:0} mm", "spacing", p => p with { SpacingMm = mm }, p => Math.Abs(p.SpacingMm - mm) < 0.01))
            .ToArray();
        var formats = new[] { (PageFormat.Wide16x9, "16:9 (Folie)"), (PageFormat.A4Portrait, "A4 hoch"), (PageFormat.A4Landscape, "A4 quer") }
            .Select(t => Radio(t.Item2, "format", p => p with { Format = t.Item1 }, p => p.Format == t.Item1))
            .ToArray();

        menu.Items.Add(insert);
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        menu.Items.Add(Sub("_Hintergrund", styles));
        menu.Items.Add(Sub("_Abstand", spacings));
        menu.Items.Add(Sub("_Format", formats));
        // Häkchen beim Öffnen an die aktuelle Seite bzw. Vorgabe anpassen
        menu.SubmenuOpened += (_, _) =>
        {
            var paper = CurrentPaper();
            foreach (var (item, isChecked) in radios) item.IsChecked = isChecked(paper);
            delete.IsEnabled = _view.Deck is { Pages.Count: > 1 };
        };
        return menu;
    }

    private Paper CurrentPaper() => _view.CurrentPage is PaperPage p ? p.Paper : _view.DefaultPaper;

    private void ApplyPaper(Paper paper)
    {
        _settings.Paper = paper;
        _settings.Save();
        if (_view.CurrentPage is PaperPage) _view.SetCurrentPaper(paper);
        else
        {
            _view.DefaultPaper = paper;
            ShowNotice($"Neue Blätter: {paper.Describe()}");
        }
    }

    private async Task NewBlankAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        _view.NewBlank(_settings.Paper);
    }

    private async Task DeletePageAsync()
    {
        if (_view.CurrentPage is not { } page) return;
        if (page.Strokes.Count > 0 &&
            await ConfirmDialog.AskYesNoAsync(this, "Diese Seite enthält Annotationen. Trotzdem löschen?") != true)
            return;
        if (!_view.DeleteCurrentPage()) ShowNotice("Die letzte Seite kann nicht gelöscht werden.");
        else if (page is PdfPage) ShowNotice("Seite aus der Präsentation entfernt (das PDF selbst bleibt unverändert).");
    }

    /// <summary>
    /// Speichert das Dokument: mit PDF in die Begleitdatei, ohne PDF als .pdfp (beim ersten Mal
    /// mit Dateiauswahl). Liefert false bei Fehler oder Abbruch.
    /// </summary>
    private async Task<bool> SaveAsync()
    {
        if (_view.Deck is not { } deck) return false;
        if (deck.PdfPath is null && deck.FilePath is null)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Tafel speichern",
                SuggestedFileName = "Tafel" + DocumentFile.Extension,
                DefaultExtension = DocumentFile.Extension.TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType("PDF-Presenter-Tafel") { Patterns = ["*" + DocumentFile.Extension] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return false;
            deck.FilePath = path;
        }
        try
        {
            var written = DocumentFile.Save(deck);
            _view.MarkSaved();
            ShowNotice($"Gespeichert: {Path.GetFileName(written)}");
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
        if (_view.Deck is not { } deck) return;
        var source = deck.PdfPath ?? deck.FilePath;
        var folder = source is null ? null : await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(source)!);
        var baseName = source is null ? "Tafel" : Path.GetFileNameWithoutExtension(source);
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Als PDF exportieren",
            SuggestedFileName = baseName + (deck.Pdf is null ? ".pdf" : "-annotiert.pdf"),
            SuggestedStartLocation = folder,
            DefaultExtension = "pdf",
            FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
        });
        if (file?.TryGetLocalPath() is not { } target) return;
        if (deck.PdfPath is { } pdf && Path.GetFullPath(target) == Path.GetFullPath(pdf))
        {
            ShowNotice("Bitte nicht über das geöffnete PDF exportieren.");
            return;
        }
        try
        {
            await PdfExporter.ExportAsync(deck, target);
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
            $"„{_view.Deck?.Name}“ wurde geändert. Speichern?");
        return choice switch
        {
            SaveChoice.Save => await SaveAsync(),
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

    private void RefreshBar() => _bar.Refresh(_picker.Presets, _picker.Current, _view.Tool, _view.PageStatus, _view.GeoVisible);

    /// <summary>Größe, Sperre und Einrasten des Geodreiecks merken (nur bei Änderung schreiben).</summary>
    private void SaveGeoSettings()
    {
        double scale = Math.Round(_view.GeoScale, 3);
        if (scale == _settings.GeoScale && _view.GeoScaleLocked == _settings.GeoScaleLocked
            && _view.GeoSnap == _settings.GeoSnap && _view.GeoRuler == _settings.GeoRuler
            && _view.GeoAngleTool == _settings.GeoAngleTool && _view.SwipeToPage == _settings.SwipeToPage)
            return;
        _settings.SwipeToPage = _view.SwipeToPage;
        _settings.GeoAngleTool = _view.GeoAngleTool;
        _settings.GeoRuler = _view.GeoRuler;
        _settings.GeoScale = scale;
        _settings.GeoScaleLocked = _view.GeoScaleLocked;
        _settings.GeoSnap = _view.GeoSnap;
        _settings.Save();
    }

    /// <summary>Lage und Sichtbarkeit des Geodreiecks merken (nach jeder abgeschlossenen Geste).</summary>
    private void SaveGeoPose()
    {
        _settings.GeoVisible = _view.GeoVisible;
        if (_view.GeoPlacement is { } p)
            _settings.GeoPose = [Math.Round(p.Center.X, 5), Math.Round(p.Center.Y, 5), Math.Round(p.AngleDeg, 3)];
        _settings.GeoScale = Math.Round(_view.GeoScale, 3);
        _settings.Save();
    }

    private MenuItem BuildGeoMenu()
    {
        var show = new MenuItem { Header = "_Anzeigen", ToggleType = MenuItemToggleType.CheckBox, InputGesture = KeyGesture.Parse("G") };
        show.Click += (_, _) => _view.GeoVisible = !_view.GeoVisible;
        var locked = new MenuItem { Header = "Größe _sperren (kein Pinch)", ToggleType = MenuItemToggleType.CheckBox };
        locked.Click += (_, _) => _view.GeoScaleLocked = !_view.GeoScaleLocked;
        var snap = new MenuItem { Header = "Bei 0°/45°/90° _einrasten", ToggleType = MenuItemToggleType.CheckBox };
        snap.Click += (_, _) => _view.GeoSnap = !_view.GeoSnap;
        var ruler = new MenuItem { Header = "Am _Lineal entlang zeichnen", ToggleType = MenuItemToggleType.CheckBox };
        ruler.Click += (_, _) => _view.GeoRuler = !_view.GeoRuler;
        var angleTool = new MenuItem { Header = "_Winkel antragen (am Nullpunkt ansetzen)", ToggleType = MenuItemToggleType.CheckBox };
        angleTool.Click += (_, _) => _view.GeoAngleTool = !_view.GeoAngleTool;
        var level = new MenuItem { Header = "Auf 0° aus_richten" };
        level.Click += (_, _) => _view.ResetGeoAngle();

        var size = new MenuItem { Header = "_Größe" };
        var sizes = new List<(MenuItem Item, double Scale)>();
        foreach (var pct in new[] { 50, 75, 100, 125, 150 })
        {
            var item = new MenuItem { Header = pct == 100 ? "100 % (Original, 16 cm)" : $"{pct} %", ToggleType = MenuItemToggleType.Radio, GroupName = "geo-size" };
            item.Click += (_, _) => _view.GeoScale = pct / 100.0;
            sizes.Add((item, pct / 100.0));
            size.Items.Add(item);
        }

        var menu = new MenuItem { Header = "_Geodreieck", Items = { show, new Separator(), size, locked, snap, ruler, angleTool, level } };
        menu.SubmenuOpened += (_, _) =>
        {
            show.IsChecked = _view.GeoVisible;
            locked.IsChecked = _view.GeoScaleLocked;
            snap.IsChecked = _view.GeoSnap;
            ruler.IsChecked = _view.GeoRuler;
            angleTool.IsChecked = _view.GeoAngleTool;
            foreach (var (item, scale) in sizes) item.IsChecked = Math.Abs(_view.GeoScale - scale) < 0.005;
        };
        return menu;
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
                await SaveAsync();
                break;
            case Key.N when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                _view.InsertPaperPage();
                break;
            case Key.N when ctrl:
                await NewBlankAsync();
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
            Title = "Öffnen",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("PDF oder Tafel") { Patterns = ["*.pdf", "*" + DocumentFile.Extension] },
                new FilePickerFileType("PDF") { Patterns = ["*.pdf"] },
                new FilePickerFileType("PDF-Presenter-Tafel") { Patterns = ["*" + DocumentFile.Extension] },
            ],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await _view.OpenAsync(path);
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var file = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (file?.TryGetLocalPath() is { } path && await ConfirmDiscardAsync()) await _view.OpenAsync(path);
    }
}
