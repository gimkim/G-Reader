using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CDisplayEx.CSharp;

internal sealed record AvailableUpdate(
    Version Version, string DisplayVersion, string Name, string DownloadUrl,
    string? Sha256, long Size, string ReleaseUrl);

internal static class UpdateManager
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/gimkim/G-Reader/releases/latest";
    private static readonly HttpClient Client = CreateClient();
    private static int _checkRunning;

    public static Version CurrentVersion => NormalizeVersion(
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0));

    public static string CurrentDisplayVersion => FormatVersion(CurrentVersion);

    public static async Task<bool> CheckAndPromptAsync(
        IWin32Window owner, bool showUpToDate, CancellationToken cancellationToken = default)
    {
        if (AppPackageContext.IsPackaged)
        {
            if (showUpToDate)
                MessageBox.Show(owner,
                    "Updates for this installation are managed by Microsoft Store.",
                    "Microsoft Store updates", MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            return false;
        }
        if (Interlocked.Exchange(ref _checkRunning, 1) != 0) return false;
        try
        {
            var update = await CheckAsync(cancellationToken);
            if (update is null)
            {
                if (showUpToDate)
                    MessageBox.Show(owner,
                        $"Fast Reader/Viewer {CurrentDisplayVersion} is the latest version.",
                        "Check for updates", MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                return false;
            }

            var answer = MessageBox.Show(owner,
                $"Fast Reader/Viewer {update.DisplayVersion} is available and will replace " +
                $"the current version {CurrentDisplayVersion}.\n\n" +
                $"Download {FormatBytes(update.Size)} and install it now?\n\n" +
                "Fast Reader/Viewer will close and relaunch automatically after the file is replaced.",
                "Fast Reader/Viewer update available", MessageBoxButtons.YesNo,
                MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            if (answer != DialogResult.Yes) return true;

            using var progress = new UpdateDownloadDialog(
                CurrentDisplayVersion, update.DisplayVersion);
            progress.Show(owner);
            progress.Refresh();
            try
            {
                var downloaded = await DownloadAsync(update,
                    new Progress<int>(progress.SetProgress), cancellationToken);
                progress.SetStatus("Download verified. Preparing to restart...");
                ScheduleReplacementAndRelaunch(downloaded);
                Application.Exit();
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                ExtendedDiagnostics.LogException("Update installation failed", exception,
                    $"latest={update.DisplayVersion}; url={update.DownloadUrl}");
                if (!progress.IsDisposed) progress.Close();
                MessageBox.Show(owner,
                    "The update could not be installed. The current version was not changed.\n\n" +
                    exception.Message,
                    "Update failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return true;
            }
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception exception)
        {
            ExtendedDiagnostics.LogException("Update check failed", exception);
            if (showUpToDate)
                MessageBox.Show(owner,
                    "Could not check GitHub for updates.\n\n" + exception.Message,
                    "Check for updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally { Interlocked.Exchange(ref _checkRunning, 0); }
    }

    public static async Task<AvailableUpdate?> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(
            LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("GitHub returned an empty release response.");
        if (release.Draft || release.Prerelease) return null;
        var version = ParseTagVersion(release.TagName);
        if (version <= CurrentVersion) return null;
        var asset = release.Assets.FirstOrDefault(candidate =>
            candidate.Name.Equals("Fast.Reader.Viewer.exe", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(candidate =>
                candidate.Name.Equals("G.Reader.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Release {release.TagName} does not contain Fast.Reader.Viewer.exe.");
        var digest = asset.Digest?.StartsWith("sha256:",
            StringComparison.OrdinalIgnoreCase) == true
            ? asset.Digest[7..].Trim() : null;
        if (string.IsNullOrWhiteSpace(digest))
            throw new InvalidDataException(
                $"Release asset {asset.Name} does not include a GitHub SHA-256 digest.");
        return new AvailableUpdate(version, FormatVersion(version),
            string.IsNullOrWhiteSpace(release.Name) ? release.TagName : release.Name,
            asset.DownloadUrl, digest, asset.Size, release.HtmlUrl);
    }

    private static async Task<string> DownloadAsync(
        AvailableUpdate update, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), "Fast Reader Viewer Updates",
            "v" + update.DisplayVersion);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Fast.Reader.Viewer.exe.download");
        using var response = await Client.GetAsync(update.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.Size;
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var destination = new FileStream(path, FileMode.Create,
                         FileAccess.Write, FileShare.None, 1024 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024];
            long completed = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                completed += read;
                if (total > 0)
                    progress.Report((int)Math.Clamp(completed * 100 / total, 0, 100));
            }
        }
        var downloadedSize = new FileInfo(path).Length;
        if (downloadedSize < 1024 * 1024)
            throw new InvalidDataException("The downloaded executable is unexpectedly small.");
        if (update.Size > 0 && downloadedSize != update.Size)
            throw new InvalidDataException(
                $"The downloaded size ({downloadedSize:N0} bytes) does not match GitHub ({update.Size:N0} bytes).");
        if (!string.IsNullOrWhiteSpace(update.Sha256))
        {
            await using var file = File.OpenRead(path);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(
                file, cancellationToken)).ToLowerInvariant();
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded SHA-256 digest does not match GitHub.");
        }
        using (var file = File.OpenRead(path))
            if (file.ReadByte() != 'M' || file.ReadByte() != 'Z')
                throw new InvalidDataException("The downloaded file is not a Windows executable.");
        progress.Report(100);
        return path;
    }

    private static void ScheduleReplacementAndRelaunch(string downloadedPath)
    {
        var target = Application.ExecutablePath;
        var processId = Environment.ProcessId;
        var staged = Path.Combine(Path.GetDirectoryName(target)!,
            "." + Path.GetFileName(target) + ".update");
        var backup = target + ".previous";
        var log = Path.Combine(Path.GetTempPath(), "Fast Reader Viewer Updates", "update-error.log");
        // Stage while this process is still alive. This verifies write access to
        // the installation directory before Fast Reader/Viewer closes.
        File.Copy(downloadedPath, staged, overwrite: true);
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $source = {{Quote(downloadedPath)}}
            $target = {{Quote(target)}}
            $staged = {{Quote(staged)}}
            $backup = {{Quote(backup)}}
            $log = {{Quote(log)}}
            try {
                try { Wait-Process -Id {{processId}} -Timeout 60 -ErrorAction SilentlyContinue } catch { }
                if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Force }
                [System.IO.File]::Replace($staged, $target, $backup, $true)
                Start-Process -FilePath $target
                Start-Sleep -Milliseconds 750
                Remove-Item -LiteralPath $backup -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $source -Force -ErrorAction SilentlyContinue
            } catch {
                New-Item -ItemType Directory -Path (Split-Path -Parent $log) -Force | Out-Null
                ($_ | Out-String) | Set-Content -LiteralPath $log
                if ((Test-Path -LiteralPath $backup) -and -not (Test-Path -LiteralPath $target)) {
                    Move-Item -LiteralPath $backup -Destination $target -Force
                }
            }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var powerShell = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powerShell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-WindowStyle");
        startInfo.ArgumentList.Add("Hidden");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);
        if (Process.Start(startInfo) is null)
            throw new InvalidOperationException("The update helper could not be started.");
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Fast-Reader-Viewer/" + CurrentDisplayVersion);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static Version ParseTagVersion(string tag)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        var separator = value.IndexOfAny(['-', '+']);
        if (separator >= 0) value = value[..separator];
        if (!Version.TryParse(value, out var version))
            throw new InvalidDataException($"GitHub release tag '{tag}' is not a version number.");
        return NormalizeVersion(version);
    }

    private static Version NormalizeVersion(Version value) => new(
        Math.Max(0, value.Major), Math.Max(0, value.Minor),
        Math.Max(0, value.Build), Math.Max(0, value.Revision));

    private static string FormatVersion(Version value) =>
        $"{value.Major}.{value.Minor}.{Math.Max(0, value.Build)}";

    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024d * 1024):N1} MB"
        : $"{Math.Max(0, bytes) / 1024d:N1} KB";

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = string.Empty;
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? Digest { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}

internal sealed class UpdateDownloadDialog : Form
{
    private readonly Label _status = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.FromArgb(55, 64, 78)
    };
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill, Minimum = 0, Maximum = 100
    };

    public UpdateDownloadDialog(string current, string latest)
    {
        Text = "Updating Fast Reader/Viewer";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(470, 130);
        Font = new Font("Segoe UI", 9.5f);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(22, 16, 22, 18)
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            Text = $"Downloading {latest} to replace {current}",
            Dock = DockStyle.Fill, Font = new Font("Segoe UI Semibold", 10f),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        layout.Controls.Add(_progress, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        _status.Text = "Downloading from GitHub...";
        Controls.Add(layout);
    }

    public void SetProgress(int value)
    {
        if (IsDisposed) return;
        value = Math.Clamp(value, 0, 100);
        _progress.Value = value;
        _status.Text = $"Downloading from GitHub... {value}%";
    }

    public void SetStatus(string text)
    {
        if (!IsDisposed) _status.Text = text;
    }
}
