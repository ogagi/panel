using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using AiCoreMonitor.Core;
using AiCoreMonitor.Services;

namespace AiCoreMonitor.ViewModels;

public sealed class MainViewModel(TelemetryService telemetry) : INotifyPropertyChanged, IDisposable
{
    private readonly Queue<double> _cpuHistory = new();
    private readonly Queue<double> _gpuHistory = new();
    private ProviderResult<CodexSnapshot>? _codex;
    private ProviderResult<CpuSnapshot>? _cpu;
    private ProviderResult<GpuSnapshot>? _gpu;
    private ProviderResult<LocalEngineSnapshot>? _localEngine;
    private bool _isRefreshing;
    private double[] _cpuSamples = [];
    private double[] _gpuSamples = [];
    private string _lastRefreshText = "INITIALIZING";

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CodexRemaining => _codex?.Value is { } value ? $"{100 - Math.Clamp(value.UsedPercent, 0, 100):N1}%" : "--%";
    public double CodexUsedPercent => Math.Clamp(_codex?.Value?.UsedPercent ?? 0, 0, 100);
    public string CodexPlan => _codex?.Value is { } value ? $"{value.Plan.ToUpperInvariant()} / {FormatWindow(value.WindowMinutes)}" : "UNAVAILABLE";
    public string CodexReset => _codex?.Value?.ResetsAt is { } reset
        ? reset > DateTimeOffset.Now
            ? $"RESETS {reset:ddd dd MMM HH:mm}".ToUpperInvariant()
            : "RESET DATA STALE"
        : "NO RESET DATA";
    public string CodexTokens => _codex?.Value is { } value ? $"{Compact(value.TotalTokens)} CONTEXT TOKENS" : "NO TOKEN DATA";

    public string CpuName => _cpu?.Value?.Name.ToUpperInvariant() ?? "CPU UNAVAILABLE";
    public string CpuUsage => _cpu?.Value is { } value ? $"{value.UtilizationPercent:N0}%" : "--%";
    public double CpuUsagePercent => Math.Clamp(_cpu?.Value?.UtilizationPercent ?? 0, 0, 100);
    public string CpuDetails => _cpu?.Value is { } value
        ? value.NominalClockGhz > 0
            ? $"{value.LogicalProcessorCount} LOGICAL   /   {value.NominalClockGhz:N1} GHZ"
            : $"{value.LogicalProcessorCount} LOGICAL PROCESSORS"
        : "-- LOGICAL PROCESSORS";
    public double[] CpuSamples { get => _cpuSamples; private set => Set(ref _cpuSamples, value); }

    public string GpuName => _gpu?.Value?.Name.ToUpperInvariant() ?? "NVIDIA GPU UNAVAILABLE";
    public string GpuUsage => _gpu?.Value is { } value ? $"{value.UtilizationPercent:N0}%" : "--%";
    public double GpuUsagePercent => Math.Clamp(_gpu?.Value?.UtilizationPercent ?? 0, 0, 100);
    public string GpuMemory => _gpu?.Value is { } value ? $"{value.MemoryUsedMiB / 1024:N1} / {value.MemoryTotalMiB / 1024:N1} GB" : "-- / -- GB";
    public string GpuThermals => _gpu?.Value is { } value ? $"{value.TemperatureC:N0} C   /   {value.PowerWatts:N0} W" : "-- C   /   -- W";
    public string GpuDetails => $"{GpuMemory}   /   {GpuThermals}";
    public string GpuVramConsumer => _gpu?.Value is { } value ? FormatGpuVramConsumer(value) : "VRAM PROCESS UNAVAILABLE";
    public string GpuVramTooltip => _gpu?.Value is { } value ? FormatGpuVramTooltip(value) : "No NVIDIA process data is available.";
    public double[] GpuSamples { get => _gpuSamples; private set => Set(ref _gpuSamples, value); }

    public string ModelCount => _localEngine?.Value is { } value ? value.InstalledCount.ToString(CultureInfo.InvariantCulture) : "--";
    public string ModelCountLabel => _localEngine?.Value is { InstalledCount: 1 } ? "MODEL INSTALLED" : "MODELS INSTALLED";
    public string LocalEngineState => _localEngine?.Value is { LoadedCount: > 0 } value
        ? $"{value.EngineName.ToUpperInvariant()} ACTIVE"
        : _localEngine?.Value is { } online ? $"{online.EngineName.ToUpperInvariant()} ONLINE" : "LOCAL ENGINES OFFLINE";
    public string ActiveEngine => _localEngine?.Value?.EngineName.ToUpperInvariant() ?? "LOCAL ENGINE";
    public string ActiveModel => _localEngine?.Value?.ActiveModel ?? "No model currently loaded";
    public string ActiveModelShort => ShortModelName(ActiveModel);
    public string ModelStorage => _localEngine?.Value is { } value
        ? value.Backend is { Length: > 0 } backend
            ? $"{value.TotalBytes / 1_073_741_824d:N1} GB   /   {FormatBackend(backend)}"
            : $"{value.TotalBytes / 1_073_741_824d:N1} GB ON DISK   /   {value.LoadedCount} LOADED"
        : "NO LOCAL DATA";
    public string EnginePrivacyLabel => $"LOCALHOST / {ActiveEngine}";

    public int AvailableProviders => (_codex?.IsAvailable == true ? 1 : 0) + (_cpu?.IsAvailable == true ? 1 : 0) +
        (_gpu?.IsAvailable == true ? 1 : 0) + (_localEngine?.IsAvailable == true ? 1 : 0);
    public string SystemState => AvailableProviders switch { 4 => "ALL SYSTEMS NOMINAL", > 0 => $"{AvailableProviders}/4 PROVIDERS ONLINE", _ => "PROVIDERS OFFLINE" };
    public string StatusColor => AvailableProviders switch { 4 => "#35F2B4", > 0 => "#FFC857", _ => "#FF5A7A" };
    public string ErrorSummary
    {
        get
        {
            var errors = new[] { Prefix("Codex", _codex?.Error), Prefix("CPU", _cpu?.Error), Prefix("GPU", _gpu?.Error),
                Prefix("Local engine", _localEngine?.Error) }.Where(value => value is not null);
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
                var localEngineTask = telemetry.CollectLocalEngineAsync(cancellationToken);
                await Task.WhenAll(gpuTask, cpuTask, codexTask, localEngineTask);
                _codex = await codexTask;
                _localEngine = await localEngineTask;
            }
            await Task.WhenAll(gpuTask, cpuTask);
            _gpu = await gpuTask;
            _cpu = await cpuTask;
            if (_cpu.Value is { } cpu)
            {
                _cpuHistory.Enqueue(cpu.UtilizationPercent);
                while (_cpuHistory.Count > 48) _cpuHistory.Dequeue();
                CpuSamples = _cpuHistory.ToArray();
            }
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
        foreach (var property in new[] { nameof(CodexRemaining), nameof(CodexUsedPercent), nameof(CodexPlan), nameof(CodexReset), nameof(CodexTokens),
                     nameof(CpuName), nameof(CpuUsage), nameof(CpuUsagePercent), nameof(CpuDetails),
                     nameof(GpuName), nameof(GpuUsage), nameof(GpuUsagePercent), nameof(GpuMemory), nameof(GpuThermals), nameof(GpuDetails),
                     nameof(GpuVramConsumer), nameof(GpuVramTooltip),
                     nameof(ModelCount), nameof(ModelCountLabel), nameof(LocalEngineState), nameof(ActiveEngine), nameof(ActiveModel),
                     nameof(ActiveModelShort), nameof(ModelStorage), nameof(EnginePrivacyLabel),
                     nameof(AvailableProviders), nameof(SystemState), nameof(StatusColor), nameof(ErrorSummary) })
            OnPropertyChanged(property);
    }

    private static string FormatWindow(int minutes) => minutes switch
    {
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

    internal static string FormatGpuVramConsumer(GpuSnapshot value)
    {
        var usage = value.MemoryTotalMiB > 0 ? value.MemoryUsedMiB / value.MemoryTotalMiB * 100 : 0;
        var process = SelectLikelyGpuConsumer(value.Processes);
        if (process is null)
            return value.Processes.Count > 0
                ? $"{usage:N0}% VRAM   /   {value.Processes.Count} GPU PROCESSES"
                : $"{usage:N0}% VRAM   /   NO PROCESS DATA";

        var name = Path.GetFileNameWithoutExtension(process.Name).ToUpperInvariant();
        var detail = process.MemoryUsedMiB is { } memory
            ? $"{memory / 1024:N1} GB"
            : $"PID {process.ProcessId}";
        return $"{usage:N0}% VRAM   /   {name} {detail}";
    }

    internal static GpuProcess? SelectLikelyGpuConsumer(IReadOnlyList<GpuProcess> processes)
    {
        var measured = processes.Where(process => process.MemoryUsedMiB is not null)
            .OrderByDescending(process => process.MemoryUsedMiB)
            .FirstOrDefault();
        if (measured is not null) return measured;

        return processes.Select(process => (Process: process, Rank: GpuProcessRank(process.Name)))
            .Where(candidate => candidate.Rank > 0)
            .OrderByDescending(candidate => candidate.Rank)
            .ThenBy(candidate => candidate.Process.Name, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => candidate.Process)
            .FirstOrDefault();
    }

    private static string FormatGpuVramTooltip(GpuSnapshot value)
    {
        var process = SelectLikelyGpuConsumer(value.Processes);
        if (process is null) return value.Processes.Count == 0
            ? "NVIDIA did not report any GPU processes."
            : $"NVIDIA reports {value.Processes.Count} GPU processes, but Windows WDDM does not expose their individual VRAM usage.";
        return process.MemoryUsedMiB is { } memory
            ? $"Largest reported GPU process: {process.Name} (PID {process.ProcessId}), {memory / 1024:N1} GB."
            : $"Likely compute process: {process.Name} (PID {process.ProcessId}). Windows WDDM does not expose its individual VRAM usage.";
    }

    private static int GpuProcessRank(string name)
    {
        var value = Path.GetFileNameWithoutExtension(name);
        if (value.Contains("agent-os-engine", StringComparison.OrdinalIgnoreCase)) return 100;
        if (value.Contains("ollama", StringComparison.OrdinalIgnoreCase)) return 95;
        if (value.Contains("llama", StringComparison.OrdinalIgnoreCase)) return 90;
        if (value.Contains("comfy", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("stable-diffusion", StringComparison.OrdinalIgnoreCase)) return 85;
        if (value.Contains("python", StringComparison.OrdinalIgnoreCase)) return 70;
        if (value.Contains("blender", StringComparison.OrdinalIgnoreCase)) return 60;
        return 0;
    }

    private static string FormatBackend(string value) => value switch
    {
        "cuda-full-device" or "nvidia-cuda" => "NVIDIA CUDA",
        "hybrid-cpu-cuda" => "CPU + CUDA",
        "cpu" => "CPU",
        _ => value.Replace('-', ' ').ToUpperInvariant()
    };

    private static double Rounded(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private static string? Prefix(string provider, string? error) => string.IsNullOrWhiteSpace(error) ? null : $"{provider}: {error}";
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return; field = value; OnPropertyChanged(name); }
    public void Dispose() => telemetry.Dispose();
}
