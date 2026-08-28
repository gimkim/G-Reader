namespace CDisplayEx.CSharp;

internal sealed class HistoryPopupForm : Form
{
    private readonly ThumbnailGridView _grid = new() { Dock = DockStyle.Fill };
    private readonly HistoryEntry[] _entries;
    private readonly int _quality;
    private readonly int _threads;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _renderSlots = new(2, 2);
    private int _previewRefreshRequested;
    private int _previewRefreshRunning;

    public string? SelectedPath { get; private set; }

    public HistoryPopupForm(
        IEnumerable<HistoryEntry> entries, bool historyEnabled,
        int quality, int threads)
    {
        _entries = entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Path))
            .OrderByDescending(entry => entry.LastOpenedUtc)
            .Select(entry => new HistoryEntry
            {
                Path = entry.Path,
                LastOpenedUtc = entry.LastOpenedUtc
            })
            .ToArray();
        _quality = Math.Clamp(quality, 0, 3);
        _threads = Math.Clamp(threads, 1, 64);

        Text = "Recently opened";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        KeyPreview = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(960, 700);
        MinimumSize = new Size(640, 440);
        BackColor = Color.FromArgb(26, 28, 33);

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 48,
            Padding = new Padding(14, 0, 14, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(232, 236, 244),
            BackColor = Color.FromArgb(36, 38, 44),
            Font = new Font("Segoe UI Semibold", 10.5f),
            Text = historyEnabled
                ? $"Recently opened — {_entries.Length:N0} item(s), newest first"
                : "History collection is disabled in Settings"
        };
        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 34,
            Padding = new Padding(14, 0, 14, 0),
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(165, 174, 190),
            BackColor = Color.FromArgb(31, 34, 40),
            Text = "Double-click or press Enter to open • Esc to close"
        };

        _grid.ImagesPerRow = 5;
        _grid.SetCacheLimits(192L * 1024 * 1024, 96L * 1024 * 1024);
        _grid.SetInternalPreviewMaxSize(360);
        var browseEntries = historyEnabled
            ? _entries.Select(entry => new ThumbnailFolderEntry(
                BuildLabel(entry), entry.Path,
                IsContainer: !Directory.Exists(entry.Path),
                IsPdf: Path.GetExtension(entry.Path).Equals(
                    ".pdf", StringComparison.OrdinalIgnoreCase)))
            : [];
        _grid.ResetPages([], browseEntries,
            historyEnabled
                ? "No recently opened folders, archives, or PDFs"
                : "History is disabled. Enable it in Settings > General > Library navigation.");

        Controls.Add(_grid);
        Controls.Add(hint);
        Controls.Add(heading);

        _grid.FolderActivated += (_, path) => ActivatePath(path);
        _grid.BrowsePriorityChanged += (_, _) => QueuePreviewRefresh();
        _grid.VisiblePreviewRefreshRequested += (_, _) => QueuePreviewRefresh();
        _grid.ThumbnailRefreshRequested += (_, _) => QueuePreviewRefresh();
        Shown += (_, _) =>
        {
            _grid.RefreshVirtualLayoutAfterShow();
            _grid.Focus();
            QueuePreviewRefresh();
        };
        FormClosed += (_, _) => _cancellation.Cancel();
    }

    private static string BuildLabel(HistoryEntry entry)
    {
        var path = Path.TrimEndingDirectorySeparator(entry.Path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = path;
        var opened = entry.LastOpenedUtc == default
            ? string.Empty
            : entry.LastOpenedUtc.ToLocalTime().ToString("g");
        return string.IsNullOrWhiteSpace(opened) ? name : $"{name}\n{opened}";
    }

    private void ActivatePath(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            MessageBox.Show(this,
                "This folder or file is no longer available:\n\n" + path,
                "History item unavailable", MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }
        SelectedPath = path;
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        switch (key)
        {
            case Keys.Enter:
                if (_grid.ActivateSelection()) return true;
                break;
            case Keys.Escape:
                DialogResult = DialogResult.Cancel;
                Close();
                return true;
            case Keys.Left: _grid.MoveSelection(Keys.Left); return true;
            case Keys.Right: _grid.MoveSelection(Keys.Right); return true;
            case Keys.Up: _grid.MoveSelection(Keys.Up); return true;
            case Keys.Down: _grid.MoveSelection(Keys.Down); return true;
            case Keys.PageUp: _grid.MoveSelectionPage(down: false); return true;
            case Keys.PageDown: _grid.MoveSelectionPage(down: true); return true;
            case Keys.Home: _grid.MoveToBoundary(end: false); return true;
            case Keys.End: _grid.MoveToBoundary(end: true); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void QueuePreviewRefresh()
    {
        if (IsDisposed || Disposing || _cancellation.IsCancellationRequested) return;
        Interlocked.Exchange(ref _previewRefreshRequested, 1);
        if (Interlocked.CompareExchange(ref _previewRefreshRunning, 1, 0) != 0)
            return;
        _ = RefreshVisiblePreviewsAsync();
    }

    private async Task RefreshVisiblePreviewsAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _previewRefreshRequested, 0) != 0)
            {
                var token = _cancellation.Token;
                token.ThrowIfCancellationRequested();
                var generation = _grid.ContentGeneration;
                var targetSize = _grid.RenderTargetSize;
                var visible = _grid.GetVisiblePreviewPriorityOrder()
                    .Where(work => work.IsBrowse && work.Index < _entries.Length)
                    .ToArray();
                await Task.WhenAll(visible
                    .Where(work => !_grid.HasBrowseFastPreview(
                        work.Index, targetSize))
                    .Select(work => RenderPreviewAsync(
                        work.Index, targetSize, generation,
                        fastPreview: true, cancellationToken: token)));
                // A scroll or resize makes the new visible range more important
                // than upgrading the previous range to final quality.
                if (Volatile.Read(ref _previewRefreshRequested) != 0) continue;
                await Task.WhenAll(visible
                    .Where(work => !_grid.HasBrowseFullPreview(
                        work.Index, targetSize))
                    .Select(work => RenderPreviewAsync(
                        work.Index, targetSize, generation,
                        fastPreview: false, cancellationToken: token)));
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.Exchange(ref _previewRefreshRunning, 0);
            if (Volatile.Read(ref _previewRefreshRequested) != 0 &&
                !IsDisposed && !Disposing)
                QueuePreviewRefresh();
        }
    }

    private async Task RenderPreviewAsync(
        int item, Size targetSize, int generation, bool fastPreview,
        CancellationToken cancellationToken)
    {
        await _renderSlots.WaitAsync(cancellationToken);
        try
        {
            if (fastPreview
                    ? _grid.HasBrowseFastPreview(item, targetSize)
                    : _grid.HasBrowseFullPreview(item, targetSize))
                return;
            var preview = await Task.Run(() =>
            {
                var path = _entries[item].Path;
                if (PersistentPreviewCache.TryLoadBrowse(
                        path, targetSize, fastPreview, _quality,
                        out var cached, cancellationToken) && cached is not null)
                    return cached;
                var generated = BrowsePreviewRenderer.Create(
                    path, targetSize, _threads,
                    fastPreview: fastPreview, quality: _quality,
                    cancellationToken: cancellationToken);
                if (generated is not null)
                    PersistentPreviewCache.StoreBrowseCopyInBackground(
                        path, targetSize, fastPreview, _quality, generated);
                return generated;
            }, cancellationToken);
            if (preview is not null)
            {
                if (cancellationToken.IsCancellationRequested || IsDisposed)
                    preview.Dispose();
                else
                    _grid.SetBrowsePreview(
                        item, targetSize, preview, fastPreview, generation);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException(
                "History thumbnail failed", exception,
                $"path={_entries[item].Path}");
        }
        finally { _renderSlots.Release(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
        base.Dispose(disposing);
    }
}
