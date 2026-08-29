using AiCoreMonitor.Services;
using AiCoreMonitor.ViewModels;
using VoiceEngine.Client;

namespace AiCoreMonitor.Tests;

[TestClass]
public sealed class VoiceConversationTests
{
    [TestMethod]
    public void LoopbackEndpointValidationRejectsRemoteTargets()
    {
        Assert.IsTrue(VoiceServerService.TryGetLoopbackBaseUri("http://127.0.0.1:8765", out var loopback));
        Assert.AreEqual("http://127.0.0.1:8765/", loopback!.ToString());
        Assert.IsFalse(VoiceServerService.TryGetLoopbackBaseUri("https://example.com", out _));
        Assert.IsFalse(VoiceServerService.TryGetLoopbackBaseUri("ws://127.0.0.1:8765", out _));
    }

    [TestMethod]
    public void ConversationViewModelBuildsLiveTranscriptAndState()
    {
        var viewModel = new ConversationViewModel();
        viewModel.Starting();
        viewModel.Apply(new SessionReadyEvent("qwen", "natural", "default", "tts", 8192, "auto"));
        Assert.AreEqual("qwen", viewModel.SelectedModel);
        CollectionAssert.Contains(viewModel.Models, "qwen");
        viewModel.Apply(new ModelListEvent(["other"], "qwen"));
        Assert.AreEqual("qwen", viewModel.SelectedModel);
        CollectionAssert.Contains(viewModel.Models, "qwen");
        viewModel.Apply(new TranscriptEvent("transcript.final", 1, "hello"));
        viewModel.Apply(new ResponseStateEvent("response.started", 1));
        viewModel.Apply(new ResponseTextEvent(1, "hello back"));
        viewModel.Apply(new ResponseStateEvent("response.completed", 1));

        Assert.IsTrue(viewModel.IsActive);
        Assert.AreEqual("LISTENING", viewModel.Status);
        Assert.HasCount(2, viewModel.Transcript);
        Assert.AreEqual("YOU", viewModel.Transcript[0].Role);
        Assert.AreEqual("ASSISTANT", viewModel.Transcript[1].Role);
        Assert.AreEqual("hello back", viewModel.Transcript[1].Text);
    }
}
