using FluentAssertions;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Covers shaping a server's update script for the launcher. The update runs through the
/// same path as the server itself, so it inherits the quoting, the environment and the
/// job object rather than getting a second, less tested launch route.
/// </summary>
public class UpdateDefinitionTests
{
    [Fact]
    public void AServerWithNoUpdateScriptSaysSo()
    {
        new ServerDefinition().HasUpdateScript.Should().BeFalse();
        new ServerDefinition { UpdateScriptPath = "   " }.HasUpdateScript.Should().BeFalse();
        new ServerDefinition { UpdateScriptPath = @"C:\u.bat" }.HasUpdateScript.Should().BeTrue();
    }

    [Fact]
    public void TheUpdateRunsTheUpdateScriptNotTheServerScript()
    {
        var definition = new ServerDefinition
        {
            ScriptPath = @"C:\servers\start.bat",
            Arguments = "-port 2302",
            UpdateScriptPath = @"C:\servers\update.bat",
            UpdateArguments = "+app_update 233780"
        };

        var update = definition.CreateUpdateDefinition();

        update.ScriptPath.Should().Be(@"C:\servers\update.bat");
        update.Arguments.Should().Be("+app_update 233780",
            "the server's own arguments would mean nothing to an updater");
    }

    [Fact]
    public void TheUpdateRunsWhereTheServerRuns()
    {
        // A mod updater's relative paths are written against the server folder.
        var definition = new ServerDefinition
        {
            ScriptPath = @"C:\servers\arma\start.bat",
            UpdateScriptPath = @"D:\tools\steamcmd-update.bat"
        };

        definition.CreateUpdateDefinition().ResolveWorkingDirectory()
            .Should().Be(@"C:\servers\arma");
    }

    [Fact]
    public void AnExplicitWorkingDirectoryIsCarriedOver()
    {
        var definition = new ServerDefinition
        {
            ScriptPath = @"C:\servers\arma\start.bat",
            WorkingDirectory = @"E:\gamedata",
            UpdateScriptPath = @"C:\servers\arma\update.bat"
        };

        definition.CreateUpdateDefinition().ResolveWorkingDirectory().Should().Be(@"E:\gamedata");
    }

    [Fact]
    public void TheServersEnvironmentIsAvailableToTheUpdate()
    {
        var definition = new ServerDefinition { UpdateScriptPath = @"C:\u.bat" };
        definition.EnvironmentVariables["STEAM_USER"] = "someone";

        definition.CreateUpdateDefinition().EnvironmentVariables
            .Should().ContainKey("STEAM_USER");
    }

    [Fact]
    public void TheUpdatesEnvironmentIsACopy()
    {
        // Otherwise an update mutating its environment would edit the server's.
        var definition = new ServerDefinition { UpdateScriptPath = @"C:\u.bat" };
        definition.EnvironmentVariables["A"] = "1";

        var update = definition.CreateUpdateDefinition();
        update.EnvironmentVariables["B"] = "2";

        definition.EnvironmentVariables.Should().NotContainKey("B");
    }
}

/// <summary>
/// Runs real update scripts against a real supervised server.
/// </summary>
[Collection(ProcessIntegrationCollection.Name)]
public sealed class UpdateRunTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "ServerLauncherUpdateTests", Guid.NewGuid().ToString("N"));

    private readonly List<ServerInstance> _created = new();

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private ServerInstance Create(ServerDefinition definition)
    {
        var logDir = Path.Combine(_tempRoot, definition.Id.ToString("N"));
        var instance = new ServerInstance(definition, new AppSettings(), logDir);
        _created.Add(instance);
        return instance;
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    [Fact]
    public async Task AnUpdateScriptRunsAndReportsSuccess()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-ok.bat")
        });

        (await instance.RunUpdateAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task TheUpdatesOutputLandsInTheServersConsole()
    {
        // Otherwise a failed mod download would be invisible, which is exactly when the
        // output matters.
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-ok.bat")
        });

        await instance.RunUpdateAsync();

        var text = string.Join("\n", instance.ConsoleSnapshot().Select(l => l.Text));
        text.Should().Contain("Updating mods");
        text.Should().Contain("Update script finished successfully");
    }

    [Fact]
    public async Task ANonZeroExitCodeIsReportedAsAFailure()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-fails.bat")
        });

        (await instance.RunUpdateAsync()).Should().BeFalse();

        string.Join("\n", instance.ConsoleSnapshot().Select(l => l.Text))
            .Should().Contain("exit code 7");
    }

    [Fact]
    public async Task AskingToUpdateWithNoScriptConfiguredSaysSoRatherThanFailingSilently()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat")
        });

        (await instance.RunUpdateAsync()).Should().BeFalse();

        string.Join("\n", instance.ConsoleSnapshot().Select(l => l.Text))
            .Should().Contain("No update script is configured");
    }

    [Fact]
    public async Task UpdatingARunningServerStopsItAndBringsItBack()
    {
        // The whole point: a mod updater cannot overwrite files the running server holds
        // open, and the server has to come back afterwards without anyone intervening.
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            StopCommand = "quit",
            GracefulStopTimeoutSeconds = 5,
            UpdateScriptPath = Fixture("update-ok.bat"),
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();
        (await WaitUntilAsync(() => instance.State == ServerState.Running, TimeSpan.FromSeconds(10)))
            .Should().BeTrue("the server should be up before the update");

        var wasStopped = false;
        instance.StateChanged += (_, state) =>
        {
            if (state == ServerState.Stopped)
            {
                wasStopped = true;
            }
        };

        (await instance.RunUpdateAsync()).Should().BeTrue();

        wasStopped.Should().BeTrue("the server must be down while its files are replaced");
        (await WaitUntilAsync(() => instance.State == ServerState.Running, TimeSpan.FromSeconds(15)))
            .Should().BeTrue("the server must come back after the update");
    }

    [Fact]
    public async Task AFailedUpdateStillBringsTheServerBack()
    {
        // A failed update is bad; turning it into an outage is worse.
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            StopCommand = "quit",
            GracefulStopTimeoutSeconds = 5,
            UpdateScriptPath = Fixture("update-fails.bat"),
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();
        await WaitUntilAsync(() => instance.State == ServerState.Running, TimeSpan.FromSeconds(10));

        (await instance.RunUpdateAsync()).Should().BeFalse();

        (await WaitUntilAsync(() => instance.State == ServerState.Running, TimeSpan.FromSeconds(15)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task UpdatingAStoppedServerLeavesItStopped()
    {
        // Updating before starting later is a normal thing to want.
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-ok.bat")
        });

        await instance.RunUpdateAsync();

        instance.State.Should().Be(ServerState.Stopped);
    }

    [Fact]
    public async Task AHangingUpdateIsStoppedRatherThanBlockingForever()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-hangs.bat"),

            // Clamped to a minimum of one minute in the runner, so this is the floor.
            UpdateTimeoutMinutes = 1
        });

        var started = DateTime.UtcNow;
        (await instance.RunUpdateAsync()).Should().BeFalse();

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromMinutes(3));

        string.Join("\n", instance.ConsoleSnapshot().Select(l => l.Text))
            .Should().Contain("did not finish");
    }

    [Fact]
    public async Task IsUpdatingIsTrueOnlyWhileTheUpdateRuns()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-ok.bat")
        });

        var sawUpdating = false;
        instance.UpdatingChanged += (_, updating) => sawUpdating |= updating;

        instance.IsUpdating.Should().BeFalse();
        await instance.RunUpdateAsync();

        sawUpdating.Should().BeTrue("the UI needs to know an update is in progress");
        instance.IsUpdating.Should().BeFalse("and needs to know when it is over");
    }

    [Fact]
    public async Task RunUpdateBeforeStartUpdatesFirstAndThenStarts()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-ok.bat"),
            RunUpdateBeforeStart = true,
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();

        (await WaitUntilAsync(() => instance.State == ServerState.Running, TimeSpan.FromSeconds(10)))
            .Should().BeTrue();

        var lines = instance.ConsoleSnapshot().Select(l => l.Text).ToList();
        var updateIndex = lines.FindIndex(l => l.Contains("Updating mods"));
        var startIndex = lines.FindIndex(l => l.StartsWith("Starting from", StringComparison.Ordinal));

        updateIndex.Should().BeGreaterThanOrEqualTo(0, "the update should have run");
        startIndex.Should().BeGreaterThan(updateIndex, "the update runs before the server starts");
    }

    [Fact]
    public async Task AFailedPreStartUpdateDoesNotStopTheServerComingUp()
    {
        // An out-of-date server running beats a server that refuses to start.
        var instance = Create(new ServerDefinition
        {
            Name = "Alpha",
            ScriptPath = Fixture("interactive.bat"),
            UpdateScriptPath = Fixture("update-fails.bat"),
            RunUpdateBeforeStart = true,
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();

        (await WaitUntilAsync(() => instance.State == ServerState.Running, TimeSpan.FromSeconds(10)))
            .Should().BeTrue();
    }

    public void Dispose()
    {
        foreach (var instance in _created)
        {
            try
            {
                instance.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }

            instance.Dispose();
        }

        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
