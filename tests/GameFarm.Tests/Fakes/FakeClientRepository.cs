using GameFarm.Controller.Data;
using GameFarm.Core.Models;

namespace GameFarm.Tests.Fakes;

/// <summary>In-memory <see cref="IClientRepository"/> fake — no SQLite/file I/O in tests.</summary>
public sealed class FakeClientRepository : IClientRepository
{
    private readonly Dictionary<int, GameClientInstance> _byId = new();
    private int _nextId = 1;

    public Task<int> AddAsync(GameClientInstance instance, CancellationToken ct = default)
    {
        instance.Id = _nextId++;
        _byId[instance.Id] = instance;
        return Task.FromResult(instance.Id);
    }

    public Task<IReadOnlyList<GameClientInstance>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GameClientInstance>>(_byId.Values.OrderBy(c => c.Name).ToList());

    public Task<GameClientInstance?> GetByIdAsync(int id, CancellationToken ct = default) =>
        Task.FromResult(_byId.GetValueOrDefault(id));

    public Task<GameClientInstance?> GetByNameAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(_byId.Values.FirstOrDefault(c => c.Name == name));

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        _byId.Remove(id);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(GameClientInstance instance, CancellationToken ct = default)
    {
        _byId[instance.Id] = instance;
        return Task.CompletedTask;
    }
}
