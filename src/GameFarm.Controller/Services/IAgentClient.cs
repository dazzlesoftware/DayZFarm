using GameFarm.Core.Plugins;
using GameFarm.Shared;

namespace GameFarm.Controller.Services;

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

    /// <summary>Lists this client's own guest-side plugins (see docs/PLUGINS.md).</summary>
    Task<IReadOnlyList<PluginSummary>?> GetAgentPluginsAsync(Uri baseAddress, string token, CancellationToken ct = default);

    /// <summary>Runs one guest-side plugin by name -- the name is the only thing sent; what it
    /// actually runs is fixed by the matching JSON file already on disk inside that VM.</summary>
    Task<PluginRunResult?> RunAgentPluginAsync(Uri baseAddress, string token, string pluginName, CancellationToken ct = default);

    /// <summary>Pushes a new Agent build (a zip) to this VM for it to apply to itself -- see
    /// docs/AGENT-UPDATES.md. Distinct from the JSON-body <see cref="PostAsync{TBody}"/> calls
    /// above since this sends raw bytes.</summary>
    Task<AgentCommandResult> PushUpdatePackageAsync(Uri baseAddress, string token, byte[] zipBytes, CancellationToken ct = default);
}
