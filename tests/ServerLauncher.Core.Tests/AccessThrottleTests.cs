using FluentAssertions;
using ServerLauncher.Core.Remote;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Covers the brute-force protection. Once the API is reachable from the internet it will
/// be found and probed, so a wrong token has to cost the caller something.
/// </summary>
public class AccessThrottleTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private AccessThrottle Create() => new(() => _now);

    [Fact]
    public void AnUnknownAddressIsNotBlocked()
    {
        Create().IsBlocked("203.0.113.5").Should().BeFalse();
    }

    [Fact]
    public void ABlockFollowsEnoughFailures()
    {
        var throttle = Create();

        for (var i = 0; i < AccessThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure("203.0.113.5");
        }

        throttle.IsBlocked("203.0.113.5").Should().BeTrue();
    }

    [Fact]
    public void AFewFailuresAreTolerated()
    {
        // Mistyping a pairing code twice should not lock you out of your own servers.
        var throttle = Create();

        throttle.RecordFailure("203.0.113.5");
        throttle.RecordFailure("203.0.113.5");

        throttle.IsBlocked("203.0.113.5").Should().BeFalse();
    }

    [Fact]
    public void OneAddressCannotBlockAnother()
    {
        // Counting globally would let anyone on the internet lock the owner out simply by
        // failing enough times, turning the protection into the attack.
        var throttle = Create();

        for (var i = 0; i < AccessThrottle.MaxFailures * 2; i++)
        {
            throttle.RecordFailure("198.51.100.9");
        }

        throttle.IsBlocked("198.51.100.9").Should().BeTrue();
        throttle.IsBlocked("203.0.113.5").Should().BeFalse("the owner is a different address");
    }

    [Fact]
    public void ABlockExpires()
    {
        var throttle = Create();

        for (var i = 0; i < AccessThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure("203.0.113.5");
        }

        _now = _now.Add(AccessThrottle.BlockDuration).AddSeconds(1);

        throttle.IsBlocked("203.0.113.5").Should().BeFalse();
    }

    [Fact]
    public void FailuresSpreadOutDoNotAccumulate()
    {
        // A wrong token once a day is someone with a stale phone, not an attack.
        var throttle = Create();

        for (var i = 0; i < AccessThrottle.MaxFailures * 3; i++)
        {
            throttle.RecordFailure("203.0.113.5");
            _now = _now.Add(AccessThrottle.FailureWindow).AddSeconds(1);
        }

        throttle.IsBlocked("203.0.113.5").Should().BeFalse();
    }

    [Fact]
    public void AuthenticatingClearsTheHistory()
    {
        var throttle = Create();

        for (var i = 0; i < AccessThrottle.MaxFailures - 1; i++)
        {
            throttle.RecordFailure("203.0.113.5");
        }

        throttle.RecordSuccess("203.0.113.5");

        // Starting from clean, one more failure is nowhere near the limit.
        throttle.RecordFailure("203.0.113.5");
        throttle.IsBlocked("203.0.113.5").Should().BeFalse();
    }

    [Fact]
    public void RemainingBlockCountsDown()
    {
        var throttle = Create();

        for (var i = 0; i < AccessThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure("203.0.113.5");
        }

        var initial = throttle.RemainingBlock("203.0.113.5");
        _now = _now.AddMinutes(4);
        var later = throttle.RemainingBlock("203.0.113.5");

        initial.Should().BeCloseTo(AccessThrottle.BlockDuration, TimeSpan.FromSeconds(1));
        later.Should().BeLessThan(initial);
    }

    [Fact]
    public void AMissingAddressIsIgnoredRatherThanCrashing()
    {
        // RemoteIpAddress can be null for some connections; that must not throw.
        var throttle = Create();

        var act = () =>
        {
            throttle.RecordFailure(null);
            throttle.RecordSuccess(null);
            throttle.IsBlocked(null);
            throttle.RemainingBlock(null);
        };

        act.Should().NotThrow();
    }

    [Fact]
    public void BlockedAddressesAreListed()
    {
        var throttle = Create();

        for (var i = 0; i < AccessThrottle.MaxFailures; i++)
        {
            throttle.RecordFailure("203.0.113.5");
        }

        throttle.RecordFailure("198.51.100.9");

        throttle.BlockedAddresses().Should().ContainSingle().Which.Should().Be("203.0.113.5");
    }
}
