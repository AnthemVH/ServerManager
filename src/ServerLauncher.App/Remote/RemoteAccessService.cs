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
            return true;
        }

        try
        {
            await _server.StartAsync(settings.RemoteAccess).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            // A misconfigured listener must not take the app down; Settings shows why.
            LastError = ex.Message;
            await _server.StopAsync().ConfigureAwait(false);
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Pairing.CancelPairing();
        await _server.DisposeAsync().ConfigureAwait(false);
    }
}
