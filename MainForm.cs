using System.Drawing.Printing;
using ImageMagick;

namespace CDisplayEx.CSharp;

internal sealed class MainForm : Form
{
    private readonly string? _initialPath;
    private readonly UserSettings _settings = UserSettings.Load();
    private readonly ViewerPanel _viewer = new() { Dock = DockStyle.Fill };
    private readonly MenuStrip _menu = new();
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly Panel _sliderPanel = new() { Height = 32, Dock = DockStyle.Bottom };
    private readonly TrackBar _slider = new() { Dock = DockStyle.Fill, TickStyle = TickStyle.None, Minimum = 0 };
    private readonly TextBox _pageBox = new() { Dock = DockStyle.Right, Width = 64, TextAlign = HorizontalAlignment.Center };
    private readonly Label _pageCount = new() { Dock = DockStyle.Right, Width = 72, TextAlign = ContentAlignment.MiddleCenter };
    private readonly FlowLayoutPanel _thumbs = new()
    {
        Dock = DockStyle.Bottom, Height = 118, AutoScroll = true, WrapContents = false,
        FlowDirection = FlowDirection.LeftToRight, BackColor = Color.FromArgb(28, 28, 28), Visible = false
    };
    private readonly System.Windows.Forms.Timer _slideTimer = new() { Interval = 4000 };
    private readonly Dictionary<int, int> _rotations = [];

    private Book? _book;
    private int _pageIndex;
    private bool _doublePage;
    private bool _cover;
    private bool _forwardOnePage;
    private bool _fullScreen;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;
    private ToolStripMenuItem? _doublePageItem;
    private ToolStripMenuItem? _japaneseItem;
    private ToolStripMenuItem? _sliderItem;
    private ToolStripMenuItem? _thumbItem;
    private ToolStripMenuItem? _shadowItem;
    private ToolStripMenuItem? _toolbarItem;
    private ToolStripMenuItem? _autoSlideItem;

    public MainForm(string? initialPath)
    {
        _initialPath = initialPath;
        Text = "CDisplayEx";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        AllowDrop = true;
        Icon = SystemIcons.Application;

        BuildMenu();
        BuildToolbar();
        BuildSlider();
        Controls.Add(_viewer);
        Controls.Add(_thumbs);
        Controls.Add(_sliderPanel);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _doublePage = _settings.DoublePage;
        _viewer.JapaneseMode = _settings.JapaneseMode;
        _viewer.ShowShadow = _settings.Shadow;
        _viewer.FitToScreen = _settings.FitToScreen;
        _sliderPanel.Visible = _settings.SliderVisible;
        _toolbar.Visible = _settings.ToolbarVisible;
        SyncChecks();

        _slider.Scroll += (_, _) => GoToPage(_slider.Value);
        _pageBox.KeyDown += PageBoxKeyDown;
        _slideTimer.Tick += (_, _) => NextPage();
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        KeyDown += MainFormKeyDown;
        FormClosing += (_, _) => SaveSettings();
        Shown += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_initialPath)) BeginInvoke(() => TryOpen(_initialPath));
        };
    }

    private void BuildMenu()
    {
        var file = Menu("&File",
            Item("&Load Files...", (_, _) => OpenFiles(), Keys.Control | Keys.L),
            Item("L&oad Next File", (_, _) => OpenAdjacentBook(1)),
            Item("Lo&ad Previous File", (_, _) => OpenAdjacentBook(-1)),
            Item("O&pen Folder...", (_, _) => OpenFolder(), Keys.Control | Keys.O),
            Sep(),
            Item("&Save to File...", (_, _) => SaveCurrent(), Keys.Control | Keys.S),
            Item("Sa&ve to Clipboard...", (_, _) => CopyCurrent(), Keys.Control | Keys.C),
            Item("Pr&int...", (_, _) => PrintCurrent()),
            Item("Pri&nter Setup...", (_, _) => ShowPrinterSetup()),
            Sep(),
            Item("&Full Screen", (_, _) => ToggleFullscreen(), Keys.F11),
            Item("&Minimize", (_, _) => WindowState = FormWindowState.Minimized, Keys.Control | Keys.M),
            Item("E&xit", (_, _) => Close(), Keys.Control | Keys.Q));

        _thumbItem = CheckItem("&Thumbnails", (_, _) => _thumbs.Visible = _thumbItem!.Checked, Keys.Control | Keys.T);
        _sliderItem = CheckItem("&Slider", (_, _) => _sliderPanel.Visible = _sliderItem!.Checked, Keys.Control | Keys.I);
        _autoSlideItem = CheckItem("&Automatic Slideshow", (_, _) =>
        {
            _slideTimer.Enabled = _autoSlideItem!.Checked;
        }, Keys.Control | Keys.H);
        var read = Menu("&Read",
            Item("&First Page", (_, _) => GoToPage(0)),
            Item("&Last Page", (_, _) => GoToPage((_book?.Pages.Count ?? 1) - 1)),
            Item("&Next Page", (_, _) => NextPage()),
            Item("&Previous Page", (_, _) => PreviousPage()),
            Sep(),
            Item("S&croll Up", (_, _) => ScrollViewer(0, -80)),
            Item("Sc&roll Down", (_, _) => ScrollViewer(0, 80)),
            Item("Scr&oll Left", (_, _) => ScrollViewer(-80, 0)),
            Item("Scroll R&ight", (_, _) => ScrollViewer(80, 0)),
            Sep(), _thumbItem, _sliderItem, _autoSlideItem);

        _doublePageItem = CheckItem("&Double Page", (_, _) => { _doublePage = _doublePageItem!.Checked; ShowPage(); }, Keys.Control | Keys.D);
        var coverItem = CheckItem("&Cover", (s, _) => { _cover = ((ToolStripMenuItem)s!).Checked; ShowPage(); });
        _shadowItem = CheckItem("&Shadow", (_, _) => { _viewer.ShowShadow = _shadowItem!.Checked; _viewer.Invalidate(); });
        var forwardItem = CheckItem("&Forward One Page", (s, _) => _forwardOnePage = ((ToolStripMenuItem)s!).Checked);
        var doubleMenu = Menu("&Double Page", _doublePageItem, coverItem, _shadowItem, forwardItem);
        var zoom = Menu("&Zoom",
            Item("&Zoom In", (_, _) => _viewer.ZoomBy(1.15f), Keys.Control | Keys.Oemplus),
            Item("Z&oom Out", (_, _) => _viewer.ZoomBy(1 / 1.15f), Keys.Control | Keys.OemMinus),
            Item("Zoo&m 100%", (_, _) => _viewer.SetZoom(1f)),
            Item("Zoom &125%", (_, _) => _viewer.SetZoom(1.25f)),
            Item("Zoom 1&50%", (_, _) => _viewer.SetZoom(1.5f)),
            Item("Zoom &200%", (_, _) => _viewer.SetZoom(2f)),
            Item("Zoom &400%", (_, _) => _viewer.SetZoom(4f)));
        var rotate = Menu("&Rotate",
            Item("&Left", (_, _) => RotateCurrent(-90)),
            Item("&Right", (_, _) => RotateCurrent(90)),
            Item("R&eset", (_, _) => ResetRotation()));
        var fit = Menu("&Fit",
            Item("Fi&t to Screen", (_, _) => _viewer.ResetFit(), Keys.Control | Keys.F),
            Item("&Under Height", (_, _) => _viewer.ResetFit()),
            Item("U&nder Width", (_, _) => _viewer.ResetFit()),
            Item("&Over Height", (_, _) => _viewer.ResetFit()),
            Item("O&ver Width", (_, _) => _viewer.ResetFit()));

        _japaneseItem = CheckItem("&Japanese Mode", (_, _) =>
        {
            _viewer.JapaneseMode = _japaneseItem!.Checked;
            _viewer.SwapReadingDirection();
        }, Keys.Control | Keys.J);
        _toolbarItem = CheckItem("Too&lbar Visible", (_, _) => _toolbar.Visible = _toolbarItem!.Checked);
        var options = Menu("&Options", doubleMenu, zoom, rotate, fit, Sep(),
            _japaneseItem,
            CheckItem("&Scrollbars", (_, _) => _viewer.AutoScroll = ((ToolStripMenuItem)_viewer.ContextMenuStrip!.Items[0]).Checked),
            _toolbarItem,
            Item("&Configure...", (_, _) => ShowConfiguration()));

        var help = Menu("&Help",
            Item("&Website", (_, _) => OpenWebsite()),
            Item("&About", (_, _) => MessageBox.Show(this,
                "CDisplayEx C#\nClean-room reader rebuilt from the original UI behavior.\n\nHistory, bookmarks, resume, color correction and Explorer shell integration are intentionally omitted.",
                "About", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.AddRange([file, read, options, help]);
        _viewer.ContextMenuStrip = new ContextMenuStrip();
        _viewer.ContextMenuStrip.Items.Add(CheckItem("Scrollbars", (_, _) => { }));
    }

    private void BuildToolbar()
    {
        AddTool("Open", (_, _) => OpenFiles());
        AddTool("Folder", (_, _) => OpenFolder());
        _toolbar.Items.Add(new ToolStripSeparator());
        AddTool("|<", (_, _) => GoToPage(0));
        AddTool("<", (_, _) => PreviousPage());
        AddTool(">", (_, _) => NextPage());
        AddTool(">|", (_, _) => GoToPage((_book?.Pages.Count ?? 1) - 1));
        _toolbar.Items.Add(new ToolStripSeparator());
        AddTool("Fit", (_, _) => _viewer.ResetFit());
        AddTool("Double", (_, _) => { _doublePage = !_doublePage; ShowPage(); });
        AddTool("RTL", (_, _) => { _viewer.JapaneseMode = !_viewer.JapaneseMode; _viewer.SwapReadingDirection(); });
        AddTool("Fullscreen", (_, _) => ToggleFullscreen());
    }

    private void BuildSlider()
    {
        _pageCount.Text = "/ 0";
        _pageBox.Text = "0";
        _sliderPanel.Controls.Add(_slider);
        _sliderPanel.Controls.Add(_pageCount);
        _sliderPanel.Controls.Add(_pageBox);
    }

    private void OpenFiles()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Comic books and images|*.cbz;*.cbr;*.cb7;*.zip;*.rar;*.7z;*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*",
            Multiselect = false, Title = "Load Files"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) TryOpen(dialog.FileName);
    }

    private void OpenFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Open image folder", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) TryOpen(dialog.SelectedPath);
    }

    private void TryOpen(string path)
    {
        try
        {
            UseWaitCursor = true;
            _book = Book.Open(path);
            _pageIndex = File.Exists(path) && Book.IsSupportedImage(path) ? _book.IndexOfFile(path) : 0;
            _rotations.Clear();
            _slider.Maximum = Math.Max(0, _book.Pages.Count - 1);
            _sliderPanel.Visible = true;
            if (_sliderItem is not null) _sliderItem.Checked = true;
            Text = $"{Path.GetFileName(_book.SourcePath)} - CDisplayEx";
            BuildThumbnails();
            ShowPage();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open book", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { UseWaitCursor = false; }
    }

    private void ShowPage()
    {
        if (_book is null || _book.Pages.Count == 0) { _viewer.SetPages(null, null); return; }
        _pageIndex = Math.Clamp(_pageIndex, 0, _book.Pages.Count - 1);
        var secondIndex = GetSecondPageIndex();
        var first = LoadImage(_pageIndex);
        var second = secondIndex >= 0 ? LoadImage(secondIndex) : null;
        _viewer.SetPages(first, second);
        _slider.Value = _pageIndex;
        _pageBox.Text = (_pageIndex + 1).ToString();
        _pageCount.Text = $"/ {_book.Pages.Count}";
        HighlightThumbnail();
    }

    private int GetSecondPageIndex()
    {
        if (!_doublePage || _book is null) return -1;
        if (_cover && _pageIndex == 0) return -1;
        var value = _pageIndex + 1;
        return value < _book.Pages.Count ? value : -1;
    }

    private Image LoadImage(int index)
    {
        using var stream = _book!.Pages[index].Open();
        Bitmap result;
        try
        {
            using var source = Image.FromStream(stream);
            result = new Bitmap(source);
        }
        catch (ArgumentException)
        {
            stream.Position = 0;
            using var magick = new MagickImage(stream);
            magick.Format = MagickFormat.Bmp;
            using var converted = new MemoryStream();
            magick.Write(converted);
            converted.Position = 0;
            using var decoded = new Bitmap(converted);
            result = new Bitmap(decoded);
        }
        if (_rotations.TryGetValue(index, out var rotation))
        {
            var normalized = (rotation % 360 + 360) % 360;
            result.RotateFlip(normalized switch
            {
                90 => RotateFlipType.Rotate90FlipNone,
                180 => RotateFlipType.Rotate180FlipNone,
                270 => RotateFlipType.Rotate270FlipNone,
                _ => RotateFlipType.RotateNoneFlipNone
            });
        }
        return result;
    }

    private void NextPage()
    {
        if (_book is null) return;
        var step = _doublePage && !_forwardOnePage && !(_cover && _pageIndex == 0) ? 2 : 1;
        GoToPage(Math.Min(_book.Pages.Count - 1, _pageIndex + step));
    }

    private void PreviousPage()
    {
        if (_book is null) return;
        var step = _doublePage && !_forwardOnePage ? 2 : 1;
        GoToPage(Math.Max(0, _pageIndex - step));
    }

    private void GoToPage(int index)
    {
        if (_book is null) return;
        _pageIndex = Math.Clamp(index, 0, _book.Pages.Count - 1);
        ShowPage();
    }

    private void RotateCurrent(int degrees)
    {
        if (_book is null) return;
        _rotations[_pageIndex] = _rotations.GetValueOrDefault(_pageIndex) + degrees;
        var second = GetSecondPageIndex();
        if (second >= 0) _rotations[second] = _rotations.GetValueOrDefault(second) + degrees;
        ShowPage();
    }

    private void ResetRotation()
    {
        _rotations.Remove(_pageIndex);
        var second = GetSecondPageIndex();
        if (second >= 0) _rotations.Remove(second);
        ShowPage();
    }

    private void SaveCurrent()
    {
        if (_viewer.CurrentImage is null) return;
        using var dialog = new SaveFileDialog { Filter = "PNG image|*.png|JPEG image|*.jpg", FileName = $"page-{_pageIndex + 1}.png" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _viewer.CurrentImage.Save(dialog.FileName);
    }

    private void CopyCurrent()
    {
        if (_viewer.CurrentImage is not null) Clipboard.SetImage(_viewer.CurrentImage);
    }

    private void PrintCurrent()
    {
        if (_viewer.CurrentImage is null) return;
        using var document = new PrintDocument();
        document.PrintPage += (_, e) =>
        {
            var image = _viewer.CurrentImage;
            if (image is null) return;
            var scale = Math.Min((float)e.MarginBounds.Width / image.Width, (float)e.MarginBounds.Height / image.Height);
            var size = new Size((int)(image.Width * scale), (int)(image.Height * scale));
            e.Graphics?.DrawImage(image, new Rectangle(e.MarginBounds.Left, e.MarginBounds.Top, size.Width, size.Height));
        };
        using var dialog = new PrintDialog { Document = document };
        if (dialog.ShowDialog(this) == DialogResult.OK) document.Print();
    }

    private void ShowPrinterSetup()
    {
        using var document = new PrintDocument();
        using var dialog = new PageSetupDialog { Document = document };
        dialog.ShowDialog(this);
    }

    private void BuildThumbnails()
    {
        _thumbs.SuspendLayout();
        foreach (Control control in _thumbs.Controls) control.Dispose();
        _thumbs.Controls.Clear();
        if (_book is null) { _thumbs.ResumeLayout(); return; }
        for (var i = 0; i < Math.Min(_book.Pages.Count, 300); i++)
        {
            var page = i;
            var button = new Button { Width = 72, Height = 92, Text = (i + 1).ToString(), TextAlign = ContentAlignment.BottomCenter, Tag = i };
            try
            {
                using var image = LoadImage(i);
                button.BackgroundImage = image.GetThumbnailImage(64, 68, null, IntPtr.Zero);
                button.BackgroundImageLayout = ImageLayout.Zoom;
            }
            catch { }
            button.Click += (_, _) => GoToPage(page);
            _thumbs.Controls.Add(button);
        }
        _thumbs.ResumeLayout();
    }

    private void HighlightThumbnail()
    {
        foreach (Control control in _thumbs.Controls)
            control.BackColor = control.Tag is int page && page == _pageIndex ? Color.Goldenrod : SystemColors.Control;
    }

    private void OpenAdjacentBook(int direction)
    {
        if (_book is null || Directory.Exists(_book.SourcePath)) return;
        var folder = Path.GetDirectoryName(_book.SourcePath)!;
        var books = Directory.EnumerateFiles(folder).Where(Book.IsSupportedBook)
            .Where(p => !Book.IsSupportedImage(p)).OrderBy(p => p, NaturalStringComparer.Instance).ToArray();
        var index = Array.FindIndex(books, p => p.Equals(_book.SourcePath, StringComparison.OrdinalIgnoreCase));
        var next = index + direction;
        if (index >= 0 && next >= 0 && next < books.Length) TryOpen(books[next]);
    }

    private void ToggleFullscreen()
    {
        if (!_fullScreen)
        {
            _savedBorder = FormBorderStyle;
            _savedState = WindowState;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _savedBorder;
            WindowState = _savedState;
        }
        _fullScreen = !_fullScreen;
    }

    private void ScrollViewer(int dx, int dy)
    {
        var p = _viewer.AutoScrollPosition;
        _viewer.AutoScrollPosition = new Point(-p.X + dx, -p.Y + dy);
    }

    private void PageBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && int.TryParse(_pageBox.Text, out var page))
        {
            GoToPage(page - 1);
            e.SuppressKeyPress = true;
        }
    }

    private void MainFormKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Home: GoToPage(0); break;
            case Keys.End: GoToPage((_book?.Pages.Count ?? 1) - 1); break;
            case Keys.PageDown: NextPage(); break;
            case Keys.PageUp: PreviousPage(); break;
            case Keys.Up: ScrollViewer(0, -80); break;
            case Keys.Down: ScrollViewer(0, 80); break;
            case Keys.Left: ScrollViewer(-80, 0); break;
            case Keys.Right: ScrollViewer(80, 0); break;
            default: return;
        }
        e.Handled = true;
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) TryOpen(files[0]);
    }

    private void ShowConfiguration()
    {
        MessageBox.Show(this,
            "Configuration is intentionally compact in this build.\n\nDisplay choices are saved automatically. No history, bookmarks, resume state, gamma, white balance or vibrance data is stored.",
            "Configure", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void OpenWebsite()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.cdisplayex.com") { UseShellExecute = true }); }
        catch { }
    }

    private void SaveSettings()
    {
        _settings.DoublePage = _doublePage;
        _settings.JapaneseMode = _viewer.JapaneseMode;
        _settings.SliderVisible = _sliderPanel.Visible;
        _settings.ToolbarVisible = _toolbar.Visible;
        _settings.Shadow = _viewer.ShowShadow;
        _settings.FitToScreen = _viewer.FitToScreen;
        _settings.Save();
    }

    private void SyncChecks()
    {
        if (_doublePageItem is not null) _doublePageItem.Checked = _doublePage;
        if (_japaneseItem is not null) _japaneseItem.Checked = _viewer.JapaneseMode;
        if (_sliderItem is not null) _sliderItem.Checked = _sliderPanel.Visible;
        if (_shadowItem is not null) _shadowItem.Checked = _viewer.ShowShadow;
        if (_toolbarItem is not null) _toolbarItem.Checked = _toolbar.Visible;
    }

    private void AddTool(string text, EventHandler click) => _toolbar.Items.Add(new ToolStripButton(text, null, click));
    private static ToolStripMenuItem Menu(string text, params ToolStripItem[] items) => new(text, null, items);
    private static ToolStripMenuItem Item(string text, EventHandler click, Keys shortcut = Keys.None) => new(text, null, click) { ShortcutKeys = shortcut };
    private static ToolStripMenuItem CheckItem(string text, EventHandler click, Keys shortcut = Keys.None) => new(text, null, click) { CheckOnClick = true, ShortcutKeys = shortcut };
    private static ToolStripMenuItem Disabled(string text) => new(text) { Enabled = false };
    private static ToolStripSeparator Sep() => new();
}
