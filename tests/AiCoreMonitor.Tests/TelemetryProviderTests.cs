using AiCoreMonitor.Providers;
using AiCoreMonitor.ViewModels;

namespace AiCoreMonitor.Tests;

[TestClass]
public sealed class TelemetryProviderTests
{
    private string? _temporaryRoot;

    [TestInitialize]
    public void Initialize()
    {
        _temporaryRoot = Path.Combine(Path.GetTempPath(), $"AiCoreMonitor.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_temporaryRoot);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (_temporaryRoot is not null && Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    [TestMethod]
    public async Task CodexProvider_ReadsLatestValidEventFromMalformedJsonl()
    {
        const string json = """
            {"timestamp":"2026-08-16T12:00:00+00:00","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"total_tokens":12345},"last_token_usage":{"total_tokens":234},"model_context_window":258400},"rate_limits":{"plan_type":"plus","primary":{"window_minutes":300,"used_percent":42.5,"resets_at":1786900000}}}}
            """;
        await File.WriteAllLinesAsync(Path.Combine(_temporaryRoot!, "fixture.jsonl"), ["not json", json]);

        var snapshot = await new CodexTelemetryProvider(_temporaryRoot).CollectAsync(CancellationToken.None);

        Assert.AreEqual(12_345, snapshot.TotalTokens);
        Assert.AreEqual(42.5, snapshot.UsedPercent);
        Assert.AreEqual("plus", snapshot.Plan);
        Assert.AreEqual(300, snapshot.WindowMinutes);
    }

    [TestMethod]
    public void NvidiaParser_UsesInvariantCsvMetrics()
    {
        var snapshot = NvidiaGpuTelemetryProvider.Parse("NVIDIA GeForce RTX 5060 Ti, 73, 8123, 16311, 61, 145.25");

        Assert.AreEqual("NVIDIA GeForce RTX 5060 Ti", snapshot.Name);
        Assert.AreEqual(73, snapshot.UtilizationPercent);
        Assert.AreEqual(16_311, snapshot.MemoryTotalMiB);
        Assert.AreEqual(145.25, snapshot.PowerWatts);
    }

    [TestMethod]
    public void CpuUtilization_UsesIdleShareOfKernelAndUserDeltas()
    {
        var utilization = CpuTelemetryProvider.CalculateUtilization(
            previousIdle: 100, previousKernel: 200, previousUser: 200,
            idle: 150, kernel: 300, user: 300);

        Assert.AreEqual(75, utilization);
    }

    [TestMethod]
    public void OgagiPortDiscovery_MatchesDevelopmentControllerNamespace()
    {
        var path = @"C:\Users\uri_k\AppData\Roaming\Ogagi";

        Assert.AreEqual(26_506, OgagiTelemetryProvider.DeterministicProfilePort(path, "packaged"));
        Assert.AreEqual(45_019, OgagiTelemetryProvider.DeterministicProfilePort(path, "development"));
        Assert.AreEqual(12_736, OgagiTelemetryProvider.DeterministicProfilePort(path, "development-wsl"));
    }

    [TestMethod]
    public void LocalEngineSelection_PrefersActiveOgagiSession()
    {
        var observedAt = DateTimeOffset.Now;
        var ollama = new AiCoreMonitor.Core.OllamaSnapshot(observedAt, 2, 1, 2_000,
            "ollama-active", []);
        var ogagi = new AiCoreMonitor.Core.OgagiSnapshot(observedAt, "ready",
            "ogagi-active", "cuda-full-device", []);

        var selected = LocalEngineTelemetryProvider.Select(ollama, ogagi);

        Assert.IsNotNull(selected);
        Assert.AreEqual("ogagi", selected.EngineId);
        Assert.AreEqual("ogagi-active", selected.ActiveModel);
    }

    [TestMethod]
    public void LocalEngineSelection_UsesActiveOllamaWhenOgagiHasNoModel()
    {
        var observedAt = DateTimeOffset.Now;
        var ollama = new AiCoreMonitor.Core.OllamaSnapshot(observedAt, 2, 1, 2_000,
            "ollama-active", []);
        var ogagi = new AiCoreMonitor.Core.OgagiSnapshot(observedAt, "online", null, null, []);

        var selected = LocalEngineTelemetryProvider.Select(ollama, ogagi);

        Assert.IsNotNull(selected);
        Assert.AreEqual("ollama", selected.EngineId);
        Assert.AreEqual("ollama-active", selected.ActiveModel);
    }

    [TestMethod]
    [DataRow(999L, "999")]
    [DataRow(1_250L, "1.3K")]
    [DataRow(2_500_000L, "2.5M")]
    public void CompactFormatter_ProducesReadableValues(long input, string expected) =>
        Assert.AreEqual(expected, MainViewModel.Compact(input));

    [TestMethod]
    [DataRow("qwen3:14b", "qwen3:14b")]
    [DataRow("hf.co/library/qwen3:latest", "qwen3")]
    [DataRow("registry.example/models/very-long-model-name-for-testing:8b", "very-long-model-name...")]
    public void ShortModelName_RemovesRegistryNoise(string input, string expected) =>
        Assert.AreEqual(expected, MainViewModel.ShortModelName(input));
}
