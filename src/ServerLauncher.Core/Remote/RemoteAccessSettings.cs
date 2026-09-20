using System.Net;

namespace ServerLauncher.Core.Remote;

/// <summary>Where and how the browser interface is served.</summary>
public sealed class RemoteAccessSettings
{
    /// <summary>Listens on this machine only. The default, and the safe one.</summary>
    public const string LoopbackAddress = "127.0.0.1";

    /// <summary>Listens on every network interface, which is what port forwarding needs.</summary>
    public const string AllInterfacesAddress = "0.0.0.0";

    /// <summary>
    /// Off by default. This is a way to run commands on this machine, so it exists only
    /// once the user has deliberately turned it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The address to bind. <see cref="LoopbackAddress"/> keeps it on this machine,
    /// <see cref="AllInterfacesAddress"/> accepts connections from the network, and a
    /// specific IP binds just that interface.
    /// </summary>
    /// <remarks>
    /// Binding beyond loopback is what makes port forwarding possible, and it is also the
    /// point at which a device token becomes the only thing between a stranger and a
    /// service that starts processes. Hence <see cref="IsLocalOnly"/>, which the settings
    /// screen uses to say so.
    /// </remarks>
    public string BindAddress { get; set; } = LoopbackAddress;

    public int Port { get; set; } = 8787;

    /// <summary>
    /// Thumbprint of a certificate in this machine's or this user's personal store. The
    /// simplest option on Windows: a tool that obtains a Let's Encrypt certificate puts it
    /// there and keeps it renewed, and no password is involved.
    /// </summary>
    public string CertificateThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Path to a .pfx file, as an alternative to a thumbprint. Its password is read from
    /// the SERVERMANAGER_CERT_PASSWORD environment variable, never from this file.
    /// </summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// The address people type, such as https://servers.example.com or
    /// http://203.0.113.9:8787. Shown when pairing, because it cannot be derived from the
    /// listener — that only knows which socket it bound, not how the world reaches it.
    /// </summary>
    public string PublicAddress { get; set; } = string.Empty;

    /// <summary>Whether TLS is configured.</summary>
    public bool HasCertificate =>
        !string.IsNullOrWhiteSpace(CertificateThumbprint)
        || !string.IsNullOrWhiteSpace(CertificatePath);

    /// <summary>Whether only this machine can reach the listener.</summary>
    public bool IsLocalOnly =>
        TryResolveBindAddress(out var address, out _) && IPAddress.IsLoopback(address);

    /// <summary>
    /// True when the listener is reachable from the network but has no TLS, so device
    /// tokens cross it in clear text. The settings screen warns about exactly this.
    /// </summary>
    public bool IsUnencryptedOnTheNetwork => !IsLocalOnly && !HasCertificate;

    /// <summary>Parses <see cref="BindAddress"/>.</summary>
    /// <param name="address">The parsed address, or loopback when it cannot be parsed.</param>
    /// <param name="error">Why it could not be parsed, or empty.</param>
    public bool TryResolveBindAddress(out IPAddress address, out string error)
    {
        error = string.Empty;

        var value = BindAddress?.Trim() ?? string.Empty;

        // An empty setting means the default rather than an error: an older settings.json
        // has no bind address at all, and that should keep working as local only.
        if (value.Length == 0)
        {
            address = IPAddress.Loopback;
            return true;
        }

        // Spelled out as well as numeric, because "0.0.0.0" is not obvious and someone
        // reading settings.json should be able to guess what to write.
        if (value.Equals("any", StringComparison.OrdinalIgnoreCase)
            || value.Equals("all", StringComparison.OrdinalIgnoreCase)
            || value == "*")
        {
            address = IPAddress.Any;
            return true;
        }

        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            address = IPAddress.Loopback;
            return true;
        }

        if (IPAddress.TryParse(value, out var parsed))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.Loopback;
        error = $"'{value}' is not an IP address. Use {LoopbackAddress} for this machine "
                + $"only, {AllInterfacesAddress} for every network interface, or the "
                + "address of one interface.";
        return false;
    }

    /// <summary>The scheme the listener serves, which follows from having a certificate.</summary>
    public string Scheme => HasCertificate ? "https" : "http";

    /// <summary>The address that reaches the listener from this machine.</summary>
    public string LocalAddress => $"{Scheme}://127.0.0.1:{Port}";

    /// <summary>
    /// The address to hand a device that is pairing: the public one if set, otherwise the
    /// local one, which is at least correct for a browser on this machine.
    /// </summary>
    public string ResolvePairingAddress() =>
        string.IsNullOrWhiteSpace(PublicAddress)
            ? LocalAddress
            : PublicAddress.Trim().TrimEnd('/');

    /// <summary>A copy, so the settings dialog can edit without touching the live values.</summary>
    public RemoteAccessSettings Clone() => (RemoteAccessSettings)MemberwiseClone();
}
