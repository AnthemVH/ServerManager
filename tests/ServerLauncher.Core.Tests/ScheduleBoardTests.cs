using FluentAssertions;
using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Covers the weekly view: which entries land in which day's column, in what order, what
/// fires next, and how entries are added, changed and removed from it.
/// </summary>
public class ScheduleBoardTests
{
    // 2026-09-21 is a Monday.
    private static DateTime Monday(int hour, int minute) => new(2026, 9, 21, hour, minute, 0);

    private static ScheduledTask Task(string time, ScheduleDays days,
        ScheduledAction action = ScheduledAction.Restart, bool enabled = true) => new()
    {
        Time = time,
        Days = days,
        Action = action,
        Enabled = enabled
    };

    private static ServerDefinition Server(string name, params ScheduledTask[] tasks)
    {
        var server = new ServerDefinition { Name = name };
        server.Schedule.AddRange(tasks);
        return server;
    }

    // --- Columns ---

    [Fact]
    public void AMultiDayTaskAppearsInEachOfItsDaysColumns()
    {
        var arma = Server("Arma", Task("05:00", ScheduleDays.Monday | ScheduleDays.Thursday));

        ScheduleBoard.ForDay(new[] { arma }, ScheduleDays.Monday).Should().ContainSingle();
        ScheduleBoard.ForDay(new[] { arma }, ScheduleDays.Thursday).Should().ContainSingle();
        ScheduleBoard.ForDay(new[] { arma }, ScheduleDays.Tuesday).Should().BeEmpty();
    }

    [Fact]
    public void EachAppearanceKnowsWhichColumnItIsIn()
    {
        // Deleting "just Thursday" depends on this.
        var arma = Server("Arma", Task("05:00", ScheduleDays.Monday | ScheduleDays.Thursday));

        ScheduleBoard.ForDay(new[] { arma }, ScheduleDays.Thursday).Single().Day
            .Should().Be(ScheduleDays.Thursday);
    }

    [Fact]
    public void EveryServersEntriesShareTheColumn()
    {
        var servers = new[]
        {
            Server("Arma", Task("05:00", ScheduleDays.Monday)),
            Server("Minecraft", Task("04:00", ScheduleDays.Monday))
        };

        ScheduleBoard.ForDay(servers, ScheduleDays.Monday)
            .Select(e => e.ServerName).Should().Equal("Minecraft", "Arma");
    }

    [Fact]
    public void EntriesAreInTimeOrderNotEntryOrder()
    {
        var server = Server("Arma",
            Task("18:30", ScheduleDays.Monday),
            Task("05:00", ScheduleDays.Monday),
            Task("09:15", ScheduleDays.Monday));

        ScheduleBoard.ForDay(new[] { server }, ScheduleDays.Monday)
            .Select(e => e.Task.Time).Should().Equal("05:00", "09:15", "18:30");
    }

    [Fact]
    public void TimesSortAsTimesNotAsText()
    {
        // As text, "10:00" sorts before "9:..." forms; stored times are always two-digit
        // hours, but the comparison should not depend on that.
        var server = Server("Arma",
            Task("10:00", ScheduleDays.Monday),
            Task("09:00", ScheduleDays.Monday));

        ScheduleBoard.ForDay(new[] { server }, ScheduleDays.Monday)
            .Select(e => e.Task.Time).Should().Equal("09:00", "10:00");
    }

    [Fact]
    public void AnEntryWithAnUnusableTimeSortsLastRatherThanPosingAsMidnight()
    {
        var server = Server("Arma",
            Task("broken", ScheduleDays.Monday),
            Task("05:00", ScheduleDays.Monday));

        ScheduleBoard.ForDay(new[] { server }, ScheduleDays.Monday)
            .Select(e => e.Task.Time).Should().Equal("05:00", "broken");
    }

    [Fact]
    public void DisabledEntriesStillAppearSoTheyCanBeTurnedBackOn()
    {
        var server = Server("Arma", Task("05:00", ScheduleDays.Monday, enabled: false));

        ScheduleBoard.ForDay(new[] { server }, ScheduleDays.Monday).Should().ContainSingle();
    }

    // --- What fires next ---

    [Fact]
    public void NextIsLaterTodayWhenThereIsSomethingLaterToday()
    {
        var server = Server("Arma", Task("18:00", ScheduleDays.Monday));

        ScheduleBoard.Next(new[] { server }, Monday(9, 0))!.At.Should().Be(Monday(18, 0));
    }

    [Fact]
    public void NextSkipsTodaysEntriesThatHaveAlreadyPassed()
    {
        var server = Server("Arma", Task("05:00", ScheduleDays.Monday | ScheduleDays.Thursday));

        ScheduleBoard.Next(new[] { server }, Monday(9, 0))!.At
            .Should().Be(new DateTime(2026, 9, 24, 5, 0, 0), "Thursday is the next matching day");
    }

    [Fact]
    public void AWeeklyTaskThatHasPassedTodayIsNextSeenAWeekLater()
    {
        var server = Server("Arma", Task("05:00", ScheduleDays.Monday));

        ScheduleBoard.Next(new[] { server }, Monday(9, 0))!.At
            .Should().Be(new DateTime(2026, 9, 28, 5, 0, 0));
    }

    [Fact]
    public void ATaskDueThisVeryMinuteIsNotNext()
    {
        // It has just fired or is about to; "next" should mean the one after.
        var server = Server("Arma", Task("09:00", ScheduleDays.Monday), Task("10:00", ScheduleDays.Monday));

        ScheduleBoard.Next(new[] { server }, Monday(9, 0))!.At.Should().Be(Monday(10, 0));
    }

    [Fact]
    public void NextLooksAcrossEveryServer()
    {
        var servers = new[]
        {
            Server("Arma", Task("18:00", ScheduleDays.Monday)),
            Server("Minecraft", Task("12:00", ScheduleDays.Monday, ScheduledAction.Backup))
        };

        var next = ScheduleBoard.Next(servers, Monday(9, 0))!;

        next.ServerName.Should().Be("Minecraft");
        next.Task.Action.Should().Be(ScheduledAction.Backup);
    }

    [Fact]
    public void DisabledAndUnusableEntriesAreNeverNext()
    {
        var server = Server("Arma",
            Task("10:00", ScheduleDays.Monday, enabled: false),
            Task("broken", ScheduleDays.Monday),
            Task("11:00", ScheduleDays.None),
            Task("12:00", ScheduleDays.Monday));

        ScheduleBoard.Next(new[] { server }, Monday(9, 0))!.At.Should().Be(Monday(12, 0));
    }

    [Fact]
    public void WithNothingScheduledThereIsNoNext()
    {
        ScheduleBoard.Next(new[] { Server("Arma") }, Monday(9, 0)).Should().BeNull();
    }

    // --- Validation ---

    [Fact]
    public void AnOrdinaryRestartIsValid()
    {
        ScheduleBoard.Validate(Task("05:00", ScheduleDays.Monday), new ServerDefinition())
            .Should().BeNull();
    }

    [Fact]
    public void ABadTimeIsRefused()
    {
        ScheduleBoard.Validate(Task("5am", ScheduleDays.Monday), new ServerDefinition())
            .Should().Contain("HH:mm");
    }

    [Fact]
    public void NoDaysIsRefused()
    {
        ScheduleBoard.Validate(Task("05:00", ScheduleDays.None), new ServerDefinition())
            .Should().Contain("at least one day");
    }

    [Fact]
    public void AnUpdateOnAServerWithNoUpdateScriptIsRefused()
    {
        var server = new ServerDefinition { Name = "Arma" };

        ScheduleBoard.Validate(Task("05:00", ScheduleDays.Monday, ScheduledAction.RunUpdate), server)
            .Should().Contain("no update script");

        server.UpdateScriptPath = @"C:\u.bat";
        ScheduleBoard.Validate(Task("05:00", ScheduleDays.Monday, ScheduledAction.RunUpdate), server)
            .Should().BeNull();
    }

    [Fact]
    public void ABackupOnAServerWithNowhereToWriteIsRefused()
    {
        var server = new ServerDefinition { Name = "Arma" };

        ScheduleBoard.Validate(Task("05:00", ScheduleDays.Monday, ScheduledAction.Backup), server)
            .Should().Contain("no backup destination");

        server.BackupDestinationFolder = @"D:\backups";
        ScheduleBoard.Validate(Task("05:00", ScheduleDays.Monday, ScheduledAction.Backup), server)
            .Should().BeNull();
    }

    // --- Editing ---

    [Fact]
    public void RemovingOneDayLeavesTheOthers()
    {
        var task = Task("05:00", ScheduleDays.Monday | ScheduleDays.Thursday);
        var server = Server("Arma", task);

        server.RemoveScheduleDay(task.Id, ScheduleDays.Monday).Should().BeTrue();

        server.Schedule.Single().Days.Should().Be(ScheduleDays.Thursday);
    }

    [Fact]
    public void RemovingATasksLastDayRemovesTheTask()
    {
        var task = Task("05:00", ScheduleDays.Monday);
        var server = Server("Arma", task);

        server.RemoveScheduleDay(task.Id, ScheduleDays.Monday);

        server.Schedule.Should().BeEmpty("a task with no days can never fire");
    }

    [Fact]
    public void RemovingATaskRemovesItOnEveryDay()
    {
        var task = Task("05:00", ScheduleDays.EveryDay);
        var server = Server("Arma", task, Task("06:00", ScheduleDays.Monday));

        server.RemoveScheduledTask(task.Id).Should().BeTrue();

        server.Schedule.Should().ContainSingle().Which.Time.Should().Be("06:00");
    }

    [Fact]
    public void RemovingSomethingThatIsNotThereSaysSo()
    {
        var server = Server("Arma");

        server.RemoveScheduledTask(Guid.NewGuid()).Should().BeFalse();
        server.RemoveScheduleDay(Guid.NewGuid(), ScheduleDays.Monday).Should().BeFalse();
    }

    [Fact]
    public void SavingAnEditedTaskReplacesItInPlace()
    {
        // Same id, so the once-per-day guard keeps tracking it.
        var task = Task("05:00", ScheduleDays.Monday);
        var server = Server("Arma", task, Task("06:00", ScheduleDays.Monday));

        var edited = task.Clone();
        edited.Time = "07:00";
        server.UpsertScheduledTask(edited);

        server.Schedule.Should().HaveCount(2);
        server.Schedule[0].Id.Should().Be(task.Id);
        server.Schedule[0].Time.Should().Be("07:00", "the order in the file is kept as well");
    }

    [Fact]
    public void SavingANewTaskAddsIt()
    {
        var server = Server("Arma");

        server.UpsertScheduledTask(Task("05:00", ScheduleDays.Monday));

        server.Schedule.Should().ContainSingle();
    }
}
