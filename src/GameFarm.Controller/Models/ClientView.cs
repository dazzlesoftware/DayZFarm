using GameFarm.Core.Models;
using GameFarm.Shared;

namespace GameFarm.Controller.Models;

/// <summary>Combined view of a client's DB record + live VM state + live agent status, sent to the dashboard.</summary>
public sealed class ClientView
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string VmName { get; set; }
    public string? SteamAccountName { get; set; }
    public required string ServerAddress { get; set; }
    public int ServerPort { get; set; }
    public bool AutoStart { get; set; }
    public bool AutoReconnect { get; set; }
    public bool AutoUpdate { get; set; }
    public List<string> RequiredMods { get; set; } = new();

    public VirtualMachineStatus VmStatus { get; set; } = VirtualMachineStatus.Unknown;
    public int CpuCount { get; set; }
    public long MemoryStartupBytes { get; set; }
    public string? GuestIpAddress { get; set; }
    public TimeSpan VmUptime { get; set; }

    /// <summary>
    /// Whether an agent token has been registered for this client (see Register Token in the
    /// dashboard / docs/STEAM-SETUP.md). Lets the dashboard distinguish a freshly created client
    /// still "Awaiting Token" from one whose agent is genuinely unreachable despite having a
    /// registered token — both otherwise look identical (<see cref="AgentOnline"/> false).
    /// </summary>
    public bool TokenRegistered { get; set; }

    /// <summary>Whether a server join password is currently stored for this client (DPAPI secret
    /// store — see ISecretStore) — never the password itself.</summary>
    public bool ServerPasswordSet { get; set; }

    public bool AgentOnline { get; set; }

    /// <summary>SHA256 hash of the Agent package this client last self-updated to (see
    /// docs/AGENT-UPDATES.md) -- null if it's never been updated that way.</summary>
    public string? AgentVersion { get; set; }

    /// <summary>True when the Controller's currently uploaded Agent package differs from
    /// <see cref="AgentVersion"/> -- computed by <c>ClientOrchestrator</c>, never persisted.
    /// False (not an error state) whenever either side is unknown, e.g. no package has been
    /// uploaded yet, or this agent is unreachable so its version couldn't be read.</summary>
    public bool AgentUpdateAvailable { get; set; }
    public bool SteamRunning { get; set; }
    public bool SteamLoggedIn { get; set; }
    public bool GameRunning { get; set; }
    public int? GameProcessId { get; set; }
    public string? GameVersion { get; set; }
    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Offline;
    public string? LastError { get; set; }
    public DateTimeOffset LastPolledUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FarmSummary
{
    public int TotalClients { get; set; }
    public int RunningVms { get; set; }
    public int ConnectedClients { get; set; }
    public int Updating { get; set; }
    public int Errors { get; set; }
    public int Offline { get; set; }
}
