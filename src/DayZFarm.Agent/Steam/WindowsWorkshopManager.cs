using System.Diagnostics;
using DayZFarm.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace DayZFarm.Agent.Steam;

/// <summary>
/// Reports installed DayZ Workshop content by scanning Steam's own download directory
/// (steamapps/workshop/content/221100/&lt;id&gt;) — no private Steam API involved. Subscribing to a
/// new mod cannot be done safely without either the Steamworks API (requires a running game
/// session and an App ID-scoped API key) or storing Steam credentials for an unattended
/// steamcmd login, both out of scope for v1. <see cref="EnsureModsInstalledAsync"/> instead opens
/// the mod's Workshop page in Steam so an administrator can click Subscribe once per VM — the
/// one remaining manual action, analogous to the Steam Guard login step.
/// </summary>
public sealed class WindowsWorkshopManager : IWorkshopManager
{
    private const uint DayZAppId = DayZFarm.Core.Models.SteamAppIds.DayZ;
    private readonly SteamManager _steam;
    private readonly ILogger<WindowsWorkshopManager> _logger;

    public WindowsWorkshopManager(SteamManager steam, ILogger<WindowsWorkshopManager> logger)
    {
        _steam = steam;
        _logger = logger;
    }

    /// <summary>
    /// Every directory that could hold DayZ's Workshop content across every registered Steam
    /// Library Folder (via <see cref="SteamManager.LibraryRoots"/> -- shared there since
    /// discovering install/library locations isn't workshop-specific). Workshop content for an
    /// app lives under whichever library folder *that app itself* is installed to -- not
    /// necessarily alongside steam.exe. This project's own design routinely puts DayZ on a
    /// separate library folder entirely (the SteamLibrary-Master.vhdx disk, often a different
    /// drive letter — see docs/MASTER-IMAGE.md), so assuming co-location with steam.exe silently
    /// scanned an empty/wrong folder and reported every mod as "Missing" even when genuinely
    /// installed. See docs/TROUBLESHOOTING.md.
    /// </summary>
    private IReadOnlyList<string> WorkshopContentDirectories() =>
        _steam.LibraryRoots()
            .Select(root => Path.Combine(root, "steamapps", "workshop", "content", DayZAppId.ToString()))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Every installed mod's content folder, keyed by Workshop ID (the folder name).
    /// Shared by <see cref="GetInstalledModsAsync"/> and <see cref="ResolveModPathsAsync"/> so
    /// both agree on what "installed" means from a single directory scan.</summary>
    private Dictionary<string, string> FindInstalledModFolders() =>
        WorkshopContentDirectories()
            .SelectMany(Directory.EnumerateDirectories)
            .GroupBy(Path.GetFileName)
            .ToDictionary(g => g.Key!, g => g.First(), StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<WorkshopMod>> GetInstalledModsAsync(CancellationToken ct = default)
    {
        var mods = FindInstalledModFolders()
            .Select(kv => new WorkshopMod
            {
                WorkshopId = kv.Key,
                Installed = true,
                LastUpdated = Directory.GetLastWriteTimeUtc(kv.Value)
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<WorkshopMod>>(mods);
    }

    public Task<IReadOnlyList<string>> ResolveModPathsAsync(IReadOnlyList<string> workshopIds, CancellationToken ct = default)
    {
        var installed = FindInstalledModFolders();
        var dayzInstallDir = _steam.FindDayZInstallDirectory();
        var tokens = new List<string>();

        foreach (var id in workshopIds)
        {
            if (!installed.TryGetValue(id, out var contentFolder))
            {
                _logger.LogWarning(
                    "Required mod {Id} has no installed Workshop content folder -- omitting it from the DayZ " +
                    "launch. Run Ensure Mods first, or confirm it's actually installed for this VM.", id);
                continue;
            }

            // Prefer referencing the mod via a "!Workshop\<id>" junction inside DayZ's own
            // install directory -- the same convention Steam's own launcher uses -- rather than
            // the mod's raw absolute steamapps/workshop/content path. DayZ connects fine either
            // way, but BattlEye's periodic in-session integrity re-check was observed to kick
            // ("Game restart required", 1-5 minutes in) when mods live outside the game's own
            // install tree. Steam only creates that junction itself via its own "SETUP DLCS AND
            // MODS AND JOIN" UI flow, not for a direct -applaunch, so it's created here instead,
            // idempotently, the first time each mod is needed. Falls back to the raw absolute
            // path if DayZ's install directory can't be found or junction creation fails for any
            // reason, so a launch never breaks outright over this. See docs/TROUBLESHOOTING.md.
            if (dayzInstallDir is not null)
            {
                try
                {
                    EnsureWorkshopJunction(dayzInstallDir, id, contentFolder);
                    tokens.Add($@"!Workshop\{id}");
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to create !Workshop junction for mod {Id}; falling back to its absolute content path.", id);
                }
            }

            tokens.Add(contentFolder);
        }

        return Task.FromResult<IReadOnlyList<string>>(tokens);
    }

    private static void EnsureWorkshopJunction(string dayzInstallDir, string workshopId, string targetContentFolder)
    {
        var workshopLinkRoot = Path.Combine(dayzInstallDir, "!Workshop");
        Directory.CreateDirectory(workshopLinkRoot);
        var linkPath = Path.Combine(workshopLinkRoot, workshopId);

        // Already there (created by us on a previous launch, or genuinely by Steam itself) --
        // Directory.Exists follows the junction, so this is true for a valid link either way.
        if (Directory.Exists(linkPath)) return;

        // No built-in .NET API creates a true NTFS junction (Directory.CreateSymbolicLink makes
        // a symbolic link instead, which historically needs elevation/Developer Mode on
        // Windows -- a junction never does, which matters since mklink is run here from
        // whatever session this agent's process happens to be in). mklink /J is the simplest
        // reliable way to create one.
        using var proc = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetContentFolder}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Failed to start mklink process.");

        proc.WaitForExit(10_000);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J exited with code {proc.ExitCode}: {proc.StandardError.ReadToEnd()}");
    }

    public async Task EnsureModsInstalledAsync(IReadOnlyList<string> workshopIds, CancellationToken ct = default)
    {
        var installed = (await GetInstalledModsAsync(ct)).Select(m => m.WorkshopId).ToHashSet();
        var missing = workshopIds.Where(id => !installed.Contains(id)).ToList();

        if (missing.Count == 0)
        {
            _logger.LogInformation("All {Count} required mods already installed.", workshopIds.Count);
            return;
        }

        var steamExe = _steam.DiscoverSteamExePath();
        if (steamExe is null)
        {
            _logger.LogWarning("Cannot open Workshop pages for missing mods ({Ids}) — Steam not found.", string.Join(",", missing));
            return;
        }

        foreach (var id in missing)
        {
            _logger.LogWarning(
                "Workshop mod {Id} is not installed and cannot be subscribed automatically. " +
                "Opening its Workshop page — an administrator must click Subscribe once for this VM.", id);
            Process.Start(new ProcessStartInfo(steamExe, $"steam://url/CommunityFilePage/{id}") { UseShellExecute = true });
            // Steam visibly struggles/misbehaves (dropped or stuck overlay windows) if these
            // steam:// URLs are fired back-to-back for a large mod list -- observed directly
            // when the directory-detection bug above falsely flagged an entire ~24-mod list as
            // missing at once. 2s is a deliberately conservative gap; this path is meant for a
            // handful of genuinely-missing mods; a client that's missing dozens should instead
            // get them installed once in the master image (see docs/MASTER-IMAGE.md).
            await Task.Delay(2000, ct);
        }
    }
}
