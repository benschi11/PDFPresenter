using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace PdfPresenter;

/// <summary>
/// Zeigt eine PDF-Seite randlos an, blendet beim Blättern über und nimmt Stiftstriche entgegen.
/// Gezeichnet wird in drei Schichten: PDF-Bitmap → gecachte Ink-Ebene (fertige Striche) →
/// aktiver Strich (wird jeden Frame neu gezeichnet).
/// </summary>
public sealed class PresenterView : Control
{
    private static readonly Color[] PenColors =
    [
        Color.FromRgb(0x10, 0x10, 0x10), // 1 schwarz
        Color.FromRgb(0x1f, 0x5f, 0xd6), // 2 blau
        Color.FromRgb(0xd6, 0x28, 0x28), // 3 rot
        Color.FromRgb(0x1f, 0x9d, 0x4a), // 4 grün
    ];
    private static readonly string[] PenColorNames = ["schwarz", "blau", "rot", "grün"];
    private static readonly Color HighlighterColor = Color.FromArgb(0x60, 0xff, 0xe0, 0x00);
    private const double PenWidth = 0.0035;        // Anteil der Seitenhöhe (≈ 3,8 px bei 1080p)
    private const double HighlighterWidth = 0.025;
    private const double EraserRadius = 0.012;
    private const int PalmRejectMs = 1000;         // Touch wird so lange nach Stiftkontakt ignoriert

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Options _options;
    private PdfDocument? _doc;
    private string? _error;

    // Seiten-Bitmaps (exakt in Zielpixelgröße) und laufende Render-Aufträge
    private readonly Dictionary<int, Bitmap> _pageBitmaps = new();
    private readonly HashSet<(int Page, PixelSize Size)> _inFlight = new();
    private PixelSize _targetSize;

    // Annotationen pro Seite + gecachte Ink-Ebenen (nur für sichtbare Seiten)
    private readonly Dictionary<int, List<Stroke>> _ink = new();
    private readonly Dictionary<int, RenderTargetBitmap> _inkLayers = new();

    // Navigation / Überblendung
    private int _page;
    private int _fromPage = -1;
    private double _fadeStartMs = double.NaN;
    private bool _fadePending;
    private int _fadeMs;

    // Eingabe
    private Tool _tool = Tool.Pen;
    private int _colorIndex;
    private Stroke? _active;
    private int? _activePointerId;
    private PointerType _activePointerType;
    private bool _erasing;
    private Point? _eraserPos;
    private bool _touchDraws;
    private double _lastPenSeenMs = double.NegativeInfinity;

    // Messwerte
    private bool _showHud = true;
    private readonly bool _continuous;
    private bool _frameLoopRunning;
    private double _lastFrameMs = double.NaN;
    private readonly RollingStats _frameTimes = new();
    private readonly RollingStats _inputLatency = new();
    private readonly RollingStats _penEvents = new();
    private double _pendingInputMs = double.NaN;
    private float _lastPressure;
    private PointerType _lastPointerType = PointerType.Mouse;

    public event Action? StatusChanged;

    public PresenterView(Options options)
    {
        _options = options;
        _fadeMs = options.FadeMs;
        _touchDraws = options.TouchDraws;
        _continuous = options.Continuous;
        ClipToBounds = true;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public string Title => _doc is null
        ? "PDF-Presenter (Avalonia)"
        : $"Seite {_page + 1}/{_doc.PageCount} – PDF-Presenter (Avalonia)";

    public async Task OpenAsync(string path)
    {
        try
        {
            _doc?.Dispose();
            _doc = await PdfDocument.OpenAsync(path);
            _pageBitmaps.Clear();
            _ink.Clear();
            ClearInkLayers();
            _page = 0;
            _fromPage = -1;
            _error = null;
            RequestPages();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        StatusChanged?.Invoke();
        InvalidateVisual();
    }

    // ───────────────────────────── Navigation ─────────────────────────────

    public void GoTo(int page)
    {
        if (_doc is null) return;
        page = Math.Clamp(page, 0, _doc.PageCount - 1);
        if (page == _page) return;
        CommitActiveStroke();

        _fromPage = _page;
        _page = page;
        if (_pageBitmaps.ContainsKey(page)) StartFade();
        else _fadePending = true; // Überblendung startet, sobald die Seite gerendert ist

        RequestPages();
        StatusChanged?.Invoke();
        InvalidateVisual();
    }

    public void Next() => GoTo(_page + 1);
    public void Previous() => GoTo(_page - 1);

    private void StartFade()
    {
        _fadePending = false;
        _fadeStartMs = _clock.Elapsed.TotalMilliseconds;
        EnsureFrameLoop();
    }

    /// <summary>Überblend-Fortschritt 0..1, oder null wenn keine Überblendung läuft.</summary>
    private double? FadeProgress(double nowMs)
    {
        if (_fromPage < 0 || _fadePending) return _fadePending ? 0 : null;
        if (_fadeMs <= 0) { _fromPage = -1; return null; }
        double t = (nowMs - _fadeStartMs) / _fadeMs;
        if (t >= 1) { _fromPage = -1; return null; }
        return t * t * (3 - 2 * t); // smoothstep
    }

    // ─────────────────────────────── Tastatur ──────────────────────────────

    public bool HandleKey(KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Space or Key.Right or Key.Down or Key.PageDown or Key.N: Next(); break;
            case Key.Left or Key.Up or Key.PageUp or Key.Back or Key.P: Previous(); break;
            case Key.Home: GoTo(0); break;
            case Key.End: GoTo(int.MaxValue); break;
            case Key.D1 or Key.D2 or Key.D3 or Key.D4:
                _tool = Tool.Pen;
                _colorIndex = e.Key - Key.D1;
                break;
            case Key.H: _tool = Tool.Highlighter; break;
            case Key.E: _tool = Tool.Eraser; break;
            case Key.C: ClearPage(); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.T: _touchDraws = !_touchDraws; break;
            case Key.F1: _showHud = !_showHud; break;
            case Key.OemPlus or Key.Add: _fadeMs = Math.Min(_fadeMs + 50, 3000); break;
            case Key.OemMinus or Key.Subtract: _fadeMs = Math.Max(_fadeMs - 50, 0); break;
            default: return false;
        }
        StatusChanged?.Invoke();
        InvalidateVisual();
        return true;
    }

    private List<Stroke> StrokesOf(int page)
    {
        if (!_ink.TryGetValue(page, out var list)) _ink[page] = list = [];
        return list;
    }

    private void ClearPage()
    {
        StrokesOf(_page).Clear();
        InvalidateInkLayer(_page);
    }

    private void Undo()
    {
        var list = StrokesOf(_page);
        if (list.Count == 0) return;
        list.RemoveAt(list.Count - 1);
        InvalidateInkLayer(_page);
    }

    // ─────────────────────────────── Zeiger ────────────────────────────────

    /// <summary>Handballenerkennung: Finger nur, wenn erlaubt und der Stift nicht gerade in der Nähe ist.</summary>
    private bool Accept(PointerType type, double nowMs) => type switch
    {
        PointerType.Pen => true,
        PointerType.Touch => _touchDraws && nowMs - _lastPenSeenMs > PalmRejectMs,
        _ => true,
    };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        double now = _clock.Elapsed.TotalMilliseconds;
        var pt = e.GetCurrentPoint(this);
        NotePointer(e.Pointer.Type, pt.Properties.Pressure, now);

        if (_doc is null || !Accept(e.Pointer.Type, now)) return;

        // Stift setzt auf, während ein Finger-/Handballenstrich läuft → den verwerfen.
        if (e.Pointer.Type == PointerType.Pen && _activePointerType == PointerType.Touch && _active is not null)
        {
            _active = null;
            _activePointerId = null;
        }
        if (_activePointerId is not null) return;

        _activePointerId = e.Pointer.Id;
        _activePointerType = e.Pointer.Type;
        _erasing = _tool == Tool.Eraser
                   || pt.Properties.IsEraser
                   || pt.Properties.IsRightButtonPressed
                   || pt.Properties.IsBarrelButtonPressed;
        e.Pointer.Capture(this);

        if (_erasing)
        {
            EraseAt(pt.Position);
        }
        else
        {
            bool hl = _tool == Tool.Highlighter;
            _active = new Stroke(hl ? HighlighterColor : PenColors[_colorIndex], hl ? HighlighterWidth : PenWidth, hl);
            AddPoint(pt);
        }
        _pendingInputMs = double.IsNaN(_pendingInputMs) ? now : _pendingInputMs;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        double now = _clock.Elapsed.TotalMilliseconds;
        var current = e.GetCurrentPoint(this);
        NotePointer(e.Pointer.Type, current.Properties.Pressure, now);
        if (e.Pointer.Type == PointerType.Pen && _tool != Tool.Eraser && !current.Properties.IsEraser)
            _eraserPos = null;

        if (_activePointerId != e.Pointer.Id) return;

        // Zwischenpunkte: Windows liefert Stiftdaten mit höherer Rate als Frames.
        foreach (var pt in e.GetIntermediatePoints(this))
        {
            _penEvents.Add(now, 1);
            if (_erasing) EraseAt(pt.Position);
            else AddPoint(pt);
        }
        _pendingInputMs = double.IsNaN(_pendingInputMs) ? now : _pendingInputMs;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_activePointerId != e.Pointer.Id) return;
        CommitActiveStroke();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_activePointerId == e.Pointer.Id) CommitActiveStroke();
    }

    private void NotePointer(PointerType type, float pressure, double now)
    {
        _lastPointerType = type;
        _lastPressure = pressure;
        if (type == PointerType.Pen) _lastPenSeenMs = now;
    }

    private void AddPoint(PointerPoint pt)
    {
        if (_active is null || _doc is null) return;
        var p = ScreenToPage(pt.Position);
        float pressure = pt.Pointer.Type == PointerType.Mouse ? 0.5f : pt.Properties.Pressure;
        _active.Points.Add(new InkPoint(p.X, p.Y, pressure));
    }

    private void CommitActiveStroke()
    {
        var stroke = _active;
        _active = null;
        _activePointerId = null;
        _erasing = false;
        if (stroke is null || stroke.Points.Count == 0) return;
        StrokesOf(_page).Add(stroke);
        // inkrementell in die vorhandene Ink-Ebene zeichnen statt alles neu aufzubauen
        if (_inkLayers.TryGetValue(_page, out var layer))
        {
            using var ctx = layer.CreateDrawingContext(false);
            stroke.Draw(ctx, Matrix.CreateScale(layer.PixelSize.Height, layer.PixelSize.Height));
        }
        InvalidateVisual();
    }

    private void EraseAt(Point screen)
    {
        _eraserPos = screen;
        var p = ScreenToPage(screen);
        int removed = StrokesOf(_page).RemoveAll(s => s.HitTest(p, EraserRadius));
        if (removed > 0) InvalidateInkLayer(_page);
    }

    // ───────────────────────────── Geometrie ───────────────────────────────

    private double Scaling => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>
    /// Zielrechteck der Seite in physischen Pixeln. Passt das Seitenverhältnis (±1 %),
    /// wird die Fläche komplett gefüllt – randlos. Sonst wird eingepasst und zentriert.
    /// </summary>
    private PixelRect DestPixels(int page)
    {
        var s = Scaling;
        int vw = Math.Max(1, (int)Math.Round(Bounds.Width * s));
        int vh = Math.Max(1, (int)Math.Round(Bounds.Height * s));
        if (_doc is null) return new PixelRect(0, 0, vw, vh);

        var ps = _doc.PageSizes[page];
        double pageAspect = ps.Width / ps.Height, viewAspect = (double)vw / vh;
        if (Math.Abs(pageAspect - viewAspect) / pageAspect < 0.01) return new PixelRect(0, 0, vw, vh);
        if (pageAspect > viewAspect)
        {
            int h = (int)Math.Round(vw / pageAspect);
            return new PixelRect(0, (vh - h) / 2, vw, h);
        }
        int w = (int)Math.Round(vh * pageAspect);
        return new PixelRect((vw - w) / 2, 0, w, vh);
    }

    private Rect DestDip(int page)
    {
        var r = DestPixels(page);
        var s = Scaling;
        return new Rect(r.X / s, r.Y / s, r.Width / s, r.Height / s);
    }

    private Point ScreenToPage(Point screen)
    {
        var d = DestDip(_page);
        return new Point((screen.X - d.X) / d.Height, (screen.Y - d.Y) / d.Height);
    }

    private Matrix PageToScreen(int page)
    {
        var d = DestDip(page);
        return Matrix.CreateScale(d.Height, d.Height) * Matrix.CreateTranslation(d.X, d.Y);
    }

    // ───────────────────────────── PDF-Seiten ──────────────────────────────

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ClearInkLayers();
        RequestPages();
    }

    /// <summary>Aktuelle Seite sofort, Nachbarseiten vorausschauend rendern.</summary>
    private void RequestPages()
    {
        if (_doc is null || Bounds.Width <= 0) return;
        _targetSize = DestPixels(_page).Size;
        foreach (var p in new[] { _page, _page + 1, _page - 1 })
            if (p >= 0 && p < _doc.PageCount) RequestPage(p);

        // Cache klein halten: nur Seiten in der Nähe behalten
        foreach (var p in _pageBitmaps.Keys.Where(p => Math.Abs(p - _page) > 2 && p != _fromPage).ToList())
        {
            _pageBitmaps[p].Dispose();
            _pageBitmaps.Remove(p);
        }
        foreach (var p in _inkLayers.Keys.Where(p => p != _page && p != _fromPage).ToList())
        {
            _inkLayers[p].Dispose();
            _inkLayers.Remove(p);
        }
    }

    private async void RequestPage(int page)
    {
        var doc = _doc!;
        var size = DestPixels(page).Size;
        if (_pageBitmaps.TryGetValue(page, out var existing) && existing.PixelSize == size) return;
        if (!_inFlight.Add((page, size))) return;
        try
        {
            // Auftrag verwerfen, falls sich die Zielgröße inzwischen geändert hat.
            var bmp = await doc.RenderAsync(page, size, () => size == _targetSize || Math.Abs(page - _page) <= 1);
            if (bmp is null || doc != _doc) { bmp?.Dispose(); return; }
            if (_pageBitmaps.Remove(page, out var old)) old.Dispose();
            _pageBitmaps[page] = bmp;
            if (_fadePending && page == _page) StartFade();
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _inFlight.Remove((page, size));
        }
    }

    private void ClearInkLayers()
    {
        foreach (var l in _inkLayers.Values) l.Dispose();
        _inkLayers.Clear();
    }

    private void InvalidateInkLayer(int page)
    {
        if (_inkLayers.Remove(page, out var l)) l.Dispose();
        InvalidateVisual();
    }

    private RenderTargetBitmap? InkLayer(int page)
    {
        if (!_ink.TryGetValue(page, out var strokes) || strokes.Count == 0) return null;
        var size = DestPixels(page).Size;
        if (_inkLayers.TryGetValue(page, out var layer) && layer.PixelSize == size) return layer;
        layer?.Dispose();

        layer = new RenderTargetBitmap(size, new Vector(96, 96));
        using (var ctx = layer.CreateDrawingContext(true))
        {
            var m = Matrix.CreateScale(size.Height, size.Height);
            foreach (var s in strokes) s.Draw(ctx, m);
        }
        _inkLayers[page] = layer;
        return layer;
    }

    // ────────────────────────────── Rendern ────────────────────────────────

    private void EnsureFrameLoop()
    {
        if (_frameLoopRunning) return;
        var top = TopLevel.GetTopLevel(this);
        if (top is null) return;
        _frameLoopRunning = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        InvalidateVisual();
        bool keepGoing = _continuous || _fromPage >= 0;
        var top = TopLevel.GetTopLevel(this);
        if (keepGoing && top is not null) top.RequestAnimationFrame(OnFrame);
        else _frameLoopRunning = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_continuous) Dispatcher.UIThread.Post(EnsureFrameLoop);
    }

    public override void Render(DrawingContext ctx)
    {
        double now = _clock.Elapsed.TotalMilliseconds;
        // Frame-Intervalle nur messen, solange die Frame-Schleife läuft (Überblendung oder
        // --continuous); sonst wird nur bei Bedarf gerendert und die Abstände sagen nichts aus.
        if (_frameLoopRunning && !double.IsNaN(_lastFrameMs)) _frameTimes.Add(now, now - _lastFrameMs);
        _lastFrameMs = _frameLoopRunning ? now : double.NaN;
        if (!double.IsNaN(_pendingInputMs))
        {
            _inputLatency.Add(now, now - _pendingInputMs);
            _pendingInputMs = double.NaN;
        }

        ctx.FillRectangle(Brushes.Black, new Rect(Bounds.Size));

        if (_doc is null)
        {
            DrawText(ctx, _error ?? "PDF per Drag & Drop, Strg+O oder als Argument öffnen.", new Point(40, 40), 22);
            return;
        }

        var fade = FadeProgress(now);
        if (fade is { } t && _fromPage >= 0)
        {
            DrawPage(ctx, _fromPage);
            using (ctx.PushOpacity(t)) DrawPage(ctx, _page);
            if (_fromPage >= 0) EnsureFrameLoop();
        }
        else
        {
            DrawPage(ctx, _page);
        }

        if (_active is not null)
        {
            using (ctx.PushClip(DestDip(_page))) _active.Draw(ctx, PageToScreen(_page));
        }
        if (_eraserPos is { } ep && (_erasing || _tool == Tool.Eraser))
        {
            double r = EraserRadius * DestDip(_page).Height;
            ctx.DrawEllipse(null, new Pen(Brushes.Gray, 1), ep, r, r);
        }

        if (_showHud) DrawHud(ctx, now);
    }

    private void DrawPage(DrawingContext ctx, int page)
    {
        var dest = DestDip(page);
        if (_pageBitmaps.TryGetValue(page, out var bmp))
            ctx.DrawImage(bmp, new Rect(bmp.Size), dest);
        else
            ctx.FillRectangle(Brushes.White, dest);

        if (InkLayer(page) is { } layer)
            ctx.DrawImage(layer, new Rect(layer.Size), dest);
    }

    private void DrawHud(DrawingContext ctx, double now)
    {
        var px = DestPixels(_page);
        string tool = _tool switch
        {
            Tool.Pen => $"Stift ({PenColorNames[_colorIndex]})",
            Tool.Highlighter => "Textmarker",
            _ => "Radierer",
        };
        var inv = CultureInfo.InvariantCulture;
        string text = string.Join('\n',
            $"Seite {_page + 1}/{_doc!.PageCount} · {tool} · Überblendung {_fadeMs} ms · Touch zeichnet: {(_touchDraws ? "ja" : "nein")}",
            string.Format(inv, "Frame Ø {0:F1} ms · max {1:F1} ms · {2:F0} fps {3}",
                _frameTimes.Average(now), _frameTimes.Max(now), _frameTimes.Count(now),
                _continuous ? "(kontinuierlich)" : "(nur während Überblendung gemessen)"),
            string.Format(inv, "Zeiger: {0} · Druck {1:F2} · {2:F0} Punkte/s · Eingabe→Render Ø {3:F1} ms · max {4:F1} ms",
                _lastPointerType, _lastPressure, _penEvents.Sum(now), _inputLatency.Average(now), _inputLatency.Max(now)),
            string.Format(inv, "Seite in Pixeln: {0}×{1} · Skalierung {2:F2} · F1 blendet aus", px.Width, px.Height, Scaling));
        var ft = MakeText(text, 13);
        var origin = new Point(12, 12);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(0xb0, 0, 0, 0)), new Rect(origin, new Size(ft.Width + 16, ft.Height + 12)));
        ctx.DrawText(ft, origin + new Vector(8, 6));
    }

    private static FormattedText MakeText(string text, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, Brushes.White);

    private static void DrawText(DrawingContext ctx, string text, Point at, double size) =>
        ctx.DrawText(MakeText(text, size), at);
}

/// <summary>Messwerte der letzten Sekunde (gleitendes Fenster).</summary>
internal sealed class RollingStats
{
    private readonly Queue<(double T, double V)> _samples = new();

    public void Add(double nowMs, double value)
    {
        _samples.Enqueue((nowMs, value));
        Trim(nowMs);
    }

    private void Trim(double nowMs)
    {
        while (_samples.Count > 0 && nowMs - _samples.Peek().T > 1000) _samples.Dequeue();
    }

    public double Average(double now) { Trim(now); return _samples.Count == 0 ? 0 : _samples.Average(s => s.V); }
    public double Max(double now) { Trim(now); return _samples.Count == 0 ? 0 : _samples.Max(s => s.V); }
    public double Sum(double now) { Trim(now); return _samples.Sum(s => s.V); }
    public int Count(double now) { Trim(now); return _samples.Count; }
}
