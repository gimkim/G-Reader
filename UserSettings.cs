using System.Text.Json;

namespace CDisplayEx.CSharp;

internal sealed class UserSettings
{
    public bool DoublePage { get; set; } = true;
    public bool DoublePageOffset { get; set; }
    public bool AutoSingleLandscape { get; set; } = true;
    public bool ThumbnailMode { get; set; }
    public int ThumbnailImagesPerRow { get; set; } = 6;
    public Dictionary<string, int> ToolbarHotkeys { get; set; } = ToolbarHotkeyCatalog.CreateDefaults();
    public bool JapaneseMode { get; set; }
    public bool SliderVisible { get; set; } = true;
    public bool ToolbarVisible { get; set; } = true;
    public bool Shadow { get; set; } = true;
    public bool FitToScreen { get; set; } = true;
    public int LanczosQuality { get; set; } = 1;
    public int CacheAheadMB { get; set; } = 3072;
    public int CacheBehindMB { get; set; } = 1024;
    public int BackgroundArgb { get; set; } = Color.FromArgb(30, 32, 38).ToArgb();
    // 0 = off, 1 = natural file name, 2 = last-write time (oldest to newest).
    public int AdjacentBookOrder { get; set; }
    public bool IncludeFoldersInAdjacentBooks { get; set; }
    public string RandomLibraryPath { get; set; } = string.Empty;
    public bool HasWindowBounds { get; set; }
    public bool WindowMaximized { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; } = 1100;
    public int WindowHeight { get; set; } = 760;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "G Reader", "settings.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CDisplayEx.CSharp", "settings.json");

    public static UserSettings Load()
    {
        try
        {
            var path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            return File.Exists(path)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    public void Save()
    {
        var folder = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
