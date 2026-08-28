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
    private readonly DispatcherTimer _settingsSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly LavaCompositionController _lava;
    private readonly SparklineCompositionController _sparkline;
    private readonly GlassBackdropController _glass;
    private LavaOverlayWindow? _overlay;
    private AppIcon? _appIcon;
    private TrayIconController? _trayIcon;
    private AppWindow? _appWindow;
    private nint _windowHandle;
    private double _rasterizationScale = 1;
    private int _tick;
    private bool _closing;
    private bool _correctingSize;
    private bool _hiddenToTray;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        Root.DataContext = _viewModel;
        _glass = new GlassBackdropController(this);
        var lavaEnabled = _settings.LavaEnabled ?? _settings.AnimationEnabled;
        var cracksEnabled = _settings.CracksEnabled ?? _settings.AnimationEnabled;
        _settings.LavaEnabled = lavaEnabled;
        _settings.CracksEnabled = cracksEnabled;
        _settings.LavaAmount = Math.Clamp(_settings.LavaAmount ?? _settings.EffectIntensity, 0.1, 1);
        _settings.CrackAmount = Math.Clamp(_settings.CrackAmount ?? _settings.EffectIntensity, 0.1, 1);
        _settings.LavaHue ??= 275;
        _settings.CrackHue ??= Math.Clamp(_settings.EffectHue, 0, 360);
        _lava = new LavaCompositionController(LavaHost)
        {
            IsEnabled = lavaEnabled || cracksEnabled,
            LavaEnabled = lavaEnabled,
            CracksEnabled = cracksEnabled,
            LavaAmount = (float)_settings.LavaAmount.Value,
            CrackAmount = (float)_settings.CrackAmount.Value,
            CrackHue = (float)_settings.CrackHue.Value,
            Variation = (float)Math.Clamp(_settings.EffectVariation, 0.25, 2)
        };
        _sparkline = new SparklineCompositionController(GpuSparklineHost);
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        UpdateEffectControls();
        _refreshTimer.Tick += RefreshTimer_Tick;
        _settingsSaveTimer.Tick += SettingsSaveTimer_Tick;
        Root.Loaded += Root_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs args)
    {
        if (_appWindow is not null) return;

        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_windowHandle);
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
            presenter.IsMinimizable = false;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        HeaderActions.Margin = new Thickness(0, 0, 2, 0);
        WindowEffects.Apply(_windowHandle);
        _appIcon = new AppIcon();
        WindowEffects.SetIcon(_windowHandle, _appIcon.Handle);
        WindowEffects.DisableMaximize(_windowHandle);
        _trayIcon = new TrayIconController(RestoreFromTray, Close, _appIcon.Handle);
        _appWindow.Closing += AppWindow_Closing;
        _appWindow.Changed += AppWindow_Changed;
        _overlay = new LavaOverlayWindow
        {
            IsEnabled = _settings.LavaEnabled == true,
            Amount = (float)_settings.LavaAmount.GetValueOrDefault(0.78),
            LavaHue = (float)_settings.LavaHue.GetValueOrDefault(275),
            Variation = (float)_settings.EffectVariation
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

        if (args.DidPositionChange || args.DidSizeChange)
            ScheduleSettingsSave();

        SynchronizeOverlay();
    }

    private void ScheduleSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SettingsSaveTimer_Tick(object? sender, object e)
    {
        _settingsSaveTimer.Stop();
        SaveWindowGeometry();
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
        _overlay.IsEnabled = _settings.LavaEnabled == true && !_hiddenToTray && !minimized;
        _overlay.UpdateBounds(_windowHandle, _appWindow.Position.X, overlayTop, _appWindow.Size.Width, overlayHeight,
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

    private void SettingsFlyout_Opened(object sender, object e) => UpdateEffectControls();

    private void LavaButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.LavaEnabled = _settings.LavaEnabled != true;
        ApplyEffectSettings();
        UpdateEffectControls();
        ScheduleSettingsSave();
    }

    private void CracksButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.CracksEnabled = _settings.CracksEnabled != true;
        ApplyEffectSettings();
        UpdateEffectControls();
        ScheduleSettingsSave();
    }

    private void TopmostButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.Topmost = !_settings.Topmost;
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _settings.Topmost;
        SynchronizeOverlay();
        UpdateEffectControls();
        ScheduleSettingsSave();
    }

    private void GpuVisualsButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.GpuVisualsEnabled = !_settings.GpuVisualsEnabled;
        ApplyGpuVisualsSetting();
        UpdateEffectControls();
        ScheduleSettingsSave();
    }

    private void LavaAmountDown_Click(object sender, RoutedEventArgs e) => ChangeLavaAmount(-0.1);
    private void LavaAmountUp_Click(object sender, RoutedEventArgs e) => ChangeLavaAmount(0.1);
    private void CrackAmountDown_Click(object sender, RoutedEventArgs e) => ChangeCrackAmount(-0.1);
    private void CrackAmountUp_Click(object sender, RoutedEventArgs e) => ChangeCrackAmount(0.1);
    private void LavaHueDown_Click(object sender, RoutedEventArgs e) => ChangeLavaHue(-15);
    private void LavaHueUp_Click(object sender, RoutedEventArgs e) => ChangeLavaHue(15);
    private void CrackHueDown_Click(object sender, RoutedEventArgs e) => ChangeCrackHue(-15);
    private void CrackHueUp_Click(object sender, RoutedEventArgs e) => ChangeCrackHue(15);
    private void VariationDown_Click(object sender, RoutedEventArgs e) => ChangeVariation(-0.25);
    private void VariationUp_Click(object sender, RoutedEventArgs e) => ChangeVariation(0.25);

    private void ChangeLavaAmount(double delta) { _settings.LavaAmount = Math.Clamp(_settings.LavaAmount!.Value + delta, 0.1, 1); ApplyEffectSettings(); UpdateEffectControls(); ScheduleSettingsSave(); }
    private void ChangeCrackAmount(double delta) { _settings.CrackAmount = Math.Clamp(_settings.CrackAmount!.Value + delta, 0.1, 1); ApplyEffectSettings(); UpdateEffectControls(); ScheduleSettingsSave(); }
    private void ChangeLavaHue(double delta) { _settings.LavaHue = (_settings.LavaHue!.Value + delta + 360) % 360; if (_overlay is not null) _overlay.LavaHue = (float)_settings.LavaHue.Value; ScheduleSettingsSave(); }
    private void ChangeCrackHue(double delta) { _settings.CrackHue = (_settings.CrackHue!.Value + delta + 360) % 360; _lava.CrackHue = (float)_settings.CrackHue.Value; ScheduleSettingsSave(); }
    private void ChangeVariation(double delta) { _settings.EffectVariation = Math.Clamp(_settings.EffectVariation + delta, 0.25, 2); _lava.Variation = (float)_settings.EffectVariation; if (_overlay is not null) _overlay.Variation = (float)_settings.EffectVariation; ScheduleSettingsSave(); }

    private void ApplyEffectSettings()
    {
        var lavaEnabled = _settings.LavaEnabled == true;
        var cracksEnabled = _settings.CracksEnabled == true;
        _settings.AnimationEnabled = lavaEnabled || cracksEnabled;
        _lava.IsEnabled = _settings.AnimationEnabled && _settings.GpuVisualsEnabled;
        _lava.LavaEnabled = lavaEnabled;
        _lava.CracksEnabled = cracksEnabled;
        _lava.LavaAmount = (float)_settings.LavaAmount!.Value;
        _lava.CrackAmount = (float)_settings.CrackAmount!.Value;
        if (_overlay is not null)
        {
            _overlay.Amount = (float)_settings.LavaAmount.Value;
            _overlay.LavaHue = (float)_settings.LavaHue!.Value;
            _overlay.Variation = (float)_settings.EffectVariation;
            SynchronizeOverlay();
        }
        SettingsButton.Opacity = _settings.AnimationEnabled ? 1 : 0.65;
    }

    private void ApplyGpuVisualsSetting()
    {
        _lava.IsEnabled = _settings.AnimationEnabled && _settings.GpuVisualsEnabled;
        _sparkline.IsEnabled = _settings.GpuVisualsEnabled;
    }

    private void UpdateEffectControls()
    {
        TopmostButton.Content = _settings.Topmost ? "ON" : "OFF";
        GpuVisualsButton.Content = _settings.GpuVisualsEnabled ? "ON" : "OFF";
        LavaButton.Content = _settings.LavaEnabled == true ? "ON" : "OFF";
        CracksButton.Content = _settings.CracksEnabled == true ? "ON" : "OFF";
        LavaAmountText.Text = $"{_settings.LavaAmount!.Value * 100:N0}";
        CrackAmountText.Text = $"{_settings.CrackAmount!.Value * 100:N0}";
        SettingsButton.Opacity = (_settings.LavaEnabled == true || _settings.CracksEnabled == true) ? 1 : 0.65;
        ApplyGpuVisualsSetting();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        _hiddenToTray = true;
        SynchronizeOverlay();
        WindowEffects.Hide(_windowHandle);
    }

    private void RestoreFromTray()
    {
        if (_closing) return;
        _hiddenToTray = false;
        WindowEffects.Show(_windowHandle);
        Activate();
        SynchronizeOverlay();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args) => _closing = true;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (!_closing) return;
        _refreshTimer.Stop();
        _settingsSaveTimer.Stop();
        _lifetime.Cancel();
        SaveWindowGeometry();
        _overlay?.Dispose();
        _trayIcon?.Dispose();
        _appIcon?.Dispose();
        _glass.Dispose();
        _lava.Dispose();
        _sparkline.Dispose();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel.Dispose();
        _lifetime.Dispose();
    }

    private void SaveWindowGeometry()
    {
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
    }
}
