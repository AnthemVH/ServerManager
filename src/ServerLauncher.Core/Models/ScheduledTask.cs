using System.Globalization;

namespace ServerLauncher.Core.Models;

/// <summary>Days a scheduled task runs on.</summary>
/// <remarks>
/// Flags rather than a single day, because "twice a week" is the common case and
/// expressing it as two separate tasks would make editing it twice the work.
/// </remarks>
[Flags]
public enum ScheduleDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,

    Weekdays = Monday | Tuesday | Wednesday | Thursday | Friday,
    Weekend = Saturday | Sunday,
    EveryDay = Weekdays | Weekend
}

/// <summary>What a scheduled task does when it fires.</summary>
public enum ScheduledAction
{
    Start,
    Stop,
    Restart,
    RunUpdate,
    Backup
}

/// <summary>
/// One entry in a server's schedule: an action, a time of day, and the days it applies to.
/// </summary>
public sealed class ScheduledTask
{
    /// <summary>
    /// Identifies this task for the once-per-day fire guard, so editing the list does not
    /// make an already-fired task run again.
    /// </summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public bool Enabled { get; set; } = true;

    public ScheduledAction Action { get; set; } = ScheduledAction.Restart;

    /// <summary>Time of day as "HH:mm" on a 24-hour clock.</summary>
    public string Time { get; set; } = string.Empty;

    public ScheduleDays Days { get; set; } = ScheduleDays.EveryDay;

    /// <summary>Whether this task would fire at the given moment.</summary>
    /// <remarks>
    /// Compares formatted strings rather than parsing, matching how the schedule tick has
    /// always worked: it runs once a minute and a task fires when the clock reads its time.
    /// </remarks>
    public bool IsDue(DateTime now)
    {
        if (!Enabled || !IsValidTime(Time))
        {
            return false;
        }

        return Days.HasFlag(ToFlag(now.DayOfWeek)) && FormatTime(now) == Time.Trim();
    }

    /// <summary>
    /// Formats a moment the way schedule times are stored.
    /// </summary>
    /// <remarks>
    /// Invariant culture is not optional: ":" in a custom format string is the culture's
    /// time separator, so a machine set to a locale using "." would silently stop matching
    /// every schedule that was saved on a machine that uses ":".
    /// </remarks>
    public static string FormatTime(DateTime moment) =>
        moment.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Whether a string is a time this schedule can actually fire on.</summary>
    public static bool IsValidTime(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _);

    public static ScheduleDays ToFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => ScheduleDays.Monday,
        DayOfWeek.Tuesday => ScheduleDays.Tuesday,
        DayOfWeek.Wednesday => ScheduleDays.Wednesday,
        DayOfWeek.Thursday => ScheduleDays.Thursday,
        DayOfWeek.Friday => ScheduleDays.Friday,
        DayOfWeek.Saturday => ScheduleDays.Saturday,
        _ => ScheduleDays.Sunday
    };

    /// <summary>A one-line summary for the schedule list, e.g. "Restart · 05:00 · Mon, Thu".</summary>
    public string Describe() =>
        $"{DescribeAction(Action)} · {(IsValidTime(Time) ? Time.Trim() : "no time set")} · {DescribeDays(Days)}";

    public static string DescribeAction(ScheduledAction action) => action switch
    {
        ScheduledAction.Start => "Start",
        ScheduledAction.Stop => "Stop",
        ScheduledAction.Restart => "Restart",
        ScheduledAction.RunUpdate => "Run update script",
        ScheduledAction.Backup => "Back up",
        _ => action.ToString()
    };

    public static string DescribeDays(ScheduleDays days) => days switch
    {
        ScheduleDays.None => "never",
        ScheduleDays.EveryDay => "every day",
        ScheduleDays.Weekdays => "weekdays",
        ScheduleDays.Weekend => "weekends",
        _ => string.Join(", ", Ordered().Where(d => days.HasFlag(d)).Select(Abbreviate))
    };

    /// <summary>The individual days, Monday first, for building day pickers.</summary>
    public static IReadOnlyList<ScheduleDays> Ordered() => new[]
    {
        ScheduleDays.Monday, ScheduleDays.Tuesday, ScheduleDays.Wednesday, ScheduleDays.Thursday,
        ScheduleDays.Friday, ScheduleDays.Saturday, ScheduleDays.Sunday
    };

    public static string Abbreviate(ScheduleDays day) => day switch
    {
        ScheduleDays.Monday => "Mon",
        ScheduleDays.Tuesday => "Tue",
        ScheduleDays.Wednesday => "Wed",
        ScheduleDays.Thursday => "Thu",
        ScheduleDays.Friday => "Fri",
        ScheduleDays.Saturday => "Sat",
        ScheduleDays.Sunday => "Sun",
        _ => day.ToString()
    };

    public ScheduledTask Clone() => (ScheduledTask)MemberwiseClone();
}
