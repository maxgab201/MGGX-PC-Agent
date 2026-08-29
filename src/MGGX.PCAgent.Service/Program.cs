using System.Diagnostics;
using System.Threading.RateLimiting;
using MGGX.PCAgent.Core;
using MGGX.PCAgent.Service;
using Microsoft.AspNetCore.RateLimiting;
using Serilog;

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
    builder.WebHost.UseUrls($"http://0.0.0.0:{config.Port}");
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton<ITokenStore>(_ => new DpapiTokenStore(dataDirectory));
    builder.Services.AddSingleton<IPowerController, WindowsPowerController>();
    builder.Services.AddSingleton<IComponentProbe, WindowsComponentProbe>();
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<StatusProvider>();
    builder.Services.AddSingleton<IStatusProvider>(sp => sp.GetRequiredService<StatusProvider>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<StatusProvider>());
    builder.Services.AddHostedService<DiscoveryService>();
    builder.Services.AddRateLimiter(options => options.AddPolicy("api", http =>
        RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ =>
            new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

    var app = builder.Build();
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

    app.MapGet("/health", () => Results.Ok(new HealthResponse(true, "mggx-pc-agent", 1, AgentConstants.Version, Environment.TickCount64 / 1000)));

    var api = app.MapGroup("/api/v1").RequireRateLimiting("api").AddEndpointFilter(async (invocation, next) =>
    {
        var http = invocation.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
        var expected = http.RequestServices.GetRequiredService<ITokenStore>().GetOrCreate();
        return TokenComparer.IsValid(supplied, expected) ? await next(invocation) : Results.Json(new ErrorResponse(false, "unauthorized"), statusCode: 401);
    });

    api.MapGet("/status", (IStatusProvider status) => Results.Ok(status.Current));
    MapPower(api, "/power/shutdown", "shutting_down", (power, ct) => power.ShutdownAsync(ct));
    MapPower(api, "/power/restart", "restarting", (power, ct) => power.RestartAsync(ct));
    MapPower(api, "/power/sleep", "sleeping", (power, ct) => power.SleepAsync(ct), power => power.SleepSupported, "sleep_not_available");
    MapPower(api, "/power/hibernate", "hibernating", (power, ct) => power.HibernateAsync(ct), power => power.HibernateSupported, "hibernate_not_available");
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

static void MapPower(RouteGroupBuilder api, string path, string state,
    Func<IPowerController, CancellationToken, Task> action,
    Func<IPowerController, bool>? supported = null, string? unavailableError = null)
{
    api.MapPost(path, (IPowerController power, ILoggerFactory loggerFactory) =>
    {
        if (supported is not null && !supported(power)) return Results.Json(new ErrorResponse(false, unavailableError!), statusCode: 409);
        var logger = loggerFactory.CreateLogger("PowerAction");
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(500); await action(power, CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Power action {Action} failed", path); }
        });
        logger.LogWarning("Authorized power action requested: {Action}", path);
        return Results.Accepted(value: new ActionResponse(true, state));
    });
}

public partial class Program { }
