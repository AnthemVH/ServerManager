using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ServerLauncher.Core.Models;
using ServerLauncher.Core.Supervision;

namespace ServerLauncher.Core.Remote;

/// <summary>
/// Hosts the remote control API and the browser interface.
/// </summary>
/// <remarks>
/// <para>
/// Kestrel, because this has to listen on a public address with TLS. HttpListener runs on
/// http.sys, which refuses any prefix but loopback without a URL reservation created by an
/// administrator and needs a second admin step to attach a certificate to the port;
/// ServerManager runs unelevated so it can replace its own executable when updating.
/// Kestrel binds the socket itself and takes a certificate directly.
/// </para>
/// <para>
/// The cost is that Microsoft.AspNetCore.App becomes a required framework, so the machine
/// needs the ASP.NET Core runtime as well as the Desktop one. The updater checks for it
/// before installing, so an update cannot leave an install unable to start.
/// </para>
/// <para>
/// The API can start, stop and inspect servers that already exist. It has no endpoint that
/// creates or edits one, because this app launches arbitrary scripts: an endpoint that
/// could set a script path would turn a leaked token into remote code execution.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class RemoteApiServer : IAsyncDisposable
{
    private const string DeviceItemKey = "ServerLauncher.Device";

    private readonly ServerManager _manager;
    private readonly DeviceStore _devices;
    private readonly PairingService _pairing;
    private readonly RemoteAuditLog _audit;
    private readonly AccessThrottle _throttle;

    private WebApplication? _app;

    public RemoteApiServer(
        ServerManager manager,
        DeviceStore devices,
        PairingService pairing,
        RemoteAuditLog audit,
        AccessThrottle? throttle = null)
    {
        _manager = manager;
        _devices = devices;
        _pairing = pairing;
        _audit = audit;
        _throttle = throttle ?? new AccessThrottle();
    }

    public bool IsRunning => _app is not null;

    /// <summary>Where the API is listening, for display in Settings.</summary>
    public string? ListeningOn { get; private set; }

    /// <summary>Checks the configuration is coherent before anything is bound.</summary>
    /// <exception cref="InvalidOperationException">It is not safe or possible to listen.</exception>
    public static void Validate(RemoteAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"{settings.Port} is not a usable port.");
        }

        if (!settings.PublishDirectly)
        {
            return;
        }

        // Publishing without TLS would put device tokens on the wire in clear text for
        // anyone on the path. There is no version of that worth offering.
        if (!settings.HasCertificate)
        {
            throw new InvalidOperationException(
                "Publishing directly requires a TLS certificate. Set a certificate "
                + "thumbprint or a .pfx path before turning it on.");
        }
    }

    public async Task StartAsync(RemoteAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        if (_app is not null)
        {
            await StopAsync().ConfigureAwait(false);
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        var address = settings.PublishDirectly ? IPAddress.Any : IPAddress.Loopback;

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, settings.Port, listen =>
            {
                if (settings.PublishDirectly)
                {
                    listen.UseHttps(CertificateResolver.Resolve(settings));
                }
            });

            // Everything here is a small JSON document or one HTML page.
            options.Limits.MaxRequestBodySize = 64 * 1024;

            // Slow-loris style connections should not be able to pile up.
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);

            // Do not advertise what this is to whatever scans the port.
            options.AddServerHeader = false;
        });

        var app = builder.Build();

        app.Use(GateAsync);
        MapEndpoints(app);

        await app.StartAsync().ConfigureAwait(false);

        _app = app;

        var scheme = settings.PublishDirectly ? "https" : "http";
        var host = settings.PublishDirectly ? "0.0.0.0" : "127.0.0.1";
        ListeningOn = $"{scheme}://{host}:{settings.Port}";
    }

    public async Task StopAsync()
    {
        var app = _app;
        _app = null;
        ListeningOn = null;

        if (app is null)
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await app.StopAsync(deadline.Token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    // --- Gate: throttling, then authentication ---

    private async Task GateAsync(HttpContext context, RequestDelegate next)
    {
        var caller = context.Connection.RemoteIpAddress?.ToString();

        if (_throttle.IsBlocked(caller))
        {
            var remaining = _throttle.RemainingBlock(caller);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers["Retry-After"] = ((int)remaining.TotalSeconds).ToString();
            await context.Response
                .WriteAsJsonAsync(new ApiError("Too many failed attempts. Try again later."))
                .ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        // The browser interface itself is public: it holds no data, and pairing has to be
        // reachable before any token exists.
        if (!path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await ServeWebInterfaceAsync(context, path).ConfigureAwait(false);
            return;
        }

        if (path.StartsWith("/api/v1/pair", StringComparison.Ordinal))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal)
            ? header["Bearer ".Length..].Trim()
            : null;

        var device = _devices.Authenticate(token);

        if (device is null)
        {
            _throttle.RecordFailure(caller);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new ApiError("Unauthorized")).ConfigureAwait(false);
            return;
        }

        _throttle.RecordSuccess(caller);
        context.Items[DeviceItemKey] = device;
        _devices.TouchLastSeen(device.Id);

        await next(context).ConfigureAwait(false);
    }

    private static PairedDevice Device(HttpContext context) => (PairedDevice)context.Items[DeviceItemKey]!;

    private static async Task<bool> RequireAsync(HttpContext context, DeviceCapabilities capability)
    {
        if (Device(context).Can(capability))
        {
            return true;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response
            .WriteAsJsonAsync(new ApiError($"This device is not permitted to {Describe(capability)}."))
            .ConfigureAwait(false);

        return false;
    }

    private static string Describe(DeviceCapabilities capability) => capability switch
    {
        DeviceCapabilities.View => "view servers",
        DeviceCapabilities.Control => "start or stop servers",
        DeviceCapabilities.ReadConsole => "read console output",
        DeviceCapabilities.SendCommands => "send console commands",
        _ => "perform that action"
    };

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
    /// Serves the single-page browser interface. Only this one embedded page is ever
    /// returned, so no request path is turned into a file path and there is no traversal
    /// to get wrong.
    /// </summary>
    private static async Task ServeWebInterfaceAsync(HttpContext context, string path)
    {
        if (context.Request.Method is not ("GET" or "HEAD"))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var normalised = path.Trim('/');
        if (normalised.Length != 0
            && !normalised.Equals("index.html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var payload = WebPage.Value;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = payload.Length;

        // Rebuilt with each release, so it must not be cached across updates.
        context.Response.Headers["Cache-Control"] = "no-store";

        // The page loads nothing from anywhere else and is never framed.
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; "
            + "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";

        if (context.Request.Method == "HEAD")
        {
            return;
        }

        await context.Response.Body.WriteAsync(payload).ConfigureAwait(false);
    }

    // --- Routing ---

    private void MapEndpoints(WebApplication app)
    {
        app.MapPost("/api/v1/pair", async (HttpContext context, PairRequest request) =>
        {
            var caller = context.Connection.RemoteIpAddress?.ToString();

            var result = _pairing.Redeem(
                request.Code, request.DeviceName ?? "Device", DeviceCapabilities.Default);

            if (!result.Success || result.Token is null || result.Device is null)
            {
                // A wrong code counts against the caller: pairing is reachable by anyone
                // who can see the port.
                _throttle.RecordFailure(caller);

                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response
                    .WriteAsJsonAsync(new ApiError(result.Error ?? "Pairing failed."))
                    .ConfigureAwait(false);
                return;
            }

            _throttle.RecordSuccess(caller);
            _audit.Record(result.Device.Name, $"Paired a new device from {caller ?? "unknown"}");

            await context.Response.WriteAsJsonAsync(new PairResponse(
                result.Token,
                result.Device.Id,
                result.Device.Name,
                CapabilityNames(result.Device.Capabilities))).ConfigureAwait(false);
        });

        app.MapGet("/api/v1/servers", async context =>
        {
            if (!await RequireAsync(context, DeviceCapabilities.View).ConfigureAwait(false))
            {
                return;
            }

            await context.Response
                .WriteAsJsonAsync(_manager.Instances.Select(Summarise).ToList())
                .ConfigureAwait(false);
        });

        app.MapGet("/api/v1/servers/{id}", async (HttpContext context, string id) =>
        {
            if (!await RequireAsync(context, DeviceCapabilities.View).ConfigureAwait(false))
            {
                return;
            }

            var instance = FindServer(id);
            if (instance is null)
            {
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
            }

            await context.Response.WriteAsJsonAsync(Summarise(instance)).ConfigureAwait(false);
        });

        app.MapGet("/api/v1/health", async context =>
        {
            if (!await RequireAsync(context, DeviceCapabilities.View).ConfigureAwait(false))
            {
                return;
            }

            var health = _manager.AppHealth;
            var uptime = DateTimeOffset.Now - _manager.AppStartedAt;

            await context.Response.WriteAsJsonAsync(new LauncherHealth(
                health.CpuPercent,
                health.WorkingSetMegabytes,
                health.ManagedMemoryMegabytes,
                health.ThreadCount,
                health.HandleCount,
                FormatDuration(uptime),
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown",
                _manager.Instances.Count(i => i.State == ServerState.Running),
                _manager.Instances.Count)).ConfigureAwait(false);
        });

        app.MapGet("/api/v1/servers/{id}/console", async (HttpContext context, string id, int? tail) =>
        {
            if (!await RequireAsync(context, DeviceCapabilities.ReadConsole).ConfigureAwait(false))
            {
                return;
            }

            var instance = FindServer(id);
            if (instance is null)
            {
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
            }

            var take = Math.Clamp(tail ?? 200, 1, 2000);
            var lines = instance.ConsoleSnapshot()
                .TakeLast(take)
                .Select(l => new ConsoleLineDto(
                    l.Timestamp.ToString("HH:mm:ss"), l.Stream.ToString(), l.Text))
                .ToList();

            await context.Response
                .WriteAsJsonAsync(new ConsoleResponse(id, lines))
                .ConfigureAwait(false);
        });

        app.MapPost("/api/v1/servers/{id}/command",
            async (HttpContext context, string id, CommandRequest request) =>
        {
            // The sharpest endpoint: arbitrary input to a running game server. It has its
            // own capability, which paired devices do not get by default.
            if (!await RequireAsync(context, DeviceCapabilities.SendCommands).ConfigureAwait(false))
            {
                return;
            }

            var instance = FindServer(id);
            if (instance is null)
            {
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(request.Command))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response
                    .WriteAsJsonAsync(new ApiError("No command given."))
                    .ConfigureAwait(false);
                return;
            }

            var device = Device(context);
            _audit.Record(device.Name, $"Sent command '{request.Command}'", instance.Definition.Name);

            var sent = instance.SendCommand(request.Command);

            await context.Response.WriteAsJsonAsync(new ActionResult(
                sent,
                sent ? "Command sent." : "The server is not accepting console input."))
                .ConfigureAwait(false);
        });

        app.MapPost("/api/v1/servers/{id}/{action}", async (HttpContext context, string id, string action) =>
        {
            if (!await RequireAsync(context, DeviceCapabilities.Control).ConfigureAwait(false))
            {
                return;
            }

            var instance = FindServer(id);
            if (instance is null)
            {
                await NotFoundAsync(context).ConfigureAwait(false);
                return;
            }

            var device = Device(context);

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
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response
                        .WriteAsJsonAsync(new ApiError($"Unknown action '{action}'."))
                        .ConfigureAwait(false);
                    return;
            }

            await context.Response
                .WriteAsJsonAsync(new ActionResult(true, $"{action} requested."))
                .ConfigureAwait(false);
        });
    }

    private static async Task NotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new ApiError("No such server.")).ConfigureAwait(false);
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

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
