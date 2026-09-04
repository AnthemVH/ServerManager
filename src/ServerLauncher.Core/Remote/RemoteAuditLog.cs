using System.Globalization;
using System.Text;
using ServerLauncher.Core.Storage;

namespace ServerLauncher.Core.Remote;

/// <summary>One recorded remote action.</summary>
/// <param name="Timestamp">When it happened.</param>
/// <param name="DeviceName">Which paired device asked.</param>
/// <param name="Action">What it asked for.</param>
/// <param name="Target">Which server, when the action names one.</param>
public readonly record struct AuditEntry(
    DateTimeOffset Timestamp,
    string DeviceName,
    string Action,
    string? Target)
{
    public string Format() =>
        $"{Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} "
        + $"[{DeviceName}] {Action}"
        + (string.IsNullOrEmpty(Target) ? string.Empty : $" -> {Target}");
}

/// <summary>
/// Append-only record of what phones did.
/// </summary>
/// <remarks>
/// Anything a remote device changes should be visible from the desk afterwards. Without
/// this, a server restarting on its own is indistinguishable from a crash, and a token
/// being misused would leave no trace at all.
/// </remarks>
public sealed class RemoteAuditLog
{
    private const int MemoryEntries = 200;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly Queue<AuditEntry> _recent = new();

    public RemoteAuditLog(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.LogRoot, "remote-audit.log");

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    /// <summary>Raised for each entry, so the UI can surface it live.</summary>
    public event Action<AuditEntry>? Recorded;

    public void Record(string deviceName, string action, string? target = null)
    {
        var entry = new AuditEntry(DateTimeOffset.Now, deviceName, action, target);

        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > MemoryEntries)
            {
                _recent.Dequeue();
            }

            try
            {
                File.AppendAllText(_path, entry.Format() + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unwritable audit file must not stop the app working.
            }
        }

        Recorded?.Invoke(entry);
    }

    public IReadOnlyList<AuditEntry> Recent()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }
}
