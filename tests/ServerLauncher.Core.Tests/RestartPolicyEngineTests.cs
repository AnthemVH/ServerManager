using FluentAssertions;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Tests;

public class RestartPolicyEngineTests
{
    private static ServerDefinition Definition(
        RestartPolicy policy = RestartPolicy.OnCrash,
        int maxRestarts = 5,
        int stableMinutes = 5) => new()
        {
            Name = "Test",
            ScriptPath = @"C:\servers\start.bat",
            RestartPolicy = policy,
            MaxConsecutiveRestarts = maxRestarts,
            StableUptimeMinutes = stableMinutes
        };

    private static readonly TimeSpan ShortRun = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LongRun = TimeSpan.FromHours(6);

    [Fact]
    public void OperatorStop_NeverRestarts_EvenUnderAlwaysPolicy()
    {
        // Clicking Stop must be final; anything else makes the button feel broken.
        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.Always), exitCode: 0, operatorInitiated: true, ShortRun, 0);

        decision.ShouldRestart.Should().BeFalse();
        decision.ResultingState.Should().Be(ServerState.Stopped);
        decision.ConsecutiveFailures.Should().Be(0);
    }

    [Fact]
    public void OperatorStop_OfACrashingServer_StillDoesNotRestart()
    {
        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.Always), exitCode: 1, operatorInitiated: true, ShortRun, 3);

        decision.ShouldRestart.Should().BeFalse();
        decision.ConsecutiveFailures.Should().Be(0, "an operator stop clears the crash history");
    }

    [Fact]
    public void NeverPolicy_DoesNotRestart_ButStillReportsACrash()
    {
        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.Never), exitCode: 1, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeFalse();
        decision.ResultingState.Should().Be(ServerState.Crashed);
    }

    [Fact]
    public void OnCrashPolicy_IgnoresACleanExit()
    {
        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.OnCrash), exitCode: 0, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeFalse();
        decision.ResultingState.Should().Be(ServerState.Stopped);
    }

    [Fact]
    public void OnCrashPolicy_RestartsAfterANonZeroExit()
    {
        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.OnCrash), exitCode: 1, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeTrue();
        decision.ConsecutiveFailures.Should().Be(1);
        decision.Delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void AlwaysPolicy_RestartsEvenOnACleanExit()
    {
        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.Always), exitCode: 0, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeTrue();
    }

    [Fact]
    public void ClosingAServersOwnWindow_CountsAsAStopNotACrash()
    {
        // Closing a console window makes Windows end the process with STATUS_CONTROL_C_EXIT.
        // Reading that as a crash restarted a server the user had just closed by hand.
        const int statusControlCExit = unchecked((int)0xC000013A);

        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.OnCrash), statusControlCExit, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeFalse();
        decision.ResultingState.Should().Be(ServerState.Stopped);
        decision.Reason.Should().Contain("Closed by hand");
    }

    [Fact]
    public void CtrlBreak_AlsoCountsAsAStop()
    {
        const int dbgControlBreak = unchecked((int)0x40010004);

        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.OnCrash), dbgControlBreak, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeFalse();
        decision.ResultingState.Should().Be(ServerState.Stopped);
    }

    [Fact]
    public void UserTerminationUnderNeverPolicy_ShowsStoppedRatherThanCrashed()
    {
        const int statusControlCExit = unchecked((int)0xC000013A);

        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.Never), statusControlCExit, operatorInitiated: false, ShortRun, 0);

        decision.ResultingState.Should().Be(ServerState.Stopped,
            "the user closed it, so calling it crashed would be wrong");
    }

    [Fact]
    public void AGenuineCrashIsStillTreatedAsACrash()
    {
        // The point of the change is not to stop restarting real failures.
        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.OnCrash), exitCode: 1, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeTrue();
        decision.ResultingState.Should().Be(ServerState.Crashed);
    }

    [Fact]
    public void ServerSpecificCleanExitCodesAreHonoured()
    {
        var definition = Definition();
        definition.CleanExitCodes.Add(7);

        var decision = RestartPolicyEngine.Evaluate(definition, 7, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeFalse();
        decision.ResultingState.Should().Be(ServerState.Stopped);
    }

    [Fact]
    public void AlwaysPolicy_StillRestartsAfterAManualClose()
    {
        // Always means always; someone choosing it wants the server back regardless.
        const int statusControlCExit = unchecked((int)0xC000013A);

        var decision = RestartPolicyEngine.Evaluate(
            Definition(RestartPolicy.Always), statusControlCExit, operatorInitiated: false, ShortRun, 0);

        decision.ShouldRestart.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void IsCleanExit_ClassifiesOrdinaryCodes(int exitCode, bool expected)
    {
        RestartPolicyEngine.IsCleanExit(exitCode, Definition()).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 30)]
    [InlineData(4, 60)]
    [InlineData(9, 60)]
    public void Backoff_GrowsThenCaps(int failures, int expectedSeconds)
    {
        RestartPolicyEngine.BackoffFor(failures).Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void RepeatedFailures_EscalateTheBackoffDelay()
    {
        var definition = Definition();

        var first = RestartPolicyEngine.Evaluate(definition, 1, false, ShortRun, 0);
        var second = RestartPolicyEngine.Evaluate(definition, 1, false, ShortRun, first.ConsecutiveFailures);
        var third = RestartPolicyEngine.Evaluate(definition, 1, false, ShortRun, second.ConsecutiveFailures);

        first.Delay.Should().Be(TimeSpan.FromSeconds(5));
        second.Delay.Should().Be(TimeSpan.FromSeconds(10));
        third.Delay.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void ExceedingMaxRestarts_ParksTheServerInFailed()
    {
        var definition = Definition(maxRestarts: 3);

        var decision = RestartPolicyEngine.Evaluate(definition, 1, false, ShortRun, consecutiveFailures: 3);

        decision.ShouldRestart.Should().BeFalse();
        decision.ResultingState.Should().Be(ServerState.Failed);
        decision.Reason.Should().Contain("giving up");
    }

    [Fact]
    public void RestartsAreAllowedRightUpToTheLimit()
    {
        var definition = Definition(maxRestarts: 3);

        var decision = RestartPolicyEngine.Evaluate(definition, 1, false, ShortRun, consecutiveFailures: 2);

        decision.ShouldRestart.Should().BeTrue();
        decision.ConsecutiveFailures.Should().Be(3);
    }

    [Fact]
    public void StableUptime_ResetsTheFailureCount()
    {
        // A server that ran fine for six hours before dying is not in a crash loop,
        // so it must not inherit backoff or the failure count from weeks ago.
        var definition = Definition(maxRestarts: 3);

        var decision = RestartPolicyEngine.Evaluate(definition, 1, false, LongRun, consecutiveFailures: 3);

        decision.ShouldRestart.Should().BeTrue("stable uptime clears the earlier failures");
        decision.ConsecutiveFailures.Should().Be(1);
        decision.Delay.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void ShortUptime_DoesNotResetTheFailureCount()
    {
        var definition = Definition(maxRestarts: 5, stableMinutes: 5);

        var decision = RestartPolicyEngine.Evaluate(
            definition, 1, false, TimeSpan.FromMinutes(1), consecutiveFailures: 2);

        decision.ConsecutiveFailures.Should().Be(3, "a one-minute run is still part of the crash loop");
    }

    [Fact]
    public void CrashLoop_EventuallyStopsRatherThanThrashingForever()
    {
        var definition = Definition(maxRestarts: 3);
        var failures = 0;
        var restarts = 0;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var decision = RestartPolicyEngine.Evaluate(definition, 1, false, ShortRun, failures);
            failures = decision.ConsecutiveFailures;

            if (!decision.ShouldRestart)
            {
                decision.ResultingState.Should().Be(ServerState.Failed);
                break;
            }

            restarts++;
        }

        restarts.Should().Be(3, "the server should give up after the configured number of attempts");
    }
}
