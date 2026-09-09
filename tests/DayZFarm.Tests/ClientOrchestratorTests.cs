using DayZFarm.Controller.Models;
using DayZFarm.Controller.Services;
using DayZFarm.Core.Configuration;
using DayZFarm.Core.Models;
using DayZFarm.Shared;
using DayZFarm.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DayZFarm.Tests;

/// <summary>
/// Exercises <see cref="ClientOrchestrator"/> end-to-end against fakes for every external
/// dependency (repository, VM provider, guest agent, secret store) — no SQLite, DPAPI, Hyper-V,
/// or network access required, per the project's testing requirements.
/// </summary>
public class ClientOrchestratorTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "DayZFarmTests-" + Guid.NewGuid());
    private readonly FakeClientRepository _repository = new();
    private readonly FakeVirtualMachineProvider _vmProvider = new();
    private readonly FakeAgentClient _agentClient = new();
    private readonly FakeSecretStore _secrets = new();

    private ClientOrchestrator CreateSut(DayZFarmOptions? options = null)
    {
        options ??= new DayZFarmOptions { RootDirectory = _tempRoot, UpdateBatchSize = 2, MaxConcurrentVmOperations = 4, MaxConcurrentAgentCommands = 4 };
        var iOptions = Options.Create(options);
        return new ClientOrchestrator(
            _repository,
            _vmProvider,
            _agentClient,
            new AgentEndpointResolver(iOptions),
            _secrets,
            new ConcurrencyGates(iOptions),
            new ClientActionLogger(iOptions),
            iOptions,
            NullLogger<ClientOrchestrator>.Instance);
    }

    private static DayZClientInstance NewInstance(string name = "DayZ-001") => new()
    {
        Name = name,
        VmName = name,
        ServerAddress = "192.168.1.50",
        ServerPort = 2302
    };

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task CreateClientAsync_CreatesVmAndPersistsRecord()
    {
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());

        Assert.True(id > 0);
        Assert.Contains(("create", "DayZ-001"), _vmProvider.Calls);
        Assert.NotNull(await _repository.GetByIdAsync(id));
    }

    [Fact]
    public async Task CreateClientAsync_WithNoSteamLibraryDiskPresent_PassesNullBasePath()
    {
        var sut = CreateSut();
        await sut.CreateClientAsync(NewInstance());

        Assert.Null(_vmProvider.CreateRequests.Single().SteamLibraryBasePath);
    }

    [Fact]
    public async Task CreateClientAsync_WithSteamLibraryDiskPresent_PassesItsPathDirectlyToEveryClient()
    {
        var options = new DayZFarmOptions { RootDirectory = _tempRoot };
        Directory.CreateDirectory(Path.GetDirectoryName(options.SteamLibraryVhdx)!);
        File.WriteAllText(options.SteamLibraryVhdx, "fake-vhdx-content");
        var sut = CreateSut(options);

        await sut.CreateClientAsync(NewInstance("DayZ-001"));
        await sut.CreateClientAsync(NewInstance("DayZ-002"));

        // Never copied -- both clients reference the exact same real file.
        Assert.All(_vmProvider.CreateRequests, r => Assert.Equal(options.SteamLibraryVhdx, r.SteamLibraryBasePath));
        Assert.Equal("fake-vhdx-content", File.ReadAllText(options.SteamLibraryVhdx));
    }

    [Fact]
    public async Task CreateClientAsync_InvalidInstance_ThrowsWithoutTouchingVmProvider()
    {
        var sut = CreateSut();
        var invalid = NewInstance();
        invalid.VmName = "bad name!";

        await Assert.ThrowsAsync<ArgumentException>(() => sut.CreateClientAsync(invalid));
        Assert.Empty(_vmProvider.Calls);
    }

    [Fact]
    public async Task DeleteClientAsync_RemovesVmRepositoryRecordAndSecret()
    {
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());
        sut.RegisterAgentToken("DayZ-001", "token123");

        await sut.DeleteClientAsync(id);

        Assert.Contains(("delete", "DayZ-001"), _vmProvider.Calls);
        Assert.Null(await _repository.GetByIdAsync(id));
        Assert.Null(_secrets.Get("agent-token-DayZ-001"));
    }

    [Fact]
    public async Task GetAllViewsAsync_ReportsConnectedWhenVmRunningAndTokenRegistered()
    {
        var sut = CreateSut();
        await sut.CreateClientAsync(NewInstance());
        sut.RegisterAgentToken("DayZ-001", "token123");
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });
        _agentClient.StatusToReturn = new AgentStatusResponse { AgentOnline = true, ConnectionStatus = ConnectionStatus.Connected };

        var views = await sut.GetAllViewsAsync();
        var summary = ClientOrchestrator.Summarize(views);

        Assert.Single(views);
        Assert.True(views[0].AgentOnline);
        Assert.Equal(ConnectionStatus.Connected, views[0].ConnectionStatus);
        Assert.Equal(1, summary.RunningVms);
        Assert.Equal(1, summary.ConnectedClients);
        Assert.Equal(0, summary.Offline);
    }

    [Fact]
    public async Task GetAllViewsAsync_OfflineWhenNoTokenRegistered()
    {
        var sut = CreateSut();
        await sut.CreateClientAsync(NewInstance());
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });
        // No RegisterAgentToken call.

        var views = await sut.GetAllViewsAsync();

        Assert.False(views[0].AgentOnline);
        Assert.False(views[0].TokenRegistered);
        Assert.Equal(1, ClientOrchestrator.Summarize(views).Offline);
    }

    [Fact]
    public async Task GetAllViewsAsync_DistinguishesAwaitingTokenFromUnreachableAgent()
    {
        var sut = CreateSut();
        await sut.CreateClientAsync(NewInstance());
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });

        // Before registering a token: TokenRegistered should read false (dashboard shows "Awaiting Token").
        var beforeToken = await sut.GetAllViewsAsync();
        Assert.False(beforeToken[0].TokenRegistered);
        Assert.False(beforeToken[0].AgentOnline);

        // After registering a token but with the fake agent still unreachable: TokenRegistered
        // true while AgentOnline stays false is what the dashboard renders as "Unreachable".
        sut.RegisterAgentToken("DayZ-001", "token123");
        _agentClient.StatusToReturn = null; // simulate an unreachable/non-responding agent
        var afterToken = await sut.GetAllViewsAsync();
        Assert.True(afterToken[0].TokenRegistered);
        Assert.False(afterToken[0].AgentOnline);
    }

    [Fact]
    public async Task SendAgentCommandAsync_FailsCleanlyWithoutTokenRegistered()
    {
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });

        var result = await sut.SendAgentCommandAsync(id, AgentApiRoutes.SteamStart);

        Assert.False(result.Success);
        Assert.Contains("token", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_agentClient.PostedRoutes);
    }

    [Fact]
    public async Task SendAgentCommandAsync_FailsCleanlyWithoutKnownGuestIp()
    {
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());
        sut.RegisterAgentToken("DayZ-001", "token123");
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Off });

        var result = await sut.SendAgentCommandAsync(id, AgentApiRoutes.SteamStart);

        Assert.False(result.Success);
        Assert.Contains("IP", result.Message);
    }

    [Fact]
    public async Task SendAgentCommandAsync_SucceedsAndReachesAgent()
    {
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());
        sut.RegisterAgentToken("DayZ-001", "token123");
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });

        var result = await sut.SendAgentCommandAsync(id, AgentApiRoutes.SteamStart);

        Assert.True(result.Success);
        Assert.Single(_agentClient.PostedRoutes);
        Assert.Equal(AgentApiRoutes.SteamStart, _agentClient.PostedRoutes[0].Route);
    }

    [Fact]
    public async Task JoinAsync_PopulatesPasswordFromSecretStore()
    {
        var sut = CreateSut();
        var instance = NewInstance();
        instance.ServerPasswordSecretName = "server-pw-DayZ-001";
        var id = await sut.CreateClientAsync(instance);
        sut.RegisterAgentToken("DayZ-001", "token123");
        _secrets.Set("server-pw-DayZ-001", "s3cret");
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });

        var result = await sut.JoinAsync(id);

        Assert.True(result.Success);
        Assert.NotNull(_agentClient.LastJoinRequest);
        Assert.Equal("s3cret", _agentClient.LastJoinRequest!.ServerPassword);
    }

    [Fact]
    public async Task UpdateClientsAsync_MarksEachJobCompletedOnSuccess()
    {
        var sut = CreateSut();
        var ids = new List<int>();
        foreach (var name in new[] { "DayZ-001", "DayZ-002", "DayZ-003" })
        {
            var id = await sut.CreateClientAsync(NewInstance(name));
            sut.RegisterAgentToken(name, "token");
            _vmProvider.Seed(new VirtualMachineInfo { Name = name, Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });
            ids.Add(id);
        }

        var batches = await sut.UpdateClientsAsync(ids, null);

        Assert.Equal(2, batches.Count); // UpdateBatchSize = 2 → batches of 2 then 1
        Assert.All(batches.SelectMany(b => b), job => Assert.Equal(DayZFarm.Core.UpdateState.Completed, job.State));
    }

    [Fact]
    public async Task UpdateClientAsync_PersistsEditableFields()
    {
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());

        var updated = await sut.UpdateClientAsync(id, new UpdateClientRequest(
            SteamAccountName: "acct001",
            ServerAddress: "10.0.0.99",
            ServerPort: 2402,
            AutoStart: true,
            AutoReconnect: false,
            AutoUpdate: true,
            RequiredMods: new List<string> { "123456" }));

        Assert.True(updated);
        var stored = await _repository.GetByIdAsync(id);
        Assert.Equal("acct001", stored!.SteamAccountName);
        Assert.Equal("10.0.0.99", stored.ServerAddress);
        Assert.Equal(2402, stored.ServerPort);
        Assert.True(stored.AutoStart);
        Assert.False(stored.AutoReconnect);
        Assert.Equal(new[] { "123456" }, stored.RequiredMods);
    }

    [Fact]
    public async Task UpdateClientAsync_ReturnsFalseForUnknownClient()
    {
        var sut = CreateSut();
        var updated = await sut.UpdateClientAsync(999, new UpdateClientRequest(null, "a", 1, false, false, false, null));
        Assert.False(updated);
    }

    [Fact]
    public async Task UpdateClientAsync_RejectsInvalidServerPort()
    {
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());

        await Assert.ThrowsAsync<ArgumentException>(() => sut.UpdateClientAsync(id, new UpdateClientRequest(null, "a", 0, false, false, false, null)));
    }

    [Fact]
    public async Task CreateClientsBulkAsync_CreatesSequentiallyNamedClients()
    {
        var sut = CreateSut();
        var request = new BulkCreateClientsRequest(1, 3, "192.168.1.50", 2302, false, true, true, null);

        var results = await sut.CreateClientsBulkAsync(request);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(new[] { "DayZ-001", "DayZ-002", "DayZ-003" }, results.Select(r => r.Name));
        Assert.Equal(3, (await _repository.GetAllAsync()).Count);
    }

    [Fact]
    public async Task CreateClientsBulkAsync_ContinuesPastAFailure()
    {
        var sut = CreateSut();
        _vmProvider.FailCreateForNames.Add("DayZ-002");

        var request = new BulkCreateClientsRequest(1, 3, "192.168.1.50", 2302, false, true, true, null);
        var results = await sut.CreateClientsBulkAsync(request);

        Assert.Equal(3, results.Count);
        Assert.True(results[0].Success);
        Assert.False(results[1].Success);
        Assert.NotNull(results[1].Error);
        Assert.True(results[2].Success);
        // The failed one never made it into the repository; the other two did.
        Assert.Equal(2, (await _repository.GetAllAsync()).Count);
    }

    [Fact]
    public async Task UpdateClientsAsync_MarksJobFailedWhenAgentFails()
    {
        _agentClient.FailAllCommands = true;
        var sut = CreateSut();
        var id = await sut.CreateClientAsync(NewInstance());
        sut.RegisterAgentToken("DayZ-001", "token");
        _vmProvider.Seed(new VirtualMachineInfo { Name = "DayZ-001", Status = VirtualMachineStatus.Running, GuestIpAddress = "10.0.0.5" });

        var batches = await sut.UpdateClientsAsync(new[] { id }, null);

        var job = Assert.Single(batches.SelectMany(b => b));
        Assert.Equal(DayZFarm.Core.UpdateState.Failed, job.State);
        Assert.NotNull(job.Error);
    }
}
