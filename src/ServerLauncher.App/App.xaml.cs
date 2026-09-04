using System.Windows;
using ServerLauncher.App.Remote;
using ServerLauncher.App.TrayIcon;
using ServerLauncher.App.ViewModels;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;
using ServerLauncher.Core.Updates;

namespace ServerLauncher.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\ServerLauncher.SingleInstance";

    /// <summary>Signalled by a second launch to bring the running instance forward.</summary>
    private const string ShowWindowEventName = @"Global\ServerLauncher.ShowWindow";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showWindowRequest;
    private ManualResetEvent? _watcherShutdown;
    private ServerManager? _manager;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIconController? _tray;
    private RemoteAccessService? _remote;
    private bool _exiting;

    public new static App Current => (App)Application.Current;

    public AppSettings Settings => _manager?.Settings ?? new AppSettings();

    /// <summary>The remote control API, or null before startup has run.</summary>
    public RemoteAccessService? Remote => _remote;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // When relaunched by an update, the superseded process may still be shutting
        // down and still holding the mutex. Wait for it rather than refusing to start.
        if (UpdateInstaller.GetProcessIdToWaitFor(e.Args) is { } previousPid)
        {
            UpdateInstaller.WaitForPreviousInstance(previousPid, TimeSpan.FromSeconds(30));
        }

        // This build started successfully, so the displaced one is safe to remove.
        UpdateInstaller.CleanUpPreviousVersion();

        // Two copies supervising the same servers would fight over process ownership
        // and produce duplicate restarts, so only one instance is allowed.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Asking the running copy to show itself is far more useful than telling the
            // user it is already running and leaving them with nothing to click.
            SignalRunningInstance();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}",
                "ServerManager",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            args.Handled = true;
        };

        _manager = new ServerManager();
        _viewModel = new MainViewModel(_manager);
        _window = new MainWindow(_viewModel);
        _window.HiddenToTray += OnWindowHiddenToTray;

        // Created before any decision about showing the window: the tray icon is the only
        // way back to a launcher that starts minimised, so it must never depend on a
        // window having been shown.
        _tray = new TrayIconController(
            _viewModel,
            onOpen: ShowMainWindow,
            onExit: RequestExit,
            onToggle: ToggleMainWindow);

        StartShowWindowWatcher();

        var startHidden = _manager.Settings.StartMinimised
                          || e.Args.Any(a => a.Equals("--minimised", StringComparison.OrdinalIgnoreCase));

        if (startHidden && !_tray.IsCreated)
        {
            // Refusing to hide with no way back is better than becoming an invisible
            // process the user can only find in Task Manager.
            startHidden = false;
        }

        if (!startHidden)
        {
            _window.Show();
        }

        await _manager.InitialiseAsync();
        _viewModel.SyncServers();

        _remote = new RemoteAccessService(_manager);
        await ApplyRemoteAccessAsync();

        if (_manager.Settings.CheckForUpdatesOnStartup)
        {
            // Deliberately not awaited into startup: a slow or unreachable GitHub must
            // never delay the servers coming up.
            _ = _viewModel.CheckForUpdatesAsync(announceResult: false);
        }
    }

    /// <summary>
    /// Starts or stops the remote API to match the settings, surfacing any failure rather
    /// than leaving the user believing remote access is on when it is not.
    /// </summary>
    public async Task ApplyRemoteAccessAsync(bool reportFailure = false)
    {
        if (_remote is null || _manager is null)
        {
            return;
        }

        var ok = await _remote.ApplyAsync(_manager.Settings).ConfigureAwait(true);

        if (!ok && reportFailure && _remote.LastError is { } error)
        {
            MessageBox.Show(
                $"Remote access could not be started.\n\n{error}",
                "Remote access",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnWindowHiddenToTray() =>
        _tray?.ShowMessage("Still running", "ServerManager is in the tray and your servers keep running.");

    private void ShowMainWindow() => _window?.RestoreFromTray();

    private void ToggleMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (_window.IsVisible)
        {
            _window.HideToTray();
        }
        else
        {
            _window.RestoreFromTray();
        }
    }

    private void SignalRunningInstance()
    {
        try
        {
            using var handle = EventWaitHandle.OpenExisting(ShowWindowEventName);
            handle.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The other instance is older than this feature, or is shutting down.
            MessageBox.Show(
                "ServerManager is already running. Check the system tray.",
                "Already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show(
                "ServerManager is already running, possibly for another user.",
                "Already running",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>Watches for a second launch asking this instance to show its window.</summary>
    private void StartShowWindowWatcher()
    {
        _showWindowRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
        _watcherShutdown = new ManualResetEvent(false);

        var handles = new WaitHandle[] { _showWindowRequest, _watcherShutdown };

        var thread = new Thread(() =>
        {
            while (true)
            {
                var signalled = WaitHandle.WaitAny(handles);
                if (signalled != 0)
                {
                    return;
                }

                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        })
        {
            IsBackground = true,
            Name = "ShowWindowWatcher"
        };

        thread.Start();
    }

    /// <summary>
    /// Stops every server, swaps in the downloaded build, and restarts.
    /// Called only after the user has approved the update.
    /// </summary>
    public async Task ApplyUpdateAsync(string downloadedExecutable)
    {
        if (_manager is null)
        {
            return;
        }

        // Stop cleanly first: the job objects would kill these trees when this process
        // exits anyway, and a clean stop lets each server save its world properly.
        await _manager.StopAllAsync().ConfigureAwait(true);

        _exiting = true;

        if (_window is not null)
        {
            _window.AllowClose = true;
        }

        // Hands over to the new build, which waits for this process to exit before
        // taking the single-instance mutex.
        UpdateInstaller.ApplyAndRestart(downloadedExecutable);

        Shutdown();
    }

    /// <summary>
    /// Exits the application, stopping every supervised server first.
    /// </summary>
    /// <remarks>
    /// Exiting always stops the servers, and that is not a policy choice we could
    /// reverse with a prompt. Each server's process tree lives in a job object created
    /// with KILL_ON_JOB_CLOSE, which is what guarantees no orphans if this app crashes.
    /// Windows closes those handles when the process ends, so the trees go down with us
    /// no matter how we exit. Stopping them cleanly first is strictly better than
    /// letting the OS kill them mid-write, so the only real question is whether the
    /// user meant to exit at all — closing the window just hides to the tray instead.
    /// </remarks>
    public async void RequestExit()
    {
        if (_exiting || _manager is null)
        {
            return;
        }

        if (_manager.AnyRunning())
        {
            var running = _manager.Instances
                .Where(i => i.State is ServerState.Running or ServerState.Starting or ServerState.Stopping)
                .Select(i => "  • " + i.Definition.Name);

            var choice = MessageBox.Show(
                "Exiting will shut down these running servers:\n\n" +
                string.Join("\n", running) +
                "\n\nThey will be stopped cleanly using each server's stop command.\n" +
                "To keep them running, close the window instead — ServerManager stays in the tray.\n\n" +
                "Exit and stop all servers?",
                "Exit ServerManager",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (choice != MessageBoxResult.OK)
            {
                return;
            }

            _exiting = true;

            if (_window is not null)
            {
                _window.RestoreFromTray();
            }

            await _manager.StopAllAsync();
        }

        _exiting = true;

        if (_window is not null)
        {
            _window.AllowClose = true;
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _watcherShutdown?.Set();

        _remote?.Dispose();
        _tray?.Dispose();
        _viewModel?.Dispose();
        _manager?.Dispose();

        _showWindowRequest?.Dispose();
        _watcherShutdown?.Dispose();
        _singleInstanceMutex?.Dispose();

        base.OnExit(e);
    }
}
