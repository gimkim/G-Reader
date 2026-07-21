namespace CDisplayEx.CSharp;

/// <summary>
/// A layered owned window keeps the fullscreen slider translucent without
/// reserving layout space in the reader viewport. It deliberately never
/// activates, so keyboard navigation remains with the main window.
/// </summary>
internal sealed class FullscreenSliderOverlay : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    public FullscreenSliderOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        Opacity = 0.78d;
        BackColor = Color.FromArgb(28, 30, 36);
    }
}
