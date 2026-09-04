namespace ServerLauncher.Core.Remote;

/// <summary>
/// Wire types for the remote API.
/// </summary>
/// <remarks>
/// Deliberately separate from the domain models. The Android client is versioned and
/// released independently, so the API shape must be something we choose and keep stable,
/// not whatever a refactor of <see cref="Models.ServerDefinition"/> happens to produce.
/// Note there is no contract for creating or editing a server — that boundary is what
/// stops a stolen token becoming code execution.
/// </remarks>
public sealed record PairRequest(string? Code, string? DeviceName);

public sealed record PairResponse(
    string Token,
    string DeviceId,
    string DeviceName,
    IReadOnlyList<string> Capabilities);

public sealed record ServerSummary(
    string Id,
    string Name,
    string State,
    double CpuPercent,
    double MemoryMegabytes,
    int ProcessCount,
    string Uptime,
    bool CanStart,
    bool CanStop,
    bool IsLauncherDetached);

public sealed record ConsoleLineDto(string Timestamp, string Stream, string Text);

public sealed record ConsoleResponse(string ServerId, IReadOnlyList<ConsoleLineDto> Lines);

public sealed record CommandRequest(string? Command);

public sealed record LauncherHealth(
    double CpuPercent,
    double MemoryMegabytes,
    double ManagedMemoryMegabytes,
    int ThreadCount,
    int HandleCount,
    string Uptime,
    string Version,
    int RunningServers,
    int TotalServers);

public sealed record ApiError(string Error);

public sealed record ActionResult(bool Ok, string Message);
