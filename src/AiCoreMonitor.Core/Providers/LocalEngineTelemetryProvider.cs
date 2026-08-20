using AiCoreMonitor.Core;

namespace AiCoreMonitor.Providers;

public sealed class LocalEngineTelemetryProvider(
    ITelemetryProvider<OllamaSnapshot> ollama,
    ITelemetryProvider<OgagiSnapshot> ogagi) : ITelemetryProvider<LocalEngineSnapshot>
{
    public string Name => "Local engine";

    public async Task<LocalEngineSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var ollamaTask = TryCollectAsync(ollama, cancellationToken);
        var ogagiTask = TryCollectAsync(ogagi, cancellationToken);
        await Task.WhenAll(ollamaTask, ogagiTask).ConfigureAwait(false);
        var ollamaSnapshot = (await ollamaTask.ConfigureAwait(false)).Value;
        var ogagiSnapshot = (await ogagiTask.ConfigureAwait(false)).Value;
        return Select(ollamaSnapshot, ogagiSnapshot)
            ?? throw new InvalidOperationException("Neither Ogagi nor Ollama is available.");
    }

    internal static LocalEngineSnapshot? Select(OllamaSnapshot? ollama, OgagiSnapshot? ogagi)
    {
        if (ogagi?.ActiveModel is not null) return FromOgagi(ogagi);
        if (ollama?.LoadedCount > 0) return FromOllama(ollama);
        if (ogagi is not null) return FromOgagi(ogagi);
        return ollama is null ? null : FromOllama(ollama);
    }

    private static LocalEngineSnapshot FromOgagi(OgagiSnapshot value) =>
        new(value.ObservedAt, "ogagi", "Ogagi", value.Models.Count,
            value.ActiveModel is null ? 0 : 1, value.Models.Sum(model => model.SizeBytes),
            value.ActiveModel, value.Backend, value.Models);

    private static LocalEngineSnapshot FromOllama(OllamaSnapshot value) =>
        new(value.ObservedAt, "ollama", "Ollama", value.InstalledCount, value.LoadedCount,
            value.TotalBytes, value.ActiveModel, null, value.Models);

    private static async Task<ProviderAttempt<T>> TryCollectAsync<T>(ITelemetryProvider<T> provider,
        CancellationToken cancellationToken) where T : class
    {
        try
        {
            return new ProviderAttempt<T>(await provider.CollectAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new ProviderAttempt<T>(null);
        }
    }

    private sealed record ProviderAttempt<T>(T? Value) where T : class;
}
