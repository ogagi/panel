using System.Diagnostics;
using System.Globalization;
using System.IO;
using AiCoreMonitor.Core;

namespace AiCoreMonitor.Providers;

public sealed class NvidiaGpuTelemetryProvider : ITelemetryProvider<GpuSnapshot>
{
    private const string GpuQuery = "--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw";
    private const string ProcessQuery = "--query-compute-apps=pid,process_name,used_gpu_memory";
    public string Name => "NVIDIA GPU";

    public async Task<GpuSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var gpuTask = RunQueryAsync(GpuQuery, cancellationToken);
        var processTask = CollectProcessesAsync(cancellationToken);
        var output = await gpuTask.ConfigureAwait(false);
        var processes = await processTask.ConfigureAwait(false);

        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? throw new InvalidDataException("No NVIDIA GPU was reported.");
        return Parse(line) with { Processes = processes };
    }

    private static async Task<string> RunQueryAsync(string query, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("nvidia-smi")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(query);
        startInfo.ArgumentList.Add("--format=csv,noheader,nounits");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start nvidia-smi.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "nvidia-smi failed." : error.Trim());
        return output;
    }

    private static async Task<IReadOnlyList<GpuProcess>> CollectProcessesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return ParseProcesses(await RunQueryAsync(ProcessQuery, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    internal static GpuSnapshot Parse(string line)
    {
        var fields = line.Split(',').Select(value => value.Trim()).ToArray();
        if (fields.Length < 6) throw new InvalidDataException($"Unexpected nvidia-smi output: {line}");
        return new GpuSnapshot(DateTimeOffset.Now, fields[0], Metric(fields[1]), Metric(fields[2]),
            Metric(fields[3]), Metric(fields[4]), Metric(fields[5]), []);
    }

    internal static IReadOnlyList<GpuProcess> ParseProcesses(string output)
    {
        var processes = new Dictionary<int, GpuProcess>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',', 3, StringSplitOptions.TrimEntries);
            if (fields.Length < 3 || !int.TryParse(fields[0], NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var processId) || fields[1].StartsWith('['))
                continue;

            var name = Path.GetFileName(fields[1]);
            if (string.IsNullOrWhiteSpace(name)) continue;
            double? memory = double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture,
                out var memoryMiB) ? memoryMiB : null;
            processes[processId] = new GpuProcess(processId, name, memory);
        }

        return processes.Values
            .OrderByDescending(process => process.MemoryUsedMiB ?? -1)
            .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static double Metric(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;
}
