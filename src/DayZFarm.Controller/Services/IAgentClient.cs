using DayZFarm.Shared;

namespace DayZFarm.Controller.Services;

/// <summary>
/// HTTP contract for talking to one guest agent, abstracted away from <see cref="AgentHttpClient"/>
/// so <c>ClientOrchestrator</c> can be unit-tested against a fake agent (no real HTTP/network
/// required — mirrors "mock guest agents in tests" from the project's testing requirements).
/// </summary>
public interface IAgentClient
{
    Task<AgentStatusResponse?> GetStatusAsync(Uri baseAddress, string token, CancellationToken ct = default);
    Task<AgentCommandResult> PostAsync(Uri baseAddress, string route, string token, CancellationToken ct = default);
    Task<AgentCommandResult> PostAsync<TBody>(Uri baseAddress, string route, string token, TBody body, CancellationToken ct = default);
    Task<LogsResponse?> GetLogsAsync(Uri baseAddress, string token, CancellationToken ct = default);
    Task<ModsResponse?> GetModsAsync(Uri baseAddress, string token, CancellationToken ct = default);
}
