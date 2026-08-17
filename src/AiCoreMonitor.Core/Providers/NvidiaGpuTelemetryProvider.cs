using System.Diagnostics;
using System.Globalization;
using System.IO;
using AiCoreMonitor.Core;

namespace AiCoreMonitor.Providers;

public sealed class NvidiaGpuTelemetryProvider : ITelemetryProvider<GpuSnapshot>
{
    private const string Query = "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw";
    public string Name => "NVIDIA GPU";

    public async Task<GpuSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("nvidia-smi")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(Query);
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start nvidia-smi.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "nvidia-smi failed." : error.Trim());

        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidDataException("No NVIDIA GPU was reported.");
        return Parse(line);
    }

    internal static GpuSnapshot Parse(string line)
    {
        var fields = line.Split(',').Select(value => value.Trim()).ToArray();
        if (fields.Length < 6) throw new InvalidDataException($"Unexpected nvidia-smi output: {line}");
        return new GpuSnapshot(DateTimeOffset.Now, fields[0], Metric(fields[1]), Metric(fields[2]),
            Metric(fields[3]), Metric(fields[4]), Metric(fields[5]));
    }

    private static double Metric(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
}
