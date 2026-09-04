using System.Security.Cryptography;
using System.Text;
using ServerLauncher.Core.Storage;

namespace ServerLauncher.Core.Remote;

/// <summary>
/// Holds the paired devices and authenticates their tokens.
/// </summary>
/// <remarks>
/// Tokens are 256 bits of cryptographic randomness rather than passwords, so a plain
/// SHA-256 is the right primitive: there is nothing to brute force, and a slow KDF would
/// only add latency to every request. What matters is that the plaintext is never stored
/// and that comparisons do not leak timing.
/// </remarks>
public sealed class DeviceStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<PairedDevice> _devices;

    public DeviceStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.ConfigRoot, "devices.json");
        _devices = JsonStore.Load(_path, () => new List<PairedDevice>());
    }

    /// <summary>Generates a token for a new device. Returned once and never stored.</summary>
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public static string HashToken(string token) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public IReadOnlyList<PairedDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return _devices.ToArray();
            }
        }
    }

    public PairedDevice Add(string name, string token, DeviceCapabilities capabilities)
    {
        var device = new PairedDevice
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Device" : name.Trim(),
            TokenHash = HashToken(token),
            Capabilities = capabilities
        };

        lock (_gate)
        {
            _devices.Add(device);
            Persist();
        }

        return device;
    }

    public bool Revoke(string deviceId)
    {
        lock (_gate)
        {
            var removed = _devices.RemoveAll(d => d.Id == deviceId) > 0;
            if (removed)
            {
                Persist();
            }

            return removed;
        }
    }

    public bool SetCapabilities(string deviceId, DeviceCapabilities capabilities)
    {
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null)
            {
                return false;
            }

            device.Capabilities = capabilities;
            Persist();
            return true;
        }
    }

    /// <summary>
    /// Finds the device a token belongs to, or null. Every candidate is compared in
    /// constant time so a caller cannot learn a hash prefix by measuring responses.
    /// </summary>
    public PairedDevice? Authenticate(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var hash = Encoding.UTF8.GetBytes(HashToken(token));

        lock (_gate)
        {
            PairedDevice? match = null;

            foreach (var device in _devices)
            {
                var candidate = Encoding.UTF8.GetBytes(device.TokenHash);

                // Compare all of them rather than returning early, so the work done does
                // not depend on which device matched or how far the scan got.
                if (candidate.Length == hash.Length
                    && CryptographicOperations.FixedTimeEquals(candidate, hash))
                {
                    match = device;
                }
            }

            return match;
        }
    }

    /// <summary>Records that a device just made a request.</summary>
    public void TouchLastSeen(string deviceId)
    {
        lock (_gate)
        {
            var device = _devices.FirstOrDefault(d => d.Id == deviceId);
            if (device is null)
            {
                return;
            }

            device.LastSeen = DateTimeOffset.Now;
            Persist();
        }
    }

    /// <summary>Re-reads from disk, for tests and for external edits.</summary>
    public void Reload()
    {
        lock (_gate)
        {
            _devices = JsonStore.Load(_path, () => new List<PairedDevice>());
        }
    }

    private void Persist() => JsonStore.Save(_path, _devices);
}
