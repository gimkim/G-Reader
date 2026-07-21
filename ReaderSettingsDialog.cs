namespace CDisplayEx.CSharp;

internal sealed class ReaderSettingsDialog : Form
{
    public event EventHandler<bool>? BenchmarkRunningChanged;
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _useNvJpeg = new()
    {
        Text = "Use NVIDIA nvJPEG when available (automatic libjpeg-turbo fallback)",
        AutoSize = true, Dock = DockStyle.Fill
    };
    private readonly NumericUpDown _pdfiumProcesses = CreatePdfiumProcessInput();
    private readonly NumericUpDown _jpegCpuFastWorkers = CreateWorkerInput();
    private readonly NumericUpDown _jpegCpuBackgroundWorkers = CreateWorkerInput();
    private readonly NumericUpDown _nvJpegWorkers = CreateCountInput(1, 16);
    private readonly NumericUpDown _nvJpegBatchSize = CreateCountInput(1, 16);
    private readonly NumericUpDown _nvJpegBatchDelay = CreateMillisecondsInput();
    private readonly NumericUpDown _nvJpegVramHeadroom = CreatePercentInput();
    private readonly CheckBox _useWicFastPreview = CreateOption(
        "Use Windows Imaging Component for first-frame previews");
    private readonly NumericUpDown _wicWorkers = CreateWorkerInput();
    private readonly NumericUpDown _pngWorkers = CreateWorkerInput();
    private readonly NumericUpDown _webpWorkers = CreateWorkerInput();
    private readonly NumericUpDown _gifWorkers = CreateWorkerInput();
    private readonly NumericUpDown _tiffWorkers = CreateWorkerInput();
    private readonly NumericUpDown _bmpWorkers = CreateWorkerInput();
    private readonly NumericUpDown _genericWorkers = CreateWorkerInput();
    private readonly CheckBox _useGenericGpuFast = CreateOption(
        "Use Direct2D GPU scaling for generic fast previews");
    private readonly CheckBox _useGenericGpuLanczos = CreateOption(
        "Use GPU final scaling for non-JPEG images when available");
    private readonly NumericUpDown _genericGpuWorkers = CreateWorkerInput();
    private readonly NumericUpDown _genericGpuMinimumSource = CreateMemoryInput();
    private readonly NumericUpDown _genericGpuFastMaximumSource = CreateMemoryInput();
    private readonly NumericUpDown _thumbnailIdleUploadBudget = CreateTimeBudgetInput();
    private readonly NumericUpDown _thumbnailScrollUploadBudget = CreateTimeBudgetInput();
    private readonly NumericUpDown _thumbnailUploadBudget = CreateMemoryInput();
    private readonly NumericUpDown _thumbnailUploadsPerFrame = CreateCountInput(1, 1024);
    private readonly CheckBox _useMonitorColorProfile = new()
    {
        Text = "Use the ICC profile assigned to the current monitor",
        AutoSize = true, Dock = DockStyle.Fill
    };
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
    private readonly NumericUpDown _globalFastPreviewConcurrency = CreateWorkerInput();
    private readonly NumericUpDown _fastPreviewWorkers = CreateWorkerInput();
    private readonly NumericUpDown _fastPreviewThreads = CreateWorkerInput();
    private readonly NumericUpDown _precacheWorkers = CreateWorkerInput();
    private readonly NumericUpDown _imageMagickThreads = CreateWorkerInput();
    private readonly NumericUpDown _zoomImageMagickThreads = CreateZoomThreadInput();
    private readonly Label _effectiveFastParallelism = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(70, 79, 94), AutoEllipsis = false,
        MinimumSize = new Size(0, 100), Padding = new Padding(0, 3, 0, 3)
    };
    private readonly CheckBox _autoOptimize = new()
    {
        Text = "Use automatic initial value suggestions for this computer",
        AutoSize = true, Dock = DockStyle.Fill
    };
    private readonly CheckBox _useBenchmarkProfile = CreateOption(
        "Use the most recent dataset benchmark profile");
    private readonly ComboBox _benchmarkDatasetSource = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill
    };
    private readonly TextBox _benchmarkDatasetPath = new() { Width = 420 };
    private readonly Label _autoOptimizeSummary = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(70, 79, 94), AutoEllipsis = false,
        MinimumSize = new Size(0, 76), Padding = new Padding(0, 4, 0, 4)
    };
    private readonly ComboBox _autoMove = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _rememberReadingPosition = new()
    {
        Text = "Remember the last page separately for each folder, archive, and PDF",
        AutoSize = true, Dock = DockStyle.Fill
    };
    private readonly Panel _colorPreview = new() { Width = 92, Height = 28, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _randomLibraryPath = new() { Width = 420 };
    private readonly Dictionary<string, HotkeyTextBox> _hotkeyEditors = new(StringComparer.Ordinal);
    private AutomaticInitialValueProfile _manualInitialValues = null!;
    private readonly AutomaticInitialValueProfile _automaticInitialValues;
    private readonly UserSettings _sourceSettings;
    private CancellationTokenSource? _benchmarkCancellation;
    private bool _benchmarkRunning;

    public int LanczosQuality => _quality.SelectedIndex;
    public bool UseNvJpeg => _useNvJpeg.Checked;
    public int PdfiumProcessCount => (int)_pdfiumProcesses.Value;
    public int JpegCpuFastWorkers => (int)_jpegCpuFastWorkers.Value;
    public int JpegCpuBackgroundWorkers => (int)_jpegCpuBackgroundWorkers.Value;
    public int NvJpegWorkerCount => (int)_nvJpegWorkers.Value;
    public int NvJpegBatchSize => (int)_nvJpegBatchSize.Value;
    public int NvJpegBatchDelayMs => (int)_nvJpegBatchDelay.Value;
    public int NvJpegVramHeadroomPercent => (int)_nvJpegVramHeadroom.Value;
    public bool UseWicFastPreview => _useWicFastPreview.Checked;
    public int WicFastPreviewWorkers => (int)_wicWorkers.Value;
    public int PngDecodeWorkers => (int)_pngWorkers.Value;
    public int WebPDecodeWorkers => (int)_webpWorkers.Value;
    public int GifDecodeWorkers => (int)_gifWorkers.Value;
    public int TiffDecodeWorkers => (int)_tiffWorkers.Value;
    public int BmpDecodeWorkers => (int)_bmpWorkers.Value;
    public int GenericFallbackWorkers => (int)_genericWorkers.Value;
    public bool UseGenericGpuFastPreview => _useGenericGpuFast.Checked;
    public bool UseGenericGpuLanczos => _useGenericGpuLanczos.Checked;
    public int GenericGpuWorkers => (int)_genericGpuWorkers.Value;
    public int GenericGpuMinimumSourceMB => (int)_genericGpuMinimumSource.Value;
    public int GenericGpuFastMaximumSourceMB => (int)_genericGpuFastMaximumSource.Value;
    public double ThumbnailIdleUploadBudgetMs => (double)_thumbnailIdleUploadBudget.Value;
    public double ThumbnailScrollUploadBudgetMs => (double)_thumbnailScrollUploadBudget.Value;
    public int ThumbnailUploadBudgetMB => (int)_thumbnailUploadBudget.Value;
    public int ThumbnailUploadsPerFrame => (int)_thumbnailUploadsPerFrame.Value;
    public bool UseMonitorColorProfile => _useMonitorColorProfile.Checked;
    public int CacheAheadMB => (int)_ahead.Value;
    public int CacheBehindMB => (int)_behind.Value;
    public int PreviewCacheMB => (int)_previewCache.Value;
    public int ThumbnailCacheMB => (int)_thumbnailCache.Value;
    public int ThumbnailFastPreviewCacheMB => (int)_thumbnailFastPreviewCache.Value;
    public string PersistentCachePath => _persistentCachePath.Text.Trim();
    public int FullViewDiskCacheMB => (int)_fullViewDiskCache.Value;
    public int ThumbnailDiskCacheMB => (int)_thumbnailDiskCache.Value;
    public int ThumbnailMaxPreviewSizePx => (int)_thumbnailMaxPreviewSize.Value;
    public int GlobalFastPreviewConcurrency => (int)_globalFastPreviewConcurrency.Value;
    public int FastPreviewWorkerCount => (int)_fastPreviewWorkers.Value;
    public int FastPreviewThreadsPerWorker => (int)_fastPreviewThreads.Value;
    public int PrecacheWorkerCount => (int)_precacheWorkers.Value;
    public int ImageMagickThreadsPerImage => (int)_imageMagickThreads.Value;
    public int ZoomImageMagickThreadsPerImage => (int)_zoomImageMagickThreads.Value;
    public bool AutoOptimizePerformance => _autoOptimize.Checked;
    public bool UseBenchmarkProfile => _useBenchmarkProfile.Checked;
    public int BenchmarkDatasetMode => _benchmarkDatasetSource.SelectedIndex;
    public string BenchmarkDatasetPath => _benchmarkDatasetPath.Text.Trim();
    public DateTime? BenchmarkCompletedUtc { get; private set; }
    public string BenchmarkSummary { get; private set; } = string.Empty;
    public Color ReaderBackground { get; private set; }
    public int AutoMoveMode => _autoMove.SelectedIndex;
    public bool RememberReadingPosition => _rememberReadingPosition.Checked;
    public bool ClearRememberedReadingPositionsRequested { get; private set; }
    public string RandomLibraryPath => _randomLibraryPath.Text.Trim();
    public Dictionary<string, int> ToolbarHotkeys => _hotkeyEditors.ToDictionary(
        pair => pair.Key, pair => (int)pair.Value.Shortcut, StringComparer.Ordinal);

    public ReaderSettingsDialog(UserSettings settings)
    {
        _sourceSettings = settings;
        _manualInitialValues = AutomaticInitialValueProfile.FromSettings(settings);
        _automaticInitialValues = AutomaticInitialValueProfile.Detect();
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
        _useNvJpeg.Checked = settings.UseNvJpeg;
        _pdfiumProcesses.Value = Math.Clamp(settings.PdfiumProcessCount,
            (int)_pdfiumProcesses.Minimum, (int)_pdfiumProcesses.Maximum);
        SetValue(_jpegCpuFastWorkers, settings.JpegCpuFastWorkers);
        SetValue(_jpegCpuBackgroundWorkers, settings.JpegCpuBackgroundWorkers);
        SetValue(_nvJpegWorkers, settings.NvJpegWorkerCount);
        SetValue(_nvJpegBatchSize, settings.NvJpegBatchSize);
        SetValue(_nvJpegBatchDelay, settings.NvJpegBatchDelayMs);
        SetValue(_nvJpegVramHeadroom, settings.NvJpegVramHeadroomPercent);
        _useWicFastPreview.Checked = settings.UseWicFastPreview;
        SetValue(_wicWorkers, settings.WicFastPreviewWorkers);
        SetValue(_pngWorkers, settings.PngDecodeWorkers);
        SetValue(_webpWorkers, settings.WebPDecodeWorkers);
        SetValue(_gifWorkers, settings.GifDecodeWorkers);
        SetValue(_tiffWorkers, settings.TiffDecodeWorkers);
        SetValue(_bmpWorkers, settings.BmpDecodeWorkers);
        SetValue(_genericWorkers, settings.GenericFallbackWorkers);
        _useGenericGpuFast.Checked = settings.UseGenericGpuFastPreview;
        _useGenericGpuLanczos.Checked = settings.UseGenericGpuLanczos;
        SetValue(_genericGpuWorkers, settings.GenericGpuWorkers);
        SetValue(_genericGpuMinimumSource, settings.GenericGpuMinimumSourceMB);
        SetValue(_genericGpuFastMaximumSource, settings.GenericGpuFastMaximumSourceMB);
        SetDecimalValue(_thumbnailIdleUploadBudget, settings.ThumbnailIdleUploadBudgetMs);
        SetDecimalValue(_thumbnailScrollUploadBudget, settings.ThumbnailScrollUploadBudgetMs);
        SetValue(_thumbnailUploadBudget, settings.ThumbnailUploadBudgetMB);
        SetValue(_thumbnailUploadsPerFrame, settings.ThumbnailUploadsPerFrame);
        _useMonitorColorProfile.Checked = settings.UseMonitorColorProfile;
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
        SetValue(_globalFastPreviewConcurrency, settings.GlobalFastPreviewConcurrency);
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
        foreach (var input in new[]
                 {
                     _globalFastPreviewConcurrency, _fastPreviewWorkers,
                     _fastPreviewThreads, _jpegCpuFastWorkers, _nvJpegWorkers
                 })
            input.ValueChanged += (_, _) => UpdateEffectiveFastParallelism();
        _useNvJpeg.CheckedChanged += (_, _) => UpdateEffectiveFastParallelism();
        UpdateEffectiveFastParallelism();
        _autoOptimize.Checked = settings.AutoOptimizePerformance;
        _useBenchmarkProfile.Checked = settings.UseBenchmarkProfile;
        _benchmarkDatasetSource.Items.AddRange([
            "Temporary comprehensive dataset (recommended)",
            "Custom dataset folder"
        ]);
        _benchmarkDatasetSource.SelectedIndex = Math.Clamp(
            settings.BenchmarkDatasetMode, 0, 1);
        _benchmarkDatasetPath.Text = settings.BenchmarkDatasetPath ?? string.Empty;
        BenchmarkCompletedUtc = settings.LastBenchmarkUtc;
        BenchmarkSummary = settings.LastBenchmarkSummary ?? string.Empty;
        _autoOptimize.CheckedChanged += (_, _) =>
        {
            if (_autoOptimize.Checked) _useBenchmarkProfile.Checked = false;
            ApplyPerformanceMode(captureManual: _autoOptimize.Checked);
        };
        _useBenchmarkProfile.CheckedChanged += (_, _) =>
        {
            if (_useBenchmarkProfile.Checked) _autoOptimize.Checked = false;
        };
        ApplyPerformanceMode(captureManual: false);
        _autoMove.Items.AddRange([
            "No auto move",
            "Auto move — folders only",
            "Auto move — archives/PDFs only",
            "Auto move — both folders and archives/PDFs"
        ]);
        _autoMove.SelectedIndex = Math.Clamp(settings.AutoMoveMode, 0, 3);
        _rememberReadingPosition.Checked = settings.RememberReadingPosition;
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

        var browsePersistentCache = CreateSecondaryButton("Browse...");
        browsePersistentCache.Click += (_, _) => ChoosePersistentCachePath();
        var persistentCachePathRow = CreateStretchButtonRow(
            _persistentCachePath, browsePersistentCache);

        var clearDiskCache = CreateSecondaryButton("Clear all disk cache");
        clearDiskCache.MinimumSize = new Size(174, 32);
        var clearDiskCacheStatus = new Label
        {
            AutoSize = true, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(70, 79, 94),
            Margin = new Padding(10, 8, 0, 0)
        };
        clearDiskCache.Click += async (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Delete every full-view and thumbnail preview in the disk cache?",
                    "Clear all disk cache", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes) return;
            clearDiskCache.Enabled = false;
            clearDiskCacheStatus.Text = "Clearing cache...";
            try
            {
                var result = await PersistentPreviewCache.ClearAllAsync(
                    _persistentCachePath.Text);
                if (IsDisposed) return;
                var removed = FormatByteCount(result.Bytes);
                clearDiskCacheStatus.Text = result.FailedCount == 0
                    ? $"Removed {result.FileCount:N0} files ({removed})"
                    : $"Removed {result.FileCount:N0} files ({removed}); " +
                      $"{result.FailedCount:N0} could not be removed";
            }
            catch (Exception ex)
            {
                if (!IsDisposed)
                    clearDiskCacheStatus.Text = $"Could not clear cache: {ex.Message}";
            }
            finally
            {
                if (!IsDisposed) clearDiskCache.Enabled = true;
            }
        };
        var clearDiskCacheRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            Margin = Padding.Empty
        };
        clearDiskCacheRow.Controls.AddRange([clearDiskCache, clearDiskCacheStatus]);

        var clearReadingPositions = CreateSecondaryButton("Clear saved positions");
        clearReadingPositions.MinimumSize = new Size(174, 32);
        var readingPositionsStatus = new Label
        {
            AutoSize = true, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(70, 79, 94),
            Margin = new Padding(10, 8, 0, 0)
        };
        clearReadingPositions.Click += (_, _) =>
        {
            if (MessageBox.Show(this,
                    "Forget every saved reading position for folders, archives, and PDFs?",
                    "Clear saved positions", MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning) != DialogResult.Yes) return;
            ClearRememberedReadingPositionsRequested = true;
            readingPositionsStatus.Text = "Will clear when you save settings";
            clearReadingPositions.Enabled = false;
        };
        var clearReadingPositionsRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            Margin = Padding.Empty
        };
        clearReadingPositionsRow.Controls.AddRange([
            clearReadingPositions, readingPositionsStatus
        ]);

        var chooseDefaultViewer = CreateSecondaryButton("Choose defaults…");
        chooseDefaultViewer.Click += (_, _) => ChooseDefaultImageViewer();
        var defaultViewerRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false
        };
        defaultViewerRow.Controls.Add(chooseDefaultViewer);

        var copyAutomatic = CreateSecondaryButton("Copy suggested values to Manual");
        copyAutomatic.MinimumSize = new Size(218, 32);
        copyAutomatic.Click += (_, _) => CopyAutomaticToManual();
        var copyAutomaticRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = true, WrapContents = false,
            Margin = Padding.Empty
        };
        copyAutomaticRow.Controls.Add(copyAutomatic);

        var browseBenchmarkDataset = CreateSecondaryButton("Browse...");
        browseBenchmarkDataset.Click += (_, _) => ChooseBenchmarkDataset();
        var benchmarkPathRow = CreateStretchButtonRow(
            _benchmarkDatasetPath, browseBenchmarkDataset);
        void UpdateBenchmarkDatasetControls()
        {
            var custom = _benchmarkDatasetSource.SelectedIndex == 1;
            _benchmarkDatasetPath.Enabled = custom;
            browseBenchmarkDataset.Enabled = custom;
        }
        _benchmarkDatasetSource.SelectedIndexChanged += (_, _) =>
            UpdateBenchmarkDatasetControls();
        UpdateBenchmarkDatasetControls();
        var runBenchmark = CreateSecondaryButton("Run automatic benchmark");
        runBenchmark.MinimumSize = new Size(218, 32);
        var cancelBenchmark = CreateSecondaryButton("Cancel");
        cancelBenchmark.Enabled = false;
        var benchmarkProgress = new ProgressBar
        {
            Width = 260, Height = 18, Minimum = 0, Maximum = 100,
            Margin = new Padding(10, 8, 0, 0)
        };
        var benchmarkStatus = new Label
        {
            Dock = DockStyle.Fill, AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            ForeColor = Color.FromArgb(70, 79, 94),
            Margin = new Padding(4, 4, 4, 0),
            Text = string.IsNullOrWhiteSpace(BenchmarkSummary)
                ? "No benchmark has been run yet."
                : BenchmarkSummary
        };
        cancelBenchmark.Click += (_, _) => _benchmarkCancellation?.Cancel();
        runBenchmark.Click += async (_, _) =>
        {
            var useCustomDataset = _benchmarkDatasetSource.SelectedIndex == 1;
            if (useCustomDataset && !Directory.Exists(_benchmarkDatasetPath.Text.Trim()))
            {
                ChooseBenchmarkDataset();
                if (!Directory.Exists(_benchmarkDatasetPath.Text.Trim())) return;
            }
            runBenchmark.Enabled = false;
            cancelBenchmark.Enabled = true;
            _benchmarkRunning = true;
            BenchmarkRunningChanged?.Invoke(this, true);
            benchmarkProgress.Value = 0;
            benchmarkStatus.Text = "Scanning dataset...";
            _benchmarkCancellation?.Dispose();
            _benchmarkCancellation = new CancellationTokenSource();
            TemporaryBenchmarkDataset? generatedDataset = null;
            var progress = new Progress<PerformanceBenchmarkProgress>(value =>
            {
                benchmarkProgress.Maximum = Math.Max(1, value.Total);
                benchmarkProgress.Value = Math.Clamp(value.Completed, 0,
                    benchmarkProgress.Maximum);
                benchmarkStatus.Text = $"{value.Stage}: {value.Detail}";
            });
            try
            {
                // Give cancelled pre-cache/thumbnail workers a brief, non-blocking
                // drain window so they do not contaminate the first measurement.
                await Task.Delay(300, _benchmarkCancellation.Token);
                var datasetPath = _benchmarkDatasetPath.Text.Trim();
                if (!useCustomDataset)
                {
                    benchmarkStatus.Text = "Generating comprehensive temporary dataset...";
                    var generationProgress = new Progress<TemporaryDatasetProgress>(value =>
                    {
                        benchmarkProgress.Maximum = Math.Max(1, value.Total);
                        benchmarkProgress.Value = Math.Clamp(value.Completed, 0,
                            benchmarkProgress.Maximum);
                        benchmarkStatus.Text = "Generating: " + value.Detail;
                    });
                    generatedDataset = await TemporaryBenchmarkDataset.CreateAsync(
                        generationProgress, _benchmarkCancellation.Token);
                    datasetPath = generatedDataset.Path;
                    benchmarkProgress.Value = 0;
                    benchmarkStatus.Text = "Temporary dataset ready; starting benchmark...";
                }
                var benchmarkSettings = CreateBenchmarkSettingsSnapshot();
                var result = await PerformanceBenchmark.RunAsync(
                    datasetPath, benchmarkSettings,
                    progress, _benchmarkCancellation.Token);
                ApplyBenchmarkResult(result);
                BenchmarkCompletedUtc = DateTime.UtcNow;
                BenchmarkSummary = result.Summary;
                benchmarkStatus.Text = "Applied: " + result.Summary;
                benchmarkProgress.Value = benchmarkProgress.Maximum;
            }
            catch (OperationCanceledException)
            {
                benchmarkStatus.Text = "Benchmark cancelled; previous values were kept.";
            }
            catch (Exception ex)
            {
                benchmarkStatus.Text = "Benchmark failed: " + ex.Message;
            }
            finally
            {
                if (generatedDataset is not null)
                    await generatedDataset.DisposeAsync();
                ImagePipelineTuning.Configure(_sourceSettings);
                NvJpegNativeDecoder.Configure(_sourceSettings.UseNvJpeg, _sourceSettings);
                _benchmarkRunning = false;
                BenchmarkRunningChanged?.Invoke(this, false);
                runBenchmark.Enabled = true;
                cancelBenchmark.Enabled = false;
            }
        };
        var benchmarkButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, AutoSize = false, WrapContents = false,
            Margin = Padding.Empty
        };
        benchmarkButtons.Controls.AddRange([runBenchmark, cancelBenchmark, benchmarkProgress]);
        var benchmarkCommandRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty,
            MinimumSize = new Size(0, 104)
        };
        benchmarkCommandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        benchmarkCommandRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        benchmarkCommandRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        benchmarkCommandRow.Controls.Add(benchmarkButtons, 0, 0);
        benchmarkCommandRow.Controls.Add(benchmarkStatus, 0, 1);

        var generalPage = CreateSettingsPage("General",
            CreateSection("Reader appearance",
                "Control the canvas appearance and color correction used on the active monitor.",
                ("Monitor color management", _useMonitorColorProfile),
                ("Background color", colorRow)),
            CreateSection("Library navigation",
                "At a book boundary, follow the same ordered folder/archive list shown in Thumbnail view.",
                ("Automatic move", _autoMove),
                ("Resume reading", _rememberReadingPosition),
                ("Saved positions", clearReadingPositionsRow),
                ("Random library path", randomPathRow)),
            CreateSection("Windows integration",
                "Register G Reader for supported image formats and choose which formats open with it by default.",
                ("Default image viewer", defaultViewerRow)));

        var renderingPage = CreateSettingsPage("Rendering",
            CreateSection("Image quality",
                "Choose the final resize filter and optional NVIDIA JPEG decoder. Unsupported images automatically use the CPU path.",
                ("Lanczos quality", _quality),
                ("NVIDIA GPU decode", _useNvJpeg)),
            CreateSection("PDF rendering",
                "PDFium uses isolated native processes for parallel page rendering and can pass eligible full-page JPEG data to nvJPEG.",
                ("PDFium worker processes", _pdfiumProcesses)),
            CreateSection("Thumbnail rendering",
                "Limit the internal preview resolution used to populate the thumbnail grid.",
                ("Maximum preview edge (px)", _thumbnailMaxPreviewSize)));

        var cachePage = CreateSettingsPage("Cache",
            CreateSection("Memory cache",
                "Reading pages and thumbnails use separate soft budgets. Cleanup runs after navigation work to preserve responsiveness.",
                ("Pages ahead (MB)", _ahead),
                ("Pages behind (MB)", _behind),
                ("Full-view fast previews (MB)", _previewCache),
                ("Final thumbnails (MB)", _thumbnailCache),
                ("Fast thumbnails (MB)", _thumbnailFastPreviewCache)),
            CreateSection("Persistent disk cache",
                "Generated previews survive closing the app. Full-view and thumbnail files are trimmed independently in the background.",
                ("Cache location", persistentCachePathRow),
                ("Full-view quota (MB)", _fullViewDiskCache),
                ("Thumbnail quota (MB)", _thumbnailDiskCache),
                ("Total disk allowance", _diskCacheTotal),
                ("Cache maintenance", clearDiskCacheRow)));

        var performancePage = CreateSettingsPage("Performance",
            CreateSection("Automatic initial value suggestion",
                "Suggest starting values for memory, codec/PDF workers, GPU batching, thumbnail uploads and CPU parallelism from this computer's resources. This is a hardware estimate, not a dataset benchmark.",
                ("Initial-value suggestion", _autoOptimize),
                ("Suggested profile", _autoOptimizeSummary),
                ("Copy to manual settings", copyAutomaticRow)),
            CreateSection("Dataset benchmark",
                "Use a generated temporary set covering supported formats and multiple resolutions, or choose your own folder. Generation time is excluded from benchmark scores.",
                ("Benchmark profile", _useBenchmarkProfile),
                ("Dataset source", _benchmarkDatasetSource),
                ("Custom folder", benchmarkPathRow),
                ("Benchmark", benchmarkCommandRow)),
            CreateSection("Processing threads",
                "The global limit controls fast-pass scheduling. Non-JPEG resize jobs may each split their output rows across several CPU threads; JPEG decoding has separate image-level limits.",
                ("Global fast-preview concurrency", _globalFastPreviewConcurrency),
                ("Non-JPEG resize images in parallel", _fastPreviewWorkers),
                ("CPU threads per non-JPEG resize", _fastPreviewThreads),
                ("Effective fast paths", _effectiveFastParallelism),
                ("Full-quality pre-cache images", _precacheWorkers),
                ("CPU threads per Lanczos image", _imageMagickThreads),
                ("Zoom Lanczos threads", _zoomImageMagickThreads)));

        var codecsPage = CreateSettingsPage("Codecs & GPU",
            CreateSection("JPEG on CPU",
                "JPEG decoding is single-threaded per image. These limits are numbers of images decoded simultaneously, not threads inside one image.",
                ("Fast CPU JPEG images in parallel", _jpegCpuFastWorkers),
                ("Background CPU JPEG images in parallel", _jpegCpuBackgroundWorkers)),
            CreateSection("NVIDIA nvJPEG",
                "Urgent pages are submitted immediately. Only background thumbnail work may wait briefly to form a batch; VRAM admission remains adaptive.",
                ("GPU JPEG images in parallel", _nvJpegWorkers),
                ("Background batch size", _nvJpegBatchSize),
                ("Maximum batch wait (ms)", _nvJpegBatchDelay),
                ("VRAM headroom (%)", _nvJpegVramHeadroom)),
            CreateSection("Format decode parallelism",
                "Independent limits prevent a slow codec from occupying every preview worker.",
                ("WIC first-frame previews", _useWicFastPreview),
                ("WIC preview workers", _wicWorkers),
                ("PNG workers", _pngWorkers),
                ("WebP workers", _webpWorkers),
                ("GIF workers", _gifWorkers),
                ("TIFF workers", _tiffWorkers),
                ("BMP workers", _bmpWorkers),
                ("Other/fallback workers", _genericWorkers)),
            CreateSection("Generic GPU scaling",
                "Direct2D supplies a low-latency preview. The final GPU path is used only when its size threshold makes transfer worthwhile.",
                ("GPU fast preview", _useGenericGpuFast),
                ("GPU final resize", _useGenericGpuLanczos),
                ("GPU resize workers", _genericGpuWorkers),
                ("Final GPU minimum source (MB)", _genericGpuMinimumSource),
                ("Fast GPU maximum source (MB)", _genericGpuFastMaximumSource)),
            CreateSection("Thumbnail GPU upload",
                "Adaptive frame budgets keep scrolling responsive while allowing textures to appear continuously.",
                ("Idle time budget (ms)", _thumbnailIdleUploadBudget),
                ("Scrolling time budget (ms)", _thumbnailScrollUploadBudget),
                ("Maximum upload per frame (MB)", _thumbnailUploadBudget),
                ("Maximum textures per frame", _thumbnailUploadsPerFrame)));

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
        tabs.TabPages.Add(generalPage);
        tabs.TabPages.Add(renderingPage);
        tabs.TabPages.Add(cachePage);
        tabs.TabPages.Add(performancePage);
        tabs.TabPages.Add(codecsPage);
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
            if (captureManual) _manualInitialValues = CaptureInitialValues();
            ApplyInitialValues(_automaticInitialValues);
            var core = _automaticInitialValues.Core;
            _autoOptimizeSummary.Text =
                $"{core.CacheAheadMB + core.CacheBehindMB:N0} MB pages " +
                $"({core.CacheAheadMB:N0} ahead + {core.CacheBehindMB:N0} behind)\n" +
                $"{core.GlobalFastPreviewConcurrency} global fast / " +
                $"{core.FastPreviewWorkerCount}×{core.FastPreviewThreadsPerWorker} non-JPEG / " +
                $"{_automaticInitialValues.JpegCpuFastWorkers} JPEG\n" +
                $"{core.PrecacheWorkerCount} pre-cache / " +
                $"{_automaticInitialValues.PdfiumProcessCount} PDFium workers • " +
                $"nvJPEG {_automaticInitialValues.NvJpegWorkerCount}, batch {_automaticInitialValues.NvJpegBatchSize}\n" +
                $"GPU upload {_automaticInitialValues.ThumbnailIdleUploadBudgetMs:0.#}/" +
                $"{_automaticInitialValues.ThumbnailScrollUploadBudgetMs:0.#} ms • " +
                $"{_automaticInitialValues.ThumbnailUploadBudgetMB} MB/frame";
        }
        else
        {
            ApplyInitialValues(_manualInitialValues);
            _autoOptimizeSummary.Text = _useBenchmarkProfile.Checked
                ? "Dataset benchmark values are active. " + BenchmarkSummary
                : "Manual cache and worker values are active.";
        }
        SetSuggestedInputsEnabled(!_autoOptimize.Checked);
    }

    private void UpdateEffectiveFastParallelism()
    {
        var logical = Math.Clamp(Environment.ProcessorCount, 1, 64);
        var global = Math.Min(logical, (int)_globalFastPreviewConcurrency.Value);
        var nonJpegImages = Math.Min(global, (int)_fastPreviewWorkers.Value);
        var threadsPerResize = (int)_fastPreviewThreads.Value;
        var nonJpegThreads = Math.Min(logical, nonJpegImages * threadsPerResize);
        var cpuJpegImages = Math.Min(global, (int)_jpegCpuFastWorkers.Value);
        var gpuJpeg = _useNvJpeg.Checked
            ? $"{Math.Min(global, (int)_nvJpegWorkers.Value)} images"
            : $"Off (configured {(int)_nvJpegWorkers.Value})";
        _effectiveFastParallelism.Text =
            $"Fast scheduling: {global} images maximum\n" +
            $"CPU JPEG: {cpuJpegImages} images × 1 decode thread\n" +
            $"Non-JPEG resize: {nonJpegImages} images × up to {threadsPerResize} threads " +
            $"(up to {nonJpegThreads} CPU threads)\n" +
            $"GPU JPEG: {gpuJpeg}";
    }

    private void CopyAutomaticToManual()
    {
        _manualInitialValues = _automaticInitialValues;
        _useBenchmarkProfile.Checked = false;
        if (_autoOptimize.Checked)
            _autoOptimize.Checked = false;
        else
            ApplyInitialValues(_manualInitialValues);
        _autoOptimizeSummary.Text =
            "Suggested initial values copied to Manual. Adjust them below, then choose Save.";
    }

    private UserSettings CreateBenchmarkSettingsSnapshot()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<UserSettings>(
            System.Text.Json.JsonSerializer.Serialize(_sourceSettings)) ?? new UserSettings();
        settings.UseNvJpeg = _useNvJpeg.Checked;
        settings.NvJpegWorkerCount = (int)_nvJpegWorkers.Value;
        settings.NvJpegBatchSize = (int)_nvJpegBatchSize.Value;
        settings.NvJpegBatchDelayMs = (int)_nvJpegBatchDelay.Value;
        settings.NvJpegVramHeadroomPercent = (int)_nvJpegVramHeadroom.Value;
        return settings;
    }

    private void ApplyBenchmarkResult(PerformanceBenchmarkResult result)
    {
        _autoOptimize.Checked = false;
        _useBenchmarkProfile.Checked = true;
        // Cache budgets are capacity decisions rather than decode throughput
        // decisions, so seed them from current available RAM before applying
        // the measured CPU/GPU values.
        var capacity = PerformanceProfile.Detect();
        SetValue(_ahead, capacity.CacheAheadMB);
        SetValue(_behind, capacity.CacheBehindMB);
        SetValue(_previewCache, capacity.PreviewCacheMB);
        SetValue(_thumbnailCache, capacity.ThumbnailCacheMB);
        SetValue(_thumbnailFastPreviewCache, capacity.ThumbnailFastPreviewCacheMB);
        SetValue(_globalFastPreviewConcurrency,
            Math.Clamp(result.FastWorkers * result.FastThreads, 1,
                Math.Clamp(Environment.ProcessorCount, 1, 64)));
        SetValue(_zoomImageMagickThreads, capacity.ZoomImageMagickThreadsPerImage);
        SetValue(_jpegCpuFastWorkers, result.JpegCpuWorkers);
        SetValue(_jpegCpuBackgroundWorkers, result.JpegCpuWorkers);
        SetValue(_pngWorkers, result.PngWorkers);
        SetValue(_webpWorkers, result.WebPWorkers);
        SetValue(_gifWorkers, result.GifWorkers);
        SetValue(_tiffWorkers, result.TiffWorkers);
        SetValue(_bmpWorkers, result.BmpWorkers);
        SetValue(_genericWorkers, result.GenericWorkers);
        SetValue(_wicWorkers, result.WicWorkers);
        SetValue(_imageMagickThreads, result.ImageMagickThreads);
        SetValue(_precacheWorkers, result.PrecacheWorkers);
        SetValue(_fastPreviewWorkers, result.FastWorkers);
        SetValue(_fastPreviewThreads, result.FastThreads);
        SetValue(_nvJpegWorkers, result.NvJpegWorkers);
        SetValue(_nvJpegBatchSize, result.NvJpegBatchSize);
        SetValue(_nvJpegBatchDelay, result.NvJpegBatchDelayMs);
        SetValue(_pdfiumProcesses, result.PdfiumProcesses);
        _manualInitialValues = CaptureInitialValues();
        SetSuggestedInputsEnabled(true);
        _autoOptimizeSummary.Text = "Dataset benchmark values are active.";
    }

    private AutomaticInitialValueProfile CaptureInitialValues() => new(
        CapturePerformanceValues(), (int)_pdfiumProcesses.Value,
        (int)_jpegCpuFastWorkers.Value, (int)_jpegCpuBackgroundWorkers.Value,
        (int)_nvJpegWorkers.Value, (int)_nvJpegBatchSize.Value,
        (int)_nvJpegBatchDelay.Value, (int)_nvJpegVramHeadroom.Value,
        (int)_wicWorkers.Value, (int)_pngWorkers.Value, (int)_webpWorkers.Value,
        (int)_gifWorkers.Value, (int)_tiffWorkers.Value, (int)_bmpWorkers.Value,
        (int)_genericWorkers.Value, (int)_genericGpuWorkers.Value,
        (int)_genericGpuMinimumSource.Value, (int)_genericGpuFastMaximumSource.Value,
        (double)_thumbnailIdleUploadBudget.Value,
        (double)_thumbnailScrollUploadBudget.Value,
        (int)_thumbnailUploadBudget.Value, (int)_thumbnailUploadsPerFrame.Value,
        (int)_thumbnailMaxPreviewSize.Value);

    private PerformanceProfile CapturePerformanceValues() => new(
        (int)_ahead.Value,
        (int)_behind.Value,
        (int)_previewCache.Value,
        (int)_thumbnailCache.Value,
        (int)_thumbnailFastPreviewCache.Value,
        (int)_globalFastPreviewConcurrency.Value,
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
        SetValue(_globalFastPreviewConcurrency, profile.GlobalFastPreviewConcurrency);
        SetValue(_fastPreviewWorkers, profile.FastPreviewWorkerCount);
        SetValue(_fastPreviewThreads, profile.FastPreviewThreadsPerWorker);
        SetValue(_precacheWorkers, profile.PrecacheWorkerCount);
        SetValue(_imageMagickThreads, profile.ImageMagickThreadsPerImage);
        SetValue(_zoomImageMagickThreads, profile.ZoomImageMagickThreadsPerImage);
    }

    private void ApplyInitialValues(AutomaticInitialValueProfile profile)
    {
        ApplyPerformanceValues(profile.Core);
        SetValue(_pdfiumProcesses, profile.PdfiumProcessCount);
        SetValue(_jpegCpuFastWorkers, profile.JpegCpuFastWorkers);
        SetValue(_jpegCpuBackgroundWorkers, profile.JpegCpuBackgroundWorkers);
        SetValue(_nvJpegWorkers, profile.NvJpegWorkerCount);
        SetValue(_nvJpegBatchSize, profile.NvJpegBatchSize);
        SetValue(_nvJpegBatchDelay, profile.NvJpegBatchDelayMs);
        SetValue(_nvJpegVramHeadroom, profile.NvJpegVramHeadroomPercent);
        SetValue(_wicWorkers, profile.WicFastPreviewWorkers);
        SetValue(_pngWorkers, profile.PngDecodeWorkers);
        SetValue(_webpWorkers, profile.WebPDecodeWorkers);
        SetValue(_gifWorkers, profile.GifDecodeWorkers);
        SetValue(_tiffWorkers, profile.TiffDecodeWorkers);
        SetValue(_bmpWorkers, profile.BmpDecodeWorkers);
        SetValue(_genericWorkers, profile.GenericFallbackWorkers);
        SetValue(_genericGpuWorkers, profile.GenericGpuWorkers);
        SetValue(_genericGpuMinimumSource, profile.GenericGpuMinimumSourceMB);
        SetValue(_genericGpuFastMaximumSource, profile.GenericGpuFastMaximumSourceMB);
        SetDecimalValue(_thumbnailIdleUploadBudget, profile.ThumbnailIdleUploadBudgetMs);
        SetDecimalValue(_thumbnailScrollUploadBudget, profile.ThumbnailScrollUploadBudgetMs);
        SetValue(_thumbnailUploadBudget, profile.ThumbnailUploadBudgetMB);
        SetValue(_thumbnailUploadsPerFrame, profile.ThumbnailUploadsPerFrame);
        SetValue(_thumbnailMaxPreviewSize, profile.ThumbnailMaxPreviewSizePx);
    }

    private void SetSuggestedInputsEnabled(bool enabled)
    {
        foreach (var input in new Control[]
                 {
                     _ahead, _behind, _previewCache, _thumbnailCache,
                     _thumbnailFastPreviewCache, _globalFastPreviewConcurrency,
                     _fastPreviewWorkers,
                     _fastPreviewThreads, _precacheWorkers, _imageMagickThreads,
                     _zoomImageMagickThreads, _pdfiumProcesses,
                     _jpegCpuFastWorkers, _jpegCpuBackgroundWorkers,
                     _nvJpegWorkers, _nvJpegBatchSize, _nvJpegBatchDelay,
                     _nvJpegVramHeadroom, _wicWorkers, _pngWorkers,
                     _webpWorkers, _gifWorkers, _tiffWorkers, _bmpWorkers,
                     _genericWorkers, _genericGpuWorkers,
                     _genericGpuMinimumSource, _genericGpuFastMaximumSource,
                     _thumbnailIdleUploadBudget, _thumbnailScrollUploadBudget,
                     _thumbnailUploadBudget, _thumbnailUploadsPerFrame,
                     _thumbnailMaxPreviewSize
                 })
            input.Enabled = enabled;
    }

    private static void SetValue(NumericUpDown input, int value) =>
        input.Value = Math.Clamp(value, (int)input.Minimum, (int)input.Maximum);

    private static void SetDecimalValue(NumericUpDown input, double value) =>
        input.Value = Math.Clamp((decimal)value, input.Minimum, input.Maximum);

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

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_benchmarkRunning)
        {
            _benchmarkCancellation?.Cancel();
            e.Cancel = true;
        }
        base.OnFormClosing(e);
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

    private void ChooseBenchmarkDataset()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder containing representative test images",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_benchmarkDatasetPath.Text)
                ? _benchmarkDatasetPath.Text : string.Empty
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _benchmarkDatasetPath.Text = dialog.SelectedPath;
    }

    private static string FormatByteCount(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):N2} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):N1} MB";
        if (bytes >= 1024L)
            return $"{bytes / 1024d:N1} KB";
        return $"{bytes:N0} bytes";
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
        const int descriptionHeight = 60;
        var card = new Panel
        {
            Dock = DockStyle.Top,
            Height = 122 + (descriptionHeight - 42) + fieldHeights.Sum(),
            BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 0, 0, 14)
        };
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(20, 10, 20, 12), BackColor = Color.White
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, descriptionHeight));
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
            TextAlign = ContentAlignment.TopLeft, AutoEllipsis = false,
            UseCompatibleTextRendering = true,
            Padding = new Padding(0, 1, 0, 3)
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

    private static NumericUpDown CreatePdfiumProcessInput() => new()
    {
        Minimum = 1, Maximum = 16, Increment = 1,
        ThousandsSeparator = false, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreateZoomThreadInput() => new()
    {
        Minimum = 1, Maximum = 255, Increment = 1,
        ThousandsSeparator = false, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreatePixelInput() => new()
    {
        Minimum = 32, Maximum = 8192, Increment = 32,
        ThousandsSeparator = true, Dock = DockStyle.Fill
    };

    private static CheckBox CreateOption(string text) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreateMillisecondsInput() => new()
    {
        Minimum = 0, Maximum = 100, Increment = 1, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreatePercentInput() => new()
    {
        Minimum = 5, Maximum = 75, Increment = 1, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreateTimeBudgetInput() => new()
    {
        Minimum = 0.5m, Maximum = 50m, Increment = 0.5m,
        DecimalPlaces = 1, Dock = DockStyle.Fill
    };

    private static NumericUpDown CreateCountInput(int minimum, int maximum) => new()
    {
        Minimum = minimum, Maximum = maximum, Increment = 1,
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
