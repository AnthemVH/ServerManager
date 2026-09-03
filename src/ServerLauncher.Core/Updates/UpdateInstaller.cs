using System.Diagnostics;

namespace ServerLauncher.Core.Updates;

/// <summary>
/// Swaps the running executable for a downloaded one and restarts.
///
/// Windows will not let a running .exe be overwritten or deleted, but it will let one
/// be *renamed*. So the update moves the live executable aside, drops the new build
/// into its place, and relaunches. If the second move fails the first is undone, so a
/// failed update always leaves a working app rather than an empty folder.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Suffix for the displaced previous build, removed on the next start.</summary>
    public const string BackupSuffix = ".old";

    /// <summary>
    /// Passed to the relaunched process so it waits for this one to exit before taking
    /// the single-instance mutex.
    /// </summary>
    public const string AfterUpdateSwitch = "--after-update";

    /// <summary>
    /// Path of the running executable. Uses ProcessPath rather than the assembly
    /// location, which points at a temp extraction folder for single-file builds.
    /// </summary>
    public static string CurrentExecutablePath =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("Could not determine the running executable path.");

    /// <summary>
    /// Deletes the previous build left behind by an update. Called on startup, once the
    /// new executable is confirmed working by virtue of having started at all.
    /// </summary>
    public static void CleanUpPreviousVersion()
    {
        try
        {
            var backup = CurrentExecutablePath + BackupSuffix;
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The old build is harmless if it lingers; it will be retried next start.
        }
    }

    /// <summary>
    /// Checks the app can actually replace itself before anything is downloaded or any
    /// servers are stopped — failing after a shutdown would be far worse.
    /// </summary>
    public static bool CanSelfUpdate(out string reason)
    {
        reason = string.Empty;

        try
        {
            var directory = Path.GetDirectoryName(CurrentExecutablePath);
            if (string.IsNullOrEmpty(directory))
            {
                reason = "Could not determine the application folder.";
                return false;
            }

            // Probe for write access rather than inspecting ACLs, which is both simpler
            // and accurate under UAC virtualisation.
            var probe = Path.Combine(directory, $".update-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = "The application folder is not writable. Move Server Launcher out of "
                     + "Program Files, or run it from a folder you own.";
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Puts the downloaded build in place and relaunches it.
    /// Callers must stop supervised servers first — this does not return.
    /// </summary>
    /// <param name="downloadedExecutable">Verified executable to install.</param>
    public static void ApplyAndRestart(string downloadedExecutable) =>
        Apply(downloadedExecutable, CurrentExecutablePath, restart: true);

    /// <summary>
    /// The swap itself, with the target made explicit so the rename-and-rollback
    /// behaviour can be tested without renaming the test runner out from under itself.
    /// </summary>
    /// <param name="downloadedExecutable">Verified executable to install.</param>
    /// <param name="targetExecutable">Path to replace.</param>
    /// <param name="restart">Launch the new build afterwards.</param>
    public static void Apply(string downloadedExecutable, string targetExecutable, bool restart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadedExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetExecutable);

        if (!File.Exists(downloadedExecutable))
        {
            throw new FileNotFoundException("The downloaded update is missing.", downloadedExecutable);
        }

        var current = targetExecutable;
        var backup = current + BackupSuffix;

        // A backup from an earlier update would block the rename.
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        // Step 1: move the running executable aside. Windows permits this.
        File.Move(current, backup);

        try
        {
            // Step 2: put the new build where the old one was.
            File.Move(downloadedExecutable, current);
        }
        catch
        {
            // Undo step 1 so a failure never leaves the app missing entirely.
            try
            {
                File.Move(backup, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Both moves failed; surface the original error to the caller.
            }

            throw;
        }

        if (!restart)
        {
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = current,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(current) ?? Environment.CurrentDirectory
        };

        // Tell the new instance to wait for this process to release the single-instance
        // mutex, otherwise it would see itself as a second copy and refuse to start.
        startInfo.ArgumentList.Add(AfterUpdateSwitch);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

        Process.Start(startInfo);
    }

    /// <summary>
    /// Reads the process id to wait for from the command line, when this instance was
    /// relaunched by an update.
    /// </summary>
    public static int? GetProcessIdToWaitFor(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(AfterUpdateSwitch, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var pid))
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>Waits for the superseded instance to exit so its mutex is released.</summary>
    public static void WaitForPreviousInstance(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.WaitForExit((int)timeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Already gone, which is exactly what we were waiting for.
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
