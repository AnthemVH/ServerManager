using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Supervision;

/// <summary>
/// Decides whether an exited server should be relaunched. Written as a pure function
/// so the awkward cases — an operator stop racing a crash, a server that crashes once
/// a week, a server crash-looping on a bad config — can be tested directly.
/// </summary>
public static class RestartPolicyEngine
{
    /// <summary>Backoff steps applied to successive failures; the last value repeats.</summary>
    public static readonly TimeSpan[] BackoffSchedule =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    };

    /// <summary>
    /// Exit codes Windows produces when a user ends a process themselves rather than it
    /// failing: closing the console window, or Ctrl+C / Ctrl+Break. Treating these as
    /// crashes made a manually closed server restart itself, which is the opposite of
    /// what closing it meant.
    /// </summary>
    public static readonly IReadOnlyList<int> UserTerminationExitCodes = new[]
    {
        unchecked((int)0xC000013A), // STATUS_CONTROL_C_EXIT - console closed, or Ctrl+C
        unchecked((int)0x40010004)  // DBG_CONTROL_BREAK - Ctrl+Break
    };

    /// <summary>
    /// Whether an exit code represents a clean stop: success, a user closing the server
    /// themselves, or a code the server has been configured to report on shutdown.
    /// </summary>
    public static bool IsCleanExit(int exitCode, ServerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return exitCode == 0
               || UserTerminationExitCodes.Contains(exitCode)
               || definition.CleanExitCodes.Contains(exitCode);
    }

    public static TimeSpan BackoffFor(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0)
        {
            return BackoffSchedule[0];
        }

        var index = Math.Min(consecutiveFailures - 1, BackoffSchedule.Length - 1);
        return BackoffSchedule[index];
    }

    /// <param name="definition">The server's configured policy.</param>
    /// <param name="exitCode">Process exit code; non-zero counts as a crash.</param>
    /// <param name="operatorInitiated">True when the user asked for the stop.</param>
    /// <param name="uptime">How long the server stayed up this run.</param>
    /// <param name="consecutiveFailures">Failures accumulated before this exit.</param>
    public static RestartDecision Evaluate(
        ServerDefinition definition,
        int exitCode,
        bool operatorInitiated,
        TimeSpan uptime,
        int consecutiveFailures)
    {
        ArgumentNullException.ThrowIfNull(definition);

        // A user clicking Stop must never trigger a restart, whatever the policy says.
        if (operatorInitiated)
        {
            return new RestartDecision(false, TimeSpan.Zero, 0, ServerState.Stopped,
                "Stopped by operator.");
        }

        if (definition.RestartPolicy == RestartPolicy.Never)
        {
            return new RestartDecision(false, TimeSpan.Zero, 0,
                IsCleanExit(exitCode, definition) ? ServerState.Stopped : ServerState.Crashed,
                $"Exited with code {exitCode}; restart policy is Never.");
        }

        var cleanExit = IsCleanExit(exitCode, definition);

        if (definition.RestartPolicy == RestartPolicy.OnCrash && cleanExit)
        {
            return new RestartDecision(false, TimeSpan.Zero, 0, ServerState.Stopped,
                UserTerminationExitCodes.Contains(exitCode)
                    ? "Closed by hand rather than crashing, so it will not be restarted."
                    : "Exited cleanly; restart policy is OnCrash.");
        }

        // Staying up for a decent stretch means the previous failures are not part of a
        // crash loop, so the counter resets and backoff starts from the beginning again.
        var stableThreshold = TimeSpan.FromMinutes(Math.Max(1, definition.StableUptimeMinutes));
        var priorFailures = uptime >= stableThreshold ? 0 : consecutiveFailures;

        var failures = priorFailures + 1;

        if (failures > Math.Max(1, definition.MaxConsecutiveRestarts))
        {
            return new RestartDecision(false, TimeSpan.Zero, failures, ServerState.Failed,
                $"Failed {failures - 1} times in a row; giving up until restarted manually.");
        }

        var delay = BackoffFor(failures);
        var trigger = cleanExit ? "Exited cleanly" : $"Exited with code {exitCode}";

        return new RestartDecision(true, delay, failures, ServerState.Crashed,
            $"{trigger}; restarting in {delay.TotalSeconds:0}s (attempt {failures} of {definition.MaxConsecutiveRestarts}).");
    }
}
