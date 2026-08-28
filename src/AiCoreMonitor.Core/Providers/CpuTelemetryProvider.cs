using System.Runtime.InteropServices;
using AiCoreMonitor.Core;

namespace AiCoreMonitor.Providers;

public sealed class CpuTelemetryProvider : ITelemetryProvider<CpuSnapshot>
{
    private readonly object _sync = new();
    private SystemTimes? _previous;

    public string Name => "CPU";

    public Task<CpuSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            throw new InvalidOperationException("Could not read system CPU times.");
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new InvalidOperationException("Could not read system memory status.");

        var current = new SystemTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
        double utilization;
        lock (_sync)
        {
            utilization = _previous is { } previous ? CalculateUtilization(previous, current) : 0;
            _previous = current;
        }

        var usedMemory = memory.TotalPhysical - memory.AvailablePhysical;
        return Task.FromResult(new CpuSnapshot(DateTimeOffset.Now, Environment.ProcessorCount, utilization,
            usedMemory, memory.TotalPhysical));
    }

    internal static double CalculateUtilization(SystemTimes previous, SystemTimes current)
    {
        var total = (current.Kernel - previous.Kernel) + (current.User - previous.User);
        var idle = current.Idle - previous.Idle;
        return total <= 0 ? 0 : Math.Clamp(100d * (total - idle) / total, 0, 100);
    }

    private static ulong ToUInt64(FileTime value) => ((ulong)value.High << 32) | value.Low;

    internal readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
