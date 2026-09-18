using GameFarm.Controller.Data;
using GameFarm.Controller.Models;
using GameFarm.Core;
using GameFarm.Core.Configuration;
using GameFarm.Core.Models;
using GameFarm.Core.Plugins;
using GameFarm.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using GameFarm.Core.Interfaces;

namespace GameFarm.Controller.Services;

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
    private readonly AgentPackageStore _agentPackageStore;
    private readonly ConcurrencyGates _gates;
    private readonly GameFarmOptions _options;
    private readonly ClientActionLogger _actionLog;
    private readonly ILogger<ClientOrchestrator> _logger;

    public ClientOrchestrator(
        IClientRepository repository,
        IVirtualMachineProvider vmProvider,
        IAgentClient agentClient,
        AgentEndpointResolver endpointResolver,
        ISecretStore secrets,
        AgentPackageStore agentPackageStore,
        ConcurrencyGates gates,
        ClientActionLogger actionLog,
        IOptions<GameFarmOptions> options,
        ILogger<ClientOrchestrator> logger)
    {
        _repository = repository;
        _vmProvider = vmProvider;
        _agentClient = agentClient;
        _endpointResolver = endpointResolver;
        _secrets = secrets;
        _agentPackageStore = agentPackageStore;
        _gates = gates;
        _actionLog = actionLog;
        _options = options.Value;
        _logger = logger;
    }

    private static string AgentTokenSecretName(string clientName) => $"agent-token-{clientName}";
    private static string ServerPasswordSecretName(string clientName) => $"server-password-{clientName}";

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

    private async Task<ClientView> BuildViewAsync(GameClientInstance client, VirtualMachineInfo? vm, CancellationToken ct)
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
            ServerPasswordSet = client.ServerPasswordSecretName is not null,
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
                    view.GameRunning = status.GameRunning;
                    view.GameProcessId = status.GameProcessId;
                    view.GameVersion = status.GameVersion;
                    view.ConnectionStatus = status.ConnectionStatus;
                    view.LastError = status.LastError;
                    view.AgentVersion = status.AgentVersion;
                }
            }
        }

        var packageVersion = _agentPackageStore.GetInfo()?.VersionHash;
        view.AgentUpdateAvailable = packageVersion is not null && view.AgentVersion is not null &&
            !string.Equals(view.AgentVersion, packageVersion, StringComparison.OrdinalIgnoreCase);

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

    // ---- Guest-side plugins (see docs/PLUGINS.md) ----

    /// <summary>Lists this client's own guest-side plugins. Null means the client/agent
    /// couldn't be reached at all (not the same as an empty list, which just means no plugins
    /// are configured on that VM).</summary>
    public async Task<IReadOnlyList<PluginSummary>?> GetAgentPluginsAsync(int clientId, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return null;
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null || token is null) return null;

        return await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.GetAgentPluginsAsync(baseAddress, token, ct), ct);
    }

    /// <summary>Runs one of this client's guest-side plugins by name -- only the name ever
    /// travels over HTTP; what it actually runs is fixed by the matching JSON file already on
    /// disk inside that VM. See docs/PLUGINS.md.</summary>
    public async Task<PluginRunResult> RunAgentPluginAsync(int clientId, string pluginName, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return new PluginRunResult { Name = pluginName, Success = false, StandardError = "Client not found." };
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null) return new PluginRunResult { Name = pluginName, Success = false, StandardError = "VM has no known guest IP yet (is it running?)." };
        if (token is null) return new PluginRunResult { Name = pluginName, Success = false, StandardError = "No agent token registered for this client." };

        var result = await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.RunAgentPluginAsync(baseAddress, token, pluginName, ct), ct)
            ?? new PluginRunResult { Name = pluginName, Success = false, StandardError = "Agent unreachable or plugin not found." };

        await _actionLog.LogAsync(client.Name, $"plugin:{pluginName}", result.Success ? "OK" : result.StandardError, isError: !result.Success);
        return result;
    }

    // ---- Agent binary self-update (see docs/AGENT-UPDATES.md) ----

    /// <summary>Pushes the Controller's currently uploaded Agent package to one client, which
    /// then applies it to itself and restarts. Distinct from <see cref="SendAgentCommandAsync"/>
    /// with <see cref="AgentApiRoutes.Update"/>, which updates the DayZ game install via Steam,
    /// not the Agent binary.</summary>
    public async Task<AgentCommandResult> UpdateAgentPackageAsync(int clientId, CancellationToken ct = default)
    {
        var (client, vm) = await LoadAsync(clientId, ct);
        if (client is null) return AgentCommandResult.Fail("Client not found.");
        var baseAddress = _endpointResolver.Resolve(vm);
        var token = _secrets.Get(AgentTokenSecretName(client.Name));
        if (baseAddress is null) return AgentCommandResult.Fail("VM has no known guest IP yet (is it running?).");
        if (token is null) return AgentCommandResult.Fail("No agent token registered for this client.");

        var package = _agentPackageStore.ReadPackageBytes();
        if (package is null)
            return AgentCommandResult.Fail("No agent package has been uploaded yet -- upload one from the dashboard's Agent Package panel first.");

        var result = await ConcurrencyGates.RunAsync(_gates.AgentCommands, () => _agentClient.PushUpdatePackageAsync(baseAddress, token, package, ct), ct);
        await _actionLog.LogAsync(client.Name, "agent-update-package", result.Message ?? (result.Success ? "OK" : "Failed"), isError: !result.Success);
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

    private async Task<(GameClientInstance? Client, VirtualMachineInfo? Vm)> LoadAsync(int clientId, CancellationToken ct)
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

    public async Task<int> CreateClientAsync(GameClientInstance instance, string? serverPassword = null, CancellationToken ct = default)
    {
        var errors = GameClientInstanceValidator.Validate(instance);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors));

        if (!string.IsNullOrEmpty(serverPassword))
        {
            instance.ServerPasswordSecretName = ServerPasswordSecretName(instance.Name);
            _secrets.Set(instance.ServerPasswordSecretName, serverPassword);
        }

        var request = new VirtualMachineCreateRequest
        {
            Name = instance.VmName,
            ParentDiskPath = _options.ActiveMasterImagePath,
            InstanceDirectory = Path.Combine(_options.InstancesDirectory, instance.VmName),
            CpuCount = _options.DefaultCpuCount,
            MemoryGB = _options.DefaultMemoryGB,
            NetworkName = _options.ActiveNetworkName,
            SourceSnapshotName = _options.Hypervisor == HypervisorType.VMwareWorkstation ? _options.MasterVmxSnapshot : null,
            // Auto-detect, exactly like scripts/Hyper-V/Create-Client.ps1: this is a real shared disk
            // (never copied) that either already exists — because Create-Master.ps1 built it —
            // or simply doesn't, in which case this client gets no second disk. Hyper-V-only;
            // for a VMware-backed farm this file naturally never exists, so this always
            // resolves to null there without any extra branching. See docs/VMWARE-SETUP.md.
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
        if (client.ServerPasswordSecretName is { } pwName) _secrets.Delete(pwName);
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

        // ServerPassword: null means "leave whatever's already stored alone" (the dashboard
        // never round-trips the actual password back into the edit form, so a blank field on
        // submit must not be read as "clear it"). An empty string is the explicit "clear it"
        // signal instead, sent only from a dedicated action. A non-empty value replaces it.
        if (fields.ServerPassword is { Length: > 0 } newPassword)
        {
            var secretName = client.ServerPasswordSecretName ?? ServerPasswordSecretName(client.Name);
            _secrets.Set(secretName, newPassword);
            client.ServerPasswordSecretName = secretName;
        }
        else if (fields.ServerPassword is { Length: 0 })
        {
            if (client.ServerPasswordSecretName is { } existing) _secrets.Delete(existing);
            client.ServerPasswordSecretName = null;
        }

        var errors = GameClientInstanceValidator.Validate(client);
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("; ", errors));

        await _repository.UpdateAsync(client, ct);
        await _actionLog.LogAsync(client.Name, "update", $"Updated client settings (server {client.ServerAddress}:{client.ServerPort}).");
        return true;
    }

    /// <summary>
    /// Bulk-creates a numbered range of clients (DayZ-001, DayZ-002, ...) against the same
    /// master/switch/server settings — the dashboard equivalent of scripts/Hyper-V/Create-Clients.ps1.
    /// A failure on one client does not abort the rest of the range.
    /// </summary>
    public async Task<IReadOnlyList<BulkCreateResult>> CreateClientsBulkAsync(BulkCreateClientsRequest request, CancellationToken ct = default)
    {
        var results = new List<BulkCreateResult>();
        foreach (var name in ClientNaming.Range(request.StartIndex, request.Count))
        {
            try
            {
                var instance = new GameClientInstance
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
                var id = await CreateClientAsync(instance, request.ServerPassword, ct);
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
    List<string>? RequiredMods,
    // null = leave the stored password alone, "" = clear it, anything else = replace it.
    // See ClientOrchestrator.UpdateClientAsync.
    string? ServerPassword = null);

public sealed record BulkCreateClientsRequest(
    int StartIndex,
    int Count,
    string ServerAddress,
    int ServerPort,
    bool AutoStart,
    bool AutoReconnect,
    bool AutoUpdate,
    List<string>? RequiredMods,
    string? ServerPassword = null);

public sealed record BulkCreateResult(string Name, bool Success, int? Id, string? Error);
