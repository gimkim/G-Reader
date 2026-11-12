using System.Runtime.InteropServices;
using System.Buffers.Binary;
using System.Text;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal static class ColorProfileService
{
    private const int IcmOn = 2;
    private const int IccHeaderLength = 128;
    private const int IccTagTableHeaderLength = 4;
    private const int IccTagRecordLength = 12;
    private const int IccTagDataHeaderLength = 8;

    public static byte[]? ReadEmbeddedProfile(PageEntry page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var stream = page.OpenStream(token);
            using var image = new MagickImage();
            image.Ping(stream);
            token.ThrowIfCancellationRequested();
            var profile = image.GetColorProfile()?.ToByteArray();
            if (profile is null) return null;
            if (!TryValidateEmbeddedProfile(profile, out var declaredLength, out var reason))
            {
                ExtendedDiagnostics.Breadcrumb(
                    $"Embedded ICC rejected; page={page.Name}; bytes={profile.Length}; reason={reason}");
                return null;
            }

            // ICC payloads may be padded by a container. Do not pass bytes beyond
            // the profile's declared boundary to Direct2D's color-context parser.
            return declaredLength == profile.Length
                ? profile
                : profile.AsSpan(0, declaredLength).ToArray();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    internal static bool TryValidateEmbeddedProfile(byte[] profile,
        out int declaredLength, out string reason)
    {
        declaredLength = 0;
        reason = string.Empty;
        if (profile.Length < IccHeaderLength + IccTagTableHeaderLength)
            return Invalid("shorter than the ICC header and tag count", out reason);

        var data = profile.AsSpan();
        var declared = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (declared < IccHeaderLength + IccTagTableHeaderLength || declared > profile.Length)
            return Invalid($"declared size {declared} is outside payload size {profile.Length}",
                out reason);
        if (!data.Slice(36, 4).SequenceEqual("acsp"u8))
            return Invalid("missing ICC profile signature", out reason);

        var tagCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(IccHeaderLength, 4));
        var tagTableEnd = (ulong)IccHeaderLength + IccTagTableHeaderLength +
            (ulong)tagCount * IccTagRecordLength;
        if (tagTableEnd > declared)
            return Invalid($"tag table with {tagCount} entries exceeds declared profile size",
                out reason);

        // Real ICC profiles contain a small tag table. Bound hostile/corrupt
        // metadata so validation itself cannot become a CPU or allocation sink.
        if (tagCount > 4096)
            return Invalid($"tag count {tagCount} exceeds the safety limit", out reason);

        var ranges = new List<IccTagRange>((int)tagCount);
        for (uint index = 0; index < tagCount; index++)
        {
            var recordOffset = IccHeaderLength + IccTagTableHeaderLength +
                checked((int)index * IccTagRecordLength);
            var record = data.Slice(recordOffset, IccTagRecordLength);
            var signature = Encoding.ASCII.GetString(record.Slice(0, 4));
            var offset = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(4, 4));
            var length = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8, 4));
            var end = (ulong)offset + length;

            if (length < IccTagDataHeaderLength)
                return Invalid($"tag {signature} has invalid size {length}", out reason);
            if (offset < tagTableEnd || end > declared)
                return Invalid(
                    $"tag {signature} range {offset}..{end} is outside tag-data bounds {tagTableEnd}..{declared}",
                    out reason);

            ranges.Add(new IccTagRange(signature, offset, end));
        }

        ranges.Sort(static (left, right) =>
        {
            var byOffset = left.Offset.CompareTo(right.Offset);
            return byOffset != 0 ? byOffset : left.End.CompareTo(right.End);
        });
        for (var index = 1; index < ranges.Count; index++)
        {
            var previous = ranges[index - 1];
            var current = ranges[index];
            var overlaps = current.Offset < previous.End;
            var sharesExactData = current.Offset == previous.Offset && current.End == previous.End;
            if (overlaps && !sharesExactData)
                return Invalid(
                    $"tag {current.Signature} range {current.Offset}..{current.End} overlaps tag {previous.Signature} range {previous.Offset}..{previous.End}",
                    out reason);
        }

        declaredLength = checked((int)declared);
        return true;
    }

    private static bool Invalid(string message, out string reason)
    {
        reason = message;
        return false;
    }

    private readonly record struct IccTagRange(string Signature, ulong Offset, ulong End);

    public static byte[]? ReadMonitorProfile(string deviceName)
    {
        var dc = CreateDC("DISPLAY", deviceName, null, IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        try
        {
            _ = SetICMMode(dc, IcmOn);
            uint length = 0;
            _ = GetICMProfile(dc, ref length, null);
            if (length == 0 || length > 32768) return null;
            var path = new StringBuilder((int)length);
            if (!GetICMProfile(dc, ref length, path)) return null;
            var profilePath = path.ToString();
            if (!Path.IsPathRooted(profilePath))
                profilePath = Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.System), "spool", "drivers", "color", profilePath);
            return File.ReadAllBytes(profilePath);
        }
        catch { return null; }
        finally { _ = DeleteDC(dc); }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDC(string driver, string device,
        string? output, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int SetICMMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetICMProfile(IntPtr dc, ref uint length,
        StringBuilder? filename);
}
