using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace ServerLauncher.Core.Remote;

/// <summary>
/// Finds the TLS certificate the listener should present.
/// </summary>
/// <remarks>
/// Two ways in, and neither puts a password in settings.json. A thumbprint reads a
/// certificate already installed in the Windows store, which is where tools like win-acme
/// put a Let's Encrypt certificate and where renewal keeps it up to date — nothing to
/// type and nothing to store. A .pfx path is the alternative, and its password is read
/// from an environment variable rather than the config file, for the same reason the
/// GitHub token is.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class CertificateResolver
{
    /// <summary>Environment variable holding the password for a .pfx file.</summary>
    public const string PasswordEnvironmentVariable = "SERVERMANAGER_CERT_PASSWORD";

    /// <summary>
    /// Loads the configured certificate.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Nothing is configured, or what is configured cannot be used. Serving a control API
    /// over the internet without TLS is not offered as a fallback, so this throws rather
    /// than quietly degrading.
    /// </exception>
    public static X509Certificate2 Resolve(RemoteAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
        {
            return FromStore(settings.CertificateThumbprint.Trim());
        }

        if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            return FromFile(settings.CertificatePath.Trim());
        }

        throw new InvalidOperationException(
            "Publishing directly needs a TLS certificate. Set either a certificate "
            + "thumbprint, for one already installed on this machine, or the path to a "
            + ".pfx file.");
    }

    /// <summary>Describes what is configured, for the settings screen.</summary>
    public static string Describe(RemoteAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            using var certificate = Resolve(settings);

            var expiry = certificate.NotAfter;
            var remaining = expiry - DateTime.Now;

            var validity = remaining <= TimeSpan.Zero
                ? "EXPIRED"
                : remaining.TotalDays < 21
                    ? $"expires in {(int)remaining.TotalDays} days"
                    : $"valid until {expiry:yyyy-MM-dd}";

            return $"{certificate.GetNameInfo(X509NameType.DnsName, forIssuer: false)} — {validity}";
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    private static X509Certificate2 FromStore(string thumbprint)
    {
        // Thumbprints are often pasted from certmgr, which pads them with spaces and
        // invisible marks.
        var cleaned = new string(thumbprint.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);

            try
            {
                store.Open(OpenFlags.ReadOnly);
            }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException)
            {
                continue;
            }

            foreach (var candidate in store.Certificates)
            {
                if (!string.Equals(candidate.Thumbprint, cleaned, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!candidate.HasPrivateKey)
                {
                    throw new InvalidOperationException(
                        $"The certificate {cleaned} was found in the {location} store but has no "
                        + "private key, so it cannot be used to serve HTTPS.");
                }

                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"No certificate with thumbprint {cleaned} was found in this machine's or this "
            + "user's personal store.");
    }

    private static X509Certificate2 FromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"No certificate file at {path}.");
        }

        var password = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);

        try
        {
            // X509CertificateLoader is .NET 9; this is the .NET 8 way. The key is marked
            // exportable so Kestrel can use it after the file handle is gone.
            var certificate = new X509Certificate2(
                path,
                password,
                X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet
                    | X509KeyStorageFlags.Exportable);

            if (!certificate.HasPrivateKey)
            {
                throw new InvalidOperationException(
                    $"{path} contains no private key, so it cannot be used to serve HTTPS.");
            }

            return certificate;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new InvalidOperationException(
                string.IsNullOrEmpty(password)
                    ? $"Could not read {path}. If it is password protected, put the password in "
                      + $"the {PasswordEnvironmentVariable} environment variable."
                    : $"Could not read {path} with the password from "
                      + $"{PasswordEnvironmentVariable}: {ex.Message}",
                ex);
        }
    }
}
