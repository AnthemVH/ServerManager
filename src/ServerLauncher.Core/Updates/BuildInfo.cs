using System.Reflection;

namespace ServerLauncher.Core.Updates;

/// <summary>
/// Which kind of build this is.
/// </summary>
/// <remarks>
/// There are two: a small one that needs the .NET runtimes installed, and a standalone one
/// that carries them. They cannot be swapped for one another — replacing a standalone
/// build with the small one on a machine without the runtimes would leave an app that
/// cannot start, and nothing in the app could report why, because a missing framework
/// stops the process before its code runs. So each build updates itself with its own kind.
/// </remarks>
public static class BuildInfo
{
    /// <summary>Release asset for the build that needs the runtimes installed.</summary>
    public const string FrameworkDependentAsset = "ServerLauncher.exe";

    /// <summary>Release asset for the build that carries its own runtimes.</summary>
    public const string StandaloneAsset = "ServerLauncher-standalone.exe";

    private static readonly Lazy<bool> SelfContained = new(() =>
    {
        var value = Assembly.GetEntryAssembly()
            ?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "SelfContained")
            ?.Value;

        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    });

    /// <summary>True when this build carries its own .NET runtimes.</summary>
    public static bool IsStandalone => SelfContained.Value;

    /// <summary>The release asset this build should update itself with.</summary>
    public static string UpdateAssetName =>
        IsStandalone ? StandaloneAsset : FrameworkDependentAsset;

    /// <summary>How this build is described in the settings screen.</summary>
    public static string Describe() => IsStandalone
        ? "Standalone build — carries its own .NET runtimes."
        : "Needs the .NET 8 Desktop and ASP.NET Core runtimes installed.";
}
