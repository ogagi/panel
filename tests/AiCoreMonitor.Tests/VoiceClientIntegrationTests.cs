using VoiceEngine.Client;

namespace AiCoreMonitor.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class VoiceClientIntegrationTests
{
    [TestMethod]
    public async Task ClientNegotiatesAgainstConfiguredLoopbackServer()
    {
        var configuredUrl = Environment.GetEnvironmentVariable("VOICE_ENGINE_E2E_URL");
        if (string.IsNullOrWhiteSpace(configuredUrl)) return;

        var baseUri = new Uri(configuredUrl, UriKind.Absolute);
        var capabilities = await VoiceConversationClient.GetCapabilitiesAsync(baseUri);
        Assert.AreEqual(1, capabilities.ProtocolVersion);

        await using var client = new VoiceConversationClient();
        var ready = await client.ConnectAsync(baseUri, new VoiceSessionOptions("responsive"));
        Assert.AreEqual("responsive", ready.Profile);

        await client.ListModelsAsync();
        await client.GetContextAsync();
        await client.ListVoicesAsync();
        await client.ListProfilesAsync();

        var remaining = new HashSet<string>(StringComparer.Ordinal)
        {
            "model.list", "context.info", "voice.list", "profile.list"
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var item in client.ReadEventsAsync(timeout.Token))
        {
            remaining.Remove(item.Type);
            if (remaining.Count == 0) break;
        }
        Assert.HasCount(0, remaining);
    }
}
