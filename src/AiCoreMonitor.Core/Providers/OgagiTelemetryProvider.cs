using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiCoreMonitor.Core;
using Microsoft.Data.Sqlite;

namespace AiCoreMonitor.Providers;

public sealed class OgagiTelemetryProvider : ITelemetryProvider<OgagiSnapshot>
{
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly string[] DefaultNamespaces = ["packaged", "development", "development-wsl"];
    private static readonly Regex TokenPattern = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex SessionPattern = new("^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient;
    private readonly string _userDataPath;
    private readonly IReadOnlyList<string> _namespaces;
    private readonly string _clientInstance = RandomNumberGenerator.GetHexString(32).ToLowerInvariant();

    public OgagiTelemetryProvider(HttpClient httpClient, string? userDataPath = null,
        IReadOnlyList<string>? namespaces = null)
    {
        _httpClient = httpClient;
        _userDataPath = Path.GetFullPath(userDataPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ogagi"));
        _namespaces = namespaces ?? DefaultNamespaces;
    }

    public string Name => "Ogagi";

    public async Task<OgagiSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var token = await ReadDaemonTokenAsync(cancellationToken).ConfigureAwait(false);
        var attempts = await Task.WhenAll(_namespaces.Select(value =>
            ProbeAsync(value, token, cancellationToken))).ConfigureAwait(false);
        var probes = attempts.Select(value => value.Probe).ToArray();
        var controller = probes.FirstOrDefault(value => value?.ActiveSessionId is not null)
            ?? probes.FirstOrDefault(value => value is not null)
            ?? throw new HttpRequestException(
                $"The Ogagi controller is offline. {string.Join(" / ", attempts.Select(value => value.Error).Where(value => value is not null))}");

        var models = await ReadCatalogAsync(cancellationToken).ConfigureAwait(false);
        string? activeModel = null;
        string? backend = null;
        var controllerState = "online";
        if (controller.ActiveSessionId is { } sessionId)
        {
            var session = await ReadSessionAsync(controller.Port, token, sessionId, cancellationToken)
                .ConfigureAwait(false);
            activeModel = models.FirstOrDefault(model => model.Id == session.ModelId)?.Name
                ?? session.ModelId;
            backend = session.Backend;
            controllerState = session.Status;
        }

        return new OgagiSnapshot(DateTimeOffset.Now, controllerState, activeModel, backend, models);
    }

    internal static int DeterministicProfilePort(string userDataPath, string daemonNamespace)
    {
        var normalizedPath = OperatingSystem.IsWindows() ? userDataPath.ToLowerInvariant() : userDataPath;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{normalizedPath}\0{daemonNamespace}"));
        var value = BinaryPrimitives.ReadUInt32BigEndian(digest);
        if (daemonNamespace == "development-wsl") return 5_000 + (int)(value % 15_000);
        var offset = daemonNamespace == "development" ? 14_000 : 0;
        var count = daemonNamespace is "packaged" or "development" ? 14_000 : 28_000;
        return 20_000 + offset + (int)(value % count);
    }

    private async Task<ControllerProbeAttempt> ProbeAsync(string daemonNamespace, string token,
        CancellationToken cancellationToken)
    {
        var port = DeterministicProfilePort(_userDataPath, daemonNamespace);
        try
        {
            using var document = await GetJsonAsync(port, "/api/v1/health", token, cancellationToken)
                .ConfigureAwait(false);
            var root = document.RootElement;
            if (GetString(root, "service") != "ogagi-engine-daemon")
                throw new InvalidDataException("The controller identity is invalid.");
            var protocol = GetInt32(root, "protocolVersion");
            if (protocol is not (1 or 2) || GetString(root, "status") is not "ready")
                throw new InvalidDataException("The controller protocol or state is invalid.");
            var sessionId = GetString(root, "activeSessionId");
            if (sessionId is not null && !SessionPattern.IsMatch(sessionId))
                throw new InvalidDataException("The controller session identity is invalid.");
            return new ControllerProbeAttempt(new ControllerProbe(port, sessionId), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new ControllerProbeAttempt(null,
                $"{daemonNamespace}: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task<SessionProbe> ReadSessionAsync(int port, string token, string sessionId,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(port, $"/api/v1/sessions/{sessionId}", token,
            cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        if (GetString(root, "service") != "ogagi-engine-daemon" || GetInt32(root, "protocolVersion") != 2)
            throw new InvalidDataException("The Ogagi session response has an unexpected identity.");
        var returnedSessionId = GetString(root, "sessionId");
        var modelId = GetString(root, "modelId");
        var status = GetString(root, "status");
        if (returnedSessionId != sessionId || string.IsNullOrWhiteSpace(modelId) || modelId.Length > 256 ||
            status is not ("starting" or "ready" or "stopping"))
            throw new InvalidDataException("The Ogagi session response is invalid.");

        string? backend = null;
        if (root.TryGetProperty("backend", out var backendElement) && backendElement.ValueKind == JsonValueKind.Object)
        {
            backend = GetString(backendElement, "execution") ?? GetString(backendElement, "selected");
        }
        return new SessionProbe(modelId, status, backend);
    }

    private async Task<JsonDocument> GetJsonAsync(int port, string path, string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Ogagi-Client-Role", "cli");
        request.Headers.Add("X-Ogagi-Client-Instance", _clientInstance);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            throw new InvalidDataException("The Ogagi response exceeded the telemetry limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaxResponseBytes)
                throw new InvalidDataException("The Ogagi response exceeded the telemetry limit.");
            buffer.Write(chunk, 0, read);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }

    private async Task<string> ReadDaemonTokenAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_userDataPath, "engine-daemon", "daemon-token");
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != 64 || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new FileNotFoundException("The Ogagi controller credential is unavailable.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            65, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[65];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        var value = Encoding.ASCII.GetString(bytes, 0, total);
        if (total != 64 || !TokenPattern.IsMatch(value))
            throw new InvalidDataException("The Ogagi controller credential is invalid.");
        return value;
    }

    private async Task<IReadOnlyList<LocalModel>> ReadCatalogAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_userDataPath, "hearth.sqlite3");
        if (!File.Exists(path)) return [];
        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 1
            };
            await using var connection = new SqliteConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, name, family, quantization, size
                FROM models
                WHERE status <> 'downloading'
                ORDER BY created_at DESC
                LIMIT 64
                """;
            var models = new List<LocalModel>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                models.Add(new LocalModel(reader.GetString(1), reader.GetInt64(4),
                    reader.IsDBNull(2) ? null : reader.GetString(2), null,
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(0)));
            }
            return models;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int GetInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var result) ? result : 0;

    private sealed record ControllerProbe(int Port, string? ActiveSessionId);
    private sealed record ControllerProbeAttempt(ControllerProbe? Probe, string? Error);
    private sealed record SessionProbe(string ModelId, string Status, string? Backend);
}
