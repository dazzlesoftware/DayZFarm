using DayZFarm.Core.Models;
using DayZFarm.Shared;

namespace DayZFarm.Controller.Models;

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

    public bool AgentOnline { get; set; }
    public bool SteamRunning { get; set; }
    public bool SteamLoggedIn { get; set; }
    public bool DayZRunning { get; set; }
    public int? DayZProcessId { get; set; }
    public string? DayZVersion { get; set; }
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
