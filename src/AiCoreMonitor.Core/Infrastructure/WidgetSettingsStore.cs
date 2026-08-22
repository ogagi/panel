using System.IO;
using System.Text.Json;

namespace AiCoreMonitor.Infrastructure;

public sealed class WidgetSettings
{
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 480;
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public bool AnimationEnabled { get; set; } = true;
    public bool? LavaEnabled { get; set; } = false;
    public bool? CracksEnabled { get; set; } = true;
    public string? VisualMode { get; set; } = "Freeze";
    public double EffectIntensity { get; set; } = 0.78;
    public double? LavaAmount { get; set; }
    public double? CrackAmount { get; set; }
    public bool Topmost { get; set; } = true;
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
