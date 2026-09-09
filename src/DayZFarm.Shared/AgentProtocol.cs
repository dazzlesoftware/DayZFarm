namespace DayZFarm.Shared;

/// <summary>
/// Wire contract between the Controller and the guest Agent. Kept OS-agnostic on purpose:
/// nothing here assumes a Windows guest, so a future Linux/Proton agent can implement the
/// same contract without changing the Controller.
/// </summary>
public static class AgentApiRoutes
{
    public const string Status = "/api/agent/status";
    public const string SteamStart = "/api/agent/steam/start";
    public const string SteamStop = "/api/agent/steam/stop";
    public const string SteamRestart = "/api/agent/steam/restart";
    public const string DayZStart = "/api/agent/dayz/start";
    public const string DayZStop = "/api/agent/dayz/stop";
    public const string DayZRestart = "/api/agent/dayz/restart";
    public const string Join = "/api/agent/join";
    public const string Disconnect = "/api/agent/disconnect";
    public const string Update = "/api/agent/update";
    public const string Logs = "/api/agent/logs";
    public const string Reboot = "/api/agent/reboot";
    public const string Shutdown = "/api/agent/shutdown";
    public const string Mods = "/api/agent/mods";
    public const string ModsEnsure = "/api/agent/mods/ensure";
}

/// <summary> Header carrying the per-agent bearer token issued during provisioning. </summary>
public static class AgentAuthConstants
{
    public const string TokenHeaderName = "X-Agent-Token";
    public const string TokenFileName = "agent-token.secret";
}

public enum ConnectionStatus
{
    Offline,
    StartingSteam,
    SteamReady,
    Updating,
    StartingDayZ,
    Connecting,
    Connected,
    Disconnected,
    Error
}

public sealed class AgentStatusResponse
{
    public bool AgentOnline { get; set; } = true;
    public bool SteamRunning { get; set; }
    public bool SteamLoggedIn { get; set; }
    public string? SteamAccount { get; set; }
    public bool DayZInstalled { get; set; }
    public bool DayZRunning { get; set; }
    public int? DayZProcessId { get; set; }
    public string? DayZVersion { get; set; }
    public string? CurrentServer { get; set; }
    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Offline;
    public string? LastError { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class JoinServerRequest
{
    public required string ServerAddress { get; set; }
    public required int ServerPort { get; set; }
    public string? ServerPassword { get; set; }
    public List<string> RequiredMods { get; set; } = new();
    public List<string> AdditionalArguments { get; set; } = new();
}

public sealed class UpdateDayZRequest
{
    /// <summary>When true, validates files without a full reinstall (steamcmd validate).</summary>
    public bool ValidateOnly { get; set; }
}

public sealed class LogsResponse
{
    public required string SourceName { get; set; }
    public List<string> Lines { get; set; } = new();
}

public sealed class WorkshopModStatus
{
    public required string WorkshopId { get; set; }
    public bool Installed { get; set; }
    public DateTimeOffset? LastUpdatedUtc { get; set; }
}

public sealed class ModsResponse
{
    public List<WorkshopModStatus> Mods { get; set; } = new();
}

public sealed class EnsureModsRequest
{
    public List<string> WorkshopIds { get; set; } = new();
}

public sealed class AgentCommandResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }

    public static AgentCommandResult Ok(string? message = null) => new() { Success = true, Message = message };
    public static AgentCommandResult Fail(string message) => new() { Success = false, Message = message };
}
