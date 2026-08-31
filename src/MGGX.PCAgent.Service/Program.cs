using System.Diagnostics;
using System.Net;
using System.Threading.RateLimiting;
using MGGX.PCAgent.Core;
using MGGX.PCAgent.Service;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

var processClock = Stopwatch.StartNew();
var dataDirectory = AgentConstants.DataDirectory;
var bootstrapWarnings = new List<string>();
var config = AgentConfigLoader.Load(dataDirectory, bootstrapWarnings.Add);
Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.File(Path.Combine(dataDirectory, "logs", "agent-.log"), rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: config.LogRetentionDays, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseWindowsService(options => options.ServiceName = AgentConstants.DisplayName).UseSerilog();
    if (!builder.Environment.IsEnvironment("Testing"))
        builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<ITokenStore>(_ => new DpapiTokenStore(dataDirectory));
    builder.Services.AddSingleton<IPairedCredentialStore>(_ => new DpapiPairedCredentialStore(dataDirectory));
    builder.Services.AddSingleton<IPowerController, WindowsPowerController>();
    builder.Services.AddSingleton<INetworkInfoProvider, WindowsNetworkProbe>();
    builder.Services.AddSingleton<IComponentProbe, WindowsComponentProbe>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<StatusProvider>();
    builder.Services.AddSingleton<IStatusProvider>(sp => sp.GetRequiredService<StatusProvider>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusProvider>());
    builder.Services.AddHostedService<DiscoveryService>();
    builder.Services.AddSingleton<IPairingSession, PairingSession>();
    builder.Services.AddHostedService<PairingPipeServer>();
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy("api", http => FixedWindow(http, 30, TimeSpan.FromMinutes(1)));
        options.AddPolicy("pairing", http => FixedWindow(http, 10, TimeSpan.FromMinutes(1)));
    });

    var app = builder.Build();
    _ = app.Services.GetRequiredService<ITokenStore>().GetOrCreate();
    foreach (var warning in bootstrapWarnings) app.Logger.LogWarning("{Warning}", warning);

    app.Use(async (context, next) =>
    {
        var sw = Stopwatch.StartNew();
        try { await next(); }
        finally
        {
            app.Logger.LogInformation("HTTP {Method} {Path} -> {Status} from {RemoteIp} in {Duration}ms",
                context.Request.Method, context.Request.Path, context.Response.StatusCode,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", sw.ElapsedMilliseconds);
        }
    });
    app.UseRateLimiter();

    app.MapGet("/health", () => Results.Ok(new HealthResponse(true, "mggx-pc-agent", 1, AgentConstants.Version, (long)processClock.Elapsed.TotalSeconds)));

    app.MapPost("/api/v1/pair/claim", (HttpContext http, PairClaimRequest? body, IPairingSession pairing, IPairedCredentialStore credentials,
        INetworkInfoProvider network, AgentConfig cfg, IHostEnvironment environment, ILoggerFactory loggerFactory) =>
    {
        var logger = loggerFactory.CreateLogger("Pairing");
        var remoteIp = http.Connection.RemoteIpAddress ?? (environment.IsEnvironment("Testing") ? IPAddress.Loopback : null);
        if (!NetworkSelector.IsAllowedClaimOrigin(remoteIp))
        {
            logger.LogWarning("pairing_rejected: origin not allowed");
            return Results.Json(new ErrorResponse(false, "forbidden_network"), statusCode: 403);
        }
        if (body is null || body.ProtocolVersion != PairingConstants.ProtocolVersion ||
            string.IsNullOrWhiteSpace(body.Secret) || body.Client != PairingConstants.ExpectedClient)
        {
            logger.LogWarning("pairing_rejected: bad request");
            return Results.Json(new ErrorResponse(false, "bad_request"), statusCode: 400);
        }
        if (!pairing.TryClaim(body.Secret, out var error))
        {
            logger.LogInformation("pairing_rejected: {Error}", error);
            return Results.Json(new ErrorResponse(false, "unauthorized"), statusCode: 401);
        }

        var snapshot = network.GetSnapshot(cfg.LanAdapterId);
        if (snapshot.LanIp is null || snapshot.MacAddress is null || snapshot.BroadcastAddress is null)
        {
            logger.LogError("pairing_claim_failed: no usable LAN adapter");
            return Results.Json(new ErrorResponse(false, "network_unavailable"), statusCode: 500);
        }

        var (_, token) = credentials.Issue(body.Client, "Celular de casa");
        logger.LogInformation("pairing_claim_success");
        return Results.Ok(new PairClaimResponse(true, PairingConstants.ProtocolVersion, token, cfg.Port, AgentConstants.Version,
            cfg.PcId, cfg.PcName, snapshot.LanIp, snapshot.TailscaleIp, snapshot.MacAddress, snapshot.BroadcastAddress));
    }).RequireRateLimiting("pairing");

    var api = app.MapGroup("/api/v1").RequireRateLimiting("api").AddEndpointFilter(async (invocation, next) =>
    {
        var http = invocation.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        var legacyToken = http.RequestServices.GetRequiredService<ITokenStore>().GetOrCreate();
        var pairedCredentials = http.RequestServices.GetRequiredService<IPairedCredentialStore>();
        var authorized = TokenComparer.IsValid(supplied, legacyToken) || pairedCredentials.TryValidate(supplied, out _);
        return authorized ? await next(invocation) : Results.Json(new ErrorResponse(false, "unauthorized"), statusCode: 401);
    });

    api.MapGet("/status", (IStatusProvider status) => Results.Ok(status.Current));
    MapPower(api, "/power/shutdown", "shutting_down", (power, ct) => power.ShutdownAsync(ct));
    MapPower(api, "/power/restart", "restarting", (power, ct) => power.RestartAsync(ct));
    MapPower(api, "/power/sleep", "sleeping", (power, ct) => power.SleepAsync(ct), power => power.SleepSupported, "sleep_not_available", awaitable: false);
    MapPower(api, "/power/hibernate", "hibernating", (power, ct) => power.HibernateAsync(ct), power => power.HibernateSupported, "hibernate_not_available", awaitable: false);
    MapPower(api, "/power/lock", "locking", (power, ct) => power.LockAsync(ct));

    api.MapPost("/services/sunshine/restart", async (IComponentProbe probe, CancellationToken ct) =>
    {
        if (!await probe.RestartSunshineAsync(ct)) return Results.Json(new ErrorResponse(false, "sunshine_not_installed"), statusCode: 409);
        return Results.Accepted(value: new ActionResponse(true, "restarting"));
    });

    app.Logger.LogInformation("MGGX PC Agent {Version} starting on port {Port}", AgentConstants.Version, config.Port);
    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "MGGX PC Agent terminated unexpectedly");
    throw;
}
finally { await Log.CloseAndFlushAsync(); }

static RateLimitPartition<string> FixedWindow(HttpContext http, int permitLimit, TimeSpan window)
{
    var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
    var testId = environment.IsEnvironment("Testing") ? http.Request.Headers["X-Test-Client-Id"].ToString() : string.Empty;
    var partition = string.IsNullOrEmpty(testId) ? http.Connection.RemoteIpAddress?.ToString() ?? "unknown" : $"test:{testId}";
    return RateLimitPartition.GetFixedWindowLimiter(partition, _ =>
        new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = window, QueueLimit = 0 });
}

/// <summary>
/// Shutdown/restart/lock are confirmed synchronously (they only *schedule* the OS transition, which
/// happens almost instantly) so a real failure is reported instead of a fabricated success. Sleep and
/// hibernate cannot be awaited to completion — Windows only returns once the machine wakes back up — so
/// the call is raced against a short grace window: a rejection inside that window (policy disallows it,
/// hardware doesn't support it) is reported as a real error; otherwise the transition is genuinely under way.
/// </summary>
static void MapPower(RouteGroupBuilder api, string path, string state,
    Func<IPowerController, CancellationToken, Task> action,
    Func<IPowerController, bool>? supported = null, string? unavailableError = null, bool awaitable = true)
{
    api.MapPost(path, async (IPowerController power, ILoggerFactory loggerFactory) =>
    {
        if (supported is not null && !supported(power)) return Results.Json(new ErrorResponse(false, unavailableError!), statusCode: 409);
        var logger = loggerFactory.CreateLogger("PowerAction");
        logger.LogWarning("Authorized power action requested: {Action}", path);
        try
        {
            if (awaitable)
            {
                await action(power, CancellationToken.None);
                return Results.Ok(new ActionResponse(true, state));
            }

            var task = action(power, CancellationToken.None);
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
            if (completed == task)
            {
                await task; // observe a synchronous rejection instead of masking it
                return Results.Ok(new ActionResponse(true, state));
            }
            _ = task.ContinueWith(t => logger.LogError(t.Exception, "Power action {Action} failed after acceptance", path),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
            return Results.Accepted(value: new ActionResponse(true, state));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Power action {Action} failed", path);
            return Results.Json(new ErrorResponse(false, "power_action_failed"), statusCode: 500);
        }
    });
}

public partial class Program { }
