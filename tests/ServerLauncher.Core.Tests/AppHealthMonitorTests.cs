using FluentAssertions;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Covers the launcher's monitoring of its own resource use. The supervisor falling over
/// takes every server with it, so these figures need to be real rather than plausible.
/// </summary>
public class AppHealthMonitorTests
{
    [Fact]
    public void Sample_ReportsTheCurrentProcess()
    {
        var monitor = new AppHealthMonitor();

        var sample = monitor.Sample();

        sample.WorkingSetBytes.Should().BeGreaterThan(0, "the process obviously occupies memory");
        sample.ManagedMemoryBytes.Should().BeGreaterThan(0);
        sample.ThreadCount.Should().BeGreaterThan(0);
        sample.HandleCount.Should().BeGreaterThan(0, "handle count is the earliest leak signal");
    }

    [Fact]
    public void FirstSample_ReportsZeroCpuRatherThanGuessing()
    {
        // CPU is a rate, so the first reading only establishes a baseline. Reporting a
        // made-up number would be worse than reporting nothing.
        var monitor = new AppHealthMonitor();

        monitor.Sample().CpuPercent.Should().Be(0);
    }

    [Fact]
    public async Task SecondSample_ProducesAUsableCpuReading()
    {
        var monitor = new AppHealthMonitor();
        monitor.Sample();

        await Task.Delay(400);
        var sample = monitor.Sample();

        sample.CpuPercent.Should().BeGreaterThanOrEqualTo(0);
        sample.CpuPercent.Should().BeLessThanOrEqualTo(100,
            "the figure is normalised against core count, like Task Manager");
    }

    [Fact]
    public void Last_ExposesTheMostRecentSample()
    {
        var monitor = new AppHealthMonitor();

        var returned = monitor.Sample();

        monitor.Last.Should().Be(returned);
    }

    [Fact]
    public async Task Uptime_GrowsBetweenSamples()
    {
        var monitor = new AppHealthMonitor();
        var first = monitor.Sample();

        await Task.Delay(1100);
        var second = monitor.Sample();

        second.Uptime.Should().BeGreaterThan(first.Uptime);
    }

    [Fact]
    public void StartedAt_IsInThePast()
    {
        var monitor = new AppHealthMonitor();

        monitor.StartedAt.Should().BeBefore(DateTimeOffset.Now.AddSeconds(1));
        monitor.Sample().Uptime.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void MegabyteHelpers_ConvertFromBytes()
    {
        var sample = new AppHealthSample(
            DateTimeOffset.Now, 12.5, 1024 * 1024 * 8, 1024 * 1024 * 2, 20, 300, TimeSpan.FromMinutes(5));

        sample.WorkingSetMegabytes.Should().Be(8);
        sample.ManagedMemoryMegabytes.Should().Be(2);
    }
}
