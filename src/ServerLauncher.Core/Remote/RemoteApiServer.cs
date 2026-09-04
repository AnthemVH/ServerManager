using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Remote;

/// <summary>
/// Hosts the remote control API.
/// </summary>
/// <remarks>
/// <para>
/// Uses <see cref="HttpListener"/> and binds loopback by default. Kestrel was tried and
/// rejected: it needs a framework reference to Microsoft.AspNetCore.App, which becomes a
/// <em>required framework</em> in the app's runtimeconfig. The app would then refuse to
/// start on any machine without the ASP.NET Core runtime — including every existing
/// install the moment it auto-updated, leaving a dead app and stopped servers with no way
/// to warn anyone. Not worth it for a feature most installs will never switch on.
/// </para>
/// <para>
/// Loopback also sidesteps http.sys URL reservations, which would otherwise demand
/// administrator rights that this app deliberately does not take. Tailscale Serve
/// publishes the local port onto the tailnet, which is what makes it reachable from a
/// phone — and it terminates TLS with a real Tailscale-issued certificate on the way.
/// </para>
/// <para>
/// The API can start, stop and inspect servers that already exist. There is no endpoint
/// that creates or edits one, because this app launches arbitrary scripts: an endpoint
/// that could set a script path would turn a leaked token into remote code execution.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RemoteApiServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ServerManager _manager;
    private readonly DeviceStore _devices;
    private readonly PairingService _pairing;
    private readonly RemoteAuditLog _audit;

    private HttpListener? _listener;
    private CancellationTokenSource? _shutdown;
    private Task? _loop;

    public RemoteApiServer(
        ServerManager manager,
        DeviceStore devices,
        PairingService pairing,
        RemoteAuditLog audit)
    {
        _manager = manager;
        _devices = devices;
        _pairing = pairing;
        _audit = audit;
    }

    public bool IsRunning => _listener?.IsListening == true;

    /// <summary>Where the API is listening, for display in Settings.</summary>
    public string? ListeningOn { get; private set; }

    /// <summary>
    /// Resolves the address to listen on, refusing anything reachable beyond this machine
    /// unless the user has explicitly opted in.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configuration is not safe to bind.</exception>
    public static IPAddress ResolveBindAddress(RemoteAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.BindAddress))
        {
            return IPAddress.Loopback;
        }

        if (!IPAddress.TryParse(settings.BindAddress.Trim(), out var address))
        {
            throw new InvalidOperationException(
                $"'{settings.BindAddress}' is not a valid IP address.");
        }

        if (address.Equals(IPAddress.Any) && !settings.AllowNonTailscaleBinding)
        {
            throw new InvalidOperationException(
                "Listening on all interfaces would expose server control to every network "
                + "this machine is on. Enable the explicit override in Settings if that is "
                + "really what you want.");
        }

        if (IPAddress.IsLoopback(address))
        {
            return address;
        }

        if (!TailscaleDetector.IsTailscaleAddress(address) && !settings.AllowNonTailscaleBinding)
        {
            throw new InvalidOperationException(
                $"{address} is not a Tailscale address. Binding outside the tailnet removes "
                + "the network layer protecting this API; enable the explicit override in "
                + "Settings to allow it.");
        }

        return address;
    }

    public Task StartAsync(RemoteAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Stop();

        var address = ResolveBindAddress(settings);
        var prefix = $"http://{address}:{settings.Port}/";

        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            listener.Close();

            // Binding anything but loopback goes through http.sys, which requires a URL
            // reservation made by an administrator. Say so, and point at the way round it.
            throw new InvalidOperationException(
                IPAddress.IsLoopback(address)
                    ? $"Could not listen on {prefix}: {ex.Message}"
                    : $"Windows refused to let ServerManager listen on {prefix}. Binding an "
                      + "address other than 127.0.0.1 needs an administrator to reserve the "
                      + "URL. Leave the bind address empty and publish the local port with "
                      + "Tailscale Serve instead.",
                ex);
        }

        _listener = listener;
        _shutdown = new CancellationTokenSource();
        ListeningOn = prefix;

        _loop = Task.Run(() => AcceptLoopAsync(listener, _shutdown.Token));

        return Task.CompletedTask;
    }

    public void Stop()
    {
        var listener = _listener;
        var shutdown = _shutdown;

        _listener = null;
        _shutdown = null;
        ListeningOn = null;

        if (listener is null)
        {
            return;
        }

        try
        {
            shutdown?.Cancel();
            listener.Stop();
            listener.Close();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException)
        {
        }
        finally
        {
            shutdown?.Dispose();
        }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            // Each request is handled off the accept loop so one slow client cannot stall
            // the others.
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(context).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    TrySetStatus(context, HttpStatusCode.InternalServerError);
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }, cancellationToken);
        }
    }

    private static void TrySetStatus(HttpListenerContext context, HttpStatusCode status)
    {
        try
        {
            context.Response.StatusCode = (int)status;
        }
        catch (Exception)
        {
        }
    }

    // --- Request handling ---

    private async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        var method = context.Request.HttpMethod;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Anything that is not the API is the browser interface. Only the one embedded
        // page is ever served — nothing maps a request path onto the filesystem, so there
        // is no traversal to get wrong.
        if (segments.Length == 0 || segments[0] != "api")
        {
            await ServeWebInterfaceAsync(context, path, method).ConfigureAwait(false);
            return;
        }

        // /api/v1/...
        if (segments.Length < 3 || segments[1] != "v1")
        {
            await WriteAsync(context, HttpStatusCode.NotFound, new ApiError("No such endpoint."))
                .ConfigureAwait(false);
            return;
        }

        var route = segments[2..];

        if (route[0] == "pair")
        {
            await HandlePairAsync(context, method).ConfigureAwait(false);
            return;
        }

        var device = Authenticate(context);
        if (device is null)
        {
            await WriteAsync(context, HttpStatusCode.Unauthorized, new ApiError("Unauthorized"))
                .ConfigureAwait(false);
            return;
        }

        _devices.TouchLastSeen(device.Id);

        switch (route)
        {
            case ["health"] when method == "GET":
                await RequireThen(context, device, DeviceCapabilities.View, HandleHealthAsync)
                    .ConfigureAwait(false);
                return;

            case ["servers"] when method == "GET":
                await RequireThen(context, device, DeviceCapabilities.View, HandleListAsync)
                    .ConfigureAwait(false);
                return;

            case ["servers", var id] when method == "GET":
                await RequireThen(context, device, DeviceCapabilities.View,
                    ctx => HandleDetailAsync(ctx, id)).ConfigureAwait(false);
                return;

            case ["servers", var id, "console"] when method == "GET":
                await RequireThen(context, device, DeviceCapabilities.ReadConsole,
                    ctx => HandleConsoleAsync(ctx, id)).ConfigureAwait(false);
                return;

            case ["servers", var id, "command"] when method == "POST":
                // The sharpest endpoint: arbitrary input to a running game server. It has
                // its own capability, which paired devices do not get by default.
                await RequireThen(context, device, DeviceCapabilities.SendCommands,
                    ctx => HandleCommandAsync(ctx, id, device)).ConfigureAwait(false);
                return;

            case ["servers", var id, var action] when method == "POST":
                await RequireThen(context, device, DeviceCapabilities.Control,
                    ctx => HandleActionAsync(ctx, id, action, device)).ConfigureAwait(false);
                return;

            default:
                await WriteAsync(context, HttpStatusCode.NotFound, new ApiError("No such endpoint."))
                    .ConfigureAwait(false);
                return;
        }
    }

    // --- Browser interface ---

    private static readonly Lazy<byte[]> WebPage = new(() =>
    {
        var assembly = typeof(RemoteApiServer).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("WebUI.index.html", StringComparison.Ordinal));

        if (name is null)
        {
            return Encoding.UTF8.GetBytes(
                "<!doctype html><title>ServerManager</title>"
                + "<p>The browser interface is missing from this build.");
        }

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    });

    /// <summary>
    /// Serves the single-page browser interface.
    /// </summary>
    /// <remarks>
    /// The page itself needs no authentication — it holds no data, and pairing has to be
    /// reachable before a token exists. Everything it displays comes from the API, which
    /// still demands a device token for every request.
    /// </remarks>
    private static async Task ServeWebInterfaceAsync(
        HttpListenerContext context, string path, string method)
    {
        if (method != "GET" && method != "HEAD")
        {
            await WriteAsync(context, HttpStatusCode.MethodNotAllowed,
                new ApiError("Only GET is served here.")).ConfigureAwait(false);
            return;
        }

        // The interface is one page; treat "/" and "/index.html" as it, and anything else
        // as missing rather than trying to resolve it.
        var normalised = path.Trim('/');
        if (normalised.Length != 0
            && !normalised.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var payload = WebPage.Value;

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = payload.Length;

        // The page is rebuilt with each release, so it must not be cached across updates.
        context.Response.Headers["Cache-Control"] = "no-store";

        if (method == "HEAD")
        {
            return;
        }

        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    private PairedDevice? Authenticate(HttpListenerContext context)
    {
        var header = context.Request.Headers["Authorization"] ?? string.Empty;
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim()
            : null;

        return _devices.Authenticate(token);
    }

    private static async Task RequireThen(
        HttpListenerContext context,
        PairedDevice device,
        DeviceCapabilities capability,
        Func<HttpListenerContext, Task> handler)
    {
        if (!device.Can(capability))
        {
            await WriteAsync(context, HttpStatusCode.Forbidden,
                new ApiError($"This device is not permitted to {Describe(capability)}."))
                .ConfigureAwait(false);
            return;
        }

        await handler(context).ConfigureAwait(false);
    }

    private static string Describe(DeviceCapabilities capability) => capability switch
    {
        DeviceCapabilities.View => "view servers",
        DeviceCapabilities.Control => "start or stop servers",
        DeviceCapabilities.ReadConsole => "read console output",
        DeviceCapabilities.SendCommands => "send console commands",
        _ => "perform that action"
    };

    // --- Endpoints ---

    private async Task HandlePairAsync(HttpListenerContext context, string method)
    {
        if (method != "POST")
        {
            await WriteAsync(context, HttpStatusCode.MethodNotAllowed,
                new ApiError("Pairing is a POST.")).ConfigureAwait(false);
            return;
        }

        var request = await ReadAsync<PairRequest>(context).ConfigureAwait(false);

        var result = _pairing.Redeem(
            request?.Code, request?.DeviceName ?? "Android device", DeviceCapabilities.Default);

        if (!result.Success || result.Token is null || result.Device is null)
        {
            await WriteAsync(context, HttpStatusCode.BadRequest,
                new ApiError(result.Error ?? "Pairing failed.")).ConfigureAwait(false);
            return;
        }

        _audit.Record(result.Device.Name, "Paired a new device");

        await WriteAsync(context, HttpStatusCode.OK, new PairResponse(
            result.Token,
            result.Device.Id,
            result.Device.Name,
            CapabilityNames(result.Device.Capabilities))).ConfigureAwait(false);
    }

    private Task HandleListAsync(HttpListenerContext context) =>
        WriteAsync(context, HttpStatusCode.OK, _manager.Instances.Select(Summarise).ToList());

    private async Task HandleDetailAsync(HttpListenerContext context, string id)
    {
        var instance = FindServer(id);
        if (instance is null)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        await WriteAsync(context, HttpStatusCode.OK, Summarise(instance)).ConfigureAwait(false);
    }

    private Task HandleHealthAsync(HttpListenerContext context)
    {
        var health = _manager.AppHealth;
        var uptime = DateTimeOffset.Now - _manager.AppStartedAt;

        return WriteAsync(context, HttpStatusCode.OK, new LauncherHealth(
            health.CpuPercent,
            health.WorkingSetMegabytes,
            health.ManagedMemoryMegabytes,
            health.ThreadCount,
            health.HandleCount,
            FormatDuration(uptime),
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown",
            _manager.Instances.Count(i => i.State == ServerState.Running),
            _manager.Instances.Count));
    }

    private async Task HandleConsoleAsync(HttpListenerContext context, string id)
    {
        var instance = FindServer(id);
        if (instance is null)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        var requested = context.Request.QueryString["tail"];
        var take = int.TryParse(requested, out var parsed) ? Math.Clamp(parsed, 1, 2000) : 200;

        var lines = instance.ConsoleSnapshot()
            .TakeLast(take)
            .Select(l => new ConsoleLineDto(l.Timestamp.ToString("HH:mm:ss"), l.Stream.ToString(), l.Text))
            .ToList();

        await WriteAsync(context, HttpStatusCode.OK, new ConsoleResponse(id, lines)).ConfigureAwait(false);
    }

    private async Task HandleActionAsync(
        HttpListenerContext context, string id, string action, PairedDevice device)
    {
        var instance = FindServer(id);
        if (instance is null)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        switch (action.ToLowerInvariant())
        {
            case "start":
                _audit.Record(device.Name, "Started server", instance.Definition.Name);
                await instance.StartAsync().ConfigureAwait(false);
                break;

            case "stop":
                _audit.Record(device.Name, "Stopped server", instance.Definition.Name);
                await instance.StopAsync().ConfigureAwait(false);
                break;

            case "restart":
                _audit.Record(device.Name, "Restarted server", instance.Definition.Name);
                await instance.RestartAsync().ConfigureAwait(false);
                break;

            default:
                await WriteAsync(context, HttpStatusCode.NotFound,
                    new ApiError($"Unknown action '{action}'.")).ConfigureAwait(false);
                return;
        }

        await WriteAsync(context, HttpStatusCode.OK, new ActionResult(true, $"{action} requested."))
            .ConfigureAwait(false);
    }

    private async Task HandleCommandAsync(HttpListenerContext context, string id, PairedDevice device)
    {
        var instance = FindServer(id);
        if (instance is null)
        {
            await NotFoundAsync(context).ConfigureAwait(false);
            return;
        }

        var request = await ReadAsync<CommandRequest>(context).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(request?.Command))
        {
            await WriteAsync(context, HttpStatusCode.BadRequest, new ApiError("No command given."))
                .ConfigureAwait(false);
            return;
        }

        _audit.Record(device.Name, $"Sent command '{request.Command}'", instance.Definition.Name);
        var sent = instance.SendCommand(request.Command);

        await WriteAsync(context, HttpStatusCode.OK, new ActionResult(
            sent,
            sent ? "Command sent." : "The server is not accepting console input.")).ConfigureAwait(false);
    }

    // --- Plumbing ---

    private static Task NotFoundAsync(HttpListenerContext context) =>
        WriteAsync(context, HttpStatusCode.NotFound, new ApiError("No such server."));

    private static async Task<T?> ReadAsync<T>(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(body)
                ? default
                : JsonSerializer.Deserialize<T>(body, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return default;
        }
    }

    private static async Task WriteAsync<T>(HttpListenerContext context, HttpStatusCode status, T body)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, Json);

        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = payload.Length;

        await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
    }

    private ServerInstance? FindServer(string id) =>
        Guid.TryParse(id, out var parsed) ? _manager.Find(parsed) : null;

    private static ServerSummary Summarise(ServerInstance instance)
    {
        var sample = instance.LastSample;
        var running = instance.State == ServerState.Running;

        return new ServerSummary(
            instance.Id.ToString(),
            instance.Definition.Name,
            instance.State.ToString(),
            running ? sample.CpuPercent : 0,
            running ? sample.WorkingSetMegabytes : 0,
            running ? sample.ProcessCount : 0,
            instance.Uptime is { } uptime ? FormatDuration(uptime) : "—",
            instance.State is ServerState.Stopped or ServerState.Crashed or ServerState.Failed,
            instance.State is ServerState.Running or ServerState.Starting,
            instance.IsLauncherDetached);
    }

    private static string FormatDuration(TimeSpan value) =>
        value.TotalDays >= 1
            ? $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m"
            : $"{value.Hours:00}:{value.Minutes:00}:{value.Seconds:00}";

    internal static IReadOnlyList<string> CapabilityNames(DeviceCapabilities capabilities) =>
        Enum.GetValues<DeviceCapabilities>()
            .Where(c => c != DeviceCapabilities.None
                        && c != DeviceCapabilities.Default
                        && capabilities.HasFlag(c))
            .Select(c => c.ToString())
            .ToList();

    public void Dispose() => Stop();
}
