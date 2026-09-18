using GameFarm.Agent.Games.DayZ;
using GameFarm.Agent.Security;
using GameFarm.Agent.Steam;
using GameFarm.Agent.Update;
using GameFarm.Agent.Watchdog;
using GameFarm.Core;
using GameFarm.Core.Interfaces;
using GameFarm.Core.Models;
using GameFarm.Core.Plugins;
using GameFarm.Shared;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// UseWindowsService() only engages the Windows Service lifecycle when this process is actually
// started by the Service Control Manager -- it's a safe no-op otherwise, so it's left in place
// even though the RECOMMENDED way to run this agent is no longer as a LocalSystem Windows
// Service. It's now installed as a Scheduled Task that runs directly in the VM's interactive
// logon session instead (see scripts/Install-Agent.ps1): BattlEye was found to consistently
// kick a session launched via the old (Session-0 + CreateProcessAsUser) approach, since its
// anti-tamper checks distrust a game process descending from a privileged service using a
// duplicated security token -- the same pattern real cheat-injection tooling uses. Running the
// agent itself as a normal interactive process avoids that pattern entirely: Steam/DayZ launch
// via a plain Process.Start (see SteamManager), indistinguishable from a human launching them.
// See docs/TROUBLESHOOTING.md.
builder.Host.UseWindowsService(options => options.ServiceName = "Game Farm Agent");

// Local rolling log (this VM's own record of what the agent did — launches, exits, errors).
// Kept independent of the Controller's per-client log since the two run on different machines.
var localLogDir = @"C:\GameFarmAgent\Logs";
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

// Exactly one game module is registered, picked by AgentOptions.GameId -- mirrors how the
// Controller picks exactly one IVirtualMachineProvider by GameFarmOptions.Hypervisor. Adding a
// new game means writing its own IGameLauncher/IGameStatusProvider/IGameProfile implementation
// (see docs/ARCHITECTURE.md's "Game modules" section) and adding a case here; nothing above
// these interfaces (route handlers, ReconnectWatchdogService, the Controller) needs to change.
var gameId = (builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions()).GameId;
switch (gameId.ToLowerInvariant())
{
    case "dayz":
        builder.Services.AddSingleton<DayZGameLauncher>();
        builder.Services.AddSingleton<IGameLauncher>(sp => sp.GetRequiredService<DayZGameLauncher>());
        builder.Services.AddSingleton<IGameProfile, DayZGameProfile>();
        builder.Services.AddSingleton<IGameStatusProvider>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
            return new DayZGameStatusProvider(
                opts.ProfileDirectory,
                sp.GetRequiredService<DayZGameLauncher>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<DayZGameStatusProvider>());
        });
        break;
    default:
        throw new NotSupportedException(
            $"Unknown Agent:GameId '{gameId}'. Only 'dayz' is implemented -- see docs/ARCHITECTURE.md's " +
            "\"Game modules\" section for how to add another.");
}

builder.Services.AddSingleton<WindowsWorkshopManager>();
builder.Services.AddSingleton<IWorkshopManager>(sp => sp.GetRequiredService<WindowsWorkshopManager>());
builder.Services.AddSingleton<AgentRuntimeState>();
builder.Services.AddHostedService<ReconnectWatchdogService>();
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AgentOptions>>().Value;
    return new PluginService(opts.PluginsDirectory, sp.GetRequiredService<ILoggerFactory>().CreateLogger<PluginService>());
});
builder.Services.AddSingleton<IAgentSelfUpdater, WindowsAgentSelfUpdater>();

var agentSection = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
builder.WebHost.ConfigureKestrel(k =>
{
    // Bind only to the configured (typically private Hyper-V) interface, never 0.0.0.0 in production.
    k.Listen(System.Net.IPAddress.Parse(agentSection.ListenAddress == "0.0.0.0" ? "0.0.0.0" : agentSection.ListenAddress), agentSection.ListenPort);
    // Default (~28.6MB) is comfortably too small for an Agent update package upload (see
    // docs/AGENT-UPDATES.md) -- a framework-dependent publish output is usually well under this,
    // but leave real headroom rather than have this silently reject a slightly larger build.
    k.Limits.MaxRequestBodySize = 300 * 1024 * 1024;
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
    IGameLauncher launcher,
    IGameStatusProvider statusProvider) =>
{
    var (loggedIn, account) = steam.GetLoginState();
    var connection = await statusProvider.DetectConnectionStatusAsync();
    launcher.IsRunning(out var pid);

    // Written by the detached updater script after a successful self-update (see
    // docs/AGENT-UPDATES.md / WindowsAgentSelfUpdater) -- absent on an agent that has never been
    // updated that way (e.g. the original master-image install), not an error.
    var versionFile = Path.Combine(AppContext.BaseDirectory, "agent-version.txt");
    var agentVersion = File.Exists(versionFile) ? File.ReadAllText(versionFile).Trim() : null;

    return Results.Ok(new AgentStatusResponse
    {
        AgentOnline = true,
        SteamRunning = steam.IsSteamRunning(out _),
        SteamLoggedIn = loggedIn,
        SteamAccount = account,
        GameInstalled = steam.DiscoverSteamExePath() is not null,
        GameRunning = pid is not null,
        GameProcessId = pid,
        GameVersion = await statusProvider.DetectInstalledVersionAsync(),
        CurrentServer = state.DesiredLaunchOptions is { } o ? $"{o.ServerAddress}:{o.ServerPort}" : null,
        ConnectionStatus = connection,
        LastError = state.LastError,
        Uptime = DateTimeOffset.UtcNow - state.StartedUtc,
        AgentVersion = string.IsNullOrWhiteSpace(agentVersion) ? null : agentVersion
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

app.MapPost(AgentApiRoutes.GameStart, async (JoinServerRequest req, IGameLauncher launcher, AgentRuntimeState state) =>
{
    var options = new GameLaunchOptions
    {
        ServerAddress = req.ServerAddress,
        ServerPort = req.ServerPort,
        ServerPassword = req.ServerPassword,
        Mods = req.RequiredMods,
        AdditionalArguments = req.AdditionalArguments
    };
    state.DesiredLaunchOptions = options;
    state.LastStopReason = GameFarm.Core.StopReason.None;
    var pid = await launcher.LaunchAsync(options);
    return Results.Ok(AgentCommandResult.Ok($"Game started (PID {pid})."));
});

app.MapPost(AgentApiRoutes.GameStop, async (IGameLauncher launcher, AgentRuntimeState state) =>
{
    state.LastStopReason = GameFarm.Core.StopReason.AdministratorStop;
    await launcher.StopAsync();
    return Results.Ok(AgentCommandResult.Ok("Game stopped."));
});

app.MapPost(AgentApiRoutes.GameRestart, async (IGameLauncher launcher, AgentRuntimeState state) =>
{
    await launcher.StopAsync();
    if (state.DesiredLaunchOptions is null)
        return Results.BadRequest(AgentCommandResult.Fail("No prior launch options to restart with."));
    state.LastStopReason = GameFarm.Core.StopReason.None;
    var pid = await launcher.LaunchAsync(state.DesiredLaunchOptions);
    return Results.Ok(AgentCommandResult.Ok($"Game restarted (PID {pid})."));
});

app.MapPost(AgentApiRoutes.Join, async (JoinServerRequest req, IGameLauncher launcher, AgentRuntimeState state) =>
{
    var options = new GameLaunchOptions
    {
        ServerAddress = req.ServerAddress,
        ServerPort = req.ServerPort,
        ServerPassword = req.ServerPassword,
        Mods = req.RequiredMods,
        AdditionalArguments = req.AdditionalArguments
    };
    state.DesiredLaunchOptions = options;
    state.LastStopReason = GameFarm.Core.StopReason.None;
    var pid = await launcher.LaunchAsync(options);
    return Results.Ok(AgentCommandResult.Ok($"Joining {req.ServerAddress}:{req.ServerPort} (PID {pid})."));
});

app.MapPost(AgentApiRoutes.Disconnect, async (IGameLauncher launcher, AgentRuntimeState state) =>
{
    state.LastStopReason = GameFarm.Core.StopReason.AdministratorStop;
    await launcher.StopAsync();
    return Results.Ok(AgentCommandResult.Ok("Disconnected."));
});

app.MapPost(AgentApiRoutes.Update, async (UpdateGameRequest req, SteamManager steam, IGameLauncher launcher) =>
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

// --- Guest-side plugins (see docs/PLUGINS.md): the Controller can only ever trigger one of
// these BY NAME (registered by whoever placed a JSON file under Agent:PluginsDirectory inside
// this VM) -- there is no route anywhere that accepts free-form command text. ---
app.MapGet(AgentApiRoutes.Plugins, (PluginService plugins) => Results.Ok(plugins.ListPlugins()));

app.MapPost("/api/agent/plugins/{name}/run", async (string name, PluginService plugins) =>
{
    try
    {
        return Results.Ok(await plugins.RunAsync(name));
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(AgentCommandResult.Fail(ex.Message));
    }
});

// --- Agent binary self-update (see docs/AGENT-UPDATES.md): the Controller pushes a zip built
// via `dotnet publish src/GameFarm.Agent`; this VM stages it and hands off to a detached script
// that kills this process, copies the new files in, and restarts the Scheduled Task. ---
app.MapPost(AgentApiRoutes.UpdatePackage, async (HttpRequest request, IAgentSelfUpdater updater, ILogger<Program> logger) =>
{
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);
    var zipBytes = body.ToArray();
    if (zipBytes.Length == 0)
        return Results.BadRequest(AgentCommandResult.Fail("Empty update package."));

    try
    {
        var version = updater.ApplyUpdate(zipBytes);
        return Results.Ok(AgentCommandResult.Ok($"Update staged (version {version[..12]}...); agent will restart shortly."));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to apply agent update package.");
        return Results.BadRequest(AgentCommandResult.Fail($"Failed to apply update: {ex.Message}"));
    }
});

app.MapPost(AgentApiRoutes.Reboot, () =>
{
    System.Diagnostics.Process.Start("shutdown", "/r /t 5 /c \"Game Farm Agent requested restart\"");
    return Results.Ok(AgentCommandResult.Ok("Reboot scheduled."));
});

app.MapPost(AgentApiRoutes.Shutdown, () =>
{
    System.Diagnostics.Process.Start("shutdown", "/s /t 5 /c \"Game Farm Agent requested shutdown\"");
    return Results.Ok(AgentCommandResult.Ok("Shutdown scheduled."));
});

app.Run();
