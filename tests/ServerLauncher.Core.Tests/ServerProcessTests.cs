using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Processes;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Integration tests that actually launch processes. These cover the behaviour that
/// unit tests cannot prove: real stdio capture, real exit codes, and real tree kills.
/// </summary>
public class ServerProcessTests
{
    private static readonly AppSettings Settings = new();

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static ServerDefinition Definition(string fixture, string stopCommand = "") => new()
    {
        Name = fixture,
        ScriptPath = Fixture(fixture),
        StopCommand = stopCommand,
        GracefulStopTimeoutSeconds = 10
    };

    private static (ServerProcess Process, ConcurrentQueue<LogLine> Lines) StartCapturing(ServerDefinition definition)
    {
        var lines = new ConcurrentQueue<LogLine>();
        var process = ServerProcess.Start(definition, Settings);
        process.LineReceived += lines.Enqueue;
        return (process, lines);
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

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task BatchScript_StreamsOutputLines()
    {
        var (process, lines) = StartCapturing(Definition("chatty.bat"));
        using var _ = process;

        var exitCode = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(30));

        exitCode.Should().Be(0);
        var captured = lines.Where(l => l.Text.StartsWith("LINE ")).ToList();
        captured.Should().HaveCount(200, "the fixture emits exactly 200 numbered lines");
        captured[0].Text.Should().Be("LINE 1");
        captured[^1].Text.Should().Be("LINE 200");
        captured.Should().OnlyContain(l => l.Stream == LogStream.StandardOutput);
    }

    [Fact]
    public async Task CrashingScript_SurfacesNonZeroExitCode()
    {
        var (process, lines) = StartCapturing(Definition("crashy.bat"));
        using var _ = process;

        var exitCode = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(30));

        exitCode.Should().Be(1, "a non-zero exit is what triggers the restart-on-crash policy");
        lines.Should().Contain(l => l.Text.Contains("CRASHY: starting"));
    }

    [Fact]
    public async Task PowerShellScript_RunsUnderExecutionPolicyBypass()
    {
        var (process, lines) = StartCapturing(Definition("hello.ps1"));
        using var _ = process;

        var exitCode = await process.ExitTask.WaitAsync(TimeSpan.FromSeconds(45));

        exitCode.Should().Be(0);
        lines.Should().Contain(l => l.Text.Contains("HELLO FROM POWERSHELL"));
    }

    [Fact]
    public async Task StopCommand_ShutsServerDownGracefully()
    {
        var (process, lines) = StartCapturing(Definition("interactive.bat", stopCommand: "stop"));
        using var _ = process;

        await WaitUntilAsync(() => lines.Any(l => l.Text.Contains("READY")), TimeSpan.FromSeconds(15));

        var exitedCleanly = await process.StopAsync("stop", TimeSpan.FromSeconds(15));

        exitedCleanly.Should().BeTrue("the server should honour the stop command before any force-kill");
        lines.Should().Contain(l => l.Text.Contains("STOPPING"));
    }

    /// <summary>
    /// The test the whole job-object design exists for. A .bat that spawns a detached
    /// child must not leave that child running after the server is stopped.
    /// </summary>
    [Fact]
    public async Task StoppingServer_LeavesNoOrphanedChildProcesses()
    {
        var (process, _) = StartCapturing(Definition("tree-spawner.bat"));
        using var serverProcess = process;

        var treeGrew = await WaitUntilAsync(
            () => serverProcess.GetTreeProcessIds().Count >= 2,
            TimeSpan.FromSeconds(20));

        treeGrew.Should().BeTrue("the fixture spawns a detached child that must join the job");

        var treePids = serverProcess.GetTreeProcessIds().ToList();
        treePids.Should().HaveCountGreaterThanOrEqualTo(2);
        treePids.Should().Contain(serverProcess.ProcessId);

        serverProcess.Kill();
        await serverProcess.ExitTask.WaitAsync(TimeSpan.FromSeconds(15));

        var allDead = await WaitUntilAsync(
            () => treePids.All(pid => !IsAlive(pid)),
            TimeSpan.FromSeconds(15));

        allDead.Should().BeTrue(
            "every process in the tree must die with the job; survivors are orphaned servers");
    }

    [Fact]
    public async Task Disposing_WithoutExplicitKill_StillTearsDownTheTree()
    {
        // KILL_ON_JOB_CLOSE is the safety net for an unexpected launcher shutdown.
        var (process, _) = StartCapturing(Definition("tree-spawner.bat"));

        await WaitUntilAsync(() => process.GetTreeProcessIds().Count >= 2, TimeSpan.FromSeconds(20));
        var treePids = process.GetTreeProcessIds().ToList();
        treePids.Should().NotBeEmpty();

        process.Dispose();

        var allDead = await WaitUntilAsync(
            () => treePids.All(pid => !IsAlive(pid)),
            TimeSpan.FromSeconds(15));

        allDead.Should().BeTrue("closing the job handle must terminate everything inside it");
    }

    [Fact]
    public async Task StopAsync_ForceKillsAServerThatIgnoresTheStopCommand()
    {
        // tree-spawner never reads stdin, so the graceful phase must time out and kill.
        var (process, lines) = StartCapturing(Definition("tree-spawner.bat", stopCommand: "stop"));
        using var _ = process;

        await WaitUntilAsync(() => process.GetTreeProcessIds().Count >= 2, TimeSpan.FromSeconds(20));

        var exitedCleanly = await process.StopAsync("stop", TimeSpan.FromSeconds(3));

        exitedCleanly.Should().BeFalse("the fixture ignores stdin, so it has to be terminated");
        lines.Should().Contain(l => l.Stream == LogStream.Launcher && l.Text.Contains("terminating process tree"));
        process.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task WriteLine_ReachesTheRunningServer()
    {
        var (process, lines) = StartCapturing(Definition("interactive.bat", stopCommand: "stop"));
        using var _ = process;

        await WaitUntilAsync(() => lines.Any(l => l.Text.Contains("READY")), TimeSpan.FromSeconds(15));

        process.WriteLine("hello there").Should().BeTrue();

        var echoed = await WaitUntilAsync(
            () => lines.Any(l => l.Text.Contains("ECHO: hello there")),
            TimeSpan.FromSeconds(15));

        echoed.Should().BeTrue("stdin is how console commands reach a running game server");

        await process.StopAsync("stop", TimeSpan.FromSeconds(10));
    }
}
