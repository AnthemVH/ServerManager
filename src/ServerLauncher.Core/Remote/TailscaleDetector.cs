using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ServerLauncher.Core.Remote;

/// <summary>
/// Finds the machine's Tailscale address.
/// </summary>
/// <remarks>
/// Tailscale assigns addresses from the carrier-grade NAT range 100.64.0.0/10, which is
/// reserved and never appears on an ordinary LAN, so an address in that range is a
/// reliable signal without shelling out to the Tailscale CLI.
/// </remarks>
public static class TailscaleDetector
{
    /// <summary>True when the address falls in Tailscale's 100.64.0.0/10 range.</summary>
    public static bool IsTailscaleAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var octets = address.GetAddressBytes();
        return octets[0] == 100 && octets[1] >= 64 && octets[1] <= 127;
    }

    public static bool IsTailscaleAddress(string? address) =>
        IPAddress.TryParse(address, out var parsed) && IsTailscaleAddress(parsed);

    /// <summary>
    /// The first Tailscale address on an operational interface, or null when Tailscale is
    /// not running.
    /// </summary>
    public static IPAddress? Detect()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (IsTailscaleAddress(info.Address))
                    {
                        return info.Address;
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Treated as "not found"; the user can still set an address by hand.
        }

        return null;
    }

    /// <summary>Human-readable status for the settings screen.</summary>
    public static string DescribeDetection()
    {
        var address = Detect();
        return address is null
            ? "Tailscale not detected. Install it and sign in, then reopen this window."
            : $"Tailscale address detected: {address}";
    }
}
