using DayZFarm.Controller.Data;
using DayZFarm.Core.Models;

namespace DayZFarm.Tests.Fakes;

/// <summary>In-memory <see cref="IClientRepository"/> fake — no SQLite/file I/O in tests.</summary>
public sealed class FakeClientRepository : IClientRepository
{
    private readonly Dictionary<int, DayZClientInstance> _byId = new();
    private int _nextId = 1;

    public Task<int> AddAsync(DayZClientInstance instance, CancellationToken ct = default)
    {
        instance.Id = _nextId++;
        _byId[instance.Id] = instance;
        return Task.FromResult(instance.Id);
    }

    public Task<IReadOnlyList<DayZClientInstance>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DayZClientInstance>>(_byId.Values.OrderBy(c => c.Name).ToList());

    public Task<DayZClientInstance?> GetByIdAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<DayZClientInstance?> GetByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(c => c.Name == name));

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _byId.Remove(id);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(DayZClientInstance instance, CancellationToken ct = default)
    {
        _byId[instance.Id] = instance;
        return Task.CompletedTask;
    }
}
