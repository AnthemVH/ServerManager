namespace ServerLauncher.Core.Remote;

/// <summary>Configuration for the remote control API.</summary>
public sealed class RemoteAccessSettings
{
    /// <summary>
    /// Off by default. Remote control is a way to run commands on this machine, so it
    /// exists only once the user has deliberately turned it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Address to listen on. Empty means loopback, which is the intended configuration:
    /// Tailscale Serve publishes the local port onto the tailnet, so nothing but this
    /// machine ever binds a routable address.
    /// </summary>
    public string BindAddress { get; set; } = string.Empty;

    public int Port { get; set; } = 8787;

    /// <summary>
    /// The base URL a phone should connect to, which is not the address the listener binds.
    /// </summary>
    /// <remarks>
    /// The API listens on loopback and Tailscale Serve republishes it, so the address the
    /// phone needs depends on how Serve was configured — a tailnet IP and port, or an
    /// HTTPS name like https://box.tail1234.ts.net. It cannot be derived reliably, so it is
    /// stated here and embedded in the pairing QR code. Empty falls back to the detected
    /// Tailscale address and configured port.
    /// </remarks>
    public string PhoneAddress { get; set; } = string.Empty;

    /// <summary>Resolves the URL to hand a phone, falling back to the tailnet address.</summary>
    public string ResolvePhoneAddress()
    {
        if (!string.IsNullOrWhiteSpace(PhoneAddress))
        {
            return PhoneAddress.Trim().TrimEnd('/');
        }

        var tailscale = TailscaleDetector.Detect();
        return tailscale is null ? string.Empty : $"http://{tailscale}:{Port}";
    }

    /// <summary>
    /// Permits binding to an address outside Tailscale's range, including one reachable
    /// from the open internet.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Enabled"/> on purpose. Listening on the tailnet means only
    /// devices in your tailnet can reach the port at all; listening anywhere else removes
    /// that layer and leaves the token check as the only thing between the internet and a
    /// service that starts processes. The user has to ask for that explicitly.
    /// </remarks>
    public bool AllowNonTailscaleBinding { get; set; }
}
