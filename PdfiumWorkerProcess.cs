using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using ImageMagick;
using PDFiumCore;

namespace CDisplayEx.CSharp;

internal static unsafe class PdfiumWorkerServer
{
    private const string WorkerArgument = "--pdfium-worker";
    private const int BitmapFormatBgra = 4;
    private const int PageObjectImage = 3;
    private const int RenderAnnotations = 0x01;
    private const int RenderLcdText = 0x02;
    // A pool worker only needs the actively opened book and at most one cover
    // document. Keeping eight large PDFs per process retained hundreds of MB
    // after long folder-browsing sessions.
    private const int MaximumOpenDocumentsPerWorker = 2;
    private const ulong MaximumExtractedJpegBytes = 1024UL * 1024 * 1024;

    private enum Command : byte
    {
        PageCount = 1,
        PageSize = 2,
        RenderBgra = 3,
        ExtractImageOnlyJpeg = 4,
        CloseDocument = 5,
        DeletePageCopy = 6,
        Shutdown = 255
    }

    public static bool TryRun(string[] args)
    {
        if (args.Length != 3 || !string.Equals(
                args[0], WorkerArgument, StringComparison.OrdinalIgnoreCase)) return false;
        using var input = new AnonymousPipeClientStream(PipeDirection.In, args[1]);
        using var output = new AnonymousPipeClientStream(PipeDirection.Out, args[2]);
        try { Run(input, output); }
        catch (IOException)
        {
            // BinaryWriter.Dispose may flush after Run's loop has already left
            // its pipe-error guard. A vanished parent is a normal worker exit.
        }
        return true;
    }

    private static void Run(Stream input, Stream output)
    {
        fpdfview.FPDF_InitLibrary();
        var documents = new Dictionary<string, FpdfDocumentT>(StringComparer.OrdinalIgnoreCase);
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        try
        {
            while (true)
            {
                Command command;
                try { command = (Command)reader.ReadByte(); }
                catch (EndOfStreamException) { break; }
                if (command == Command.Shutdown) break;

                try
                {
                    Execute(command, reader, writer, documents);
                }
                catch (Exception exception)
                {
                    writer.Write(false);
                    writer.Write(exception.Message);
                }
                writer.Flush();
            }
        }
        catch (IOException)
        {
            // The parent owns both anonymous pipes. If it exits or cancels while
            // a response is being written, the worker should terminate quietly
            // instead of surfacing a second, misleading crash.
        }
        finally
        {
            foreach (var document in documents.Values)
                fpdfview.FPDF_CloseDocument(document);
        }
    }

    private static void Execute(Command command, BinaryReader reader,
        BinaryWriter writer, Dictionary<string, FpdfDocumentT> documents)
    {
        var path = reader.ReadString();
        if (command == Command.CloseDocument)
        {
            path = Path.GetFullPath(path);
            if (documents.Remove(path, out var openDocument))
                fpdfview.FPDF_CloseDocument(openDocument);
            writer.Write(true);
            return;
        }
        if (command == Command.DeletePageCopy)
        {
            var pageCount = reader.ReadInt32();
            if (pageCount <= 0 || pageCount > 1_000_000)
                throw new InvalidDataException("Invalid PDF page deletion count.");
            var pageIndices = new int[pageCount];
            for (var i = 0; i < pageIndices.Length; i++)
                pageIndices[i] = reader.ReadInt32();
            var outputPath = reader.ReadString();
            DeletePageCopy(path, pageIndices, outputPath);
            writer.Write(true);
            return;
        }
        var document = GetDocument(path, documents);
        switch (command)
        {
            case Command.PageCount:
                writer.Write(true);
                writer.Write(fpdfview.FPDF_GetPageCount(document));
                return;
            case Command.PageSize:
            {
                var pageIndex = reader.ReadInt32();
                var page = LoadPage(document, pageIndex);
                try
                {
                    var width = fpdfview.FPDF_GetPageWidthF(page);
                    var height = fpdfview.FPDF_GetPageHeightF(page);
                    if (width <= 0 || height <= 0)
                        throw new InvalidDataException("PDFium returned invalid page dimensions.");
                    writer.Write(true);
                    writer.Write(width);
                    writer.Write(height);
                }
                finally { fpdfview.FPDF_ClosePage(page); }
                return;
            }
            case Command.RenderBgra:
            {
                var pageIndex = reader.ReadInt32();
                var width = reader.ReadInt32();
                var height = reader.ReadInt32();
                var pixels = RenderBgra(document, pageIndex, width, height);
                writer.Write(true);
                writer.Write(pixels.Length);
                writer.Write(pixels);
                return;
            }
            case Command.ExtractImageOnlyJpeg:
            {
                var pageIndex = reader.ReadInt32();
                var jpeg = ExtractImageOnlyJpeg(document, pageIndex);
                writer.Write(true);
                writer.Write(jpeg?.Length ?? 0);
                if (jpeg is not null) writer.Write(jpeg);
                return;
            }
            default:
                throw new InvalidDataException("Unknown PDFium worker command.");
        }
    }

    private static void DeletePageCopy(
        string sourcePath, IReadOnlyCollection<int> requestedPageIndices, string outputPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        var document = fpdfview.FPDF_LoadDocument(sourcePath, null!)
            ?? throw CreatePdfiumException("open document for editing");
        var expectedPageCount = 0;
        try
        {
            var pageCount = fpdfview.FPDF_GetPageCount(document);
            var pageIndices = requestedPageIndices.Distinct().OrderDescending().ToArray();
            if (pageIndices.Length == 0)
                throw new InvalidOperationException("Select at least one PDF page to delete.");
            if (pageIndices.Length >= pageCount)
                throw new InvalidOperationException("A PDF must keep at least one page.");
            if (pageIndices.Any(index => (uint)index >= (uint)pageCount))
                throw new ArgumentOutOfRangeException(nameof(requestedPageIndices));
            expectedPageCount = pageCount - pageIndices.Length;

            // Delete backwards so earlier indices remain stable.
            foreach (var pageIndex in pageIndices)
                fpdf_edit.FPDFPageDelete(document, pageIndex);
            using var output = new FileStream(outputPath, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 1024 * 1024,
                FileOptions.SequentialScan);
            Exception? writeFailure = null;
            PDFiumCore.Delegates.Func_int___IntPtr___IntPtr_ulong writeBlock =
                (_, data, size) =>
                {
                    try
                    {
                        var remaining = size;
                        var address = data;
                        while (remaining > 0)
                        {
                            var length = (int)Math.Min(remaining, (ulong)int.MaxValue);
                            output.Write(new ReadOnlySpan<byte>(address.ToPointer(), length));
                            address += length;
                            remaining -= (ulong)length;
                        }
                        return 1;
                    }
                    catch (Exception exception)
                    {
                        writeFailure = exception;
                        return 0;
                    }
                };
            using var writer = new FPDF_FILEWRITE_
            {
                Version = 1,
                WriteBlock = writeBlock
            };
            // FPDF_NO_INCREMENTAL produces a self-contained replacement file.
            if (fpdf_save.FPDF_SaveAsCopy(document, writer, 2) == 0)
                throw new IOException("PDFium could not save the edited PDF.", writeFailure);
            output.Flush(flushToDisk: true);
            GC.KeepAlive(writeBlock);
        }
        finally { fpdfview.FPDF_CloseDocument(document); }

        var verification = fpdfview.FPDF_LoadDocument(outputPath, null!)
            ?? throw CreatePdfiumException("open the edited PDF for verification");
        try
        {
            if (fpdfview.FPDF_GetPageCount(verification) != expectedPageCount)
                throw new InvalidDataException(
                    "The edited PDF did not contain the expected number of pages.");
        }
        finally { fpdfview.FPDF_CloseDocument(verification); }
    }

    private static FpdfDocumentT GetDocument(
        string path, Dictionary<string, FpdfDocumentT> documents)
    {
        path = Path.GetFullPath(path);
        if (documents.TryGetValue(path, out var document)) return document;
        if (documents.Count >= MaximumOpenDocumentsPerWorker)
        {
            var oldest = documents.First();
            documents.Remove(oldest.Key);
            fpdfview.FPDF_CloseDocument(oldest.Value);
        }
        document = fpdfview.FPDF_LoadDocument(path, null!)
            ?? throw CreatePdfiumException("open document");
        documents.Add(path, document);
        return document;
    }

    private static FpdfPageT LoadPage(FpdfDocumentT document, int pageIndex) =>
        fpdfview.FPDF_LoadPage(document, pageIndex)
        ?? throw CreatePdfiumException("load page");

    private static byte[] RenderBgra(
        FpdfDocumentT document, int pageIndex, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue / 4)
            throw new InvalidDataException("Requested PDF page bitmap is too large.");
        var page = LoadPage(document, pageIndex);
        try
        {
            var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4), pinned: true);
            fixed (byte* destination = pixels)
            {
                var bitmap = fpdfview.FPDFBitmapCreateEx(
                    width, height, BitmapFormatBgra, (IntPtr)destination, width * 4)
                    ?? throw CreatePdfiumException("create BGRA bitmap");
                try
                {
                    fpdfview.FPDFBitmapFillRect(bitmap, 0, 0, width, height, 0xFFFFFFFFUL);
                    fpdfview.FPDF_RenderPageBitmap(bitmap, page, 0, 0,
                        width, height, 0, RenderAnnotations | RenderLcdText);
                }
                finally { fpdfview.FPDFBitmapDestroy(bitmap); }
            }
            return pixels;
        }
        finally { fpdfview.FPDF_ClosePage(page); }
    }

    private static byte[]? ExtractImageOnlyJpeg(FpdfDocumentT document, int pageIndex)
    {
        var page = LoadPage(document, pageIndex);
        try
        {
            if (fpdf_annot.FPDFPageGetAnnotCount(page) != 0 ||
                fpdf_edit.FPDFPageGetRotation(page) != 0 ||
                fpdf_edit.FPDFPageCountObjects(page) != 1) return null;
            var image = fpdf_edit.FPDFPageGetObject(page, 0);
            if (image is null || fpdf_edit.FPDFPageObjGetType(image) != PageObjectImage)
                return null;

            var pageWidth = fpdfview.FPDF_GetPageWidthF(page);
            var pageHeight = fpdfview.FPDF_GetPageHeightF(page);
            if (pageWidth <= 0 || pageHeight <= 0 ||
                !CoversWholePage(image, pageWidth, pageHeight) ||
                !HasSimplePositiveTransform(image) ||
                !HasNvJpegSafeColorSpace(image, page)) return null;

            var length = fpdf_edit.FPDFImageObjGetImageDataRaw(image, IntPtr.Zero, 0);
            if (length < 4 || length > MaximumExtractedJpegBytes || length > int.MaxValue)
                return null;
            var bytes = new byte[(int)length];
            fixed (byte* destination = bytes)
            {
                var written = fpdf_edit.FPDFImageObjGetImageDataRaw(
                    image, (IntPtr)destination, length);
                if (written < 4 || written > length) return null;
                if (written != length) Array.Resize(ref bytes, (int)written);
            }
            return IsJpeg(bytes) ? bytes : null;
        }
        finally { fpdfview.FPDF_ClosePage(page); }
    }

    private static bool CoversWholePage(
        FpdfPageobjectT image, float pageWidth, float pageHeight)
    {
        float left = 0, bottom = 0, right = 0, top = 0;
        if (fpdf_edit.FPDFPageObjGetBounds(
                image, ref left, ref bottom, ref right, ref top) == 0) return false;
        var toleranceX = Math.Max(0.5f, pageWidth * 0.001f);
        var toleranceY = Math.Max(0.5f, pageHeight * 0.001f);
        return Math.Abs(left) <= toleranceX && Math.Abs(bottom) <= toleranceY &&
            Math.Abs(right - pageWidth) <= toleranceX &&
            Math.Abs(top - pageHeight) <= toleranceY;
    }

    private static bool HasSimplePositiveTransform(FpdfPageobjectT image)
    {
        using var matrix = new FS_MATRIX_();
        if (fpdf_edit.FPDFPageObjGetMatrix(image, matrix) == 0) return false;
        const float epsilon = 0.001f;
        return matrix.A > 0 && matrix.D > 0 &&
            Math.Abs(matrix.B) <= epsilon && Math.Abs(matrix.C) <= epsilon;
    }

    private static bool HasNvJpegSafeColorSpace(FpdfPageobjectT image, FpdfPageT page)
    {
        using var metadata = new FPDF_IMAGEOBJ_METADATA();
        if (fpdf_edit.FPDFImageObjGetImageMetadata(image, page, metadata) == 0)
            return false;
        if (metadata.BitsPerPixel is not (8 or 24)) return false;
        if (metadata.Colorspace is 1 or 2) return true;
        return metadata.Colorspace == 7 && HasSrgbIccProfile(image, page);
    }

    private static bool HasSrgbIccProfile(FpdfPageobjectT image, FpdfPageT page)
    {
        ulong length = 0;
        if (fpdf_edit.FPDFImageObjGetIccProfileDataDecoded(
                image, page, null, 0, ref length) == 0 || length == 0 ||
            length > 16UL * 1024 * 1024 || length > int.MaxValue) return false;
        var bytes = new byte[(int)length];
        fixed (byte* destination = bytes)
        {
            ulong written = 0;
            if (fpdf_edit.FPDFImageObjGetIccProfileDataDecoded(
                    image, page, destination, length, ref written) == 0 ||
                written == 0 || written > length) return false;
            if (written != length) Array.Resize(ref bytes, (int)written);
        }
        try
        {
            var profile = new ColorProfile(bytes);
            return profile.Description?.Contains(
                "sRGB", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch { return false; }
    }

    private static bool IsJpeg(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xD8 &&
        bytes[^2] == 0xFF && bytes[^1] == 0xD9;

    private static Exception CreatePdfiumException(string operation) =>
        new InvalidDataException(
            $"PDFium could not {operation} (error {fpdfview.FPDF_GetLastError()}).");

    internal static PdfiumWorkerClient StartClient() => new(WorkerArgument);
}

internal sealed class PdfiumWorkerClient : IDisposable
{
    private readonly string _workerArgument;
    private Process? _process;
    private AnonymousPipeServerStream? _requestPipe;
    private AnonymousPipeServerStream? _responsePipe;
    private BinaryWriter? _writer;
    private BinaryReader? _reader;
    private int _disposed;

    public PdfiumWorkerClient(string workerArgument)
    {
        _workerArgument = workerArgument;
        Start();
    }

    public T Execute<T>(Action<BinaryWriter> writeRequest, Func<BinaryReader, T> readResponse)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try { return ExecuteCore(writeRequest, readResponse); }
        catch (Exception first) when (first is IOException or EndOfStreamException or ObjectDisposedException)
        {
            Restart();
            try { return ExecuteCore(writeRequest, readResponse); }
            catch (Exception second)
            {
                throw new InvalidDataException(
                    $"PDFium worker process failed after restart: {second.Message}", second);
            }
        }
    }

    private T ExecuteCore<T>(Action<BinaryWriter> writeRequest, Func<BinaryReader, T> readResponse)
    {
        var writer = _writer ?? throw new ObjectDisposedException(nameof(PdfiumWorkerClient));
        var reader = _reader ?? throw new ObjectDisposedException(nameof(PdfiumWorkerClient));
        writeRequest(writer);
        writer.Flush();
        if (!reader.ReadBoolean()) throw new InvalidDataException(reader.ReadString());
        return readResponse(reader);
    }

    private void Start()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the Fast Reader/Viewer executable.");
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals(
                "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name
                ?? throw new InvalidOperationException("Cannot identify Fast Reader Viewer.dll.");
            var entryAssemblyPath = Path.Combine(
                AppContext.BaseDirectory, $"{entryAssemblyName}.dll");
            if (!File.Exists(entryAssemblyPath))
                throw new InvalidOperationException(
                    $"Cannot locate Fast Reader Viewer.dll at '{entryAssemblyPath}'.");
            start.ArgumentList.Add(entryAssemblyPath);
        }
        start.ArgumentList.Add(_workerArgument);
        var requestPipe = new AnonymousPipeServerStream(
            PipeDirection.Out, HandleInheritability.Inheritable);
        var responsePipe = new AnonymousPipeServerStream(
            PipeDirection.In, HandleInheritability.Inheritable);
        start.ArgumentList.Add(requestPipe.GetClientHandleAsString());
        start.ArgumentList.Add(responsePipe.GetClientHandleAsString());
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Could not start a PDFium worker process.");
            requestPipe.DisposeLocalCopyOfClientHandle();
            responsePipe.DisposeLocalCopyOfClientHandle();
            _process = process;
            _requestPipe = requestPipe;
            _responsePipe = responsePipe;
            _writer = new BinaryWriter(requestPipe,
                System.Text.Encoding.UTF8, leaveOpen: true);
            _reader = new BinaryReader(responsePipe,
                System.Text.Encoding.UTF8, leaveOpen: true);
        }
        catch
        {
            process.Dispose();
            requestPipe.Dispose();
            responsePipe.Dispose();
            throw;
        }
    }

    private void Restart()
    {
        Stop();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Start();
    }

    private void Stop()
    {
        try
        {
            if (_process is { HasExited: false } && _writer is not null)
            {
                _writer.Write((byte)255);
                _writer.Flush();
                if (!_process.WaitForExit(500)) _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
            catch { }
        }
        _writer?.Dispose();
        _reader?.Dispose();
        _requestPipe?.Dispose();
        _responsePipe?.Dispose();
        _process?.Dispose();
        _writer = null;
        _reader = null;
        _requestPipe = null;
        _responsePipe = null;
        _process = null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
    }
}

internal sealed class PdfiumProcessPool : IDisposable
{
    private readonly ConcurrentQueue<PdfiumWorkerClient> _available = [];
    private readonly PdfiumWorkerClient[] _workers;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _backgroundSlots;
    private readonly SemaphoreSlim _documentCloseGate = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _documentReferences =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pendingDocumentCloses =
        new(StringComparer.OrdinalIgnoreCase);
    private int _referenceCount;
    private int _retired;
    private int _disposed;

    public int Count => _workers.Length;

    public PdfiumProcessPool(int count)
    {
        count = Math.Clamp(count, 1, 16);
        var workers = new List<PdfiumWorkerClient>(count);
        try
        {
            for (var i = 0; i < count; i++) workers.Add(PdfiumWorkerServer.StartClient());
        }
        catch
        {
            foreach (var worker in workers) worker.Dispose();
            throw;
        }
        _workers = workers.ToArray();
        foreach (var worker in _workers) _available.Enqueue(worker);
        _slots = new SemaphoreSlim(count, count);
        // Keep one process free for opening/reading the file the user selected.
        // Folder contact sheets and PDF thumbnail renders must not occupy the
        // complete pool and make navigation look hung.
        var backgroundCount = count == 1 ? 1 : count - 1;
        _backgroundSlots = new SemaphoreSlim(backgroundCount, backgroundCount);
    }

    public void AddReference() => Interlocked.Increment(ref _referenceCount);

    public void AddDocumentReference(string path)
    {
        path = Path.GetFullPath(path);
        _documentReferences.AddOrUpdate(path, 1, static (_, count) => count + 1);
    }

    public void ReleaseDocumentReference(string path)
    {
        path = Path.GetFullPath(path);
        while (_documentReferences.TryGetValue(path, out var count))
        {
            if (count > 1)
            {
                if (_documentReferences.TryUpdate(path, count - 1, count)) return;
                continue;
            }
            if (!_documentReferences.TryRemove(
                    new KeyValuePair<string, int>(path, count))) continue;
            QueueDocumentClose(path);
            return;
        }
    }

    public void ReleaseReference()
    {
        if (Interlocked.Decrement(ref _referenceCount) == 0 &&
            Volatile.Read(ref _retired) != 0) Dispose();
    }

    public void Retire()
    {
        Volatile.Write(ref _retired, 1);
        if (Volatile.Read(ref _referenceCount) == 0) Dispose();
    }

    public T Execute<T>(Func<PdfiumWorkerClient, T> operation,
        bool background = false, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (background) _backgroundSlots.Wait(cancellationToken);
        try
        {
            _slots.Wait(cancellationToken);
            if (!_available.TryDequeue(out var worker))
            {
                _slots.Release();
                throw new InvalidOperationException("PDFium process pool lost a worker.");
            }
            try { return operation(worker); }
            finally
            {
                _available.Enqueue(worker);
                _slots.Release();
            }
        }
        finally { if (background) _backgroundSlots.Release(); }
    }

    public void CloseDocument(string path)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _documentCloseGate.Wait();
        try { CloseDocumentCore(path); }
        finally { _documentCloseGate.Release(); }
    }

    private void QueueDocumentClose(string path)
    {
        if (!_pendingDocumentCloses.TryAdd(path, 0)) return;
        // Keep a retired pool alive until its queued native handles are closed.
        AddReference();
        _ = Task.Run(() =>
        {
            try
            {
                _documentCloseGate.Wait();
                try
                {
                    // A new renderer may have acquired the same path while this
                    // cleanup waited behind active page renders.
                    if (!_documentReferences.ContainsKey(path) &&
                        Volatile.Read(ref _disposed) == 0)
                        CloseDocumentOnAvailableWorkers(path);
                }
                finally { _documentCloseGate.Release(); }
            }
            catch (ObjectDisposedException) { }
            catch (Exception exception)
            {
                ExtendedDiagnostics.LogException(
                    "PDFium idle document cleanup failed", exception,
                    $"path={path}");
            }
            finally
            {
                _pendingDocumentCloses.TryRemove(path, out _);
                ReleaseReference();
            }
        });
    }

    private void CloseDocumentOnAvailableWorkers(string path)
    {
        var workers = new List<PdfiumWorkerClient>(_workers.Length);
        try
        {
            // Idle cleanup must never reserve part of the pool while waiting for
            // one long native PDFium command. Take only workers that are already
            // idle; busy workers will evict the document through their bounded
            // two-document cache when they process later work.
            while (_slots.Wait(0))
            {
                if (_available.TryDequeue(out var worker))
                    workers.Add(worker);
                else
                {
                    _slots.Release();
                    break;
                }
            }
            foreach (var worker in workers)
                CloseDocument(worker, path);
        }
        finally
        {
            foreach (var worker in workers) _available.Enqueue(worker);
            for (var i = 0; i < workers.Count; i++) _slots.Release();
        }
    }

    private void CloseDocumentCore(string path)
    {
        var acquired = 0;
        var workers = new List<PdfiumWorkerClient>(_workers.Length);
        try
        {
            for (; acquired < _workers.Length; acquired++) _slots.Wait();
            while (_available.TryDequeue(out var worker)) workers.Add(worker);
            if (workers.Count != _workers.Length)
                throw new InvalidOperationException("PDFium process pool lost a worker.");
            foreach (var worker in workers)
                CloseDocument(worker, path);
        }
        finally
        {
            foreach (var worker in workers) _available.Enqueue(worker);
            for (var i = 0; i < acquired; i++) _slots.Release();
        }
    }

    private static void CloseDocument(PdfiumWorkerClient worker, string path) =>
        worker.Execute(writer =>
        {
            writer.Write((byte)5);
            writer.Write(Path.GetFullPath(path));
        }, _ => true);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var worker in _workers) worker.Dispose();
        _documentCloseGate.Dispose();
        _backgroundSlots.Dispose();
        _slots.Dispose();
    }
}

internal static class PdfiumProcessPoolManager
{
    private static readonly object Gate = new();
    private static PdfiumProcessPool? _current;
    private static int _configuredCount = 4;

    public static int ConfiguredCount
    {
        get { lock (Gate) return _configuredCount; }
        set
        {
            PdfiumProcessPool? retired = null;
            lock (Gate)
            {
                value = Math.Clamp(value, 1, 16);
                if (_configuredCount == value) return;
                _configuredCount = value;
                retired = _current;
                _current = null;
            }
            retired?.Retire();
        }
    }

    public static PdfiumProcessPool Acquire()
    {
        lock (Gate)
        {
            _current ??= new PdfiumProcessPool(_configuredCount);
            _current.AddReference();
            return _current;
        }
    }

    public static void CloseDocument(string path)
    {
        PdfiumProcessPool? pool;
        lock (Gate) pool = _current;
        pool?.CloseDocument(path);
    }
}
