using AiCoreMonitor.Infrastructure;
using AiCoreMonitor.Services;
using AiCoreMonitor.ViewModels;
using AiCoreMonitor.WinUI.Interop;
using AiCoreMonitor.WinUI.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace AiCoreMonitor.WinUI;

public sealed partial class MainWindow : Window
{
    private const double MinimumWidth = 340;
    private const double MinimumHeight = 480;
    private const double MaximumWidth = 920;
    private const double MaximumHeight = 1120;
    private const double EdgeSnapDistance = 24;
    private readonly MainViewModel _viewModel = new(new TelemetryService());
    private readonly WidgetSettingsStore _settingsStore = new();
    private readonly WidgetSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _edgeSnapTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly LavaCompositionController _lava;
    private readonly SparklineCompositionController _cpuSparkline;
    private readonly SparklineCompositionController _sparkline;
    private readonly GlassBackdropController _glass;
    private LavaOverlayWindow? _overlay;
    private AppWindow? _appWindow;
    private PanelVisualMode _visualMode;
    private double _rasterizationScale = 1;
    private int _tick;
    private bool _closing;
    private bool _correctingSize;
    private bool _snappingToEdge;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        _settings.Width = Math.Clamp(_settings.Width, MinimumWidth, MaximumWidth);
        _settings.Height = Math.Clamp(_settings.Height, MinimumHeight, MaximumHeight);
        Root.DataContext = _viewModel;
        _glass = new GlassBackdropController(this);
        _visualMode = Enum.TryParse<PanelVisualMode>(_settings.VisualMode, true, out var savedMode)
            ? savedMode
            : InferVisualMode(_settings.LavaEnabled ?? _settings.AnimationEnabled,
                _settings.CracksEnabled ?? _settings.AnimationEnabled);
        var lavaEnabled = _visualMode == PanelVisualMode.Lava;
        var cracksEnabled = _visualMode != PanelVisualMode.Regular;
        _settings.LavaEnabled = lavaEnabled;
        _settings.CracksEnabled = cracksEnabled;
        _settings.VisualMode = _visualMode.ToString();
        _settings.LavaAmount = Math.Clamp(_settings.LavaAmount ?? _settings.EffectIntensity, 0.1, 1);
        _settings.CrackAmount = Math.Clamp(_settings.CrackAmount ?? _settings.EffectIntensity, 0.1, 1);
        _lava = new LavaCompositionController(LavaHost)
        {
            IsEnabled = lavaEnabled || cracksEnabled,
            LavaEnabled = lavaEnabled,
            CracksEnabled = cracksEnabled,
            LavaAmount = (float)_settings.LavaAmount.Value,
            CrackAmount = (float)_settings.CrackAmount.Value
        };
        _cpuSparkline = new SparklineCompositionController(CpuSparklineHost);
        _sparkline = new SparklineCompositionController(GpuSparklineHost);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateModeControls();
        _refreshTimer.Tick += RefreshTimer_Tick;
        _edgeSnapTimer.Tick += EdgeSnapTimer_Tick;
        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs args)
    {
        if (_appWindow is not null) return;

        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Title = "AI Core Monitor";
        _rasterizationScale = Math.Max(1, Root.XamlRoot?.RasterizationScale ?? 1);
        _appWindow.Resize(new SizeInt32(
            LogicalToPhysical(_settings.Width),
            LogicalToPhysical(_settings.Height)));
        if (double.IsFinite(_settings.Left) && double.IsFinite(_settings.Top))
            _appWindow.Move(new PointInt32(LogicalToPhysical(_settings.Left), LogicalToPhysical(_settings.Top)));
        else
            MoveToDefaultPosition(_appWindow);
        EnsureVisiblePosition(_appWindow);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            presenter.IsAlwaysOnTop = _settings.Topmost;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        HeaderActions.Margin = new Thickness(0, 0, 2, 0);
        WindowEffects.Apply(windowHandle);
        _appWindow.Closing += AppWindow_Closing;
        _appWindow.Changed += AppWindow_Changed;
        _overlay = new LavaOverlayWindow
        {
            IsEnabled = _visualMode != PanelVisualMode.Regular,
            IsFrozen = _visualMode == PanelVisualMode.Freeze,
            Amount = (float)(_visualMode == PanelVisualMode.Freeze
                ? _settings.CrackAmount.GetValueOrDefault(0.78)
                : _settings.LavaAmount.GetValueOrDefault(0.78))
        };
        SynchronizeOverlay();
        CaptureWindowSettings();
        try { _settingsStore.Save(_settings); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }

        await RefreshAsync(includeSlowProviders: true);
        _refreshTimer.Start();
    }

    private static void MoveToDefaultPosition(AppWindow window)
    {
        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        const int effectRunway = 128;
        window.Move(new PointInt32(work.X + work.Width - window.Size.Width - 24,
            work.Y + work.Height - window.Size.Height - effectRunway));
    }

    private static void EnsureVisiblePosition(AppWindow window)
    {
        var area = DisplayArea.GetFromWindowId(window.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var x = Math.Clamp(window.Position.X, work.X, Math.Max(work.X, work.X + work.Width - window.Size.Width));
        var y = Math.Clamp(window.Position.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - window.Size.Height));
        if (x != window.Position.X || y != window.Position.Y) window.Move(new PointInt32(x, y));
    }

    private int LogicalToPhysical(double value) => (int)Math.Round(value * _rasterizationScale);
    private double PhysicalToLogical(int value) => value / _rasterizationScale;

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_correctingSize)
        {
            var minimumWidth = LogicalToPhysical(MinimumWidth);
            var minimumHeight = LogicalToPhysical(MinimumHeight);
            var maximumWidth = LogicalToPhysical(MaximumWidth);
            var maximumHeight = LogicalToPhysical(MaximumHeight);
            var width = Math.Clamp(sender.Size.Width, minimumWidth, maximumWidth);
            var height = Math.Clamp(sender.Size.Height, minimumHeight, maximumHeight);
            if (width != sender.Size.Width || height != sender.Size.Height)
            {
                _correctingSize = true;
                sender.Resize(new SizeInt32(width, height));
                _correctingSize = false;
            }
        }
        if (args.DidPositionChange && !_snappingToEdge)
        {
            _edgeSnapTimer.Stop();
            _edgeSnapTimer.Start();
        }
        SynchronizeOverlay();
    }

    private void EdgeSnapTimer_Tick(object? sender, object e)
    {
        _edgeSnapTimer.Stop();
        if (_appWindow is null ||
            _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;

        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;
        var position = _appWindow.Position;
        var size = _appWindow.Size;
        var threshold = LogicalToPhysical(EdgeSnapDistance);
        var leftTarget = work.X;
        var rightTarget = work.X + work.Width - size.Width;
        var topTarget = work.Y;
        var bottomTarget = work.Y + work.Height - size.Height;
        var x = position.X;
        var y = position.Y;

        var leftDistance = Math.Abs(position.X - leftTarget);
        var rightDistance = Math.Abs(position.X - rightTarget);
        if (Math.Min(leftDistance, rightDistance) <= threshold)
            x = leftDistance <= rightDistance ? leftTarget : rightTarget;

        var topDistance = Math.Abs(position.Y - topTarget);
        var bottomDistance = Math.Abs(position.Y - bottomTarget);
        if (Math.Min(topDistance, bottomDistance) <= threshold)
            y = topDistance <= bottomDistance ? topTarget : bottomTarget;

        if (x == position.X && y == position.Y) return;

        _snappingToEdge = true;
        _appWindow.Move(new PointInt32(x, y));
        _snappingToEdge = false;
        SynchronizeOverlay();
        CaptureWindowSettings();
        try { _settingsStore.Save(_settings); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void SynchronizeOverlay()
    {
        if (_appWindow is null || _overlay is null) return;
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var overlayTop = _appWindow.Position.Y;
        var overlayHeight = Math.Max(1, work.Y + work.Height - overlayTop);
        var topmost = _appWindow.Presenter is OverlappedPresenter { IsAlwaysOnTop: true };
        var minimized = _appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        _overlay.IsFrozen = _visualMode == PanelVisualMode.Freeze;
        _overlay.IsEnabled = _visualMode != PanelVisualMode.Regular && !minimized;
        _overlay.UpdateBounds(_appWindow.Position.X, overlayTop, _appWindow.Size.Width, overlayHeight,
            _appWindow.Size.Height, topmost);
    }

    private async void RefreshTimer_Tick(object? sender, object e)
    {
        _tick++;
        await RefreshAsync(_tick % 5 == 0);
    }

    private async Task RefreshAsync(bool includeSlowProviders)
    {
        try
        {
            await _viewModel.RefreshAsync(includeSlowProviders, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.CpuSamples))
            _cpuSparkline.SetValues(_viewModel.CpuSamples);
        else if (args.PropertyName == nameof(MainViewModel.GpuSamples))
            _sparkline.SetValues(_viewModel.GpuSamples);
    }

    private void RegularModeButton_Click(object sender, RoutedEventArgs e) => SetVisualMode(PanelVisualMode.Regular);

    private void FreezeModeButton_Click(object sender, RoutedEventArgs e) => SetVisualMode(PanelVisualMode.Freeze);

    private void LavaModeButton_Click(object sender, RoutedEventArgs e) => SetVisualMode(PanelVisualMode.Lava);

    private void SetVisualMode(PanelVisualMode mode)
    {
        _visualMode = mode;
        _settings.VisualMode = mode.ToString();
        _settings.LavaEnabled = mode == PanelVisualMode.Lava;
        _settings.CracksEnabled = mode != PanelVisualMode.Regular;
        ApplyEffectSettings();
        CaptureWindowSettings();
        try { _settingsStore.Save(_settings); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void ApplyEffectSettings()
    {
        var lavaEnabled = _visualMode == PanelVisualMode.Lava;
        var cracksEnabled = _visualMode != PanelVisualMode.Regular;
        _settings.LavaEnabled = lavaEnabled;
        _settings.CracksEnabled = cracksEnabled;
        _settings.VisualMode = _visualMode.ToString();
        _settings.AnimationEnabled = lavaEnabled || cracksEnabled;
        _lava.IsEnabled = _settings.AnimationEnabled;
        _lava.LavaEnabled = lavaEnabled;
        _lava.CracksEnabled = cracksEnabled;
        _lava.LavaAmount = (float)_settings.LavaAmount!.Value;
        _lava.CrackAmount = (float)_settings.CrackAmount!.Value;
        if (_overlay is not null)
        {
            _overlay.IsFrozen = _visualMode == PanelVisualMode.Freeze;
            _overlay.Amount = (float)(_visualMode == PanelVisualMode.Freeze
                ? _settings.CrackAmount.Value
                : _settings.LavaAmount.Value);
            SynchronizeOverlay();
        }
        UpdateModeControls();
    }

    private void UpdateModeControls()
    {
        var mode = _visualMode;
        var regular = mode == PanelVisualMode.Regular;
        var freeze = mode == PanelVisualMode.Freeze;
        UpdateThemeButton(RegularModeButton, regular, Color.FromArgb(255, 220, 232, 244));
        UpdateThemeButton(FreezeModeButton, freeze, Color.FromArgb(255, 130, 225, 255));
        UpdateThemeButton(LavaModeButton, !regular && !freeze, Color.FromArgb(255, 255, 155, 45));
        ApplyTheme(mode);
    }

    private static void UpdateThemeButton(Button button, bool active, Color activeColor)
    {
        button.Opacity = active ? 1 : 0.55;
        button.Foreground = new SolidColorBrush(active
            ? activeColor
            : Color.FromArgb(255, 142, 168, 198));
        button.Background = new SolidColorBrush(active
            ? Color.FromArgb(58, activeColor.R, activeColor.G, activeColor.B)
            : Color.FromArgb(0, 0, 0, 0));
        button.BorderBrush = new SolidColorBrush(active
            ? Color.FromArgb(120, activeColor.R, activeColor.G, activeColor.B)
            : Color.FromArgb(0, 0, 0, 0));
        button.BorderThickness = active ? new Thickness(1) : new Thickness(0);
    }

    private void ApplyTheme(PanelVisualMode mode)
    {
        var palette = mode switch
        {
            PanelVisualMode.Freeze => new ThemePalette(
                Color.FromArgb(200, 9, 17, 29), Color.FromArgb(205, 126, 166, 205),
                [Color.FromArgb(255, 42, 223, 255), Color.FromArgb(255, 115, 136, 255), Color.FromArgb(255, 193, 92, 255)],
                [Color.FromArgb(180, 23, 39, 60), Color.FromArgb(166, 11, 20, 34), Color.FromArgb(176, 29, 24, 49)], 0.64),
            PanelVisualMode.Lava => new ThemePalette(
                Color.FromArgb(255, 18, 4, 2), Color.FromArgb(220, 240, 73, 20),
                [Color.FromArgb(255, 255, 207, 72), Color.FromArgb(255, 255, 107, 28), Color.FromArgb(255, 218, 34, 14)],
                [Color.FromArgb(255, 56, 15, 8), Color.FromArgb(255, 23, 6, 4), Color.FromArgb(255, 65, 12, 5)], 0.72),
            _ => new ThemePalette(
                Color.FromArgb(255, 3, 6, 11), Color.FromArgb(160, 89, 108, 132),
                [Color.FromArgb(255, 49, 200, 230), Color.FromArgb(255, 76, 125, 217), Color.FromArgb(255, 142, 111, 209)],
                [Color.FromArgb(255, 14, 22, 34), Color.FromArgb(255, 7, 12, 20), Color.FromArgb(255, 15, 12, 24)], 0.10)
        };

        _glass.ApplyTheme(mode);
        PanelFrame.Background = new SolidColorBrush(palette.Frame);
        PanelFrame.BorderBrush = new SolidColorBrush(palette.Border);
        ThemeSheen.Opacity = palette.SheenOpacity;
        ApplyGradient("AccentGradient", palette.AccentColors);
        ApplyGradient("CardGradient", palette.CardColors);
        if (ThemeSheen.Background is LinearGradientBrush sheenBrush)
            ApplyGradient(sheenBrush, mode == PanelVisualMode.Lava
                ? [Color.FromArgb(72, 255, 197, 64), Color.FromArgb(32, 255, 104, 28),
                   Color.FromArgb(0, 0, 0, 0), Color.FromArgb(32, 255, 50, 18), Color.FromArgb(62, 180, 20, 8)]
                : [Color.FromArgb(31, 221, 246, 255), Color.FromArgb(8, 234, 248, 255),
                   Color.FromArgb(0, 0, 0, 0), Color.FromArgb(8, 30, 124, 255), Color.FromArgb(32, 183, 92, 255)]);
    }

    private void ApplyGradient(string resourceName, IReadOnlyList<Color> colors)
    {
        if (Root.Resources[resourceName] is not LinearGradientBrush brush) return;
        ApplyGradient(brush, colors);
    }

    private static void ApplyGradient(LinearGradientBrush brush, IReadOnlyList<Color> colors)
    {
        for (var index = 0; index < Math.Min(brush.GradientStops.Count, colors.Count); index++)
            brush.GradientStops[index].Color = colors[index];
    }

    private static PanelVisualMode InferVisualMode(bool lavaEnabled, bool cracksEnabled) =>
        lavaEnabled ? PanelVisualMode.Lava : cracksEnabled ? PanelVisualMode.Freeze : PanelVisualMode.Regular;

    private sealed record ThemePalette(Color Frame, Color Border, IReadOnlyList<Color> AccentColors,
        IReadOnlyList<Color> CardColors, double SheenOpacity);

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter) presenter.Minimize();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args) => _closing = true;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (!_closing) return;
        _refreshTimer.Stop();
        _edgeSnapTimer.Stop();
        _lifetime.Cancel();
        CaptureWindowSettings();
        try { _settingsStore.Save(_settings); } catch (IOException) { }
        _overlay?.Dispose();
        _glass.Dispose();
        _lava.Dispose();
        _cpuSparkline.Dispose();
        _sparkline.Dispose();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        _lifetime.Dispose();
    }

    private void CaptureWindowSettings()
    {
        if (_appWindow is not null)
        {
            var presenter = _appWindow.Presenter as OverlappedPresenter;
            if (presenter is not { State: OverlappedPresenterState.Minimized })
            {
                _settings.Width = PhysicalToLogical(_appWindow.Size.Width);
                _settings.Height = PhysicalToLogical(_appWindow.Size.Height);
                _settings.Left = PhysicalToLogical(_appWindow.Position.X);
                _settings.Top = PhysicalToLogical(_appWindow.Position.Y);
            }
            if (presenter is not null)
                _settings.Topmost = presenter.IsAlwaysOnTop;
        }
    }
}
