using System.Drawing.Drawing2D;

namespace CDisplayEx.CSharp;

internal sealed class ThumbnailGridView : Panel
{
    private readonly Dictionary<int, Bitmap> _thumbnails = [];
    private int _pageCount;
    private int _imagesPerRow = 6;
    private int _selectedPage = -1;
    private int _cellWidth = 120;
    private int _cellHeight = 170;

    public event EventHandler<int>? PageActivated;

    public int PageCount => _pageCount;

    public int ImagesPerRow
    {
        get => _imagesPerRow;
        set
        {
            value = Math.Clamp(value, 2, 12);
            if (_imagesPerRow == value) return;
            _imagesPerRow = value;
            UpdateVirtualLayout();
        }
    }

    public int SelectedPage
    {
        get => _selectedPage;
        set
        {
            value = _pageCount == 0 ? -1 : Math.Clamp(value, 0, _pageCount - 1);
            if (_selectedPage == value) return;
            var previous = _selectedPage;
            _selectedPage = value;
            InvalidateCell(previous);
            InvalidateCell(value);
        }
    }

    public ThumbnailGridView()
    {
        SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        Dock = DockStyle.Fill;
        AutoScroll = true;
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.FromArgb(26, 28, 33);
        TabStop = true;
        Resize += (_, _) => UpdateVirtualLayout();
    }

    public void ResetPages(int pageCount)
    {
        foreach (var thumbnail in _thumbnails.Values) thumbnail.Dispose();
        _thumbnails.Clear();
        _pageCount = Math.Max(0, pageCount);
        _selectedPage = _pageCount == 0 ? -1 : Math.Clamp(_selectedPage, 0, _pageCount - 1);
        AutoScrollPosition = Point.Empty;
        UpdateVirtualLayout();
    }

    public bool HasThumbnail(int page) => _thumbnails.ContainsKey(page);

    public void SetThumbnail(int page, Bitmap thumbnail)
    {
        if (page < 0 || page >= _pageCount)
        {
            thumbnail.Dispose();
            return;
        }
        if (_thumbnails.Remove(page, out var previous)) previous.Dispose();
        _thumbnails[page] = thumbnail;
        InvalidateCell(page);
    }

    public void EnsurePageVisible(int page)
    {
        if (page < 0 || page >= _pageCount) return;
        var row = page / _imagesPerRow;
        var top = row * _cellHeight;
        var bottom = top + _cellHeight;
        var viewportTop = -AutoScrollPosition.Y;
        var viewportBottom = viewportTop + ClientSize.Height;
        if (top < viewportTop) AutoScrollPosition = new Point(0, top);
        else if (bottom > viewportBottom) AutoScrollPosition = new Point(0, Math.Max(0, bottom - ClientSize.Height));
    }

    public void ScrollByWheel(int delta)
    {
        var notches = delta / Math.Max(1, SystemInformation.MouseWheelScrollDelta);
        if (notches == 0) return;
        var current = -AutoScrollPosition.Y;
        AutoScrollPosition = new Point(0, Math.Max(0, current - notches * Math.Max(48, _cellHeight / 3)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_pageCount == 0) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var scrollY = -AutoScrollPosition.Y;
        var firstRow = Math.Max(0, scrollY / _cellHeight);
        var lastRow = Math.Min((_pageCount - 1) / _imagesPerRow,
            (scrollY + ClientSize.Height) / _cellHeight + 1);
        using var selectedBrush = new SolidBrush(Color.FromArgb(65, 103, 170));
        using var normalBrush = new SolidBrush(Color.FromArgb(42, 45, 52));
        using var borderPen = new Pen(Color.FromArgb(78, 82, 92));
        using var selectedPen = new Pen(Color.FromArgb(107, 166, 255), 2f);
        using var font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = 0; column < _imagesPerRow; column++)
            {
                var page = row * _imagesPerRow + column;
                if (page >= _pageCount) break;
                var bounds = GetCellBounds(page);
                bounds.Offset(0, AutoScrollPosition.Y);
                var selected = page == _selectedPage;
                e.Graphics.FillRectangle(selected ? selectedBrush : normalBrush, bounds);
                e.Graphics.DrawRectangle(selected ? selectedPen : borderPen, bounds);
                var labelHeight = 25;
                var imageArea = Rectangle.Inflate(bounds, -8, -8);
                imageArea.Height -= labelHeight;
                if (_thumbnails.TryGetValue(page, out var thumbnail))
                {
                    var scale = Math.Min((float)imageArea.Width / thumbnail.Width,
                        (float)imageArea.Height / thumbnail.Height);
                    var width = Math.Max(1, (int)(thumbnail.Width * scale));
                    var height = Math.Max(1, (int)(thumbnail.Height * scale));
                    var target = new Rectangle(
                        imageArea.X + (imageArea.Width - width) / 2,
                        imageArea.Y + (imageArea.Height - height) / 2,
                        width, height);
                    e.Graphics.DrawImage(thumbnail, target);
                }
                var label = new Rectangle(bounds.X + 4, bounds.Bottom - labelHeight, bounds.Width - 8, labelHeight - 2);
                TextRenderer.DrawText(e.Graphics, (page + 1).ToString(), font, label, Color.Gainsboro,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left) return;
        var page = HitTestPage(e.Location);
        if (page >= 0) SelectedPage = page;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        var page = HitTestPage(e.Location);
        if (page < 0) return;
        SelectedPage = page;
        PageActivated?.Invoke(this, page);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (var thumbnail in _thumbnails.Values) thumbnail.Dispose();
            _thumbnails.Clear();
        }
        base.Dispose(disposing);
    }

    private void UpdateVirtualLayout()
    {
        if (IsDisposed) return;
        var availableWidth = Math.Max(160, ClientSize.Width - (VerticalScroll.Visible ? SystemInformation.VerticalScrollBarWidth : 0));
        _cellWidth = Math.Max(72, availableWidth / _imagesPerRow);
        _cellHeight = Math.Max(110, (int)((_cellWidth - 14) * 1.35f) + 30);
        var rows = (_pageCount + _imagesPerRow - 1) / _imagesPerRow;
        AutoScrollMinSize = new Size(0, rows * _cellHeight + 8);
        Invalidate();
    }

    private Rectangle GetCellBounds(int page)
    {
        var column = page % _imagesPerRow;
        var row = page / _imagesPerRow;
        return new Rectangle(column * _cellWidth + 5, row * _cellHeight + 5,
            Math.Max(20, _cellWidth - 10), Math.Max(30, _cellHeight - 10));
    }

    private int HitTestPage(Point location)
    {
        var virtualX = location.X;
        var virtualY = location.Y - AutoScrollPosition.Y;
        var column = Math.Clamp(virtualX / Math.Max(1, _cellWidth), 0, _imagesPerRow - 1);
        var row = virtualY / Math.Max(1, _cellHeight);
        var page = row * _imagesPerRow + column;
        return page >= 0 && page < _pageCount && GetCellBounds(page).Contains(virtualX, virtualY)
            ? page
            : -1;
    }

    private void InvalidateCell(int page)
    {
        if (page < 0 || page >= _pageCount) return;
        var bounds = GetCellBounds(page);
        bounds.Offset(0, AutoScrollPosition.Y);
        Invalidate(Rectangle.Inflate(bounds, 3, 3));
    }
}
