using AiCoreMonitor.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AiCoreMonitor.WinUI;

public sealed partial class SettingsWindow : Window
{
    private readonly WidgetSettings _settings;
    private readonly Action _settingsChanged;
    private readonly nint _pickerOwnerHandle;
    private bool _isLoading = true;

    public SettingsWindow(WidgetSettings settings, Action settingsChanged, nint pickerOwnerHandle)
    {
        _settings = settings;
        _settingsChanged = settingsChanged;
        _pickerOwnerHandle = pickerOwnerHandle;
        InitializeComponent();
        Title = "AI Core Monitor Settings";

        LavaAmountSlider.Minimum = 0.1;
        LavaAmountSlider.Maximum = 1.0;
        LavaAmountSlider.StepFrequency = 0.1;
        CrackAmountSlider.Minimum = 0.1;
        CrackAmountSlider.Maximum = 1.0;
        CrackAmountSlider.StepFrequency = 0.1;
        VariationSlider.Minimum = 0.25;
        VariationSlider.Maximum = 2.0;
        VariationSlider.StepFrequency = 0.25;

        TopmostSwitch.IsOn = _settings.Topmost;
        GpuVisualsSwitch.IsOn = _settings.GpuVisualsEnabled;
        VoiceEndpointBox.Text = _settings.VoiceServerBaseUri;
        VoiceDirectoryBox.Text = _settings.VoiceServerWorkingDirectory;
        LavaSwitch.IsOn = _settings.LavaEnabled == true;
        LavaDrippingSwitch.IsOn = _settings.LavaDripping;
        CracksSwitch.IsOn = _settings.CracksEnabled == true;
        LavaAmountSlider.Value = _settings.LavaAmount!.Value;
        CrackAmountSlider.Value = _settings.CrackAmount!.Value;
        LavaHueSlider.Value = _settings.LavaHue!.Value;
        CrackHueSlider.Value = _settings.CrackHue!.Value;
        VariationSlider.Value = _settings.EffectVariation;
        UpdateLabels();
        _isLoading = false;
    }

    private void TopmostSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.Topmost = TopmostSwitch.IsOn; Changed(); }
    private void GpuVisualsSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.GpuVisualsEnabled = GpuVisualsSwitch.IsOn; Changed(); }
    private void LavaSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.LavaEnabled = LavaSwitch.IsOn; Changed(); }
    private void LavaDrippingSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.LavaDripping = LavaDrippingSwitch.IsOn; Changed(); }
    private void CracksSwitch_Toggled(object sender, RoutedEventArgs e) { _settings.CracksEnabled = CracksSwitch.IsOn; Changed(); }

    private void LavaAmountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { _settings.LavaAmount = e.NewValue; UpdateLabels(); Changed(); }
    private void CrackAmountSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { _settings.CrackAmount = e.NewValue; UpdateLabels(); Changed(); }
    private void LavaHueSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { _settings.LavaHue = e.NewValue; UpdateLabels(); Changed(); }
    private void CrackHueSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { _settings.CrackHue = e.NewValue; UpdateLabels(); Changed(); }
    private void VariationSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { _settings.EffectVariation = e.NewValue; UpdateLabels(); Changed(); }

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

    private void UpdateLabels()
    {
        LavaAmountLabel.Text = $"Lava flow amount: {_settings.LavaAmount.GetValueOrDefault() * 100:N0}%";
        CrackAmountLabel.Text = $"Crack field amount: {_settings.CrackAmount.GetValueOrDefault() * 100:N0}%";
        LavaHueLabel.Text = $"Lava hue: {_settings.LavaHue.GetValueOrDefault():N0}°";
        CrackHueLabel.Text = $"Crack hue: {_settings.CrackHue.GetValueOrDefault():N0}°";
        VariationLabel.Text = $"Movement: {_settings.EffectVariation:N2}";
    }
}
