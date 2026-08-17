namespace AiCoreMonitor.Core;

public interface ITelemetryProvider<T> where T : class
{
    string Name { get; }
    Task<T> CollectAsync(CancellationToken cancellationToken);
}
