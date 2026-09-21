namespace ServerLauncher.Core.Models;

/// <summary>
/// A user-configured server: which script to run and how to supervise it.
/// This is the unit persisted to servers.json. The script itself is never modified.
/// </summary>
public sealed class ServerDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New Server";

    /// <summary>Absolute path to the .bat, .cmd, .ps1 or .exe that starts the server.</summary>
    public string ScriptPath { get; set; } = string.Empty;

    /// <summary>
    /// Directory the script runs in. Empty means "the script's own folder", which is
    /// what most server scripts assume when they reference relative paths.
    /// </summary>
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>Extra arguments appended to the script invocation.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Environment variables applied on top of the inherited environment.</summary>
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();

    // --- Startup ---

    /// <summary>Start this server automatically when the launcher starts.</summary>
    public bool AutoStartOnLaunch { get; set; }

    // --- Shutdown ---

    /// <summary>
    /// Command written to stdin to request a clean shutdown (e.g. "stop" for Minecraft).
    /// Leave empty for servers that do not read stdin; the launcher then waits briefly
    /// and terminates the tree.
    /// </summary>
    public string StopCommand { get; set; } = string.Empty;

    /// <summary>How long to wait for a clean exit before killing the process tree.</summary>
    public int GracefulStopTimeoutSeconds { get; set; } = 30;

    // --- Restart supervision ---

    public RestartPolicy RestartPolicy { get; set; } = RestartPolicy.OnCrash;

    /// <summary>
    /// Extra exit codes to treat as a clean shutdown rather than a crash, on top of 0 and
    /// the standard Windows user-termination codes. Useful for servers that report their
    /// own code when you close their window.
    /// </summary>
    public List<int> CleanExitCodes { get; set; } = new();

    /// <summary>Consecutive failed restarts before the server is parked in Failed state.</summary>
    public int MaxConsecutiveRestarts { get; set; } = 5;

    /// <summary>
    /// How long a server must stay up before its crash counter resets, so a server that
    /// crashes rarely never accumulates its way into the Failed state.
    /// </summary>
    public int StableUptimeMinutes { get; set; } = 5;

    /// <summary>
    /// Superseded by <see cref="Schedule"/>. Still read so existing servers.json files
    /// keep working; <see cref="MigrateLegacySchedules"/> moves it across and clears it,
    /// so nothing has to consult two places to know when a server restarts.
    /// </summary>
    public string ScheduledRestartTime { get; set; } = string.Empty;

    // --- Schedule ---

    /// <summary>
    /// Times this server starts, stops, restarts, updates or backs itself up, each on its
    /// own set of days.
    /// </summary>
    public List<ScheduledTask> Schedule { get; set; } = new();

    // --- Update script ---

    /// <summary>
    /// A .bat, .cmd, .ps1 or .exe that updates the server or its mods. Run on demand,
    /// on a schedule, or before starting — never automatically on its own.
    /// </summary>
    public string UpdateScriptPath { get; set; } = string.Empty;

    /// <summary>Extra arguments appended to the update script invocation.</summary>
    public string UpdateArguments { get; set; } = string.Empty;

    /// <summary>
    /// Run the update script every time this server starts. Off by default: an update
    /// that fetches from the network turns a fast restart into a slow one.
    /// </summary>
    public bool RunUpdateBeforeStart { get; set; }

    /// <summary>
    /// How long to let the update script run before giving up on it. An update that hangs
    /// would otherwise keep a stopped server stopped indefinitely.
    /// </summary>
    public int UpdateTimeoutMinutes { get; set; } = 30;

    /// <summary>Whether an update script is configured at all.</summary>
    public bool HasUpdateScript => !string.IsNullOrWhiteSpace(UpdateScriptPath);

    // --- Backups ---
    //
    // There is no "backups enabled" flag: a Backup entry in Schedule is what turns them
    // on, and a second switch that also had to be on was a checkbox that silently did
    // nothing once schedules replaced the single daily backup time.

    /// <summary>Folder to archive. Empty means the working directory.</summary>
    public string BackupSourceFolder { get; set; } = string.Empty;

    public string BackupDestinationFolder { get; set; } = string.Empty;

    public BackupMode BackupMode { get; set; } = BackupMode.SafeStopAndRestart;

    /// <summary>Superseded by <see cref="Schedule"/>; see <see cref="ScheduledRestartTime"/>.</summary>
    public string BackupScheduleTime { get; set; } = string.Empty;

    /// <summary>Number of archives to keep; older ones are pruned after each run.</summary>
    public int BackupRetentionCount { get; set; } = 5;

    /// <summary>Resolves the effective working directory, falling back to the script's folder.</summary>
    public string ResolveWorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            return WorkingDirectory;
        }

        return string.IsNullOrWhiteSpace(ScriptPath)
            ? Environment.CurrentDirectory
            : Path.GetDirectoryName(Path.GetFullPath(ScriptPath)) ?? Environment.CurrentDirectory;
    }

    /// <summary>Resolves the folder a backup should archive.</summary>
    public string ResolveBackupSource() =>
        string.IsNullOrWhiteSpace(BackupSourceFolder) ? ResolveWorkingDirectory() : BackupSourceFolder;

    /// <summary>
    /// Shapes the update script as a definition the normal launch path understands, so an
    /// update .bat is quoted, environment-injected and job-object-contained exactly like a
    /// server script rather than through a second, less tested code path.
    /// </summary>
    public ServerDefinition CreateUpdateDefinition() => new()
    {
        Id = Id,
        Name = Name + " (update)",
        ScriptPath = UpdateScriptPath,
        Arguments = UpdateArguments,

        // The update runs where the server runs; that is what a mod updater's relative
        // paths are written against.
        WorkingDirectory = ResolveWorkingDirectory(),
        EnvironmentVariables = new Dictionary<string, string>(EnvironmentVariables)
    };

    /// <summary>
    /// Replaces the task with the same id, or adds it if there is none. Replacing in place
    /// keeps the id, which is what the once-per-day fire guard tracks.
    /// </summary>
    public void UpsertScheduledTask(ScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var index = Schedule.FindIndex(t => t.Id == task.Id);
        if (index >= 0)
        {
            Schedule[index] = task;
        }
        else
        {
            Schedule.Add(task);
        }
    }

    /// <summary>Removes a task entirely.</summary>
    /// <returns>False if there was no such task.</returns>
    public bool RemoveScheduledTask(Guid taskId) => Schedule.RemoveAll(t => t.Id == taskId) > 0;

    /// <summary>
    /// Takes one day off a task, leaving its other days alone. A task left with no days
    /// is removed, since it could never fire and would only be clutter.
    /// </summary>
    /// <returns>False if there was no such task.</returns>
    public bool RemoveScheduleDay(Guid taskId, ScheduleDays day)
    {
        var task = Schedule.FirstOrDefault(t => t.Id == taskId);
        if (task is null)
        {
            return false;
        }

        task.Days &= ~day;

        if (task.Days == ScheduleDays.None)
        {
            Schedule.Remove(task);
        }

        return true;
    }

    /// <summary>
    /// Moves the old single daily restart and backup times into <see cref="Schedule"/>.
    /// Idempotent, so running it on every load is safe.
    /// </summary>
    /// <returns>True if anything changed and the file should be rewritten.</returns>
    public bool MigrateLegacySchedules()
    {
        var changed = false;

        if (ScheduledTask.IsValidTime(ScheduledRestartTime))
        {
            Schedule.Add(new ScheduledTask
            {
                Action = ScheduledAction.Restart,
                Time = ScheduledRestartTime.Trim(),
                Days = ScheduleDays.EveryDay
            });

            ScheduledRestartTime = string.Empty;
            changed = true;
        }

        if (ScheduledTask.IsValidTime(BackupScheduleTime))
        {
            Schedule.Add(new ScheduledTask
            {
                Action = ScheduledAction.Backup,
                Time = BackupScheduleTime.Trim(),
                Days = ScheduleDays.EveryDay
            });

            BackupScheduleTime = string.Empty;
            changed = true;
        }

        // A time that was never valid cannot have been firing, so discarding it loses
        // nothing and stops it being carried forward forever.
        if (!string.IsNullOrWhiteSpace(ScheduledRestartTime) || !string.IsNullOrWhiteSpace(BackupScheduleTime))
        {
            ScheduledRestartTime = string.Empty;
            BackupScheduleTime = string.Empty;
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Copies the definition for editing. The environment dictionary is duplicated as
    /// well, so cancelling an edit cannot leave mutations behind on the live definition.
    /// </summary>
    public ServerDefinition Clone()
    {
        var copy = (ServerDefinition)MemberwiseClone();
        copy.EnvironmentVariables = new Dictionary<string, string>(EnvironmentVariables);
        copy.CleanExitCodes = new List<int>(CleanExitCodes);

        // Each task is cloned too, not just the list: editing a schedule entry on a copy
        // must not reach through into the live definition.
        copy.Schedule = Schedule.Select(t => t.Clone()).ToList();

        return copy;
    }

    /// <summary>
    /// Parses "KEY=VALUE" lines into environment variables. Blank lines and lines
    /// starting with # are ignored, and only the first "=" splits, so values may
    /// contain "=" themselves.
    /// </summary>
    public static Dictionary<string, string> ParseEnvironment(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();

            if (key.Length > 0)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Renders environment variables back into editable "KEY=VALUE" lines.</summary>
    public static string FormatEnvironment(IDictionary<string, string>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, variables.Select(pair => $"{pair.Key}={pair.Value}"));
    }
}
