using System.Runtime.InteropServices;
using AiCoreMonitor.Core;
using Microsoft.Win32;

namespace AiCoreMonitor.Providers;

public sealed class CpuTelemetryProvider : ITelemetryProvider<CpuSnapshot>
{
    private readonly object _gate = new();
    private readonly string _name = ReadProcessorName();
    private readonly double _nominalClockGhz = ReadNominalClockGhz();
    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private double _lastUtilization;

    public CpuTelemetryProvider()
    {
        if (TryReadSystemTimes(out var idle, out var kernel, out var user))
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
        }
    }

    public string Name => "CPU";

    public Task<CpuSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryReadSystemTimes(out var idle, out var kernel, out var user))
            throw new InvalidOperationException("Windows CPU counters are unavailable.");

        double utilization;
        lock (_gate)
        {
            utilization = CalculateUtilization(
                _previousIdle, _previousKernel, _previousUser, idle, kernel, user, _lastUtilization);
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            _lastUtilization = utilization;
        }

        return Task.FromResult(new CpuSnapshot(DateTimeOffset.Now, _name, utilization,
            Environment.ProcessorCount, _nominalClockGhz));
    }

    internal static double CalculateUtilization(ulong previousIdle, ulong previousKernel,
        ulong previousUser, ulong idle, ulong kernel, ulong user, double fallback = 0)
    {
        if (idle < previousIdle || kernel < previousKernel || user < previousUser)
            return Math.Clamp(fallback, 0, 100);
        var idleDelta = idle - previousIdle;
        var totalDelta = kernel - previousKernel + user - previousUser;
        if (totalDelta == 0) return Math.Clamp(fallback, 0, 100);
        return Math.Clamp((1d - (double)idleDelta / totalDelta) * 100, 0, 100);
    }

    private static bool TryReadSystemTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        if (!GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            idle = kernel = user = 0;
            return false;
        }
        idle = idleTime.Value;
        kernel = kernelTime.Value;
        user = userTime.Value;
        return true;
    }

    private static string ReadProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Convert.ToString(key?.GetValue("ProcessorNameString"))?.Trim() is { Length: > 0 } name
                ? name : "WINDOWS PROCESSOR";
        }
        catch
        {
            return "WINDOWS PROCESSOR";
        }
    }

    private static double ReadNominalClockGhz()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return Convert.ToDouble(key?.GetValue("~MHz") ?? 0) / 1000;
        }
        catch
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out NativeFileTime idleTime,
        out NativeFileTime kernelTime, out NativeFileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeFileTime
    {
        private readonly uint _low;
        private readonly uint _high;
        public ulong Value => ((ulong)_high << 32) | _low;
    }
}
