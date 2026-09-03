using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ServerLauncher.App.ViewModels;
using ServerLauncher.Core.Models;

namespace ServerLauncher.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>Tray submenu handlers, tracked so they can be detached on rebuild.</summary>
    private readonly List<(ServerViewModel Server, PropertyChangedEventHandler Handler)> _trayMenuSubscriptions = new();

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        DataContext = viewModel;
        viewModel.ConsoleUpdated += OnConsoleUpdated;
        viewModel.Servers.CollectionChanged += (_, _) => RebuildTrayServerMenu();

        RebuildTrayServerMenu();
    }

    /// <summary>Set by the application when a real exit is under way, so Close is not intercepted.</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window must not stop the servers; the app keeps supervising
        // from the tray until the user explicitly exits.
        if (!AllowClose && App.Current.Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _viewModel.ConsoleUpdated -= OnConsoleUpdated;
        base.OnClosing(e);
    }

    public void HideToTray()
    {
        Hide();
        TrayIcon.ShowNotification(
            "Still running",
            "Server Launcher is in the tray and your servers keep running.",
            H.NotifyIcon.Core.NotificationIcon.Info);
    }

    public void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Focus();
    }

    private void OnConsoleUpdated()
    {
        if (!_viewModel.AutoScroll)
        {
            return;
        }

        var items = ConsoleList.Items;
        if (items.Count > 0)
        {
            ConsoleList.ScrollIntoView(items[items.Count - 1]);
        }
    }

    private void OnCommandBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        if (_viewModel.SendCommandCommand.CanExecute(null))
        {
            _viewModel.SendCommandCommand.Execute(null);
        }
    }

    // --- Tray ---

    private void OnTrayLeftClick(object sender, RoutedEventArgs e)
    {
        if (IsVisible)
        {
            HideToTray();
        }
        else
        {
            RestoreFromTray();
        }
    }

    private void OnTrayOpen(object sender, RoutedEventArgs e) => RestoreFromTray();

    private void OnTrayExit(object sender, RoutedEventArgs e) => App.Current.RequestExit();

    /// <summary>
    /// Rebuilds the tray submenu so each server can be started or stopped without
    /// opening the window.
    /// </summary>
    private void RebuildTrayServerMenu()
    {
        // Detach the previous round of handlers first: the menu is rebuilt whenever the
        // server list changes, and re-subscribing without this would leave handlers
        // behind holding on to discarded menu items.
        foreach (var (server, handler) in _trayMenuSubscriptions)
        {
            server.PropertyChanged -= handler;
        }

        _trayMenuSubscriptions.Clear();
        TrayServersMenu.Items.Clear();

        if (_viewModel.Servers.Count == 0)
        {
            TrayServersMenu.Items.Add(new MenuItem { Header = "(none configured)", IsEnabled = false });
            return;
        }

        foreach (var server in _viewModel.Servers)
        {
            var item = new MenuItem { Header = server.Name };

            var start = new MenuItem { Header = "Start" };
            start.Click += async (_, _) => await server.Instance.StartAsync();

            var stop = new MenuItem { Header = "Stop" };
            stop.Click += async (_, _) => await server.Instance.StopAsync();

            var restart = new MenuItem { Header = "Restart" };
            restart.Click += async (_, _) => await server.Instance.RestartAsync();

            var status = new MenuItem { Header = server.StatusText, IsEnabled = false };

            // Keep the submenu labels current as the server changes state.
            PropertyChangedEventHandler handler = (_, args) =>
            {
                if (args.PropertyName is nameof(ServerViewModel.StatusText) or nameof(ServerViewModel.State))
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        status.Header = server.StatusText;
                        start.IsEnabled = server.CanStart;
                        stop.IsEnabled = server.CanStop;
                        restart.IsEnabled = server.CanStop;
                    });
                }
                else if (args.PropertyName == nameof(ServerViewModel.Name))
                {
                    Dispatcher.BeginInvoke(() => item.Header = server.Name);
                }
            };

            server.PropertyChanged += handler;
            _trayMenuSubscriptions.Add((server, handler));

            start.IsEnabled = server.CanStart;
            stop.IsEnabled = server.CanStop;
            restart.IsEnabled = server.CanStop;

            item.Items.Add(status);
            item.Items.Add(new Separator());
            item.Items.Add(start);
            item.Items.Add(stop);
            item.Items.Add(restart);

            TrayServersMenu.Items.Add(item);
        }
    }
}
