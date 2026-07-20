using System.ComponentModel;

namespace CDisplayEx.CSharp;

internal sealed record ToolbarHotkeyDefinition(string Id, string Label, Keys DefaultShortcut);

internal static class ToolbarHotkeyCatalog
{
    public const string OpenFile = "open_file";
    public const string OpenFolder = "open_folder";
    public const string OpenRandom = "open_random";
    public const string OpenInExplorer = "open_in_explorer";
    public const string MoveUp = "move_up";
    public const string Start = "start";
    public const string Left = "left";
    public const string Right = "right";
    public const string End = "end";
    public const string ViewMode = "view_mode";
    public const string PageLayout = "page_layout";
    public const string AutoSingleLandscape = "auto_single_landscape";
    public const string ReadingDirection = "reading_direction";
    public const string Settings = "settings";

    public static IReadOnlyList<ToolbarHotkeyDefinition> All { get; } =
    [
        new(OpenFile, "Open file", Keys.Control | Keys.L),
        new(OpenFolder, "Open folder", Keys.Control | Keys.O),
        new(OpenRandom, "Open random", Keys.Control | Keys.R),
        new(OpenInExplorer, "Open in Explorer", Keys.Control | Keys.Shift | Keys.E),
        new(MoveUp, "Move up", Keys.Alt | Keys.Up),
        new(Start, "Start", Keys.Home),
        new(Left, "Left", Keys.Left),
        new(Right, "Right", Keys.Right),
        new(End, "End", Keys.End),
        new(ViewMode, "Full page / Thumbnail grid", Keys.Control | Keys.T),
        new(PageLayout, "Page layout", Keys.Control | Keys.D),
        new(AutoSingleLandscape, "Auto-single landscape", Keys.Control | Keys.Shift | Keys.A),
        new(ReadingDirection, "LTR / RTL", Keys.Control | Keys.J),
        new(Settings, "Settings", Keys.Control | Keys.Oemcomma)
    ];

    public static Dictionary<string, int> CreateDefaults() =>
        All.ToDictionary(item => item.Id, item => (int)item.DefaultShortcut, StringComparer.Ordinal);

    public static Keys GetShortcut(IReadOnlyDictionary<string, int>? values, string id)
    {
        if (values is not null && values.TryGetValue(id, out var stored))
            return Normalize((Keys)stored);
        return All.FirstOrDefault(item => item.Id == id)?.DefaultShortcut ?? Keys.None;
    }

    public static Keys Normalize(Keys shortcut) => shortcut & (Keys.KeyCode | Keys.Modifiers);

    public static bool IsReserved(Keys shortcut)
    {
        shortcut = Normalize(shortcut);
        return shortcut is (Keys.Control | Keys.C) or (Keys.Control | Keys.V);
    }

    public static string Format(Keys shortcut)
    {
        shortcut = Normalize(shortcut);
        if (shortcut == Keys.None) return "None";
        var text = new KeysConverter().ConvertToString(shortcut) ?? shortcut.ToString();
        return text.Replace("Oemcomma", ",", StringComparison.OrdinalIgnoreCase)
            .Replace("OemPeriod", ".", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class HotkeyTextBox : TextBox
{
    private Keys _shortcut;

    public Keys Shortcut
    {
        get => _shortcut;
        set
        {
            _shortcut = ToolbarHotkeyCatalog.Normalize(value);
            Text = ToolbarHotkeyCatalog.Format(_shortcut);
        }
    }

    public HotkeyTextBox()
    {
        ReadOnly = true;
        Dock = DockStyle.Fill;
        TextAlign = HorizontalAlignment.Center;
        BackColor = SystemColors.Window;
        TabStop = true;
    }

    protected override bool IsInputKey(Keys keyData) => true;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var shortcut = ToolbarHotkeyCatalog.Normalize(keyData);
        var keyCode = shortcut & Keys.KeyCode;
        if (keyCode == Keys.Tab)
            return base.ProcessCmdKey(ref msg, keyData);
        if (keyCode == Keys.Escape || shortcut == (Keys.Alt | Keys.F4))
        {
            if (FindForm() is { } form)
            {
                form.DialogResult = DialogResult.Cancel;
                form.Close();
            }
            return true;
        }
        if (keyCode is Keys.Back or Keys.Delete)
        {
            Shortcut = Keys.None;
            return true;
        }
        if (keyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None)
            return true;
        if (ToolbarHotkeyCatalog.IsReserved(shortcut))
        {
            System.Media.SystemSounds.Beep.Play();
            return true;
        }
        Shortcut = shortcut;
        return true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        e.SuppressKeyPress = true;
        if (e.KeyCode is Keys.Back or Keys.Delete)
        {
            Shortcut = Keys.None;
            return;
        }
        if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return;
        Shortcut = e.Modifiers | e.KeyCode;
    }
}
