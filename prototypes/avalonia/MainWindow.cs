using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace PdfPresenter;

public sealed class MainWindow : Window
{
    private readonly PresenterView _view;
    private WindowState _stateBeforeFullscreen = WindowState.Normal;

    public MainWindow(Options options)
    {
        Width = 1280;
        Height = 720;
        Background = Brushes.Black;
        _view = new PresenterView(options);
        Content = _view;
        Title = _view.Title;
        _view.StatusChanged += () => Title = _view.Title;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);

        if (options.Fullscreen) WindowState = WindowState.FullScreen;
        Opened += async (_, _) =>
        {
            _view.Focus();
            if (options.PdfPath is { } path) await _view.OpenAsync(path);
        };
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
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
            default:
                if (!_view.HandleKey(e)) base.OnKeyDown(e);
                return;
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
