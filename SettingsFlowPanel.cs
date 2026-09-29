namespace CDisplayEx.CSharp;

internal sealed class SettingsFlowPanel : FlowLayoutPanel
{
    protected override void OnLayout(LayoutEventArgs e)
    {
        foreach (Control child in Controls)
        {
            var width = Math.Max(1, ClientSize.Width - Padding.Horizontal - child.Margin.Horizontal);
            if (child is Button or Label)
            {
                if (child.MinimumSize.Width > width)
                    child.MinimumSize = new Size(0, child.MinimumSize.Height);
                var maximum = new Size(width, 0);
                if (child.MaximumSize != maximum) child.MaximumSize = maximum;
            }
            else if (child is ProgressBar)
                child.Width = Math.Min(width, LogicalToDeviceUnits(260));
        }
        base.OnLayout(e);
    }
}
