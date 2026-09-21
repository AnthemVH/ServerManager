using System.Windows;
using System.Windows.Controls;
using ServerLauncher.App.ViewModels;
using ServerLauncher.Core.Models;

namespace ServerLauncher.App.Views;

/// <summary>Adds or edits one schedule entry from the weekly view.</summary>
/// <remarks>
/// Works on a copy of the task, so cancelling changes nothing. The server can be changed
/// while editing, which moves the entry from one server's schedule to another's.
/// </remarks>
public partial class ScheduleEntryWindow : Window
{
    private readonly ScheduleRow _row;

    /// <param name="servers">Every server, for the server picker.</param>
    /// <param name="serverId">The server the entry belongs to, or will belong to.</param>
    /// <param name="task">The entry to edit, or a new one with defaults filled in.</param>
    /// <param name="isNew">Whether this adds an entry rather than editing one.</param>
    public ScheduleEntryWindow(
        IReadOnlyList<ServerDefinition> servers, Guid serverId, ScheduledTask task, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(task);

        InitializeComponent();

        Title = isNew ? "Add schedule entry" : "Edit schedule entry";

        _row = new ScheduleRow(task.Clone());
        DataContext = _row;

        ServerBox.ItemsSource = servers;
        ServerBox.SelectedItem = servers.FirstOrDefault(s => s.Id == serverId) ?? servers.FirstOrDefault();

        ValidationText.Text = string.Empty;
    }

    /// <summary>The server chosen, once saved.</summary>
    public ServerDefinition? SelectedServer => ServerBox.SelectedItem as ServerDefinition;

    /// <summary>The entry as edited, once saved. Keeps the original id.</summary>
    public ScheduledTask Task => _row.ToTask();

    /// <summary>
    /// Checks the entry against the chosen server. Public so the rules can be exercised
    /// without showing a modal dialog.
    /// </summary>
    public bool TryValidate(out string error)
    {
        if (SelectedServer is not { } server)
        {
            error = "Choose a server.";
            return false;
        }

        error = ScheduleBoard.Validate(Task, server) ?? string.Empty;
        return error.Length == 0;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!TryValidate(out var error))
        {
            ValidationText.Text = error;
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDayPreset(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name } && Enum.TryParse<ScheduleDays>(name, out var days))
        {
            _row.Days = days;
            ValidationText.Text = string.Empty;
        }
    }

    // Clears a stale error as soon as the user starts fixing it, rather than leaving it
    // up until the next Save.
    private void OnFieldChanged(object sender, RoutedEventArgs e)
    {
        if (ValidationText is not null)
        {
            ValidationText.Text = string.Empty;
        }
    }

    private void OnServerChanged(object sender, SelectionChangedEventArgs e) => OnFieldChanged(sender, e);
}
