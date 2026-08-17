using AiCoreMonitor.Infrastructure;
using AiCoreMonitor.Services;
using AiCoreMonitor.ViewModels;
using AiCoreMonitor.WinUI.Interop;
using AiCoreMonitor.WinUI.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AiCoreMonitor.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new(new TelemetryService());
    private readonly WidgetSettingsStore _settingsStore = new();
    private readonly WidgetSettings _settings;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly LavaCompositionController _lava;
    private readonly SparklineCompositionController _sparkline;
    private readonly GlassBackdropController _glass;
    private LavaOverlayWindow? _overlay;
    private AppWindow? _appWindow;
    private double _rasterizationScale = 1;
    private int _tick;
    private bool _closing;
    private bool _correctingSize;
    private bool _updatingEffectControls;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        if (_settings.Width == 460 && _settings.Height == 560)
        {
            _settings.Width = 340;
            _settings.Height = 440;
        }
        Root.DataContext = _viewModel;
        _glass = new GlassBackdropController(this);
        var lavaEnabled = _settings.LavaEnabled ?? _settings.AnimationEnabled;
        var cracksEnabled = _settings.CracksEnabled ?? _settings.AnimationEnabled;
        _settings.LavaEnabled = lavaEnabled;
        _settings.CracksEnabled = cracksEnabled;
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
        _sparkline = new SparklineCompositionController(GpuSparklineHost);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateEffectControls();
        LavaToggle.Toggled += LavaToggle_Toggled;
        CracksToggle.Toggled += CracksToggle_Toggled;
        LavaAmountSlider.ValueChanged += LavaAmountSlider_ValueChanged;
        CrackAmountSlider.ValueChanged += CrackAmountSlider_ValueChanged;
        _refreshTimer.Tick += RefreshTimer_Tick;
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
            LogicalToPhysical(Math.Clamp(_settings.Width, 340, 920)),
            LogicalToPhysical(Math.Clamp(_settings.Height, 440, 1120))));
        if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top))
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
            IsEnabled = _settings.LavaEnabled == true,
            Amount = (float)_settings.LavaAmount.GetValueOrDefault(0.78)
        };
        SynchronizeOverlay();

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
        var x = Math.Clamp(window.Position.X, work.X - window.Size.Width + 80, work.X + work.Width - 80);
        var y = Math.Clamp(window.Position.Y, work.Y, work.Y + work.Height - 48);
        if (x != window.Position.X || y != window.Position.Y) window.Move(new PointInt32(x, y));
    }

    private int LogicalToPhysical(double value) => (int)Math.Round(value * _rasterizationScale);
    private double PhysicalToLogical(int value) => value / _rasterizationScale;

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPositionChange)
            EnableEffects();

        if (!_correctingSize)
        {
            var minimumWidth = LogicalToPhysical(340);
            var minimumHeight = LogicalToPhysical(440);
            var maximumWidth = LogicalToPhysical(920);
            var maximumHeight = LogicalToPhysical(1120);
            var width = Math.Clamp(sender.Size.Width, minimumWidth, maximumWidth);
            var height = Math.Clamp(sender.Size.Height, minimumHeight, maximumHeight);
            if (width != sender.Size.Width || height != sender.Size.Height)
            {
                _correctingSize = true;
                sender.Resize(new SizeInt32(width, height));
                _correctingSize = false;
            }
        }
        SynchronizeOverlay();
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
        _overlay.IsEnabled = _settings.LavaEnabled == true && !minimized;
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
        if (args.PropertyName == nameof(MainViewModel.GpuSamples))
            _sparkline.SetValues(_viewModel.GpuSamples);
    }

    private void EffectsFlyout_Opened(object sender, object e) => UpdateEffectControls();

    private void LavaToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingEffectControls) return;
        _settings.LavaEnabled = LavaToggle.IsOn;
        ApplyEffectSettings();
    }

    private void CracksToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_updatingEffectControls) return;
        _settings.CracksEnabled = CracksToggle.IsOn;
        ApplyEffectSettings();
    }

    private void LavaAmountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingEffectControls) return;
        _settings.LavaAmount = e.NewValue / 100;
        ApplyEffectSettings();
    }

    private void CrackAmountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingEffectControls) return;
        _settings.CrackAmount = e.NewValue / 100;
        ApplyEffectSettings();
    }

    private void EnableEffects()
    {
        if (_settings.LavaEnabled == true && _settings.CracksEnabled == true) return;
        _settings.LavaEnabled = true;
        _settings.CracksEnabled = true;
        ApplyEffectSettings();
        UpdateEffectControls();
    }

    private void ApplyEffectSettings()
    {
        var lavaEnabled = _settings.LavaEnabled == true;
        var cracksEnabled = _settings.CracksEnabled == true;
        _settings.AnimationEnabled = lavaEnabled || cracksEnabled;
        _lava.IsEnabled = _settings.AnimationEnabled;
        _lava.LavaEnabled = lavaEnabled;
        _lava.CracksEnabled = cracksEnabled;
        _lava.LavaAmount = (float)_settings.LavaAmount!.Value;
        _lava.CrackAmount = (float)_settings.CrackAmount!.Value;
        if (_overlay is not null)
        {
            _overlay.Amount = (float)_settings.LavaAmount.Value;
            _overlay.IsEnabled = lavaEnabled;
        }
        AnimationButton.Opacity = _settings.AnimationEnabled ? 1 : 0.45;
    }

    private void UpdateEffectControls()
    {
        _updatingEffectControls = true;
        LavaToggle.IsOn = _settings.LavaEnabled == true;
        CracksToggle.IsOn = _settings.CracksEnabled == true;
        LavaAmountSlider.Value = _settings.LavaAmount!.Value * 100;
        CrackAmountSlider.Value = _settings.CrackAmount!.Value * 100;
        AnimationButton.Opacity = (_settings.LavaEnabled == true || _settings.CracksEnabled == true) ? 1 : 0.45;
        _updatingEffectControls = false;
    }

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
        _lifetime.Cancel();
        if (_appWindow is not null)
        {
            _settings.Width = PhysicalToLogical(_appWindow.Size.Width);
            _settings.Height = PhysicalToLogical(_appWindow.Size.Height);
            _settings.Left = PhysicalToLogical(_appWindow.Position.X);
            _settings.Top = PhysicalToLogical(_appWindow.Position.Y);
            if (_appWindow.Presenter is OverlappedPresenter presenter)
                _settings.Topmost = presenter.IsAlwaysOnTop;
        }
        try { _settingsStore.Save(_settings); } catch (IOException) { }
        _overlay?.Dispose();
        _glass.Dispose();
        _lava.Dispose();
        _sparkline.Dispose();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        _lifetime.Dispose();
    }
}
