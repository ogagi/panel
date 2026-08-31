using System.Runtime.InteropServices;

namespace AiCoreMonitor.WinUI.Interop;

internal static partial class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;
    private const int GwlStyle = -16;
    private const uint WmSetIcon = 0x0080;
    private const uint WmNcLButtonDown = 0x00A1;
    private const nint HtCaption = 2;
    private static readonly nint IconSmall = 0;
    private static readonly nint IconBig = 1;
    private const nint WsMaximizeBox = 0x00010000;

    public static void Apply(nint windowHandle)
    {
        var enabled = 1;
        var corner = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(windowHandle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    public static void Hide(nint windowHandle) => _ = ShowWindow(windowHandle, 0);
    public static void Show(nint windowHandle) => _ = ShowWindow(windowHandle, 5);

    public static void BeginMove(nint windowHandle)
    {
        _ = ReleaseCapture();
        _ = SendMessage(windowHandle, WmNcLButtonDown, HtCaption, 0);
    }

    public static void SetIcon(nint windowHandle, nint icon)
    {
        _ = SendMessage(windowHandle, WmSetIcon, IconSmall, icon);
        _ = SendMessage(windowHandle, WmSetIcon, IconBig, icon);
    }

    public static void DisableMaximize(nint windowHandle)
    {
        var style = GetWindowLongPtr(windowHandle, GwlStyle);
        _ = SetWindowLongPtr(windowHandle, GwlStyle, style & ~WsMaximizeBox);
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReleaseCapture();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint window, int index, nint value);

}
