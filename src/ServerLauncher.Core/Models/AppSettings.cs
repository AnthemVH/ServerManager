namespace ServerLauncher.Core.Models;

/// <summary>Application-wide preferences, persisted to settings.json.</summary>
public sealed class AppSettings
{
    /// <summary>Console lines kept in memory per server before the oldest are dropped.</summary>
    public int ConsoleBufferLines { get; set; } = 5000;

    /// <summary>Days of rolling log files to keep on disk.</summary>
    public int LogRetentionDays { get; set; } = 14;

    /// <summary>Seconds between resource samples.</summary>
    public int ResourceSampleIntervalSeconds { get; set; } = 2;

    /// <summary>Closing the main window hides to the tray instead of exiting.</summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>Start minimised to the tray, for use with run-at-login.</summary>
    public bool StartMinimised { get; set; }

    /// <summary>
    /// Executable used to run .ps1 scripts. Defaults to Windows PowerShell, which is
    /// always present; point this at pwsh.exe if PowerShell 7 is installed.
    /// </summary>
    public string PowerShellPath { get; set; } = "powershell.exe";

    // --- Updates ---

    /// <summary>
    /// GitHub repository to check for new releases, as "owner/name". Empty disables
    /// update checking entirely.
    /// </summary>
    public string UpdateRepository { get; set; } = "AnthemVH/ServerManager";

    /// <summary>Check for a newer release when the launcher starts.</summary>
    public bool CheckForUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// Start the launcher when the current user logs in. Paired with Windows auto-logon,
    /// this is what brings servers back after an unattended reboot.
    /// </summary>
    public bool StartWithWindows { get; set; }
}
