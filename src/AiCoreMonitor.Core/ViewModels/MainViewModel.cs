using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AiCoreMonitor.Core;
using AiCoreMonitor.Services;

namespace AiCoreMonitor.ViewModels;

public sealed class MainViewModel(TelemetryService telemetry) : INotifyPropertyChanged, IDisposable
{
    private readonly Queue<double> _gpuHistory = new();
    private ProviderResult<CodexSnapshot>? _codex;
    private ProviderResult<GpuSnapshot>? _gpu;
    private ProviderResult<CpuSnapshot>? _cpu;
    private ProviderResult<OllamaSnapshot>? _ollama;
    private bool _isRefreshing;
    private double[] _gpuSamples = [];
    private string _lastRefreshText = "INITIALIZING";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CodexRemaining => _codex?.Value is { } value ? $"{100 - Math.Clamp(value.UsedPercent, 0, 100):N1}%" : "--%";
    public double CodexUsedPercent => Math.Clamp(_codex?.Value?.UsedPercent ?? 0, 0, 100);
    public string CodexPlan => _codex?.Value is { } value ? $"{value.Plan.ToUpperInvariant()} / {FormatWindow(value.WindowMinutes)}" : "UNAVAILABLE";
    public string CodexReset => _codex?.Value?.ResetsAt is { } reset ? $"RESET {reset:ddd HH:mm}" : "NO RESET DATA";
    public string CodexWindowUsage => _codex?.Value is { } value ? $"{FormatWindow(value.WindowMinutes)}  {value.UsedPercent:N1}% USED" : "NO WINDOW DATA";
    public string CodexSecondaryUsage => _codex?.Value is { SecondaryWindowMinutes: > 0 } value
        ? $"{FormatWindow(value.SecondaryWindowMinutes)}  {value.SecondaryUsedPercent:N1}% USED" : "NO WEEKLY DATA";
    public string CodexSecondaryReset => _codex?.Value?.SecondaryResetsAt is { } reset ? $"RESET {reset:ddd HH:mm}" : string.Empty;
    public string CodexTokens => _codex?.Value is { } value ? $"{Compact(value.TotalTokens)} CONTEXT TOKENS" : "NO TOKEN DATA";

    public string GpuName => _gpu?.Value?.Name.ToUpperInvariant() ?? "NVIDIA GPU UNAVAILABLE";
    public string GpuUsage => _gpu?.Value is { } value ? $"{value.UtilizationPercent:N0}%" : "--%";
    public double GpuUsagePercent => Math.Clamp(_gpu?.Value?.UtilizationPercent ?? 0, 0, 100);
    public string GpuMemory => _gpu?.Value is { } value ? $"{value.MemoryUsedMiB / 1024:N1} / {value.MemoryTotalMiB / 1024:N1} GB" : "-- / -- GB";
    public string GpuThermals => _gpu?.Value is { } value ? $"{value.TemperatureC:N0} C   /   {value.PowerWatts:N0} W" : "-- C   /   -- W";
    public double[] GpuSamples { get => _gpuSamples; private set => Set(ref _gpuSamples, value); }

    public string CpuUsage => _cpu?.Value is { } value ? $"{value.UtilizationPercent:N0}%" : "--%";
    public double CpuUsagePercent => Math.Clamp(_cpu?.Value?.UtilizationPercent ?? 0, 0, 100);
    public string CpuDetails => _cpu?.Value is { } value ? $"{value.LogicalProcessorCount} LOGICAL CORES" : "CPU UNAVAILABLE";

    public string ModelCount => _ollama?.Value is { } value ? value.InstalledCount.ToString(CultureInfo.InvariantCulture) : "--";
    public string ModelCountLabel => _ollama?.Value is { InstalledCount: 1 } ? "MODEL INSTALLED" : "MODELS INSTALLED";
    public string OllamaState => _ollama?.Value is { LoadedCount: > 0 } ? "INFERENCE ACTIVE" : _ollama?.IsAvailable == true ? "OLLAMA ONLINE" : "OLLAMA OFFLINE";
    public string ActiveModel => _ollama?.Value?.ActiveModel ?? "No model currently loaded";
    public string ActiveModelShort => ShortModelName(ActiveModel);
    public string ModelStorage => _ollama?.Value is { } value ? $"{value.TotalBytes / 1_073_741_824d:N1} GB ON DISK   /   {value.LoadedCount} LOADED" : "NO LOCAL DATA";

    public int AvailableProviders => (_codex?.IsAvailable == true ? 1 : 0) + (_gpu?.IsAvailable == true ? 1 : 0) + (_cpu?.IsAvailable == true ? 1 : 0) + (_ollama?.IsAvailable == true ? 1 : 0);
    public string SystemState => AvailableProviders switch { 4 => "ALL SYSTEMS NOMINAL", > 0 => $"{AvailableProviders}/4 PROVIDERS ONLINE", _ => "PROVIDERS OFFLINE" };
    public string StatusColor => AvailableProviders switch { 4 => "#35F2B4", > 0 => "#FFC857", _ => "#FF5A7A" };
    public string ErrorSummary
    {
        get
        {
            var errors = new[] { Prefix("Codex", _codex?.Error), Prefix("GPU", _gpu?.Error), Prefix("CPU", _cpu?.Error), Prefix("Ollama", _ollama?.Error) }.Where(value => value is not null);
            return string.Join("  /  ", errors!);
        }
    }
    public string LastRefreshText { get => _lastRefreshText; private set => Set(ref _lastRefreshText, value); }

    public async Task RefreshAsync(bool includeSlowProviders, CancellationToken cancellationToken)
    {
        if (_isRefreshing) return;
        _isRefreshing = true;
        try
        {
            var gpuTask = telemetry.CollectGpuAsync(cancellationToken);
            var cpuTask = telemetry.CollectCpuAsync(cancellationToken);
            if (includeSlowProviders)
            {
                var codexTask = telemetry.CollectCodexAsync(cancellationToken);
                var ollamaTask = telemetry.CollectOllamaAsync(cancellationToken);
                await Task.WhenAll(gpuTask, codexTask, ollamaTask);
                _codex = await codexTask;
                _ollama = await ollamaTask;
            }
            _gpu = await gpuTask;
            _cpu = await cpuTask;
            if (_gpu.Value is { } gpu)
            {
                _gpuHistory.Enqueue(gpu.UtilizationPercent);
                while (_gpuHistory.Count > 48) _gpuHistory.Dequeue();
                GpuSamples = _gpuHistory.ToArray();
            }
            LastRefreshText = $"UPDATED {DateTime.Now:HH:mm:ss}";
            RaiseAll();
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RaiseAll()
    {
        foreach (var property in new[] { nameof(CodexRemaining), nameof(CodexUsedPercent), nameof(CodexPlan), nameof(CodexReset), nameof(CodexWindowUsage), nameof(CodexSecondaryUsage), nameof(CodexSecondaryReset), nameof(CodexTokens),
                     nameof(GpuName), nameof(GpuUsage), nameof(GpuUsagePercent), nameof(GpuMemory), nameof(GpuThermals),
                     nameof(CpuUsage), nameof(CpuUsagePercent), nameof(CpuDetails),
                     nameof(ModelCount), nameof(ModelCountLabel), nameof(OllamaState), nameof(ActiveModel), nameof(ActiveModelShort), nameof(ModelStorage),
                     nameof(AvailableProviders), nameof(SystemState), nameof(StatusColor), nameof(ErrorSummary) })
            OnPropertyChanged(property);
    }

    private static string FormatWindow(int minutes) => minutes switch
    {
        10_080 => "WEEKLY",
        >= 1440 when minutes % 1440 == 0 => $"{minutes / 1440}D WINDOW",
        >= 60 when minutes % 60 == 0 => $"{minutes / 60}H WINDOW",
        _ => $"{minutes}M WINDOW"
    };

    internal static string Compact(long value) => value switch
    {
        >= 1_000_000_000 => $"{Rounded(value / 1_000_000_000d):N1}B",
        >= 1_000_000 => $"{Rounded(value / 1_000_000d):N1}M",
        >= 1_000 => $"{Rounded(value / 1_000d):N1}K",
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };

    internal static string ShortModelName(string value)
    {
        var name = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? value;
        if (name.EndsWith(":latest", StringComparison.OrdinalIgnoreCase))
            name = name[..^":latest".Length];
        return name.Length <= 24 ? name : $"{name[..21].TrimEnd('-', '_', '.')}...";
    }

    private static double Rounded(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static string? Prefix(string provider, string? error) => string.IsNullOrWhiteSpace(error) ? null : $"{provider}: {error}";
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    public void Dispose() => telemetry.Dispose();
}
