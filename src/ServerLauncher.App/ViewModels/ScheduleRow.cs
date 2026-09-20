using System.ComponentModel;
using System.Runtime.CompilerServices;
using ServerLauncher.Core.Models;

namespace ServerLauncher.App.ViewModels;

/// <summary>One entry in the server editor's schedule list.</summary>
/// <remarks>
/// The seven days are separate properties rather than a bound flags value because a row of
/// checkboxes is how people read "twice a week", and WPF has no native way to bind one
/// checkbox to one bit of a flags enum without a converter per day.
/// </remarks>
public sealed class ScheduleRow : INotifyPropertyChanged
{
    /// <summary>The action choices, with the wording shown in the dropdown.</summary>
    public sealed record ActionChoice(ScheduledAction Value, string Label);

    public static IReadOnlyList<ActionChoice> ActionChoices { get; } =
        Enum.GetValues<ScheduledAction>()
            .Select(a => new ActionChoice(a, ScheduledTask.DescribeAction(a)))
            .ToList();

    private ScheduledAction _action;
    private string _time;
    private bool _enabled;
    private ScheduleDays _days;

    public ScheduleRow(ScheduledTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        Id = task.Id;
        _action = task.Action;
        _time = task.Time;
        _enabled = task.Enabled;
        _days = task.Days;
    }

    /// <summary>
    /// Carried through editing so the once-per-day fire guard keeps tracking the same
    /// task: a task that already ran today must not run again because it was saved.
    /// </summary>
    public Guid Id { get; }

    public ScheduledAction Action
    {
        get => _action;
        set => Set(ref _action, value);
    }

    public string Time
    {
        get => _time;
        set => Set(ref _time, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public bool Monday
    {
        get => Has(ScheduleDays.Monday);
        set => SetDay(ScheduleDays.Monday, value);
    }

    public bool Tuesday
    {
        get => Has(ScheduleDays.Tuesday);
        set => SetDay(ScheduleDays.Tuesday, value);
    }

    public bool Wednesday
    {
        get => Has(ScheduleDays.Wednesday);
        set => SetDay(ScheduleDays.Wednesday, value);
    }

    public bool Thursday
    {
        get => Has(ScheduleDays.Thursday);
        set => SetDay(ScheduleDays.Thursday, value);
    }

    public bool Friday
    {
        get => Has(ScheduleDays.Friday);
        set => SetDay(ScheduleDays.Friday, value);
    }

    public bool Saturday
    {
        get => Has(ScheduleDays.Saturday);
        set => SetDay(ScheduleDays.Saturday, value);
    }

    public bool Sunday
    {
        get => Has(ScheduleDays.Sunday);
        set => SetDay(ScheduleDays.Sunday, value);
    }

    /// <summary>A plain-words restatement of the row, so a misread checkbox is visible.</summary>
    public string Summary => ToTask().Describe();

    /// <summary>Whether the time in this row is one a schedule could actually fire on.</summary>
    public bool HasUsableTime => ScheduledTask.IsValidTime(Time);

    public ScheduledTask ToTask() => new()
    {
        Id = Id,
        Action = Action,
        Time = Time?.Trim() ?? string.Empty,
        Enabled = Enabled,
        Days = _days
    };

    private bool Has(ScheduleDays day) => _days.HasFlag(day);

    private void SetDay(ScheduleDays day, bool value, [CallerMemberName] string? property = null)
    {
        var updated = value ? _days | day : _days & ~day;
        if (updated == _days)
        {
            return;
        }

        _days = updated;
        Raise(property);
        Raise(nameof(Summary));
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        Raise(property);
        Raise(nameof(Summary));
    }

    private void Raise(string? property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public event PropertyChangedEventHandler? PropertyChanged;
}
