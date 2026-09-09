using DayZFarm.Core.Models;

namespace DayZFarm.Tests.Fakes;

/// <summary>In-memory <see cref="IVirtualMachineProvider"/> fake — no Hyper-V required.</summary>
public sealed class FakeVirtualMachineProvider : IVirtualMachineProvider
{
    private readonly Dictionary<string, VirtualMachineInfo> _machines = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Action, string Name)> Calls { get; } = new();
    public List<VirtualMachineCreateRequest> CreateRequests { get; } = new();
    public bool ThrowOnCreate { get; set; }
    public HashSet<string> FailCreateForNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeVirtualMachineProvider Seed(VirtualMachineInfo vm)
    {
        _machines[vm.Name] = vm;
        return this;
    }

    public Task<IReadOnlyList<VirtualMachineInfo>> GetMachinesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VirtualMachineInfo>>(_machines.Values.ToList());

    public Task<VirtualMachineInfo?> GetMachineAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_machines.GetValueOrDefault(name));

    public Task StartAsync(string name, CancellationToken ct = default)
    {
        Calls.Add(("start", name));
        if (_machines.TryGetValue(name, out var vm)) vm.Status = VirtualMachineStatus.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken ct = default)
    {
        Calls.Add(("stop", name));
        if (_machines.TryGetValue(name, out var vm)) vm.Status = VirtualMachineStatus.Off;
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(string name, CancellationToken ct = default)
    {
        Calls.Add(("shutdown", name));
        if (_machines.TryGetValue(name, out var vm)) vm.Status = VirtualMachineStatus.Off;
        return Task.CompletedTask;
    }

    public Task RestartAsync(string name, CancellationToken ct = default)
    {
        Calls.Add(("restart", name));
        return Task.CompletedTask;
    }

    public Task CreateAsync(VirtualMachineCreateRequest request, CancellationToken ct = default)
    {
        if (ThrowOnCreate || FailCreateForNames.Contains(request.Name))
            throw new InvalidOperationException($"simulated create failure for '{request.Name}'");
        Calls.Add(("create", request.Name));
        CreateRequests.Add(request);
        _machines[request.Name] = new VirtualMachineInfo { Name = request.Name, Status = VirtualMachineStatus.Off, CpuCount = request.CpuCount };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        Calls.Add(("delete", name));
        _machines.Remove(name);
        return Task.CompletedTask;
    }

    public Task<VirtualMachineStatus> GetStatusAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_machines.GetValueOrDefault(name)?.Status ?? VirtualMachineStatus.Unknown);
}
