using GameFarm.Core.Models;

namespace GameFarm.Controller.Data;

/// <summary>
/// Persistence abstraction for <see cref="GameClientInstance"/> records. Separated from
/// <see cref="ClientRepository"/> (the SQLite implementation) purely so
/// <c>ClientOrchestrator</c> can be unit-tested against an in-memory fake — no SQLite/file I/O
/// required for that test suite.
/// </summary>
public interface IClientRepository
{
    Task<int> AddAsync(GameClientInstance instance, CancellationToken ct = default);
    Task<IReadOnlyList<GameClientInstance>> GetAllAsync(CancellationToken ct = default);
    Task<GameClientInstance?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<GameClientInstance?> GetByNameAsync(string name, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task UpdateAsync(GameClientInstance instance, CancellationToken ct = default);
}
