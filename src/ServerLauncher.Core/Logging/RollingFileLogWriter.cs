using System.Text;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Logging;

/// <summary>
/// Appends console output to a per-server, per-day log file so history survives
/// beyond the in-memory ring buffer. Writes are batched and serialised on a single
/// background worker to keep them off the capture threads.
/// </summary>
public sealed class RollingFileLogWriter : IDisposable
{
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly object _gate = new();
    private readonly Queue<LogLine> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private DateOnly _currentDay;

    public RollingFileLogWriter(string directory, int retentionDays)
    {
        _directory = directory;
        _retentionDays = Math.Max(1, retentionDays);
        Directory.CreateDirectory(_directory);
        _currentDay = DateOnly.FromDateTime(DateTime.Now);
        PruneOldFiles();

        _worker = Task.Run(DrainAsync);
    }

    public string CurrentFilePath => Path.Combine(_directory, $"{_currentDay:yyyy-MM-dd}.log");

    public void Write(LogLine line)
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            _pending.Enqueue(line);
        }

        _signal.Release();
    }

    private async Task DrainAsync()
    {
        var batch = new List<LogLine>(256);

        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            batch.Clear();
            lock (_gate)
            {
                while (_pending.Count > 0 && batch.Count < 1000)
                {
                    batch.Add(_pending.Dequeue());
                }
            }

            if (batch.Count > 0)
            {
                FlushBatch(batch);
            }
        }

        // Drain whatever is left so the tail of a shutdown is not lost.
        batch.Clear();
        lock (_gate)
        {
            while (_pending.Count > 0)
            {
                batch.Add(_pending.Dequeue());
            }
        }

        if (batch.Count > 0)
        {
            FlushBatch(batch);
        }
    }

    private void FlushBatch(List<LogLine> batch)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today != _currentDay)
        {
            _currentDay = today;
            PruneOldFiles();
        }

        var builder = new StringBuilder();
        foreach (var line in batch)
        {
            builder.AppendLine(line.ToLogFileLine());
        }

        try
        {
            File.AppendAllText(CurrentFilePath, builder.ToString(), Encoding.UTF8);
        }
        catch (IOException)
        {
            // A locked or unavailable log file must never take the server down.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (DateTime.TryParse(name, out var date) && date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Retention is best-effort.
        }
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
        {
            return;
        }

        _shutdown.Cancel();
        _signal.Release();

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }
}
