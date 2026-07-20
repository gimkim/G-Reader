namespace CDisplayEx.CSharp;

internal sealed class ReaderSettingsDialog : Form
{
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _ahead = CreateMemoryInput();
    private readonly NumericUpDown _behind = CreateMemoryInput();
    private readonly NumericUpDown _previewCache = CreateMemoryInput();
    private readonly NumericUpDown _thumbnailCache = CreateMemoryInput();
    private readonly NumericUpDown _thumbnailFastPreviewCache = CreateMemoryInput();
    private readonly TextBox _persistentCachePath = new() { Width = 420 };
    private readonly NumericUpDown _fullViewDiskCache = CreateMemoryInput();
    private readonly NumericUpDown _thumbnailDiskCache = CreateMemoryInput();
    private readonly Label _diskCacheTotal = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(70, 79, 94)
    };
    private readonly NumericUpDown _thumbnailMaxPreviewSize = CreatePixelInput();
    private readonly NumericUpDown _fastPreviewWorkers = CreateWorkerInput();
    private readonly NumericUpDown _fastPreviewThreads = CreateWorkerInput();
    private readonly NumericUpDown _precacheWorkers = CreateWorkerInput();
    private readonly NumericUpDown _imageMagickThreads = CreateWorkerInput();
    private readonly NumericUpDown _zoomImageMagickThreads = CreateWorkerInput();
    private readonly CheckBox _autoOptimize = new()
    {
        Text = "Automatically optimize cache and worker threads for this computer",
        AutoSize = true, Dock = DockStyle.Fill
    };
    private readonly Label _autoOptimizeSummary = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(70, 79, 94), AutoEllipsis = false,
        MinimumSize = new Size(0, 76), Padding = new Padding(0, 4, 0, 4)
    };
    private readonly ComboBox _autoMove = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Panel _colorPreview = new() { Width = 92, Height = 28, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _randomLibraryPath = new() { Width = 420 };
    private readonly Dictionary<string, HotkeyTextBox> _hotkeyEditors = new(StringComparer.Ordinal);
    private PerformanceProfile _manualPerformance = null!;
    private readonly PerformanceProfile _automaticPerformance;

    public int LanczosQuality => _quality.SelectedIndex;
    public int CacheAheadMB => (int)_ahead.Value;
    public int CacheBehindMB => (int)_behind.Value;
    public int PreviewCacheMB => (int)_previewCache.Value;
    public int ThumbnailCacheMB => (int)_thumbnailCache.Value;
    public int ThumbnailFastPreviewCacheMB => (int)_thumbnailFastPreviewCache.Value;
    public string PersistentCachePath => _persistentCachePath.Text.Trim();
    public int FullViewDiskCacheMB => (int)_fullViewDiskCache.Value;
    public int ThumbnailDiskCacheMB => (int)_thumbnailDiskCache.Value;
    public int ThumbnailMaxPreviewSizePx => (int)_thumbnailMaxPreviewSize.Value;
    public int FastPreviewWorkerCount => (int)_fastPreviewWorkers.Value;
    public int FastPreviewThreadsPerWorker => (int)_fastPreviewThreads.Value;
    public int PrecacheWorkerCount => (int)_precacheWorkers.Value;
    public int ImageMagickThreadsPerImage => (int)_imageMagickThreads.Value;
    public int ZoomImageMagickThreadsPerImage => (int)_zoomImageMagickThreads.Value;
    public bool AutoOptimizePerformance => _autoOptimize.Checked;
    public Color ReaderBackground { get; private set; }
    public int AutoMoveMode => _autoMove.SelectedIndex;
    public string RandomLibraryPath => _randomLibraryPath.Text.Trim();
    public Dictionary<string, int> ToolbarHotkeys => _hotkeyEditors.ToDictionary(
        pair => pair.Key, pair => (int)pair.Value.Shortcut, StringComparer.Ordinal);

    public ReaderSettingsDialog(UserSettings settings)
    {
        _manualPerformance = PerformanceProfile.FromSettings(settings);
        _automaticPerformance = PerformanceProfile.Detect();
        Text = "G Reader Settings";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(1040, 820);
        MinimumSize = new Size(980, 720);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(242, 244, 248);
        AutoScaleMode = AutoScaleMode.Dpi;

        _quality.Items.AddRange([
            "Fast — Lanczos2",
            "Balanced — Lanczos",
            "Sharp — LanczosSharp",
            "Maximum radius — LanczosRadius"
        ]);
        _quality.SelectedIndex = Math.Clamp(settings.LanczosQuality, 0, _quality.Items.Count - 1);
        _ahead.Value = Math.Clamp(settings.CacheAheadMB, (int)_ahead.Minimum, (int)_ahead.Maximum);
        _behind.Value = Math.Clamp(settings.CacheBehindMB, (int)_behind.Minimum, (int)_behind.Maximum);
        _previewCache.Value = Math.Clamp(settings.PreviewCacheMB,
            (int)_previewCache.Minimum, (int)_previewCache.Maximum);
        _thumbnailCache.Value = Math.Clamp(settings.ThumbnailCacheMB,
            (int)_thumbnailCache.Minimum, (int)_thumbnailCache.Maximum);
        _thumbnailFastPreviewCache.Value = Math.Clamp(settings.ThumbnailFastPreviewCacheMB,
            (int)_thumbnailFastPreviewCache.Minimum, (int)_thumbnailFastPreviewCache.Maximum);
        _persistentCachePath.Text = string.IsNullOrWhiteSpace(settings.PersistentCachePath)
            ? UserSettings.DefaultPersistentCachePath
            : settings.PersistentCachePath;
        _fullViewDiskCache.Value = Math.Clamp(settings.FullViewDiskCacheMB,
            (int)_fullViewDiskCache.Minimum, (int)_fullViewDiskCache.Maximum);
        _thumbnailDiskCache.Value = Math.Clamp(settings.ThumbnailDiskCacheMB,
            (int)_thumbnailDiskCache.Minimum, (int)_thumbnailDiskCache.Maximum);
        void UpdateDiskCacheTotal() => _diskCacheTotal.Text =
            $"{_fullViewDiskCache.Value + _thumbnailDiskCache.Value:N0} MB maximum combined";
        _fullViewDiskCache.ValueChanged += (_, _) => UpdateDiskCacheTotal();
        _thumbnailDiskCache.ValueChanged += (_, _) => UpdateDiskCacheTotal();
        UpdateDiskCacheTotal();
        _thumbnailMaxPreviewSize.Value = Math.Clamp(settings.ThumbnailMaxPreviewSizePx,
            (int)_thumbnailMaxPreviewSize.Minimum, (int)_thumbnailMaxPreviewSize.Maximum);
        _fastPreviewWorkers.Value = Math.Clamp(settings.FastPreviewWorkerCount,
            (int)_fastPreviewWorkers.Minimum, (int)_fastPreviewWorkers.Maximum);
        _fastPreviewThreads.Value = Math.Clamp(settings.FastPreviewThreadsPerWorker,
            (int)_fastPreviewThreads.Minimum, (int)_fastPreviewThreads.Maximum);
        _precacheWorkers.Value = Math.Clamp(settings.PrecacheWorkerCount,
            (int)_precacheWorkers.Minimum, (int)_precacheWorkers.Maximum);
        _imageMagickThreads.Value = Math.Clamp(settings.ImageMagickThreadsPerImage,
            (int)_imageMagickThreads.Minimum, (int)_imageMagickThreads.Maximum);
        _zoomImageMagickThreads.Value = Math.Clamp(settings.ZoomImageMagickThreadsPerImage,
            (int)_zoomImageMagickThreads.Minimum, (int)_zoomImageMagickThreads.Maximum);
        _autoOptimize.Checked = settings.AutoOptimizePerformance;
        _autoOptimize.CheckedChanged += (_, _) =>
            ApplyPerformanceMode(captureManual: _autoOptimize.Checked);
        ApplyPerformanceMode(captureManual: false);
        _autoMove.Items.AddRange([
            "No auto move",
            "Auto move — folders only",
            "Auto move — archives/PDFs only",
            "Auto move — both folders and archives/PDFs"
        ]);
        _autoMove.SelectedIndex = Math.Clamp(settings.AutoMoveMode, 0, 3);
        _randomLibraryPath.Text = settings.RandomLibraryPath ?? string.Empty;
        ReaderBackground = Color.FromArgb(settings.BackgroundArgb);
        _colorPreview.BackColor = ReaderBackground;

        var chooseColor = CreateSecondaryButton("Choose…");
        chooseColor.Click += (_, _) => ChooseBackground();
        _colorPreview.Click += (_, _) => ChooseBackground();
        var colorRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        colorRow.Controls.AddRange([_colorPreview, chooseColor]);

        var browseRandomPath = CreateSecondaryButton("Browse…");
        browseRandomPath.Click += (_, _) => ChooseRandomLibraryPath();
        var randomPathRow = CreateStretchButtonRow(_randomLibraryPath, browseRandomPath);

        var browsePersistentCache = CreateSecondaryButton("Browseâ€¦");
        browsePersistentCache.Click += (_, _) => ChoosePersistentCachePath();
        var persistentCachePathRow = CreateStretchButtonRow(
            _persistentCachePath, browsePersistentCache);

        var chooseDefaultViewer = CreateSecondaryButton("Choose defaults…");
        chooseDefaultViewer.Click += (_, _) => ChooseDefaultImageViewer();
        var defaultViewerRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false
        };
        defaultViewerRow.Controls.Add(chooseDefaultViewer);

        var copyAutomatic = CreateSecondaryButton("Copy Auto values to Manual");
        copyAutomatic.MinimumSize = new Size(218, 32);
        copyAutomatic.Click += (_, _) => CopyAutomaticToManual();
        var copyAutomaticRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            Margin = Padding.Empty
        };
        copyAutomaticRow.Controls.Add(copyAutomatic);

        var readerPage = CreateSettingsPage("Reader & Library",
            CreateSection("Image rendering",
                "Choose final resize quality and the canvas color used around fitted pages.",
                ("Lanczos quality", _quality),
                ("Background color", colorRow)),
            CreateSection("Library navigation",
                "At a book boundary, follow the same ordered folder/archive list shown in Thumbnail view.",
                ("Automatic move", _autoMove),
                ("Random library path", randomPathRow)),
            CreateSection("Windows integration",
                "Register G Reader for supported image formats and choose which formats open with it by default.",
                ("Default image viewer", defaultViewerRow)));

        var performancePage = CreateSettingsPage("Cache & Performance",
            CreateSection("Automatic optimization",
                "Tune memory budgets and CPU parallelism from available RAM and logical processor count. Manual values are preserved when Auto is enabled.",
                ("Optimization mode", _autoOptimize),
                ("Active profile", _autoOptimizeSummary),
                ("Manual starting point", copyAutomaticRow)),
            CreateSection("Full-page cache",
                "Memory budgets for resized reading pages. Ahead and behind are soft limits; cleanup is deferred to keep navigation responsive.",
                ("Ahead cache (MB)", _ahead),
                ("Behind cache (MB)", _behind),
                ("Fast preview cache (MB)", _previewCache)),
            CreateSection("Thumbnail cache",
                "Thumbnail previews have independent fast and final-quality memory budgets.",
                ("Lanczos thumbnail cache (MB)", _thumbnailCache),
                ("Fast thumbnail cache (MB)", _thumbnailFastPreviewCache),
                ("Maximum preview edge (px)", _thumbnailMaxPreviewSize)),
            CreateSection("Persistent disk cache",
                "Previously generated previews survive closing the app. Full-view and thumbnail files are trimmed independently in the background.",
                ("Cache location", persistentCachePathRow),
                ("Full-view preview quota (MB)", _fullViewDiskCache),
                ("Thumbnail quota (MB)", _thumbnailDiskCache),
                ("Total disk allowance", _diskCacheTotal)),
            CreateSection("Processing threads",
                "These values control parallel background resizing. Higher values use more CPU and memory bandwidth.",
                ("Fast preview workers", _fastPreviewWorkers),
                ("CPU threads per fast worker", _fastPreviewThreads),
                ("Pre-cache workers", _precacheWorkers),
                ("Batch Lanczos threads per image", _imageMagickThreads),
                ("Zoom Lanczos threads", _zoomImageMagickThreads)));

        var hotkeyTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Padding = new Padding(24, 16, 24, 18), BackColor = Color.White
        };
        hotkeyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        hotkeyTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        foreach (var definition in ToolbarHotkeyCatalog.All)
        {
            var editor = new HotkeyTextBox
            {
                Shortcut = ToolbarHotkeyCatalog.GetShortcut(settings.ToolbarHotkeys, definition.Id),
                Margin = new Padding(4, 5, 4, 5)
            };
            _hotkeyEditors[definition.Id] = editor;
            var row = hotkeyTable.RowCount++;
            hotkeyTable.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            hotkeyTable.Controls.Add(new Label
            {
                Text = definition.Label, Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4)
            }, 0, row);
            hotkeyTable.Controls.Add(editor, 1, row);
        }

        var hotkeyHint = new Label
        {
            Text = "Press a shortcut. Backspace/Delete clears it. Ctrl+C and Ctrl+V are reserved.",
            Dock = DockStyle.Top, Height = 48, Padding = new Padding(24, 15, 0, 0),
            ForeColor = Color.FromArgb(91, 99, 112), BackColor = Color.White
        };
        var resetHotkeys = CreateSecondaryButton("Reset defaults");
        resetHotkeys.Click += (_, _) => ResetHotkeys();
        var hotkeyCommands = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(20, 10, 0, 8),
            BackColor = Color.White
        };
        hotkeyCommands.Controls.Add(resetHotkeys);
        var hotkeyScroll = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.White,
            Padding = new Padding(0, 4, 0, 0)
        };
        hotkeyScroll.Controls.Add(hotkeyTable);
        var hotkeyPage = new TabPage("Hotkeys") { BackColor = Color.White };
        hotkeyPage.Controls.Add(hotkeyScroll);
        hotkeyPage.Controls.Add(hotkeyCommands);
        hotkeyPage.Controls.Add(hotkeyHint);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill, Padding = new Point(18, 7),
            Font = new Font("Segoe UI Semibold", 10f)
        };
        tabs.TabPages.Add(readerPage);
        tabs.TabPages.Add(performancePage);
        tabs.TabPages.Add(hotkeyPage);

        var ok = new Button
        {
            Text = "Save", Width = 104, Height = 34, FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(45, 108, 223), ForeColor = Color.White
        };
        ok.FlatAppearance.BorderSize = 0;
        ok.Click += (_, _) => AcceptSettings();
        var cancel = CreateSecondaryButton("Cancel");
        cancel.Width = 104;
        cancel.Height = 34;
        cancel.DialogResult = DialogResult.Cancel;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 64, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 14, 20, 10), BackColor = Color.FromArgb(242, 244, 248)
        };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void ApplyPerformanceMode(bool captureManual)
    {
        if (_autoOptimize.Checked)
        {
            if (captureManual) _manualPerformance = CapturePerformanceValues();
            ApplyPerformanceValues(_automaticPerformance);
            _autoOptimizeSummary.Text =
                $"{_automaticPerformance.CacheAheadMB + _automaticPerformance.CacheBehindMB:N0} MB pages " +
                $"({_automaticPerformance.CacheAheadMB:N0} ahead + {_automaticPerformance.CacheBehindMB:N0} behind)\n" +
                $"{_automaticPerformance.PrecacheWorkerCount} pre-cache workers • " +
                $"{_automaticPerformance.ImageMagickThreadsPerImage} batch threads • " +
                $"{_automaticPerformance.ZoomImageMagickThreadsPerImage} zoom threads";
        }
        else
        {
            ApplyPerformanceValues(_manualPerformance);
            _autoOptimizeSummary.Text = "Manual cache and worker values are active.";
        }
        SetPerformanceInputsEnabled(!_autoOptimize.Checked);
    }

    private void CopyAutomaticToManual()
    {
        _manualPerformance = _automaticPerformance;
        if (_autoOptimize.Checked)
            _autoOptimize.Checked = false;
        else
            ApplyPerformanceValues(_manualPerformance);
        _autoOptimizeSummary.Text =
            "Auto values copied to Manual. Adjust them below, then choose Save.";
    }

    private PerformanceProfile CapturePerformanceValues() => new(
        (int)_ahead.Value,
        (int)_behind.Value,
        (int)_previewCache.Value,
        (int)_thumbnailCache.Value,
        (int)_thumbnailFastPreviewCache.Value,
        (int)_fastPreviewWorkers.Value,
        (int)_fastPreviewThreads.Value,
        (int)_precacheWorkers.Value,
        (int)_imageMagickThreads.Value,
        (int)_zoomImageMagickThreads.Value);

    private void ApplyPerformanceValues(PerformanceProfile profile)
    {
        SetValue(_ahead, profile.CacheAheadMB);
        SetValue(_behind, profile.CacheBehindMB);
        SetValue(_previewCache, profile.PreviewCacheMB);
        SetValue(_thumbnailCache, profile.ThumbnailCacheMB);
        SetValue(_thumbnailFastPreviewCache, profile.ThumbnailFastPreviewCacheMB);
        SetValue(_fastPreviewWorkers, profile.FastPreviewWorkerCount);
        SetValue(_fastPreviewThreads, profile.FastPreviewThreadsPerWorker);
        SetValue(_precacheWorkers, profile.PrecacheWorkerCount);
        SetValue(_imageMagickThreads, profile.ImageMagickThreadsPerImage);
        SetValue(_zoomImageMagickThreads, profile.ZoomImageMagickThreadsPerImage);
    }

    private void SetPerformanceInputsEnabled(bool enabled)
    {
        foreach (var input in new Control[]
                 {
                     _ahead, _behind, _previewCache, _thumbnailCache,
                     _thumbnailFastPreviewCache, _fastPreviewWorkers,
                     _fastPreviewThreads, _precacheWorkers, _imageMagickThreads,
                     _zoomImageMagickThreads
                 })
            input.Enabled = enabled;
    }

    private static void SetValue(NumericUpDown input, int value) =>
        input.Value = Math.Clamp(value, (int)input.Minimum, (int)input.Maximum);

    private void AcceptSettings()
    {
        if (string.IsNullOrWhiteSpace(_persistentCachePath.Text))
        {
            MessageBox.Show(this, "Choose a persistent cache location.",
                "Cache location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { _ = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            _persistentCachePath.Text.Trim())); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            MessageBox.Show(this, "The persistent cache location is not valid.",
                "Cache location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var reserved = _hotkeyEditors.FirstOrDefault(pair =>
            ToolbarHotkeyCatalog.IsReserved(pair.Value.Shortcut));
        if (!string.IsNullOrEmpty(reserved.Key))
        {
            var label = ToolbarHotkeyCatalog.All.First(item => item.Id == reserved.Key).Label;
            MessageBox.Show(this,
                $"{ToolbarHotkeyCatalog.Format(reserved.Value.Shortcut)} is reserved and cannot be assigned to {label}.",
                "Reserved hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var duplicate = _hotkeyEditors
            .Where(pair => pair.Value.Shortcut != Keys.None)
            .GroupBy(pair => pair.Value.Shortcut)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            var labels = duplicate
                .Select(pair => ToolbarHotkeyCatalog.All.First(item => item.Id == pair.Key).Label);
            MessageBox.Show(this,
                $"The shortcut {ToolbarHotkeyCatalog.Format(duplicate.Key)} is assigned more than once:\n\n{string.Join("\n", labels)}",
                "Duplicate hotkey", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResetHotkeys()
    {
        foreach (var definition in ToolbarHotkeyCatalog.All)
            _hotkeyEditors[definition.Id].Shortcut = definition.DefaultShortcut;
    }

    private void ChooseBackground()
    {
        using var dialog = new ColorDialog { Color = ReaderBackground, FullOpen = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ReaderBackground = dialog.Color;
        _colorPreview.BackColor = ReaderBackground;
    }

    private void ChooseRandomLibraryPath()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose the library root used by Open random",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_randomLibraryPath.Text) ? _randomLibraryPath.Text : string.Empty
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _randomLibraryPath.Text = dialog.SelectedPath;
    }

    private void ChoosePersistentCachePath()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where G Reader stores persistent previews",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_persistentCachePath.Text)
                ? _persistentCachePath.Text
                : UserSettings.DefaultPersistentCachePath
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _persistentCachePath.Text = dialog.SelectedPath;
    }

    private void ChooseDefaultImageViewer()
    {
        try
        {
            WindowsFileAssociations.OpenDefaultAppsSettings();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot open Default apps",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static TabPage CreateSettingsPage(string title, params Panel[] sections)
    {
        var page = new TabPage(title) { BackColor = Color.FromArgb(242, 244, 248) };
        var viewport = new Panel
        {
            Dock = DockStyle.Fill, AutoScroll = true,
            BackColor = Color.FromArgb(242, 244, 248), Padding = new Padding(20, 18, 20, 18)
        };
        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1,
            BackColor = Color.FromArgb(242, 244, 248), Margin = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var section in sections)
        {
            var row = stack.RowCount++;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.Controls.Add(section, 0, row);
        }
        viewport.Controls.Add(stack);
        page.Controls.Add(viewport);
        return page;
    }

    private static Panel CreateSection(
        string title, string description, params (string Label, Control Input)[] fields)
    {
        var fieldHeights = fields
            .Select(field => Math.Max(46, field.Input.MinimumSize.Height))
            .ToArray();
        var card = new Panel
        {
            Dock = DockStyle.Top, Height = 122 + fieldHeights.Sum(),
            BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 14)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(20, 10, 20, 12), BackColor = Color.White
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = title, Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 11f),
            ForeColor = Color.FromArgb(31, 38, 50),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        layout.Controls.Add(new Label
        {
            Text = description, Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(91, 99, 112),
            TextAlign = ContentAlignment.TopLeft, AutoEllipsis = false
        }, 0, 1);

        var fieldTable = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = fields.Length,
            Margin = Padding.Empty, BackColor = Color.White
        };
        fieldTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 320));
        fieldTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < fields.Length; row++)
        {
            fieldTable.RowStyles.Add(new RowStyle(SizeType.Absolute, fieldHeights[row]));
            AddField(fieldTable, row, fields[row].Label, fields[row].Input);
        }
        layout.Controls.Add(fieldTable, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private static Button CreateSecondaryButton(string text)
    {
        var button = new Button
        {
            Text = text, AutoSize = true, MinimumSize = new Size(96, 32),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White,
            ForeColor = Color.FromArgb(45, 52, 64), Margin = new Padding(4)
        };
        button.FlatAppearance.BorderColor = Color.FromArgb(194, 200, 210);
        return button;
    }

    private static TableLayoutPanel CreateStretchButtonRow(Control content, Button button)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Margin = Padding.Empty, Padding = Padding.Empty
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        content.Dock = DockStyle.Fill;
        content.Margin = new Padding(0, 3, 8, 3);
        button.Margin = new Padding(0, 1, 0, 1);
        row.Controls.Add(content, 0, 0);
        row.Controls.Add(button, 1, 0);
        return row;
    }

    private static NumericUpDown CreateMemoryInput() => new()
    {
        Minimum = 0, Maximum = 65536, Increment = 128,
        ThousandsSeparator = true, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreateWorkerInput() => new()
    {
        Minimum = 1, Maximum = 64, Increment = 1,
        ThousandsSeparator = false, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreatePixelInput() => new()
    {
        Minimum = 32, Maximum = 8192, Increment = 32,
        ThousandsSeparator = true, Dock = DockStyle.Fill
    };

    private static void AddField(TableLayoutPanel table, int row, string label, Control input)
    {
        table.Controls.Add(new Label
        {
            Text = label, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(45, 52, 64), Margin = new Padding(4, 5, 12, 5)
        }, 0, row);
        input.Margin = new Padding(4, 7, 4, 7);
        table.Controls.Add(input, 1, row);
    }
}
