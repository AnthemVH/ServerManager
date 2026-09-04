using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ServerLauncher.App.Views;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Storage;
using ServerLauncher.Core.Supervision;
using ServerLauncher.Core.Updates;

namespace ServerLauncher.App.ViewModels;

/// <summary>Top-level view model: the server list, the selected server, and its actions.</summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ServerManager _manager;
    private readonly DispatcherTimer _consoleTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private readonly UpdateService _updateService = new();

    [ObservableProperty]
    private ServerViewModel? _selectedServer;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _commandInput = string.Empty;

    [ObservableProperty]
    private bool _autoScroll = true;

    // --- Updates ---

    [ObservableProperty]
    private ReleaseInfo? _availableUpdate;

    [ObservableProperty]
    private bool _isUpdating;

    [ObservableProperty]
    private double _updateProgress;

    [ObservableProperty]
    private string _updateProgressText = string.Empty;

    // --- ServerManager's own resource use ---

    [ObservableProperty]
    private double _appCpuPercent;

    [ObservableProperty]
    private double _appMemoryMegabytes;

    [ObservableProperty]
    private double _appManagedMemoryMegabytes;

    [ObservableProperty]
    private int _appThreadCount;

    [ObservableProperty]
    private int _appHandleCount;

    [ObservableProperty]
    private string _appUptimeText = "—";

    public MainViewModel(ServerManager manager)
    {
        _manager = manager;

        _manager.ServersChanged += OnServersChanged;
        _manager.BackupCompleted += OnBackupCompleted;
        _manager.AppHealthSampled += OnAppHealthSampled;

        // One shared timer drains console output for the selected server. Appending
        // per line straight from the capture thread would stall the UI on a chatty server.
        _consoleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _consoleTimer.Tick += (_, _) => DrainConsole();
        _consoleTimer.Start();

        _uptimeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uptimeTimer.Tick += (_, _) =>
        {
            foreach (var server in Servers)
            {
                server.RefreshUptime();
            }

            RefreshAppUptime();
        };
        _uptimeTimer.Start();
    }

    public ObservableCollection<ServerViewModel> Servers { get; } = new();

    public bool HasServers => Servers.Count > 0;

    /// <summary>Raised when new console output arrives, so the view can auto-scroll.</summary>
    public event Action? ConsoleUpdated;

    public void SyncServers()
    {
        var existing = Servers.ToDictionary(s => s.Id);
        var current = _manager.Instances.ToList();

        foreach (var instance in current)
        {
            if (existing.TryGetValue(instance.Id, out var viewModel))
            {
                viewModel.NotifyDefinitionChanged();
                existing.Remove(instance.Id);
            }
            else
            {
                Servers.Add(new ServerViewModel(instance, _manager.Settings.ConsoleBufferLines));
            }
        }

        foreach (var removed in existing.Values)
        {
            if (ReferenceEquals(SelectedServer, removed))
            {
                SelectedServer = null;
            }

            removed.Dispose();
            Servers.Remove(removed);
        }

        OnPropertyChanged(nameof(HasServers));

        SelectedServer ??= Servers.FirstOrDefault();
    }

    // The two-parameter overload is the one that hands us the previous selection;
    // the single-parameter form receives the incoming value instead.
    partial void OnSelectedServerChanging(ServerViewModel? oldValue, ServerViewModel? newValue) =>
        oldValue?.DetachConsole();

    partial void OnSelectedServerChanged(ServerViewModel? value)
    {
        value?.AttachConsole();
        ConsoleUpdated?.Invoke();
    }

    private void DrainConsole()
    {
        if (SelectedServer?.DrainConsole() == true)
        {
            ConsoleUpdated?.Invoke();
        }
    }

    private void OnServersChanged() => OnUiThread(SyncServers);

    private void OnBackupCompleted(ServerInstance instance, Core.Backup.BackupResult result) =>
        OnUiThread(() => StatusMessage = $"{instance.Definition.Name}: {result.Message}");

    // --- ServerManager's own resource use ---

    /// <summary>
    /// Compact readout for the status bar. The launcher supervises everything else, so a
    /// slow leak here eventually takes every server down with it.
    /// </summary>
    public string AppHealthSummary =>
        $"ServerManager: {AppCpuPercent:0.0}% CPU · {AppMemoryMegabytes:0} MB · up {AppUptimeText}";

    /// <summary>Recent launcher CPU samples, oldest first, for the sparkline.</summary>
    public IReadOnlyList<double> AppCpuHistory =>
        _manager.AppHealthHistory().Select(s => s.CpuPercent).ToList();

    private void OnAppHealthSampled(AppHealthSample sample) =>
        OnUiThread(() =>
        {
            AppCpuPercent = sample.CpuPercent;
            AppMemoryMegabytes = sample.WorkingSetMegabytes;
            AppManagedMemoryMegabytes = sample.ManagedMemoryMegabytes;
            AppThreadCount = sample.ThreadCount;
            AppHandleCount = sample.HandleCount;
            RefreshAppUptime();

            OnPropertyChanged(nameof(AppHealthSummary));
            OnPropertyChanged(nameof(AppCpuHistory));
        });

    private void RefreshAppUptime()
    {
        var uptime = DateTimeOffset.Now - _manager.AppStartedAt;

        AppUptimeText = uptime.TotalDays >= 1
            ? $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m"
            : $"{uptime.Hours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";

        OnPropertyChanged(nameof(AppHealthSummary));
    }

    // --- Commands ---

    [RelayCommand]
    private async Task StartAsync(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        try
        {
            StatusMessage = $"Starting {server.Name}…";
            await server.Instance.StartAsync();
            StatusMessage = $"{server.Name} started.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start {server.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopAsync(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        StatusMessage = $"Stopping {server.Name}…";
        await server.Instance.StopAsync();
        StatusMessage = $"{server.Name} stopped.";
    }

    [RelayCommand]
    private async Task RestartAsync(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        StatusMessage = $"Restarting {server.Name}…";
        await server.Instance.RestartAsync();
        StatusMessage = $"{server.Name} restarted.";
    }

    [RelayCommand]
    private void AddServer()
    {
        var definition = new ServerDefinition { Name = "New Server" };
        var dialog = new ServerEditorWindow(definition, isNew: true)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            var instance = _manager.Add(dialog.Definition);
            SyncServers();
            SelectedServer = Servers.FirstOrDefault(s => s.Id == instance.Id);
            StatusMessage = $"Added {dialog.Definition.Name}.";
        }
    }

    [RelayCommand]
    private void EditServer(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        // Edit a copy so cancelling leaves the live definition untouched.
        var dialog = new ServerEditorWindow(server.Definition.Clone(), isNew: false)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            _manager.Update(dialog.Definition);
            server.NotifyDefinitionChanged();
            StatusMessage = $"Updated {dialog.Definition.Name}.";
        }
    }

    [RelayCommand]
    private async Task RemoveServerAsync(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Remove '{server.Name}' from the launcher?\n\n" +
            "The server's script and files are left untouched.",
            "Remove server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        await _manager.RemoveAsync(server.Id);
        StatusMessage = $"Removed {server.Name}.";
    }

    [RelayCommand]
    private void SendCommand()
    {
        var server = SelectedServer;
        var text = CommandInput;

        if (server is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!server.Instance.SendCommand(text))
        {
            StatusMessage = "Server is not running; command not sent.";
            return;
        }

        CommandInput = string.Empty;
    }

    [RelayCommand]
    private async Task RunBackupAsync(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(server.Definition.BackupDestinationFolder))
        {
            StatusMessage = "Set a backup destination folder in this server's settings first.";
            return;
        }

        StatusMessage = $"Backing up {server.Name}…";
        await _manager.RunBackupAsync(server.Id);
    }

    [RelayCommand]
    private void OpenServerFolder(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        OpenInExplorer(server.Definition.ResolveWorkingDirectory());
    }

    [RelayCommand]
    private void OpenLogFolder(ServerViewModel? server)
    {
        server ??= SelectedServer;
        if (server is null)
        {
            return;
        }

        OpenInExplorer(AppPaths.LogDirectoryFor(server.Id));
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var dialog = new SettingsWindow(_manager.Settings)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            _manager.SaveSettings(dialog.Settings);
            StatusMessage = "Settings saved. Console and log limits apply to newly started servers.";
        }
    }

    [RelayCommand]
    private void ClearConsole() => SelectedServer?.ConsoleLines.Clear();

    // --- Updates ---

    public bool HasUpdate => AvailableUpdate is not null;

    public string CurrentVersionText => $"Version {UpdateService.DetectCurrentVersion().ToString(3)}";

    public string UpdateBannerText => AvailableUpdate is { } release
        ? $"Version {release.Version.ToString(3)} is available ({release.SizeDisplay})."
        : string.Empty;

    partial void OnAvailableUpdateChanged(ReleaseInfo? value)
    {
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(UpdateBannerText));
    }

    /// <summary>
    /// Checks for a newer release. Silent on failure when run automatically at startup —
    /// a flaky connection should not throw a dialog at someone who did not ask.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool announceResult)
    {
        var repository = _manager.Settings.UpdateRepository;

        if (string.IsNullOrWhiteSpace(repository))
        {
            if (announceResult)
            {
                StatusMessage = "Set a GitHub repository in Settings to enable update checks.";
            }

            return;
        }

        if (announceResult)
        {
            StatusMessage = "Checking for updates…";
        }

        var result = await _updateService
            .CheckAsync(repository, UpdateService.DetectCurrentVersion())
            .ConfigureAwait(true);

        AvailableUpdate = result.Release;

        if (announceResult || result.Status == UpdateCheckStatus.UpdateAvailable)
        {
            StatusMessage = result.Message;
        }
    }

    [RelayCommand]
    private Task CheckForUpdatesManuallyAsync() => CheckForUpdatesAsync(announceResult: true);

    [RelayCommand]
    private void DismissUpdate()
    {
        // Only clears the banner; the release is found again on the next check.
        AvailableUpdate = null;
    }

    [RelayCommand]
    private void ViewReleaseNotes()
    {
        var url = AvailableUpdate?.HtmlUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the release page: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is not { } release || IsUpdating)
        {
            return;
        }

        // Check writability before stopping anything: failing after a shutdown would
        // mean downtime for nothing.
        if (!UpdateInstaller.CanSelfUpdate(out var reason))
        {
            MessageBox.Show(reason, "Cannot update", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var running = _manager.Instances
            .Where(i => i.State is ServerState.Running or ServerState.Starting)
            .Select(i => "  • " + i.Definition.Name)
            .ToList();

        var warning = running.Count > 0
            ? "These servers will be stopped and will NOT restart automatically:\n\n"
              + string.Join("\n", running)
              + "\n\nThey are supervised by the launcher, so updating it takes them down.\n"
              + "Start them again once the new version has loaded.\n\n"
            : string.Empty;

        var confirm = MessageBox.Show(
            $"Install version {release.Version.ToString(3)}?\n\n"
            + warning
            + "The launcher will download the update, verify it, and restart.",
            "Install update",
            MessageBoxButton.OKCancel,
            running.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsUpdating = true;
        UpdateProgress = 0;
        UpdateProgressText = "Downloading…";

        try
        {
            var progress = new Progress<double>(fraction =>
            {
                UpdateProgress = fraction * 100d;
                UpdateProgressText = $"Downloading… {fraction * 100:0}%";
            });

            var downloadFolder = Path.Combine(AppPaths.ConfigRoot, "updates");
            var downloaded = await _updateService
                .DownloadAsync(release, downloadFolder, progress)
                .ConfigureAwait(true);

            UpdateProgressText = "Stopping servers…";
            await App.Current.ApplyUpdateAsync(downloaded).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            IsUpdating = false;
            UpdateProgressText = string.Empty;
            StatusMessage = $"Update failed: {ex.Message}";

            MessageBox.Show(
                $"The update was not installed.\n\n{ex.Message}\n\nYour servers were left alone.",
                "Update failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                StatusMessage = $"Folder not found: {path}";
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    public void Dispose()
    {
        _consoleTimer.Stop();
        _uptimeTimer.Stop();
        _manager.ServersChanged -= OnServersChanged;
        _manager.BackupCompleted -= OnBackupCompleted;
        _manager.AppHealthSampled -= OnAppHealthSampled;

        foreach (var server in Servers)
        {
            server.Dispose();
        }
    }
}
