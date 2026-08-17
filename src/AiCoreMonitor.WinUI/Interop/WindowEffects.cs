using System.Runtime.InteropServices;

namespace AiCoreMonitor.WinUI.Interop;

internal static partial class WindowEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    public static void Apply(nint windowHandle)
    {
        var enabled = 1;
        var corner = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(windowHandle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        _ = DwmSetWindowAttribute(windowHandle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int valueSize);
}
