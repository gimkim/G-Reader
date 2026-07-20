namespace CDisplayEx.CSharp;

/// <summary>
/// Activates the owner window without consuming the mouse click that performed
/// the activation. Standard ToolStrip can return MA_ACTIVATEANDEAT when its form
/// is inactive, making the first toolbar click appear unresponsive.
/// </summary>
internal sealed class ClickThroughToolStrip : ToolStrip
{
    private const int WmMouseActivate = 0x0021;
    private static readonly IntPtr MaActivate = new(1);
    private static readonly IntPtr MaActivateAndEat = new(2);

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == WmMouseActivate && message.Result == MaActivateAndEat)
            message.Result = MaActivate;
    }
}
