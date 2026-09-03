namespace ServerLauncher.Core.Models;

/// <summary>Lifecycle states a supervised server moves through.</summary>
public enum ServerState
{
    /// <summary>Not running, and not expected to be.</summary>
    Stopped,

    /// <summary>Launch requested; process starting up.</summary>
    Starting,

    /// <summary>Process is alive and being supervised.</summary>
    Running,

    /// <summary>Graceful shutdown in progress, awaiting exit before force-kill.</summary>
    Stopping,

    /// <summary>Exited unexpectedly; a restart may be pending under the policy.</summary>
    Crashed,

    /// <summary>Crashed too many times in a row and has been parked for manual attention.</summary>
    Failed
}
