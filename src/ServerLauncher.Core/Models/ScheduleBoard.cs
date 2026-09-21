using System.Globalization;

namespace ServerLauncher.Core.Models;

/// <summary>One scheduled task as it appears in one day's column of the weekly view.</summary>
/// <param name="ServerId">The server the task belongs to.</param>
/// <param name="ServerName">That server's name, for display.</param>
/// <param name="Task">The task itself. A task on three days appears in three columns.</param>
/// <param name="Day">The column this appearance is in.</param>
public sealed record ScheduleBoardEntry(Guid ServerId, string ServerName, ScheduledTask Task, ScheduleDays Day);

/// <summary>The next time something on the schedule will fire.</summary>
public sealed record ScheduleOccurrence(Guid ServerId, string ServerName, ScheduledTask Task, DateTime At);

/// <summary>
/// Reads every server's schedule as one week, which is how people think about it: "what
/// happens on Thursday" rather than "what does each server do".
/// </summary>
public static class ScheduleBoard
{
    /// <summary>Every task, from every server, that runs on the given day — earliest first.</summary>
    /// <remarks>
    /// Disabled tasks are included: the board is where you would go to turn one back on,
    /// so hiding them would make them unreachable except through each server's editor.
    /// </remarks>
    public static IReadOnlyList<ScheduleBoardEntry> ForDay(IEnumerable<ServerDefinition> servers, ScheduleDays day)
    {
        ArgumentNullException.ThrowIfNull(servers);

        return servers
            .SelectMany(server => server.Schedule
                .Where(task => task.Days.HasFlag(day) && day != ScheduleDays.None)
                .Select(task => new ScheduleBoardEntry(server.Id, server.Name, task, day)))
            .OrderBy(entry => SortKey(entry.Task.Time))
            .ThenBy(entry => entry.ServerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => entry.Task.Action)
            .ToList();
    }

    /// <summary>
    /// The soonest enabled task after <paramref name="now"/>, or null when nothing is
    /// scheduled at all.
    /// </summary>
    /// <remarks>
    /// Strictly after: a task due this very minute has either just fired or is about to,
    /// and in both cases "next" should mean the one after it.
    /// </remarks>
    public static ScheduleOccurrence? Next(IEnumerable<ServerDefinition> servers, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(servers);

        ScheduleOccurrence? best = null;

        foreach (var server in servers)
        {
            foreach (var task in server.Schedule)
            {
                if (!task.Enabled || task.Days == ScheduleDays.None
                    || !TryParseTime(task.Time, out var time))
                {
                    continue;
                }

                // Eight days, not seven: a weekly task whose time today has already
                // passed next fires on this same weekday a week from now.
                for (var offset = 0; offset <= 7; offset++)
                {
                    var date = now.Date.AddDays(offset);
                    if (!task.Days.HasFlag(ScheduledTask.ToFlag(date.DayOfWeek)))
                    {
                        continue;
                    }

                    var at = date.Add(time.ToTimeSpan());
                    if (at <= now)
                    {
                        continue;
                    }

                    if (best is null || at < best.At)
                    {
                        best = new ScheduleOccurrence(server.Id, server.Name, task, at);
                    }

                    break;
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Why a task cannot be saved against this server, or null if it can.
    /// </summary>
    /// <remarks>
    /// Each of these would otherwise be saved as an entry that looks configured and
    /// silently does nothing: a bad time is never due, no days means never, and an
    /// update or backup with nothing to run or nowhere to write fails at 5am unseen.
    /// </remarks>
    public static string? Validate(ScheduledTask task, ServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(server);

        if (!ScheduledTask.IsValidTime(task.Time))
        {
            return "Enter a time in HH:mm form on a 24-hour clock, such as 05:00 or 17:30.";
        }

        if (task.Days == ScheduleDays.None)
        {
            return "Tick at least one day.";
        }

        if (task.Action == ScheduledAction.RunUpdate && !server.HasUpdateScript)
        {
            return $"{server.Name} has no update script. Set one in its settings first.";
        }

        if (task.Action == ScheduledAction.Backup && string.IsNullOrWhiteSpace(server.BackupDestinationFolder))
        {
            return $"{server.Name} has no backup destination folder. Set one in its settings first.";
        }

        return null;
    }

    /// <summary>Parses a stored "HH:mm" time.</summary>
    public static bool TryParseTime(string? value, out TimeOnly time)
    {
        time = default;

        return !string.IsNullOrWhiteSpace(value)
               && TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out time);
    }

    /// <summary>The full name of a single day, for column headings.</summary>
    public static string DayName(ScheduleDays day) => day switch
    {
        ScheduleDays.Monday => "Monday",
        ScheduleDays.Tuesday => "Tuesday",
        ScheduleDays.Wednesday => "Wednesday",
        ScheduleDays.Thursday => "Thursday",
        ScheduleDays.Friday => "Friday",
        ScheduleDays.Saturday => "Saturday",
        ScheduleDays.Sunday => "Sunday",
        _ => day.ToString()
    };

    // An unusable time sorts last, so it collects at the bottom of the column where it is
    // easy to spot rather than masquerading as midnight.
    private static TimeSpan SortKey(string? time) =>
        TryParseTime(time, out var parsed) ? parsed.ToTimeSpan() : TimeSpan.MaxValue;
}
