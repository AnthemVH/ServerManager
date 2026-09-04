using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using FluentAssertions;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Remote;
using ServerLauncher.Core.Storage;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Tests;

/// <summary>
/// Drives the real API over a real Kestrel listener on loopback.
/// </summary>
/// <remarks>
/// Everything here runs against the same code paths a phone would hit, because the
/// interesting failures — an endpoint that forgets its capability check, a route that
/// should not exist — are invisible to unit tests of the pieces.
/// </remarks>
[Collection(ProcessIntegrationCollection.Name)]
public sealed class RemoteApiTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "ServerLauncherApiTests", Guid.NewGuid().ToString("N"));

    private ServerManager _manager = null!;
    private DeviceStore _devices = null!;
    private PairingService _pairing = null!;
    private RemoteApiServer _server = null!;
    private HttpClient _client = null!;
    private Guid _serverId;

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _manager = new ServerManager(new ConfigurationStore(
            Path.Combine(_root, "servers.json"),
            Path.Combine(_root, "settings.json")));

        var instance = _manager.Add(new ServerDefinition
        {
            Name = "Test Server",
            ScriptPath = Path.Combine(AppContext.BaseDirectory, "fixtures", "interactive.bat"),
            StopCommand = "stop"
        });

        _serverId = instance.Id;

        _devices = new DeviceStore(Path.Combine(_root, "devices.json"));
        _pairing = new PairingService(_devices);
        _server = new RemoteApiServer(_manager, _devices, _pairing, new RemoteAuditLog(
            Path.Combine(_root, "audit.log")));

        var port = FreePort();
        await _server.StartAsync(new RemoteAccessSettings
        {
            Enabled = true,
            BindAddress = "127.0.0.1",
            Port = port
        });

        _client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        _server?.Stop();
        _manager?.Dispose();

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

        return Task.CompletedTask;
    }

    /// <summary>Pairs a device and returns a client already carrying its token.</summary>
    private async Task<HttpClient> PairAsync(DeviceCapabilities capabilities = DeviceCapabilities.Default)
    {
        var code = _pairing.BeginPairing();

        var response = await _client.PostAsJsonAsync(
            "/api/v1/pair", new PairRequest(code, "Test phone"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var paired = await response.Content.ReadFromJsonAsync<PairResponse>();
        paired.Should().NotBeNull();

        _devices.SetCapabilities(paired!.DeviceId, capabilities);

        var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", paired.Token);

        return client;
    }

    // --- Browser interface ---

    [Fact]
    public async Task TheBrowserInterfaceIsServedWithoutAToken()
    {
        // It has to be reachable before a token exists, since pairing happens on it.
        var response = await _client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("ServerManager");
        html.Should().Contain("Pairing code", "the page must offer the pairing form");
    }

    [Fact]
    public async Task TheBrowserInterfaceServesTheSamePageForIndexHtml()
    {
        (await _client.GetAsync("/index.html")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ServingThePageDoesNotExposeTheFilesystem()
    {
        // Only one embedded page is ever served, so no request path is turned into a file
        // path. These would be traversal attempts if it were.
        var attempts = new[]
        {
            "/../../../../Windows/System32/drivers/etc/hosts",
            "/..%2f..%2fwindows%2fwin.ini",
            "/servers.json",
            "/devices.json",
            "/appsettings.json",
        };

        foreach (var attempt in attempts)
        {
            var response = await _client.GetAsync(attempt);

            response.StatusCode.Should().NotBe(HttpStatusCode.OK,
                $"{attempt} must not resolve to anything on disk");
        }
    }

    [Fact]
    public async Task TheApiStillRequiresATokenEvenThoughThePageIsPublic()
    {
        // Serving the page anonymously must not have opened up the data behind it.
        (await _client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/api/v1/servers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // --- The boundary the whole design rests on ---

    [Fact]
    public async Task NoEndpointCanCreateOrModifyAServer()
    {
        // ServerManager launches arbitrary scripts, so an endpoint that could set a script
        // path would turn a leaked token into remote code execution. If someone later adds
        // one, this fails.
        using var client = await PairAsync(
            DeviceCapabilities.Default | DeviceCapabilities.SendCommands);

        var before = _manager.Instances.Count;

        var payload = JsonContent.Create(new
        {
            name = "Evil",
            scriptPath = @"C:\Windows\System32\cmd.exe",
            arguments = "/c calc.exe"
        });

        var attempts = new[]
        {
            await client.PostAsync("/api/v1/servers", payload),
            await client.PutAsync($"/api/v1/servers/{_serverId}", payload),
            await client.PostAsync($"/api/v1/servers/{_serverId}", payload),
            await client.DeleteAsync($"/api/v1/servers/{_serverId}"),
            await client.PostAsync("/api/v1/servers/add", payload),
            await client.PostAsync("/api/v1/settings", payload)
        };

        foreach (var attempt in attempts)
        {
            attempt.IsSuccessStatusCode.Should().BeFalse(
                $"{attempt.RequestMessage?.Method} {attempt.RequestMessage?.RequestUri?.PathAndQuery} "
                + "must not be a working route");
        }

        _manager.Instances.Count.Should().Be(before, "no server may be created or removed remotely");
        _manager.Instances.Single().Definition.ScriptPath.Should().EndWith("interactive.bat",
            "the script path must be unchanged");
    }

    [Fact]
    public async Task SendingCommandsIsRefusedWithoutThatCapability()
    {
        // Paired devices do not get this by default; it is arbitrary input to a game server.
        using var client = await PairAsync(DeviceCapabilities.Default);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/servers/{_serverId}/command", new CommandRequest("say hello"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var error = await response.Content.ReadFromJsonAsync<ApiError>();
        error!.Error.Should().Contain("send console commands");
    }

    [Fact]
    public async Task SendingCommandsIsAllowedOnceGranted()
    {
        using var client = await PairAsync(
            DeviceCapabilities.Default | DeviceCapabilities.SendCommands);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/servers/{_serverId}/command", new CommandRequest("say hello"));

        // The server is not running, so the command cannot be delivered — but the request
        // is authorised, which is what this test is about.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- Authentication ---

    [Fact]
    public async Task RequestsWithoutATokenAreRefused()
    {
        var response = await _client.GetAsync("/api/v1/servers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnInventedTokenIsRefused()
    {
        using var client = new HttpClient { BaseAddress = _client.BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", DeviceStore.GenerateToken());

        var response = await client.GetAsync("/api/v1/servers");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARevokedDeviceLosesAccessImmediately()
    {
        using var client = await PairAsync();
        (await client.GetAsync("/api/v1/servers")).StatusCode.Should().Be(HttpStatusCode.OK);

        _devices.Revoke(_devices.Devices.Single().Id);

        (await client.GetAsync("/api/v1/servers")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PairingRequiresACurrentCode()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/pair", new PairRequest("BOGUS123", "Uninvited"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _devices.Devices.Should().BeEmpty();
    }

    // --- Reading ---

    [Fact]
    public async Task ServersAreListedWithTheirState()
    {
        using var client = await PairAsync();

        var servers = await client.GetFromJsonAsync<List<ServerSummary>>("/api/v1/servers");

        servers.Should().ContainSingle();
        servers![0].Name.Should().Be("Test Server");
        servers[0].State.Should().Be("Stopped");
        servers[0].CanStart.Should().BeTrue();
        servers[0].CanStop.Should().BeFalse();
    }

    [Fact]
    public async Task ASingleServerCanBeFetched()
    {
        using var client = await PairAsync();

        var server = await client.GetFromJsonAsync<ServerSummary>($"/api/v1/servers/{_serverId}");

        server!.Id.Should().Be(_serverId.ToString());
    }

    [Fact]
    public async Task AnUnknownServerIsNotFound()
    {
        using var client = await PairAsync();

        var response = await client.GetAsync($"/api/v1/servers/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HealthReportsTheLauncherItself()
    {
        using var client = await PairAsync();

        var health = await client.GetFromJsonAsync<LauncherHealth>("/api/v1/health");

        health!.TotalServers.Should().Be(1);
        health.RunningServers.Should().Be(0);
        health.ThreadCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ConsoleReadingIsRefusedWithoutThatCapability()
    {
        using var client = await PairAsync(DeviceCapabilities.View);

        var response = await client.GetAsync($"/api/v1/servers/{_serverId}/console");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ViewingIsRefusedWithoutThatCapability()
    {
        using var client = await PairAsync(DeviceCapabilities.None);

        var response = await client.GetAsync("/api/v1/servers");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // --- Controlling ---

    [Fact]
    public async Task ControlIsRefusedWithoutThatCapability()
    {
        using var client = await PairAsync(DeviceCapabilities.View);

        var response = await client.PostAsync($"/api/v1/servers/{_serverId}/start", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AServerCanBeStartedAndStoppedRemotely()
    {
        using var client = await PairAsync();

        var started = await client.PostAsync($"/api/v1/servers/{_serverId}/start", null);
        started.StatusCode.Should().Be(HttpStatusCode.OK);

        _manager.Find(_serverId)!.State.Should().Be(ServerState.Running);

        var stopped = await client.PostAsync($"/api/v1/servers/{_serverId}/stop", null);
        stopped.StatusCode.Should().Be(HttpStatusCode.OK);

        _manager.Find(_serverId)!.State.Should().Be(ServerState.Stopped);
    }

    [Fact]
    public async Task AnUnknownActionIsRejected()
    {
        using var client = await PairAsync();

        var response = await client.PostAsync($"/api/v1/servers/{_serverId}/selfdestruct", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoteActionsAreWrittenToTheAuditLog()
    {
        // A server restarting on its own should be traceable to the device that asked.
        using var client = await PairAsync();

        await client.PostAsync($"/api/v1/servers/{_serverId}/start", null);
        await client.PostAsync($"/api/v1/servers/{_serverId}/stop", null);

        var audit = await File.ReadAllTextAsync(Path.Combine(_root, "audit.log"));

        audit.Should().Contain("Test phone");
        audit.Should().Contain("Started server");
        audit.Should().Contain("Stopped server");
        audit.Should().Contain("Test Server");
    }
}
