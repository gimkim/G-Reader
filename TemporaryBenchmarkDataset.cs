using System.Text;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal sealed record TemporaryDatasetProgress(
    int Completed, int Total, string Detail);

internal sealed class TemporaryBenchmarkDataset : IAsyncDisposable
{
    public string Path { get; }

    private TemporaryBenchmarkDataset(string path) => Path = path;

    public static async Task<TemporaryBenchmarkDataset> CreateAsync(
        IProgress<TemporaryDatasetProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "GReader-Benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Task.Run(() => Generate(root, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return new TemporaryBenchmarkDataset(root);
        }
        catch
        {
            _ = Task.Run(() => TryDelete(root));
            throw;
        }
    }

    private static void Generate(
        string root, IProgress<TemporaryDatasetProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sizes = SelectSizes(root);
        var jobs = sizes.Sum(item => item.Variants) * 7;
        var completed = 0;
        foreach (var size in sizes)
        {
            for (var variant = 0; variant < size.Variants; variant++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pixels = CreatePattern(size.Width, size.Height, variant,
                    cancellationToken);
                using var image = new MagickImage(pixels, new MagickReadSettings
                {
                    Width = (uint)size.Width,
                    Height = (uint)size.Height,
                    Format = MagickFormat.Bgra
                });
                image.ColorSpace = ColorSpace.sRGB;
                var stem = $"{size.Label}-{variant + 1:D2}";
                Write(image, root, stem, MagickFormat.Jpeg, "jpg", 88);
                Report("JPEG");
                Write(image, root, stem, MagickFormat.Png, "png", 90);
                Report("PNG");
                Write(image, root, stem, MagickFormat.WebP, "webp", 86);
                Report("WebP");
                Write(image, root, stem, MagickFormat.Gif, "gif", 90);
                Report("GIF");
                Write(image, root, stem, MagickFormat.Tiff, "tiff", 90);
                Report("TIFF");
                Write(image, root, stem, MagickFormat.Bmp, "bmp", 90);
                Report("BMP");
                var jpegPath = System.IO.Path.Combine(root, "jpg", stem + ".jpg");
                var pdfFolder = System.IO.Path.Combine(root, "pdf");
                Directory.CreateDirectory(pdfFolder);
                WriteJpegPdf(System.IO.Path.Combine(pdfFolder, stem + ".pdf"),
                    File.ReadAllBytes(jpegPath), size.Width, size.Height);
                Report("PDF");

                void Report(string format)
                {
                    completed++;
                    progress?.Report(new TemporaryDatasetProgress(completed, jobs,
                        $"{format} {size.Width:N0} x {size.Height:N0}"));
                }
            }
        }
    }

    private static IReadOnlyList<(string Label, int Width, int Height, int Variants)>
        SelectSizes(string root)
    {
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (ram <= 0) ram = 8L * 1024 * 1024 * 1024;
        long freeDisk;
        try
        {
            var drive = new DriveInfo(System.IO.Path.GetPathRoot(root)!);
            freeDisk = drive.AvailableFreeSpace;
        }
        catch { freeDisk = 8L * 1024 * 1024 * 1024; }
        var sizes = new List<(string, int, int, int)>
        {
            ("01-small-0.9MP", 1280, 720, 2),
            ("02-medium-3.7MP", 2560, 1440, 2),
            ("03-4k-8.3MP", 3840, 2160, 1)
        };
        if (ram >= 10L * 1024 * 1024 * 1024 &&
            freeDisk >= 5L * 1024 * 1024 * 1024)
            sizes.Add(("04-large-24MP", 6000, 4000, 1));
        if (ram >= 20L * 1024 * 1024 * 1024 &&
            freeDisk >= 10L * 1024 * 1024 * 1024)
            sizes.Add(("05-camera-45MP", 8192, 5464, 1));
        return sizes;
    }

    private static byte[] CreatePattern(
        int width, int height, int variant, CancellationToken cancellationToken)
    {
        var pixels = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
        Parallel.For(0, height, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
            CancellationToken = cancellationToken
        }, y =>
        {
            var row = y * width * 4;
            var state = unchecked((uint)(y * 747796405 + variant * 2891336453));
            for (var x = 0; x < width; x++)
            {
                // Mixed gradients, fine texture, and hard edges exercise both
                // photographic compression and resize filters without assets.
                state = state * 1664525u + 1013904223u;
                var noise = (int)(state >> 27);
                var checker = ((x / 97 + y / 83 + variant) & 1) * 38;
                var radial = (int)(31 * Math.Sin((x + y * 0.7 + variant * 113) / 41.0));
                var index = row + x * 4;
                pixels[index] = (byte)Math.Clamp(x * 255 / Math.Max(1, width - 1) + noise + checker, 0, 255);
                pixels[index + 1] = (byte)Math.Clamp(y * 255 / Math.Max(1, height - 1) + radial + noise, 0, 255);
                pixels[index + 2] = (byte)Math.Clamp((x + y) * 255 /
                    Math.Max(1, width + height - 2) + checker + radial, 0, 255);
                pixels[index + 3] = 255;
            }
        });
        return pixels;
    }

    private static void Write(
        MagickImage image, string root, string stem,
        MagickFormat format, string extension, uint quality)
    {
        var folder = System.IO.Path.Combine(root, extension);
        Directory.CreateDirectory(folder);
        image.Format = format;
        image.Quality = quality;
        image.Write(System.IO.Path.Combine(folder, stem + "." + extension));
    }

    private static void WriteJpegPdf(
        string path, byte[] jpeg, int width, int height)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        var offsets = new long[6];
        WriteAscii("%PDF-1.4\n%âãÏÓ\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>");
        offsets[4] = stream.Position;
        WriteAscii($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        stream.Write(jpeg);
        WriteAscii("\nendstream\nendobj\n");
        var content = $"q {width} 0 0 {height} 0 0 cm /Im0 Do Q\n";
        offsets[5] = stream.Position;
        WriteAscii($"5 0 obj\n<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream\nendobj\n");
        var xref = stream.Position;
        WriteAscii("xref\n0 6\n0000000000 65535 f \n");
        for (var index = 1; index <= 5; index++)
            WriteAscii($"{offsets[index]:D10} 00000 n \n");
        WriteAscii($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return;

        void Object(int number, string body)
        {
            offsets[number] = stream.Position;
            WriteAscii($"{number} 0 obj\n{body}\nendobj\n");
        }
        void WriteAscii(string text)
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            stream.Write(bytes);
        }
    }

    public async ValueTask DisposeAsync() =>
        await Task.Run(() => TryDelete(Path)).ConfigureAwait(false);

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { }
    }
}
