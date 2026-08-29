using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceEngine.Client;

public enum VoiceConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Closing
}

public sealed record VoiceSessionOptions(string Profile = "natural", string Language = "auto");

public sealed record VoiceCapabilities(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    IReadOnlyList<string> Languages,
    JsonElement Profiles,
    [property: JsonPropertyName("input_audio")] JsonElement InputAudio,
    [property: JsonPropertyName("output_audio")] JsonElement OutputAudio,
    JsonElement Features);

public sealed record VoiceChoice(string Id, string Name, string? Backend = null);

public abstract record ConversationEvent(string Type, ulong? TurnId);
public sealed record SessionReadyEvent(string Model, string Profile, string Voice, string TtsBackend,
    int? ContextTokens, string? ContextMode) : ConversationEvent("session.ready", null);
public sealed record AudioOutputEvent(AudioFrame Frame) : ConversationEvent("audio.output", Frame.TurnId);
public sealed record SpeechEvent(string EventType, ulong? Id) : ConversationEvent(EventType, Id);
public sealed record TranscriptEvent(string EventType, ulong? Id, string Text) : ConversationEvent(EventType, Id);
public sealed record ResponseTextEvent(ulong? Id, string Text) : ConversationEvent("response.text.delta", Id);
public sealed record AudioClearEvent(uint? Sequence) : ConversationEvent("audio.clear", null);
public sealed record ModelListEvent(IReadOnlyList<string> Models, string? Current) : ConversationEvent("model.list", null);
public sealed record VoiceListEvent(IReadOnlyList<VoiceChoice> Voices, string? Current, string? Backend) : ConversationEvent("voice.list", null);
public sealed record ProfileListEvent(IReadOnlyList<VoiceChoice> Profiles, string? Current) : ConversationEvent("profile.list", null);
public sealed record ContextInfoEvent(int? ContextTokens, string? ContextMode, int? ModelMaximum) : ConversationEvent("context.info", null);
public sealed record SelectionEvent(string EventType, string? Value, string? Backend) : ConversationEvent(EventType, null);
public sealed record ResponseStateEvent(string EventType, ulong? Id) : ConversationEvent(EventType, Id);
public sealed record VoiceErrorEvent(string Code, string Message, bool Recoverable) : ConversationEvent("error", null);
public sealed record UnknownConversationEvent(string EventType, ulong? Id, JsonElement Payload) : ConversationEvent(EventType, Id);

public class VoiceClientException(string message, Exception? inner = null) : Exception(message, inner);
public sealed class VoiceConnectionException(string message, Exception? inner = null) : VoiceClientException(message, inner);
public sealed class VoiceProtocolException(string message, Exception? inner = null) : VoiceClientException(message, inner);
public sealed class VoiceServerRejectedException(string code, string message, bool recoverable)
    : VoiceClientException($"{code}: {message}")
{
    public string Code { get; } = code;
    public bool Recoverable { get; } = recoverable;
}
