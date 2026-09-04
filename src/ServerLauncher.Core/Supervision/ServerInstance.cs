using System.Runtime.Versioning;
using ServerLauncher.Core.Logging;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Processes;
using ServerLauncher.Core.Storage;

namespace ServerLauncher.Core.Supervision;

/// <summary>
/// Supervises one server: owns its process, console buffer, restart policy and
/// resource history, and exposes the state machine the UI binds to.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerInstance : IDisposable
{
    private const int ResourceHistoryLength = 60;

    /// <summary>
    /// How long to wait before deciding a script that exited was a launcher. Long enough
    /// for Windows to clear the console host it leaves in the job, short enough that
    /// crash detection stays prompt.
    /// </summary>
    private static readonly TimeSpan LauncherSettleDelay = TimeSpan.FromSeconds(2);

    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogRingBuffer _console;
    private readonly RollingFileLogWriter _logWriter;
    private readonly ResourceSampler _sampler = new();
    private readonly Queue<ResourceSample> _resourceHistory = new();
    private readonly object _historyGate = new();
    private readonly object _detachGate = new();

    private ServerProcess? _process;
    private CancellationTokenSource? _pendingRestart;
    private int _consecutiveFailures;
    private bool _operatorStop;
    private bool _disposed;

    /// <summary>
    /// Set when the launched script exited but left running processes behind. From then
    /// on the job object, not the script, tells us whether the server is up.
    /// </summary>
    private bool _launcherDetached;

    /// <summary>
    /// Set once a run has reported its outcome, so a stop racing the launcher settle
    /// window cannot report it a second time.
    /// </summary>
    private int _runCompleted;

    /// <param name="definition">The server to supervise.</param>
    /// <param name="settings">Application settings governing buffers and retention.</param>
    /// <param name="logDirectory">
    /// Where to write rolling log files. Defaults to this server's folder under
    /// %LOCALAPPDATA%; overridden by tests so they do not write into real app data.
    /// </param>
    public ServerInstance(ServerDefinition definition, AppSettings settings, string? logDirectory = null)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        _console = new LogRingBuffer(Math.Max(100, settings.ConsoleBufferLines));
        _logWriter = new RollingFileLogWriter(
            logDirectory ?? AppPaths.LogDirectoryFor(definition.Id), settings.LogRetentionDays);
    }

    public ServerDefinition Definition { get; private set; }

    public Guid Id => Definition.Id;

    public ServerState State { get; private set; } = ServerState.Stopped;

    public DateTimeOffset? StartedAt { get; private set; }

    public ResourceSample LastSample { get; private set; }

    /// <summary>How long the server has been up, or null when it is not running.</summary>
    public TimeSpan? Uptime => StartedAt is { } started && State == ServerState.Running
        ? DateTimeOffset.Now - started
        : null;

    public event Action<ServerInstance, ServerState>? StateChanged;
    public event Action<ServerInstance, LogLine>? LineAppended;
    public event Action<ServerInstance, ResourceSample>? ResourceSampled;

    public IReadOnlyList<LogLine> ConsoleSnapshot() => _console.Snapshot();

    /// <summary>Live process IDs in this server's tree.</summary>
    public IReadOnlyList<int> TreeProcessIds() =>
        _process?.GetTreeProcessIds() ?? Array.Empty<int>();

    /// <summary>
    /// True when the script that started this server has exited and the server it
    /// launched is what we are now supervising.
    /// </summary>
    public bool IsLauncherDetached => _launcherDetached;

    public IReadOnlyList<ResourceSample> ResourceHistory()
    {
        lock (_historyGate)
        {
            return _resourceHistory.ToArray();
        }
    }

    /// <summary>Applies edited settings. A running server keeps its current process.</summary>
    public void UpdateDefinition(ServerDefinition definition)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Append(LogLine.Launcher("Configuration updated."));
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CancelPendingRestart();

            if (_process is not null && (_process.IsRunning || _process.HasLiveProcesses))
            {
                return;
            }

            _launcherDetached = false;
            Interlocked.Exchange(ref _runCompleted, 0);

            SetState(ServerState.Starting);
            Append(LogLine.Launcher($"Starting from {Definition.ScriptPath}"));

            try
            {
                var process = ServerProcess.Start(Definition, _settings);
                process.LineReceived += OnLineReceived;
                process.Exited += code => OnProcessExited(process, code);

                _process = process;
                _operatorStop = false;
                StartedAt = process.StartedAt;
                _sampler.Reset();

                SetState(ServerState.Running);
                Append(LogLine.Launcher($"Started (pid {process.ProcessId})."));
            }
            catch (Exception ex)
            {
                Append(LogLine.Launcher($"Failed to start: {ex.Message}"));
                SetState(ServerState.Failed);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ServerProcess? process;

        try
        {
            CancelPendingRestart();
            _consecutiveFailures = 0;

            process = _process;
            if (process is null || (!process.IsRunning && !process.HasLiveProcesses))
            {
                SetState(ServerState.Stopped);
                return;
            }

            // Recorded before the stop so the exit handler knows this was intentional
            // and does not treat it as a crash worth restarting.
            _operatorStop = true;
            SetState(ServerState.Stopping);
        }
        finally
        {
            _gate.Release();
        }

        var timeout = TimeSpan.FromSeconds(Math.Max(1, Definition.GracefulStopTimeoutSeconds));
        await process.StopAsync(Definition.StopCommand, timeout, cancellationToken).ConfigureAwait(false);

        if (_launcherDetached)
        {
            // No root process remains to raise Exited, so complete the teardown here.
            FinishDetachedRun(operatorInitiated: true, process);
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        Append(LogLine.Launcher("Restart requested."));
        await StopAsync(cancellationToken).ConfigureAwait(false);

        // Give the OS a moment to release ports and file locks before relaunching.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a console command to the running server via stdin.</summary>
    public bool SendCommand(string command)
    {
        var process = _process;
        if (process is null || !process.IsRunning)
        {
            if (_launcherDetached)
            {
                Append(LogLine.Launcher(
                    "Cannot send commands: this server was started by a launcher script that has "
                    + "exited, so there is no console to write to."));
            }

            return false;
        }

        Append(LogLine.Launcher($"> {command}"));
        return process.WriteLine(command);
    }

    /// <summary>Takes a resource reading. Called on the manager's shared timer.</summary>
    public void Poll()
    {
        var process = _process;
        if (process is null || State != ServerState.Running)
        {
            return;
        }

        if (_launcherDetached)
        {
            // The script is gone, so the job object is the only thing that can tell us
            // whether the server it started is still alive.
            if (!process.HasLiveProcesses)
            {
                FinishDetachedRun(operatorInitiated: false, process);
                return;
            }
        }
        else if (!process.IsRunning)
        {
            return;
        }

        var sample = _sampler.Sample(process.GetTreeProcessIds());
        LastSample = sample;

        lock (_historyGate)
        {
            _resourceHistory.Enqueue(sample);
            while (_resourceHistory.Count > ResourceHistoryLength)
            {
                _resourceHistory.Dequeue();
            }
        }

        ResourceSampled?.Invoke(this, sample);
    }

    private void OnLineReceived(LogLine line) => Append(line);

    private void OnProcessExited(ServerProcess process, int exitCode)
    {
        var operatorStop = _operatorStop;

        // A launcher script starts the real server and then exits — the Arma 3 scripts
        // work exactly this way. Treating that exit as the server's exit would dispose the
        // job object, and KILL_ON_JOB_CLOSE would kill the server that had just started.
        if (!operatorStop && process.HasLiveProcesses)
        {
            // Not decided yet: Windows leaves a console host in the job for a few hundred
            // milliseconds after an ordinary script exits, and mistaking that straggler
            // for a launched server would stop crashes ever being detected. Only survivors
            // that are still there a moment later mean a real launcher.
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(LauncherSettleDelay).ConfigureAwait(false);

                    if (process.HasLiveProcesses)
                    {
                        BeginDetachedSupervision(process, exitCode);
                    }
                    else
                    {
                        CompleteRun(process, exitCode, operatorStop);
                    }
                }
                catch (Exception ex)
                {
                    Append(LogLine.Launcher($"Failed to resolve server exit: {ex.Message}"));
                }
            });

            return;
        }

        CompleteRun(process, exitCode, operatorStop);
    }

    /// <summary>
    /// Hands supervision over to the job object after a launcher script has exited,
    /// leaving the server it started running.
    /// </summary>
    private void BeginDetachedSupervision(ServerProcess process, int exitCode)
    {
        if (Volatile.Read(ref _runCompleted) == 1)
        {
            return;
        }

        lock (_detachGate)
        {
            _launcherDetached = true;
        }

        process.LineReceived -= OnLineReceived;

        var survivors = process.GetSurvivingProcessIds().Count;
        Append(LogLine.Launcher(
            $"Launcher script exited with code {exitCode}, leaving {survivors} process(es) "
            + "running. Supervising those instead."));
        Append(LogLine.Launcher(
            "Console output and stop commands are unavailable for a launcher-started server, "
            + "because the script that owned the console has gone. Stopping still terminates "
            + "everything it launched."));
    }

    /// <summary>Finishes a run whose process actually ended, applying the restart policy.</summary>
    private void CompleteRun(ServerProcess process, int exitCode, bool operatorStop)
    {
        // Guarded so a stop racing the settle window cannot report the outcome twice.
        if (Interlocked.Exchange(ref _runCompleted, 1) == 1)
        {
            return;
        }

        var uptime = DateTimeOffset.Now - process.StartedAt;

        process.LineReceived -= OnLineReceived;
        _process = null;
        StartedAt = null;

        var decision = RestartPolicyEngine.Evaluate(
            Definition, exitCode, operatorStop, uptime, _consecutiveFailures);

        _consecutiveFailures = decision.ConsecutiveFailures;

        Append(LogLine.Launcher(decision.Reason));
        SetState(decision.ResultingState);

        process.Dispose();

        if (decision.ShouldRestart)
        {
            ScheduleRestart(decision.Delay);
        }
    }

    /// <summary>
    /// Completes a run that outlived its launcher script.
    /// </summary>
    /// <remarks>
    /// There is no exit code to read here — the script reported its own when it finished,
    /// and it says nothing about the server. A tree that empties without being asked to is
    /// treated as a crash, which is what it is from the operator's point of view, and is
    /// what makes the On crash policy work for launcher-started servers.
    /// </remarks>
    private void FinishDetachedRun(bool operatorInitiated, ServerProcess process)
    {
        lock (_detachGate)
        {
            if (!_launcherDetached)
            {
                return;
            }

            _launcherDetached = false;
        }

        if (Interlocked.Exchange(ref _runCompleted, 1) == 1)
        {
            return;
        }

        var uptime = DateTimeOffset.Now - process.StartedAt;

        _process = null;
        StartedAt = null;

        var decision = RestartPolicyEngine.Evaluate(
            Definition,
            operatorInitiated ? 0 : -1,
            operatorInitiated,
            uptime,
            _consecutiveFailures);

        _consecutiveFailures = decision.ConsecutiveFailures;

        Append(LogLine.Launcher(decision.Reason));
        SetState(decision.ResultingState);

        process.Dispose();

        if (decision.ShouldRestart)
        {
            ScheduleRestart(decision.Delay);
        }
    }

    private void ScheduleRestart(TimeSpan delay)
    {
        var cts = new CancellationTokenSource();
        _pendingRestart = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                await StartAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a manual start or stop.
            }
            catch (Exception ex)
            {
                Append(LogLine.Launcher($"Automatic restart failed: {ex.Message}"));
            }
            finally
            {
                if (ReferenceEquals(_pendingRestart, cts))
                {
                    _pendingRestart = null;
                }

                cts.Dispose();
            }
        });
    }

    private void CancelPendingRestart()
    {
        var pending = _pendingRestart;
        _pendingRestart = null;

        if (pending is null)
        {
            return;
        }

        try
        {
            pending.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Append(LogLine line)
    {
        _console.Add(line);
        _logWriter.Write(line);
        LineAppended?.Invoke(this, line);
    }

    private void SetState(ServerState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPendingRestart();
        _process?.Dispose();
        _logWriter.Dispose();
        _gate.Dispose();
    }
}
