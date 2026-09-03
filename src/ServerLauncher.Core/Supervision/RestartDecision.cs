using ServerLauncher.Core.Models;

namespace ServerLauncher.Core.Supervision;

/// <summary>The outcome of evaluating what to do after a server exits.</summary>
/// <param name="ShouldRestart">Whether the supervisor should relaunch the server.</param>
/// <param name="Delay">How long to wait before relaunching.</param>
/// <param name="ConsecutiveFailures">The updated failure count to carry forward.</param>
/// <param name="ResultingState">The state to display while waiting.</param>
/// <param name="Reason">Human-readable explanation, written to the console log.</param>
public readonly record struct RestartDecision(
    bool ShouldRestart,
    TimeSpan Delay,
    int ConsecutiveFailures,
    ServerState ResultingState,
    string Reason);
