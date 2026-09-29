namespace CDisplayEx.CSharp;

// Narrow windows use a full-width editor below its label. The threshold is in
// logical pixels, so moving to a high-DPI monitor uses the same layout rule.
internal sealed class SettingsFieldTable : TableLayoutPanel
{
    private bool _updating;
    private bool? _stacked;
    private int _controlCount;

    protected override void OnLayout(LayoutEventArgs e)
    {
        var stacked = ClientSize.Width < LogicalToDeviceUnits(620);
        if (!_updating && Controls.Count > 0 && Controls.Count % 2 == 0 &&
            (_stacked != stacked || _controlCount != Controls.Count))
        {
            _updating = true;
            SuspendLayout();
            try
            {
                _stacked = stacked;
                _controlCount = Controls.Count;
                // Clear spans before moving cells to avoid transient conflicts.
                foreach (Control control in Controls) SetColumnSpan(control, 1);
                RowCount = stacked ? Controls.Count : Controls.Count / 2;
                RowStyles.Clear();
                for (var row = 0; row < RowCount; row++)
                    RowStyles.Add(new RowStyle(SizeType.AutoSize));
                for (var i = 0; i < Controls.Count; i++)
                {
                    SetCellPosition(Controls[i], stacked
                        ? new TableLayoutPanelCellPosition(0, i)
                        : new TableLayoutPanelCellPosition(i % 2, i / 2));
                    SetColumnSpan(Controls[i], stacked ? 2 : 1);
                }
            }
            finally { ResumeLayout(false); _updating = false; }
        }
        base.OnLayout(e);
    }
}
