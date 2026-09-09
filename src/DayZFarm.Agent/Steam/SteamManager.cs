using System.Diagnostics;
using System.Runtime.Versioning;
using DayZFarm.Agent.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace DayZFarm.Agent.Steam;

/// <summary>
/// Discovers the local Steam install and manages the Steam client process. Never touches Steam
/// credentials — the account is logged in once, manually, by an administrator during
/// provisioning (see docs/STEAM-SETUP.md); Steam then remembers that login for this VM.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamManager
{
    private readonly ILogger<SteamManager> _logger;
    private string? _cachedSteamExePath;

    public SteamManager(ILogger<SteamManager> logger)
    {
        _logger = logger;
    }

    public string? DiscoverSteamExePath()
    {
        if (_cachedSteamExePath is not null && File.Exists(_cachedSteamExePath))
            return _cachedSteamExePath;

        var candidates = new List<string?>
        {
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamExe", null) as string,
            CombineInstallPath(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string),
            CombineInstallPath(Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string),
            @"C:\Program Files (x86)\Steam\steam.exe"
        };

        _cachedSteamExePath = candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p));
        return _cachedSteamExePath;
    }

    private static string? CombineInstallPath(string? installDir) =>
        string.IsNullOrWhiteSpace(installDir) ? null : Path.Combine(installDir, "steam.exe");

    public bool IsSteamRunning(out int? processId)
    {
        var proc = Process.GetProcessesByName("steam").FirstOrDefault();
        processId = proc?.Id;
        return proc is not null;
    }

    /// <summary>
    /// Best-effort "is a Steam account currently logged in" check based on the ActiveProcess
    /// registry key Steam maintains. Not 100% authoritative — Steam does not publish an official
    /// API for this — hence the pluggable <c>IDayZStatusProvider</c> extension point for future
    /// improvement.
    ///
    /// Deliberately does NOT use <see cref="Registry.CurrentUser"/>: this agent runs as a
    /// Windows Service (LocalSystem, Session 0 — see Program.cs), while Steam runs in the
    /// actual interactive desktop session logged into that VM. Registry.CurrentUser from a
    /// LocalSystem process resolves to LocalSystem's own (unrelated, empty) hive, never the
    /// interactive user's — so the ActiveProcess key was never found and this always reported
    /// "not logged in" even with Steam genuinely logged in and running. Every interactively
    /// logged-on user's hive is mounted under HKEY_USERS\&lt;SID&gt; while their session is
    /// active, regardless of which session this (Session 0) process itself runs in, so scanning
    /// HKEY_USERS finds the right one without needing to know which session/user it is ahead of
    /// time. See docs/TROUBLESHOOTING.md.
    /// </summary>
    public (bool LoggedIn, string? AccountName) GetLoginState()
    {
        try
        {
            foreach (var sid in Registry.Users.GetSubKeyNames())
            {
                using var steamKey = Registry.Users.OpenSubKey($@"{sid}\Software\Valve\Steam");
                if (steamKey is null) continue;

                using var activeProcessKey = steamKey.OpenSubKey("ActiveProcess");
                var activeUser = activeProcessKey?.GetValue("ActiveUser") as int?;
                if (activeUser is not (null or 0))
                {
                    var accountName = steamKey.GetValue("AutoLoginUser") as string;
                    return (true, accountName);
                }
            }
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read Steam login state from registry.");
            return (false, null);
        }
    }

    public void Start()
    {
        var exe = DiscoverSteamExePath();
        if (exe is null)
            throw new InvalidOperationException("Steam installation not found. Install Steam in the master image first.");

        if (IsSteamRunning(out _))
        {
            _logger.LogInformation("Steam is already running.");
            return;
        }

        _logger.LogInformation("Starting Steam from {Path}", exe);
        // Must run in the interactive session, not Session 0 -- see InteractiveProcessLauncher's
        // remarks. A plain Process.Start here would start a second, isolated, logged-out Steam
        // instance in Session 0 instead of the one the interactive user actually uses.
        InteractiveProcessLauncher.StartInInteractiveSession(exe, string.Empty);
    }

    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken ct = default)
    {
        var proc = Process.GetProcessesByName("steam").FirstOrDefault();
        if (proc is null) return;

        _logger.LogInformation("Requesting graceful Steam shutdown (PID {Pid})", proc.Id);
        // Steam supports a documented graceful-shutdown command line switch. Must be issued in
        // the interactive session -- see InteractiveProcessLauncher's remarks.
        var exe = DiscoverSteamExePath();
        if (exe is not null)
        {
            InteractiveProcessLauncher.StartInInteractiveSession(exe, "-shutdown");
        }

        try
        {
            await proc.WaitForExitAsync(new CancellationTokenSource(gracefulTimeout).Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Steam did not exit gracefully within {Timeout}; terminating its own process only.", gracefulTimeout);
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: false);
        }
    }

    /// <summary>Launches DayZ through Steam's own launch mechanism (-applaunch), which is the
    /// only supported way to start a Steam game with DRM/BattlEye intact. Must run in the
    /// interactive session -- see InteractiveProcessLauncher's remarks; the "steam.exe
    /// -applaunch" process itself just hands the request off to the already-running Steam
    /// client and exits, so there's no meaningful child Process to hand back to the caller
    /// (WindowsDayZLauncher polls for the actual DayZ_x64 process separately).</summary>
    public void LaunchAppViaSteam(uint appId, string arguments)
    {
        var exe = DiscoverSteamExePath();
        if (exe is null)
            throw new InvalidOperationException("Steam installation not found.");

        _logger.LogInformation("Launching app {AppId} via Steam with arguments: {Args}", appId, arguments);
        InteractiveProcessLauncher.StartInInteractiveSession(exe, $"-applaunch {appId} {arguments}");
    }
}
