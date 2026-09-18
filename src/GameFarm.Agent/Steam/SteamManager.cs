using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace GameFarm.Agent.Steam;

/// <summary>
/// Discovers the local Steam install and manages the Steam client process. Never touches Steam
/// credentials — the account is logged in once, manually, by an administrator during
/// provisioning (see docs/STEAM-SETUP.md); Steam then remembers that login for this VM.
///
/// Launches Steam/DayZ via plain <see cref="Process.Start(ProcessStartInfo)"/> deliberately, NOT
/// <c>InteractiveProcessLauncher</c> -- this agent now runs as a Scheduled Task in the VM's own
/// interactive logon session (see Program.cs and scripts/Install-Agent.ps1), not a Session-0
/// LocalSystem service, so no cross-session/token-duplication trick is needed at all. An earlier
/// version of this agent ran as a LocalSystem service and used
/// <c>InteractiveProcessLauncher</c>'s CreateProcessAsUser-based technique to reach the
/// interactive session; that worked (Steam/DayZ genuinely launched and connected), but BattlEye
/// consistently kicked ("Bad Packet" / "Game restart required") every session launched that way
/// and never a manually-launched one. BattlEye's anti-tamper checks are known to distrust a game
/// process descending from a privileged service using a duplicated security token -- the same
/// pattern real cheat-injection tooling uses -- so the fix was to stop needing that pattern at
/// all, not to try to hide it. See docs/TROUBLESHOOTING.md.
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

    /// <summary>
    /// Every registered Steam Library Folder (the folder alongside steam.exe itself, plus every
    /// one listed in steamapps/libraryfolders.vdf). DayZ, and its Workshop content, are commonly
    /// installed on a different library folder than steam.exe itself (this project's own
    /// SteamLibrary-Master.vhdx design, often a different drive letter) -- see
    /// docs/MASTER-IMAGE.md and docs/TROUBLESHOOTING.md. Shared by <see cref="WindowsWorkshopManager"/>.
    /// </summary>
    internal IReadOnlyList<string> LibraryRoots()
    {
        var steamExe = DiscoverSteamExePath();
        if (steamExe is null) return Array.Empty<string>();
        var steamRoot = Path.GetDirectoryName(steamExe);
        if (steamRoot is null) return Array.Empty<string>();

        var roots = new List<string> { steamRoot };
        roots.AddRange(ParseLibraryFolderRoots(steamRoot));
        return roots;
    }

    private static IEnumerable<string> ParseLibraryFolderRoots(string steamRoot)
    {
        var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) yield break;

        // libraryfolders.vdf is Valve's simple key-value format; each library folder entry has
        // a "path" line with the folder's root (backslash-escaped). A tiny regex scan is enough
        // -- no need for a full VDF parser for this one field.
        foreach (Match m in Regex.Matches(File.ReadAllText(vdfPath), "\"path\"\\s*\"(.*?)\""))
            yield return m.Groups[1].Value.Replace(@"\\", @"\");
    }

    /// <summary>DayZ's own install directory (whichever library folder it happens to be
    /// installed to), or null if it can't be found.</summary>
    public string? FindDayZInstallDirectory() =>
        LibraryRoots()
            .Select(root => Path.Combine(root, "steamapps", "common", "DayZ"))
            .FirstOrDefault(Directory.Exists);

    /// <summary>
    /// DayZ ships its own BattlEye-aware host executable, DayZ_BE.exe, alongside DayZ_x64.exe --
    /// distinct from it, and the one that actually establishes BattlEye's hooks correctly before
    /// the real game process starts (confirmed via DayZ Launcher's own log, which resolves and
    /// logs this exact path before ever starting the game). See docs/TROUBLESHOOTING.md.
    /// </summary>
    public string? DiscoverDayZBattlEyeExePath()
    {
        var installDir = FindDayZInstallDirectory();
        if (installDir is null) return null;
        var path = Path.Combine(installDir, "DayZ_BE.exe");
        return File.Exists(path) ? path : null;
    }

    public bool IsSteamRunning(out int? processId)
    {
        var proc = Process.GetProcessesByName("steam").FirstOrDefault();
        processId = proc?.Id;
        return proc is not null;
    }

    /// <summary>
    /// Best-effort "is a Steam account currently logged in" check based on the ActiveProcess
    /// registry key Steam maintains. Not 100% authoritative — Steam does not publish an official
    /// API for this — hence the pluggable <c>IGameStatusProvider</c> extension point for future
    /// improvement.
    ///
    /// Deliberately does NOT use <see cref="Registry.CurrentUser"/>: at the time this was
    /// written the agent ran as a Windows Service (LocalSystem, Session 0), while Steam ran in
    /// the actual interactive desktop session logged into that VM -- Registry.CurrentUser from
    /// a LocalSystem process resolves to LocalSystem's own (unrelated, empty) hive, never the
    /// interactive user's, so the ActiveProcess key was never found there. The agent now runs
    /// interactively too (see Program.cs and scripts/Install-Agent.ps1), but this scan remains
    /// correct and needs no changes either way: every interactively logged-on user's hive is
    /// mounted under HKEY_USERS\&lt;SID&gt; while their session is active, regardless of which
    /// session the *reading* process itself runs in. See docs/TROUBLESHOOTING.md.
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
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken ct = default)
    {
        var proc = Process.GetProcessesByName("steam").FirstOrDefault();
        if (proc is null) return;

        _logger.LogInformation("Requesting graceful Steam shutdown (PID {Pid})", proc.Id);
        // Steam supports a documented graceful-shutdown command line switch.
        var exe = DiscoverSteamExePath();
        if (exe is not null)
        {
            Process.Start(new ProcessStartInfo(exe, "-shutdown") { UseShellExecute = true });
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

    /// <summary>
    /// Launches DayZ. Prefers running <c>DayZ_BE.exe</c> directly -- DayZ's own BattlEye-aware
    /// host executable, distinct from <c>DayZ_x64.exe</c> -- over Steam's
    /// <c>-applaunch ... -nolauncher</c> mechanism used previously.
    ///
    /// <c>-nolauncher</c> bypasses DayZ's own graphical Launcher application entirely, and that
    /// Launcher is what actually starts the game via <c>DayZ_BE.exe</c> when you click Play --
    /// skip it, and Steam falls through to starting <c>DayZ_x64.exe</c> directly instead. The
    /// game still connects and plays completely normally either way (BattlEye isn't required
    /// just to connect), but a directly-started <c>DayZ_x64.exe</c> process never gets
    /// BattlEye's hooks correctly established the way <c>DayZ_BE.exe</c> establishes them --
    /// and BattlEye's own periodic in-session validation eventually notices and kicks it as
    /// anomalous ("Bad Packet"), something that never happened for a manually Launcher-routed
    /// session. Confirmed by comparing a manual session's actual RPT-logged command line
    /// (routed through the Launcher/DayZ_BE.exe) against an agent-launched one (direct
    /// DayZ_x64.exe via -applaunch) -- see docs/TROUBLESHOOTING.md.
    ///
    /// Sets the SteamAppId/SteamGameId environment variables the same way DayZLauncher.exe
    /// itself does (visible in its own log) before starting <c>DayZ_BE.exe</c> directly, so
    /// Steamworks still initializes correctly for a process not started via Steam's own
    /// <c>-applaunch</c> -- this only requires the Steam client to already be running, which
    /// <see cref="Start"/> already guarantees before this is ever called.
    /// </summary>
    public void LaunchAppViaSteam(uint appId, string arguments)
    {
        var beExe = DiscoverDayZBattlEyeExePath();
        if (beExe is not null)
        {
            _logger.LogInformation("Launching DayZ via its BattlEye host executable ({Path}) with arguments: {Args}", beExe, arguments);
            var psi = new ProcessStartInfo(beExe, arguments)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(beExe)!
            };
            psi.EnvironmentVariables["SteamAppId"] = appId.ToString();
            psi.EnvironmentVariables["SteamGameId"] = appId.ToString();
            Process.Start(psi);
            return;
        }

        // Fallback: DayZ_BE.exe not found (unexpected install layout) -- fall back to Steam's
        // own -applaunch mechanism rather than failing outright, though this reintroduces the
        // BattlEye risk described above. Should not happen in a normal DayZ install.
        _logger.LogWarning(
            "DayZ_BE.exe not found under DayZ's install directory; falling back to 'steam.exe -applaunch'. " +
            "This may not properly attach BattlEye -- see docs/TROUBLESHOOTING.md.");
        var exe = DiscoverSteamExePath();
        if (exe is null)
            throw new InvalidOperationException("Steam installation not found.");

        _logger.LogInformation("Launching app {AppId} via Steam with arguments: {Args}", appId, arguments);
        Process.Start(new ProcessStartInfo(exe, $"-applaunch {appId} {arguments}") { UseShellExecute = true });
    }
}
