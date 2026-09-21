using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ServerLauncher.Core.Models;

namespace ServerLauncher.App.ViewModels;

/// <summary>
/// The weekly schedule view: seven day columns holding every server's scheduled entries.
/// </summary>
/// <remarks>
/// Rebuilt wholesale on every refresh rather than patched. A schedule is a few dozen
/// entries at most, and rebuilding means an edit made anywhere — here, in a server's own
/// editor, or by removing a server — can never leave a stale card behind.
/// </remarks>
public sealed partial class ScheduleBoardViewModel : ObservableObject
{
    [ObservableProperty]
    private string _nextUpText = string.Empty;

    [ObservableProperty]
    private bool _hasAnyEntries;

    public ScheduleBoardViewModel()
    {
        foreach (var day in ScheduledTask.Ordered())
        {
            Days.Add(new ScheduleDayColumn(day));
        }
    }

    /// <summary>Monday to Sunday.</summary>
    public ObservableCollection<ScheduleDayColumn> Days { get; } = new();

    public void Refresh(IReadOnlyCollection<ServerDefinition> servers, DateTime now)
    {
        var today = ScheduledTask.ToFlag(now.DayOfWeek);

        foreach (var column in Days)
        {
            column.IsToday = column.Day == today;
            column.Entries.Clear();

            foreach (var entry in ScheduleBoard.ForDay(servers, column.Day))
            {
                column.Entries.Add(new ScheduleEntryItem(entry));
            }

            column.NotifyEntriesChanged();
        }

        HasAnyEntries = Days.Any(d => d.Entries.Count > 0);
        NextUpText = DescribeNext(ScheduleBoard.Next(servers, now), now);
    }

    private static string DescribeNext(ScheduleOccurrence? next, DateTime now)
    {
        if (next is null)
        {
            return "Nothing scheduled.";
        }

        var when = next.At.Date == now.Date
            ? $"today at {next.At:HH:mm}"
            : next.At.Date == now.Date.AddDays(1)
                ? $"tomorrow at {next.At:HH:mm}"
                : $"{next.At.ToString("dddd", CultureInfo.CurrentCulture)} at {next.At:HH:mm}";

        return $"Next: {ScheduledTask.DescribeAction(next.Task.Action)} {next.ServerName}, {when}";
    }
}

/// <summary>One day's column.</summary>
public sealed partial class ScheduleDayColumn : ObservableObject
{
    [ObservableProperty]
    private bool _isToday;

    public ScheduleDayColumn(ScheduleDays day)
    {
        Day = day;
        Name = ScheduleBoard.DayName(day);
    }

    public ScheduleDays Day { get; }

    public string Name { get; }

    public ObservableCollection<ScheduleEntryItem> Entries { get; } = new();

    public bool IsEmpty => Entries.Count == 0;

    public string CountText => Entries.Count switch
    {
        0 => string.Empty,
        1 => "1 entry",
        var n => $"{n} entries"
    };

    internal void NotifyEntriesChanged()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>One entry as a card in one column.</summary>
public sealed class ScheduleEntryItem
{
    public ScheduleEntryItem(ScheduleBoardEntry entry)
    {
        ServerId = entry.ServerId;
        ServerName = entry.ServerName;
        Task = entry.Task;
        Day = entry.Day;

        Time = ScheduledTask.IsValidTime(entry.Task.Time) ? entry.Task.Time.Trim() : "--:--";
        ActionText = ScheduledTask.DescribeAction(entry.Task.Action);
        IsActive = entry.Task.Enabled;

        // Said on the card because deleting from this column may or may not touch the
        // other days, and the user should know there are other days before choosing.
        var otherDays = entry.Task.Days & ~entry.Day;
        // "every day" reads better than "also Tue, Wed, Thu, Fri, Sat, Sun".
        OtherDaysText = otherDays == ScheduleDays.None
            ? string.Empty
            : entry.Task.Days is ScheduleDays.EveryDay or ScheduleDays.Weekdays or ScheduleDays.Weekend
                ? ScheduledTask.DescribeDays(entry.Task.Days)
                : "also " + ScheduledTask.DescribeDays(otherDays);

        StatusText = IsActive ? string.Empty : "Paused";
    }

    public Guid ServerId { get; }

    public string ServerName { get; }

    public ScheduledTask Task { get; }

    /// <summary>The column this card sits in, which "remove from this day" acts on.</summary>
    public ScheduleDays Day { get; }

    public string DayName => ScheduleBoard.DayName(Day);

    public string Time { get; }

    public string ActionText { get; }

    /// <summary>Named apart from UIElement.IsEnabled, which a DataTrigger could confuse it with.</summary>
    public bool IsActive { get; }

    public string OtherDaysText { get; }

    public string StatusText { get; }

    public bool RunsOnOtherDays => OtherDaysText.Length > 0;

    /// <summary>The whole entry in one line, for the card's tooltip.</summary>
    public string Summary => $"{ServerName}: {Task.Describe()}{(IsActive ? string.Empty : " (paused)")}";
}
