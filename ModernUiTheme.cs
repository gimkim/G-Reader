using System.Drawing;
using System.Windows.Forms;

namespace CDisplayEx.CSharp;

/// <summary>
/// Compact, dark UI palette shared by the WinForms chrome.  The thumbnail
/// surface has its own Direct2D brushes, but uses the same colors (see
/// ThumbnailGridView.Direct2D.cs) so the chrome and grid read as one surface.
/// </summary>
internal static class ModernUiTheme
{
    public static readonly Color WindowBackground = Color.FromArgb(13, 19, 30);
    public static readonly Color HeaderBackground = Color.FromArgb(16, 25, 38);
    public static readonly Color HeaderEdge = Color.FromArgb(38, 55, 76);
    public static readonly Color ControlBackground = Color.FromArgb(18, 29, 44);
    public static readonly Color ControlRaised = Color.FromArgb(25, 39, 58);
    public static readonly Color ControlBorder = Color.FromArgb(43, 62, 85);
    public static readonly Color Text = Color.FromArgb(231, 239, 249);
    public static readonly Color MutedText = Color.FromArgb(145, 168, 193);
    public static readonly Color Accent = Color.FromArgb(111, 178, 255);
    public static readonly Color AccentPressed = Color.FromArgb(78, 143, 222);

    public static void Apply(
        Form form, MenuStrip menu, ToolStrip toolbar, Panel bottom,
        Label status, Panel thumbnailPanel, Panel thumbnailControls,
        Panel thumbnailAddress, TextBox thumbnailAddressBox,
        Label thumbnailColumnsLabel, TrackBar thumbnailColumnsSlider)
    {
        form.BackColor = WindowBackground;
        form.ForeColor = Text;

        menu.BackColor = HeaderBackground;
        menu.ForeColor = Text;
        menu.Padding = new Padding(4, 1, 4, 1);
        menu.RenderMode = ToolStripRenderMode.Professional;
        menu.Renderer = new CompactToolStripRenderer();

        toolbar.BackColor = HeaderBackground;
        toolbar.ForeColor = Text;
        toolbar.RenderMode = ToolStripRenderMode.Professional;
        toolbar.Renderer = new CompactToolStripRenderer();
        toolbar.ImageScalingSize = new Size(20, 20);
        toolbar.Padding = new Padding(5, 2, 5, 2);
        toolbar.AutoSize = true;

        bottom.BackColor = HeaderBackground;
        bottom.Padding = new Padding(0, 1, 0, 1);
        status.BackColor = HeaderBackground;
        status.ForeColor = MutedText;
        status.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        status.Padding = new Padding(8, 0, 6, 0);

        thumbnailPanel.BackColor = WindowBackground;
        thumbnailControls.BackColor = HeaderBackground;
        thumbnailControls.Padding = new Padding(6, 2, 8, 2);
        thumbnailAddress.BackColor = WindowBackground;
        thumbnailAddress.Padding = new Padding(6, 4, 8, 4);
        thumbnailAddressBox.BackColor = ControlRaised;
        thumbnailAddressBox.ForeColor = Text;
        thumbnailAddressBox.BorderStyle = BorderStyle.FixedSingle;
        thumbnailAddressBox.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        thumbnailColumnsLabel.ForeColor = MutedText;
        thumbnailColumnsLabel.Font = new Font("Segoe UI", 9f, FontStyle.Regular);
        thumbnailColumnsSlider.BackColor = HeaderBackground;
    }
}

internal sealed class CompactToolStripRenderer : ToolStripProfessionalRenderer
{
    public CompactToolStripRenderer() : base(new CompactColorTable())
    {
        RoundedEdges = false;
    }
}

internal sealed class CompactColorTable : ProfessionalColorTable
{
    public override Color MenuBorder => ModernUiTheme.HeaderEdge;
    public override Color MenuItemBorder => ModernUiTheme.Accent;
    public override Color MenuItemSelected => ModernUiTheme.ControlRaised;
    public override Color MenuItemSelectedGradientBegin => ModernUiTheme.ControlRaised;
    public override Color MenuItemSelectedGradientEnd => ModernUiTheme.ControlRaised;
    public override Color MenuStripGradientBegin => ModernUiTheme.HeaderBackground;
    public override Color MenuStripGradientEnd => ModernUiTheme.HeaderBackground;
    public override Color ToolStripDropDownBackground => ModernUiTheme.ControlBackground;
    public override Color ImageMarginGradientBegin => ModernUiTheme.ControlBackground;
    public override Color ImageMarginGradientMiddle => ModernUiTheme.ControlBackground;
    public override Color ImageMarginGradientEnd => ModernUiTheme.ControlBackground;
    public override Color ToolStripGradientBegin => ModernUiTheme.HeaderBackground;
    public override Color ToolStripGradientMiddle => ModernUiTheme.HeaderBackground;
    public override Color ToolStripGradientEnd => ModernUiTheme.HeaderBackground;
    public override Color ToolStripContentPanelGradientBegin => ModernUiTheme.WindowBackground;
    public override Color ToolStripContentPanelGradientEnd => ModernUiTheme.WindowBackground;
    public override Color ToolStripPanelGradientBegin => ModernUiTheme.WindowBackground;
    public override Color ToolStripPanelGradientEnd => ModernUiTheme.WindowBackground;
    public override Color ButtonSelectedBorder => ModernUiTheme.Accent;
    public override Color ButtonSelectedGradientBegin => ModernUiTheme.ControlRaised;
    public override Color ButtonSelectedGradientMiddle => ModernUiTheme.ControlRaised;
    public override Color ButtonSelectedGradientEnd => ModernUiTheme.ControlRaised;
    public override Color ButtonPressedBorder => ModernUiTheme.AccentPressed;
    public override Color ButtonPressedGradientBegin => ModernUiTheme.AccentPressed;
    public override Color ButtonPressedGradientMiddle => ModernUiTheme.AccentPressed;
    public override Color ButtonPressedGradientEnd => ModernUiTheme.AccentPressed;
    public override Color CheckBackground => ModernUiTheme.ControlRaised;
    public override Color CheckPressedBackground => ModernUiTheme.AccentPressed;
    public override Color CheckSelectedBackground => ModernUiTheme.ControlRaised;
    public override Color SeparatorDark => ModernUiTheme.HeaderEdge;
    public override Color SeparatorLight => ModernUiTheme.HeaderEdge;
}
