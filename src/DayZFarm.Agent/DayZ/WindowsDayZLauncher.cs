using System.Diagnostics;
using DayZFarm.Agent.Steam;
using DayZFarm.Core.Interfaces;
using DayZFarm.Core.Models;
using Microsoft.Extensions.Logging;

namespace DayZFarm.Agent.DayZ;

/// <summary>
/// Windows implementation of <see cref="IDayZLauncher"/>. Launches via Steam's own
/// "-applaunch" mechanism (never a bare .exe path) so Steam DRM and BattlEye stay intact. A
/// future <c>LinuxProtonDayZLauncher</c> can implement the same interface for Proton guests
/// without any Controller/agent-protocol changes.
/// </summary>
public sealed class WindowsDayZLauncher : IDayZLauncher
{
    private const string ProcessName = "DayZ_x64";
    private readonly SteamManager _steam;
    private readonly IWorkshopManager _workshop;
    private readonly ILogger<WindowsDayZLauncher> _logger;
    private int? _lastKnownPid;

    public WindowsDayZLauncher(SteamManager steam, IWorkshopManager workshop, ILogger<WindowsDayZLauncher> logger)
    {
        _steam = steam;
        _workshop = workshop;
        _logger = logger;
    }

    public async Task<int> LaunchAsync(DayZLaunchOptions options, CancellationToken ct = default)
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
        var resolvedOptions = new DayZLaunchOptions
        {
            ServerAddress = options.ServerAddress,
            ServerPort = options.ServerPort,
            ServerPassword = options.ServerPassword,
            Mods = modPaths.ToList(),
            AdditionalArguments = options.AdditionalArguments
        };

        var argString = DayZLaunchArgumentBuilder.BuildArgumentString(resolvedOptions);
        _steam.LaunchAppViaSteam(SteamAppIds.DayZ, argString);

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
