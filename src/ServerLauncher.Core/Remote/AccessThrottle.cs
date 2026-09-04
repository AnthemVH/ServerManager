using System.Collections.Concurrent;

namespace ServerLauncher.Core.Remote;

/// <summary>
/// Slows down repeated failed attempts from one address.
/// </summary>
/// <remarks>
/// Once this is reachable from the internet it will be found and probed, so a wrong token
/// has to cost something. Counting per address rather than globally matters: a global
/// counter would let anyone on the internet lock the owner out simply by failing enough
/// times, turning the protection into the attack.
/// </remarks>
public sealed class AccessThrottle
{
    /// <summary>Failures from one address before it is refused.</summary>
    public const int MaxFailures = 10;

    /// <summary>How long an address stays blocked once it trips the limit.</summary>
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(10);

    /// <summary>Failures older than this stop counting.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);

    private sealed class Record
    {
        public readonly List<DateTimeOffset> Failures = new();
        public DateTimeOffset? BlockedUntil;
    }

    private readonly ConcurrentDictionary<string, Record> _records = new();
    private readonly Func<DateTimeOffset> _now;

    public AccessThrottle(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    /// <summary>True when this address is currently refused.</summary>
    public bool IsBlocked(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        if (!_records.TryGetValue(address, out var record))
        {
            return false;
        }

        lock (record)
        {
            if (record.BlockedUntil is not { } until)
            {
                return false;
            }

            if (_now() < until)
            {
                return true;
            }

            // Served its time; start again from clean rather than staying half-blocked.
            record.BlockedUntil = null;
            record.Failures.Clear();
            return false;
        }
    }

    /// <summary>How much longer this address stays blocked, for the response.</summary>
    public TimeSpan RemainingBlock(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !_records.TryGetValue(address, out var record))
        {
            return TimeSpan.Zero;
        }

        lock (record)
        {
            if (record.BlockedUntil is not { } until)
            {
                return TimeSpan.Zero;
            }

            var remaining = until - _now();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Records a rejected attempt, blocking the address once it has had enough.</summary>
    public void RecordFailure(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var record = _records.GetOrAdd(address, _ => new Record());

        lock (record)
        {
            var cutoff = _now() - FailureWindow;
            record.Failures.RemoveAll(at => at < cutoff);
            record.Failures.Add(_now());

            if (record.Failures.Count >= MaxFailures)
            {
                record.BlockedUntil = _now().Add(BlockDuration);
            }
        }
    }

    /// <summary>Clears an address's history after it authenticates successfully.</summary>
    public void RecordSuccess(string? address)
    {
        if (!string.IsNullOrWhiteSpace(address))
        {
            _records.TryRemove(address, out _);
        }
    }

    /// <summary>Addresses currently blocked, for display and for the audit log.</summary>
    public IReadOnlyList<string> BlockedAddresses() =>
        _records.Where(pair => IsBlocked(pair.Key)).Select(pair => pair.Key).ToList();
}
