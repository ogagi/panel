using VoiceEngine.Client;

namespace AiCoreMonitor.Tests;

[TestClass]
public sealed class VoiceProtocolTests
{
    [TestMethod]
    public void AudioFrameMatchesPythonGoldenVector()
    {
        var pcm = Enumerable.Range(0, 320).SelectMany(value => BitConverter.GetBytes((short)value)).ToArray();
        var encoded = new AudioFrame(AudioFrameKind.Input, 3, 42, 99, pcm).Encode();

        CollectionAssert.AreEqual(new byte[]
        {
            1, 1, 3, 0, 42, 0, 0, 0, 99, 0, 0, 0, 0, 0, 0, 0
        }, encoded[..AudioFrame.HeaderSize]);
        Assert.HasCount(656, encoded);
        var decoded = AudioFrame.Decode(encoded);
        Assert.AreEqual(AudioFrameKind.Input, decoded.Kind);
        Assert.AreEqual((uint)42, decoded.Sequence);
        Assert.AreEqual((ulong)99, decoded.TurnId);
        CollectionAssert.AreEqual(pcm, decoded.Pcm);
    }

    [TestMethod]
    public void AudioFrameRejectsProtocolViolations()
    {
        Assert.Throws<VoiceProtocolException>(() => AudioFrame.Decode([]));
        Assert.Throws<VoiceProtocolException>(() => AudioFrame.Decode(new byte[17]));
        var frame = new AudioFrame(AudioFrameKind.Input, 0, 0, 0, new byte[640]).Encode();
        frame[0] = 2;
        Assert.Throws<VoiceProtocolException>(() => AudioFrame.Decode(frame));
    }

    [TestMethod]
    public void ParsesTypedConversationEvents()
    {
        var ready = ConversationEventParser.Parse("""{"type":"session.ready","model":"qwen","profile":"natural","voice":"v","tts_backend":"tts","context_tokens":8192,"context_mode":"auto"}"""u8);
        Assert.IsInstanceOfType<SessionReadyEvent>(ready);
        Assert.AreEqual(8192, ((SessionReadyEvent)ready).ContextTokens);

        var models = (ModelListEvent)ConversationEventParser.Parse("""{"type":"model.list","models":["a","b"],"current":"b"}"""u8);
        CollectionAssert.AreEqual(new[] { "a", "b" }, models.Models.ToArray());

        var error = (VoiceErrorEvent)ConversationEventParser.Parse("""{"type":"error","code":"server_busy","message":"busy","recoverable":true}"""u8);
        Assert.IsTrue(error.Recoverable);
    }

    [TestMethod]
    public void ContextSelectionRejectsNonPositiveSize()
    {
        using var client = new AsyncDisposableScope<VoiceConversationClient>(new VoiceConversationClient());
        Assert.Throws<ArgumentOutOfRangeException>(() => client.Value.SelectContextAsync(0));
    }

    private sealed class AsyncDisposableScope<T>(T value) : IDisposable where T : IAsyncDisposable
    {
        public T Value { get; } = value;
        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
