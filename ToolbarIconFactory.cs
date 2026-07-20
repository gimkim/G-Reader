using System.Drawing.Drawing2D;

namespace CDisplayEx.CSharp;

internal static class ToolbarIconFactory
{
    private static readonly Color Ink = Color.FromArgb(225, 231, 242);
    private static readonly Color Accent = Color.FromArgb(91, 153, 255);

    public static Image OpenFile() => Draw(g =>
    {
        using var pen = Pen(Ink, 2);
        using var fill = new SolidBrush(Color.FromArgb(58, 91, 145));
        g.FillRectangle(fill, 5, 4, 12, 16); g.DrawRectangle(pen, 5, 4, 12, 16);
        g.DrawLine(pen, 12, 4, 17, 9); g.DrawLine(pen, 12, 4, 12, 9); g.DrawLine(pen, 12, 9, 17, 9);
        using var plus = Pen(Accent, 2); g.DrawLine(plus, 16, 16, 22, 16); g.DrawLine(plus, 19, 13, 19, 19);
    });

    public static Image OpenFolder() => Draw(g =>
    {
        using var pen = Pen(Ink, 2); using var fill = new SolidBrush(Color.FromArgb(72, 120, 196));
        var path = new GraphicsPath();
        path.AddPolygon(new Point[] { new(2, 7), new(9, 7), new(11, 10), new(22, 10), new(20, 20), new(3, 20) });
        g.FillPath(fill, path); g.DrawPath(pen, path);
    });

    public static Image OpenRandom() => Draw(g =>
    {
        using var pen = Pen(Ink, 1.8f);
        using var accent = Pen(Accent, 2.1f);
        g.DrawLine(pen, 3, 6, 7, 6);
        g.DrawBezier(pen, new Point(7, 6), new Point(11, 6), new Point(12, 18), new Point(17, 18));
        g.DrawLine(accent, 17, 18, 21, 18);
        g.DrawLine(accent, 21, 18, 18, 15);
        g.DrawLine(accent, 21, 18, 18, 21);
        g.DrawLine(pen, 3, 18, 7, 18);
        g.DrawBezier(pen, new Point(7, 18), new Point(11, 18), new Point(12, 6), new Point(17, 6));
        g.DrawLine(accent, 17, 6, 21, 6);
        g.DrawLine(accent, 21, 6, 18, 3);
        g.DrawLine(accent, 21, 6, 18, 9);
    });

    public static Image OpenInExplorer() => Draw(g =>
    {
        using var pen = Pen(Ink, 1.8f);
        using var fill = new SolidBrush(Color.FromArgb(72, 120, 196));
        var folder = new GraphicsPath();
        folder.AddPolygon(new Point[]
        {
            new(2, 8), new(9, 8), new(11, 11), new(22, 11), new(20, 20), new(3, 20)
        });
        g.FillPath(fill, folder);
        g.DrawPath(pen, folder);
        using var accent = Pen(Accent, 2.1f);
        g.DrawLine(accent, 10, 4, 20, 4);
        g.DrawLine(accent, 20, 4, 20, 10);
        g.DrawLine(accent, 20, 4, 13, 11);
    });

    public static Image MoveUp() => Draw(g =>
    {
        using var folderPen = Pen(Ink, 1.7f);
        using var folderFill = new SolidBrush(Color.FromArgb(72, 120, 196));
        var folder = new GraphicsPath();
        folder.AddPolygon(new Point[]
        {
            new(2, 9), new(9, 9), new(11, 12), new(22, 12), new(20, 21), new(3, 21)
        });
        g.FillPath(folderFill, folder);
        g.DrawPath(folderPen, folder);
        using var arrow = Pen(Accent, 2.5f);
        g.DrawLine(arrow, 12, 15, 12, 3);
        g.DrawLine(arrow, 12, 3, 7, 8);
        g.DrawLine(arrow, 12, 3, 17, 8);
    });

    public static Image Boundary(bool pointsRight, bool start) => Draw(g =>
    {
        using var pen = Pen(Ink, 2.2f); using var accent = Pen(Accent, 2.5f);
        var direction = pointsRight ? 1 : -1;
        var barX = start ? (pointsRight ? 4 : 20) : (pointsRight ? 20 : 4);
        var tipX = pointsRight ? 18 : 6;
        var tailX = pointsRight ? 7 : 17;
        g.DrawLine(accent, barX, 4, barX, 20);
        g.DrawLine(pen, tailX, 12, tipX, 12);
        g.DrawLine(pen, tipX, 12, tipX - direction * 5, 7);
        g.DrawLine(pen, tipX, 12, tipX - direction * 5, 17);
    });

    public static Image Arrow(bool right) => Draw(g =>
    {
        using var pen = Pen(Ink, 2.5f); var tip = right ? 18 : 6; var tail = right ? 5 : 19; var d = right ? 1 : -1;
        g.DrawLine(pen, tail, 12, tip, 12); g.DrawLine(pen, tip, 12, tip - d * 6, 6); g.DrawLine(pen, tip, 12, tip - d * 6, 18);
    });

    public static Image Direction(bool rtl) => Draw(g =>
    {
        using var pen = Pen(Accent, 2.3f); var right = !rtl; var tip = right ? 20 : 4; var tail = right ? 4 : 20; var d = right ? 1 : -1;
        g.DrawLine(pen, tail, 12, tip, 12); g.DrawLine(pen, tip, 12, tip - d * 5, 7); g.DrawLine(pen, tip, 12, tip - d * 5, 17);
    });

    public static Image PageLayout(int mode) => Draw(g =>
    {
        using var pen = Pen(Ink, 1.7f);
        using var fill = new SolidBrush(Color.FromArgb(55, 75, 108));
        if (mode == 0)
        {
            var page = new Rectangle(6, 3, 12, 18);
            g.FillRectangle(fill, page); g.DrawRectangle(pen, page);
        }
        else
        {
            var left = new Rectangle(2, 4, 9, 16);
            var right = new Rectangle(13, 4, 9, 16);
            g.FillRectangle(fill, left); g.FillRectangle(fill, right);
            g.DrawRectangle(pen, left); g.DrawRectangle(pen, right);
            if (mode == 2)
            {
                using var accent = Pen(Accent, 2.2f);
                g.DrawLine(accent, 3, 2, 10, 2);
                g.DrawLine(accent, 3, 2, 3, 6);
            }
        }
    });

    public static Image AutoSingleLandscape(bool enabled) => Draw(g =>
    {
        using var pen = Pen(enabled ? Accent : Ink, 1.8f);
        using var fill = new SolidBrush(enabled
            ? Color.FromArgb(55, 91, 145)
            : Color.FromArgb(55, 60, 70));
        var page = new Rectangle(2, 7, 20, 11);
        g.FillRectangle(fill, page);
        g.DrawRectangle(pen, page);
        using var mark = Pen(enabled ? Color.FromArgb(115, 225, 155) : Color.FromArgb(145, 150, 160), 2.2f);
        if (enabled)
        {
            g.DrawLine(mark, 7, 13, 10, 16);
            g.DrawLine(mark, 10, 16, 17, 9);
        }
        else
        {
            g.DrawLine(mark, 8, 10, 16, 16);
            g.DrawLine(mark, 16, 10, 8, 16);
        }
    });

    public static Image ThumbnailMode(bool gridMode) => Draw(g =>
    {
        using var pen = Pen(gridMode ? Accent : Ink, 1.7f);
        using var fill = new SolidBrush(Color.FromArgb(55, 75, 108));
        if (!gridMode)
        {
            var page = new Rectangle(5, 3, 14, 18);
            g.FillRectangle(fill, page);
            g.DrawRectangle(pen, page);
        }
        else
        {
            for (var row = 0; row < 2; row++)
                for (var column = 0; column < 3; column++)
                {
                    var cell = new Rectangle(2 + column * 7, 4 + row * 9, 5, 7);
                    g.FillRectangle(fill, cell);
                    g.DrawRectangle(pen, cell);
                }
        }
    });

    public static Image Settings() => Draw(g =>
    {
        using var pen = Pen(Ink, 2); using var fill = new SolidBrush(Color.FromArgb(70, 98, 145));
        g.FillEllipse(fill, 5, 5, 14, 14); g.DrawEllipse(pen, 5, 5, 14, 14); g.FillEllipse(Brushes.Black, 10, 10, 4, 4);
        for (var i = 0; i < 8; i++) { var a = i * Math.PI / 4; g.DrawLine(pen, 12 + (int)(7 * Math.Cos(a)), 12 + (int)(7 * Math.Sin(a)), 12 + (int)(10 * Math.Cos(a)), 12 + (int)(10 * Math.Sin(a))); }
    });

    private static Bitmap Draw(Action<Graphics> painter)
    {
        var bitmap = new Bitmap(24, 24, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        painter(graphics);
        return bitmap;
    }

    private static Pen Pen(Color color, float width) => new(color, width)
    {
        StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
    };
}
