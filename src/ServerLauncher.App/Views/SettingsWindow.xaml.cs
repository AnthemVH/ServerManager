using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ServerLauncher.App.Remote;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Remote;
using ServerLauncher.Core.Updates;

namespace ServerLauncher.App.Views;

/// <summary>Editor for application-wide preferences.</summary>
public partial class SettingsWindow : Window
{
    private readonly RemoteAccessService? _remote;
    private readonly ObservableCollection<DeviceRow> _devices = new();

    public SettingsWindow(AppSettings settings, RemoteAccessService? remote = null)
    {
        InitializeComponent();

        _remote = remote;

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
            StartWithWindows = settings.StartWithWindows,
            RemoteAccess = new RemoteAccessSettings
            {
                Enabled = settings.RemoteAccess.Enabled,
                PublishDirectly = settings.RemoteAccess.PublishDirectly,
                Port = settings.RemoteAccess.Port,
                PublicAddress = settings.RemoteAccess.PublicAddress,
                CertificateThumbprint = settings.RemoteAccess.CertificateThumbprint,
                CertificatePath = settings.RemoteAccess.CertificatePath
            }
        };

        ConsoleLinesBox.Text = Settings.ConsoleBufferLines.ToString();
        LogRetentionBox.Text = Settings.LogRetentionDays.ToString();
        SampleIntervalBox.Text = Settings.ResourceSampleIntervalSeconds.ToString();
        MinimiseToTrayBox.IsChecked = Settings.MinimizeToTrayOnClose;
        StartMinimisedBox.IsChecked = Settings.StartMinimised;
        PowerShellBox.Text = Settings.PowerShellPath;
        UpdateRepositoryBox.Text = Settings.UpdateRepository;
        CheckUpdatesBox.IsChecked = Settings.CheckForUpdatesOnStartup;

        // Which build this is decides which release asset an update installs, so it is
        // worth being able to see it without inspecting the file on disk.
        BuildKindText.Text = "This install: " + BuildInfo.Describe()
            + $" Updates install '{UpdateService.AssetName}' from each release.";

        // Read the real registry state rather than trusting the saved flag, which can
        // drift if the entry was removed outside the app.
        StartWithWindowsBox.IsChecked = StartupRegistration.IsEnabled();

        RemoteEnabledBox.IsChecked = Settings.RemoteAccess.Enabled;
        RemotePortBox.Text = Settings.RemoteAccess.Port.ToString();
        RemotePublicAddressBox.Text = Settings.RemoteAccess.PublicAddress;
        RemotePublishBox.IsChecked = Settings.RemoteAccess.PublishDirectly;
        CertThumbprintBox.Text = Settings.RemoteAccess.CertificateThumbprint;
        CertPathBox.Text = Settings.RemoteAccess.CertificatePath;
        CertStatusText.Text = CertificateResolver.Describe(Settings.RemoteAccess);

        DeviceList.ItemsSource = _devices;
        RefreshRemoteStatus();
        RefreshDevices();
    }

    public AppSettings Settings { get; }

    /// <summary>A paired device as shown in the list, with its command permission bound.</summary>
    public sealed class DeviceRow : INotifyPropertyChanged
    {
        private readonly DeviceStore _store;
        private bool _canSendCommands;

        public DeviceRow(DeviceStore store, PairedDevice device)
        {
            _store = store;
            Id = device.Id;
            Name = device.Name;
            _canSendCommands = device.Can(DeviceCapabilities.SendCommands);

            var seen = device.LastSeen is null
                ? "never seen"
                : $"last seen {device.LastSeen:yyyy-MM-dd HH:mm}";

            Detail = $"Paired {device.PairedAt:yyyy-MM-dd HH:mm} · {seen}";
        }

        public string Id { get; }

        public string Name { get; }

        public string Detail { get; }

        public bool CanSendCommands
        {
            get => _canSendCommands;
            set
            {
                if (_canSendCommands == value)
                {
                    return;
                }

                _canSendCommands = value;

                // Applied at once: a permission just switched off should not wait for the
                // user to also press Save.
                var capabilities = DeviceCapabilities.Default;
                if (value)
                {
                    capabilities |= DeviceCapabilities.SendCommands;
                }

                _store.SetCapabilities(Id, capabilities);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSendCommands)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void RefreshDevices()
    {
        _devices.Clear();

        if (_remote is not null)
        {
            foreach (var device in _remote.Devices.Devices)
            {
                _devices.Add(new DeviceRow(_remote.Devices, device));
            }
        }

        NoDevicesText.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshRemoteStatus()
    {
        if (_remote is null)
        {
            RemoteStatusText.Text = string.Empty;
            return;
        }

        RemoteStatusText.Text = _remote.LastError is { } error
            ? $"Not running: {error}"
            : _remote.IsRunning
                ? $"Running, listening on {_remote.ListeningOn}"
                : "Not running.";
    }

    private void OnPairDevice(object sender, RoutedEventArgs e)
    {
        if (_remote is null)
        {
            return;
        }

        // Pairing needs the API up, otherwise the phone has nothing to talk to.
        if (!_remote.IsRunning)
        {
            MessageBox.Show(
                "Turn on remote access and press Save first, then pair a phone.",
                "Remote access is off",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var dialog = new PairingWindow(_remote.Pairing, Settings) { Owner = this };
        dialog.ShowDialog();

        RefreshDevices();
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e) => App.Current.OpenBrowserInterface();

    private void OnRevokeDevice(object sender, RoutedEventArgs e)
    {
        if (_remote is null || sender is not Button { Tag: string deviceId })
        {
            return;
        }

        var row = _devices.FirstOrDefault(d => d.Id == deviceId);

        var confirm = MessageBox.Show(
            $"Revoke access for '{row?.Name ?? "this device"}'?\n\n"
            + "It stops working immediately and would have to be paired again.",
            "Revoke device",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        _remote.Devices.Revoke(deviceId);
        RefreshDevices();
    }

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

        Settings.RemoteAccess.Enabled = RemoteEnabledBox.IsChecked == true;
        Settings.RemoteAccess.Port = ParseInt(RemotePortBox.Text, 8787, min: 1);
        Settings.RemoteAccess.PublicAddress = RemotePublicAddressBox.Text.Trim();
        Settings.RemoteAccess.PublishDirectly = RemotePublishBox.IsChecked == true;
        Settings.RemoteAccess.CertificateThumbprint = CertThumbprintBox.Text.Trim();
        Settings.RemoteAccess.CertificatePath = CertPathBox.Text.Trim();

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
