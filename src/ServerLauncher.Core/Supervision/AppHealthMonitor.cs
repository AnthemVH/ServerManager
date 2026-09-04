using System.Diagnostics;
using System.Runtime.Versioning;

namespace ServerLauncher.Core.Supervision;

/// <summary>One reading of the launcher's own resource use.</summary>
/// <param name="Timestamp">When the sample was taken.</param>
/// <param name="CpuPercent">CPU use across all cores, 0-100.</param>
/// <param name="WorkingSetBytes">Physical memory held by the launcher process.</param>
/// <param name="ManagedMemoryBytes">Bytes the .NET heap believes are allocated.</param>
/// <param name="ThreadCount">Threads in the launcher process.</param>
/// <param name="HandleCount">Open OS handles.</param>
/// <param name="Uptime">How long the launcher has been running.</param>
public readonly record struct AppHealthSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    long WorkingSetBytes,
    long ManagedMemoryBytes,
    int ThreadCount,
    int HandleCount,
    TimeSpan Uptime)
{
    public double WorkingSetMegabytes => WorkingSetBytes / 1024d / 1024d;

    public double ManagedMemoryMegabytes => ManagedMemoryBytes / 1024d / 1024d;
}

/// <summary>
/// Samples the launcher's own resource use.
///
/// A supervisor that quietly leaks memory or handles takes its servers down with it when
/// it eventually falls over, and on an unattended box nobody is watching Task Manager.
/// Handle count is included deliberately: this app opens job objects, process handles and
/// log files continuously, so a handle count that climbs without bound is the earliest
/// visible sign of a leak.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AppHealthMonitor
{
    private readonly ResourceSampler _sampler = new();
    private readonly int _processId = Environment.ProcessId;
    private readonly DateTimeOffset _startedAt;

    public AppHealthMonitor()
    {
        DateTimeOffset started;
        try
        {
            using var process = Process.GetCurrentProcess();
            started = process.StartTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            started = DateTimeOffset.Now;
        }

        _startedAt = started;
    }

    public DateTimeOffset StartedAt => _startedAt;

    public AppHealthSample Last { get; private set; }

    /// <summary>
    /// Takes a reading. The first call establishes the CPU baseline and reports 0%,
    /// since a rate needs two points.
    /// </summary>
    public AppHealthSample Sample()
    {
        var resources = _sampler.Sample(new[] { _processId });

        var threads = 0;
        var handles = 0;

        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            threads = process.Threads.Count;
            handles = process.HandleCount;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Diagnostics only; never let sampling disturb the app.
        }

        var sample = new AppHealthSample(
            resources.Timestamp,
            resources.CpuPercent,
            resources.WorkingSetBytes,
            GC.GetTotalMemory(forceFullCollection: false),
            threads,
            handles,
            DateTimeOffset.Now - _startedAt);

        Last = sample;
        return sample;
    }
}
