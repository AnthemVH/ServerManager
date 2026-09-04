using System.Net;
using FluentAssertions;
using ServerLauncher.Core.Remote;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Covers the credential handling behind remote control. These are the parts where a
/// mistake is not a bug but a way in.
/// </summary>
public sealed class DeviceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ServerLauncherDeviceTests", Guid.NewGuid().ToString("N"));

    public DeviceStoreTests() => Directory.CreateDirectory(_root);

    private DeviceStore CreateStore() => new(Path.Combine(_root, "devices.json"));

    [Fact]
    public void TokensAreNeverWrittenToDisk()
    {
        // The whole point of hashing: a copy of devices.json must be useless for logging in.
        var store = CreateStore();
        var token = DeviceStore.GenerateToken();

        store.Add("Phone", token, DeviceCapabilities.Default);

        var contents = File.ReadAllText(Path.Combine(_root, "devices.json"));
        contents.Should().NotContain(token, "the plaintext token must never be persisted");
        contents.Should().Contain(DeviceStore.HashToken(token), "only the hash is stored");
    }

    [Fact]
    public void GeneratedTokensAreLongAndDistinct()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => DeviceStore.GenerateToken()).ToList();

        tokens.Should().OnlyHaveUniqueItems();
        tokens.Should().OnlyContain(t => t.Length >= 40, "256 bits of randomness, base64url encoded");
    }

    [Fact]
    public void AValidTokenAuthenticates()
    {
        var store = CreateStore();
        var token = DeviceStore.GenerateToken();
        var device = store.Add("Phone", token, DeviceCapabilities.Default);

        store.Authenticate(token)!.Id.Should().Be(device.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public void RubbishTokensAreRejected(string? token)
    {
        var store = CreateStore();
        store.Add("Phone", DeviceStore.GenerateToken(), DeviceCapabilities.Default);

        store.Authenticate(token).Should().BeNull();
    }

    [Fact]
    public void AnotherInstallsTokenDoesNotWorkHere()
    {
        // The isolation requirement: every install generates its own credentials, so a
        // token paired against one copy of ServerManager is meaningless to another.
        var mine = CreateStore();
        var theirs = new DeviceStore(Path.Combine(_root, "other-devices.json"));

        var theirToken = DeviceStore.GenerateToken();
        theirs.Add("Their phone", theirToken, DeviceCapabilities.Default);
        mine.Add("My phone", DeviceStore.GenerateToken(), DeviceCapabilities.Default);

        mine.Authenticate(theirToken).Should().BeNull();
    }

    [Fact]
    public void RevokingADeviceStopsItsToken()
    {
        var store = CreateStore();
        var token = DeviceStore.GenerateToken();
        var device = store.Add("Phone", token, DeviceCapabilities.Default);

        store.Revoke(device.Id).Should().BeTrue();

        store.Authenticate(token).Should().BeNull("a revoked device must lose access at once");
    }

    [Fact]
    public void RevocationSurvivesARestart()
    {
        var store = CreateStore();
        var token = DeviceStore.GenerateToken();
        var device = store.Add("Phone", token, DeviceCapabilities.Default);
        store.Revoke(device.Id);

        var reloaded = CreateStore();

        reloaded.Authenticate(token).Should().BeNull();
    }

    [Fact]
    public void NewDevicesCannotSendConsoleCommands()
    {
        // Sending commands is arbitrary input to a game server, so it is granted
        // deliberately rather than handed out at pairing.
        var store = CreateStore();
        var device = store.Add("Phone", DeviceStore.GenerateToken(), DeviceCapabilities.Default);

        device.Can(DeviceCapabilities.View).Should().BeTrue();
        device.Can(DeviceCapabilities.Control).Should().BeTrue();
        device.Can(DeviceCapabilities.ReadConsole).Should().BeTrue();
        device.Can(DeviceCapabilities.SendCommands).Should().BeFalse();
    }

    [Fact]
    public void CapabilitiesCanBeGrantedLater()
    {
        var store = CreateStore();
        var device = store.Add("Phone", DeviceStore.GenerateToken(), DeviceCapabilities.Default);

        store.SetCapabilities(device.Id, DeviceCapabilities.Default | DeviceCapabilities.SendCommands);

        store.Devices.Single().Can(DeviceCapabilities.SendCommands).Should().BeTrue();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

public sealed class PairingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ServerLauncherPairingTests", Guid.NewGuid().ToString("N"));

    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public PairingServiceTests() => Directory.CreateDirectory(_root);

    private PairingService CreateService(out DeviceStore store)
    {
        store = new DeviceStore(Path.Combine(_root, "devices.json"));
        return new PairingService(store, () => _now);
    }

    [Fact]
    public void AValidCodePairsTheDevice()
    {
        var service = CreateService(out var store);
        var code = service.BeginPairing();

        var result = service.Redeem(code, "Pixel", DeviceCapabilities.Default);

        result.Success.Should().BeTrue(result.Error);
        result.Token.Should().NotBeNullOrWhiteSpace();
        store.Authenticate(result.Token).Should().NotBeNull();
    }

    [Fact]
    public void ACodeWorksOnlyOnce()
    {
        // Otherwise a QR left on screen, or a photo of it, would keep pairing devices.
        var service = CreateService(out _);
        var code = service.BeginPairing();

        service.Redeem(code, "First", DeviceCapabilities.Default).Success.Should().BeTrue();
        service.Redeem(code, "Second", DeviceCapabilities.Default).Success.Should().BeFalse();
    }

    [Fact]
    public void ACodeExpires()
    {
        var service = CreateService(out _);
        var code = service.BeginPairing();

        _now = _now.Add(PairingService.CodeLifetime).AddSeconds(1);

        var result = service.Redeem(code, "Late", DeviceCapabilities.Default);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("expired");
    }

    [Fact]
    public void PairingIsImpossibleWhenNoCodeIsOutstanding()
    {
        // Nothing can pair while the user is not actively looking at the pairing dialog.
        var service = CreateService(out _);

        service.Redeem("ABCD2345", "Uninvited", DeviceCapabilities.Default)
            .Success.Should().BeFalse();
    }

    [Fact]
    public void CancellingPairingInvalidatesTheCode()
    {
        var service = CreateService(out _);
        var code = service.BeginPairing();

        service.CancelPairing();

        service.Redeem(code, "Too late", DeviceCapabilities.Default).Success.Should().BeFalse();
    }

    [Fact]
    public void GuessingIsRateLimited()
    {
        var service = CreateService(out _);
        service.BeginPairing();

        for (var i = 0; i < PairingService.MaxFailedAttempts; i++)
        {
            service.Redeem("WRONGONE", "Attacker", DeviceCapabilities.Default)
                .Success.Should().BeFalse();
        }

        var blocked = service.Redeem("WRONGONE", "Attacker", DeviceCapabilities.Default);
        blocked.Error.Should().Contain("Too many pairing attempts");
    }

    [Fact]
    public void TheRateLimitLiftsAfterTheWindow()
    {
        var service = CreateService(out _);
        service.BeginPairing();

        for (var i = 0; i < PairingService.MaxFailedAttempts; i++)
        {
            service.Redeem("WRONGONE", "Attacker", DeviceCapabilities.Default);
        }

        _now = _now.Add(PairingService.FailureWindow).AddSeconds(1);
        var code = service.BeginPairing();

        service.Redeem(code, "Legitimate", DeviceCapabilities.Default).Success.Should().BeTrue();
    }

    [Fact]
    public void EachCodeIsDifferent()
    {
        var service = CreateService(out _);

        var codes = Enumerable.Range(0, 100).Select(_ => service.BeginPairing()).ToList();

        codes.Should().OnlyHaveUniqueItems();
        codes.Should().OnlyContain(c => c.Length == 8);
    }

    [Fact]
    public void CodesAvoidCharactersThatAreEasilyMisread()
    {
        var service = CreateService(out _);

        for (var i = 0; i < 200; i++)
        {
            service.BeginPairing().Should().NotContainAny("0", "O", "1", "I");
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Covers the rule that the API must never be exposed carelessly.</summary>
public class BindAddressTests
{
    [Fact]
    public void ListeningOnAllInterfacesIsRefusedByDefault()
    {
        var settings = new RemoteAccessSettings { BindAddress = "0.0.0.0" };

        var act = () => RemoteApiServer.ResolveBindAddress(settings);

        act.Should().Throw<InvalidOperationException>().WithMessage("*all interfaces*");
    }

    [Fact]
    public void AnOrdinaryLanAddressIsRefusedByDefault()
    {
        // A LAN address is routable from the rest of the network and, behind a forwarded
        // port, from the internet. It needs the same deliberate opt-in.
        var settings = new RemoteAccessSettings { BindAddress = "192.168.1.50" };

        var act = () => RemoteApiServer.ResolveBindAddress(settings);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a Tailscale address*");
    }

    [Fact]
    public void AnEmptyBindAddressMeansLoopback()
    {
        // The default configuration binds nothing routable; Tailscale Serve publishes it.
        RemoteApiServer.ResolveBindAddress(new RemoteAccessSettings())
            .Should().Be(IPAddress.Loopback);
    }

    [Fact]
    public void ATailscaleAddressIsAccepted()
    {
        var settings = new RemoteAccessSettings { BindAddress = "100.101.102.103" };

        RemoteApiServer.ResolveBindAddress(settings).Should().Be(IPAddress.Parse("100.101.102.103"));
    }

    [Fact]
    public void LoopbackIsAlwaysAllowed()
    {
        // Reachable only from this machine, so it needs no override.
        var settings = new RemoteAccessSettings { BindAddress = "127.0.0.1" };

        RemoteApiServer.ResolveBindAddress(settings).Should().Be(IPAddress.Loopback);
    }

    [Fact]
    public void TheOverrideAllowsANonTailscaleAddress()
    {
        var settings = new RemoteAccessSettings
        {
            BindAddress = "192.168.1.50",
            AllowNonTailscaleBinding = true
        };

        RemoteApiServer.ResolveBindAddress(settings).Should().Be(IPAddress.Parse("192.168.1.50"));
    }

    [Fact]
    public void NonsenseAddressesAreRejected()
    {
        var settings = new RemoteAccessSettings { BindAddress = "not an address" };

        var act = () => RemoteApiServer.ResolveBindAddress(settings);

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a valid IP address*");
    }

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.100.50.20", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("192.168.0.1", false)]
    [InlineData("10.0.0.1", false)]
    public void TheTailscaleRangeIsRecognisedCorrectly(string address, bool expected)
    {
        TailscaleDetector.IsTailscaleAddress(address).Should().Be(expected);
    }
}
