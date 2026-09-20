using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ServerLauncher.Core.Backup;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Storage;

namespace ServerLauncher.Core.Supervision;

/// <summary>
/// Owns every supervised server, plus the shared timers that drive resource sampling
/// and time-of-day schedules. A single pair of timers serves all servers rather than
/// each instance owning its own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerManager : IDisposable
{
    private readonly ConfigurationStore _store;
    private readonly BackupService _backupService = new();
    private readonly AppHealthMonitor _appHealth = new();
    private readonly Queue<AppHealthSample> _appHealthHistory = new();
    private readonly object _appHealthGate = new();
    private readonly List<ServerInstance> _instances = new();
    private readonly object _instancesGate = new();

    // Tracks the last date each scheduled task fired, so a task runs once a day even
    // though the timer ticks twice within the minute its time matches. Keyed by task id
    // rather than by server and action, so two tasks doing the same thing at different
    // times do not suppress one another.
    private readonly ConcurrentDictionary<Guid, DateOnly> _lastFired = new();

    private const int AppHealthHistoryLength = 60;

    private Timer? _sampleTimer;
    private Timer? _scheduleTimer;
    private bool _disposed;

    public ServerManager(ConfigurationStore? store = null)
    {
        _store = store ?? new ConfigurationStore();
        AppPaths.EnsureCreated();
        Settings = _store.LoadSettings();

        // Take a reading immediately so AppHealth reports real memory, thread and handle
        // counts from the moment the manager exists rather than zeros until the first
        // timer tick, and so the first timed sample already has a CPU baseline to diff.
        _appHealth.Sample();
    }

    public AppSettings Settings { get; private set; }

    /// <summary>Raised when a server is added or removed, so the UI can resync its list.</summary>
    public event Action? ServersChanged;

    /// <summary>Raised when any server changes state.</summary>
    public event Action<ServerInstance, ServerState>? ServerStateChanged;

    /// <summary>Raised when a server appends a console line.</summary>
    public event Action<ServerInstance, LogLine>? ServerLineAppended;

    /// <summary>Raised after each resource sample.</summary>
    public event Action<ServerInstance, ResourceSample>? ServerResourceSampled;

    /// <summary>Raised when a scheduled or manual backup completes.</summary>
    public event Action<ServerInstance, BackupResult>? BackupCompleted;

    /// <summary>Raised when a server's update script starts or finishes.</summary>
    public event Action<ServerInstance, bool>? ServerUpdatingChanged;

    /// <summary>Raised after each reading of the launcher's own resource use.</summary>
    public event Action<AppHealthSample>? AppHealthSampled;

    /// <summary>Most recent reading of the launcher's own resource use.</summary>
    public AppHealthSample AppHealth => _appHealth.Last;

    /// <summary>When the launcher process started.</summary>
    public DateTimeOffset AppStartedAt => _appHealth.StartedAt;

    /// <summary>Recent launcher samples, oldest first, for the history graph.</summary>
    public IReadOnlyList<AppHealthSample> AppHealthHistory()
    {
        lock (_appHealthGate)
        {
            return _appHealthHistory.ToArray();
        }
    }

    public IReadOnlyList<ServerInstance> Instances
    {
        get
        {
            lock (_instancesGate)
            {
                return _instances.ToArray();
            }
        }
    }

    /// <summary>Loads saved servers, starts the timers, and auto-starts what is configured to.</summary>
    public async Task InitialiseAsync(CancellationToken cancellationToken = default)
    {
        var migrated = false;

        foreach (var definition in _store.LoadServers())
        {
            // Old files hold a single daily restart time and a single daily backup time.
            // Moving them into the schedule list here means everything downstream has one
            // place to look, and an existing server keeps firing at the time it always did.
            migrated |= definition.MigrateLegacySchedules();

            AttachInstance(new ServerInstance(definition, Settings));
        }

        if (migrated)
        {
            Persist();
        }

        ServersChanged?.Invoke();

        var interval = TimeSpan.FromSeconds(Math.Max(1, Settings.ResourceSampleIntervalSeconds));
        _sampleTimer = new Timer(_ => PollAll(), null, interval, interval);
        _scheduleTimer = new Timer(_ => RunSchedules(), null,
            TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));

        foreach (var instance in Instances.Where(i => i.Definition.AutoStartOnLaunch))
        {
            try
            {
                await instance.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The failure is already recorded in that server's console log.
            }
        }
    }

    public ServerInstance Add(ServerDefinition definition)
    {
        var instance = new ServerInstance(definition, Settings);
        AttachInstance(instance);
        Persist();
        ServersChanged?.Invoke();
        return instance;
    }

    public void Update(ServerDefinition definition)
    {
        var instance = Find(definition.Id);
        instance?.UpdateDefinition(definition);
        Persist();
        ServersChanged?.Invoke();
    }

    /// <summary>Removes a server. Its script and data files are never touched.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = Find(id);
        if (instance is null)
        {
            return;
        }

        await instance.StopAsync(cancellationToken).ConfigureAwait(false);

        lock (_instancesGate)
        {
            _instances.Remove(instance);
        }

        DetachInstance(instance);
        instance.Dispose();

        Persist();
        ServersChanged?.Invoke();
    }

    public ServerInstance? Find(Guid id)
    {
        lock (_instancesGate)
        {
            return _instances.FirstOrDefault(i => i.Id == id);
        }
    }

    public Task<BackupResult> RunBackupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = Find(id);
        return instance is null
            ? Task.FromResult(new BackupResult(false, null, 0, 0, "Server not found."))
            : RunBackupAsync(instance, cancellationToken);
    }

    /// <summary>
    /// Runs a server's update script. Stops and restarts the server around it if it is
    /// running — see <see cref="ServerInstance.RunUpdateAsync"/>.
    /// </summary>
    public Task<bool> RunUpdateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var instance = Find(id);
        return instance is null
            ? Task.FromResult(false)
            : instance.RunUpdateAsync(cancellationToken);
    }

    private async Task<BackupResult> RunBackupAsync(ServerInstance instance, CancellationToken cancellationToken)
    {
        var result = await _backupService.RunAsync(instance, cancellationToken).ConfigureAwait(false);
        BackupCompleted?.Invoke(instance, result);
        return result;
    }

    public void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        _store.SaveSettings(settings);
    }

    public void Persist()
    {
        lock (_instancesGate)
        {
            _store.SaveServers(_instances.Select(i => i.Definition));
        }
    }

    /// <summary>Stops every running server, used on application exit.</summary>
    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        var running = Instances.Where(i => i.State is ServerState.Running or ServerState.Starting).ToList();
        await Task.WhenAll(running.Select(i => i.StopAsync(cancellationToken))).ConfigureAwait(false);
    }

    public bool AnyRunning() =>
        Instances.Any(i => i.State is ServerState.Running or ServerState.Starting or ServerState.Stopping);

    private void AttachInstance(ServerInstance instance)
    {
        instance.StateChanged += OnStateChanged;
        instance.LineAppended += OnLineAppended;
        instance.ResourceSampled += OnResourceSampled;
        instance.UpdatingChanged += OnUpdatingChanged;

        lock (_instancesGate)
        {
            _instances.Add(instance);
        }
    }

    private void DetachInstance(ServerInstance instance)
    {
        instance.StateChanged -= OnStateChanged;
        instance.LineAppended -= OnLineAppended;
        instance.ResourceSampled -= OnResourceSampled;
        instance.UpdatingChanged -= OnUpdatingChanged;
    }

    private void OnStateChanged(ServerInstance instance, ServerState state) =>
        ServerStateChanged?.Invoke(instance, state);

    private void OnLineAppended(ServerInstance instance, LogLine line) =>
        ServerLineAppended?.Invoke(instance, line);

    private void OnUpdatingChanged(ServerInstance instance, bool updating) =>
        ServerUpdatingChanged?.Invoke(instance, updating);

    private void OnResourceSampled(ServerInstance instance, ResourceSample sample) =>
        ServerResourceSampled?.Invoke(instance, sample);

    private void PollAll()
    {
        foreach (var instance in Instances)
        {
            try
            {
                instance.Poll();
            }
            catch (Exception)
            {
                // Sampling is diagnostic; never let it disturb a running server.
            }
        }

        PollAppHealth();
    }

    /// <summary>
    /// Samples the launcher itself on the same cadence as the servers, so one timer
    /// drives everything rather than a second one ticking alongside it.
    /// </summary>
    private void PollAppHealth()
    {
        try
        {
            var sample = _appHealth.Sample();

            lock (_appHealthGate)
            {
                _appHealthHistory.Enqueue(sample);
                while (_appHealthHistory.Count > AppHealthHistoryLength)
                {
                    _appHealthHistory.Dequeue();
                }
            }

            AppHealthSampled?.Invoke(sample);
        }
        catch (Exception)
        {
            // Diagnostics must never destabilise supervision.
        }
    }

    private void RunSchedules()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        foreach (var instance in Instances)
        {
            foreach (var task in instance.Definition.Schedule)
            {
                if (!task.IsDue(now) || !ClaimForToday(task.Id, today))
                {
                    continue;
                }

                RunScheduledTask(instance, task);
            }
        }
    }

    /// <summary>
    /// Dispatches one due task. Each runs detached: a backup or an update takes minutes,
    /// and the schedule tick must not be sitting inside one when the next minute arrives.
    /// </summary>
    private void RunScheduledTask(ServerInstance instance, ScheduledTask task)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                switch (task.Action)
                {
                    case ScheduledAction.Start:
                        // Starting an already-running server is a no-op inside the
                        // instance, so this needs no state check of its own.
                        await instance.StartAsync().ConfigureAwait(false);
                        break;

                    case ScheduledAction.Stop:
                        await instance.StopAsync().ConfigureAwait(false);
                        break;

                    case ScheduledAction.Restart:
                        // A stopped server is started rather than restarted: a scheduled
                        // restart of something that crashed overnight should bring it back.
                        if (instance.State == ServerState.Running)
                        {
                            await instance.RestartAsync().ConfigureAwait(false);
                        }
                        else
                        {
                            await instance.StartAsync().ConfigureAwait(false);
                        }

                        break;

                    case ScheduledAction.RunUpdate:
                        await instance.RunUpdateAsync().ConfigureAwait(false);
                        break;

                    case ScheduledAction.Backup:
                        await RunBackupAsync(instance, CancellationToken.None).ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception)
            {
                // Recorded in that server's console log; a failed scheduled action must
                // never take the schedule timer down with it.
            }
        });
    }

    /// <summary>
    /// Marks a task as fired today, returning false if it already had been. The timer
    /// ticks every 30 seconds, so a task's minute comes round twice.
    /// </summary>
    private bool ClaimForToday(Guid taskId, DateOnly today)
    {
        if (_lastFired.TryGetValue(taskId, out var lastDate) && lastDate == today)
        {
            return false;
        }

        _lastFired[taskId] = today;
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sampleTimer?.Dispose();
        _scheduleTimer?.Dispose();

        foreach (var instance in Instances)
        {
            DetachInstance(instance);
            instance.Dispose();
        }
    }
}
