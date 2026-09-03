using System.Diagnostics;
using System.Runtime.Versioning;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Supervision;

/// <summary>
/// Samples CPU and memory across a server's whole process tree.
///
/// Deliberately avoids <see cref="PerformanceCounter"/>, which is slow to initialise
/// and breaks on non-English category names. Instead it diffs cumulative processor
/// time between polls, which is cheap and locale-independent.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ResourceSampler
{
    private readonly int _coreCount = Math.Max(1, Environment.ProcessorCount);
    private TimeSpan _previousCpuTotal;
    private DateTimeOffset _previousSampleAt;
    private bool _primed;

    /// <summary>
    /// Takes a reading. The first call establishes a baseline and reports 0% CPU,
    /// since a rate needs two points to compute.
    /// </summary>
    public ResourceSample Sample(IReadOnlyList<int> processIds)
    {
        var now = DateTimeOffset.Now;
        var cpuTotal = TimeSpan.Zero;
        long workingSet = 0;
        var liveCount = 0;

        foreach (var pid in processIds)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                cpuTotal += process.TotalProcessorTime;
                workingSet += process.WorkingSet64;
                liveCount++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Process exited between enumeration and inspection; skip it.
            }
        }

        double cpuPercent = 0;

        if (_primed)
        {
            var wallElapsed = (now - _previousSampleAt).TotalMilliseconds;
            var cpuElapsed = (cpuTotal - _previousCpuTotal).TotalMilliseconds;

            if (wallElapsed > 0 && cpuElapsed >= 0)
            {
                // Normalise against core count so the figure matches Task Manager.
                cpuPercent = Math.Clamp(cpuElapsed / (wallElapsed * _coreCount) * 100d, 0d, 100d);
            }
        }

        _previousCpuTotal = cpuTotal;
        _previousSampleAt = now;
        _primed = true;

        return new ResourceSample(now, cpuPercent, workingSet, liveCount);
    }

    /// <summary>Clears the baseline, so a restarted server does not inherit stale CPU deltas.</summary>
    public void Reset()
    {
        _primed = false;
        _previousCpuTotal = TimeSpan.Zero;
    }
}
