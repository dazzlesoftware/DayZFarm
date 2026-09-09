using DayZFarm.Controller.Services;
using DayZFarm.Shared;

namespace DayZFarm.Tests.Fakes;

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
}
