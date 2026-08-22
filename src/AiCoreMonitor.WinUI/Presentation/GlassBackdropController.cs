using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinRT;

namespace AiCoreMonitor.WinUI.Presentation;

internal enum PanelVisualMode
{
    Regular,
    Freeze,
    Lava
}

internal sealed class GlassBackdropController : IDisposable
{
    private readonly SystemBackdropConfiguration _configuration = new()
    {
        IsInputActive = true,
        Theme = SystemBackdropTheme.Dark
    };
    private readonly DesktopAcrylicController? _controller;

    public GlassBackdropController(Window window)
    {
        if (!DesktopAcrylicController.IsSupported()) return;

        _controller = new DesktopAcrylicController();
        ApplyTheme(PanelVisualMode.Regular);
        _controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    public void ApplyTheme(PanelVisualMode mode)
    {
        if (_controller is null) return;
        var (tint, tintOpacity, luminosityOpacity, fallback) = mode switch
        {
            PanelVisualMode.Freeze => (Color.FromArgb(255, 7, 13, 27), 0.62f, 0.08f, Color.FromArgb(255, 10, 16, 29)),
            PanelVisualMode.Lava => (Color.FromArgb(255, 25, 4, 2), 1.0f, 0.0f, Color.FromArgb(255, 20, 5, 3)),
            _ => (Color.FromArgb(255, 5, 10, 18), 1.0f, 0.0f, Color.FromArgb(255, 7, 12, 21))
        };
        _controller.TintColor = tint;
        _controller.TintOpacity = tintOpacity;
        _controller.LuminosityOpacity = luminosityOpacity;
        _controller.FallbackColor = fallback;
    }

    public void Dispose() => _controller?.Dispose();
}
