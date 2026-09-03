using FluentAssertions;
using ServerLauncher.Core.Backup;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// End-to-end supervision tests: real processes, real crashes, real restarts.
/// Each test gets a temporary log directory so nothing lands in the user's app data.
/// </summary>
[Collection(ProcessIntegrationCollection.Name)]
public sealed class ServerInstanceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "ServerLauncherTests", Guid.NewGuid().ToString("N"));

    private readonly List<ServerInstance> _created = new();

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private ServerInstance Create(ServerDefinition definition, AppSettings? settings = null)
    {
        var logDir = Path.Combine(_tempRoot, definition.Id.ToString("N"));
        var instance = new ServerInstance(definition, settings ?? new AppSettings(), logDir);
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

            await Task.Delay(100);
        }

        return condition();
    }

    [Fact]
    public async Task StartingAndStopping_MovesThroughTheExpectedStates()
    {
        var states = new List<ServerState>();
        var instance = Create(new ServerDefinition
        {
            Name = "Interactive",
            ScriptPath = Fixture("interactive.bat"),
            StopCommand = "stop",
            GracefulStopTimeoutSeconds = 15,
            RestartPolicy = RestartPolicy.Never
        });

        instance.StateChanged += (_, state) => states.Add(state);

        await instance.StartAsync();
        instance.State.Should().Be(ServerState.Running);
        instance.StartedAt.Should().NotBeNull();

        await instance.StopAsync();
        await WaitUntilAsync(() => instance.State == ServerState.Stopped, TimeSpan.FromSeconds(15));

        instance.State.Should().Be(ServerState.Stopped);
        states.Should().ContainInOrder(ServerState.Starting, ServerState.Running, ServerState.Stopping);
    }

    [Fact]
    public async Task ConsoleOutput_IsCapturedIntoTheBuffer()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Chatty",
            ScriptPath = Fixture("chatty.bat"),
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();
        await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Any(l => l.Text == "LINE 200"),
            TimeSpan.FromSeconds(30));

        var snapshot = instance.ConsoleSnapshot();
        snapshot.Should().Contain(l => l.Text == "LINE 1");
        snapshot.Should().Contain(l => l.Text == "LINE 200");
        snapshot.Should().Contain(l => l.Stream == LogStream.Launcher && l.Text.Contains("Started"));
    }

    [Fact]
    public async Task OperatorStop_DoesNotTriggerARestart()
    {
        // Regression guard: with an Always policy it would be easy to relaunch a
        // server the user just asked to stop.
        var instance = Create(new ServerDefinition
        {
            Name = "Interactive",
            ScriptPath = Fixture("interactive.bat"),
            StopCommand = "stop",
            RestartPolicy = RestartPolicy.Always,
            GracefulStopTimeoutSeconds = 15
        });

        await instance.StartAsync();
        await instance.StopAsync();
        await WaitUntilAsync(() => instance.State == ServerState.Stopped, TimeSpan.FromSeconds(15));

        // Well past the 5s first backoff step: if a restart were coming, it would have happened.
        await Task.Delay(TimeSpan.FromSeconds(8));

        instance.State.Should().Be(ServerState.Stopped);
    }

    [Fact]
    public async Task CrashingServer_IsRestartedAutomatically()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Crashy",
            ScriptPath = Fixture("crashy.bat"),
            RestartPolicy = RestartPolicy.OnCrash,
            MaxConsecutiveRestarts = 2
        });

        await instance.StartAsync();

        // First exit is a crash, so a restart is scheduled 5s later.
        var restarted = await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Count(l => l.Text.Contains("CRASHY: starting")) >= 2,
            TimeSpan.FromSeconds(45));

        restarted.Should().BeTrue("an OnCrash policy should relaunch the server after a non-zero exit");
    }

    [Fact]
    public async Task CrashLoop_EndsInFailedRatherThanRestartingForever()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Crashy",
            ScriptPath = Fixture("crashy.bat"),
            RestartPolicy = RestartPolicy.OnCrash,
            MaxConsecutiveRestarts = 1
        });

        await instance.StartAsync();

        var parked = await WaitUntilAsync(
            () => instance.State == ServerState.Failed,
            TimeSpan.FromSeconds(60));

        parked.Should().BeTrue("a server that keeps crashing must eventually be parked");
        instance.ConsoleSnapshot().Should().Contain(l => l.Text.Contains("giving up"));
    }

    [Fact]
    public async Task NeverPolicy_LeavesACrashedServerDown()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Crashy",
            ScriptPath = Fixture("crashy.bat"),
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();
        await WaitUntilAsync(() => instance.State == ServerState.Crashed, TimeSpan.FromSeconds(30));

        instance.State.Should().Be(ServerState.Crashed);
    }

    [Fact]
    public async Task SendCommand_ReachesTheServerConsole()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Interactive",
            ScriptPath = Fixture("interactive.bat"),
            StopCommand = "stop",
            RestartPolicy = RestartPolicy.Never,
            GracefulStopTimeoutSeconds = 15
        });

        await instance.StartAsync();
        await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Any(l => l.Text.Contains("READY")),
            TimeSpan.FromSeconds(15));

        instance.SendCommand("ping").Should().BeTrue();

        var echoed = await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Any(l => l.Text.Contains("ECHO: ping")),
            TimeSpan.FromSeconds(15));

        echoed.Should().BeTrue();

        await instance.StopAsync();
    }

    [Fact]
    public async Task ResourcePolling_ReportsTheWholeProcessTree()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Tree",
            ScriptPath = Fixture("tree-spawner.bat"),
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();
        await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Any(l => l.Text.Contains("child started")),
            TimeSpan.FromSeconds(20));

        // Two polls: the first establishes the CPU baseline, the second yields a rate.
        instance.Poll();
        await Task.Delay(1500);
        instance.Poll();

        instance.LastSample.ProcessCount.Should().BeGreaterThanOrEqualTo(2,
            "the sample must cover the spawned child, not just the launcher script");
        instance.LastSample.WorkingSetBytes.Should().BeGreaterThan(0);
        instance.ResourceHistory().Should().HaveCountGreaterThanOrEqualTo(2);

        await instance.StopAsync();
    }

    [Fact]
    public async Task LogFile_IsWrittenAlongsideTheInMemoryBuffer()
    {
        var definition = new ServerDefinition
        {
            Name = "Chatty",
            ScriptPath = Fixture("chatty.bat"),
            RestartPolicy = RestartPolicy.Never
        };

        var instance = Create(definition);
        await instance.StartAsync();
        await WaitUntilAsync(
            () => instance.State == ServerState.Stopped || instance.State == ServerState.Crashed,
            TimeSpan.FromSeconds(30));

        var logDir = Path.Combine(_tempRoot, definition.Id.ToString("N"));

        var written = await WaitUntilAsync(
            () => Directory.Exists(logDir) && Directory.GetFiles(logDir, "*.log").Length > 0,
            TimeSpan.FromSeconds(10));

        written.Should().BeTrue("console history must survive past the in-memory ring buffer");

        var contents = File.ReadAllText(Directory.GetFiles(logDir, "*.log")[0]);
        contents.Should().Contain("LINE 1");
    }

    [Fact]
    public async Task SafeBackup_StopsTheServerArchivesItAndRestartsIt()
    {
        var sourceFolder = Path.Combine(_tempRoot, "world");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "level.dat"), "world data");
        File.WriteAllText(Path.Combine(sourceFolder, "config.yml"), "settings");

        var destination = Path.Combine(_tempRoot, "backups");

        var instance = Create(new ServerDefinition
        {
            Name = "Backup Target",
            ScriptPath = Fixture("interactive.bat"),
            StopCommand = "stop",
            GracefulStopTimeoutSeconds = 15,
            RestartPolicy = RestartPolicy.Never,
            BackupSourceFolder = sourceFolder,
            BackupDestinationFolder = destination,
            BackupMode = BackupMode.SafeStopAndRestart
        });

        await instance.StartAsync();
        await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Any(l => l.Text.Contains("READY")),
            TimeSpan.FromSeconds(15));

        var result = await new BackupService().RunAsync(instance);

        result.Success.Should().BeTrue(result.Message);
        result.FilesArchived.Should().Be(2);
        File.Exists(result.ArchivePath).Should().BeTrue();

        // Safe mode must bring the server back up afterwards.
        instance.State.Should().Be(ServerState.Running);

        await instance.StopAsync();
    }

    [Fact]
    public async Task BackupRetention_KeepsOnlyTheNewestArchives()
    {
        var sourceFolder = Path.Combine(_tempRoot, "retention-source");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "data.txt"), "x");

        var destination = Path.Combine(_tempRoot, "retention-backups");

        var instance = Create(new ServerDefinition
        {
            Name = "Retention",
            ScriptPath = Fixture("interactive.bat"),
            RestartPolicy = RestartPolicy.Never,
            BackupSourceFolder = sourceFolder,
            BackupDestinationFolder = destination,
            BackupMode = BackupMode.Live,
            BackupRetentionCount = 2
        });

        var service = new BackupService();
        for (var i = 0; i < 4; i++)
        {
            var result = await service.RunAsync(instance);
            result.Success.Should().BeTrue(result.Message);

            // Archive names carry a whole-second timestamp, so space the runs out.
            await Task.Delay(1100);
        }

        Directory.GetFiles(destination, "*.zip").Should().HaveCount(2,
            "older archives beyond the retention count should be pruned");
    }

    public void Dispose()
    {
        foreach (var instance in _created)
        {
            try
            {
                instance.Dispose();
            }
            catch (Exception)
            {
                // Best effort during teardown.
            }
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
