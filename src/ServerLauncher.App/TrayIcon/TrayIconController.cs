using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using ServerLauncher.App.ViewModels;

namespace ServerLauncher.App.TrayIcon;

/// <summary>
/// Owns the notification-area icon for the whole application.
/// </summary>
/// <remarks>
/// This deliberately does NOT live inside a window. A TaskbarIcon declared in a window's
/// XAML only creates its Win32 icon once that window loads, so starting minimised — the
/// normal case on a dedicated box — produced a running process with no window and no tray
/// icon at all: invisible, yet still holding the single-instance mutex. Creating it here
/// and calling ForceCreate makes the icon exist regardless of whether a window is ever
/// shown.
/// </remarks>
public sealed class TrayIconController : IDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Action _onOpen;
    private readonly Action _onExit;
    private readonly Action _onToggle;
    private readonly Action _onOpenBrowser;
    private readonly List<(ServerViewModel Server, PropertyChangedEventHandler Handler)> _subscriptions = new();

    private TaskbarIcon? _icon;
    private MenuItem? _serversMenu;
    private bool _disposed;

    public TrayIconController(
        MainViewModel viewModel,
        Action onOpen,
        Action onExit,
        Action onToggle,
        Action? onOpenBrowser = null)
    {
        _viewModel = viewModel;
        _onOpen = onOpen;
        _onExit = onExit;
        _onToggle = onToggle;
        _onOpenBrowser = onOpenBrowser ?? (() => { });

        Create();

        _viewModel.Servers.CollectionChanged += (_, _) => RebuildServerMenu();
        RebuildServerMenu();
    }

    /// <summary>True when the icon was created successfully and is visible.</summary>
    public bool IsCreated => _icon is not null;

    private void Create()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "ServerManager",
            NoLeftClickDelay = true,
            IconSource = LoadIcon()
        };

        _icon.TrayLeftMouseUp += (_, _) => _onToggle();
        _icon.ContextMenu = BuildContextMenu();

        // Without this the icon is only created when a hosting element loads, which never
        // happens when the app starts straight to the tray.
        _icon.ForceCreate();
    }

    /// <summary>
    /// Loads the tray image. The pack URI names this assembly explicitly rather than
    /// relying on the entry assembly, which is only the app when the app is what started
    /// the process.
    /// </summary>
    private static BitmapImage? LoadIcon()
    {
        try
        {
            var assembly = typeof(TrayIconController).Assembly.GetName().Name;
            return new BitmapImage(
                new Uri($"pack://application:,,,/{assembly};component/Assets/app.ico", UriKind.Absolute));
        }
        catch (Exception)
        {
            // A missing image must not cost us the icon itself; without a tray entry the
            // app would be unreachable when started minimised.
            return null;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var open = new MenuItem { Header = "Open ServerManager", FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => _onOpen();

        // Reachable without opening the window, which on a server is usually hidden.
        var browser = new MenuItem { Header = "Open browser interface" };
        browser.Click += (_, _) => _onOpenBrowser();

        _serversMenu = new MenuItem { Header = "Servers" };

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => _onExit();

        menu.Items.Add(open);
        menu.Items.Add(browser);
        menu.Items.Add(new Separator());
        menu.Items.Add(_serversMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);

        return menu;
    }

    /// <summary>Shows a balloon notification, if the icon exists.</summary>
    public void ShowMessage(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message, NotificationIcon.Info);
        }
        catch (Exception)
        {
            // A notification failing must never take the app down.
        }
    }

    /// <summary>
    /// Rebuilds the per-server submenu so servers can be started and stopped without
    /// opening the window.
    /// </summary>
    private void RebuildServerMenu()
    {
        if (_serversMenu is null)
        {
            return;
        }

        // Detach the previous round first: the menu is rebuilt whenever the server list
        // changes, and re-subscribing without this leaves handlers holding discarded items.
        foreach (var (server, handler) in _subscriptions)
        {
            server.PropertyChanged -= handler;
        }

        _subscriptions.Clear();
        _serversMenu.Items.Clear();

        if (_viewModel.Servers.Count == 0)
        {
            _serversMenu.Items.Add(new MenuItem { Header = "(none configured)", IsEnabled = false });
            return;
        }

        foreach (var server in _viewModel.Servers)
        {
            _serversMenu.Items.Add(BuildServerMenu(server));
        }
    }

    private MenuItem BuildServerMenu(ServerViewModel server)
    {
        var item = new MenuItem { Header = server.Name };

        var status = new MenuItem { Header = server.StatusText, IsEnabled = false };

        var start = new MenuItem { Header = "Start", IsEnabled = server.CanStart };
        start.Click += async (_, _) => await server.Instance.StartAsync();

        var stop = new MenuItem { Header = "Stop", IsEnabled = server.CanStop };
        stop.Click += async (_, _) => await server.Instance.StopAsync();

        var restart = new MenuItem { Header = "Restart", IsEnabled = server.CanStop };
        restart.Click += async (_, _) => await server.Instance.RestartAsync();

        PropertyChangedEventHandler handler = (_, args) =>
        {
            if (args.PropertyName is nameof(ServerViewModel.StatusText) or nameof(ServerViewModel.State))
            {
                OnUiThread(() =>
                {
                    status.Header = server.StatusText;
                    start.IsEnabled = server.CanStart;
                    stop.IsEnabled = server.CanStop;
                    restart.IsEnabled = server.CanStop;
                });
            }
            else if (args.PropertyName == nameof(ServerViewModel.Name))
            {
                OnUiThread(() => item.Header = server.Name);
            }
        };

        server.PropertyChanged += handler;
        _subscriptions.Add((server, handler));

        item.Items.Add(status);
        item.Items.Add(new Separator());
        item.Items.Add(start);
        item.Items.Add(stop);
        item.Items.Add(restart);

        return item;
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var (server, handler) in _subscriptions)
        {
            server.PropertyChanged -= handler;
        }

        _subscriptions.Clear();

        // Removes the icon from the notification area rather than leaving a ghost behind.
        _icon?.Dispose();
        _icon = null;
    }
}
