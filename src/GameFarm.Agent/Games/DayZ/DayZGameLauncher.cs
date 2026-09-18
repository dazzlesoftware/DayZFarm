using System.Diagnostics;
using GameFarm.Agent.Steam;
using GameFarm.Core.Interfaces;
using GameFarm.Core.Models;
using Microsoft.Extensions.Logging;

namespace GameFarm.Agent.Games.DayZ;

/// <summary>
/// DayZ's <see cref="IGameLauncher"/> implementation -- the first, reference game module (see
/// docs/ARCHITECTURE.md's "Game modules" section). Launches via Steam's own "-applaunch"
/// mechanism (never a bare .exe path) so Steam DRM and BattlEye stay intact. A future game module
/// implements the same interface for its own game/guest OS without any Controller/agent-protocol
/// changes.
/// </summary>
public sealed class DayZGameLauncher : IGameLauncher
{
    private const string ProcessName = "DayZ_x64";
    private readonly SteamManager _steam;
    private readonly IWorkshopManager _workshop;
    private readonly ILogger<DayZGameLauncher> _logger;
    private int? _lastKnownPid;

    public DayZGameLauncher(SteamManager steam, IWorkshopManager workshop, ILogger<DayZGameLauncher> logger)
    {
        _steam = steam;
        _workshop = workshop;
        _logger = logger;
    }

    public async Task<int> LaunchAsync(GameLaunchOptions options, CancellationToken ct = default)
    {
        if (IsRunning(out var existingPid))
        {
            _logger.LogInformation("DayZ already running (PID {Pid}); not launching a second instance.", existingPid);
            return existingPid!.Value;
        }

        // options.Mods holds bare numeric Workshop IDs (what RequiredMods/the dashboard work
        // with) -- DayZ's own -mod= argument needs an actual folder to load, so each ID is
        // resolved to its installed content folder's absolute path here, right before building
        // the launch command. A bare numeric ID passed straight through (the original behavior)
        // means nothing to DayZ, so the game silently launched with none of its required mods
        // loaded even when RequiredMods was populated and Ensure Mods showed everything
        // Installed. See docs/TROUBLESHOOTING.md.
        var modPaths = await _workshop.ResolveModPathsAsync(options.Mods, ct);
        var resolvedOptions = new GameLaunchOptions
        {
            ServerAddress = options.ServerAddress,
            ServerPort = options.ServerPort,
            ServerPassword = options.ServerPassword,
            Mods = modPaths.ToList(),
            AdditionalArguments = options.AdditionalArguments
        };

        var argString = DayZLaunchArgumentBuilder.BuildArgumentString(resolvedOptions);
        _steam.LaunchAppViaSteam(DayZSteamApp.Id, argString);

        // Steam forks the actual game process; poll briefly for it to appear.
        for (var i = 0; i < 30; i++)
        {
            if (IsRunning(out var pid))
            {
                _lastKnownPid = pid;
                return pid!.Value;
            }
            await Task.Delay(1000, ct);
        }

        throw new TimeoutException("DayZ process did not appear within 30 seconds of launch.");
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        foreach (var proc in Process.GetProcessesByName(ProcessName))
        {
            _logger.LogInformation("Stopping DayZ process PID {Pid}", proc.Id);
            try
            {
                if (!proc.CloseMainWindow())
                    proc.Kill();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop DayZ process PID {Pid} cleanly.", proc.Id);
            }
        }
        _lastKnownPid = null;
        return Task.CompletedTask;
    }

    public bool IsRunning(out int? processId)
    {
        var proc = Process.GetProcessesByName(ProcessName).FirstOrDefault();
        processId = proc?.Id;
        return proc is not null;
    }
}
