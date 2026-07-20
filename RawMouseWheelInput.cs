using System.Runtime.InteropServices;

namespace CDisplayEx.CSharp;

/// <summary>
/// Receives background mouse-wheel input asynchronously through WM_INPUT.
/// Unlike WH_MOUSE_LL, raw input is not part of the synchronous system cursor path.
/// </summary>
internal static class RawMouseWheelInput
{
    public const int WindowMessage = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevRemove = 0x00000001;
    private const uint RidevInputSink = 0x00000100;
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsageMouse = 0x02;
    private const ushort RawMouseWheel = 0x0400;

    public static bool Register(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        var device = new RawInputDevice
        {
            UsagePage = UsagePageGenericDesktop,
            Usage = UsageMouse,
            Flags = RidevInputSink,
            TargetWindow = window
        };
        return RegisterRawInputDevices(
            [device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public static void Unregister()
    {
        var device = new RawInputDevice
        {
            UsagePage = UsagePageGenericDesktop,
            Usage = UsageMouse,
            Flags = RidevRemove,
            TargetWindow = IntPtr.Zero
        };
        RegisterRawInputDevices([device], 1, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public static bool TryGetWheelDelta(IntPtr rawInputHandle, out int delta)
    {
        delta = 0;
        var size = (uint)Marshal.SizeOf<RawInput>();
        var result = GetRawInputData(
            rawInputHandle, RidInput, out var input, ref size,
            (uint)Marshal.SizeOf<RawInputHeader>());
        if (result == uint.MaxValue || input.Header.Type != 0) return false;

        var buttonFlags = (ushort)(input.Mouse.Buttons & 0xffff);
        if ((buttonFlags & RawMouseWheel) == 0) return false;
        delta = (short)(input.Mouse.Buttons >> 16);
        return delta != 0;
    }

    public static bool IsWindowOrChildAtPoint(IntPtr rootWindow, Point screenPoint)
    {
        if (rootWindow == IntPtr.Zero) return false;
        var hitWindow = WindowFromPoint(new NativePoint { X = screenPoint.X, Y = screenPoint.Y });
        return hitWindow == rootWindow ||
               hitWindow != IntPtr.Zero && IsChild(rootWindow, hitWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr TargetWindow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawMouse
    {
        public ushort Flags;
        private ushort _alignment;
        public uint Buttons;
        public uint RawButtons;
        public int LastX;
        public int LastY;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInput
    {
        public RawInputHeader Header;
        public RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices, uint numberOfDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput, uint command, out RawInput data, ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parentWindow, IntPtr window);
}
