namespace ServerLauncher.Core.Storage;

/// <summary>Canonical locations for configuration and logs.</summary>
public static class AppPaths
{
    private const string AppFolderName = "ServerLauncher";

    /// <summary>Configuration root, under %APPDATA%.</summary>
    public static string ConfigRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>Log root, under %LOCALAPPDATA% since logs are machine-local and bulky.</summary>
    public static string LogRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName, "logs");

    public static string ServersFile => Path.Combine(ConfigRoot, "servers.json");

    public static string SettingsFile => Path.Combine(ConfigRoot, "settings.json");

    public static string LogDirectoryFor(Guid serverId) => Path.Combine(LogRoot, serverId.ToString("N"));

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(LogRoot);
    }
}
