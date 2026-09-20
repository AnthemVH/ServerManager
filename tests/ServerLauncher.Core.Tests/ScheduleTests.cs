using System.Globalization;
using FluentAssertions;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Covers when a scheduled task fires. The schedule tick runs every 30 seconds and asks
/// each task whether its moment has come, so everything that decides "yes" lives here.
/// </summary>
public class ScheduledTaskTests
{
    private static DateTime At(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Local);

    // 2026-09-21 is a Monday; the dates below walk that week.
    private static readonly DateTime MondayAt0500 = At(2026, 9, 21, 5, 0);
    private static readonly DateTime ThursdayAt0500 = At(2026, 9, 24, 5, 0);
    private static readonly DateTime TuesdayAt0500 = At(2026, 9, 22, 5, 0);
    private static readonly DateTime SundayAt0500 = At(2026, 9, 27, 5, 0);

    private static ScheduledTask TwiceAWeek() => new()
    {
        Action = ScheduledAction.Restart,
        Time = "05:00",
        Days = ScheduleDays.Monday | ScheduleDays.Thursday
    };

    [Fact]
    public void ATwiceAWeekTaskFiresOnBothOfItsDays()
    {
        var task = TwiceAWeek();

        task.IsDue(MondayAt0500).Should().BeTrue();
        task.IsDue(ThursdayAt0500).Should().BeTrue();
    }

    [Fact]
    public void ATwiceAWeekTaskIsSilentOnEveryOtherDay()
    {
        var task = TwiceAWeek();

        task.IsDue(TuesdayAt0500).Should().BeFalse();
        task.IsDue(SundayAt0500).Should().BeFalse();
    }

    [Fact]
    public void TheMinuteHasToMatchAsWellAsTheDay()
    {
        var task = TwiceAWeek();

        task.IsDue(MondayAt0500.AddMinutes(-1)).Should().BeFalse();
        task.IsDue(MondayAt0500.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public void SecondsWithinTheMatchingMinuteStillCount()
    {
        // The tick runs twice a minute, so it will usually ask partway through. The
        // once-per-day guard in ServerManager, not this, stops the second firing.
        TwiceAWeek().IsDue(MondayAt0500.AddSeconds(45)).Should().BeTrue();
    }

    [Fact]
    public void ADisabledTaskNeverFires()
    {
        var task = TwiceAWeek();
        task.Enabled = false;

        task.IsDue(MondayAt0500).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("5:00")]
    [InlineData("25:00")]
    [InlineData("05:60")]
    [InlineData("five o'clock")]
    public void ATaskWithNoUsableTimeNeverFires(string time)
    {
        var task = TwiceAWeek();
        task.Time = time;

        task.IsDue(MondayAt0500).Should().BeFalse();
    }

    [Fact]
    public void SurroundingWhitespaceInASavedTimeIsTolerated()
    {
        var task = TwiceAWeek();
        task.Time = " 05:00 ";

        task.IsDue(MondayAt0500).Should().BeTrue();
    }

    [Fact]
    public void EveryDayMeansEveryDay()
    {
        var task = new ScheduledTask { Time = "05:00", Days = ScheduleDays.EveryDay };

        for (var offset = 0; offset < 7; offset++)
        {
            task.IsDue(MondayAt0500.AddDays(offset)).Should().BeTrue($"day offset {offset}");
        }
    }

    [Fact]
    public void ATaskWithNoDaysSelectedNeverFires()
    {
        var task = new ScheduledTask { Time = "05:00", Days = ScheduleDays.None };

        for (var offset = 0; offset < 7; offset++)
        {
            task.IsDue(MondayAt0500.AddDays(offset)).Should().BeFalse();
        }
    }

    [Fact]
    public void WeekdaysAndWeekendsCoverTheWholeWeekBetweenThem()
    {
        var weekdays = new ScheduledTask { Time = "05:00", Days = ScheduleDays.Weekdays };
        var weekend = new ScheduledTask { Time = "05:00", Days = ScheduleDays.Weekend };

        for (var offset = 0; offset < 7; offset++)
        {
            var moment = MondayAt0500.AddDays(offset);
            (weekdays.IsDue(moment) ^ weekend.IsDue(moment))
                .Should().BeTrue($"exactly one should fire on day offset {offset}");
        }
    }

    [Fact]
    public void ScheduleTimesDoNotDependOnTheMachinesLocale()
    {
        // ":" in a custom format string is the culture's time separator. A machine set to
        // a locale that uses "." would render 05.00, match nothing, and silently stop
        // running every schedule that was saved elsewhere.
        var original = CultureInfo.CurrentCulture;

        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");

            ScheduledTask.FormatTime(MondayAt0500).Should().Be("05:00");
            TwiceAWeek().IsDue(MondayAt0500).Should().BeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ADescriptionNamesTheActionTheTimeAndTheDays()
    {
        TwiceAWeek().Describe().Should().Be("Restart · 05:00 · Mon, Thu");
    }

    [Fact]
    public void CommonDaySetsAreDescribedInWords()
    {
        ScheduledTask.DescribeDays(ScheduleDays.EveryDay).Should().Be("every day");
        ScheduledTask.DescribeDays(ScheduleDays.Weekdays).Should().Be("weekdays");
        ScheduledTask.DescribeDays(ScheduleDays.Weekend).Should().Be("weekends");
        ScheduledTask.DescribeDays(ScheduleDays.None).Should().Be("never");
    }

    [Fact]
    public void EveryDayOfTheWeekMapsToItsOwnFlag()
    {
        var flags = Enum.GetValues<DayOfWeek>().Select(ScheduledTask.ToFlag).ToList();

        flags.Should().OnlyHaveUniqueItems("a day mapped to the wrong flag fires on the wrong day");
        flags.Should().BeEquivalentTo(ScheduledTask.Ordered());
    }
}

/// <summary>
/// Covers carrying old servers.json files forward. An existing server's restart time must
/// keep firing at the time it always did, without the user reconfiguring anything.
/// </summary>
public class ScheduleMigrationTests
{
    [Fact]
    public void AnOldDailyRestartTimeBecomesAnEveryDayTask()
    {
        var definition = new ServerDefinition { ScheduledRestartTime = "05:00" };

        definition.MigrateLegacySchedules().Should().BeTrue("the file needs rewriting");

        var task = definition.Schedule.Single();
        task.Action.Should().Be(ScheduledAction.Restart);
        task.Time.Should().Be("05:00");
        task.Days.Should().Be(ScheduleDays.EveryDay);
        task.Enabled.Should().BeTrue();
    }

    [Fact]
    public void AnOldDailyBackupTimeBecomesAnEveryDayTask()
    {
        var definition = new ServerDefinition { BackupScheduleTime = "03:30" };

        definition.MigrateLegacySchedules();

        definition.Schedule.Single().Action.Should().Be(ScheduledAction.Backup);
        definition.Schedule.Single().Time.Should().Be("03:30");
    }

    [Fact]
    public void BothOldTimesMigrateTogether()
    {
        var definition = new ServerDefinition
        {
            ScheduledRestartTime = "05:00",
            BackupScheduleTime = "03:30"
        };

        definition.MigrateLegacySchedules();

        definition.Schedule.Should().HaveCount(2);
    }

    [Fact]
    public void TheOldFieldsAreClearedSoNothingReadsThemAgain()
    {
        // Two places holding a restart time is the classic way to end up editing one and
        // wondering why the other still fires.
        var definition = new ServerDefinition
        {
            ScheduledRestartTime = "05:00",
            BackupScheduleTime = "03:30"
        };

        definition.MigrateLegacySchedules();

        definition.ScheduledRestartTime.Should().BeEmpty();
        definition.BackupScheduleTime.Should().BeEmpty();
    }

    [Fact]
    public void MigratingTwiceDoesNotDuplicateTheTask()
    {
        // It runs on every load, so it has to be safe to run on every load.
        var definition = new ServerDefinition { ScheduledRestartTime = "05:00" };

        definition.MigrateLegacySchedules().Should().BeTrue();
        definition.MigrateLegacySchedules().Should().BeFalse("nothing is left to migrate");

        definition.Schedule.Should().HaveCount(1);
    }

    [Fact]
    public void AFileWithNothingToMigrateIsNotRewritten()
    {
        new ServerDefinition().MigrateLegacySchedules().Should().BeFalse();
    }

    [Fact]
    public void AnUnusableOldTimeIsDiscardedRatherThanCarriedForever()
    {
        // It could never have fired, so nothing is lost — and leaving it would mean
        // migration reported "changed" on every single load.
        var definition = new ServerDefinition { ScheduledRestartTime = "not a time" };

        definition.MigrateLegacySchedules().Should().BeTrue();

        definition.Schedule.Should().BeEmpty();
        definition.ScheduledRestartTime.Should().BeEmpty();
        definition.MigrateLegacySchedules().Should().BeFalse();
    }

    [Fact]
    public void EditingAClonedSchedulesTaskDoesNotReachTheOriginal()
    {
        // The editor window works on a clone so that cancelling changes nothing.
        var definition = new ServerDefinition();
        definition.Schedule.Add(new ScheduledTask { Time = "05:00", Days = ScheduleDays.Monday });

        var copy = definition.Clone();
        copy.Schedule[0].Time = "23:00";
        copy.Schedule.Add(new ScheduledTask { Time = "01:00" });

        definition.Schedule.Should().HaveCount(1);
        definition.Schedule[0].Time.Should().Be("05:00");
    }
}
