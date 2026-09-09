using DayZFarm.Core.Models;
using DayZFarm.Shared;

namespace DayZFarm.Core.Interfaces;

/// <summary>
/// Determines actual DayZ connection state from whatever evidence is available (process state,
/// log files, network connections). Implemented inside the guest agent (it has access to the
/// game's files); abstracted here so detection quality can improve without touching callers.
/// </summary>
public interface IDayZStatusProvider
{
    Task<ConnectionStatus> DetectConnectionStatusAsync(CancellationToken ct = default);
    Task<string?> DetectInstalledVersionAsync(CancellationToken ct = default);
}

/// <summary>
/// Launches the DayZ client for a given guest OS/runtime. Windows is implemented first;
/// a Proton/Linux implementation can be added later without changing agent command handling.
/// </summary>
public interface IDayZLauncher
{
    Task<int> LaunchAsync(DayZLaunchOptions options, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    bool IsRunning(out int? processId);
}

public sealed class WorkshopMod
{
    public required string WorkshopId { get; set; }
    public string? Name { get; set; }
    public bool Installed { get; set; }
    public string? Version { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
}

/// <summary>Future Workshop/mod management. Not fully implemented in v1 — architecture only.</summary>
public interface IWorkshopManager
{
    Task<IReadOnlyList<WorkshopMod>> GetInstalledModsAsync(CancellationToken ct = default);
    Task EnsureModsInstalledAsync(IReadOnlyList<string> workshopIds, CancellationToken ct = default);

    /// <summary>
    /// Resolves each installed Workshop ID to a `-mod=` launch-argument token DayZ can act on. A
    /// bare numeric Workshop ID (what `RequiredMods` stores, and what the dashboard/Ensure Mods
    /// work with) means nothing to DayZ's `-mod=` argument by itself. Prefers Steam's own
    /// `!Workshop\&lt;id&gt;` junction convention (a link inside DayZ's own install directory,
    /// created here if it doesn't already exist — Steam itself only creates it via its own
    /// "SETUP DLCS AND MODS AND JOIN" UI flow, not for a direct `-applaunch`), since BattlEye's
    /// periodic in-session integrity re-check was observed to reject mods referenced by their
    /// raw absolute content-folder path (outside the game's own install tree). Falls back to
    /// that raw absolute path if DayZ's install directory can't be found or junction creation
    /// fails, so a launch never breaks outright over this. IDs with no installed folder found
    /// are omitted (not thrown) — the caller decides how to handle a still-missing mod at launch
    /// time. See docs/TROUBLESHOOTING.md.
    /// </summary>
    Task<IReadOnlyList<string>> ResolveModPathsAsync(IReadOnlyList<string> workshopIds, CancellationToken ct = default);
}

/// <summary>
/// Hook point for GPU virtualization (e.g. GPU-P) configuration on a VM. Manual configuration
/// is acceptable for v1; this interface lets an automated implementation be dropped in later.
/// </summary>
public interface IGpuVirtualizationProvider
{
    Task<bool> IsConfiguredAsync(string vmName, CancellationToken ct = default);
    Task ConfigureAsync(string vmName, CancellationToken ct = default);
}

/// <summary>
/// Future hook for a server-side test mod (or client-side automation) to drive controlled
/// gameplay actions across the farm. No implementation ships in v1 — this only reserves the
/// extension point so it can be added without reworking the controller. Implementations must
/// only ever issue legitimate in-game actions through a supported mod/API — never memory
/// manipulation, packet spoofing, or anti-cheat bypasses.
/// </summary>
public interface IGameTestCommandProvider
{
    Task<AgentCommandResult> ExecuteAsync(string clientName, string command, IReadOnlyDictionary<string, string>? parameters, CancellationToken ct = default);
}
