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

    /// <summary>Daily restart time as "HH:mm", or empty for no scheduled restart.</summary>
    public string ScheduledRestartTime { get; set; } = string.Empty;

    // --- Backups ---

    public bool BackupEnabled { get; set; }

    /// <summary>Folder to archive. Empty means the working directory.</summary>
    public string BackupSourceFolder { get; set; } = string.Empty;

    public string BackupDestinationFolder { get; set; } = string.Empty;

    public BackupMode BackupMode { get; set; } = BackupMode.SafeStopAndRestart;

    /// <summary>Daily backup time as "HH:mm", or empty for manual backups only.</summary>
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
    /// Copies the definition for editing. The environment dictionary is duplicated as
    /// well, so cancelling an edit cannot leave mutations behind on the live definition.
    /// </summary>
    public ServerDefinition Clone()
    {
        var copy = (ServerDefinition)MemberwiseClone();
        copy.EnvironmentVariables = new Dictionary<string, string>(EnvironmentVariables);
        copy.CleanExitCodes = new List<int>(CleanExitCodes);
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
