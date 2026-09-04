namespace ServerLauncher.Core.Remote;

/// <summary>Configuration for the remote control API and browser interface.</summary>
public sealed class RemoteAccessSettings
{
    /// <summary>
    /// Off by default. Remote control is a way to run commands on this machine, so it
    /// exists only once the user has deliberately turned it on.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Accept connections from other machines rather than only this one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Enabled"/> deliberately. Listening on loopback means only
    /// something already on this machine can reach it; publishing it means the internet
    /// can, and the token check becomes the only thing between a stranger and a service
    /// that starts processes. TLS is required in that mode, so this cannot be switched on
    /// without a certificate.
    /// </remarks>
    public bool PublishDirectly { get; set; }

    public int Port { get; set; } = 8787;

    /// <summary>
    /// Thumbprint of a certificate installed in this machine's or this user's personal
    /// store. The simplest option on Windows: tools that obtain a Let's Encrypt
    /// certificate put it there and keep it renewed, and no password is involved.
    /// </summary>
    public string CertificateThumbprint { get; set; } = string.Empty;

    /// <summary>
    /// Path to a .pfx file, as an alternative to a thumbprint. Its password is read from
    /// the SERVERMANAGER_CERT_PASSWORD environment variable, never from this file.
    /// </summary>
    public string CertificatePath { get; set; } = string.Empty;

    /// <summary>
    /// The address people should use, such as https://servers.example.com. Shown when
    /// pairing so a phone knows where to connect; it cannot be derived from the listener,
    /// which only knows which port it bound.
    /// </summary>
    public string PublicAddress { get; set; } = string.Empty;

    /// <summary>The address to hand a device that is pairing.</summary>
    public string ResolvePublicAddress()
    {
        if (!string.IsNullOrWhiteSpace(PublicAddress))
        {
            return PublicAddress.Trim().TrimEnd('/');
        }

        return PublishDirectly ? string.Empty : $"http://127.0.0.1:{Port}";
    }

    /// <summary>Whether TLS is configured, which publishing directly requires.</summary>
    public bool HasCertificate =>
        !string.IsNullOrWhiteSpace(CertificateThumbprint)
        || !string.IsNullOrWhiteSpace(CertificatePath);
}
