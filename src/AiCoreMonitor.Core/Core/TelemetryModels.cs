namespace AiCoreMonitor.Core;

public sealed record CodexSnapshot(DateTimeOffset ObservedAt, string Plan, int WindowMinutes,
    double UsedPercent, DateTimeOffset? ResetsAt, long TotalTokens, long LastTokens,
    long ContextWindow, string Source);

public sealed record GpuSnapshot(DateTimeOffset ObservedAt, string Name, double UtilizationPercent,
    double MemoryUsedMiB, double MemoryTotalMiB, double TemperatureC, double PowerWatts,
    IReadOnlyList<GpuProcess> Processes);

public sealed record GpuProcess(int ProcessId, string Name, double? MemoryUsedMiB);

public sealed record CpuSnapshot(DateTimeOffset ObservedAt, string Name, double UtilizationPercent,
    int LogicalProcessorCount, double NominalClockGhz);

public sealed record OllamaSnapshot(DateTimeOffset ObservedAt, int InstalledCount, int LoadedCount,
    long TotalBytes, string? ActiveModel, IReadOnlyList<LocalModel> Models);

public sealed record OgagiSnapshot(DateTimeOffset ObservedAt, string ControllerState,
    string? ActiveModel, string? Backend, IReadOnlyList<LocalModel> Models);

public sealed record LocalEngineSnapshot(DateTimeOffset ObservedAt, string EngineId,
    string EngineName, int InstalledCount, int LoadedCount, long TotalBytes,
    string? ActiveModel, string? Backend, IReadOnlyList<LocalModel> Models);

public sealed record LocalModel(string Name, long SizeBytes, string? Family,
    string? ParameterSize, string? Quantization, string? Id = null);

public sealed record ProviderResult<T>(T? Value, string? Error, DateTimeOffset CollectedAt) where T : class
{
    public bool IsAvailable => Value is not null;
    public static ProviderResult<T> Success(T value) => new(value, null, DateTimeOffset.Now);
    public static ProviderResult<T> Failure(Exception exception) => new(null, exception.Message, DateTimeOffset.Now);
}
