namespace ServerLauncher.Core.Models;

/// <summary>Determines whether a server is relaunched after it exits.</summary>
public enum RestartPolicy
{
    /// <summary>Never restart automatically.</summary>
    Never,

    /// <summary>Restart only on a non-zero exit code or an external kill.</summary>
    OnCrash,

    /// <summary>Restart on any exit, including a clean one.</summary>
    Always
}

/// <summary>How a backup handles files the running server holds open.</summary>
public enum BackupMode
{
    /// <summary>Copy while running, skipping locked files. Fast, not guaranteed consistent.</summary>
    Live,

    /// <summary>Stop the server, back up, then restart it. Consistent, brief downtime.</summary>
    SafeStopAndRestart
}

/// <summary>Which stream a captured console line came from.</summary>
public enum LogStream
{
    StandardOutput,
    StandardError,

    /// <summary>Emitted by the launcher itself (state changes, restart notices).</summary>
    Launcher
}
