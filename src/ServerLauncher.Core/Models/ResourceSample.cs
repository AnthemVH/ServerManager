namespace ServerLauncher.Core.Models;

/// <summary>One point-in-time resource reading for a server's whole process tree.</summary>
/// <param name="Timestamp">When the sample was taken.</param>
/// <param name="CpuPercent">CPU use across all cores, 0-100.</param>
/// <param name="WorkingSetBytes">Summed private working set of every process in the tree.</param>
/// <param name="ProcessCount">Number of live processes in the tree.</param>
public readonly record struct ResourceSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    long WorkingSetBytes,
    int ProcessCount)
{
    public double WorkingSetMegabytes => WorkingSetBytes / 1024d / 1024d;
}
