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
    public async Task CodexProvider_UsesNewestEventAcrossActiveSessions()
    {
        const string older = """
            {"timestamp":"2026-08-16T12:00:00+00:00","type":"event_msg","payload":{"type":"token_count","info":{},"rate_limits":{"plan_type":"plus","primary":{"window_minutes":300,"used_percent":70,"resets_at":1786900000}}}}
            """;
        const string newer = """
            {"timestamp":"2026-08-16T12:01:00+00:00","type":"event_msg","payload":{"type":"token_count","info":{},"rate_limits":{"plan_type":"plus","primary":{"window_minutes":300,"used_percent":5,"resets_at":1786900000}}}}
            """;
        await File.WriteAllTextAsync(Path.Combine(_temporaryRoot!, "older.jsonl"), older);
        await File.WriteAllTextAsync(Path.Combine(_temporaryRoot!, "newer.jsonl"), newer);

        var snapshot = await new CodexTelemetryProvider(_temporaryRoot).CollectAsync(CancellationToken.None);

        Assert.AreEqual(5d, snapshot.UsedPercent);
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
    public void CpuUtilization_ExcludesIdleKernelTime()
    {
        var utilization = CpuTelemetryProvider.CalculateUtilization(
            new CpuTelemetryProvider.SystemTimes(100, 200, 300),
            new CpuTelemetryProvider.SystemTimes(130, 260, 360));

        Assert.AreEqual(75d, utilization);
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
