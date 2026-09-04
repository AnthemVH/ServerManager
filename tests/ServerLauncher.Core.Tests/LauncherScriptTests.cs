using System.Diagnostics;
using FluentAssertions;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Covers "launcher" scripts: scripts that start the real server and then exit
/// immediately rather than staying alive for its lifetime.
///
/// The Arma 3 server scripts work this way — they build a ProcessStartInfo, call
/// Start(), and fall off the end of the file. Treating the script's exit as the
/// server's exit tore down a server that had only just started.
/// </summary>
[Collection(ProcessIntegrationCollection.Name)]
public sealed class LauncherScriptTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "ServerLauncherLauncherTests", Guid.NewGuid().ToString("N"));

    private readonly List<ServerInstance> _created = new();

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private ServerInstance Create(ServerDefinition definition)
    {
        var instance = new ServerInstance(
            definition, new AppSettings(), Path.Combine(_tempRoot, definition.Id.ToString("N")));
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

            await Task.Delay(150);
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
    public async Task ScriptThatStartsAServerAndExits_LeavesTheServerRunning()
    {
        // The regression: the script exiting used to dispose the job object, and
        // KILL_ON_JOB_CLOSE then killed the server the script had just launched.
        var instance = Create(new ServerDefinition
        {
            Name = "Arma-style launcher",
            ScriptPath = Fixture("launcher-detach.ps1"),
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();

        var launched = await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Any(l => l.Text.Contains("exiting immediately")),
            TimeSpan.FromSeconds(60));

        launched.Should().BeTrue("the launcher script should run to completion");

        // Wait for the supervisor to actually decide, rather than guessing at how long
        // draining the script's output and confirming the survivors takes.
        var detached = await WaitUntilAsync(
            () => instance.IsLauncherDetached, TimeSpan.FromSeconds(45));

        detached.Should().BeTrue("the supervisor should recognise a launcher script");

        instance.State.Should().Be(ServerState.Running,
            "the server the script started is still alive, so the server is still up");

        instance.Poll();
        instance.LastSample.ProcessCount.Should().BeGreaterThan(0,
            "the surviving child is what we are now supervising");

        await instance.StopAsync();
        await WaitUntilAsync(() => instance.State == ServerState.Stopped, TimeSpan.FromSeconds(20));
        instance.State.Should().Be(ServerState.Stopped);
    }

    [Fact]
    public async Task StoppingALauncherStartedServer_KillsTheProcessItLeftBehind()
    {
        var instance = Create(new ServerDefinition
        {
            Name = "Arma-style launcher",
            ScriptPath = Fixture("launcher-detach.ps1"),
            RestartPolicy = RestartPolicy.Never
        });

        await instance.StartAsync();
        await WaitUntilAsync(
            () => instance.ConsoleSnapshot().Any(l => l.Text.Contains("exiting immediately")),
            TimeSpan.FromSeconds(60));

        var detached = await WaitUntilAsync(
            () => instance.IsLauncherDetached, TimeSpan.FromSeconds(45));

        detached.Should().BeTrue("the supervisor should recognise a launcher script");

        instance.Poll();
        var survivors = instance.TreeProcessIds().ToList();
        survivors.Should().NotBeEmpty("the launched server is still running");

        await instance.StopAsync();

        var allDead = await WaitUntilAsync(
            () => survivors.All(pid => !IsAlive(pid)),
            TimeSpan.FromSeconds(20));

        allDead.Should().BeTrue("stopping must kill what the launcher started, not just the script");
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
