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
            RemoteAccess = settings.RemoteAccess.Clone()
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
        CertThumbprintBox.Text = Settings.RemoteAccess.CertificateThumbprint;
        CertPathBox.Text = Settings.RemoteAccess.CertificatePath;
        CertStatusText.Text = Settings.RemoteAccess.HasCertificate
            ? CertificateResolver.Describe(Settings.RemoteAccess)
            : "No certificate set — the site is served over plain HTTP.";

        BindModeBox.ItemsSource = BindModes;
        BindAddressBox.Text = string.IsNullOrWhiteSpace(Settings.RemoteAccess.BindAddress)
            ? RemoteAccessSettings.LoopbackAddress
            : Settings.RemoteAccess.BindAddress;

        BindModeBox.SelectedItem = BindModes.FirstOrDefault(
            m => string.Equals(m.Address, BindAddressBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? BindModes[^1];

        BindAddressBox.TextChanged += (_, _) => RefreshExposureWarning();
        CertThumbprintBox.TextChanged += (_, _) => RefreshExposureWarning();
        CertPathBox.TextChanged += (_, _) => RefreshExposureWarning();

        RefreshExposureWarning();

        DeviceList.ItemsSource = _devices;
        RefreshRemoteStatus();
        RefreshDevices();
    }

    public AppSettings Settings { get; }

    /// <summary>A choice in the "Listen on" dropdown.</summary>
    /// <param name="Address">
    /// The value written to settings, or empty for "a specific address", which leaves the
    /// text box alone rather than overwriting what the user typed.
    /// </param>
    private sealed record BindMode(string Label, string Address)
    {
        public override string ToString() => Label;
    }

    private static readonly BindMode[] BindModes =
    {
        new("This machine only (127.0.0.1)", RemoteAccessSettings.LoopbackAddress),
        new("Every network interface (0.0.0.0)", RemoteAccessSettings.AllInterfacesAddress),
        new("A specific address…", string.Empty)
    };

    private void OnBindModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BindModeBox.SelectedItem is not BindMode mode)
        {
            return;
        }

        // The box stays editable in every mode: the presets fill it in, and picking
        // "a specific address" simply stops overwriting whatever is there.
        if (mode.Address.Length > 0)
        {
            BindAddressBox.Text = mode.Address;
        }

        RefreshExposureWarning();
    }

    /// <summary>
    /// Says plainly when the site is reachable from the network without TLS. Not a block:
    /// it is the user's machine and their call, but it should never be a surprise.
    /// </summary>
    private void RefreshExposureWarning()
    {
        if (ExposureWarning is null)
        {
            return;
        }

        var probe = new RemoteAccessSettings
        {
            BindAddress = BindAddressBox.Text,
            CertificateThumbprint = CertThumbprintBox.Text,
            CertificatePath = CertPathBox.Text
        };

        if (!probe.TryResolveBindAddress(out _, out var addressError) && addressError.Length > 0)
        {
            ExposureWarningText.Text = addressError;
            ExposureWarning.Visibility = Visibility.Visible;
            return;
        }

        if (probe.IsUnencryptedOnTheNetwork)
        {
            ExposureWarningText.Text =
                "This listens on the network with no certificate, so the site is served over "
                + "plain HTTP. Every request carries a device token, and anyone on the path "
                + "between a phone and this machine can read it and then control your servers.\n\n"
                + "Set a certificate above to serve HTTPS instead. If the port is forwarded, it "
                + "will be found and probed within hours, and from then on a device token is the "
                + "only thing standing in the way.";

            ExposureWarning.Visibility = Visibility.Visible;
            return;
        }

        ExposureWarning.Visibility = Visibility.Collapsed;
    }

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
                ? $"Running, bound to {_remote.ListeningOn}. On this machine, open {_remote.BrowsableAddress}."
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
        // Checked before anything is written. An unparseable address would otherwise fall
        // back to loopback, and the user would be left wondering why their forwarded port
        // reaches nothing.
        var probe = new RemoteAccessSettings { BindAddress = BindAddressBox.Text };
        if (!probe.TryResolveBindAddress(out _, out var addressError))
        {
            RefreshExposureWarning();

            MessageBox.Show(
                addressError,
                "Website address",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return;
        }

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
        Settings.RemoteAccess.BindAddress = BindAddressBox.Text.Trim();
        Settings.RemoteAccess.PublicAddress = RemotePublicAddressBox.Text.Trim();
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
