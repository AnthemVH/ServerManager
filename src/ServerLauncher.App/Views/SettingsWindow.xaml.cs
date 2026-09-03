using System.Windows;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Updates;

namespace ServerLauncher.App.Views;

/// <summary>Editor for application-wide preferences.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();

        // Edited on a copy so cancelling changes nothing.
        Settings = new AppSettings
        {
            ConsoleBufferLines = settings.ConsoleBufferLines,
            LogRetentionDays = settings.LogRetentionDays,
            ResourceSampleIntervalSeconds = settings.ResourceSampleIntervalSeconds,
            MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose,
            StartMinimised = settings.StartMinimised,
            PowerShellPath = settings.PowerShellPath,
            UpdateRepository = settings.UpdateRepository,
            CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup,
            StartWithWindows = settings.StartWithWindows
        };

        ConsoleLinesBox.Text = Settings.ConsoleBufferLines.ToString();
        LogRetentionBox.Text = Settings.LogRetentionDays.ToString();
        SampleIntervalBox.Text = Settings.ResourceSampleIntervalSeconds.ToString();
        MinimiseToTrayBox.IsChecked = Settings.MinimizeToTrayOnClose;
        StartMinimisedBox.IsChecked = Settings.StartMinimised;
        PowerShellBox.Text = Settings.PowerShellPath;
        UpdateRepositoryBox.Text = Settings.UpdateRepository;
        CheckUpdatesBox.IsChecked = Settings.CheckForUpdatesOnStartup;

        // Read the real registry state rather than trusting the saved flag, which can
        // drift if the entry was removed outside the app.
        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();
    }

    public AppSettings Settings { get; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Settings.ConsoleBufferLines = ParseInt(ConsoleLinesBox.Text, 5000, min: 100);
        Settings.LogRetentionDays = ParseInt(LogRetentionBox.Text, 14, min: 1);
        Settings.ResourceSampleIntervalSeconds = ParseInt(SampleIntervalBox.Text, 2, min: 1);
        Settings.MinimizeToTrayOnClose = MinimiseToTrayBox.IsChecked == true;
        Settings.StartMinimised = StartMinimisedBox.IsChecked == true;

        var shell = PowerShellBox.Text.Trim();
        Settings.PowerShellPath = shell.Length == 0 ? "powershell.exe" : shell;

        Settings.UpdateRepository = NormaliseRepository(UpdateRepositoryBox.Text);
        Settings.CheckForUpdatesOnStartup = CheckUpdatesBox.IsChecked == true;

        Settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        if (!StartupRegistration.SetEnabled(Settings.StartWithWindows, Settings.StartMinimised))
        {
            MessageBox.Show(
                "Could not change the start-at-login setting. Everything else was saved.",
                "Server Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private static int ParseInt(string text, int fallback, int min) =>
        int.TryParse(text.Trim(), out var value) && value >= min ? value : fallback;

    /// <summary>
    /// Accepts a full GitHub URL as well as "owner/name", since pasting the browser
    /// address is the obvious thing to do.
    /// </summary>
    private static string NormaliseRepository(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        const string prefix = "github.com/";
        var index = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            value = value[(index + prefix.Length)..];
        }

        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        // Keep only owner/name, discarding any trailing path such as /releases.
        var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : value;
    }
}
