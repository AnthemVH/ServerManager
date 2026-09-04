using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ServerLauncher.App.ViewModels;

namespace ServerLauncher.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();

        DataContext = viewModel;
        viewModel.ConsoleUpdated += OnConsoleUpdated;
    }

    /// <summary>Set by the application when a real exit is under way, so Close is not intercepted.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Raised when the window wants to hide itself, so the app can notify from the tray.</summary>
    public event Action? HiddenToTray;

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
        HiddenToTray?.Invoke();
    }

    public void RestoreFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

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
}
