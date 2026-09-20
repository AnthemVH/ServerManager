using System.Runtime.Versioning;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Remote;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.App.Remote;

/// <summary>
/// Owns the remote API and its stores for the lifetime of the application, and starts or
/// stops it to match the current settings.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RemoteAccessService : IAsyncDisposable
{
    private readonly RemoteApiServer _server;

    /// <summary>The settings the running listener was started with, or null when stopped.</summary>
    private RemoteAccessSettings? _applied;

    public RemoteAccessService(ServerManager manager)
    {
        Devices = new DeviceStore();
        Audit = new RemoteAuditLog();
        Pairing = new PairingService(Devices);

        _server = new RemoteApiServer(manager, Devices, Pairing, Audit);
    }

    public DeviceStore Devices { get; }

    public PairingService Pairing { get; }

    public RemoteAuditLog Audit { get; }

    public bool IsRunning => _server.IsRunning;

    /// <summary>Local address the API is bound to, or null when stopped.</summary>
    public string? ListeningOn => _server.ListeningOn;

    /// <summary>
    /// The address to open a browser at on this machine. Not the same as
    /// <see cref="ListeningOn"/>, which reports the socket that was bound — a browser
    /// cannot open "0.0.0.0", and loopback reaches the listener whatever it bound.
    /// </summary>
    public string? BrowsableAddress =>
        _server.IsRunning ? _applied?.LocalAddress : null;

    /// <summary>Why the last attempt to start failed, for display in Settings.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Brings the API into line with the settings: running when enabled, stopped when not.
    /// </summary>
    /// <returns>True when the resulting state matches what was asked for.</returns>
    public async Task<bool> ApplyAsync(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        LastError = null;

        if (!settings.RemoteAccess.Enabled)
        {
            await _server.StopAsync().ConfigureAwait(false);
            _applied = null;
            return true;
        }

        try
        {
            await _server.StartAsync(settings.RemoteAccess).ConfigureAwait(false);

            // Held as a copy: the settings object the dialog hands over keeps being
            // edited, and this has to describe the listener that is actually running.
            _applied = settings.RemoteAccess.Clone();
            return true;
        }
        catch (Exception ex)
        {
            // A misconfigured listener must not take the app down; Settings shows why.
            LastError = ex.Message;
            await _server.StopAsync().ConfigureAwait(false);
            _applied = null;
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Pairing.CancelPairing();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
