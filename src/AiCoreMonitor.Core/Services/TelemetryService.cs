using System.Net.Http;
using AiCoreMonitor.Core;
using AiCoreMonitor.Providers;

namespace AiCoreMonitor.Services;

public sealed class TelemetryService : IDisposable
{
    private readonly HttpClient _ollamaHttpClient;
    private readonly HttpClient _ogagiHttpClient;
    private readonly ITelemetryProvider<CodexSnapshot> _codex;
    private readonly ITelemetryProvider<CpuSnapshot> _cpu;
    private readonly ITelemetryProvider<GpuSnapshot> _gpu;
    private readonly ITelemetryProvider<LocalEngineSnapshot> _localEngine;

    public TelemetryService(string? codexSessionRoot = null)
    {
        _ollamaHttpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        })
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(3)
        };
        _ogagiHttpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        })
        {
            Timeout = TimeSpan.FromSeconds(3)
        };
        _codex = new CodexTelemetryProvider(codexSessionRoot);
        _cpu = new CpuTelemetryProvider();
        _gpu = new NvidiaGpuTelemetryProvider();
        _localEngine = new LocalEngineTelemetryProvider(
            new OllamaTelemetryProvider(_ollamaHttpClient),
            new OgagiTelemetryProvider(_ogagiHttpClient));
    }

    public Task<ProviderResult<CodexSnapshot>> CollectCodexAsync(CancellationToken cancellationToken) =>
        CollectAsync(_codex, TimeSpan.FromSeconds(3), cancellationToken);

    public Task<ProviderResult<GpuSnapshot>> CollectGpuAsync(CancellationToken cancellationToken) =>
        CollectAsync(_gpu, TimeSpan.FromSeconds(2), cancellationToken);

    public Task<ProviderResult<CpuSnapshot>> CollectCpuAsync(CancellationToken cancellationToken) =>
        CollectAsync(_cpu, TimeSpan.FromSeconds(2), cancellationToken);

    public Task<ProviderResult<LocalEngineSnapshot>> CollectLocalEngineAsync(CancellationToken cancellationToken) =>
        CollectAsync(_localEngine, TimeSpan.FromSeconds(4), cancellationToken);

    private static async Task<ProviderResult<T>> CollectAsync<T>(ITelemetryProvider<T> provider,
        TimeSpan timeout, CancellationToken cancellationToken) where T : class
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return ProviderResult<T>.Success(await provider.CollectAsync(timeoutSource.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult<T>.Failure(new TimeoutException($"{provider.Name} timed out after {timeout.TotalSeconds:N0}s."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ProviderResult<T>.Failure(exception);
        }
    }

    public void Dispose()
    {
        _ollamaHttpClient.Dispose();
        _ogagiHttpClient.Dispose();
    }
}
