using System.IO;
using System.Text;
using System.Text.Json;
using AiCoreMonitor.Core;

namespace AiCoreMonitor.Providers;

public sealed class CodexTelemetryProvider(string? sessionRoot = null) : ITelemetryProvider<CodexSnapshot>
{
    private const int MaxFiles = 12;
    private const int MaxTailBytes = 4 * 1024 * 1024;
    private readonly string _sessionRoot = sessionRoot ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    public string Name => "Codex";

    public async Task<CodexSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_sessionRoot))
            throw new DirectoryNotFoundException($"Codex sessions were not found at {_sessionRoot}.");

        var files = Directory.EnumerateFiles(_sessionRoot, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(MaxFiles);

        CodexSnapshot? latest = null;
        foreach (var file in files)
        {
            var tail = await ReadTailAsync(file.FullName, MaxTailBytes, cancellationToken).ConfigureAwait(false);
            var lines = tail.Split('\n');
            for (var index = lines.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryParseEvent(lines[index].TrimEnd('\r'), file.FullName, out var snapshot) &&
                    (latest is null || snapshot!.ObservedAt > latest.ObservedAt))
                    latest = snapshot;
            }
        }

        if (latest is not null) return latest;
        throw new InvalidDataException("No Codex token telemetry has been recorded yet.");
    }

    internal static bool TryParseEvent(string line, string source, out CodexSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (GetString(root, "type") != "event_msg" ||
                !root.TryGetProperty("payload", out var payload) ||
                GetString(payload, "type") != "token_count" ||
                !root.TryGetProperty("timestamp", out var timestampElement) ||
                !DateTimeOffset.TryParse(timestampElement.GetString(), out var observedAt))
                return false;

            payload.TryGetProperty("info", out var info);
            payload.TryGetProperty("rate_limits", out var limits);
            var limitId = GetString(limits, "limit_id");
            if (limitId is not null && !limitId.Equals("codex", StringComparison.OrdinalIgnoreCase))
                return false;
            limits.TryGetProperty("primary", out var primary);

            DateTimeOffset? resetAt = null;
            if (TryGetInt64(primary, "resets_at", out var resetSeconds))
                resetAt = DateTimeOffset.FromUnixTimeSeconds(resetSeconds).ToLocalTime();

            snapshot = new CodexSnapshot(
                observedAt.ToLocalTime(),
                GetString(limits, "plan_type") ?? "Unknown",
                GetInt32(primary, "window_minutes"),
                GetDouble(primary, "used_percent"),
                resetAt,
                GetNestedInt64(info, "total_token_usage", "total_tokens"),
                GetNestedInt64(info, "last_token_usage", "total_tokens"),
                GetInt64(info, "model_context_window"),
                source);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string> ReadTailAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytesToRead = (int)Math.Min(stream.Length, maxBytes);
        if (bytesToRead == 0) return string.Empty;
        stream.Seek(-bytesToRead, SeekOrigin.End);
        var buffer = new byte[bytesToRead];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(buffer);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static double GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var result)
            ? result : 0;

    private static int GetInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result)
            ? result : 0;

    private static long GetInt64(JsonElement element, string name) => TryGetInt64(element, name, out var result) ? result : 0;

    private static bool TryGetInt64(JsonElement element, string name, out long result)
    {
        result = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetInt64(out result);
    }

    private static long GetNestedInt64(JsonElement element, string objectName, string valueName) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(objectName, out var nested)
            ? GetInt64(nested, valueName) : 0;
}
