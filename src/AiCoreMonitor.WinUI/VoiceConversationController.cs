using System.Threading.Channels;
using VoiceEngine.Client;
using VoiceEngine.Client.WindowsAudio;

namespace AiCoreMonitor.WinUI;

internal sealed class VoiceConversationController : IAsyncDisposable
{
    private readonly Channel<byte[]> _input = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _lifetime = new();
    private VoiceConversationClient? _client;
    private WindowsAudioDevice? _audio;
    private Task? _eventsTask;
    private Task? _inputTask;
    private int _stopping;

    public event Action<ConversationEvent>? EventReceived;

    public async Task StartAsync(Uri baseUri, string profile, CancellationToken cancellationToken)
    {
        if (_client is not null) throw new InvalidOperationException("A voice conversation is already active.");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            var client = new VoiceConversationClient();
            _client = client;
            await client.ConnectAsync(baseUri, new VoiceSessionOptions(profile), linked.Token);

            var audio = await WindowsAudioDevice.CreateAsync();
            _audio = audio;
            audio.InputFrameReady += OnInputFrameReady;
            audio.OutputFrameConsumed += OnOutputFrameConsumed;
            audio.AudioDeviceFailed += OnAudioDeviceFailed;
            _eventsTask = Task.Run(() => ConsumeEventsAsync(client, audio, _lifetime.Token), CancellationToken.None);
            _inputTask = Task.Run(() => SendInputAsync(client, _lifetime.Token), CancellationToken.None);
            audio.Start();

            await client.ListModelsAsync(linked.Token);
            await client.GetContextAsync(linked.Token);
            await client.ListVoicesAsync(linked.Token);
            await client.ListProfilesAsync(linked.Token);
        }
        catch
        {
            await StopAsync();
            throw;
        }
        finally { linked.Dispose(); }
    }

    public void SetMuted(bool muted) => _audio?.SetMuted(muted);
    public Task CancelResponseAsync(CancellationToken token = default) => RequiredClient().CancelResponseAsync(token);
    public Task SelectModelAsync(string model, CancellationToken token = default) => RequiredClient().SelectModelAsync(model, token);
    public Task SelectVoiceAsync(string voice, CancellationToken token = default) => RequiredClient().SelectVoiceAsync(voice, token);
    public Task SelectProfileAsync(string profile, CancellationToken token = default) => RequiredClient().SelectProfileAsync(profile, token);
    public Task SelectContextAsync(string context, CancellationToken token = default) => context == "auto"
        ? RequiredClient().SelectAutomaticContextAsync(token)
        : RequiredClient().SelectContextAsync(int.Parse(context, System.Globalization.CultureInfo.InvariantCulture), token);

    private void OnInputFrameReady(ReadOnlyMemory<byte> pcm) => _input.Writer.TryWrite(pcm.ToArray());
    private void OnOutputFrameConsumed(uint sequence)
    {
        var client = _client;
        if (client is not null) _ = AcknowledgeAsync(client, sequence, _lifetime.Token);
    }

    private void OnAudioDeviceFailed(VoiceAudioDeviceException exception) =>
        EventReceived?.Invoke(new VoiceErrorEvent("audio_device_failed", exception.InnerException?.Message ?? exception.Message, false));

    private static async Task AcknowledgeAsync(VoiceConversationClient client, uint sequence, CancellationToken token)
    {
        try { await client.AcknowledgePlaybackAsync(sequence, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (VoiceClientException) { }
    }

    private async Task SendInputAsync(VoiceConversationClient client, CancellationToken token)
    {
        await foreach (var pcm in _input.Reader.ReadAllAsync(token)) await client.SendInputAudioAsync(pcm, token);
    }

    private async Task ConsumeEventsAsync(VoiceConversationClient client, WindowsAudioDevice audio, CancellationToken token)
    {
        await foreach (var item in client.ReadEventsAsync(token))
        {
            if (item is AudioOutputEvent output)
                await audio.QueueOutputAsync(output.Frame, token);
            else if (item is AudioClearEvent)
            {
                var sequence = audio.ClearOutput();
                if (sequence.HasValue) await client.AcknowledgeClearedAsync(sequence.Value, token);
            }
            else EventReceived?.Invoke(item);
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        try
        {
            _lifetime.Cancel();
            _input.Writer.TryComplete();
            if (_audio is not null)
            {
                _audio.InputFrameReady -= OnInputFrameReady;
                _audio.OutputFrameConsumed -= OnOutputFrameConsumed;
                _audio.AudioDeviceFailed -= OnAudioDeviceFailed;
                await _audio.DisposeAsync();
            }
            if (_client is not null) await _client.DisposeAsync();
            if (_eventsTask is not null) try { await _eventsTask; } catch (OperationCanceledException) { }
            if (_inputTask is not null) try { await _inputTask; } catch (OperationCanceledException) { }
        }
        finally
        {
            _audio = null;
            _client = null;
        }
    }

    private VoiceConversationClient RequiredClient() => _client ?? throw new InvalidOperationException("No active voice conversation.");
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifetime.Dispose();
    }
}
