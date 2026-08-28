using System.Runtime.InteropServices;

namespace AiCoreMonitor.WinUI.Interop;

internal sealed class TrayIconController : IDisposable
{
    private const uint NimAdd = 0, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4;
    private const uint CallbackMessage = 0x8000 + 42, WmLButtonDblClk = 0x0203, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205;
    private const uint MfString = 0, TpmRightButton = 0x0002;
    private const uint ShowCommand = 1, ExitCommand = 2;
    private readonly string _className = $"AiCoreMonitor.Tray.{Guid.NewGuid():N}";
    private readonly Action _show;
    private readonly Action _exit;
    private readonly nint _icon;
    private readonly WindowProcedure _procedure;
    private readonly nint _module;
    private nint _window;
    private bool _disposed;

    public TrayIconController(Action show, Action exit, nint icon)
    {
        _show = show;
        _exit = exit;
        _icon = icon;
        _procedure = WindowProc;
        _module = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Instance = _module,
            Procedure = Marshal.GetFunctionPointerForDelegate(_procedure), ClassName = _className
        };
        if (RegisterClassEx(ref windowClass) == 0)
            throw new InvalidOperationException("Could not register the tray icon window class.");

        _window = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0, 0, 0, _module, 0);
        if (_window == 0)
            throw new InvalidOperationException("Could not create the tray icon message window.");

        var data = CreateIconData(includeDetails: true);
        if (!ShellNotifyIcon(NimAdd, ref data))
            throw new InvalidOperationException("Could not add the notification-area icon.");
    }

    private nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            if ((uint)lParam is WmLButtonUp or WmLButtonDblClk)
                _show();
            else if ((uint)lParam == WmRButtonUp)
                ShowContextMenu();
        }
        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0) return;
        try
        {
            _ = AppendMenu(menu, MfString, ShowCommand, "Show AI Core Monitor");
            _ = AppendMenu(menu, MfString, ExitCommand, "Exit");
            _ = GetCursorPos(out var point);
            _ = SetForegroundWindow(_window);
            var command = TrackPopupMenu(menu, TpmRightButton, point.X, point.Y, 0, _window, 0);
            if (command == ShowCommand) _show();
            if (command == ExitCommand) _exit();
        }
        finally { _ = DestroyMenu(menu); }
    }

    private NotifyIconData CreateIconData(bool includeDetails) => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = 1,
        Flags = includeDetails ? NifMessage | NifIcon | NifTip : 0,
        CallbackMessage = CallbackMessage,
        Icon = includeDetails ? _icon : 0,
        Tip = includeDetails ? "AI Core Monitor" : string.Empty
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != 0)
        {
            var data = CreateIconData(includeDetails: false);
            _ = ShellNotifyIcon(NimDelete, ref data);
            _ = DestroyWindow(_window);
            _window = 0;
        }
        _ = UnregisterClass(_className, _module);
    }

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size; public uint Style; public nint Procedure; public int ClassExtra; public int WindowExtra;
        public nint Instance; public nint Icon; public nint Cursor; public nint Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id; public uint Flags; public uint CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? moduleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, uint id, string text);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rectangle);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
}
