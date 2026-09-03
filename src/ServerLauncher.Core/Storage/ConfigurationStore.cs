using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Storage;

/// <summary>Loads and persists the server list and application settings.</summary>
public sealed class ConfigurationStore
{
    private readonly string _serversFile;
    private readonly string _settingsFile;

    /// <param name="serversFile">Server list path. Defaults to the location under %APPDATA%.</param>
    /// <param name="settingsFile">Settings path. Defaults to the location under %APPDATA%.</param>
    /// <remarks>
    /// The paths are injectable so tests can exercise persistence without touching the
    /// real configuration on the machine running them.
    /// </remarks>
    public ConfigurationStore(string? serversFile = null, string? settingsFile = null)
    {
        _serversFile = serversFile ?? AppPaths.ServersFile;
        _settingsFile = settingsFile ?? AppPaths.SettingsFile;
    }

    public List<ServerDefinition> LoadServers() =>
        JsonStore.Load(_serversFile, () => new List<ServerDefinition>());

    public void SaveServers(IEnumerable<ServerDefinition> servers) =>
        JsonStore.Save(_serversFile, servers.ToList());

    public AppSettings LoadSettings() =>
        JsonStore.Load(_settingsFile, () => new AppSettings());

    public void SaveSettings(AppSettings settings) =>
        JsonStore.Save(_settingsFile, settings);
}
