using DayZFarm.Controller.Data;
using DayZFarm.Controller.Models;
using DayZFarm.Core;
using DayZFarm.Core.Configuration;
using DayZFarm.Core.Models;
using DayZFarm.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DayZFarm.Controller.Services;

/// <summary>
/// Central coordination point: combines the client database, the VM provider, and per-agent
/// HTTP calls into the single/bulk operations the API and dashboard use. All fan-out operations
/// are bounded by <see cref="ConcurrencyGates"/>.
/// </summary>
public sealed class ClientOrchestrator
{
    private readonly IClientRepository _repository;
    private readonly IVirtualMachineProvider _vmProvider;
    private readonly IAgentClient _agentClient;
    private readonly AgentEndpointResolver _endpointResolver;
    private readonly ISecretStore _secrets;
    private readonly ConcurrencyGates _gates;
    private readonly DayZFarmOptions _options;
    private readonly ClientActionLogger _actionLog;
    private readonly ILogger<ClientOrchestrator> _logger;

    public ClientOrchestrator(
        IClientRepository repository,
        IVirtualMachineProvider vmProvider,
        IAgentClient agentClient,
        AgentEndpointResolver endpointResolver,
        ISecretStore secrets,
        ConcurrencyGates gates,
        ClientActionLogger actionLog,
        IOptions<DayZFarmOptions> options,
        ILogger<ClientOrchestrator> logger)
    {
        _repository = repository;
        _vmProvider = vmProvider;
        _agentClient = agentClient;
        _endpointResolver = endpointResolver;
        _secrets = secrets;
        _gates = gates;
        _actionLog = actionLog;
        _options = options.Value;
        _logger = logger;
    }

    private static string AgentTokenSecretName(string clientName) => $"agent-token-{clientName}";

    public void RegisterAgentToken(string clientName, string token) => _secrets.Set(AgentTokenSecretName(clientName), token);

    public async Task<IReadOnlyList<ClientView>> GetAllViewsAsync(CancellationToken ct = default)
    {
        var clients = await _repository.GetAllAsync(ct);
        var vms = await _vmProvider.GetMachinesAsync(ct);
        var vmByName = vms.ToDictionary(v => v.Name, StringComparer.OrdinalIgnoreCase);

        var tasks = clients.Select(async client =>
        {
            vmByName.TryGetValue(client.VmName, out var vm);
            return await BuildViewAsync(client, vm, ct);
        });

        return await Task.WhenAll(tasks);
    }

    public async Task<ClientView?> GetViewAsync(int id, CancellationToken ct = default)
    {
        var client = await _repository.GetByIdAsync(id, ct);
        if (client is null) return null;
        var vm = await _vmProvider.GetMachineAsync(client.VmName, ct);
        return await BuildViewAsync(client, vm, ct);
    }

    private async Task<ClientView> BuildViewAsync(DayZClientInstance client, VirtualMachineInfo? vm, CancellationToken ct)
    {
        var view = new ClientView
        {
            Id = client.Id,
            Name = client.Name,
            VmName = client.VmName,
            SteamAccountName = client.SteamAccountName,
            ServerAddress = client.ServerAddress,
            ServerPort = client.ServerPort,
            AutoStart = client.AutoStart,
            AutoReconnect = client.AutoReconnect,
            AutoUpdate = client.AutoUpdate,
            RequiredMods = client.RequiredMods,
            VmStatus = vm?.Status ?? VirtualMachineStatus.Unknown,
            CpuCount = vm?.CpuCount ?? 0,
            MemoryStartupBytes = vm?.MemoryStartupBytes ?? 0,
            GuestIpAddress = vm?.GuestIpAddress,
            VmUptime = vm?.Uptime ?? TimeSpan.Zero
        };

        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        view.TokenRegistered = token is not null;

        if (vm is { Status: VirtualMachineStatus.Running } && vm.GuestIpAddress is not null)
        {
            var baseAddress = _endpointResolver.Resolve(vm);
            if (baseAddress is not null && token is not null)
            {
                var status = await TryGetAgentStatusAsync(baseAddress, token, ct);
                if (status is not null)
                {
                    view.AgentOnline = true;
                    view.SteamRunning = status.SteamRunning;
                    view.SteamLoggedIn = status.SteamLoggedIn;
                    view.DayZRunning = status.DayZRunning;
                    view.DayZProcessId = status.DayZProcessId;
                    view.DayZVersion = status.DayZVersion;
                    view.ConnectionStatus = status.ConnectionStatus;
                    view.LastError = status.LastError;
                }
            }
        }

        return view;
    }

    private async Task<AgentStatusResponse?> TryGetAgentStatusAsync(Uri baseAddress, string token, CancellationToken ct)
    {
        try
        {
            return await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.GetStatusAsync(baseAddress, token, ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent status check failed for {Address}", baseAddress);
            return null;
        }
    }

    public static FarmSummary Summarize(IReadOnlyList<ClientView> views) => new()
    {
        TotalClients = views.Count,
        RunningVms = views.Count(v => v.VmStatus == VirtualMachineStatus.Running),
        ConnectedClients = views.Count(v => v.ConnectionStatus == ConnectionStatus.Connected),
        Updating = views.Count(v => v.ConnectionStatus == ConnectionStatus.Updating),
        Errors = views.Count(v => v.ConnectionStatus == ConnectionStatus.Error || v.LastError is not null),
        Offline = views.Count(v => v.VmStatus != VirtualMachineStatus.Running || !v.AgentOnline)
    };

    // ---- VM lifecycle ----

    public Task StartVmAsync(string vmName, CancellationToken ct = default) =>
        RunVmActionAsync(vmName, "vm-start", () => _vmProvider.StartAsync(vmName, ct), ct);

    public Task StopVmAsync(string vmName, CancellationToken ct = default) =>
        RunVmActionAsync(vmName, "vm-stop", () => _vmProvider.StopAsync(vmName, ct), ct);

    public Task RestartVmAsync(string vmName, CancellationToken ct = default) =>
        RunVmActionAsync(vmName, "vm-restart", () => _vmProvider.RestartAsync(vmName, ct), ct);

    public Task ShutdownVmAsync(string vmName, CancellationToken ct = default) =>
        RunVmActionAsync(vmName, "vm-shutdown", () => _vmProvider.ShutdownAsync(vmName, ct), ct);

    private async Task RunVmActionAsync(string vmName, string action, Func<Task> operation, CancellationToken ct)
    {
        try
        {
            await ConcurrencyGates.RunAsync(_gates.VmOperations, operation, ct);
            await _actionLog.LogAsync(vmName, action, "Succeeded.");
        }
        catch (Exception ex)
        {
            await _actionLog.LogAsync(vmName, action, ex.Message, isError: true);
            throw;
        }
    }

    // ---- Agent-mediated actions ----

    public async Task<AgentCommandResult> SendAgentCommandAsync(int clientId, string route, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return AgentCommandResult.Fail("Client not found.");
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null) return AgentCommandResult.Fail("VM has no known guest IP yet (is it running?).");
        if (token is null) return AgentCommandResult.Fail("No agent token registered for this client. Register it after first agent start.");

        var result = await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.PostAsync(baseAddress, route, token, ct), ct);
        await _actionLog.LogAsync(client.Name, $"agent:{route}", result.Message ?? (result.Success ? "OK" : "Failed"), isError: !result.Success);
        return result;
    }

    public async Task<AgentCommandResult> JoinAsync(int clientId, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return AgentCommandResult.Fail("Client not found.");
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null) return AgentCommandResult.Fail("VM has no known guest IP yet (is it running?).");
        if (token is null) return AgentCommandResult.Fail("No agent token registered for this client.");

        var request = new JoinServerRequest
        {
            ServerAddress = client.ServerAddress,
            ServerPort = client.ServerPort,
            ServerPassword = client.ServerPasswordSecretName is { } secretName ? _secrets.Get(secretName) : null,
            RequiredMods = client.RequiredMods
        };

        var result = await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.PostAsync(baseAddress, AgentApiRoutes.Join, token, request, ct), ct);
        await _actionLog.LogAsync(client.Name, "join", $"{client.ServerAddress}:{client.ServerPort} -> {(result.Success ? "OK" : result.Message)}", isError: !result.Success);
        return result;
    }

    /// <summary>Fetches the agent's tailed DayZ log (RPT/ADM) for the detail page / Logs button.</summary>
    public async Task<LogsResponse?> GetAgentLogsAsync(int clientId, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return null;
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null || token is null) return null;

        return await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.GetLogsAsync(baseAddress, token, ct), ct);
    }

    /// <summary>Reports Workshop mods currently installed on this client's VM (agent-side detection).</summary>
    public async Task<ModsResponse?> GetModsAsync(int clientId, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return null;
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null || token is null) return null;

        return await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.GetModsAsync(baseAddress, token, ct), ct);
    }

    /// <summary>
    /// Asks the agent to check the client's RequiredMods against what's installed and, for
    /// anything missing, open that mod's Workshop page for a one-click manual subscribe.
    /// </summary>
    public async Task<AgentCommandResult> EnsureModsAsync(int clientId, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return AgentCommandResult.Fail("Client not found.");
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null) return AgentCommandResult.Fail("VM has no known guest IP yet (is it running?).");
        if (token is null) return AgentCommandResult.Fail("No agent token registered for this client.");

        var request = new EnsureModsRequest { WorkshopIds = client.RequiredMods };
        var result = await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.PostAsync(baseAddress, AgentApiRoutes.ModsEnsure, token, request, ct), ct);
        await _actionLog.LogAsync(client.Name, "mods-ensure", result.Message ?? (result.Success ? "OK" : "Failed"), isError: !result.Success);
        return result;
    }

    /// <summary>Tail of this client's own controller-side action log (VM/agent actions, join/update attempts).</summary>
    public async Task<IReadOnlyList<string>> GetActionLogTailAsync(string clientName, int maxLines = 200, CancellationToken ct = default)
    {
        var dir = Path.Combine(_options.ClientLogsDirectory, clientName);
        var file = Path.Combine(dir, $"actions-{DateTime.UtcNow:yyyyMMdd}.log");
        if (!File.Exists(file)) return Array.Empty<string>();

        var lines = await File.ReadAllLinesAsync(file, ct);
        return lines.Length <= maxLines ? lines : lines[^maxLines..];
    }

    private async Task<(DayZClientInstance? Client, VirtualMachineInfo? Vm)> LoadAsync(int clientId, CancellationToken ct)
    {
        var client = await _repository.GetByIdAsync(clientId, ct);
        if (client is null) return (null, null);
        var vm = await _vmProvider.GetMachineAsync(client.VmName, ct);
        return (client, vm);
    }

    // ---- Update batching ----

    public async Task<IReadOnlyList<IReadOnlyList<UpdateJob>>> UpdateClientsAsync(IEnumerable<int> clientIds, IProgress<UpdateJob>? progress, CancellationToken ct = default)
    {
        var clients = await _repository.GetAllAsync(ct);
        var byId = clients.ToDictionary(c => c.Id);
        var names = clientIds.Where(byId.ContainsKey).Select(id => byId[id].Name).ToList();

        var planner = new UpdateBatchPlanner(_options.UpdateBatchSize);
        var batches = planner.Plan(names);
        var nameToId = clients.ToDictionary(c => c.Name, c => c.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var batch in batches)
        {
            var batchTasks = batch.Select(async job =>
            {
                job.State = UpdateState.Updating;
                progress?.Report(job);
                var result = await SendAgentCommandAsync(nameToId[job.ClientName], AgentApiRoutes.Update, ct);
                job.State = result.Success ? UpdateState.Completed : UpdateState.Failed;
                job.Error = result.Success ? null : result.Message;
                progress?.Report(job);
            });
            await Task.WhenAll(batchTasks);
        }

        return batches;
    }

    public async Task<int> CreateClientAsync(DayZClientInstance instance, CancellationToken ct = default)
    {
        var errors = DayZClientInstanceValidator.Validate(instance);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors));

        var request = new VirtualMachineCreateRequest
        {
            Name = instance.VmName,
            ParentVhdxPath = _options.MasterVhdx,
            InstanceDirectory = Path.Combine(_options.InstancesDirectory, instance.VmName),
            CpuCount = _options.DefaultCpuCount,
            MemoryGB = _options.DefaultMemoryGB,
            VirtualSwitchName = _options.VirtualSwitch,
            // Auto-detect, exactly like scripts/Create-Client.ps1: this is a real shared disk
            // (never copied) that either already exists — because Create-Master.ps1 built it —
            // or simply doesn't, in which case this client gets no second disk.
            SteamLibraryBasePath = File.Exists(_options.SteamLibraryVhdx) ? _options.SteamLibraryVhdx : null
        };
        await ConcurrencyGates.RunAsync(_gates.VmOperations, () => _vmProvider.CreateAsync(request, ct), ct);
        var id = await _repository.AddAsync(instance, ct);
        await _actionLog.LogAsync(instance.Name, "create", $"Created VM '{instance.VmName}' targeting {instance.ServerAddress}:{instance.ServerPort}.");
        return id;
    }

    public async Task DeleteClientAsync(int id, CancellationToken ct = default)
    {
        var client = await _repository.GetByIdAsync(id, ct);
        if (client is null) return;
        await ConcurrencyGates.RunAsync(_gates.VmOperations, () => _vmProvider.DeleteAsync(client.VmName, ct), ct);
        await _repository.DeleteAsync(id, ct);
        _secrets.Delete(AgentTokenSecretName(client.Name));
        await _actionLog.LogAsync(client.Name, "delete", $"Removed VM '{client.VmName}' and client record.");
    }

    /// <summary>
    /// Updates the editable fields of an existing client (Steam account label, target server,
    /// auto-start/reconnect/update flags, required mods). Never touches the VM or its name —
    /// use Remove-Client/Create-Client to rename or resize a VM.
    /// </summary>
    public async Task<bool> UpdateClientAsync(int id, UpdateClientRequest fields, CancellationToken ct = default)
    {
        var client = await _repository.GetByIdAsync(id, ct);
        if (client is null) return false;

        client.SteamAccountName = fields.SteamAccountName;
        client.ServerAddress = fields.ServerAddress;
        client.ServerPort = fields.ServerPort;
        client.AutoStart = fields.AutoStart;
        client.AutoReconnect = fields.AutoReconnect;
        client.AutoUpdate = fields.AutoUpdate;
        client.RequiredMods = fields.RequiredMods ?? new();

        var errors = DayZClientInstanceValidator.Validate(client);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors));

        await _repository.UpdateAsync(client, ct);
        await _actionLog.LogAsync(client.Name, "update", $"Updated client settings (server {client.ServerAddress}:{client.ServerPort}).");
        return true;
    }

    /// <summary>
    /// Bulk-creates a numbered range of clients (DayZ-001, DayZ-002, ...) against the same
    /// master/switch/server settings — the dashboard equivalent of scripts/Create-Clients.ps1.
    /// A failure on one client does not abort the rest of the range.
    /// </summary>
    public async Task<IReadOnlyList<BulkCreateResult>> CreateClientsBulkAsync(BulkCreateClientsRequest request, CancellationToken ct = default)
    {
        var results = new List<BulkCreateResult>();
        foreach (var name in ClientNaming.Range(request.StartIndex, request.Count))
        {
            try
            {
                var instance = new DayZClientInstance
                {
                    Name = name,
                    VmName = name,
                    ServerAddress = request.ServerAddress,
                    ServerPort = request.ServerPort,
                    AutoStart = request.AutoStart,
                    AutoReconnect = request.AutoReconnect,
                    AutoUpdate = request.AutoUpdate,
                    RequiredMods = request.RequiredMods ?? new()
                };
                var id = await CreateClientAsync(instance, ct);
                results.Add(new BulkCreateResult(name, true, id, null));
            }
            catch (Exception ex)
            {
                results.Add(new BulkCreateResult(name, false, null, ex.Message));
            }
        }
        return results;
    }
}

public sealed record UpdateClientRequest(
    string? SteamAccountName,
    string ServerAddress,
    int ServerPort,
    bool AutoStart,
    bool AutoReconnect,
    bool AutoUpdate,
    List<string>? RequiredMods);

public sealed record BulkCreateClientsRequest(
    int StartIndex,
    int Count,
    string ServerAddress,
    int ServerPort,
    bool AutoStart,
    bool AutoReconnect,
    bool AutoUpdate,
    List<string>? RequiredMods);

public sealed record BulkCreateResult(string Name, bool Success, int? Id, string? Error);
