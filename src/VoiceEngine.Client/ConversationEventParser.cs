using System.Text.Json;

namespace VoiceEngine.Client;

public static class ConversationEventParser
{
    public static ConversationEvent Parse(ReadOnlySpan<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8.ToArray());
            var root = document.RootElement;
            var type = RequiredString(root, "type");
            var turnId = UInt64(root, "turn_id");
            return type switch
            {
                "session.ready" => new SessionReadyEvent(
                    String(root, "model") ?? "", String(root, "profile") ?? "",
                    String(root, "voice") ?? "", String(root, "tts_backend") ?? "",
                    Int32(root, "context_tokens"), String(root, "context_mode")),
                "speech.started" or "speech.ended" => new SpeechEvent(type, turnId),
                "transcript.partial" or "transcript.final" => new TranscriptEvent(type, turnId, String(root, "text") ?? ""),
                "response.text.delta" => new ResponseTextEvent(turnId, String(root, "text") ?? ""),
                "audio.clear" => new AudioClearEvent(UInt32(root, "sequence")),
                "model.list" => new ModelListEvent(StringArray(root, "models"), String(root, "current")),
                "voice.list" => new VoiceListEvent(Choices(root, "voices", "name"), String(root, "current"), String(root, "backend")),
                "profile.list" => new ProfileListEvent(Choices(root, "profiles", "backend"), String(root, "current")),
                "context.info" => new ContextInfoEvent(Int32(root, "context_tokens"), String(root, "mode"), Int32(root, "maximum_tokens")),
                "model.loading" or "model.selected" => new SelectionEvent(type, String(root, "model"), null),
                "context.loading" or "context.selected" => new SelectionEvent(type,
                    root.TryGetProperty("context_tokens", out var context) ? context.ToString() : String(root, "context_mode"), null),
                "voice.selected" => new SelectionEvent(type, String(root, "voice"), String(root, "backend")),
                "profile.selected" => new SelectionEvent(type, String(root, "profile"), String(root, "backend")),
                "response.started" or "response.audio.started" or "response.completed" or "response.cancelled" => new ResponseStateEvent(type, turnId),
                "error" => new VoiceErrorEvent(String(root, "code") ?? "unknown", String(root, "message") ?? "", Bool(root, "recoverable")),
                _ => new UnknownConversationEvent(type, turnId, root.Clone())
            };
        }
        catch (JsonException exception)
        {
            throw new VoiceProtocolException("Server event is not valid JSON.", exception);
        }
    }

    private static string RequiredString(JsonElement root, string name) =>
        String(root, name) ?? throw new VoiceProtocolException($"Server event is missing '{name}'.");
    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True;
    private static int? Int32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;
    private static uint? UInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetUInt32(out var result) ? result : null;
    private static ulong? UInt64(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetUInt64(out var result) ? result : null;
    private static IReadOnlyList<string> StringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
    private static IReadOnlyList<VoiceChoice> Choices(JsonElement root, string name, string displayProperty)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(item =>
        {
            var id = String(item, "id") ?? "";
            var display = String(item, displayProperty) ?? id;
            return new VoiceChoice(id, display, String(item, "backend"));
        }).Where(choice => choice.Id.Length > 0).ToArray();
    }
}
