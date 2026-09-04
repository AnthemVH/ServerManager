using System.Diagnostics;
using System.Runtime.Versioning;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Processes;

/// <summary>
/// One running server: the launched process, the job object holding its whole tree,
/// and the plumbing that streams its console output back to the supervisor.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ServerProcess : IDisposable
{
    private readonly Process _process;
    private readonly JobObject _job;
    private readonly TaskCompletionSource<int> _exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private ServerProcess(Process process, JobObject job)
    {
        _process = process;
        _job = job;
    }

    /// <summary>Raised for every line captured from stdout or stderr, on a background thread.</summary>
    public event Action<LogLine>? LineReceived;

    /// <summary>Raised once the process tree has exited, carrying the exit code.</summary>
    public event Action<int>? Exited;

    public int ProcessId { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    public Task<int> ExitTask => _exitSource.Task;

    /// <summary>
    /// Process IDs in the job other than the script we launched.
    /// </summary>
    /// <remarks>
    /// The root is excluded deliberately. When its Exited event fires the process is
    /// often still listed in the job for a moment, so counting it would make every
    /// ordinary crash look like a launcher that left work behind.
    /// </remarks>
    public IReadOnlyList<int> GetSurvivingProcessIds() =>
        GetTreeProcessIds().Where(pid => pid != ProcessId).ToList();

    /// <summary>
    /// True when something the script started is still alive, even though the script
    /// itself has exited. Launcher-style scripts start the real server and then return,
    /// so the root exiting does not mean the server has stopped.
    /// </summary>
    public bool HasLiveProcesses => GetSurvivingProcessIds().Count > 0;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static ServerProcess Start(ServerDefinition definition, AppSettings settings)
    {
        var startInfo = ScriptLauncher.BuildStartInfo(definition, settings);

        var job = new JobObject();
        Process? process = null;

        try
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start '{definition.Name}'.");
            }

            // Assign before the script has time to spawn the real server executable,
            // so every descendant lands in the job and dies with it.
            job.Assign(process);

            var wrapper = new ServerProcess(process, job)
            {
                ProcessId = process.Id,
                StartedAt = DateTimeOffset.Now
            };

            wrapper.Attach();
            return wrapper;
        }
        catch
        {
            process?.Dispose();
            job.Dispose();
            throw;
        }
    }

    private void Attach()
    {
        _process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                LineReceived?.Invoke(LogLine.Output(e.Data));
            }
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                LineReceived?.Invoke(LogLine.Error(e.Data));
            }
        };

        _process.Exited += (_, _) =>
        {
            // WaitForExit with no timeout flushes the async output readers so the last
            // lines a crashing server printed are not lost. It is bounded here because a
            // launcher script's child inherits our stdout pipe and holds it open: an
            // unbounded wait would then block this handler for as long as that child runs.
            try
            {
                var drain = Task.Run(() =>
                {
                    try
                    {
                        _process.WaitForExit();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or SystemException)
                    {
                        // Nothing further to drain.
                    }
                });

                drain.Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception ex) when (ex is InvalidOperationException or AggregateException)
            {
                // Process already cleaned up; nothing further to drain.
            }

            var exitCode = TryGetExitCode();
            _exitSource.TrySetResult(exitCode);
            Exited?.Invoke(exitCode);
        };

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private int TryGetExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    /// <summary>Writes a line to the server's stdin, e.g. an in-game console command.</summary>
    public bool WriteLine(string text)
    {
        if (!IsRunning)
        {
            return false;
        }

        try
        {
            _process.StandardInput.WriteLine(text);
            _process.StandardInput.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Attempts a clean shutdown, then force-kills the tree if the server does not
    /// exit within the timeout.
    /// </summary>
    /// <returns>True if the server exited on its own; false if it had to be killed.</returns>
    public async Task<bool> StopAsync(string? stopCommand, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            // The script we launched has exited. If it left the real server behind,
            // stopping still has to take that down rather than quietly succeeding.
            if (HasLiveProcesses)
            {
                LineReceived?.Invoke(LogLine.Launcher(
                    "Launcher script already exited; terminating the processes it started."));
                Kill();
                return false;
            }

            return true;
        }

        if (!string.IsNullOrWhiteSpace(stopCommand))
        {
            LineReceived?.Invoke(LogLine.Launcher($"Sending stop command: {stopCommand}"));
            WriteLine(stopCommand);
        }
        else
        {
            // Nothing to ask politely with, so allow only a short grace period.
            timeout = TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, 5));
        }

        var completed = await Task.WhenAny(ExitTask, Task.Delay(timeout, cancellationToken))
            .ConfigureAwait(false);

        if (completed == ExitTask)
        {
            return true;
        }

        LineReceived?.Invoke(LogLine.Launcher(
            $"Server did not exit within {timeout.TotalSeconds:0}s; terminating process tree."));
        Kill();
        return false;
    }

    /// <summary>Immediately terminates the entire process tree.</summary>
    public void Kill()
    {
        try
        {
            _job.Terminate();
        }
        catch (Exception ex)
        {
            LineReceived?.Invoke(LogLine.Launcher($"Failed to terminate process tree: {ex.Message}"));
        }
    }

    /// <summary>Live process IDs in this server's tree, for resource sampling.</summary>
    public IReadOnlyList<int> GetTreeProcessIds()
    {
        try
        {
            return _job.GetProcessIds();
        }
        catch (ObjectDisposedException)
        {
            return Array.Empty<int>();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        // Disposing the job closes its last handle, which kills any survivors.
        _job.Dispose();
        _process.Dispose();
        _exitSource.TrySetResult(-1);
    }
}
