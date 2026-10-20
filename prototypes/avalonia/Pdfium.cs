using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PdfPresenter;

/// <summary>
/// Minimale P/Invoke-Bindings auf PDFium. Die nativen Bibliotheken kommen aus den
/// NuGet-Paketen bblanchon.PDFium.Win32 / .Linux.
/// </summary>
internal static partial class Native
{
    private const string Lib = "pdfium";

    [LibraryImport(Lib)] public static partial void FPDF_InitLibrary();
    [LibraryImport(Lib)] public static partial nint FPDF_LoadMemDocument(nint data, int size, nint password);
    [LibraryImport(Lib)] public static partial void FPDF_CloseDocument(nint doc);
    [LibraryImport(Lib)] public static partial uint FPDF_GetLastError();
    [LibraryImport(Lib)] public static partial int FPDF_GetPageCount(nint doc);
    [LibraryImport(Lib)] public static partial nint FPDF_LoadPage(nint doc, int index);
    [LibraryImport(Lib)] public static partial void FPDF_ClosePage(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageWidthF(nint page);
    [LibraryImport(Lib)] public static partial float FPDF_GetPageHeightF(nint page);
    [LibraryImport(Lib)] public static partial nint FPDFBitmap_CreateEx(int w, int h, int format, nint buffer, int stride);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_FillRect(nint bmp, int l, int t, int w, int h, uint color);
    [LibraryImport(Lib)] public static partial void FPDFBitmap_Destroy(nint bmp);
    [LibraryImport(Lib)]
    public static partial void FPDF_RenderPageBitmap(nint bmp, nint page, int x, int y, int w, int h, int rotate, int flags);

    // Export: Pfadobjekte einfügen und Dokument speichern
    [LibraryImport(Lib)] public static partial nint FPDFPageObj_CreateNewPath(float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_LineTo(nint path, float x, float y);
    [LibraryImport(Lib)] public static partial int FPDFPath_SetDrawMode(nint path, int fillMode, int stroke);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetStrokeColor(nint obj, uint r, uint g, uint b, uint a);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetStrokeWidth(nint obj, float width);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetLineCap(nint obj, int cap);
    [LibraryImport(Lib)] public static partial int FPDFPageObj_SetLineJoin(nint obj, int join);
    [LibraryImport(Lib)] public static partial void FPDFPage_InsertObject(nint page, nint obj);
    [LibraryImport(Lib)] public static partial int FPDFPage_GenerateContent(nint page);
    [LibraryImport(Lib)] public static unsafe partial int FPDF_SaveAsCopy(nint doc, FileWrite* writer, uint flags);
    [LibraryImport(Lib)] public static partial nint FPDF_CreateNewDocument();
    [LibraryImport(Lib)] public static partial nint FPDFPage_New(nint doc, int index, double width, double height);
    [LibraryImport(Lib)]
    public static unsafe partial int FPDF_ImportPagesByIndex(nint dest, nint src, int* indices, CULong length, int index);

    /// <summary>FPDF_FILEWRITE: Version + Callback, der die Bytes blockweise liefert.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct FileWrite
    {
        public int Version;
        public delegate* unmanaged[Cdecl]<FileWrite*, void*, CULong, int> WriteBlock;
    }

    public const int FPDF_LINECAP_ROUND = 1;
    public const int FPDF_LINEJOIN_ROUND = 1;
    public const uint FPDF_NO_INCREMENTAL = 2;

    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT = 0x01;
}

/// <summary>
/// Eigener Thread für alle PDFium-Aufrufe (PDFium ist nicht threadsicher). Rendern und Export
/// laufen hier, damit der UI-Thread nie blockiert.
/// </summary>
internal static class PdfiumThread
{
    private static readonly BlockingCollection<Action> Queue = new();

    static PdfiumThread()
    {
        var worker = new Thread(() =>
        {
            Native.FPDF_InitLibrary();
            foreach (var job in Queue.GetConsumingEnumerable()) job();
        })
        {
            IsBackground = true,
            Name = "PDFium",
        };
        worker.Start();
    }

    public static Task<T> Run<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        Queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public static void Post(Action action) => Queue.Add(action);
}

/// <summary>Ein geöffnetes PDF (gerendert wird auf dem <see cref="PdfiumThread"/>).</summary>
public sealed class PdfDocument : IDisposable
{
    private GCHandle _data;
    internal nint Handle { get; private set; }

    public int PageCount { get; private set; }
    /// <summary>Seitengrößen in pt.</summary>
    public IReadOnlyList<Size> PageSizes { get; private set; } = [];

    private PdfDocument() { }

    public static Task<PdfDocument> OpenAsync(string path) => PdfiumThread.Run(() =>
    {
        var doc = new PdfDocument();
        var bytes = File.ReadAllBytes(path);
        doc._data = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        doc.Handle = Native.FPDF_LoadMemDocument(doc._data.AddrOfPinnedObject(), bytes.Length, 0);
        if (doc.Handle == 0)
        {
            doc._data.Free();
            throw new InvalidOperationException($"PDFium konnte '{path}' nicht öffnen (Fehler {Native.FPDF_GetLastError()}).");
        }
        doc.PageCount = Native.FPDF_GetPageCount(doc.Handle);
        var sizes = new Size[doc.PageCount];
        for (int i = 0; i < sizes.Length; i++)
        {
            var page = Native.FPDF_LoadPage(doc.Handle, i);
            sizes[i] = new Size(Native.FPDF_GetPageWidthF(page), Native.FPDF_GetPageHeightF(page));
            Native.FPDF_ClosePage(page);
        }
        doc.PageSizes = sizes;
        return doc;
    });

    /// <summary>
    /// Rendert eine Seite exakt auf <paramref name="size"/> Pixel. <paramref name="isStillWanted"/>
    /// wird direkt vor dem Rendern geprüft, damit veraltete Aufträge (z. B. nach Größenänderung) entfallen.
    /// </summary>
    public Task<Bitmap?> RenderAsync(int index, PixelSize size, Func<bool> isStillWanted) =>
        PdfiumThread.Run(() => isStillWanted() && Handle != 0 ? Render(index, size) : null);

    private Bitmap Render(int index, PixelSize size)
    {
        int stride = size.Width * 4;
        var buffer = GC.AllocateUninitializedArray<byte>(stride * size.Height, pinned: true);
        unsafe
        {
            fixed (byte* p = buffer)
            {
                var bmp = Native.FPDFBitmap_CreateEx(size.Width, size.Height, Native.FPDFBitmap_BGRA, (nint)p, stride);
                Native.FPDFBitmap_FillRect(bmp, 0, 0, size.Width, size.Height, 0xFFFFFFFF);
                var page = Native.FPDF_LoadPage(Handle, index);
                Native.FPDF_RenderPageBitmap(bmp, page, 0, 0, size.Width, size.Height, 0, Native.FPDF_ANNOT);
                Native.FPDF_ClosePage(page);
                Native.FPDFBitmap_Destroy(bmp);
                // Weiß hinterlegt => vollständig opak, Premul == Straight.
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (nint)p, size, new Vector(96, 96), stride);
            }
        }
    }

    public void Dispose() => PdfiumThread.Post(() =>
    {
        if (Handle != 0) Native.FPDF_CloseDocument(Handle);
        Handle = 0;
        if (_data.IsAllocated) _data.Free();
    });
}

/// <summary>
/// PDF-Export: baut ein neues PDF in der Seitenreihenfolge des Dokuments. PDF-Seiten werden aus
/// der Quelle importiert, Papierseiten neu angelegt (Raster als Vektorlinien); Striche kommen als
/// Vektorpfade dazu. Das geöffnete PDF bleibt unverändert.
/// </summary>
public static class PdfExporter
{
    private sealed record Item(int? PdfIndex, Paper? Paper, List<Stroke> Strokes, Rect? CanvasArea = null);

    /// <summary>Exportbereich einer Leinwand: Inhalt plus 1 cm Rand, leer → aktueller Ausschnitt.</summary>
    public static Rect CanvasArea(PaperPage page)
    {
        double margin = 10 * Paper.PtPerMm / Paper.FrameHeightPt;
        if (page.Strokes.Count == 0)
        {
            var v = page.View;
            return new Rect(v.Offset.X, v.Offset.Y, page.SizePt.Width / page.SizePt.Height / v.Zoom, 1 / v.Zoom);
        }
        return page.Strokes.Select(s => s.Bounds).Aggregate((a, b) => a.Union(b)).Inflate(margin);
    }

    [ThreadStatic] private static Stream? _stream;

    public static Task ExportAsync(Deck deck, string target)
    {
        // Momentaufnahme auf dem UI-Thread; fertige Striche werden nicht mehr verändert.
        var items = deck.Pages.Select(p => p switch
        {
            PdfPage pp => new Item(pp.PdfIndex, null, p.Strokes.ToList()),
            PaperPage { Paper.IsInfinite: true } pa => CanvasItem(pa),
            PaperPage pa => new Item(null, pa.Paper, p.Strokes.ToList()),
            _ => throw new NotSupportedException(p.GetType().Name),
        }).ToList();
        var source = deck.Pdf;

        return PdfiumThread.Run(() =>
        {
            var doc = Native.FPDF_CreateNewDocument();
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item.PdfIndex is { } index)
                        Import(doc, source!, index, i);
                    else if (item.CanvasArea is { } area)
                        Native.FPDF_ClosePage(Native.FPDFPage_New(doc, i, area.Width * Paper.FrameHeightPt, area.Height * Paper.FrameHeightPt));
                    else
                        Native.FPDF_ClosePage(Native.FPDFPage_New(doc, i, item.Paper!.SizePt.Width, item.Paper.SizePt.Height));

                    var page = Native.FPDF_LoadPage(doc, i);
                    double h = Native.FPDF_GetPageHeightF(page);
                    if (item.CanvasArea is { } canvasArea) AddCanvasPattern(page, item.Paper!, canvasArea, h);
                    else if (item.Paper is { } paper) AddPaper(page, paper, h);
                    foreach (var stroke in item.Strokes) AddStroke(page, stroke, h);
                    Native.FPDFPage_GenerateContent(page);
                    Native.FPDF_ClosePage(page);
                }
                using var file = File.Create(target);
                Save(doc, file);
                return true;
            }
            finally
            {
                Native.FPDF_CloseDocument(doc);
            }
        });
    }

    private static unsafe void Import(nint dest, PdfDocument source, int pdfIndex, int at)
    {
        if (Native.FPDF_ImportPagesByIndex(dest, source.Handle, &pdfIndex, new CULong(1), at) == 0)
            throw new InvalidOperationException($"Seite {pdfIndex + 1} konnte nicht übernommen werden.");
    }

    private static void AddPaper(nint page, Paper paper, double pageHeight)
    {
        var lineColor = paper.Style == PaperStyle.Lined ? PaperRenderer.LineColor : PaperRenderer.GridColor;
        foreach (var l in PaperRenderer.Lines(paper))
            AddPath(page, [Flip(l.A, pageHeight), Flip(l.B, pageHeight)], PaperRenderer.LineWidthPt, lineColor);
        // Punkte als Linie der Länge ~0 mit runden Enden
        foreach (var d in PaperRenderer.Dots(paper))
        {
            var p = Flip(d, pageHeight);
            AddPath(page, [p, p + new Vector(0.01, 0)], PaperRenderer.DotRadiusPt * 2, PaperRenderer.DotColor);
        }
    }

    private static Point Flip(Point p, double pageHeight) => new(p.X, pageHeight - p.Y);

    /// <summary>Leinwand: Striche relativ zum Exportbereich, normiert auf dessen Höhe (= Seitenhöhe).</summary>
    private static Item CanvasItem(PaperPage page)
    {
        var area = CanvasArea(page);
        var strokes = page.Strokes.Select(s => s.Transformed(new Vector(-area.X, -area.Y), 1 / area.Height)).ToList();
        return new Item(null, page.Paper, strokes, area);
    }

    private static void AddCanvasPattern(nint page, Paper paper, Rect area, double pageHeight)
    {
        var (lines, dots) = PaperRenderer.CanvasPattern(paper, area);
        var lineColor = paper.Style == PaperStyle.Lined ? PaperRenderer.LineColor : PaperRenderer.GridColor;
        foreach (var l in lines)
            AddPath(page, [Flip(l.A, pageHeight), Flip(l.B, pageHeight)], PaperRenderer.LineWidthPt, lineColor);
        foreach (var d in dots)
        {
            var p = Flip(d, pageHeight);
            AddPath(page, [p, p + new Vector(0.01, 0)], PaperRenderer.DotRadiusPt * 2, PaperRenderer.DotColor);
        }
    }

    private static void AddStroke(nint page, Stroke stroke, double pageHeight)
    {
        foreach (var (points, width) in DocumentFile.ToPdfPaths(stroke, pageHeight))
            AddPath(page, points, width, stroke.Color);
    }

    private static void AddPath(nint page, List<Point> points, double width, Avalonia.Media.Color c)
    {
        var path = Native.FPDFPageObj_CreateNewPath((float)points[0].X, (float)points[0].Y);
        for (int i = 1; i < points.Count; i++) Native.FPDFPath_LineTo(path, (float)points[i].X, (float)points[i].Y);
        Native.FPDFPath_SetDrawMode(path, 0, 1);
        Native.FPDFPageObj_SetStrokeColor(path, c.R, c.G, c.B, c.A);
        Native.FPDFPageObj_SetStrokeWidth(path, (float)width);
        Native.FPDFPageObj_SetLineCap(path, Native.FPDF_LINECAP_ROUND);
        Native.FPDFPageObj_SetLineJoin(path, Native.FPDF_LINEJOIN_ROUND);
        Native.FPDFPage_InsertObject(page, path);
    }

    private static unsafe void Save(nint doc, Stream target)
    {
        _stream = target;
        try
        {
            var writer = new Native.FileWrite { Version = 1, WriteBlock = &WriteBlock };
            if (Native.FPDF_SaveAsCopy(doc, &writer, Native.FPDF_NO_INCREMENTAL) == 0)
                throw new IOException("PDFium konnte das PDF nicht speichern.");
        }
        finally { _stream = null; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int WriteBlock(Native.FileWrite* self, void* data, CULong size)
    {
        try
        {
            _stream!.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch { return 0; }
    }
}
