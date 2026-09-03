using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Logging;

/// <summary>
/// Fixed-capacity, thread-safe buffer of recent console lines. A server left running
/// for weeks would otherwise grow the in-memory log without bound, so the oldest
/// lines are dropped once capacity is reached. Full history lives in the rolling
/// log files instead.
/// </summary>
public sealed class LogRingBuffer
{
    private readonly object _gate = new();
    private readonly LogLine[] _items;
    private int _start;
    private int _count;

    public LogRingBuffer(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        _items = new LogLine[capacity];
    }

    public int Capacity => _items.Length;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>Total lines ever added, including those since dropped.</summary>
    public long TotalWritten { get; private set; }

    public void Add(LogLine line)
    {
        lock (_gate)
        {
            TotalWritten++;

            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = line;
                _count++;
                return;
            }

            // Full: overwrite the oldest entry and advance the window.
            _items[_start] = line;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void AddRange(IEnumerable<LogLine> lines)
    {
        foreach (var line in lines)
        {
            Add(line);
        }
    }

    /// <summary>Returns the buffered lines, oldest first.</summary>
    public IReadOnlyList<LogLine> Snapshot()
    {
        lock (_gate)
        {
            var result = new LogLine[_count];
            for (var i = 0; i < _count; i++)
            {
                result[i] = _items[(_start + i) % _items.Length];
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_items);
            _start = 0;
            _count = 0;
        }
    }
}
