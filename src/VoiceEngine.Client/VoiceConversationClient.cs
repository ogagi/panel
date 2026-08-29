using System.Buffers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace VoiceEngine.Client;

public sealed class VoiceConversationClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<ConversationEvent> _events = Channel.CreateBounded<ConversationEvent>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = true });
    private Task? _receiveTask;
    private uint _inputSequence;
    private int _disposed;

    public VoiceConnectionState State { get; private set; } = VoiceConnectionState.Disconnected;
    public Uri? ServerBaseUri { get; private set; }

    public static async Task<VoiceCapabilities> GetCapabilitiesAsync(Uri serverBaseUri, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { BaseAddress = NormalizeBaseUri(serverBaseUri), Timeout = TimeSpan.FromSeconds(5) };
        var result = await http.GetFromJsonAsync<VoiceCapabilities>("v1/capabilities", cancellationToken).ConfigureAwait(false);
        return result ?? throw new VoiceProtocolException("Capabilities response was empty.");
    }

    public async Task<SessionReadyEvent> ConnectAsync(Uri serverBaseUri, VoiceSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (State != VoiceConnectionState.Disconnected) throw new InvalidOperationException("The client has already been connected.");
        State = VoiceConnectionState.Connecting;
        ServerBaseUri = NormalizeBaseUri(serverBaseUri);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        try
        {
            await _socket.ConnectAsync(BuildStreamUri(ServerBaseUri, Guid.NewGuid()), linked.Token).ConfigureAwait(false);
            options ??= new VoiceSessionOptions();
            await SendJsonAsync(new
            {
                type = "session.configure",
                profile = options.Profile,
                language = options.Language,
                input_audio = new { encoding = "pcm_s16le", sample_rate = 16_000, channels = 1 }
            }, linked.Token).ConfigureAwait(false);

            var first = await ReceiveMessageAsync(linked.Token).ConfigureAwait(false);
            if (first.Type != WebSocketMessageType.Text) throw new VoiceProtocolException("Expected session.ready as the first server message.");
            var parsed = ConversationEventParser.Parse(first.Data);
            if (parsed is VoiceErrorEvent error)
                throw new VoiceServerRejectedException(error.Code, error.Message, error.Recoverable);
            if (parsed is not SessionReadyEvent ready)
                throw new VoiceProtocolException("Expected session.ready as the first server event.");

            State = VoiceConnectionState.Connected;
            await _events.Writer.WriteAsync(ready, linked.Token).ConfigureAwait(false);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
            return ready;
        }
        catch (VoiceClientException)
        {
            State = VoiceConnectionState.Disconnected;
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException)
        {
            State = VoiceConnectionState.Disconnected;
            throw new VoiceConnectionException($"Could not connect to {ServerBaseUri}.", exception);
        }
        finally
        {
            linked.Dispose();
        }
    }

    public async IAsyncEnumerable<ConversationEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
    }

    public Task SendInputAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken = default)
    {
        if (pcm.Length != AudioFrame.InputPcmBytes) throw new ArgumentException("Input audio must be exactly 640 bytes (20 ms).", nameof(pcm));
        var frame = new AudioFrame(AudioFrameKind.Input, 0, _inputSequence++, 0, pcm.ToArray());
        return SendBinaryAsync(frame.Encode(), cancellationToken);
    }

    public Task CancelResponseAsync(CancellationToken token = default) => SendControlAsync("response.cancel", token);
    public Task ListModelsAsync(CancellationToken token = default) => SendControlAsync("model.list", token);
    public Task SelectModelAsync(string model, CancellationToken token = default) => SendJsonAsync(new { type = "model.select", model }, token);
    public Task GetContextAsync(CancellationToken token = default) => SendControlAsync("context.get", token);
    public Task SelectContextAsync(int tokens, CancellationToken token = default) => tokens > 0
        ? SendJsonAsync(new { type = "context.select", context_tokens = tokens }, token)
        : throw new ArgumentOutOfRangeException(nameof(tokens));
    public Task SelectAutomaticContextAsync(CancellationToken token = default) => SendJsonAsync(new { type = "context.select", context_tokens = "auto" }, token);
    public Task ListVoicesAsync(CancellationToken token = default) => SendControlAsync("voice.list", token);
    public Task SelectVoiceAsync(string voice, CancellationToken token = default) => SendJsonAsync(new { type = "voice.select", voice }, token);
    public Task ListProfilesAsync(CancellationToken token = default) => SendControlAsync("profile.list", token);
    public Task SelectProfileAsync(string profile, CancellationToken token = default) => SendJsonAsync(new { type = "profile.select", profile }, token);
    public Task AcknowledgePlaybackAsync(uint sequence, CancellationToken token = default) => SendJsonAsync(new { type = "playback.progress", sequence }, token);
    public Task AcknowledgeClearedAsync(uint sequence, CancellationToken token = default) => SendJsonAsync(new { type = "playback.cleared", sequence }, token);

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (State is VoiceConnectionState.Disconnected or VoiceConnectionState.Closing) return;
        State = VoiceConnectionState.Closing;
        try
        {
            if (_socket.State == WebSocketState.Open)
                await SendControlAsync("session.close", cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException) { }
        finally
        {
            _lifetime.Cancel();
            if (_receiveTask is not null)
                try { await _receiveTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
            State = VoiceConnectionState.Disconnected;
            _events.Writer.TryComplete();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message.Type == WebSocketMessageType.Close) break;
                ConversationEvent item = message.Type == WebSocketMessageType.Binary
                    ? ParseAudio(message.Data)
                    : ConversationEventParser.Parse(message.Data);
                await _events.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            }
            _events.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _events.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            _events.Writer.TryComplete(exception is VoiceClientException ? exception : new VoiceConnectionException("Voice connection ended unexpectedly.", exception));
        }
    }

    private static AudioOutputEvent ParseAudio(byte[] data)
    {
        var frame = AudioFrame.Decode(data);
        if (frame.Kind != AudioFrameKind.Output) throw new VoiceProtocolException("Server sent a non-output audio frame.");
        return new AudioOutputEvent(frame);
    }

    private Task SendControlAsync(string type, CancellationToken token) => SendJsonAsync(new { type }, token);
    private Task SendJsonAsync<T>(T value, CancellationToken token) => SendBinaryCoreAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, token);
    private Task SendBinaryAsync(ReadOnlyMemory<byte> value, CancellationToken token) => SendBinaryCoreAsync(value, WebSocketMessageType.Binary, token);

    private async Task SendBinaryCoreAsync(ReadOnlyMemory<byte> value, WebSocketMessageType type, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _sendGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open) throw new VoiceConnectionException("Voice WebSocket is not open.");
            await _socket.SendAsync(value, type, true, linked.Token).ConfigureAwait(false);
        }
        finally { _sendGate.Release(); }
    }

    private async Task<(WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(CancellationToken token)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var memory = writer.GetMemory(16 * 1024);
            var result = await _socket.ReceiveAsync(memory, token).ConfigureAwait(false);
            writer.Advance(result.Count);
            if (result.EndOfMessage) return (result.MessageType, writer.WrittenSpan.ToArray());
            if (writer.WrittenCount > 1_048_576) throw new VoiceProtocolException("Server message exceeds 1 MiB.");
        }
    }

    private static Uri NormalizeBaseUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https")) throw new ArgumentException("Server base URI must use HTTP or HTTPS.", nameof(uri));
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
    }

    private static Uri BuildStreamUri(Uri baseUri, Guid sessionId)
    {
        var builder = new UriBuilder(baseUri) { Scheme = baseUri.Scheme == "https" ? "wss" : "ws", Path = $"v1/conversations/{sessionId:N}/stream" };
        return builder.Uri;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await CloseAsync().ConfigureAwait(false);
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
