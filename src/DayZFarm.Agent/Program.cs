using DayZFarm.Agent.DayZ;
using DayZFarm.Agent.Security;
using DayZFarm.Agent.Steam;
using DayZFarm.Agent.Watchdog;
using DayZFarm.Core;
using DayZFarm.Core.Interfaces;
using DayZFarm.Core.Models;
using DayZFarm.Shared;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options => options.ServiceName = "DayZ Farm Agent");

// Local rolling log (this VM's own record of what the agent did — launches, exits, errors).
// Kept independent of the Controller's per-client log since the two run on different machines.
var localLogDir = @"C:\DayZFarmAgent\Logs";
Directory.CreateDirectory(localLogDir);
builder.Host.UseSerilog((_, loggerConfig) => loggerConfig
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(localLogDir, "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}"));

builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<AgentTokenStore>();
builder.Services.AddSingleton<SteamManager>();
builder.Services.AddSingleton<WindowsDayZLauncher>();
builder.Services.AddSingleton<IDayZLauncher>(sp => sp.GetRequiredService<WindowsDayZLauncher>());
builder.Services.AddSingleton<WindowsWorkshopManager>();
builder.Services.AddSingleton<IWorkshopManager>(sp => sp.GetRequiredService<WindowsWorkshopManager>());
builder.Services.AddSingleton<AgentRuntimeState>();
builder.Services.AddSingleton<IDayZStatusProvider>(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    return new DayZLogStatusProvider(
        opts.ProfileDirectory,
        sp.GetRequiredService<WindowsDayZLauncher>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<DayZLogStatusProvider>());
});
builder.Services.AddHostedService<ReconnectWatchdogService>();

var agentSection = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
builder.WebHost.ConfigureKestrel(k =>
{
    // Bind only to the configured (typically private Hyper-V) interface, never 0.0.0.0 in production.
    k.Listen(System.Net.IPAddress.Parse(agentSection.ListenAddress == "0.0.0.0" ? "0.0.0.0" : agentSection.ListenAddress), agentSection.ListenPort);
});

var app = builder.Build();

// --- Authentication: every route requires the per-agent bearer token except a liveness probe. ---
var tokenStore = app.Services.GetRequiredService<AgentTokenStore>();
var expectedToken = tokenStore.GetOrCreateToken();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        await next();
        return;
    }

    if (!context.Request.Headers.TryGetValue(AgentAuthConstants.TokenHeaderName, out var provided) ||
        provided.ToString() != expectedToken)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Missing or invalid agent token.");
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet(AgentApiRoutes.Status, async (
    AgentRuntimeState state,
    SteamManager steam,
    WindowsDayZLauncher launcher,
    IDayZStatusProvider statusProvider) =>
{
    var (loggedIn, account) = steam.GetLoginState();
    var connection = await statusProvider.DetectConnectionStatusAsync();
    launcher.IsRunning(out var pid);

    return Results.Ok(new AgentStatusResponse
    {
        AgentOnline = true,
        SteamRunning = steam.IsSteamRunning(out _),
        SteamLoggedIn = loggedIn,
        SteamAccount = account,
        DayZInstalled = steam.DiscoverSteamExePath() is not null,
        DayZRunning = pid is not null,
        DayZProcessId = pid,
        DayZVersion = await statusProvider.DetectInstalledVersionAsync(),
        CurrentServer = state.DesiredLaunchOptions is { } o ? $"{o.ServerAddress}:{o.ServerPort}" : null,
        ConnectionStatus = connection,
        LastError = state.LastError,
        Uptime = DateTimeOffset.UtcNow - state.StartedUtc
    });
});

app.MapPost(AgentApiRoutes.SteamStart, (SteamManager steam) =>
{
    steam.Start();
    return Results.Ok(AgentCommandResult.Ok("Steam start requested."));
});

app.MapPost(AgentApiRoutes.SteamStop, async (SteamManager steam) =>
{
    await steam.StopAsync(TimeSpan.FromSeconds(20));
    return Results.Ok(AgentCommandResult.Ok("Steam stopped."));
});

app.MapPost(AgentApiRoutes.SteamRestart, async (SteamManager steam) =>
{
    await steam.StopAsync(TimeSpan.FromSeconds(20));
    steam.Start();
    return Results.Ok(AgentCommandResult.Ok("Steam restarted."));
});

app.MapPost(AgentApiRoutes.DayZStart, async (JoinServerRequest req, WindowsDayZLauncher launcher, AgentRuntimeState state) =>
{
    var options = new DayZLaunchOptions
    {
        ServerAddress = req.ServerAddress,
        ServerPort = req.ServerPort,
        ServerPassword = req.ServerPassword,
        Mods = req.RequiredMods,
        AdditionalArguments = req.AdditionalArguments
    };
    state.DesiredLaunchOptions = options;
    state.LastStopReason = DayZFarm.Core.StopReason.None;
    var pid = await launcher.LaunchAsync(options);
    return Results.Ok(AgentCommandResult.Ok($"DayZ started (PID {pid})."));
});

app.MapPost(AgentApiRoutes.DayZStop, async (WindowsDayZLauncher launcher, AgentRuntimeState state) =>
{
    state.LastStopReason = DayZFarm.Core.StopReason.AdministratorStop;
    await launcher.StopAsync();
    return Results.Ok(AgentCommandResult.Ok("DayZ stopped."));
});

app.MapPost(AgentApiRoutes.DayZRestart, async (WindowsDayZLauncher launcher, AgentRuntimeState state) =>
{
    await launcher.StopAsync();
    if (state.DesiredLaunchOptions is null)
        return Results.BadRequest(AgentCommandResult.Fail("No prior launch options to restart with."));
    state.LastStopReason = DayZFarm.Core.StopReason.None;
    var pid = await launcher.LaunchAsync(state.DesiredLaunchOptions);
    return Results.Ok(AgentCommandResult.Ok($"DayZ restarted (PID {pid})."));
});

app.MapPost(AgentApiRoutes.Join, async (JoinServerRequest req, WindowsDayZLauncher launcher, AgentRuntimeState state) =>
{
    var options = new DayZLaunchOptions
    {
        ServerAddress = req.ServerAddress,
        ServerPort = req.ServerPort,
        ServerPassword = req.ServerPassword,
        Mods = req.RequiredMods,
        AdditionalArguments = req.AdditionalArguments
    };
    state.DesiredLaunchOptions = options;
    state.LastStopReason = DayZFarm.Core.StopReason.None;
    var pid = await launcher.LaunchAsync(options);
    return Results.Ok(AgentCommandResult.Ok($"Joining {req.ServerAddress}:{req.ServerPort} (PID {pid})."));
});

app.MapPost(AgentApiRoutes.Disconnect, async (WindowsDayZLauncher launcher, AgentRuntimeState state) =>
{
    state.LastStopReason = DayZFarm.Core.StopReason.AdministratorStop;
    await launcher.StopAsync();
    return Results.Ok(AgentCommandResult.Ok("Disconnected."));
});

app.MapPost(AgentApiRoutes.Update, async (UpdateDayZRequest req, SteamManager steam, WindowsDayZLauncher launcher) =>
{
    await launcher.StopAsync();
    // Steam updates the game automatically the next time it (or -applaunch) runs; a validate-only
    // pass can be triggered via steam://validate, left as a documented manual/console step for v1.
    steam.Start();
    return Results.Ok(AgentCommandResult.Ok(req.ValidateOnly ? "Validation requested." : "Update requested via Steam."));
});

app.MapGet(AgentApiRoutes.Logs, (Microsoft.Extensions.Options.IOptions<AgentOptions> options) =>
{
    var dir = options.Value.ProfileDirectory;
    var latest = Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*.RPT").Concat(Directory.EnumerateFiles(dir, "*.ADM"))
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
        : null;

    if (latest is null)
        return Results.Ok(new LogsResponse { SourceName = "none", Lines = new List<string> { "No log files found yet." } });

    using var stream = new FileStream(latest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    var lines = new List<string>();
    while (reader.ReadLine() is { } line) lines.Add(line);
    var tail = lines.Count > 300 ? lines.GetRange(lines.Count - 300, 300) : lines;

    return Results.Ok(new LogsResponse { SourceName = Path.GetFileName(latest), Lines = tail });
});

app.MapGet(AgentApiRoutes.Mods, async (IWorkshopManager workshop) =>
{
    var mods = await workshop.GetInstalledModsAsync();
    return Results.Ok(new ModsResponse
    {
        Mods = mods.Select(m => new WorkshopModStatus { WorkshopId = m.WorkshopId, Installed = m.Installed, LastUpdatedUtc = m.LastUpdated }).ToList()
    });
});

app.MapPost(AgentApiRoutes.ModsEnsure, async (EnsureModsRequest req, IWorkshopManager workshop) =>
{
    await workshop.EnsureModsInstalledAsync(req.WorkshopIds);
    return Results.Ok(AgentCommandResult.Ok("Mod check complete; see agent log for any manual subscription steps opened in Steam."));
});

app.MapPost(AgentApiRoutes.Reboot, () =>
{
    System.Diagnostics.Process.Start("shutdown", "/r /t 5 /c \"DayZ Farm Agent requested restart\"");
    return Results.Ok(AgentCommandResult.Ok("Reboot scheduled."));
});

app.MapPost(AgentApiRoutes.Shutdown, () =>
{
    System.Diagnostics.Process.Start("shutdown", "/s /t 5 /c \"DayZ Farm Agent requested shutdown\"");
    return Results.Ok(AgentCommandResult.Ok("Shutdown scheduled."));
});

app.Run();
