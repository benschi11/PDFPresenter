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
/// Ein geöffnetes PDF. Alle PDFium-Aufrufe laufen auf einem eigenen Worker-Thread
/// (PDFium ist nicht threadsicher), damit das Rendern einer Seite nie den UI-Thread blockiert.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _worker;
    private GCHandle _data;
    private nint _doc;
    private byte[] _bytes = [];

    [ThreadStatic] private static Stream? _exportStream;

    public int PageCount { get; private set; }
    /// <summary>Seitengrößen in pt.</summary>
    public IReadOnlyList<Size> PageSizes { get; private set; } = [];

    private PdfDocument()
    {
        _worker = new Thread(() => { foreach (var job in _queue.GetConsumingEnumerable()) job(); })
        {
            IsBackground = true,
            Name = "PDFium",
        };
        _worker.Start();
    }

    public static Task<PdfDocument> OpenAsync(string path)
    {
        var doc = new PdfDocument();
        var tcs = new TaskCompletionSource<PdfDocument>();
        doc._queue.Add(() =>
        {
            try
            {
                Native.FPDF_InitLibrary();
                var bytes = File.ReadAllBytes(path);
                doc._bytes = bytes;
                doc._data = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                doc._doc = Native.FPDF_LoadMemDocument(doc._data.AddrOfPinnedObject(), bytes.Length, 0);
                if (doc._doc == 0)
                    throw new InvalidOperationException($"PDFium konnte '{path}' nicht öffnen (Fehler {Native.FPDF_GetLastError()}).");
                doc.PageCount = Native.FPDF_GetPageCount(doc._doc);
                var sizes = new Size[doc.PageCount];
                for (int i = 0; i < sizes.Length; i++)
                {
                    var page = Native.FPDF_LoadPage(doc._doc, i);
                    sizes[i] = new Size(Native.FPDF_GetPageWidthF(page), Native.FPDF_GetPageHeightF(page));
                    Native.FPDF_ClosePage(page);
                }
                doc.PageSizes = sizes;
                tcs.SetResult(doc);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// Rendert eine Seite exakt auf <paramref name="size"/> Pixel. <paramref name="isStillWanted"/>
    /// wird direkt vor dem Rendern geprüft, damit veraltete Aufträge (z. B. nach Größenänderung) entfallen.
    /// </summary>
    public Task<Bitmap?> RenderAsync(int index, PixelSize size, Func<bool> isStillWanted)
    {
        var tcs = new TaskCompletionSource<Bitmap?>();
        _queue.Add(() =>
        {
            if (!isStillWanted()) { tcs.SetResult(null); return; }
            try { tcs.SetResult(Render(index, size)); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

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
                var page = Native.FPDF_LoadPage(_doc, index);
                Native.FPDF_RenderPageBitmap(bmp, page, 0, 0, size.Width, size.Height, 0, Native.FPDF_ANNOT);
                Native.FPDF_ClosePage(page);
                Native.FPDFBitmap_Destroy(bmp);
                // Weiß hinterlegt => vollständig opak, Premul == Straight.
                return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, (nint)p, size, new Vector(96, 96), stride);
            }
        }
    }

    /// <summary>
    /// Schreibt eine Kopie des PDFs mit eingezeichneten Annotationen (Vektorpfade) nach
    /// <paramref name="target"/>. Das geöffnete Dokument bleibt unverändert.
    /// </summary>
    public Task ExportAsync(string target, IReadOnlyDictionary<int, List<Stroke>> ink)
    {
        // Momentaufnahme der Striche, da der UI-Thread weiterzeichnen darf
        var snapshot = ink.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        var tcs = new TaskCompletionSource();
        _queue.Add(() =>
        {
            var handle = GCHandle.Alloc(_bytes.ToArray(), GCHandleType.Pinned);
            nint doc = 0;
            try
            {
                var bytes = (byte[])handle.Target!;
                doc = Native.FPDF_LoadMemDocument(handle.AddrOfPinnedObject(), bytes.Length, 0);
                if (doc == 0) throw new InvalidOperationException("PDF konnte für den Export nicht geladen werden.");
                foreach (var (index, strokes) in snapshot)
                {
                    if (strokes.Count == 0 || index >= PageCount) continue;
                    var page = Native.FPDF_LoadPage(doc, index);
                    double h = Native.FPDF_GetPageHeightF(page);
                    foreach (var stroke in strokes) AddStroke(page, stroke, h);
                    Native.FPDFPage_GenerateContent(page);
                    Native.FPDF_ClosePage(page);
                }
                using var file = File.Create(target);
                Save(doc, file);
                tcs.SetResult();
            }
            catch (Exception ex) { tcs.SetException(ex); }
            finally
            {
                if (doc != 0) Native.FPDF_CloseDocument(doc);
                handle.Free();
            }
        });
        return tcs.Task;
    }

    private static void AddStroke(nint page, Stroke stroke, double pageHeight)
    {
        var c = stroke.Color;
        foreach (var (points, width) in AnnotationFile.ToPdfPaths(stroke, pageHeight))
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
    }

    private static unsafe void Save(nint doc, Stream target)
    {
        _exportStream = target;
        try
        {
            var writer = new Native.FileWrite { Version = 1, WriteBlock = &WriteBlock };
            if (Native.FPDF_SaveAsCopy(doc, &writer, Native.FPDF_NO_INCREMENTAL) == 0)
                throw new IOException("PDFium konnte das PDF nicht speichern.");
        }
        finally { _exportStream = null; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static unsafe int WriteBlock(Native.FileWrite* self, void* data, CULong size)
    {
        try
        {
            _exportStream!.Write(new ReadOnlySpan<byte>(data, checked((int)size.Value)));
            return 1;
        }
        catch { return 0; }
    }

    public void Dispose()
    {
        _queue.Add(() =>
        {
            if (_doc != 0) Native.FPDF_CloseDocument(_doc);
            if (_data.IsAllocated) _data.Free();
        });
        _queue.CompleteAdding();
    }
}
