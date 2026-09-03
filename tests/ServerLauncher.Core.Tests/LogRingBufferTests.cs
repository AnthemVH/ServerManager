using FluentAssertions;
using ServerLauncher.Core.Logging;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Tests;

public class LogRingBufferTests
{
    [Fact]
    public void HoldsLinesInOrder_WhileBelowCapacity()
    {
        var buffer = new LogRingBuffer(10);

        buffer.Add(LogLine.Output("first"));
        buffer.Add(LogLine.Output("second"));

        buffer.Snapshot().Select(l => l.Text).Should().Equal("first", "second");
        buffer.Count.Should().Be(2);
    }

    [Fact]
    public void DropsOldestLines_OnceCapacityIsReached()
    {
        // A server left running for weeks must not grow the console buffer without bound.
        var buffer = new LogRingBuffer(3);

        for (var i = 1; i <= 5; i++)
        {
            buffer.Add(LogLine.Output($"line {i}"));
        }

        buffer.Count.Should().Be(3);
        buffer.Snapshot().Select(l => l.Text).Should().Equal("line 3", "line 4", "line 5");
    }

    [Fact]
    public void TracksTotalWritten_IncludingDroppedLines()
    {
        var buffer = new LogRingBuffer(2);

        for (var i = 0; i < 10; i++)
        {
            buffer.Add(LogLine.Output($"line {i}"));
        }

        buffer.TotalWritten.Should().Be(10);
        buffer.Count.Should().Be(2);
    }

    [Fact]
    public void WrapsRepeatedly_WithoutLosingOrdering()
    {
        var buffer = new LogRingBuffer(4);

        for (var i = 1; i <= 100; i++)
        {
            buffer.Add(LogLine.Output($"line {i}"));
        }

        buffer.Snapshot().Select(l => l.Text).Should().Equal("line 97", "line 98", "line 99", "line 100");
    }

    [Fact]
    public void Clear_EmptiesTheBuffer()
    {
        var buffer = new LogRingBuffer(4);
        buffer.AddRange(new[] { LogLine.Output("a"), LogLine.Output("b") });

        buffer.Clear();

        buffer.Count.Should().Be(0);
        buffer.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void PreservesStreamTagging()
    {
        var buffer = new LogRingBuffer(4);

        buffer.Add(LogLine.Output("out"));
        buffer.Add(LogLine.Error("err"));
        buffer.Add(LogLine.Launcher("note"));

        var snapshot = buffer.Snapshot();
        snapshot[0].Stream.Should().Be(LogStream.StandardOutput);
        snapshot[1].Stream.Should().Be(LogStream.StandardError);
        snapshot[2].Stream.Should().Be(LogStream.Launcher);
    }

    [Fact]
    public void RejectsNonPositiveCapacity()
    {
        var act = () => new LogRingBuffer(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task ConcurrentWriters_DoNotCorruptTheBuffer()
    {
        // stdout and stderr arrive on separate threads, so the buffer must be safe.
        var buffer = new LogRingBuffer(500);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                buffer.Add(LogLine.Output($"w{writer}-{i}"));
            }
        })));

        buffer.TotalWritten.Should().Be(4000);
        buffer.Count.Should().Be(500);
        buffer.Snapshot().Should().HaveCount(500).And.OnlyContain(l => l.Text.Length > 0);
    }
}
