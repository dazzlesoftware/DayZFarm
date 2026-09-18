using GameFarm.Core.Models;
using GameFarm.Shared;

namespace GameFarm.Core.Interfaces;

/// <summary>
/// Determines actual in-game connection state from whatever evidence is available (process
/// state, log files, network connections) for whichever game module (<see cref="IGameLauncher"/>
/// implementation) is active. Implemented inside the guest agent (it has access to the game's own
/// files); abstracted here so detection quality -- and which game it's detecting for -- can
/// change without touching callers. See docs/ARCHITECTURE.md's "Game modules" section.
/// </summary>
public interface IGameStatusProvider
{
    Task<ConnectionStatus> DetectConnectionStatusAsync(CancellationToken ct = default);
    Task<string?> DetectInstalledVersionAsync(CancellationToken ct = default);
}

/// <summary>
/// Launches and stops a specific game's client for a given guest OS/runtime -- the extension
/// point for supporting a game beyond DayZ (the first, reference implementation; see
/// <c>GameFarm.Agent.Games.DayZ.DayZGameLauncher</c>). Adding a new game means writing a new
/// class implementing this interface (and usually <see cref="IGameStatusProvider"/>/
/// <see cref="IGameProfile"/>), then registering it by <c>AgentOptions.GameId</c> in the Agent's
/// Program.cs -- not a config-only change, since a different game's launch/detection logic is
/// genuinely different code, not just different parameters. See docs/ARCHITECTURE.md.
/// </summary>
public interface IGameLauncher
{
    Task<int> LaunchAsync(GameLaunchOptions options, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    bool IsRunning(out int? processId);
}

/// <summary>
/// Static identity for the currently active game module -- deliberately its own interface,
/// separate from <see cref="IGameLauncher"/>, so something that only needs to know "which
/// game/App ID is this" (e.g. <see cref="IWorkshopManager"/>, for its Workshop content-folder
/// scan) doesn't end up depending on the launcher itself. A concrete launcher (e.g.
/// <c>DayZGameLauncher</c>) commonly reads its own module's profile constants directly rather
/// than taking a DI dependency on this -- avoiding the launcher/workshop-manager cycle that would
/// otherwise result if both took a dependency on each other's interface.
/// </summary>
public interface IGameProfile
{
    /// <summary>This game's Steam App ID. A game module that isn't Steam-distributed can return
    /// 0; nothing in this codebase requires a real value beyond the Workshop-scanning path.</summary>
    uint SteamAppId { get; }
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
/// Registered in place of a real <see cref="IGpuVirtualizationProvider"/> for any hypervisor
/// backend that doesn't need one (currently: VMware Workstation — see docs/VMWARE-SETUP.md).
/// This isn't a missing feature: VMware Workstation's virtual SVGA 3D device gives guests real
/// DirectX-accelerated graphics with no host GPU passthrough/partitioning at all, just VMware
/// Tools installed and "Accelerate 3D Graphics" enabled on the VM — there's nothing left for an
/// automated provider to configure. <see cref="IsConfiguredAsync"/> reports "not configured"
/// rather than throwing, since the dashboard queries this routinely for every client and a hard
/// failure there would break otherwise-unrelated status polling; <see cref="ConfigureAsync"/>
/// throws, since actually requesting GPU configuration through this interface is a deliberate
/// action that should fail loudly rather than silently do nothing.
/// </summary>
public sealed class UnsupportedGpuVirtualizationProvider : IGpuVirtualizationProvider
{
    public Task<bool> IsConfiguredAsync(string vmName, CancellationToken ct = default) => Task.FromResult(false);

    public Task ConfigureAsync(string vmName, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This hypervisor backend has no automated GPU configuration provider -- and doesn't need one. " +
            "See docs/VMWARE-SETUP.md's master-build steps (VMware Tools + Accelerate 3D Graphics).");
}

/// <summary>
/// Applies a new Agent build (a zip from `dotnet publish`, pushed out by the Controller) to this
/// VM -- see docs/AGENT-UPDATES.md. Isolated behind an interface for the same reason as
/// <see cref="IGameLauncher"/>/<see cref="IWorkshopManager"/>: a future Linux/Proton agent would
/// need an entirely different self-update mechanism (no Scheduled Task, no PowerShell), not just
/// a different file path.
/// </summary>
public interface IAgentSelfUpdater
{
    /// <summary>Stages the given package and hands off to a detached process that replaces this
    /// agent's own files and restarts it. Returns almost immediately -- the actual file
    /// replacement happens moments later, out-of-process, since Windows won't let a running
    /// executable overwrite itself. Returns the new package's version (its own SHA256 hash) so
    /// the caller can report it back to the Controller right away.</summary>
    string ApplyUpdate(byte[] zipBytes);
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
