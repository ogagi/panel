using System.Net.Http;
using System.Text.Json;
using AiCoreMonitor.Core;

namespace AiCoreMonitor.Providers;

public sealed class OllamaTelemetryProvider(HttpClient httpClient) : ITelemetryProvider<OllamaSnapshot>
{
    public string Name => "Ollama";

    public async Task<OllamaSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        using var tagsResponse = await httpClient.GetAsync("api/tags", cancellationToken).ConfigureAwait(false);
        tagsResponse.EnsureSuccessStatusCode();
        using var psResponse = await httpClient.GetAsync("api/ps", cancellationToken).ConfigureAwait(false);
        psResponse.EnsureSuccessStatusCode();

        await using var tagsStream = await tagsResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var psStream = await psResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var tags = await JsonDocument.ParseAsync(tagsStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        using var ps = await JsonDocument.ParseAsync(psStream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var models = new List<LocalModel>();
        if (tags.RootElement.TryGetProperty("models", out var modelArray))
        {
            foreach (var model in modelArray.EnumerateArray())
            {
                model.TryGetProperty("details", out var details);
                var name = GetString(model, "name") ?? "Unknown model";
                models.Add(new LocalModel(
                    name,
                    GetInt64(model, "size"),
                    GetString(details, "family"),
                    GetString(details, "parameter_size"),
                    GetString(details, "quantization_level"),
                    name));
            }
        }

        string? activeModel = null;
        var loadedCount = 0;
        if (ps.RootElement.TryGetProperty("models", out var loadedModels))
        {
            loadedCount = loadedModels.GetArrayLength();
            if (loadedCount > 0) activeModel = GetString(loadedModels[0], "name");
        }

        return new OllamaSnapshot(DateTimeOffset.Now, models.Count, loadedCount,
            models.Sum(model => model.SizeBytes), activeModel, models);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long GetInt64(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            ? result : 0;
}
