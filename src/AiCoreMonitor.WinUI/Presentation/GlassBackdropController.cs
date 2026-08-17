using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinRT;

namespace AiCoreMonitor.WinUI.Presentation;

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

        _controller = new DesktopAcrylicController
        {
            TintColor = Color.FromArgb(255, 7, 13, 27),
            TintOpacity = 0.18f,
            LuminosityOpacity = 0.13f,
            FallbackColor = Color.FromArgb(255, 10, 16, 29)
        };
        _controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    public void Dispose() => _controller?.Dispose();
}
