namespace ServerLauncher.Core.Remote;

/// <summary>
/// What a paired device is allowed to do.
/// </summary>
/// <remarks>
/// Note what is absent: there is no capability for creating or editing a server. The API
/// deliberately cannot define new servers, because ServerManager launches arbitrary
/// scripts — an endpoint that could point a "server" at any executable would turn a
/// stolen token into remote code execution on the machine.
/// </remarks>
[Flags]
public enum DeviceCapabilities
{
    None = 0,

    /// <summary>Read server status, resource use and launcher health.</summary>
    View = 1,

    /// <summary>Start, stop and restart existing servers.</summary>
    Control = 2,

    /// <summary>Read captured console output.</summary>
    ReadConsole = 4,

    /// <summary>
    /// Send console commands to a running server. Off by default: it is arbitrary input to
    /// the game server, and many servers have admin commands that touch the filesystem.
    /// </summary>
    SendCommands = 8,

    /// <summary>What a newly paired device gets unless the user grants more.</summary>
    Default = View | Control | ReadConsole
}

/// <summary>A phone or other client that has been paired with this install.</summary>
public sealed class PairedDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "Device";

    /// <summary>
    /// Base64 SHA-256 of the device token. The token itself is shown once at pairing and
    /// never stored, so a copy of devices.json cannot be used to authenticate.
    /// </summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset PairedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset? LastSeen { get; set; }

    public DeviceCapabilities Capabilities { get; set; } = DeviceCapabilities.Default;

    public bool Can(DeviceCapabilities capability) => (Capabilities & capability) == capability;
}
