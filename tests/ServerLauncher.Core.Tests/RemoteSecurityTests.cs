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

        // Base64url tokens contain no characters JSON escapes, so a raw search is a fair
        // test that the plaintext is absent.
        contents.Should().NotContain(token, "the plaintext token must never be persisted");

        // The hash is plain base64 and can contain '+' or '/', which the JSON encoder
        // escapes, so compare the parsed value rather than the raw file text.
        var stored = System.Text.Json.JsonDocument.Parse(contents)
            .RootElement[0].GetProperty("TokenHash").GetString();

        stored.Should().Be(DeviceStore.HashToken(token), "only the hash is stored");
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

/// <summary>
/// Covers where the browser interface listens. Binding beyond loopback is what makes a
/// forwarded port reach it, and also what turns a device token into the only thing
/// standing between a stranger and a service that starts processes — so the rules about
/// which address is bound, and about knowing when it is unencrypted, are worth pinning.
/// </summary>
public class HostingRulesTests
{
    [Fact]
    public void TheDefaultIsThisMachineOnly()
    {
        // Turning the feature on must never, by itself, put it on the network.
        var settings = new RemoteAccessSettings { Enabled = true };

        settings.BindAddress.Should().Be("127.0.0.1");
        settings.IsLocalOnly.Should().BeTrue();
    }

    [Fact]
    public void ALoopbackListenerNeedsNoOtherConfiguration()
    {
        var act = () => RemoteApiServer.Validate(new RemoteAccessSettings { Enabled = true });

        act.Should().NotThrow();
    }

    [Fact]
    public void BindingEveryInterfaceIsAllowed()
    {
        // This is what port forwarding needs. It is the user's machine and their call.
        var settings = new RemoteAccessSettings
        {
            Enabled = true,
            BindAddress = RemoteAccessSettings.AllInterfacesAddress
        };

        var act = () => RemoteApiServer.Validate(settings);

        act.Should().NotThrow();
        settings.IsLocalOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("any")]
    [InlineData("ALL")]
    [InlineData("*")]
    [InlineData("192.168.1.50")]
    public void EveryWayOfSayingNotLocalIsUnderstood(string address)
    {
        var settings = new RemoteAccessSettings { BindAddress = address };

        settings.TryResolveBindAddress(out _, out var error).Should().BeTrue(error);
        settings.IsLocalOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("")]
    [InlineData("   ")]
    public void EveryWayOfSayingLocalIsUnderstood(string address)
    {
        // The empty cases matter: a settings.json written before this setting existed has
        // no bind address at all, and must keep behaving as local only.
        new RemoteAccessSettings { BindAddress = address }.IsLocalOnly.Should().BeTrue();
    }

    [Fact]
    public void AnAddressThatIsNotAnAddressIsRejectedRatherThanIgnored()
    {
        // Falling back to loopback silently would leave someone wondering why their
        // forwarded port reaches nothing.
        var settings = new RemoteAccessSettings { BindAddress = "my-server.example.com" };

        settings.TryResolveBindAddress(out _, out var error).Should().BeFalse();
        error.Should().Contain("not an IP address");

        var act = () => RemoteApiServer.Validate(settings);
        act.Should().Throw<InvalidOperationException>().WithMessage("*not an IP address*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public void ImpossiblePortsAreRejected(int port)
    {
        var act = () => RemoteApiServer.Validate(new RemoteAccessSettings { Port = port });

        act.Should().Throw<InvalidOperationException>().WithMessage("*not a usable port*");
    }

    [Fact]
    public void ACertificateTurnsTheSiteIntoHttps()
    {
        new RemoteAccessSettings().Scheme.Should().Be("http");

        new RemoteAccessSettings { CertificateThumbprint = "AABB" }.Scheme.Should().Be("https");
        new RemoteAccessSettings { CertificatePath = @"C:\cert.pfx" }.Scheme.Should().Be("https");
    }

    [Fact]
    public void BeingOnTheNetworkWithoutTlsIsSomethingTheAppCanTell()
    {
        // The settings screen shows a warning off the back of this, so it has to be
        // right in both directions.
        new RemoteAccessSettings
        {
            BindAddress = RemoteAccessSettings.AllInterfacesAddress
        }.IsUnencryptedOnTheNetwork.Should().BeTrue();

        new RemoteAccessSettings
        {
            BindAddress = RemoteAccessSettings.AllInterfacesAddress,
            CertificateThumbprint = "AABB"
        }.IsUnencryptedOnTheNetwork.Should().BeFalse("TLS is configured");

        new RemoteAccessSettings().IsUnencryptedOnTheNetwork
            .Should().BeFalse("nothing but this machine can reach loopback");
    }

    [Fact]
    public void TheAddressToOpenOnThisMachineIsAlwaysLoopback()
    {
        // Whatever interface was bound, a browser here reaches it on 127.0.0.1 — and it
        // certainly cannot open "0.0.0.0".
        var settings = new RemoteAccessSettings
        {
            BindAddress = RemoteAccessSettings.AllInterfacesAddress,
            Port = 9000
        };

        settings.LocalAddress.Should().Be("http://127.0.0.1:9000");
    }

    [Fact]
    public void APairingDeviceIsGivenThePublicAddressWhenThereIsOne()
    {
        var settings = new RemoteAccessSettings
        {
            BindAddress = RemoteAccessSettings.AllInterfacesAddress,
            PublicAddress = "https://servers.example.com/"
        };

        // Trailing slash trimmed, since the client appends its own paths.
        settings.ResolvePairingAddress().Should().Be("https://servers.example.com");
    }

    [Fact]
    public void WithoutAPublicAddressPairingFallsBackToSomethingThatWorksHere()
    {
        new RemoteAccessSettings { Port = 8787 }.ResolvePairingAddress()
            .Should().Be("http://127.0.0.1:8787");
    }

    [Fact]
    public void EditingACopyOfTheSettingsDoesNotTouchTheLiveOnes()
    {
        // The settings dialog edits a clone so that cancelling changes nothing.
        var live = new RemoteAccessSettings { Port = 8787 };

        var copy = live.Clone();
        copy.Port = 9999;
        copy.BindAddress = RemoteAccessSettings.AllInterfacesAddress;

        live.Port.Should().Be(8787);
        live.IsLocalOnly.Should().BeTrue();
    }

    [Fact]
    public void TokensStillGuardTheApiWhereverItListens()
    {
        // Loopback is not a boundary between programs on one machine, and a bound
        // interface is not a boundary at all. The token layer is never redundant.
        var store = new DeviceStore(Path.Combine(
            Path.GetTempPath(), "ServerLauncherHostingTests", Guid.NewGuid().ToString("N"), "devices.json"));

        store.Authenticate("anything-at-all").Should().BeNull();
    }
}
