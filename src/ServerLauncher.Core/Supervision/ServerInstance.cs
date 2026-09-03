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

    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LogRingBuffer _console;
    private readonly RollingFileLogWriter _logWriter;
    private readonly ResourceSampler _sampler = new();
    private readonly Queue<ResourceSample> _resourceHistory = new();
    private readonly object _historyGate = new();

    private ServerProcess? _process;
    private CancellationTokenSource? _pendingRestart;
    private int _consecutiveFailures;
    private bool _operatorStop;
    private bool _disposed;

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

            if (_process is not null && _process.IsRunning)
            {
                return;
            }

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
            if (process is null || !process.IsRunning)
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
            return false;
        }

        Append(LogLine.Launcher($"> {command}"));
        return process.WriteLine(command);
    }

    /// <summary>Takes a resource reading. Called on the manager's shared timer.</summary>
    public void Poll()
    {
        var process = _process;
        if (process is null || !process.IsRunning || State != ServerState.Running)
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
        var uptime = DateTimeOffset.Now - process.StartedAt;
        var operatorStop = _operatorStop;

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
