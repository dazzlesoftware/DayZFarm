using GameFarm.Controller.Services;
using GameFarm.Core.Plugins;
using GameFarm.Shared;

namespace GameFarm.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IAgentClient"/> fake — simulates a guest agent's responses without any
/// real HTTP/network call, per the project's "mock guest agents in tests" testing requirement.
/// </summary>
public sealed class FakeAgentClient : IAgentClient
{
    public AgentStatusResponse? StatusToReturn { get; set; } = new() { AgentOnline = true, ConnectionStatus = ConnectionStatus.Connected };
    public bool FailAllCommands { get; set; }
    public List<(Uri BaseAddress, string Route)> PostedRoutes { get; } = new();
    public JoinServerRequest? LastJoinRequest { get; set; }
    public ModsResponse? ModsToReturn { get; set; } = new();
    public LogsResponse? LogsToReturn { get; set; } = new() { SourceName = "fake.RPT", Lines = new List<string> { "line1" } };

    public Task<AgentStatusResponse?> GetStatusAsync(Uri baseAddress, string token, CancellationToken ct = default) =>
        Task.FromResult(StatusToReturn);

    public Task<AgentCommandResult> PostAsync(Uri baseAddress, string route, string token, CancellationToken ct = default)
    {
        PostedRoutes.Add((baseAddress, route));
        return Task.FromResult(FailAllCommands ? AgentCommandResult.Fail("simulated failure") : AgentCommandResult.Ok("simulated success"));
    }

    public Task<AgentCommandResult> PostAsync<TBody>(Uri baseAddress, string route, string token, TBody body, CancellationToken ct = default)
    {
        PostedRoutes.Add((baseAddress, route));
        if (body is JoinServerRequest join) LastJoinRequest = join;
        return Task.FromResult(FailAllCommands ? AgentCommandResult.Fail("simulated failure") : AgentCommandResult.Ok("simulated success"));
    }

    public Task<LogsResponse?> GetLogsAsync(Uri baseAddress, string token, CancellationToken ct = default) =>
        Task.FromResult(LogsToReturn);

    public Task<ModsResponse?> GetModsAsync(Uri baseAddress, string token, CancellationToken ct = default) =>
        Task.FromResult(ModsToReturn);

    public IReadOnlyList<PluginSummary>? PluginsToReturn { get; set; } = new List<PluginSummary>();
    public PluginRunResult? PluginRunResultToReturn { get; set; } = new() { Name = "fake-plugin", Success = true, StandardOutput = "simulated output" };

    public Task<IReadOnlyList<PluginSummary>?> GetAgentPluginsAsync(Uri baseAddress, string token, CancellationToken ct = default) =>
        Task.FromResult(PluginsToReturn);

    public Task<PluginRunResult?> RunAgentPluginAsync(Uri baseAddress, string token, string pluginName, CancellationToken ct = default) =>
        Task.FromResult(PluginRunResultToReturn);

    public byte[]? LastPushedUpdatePackage { get; set; }

    public Task<AgentCommandResult> PushUpdatePackageAsync(Uri baseAddress, string token, byte[] zipBytes, CancellationToken ct = default)
    {
        PostedRoutes.Add((baseAddress, AgentApiRoutes.UpdatePackage));
        LastPushedUpdatePackage = zipBytes;
        return Task.FromResult(FailAllCommands ? AgentCommandResult.Fail("simulated failure") : AgentCommandResult.Ok("simulated success"));
    }
}
