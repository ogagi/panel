using AiCoreMonitor.Infrastructure;
using AiCoreMonitor.Services;
using AiCoreMonitor.ViewModels;
using AiCoreMonitor.WinUI.Interop;
using AiCoreMonitor.WinUI.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using VoiceEngine.Client;

namespace AiCoreMonitor.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new(new TelemetryService());
    private readonly ConversationViewModel _conversationViewModel = new();
    private readonly VoiceServerService _voiceServer = new();
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
    private FrameworkElement? _draggedSection;
    private int _dropIndex;
    private VoiceConversationController? _conversationController;
    private bool _conversationWasCompact;
    private bool _suppressConversationSelection;

    public MainWindow()
    {
        InitializeComponent();
        _settings = _settingsStore.Load();
        // The compact widget has one fixed top-bar layout. Migrate older side-bar settings.
        _settings.CompactSideBar = false;
        ApplySectionOrder();
        Root.DataContext = _viewModel;
        ConversationPanel.DataContext = _conversationViewModel;
        VoiceEndpointBox.Text = _settings.VoiceServerBaseUri;
        VoiceDirectoryBox.Text = _settings.VoiceServerWorkingDirectory;
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
        ApplyDisplayMode(resizeWindow: false);
        _appWindow.Resize(GetDisplaySize());
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
        SetTitleBar(_settings.CompactMode ? CompactTopBar : DragRegion);
        UpdateCompactDensity(_appWindow.Size);
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
            Variation = (float)_settings.EffectVariation,
            Dripping = _settings.LavaDripping
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
            var minimumWidth = LogicalToPhysical(_settings.CompactMode ? 400 : 340);
            var minimumHeight = LogicalToPhysical(_settings.CompactMode ? 56 : 440);
            var maximumWidth = LogicalToPhysical(_settings.CompactMode ? 1000 : 920);
            var maximumHeight = LogicalToPhysical(_settings.CompactMode ? 160 : 1120);
            var width = Math.Clamp(sender.Size.Width, minimumWidth, maximumWidth);
            var height = Math.Clamp(sender.Size.Height, minimumHeight, maximumHeight);
            if (width != sender.Size.Width || height != sender.Size.Height)
            {
                _correctingSize = true;
                sender.Resize(new SizeInt32(width, height));
                _correctingSize = false;
            }
        }

        if (args.DidSizeChange)
            UpdateCompactDensity(sender.Size);

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
        _overlay.IsEnabled = _settings.LavaEnabled == true;
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

    private FrameworkElement[] Sections => [CodexSection, SystemSection, ModelsSection];

    private void ApplySectionOrder()
    {
        var sectionById = Sections.ToDictionary(section => (string)section.Tag, StringComparer.Ordinal);
        var order = (_settings.SectionOrder ?? []).Where(sectionById.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
        order.AddRange(sectionById.Keys.Where(id => !order.Contains(id, StringComparer.Ordinal)));
        _settings.SectionOrder = [.. order];
        for (var row = 0; row < order.Count; row++)
            Grid.SetRow(sectionById[order[row]], row);
    }

    private void Section_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        _draggedSection = (FrameworkElement)sender;
        _dropIndex = Grid.GetRow(_draggedSection);
        args.Data.SetText((string)_draggedSection.Tag);
        args.Data.RequestedOperation = DataPackageOperation.Move;
        AnimateDragSource(_draggedSection, dragging: true);
    }

    private void Dashboard_DragOver(object sender, DragEventArgs args)
    {
        if (_draggedSection is null) return;
        args.AcceptedOperation = DataPackageOperation.Move;
        _dropIndex = GetDropIndex(args.GetPosition(Dashboard).Y);
        ShowDropIndicator(_dropIndex);
    }

    private void Dashboard_DragLeave(object sender, DragEventArgs args) => HideDropIndicator();

    private void Dashboard_Drop(object sender, DragEventArgs args)
    {
        if (_draggedSection is null) return;
        args.AcceptedOperation = DataPackageOperation.Move;

        var oldPositions = Sections.ToDictionary(section => section, section => section.TransformToVisual(Dashboard).TransformPoint(default).Y);
        var ordered = Sections.OrderBy(Grid.GetRow).ToList();
        var oldIndex = ordered.IndexOf(_draggedSection);
        ordered.RemoveAt(oldIndex);
        var insertionIndex = _dropIndex > oldIndex ? _dropIndex - 1 : _dropIndex;
        ordered.Insert(Math.Clamp(insertionIndex, 0, ordered.Count), _draggedSection);
        for (var row = 0; row < ordered.Count; row++)
            Grid.SetRow(ordered[row], row);
        _settings.SectionOrder = [.. ordered.Select(section => (string)section.Tag)];
        Dashboard.UpdateLayout();
        AnimateReorder(oldPositions);
        ScheduleSettingsSave();
        HideDropIndicator();
    }

    private void Section_DropCompleted(UIElement sender, DropCompletedEventArgs args)
    {
        AnimateDragSource((FrameworkElement)sender, dragging: false);
        _draggedSection = null;
        HideDropIndicator();
    }

    private int GetDropIndex(double pointerY)
    {
        var index = 0;
        foreach (var section in Sections.OrderBy(Grid.GetRow))
        {
            var top = section.TransformToVisual(Dashboard).TransformPoint(default).Y;
            if (pointerY >= top + section.ActualHeight / 2) index++;
        }
        return index;
    }

    private void ShowDropIndicator(int index)
    {
        var ordered = Sections.OrderBy(Grid.GetRow).ToArray();
        var atEnd = index >= ordered.Length;
        Grid.SetRow(DragInsertionIndicator, Grid.GetRow(atEnd ? ordered[^1] : ordered[Math.Max(index, 0)]));
        DragInsertionIndicator.VerticalAlignment = atEnd ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        DragInsertionIndicator.Opacity = 1;
    }

    private void HideDropIndicator() => DragInsertionIndicator.Opacity = 0;

    private static void AnimateDragSource(FrameworkElement section, bool dragging)
    {
        var visual = ElementCompositionPreview.GetElementVisual(section);
        visual.CenterPoint = new Vector3((float)(section.ActualWidth / 2), (float)(section.ActualHeight / 2), 0);
        var compositor = visual.Compositor;
        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, dragging ? new Vector3(0.975f, 0.975f, 1) : Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Scale", scale);
        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(1, dragging ? 0.68f : 1);
        opacity.Duration = TimeSpan.FromMilliseconds(140);
        visual.StartAnimation("Opacity", opacity);
    }

    private void AnimateReorder(IReadOnlyDictionary<FrameworkElement, double> oldPositions)
    {
        foreach (var section in Sections)
        {
            var newTop = section.TransformToVisual(Dashboard).TransformPoint(default).Y;
            var delta = oldPositions[section] - newTop;
            if (Math.Abs(delta) < 0.5) continue;
            var transform = new TranslateTransform { Y = delta };
            section.RenderTransform = transform;
            var animation = new DoubleAnimation
            {
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                EnableDependentAnimation = true
            };
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, "Y");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
    }

    private void SettingsFlyout_Opened(object sender, object e) => UpdateEffectControls();

    private void CompactSettingsButton_Click(object sender, RoutedEventArgs e)
        => SettingsButton.Flyout?.ShowAt(CompactSettingsButton);

    private void LavaButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.LavaEnabled = _settings.LavaEnabled != true;
        ApplyEffectSettings();
        UpdateEffectControls();
        ScheduleSettingsSave();
    }

    private void LavaDripping_Changed(object sender, RoutedEventArgs e)
    {
        _settings.LavaDripping = LavaDrippingCheckBox.IsChecked == true;
        if (_overlay is not null) _overlay.Dripping = _settings.LavaDripping;
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
            _overlay.Dripping = _settings.LavaDripping;
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
        LavaDrippingCheckBox.IsChecked = _settings.LavaDripping;
        CracksButton.Content = _settings.CracksEnabled == true ? "ON" : "OFF";
        LavaAmountText.Text = $"{_settings.LavaAmount!.Value * 100:N0}";
        CrackAmountText.Text = $"{_settings.CrackAmount!.Value * 100:N0}";
        SettingsButton.Opacity = (_settings.LavaEnabled == true || _settings.CracksEnabled == true) ? 1 : 0.65;
        ApplyGpuVisualsSetting();
    }

    private void CompactButton_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowGeometry();
        _settings.CompactMode = true;
        ApplyDisplayMode();
        SaveWindowGeometry();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        SynchronizeOverlay();
        WindowEffects.Hide(_windowHandle);
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e)
    {
        SaveWindowGeometry();
        _settings.CompactMode = false;
        ApplyDisplayMode();
        if (_conversationController is not null)
            ShowConversationLayout();
        SaveWindowGeometry();
    }

    private SizeInt32 GetDisplaySize() => _settings.CompactMode
        ? new SizeInt32(
            LogicalToPhysical(Math.Clamp(_settings.CompactTopWidth, 400, 1000)),
            LogicalToPhysical(Math.Clamp(_settings.CompactTopHeight, 56, 160)))
        : new SizeInt32(LogicalToPhysical(Math.Clamp(_settings.Width, 340, 920)), LogicalToPhysical(Math.Clamp(_settings.Height, 440, 1120)));

    private void ApplyDisplayMode(bool resizeWindow = true)
    {
        FullShell.Visibility = _settings.CompactMode ? Visibility.Collapsed : Visibility.Visible;
        CompactShell.Visibility = _settings.CompactMode ? Visibility.Visible : Visibility.Collapsed;
        if (_appWindow is not null && ExtendsContentIntoTitleBar)
            SetTitleBar(_settings.CompactMode ? CompactTopBar : DragRegion);
        if (resizeWindow && _appWindow is not null)
            _appWindow.Resize(GetDisplaySize());
        if (_appWindow is not null)
            UpdateCompactDensity(_appWindow.Size);
        SynchronizeOverlay();
    }

    private void UpdateCompactDensity(SizeInt32 physicalSize)
    {
        if (!_settings.CompactMode) return;

        var width = PhysicalToLogical(physicalSize.Width);
        var height = PhysicalToLogical(physicalSize.Height);

        var showTopDetails = width >= 620 && height >= 78;
        var showTopExtended = width >= 780 && height >= 104;
        CompactTopBar.ColumnSpacing = width < 540 ? 4 : 12;
        CompactBrandLabel.Visibility = width >= 560 ? Visibility.Visible : Visibility.Collapsed;
        TopCodexPlan.Visibility = showTopDetails ? Visibility.Visible : Visibility.Collapsed;
        TopGpuMemory.Visibility = Visibility.Visible;
        TopCpuDetails.Visibility = showTopDetails ? Visibility.Visible : Visibility.Collapsed;
        TopModelState.Visibility = showTopDetails ? Visibility.Visible : Visibility.Collapsed;
        TopCodexReset.Visibility = showTopExtended ? Visibility.Visible : Visibility.Collapsed;
        TopGpuThermals.Visibility = showTopExtended ? Visibility.Visible : Visibility.Collapsed;
        TopActiveModel.Visibility = showTopExtended ? Visibility.Visible : Visibility.Collapsed;
        TopRefresh.Visibility = showTopExtended ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RestoreFromTray()
    {
        if (_closing) return;
        WindowEffects.Show(_windowHandle);
        Activate();
        SynchronizeOverlay();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void ConversationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversationController is not null)
            await EndConversationAsync();
        else
            await StartConversationAsync();
    }
    private async void RetryVoiceButton_Click(object sender, RoutedEventArgs e) => await StartConversationAsync();

    private async Task StartConversationAsync()
    {
        if (_conversationController is not null) return;
        if (!VoiceServerService.TryGetLoopbackBaseUri(VoiceEndpointBox.Text, out var baseUri))
        {
            _conversationViewModel.Failed("Use an HTTP loopback endpoint.");
            if (!_settings.CompactMode) ShowConversationLayout();
            return;
        }

        if (_settings.CompactMode)
        {
            _conversationWasCompact = true;
        }
        else ShowConversationLayout();
        _conversationViewModel.Starting();
        UpdateConversationButtons(isRunning: true);
        var controller = new VoiceConversationController();
        controller.EventReceived += ConversationEventReceived;
        _conversationController = controller;
        try
        {
            // A compact-mode press is self-contained: use an already running server, or
            // start the configured local server before requesting microphone capture.
            if (!await _voiceServer.IsReadyAsync(baseUri!, _lifetime.Token) && CanStartVoiceServer())
                await _voiceServer.StartAndWaitAsync(_settings.VoiceServerWorkingDirectory, baseUri!, TimeSpan.FromSeconds(45), _lifetime.Token);
            await controller.StartAsync(baseUri!, _settings.VoiceProfile, _lifetime.Token);
        }
        catch (Exception exception) when (exception is VoiceClientException or InvalidOperationException or UnauthorizedAccessException or IOException or TimeoutException)
        {
            controller.EventReceived -= ConversationEventReceived;
            await controller.DisposeAsync();
            _conversationController = null;
            _conversationViewModel.Failed(exception.Message);
            UpdateConversationButtons(isRunning: false);
        }
    }

    private bool CanStartVoiceServer() =>
        !string.IsNullOrWhiteSpace(_settings.VoiceServerWorkingDirectory) &&
        Directory.Exists(_settings.VoiceServerWorkingDirectory) &&
        File.Exists(Path.Combine(_settings.VoiceServerWorkingDirectory, "config.toml"));

    private void ShowConversationLayout()
    {
        if (ConversationPanel.Visibility == Visibility.Visible) return;
        if (_conversationController is null)
        {
            _conversationWasCompact = _settings.CompactMode;
        }
        _settings.CompactMode = false;
        ApplyDisplayMode();
        Dashboard.Visibility = Visibility.Collapsed;
        ConversationPanel.Visibility = Visibility.Visible;
        ConversationButton.Content = "\uE720";
    }

    private async void EndConversationButton_Click(object sender, RoutedEventArgs e) => await EndConversationAsync();

    private async Task EndConversationAsync()
    {
        var controller = _conversationController;
        _conversationController = null;
        if (controller is not null)
        {
            controller.EventReceived -= ConversationEventReceived;
            await controller.DisposeAsync();
        }
        _conversationViewModel.Stopped();
        ConversationPanel.Visibility = Visibility.Collapsed;
        Dashboard.Visibility = Visibility.Visible;
        _settings.CompactMode = _conversationWasCompact;
        ApplyDisplayMode();
        UpdateConversationButtons(isRunning: false);
        ScheduleSettingsSave();
    }

    private void UpdateConversationButtons(bool isRunning)
    {
        var background = new SolidColorBrush(isRunning ? Windows.UI.Color.FromArgb(0xD8, 0x9D, 0x1A, 0x2C) : Windows.UI.Color.FromArgb(0xB6, 0x18, 0x20, 0x2A));
        var border = new SolidColorBrush(isRunning ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x62, 0x7C) : Windows.UI.Color.FromArgb(0x40, 0x5B, 0x6A, 0x78));
        foreach (var button in new[] { ConversationButton, CompactConversationButton })
        {
            button.Background = background;
            button.BorderBrush = border;
            button.Content = "\uE720";
            ToolTipService.SetToolTip(button, isRunning ? "End voice conversation" : "Start voice conversation");
        }
    }

    private void ConversationEventReceived(ConversationEvent item)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _suppressConversationSelection = true;
            _conversationViewModel.Apply(item);
            _suppressConversationSelection = false;
            if (_conversationViewModel.Transcript.Count > 0)
                TranscriptList.ScrollIntoView(_conversationViewModel.Transcript[^1]);
        });
    }

    private async void StopResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversationController is null) return;
        try { await _conversationController.CancelResponseAsync(_lifetime.Token); }
        catch (VoiceClientException exception) { _conversationViewModel.Failed(exception.Message); }
    }

    private void MuteVoiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_conversationController is null) return;
        _conversationViewModel.IsMuted = !_conversationViewModel.IsMuted;
        _conversationController.SetMuted(_conversationViewModel.IsMuted);
        MuteVoiceButton.Content = _conversationViewModel.IsMuted ? "UNMUTE" : "MUTE";
    }

    private async void ModelSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConversationSelection || _conversationController is null || e.AddedItems.FirstOrDefault() is not string model) return;
        try { await _conversationController.SelectModelAsync(model, _lifetime.Token); }
        catch (VoiceClientException exception) { _conversationViewModel.Failed(exception.Message); }
    }

    private async void ContextSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConversationSelection || _conversationController is null || e.AddedItems.FirstOrDefault() is not string context) return;
        try { await _conversationController.SelectContextAsync(context, _lifetime.Token); }
        catch (Exception exception) when (exception is VoiceClientException or FormatException) { _conversationViewModel.Failed(exception.Message); }
    }

    private async void VoiceSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConversationSelection || _conversationController is null || e.AddedItems.FirstOrDefault() is not VoiceChoice voice) return;
        try { await _conversationController.SelectVoiceAsync(voice.Id, _lifetime.Token); }
        catch (VoiceClientException exception) { _conversationViewModel.Failed(exception.Message); }
    }

    private async void ProfileSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressConversationSelection || _conversationController is null || e.AddedItems.FirstOrDefault() is not VoiceChoice profile) return;
        _settings.VoiceProfile = profile.Id;
        ScheduleSettingsSave();
        try { await _conversationController.SelectProfileAsync(profile.Id, _lifetime.Token); }
        catch (VoiceClientException exception) { _conversationViewModel.Failed(exception.Message); }
    }

    private void VoiceSettings_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.VoiceServerBaseUri = VoiceEndpointBox.Text.Trim();
        _settings.VoiceServerWorkingDirectory = VoiceDirectoryBox.Text.Trim();
        ScheduleSettingsSave();
    }

    private async void BrowseVoiceDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _windowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        VoiceDirectoryBox.Text = folder.Path;
        VoiceSettings_LostFocus(VoiceDirectoryBox, e);
    }

    private async void StartVoiceServerButton_Click(object sender, RoutedEventArgs e)
    {
        if (!VoiceServerService.TryGetLoopbackBaseUri(VoiceEndpointBox.Text, out var baseUri))
        {
            _conversationViewModel.Failed("Use an HTTP loopback endpoint.");
            return;
        }
        VoiceSettings_LostFocus(VoiceDirectoryBox, e);
        _conversationViewModel.Failed("STARTING VOICE SERVER");
        try
        {
            await _voiceServer.StartAndWaitAsync(_settings.VoiceServerWorkingDirectory, baseUri!, TimeSpan.FromSeconds(45), _lifetime.Token);
            _conversationViewModel.Stopped();
            await StartConversationAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException or UnauthorizedAccessException)
        {
            _conversationViewModel.Failed(exception.Message);
        }
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args) => _closing = true;

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (!_closing) return;
        _refreshTimer.Stop();
        _settingsSaveTimer.Stop();
        _lifetime.Cancel();
        if (_conversationController is not null)
            _conversationController.DisposeAsync().AsTask().GetAwaiter().GetResult();
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
            if (!_settings.CompactMode)
            {
                _settings.Width = PhysicalToLogical(_appWindow.Size.Width);
                _settings.Height = PhysicalToLogical(_appWindow.Size.Height);
            }
            else
            {
                _settings.CompactTopWidth = PhysicalToLogical(_appWindow.Size.Width);
                _settings.CompactTopHeight = PhysicalToLogical(_appWindow.Size.Height);
            }
            _settings.Left = PhysicalToLogical(_appWindow.Position.X);
            _settings.Top = PhysicalToLogical(_appWindow.Position.Y);
            if (_appWindow.Presenter is OverlappedPresenter presenter)
                _settings.Topmost = presenter.IsAlwaysOnTop;
        }

        try { _settingsStore.Save(_settings); } catch (IOException) { }
    }
}
