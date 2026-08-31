using AiCoreMonitor.Infrastructure;
using AiCoreMonitor.WinUI.Interop;
using AiCoreMonitor.WinUI.Presentation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace AiCoreMonitor.WinUI;

public sealed partial class SettingsWindow : Window
{
    private readonly WidgetSettings _settings;
    private readonly Action _settingsChanged;
    private readonly nint _pickerOwnerHandle;
    private readonly GlassBackdropController _glass;
    private AppWindow? _appWindow;
    private bool _isLoading = true;

    public SettingsWindow(WidgetSettings settings, Action settingsChanged, nint pickerOwnerHandle)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _pickerOwnerHandle = pickerOwnerHandle;
        InitializeComponent();
        _glass = new GlassBackdropController(this);
        Title = "AI Core Monitor Settings";

        LavaAmountSlider.Minimum = 0.1;
        LavaAmountSlider.Maximum = 1.0;
        LavaAmountSlider.StepFrequency = 0.1;
        CrackAmountSlider.Minimum = 0.1;
        CrackAmountSlider.Maximum = 1.0;
        CrackAmountSlider.StepFrequency = 0.1;
        LavaHueSlider.Maximum = CrackHueSlider.Maximum = 360;
        LavaHueSlider.StepFrequency = CrackHueSlider.StepFrequency = 15;
        LavaVariationSlider.Minimum = CrackVariationSlider.Minimum = 0.25;
        LavaVariationSlider.Maximum = CrackVariationSlider.Maximum = 2.0;
        LavaVariationSlider.StepFrequency = CrackVariationSlider.StepFrequency = 0.25;

        TopmostSwitch.IsOn = _settings.Topmost;
        GpuEnhancementsSwitch.IsOn = _settings.GpuVisualsEnabled;
        VoiceEndpointBox.Text = _settings.VoiceServerBaseUri;
        VoiceDirectoryBox.Text = _settings.VoiceServerWorkingDirectory;
        LavaSwitch.IsOn = _settings.LavaEnabled == true;
        LavaDrippingSwitch.IsOn = _settings.LavaDripping;
        CracksSwitch.IsOn = _settings.CracksEnabled == true;
        LavaAmountSlider.Value = _settings.LavaAmount!.Value;
        CrackAmountSlider.Value = _settings.CrackAmount!.Value;
        LavaHueSlider.Value = _settings.LavaHue!.Value;
        CrackHueSlider.Value = _settings.CrackHue!.Value;
        LavaVariationSlider.Value = _settings.LavaVariation!.Value;
        CrackVariationSlider.Value = _settings.CrackVariation!.Value;
        UpdateLabels();
        _isLoading = false;
        UpdateEffectControlStates();
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_appWindow is not null) return;

        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Title = "AI Core Monitor - Settings";
        _appWindow.Resize(new SizeInt32(780, 760));
        _appWindow.TitleBar.ButtonForegroundColor = Color.FromArgb(255, 255, 255, 255);
        _appWindow.TitleBar.ButtonInactiveForegroundColor = Color.FromArgb(255, 160, 174, 190);
        _appWindow.TitleBar.ButtonHoverForegroundColor = Color.FromArgb(255, 255, 255, 255);
        _appWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(80, 255, 255, 255);
        _appWindow.TitleBar.ButtonPressedForegroundColor = Color.FromArgb(255, 255, 255, 255);
        _appWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(110, 255, 255, 255);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            // A topmost monitor must not cover its active settings window.
            presenter.IsAlwaysOnTop = _settings.Topmost;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        WindowEffects.Apply(handle);
        WindowEffects.DisableMaximize(handle);
    }

    private void TopmostSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.Topmost = TopmostSwitch.IsOn;
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
            presenter.IsAlwaysOnTop = _settings.Topmost;
        Changed();
    }
    private void GpuEnhancementsSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.GpuVisualsEnabled = GpuEnhancementsSwitch.IsOn; UpdateEffectControlStates(); Changed(); }
    private void LavaSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.LavaEnabled = LavaSwitch.IsOn; UpdateEffectControlStates(); Changed(); }
    private void LavaDrippingSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.LavaDripping = LavaDrippingSwitch.IsOn; UpdateEffectControlStates(); Changed(); }
    private void CracksSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.CracksEnabled = CracksSwitch.IsOn; UpdateEffectControlStates(); Changed(); }

    private void LavaAmountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_isLoading) return; _settings.LavaAmount = e.NewValue; UpdateLabels(); Changed(); }
    private void CrackAmountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_isLoading) return; _settings.CrackAmount = e.NewValue; UpdateLabels(); Changed(); }
    private void LavaHueSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_isLoading) return; _settings.LavaHue = e.NewValue; UpdateLabels(); Changed(); }
    private void CrackHueSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_isLoading) return; _settings.CrackHue = e.NewValue; UpdateLabels(); Changed(); }
    private void LavaVariationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_isLoading) return; _settings.LavaVariation = e.NewValue; UpdateLabels(); Changed(); }
    private void CrackVariationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (_isLoading) return; _settings.CrackVariation = e.NewValue; UpdateLabels(); Changed(); }

    private void VoiceSettings_LostFocus(object sender, RoutedEventArgs e)
    {
        _settings.VoiceServerBaseUri = VoiceEndpointBox.Text.Trim();
        _settings.VoiceServerWorkingDirectory = VoiceDirectoryBox.Text.Trim();
        Changed();
    }

    private async void BrowseVoiceDirectory_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _pickerOwnerHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        VoiceDirectoryBox.Text = folder.Path;
        VoiceSettings_LostFocus(VoiceDirectoryBox, e);
    }

    private void Changed()
    {
        if (!_isLoading) _settingsChanged();
    }

    private void UpdateEffectControlStates()
    {
        var gpuEnhancementsEnabled = GpuEnhancementsSwitch.IsOn;
        LavaSwitch.IsEnabled = gpuEnhancementsEnabled;
        CracksSwitch.IsEnabled = gpuEnhancementsEnabled;
        var lavaEnabled = gpuEnhancementsEnabled && LavaSwitch.IsOn;
        LavaDrippingSwitch.IsEnabled = lavaEnabled;
        LavaAmountSlider.IsEnabled = lavaEnabled && LavaDrippingSwitch.IsOn;
        LavaHueSlider.IsEnabled = lavaEnabled;
        LavaVariationSlider.IsEnabled = lavaEnabled;

        var lightningEnabled = gpuEnhancementsEnabled && CracksSwitch.IsOn;
        CrackAmountSlider.IsEnabled = lightningEnabled;
        CrackHueSlider.IsEnabled = lightningEnabled;
        CrackVariationSlider.IsEnabled = lightningEnabled;
    }

    private void TitleBar_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(TitleBar).Properties.IsLeftButtonPressed)
            WindowEffects.BeginMove(WindowNative.GetWindowHandle(this));
    }

    private void UpdateLabels()
    {
        LavaAmountLabel.Text = $"FLOW DENSITY  ·  {_settings.LavaAmount.GetValueOrDefault() * 100:N0}%";
        CrackAmountLabel.Text = $"LIGHTNING DENSITY  ·  {_settings.CrackAmount.GetValueOrDefault() * 100:N0}%";
        LavaHueLabel.Text = $"LAVA SPECTRUM  ·  {_settings.LavaHue.GetValueOrDefault():N0}°";
        CrackHueLabel.Text = $"LIGHTNING SPECTRUM  ·  {_settings.CrackHue.GetValueOrDefault():N0}°";
        LavaVariationLabel.Text = $"LAVA VARIATION  ·  {_settings.LavaVariation.GetValueOrDefault():N2}x";
        CrackVariationLabel.Text = $"LIGHTNING VARIATION  ·  {_settings.CrackVariation.GetValueOrDefault():N2}x";
    }
}
