using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Json;

namespace AiCoreMonitor.Services;

public sealed class VoiceServerService
{
    public static bool TryGetLoopbackBaseUri(string value, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https") || !parsed.IsLoopback)
            return false;
        uri = new Uri(parsed.GetLeftPart(UriPartial.Authority) + "/");
        return true;
    }

    public async Task<bool> IsReadyAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            using var response = await http.GetAsync("health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public async Task StartAndWaitAsync(string workingDirectory, Uri baseUri, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException("Select the Local Voice Engine working directory.");
        if (!File.Exists(Path.Combine(workingDirectory, "config.toml")))
            throw new FileNotFoundException("The selected directory does not contain config.toml.");

        var start = new ProcessStartInfo
        {
            FileName = "uv",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("voice-engine");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add("config.toml");
        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The voice server process could not be started.");
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Could not start 'uv'. Install uv or add it to PATH.", exception);
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsReadyAsync(baseUri, cancellationToken).ConfigureAwait(false)) return;
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }
        throw new TimeoutException("The voice server did not become ready before the startup timeout.");
    }
}
