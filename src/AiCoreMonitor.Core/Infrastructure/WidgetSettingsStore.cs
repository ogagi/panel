using System.IO;
using System.Text.Json;

namespace AiCoreMonitor.Infrastructure;

public sealed class WidgetSettings
{
    public double Width { get; set; } = 460;
    public double Height { get; set; } = 560;
    public bool CompactMode { get; set; }
    public bool CompactSideBar { get; set; }
    public double CompactTopWidth { get; set; } = 520;
    public double CompactTopHeight { get; set; } = 64;
    public double CompactSideWidth { get; set; } = 150;
    public double CompactSideHeight { get; set; } = 330;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool AnimationEnabled { get; set; } = true;
    public bool? LavaEnabled { get; set; }
    public bool? CracksEnabled { get; set; }
    public double EffectIntensity { get; set; } = 0.40;
    public double? LavaAmount { get; set; } = 0.40;
    public double? CrackAmount { get; set; } = 0.46;
    public bool Topmost { get; set; } = true;
    public string[] SectionOrder { get; set; } = ["codex", "system", "models"];
    // WinUI itself is composed by Windows. This controls the panel-owned Composition visuals.
    public bool GpuVisualsEnabled { get; set; } = true;
    // EffectHue is retained to migrate settings saved before the colors were independent.
    public double EffectHue { get; set; } = 215;
    public double? LavaHue { get; set; } = 235;
    public double? CrackHue { get; set; } = 215;
    public double EffectVariation { get; set; } = 0.82;
    public string VoiceServerBaseUri { get; set; } = "http://127.0.0.1:8765";
    public string VoiceProfile { get; set; } = "natural";
    public string VoiceServerWorkingDirectory { get; set; } = "";
}

public sealed class WidgetSettingsStore
{
    private readonly string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiCoreMonitor", "settings.json");

    public WidgetSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(_path)) ?? new WidgetSettings()
                : new WidgetSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new WidgetSettings();
        }
    }

    public void Save(WidgetSettings settings)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, true);
    }
}
