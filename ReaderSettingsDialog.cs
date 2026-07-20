namespace CDisplayEx.CSharp;

internal sealed class ReaderSettingsDialog : Form
{
    private readonly ComboBox _quality = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly NumericUpDown _ahead = CreateMemoryInput();
    private readonly NumericUpDown _behind = CreateMemoryInput();
    private readonly ComboBox _adjacentBooks = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _includeFolders = new() { Text = "Include image folders", AutoSize = true, Dock = DockStyle.Fill };
    private readonly Panel _colorPreview = new() { Width = 92, Height = 28, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _randomLibraryPath = new() { Width = 255 };
    private readonly Dictionary<string, HotkeyTextBox> _hotkeyEditors = new(StringComparer.Ordinal);

    public int LanczosQuality => _quality.SelectedIndex;
    public int CacheAheadMB => (int)_ahead.Value;
    public int CacheBehindMB => (int)_behind.Value;
    public Color ReaderBackground { get; private set; }
    public int AdjacentBookOrder => _adjacentBooks.SelectedIndex;
    public bool IncludeFoldersInAdjacentBooks => _includeFolders.Checked;
    public string RandomLibraryPath => _randomLibraryPath.Text.Trim();
    public Dictionary<string, int> ToolbarHotkeys => _hotkeyEditors.ToDictionary(
        pair => pair.Key, pair => (int)pair.Value.Shortcut, StringComparer.Ordinal);

    public ReaderSettingsDialog(UserSettings settings)
    {
        Text = "G Reader Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 575);
        Font = new Font("Segoe UI", 9f);

        _quality.Items.AddRange([
            "Fast — Lanczos2",
            "Balanced — Lanczos",
            "Sharp — LanczosSharp",
            "Maximum radius — LanczosRadius"
        ]);
        _quality.SelectedIndex = Math.Clamp(settings.LanczosQuality, 0, _quality.Items.Count - 1);
        _ahead.Value = Math.Clamp(settings.CacheAheadMB, (int)_ahead.Minimum, (int)_ahead.Maximum);
        _behind.Value = Math.Clamp(settings.CacheBehindMB, (int)_behind.Minimum, (int)_behind.Maximum);
        _adjacentBooks.Items.AddRange([
            "Off — stay at first/last page",
            "File name — natural numeric order",
            "Date modified — oldest to newest"
        ]);
        _adjacentBooks.SelectedIndex = Math.Clamp(settings.AdjacentBookOrder, 0, 2);
        _includeFolders.Checked = settings.IncludeFoldersInAdjacentBooks;
        _randomLibraryPath.Text = settings.RandomLibraryPath ?? string.Empty;
        _includeFolders.Enabled = _adjacentBooks.SelectedIndex != 0;
        _adjacentBooks.SelectedIndexChanged += (_, _) =>
            _includeFolders.Enabled = _adjacentBooks.SelectedIndex != 0;
        ReaderBackground = Color.FromArgb(settings.BackgroundArgb);
        _colorPreview.BackColor = ReaderBackground;

        var chooseColor = new Button { Text = "Choose…", AutoSize = true };
        chooseColor.Click += (_, _) => ChooseBackground();
        _colorPreview.Click += (_, _) => ChooseBackground();
        var colorRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        colorRow.Controls.AddRange([_colorPreview, chooseColor]);

        var browseRandomPath = new Button { Text = "Browse…", AutoSize = true };
        browseRandomPath.Click += (_, _) => ChooseRandomLibraryPath();
        var randomPathRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        randomPathRow.Controls.AddRange([_randomLibraryPath, browseRandomPath]);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 301, ColumnCount = 2, RowCount = 7,
            Padding = new Padding(16, 16, 16, 0)
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 155));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        AddField(fields, 0, "Lanczos quality", _quality);
        AddField(fields, 1, "Cache ahead (MB)", _ahead);
        AddField(fields, 2, "Cache behind (MB)", _behind);
        AddField(fields, 3, "Background color", colorRow);
        AddField(fields, 4, "At book boundary", _adjacentBooks);
        AddField(fields, 5, "Adjacent book types", _includeFolders);
        AddField(fields, 6, "Random library path", randomPathRow);

        var generalPage = new TabPage("General") { Padding = new Padding(0) };
        generalPage.Controls.Add(fields);

        var hotkeyTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2,
            Padding = new Padding(14, 10, 14, 10)
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
            Dock = DockStyle.Top, Height = 34, Padding = new Padding(16, 8, 0, 0),
            ForeColor = SystemColors.GrayText
        };
        var resetHotkeys = new Button { Text = "Reset defaults", AutoSize = true };
        resetHotkeys.Click += (_, _) => ResetHotkeys();
        var hotkeyCommands = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(12, 6, 0, 4)
        };
        hotkeyCommands.Controls.Add(resetHotkeys);
        var hotkeyScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        hotkeyScroll.Controls.Add(hotkeyTable);
        var hotkeyPage = new TabPage("Hotkeys");
        hotkeyPage.Controls.Add(hotkeyScroll);
        hotkeyPage.Controls.Add(hotkeyCommands);
        hotkeyPage.Controls.Add(hotkeyHint);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(generalPage);
        tabs.TabPages.Add(hotkeyPage);

        var ok = new Button { Text = "OK", Width = 88 };
        ok.Click += (_, _) => AcceptSettings();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88 };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 54, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 10, 14, 8)
        };
        buttons.Controls.AddRange([cancel, ok]);
        Controls.Add(tabs);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void AcceptSettings()
    {
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

    private static NumericUpDown CreateMemoryInput() => new()
    {
        Minimum = 0, Maximum = 65536, Increment = 128,
        ThousandsSeparator = true, Dock = DockStyle.Fill
    };

    private static void AddField(TableLayoutPanel table, int row, string label, Control input)
    {
        table.Controls.Add(new Label
        {
            Text = label, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);
        table.Controls.Add(input, 1, row);
    }
}
