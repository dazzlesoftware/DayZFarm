namespace GameFarm.Shared;

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
    public const string GameStart = "/api/agent/game/start";
    public const string GameStop = "/api/agent/game/stop";
    public const string GameRestart = "/api/agent/game/restart";
    public const string Join = "/api/agent/join";
    public const string Disconnect = "/api/agent/disconnect";
    public const string Update = "/api/agent/update";
    public const string Logs = "/api/agent/logs";
    public const string Reboot = "/api/agent/reboot";
    public const string Shutdown = "/api/agent/shutdown";
    public const string Mods = "/api/agent/mods";
    public const string ModsEnsure = "/api/agent/mods/ensure";

    /// <summary>Lists this VM's own configured guest-side plugins (see docs/PLUGINS.md) --
    /// admin-authored command definitions loaded from disk inside the guest, never anything
    /// supplied over the network.</summary>
    public const string Plugins = "/api/agent/plugins";

    /// <summary>Runs one guest-side plugin by name. The name is the only thing accepted from
    /// the caller -- what actually runs is fixed by the matching JSON file already on disk
    /// inside this VM. See docs/PLUGINS.md.</summary>
    public static string PluginRun(string name) => $"/api/agent/plugins/{Uri.EscapeDataString(name)}/run";

    /// <summary>Uploads a new Agent build (a zip from `dotnet publish`) for this VM to apply to
    /// itself -- see docs/AGENT-UPDATES.md. Distinct from <see cref="Update"/>, which updates the
    /// DayZ game install via Steam, not the Agent binary itself.</summary>
    public const string UpdatePackage = "/api/agent/update-package";
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
    StartingGame,
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
    public bool GameInstalled { get; set; }
    public bool GameRunning { get; set; }
    public int? GameProcessId { get; set; }
    public string? GameVersion { get; set; }
    public string? CurrentServer { get; set; }
    public ConnectionStatus ConnectionStatus { get; set; } = ConnectionStatus.Offline;
    public string? LastError { get; set; }
    public TimeSpan Uptime { get; set; }
    public DateTimeOffset ReportedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>SHA256 hash of the Agent package this VM was last updated to via
    /// <see cref="AgentApiRoutes.UpdatePackage"/> (see docs/AGENT-UPDATES.md) -- null if this
    /// agent has never been updated that way (e.g. the original master-image install). Lets the
    /// dashboard flag "update available" by comparing against the Controller's currently
    /// uploaded package.</summary>
    public string? AgentVersion { get; set; }
}

public sealed class JoinServerRequest
{
    public required string ServerAddress { get; set; }
    public required int ServerPort { get; set; }
    public string? ServerPassword { get; set; }
    public List<string> RequiredMods { get; set; } = new();
    public List<string> AdditionalArguments { get; set; } = new();
}

public sealed class UpdateGameRequest
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
