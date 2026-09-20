namespace ServerLauncher.Core.Remote;

/// <summary>Configuration for the browser interface and the local control API.</summary>
public sealed class RemoteAccessSettings
{
    /// <summary>
    /// Off by default. This is a way to run commands on this machine, so it exists only
    /// once the user has deliberately turned it on.
    /// </summary>
    public bool Enabled { get; set; }

    public int Port { get; set; } = 8787;

    /// <summary>The address the browser interface is served at.</summary>
    /// <remarks>
    /// Always loopback. The listener binds 127.0.0.1, so only something already running
    /// on this machine can reach it — there is nothing to publish, no certificate to
    /// obtain and no port to open, and a stranger on the internet has no route to a
    /// service that starts processes.
    /// </remarks>
    public string LocalAddress => $"http://127.0.0.1:{Port}";
}
